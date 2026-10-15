using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LarkControl.Windows;

public static class Localization
{
    private static readonly object Sync = new();
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? catalogues;
    private static string currentLanguage = "pl";

    public static event EventHandler? LanguageChanged;
    public static bool IsInitialized { get { lock (Sync) return catalogues is not null; } }
    public static string CurrentLanguage { get { lock (Sync) return currentLanguage; } }
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(CurrentLanguage == "en" ? "en-GB" : "pl-PL");

    public static void Initialize(string? directory = null)
    {
        string location = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "langs"));
        var loaded = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (string language in new[] { "pl", "en" })
        {
            string path = Path.Combine(location, language + ".lng");
            using var stream = File.OpenRead(path);
            if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException($"Language file is too large: {path}");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            if (reader.CurrentEncoding.CodePage != Encoding.UTF8.CodePage)
                throw new InvalidDataException($"Language file must use UTF-8: {path}");
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Language file must contain a JSON string dictionary: {path}");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(entry.Value.GetString()) || !entries.TryAdd(entry.Name, entry.Value.GetString()!))
                    throw new InvalidDataException($"Invalid or duplicate language entry: {path}, {entry.Name}");
                _ = CompositeFormat.Parse(entries[entry.Name]);
            }
            if (entries.Count == 0) throw new InvalidDataException($"Language file is empty: {path}");
            loaded.Add(language, new ReadOnlyDictionary<string, string>(entries));
        }
        if (!loaded["pl"].Keys.Order(StringComparer.Ordinal).SequenceEqual(loaded["en"].Keys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Polish and English language files must contain the same keys.");
        foreach (string key in loaded["pl"].Keys)
            if (!ArgumentIndexes(loaded["pl"][key]).SetEquals(ArgumentIndexes(loaded["en"][key])))
                throw new InvalidDataException($"Language format arguments do not match: {key}");
        using Stream contractStream = typeof(Localization).Assembly.GetManifestResourceStream("Hollylark.LanguageContract")
            ?? throw new InvalidDataException("The application language contract is missing.");
        using JsonDocument contract = JsonDocument.Parse(contractStream);
        string[] requiredKeys = contract.RootElement.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
        if (!requiredKeys.SequenceEqual(loaded["pl"].Keys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Language files do not match the keys required by this application version.");
        foreach (JsonProperty entry in contract.RootElement.EnumerateObject())
            if (!ArgumentIndexes(entry.Value.GetString()!).SetEquals(ArgumentIndexes(loaded["pl"][entry.Name])))
                throw new InvalidDataException($"Language arguments do not match the application contract: {entry.Name}");
        lock (Sync) catalogues = loaded;
        SetLanguage(CurrentLanguage);
    }

    public static void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (language is not ("pl" or "en")) throw new ArgumentOutOfRangeException(nameof(language), "Supported languages: pl, en.");
        EnsureInitialized();
        bool changed;
        lock (Sync)
        {
            changed = currentLanguage != language;
            currentLanguage = language;
        }
        Application? application = Application.Current;
        if (application is null)
        {
            if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
            return;
        }
        if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished) return;
        void Apply()
        {
            ApplyResources(application.Resources);
            if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
        if (application.Dispatcher.CheckAccess()) Apply();
        else application.Dispatcher.Invoke(Apply);
    }

    public static void ApplyResources(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (KeyValuePair<string, string> entry in GetEntries(CurrentLanguage)) resources[entry.Key] = entry.Value;
    }

    public static string Text(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);
        EnsureInitialized();
        string template;
        CultureInfo culture;
        lock (Sync)
        {
            if (!catalogues![currentLanguage].TryGetValue(key, out template!))
                throw new KeyNotFoundException($"Missing localization key: {key}");
            culture = CultureInfo.GetCultureInfo(currentLanguage == "en" ? "en-GB" : "pl-PL");
        }
        return string.Format(culture, template, arguments);
    }

    internal static IReadOnlyDictionary<string, string> GetEntries(string language)
    {
        EnsureInitialized();
        lock (Sync) return catalogues![language];
    }

    private static HashSet<int> ArgumentIndexes(string template)
    {
        _ = CompositeFormat.Parse(template);
        var indexes = new HashSet<int>();
        for (int position = 0; position < template.Length; position++)
        {
            if (template[position] != '{') continue;
            if (position + 1 < template.Length && template[position + 1] == '{') { position++; continue; }
            int start = ++position;
            while (position < template.Length && char.IsAsciiDigit(template[position])) position++;
            indexes.Add(int.Parse(template.AsSpan(start, position - start), CultureInfo.InvariantCulture));
        }
        return indexes;
    }

    private static void EnsureInitialized()
    {
        lock (Sync) { if (catalogues is not null) return; }
        Initialize();
    }
}
