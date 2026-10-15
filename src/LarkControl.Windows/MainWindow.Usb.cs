using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly bool automaticReceiverManagement;
    private readonly DispatcherTimer receiverTimer;
    private readonly SemaphoreSlim transportOpenGate = new(1, 1);
    private Task receiverScanTask = Task.CompletedTask;
    private Task receiverSessionTask = Task.CompletedTask;
    private ReceiverInfo? sessionReceiver;
    private string? failedReceiverPath;
    private string? lastReceiverError;
    private DateTimeOffset nextReceiverRetry;
    private int receiverFailures;
    private bool receiverRetryRequested;
    private bool receiverScanFailed;
    private bool updatingReceiverSelection;

    internal string? ActiveReceiverPath => IsConnected ? sessionReceiver?.Path : null;
    internal Task ReceiverSessionTask => receiverSessionTask;
    private ReceiverInfo? SelectedReceiver => ReceiverList?.SelectedItem as ReceiverInfo;

    internal Task RefreshUsbAsync(bool retryFailed = false)
    {
        if (closed) return Task.CompletedTask;
        receiverRetryRequested |= retryFailed;
        if (!receiverScanTask.IsCompleted) return receiverScanTask;
        receiverScanTask = RefreshUsbCoreAsync();
        return receiverScanTask;
    }

    private async Task RefreshUsbCoreAsync()
    {
        try
        {
            IReadOnlyList<ReceiverInfo> discovered = await Task.Run(discover);
            if (closed) return;
            ReceiverInfo[] snapshot = discovered.ToArray();
            if (snapshot.Any(receiver => receiver is null || string.IsNullOrWhiteSpace(receiver.Path)))
                throw new InvalidDataException("Discovery returned an invalid receiver identity.");
            snapshot = snapshot.DistinctBy(receiver => receiver.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            bool hadFailure = receiverScanFailed;
            receiverScanFailed = false;
            bool retry = receiverRetryRequested;
            receiverRetryRequested = false;
            string? selectedPath = SelectedReceiver?.Path;
            bool selectionRemoved = selectedPath is not null && !snapshot.Any(receiver => SameReceiver(receiver.Path, selectedPath));
            bool listHadFocus = ReceiverList.IsKeyboardFocusWithin;
            var previousFocus = CaptureOperationFocus();
            bool sessionRemoved = sessionReceiver is not null && !snapshot.Any(receiver => SameReceiver(receiver.Path, sessionReceiver.Path));
            if (sessionRemoved) EndSession();
            if (failedReceiverPath is not null && !snapshot.Any(receiver => SameReceiver(receiver.Path, failedReceiverPath))) ClearReceiverFailure();
            bool changed = ReconcileReceiverList(snapshot, selectedPath);
            if (changed || hadFailure || sessionRemoved) UpdateView();
            if (changed || hadFailure)
                SetResult(sessionRemoved ? Localization.Text("Main.Usb.Removed") : GetUsbConnectionText());
            if (selectionRemoved && listHadFocus && IsActive) ConnectionReadout.Focus();
            else if (sessionRemoved) RestoreOperationFocus(previousFocus);
            SynchronizeReceiverSession(retry);
        }
        catch (Exception exception)
        {
            if (closed) return;
            Log(Localization.Text("Main.Log.SearchError", exception.GetType().Name, exception.Message));
            if (!receiverScanFailed) SetResult(Localization.Text("Main.Usb.ScanFailed"), true);
            receiverScanFailed = true;
        }
        finally { CommandManager.InvalidateRequerySuggested(); }
    }

    private bool ReconcileReceiverList(IReadOnlyList<ReceiverInfo> snapshot, string? selectedPath)
    {
        bool changed = false;
        updatingReceiverSelection = true;
        try
        {
            for (int index = Receivers.Count - 1; index >= 0; index--)
            {
                if (snapshot.Any(receiver => SameReceiver(receiver.Path, Receivers[index].Path))) continue;
                Receivers.RemoveAt(index);
                changed = true;
            }
            foreach (ReceiverInfo receiver in snapshot)
            {
                if (Receivers.Any(existing => SameReceiver(existing.Path, receiver.Path))) continue;
                string name = receiver.Name;
                int suffix = 2;
                while (Receivers.Any(existing => existing.Name == name))
                    name = Localization.Text("Main.Usb.DistinctName", receiver.Name, suffix++);
                Receivers.Add(receiver with { Name = name });
                changed = true;
            }
            ReceiverInfo? selection = Receivers.FirstOrDefault(receiver => SameReceiver(receiver.Path, selectedPath));
            if (selection is null && Receivers.Count == 1) selection = Receivers[0];
            if (!ReferenceEquals(ReceiverList.SelectedItem, selection)) ReceiverList.SelectedItem = selection;
        }
        finally { updatingReceiverSelection = false; }
        return changed;
    }

    private void SynchronizeReceiverSession(bool retryFailed = false)
    {
        if (closed || !automaticReceiverManagement) return;
        ReceiverInfo? selected = SelectedReceiver;
        if (sessionCancellation is not null && SameReceiver(sessionReceiver?.Path, selected?.Path)) return;
        if (sessionCancellation is not null) EndSession();
        if (selected is null) { UpdateView(); return; }
        bool failed = SameReceiver(failedReceiverPath, selected.Path);
        if (failed && !retryFailed && DateTimeOffset.UtcNow < nextReceiverRetry) return;
        if (retryFailed || !failed) ClearReceiverFailure();
        receiverSessionTask = ConnectAsync(reportProgress: !failed || retryFailed);
    }

    private void OnReceiverSelected(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (updatingReceiverSelection || closed) return;
        ClearReceiverFailure();
        SynchronizeReceiverSession();
        UpdateView();
    }

    private void UpdateReceiverActions()
    {
        if (ReceiverList is null) return;
        ReceiverList.IsEnabled = !closed;
        ActiveReceiverReadout.Text = IsConnected && sessionReceiver is not null
            ? sessionReceiver.Name : GetUsbConnectionText();
        CommandManager.InvalidateRequerySuggested();
    }

    private string GetUsbConnectionText()
    {
        if (IsConnected && sessionReceiver is not null)
            return Localization.Text("Main.Usb.Ready", sessionReceiver.Name,
                currentStatus!.Transmitters.Count(transmitter => transmitter.Connected), currentStatus.Transmitters.Count);
        if (failedReceiverPath is not null && SelectedReceiver is { } failed && SameReceiver(failed.Path, failedReceiverPath))
            return Localization.Text(nextReceiverRetry == DateTimeOffset.MaxValue ? "Main.Usb.Paused" : "Main.Usb.Unavailable", failed.Name);
        if (sessionReceiver is not null && sessionCancellation is not null)
            return Localization.Text("Main.Usb.Reading", sessionReceiver.Name);
        return Receivers.Count switch
        {
            0 => Localization.Text("Main.Usb.None"),
            1 => Localization.Text("Main.Usb.Waiting"),
            _ => Localization.Text("Main.Usb.Choose", Receivers.Count)
        };
    }

    private void ClearReceiverFailure()
    {
        failedReceiverPath = null;
        lastReceiverError = null;
        receiverFailures = 0;
        nextReceiverRetry = DateTimeOffset.MinValue;
    }

    private static bool SameReceiver(string? first, string? second) =>
        first is not null && second is not null && StringComparer.OrdinalIgnoreCase.Equals(first, second);
}
