using System.IO;
using System.Text.Json;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private sealed record HardwareSettingCase(string Name, SettingChange Change, SettingChange Restore);
    private sealed record HardwareSettingResult(string Name, DateTimeOffset Time, A1Status Status);

    private static async Task VerifyHardwareSettingsAsync()
    {
        IReadOnlyList<ReceiverInfo> receivers = HidTransport.FindReceivers();
        Check(receivers.Count == 1, "Exactly one normal receiver is connected");
        var trace = new List<string>();
        var results = new List<HardwareSettingResult>();
        var touched = new List<HardwareSettingCase>();
        var versions = new Dictionary<int, string>();
        A1Status? baseline = null;
        A1Status? finalStatus = null;
        bool restored = false;
        Exception? failure = null;
        Directory.CreateDirectory(ReportDirectory);
        string reportPath = Path.Combine(ReportDirectory, "hardware-settings.json");

        void SaveReport()
        {
            string[] traceSnapshot;
            lock (trace) traceSnapshot = trace.ToArray();
            string json = JsonSerializer.Serialize(new
            {
                Time = DateTimeOffset.Now,
                Receiver = receivers[0],
                Versions = versions,
                Baseline = baseline,
                Results = results,
                FinalStatus = finalStatus,
                Restored = restored,
                Failure = failure?.ToString(),
                AcousticEffectsTested = false,
                Trace = traceSnapshot
            }, new JsonSerializerOptions { WriteIndented = true });
            string temporaryPath = reportPath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, reportPath, true);
        }

        A1Controller OpenController()
        {
            var controller = new A1Controller(new HidTransport(receivers[0]));
            controller.Trace += message =>
            {
                lock (trace) trace.Add(DateTimeOffset.Now.ToString("O") + " " + message);
            };
            return controller;
        }

        try
        {
            using A1Controller controller = OpenController();
            baseline = await controller.ReadStatusAsync();
            Check(baseline.Transmitters.Count == 2 && baseline.Transmitters.All(transmitter => transmitter.Connected),
                "Both A1 transmitters are online before any setting write");
            for (int target = 0; target <= 2; target++)
                versions[target] = (await controller.ReadVersionAsync(target)).ToString();
            Check(Version.Parse(versions[0]) >= new Version(1, 0, 1, 3), "Receiver firmware supports mute");
            Check(baseline.IdentificationEnabled.HasValue, "Receiver reports identification state");
            SaveReport();
            foreach (HardwareSettingCase test in CreateHardwareSettingCases(baseline))
            {
                A1Status before = await controller.ReadStatusAsync();
                Check(SameConfiguration(baseline, before), test.Name + ": baseline intact before change");
                touched.Add(test);
                A1Status applied = await controller.ApplyAsync(test.Change);
                Check(test.Change.IsConfirmed(applied), test.Name + ": ACK and physical readback confirmed");
                results.Add(new HardwareSettingResult(test.Name, DateTimeOffset.Now, applied));
                A1Status returned = await controller.ApplyAsync(test.Restore);
                Check(SameConfiguration(baseline, returned), test.Name + ": original configuration restored");
                SaveReport();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (baseline is not null && touched.Count > 0)
            {
                try
                {
                    using A1Controller recovery = OpenController();
                    A1Status current = await recovery.ReadStatusAsync();
                    foreach (HardwareSettingCase test in touched.AsEnumerable().Reverse())
                    {
                        if (!IsRestorationConfirmed(test.Restore, current))
                            current = await recovery.ApplyAsync(test.Restore);
                    }
                    finalStatus = await recovery.ReadStatusAsync();
                    restored = SameConfiguration(baseline, finalStatus);
                    Check(restored, "Fresh HID session confirms original settings and mute states");
                }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
            }
            SaveReport();
            Console.WriteLine("Hardware settings report: " + reportPath);
        }
        if (failure is not null)
            throw new InvalidOperationException($"Hardware test failed. Original settings restored: {restored}. See {reportPath}", failure);
    }

    private static IEnumerable<HardwareSettingCase> CreateHardwareSettingCases(A1Status initial)
    {
        for (int value = 0; value <= 5; value++)
            yield return new($"Gain {value + 1}/6", SettingChange.Gain(value), SettingChange.Gain(initial.Gain));
        for (int value = 0; value <= 1; value++)
            yield return new($"Channel mode {value}", SettingChange.ChannelMode(value), SettingChange.ChannelMode(initial.ChannelMode));
        for (int value = 1; value <= 3; value++)
            yield return new($"Equalizer {value}", SettingChange.Equalizer(value), SettingChange.Equalizer(initial.Equalizer));
        for (int level = 0; level <= 2; level++)
        {
            yield return new($"Noise level {level}", SettingChange.Noise(true, level), SettingChange.Noise(initial.NoiseEnabled, initial.NoiseLevel));
            yield return new($"Reverb level {level}", SettingChange.Reverb(true, level), SettingChange.Reverb(initial.ReverbEnabled, initial.ReverbLevel));
        }
        yield return new("Noise disabled", SettingChange.Noise(false, initial.NoiseLevel), SettingChange.Noise(initial.NoiseEnabled, initial.NoiseLevel));
        yield return new("Reverb disabled", SettingChange.Reverb(false, initial.ReverbLevel), SettingChange.Reverb(initial.ReverbEnabled, initial.ReverbLevel));
        yield return new("LED toggle", SettingChange.Lights(!initial.LightsEnabled), SettingChange.Lights(initial.LightsEnabled));
        yield return new("Auto-off toggle", SettingChange.AutoOff(1 - initial.AutoOff), SettingChange.AutoOff(initial.AutoOff));
        yield return new("Identification toggle", SettingChange.Identification(!initial.IdentificationEnabled!.Value),
            SettingChange.Identification(initial.IdentificationEnabled.Value));
        for (int index = 0; index < 2; index++)
            yield return new($"TX{index + 1} mute toggle", SettingChange.Mute(index, !initial.Transmitters[index].Muted),
                SettingChange.Mute(index, initial.Transmitters[index].Muted));
    }

    private static bool IsRestorationConfirmed(SettingChange restore, A1Status current) =>
        restore.IsConfirmed(current) && (restore.Command switch
        {
            A1Command.SetNoise => current.NoiseLevel == restore.Payload[1],
            A1Command.SetReverb => current.ReverbLevel == restore.Payload[1],
            _ => true
        });

    private static bool SameConfiguration(A1Status expected, A1Status actual) =>
        expected.NoiseEnabled == actual.NoiseEnabled && expected.NoiseLevel == actual.NoiseLevel &&
        expected.Gain == actual.Gain && expected.ChannelMode == actual.ChannelMode &&
        expected.AutoOff == actual.AutoOff && expected.LightsEnabled == actual.LightsEnabled &&
        expected.Equalizer == actual.Equalizer && expected.ReverbEnabled == actual.ReverbEnabled &&
        expected.ReverbLevel == actual.ReverbLevel && expected.IdentificationEnabled == actual.IdentificationEnabled &&
        expected.Transmitters.Count == actual.Transmitters.Count &&
        expected.Transmitters.Zip(actual.Transmitters).All(pair =>
            pair.First.Connected == pair.Second.Connected && pair.First.Muted == pair.Second.Muted);
}
