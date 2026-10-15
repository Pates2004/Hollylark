using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Windows;

internal static partial class Program
{
    private static readonly string ReportDirectory = Path.Combine(AppContext.BaseDirectory, "reports",
        "validation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));

    private static void VerifyPreferences()
    {
        string preferencesDirectory = Path.Combine(ReportDirectory, "preferences");
        var store = new UiPreferencesStore(preferencesDirectory);
        UiPreferences defaults = store.Load(out string? error);
        Check(defaults.ShowHints && error is null && !File.Exists(store.FilePath), "Missing preferences use defaults without writing a file");
        Check(store.FilePath == Path.Combine(preferencesDirectory, "data", "settings.json"), "Preferences are relative to the supplied executable base");
        Check(new UiPreferencesStore().BaseDirectory == Path.GetFullPath(AppContext.BaseDirectory), "Default data root is actual executable directory, not terminal CWD");
        Check(store.TrySave(defaults with { ShowHints = false }, out error) && error is null, "Hints preference saves");
        UiPreferences reloaded = new UiPreferencesStore(preferencesDirectory).Load(out error);
        Check(!reloaded.ShowHints && error is null, "Hints preference survives a new store instance");
        Check(store.TrySave(reloaded with { ShowHints = true }, out error) && error is null, "Hints can be re-enabled");
        Check(new UiPreferencesStore(preferencesDirectory).Load(out _).ShowHints, "Enabled preference survives reload");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(), "Successful atomic save leaves no temporary files");

        string[] invalidDocuments = ["{broken", "null", "{\"SchemaVersion\":99,\"ShowHints\":false}", "{\"Unknown\":42}", "{\"ShowHints\":\"wrong type\"}"];
        for (int index = 0; index < invalidDocuments.Length; index++)
        {
            var invalidStore = new UiPreferencesStore(Path.Combine(ReportDirectory, "invalid-" + index));
            Directory.CreateDirectory(Path.GetDirectoryName(invalidStore.FilePath)!);
            File.WriteAllText(invalidStore.FilePath, invalidDocuments[index]);
            invalidStore.Load(out error);
            Check(error is not null, $"Malformed/unsupported preferences case {index} reported");
            Check(!invalidStore.TrySave(new UiPreferences(), out error) && error is not null,
                $"Malformed preferences case {index} cannot be silently replaced");
            Check(File.ReadAllText(invalidStore.FilePath) == invalidDocuments[index], $"Malformed preferences case {index} preserved byte-for-byte");
        }
        var blockedStore = new UiPreferencesStore(Path.Combine(ReportDirectory, "blocked"));
        Directory.CreateDirectory(blockedStore.BaseDirectory);
        File.WriteAllText(Path.Combine(blockedStore.BaseDirectory, "data"), "A file intentionally blocks the preferences directory.");
        Check(!blockedStore.TrySave(new UiPreferences(), out error) && error is not null, "Unwritable location reports failure rather than falling back");
        Check(!store.TrySave(new UiPreferences { SchemaVersion = 99 }, out error), "Unsupported schema cannot be saved");

        var button = new Button();
        var parent = new StackPanel();
        parent.Children.Add(button);
        AutomationProperties.SetName(button, "Essential control label");
        UsageHints.SetText(button, "Optional usage hint");
        Check(AutomationProperties.GetHelpText(button) == "Optional usage hint", "Enabled hint appears in accessibility help text");
        UsageHints.SetEnabled(parent, false);
        Check(AutomationProperties.GetHelpText(button) == string.Empty, "Inherited hint setting removes optional screen-reader hints");
        Check(AutomationProperties.GetName(button) == "Essential control label", "Disabling hints preserves essential accessible name");
        UsageHints.SetEnabled(parent, true);
        Check(AutomationProperties.GetHelpText(button) == "Optional usage hint", "Screen-reader hints can be restored");
    }
}
