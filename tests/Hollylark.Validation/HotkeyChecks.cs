using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Interop;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyHotkeyChecks()
    {
        VerifyHotkeyDefaultsAndCopies();
        VerifyHotkeyParsing();
        VerifyHotkeyBindingValidation();
        VerifyHotkeyRegistration();
        VerifyHotkeyEnableConflict();
        VerifyHotkeyEditRollback();
        VerifyHotkeyUnregisterRollback();
        VerifyHotkeyPartialRestoration();
        VerifyHotkeySuspension();
        VerifyHotkeyConfigureAndResume();
        VerifyHotkeySuspendFailure();
        VerifyHotkeyDispatch();
        VerifyHotkeyDisposal();
        VerifyHotkeySourceOwnership();
    }

    private static void VerifyHotkeyDefaultsAndCopies()
    {
        HotkeyBinding[] defaults = HotkeyBinding.CreateDefaults();
        HotkeyBinding.ValidateBindings(defaults);
        Check(defaults.Length == Enum.GetValues<HotkeyAction>().Length
            && defaults.Select(binding => binding.Action).Distinct().Count() == defaults.Length,
            "Hotkey defaults include each supported action exactly once");
        Check(defaults.Single(binding => binding.Action == HotkeyAction.ShowWindow).Gesture == "Ctrl+Alt+Shift+F1"
            && defaults.Single(binding => binding.Action == HotkeyAction.ReadStatus).Gesture == "Ctrl+Alt+Shift+F5",
            "Hotkey show and read defaults use F1 and F5 respectively");
        Check(defaults.Where(binding => binding.Gesture.Length > 0).All(binding =>
        {
            string primary = binding.Gesture.Split('+')[^1];
            return primary.Length != 1 || !char.IsLetter(primary[0]);
        }),
            "Default global shortcuts never consume letter keys used by AltGr text entry");
        Check(defaults.Count(binding => binding.Gesture.Length > 0) == 11
            && defaults.Single(binding => binding.Action == HotkeyAction.CycleAutoOff).Gesture.Length == 0
            && defaults.Single(binding => binding.Action == HotkeyAction.CycleIdentification).Gesture.Length == 0,
            "Auto-off and identification hotkeys remain unassigned by default");
        HotkeyBinding[] copy = HotkeyBinding.Copy(defaults);
        Check(copy.SequenceEqual(defaults) && !ReferenceEquals(copy, defaults)
            && !ReferenceEquals(copy[0], defaults[0]), "Hotkey copies own their array and immutable records");
        copy[0] = copy[0] with { Gesture = "Ctrl+F8" };
        Check(defaults[0].Gesture == "Ctrl+Alt+Shift+F1"
            && HotkeyBinding.CreateDefaults().SequenceEqual(defaults), "Editing a hotkey copy preserves independent defaults");
        var options = new JsonSerializerOptions
        {
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter<HotkeyAction>() }
        };
        string json = JsonSerializer.Serialize(defaults, options);
        HotkeyBinding[] restored = JsonSerializer.Deserialize<HotkeyBinding[]>(json, options)!;
        Check(restored.SequenceEqual(defaults), "Positional hotkey records round-trip through JSON with action names");
        Throws<ArgumentNullException>(() => HotkeyBinding.Copy(null!), "Hotkey copying rejects a missing collection");
        Throws<ArgumentException>(() => HotkeyBinding.Copy([null!]), "Hotkey copying rejects missing records");
    }

    private static void VerifyHotkeyParsing()
    {
        (string Text, string Canonical)[] examples =
        [
            (" shift + ALT + control + f5 ", "Ctrl+Alt+Shift+F5"),
            ("ctrl+m", "Ctrl+M"), ("Alt+Up", "Alt+Up"), ("Shift+F8", "Shift+F8"),
            ("Ctrl+Return", "Ctrl+Enter"), ("Ctrl+Enter", "Ctrl+Enter"),
            ("Ctrl+Prior", "Ctrl+PageUp"), ("Ctrl+PgUp", "Ctrl+PageUp"),
            ("Ctrl+Next", "Ctrl+PageDown"), ("Ctrl+PgDn", "Ctrl+PageDown"),
            ("Alt+Backspace", "Alt+Back"), ("Ctrl+Del", "Ctrl+Delete"), ("Ctrl+Ins", "Ctrl+Insert"),
            ("Ctrl+1", "Ctrl+D1"), ("Ctrl+NumPad1", "Ctrl+NumPad1"),
            ("Ctrl+Oem1", "Ctrl+OemSemicolon"), ("Ctrl+OemSemicolon", "Ctrl+OemSemicolon"),
            ("Ctrl+Oem2", "Ctrl+OemQuestion"), ("Ctrl+Scroll", "Ctrl+ScrollLock"),
            ("Ctrl+Space", "Ctrl+Space"), ("Ctrl+Add", "Ctrl+Add"), ("", ""), ("   ", "")
        ];
        foreach ((string text, string canonical) in examples)
        {
            bool parsed = HotkeyBinding.TryNormalizeGesture(text, out string actual, out HotkeyErrorCode error);
            Check(parsed && actual == canonical && error == HotkeyErrorCode.None, $"Hotkey parser canonicalizes {text}");
            Check(HotkeyBinding.TryNormalizeGesture(actual, out string repeated, out _) && repeated == actual,
                $"Canonical hotkey is stable: {actual}");
        }
        (string? Text, HotkeyErrorCode Code)[] invalid =
        [
            (null, HotkeyErrorCode.InvalidGesture), ("A", HotkeyErrorCode.ModifierRequired),
            ("Escape", HotkeyErrorCode.ModifierRequired), ("Tab", HotkeyErrorCode.ModifierRequired),
            ("F5", HotkeyErrorCode.ModifierRequired), ("Ctrl", HotkeyErrorCode.InvalidGesture),
            ("Alt", HotkeyErrorCode.InvalidGesture), ("Shift", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+Alt", HotkeyErrorCode.InvalidGesture), ("Ctrl+LeftCtrl", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+RightAlt", HotkeyErrorCode.InvalidGesture), ("Ctrl+System", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+None", HotkeyErrorCode.InvalidGesture), ("Ctrl+65", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+A,B", HotkeyErrorCode.InvalidGesture), ("Ctrl++A", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+", HotkeyErrorCode.InvalidGesture), ("Ctrl+A+B", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+Control+A", HotkeyErrorCode.InvalidGesture), ("Ctrl+NotAKey", HotkeyErrorCode.InvalidGesture),
            ("Ctrl+\nA", HotkeyErrorCode.InvalidGesture), (new string('A', 129), HotkeyErrorCode.InvalidGesture),
            ("F12", HotkeyErrorCode.ReservedGesture), ("Ctrl+F12", HotkeyErrorCode.ReservedGesture),
            ("Win+A", HotkeyErrorCode.ReservedGesture), ("Windows+Shift+A", HotkeyErrorCode.ReservedGesture),
            ("Ctrl+LWin", HotkeyErrorCode.ReservedGesture), ("Ctrl+Alt+Delete", HotkeyErrorCode.ReservedGesture),
            ("Alt+Tab", HotkeyErrorCode.ReservedGesture), ("Alt+F4", HotkeyErrorCode.ReservedGesture),
            ("Ctrl+Esc", HotkeyErrorCode.ReservedGesture)
        ];
        foreach ((string? text, HotkeyErrorCode code) in invalid)
        {
            Check(!HotkeyBinding.TryNormalizeGesture(text, out string actual, out HotkeyErrorCode error)
                && actual.Length == 0 && error == code, $"Hotkey parser rejects {text ?? "null"}: {code}");
        }
        Check(HotkeyBinding.TryFormatGesture(ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt,
            Key.Return, out string formatted, out _) && formatted == "Ctrl+Alt+Shift+Enter",
            "Capture formatter uses the same canonical modifier and key aliases as typed gestures");
        Check(!HotkeyBinding.TryFormatGesture(ModifierKeys.Control, Key.LeftCtrl, out _, out _),
            "Capture formatter ignores a modifier pressed without a primary key");
        Check(!HotkeyBinding.TryFormatGesture(ModifierKeys.Control, Key.F12, out _, out HotkeyErrorCode reserved)
            && reserved == HotkeyErrorCode.ReservedGesture, "Capture formatter also rejects reserved F12");
        Check(!HotkeyBinding.TryFormatGesture((ModifierKeys)128, Key.A, out _, out _),
            "Capture formatter rejects undefined modifier bits");
        Throws<ArgumentNullException>(() => HotkeyBinding.TryFormatGesture(null!, out _, out _),
            "Capture formatter rejects missing key event arguments without reading keyboard state");
    }

    private static void VerifyHotkeyBindingValidation()
    {
        Check(HotkeyBinding.Validate(null)?.Code == HotkeyErrorCode.MissingBindings,
            "Hotkey validation reports a missing collection as data");
        Check(HotkeyBinding.Validate([])?.Code == HotkeyErrorCode.MissingAction,
            "Hotkey validation requires an entry for every action");
        HotkeyBinding[] empty = HotkeyTestBindings();
        Check(HotkeyBinding.Validate(empty) is null, "Every hotkey action may be explicitly unassigned");
        Check(HotkeyBinding.Validate(empty.Skip(1))?.Action == HotkeyAction.ShowWindow,
            "Missing-action errors identify the action for parent localization");
        HotkeyBinding[] duplicateAction = [.. empty, empty[0]];
        Check(HotkeyBinding.Validate(duplicateAction)?.Code == HotkeyErrorCode.DuplicateAction,
            "Duplicate action entries are rejected even when unassigned");
        HotkeyBinding[] invalid = HotkeyBinding.Copy(empty);
        invalid[0] = null!;
        Check(HotkeyBinding.Validate(invalid)?.Code == HotkeyErrorCode.InvalidBinding,
            "Missing hotkey records are rejected without a null dereference");
        invalid[0] = new((HotkeyAction)int.MaxValue, "Ctrl+A");
        Check(HotkeyBinding.Validate(invalid)?.Code == HotkeyErrorCode.InvalidAction,
            "Undefined JSON action values are rejected");
        invalid[0] = new(HotkeyAction.ShowWindow, null!);
        Check(HotkeyBinding.Validate(invalid)?.Code == HotkeyErrorCode.InvalidGesture,
            "Null JSON gestures are rejected rather than silently unassigned");
        HotkeyBinding[] duplicates = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "alt+control+Return"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+Alt+Enter"));
        HotkeyError? conflict = HotkeyBinding.Validate(duplicates);
        Check(conflict?.Code == HotkeyErrorCode.DuplicateGesture && conflict.Action == HotkeyAction.ReadStatus
            && conflict.ConflictingAction == HotkeyAction.ShowWindow && conflict.Gesture == "Ctrl+Alt+Enter",
            "Normalized duplicate errors identify both actions and the canonical gesture");
        Throws<ArgumentException>(() => HotkeyBinding.ValidateBindings(duplicates),
            "UiPreferences validation helper throws for normalized duplicates");
        Throws<ArgumentException>(() => HotkeyBinding.ValidateBindings([]),
            "UiPreferences validation helper throws for missing action entries");
        HotkeyBinding[] typed = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, " alt + CTRL + h "));
        Check(HotkeyBinding.TryNormalize(typed, out HotkeyBinding[] normalized, out HotkeyError? error)
            && error is null && normalized[0].Gesture == "Ctrl+Alt+H" && typed[0].Gesture == " alt + CTRL + h ",
            "Collection normalization produces a new canonical set without mutating the settings draft");
        Check(!HotkeyBinding.TryNormalize(duplicates, out HotkeyBinding[] rejected, out _) && rejected.Length == 0,
            "Failed collection normalization never returns partially validated bindings");
    }

    private static void VerifyHotkeyRegistration()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        Check(!hotkeys.Enabled && !hotkeys.IsSuspended && hotkeys.IsConsistent && native.Calls.Count == 0,
            "Constructing global hotkeys leaves every optional shortcut disabled");
        HotkeyBinding[] bindings = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "alt+ctrl+h"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+F5"));
        Check(hotkeys.Configure(false, bindings).Succeeded && native.Calls.Count == 0,
            "Disabled configuration validates and stores bindings without native registration");
        Check(hotkeys.Configure(true, bindings).Succeeded && hotkeys.Enabled && hotkeys.IsConsistent
            && native.Registered.Count == 2, "Enabling registers only assigned hotkeys");
        Check(native.Registered.Values.All(value => (value.Modifiers & GlobalHotkeys.NoRepeat) != 0)
            && hotkeys.ActiveBindings[0].Gesture == "Ctrl+Alt+H", "Native registration uses MOD_NOREPEAT and canonical binding data");
        int calls = native.Calls.Count;
        Check(hotkeys.Configure(true, bindings).Succeeded && native.Calls.Count == calls,
            "Applying unchanged hotkeys preserves native registrations and their identifiers");
        bindings[0] = bindings[0] with { Gesture = "Ctrl+F9" };
        Check(hotkeys.ActiveBindings[0].Gesture == "Ctrl+Alt+H", "A caller cannot mutate committed hotkeys through its input array");
        Check(hotkeys.Configure(false, bindings).Succeeded && !hotkeys.Enabled && hotkeys.IsConsistent
            && native.Registered.Count == 0, "Disabling unregisters all owned hotkeys");
        Check(hotkeys.Configure(true, HotkeyTestBindings()).Succeeded && hotkeys.Enabled
            && hotkeys.ActiveBindings.Count == 0 && hotkeys.IsConsistent, "An enabled set may have every action unassigned");
    }

    private static void VerifyHotkeyEnableConflict()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] bindings = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"));
        native.Block("Ctrl+B");
        HotkeyOperationResult failure = hotkeys.Configure(true, bindings);
        Check(!failure.Succeeded && failure.Error?.Code == HotkeyErrorCode.RegistrationFailed
            && failure.Error.Action == HotkeyAction.ReadStatus && failure.Error.Win32Error == 1409,
            "Enable conflicts return the failing action and Win32 error as localizable data");
        Check(failure.RestorationSucceeded && !hotkeys.Enabled && hotkeys.IsConsistent && native.Registered.Count == 0,
            "A failed enable unregisters earlier candidate hotkeys and restores disabled state");
        native.Unblock();
        Check(hotkeys.Configure(true, bindings).Succeeded && native.Registered.Count == 2,
            "An enable conflict can be corrected and retried using the same service");
        int calls = native.Calls.Count;
        Check(!hotkeys.Configure(true, []).Succeeded && native.Calls.Count == calls && hotkeys.IsConsistent,
            "Invalid edits fail before touching existing native registrations");
    }

    private static void VerifyHotkeyEditRollback()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] original = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"), new HotkeyBinding(HotkeyAction.CycleNoise, "Ctrl+C"));
        Check(hotkeys.Configure(true, original).Succeeded, "Edit rollback fixture enables its original hotkeys");
        HotkeyNativeEntry[] before = native.Registered.Values.OrderBy(entry => entry.Id).ToArray();
        HotkeyBinding[] candidate = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+D"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+E"), new HotkeyBinding(HotkeyAction.CycleNoise, "Ctrl+C"));
        native.Block("Ctrl+E");
        HotkeyOperationResult failure = hotkeys.Configure(true, candidate);
        Check(!failure.Succeeded && failure.RestorationSucceeded && hotkeys.Enabled && hotkeys.IsConsistent
            && native.Registered.Values.OrderBy(entry => entry.Id).SequenceEqual(before),
            "An edit conflict removes new registrations and restores original keys and identifiers");
        Check(hotkeys.ActiveBindings.SequenceEqual(original.Where(binding => binding.Gesture.Length > 0)),
            "A failed edit preserves the committed action-to-gesture mapping");
        HotkeyBinding[] swapped = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+B"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+A"), new HotkeyBinding(HotkeyAction.CycleNoise, "Ctrl+C"));
        int unchangedId = before.Single(entry => entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.C)).Id;
        int start = native.Calls.Count;
        Check(hotkeys.Configure(true, swapped).Succeeded && hotkeys.IsConsistent,
            "Two actions can exchange gestures in one native transaction");
        Check(native.Registered.ContainsKey((source.Handle, unchangedId))
            && native.Calls.Skip(start).All(call => call.Entry.Id != unchangedId),
            "Editing other actions leaves an unchanged registration available throughout");
    }

    private static void VerifyHotkeyUnregisterRollback()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] original = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"));
        Check(hotkeys.Configure(true, original).Succeeded, "Unregister rollback fixture enables its original set");
        HotkeyNativeEntry[] before = native.Registered.Values.OrderBy(entry => entry.Id).ToArray();
        native.Fail = call => !call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.B) ? 5 : 0;
        HotkeyOperationResult failure = hotkeys.Configure(false, original);
        Check(!failure.Succeeded && failure.Error?.Code == HotkeyErrorCode.UnregistrationFailed
            && failure.Error.Win32Error == 5 && failure.RestorationSucceeded && hotkeys.Enabled && hotkeys.IsConsistent
            && native.Registered.Values.OrderBy(entry => entry.Id).SequenceEqual(before),
            "A failed disable restores already removed hotkeys and preserves the enabled configuration");
        native.Fail = null;
        Check(hotkeys.Configure(false, original).Succeeded && native.Registered.Count == 0,
            "Disable succeeds after its unregister failure is resolved");
    }

    private static void VerifyHotkeyPartialRestoration()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        int dispatched = 0;
        using var hotkeys = new GlobalHotkeys(source, _ => dispatched++, nativeApi: native);
        HotkeyBinding[] original = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"));
        Check(hotkeys.Configure(true, original).Succeeded, "Partial rollback fixture enables its original set");
        HotkeyBinding[] candidate = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+D"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+E"));
        native.Block("Ctrl+E");
        native.Fail = call => call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.A)
            || !call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.D) ? 5 : 0;
        HotkeyOperationResult failure = hotkeys.Configure(true, candidate);
        Check(!failure.Succeeded && failure.Error?.Win32Error == 1409 && !failure.RestorationSucceeded
            && failure.RestorationErrors.Count == 2
            && failure.RestorationErrors.Any(error => error.Code == HotkeyErrorCode.UnregistrationFailed)
            && failure.RestorationErrors.Any(error => error.Code == HotkeyErrorCode.RegistrationFailed),
            "Partial rollback reports both leaked-candidate cleanup and original restoration failures honestly");
        Check(!hotkeys.IsConsistent && hotkeys.Enabled && hotkeys.ActiveBindings.Count == native.Registered.Count
            && hotkeys.ActiveBindings.Any(binding => binding.Gesture == "Ctrl+D")
            && hotkeys.ActiveBindings.All(binding => binding.Gesture != "Ctrl+A"),
            "Partial rollback exposes actual registrations rather than claiming the original set was restored");
        foreach (HotkeyNativeEntry entry in native.Registered.Values.ToArray()) SendHotkeyMessage(hotkeys, entry);
        Check(dispatched == 0, "An inconsistent native registration set dispatches no application actions");
        native.Fail = null;
        native.Unblock();
        Check(hotkeys.Configure(true, original).Succeeded && hotkeys.IsConsistent
            && hotkeys.ActiveBindings.SequenceEqual(original.Where(binding => binding.Gesture.Length > 0)),
            "Reconfiguration repairs a partial rollback by removing leaks and restoring missing actions");
    }

    private static void VerifyHotkeySuspension()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] original = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"));
        HotkeyBinding[] candidate = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+B"));
        Check(hotkeys.Configure(true, original).Succeeded && hotkeys.Suspend().Succeeded && hotkeys.IsSuspended
            && hotkeys.Enabled && hotkeys.IsConsistent && native.Registered.Count == 0,
            "Suspending releases native registrations so a settings capture can receive the mapped combination");
        int calls = native.Calls.Count;
        Check(hotkeys.Configure(true, candidate).Succeeded && hotkeys.Suspend().Succeeded
            && hotkeys.Resume().Succeeded && hotkeys.IsSuspended && native.Calls.Count == calls,
            "Nested modal suspension and editing do not prematurely reactivate global shortcuts");
        native.Block("Ctrl+B");
        HotkeyOperationResult conflict = hotkeys.Resume();
        Check(!conflict.Succeeded && conflict.Error?.Win32Error == 1409 && conflict.RestorationSucceeded
            && hotkeys.IsSuspended && hotkeys.IsConsistent && native.Registered.Count == 0,
            "The final resume probes edited shortcuts and remains suspended on an OS conflict before settings are saved");
        native.Unblock();
        Check(hotkeys.Resume().Succeeded && !hotkeys.IsSuspended && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+B",
            "A successful final resume applies the edited candidate");
        Check(hotkeys.Suspend().Succeeded && hotkeys.Configure(true, original).Succeeded
            && hotkeys.IsSuspended && native.Registered.Count == 0,
            "Settings persistence failure can re-suspend and restore the old configuration for continued capture");
        Check(hotkeys.Resume().Succeeded && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+A",
            "Closing settings after rollback restores the original shortcuts");
        calls = native.Calls.Count;
        Check(hotkeys.Resume().Succeeded && native.Calls.Count == calls, "An unmatched resume is an idempotent no-op");
        Check(hotkeys.Suspend().Succeeded && hotkeys.Configure(false, candidate).Succeeded && hotkeys.Resume().Succeeded
            && !hotkeys.Enabled && native.Registered.Count == 0, "Disabling inside settings remains disabled after the modal closes");
    }

    private static void VerifyHotkeyConfigureAndResume()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] original = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"));
        HotkeyBinding[] candidate = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+B"));
        Check(hotkeys.Configure(true, original).Succeeded && hotkeys.Suspend().Succeeded,
            "Atomic save fixture releases its original shortcut for capture");
        native.Block("Ctrl+A");
        Check(hotkeys.ConfigureAndResume(true, candidate).Succeeded && !hotkeys.IsSuspended
            && hotkeys.IsConsistent && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+B",
            "Atomic configuration resumes a free replacement even if another application claimed the old suspended shortcut");
        Check(hotkeys.Suspend().Succeeded, "Atomic save fixture suspends its committed replacement");
        HotkeyBinding[] conflictingCandidate = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+C"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+A"));
        HotkeyOperationResult conflict = hotkeys.ConfigureAndResume(true, conflictingCandidate);
        Check(!conflict.Succeeded && conflict.Error?.Win32Error == 1409 && hotkeys.IsSuspended
            && hotkeys.IsConsistent && native.Registered.Count == 0,
            "An atomic save conflict preserves suspension and rolls back all candidate registrations");
        Check(hotkeys.Resume().Succeeded && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+B",
            "An atomic save conflict retains the previously committed candidate rather than the failed edit");
        Check(hotkeys.Suspend().Succeeded && hotkeys.Suspend().Succeeded,
            "Atomic save fixture can represent an additional active modal scope");
        int calls = native.Calls.Count;
        Check(hotkeys.ConfigureAndResume(false, original).Error?.Code == HotkeyErrorCode.NestedSuspension
            && hotkeys.IsSuspended && hotkeys.Enabled && native.Calls.Count == calls,
            "Atomic save cannot silently release an unrelated nested modal suspension");
        Check(hotkeys.Resume().Succeeded && hotkeys.Resume().Succeeded
            && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+B", "Rejected nested save preserves the committed bindings");
        Check(hotkeys.ConfigureAndResume(false, candidate).Succeeded && !hotkeys.Enabled && native.Registered.Count == 0,
            "Atomic save without a suspension applies an ordinary disabled configuration");
        native.Unblock();
        Check(hotkeys.ConfigureAndResume(true, original).Succeeded && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+A",
            "Atomic save without a suspension can also enable a configuration");
        Check(hotkeys.Suspend().Succeeded && !hotkeys.ConfigureAndResume(true, []).Succeeded
            && hotkeys.IsSuspended && hotkeys.Enabled, "Invalid atomic edits leave suspension and committed state intact");
        Check(hotkeys.Resume().Succeeded && hotkeys.ActiveBindings.Single().Gesture == "Ctrl+A",
            "Resuming after invalid atomic input uses the last valid configuration");
    }

    private static void VerifyHotkeySuspendFailure()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] bindings = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"));
        Check(hotkeys.Configure(true, bindings).Succeeded, "Suspend failure fixture enables its original set");
        native.Fail = call => !call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.B) ? 5 : 0;
        HotkeyOperationResult failure = hotkeys.Suspend();
        Check(!failure.Succeeded && !hotkeys.IsSuspended && hotkeys.IsConsistent && native.Registered.Count == 2,
            "A failed capture suspension restores the original native set without claiming capture is available");
        native.Fail = null;
    }

    private static void VerifyHotkeyDispatch()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        var actions = new List<HotkeyAction>();
        bool allowed = true;
        int callbackThread = 0;
        using var hotkeys = new GlobalHotkeys(source, action =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            actions.Add(action);
        }, () => allowed, native);
        HotkeyBinding[] bindings = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"));
        Check(hotkeys.Configure(true, bindings).Succeeded, "Synthetic WM_HOTKEY fixture enables its fake registration");
        HotkeyNativeEntry original = native.Registered.Values.Single();
        bool handled = false;
        hotkeys.HandleMessage(source.Handle, 0x0100, new IntPtr(original.Id), IntPtr.Zero, ref handled);
        Check(!handled && actions.Count == 0, "The hotkey hook leaves ordinary keyboard messages untouched");
        Check(SendHotkeyMessage(hotkeys, original) && actions.SequenceEqual(new[] { HotkeyAction.ShowWindow })
            && callbackThread == Environment.CurrentManagedThreadId,
            "Owned WM_HOTKEY dispatch invokes the mapped action on the source UI thread without input injection");
        allowed = false;
        Check(SendHotkeyMessage(hotkeys, original) && actions.Count == 1,
            "The modal predicate suppresses dispatch while recognizing an owned message");
        allowed = true;
        Check(!SendHotkeyMessage(hotkeys, original with { Id = 0xbfff }) && actions.Count == 1,
            "Unknown registration identifiers do not dispatch or consume another component's hotkey");
        Check(!SendHotkeyMessage(hotkeys, original with { VirtualKey = original.VirtualKey + 1 })
            && !SendHotkeyMessage(hotkeys, original with { Modifiers = original.Modifiers ^ 1 })
            && !SendHotkeyMessage(hotkeys, original with { Hwnd = new IntPtr(-1) }) && actions.Count == 1,
            "The hook verifies the key, modifiers and owning window against the registration");
        Check(hotkeys.Suspend().Succeeded && !SendHotkeyMessage(hotkeys, original) && actions.Count == 1,
            "A message queued before suspension cannot dispatch during capture");
        Check(hotkeys.Resume().Succeeded && !SendHotkeyMessage(hotkeys, original) && actions.Count == 1,
            "Resume uses fresh identifiers so a message queued before capture stays obsolete");
        HotkeyNativeEntry resumed = native.Registered.Values.Single();
        Check(SendHotkeyMessage(hotkeys, resumed) && actions.Count == 2, "A current post-capture message dispatches normally");
        Throws<InvalidOperationException>(() => Task.Run(() => hotkeys.Configure(false, bindings)).GetAwaiter().GetResult(),
            "Reconfiguration rejects a worker thread before making native calls");
        Throws<InvalidOperationException>(() => Task.Run(() => hotkeys.Suspend()).GetAwaiter().GetResult(),
            "Suspension also requires the HwndSource dispatcher thread");
        Check(hotkeys.TryDispose().Succeeded && !SendHotkeyMessage(hotkeys, resumed) && actions.Count == 2,
            "Disposal prevents queued messages from invoking application actions");
    }

    private static void VerifyHotkeyDisposal()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var hotkeys = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        HotkeyBinding[] bindings = HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"),
            new HotkeyBinding(HotkeyAction.ReadStatus, "Ctrl+B"));
        Check(hotkeys.Configure(true, bindings).Succeeded, "Cleanup failure fixture enables both hotkeys");
        native.Fail = call => !call.Registering && call.Entry.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(Key.A) ? 5 : 0;
        HotkeyOperationResult failure = hotkeys.TryDispose();
        Check(!failure.Succeeded && failure.Errors.Count == 1 && failure.Error?.Win32Error == 5
            && hotkeys.LastResult == failure && hotkeys.IsDisposed && !hotkeys.Enabled && !hotkeys.IsConsistent
            && native.Registered.Count == 1 && hotkeys.ActiveBindings.Count == 1,
            "Disposal attempts every unregister and reports any remaining native registration");
        Throws<ObjectDisposedException>(() => hotkeys.Configure(true, bindings), "Disposed hotkeys cannot be re-enabled");
        Throws<ObjectDisposedException>(() => hotkeys.Suspend(), "Disposed hotkeys cannot enter a new capture scope");
        native.Fail = null;
        Check(hotkeys.TryDispose().Succeeded && hotkeys.IsConsistent && native.Registered.Count == 0,
            "Explicit cleanup can retry an unregister failure even after disposal");
        int calls = native.Calls.Count;
        hotkeys.Dispose();
        Check(native.Calls.Count == calls, "Repeated successful disposal makes no extra native calls");
    }

    private static void VerifyHotkeySourceOwnership()
    {
        using HwndSource source = CreateHotkeyTestSource();
        var native = new FakeHotkeyNativeApi();
        using var first = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        using var second = new GlobalHotkeys(source, _ => { }, nativeApi: native);
        Check(first.Configure(true, HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+A"))).Succeeded
            && second.Configure(true, HotkeyTestBindings(new HotkeyBinding(HotkeyAction.ShowWindow, "Ctrl+B"))).Succeeded
            && native.Registered.Count == 2, "Services sharing a source allocate distinct native identifiers");
        first.Dispose();
        Check(second.IsConsistent && native.Registered.Count == 1 && !second.IsDisposed,
            "Disposing one source client preserves another client's registration");
        source.Dispose();
        Check(second.IsDisposed && native.Registered.Count == 0,
            "HwndSource disposal automatically cleans up its remaining hotkey service");
        Throws<ObjectDisposedException>(() => new GlobalHotkeys(source, _ => { }, nativeApi: native),
            "A destroyed source cannot acquire a new hotkey hook");
    }

    private static HotkeyBinding[] HotkeyTestBindings(params HotkeyBinding[] assigned) => Enum.GetValues<HotkeyAction>()
        .Select(action => assigned.SingleOrDefault(binding => binding.Action == action) ?? new HotkeyBinding(action, "")).ToArray();

    private static HwndSource CreateHotkeyTestSource() => new(new HwndSourceParameters("Hollylark.HotkeyChecks." + Guid.NewGuid().ToString("N"))
    {
        ParentWindow = new IntPtr(-3),
        WindowStyle = 0,
        Width = 1,
        Height = 1
    });

    private static bool SendHotkeyMessage(GlobalHotkeys hotkeys, HotkeyNativeEntry entry)
    {
        bool handled = false;
        uint payload = entry.VirtualKey << 16 | entry.Modifiers & ~GlobalHotkeys.NoRepeat;
        hotkeys.HandleMessage(entry.Hwnd, GlobalHotkeys.HotkeyMessage, new IntPtr(entry.Id), new IntPtr(payload), ref handled);
        return handled;
    }

    private sealed record HotkeyNativeEntry(IntPtr Hwnd, int Id, uint Modifiers, uint VirtualKey);
    private sealed record HotkeyNativeCall(bool Registering, HotkeyNativeEntry Entry);

    private sealed class FakeHotkeyNativeApi : IHotkeyNativeApi
    {
        internal Dictionary<(IntPtr Hwnd, int Id), HotkeyNativeEntry> Registered { get; } = new();
        internal List<HotkeyNativeCall> Calls { get; } = new();
        internal Func<HotkeyNativeCall, int>? Fail { get; set; }
        private readonly HashSet<(uint Modifiers, uint VirtualKey)> blocked = new();

        internal void Block(string text)
        {
            if (!HotkeyBinding.TryParseGesture(text, out ParsedHotkey gesture, out _) || gesture.VirtualKey == 0)
                throw new ArgumentException("The fake conflict must use an assigned valid gesture.", nameof(text));
            blocked.Add((gesture.Modifiers, gesture.VirtualKey));
        }

        internal void Unblock() => blocked.Clear();

        public bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int win32Error)
        {
            var entry = new HotkeyNativeEntry(hwnd, id, modifiers, virtualKey);
            var call = new HotkeyNativeCall(true, entry);
            Calls.Add(call);
            win32Error = Fail?.Invoke(call) ?? 0;
            if (win32Error != 0) return false;
            uint keyModifiers = modifiers & ~GlobalHotkeys.NoRepeat;
            if (blocked.Contains((keyModifiers, virtualKey)) || Registered.Values.Any(value => value.VirtualKey == virtualKey
                && (value.Modifiers & ~GlobalHotkeys.NoRepeat) == keyModifiers))
            {
                win32Error = 1409;
                return false;
            }
            Registered.Add((hwnd, id), entry);
            return true;
        }

        public bool UnregisterHotKey(IntPtr hwnd, int id, out int win32Error)
        {
            HotkeyNativeEntry entry = Registered.GetValueOrDefault((hwnd, id)) ?? new(hwnd, id, 0, 0);
            var call = new HotkeyNativeCall(false, entry);
            Calls.Add(call);
            win32Error = Fail?.Invoke(call) ?? 0;
            if (win32Error != 0) return false;
            if (Registered.Remove((hwnd, id))) return true;
            win32Error = 1419;
            return false;
        }
    }
}
