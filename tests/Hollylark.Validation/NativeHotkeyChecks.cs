using System.Windows.Interop;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyNativeHotkeyRegistrationChecks()
    {
        Console.WriteLine("REAL WIN32 hotkey registration checks: run only through --native-hotkeys-only on a private desktop. No fake native API, input injection or device I/O.");
        using HwndSource firstSource = CreateNativeHotkeySource("first");
        using HwndSource secondSource = CreateNativeHotkeySource("second");
        int callbacks = 0;
        using var first = new GlobalHotkeys(firstSource, _ => callbacks++, canDispatch: () => false);
        using var second = new GlobalHotkeys(secondSource, _ => callbacks++, canDispatch: () => false);
        Check(firstSource.Handle != IntPtr.Zero && secondSource.Handle != IntPtr.Zero
            && firstSource.Handle != secondSource.Handle,
            "Real Win32 hotkey checks own two distinct hidden message-only windows");
        Check(!first.Enabled && !second.Enabled && first.ActiveBindings.Count == 0 && second.ActiveBindings.Count == 0,
            "Real native hotkey services start without registrations");

        HotkeyBinding[]? selected = null;
        var occupied = new List<string>();
        foreach (string key in new[] { "F11", "F10", "F9", "F8", "F7" })
        {
            string gesture = "Ctrl+Alt+Shift+" + key;
            HotkeyBinding[] candidate = Enum.GetValues<HotkeyAction>()
                .Select(action => new HotkeyBinding(action, action == HotkeyAction.ShowWindow ? gesture : "")).ToArray();
            HotkeyOperationResult result = first.Configure(true, candidate);
            if (result.Succeeded)
            {
                selected = candidate;
                break;
            }
            Check(result.Error?.Code == HotkeyErrorCode.RegistrationFailed && result.Error.Win32Error == 1409
                && result.Error.Action == HotkeyAction.ShowWindow && result.Error.Gesture == gesture
                && result.RestorationSucceeded && first.IsConsistent && !first.Enabled && first.ActiveBindings.Count == 0,
                $"Initial native registration failure for {gesture} is an occupied shortcut with clean rollback: {DescribeNativeHotkeyResult(result)}");
            occupied.Add(gesture);
            Console.WriteLine($"Real Win32 candidate {gesture} is occupied; trying the next bounded candidate without replacing its owner.");
        }
        if (selected is null)
            throw new InvalidOperationException("No free native hotkey candidate among: " + string.Join(", ", occupied));

        HotkeyBinding assigned = selected.Single(binding => binding.Gesture.Length > 0);
        Check(first.Enabled && !first.IsSuspended && first.IsConsistent
            && first.ActiveBindings.SequenceEqual(new[] { assigned }),
            "Real RegisterHotKey acquires exactly one assigned shortcut: " + assigned.Gesture);
        HotkeyOperationResult conflict = second.Configure(true, selected);
        Check(!conflict.Succeeded && conflict.Error?.Code == HotkeyErrorCode.RegistrationFailed
            && conflict.Error.Win32Error == 1409 && conflict.Error.Action == assigned.Action
            && conflict.Error.Gesture == assigned.Gesture && conflict.RestorationSucceeded
            && !second.Enabled && second.IsConsistent && second.ActiveBindings.Count == 0
            && first.Enabled && first.IsConsistent && first.ActiveBindings.SequenceEqual(new[] { assigned }),
            "A second owned HWND cannot register the first HWND's shortcut and rolls back cleanly: "
                + DescribeNativeHotkeyResult(conflict));

        HotkeyOperationResult suspended = first.Suspend();
        Check(suspended.Succeeded && first.Enabled && first.IsSuspended && first.IsConsistent
            && first.ActiveBindings.Count == 0,
            "Real UnregisterHotKey releases the first owner's shortcut during suspension: "
                + DescribeNativeHotkeyResult(suspended));
        HotkeyOperationResult acquired = second.Configure(true, selected);
        Check(acquired.Succeeded && second.Enabled && second.IsConsistent
            && second.ActiveBindings.SequenceEqual(new[] { assigned }),
            "The second owned HWND acquires the shortcut released by suspension: " + DescribeNativeHotkeyResult(acquired));

        second.Dispose();
        Check(second.LastResult.Succeeded && second.IsDisposed && second.IsConsistent && second.ActiveBindings.Count == 0,
            "Disposing the second native service unregisters its shortcut: " + DescribeNativeHotkeyResult(second.LastResult));
        HotkeyOperationResult resumed = first.Resume();
        Check(resumed.Succeeded && first.Enabled && !first.IsSuspended && first.IsConsistent
            && first.ActiveBindings.SequenceEqual(new[] { assigned }),
            "The first owned HWND reacquires the shortcut after the second service is disposed: "
                + DescribeNativeHotkeyResult(resumed));
        first.Dispose();
        Check(first.LastResult.Succeeded && first.IsDisposed && first.IsConsistent && first.ActiveBindings.Count == 0,
            "Final native service disposal reports successful cleanup: " + DescribeNativeHotkeyResult(first.LastResult));
        Check(callbacks == 0, "Real registration lifecycle checks execute no hotkey actions or injected keystrokes");
    }

    private static HwndSource CreateNativeHotkeySource(string role) => new(new HwndSourceParameters(
        "Hollylark.NativeHotkeyChecks." + role + "." + Guid.NewGuid().ToString("N"))
    {
        ParentWindow = new IntPtr(-3),
        WindowStyle = 0,
        Width = 1,
        Height = 1
    });

    private static string DescribeNativeHotkeyResult(HotkeyOperationResult result) => result.Succeeded
        ? "success"
        : $"{result.Error?.Code}, action={result.Error?.Action}, gesture={result.Error?.Gesture}, "
            + $"Win32={result.Error?.Win32Error}, restorationFailures={result.RestorationErrors.Count}";
}
