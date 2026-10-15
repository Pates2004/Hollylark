using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyHardwareUserActions()
    {
        IReadOnlyList<ReceiverInfo> receivers = HidTransport.FindReceivers();
        Check(receivers.Count == 1, "Exactly one receiver for end-to-end UI test");
        A1Status baseline;
        using (var initial = new A1Controller(new HidTransport(receivers[0])))
            baseline = initial.ReadStatusAsync().GetAwaiter().GetResult();
        Check(baseline.Transmitters.Count == 2 && baseline.Transmitters.All(transmitter => transmitter.Connected),
            "Both microphones connected before end-to-end UI test");
        Check(baseline.IdentificationEnabled.HasValue, "Identification baseline is available for safe restoration");
        Directory.CreateDirectory(ReportDirectory);
        string reportPath = Path.Combine(ReportDirectory, "hardware-ui-settings.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Baseline = baseline, Restored = false }));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(HidTransport.FindReceivers, receiver => new HidTransport(receiver), ReportDirectory,
            confirm: (_, _) => true, automaticReceiverManagement: false);
        var operations = new List<string>();
        string trace = string.Empty;
        Exception? failure = null;
        A1Status? finalStatus = null;
        bool restored = false;
        MoveOffscreen(window);
        window.Show();
        try
        {
            PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && window.Transmitters.All(transmitter => transmitter.CanMute),
                "Production UI reads online TX firmware and enables mute");
            foreach (SettingRowViewModel setting in AllSettings(window))
            {
                ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
                Pump();
                int originalIndex = setting.SelectedIndex;
                int changedIndex = (originalIndex + 1) % setting.Options.Count;
                ComboBox choice = FindById<ComboBox>(window, setting.ChoiceId);
                Button apply = FindById<Button>(window, setting.ApplyId);
                Check(apply.IsEnabled, setting.Id + ": real Apply button enabled");
                choice.SelectedIndex = changedIndex;
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !window.IsBusy);
                Check(window.IsConnected && !setting.IsDirty && setting.ConfirmedValue == setting.Options[changedIndex] &&
                    window.ResultText.Contains("potwierdziło"), setting.Id + ": button confirms physical setting readback");
                choice.SelectedIndex = originalIndex;
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !window.IsBusy);
                Check(window.IsConnected && !setting.IsDirty && setting.ConfirmedValue == setting.Options[originalIndex],
                    setting.Id + ": button restores original visible value");
                operations.Add(setting.Id);
            }
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
            Pump();
            for (int index = 0; index < 2; index++)
            {
                Button mute = FindById<Button>(window, $"TX{index + 1}.Mute");
                mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !window.IsBusy);
                Check(window.IsConnected && window.Transmitters[index].IsMuted != baseline.Transmitters[index].Muted,
                    $"TX{index + 1}: real mute button changes reported state");
                mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !window.IsBusy);
                Check(window.IsConnected && window.Transmitters[index].IsMuted == baseline.Transmitters[index].Muted,
                    $"TX{index + 1}: real mute button restores reported state");
                operations.Add($"TX{index + 1}.Mute");
            }
            Window information = window.OpenInformationWindow();
            MoveOffscreen(information);
            Pump();
            trace = FindById<TextBox>(information, "DiagnosticLogReadout").Text;
            information.Close();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            window.Disconnect();
            window.Close();
            application.Shutdown();
            try
            {
                using var recovery = new A1Controller(new HidTransport(receivers[0]));
                A1Status current = recovery.ReadStatusAsync().GetAwaiter().GetResult();
                foreach (HardwareSettingCase test in CreateHardwareSettingCases(baseline))
                    if (!IsRestorationConfirmed(test.Restore, current))
                        current = recovery.ApplyAsync(test.Restore).GetAwaiter().GetResult();
                finalStatus = recovery.ReadStatusAsync().GetAwaiter().GetResult();
                restored = SameConfiguration(baseline, finalStatus);
                Check(restored, "End-to-end UI test restores full baseline including disabled-effect levels");
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Time = DateTimeOffset.Now, Baseline = baseline, Operations = operations, Trace = trace,
                FinalStatus = finalStatus, Restored = restored, Failure = failure?.ToString(), AcousticEffectsTested = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Hardware UI settings report: " + reportPath);
        }
        if (failure is not null)
            throw new InvalidOperationException($"Hardware UI test failed. Original settings restored: {restored}. See {reportPath}", failure);
    }
}
