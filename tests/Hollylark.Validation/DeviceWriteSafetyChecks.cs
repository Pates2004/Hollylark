using System.IO;
using LarkControl.Core;

internal static partial class Program
{
    private static async Task VerifyDeviceWriteSafetyChecks()
    {
        await VerifyAllDeviceWritesRequireConnectedTransmitter();
        await VerifyMuteRequiresConnectedTarget();
        await VerifyExtendedTransmitterWriteSafety();
        await VerifyQueuedDeviceWriteSafety();
        await VerifyDeviceWritePayloadSnapshot();
        await VerifyReceiverOnlyReads();
        await VerifyDeviceWritePreflightFailures();
    }

    private static async Task VerifyAllDeviceWritesRequireConnectedTransmitter()
    {
        SettingChange[] changes =
        [
            SettingChange.Gain(4), SettingChange.ChannelMode(1),
            SettingChange.Noise(false, 1), SettingChange.Noise(true, 1), SettingChange.Equalizer(2),
            SettingChange.Reverb(false, 1), SettingChange.Reverb(true, 1),
            SettingChange.Lights(false), SettingChange.Lights(true),
            SettingChange.Identification(true), SettingChange.Identification(false),
            SettingChange.AutoOff(1), SettingChange.AutoOff(0),
            SettingChange.Mute(0, true), SettingChange.Mute(0, false),
            SettingChange.Mute(1, true), SettingChange.Mute(1, false)
        ];
        foreach (SettingChange change in changes)
        {
            using var transport = new FakeTransport();
            using var controller = new A1Controller(transport);
            A1Status cached = await controller.ReadStatusAsync();
            transport.SetTransmitterConnected(0, false);
            transport.SetTransmitterConnected(1, false);
            string name = $"{change.Command} {Convert.ToHexString(change.Payload)}";

            DeviceWriteSafetyException exception = await ExpectDeviceWriteSafetyFailure(
                () => controller.ApplyAsync(change), $"{name} refuses a fresh disconnect");
            Check(cached.Transmitters.All(transmitter => transmitter.Connected) &&
                exception.Status.Transmitters.All(transmitter => !transmitter.Connected),
                $"{name} supplies fresh status rather than the earlier connected status");
            Check(exception.Reason == DeviceWriteSafetyReason.NoConnectedTransmitter && exception.TransmitterIndex is null,
                $"{name} exposes a localizable no-transmitter reason");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
                $"{name} sends only the safety read and preserves the session");

            int connectedIndex = change.Command == A1Command.SetMute ? change.Payload[0] : 1;
            transport.SetTransmitterConnected(connectedIndex, true);
            A1Status confirmed = await controller.ApplyAsync(change);
            Check(change.IsConfirmed(confirmed) && confirmed.Transmitters.Count(transmitter => transmitter.Connected) == 1,
                $"{name} recovers in the same session with only one connected transmitter");
            Check(transport.Commands.TakeLast(3).SequenceEqual(new[] { A1Command.GetStatus, change.Command, A1Command.GetStatus }) &&
                !transport.Disposed, $"{name} retains acknowledgment and readback after its fresh safety read");
        }
    }

    private static async Task VerifyMuteRequiresConnectedTarget()
    {
        for (int index = 0; index < 4; index++)
        foreach (bool muted in new[] { false, true })
        {
            using var transport = new FakeTransport();
            using var controller = new A1Controller(transport);
            A1Status cached = await controller.ReadStatusAsync();
            if (index < cached.Transmitters.Count)
                transport.SetTransmitterConnected(index, false);
            DeviceWriteSafetyException exception = await ExpectDeviceWriteSafetyFailure(
                () => controller.ApplyAsync(SettingChange.Mute(index, muted)),
                $"Mute {muted} requires connected TX{index + 1}, not merely another transmitter");
            Check(exception.Reason == DeviceWriteSafetyReason.TargetTransmitterDisconnected && exception.TransmitterIndex == index &&
                exception.Status.Transmitters.Any(transmitter => transmitter.Connected) &&
                (index >= exception.Status.Transmitters.Count || !exception.Status.Transmitters[index].Connected),
                $"Mute TX{index + 1} distinguishes a disconnected or absent target using fresh status");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
                $"Blocked mute TX{index + 1} does not write or disconnect the session");
            Check((await controller.ReadStatusAsync()).Transmitters.Any(transmitter => transmitter.Connected),
                $"Status remains readable after blocking mute TX{index + 1}");
        }
    }

    private static async Task VerifyExtendedTransmitterWriteSafety()
    {
        int[] connectionOffsets = [0, 1, 17, 18];
        int[] muteOffsets = [4, 5, 21, 22];
        for (int index = 0; index < 4; index++)
        foreach (bool muted in new[] { false, true })
        {
            byte[] status = [.. ValidStatus, 0, 0, 80, 75, 0, 0];
            status[0] = 0;
            status[1] = 0;
            status[connectionOffsets[index]] = 1;
            status[muteOffsets[index]] = muted ? (byte)0 : (byte)1;
            using var transport = new DeviceWriteSafetyTransport
            {
                Respond = (source, request) =>
                {
                    switch (request.Command)
                    {
                        case A1Command.GetStatus:
                            source.Deliver(request.Command, status);
                            break;
                        case A1Command.SetMute:
                            status[muteOffsets[request.Payload[0]]] = request.Payload[1];
                            source.Deliver(request.Command, [0]);
                            break;
                        case A1Command.SetLight:
                            status[11] = request.Payload[0];
                            source.Deliver(request.Command, [0]);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected extended safety test command.");
                    }
                }
            };
            using var controller = new A1Controller(transport);
            A1Status confirmed = await controller.ApplyAsync(SettingChange.Mute(index, muted));
            Check(confirmed.Transmitters.Count == 4 && confirmed.Transmitters[index].Muted == muted &&
                confirmed.Transmitters.Count(transmitter => transmitter.Connected) == 1,
                $"Extended status permits mute {muted} with only TX{index + 1} connected");
            Check(!(await controller.ApplyAsync(SettingChange.Lights(false))).LightsEnabled,
                $"Only TX{index + 1} connected also permits global writes");

            status[connectionOffsets[index]] = 0;
            status[connectionOffsets[(index + 1) % 4]] = 1;
            DeviceWriteSafetyException exception = await ExpectDeviceWriteSafetyFailure(
                () => controller.ApplyAsync(SettingChange.Mute(index, !muted)),
                $"Extended target TX{index + 1} is checked again after unplugging");
            Check(exception.Reason == DeviceWriteSafetyReason.TargetTransmitterDisconnected && exception.TransmitterIndex == index &&
                exception.Status.Transmitters.Count == 4 && !exception.Status.Transmitters[index].Connected,
                $"Extended target TX{index + 1} refusal carries its fresh four-transmitter status");
            Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
                {
                    A1Command.GetStatus, A1Command.SetMute, A1Command.GetStatus,
                    A1Command.GetStatus, A1Command.SetLight, A1Command.GetStatus, A1Command.GetStatus
                }) && !transport.Disposed, $"Extended target TX{index + 1} refusal emits no setting command");
        }
    }

    private static async Task VerifyQueuedDeviceWriteSafety()
    {
        byte[] status = (byte[])ValidStatus.Clone();
        var writeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new DeviceWriteSafetyTransport
        {
            Respond = (source, request) =>
            {
                if (request.Command == A1Command.GetStatus)
                    source.Deliver(request.Command, status);
                else if (request.Command == A1Command.SetGain)
                {
                    status[8] = request.Payload[0];
                    writeStarted.TrySetResult(true);
                }
                else
                    throw new InvalidDataException("Unexpected queued safety test command.");
            }
        };
        using var controller = new A1Controller(transport);
        Task<A1Status> first = controller.ApplyAsync(SettingChange.Gain(4));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<A1Status> queued = controller.ApplyAsync(SettingChange.Lights(false));
        Check(!queued.IsCompleted && transport.Requests.Select(request => request.Command)
            .SequenceEqual(new[] { A1Command.GetStatus, A1Command.SetGain }),
            "Queued safety read cannot enter a transaction waiting for acknowledgment");
        status[0] = 0;
        status[1] = 0;
        transport.Deliver(A1Command.SetGain, [0]);
        Check((await first).Gain == 4, "Existing write readback remains allowed after transmitter disconnection");
        DeviceWriteSafetyException exception = await ExpectDeviceWriteSafetyFailure(() => queued,
            "Queued write checks connectivity only after entering the transaction lock");
        Check(exception.Status.Transmitters.All(transmitter => !transmitter.Connected) && !transport.Disposed,
            "Queued refusal carries the unplugged state and preserves the session");
        Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
            { A1Command.GetStatus, A1Command.SetGain, A1Command.GetStatus, A1Command.GetStatus }),
            "Queued write has its own fresh safety read after the previous transaction readback");
    }

    private static async Task VerifyDeviceWritePayloadSnapshot()
    {
        byte[] status = (byte[])ValidStatus.Clone();
        status[1] = 0;
        byte[] payload = [0, 1];
        int statusRequests = 0;
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new DeviceWriteSafetyTransport
        {
            Respond = (source, request) =>
            {
                if (request.Command == A1Command.GetStatus)
                {
                    if (++statusRequests == 1)
                        readStarted.TrySetResult(true);
                    else
                        source.Deliver(request.Command, status);
                }
                else if (request.Command == A1Command.SetMute)
                {
                    status[4 + request.Payload[0]] = request.Payload[1];
                    source.Deliver(request.Command, [0]);
                }
                else
                    throw new InvalidDataException("Unexpected payload safety test command.");
            }
        };
        using var controller = new A1Controller(transport);
        Task<A1Status> applying = controller.ApplyAsync(new SettingChange(A1Command.SetMute, payload,
            state => state.Transmitters[0].Muted));
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        payload[0] = 1;
        transport.Deliver(A1Command.GetStatus, status);
        Check((await applying).Transmitters[0].Muted && !transport.Disposed,
            "Caller payload mutation cannot redirect an in-flight mute to a disconnected transmitter");
        Check(transport.Requests.Length == 3 && transport.Requests[1].Command == A1Command.SetMute &&
            transport.Requests[1].Payload.SequenceEqual(new byte[] { 0, 1 }),
            "Mute safety gate and write use the same captured payload");
    }

    private static async Task VerifyReceiverOnlyReads()
    {
        using var transport = new FakeTransport();
        transport.SetTransmitterConnected(0, false);
        transport.SetTransmitterConnected(1, false);
        using var controller = new A1Controller(transport);
        Check((await controller.ReadStatusAsync()).Transmitters.All(transmitter => !transmitter.Connected),
            "Receiver-only status reading is not gated");
        Check(await controller.ReadVersionAsync(1) == new Version(1, 0, 1, 3), "Disconnected transmitter firmware read is not gated");
        Check(await controller.ReadSerialNumberAsync(1) is not null, "Disconnected transmitter serial read is not gated");
        Check(await controller.ReadMacAddressAsync(0) is not null, "Receiver-only receiver MAC read is not gated");
        Check(await controller.ReadMacAddressAsync(1) is not null, "Disconnected transmitter MAC read is not gated");
        Check(transport.Commands.SequenceEqual(new[]
            {
                A1Command.GetStatus, A1Command.GetVersion, A1Command.GetSerialNumber,
                A1Command.GetReceiverMacAddress, A1Command.GetTransmitterMacAddress
            }) && !transport.Disposed, "Read-only requests add no transmitter safety checks or setting writes");
    }

    private static async Task VerifyDeviceWritePreflightFailures()
    {
        using (var transport = new FakeTransport { StatusPayloadLength = 14 })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(SettingChange.Gain(4)),
                "An incomplete safety status cannot authorize a write");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus }), "Invalid safety status emits no setting command");
        }
        using (var transport = new FakeTransport { Fail = true })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<IOException>(() => controller.ApplyAsync(SettingChange.Lights(false)),
                "A failed safety read preserves transport failure semantics");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus }) && transport.Disposed,
                "Transport failure still closes the session without a setting command");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var transport = new DeviceWriteSafetyTransport { Respond = (_, _) => cancellation.Cancel() })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<OperationCanceledException>(() => controller.ApplyAsync(SettingChange.AutoOff(1), cancellation.Token),
                "Cancellation during the safety read prevents auto-off writes");
            Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[] { A1Command.GetStatus }) && transport.Disposed,
                "Cancelled safety exchange retains transport disposal semantics");
        }
        byte[][] invalidMutePayloads = [[], [0], [0, 0, 0], [4, 0], [0, 2]];
        foreach (byte[] payload in invalidMutePayloads)
        {
            using var transport = new FakeTransport();
            using var controller = new A1Controller(transport);
            await ThrowsAsync<ArgumentException>(() => controller.ApplyAsync(new SettingChange(A1Command.SetMute, payload, _ => true)),
                $"Malformed mute payload {Convert.ToHexString(payload)} is rejected");
            Check(transport.Commands.Count == 0 && !transport.Disposed, "Malformed mute payload never reaches the transport");
        }
    }

    private static async Task<DeviceWriteSafetyException> ExpectDeviceWriteSafetyFailure(Func<Task> action, string name)
    {
        try { await action(); }
        catch (DeviceWriteSafetyException exception) { Check(true, name); return exception; }
        throw new InvalidOperationException("FAIL: " + name);
    }

    private sealed class DeviceWriteSafetyTransport : IA1Transport
    {
        private readonly List<A1Frame> requests = [];
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;
        public required Action<DeviceWriteSafetyTransport, A1Frame> Respond { get; init; }
        public bool Disposed { get; private set; }
        public A1Frame[] Requests { get { lock (requests) return requests.ToArray(); } }

        public void SendFeature(byte[] report)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (!A1Protocol.TryDecodeRequest(report, out A1Frame? request) || request is null)
                throw new InvalidDataException("Invalid device-write safety test request.");
            lock (requests) requests.Add(request);
            try { Respond(this, request); }
            catch (Exception exception) { Failed?.Invoke(exception); throw; }
        }

        public void Deliver(A1Command command, byte[] payload) => ReportReceived?.Invoke(Reply(command, payload));
        public void Dispose() => Disposed = true;
    }
}
