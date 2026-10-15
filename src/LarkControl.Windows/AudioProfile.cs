using LarkControl.Core;

namespace LarkControl.Windows;

public sealed record AudioProfile(
    string Name,
    int Gain,
    int ChannelMode,
    bool NoiseEnabled,
    int NoiseLevel,
    int Equalizer,
    bool ReverbEnabled,
    int ReverbLevel)
{
    public const int MaximumNameLength = 80;

    public override string ToString() => Name;

    public static AudioProfile FromStatus(string name, A1Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var profile = new AudioProfile(name, status.Gain, status.ChannelMode, status.NoiseEnabled,
            status.NoiseLevel, status.Equalizer, status.ReverbEnabled, status.ReverbLevel);
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameLength || Name.Any(char.IsControl))
            throw new ArgumentException("A profile name must contain 1 to 80 characters without control characters.", nameof(Name));
        if (Gain is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(Gain));
        if (ChannelMode is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ChannelMode));
        if (NoiseLevel is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(NoiseLevel));
        if (Equalizer is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(Equalizer));
        if (ReverbLevel is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(ReverbLevel));
    }

    public IReadOnlyList<SettingChange> CreateChanges()
    {
        Validate();
        return Array.AsReadOnly<SettingChange>(
        [
            SettingChange.Gain(Gain),
            SettingChange.ChannelMode(ChannelMode),
            SettingChange.Noise(NoiseEnabled, NoiseLevel),
            SettingChange.Equalizer(Equalizer),
            SettingChange.Reverb(ReverbEnabled, ReverbLevel)
        ]);
    }
}
