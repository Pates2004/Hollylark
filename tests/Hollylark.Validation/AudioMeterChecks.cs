using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyAudioLevels()
    {
        Check(AudioLevelCalculator.Measure([], 2, 16, false).Select(level => level.RmsDecibels).SequenceEqual(new[] { -96d, -96d }), "Empty PCM has floor values, not full scale");
        Check(AudioLevelCalculator.Measure(new byte[8], 2, 16, false).All(level => level.RmsDecibels == -96), "Zero PCM has floor level");
        byte[] stereo = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(2), -8192);
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(4), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(6), 8192);
        AudioChannelLevel[] levels = AudioLevelCalculator.Measure(stereo, 2, 16, false);
        Check(Math.Abs(levels[0].RmsDecibels + 6.020599913) < 0.0001, "PCM16 channel one RMS uses both sample signs");
        Check(Math.Abs(levels[1].RmsDecibels + 12.041199826) < 0.0001, "Stereo channels remain separate");
        Check(AudioLevelCalculator.Measure([128, 128], 1, 8, false)[0].RmsDecibels == -96, "PCM8 silence is unsigned midpoint");
        Check(AudioLevelCalculator.Measure([0, 0, 128], 1, 24, false)[0].RmsDecibels == 0, "PCM24 negative full scale sign extends correctly");
        Check(AudioLevelCalculator.Measure([0, 0, 0, 128], 1, 32, false)[0].RmsDecibels == 0, "PCM32 negative full scale is valid");
        var floats = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(floats.AsSpan(0), BitConverter.SingleToInt32Bits(0.5f));
        BinaryPrimitives.WriteInt32LittleEndian(floats.AsSpan(4), BitConverter.SingleToInt32Bits(float.NaN));
        levels = AudioLevelCalculator.Measure(floats, 2, 32, true);
        Check(Math.Abs(levels[0].RmsDecibels + 6.020599913) < 0.0001 && levels[1].HasInvalidSamples
            && double.IsNaN(levels[1].RmsDecibels), "Float RMS remains valid while nonfinite input is explicitly invalid, not silence");
        BinaryPrimitives.WriteInt32LittleEndian(floats.AsSpan(0), BitConverter.SingleToInt32Bits(1.5f));
        Check(AudioLevelCalculator.Measure(floats, 2, 32, true)[0].RmsDecibels == 0, "Float RMS overshoots saturate without invalid bar range");
        Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 0, 16, false), "Channel count validated");
        Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 33, 16, false), "Channel allocation bounded");
        Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 2, 24, true), "Unsupported float format rejected");
        Throws<ArgumentException>(() => AudioLevelCalculator.Measure([0], 2, 16, false), "Incomplete interleaved frame rejected");
    }

    private static void VerifyAudioMeterInterface()
    {
        using var source = new FakeAudioMeterSource();
        int? channelMode = 0;
        var window = new AudioMeterWindow(() => channelMode, source);
        MoveOffscreen(window); window.Show(); Pump();
        try
        {
            ComboBox inputs = FindById<ComboBox>(window, "AudioInputChoice");
            Button start = FindById<Button>(window, "StartAudioMeter");
            Button stop = FindById<Button>(window, "StopAudioMeter");
            TextBlock reading = FindById<TextBlock>(window, "AudioLevelsReadout");
            ProgressBar first = FindById<ProgressBar>(window, "AudioLevel1");
            ProgressBar second = FindById<ProgressBar>(window, "AudioLevel2");
            Check(source.Starts == 0 && inputs.SelectedIndex == -1 && !start.IsEnabled, "Meter never chooses/captures default microphone");
            inputs.SelectedIndex = 0;
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(source.Starts == 1 && source.Selected == "other" && stop.IsEnabled && !inputs.IsEnabled, "Start captures only explicitly selected unrelated Windows endpoint");
            Check(!second.IsVisible && reading.Text.Contains("brak bieżącego pomiaru"),
                "Meter does not infer an input channel count before samples arrive");
            source.Emit([-6.02, -12.04]);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(reading.Text.Contains("liczba kanałów: 2") && reading.Text.Contains("6,0") && reading.Text.Contains("12,0")
                && first.Value == -6.02 && second.Value == -12.04 && second.IsVisible,
                "Unrelated stereo input keeps both real channels when the LARK receiver reports mono");
            RenderWindow(window, "audio-meters-stereo.png");
            Thread.Sleep(2050);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reading.Text.Contains("brak bieżącego pomiaru") && FindById<ProgressBar>(window, "AudioLevel1").Value == AudioLevelCalculator.SilenceDecibels,
                "Stalled input clears stale bars and reports missing samples rather than inventing silence");
            source.Emit([-6.02, -12.04]);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string stereoReading = reading.Text;
            channelMode = 1;
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reading.Text == stereoReading && second.IsVisible && first.Value == -6.02 && second.Value == -12.04,
                "Changing the receiver to stereo does not reinterpret the selected Windows input");
            channelMode = null;
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reading.Text == stereoReading && second.IsVisible && first.Value == -6.02 && second.Value == -12.04
                && source.Starts == 1 && source.Stops == 0,
                "Disconnecting the receiver does not relabel, hide or stop an unrelated stereo input");
            channelMode = 0;
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reading.Text == stereoReading && second.IsVisible,
                "Reconnecting a mono receiver does not hide the real second input channel");
            channelMode = 1; source.Emit([-20]);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(reading.Text.Contains("liczba kanałów: 1") && reading.Text.Contains("Kanał 1: -20,0")
                && !reading.Text.Contains("Kanał 2") && first.Value == -20 && !second.IsVisible,
                "An actual one-channel Windows input has no fabricated second channel even with a stereo receiver");
            channelMode = null;
            string singleChannelReading = reading.Text;
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reading.Text == singleChannelReading && !second.IsVisible,
                "Receiver disconnection does not change an actual one-channel input");
            channelMode = 0; source.Emit([-10, -20, -30, -40]);
            FindById<Button>(window, "ReadAudioLevels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(reading.Text.Contains("liczba kanałów: 4") && reading.Text.Contains("Kanał 3: -30,0")
                && reading.Text.Contains("Kanał 4: -40,0") && second.IsVisible,
                "Text readout includes every supplied Windows channel without inventing transmitter identities");
            source.EmitFailure("Test odłączenia wejścia audio"); Pump();
            Check(!stop.IsEnabled && reading.Text.Contains("zatrzymany"), "Audio failure stops capture and clears stale levels");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(source.Stops >= 2 && !stop.IsEnabled, "User stop releases audio source");
            Check(FindById<TextBlock>(window, "SectionResultReadout").Text.Contains("Pomiar zatrzymany"), "Stop replaces the stale running footer as well as live readouts");
            window.ApplyPreferences(new UiPreferences { ShowHints = false });
            Check(Descendants(window).OfType<TextBlock>().Where(block => block.Style == window.FindResource("HintStyle"))
                .All(block => block.Visibility == Visibility.Collapsed), "Meter optional hints can be hidden");
            VerifyAccessibleControls(window);
            RenderWindow(window, "audio-meters-stopped.png");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.StopFailure = true;
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(FindById<TextBlock>(window, "SectionResultReadout").Text.Contains("Nie potwierdzono zamknięcia"),
                "Stop failure is not replaced by a success message");
            Check(reading.Text.Contains("Nie potwierdzono zamknięcia") && !start.IsEnabled && stop.IsEnabled,
                "Failed audio cleanup blocks restart and offers a stop retry without claiming capture ended");
            source.EmitFailure("Test błędu źródła"); Pump();
            string failure = FindById<TextBlock>(window, "SectionResultReadout").Text;
            Check(failure.Contains("Test błędu źródła") && failure.Contains("Nie potwierdzono zamknięcia"),
                "Source failure preserves both original failure and cleanup failure");
            source.StopFailure = false;
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(start.IsEnabled && !stop.IsEnabled && reading.Text == "Pomiar zatrzymany.",
                "Successful stop retry clears the uncertain capture state");
        }
        finally { source.StopFailure = false; window.Close(); Pump(); }
        Check(source.Disposed, "Closing meter releases source and device handles");
        VerifyAudioMeterOperationMessages();
    }

    private static void VerifyAudioMeterOperationMessages()
    {
        using var source = new FakeAudioMeterSource();
        var window = new AudioMeterWindow(() => 1, source);
        var quiet = new UiPreferences { ShowOperationMessages = false, AnnounceStateChanges = false };
        window.ApplyPreferences(quiet);
        MoveOffscreen(window); window.Show(); Pump();
        try
        {
            TextBlock result = FindById<TextBlock>(window, "SectionResultReadout");
            ComboBox inputs = FindById<ComboBox>(window, "AudioInputChoice");
            Button start = FindById<Button>(window, "StartAudioMeter");
            Button stop = FindById<Button>(window, "StopAudioMeter");
            Button read = FindById<Button>(window, "ReadAudioLevels");
            Check(result.Visibility == Visibility.Collapsed && source.Starts == 0,
                "Disabled operation messages also hide automatic meter enumeration feedback without capture");
            inputs.SelectedIndex = 1;
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(source.Starts == 1 && stop.IsEnabled && result.Visibility == Visibility.Collapsed,
                "Hidden meter start feedback does not disable the explicitly requested operation");
            Check(AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                "Hidden meter success feedback does not remain an announced live region");
            source.Emit([-12, -18]);
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(result.IsVisible && result.Text.Contains("12,0"),
                "Explicit Read Levels remains available even when routine operation messages are hidden");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(result.Visibility == Visibility.Collapsed && !stop.IsEnabled,
                "Meter stop also respects disabled routine messages");
            window.ApplyPreferences(quiet with { ShowOperationMessages = true });
            Pump();
            Check(result.IsVisible && result.Text.Contains("Pomiar zatrzymany"),
                "Re-enabling operation messages restores the latest actual meter status");
            window.ApplyPreferences(quiet);
            Pump();
            Check(result.Visibility == Visibility.Collapsed,
                "Disabling messages hides an already displayed optional footer immediately");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.StopFailure = true;
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(result.IsVisible && result.Text.Contains("Nie potwierdzono zamknięcia")
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Audio cleanup errors remain visible and announced with both optional feedback settings disabled");
            window.ApplyPreferences(quiet);
            Pump();
            Check(result.IsVisible, "Applying preferences never hides an important audio error");
            source.StopFailure = false;
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { source.StopFailure = false; window.Close(); Pump(); }
    }

    private sealed class FakeAudioMeterSource : IAudioMeterSource
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public bool StopFailure { get; set; }
        public string? Selected { get; private set; }
        public event Action<AudioChannelLevel[]>? Levels;
        public event Action<string>? Failed;
        public IReadOnlyList<AudioInput> GetInputs() => [new("other", "Inne wejście"), new("lark", "LARK A1")];
        public void Start(string endpointId) { Starts++; Selected = endpointId; }
        public void Stop()
        {
            Stops++;
            if (StopFailure) throw new IOException("Test audio cleanup failure");
        }
        public void Dispose() { Disposed = true; Stop(); }
        public void Emit(double[] values) => Levels?.Invoke(SimulateAudioLevels(values));
        public void EmitMeasurement(AudioChannelLevel[] values) => Levels?.Invoke(values);
        public void EmitFailure(string message) => Failed?.Invoke(message);
    }
}
