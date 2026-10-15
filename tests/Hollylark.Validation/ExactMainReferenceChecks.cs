using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyExactMainReference()
    {
        VerifyExactMainLayout();
        VerifyReferenceReceiverSelection();
        VerifyReferenceCancellation();
        VerifyNonblockingStartupDiscovery();
        VerifyMainOperationFocus();
    }

    private static void VerifyExactMainLayout()
    {
        using var transport = new FakeTransport();
        MainWindow window = CreatePresentationWindow(transport, "exact-main-layout", new MemoryDesktop());
        try
        {
            Check(window.Width == 1080 && window.Height == 900 && window.MinWidth == 820 && window.MinHeight == 640,
                "Main shell retains the reference dimensions");
            var scroll = (ScrollViewer)window.FindName("ControlsScrollViewer");
            Check(scroll.Padding == new Thickness(12) && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto,
                "Main reference columns have padding and accessible horizontal scrolling");
            TextBlock result = FindById<TextBlock>(window, "ResultReadout");
            Check(result.MinHeight == 36 && double.IsPositiveInfinity(result.MaxHeight)
                && result.Focusable && KeyboardNavigation.GetIsTabStop(result)
                && GetAccessiblePeer(result).GetPattern(System.Windows.Automation.Peers.PatternInterface.Value) is null,
                "Main results use focusable static text without clipping long errors or exposing an edit pattern");
            Button refresh = FindById<Button>(window, "RefreshButton");
            Button cancel = FindById<Button>(window, "CancelOperationButton");
            Check(refresh.Parent is StackPanel buttons && ReferenceEquals(buttons, cancel.Parent)
                && buttons.Orientation == Orientation.Vertical, "Refresh and Cancel use the reference stacked footer");
            Check(!cancel.IsEnabled, "Cancel is unavailable while idle");
            TextBlock connection = FindById<TextBlock>(window, "ConnectionReadout");
            TextBlock status = FindById<TextBlock>(window, "ReadoutStatus");
            Check(connection.Focusable && status.Focusable && GetAccessiblePeer(connection).GetName() == connection.Text
                && GetAccessiblePeer(status).GetName() == status.Text, "Main static readouts expose actual state and keyboard focus");
            foreach (int scale in new[] { 100, 200 })
            {
                SavePreferences(window, window.CurrentPreferences with { TextScale = scale });
                foreach (SettingRowViewModel setting in AllSettings(window))
                    Check(setting.LabelColumnWidth.Value == 150 * scale / 100.0
                        && setting.CurrentColumnWidth.Value == 170 * scale / 100.0
                        && setting.EditorMinimumWidth == 370 * scale / 100.0,
                        "Reference 150/170/370 columns follow text scale: " + setting.Id);
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Pump();
                RenderWindow(window, $"exact-main-{scale}.png");
            }
            SavePreferences(window, window.CurrentPreferences with { ValueDisplayMode = ValueDisplayMode.None });
            Check(AllSettings(window).All(setting => setting.CurrentColumnWidth.Value == 0
                && setting.EditorLabelVisibility == Visibility.Visible), "Hidden current values retain visible editor labels");
            var copied = new List<Uri>();
            window.CopyManualAddress(copied.Add);
            Check(copied.SequenceEqual(new[] { OfficialSupportLinks.Manual }), "Manual menu copies only the official address through its supplied handler");
            Func<Window>[] openSections = [window.OpenSettingsWindow, window.OpenInformationWindow, window.OpenHelpWindow];
            foreach (Func<Window> open in openSections)
            {
                Window section = open();
                MoveOffscreen(section);
                section.WindowState = WindowState.Minimized;
                Pump();
                Check(ReferenceEquals(section, open()) && section.WindowState == WindowState.Normal,
                    "Reopening a minimized section restores its existing instance: " + section.Name);
                section.Close();
                Pump();
            }
            Check(transport.Commands.Count == 0, "Reference shell checks never open USB or issue hardware commands");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyReferenceReceiverSelection()
    {
        using var transport = new FakeTransport();
        MainWindow window = CreatePresentationWindow(transport, "exact-receiver-selection", new MemoryDesktop());
        try
        {
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
            ListBox discovered = FindById<ListBox>(window, "ReceiverList");
            var receiverTab = (TabItem)window.FindName("ReceiverTab");
            Check(receiverTab.IsSelected && TabContentElements(receiverTab).OfType<ListBox>().SequenceEqual(new[] { discovered })
                && discovered.IsEnabled && !discovered.IsTabStop
                && KeyboardNavigation.GetTabNavigation(discovered) == KeyboardNavigationMode.Once,
                "Receiver selection exposes one enabled native list using item focus and Once navigation");
            foreach (string identifier in new[] { "SavedReceiverList", "RememberedReceiverReadout", "ConnectButton",
                "ConnectAndRememberButton", "RememberReceiverButton", "ForgetReceiverButton", "DisconnectButton",
                "ConnectMenuItem", "ConnectOnceMenuItem", "ConnectAndRememberMenuItem", "RememberReceiverMenuItem", "ForgetReceiverMenuItem", "DisconnectMenuItem" })
                Check(window.FindName(identifier) is null && !Descendants(window).OfType<FrameworkElement>()
                    .Any(element => AutomationProperties.GetAutomationId(element) == identifier),
                    "The automatic receiver UI has no obsolete manual control: " + identifier);
            foreach (string member in new[] { "SavedReceivers", "ConnectCommand", "ConnectAndRememberCommand",
                "RememberReceiverCommand", "ForgetReceiverCommand", "DisconnectCommand" })
                Check(typeof(MainWindow).GetMember(member, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static).Length == 0,
                    "The automatic receiver model has no obsolete manual UI member: " + member);
            Check(!window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.D
                && binding.Modifiers == ModifierKeys.Control), "Ctrl+D no longer disconnects the receiver");
            Check(window.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == MainWindow.ScanCommand)
                && window.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == MainWindow.RefreshCommand),
                "Physical discovery and read/retry remain routed commands");
            TextBlock active = FindById<TextBlock>(window, "ActiveReceiverReadout");
            Check(active.Focusable && KeyboardNavigation.GetIsTabStop(active) && !string.IsNullOrWhiteSpace(active.Text),
                "The active receiver exposes an essential focusable status readout");
            Check(TabContentElements(receiverTab).OfType<Label>().Any(label => ReferenceEquals(label.Target, active)),
                "The active receiver readout has a visible associated label target");
            var source = discovered.ItemsSource;
            object selected = discovered.SelectedItem;
            Window settings = window.OpenSettingsWindow();
            var settingsTrace = new List<string>();
            string SettingsState() => $"visible={settings.IsVisible}; active={settings.IsActive}; connected={window.IsConnected}; commands={transport.Commands.Count}; busy={window.IsBusy}; tracked="
                + ReferenceEquals(settings, typeof(MainWindow).GetField("settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window));
            System.ComponentModel.CancelEventHandler closingTrace = (_, _) => settingsTrace.Add("closing: " + SettingsState() + Environment.NewLine + Environment.StackTrace);
            settings.Closing += closingTrace;
            try
            {
                settingsTrace.Add("opened: " + SettingsState());
                Pump();
                settingsTrace.Add("after-pump: " + SettingsState());
                Check(settings.IsVisible, "Settings guard trial requires its transaction window to remain open: " + SettingsState());
                RunTask(window.ConnectAsync());
                settingsTrace.Add("after-connect-attempt: " + SettingsState());
                Check(window.IsConnected && settings.IsVisible
                    && transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                    "Settings transaction remains open while receiver availability changes through reads only: " + SettingsState());
            }
            finally
            {
                settings.Closing -= closingTrace;
                Directory.CreateDirectory(ReportDirectory);
                File.WriteAllLines(Path.Combine(ReportDirectory, "settings-connection-guard-trace.txt"), settingsTrace);
            }
            settings.Close();
            Pump();
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && discovered.IsEnabled && window.CurrentPreferences.PreferredReceiverPath is null,
                "Backend connection keeps the physical list available without saving receiver preferences");
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "Connecting the receiver issues reads only, not settings writes");
            discovered.UpdateLayout();
            object[] receiverItems = discovered.Items.Cast<object>().ToArray();
            var receiverContainers = receiverItems.Select(item => discovered.ItemContainerGenerator.ContainerFromItem(item)).ToArray();
            RunTask(Poll(window));
            Check(ReferenceEquals(discovered, FindById<ListBox>(window, "ReceiverList"))
                && ReferenceEquals(discovered.ItemsSource, source) && ReferenceEquals(discovered.SelectedItem, selected),
                "Status polling preserves the native physical list, collection and selection");
            Check(receiverContainers.All(container => container is not null)
                && receiverItems.Select(item => discovered.ItemContainerGenerator.ContainerFromItem(item)).SequenceEqual(receiverContainers),
                "Status polling preserves existing visible native receiver item containers");
            window.Disconnect();
            Pump();
            Check(!window.IsConnected && discovered.IsEnabled && ReferenceEquals(discovered.ItemsSource, source),
                "Backend disconnection preserves the available physical receiver list");
            SavePreferences(window, window.CurrentPreferences with { ShowHints = false });
            Check(active.IsVisible && active.Focusable && !string.IsNullOrWhiteSpace(GetAccessiblePeer(active).GetName()),
                "Disabling optional hints preserves the essential active receiver status");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyReferenceCancellation()
    {
        using var transport = new FakeTransport();
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var receiver = new ReceiverInfo("Test receiver", "simulated://cancel-reference", 0, 64);
        var window = new MainWindow(() => [receiver], _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return transport;
        }, Path.Combine(ReportDirectory, "exact-cancel-connect"), new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        try
        {
            Task connect = window.ConnectAsync();
            PumpUntil(() => entered.IsSet);
            Check(MainWindow.CancelOperationCommand.CanExecute(null, window), "Cancel is enabled during delayed connection");
            window.CancelCurrentOperation();
            gate.Set();
            RunTask(connect);
            Check(!window.IsBusy && !window.IsConnected && transport.Disposed && transport.Commands.Count == 0,
                "A cancelled connection disposes its late transport without reading it");
        }
        finally { gate.Set(); window.Close(); Pump(); }
    }

    private static void VerifyNonblockingStartupDiscovery()
    {
        string directory = Path.Combine(ReportDirectory, "exact-nonblocking-startup");
        var store = new UiPreferencesStore(directory);
        Check(store.TrySave(new UiPreferences { AutoConnectAtStartup = true, CloseToTray = true, FirstRunCompleted = true }, out string? error),
            "Save isolated startup discovery fixture with legacy preferences: " + error);
        using var gate = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var desktop = new MemoryDesktop();
        int opened = 0;
        var window = new MainWindow(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            finished.Set();
            return new[] { new ReceiverInfo("Late receiver", "simulated://late", 0, 64) };
        }, _ => { opened++; return new FakeTransport(); }, directory, desktop, (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        try
        {
            Check(!window.IsBusy && !MainWindow.CancelOperationCommand.CanExecute(null, window),
                "Physical discovery does not create a global busy or cancellable hardware operation");
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
            Check(FindById<ListBox>(window, "ReceiverList").IsEnabled,
                "A pending startup enumeration leaves the native physical list enabled");
            gate.Set();
            PumpUntil(() => finished.IsSet);
            PumpUntil(() => desktop.TrayVisible && window.Receivers.Count == 1);
            Check(desktop.TrayShows == 1, "Nonblocking discovery preserves requested startup tray initialization");
            Check(!window.IsBusy && opened == 0,
                "Isolated discovery populates the physical list without honoring legacy auto-open preferences");
        }
        finally { gate.Set(); window.RequestExit(); Pump(); }
    }

    private static void VerifyMainOperationFocus(string runSuffix = "")
    {
        using var transport = new FakeTransport { DelayMilliseconds = 10 };
        MainWindow window = CreatePresentationWindow(transport, "exact-main-operation-focus" + runSuffix, new MemoryDesktop());
        var otherReadout = new TextBox { Text = "Independent focus target", IsReadOnly = true };
        var other = new Window { Content = otherReadout, Width = 320, Height = 200 };
        try
        {
            window.Activate();
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
            Button search = FindById<Button>(window, "SearchButton");
            FocusForKeyboardCheck(search);
            RunTask(window.ScanAsync());
            CheckKeyboardFocus(search, "Discovery restores its enabled initiating action");
            ListBox receivers = FindById<ListBox>(window, "ReceiverList");
            receivers.UpdateLayout();
            var receiverItem = (ListBoxItem)receivers.ItemContainerGenerator.ContainerFromItem(receivers.SelectedItem);
            FocusForKeyboardCheck(receiverItem);
            TextBlock result = FindById<TextBlock>(window, "ResultReadout");
            RunTask(window.ConnectAsync());
            Check(receivers.IsEnabled, "Connection keeps the physical receiver selector enabled");
            CheckKeyboardFocus(receiverItem, "Backend connection preserves focus on the enabled native receiver item");
            Button refresh = FindById<Button>(window, "RefreshButton");
            FocusForKeyboardCheck(refresh);
            RunTask(window.RefreshAsync());
            CheckKeyboardFocus(refresh, "Explicit refresh restores the initiating control");
            transport.DelayMilliseconds = 150;
            Task refreshTask = window.RefreshAsync();
            FocusForKeyboardCheck(result);
            RunTask(refreshTask);
            CheckKeyboardFocus(result, "Finishing refresh does not override a newly focused readout");
            FocusForKeyboardCheck(refresh);
            refreshTask = window.RefreshAsync();
            MoveOffscreen(other);
            other.Show();
            other.Activate();
            FocusForKeyboardCheck(otherReadout);
            RunTask(refreshTask);
            Check(other.IsActive && !window.IsActive, "Receiver operation completion never activates the main window");
            CheckKeyboardFocus(otherReadout, "Receiver operation completion preserves another window's focus");
            other.Close();
            window.Activate();
            Pump();
            SavePreferences(window, window.CurrentPreferences with { ShowOperationMessages = false });
            using var receiverProbe = new FocusTransitionProbe(window, "receiver-disconnect" + runSuffix);
            FocusForKeyboardCheck(receiverItem);
            receiverProbe.Record("before-disconnect");
            window.Disconnect();
            receiverProbe.Record("after-disconnect-before-pump");
            Pump();
            receiverProbe.Record("after-disconnect-pump");
            CheckKeyboardFocus(receiverItem, "Backend disconnection preserves the still-available receiver item focus with optional results hidden");
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "Operation focus validation never changes receiver settings");
        }
        finally { other.Close(); window.Close(); Pump(); }
    }
}
