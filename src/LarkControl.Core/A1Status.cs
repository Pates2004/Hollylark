namespace LarkControl.Core;

public sealed record TransmitterState(bool Connected, int? BatteryPercent, bool Muted);

public sealed record A1Status(
    IReadOnlyList<TransmitterState> Transmitters,
    bool NoiseEnabled,
    int NoiseLevel,
    int Gain,
    int ChannelMode,
    int AutoOff,
    bool LightsEnabled,
    int Equalizer,
    bool ReverbEnabled,
    int ReverbLevel,
    bool? IdentificationEnabled)
{
    public byte? RadioModeCode { get; init; }

    public static A1Status Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 15)
            throw new InvalidDataException("Status payload is incomplete.");
        int[] booleanOffsets = [0, 1, 4, 5, 6, 11, 13];
        foreach (int offset in booleanOffsets)
            if (payload[offset] > 1)
                throw new InvalidDataException("Status contains an invalid boolean.");
        if (payload[7] > 2 || payload[8] > 5 || payload[9] > 1 || payload[10] > 1 ||
            payload[12] is < 1 or > 3 || payload[14] > 2 || (payload.Length > 15 && payload[15] > 1))
            throw new InvalidDataException("Status contains unsupported setting values.");

        List<TransmitterState> transmitters =
        [
            new(payload[0] == 1, Battery(payload[2]), payload[4] == 1),
            new(payload[1] == 1, Battery(payload[3]), payload[5] == 1)
        ];
        if (payload.Length >= 23)
        {
            foreach (int offset in new[] { 17, 18, 21, 22 })
                if (payload[offset] > 1)
                    throw new InvalidDataException("Extended status contains an invalid boolean.");
            transmitters.Add(new(payload[17] == 1, Battery(payload[19]), payload[21] == 1));
            transmitters.Add(new(payload[18] == 1, Battery(payload[20]), payload[22] == 1));
        }
        return new A1Status(transmitters.AsReadOnly(), payload[6] == 1, payload[7], payload[8],
            payload[9], payload[10], payload[11] == 0, payload[12], payload[13] == 1,
            payload[14], payload.Length > 15 ? payload[15] == 1 : null)
        {
            RadioModeCode = payload.Length > 16 ? payload[16] : null
        };
    }

    private static int? Battery(byte value) => value <= 100 ? value : null;
}

public sealed record SettingChange(A1Command Command, byte[] Payload, Func<A1Status, bool> IsConfirmed)
{
    public static SettingChange Gain(int value)
    {
        Validate(value, 0, 5);
        return new(A1Command.SetGain, [(byte)value], state => state.Gain == value);
    }

    public static SettingChange ChannelMode(int value)
    {
        Validate(value, 0, 1);
        return new(A1Command.SetChannelMode, [(byte)value], state => state.ChannelMode == value);
    }

    public static SettingChange Noise(bool enabled, int level)
    {
        Validate(level, 0, 2);
        return new(A1Command.SetNoise, [Flag(enabled), (byte)level],
            state => state.NoiseEnabled == enabled && (!enabled || state.NoiseLevel == level));
    }

    public static SettingChange Equalizer(int value)
    {
        Validate(value, 1, 3);
        return new(A1Command.SetEqualizer, [(byte)value], state => state.Equalizer == value);
    }

    public static SettingChange Reverb(bool enabled, int level)
    {
        Validate(level, 0, 2);
        return new(A1Command.SetReverb, [Flag(enabled), (byte)level],
            state => state.ReverbEnabled == enabled && (!enabled || state.ReverbLevel == level));
    }

    public static SettingChange Lights(bool enabled) =>
        new(A1Command.SetLight, [Flag(!enabled)], state => state.LightsEnabled == enabled);

    public static SettingChange Identification(bool enabled) =>
        new(A1Command.SetIdentification, [Flag(enabled)], state => state.IdentificationEnabled == enabled);

    public static SettingChange AutoOff(int value)
    {
        Validate(value, 0, 1);
        return new(A1Command.SetAutoOff, [(byte)value], state => state.AutoOff == value);
    }

    public static SettingChange Mute(int index, bool muted)
    {
        Validate(index, 0, 3);
        return new(A1Command.SetMute, [(byte)index, Flag(muted)], state =>
            index < state.Transmitters.Count && state.Transmitters[index].Connected &&
            state.Transmitters[index].Muted == muted);
    }

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;

    private static void Validate(int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}
