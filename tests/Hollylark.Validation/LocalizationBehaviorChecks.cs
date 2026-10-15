using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyLocalizedBehavior()
    {
        Check(Application.Current is not null, "Localized behavior uses the existing WPF application");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage("en");
            VerifyLocalizedMainBehavior();
            VerifyLocalizedConnectionFailure();
            VerifyLocalizedAudioBehavior();
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyLocalizedMainBehavior()
    {
        using var transport = new FakeTransport { RejectAfterWrites = 1, DelayMilliseconds = 10 };
        MainWindow window = CreatePresentationWindow(transport, "localized-behavior-main-en", new MemoryDesktop());
        try
        {
            window.Activate();
            Pump();
            TextBlock connection = FindById<TextBlock>(window, "ConnectionReadout");
            TextBlock result = FindById<TextBlock>(window, "ResultReadout");
            Check(connection.Text == Localization.Text("Main.Usb.Waiting")
                && ReadAccessibleValue(result) == Localization.Text("Main.Usb.Waiting"),
                "English isolated discovery exposes the exact waiting status and result");
            TabControl tabs = FindById<TabControl>(window, "MainTabs");
            SelectAccessibleTab(tabs, 1, Localization.Text("Main.Receiver.TabName"));
            TextBlock active = FindById<TextBlock>(window, "ActiveReceiverReadout");
            FocusForKeyboardCheck(active);
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && connection.Text == Localization.Text("Main.Usb.Ready", window.Receivers[0].Name, 2, 2)
                && GetAccessiblePeer(connection).GetName() == connection.Text,
                "Simulated connection updates the exact English connected count and UIA name");
            Check(ReadAccessibleValue(result) == Localization.Text("Main.Usb.Ready", window.Receivers[0].Name, 2, 2)
                && GetAccessiblePeer(result).GetName() == Localization.Text("Main.Result.AccessibleName", result.Text),
                "English connection completion is exposed through UIA Value and Name");
            CheckKeyboardFocus(active, "English backend connection preserves the essential receiver readout focus");
            SelectAccessibleTab(tabs, 0, Localization.Text("Main.Control.Name"));

            var expectedValues = new Dictionary<string, string>
            {
                ["Gain"] = Localization.Text("Setting.Gain.Option", 4),
                ["Noise"] = Localization.Text("Setting.Noise.Strong"),
                ["Reverb"] = Localization.Text("Setting.Reverb.LevelThree")
            };
            foreach (var entry in expectedValues)
            {
                SettingRowViewModel setting = window.AudioSettings.Single(candidate => candidate.Id == entry.Key);
                Check(setting.ConfirmedValue == entry.Value
                    && ReadAccessibleValue(FindById<TextBlock>(window, entry.Key + ".StaticCurrent")) == Localization.Text("Setting.Value", setting.DisplayName, entry.Value),
                    "English confirmed setting matches the simulated snapshot and UIA readout: " + entry.Key);
            }
            foreach (var (number, battery, muted) in new[] { (1, 90, false), (2, 50, true) })
            {
                string status = Localization.Text("Transmitter.Connected", Localization.Text("Transmitter.BatteryPercent", battery),
                    Localization.Text(muted ? "Transmitter.Muted" : "Transmitter.Active"));
                Check(ReadTransmitterSummary(window, number) == Localization.Text("Transmitter.Summary", number, status),
                    "English native transmitter item includes the exact battery and mute state: " + number);
                SelectTransmitter(window, number);
                VerifyNamedPeer(FindById<Button>(window, $"TX{number}.Mute"),
                    Localization.Text(muted ? "Transmitter.UnmuteName" : "Transmitter.MuteName", number), AutomationControlType.Button);
            }

            FindById<ListBox>(window, "TransmitterList").SelectedItem = window.TransmitterTargets.Last();
            Pump();
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            ComboBox choice = FindById<ComboBox>(window, "Gain.Choice");
            Button apply = FindById<Button>(window, "Gain.Apply");
            string gainName = Localization.Text("Setting.Gain.Name");
            VerifyNamedPeer(choice, Localization.Text("Setting.ChoiceName", gainName), AutomationControlType.ComboBox);
            VerifyNamedPeer(apply, Localization.Text("Setting.ApplyName", gainName), AutomationControlType.Button);
            Check(GetAccessiblePeer(choice).GetHelpText() == Localization.Text("Setting.Gain.RangeHint") + " " + Localization.Text("Setting.Choice.ManualHint"),
                "English gain selector exposes range and manual-apply guidance only as optional hints");
            FocusForKeyboardCheck(choice);
            RaiseLocalKey(choice, Key.Down);
            Check(choice.SelectedIndex == 4 && gain.IsDirty && CountMockWrites(transport) == 0
                && gain.ConfirmedValue == Localization.Text("Setting.Gain.Option", 4),
                "English keyboard draft changes the selection without fabricating confirmed readback or sending a setting");
            Check(gain.StatusText == Localization.Text("Setting.Unapplied")
                && !Descendants(window).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == "Gain.Status"),
                "The English draft remains in the model without a redundant instructional status control");
            MoveKeyboardFocus(choice, apply, FocusNavigationDirection.Next);
            MoveKeyboardFocus(apply, choice, FocusNavigationDirection.Previous);
            FocusForKeyboardCheck(result);
            InvokeAccessible(apply);
            PumpUntil(() => !window.IsBusy && !gain.IsDirty && transport.Commands.Contains(A1Command.SetGain));
            Check(ReadAccessibleValue(FindById<TextBlock>(window, "Gain.StaticCurrent")) == Localization.Text("Setting.Value", gainName, Localization.Text("Setting.Gain.Option", 5))
                && ReadAccessibleValue(result) == Localization.Text("Main.Apply.Success", gainName)
                && transport.Commands.Count(command => command == A1Command.SetGain) == 1 && !gain.IsDirty,
                "UIA Apply performs one simulated write and exposes exact English confirmed readback");
            CheckKeyboardFocus(result, "English Apply preserves focus on the independently focused result");

            SavePreferences(window, window.CurrentPreferences with
            {
                ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false
            });
            choice = FindById<ComboBox>(window, "Gain.Choice");
            Check(GetAccessiblePeer(choice).GetHelpText() == string.Empty
                && GetAccessiblePeer(choice).GetName() == Localization.Text("Setting.ChoiceName", gainName),
                "Disabling hints removes English optional UIA help without removing the selector name; help="
                + GetAccessiblePeer(choice).GetHelpText() + "; name=" + GetAccessiblePeer(choice).GetName()
                + "; attached=" + AutomationProperties.GetHelpText(choice) + "; enabled=" + UsageHints.GetEnabled(choice));
            Check(result.Visibility == Visibility.Collapsed && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                "English successful-operation feedback is hidden and silent when disabled");
            Check(GetAccessiblePeer(FindById<Button>(window, "RefreshButton")).GetHelpText() == string.Empty,
                "The optional English refresh explanation is hidden when hints are disabled");

            transport.SetTransmitterConnected(1, false);
            RunTask(window.RefreshAsync());
            Check(connection.Text == Localization.Text("Main.Usb.Ready", window.Receivers[0].Name, 1, 2)
                && ReadTransmitterSummary(window, 2) == Localization.Text("Transmitter.Summary", 2,
                    Localization.Text("Transmitter.Disconnected")) && !window.Transmitters[1].CanMute,
                "English refresh replaces stale native transmitter telemetry and preserves the backend mute guard");
            SelectTransmitter(window, 1);
            gain.SelectedIndex = 0;
            RunTask(window.ApplySettingAsync(gain));
            string failure = Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.InvalidResponse"));
            Check(!window.IsConnected && transport.Disposed
                && transport.Commands.Count(command => command == A1Command.SetGain) == 2,
                "A rejected simulated setting closes the session rather than reporting success");
            Check(result.IsVisible && ReadAccessibleValue(result) == failure
                && GetAccessiblePeer(result).GetName() == Localization.Text("Main.Result.AccessibleName", failure)
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Exact English rejection feedback remains visible and accessible with all optional messages disabled");
            Check(ReadAccessibleValue(FindById<TextBlock>(window, "Gain.StaticCurrent")) == Localization.Text("Setting.Value", gainName, Localization.Text("Setting.NoData"))
                && !gain.CanApply && connection.Text == Localization.Text("Main.Usb.Unavailable", window.Receivers[0].Name),
                "English rejection clears stale confirmed values and disables further writes");
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion or A1Command.SetGain),
                "Localized main behavior uses only snapshot reads and the two intentional fake gain writes");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyLocalizedConnectionFailure()
    {
        using var transport = new FakeTransport { Fail = true };
        MainWindow window = CreatePresentationWindow(transport, "localized-behavior-connection-error-en", new MemoryDesktop());
        try
        {
            SavePreferences(window, window.CurrentPreferences with { ShowHints = false, ShowOperationMessages = false });
            RunTask(window.ConnectAsync());
            TextBlock result = FindById<TextBlock>(window, "ResultReadout");
            string failure = Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.Failed"));
            Check(!window.IsConnected && transport.Disposed && result.IsVisible && ReadAccessibleValue(result) == failure,
                "Simulated removal during connection exposes the exact essential English connection error");
            Check(GetAccessiblePeer(result).GetName() == Localization.Text("Main.Result.AccessibleName", failure)
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "English connection failure retains its UIA error name and live-region state with optional feedback disabled");
            Check(transport.Commands.Count > 0 && transport.Commands.All(command => command == A1Command.GetStatus)
                && AllSettings(window).All(setting => !setting.CanApply),
                "Failed simulated connection never writes settings or enables disconnected controls");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyLocalizedAudioBehavior()
    {
        using var source = new FakeAudioMeterSource();
        var cleanupErrors = new List<string>();
        var meter = new AudioMeterWindow(() => 0, source, reportError: cleanupErrors.Add);
        try
        {
            meter.ApplyPreferences(new UiPreferences { Language = "en" });
            MoveOffscreen(meter);
            meter.Show();
            Pump();
            ComboBox inputs = FindById<ComboBox>(meter, "AudioInputChoice");
            Button start = FindById<Button>(meter, "StartAudioMeter");
            Button stop = FindById<Button>(meter, "StopAudioMeter");
            TextBlock reading = FindById<TextBlock>(meter, "AudioLevelsReadout");
            TextBlock result = FindById<TextBlock>(meter, "SectionResultReadout");
            ProgressBar first = FindById<ProgressBar>(meter, "AudioLevel1");
            ProgressBar second = FindById<ProgressBar>(meter, "AudioLevel2");
            Check(source.Starts == 0 && inputs.SelectedIndex == -1 && !start.IsEnabled
                && ReadAccessibleValue(reading) == Localization.Text("Meter.Stopped"),
                "English meter starts stopped without selecting or capturing an input");
            inputs.SelectedIndex = 1;
            InvokeAccessible(start);
            Check(source.Starts == 1 && source.Selected == "lark" && stop.IsEnabled && !inputs.IsEnabled
                && ReadAccessibleValue(reading) == Localization.Text("Meter.WaitingForSamples")
                && ReadAccessibleValue(result) == Localization.Text("Meter.Started", "LARK A1"),
                "English Start invokes only the selected fake input and reports waiting without fabricated levels");
            VerifyNamedPeer(reading, reading.Text, AutomationControlType.Text);
            source.Emit([-6.02, -12.04]);
            FindById<Button>(meter, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string expectedLevels = Localization.Text("Meter.Levels", 2,
                string.Join(Localization.Text("Meter.ChannelSeparator"),
                    ExpectedAudioChannel(1, -6.02, -6.02, false),
                    ExpectedAudioChannel(2, -12.04, -12.04, false)));
            Check(ReadAccessibleValue(reading) == expectedLevels && ReadAccessibleValue(result) == expectedLevels
                && first.Value == -6.02 && second.Value == -12.04 && second.IsVisible,
                "Emitted stereo samples produce exact English decimal levels and both bars despite a mono receiver callback");

            meter.ApplyPreferences(new UiPreferences { Language = "en", ShowHints = false, ShowOperationMessages = false });
            Pump();
            TextBlock hint = Descendants(meter).OfType<TextBlock>().Single(block => block.Text == Localization.Text("Meter.LevelHint"));
            TextBlock instruction = Descendants(meter).OfType<TextBlock>().Single(block => block.Text == Localization.Text("Meter.Instruction"));
            Check(hint.Visibility == Visibility.Collapsed && instruction.Visibility == Visibility.Collapsed
                && GetAccessiblePeer(inputs).GetName() == Localization.Text("Meter.InputName"),
                "English meter explanations follow disabled hints while the essential input name remains");
            InvokeAccessible(stop);
            Check(ReadAccessibleValue(reading) == Localization.Text("Meter.Stopped")
                && result.Text == Localization.Text("Meter.StoppedResult") && result.Visibility == Visibility.Collapsed,
                "English stopped state stays readable while optional success feedback is hidden");
            InvokeAccessible(start);
            Check(source.Starts == 2 && result.Text == Localization.Text("Meter.Started", "LARK A1")
                && result.Visibility == Visibility.Collapsed,
                "Restarting the fake input respects the disabled English operation-message preference");
            source.Emit([-10, -20]);
            FindById<Button>(meter, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.StopFailure = true;
            source.EmitFailure("Simulated input loss");
            Pump();
            string stopFailure = Localization.Text("Meter.StopFailed", "Test audio cleanup failure");
            string combinedFailure = Localization.Text("Meter.CombinedError", "Simulated input loss", stopFailure);
            Check(result.IsVisible && ReadAccessibleValue(result) == combinedFailure
                && GetAccessiblePeer(result).GetName() == Localization.Text("Section.Result.WithMessage", combinedFailure)
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Fake input failure preserves the exact English cleanup error as essential UIA feedback");
            Check(ReadAccessibleValue(reading) == Localization.Text("Meter.DisplayInterrupted", stopFailure)
                && first.Value == AudioLevelCalculator.SilenceDecibels && second.Value == AudioLevelCalculator.SilenceDecibels
                && !second.IsVisible && !start.IsEnabled && stop.IsEnabled,
                "English cleanup failure clears stale samples, blocks restart and offers a stop retry");
            source.StopFailure = false;
            InvokeAccessible(stop);
            Check(source.Stops == 3 && start.IsEnabled && !stop.IsEnabled
                && ReadAccessibleValue(reading) == Localization.Text("Meter.Stopped")
                && result.Text == Localization.Text("Meter.StoppedResult") && result.Visibility == Visibility.Collapsed,
                "Successful fake stop retry restores the exact English stopped state and hides optional success feedback");
        }
        finally { source.StopFailure = false; meter.Close(); Pump(); }
        Check(source.Disposed && cleanupErrors.Count == 0, "Localized audio test disposes only its fake source without native cleanup dialogs");
    }
}
