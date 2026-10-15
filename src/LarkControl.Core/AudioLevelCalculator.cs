using System.Buffers.Binary;

namespace LarkControl.Core;

public readonly record struct AudioChannelLevel(double RmsDecibels, double PeakDecibels, bool ReachedFullScale, bool HasInvalidSamples);

public static class AudioLevelCalculator
{
    public const double SilenceDecibels = -96;

    public static AudioChannelLevel[] Measure(ReadOnlySpan<byte> buffer, int channels, int bitsPerSample, bool floatingPoint,
        int? validBitsPerSample = null)
    {
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (floatingPoint ? bitsPerSample != 32 : bitsPerSample is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        int validBits = validBitsPerSample ?? bitsPerSample;
        if (floatingPoint ? validBits != 32 : validBits < 1 || validBits > bitsPerSample)
            throw new ArgumentOutOfRangeException(nameof(validBitsPerSample));
        int sampleBytes = bitsPerSample / 8;
        int frameBytes = sampleBytes * channels;
        if (buffer.Length % frameBytes != 0) throw new ArgumentException("Incomplete audio frame.", nameof(buffer));
        var squares = new double[channels];
        var peaks = new double[channels];
        var reachedFullScale = new bool[channels];
        var hasInvalidSamples = new bool[channels];
        double pcmScale = 1L << (bitsPerSample - 1);
        int padding = bitsPerSample - validBits;
        long paddingMask = (1L << padding) - 1;
        long maximumPcmValue = ((1L << (validBits - 1)) - 1) << padding;
        int frames = buffer.Length / frameBytes;
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> sample = buffer.Slice(frame * frameBytes + channel * sampleBytes, sampleBytes);
                double value;
                if (floatingPoint)
                {
                    value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
                    if (!double.IsFinite(value))
                    {
                        hasInvalidSamples[channel] = true;
                        continue;
                    }
                    reachedFullScale[channel] |= Math.Abs(value) >= 1;
                }
                else
                {
                    int pcmValue = bitsPerSample switch
                    {
                        8 => sample[0] - 128,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(sample),
                        24 => (sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(sample),
                        _ => throw new InvalidOperationException("Unsupported sample format.")
                    };
                    if ((pcmValue & paddingMask) != 0)
                    {
                        hasInvalidSamples[channel] = true;
                        continue;
                    }
                    reachedFullScale[channel] |= pcmValue == -pcmScale || pcmValue == maximumPcmValue;
                    value = pcmValue / pcmScale;
                }
                peaks[channel] = Math.Max(peaks[channel], Math.Abs(value));
                value = Math.Clamp(value, -1, 1);
                squares[channel] += value * value;
            }
        }
        var levels = new AudioChannelLevel[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            if (hasInvalidSamples[channel])
            {
                levels[channel] = new AudioChannelLevel(double.NaN, double.NaN, reachedFullScale[channel], true);
                continue;
            }
            double rmsDecibels = frames == 0 || squares[channel] == 0 ? SilenceDecibels
                : Math.Clamp(10 * Math.Log10(squares[channel] / frames), SilenceDecibels, 0);
            double peakDecibels = peaks[channel] == 0 ? SilenceDecibels
                : Math.Max(SilenceDecibels, 20 * Math.Log10(peaks[channel]));
            levels[channel] = new AudioChannelLevel(rmsDecibels, peakDecibels, reachedFullScale[channel], false);
        }
        return levels;
    }
}
