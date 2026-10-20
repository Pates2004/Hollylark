using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LarkControl.Windows;

public interface IHotkeyNativeApi
{
    bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int win32Error);
    bool UnregisterHotKey(IntPtr hwnd, int id, out int win32Error);
}

public sealed class HotkeyOperationResult
{
    public bool Succeeded => Errors.Count == 0;
    public HotkeyError? Error => Errors.Count == 0 ? null : Errors[0];
    public IReadOnlyList<HotkeyError> Errors { get; }
    public IReadOnlyList<HotkeyError> RestorationErrors { get; }
    public bool RestorationSucceeded => RestorationErrors.Count == 0;

    internal static HotkeyOperationResult Success { get; } = new([], []);

    internal HotkeyOperationResult(HotkeyError[] errors, HotkeyError[] restorationErrors)
    {
        Errors = Array.AsReadOnly(errors);
        RestorationErrors = Array.AsReadOnly(restorationErrors);
    }
}

public sealed class GlobalHotkeys : IDisposable
{
    internal const int HotkeyMessage = 0x0312;
    internal const uint NoRepeat = 0x4000;
    private static readonly ConditionalWeakTable<HwndSource, IdentifierPool> IdentifierPools = new();
    private readonly HwndSource source;
    private readonly IntPtr hwnd;
    private readonly IHotkeyNativeApi nativeApi;
    private readonly Action<HotkeyAction> dispatch;
    private readonly Func<bool>? canDispatch;
    private readonly IdentifierPool identifiers;
    private readonly Dictionary<int, Registration> registrations = new();
    private HotkeyBinding[] bindings = HotkeyBinding.CreateDefaults();
    private bool changing;
    private bool disposed;
    private bool hookAttached;
    private int suspensionCount;

    public bool Enabled { get; private set; }
    public bool IsSuspended => suspensionCount > 0;
    public bool IsDisposed => disposed;
    public bool IsConsistent => Matches(Enabled && !IsSuspended && !disposed ? AssignedBindings(bindings) : []);
    public HotkeyOperationResult LastResult { get; private set; } = HotkeyOperationResult.Success;
    public IReadOnlyList<HotkeyBinding> ActiveBindings => Array.AsReadOnly(registrations.Values
        .OrderBy(registration => registration.Binding.Action).Select(registration => registration.Binding with { }).ToArray());

    public GlobalHotkeys(HwndSource source, Action<HotkeyAction> dispatch,
        Func<bool>? canDispatch = null, IHotkeyNativeApi? nativeApi = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dispatch);
        source.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);
        if (source.Handle == IntPtr.Zero)
            throw new ArgumentException("The hotkey source must have a native window handle.", nameof(source));
        this.source = source;
        hwnd = source.Handle;
        this.dispatch = dispatch;
        this.canDispatch = canDispatch;
        this.nativeApi = nativeApi ?? new WindowsHotkeyNativeApi();
        identifiers = IdentifierPools.GetValue(source, _ => new IdentifierPool());
        source.AddHook(HandleMessage);
        hookAttached = true;
        source.Disposed += OnSourceDisposed;
    }

    public HotkeyOperationResult Configure(bool enabled, IEnumerable<HotkeyBinding>? bindings)
    {
        VerifyUsable();
        if (!HotkeyBinding.TryNormalize(bindings, out HotkeyBinding[] normalized, out HotkeyError? error))
            return LastResult = new([error!], []);
        HotkeyOperationResult result = Transition(enabled && !IsSuspended ? AssignedBindings(normalized) : []);
        if (result.Succeeded)
        {
            this.bindings = normalized;
            Enabled = enabled;
        }
        return LastResult = result;
    }

    public HotkeyOperationResult Suspend()
    {
        VerifyUsable();
        if (suspensionCount == int.MaxValue)
            throw new InvalidOperationException("The hotkey suspension count is exhausted.");
        HotkeyOperationResult result = Transition([]);
        if (result.Succeeded) suspensionCount++;
        return LastResult = result;
    }

    public HotkeyOperationResult ConfigureAndResume(bool enabled, IEnumerable<HotkeyBinding>? bindings)
    {
        VerifyUsable();
        if (suspensionCount > 1)
            return LastResult = new([new(HotkeyErrorCode.NestedSuspension)], []);
        if (!HotkeyBinding.TryNormalize(bindings, out HotkeyBinding[] normalized, out HotkeyError? error))
            return LastResult = new([error!], []);
        HotkeyOperationResult result = Transition(enabled ? AssignedBindings(normalized) : []);
        if (result.Succeeded)
        {
            this.bindings = normalized;
            Enabled = enabled;
            suspensionCount = 0;
        }
        return LastResult = result;
    }

    public HotkeyOperationResult Resume()
    {
        VerifyUsable();
        if (suspensionCount == 0) return LastResult = HotkeyOperationResult.Success;
        HotkeyOperationResult result = Transition(Enabled && suspensionCount == 1 ? AssignedBindings(bindings) : []);
        if (result.Succeeded) suspensionCount--;
        return LastResult = result;
    }

    public HotkeyOperationResult TryDispose()
    {
        source.Dispatcher.VerifyAccess();
        if (changing) throw new InvalidOperationException("A hotkey change is already in progress.");
        disposed = true;
        Enabled = false;
        suspensionCount = 0;
        var errors = new List<HotkeyError>();
        changing = true;
        try
        {
            foreach (Registration registration in registrations.Values.ToArray())
            {
                if (Remove(registration) is HotkeyError error) errors.Add(error);
            }
            if (hookAttached)
            {
                if (!source.IsDisposed) source.RemoveHook(HandleMessage);
                hookAttached = false;
            }
            source.Disposed -= OnSourceDisposed;
        }
        finally { changing = false; }
        return LastResult = new(errors.ToArray(), []);
    }

    public void Dispose() => TryDispose();

    internal IntPtr HandleMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HotkeyMessage || handle != hwnd || disposed) return IntPtr.Zero;
        source.Dispatcher.VerifyAccess();
        long id = wParam.ToInt64();
        if (id is < 1 or > 0xbfff || !registrations.TryGetValue((int)id, out Registration? registration))
            return IntPtr.Zero;
        uint payload = unchecked((uint)lParam.ToInt64());
        if ((payload & 0xffff) != registration.Gesture.Modifiers
            || (payload >> 16) != registration.Gesture.VirtualKey)
            return IntPtr.Zero;
        handled = true;
        if (!changing && Enabled && !IsSuspended && IsConsistent && (canDispatch?.Invoke() ?? true)
            && !disposed && Enabled && !IsSuspended && IsConsistent && registrations.ContainsKey(registration.Id))
            dispatch(registration.Binding.Action);
        return IntPtr.Zero;
    }

    private HotkeyOperationResult Transition(HotkeyBinding[] desired)
    {
        Registration[] previous = registrations.Values.ToArray();
        changing = true;
        try
        {
            foreach (Registration registration in previous)
            {
                if (desired.Contains(registration.Binding)) continue;
                if (Remove(registration) is HotkeyError error) return Restore(previous, error);
            }
            foreach (HotkeyBinding binding in desired)
            {
                if (registrations.Values.Any(registration => registration.Binding == binding)) continue;
                if (!identifiers.TryNext(out int id))
                    return Restore(previous, new(HotkeyErrorCode.IdentifierLimitReached, binding.Action, binding.Gesture));
                HotkeyBinding.TryParseGesture(binding.Gesture, out ParsedHotkey gesture, out _);
                var registration = new Registration(id, binding, gesture);
                if (Add(registration) is HotkeyError error) return Restore(previous, error);
            }
            return HotkeyOperationResult.Success;
        }
        finally { changing = false; }
    }

    private HotkeyOperationResult Restore(Registration[] previous, HotkeyError failure)
    {
        var errors = new List<HotkeyError>();
        foreach (Registration registration in registrations.Values.ToArray())
        {
            if (previous.Any(original => original.Id == registration.Id)) continue;
            if (Remove(registration) is HotkeyError error) errors.Add(error);
        }
        foreach (Registration registration in previous)
        {
            if (registrations.ContainsKey(registration.Id)) continue;
            if (Add(registration) is HotkeyError error) errors.Add(error);
        }
        return new([failure], errors.ToArray());
    }

    private HotkeyError? Add(Registration registration)
    {
        if (!nativeApi.RegisterHotKey(hwnd, registration.Id, registration.Gesture.Modifiers | NoRepeat,
            registration.Gesture.VirtualKey, out int error))
            return new(HotkeyErrorCode.RegistrationFailed, registration.Binding.Action, registration.Binding.Gesture, error);
        registrations.Add(registration.Id, registration);
        return null;
    }

    private HotkeyError? Remove(Registration registration)
    {
        if (!nativeApi.UnregisterHotKey(hwnd, registration.Id, out int error))
            return new(HotkeyErrorCode.UnregistrationFailed, registration.Binding.Action, registration.Binding.Gesture, error);
        registrations.Remove(registration.Id);
        return null;
    }

    private bool Matches(HotkeyBinding[] expected) => expected.Length == registrations.Count
        && expected.All(binding => registrations.Values.Any(registration => registration.Binding == binding));

    private static HotkeyBinding[] AssignedBindings(HotkeyBinding[] candidates) => candidates
        .Where(binding => binding.Gesture.Length > 0).ToArray();

    private void VerifyUsable()
    {
        source.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);
        if (changing) throw new InvalidOperationException("A hotkey change is already in progress.");
    }

    private void OnSourceDisposed(object? sender, EventArgs args) => Dispose();

    private sealed record Registration(int Id, HotkeyBinding Binding, ParsedHotkey Gesture);

    private sealed class IdentifierPool
    {
        private int last;

        internal bool TryNext(out int id)
        {
            id = 0;
            if (last == 0xbfff) return false;
            id = ++last;
            return true;
        }
    }

    private sealed class WindowsHotkeyNativeApi : IHotkeyNativeApi
    {
        public bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int win32Error)
        {
            bool success = Native.RegisterHotKey(hwnd, id, modifiers, virtualKey);
            win32Error = success ? 0 : Marshal.GetLastPInvokeError();
            return success;
        }

        public bool UnregisterHotKey(IntPtr hwnd, int id, out int win32Error)
        {
            bool success = Native.UnregisterHotKey(hwnd, id);
            win32Error = success ? 0 : Marshal.GetLastPInvokeError();
            return success;
        }
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
