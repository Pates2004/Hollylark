using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyLocalizationCore()
    {
        Localization.SetLanguage("pl");
        var polish = new Dictionary<string, string>(Localization.GetEntries("pl"));
        var english = new Dictionary<string, string>(Localization.GetEntries("en"));
        Check(polish.Count > 150 && polish.Keys.Order().SequenceEqual(english.Keys.Order()),
            "Both shipped language catalogs contain the complete matching UI key set");
        foreach (string language in new[] { "pl", "en" })
        {
            Localization.SetLanguage(language);
            Check(Localization.Culture.Name == (language == "pl" ? "pl-PL" : "en-GB"), "Language selects its formatting culture: " + language);
            foreach (var entry in Localization.GetEntries(language))
            {
                CompositeFormat format = CompositeFormat.Parse(entry.Value);
                Check(!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value), "Complete language entry: " + language + "/" + entry.Key);
                if (format.MinimumArgumentCount == 0)
                    Check(!string.IsNullOrWhiteSpace(Localization.Text(entry.Key)), "Static language entry resolves: " + language + "/" + entry.Key);
            }
            string directory = Path.Combine(ReportDirectory, "language-preferences-" + language);
            var store = new UiPreferencesStore(directory);
            var preferences = new UiPreferences { Language = language, FirstRunCompleted = true };
            Check(store.TrySave(preferences, out string? error) && error is null, "Persist language preference: " + language);
            Check(new UiPreferencesStore(directory).Load(out error).Language == language && error is null, "Reload language preference: " + language);
            Check(!store.TrySave(preferences with { Language = "DE" }, out error) && !string.IsNullOrEmpty(error), "Reject unsupported preference language: " + language);
            Check(File.ReadAllText(store.FilePath).Contains('"' + language + '"'), "Rejected language never overwrites the valid preference");
        }
        Localization.SetLanguage("pl");
        Throws<ArgumentOutOfRangeException>(() => Localization.SetLanguage("de"), "Unsupported UI language is rejected");
        Throws<KeyNotFoundException>(() => Localization.Text("Missing.Key.For.Validation"), "Missing translation never becomes a blank or literal key");
        VerifyLegacyLanguageMigration();
        VerifyInvalidLanguageFiles(polish, english);
    }

    private static void VerifyLegacyLanguageMigration()
    {
        string directory = Path.Combine(ReportDirectory, "language-schema-migration");
        var store = new UiPreferencesStore(directory);
        AudioProfile profile = AudioProfile.FromStatus("Preserved profile", A1Status.Parse(ValidStatus));
        var legacy = new UiPreferences
        {
            FirstRunCompleted = true, PreferredReceiverPath = "simulated://preserved", Theme = "Dark", TextScale = 137,
            HiddenRows = ["Lights"], AudioProfiles = [profile], ShowHints = false
        };
        var options = new JsonSerializerOptions();
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var document = JsonSerializer.SerializeToNode(legacy, options)!.AsObject();
        document["SchemaVersion"] = 2;
        document.Remove("Language");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        string contents = document.ToJsonString();
        File.WriteAllText(store.FilePath, contents, new UTF8Encoding(false));
        UiPreferences migrated = store.Load(out string? error);
        Check(error is null && migrated.SchemaVersion == 3 && migrated.Language == "pl" && migrated.FirstRunCompleted,
            "Schema2 migrates to Polish schema3 without reopening completed setup");
        Check(migrated.PreferredReceiverPath == legacy.PreferredReceiverPath && migrated.TextScale == 137 && migrated.Theme == "Dark"
            && migrated.HiddenRows.SequenceEqual(legacy.HiddenRows) && migrated.AudioProfiles.SequenceEqual(legacy.AudioProfiles) && !migrated.ShowHints,
            "Language migration preserves receiver, profiles, appearance and accessibility choices");
        Check(File.ReadAllText(store.FilePath) == contents, "Schema migration does not rewrite the original configuration on read");
        Check(store.TrySave(migrated with { Language = "en" }, out error) && error is null, "Explicit save commits migrated language");
        Check(new UiPreferencesStore(directory).Load(out error).Language == "en" && error is null, "Migrated English preference survives restart");
    }

    private static void VerifyInvalidLanguageFiles(Dictionary<string, string> polish, Dictionary<string, string> english)
    {
        string first = polish.Keys.Order().First();
        var variants = new (string Name, Action<Dictionary<string, string>, Dictionary<string, string>> Change)[]
        {
            ("missing-English-key", (_, target) => target.Remove(first)),
            ("missing-in-both", (source, target) => { source.Remove(first); target.Remove(first); }),
            ("extra-in-both", (source, target) => { source.Add("Unexpected.Key", "value"); target.Add("Unexpected.Key", "value"); }),
            ("empty-value", (_, target) => target[first] = " "),
            ("invalid-format", (_, target) => target[first] = "{broken"),
            ("wrong-arguments", (_, target) => target[first] = "{37}"),
            ("wrong-contract-arguments", (source, target) => { source[first] = "{39}"; target[first] = "{39}"; })
        };
        foreach (var variant in variants)
        {
            var source = new Dictionary<string, string>(polish);
            var target = new Dictionary<string, string>(english);
            variant.Change(source, target);
            string directory = Path.Combine(ReportDirectory, "language-invalid-" + variant.Name);
            WriteLanguageFixture(directory, source, target);
            bool rejected = false;
            try { Localization.Initialize(directory); }
            catch (Exception exception) when (exception is InvalidDataException or FormatException) { rejected = true; }
            Check(rejected, "Reject invalid external catalogs: " + variant.Name);
            Check(Localization.GetEntries("pl")[first] == polish[first], "Invalid catalog leaves the previously validated catalog unchanged");
        }
        foreach (var (name, contents) in new[] { ("duplicate", "{\"Same\":\"first\",\"Same\":\"second\"}"), ("non-string", "{\"Same\":12}"), ("empty", "{}"), ("array", "[]") })
        {
            string directory = Path.Combine(ReportDirectory, "language-invalid-" + name);
            WriteLanguageFixture(directory, polish, english);
            File.WriteAllText(Path.Combine(directory, "en.lng"), contents);
            Throws<InvalidDataException>(() => Localization.Initialize(directory), "Reject invalid catalog document: " + name);
        }
        string invalidEncoding = Path.Combine(ReportDirectory, "language-invalid-encoding");
        WriteLanguageFixture(invalidEncoding, polish, english);
        File.WriteAllBytes(Path.Combine(invalidEncoding, "en.lng"), [0xC3, 0x28]);
        Throws<DecoderFallbackException>(() => Localization.Initialize(invalidEncoding), "Malformed UTF-8 catalog is rejected");
        string utf16Directory = Path.Combine(ReportDirectory, "language-invalid-utf16");
        WriteLanguageFixture(utf16Directory, polish, english);
        File.WriteAllText(Path.Combine(utf16Directory, "en.lng"), JsonSerializer.Serialize(english), Encoding.Unicode);
        Throws<InvalidDataException>(() => Localization.Initialize(utf16Directory), "UTF-16 catalogs are rejected despite a valid BOM");
        string missingDirectory = Path.Combine(ReportDirectory, "language-missing-directory");
        Throws<DirectoryNotFoundException>(() => Localization.Initialize(missingDirectory), "Missing language directory is rejected without fallback");
        Localization.Initialize();
        Localization.SetLanguage("pl");
    }

    private static void WriteLanguageFixture(string directory, Dictionary<string, string> polish, Dictionary<string, string> english)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pl.lng"), JsonSerializer.Serialize(polish), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "en.lng"), JsonSerializer.Serialize(english), new UTF8Encoding(false));
    }

    private static void VerifyLocalizationApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyLocalizationInterface(); }
        finally { application.Shutdown(); }
    }

    private static void VerifyLocalizationInterface()
    {
        VerifyWizardLanguagePreview();
        foreach (string language in new[] { "pl", "en" })
        {
            Localization.SetLanguage(language);
            using var transport = new FakeTransport();
            MainWindow window = CreatePresentationWindow(transport, "localized-main-" + language, new MemoryDesktop());
            try
            {
                RunTask(window.ConnectAsync());
                VerifyAccessibleControls(window);
                VerifyLocalizedWindowText(window, language);
                var mainTabs = Descendants(window).OfType<TabControl>().First();
                foreach (TabItem tab in mainTabs.Items) { tab.IsSelected = true; Pump(); VerifyLocalizedWindowText(window, language); }
                ((TabItem)mainTabs.Items[0]).IsSelected = true;
                Check(AllSettings(window).All(setting => setting.Options.Count >= 2 && !string.IsNullOrWhiteSpace(setting.DisplayName)),
                    "All device settings retain their choices and labels: " + language);
                foreach (Func<Window> openSection in new Func<Window>[] { window.OpenSettingsWindow, window.OpenInformationWindow, window.OpenHelpWindow, window.OpenFirmwareWindow })
                {
                    Window section = openSection();
                    MoveOffscreen(section);
                    Pump();
                    VerifyAccessibleControls(section);
                    if (section is SettingsWindow)
                    {
                        var tabs = FindById<TabControl>(section, "SettingsTabs");
                        foreach (TabItem tab in tabs.Items) { tab.IsSelected = true; Pump(); VerifyLocalizedWindowText(section, language); }
                    }
                    else VerifyLocalizedWindowText(section, language);
                    section.Close();
                    Pump();
                }
                SavePreferences(window, window.CurrentPreferences with { ShowHints = false, TextScale = 200 });
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Pump();
                VerifyLocalizedWindowText(window, language);
                RenderWindow(window, "localization-main-" + language + "-200.png");
                VerifyLanguageSettingsDraft(window, language);
                VerifyRadioModeReadout(window, transport, language);
                using var source = new FakeAudioMeterSource();
                var meter = new AudioMeterWindow(() => 0, source);
                MoveOffscreen(meter);
                meter.Show();
                Pump();
                VerifyAccessibleControls(meter);
                VerifyLocalizedWindowText(meter, language);
                meter.Close();
                Pump();
                Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion), "Language validation never writes receiver settings: " + language);
            }
            finally { window.Close(); Pump(); }
        }
        VerifyLocalizedBehavior();
        Localization.SetLanguage("pl");
    }

    private static void VerifyWizardLanguagePreview()
    {
        Localization.SetLanguage("pl");
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "localized-wizard"));
        var original = new UiPreferences { TextScale = 137 };
        var wizard = new FirstRunWindow(original, store);
        MoveOffscreen(wizard);
        wizard.Show();
        Pump();
        try
        {
            var language = (ComboBox)wizard.FindName("FirstRunLanguageChoice");
            var theme = (ComboBox)wizard.FindName("FirstRunThemeChoice");
            var scale = (ComboBox)wizard.FindName("FirstRunScaleChoice");
            var hints = (CheckBox)wizard.FindName("FirstRunHintsCheckBox");
            Check(language.SelectedIndex == -1 && theme.SelectedIndex == -1, "Wizard explicitly requires both language and theme");
            theme.SelectedValue = "Dark";
            scale.SelectedValue = 175;
            hints.IsChecked = false;
            language.SelectedValue = "en";
            Pump();
            VerifyLocalizedWindowText(wizard, "en");
            VerifyAccessibleControls(wizard);
            Check(theme.SelectedValue is "Dark" && scale.SelectedValue is 175 && hints.IsChecked == false,
                "Language preview preserves the complete wizard draft");
            Check(ReferenceEquals(theme, wizard.FindName("FirstRunThemeChoice")) && ReferenceEquals(language, wizard.FindName("FirstRunLanguageChoice")),
                "Language preview never rebuilds focused controls");
            Check(theme.Items.Cast<ComboBoxItem>().Select(item => item.Content as string).SequenceEqual(new[] { "Light", "Dark" }),
                "Wizard dropdown choices update during English preview");
            Check(((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled && !File.Exists(store.FilePath) && original.Language == "pl",
                "Language preview enables Continue without persisting or mutating original preferences");
            language.SelectedValue = "pl";
            Pump();
            VerifyLocalizedWindowText(wizard, "pl");
            language.SelectedValue = "en";
            Pump();
            RenderWindow(wizard, "localization-wizard-en-175.png");
            ClickFirstRun(wizard, "CancelFirstRunButton");
            Check(wizard.SavedPreferences is null && !File.Exists(store.FilePath), "Cancelling a translated wizard saves nothing");
        }
        finally { if (wizard.IsVisible) wizard.Close(); Pump(); Localization.SetLanguage("pl"); }
    }

    private static void VerifyRadioModeReadout(MainWindow window, FakeTransport transport, string language)
    {
        Window information = window.OpenInformationWindow();
        MoveOffscreen(information);
        Pump();
        var readout = (TextBox)information.FindName("DeviceInformationReadout");
        foreach (byte code in new byte[] { 0, 1, 255 })
        {
            transport.SetRadioModeCode(code);
            RunTask(window.RefreshAsync());
            Check(readout.Text.Contains(Localization.Text("Main.Summary.RadioModeCode", code.ToString(Localization.Culture))),
                "Diagnostics preserve the raw radio-mode code without inventing RF meaning: " + language + "/" + code);
        }
        transport.StatusPayloadLength = 16;
        RunTask(window.RefreshAsync());
        string unknown = Localization.Text("Main.Summary.RadioModeCode", Localization.Text("Main.Summary.RadioModeUnknown"));
        Check(readout.Text.Contains(unknown), "A missing optional field clears previous radio-mode information: " + language);
        transport.StatusPayloadLength = ValidStatus.Length;
        RunTask(window.RefreshAsync());
        window.Disconnect();
        Pump();
        Check(readout.Text.Contains(unknown), "Disconnect clears the diagnostic radio-mode code: " + language);
        RunTask(window.ConnectAsync());
        Check(readout.Text.Contains(Localization.Text("Main.Summary.RadioModeCode", "255")),
            "A new connection refreshes rather than caches the diagnostic code: " + language);
        information.Close();
        Pump();
    }

    private static void VerifyLanguageSettingsDraft(MainWindow window, string language)
    {
        SavePreferences(window, window.CurrentPreferences with { Language = language });
        string target = language == "pl" ? "en" : "pl";
        var settings = window.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        ((ComboBox)settings.FindName("SettingsLanguageChoice")).SelectedValue = target;
        Check(window.CurrentPreferences.Language == language && Localization.CurrentLanguage == language,
            "Unsaved language selection leaves the running application unchanged");
        ((Button)settings.FindName("CancelSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(window.CurrentPreferences.Language == language, "Cancel discards the language draft");
        settings = window.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        ((ComboBox)settings.FindName("SettingsLanguageChoice")).SelectedValue = target;
        Check(Descendants(settings).OfType<TextBlock>().Any(block => block.IsVisible && block.Text == Localization.Text("Settings.Language.RestartNotice")),
            "Restart notice remains visible even with optional hints disabled");
        ((Button)settings.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(window.CurrentPreferences.Language == target && Localization.CurrentLanguage == language && !settings.IsVisible,
            "OK saves the language for next launch without partially translating an active session");
        VerifyLocalizedWindowText(window, language);
    }

    private static void VerifyLocalizedWindowText(Window window, string language)
    {
        Check(string.Equals(window.Language.IetfLanguageTag, Localization.Culture.IetfLanguageTag, StringComparison.OrdinalIgnoreCase),
            "Window advertises the selected language to accessibility: " + window.Name + "/" + language);
        var texts = new List<string> { window.Title };
        foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>().Where(element => element.IsVisible))
        {
            texts.Add(AutomationProperties.GetName(element));
            texts.Add(AutomationProperties.GetHelpText(element));
            if (element is TextBox field) texts.Add(field.Text);
            if (element is TextBlock block) texts.Add(block.Text);
            if (element is ContentControl { Content: string caption }) texts.Add(caption);
            if (element is HeaderedContentControl { Header: string header }) texts.Add(header);
            if (element is AccessText access) texts.Add(access.Text);
        }
        Check(texts.Count > 10 && !texts.Any(text => text.Contains("Missing localization", StringComparison.Ordinal)), "Localized window exposes actual readable text: " + window.Name);
        if (language == "en")
        {
            var polishOnly = Localization.GetEntries("pl").Where(entry => entry.Value != Localization.GetEntries("en")[entry.Key]
                && entry.Value.Length > 6 && !entry.Value.Contains('{')).Select(entry => entry.Value.Replace("_", "")).ToHashSet(StringComparer.Ordinal);
            foreach (string text in texts.Where(text => !string.IsNullOrWhiteSpace(text)))
                Check(!polishOnly.Contains(text.Replace("_", "")), "English UI does not retain a Polish caption: " + window.Name + "/" + text.Replace(Environment.NewLine, " "));
        }
    }
}
