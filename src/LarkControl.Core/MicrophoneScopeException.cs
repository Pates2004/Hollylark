namespace LarkControl.Core;

public enum MicrophoneScopeReason
{
    SelectedUnavailable,
    SharedParameters,
    GroupUnavailable,
    GroupChanged
}

public sealed class MicrophoneScopeException : InvalidOperationException
{
    public MicrophoneScopeException(A1Status status, MicrophoneScopeReason reason)
        : base(reason switch
        {
            MicrophoneScopeReason.SelectedUnavailable => "The selected transmitter is not connected.",
            MicrophoneScopeReason.SharedParameters => "Receiver-wide settings cannot be changed for only one of multiple connected transmitters.",
            MicrophoneScopeReason.GroupUnavailable => "At least two connected transmitters are required for group control.",
            MicrophoneScopeReason.GroupChanged => "The connected transmitters no longer match the selected group.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        })
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        Reason = reason;
    }

    public A1Status Status { get; }
    public MicrophoneScopeReason Reason { get; }
    public bool WriteAttempted { get; internal set; }
}
