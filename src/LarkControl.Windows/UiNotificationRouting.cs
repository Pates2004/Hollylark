using System.Runtime.InteropServices;
using System.Windows;

namespace LarkControl.Windows;

internal static class UiNotificationRouting
{
    internal static void Announce(IScreenReaderSpeech? speech, string message, Action fallback, Action<Exception> reportError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            if (speech?.TrySpeak(message) == true) return;
        }
        catch (Exception exception) when (IsSpeechFailure(exception)) { reportError(exception); }
        fallback();
    }

    internal static bool IsSpeechFailure(Exception exception) => exception is
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException or COMException;

    internal static Window SelectStateTarget(Window mainWindow, IEnumerable<AuxiliaryWindow> auxiliaryWindows,
        Func<Window, bool>? isActive = null) => SelectTarget(mainWindow, auxiliaryWindows, isActive) ?? mainWindow;

    internal static Window? SelectTarget(Window mainWindow, IEnumerable<AuxiliaryWindow> auxiliaryWindows,
        Func<Window, bool>? isActive = null)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(auxiliaryWindows);
        isActive ??= window => window.IsActive;
        AuxiliaryWindow? activeSection = auxiliaryWindows.FirstOrDefault(window => isActive(window));
        return activeSection ?? (isActive(mainWindow) ? mainWindow : null);
    }
}
