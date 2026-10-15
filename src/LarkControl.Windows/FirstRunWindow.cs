using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace LarkControl.Windows;

internal sealed class FirstRunWindow : AuxiliaryWindow
{
    private readonly UiPreferencesStore store;
    private readonly string presentationTheme;
    private readonly List<Action> refreshChoiceText = [];
    private UiPreferences draft;
    private bool committing;
    private readonly ComboBox languageChoice;
    private readonly ComboBox themeChoice;
    private readonly Button continueButton;

    internal UiPreferences? SavedPreferences { get; private set; }
    internal string? SaveWarning { get; private set; }
    protected override bool CloseOnEscape => false;

    internal FirstRunWindow(UiPreferences preferences, UiPreferencesStore store)
        : base(Localization.Text("Setup.Title"), "FirstRunWindow")
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(store);
        preferences.Validate();
        this.store = store;
        presentationTheme = preferences.Theme;
        draft = preferences with { HiddenRows = [.. preferences.HiddenRows], AudioProfiles = [.. preferences.AudioProfiles] };
        SetResourceReference(TitleProperty, "Setup.Title");
        SetResourceReference(AutomationProperties.NameProperty, "Setup.Title");
        Width = 660;
        Height = 590;
        MinWidth = 480;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        var layout = (Grid)Content;
        layout.Margin = new Thickness(20);
        layout.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(Body, 1);
        var setupStatus = (TextBlock)FindName("SectionResultReadout");
        Grid.SetRow(setupStatus, 2);
        setupStatus.MinHeight = 0;
        setupStatus.MaxHeight = double.PositiveInfinity;
        setupStatus.Margin = new Thickness(0, 12, 0, 12);
        setupStatus.Padding = new Thickness(0);
        setupStatus.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(Actions, 3);
        ApplyPreferences(draft);

        var panel = new StackPanel();
        var scope = Named(new TextBlock
        {
            Style = (Style)FindResource("HintStyle"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        }, "FirstRunScope", Localization.Text("Setup.Scope"));
        scope.SetResourceReference(TextBlock.TextProperty, "Setup.Scope");
        scope.SetResourceReference(AutomationProperties.NameProperty, "Setup.Scope");
        layout.Children.Insert(0, scope);

        languageChoice = AddChoice(panel, "FirstRunLanguageChoice", "Settings.Language.Label", "Setup.Language.Name",
            [new("pl", "Settings.Language.Polish"), new("en", "Settings.Language.English")],
            draft.Language, (preferences, value) => preferences with { Language = value });
        languageChoice.SetResourceReference(UsageHints.TextProperty, "Setup.Language.Hint");
        languageChoice.SelectedIndex = -1;
        InitialFocus = languageChoice;
        themeChoice = AddChoice(panel, "FirstRunThemeChoice", "Settings.Theme.Label", "Setup.Theme.Name",
            [new("Light", "Settings.Theme.Light"), new("Dark", "Settings.Theme.Dark")],
            draft.Theme, (preferences, value) => preferences with { Theme = value }, previewPresentation: false);
        themeChoice.SetResourceReference(UsageHints.TextProperty, "Setup.Hint");
        themeChoice.SelectedIndex = -1;
        AddChoice(panel, "FirstRunScaleChoice", "Settings.TextScale.Label", "Settings.TextScale.Name",
            new[] { 100, 125, 150, 175, 200, draft.TextScale }.Distinct().Order()
                .Select(value => new SetupChoice<int>(value, "Settings.TextScale.Percentage", value)).ToArray(),
            draft.TextScale, (preferences, value) => preferences with { TextScale = value }, dataBound: true);
        AddToggle(panel, "FirstRunHintsCheckBox", "Settings.Hints.Label", "Settings.Hints.Name", draft.ShowHints,
            (preferences, value) => preferences with { ShowHints = value },
            "Setup.Hints.Hint", 16);
        AddToggle(panel, "FirstRunMessagesCheckBox", "Settings.Messages.Label", "Settings.Messages.Name", draft.ShowOperationMessages,
            (preferences, value) => preferences with { ShowOperationMessages = value },
            "Setup.Messages.Hint");
        AddChoice(panel, "FirstRunValueDisplayChoice", "Settings.ValueDisplay.Label", "Settings.ValueDisplay.Name",
            [new(ValueDisplayMode.ReadOnlyFields, "Settings.ValueDisplay.ReadOnlyFields"),
                new(ValueDisplayMode.StaticText, "Settings.ValueDisplay.StaticText"), new(ValueDisplayMode.None, "Settings.ValueDisplay.None")],
            draft.ValueDisplayMode,
            (preferences, value) => preferences with { ValueDisplayMode = value });
        AddToggle(panel, "FirstRunAnnouncementsCheckBox", "Settings.Announcements.Label", "Settings.Announcements.Name", draft.AnnounceStateChanges,
            (preferences, value) => preferences with { AnnounceStateChanges = value },
            "Setup.Announcements.Hint");

        Body.Children.Add(new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        });
        CloseButton.Visibility = Visibility.Collapsed;
        continueButton = Button("SaveFirstRunButton", Localization.Text("Setup.Continue.Label"), Localization.Text("Setup.Continue.Name"));
        BindCaption(continueButton, "Setup.Continue.Label", "Setup.Continue.Name");
        continueButton.IsDefault = true;
        continueButton.IsEnabled = false;
        continueButton.Click += (_, _) => SaveSetup();
        Button cancel = Button("CancelFirstRunButton", Localization.Text("Settings.Cancel.Label"), Localization.Text("Setup.Cancel.Name"));
        BindCaption(cancel, "Settings.Cancel.Label", "Setup.Cancel.Name");
        continueButton.SetResourceReference(UsageHints.TextProperty, "Setup.Continue.Hint");
        cancel.SetResourceReference(UsageHints.TextProperty, "Setup.Cancel.Hint");
        cancel.Click += (_, _) => Close();
        Actions.Children.Add(continueButton);
        Actions.Children.Add(cancel);
        languageChoice.SelectionChanged += (_, _) => PreviewLanguage();
        themeChoice.SelectionChanged += (_, _) => UpdateSetupReadiness();
        UpdateSetupReadiness();
        Closing += (_, eventArgs) => { if (committing) eventArgs.Cancel = true; };
    }

    private static void BindCaption(ContentControl control, string labelKey, string accessibleNameKey)
    {
        control.SetResourceReference(ContentControl.ContentProperty, labelKey);
        control.SetResourceReference(AutomationProperties.NameProperty, accessibleNameKey);
    }

    private ComboBox AddChoice<TValue>(Panel panel, string identifier, string labelKey, string accessibleNameKey,
        SetupChoice<TValue>[] choices, TValue selected, Func<UiPreferences, TValue, UiPreferences> change,
        bool dataBound = false, bool previewPresentation = true)
    {
        var control = Named(new ComboBox
        {
            Margin = new Thickness(3)
        }, identifier, Localization.Text(accessibleNameKey));
        control.SetResourceReference(AutomationProperties.NameProperty, accessibleNameKey);
        if (dataBound)
        {
            control.DisplayMemberPath = nameof(SetupChoice<TValue>.Label);
            control.SelectedValuePath = nameof(SetupChoice<TValue>.Value);
            control.ItemsSource = choices;
            foreach (SetupChoice<TValue> choice in choices) refreshChoiceText.Add(choice.RefreshLanguage);
        }
        else
        {
            control.SelectedValuePath = nameof(ComboBoxItem.Tag);
            foreach (SetupChoice<TValue> choice in choices)
            {
                var item = new ComboBoxItem { Tag = choice.Value };
                item.SetResourceReference(ContentControl.ContentProperty, choice.LabelKey);
                control.Items.Add(item);
            }
        }
        control.SelectedValue = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (!committing && control.SelectedValue is TValue value)
            {
                draft = change(draft, value);
                if (previewPresentation) ApplyPreviewPreferences();
            }
        };
        Label label = Label(Localization.Text(labelKey), control);
        BindCaption(label, labelKey, accessibleNameKey);
        panel.Children.Add(label);
        panel.Children.Add(control);
        return control;
    }

    private void AddToggle(Panel panel, string identifier, string labelKey, string accessibleNameKey, bool selected,
        Func<UiPreferences, bool, UiPreferences> change, string hintKey, double verticalMargin = 8)
    {
        var control = Named(new CheckBox
        {
            IsChecked = selected, Margin = new Thickness(3, verticalMargin, 3, verticalMargin)
        }, identifier, Localization.Text(accessibleNameKey));
        BindCaption(control, labelKey, accessibleNameKey);
        control.SetResourceReference(UsageHints.TextProperty, hintKey);
        control.Checked += (_, _) => ChangeToggle(true);
        control.Unchecked += (_, _) => ChangeToggle(false);
        panel.Children.Add(control);

        void ChangeToggle(bool value)
        {
            if (committing) return;
            draft = change(draft, value);
            ApplyPreviewPreferences();
        }
    }

    private void ApplyPreviewPreferences() => ApplyPreferences(draft with { Theme = presentationTheme });

    private void PreviewLanguage()
    {
        if (committing) return;
        if (languageChoice.SelectedValue is string language)
        {
            Localization.SetLanguage(language);
            Localization.ApplyResources(Resources);
            Language = XmlLanguage.GetLanguage(Localization.Culture.IetfLanguageTag);
            foreach (Action refresh in refreshChoiceText) refresh();
        }
        UpdateSetupReadiness();
    }

    private void UpdateSetupReadiness()
    {
        if (committing) return;
        bool languageSelected = languageChoice.SelectedIndex >= 0;
        bool themeSelected = themeChoice.SelectedIndex >= 0;
        continueButton.IsEnabled = languageSelected && themeSelected;
    }

    private void SaveSetup()
    {
        if (committing || SavedPreferences is not null || languageChoice.SelectedIndex < 0 || themeChoice.SelectedIndex < 0) return;
        UiPreferences candidate = draft with { FirstRunCompleted = true };
        committing = true;
        continueButton.IsEnabled = false;
        try
        {
            if (!store.TrySave(candidate, out string? error))
            {
                ShowResult(error ?? Localization.Text("Setup.Save.Failed"));
                ((TextBlock)FindName("SectionResultReadout")).Focus();
                return;
            }
            SavedPreferences = candidate;
            SaveWarning = error;
        }
        finally { committing = false; continueButton.IsEnabled = languageChoice.SelectedIndex >= 0 && themeChoice.SelectedIndex >= 0; }
        Close();
    }

    private sealed class SetupChoice<TValue>(TValue value, string labelKey, params object[] arguments) : INotifyPropertyChanged
    {
        public TValue Value { get; } = value;
        internal string LabelKey => labelKey;
        public string Label => Localization.Text(labelKey, arguments);
        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

        public override string ToString() => Label;
    }
}
