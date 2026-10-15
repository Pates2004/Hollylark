using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyAudioFollowupChecks()
    {
        Check(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(),
            "Audio follow-up checks reuse the current WPF application and dispatcher");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyAudioStartCleanup(false, false);
                VerifyAudioStartCleanup(true, false);
                VerifyAudioStartCleanup(false, true);
                VerifyAudioStartCleanup(true, true);
                VerifyAudioCallbackGenerations();
                VerifyAudioCallbacksAfterClose();
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyAudioStartCleanup(bool cleanupFails, bool emitStartupCallbacks)
    {
        using var source = new FollowupAudioMeterSource
        {
            StartFailure = true, StopFailure = cleanupFails, EmitDuringFailedStart = emitStartupCallbacks
        };
        var cleanupErrors = new List<string>();
        var window = new AudioMeterWindow(() => null, source, cleanupErrors.Add);
        window.ApplyPreferences(new UiPreferences { ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false });
        MoveOffscreen(window); window.Show(); Pump();
        try
        {
            ComboBox inputs = FindById<ComboBox>(window, "AudioInputChoice");
            Button start = FindById<Button>(window, "StartAudioMeter");
            Button stop = FindById<Button>(window, "StopAudioMeter");
            Button scan = FindById<Button>(window, "ScanAudioInputs");
            TextBlock result = FindById<TextBlock>(window, "SectionResultReadout");
            TextBlock reading = FindById<TextBlock>(window, "AudioLevelsReadout");
            ProgressBar first = FindById<ProgressBar>(window, "AudioLevel1");
            ProgressBar second = FindById<ProgressBar>(window, "AudioLevel2");
            inputs.SelectedIndex = 0;
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(source.Stops == 1 && source.HasLiveInput == cleanupFails,
                "Startup cleanup is immediate and does not depend on a source failure event or dispatcher turn");
            Pump();
            string startError = Localization.Text("Meter.StartFailed", FollowupAudioMeterSource.StartError);
            string stopError = Localization.Text("Meter.StopFailed", FollowupAudioMeterSource.StopError);
            string expectedResult = cleanupFails ? Localization.Text("Meter.CombinedError", startError, stopError) : startError;
            Check(source.Starts == 1 && source.Stops == 1 && source.HasLiveInput == cleanupFails,
                "A throwing audio start always attempts to release a partially opened input");
            Check(result.IsVisible && ReadAccessibleValue(result) == expectedResult
                && GetAccessiblePeer(result).GetName() == Localization.Text("Section.Result.WithMessage", expectedResult)
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Start and cleanup errors remain exact, accessible and visible with optional messages disabled");
            Check(reading.Text == (cleanupFails ? Localization.Text("Meter.DisplayInterrupted", stopError) : Localization.Text("Meter.Stopped"))
                && first.Value == AudioLevelCalculator.SilenceDecibels && second.Value == AudioLevelCalculator.SilenceDecibels
                && !second.IsVisible,
                "Failed start clears synchronous samples and rejects its already queued failure callback");
            Check(start.IsEnabled == !cleanupFails && stop.IsEnabled == cleanupFails
                && inputs.IsEnabled == !cleanupFails && scan.IsEnabled == !cleanupFails,
                "Unconfirmed cleanup blocks a new capture and offers a stop retry instead");

            if (cleanupFails)
            {
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(source.Starts == 1, "An unconfirmed startup cleanup cannot be bypassed by a routed start action");
                source.EmitFailure("Input still unavailable");
                Pump();
                Check(source.Stops == 2 && result.Text == Localization.Text("Meter.CombinedError", "Input still unavailable", stopError)
                    && stop.IsEnabled && !start.IsEnabled,
                    "A failure reported while cleanup is pending preserves its detail and a retry action");
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(source.Stops == 3 && source.HasLiveInput && stop.IsEnabled && result.Text == stopError,
                    "A failed explicit stop retry retains the uncertain input state");
                source.StopFailure = false;
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(source.Stops == 4 && !source.HasLiveInput && !stop.IsEnabled && start.IsEnabled
                    && inputs.IsEnabled && scan.IsEnabled && reading.Text == Localization.Text("Meter.Stopped")
                    && result.Text == Localization.Text("Meter.StoppedResult") && result.Visibility == Visibility.Collapsed,
                    "Successful cleanup retry restores the stopped state without auto-start or optional announcements");
            }

            source.StartFailure = false;
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.Emit([-12, -18]);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(source.Starts == 2 && source.HasLiveInput && stop.IsEnabled && !start.IsEnabled
                && first.Value == -12 && second.Value == -18 && second.IsVisible,
                "Explicit restart after a failed start still accepts actual stereo samples");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { source.StopFailure = false; window.Close(); Pump(); }
        Check(source.Disposed && !source.HasLiveInput && cleanupErrors.Count == 0,
            "Failed-start regression releases only its fake input without a native error dialog");
    }

    private static void VerifyAudioCallbackGenerations()
    {
        using var source = new FollowupAudioMeterSource();
        var cleanupErrors = new List<string>();
        var window = new AudioMeterWindow(() => 0, source, cleanupErrors.Add);
        MoveOffscreen(window); window.Show(); Pump();
        try
        {
            FindById<ComboBox>(window, "AudioInputChoice").SelectedIndex = 0;
            Button start = FindById<Button>(window, "StartAudioMeter");
            Button stop = FindById<Button>(window, "StopAudioMeter");
            Button read = FindById<Button>(window, "ReadAudioLevels");
            TextBlock result = FindById<TextBlock>(window, "SectionResultReadout");
            TextBlock reading = FindById<TextBlock>(window, "AudioLevelsReadout");
            ProgressBar first = FindById<ProgressBar>(window, "AudioLevel1");
            ProgressBar second = FindById<ProgressBar>(window, "AudioLevel2");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FollowupAudioCallbacks previous = source.Sessions[0];
            previous.Failed?.Invoke("Queued failure from previous capture");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.Emit([-12, -18]);
            Task.Run(() =>
            {
                previous.Levels?.Invoke(SimulateAudioLevels(-1, -2, -3));
                previous.Failed?.Invoke("Delayed failure from previous capture");
            }).GetAwaiter().GetResult();
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(source.Starts == 2 && source.Stops == 1 && source.HasLiveInput && stop.IsEnabled && !start.IsEnabled,
                "Queued and newly delivered old failures cannot stop a later capture generation");
            string firstChannel = ExpectedAudioChannel(1, -12, -12, false);
            string secondChannel = ExpectedAudioChannel(2, -18, -18, false);
            Check(first.Value == -12 && second.Value == -18 && second.IsVisible
                && reading.Text == Localization.Text("Meter.Levels", 2,
                    string.Join(Localization.Text("Meter.ChannelSeparator"), firstChannel, secondChannel)),
                "Late samples from the old callback cannot replace the current capture's values or channel count");

            FollowupAudioCallbacks current = source.Sessions[1];
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Task.Run(() =>
            {
                previous.Levels?.Invoke(SimulateAudioLevels(-1, -2));
                current.Levels?.Invoke(SimulateAudioLevels(-3, -4));
                current.Failed?.Invoke("Delayed failure after stop");
            }).GetAwaiter().GetResult();
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(source.Stops == 2 && !source.HasLiveInput && !stop.IsEnabled && start.IsEnabled
                && first.Value == AudioLevelCalculator.SilenceDecibels && second.Value == AudioLevelCalculator.SilenceDecibels
                && !second.IsVisible && reading.Text == Localization.Text("Meter.Stopped") && result.Text == reading.Text,
                "Callbacks delivered after stop neither restore channels nor replace the stopped result");

            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Task.Run(() => source.EmitFailure("Current capture interrupted")).GetAwaiter().GetResult();
            Pump();
            Check(source.Starts == 3 && source.Stops == 3 && !source.HasLiveInput && !stop.IsEnabled && start.IsEnabled
                && result.Text == "Current capture interrupted" && reading.Text == Localization.Text("Meter.Stopped"),
                "A current-generation worker failure is still reported and releases its input");
        }
        finally { window.Close(); Pump(); }
        Check(source.Disposed && cleanupErrors.Count == 0, "Callback regressions release their fake source cleanly");
    }

    private static void VerifyAudioCallbacksAfterClose()
    {
        using var source = new FollowupAudioMeterSource();
        var cleanupErrors = new List<string>();
        var window = new AudioMeterWindow(() => null, source, cleanupErrors.Add);
        MoveOffscreen(window); window.Show(); Pump();
        try
        {
            FindById<ComboBox>(window, "AudioInputChoice").SelectedIndex = 0;
            FindById<Button>(window, "StartAudioMeter").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FollowupAudioCallbacks callbacks = source.Sessions[0];
            callbacks.Failed?.Invoke("Failure queued before closing");
            window.Close();
            Task.Run(() =>
            {
                callbacks.Levels?.Invoke(SimulateAudioLevels(-1, -2));
                callbacks.Failed?.Invoke("Failure delivered after closing");
            }).GetAwaiter().GetResult();
            Pump();
            Check(source.Disposed && !source.HasLiveInput && source.Stops == 1
                && source.LevelSubscribers == 0 && source.FailureSubscribers == 0 && cleanupErrors.Count == 0,
                "Closing invalidates pending callbacks and removes both subscriptions without another stop or dialog");
        }
        finally { if (!source.Disposed) window.Close(); Pump(); }
    }

    private sealed record FollowupAudioCallbacks(Action<AudioChannelLevel[]>? Levels, Action<string>? Failed);

    private sealed class FollowupAudioMeterSource : IAudioMeterSource
    {
        internal const string StartError = "Simulated partially opened input start failure";
        internal const string StopError = "Simulated input cleanup failure";
        public bool StartFailure { get; set; }
        public bool StopFailure { get; set; }
        public bool EmitDuringFailedStart { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool HasLiveInput { get; private set; }
        public bool Disposed { get; private set; }
        public List<FollowupAudioCallbacks> Sessions { get; } = [];
        public int LevelSubscribers => Levels?.GetInvocationList().Length ?? 0;
        public int FailureSubscribers => Failed?.GetInvocationList().Length ?? 0;
        public event Action<AudioChannelLevel[]>? Levels;
        public event Action<string>? Failed;
        public IReadOnlyList<AudioInput> GetInputs() => [new("fake-audio", "Simulated stereo input")];

        public void Start(string endpointId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
            Starts++;
            HasLiveInput = true;
            var callbacks = new FollowupAudioCallbacks(Levels, Failed);
            Sessions.Add(callbacks);
            if (!StartFailure) return;
            if (EmitDuringFailedStart)
            {
                callbacks.Levels?.Invoke(SimulateAudioLevels(-3, -6));
                callbacks.Failed?.Invoke("Failure callback during a throwing start");
            }
            throw new IOException(StartError);
        }

        public void Stop()
        {
            Stops++;
            if (StopFailure) throw new IOException(StopError);
            HasLiveInput = false;
        }

        public void Emit(double[] values) => Levels?.Invoke(SimulateAudioLevels(values));
        public void EmitFailure(string message) => Failed?.Invoke(message);

        public void Dispose()
        {
            if (Disposed) return;
            Stop();
            Disposed = true;
        }
    }
}
