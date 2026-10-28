using System.IO;
using System.Reflection;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyRetentionOverrideChecks()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (bool identityRead in new[] { false, true })
                    foreach (string mode in new[] { "manual", "automatic", "profile", "equalizer", "hotkey", "read-only", "mute", "profile-refused" })
                        VerifyRetentionExplicitOverride(identityRead, mode);
                VerifyRetentionFinalOverride();
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyRetentionExplicitOverride(bool identityRead, string mode)
    {
        string scenario = Localization.CurrentLanguage + "/" + identityRead + "/" + mode;
        using var transport = new FakeTransport();
        var receiver = new ReceiverInfo("Simulated retention override receiver", "simulated://retention-override", 0, 64);
        string directory = Path.Combine(ReportDirectory, "retention-override-" + Guid.NewGuid().ToString("N"));
        var preferencesStore = new UiPreferencesStore(directory);
        Check(preferencesStore.TrySave(new UiPreferences
        {
            FirstRunCompleted = true, Language = Localization.CurrentLanguage, ConfirmExit = false,
            AutoApplySelections = mode == "automatic", ShowHints = false, ShowOperationMessages = false,
            AnnounceStateChanges = false, PreserveVolatileSettings = true, GlobalHotkeysEnabled = mode is "mute" or "hotkey"
        }, out string? error), "Prepare isolated retention override preferences: " + error);
        byte[] preferencesBefore = File.ReadAllBytes(preferencesStore.FilePath);
        var retainedStore = new DeviceSettingsRetentionStore(directory);
        Check(retainedStore.TrySave([new(receiver.Path, RetainedSettings.FromStatus(A1Status.Parse(ValidStatus)) with { Gain = 5 })],
            out error), "Seed an older persisted baseline for startup safety: " + error);
        var native = new FakeHotkeyNativeApi();
        var desktop = new MemoryDesktop();
        var window = new MainWindow(() => [receiver], _ => transport, directory, desktop, (_, _) => true,
            announceState: _ => { }, automaticReceiverManagement: false, hotkeyNativeApi: native);
        try
        {
            RunTask(window.ScanAsync());
            RunTask(window.ConnectAsync());
            RetentionOverrideField<DispatcherTimer>(window, "pollTimer")!.Stop();
            SettingRowViewModel gain = window.AudioSettings.Single(row => row.Id == "Gain");
            Check(!window.IsVisible && window.IsConnected && gain.SelectedIndex == 3 && CountMockWrites(transport) == 0
                && ReadRetention(new DeviceSettingsRetentionStore(directory), receiver.Path).Gain == 3,
                "Initial active status replaces the old baseline without an automatic write: " + scenario);

            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            SetAutomationStatusByte(transport, 8, 0);
            SetAutomationStatusByte(transport, 12, 1);
            SetAutomationStatusByte(transport, 11, 1);
            RunTask(Poll(window));
            transport.SetTransmitterConnected(0, true);
            transport.SetTransmitterConnected(1, true);
            RunTask(identityRead ? window.ReadIdentitiesAsync() : window.RefreshAsync());
            DeviceSettingsRetention model = RetentionOverrideField<DeviceSettingsRetention>(window, "retention")!;
            RetentionRestoreRequest? deferred = RetentionOverrideField<RetentionRestoreRequest>(window, "deferredRetention");
            Check(deferred is not null && model.IsCurrent(deferred) && deferred.DesiredSettings.Gain == 3
                && deferred.DesiredSettings.Equalizer == 3 && deferred.DesiredSettings.LightsEnabled
                && gain.SelectedIndex == 0 && CountMockWrites(transport) == 0,
                "The explicit read defers a real reconnect restore without changing hardware: " + scenario);

            if (mode is "read-only" or "mute" or "profile-refused")
            {
                if (mode == "mute")
                {
                    RunTask(window.ExecuteHotkeyAsync(HotkeyAction.ToggleAllMute));
                    Check(model.IsCurrent(deferred!)
                        && ReferenceEquals(RetentionOverrideField<RetentionRestoreRequest>(window, "deferredRetention"), deferred)
                        && gain.SelectedIndex == 0 && window.Transmitters.All(transmitter => transmitter.IsMuted)
                        && CountMockWrites(transport) == 1 && transport.Commands.Count(command => command == A1Command.SetMute) == 1,
                        "An explicit mute preserves the pending shared restore and changes only mute: " + scenario);
                }
                if (mode == "profile-refused")
                {
                    SetAutomationStatusByte(transport, 4, 1);
                    SetAutomationStatusByte(transport, 5, 1);
                    AudioProfile refused = AudioProfile.FromStatus("Refused profile must not confirm intent", A1Status.Parse(ValidStatus))
                        with { NoiseEnabled = false };
                    string? refusal = RunTaskResult(window.ApplyAudioProfileAsync(refused));
                    Check(refusal == Localization.Text("Main.Profile.NoiseBlocked") && model.IsCurrent(deferred!)
                        && ReferenceEquals(RetentionOverrideField<RetentionRestoreRequest>(window, "deferredRetention"), deferred)
                        && CountMockWrites(transport) == 0,
                        "A refused profile confirms no audio intent and preserves the deferred restore: " + scenario);
                }
                bool[] mutedBefore = window.Transmitters.Select(transmitter => transmitter.IsMuted).ToArray();
                int writesBefore = CountMockWrites(transport);
                int muteWritesBefore = transport.Commands.Count(command => command == A1Command.SetMute);
                RunTask(Poll(window));
                Check(gain.SelectedIndex == 3 && window.AudioSettings.Single(row => row.Id == "Equalizer").SelectedIndex == 2
                    && window.DeviceSettings.Single(row => row.Id == "Lights").SelectedIndex == 0
                    && CountMockWrites(transport) == writesBefore + 3 && !model.IsCurrent(deferred!)
                    && window.Transmitters.Select(transmitter => transmitter.IsMuted).SequenceEqual(mutedBefore)
                    && transport.Commands.Count(command => command == A1Command.SetMute) == muteWritesBefore,
                    "The next background poll restores gain, EQ and lights without changing mute: " + scenario);
                RunTask(Poll(window));
                Check(CountMockWrites(transport) == writesBefore + 3,
                    "The completed shared restore is not repeated after a read or mute: " + scenario);
            }
            else
            {
                if (mode == "manual")
                    RunTask(window.ApplySettingAsync(gain));
                else if (mode == "automatic")
                {
                    gain.SelectedIndex = 1;
                    gain.SelectedIndex = 0;
                    RunTask(window.FlushAutomaticChangesAsync());
                }
                else if (mode == "equalizer")
                {
                    SettingRowViewModel equalizer = window.AudioSettings.Single(row => row.Id == "Equalizer");
                    equalizer.SelectedIndex = 1;
                    RunTask(window.ApplySettingAsync(equalizer));
                }
                else if (mode == "hotkey")
                    RunTask(window.ExecuteHotkeyAsync(HotkeyAction.CycleLights));
                else
                {
                    AudioProfile profile = AudioProfile.FromStatus("Keep the confirmed reconnect state",
                        A1Status.Parse(ValidStatus)) with { Gain = 0, Equalizer = 1 };
                    Check(RunTaskResult(window.ApplyAudioProfileAsync(profile)) is null,
                        "Applying an already-matching profile is explicitly confirmed: " + scenario);
                }
                int expectedWrites = mode == "profile" ? 0 : 1;
                int desiredGain = mode is "equalizer" or "hotkey" ? 3 : 0;
                int desiredEqualizer = mode == "equalizer" ? 2 : mode == "profile" ? 1 : 3;
                RetentionRestoreRequest? replacement = RetentionOverrideField<RetentionRestoreRequest>(window, "deferredRetention");
                Check(!model.IsCurrent(deferred!) && replacement is not null && model.IsCurrent(replacement)
                    && replacement.DesiredSettings.Gain == desiredGain && replacement.DesiredSettings.Equalizer == desiredEqualizer
                    && replacement.DesiredSettings.LightsEnabled && replacement.ObservedSettings.Gain == 0
                    && replacement.ObservedSettings.Equalizer == (mode == "equalizer" ? 2 : 1)
                    && replacement.ObservedSettings.LightsEnabled == (mode == "hotkey")
                    && !gain.IsDirty && CountMockWrites(transport) == expectedWrites,
                    "Confirmation replaces only the selected intent and refreshes the guarded pending snapshot: " + scenario);
                RetainedSettings persisted = ReadRetention(new DeviceSettingsRetentionStore(directory), receiver.Path);
                Check(persisted.Gain == desiredGain && persisted.Equalizer == desiredEqualizer && persisted.LightsEnabled,
                    "The hybrid explicit and still-pending intent is persisted before restoration: " + scenario);
                RunTask(Poll(window));
                int restorationWrites = mode == "profile" ? 1 : 2;
                Check(gain.SelectedIndex == desiredGain
                    && window.AudioSettings.Single(row => row.Id == "Equalizer").SelectedIndex == desiredEqualizer - 1
                    && window.DeviceSettings.Single(row => row.Id == "Lights").SelectedIndex == 0
                    && CountMockWrites(transport) == expectedWrites + restorationWrites && !model.IsCurrent(replacement!),
                    "Polling restores only remaining fields while retaining the user's gain, EQ or five-field audio profile: " + scenario);
                RunTask(Poll(window));
                Check(gain.SelectedIndex == desiredGain && CountMockWrites(transport) == expectedWrites + restorationWrites,
                    "Later polls cannot overwrite explicit fields or repeat the remaining restore: " + scenario);
            }
            Check(transport.Commands.Count(command => command == A1Command.SetMute) == (mode == "mute" ? 1 : 0)
                && File.ReadAllBytes(preferencesStore.FilePath).SequenceEqual(preferencesBefore)
                && native.Calls.Count == 0 && desktop.StartupWrites == 0 && desktop.TrayShows == 0,
                "Only the explicit mute may change mute; checks never rewrite preferences or use native integration: " + scenario);
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void VerifyRetentionFinalOverride()
    {
        const string path = "simulated://retention-final-override";
        var store = RetentionStore("final-explicit-override-" + Localization.CurrentLanguage);
        var model = new DeviceSettingsRetention(store);
        RetainedSettings original = RetainedSettings.FromStatus(A1Status.Parse(ValidStatus));
        model.Observe(path, RetentionStatus(original, [0, 1]));
        model.Observe(path, RetentionStatus(original, []));
        A1Status reset = RetentionStatus(original with { Gain = 0 }, [0, 1]);
        RetentionRestoreRequest pending = RequireRetentionRequest(model.Observe(path, reset),
            "Prepare a pending restore with only gain differing");
        Check(model.Confirm(path, reset, [A1Command.SetGain]) is null && !model.IsCurrent(pending)
            && ReadRetention(store, path).Gain == 0 && model.Observe(path, reset) is null,
            "Confirming the final differing field consumes the token and never schedules an unnecessary restore");
    }

    private static T? RetentionOverrideField<T>(MainWindow window, string name) where T : class =>
        (T?)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
}
