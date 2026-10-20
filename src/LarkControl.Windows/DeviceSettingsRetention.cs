using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LarkControl.Core;

namespace LarkControl.Windows;

public sealed record RetainedSettings(
    int Gain,
    int ChannelMode,
    bool NoiseEnabled,
    int NoiseLevel,
    int Equalizer,
    bool ReverbEnabled,
    int ReverbLevel,
    bool LightsEnabled,
    int AutoOff,
    bool? IdentificationEnabled)
{
    public static RetainedSettings FromStatus(A1Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var settings = new RetainedSettings(status.Gain, status.ChannelMode, status.NoiseEnabled,
            status.NoiseLevel, status.Equalizer, status.ReverbEnabled, status.ReverbLevel,
            status.LightsEnabled, status.AutoOff, status.IdentificationEnabled);
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (Gain is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(Gain));
        if (ChannelMode is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ChannelMode));
        if (NoiseLevel is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(NoiseLevel));
        if (Equalizer is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(Equalizer));
        if (ReverbLevel is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(ReverbLevel));
        if (AutoOff is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(AutoOff));
    }

    public IReadOnlyList<SettingChange> CreateChanges(A1Status current)
    {
        Validate();
        RetainedSettings actual = FromStatus(current);
        var changes = new List<SettingChange>();
        if (Gain != actual.Gain) changes.Add(SettingChange.Gain(Gain));
        if (ChannelMode != actual.ChannelMode) changes.Add(SettingChange.ChannelMode(ChannelMode));
        if (NoiseEnabled != actual.NoiseEnabled || NoiseLevel != actual.NoiseLevel)
            changes.Add(SettingChange.Noise(NoiseEnabled, NoiseLevel) with
            {
                IsConfirmed = status => status.NoiseEnabled == NoiseEnabled && status.NoiseLevel == NoiseLevel
            });
        if (Equalizer != actual.Equalizer) changes.Add(SettingChange.Equalizer(Equalizer));
        if (ReverbEnabled != actual.ReverbEnabled || ReverbLevel != actual.ReverbLevel)
            changes.Add(SettingChange.Reverb(ReverbEnabled, ReverbLevel) with
            {
                IsConfirmed = status => status.ReverbEnabled == ReverbEnabled && status.ReverbLevel == ReverbLevel
            });
        if (LightsEnabled != actual.LightsEnabled) changes.Add(SettingChange.Lights(LightsEnabled));
        if (AutoOff != actual.AutoOff) changes.Add(SettingChange.AutoOff(AutoOff));
        if (IdentificationEnabled is bool identification && actual.IdentificationEnabled.HasValue
            && IdentificationEnabled != actual.IdentificationEnabled)
            changes.Add(SettingChange.Identification(identification));
        return changes.AsReadOnly();
    }
}

public sealed class RetentionRestoreRequest
{
    public Guid Token { get; } = Guid.NewGuid();
    public string ReceiverPath { get; }
    public RetainedSettings DesiredSettings { get; }
    public RetainedSettings ObservedSettings { get; }
    public IReadOnlyList<int> ConnectedIndices { get; }

    internal RetentionRestoreRequest(string receiverPath, RetainedSettings desiredSettings,
        RetainedSettings observedSettings, int[] connectedIndices)
    {
        ReceiverPath = receiverPath;
        DesiredSettings = desiredSettings;
        ObservedSettings = observedSettings;
        ConnectedIndices = Array.AsReadOnly((int[])connectedIndices.Clone());
    }
}

public sealed class DeviceSettingsRetention
{
    private readonly DeviceSettingsRetentionStore store;
    private readonly Dictionary<string, ReceiverState> receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> recency = new();

    private sealed class ReceiverState
    {
        internal RetainedSettings? Desired { get; set; }
        internal int[]? ConnectedIndices { get; set; }
        internal RetentionRestoreRequest? Pending { get; set; }
        internal bool NeedsBaseline { get; set; }
    }

    public string? LastError { get; private set; }
    public int RememberedReceiverCount => receivers.Values.Count(receiver => receiver.Desired is not null);

    public DeviceSettingsRetention(DeviceSettingsRetentionStore? store = null)
    {
        this.store = store ?? new DeviceSettingsRetentionStore();
        IReadOnlyList<RetainedReceiverSettings> saved = this.store.Load(out string? error);
        foreach (RetainedReceiverSettings receiver in saved)
        {
            receivers.Add(receiver.ReceiverPath, new ReceiverState { Desired = receiver.Settings });
            recency.AddLast(receiver.ReceiverPath);
        }
        LastError = error;
    }

    public RetentionRestoreRequest? Observe(string receiverPath, A1Status status)
    {
        DeviceSettingsRetentionStore.ValidateReceiverPath(receiverPath);
        RetainedSettings actual = RetainedSettings.FromStatus(status);
        int[] connected = ReadConnectedIndices(status);
        ReceiverState receiver = GetOrAdd(receiverPath);
        int[]? previous = receiver.ConnectedIndices;
        receiver.ConnectedIndices = connected;
        if (receiver.Pending is { } pending)
        {
            if (!pending.ConnectedIndices.SequenceEqual(connected))
            {
                receiver.Pending = null;
                receiver.NeedsBaseline = connected.Length == 0;
                if (connected.Length > 0) Adopt(receiver, actual);
            }
            return null;
        }
        if (connected.Length == 0) return null;
        if (receiver.NeedsBaseline || previous is null || receiver.Desired is null)
        {
            receiver.NeedsBaseline = false;
            Adopt(receiver, actual);
            return null;
        }
        if (connected.Except(previous).Any() && receiver.Desired.CreateChanges(status).Count > 0)
        {
            receiver.Pending = new RetentionRestoreRequest(receiverPath, receiver.Desired, actual, connected);
            return receiver.Pending;
        }
        Adopt(receiver, actual);
        return null;
    }

    public bool IsCurrent(RetentionRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return receivers.TryGetValue(request.ReceiverPath, out ReceiverState? receiver)
            && ReferenceEquals(receiver.Pending, request);
    }

    public bool Complete(RetentionRestoreRequest request, A1Status finalStatus)
    {
        ArgumentNullException.ThrowIfNull(finalStatus);
        return Finish(request, finalStatus);
    }

    public bool Abandon(RetentionRestoreRequest request, A1Status? finalStatus = null) => Finish(request, finalStatus);

    private bool Finish(RetentionRestoreRequest request, A1Status? finalStatus)
    {
        if (!IsCurrent(request)) return false;
        RetainedSettings? actual = finalStatus is null ? null : RetainedSettings.FromStatus(finalStatus);
        int[]? connected = finalStatus is null ? null : ReadConnectedIndices(finalStatus);
        ReceiverState receiver = receivers[request.ReceiverPath];
        receiver.Pending = null;
        receiver.ConnectedIndices = connected;
        receiver.NeedsBaseline = connected is null || connected.Length == 0;
        if (connected is { Length: > 0 }) Adopt(receiver, actual!);
        return true;
    }

    public void MarkDisconnected(string receiverPath)
    {
        DeviceSettingsRetentionStore.ValidateReceiverPath(receiverPath);
        if (!receivers.TryGetValue(receiverPath, out ReceiverState? receiver)) return;
        receiver.Pending = null;
        receiver.ConnectedIndices = [];
        receiver.NeedsBaseline = false;
    }

    public void ResetObservations()
    {
        foreach (ReceiverState receiver in receivers.Values) ResetObservation(receiver);
    }

    public void ResetObservation(string receiverPath)
    {
        DeviceSettingsRetentionStore.ValidateReceiverPath(receiverPath);
        if (receivers.TryGetValue(receiverPath, out ReceiverState? receiver)) ResetObservation(receiver);
    }

    private static void ResetObservation(ReceiverState receiver)
    {
        receiver.Pending = null;
        receiver.ConnectedIndices = null;
        receiver.NeedsBaseline = true;
    }

    public bool Clear()
    {
        receivers.Clear();
        recency.Clear();
        return Save();
    }

    private ReceiverState GetOrAdd(string receiverPath)
    {
        if (receivers.TryGetValue(receiverPath, out ReceiverState? receiver))
        {
            string storedPath = recency.First(path => StringComparer.OrdinalIgnoreCase.Equals(path, receiverPath));
            recency.Remove(storedPath);
            recency.AddLast(storedPath);
            return receiver;
        }
        bool evicted = receivers.Count == DeviceSettingsRetentionStore.MaximumReceiverPaths;
        if (evicted)
        {
            receivers.Remove(recency.First!.Value);
            recency.RemoveFirst();
        }
        receiver = new ReceiverState();
        receivers.Add(receiverPath, receiver);
        recency.AddLast(receiverPath);
        if (evicted) Save();
        return receiver;
    }

    private void Adopt(ReceiverState receiver, RetainedSettings actual)
    {
        if (receiver.Desired == actual) return;
        receiver.Desired = actual;
        Save();
    }

    private bool Save()
    {
        bool saved = store.TrySave(recency.Where(path => receivers[path].Desired is not null)
            .Select(path => new RetainedReceiverSettings(path, receivers[path].Desired!)), out string? error);
        LastError = error;
        return saved;
    }

    private static int[] ReadConnectedIndices(A1Status status)
    {
        if (status.Transmitters is null || status.Transmitters.Count is not (2 or 4)
            || status.Transmitters.Any(transmitter => transmitter is null))
            throw new ArgumentException("A complete two- or four-transmitter status is required.", nameof(status));
        return Enumerable.Range(0, status.Transmitters.Count)
            .Where(index => status.Transmitters[index].Connected).ToArray();
    }
}

public sealed record RetainedReceiverSettings(string ReceiverPath, RetainedSettings Settings);

public sealed class DeviceSettingsRetentionStore
{
    public const int MaximumReceiverPaths = 32;
    public const int MaximumFileBytes = 1024 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };
    private bool loaded;
    private bool unreadable;
    private byte[]? expectedHash;

    private sealed record RetentionDocument(int SchemaVersion, RetainedReceiverSettings[] Receivers);

    public string BaseDirectory { get; }
    public string FilePath => Path.Combine(BaseDirectory, "data", "retained-settings.json");

    public DeviceSettingsRetentionStore(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    public IReadOnlyList<RetainedReceiverSettings> Load(out string? error)
    {
        error = null;
        loaded = true;
        unreadable = false;
        expectedHash = null;
        try
        {
            byte[]? contents = ReadContents();
            if (contents is null) return Array.Empty<RetainedReceiverSettings>();
            using JsonDocument document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 8 });
            ValidatePropertyNames(document.RootElement);
            RetentionDocument data = document.RootElement.Deserialize<RetentionDocument>(Options)
                ?? throw new JsonException("The retained settings document must be an object.");
            if (data.SchemaVersion != SchemaVersion)
                throw new JsonException("Unsupported retained settings schema.");
            ValidateReceivers(data.Receivers);
            expectedHash = SHA256.HashData(contents);
            return Array.AsReadOnly(data.Receivers);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            unreadable = true;
            error = "Cannot read retained settings: " + exception.Message;
            return Array.Empty<RetainedReceiverSettings>();
        }
    }

    public bool TrySave(IEnumerable<RetainedReceiverSettings> receivers, out string? error)
    {
        error = null;
        RetainedReceiverSettings[] snapshot;
        byte[] contents;
        try
        {
            ArgumentNullException.ThrowIfNull(receivers);
            snapshot = receivers.Take(MaximumReceiverPaths + 1).ToArray();
            ValidateReceivers(snapshot);
            contents = JsonSerializer.SerializeToUtf8Bytes(new RetentionDocument(SchemaVersion, snapshot), Options);
            if (contents.Length > MaximumFileBytes)
                throw new JsonException("The retained settings file exceeds the supported size.");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            error = "Invalid retained settings: " + exception.Message;
            return false;
        }
        if (!loaded) Load(out error);
        if (unreadable)
        {
            error ??= "The unreadable retained settings file will not be overwritten.";
            return false;
        }
        string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool saved = false;
        try
        {
            EnsureUnchanged();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(true);
            }
            EnsureUnchanged();
            File.Move(temporaryPath, FilePath, true);
            expectedHash = SHA256.HashData(contents);
            saved = true;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            error = "Cannot save retained settings: " + exception.Message;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (IsFileError(exception))
            {
                error = (error is null ? string.Empty : error + " ")
                    + "Cannot remove the temporary retained settings file: " + exception.Message;
            }
        }
        return saved;
    }

    internal static void ValidateReceiverPath(string receiverPath)
    {
        if (string.IsNullOrWhiteSpace(receiverPath) || receiverPath.Length > 4096 || receiverPath.Any(char.IsControl))
            throw new ArgumentException("A receiver path must contain 1 to 4096 characters without control characters.", nameof(receiverPath));
    }

    private static void ValidateReceivers(RetainedReceiverSettings[] receivers)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        if (receivers.Length > MaximumReceiverPaths)
            throw new ArgumentException("At most 32 receiver paths can be retained.", nameof(receivers));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RetainedReceiverSettings receiver in receivers)
        {
            ArgumentNullException.ThrowIfNull(receiver);
            ValidateReceiverPath(receiver.ReceiverPath);
            ArgumentNullException.ThrowIfNull(receiver.Settings);
            receiver.Settings.Validate();
            if (!paths.Add(receiver.ReceiverPath))
                throw new ArgumentException("Retained receiver paths must be unique.", nameof(receivers));
        }
    }

    private byte[]? ReadContents()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes)
                throw new JsonException("The retained settings file exceeds the supported size.");
            byte[] contents = new byte[checked((int)stream.Length)];
            stream.ReadExactly(contents);
            return contents;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void EnsureUnchanged()
    {
        byte[]? contents = ReadContents();
        byte[]? actualHash = contents is null ? null : SHA256.HashData(contents);
        if (expectedHash is null ? actualHash is not null : actualHash is null || !expectedHash.SequenceEqual(actualHash))
        {
            unreadable = true;
            throw new IOException("The retained settings file changed externally; reload it before saving.");
        }
    }

    private static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException;

    private static void ValidatePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate retained settings field.");
                ValidatePropertyNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) ValidatePropertyNames(item);
    }
}
