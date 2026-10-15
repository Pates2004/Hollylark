namespace LarkControl.Core;

public enum DeviceWriteSafetyReason
{
    NoConnectedTransmitter,
    TargetTransmitterDisconnected
}

public sealed class DeviceWriteSafetyException : InvalidOperationException
{
    public DeviceWriteSafetyException(A1Status status, int? transmitterIndex = null)
        : base(transmitterIndex is null
            ? "At least one connected transmitter is required to change device settings."
            : "The target transmitter is not connected.")
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        TransmitterIndex = transmitterIndex;
    }

    public A1Status Status { get; }
    public int? TransmitterIndex { get; }
    public DeviceWriteSafetyReason Reason => TransmitterIndex is null
        ? DeviceWriteSafetyReason.NoConnectedTransmitter
        : DeviceWriteSafetyReason.TargetTransmitterDisconnected;
}
