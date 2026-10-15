using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using NAudio.Wave;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static AudioChannelLevel[] SimulateAudioLevels(params double[] values) =>
        values.Select(value => new AudioChannelLevel(value, value, value >= 0, false)).ToArray();

    private static string ExpectedAudioChannel(int channel, double rms, double peak, bool fullScale)
    {
        string FormatExpected(double value) => value <= -96 ? Localization.Text("Meter.BelowFloor")
            : Localization.Text("Meter.Level", (Math.Round(value, 1) == 0 ? 0 : value).ToString("F1", Localization.Culture));
        return Localization.Text("Meter.ChannelMeasurement", Localization.Text("Meter.ChannelLevel", channel, FormatExpected(rms)),
            FormatExpected(peak), Localization.Text(fullScale ? "Meter.FullScaleReached" : "Meter.FullScaleNotReached"));
    }

    private static void VerifyAudioPeakApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            VerifyAudioSourceFormats();
            VerifyAudioMeterInterface();
            VerifyAudioFollowupChecks();
            VerifyAudioPeakInterface();
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyAudioSourceFormats()
    {
        Check(AudioMeterSource.DescribeFormat(new WaveFormat(48000, 16, 2)) == (false, 16),
            "Standard PCM descriptor preserves its valid-bit depth");
        Check(AudioMeterSource.DescribeFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) == (true, 32),
            "Standard float descriptor is recognized without opening an audio endpoint");
        var extensible = new WaveFormatExtensible(48000, 32, 2);
        Check(AudioMeterSource.DescribeFormat(extensible) == (true, 32), "Extensible float descriptor has 32 valid bits");
        IntPtr pointer = WaveFormat.MarshalToPtr(extensible);
        try
        {
            Marshal.WriteInt16(pointer, 18, 24);
            WaveFormat invalidFloat = WaveFormat.MarshalFromPtr(pointer);
            Throws<NotSupportedException>(() => AudioMeterSource.DescribeFormat(invalidFloat), "Invalid float valid-bit depth is rejected");
            Marshal.WriteInt32(pointer, 24, 1);
            WaveFormat pcm = WaveFormat.MarshalFromPtr(pointer);
            Check(AudioMeterSource.DescribeFormat(pcm) == (false, 24), "PCM24 inside a 32-bit container retains its actual quantization");
            byte[] maximum = [0, 255, 255, 127];
            var descriptor = AudioMeterSource.DescribeFormat(pcm);
            Check(AudioLevelCalculator.Measure(maximum, 1, pcm.BitsPerSample, descriptor.FloatingPoint, descriptor.ValidBitsPerSample)[0].ReachedFullScale,
                "The Windows descriptor reaches the calculator, detecting positive PCM24-in32 full scale");
            foreach (short validBits in new short[] { 0, 33 })
            {
                Marshal.WriteInt16(pointer, 18, validBits);
                WaveFormat invalid = WaveFormat.MarshalFromPtr(pointer);
                Throws<NotSupportedException>(() => AudioMeterSource.DescribeFormat(invalid), "Malformed extensible valid-bit depth is rejected");
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var misaligned = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 48000, 2, 192000, 3, 16);
        Throws<NotSupportedException>(() => AudioMeterSource.DescribeFormat(misaligned), "Invalid PCM block alignment cannot misassign channels");
        Throws<NotSupportedException>(() => AudioMeterSource.DescribeFormat(new WaveFormat(48000, 16, 33)), "Unsupported channel allocation is rejected before capture");
        Throws<ArgumentNullException>(() => AudioMeterSource.DescribeFormat(null!), "A missing descriptor is rejected");
    }

    private static void VerifyAudioPeakInterface()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            foreach (int scale in new[] { 100, 200 })
            {
                Localization.SetLanguage(language);
                VerifyAudioPeakLifecycle(language, scale);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyAudioPeakLifecycle(string language, int scale)
    {
        using var source = new FakeAudioMeterSource();
        long timestamp = 1000;
        var preferences = new UiPreferences { Language = language, Theme = scale == 100 ? "Light" : "Dark", TextScale = scale,
            ShowHints = false, ShowOperationMessages = false };
        var window = new AudioMeterWindow(() => null, source, readTimestamp: () => timestamp);
        window.ApplyPreferences(preferences);
        if (scale == 200) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        MoveOffscreen(window);
        window.Show(); Pump();
        try
        {
            ComboBox inputs = FindById<ComboBox>(window, "AudioInputChoice");
            Button start = FindById<Button>(window, "StartAudioMeter");
            Button stop = FindById<Button>(window, "StopAudioMeter");
            Button reset = FindById<Button>(window, "ResetAudioPeaks");
            Button read = FindById<Button>(window, "ReadAudioLevels");
            TextBlock reading = FindById<TextBlock>(window, "AudioLevelsReadout");
            TextBlock result = FindById<TextBlock>(window, "SectionResultReadout");
            ProgressBar first = FindById<ProgressBar>(window, "AudioLevel1");
            ProgressBar second = FindById<ProgressBar>(window, "AudioLevel2");
            Check(source.Starts == 0 && !reset.IsEnabled && inputs.SelectedIndex == -1, "Peaks do not select or start an input: " + language);
            VerifyNamedPeer(reset, Localization.Text("Meter.ResetPeaksName"), AutomationControlType.Button, enabled: false);
            VerifyNamedPeer(reading, reading.Text, AutomationControlType.Text);
            Check(GetAccessiblePeer(reset).GetName().StartsWith(Localization.Text("Meter.ResetPeaks").Replace("_", string.Empty), StringComparison.Ordinal),
                "Peak reset accessible name begins with its exact visible action caption");
            VerifyReferenceReadoutLabel(window, reading, Localization.Text("Meter.LevelsName"));
            inputs.SelectedIndex = 1;
            InvokeAccessible(start);
            VerifyNamedPeer(reset, Localization.Text("Meter.ResetPeaksName"), AutomationControlType.Button);
            Check(reset.IsEnabled && reading.Text == Localization.Text("Meter.WaitingForSamples"), "Reset is available only during measurement");

            byte[] impulse = new byte[512];
            BinaryPrimitives.WriteInt16LittleEndian(impulse, short.MaxValue);
            for (int offset = 2; offset < impulse.Length; offset += 4)
                BinaryPrimitives.WriteInt16LittleEndian(impulse.AsSpan(offset), -8192);
            AudioChannelLevel[] measured = AudioLevelCalculator.Measure(impulse, 2, 16, false);
            source.EmitMeasurement(measured);
            measured[0] = new AudioChannelLevel(-96, -96, false, false);
            source.Emit([-60, -50]);
            PumpUntil(() => reading.Text != Localization.Text("Meter.WaitingForSamples"));
            string expected = Localization.Text("Meter.Levels", 2, string.Join(Localization.Text("Meter.ChannelSeparator"),
                ExpectedAudioChannel(1, -60, 20 * Math.Log10(32767 / 32768.0), true),
                ExpectedAudioChannel(2, -50, 20 * Math.Log10(0.25), false)));
            Check(ReadAccessibleValue(reading) == expected && first.Value == -60 && second.Value == -50,
                "A brief integer maximum is retained between display ticks without replacing current RMS or trusting mutated arrays; actual=" + reading.Text);
            Check(reading.IsVisible && !result.IsVisible && AutomationProperties.GetLiveSetting(reading) == AutomationLiveSetting.Off,
                "A full-scale warning stays essential text with hints/messages off, without continuous live announcements");
            InvokeAccessible(read);
            Check(result.IsVisible && ReadAccessibleValue(result) == expected && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Explicit Read levels exposes RMS, peaks and warnings even with optional messages disabled");
            VerifyFocusOrder(new UIElement[] { reset, read }, false);
            RenderWindow(window, $"audio-peaks-{language}-{scale}.png");

            timestamp += 2100;
            source.Emit([]);
            InvokeAccessible(read);
            Check(first.Value == -96 && second.Value == -96 && reading.Text.Contains(Localization.Text("Meter.WaitingForSamples"), StringComparison.Ordinal)
                && reading.Text.Contains(Localization.Text("Meter.FullScaleReached"), StringComparison.Ordinal)
                && !reading.Text.Contains("RMS", StringComparison.Ordinal),
                "Empty callbacks cannot refresh stale data; retained warning is explicitly historical without a current RMS value");
            InvokeAccessible(reset);
            Check(source.Starts == 1 && source.Stops == 0 && reset.IsEnabled && !result.IsVisible
                && reading.Text == Localization.Text("Meter.WaitingForSamples") && !second.IsVisible,
                "Reset clears all evidence and waits for new samples without reopening or stopping the input");
            source.Emit([-40, -60]);
            InvokeAccessible(read);
            expected = Localization.Text("Meter.Levels", 2, string.Join(Localization.Text("Meter.ChannelSeparator"),
                ExpectedAudioChannel(1, -40, -40, false), ExpectedAudioChannel(2, -60, -60, false)));
            Check(reading.Text == expected, "A quiet buffer after reset does not inherit an old peak or full-scale warning");

            byte[] overshoot = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(overshoot, BitConverter.SingleToInt32Bits(1.5f));
            source.EmitMeasurement(AudioLevelCalculator.Measure(overshoot, 1, 32, true));
            InvokeAccessible(read);
            Check(first.Value == 0 && !second.IsVisible && reading.Text == Localization.Text("Meter.Levels", 1,
                ExpectedAudioChannel(1, 0, 20 * Math.Log10(1.5), true)),
                "Float overshoot remains a positive sample peak while the RMS bar stays bounded");
            source.Emit([-20, -30, -40]);
            InvokeAccessible(read);
            Check(reading.Text == Localization.Text("Meter.Levels", 3, string.Join(Localization.Text("Meter.ChannelSeparator"),
                ExpectedAudioChannel(1, -20, -20, false), ExpectedAudioChannel(2, -30, -30, false), ExpectedAudioChannel(3, -40, -40, false))),
                "Changing actual channel layout clears incompatible history and includes all supplied channels");
            InvokeAccessible(stop);
            inputs.SelectedIndex = 0;
            InvokeAccessible(start);
            Check(source.Starts == 2 && source.Stops == 1 && source.Selected == "other"
                && reading.Text == Localization.Text("Meter.WaitingForSamples"), "Restart on another selected input cannot inherit previous evidence");
            BinaryPrimitives.WriteInt32LittleEndian(overshoot, BitConverter.SingleToInt32Bits(float.NaN));
            source.EmitMeasurement(AudioLevelCalculator.Measure(overshoot, 1, 32, true));
            Pump();
            Check(source.Stops == 2 && start.IsEnabled && !stop.IsEnabled && !reset.IsEnabled && first.Value == -96
                && result.IsVisible && ReadAccessibleValue(result) == Localization.Text("Meter.InvalidSamples")
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite
                && reading.Text == Localization.Text("Meter.Stopped"),
                "Invalid samples stop the fake input with an essential bilingual error, not healthy silence or stale peaks");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(result.Text == Localization.Text("Meter.InvalidSamples") && source.Starts == 2 && source.Stops == 2,
                "A routed reset cannot bypass the stopped/error state");
            window.ApplyPreferences(preferences with { ShowHints = true });
            Pump();
            Check(Descendants(window).OfType<TextBlock>().Single(block => block.Text == Localization.Text("Meter.LevelHint")).IsVisible,
                "Peak explanations can be re-enabled without removing essential labels or warnings");
            VerifyAccessibleControls(window);
        }
        finally { window.Close(); Pump(); }
    }
}
