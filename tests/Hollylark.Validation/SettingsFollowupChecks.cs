using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifySettingsFollowup()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyWizardInitialPresentation(language);
                VerifySettingsResetRetention(language);
                VerifyPendingProfileSave(language);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyWizardInitialPresentation(string language)
    {
        var preferences = new UiPreferences
        {
            Language = language, Theme = "Dark", TextScale = 175, ShowHints = false,
            ShowOperationMessages = false, HiddenRows = ["TX2"],
            AudioProfiles = [AudioProfile.FromStatus("Preserved wizard profile", A1Status.Parse(ValidStatus))]
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "settings-followup", language, "wizard-presentation"));
        Check(store.TrySave(preferences, out string? error) && error is null, "Prepare the existing wizard presentation fixture: " + language);
        UiPreferences restored = store.Load(out error);
        Check(error is null && store.ShouldOfferFirstRun, "Incomplete setup preserves its stored presentation: " + language);
        byte[] originalFile = File.ReadAllBytes(store.FilePath);
        var wizard = new FirstRunWindow(restored, store);
        try
        {
            Check(wizard.ThemeMode == ThemeMode.Dark && Math.Abs(wizard.FontSize - SystemFonts.MessageFontSize * 1.75) < 0.01,
                "Wizard applies stored theme and text scale before the first interaction: " + language);
            Check(!UsageHints.GetEnabled(wizard), "Wizard respects disabled hints before any selection: " + language);
            MoveOffscreen(wizard);
            wizard.Show();
            Pump();
            foreach (string identifier in new[] { "FirstRunLanguageChoice", "FirstRunThemeChoice", "FirstRunHintsCheckBox" })
            {
                var control = (FrameworkElement)wizard.FindName(identifier);
                Check(AutomationProperties.GetHelpText(control) == string.Empty
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)),
                    "Restored hints remain silent without losing essential wizard labels: " + identifier + "/" + language);
            }
            Check(((ComboBox)wizard.FindName("FirstRunLanguageChoice")).SelectedIndex == -1
                && ((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedIndex == -1
                && !((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled,
                "Restoring presentation does not silently complete explicit setup choices: " + language);
            var result = (TextBlock)wizard.FindName("SectionResultReadout");
            Check(!result.IsVisible && result.Text.Length == 0,
                "Setup reserves its static result for actual operations or errors: " + language);
            ClickFirstRun(wizard, "CancelFirstRunButton");
            Check(wizard.SavedPreferences is null && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath))
                && EquivalentExpandedPreferences(preferences, restored),
                "Cancelling restored setup preserves the complete existing file and caller preferences: " + language);
        }
        finally { if (wizard.IsVisible) wizard.Close(); Pump(); }
    }

    private static void VerifySettingsResetRetention(string language)
    {
        var preferences = new UiPreferences
        {
            Language = language, FirstRunCompleted = true, Theme = "Dark", TextScale = 137,
            HiddenRows = ["TX2", "Lights"], ShowHints = false, AutoApplySelections = true,
            AutoConnectAtStartup = true, PreferredReceiverPath = "simulated://remembered-receiver",
            AudioProfiles = [AudioProfile.FromStatus("Preserved settings profile", A1Status.Parse(ValidStatus))]
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "settings-followup", language, "reset"));
        Check(store.TrySave(preferences, out string? error) && error is null, "Prepare reset transaction fixture: " + language);
        byte[] originalFile = File.ReadAllBytes(store.FilePath);
        bool consent = false;
        UiPreferences? committed = null;
        var settings = new SettingsWindow(preferences, store, (candidate, _) => committed = candidate,
            confirm: (_, _) => consent);
        try
        {
            ShowFollowupSettings(settings);
            string chosenLanguage = language == "pl" ? "en" : "pl";
            SetChoice(settings, "SettingsLanguageChoice", chosenLanguage);
            ClickSettings(settings, "ResetAppearanceSettingsButton");
            Check(((ComboBox)settings.FindName("SettingsThemeChoice")).SelectedValue is "Dark",
                "Declining appearance reset leaves its draft unchanged: " + language);
            consent = true;
            ClickSettings(settings, "ResetAppearanceSettingsButton");
            Check(((ComboBox)settings.FindName("SettingsThemeChoice")).SelectedValue is "Light"
                && ((ComboBox)settings.FindName("SettingsTextScaleChoice")).SelectedValue is 100
                && Equals(((ComboBox)settings.FindName("SettingsLanguageChoice")).SelectedValue, chosenLanguage),
                "Appearance reset restores styling without silently changing the chosen language: " + language);
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            ClickSettings(settings, "ResetAllSettingsButton");
            Check(((ListBox)settings.FindName("AudioProfilesList")).Items.Count == 0
                && Equals(((ComboBox)settings.FindName("SettingsLanguageChoice")).SelectedValue, chosenLanguage),
                "Global reset clears profiles but retains the explicit language draft: " + language);
            Check(committed is null && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath))
                && preferences.AudioProfiles.Length == 1 && preferences.HiddenRows.Length == 2,
                "Both reset operations remain transactional and preserve caller collections: " + language);
            consent = false;
            ClickSettings(settings, "CancelSettingsButton");
            Check(!settings.IsVisible && committed is null && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "No on reset cancellation discards the draft and preserves persisted language, setup state and profiles: " + language);
        }
        finally { consent = false; if (settings.IsVisible) settings.Close(); Pump(); }

        settings = new SettingsWindow(preferences, store, (candidate, _) => committed = candidate, confirm: (_, _) => true);
        try
        {
            ShowFollowupSettings(settings);
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            ClickSettings(settings, "ResetAllSettingsButton");
            ClickSettings(settings, "SaveSettingsButton");
            var expected = new UiPreferences
            {
                Language = language, FirstRunCompleted = true, AutoConnectAtStartup = preferences.AutoConnectAtStartup,
                PreferredReceiverPath = preferences.PreferredReceiverPath
            };
            UiPreferences loaded = store.Load(out error);
            Check(committed is not null && EquivalentExpandedPreferences(expected, committed)
                && EquivalentExpandedPreferences(expected, loaded) && error is null,
                "Committed global reset resets active preferences while retaining language, setup completion and legacy USB round-trip fields: " + language);
            Check(!store.ShouldOfferFirstRun && Localization.CurrentLanguage == language,
                "A normal reset does not reopen setup or change the active session language: " + language);
        }
        finally { if (settings.IsVisible) settings.Close(); Pump(); }
    }

    private static void VerifyPendingProfileSave(string language)
    {
        var preferences = new UiPreferences { Language = language, FirstRunCompleted = true, Theme = "Dark" };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "settings-followup", language, "pending-profile"));
        Check(store.TrySave(preferences, out string? error) && error is null, "Prepare incomplete profile transaction fixture: " + language);
        byte[] originalFile = File.ReadAllBytes(store.FilePath);
        bool consent = false;
        bool allowSave = false;
        int confirmations = 0;
        int applicationAttempts = 0;
        int captureAttempts = 0;
        UiPreferences? committed = null;
        SettingsWindow CreatePendingProfileWindow() => new(preferences, store, (candidate, _) => committed = candidate,
            apply: candidate =>
            {
                applicationAttempts++;
                if (!allowSave) return Localization.Text("Settings.Save.ApplyFailed");
                return store.TrySave(candidate, out string? failure) ? null : failure;
            },
            readStatus: () => { captureAttempts++; return A1Status.Parse(ValidStatus); },
            confirm: (message, title) =>
            {
                confirmations++;
                Check(title == Localization.Text("Settings.Save.Title")
                    && message == Localization.Text("Settings.Save.Confirmation"),
                    "Only closing asks the short localized save question: " + language);
                return consent;
            });
        SettingsWindow settings = CreatePendingProfileWindow();
        try
        {
            ShowFollowupSettings(settings);
            SetChoice(settings, "SettingsThemeChoice", "Light");
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            var name = (TextBox)settings.FindName("AudioProfileNameTextBox");
            name.Text = " Uncaptured _profile ";
            SelectSettingsCategory(settings, "AppearanceSettingsTab");
            ClickSettings(settings, "SaveSettingsButton");
            Check(confirmations == 0 && applicationAttempts == 0 && captureAttempts == 0 && committed is null
                && settings.IsVisible && name.Text == " Uncaptured _profile " && name.IsKeyboardFocused
                && ((TabItem)settings.FindName("ProfilesSettingsTab")).IsSelected
                && ((TextBlock)settings.FindName("SectionResultReadout")).Text
                    == Localization.Text("Settings.Save.PendingProfile", "Uncaptured _profile")
                && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "OK preserves an uncaptured name and directs focus to its localized error without asking or saving: " + language);
            ClickSettings(settings, "CancelSettingsButton");
            Check(confirmations == 1 && applicationAttempts == 0 && captureAttempts == 0 && committed is null
                && !settings.IsVisible && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "No discards the pending-name draft and closes after one confirmation without capture or file changes: " + language);
            settings = CreatePendingProfileWindow();
            ShowFollowupSettings(settings);
            SetChoice(settings, "SettingsThemeChoice", "Light");
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            name = (TextBox)settings.FindName("AudioProfileNameTextBox");
            Check(name.Text.Length == 0, "Declined pending names are not carried into a later settings transaction: " + language);
            name.Text = " Uncaptured _profile ";
            consent = true;
            ClickSettings(settings, "SaveSettingsButton");
            Check(confirmations == 1 && applicationAttempts == 0 && committed is null && settings.IsVisible
                && name.Text == " Uncaptured _profile " && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Repeated OK cannot silently discard a pending profile name even when confirmations would allow saving: " + language);
            name.Clear();
            ClickSettings(settings, "SaveSettingsButton");
            Check(confirmations == 1 && applicationAttempts == 1 && committed is null && settings.IsVisible
                && originalFile.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "After explicit name removal a failed direct commit preserves the settings draft and persisted file: " + language);
            Check(((TextBlock)settings.FindName("SectionResultReadout")).Text == Localization.Text("Settings.Save.ApplyFailed"),
                "The failed pending-profile commit reports an essential localized error: " + language);
            allowSave = true;
            ClickSettings(settings, "SaveSettingsButton");
            Check(confirmations == 1 && applicationAttempts == 2 && committed is { Theme: "Light" }
                && committed.AudioProfiles.Length == 0 && !settings.IsVisible && captureAttempts == 0,
                "OK saves the corrected draft directly without another prompt, inventing a profile or capturing audio: " + language);
            Check(EquivalentExpandedPreferences(preferences with { Theme = "Light" }, store.Load(out error)) && error is null,
                "Explicit name removal persists exactly the chosen preferences: " + language);
        }
        finally { consent = false; if (settings.IsVisible) settings.Close(); Pump(); }
    }

    private static void ShowFollowupSettings(SettingsWindow settings)
    {
        MoveOffscreen(settings);
        settings.Show();
        Pump();
    }
}
