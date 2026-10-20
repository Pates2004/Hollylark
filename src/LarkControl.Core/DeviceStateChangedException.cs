namespace LarkControl.Core;

public sealed class DeviceStateChangedException(A1Status status) : InvalidOperationException("Device state changed before the setting could be sent.")
{
    public A1Status Status { get; } = status ?? throw new ArgumentNullException(nameof(status));
}
