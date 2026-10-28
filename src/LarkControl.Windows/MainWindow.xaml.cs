using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand ScanCommand = new(Localization.Text("Main.Command.Search"), nameof(ScanCommand), typeof(MainWindow));
    public static readonly RoutedUICommand RefreshCommand = new(Localization.Text("Main.Command.Refresh"), nameof(RefreshCommand), typeof(MainWindow));

    private readonly Func<IReadOnlyList<ReceiverInfo>> discover;
    private readonly Func<ReceiverInfo, IA1Transport> factory;
    private readonly UiPreferencesStore preferencesStore;
    private readonly DispatcherTimer pollTimer;
    private readonly Dictionary<int, Version> versions = [];
    private readonly Dictionary<int, (string? Serial, string? Mac)> identities = [];
    private readonly Queue<string> logLines = new();
    private readonly string? preferencesError;
    private UiPreferences preferences;
    private A1Controller? controller;
    private A1Status? currentStatus;
    private CancellationTokenSource? sessionCancellation;
    private Action<string>? traceHandler;
    private SettingsWindow? settingsWindow;
    private InfoWindow? informationWindow;
    private HelpWindow? helpWindow;
    private AudioMeterWindow? audioMeterWindow;
    private FirmwareWindow? firmwareWindow;
    private IInputElement? lastContentFocus;
    private long generation;
    private long operation;
    private long focusInteraction;
    private bool busy;
    private bool polling;
    private bool closed;
    private bool loaded;

    public ObservableCollection<ReceiverInfo> Receivers { get; } = [];
    public ObservableCollection<SettingRowViewModel> AudioSettings { get; } = [];
    public ObservableCollection<SettingRowViewModel> DeviceSettings { get; } = [];
    public ObservableCollection<TransmitterViewModel> Transmitters { get; } = [];
    public ObservableCollection<TransmitterViewModel> TransmitterTargets { get; } = [];
    public bool IsBusy => busy;
    public bool IsConnected => controller is not null && currentStatus is not null;
    public bool ShowHints => preferences.ShowHints;
    public string ResultText => ResultReadout.Text;
    private IEnumerable<SettingRowViewModel> AllSettings => AudioSettings.Concat(DeviceSettings);

    public MainWindow() : this(HidTransport.FindReceivers, receiver => new HidTransport(receiver),
        screenReader: new PrismScreenReaderSpeech()) { }

    public MainWindow(Func<IReadOnlyList<ReceiverInfo>> discover, Func<ReceiverInfo, IA1Transport> factory,
        string? baseDirectory = null, IDesktopIntegration? desktop = null, Func<string, string, bool>? confirm = null,
        Action<string>? announceState = null, Action<string>? reportDesktopError = null, bool automaticReceiverManagement = true,
        IHotkeyNativeApi? hotkeyNativeApi = null, IScreenReaderSpeech? screenReader = null,
        Action<BatteryAlertSeverity>? playBatterySound = null)
    {
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(factory);
        this.discover = discover;
        this.factory = factory;
        this.automaticReceiverManagement = automaticReceiverManagement;
        this.hotkeyNativeApi = hotkeyNativeApi;
        this.screenReader = screenReader;
        this.playBatterySound = playBatterySound ?? PlayBatterySystemSound;
        desktopIntegration = desktop ?? new DesktopIntegration();
        if (desktopIntegration is DesktopIntegration nativeDesktop) nativeDesktop.Failed += OnDesktopFailure;
        Func<string, string, bool> confirmDialog = confirm ?? ((message, title) => MessageBox.Show(GetDialogOwner(), message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        confirmAction = (message, title) =>
        {
            bool previous = shortcutDialogActive;
            shortcutDialogActive = true;
            try { return confirmDialog(message, title); }
            finally { shortcutDialogActive = previous; }
        };
        stateAnnouncement = announceState;
        desktopError = reportDesktopError ?? (message => MessageBox.Show(message, Localization.Text("Main.Tray.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error));
        preferencesStore = new UiPreferencesStore(baseDirectory);
        preferences = preferencesStore.Load(out preferencesError);
        retentionStore = new DeviceSettingsRetentionStore(preferencesStore.BaseDirectory);
        retention = new DeviceSettingsRetention(retentionStore);
        InitializeComponent();
        Localization.ApplyResources(Resources);
        Language = XmlLanguage.GetLanguage(Localization.Culture.IetfLanguageTag);
        UpdateCommandText();
        CreateSettings();
        PopulateSettingPanel(AudioSettingsPanel, AudioSettings);
        PopulateSettingPanel(DeviceSettingsPanel, DeviceSettings);
        DataContext = this;
        autoApplyTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        autoApplyTimer.Tick += async (_, _) => await FlushAutomaticChangesAsync();
        foreach (SettingRowViewModel setting in AllSettings)
        {
            setting.EditRequested = () => RequestSettingEdit(setting);
            setting.DraftChanged += OnSettingDraftChanged;
        }
        ApplyViewPreferences();
        pollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        pollTimer.Tick += async (_, _) => await PollAsync();
        receiverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        receiverTimer.Tick += async (_, _) => await RefreshUsbAsync();
        Loaded += OnLoaded;
        SourceInitialized += InitializeGlobalHotkeys;
        PreviewKeyDown += (_, _) => focusInteraction++;
        PreviewMouseDown += (_, _) => focusInteraction++;
        Deactivated += (_, _) => focusInteraction++;
        PreviewGotKeyboardFocus += (_, eventArgs) =>
        {
            if (eventArgs.NewFocus is FrameworkElement element && element is not MenuItem and not Menu
                && Window.GetWindow(element) == this)
                lastContentFocus = element;
        };
        UpdateView();
        desktopIntegration.ConfigureTrayControls(ReadTraySnapshot, AdjustGainFromTrayAsync, ToggleMuteFromTrayAsync);
    }

    private void PopulateSettingPanel(Panel panel, IEnumerable<SettingRowViewModel> settings)
    {
        var template = (DataTemplate)Resources["SettingTemplate"];
        foreach (SettingRowViewModel setting in settings)
            panel.Children.Add(new ContentControl
            {
                Content = setting,
                ContentTemplate = template,
                Focusable = false,
                IsTabStop = false
            });
    }

    private void CreateSettings()
    {
        static string? RequiresTransmitter(A1Status state) => state.Transmitters.Any(transmitter => transmitter.Connected)
            ? null : Localization.Text("Setting.Availability.TransmitterRequired");
        AudioSettings.Add(new("Gain", Localization.Text("Setting.Gain.Name"), [Localization.Text("Setting.Gain.Lowest"),
            Localization.Text("Setting.Gain.Option", 2), Localization.Text("Setting.Gain.Option", 3),
            Localization.Text("Setting.Gain.Option", 4), Localization.Text("Setting.Gain.Option", 5), Localization.Text("Setting.Gain.Highest")],
            SettingChange.Gain, state => state.Gain, RequiresTransmitter));
        AudioSettings.Add(new("Noise", Localization.Text("Setting.Noise.Name"), [Localization.Text("Setting.Noise.Off"), Localization.Text("Setting.Noise.Weak"), Localization.Text("Setting.Noise.Medium"), Localization.Text("Setting.Noise.Strong")],
            selected => SettingChange.Noise(selected > 0, selected > 0 ? selected - 1
                : currentStatus?.NoiseLevel ?? throw new InvalidOperationException("A confirmed receiver status is required.")),
            state => state.NoiseEnabled ? state.NoiseLevel + 1 : 0, state => RequiresTransmitter(state)
                ?? (state.Transmitters.Where(transmitter => transmitter.Connected).All(transmitter => transmitter.Muted)
                    ? Localization.Text("Setting.Noise.UnmuteRequired") : null)));
        AudioSettings.Add(new("Equalizer", Localization.Text("Setting.Equalizer.Name"), [Localization.Text("Setting.Equalizer.Bass"), Localization.Text("Setting.Equalizer.Bright"), Localization.Text("Setting.Equalizer.Balanced")],
            selected => SettingChange.Equalizer(selected + 1), state => state.Equalizer - 1, RequiresTransmitter));
        AudioSettings.Add(new("Reverb", Localization.Text("Setting.Reverb.Name"), [Localization.Text("Setting.Reverb.Off"), Localization.Text("Setting.Reverb.LevelOne"), Localization.Text("Setting.Reverb.LevelTwo"), Localization.Text("Setting.Reverb.LevelThree")],
            selected => SettingChange.Reverb(selected > 0, selected > 0 ? selected - 1
                : currentStatus?.ReverbLevel ?? throw new InvalidOperationException("A confirmed receiver status is required.")),
            state => state.ReverbEnabled ? state.ReverbLevel + 1 : 0, RequiresTransmitter));
        AudioSettings.Add(new("ChannelMode", Localization.Text("Setting.ChannelMode.Name"), [Localization.Text("Setting.ChannelMode.Mono"), Localization.Text("Setting.ChannelMode.Stereo")],
            SettingChange.ChannelMode, state => state.ChannelMode, RequiresTransmitter));
        DeviceSettings.Add(new("Lights", Localization.Text("Setting.Lights.Name"), [Localization.Text("Setting.Enabled"), Localization.Text("Setting.Disabled")],
            selected => SettingChange.Lights(selected == 0), state => state.LightsEnabled ? 0 : 1, _ => null));
        DeviceSettings.Add(new("AutoOff", Localization.Text("Setting.AutoOff.Name"), [Localization.Text("Setting.AutoOff.AfterUnpaired"), Localization.Text("Setting.AutoOff.Never")],
            SettingChange.AutoOff, state => state.AutoOff, _ => null));
        DeviceSettings.Add(new("Identification", Localization.Text("Setting.Identification.Name"), [Localization.Text("Setting.Disabled"), Localization.Text("Setting.Enabled")],
            selected => SettingChange.Identification(selected == 1),
            state => state.IdentificationEnabled is bool enabled ? (enabled ? 1 : 0) : null,
            state => !state.IdentificationEnabled.HasValue ? Localization.Text("Setting.Identification.Unknown")
                : state.Transmitters.Count(transmitter => transmitter.Connected) < (A1DeviceIdentity.IsFourTransmitterFirmware(versions.GetValueOrDefault(0)) ? 1 : 2)
                    ? Localization.Text("Setting.Identification.TransmittersRequired") : null));
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (loaded) return;
        loaded = true;
        if (IsActive && Keyboard.FocusedElement is null) ConnectionReadout.Focus();
        if (automaticReceiverManagement) receiverTimer.Start();
        await ScanAsync();
        if (closed) return;
        if (preferencesError is not null) SetResult(preferencesError, true);
        if (preferences.CloseToTray)
        {
            try { EnsureTray(); }
            catch (Exception exception) { SetResult(Localization.Text("Main.Tray.StartFailed", exception.Message), true); }
        }
    }

    public async Task ScanAsync()
    {
        if (closed) return;
        await RefreshUsbAsync(retryFailed: true);
        await receiverSessionTask;
    }

    public async Task ConnectAsync(bool reportProgress = true)
    {
        if (closed || busy || controller is not null || SelectedReceiver is not ReceiverInfo receiver) return;
        var previousFocus = CaptureOperationFocus();
        long expectedGeneration = ++generation;
        controlContextGeneration++;
        long expectedOperation = ++operation;
        var cancellation = new CancellationTokenSource();
        sessionCancellation = cancellation;
        sessionReceiver = receiver;
        CancellationToken token = cancellation.Token;
        busy = true;
        UpdateView();
        if (reportProgress) SetResult(Localization.Text("Main.Usb.Reading", receiver.Name));
        long initialBatteryRevision = batteryAlertRevision;
        try
        {
            IA1Transport transport;
            await transportOpenGate.WaitAsync(token);
            try
            {
                transport = await Task.Run(() => factory(receiver), token);
                if (!IsCurrent(expectedGeneration, expectedOperation)) { transport.Dispose(); return; }
            }
            finally { transportOpenGate.Release(); }
            var session = new A1Controller(transport);
            controller = session;
            traceHandler = message => QueueTrace(session, expectedGeneration, message);
            session.Trace += traceHandler;
            Log(Localization.Text("Main.Log.Receiver", receiver.Name, receiver.Path));
            A1Status state = await ReadSnapshotAsync(session, expectedGeneration, token);
            if (!IsSession(session, expectedGeneration) || operation != expectedOperation) return;
            ClearReceiverFailure();
            bool attemptedRestoration = await AcceptObservedStateAsync(state, session, expectedGeneration, token);
            if (!IsSession(session, expectedGeneration) || operation != expectedOperation) return;
            pollTimer.Start();
            if (!attemptedRestoration && retention.LastError is null && batteryAlertRevision == initialBatteryRevision)
                SetResult(GetUsbConnectionText());
            else if (retention.LastError is not null) SetResult(Localization.Text("Retention.StoreError", retentionStore.FilePath), true);
        }
        catch (Exception exception)
        {
            if (IsCurrent(expectedGeneration, expectedOperation)) HandleSessionError(exception);
        }
        finally
        {
            if (IsCurrent(expectedGeneration, expectedOperation)) { busy = false; UpdateView(); }
            RestoreOperationFocus(previousFocus);
        }
    }

    private async Task<A1Status> ReadSnapshotAsync(A1Controller session, long expectedGeneration, CancellationToken token)
    {
        long expectedOperation = operation;
        bool SnapshotIsCurrent() => IsSession(session, expectedGeneration) && operation == expectedOperation;
        A1Status state = await session.ReadStatusAsync(token);
        if (!SnapshotIsCurrent()) return state;
        if (!versions.ContainsKey(0))
        {
            Version version = await session.ReadVersionAsync(0, token);
            if (!SnapshotIsCurrent()) return state;
            versions[0] = version;
        }
        if (A1DeviceIdentity.IsFourTransmitterFirmware(versions[0]) && state.Transmitters.Count < 4)
        {
            state = await session.ReadStatusAsync(token);
            if (!SnapshotIsCurrent()) return state;
        }
        for (int index = 0; index < state.Transmitters.Count; index++)
        {
            if (!state.Transmitters[index].Connected) { versions.Remove(index + 1); continue; }
            if (versions.ContainsKey(index + 1)) continue;
            Version version = await session.ReadVersionAsync(index + 1, token);
            if (!SnapshotIsCurrent()) return state;
            versions[index + 1] = version;
        }
        return state;
    }

    public Task RefreshAsync() => !IsConnected && automaticReceiverManagement ? ScanAsync() : RunSessionOperationAsync(Localization.Text("Main.Refresh.Pending"),
        (session, expectedGeneration, token) => ReadSnapshotAsync(session, expectedGeneration, token),
        Localization.Text("Main.Refresh.Success"),
        () => { foreach (SettingRowViewModel setting in AllSettings) setting.DiscardDraft(); }, observeSettings: true);

    public Task ApplySettingAsync(SettingRowViewModel setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!AllSettings.Contains(setting)) throw new ArgumentException("The setting does not belong to this window.", nameof(setting));
        if (busy || closed) return Task.CompletedTask;
        if (!RequireConnectedMicrophone()) return Task.CompletedTask;
        if (!TryGetSharedControlScope(out MicrophoneControlScope? scope)) return Task.CompletedTask;
        if (!setting.CanApply)
        {
            SetResult(setting.UnavailableReason ?? Localization.Text("Setting.Unavailable"), true);
            return Task.CompletedTask;
        }
        pendingAutomaticChanges.Remove(setting);
        SettingChange change = setting.CreateChange();
        long revision = setting.Revision;
        return RunSessionOperationAsync(Localization.Text("Main.Apply.Pending", setting.DisplayName),
            (session, _, token) => session.ApplyAsync(change, token, scope),
            Localization.Text("Main.Apply.Success", setting.DisplayName),
            () => setting.AcceptDraft(revision), confirmedCommands: [change.Command]);
    }

    public Task ToggleMuteAsync(int transmitterIndex)
    {
        if (transmitterIndex < 0 || transmitterIndex >= Transmitters.Count)
            throw new ArgumentOutOfRangeException(nameof(transmitterIndex));
        if (busy || closed) return Task.CompletedTask;
        if (!RequireConnectedMicrophone()) return Task.CompletedTask;
        if (!Transmitters[transmitterIndex].CanMute)
        {
            SetResult(Transmitters[transmitterIndex].IsConnected
                ? Localization.Text("Main.Mute.Unavailable")
                : Localization.Text("Main.Mute.ConnectSelected", transmitterIndex + 1), true);
            return Task.CompletedTask;
        }
        MicrophoneControlScope scope = MicrophoneControlScope.Individual(transmitterIndex);
        return RunSessionOperationAsync(Localization.Text("Main.Mute.Pending", transmitterIndex + 1),
            async (session, _, token) =>
            {
                A1Status status = await session.ReadStatusAsync(token);
                scope.Validate(status, SettingChange.Mute(transmitterIndex, true));
                bool muted = !status.Transmitters[transmitterIndex].Muted;
                return await session.ApplyAsync(SettingChange.Mute(transmitterIndex, muted), token, scope,
                    live => live.Transmitters[transmitterIndex].Muted == status.Transmitters[transmitterIndex].Muted);
            }, Localization.Text("Main.Hotkey.MuteConfirmed"),
            observeSettings: true,
            successMessage: status => Localization.Text(status.Transmitters[transmitterIndex].Muted
                ? "Main.Mute.Confirmed" : "Main.Mute.Unmuted", transmitterIndex + 1));
    }

    private async Task RunSessionOperationAsync(string pending,
        Func<A1Controller, long, CancellationToken, Task<A1Status>> action, string success, Action? confirmed = null,
        string? failureWarning = null, bool observeSettings = false, Func<A1Status, string>? successMessage = null,
        IReadOnlyCollection<A1Command>? confirmedCommands = null)
    {
        if (closed || busy || controller is null || currentStatus is null || sessionCancellation is null) return;
        var previousFocus = CaptureOperationFocus();
        A1Controller session = controller;
        long expectedGeneration = generation;
        long expectedOperation = ++operation;
        CancellationToken token = sessionCancellation.Token;
        busy = true;
        UpdateView();
        SetResult(pending);
        long initialBatteryRevision = batteryAlertRevision;
        try
        {
            A1Status state = await action(session, expectedGeneration, token);
            if (!IsSession(session, expectedGeneration) || operation != expectedOperation) return;
            confirmed?.Invoke();
            if (observeSettings)
            {
                ObserveReadOnlySettings(state);
                ShowState(state, rememberSettings: false);
            }
            else
            {
                ShowState(state, confirmedCommands: confirmedCommands);
            }
            string confirmedResult = successMessage?.Invoke(state) ?? success;
            string result = retention.LastError is not null && preferences.PreserveVolatileSettings
                ? confirmedResult + " " + Localization.Text("Retention.StoreError", retentionStore.FilePath) : confirmedResult;
            bool retentionError = retention.LastError is not null && preferences.PreserveVolatileSettings;
            if (retentionError || batteryAlertRevision == initialBatteryRevision) SetResult(result, retentionError);
            else Log(result);
        }
        catch (MicrophoneScopeException exception)
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation)
            {
                DiscardControlTargetDrafts();
                ObserveReadOnlySettings(exception.Status);
                ShowState(exception.Status, rememberSettings: false);
                SetResult(ScopeFailureMessage(exception) + (failureWarning is null ? string.Empty : " " + failureWarning), true);
            }
        }
        catch (DeviceStateChangedException exception)
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation)
            {
                DiscardControlTargetDrafts();
                ObserveReadOnlySettings(exception.Status);
                ShowState(exception.Status, rememberSettings: false);
                SetResult(Localization.Text("Main.Change.Concurrent") + (failureWarning is null ? string.Empty : " " + failureWarning), true);
            }
        }
        catch (DeviceWriteSafetyException exception)
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation)
            {
                pendingAutomaticChanges.Clear();
                autoApplyTimer.Stop();
                foreach (SettingRowViewModel setting in AllSettings) setting.DiscardDraft();
                ObserveReadOnlySettings(exception.Status);
                ShowState(exception.Status, rememberSettings: false);
                SetResult(DeviceWriteSafetyMessage(exception) + (failureWarning is null ? string.Empty : " " + failureWarning), true);
            }
        }
        catch (Exception exception)
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation)
            {
                HandleSessionError(exception);
                if (failureWarning is not null) SetResult(ResultText + " " + failureWarning, true);
            }
        }
        finally
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation) { busy = false; UpdateView(); }
            RestoreOperationFocus(previousFocus);
        }
    }

    private async Task PollAsync()
    {
        if (closed || busy || polling || controller is null || currentStatus is null || sessionCancellation is null) return;
        A1Controller session = controller;
        long expectedGeneration = generation;
        long expectedOperation = operation;
        CancellationToken token = sessionCancellation.Token;
        polling = true;
        try
        {
            A1Status state = await ReadSnapshotAsync(session, expectedGeneration, token);
            if (IsSession(session, expectedGeneration) && operation == expectedOperation && !busy)
                await AcceptObservedStateAsync(state, session, expectedGeneration, token);
        }
        catch (Exception exception)
        {
            if (IsSession(session, expectedGeneration) && operation == expectedOperation) HandleSessionError(exception);
        }
        finally
        {
            if (IsSession(session, expectedGeneration)) polling = false;
        }
    }

    private bool IsCurrent(long expectedGeneration, long expectedOperation) =>
        !closed && generation == expectedGeneration && operation == expectedOperation;

    private bool IsSession(A1Controller session, long expectedGeneration) =>
        !closed && generation == expectedGeneration && ReferenceEquals(session, controller);

    public void Disconnect()
    {
        if (closed || sessionCancellation is null) return;
        var previousFocus = CaptureOperationFocus();
        bool interrupted = busy;
        string? receiverPath = sessionReceiver?.Path;
        EndSession();
        failedReceiverPath = receiverPath;
        nextReceiverRetry = DateTimeOffset.MaxValue;
        UpdateView();
        SetResult(interrupted
            ? Localization.Text("Main.Disconnect.Interrupted")
            : Localization.Text("Main.Disconnect.Success"));
        RestoreOperationFocus(previousFocus);
    }

    private void EndSession(bool receiverRemoved = false)
    {
        deferredRetention = null;
        if (sessionReceiver is { } previousReceiver)
        {
            if (receiverRemoved) retention.MarkDisconnected(previousReceiver.Path);
            else retention.ResetObservation(previousReceiver.Path);
        }
        generation++;
        controlContextGeneration++;
        targetInitializedForSession = false;
        lastConnectedTargets = null;
        operation++;
        pollTimer.Stop();
        autoApplyTimer.Stop();
        pendingAutomaticChanges.Clear();
        A1Controller? session = controller;
        CancellationTokenSource? cancellation = sessionCancellation;
        controller = null;
        sessionCancellation = null;
        sessionReceiver = null;
        if (session is not null && traceHandler is not null) session.Trace -= traceHandler;
        traceHandler = null;
        currentStatus = null;
        versions.Clear();
        identities.Clear();
        busy = false;
        polling = false;
        foreach (SettingRowViewModel setting in AllSettings) setting.DiscardDraft();
        try { cancellation?.Cancel(); }
        catch (Exception exception) { Log(Localization.Text("Main.Log.CancelError", exception.GetType().Name, exception.Message)); }
        try { session?.Dispose(); }
        catch (Exception exception) { Log(Localization.Text("Main.Log.CloseError", exception.GetType().Name, exception.Message)); }
        finally { cancellation?.Dispose(); }
    }

    private void HandleSessionError(Exception exception)
    {
        Log(Localization.Text("Main.Log.SessionError", exception.GetType().Name, exception.Message));
        string? receiverPath = sessionReceiver?.Path;
        EndSession();
        string message = exception switch
        {
            TimeoutException => Localization.Text("Main.Session.Timeout"),
            UnauthorizedAccessException => Localization.Text("Main.Session.AccessDenied"),
            InvalidDataException => Localization.Text("Main.Session.InvalidResponse"),
            OperationCanceledException or ObjectDisposedException => Localization.Text("Main.Session.Cancelled"),
            _ => Localization.Text("Main.Session.Failed")
        };
        bool repeated = SameReceiver(failedReceiverPath, receiverPath) && lastReceiverError == message;
        if (!SameReceiver(failedReceiverPath, receiverPath)) receiverFailures = 0;
        failedReceiverPath = receiverPath;
        lastReceiverError = message;
        receiverFailures = Math.Min(receiverFailures + 1, 5);
        nextReceiverRetry = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 1 << receiverFailures));
        UpdateView();
        if (!repeated) SetResult(Localization.Text("Main.Session.Closed", message), true);
    }

    private void ShowState(A1Status state, bool rememberSettings = true, IReadOnlyCollection<A1Command>? confirmedCommands = null)
    {
        A1Status? previous = currentStatus;
        currentStatus = state;
        if (rememberSettings) RememberConfirmedSettings(state, confirmedCommands);
        lastConfirmedRead = DateTimeOffset.Now;
        foreach (int device in versions.Keys.Where(device => device > 0 &&
            (device > state.Transmitters.Count || !state.Transmitters[device - 1].Connected)).ToArray())
            versions.Remove(device);
        foreach (int device in identities.Keys.Where(device => device > 0 &&
            (device > state.Transmitters.Count || !state.Transmitters[device - 1].Connected)).ToArray())
            identities.Remove(device);
        UpdateView();
        if (!busy) AnnounceConfirmedChanges(previous, state);
        ObserveBatteryLevels(state);
    }

    private void UpdateView()
    {
        if (closed) return;
        UpdateReceiverActions();
        int transmitterCount = currentStatus?.Transmitters.Count ?? 2;
        while (Transmitters.Count < transmitterCount) Transmitters.Add(new TransmitterViewModel(Transmitters.Count));
        while (Transmitters.Count > transmitterCount) Transmitters.RemoveAt(Transmitters.Count - 1);
        for (int index = 0; index < Transmitters.Count; index++)
            Transmitters[index].Update(currentStatus?.Transmitters.ElementAtOrDefault(index), versions.GetValueOrDefault(0), busy);
        UpdateTransmitterTargets();
        UpdateMicrophoneVisibility();
        foreach (SettingRowViewModel setting in AllSettings) setting.Update(currentStatus, busy, SharedControlUnavailableReason());
        string connection = GetUsbConnectionText();
        ConnectionReadout.Text = connection;
        ReadoutStatus.Text = currentStatus is null ? Localization.Text("Main.Readout.None")
            : Localization.Text("Main.Readout.Confirmed", lastConfirmedRead?.ToString("HH:mm:ss", Localization.Culture) ?? string.Empty);
        informationWindow?.UpdateContent(BuildSummary(), string.Join(Environment.NewLine, logLines), IsConnected && !busy);
        firmwareWindow?.UpdateContent(BuildFirmwareSummary());
        settingsWindow?.RefreshReceiverAvailability();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateMicrophoneVisibility()
    {
        foreach (TransmitterViewModel transmitter in Transmitters)
            transmitter.RowVisibility = preferences.HiddenRows.Contains($"TX{transmitter.Index + 1}") ? Visibility.Collapsed : Visibility.Visible;
        bool microphonesVisible = !preferences.HiddenRows.Contains("Microphones")
            && Transmitters.Any(transmitter => transmitter.RowVisibility == Visibility.Visible);
        MicrophonesPanel.Visibility = microphonesVisible ? Visibility.Visible : Visibility.Collapsed;
        if (TransmitterList.SelectedItem is TransmitterViewModel { RowVisibility: not Visibility.Visible })
        {
            TransmitterList.SelectedItem = allMicrophones;
            TransmitterList.ScrollIntoView(allMicrophones);
        }
        EmptyControlsReadout.Visibility = !microphonesVisible && AllSettings.All(setting => preferences.HiddenRows.Contains(setting.Id))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private TrayControlSnapshot ReadTraySnapshot() => new(IsConnected && !busy && !closed,
        currentStatus?.Gain, !closed && AudioSettings.First(setting => setting.Id == "Gain").IsEnabled,
        Transmitters.Select(transmitter => new TrayTransmitterSnapshot(transmitter.Index,
            transmitter.IsConnected, transmitter.IsMuted, !closed && transmitter.CanMute)).ToArray())
        { ContextGeneration = controlContextGeneration };

    private Task AdjustGainFromTrayAsync(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        SettingRowViewModel gain = AudioSettings.First(setting => setting.Id == "Gain");
        if (currentStatus is null || !gain.IsEnabled || closed) return Task.CompletedTask;
        RestoreFromTray();
        if (!TryGetSharedControlScope(out MicrophoneControlScope? scope)) return Task.CompletedTask;
        pendingAutomaticChanges.Remove(gain);
        gain.DiscardDraft();
        return RunSessionOperationAsync(Localization.Text("Main.Apply.Pending", gain.DisplayName), async (session, _, token) =>
        {
            A1Status status = await session.ReadStatusAsync(token);
            SettingChange change = SettingChange.Gain(Math.Clamp(status.Gain + direction, 0, 5));
            scope!.Validate(status, change);
            return change.IsConfirmed(status) ? status : await session.ApplyAsync(change, token, scope,
                live => live.Gain == status.Gain);
        }, Localization.Text("Main.Apply.Success", gain.DisplayName), confirmedCommands: [A1Command.SetGain]);
    }

    private Task ToggleMuteFromTrayAsync(int index)
    {
        if (!closed) RestoreFromTray();
        return ToggleMuteAsync(index);
    }

    private string BuildSummary()
    {
        var summary = new StringBuilder();
        summary.AppendLine(Localization.Text("Main.Summary.Version", typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? string.Empty));
        summary.AppendLine(ConnectionReadout.Text);
        summary.AppendLine(Localization.Text("Main.Firmware.Receiver", versions.GetValueOrDefault(0)?.ToString() ?? Localization.Text("Main.Firmware.NotRead")));
        AppendIdentity(summary, 0, Localization.Text("Main.Receiver.Name"));
        summary.AppendLine(Localization.Text("Main.Summary.RadioModeCode",
            currentStatus?.RadioModeCode?.ToString(Localization.Culture) ?? Localization.Text("Main.Summary.RadioModeUnknown")));
        foreach (TransmitterViewModel transmitter in Transmitters)
        {
            summary.AppendLine(transmitter.Summary);
            summary.AppendLine(Localization.Text("Main.Firmware.Transmitter", transmitter.Index + 1,
                versions.GetValueOrDefault(transmitter.Index + 1)?.ToString() ?? Localization.Text("Main.Firmware.NotRead")));
            AppendIdentity(summary, transmitter.Index + 1, Localization.Text("Transmitter.Name", transmitter.Index + 1));
        }
        foreach (SettingRowViewModel setting in AllSettings)
            summary.AppendLine(Localization.Text("Setting.Value", setting.DisplayName, setting.ConfirmedValue));
        return summary.ToString();
    }

    private string BuildFirmwareSummary()
    {
        var summary = new StringBuilder(ConnectionReadout.Text);
        summary.AppendLine();
        summary.AppendLine(Localization.Text("Main.Firmware.Receiver", versions.GetValueOrDefault(0)?.ToString() ?? Localization.Text("Main.Firmware.NotRead")));
        foreach (TransmitterViewModel transmitter in Transmitters)
            summary.AppendLine(Localization.Text("Main.Firmware.Transmitter", transmitter.Index + 1,
                versions.GetValueOrDefault(transmitter.Index + 1)?.ToString() ?? Localization.Text("Main.Firmware.NotRead")));
        return summary.ToString();
    }

    private void AppendIdentity(StringBuilder summary, int device, string label)
    {
        if (identities.TryGetValue(device, out var identity))
            summary.AppendLine(Localization.Text("Main.Identity.Readout", label, identity.Serial ?? Localization.Text("Main.Identity.Unavailable"), identity.Mac ?? Localization.Text("Main.Identity.Unavailable")));
        else summary.AppendLine(Localization.Text("Main.Identity.NotRead", label));
    }

    internal Task ReadIdentitiesAsync() => RunSessionOperationAsync(Localization.Text("Main.Identity.Pending"),
        async (session, expectedGeneration, token) =>
        {
            A1Status state = await ReadSnapshotAsync(session, expectedGeneration, token);
            var fresh = new Dictionary<int, (string? Serial, string? Mac)>();
            for (int device = 0; device <= state.Transmitters.Count; device++)
            {
                if (device > 0 && !state.Transmitters[device - 1].Connected) continue;
                string? serial = await session.ReadSerialNumberAsync(device, token);
                string? mac = await session.ReadMacAddressAsync(device, token);
                fresh[device] = (serial, mac);
            }
            if (IsSession(session, expectedGeneration))
            {
                identities.Clear();
                foreach (var entry in fresh) identities.Add(entry.Key, entry.Value);
            }
            return state;
        }, Localization.Text("Main.Identity.Success"), observeSettings: true);

    private void SetResult(string message, bool important = false, bool announce = true, bool batteryWarning = false)
    {
        if (closed) return;
        AutomationProperties.SetLiveSetting(ResultReadout, AutomationLiveSetting.Off);
        resultCanAnnounce = announce;
        resultIsBatteryWarning = batteryWarning;
        UiSupport.UpdateReadout(ResultReadout, message);
        AutomationProperties.SetName(ResultReadout, Localization.Text("Main.Result.AccessibleName", message));
        resultIsImportant = important;
        UpdateResultPresentation();
        Log(message);
        if (announce) AnnounceOperationResult(message, important);
    }

    private bool RequireConnectedMicrophone()
    {
        if (IsConnected && currentStatus!.Transmitters.Any(transmitter => transmitter.Connected)) return true;
        SetResult(Localization.Text("Main.Change.TransmitterRequired"), true);
        return false;
    }

    private static string DeviceWriteSafetyMessage(DeviceWriteSafetyException exception) =>
        exception.TransmitterIndex is int index
            ? Localization.Text("Main.Mute.ConnectSelected", index + 1)
            : Localization.Text("Main.Change.TransmitterRequired");

    private void QueueTrace(A1Controller session, long expectedGeneration, string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsSession(session, expectedGeneration)) Log(message);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void Log(string message)
    {
        if (closed) return;
        logLines.Enqueue(Localization.Text("Main.Log.Entry", DateTime.Now, message));
        while (logLines.Count > 200) logLines.Dequeue();
        informationWindow?.UpdateContent(BuildSummary(), string.Join(Environment.NewLine, logLines), IsConnected && !busy);
    }

    public Window OpenSettingsWindow()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (settingsWindow is not null) { RestoreAndActivate(settingsWindow); return settingsWindow; }
        var window = new SettingsWindow(preferences, preferencesStore, (updated, warning) =>
        {
            preferences = updated;
            ApplyViewPreferences();
            SetResult(warning ?? Localization.Text("Main.Preferences.Saved"), warning is not null);
        }, apply: SaveApplicationPreferences, readStatus: () => IsConnected && !busy ? currentStatus : null,
            applyProfile: ApplyAudioProfileAsync, confirm: confirmAction, refreshStatus: ReadProfileStatusAsync,
            canUseReceiver: () => IsConnected && !busy, receiverGeneration: () => controlContextGeneration);
        settingsWindow = window;
        if (globalHotkeys is not null)
        {
            HotkeyOperationResult result = globalHotkeys.IsSuspended
                ? globalHotkeys.Configure(preferences.GlobalHotkeysEnabled, preferences.Hotkeys) : globalHotkeys.Suspend();
            window.SetHotkeyCaptureAvailable(result.Succeeded && globalHotkeys.ActiveBindings.Count == 0);
            if (!result.Succeeded)
            {
                hotkeyConfigurationValid = false;
                ReportHotkeyFailure(result);
            }
        }
        ShowSection(window, () => { settingsWindow = null; ResumeApplicationHotkeys(); });
        return window;
    }

    public Window OpenInformationWindow()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (informationWindow is not null) { RestoreAndActivate(informationWindow); return informationWindow; }
        var window = new InfoWindow(SaveDiagnostics, ReadIdentitiesAsync);
        informationWindow = window;
        window.UpdateContent(BuildSummary(), string.Join(Environment.NewLine, logLines), IsConnected && !busy);
        ShowSection(window, () => informationWindow = null);
        return window;
    }

    public Window OpenHelpWindow()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (helpWindow is not null) { RestoreAndActivate(helpWindow); return helpWindow; }
        var window = new HelpWindow();
        helpWindow = window;
        ShowSection(window, () => helpWindow = null);
        return window;
    }

    public Window OpenAudioMeterWindow()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (audioMeterWindow is not null) { RestoreAndActivate(audioMeterWindow); return audioMeterWindow; }
        var window = new AudioMeterWindow(() => currentStatus?.ChannelMode, reportError: message => SetResult(message, true));
        audioMeterWindow = window;
        ShowSection(window, () => audioMeterWindow = null);
        return window;
    }

    public Window OpenFirmwareWindow()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (firmwareWindow is not null) { RestoreAndActivate(firmwareWindow); return firmwareWindow; }
        var window = new FirmwareWindow();
        firmwareWindow = window;
        window.UpdateContent(BuildFirmwareSummary());
        ShowSection(window, () => firmwareWindow = null);
        return window;
    }

    private async Task<A1Status?> ReadProfileStatusAsync()
    {
        if (!IsConnected || busy) return null;
        A1Controller originalSession = controller!;
        long expectedGeneration = generation;
        Task pendingRead = RunSessionOperationAsync(Localization.Text("Main.Profile.ReadPending"), ReadSnapshotAsync,
            Localization.Text("Main.Profile.ReadSuccess"), observeSettings: true);
        long expectedOperation = operation;
        await pendingRead;
        return IsSession(originalSession, expectedGeneration) && operation == expectedOperation && IsConnected ? currentStatus : null;
    }


    private void ShowSection(AuxiliaryWindow window, Action release)
    {
        IInputElement? returnFocus = lastContentFocus;
        bool restoreFocus = false;
        window.Owner = this;
        window.ApplyPreferences(preferences);
        window.Closing += (_, _) => restoreFocus = window.IsActive;
        window.Closed += (_, _) =>
        {
            release();
            UpdateReceiverActions();
            if (closed || !restoreFocus) return;
            long expectedInteraction = focusInteraction;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (closed || !IsActive || OwnedWindows.Count > 0 || focusInteraction != expectedInteraction) return;
                if (returnFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous)
                    previous.Focus();
                else ConnectionReadout.Focus();
            }));
        };
        window.Show();
        UpdateReceiverActions();
    }

    private string SaveDiagnostics()
    {
        if (closed) return Localization.Text("Main.Window.Closed");
        try
        {
            Directory.CreateDirectory(preferencesStore.ReportsDirectory);
            string path = Path.Combine(preferencesStore.ReportsDirectory, $"Hollylark-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(DateTimeOffset.Now.ToString("O"));
                writer.WriteLine(BuildSummary());
                writer.WriteLine(string.Join(Environment.NewLine, logLines));
            }
            Log(Localization.Text("Main.Diagnostics.Saved", path));
            return Localization.Text("Main.Diagnostics.Saved", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log(Localization.Text("Main.Log.DiagnosticsError", exception.GetType().Name, exception.Message));
            return Localization.Text("Main.Diagnostics.Failed", preferencesStore.ReportsDirectory);
        }
    }

    private async void OnScan(object sender, ExecutedRoutedEventArgs eventArgs) => await ScanAsync();
    private async void OnRefresh(object sender, ExecutedRoutedEventArgs eventArgs) => await RefreshAsync();
    private void CanScan(object sender, CanExecuteRoutedEventArgs eventArgs) => eventArgs.CanExecute = !closed && receiverScanTask.IsCompleted;
    private void CanRefresh(object sender, CanExecuteRoutedEventArgs eventArgs) => eventArgs.CanExecute = !closed && !busy && (IsConnected || automaticReceiverManagement);
    private void OnOpenSettings(object sender, RoutedEventArgs eventArgs) => OpenSettingsWindow();
    private void OnOpenInformation(object sender, RoutedEventArgs eventArgs) => OpenInformationWindow();
    private void OnOpenHelp(object sender, RoutedEventArgs eventArgs) => OpenHelpWindow();
    private void OnOpenAudioMeter(object sender, RoutedEventArgs eventArgs) => OpenAudioMeterWindow();
    private void OnOpenFirmware(object sender, RoutedEventArgs eventArgs) => OpenFirmwareWindow();
    private void OnExit(object sender, RoutedEventArgs eventArgs) => RequestExit();

    private async void OnApplySetting(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { DataContext: SettingRowViewModel setting }) await ApplySettingAsync(setting);
    }

    private async void OnToggleMute(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { DataContext: TransmitterViewModel transmitter })
        {
            if (transmitter.IsGroup) await ToggleAllMicrophonesMuteAsync();
            else await ToggleMuteAsync(transmitter.Index);
        }
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        closed = true;
        try { screenReader?.Dispose(); }
        catch (Exception exception) when (UiNotificationRouting.IsSpeechFailure(exception))
        {
            Log(Localization.Text("Main.Log.AccessibilityError", exception.Message));
        }
        globalHotkeys?.Dispose();
        receiverTimer.Stop();
        EndSession();
        try { desktopIntegration.Dispose(); }
        catch (Exception exception) { OnDesktopFailure(exception); }
        if (desktopIntegration is DesktopIntegration nativeDesktop) nativeDesktop.Failed -= OnDesktopFailure;
        base.OnClosed(eventArgs);
    }
}

public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<TValue>(ref TValue field, TValue value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed partial class SettingRowViewModel : ObservableViewModel
{
    private readonly Func<int, SettingChange> create;
    private readonly Func<A1Status, int?> read;
    private readonly Func<A1Status, string?> availability;
    private int selectedIndex = -1;
    private bool isDirty;
    private bool isEnabled;
    private bool operationPending;
    private bool hasConnectedMicrophones;
    private string confirmedValue = Localization.Text("Setting.NoData");
    private string statusText = string.Empty;
    private string? unavailableReason;

    public string Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> Options { get; }
    public string CurrentName => Localization.Text("Setting.CurrentName", DisplayName);
    public string ChoiceName => Localization.Text("Setting.ChoiceName", DisplayName);
    public string ApplyName => Localization.Text("Setting.ApplyName", DisplayName);
    public string StatusName => Localization.Text("Setting.StatusName", DisplayName);
    public string CurrentId => Id + ".Current";
    public string ChoiceId => Id + ".Choice";
    public string ApplyId => Id + ".Apply";
    public string StatusId => Id + ".Status";
    public bool IsDirty => isDirty;
    public bool IsEnabled => isEnabled;
    public bool CanApply => isEnabled && selectedIndex >= 0 && selectedIndex < Options.Count;
    public bool CanEdit => !operationPending;
    public bool CanRequestApply => !operationPending;
    public override string ToString() => DisplayName;
    public string ConfirmedValue => confirmedValue;
    public string StatusText => statusText;
    public string? UnavailableReason => unavailableReason;
    internal long Revision { get; private set; }
    internal Func<bool>? EditRequested { get; set; }
    internal bool RequestEdit() => EditRequested?.Invoke() ?? IsEnabled;

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value < -1 || value >= Options.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == selectedIndex || !RequestEdit()) return;
            if (!SetField(ref selectedIndex, value)) return;
            Revision++;
            SetField(ref isDirty, true, nameof(IsDirty));
            RaisePropertyChanged(nameof(CanApply));
            UpdateStatus();
            NotifyPresentation();
            DraftChanged?.Invoke(this);
        }
    }

    internal SettingRowViewModel(string id, string displayName, string[] options, Func<int, SettingChange> create,
        Func<A1Status, int?> read, Func<A1Status, string?> availability)
    {
        Id = id;
        DisplayName = displayName;
        Options = Array.AsReadOnly(options);
        this.create = create;
        this.read = read;
        this.availability = availability;
    }

    internal void Update(A1Status? state, bool busy, string? scopeUnavailable = null)
    {
        int confirmedIndex = state is null ? -1 : read(state) ?? -1;
        bool known = confirmedIndex >= 0 && confirmedIndex < Options.Count;
        SetField(ref confirmedValue, known ? Options[confirmedIndex] : Localization.Text("Setting.NoData"), nameof(ConfirmedValue));
        if (!isDirty || state is null)
            SetField(ref selectedIndex, known ? confirmedIndex : -1, nameof(SelectedIndex));
        hasConnectedMicrophones = state?.Transmitters.Any(transmitter => transmitter.Connected) == true;
        string? reason = !hasConnectedMicrophones ? Localization.Text("Main.Change.TransmitterRequired")
            : scopeUnavailable ?? availability(state!) ?? (known ? null : Localization.Text("Setting.Unavailable"));
        SetField(ref unavailableReason, reason, nameof(UnavailableReason));
        operationPending = busy;
        SetField(ref isEnabled, reason is null && !busy, nameof(IsEnabled));
        RaisePropertyChanged(nameof(CanApply));
        RaisePropertyChanged(nameof(CanEdit));
        RaisePropertyChanged(nameof(CanRequestApply));
        UpdateStatus();
        NotifyPresentation();
    }

    internal SettingChange CreateChange() => create(selectedIndex);

    internal void DiscardDraft()
    {
        Revision++;
        SetField(ref isDirty, false, nameof(IsDirty));
        UpdateStatus();
    }

    internal void AcceptDraft(long expectedRevision)
    {
        if (Revision == expectedRevision) DiscardDraft();
    }

    private void UpdateStatus()
    {
        string reason = unavailableReason ?? (operationPending ? Localization.Text("Setting.Pending") : string.Empty);
        if (isDirty) reason = (reason.Length == 0 ? string.Empty : reason + " ") + Localization.Text("Setting.Unapplied");
        SetField(ref statusText, reason, nameof(StatusText));
        RaisePropertyChanged(nameof(ApplyVisibility));
    }
}

public sealed partial class TransmitterViewModel : ObservableViewModel
{
    private static readonly Version MinimumMuteVersion = new(1, 0, 1, 3);
    private string summary = string.Empty;
    private string actionText = Localization.Text("Transmitter.Mute");
    private bool canMute;
    private bool canRequestMute;
    private bool connected;
    private bool muted;
    private Visibility rowVisibility = Visibility.Visible;

    public Visibility RowVisibility { get => rowVisibility; internal set => SetField(ref rowVisibility, value); }

    public int Index { get; }
    public bool IsGroup => Index < 0;
    public string Summary => summary;
    public string ActionText => actionText;
    public bool CanMute => canMute;
    public bool CanRequestMute => canRequestMute;
    public bool IsConnected => connected;
    public bool IsMuted => muted;
    public string ReadoutName => IsGroup ? Localization.Text("Target.All.Name") : Localization.Text("Transmitter.ReadoutName", Index + 1);
    public string ActionName => IsGroup ? Localization.Text(muted ? "Target.All.Unmute" : "Target.All.Mute")
        : muted ? Localization.Text("Transmitter.UnmuteName", Index + 1) : Localization.Text("Transmitter.MuteName", Index + 1);
    public string ReadoutId => IsGroup ? "AllTX.Status" : $"TX{Index + 1}.Status";
    public string ActionId => IsGroup ? "AllTX.Mute" : $"TX{Index + 1}.Mute";
    public string ItemId => IsGroup ? "AllTX.Item" : $"TX{Index + 1}.Item";
    public override string ToString() => Summary;

    internal TransmitterViewModel(int index) => Index = index;

    internal void Update(TransmitterState? state, Version? receiverVersion, bool busy)
    {
        bool supportsMute = receiverVersion is not null && receiverVersion >= MinimumMuteVersion;
        SetField(ref connected, state?.Connected == true, nameof(IsConnected));
        SetField(ref muted, state?.Connected == true && state.Muted, nameof(IsMuted));
        SetField(ref canMute, connected && supportsMute && !busy, nameof(CanMute));
        SetField(ref canRequestMute, !busy, nameof(CanRequestMute));
        SetField(ref actionText, muted ? Localization.Text("Transmitter.Unmute") : Localization.Text("Transmitter.Mute"), nameof(ActionText));
        RaisePropertyChanged(nameof(ActionName));
        string status = state is null ? Localization.Text("Transmitter.NoReadout")
            : !connected ? Localization.Text("Transmitter.Disconnected")
            : Localization.Text("Transmitter.Connected", state.BatteryPercent is int percent
                ? Localization.Text("Transmitter.BatteryPercent", percent) : Localization.Text("Transmitter.BatteryUnknown"),
                Localization.Text(muted ? "Transmitter.Muted" : "Transmitter.Active"));
        if (connected && !supportsMute)
            status = Localization.Text(receiverVersion is null ? "Transmitter.Mute.NeedsVersion"
                : "Transmitter.Mute.NeedsFirmware", status);
        SetField(ref summary, Localization.Text("Transmitter.Summary", Index + 1, status), nameof(Summary));
    }
}

internal static class UiSupport
{
    internal static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri($"/{typeof(MainWindow).Assembly.GetName().Name};component/Styles.xaml", UriKind.Relative)
        };
        Localization.ApplyResources(resources);
        return resources;
    }

    internal static void UpdateReadout(TextBox readout, string text)
    {
        if (readout.Text == text) return;
        int selectionStart = readout.SelectionStart;
        int selectionLength = readout.SelectionLength;
        double verticalOffset = readout.VerticalOffset;
        double horizontalOffset = readout.HorizontalOffset;
        readout.Text = text;
        int start = Math.Min(selectionStart, text.Length);
        readout.Select(start, Math.Min(selectionLength, text.Length - start));
        readout.ScrollToVerticalOffset(verticalOffset);
        readout.ScrollToHorizontalOffset(horizontalOffset);
    }

    internal static void UpdateReadout(TextBlock readout, string text)
    {
        if (readout.Text != text) readout.Text = text;
    }

    internal static void Announce(UIElement readout)
    {
        if (!readout.IsVisible) return;
        try
        {
            AutomationPeer? peer = UIElementAutomationPeer.FromElement(readout)
                ?? UIElementAutomationPeer.CreatePeerForElement(readout);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            System.Diagnostics.Trace.WriteLine(exception);
        }
    }
}
