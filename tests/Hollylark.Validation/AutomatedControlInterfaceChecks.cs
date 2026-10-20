using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static async Task VerifyExpectedStateSafety()
    {
        using var transport = new FakeTransport();
        using var controller = new A1Controller(transport);
        A1Status original = await controller.ReadStatusAsync();
        await ThrowsAsync<DeviceStateChangedException>(() => controller.ApplyAsync(SettingChange.Gain(5),
            expectedState: _ => false), "A failed expected-state predicate prevents the hardware write under the transaction lock");
        Check(CountMockWrites(transport) == 0, "Rejected expected state emits no setting command");
        A1Status confirmed = await controller.ApplyAsync(SettingChange.Gain(5), expectedState: status => status.Gain == original.Gain);
        Check(confirmed.Gain == 5 && CountMockWrites(transport) == 1, "A matching expected state applies and confirms one command");
    }

    private static void VerifyAutomatedControlInterface()
    {
        foreach (string language in new[] { "pl", "en" })
        {
            Localization.SetLanguage(language);
            VerifyShortcutSettingsInterface(language);
            VerifyShortcutDeviceActions(language);
            VerifyAutomaticRetentionInterface(language);
            VerifyDeferredRetentionInterface(language);
            VerifyShortcutRecoveryInterface(language);
        }
        VerifyRetentionHotplugChecks();
        Localization.SetLanguage("pl");
    }

    private static MainWindow CreateAutomationWindow(FakeTransport transport, string name, FakeHotkeyNativeApi native,
        List<string>? announcements = null, bool retain = true)
    {
        string directory = Path.Combine(ReportDirectory, name + "-" + Guid.NewGuid().ToString("N"));
        var store = new UiPreferencesStore(directory);
        Check(store.TrySave(new UiPreferences
        {
            FirstRunCompleted = true, Language = Localization.CurrentLanguage, ConfirmExit = false,
            ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = true, PreserveVolatileSettings = retain
        }, out string? error), "Prepare automation fixture preferences: " + error);
        var receiver = new ReceiverInfo("Simulated automation receiver", "simulated://automation-" + name, 0, 64);
        var window = new MainWindow(() => [receiver], _ => transport, directory, new MemoryDesktop(), (_, _) => false,
            announceState: announcements is null ? null : announcements.Add, automaticReceiverManagement: false, hotkeyNativeApi: native);
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        RunTask(window.ConnectAsync());
        return window;
    }

    private static void VerifyShortcutSettingsInterface(string language)
    {
        using var transport = new FakeTransport();
        var native = new FakeHotkeyNativeApi();
        MainWindow window = CreateAutomationWindow(transport, "shortcut-ui-" + language, native);
        try
        {
            Check(native.Registered.Count == 0 && !window.CurrentPreferences.GlobalHotkeysEnabled,
                "Default shortcut assignments do not register global keys: " + language);
            SavePreferences(window, window.CurrentPreferences with { GlobalHotkeysEnabled = true });
            int assigned = window.CurrentPreferences.Hotkeys.Count(binding => binding.Gesture.Length > 0);
            Check(native.Registered.Count == assigned, "Enabling registers assigned shortcuts against the fake native API");
            var settings = (SettingsWindow)window.OpenSettingsWindow();
            Check(native.Registered.Count == 0, "Opening Settings suspends global registrations before capture");
            var tabs = FindById<TabControl>(settings, "SettingsTabs");
            tabs.SelectedItem = settings.FindName("HotkeysSettingsTab");
            Pump();
            ListBox actions = FindById<ListBox>(settings, "HotkeyActionsList");
            TextBox field = FindById<TextBox>(settings, "HotkeyGestureTextBox");
            Button capture = FindById<Button>(settings, "CaptureHotkeyButton");
            Check(actions.Items.Count == Enum.GetValues<HotkeyAction>().Length
                && actions.Items.OfType<ListBoxItem>().All(item => item.Content is string text && text.Length > 0),
                "All shortcut actions are native named list items, not data objects: " + language);
            using (var observer = new ExternalArrowObserver(settings, "shortcut-actions-arrows-" + language, includeFocus: true))
                VerifyListItemNavigation(actions, observer);
            actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, HotkeyAction.IncreaseGain));
            Check(!field.IsReadOnly && AutomationProperties.GetName(field).Contains(SettingsWindow.HotkeyActionName(HotkeyAction.IncreaseGain)),
                "Selected action has an editable, action-labelled shortcut field");
            Check(AutomationProperties.GetHelpText(field).Length == 0, "Shortcut usage hints respect disabled hints");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.TryCaptureHotkey(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.J)
                && field.Text == "Ctrl+Alt+Shift+J", "Capture formats and stores the actually selected action combination");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(settings)!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            settings.RaiseEvent(escape);
            Check(settings.IsVisible && !settings.TryCaptureHotkey(ModifierKeys.Control, Key.K),
                "Escape cancels only shortcut capture, not Settings or the application");
            field.Text = "Ctrl+Alt+Shift+J";
            native.Fail = call => call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.J) ? 1409 : 0;
            FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.IsVisible && window.CurrentPreferences.Hotkeys.Single(binding => binding.Action == HotkeyAction.IncreaseGain).Gesture != field.Text,
                "A native conflict keeps Settings open and does not commit the candidate binding");
            native.Fail = null;
            FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(!settings.IsVisible && native.Registered.Count == assigned
                && window.CurrentPreferences.Hotkeys.Single(binding => binding.Action == HotkeyAction.IncreaseGain).Gesture == "Ctrl+Alt+Shift+J",
                "Successful OK commits bindings and resumes them after Settings closes");
            HotkeyBinding[] saved = window.CurrentPreferences.Hotkeys;
            SavePreferences(window, window.CurrentPreferences with { GlobalHotkeysEnabled = false });
            Check(native.Registered.Count == 0 && window.CurrentPreferences.Hotkeys.SequenceEqual(saved),
                "Disabling unregisters keys without discarding assignments");
        }
        finally { window.Close(); Pump(); }
        Check(native.Registered.Count == 0, "Closing the window releases all shortcut registrations");
    }

    private static void VerifyShortcutDeviceActions(string language)
    {
        using var transport = new FakeTransport();
        var announcements = new List<string>();
        MainWindow window = CreateAutomationWindow(transport, "shortcut-actions-" + language, new FakeHotkeyNativeApi(), announcements);
        try
        {
            SavePreferences(window, window.CurrentPreferences with { GlobalHotkeysEnabled = true });
            ListBox targets = FindById<ListBox>(window, "TransmitterList");
            targets.SelectedItem = window.Transmitters[0];
            int before = CountMockWrites(transport);
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.IncreaseGain));
            Check(CountMockWrites(transport) == before, "Individual shared-parameter shortcut cannot silently affect several active microphones");
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.ToggleSelectedMute));
            Check(window.Transmitters[0].IsMuted && window.Transmitters[1].IsMuted,
                "Selected-microphone shortcut uses indexed mute");
            targets.SelectedItem = window.TransmitterTargets.Last();
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.ToggleAllMute));
            Check(window.Transmitters.All(transmitter => !transmitter.IsMuted), "All-microphone shortcut confirms the whole group");
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.IncreaseGain));
            Check(window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 4,
                "Global gain shortcut applies immediately and waits for confirmed readback");
            SetAutomationStatusByte(transport, 8, 1);
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.IncreaseGain));
            Check(window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 2,
                "Gain shortcut calculates from fresh hardware status, not a stale displayed value");
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            gain.SelectedIndex = 3;
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus));
            Check(gain.IsDirty && gain.SelectedIndex == 3, "A global read shortcut refuses instead of discarding unsent choices");
            RunTask(window.RefreshAsync());
            SetAutomationStatusByte(transport, 8, 5);
            announcements.Clear();
            RunTask(Poll(window));
            Check(announcements.Any(message => message.Contains(Localization.Text("Main.State.Gain", 6))),
                "An external confirmed change is announced even with operation messages and hints disabled");
            int spoken = announcements.Count;
            RunTask(Poll(window));
            Check(announcements.Count == spoken, "Unchanged polls do not repeat device announcements");
            SavePreferences(window, window.CurrentPreferences with { AnnounceStateChanges = false });
            SetAutomationStatusByte(transport, 8, 4);
            RunTask(Poll(window));
            Check(announcements.Count == spoken, "Disabling state announcements silences external ordinary changes");
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            before = CountMockWrites(transport);
            window.Hide();
            RunTask(window.ExecuteHotkeyAsync(HotkeyAction.CycleLights));
            Check(CountMockWrites(transport) == before && announcements.Count > spoken,
                "Offline shortcuts send no device writes and still announce essential refusal errors");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyAutomaticRetentionInterface(string language)
    {
        using var transport = new FakeTransport();
        MainWindow window = CreateAutomationWindow(transport, "retention-ui-" + language, new FakeHotkeyNativeApi());
        try
        {
            Check(window.CurrentPreferences.PreserveVolatileSettings && CountMockWrites(transport) == 0,
                "Default-on retention learns initial active status without writing to hardware");
            SetAutomationStatusByte(transport, 8, 5);
            SetAutomationStatusByte(transport, 11, 1);
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == 0, "Deliberate continuous-session external changes are learned, not overwritten");
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            SetAutomationStatusByte(transport, 8, 0);
            SetAutomationStatusByte(transport, 11, 0);
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == 0, "Offline reset status never causes writes or destroys remembered active parameters");
            transport.SetTransmitterConnected(0, true);
            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            Check(window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 5
                && window.DeviceSettings.Single(setting => setting.Id == "Lights").SelectedIndex == 1
                && CountMockWrites(transport) == 2, "Observed return restores changed gain and LEDs once with readback");
            Check(!transport.Commands.Any(command => command == A1Command.SetMute), "Retention never sends mute or unmute commands");
            int writes = CountMockWrites(transport);
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == writes, "Steady polling does not loop restoration writes");
            SavePreferences(window, window.CurrentPreferences with { PreserveVolatileSettings = false });
            transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            SetAutomationStatusByte(transport, 8, 0);
            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == writes
                && window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 0,
                "Disabled retention leaves a reconnect reset untouched");
            SavePreferences(window, window.CurrentPreferences with { PreserveVolatileSettings = true });
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == writes, "Re-enabling retention takes a fresh baseline without replaying stale remembered settings");
        }
        finally { window.Close(); Pump(); }
    }

    private static void SetAutomationStatusByte(FakeTransport transport, int offset, byte value)
    {
        byte[] status = (byte[])typeof(FakeTransport).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
        status[offset] = value;
    }

    private static void VerifyDeferredRetentionInterface(string language)
    {
        foreach (bool identities in new[] { false, true })
        {
            using var transport = new FakeTransport();
            MainWindow window = CreateAutomationWindow(transport, "retention-read-" + language + identities, new FakeHotkeyNativeApi());
            try
            {
                transport.SetTransmitterConnected(0, false);
                transport.SetTransmitterConnected(1, false);
                RunTask(Poll(window));
                SetAutomationStatusByte(transport, 8, 0);
                transport.SetTransmitterConnected(0, true);
                transport.SetTransmitterConnected(1, true);
                RunTask(identities ? window.ReadIdentitiesAsync() : window.RefreshAsync());
                Check(CountMockWrites(transport) == 0, "Explicit status/identity reads stay read-only across a reconnect: " + identities);
                RunTask(Poll(window));
                Check(window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 3
                    && CountMockWrites(transport) == 1, "A subsequent background poll restores the deferred reconnect request: " + identities);
            }
            finally { window.Close(); Pump(); }
        }
    }

    private static void VerifyShortcutRecoveryInterface(string language)
    {
        using var transport = new FakeTransport();
        var native = new FakeHotkeyNativeApi();
        MainWindow window = CreateAutomationWindow(transport, "shortcut-recovery-" + language, native);
        try
        {
            SavePreferences(window, window.CurrentPreferences with { GlobalHotkeysEnabled = true });
            var settings = (SettingsWindow)window.OpenSettingsWindow();
            FindById<TabControl>(settings, "SettingsTabs").SelectedItem = settings.FindName("HotkeysSettingsTab");
            Pump();
            ListBox actions = FindById<ListBox>(settings, "HotkeyActionsList");
            actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, HotkeyAction.IncreaseGain));
            FindById<TextBox>(settings, "HotkeyGestureTextBox").Text = "Ctrl+Alt+Shift+J";
            var store = (UiPreferencesStore)typeof(MainWindow).GetField("preferencesStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            byte[] original = File.ReadAllBytes(store.FilePath);
            native.Block("Ctrl+Alt+Shift+Up");
            using (FileStream locked = File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.IsVisible && native.Registered.Count == 0 && File.ReadAllBytes(store.FilePath).SequenceEqual(original),
                "Failed persistence suspends candidate keys before restoring saved bindings, even when an old key is occupied");
            settings.Close();
            Pump();
            Check(native.Registered.Count == 0 && window.CurrentPreferences.Hotkeys.Single(binding => binding.Action == HotkeyAction.IncreaseGain).Gesture.EndsWith("+Up"),
                "Cancel after failed save never activates the unsaved candidate combination");
            settings = (SettingsWindow)window.OpenSettingsWindow();
            FindById<TabControl>(settings, "SettingsTabs").SelectedItem = settings.FindName("HotkeysSettingsTab");
            Pump();
            FindById<CheckBox>(settings, "EnableGlobalHotkeysCheckBox").IsChecked = false;
            FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(!settings.IsVisible && !window.CurrentPreferences.GlobalHotkeysEnabled && native.Registered.Count == 0,
                "Reopening after resume conflict does not nest suspension and still allows disabling shortcuts");
            HotkeyBinding[] recovered = window.CurrentPreferences.Hotkeys.Select(binding => binding.Action == HotkeyAction.IncreaseGain
                ? binding with { Gesture = "Ctrl+Alt+Shift+K" } : binding).ToArray();
            SavePreferences(window, window.CurrentPreferences with { GlobalHotkeysEnabled = true, Hotkeys = recovered });
            native.Fail = call => !call.Registering ? 5 : 0;
            settings = (SettingsWindow)window.OpenSettingsWindow();
            FindById<TabControl>(settings, "SettingsTabs").SelectedItem = settings.FindName("HotkeysSettingsTab");
            Pump();
            Check(!FindById<Button>(settings, "CaptureHotkeyButton").IsEnabled && native.Registered.Count > 0,
                "An unregister failure disables capture instead of pretending registered combinations will reach the editor");
            native.Fail = null;
            FindById<CheckBox>(settings, "EnableGlobalHotkeysCheckBox").IsChecked = false;
            FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(!settings.IsVisible && native.Registered.Count == 0, "After an unregister error, disabling shortcuts remains recoverable");
        }
        finally { window.Close(); Pump(); }
    }
}
