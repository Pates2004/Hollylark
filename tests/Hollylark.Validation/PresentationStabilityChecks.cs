using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyPresentationStabilityApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyPresentationStability(); }
        finally { application.Shutdown(); }
    }

    private static void VerifyPresentationStability()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                var preferences = new UiPreferences { Language = language, Theme = "Light" };
                var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "presentation-stability", language));
                var wizard = new FirstRunWindow(preferences, store);
                MoveOffscreen(wizard);
                try
                {
                    wizard.Show();
                    var languageChoice = (ComboBox)wizard.FindName("FirstRunLanguageChoice");
                    var theme = (ComboBox)wizard.FindName("FirstRunThemeChoice");
                    languageChoice.SelectedValue = language;
                    theme.SelectedValue = "Light";
                    Pump();
                    foreach (string identifier in new[] { "FirstRunScaleChoice", "FirstRunValueDisplayChoice" })
                    {
                        var choice = (ComboBox)wizard.FindName(identifier);
                        choice.SelectedIndex = 0;
                        Pump();
                        ResourceDictionary[] resources = wizard.Resources.MergedDictionaries.ToArray();
                        FocusForKeyboardCheck(choice);
                        RaiseLocalKey(choice, Key.Down);
                        Check(choice.SelectedIndex == 1 && wizard.ThemeMode == ThemeMode.Light,
                            "Non-theme arrow updates only its choice: " + identifier);
                        Check(wizard.Resources.MergedDictionaries.SequenceEqual(resources),
                            "Non-theme arrows preserve the current theme resource instances: " + identifier);
                        CheckKeyboardFocus(choice, "Non-theme arrows retain focus: " + identifier);
                    }
                    ResourceDictionary[] beforeHints = wizard.Resources.MergedDictionaries.ToArray();
                    ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked = false;
                    Pump();
                    Check(wizard.Resources.MergedDictionaries.SequenceEqual(beforeHints),
                        "Hint toggles do not recreate theme resources");
                    ResourceDictionary[] beforeTheme = wizard.Resources.MergedDictionaries.ToArray();
                    FocusForKeyboardCheck(theme);
                    RaiseLocalKey(theme, Key.Down);
                    Pump();
                    Check(wizard.ThemeMode == ThemeMode.Light && theme.SelectedValue is "Dark"
                        && wizard.Resources.MergedDictionaries.SequenceEqual(beforeTheme),
                        "Theme arrows stage the selection without swapping theme resources");
                    CheckKeyboardFocus(theme, "Staging the theme preserves native keyboard focus");
                    ClickFirstRun(wizard, "SaveFirstRunButton");
                    UiPreferences saved = store.Load(out string? error);
                    Check(error is null && saved.FirstRunCompleted && saved.Theme == "Dark" && saved.Language == language &&
                        saved.TextScale == 125 && saved.ValueDisplayMode == ValueDisplayMode.StaticText && !saved.ShowHints,
                        "Setup commits the staged theme and all other selected preferences only on Continue");
                }
                finally { if (wizard.IsVisible) wizard.Close(); Pump(); }

                var section = new HelpWindow(_ => { });
                MoveOffscreen(section);
                try
                {
                    section.ApplyPreferences(preferences);
                    section.Show();
                    Pump();
                    ResourceDictionary[] resources = section.Resources.MergedDictionaries.ToArray();
                    section.ApplyPreferences(preferences with { ShowHints = false });
                    Pump();
                    Check(section.Resources.MergedDictionaries.SequenceEqual(resources),
                        "Applying unchanged auxiliary theme preserves resource instances");
                }
                finally { section.Close(); Pump(); }

                using var transport = new FakeTransport();
                MainWindow main = CreatePresentationWindow(transport, "presentation-stability-main-" + language, new MemoryDesktop());
                try
                {
                    SavePreferences(main, main.CurrentPreferences with { Theme = "Light" });
                    ResourceDictionary[] resources = main.Resources.MergedDictionaries.ToArray();
                    SavePreferences(main, main.CurrentPreferences with { ShowHints = false });
                    Check(main.Resources.MergedDictionaries.SequenceEqual(resources),
                        "Saving non-theme main preferences preserves resource instances");
                }
                finally { main.RequestExit(); Pump(); }
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }
}
