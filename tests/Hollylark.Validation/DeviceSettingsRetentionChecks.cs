using System.IO;
using System.Text.Json;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static readonly RetainedSettings RetentionDesired = new(5, 1, true, 2, 1, true, 2, false, 1, true);
    private static readonly RetainedSettings RetentionReset = new(0, 0, false, 0, 3, false, 0, true, 0, false);

    private static void VerifyDeviceSettingsRetentionChecks()
    {
        VerifyRetainedSettingsChanges();
        VerifyRetentionStartupAndPersistence();
        VerifyRetentionMembershipAndSnapshots();
        VerifyRetentionExternalChangesAndCompletion();
        VerifyRetentionAbandonmentAndInvalidation();
        VerifyRetentionIndependentReceivers();
        VerifyRetentionObservationResets();
        VerifyRetentionClearAndCapacity();
        VerifyRetentionStrictFiles();
        VerifyRetentionAtomicFailures();
    }

    private static A1Status RetentionStatus(RetainedSettings settings, int[] connected, int slots = 2, bool muted = false) =>
        new(Enumerable.Range(0, slots).Select(index => new TransmitterState(connected.Contains(index),
            connected.Contains(index) ? 80 : null, muted)).ToArray(), settings.NoiseEnabled, settings.NoiseLevel,
            settings.Gain, settings.ChannelMode, settings.AutoOff, settings.LightsEnabled, settings.Equalizer,
            settings.ReverbEnabled, settings.ReverbLevel, settings.IdentificationEnabled);

    private static DeviceSettingsRetentionStore RetentionStore(string scenario) =>
        new(Path.Combine(ReportDirectory, "device-settings-retention", scenario + "-" + Guid.NewGuid().ToString("N")));

    private static void SeedRetention(DeviceSettingsRetentionStore store, params RetainedReceiverSettings[] entries)
    {
        Check(store.TrySave(entries, out string? error) && error is null, "Seed isolated retention fixture: " + store.BaseDirectory);
    }

    private static RetainedSettings ReadRetention(DeviceSettingsRetentionStore store, string path)
    {
        IReadOnlyList<RetainedReceiverSettings> entries = new DeviceSettingsRetentionStore(store.BaseDirectory).Load(out string? error);
        Check(error is null, "Retention remains readable after a policy transition");
        return entries.Single(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.ReceiverPath, path)).Settings;
    }

    private static RetentionRestoreRequest RequireRetentionRequest(RetentionRestoreRequest? request, string scenario)
    {
        Check(request is not null, scenario);
        return request ?? throw new InvalidOperationException(scenario);
    }

    private static void VerifyRetainedSettingsChanges()
    {
        A1Status current = RetentionStatus(RetentionReset, [0, 1], muted: true);
        IReadOnlyList<SettingChange> changes = RetentionDesired.CreateChanges(current);
        Check(changes.Select(change => change.Command).SequenceEqual(new[]
        {
            A1Command.SetGain, A1Command.SetChannelMode, A1Command.SetNoise, A1Command.SetEqualizer,
            A1Command.SetReverb, A1Command.SetLight, A1Command.SetAutoOff, A1Command.SetIdentification
        }), "Retention covers exactly the eight shared setting commands in a deterministic order");
        Check(changes.All(change => change.Command != A1Command.SetMute)
            && changes.All(change => change.IsConfirmed(RetentionStatus(RetentionDesired, [0, 1], muted: true))),
            "Every retained shared value confirms without ever emitting mute or unmute");
        Check(changes.Single(change => change.Command == A1Command.SetLight).Payload.SequenceEqual(new byte[] { 1 }),
            "Retention preserves the inverted lights command flag");
        Check(RetentionDesired.CreateChanges(RetentionStatus(RetentionDesired, [0])).Count == 0,
            "Already matching shared settings require no writes");
        Check(RetainedSettings.FromStatus(RetentionStatus(RetentionDesired, [1], muted: true) with { RadioModeCode = 255 })
            == RetentionDesired, "Retained snapshots exclude mute, connection, battery and raw radio mode");
        Check(RetentionDesired.CreateChanges(current with { IdentificationEnabled = null })
            .All(change => change.Command != A1Command.SetIdentification)
            && (RetentionDesired with { IdentificationEnabled = null }).CreateChanges(current)
                .All(change => change.Command != A1Command.SetIdentification),
            "Identification remains unwritten when either desired or current support is unknown");
        RetainedSettings latent = RetentionReset with { NoiseLevel = 2, ReverbLevel = 1 };
        IReadOnlyList<SettingChange> latentChanges = latent.CreateChanges(current);
        Check(latentChanges.Select(change => change.Command).SequenceEqual(new[] { A1Command.SetNoise, A1Command.SetReverb })
            && latentChanges.All(change => !change.IsConfirmed(current))
            && latentChanges.All(change => change.IsConfirmed(RetentionStatus(latent, [0, 1], muted: true))),
            "Disabled effects retain and strictly confirm their latent levels without unmuting");
        foreach (RetainedSettings invalid in new[]
        {
            RetentionDesired with { Gain = 6 }, RetentionDesired with { ChannelMode = -1 },
            RetentionDesired with { NoiseLevel = 3 }, RetentionDesired with { Equalizer = 0 },
            RetentionDesired with { ReverbLevel = 3 }, RetentionDesired with { AutoOff = 2 }
        })
            Throws<ArgumentException>(() => invalid.CreateChanges(current), "Invalid retained settings cannot create commands");
    }

    private static void VerifyRetentionStartupAndPersistence()
    {
        const string path = "hid://retention/startup";
        DeviceSettingsRetentionStore store = RetentionStore("startup");
        SeedRetention(store, new RetainedReceiverSettings(path, RetentionDesired));
        var activeFirst = new DeviceSettingsRetention(new DeviceSettingsRetentionStore(store.BaseDirectory));
        Check(activeFirst.Observe(path, RetentionStatus(RetentionReset, [0])) is null
            && ReadRetention(store, path) == RetentionReset,
            "First active read after process restart adopts actual values instead of blindly writing saved values");
        SeedRetention(new DeviceSettingsRetentionStore(store.BaseDirectory), new RetainedReceiverSettings(path, RetentionDesired));
        var inactiveFirst = new DeviceSettingsRetention(new DeviceSettingsRetentionStore(store.BaseDirectory));
        byte[] beforeOffline = File.ReadAllBytes(store.FilePath);
        for (int repetition = 0; repetition < 3; repetition++)
            Check(inactiveFirst.Observe(path, RetentionStatus(RetentionReset, [])) is null,
                "Receiver-only polling cannot initiate a restore");
        Check(File.ReadAllBytes(store.FilePath).SequenceEqual(beforeOffline),
            "Zero-active startup reads leave the persisted confirmed baseline byte-identical");
        RetentionRestoreRequest request = RequireRetentionRequest(inactiveFirst.Observe(path, RetentionStatus(RetentionReset, [1])),
            "First inactive then active read can restore saved values across process restart");
        Check(request.DesiredSettings == RetentionDesired && request.ObservedSettings == RetentionReset
            && request.ConnectedIndices.SequenceEqual(new[] { 1 }) && request.ReceiverPath == path,
            "Startup restore captures desired values, expected state and the exact active set");
        Check(inactiveFirst.Complete(request, RetentionStatus(RetentionDesired, [1]))
            && !inactiveFirst.IsCurrent(request) && ReadRetention(store, path) == RetentionDesired,
            "Completion consumes its token and persists the actual confirmed shared state");
        var unknown = new DeviceSettingsRetention(RetentionStore("no-saved-baseline"));
        Check(unknown.Observe(path, RetentionStatus(RetentionReset, [])) is null
            && unknown.Observe(path, RetentionStatus(RetentionDesired, [0, 1])) is null,
            "An inactive startup without a saved baseline still performs no restore");
        Check(new DeviceSettingsRetentionStore().FilePath == Path.Combine(AppContext.BaseDirectory, "data", "retained-settings.json"),
            "The default retained settings path belongs beside the executable, independently of the working directory");
    }

    private static void VerifyRetentionMembershipAndSnapshots()
    {
        const string path = "hid://retention/membership";
        var model = new DeviceSettingsRetention(RetentionStore("membership"));
        Check(model.Observe(path, RetentionStatus(RetentionDesired, [0, 3], 4)) is null,
            "Four-slot startup establishes a read-only baseline with a noncontiguous active set");
        Check(model.Observe(path, RetentionStatus(RetentionDesired, [0], 4)) is null,
            "Losing one active microphone does not create a restore request");
        A1Status rejoined = RetentionStatus(RetentionReset, [0, 2, 3], 4);
        RetentionRestoreRequest request = RequireRetentionRequest(model.Observe(path, rejoined),
            "An observed inactive slot becoming active captures one restore request");
        ((TransmitterState[])rejoined.Transmitters)[2] = new(false, null, false);
        Check(request.ConnectedIndices.SequenceEqual(new[] { 0, 2, 3 }) && request.DesiredSettings == RetentionDesired,
            "Caller mutations cannot alter captured membership or desired settings");
        Throws<NotSupportedException>(() => ((IList<int>)request.ConnectedIndices)[0] = 1,
            "Captured active indices expose no writable collection");
        for (int repetition = 0; repetition < 3; repetition++)
            Check(model.Observe(path, RetentionStatus(RetentionReset, [0, 2, 3], 4)) is null && model.IsCurrent(request),
                "Pending restoration produces neither duplicate requests nor reset-value learning");
        Check(model.Complete(request, RetentionStatus(RetentionDesired, [0, 2, 3], 4)),
            "The captured four-slot request can complete independently of mutable source status");
        Throws<ArgumentException>(() => model.Observe(path, RetentionStatus(RetentionReset, [0], 3)),
            "Incomplete or invented transmitter topology is rejected before changing observations");
    }

    private static void VerifyRetentionExternalChangesAndCompletion()
    {
        const string path = "hid://retention/external";
        DeviceSettingsRetentionStore store = RetentionStore("external");
        var model = new DeviceSettingsRetention(store);
        model.Observe(path, RetentionStatus(RetentionDesired, [0, 1]));
        Check(model.Observe(path, RetentionStatus(RetentionReset, [0, 1])) is null
            && ReadRetention(store, path) == RetentionReset,
            "Continuous active phone or panel changes replace desired values without counter-writes");
        model.Observe(path, RetentionStatus(RetentionDesired, []));
        RetentionRestoreRequest request = RequireRetentionRequest(model.Observe(path, RetentionStatus(RetentionDesired, [0, 1])),
            "Rejoin uses the newest externally confirmed baseline rather than the original application choice");
        Check(request.DesiredSettings == RetentionReset, "External changes remain authoritative across an observed inactive interval");
        RetainedSettings honestPartial = RetentionReset with { Gain = 2, LightsEnabled = false };
        Check(model.Complete(request, RetentionStatus(honestPartial, [0, 1]))
            && ReadRetention(store, path) == honestPartial,
            "Complete adopts honest final readback rather than claiming every desired field was restored");
        Check(model.Observe(path, RetentionStatus(honestPartial, [0, 1])) is null
            && !model.Complete(request, RetentionStatus(RetentionDesired, [0, 1])),
            "A completed request cannot retry or overwrite later actual state through a reused token");
    }

    private static void VerifyRetentionAbandonmentAndInvalidation()
    {
        const string path = "hid://retention/abandon";
        DeviceSettingsRetentionStore store = RetentionStore("abandon");
        var model = new DeviceSettingsRetention(store);
        model.Observe(path, RetentionStatus(RetentionDesired, [0, 1]));
        model.Observe(path, RetentionStatus(RetentionReset, []));
        RetentionRestoreRequest request = RequireRetentionRequest(model.Observe(path, RetentionStatus(RetentionReset, [0, 1])),
            "Prepare an abandonable request");
        Check(model.Abandon(request, RetentionStatus(RetentionReset, [0, 1]))
            && ReadRetention(store, path) == RetentionReset
            && model.Observe(path, RetentionStatus(RetentionReset, [0, 1])) is null,
            "Abandon with final readback adopts the actual state without repeating the restore");
        model.Observe(path, RetentionStatus(RetentionDesired, [0, 1]));
        model.MarkDisconnected(path);
        request = RequireRetentionRequest(model.Observe(path, RetentionStatus(RetentionReset, [0])),
            "Real receiver removal arms a new one-shot restore");
        Check(model.Abandon(request), "An unavailable final readback can abandon a current token");
        model.Observe(path, RetentionStatus(RetentionReset, []));
        Check(model.Observe(path, RetentionStatus(RetentionReset, [0])) is null
            && ReadRetention(store, path) == RetentionReset,
            "Abandon without readback requires a fresh active baseline even after another inactive sample");
        model.Observe(path, RetentionStatus(RetentionDesired, [0]));
        model.MarkDisconnected(path);
        request = RequireRetentionRequest(model.Observe(path, RetentionStatus(RetentionReset, [0])),
            "Prepare a request invalidated by receiver removal");
        model.MarkDisconnected(path);
        Check(!model.IsCurrent(request) && !model.Complete(request, RetentionStatus(RetentionReset, [0])),
            "Physical removal invalidates old request tokens before a replacement session can finish them");
        request = RequireRetentionRequest(model.Observe(path, RetentionStatus(RetentionReset, [0])),
            "A new physical rejoin creates a distinct request");
        Check(model.Observe(path, RetentionStatus(RetentionReset, [1])) is null && !model.IsCurrent(request)
            && !model.Abandon(request, RetentionStatus(RetentionDesired, [0]))
            && ReadRetention(store, path) == RetentionReset,
            "A membership replacement during restore invalidates its token and learns honest current values");
    }

    private static void VerifyRetentionIndependentReceivers()
    {
        const string firstPath = "hid://retention/first";
        const string secondPath = "hid://retention/second";
        DeviceSettingsRetentionStore store = RetentionStore("independent");
        var model = new DeviceSettingsRetention(store);
        model.Observe(firstPath, RetentionStatus(RetentionDesired, [0, 1]));
        model.Observe(secondPath, RetentionStatus(RetentionReset, [0]));
        model.MarkDisconnected(firstPath.ToUpperInvariant());
        RetentionRestoreRequest request = RequireRetentionRequest(model.Observe(firstPath.ToUpperInvariant(), RetentionStatus(RetentionReset, [1])),
            "Receiver paths use the same ordinal case-insensitive identity as USB discovery");
        RetainedSettings secondExternal = RetentionReset with { Gain = 3 };
        model.Observe(secondPath, RetentionStatus(secondExternal, [0]));
        Check(request.DesiredSettings == RetentionDesired && model.IsCurrent(request)
            && ReadRetention(store, firstPath) == RetentionDesired && ReadRetention(store, secondPath) == secondExternal
            && model.RememberedReceiverCount == 2,
            "Independent receiver paths cannot overwrite each other's baseline or pending request");
        var otherModel = new DeviceSettingsRetention(RetentionStore("foreign-token"));
        Check(!otherModel.Complete(request, RetentionStatus(RetentionReset, [1])),
            "A request token from another policy instance is rejected");
    }

    private static void VerifyRetentionObservationResets()
    {
        const string firstPath = "hid://retention/reset-first";
        const string secondPath = "hid://retention/reset-second";
        DeviceSettingsRetentionStore store = RetentionStore("observation-reset");
        var model = new DeviceSettingsRetention(store);
        model.Observe(firstPath, RetentionStatus(RetentionDesired, [0]));
        model.Observe(secondPath, RetentionStatus(RetentionDesired, [0]));
        model.MarkDisconnected(firstPath);
        model.MarkDisconnected(secondPath);
        RetentionRestoreRequest first = RequireRetentionRequest(model.Observe(firstPath, RetentionStatus(RetentionReset, [0])),
            "Prepare the selected receiver's pending request");
        RetentionRestoreRequest second = RequireRetentionRequest(model.Observe(secondPath, RetentionStatus(RetentionReset, [0])),
            "Prepare the other receiver's pending request");
        byte[] beforeReset = File.ReadAllBytes(store.FilePath);
        model.ResetObservation(firstPath.ToUpperInvariant());
        Check(!model.IsCurrent(first) && model.IsCurrent(second) && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeReset),
            "A single observation reset invalidates only that path and preserves disk desired settings");
        model.Observe(firstPath, RetentionStatus(RetentionReset, []));
        Check(model.Observe(firstPath, RetentionStatus(RetentionReset, [0])) is null,
            "Selection or session-error reset requires a new active baseline rather than inferring a power cycle");
        beforeReset = File.ReadAllBytes(store.FilePath);
        model.ResetObservations();
        Check(!model.IsCurrent(second) && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeReset),
            "ResetObservations invalidates all pending requests without rewriting persisted desired values");
        Check(model.Observe(secondPath, RetentionStatus(RetentionReset, [0])) is null,
            "Re-enabling retention first adopts the actual active state");
        model.Observe(firstPath, RetentionStatus(RetentionDesired, [0]));
        model.ResetObservation(firstPath);
        model.MarkDisconnected(firstPath);
        Check(model.Observe(firstPath, RetentionStatus(RetentionReset, [0])) is not null,
            "A subsequent real discovery removal can arm retention after an observation reset");
        model.ResetObservation("hid://not-yet-seen");
        Throws<ArgumentException>(() => model.ResetObservation("\n"), "Observation resets reject invalid receiver identities");
    }

    private static void VerifyRetentionClearAndCapacity()
    {
        DeviceSettingsRetentionStore store = RetentionStore("clear-and-capacity");
        var model = new DeviceSettingsRetention(store);
        for (int index = 0; index < DeviceSettingsRetentionStore.MaximumReceiverPaths; index++)
            model.Observe("hid://retention/capacity/" + index, RetentionStatus(RetentionDesired, [0]));
        model.Observe("HID://RETENTION/CAPACITY/0", RetentionStatus(RetentionDesired, [0]));
        model.Observe("hid://retention/capacity/32", RetentionStatus(RetentionDesired, [0]));
        IReadOnlyList<RetainedReceiverSettings> entries = new DeviceSettingsRetentionStore(store.BaseDirectory).Load(out string? error);
        Check(error is null && entries.Count == 32 && model.RememberedReceiverCount == 32
            && entries.Any(entry => entry.ReceiverPath.EndsWith("/0", StringComparison.Ordinal))
            && !entries.Any(entry => entry.ReceiverPath.EndsWith("/1", StringComparison.Ordinal)),
            "Capacity is limited to 32 paths and evicts the least recently observed path, including mixed-case touches");
        model.MarkDisconnected("hid://retention/capacity/0");
        RetentionRestoreRequest request = RequireRetentionRequest(model.Observe("hid://retention/capacity/0", RetentionStatus(RetentionReset, [0])),
            "Prepare a pending request before clearing retention");
        Check(model.Clear() && model.LastError is null && !model.IsCurrent(request)
            && model.RememberedReceiverCount == 0
            && new DeviceSettingsRetentionStore(store.BaseDirectory).Load(out error).Count == 0 && error is null,
            "Clear removes saved values and observations and invalidates every request token");
        model.Observe("hid://retention/capacity/0", RetentionStatus(RetentionDesired, []));
        Check(model.Observe("hid://retention/capacity/0", RetentionStatus(RetentionReset, [0])) is null,
            "Clearing before re-enable prevents old settings from returning after an inactive sample");
        byte[] beforeInvalidSave = File.ReadAllBytes(store.FilePath);
        Check(!store.TrySave(Enumerable.Range(0, 33).Select(index => new RetainedReceiverSettings("hid://overflow/" + index, RetentionDesired)), out error)
            && error is not null && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeInvalidSave),
            "The store rejects over-capacity snapshots without modifying a valid file");
        var maximumStore = RetentionStore("maximum-paths");
        SeedRetention(maximumStore, Enumerable.Range(0, 32)
            .Select(index => new RetainedReceiverSettings(index.ToString("D2") + new string('\u0105', 4094), RetentionDesired)).ToArray());
        Check(new DeviceSettingsRetentionStore(maximumStore.BaseDirectory).Load(out error).Count == 32 && error is null,
            "Thirty-two maximum-length escaped Unicode paths round-trip within the bounded file size");
    }

    private static void VerifyRetentionStrictFiles()
    {
        const string path = "hid://retention/json";
        string valid = JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[] { new RetainedReceiverSettings(path, RetentionDesired) } });
        string[] invalidDocuments =
        [
            "{", "null", "[]", "{}", valid + "{}", "/*comment*/" + valid,
            valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2", StringComparison.Ordinal),
            valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal),
            valid.Replace("\"Receivers\":", "\"Unknown\":true,\"Receivers\":", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5", "\"Gain\":5,\"Gain\":5", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5", "\"Gain\":6", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5", "\"gain\":5", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5,", "", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5", "\"Gain\":null", StringComparison.Ordinal),
            valid.Replace("\"NoiseEnabled\":true", "\"NoiseEnabled\":1", StringComparison.Ordinal),
            valid.Replace("\"AutoOff\":1", "\"AutoOff\":2", StringComparison.Ordinal),
            valid.Replace("\"NoiseLevel\":2", "\"NoiseLevel\":3", StringComparison.Ordinal),
            valid.Replace("\"Equalizer\":1", "\"Equalizer\":0", StringComparison.Ordinal),
            valid.Replace("\"ReverbLevel\":2", "\"ReverbLevel\":3", StringComparison.Ordinal),
            valid.Replace("\"ChannelMode\":1", "\"ChannelMode\":2", StringComparison.Ordinal),
            valid.Replace("\"IdentificationEnabled\":true", "\"IdentificationEnabled\":\"true\"", StringComparison.Ordinal),
            valid.Replace("\"Gain\":5", "\"UnknownSetting\":0,\"Gain\":5", StringComparison.Ordinal),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = (RetainedReceiverSettings[]?)null }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new RetainedReceiverSettings?[] { null } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[] { new RetainedReceiverSettings(path, null!) } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[] { new RetainedReceiverSettings(null!, RetentionDesired) } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[] { new RetainedReceiverSettings("\n", RetentionDesired) } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[] { new RetainedReceiverSettings(new string('a', 4097), RetentionDesired) } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = new[]
                { new RetainedReceiverSettings(path, RetentionDesired), new RetainedReceiverSettings(path.ToUpperInvariant(), RetentionReset) } }),
            JsonSerializer.Serialize(new { SchemaVersion = 1, Receivers = Enumerable.Range(0, 33)
                .Select(index => new RetainedReceiverSettings(path + index, RetentionDesired)).ToArray() }),
            new string(' ', DeviceSettingsRetentionStore.MaximumFileBytes + 1)
        ];
        for (int index = 0; index < invalidDocuments.Length; index++)
        {
            DeviceSettingsRetentionStore store = RetentionStore("invalid-json-" + index);
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, invalidDocuments[index]);
            byte[] original = File.ReadAllBytes(store.FilePath);
            Check(store.Load(out string? error).Count == 0 && error is not null,
                "Invalid retained JSON fails closed: " + index);
            Check(!store.TrySave([new(path, RetentionReset)], out error) && error is not null
                && original.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Unreadable retained JSON cannot be overwritten: " + index);
            var withoutLoad = new DeviceSettingsRetentionStore(store.BaseDirectory);
            Check(!withoutLoad.TrySave([], out error) && error is not null
                && original.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Saving before an explicit load still protects unreadable files: " + index);
            var model = new DeviceSettingsRetention(new DeviceSettingsRetentionStore(store.BaseDirectory));
            Check(model.LastError is not null, "The retention policy exposes loading errors: " + index);
            model.Observe(path, RetentionStatus(RetentionDesired, [0]));
            Check(model.LastError is not null && !model.Clear() && model.RememberedReceiverCount == 0
                && original.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "Memory reset and failed persistence never destroy unreadable retained settings: " + index);
        }
        DeviceSettingsRetentionStore unknownIdentification = RetentionStore("nullable-identification");
        SeedRetention(unknownIdentification, new RetainedReceiverSettings(path, RetentionDesired with { IdentificationEnabled = null }));
        Check(ReadRetention(unknownIdentification, path).IdentificationEnabled is null,
            "Unknown identification support is a valid nullable persisted value");
    }

    private static void VerifyRetentionAtomicFailures()
    {
        const string path = "hid://retention/atomic";
        DeviceSettingsRetentionStore store = RetentionStore("atomic");
        SeedRetention(store, new RetainedReceiverSettings(path, RetentionDesired));
        byte[] original = File.ReadAllBytes(store.FilePath);
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(!store.TrySave([new(path, RetentionReset)], out string? error) && error is not null
                && original.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                "A failed atomic replacement leaves the previous complete settings file intact");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(),
            "A failed replacement cleans up its exclusively owned temporary file");
        Check(store.TrySave([new(path, RetentionReset)], out string? saveError) && saveError is null
            && ReadRetention(store, path) == RetentionReset,
            "A transient replacement failure can be retried after the lock is released");
        var blocked = new DeviceSettingsRetentionStore(store.BaseDirectory);
        blocked.Load(out _);
        File.WriteAllText(store.FilePath, "{externally-corrupted");
        byte[] changed = File.ReadAllBytes(store.FilePath);
        Check(!blocked.TrySave([new(path, RetentionDesired)], out saveError) && saveError is not null
            && changed.SequenceEqual(File.ReadAllBytes(store.FilePath)),
            "A file changed after loading cannot be overwritten by stale in-memory settings");
        DeviceSettingsRetentionStore denied = RetentionStore("unwritable-data");
        Directory.CreateDirectory(denied.BaseDirectory);
        File.WriteAllText(Path.Combine(denied.BaseDirectory, "data"), "fixture blocking directory creation");
        var model = new DeviceSettingsRetention(denied);
        Check(model.Observe(path, RetentionStatus(RetentionDesired, [0])) is null && model.LastError is not null
            && File.ReadAllText(Path.Combine(denied.BaseDirectory, "data")) == "fixture blocking directory creation",
            "A blocked data directory exposes persistence failure without overwriting data or selecting another location");
    }
}
