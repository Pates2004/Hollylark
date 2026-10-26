using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly IDesktopIntegration desktopIntegration;
    private readonly Func<string, string, bool> confirmAction;
    private readonly Action<string>? stateAnnouncement;
    private readonly IScreenReaderSpeech? screenReader;
    private string? lastSpeechFailure;
    private readonly Action<string> desktopError;
    private readonly DispatcherTimer autoApplyTimer;
    private readonly HashSet<SettingRowViewModel> pendingAutomaticChanges = [];
    private bool resultIsImportant;
    private bool exitRequested;
    private WindowState stateBeforeTray = WindowState.Normal;
    private IInputElement? focusBeforeTray;

    internal UiPreferences CurrentPreferences => preferences;

    private void ApplyViewPreferences()
    {
        ThemeMode requestedTheme = preferences.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.Light
        };
        if (ThemeMode != requestedTheme) ThemeMode = requestedTheme;
        FontSize = SystemFonts.MessageFontSize * preferences.TextScale / 100.0;
        UsageHints.SetEnabled(this, preferences.ShowHints);
        foreach (SettingRowViewModel setting in AllSettings) setting.UpdatePresentation(preferences);
        UpdateMicrophoneVisibility();
        AudioHeading.Visibility = preferences.ShowHints && AudioSettings.Any(setting => setting.RowVisibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        DeviceHeading.Visibility = preferences.ShowHints && DeviceSettings.Any(setting => setting.RowVisibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        ControlInstructions.Text = preferences.AutoApplySelections
            ? Localization.Text("Main.Control.AutomaticHint")
            : Localization.Text("Main.Control.ManualHint");
        foreach (AuxiliaryWindow section in OwnedWindows.OfType<AuxiliaryWindow>())
            section.ApplyPreferences(preferences);
        UpdateResultPresentation();
        UpdateReceiverActions();
    }

    private void UpdateResultPresentation()
    {
        bool visible = !string.IsNullOrWhiteSpace(ResultReadout.Text) && (resultIsImportant || preferences.ShowOperationMessages);
        ResultReadout.Visibility = ResultLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(ResultReadout,
            visible ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
    }

    internal void AnnounceOperationResult(string message, bool important)
    {
        if (closed || (!important && !preferences.ShowOperationMessages)) return;
        Window? target = UiNotificationRouting.SelectTarget(this, OwnedWindows.OfType<AuxiliaryWindow>());
        if (target is AuxiliaryWindow section)
            section.ShowResult(message, important, announce: true, optional: true);
        else if (ReferenceEquals(target, this))
            UiSupport.Announce(ResultReadout);
    }

    internal string? SaveApplicationPreferences(UiPreferences candidate)
    {
        if (closed || busy) return Localization.Text("Main.Preferences.Busy");
        StartupRegistrationSnapshot? previousStartup = null;
        bool updateStartup = candidate.StartWithWindows || candidate.StartWithWindows != preferences.StartWithWindows;
        bool stagedTray = false;
        bool removedTray = false;
        bool committed = false;
        try
        {
            candidate.Validate();
            string? hotkeyError = ConfigureApplicationHotkeys(candidate);
            if (hotkeyError is not null) throw new InvalidOperationException(hotkeyError);
            if (updateStartup)
            {
                previousStartup = desktopIntegration.CaptureStartup();
                desktopIntegration.SetStartup(candidate.StartWithWindows);
            }
            if (candidate.CloseToTray && !preferences.CloseToTray)
            {
                stagedTray = true;
                EnsureTray();
            }
            if (!candidate.CloseToTray && preferences.CloseToTray)
            {
                removedTray = true;
                desktopIntegration.HideTray();
            }
            if (!preferencesStore.TrySave(candidate, out string? error))
                throw new InvalidOperationException(error ?? Localization.Text("Main.Preferences.SaveFailed"));
            if (candidate.AutoApplySelections != preferences.AutoApplySelections)
            {
                pendingAutomaticChanges.Clear();
                autoApplyTimer.Stop();
            }
            bool retentionChanged = candidate.PreserveVolatileSettings != preferences.PreserveVolatileSettings;
            preferences = candidate;
            hotkeyConfigurationValid = true;
            if (retentionChanged)
            {
                retention.ResetObservations();
            }
            committed = true;
            ApplyViewPreferences();
            if (error is not null) Log(error);
            return null;
        }
        catch (Exception exception)
        {
            if (committed)
            {
                if (settingsWindow is not null && globalHotkeys is not null && !globalHotkeys.IsSuspended)
                {
                    HotkeyOperationResult suspended = globalHotkeys.Suspend();
                    settingsWindow.SetHotkeyCaptureAvailable(suspended.Succeeded);
                    if (!suspended.Succeeded) hotkeyConfigurationValid = false;
                }
                return Localization.Text("Main.Preferences.RefreshFailed", exception.Message);
            }
            string error = Localization.Text("Main.Preferences.ApplyFailed", exception.Message);
            error += RestoreApplicationHotkeys();
            if (previousStartup is not null)
            {
                try { desktopIntegration.RestoreStartup(previousStartup); }
                catch (Exception restorationError) { error += Localization.Text("Main.Preferences.StartupRestoreFailed", restorationError.Message); }
            }
            if (stagedTray)
            {
                try { desktopIntegration.HideTray(); }
                catch (Exception restorationError) { error += Localization.Text("Main.Preferences.TrayRemovalFailed", restorationError.Message); }
            }
            if (removedTray)
            {
                try { EnsureTray(); }
                catch (Exception restorationError) { error += Localization.Text("Main.Preferences.TrayRestoreFailed", restorationError.Message); }
            }
            return error;
        }
    }

    private bool RequestSettingEdit(SettingRowViewModel setting)
    {
        if (closed) return false;
        if (!RequireConnectedMicrophone()) return false;
        if (busy || setting.UnavailableReason is not null)
        {
            SetResult(setting.UnavailableReason ?? Localization.Text("Setting.Pending"), true);
            return false;
        }
        return true;
    }

    private void OnSettingDraftChanged(SettingRowViewModel setting)
    {
        if (closed) return;
        if (!RequireConnectedMicrophone() || setting.UnavailableReason is not null)
        {
            if (currentStatus?.Transmitters.Any(transmitter => transmitter.Connected) == true)
                SetResult(setting.UnavailableReason ?? Localization.Text("Setting.Unavailable"), true);
            setting.DiscardDraft();
            setting.Update(currentStatus, busy, SharedControlUnavailableReason());
            return;
        }
        if (!preferences.AutoApplySelections || closed || !IsConnected) return;
        pendingAutomaticChanges.Add(setting);
        autoApplyTimer.Stop();
        autoApplyTimer.Start();
    }

    internal async Task FlushAutomaticChangesAsync()
    {
        if (closed || !IsConnected || !preferences.AutoApplySelections)
        {
            pendingAutomaticChanges.Clear();
            autoApplyTimer.Stop();
            return;
        }
        if (busy || VisualChildren(this).OfType<Slider>().Any(slider => slider.IsMouseCaptureWithin)) return;
        SettingRowViewModel? next = AllSettings.FirstOrDefault(setting => pendingAutomaticChanges.Contains(setting) && setting.IsDirty && setting.CanApply);
        if (next is null)
        {
            pendingAutomaticChanges.Clear();
            autoApplyTimer.Stop();
            return;
        }
        pendingAutomaticChanges.Remove(next);
        await ApplySettingAsync(next);
        if (pendingAutomaticChanges.Count == 0) autoApplyTimer.Stop();
    }

    private void OnStepGain(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: SettingRowViewModel setting, Tag: string step } || !setting.CanEdit) return;
        if (!RequireConnectedMicrophone()) return;
        int direction = step == "Increase" ? 1 : -1;
        setting.SelectedIndex = Math.Clamp(setting.SelectedIndex + direction, 0, setting.Options.Count - 1);
    }

    internal async Task<string?> ApplyAudioProfileAsync(AudioProfile profile)
    {
        if (!IsConnected || currentStatus is null) return Localization.Text("Main.Profile.ConnectFirst");
        if (busy) return Localization.Text("Main.Profile.Busy");
        if (!currentStatus.Transmitters.Any(transmitter => transmitter.Connected)) return Localization.Text("Main.Profile.TransmitterRequired");
        string? scopeRestriction = SharedControlUnavailableReason();
        if (scopeRestriction is not null) return scopeRestriction;
        if (!TryGetSharedControlScope(out MicrophoneControlScope? scope)) return ResultText;
        try { profile.Validate(); }
        catch (Exception exception) { return Localization.Text("Main.Profile.Invalid", exception.Message); }
        A1Controller originalSession = controller!;
        long expectedGeneration = generation;
        pendingAutomaticChanges.Clear();
        autoApplyTimer.Stop();
        foreach (SettingRowViewModel setting in AllSettings) setting.DiscardDraft();
        string? restriction = null;
        bool wroteSettings = false;
        SettingChange noiseChange = SettingChange.Noise(profile.NoiseEnabled, profile.NoiseLevel);
        Task pendingApplication = RunSessionOperationAsync(Localization.Text("Main.Profile.Pending", profile.Name),
            async (session, _, token) =>
            {
                A1Status status = await session.ReadStatusAsync(token);
                if (!ValidateScope(status)) return status;
                if (!status.Transmitters.Any(transmitter => transmitter.Connected))
                {
                    restriction = Localization.Text("Main.Profile.Disconnected");
                    return status;
                }
                if (NoiseChangeBlocked(status))
                {
                    restriction = Localization.Text("Main.Profile.NoiseBlocked");
                    return status;
                }
                foreach (SettingChange change in profile.CreateChanges())
                {
                    if (!ValidateScope(status)) return status;
                    if (change.Command == A1Command.SetNoise)
                    {
                        status = await session.ReadStatusAsync(token);
                        if (!ValidateScope(status)) return status;
                        if (NoiseChangeBlocked(status))
                        {
                            restriction = Localization.Text(wroteSettings
                                ? "Main.Profile.InterruptedAfterChanges" : "Main.Profile.InterruptedBeforeChanges");
                            return status;
                        }
                    }
                    if (!change.IsConfirmed(status))
                    {
                        try { status = await session.ApplyAsync(change, token, scope); }
                        catch (MicrophoneScopeException exception)
                        {
                            restriction = ScopeFailureMessage(exception);
                            if (wroteSettings) restriction += " " + Localization.Text("Main.Profile.ConnectionInterrupted");
                            return exception.Status;
                        }
                        catch (DeviceWriteSafetyException exception)
                        {
                            restriction = DeviceWriteSafetyMessage(exception);
                            if (wroteSettings) restriction += " " + Localization.Text("Main.Profile.ConnectionInterrupted");
                            return exception.Status;
                        }
                        wroteSettings = true;
                    }
                }
                return status;
            }, Localization.Text("Main.Profile.StatusRead"));
        long expectedOperation = operation;
        await pendingApplication;
        if (!IsSession(originalSession, expectedGeneration) || operation != expectedOperation)
            return Localization.Text("Main.Profile.NotConfirmed");
        if (restriction is not null)
        {
            SetResult(restriction, true);
            return restriction;
        }
        if (IsConnected && currentStatus is not null && profile.CreateChanges().All(change => change.IsConfirmed(currentStatus)))
        {
            SetResult(Localization.Text("Main.Profile.Confirmed", profile.Name));
            return null;
        }
        string error = Localization.Text("Main.Profile.NotConfirmed");
        SetResult(error, true);
        return error;

        bool NoiseChangeBlocked(A1Status status) => !noiseChange.IsConfirmed(status)
            && !status.Transmitters.Any(transmitter => transmitter.Connected && !transmitter.Muted);

        bool ValidateScope(A1Status status)
        {
            try { scope!.Validate(status, noiseChange); return true; }
            catch (MicrophoneScopeException exception)
            {
                restriction = ScopeFailureMessage(exception);
                if (wroteSettings) restriction += " " + Localization.Text("Main.Profile.ConnectionInterrupted");
                return false;
            }
        }
    }

    private void AnnounceConfirmedChanges(A1Status? previous, A1Status current)
    {
        if (!preferences.AnnounceStateChanges || previous is null) return;
        var changes = new List<string>();
        if (previous.Gain != current.Gain) changes.Add(Localization.Text("Main.State.Gain", current.Gain + 1));
        if (previous.ChannelMode != current.ChannelMode)
            changes.Add(Localization.Text(current.ChannelMode == 1 ? "Main.State.Stereo" : "Main.State.Mono"));
        if (previous.NoiseEnabled != current.NoiseEnabled || previous.NoiseLevel != current.NoiseLevel)
            changes.Add(current.NoiseEnabled ? Localization.Text("Main.State.NoiseLevel", current.NoiseLevel + 1)
                : Localization.Text("Main.State.NoiseOff"));
        if (previous.ReverbEnabled != current.ReverbEnabled || previous.ReverbLevel != current.ReverbLevel)
            changes.Add(current.ReverbEnabled ? Localization.Text("Main.State.ReverbLevel", current.ReverbLevel + 1)
                : Localization.Text("Main.State.ReverbOff"));
        if (previous.Equalizer != current.Equalizer) changes.Add(Localization.Text("Main.State.Equalizer", AudioSettings.Single(setting => setting.Id == "Equalizer").ConfirmedValue));
        if (previous.LightsEnabled != current.LightsEnabled)
            changes.Add(Localization.Text(current.LightsEnabled ? "Main.State.LightsOn" : "Main.State.LightsOff"));
        if (previous.AutoOff != current.AutoOff) changes.Add(Localization.Text("Main.State.AutoOff", DeviceSettings.Single(setting => setting.Id == "AutoOff").ConfirmedValue));
        if (previous.IdentificationEnabled != current.IdentificationEnabled) changes.Add(Localization.Text("Main.State.Identification", DeviceSettings.Single(setting => setting.Id == "Identification").ConfirmedValue));
        for (int index = 0; index < current.Transmitters.Count; index++)
        {
            TransmitterState state = current.Transmitters[index];
            TransmitterState? before = previous.Transmitters.ElementAtOrDefault(index);
            if (before is null || before.Connected != state.Connected || before.Muted != state.Muted)
                changes.Add(Localization.Text("Main.State.Transmitter", index + 1,
                    Localization.Text(!state.Connected ? "Transmitter.Disconnected" : state.Muted ? "Transmitter.Muted" : "Transmitter.Active")));
        }
        if (changes.Count == 0) return;
        string message = string.Join(". ", changes);
        AnnounceBackgroundNotification(message, "Hollylark.State");
    }

    private void AnnounceBackgroundNotification(string message, string activity)
    {
        if (closed) return;
        if (stateAnnouncement is not null) { stateAnnouncement(message); return; }
        UiNotificationRouting.Announce(screenReader, message, () => RaiseBackgroundNotification(message, activity), exception =>
        {
            if (lastSpeechFailure == exception.Message) return;
            lastSpeechFailure = exception.Message;
            Log(Localization.Text("Main.Log.AccessibilityError", exception.Message));
        });
    }

    private void RaiseBackgroundNotification(string message, string activity)
    {
        Window target = UiNotificationRouting.SelectStateTarget(this, OwnedWindows.OfType<AuxiliaryWindow>());
        try
        {
            AutomationPeer? peer = UIElementAutomationPeer.FromElement(target) ?? UIElementAutomationPeer.CreatePeerForElement(target);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, message, activity);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            Log(Localization.Text("Main.Log.AccessibilityError", exception.Message));
        }
    }

    private void EnsureTray() => desktopIntegration.ShowTray(this, RestoreFromTray,
        () => { RestoreFromTray(); OpenSettingsWindow(); },
        () => { RestoreFromTray(); _ = RefreshAsync(); },
        () => { RestoreFromTray(); RequestExit(); });

    private void OnDesktopFailure(Exception exception)
    {
        if (closed)
        {
            System.Diagnostics.Trace.WriteLine(Localization.Text("Main.Log.DesktopCleanup", exception));
            desktopError(Localization.Text("Main.Tray.CleanupFailed", exception.Message));
            return;
        }
        RestoreFromTray();
        Log(exception.ToString());
        SetResult(Localization.Text("Main.Tray.IntegrationFailed", exception.Message), true);
    }

    internal void RestoreFromTray()
    {
        if (closed) return;
        bool wasHidden = !IsVisible;
        ShowInTaskbar = true;
        if (wasHidden)
        {
            Show();
            WindowState = stateBeforeTray == WindowState.Minimized ? WindowState.Normal : stateBeforeTray;
        }
        else if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (wasHidden)
        {
            if (focusBeforeTray is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous) previous.Focus();
            else ConnectionReadout.Focus();
        }
    }

    public void RequestExit()
    {
        if (closed) return;
        exitRequested = true;
        Close();
        if (!closed) exitRequested = false;
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        foreach (Window section in OwnedWindows.Cast<Window>().ToArray())
        {
            section.Close();
            if (section.IsVisible) { eventArgs.Cancel = true; base.OnClosing(eventArgs); return; }
        }
        if (!exitRequested && preferences.CloseToTray)
        {
            eventArgs.Cancel = true;
            try
            {
                EnsureTray();
                stateBeforeTray = WindowState;
                focusBeforeTray = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closed || exitRequested || !preferences.CloseToTray) return;
                    try
                    {
                        ShowInTaskbar = false;
                        Hide();
                    }
                    catch (Exception exception) { OnDesktopFailure(exception); }
                }));
            }
            catch (Exception exception)
            {
                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                SetResult(Localization.Text("Main.Tray.HideFailed", exception.Message), true);
            }
        }
        else if ((preferences.ConfirmExit || busy) && !confirmAction(busy
            ? Localization.Text("Main.Exit.BusyConfirmation")
            : Localization.Text("Main.Exit.Confirmation"), Localization.Text("Main.Exit.Title"))) eventArgs.Cancel = true;
        base.OnClosing(eventArgs);
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualChildren(child)) yield return descendant;
        }
    }
}
