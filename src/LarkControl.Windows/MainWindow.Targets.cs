using System.IO;
using System.Windows;
using System.Windows.Controls;
using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly TransmitterViewModel allMicrophones = new(-1);
    private bool updatingTransmitterTargets;
    private bool targetInitializedForSession;
    private long controlContextGeneration;
    private int[]? lastConnectedTargets;

    private void UpdateTransmitterTargets()
    {
        updatingTransmitterTargets = true;
        try
        {
            int[] connected = currentStatus is null ? [] : ConnectedMicrophoneIndices(currentStatus);
            if (lastConnectedTargets is not null && !lastConnectedTargets.SequenceEqual(connected))
            {
                bool hadDrafts = AllSettings.Any(setting => setting.IsDirty);
                controlContextGeneration++;
                DiscardControlTargetDrafts();
                if (hadDrafts) SetResult(Localization.Text("Target.DraftsDiscarded"), true);
            }
            lastConnectedTargets = currentStatus is null ? null : connected;
            foreach (TransmitterViewModel removed in TransmitterTargets.Where(target => !target.IsGroup && !Transmitters.Contains(target)).ToArray())
                TransmitterTargets.Remove(removed);
            for (int index = 0; index < Transmitters.Count; index++)
                if (!TransmitterTargets.Contains(Transmitters[index])) TransmitterTargets.Insert(index, Transmitters[index]);
            if (!TransmitterTargets.Contains(allMicrophones)) TransmitterTargets.Add(allMicrophones);
            allMicrophones.UpdateGroup(currentStatus, versions.GetValueOrDefault(0), busy);
            if (currentStatus is not null && !targetInitializedForSession)
            {
                TransmitterList.SelectedItem = connected.Length == 1 ? Transmitters[connected[0]] : allMicrophones;
                TransmitterList.ScrollIntoView(TransmitterList.SelectedItem);
                targetInitializedForSession = true;
                controlContextGeneration++;
                DiscardControlTargetDrafts();
            }
            if (TransmitterList.SelectedItem is null)
            {
                TransmitterList.SelectedItem = allMicrophones;
                TransmitterList.ScrollIntoView(allMicrophones);
                controlContextGeneration++;
                DiscardControlTargetDrafts();
            }
            TransmitterList.IsEnabled = !busy;
        }
        finally { updatingTransmitterTargets = false; }
    }

    private void OnTransmitterSelected(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (updatingTransmitterTargets || closed || !ReferenceEquals(eventArgs.OriginalSource, TransmitterList)) return;
        controlContextGeneration++;
        DiscardControlTargetDrafts();
        foreach (SettingRowViewModel setting in AllSettings) setting.Update(currentStatus, busy, SharedControlUnavailableReason());
        settingsWindow?.RefreshReceiverAvailability();
    }

    private void DiscardControlTargetDrafts()
    {
        pendingAutomaticChanges.Clear();
        autoApplyTimer?.Stop();
        foreach (SettingRowViewModel setting in AllSettings) setting.DiscardDraft();
    }

    private static int[] ConnectedMicrophoneIndices(A1Status status) => Enumerable.Range(0, status.Transmitters.Count)
        .Where(index => status.Transmitters[index].Connected).ToArray();

    private string? SharedControlUnavailableReason()
    {
        if (currentStatus is null || !currentStatus.Transmitters.Any(transmitter => transmitter.Connected))
            return Localization.Text("Main.Change.TransmitterRequired");
        int[] connected = ConnectedMicrophoneIndices(currentStatus);
        if (TransmitterList.SelectedItem is not TransmitterViewModel target || target.IsGroup)
            return connected.Length >= 2 ? null : Localization.Text("Target.GroupUnavailable");
        if (!connected.Contains(target.Index)) return Localization.Text("Target.SelectedUnavailable");
        return connected.Length == 1 ? null : Localization.Text("Target.SharedParameters");
    }

    private bool TryGetSharedControlScope(out MicrophoneControlScope? scope)
    {
        scope = null;
        string? unavailable = SharedControlUnavailableReason();
        if (unavailable is not null)
        {
            SetResult(unavailable, true);
            return false;
        }
        scope = TransmitterList.SelectedItem is TransmitterViewModel { IsGroup: false } target
            ? MicrophoneControlScope.Individual(target.Index)
            : MicrophoneControlScope.All(ConnectedMicrophoneIndices(currentStatus!));
        return true;
    }

    private static string ScopeFailureMessage(MicrophoneScopeException exception)
    {
        string reason = !exception.Status.Transmitters.Any(transmitter => transmitter.Connected)
            ? Localization.Text("Main.Change.TransmitterRequired")
            : Localization.Text(exception.Reason switch
            {
                MicrophoneScopeReason.SelectedUnavailable => "Target.SelectedUnavailable",
                MicrophoneScopeReason.SharedParameters => "Target.SharedParameters",
                MicrophoneScopeReason.GroupUnavailable => "Target.GroupUnavailable",
                _ => "Target.GroupChanged"
            });
        return exception.WriteAttempted ? reason + " " + Localization.Text("Target.WriteUnconfirmed") : reason;
    }

    internal Task ToggleAllMicrophonesMuteAsync()
    {
        if (closed || busy || !RequireConnectedMicrophone()) return Task.CompletedTask;
        int[] connected = ConnectedMicrophoneIndices(currentStatus!);
        if (connected.Length < 2)
        {
            SetResult(Localization.Text("Target.GroupUnavailable"), true);
            return Task.CompletedTask;
        }
        if (!allMicrophones.CanMute)
        {
            SetResult(Localization.Text("Main.Mute.Unavailable"), true);
            return Task.CompletedTask;
        }
        MicrophoneControlScope scope = MicrophoneControlScope.All(connected);
        return RunSessionOperationAsync(Localization.Text("Target.All.Pending"), async (session, _, token) =>
        {
            A1Status status = await session.ReadStatusAsync(token);
            scope.Validate(status, SettingChange.Mute(connected[0], true));
            bool muted = !connected.All(index => status.Transmitters[index].Muted);
            foreach (int index in connected)
            {
                SettingChange change = SettingChange.Mute(index, muted);
                scope.Validate(status, change);
                if (status.Transmitters[index].Muted != muted)
                {
                    A1Status checkpoint = status;
                    status = await session.ApplyAsync(change, token, scope, live => connected.All(activeIndex =>
                        live.Transmitters[activeIndex].Muted == checkpoint.Transmitters[activeIndex].Muted));
                }
            }
            status = await session.ReadStatusAsync(token);
            scope.Validate(status, SettingChange.Mute(connected[0], muted));
            if (connected.Any(index => status.Transmitters[index].Muted != muted))
                throw new InvalidDataException("The device did not confirm the requested mute state for all selected microphones.");
            return status;
        }, Localization.Text("Main.Hotkey.MuteConfirmed"),
            failureWarning: Localization.Text("Target.All.PartialFailure"),
            observeSettings: true,
            successMessage: status => Localization.Text(connected.All(index => status.Transmitters[index].Muted)
                ? "Target.All.MutedConfirmed" : "Target.All.UnmutedConfirmed"));
    }
}

public sealed partial class TransmitterViewModel
{
    internal void UpdateGroup(A1Status? status, Version? receiverVersion, bool busy)
    {
        TransmitterState[] active = status?.Transmitters.Where(transmitter => transmitter.Connected).ToArray() ?? [];
        bool supportsMute = receiverVersion is not null && receiverVersion >= MinimumMuteVersion;
        SetField(ref connected, active.Length >= 2, nameof(IsConnected));
        SetField(ref muted, active.Length > 0 && active.All(transmitter => transmitter.Muted), nameof(IsMuted));
        SetField(ref canMute, connected && supportsMute && !busy, nameof(CanMute));
        SetField(ref canRequestMute, !busy, nameof(CanRequestMute));
        SetField(ref actionText, ActionName, nameof(ActionText));
        RaisePropertyChanged(nameof(ActionName));
        string state = status is null ? Localization.Text("Transmitter.NoReadout")
            : active.Length == 0 ? Localization.Text("Transmitter.Disconnected")
            : Localization.Text(muted ? "Transmitter.Muted" : active.Any(transmitter => transmitter.Muted)
                ? "Target.All.Mixed" : "Transmitter.Active");
        SetField(ref summary, Localization.Text("Target.All.Summary", active.Length, state), nameof(Summary));
    }
}
