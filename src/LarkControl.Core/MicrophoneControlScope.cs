namespace LarkControl.Core;

public sealed class MicrophoneControlScope
{
    private MicrophoneControlScope(int? transmitterIndex, int[] connectedIndices)
    {
        TransmitterIndex = transmitterIndex;
        ConnectedIndices = Array.AsReadOnly(connectedIndices);
    }

    public int? TransmitterIndex { get; }
    public IReadOnlyList<int> ConnectedIndices { get; }

    public static MicrophoneControlScope Individual(int transmitterIndex)
    {
        if (transmitterIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(transmitterIndex));
        return new MicrophoneControlScope(transmitterIndex, []);
    }

    public static MicrophoneControlScope All(IEnumerable<int> connectedIndices)
    {
        ArgumentNullException.ThrowIfNull(connectedIndices);
        int[] indices = connectedIndices.ToArray();
        if (indices.Any(index => index is < 0 or > 3))
            throw new ArgumentOutOfRangeException(nameof(connectedIndices));
        if (indices.Length < 2 || indices.Distinct().Count() != indices.Length)
            throw new ArgumentException("A group requires at least two unique transmitter indices.", nameof(connectedIndices));
        Array.Sort(indices);
        return new MicrophoneControlScope(null, indices);
    }

    public void Validate(A1Status status, SettingChange change)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Payload);
        bool isMute = change.Command == A1Command.SetMute;
        if (isMute)
        {
            if (change.Payload.Length != 2 || change.Payload[0] > 3 || change.Payload[1] > 1)
                throw new ArgumentException("Invalid mute setting payload.", nameof(change));
            int targetIndex = change.Payload[0];
            if (TransmitterIndex is int selectedIndex
                ? targetIndex != selectedIndex
                : !ConnectedIndices.Contains(targetIndex))
                throw new ArgumentException("The mute target does not belong to the microphone control scope.", nameof(change));
        }

        int[] currentIndices = Enumerable.Range(0, status.Transmitters.Count)
            .Where(index => status.Transmitters[index].Connected).ToArray();
        if (TransmitterIndex is int transmitterIndex)
        {
            if (!currentIndices.Contains(transmitterIndex))
                throw new MicrophoneScopeException(status, MicrophoneScopeReason.SelectedUnavailable);
            if (!isMute && currentIndices.Length != 1)
                throw new MicrophoneScopeException(status, MicrophoneScopeReason.SharedParameters);
        }
        else
        {
            if (currentIndices.Length < 2)
                throw new MicrophoneScopeException(status, MicrophoneScopeReason.GroupUnavailable);
            if (!ConnectedIndices.SequenceEqual(currentIndices))
                throw new MicrophoneScopeException(status, MicrophoneScopeReason.GroupChanged);
        }
    }
}
