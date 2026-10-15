namespace LarkControl.Core;

public sealed class A1Controller : IDisposable
{
    private readonly IA1Transport transport;
    private readonly SemaphoreSlim transactionLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object responseLock = new();
    private PendingResponse? pending;
    private int maximumTransmitterCount;
    private bool disposed;

    private sealed record PendingResponse(A1Command Command, TaskCompletionSource<A1Frame> Completion,
        Func<A1Frame, bool>? Matches);

    public event Action<string>? Trace;

    public A1Controller(IA1Transport transport)
    {
        this.transport = transport;
        transport.ReportReceived += OnReport;
        transport.Failed += OnFailure;
    }

    public async Task<A1Status> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await transactionLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await ReadStatusCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { transactionLock.Release(); }
    }

    public async Task<Version> ReadVersionAsync(int device, CancellationToken cancellationToken = default)
    {
        if (device is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(device));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await transactionLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            A1Frame frame = await ExchangeAsync(A1Command.GetVersion, [(byte)device], linked.Token,
                response => response.Payload.Length > 0 && response.Payload[0] == device).ConfigureAwait(false);
            if (frame.Payload.Length is < 3 or > 5 || frame.Payload[0] != device)
                throw new InvalidDataException("Invalid firmware version response.");
            Version version = Version.Parse(string.Join('.', frame.Payload.Skip(1)));
            if (device == 0 && A1DeviceIdentity.IsFourTransmitterFirmware(version))
                maximumTransmitterCount = Math.Max(maximumTransmitterCount, 4);
            return version;
        }
        finally { transactionLock.Release(); }
    }

    public Task<string?> ReadSerialNumberAsync(int device, CancellationToken cancellationToken = default)
    {
        A1DeviceIdentity.ValidateDevice(device);
        return ReadIdentityFieldAsync(A1Command.GetSerialNumber, [(byte)device],
            response => response.Payload.Length > 0 && response.Payload[0] == device,
            response => A1DeviceIdentity.ParseSerialNumber(device, response.Payload), cancellationToken);
    }

    public Task<string?> ReadMacAddressAsync(int device, CancellationToken cancellationToken = default)
    {
        A1DeviceIdentity.ValidateDevice(device);
        if (device == 0)
            return ReadIdentityFieldAsync(A1Command.GetReceiverMacAddress, [], null,
                response => A1DeviceIdentity.ParseMacAddress(device, response.Payload), cancellationToken);
        return ReadIdentityFieldAsync(A1Command.GetTransmitterMacAddress, [(byte)(device - 1)],
            response => response.Payload.Length > 0 && response.Payload[0] == device - 1,
            response => A1DeviceIdentity.ParseMacAddress(device, response.Payload), cancellationToken);
    }

    private async Task<string?> ReadIdentityFieldAsync(A1Command command, byte[] payload,
        Func<A1Frame, bool>? matches, Func<A1Frame, string?> parse, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await transactionLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            A1Frame frame = await ExchangeAsync(command, payload, linked.Token, matches).ConfigureAwait(false);
            return parse(frame);
        }
        finally { transactionLock.Release(); }
    }

    public async Task<A1Status> ApplyAsync(SettingChange change, CancellationToken cancellationToken = default,
        MicrophoneControlScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Payload);
        byte[] payload = (byte[])change.Payload.Clone();
        if (change.Command == A1Command.SetMute &&
            (payload.Length != 2 || payload[0] > 3 || payload[1] > 1))
            throw new ArgumentException("Invalid mute setting payload.", nameof(change));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await transactionLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            A1Status currentStatus = await ReadStatusCoreAsync(linked.Token).ConfigureAwait(false);
            scope?.Validate(currentStatus, change with { Payload = payload });
            if (!currentStatus.Transmitters.Any(transmitter => transmitter.Connected))
                throw new DeviceWriteSafetyException(currentStatus);
            if (change.Command == A1Command.SetMute &&
                (payload[0] >= currentStatus.Transmitters.Count || !currentStatus.Transmitters[payload[0]].Connected))
                throw new DeviceWriteSafetyException(currentStatus, payload[0]);
            A1Frame acknowledgment = await ExchangeAsync(change.Command, payload, linked.Token).ConfigureAwait(false);
            if (acknowledgment.Payload.Length < 1 || acknowledgment.Payload[0] != 0)
                throw new InvalidDataException("Device rejected the setting.");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(150, linked.Token).ConfigureAwait(false);
                A1Status status = await ReadStatusCoreAsync(linked.Token).ConfigureAwait(false);
                try { scope?.Validate(status, change with { Payload = payload }); }
                catch (MicrophoneScopeException exception)
                {
                    exception.WriteAttempted = true;
                    throw;
                }
                if (change.IsConfirmed(status))
                    return status;
            }
            throw new InvalidDataException("The device did not confirm the requested value in its status.");
        }
        finally { transactionLock.Release(); }
    }

    private async Task<A1Status> ReadStatusCoreAsync(CancellationToken cancellationToken)
    {
        A1Frame frame = await ExchangeAsync(A1Command.GetStatus, [], cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length is >= 18 and <= 22)
            throw new InvalidDataException("Status payload contains an incomplete transmitter extension.");
        A1Status status = A1Status.Parse(frame.Payload);
        if (status.Transmitters.Count < maximumTransmitterCount)
            throw new InvalidDataException("Status omitted transmitters established in this session. Reconnect to establish a new topology.");
        maximumTransmitterCount = Math.Max(maximumTransmitterCount, status.Transmitters.Count);
        return status;
    }

    private async Task<A1Frame> ExchangeAsync(A1Command command, byte[] payload, CancellationToken cancellationToken,
        Func<A1Frame, bool>? matches = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] report = A1Protocol.Encode(command, payload);
        var completion = new TaskCompletionSource<A1Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new PendingResponse(command, completion, matches);
        lock (responseLock) { pending = response; }
        try
        {
            Trace?.Invoke($"TX {Convert.ToHexString(report.AsSpan(0, payload.Length + 8))}");
            await Task.Run(() => transport.SendFeature(report), cancellationToken).WaitAsync(
                TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            lock (responseLock)
                if (ReferenceEquals(pending, response))
                    pending = null;
        }
    }

    private void OnReport(byte[] report)
    {
        if (!A1Protocol.TryDecode(report, out A1Frame? frame) || frame is null)
        {
            Trace?.Invoke($"RX rejected: {Convert.ToHexString(report.AsSpan(0, Math.Min(report.Length, 64)))}");
            return;
        }
        Trace?.Invoke($"RX command={(byte)frame.Command:X2} payload={Convert.ToHexString(frame.Payload)}");
        lock (responseLock)
            if (pending?.Command == frame.Command && (pending.Matches is null || pending.Matches(frame)))
                pending.Completion.TrySetResult(frame);
    }

    private void OnFailure(Exception exception)
    {
        lock (responseLock) { pending?.Completion.TrySetException(exception); }
    }

    public void Dispose()
    {
        lock (responseLock)
        {
            if (disposed) return;
            disposed = true;
            pending?.Completion.TrySetCanceled();
        }
        lifetime.Cancel();
        transport.ReportReceived -= OnReport;
        transport.Failed -= OnFailure;
        transport.Dispose();
    }
}
