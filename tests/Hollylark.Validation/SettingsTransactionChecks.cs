using System.IO;
using System.Windows;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifySettingsTransactions()
    {
        using var transport = new FakeTransport();
        bool consent = true;
        MainWindow owner = CreatePresentationWindow(transport, "settings-transactions", new MemoryDesktop(), (_, _) => consent);
        try
        {
            RunTask(owner.ConnectAsync());
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "settings-transactions"));
            SettingsWindow settings = OpenTransactionSettings(owner);
            SetChoice(settings, "SettingsThemeChoice", "Dark");
            SetChoice(settings, "SettingsTextScaleChoice", 150);
            Check(owner.CurrentPreferences.Theme == "Light" && !File.Exists(store.FilePath), "Appearance selection remains a draft before OK");
            consent = false;
            settings.Close();
            Check(!settings.IsVisible && owner.CurrentPreferences.Theme == "Light" && !File.Exists(store.FilePath),
                "No on dirty close discards the draft without saving");
            settings = OpenTransactionSettings(owner);
            SetChoice(settings, "SettingsThemeChoice", "Dark");
            SetChoice(settings, "SettingsTextScaleChoice", 150);
            consent = true;
            ClickSettings(settings, "ResetAppearanceSettingsButton");
            Check(((ComboBox)settings.FindName("SettingsThemeChoice")).SelectedValue is "Light", "Category reset restores its draft controls");
            SetChoice(settings, "SettingsThemeChoice", "Dark");
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            ((TextBox)settings.FindName("AudioProfileNameTextBox")).Text = "Próba LARK";
            int writes = CountMockWrites(transport);
            ClickSettings(settings, "SaveAudioProfileButton");
            var profiles = (ListBox)settings.FindName("AudioProfilesList");
            Check(profiles.Items.Count == 1 && owner.CurrentPreferences.AudioProfiles.Length == 0 && !File.Exists(store.FilePath), "Capture adds confirmed audio profile to draft only");
            Check(CountMockWrites(transport) == writes, "Capturing a profile never changes receiver settings");
            ClickSettings(settings, "SaveSettingsButton");
            Check(!settings.IsVisible && owner.CurrentPreferences.Theme == "Dark" && owner.CurrentPreferences.AudioProfiles.Single().Name == "Próba LARK", "OK commits appearance and profile together");
            Check(store.Load(out _).AudioProfiles.Length == 1, "Profile captured through UI survives file reload");
            settings = OpenTransactionSettings(owner);
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            ClickSettings(settings, "DeleteAudioProfileButton");
            Check(((ListBox)settings.FindName("AudioProfilesList")).Items.Count == 0, "Delete changes the profile draft");
            consent = false;
            ClickSettings(settings, "CancelSettingsButton");
            Check(owner.CurrentPreferences.AudioProfiles.Length == 1 && store.Load(out _).AudioProfiles.Length == 1, "Cancelling profile deletion preserves committed list");
            settings = OpenTransactionSettings(owner);
            Check(!((Button)settings.FindName("ResetAllSettingsButton")).IsVisible,
                "Global reset is not exposed on the initial Appearance page");
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            consent = true;
            ClickSettings(settings, "ResetAllSettingsButton");
            Check(((ListBox)settings.FindName("AudioProfilesList")).Items.Count == 0, "Global reset clears profiles only in draft");
            consent = false;
            ClickSettings(settings, "CancelSettingsButton");
            Check(owner.CurrentPreferences.Theme == "Dark" && owner.CurrentPreferences.AudioProfiles.Length == 1, "Cancelling global reset preserves complete configuration");
            SettingRowViewModel gain = owner.AudioSettings.Single(setting => setting.Id == "Gain");
            gain.SelectedIndex = 0;
            RunTask(owner.ApplySettingAsync(gain));
            settings = OpenTransactionSettings(owner);
            SelectSettingsCategory(settings, "ProfilesSettingsTab");
            consent = false;
            writes = CountMockWrites(transport);
            ClickSettings(settings, "ApplyAudioProfileButton");
            Check(CountMockWrites(transport) == writes, "Declining profile confirmation sends nothing");
            consent = true;
            ClickSettings(settings, "ApplyAudioProfileButton");
            PumpUntil(() => !owner.IsBusy && gain.SelectedIndex == 3);
            Check(gain.SelectedIndex == 3 && !gain.IsDirty, "Actual settings profile button invokes validated hardware application");
            ClickSettings(settings, "CancelSettingsButton");
            Check(gain.SelectedIndex == 3, "Cancel does not pretend to roll back an already applied audio profile");
        }
        finally { consent = true; owner.RequestExit(); }
    }

    private static SettingsWindow OpenTransactionSettings(MainWindow owner)
    {
        var settings = (SettingsWindow)owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        return settings;
    }

    private static void SelectSettingsCategory(SettingsWindow settings, string identifier)
    {
        ((TabControl)settings.FindName("SettingsTabs")).SelectedItem = settings.FindName(identifier);
        Pump();
    }

    private static void SetChoice(SettingsWindow settings, string identifier, object value)
    {
        ((ComboBox)settings.FindName(identifier)).SelectedValue = value;
        Pump();
    }

    private static void ClickSettings(SettingsWindow settings, string identifier)
    {
        ((Button)settings.FindName(identifier)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static void VerifyNativeTrayLifecycle()
    {
        if (skipNativeTraySmoke)
        {
            Console.WriteLine("SKIP: Native Explorer tray smoke is excluded on this desktop; run --native-tray-only separately.");
            return;
        }
        var registry = new DesktopStartupStore();
        using var integration = new DesktopIntegration(DesktopExecutablePath, registry, _ => FileAttributes.Normal);
        var failures = new List<Exception>();
        integration.Failed += failures.Add;
        var window = new MainWindow(() => [], _ => throw new InvalidOperationException("Native tray test cannot open USB."),
            Path.Combine(ReportDirectory, "native-tray"), integration, (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        try
        {
            window.Show();
            PumpUntil(() => !window.IsBusy);
            SavePreferences(window, window.CurrentPreferences with { CloseToTray = true });
            Check(failures.Count == 0, "Native Shell_NotifyIcon adds a keyboard-capable icon without error");
            window.Close();
            Pump();
            Check(!window.IsVisible, "Main window closes to real Windows tray without an exception");
            window.RestoreFromTray();
            Check(window.IsVisible && window.ShowInTaskbar, "Main window restores after native tray hide");
            SavePreferences(window, window.CurrentPreferences with { CloseToTray = false });
            SavePreferences(window, window.CurrentPreferences with { CloseToTray = true });
            Check(failures.Count == 0, "Native tray icon can be removed and recreated");
        }
        finally { window.RequestExit(); }
        Check(failures.Count == 0, "Native tray cleanup on actual owner close succeeds");
        Check(registry.ReadCount == 0 && registry.WriteCount == 0 && registry.DeleteCount == 0, "Native tray smoke never accesses even the fake startup store");
    }
}
