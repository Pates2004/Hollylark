using LarkControl.Core;

namespace LarkControl.Windows;

public sealed record BatteryAlertOutput
{
    public bool Notification { get; init; }
    public bool Speech { get; init; } = true;
    public bool Sound { get; init; }
}

public sealed record BatteryAlertPreferences
{
    public BatteryAlertPolicy Policy { get; init; } = new();
    public BatteryAlertOutput Low { get; init; } = new();
    public BatteryAlertOutput Critical { get; init; } = new();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Policy);
        ArgumentNullException.ThrowIfNull(Low);
        ArgumentNullException.ThrowIfNull(Critical);
        Policy.Validate();
    }
}
