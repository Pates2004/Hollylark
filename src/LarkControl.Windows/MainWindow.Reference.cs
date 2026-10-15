using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LarkControl.Windows;

public partial class MainWindow
{
    public static readonly RoutedUICommand CancelOperationCommand = new(Localization.Text("Main.Command.Cancel"), nameof(CancelOperationCommand), typeof(MainWindow));
    private DateTimeOffset? lastConfirmedRead;

    private static void UpdateCommandText()
    {
        ScanCommand.Text = Localization.Text("Main.Command.Search");
        RefreshCommand.Text = Localization.Text("Main.Command.Refresh");
        CancelOperationCommand.Text = Localization.Text("Main.Command.Cancel");
    }

    private void OnReceiverListKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (updatingReceiverSelection || sender is not ListBox list) return;
        if (eventArgs.NewFocus is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == list)
        {
            item.IsSelected = true;
            return;
        }
        if (eventArgs.NewFocus != list || !list.HasItems) return;
        object entry = list.SelectedItem ?? list.Items[0];
        list.ScrollIntoView(entry);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem target) target.Focus();
    }

    private void OnReadoutFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (sender is FrameworkElement element) element.BringIntoView();
    }


    internal void CancelCurrentOperation()
    {
        if (closed || !busy || sessionCancellation is null) return;
        Disconnect();
    }

    internal void CopyManualAddress(Action<Uri>? copyAddress = null)
    {
        SupportLinkResult result = OfficialSupportLinks.CopyAddress(OfficialSupportLinks.Manual, copyAddress);
        SetResult(result.Message, result.IsError);
    }

    private (UIElement? Element, long Interaction) CaptureOperationFocus() =>
        (IsActive && Keyboard.FocusedElement is UIElement element && Window.GetWindow(element) == this
            ? element : null, focusInteraction);

    private void RestoreOperationFocus((UIElement? Element, long Interaction) previous)
    {
        if (previous.Element is null) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (closed || busy || !IsActive || previous.Interaction != focusInteraction) return;
            if (Keyboard.FocusedElement is UIElement current && current != this && current != previous.Element
                && current.IsVisible && current.IsEnabled && current.Focusable && !current.IsAncestorOf(previous.Element)) return;
            UIElement target = previous.Element is { IsVisible: true, IsEnabled: true, Focusable: true }
                ? previous.Element : ResultReadout.IsVisible ? ResultReadout : ConnectionReadout;
            target.Focus();
        }));
    }

    internal Window GetDialogOwner() => OwnedWindows.OfType<AuxiliaryWindow>().FirstOrDefault(window => window.IsActive) ?? (Window)this;

    private static void RestoreAndActivate(AuxiliaryWindow window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnCancelOperation(object sender, ExecutedRoutedEventArgs eventArgs) => CancelCurrentOperation();
    private void CanCancelOperation(object sender, CanExecuteRoutedEventArgs eventArgs) => eventArgs.CanExecute = !closed && busy;
    private void OnCopyManualAddress(object sender, RoutedEventArgs eventArgs) => CopyManualAddress();
}
