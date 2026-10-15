using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyAutomaticUsbChecks()
    {
        Check(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(),
            "Automatic USB integration reuses the existing WPF application and dispatcher");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyUsbSoleReceiverAndLegacyPreferences();
                VerifyUsbMultipleReceiversAndStableList();
            }
            Localization.SetLanguage("en");
            VerifyUsbMonitoringTimer();
            VerifyUsbUnplugDuringFactory();
            VerifyUsbRapidSelection();
            VerifyUsbUnplugDuringInitialRead();
            VerifyUsbUnplugDuringTraffic(A1Command.GetStatus);
            VerifyUsbUnplugDuringTraffic(A1Command.SetGain);
            VerifyUsbScanFailureIsolation();
            VerifyUsbBoundedRetries();
            VerifyUsbDraftIsolation(false);
            VerifyUsbDraftIsolation(true);
            VerifyUsbCloseDuringScan();
            VerifyUsbRemovedItemFocus();
            VerifyUsbOtherWindowFocus(true);
            VerifyUsbOtherWindowFocus(false);
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyUsbSoleReceiverAndLegacyPreferences()
    {
        ReceiverInfo receiver = UsbReceiver("sole");
        foreach (bool legacyAutomatic in new[] { false, true })
        {
            var preferences = new UiPreferences
            {
                Language = Localization.CurrentLanguage, FirstRunCompleted = true,
                AutoConnectAtStartup = legacyAutomatic, PreferredReceiverPath = UsbReceiver("absent").Path,
                AudioProfiles = [new("Never automatically applied", 0, 1, false, 0, 1, false, 0)]
            };
            using var fixture = new UsbIntegrationFixture("sole-legacy-" + legacyAutomatic, [receiver], preferences);
            byte[] original = File.ReadAllBytes(fixture.Store.FilePath);
            fixture.Start();
            Check(fixture.Window.IsConnected && !fixture.Window.IsBusy && fixture.Window.ActiveReceiverPath == receiver.Path
                && fixture.List.SelectedItem is ReceiverInfo selected && selected.Path == receiver.Path,
                "A sole receiver is automatically selected and read regardless of legacy startup preferences: " + legacyAutomatic);
            Check(fixture.Factory.Requests.SequenceEqual(new[] { receiver.Path }), "Automatic startup opens the sole physical receiver exactly once");
            A1Frame[] frames = fixture.Factory.Transports.Single().Frames;
            Check(frames.Any(frame => frame.Command == A1Command.GetStatus)
                && frames.Where(frame => frame.Command == A1Command.GetVersion).Select(frame => frame.Payload[0]).Order()
                    .SequenceEqual(new byte[] { 0, 1, 2 }),
                "Automatic startup confirms the status and RX/TX firmware before enabling controls");
            Check(AllSettings(fixture.Window).All(setting => setting.CanApply && !setting.IsDirty)
                && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 3,
                "Startup displays actual readback and does not apply an existing audio profile");
            Check(original.SequenceEqual(File.ReadAllBytes(fixture.Store.FilePath))
                && fixture.Window.CurrentPreferences.AutoConnectAtStartup == legacyAutomatic
                && fixture.Window.CurrentPreferences.PreferredReceiverPath == preferences.PreferredReceiverPath,
                "Automatic USB leaves legacy configuration bytes and in-memory round-trip fields intact");
            VerifyUsbReadOnly(fixture, "sole startup with legacy preferences");
            VerifyUsbAccessibleSurface(fixture);
        }
    }

    private static void VerifyUsbAccessibleSurface(UsbIntegrationFixture fixture)
    {
        fixture.ShowReceiverTab();
        MainWindow window = fixture.Window;
        ListBox list = fixture.List;
        var tab = (TabItem)window.FindName("ReceiverTab");
        Check(tab.IsSelected && list.GetType() == typeof(ListBox) && TabContentElements(tab).OfType<ListBox>().SequenceEqual(new[] { list })
            && ReferenceEquals(list.Style, window.FindResource("DeviceListStyle"))
            && list.DisplayMemberPath == nameof(ReceiverInfo.Name),
            "Automatic USB retains exactly one native reference-style receiver list");
        Check(list.IsEnabled && !list.IsTabStop && KeyboardNavigation.GetTabNavigation(list) == KeyboardNavigationMode.Once,
            "The populated native list uses its item focus stop and Once navigation, including while connected");
        VerifyNamedPeer(list, Localization.Text("Main.Receiver.DiscoveredName"), AutomationControlType.List);
        VerifyNamedPeer(FindById<Button>(window, "SearchButton"), Localization.Text("Main.Receiver.SearchName"), AutomationControlType.Button);
        var active = (TextBlock)window.FindName("ActiveReceiverReadout");
        var label = (Label)window.FindName("ActiveReceiverLabel");
        Check(active.Focusable && KeyboardNavigation.GetIsTabStop(active) && ReferenceEquals(label.Target, active)
            && ReferenceEquals(AutomationProperties.GetLabeledBy(active), label)
            && label.Content?.ToString() == Localization.Text("Main.Usb.ActiveLabel"),
            "The active receiver readout has the localized native label and keyboard focus stop");
        Check(active.Text == ((ReceiverInfo)list.SelectedItem).Name && GetAccessiblePeer(active).GetName() == active.Text,
            "The active receiver's actual identity, not a fixed caption, is exposed by its UIA text peer");
        TextBlock connection = FindById<TextBlock>(window, "ConnectionReadout");
        Check(connection.Text == Localization.Text("Main.Usb.Ready", active.Text, 2, 2)
            && GetAccessiblePeer(connection).GetName() == connection.Text,
            "The selected PL/EN catalog describes the confirmed receiver and microphone counts");
        foreach (object entry in list.Items)
        {
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(entry);
            Check(item.IsTabStop && GetAccessiblePeer(item).GetName() == ((ReceiverInfo)entry).Name,
                "Each native physical receiver item exposes its readable identity");
        }
        string[] obsolete = ["SavedReceiverList", "RememberedReceiverReadout", "ConnectButton", "ConnectAndRememberButton",
            "RememberReceiverButton", "ForgetReceiverButton", "DisconnectButton", "ConnectOnceMenuItem", "DisconnectMenuItem"];
        foreach (string identifier in obsolete)
            Check(window.FindName(identifier) is null
                && !Descendants(window).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == identifier)
                && !UsbMenuItems((Menu)window.FindName("MainMenu")).Any(item => AutomationProperties.GetAutomationId(item) == identifier),
                "Automatic USB exposes no obsolete manual receiver control: " + identifier);
        foreach (string member in new[] { "SavedReceivers", "ConnectCommand", "ConnectAndRememberCommand", "DisconnectCommand",
            "RememberReceiverCommand", "ForgetReceiverCommand" })
            Check(typeof(MainWindow).GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0,
                "Automatic USB has no obsolete connection UI member: " + member);
        Check(!window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.D && binding.Modifiers == ModifierKeys.Control)
            && window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.F5 && binding.Command == MainWindow.RefreshCommand)
            && ReferenceEquals(FindById<Button>(window, "SearchButton").Command, MainWindow.ScanCommand),
            "Only physical discovery and F5 read/retry remain; Ctrl+D is absent");
        SavePreferences(window, window.CurrentPreferences with { ShowHints = false });
        Check(((TextBlock)window.FindName("ReceiverSelectionHint")).Visibility == Visibility.Collapsed
            && active.IsVisible && active.Focusable && GetAccessiblePeer(active).GetName() == active.Text,
            "Disabling optional guidance leaves the active receiver identity fully available");
    }

    private static IEnumerable<MenuItem> UsbMenuItems(ItemsControl parent)
    {
        foreach (MenuItem item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (MenuItem descendant in UsbMenuItems(item)) yield return descendant;
        }
    }

    private static void VerifyUsbMultipleReceiversAndStableList()
    {
        ReceiverInfo first = UsbReceiver("first", "Simulated LARK A1");
        ReceiverInfo second = UsbReceiver("second", "Simulated LARK A1");
        ReceiverInfo third = UsbReceiver("third");
        var preferences = new UiPreferences
        {
            Language = Localization.CurrentLanguage, FirstRunCompleted = true,
            AutoConnectAtStartup = true, PreferredReceiverPath = second.Path
        };
        using var fixture = new UsbIntegrationFixture("multiple-stable", [first, second], preferences);
        byte[] original = File.ReadAllBytes(fixture.Store.FilePath);
        fixture.Start();
        Check(!fixture.Window.IsConnected && fixture.List.SelectedItem is null && fixture.Factory.Calls == 0,
            "Multiple startup receivers require a real choice, even when legacy preferences identify a present receiver");
        Check(FindById<TextBlock>(fixture.Window, "ConnectionReadout").Text == Localization.Text("Main.Usb.Choose", 2),
            "Multiple startup devices expose the exact localized choose-a-receiver state");
        fixture.ShowReceiverTab();
        fixture.Select(second.Path);
        Check(fixture.Window.ActiveReceiverPath == second.Path && fixture.Factory.Requests.SequenceEqual(new[] { second.Path }),
            "A native list selection automatically reads exactly the chosen receiver");
        Check(fixture.Window.Receivers.Select(receiver => receiver.Name).Distinct().Count() == 2
            && fixture.Window.Receivers[1].Name == Localization.Text("Main.Usb.DistinctName", first.Name, 2),
            "Equal product names receive distinct localized native-list captions");
        ListBox list = fixture.List;
        object? source = list.ItemsSource;
        ReceiverInfo[] entries = fixture.Window.Receivers.ToArray();
        list.UpdateLayout();
        var containers = entries.Select(entry => list.ItemContainerGenerator.ContainerFromItem(entry)).ToArray();
        object selection = list.SelectedItem ?? throw new InvalidOperationException("The connected receiver has no physical list selection.");
        var changes = new List<NotifyCollectionChangedAction>();
        int selectionEvents = 0;
        NotifyCollectionChangedEventHandler onCollection = (_, arguments) => changes.Add(arguments.Action);
        SelectionChangedEventHandler onSelection = (_, _) => selectionEvents++;
        fixture.Window.Receivers.CollectionChanged += onCollection;
        list.SelectionChanged += onSelection;
        try
        {
            fixture.Discovery.Set(first with { }, second with { }, third);
            fixture.Rescan();
            fixture.Settle();
            Check(changes.SequenceEqual(new[] { NotifyCollectionChangedAction.Add }) && selectionEvents == 0,
                "Adding an unrelated USB receiver appends once without resetting the list or selected item");
            fixture.Discovery.Set(third with { }, second with { }, first with { });
            fixture.Rescan();
            RunTask(fixture.Window.ScanAsync());
            RunTask(Poll(fixture.Window));
            list.UpdateLayout();
            Check(fixture.Window.ActiveReceiverPath == second.Path && fixture.Factory.Calls == 1
                && !fixture.Factory.Transports.Single().IsDisposed && ReferenceEquals(selection, list.SelectedItem),
                "Reordered discovery, explicit Search and status polling do not reopen or replace the active receiver");
            Check(ReferenceEquals(list, fixture.List) && ReferenceEquals(source, list.ItemsSource)
                && entries.Select((entry, index) => ReferenceEquals(entry, fixture.Window.Receivers[index])).All(same => same)
                && containers.All(container => container is not null)
                && entries.Select(entry => list.ItemContainerGenerator.ContainerFromItem(entry)).SequenceEqual(containers),
                "Physical refresh preserves the native list, backing collection, existing entries and visible item containers");
            Check(changes.Count == 1 && selectionEvents == 0,
                "Snapshot reordering does not emit Reset, Move or redundant SelectionChanged events");
            fixture.Discovery.Set(second, first);
            fixture.Rescan();
            Check(changes.SequenceEqual(new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove })
                && fixture.Window.ActiveReceiverPath == second.Path && fixture.Factory.Calls == 1,
                "Removing an unselected device changes only that list entry");
        }
        finally
        {
            fixture.Window.Receivers.CollectionChanged -= onCollection;
            list.SelectionChanged -= onSelection;
        }
        UsbControlledTransport previous = fixture.Factory.Transports.Single();
        fixture.Select(first.Path);
        Check(fixture.Window.ActiveReceiverPath == first.Path && previous.IsDisposed
            && fixture.Factory.Requests.SequenceEqual(new[] { second.Path, first.Path }) && list.IsEnabled,
            "Explicitly selecting another physical receiver replaces only the old transport and keeps the list enabled");
        Check(original.SequenceEqual(File.ReadAllBytes(fixture.Store.FilePath)), "Receiver selection never writes a remembered path to preferences");
        VerifyUsbReadOnly(fixture, "multiple receiver selection and list reconciliation");
        VerifyUsbAccessibleSurface(fixture);
    }

    private static void VerifyUsbMonitoringTimer()
    {
        using var fixture = new UsbIntegrationFixture("timer-hotplug", []);
        fixture.Start(monitor: true);
        Check(!fixture.Window.IsConnected && fixture.Factory.Calls == 0
            && FindById<TextBlock>(fixture.Window, "ConnectionReadout").Text == Localization.Text("Main.Usb.None"),
            "Default monitoring starts safely with no attached receiver");
        ReceiverInfo receiver = UsbReceiver("timer");
        fixture.Discovery.Set(receiver);
        PumpUntil(() => fixture.Window.ActiveReceiverPath == receiver.Path && !fixture.Window.IsBusy);
        Check(fixture.Factory.Calls == 1, "The real dispatcher monitoring timer discovers and reads a fake hotplug without Search");
        fixture.Discovery.Set();
        PumpUntil(() => fixture.Window.Receivers.Count == 0 && !fixture.Window.IsConnected);
        Check(fixture.Factory.Transports.Single().IsDisposed && AllSettings(fixture.Window).All(setting => !setting.CanApply),
            "The monitoring timer detects unplugging and clears stale availability");
        fixture.Window.RequestExit();
        RunTask(UsbField<Task>(fixture.Window, "receiverScanTask"));
        int calls = fixture.Discovery.Calls;
        UsbPumpFor(TimeSpan.FromMilliseconds(120));
        Check(!fixture.ReceiverTimer.IsEnabled && !fixture.PollTimer.IsEnabled && fixture.Discovery.Calls == calls,
            "Closing a monitored window stops both timers and further physical discovery");
        VerifyUsbReadOnly(fixture, "timer-driven fake hotplug");
    }

    private static void VerifyUsbUnplugDuringFactory()
    {
        ReceiverInfo first = UsbReceiver("slow-open");
        ReceiverInfo replacement = UsbReceiver("after-open");
        using var fixture = new UsbIntegrationFixture("unplug-open", [first]);
        UsbPause pause = fixture.Pause();
        var lateTransport = new UsbControlledTransport();
        bool replacementSawDisposedTransport = false;
        fixture.Factory.Create = (_, call) =>
        {
            if (call == 1) { pause.Hold(); return lateTransport; }
            replacementSawDisposedTransport = lateTransport.IsDisposed;
            return new UsbControlledTransport();
        };
        fixture.Start(awaitSession: false);
        Task original = fixture.Track(fixture.Window.ReceiverSessionTask);
        PumpUntil(() => pause.Entered);
        Check(fixture.Window.IsBusy && !fixture.Window.IsConnected && fixture.List.IsEnabled,
            "A delayed automatic open exposes Reading without disabling physical receiver selection");
        fixture.Discovery.Set();
        fixture.Rescan();
        Check(!fixture.Window.IsConnected && !fixture.Window.IsBusy && fixture.Window.ActiveReceiverPath is null
            && fixture.Window.Receivers.Count == 0 && AllSettings(fixture.Window).All(setting => !setting.CanApply),
            "Unplug during a blocked transport factory immediately clears the pending session");
        fixture.Discovery.Set(replacement);
        fixture.Rescan();
        Task current = fixture.Track(fixture.Window.ReceiverSessionTask);
        UsbPumpFor(TimeSpan.FromMilliseconds(60));
        Check(fixture.Factory.Calls == 1 && fixture.Factory.MaximumConcurrent == 1 && !current.IsCompleted,
            "A replacement open waits for the cancelled factory instead of opening transports concurrently");
        pause.Release();
        RunTask(original);
        RunTask(current);
        fixture.Settle();
        Check(lateTransport.DisposalCount == 1 && lateTransport.Frames.Length == 0 && replacementSawDisposedTransport,
            "A late factory result is disposed unread before the replacement transport opens");
        Check(fixture.Window.ActiveReceiverPath == replacement.Path && fixture.Factory.MaximumConcurrent == 1
            && fixture.Factory.Requests.SequenceEqual(new[] { first.Path, replacement.Path }),
            "Automatic recovery after unplug selects only the newly present receiver");
        VerifyUsbReadOnly(fixture, "unplug during transport open");
    }

    private static void VerifyUsbRapidSelection()
    {
        ReceiverInfo first = UsbReceiver("rapid-a");
        ReceiverInfo second = UsbReceiver("rapid-b");
        using var fixture = new UsbIntegrationFixture("rapid-a-b-a", [first, second]);
        UsbPause pause = fixture.Pause();
        var lateTransport = new UsbControlledTransport();
        bool oldDisposedBeforeNewOpen = false;
        fixture.Factory.Create = (_, call) =>
        {
            if (call == 1) { pause.Hold(); return lateTransport; }
            oldDisposedBeforeNewOpen = lateTransport.IsDisposed;
            return new UsbControlledTransport();
        };
        fixture.Start();
        fixture.Select(first.Path, settle: false);
        Task original = fixture.Track(fixture.Window.ReceiverSessionTask);
        PumpUntil(() => pause.Entered);
        fixture.Select(second.Path, settle: false);
        Task intermediate = fixture.Track(fixture.Window.ReceiverSessionTask);
        fixture.Select(first.Path, settle: false);
        Task final = fixture.Track(fixture.Window.ReceiverSessionTask);
        UsbPumpFor(TimeSpan.FromMilliseconds(60));
        Check(fixture.Factory.Requests.SequenceEqual(new[] { first.Path }) && fixture.Factory.MaximumConcurrent == 1,
            "Rapid A-B-A changes do not bypass the serialized factory gate");
        pause.Release();
        RunTask(Task.WhenAll(original, intermediate, final));
        fixture.Settle();
        Check(fixture.Factory.Requests.SequenceEqual(new[] { first.Path, first.Path })
            && fixture.Factory.MaximumConcurrent == 1 && oldDisposedBeforeNewOpen,
            "The superseded B wait is cancelled before its factory runs, and the final A open is serialized");
        Check(lateTransport.DisposalCount == 1 && lateTransport.Frames.Length == 0
            && fixture.Window.IsConnected && fixture.Window.ActiveReceiverPath == first.Path
            && ((ReceiverInfo)fixture.List.SelectedItem).Path == first.Path,
            "Only the newest A generation publishes status; the abandoned A transport remains unread");
        Check(fixture.Factory.Transports.Length == 2 && !fixture.Factory.Transports[^1].IsDisposed,
            "Late disposal cannot close the replacement session for the same physical path");
        VerifyUsbReadOnly(fixture, "rapid A-B-A selection");
    }

    private static void VerifyUsbUnplugDuringInitialRead()
    {
        ReceiverInfo first = UsbReceiver("initial-read-a");
        ReceiverInfo second = UsbReceiver("initial-read-b");
        using var fixture = new UsbIntegrationFixture("unplug-initial-read", [first]);
        UsbPause pause = fixture.Pause();
        var oldTransport = new UsbControlledTransport();
        oldTransport.PauseNext(A1Command.GetStatus, pause);
        fixture.Factory.Create = (_, call) => call == 1 ? oldTransport : new UsbControlledTransport();
        fixture.Start(awaitSession: false);
        Task initialRead = fixture.Track(fixture.Window.ReceiverSessionTask);
        PumpUntil(() => pause.Entered);
        Check(!fixture.Window.IsConnected && fixture.Window.IsBusy && fixture.Window.ActiveReceiverPath is null,
            "A blocked first status query never invents a confirmed active receiver");
        fixture.Discovery.Set(second);
        fixture.Rescan();
        fixture.Settle();
        string result = fixture.Window.ResultText;
        Check(oldTransport.IsDisposed && fixture.Window.ActiveReceiverPath == second.Path,
            "A replacement can finish its own initial read while the old cancelled query is still blocked");
        pause.Release();
        RunTask(initialRead);
        PumpUntil(() => oldTransport.InFlight == 0);
        Check(fixture.Window.ActiveReceiverPath == second.Path && fixture.Window.ResultText == result
            && oldTransport.Frames.Count(frame => frame.Command == A1Command.GetStatus) == 1
            && oldTransport.Frames.All(frame => frame.Command != A1Command.GetVersion),
            "Late initial status cannot publish stale data or continue reading old receiver firmware");
        VerifyUsbReadOnly(fixture, "unplug during the first automatic status read");
    }

    private static void VerifyUsbUnplugDuringTraffic(A1Command command)
    {
        ReceiverInfo first = UsbReceiver("traffic-a");
        ReceiverInfo replacement = UsbReceiver("traffic-b");
        using var fixture = new UsbIntegrationFixture("unplug-" + command, [first]);
        fixture.Start();
        UsbControlledTransport oldTransport = fixture.Factory.Transports.Single();
        UsbPause pause = fixture.Pause();
        oldTransport.PauseNext(command, pause);
        SettingRowViewModel gain = fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain");
        Task operation;
        if (command == A1Command.SetGain)
        {
            gain.SelectedIndex = 0;
            operation = fixture.Track(fixture.Window.ApplySettingAsync(gain));
        }
        else operation = fixture.Track(fixture.Window.RefreshAsync());
        PumpUntil(() => pause.Entered);
        Check(fixture.Window.IsBusy && fixture.List.IsEnabled, "Physical selection remains available during a paused " + command);
        fixture.Discovery.Set();
        fixture.Rescan();
        Check(oldTransport.IsDisposed && !fixture.Window.IsConnected && !fixture.Window.IsBusy
            && !gain.IsDirty && gain.SelectedIndex == -1 && !gain.CanApply,
            "Unplug during " + command + " clears confirmed data, drafts and action availability immediately");
        fixture.Discovery.Set(replacement);
        fixture.Rescan();
        fixture.Settle();
        string confirmedResult = fixture.Window.ResultText;
        Check(fixture.Window.ActiveReceiverPath == replacement.Path && gain.SelectedIndex == 3 && !gain.IsDirty,
            "The replacement receiver starts with its own confirmed state, not the previous draft");
        pause.Release();
        RunTask(operation);
        PumpUntil(() => oldTransport.InFlight == 0);
        Pump();
        Check(fixture.Window.ActiveReceiverPath == replacement.Path && fixture.Window.ResultText == confirmedResult
            && gain.SelectedIndex == 3 && !gain.IsDirty && !fixture.Factory.Transports[^1].IsDisposed,
            "Late " + command + " traffic cannot overwrite or dispose the replacement session");
        Check(fixture.Factory.Transports[^1].Frames.All(frame => frame.Command is A1Command.GetStatus or A1Command.GetVersion),
            "A replacement after interrupted traffic never receives the old receiver's writes");
        int settingsWrites = fixture.Factory.Transports.SelectMany(transport => transport.Frames)
            .Count(frame => frame.Command is not (A1Command.GetStatus or A1Command.GetVersion));
        Check(settingsWrites == (command == A1Command.SetGain ? 1 : 0),
            "Only the explicitly requested fake write may have reached the old device; it is never replayed");
    }

    private static void VerifyUsbScanFailureIsolation()
    {
        ReceiverInfo receiver = UsbReceiver("scan-error");
        using var fixture = new UsbIntegrationFixture("scan-error", [receiver]);
        fixture.Start();
        fixture.ShowReceiverTab();
        ListBox list = fixture.List;
        object? source = list.ItemsSource;
        object entry = list.SelectedItem;
        object container = list.ItemContainerGenerator.ContainerFromItem(entry);
        UsbControlledTransport transport = fixture.Factory.Transports.Single();
        fixture.Discovery.FailWith(new IOException("Simulated enumeration failure"));
        string failure = Localization.Text("Main.Usb.ScanFailed");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            fixture.Rescan();
            Check(fixture.Window.IsConnected && fixture.Window.ActiveReceiverPath == receiver.Path && !transport.IsDisposed
                && ReferenceEquals(list.ItemsSource, source) && ReferenceEquals(list.SelectedItem, entry)
                && ReferenceEquals(list.ItemContainerGenerator.ContainerFromItem(entry), container),
                "Enumeration failure preserves the valid session and native selection: " + attempt);
        }
        Check(fixture.Window.ResultText == failure && UsbLog(fixture.Window).Count(line => line.Contains(failure, StringComparison.Ordinal)) == 1,
            "Repeated enumeration errors report one essential result instead of repeating user-facing announcements");
        Check(fixture.Factory.Calls == 1 && AllSettings(fixture.Window).All(setting => setting.CanApply),
            "Enumeration errors do not reopen the transport or disable a confirmed session");
        fixture.Discovery.FailWith(null);
        fixture.Rescan();
        Check(fixture.Window.ResultText == Localization.Text("Main.Usb.Ready", receiver.Name, 2, 2) && fixture.Factory.Calls == 1,
            "Successful enumeration clears its prior failure without reopening an unchanged receiver");
        VerifyUsbReadOnly(fixture, "scan failure and recovery");
    }

    private static void VerifyUsbBoundedRetries()
    {
        ReceiverInfo receiver = UsbReceiver("retry");
        using var fixture = new UsbIntegrationFixture("bounded-retry", [receiver]);
        bool fail = true;
        fixture.Factory.Create = (_, _) => new UsbControlledTransport(fail);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        fixture.Start();
        string failure = Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.Failed"));
        Check(!fixture.Window.IsConnected && fixture.Factory.Calls == 1 && fixture.Window.ResultText == failure
            && fixture.Factory.Transports.Single().IsDisposed,
            "A failed automatic read disposes its transport and exposes an essential error");
        DateTimeOffset deadline = UsbField<DateTimeOffset>(fixture.Window, "nextReceiverRetry");
        Check(deadline >= started.AddSeconds(2) && deadline <= DateTimeOffset.UtcNow.AddSeconds(2),
            "The first automatic retry is delayed by a bounded two-second backoff");
        UsbSetRetryTime(fixture.Window, DateTimeOffset.UtcNow.AddMinutes(1));
        for (int attempt = 0; attempt < 4; attempt++) { fixture.Rescan(); fixture.Settle(); }
        Check(fixture.Factory.Calls == 1 && fixture.Window.ResultText == failure,
            "Ordinary monitoring scans do not retry before the recorded deadline");
        foreach (int expectedDelay in new[] { 4, 8, 16, 30, 30 })
        {
            UsbSetRetryTime(fixture.Window, DateTimeOffset.MinValue);
            DateTimeOffset before = DateTimeOffset.UtcNow;
            fixture.Rescan();
            fixture.Settle();
            DateTimeOffset after = DateTimeOffset.UtcNow;
            deadline = UsbField<DateTimeOffset>(fixture.Window, "nextReceiverRetry");
            Check(deadline >= before.AddSeconds(expectedDelay) && deadline <= after.AddSeconds(expectedDelay)
                && UsbField<int>(fixture.Window, "receiverFailures") <= 5,
                "Repeated failures increase the retry delay but cap it at thirty seconds: " + expectedDelay);
        }
        Check(fixture.Factory.Calls == 6 && fixture.Factory.Transports.All(transport => transport.IsDisposed)
            && UsbLog(fixture.Window).Count(line => line.Contains(failure, StringComparison.Ordinal)) == 1,
            "Automatic retry failures dispose every failed session and suppress duplicate user-facing error results");
        Check(FindById<TextBlock>(fixture.Window, "ConnectionReadout").Text == Localization.Text("Main.Usb.Unavailable", receiver.Name),
            "Failure keeps the chosen receiver identifiable with the localized retry instruction");
        fail = false;
        UsbSetRetryTime(fixture.Window, DateTimeOffset.UtcNow.AddMinutes(1));
        int previousCalls = fixture.Factory.Calls;
        Check(MainWindow.RefreshCommand.CanExecute(null, fixture.Window), "F5 remains available after an automatic read failure");
        MainWindow.RefreshCommand.Execute(null, fixture.Window);
        PumpUntil(() => fixture.Factory.Calls > previousCalls);
        fixture.Settle();
        Check(fixture.Window.IsConnected && fixture.Window.ActiveReceiverPath == receiver.Path
            && fixture.Factory.Calls == previousCalls + 1 && UsbField<int>(fixture.Window, "receiverFailures") == 0
            && UsbField<DateTimeOffset>(fixture.Window, "nextReceiverRetry") == DateTimeOffset.MinValue,
            "The routed F5 command bypasses backoff, confirms recovery and clears prior retry failure state");
        VerifyUsbReadOnly(fixture, "bounded retries and routed F5 recovery");
    }

    private static void VerifyUsbDraftIsolation(bool automaticApply)
    {
        ReceiverInfo first = UsbReceiver("draft-a");
        ReceiverInfo second = UsbReceiver("draft-b");
        var preferences = new UiPreferences
        {
            Language = Localization.CurrentLanguage, FirstRunCompleted = true, AutoApplySelections = automaticApply
        };
        using var fixture = new UsbIntegrationFixture("draft-isolation-" + automaticApply, [first], preferences);
        fixture.Start();
        fixture.Discovery.Set(first, second);
        fixture.Rescan();
        SettingRowViewModel gain = fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain");
        gain.SelectedIndex = 0;
        Check(gain.IsDirty, "The old receiver has a real unconfirmed gain draft before switching: " + automaticApply);
        if (automaticApply)
            Check(UsbField<HashSet<SettingRowViewModel>>(fixture.Window, "pendingAutomaticChanges").Contains(gain)
                && UsbField<DispatcherTimer>(fixture.Window, "autoApplyTimer").IsEnabled,
                "The automatic-apply case really queues its old-receiver draft before cancellation");
        fixture.Select(second.Path, settle: false);
        Check(UsbField<HashSet<SettingRowViewModel>>(fixture.Window, "pendingAutomaticChanges").Count == 0
            && !UsbField<DispatcherTimer>(fixture.Window, "autoApplyTimer").IsEnabled,
            "Switching receivers cancels the old draft queue and timer synchronously");
        fixture.Settle();
        RunTask(fixture.Window.FlushAutomaticChangesAsync());
        UsbPumpFor(TimeSpan.FromMilliseconds(350));
        Check(fixture.Window.ActiveReceiverPath == second.Path && gain.SelectedIndex == 3
            && AllSettings(fixture.Window).All(setting => !setting.IsDirty),
            "The replacement snapshot, not any old draft, supplies every confirmed setting");
        VerifyUsbReadOnly(fixture, "manual/queued-auto draft cancellation: " + automaticApply);
    }

    private static void VerifyUsbCloseDuringScan()
    {
        using var fixture = new UsbIntegrationFixture("close-scan", []);
        fixture.Start();
        UsbPause pause = fixture.Pause();
        fixture.Discovery.Set(UsbReceiver("late-scan"));
        fixture.Discovery.PauseNext(pause);
        Task scan = fixture.Track(fixture.Window.RefreshUsbAsync());
        PumpUntil(() => pause.Entered);
        Check(ReferenceEquals(scan, fixture.Window.RefreshUsbAsync(retryFailed: true))
            && !fixture.Window.IsBusy && fixture.List.IsEnabled,
            "Concurrent refresh requests share one nonblocking physical enumeration");
        string result = fixture.Window.ResultText;
        fixture.Window.RequestExit();
        Check(!fixture.Window.IsVisible && !fixture.ReceiverTimer.IsEnabled && !fixture.PollTimer.IsEnabled,
            "Closing does not wait for a blocked physical enumeration or leave timers active");
        pause.Release();
        RunTask(scan);
        int calls = fixture.Discovery.Calls;
        RunTask(fixture.Window.RefreshUsbAsync());
        RunTask(fixture.Window.ScanAsync());
        Check(fixture.Discovery.Calls == calls && fixture.Factory.Calls == 0 && fixture.Window.Receivers.Count == 0
            && !fixture.Window.IsConnected && fixture.Window.ActiveReceiverPath is null && fixture.Window.ResultText == result,
            "A late scan and post-close refresh calls cannot repopulate UI, open USB or replace the result");
    }

    private static void VerifyUsbRemovedItemFocus()
    {
        ReceiverInfo first = UsbReceiver("focus-a");
        ReceiverInfo second = UsbReceiver("focus-b");
        using var fixture = new UsbIntegrationFixture("focus-removal", [first]);
        fixture.Start();
        fixture.ShowReceiverTab();
        fixture.Window.Activate();
        var item = (ListBoxItem)fixture.List.ItemContainerGenerator.ContainerFromItem(fixture.List.SelectedItem);
        FocusForKeyboardCheck(item);
        fixture.Discovery.Set(first, second);
        fixture.Rescan();
        CheckKeyboardFocus(item, "Adding an unrelated receiver preserves the focused native item");
        fixture.Discovery.Set(second, first);
        fixture.Rescan();
        RunTask(Poll(fixture.Window));
        CheckKeyboardFocus(item, "Unchanged/reordered USB scans and status polling preserve receiver item focus");
        fixture.Discovery.Set(second);
        fixture.Rescan();
        fixture.Settle();
        Check(fixture.Window.ActiveReceiverPath == second.Path,
            "Removing a focused active receiver automatically reads the sole remaining receiver");
        CheckKeyboardFocus(FindById<TextBlock>(fixture.Window, "ConnectionReadout"),
            "Removing the focused native receiver item falls back to the essential connection readout");
        VerifyUsbReadOnly(fixture, "native receiver focus removal");
    }

    private static void VerifyUsbOtherWindowFocus(bool auxiliary)
    {
        ReceiverInfo first = UsbReceiver("other-focus-a");
        ReceiverInfo second = UsbReceiver("other-focus-b");
        using var fixture = new UsbIntegrationFixture("other-window-focus-" + auxiliary, [first]);
        fixture.Start();
        UsbPause pause = fixture.Pause();
        fixture.Factory.Create = (_, _) => { pause.Hold(); return new UsbControlledTransport(); };
        fixture.Window.Activate();
        FocusForKeyboardCheck(FindById<TextBlock>(fixture.Window, "ConnectionReadout"));
        fixture.Discovery.Set(second);
        fixture.Rescan();
        Task connecting = fixture.Track(fixture.Window.ReceiverSessionTask);
        PumpUntil(() => pause.Entered);
        var foreignReadout = new TextBox { Text = "Independent keyboard focus target", IsReadOnly = true };
        Window other = auxiliary ? fixture.Window.OpenHelpWindow()
            : new Window { Content = foreignReadout, Width = 320, Height = 200 };
        try
        {
            MoveOffscreen(other);
            if (!other.IsVisible) other.Show();
            other.Activate();
            UIElement target = auxiliary ? FindById<TextBox>(other, "HelpReadout") : foreignReadout;
            FocusForKeyboardCheck(target);
            Check(other.IsActive && !fixture.Window.IsActive,
                "The focus fixture really activates its separate auxiliary/foreign window: " + auxiliary);
            pause.Release();
            RunTask(connecting);
            fixture.Settle();
            Check(fixture.Window.ActiveReceiverPath == second.Path && other.IsActive && !fixture.Window.IsActive,
                "A late automatic connection completion never activates the main window: " + auxiliary);
            CheckKeyboardFocus(target, "Automatic connection completion preserves auxiliary/foreign focus: " + auxiliary);
            fixture.Discovery.Set();
            fixture.Rescan();
            Check(!fixture.Window.IsConnected && other.IsActive && !fixture.Window.IsActive,
                "Unplug does not activate the main window behind an auxiliary/foreign window: " + auxiliary);
            CheckKeyboardFocus(target, "Automatic removal preserves auxiliary/foreign keyboard focus: " + auxiliary);
            VerifyUsbReadOnly(fixture, "auxiliary/foreign focus isolation");
        }
        finally { pause.Release(); other.Close(); Pump(); }
    }

    private static ReceiverInfo UsbReceiver(string identifier, string? name = null) =>
        new(name ?? "Simulated USB " + identifier, "simulated://automatic-usb/" + identifier, 0, 64);

    private static TField UsbField<TField>(MainWindow window, string name) =>
        (TField)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void UsbSetRetryTime(MainWindow window, DateTimeOffset value) =>
        typeof(MainWindow).GetField("nextReceiverRetry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static string[] UsbLog(MainWindow window) => UsbField<Queue<string>>(window, "logLines").ToArray();

    private static void UsbPumpFor(TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration) { Pump(); Thread.Sleep(2); }
    }

    private static void VerifyUsbReadOnly(UsbIntegrationFixture fixture, string operation) =>
        Check(fixture.Factory.Transports.SelectMany(transport => transport.Frames)
            .All(frame => frame.Command is A1Command.GetStatus or A1Command.GetVersion),
            "Automatic USB uses only status/version reads: " + operation);

    private sealed class UsbPause : IDisposable
    {
        private readonly ManualResetEventSlim entered = new();
        private readonly ManualResetEventSlim released = new();
        private readonly ManualResetEventSlim returned = new();
        internal bool Entered => entered.IsSet;
        internal bool Returned => returned.IsSet;

        internal void Hold()
        {
            entered.Set();
            try
            {
                if (!released.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The simulated USB pause was not released.");
            }
            finally { returned.Set(); }
        }

        internal void Release() => released.Set();

        public void Dispose()
        {
            Release();
            if (Entered && !returned.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The simulated USB worker did not leave its pause.");
            entered.Dispose();
            released.Dispose();
            returned.Dispose();
        }
    }

    private sealed class UsbMutableDiscovery
    {
        private readonly object sync = new();
        private ReceiverInfo[] receivers = [];
        private Exception? failure;
        private UsbPause? pendingPause;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);

        internal void Set(params ReceiverInfo[] snapshot) { lock (sync) receivers = snapshot.ToArray(); }
        internal void FailWith(Exception? error) { lock (sync) failure = error; }
        internal void PauseNext(UsbPause pause) => Interlocked.Exchange(ref pendingPause, pause);

        internal IReadOnlyList<ReceiverInfo> Discover()
        {
            Interlocked.Increment(ref calls);
            ReceiverInfo[] snapshot;
            Exception? error;
            lock (sync) { snapshot = receivers.ToArray(); error = failure; }
            Interlocked.Exchange(ref pendingPause, null)?.Hold();
            if (error is not null) throw error;
            return snapshot;
        }
    }

    private sealed class UsbControlledTransport : IA1Transport
    {
        private readonly FakeTransport inner;
        private readonly ConcurrentQueue<A1Frame> frames = new();
        private Action<A1Frame>? beforeSend;
        private int inFlight;
        private int disposalCount;
        internal A1Frame[] Frames => frames.ToArray();
        internal int InFlight => Volatile.Read(ref inFlight);
        internal int DisposalCount => Volatile.Read(ref disposalCount);
        internal bool IsDisposed => DisposalCount > 0;
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;

        internal UsbControlledTransport(bool fail = false)
        {
            inner = new FakeTransport { Fail = fail };
            inner.ReportReceived += report => ReportReceived?.Invoke(report);
            inner.Failed += error => Failed?.Invoke(error);
        }

        internal void PauseNext(A1Command command, UsbPause pause)
        {
            int armed = 1;
            Volatile.Write(ref beforeSend, frame =>
            {
                if (frame.Command == command && Interlocked.Exchange(ref armed, 0) == 1) pause.Hold();
            });
        }

        public void SendFeature(byte[] report)
        {
            Interlocked.Increment(ref inFlight);
            try
            {
                if (!A1Protocol.TryDecodeRequest(report, out A1Frame? frame) || frame is null)
                    throw new InvalidDataException("The simulated USB transport received a malformed request.");
                frames.Enqueue(frame);
                Volatile.Read(ref beforeSend)?.Invoke(frame);
                inner.SendFeature(report);
            }
            finally { Interlocked.Decrement(ref inFlight); }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref disposalCount);
            inner.Dispose();
        }
    }

    private sealed class UsbTransportFactory
    {
        private readonly object sync = new();
        private readonly ConcurrentQueue<string> requests = new();
        private readonly ConcurrentQueue<UsbControlledTransport> transports = new();
        private int inFlight;
        private int maximumConcurrent;
        private int calls;
        internal Func<ReceiverInfo, int, UsbControlledTransport> Create { get; set; } = (_, _) => new UsbControlledTransport();
        internal string[] Requests => requests.ToArray();
        internal UsbControlledTransport[] Transports => transports.ToArray();
        internal int Calls => Volatile.Read(ref calls);
        internal int InFlight { get { lock (sync) return inFlight; } }
        internal int MaximumConcurrent { get { lock (sync) return maximumConcurrent; } }

        internal IA1Transport Open(ReceiverInfo receiver)
        {
            int call = Interlocked.Increment(ref calls);
            requests.Enqueue(receiver.Path);
            lock (sync) { inFlight++; maximumConcurrent = Math.Max(maximumConcurrent, inFlight); }
            try
            {
                UsbControlledTransport transport = Create(receiver, call);
                transports.Enqueue(transport);
                return transport;
            }
            finally { lock (sync) inFlight--; }
        }
    }

    private sealed class UsbIntegrationFixture : IDisposable
    {
        private readonly List<UsbPause> pauses = [];
        private readonly List<Task> tasks = [];
        internal UsbMutableDiscovery Discovery { get; } = new();
        internal UsbTransportFactory Factory { get; } = new();
        internal MemoryDesktop Desktop { get; } = new();
        internal List<string> DesktopErrors { get; } = [];
        internal UiPreferencesStore Store { get; }
        internal MainWindow Window { get; }
        internal ListBox List => (ListBox)Window.FindName("ReceiverList");
        internal DispatcherTimer ReceiverTimer => UsbField<DispatcherTimer>(Window, "receiverTimer");
        internal DispatcherTimer PollTimer => UsbField<DispatcherTimer>(Window, "pollTimer");

        internal UsbIntegrationFixture(string name, ReceiverInfo[] receivers, UiPreferences? preferences = null)
        {
            Discovery.Set(receivers);
            Store = new UiPreferencesStore(Path.Combine(ReportDirectory, "automatic-usb",
                Localization.CurrentLanguage + "-" + name + "-" + Guid.NewGuid().ToString("N")));
            preferences ??= new UiPreferences { Language = Localization.CurrentLanguage, FirstRunCompleted = true };
            Check(Store.TrySave(preferences, out string? error), "Prepare isolated automatic USB preferences: " + error);
            Window = new MainWindow(Discovery.Discover, Factory.Open, Store.BaseDirectory, Desktop,
                (_, _) => true, _ => { }, DesktopErrors.Add);
            Check(UsbField<bool>(Window, "automaticReceiverManagement"), "The USB integration fixture uses the constructor's default automatic policy");
        }

        internal UsbPause Pause() { var pause = new UsbPause(); pauses.Add(pause); return pause; }
        internal Task Track(Task task) { tasks.Add(task); return task; }

        internal void Start(bool awaitSession = true, bool monitor = false)
        {
            ReceiverTimer.Interval = monitor ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMinutes(1);
            MoveOffscreen(Window);
            Window.Show();
            PumpUntil(() => UsbField<bool>(Window, "loaded"));
            Check(ReceiverTimer.IsEnabled, "Showing a default-policy window starts physical USB monitoring");
            if (!monitor) ReceiverTimer.Stop();
            Rescan();
            if (awaitSession) Settle();
        }

        internal void Rescan(bool retry = false) => RunTask(Track(Window.RefreshUsbAsync(retry)));

        internal void Settle()
        {
            RunTask(Track(Window.ReceiverSessionTask));
            PollTimer.Stop();
        }

        internal void Select(string path, bool settle = true)
        {
            List.SelectedItem = Window.Receivers.Single(receiver => receiver.Path == path);
            Track(Window.ReceiverSessionTask);
            if (settle) Settle();
        }

        internal void ShowReceiverTab()
        {
            ((TabControl)Window.FindName("MainTabs")).SelectedItem = Window.FindName("ReceiverTab");
            Pump();
            List.UpdateLayout();
        }

        public void Dispose()
        {
            ReceiverTimer.Stop();
            PollTimer.Stop();
            foreach (UsbPause pause in pauses) pause.Release();
            if (!UsbField<bool>(Window, "closed")) Window.RequestExit();
            Task[] pending = tasks.Append(UsbField<Task>(Window, "receiverScanTask")).Append(Window.ReceiverSessionTask).Distinct().ToArray();
            RunTask(Task.WhenAll(pending));
            PumpUntil(() => Factory.InFlight == 0 && Factory.Transports.All(transport => transport.InFlight == 0));
            foreach (UsbControlledTransport transport in Factory.Transports.Where(transport => !transport.IsDisposed)) transport.Dispose();
            foreach (UsbPause pause in pauses) pause.Dispose();
            Check(DesktopErrors.Count == 0, "USB integration reports no hidden desktop cleanup errors");
            Pump();
        }
    }
}
