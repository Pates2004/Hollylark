using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;

namespace LarkControl.Windows;

internal sealed class AudioMeterWindow : AuxiliaryWindow
{
    private readonly IAudioMeterSource source;
    private readonly Func<long> readTimestamp;
    private readonly ComboBox inputs;
    private readonly Button start;
    private readonly Button stop;
    private readonly Button scan;
    private readonly Button resetPeaks;
    private readonly TextBlock reading;
    private readonly ProgressBar first;
    private readonly ProgressBar second;
    private readonly StackPanel secondPanel;
    private readonly DispatcherTimer timer;
    private readonly object sampleLock = new();
    private Action<AudioChannelLevel[]>? levelsHandler;
    private Action<string>? failureHandler;
    private AudioChannelLevel[]? latest;
    private long lastSample;
    private bool running;
    private string? stopError;
    private bool closed;
    private long generation;

    internal AudioMeterWindow(Func<int?> channelMode, IAudioMeterSource? source = null, Action<string>? reportError = null,
        Func<long>? readTimestamp = null)
        : base(Localization.Text("Meter.Title"), "AudioMeterWindow")
    {
        this.source = source ?? new AudioMeterSource();
        this.readTimestamp = readTimestamp ?? (() => Environment.TickCount64);
        var panel = new StackPanel();
        var instruction = new TextBlock { Text = Localization.Text("Meter.Instruction"), Style = (Style)Resources["HintStyle"] };
        panel.Children.Add(instruction);
        inputs = Named(new ComboBox { DisplayMemberPath = nameof(AudioInput.Name), MinWidth = 280 }, "AudioInputChoice", Localization.Text("Meter.InputName"));
        panel.Children.Add(Label(Localization.Text("Meter.InputLabel"), inputs));
        panel.Children.Add(inputs);
        var buttons = new WrapPanel();
        scan = Button("ScanAudioInputs", Localization.Text("Meter.Scan"), Localization.Text("Meter.ScanName"));
        start = Button("StartAudioMeter", Localization.Text("Meter.Start"), Localization.Text("Meter.StartName"));
        stop = Button("StopAudioMeter", Localization.Text("Meter.Stop"), Localization.Text("Meter.StopName"));
        buttons.Children.Add(scan); buttons.Children.Add(start); buttons.Children.Add(stop);
        panel.Children.Add(buttons);
        first = Meter("AudioLevel1", Localization.Text("Meter.Channel1Name"));
        second = Meter("AudioLevel2", Localization.Text("Meter.Channel2Name"));
        panel.Children.Add(Label(Localization.Text("Meter.Channel1Label"), first));
        panel.Children.Add(first);
        secondPanel = new StackPanel();
        secondPanel.Children.Add(Label(Localization.Text("Meter.Channel2Label"), second));
        secondPanel.Children.Add(second);
        panel.Children.Add(secondPanel);
        reading = StaticReadout("AudioLevelsReadout", Localization.Text("Meter.LevelsName"));
        reading.TextWrapping = TextWrapping.Wrap;
        reading.MinHeight = 85;
        panel.Children.Add(Label(Localization.Text("Meter.LevelsLabel"), reading));
        panel.Children.Add(reading);
        var explain = new TextBlock { Style = (Style)FindResource("HintStyle"), Text = Localization.Text("Meter.LevelHint") };
        panel.Children.Add(explain);
        var levelActions = new WrapPanel();
        resetPeaks = Button("ResetAudioPeaks", Localization.Text("Meter.ResetPeaks"), Localization.Text("Meter.ResetPeaksName"));
        resetPeaks.Click += (_, _) => ResetPeaks();
        levelActions.Children.Add(resetPeaks);
        Button announce = Button("ReadAudioLevels", Localization.Text("Meter.ReadLevels"), Localization.Text("Meter.ReadLevelsName"));
        announce.Click += (_, _) => { UpdateLevels(); ShowResult(reading.Text, false); };
        levelActions.Children.Add(announce);
        panel.Children.Add(levelActions);
        Body.Children.Add(Named(new ScrollViewer
        {
            Content = panel, Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        }, "AudioMeterScroll", Localization.Text("Meter.ScrollName")));
        InitialFocus = inputs;
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => UpdateLevels();
        inputs.SelectionChanged += (_, _) => UpdateButtons();
        scan.Click += (_, _) => ScanInputs();
        start.Click += (_, _) => Start();
        stop.Click += (_, _) => Stop();
        Loaded += (_, _) => ScanInputs();
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            InvalidateCallbacks();
            try { this.source.Dispose(); }
            catch (Exception exception)
            {
                string message = Localization.Text("Meter.DisposeFailed", exception.Message);
                if (reportError is not null) reportError(message);
                else MessageBox.Show(message, Localization.Text("Meter.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        UpdateLevels(); UpdateButtons();
    }

    private ProgressBar Meter(string identifier, string name) => Named(new ProgressBar
    {
        Minimum = AudioLevelCalculator.SilenceDecibels, Maximum = 0, Value = AudioLevelCalculator.SilenceDecibels,
        Height = 20, Margin = new Thickness(3)
    }, identifier, name);

    private void ScanInputs()
    {
        if (running || closed) return;
        try
        {
            string? previous = (inputs.SelectedItem as AudioInput)?.Id;
            IReadOnlyList<AudioInput> available = source.GetInputs();
            inputs.ItemsSource = available;
            inputs.SelectedItem = available.FirstOrDefault(input => input.Id == previous);
            ShowResult(available.Count == 0 ? Localization.Text("Meter.NoInputs")
                : Localization.Text("Meter.SelectInput"), available.Count == 0, optional: true);
        }
        catch (Exception exception) { ShowResult(Localization.Text("Meter.ScanFailed", exception.Message)); }
        UpdateButtons();
    }

    private void Start()
    {
        if (running || stopError is not null || closed || inputs.SelectedItem is not AudioInput input) return;
        InvalidateCallbacks();
        SubscribeCallbacks(true);
        try
        {
            source.Start(input.Id);
            running = true; timer.Start();
            ShowResult(Localization.Text("Meter.Started", input.Name), false, optional: true);
        }
        catch (Exception exception)
        {
            Stop(false);
            string message = Localization.Text("Meter.StartFailed", exception.Message);
            ShowResult(stopError is null ? message : Localization.Text("Meter.CombinedError", message, stopError));
        }
        UpdateButtons(); UpdateLevels();
    }

    internal void Stop(bool report = true)
    {
        InvalidateCallbacks();
        timer.Stop(); running = false;
        stopError = null;
        try { source.Stop(); }
        catch (Exception exception)
        {
            stopError = Localization.Text("Meter.StopFailed", exception.Message);
            if (!closed) SubscribeCallbacks(false);
        }
        UpdateButtons(); UpdateLevels();
        if (!closed && stopError is not null) ShowResult(stopError);
        else if (report && !closed) ShowResult(Localization.Text("Meter.StoppedResult"), false, optional: true);
    }

    private void InvalidateCallbacks()
    {
        if (levelsHandler is not null) source.Levels -= levelsHandler;
        if (failureHandler is not null) source.Failed -= failureHandler;
        levelsHandler = null;
        failureHandler = null;
        lock (sampleLock)
        {
            Interlocked.Increment(ref generation);
            latest = null;
            lastSample = 0;
        }
    }

    private void SubscribeCallbacks(bool includeLevels)
    {
        long expectedGeneration = Interlocked.Read(ref generation);
        if (includeLevels)
        {
            levelsHandler = values => OnLevels(values, expectedGeneration);
            source.Levels += levelsHandler;
        }
        failureHandler = message => OnFailure(message, expectedGeneration);
        source.Failed += failureHandler;
    }

    private void OnLevels(AudioChannelLevel[] values, long expectedGeneration)
    {
        if (values.Length == 0) return;
        if (values.Length > 32 || values.Any(level => level.HasInvalidSamples || !double.IsFinite(level.RmsDecibels)
            || !double.IsFinite(level.PeakDecibels) || level.RmsDecibels < AudioLevelCalculator.SilenceDecibels
            || level.RmsDecibels > 0 || level.PeakDecibels + 1e-9 < level.RmsDecibels))
        {
            OnFailure(Localization.Text("Meter.InvalidSamples"), expectedGeneration);
            return;
        }
        lock (sampleLock)
        {
            if (closed || generation != expectedGeneration) return;
            AudioChannelLevel[]? previous = latest;
            latest = values.Select((level, index) => previous is not null && previous.Length == values.Length
                ? level with
                {
                    PeakDecibels = Math.Max(level.PeakDecibels, previous[index].PeakDecibels),
                    ReachedFullScale = level.ReachedFullScale || previous[index].ReachedFullScale
                } : level).ToArray();
            lastSample = readTimestamp();
        }
    }

    private void ResetPeaks()
    {
        if (closed || !running || stopError is not null) return;
        lock (sampleLock)
        {
            latest = null;
            lastSample = 0;
        }
        UpdateLevels();
        ShowResult(Localization.Text("Meter.PeaksReset"), false, optional: true);
    }

    private void OnFailure(string message, long expectedGeneration)
    {
        if (Interlocked.Read(ref generation) != expectedGeneration || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed || generation != expectedGeneration) return;
            Stop(false);
            ShowResult(stopError is null ? message : Localization.Text("Meter.CombinedError", message, stopError));
        }));
    }

    private void UpdateButtons()
    {
        inputs.IsEnabled = scan.IsEnabled = !running && stopError is null;
        start.IsEnabled = !running && stopError is null && inputs.SelectedItem is AudioInput;
        stop.IsEnabled = running || stopError is not null;
        resetPeaks.IsEnabled = running && stopError is null;
    }

    private void UpdateLevels()
    {
        AudioChannelLevel[]? values;
        long sampledAt;
        lock (sampleLock)
        {
            values = latest;
            sampledAt = lastSample;
        }
        secondPanel.Visibility = values is { Length: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        bool fresh = running && values is { Length: > 0 } && readTimestamp() - sampledAt is >= 0 and < 2000;
        first.Value = fresh ? values![0].RmsDecibels : AudioLevelCalculator.SilenceDecibels;
        second.Value = fresh && values!.Length > 1 ? values[1].RmsDecibels : AudioLevelCalculator.SilenceDecibels;
        string text = stopError is not null ? Localization.Text("Meter.DisplayInterrupted", stopError)
            : !running ? Localization.Text("Meter.Stopped") : values is not { Length: > 0 } ? Localization.Text("Meter.WaitingForSamples")
            : !fresh ? Localization.Text("Meter.StaleWithHistory", Localization.Text("Meter.WaitingForSamples"),
                string.Join(Localization.Text("Meter.ChannelSeparator"), values.Select((level, index) =>
                    Localization.Text("Meter.HistoricalChannel", index + 1, Format(level.PeakDecibels), FullScaleText(level)))))
            : Localization.Text("Meter.Levels", values!.Length,
                string.Join(Localization.Text("Meter.ChannelSeparator"), values.Select((level, index) =>
                    Localization.Text("Meter.ChannelMeasurement", Localization.Text("Meter.ChannelLevel", index + 1, Format(level.RmsDecibels)),
                        Format(level.PeakDecibels), FullScaleText(level)))));
        UiSupport.UpdateReadout(reading, text);
        System.Windows.Automation.AutomationProperties.SetName(reading, text);
    }

    private static string FullScaleText(AudioChannelLevel level) =>
        Localization.Text(level.ReachedFullScale ? "Meter.FullScaleReached" : "Meter.FullScaleNotReached");

    private static string Format(double decibels) => decibels <= AudioLevelCalculator.SilenceDecibels
        ? Localization.Text("Meter.BelowFloor") : Localization.Text("Meter.Level",
            (Math.Round(decibels, 1) == 0 ? 0 : decibels).ToString("F1", Localization.Culture));
}
