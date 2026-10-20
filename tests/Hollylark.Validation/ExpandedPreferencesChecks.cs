using System.IO;
using System.Text.Json;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyExpandedPreferences()
    {
        var defaults = new UiPreferences();
        defaults.Validate();
        Check(defaults.SchemaVersion == UiPreferences.CurrentSchemaVersion && defaults.Language == "pl" && defaults.Theme == "Light" && defaults.TextScale == 100,
            "Expanded preferences use the current schema and light appearance");
        Check(defaults.GainControlMode == GainControlMode.Choice && defaults.ValueDisplayMode == ValueDisplayMode.StaticText
            && defaults.ShowHints && defaults.ShowOperationMessages && defaults.AnnounceStateChanges,
            "Expanded control defaults use native choices and non-editable static readouts");
        Check(!defaults.AutoApplySelections && !defaults.StartWithWindows && !defaults.AutoConnectAtStartup
            && !defaults.CloseToTray && defaults.ConfirmExit && defaults.HiddenRows.Length == 0 && defaults.AudioProfiles.Length == 0,
            "Expanded defaults confirm exit without opting into startup, device writes, tray behavior or profiles");
        Check(UiPreferences.SupportedRowIds.SequenceEqual(new[]
        {
            "Microphones", "Gain", "Noise", "Equalizer", "Reverb", "ChannelMode", "Lights", "AutoOff", "Identification", "TX1", "TX2", "TX3", "TX4"
        }), "Row identifiers contain only supported LARK categories");

        VerifyExpandedMigration();
        VerifyAudioProfiles();
        VerifyExpandedInvalidFiles();

        AudioProfile profile = AudioProfile.FromStatus("Nagranie próbne", A1Status.Parse(ValidStatus));
        UiPreferences expanded = defaults with
        {
            Theme = "Dark", TextScale = 137, HiddenRows = ["Microphones", "Lights"],
            GainControlMode = GainControlMode.Slider, ValueDisplayMode = ValueDisplayMode.None,
            AutoApplySelections = true, ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false,
            StartWithWindows = true, AutoConnectAtStartup = true, CloseToTray = true, ConfirmExit = false, AudioProfiles = [profile]
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "expanded-preferences", "round-trip"));
        Check(store.TrySave(expanded, out string? error) && error is null, "All expanded preferences save explicitly");
        UiPreferences loaded = new UiPreferencesStore(store.BaseDirectory).Load(out error);
        Check(error is null && EquivalentExpandedPreferences(expanded, loaded), "Every expanded field, including explicit ConfirmExit=false, survives a store round-trip");
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
        {
            Check(document.RootElement.GetProperty("GainControlMode").GetString() == "Slider"
                && document.RootElement.GetProperty("ValueDisplayMode").GetString() == "None",
                "Preference enums persist as supported names, not numeric values");
        }
        foreach (string theme in new[] { "Light", "Dark" })
        foreach (int scale in new[] { 100, 101, 137, 199, 200 })
        foreach (GainControlMode gainMode in Enum.GetValues<GainControlMode>())
        foreach (ValueDisplayMode valueMode in Enum.GetValues<ValueDisplayMode>())
        {
            UiPreferences candidate = defaults with { Theme = theme, TextScale = scale, GainControlMode = gainMode, ValueDisplayMode = valueMode };
            candidate.Validate();
            Check(store.TrySave(candidate, out error) && error is null
                && EquivalentExpandedPreferences(candidate, new UiPreferencesStore(store.BaseDirectory).Load(out error)) && error is null,
                $"Appearance and enum combination round-trips: {theme}/{scale}/{gainMode}/{valueMode}");
        }
        UiPreferences allRows = defaults with { HiddenRows = UiPreferences.SupportedRowIds.ToArray() };
        allRows.Validate();
        Check(store.TrySave(allRows, out error) && error is null
            && new UiPreferencesStore(store.BaseDirectory).Load(out _).HiddenRows.SequenceEqual(allRows.HiddenRows),
            "Every supported row can be hidden explicitly");

        UiPreferences[] invalidPreferences =
        [
            defaults with { SchemaVersion = 1 }, defaults with { SchemaVersion = UiPreferences.CurrentSchemaVersion + 1 },
            defaults with { Language = "de" }, defaults with { Language = "" }, defaults with { Language = null! },
            defaults with { Theme = null! }, defaults with { Theme = "dark" }, defaults with { Theme = "Unknown" },
            defaults with { Theme = "System" },
            defaults with { TextScale = 99 }, defaults with { TextScale = 201 },
            defaults with { GainControlMode = (GainControlMode)(-1) }, defaults with { GainControlMode = (GainControlMode)3 },
            defaults with { ValueDisplayMode = (ValueDisplayMode)(-1) }, defaults with { ValueDisplayMode = (ValueDisplayMode)3 },
            defaults with { HiddenRows = null! }, defaults with { HiddenRows = [null!] },
            defaults with { HiddenRows = ["gain"] }, defaults with { HiddenRows = ["Power"] },
            defaults with { HiddenRows = ["Gain", "Gain"] }, defaults with { AudioProfiles = null! },
            defaults with { AudioProfiles = [null!] }, defaults with { AudioProfiles = [profile with { Gain = 6 }] },
            defaults with { AudioProfiles = [profile, profile with { Name = " NAGRANIE PRÓBNE " }] },
            defaults with { AudioProfiles = Enumerable.Range(0, 33).Select(index => profile with { Name = "Profile " + index }).ToArray() }
        ];
        byte[] beforeInvalidSave = File.ReadAllBytes(store.FilePath);
        for (int index = 0; index < invalidPreferences.Length; index++)
        {
            UiPreferences candidate = invalidPreferences[index];
            Throws<ArgumentException>(candidate.Validate, $"Invalid expanded model {index} fails validation");
            Check(!store.TrySave(candidate, out error) && error is not null
                && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeInvalidSave),
                $"Invalid expanded model {index} cannot overwrite valid preferences");
        }
        Check(!store.TrySave(null!, out error) && error is not null, "Null preferences cannot be saved");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(),
            "Expanded saves and validation failures leave no temporary files");
        UiPreferences maximumProfiles = defaults with
        {
            AudioProfiles = Enumerable.Range(0, 32).Select(index => profile with { Name = index + new string('a', 78) }).ToArray()
        };
        Check(store.TrySave(maximumProfiles, out error) && error is null
            && new UiPreferencesStore(store.BaseDirectory).Load(out error).AudioProfiles.SequenceEqual(maximumProfiles.AudioProfiles) && error is null,
            "Thirty-two profiles with names up to eighty characters survive the file-size limit");
    }

    private static void VerifyExpandedMigration()
    {
        string[] legacyDocuments =
        [
            "{\"SchemaVersion\":1,\"ShowHints\":false}", "{\"SchemaVersion\":1,\"ShowHints\":true}",
            "{\"ShowHints\":false}", "{}", "{\"SchemaVersion\":1}"
        ];
        for (int index = 0; index < legacyDocuments.Length; index++)
        {
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "expanded-preferences", "migration-" + index));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, legacyDocuments[index]);
            byte[] original = File.ReadAllBytes(store.FilePath);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(store.FilePath);
            UiPreferences migrated = store.Load(out string? error);
            bool expectedHints = !legacyDocuments[index].Contains("false", StringComparison.Ordinal);
            Check(error is null && EquivalentExpandedPreferences(migrated, new UiPreferences { ShowHints = expectedHints }),
                $"Legacy migration {index} preserves hints and supplies only safe defaults");
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(original) && File.GetLastWriteTimeUtc(store.FilePath) == originalWriteTime,
                $"Legacy migration {index} makes no implicit disk write");
            Check(store.TrySave(migrated, out error) && error is null, $"Legacy migration {index} can be explicitly saved");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Check(document.RootElement.GetProperty("SchemaVersion").GetInt32() == UiPreferences.CurrentSchemaVersion
                && EquivalentExpandedPreferences(migrated, new UiPreferencesStore(store.BaseDirectory).Load(out error)) && error is null,
                $"Legacy migration {index} persists the current schema only after save");
        }
        var missingStore = new UiPreferencesStore(Path.Combine(ReportDirectory, "expanded-preferences", "missing"));
        Check(EquivalentExpandedPreferences(missingStore.Load(out string? missingError), new UiPreferences())
            && missingError is null && !Directory.Exists(missingStore.BaseDirectory),
            "Loading absent expanded preferences creates neither files nor directories");
    }

    private static void VerifyAudioProfiles()
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile profile = AudioProfile.FromStatus("Próba", status);
        Check(profile.Name == "Próba" && profile.Gain == status.Gain && profile.ChannelMode == status.ChannelMode
            && profile.NoiseEnabled == status.NoiseEnabled && profile.NoiseLevel == status.NoiseLevel
            && profile.Equalizer == status.Equalizer && profile.ReverbEnabled == status.ReverbEnabled && profile.ReverbLevel == status.ReverbLevel,
            "Audio profile captures every supported audio field from confirmed status");
        IReadOnlyList<SettingChange> changes = profile.CreateChanges();
        Check(changes.Select(change => change.Command).SequenceEqual(new[]
        {
            A1Command.SetGain, A1Command.SetChannelMode, A1Command.SetNoise, A1Command.SetEqualizer, A1Command.SetReverb
        }), "Audio profile changes have stable order and contain no mute, power, lights, identification or auto-off commands");
        byte[][] payloads =
        [
            [(byte)profile.Gain], [(byte)profile.ChannelMode], [(byte)(profile.NoiseEnabled ? 1 : 0), (byte)profile.NoiseLevel],
            [(byte)profile.Equalizer], [(byte)(profile.ReverbEnabled ? 1 : 0), (byte)profile.ReverbLevel]
        ];
        for (int index = 0; index < changes.Count; index++)
            Check(changes[index].Payload.SequenceEqual(payloads[index]) && changes[index].IsConfirmed(status),
                $"Audio profile command {index} has supported payload and readback predicate");
        A1Status[] mismatches =
        [
            status with { Gain = 0 }, status with { ChannelMode = 1 }, status with { NoiseEnabled = false },
            status with { Equalizer = 1 }, status with { ReverbEnabled = false }
        ];
        for (int index = 0; index < changes.Count; index++)
            Check(!changes[index].IsConfirmed(mismatches[index]), $"Audio profile command {index} rejects mismatched readback");
        changes[0].Payload[0] = 255;
        Check(profile.CreateChanges()[0].Payload[0] == profile.Gain, "Audio profile creates independent command payloads on every request");
        AudioProfile disabled = profile with { Gain = 0, ChannelMode = 0, NoiseEnabled = false, NoiseLevel = 0, Equalizer = 1, ReverbEnabled = false, ReverbLevel = 0 };
        disabled.Validate();
        Check(disabled.CreateChanges()[2].Payload.SequenceEqual(new byte[] { 0, 0 })
            && disabled.CreateChanges()[4].Payload.SequenceEqual(new byte[] { 0, 0 }), "Disabled audio effects retain explicit supported levels");
        (profile with { Name = new string('a', 80), Gain = 5, ChannelMode = 1, NoiseLevel = 2, Equalizer = 3, ReverbLevel = 2 }).Validate();
        AudioProfile[] invalidProfiles =
        [
            profile with { Name = null! }, profile with { Name = "" }, profile with { Name = "   " },
            profile with { Name = new string('a', 81) }, profile with { Name = "bad\nname" },
            profile with { Gain = -1 }, profile with { Gain = 6 }, profile with { ChannelMode = -1 }, profile with { ChannelMode = 2 },
            profile with { NoiseLevel = -1 }, profile with { NoiseLevel = 3 }, profile with { Equalizer = 0 }, profile with { Equalizer = 4 },
            profile with { ReverbLevel = -1 }, profile with { ReverbLevel = 3 },
            disabled with { NoiseLevel = 3 }, disabled with { ReverbLevel = 3 }
        ];
        for (int index = 0; index < invalidProfiles.Length; index++)
        {
            AudioProfile candidate = invalidProfiles[index];
            Throws<ArgumentException>(candidate.Validate, $"Invalid audio profile {index} is rejected");
            Throws<ArgumentException>(() => candidate.CreateChanges(), $"Invalid audio profile {index} cannot produce device commands");
        }
        Throws<ArgumentException>(() => AudioProfile.FromStatus("Bad", status with { Gain = 6 }), "Profile capture validates supplied status");
        Throws<ArgumentNullException>(() => AudioProfile.FromStatus("Bad", null!), "Profile capture rejects missing status");
    }

    private static void VerifyExpandedInvalidFiles()
    {
        string profileJson = JsonSerializer.Serialize(AudioProfile.FromStatus("Valid", A1Status.Parse(ValidStatus)));
        var documents = new List<string>
        {
            "null", "[]", "{broken", "{\"SchemaVersion\":99}", "{\"SchemaVersion\":-1}", "{\"SchemaVersion\":\"2\"}",
            "{\"SchemaVersion\":null}", "{\"SchemaVersion\":1,\"Theme\":\"Dark\"}",
            "{\"SchemaVersion\":2,\"Unknown\":42}", "{\"SchemaVersion\":2,\"Theme\":\"Future\"}",
            "{\"SchemaVersion\":2,\"SchemaVersion\":2}", "{\"SchemaVersion\":2,\"Theme\":\"Dark\",\"Theme\":\"Light\"}",
            "{\"SchemaVersion\":2,\"TextScale\":99}", "{\"SchemaVersion\":2,\"TextScale\":201}",
            "{\"SchemaVersion\":2,\"TextScale\":125.5}", "{\"SchemaVersion\":2,\"HiddenRows\":[\"Power\"]}",
            "{\"SchemaVersion\":2,\"HiddenRows\":[\"Gain\",\"Gain\"]}", "{\"SchemaVersion\":2,\"HiddenRows\":[null]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[null]}", "{\"SchemaVersion\":2,\"AudioProfiles\":[{}]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[" + profileJson[..^1] + ",\"Unknown\":true}]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[" + profileJson + "," + profileJson + "]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[" + profileJson.Replace("\"Gain\":3", "\"Gain\":6") + "]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[" + profileJson.Replace("\"NoiseEnabled\":true", "\"NoiseEnabled\":1") + "]}",
            "{\"SchemaVersion\":2,\"AudioProfiles\":[" + profileJson.Replace("\"ReverbEnabled\":true,", "") + "]}",
            new string(' ', 65537)
        };
        foreach (string field in new[]
        {
            "Theme", "TextScale", "HiddenRows", "GainControlMode", "ValueDisplayMode", "AudioProfiles",
            "AutoApplySelections", "ShowHints", "ShowOperationMessages", "AnnounceStateChanges", "StartWithWindows",
            "AutoConnectAtStartup", "CloseToTray", "ConfirmExit"
        })
            documents.Add("{\"SchemaVersion\":2,\"" + field + "\":null}");
        foreach (string field in new[] { "GainControlMode", "ValueDisplayMode" })
        foreach (string value in new[] { "0", "99", "\"Unknown\"", "\"0\"", "\"Choice, Buttons\"", "true" })
            documents.Add("{\"SchemaVersion\":2,\"" + field + "\":" + value + "}");
        foreach (string field in new[]
        {
            "AutoApplySelections", "ShowHints", "ShowOperationMessages", "AnnounceStateChanges", "StartWithWindows",
            "AutoConnectAtStartup", "CloseToTray", "ConfirmExit"
        })
            documents.Add("{\"SchemaVersion\":2,\"" + field + "\":\"false\"}");
        for (int index = 0; index < documents.Count; index++)
        {
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "expanded-preferences", "invalid-file-" + index));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, documents[index]);
            byte[] original = File.ReadAllBytes(store.FilePath);
            UiPreferences loaded = store.Load(out string? error);
            Check(error is not null && EquivalentExpandedPreferences(loaded, new UiPreferences()),
                $"Invalid expanded document {index} reports failure and uses safe memory defaults");
            Check(!store.TrySave(new UiPreferences(), out error) && error is not null
                && File.ReadAllBytes(store.FilePath).SequenceEqual(original),
                $"Invalid expanded document {index} is preserved byte-for-byte after rejected save");
        }
    }

    private static bool EquivalentExpandedPreferences(UiPreferences first, UiPreferences second) =>
        first.HiddenRows.SequenceEqual(second.HiddenRows) && first.AudioProfiles.SequenceEqual(second.AudioProfiles)
        && first.Hotkeys.SequenceEqual(second.Hotkeys)
        && first with { HiddenRows = Array.Empty<string>(), AudioProfiles = Array.Empty<AudioProfile>(), Hotkeys = Array.Empty<HotkeyBinding>() }
            == second with { HiddenRows = Array.Empty<string>(), AudioProfiles = Array.Empty<AudioProfile>(), Hotkeys = Array.Empty<HotkeyBinding>() };
}
