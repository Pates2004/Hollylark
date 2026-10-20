using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    internal static void VerifyFirstRunSettingsRevisionChecks()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyRevisionWizardThemeStaging(language);
                VerifyRevisionWizardSaveError(language);
                VerifyRevisionSettingsPresentation(language);
                VerifyRevisionThemeMigration(language);
                VerifyRevisionAppliedThemeBrushes(language);
                VerifyRevisionValueDisplayPersistence(language);
                foreach (RevisionSaveScenario scenario in Enum.GetValues<RevisionSaveScenario>())
                    VerifyRevisionSettingsSaveDecision(language, scenario);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyRevisionWizardThemeStaging(string language)
    {
        var preferences = new UiPreferences
        {
            Language = language, Theme = "Light", ShowHints = false, ShowOperationMessages = false,
            ConfirmExit = false, ValueDisplayMode = ValueDisplayMode.ReadOnlyFields
        };
        string original = JsonSerializer.Serialize(preferences);
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, "wizard-theme"));
        var wizard = new FirstRunWindow(preferences, store);
        try
        {
            MoveOffscreen(wizard);
            wizard.Show();
            Pump();
            var result = (TextBlock)wizard.FindName("SectionResultReadout");
            var languageChoice = (ComboBox)wizard.FindName("FirstRunLanguageChoice");
            var themeChoice = (ComboBox)wizard.FindName("FirstRunThemeChoice");
            var continueButton = (Button)wizard.FindName("SaveFirstRunButton");
            var valueDisplay = (ComboBox)wizard.FindName("FirstRunValueDisplayChoice");
            Check(result.Visibility == Visibility.Collapsed && result.Text.Length == 0 && !continueButton.IsEnabled,
                "The wizard has no initial configuration-state readout or instruction result: " + language);
            Check(!Descendants(wizard).OfType<TextBox>().Any(control => control.IsVisible && control.IsReadOnly),
                "The wizard exposes no read-only edit fields for routine instructions: " + language);
            VerifyRevisionValueDisplayChoices(valueDisplay, language + "/wizard");
            VerifyRevisionHiddenDescription((TextBlock)wizard.FindName("FirstRunScope"), language + "/wizard-scope");
            languageChoice.SelectedValue = language;
            Pump();
            Check(!continueButton.IsEnabled && result.Visibility == Visibility.Collapsed && result.Text.Length == 0,
                "Selecting only a language changes readiness without creating a routine result: " + language);
            themeChoice.SelectedValue = "Light";
            Pump();
            ResourceDictionary[] resources = wizard.Resources.MergedDictionaries.ToArray();
            FocusForKeyboardCheck(themeChoice);
            foreach (Key key in new[] { Key.Down, Key.Up, Key.Down })
            {
                RaiseLocalKey(themeChoice, key);
                Check(themeChoice.SelectedValue is "Light" or "Dark" && wizard.ThemeMode == ThemeMode.Light
                    && wizard.Resources.MergedDictionaries.SequenceEqual(resources),
                    "Theme arrows stage their selection without changing theme resources: " + language + "/" + key);
                CheckKeyboardFocus(themeChoice, "Theme arrows preserve wizard focus: " + language + "/" + key);
                Check(themeChoice.SelectedItem is ComboBoxItem item && item.Content is string text && text.Length > 0,
                    "Each staged wizard theme remains a native, named list item: " + language + "/" + key);
            }
            Check(themeChoice.SelectedValue is "Dark" && continueButton.IsEnabled,
                "The pending dark theme satisfies setup without being previewed: " + language);
            ((ComboBox)wizard.FindName("FirstRunScaleChoice")).SelectedValue = 150;
            ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked = true;
            ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked = false;
            ((CheckBox)wizard.FindName("FirstRunMessagesCheckBox")).IsChecked = true;
            valueDisplay.SelectedValue = ValueDisplayMode.None;
            valueDisplay.SelectedValue = ValueDisplayMode.StaticText;
            valueDisplay.SelectedValue = ValueDisplayMode.ReadOnlyFields;
            languageChoice.SelectedValue = language == "pl" ? "en" : "pl";
            Pump();
            Check(Localization.CurrentLanguage != language, "Language preview remains live independently of staged theme: " + language);
            languageChoice.SelectedValue = language;
            Pump();
            Check(wizard.ThemeMode == ThemeMode.Light && wizard.Resources.MergedDictionaries.SequenceEqual(resources)
                && Math.Abs(wizard.FontSize - SystemFonts.MessageFontSize * 1.5) < 0.01,
                "Scale, hints, messages, language and display choices cannot apply a pending theme indirectly: " + language);
            Check(result.Visibility == Visibility.Collapsed && result.Text.Length == 0 && !File.Exists(store.FilePath),
                "Completed setup choices remain unsaved and produce no press-Continue status field: " + language);
            ClickFirstRun(wizard, "SaveFirstRunButton");
            UiPreferences restored = store.Load(out string? error);
            Check(error is null && !wizard.IsVisible && wizard.SavedPreferences is { Theme: "Dark", FirstRunCompleted: true }
                && restored.Theme == "Dark" && restored.Language == language && restored.TextScale == 150
                && restored.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields && !restored.ConfirmExit,
                "Continue commits staged theme and presentation while preserving an explicit false exit preference: " + language);
            Check(JsonSerializer.Serialize(preferences) == original,
                "Wizard exploration and commit never mutate the caller preferences: " + language);
        }
        finally
        {
            if (wizard.IsVisible) wizard.Close();
            Pump();
            Localization.SetLanguage(language);
        }
    }

    private static void VerifyRevisionWizardSaveError(string language)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, "wizard-save-error"));
        Directory.CreateDirectory(store.BaseDirectory);
        string blocker = Path.Combine(store.BaseDirectory, "data");
        const string blockerText = "This diagnostic fixture intentionally blocks the settings directory.";
        File.WriteAllText(blocker, blockerText);
        var wizard = new FirstRunWindow(new UiPreferences
        {
            Language = language, Theme = "Light", ShowHints = false, ShowOperationMessages = false
        }, store);
        try
        {
            MoveOffscreen(wizard);
            wizard.Show();
            Pump();
            ((ComboBox)wizard.FindName("FirstRunLanguageChoice")).SelectedValue = language;
            ((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedValue = "Dark";
            ClickFirstRun(wizard, "SaveFirstRunButton");
            var result = (TextBlock)wizard.FindName("SectionResultReadout");
            string expected = Localization.Text("Preferences.WriteError", store.FilePath);
            VerifyRevisionStaticReadout(result, expected, language + "/wizard-save-error");
            Check(wizard.IsVisible && wizard.SavedPreferences is null && !File.Exists(store.FilePath)
                && ((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled,
                "An actual wizard storage error preserves the choices and permits retry: " + language);
            ((ComboBox)wizard.FindName("FirstRunScaleChoice")).SelectedValue = 125;
            Pump();
            Check(result.Text == expected && wizard.ThemeMode == ThemeMode.Light
                && ((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedValue is "Dark",
                "Readiness updates neither replace a real error with instructions nor apply the pending theme: " + language);
            Check(File.ReadAllText(blocker) == blockerText, "Failed setup preserves its storage-error fixture: " + language);
        }
        finally
        {
            wizard.Close();
            Pump();
        }
    }

    private static void VerifyRevisionSettingsPresentation(string language)
    {
        A1Status status = A1Status.Parse(ValidStatus);
        var preferences = new UiPreferences
        {
            Language = language, Theme = "Light", FirstRunCompleted = true, ShowHints = false,
            ShowOperationMessages = false, ValueDisplayMode = ValueDisplayMode.ReadOnlyFields,
            AudioProfiles = [AudioProfile.FromStatus("Static summary profile", status)]
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, "settings-presentation"));
        var window = new SettingsWindow(preferences, store,
            (_, _) => throw new InvalidOperationException("Presentation checks must not save settings."),
            readStatus: () => status, applyProfile: _ => Task.FromResult<string?>(null),
            confirm: (_, title) => title == Localization.Text("Settings.Reset.Title"));
        try
        {
            MoveOffscreen(window);
            window.Show();
            Pump();
            var result = (TextBlock)window.FindName("SectionResultReadout");
            var save = (Button)window.FindName("SaveSettingsButton");
            Check(result.Visibility == Visibility.Collapsed && result.Text.Length == 0
                && save.IsDefault && AutomationProperties.GetName(save) == "OK",
                "Settings opens without a routine result and exposes exactly OK as its default action name: " + language);
            var theme = (ComboBox)window.FindName("SettingsThemeChoice");
            Check(theme.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(new[] { "Light", "Dark" }),
                "Settings offers only explicit Light and Dark themes: " + language);
            ResourceDictionary[] resources = window.Resources.MergedDictionaries.ToArray();
            FocusForKeyboardCheck(theme);
            RaiseLocalKey(theme, Key.Down);
            Check(theme.SelectedValue is "Dark" && window.ThemeMode == ThemeMode.Light
                && window.Resources.MergedDictionaries.SequenceEqual(resources),
                "Normal Settings and first-run theme exploration both stage instead of rebuilding resources: " + language);
            CheckKeyboardFocus(theme, "Staged Settings theme retains keyboard focus: " + language);
            ((ComboBox)window.FindName("SettingsLanguageChoice")).SelectedValue = language == "pl" ? "en" : "pl";
            Pump();
            var restart = (TextBlock)window.FindName("SettingsLanguageRestartNotice");
            Check(restart.IsVisible && restart.Focusable,
                "A pending language change retains an essential restart notice even with hints disabled: " + language);
            ((ComboBox)window.FindName("SettingsLanguageChoice")).SelectedValue = language;
            Pump();
            Check(restart.Visibility == Visibility.Collapsed,
                "The restart notice is absent when the draft language matches the running interface: " + language);
            SelectSettingsCategory(window, "ControlsSettingsTab");
            Check(AutomationProperties.GetName((TabControl)window.FindName("SettingsTabs")).Length == 0
                && AutomationProperties.GetHelpText((TabControl)window.FindName("SettingsTabs")).Length == 0,
                "The generic category description is optional; actual tab names remain available with hints off: " + language);
            VerifyRevisionValueDisplayChoices((ComboBox)window.FindName("ValueDisplayModeChoice"), language + "/settings");
            Check(window.FindName("AutoApplySelectionsWarning") is null,
                "Automatic-selection instructions are absent from Settings: " + language);
            ((CheckBox)window.FindName("AutoApplySelectionsCheckBox")).IsChecked = true;
            Pump();
            Check(window.FindName("AutoApplySelectionsWarning") is null,
                "Enabling automatic selection does not introduce explanatory readouts: " + language);
            var categories = (TabControl)window.FindName("SettingsTabs");
            Check(categories.Items.Cast<TabItem>().Select(tab => tab.Name).SequenceEqual(new[]
                { "AppearanceSettingsTab", "MainScreenSettingsTab", "ControlsSettingsTab", "StartupAndClosingSettingsTab", "ProfilesSettingsTab", "HotkeysSettingsTab" }),
                "Startup and closing form one category with no separate legacy tabs: " + language);
            SelectSettingsCategory(window, "StartupAndClosingSettingsTab");
            var startupAndClosing = (TabItem)window.FindName("StartupAndClosingSettingsTab");
            Check(AutomationProperties.GetName(startupAndClosing) == Localization.Text("Settings.StartupAndClosing.Name"),
                "The combined category exposes its localized accessible name: " + language);
            foreach (string identifier in new[] { "StartWithWindowsCheckBox", "CloseToTrayCheckBox", "ConfirmExitCheckBox" })
            {
                var toggle = (CheckBox)window.FindName(identifier);
                Check(toggle.IsVisible && Descendants((DependencyObject)startupAndClosing.Content).Contains(toggle),
                    "The shared category contains each startup/closing control: " + language + "/" + identifier);
            }
            ((CheckBox)window.FindName("StartWithWindowsCheckBox")).IsChecked = true;
            ((CheckBox)window.FindName("CloseToTrayCheckBox")).IsChecked = true;
            ((CheckBox)window.FindName("ConfirmExitCheckBox")).IsChecked = false;
            ClickSettings(window, "ResetStartupAndClosingSettingsButton");
            UiPreferences reset = ReadRevisionSettingsDraft(window);
            Check(!reset.StartWithWindows && !reset.CloseToTray && reset.ConfirmExit && reset.Theme == "Dark"
                && reset.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields && reset.AutoApplySelections,
                "The combined reset restores all three related defaults without touching other categories: " + language);
            SelectSettingsCategory(window, "MainScreenSettingsTab");
            var heading = (TextBlock)window.FindName("SettingsVisibleRowsHeading");
            VerifyRevisionHiddenDescription(heading, language + "/settings-heading");
            ((CheckBox)window.FindName("ShowHintsCheckBox")).IsChecked = true;
            Pump();
            Check(heading.IsVisible, "Optional Settings heading can be enabled again: " + language);
            ((CheckBox)window.FindName("ShowHintsCheckBox")).IsChecked = false;
            Pump();
            VerifyRevisionHiddenDescription(heading, language + "/settings-heading-restored");
            Check(window.FindName("ReceiverUsageHint") is null,
                "Receiver usage instructions have moved out of Settings into Help: " + language);
            foreach (TabItem tab in ((TabControl)window.FindName("SettingsTabs")).Items)
            {
                ((TabControl)window.FindName("SettingsTabs")).SelectedItem = tab;
                Pump();
                Check(!Descendants(window).OfType<TextBox>().Any(control => control.IsVisible && control.IsReadOnly),
                    "No Settings category presents explanatory or status read-only edits: " + language + "/" + tab.Name);
            }
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
            VerifyRevisionStaticReadout(summary, summary.Text, language + "/profile-summary");
            Check(!((TextBox)window.FindName("AudioProfileNameTextBox")).IsReadOnly,
                "The profile name remains a real editable field: " + language);
            string error = Localization.Text("Settings.Save.ApplyFailed");
            window.ShowResult(error);
            VerifyRevisionStaticReadout(result, error, language + "/settings-error");
            Check(!File.Exists(store.FilePath), "Presentation and actual-error display do not persist settings: " + language);
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static void VerifyRevisionSettingsSaveDecision(string language, RevisionSaveScenario scenario)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, scenario.ToString()));
        var preferences = new UiPreferences
        {
            Language = language, Theme = "Light", FirstRunCompleted = true, ConfirmExit = false,
            ValueDisplayMode = ValueDisplayMode.StaticText,
            AudioProfiles = [AudioProfile.FromStatus("Preserved save profile", A1Status.Parse(ValidStatus))]
        };
        Check(store.TrySave(preferences, out string? initialError) && initialError is null,
            "Create an isolated settings decision fixture: " + language + "/" + scenario);
        byte[] originalFile = File.ReadAllBytes(store.FilePath);
        string originalPreferences = JsonSerializer.Serialize(preferences);
        var prompts = new List<(string Message, string Title)>();
        UiPreferences? committed = null;
        bool cleanup = false;
        bool injectFailure = true;
        int applyAttempts = 0;
        int saveNotifications = 0;
        bool unchanged = scenario is RevisionSaveScenario.UnchangedOk or RevisionSaveScenario.UnchangedEscape or RevisionSaveScenario.UnchangedClose;
        bool closeRequest = scenario is not (RevisionSaveScenario.UnchangedOk or RevisionSaveScenario.ModifiedOk
            or RevisionSaveScenario.ModifiedEnter or RevisionSaveScenario.PendingProfileOk or RevisionSaveScenario.StorageErrorOk
            or RevisionSaveScenario.ApplyErrorOk or RevisionSaveScenario.ApplyExceptionOk);
        bool pendingName = scenario is RevisionSaveScenario.PendingProfileOk or RevisionSaveScenario.PendingProfileEscapeYes
            or RevisionSaveScenario.PendingProfileEscapeNo;
        bool approve = scenario is not (RevisionSaveScenario.EscapeNo or RevisionSaveScenario.CloseNo or RevisionSaveScenario.CancelNo
            or RevisionSaveScenario.PendingProfileEscapeNo);
        bool storageError = scenario is RevisionSaveScenario.StorageErrorOk or RevisionSaveScenario.StorageErrorEscape;
        bool applyError = scenario is RevisionSaveScenario.ApplyErrorOk or RevisionSaveScenario.ApplyErrorEscape;
        bool applyException = scenario is RevisionSaveScenario.ApplyExceptionOk or RevisionSaveScenario.ApplyExceptionEscape;
        Func<UiPreferences, string?>? apply = applyError || applyException
            ? candidate =>
            {
                applyAttempts++;
                if (injectFailure)
                {
                    if (applyException) throw new IOException("Simulated commit failure.");
                    return Localization.Text("Settings.Save.ApplyFailed");
                }
                return store.TrySave(candidate, out string? error) ? null : error;
            }
            : null;
        var window = new SettingsWindow(preferences, store, (candidate, _) => { committed = candidate; saveNotifications++; },
            apply: apply, confirm: (message, title) =>
            {
                if (cleanup) return false;
                prompts.Add((message, title));
                if (scenario == RevisionSaveScenario.ConfirmationError && injectFailure)
                    throw new InvalidOperationException("Simulated confirmation failure.");
                return approve;
            });
        FileStream? lockedSettings = null;
        try
        {
            MoveOffscreen(window);
            window.Show();
            Pump();
            if (!unchanged)
            {
                SetChoice(window, "SettingsThemeChoice", "Dark");
                SetChoice(window, "SettingsTextScaleChoice", 150);
            }
            var name = (TextBox)window.FindName("AudioProfileNameTextBox");
            if (pendingName) name.Text = "Uncaptured pending profile";
            string draftBefore = JsonSerializer.Serialize(ReadRevisionSettingsDraft(window));
            if (storageError)
                lockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            var save = (Button)window.FindName("SaveSettingsButton");
            Check(save.IsDefault && AutomationProperties.GetName(save) == "OK",
                "Immediate save is the default OK action: " + language + "/" + scenario);
            if (scenario == RevisionSaveScenario.ModifiedEnter)
            {
                FocusForKeyboardCheck(save);
                RaiseLocalKey(save, Key.Enter);
            }
            else if (scenario is RevisionSaveScenario.UnchangedClose or RevisionSaveScenario.CloseYes or RevisionSaveScenario.CloseNo)
                window.Close();
            else if (scenario is RevisionSaveScenario.CancelYes or RevisionSaveScenario.CancelNo)
                ClickSettings(window, "CancelSettingsButton");
            else if (closeRequest)
            {
                var theme = (ComboBox)window.FindName("SettingsThemeChoice");
                FocusForKeyboardCheck(theme);
                RaiseLocalKey(theme, Key.Escape);
            }
            else ClickSettings(window, "SaveSettingsButton");
            lockedSettings?.Dispose();
            lockedSettings = null;
            if (unchanged)
            {
                int expectedNotifications = scenario == RevisionSaveScenario.UnchangedOk ? 1 : 0;
                Check(!window.IsVisible && prompts.Count == 0 && saveNotifications == expectedNotifications
                    && (committed is not null) == (expectedNotifications == 1)
                    && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "Unchanged settings close without questions; only explicit OK saves/applies them: " + language + "/" + scenario);
                return;
            }
            int expectedPrompts = closeRequest ? 1 : 0;
            Check(prompts.Count == expectedPrompts
                && prompts.All(prompt => prompt == (Localization.Text("Settings.Save.Confirmation"), Localization.Text("Settings.Save.Title"))),
                "Only a dirty close asks one save question; OK and Enter never ask: " + language + "/" + scenario);
            if (!approve)
            {
                Check(!window.IsVisible && saveNotifications == 0 && committed is null && applyAttempts == 0
                    && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "No discards and closes without saving or requesting a second discard confirmation: " + language + "/" + scenario);
                return;
            }
            if (pendingName)
            {
                Check(window.IsVisible && name.Text == "Uncaptured pending profile" && saveNotifications == 0 && committed is null
                    && JsonSerializer.Serialize(ReadRevisionSettingsDraft(window)) == draftBefore
                    && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "An uncaptured profile name blocks saving rather than silently dropping the name: " + language + "/" + scenario);
                VerifyRevisionStaticReadout((TextBlock)window.FindName("SectionResultReadout"),
                    Localization.Text("Settings.Save.PendingProfile", name.Text), language + "/" + scenario);
                Check(((TabControl)window.FindName("SettingsTabs")).SelectedItem == window.FindName("ProfilesSettingsTab")
                    && name.IsVisible && name.IsKeyboardFocused,
                    "Pending-profile validation exposes and focuses the preserved editable name: " + language + "/" + scenario);
                name.Clear();
                ClickSettings(window, "SaveSettingsButton");
                Check(prompts.Count == expectedPrompts,
                    "Clearing a pending name permits immediate OK without another confirmation: " + language + "/" + scenario);
            }
            if (storageError || applyError || applyException || scenario == RevisionSaveScenario.ConfirmationError)
            {
                string expectedError = storageError ? Localization.Text("Preferences.WriteError", store.FilePath)
                    : applyError ? Localization.Text("Settings.Save.ApplyFailed")
                    : applyException ? Localization.Text("Settings.Save.Unconfirmed")
                    : Localization.Text("Settings.Confirmation.Failed");
                Check(window.IsVisible && committed is null && saveNotifications == 0
                    && JsonSerializer.Serialize(ReadRevisionSettingsDraft(window)) == draftBefore
                    && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "A failed confirmation or commit preserves the full draft and persisted data: " + language + "/" + scenario);
                VerifyRevisionStaticReadout((TextBlock)window.FindName("SectionResultReadout"), expectedError,
                    language + "/" + scenario);
                injectFailure = false;
                ClickSettings(window, "SaveSettingsButton");
                Check(prompts.Count == expectedPrompts,
                    "Retry through OK commits without repeating the close question: " + language + "/" + scenario);
            }
            UiPreferences restored = store.Load(out string? loadError);
            Check(!window.IsVisible && saveNotifications == 1 && committed is { Theme: "Dark", TextScale: 150 }
                && loadError is null && restored.Theme == "Dark" && restored.TextScale == 150
                && !restored.ConfirmExit && restored.AudioProfiles.SequenceEqual(preferences.AudioProfiles),
                "The commit saves the complete draft once, preserving profiles and explicit false preferences: " + language + "/" + scenario);
        }
        finally
        {
            lockedSettings?.Dispose();
            cleanup = true;
            if (window.IsVisible) window.Close();
            Pump();
            Check(JsonSerializer.Serialize(preferences) == originalPreferences,
                "Save decisions do not mutate the caller preferences: " + language + "/" + scenario);
        }
    }

    private static void VerifyRevisionThemeMigration(string language)
    {
        Check(new UiPreferences().Theme == "Light", "New preferences default to an explicit Light theme: " + language);
        foreach (int schemaVersion in new[] { 2, UiPreferences.CurrentSchemaVersion })
        {
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language,
                "system-theme-migration-" + schemaVersion));
            var legacy = new UiPreferences
            {
                SchemaVersion = schemaVersion, FirstRunCompleted = true, Language = language, Theme = "System", TextScale = 125,
                ShowHints = false, ConfirmExit = false, ValueDisplayMode = ValueDisplayMode.ReadOnlyFields,
                AutoApplySelections = true, HiddenRows = ["Reverb"], PreferredReceiverPath = "simulated-retained-receiver",
                AudioProfiles = [AudioProfile.FromStatus("Migration profile", A1Status.Parse(ValidStatus))]
            };
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
            byte[] original = File.ReadAllBytes(store.FilePath);
            UiPreferences loaded = store.Load(out string? error);
            UiPreferences expected = legacy with { SchemaVersion = UiPreferences.CurrentSchemaVersion, Theme = "Light" };
            Check(error is null && !store.ShouldOfferFirstRun && JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(expected),
                "Legacy System themes migrate to Light in memory without discarding any unrelated preferences: " + language + "/" + schemaVersion);
            Check(original.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Reading a legacy System theme never rewrites the user's original configuration: " + language + "/" + schemaVersion);
            Check(store.TrySave(loaded, out error) && error is null,
                "The migrated preference can be saved normally by an explicit settings commit: " + language + "/" + schemaVersion);
            UiPreferences restored = store.Load(out error);
            Check(error is null && JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(expected),
                "Explicit save persists Light with the entire preserved profile: " + language + "/" + schemaVersion);
        }
    }

    private static void VerifyRevisionAppliedThemeBrushes(string language)
    {
        var preferences = new UiPreferences { Language = language, Theme = "Light", ShowHints = false };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, "applied-theme-brushes"));
        var wizard = new FirstRunWindow(preferences, store);
        var settings = new SettingsWindow(preferences, store,
            (_, _) => throw new InvalidOperationException("Theme brush inspection must not save settings."), confirm: (_, _) => false);
        try
        {
            foreach (AuxiliaryWindow window in new AuxiliaryWindow[] { wizard, settings })
            {
                MoveOffscreen(window);
                window.Show();
                Pump();
                string choiceIdentifier = window is FirstRunWindow ? "FirstRunThemeChoice" : "SettingsThemeChoice";
                string buttonIdentifier = window is FirstRunWindow ? "SaveFirstRunButton" : "SaveSettingsButton";
                if (window is FirstRunWindow)
                {
                    ((ComboBox)window.FindName("FirstRunLanguageChoice")).SelectedValue = language;
                    ((ComboBox)window.FindName(choiceIdentifier)).SelectedValue = "Light";
                    Pump();
                }
                RevisionThemeColors light = ReadRevisionThemeColors(window, choiceIdentifier, buttonIdentifier);
                window.ApplyPreferences(preferences with { Theme = "Dark" });
                Pump();
                RevisionThemeColors dark = ReadRevisionThemeColors(window, choiceIdentifier, buttonIdentifier);
                VerifyRevisionChangedThemeBrushes(window, light, dark, language + "/" + window.Name);
                window.ApplyPreferences(preferences);
                Pump();
                Check(window.ThemeMode == ThemeMode.Light && ReadRevisionThemeColors(window, choiceIdentifier, buttonIdentifier) == light,
                    "Switching back to Light restores actual theme brushes without sticky Dark colors: " + language + "/" + window.Name);
                Check(!File.Exists(store.FilePath), "Theme rendering checks do not persist settings: " + language + "/" + window.Name);
                window.Close();
                Pump();
            }
        }
        finally
        {
            if (wizard.IsVisible) wizard.Close();
            if (settings.IsVisible) settings.Close();
            Pump();
        }
    }

    private static void VerifyRevisionMainThemeBrushes(MainWindow window)
    {
        UiPreferences original = window.CurrentPreferences;
        string originalState = JsonSerializer.Serialize(original);
        var tabs = FindById<TabControl>(window, "MainTabs");
        object? originalTab = tabs.SelectedItem;
        try
        {
            tabs.SelectedItem = window.FindName("ControlTab");
            SavePreferences(window, original with
            {
                Theme = "Light", GainControlMode = GainControlMode.Choice,
                HiddenRows = original.HiddenRows.Where(row => row != "Gain").ToArray()
            });
            Check(FindById<ComboBox>(window, "Gain.Choice").IsVisible && FindById<Button>(window, "RefreshButton").IsVisible,
                "Main theme checks inspect the actual visible gain selector and refresh button");
            RevisionThemeColors light = ReadRevisionThemeColors(window, "Gain.Choice", "RefreshButton");
            SavePreferences(window, window.CurrentPreferences with { Theme = "Dark" });
            RevisionThemeColors dark = ReadRevisionThemeColors(window, "Gain.Choice", "RefreshButton");
            VerifyRevisionChangedThemeBrushes(window, light, dark, Localization.CurrentLanguage + "/main");
            SavePreferences(window, window.CurrentPreferences with { Theme = "Light" });
            RevisionThemeColors restored = ReadRevisionThemeColors(window, "Gain.Choice", "RefreshButton");
            Check(window.ThemeMode == ThemeMode.Light && restored == light,
                "Saving Light again restores the main window palette, controls and native backdrop: initial=" + light + "; restored=" + restored);
        }
        finally
        {
            SavePreferences(window, original);
            tabs.SelectedItem = originalTab;
            Pump();
            Check(JsonSerializer.Serialize(window.CurrentPreferences) == originalState,
                "Main theme validation restores the complete original preferences");
        }
    }

    private static void VerifyRevisionChangedThemeBrushes(Window window, RevisionThemeColors light, RevisionThemeColors dark, string scenario)
    {
        string diagnostic = "Light=" + light + "; Dark=" + dark;
        Check(window.ThemeMode == ThemeMode.Dark && light.Background != dark.Background && light.Foreground != dark.Foreground
            && light.ChoiceBackground != dark.ChoiceBackground && light.ButtonBackground != dark.ButtonBackground,
            "Applying Dark changes the window palette, text, choice and button brushes: " + scenario + "; " + diagnostic);
        Check(RevisionBrightness(light.Background) > RevisionBrightness(dark.Background)
            && RevisionBrightness(light.Foreground) < RevisionBrightness(dark.Foreground),
            "Light and Dark apply correctly ordered surface and foreground brightness: " + scenario + "; " + diagnostic);
    }

    private static RevisionThemeColors ReadRevisionThemeColors(Window window, string choiceIdentifier, string buttonIdentifier)
    {
        window.UpdateLayout();
        ComboBox choice = FindById<ComboBox>(window, choiceIdentifier);
        Button button = FindById<Button>(window, buttonIdentifier);
        object? windowPalette = window.TryFindResource("WindowBackground");
        object? foregroundPalette = window.TryFindResource("WindowForeground");
        object? choicePalette = window.TryFindResource("ComboBoxBackground");
        object? buttonPalette = window.TryFindResource("ButtonBackground");
        string diagnostic = window.Name + "/" + window.ThemeMode + "; raw window=" + window.Background
            + "; window palette=" + windowPalette + "; foreground=" + window.Foreground + "/" + foregroundPalette
            + "; choice=" + choice.Background + "/" + choicePalette + "; button=" + button.Background + "/" + buttonPalette;
        Check(window.Background is SolidColorBrush && window.Foreground is SolidColorBrush
            && choice.Background is SolidColorBrush && button.Background is SolidColorBrush && windowPalette is SolidColorBrush
            && foregroundPalette is SolidColorBrush && choicePalette is SolidColorBrush && buttonPalette is SolidColorBrush,
            "Rendered theme surfaces and palette provide inspectable brushes: " + diagnostic);
        Color rawBackground = ((SolidColorBrush)window.Background).Color;
        Color surface = ((SolidColorBrush)windowPalette!).Color;
        Color foreground = ((SolidColorBrush)window.Foreground).Color;
        Color choiceBackground = ((SolidColorBrush)choice.Background).Color;
        Color buttonBackground = ((SolidColorBrush)button.Background).Color;
        Check(surface.A == byte.MaxValue && (rawBackground.A == 0 || rawBackground == surface)
            && foreground == ((SolidColorBrush)foregroundPalette!).Color
            && choiceBackground == ((SolidColorBrush)choicePalette!).Color
            && buttonBackground == ((SolidColorBrush)buttonPalette!).Color,
            "Actual controls resolve the current palette; Fluent window transparency permits native Mica: " + diagnostic);
        nint handle = new WindowInteropHelper(window).Handle;
        int darkResult = RevisionThemeNativeMethods.GetWindowAttribute(handle, 20, out int darkMode, sizeof(int));
        Check(darkResult == 0 && darkMode == (window.ThemeMode == ThemeMode.Dark ? 1 : 0),
            "The native window follows the selected Light/Dark mode: " + diagnostic + "; result=" + darkResult + "; native dark=" + darkMode);
        if (rawBackground.A == 0)
        {
            int backdropResult = RevisionThemeNativeMethods.GetWindowAttribute(handle, 38, out int backdrop, sizeof(int));
            Check(backdropResult == 0 && backdrop == 2,
                "A transparent Fluent window has an active MainWindow backdrop, rather than a missing surface: " + diagnostic
                + "; result=" + backdropResult + "; native backdrop=" + backdrop);
        }
        return new RevisionThemeColors(surface, foreground, choiceBackground, buttonBackground, rawBackground, darkMode);
    }

    private static double RevisionBrightness(Color color) => color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;

    private static void VerifyRevisionValueDisplayPersistence(string language)
    {
        foreach (ValueDisplayMode mode in Enum.GetValues<ValueDisplayMode>())
        {
            var preferences = new UiPreferences { Language = language, Theme = "Light", FirstRunCompleted = true };
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "first-run-settings-revision", language, "value-display-" + mode));
            UiPreferences? committed = null;
            var window = new SettingsWindow(preferences, store, (candidate, _) => committed = candidate,
                confirm: (_, _) => throw new InvalidOperationException("OK must never ask for saving."));
            try
            {
                MoveOffscreen(window);
                window.Show();
                Pump();
                SelectSettingsCategory(window, "ControlsSettingsTab");
                SetChoice(window, "ValueDisplayModeChoice", mode);
                ClickSettings(window, "SaveSettingsButton");
                UiPreferences restored = store.Load(out string? error);
                Check(!window.IsVisible && committed?.ValueDisplayMode == mode && error is null && restored.ValueDisplayMode == mode,
                    "Each informational-value presentation mode survives a direct Settings save: " + language + "/" + mode);
            }
            finally
            {
                if (window.IsVisible)
                {
                    SetChoice(window, "ValueDisplayModeChoice", preferences.ValueDisplayMode);
                    window.Close();
                }
                Pump();
            }
        }
    }

    private sealed record RevisionThemeColors(Color Background, Color Foreground, Color ChoiceBackground, Color ButtonBackground,
        Color RawWindowBackground, int NativeDarkMode);

    private static class RevisionThemeNativeMethods
    {
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", ExactSpelling = true)]
        internal static extern int GetWindowAttribute(nint window, int attribute, out int value, int size);
    }

    private static void VerifyRevisionValueDisplayChoices(ComboBox choice, string scenario)
    {
        Check(choice.Items.Cast<ComboBoxItem>().Select(item => (ValueDisplayMode)item.Tag)
            .SequenceEqual(new[] { ValueDisplayMode.ReadOnlyFields, ValueDisplayMode.StaticText, ValueDisplayMode.None })
            && choice.SelectedValue is ValueDisplayMode.ReadOnlyFields,
            "All three value-display modes are available and a saved read-only preference is retained: " + scenario);
    }

    private static void VerifyRevisionHiddenDescription(TextBlock description, string scenario)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(description)
            ?? throw new InvalidOperationException("The optional description has no automation peer.");
        Check(description.Visibility == Visibility.Collapsed && !description.IsVisible && peer.IsOffscreen(),
            "Disabled descriptions are hidden visually and reported offscreen to UI Automation: " + scenario);
    }

    private static void VerifyRevisionStaticReadout(TextBlock readout, string expected, string scenario)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(readout)
            ?? throw new InvalidOperationException("The static readout has no automation peer.");
        Check(!string.IsNullOrWhiteSpace(expected) && readout.Text == expected && readout.IsVisible && readout.Focusable
            && KeyboardNavigation.GetIsTabStop(readout), "Actual status is visible and keyboard-focusable static text: " + scenario);
        Check(peer.GetAutomationControlType() == AutomationControlType.Text && peer.GetPattern(PatternInterface.Value) is null
            && peer.GetName().Contains(expected, StringComparison.Ordinal),
            "Actual status exposes its complete text without an editable-value pattern: " + scenario);
    }

    private static UiPreferences ReadRevisionSettingsDraft(SettingsWindow window) =>
        typeof(SettingsWindow).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as UiPreferences
            ?? throw new InvalidOperationException("The settings draft is unavailable to revision checks.");

    private enum RevisionSaveScenario
    {
        UnchangedOk,
        UnchangedEscape,
        UnchangedClose,
        ModifiedOk,
        ModifiedEnter,
        EscapeYes,
        EscapeNo,
        CloseYes,
        CloseNo,
        CancelYes,
        CancelNo,
        PendingProfileOk,
        PendingProfileEscapeYes,
        PendingProfileEscapeNo,
        StorageErrorOk,
        StorageErrorEscape,
        ApplyErrorOk,
        ApplyErrorEscape,
        ApplyExceptionOk,
        ApplyExceptionEscape,
        ConfirmationError
    }
}
