using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyParityIntegration()
    {
        using var transport = new FakeTransport();
        using var desktop = new ControlDesktop();
        var receiver = new ReceiverInfo("Test LARK A1", "simulated://parity", 0, 64);
        var window = new MainWindow(() => [receiver], _ => transport,
            Path.Combine(ReportDirectory, "parity-integration"), desktop,
            (_, title) => title != Localization.Text("Settings.Save.Title"), automaticReceiverManagement: false);
        MoveOffscreen(window); window.Show(); PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        try
        {
            Check(desktop.Snapshot is not null && !desktop.Snapshot().CanAdjustGain, "Tray integration starts with disabled disconnected controls");
            RunTask(window.ConnectAsync());
            RunTask(window.ReadIdentitiesAsync());
            Window information = window.OpenInformationWindow(); MoveOffscreen(information); Pump();
            string summary = FindById<TextBox>(information, "DeviceInformationReadout").Text;
            Check(summary.Contains("TEST-SN-0") && summary.Contains("TEST-SN-2") && summary.Contains("01:02:03:04:05:06"), "Identity read populates real controller results in information window");
            Check(transport.Commands.Count(command => command == A1Command.GetSerialNumber) == 3, "Identity UI reads only RX and connected TX1/TX2");
            information.Close(); Pump();
            Window settings = window.OpenSettingsWindow(); MoveOffscreen(settings); Pump();
            SelectAccessibleTab(FindById<TabControl>(settings, "SettingsTabs"), 3, "Uruchamianie i zamykanie");
            Check(settings.FindName("AutoConnectAtStartupCheckBox") is null && settings.FindName("ReceiverMemoryHint") is null,
                "Startup has no legacy automatic-connection toggle or remembered-device hint");
            Check(FindById<CheckBox>(settings, "StartWithWindowsCheckBox").IsEnabled,
                "Startup still exposes the independent Windows startup setting");
            Check(settings.FindName("ReceiverUsageHint") is null,
                "Startup contains its application preference without an instructional edit field");
            SelectAccessibleTab(FindById<TabControl>(settings, "SettingsTabs"), 4, "Profile audio");
            FindById<TextBox>(settings, "AudioProfileNameTextBox").Text = "Świeży odczyt";
            int readsBeforeProfile = transport.Commands.Count(command => command == A1Command.GetStatus);
            FindById<Button>(settings, "SaveAudioProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !window.IsBusy && FindById<ListBox>(settings, "AudioProfilesList").Items.Count == 1);
            Check(transport.Commands.Count(command => command == A1Command.GetStatus) > readsBeforeProfile,
                "Creating a profile in the actual settings window reads fresh receiver status first");
            FindById<Button>(settings, "CancelSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            int previousGain = desktop.Snapshot!().ConfirmedGain!.Value;
            window.WindowState = WindowState.Minimized; Pump();
            Check(window.WindowState == WindowState.Minimized, "Gain feedback fixture starts minimized");
            RunTask(desktop.Adjust!(1));
            Check(desktop.Snapshot().ConfirmedGain == previousGain + 1, "Tray gain invokes main controller and confirmed readback");
            Check(window.IsVisible && window.WindowState == WindowState.Normal, "Tray gain restores a minimized window for operation feedback");
            window.WindowState = WindowState.Minimized; Pump();
            RunTask(desktop.Mute!(0));
            Check(window.IsVisible && window.WindowState == WindowState.Normal, "Tray mute restores a minimized window for operation feedback");
            Check(!window.AudioSettings.Single(setting => setting.Id == "Noise").IsEnabled,
                "Noise control is blocked when every connected transmitter is muted");
            window.Hide(); Pump();
            RunTask(desktop.Mute(0));
            Check(window.IsVisible, "Tray mute restores a hidden window before hardware operation feedback");
            Check(window.AudioSettings.Single(setting => setting.Id == "Noise").IsEnabled, "Noise control returns when a connected TX is audible");
            window.WindowState = WindowState.Minimized; Pump();
            typeof(MainWindow).GetMethod("OnDesktopFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [new InvalidOperationException("Test desktop integration failure")]);
            Check(window.WindowState == WindowState.Normal && FindById<TextBlock>(window, "ResultReadout").Text.Contains("Test desktop integration failure"),
                "Desktop integration failure restores minimized window and exposes the error");
            SavePreferences(window, window.CurrentPreferences with { HiddenRows = ["TX1"], ValueDisplayMode = ValueDisplayMode.StaticText });
            Check(window.Transmitters[0].RowVisibility == Visibility.Collapsed && window.Transmitters[1].RowVisibility == Visibility.Visible, "Individual TX row visibility takes effect");
            TextBlock staticReadout = Descendants(window).OfType<TextBlock>().First(block => block.Style == window.FindResource("FocusableReadoutStyle") && block.IsVisible);
            Check(staticReadout.Focusable && KeyboardNavigation.GetIsTabStop(staticReadout), "Static values are keyboard focus stops");
            SavePreferences(window, window.CurrentPreferences with { HiddenRows = UiPreferences.SupportedRowIds.ToArray() });
            Check(FindById<TextBlock>(window, "EmptyControlsReadout").Visibility == Visibility.Visible, "All hidden rows produce an accessible explanation");
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
            Check(FindById<ListBox>(window, "ReceiverList").IsEnabled
                && FindById<TextBlock>(window, "ActiveReceiverReadout").IsVisible,
                "Connected receiver exposes its active identity and an enabled physical selector");
            Check(window.CurrentPreferences.PreferredReceiverPath is null,
                "Connecting and using settings never creates a legacy remembered receiver");
            transport.SetTransmitterConnected(0, false); transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            int writesWithoutMicrophones = CountMockWrites(transport);
            foreach (SettingRowViewModel setting in AllSettings(window))
            {
                Check(!setting.IsEnabled && !setting.CanApply, "No connected microphone blocks the backend write: " + setting.Id);
                RunTask(window.ApplySettingAsync(setting));
                Check(CountMockWrites(transport) == writesWithoutMicrophones && window.IsConnected
                    && window.ResultText == Localization.Text("Main.Change.TransmitterRequired"),
                    "No microphone reports the guard without writing or discarding the valid USB session: " + setting.Id);
            }
            Check(window.DeviceSettings.Single(setting => setting.Id == "AutoOff").Options[0].Contains("sparowania"), "Auto-off label describes unpaired state, not silence or inactivity");
        }
        finally { window.Close(); Pump(); }

        using var fourTransport = new FakeTransport { VersionBytes = [1, 1, 1, 3], StatusPayloadLength = 23 };
        fourTransport.SetTransmitterConnected(1, false);
        MainWindow four = CreateWindow(fourTransport, "four-firmware-identification");
        try
        {
            RunTask(four.ConnectAsync());
            Check(four.DeviceSettings.Single(setting => setting.Id == "Identification").IsEnabled,
                "Positive four-TX firmware identity allows identification with one active transmitter");
        }
        finally { four.Close(); Pump(); }
        VerifyLegacyReceiverPreferenceIsolation();
    }

    private static void VerifyLegacyReceiverPreferenceIsolation()
    {
        foreach (bool present in new[] { false, true })
        {
            using var transport = new FakeTransport();
            using var desktop = new ControlDesktop();
            string directory = Path.Combine(ReportDirectory, "legacy-receiver-isolation-" + present);
            var store = new UiPreferencesStore(directory);
            Check(store.TrySave(new UiPreferences { AutoConnectAtStartup = true, PreferredReceiverPath = "simulated://wanted", ConfirmExit = false }, out _), "Prepare legacy receiver preference fixture with explicit prompt-free teardown");
            byte[] original = File.ReadAllBytes(store.FilePath);
            var receivers = new List<ReceiverInfo> { new("Other LARK", "simulated://other", 0, 64) };
            if (present) receivers.Add(new("Wanted LARK", "simulated://wanted", 0, 64));
            string? selected = null;
            var window = new MainWindow(() => receivers, receiver => { selected = receiver.Path; return transport; }, directory, desktop,
                automaticReceiverManagement: false);
            MoveOffscreen(window); window.Show(); PumpUntil(() => !window.IsBusy && window.Receivers.Count == receivers.Count);
            try
            {
                SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
                ListBox physical = FindById<ListBox>(window, "ReceiverList");
                Check(!window.IsConnected && selected is null && transport.Commands.Count == 0,
                    "Isolated backend fixture does not auto-open from legacy preferences: " + present);
                Check(physical.SelectedItem is not ReceiverInfo initial || initial.Path != "simulated://wanted",
                    "Legacy receiver identity does not choose the physical list selection: " + present);
                physical.SelectedIndex = 0;
                RunTask(window.ConnectAsync());
                Check(window.IsConnected && selected == "simulated://other",
                    "Explicit backend connection follows only the current physical list selection: " + present);
                Check(original.SequenceEqual(File.ReadAllBytes(store.FilePath))
                    && window.CurrentPreferences.PreferredReceiverPath == "simulated://wanted",
                    "Legacy preferences remain unchanged by discovery and backend connection: " + present);
                Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                    "Legacy preference isolation performs status/version reads only");
            }
            finally { window.Close(); Pump(); }
        }
    }

    private sealed class ControlDesktop : IDesktopIntegration
    {
        public Func<TrayControlSnapshot>? Snapshot { get; private set; }
        public Func<int, Task>? Adjust { get; private set; }
        public Func<int, Task>? Mute { get; private set; }
        public void ConfigureTrayControls(Func<TrayControlSnapshot> snapshot, Func<int, Task> adjust, Func<int, Task> mute)
        { Snapshot = snapshot; Adjust = adjust; Mute = mute; }
        public StartupRegistrationSnapshot CaptureStartup() => StartupRegistrationSnapshot.Missing;
        public void SetStartup(bool enabled) => throw new InvalidOperationException("This test must not change startup registration.");
        public void RestoreStartup(StartupRegistrationSnapshot snapshot) { }
        public void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit) { }
        public void HideTray() { }
        public void Dispose() { }
    }
}
