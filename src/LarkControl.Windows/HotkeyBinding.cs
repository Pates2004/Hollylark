using System.Windows.Input;

namespace LarkControl.Windows;

public enum HotkeyAction
{
    ShowWindow, ReadStatus, ToggleSelectedMute, ToggleAllMute, IncreaseGain, DecreaseGain,
    CycleNoise, CycleEqualizer, CycleReverb, CycleLights, CycleAutoOff, CycleIdentification, CycleChannelMode
}

public enum HotkeyErrorCode
{
    None, MissingBindings, InvalidBinding, InvalidAction, MissingAction, DuplicateAction,
    InvalidGesture, ModifierRequired, ReservedGesture, DuplicateGesture,
    RegistrationFailed, UnregistrationFailed, IdentifierLimitReached, NestedSuspension
}

public sealed record HotkeyError(HotkeyErrorCode Code, HotkeyAction? Action = null,
    string? Gesture = null, int? Win32Error = null, HotkeyAction? ConflictingAction = null);

public sealed record HotkeyBinding(HotkeyAction Action, string Gesture)
{
    private static readonly Dictionary<string, Key> KeyNames = Enum.GetNames<Key>()
        .ToDictionary(name => name, name => Enum.Parse<Key>(name), StringComparer.OrdinalIgnoreCase);

    public static HotkeyBinding[] CreateDefaults() =>
    [
        new(HotkeyAction.ShowWindow, "Ctrl+Alt+Shift+F1"),
        new(HotkeyAction.ReadStatus, "Ctrl+Alt+Shift+F5"),
        new(HotkeyAction.ToggleSelectedMute, "Ctrl+Alt+Shift+F2"),
        new(HotkeyAction.ToggleAllMute, "Ctrl+Alt+Shift+F3"),
        new(HotkeyAction.IncreaseGain, "Ctrl+Alt+Shift+Up"),
        new(HotkeyAction.DecreaseGain, "Ctrl+Alt+Shift+Down"),
        new(HotkeyAction.CycleNoise, "Ctrl+Alt+Shift+F6"),
        new(HotkeyAction.CycleEqualizer, "Ctrl+Alt+Shift+F7"),
        new(HotkeyAction.CycleReverb, "Ctrl+Alt+Shift+F8"),
        new(HotkeyAction.CycleLights, "Ctrl+Alt+Shift+F9"),
        new(HotkeyAction.CycleAutoOff, ""),
        new(HotkeyAction.CycleIdentification, ""),
        new(HotkeyAction.CycleChannelMode, "Ctrl+Alt+Shift+F10")
    ];

    public static HotkeyBinding[] Copy(IEnumerable<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return bindings.Select(binding => binding is null
            ? throw new ArgumentException("A hotkey binding is missing.", nameof(bindings))
            : binding with { }).ToArray();
    }

    public static void ValidateBindings(HotkeyBinding[]? bindings)
    {
        if (Validate(bindings) is HotkeyError error)
            throw new ArgumentException($"Invalid hotkey bindings: {error.Code} ({error.Action}).", nameof(bindings));
    }

    public static HotkeyError? Validate(IEnumerable<HotkeyBinding>? bindings)
    {
        TryNormalize(bindings, out _, out HotkeyError? error);
        return error;
    }

    public static bool TryNormalize(IEnumerable<HotkeyBinding>? bindings,
        out HotkeyBinding[] normalized, out HotkeyError? error)
    {
        normalized = [];
        error = null;
        if (bindings is null)
        {
            error = new(HotkeyErrorCode.MissingBindings);
            return false;
        }
        var result = new List<HotkeyBinding>();
        var actions = new HashSet<HotkeyAction>();
        var gestures = new Dictionary<string, HotkeyAction>(StringComparer.Ordinal);
        foreach (HotkeyBinding? binding in bindings)
        {
            if (binding is null)
                error = new(HotkeyErrorCode.InvalidBinding);
            else if (!Enum.IsDefined(binding.Action))
                error = new(HotkeyErrorCode.InvalidAction, binding.Action, binding.Gesture);
            else if (!actions.Add(binding.Action))
                error = new(HotkeyErrorCode.DuplicateAction, binding.Action, binding.Gesture);
            else if (!TryNormalizeGesture(binding.Gesture, out string canonical, out HotkeyErrorCode code))
                error = new(code, binding.Action, binding.Gesture);
            else if (canonical.Length > 0 && gestures.TryGetValue(canonical, out HotkeyAction other))
                error = new(HotkeyErrorCode.DuplicateGesture, binding.Action, canonical, ConflictingAction: other);
            else
            {
                if (canonical.Length > 0) gestures.Add(canonical, binding.Action);
                result.Add(new(binding.Action, canonical));
            }
            if (error is not null) return false;
        }
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            if (actions.Contains(action)) continue;
            error = new(HotkeyErrorCode.MissingAction, action);
            return false;
        }
        normalized = result.ToArray();
        return true;
    }

    public static bool TryNormalizeGesture(string? text, out string canonical, out HotkeyErrorCode error)
    {
        bool success = TryParseGesture(text, out ParsedHotkey gesture, out error);
        canonical = success ? gesture.Text : "";
        return success;
    }

    public static bool TryFormatGesture(KeyEventArgs args, out string canonical, out HotkeyErrorCode error)
    {
        ArgumentNullException.ThrowIfNull(args);
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        return TryFormatGesture(args.KeyboardDevice.Modifiers, key, out canonical, out error);
    }

    public static bool TryFormatGesture(ModifierKeys modifiers, Key key,
        out string canonical, out HotkeyErrorCode error)
    {
        bool success = TryCreateGesture(modifiers, key, out ParsedHotkey gesture, out error);
        canonical = success ? gesture.Text : "";
        return success;
    }

    internal static bool TryParseGesture(string? text, out ParsedHotkey gesture, out HotkeyErrorCode error)
    {
        gesture = default;
        error = HotkeyErrorCode.InvalidGesture;
        if (text is null || text.Length > 128 || text.Any(char.IsControl)) return false;
        if (string.IsNullOrWhiteSpace(text))
        {
            gesture = new(0, 0, "");
            error = HotkeyErrorCode.None;
            return true;
        }
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            ModifierKeys modifier = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "ALT" => ModifierKeys.Alt,
                "SHIFT" => ModifierKeys.Shift,
                "WIN" or "WINDOWS" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
            if (modifier == ModifierKeys.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        string name = parts[^1].ToUpperInvariant() switch
        {
            "ESC" => "Escape",
            "BACKSPACE" => "Back",
            "PGUP" => "PageUp",
            "PGDN" => "PageDown",
            "DEL" => "Delete",
            "INS" => "Insert",
            "SCROLLLOCK" => "Scroll",
            _ => parts[^1]
        };
        if (name.Length == 1 && name[0] is >= '0' and <= '9') name = "D" + name;
        if (!KeyNames.TryGetValue(name, out Key key)) return false;
        return TryCreateGesture(modifiers, key, out gesture, out error);
    }

    private static bool TryCreateGesture(ModifierKeys modifiers, Key key,
        out ParsedHotkey gesture, out HotkeyErrorCode error)
    {
        gesture = default;
        error = HotkeyErrorCode.InvalidGesture;
        const ModifierKeys supported = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0 || key is Key.LWin or Key.RWin or Key.F12)
        {
            error = HotkeyErrorCode.ReservedGesture;
            return false;
        }
        if ((modifiers & ~supported) != 0 || !Enum.IsDefined(key)
            || key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed
                or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            return false;
        if (modifiers == ModifierKeys.None)
        {
            error = HotkeyErrorCode.ModifierRequired;
            return false;
        }
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        if (control && alt && key == Key.Delete
            || alt && key is Key.Tab or Key.Escape or Key.F4
            || control && key == Key.Escape)
        {
            error = HotkeyErrorCode.ReservedGesture;
            return false;
        }
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is 0 or > 0xfe) return false;
        string keyName = KeyInterop.KeyFromVirtualKey((int)virtualKey) switch
        {
            Key.Return => "Enter",
            Key.Capital => "CapsLock",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Snapshot => "PrintScreen",
            Key.Scroll => "ScrollLock",
            Key.Oem1 => "OemSemicolon",
            Key.Oem2 => "OemQuestion",
            Key.Oem3 => "OemTilde",
            Key.Oem4 => "OemOpenBrackets",
            Key.Oem5 => "OemPipe",
            Key.Oem6 => "OemCloseBrackets",
            Key.Oem7 => "OemQuotes",
            Key.Oem102 => "OemBackslash",
            var canonicalKey => canonicalKey.ToString()
        };
        uint nativeModifiers = (control ? 2u : 0u) | (alt ? 1u : 0u)
            | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0u);
        string canonical = (control ? "Ctrl+" : "") + (alt ? "Alt+" : "")
            + ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + keyName;
        gesture = new(nativeModifiers, virtualKey, canonical);
        error = HotkeyErrorCode.None;
        return true;
    }
}

internal readonly record struct ParsedHotkey(uint Modifiers, uint VirtualKey, string Text);
