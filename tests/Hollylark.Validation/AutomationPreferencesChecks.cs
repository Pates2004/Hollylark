using System.IO;
using System.Text.Json;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyAutomationPreferences()
    {
        string directory = Path.Combine(ReportDirectory, "automation-preferences");
        var store = new UiPreferencesStore(directory);
        HotkeyBinding[] bindings = HotkeyBinding.CreateDefaults();
        bindings[0] = bindings[0] with { Gesture = "Ctrl+Shift+J" };
        var candidate = new UiPreferences { FirstRunCompleted = true, GlobalHotkeysEnabled = true, Hotkeys = bindings, PreserveVolatileSettings = false };
        Check(store.TrySave(candidate, out string? error) && error is null, "Shortcut assignments and automation preferences persist beside the EXE");
        UiPreferences reloaded = new UiPreferencesStore(directory).Load(out error);
        Check(error is null && reloaded.GlobalHotkeysEnabled && !reloaded.PreserveVolatileSettings
            && reloaded.Hotkeys.SequenceEqual(bindings), "Every action binding and both automation toggles survive a store reload");
        string legacy = "{\"SchemaVersion\":3,\"FirstRunCompleted\":true,\"ShowHints\":false,\"AnnounceStateChanges\":false,\"ConfirmExit\":false}";
        File.WriteAllText(store.FilePath, legacy);
        reloaded = new UiPreferencesStore(directory).Load(out error);
        Check(error is null && reloaded.SchemaVersion == UiPreferences.CurrentSchemaVersion && reloaded.PreserveVolatileSettings
            && !reloaded.GlobalHotkeysEnabled && !reloaded.AnnounceStateChanges && !reloaded.ShowHints && !reloaded.ConfirmExit,
            "Schema3 adds default-on retention and opt-in shortcuts without changing existing accessibility or exit preferences");
        Check(File.ReadAllText(store.FilePath) == legacy, "Loading schema3 does not silently rewrite user preferences");
        foreach (string invalid in new[]
        {
            "{\"SchemaVersion\":4,\"Hotkeys\":null}", "{\"SchemaVersion\":4,\"Hotkeys\":[]}",
            "{\"SchemaVersion\":4,\"GlobalHotkeysEnabled\":\"false\"}", "{\"SchemaVersion\":4,\"PreserveVolatileSettings\":null}",
            JsonSerializer.Serialize(candidate with { Hotkeys = [new((HotkeyAction)999, "Ctrl+J")] })
        })
        {
            File.WriteAllText(store.FilePath, invalid);
            var unreadable = new UiPreferencesStore(directory);
            unreadable.Load(out error);
            Check(error is not null && !unreadable.TrySave(new UiPreferences(), out _) && File.ReadAllText(store.FilePath) == invalid,
                "Malformed automation preferences cannot be silently replaced");
        }
    }
}
