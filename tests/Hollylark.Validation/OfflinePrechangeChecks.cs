using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static readonly Lazy<KeyboardDevice> PrechangeKeyboard = new(() => new UnmodifiedPrechangeKeyboardDevice());

    private sealed class UnmodifiedPrechangeKeyboardDevice : KeyboardDevice
    {
        internal UnmodifiedPrechangeKeyboardDevice() : base(InputManager.Current) { }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private static void VerifyGuardedSliderInitialization()
    {
        foreach (int gainIndex in Enumerable.Range(-1, 7))
        {
            var setting = new SettingRowViewModel("Gain", "Gain", ["1", "2", "3", "4", "5", "6"],
                SettingChange.Gain, status => status.Gain, _ => null);
            byte[] payload = (byte[])ValidStatus.Clone();
            payload[0] = payload[1] = 0;
            payload[8] = (byte)Math.Max(0, gainIndex);
            setting.Update(gainIndex < 0 ? null : A1Status.Parse(payload), false);
            int editRequests = 0;
            setting.EditRequested = () => { editRequests++; return false; };
            var slider = new GuardedSettingSlider();
            slider.BeginInit();
            slider.DataContext = setting;
            slider.Minimum = 1;
            slider.Maximum = 6;
            slider.SetBinding(Slider.ValueProperty, new Binding(nameof(SettingRowViewModel.SelectedLevel))
            {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            slider.EndInit();
            Pump();
            double original = setting.SelectedLevel;
            Check(slider.IsInitialized && slider.Value == original && editRequests == 0 && !setting.IsDirty,
                "Default/range initialization is not an offline edit request: " + gainIndex);
            double alternative = original == 6 ? 5 : original + 1;
            slider.SetCurrentValue(Slider.ValueProperty, alternative);
            Pump();
            Check(editRequests > 0 && slider.Value == original && setting.SelectedIndex == gainIndex && !setting.IsDirty,
                "After initialization an offline value attempt is still rejected: " + gainIndex);
            var provider = (IRangeValueProvider)GetAccessiblePeer(slider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(alternative);
            Check(slider.Value == original && !setting.IsDirty,
                "Initialization exemption never bypasses the offline range provider: " + gainIndex);
        }
    }

    private static void VerifyOfflinePrechangeChecks(MainWindow window, FakeTransport transport)
    {
        UiPreferences preferences = window.CurrentPreferences;
        bool[] connections = window.Transmitters.Select(transmitter => transmitter.IsConnected).ToArray();
        var tabs = (TabControl)window.FindName("MainTabs");
        int selectedTab = tabs.SelectedIndex;
        int writes = CountMockWrites(transport);
        var observations = new List<string>();
        try
        {
            SavePreferences(window, preferences with
            {
                AutoApplySelections = false, GainControlMode = GainControlMode.Choice, HiddenRows = [],
                ShowHints = false, ShowOperationMessages = false
            });
            tabs.SelectedIndex = 0;
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            RunTask(window.RefreshAsync());
            Pump();
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            VerifyUnmaterializedChoiceGuard(gain, observations);
            using (var observer = new ExternalArrowObserver(window, "offline-prechange-" + Localization.CurrentLanguage))
            {
                foreach (SettingRowViewModel setting in AllSettings(window))
                {
                    var choice = FindById<GuardedSettingComboBox>(window, setting.ChoiceId);
                    Check(choice.IsVisible, "Main choice is visible during native UIA validation: " + setting.Id);
                    VerifyBlockedChoice(window, choice, setting, observations, observer);
                }
                SavePreferences(window, window.CurrentPreferences with { GainControlMode = GainControlMode.Slider });
                var slider = FindById<GuardedSettingSlider>(window, "Gain.Slider");
                VerifyBlockedSlider(window, slider, gain, observations, observer);
                VerifyOfflineAuthoritativeUpdate(window, slider, gain);
                RunTask(window.RefreshAsync());
                transport.SetTransmitterConnected(0, true);
                transport.SetTransmitterConnected(1, true);
                RunTask(Poll(window));
                VerifyGuardedControlsOnline(window, slider, gain, observer, observations);
            }
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, true);
            RunTask(window.RefreshAsync());
            SettingRowViewModel identification = window.DeviceSettings.Single(setting => setting.Id == "Identification");
            Check(identification.UnavailableReason is not null, "One transmitter leaves identification unavailable in two-TX mode");
            VerifyBlockedChoice(window, FindById<GuardedSettingComboBox>(window, identification.ChoiceId),
                identification, observations, null);
            byte[] mutedStatus = (byte[])ValidStatus.Clone();
            mutedStatus[4] = 1;
            mutedStatus[5] = 1;
            SettingRowViewModel noise = window.AudioSettings.Single(setting => setting.Id == "Noise");
            noise.Update(A1Status.Parse(mutedStatus), false);
            Pump();
            Check(noise.UnavailableReason is not null, "Muted connected transmitters leave noise reduction unavailable");
            VerifyBlockedChoice(window, FindById<GuardedSettingComboBox>(window, noise.ChoiceId), noise, observations, null);
            Check(CountMockWrites(transport) == writes, "Pre-change validation makes no receiver setting writes, including simulated writes");
        }
        finally
        {
            for (int index = 0; index < Math.Min(connections.Length, 2); index++)
                transport.SetTransmitterConnected(index, connections[index]);
            RunTask(window.RefreshAsync());
            SavePreferences(window, preferences);
            tabs.SelectedIndex = selectedTab;
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllLines(Path.Combine(ReportDirectory, "offline-prechange-" + Localization.CurrentLanguage + "-transitions.txt"), observations);
        }
    }

    private static void VerifyBlockedChoice(MainWindow window, GuardedSettingComboBox choice, SettingRowViewModel setting,
        List<string> observations, ExternalArrowObserver? observer)
    {
        using var lifetime = new ControlLifetimeProbe(window, choice, observations);
        Check(choice.IsVisible, "Native choice pattern checks require a visible combo: " + setting.Id);
        Check(choice.IsEnabled && choice.Focusable && choice.IsTabStop, "An unavailable choice remains enabled and focusable: " + setting.Id);
        AutomationPeer peer = GetAccessiblePeer(choice);
        Check(peer is ComboBoxAutomationPeer && peer.GetAutomationControlType() == AutomationControlType.ComboBox
            && peer.GetPattern(PatternInterface.Value) is null, "Guarded choice retains the native non-editable ComboBox contract: " + setting.Id);
        Check(choice.Items.OfType<GuardedSettingComboBoxItem>().Count() == choice.Items.Count
            && choice.Items.OfType<GuardedSettingComboBoxItem>().All(item => ReferenceEquals(item.SettingOwner, choice)),
            "Choice ownership guards every item before popup/container generation: " + setting.Id);
        bool wasOpen = choice.IsDropDownOpen;
        if (choice.IsVisible) choice.IsDropDownOpen = true;
        Pump();
        choice.UpdateLayout();
        int original = choice.SelectedIndex;
        Check(original >= 0 && original < choice.Items.Count, "Offline fixture has a confirmed choice: " + setting.Id);
        int alternative = (original + 1) % choice.Items.Count;
        long revision = setting.Revision;
        bool dirty = setting.IsDirty;
        var transitions = new List<string>();
        SelectionChangedEventHandler selectionChanged = (_, _) => transitions.Add("selection: " + DescribePrechangeChoice(choice, setting));
        PropertyChangedEventHandler modelChanged = (_, arguments) =>
        {
            if (arguments.PropertyName is nameof(SettingRowViewModel.SelectedIndex) or nameof(SettingRowViewModel.IsDirty))
                transitions.Add("model=" + arguments.PropertyName + ": " + DescribePrechangeChoice(choice, setting));
        };
        var itemObservations = new List<(DependencyPropertyDescriptor Property, ComboBoxItem Item, EventHandler Handler)>();
        foreach (ComboBoxItem item in choice.Items.OfType<ComboBoxItem>())
        {
            DependencyPropertyDescriptor property = DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsSelectedProperty, item.GetType())!;
            EventHandler handler = (_, _) => transitions.Add("item=" + item.Tag + ":" + item.IsSelected + "; " + DescribePrechangeChoice(choice, setting));
            property.AddValueChanged(item, handler);
            itemObservations.Add((property, item, handler));
        }
        choice.SelectionChanged += selectionChanged;
        setting.PropertyChanged += modelChanged;
        int marker = 0;
        try
        {
            if (observer is not null)
            {
                observer.Snapshot(setting.ChoiceId);
                DrainPrechangeEvents();
                lifetime.VerifyAttached("initial-snapshot");
                marker = observer.Mark();
            }
            void Attempt(string name, Action action)
            {
                string before = DescribePrechangeChoice(choice, setting);
                action();
                string after = DescribePrechangeChoice(choice, setting);
                string diagnostic = setting.Id + "/" + name + "; before={" + before + "}; after={" + after
                    + "}; events=[" + string.Join(" | ", transitions) + "]";
                observations.Add(diagnostic);
                bool unchanged = choice.SelectedIndex == original && setting.SelectedIndex == original
                    && setting.Revision == revision && setting.IsDirty == dirty && transitions.Count == 0;
                Check(unchanged, "Choice refuses before any selection/item/model event: "
                    + (unchanged ? setting.Id + "/" + name : diagnostic));
                Pump();
                bool stillUnchanged = choice.SelectedIndex == original && transitions.Count == 0;
                Check(stillUnchanged,
                    "Rejected choice is not replayed by a dispatcher callback: " + setting.Id + "/" + name
                    + (stillUnchanged ? string.Empty : "; " + DescribePrechangeChoice(choice, setting)
                        + "; events=[" + string.Join(" | ", transitions) + "]"));
                observations.Add(setting.Id + "/" + name + ": no transition");
            }
            Attempt("index", () => choice.SetCurrentValue(ComboBox.SelectedIndexProperty, alternative));
            Attempt("item", () => choice.SetCurrentValue(ComboBox.SelectedItemProperty, choice.Items[alternative]));
            Attempt("value", () => choice.SetCurrentValue(ComboBox.SelectedValueProperty, choice.Items[alternative]));
            Attempt("model", () => setting.SelectedIndex = alternative);
            Attempt("native-container-select", () => ((ComboBoxItem)choice.Items[alternative]).IsSelected = true);
            Attempt("native-container-unselect", () => ((ComboBoxItem)choice.Items[original]).IsSelected = false);
            var itemPeers = (peer.GetChildren() ?? []).OfType<SelectorItemAutomationPeer>().ToArray();
            Check(itemPeers.Length == choice.Items.Count, "Every guarded choice exposes native selection-item providers: " + setting.Id);
            var alternativeProvider = (ISelectionItemProvider)itemPeers[alternative].GetPattern(PatternInterface.SelectionItem);
            var currentProvider = (ISelectionItemProvider)itemPeers[original].GetPattern(PatternInterface.SelectionItem);
            Attempt("uia-select", alternativeProvider.Select);
            Attempt("uia-add", alternativeProvider.AddToSelection);
            Attempt("uia-remove", currentProvider.RemoveFromSelection);
            foreach (int itemIndex in new[] { original, alternative })
            {
                AutomationPeer wrapper = GetAccessiblePeer((ComboBoxItem)choice.Items[itemIndex]);
                Check(wrapper.GetAutomationControlType() == AutomationControlType.ListItem,
                    "Native visual choice peer retains the list-item role: " + setting.Id + "/" + itemIndex);
                foreach (AutomationPeer exposedPeer in new[] { wrapper, wrapper.EventsSource }.OfType<AutomationPeer>().Distinct())
                {
                    if (exposedPeer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider exposedProvider) continue;
                    Attempt("uia-visual-peer-" + itemIndex, itemIndex == original
                        ? exposedProvider.RemoveFromSelection : exposedProvider.Select);
                }
            }
            var selectionProvider = (ISelectionProvider)peer.GetPattern(PatternInterface.Selection);
            foreach (IRawElementProviderSimple selected in selectionProvider.GetSelection())
            {
                var selectedProvider = (ISelectionItemProvider)selected.GetPatternProvider(SelectionItemPattern.Pattern.Id);
                Attempt("uia-returned-selected-item", selectedProvider.RemoveFromSelection);
            }
            foreach (AutomationPeer itemPeer in itemPeers)
                Check(itemPeer.GetAutomationControlType() == AutomationControlType.ListItem,
                    "A guarded choice item retains the native list-item role: " + setting.Id);
            if (choice.IsVisible)
            {
                choice.IsDropDownOpen = false;
                FocusForKeyboardCheck(choice);
                foreach (Key key in new[] { Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Space, Key.F4 })
                    Attempt("preview-key-" + key, () => RaisePrechangeKey(choice, key));
                Attempt("bubble-key", () => RaiseLocalKey(choice, Key.Down));
                Attempt("typeahead", () => RaisePrechangeText(choice, NativeListCaption(choice.Items[alternative])));
                Attempt("mouse", () => RaisePrechangeMouse(choice));
                Attempt("wheel", () => RaisePrechangeWheel(choice));
                choice.IsDropDownOpen = true;
                Pump();
                Attempt("popup-item-mouse", () => RaisePrechangeMouse((ComboBoxItem)choice.Items[alternative]));
            }
            Check(currentProvider.IsSelected && !alternativeProvider.IsSelected,
                "Offline UIA providers keep the original selection throughout all requests: " + setting.Id);
            if (observer is not null)
            {
                ArrowAutomationMessage snapshot = observer.Snapshot(setting.ChoiceId);
                DrainPrechangeEvents();
                lifetime.VerifyAttached("final-snapshot");
                string expected = NativeListCaption(choice.Items[original]);
                Check(snapshot.Selection.SequenceEqual(new[] { expected }), "External UIA retains the authoritative choice: " + setting.Id);
                Check(!observer.Since(marker).Any(message => message.Kind == "selection" && message.Name != expected
                    || message.Kind == "property"
                    && ((message.AutomationId == setting.ChoiceId && message.Detail.StartsWith("ValuePatternIdentifiers.ValueProperty=", StringComparison.Ordinal)
                        && message.Value != expected) || message.Detail.StartsWith("SelectionItemPatternIdentifiers.IsSelectedProperty=", StringComparison.Ordinal)
                        && message.Value != (message.Name == expected ? bool.TrueString : bool.FalseString))),
                    "External UIA receives no transient wrong choice or selected-item state: " + setting.Id);
            }
            Check(window.ResultText == setting.UnavailableReason, "Blocked choice reports its actual important refusal reason: " + setting.Id);
            Check(BindingOperations.IsDataBound(choice, ComboBox.SelectedIndexProperty),
                "Rejected native/UIA choices preserve the two-way model binding: " + setting.Id);
        }
        finally
        {
            choice.SelectionChanged -= selectionChanged;
            setting.PropertyChanged -= modelChanged;
            foreach (var observation in itemObservations) observation.Property.RemoveValueChanged(observation.Item, observation.Handler);
            observations.AddRange(transitions.Select(transition => setting.Id + "/unexpected: " + transition));
            choice.IsDropDownOpen = wasOpen;
            Pump();
        }
    }

    private static void VerifyUnmaterializedChoiceGuard(SettingRowViewModel setting, List<string> observations)
    {
        var choice = new GuardedSettingComboBox
        {
            DataContext = setting,
            Visibility = Visibility.Collapsed,
            ItemsSource = (System.Collections.IEnumerable)new ChoiceItemsConverter().Convert(setting.Options,
                typeof(System.Collections.IEnumerable), string.Empty, CultureInfo.InvariantCulture)
        };
        choice.SetBinding(ComboBox.SelectedIndexProperty, new Binding(nameof(SettingRowViewModel.SelectedIndex))
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        Pump();
        int original = setting.SelectedIndex;
        int alternative = (original + 1) % choice.Items.Count;
        long revision = setting.Revision;
        bool dirty = setting.IsDirty;
        Check(!choice.IsVisible && !choice.IsLoaded && choice.Items.OfType<GuardedSettingComboBoxItem>()
            .All(item => ReferenceEquals(item.SettingOwner, choice) && choice.ItemContainerGenerator.IndexFromContainer(item) < 0),
            "An unmaterialized collapsed combo assigns guard ownership before any container preparation");
        var transitions = new List<string>();
        SelectionChangedEventHandler selectionChanged = (_, _) => transitions.Add("selection=" + choice.SelectedIndex);
        var itemObservations = new List<(DependencyPropertyDescriptor Property, ComboBoxItem Item, EventHandler Handler)>();
        foreach (ComboBoxItem item in choice.Items.OfType<ComboBoxItem>())
        {
            DependencyPropertyDescriptor property = DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsSelectedProperty, item.GetType())!;
            EventHandler handler = (_, _) => transitions.Add("item=" + item.Tag + ":" + item.IsSelected);
            property.AddValueChanged(item, handler);
            itemObservations.Add((property, item, handler));
        }
        choice.SelectionChanged += selectionChanged;
        try
        {
            (string Name, Action Apply)[] attempts =
            [
                ("index", () => choice.SetCurrentValue(ComboBox.SelectedIndexProperty, alternative)),
                ("item", () => choice.SetCurrentValue(ComboBox.SelectedItemProperty, choice.Items[alternative])),
                ("value", () => choice.SetCurrentValue(ComboBox.SelectedValueProperty, choice.Items[alternative])),
                ("native-container-select", () => ((ComboBoxItem)choice.Items[alternative]).IsSelected = true),
                ("native-container-unselect", () => ((ComboBoxItem)choice.Items[original]).IsSelected = false)
            ];
            foreach (var attempt in attempts)
            {
                attempt.Apply();
                foreach (string phase in new[] { "synchronous", "dispatcher" })
                {
                    if (phase == "dispatcher") Pump();
                    bool unchanged = choice.SelectedIndex == original && setting.SelectedIndex == original
                        && setting.Revision == revision && setting.IsDirty == dirty && transitions.Count == 0;
                    string diagnostic = "unmaterialized/" + attempt.Name + "/" + phase + "; "
                        + DescribePrechangeChoice(choice, setting) + "; events=[" + string.Join(" | ", transitions) + "]";
                    observations.Add(diagnostic);
                    Check(unchanged, "Unmaterialized choice refuses without transient state changes: "
                        + (unchanged ? attempt.Name + "/" + phase : diagnostic));
                }
            }
        }
        finally
        {
            choice.SelectionChanged -= selectionChanged;
            foreach (var observation in itemObservations) observation.Property.RemoveValueChanged(observation.Item, observation.Handler);
            BindingOperations.ClearBinding(choice, ComboBox.SelectedIndexProperty);
        }
    }

    private static void VerifyBlockedSlider(MainWindow window, GuardedSettingSlider slider, SettingRowViewModel setting,
        List<string> observations, ExternalArrowObserver observer)
    {
        Check(slider.IsVisible && slider.IsEnabled && slider.Focusable && slider.IsTabStop,
            "An offline slider remains visible, enabled and focusable");
        AutomationPeer peer = GetAccessiblePeer(slider);
        Check(peer is SliderAutomationPeer && peer.GetAutomationControlType() == AutomationControlType.Slider,
            "Guarded gain retains the native slider role");
        var provider = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
        double original = slider.Value;
        double alternative = original == slider.Minimum ? original + 1 : original - 1;
        long revision = setting.Revision;
        var transitions = new List<double>();
        RoutedPropertyChangedEventHandler<double> changed = (_, arguments) => transitions.Add(arguments.NewValue);
        slider.ValueChanged += changed;
        try
        {
            observer.Snapshot("Gain.Slider");
            DrainPrechangeEvents();
            int marker = observer.Mark();
            void Attempt(string name, Action action)
            {
                action();
                Check(slider.Value == original && setting.SelectedLevel == original && setting.Revision == revision
                    && !setting.IsDirty && transitions.Count == 0, "Slider rejects before any ValueChanged or model mutation: " + name);
                Pump();
                Check(slider.Value == original && transitions.Count == 0, "Rejected slider input is not replayed: " + name);
                observations.Add("Gain.Slider/" + name + ": no transition");
            }
            Attempt("dependency-property", () => slider.SetCurrentValue(Slider.ValueProperty, alternative));
            Attempt("model", () => setting.SelectedLevel = alternative);
            Attempt("range-provider", () => provider.SetValue(alternative));
            FocusForKeyboardCheck(slider);
            foreach (Key key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown })
                Attempt("preview-key-" + key, () => RaisePrechangeKey(slider, key));
            Attempt("bubble-key", () => RaiseLocalKey(slider, Key.Right));
            Attempt("mouse", () => RaisePrechangeMouse(slider));
            Attempt("wheel", () => RaisePrechangeWheel(slider));
            Attempt("small-command", () => Slider.IncreaseSmall.Execute(null, slider));
            Attempt("large-command", () => Slider.DecreaseLarge.Execute(null, slider));
            ArrowAutomationMessage snapshot = observer.Snapshot("Gain.Slider");
            DrainPrechangeEvents();
            string expected = original.ToString(CultureInfo.InvariantCulture);
            Check(snapshot.Value == expected && !observer.Since(marker).Any(message => message.Kind == "property"
                && message.AutomationId == "Gain.Slider"
                && message.Detail.StartsWith("RangeValuePatternIdentifiers.ValueProperty=", StringComparison.Ordinal)
                && message.Value != expected), "External UIA receives no transient wrong gain event");
            Check(BindingOperations.IsDataBound(slider, Slider.ValueProperty), "Blocked RangeValue requests do not detach the value binding");
            Check(window.ResultText == Localization.Text("Main.Change.TransmitterRequired"), "Offline slider reports the actionable microphone requirement");
        }
        finally
        {
            slider.ValueChanged -= changed;
            observations.AddRange(transitions.Select(value => "Gain.Slider/unexpected: " + value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string DescribePrechangeChoice(GuardedSettingComboBox choice, SettingRowViewModel setting)
    {
        string items = string.Join(", ", choice.Items.OfType<ComboBoxItem>().Select(item =>
            "tag=" + item.Tag + "/selected=" + item.IsSelected + "/type=" + item.GetType().Name
            + "/guardOwner=" + (item is GuardedSettingComboBoxItem guarded && ReferenceEquals(guarded.SettingOwner, choice))
            + "/generated=" + choice.ItemContainerGenerator.IndexFromContainer(item)
            + "/logicalParent=" + (LogicalTreeHelper.GetParent(item)?.GetType().Name ?? "none")));
        return "index=" + choice.SelectedIndex + "; selectedItem=" + (choice.SelectedItem as ComboBoxItem)?.Tag
            + "; selectedValue=" + (choice.SelectedValue as ComboBoxItem)?.Tag + "; model=" + setting.SelectedIndex
            + "; revision=" + setting.Revision + "; dirty=" + setting.IsDirty + "; visible=" + choice.IsVisible
            + "; available=" + setting.IsEnabled + "; refusal=" + setting.UnavailableReason + "; items=[" + items + "]";
    }

    private static void VerifyOfflineAuthoritativeUpdate(MainWindow window, GuardedSettingSlider slider, SettingRowViewModel gain)
    {
        byte[] payload = (byte[])ValidStatus.Clone();
        payload[0] = 0;
        payload[1] = 0;
        payload[8] = (byte)((gain.SelectedIndex + 1) % gain.Options.Count);
        gain.Update(A1Status.Parse(payload), false);
        Pump();
        Check(slider.Value == payload[8] + 1 && !gain.IsDirty, "Confirmed status updates bypass offline edit guards without dirtying the model");
        var choice = FindById<GuardedSettingComboBox>(window, gain.ChoiceId);
        Check(choice.SelectedIndex == payload[8] && ((ComboBoxItem)choice.Items[payload[8]]).IsSelected,
            "Confirmed offline status synchronizes both the combo and its selected container");
    }

    private static void VerifyGuardedControlsOnline(MainWindow window, GuardedSettingSlider slider, SettingRowViewModel gain,
        ExternalArrowObserver observer, List<string> observations)
    {
        using var lifetime = new ControlLifetimeProbe(window, slider, observations);
        lifetime.VerifyAttached("online-start");
        Check(!gain.IsDirty, "Reconnection does not replay any rejected gain input");
        var choice = FindById<GuardedSettingComboBox>(window, "Lights.Choice");
        SettingRowViewModel lights = window.DeviceSettings.Single(setting => setting.Id == "Lights");
        Check(!lights.IsDirty, "Reconnection does not replay any rejected combo input");
        int original = choice.SelectedIndex;
        int alternative = (original + 1) % choice.Items.Count;
        choice.IsDropDownOpen = false;
        FocusForKeyboardCheck(choice);
        RaiseLocalKey(choice, alternative > original ? Key.Down : Key.Up);
        Check(choice.SelectedIndex == alternative && lights.SelectedIndex == alternative && lights.IsDirty,
            "Online guarded combo retains native arrow selection and model propagation");
        var currentProvider = (ISelectionItemProvider)GetAccessiblePeer(choice).GetChildren()
            .OfType<SelectorItemAutomationPeer>().ElementAt(original).GetPattern(PatternInterface.SelectionItem);
        currentProvider.Select();
        Pump();
        Check(choice.SelectedIndex == original && lights.SelectedIndex == original,
            "Online selection-item provider changes the actual native control and model");
        slider.BringIntoView();
        window.UpdateLayout();
        FocusForKeyboardCheck(slider);
        DrainPrechangeEvents();
        double previous = slider.Value;
        double changed = previous == slider.Minimum ? previous + 1 : previous - 1;
        var provider = (IRangeValueProvider)GetAccessiblePeer(slider).GetPattern(PatternInterface.RangeValue);
        ArrowAutomationMessage initialSnapshot = observer.Snapshot("Gain.Slider");
        DrainPrechangeEvents();
        lifetime.VerifyAttached("online-initial-snapshot");
        Check(initialSnapshot.Value == previous.ToString(CultureInfo.InvariantCulture),
            "External UIA observes the stable online gain before the test change");
        int marker = observer.Mark();
        var nativeChanges = new List<string>();
        RoutedPropertyChangedEventHandler<double> nativeChanged = (_, arguments) => nativeChanges.Add(
            arguments.OldValue + "->" + arguments.NewValue + "; effective=" + slider.Value + "; model=" + gain.SelectedLevel);
        slider.ValueChanged += nativeChanged;
        provider.SetValue(changed);
        Pump();
        slider.ValueChanged -= nativeChanged;
        Check(slider.Value == changed && gain.SelectedLevel == changed && gain.IsDirty,
            "Online RangeValue provider updates gain while preserving the model binding");
        ArrowAutomationMessage changedSnapshot = observer.Snapshot("Gain.Slider");
        DrainPrechangeEvents();
        lifetime.VerifyAttached("online-changed-snapshot");
        string expected = changed.ToString(CultureInfo.InvariantCulture);
        Check(changedSnapshot.Value == expected, "External UIA observes the changed online gain before arrow reversal");
        bool ReceivedChangedValue() => observer.Since(marker).Any(message => message.Kind == "property"
            && message.AutomationId == "Gain.Slider" && message.Value == expected
            && message.Detail.StartsWith("RangeValuePatternIdentifiers.ValueProperty=", StringComparison.Ordinal));
        bool changedEventReceived = observer.WaitForEvent(ReceivedChangedValue, TimeSpan.FromSeconds(2), "online gain RangeValue change");
        Check(changedEventReceived && ReceivedChangedValue(), "Event observer detects the actual changed online gain before reversal and is not vacuously silent; native=["
            + string.Join(" | ", nativeChanges) + "]; parent=" + GetAccessiblePeer(slider).GetParent()?.GetType().Name + "; expected="
            + expected + "; received=[" + string.Join(" | ", observer.Since(marker)
                .Where(message => message.AutomationId == "Gain.Slider")
                .Select(message => message.Kind + ":" + message.Value + ":" + message.Detail)) + "]");
        FocusForKeyboardCheck(slider);
        RaiseLocalKey(slider, changed > previous ? Key.Left : Key.Right);
        Check(slider.Value == previous && gain.SelectedLevel == previous, "Online slider retains native one-step arrow behavior");
        ArrowAutomationMessage restoredSnapshot = observer.Snapshot("Gain.Slider");
        DrainPrechangeEvents();
        Check(restoredSnapshot.Value == previous.ToString(CultureInfo.InvariantCulture),
            "External UIA observes the value restored by the native arrow action");
        Check(BindingOperations.IsDataBound(slider, Slider.ValueProperty)
            && BindingOperations.IsDataBound(choice, ComboBox.SelectedIndexProperty), "Online automation preserves both model bindings");
    }

    private static void RaisePrechangeKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)!;
        ModifierKeys realModifiers = Keyboard.PrimaryDevice.Modifiers;
        var preview = new KeyEventArgs(PrechangeKeyboard.Value, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        Check(preview.Handled && preview.KeyboardDevice.Modifiers == ModifierKeys.None,
            "Unavailable native control handles the changing key before its default action: " + key
            + "; synthetic-modifiers=" + preview.KeyboardDevice.Modifiers + "; real-modifiers=" + realModifiers);
    }

    private static void RaisePrechangeText(UIElement target, string text)
    {
        var composition = new TextComposition(InputManager.Current, target, text);
        var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        target.RaiseEvent(preview);
        Check(preview.Handled, "Unavailable choice intercepts typeahead before text search");
    }

    private static void RaisePrechangeMouse(UIElement target)
    {
        foreach (RoutedEvent mouseEvent in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
        {
            var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = mouseEvent };
            target.RaiseEvent(preview);
            if (mouseEvent == Mouse.PreviewMouseDownEvent || target is ComboBoxItem or ComboBox)
                Check(preview.Handled, "Unavailable control intercepts the changing mouse event: " + mouseEvent.Name);
        }
    }

    private static void RaisePrechangeWheel(UIElement target)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(preview);
        Check(preview.Handled, "Unavailable native control intercepts the mouse wheel");
    }

    private static void DrainPrechangeEvents()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 150)
        {
            Pump();
            Thread.Sleep(5);
        }
    }
}
