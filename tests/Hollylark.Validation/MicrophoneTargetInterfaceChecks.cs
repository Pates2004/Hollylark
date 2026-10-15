using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    public static void VerifyMicrophoneTargetInterfaceChecks()
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && Application.Current is not null && Application.Current.Dispatcher.CheckAccess()
            && SynchronizationContext.Current is DispatcherSynchronizationContext,
            "Microphone-target checks reuse the existing STA Application and dispatcher context");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyMicrophoneTargetInitialSelection();
                VerifyMicrophoneTargetArrowNavigation();
                VerifyMicrophoneTargetIndividualGuards();
                VerifyMicrophoneTargetGroupOperations();
                VerifyMicrophoneTargetDraftIsolation();
                VerifyMicrophoneTargetMembershipDrafts();
                VerifyMicrophoneTargetHiddenSelection();
                VerifyMicrophoneTargetFreshScopeGuards();
                VerifyMicrophoneTargetPartialMuteFailure();
                VerifyGroupMuteFinalReadback();
                VerifyMicrophoneTargetMembershipLoss();
                VerifyFourMicrophoneTargetIncompleteConnection();
                VerifyFourMicrophoneTargetInterface();
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyMicrophoneTargetArrowNavigation()
    {
        using var transport = new FakeTransport();
        WithMicrophoneTargetWindow(transport, "native-arrows", window =>
        {
            using var observer = new ExternalArrowObserver(window, "microphone-target-arrows-" + Localization.CurrentLanguage, includeFocus: true);
            VerifyListItemNavigation(FindById<ListBox>(window, "TransmitterList"), observer);
            VerifyMicrophoneTargetSelectedAction(window);
            Check(CountMockWrites(transport) == 0, "Native target arrows emit real UIA focus and selection without any writes");
        });
    }

    private static void WithMicrophoneTargetWindow(FakeTransport transport, string scenario, Action<MainWindow> verify)
    {
        MainWindow window = CreatePresentationWindow(transport,
            "microphone-target-" + scenario + "-" + Localization.CurrentLanguage + "-" + Guid.NewGuid().ToString("N"),
            new MemoryDesktop());
        try
        {
            SavePreferences(window, window.CurrentPreferences with
            {
                Language = Localization.CurrentLanguage, FirstRunCompleted = true, HiddenRows = [],
                AutoApplySelections = false, GainControlMode = GainControlMode.Choice,
                ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false
            });
            RunTask(window.ConnectAsync());
            MicrophoneTargetField<DispatcherTimer>(window, "pollTimer").Stop();
            Check(window.IsConnected && !window.IsBusy, "Microphone-target fixture connects to its fake receiver: " + scenario);
            verify(window);
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void VerifyMicrophoneTargetInitialSelection()
    {
        for (int mask = 0; mask < 4; mask++)
        {
            using var transport = new FakeTransport();
            transport.SetTransmitterConnected(0, (mask & 1) != 0);
            transport.SetTransmitterConnected(1, (mask & 2) != 0);
            WithMicrophoneTargetWindow(transport, "initial-" + mask, window =>
            {
                ListBox list = FindById<ListBox>(window, "TransmitterList");
                TransmitterViewModel group = window.TransmitterTargets.Last();
                TransmitterViewModel expected = mask == 1 ? window.Transmitters[0]
                    : mask == 2 ? window.Transmitters[1] : group;
                Check(ReferenceEquals(list.SelectedItem, expected),
                    "First status selects the sole active microphone, otherwise All: " + mask);
                VerifyMicrophoneTargetNativeSurface(window);
                foreach (TransmitterViewModel target in window.TransmitterTargets)
                {
                    SelectMicrophoneTarget(window, target);
                    RunTask(Poll(window));
                    Check(ReferenceEquals(list.SelectedItem, target),
                        "Polling preserves an explicit physical or group target: " + mask + "/" + target.ItemId);
                }

                if (mask == 3) return;
                SelectMicrophoneTarget(window, group);
                string reason = Localization.Text(mask == 0 ? "Main.Change.TransmitterRequired" : "Target.GroupUnavailable");
                int writes = CountMockWrites(transport);
                SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
                int original = gain.SelectedIndex;
                gain.SelectedIndex = (original + 1) % gain.Options.Count;
                RunTask(window.ApplySettingAsync(gain));
                Check(!gain.IsDirty && gain.SelectedIndex == original && !gain.CanApply && window.ResultText == reason,
                    "All rejects shared gain with fewer than two active microphones without a draft: " + mask);
                RunTask(window.ToggleAllMicrophonesMuteAsync());
                Check(CountMockWrites(transport) == writes && window.ResultText == reason
                    && ReferenceEquals(list.SelectedItem, group) && !group.CanMute
                    && FindById<TextBlock>(window, "ResultReadout").IsVisible,
                    "All rejects mute with zero or one active microphone and exposes the error without hints: " + mask);
                if (mask == 0) return;

                TransmitterViewModel sole = window.Transmitters[mask == 1 ? 0 : 1];
                SelectMicrophoneTarget(window, sole);
                int requested = (original + 1) % gain.Options.Count;
                gain.SelectedIndex = requested;
                RunTask(window.ApplySettingAsync(gain));
                Check(CountMockWrites(transport) == writes + 1 && gain.SelectedIndex == requested
                    && gain.ConfirmedValue == gain.Options[requested] && !gain.IsDirty,
                    "The selected sole active microphone can confirm the receiver-wide gain: " + sole.ItemId);
                bool wasMuted = sole.IsMuted;
                RunTask(window.ToggleMuteAsync(sole.Index));
                Check(CountMockWrites(transport) == writes + 2 && sole.IsMuted != wasMuted
                    && ReferenceEquals(list.SelectedItem, sole),
                    "The selected sole active microphone can confirm indexed mute: " + sole.ItemId);
            });
        }
    }

    private static void VerifyMicrophoneTargetNativeSurface(MainWindow window, A1Status? expectedStatus = null)
    {
        A1Status expected = expectedStatus ?? A1Status.Parse(ValidStatus);
        ListBox list = FindById<ListBox>(window, "TransmitterList");
        AutomationPeer peer = GetAccessiblePeer(list);
        Check(list.GetType() == typeof(ListBox) && peer is ListBoxAutomationPeer
            && peer.GetAutomationControlType() == AutomationControlType.List
            && peer.GetName() == Localization.Text("Main.Control.Microphones")
            && list.SelectionMode == SelectionMode.Single
            && peer.GetPattern(PatternInterface.Selection) is ISelectionProvider { CanSelectMultiple: false },
            "Target selection retains the localized native single-selection ListBox contract: " + Localization.CurrentLanguage);
        Check(ReferenceEquals(list.ItemsSource, window.TransmitterTargets) && window.Transmitters.Count == expected.Transmitters.Count
            && list.Items.Count == window.Transmitters.Count + 1
            && window.Transmitters.Select(target => target.Index).SequenceEqual(Enumerable.Range(0, expected.Transmitters.Count))
            && window.Transmitters.All(target => !target.IsGroup)
            && window.TransmitterTargets.Take(window.Transmitters.Count).SequenceEqual(window.Transmitters),
            "Target ItemsSource includes the same exact physical slots and one extra item, without polluting Transmitters");
        TransmitterViewModel group = window.TransmitterTargets.Last();
        Check(group.IsGroup && group.Index == -1 && group.ItemId == "AllTX.Item" && group.ActionId == "AllTX.Mute"
            && window.TransmitterTargets.Count(target => target.IsGroup) == 1,
            "Exactly one All target is last and exposes its stable group identifiers");
        list.UpdateLayout();
        for (int index = 0; index < list.Items.Count; index++)
        {
            TransmitterViewModel target = window.TransmitterTargets[index];
            list.ScrollIntoView(target);
            list.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            AutomationPeer itemPeer = GetAccessiblePeer(item);
            Check(itemPeer.GetAutomationControlType() == AutomationControlType.ListItem
                && itemPeer.GetAutomationId() == target.ItemId && itemPeer.GetName() == target.Summary
                && !itemPeer.GetName().Contains("LarkControl.Windows.", StringComparison.Ordinal),
                "Native target items expose their localized summary and ID, never a CLR type: " + target.ItemId);
        }
        VerifyMicrophoneTargetGroupSummary(window);
        foreach (TransmitterViewModel target in window.Transmitters.Where(target => target.IsConnected))
        {
            string battery = expected.Transmitters[target.Index].BatteryPercent is int percent
                ? Localization.Text("Transmitter.BatteryPercent", percent) : Localization.Text("Transmitter.BatteryUnknown");
            Check(target.Summary.Contains(battery, StringComparison.Ordinal),
                "Physical target retains its individual battery readout: " + target.ItemId);
        }
        VerifyMicrophoneTargetSelectedAction(window);
    }

    private static void VerifyMicrophoneTargetGroupSummary(MainWindow window)
    {
        TransmitterViewModel group = window.TransmitterTargets.Last();
        TransmitterViewModel[] active = window.Transmitters.Where(target => target.IsConnected).ToArray();
        bool allMuted = active.Length > 0 && active.All(target => target.IsMuted);
        string state = Localization.Text(active.Length == 0 ? "Transmitter.Disconnected"
            : allMuted ? "Transmitter.Muted" : active.Any(target => target.IsMuted) ? "Target.All.Mixed" : "Transmitter.Active");
        Check(group.Summary == Localization.Text("Target.All.Summary", active.Length, state)
            && group.IsMuted == allMuted && group.IsConnected == (active.Length >= 2)
            && group.ReadoutName == Localization.Text("Target.All.Name")
            && !group.Summary.Contains('%')
            && !group.Summary.Contains(Localization.Text("Transmitter.BatteryUnknown"), StringComparison.Ordinal),
            "All summarizes the active count and real aggregate mute state without a fictitious battery");
    }

    private static void SelectMicrophoneTarget(MainWindow window, TransmitterViewModel target)
    {
        FindById<ListBox>(window, "TransmitterList").SelectedItem = target;
        Pump();
        VerifyMicrophoneTargetSelectedAction(window);
    }

    private static void VerifyMicrophoneTargetSelectedAction(MainWindow window)
    {
        var target = (TransmitterViewModel)FindById<ListBox>(window, "TransmitterList").SelectedItem;
        Button[] actions = Descendants(window).OfType<Button>().Where(button => button.IsVisible)
            .Where(button => AutomationProperties.GetAutomationId(button).EndsWith(".Mute", StringComparison.Ordinal)).ToArray();
        Check(actions.Length == 1 && AutomationProperties.GetAutomationId(actions[0]) == target.ActionId
            && ReferenceEquals(actions[0].DataContext, target) && actions[0].IsTabStop
            && GetAccessiblePeer(actions[0]).GetAutomationControlType() == AutomationControlType.Button
            && GetAccessiblePeer(actions[0]).GetName() == target.ActionName,
            "Only the selected target exposes a visible, named native mute button in the Tab surface: " + target.ItemId);
    }

    private static void VerifyMicrophoneTargetIndividualGuards()
    {
        using var transport = new FakeTransport();
        WithMicrophoneTargetWindow(transport, "individual-guards", window =>
        {
            foreach (TransmitterViewModel target in window.Transmitters)
            {
                SelectMicrophoneTarget(window, target);
                int writes = CountMockWrites(transport);
                foreach (SettingRowViewModel setting in AllSettings(window))
                    VerifyMicrophoneTargetBlockedChoice(window, setting);
                VerifyMicrophoneTargetBlockedSlider(window);
                Check(CountMockWrites(transport) == writes,
                    "Individual shared-parameter attempts send no writes when several microphones are active: " + target.ItemId);
                bool originalMute = target.IsMuted;
                bool[] otherStates = window.Transmitters.Where(other => !ReferenceEquals(other, target))
                    .Select(other => other.IsMuted).ToArray();
                RunTask(window.ToggleMuteAsync(target.Index));
                Check(CountMockWrites(transport) == writes + 1 && target.IsMuted != originalMute
                    && window.Transmitters.Where(other => !ReferenceEquals(other, target)).Select(other => other.IsMuted).SequenceEqual(otherStates)
                    && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, target),
                    "Indexed mute changes only its selected microphone among multiple active transmitters: " + target.ItemId);
                VerifyMicrophoneTargetSelectedAction(window);
            }
        });
    }

    private static void VerifyMicrophoneTargetBlockedChoice(MainWindow window, SettingRowViewModel setting)
    {
        ComboBox choice = FindById<ComboBox>(window, setting.ChoiceId);
        object target = FindById<ListBox>(window, "TransmitterList").SelectedItem;
        int original = setting.SelectedIndex;
        long revision = setting.Revision;
        string confirmed = setting.ConfirmedValue;
        int selectionChanges = 0;
        int modelChanges = 0;
        SelectionChangedEventHandler selectionChanged = (_, _) => selectionChanges++;
        PropertyChangedEventHandler modelChanged = (_, arguments) =>
        {
            if (arguments.PropertyName is nameof(SettingRowViewModel.SelectedIndex) or nameof(SettingRowViewModel.IsDirty)) modelChanges++;
        };
        Check(choice.IsVisible && choice.SelectedIndex == original && original >= 0 && !setting.IsDirty
            && !setting.CanApply && setting.UnavailableReason == Localization.Text("Target.SharedParameters"),
            "An individual target exposes its actual confirmed value and shared-parameter restriction: " + setting.Id);
        choice.SelectionChanged += selectionChanged;
        setting.PropertyChanged += modelChanged;
        try
        {
            int alternative = (original + 1) % setting.Options.Count;
            setting.SelectedIndex = alternative;
            choice.SetCurrentValue(ComboBox.SelectedIndexProperty, alternative);
            RunTask(window.ApplySettingAsync(setting));
            Check(selectionChanges == 0 && modelChanges == 0 && setting.Revision == revision
                && choice.SelectedIndex == original && setting.SelectedIndex == original
                && setting.ConfirmedValue == confirmed && !setting.IsDirty
                && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, target)
                && window.ResultText == Localization.Text("Target.SharedParameters"),
                "Guarded model and ComboBox attempts cause no transient selection or draft notification: " + setting.Id);
        }
        finally
        {
            choice.SelectionChanged -= selectionChanged;
            setting.PropertyChanged -= modelChanged;
        }
    }

    private static void VerifyMicrophoneTargetBlockedSlider(MainWindow window)
    {
        SavePreferences(window, window.CurrentPreferences with { GainControlMode = GainControlMode.Slider });
        Slider slider = FindById<Slider>(window, "Gain.Slider");
        SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
        double original = slider.Value;
        int selected = gain.SelectedIndex;
        long revision = gain.Revision;
        int changes = 0;
        RoutedPropertyChangedEventHandler<double> changed = (_, _) => changes++;
        slider.ValueChanged += changed;
        try
        {
            var provider = (IRangeValueProvider)GetAccessiblePeer(slider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(original == slider.Maximum ? original - 1 : original + 1);
            Pump();
            Check(changes == 0 && slider.Value == original && gain.SelectedIndex == selected
                && gain.Revision == revision && !gain.IsDirty && window.ResultText == Localization.Text("Target.SharedParameters"),
                "The native range provider rejects individual gain before a value transition when several microphones are active");
        }
        finally { slider.ValueChanged -= changed; }
        SavePreferences(window, window.CurrentPreferences with { GainControlMode = GainControlMode.Choice });
    }

    private static void VerifyMicrophoneTargetGroupOperations()
    {
        using var transport = new FakeTransport();
        WithMicrophoneTargetWindow(transport, "group-operations", window =>
        {
            TransmitterViewModel group = window.TransmitterTargets.Last();
            SelectMicrophoneTarget(window, group);
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            int writes = CountMockWrites(transport);
            int gainWrites = transport.Commands.Count(command => command == A1Command.SetGain);
            int requested = (gain.SelectedIndex + 1) % gain.Options.Count;
            gain.SelectedIndex = requested;
            RunTask(window.ApplySettingAsync(gain));
            Check(CountMockWrites(transport) == writes + 1
                && transport.Commands.Count(command => command == A1Command.SetGain) == gainWrites + 1
                && gain.SelectedIndex == requested && gain.ConfirmedValue == gain.Options[requested] && !gain.IsDirty,
                "All confirms shared gain with one receiver-wide command, not invented independent gain writes");
            Check(window.Transmitters.Any(target => target.IsMuted) && window.Transmitters.Any(target => !target.IsMuted),
                "The group-mute fixture starts with genuinely mixed transmitter states");
            foreach (bool desiredMute in new[] { true, false, true })
            {
                int changes = window.Transmitters.Count(target => target.IsMuted != desiredMute);
                writes = CountMockWrites(transport);
                int muteWrites = transport.Commands.Count(command => command == A1Command.SetMute);
                Check(group.ActionName == Localization.Text(desiredMute ? "Target.All.Mute" : "Target.All.Unmute"),
                    "All offers one uniform action for mixed, all-muted or all-active states");
                RunTask(window.ToggleAllMicrophonesMuteAsync());
                Check(window.Transmitters.All(target => target.IsMuted == desiredMute)
                    && CountMockWrites(transport) == writes + changes
                    && transport.Commands.Count(command => command == A1Command.SetMute) == muteWrites + changes
                    && window.ResultText == Localization.Text(desiredMute ? "Target.All.MutedConfirmed" : "Target.All.UnmutedConfirmed")
                    && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, group),
                    "Group mute uses one desired state, writes only differing indices and confirms every active target: " + desiredMute);
                VerifyMicrophoneTargetGroupSummary(window);
                VerifyMicrophoneTargetSelectedAction(window);
            }
        });
    }

    private static void VerifyMicrophoneTargetDraftIsolation()
    {
        foreach (bool automatic in new[] { false, true })
        foreach (bool sourceIsGroup in new[] { false, true })
        {
            using var transport = new FakeTransport();
            transport.SetTransmitterConnected(1, sourceIsGroup);
            WithMicrophoneTargetWindow(transport, "draft-" + automatic + "-" + sourceIsGroup, window =>
            {
                SavePreferences(window, window.CurrentPreferences with { AutoApplySelections = automatic });
                DispatcherTimer timer = MicrophoneTargetField<DispatcherTimer>(window, "autoApplyTimer");
                timer.Interval = TimeSpan.FromHours(1);
                HashSet<SettingRowViewModel> queue = MicrophoneTargetField<HashSet<SettingRowViewModel>>(window, "pendingAutomaticChanges");
                ListBox list = FindById<ListBox>(window, "TransmitterList");
                object source = list.SelectedItem;
                TransmitterViewModel destination = sourceIsGroup ? window.Transmitters[0] : window.TransmitterTargets.Last();
                SettingRowViewModel[] settings = AllSettings(window).ToArray();
                int[] originals = settings.Select(setting => setting.SelectedIndex).ToArray();
                SettingRowViewModel gain = settings.Single(setting => setting.Id == "Gain");
                SettingRowViewModel lights = settings.Single(setting => setting.Id == "Lights");
                int writes = CountMockWrites(transport);
                gain.SelectedIndex = (gain.SelectedIndex + 1) % gain.Options.Count;
                lights.SelectedIndex = (lights.SelectedIndex + 1) % lights.Options.Count;
                Check(gain.IsDirty && lights.IsDirty && CountMockWrites(transport) == writes
                    && (automatic ? queue.SetEquals(new[] { gain, lights }) && timer.IsEnabled : queue.Count == 0 && !timer.IsEnabled),
                    "Target-switch fixture holds real unapplied drafts and the expected automatic queue: " + automatic + "/" + sourceIsGroup);
                long context = MicrophoneTargetField<long>(window, "controlContextGeneration");
                list.SelectedItem = destination;
                Check(settings.All(setting => !setting.IsDirty) && settings.Select(setting => setting.SelectedIndex).SequenceEqual(originals)
                    && queue.Count == 0 && !timer.IsEnabled
                    && MicrophoneTargetField<long>(window, "controlContextGeneration") > context,
                    "Switching between individual and All invalidates the context and synchronously discards every draft, queue entry and timer");
                Pump();
                RunTask(window.FlushAutomaticChangesAsync());
                RunTask(Poll(window));
                list.SelectedItem = source;
                Pump();
                RunTask(window.FlushAutomaticChangesAsync());
                Check(CountMockWrites(transport) == writes && settings.All(setting => !setting.IsDirty)
                    && settings.Select(setting => setting.SelectedIndex).SequenceEqual(originals) && queue.Count == 0 && !timer.IsEnabled,
                    "Polling, flushing and returning to the old target never replay discarded manual or automatic drafts");
            });
        }
    }

    private static void VerifyMicrophoneTargetFreshScopeGuards()
    {
        using var transport = new FakeTransport();
        transport.SetTransmitterConnected(1, false);
        WithMicrophoneTargetWindow(transport, "fresh-scope", window =>
        {
            ListBox list = FindById<ListBox>(window, "TransmitterList");
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            int original = gain.SelectedIndex;
            int writes = CountMockWrites(transport);
            int reads = transport.Commands.Count(command => command == A1Command.GetStatus);
            gain.SelectedIndex = (original + 1) % gain.Options.Count;
            Check(gain.IsDirty && gain.CanApply, "A sole-active individual has a valid draft before fresh topology changes");
            transport.SetTransmitterConnected(1, true);
            RunTask(window.ApplySettingAsync(gain));
            Check(CountMockWrites(transport) == writes
                && transport.Commands.Count(command => command == A1Command.GetStatus) > reads
                && window.ResultText == Localization.Text("Target.SharedParameters")
                && window.Transmitters.All(target => target.IsConnected)
                && ReferenceEquals(list.SelectedItem, window.Transmitters[0])
                && !gain.IsDirty && gain.SelectedIndex == original,
                "Fresh Core preflight rejects an individual shared write after another transmitter connects, preserving target and confirmed value");

            SelectMicrophoneTarget(window, window.TransmitterTargets.Last());
            gain.SelectedIndex = (original + 1) % gain.Options.Count;
            Check(gain.IsDirty && gain.CanApply, "A valid All draft exists before group membership drops");
            transport.SetTransmitterConnected(1, false);
            RunTask(window.ApplySettingAsync(gain));
            Check(CountMockWrites(transport) == writes && window.ResultText == Localization.Text("Target.GroupUnavailable")
                && ReferenceEquals(list.SelectedItem, window.TransmitterTargets.Last())
                && !gain.IsDirty && gain.SelectedIndex == original && !window.Transmitters[1].IsConnected,
                "Fresh Core preflight rejects a shared group write after membership drops without changing the selected target");

            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            transport.SetTransmitterConnected(1, false);
            RunTask(window.ToggleAllMicrophonesMuteAsync());
            Check(CountMockWrites(transport) == writes && window.IsConnected
                && window.ResultText == Localization.Text("Target.GroupUnavailable") + " " + Localization.Text("Target.All.PartialFailure"),
                "Group mute checks fresh membership before its first indexed command and never claims full success on rejection");

            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            SelectMicrophoneTarget(window, window.Transmitters[1]);
            transport.SetTransmitterConnected(1, false);
            RunTask(window.ToggleMuteAsync(1));
            Check(CountMockWrites(transport) == writes && window.ResultText == Localization.Text("Target.SelectedUnavailable")
                && ReferenceEquals(list.SelectedItem, window.Transmitters[1]) && !window.Transmitters[1].IsConnected,
                "Indexed mute rejects a freshly disconnected target without retargeting the other active microphone");

            SelectMicrophoneTarget(window, window.Transmitters[0]);
            gain.SelectedIndex = (original + 1) % gain.Options.Count;
            Check(gain.IsDirty && gain.CanApply, "The remaining individual has a valid draft before all microphones disconnect");
            transport.SetTransmitterConnected(0, false);
            RunTask(window.ApplySettingAsync(gain));
            Check(CountMockWrites(transport) == writes && window.ResultText == Localization.Text("Main.Change.TransmitterRequired")
                && window.Transmitters.All(target => !target.IsConnected) && !gain.IsDirty && gain.SelectedIndex == original
                && ReferenceEquals(list.SelectedItem, window.Transmitters[0]),
                "Fresh zero-active preflight retains the existing transmitter-required message without claiming a sent write");

            transport.SetTransmitterConnected(0, true);
            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            SelectMicrophoneTarget(window, window.TransmitterTargets.Last());
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            RunTask(window.ToggleAllMicrophonesMuteAsync());
            Check(CountMockWrites(transport) == writes && window.IsConnected
                && window.ResultText == Localization.Text("Main.Change.TransmitterRequired") + " " + Localization.Text("Target.All.PartialFailure")
                && ReferenceEquals(list.SelectedItem, window.TransmitterTargets.Last()),
                "Fresh zero-active group preflight adds its group warning without falsely reporting an attempted write");
        });
    }

    private static void VerifyMicrophoneTargetPartialMuteFailure()
    {
        using var transport = new FakeTransport { RejectAfterWrites = 2 };
        WithMicrophoneTargetWindow(transport, "partial-mute", window =>
        {
            SelectMicrophoneTarget(window, window.Transmitters[1]);
            RunTask(window.ToggleMuteAsync(1));
            Check(window.Transmitters.All(target => !target.IsMuted), "Partial-failure fixture starts with both microphones unmuted");
            SelectMicrophoneTarget(window, window.TransmitterTargets.Last());
            var observed = new System.Collections.Concurrent.ConcurrentQueue<A1Status>();
            void ObserveStatus(byte[] report)
            {
                if (A1Protocol.TryDecode(report, out A1Frame? frame) && frame is not null && frame.Command == A1Command.GetStatus)
                    observed.Enqueue(A1Status.Parse(frame.Payload));
            }
            int writes = CountMockWrites(transport);
            transport.ReportReceived += ObserveStatus;
            try { RunTask(window.ToggleAllMicrophonesMuteAsync()); }
            finally { transport.ReportReceived -= ObserveStatus; }
            Check(observed.Any(status => status.Transmitters[0].Muted && !status.Transmitters[1].Muted)
                && CountMockWrites(transport) == writes + 2 && !window.IsConnected && transport.Disposed
                && window.ResultText.Contains(Localization.Text("Target.All.PartialFailure"), StringComparison.Ordinal)
                && !window.ResultText.Contains(Localization.Text("Target.All.MutedConfirmed"), StringComparison.Ordinal)
                && FindById<TextBlock>(window, "ResultReadout").IsVisible,
                "A second indexed-write rejection exposes partial failure after a confirmed first mute, never total success");
            writes = CountMockWrites(transport);
            RunTask(window.FlushAutomaticChangesAsync());
            RunTask(Poll(window));
            Check(CountMockWrites(transport) == writes,
                "A failed group sequence neither blindly rolls back nor replays writes after closing the unsafe session");
        });
    }

    private static void VerifyMicrophoneTargetMembershipLoss()
    {
        using var transport = new FakeTransport();
        bool armed = false;
        bool disconnected = false;
        void DisconnectAfterAcknowledgment(byte[] report)
        {
            if (armed && !disconnected && A1Protocol.TryDecode(report, out A1Frame? frame) && frame is not null
                && frame.Command == A1Command.SetMute && frame.Payload.Length == 1 && frame.Payload[0] == 0)
            {
                transport.SetTransmitterConnected(1, false);
                disconnected = true;
            }
        }
        transport.ReportReceived += DisconnectAfterAcknowledgment;
        try
        {
            WithMicrophoneTargetWindow(transport, "membership-loss", window =>
            {
                SelectMicrophoneTarget(window, window.Transmitters[1]);
                RunTask(window.ToggleMuteAsync(1));
                Check(window.Transmitters.All(target => !target.IsMuted), "Membership-loss fixture starts with both microphones unmuted");
                TransmitterViewModel group = window.TransmitterTargets.Last();
                SelectMicrophoneTarget(window, group);
                int writes = CountMockWrites(transport);
                armed = true;
                try { RunTask(window.ToggleAllMicrophonesMuteAsync()); }
                finally { armed = false; }
                Check(disconnected && CountMockWrites(transport) == writes + 1 && window.IsConnected && !transport.Disposed
                    && window.Transmitters[0].IsMuted && !window.Transmitters[1].IsConnected
                    && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, group)
                    && window.ResultText == Localization.Text("Target.GroupUnavailable") + " "
                        + Localization.Text("Target.WriteUnconfirmed") + " " + Localization.Text("Target.All.PartialFailure")
                    && FindById<TextBlock>(window, "ResultReadout").IsVisible,
                    "Membership loss during post-write readback stops the group and exposes both the sent-write uncertainty and partial-failure warning");
                transport.SetTransmitterConnected(1, true);
                RunTask(Poll(window));
                RunTask(window.FlushAutomaticChangesAsync());
                Check(CountMockWrites(transport) == writes + 1 && window.Transmitters[0].IsMuted && !window.Transmitters[1].IsMuted
                    && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, group),
                    "A returning member exposes the mixed result without rollback, replay or automatic target changes");
                VerifyMicrophoneTargetGroupSummary(window);
            });
        }
        finally { transport.ReportReceived -= DisconnectAfterAcknowledgment; }
    }

    private static void VerifyMicrophoneTargetMembershipDrafts()
    {
        foreach (bool automatic in new[] { false, true })
        foreach (bool startsWithGroup in new[] { false, true })
        {
            using var transport = new FakeTransport();
            transport.SetTransmitterConnected(1, startsWithGroup);
            WithMicrophoneTargetWindow(transport, "membership-drafts-" + automatic + "-" + startsWithGroup, window =>
            {
                SavePreferences(window, window.CurrentPreferences with { AutoApplySelections = automatic });
                DispatcherTimer timer = MicrophoneTargetField<DispatcherTimer>(window, "autoApplyTimer");
                timer.Interval = TimeSpan.FromHours(1);
                HashSet<SettingRowViewModel> queue = MicrophoneTargetField<HashSet<SettingRowViewModel>>(window, "pendingAutomaticChanges");
                ListBox list = FindById<ListBox>(window, "TransmitterList");
                object selected = list.SelectedItem;
                long context = MicrophoneTargetField<long>(window, "controlContextGeneration");
                SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
                int original = gain.SelectedIndex;
                int requested = (original + 1) % gain.Options.Count;
                int writes = CountMockWrites(transport);
                gain.SelectedIndex = requested;
                RunTask(Poll(window));
                Check(gain.IsDirty && gain.SelectedIndex == requested && ReferenceEquals(list.SelectedItem, selected)
                    && MicrophoneTargetField<long>(window, "controlContextGeneration") == context
                    && (automatic ? queue.SetEquals(new[] { gain }) && timer.IsEnabled : queue.Count == 0 && !timer.IsEnabled),
                    "Polling an unchanged connected-index set preserves the draft, selection and control context");
                transport.SetTransmitterConnected(1, !startsWithGroup);
                RunTask(Poll(window));
                Check(AllSettings(window).All(setting => !setting.IsDirty) && gain.SelectedIndex == original
                    && gain.ConfirmedValue == gain.Options[original] && queue.Count == 0 && !timer.IsEnabled
                    && ReferenceEquals(list.SelectedItem, selected)
                    && MicrophoneTargetField<long>(window, "controlContextGeneration") > context
                    && window.ResultText == Localization.Text("Target.DraftsDiscarded")
                    && FindById<TextBlock>(window, "ResultReadout").IsVisible,
                    "Membership addition or loss clears unsent drafts and the automatic queue while preserving the selected target");
                RunTask(window.FlushAutomaticChangesAsync());
                transport.SetTransmitterConnected(1, startsWithGroup);
                RunTask(Poll(window));
                RunTask(window.FlushAutomaticChangesAsync());
                Check(CountMockWrites(transport) == writes && !gain.IsDirty && gain.SelectedIndex == original
                    && queue.Count == 0 && !timer.IsEnabled && ReferenceEquals(list.SelectedItem, selected),
                    "Restoring membership does not resurrect an unsent manual or automatic draft");
            });
        }
    }

    private static void VerifyMicrophoneTargetHiddenSelection()
    {
        using var transport = new FakeTransport();
        WithMicrophoneTargetWindow(transport, "hidden-selection", window =>
        {
            ListBox list = FindById<ListBox>(window, "TransmitterList");
            TransmitterViewModel group = window.TransmitterTargets.Last();
            TransmitterViewModel hidden = window.Transmitters[0];
            SelectMicrophoneTarget(window, hidden);
            int writes = CountMockWrites(transport);
            SavePreferences(window, window.CurrentPreferences with { HiddenRows = ["TX1"] });
            Check(ReferenceEquals(list.SelectedItem, group) && !ReferenceEquals(list.SelectedItem, window.Transmitters[1])
                && hidden.RowVisibility == Visibility.Collapsed && window.Transmitters.Count == 2
                && window.TransmitterTargets.Count == 3 && ReferenceEquals(window.TransmitterTargets[0], hidden),
                "Hiding the selected physical row falls back to All without selecting a different microphone or removing physical slots");
            VerifyMicrophoneTargetSelectedAction(window);
            RunTask(Poll(window));
            SavePreferences(window, window.CurrentPreferences with { HiddenRows = [] });
            Check(ReferenceEquals(list.SelectedItem, group) && hidden.RowVisibility == Visibility.Visible
                && CountMockWrites(transport) == writes,
                "Polling and restoring a hidden physical row preserve All selection without any device writes");
        });
    }

    private static void VerifyFourMicrophoneTargetInterface()
    {
        byte[] status = CreateMicrophoneScopeStatus(4, [0, 1, 2, 3]);
        foreach (int offset in MicrophoneScopeMuteOffsets) status[offset] = 0;
        using var transport = new DeviceWriteSafetyTransport
        {
            Respond = (source, request) =>
            {
                switch (request.Command)
                {
                    case A1Command.GetStatus:
                        source.Deliver(request.Command, status);
                        return;
                    case A1Command.GetVersion:
                        source.Deliver(request.Command, [request.Payload[0], 1, 1, 2, 8]);
                        return;
                    case A1Command.SetMute:
                        if (request.Payload.Length != 2 || request.Payload[0] >= 4 || request.Payload[1] > 1)
                            throw new InvalidDataException("Invalid four-microphone UI mute request.");
                        status[MicrophoneScopeMuteOffsets[request.Payload[0]]] = request.Payload[1];
                        break;
                    case A1Command.SetGain:
                        if (request.Payload.Length != 1 || request.Payload[0] > 5)
                            throw new InvalidDataException("Invalid four-microphone UI gain request.");
                        status[8] = request.Payload[0];
                        break;
                    default:
                        throw new InvalidDataException("Unexpected four-microphone UI command: " + request.Command);
                }
                source.Deliver(request.Command, [0]);
            }
        };
        var receiver = new ReceiverInfo("Simulated four-microphone receiver", "simulated://microphone-target-four", 0, 64);
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory,
            "microphone-target-four-" + Localization.CurrentLanguage + "-" + Guid.NewGuid().ToString("N")));
        var preferences = new UiPreferences
        {
            Language = Localization.CurrentLanguage, FirstRunCompleted = true, HiddenRows = [],
            AutoApplySelections = false, GainControlMode = GainControlMode.Choice,
            ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false, ConfirmExit = false
        };
        Check(store.TrySave(preferences, out string? error), "Prepare isolated four-microphone UI preferences: " + error);
        var window = new MainWindow(() => [receiver], _ => transport, store.BaseDirectory,
            new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        try
        {
            MoveOffscreen(window);
            window.Show();
            PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
            RunTask(window.ConnectAsync());
            MicrophoneTargetField<DispatcherTimer>(window, "pollTimer").Stop();
            ListBox list = FindById<ListBox>(window, "TransmitterList");
            TransmitterViewModel group = window.TransmitterTargets.Last();
            Check(window.IsConnected && !window.IsBusy && window.Transmitters.Count == 4 && window.TransmitterTargets.Count == 5
                && ReferenceEquals(list.SelectedItem, group) && group.CanMute,
                "Four-TX status selects All after four physical slots and enables supported group mute");
            VerifyMicrophoneTargetNativeSurface(window, A1Status.Parse(status));
            using (var observer = new ExternalArrowObserver(window, "four-microphone-target-arrows-" + Localization.CurrentLanguage, includeFocus: true))
                VerifyListItemNavigation(FindById<ListBox>(window, "TransmitterList"), observer);

            SelectMicrophoneTarget(window, window.Transmitters[3]);
            foreach (bool desiredMute in new[] { true, false })
            {
                bool[] otherStates = A1Status.Parse(status).Transmitters.Take(3).Select(target => target.Muted).ToArray();
                int firstRequest = transport.Requests.Length;
                RunTask(window.ToggleMuteAsync(3));
                A1Frame[] writes = FourMicrophoneTargetWrites(transport, firstRequest);
                Check(writes.Length == 1 && writes[0].Command == A1Command.SetMute
                    && writes[0].Payload.SequenceEqual(new byte[] { 3, desiredMute ? (byte)1 : (byte)0 })
                    && A1Status.Parse(status).Transmitters.Take(3).Select(target => target.Muted).SequenceEqual(otherStates)
                    && window.Transmitters.Take(3).Select(target => target.IsMuted).SequenceEqual(otherStates)
                    && window.Transmitters[3].IsMuted == desiredMute && A1Status.Parse(status).Transmitters[3].Muted == desiredMute
                    && ReferenceEquals(list.SelectedItem, window.Transmitters[3]),
                    "TX4 mute and unmute use index three without changing TX1, TX2 or TX3");
                VerifyMicrophoneTargetSelectedAction(window);
            }

            SelectMicrophoneTarget(window, group);
            VerifyFourMicrophoneTargetGroupToggle(window, transport, status, true, [0, 1, 2, 3]);
            VerifyFourMicrophoneTargetGroupToggle(window, transport, status, false, [0, 1, 2, 3]);

            SetMicrophoneScopeConnections(status, [0, 1, 3]);
            RunTask(Poll(window));
            Check(window.Transmitters.Count == 4 && window.TransmitterTargets.Count == 5
                && window.Transmitters.Count(target => target.IsConnected) == 3 && !window.Transmitters[2].IsConnected
                && ReferenceEquals(list.SelectedItem, group) && group.CanMute,
                "A noncontiguous three-active group remains available without shrinking the four physical slots or changing selection");
            VerifyMicrophoneTargetNativeSurface(window, A1Status.Parse(status));
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            int requested = (gain.SelectedIndex + 1) % gain.Options.Count;
            int gainStart = transport.Requests.Length;
            gain.SelectedIndex = requested;
            RunTask(window.ApplySettingAsync(gain));
            A1Frame[] gainWrites = FourMicrophoneTargetWrites(transport, gainStart);
            Check(gainWrites.Length == 1 && gainWrites[0].Command == A1Command.SetGain
                && gainWrites[0].Payload.SequenceEqual(new[] { (byte)requested }) && A1Status.Parse(status).Gain == requested
                && gain.SelectedIndex == requested && gain.ConfirmedValue == gain.Options[requested] && !gain.IsDirty,
                "All with three active microphones confirms shared gain through one receiver-wide command");
            VerifyFourMicrophoneTargetGroupToggle(window, transport, status, true, [0, 1, 3]);
            VerifyFourMicrophoneTargetGroupToggle(window, transport, status, false, [0, 1, 3]);

            gain.SelectedIndex = (requested + 1) % gain.Options.Count;
            Check(gain.IsDirty && gain.CanApply, "Three-member group has a valid gain draft before an unpolled membership replacement");
            int staleGainStart = transport.Requests.Length;
            SetMicrophoneScopeConnections(status, [0, 2, 3]);
            RunTask(window.ApplySettingAsync(gain));
            Check(transport.Requests.Skip(staleGainStart).Any(request => request.Command == A1Command.GetStatus)
                && FourMicrophoneTargetWrites(transport, staleGainStart).Length == 0
                && window.ResultText == Localization.Text("Target.GroupChanged") && window.IsConnected
                && !window.Transmitters[1].IsConnected && window.Transmitters[2].IsConnected
                && !gain.IsDirty && gain.SelectedIndex == requested && A1Status.Parse(status).Gain == requested
                && ReferenceEquals(list.SelectedItem, group),
                "Fresh group gain preflight rejects a changed three-member set even though its active count is unchanged");

            int staleMuteStart = transport.Requests.Length;
            SetMicrophoneScopeConnections(status, [1, 2, 3]);
            RunTask(window.ToggleAllMicrophonesMuteAsync());
            Check(transport.Requests.Skip(staleMuteStart).Any(request => request.Command == A1Command.GetStatus)
                && FourMicrophoneTargetWrites(transport, staleMuteStart).Length == 0 && window.IsConnected
                && window.ResultText == Localization.Text("Target.GroupChanged") + " " + Localization.Text("Target.All.PartialFailure")
                && !window.Transmitters[0].IsConnected && window.Transmitters[1].IsConnected
                && A1Status.Parse(status).Transmitters.All(target => !target.Muted) && ReferenceEquals(list.SelectedItem, group),
                "Fresh group mute preflight rejects same-count membership drift before any indexed write, without claiming a sent-write failure");
            VerifyMicrophoneTargetGroupSummary(window);
            VerifyMicrophoneTargetSelectedAction(window);
            int completeStatusEnd = transport.Requests.Length;
            status = status[..17];
            RunTask(Poll(window));
            Check(!window.IsConnected && transport.Disposed && FourMicrophoneTargetWrites(transport, completeStatusEnd).Length == 0
                && window.ResultText == Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.InvalidResponse")),
                "A short poll after four-TX confirmation invalidates the session instead of silently dropping two microphones");
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void VerifyFourMicrophoneTargetIncompleteConnection()
    {
        using var transport = new FakeTransport { VersionBytes = [1, 1, 2, 8] };
        MainWindow window = CreatePresentationWindow(transport, "four-short-initial-" + Localization.CurrentLanguage,
            new MemoryDesktop());
        try
        {
            RunTask(window.ConnectAsync());
            Check(!window.IsConnected && transport.Disposed && CountMockWrites(transport) == 0
                && window.ResultText == Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.InvalidResponse")),
                "Four-TX firmware with an initially incomplete snapshot never exposes a connected two-TX control target");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.GetVersion, A1Command.GetStatus }),
                "Initial four-TX identity requests complete telemetry before transmitter versions or settings");
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void VerifyFourMicrophoneTargetGroupToggle(MainWindow window, DeviceWriteSafetyTransport transport,
        byte[] status, bool desiredMute, int[] expectedIndices)
    {
        A1Status before = A1Status.Parse(status);
        Check(GetMicrophoneScopeIndices(before).SequenceEqual(expectedIndices)
            && expectedIndices.All(index => before.Transmitters[index].Muted != desiredMute),
            "Four-slot group fixture requires one indexed change per active member: " + string.Join(',', expectedIndices));
        int firstRequest = transport.Requests.Length;
        RunTask(window.ToggleAllMicrophonesMuteAsync());
        A1Frame[] writes = FourMicrophoneTargetWrites(transport, firstRequest);
        A1Status after = A1Status.Parse(status);
        Check(writes.Length == expectedIndices.Length
            && writes.All(request => request.Command == A1Command.SetMute && request.Payload.Length == 2
                && request.Payload[1] == (desiredMute ? (byte)1 : (byte)0))
            && writes.Select(request => (int)request.Payload[0]).SequenceEqual(expectedIndices)
            && expectedIndices.All(index => after.Transmitters[index].Muted == desiredMute && window.Transmitters[index].IsMuted == desiredMute)
            && Enumerable.Range(0, 4).Where(index => !expectedIndices.Contains(index))
                .All(index => before.Transmitters[index].Muted == after.Transmitters[index].Muted)
            && window.ResultText == Localization.Text(desiredMute ? "Target.All.MutedConfirmed" : "Target.All.UnmutedConfirmed")
            && ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, window.TransmitterTargets.Last()),
            "Group mute or unmute visits every active index exactly once and leaves disconnected slots untouched: "
                + string.Join(',', expectedIndices) + "/" + desiredMute);
        VerifyMicrophoneTargetGroupSummary(window);
        VerifyMicrophoneTargetSelectedAction(window);
    }

    private static A1Frame[] FourMicrophoneTargetWrites(DeviceWriteSafetyTransport transport, int firstRequest) =>
        transport.Requests.Skip(firstRequest).Where(request => request.Command is not A1Command.GetStatus and not A1Command.GetVersion).ToArray();

    private static TValue MicrophoneTargetField<TValue>(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is TValue value
            ? value : throw new InvalidOperationException("Microphone-target fixture field is missing or has an unexpected type: " + name);
}
