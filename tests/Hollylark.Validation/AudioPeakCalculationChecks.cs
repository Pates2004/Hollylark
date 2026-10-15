using System.Buffers.Binary;
using LarkControl.Core;

internal static partial class Program
{
    private static void VerifyAudioPeakCalculations()
    {
        var silence = new AudioChannelLevel(AudioLevelCalculator.SilenceDecibels, AudioLevelCalculator.SilenceDecibels, false, false);
        double halfScaleDecibels = 20 * Math.Log10(0.5);
        double halfPowerDecibels = 10 * Math.Log10(0.5);
        int[] channelCounts = [1, 2, 5, 32];
        (int Bits, int? ValidBits, int Minimum, int Maximum, int Step, double Scale)[] pcmFormats =
        [
            (8, null, -128, 127, 1, 128d),
            (16, null, -32768, 32767, 1, 32768d),
            (24, null, -8388608, 8388607, 1, 8388608d),
            (32, null, int.MinValue, int.MaxValue, 1, 2147483648d),
            (8, 7, -128, 126, 2, 128d),
            (16, 12, -32768, 32752, 16, 32768d),
            (24, 20, -8388608, 8388592, 16, 8388608d),
            (32, 24, int.MinValue, 2147483392, 256, 2147483648d)
        ];

        foreach ((int bits, int? validBits, int minimum, int maximum, int step, double scale) in pcmFormats)
        {
            string format = $"PCM{bits}/{validBits ?? bits}";
            (int Value, bool FullScale)[] sampleCases =
            [
                (minimum, true), (minimum + step, false), (maximum - step, false), (maximum, true),
                (0, false), (step, false), (-step, false)
            ];
            foreach ((int value, bool fullScale) in sampleCases)
            {
                double decibels = PeakDecibels(value / scale);
                CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, [value]), 1, bits, false, validBits),
                    [new AudioChannelLevel(decibels, decibels, fullScale, false)],
                    $"{format} single sample {value} preserves extrema, near-extrema and quiet readings");
            }
            foreach (int channels in channelCounts)
            {
                AudioChannelLevel[] silentChannels = Enumerable.Repeat(silence, channels).ToArray();
                CheckLevels(AudioLevelCalculator.Measure([], channels, bits, false, validBits), silentChannels,
                    $"{format} empty input returns {channels} valid floor readings");
                CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, new int[channels * 3]), channels, bits, false, validBits),
                    silentChannels, $"{format} silence has no full-scale or invalid flags across {channels} channels");
                for (int offset = 0; offset < sampleCases.Length; offset++)
                {
                    var samples = new int[channels * 2];
                    var expected = new AudioChannelLevel[channels];
                    for (int channel = 0; channel < channels; channel++)
                    {
                        (int value, bool fullScale) = sampleCases[(channel + offset) % sampleCases.Length];
                        samples[channel] = value;
                        double peak = PeakDecibels(value / scale);
                        double rms = Math.Max(AudioLevelCalculator.SilenceDecibels, peak + halfPowerDecibels);
                        expected[channel] = new AudioChannelLevel(rms, peak, fullScale, false);
                    }
                    CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, samples), channels, bits, false, validBits), expected,
                        $"{format} {channels}-channel case {offset} keeps levels and flags separate across quiet frames");
                }
            }
            foreach (int fullScaleSample in new[] { minimum, maximum })
            {
                var samples = new int[256 * 2];
                samples[127 * 2 + 1] = fullScaleSample;
                double peak = PeakDecibels(fullScaleSample / scale);
                CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, samples), 2, bits, false, validBits),
                    [silence, new AudioChannelLevel(peak - 10 * Math.Log10(256), peak, true, false)],
                    $"{format} isolated full-scale sample {fullScaleSample} retains its peak and flag despite low RMS");
            }
            if (step > 1)
            {
                for (int paddingBit = 1; paddingBit < step; paddingBit <<= 1)
                {
                    foreach (int malformed in new[] { paddingBit, minimum | paddingBit, maximum | paddingBit })
                    {
                        CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, [malformed]), 1, bits, false, validBits),
                            [new AudioChannelLevel(double.NaN, double.NaN, false, true)],
                            $"{format} rejects nonzero padding bit {paddingBit} in sample {malformed}");
                    }
                }
                foreach (int[] samples in new[] { new[] { -1, maximum }, new[] { minimum, -1 } })
                {
                    CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, samples), 1, bits, false, validBits),
                        [new AudioChannelLevel(double.NaN, double.NaN, true, true)],
                        $"{format} invalid padding preserves valid full-scale evidence before or after it");
                }
                foreach (int channels in channelCounts)
                {
                    for (int invalidChannel = 0; invalidChannel < channels; invalidChannel++)
                    {
                        var samples = new int[channels * 3];
                        var expected = Enumerable.Repeat(silence, channels).ToArray();
                        samples[channels + invalidChannel] = 1;
                        expected[invalidChannel] = new AudioChannelLevel(double.NaN, double.NaN, false, true);
                        if (channels > 1)
                        {
                            int fullScaleChannel = (invalidChannel + 1) % channels;
                            samples[fullScaleChannel] = maximum;
                            double peak = PeakDecibels(maximum / scale);
                            expected[fullScaleChannel] = new AudioChannelLevel(peak - 10 * Math.Log10(3), peak, true, false);
                        }
                        CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, samples), channels, bits, false, validBits), expected,
                            $"{format} invalid padding on channel {invalidChannel + 1} of {channels} is sticky and channel-local");
                    }
                }
            }
            else
            {
                byte[] samples = EncodePcm(bits, [minimum, maximum, 0]);
                CheckLevels(AudioLevelCalculator.Measure(samples, 3, bits, false, bits),
                    AudioLevelCalculator.Measure(samples, 3, bits, false),
                    $"PCM{bits} defaults valid bits to the container width");
                CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, [minimum, 0]), 2, bits, false, 1),
                    [new AudioChannelLevel(0, 0, true, false), new AudioChannelLevel(-96, -96, true, false)],
                    $"PCM{bits}/1 accepts both representable extrema, including its zero positive maximum");
                CheckLevels(AudioLevelCalculator.Measure([], 2, bits, false, 1), [silence, silence],
                    $"PCM{bits}/1 empty input has no sample evidence and no flags");
                CheckLevels(AudioLevelCalculator.Measure(EncodePcm(bits, [1]), 1, bits, false, 1),
                    [new AudioChannelLevel(double.NaN, double.NaN, false, true)],
                    $"PCM{bits}/1 rejects nonzero unused low bits");
            }
        }

        CheckLevels(AudioLevelCalculator.Measure([0, 1, 254, 255, 128], 5, 8, false),
            [new AudioChannelLevel(0, 0, true, false),
             new AudioChannelLevel(PeakDecibels(127 / 128d), PeakDecibels(127 / 128d), false, false),
             new AudioChannelLevel(PeakDecibels(126 / 128d), PeakDecibels(126 / 128d), false, false),
             new AudioChannelLevel(PeakDecibels(127 / 128d), PeakDecibels(127 / 128d), true, false), silence],
            "Raw unsigned PCM8 flags 0 and 255, unlike near-extrema and midpoint silence");
        CheckLevels(AudioLevelCalculator.Measure([0, 2, 252, 254, 128, 255], 6, 8, false, 7),
            [new AudioChannelLevel(0, 0, true, false),
             new AudioChannelLevel(PeakDecibels(126 / 128d), PeakDecibels(126 / 128d), false, false),
             new AudioChannelLevel(PeakDecibels(124 / 128d), PeakDecibels(124 / 128d), false, false),
             new AudioChannelLevel(PeakDecibels(126 / 128d), PeakDecibels(126 / 128d), true, false), silence,
             new AudioChannelLevel(double.NaN, double.NaN, false, true)],
            "Unsigned PCM8/7 uses left-aligned extrema 0 and 254 and rejects raw 255 as invalid padding");

        float belowFullScale = MathF.BitDecrement(1f);
        (float Value, double Rms, double Peak, bool FullScale)[] floatCases =
        [
            (0f, -96, -96, false), (-0f, -96, -96, false),
            (float.Epsilon, -96, -96, false), (-float.Epsilon, -96, -96, false),
            (0.5f, halfScaleDecibels, halfScaleDecibels, false),
            (-0.5f, halfScaleDecibels, halfScaleDecibels, false),
            (belowFullScale, PeakDecibels(belowFullScale), PeakDecibels(belowFullScale), false),
            (-belowFullScale, PeakDecibels(belowFullScale), PeakDecibels(belowFullScale), false),
            (1f, 0, 0, true), (-1f, 0, 0, true),
            (MathF.BitIncrement(1f), 0, PeakDecibels(MathF.BitIncrement(1f)), true),
            (-MathF.BitIncrement(1f), 0, PeakDecibels(MathF.BitIncrement(1f)), true),
            (1.5f, 0, PeakDecibels(1.5), true), (-1.5f, 0, PeakDecibels(1.5), true),
            (2f, 0, PeakDecibels(2), true), (-2f, 0, PeakDecibels(2), true),
            (float.MaxValue, 0, PeakDecibels(float.MaxValue), true),
            (float.MinValue, 0, PeakDecibels(float.MinValue), true)
        ];
        foreach ((float value, double rms, double peak, bool fullScale) in floatCases)
        {
            CheckLevels(AudioLevelCalculator.Measure(EncodeFloats([value]), 1, 32, true),
                [new AudioChannelLevel(rms, peak, fullScale, false)],
                $"Float sample {value:R} retains pre-clamp peak with bounded RMS and exact full-scale detection");
        }
        foreach (float overshoot in new[] { -2f, 2f, float.MinValue, float.MaxValue })
        {
            CheckLevels(AudioLevelCalculator.Measure(EncodeFloats([0, overshoot, 0, 0]), 1, 32, true),
                [new AudioChannelLevel(10 * Math.Log10(0.25), PeakDecibels(overshoot), true, false)],
                $"Float overshoot {overshoot:R} is clamped before RMS accumulation but not before peak calculation");
        }
        CheckLevels(AudioLevelCalculator.Measure(EncodeFloats([0.25f, -2f]), 1, 32, true, 32),
            [new AudioChannelLevel(10 * Math.Log10(0.53125), PeakDecibels(2), true, false)],
            "Float peak follows absolute negative amplitude and accepts exactly 32 explicit valid bits");

        foreach (float invalidSample in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            CheckLevels(AudioLevelCalculator.Measure(EncodeFloats([invalidSample]), 1, 32, true),
                [new AudioChannelLevel(double.NaN, double.NaN, false, true)],
                $"Float {invalidSample} alone returns invalid RMS and peak rather than healthy silence");
            foreach (float[] samples in new[] { new[] { invalidSample, 2f }, new[] { -2f, invalidSample } })
            {
                CheckLevels(AudioLevelCalculator.Measure(EncodeFloats(samples), 1, 32, true),
                    [new AudioChannelLevel(double.NaN, double.NaN, true, true)],
                    $"Float {invalidSample} preserves finite full-scale evidence before or after the invalid sample");
            }
            foreach (int channels in channelCounts)
            {
                for (int invalidChannel = 0; invalidChannel < channels; invalidChannel++)
                {
                    var samples = Enumerable.Repeat(0.5f, channels * 2).ToArray();
                    var expected = Enumerable.Repeat(new AudioChannelLevel(halfScaleDecibels, halfScaleDecibels, false, false), channels).ToArray();
                    samples[invalidChannel] = invalidSample;
                    expected[invalidChannel] = new AudioChannelLevel(double.NaN, double.NaN, false, true);
                    if (channels > 1)
                    {
                        int fullScaleChannel = (invalidChannel + 1) % channels;
                        samples[channels + fullScaleChannel] = 1.5f;
                        expected[fullScaleChannel] = new AudioChannelLevel(10 * Math.Log10(0.625), PeakDecibels(1.5), true, false);
                    }
                    CheckLevels(AudioLevelCalculator.Measure(EncodeFloats(samples), channels, 32, true), expected,
                        $"Float {invalidSample} on channel {invalidChannel + 1} of {channels} does not contaminate other channels or flags");
                }
            }
        }
        foreach (int channels in channelCounts)
        {
            AudioChannelLevel[] expected = Enumerable.Repeat(silence, channels).ToArray();
            CheckLevels(AudioLevelCalculator.Measure([], channels, 32, true), expected,
                $"Empty float input returns {channels} silent readings without stale flags");
            CheckLevels(AudioLevelCalculator.Measure(EncodeFloats(new float[channels * 3]), channels, 32, true), expected,
                $"Silent float frames return {channels} valid floor readings after previous invalid inputs");
        }

        foreach (int channels in new[] { int.MinValue, -1, 0, 33, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], channels, 16, false),
                $"Empty PCM still rejects invalid channel count {channels}");
            Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], channels, 32, true),
                $"Empty float input still rejects invalid channel count {channels}");
        }
        foreach (int bits in new[] { int.MinValue, -8, 0, 1, 12, 20, 64, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 1, bits, false),
                $"Empty PCM still rejects unsupported container width {bits}");
        }
        foreach (int bits in new[] { 0, 8, 16, 24, 64 })
        {
            Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 1, bits, true),
                $"Empty float input still rejects unsupported container width {bits}");
        }
        foreach (int bits in new[] { 8, 16, 24, 32 })
        {
            foreach (int validBits in new[] { int.MinValue, -1, 0, bits + 1, int.MaxValue })
            {
                Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 1, bits, false, validBits),
                    $"Empty PCM{bits} still rejects invalid valid-bit count {validBits}");
            }
        }
        foreach (int validBits in new[] { int.MinValue, -1, 0, 1, 24, 31, 33, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => AudioLevelCalculator.Measure([], 1, 32, true, validBits),
                $"Empty float input rejects valid-bit count {validBits} instead of accepting padded floats");
        }
        foreach ((int bits, bool floatingPoint, int? validBits) in new (int, bool, int?)[]
        {
            (8, false, null), (16, false, null), (24, false, null), (32, false, null),
            (32, true, null), (24, false, 20), (32, false, 24)
        })
        {
            foreach (int channels in channelCounts)
            {
                int sampleBytes = bits / 8;
                int frameBytes = channels * sampleBytes;
                if (frameBytes > 1)
                {
                    Throws<ArgumentException>(() => AudioLevelCalculator.Measure(new byte[frameBytes - 1], channels, bits, floatingPoint, validBits),
                        $"{bits}-bit float={floatingPoint} valid={validBits} rejects a partial first {channels}-channel frame");
                    Throws<ArgumentException>(() => AudioLevelCalculator.Measure(new byte[frameBytes + 1], channels, bits, floatingPoint, validBits),
                        $"{bits}-bit float={floatingPoint} valid={validBits} rejects trailing bytes after a complete frame");
                }
                if (channels > 1)
                {
                    Throws<ArgumentException>(() => AudioLevelCalculator.Measure(new byte[sampleBytes], channels, bits, floatingPoint, validBits),
                        $"{bits}-bit float={floatingPoint} valid={validBits} rejects sample-aligned input missing other channels");
                }
            }
        }

        byte[] paddedPcm = EncodePcm(16, [short.MinValue, 8192, short.MaxValue]);
        double quarterScaleDecibels = 20 * Math.Log10(0.25);
        CheckLevels(AudioLevelCalculator.Measure(paddedPcm.AsSpan(2, 2), 1, 16, false),
            [new AudioChannelLevel(quarterScaleDecibels, quarterScaleDecibels, false, false)],
            "PCM span boundaries exclude neighboring full-scale samples");

        static double PeakDecibels(double value) => value == 0 ? AudioLevelCalculator.SilenceDecibels
            : Math.Max(AudioLevelCalculator.SilenceDecibels, 20 * Math.Log10(Math.Abs(value)));

        static bool DecibelsMatch(double actual, double expected) => double.IsNaN(expected)
            ? double.IsNaN(actual) : Math.Abs(actual - expected) < 1e-10;

        static void CheckLevels(AudioChannelLevel[] actual, AudioChannelLevel[] expected, string name)
        {
            Check(actual.Length == expected.Length && actual.Select((level, channel) =>
                DecibelsMatch(level.RmsDecibels, expected[channel].RmsDecibels)
                && DecibelsMatch(level.PeakDecibels, expected[channel].PeakDecibels)
                && level.ReachedFullScale == expected[channel].ReachedFullScale
                && level.HasInvalidSamples == expected[channel].HasInvalidSamples
                && (level.HasInvalidSamples || (level.RmsDecibels >= AudioLevelCalculator.SilenceDecibels
                    && level.RmsDecibels <= 0 && level.PeakDecibels + 1e-9 >= level.RmsDecibels))).All(matches => matches), name);
        }

        static byte[] EncodePcm(int bitsPerSample, ReadOnlySpan<int> samples)
        {
            int sampleBytes = bitsPerSample / 8;
            var buffer = new byte[samples.Length * sampleBytes];
            for (int index = 0; index < samples.Length; index++)
            {
                Span<byte> sample = buffer.AsSpan(index * sampleBytes, sampleBytes);
                int value = samples[index];
                switch (bitsPerSample)
                {
                    case 8:
                        sample[0] = checked((byte)(value + 128));
                        break;
                    case 16:
                        BinaryPrimitives.WriteInt16LittleEndian(sample, checked((short)value));
                        break;
                    case 24:
                        sample[0] = (byte)(value & 255);
                        sample[1] = (byte)((value >> 8) & 255);
                        sample[2] = (byte)((value >> 16) & 255);
                        break;
                    case 32:
                        BinaryPrimitives.WriteInt32LittleEndian(sample, value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
                }
            }
            return buffer;
        }

        static byte[] EncodeFloats(ReadOnlySpan<float> samples)
        {
            var buffer = new byte[samples.Length * sizeof(float)];
            for (int index = 0; index < samples.Length; index++)
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(index * sizeof(float)), BitConverter.SingleToInt32Bits(samples[index]));
            return buffer;
        }
    }
}
