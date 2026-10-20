using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using LarkControl.Core;

namespace LarkControl.Windows;

internal abstract class AuxiliaryWindow : Window
{
    protected DockPanel Body { get; } = new();
    protected StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    protected Button CloseButton { get; }
    protected UIElement? InitialFocus { get; set; }
    protected virtual bool CloseOnEscape => true;
    private readonly TextBlock result;
    private bool showOperationMessages = true;
    private bool hasResult;
    private bool resultIsOptional;
    private bool resultCanAnnounce;

    protected AuxiliaryWindow(string title, string identifier)
    {
        Title = title;
        Name = identifier;
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetAutomationId(this, identifier);
        NameScope.SetNameScope(this, new NameScope());
        Width = 840;
        Height = 760;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ThemeMode = ThemeMode.Light;
        Language = XmlLanguage.GetLanguage(Localization.Culture.IetfLanguageTag);
        Resources = UiSupport.CreateResources();
        Localization.ApplyResources(Resources);
        SetResourceReference(FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
        SetResourceReference(FontSizeProperty, SystemFonts.MessageFontSizeKey);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(Body);
        result = StaticReadout("SectionResultReadout", Localization.Text("Section.Result.Name"));
        result.MinHeight = 45;
        result.VerticalAlignment = VerticalAlignment.Center;
        result.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Off);
        Grid.SetRow(result, 1);
        layout.Children.Add(result);
        Grid.SetRow(Actions, 2);
        layout.Children.Add(Actions);
        CloseButton = Button("CloseSectionButton", Localization.Text("Section.Close.Label"), Localization.Text("Section.Close.Name"));
        CloseButton.SetResourceReference(ContentControl.ContentProperty, "Section.Close.Label");
        CloseButton.SetResourceReference(AutomationProperties.NameProperty, "Section.Close.Name");
        CloseButton.Click += (_, _) => Close();
        Actions.Children.Add(CloseButton);
        Content = layout;
        ContentRendered += (_, _) => { if (IsActive) InitialFocus?.Focus(); };
        KeyDown += (_, eventArgs) =>
        {
            if (!CloseOnEscape || eventArgs.Key != Key.Escape || eventArgs.Handled) return;
            eventArgs.Handled = true;
            Close();
        };
    }

    protected TControl Named<TControl>(TControl control, string identifier, string accessibleName) where TControl : FrameworkElement
    {
        control.Name = identifier;
        RegisterName(identifier, control);
        AutomationProperties.SetAutomationId(control, identifier);
        AutomationProperties.SetName(control, accessibleName);
        return control;
    }

    protected TextBox Readout(string identifier, string accessibleName) => Named(new TextBox
    {
        Style = (Style)FindResource("ReadoutStyle"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top
    }, identifier, accessibleName);

    protected TextBlock StaticReadout(string identifier, string accessibleName) => Named(new TextBlock
    {
        Style = (Style)FindResource("FocusableReadoutStyle")
    }, identifier, accessibleName);

    protected Button Button(string identifier, string content, string accessibleName) =>
        Named(new Button { Content = content }, identifier, accessibleName);

    protected static Label Label(string content, UIElement target)
    {
        var label = new Label { Content = content, Target = target };
        string accessibleName = AutomationProperties.GetName(target);
        AutomationProperties.SetName(label, string.IsNullOrWhiteSpace(accessibleName) ? content.Replace("_", string.Empty) : accessibleName);
        AutomationProperties.SetLabeledBy(target, label);
        return label;
    }

    internal void ApplyPreferences(UiPreferences preferences)
    {
        preferences.Validate();
        ThemeMode requestedTheme = preferences.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.Light
        };
        if (ThemeMode != requestedTheme) ThemeMode = requestedTheme;
        FontSize = SystemFonts.MessageFontSize * preferences.TextScale / 100.0;
        UsageHints.SetEnabled(this, preferences.ShowHints);
        ApplyOperationMessagePreference(preferences.ShowOperationMessages);
    }

    internal void ShowResult(string message, bool important = true, bool announce = true, bool optional = false)
    {
        hasResult = true;
        resultIsOptional = optional && !important;
        resultCanAnnounce = important || announce;
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Off);
        UiSupport.UpdateReadout(result, message);
        AutomationProperties.SetName(result, Localization.Text("Section.Result.WithMessage", message));
        UpdateResultVisibility();
        if (result.Visibility == Visibility.Visible && IsActive && resultCanAnnounce) UiSupport.Announce(result);
    }

    protected void ApplyOperationMessagePreference(bool enabled)
    {
        showOperationMessages = enabled;
        UpdateResultVisibility();
    }

    protected void ClearResult()
    {
        hasResult = false;
        result.Text = string.Empty;
        AutomationProperties.SetName(result, Localization.Text("Section.Result.Name"));
        UpdateResultVisibility();
    }

    private void UpdateResultVisibility()
    {
        bool visible = hasResult && (!resultIsOptional || showOperationMessages);
        AutomationProperties.SetLiveSetting(result, visible && resultCanAnnounce ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        result.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}

internal sealed partial class SettingsWindow : AuxiliaryWindow
{
    private readonly UiPreferences baseline;
    private readonly UiPreferencesStore store;
    private readonly Action<UiPreferences, string?> saved;
    private readonly Func<UiPreferences, string?>? applyPreferences;
    private readonly Func<A1Status?>? readStatus;
    private readonly Func<Task<A1Status?>>? refreshStatus;
    private readonly Func<AudioProfile, Task<string?>>? applyProfile;
    private readonly Func<bool>? canUseReceiver;
    private readonly Func<long>? receiverGeneration;
    private readonly Func<string, string, bool> confirm;
    private readonly List<Action<UiPreferences>> populateControls = [];
    private readonly TabControl tabs = new();
    private readonly TextBox profileName = new()
    {
        MaxLength = AudioProfile.MaximumNameLength, MinHeight = 30, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(3)
    };
    private readonly ListBox profileList = new() { MinHeight = 100, MaxHeight = 240, Margin = new Thickness(3) };
    private readonly ObservableCollection<AudioProfile> profileEntries = [];
    private readonly TextBlock profileSummary;
    private readonly TextBlock languageRestartNotice;
    private readonly Button saveProfileButton;
    private readonly Button deleteProfileButton;
    private readonly Button applyProfileButton;
    private UiPreferences draft;
    private bool populating;
    private bool applyingProfile;
    private bool committing;
    private bool confirming;
    private bool accepted;
    private UiPreferences? committedPreferences;
    private string? commitWarning;
    private long profileFocusInteraction;

    internal SettingsWindow(UiPreferences preferences, UiPreferencesStore store, Action<UiPreferences, string?> saved)
        : this(preferences, store, saved, null, null, null, null)
    {
    }

    internal SettingsWindow(UiPreferences preferences, UiPreferencesStore store, Action<UiPreferences, string?> saved,
        Func<UiPreferences, string?>? apply = null, Func<A1Status?>? readStatus = null,
        Func<AudioProfile, Task<string?>>? applyProfile = null, Func<string, string, bool>? confirm = null,
        Func<Task<A1Status?>>? refreshStatus = null, Func<bool>? canUseReceiver = null,
        Func<long>? receiverGeneration = null)
        : base(Localization.Text("Settings.Title"), "SettingsWindow")
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(saved);
        preferences.Validate();
        baseline = Copy(preferences);
        draft = Copy(preferences);
        this.store = store;
        this.saved = saved;
        applyPreferences = apply;
        this.readStatus = readStatus;
        this.refreshStatus = refreshStatus;
        this.applyProfile = applyProfile;
        this.canUseReceiver = canUseReceiver;
        this.receiverGeneration = receiverGeneration;
        this.confirm = confirm ?? ((message, title) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        ApplyPreferences(preferences);

        profileList.Style = (Style)FindResource("DeviceListStyle");
        profileList.DisplayMemberPath = nameof(AudioProfile.Name);
        profileList.ItemsSource = profileEntries;
        profileList.GotKeyboardFocus += OnProfileListKeyboardFocus;
        TextSearch.SetTextPath(profileList, nameof(AudioProfile.Name));
        profileSummary = StaticReadout("AudioProfileSummaryReadout", Localization.Text("Settings.Profiles.SummaryName"));
        languageRestartNotice = StaticReadout("SettingsLanguageRestartNotice", Localization.Text("Settings.Language.RestartNotice"));
        languageRestartNotice.Text = Localization.Text("Settings.Language.RestartNotice");
        saveProfileButton = SettingsButton("SaveAudioProfileButton", Localization.Text("Settings.Profiles.Save.Label"), Localization.Text("Settings.Profiles.Save.Name"));
        deleteProfileButton = SettingsButton("DeleteAudioProfileButton", Localization.Text("Settings.Profiles.Delete.Label"), Localization.Text("Settings.Profiles.Delete.Name"));
        applyProfileButton = SettingsButton("ApplyAudioProfileButton", Localization.Text("Settings.Profiles.Apply.Label"), Localization.Text("Settings.Profiles.Apply.Name"));

        Named(tabs, "SettingsTabs", string.Empty);
        UsageHints.SetText(tabs, Localization.Text("Settings.Categories.Name"));
        BuildAppearance();
        BuildMainScreen();
        BuildControls();
        BuildStartupAndClosing();
        BuildProfiles();
        BuildHotkeys();

        Body.Children.Add(tabs);
        CloseButton.Visibility = Visibility.Collapsed;
        Button save = Button("SaveSettingsButton", Localization.Text("Settings.Save.Label"), Localization.Text("Settings.Save.Name"));
        save.IsDefault = true;
        save.Click += (_, _) => SaveSettings();
        Button cancel = Button("CancelSettingsButton", Localization.Text("Settings.Cancel.Label"), Localization.Text("Settings.Cancel.Name"));
        cancel.Click += (_, _) => Close();
        Actions.Children.Add(save);
        Actions.Children.Add(cancel);
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            if (committedPreferences is not { } candidate) return;
            committedPreferences = null;
            saved(candidate, commitWarning);
        };
        PreviewKeyDown += (_, _) => profileFocusInteraction++;
        PreviewMouseDown += (_, _) => profileFocusInteraction++;
        Deactivated += (_, _) => profileFocusInteraction++;
        PopulateControls();
        InitialFocus = (UIElement)FindName("SettingsLanguageChoice");
    }

    private void BuildAppearance()
    {
        StackPanel panel = Category("Appearance", Localization.Text("Settings.Appearance.Label"), Localization.Text("Settings.Appearance.Name"));
        Choice(panel, "SettingsLanguageChoice", Localization.Text("Settings.Language.Label"), Localization.Text("Settings.Language.Name"),
            [new("pl", Localization.Text("Settings.Language.Polish")), new("en", Localization.Text("Settings.Language.English"))],
            preferences => preferences.Language, (preferences, value) => preferences with { Language = value });
        panel.Children.Add(languageRestartNotice);
        Choice(panel, "SettingsThemeChoice", Localization.Text("Settings.Theme.Label"), Localization.Text("Settings.Theme.Name"),
            [new("Light", Localization.Text("Settings.Theme.Light")), new("Dark", Localization.Text("Settings.Theme.Dark"))],
            preferences => preferences.Theme, (preferences, value) => preferences with { Theme = value });
        Choice(panel, "SettingsTextScaleChoice", Localization.Text("Settings.TextScale.Label"), Localization.Text("Settings.TextScale.Name"),
            new[] { 100, 125, 150, 175, 200, draft.TextScale }.Distinct().Order()
                .Select(value => new SettingsChoice<int>(value, Localization.Text("Settings.TextScale.Percentage", value))).ToArray(),
            preferences => preferences.TextScale, (preferences, value) => preferences with { TextScale = value }, useDataItems: true);
        AddReset(panel, "Appearance", Localization.Text("Settings.Appearance.ResetScope"));
    }

    private void BuildMainScreen()
    {
        StackPanel panel = Category("MainScreen", Localization.Text("Settings.MainScreen.Label"), Localization.Text("Settings.MainScreen.Name"));
        var heading = Named(new TextBlock
        {
            Style = (Style)FindResource("HintStyle"), Text = Localization.Text("Settings.MainScreen.Heading"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        }, "SettingsVisibleRowsHeading", Localization.Text("Settings.MainScreen.Heading"));
        panel.Children.Add(heading);
        (string Id, string Label)[] rows =
        [
            ("Microphones", Localization.Text("Settings.Rows.Microphones")),
            ("TX1", Localization.Text("Settings.Rows.TX1")), ("TX2", Localization.Text("Settings.Rows.TX2")),
            ("TX3", Localization.Text("Settings.Rows.TX3")), ("TX4", Localization.Text("Settings.Rows.TX4")),
            ("Gain", Localization.Text("Settings.Rows.Gain")),
            ("Noise", Localization.Text("Settings.Rows.Noise")), ("Equalizer", Localization.Text("Settings.Rows.Equalizer")),
            ("Reverb", Localization.Text("Settings.Rows.Reverb")), ("ChannelMode", Localization.Text("Settings.Rows.ChannelMode")),
            ("Lights", Localization.Text("Settings.Rows.Lights")), ("AutoOff", Localization.Text("Settings.Rows.AutoOff")),
            ("Identification", Localization.Text("Settings.Rows.Identification"))
        ];
        foreach ((string identifier, string label) in rows)
            Toggle(panel, "Show" + identifier + "RowItem", label,
                preferences => !preferences.HiddenRows.Contains(identifier, StringComparer.Ordinal),
                (preferences, visible) => preferences with
                {
                    HiddenRows = visible ? preferences.HiddenRows.Where(row => row != identifier).ToArray()
                        : preferences.HiddenRows.Where(row => row != identifier).Append(identifier).ToArray()
                }, verticalMargin: 6);
        AddReset(panel, "MainScreen", Localization.Text("Settings.MainScreen.ResetScope"));
    }

    private void BuildControls()
    {
        StackPanel panel = Category("Controls", Localization.Text("Settings.Controls.Label"), Localization.Text("Settings.Controls.Name"));
        Choice(panel, "GainControlModeChoice", Localization.Text("Settings.GainMode.Label"), Localization.Text("Settings.GainMode.Name"),
            [new(GainControlMode.Choice, Localization.Text("Settings.GainMode.Choice")), new(GainControlMode.Buttons, Localization.Text("Settings.GainMode.Buttons")), new(GainControlMode.Slider, Localization.Text("Settings.GainMode.Slider"))],
            preferences => preferences.GainControlMode, (preferences, value) => preferences with { GainControlMode = value });
        Toggle(panel, "AutoApplySelectionsCheckBox", Localization.Text("Settings.AutoApply.Label"),
            preferences => preferences.AutoApplySelections, (preferences, value) => preferences with { AutoApplySelections = value });
        Toggle(panel, "ShowHintsCheckBox", Localization.Text("Settings.Hints.Label"), preferences => preferences.ShowHints,
            (preferences, value) => preferences with { ShowHints = value });
        Toggle(panel, "ShowOperationMessagesCheckBox", Localization.Text("Settings.Messages.Label"),
            preferences => preferences.ShowOperationMessages, (preferences, value) => preferences with { ShowOperationMessages = value }, verticalMargin: 8);
        Choice(panel, "ValueDisplayModeChoice", Localization.Text("Settings.ValueDisplay.Label"), Localization.Text("Settings.ValueDisplay.Name"),
            [new(ValueDisplayMode.ReadOnlyFields, Localization.Text("Settings.ValueDisplay.ReadOnlyFields")),
                new(ValueDisplayMode.StaticText, Localization.Text("Settings.ValueDisplay.StaticText")), new(ValueDisplayMode.None, Localization.Text("Settings.ValueDisplay.None"))],
            preferences => preferences.ValueDisplayMode,
            (preferences, value) => preferences with { ValueDisplayMode = value });
        Toggle(panel, "AnnounceStateChangesCheckBox", Localization.Text("Settings.Announcements.Label"),
            preferences => preferences.AnnounceStateChanges, (preferences, value) => preferences with { AnnounceStateChanges = value }, verticalMargin: 8);
        Toggle(panel, "PreserveVolatileSettingsCheckBox", Localization.Text("Settings.Retention.Label"),
            preferences => preferences.PreserveVolatileSettings,
            (preferences, value) => preferences with { PreserveVolatileSettings = value }, verticalMargin: 8);
        AddReset(panel, "Controls", Localization.Text("Settings.Controls.ResetScope"));
    }

    private void BuildStartupAndClosing()
    {
        StackPanel panel = Category("StartupAndClosing", Localization.Text("Settings.StartupAndClosing.Label"), Localization.Text("Settings.StartupAndClosing.Name"));
        Toggle(panel, "StartWithWindowsCheckBox", Localization.Text("Settings.Startup.WithWindows"),
            preferences => preferences.StartWithWindows, (preferences, value) => preferences with { StartWithWindows = value });
        Toggle(panel, "CloseToTrayCheckBox", Localization.Text("Settings.Closing.ToTray"),
            preferences => preferences.CloseToTray, (preferences, value) => preferences with { CloseToTray = value });
        Toggle(panel, "ConfirmExitCheckBox", Localization.Text("Settings.Closing.ConfirmExit"),
            preferences => preferences.ConfirmExit, (preferences, value) => preferences with { ConfirmExit = value });
        AddReset(panel, "StartupAndClosing", Localization.Text("Settings.StartupAndClosing.ResetScope"));
    }

    private void BuildProfiles()
    {
        StackPanel panel = Category("Profiles", Localization.Text("Settings.Profiles.Label"), Localization.Text("Settings.Profiles.Name"));
        Named(profileList, "AudioProfilesList", Localization.Text("Settings.Profiles.ListName"));
        profileList.SelectionChanged += (_, _) => { if (!populating) UpdateProfileSelection(); };
        panel.Children.Add(Label(Localization.Text("Settings.Profiles.Label"), profileList));
        panel.Children.Add(profileList);
        Named(profileName, "AudioProfileNameTextBox", Localization.Text("Settings.Profiles.NewName.Name"));
        panel.Children.Add(Label(Localization.Text("Settings.Profiles.NewName.Label"), profileName));
        panel.Children.Add(profileName);
        var profileActions = new WrapPanel();
        saveProfileButton.Click += async (_, _) => await CaptureProfileAsync();
        deleteProfileButton.Click += (_, _) => DeleteProfile();
        applyProfileButton.Click += async (_, _) => await ApplySelectedProfileAsync();
        profileActions.Children.Add(saveProfileButton);
        profileActions.Children.Add(applyProfileButton);
        profileActions.Children.Add(deleteProfileButton);
        panel.Children.Add(profileActions);
        panel.Children.Add(Label(Localization.Text("Settings.Profiles.SummaryLabel"), profileSummary));
        panel.Children.Add(profileSummary);
        var warning = StaticReadout("AudioProfileWarning", Localization.Text("Settings.Profiles.Warning"));
        warning.Text = Localization.Text("Settings.Profiles.Warning");
        panel.Children.Add(warning);
        if (readStatus is null || applyProfile is null)
        {
            var availability = StaticReadout("AudioProfileAvailabilityReadout", Localization.Text("Settings.Profiles.UnavailableHint"));
            availability.Text = Localization.Text("Settings.Profiles.UnavailableHint");
            panel.Children.Add(availability);
        }
        AddReset(panel, "Profiles", Localization.Text("Settings.Profiles.ResetScope"));
        Button resetAll = SettingsButton("ResetAllSettingsButton", Localization.Text("Settings.ResetAll.Label"), Localization.Text("Settings.ResetAll.Name"));
        resetAll.HorizontalAlignment = HorizontalAlignment.Left;
        resetAll.Margin = new Thickness(3, 12, 3, 3);
        resetAll.Click += (_, _) => ResetCategory("All", Localization.Text("Settings.ResetAll.Scope"));
        panel.Children.Add(resetAll);
    }

    private StackPanel Category(string scope, string header, string accessibleName)
    {
        var panel = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(12)
        };
        var tab = Named(new TabItem
        {
            Header = new AccessText { Text = header, TextWrapping = TextWrapping.Wrap }, Content = scroll
        }, scope + "SettingsTab", accessibleName);
        tabs.Items.Add(tab);
        return panel;
    }

    private Button SettingsButton(string identifier, string text, string accessibleName)
    {
        Button button = Button(identifier, text, accessibleName);
        button.Content = new AccessText { Text = text, TextWrapping = TextWrapping.Wrap };
        return button;
    }

    private void Toggle(Panel panel, string identifier, string text, Func<UiPreferences, bool> read,
        Func<UiPreferences, bool, UiPreferences> change, double verticalMargin = 10)
    {
        string label = text.Replace("_", string.Empty);
        var control = Named(new CheckBox
        {
            Content = text, Margin = new Thickness(3, verticalMargin, 3, verticalMargin)
        }, identifier, label);
        control.Checked += (_, _) => ChangePreferences(preferences => change(preferences, true));
        control.Unchecked += (_, _) => ChangePreferences(preferences => change(preferences, false));
        populateControls.Add(preferences => control.IsChecked = read(preferences));
        panel.Children.Add(control);
    }

    private void Choice<TValue>(Panel panel, string identifier, string label, string accessibleName,
        SettingsChoice<TValue>[] choices, Func<UiPreferences, TValue> read, Func<UiPreferences, TValue, UiPreferences> change,
        bool useDataItems = false)
    {
        var control = Named(new ComboBox
        {
            SelectedValuePath = useDataItems ? nameof(SettingsChoice<TValue>.Value) : nameof(FrameworkElement.Tag)
        }, identifier, accessibleName);
        if (useDataItems)
        {
            control.ItemsSource = choices;
            control.DisplayMemberPath = nameof(SettingsChoice<TValue>.Label);
        }
        else
        {
            foreach (SettingsChoice<TValue> choice in choices)
                control.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        }
        TextSearch.SetTextPath(control, useDataItems ? nameof(SettingsChoice<TValue>.Label) : nameof(ContentControl.Content));
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is TValue value)
                ChangePreferences(preferences => change(preferences, value));
        };
        populateControls.Add(preferences => control.SelectedValue = read(preferences));
        panel.Children.Add(Label(label, control));
        panel.Children.Add(control);
    }

    private void AddReset(Panel panel, string scope, string title)
    {
        Button reset = SettingsButton("Reset" + scope + "SettingsButton", Localization.Text("Settings.ResetCategory.Label"),
            Localization.Text("Settings.ResetCategory.Name", title));
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(3, 12, 3, 3);
        reset.Click += (_, _) => ResetCategory(scope, title);
        panel.Children.Add(reset);
    }

    private void ChangePreferences(Func<UiPreferences, UiPreferences> change)
    {
        if (populating || applyingProfile || committing || confirming) return;
        draft = change(draft);
        UsageHints.SetEnabled(this, draft.ShowHints);
        ApplyOperationMessagePreference(draft.ShowOperationMessages);
        UpdateLanguageRestartNotice();
    }

    private void PopulateControls()
    {
        populating = true;
        try
        {
            foreach (Action<UiPreferences> populate in populateControls) populate(draft);
            UsageHints.SetEnabled(this, draft.ShowHints);
            ApplyOperationMessagePreference(draft.ShowOperationMessages);
            UpdateLanguageRestartNotice();
            string? selected = (profileList.SelectedItem as AudioProfile)?.Name;
            for (int index = profileEntries.Count - 1; index >= 0; index--)
            {
                if (!draft.AudioProfiles.Any(profile => profile.Name == profileEntries[index].Name))
                    profileEntries.RemoveAt(index);
            }
            for (int index = 0; index < draft.AudioProfiles.Length; index++)
            {
                AudioProfile profile = draft.AudioProfiles[index];
                int existingIndex = Enumerable.Range(0, profileEntries.Count)
                    .FirstOrDefault(entryIndex => profileEntries[entryIndex].Name == profile.Name, -1);
                if (existingIndex < 0)
                    profileEntries.Insert(index, profile);
                else if (existingIndex != index)
                    profileEntries.Move(existingIndex, index);
                if (profileEntries[index] != profile)
                    profileEntries[index] = profile;
            }
            AudioProfile? selection = profileEntries.FirstOrDefault(profile => profile.Name == selected)
                ?? profileEntries.FirstOrDefault();
            if (!ReferenceEquals(profileList.SelectedItem, selection))
                profileList.SelectedItem = selection;
            UpdateProfileSelection();
        }
        finally { populating = false; }
        if (profileList.IsKeyboardFocused) FocusSelectedProfile();
    }

    private void UpdateLanguageRestartNotice() => languageRestartNotice.Visibility =
        draft.Language != Localization.CurrentLanguage ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateProfileSelection()
    {
        RefreshReceiverAvailability();
        AudioProfile? profile = profileList.SelectedItem as AudioProfile;
        string summary = Localization.Text("Settings.Profiles.NoneSelected");
        if (profile is not null)
        {
            string equalizer = Localization.Text(profile.Equalizer switch
            {
                1 => "Settings.Profiles.Equalizer.Bass",
                2 => "Settings.Profiles.Equalizer.Bright",
                _ => "Settings.Profiles.Equalizer.Balanced"
            });
            summary = Localization.Text("Settings.Profiles.Summary", profile.Name, profile.Gain + 1,
                Localization.Text(profile.ChannelMode == 0 ? "Settings.Profiles.Channel.Mono" : "Settings.Profiles.Channel.Stereo"),
                Localization.Text(profile.NoiseEnabled ? "Settings.Profiles.Noise.Enabled" : "Settings.Profiles.Noise.Disabled"),
                profile.NoiseLevel + 1, equalizer,
                Localization.Text(profile.ReverbEnabled ? "Settings.Profiles.Reverb.Enabled" : "Settings.Profiles.Reverb.Disabled"),
                profile.ReverbLevel + 1);
        }
        UiSupport.UpdateReadout(profileSummary, summary);
        AutomationProperties.SetName(profileSummary, summary);
    }

    internal void RefreshReceiverAvailability()
    {
        bool available = !applyingProfile && (canUseReceiver?.Invoke() ?? true);
        saveProfileButton.IsEnabled = available && readStatus is not null;
        deleteProfileButton.IsEnabled = profileList.SelectedItem is AudioProfile;
        applyProfileButton.IsEnabled = available && profileList.SelectedItem is AudioProfile && applyProfile is not null;
    }

    private bool EnsureReceiverAvailable()
    {
        if (canUseReceiver?.Invoke() != false) return true;
        RefreshReceiverAvailability();
        ShowResult(Localization.Text("Settings.Profiles.ReceiverUnavailable"));
        return false;
    }

    private (UIElement? Element, long Interaction) CaptureProfileFocus() =>
        (IsActive && Keyboard.FocusedElement is UIElement element && Window.GetWindow(element) == this
            ? element : null, profileFocusInteraction);

    private void RestoreProfileFocus((UIElement? Element, long Interaction) previous)
    {
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsActive || applyingProfile || previous.Interaction != profileFocusInteraction
                || previous.Element is not { IsVisible: true, IsEnabled: true, Focusable: true } target) return;
            if (Keyboard.FocusedElement is UIElement current && current != this && current != target
                && current.IsVisible && current.IsEnabled && current.Focusable) return;
            target.Focus();
        }));
    }

    private void OnProfileListKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (populating || sender is not ListBox list) return;
        if (eventArgs.NewFocus is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == list)
        {
            item.IsSelected = true;
            return;
        }
        if (eventArgs.NewFocus == list) FocusSelectedProfile();
    }

    private void FocusSelectedProfile()
    {
        if (!profileList.HasItems) return;
        object entry = profileList.SelectedItem ?? profileList.Items[0];
        profileList.ScrollIntoView(entry);
        profileList.UpdateLayout();
        if (profileList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem target) target.Focus();
    }

    private void ResetCategory(string scope, string title)
    {
        if (applyingProfile || committing || confirming) return;
        string message = Localization.Text("Settings.Reset.Confirmation", title);
        if (scope is "Profiles" or "All") message += Localization.Text("Settings.Reset.ProfilesNotice");
        if (!Confirm(message, Localization.Text("Settings.Reset.Title"))) return;
        var defaults = new UiPreferences();
        draft = scope switch
        {
            "Appearance" => draft with { Theme = defaults.Theme, TextScale = defaults.TextScale },
            "MainScreen" => draft with { HiddenRows = [] },
            "Controls" => draft with
            {
                GainControlMode = defaults.GainControlMode, ValueDisplayMode = defaults.ValueDisplayMode,
                AutoApplySelections = defaults.AutoApplySelections, ShowHints = defaults.ShowHints,
                ShowOperationMessages = defaults.ShowOperationMessages, AnnounceStateChanges = defaults.AnnounceStateChanges,
                PreserveVolatileSettings = defaults.PreserveVolatileSettings
            },
            "Hotkeys" => draft with { GlobalHotkeysEnabled = defaults.GlobalHotkeysEnabled, Hotkeys = HotkeyBinding.CreateDefaults() },
            "StartupAndClosing" => draft with
            {
                StartWithWindows = defaults.StartWithWindows, CloseToTray = defaults.CloseToTray, ConfirmExit = defaults.ConfirmExit
            },
            "Profiles" => draft with { AudioProfiles = [] },
            "All" => defaults with
            {
                SchemaVersion = draft.SchemaVersion, Language = draft.Language, FirstRunCompleted = draft.FirstRunCompleted,
                AutoConnectAtStartup = draft.AutoConnectAtStartup, PreferredReceiverPath = draft.PreferredReceiverPath
            },
            _ => draft
        };
        if (scope is "Profiles" or "All") profileName.Clear();
        PopulateControls();
        RoutineResult(Localization.Text("Settings.Reset.Done"));
    }

    private async Task CaptureProfileAsync()
    {
        if (applyingProfile || committing || confirming) return;
        long? profileGeneration = receiverGeneration?.Invoke();
        if (!EnsureReceiverAvailable()) return;
        string name = profileName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > AudioProfile.MaximumNameLength || name.Any(char.IsControl))
        {
            ShowResult(Localization.Text("Settings.Profiles.InvalidName"));
            profileName.Focus();
            return;
        }
        if (draft.AudioProfiles.Length >= UiPreferences.MaximumProfiles)
        {
            ShowResult(Localization.Text("Settings.Profiles.LimitReached"));
            return;
        }
        if (draft.AudioProfiles.Any(profile => string.Equals(profile.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowResult(Localization.Text("Settings.Profiles.DuplicateName"));
            profileName.Focus();
            return;
        }
        var previousFocus = CaptureProfileFocus();
        try
        {
            applyingProfile = true;
            Body.IsEnabled = Actions.IsEnabled = false;
            A1Status? status = refreshStatus is not null ? await refreshStatus() : readStatus?.Invoke();
            if (!EnsureReceiverGeneration(profileGeneration)) return;
            if (status is null)
            {
                ShowResult(Localization.Text("Settings.Profiles.NoStatus"));
                return;
            }
            AudioProfile profile = AudioProfile.FromStatus(name, status);
            draft = draft with { AudioProfiles = [.. draft.AudioProfiles, profile] };
            profileName.Clear();
            PopulateControls();
            profileList.SelectedItem = profile;
            RoutineResult(Localization.Text("Settings.Profiles.Added"));
        }
        catch (Exception)
        {
            if (EnsureReceiverGeneration(profileGeneration))
                ShowResult(Localization.Text("Settings.Profiles.CaptureFailed"));
        }
        finally
        {
            applyingProfile = false;
            Body.IsEnabled = Actions.IsEnabled = true;
            UpdateProfileSelection();
            RestoreProfileFocus(previousFocus);
        }
    }

    private void DeleteProfile()
    {
        if (applyingProfile || committing || confirming || profileList.SelectedItem is not AudioProfile profile) return;
        bool deleteHadFocus = deleteProfileButton.IsKeyboardFocusWithin;
        if (!Confirm(Localization.Text("Settings.Profiles.Delete.Confirmation", profile.Name),
            Localization.Text("Settings.Profiles.Delete.Title"))) return;
        draft = draft with { AudioProfiles = draft.AudioProfiles.Where(item => item != profile).ToArray() };
        PopulateControls();
        RoutineResult(Localization.Text("Settings.Profiles.Deleted"));
        if (deleteHadFocus && !deleteProfileButton.IsEnabled && IsActive) profileList.Focus();
    }

    private async Task ApplySelectedProfileAsync()
    {
        if (applyingProfile || committing || confirming || applyProfile is null || profileList.SelectedItem is not AudioProfile profile) return;
        long? profileGeneration = receiverGeneration?.Invoke();
        if (!EnsureReceiverAvailable()) return;
        bool approved = Confirm(Localization.Text("Settings.Profiles.Apply.Confirmation", profile.Name), Localization.Text("Settings.Profiles.Apply.Title"));
        if (!EnsureReceiverGeneration(profileGeneration) || !approved) return;
        if (!EnsureReceiverAvailable()) return;
        var previousFocus = CaptureProfileFocus();
        applyingProfile = true;
        Body.IsEnabled = Actions.IsEnabled = false;
        ShowResult(Localization.Text("Settings.Profiles.Applying"), false, announce: true);
        try
        {
            profile.Validate();
            if (!EnsureReceiverGeneration(profileGeneration)) return;
            string? error = await applyProfile(profile);
            if (!EnsureReceiverGeneration(profileGeneration)) return;
            if (error is not null)
                ShowResult(error);
            else
                RoutineResult(Localization.Text("Settings.Profiles.Applied"));
        }
        catch (Exception)
        {
            if (EnsureReceiverGeneration(profileGeneration))
                ShowResult(Localization.Text("Settings.Profiles.ApplyFailed"));
        }
        finally
        {
            applyingProfile = false;
            Body.IsEnabled = Actions.IsEnabled = true;
            UpdateProfileSelection();
            RestoreProfileFocus(previousFocus);
        }
    }

    private bool EnsureReceiverGeneration(long? expectedGeneration)
    {
        if (receiverGeneration is null || receiverGeneration() == expectedGeneration) return true;
        ShowResult(Localization.Text("Settings.Profiles.ContextChanged"));
        return false;
    }

    private void SaveSettings()
    {
        if (TryCommitSettings()) Close();
    }

    private bool TryCommitSettings()
    {
        if (applyingProfile || committing || confirming || accepted) return false;
        if (!ValidateHotkeyDraft()) return false;
        if (!string.IsNullOrWhiteSpace(profileName.Text))
        {
            tabs.SelectedItem = FindName("ProfilesSettingsTab");
            ShowResult(Localization.Text("Settings.Save.PendingProfile", profileName.Text.Trim()));
            tabs.UpdateLayout();
            profileName.BringIntoView();
            profileName.Focus();
            return false;
        }
        UiPreferences candidate = Copy(draft);
        string? warning = null;
        committing = true;
        try
        {
            candidate.Validate();
            if (applyPreferences is not null)
            {
                string? error = applyPreferences(candidate);
                if (error is not null)
                {
                    ShowResult(string.IsNullOrWhiteSpace(error) ? Localization.Text("Settings.Save.ApplyFailed") : error);
                    return false;
                }
            }
            else if (!store.TrySave(candidate, out warning))
            {
                ShowResult(warning ?? Localization.Text("Settings.Save.Failed"));
                return false;
            }
        }
        catch (Exception)
        {
            ShowResult(Localization.Text("Settings.Save.Unconfirmed"));
            return false;
        }
        finally { committing = false; }
        accepted = true;
        committedPreferences = candidate;
        commitWarning = warning;
        return true;
    }

    private void HandleClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (accepted) return;
        if (applyingProfile || committing || confirming)
        {
            eventArgs.Cancel = true;
            if (!confirming) ShowResult(Localization.Text("Settings.Closing.Busy"));
            return;
        }
        if (SamePreferences(draft, baseline) && string.IsNullOrWhiteSpace(profileName.Text)) return;
        bool? decision = ConfirmDecision(Localization.Text("Settings.Save.Confirmation"), Localization.Text("Settings.Save.Title"));
        if (decision is false)
            accepted = true;
        else
            eventArgs.Cancel = decision is null || !TryCommitSettings();
    }

    private bool Confirm(string message, string title) => ConfirmDecision(message, title) == true;

    private bool? ConfirmDecision(string message, string title)
    {
        if (confirming) return null;
        confirming = true;
        try { return confirm(message, title); }
        catch (Exception)
        {
            ShowResult(Localization.Text("Settings.Confirmation.Failed"));
            return null;
        }
        finally { confirming = false; }
    }

    private void RoutineResult(string message)
    {
        if (draft.ShowOperationMessages) ShowResult(message, false, announce: true, optional: true);
        else ClearResult();
    }

    private static UiPreferences Copy(UiPreferences preferences) => preferences with
    {
        HiddenRows = [.. preferences.HiddenRows], AudioProfiles = [.. preferences.AudioProfiles], Hotkeys = [.. preferences.Hotkeys]
    };

    private static bool SamePreferences(UiPreferences first, UiPreferences second) =>
        first.HiddenRows.ToHashSet(StringComparer.Ordinal).SetEquals(second.HiddenRows)
        && first.AudioProfiles.SequenceEqual(second.AudioProfiles)
        && first.Hotkeys.SequenceEqual(second.Hotkeys)
        && first with { HiddenRows = Array.Empty<string>(), AudioProfiles = Array.Empty<AudioProfile>(), Hotkeys = Array.Empty<HotkeyBinding>() }
            == second with { HiddenRows = Array.Empty<string>(), AudioProfiles = Array.Empty<AudioProfile>(), Hotkeys = Array.Empty<HotkeyBinding>() };

    private sealed record SettingsChoice<TValue>(TValue Value, string Label)
    {
        public override string ToString() => Label;
    }
}
