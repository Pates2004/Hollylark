using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly DeviceSettingsRetention retention;
    private readonly DeviceSettingsRetentionStore retentionStore;
    private bool retentionFailureReported;
    private RetentionRestoreRequest? deferredRetention;

    private void ObserveReadOnlySettings(A1Status status)
    {
        if (!preferences.PreserveVolatileSettings || sessionReceiver is null || !RetentionStorageAvailable()) return;
        RetentionRestoreRequest? request = retention.Observe(sessionReceiver.Path, status);
        if (request is not null) deferredRetention = request;
        RetentionStorageAvailable();
    }

    private bool RetentionStorageAvailable()
    {
        if (retention.LastError is null) return true;
        if (!retentionFailureReported)
        {
            retentionFailureReported = true;
            string message = Localization.Text("Retention.StoreError", retentionStore.FilePath);
            SetResult(message, true);
            AnnounceBackgroundNotification(message, "Hollylark.RetentionError");
        }
        return false;
    }

    private void RememberConfirmedSettings(A1Status status)
    {
        if (!preferences.PreserveVolatileSettings || sessionReceiver is null || !RetentionStorageAvailable()) return;
        RetentionRestoreRequest? request = retention.Observe(sessionReceiver.Path, status);
        if (request is not null) retention.Abandon(request, status);
        RetentionStorageAvailable();
    }

    private async Task<bool> AcceptObservedStateAsync(A1Status status, A1Controller session, long expectedGeneration,
        CancellationToken token)
    {
        if (!IsSession(session, expectedGeneration) || sessionReceiver is null) return false;
        RetentionRestoreRequest? request = preferences.PreserveVolatileSettings && RetentionStorageAvailable()
            ? retention.Observe(sessionReceiver.Path, status) : null;
        if (request is null && preferences.PreserveVolatileSettings && deferredRetention is { } deferred
            && SameReceiver(deferred.ReceiverPath, sessionReceiver.Path) && retention.IsCurrent(deferred)) request = deferred;
        deferredRetention = null;
        bool hadDrafts = AllSettings.Any(setting => setting.IsDirty);
        ShowState(status, rememberSettings: false);
        if (request is null) { RetentionStorageAvailable(); return false; }
        if (hadDrafts || settingsWindow is not null || !RetentionStorageAvailable()
            || request.ObservedSettings != RetainedSettings.FromStatus(status))
        {
            retention.Abandon(request, status);
            return false;
        }
        bool wasBusy = busy;
        busy = true;
        UpdateView();
        SetResult(Localization.Text("Retention.Restoring"));
        int[] indices = request.ConnectedIndices.ToArray();
        MicrophoneControlScope scope = indices.Length == 1
            ? MicrophoneControlScope.Individual(indices[0]) : MicrophoneControlScope.All(indices);
        bool restored = false;
        bool fourTransmitters = A1DeviceIdentity.IsFourTransmitterFirmware(versions.GetValueOrDefault(0));
        try
        {
            foreach (SettingChange change in request.DesiredSettings.CreateChanges(status))
            {
                if (!IsSession(session, expectedGeneration) || !retention.IsCurrent(request)) return true;
                if (!CanRestoreParameter(change.Command, status, fourTransmitters)) continue;
                A1Status checkpoint = status;
                status = await session.ApplyAsync(change, token, scope, live =>
                    RetainedSettings.FromStatus(live) == RetainedSettings.FromStatus(checkpoint)
                    && CanRestoreParameter(change.Command, live, fourTransmitters));
                if (!SameOtherParameters(checkpoint, status, change.Command))
                    throw new DeviceStateChangedException(status);
            }
            status = await session.ReadStatusAsync(token);
            if (!IsSession(session, expectedGeneration) || !retention.IsCurrent(request)) return true;
            scope.Validate(status, SettingChange.Gain(status.Gain));
            restored = request.DesiredSettings.CreateChanges(status).Count == 0;
            retention.Complete(request, status);
        }
        catch (MicrophoneScopeException exception) { status = exception.Status; }
        catch (DeviceWriteSafetyException exception) { status = exception.Status; }
        catch (DeviceStateChangedException exception) { status = exception.Status; }
        catch (Exception exception)
        {
            if (IsSession(session, expectedGeneration))
            {
                HandleSessionError(exception);
                string warning = Localization.Text("Retention.Interrupted");
                SetResult(ResultText + " " + warning, true);
                AnnounceBackgroundNotification(warning, "Hollylark.RetentionError");
            }
        }
        finally
        {
            if (retention.IsCurrent(request)) retention.Abandon(request, IsSession(session, expectedGeneration) ? status : null);
            if (IsSession(session, expectedGeneration))
            {
                ShowState(status, rememberSettings: false);
                busy = wasBusy;
                UpdateView();
                string message = Localization.Text(restored ? "Retention.Restored" : "Retention.Interrupted");
                SetResult(message, !restored);
                if (!restored || preferences.AnnounceStateChanges)
                    AnnounceBackgroundNotification(message, "Hollylark.Retention");
                RetentionStorageAvailable();
            }
        }
        return true;
    }

    private static bool CanRestoreParameter(A1Command command, A1Status status, bool fourTransmitters) => command switch
    {
        A1Command.SetNoise => status.Transmitters.Any(transmitter => transmitter.Connected && !transmitter.Muted),
        A1Command.SetIdentification => status.IdentificationEnabled.HasValue && status.Transmitters.Count(transmitter => transmitter.Connected)
            >= (fourTransmitters ? 1 : 2),
        _ => true
    };

    private static bool SameOtherParameters(A1Status before, A1Status after, A1Command changed) =>
        (changed == A1Command.SetGain || before.Gain == after.Gain)
        && (changed == A1Command.SetChannelMode || before.ChannelMode == after.ChannelMode)
        && (changed == A1Command.SetNoise || before.NoiseEnabled == after.NoiseEnabled && before.NoiseLevel == after.NoiseLevel)
        && (changed == A1Command.SetEqualizer || before.Equalizer == after.Equalizer)
        && (changed == A1Command.SetReverb || before.ReverbEnabled == after.ReverbEnabled && before.ReverbLevel == after.ReverbLevel)
        && (changed == A1Command.SetLight || before.LightsEnabled == after.LightsEnabled)
        && (changed == A1Command.SetAutoOff || before.AutoOff == after.AutoOff)
        && (changed == A1Command.SetIdentification || before.IdentificationEnabled == after.IdentificationEnabled);
}
