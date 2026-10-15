using System.Windows;

namespace LarkControl.Windows;

internal static class UiNotificationRouting
{
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
