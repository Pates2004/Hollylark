using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;
using Microsoft.Win32;

internal static partial class Program
{
    private static void VerifyPreferencesInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        VerifyExpandedPreferences();
        VerifyDesktopIntegration();
        VerifyPresentationPreferences();
        application.Shutdown();
        Console.WriteLine("Validation reports: " + ReportDirectory);
    }

    private static void VerifyPresentationPreferences()
    {
        VerifyPreferenceEffects();
        VerifyPreferenceRollback();
        VerifyAutomaticConnection();
        VerifyTrayAndExit();
        VerifyProfileFailure();
        VerifySettingsTransactions();
        VerifyNativeTrayLifecycle();
    }

    private static MainWindow CreatePresentationWindow(FakeTransport transport, string name,
        MemoryDesktop desktop, Func<string, string, bool>? confirm = null, Action<string>? announce = null,
        Action<string>? desktopError = null)
    {
        var receiver = new ReceiverInfo("Simulated LARK A1", "simulated://presentation", 0, 64);
        var window = new MainWindow(() => [receiver], _ => transport, Path.Combine(ReportDirectory, name),
            desktop, confirm ?? ((_, title) => title != LarkControl.Windows.Localization.Text("Settings.Save.Title")),
            announce, desktopError, automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        return window;
    }

    private static void SavePreferences(MainWindow window, UiPreferences candidate)
    {
        string? error = window.SaveApplicationPreferences(candidate);
        Check(error is null, "Application preferences commit: " + error);
        Pump();
    }

    private static void VerifyPreferenceEffects()
    {
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        var announcements = new List<string>();
        MainWindow window = CreatePresentationWindow(transport, "presentation", desktop, announce: announcements.Add);
        try
        {
            RunTask(window.ConnectAsync());
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            window.OpenHelpWindow();
            AuxiliaryWindow help = window.OwnedWindows.OfType<AuxiliaryWindow>().Single();
            MoveOffscreen(help);
            SavePreferences(window, window.CurrentPreferences with { Theme = "Dark", TextScale = 150 });
            Check(window.ThemeMode == ThemeMode.Dark && help.ThemeMode == ThemeMode.Dark, "Theme affects main and existing auxiliary window");
            Check(window.FontSize == SystemFonts.MessageFontSize * 1.5 && help.FontSize == window.FontSize, "Scale affects main and auxiliary window");
            help.Close();
            SavePreferences(window, window.CurrentPreferences with
            {
                HiddenRows = ["Microphones", "Noise"], ValueDisplayMode = ValueDisplayMode.StaticText,
                GainControlMode = GainControlMode.Buttons
            });
            Check(((FrameworkElement)window.FindName("MicrophonesPanel")).Visibility == Visibility.Collapsed, "Microphone section can be hidden");
            Check(window.AudioSettings.Single(setting => setting.Id == "Noise").RowVisibility == Visibility.Collapsed, "Individual LARK row can be hidden");
            Check(gain.CurrentFieldVisibility == Visibility.Collapsed && gain.StaticValueVisibility == Visibility.Visible, "Static values replace read-only fields");
            Button increase = FindById<Button>(window, "Gain.Increase");
            Check(increase.IsVisible && !FindById<ComboBox>(window, "Gain.Choice").IsVisible, "Gain buttons replace the choice control");
            int writes = CountMockWrites(transport);
            increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(gain.SelectedIndex == 4 && gain.ConfirmedValue == "4" && gain.IsDirty, "Gain button changes only the draft");
            Check(CountMockWrites(transport) == writes, "Manual gain button does not write USB");
            gain.SelectedIndex = 5;
            Check(!gain.CanIncrease && gain.CanDecrease, "Gain upper step bound");
            gain.SelectedIndex = 0;
            Check(!gain.CanDecrease && gain.CanIncrease, "Gain lower step bound");
            SavePreferences(window, window.CurrentPreferences with
            {
                HiddenRows = [], GainControlMode = GainControlMode.Slider, ValueDisplayMode = ValueDisplayMode.None
            });
            Slider slider = FindById<Slider>(window, "Gain.Slider");
            Check(slider.IsVisible && slider.Minimum == 1 && slider.Maximum == 6, "Gain slider exposes actual six-level range");
            Check(gain.CurrentColumnWidth.Value == 0 && gain.StaticValueVisibility == Visibility.Collapsed, "Hidden current values remove their column");
            slider.Value = 3;
            Check(gain.SelectedIndex == 2 && CountMockWrites(transport) == writes, "Slider draft is not an implicit manual write");
            RunTask(window.ApplySettingAsync(gain));
            Check(gain.ConfirmedValue == "3" && !gain.IsDirty, "Slider manual apply requires confirmed readback");
            gain.SelectedIndex = 0;
            SavePreferences(window, window.CurrentPreferences with { AutoApplySelections = true });
            writes = CountMockWrites(transport);
            RunTask(window.FlushAutomaticChangesAsync());
            Check(CountMockWrites(transport) == writes && gain.IsDirty, "Enabling automatic mode never applies an existing draft");
            gain.SelectedIndex = 1;
            gain.SelectedIndex = 4;
            RunTask(window.FlushAutomaticChangesAsync());
            Check(CountMockWrites(transport) == writes + 1 && gain.ConfirmedValue == "5" && !gain.IsDirty, "Automatic mode coalesces latest draft and confirms it");
            Check(transport.MaximumConcurrentWrites == 1, "Automatic writes remain serialized");
            writes = CountMockWrites(transport);
            gain.SelectedIndex = 3;
            SettingRowViewModel noise = window.AudioSettings.Single(setting => setting.Id == "Noise");
            noise.SelectedIndex = 1;
            RunTask(window.FlushAutomaticChangesAsync());
            RunTask(window.FlushAutomaticChangesAsync());
            Check(CountMockWrites(transport) == writes + 2 && gain.SelectedIndex == 3 && noise.SelectedIndex == 1 && !noise.IsDirty,
                "Automatic mode preserves and confirms pending drafts in different rows");
            writes = CountMockWrites(transport);
            gain.SelectedIndex = 2;
            RunTask(window.ApplySettingAsync(gain));
            RunTask(window.FlushAutomaticChangesAsync());
            Check(CountMockWrites(transport) == writes + 1, "Manual apply consumes the pending automatic write without duplication");
            writes = CountMockWrites(transport);
            gain.SelectedIndex = 1;
            SavePreferences(window, window.CurrentPreferences with { Theme = "Light" });
            RunTask(window.FlushAutomaticChangesAsync());
            Check(CountMockWrites(transport) == writes + 1 && gain.SelectedIndex == 1 && !gain.IsDirty,
                "Unrelated appearance commit preserves queued automatic change");
            SavePreferences(window, window.CurrentPreferences with
            {
                AutoApplySelections = false, ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false
            });
            Check(!UsageHints.GetEnabled(window), "Hints disabled at window level");
            Check(FindById<TextBlock>(window, "ResultReadout").Visibility == Visibility.Collapsed, "Routine operation messages can be hidden");
            int spoken = announcements.Count;
            transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            Check(announcements.Count == spoken, "Disabled state announcements stay silent");
            SavePreferences(window, window.CurrentPreferences with { AnnounceStateChanges = true });
            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            Check(announcements.Count == spoken + 1 && announcements.Last().Contains("Mikrofon 2"), "Enabled announcement reports actual transmitter reconnection");
            RunTask(Poll(window));
            Check(announcements.Count == spoken + 1, "Unchanged periodic polling produces no announcement");
            var profile = new AudioProfile("Test profilu", 0, 1, false, 1, 1, false, 0);
            Task<string?> applying = window.ApplyAudioProfileAsync(profile);
            RunTask(applying);
            Check(applying.Result is null && gain.SelectedIndex == 0 && !gain.IsDirty, "Audio profile applies and confirms multiple actual setting changes");
            Check(window.AudioSettings.Single(setting => setting.Id == "ChannelMode").SelectedIndex == 1, "Profile channel mode confirmed");
            VerifyPresentationCaptures(window);
            Check(desktop.StartupWrites == 0 && desktop.TrayShows == 0, "Unrelated appearance preferences never alter startup or tray");
        }
        finally { window.RequestExit(); }
        Check(desktop.Disposed, "Application close disposes desktop integration");
    }

    private static void VerifyPresentationCaptures(MainWindow window)
    {
        foreach (string theme in new[] { "Light", "Dark" })
        {
            SavePreferences(window, window.CurrentPreferences with
            {
                Theme = theme, TextScale = theme == "Dark" ? 200 : 100, ShowHints = true,
                ShowOperationMessages = true, ValueDisplayMode = ValueDisplayMode.ReadOnlyFields
            });
            RenderWindow(window, "Main-" + theme + ".png");
            window.OpenSettingsWindow();
            SettingsWindow settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            MoveOffscreen(settings);
            TabControl tabs = (TabControl)settings.FindName("SettingsTabs");
            Check(tabs.Items.Count == 5, "All five reference-inspired settings categories exist");
            for (int index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                Pump();
                RenderWindow(settings, $"Settings-{theme}-{index}.png");
                Check(FindById<Button>(settings, "SaveSettingsButton").IsVisible, "Settings save remains visible at requested text scale");
                VerifyAccessibleControls(settings);
            }
            settings.Close();
        }
        foreach (Window auxiliary in new[] { window.OpenInformationWindow(), window.OpenHelpWindow() })
        {
            MoveOffscreen(auxiliary);
            auxiliary.Width = auxiliary.MinWidth;
            auxiliary.Height = auxiliary.MinHeight;
            RenderWindow(auxiliary, "Minimum-" + auxiliary.GetType().Name + ".png");
            if (auxiliary is InfoWindow)
            {
                ScrollViewer scroll = FindById<ScrollViewer>(auxiliary, "DiagnosticsScroll");
                Check(scroll.ScrollableHeight > 0 && FindById<TextBox>(auxiliary, "DeviceInformationReadout").ActualHeight >= 90,
                    "Minimum diagnostic window scrolls instead of collapsing readout rows at 200% scale");
                scroll.ScrollToBottom();
                Pump();
                Check(scroll.VerticalOffset > 0, "Diagnostic window exposes lower content by scrolling");
                RenderWindow(auxiliary, "Minimum-InfoWindow-bottom.png");
            }
            VerifyAccessibleControls(auxiliary);
            Check(FindById<Button>(auxiliary, "CloseSectionButton").IsVisible, "Auxiliary close remains available at minimum size and 200% scale");
            auxiliary.Close();
        }
    }

    private static void VerifyPreferenceRollback()
    {
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        MainWindow window = CreatePresentationWindow(transport, "preference-rollback", desktop);
        try
        {
            SavePreferences(window, window.CurrentPreferences with { StartWithWindows = true, CloseToTray = true });
            Check(desktop.Startup.Exists && desktop.TrayVisible, "Startup and tray effects precede successful persistence");
            desktop.RestoreStartup(new(true, "outdated executable path", RegistryValueKind.String));
            SavePreferences(window, window.CurrentPreferences);
            Check(Equals(desktop.Startup.RawValue, "test executable"), "Saving enabled startup repairs an outdated executable registration");
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "preference-rollback"));
            Check(store.Load(out _).StartWithWindows && store.Load(out _).CloseToTray, "Integration choices persist beside application");
            StartupRegistrationSnapshot previous = desktop.Startup;
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string? error = window.SaveApplicationPreferences(window.CurrentPreferences with { StartWithWindows = false, CloseToTray = false });
                Check(error is not null && desktop.Startup.Equals(previous), "Failed config write restores exact prior startup registration");
                Check(desktop.TrayVisible && window.CurrentPreferences.CloseToTray, "Failed config write restores prior tray and committed preferences");
            }
            desktop.FailHideAfterRemoval = true;
            string? trayError = window.SaveApplicationPreferences(window.CurrentPreferences with { CloseToTray = false });
            Check(trayError is not null && desktop.TrayVisible && window.CurrentPreferences.CloseToTray,
                "Partial tray removal failure restores the previous usable icon");
            desktop.FailHideAfterRemoval = false;
        }
        finally { window.RequestExit(); }
        using var rejectedTransport = new FakeTransport { RejectWrite = true };
        MainWindow rejected = CreatePresentationWindow(rejectedTransport, "hidden-error", new MemoryDesktop());
        try
        {
            SavePreferences(rejected, rejected.CurrentPreferences with { ShowOperationMessages = false, ShowHints = false, AnnounceStateChanges = false });
            RunTask(rejected.ConnectAsync());
            SettingRowViewModel gain = rejected.AudioSettings.Single(setting => setting.Id == "Gain");
            gain.SelectedIndex = 0;
            RunTask(rejected.ApplySettingAsync(gain));
            Check(FindById<TextBlock>(rejected, "ResultReadout").Visibility == Visibility.Visible, "Errors stay visible with optional messages and hints disabled");
        }
        finally { rejected.RequestExit(); }
        var cleanupErrors = new List<string>();
        using var cleanupTransport = new FakeTransport();
        MainWindow cleanup = CreatePresentationWindow(cleanupTransport, "cleanup-error", new MemoryDesktop { FailDispose = true },
            desktopError: cleanupErrors.Add);
        cleanup.RequestExit();
        Check(cleanupErrors.Count == 1 && cleanupErrors[0].Contains("błąd"), "Desktop cleanup failure remains user-visible after the main window closes");
    }

    private static void VerifyAutomaticConnection()
    {
        for (int count = 0; count <= 2; count++)
        {
            string directory = Path.Combine(ReportDirectory, "automatic-connection-" + count);
            var store = new UiPreferencesStore(directory);
            Check(store.TrySave(new UiPreferences { AutoConnectAtStartup = false }, out _), "Seed disabled legacy automatic-connection preference");
            using var transport = new FakeTransport();
            ReceiverInfo[] receivers = Enumerable.Range(0, count).Select(index =>
                new ReceiverInfo("Simulated " + index, "simulated://auto/" + index, 0, 64)).ToArray();
            int enumerated = 0;
            int opened = 0;
            var window = new MainWindow(() =>
            {
                Interlocked.Increment(ref enumerated);
                return receivers;
            }, _ => { Interlocked.Increment(ref opened); return transport; }, directory, new MemoryDesktop(), (_, _) => true);
            MoveOffscreen(window);
            try
            {
                window.Show();
                PumpUntil(() => Volatile.Read(ref enumerated) > 0 && !window.IsBusy
                    && window.Receivers.Count == count && (count != 1 || window.IsConnected));
                Check(Volatile.Read(ref opened) == (count == 1 ? 1 : 0), "Default automatic connection requires one receiver and ignores the disabled legacy preference: " + count);
                Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion), "Automatic connection sends only status/version reads");
            }
            finally { window.RequestExit(); }
        }
    }

    private static void VerifyTrayAndExit()
    {
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        bool allowExit = false;
        MainWindow window = CreatePresentationWindow(transport, "tray-exit", desktop, (_, _) => allowExit);
        try
        {
            RunTask(window.ConnectAsync());
            SavePreferences(window, window.CurrentPreferences with { CloseToTray = true, ConfirmExit = true });
            window.Close();
            Pump();
            Check(!window.IsVisible && window.IsConnected && desktop.TrayVisible, "Ordinary close hides to usable tray without disconnecting");
            desktop.Restore!();
            Check(window.IsVisible && window.ShowInTaskbar, "Tray restore recovers the main window and taskbar entry");
            typeof(MainWindow).GetField("stateBeforeTray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, WindowState.Maximized);
            System.Windows.Input.Keyboard.Focus(FindById<TextBlock>(window, "ResultReadout"));
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            desktop.Restore!();
            Check(window.WindowState == WindowState.Normal && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, focused),
                "Showing an already visible window preserves its actual size and focus");
            window.RequestExit();
            Check(window.IsVisible && window.IsConnected, "Cancelled explicit exit keeps session and main window");
            desktop.RejectTray = true;
            window.Close();
            Pump();
            Check(window.IsVisible && window.ResultText.Contains("Nie ukryto"), "Tray failure never strands a hidden window");
            desktop.RejectTray = false;
            allowExit = true;
        }
        finally { allowExit = true; window.RequestExit(); }
        Check(transport.Disposed && desktop.Disposed, "Confirmed explicit exit tears down USB and tray");
    }

    private static void VerifyProfileFailure()
    {
        using var transport = new FakeTransport { RejectAfterWrites = 1 };
        MainWindow window = CreatePresentationWindow(transport, "partial-profile", new MemoryDesktop());
        try
        {
            RunTask(window.ConnectAsync());
            Task<string?> applying = window.ApplyAudioProfileAsync(new AudioProfile("Partial", 0, 1, false, 0, 1, false, 0));
            RunTask(applying);
            Check(applying.Result is not null && applying.Result.Contains("Część"), "Partial profile failure is explicitly reported without pretending rollback");
            Check(CountMockWrites(transport) == 2 && !window.IsConnected, "Profile stops after rejected second change and closes uncertain session");
        }
        finally { window.RequestExit(); }
    }

    private sealed class MemoryDesktop : IDesktopIntegration
    {
        public StartupRegistrationSnapshot Startup { get; private set; } = new(true, "prior test value", RegistryValueKind.ExpandString);
        public bool TrayVisible { get; private set; }
        public bool RejectTray { get; set; }
        public bool FailHideAfterRemoval { get; set; }
        public bool FailDispose { get; set; }
        public bool Disposed { get; private set; }
        public int StartupWrites { get; private set; }
        public int TrayShows { get; private set; }
        public Action? Restore { get; private set; }
        public StartupRegistrationSnapshot CaptureStartup() => Startup;
        public void SetStartup(bool enabled)
        {
            StartupWrites++;
            Startup = enabled ? new(true, "test executable", RegistryValueKind.String) : StartupRegistrationSnapshot.Missing;
        }
        public void RestoreStartup(StartupRegistrationSnapshot snapshot) => Startup = snapshot;
        public void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit)
        {
            if (RejectTray) throw new IOException("Simulated tray failure");
            TrayShows++;
            TrayVisible = true;
            Restore = show;
        }
        public void HideTray()
        {
            TrayVisible = false;
            if (FailHideAfterRemoval) throw new IOException("Simulated failure after tray icon removal");
        }
        public void Dispose()
        {
            TrayVisible = false;
            Disposed = true;
            if (FailDispose) throw new IOException("Simulated desktop cleanup failure");
        }
    }
}
