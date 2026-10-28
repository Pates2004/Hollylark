using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    // Pure persistence checks: no Application, windows, native services or receiver access.
    public static void VerifyBatteryAlertPreferencesChecks()
    {
        var defaults = new UiPreferences();
        Check(UiPreferences.CurrentSchemaVersion == 5 && defaults.BatteryAlerts == new BatteryAlertPreferences()
            && defaults.BatteryAlerts.Policy == new BatteryAlertPolicy { Enabled = true, LowThreshold = 20, CriticalThreshold = 10 }
            && defaults.BatteryAlerts.Low == new BatteryAlertOutput { Notification = false, Speech = true, Sound = false }
            && defaults.BatteryAlerts.Critical == defaults.BatteryAlerts.Low,
            "Schema5 defaults enable low20/critical10 direct warnings with OS notifications and sound opt-in");
        var missing = BatteryPreferencesStore("missing");
        Check(missing.Load(out string? error).BatteryAlerts == defaults.BatteryAlerts && error is null
            && !File.Exists(missing.FilePath), "Missing battery preferences use defaults without creating a profile");

        VerifyBatteryPreferencesMigration();
        VerifyBatteryPreferencesRoundTrips();
        VerifyBatteryPreferencesRejectedSaves();
        VerifyBatteryPreferencesMalformedDocuments();
    }

    private static UiPreferencesStore BatteryPreferencesStore(string scenario) =>
        new(Path.Combine(ReportDirectory, "battery-preferences", scenario + "-" + Guid.NewGuid().ToString("N")));

    private static void VerifyBatteryPreferencesMigration()
    {
        var serializer = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var original = new UiPreferences
        {
            FirstRunCompleted = true, Language = "en", Theme = "Dark", TextScale = 150,
            ShowHints = false, AnnounceStateChanges = false, ShowOperationMessages = false, ConfirmExit = false,
            PreserveVolatileSettings = false, HiddenRows = ["TX4"],
            AudioProfiles = [AudioProfile.FromStatus("Preserved microphone profile", A1Status.Parse(ValidStatus))]
        };
        foreach (int version in new[] { 1, 2, 3, 4, 5 })
        {
            var store = BatteryPreferencesStore("schema" + version);
            JsonObject document = version == 1
                ? new JsonObject { ["SchemaVersion"] = 1, ["ShowHints"] = false }
                : JsonSerializer.SerializeToNode(original, serializer)!.AsObject();
            document["SchemaVersion"] = version;
            document.Remove("BatteryAlerts");
            if (version is 2 or 3)
            {
                document.Remove("Hotkeys");
                document.Remove("GlobalHotkeysEnabled");
                document.Remove("PreserveVolatileSettings");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, document.ToJsonString());
            byte[] before = File.ReadAllBytes(store.FilePath);
            UiPreferences loaded = store.Load(out string? error);
            Check(error is null && loaded.SchemaVersion == 5 && loaded.BatteryAlerts == new BatteryAlertPreferences()
                && !loaded.ShowHints && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                $"Schema{version} without battery options loads defaults and preserves original bytes");
            if (version > 1)
                Check(loaded.Language == original.Language && loaded.Theme == original.Theme && loaded.TextScale == 150
                    && !loaded.AnnounceStateChanges && !loaded.ShowOperationMessages && !loaded.ConfirmExit
                    && loaded.AudioProfiles.SequenceEqual(original.AudioProfiles) && loaded.HiddenRows.SequenceEqual(original.HiddenRows)
                    && loaded.PreserveVolatileSettings == (version < 4),
                    $"Schema{version} preserves profiles, accessibility and existing automation preferences");
            Check(store.TrySave(loaded, out error) && error is null, $"Schema{version} explicitly saves migrated settings");
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
            Check(saved.RootElement.GetProperty("SchemaVersion").GetInt32() == 5
                && saved.RootElement.GetProperty("BatteryAlerts").ValueKind == JsonValueKind.Object
                && new UiPreferencesStore(store.BaseDirectory).Load(out error).BatteryAlerts == loaded.BatteryAlerts && error is null,
                $"Schema{version} migration persists explicit schema5 battery options on user save");
        }
    }

    private static void VerifyBatteryPreferencesRoundTrips()
    {
        var store = BatteryPreferencesStore("channel-matrix");
        AudioProfile profile = AudioProfile.FromStatus("Battery tests preserve this profile", A1Status.Parse(ValidStatus));
        for (int mask = 0; mask < 128; mask++)
        {
            var alerts = new BatteryAlertPreferences
            {
                Policy = new BatteryAlertPolicy { Enabled = (mask & 64) == 0, LowThreshold = 37, CriticalThreshold = 13 },
                Low = new BatteryAlertOutput { Notification = (mask & 1) != 0, Speech = (mask & 2) != 0, Sound = (mask & 4) != 0 },
                Critical = new BatteryAlertOutput { Notification = (mask & 8) != 0, Speech = (mask & 16) != 0, Sound = (mask & 32) != 0 }
            };
            var candidate = new UiPreferences
            {
                FirstRunCompleted = true, BatteryAlerts = alerts, ShowHints = false, AnnounceStateChanges = false,
                ShowOperationMessages = false, PreserveVolatileSettings = false, AudioProfiles = [profile]
            };
            Check(store.TrySave(candidate, out string? error) && error is null, "Save independent battery channel matrix " + mask);
            UiPreferences reloaded = new UiPreferencesStore(store.BaseDirectory).Load(out error);
            Check(error is null && reloaded.BatteryAlerts == alerts && !reloaded.ShowHints && !reloaded.AnnounceStateChanges
                && !reloaded.ShowOperationMessages && !reloaded.PreserveVolatileSettings
                && reloaded.AudioProfiles.SequenceEqual(candidate.AudioProfiles),
                "A fresh store preserves all seven battery toggles, custom thresholds and unrelated preferences: " + mask);
        }
        foreach ((int low, int critical) in new[] { (2, 1), (100, 99), (100, 1) })
        {
            var alerts = new BatteryAlertPreferences { Policy = new BatteryAlertPolicy { LowThreshold = low, CriticalThreshold = critical } };
            Check(store.TrySave(new UiPreferences { BatteryAlerts = alerts }, out string? error) && error is null
                && new UiPreferencesStore(store.BaseDirectory).Load(out error).BatteryAlerts == alerts && error is null,
                $"Threshold boundaries {critical} < {low} persist without clamping");
        }
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(),
            "Battery preference saves leave no temporary settings files");
    }

    private static void VerifyBatteryPreferencesRejectedSaves()
    {
        var store = BatteryPreferencesStore("rejected-save");
        var original = new UiPreferences { FirstRunCompleted = true, AudioProfiles = [AudioProfile.FromStatus("Keep", A1Status.Parse(ValidStatus))] };
        Check(store.TrySave(original, out _), "Prepare an existing profile for rejected battery saves");
        byte[] before = File.ReadAllBytes(store.FilePath);
        BatteryAlertPreferences[] invalid =
        [
            new() { Policy = null! }, new() { Low = null! }, new() { Critical = null! },
            .. new[] { (0, 10), (20, 0), (20, -1), (101, 10), (20, 20), (10, 20), (100, 100), (int.MaxValue, 1) }
                .Select(pair => new BatteryAlertPreferences
                {
                    Policy = new BatteryAlertPolicy { LowThreshold = pair.Item1, CriticalThreshold = pair.Item2 }
                })
        ];
        foreach (BatteryAlertPreferences alerts in invalid.Append(null!))
        {
            Check(!store.TrySave(original with { BatteryAlerts = alerts }, out string? error) && !string.IsNullOrWhiteSpace(error)
                && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Invalid nested battery values cannot replace an existing profile");
            if (alerts is not null && alerts.Policy is not null)
                Check(!store.TrySave(original with { BatteryAlerts = alerts with { Policy = alerts.Policy with { Enabled = false } } }, out error)
                    && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "Master disable does not permit invalid thresholds or null outputs to be persisted");
        }
        Check(new UiPreferencesStore(store.BaseDirectory).Load(out _).AudioProfiles.SequenceEqual(original.AudioProfiles)
            && !Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(),
            "Rejected saves preserve audio profiles and leave no partial files");
    }

    private static void VerifyBatteryPreferencesMalformedDocuments()
    {
        string[] invalidAlerts =
        [
            "null", "[]", "false", "{\"Policy\":null}", "{\"Low\":null}", "{\"Critical\":null}",
            "{\"Policy\":{\"Enabled\":\"false\"}}", "{\"Policy\":{\"LowThreshold\":\"20\"}}",
            "{\"Policy\":{\"LowThreshold\":20.5}}", "{\"Policy\":{\"CriticalThreshold\":0}}",
            "{\"Policy\":{\"LowThreshold\":101}}", "{\"Policy\":{\"LowThreshold\":10,\"CriticalThreshold\":20}}",
            "{\"Policy\":{\"LowThreshold\":20,\"CriticalThreshold\":20}}",
            "{\"Policy\":{\"Enabled\":false,\"LowThreshold\":10,\"CriticalThreshold\":20}}",
            "{\"Low\":{\"Notification\":null}}", "{\"Low\":{\"Speech\":1}}",
            "{\"Critical\":{\"Sound\":\"true\"}}", "{\"Low\":{\"Speech\":true,\"Speech\":false}}",
            "{\"Policy\":{\"LowThreshold\":20,\"LowThreshold\":30}}",
            "{\"Low\":{\"Mute\":true}}", "{\"Critical\":{\"PowerOff\":true}}", "{\"Unknown\":true}"
        ];
        foreach (string alerts in invalidAlerts)
        {
            var store = BatteryPreferencesStore("malformed");
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{\"SchemaVersion\":5,\"BatteryAlerts\":" + alerts + "}");
            byte[] before = File.ReadAllBytes(store.FilePath);
            UiPreferences fallback = store.Load(out string? error);
            Check(!string.IsNullOrWhiteSpace(error) && fallback.BatteryAlerts == new BatteryAlertPreferences()
                && !store.TrySave(new UiPreferences(), out _) && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Malformed battery document reports an error and cannot be silently replaced: " + alerts);
        }
    }
}
