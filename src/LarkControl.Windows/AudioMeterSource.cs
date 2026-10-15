using System.IO;
using System.Runtime.InteropServices;
using LarkControl.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LarkControl.Windows;

internal sealed record AudioInput(string Id, string Name)
{
    public override string ToString() => Name;
}

internal interface IAudioMeterSource : IDisposable
{
    IReadOnlyList<AudioInput> GetInputs();
    event Action<AudioChannelLevel[]>? Levels;
    event Action<string>? Failed;
    void Start(string endpointId);
    void Stop();
}

internal sealed class AudioMeterSource : IAudioMeterSource
{
    private WasapiCapture? capture;
    private WasapiCapture? pendingCleanup;
    private MMDevice? device;
    private static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmFormat = new("00000001-0000-0010-8000-00aa00389b71");

    public event Action<AudioChannelLevel[]>? Levels;
    public event Action<string>? Failed;

    public IReadOnlyList<AudioInput> GetInputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var inputs = new List<AudioInput>();
        foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (endpoint) inputs.Add(new AudioInput(endpoint.ID, endpoint.FriendlyName));
        }
        return inputs.OrderByDescending(input => input.Name.Contains("LARK", StringComparison.OrdinalIgnoreCase))
            .ThenBy(input => input.Name, StringComparer.Create(Localization.Culture, ignoreCase: true)).ToArray();
    }

    public void Start(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(endpointId);
            if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
                throw new IOException(Localization.Text("Meter.InputUnavailable"));
            var source = new WasapiCapture(device);
            capture = source;
            WaveFormat format = source.WaveFormat;
            var (floatingPoint, validBits) = DescribeFormat(format);
            Action<AudioChannelLevel[]>? reportLevels = Levels;
            Action<string>? reportFailure = Failed;
            source.DataAvailable += (_, data) =>
            {
                if (!ReferenceEquals(Volatile.Read(ref capture), source) || data.BytesRecorded == 0) return;
                try
                {
                    AudioChannelLevel[] levels = AudioLevelCalculator.Measure(data.Buffer.AsSpan(0, data.BytesRecorded),
                        format.Channels, format.BitsPerSample, floatingPoint, validBits);
                    if (ReferenceEquals(Volatile.Read(ref capture), source)) reportLevels?.Invoke(levels);
                }
                catch (Exception exception)
                {
                    if (ReferenceEquals(Volatile.Read(ref capture), source))
                        reportFailure?.Invoke(Localization.Text("Meter.CalculationFailed", exception.Message));
                }
            };
            source.RecordingStopped += (_, stopped) =>
            {
                if (ReferenceEquals(Volatile.Read(ref capture), source))
                    reportFailure?.Invoke(stopped.Exception is null ? Localization.Text("Meter.InputStopped")
                        : Localization.Text("Meter.MeasurementInterrupted", stopped.Exception.Message));
            };
            source.StartRecording();
        }
        catch (Exception exception)
        {
            try { Stop(); }
            catch (Exception cleanupException)
            {
                throw new IOException(Localization.Text("Meter.CombinedError", exception.Message,
                    Localization.Text("Meter.StopFailed", cleanupException.Message)), new AggregateException(exception, cleanupException));
            }
            throw;
        }
    }

    internal static (bool FloatingPoint, int ValidBitsPerSample) DescribeFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        bool floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible extended && extended.SubFormat == FloatFormat;
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm
            || format is WaveFormatExtensible integer && integer.SubFormat == PcmFormat;
        if ((!floatingPoint && !pcm) || format.Channels is < 1 or > 32
            || (floatingPoint ? format.BitsPerSample != 32 : format.BitsPerSample is not (8 or 16 or 24 or 32))
            || format.BlockAlign != format.Channels * (format.BitsPerSample / 8))
            throw new NotSupportedException(Localization.Text("Meter.UnsupportedFormat"));
        int validBits = format.BitsPerSample;
        if (format is WaveFormatExtensible)
        {
            if (format.ExtraSize < 22) throw new NotSupportedException(Localization.Text("Meter.UnsupportedFormat"));
            IntPtr pointer = WaveFormat.MarshalToPtr(format);
            try { validBits = unchecked((ushort)Marshal.ReadInt16(pointer, 18)); }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        if (validBits < 1 || validBits > format.BitsPerSample || (floatingPoint && validBits != 32))
            throw new NotSupportedException(Localization.Text("Meter.UnsupportedFormat"));
        return (floatingPoint, validBits);
    }

    public void Stop()
    {
        WasapiCapture? previous = Interlocked.Exchange(ref capture, null) ?? pendingCleanup;
        pendingCleanup = previous;
        try { previous?.Dispose(); pendingCleanup = null; }
        finally { device?.Dispose(); device = null; }
    }

    public void Dispose() => Stop();
}
