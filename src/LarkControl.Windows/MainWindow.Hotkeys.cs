using System.Windows;
using System.Windows.Interop;
using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly IHotkeyNativeApi? hotkeyNativeApi;
    private GlobalHotkeys? globalHotkeys;
    private bool shortcutDialogActive;
    private bool hotkeyConfigurationValid = true;

    private void InitializeGlobalHotkeys(object? sender, EventArgs eventArgs)
    {
        if (closed || globalHotkeys is not null) return;
        HwndSource? source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source is null) return;
        globalHotkeys = new GlobalHotkeys(source, action => _ = ExecuteHotkeyAsync(action),
            () => !closed && settingsWindow is null && !exitRequested && !shortcutDialogActive && hotkeyConfigurationValid, hotkeyNativeApi);
        HotkeyOperationResult result = globalHotkeys.Configure(preferences.GlobalHotkeysEnabled, preferences.Hotkeys);
        hotkeyConfigurationValid = result.Succeeded;
        if (!result.Succeeded) ReportHotkeyFailure(result);
    }

    private static string HotkeyFailureMessage(HotkeyOperationResult result)
    {
        HotkeyError? error = result.Error;
        string message = error?.Action is { } action
            ? Localization.Text("Hotkeys.Conflict", SettingsWindow.HotkeyActionName(action), error.Win32Error ?? 0)
            : Localization.Text("Hotkeys.InvalidList");
        if (!result.RestorationSucceeded) message += " " + Localization.Text("Hotkeys.RestoreFailed");
        return message;
    }

    private void ReportHotkeyFailure(HotkeyOperationResult result)
    {
        string message = HotkeyFailureMessage(result);
        SetResult(message, true, announce: false);
        AnnounceBackgroundNotification(message, "Hollylark.HotkeysError");
    }

    private string? ConfigureApplicationHotkeys(UiPreferences candidate)
    {
        if (globalHotkeys is null) return candidate.GlobalHotkeysEnabled ? Localization.Text("Hotkeys.Busy") : null;
        hotkeyConfigurationValid = false;
        HotkeyOperationResult result = globalHotkeys.ConfigureAndResume(candidate.GlobalHotkeysEnabled, candidate.Hotkeys);
        return result.Succeeded ? null : HotkeyFailureMessage(result);
    }

    private string RestoreApplicationHotkeys()
    {
        if (globalHotkeys is null) return string.Empty;
        if (settingsWindow is not null && !globalHotkeys.IsSuspended)
        {
            HotkeyOperationResult suspended = globalHotkeys.Suspend();
            settingsWindow.SetHotkeyCaptureAvailable(suspended.Succeeded);
            if (!suspended.Succeeded) return " " + HotkeyFailureMessage(suspended);
        }
        HotkeyOperationResult restored = globalHotkeys.Configure(preferences.GlobalHotkeysEnabled, preferences.Hotkeys);
        hotkeyConfigurationValid = restored.Succeeded;
        settingsWindow?.SetHotkeyCaptureAvailable(restored.Succeeded && globalHotkeys.ActiveBindings.Count == 0);
        if (!restored.Succeeded)
        {
            if (!globalHotkeys.IsSuspended) globalHotkeys.Suspend();
            return " " + HotkeyFailureMessage(restored);
        }
        return string.Empty;
    }

    private void ResumeApplicationHotkeys()
    {
        if (closed || globalHotkeys is null) return;
        HotkeyOperationResult result = globalHotkeys.ConfigureAndResume(preferences.GlobalHotkeysEnabled, preferences.Hotkeys);
        hotkeyConfigurationValid = result.Succeeded;
        if (!result.Succeeded && !globalHotkeys.IsSuspended) globalHotkeys.Suspend();
        if (!result.Succeeded) ReportHotkeyFailure(result);
    }

    internal async Task ExecuteHotkeyAsync(HotkeyAction action)
    {
        if (closed || !preferences.GlobalHotkeysEnabled) return;
        if (action == HotkeyAction.ShowWindow) { RestoreFromTray(); return; }
        if (busy || settingsWindow is not null || exitRequested || shortcutDialogActive)
        {
            HotkeyFeedback(Localization.Text("Hotkeys.Busy"), true);
            return;
        }
        if (AllSettings.Any(setting => setting.IsDirty))
        {
            HotkeyFeedback(Localization.Text("Hotkeys.Drafts"), true);
            return;
        }
        if (action == HotkeyAction.ReadStatus)
        {
            await RefreshAsync();
            HotkeyFeedback(ResultText, resultIsImportant);
            return;
        }
        if (!RequireConnectedMicrophone()) { HotkeyFeedback(ResultText, true); return; }
        if (action is HotkeyAction.ToggleSelectedMute or HotkeyAction.ToggleAllMute)
        {
            TransmitterViewModel? selected = TransmitterList.SelectedItem as TransmitterViewModel;
            await ToggleHotkeyMuteAsync(action == HotkeyAction.ToggleAllMute);
            string message = ResultText;
            if (!resultIsImportant && currentStatus is not null)
            {
                if (action == HotkeyAction.ToggleAllMute || selected?.IsGroup == true)
                    message = Localization.Text(currentStatus.Transmitters.Where(transmitter => transmitter.Connected).All(transmitter => transmitter.Muted)
                        ? "Target.All.MutedConfirmed" : "Target.All.UnmutedConfirmed");
                else if (selected is not null)
                    message = Localization.Text(currentStatus.Transmitters[selected.Index].Muted ? "Main.Mute.Confirmed" : "Main.Mute.Unmuted", selected.Index + 1);
            }
            HotkeyFeedback(message, resultIsImportant);
            return;
        }
        if (!TryGetSharedControlScope(out MicrophoneControlScope? scope)) { HotkeyFeedback(ResultText, true); return; }
        string id = action switch
        {
            HotkeyAction.IncreaseGain or HotkeyAction.DecreaseGain => "Gain",
            HotkeyAction.CycleNoise => "Noise", HotkeyAction.CycleEqualizer => "Equalizer",
            HotkeyAction.CycleReverb => "Reverb", HotkeyAction.CycleLights => "Lights",
            HotkeyAction.CycleAutoOff => "AutoOff", HotkeyAction.CycleIdentification => "Identification",
            HotkeyAction.CycleChannelMode => "ChannelMode", _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        SettingRowViewModel row = AllSettings.Single(setting => setting.Id == id);
        if (!RequestSettingEdit(row)) { HotkeyFeedback(ResultText, true); return; }
        bool fourTransmitters = A1DeviceIdentity.IsFourTransmitterFirmware(versions.GetValueOrDefault(0));
        await RunSessionOperationAsync(Localization.Text("Main.Apply.Pending", row.DisplayName), async (session, _, token) =>
        {
            A1Status status = await session.ReadStatusAsync(token);
            SettingChange change = HotkeyChange(action, status);
            scope!.Validate(status, change);
            if (!CanRestoreParameter(change.Command, status, fourTransmitters)) throw new DeviceStateChangedException(status);
            return change.IsConfirmed(status) ? status : await session.ApplyAsync(change, token, scope,
                live => CanRestoreParameter(change.Command, live, fourTransmitters)
                    && RetainedSettings.FromStatus(live) == RetainedSettings.FromStatus(status));
        }, Localization.Text("Main.Apply.Success", row.DisplayName));
        HotkeyFeedback(resultIsImportant ? ResultText : Localization.Text("Setting.Value", row.DisplayName, row.ConfirmedValue), resultIsImportant);
    }

    private Task ToggleHotkeyMuteAsync(bool all)
    {
        if (versions.GetValueOrDefault(0) is not { } version || version < new Version(1, 0, 1, 3))
        {
            SetResult(Localization.Text("Main.Mute.Unavailable"), true);
            return Task.CompletedTask;
        }
        bool groupRequested = all || TransmitterList.SelectedItem is TransmitterViewModel { IsGroup: true };
        int[] indices = groupRequested
            ? ConnectedMicrophoneIndices(currentStatus!)
            : TransmitterList.SelectedItem is TransmitterViewModel individual ? [individual.Index] : [];
        if (indices.Length == 0 || groupRequested && indices.Length < 2)
        {
            SetResult(Localization.Text("Target.GroupUnavailable"), true);
            return Task.CompletedTask;
        }
        MicrophoneControlScope scope = indices.Length > 1 ? MicrophoneControlScope.All(indices) : MicrophoneControlScope.Individual(indices[0]);
        return RunSessionOperationAsync(Localization.Text("Target.All.Pending"), async (session, _, token) =>
        {
            A1Status status = await session.ReadStatusAsync(token);
            scope.Validate(status, SettingChange.Mute(indices[0], true));
            bool muted = !indices.All(index => status.Transmitters[index].Muted);
            foreach (int index in indices)
            {
                SettingChange change = SettingChange.Mute(index, muted);
                scope.Validate(status, change);
                if (!change.IsConfirmed(status))
                {
                    A1Status checkpoint = status;
                    status = await session.ApplyAsync(change, token, scope, live => indices.All(activeIndex =>
                        live.Transmitters[activeIndex].Muted == checkpoint.Transmitters[activeIndex].Muted));
                }
            }
            status = await session.ReadStatusAsync(token);
            scope.Validate(status, SettingChange.Mute(indices[0], muted));
            if (indices.Any(index => status.Transmitters[index].Muted != muted))
                throw new InvalidOperationException("Final microphone mute readback differs from the requested state.");
            return status;
        }, Localization.Text("Main.Hotkey.MuteConfirmed"), failureWarning: Localization.Text("Target.All.PartialFailure"));
    }

    private static SettingChange HotkeyChange(HotkeyAction action, A1Status status) => action switch
    {
        HotkeyAction.IncreaseGain => SettingChange.Gain(Math.Min(status.Gain + 1, 5)),
        HotkeyAction.DecreaseGain => SettingChange.Gain(Math.Max(status.Gain - 1, 0)),
        HotkeyAction.CycleNoise => NextNoise(status),
        HotkeyAction.CycleEqualizer => SettingChange.Equalizer(status.Equalizer % 3 + 1),
        HotkeyAction.CycleReverb => NextReverb(status),
        HotkeyAction.CycleLights => SettingChange.Lights(!status.LightsEnabled),
        HotkeyAction.CycleAutoOff => SettingChange.AutoOff(1 - status.AutoOff),
        HotkeyAction.CycleIdentification => SettingChange.Identification(!status.IdentificationEnabled.GetValueOrDefault()),
        HotkeyAction.CycleChannelMode => SettingChange.ChannelMode(1 - status.ChannelMode),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static SettingChange NextNoise(A1Status status)
    {
        int next = ((status.NoiseEnabled ? status.NoiseLevel + 1 : 0) + 1) % 4;
        return SettingChange.Noise(next > 0, next > 0 ? next - 1 : status.NoiseLevel);
    }

    private static SettingChange NextReverb(A1Status status)
    {
        int next = ((status.ReverbEnabled ? status.ReverbLevel + 1 : 0) + 1) % 4;
        return SettingChange.Reverb(next > 0, next > 0 ? next - 1 : status.ReverbLevel);
    }

    private void HotkeyFeedback(string message, bool important)
    {
        if (resultIsBatteryWarning && ResultText == message) return;
        if (ResultText != message || resultIsImportant != important) SetResult(message, important);
        bool announcedInActiveWindow = (important || preferences.ShowOperationMessages)
            && UiNotificationRouting.SelectTarget(this, OwnedWindows.OfType<AuxiliaryWindow>()) is not null;
        if (!announcedInActiveWindow && (important || preferences.ShowOperationMessages || preferences.AnnounceStateChanges))
            AnnounceBackgroundNotification(message, "Hollylark.Hotkeys");
    }
}
