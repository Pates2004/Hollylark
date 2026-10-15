using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyConfirmationOwnershipApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyConfirmationOwnershipChecks(); }
        finally
        {
            try { application.Shutdown(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }

    private static void VerifyConfirmationOwnershipChecks()
    {
        if (Application.Current is null || Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown
            || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run confirmation ownership checks inside the initialized explicit-shutdown STA WPF suite.");

        VerifyConfirmationOwnershipSourceContract();
        VerifyConfirmationOwnershipFallback();
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (bool existingConfiguration in new[] { false, true })
                    VerifyConfirmationOwnershipDeclines(language, existingConfiguration);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
        Console.WriteLine("Confirmation coverage: source contract, actual owner selector and injected responses only; no native MessageBox was inspected.");
    }

    private static void VerifyConfirmationOwnershipSourceContract()
    {
        string constructor = ReadConfirmationOwnershipSource("MainWindow.xaml.cs");
        Check(constructor.Contains("confirmAction=confirm??((message,title)=>MessageBox.Show(GetDialogOwner(),message,title,",
                StringComparison.Ordinal),
            "Source contract: injected confirmations take precedence and the default delegate resolves its owner at invocation");
        Check(constructor.Contains("MessageBox.Show(GetDialogOwner(),message,title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes);",
                StringComparison.Ordinal),
            "Source contract: native confirmation retains Yes/No, Question, default No and success only for Yes");
        Check(constructor.Contains("applyProfile:ApplyAudioProfileAsync,confirm:confirmAction,refreshStatus:ReadProfileStatusAsync,",
                StringComparison.Ordinal),
            "Source contract: MainWindow passes its confirmation delegate to Settings");
        Check(Regex.IsMatch(ReadConfirmationOwnershipSource("MainWindow.Reference.cs"),
                @"internalWindowGetDialogOwner\(\)=>OwnedWindows\.OfType<AuxiliaryWindow>\(\)\.FirstOrDefault\((?<candidate>[A-Za-z_][A-Za-z0-9_]*)=>\k<candidate>\.IsActive\)\?\?(?:\(Window\))?this;",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "Source contract: only active owned auxiliaries precede the main-window fallback");
    }

    private static string ReadConfirmationOwnershipSource(string name, [CallerFilePath] string validationSourcePath = "")
    {
        string projectDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(validationSourcePath)!, "..", ".."));
        string path = Path.Combine(projectDirectory, "src", "LarkControl.Windows", name);
        if (!File.Exists(path))
            throw new InvalidOperationException("Confirmation source-contract checks require the matching checkout: " + path);
        return Regex.Replace(File.ReadAllText(path), @"\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static void VerifyConfirmationOwnershipFallback()
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "confirmation-ownership",
            "selector-" + Guid.NewGuid().ToString("N")));
        var desktop = new MemoryDesktop();
        Check(store.TrySave(new UiPreferences { ConfirmExit = false }, out string? error) && error is null,
            "The unshown owner-selector fixture explicitly disables exit prompts without replacing its confirmation service");
        byte[] originalSettings = File.ReadAllBytes(store.FilePath);
        var owner = new MainWindow(() => [],
            _ => throw new InvalidOperationException("The confirmation selector fixture must not open USB."),
            store.BaseDirectory, desktop, confirm: null, automaticReceiverManagement: false);
        MoveOffscreen(owner);
        try
        {
            IntPtr ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
            Check(ownerHandle != IntPtr.Zero && !owner.IsVisible && !owner.IsActive,
                "Selector fixture creates only an inactive, unshown main HWND");
            Check(ReferenceEquals(owner.GetDialogOwner(), owner), "Selector returns main when it has no owned windows");
            var inactiveSettings = new SettingsWindow(owner.CurrentPreferences, store,
                (_, _) => throw new InvalidOperationException("The selector fixture must not save settings."),
                confirm: (_, _) => true);
            MoveOffscreen(inactiveSettings);
            inactiveSettings.Owner = owner;
            var ordinaryWindow = new Window { Owner = owner };
            MoveOffscreen(ordinaryWindow);
            Check(owner.OwnedWindows.Count == 2 && !inactiveSettings.IsActive && !ordinaryWindow.IsActive,
                "Selector fixture contains an inactive Settings auxiliary and an ordinary owned window");
            Check(ReferenceEquals(owner.GetDialogOwner(), owner)
                && new WindowInteropHelper(owner.GetDialogOwner()).Handle == ownerHandle,
                "Selector ignores inactive owned windows and retains the main HWND fallback");
            inactiveSettings.Close();
            ordinaryWindow.Close();
            Check(owner.OwnedWindows.Count == 0 && ReferenceEquals(owner.GetDialogOwner(), owner),
                "Selector does not retain a closed auxiliary as owner");
            Check(originalSettings.SequenceEqual(File.ReadAllBytes(store.FilePath)) && desktop.StartupWrites == 0 && desktop.TrayShows == 0,
                "Unshown selector checks do not change their explicit fixture preferences or request desktop integration");
        }
        finally { owner.RequestExit(); Pump(); }
    }

    private static void VerifyConfirmationOwnershipDeclines(string language, bool existingConfiguration)
    {
        string scenario = $"confirmation-ownership/{language}/{(existingConfiguration ? "existing" : "absent")}-{Guid.NewGuid():N}";
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, scenario));
        if (existingConfiguration)
            Check(store.TrySave(new UiPreferences { Language = language, FirstRunCompleted = true, Theme = "Light", TextScale = 125 },
                    out string? error) && error is null,
                "Create isolated confirmation configuration: " + scenario);
        byte[]? fileBefore = File.Exists(store.FilePath) ? File.ReadAllBytes(store.FilePath) : null;
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        bool consent = false;
        MainWindow? callbackOwner = null;
        var prompts = new List<(string Message, string Title, Window? Owner, bool SettingsActive)>();
        Func<string, string, bool> confirm = (message, title) =>
        {
            prompts.Add((message, title, callbackOwner?.GetDialogOwner(),
                callbackOwner?.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsActive) == true));
            return consent;
        };
        MainWindow owner = CreatePresentationWindow(transport, scenario, desktop, confirm);
        callbackOwner = owner;
        try
        {
            Check(ReferenceEquals(ReadConfirmationOwnershipField<Func<string, string, bool>>(owner, "confirmAction"), confirm),
                "MainWindow preserves the exact injected confirmation delegate: " + scenario);
            RunTask(owner.ConnectAsync());
            string committedBefore = JsonSerializer.Serialize(owner.CurrentPreferences);
            SettingsWindow settings = OpenTransactionSettings(owner);
            Check(ReferenceEquals(settings.Owner, owner)
                && ReferenceEquals(ReadConfirmationOwnershipField<Func<string, string, bool>>(settings, "confirm"), confirm),
                "Actual MainWindow Settings action preserves both ownership and the injected delegate: " + scenario);
            SetChoice(settings, "SettingsThemeChoice", "Dark");
            SetChoice(settings, "SettingsTextScaleChoice", 150);
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            var profileName = (TextBox)settings.FindName("AudioProfileNameTextBox");
            var profiles = (ListBox)settings.FindName("AudioProfilesList");
            profileName.Text = "Confirmation ownership draft";
            ClickSettings(settings, "SaveAudioProfileButton");
            PumpUntil(() => !owner.IsBusy && profiles.Items.Count == 1);
            var selectedProfile = (AudioProfile)profiles.Items[0];
            profiles.SelectedItem = selectedProfile;
            profileName.Text = "Uncaptured confirmation draft";
            UiPreferences staged = ReadConfirmationOwnershipField<UiPreferences>(settings, "draft");
            string draftBefore = JsonSerializer.Serialize(staged);
            int writesBefore = CountMockWrites(transport);
            Check(staged.Theme == "Dark" && staged.TextScale == 150 && staged.AudioProfiles.SequenceEqual(new[] { selectedProfile })
                && owner.CurrentPreferences.AudioProfiles.Length == 0 && writesBefore == 0 && prompts.Count == 0,
                "Fixture stages appearance, a fake-receiver profile and an uncaptured name without confirmation or writes: " + scenario);
            VerifyUnchanged("initial draft");

            SelectSettingsCategory(settings, "AppearanceSettingsTab");
            VerifyDecline("ResetAppearanceSettingsButton", "Settings.Reset.Title",
                Localization.Text("Settings.Reset.Confirmation", Localization.Text("Settings.Appearance.ResetScope")));
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            VerifyDecline("ResetProfilesSettingsButton", "Settings.Reset.Title",
                Localization.Text("Settings.Reset.Confirmation", Localization.Text("Settings.Profiles.ResetScope"))
                + Localization.Text("Settings.Reset.ProfilesNotice"));
            VerifyDecline("ResetAllSettingsButton", "Settings.Reset.Title",
                Localization.Text("Settings.Reset.Confirmation", Localization.Text("Settings.ResetAll.Scope"))
                + Localization.Text("Settings.Reset.ProfilesNotice"));
            VerifyDecline("DeleteAudioProfileButton", "Settings.Profiles.Delete.Title",
                Localization.Text("Settings.Profiles.Delete.Confirmation", selectedProfile.Name));
            VerifyDecline("ApplyAudioProfileButton", "Settings.Profiles.Apply.Title",
                Localization.Text("Settings.Profiles.Apply.Confirmation", selectedProfile.Name));

            consent = true;
            SelectSettingsCategory(settings, "AppearanceSettingsTab");
            int acceptedPromptCount = prompts.Count;
            ClickSettings(settings, "ResetAppearanceSettingsButton");
            Check(prompts.Count == acceptedPromptCount + 1
                && JsonSerializer.Serialize(ReadConfirmationOwnershipField<UiPreferences>(settings, "draft"))
                    == JsonSerializer.Serialize(staged with { Theme = "Light", TextScale = 100 })
                && profileName.Text == "Uncaptured confirmation draft",
                "Injected Yes still resets only the requested draft category: " + scenario);
            VerifyCommittedUnchanged("accepted draft-only reset");

            consent = false;
            int savePromptCount = prompts.Count;
            Check(settings.IsActive && ReferenceEquals(owner.GetDialogOwner(), settings),
                "The native owner selector resolves Settings before its save decision: " + scenario);
            ClickSettings(settings, "SaveSettingsButton");
            Check(prompts.Count == savePromptCount && settings.IsVisible && profileName.IsKeyboardFocused
                && ((TextBlock)settings.FindName("SectionResultReadout")).Text
                    == Localization.Text("Settings.Save.PendingProfile", profileName.Text),
                "OK protects the pending profile name with a focused error, not a save confirmation: " + scenario);
            VerifyCommittedUnchanged("pending profile blocked save");
            ClickSettings(settings, "CancelSettingsButton");
            Check(prompts.Count == savePromptCount + 1 && prompts[^1].SettingsActive
                && ReferenceEquals(prompts[^1].Owner, settings)
                && prompts[^1].Title == Localization.Text("Settings.Save.Title")
                && prompts[^1].Message == Localization.Text("Settings.Save.Confirmation"),
                "Closing invokes the injected No exactly once with its active owner and short save question: " + scenario);
            Check(!settings.IsVisible && !owner.IsBusy,
                "No on close discards the draft without a second confirmation: " + scenario);
            VerifyCommittedUnchanged("declined save");

            void VerifyDecline(string identifier, string titleKey, string message)
            {
                var button = (Button)settings.FindName(identifier);
                Check(button.IsVisible && button.IsEnabled && settings.IsActive,
                    "Confirmation requires an enabled action in locally active Settings; no activation is forced: " + identifier + "/" + scenario);
                IntPtr settingsHandle = new WindowInteropHelper(settings).Handle;
                Check(settingsHandle != IntPtr.Zero && ReferenceEquals(owner.GetDialogOwner(), settings)
                    && new WindowInteropHelper(owner.GetDialogOwner()).Handle == settingsHandle,
                    "Actual owner selector resolves the initiating Settings HWND: " + identifier + "/" + scenario);
                int promptCount = prompts.Count;
                ClickSettings(settings, identifier);
                Check(prompts.Count == promptCount + 1 && prompts[^1].Message == message
                    && prompts[^1].Title == Localization.Text(titleKey),
                    "Real Settings action invokes the injected decline exactly once with its localized prompt: " + identifier + "/" + scenario);
                Check(prompts[^1].SettingsActive && ReferenceEquals(prompts[^1].Owner, settings),
                    "Selector still resolves active Settings at injected confirmation time: " + identifier + "/" + scenario);
                VerifyUnchanged(identifier);
            }

            void VerifyUnchanged(string action)
            {
                Check(settings.IsVisible && !owner.IsBusy
                    && JsonSerializer.Serialize(ReadConfirmationOwnershipField<UiPreferences>(settings, "draft")) == draftBefore
                    && ((ComboBox)settings.FindName("SettingsThemeChoice")).SelectedValue is "Dark"
                    && ((ComboBox)settings.FindName("SettingsTextScaleChoice")).SelectedValue is 150
                    && profiles.Items.Cast<AudioProfile>().SequenceEqual(new[] { selectedProfile })
                    && ReferenceEquals(profiles.SelectedItem, selectedProfile)
                    && profileName.Text == "Uncaptured confirmation draft",
                    "Settings retains the open window, full draft, controls, selected profile and pending name: " + action + "/" + scenario);
                VerifyCommittedUnchanged(action);
            }

            void VerifyCommittedUnchanged(string action)
            {
                Check(JsonSerializer.Serialize(owner.CurrentPreferences) == committedBefore
                    && (fileBefore is null ? !File.Exists(store.FilePath)
                        : File.Exists(store.FilePath) && File.ReadAllBytes(store.FilePath).SequenceEqual(fileBefore)),
                    "Confirmation action preserves committed preferences and settings-file bytes or absence: " + action + "/" + scenario);
                Check(CountMockWrites(transport) == writesBefore && desktop.StartupWrites == 0 && desktop.TrayShows == 0,
                    "Confirmation action sends no receiver writes or desktop-integration requests: " + action + "/" + scenario);
            }
        }
        finally { consent = true; owner.RequestExit(); Pump(); }
    }

    private static TValue ReadConfirmationOwnershipField<TValue>(object target, string name) where TValue : class =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as TValue
        ?? throw new InvalidOperationException("Confirmation fixture field is missing or has an unexpected type: " + name);
}
