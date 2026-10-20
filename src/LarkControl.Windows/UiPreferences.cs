using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;

namespace LarkControl.Windows;

public enum GainControlMode { Choice, Buttons, Slider }

public enum ValueDisplayMode { ReadOnlyFields, StaticText, None }

public sealed record UiPreferences
{
    public const int CurrentSchemaVersion = 4;
    public const int MaximumProfiles = 32;
    public static IReadOnlyList<string> SupportedRowIds { get; } = Array.AsReadOnly<string>(
        ["Microphones", "Gain", "Noise", "Equalizer", "Reverb", "ChannelMode", "Lights", "AutoOff", "Identification", "TX1", "TX2", "TX3", "TX4"]);

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool FirstRunCompleted { get; init; }
    public string Language { get; init; } = "pl";
    public string Theme { get; init; } = "Light";
    public int TextScale { get; init; } = 100;
    public string[] HiddenRows { get; init; } = [];
    public GainControlMode GainControlMode { get; init; } = GainControlMode.Choice;
    public ValueDisplayMode ValueDisplayMode { get; init; } = ValueDisplayMode.StaticText;
    public bool AutoApplySelections { get; init; }
    public bool ShowHints { get; init; } = true;
    public bool ShowOperationMessages { get; init; } = true;
    public bool AnnounceStateChanges { get; init; } = true;
    public bool PreserveVolatileSettings { get; init; } = true;
    public bool GlobalHotkeysEnabled { get; init; }
    public HotkeyBinding[] Hotkeys { get; init; } = HotkeyBinding.CreateDefaults();
    public bool StartWithWindows { get; init; }
    public bool AutoConnectAtStartup { get; init; }
    public string? PreferredReceiverPath { get; init; }
    public bool CloseToTray { get; init; }
    public bool ConfirmExit { get; init; } = true;
    public AudioProfile[] AudioProfiles { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported preferences schema.", nameof(SchemaVersion));
        if (Language is not ("pl" or "en"))
            throw new ArgumentException("Unsupported language.", nameof(Language));
        if (Theme is not ("Light" or "Dark"))
            throw new ArgumentException("Unsupported theme.", nameof(Theme));
        if (TextScale is < 100 or > 200)
            throw new ArgumentOutOfRangeException(nameof(TextScale));
        if (!Enum.IsDefined(GainControlMode) || !Enum.IsDefined(ValueDisplayMode))
            throw new ArgumentException("Unsupported control or value display mode.");
        if (PreferredReceiverPath is not null
            && (string.IsNullOrWhiteSpace(PreferredReceiverPath) || PreferredReceiverPath.Length > 4096
                || PreferredReceiverPath.Any(char.IsControl)))
            throw new ArgumentException("The preferred receiver path must contain at most 4096 characters and no control characters.", nameof(PreferredReceiverPath));
        if (HiddenRows is null || HiddenRows.Any(row => !SupportedRowIds.Contains(row, StringComparer.Ordinal))
            || HiddenRows.Distinct(StringComparer.Ordinal).Count() != HiddenRows.Length)
            throw new ArgumentException("Hidden rows must contain unique supported identifiers.", nameof(HiddenRows));
        if (AudioProfiles is null || AudioProfiles.Length > MaximumProfiles)
            throw new ArgumentException("Unsupported audio profile collection.", nameof(AudioProfiles));
        HotkeyBinding.ValidateBindings(Hotkeys);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioProfile? profile in AudioProfiles)
        {
            if (profile is null)
                throw new ArgumentException("An audio profile is missing.", nameof(AudioProfiles));
            profile.Validate();
            if (!names.Add(profile.Name.Trim()))
                throw new ArgumentException("Audio profile names must be unique.", nameof(AudioProfiles));
        }
    }
}

public sealed class UiPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new PreferenceEnumConverter<GainControlMode>(), new PreferenceEnumConverter<ValueDisplayMode>(),
            new PreferenceEnumConverter<HotkeyAction>() }
    };
    private bool unreadable;

    public string BaseDirectory { get; }
    public string FilePath => Path.Combine(BaseDirectory, "data", "settings.json");
    public string ReportsDirectory => Path.Combine(BaseDirectory, "reports");
    public bool ShouldOfferFirstRun { get; private set; }

    public UiPreferencesStore(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    public UiPreferences Load(out string? error)
    {
        error = null;
        unreadable = false;
        ShouldOfferFirstRun = false;
        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            if (stream.Length > 65536)
                throw new JsonException("The preferences file exceeds the supported size.");
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The preferences document must be an object.");
            ValidateJson(root, preferencesRoot: true);
            int version = 1;
            if (root.TryGetProperty(nameof(UiPreferences.SchemaVersion), out JsonElement schema)
                && (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out version)
                    || version is not (1 or 2 or 3 or UiPreferences.CurrentSchemaVersion)))
                throw new JsonException("Unsupported preferences version.");
            if (version == 1 && root.EnumerateObject().Any(property =>
                property.Name is not (nameof(UiPreferences.SchemaVersion) or nameof(UiPreferences.ShowHints))))
                throw new JsonException("Unknown legacy preference.");
            UiPreferences preferences = root.Deserialize<UiPreferences>(Options)
                ?? throw new JsonException("The preferences document is empty.");
            if (version < UiPreferences.CurrentSchemaVersion)
                preferences = preferences with { SchemaVersion = UiPreferences.CurrentSchemaVersion };
            if (preferences.Theme == "System")
                preferences = preferences with { Theme = "Light" };
            preferences.Validate();
            ShouldOfferFirstRun = !preferences.FirstRunCompleted;
            return preferences;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ShouldOfferFirstRun = true;
            return new UiPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException)
        {
            unreadable = true;
            error = Localization.Text("Preferences.ReadError", FilePath);
            return new UiPreferences();
        }
    }

    public bool TrySave(UiPreferences preferences, out string? error)
    {
        error = null;
        if (unreadable)
        {
            error = Localization.Text("Preferences.Unreadable", FilePath);
            return false;
        }
        try
        {
            ArgumentNullException.ThrowIfNull(preferences);
            preferences.Validate();
        }
        catch (ArgumentException)
        {
            error = Localization.Text("Preferences.Invalid");
            return false;
        }
        string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences, Options);
                stream.Flush(true);
            }
            File.Move(temporaryPath, FilePath, true);
            ShouldOfferFirstRun = !preferences.FirstRunCompleted;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            error = Localization.Text("Preferences.WriteError", FilePath);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                error = (error is null ? Localization.Text("Preferences.Saved") + " " : error + " ") +
                    Localization.Text("Preferences.TemporaryFileError", temporaryPath);
            }
        }
    }
    private static void ValidateJson(JsonElement element, bool preferencesRoot = false)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new JsonException("Preference values cannot be null.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("Duplicate preference field.");
                if (!preferencesRoot || property.Name != nameof(UiPreferences.PreferredReceiverPath)
                    || property.Value.ValueKind != JsonValueKind.Null)
                    ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) ValidateJson(item);
        }
    }

    private sealed class PreferenceEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !Enum.TryParse(reader.GetString(), out TEnum value)
                || !Enum.IsDefined(value) || Enum.GetName(value) != reader.GetString())
                throw new JsonException("Unsupported preference enum name.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Unsupported preference enum value."));
    }
}

public static class UsageHints
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(UsageHints),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, OnChanged));
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(UsageHints), new PropertyMetadata(string.Empty, OnChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == TextProperty || !string.IsNullOrEmpty(GetText(element)))
            AutomationProperties.SetHelpText(element, GetEnabled(element) ? GetText(element) : string.Empty);
    }
}
