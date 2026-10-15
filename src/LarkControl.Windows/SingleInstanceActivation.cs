using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LarkControl.Windows;

internal static class SingleInstanceActivation
{
    private const uint EnabledPopup = 6;

    internal static void Queue(Application application)
    {
        Dispatcher dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try
        {
            _ = dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished) Restore(application);
            }));
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
    }

    private static void Restore(Application application)
    {
        Window[] windows = application.Windows.OfType<Window>().ToArray();
        if (TryActivateNativePopup(windows)) return;
        var main = application.MainWindow as MainWindow;
        Window? auxiliary = windows
            .Where(window => window != main && window.IsVisible && window.IsEnabled)
            .OrderByDescending(window => window.IsActive).FirstOrDefault();
        if (auxiliary is null)
        {
            main?.RestoreFromTray();
            return;
        }
        if (auxiliary.WindowState == WindowState.Minimized) auxiliary.WindowState = WindowState.Normal;
        auxiliary.Activate();
    }

    private static bool TryActivateNativePopup(Window[] windows)
    {
        foreach (Window owner in windows.Where(window => window.IsVisible).OrderByDescending(window => window.IsActive).Take(16))
        {
            IntPtr handle = new WindowInteropHelper(owner).Handle;
            if (handle == IntPtr.Zero || IsWindowEnabled(handle)) continue;
            IntPtr popup = GetWindow(handle, EnabledPopup);
            if (popup == IntPtr.Zero || popup == handle || !IsWindowVisible(popup) || !IsWindowEnabled(popup)) continue;
            _ = GetWindowThreadProcessId(popup, out uint processId);
            if (processId != (uint)Environment.ProcessId
                || windows.Any(window => new WindowInteropHelper(window).Handle == popup)) continue;
            _ = SetForegroundWindow(popup);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
