using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyNativeListParityApplication()
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Application.Current is null,
            "Standalone native-list checks require STA and no existing Application");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try { VerifyNativeListParityChecks(); }
        finally
        {
            try { application.Shutdown(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }

    private static void VerifyNativeListParityChecks()
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && Application.Current is not null && Application.Current.Dispatcher.CheckAccess()
            && SynchronizationContext.Current is DispatcherSynchronizationContext,
            "Native-list checks reuse the suite's STA Application and dispatcher context");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyNativeSetupLists(language);
                VerifyNativeSettingsLists(language);
                VerifyNativeProfileRefresh(language);
                VerifyNativeMainLists(language);
                VerifyNativeAudioInputList(language);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void ShowNativeListWindow(Window window)
    {
        MoveOffscreen(window);
        window.Show();
        Pump();
    }

    private static void VerifyNativeItemPresentation(ItemsControl control, string accessibleName)
    {
        string identifier = AutomationProperties.GetAutomationId(control);
        Check(control.ItemTemplate is null && control.ItemContainerStyle is null,
            "No custom item template or container style: " + identifier);
        Check(GetAccessiblePeer(control).GetName() == accessibleName && !string.IsNullOrWhiteSpace(accessibleName),
            "Native list retains its localized accessible name: " + identifier);
    }

    private static string NativeListCaption(object item) => item switch
    {
        ComboBoxItem { Content: string caption } => caption,
        ComboBoxItem => throw new InvalidOperationException("A native ComboBoxItem must contain a string."),
        AudioProfile profile => profile.Name,
        ReceiverInfo receiver => receiver.Name,
        AudioInput input => input.Name,
        _ => TypeDescriptor.GetProperties(item)["Label"]?.GetValue(item) as string
            ?? throw new InvalidOperationException("The generated scale item must expose a string Label.")
    };

    private static void VerifyNativeSelectedText(ComboBox choice)
    {
        Check(choice.SelectedItem is not null && choice.Text == NativeListCaption(choice.SelectedItem),
            "Closed selection text is the visible caption: " + AutomationProperties.GetAutomationId(choice));
    }

    private static void VerifyNativeTaggedChoice(ComboBox choice, string accessibleName,
        params (object Value, string Text)[] expected)
    {
        VerifyNativeItemPresentation(choice, accessibleName);
        Check(choice.SelectedValuePath == nameof(FrameworkElement.Tag) && choice.DisplayMemberPath.Length == 0,
            "Static choice uses Tag, without a data-item display path: " + AutomationProperties.GetAutomationId(choice));
        VerifyNativeComboItems(choice, expected);
    }

    private static void VerifyNativeComboItems(ComboBox choice, (object Value, string Text)[] expected)
    {
        string identifier = AutomationProperties.GetAutomationId(choice);
        Check(choice.Items.Count == expected.Length, "Native option count: " + identifier);
        choice.IsDropDownOpen = true;
        Pump();
        try
        {
            for (int index = 0; index < expected.Length; index++)
            {
                Check(choice.Items[index] is ComboBoxItem, "Explicit native ComboBoxItem: " + identifier + "/" + index);
                var item = (ComboBoxItem)choice.Items[index];
                Check(item.Content is string caption && caption == expected[index].Text
                    && item.Tag is not null && item.Tag.GetType() == expected[index].Value.GetType()
                    && Equals(item.Tag, expected[index].Value), "String Content and typed Tag: " + identifier + "/" + index);
                Check(ReferenceEquals(choice.ItemContainerGenerator.ContainerFromIndex(index), item)
                    && GetAccessiblePeer(item).GetName() == expected[index].Text,
                    "The explicit item is its own container and has the caption as UIA Name: " + identifier + "/" + index);
            }
        }
        finally { choice.IsDropDownOpen = false; Pump(); }
        if (choice.SelectedIndex >= 0) VerifyNativeSelectedText(choice);
    }

    private static void VerifyNativeLanguageAndTheme(Window window, bool setup)
    {
        string prefix = setup ? "FirstRun" : "Settings";
        VerifyNativeTaggedChoice((ComboBox)window.FindName(prefix + "LanguageChoice"),
            Localization.Text(setup ? "Setup.Language.Name" : "Settings.Language.Name"),
            ("pl", Localization.Text("Settings.Language.Polish")), ("en", Localization.Text("Settings.Language.English")));
        (object Value, string Text)[] themes =
        [
            ("Light", Localization.Text("Settings.Theme.Light")), ("Dark", Localization.Text("Settings.Theme.Dark"))
        ];
        VerifyNativeTaggedChoice((ComboBox)window.FindName(prefix + "ThemeChoice"),
            Localization.Text(setup ? "Setup.Theme.Name" : "Settings.Theme.Name"), themes);
    }

    private static void VerifyNativeValueDisplay(ComboBox choice) => VerifyNativeTaggedChoice(choice,
        Localization.Text("Settings.ValueDisplay.Name"),
        (ValueDisplayMode.ReadOnlyFields, Localization.Text("Settings.ValueDisplay.ReadOnlyFields")),
        (ValueDisplayMode.StaticText, Localization.Text("Settings.ValueDisplay.StaticText")),
        (ValueDisplayMode.None, Localization.Text("Settings.ValueDisplay.None")));

    private static void VerifyNativeScaleChoice(ComboBox choice)
    {
        VerifyNativeItemPresentation(choice, Localization.Text("Settings.TextScale.Name"));
        Check(choice.DisplayMemberPath == "Label" && choice.SelectedValuePath == "Value",
            "Text scale retains generated Label/Value data items");
        int[] expected = [100, 125, 135, 150, 175, 200];
        Check(choice.Items.Count == expected.Length, "Text scale includes 135 once, alongside the standard scales");
        for (int index = 0; index < expected.Length; index++)
        {
            object item = choice.Items[index];
            Check(item is not ComboBoxItem && TypeDescriptor.GetProperties(item)["Value"]?.GetValue(item) is int value
                && value == expected[index] && NativeListCaption(item) == Localization.Text("Settings.TextScale.Percentage", value),
                "Generated scale has an integer Value and localized Label: " + expected[index]);
        }
        Check(choice.SelectedValue is 135, "Unusual scale 135 remains selected rather than falling back");
        VerifyNativeSelectedText(choice);
    }

    private static void VerifyNativeComboKeyboard(ComboBox choice)
    {
        string identifier = AutomationProperties.GetAutomationId(choice);
        int originalIndex = choice.SelectedIndex;
        Check(choice.Items.Count >= 2 && !choice.IsEditable && choice.IsTextSearchEnabled,
            "Native noneditable choice supports typeahead: " + identifier);
        try
        {
            choice.SelectedIndex = 0;
            FocusForKeyboardCheck(choice);
            RaiseLocalKey(choice, Key.Down);
            Check(choice.SelectedIndex == 1 && choice.IsKeyboardFocused, "Closed Down selects the next native item: " + identifier);
            VerifyNativeSelectedText(choice);
            RaiseLocalKey(choice, Key.Up);
            Check(choice.SelectedIndex == 0 && choice.IsKeyboardFocused, "Closed Up restores the previous native item: " + identifier);
            VerifyNativeSelectedText(choice);
            choice.IsDropDownOpen = true;
            Pump();
            Check(choice.IsDropDownOpen, "Native dropdown opened before testing exploration: " + identifier);
            RaiseLocalKey((UIElement)Keyboard.FocusedElement, Key.Down);
            var next = (ComboBoxItem)choice.ItemContainerGenerator.ContainerFromIndex(1);
            Check(next.IsKeyboardFocused && GetAccessiblePeer(next).GetName() == NativeListCaption(choice.Items[1])
                && choice.SelectedIndex == 0, "Expanded Down highlights, but does not commit, the next item: " + identifier);
            RaiseLocalKey(next, Key.Up);
            var first = (ComboBoxItem)choice.ItemContainerGenerator.ContainerFromIndex(0);
            Check(first.IsKeyboardFocused && choice.SelectedIndex == 0, "Expanded Up restores the highlighted item: " + identifier);
            RaiseLocalKey(first, Key.Down);
            RaiseLocalKey((UIElement)Keyboard.FocusedElement, Key.Escape);
            Check(!choice.IsDropDownOpen && choice.SelectedIndex == 0 && Window.GetWindow(choice).IsVisible,
                "Escape dismisses the popup without committing exploration or closing its window: " + identifier);
            FocusForKeyboardCheck(choice);
            RaiseNativeListText(choice, NativeListCaption(choice.Items[choice.Items.Count - 1]));
            Check(choice.SelectedIndex == choice.Items.Count - 1, "Native typeahead selects by visible caption: " + identifier);
            VerifyNativeSelectedText(choice);
        }
        finally { choice.IsDropDownOpen = false; choice.SelectedIndex = originalIndex; Pump(); }
    }

    private static void RaiseNativeListText(UIElement target, string text)
    {
        Check(target.IsKeyboardFocused, "Local typeahead targets the focused native control");
        var composition = new TextComposition(InputManager.Current, target, text);
        target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.TextInputEvent
        });
        Pump();
    }

    private static void VerifyNativeSetupLists(string language)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "native-list-setup-" + language));
        var window = new FirstRunWindow(new UiPreferences { Language = language, TextScale = 135 }, store);
        try
        {
            ShowNativeListWindow(window);
            ComboBox languageChoice = FindById<ComboBox>(window, "FirstRunLanguageChoice");
            ComboBox theme = FindById<ComboBox>(window, "FirstRunThemeChoice");
            ComboBox scale = FindById<ComboBox>(window, "FirstRunScaleChoice");
            ComboBox display = FindById<ComboBox>(window, "FirstRunValueDisplayChoice");
            Check(languageChoice.SelectedIndex == -1 && theme.SelectedIndex == -1,
                "Native construction preserves the required explicit setup language/theme choices");
            languageChoice.SelectedValue = language;
            theme.SelectedValue = "Light";
            Pump();
            object[] themeItems = theme.Items.Cast<object>().ToArray();
            object[] displayItems = display.Items.Cast<object>().ToArray();
            foreach (string previewLanguage in new[] { language == "pl" ? "en" : "pl", language })
            {
                languageChoice.SelectedValue = previewLanguage;
                Pump();
                Check(Localization.CurrentLanguage == previewLanguage && Equals(languageChoice.SelectedValue, previewLanguage),
                    "A string Tag still previews the chosen setup language");
                VerifyNativeLanguageAndTheme(window, setup: true);
                VerifyNativeValueDisplay(display);
                VerifyNativeScaleChoice(scale);
                VerifyNativeItemIdentity(theme, themeItems, "setup language preview");
                VerifyNativeItemIdentity(display, displayItems, "setup language preview");
            }
            foreach (ComboBox choice in new[] { languageChoice, theme, scale, display }) VerifyNativeComboKeyboard(choice);
            languageChoice.SelectedValue = language;
            theme.SelectedValue = "Dark";
            scale.SelectedValue = 135;
            display.SelectedValue = ValueDisplayMode.None;
            Pump();
            ClickFirstRun(window, "SaveFirstRunButton");
            UiPreferences saved = store.Load(out string? error);
            Check(error is null && window.SavedPreferences is not null && !window.IsVisible && saved.FirstRunCompleted
                && saved.Language == language && saved.Theme == "Dark" && saved.TextScale == 135
                && saved.ValueDisplayMode == ValueDisplayMode.None,
                "Setup saves string/enum Tags and generated scale Values without changing their meaning");
        }
        finally { if (window.IsVisible) window.Close(); Pump(); }
    }

    private static void VerifyNativeSettingsLists(string language)
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile[] originalProfiles = [AudioProfile.FromStatus("Alpha profile", status),
            AudioProfile.FromStatus("Bravo profile", status), AudioProfile.FromStatus("Charlie profile", status)];
        var preferences = new UiPreferences
        {
            Language = language, Theme = "Dark", TextScale = 135, FirstRunCompleted = true,
            GainControlMode = GainControlMode.Buttons, ValueDisplayMode = ValueDisplayMode.StaticText,
            AudioProfiles = originalProfiles
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "native-list-settings-" + language));
        UiPreferences? committed = null;
        int captures = 0;
        var window = new SettingsWindow(preferences, store, (candidate, _) => committed = candidate,
            readStatus: () => status, refreshStatus: () => { captures++; return Task.FromResult<A1Status?>(status); },
            applyProfile: _ => throw new InvalidOperationException("List parity must never apply an audio profile."),
            canUseReceiver: () => true, confirm: (_, _) => true);
        try
        {
            ShowNativeListWindow(window);
            var languageChoice = (ComboBox)window.FindName("SettingsLanguageChoice");
            var theme = (ComboBox)window.FindName("SettingsThemeChoice");
            var scale = (ComboBox)window.FindName("SettingsTextScaleChoice");
            var gain = (ComboBox)window.FindName("GainControlModeChoice");
            var display = (ComboBox)window.FindName("ValueDisplayModeChoice");
            ComboBox[] choices = [languageChoice, theme, scale, gain, display];
            object[][] originalItems = choices.Select(choice => choice.Items.Cast<object>().ToArray()).ToArray();
            VerifyNativeLanguageAndTheme(window, setup: false);
            VerifyNativeScaleChoice(scale);
            Check(Equals(languageChoice.SelectedValue, language) && theme.SelectedValue is "Dark",
                "Settings initialize the existing language and theme SelectedValue contracts");
            foreach (ComboBox choice in new[] { languageChoice, theme, scale }) VerifyNativeComboKeyboard(choice);
            Check(Localization.CurrentLanguage == language, "Settings language arrows remain a draft, not a live language switch");
            SelectSettingsCategory(window, "ControlsSettingsTab");
            VerifyNativeTaggedChoice(gain, Localization.Text("Settings.GainMode.Name"),
                (GainControlMode.Choice, Localization.Text("Settings.GainMode.Choice")),
                (GainControlMode.Buttons, Localization.Text("Settings.GainMode.Buttons")),
                (GainControlMode.Slider, Localization.Text("Settings.GainMode.Slider")));
            VerifyNativeValueDisplay(display);
            Check(gain.SelectedValue is GainControlMode.Buttons && display.SelectedValue is ValueDisplayMode.StaticText,
                "Settings initialize typed gain/display SelectedValue contracts");
            VerifyNativeComboKeyboard(gain);
            VerifyNativeComboKeyboard(display);
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            var profiles = (ListBox)window.FindName("AudioProfilesList");
            VerifyNativeItemPresentation(profiles, Localization.Text("Settings.Profiles.ListName"));
            Check(profiles.DisplayMemberPath == nameof(AudioProfile.Name)
                && profiles.ItemsSource is ObservableCollection<AudioProfile> && VirtualizingPanel.GetIsVirtualizing(profiles),
                "Profiles use Name, an ObservableCollection and native virtualization");
            object originalSource = profiles.ItemsSource;
            VerifyNativeListKeyboard(profiles);
            profiles.SelectedItem = originalProfiles[1];
            Pump();
            foreach (string scope in new[] { "Appearance", "Controls" })
            {
                SelectSettingsCategory(window, scope + "SettingsTab");
                ClickSettings(window, "Reset" + scope + "SettingsButton");
                Check(ReferenceEquals(profiles.ItemsSource, originalSource)
                    && ReferenceEquals(profiles.SelectedItem, originalProfiles[1]),
                    "An unrelated category reset preserves the profile collection and selection: " + scope);
                VerifyNativeItemIdentity(profiles, originalProfiles, scope + " reset");
            }
            Check(theme.SelectedValue is "Light" && scale.SelectedValue is 100
                && gain.SelectedValue is GainControlMode.Choice && display.SelectedValue is ValueDisplayMode.StaticText,
                "Category resets update values without replacing native items");
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            ((TextBox)window.FindName("AudioProfileNameTextBox")).Text = "Delta captured profile";
            ClickSettings(window, "SaveAudioProfileButton");
            Check(captures == 1 && ReferenceEquals(profiles.ItemsSource, originalSource) && profiles.Items.Count == 4
                && profiles.SelectedItem is AudioProfile { Name: "Delta captured profile" }
                && originalProfiles.Select((profile, index) => ReferenceEquals(profile, profiles.Items[index])).All(same => same),
                "Capture appends and selects a new fake-status profile without rebuilding surviving items");
            var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
            Check(summary.Text.Contains("Delta captured profile", StringComparison.Ordinal),
                "Profile capture updates the current summary to the newly selected profile");
            ClickSettings(window, "DeleteAudioProfileButton");
            Check(ReferenceEquals(profiles.ItemsSource, originalSource) && profiles.SelectedItem is AudioProfile selected
                && originalProfiles.Any(profile => ReferenceEquals(profile, selected))
                && summary.Text.Contains(selected.Name, StringComparison.Ordinal),
                "Deleting the selected captured profile chooses a surviving profile and updates its summary");
            VerifyNativeItemIdentity(profiles, originalProfiles, "profile capture/delete");
            Check(committed is null && !File.Exists(store.FilePath)
                && preferences.AudioProfiles.Length == 3 && preferences.AudioProfiles.SequenceEqual(originalProfiles),
                "Profile changes and category resets remain local drafts");
            ClickSettings(window, "ResetProfilesSettingsButton");
            Check(ReferenceEquals(profiles.ItemsSource, originalSource) && profiles.Items.Count == 0 && profiles.SelectedItem is null,
                "Profile reset intentionally clears items/selection but not the collection instance");
            ClickSettings(window, "ResetAllSettingsButton");
            Check(ReferenceEquals(profiles.ItemsSource, originalSource) && profiles.Items.Count == 0
                && Equals(languageChoice.SelectedValue, language),
                "Global reset keeps the collection instance and explicit language draft");
            for (int index = 0; index < choices.Length; index++)
                VerifyNativeItemIdentity(choices[index], originalItems[index], "settings resets and profile capture/delete");
            theme.SelectedValue = "Dark";
            scale.SelectedValue = 135;
            gain.SelectedValue = GainControlMode.Slider;
            display.SelectedValue = ValueDisplayMode.None;
            Pump();
            ClickSettings(window, "SaveSettingsButton");
            UiPreferences saved = store.Load(out string? error);
            Check(error is null && committed is not null && !window.IsVisible && saved.Language == language
                && saved.Theme == "Dark" && saved.TextScale == 135 && saved.GainControlMode == GainControlMode.Slider
                && saved.ValueDisplayMode == ValueDisplayMode.None && saved.AudioProfiles.Length == 0,
                "Settings save still consumes typed Tags and the unusual scale after all draft operations");
        }
        finally { if (window.IsVisible) window.Close(); Pump(); }
    }

    private static void VerifyNativeProfileRefresh(string language)
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile[] profiles = [AudioProfile.FromStatus("First profile", status),
            AudioProfile.FromStatus("Middle profile", status), AudioProfile.FromStatus("Last profile", status)];
        var preferences = new UiPreferences { Language = language, AudioProfiles = profiles };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "native-profile-refresh-" + language));
        var window = new SettingsWindow(preferences, store, (_, _) => throw new InvalidOperationException("No settings commit expected."),
            confirm: (_, title) => title != Localization.Text("Settings.Save.Title"));
        try
        {
            ShowNativeListWindow(window);
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            var list = (ListBox)window.FindName("AudioProfilesList");
            var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
            list.SelectedIndex = 1;
            list.UpdateLayout();
            object collection = list.ItemsSource;
            ListBoxItem[] containers = profiles.Select(profile => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(profile)).ToArray();
            FocusForKeyboardCheck(containers[1]);
            MethodInfo populate = typeof(SettingsWindow).GetMethod("PopulateControls", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int pass = 0; pass < 2; pass++)
            {
                populate.Invoke(window, null);
                Pump();
                Check(ReferenceEquals(list.ItemsSource, collection) && ReferenceEquals(list.SelectedItem, profiles[1])
                    && profiles.Select((profile, index) => ReferenceEquals(containers[index], list.ItemContainerGenerator.ContainerFromItem(profile))).All(same => same),
                    "Unchanged visible profile refresh preserves collection, selection and every native container");
                CheckKeyboardFocus(containers[1], "Unchanged visible profile refresh preserves the focused middle item");
            }
            AudioProfile replacement = profiles[1] with { Gain = (profiles[1].Gain + 1) % 6 };
            typeof(SettingsWindow).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, preferences with { AudioProfiles = [profiles[0], replacement, profiles[2]] });
            populate.Invoke(window, null);
            Pump();
            Check(ReferenceEquals(list.ItemsSource, collection) && ReferenceEquals(list.SelectedItem, replacement)
                && ReferenceEquals(containers[0], list.ItemContainerGenerator.ContainerFromItem(profiles[0]))
                && ReferenceEquals(containers[2], list.ItemContainerGenerator.ContainerFromItem(profiles[2]))
                && summary.Text.Contains(replacement.Name, StringComparison.Ordinal),
                "Same-name record replacement updates selection/summary without replacing unrelated containers");
            CheckKeyboardFocus((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(replacement),
                "Replacing the focused profile forwards focus only after collection mutation finishes");
            ClickSettings(window, "DeleteAudioProfileButton");
            Check(list.Items.Count == 2 && ReferenceEquals(list.SelectedItem, profiles[0])
                && summary.Text.Contains(profiles[0].Name, StringComparison.Ordinal),
                "Deleting the focused middle profile selects and describes the existing first-item fallback");
            CheckKeyboardFocus((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(profiles[0]),
                "Deleting the focused profile leaves focus on the selected surviving item");
            ClickSettings(window, "ResetProfilesSettingsButton");
            Check(ReferenceEquals(list.ItemsSource, collection) && list.Items.Count == 0 && list.SelectedItem is null,
                "Resetting a focused profile list clears it without reentrant layout or collection replacement");
            CheckKeyboardFocus(list, "Empty profile reset retains keyboard focus on the list, not a removed container");
            Check(!File.Exists(store.FilePath) && preferences.AudioProfiles.SequenceEqual(profiles),
                "Focused profile mutation regressions do not save or change the input preferences");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyNativeListKeyboard(ListBox list)
    {
        Check(list.Items.Count >= 2 && list.IsTextSearchEnabled, "Native data list supports bounded keyboard/typeahead checks");
        int originalIndex = list.SelectedIndex;
        try
        {
            list.SelectedIndex = 0;
            list.ScrollIntoView(list.Items[0]);
            list.UpdateLayout();
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            FocusForKeyboardCheck(first);
            RaiseLocalKey(first, Key.Down);
            var next = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            Check(list.SelectedIndex == 1, "Native list Down updates selection: " + list.SelectedIndex);
            CheckKeyboardFocus(next, "Native list Down focuses the next item");
            Check(GetAccessiblePeer(next).GetName() == NativeListCaption(list.Items[1]),
                "Native list Down exposes the next name: " + GetAccessiblePeer(next).GetName());
            RaiseLocalKey(next, Key.Up);
            Check(list.SelectedIndex == 0 && first.IsKeyboardFocused
                && GetAccessiblePeer(first).GetName() == NativeListCaption(list.Items[0]),
                "Native list Up returns to the original named item");
            RaiseNativeListText(first, NativeListCaption(list.Items[list.Items.Count - 1]));
            Check(list.SelectedIndex == list.Items.Count - 1, "Native list typeahead uses Name rather than a record dump");
        }
        finally { list.SelectedIndex = originalIndex; Pump(); }
    }

    private static void VerifyNativeMainLists(string language)
    {
        string fixtureName = "native-list-main-" + language;
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, fixtureName));
        Check(store.TrySave(new UiPreferences { Language = language, TextScale = 135, FirstRunCompleted = true }, out string? error),
            "Seed isolated main-list preferences: " + error);
        using var transport = new FakeTransport();
        MainWindow window = CreateWindow(transport, fixtureName);
        try
        {
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("ReceiverTab");
            Pump();
            var receivers = (ListBox)window.FindName("ReceiverList");
            VerifyNativeItemPresentation(receivers, Localization.Text("Main.Receiver.DiscoveredName"));
            Check(receivers.DisplayMemberPath == nameof(ReceiverInfo.Name)
                && TabContentElements((TabItem)window.FindName("ReceiverTab")).OfType<ListBox>().SequenceEqual(new[] { receivers }),
                "The single physical receiver list retains the native Name display path without extra item styles");
            window.Receivers.Add(new ReceiverInfo("Zulu simulated receiver", "simulated://native-list-second", 0, 64));
            VerifyNativeListKeyboard(receivers);
            receivers.SelectedIndex = 0;
            var receiverSource = receivers.ItemsSource;
            object[] receiverItems = receivers.Items.Cast<object>().ToArray();
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
            RunTask(window.ConnectAsync());
            var snapshots = new List<NativeMainListSnapshot>();
            foreach (SettingRowViewModel setting in window.AudioSettings.Concat(window.DeviceSettings))
            {
                ComboBox choice = FindById<ComboBox>(window, setting.Id + ".Choice");
                VerifyNativeItemPresentation(choice, setting.ChoiceName);
                Binding? itemsBinding = BindingOperations.GetBinding(choice, ItemsControl.ItemsSourceProperty);
                Binding? selectionBinding = BindingOperations.GetBinding(choice, System.Windows.Controls.Primitives.Selector.SelectedIndexProperty);
                Check(itemsBinding?.Path.Path == nameof(SettingRowViewModel.Options) && itemsBinding.Converter is not null
                    && setting.Options is ICollection<string> { IsReadOnly: true },
                    "Actual main choice converts immutable Options to native items: " + setting.Id);
                Check(selectionBinding?.Path.Path == nameof(SettingRowViewModel.SelectedIndex) && selectionBinding.Mode == BindingMode.TwoWay,
                    "Main choice preserves the SelectedIndex binding: " + setting.Id);
                VerifyNativeComboItems(choice, setting.Options.Select((text, index) => ((object)index, text)).ToArray());
                snapshots.Add(new(setting, choice, choice.ItemsSource, choice.Items.Cast<object>().ToArray(),
                    setting.Options, setting.SelectedIndex, setting.ConfirmedValue));
                if (choice.IsEnabled) VerifyNativeComboKeyboard(choice);
            }
            int writes = CountMockWrites(transport);
            for (int pass = 0; pass < 2; pass++)
            {
                RunTask(window.RefreshAsync());
                VerifyNativeMainIdentity(window, snapshots, "unchanged refresh");
                RunTask(Poll(window));
                VerifyNativeMainIdentity(window, snapshots, "unchanged poll");
                Check(receivers.IsEnabled && ReferenceEquals(receivers, window.FindName("ReceiverList"))
                    && ReferenceEquals(receivers.ItemsSource, receiverSource),
                    "Status refresh and polling preserve the enabled native receiver control and collection");
                VerifyNativeItemIdentity(receivers, receiverItems, "unchanged receiver status poll");
            }
            NativeMainListSnapshot gain = snapshots.Single(snapshot => snapshot.Setting.Id == "Gain");
            gain.Choice.SelectedIndex = (gain.SelectedIndex + 1) % gain.Items.Length;
            FocusForKeyboardCheck(gain.Choice);
            RunTask(Poll(window));
            Check(gain.Setting.IsDirty && gain.Setting.SelectedIndex == gain.Choice.SelectedIndex
                && gain.Setting.SelectedIndex != gain.SelectedIndex && gain.Setting.ConfirmedValue == gain.ConfirmedValue
                && FindById<TextBlock>(window, "Gain.StaticCurrent").Text == gain.Setting.StaticValueText && gain.Choice.IsKeyboardFocused,
                "Polling preserves a native-item draft, current readout and focus without applying it");
            VerifyNativeItemIdentity(gain.Choice, gain.Items, "poll with a pending draft");
            RunTask(window.RefreshAsync());
            VerifyNativeMainIdentity(window, snapshots, "explicit draft discard");
            Check(!gain.Setting.IsDirty && CountMockWrites(transport) == writes,
                "Native navigation/poll/refresh never writes hardware settings with auto-apply disabled");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyNativeMainIdentity(MainWindow window, IEnumerable<NativeMainListSnapshot> snapshots, string operation)
    {
        foreach (NativeMainListSnapshot snapshot in snapshots)
        {
            ComboBox choice = FindById<ComboBox>(window, snapshot.Setting.Id + ".Choice");
            Check(ReferenceEquals(choice, snapshot.Choice) && ReferenceEquals(choice.ItemsSource, snapshot.Source)
                && ReferenceEquals(snapshot.Setting.Options, snapshot.Options),
                "Main control, converter output and Options survive " + operation + ": " + snapshot.Setting.Id);
            VerifyNativeItemIdentity(choice, snapshot.Items, operation);
            Check(choice.SelectedIndex == snapshot.SelectedIndex && snapshot.Setting.SelectedIndex == snapshot.SelectedIndex
                && snapshot.Setting.ConfirmedValue == snapshot.ConfirmedValue
                && FindById<TextBlock>(window, snapshot.Setting.Id + ".StaticCurrent").Text == snapshot.Setting.StaticValueText,
                "Main selection and current text survive " + operation + ": " + snapshot.Setting.Id);
            if (choice.SelectedIndex >= 0) VerifyNativeSelectedText(choice);
        }
    }

    private sealed record NativeMainListSnapshot(SettingRowViewModel Setting, ComboBox Choice, object? Source,
        object[] Items, IReadOnlyList<string> Options, int SelectedIndex, string ConfirmedValue);

    private static void VerifyNativeAudioInputList(string language)
    {
        var source = new FakeAudioMeterSource();
        var window = new AudioMeterWindow(() => null, source,
            message => throw new InvalidOperationException("Unexpected fake audio cleanup error: " + message));
        try
        {
            window.ApplyPreferences(new UiPreferences { Language = language, TextScale = 135 });
            ShowNativeListWindow(window);
            ComboBox choice = FindById<ComboBox>(window, "AudioInputChoice");
            VerifyNativeItemPresentation(choice, Localization.Text("Meter.InputName"));
            Check(choice.DisplayMemberPath == nameof(AudioInput.Name) && choice.Items.Cast<object>().All(item => item is AudioInput),
                "Audio inputs remain generated Name data items, not explicitly styled containers");
            VerifyNativeComboKeyboard(choice);
            Check(source.Starts == 0, "Audio-input navigation never starts capture");
        }
        finally { window.Close(); Pump(); }
        Check(source.Disposed, "The fake audio source is disposed when its fixture closes");
    }

    private static void VerifyNativeItemIdentity(ItemsControl control, object[] expected, string operation)
    {
        Check(control.Items.Count == expected.Length
            && expected.Select((item, index) => ReferenceEquals(item, control.Items[index])).All(same => same),
            "Item instances survive " + operation + ": " + AutomationProperties.GetAutomationId(control));
    }
}
