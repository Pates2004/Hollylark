using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static readonly (string Id, string Label)[] AccessibleSettings =
    [
        ("Gain", "Wzmocnienie"), ("Noise", "Redukcja szumu"), ("Equalizer", "Korektor EQ"),
        ("Reverb", "Pogłos"), ("ChannelMode", "Kanały"), ("Lights", "Diody LED"),
        ("AutoOff", "Automatyczne wyłączenie"), ("Identification", "Rozpoznawanie mikrofonów")
    ];

    private static void VerifyKeyboardAccessibility()
    {
        VerifyBoundReadoutSelection();
        using var transport = new FakeTransport();
        MainWindow window = CreateWindow(transport, "accessibility");
        try
        {
            using var observer = new ExternalArrowObserver(window, "keyboard-native-client");
            observer.Snapshot("Gain.Choice");
            VerifyControlPageAccessibility(window, connected: false);
            VerifyControlPageFocusOrder(window, connected: false);
            VerifyReceiverPageAccessibility(window, connected: false);
            RunTask(window.ConnectAsync());
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "The isolated backend connection opens only the mock transport and performs reads");
            VerifyReceiverPageAccessibility(window, connected: true);
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 0, "Sterowanie");
            VerifyControlPageAccessibility(window, connected: true);
            VerifyControlPageFocusOrder(window, connected: true);
            VerifyTransmitterTelemetry(window, 1, "90%", muted: false);
            VerifyTransmitterTelemetry(window, 2, "50%", muted: true);
            VerifyKeyboardDraftAndApply(window, transport);
            VerifyMuteAccessibility(window, transport);
            transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            VerifyControlPageAccessibility(window, connected: true, secondTransmitterConnected: false);
            VerifyControlPageFocusOrder(window, connected: true, secondTransmitterConnected: false);
            SelectTransmitter(window, 2);
            int writes = CountMockWrites(transport);
            InvokeAccessible(FindById<Button>(window, "TX2.Mute"));
            Check(CountMockWrites(transport) == writes && window.ResultText == Localization.Text("Main.Mute.ConnectSelected", 2),
                "Requesting an offline microphone action reports its guard without a device write");
            Check(ReadTransmitterSummary(window, 2) == "Mikrofon 2: niepołączony.",
                "Offline TX UIA item removes stale battery and mute telemetry");
            SelectTransmitter(window, 1);
            transport.SetTransmitterConnected(1, true);
            RunTask(Poll(window));
            FindById<ListBox>(window, "TransmitterList").SelectedItem = window.TransmitterTargets.Last();
            Pump();
            VerifySettingsAndHintsAccessibility(window, transport);
            window.Disconnect();
            VerifyControlPageAccessibility(window, connected: false);
            VerifyControlPageFocusOrder(window, connected: false);
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyControlPageAccessibility(MainWindow window, bool connected, bool secondTransmitterConnected = true)
    {
        Pump();
        TextBlock connection = FindById<TextBlock>(window, "ConnectionReadout");
        VerifyNamedPeer(connection, connection.Text, AutomationControlType.Text);
        TextBlock result = FindById<TextBlock>(window, "ResultReadout");
        if (result.Text.Length > 0)
            Check(GetAccessiblePeer(result).GetName() == Localization.Text("Main.Result.AccessibleName", result.Text),
                "UIA result name includes the complete actionable message");
        else
            Check(!result.IsVisible, "An empty result does not create an instructional Tab stop");
        Check(GetAccessiblePeer(result).GetLiveSetting() == (result.IsVisible ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off),
            "Only an actual visible result exposes a polite UIA live region");
        Check(GetAccessiblePeer(connection).GetLiveSetting() == AutomationLiveSetting.Off,
            "Routine connection readout is not an unsolicited live region");
        Check(!Descendants(window).OfType<TextBox>().Any(field => field.IsVisible),
            "Static-value mode has no visible duplicate edit fields");
        foreach ((string identifier, _) in AccessibleSettings)
        {
            SettingRowViewModel setting = AllSettings(window).Single(candidate => candidate.Id == identifier);
            bool available = connected && (identifier != "Identification" || secondTransmitterConnected);
            TextBlock current = FindById<TextBlock>(window, identifier + ".StaticCurrent");
            ComboBox choice = FindById<ComboBox>(window, identifier + ".Choice");
            Button apply = FindById<Button>(window, identifier + ".Apply");
            VerifyNamedPeer(current, setting.StaticValueText, AutomationControlType.Text);
            VerifyNamedPeer(choice, setting.ChoiceName, AutomationControlType.ComboBox, setting.CanEdit);
            VerifyNamedPeer(apply, setting.ApplyName, AutomationControlType.Button, setting.CanRequestApply);
            Check(setting.IsEnabled == available, "The backend retains physical setting availability: " + identifier);
            Check(!Descendants(window).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == identifier + ".Status"),
                "Setting instructions do not survive as redundant status controls: " + identifier);
            Check(GetAccessiblePeer(current).GetLiveSetting() == AutomationLiveSetting.Off,
                "Polled value is not a live region: " + identifier);
            Check(ReadAccessibleValue(current) == setting.StaticValueText
                && (connected ? setting.ConfirmedValue != Localization.Text("Setting.NoData") : setting.ConfirmedValue == Localization.Text("Setting.NoData")),
                "UIA confirmed static value follows connection state: " + identifier);
            if (setting.SelectedIndex >= 0)
            {
                VerifyNoClrItemNames(GetAccessiblePeer(window));
                _ = GetAccessiblePeer(choice).GetChildren();
                var selection = RequireAccessiblePattern<ISelectionProvider>(choice, PatternInterface.Selection);
                IRawElementProviderSimple[] selected = selection.GetSelection();
                Check(selected is [{ }] && choice.SelectedItem is ComboBoxItem { Content: string caption }
                    && Equals(selected[0].GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id), caption),
                    "UIA selection exposes the actual setting option: " + identifier
                    + "; selected=" + string.Join(" | ", selected.Select(provider => provider is null ? "null provider"
                        : provider.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id)))
                    + "; count=" + selected.Length + "; item=" + choice.SelectedItem);
            }
        }
        ListBox transmitters = FindById<ListBox>(window, "TransmitterList");
        Check(transmitters.DisplayMemberPath == nameof(TransmitterViewModel.Summary)
            && transmitters.ItemTemplate is null && KeyboardNavigation.GetTabNavigation(transmitters) == KeyboardNavigationMode.Once,
            "Microphones retain native summary-based list presentation and one-entry Tab navigation");
        foreach (int number in new[] { 1, 2 })
        {
            ListBoxItem item = GetTransmitterItem(window, number);
            VerifyNamedPeer(item, window.Transmitters[number - 1].Summary, AutomationControlType.ListItem);
            Check(GetAccessiblePeer(item).GetLiveSetting() == AutomationLiveSetting.Off,
                $"TX{number} battery polling does not announce every update");
        }
        var selectedTransmitter = (TransmitterViewModel)transmitters.SelectedItem;
        Button[] actions = Descendants(window).OfType<Button>().Where(button =>
            AutomationProperties.GetAutomationId(button).EndsWith(".Mute", StringComparison.Ordinal)).ToArray();
        Check(actions.Length == 1 && AutomationProperties.GetAutomationId(actions[0]) == selectedTransmitter.ActionId
            && GetAccessiblePeer(actions[0]).IsEnabled() == selectedTransmitter.CanRequestMute,
            "Only the selected microphone exposes its native action; backend guards remain independent");
    }

    private static void VerifyControlPageFocusOrder(MainWindow window, bool connected, bool secondTransmitterConnected = true)
    {
        var order = new List<UIElement>
        {
            FindById<TextBlock>(window, "ConnectionReadout"), (TabItem)window.FindName("ControlTab"),
            FindById<TextBlock>(window, "ReadoutStatus"),
            FindById<Button>(window, "OpenAudioMeterButton")
        };
        ListBox transmitters = FindById<ListBox>(window, "TransmitterList");
        var selected = (TransmitterViewModel)transmitters.SelectedItem;
        transmitters.UpdateLayout();
        order.Add((ListBoxItem)transmitters.ItemContainerGenerator.ContainerFromItem(selected));
        Button mute = FindById<Button>(window, selected.ActionId);
        if (mute.IsEnabled) order.Add(mute);
        foreach ((string identifier, _) in AccessibleSettings)
        {
            order.Add(FindById<TextBlock>(window, identifier + ".StaticCurrent"));
            ComboBox choice = FindById<ComboBox>(window, identifier + ".Choice");
            Button apply = FindById<Button>(window, identifier + ".Apply");
            if (choice.IsEnabled) order.Add(choice);
            if (apply.IsEnabled) order.Add(apply);
        }
        TextBlock result = FindById<TextBlock>(window, "ResultReadout");
        if (result.IsVisible) order.Add(result);
        if (connected) order.Add(FindById<Button>(window, "RefreshButton"));
        VerifyFocusOrder(order, cycle: false);
    }

    private static void VerifyReceiverPageAccessibility(MainWindow window, bool connected)
    {
        SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
        ListBox receivers = FindById<ListBox>(window, "ReceiverList");
        Button search = FindById<Button>(window, "SearchButton");
        TextBlock active = FindById<TextBlock>(window, "ActiveReceiverReadout");
        VerifyNamedPeer(receivers, Localization.Text("Main.Receiver.DiscoveredName"), AutomationControlType.List);
        VerifyNamedPeer(search, Localization.Text("Main.Receiver.SearchName"), AutomationControlType.Button);
        Check(active.Focusable && KeyboardNavigation.GetIsTabStop(active)
            && GetAccessiblePeer(active).GetAutomationControlType() == AutomationControlType.Text
            && GetAccessiblePeer(active).GetName().Contains(active.Text, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(active.Text),
            "UIA exposes the actual active receiver status as keyboard-readable static text");
        var receiverTab = (TabItem)window.FindName("ReceiverTab");
        Check(receiverTab.IsSelected && TabContentElements(receiverTab).OfType<ListBox>().SequenceEqual(new[] { receivers })
            && receivers.IsEnabled, "The same single native selector remains available while connected: " + connected);
        Check(TabContentElements(receiverTab).OfType<Label>().Any(label => ReferenceEquals(label.Target, active)),
            "The active receiver status has an associated visible label");
        var order = new List<UIElement> { FindById<TextBlock>(window, "ConnectionReadout"), receiverTab };
        receivers.UpdateLayout();
        foreach (UIElement element in TabContentElements(receiverTab).OfType<UIElement>().Where(element => element.IsVisible
            && element.IsEnabled && (ReferenceEquals(element, receivers) || KeyboardNavigation.GetIsTabStop(element)
            && element is TextBlock { Focusable: true } or TextBox or Button)))
            order.Add(ReferenceEquals(element, receivers)
                ? (ListBoxItem)receivers.ItemContainerGenerator.ContainerFromItem(receivers.SelectedItem) : element);
        Check(order.Contains(active) && order.Contains(search), "Receiver tab order includes its status and physical refresh action");
        TextBlock result = FindById<TextBlock>(window, "ResultReadout");
        if (result.IsVisible) order.Add(result);
        if (connected) order.Add(FindById<Button>(window, "RefreshButton"));
        VerifyFocusOrder(order, cycle: false);
    }

    private static void VerifyKeyboardDraftAndApply(MainWindow window, FakeTransport transport)
    {
        ComboBox gain = FindById<ComboBox>(window, "Gain.Choice");
        FocusForKeyboardCheck(gain);
        int writes = CountMockWrites(transport);
        RaiseLocalKey(gain, Key.Down);
        Check(gain.SelectedIndex == 4 && window.AudioSettings.Single(setting => setting.Id == "Gain").IsDirty,
            "Routed Down key changes the real WPF selection and marks a draft");
        Check(CountMockWrites(transport) == writes, "Keyboard selection never applies a setting implicitly");
        SettingRowViewModel setting = window.AudioSettings.Single(candidate => candidate.Id == "Gain");
        Check(setting.IsDirty && setting.StatusText.Contains("Niezastosowana zmiana"),
            "Keyboard selection retains a backend draft without a redundant instructional edit field");
        VerifyControlPageFocusOrder(window, connected: true);
        TextBlock result = FindById<TextBlock>(window, "ResultReadout");
        FocusForKeyboardCheck(result);
        string previousResult = ReadAccessibleValue(result);
        RunTask(Poll(window));
        CheckKeyboardFocus(result, "Polling preserves actual keyboard focus, not just logical focus");
        Check(ReadAccessibleValue(result) == previousResult && GetAccessiblePeer(result).GetAutomationControlType() == AutomationControlType.Text,
            "Polling preserves the static result text and native Text role");
        Check(gain.SelectedIndex == 4 && setting.IsDirty && setting.StatusText.Contains("Niezastosowana zmiana"),
            "Polling preserves the keyboard-created draft and accessible state");
        InvokeAccessible(FindById<Button>(window, "Gain.Apply"));
        PumpUntil(() => CountMockWrites(transport) > writes && !window.IsBusy && !window.AudioSettings.Single(setting => setting.Id == "Gain").IsDirty);
        Check(transport.Commands.Count(command => command == A1Command.SetGain) == 1 &&
            ReadAccessibleValue(FindById<TextBlock>(window, "Gain.StaticCurrent")) == setting.StaticValueText && setting.ConfirmedValue == "5", "UIA Invoke applies mock gain and exposes confirmed readback");
        CheckKeyboardFocus(result, "UIA Apply does not steal keyboard focus from the result");
        Check(!setting.IsDirty && ReadAccessibleValue(result).Contains("potwierdziło"),
            "Successful Apply consumes the draft and exposes its result");
        VerifyControlPageFocusOrder(window, connected: true);
    }

    private static ListBoxItem GetTransmitterItem(MainWindow window, int number)
    {
        ListBox list = FindById<ListBox>(window, "TransmitterList");
        list.UpdateLayout();
        return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(window.Transmitters[number - 1]);
    }

    private static void SelectTransmitter(MainWindow window, int number)
    {
        ListBox list = FindById<ListBox>(window, "TransmitterList");
        list.SelectedItem = window.Transmitters[number - 1];
        Pump();
        Check(ReferenceEquals(list.SelectedItem, window.Transmitters[number - 1]), "Select the native microphone item: " + number);
    }

    private static string ReadTransmitterSummary(MainWindow window, int number)
    {
        ListBoxItem item = GetTransmitterItem(window, number);
        string summary = window.Transmitters[number - 1].Summary;
        VerifyNamedPeer(item, summary, AutomationControlType.ListItem);
        ListBox list = FindById<ListBox>(window, "TransmitterList");
        AutomationPeer dataPeer = GetAccessiblePeer(list).GetChildren().Single(peer => peer.GetName() == summary);
        Check(AutomationProperties.GetAutomationId(item) == $"TX{number}.Item"
            && dataPeer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider,
            "The native microphone item exposes its stable identifier and selection pattern: " + number);
        return summary;
    }

    private static void VerifyMuteAccessibility(MainWindow window, FakeTransport transport)
    {
        SelectTransmitter(window, 1);
        ListBoxItem item = GetTransmitterItem(window, 1);
        FocusForKeyboardCheck(item);
        foreach (bool muted in new[] { true, false })
        {
            int writes = transport.Commands.Count(command => command == A1Command.SetMute);
            InvokeAccessible(FindById<Button>(window, "TX1.Mute"));
            PumpUntil(() => transport.Commands.Count(command => command == A1Command.SetMute) > writes &&
                !window.IsBusy && window.Transmitters[0].IsMuted == muted);
            VerifyTransmitterTelemetry(window, 1, "90%", muted);
            VerifyTransmitterTelemetry(window, 2, "50%", muted: true);
            CheckKeyboardFocus(item, "Mute readback preserves actual keyboard focus on the native microphone item");
            Check(ReferenceEquals(item, GetTransmitterItem(window, 1)) && item.IsSelected,
                "Changed TX state preserves native item identity and selection");
        }
    }

    private static void VerifyTransmitterTelemetry(MainWindow window, int number, string battery, bool muted)
    {
        string expected = $"Mikrofon {number}: połączony; bateria {battery}; {(muted ? "wyciszony" : "aktywny")}.";
        Check(ReadTransmitterSummary(window, number) == expected, $"UIA TX{number} exposes connected battery and mute state");
        if (ReferenceEquals(FindById<ListBox>(window, "TransmitterList").SelectedItem, window.Transmitters[number - 1]))
            VerifyNamedPeer(FindById<Button>(window, $"TX{number}.Mute"),
                muted ? $"Włącz dźwięk mikrofonu {number}" : $"Wycisz mikrofon {number}", AutomationControlType.Button);
    }

    private static void VerifySettingsAndHintsAccessibility(MainWindow owner, FakeTransport transport)
    {
        TextBlock returnFocus = FindById<TextBlock>(owner, "ResultReadout");
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "accessibility"));
        int writes = CountMockWrites(transport);
        FocusForKeyboardCheck(returnFocus);
        Window settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        try
        {
            CheckBox hints = VerifySettingsDialog(settings, enabled: true);
            RaiseLocalKey(hints, Key.Space);
            Check(RequireAccessiblePattern<IToggleProvider>(hints, PatternInterface.Toggle).ToggleState == ToggleState.Off,
                "Routed Space toggles the actual settings checkbox");
            RaiseLocalKey(hints, Key.Escape);
            Check(!settings.IsVisible && owner.ShowHints && !File.Exists(store.FilePath),
                "Escape cancels the dialog draft without persisting preferences");
            if (owner.IsActive)
                CheckKeyboardFocus(returnFocus, "Escape returns keyboard focus to the original main control");
            else
            {
                Check(Keyboard.FocusedElement is null, "Closing a background dialog does not steal foreground keyboard focus");
                Console.WriteLine("LIMIT: foreground focus restoration requires a manual active-window check; this harness stays offscreen.");
            }
        }
        finally { if (settings.IsVisible) settings.Close(); Pump(); }

        settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        try
        {
            CheckBox hints = VerifySettingsDialog(settings, enabled: true);
            RequireAccessiblePattern<IToggleProvider>(hints, PatternInterface.Toggle).Toggle();
            Pump();
            Check(RequireAccessiblePattern<IToggleProvider>(hints, PatternInterface.Toggle).ToggleState == ToggleState.Off,
                "UIA Toggle reports the settings checkbox change");
            InvokeAccessible(FindById<Button>(settings, "SaveSettingsButton"));
            PumpUntil(() => !settings.IsVisible);
            Check(!owner.ShowHints && !store.Load(out _).ShowHints, "UIA Save persists hints OFF only in the isolated test directory");
        }
        finally { if (settings.IsVisible) settings.Close(); Pump(); }

        VerifyControlPageAccessibility(owner, connected: true);
        VerifyControlPageFocusOrder(owner, connected: true);
        foreach (var (identifier, _) in AccessibleSettings)
            Check(GetAccessiblePeer(FindById<ComboBox>(owner, identifier + ".Choice")).GetHelpText() == string.Empty,
                "Hints OFF removes optional UIA help, not the essential name: " + identifier);
        Check(GetAccessiblePeer(FindById<Button>(owner, "RefreshButton")).GetHelpText() == string.Empty,
            "Hints OFF removes the optional refresh explanation from UIA");
        TextBlock[] hintsBlocks = Descendants(owner).OfType<TextBlock>()
            .Where(block => block.Style == owner.FindResource("HintStyle")).ToArray();
        Check(hintsBlocks.Length > 0 && hintsBlocks.All(block => block.Visibility == Visibility.Collapsed),
            "Hints OFF hides visual hints without removing essential readouts");
        VerifyTransmitterTelemetry(owner, 1, "90%", muted: false);
        VerifyTransmitterTelemetry(owner, 2, "50%", muted: true);
        VerifyReceiverPageAccessibility(owner, connected: true);
        SelectAccessibleTab(FindById<TabControl>(owner, "MainTabs"), 0, "Sterowanie");
        VerifyHelpKeyboardAccessibility(owner);

        FocusForKeyboardCheck(returnFocus);
        settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        try
        {
            CheckBox hints = VerifySettingsDialog(settings, enabled: false);
            Check(GetAccessiblePeer(hints).GetHelpText() == string.Empty, "Reopened hints-OFF settings hide optional UIA help");
            RaiseLocalKey(hints, Key.Space);
            Button save = FindById<Button>(settings, "SaveSettingsButton");
            UIElement[] savePath =
            [
                hints,
                FindById<CheckBox>(settings, "ShowOperationMessagesCheckBox"),
                FindById<ComboBox>(settings, "ValueDisplayModeChoice"),
                FindById<CheckBox>(settings, "AnnounceStateChangesCheckBox"),
                FindById<CheckBox>(settings, "PreserveVolatileSettingsCheckBox"),
                FindById<Button>(settings, "ResetControlsSettingsButton"),
                save
            ];
            for (int index = 1; index < savePath.Length; index++)
                MoveKeyboardFocus(savePath[index - 1], savePath[index], FocusNavigationDirection.Next);
            RaiseLocalKey(save, Key.Enter);
            Check(!settings.IsVisible && owner.ShowHints && store.Load(out _).ShowHints,
                "Routed Enter saves the keyboard-restored hints preference");
        }
        finally { if (settings.IsVisible) settings.Close(); Pump(); }
        Check(GetAccessiblePeer(FindById<ComboBox>(owner, "Gain.Choice")).GetHelpText().Length > 0,
            "Restored hints reappear through the UIA peer");
        Check(CountMockWrites(transport) == writes, "Settings and help checks never write device settings");
    }

    private static CheckBox VerifySettingsDialog(Window window, bool enabled)
    {
        VerifyNamedPeer(window, "Hollylark — ustawienia", AutomationControlType.Window);
        TabControl tabs = FindById<TabControl>(window, "SettingsTabs");
        VerifyNamedPeer(tabs, string.Empty, AutomationControlType.Tab);
        Check(AutomationProperties.GetHelpText(tabs) == (enabled ? Localization.Text("Settings.Categories.Name") : string.Empty),
            "The optional settings category heading follows the hints preference");
        TabItem appearance = tabs.Items.OfType<TabItem>().Single(tab =>
            AutomationProperties.GetAutomationId(tab) == "AppearanceSettingsTab");
        Check(tabs.Items.Count == 7 && ReferenceEquals(tabs.SelectedItem, appearance),
            "Settings open on Appearance with seven available categories including battery alerts");
        CheckKeyboardFocus(FindById<ComboBox>(window, "SettingsLanguageChoice"), "Settings initially focus the first available selector, like the reference");
        TabItem controls = SelectSettingsControlsTab(window);
        CheckBox hints = FindById<CheckBox>(window, "ShowHintsCheckBox");
        Button save = FindById<Button>(window, "SaveSettingsButton");
        Button cancel = FindById<Button>(window, "CancelSettingsButton");
        VerifyNamedPeer(hints, "Pokazuj podpowiedzi", AutomationControlType.CheckBox);
        VerifyNamedPeer(save, Localization.Text("Settings.Save.Name"), AutomationControlType.Button);
        VerifyNamedPeer(cancel, Localization.Text("Settings.Cancel.Name"), AutomationControlType.Button);
        Check(RequireAccessiblePattern<IToggleProvider>(hints, PatternInterface.Toggle).ToggleState == (enabled ? ToggleState.On : ToggleState.Off),
            "Settings UIA Toggle state matches the persisted preference");
        VerifyFocusOrder(
        [
            controls,
            FindById<ComboBox>(window, "GainControlModeChoice"),
            FindById<CheckBox>(window, "AutoApplySelectionsCheckBox"),
            hints,
            FindById<CheckBox>(window, "ShowOperationMessagesCheckBox"),
            FindById<ComboBox>(window, "ValueDisplayModeChoice"),
            FindById<CheckBox>(window, "AnnounceStateChangesCheckBox"),
            FindById<CheckBox>(window, "PreserveVolatileSettingsCheckBox"),
            FindById<Button>(window, "ResetControlsSettingsButton"),
            save,
            cancel
        ], cycle: true);
        FocusForKeyboardCheck(hints);
        return hints;
    }

    private static TabItem SelectSettingsControlsTab(Window window)
    {
        TabControl tabs = FindById<TabControl>(window, "SettingsTabs");
        TabItem controls = tabs.Items.OfType<TabItem>().Single(tab =>
            AutomationProperties.GetAutomationId(tab) == "ControlsSettingsTab");
        SelectAccessibleTab(tabs, tabs.Items.IndexOf(controls), "Sterowanie");
        Check(ReferenceEquals(tabs.SelectedItem, controls), "Settings select Controls explicitly before accessing hints");
        return controls;
    }

    private static void VerifyHelpKeyboardAccessibility(MainWindow owner)
    {
        Window help = owner.OpenHelpWindow();
        MoveOffscreen(help);
        Pump();
        try
        {
            VerifyNamedPeer(help, "Hollylark — pomoc", AutomationControlType.Window);
            TextBox instructions = FindById<TextBox>(help, "HelpReadout");
            VerifyNamedPeer(instructions, "Instrukcja i ograniczenia Hollylark", AutomationControlType.Edit);
            CheckKeyboardFocus(instructions, "Help initially focuses its readable instructions");
            string helpText = ReadAccessibleValue(instructions);
            Check(helpText.Contains("Escape") && helpText.Contains("nie cofa"),
                "Hints OFF leaves explicit help and safety instructions readable");
            Button close = FindById<Button>(help, "CloseSectionButton");
            VerifyNamedPeer(close, "Zamknij okno", AutomationControlType.Button);
            TextBox licenses = FindById<TextBox>(help, "LicensesReadout");
            VerifyNamedPeer(licenses, "Licencje bibliotek — oryginalna treść", AutomationControlType.Edit);
            string licenseText = ReadAccessibleValue(licenses);
            Check(licenseText.Contains("HidSharp") && !licenseText.Contains("Brak osadzonego dokumentu"),
                "Licenses expose the embedded document, not an error placeholder");
            VerifyFocusOrder([instructions, FindById<Button>(help, "OpenManualButton"),
                FindById<Button>(help, "OpenOfficialAppButton"), licenses, close], cycle: true);
            InvokeAccessible(close);
            Check(!help.IsVisible, "UIA Close closes only the help window");
        }
        finally { if (help.IsVisible) help.Close(); Pump(); }
    }

    private static AutomationPeer GetAccessiblePeer(UIElement element) =>
        UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException("Missing UIA peer: " + DescribeFocus(element));

    private static void VerifyBoundReadoutSelection()
    {
        var readout = new TextBox { IsReadOnly = true };
        ReadoutText.SetValue(readout, "Battery: 100 percent");
        readout.Select(0, 7);
        ReadoutText.SetValue(readout, "Battery: 99 percent");
        Check(readout.Text == "Battery: 99 percent" && readout.SelectionStart == 0 && readout.SelectionLength == 7,
            "Attached readout binding updates text without resetting the selected label");
        readout.Select(12, 5);
        ReadoutText.SetValue(readout, "None");
        Check(readout.SelectionStart == 4 && readout.SelectionLength == 0,
            "Shorter readout clamps the caret and selection to valid bounds");
        readout.SetValue(ReadoutText.ValueProperty, null);
        Check(readout.Text.Length == 0 && readout.SelectionStart == 0,
            "Unavailable attached readout becomes empty without an invalid caret");
    }

    private static TPattern RequireAccessiblePattern<TPattern>(UIElement element, PatternInterface pattern) where TPattern : class =>
        GetAccessiblePeer(element).GetPattern(pattern) as TPattern
        ?? throw new InvalidOperationException($"Missing UIA {pattern} provider: {DescribeFocus(element)}");

    private static void VerifyNamedPeer(UIElement element, string name, AutomationControlType role, bool enabled = true)
    {
        AutomationPeer peer = GetAccessiblePeer(element);
        Check(peer.GetName() == name && peer.GetAutomationControlType() == role && peer.IsEnabled() == enabled,
            "UIA name, role and enabled state: " + DescribeFocus(element));
    }

    private static string ReadAccessibleValue(TextBox readout)
    {
        var value = RequireAccessiblePattern<IValueProvider>(readout, PatternInterface.Value);
        Check(value.IsReadOnly && value.Value == readout.Text, "UIA read-only Value matches displayed text: " + DescribeFocus(readout));
        return value.Value;
    }

    private static string ReadAccessibleValue(TextBlock readout)
    {
        AutomationPeer peer = GetAccessiblePeer(readout);
        Check(peer.GetAutomationControlType() == AutomationControlType.Text
            && peer.GetPattern(PatternInterface.Value) is null
            && peer.GetName().Contains(readout.Text, StringComparison.Ordinal),
            "UIA static text exposes the displayed value without an edit pattern: " + DescribeFocus(readout));
        return readout.Text;
    }

    private static void InvokeAccessible(Button button)
    {
        RequireAccessiblePattern<IInvokeProvider>(button, PatternInterface.Invoke).Invoke();
        Pump();
    }

    private static void VerifyDisabledInvoke(Button button)
    {
        Check(!GetAccessiblePeer(button).IsEnabled(), "UIA exposes disabled action: " + DescribeFocus(button));
        Throws<ElementNotEnabledException>(() => RequireAccessiblePattern<IInvokeProvider>(button, PatternInterface.Invoke).Invoke(),
            "UIA rejects disabled action: " + DescribeFocus(button));
        Pump();
    }

    private static void SelectAccessibleTab(TabControl tabs, int index, string expectedName)
    {
        AutomationPeer peer = GetAccessiblePeer(tabs).GetChildren()
            .Where(child => child.GetAutomationControlType() == AutomationControlType.TabItem).ElementAt(index);
        Check(peer.GetName() == expectedName, "UIA tab name: " + expectedName);
        var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
            ?? throw new InvalidOperationException("Missing UIA tab SelectionItem: " + expectedName);
        selection.Select();
        Pump();
        Check(selection.IsSelected && tabs.SelectedIndex == index, "UIA selects and reports the actual tab: " + expectedName);
    }

    private static void VerifyFocusOrder(IReadOnlyList<UIElement> order, bool cycle)
    {
        FocusForKeyboardCheck(order[0]);
        for (int index = 1; index < order.Count; index++) MoveKeyboardFocus(order[index - 1], order[index], FocusNavigationDirection.Next);
        if (cycle)
        {
            MoveKeyboardFocus(order[^1], order[0], FocusNavigationDirection.Next);
            MoveKeyboardFocus(order[0], order[^1], FocusNavigationDirection.Previous);
        }
        for (int index = order.Count - 2; index >= 0; index--) MoveKeyboardFocus(order[index + 1], order[index], FocusNavigationDirection.Previous);
    }

    private static void FocusForKeyboardCheck(UIElement target)
    {
        Keyboard.Focus(target);
        Pump();
        CheckKeyboardFocus(target, "Seed in-process WPF keyboard focus: " + DescribeFocus(target));
    }

    private static void CheckKeyboardFocus(UIElement expected, string description) =>
        Check(ReferenceEquals(Keyboard.FocusedElement, expected) && expected.IsKeyboardFocused && GetAccessiblePeer(expected).HasKeyboardFocus(),
            description + "; actual=" + DescribeFocus(Keyboard.FocusedElement)
                + $"; windowActive={Window.GetWindow(expected)?.IsActive}; modifiers={Keyboard.Modifiers}");

    private static void MoveKeyboardFocus(UIElement source, UIElement expected, FocusNavigationDirection direction)
    {
        Check(ReferenceEquals(Keyboard.FocusedElement, source), "Traversal starts at " + DescribeFocus(source));
        Window owner = Window.GetWindow(source);
        string unexpectedFocus = string.Empty;
        KeyboardFocusChangedEventHandler trace = (_, arguments) =>
        {
            if (!ReferenceEquals(arguments.NewFocus, expected))
                unexpectedFocus = DescribeFocus(arguments.NewFocus) + Environment.NewLine + Environment.StackTrace;
        };
        owner.GotKeyboardFocus += trace;
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                WaitForUnmodifiedInput();
                unexpectedFocus = string.Empty;
                ModifierKeys beforeModifiers = Keyboard.Modifiers;
                bool moved = source.MoveFocus(new TraversalRequest(direction));
                string immediateFocus = DescribeFocus(Keyboard.FocusedElement);
                Pump();
                ModifierKeys afterModifiers = Keyboard.Modifiers;
                if (beforeModifiers != ModifierKeys.None || afterModifiers != ModifierKeys.None)
                {
                    Console.WriteLine($"RETRY: external modifiers affected unmodified traversal: {beforeModifiers} -> {afterModifiers}.");
                    WaitForUnmodifiedInput();
                    FocusForKeyboardCheck(source);
                    continue;
                }
                Check(moved, $"WPF {direction} traversal succeeds from {DescribeFocus(source)}");
                if (!ReferenceEquals(Keyboard.FocusedElement, expected) && unexpectedFocus.Length > 0)
                    Console.Error.WriteLine($"Modifiers before={beforeModifiers}, after={afterModifiers}; immediate={immediateFocus}; " +
                        $"selectedMainTab={((owner.FindName("MainTabs") as TabControl)?.SelectedIndex)}; expectedVisible={expected.IsVisible}; " +
                        "Unexpected focus transition: " + unexpectedFocus);
                CheckKeyboardFocus(expected, $"WPF {direction}: {DescribeFocus(source)} -> {DescribeFocus(expected)}");
                return;
            }
            throw new InvalidOperationException("External modifier activity prevented a stable unmodified keyboard traversal; expectations were not evaluated.");
        }
        finally { owner.GotKeyboardFocus -= trace; }
    }

    private static void WaitForUnmodifiedInput()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (Keyboard.Modifiers != ModifierKeys.None)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12))
                throw new InvalidOperationException("Release external Ctrl/Alt/Shift/Windows keys before running keyboard validation. No input state was changed.");
            Pump();
            Thread.Sleep(10);
        }
    }

    private static void RaiseLocalKey(UIElement target, Key key)
    {
        WaitForUnmodifiedInput();
        Check(ReferenceEquals(Keyboard.FocusedElement, target), "Routed key targets the focused test control: " + DescribeFocus(target));
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("Test control has no WPF presentation source.");
        foreach (RoutedEvent routedEvent in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routedEvent });
        Pump();
    }

    private static string DescribeFocus(IInputElement? element) => element is DependencyObject dependency
        ? $"{element.GetType().Name}[{AutomationProperties.GetAutomationId(dependency)}; {AutomationProperties.GetName(dependency)}]"
        : element?.GetType().Name ?? "none";

    private static int CountMockWrites(FakeTransport transport) =>
        transport.Commands.Count(command => command is not (A1Command.GetStatus or A1Command.GetVersion
            or A1Command.GetSerialNumber or A1Command.GetReceiverMacAddress or A1Command.GetTransmitterMacAddress));
}
