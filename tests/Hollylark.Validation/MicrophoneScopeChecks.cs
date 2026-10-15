using System.IO;
using LarkControl.Core;

internal static partial class Program
{
    private static readonly int[] MicrophoneScopeConnectionOffsets = [0, 1, 17, 18];
    private static readonly int[] MicrophoneScopeMuteOffsets = [4, 5, 21, 22];

    public static async Task VerifyMicrophoneScopeChecks()
    {
        VerifyMicrophoneScopeConstruction();
        await VerifyIndividualMicrophoneSettings();
        await VerifyMissingMicrophoneSelection();
        await VerifyIndividualMicrophoneMute();
        await VerifyMicrophoneScopeGroups();
        await VerifyMicrophoneScopeArguments();
        await VerifyMicrophoneScopeReadback();
        await VerifyQueuedMicrophoneScope();
        await VerifyMicrophoneScopePayloadSnapshot();
        await VerifyMicrophoneScopeLegacySafety();
    }

    private static void VerifyMicrophoneScopeConstruction()
    {
        for (int index = 0; index < 4; index++)
        {
            MicrophoneControlScope scope = MicrophoneControlScope.Individual(index);
            Check(scope.TransmitterIndex == index && scope.ConnectedIndices.Count == 0,
                $"Individual scope stores TX{index + 1} without a group snapshot");
        }
        Throws<ArgumentOutOfRangeException>(() => MicrophoneControlScope.Individual(-1), "Scope rejects negative TX index");
        Throws<ArgumentOutOfRangeException>(() => MicrophoneControlScope.Individual(4), "Scope rejects TX index above three");
        Throws<ArgumentNullException>(() => MicrophoneControlScope.All(null!), "Group scope rejects null indices");
        int[][] invalidGroups = [[], [0], [0, 0], [0, 1, 1], [-1, 1], [0, 4]];
        foreach (int[] indices in invalidGroups)
            Throws<ArgumentException>(() => MicrophoneControlScope.All(indices),
                $"Group scope rejects invalid indices [{string.Join(',', indices)}]");

        int[] original = [3, 0, 2, 1];
        MicrophoneControlScope group = MicrophoneControlScope.All(original);
        Check(original.SequenceEqual(new[] { 3, 0, 2, 1 }) && group.TransmitterIndex is null &&
            group.ConnectedIndices.SequenceEqual(new[] { 0, 1, 2, 3 }), "Group scope copies and sorts without changing its source");
        original[0] = 0;
        var exposed = (IList<int>)group.ConnectedIndices;
        Check(exposed.IsReadOnly, "Group scope exposes a read-only collection");
        Throws<NotSupportedException>(() => exposed[0] = 3, "Group snapshot cannot be changed through IList");
        Throws<NotSupportedException>(() => exposed.Add(0), "Group snapshot cannot be extended through IList");
        Throws<NotSupportedException>(() => exposed.Clear(), "Group snapshot cannot be cleared through IList");
        Check(group.ConnectedIndices.SequenceEqual(new[] { 0, 1, 2, 3 }), "Caller mutations cannot change a group snapshot");
        var source = new List<int> { 3, 1 };
        MicrophoneControlScope enumerableGroup = MicrophoneControlScope.All(source.Where(index => index > 0));
        source.Clear();
        Check(enumerableGroup.ConnectedIndices.SequenceEqual(new[] { 1, 3 }), "Group scope eagerly captures arbitrary enumerables");

        A1Status status = A1Status.Parse(ValidStatus);
        MicrophoneControlScope individual = MicrophoneControlScope.Individual(0);
        Throws<ArgumentNullException>(() => individual.Validate(null!, SettingChange.Gain(4)), "Scope validation requires status");
        Throws<ArgumentNullException>(() => individual.Validate(status, null!), "Scope validation requires a setting change");
        Throws<ArgumentNullException>(() => individual.Validate(status, new SettingChange(A1Command.SetMute, null!, _ => true)),
            "Scope validation requires a setting payload");
        foreach (MicrophoneScopeReason reason in Enum.GetValues<MicrophoneScopeReason>())
        {
            var exception = new MicrophoneScopeException(status, reason);
            Check(ReferenceEquals(status, exception.Status) && exception.Reason == reason && exception.Message.Length > 0,
                $"Scope exception preserves status and localizable reason {reason}");
        }
        Throws<ArgumentNullException>(() => new MicrophoneScopeException(null!, MicrophoneScopeReason.SelectedUnavailable),
            "Scope exception requires status");
        Throws<ArgumentOutOfRangeException>(() => new MicrophoneScopeException(status, (MicrophoneScopeReason)99),
            "Scope exception rejects unknown reasons");
    }

    private static async Task VerifyIndividualMicrophoneSettings()
    {
        SettingChange[] changes =
        [
            SettingChange.Gain(4), SettingChange.ChannelMode(1), SettingChange.Noise(false, 1), SettingChange.Noise(true, 1),
            SettingChange.Equalizer(2), SettingChange.Reverb(false, 1), SettingChange.Reverb(true, 1),
            SettingChange.Lights(false), SettingChange.Lights(true), SettingChange.Identification(true),
            SettingChange.Identification(false), SettingChange.AutoOff(1), SettingChange.AutoOff(0)
        ];
        foreach (SettingChange change in changes)
        for (int index = 0; index < 2; index++)
        {
            using var transport = new FakeTransport();
            transport.SetTransmitterConnected(1 - index, false);
            using var controller = new A1Controller(transport);
            MicrophoneControlScope scope = MicrophoneControlScope.Individual(index);
            A1Status confirmed = await controller.ApplyAsync(change, scope: scope);
            string name = $"Individual TX{index + 1} {change.Command} {Convert.ToHexString(change.Payload)}";
            Check(change.IsConfirmed(confirmed) && GetMicrophoneScopeIndices(confirmed).SequenceEqual(new[] { index }),
                $"{name} permits a receiver-wide setting for the sole connected transmitter");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, change.Command, A1Command.GetStatus }),
                $"{name} retains the fresh read, acknowledgment and confirmation");

            transport.SetTransmitterConnected(1 - index, true);
            await ExpectMicrophoneScopeFailure(() => controller.ApplyAsync(change, scope: scope),
                MicrophoneScopeReason.SharedParameters, [0, 1], $"{name} rejects another transmitter arriving after readback");
            Check(transport.Commands.SequenceEqual(new[]
                { A1Command.GetStatus, change.Command, A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
                $"{name} rejection adds only a fresh read and preserves the session");
        }

        for (int index = 0; index < 4; index++)
        {
            byte[] status = CreateMicrophoneScopeStatus(4, [index]);
            using var transport = CreateMicrophoneScopeTransport(status);
            using var controller = new A1Controller(transport);
            Check((await controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.Individual(index))).Gain == 4,
                $"Four-TX status permits receiver-wide changes when only TX{index + 1} is connected");
            Check(transport.Requests[1].Payload.SequenceEqual(new byte[] { 4 }),
                "Scoped gain keeps the receiver-wide wire payload without inventing a selector");
        }
    }

    private static async Task VerifyMissingMicrophoneSelection()
    {
        foreach (int transmitterCount in new[] { 2, 4 })
        for (int index = 0; index < 4; index++)
        foreach (bool receiverOnly in new[] { false, true })
        foreach (SettingChange change in new[] { SettingChange.Gain(4), SettingChange.Mute(index, true) })
        {
            int[] connectedIndices = receiverOnly ? [] : [(index + 1) % transmitterCount];
            byte[] status = CreateMicrophoneScopeStatus(transmitterCount, connectedIndices);
            using var transport = CreateMicrophoneScopeTransport(status);
            using var controller = new A1Controller(transport);
            string name = $"Missing TX{index + 1}, {transmitterCount}-TX status, receiver-only {receiverOnly}, {change.Command}";
            MicrophoneScopeException exception = await ExpectMicrophoneScopeFailure(
                () => controller.ApplyAsync(change, scope: MicrophoneControlScope.Individual(index)),
                MicrophoneScopeReason.SelectedUnavailable, connectedIndices, name);
            Check(exception.Status.Transmitters.Count == transmitterCount && transport.Requests.Length == 1 &&
                transport.Requests[0].Command == A1Command.GetStatus && !transport.Disposed,
                $"{name} reports fresh status without a write or disconnection");
        }
    }

    private static async Task VerifyIndividualMicrophoneMute()
    {
        foreach (int transmitterCount in new[] { 2, 4 })
        for (int index = 0; index < transmitterCount; index++)
        foreach (bool muted in new[] { false, true })
        {
            byte[] status = CreateMicrophoneScopeStatus(transmitterCount, Enumerable.Range(0, transmitterCount).ToArray());
            A1Status before = A1Status.Parse(status);
            using var transport = CreateMicrophoneScopeTransport(status);
            using var controller = new A1Controller(transport);
            A1Status confirmed = await controller.ApplyAsync(SettingChange.Mute(index, muted),
                scope: MicrophoneControlScope.Individual(index));
            Check(confirmed.Transmitters[index].Muted == muted && Enumerable.Range(0, transmitterCount)
                .Where(otherIndex => otherIndex != index)
                .All(otherIndex => confirmed.Transmitters[otherIndex] == before.Transmitters[otherIndex]),
                $"Individual TX{index + 1} mute {muted} leaves other connected transmitters unchanged in {transmitterCount}-TX status");
            Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
                { A1Command.GetStatus, A1Command.SetMute, A1Command.GetStatus }) &&
                transport.Requests[1].Payload.SequenceEqual(new byte[] { (byte)index, muted ? (byte)1 : (byte)0 }),
                "Individual mute sends exactly the requested indexed command");
        }
    }

    private static async Task VerifyMicrophoneScopeGroups()
    {
        foreach (int transmitterCount in new[] { 2, 4 })
        for (int snapshotMask = 0; snapshotMask < 1 << transmitterCount; snapshotMask++)
        {
            int[] snapshot = GetMicrophoneScopeMaskIndices(transmitterCount, snapshotMask);
            if (snapshot.Length < 2) continue;
            MicrophoneControlScope scope = MicrophoneControlScope.All(snapshot.Reverse());
            for (int currentMask = 0; currentMask < 1 << transmitterCount; currentMask++)
            foreach (SettingChange change in new[] { SettingChange.Gain(4), SettingChange.Mute(snapshot[^1], true) })
            {
                int[] connectedIndices = GetMicrophoneScopeMaskIndices(transmitterCount, currentMask);
                byte[] status = CreateMicrophoneScopeStatus(transmitterCount, snapshot);
                using var transport = CreateMicrophoneScopeTransport(status);
                using var controller = new A1Controller(transport);
                A1Status cached = await controller.ReadStatusAsync();
                SetMicrophoneScopeConnections(status, connectedIndices);
                string name = $"Group [{string.Join(',', snapshot)}] to [{string.Join(',', connectedIndices)}], {transmitterCount}-TX, {change.Command}";
                Check(GetMicrophoneScopeIndices(cached).SequenceEqual(snapshot), $"{name} starts with the captured topology");
                if (connectedIndices.Length < 2 || !snapshot.SequenceEqual(connectedIndices))
                {
                    MicrophoneScopeReason reason = connectedIndices.Length < 2
                        ? MicrophoneScopeReason.GroupUnavailable : MicrophoneScopeReason.GroupChanged;
                    await ExpectMicrophoneScopeFailure(() => controller.ApplyAsync(change, scope: scope), reason, connectedIndices, name);
                    Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
                        { A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
                        $"{name} refuses writes against fresh topology and keeps the receiver session");
                }
                else
                {
                    A1Status confirmed = await controller.ApplyAsync(change, scope: scope);
                    Check(change.IsConfirmed(confirmed) && GetMicrophoneScopeIndices(confirmed).SequenceEqual(snapshot),
                        $"{name} permits the exact group");
                    Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
                        { A1Command.GetStatus, A1Command.GetStatus, change.Command, A1Command.GetStatus }) &&
                        transport.Requests[2].Payload.SequenceEqual(change.Payload),
                        $"{name} sends one unchanged command with acknowledgment and readback");
                }
            }
        }
    }

    private static async Task VerifyMicrophoneScopeArguments()
    {
        (MicrophoneControlScope Scope, int Target, int[] Connected)[] wrongTargets =
        [
            (MicrophoneControlScope.Individual(0), 1, [0, 1]),
            (MicrophoneControlScope.Individual(0), 1, []),
            (MicrophoneControlScope.Individual(3), 2, [0, 1, 2, 3]),
            (MicrophoneControlScope.All([0, 2]), 1, [0, 2]),
            (MicrophoneControlScope.All([0, 1]), 2, [0, 1, 2]),
            (MicrophoneControlScope.All([0, 1]), 3, [])
        ];
        foreach (var scenario in wrongTargets)
        {
            byte[] status = CreateMicrophoneScopeStatus(4, scenario.Connected);
            SettingChange change = SettingChange.Mute(scenario.Target, true);
            Throws<ArgumentException>(() => scenario.Scope.Validate(A1Status.Parse(status), change),
                "Direct scope validation rejects a wrong target even when connectivity also changed");
            using var transport = CreateMicrophoneScopeTransport(status);
            using var controller = new A1Controller(transport);
            await ThrowsAsync<ArgumentException>(() => controller.ApplyAsync(change, scope: scenario.Scope),
                "Scoped mute rejects rather than retargeting a command");
            Check(transport.Requests.Length == 1 && transport.Requests[0].Command == A1Command.GetStatus && !transport.Disposed,
                "Wrong-target rejection sends only the fresh read");
        }

        byte[][] invalidPayloads = [[], [0], [0, 0, 0], [4, 0], [0, 2]];
        foreach (MicrophoneControlScope scope in new[] { MicrophoneControlScope.Individual(0), MicrophoneControlScope.All([0, 1]) })
        foreach (byte[] payload in invalidPayloads)
        {
            var change = new SettingChange(A1Command.SetMute, payload, _ => true);
            Throws<ArgumentException>(() => scope.Validate(A1Status.Parse(ValidStatus), change),
                "Direct scope validation rejects malformed mute payloads");
            using var transport = new FakeTransport();
            using var controller = new A1Controller(transport);
            await ThrowsAsync<ArgumentException>(() => controller.ApplyAsync(change, scope: scope),
                "Scoped writes preserve malformed mute payload validation");
            Check(transport.Commands.Count == 0 && !transport.Disposed, "Malformed scoped mute never reaches the transport");
        }
    }

    private static async Task VerifyMicrophoneScopeReadback()
    {
        foreach (int transmitterCount in new[] { 2, 4 })
        {
            int[] allIndices = Enumerable.Range(0, transmitterCount).ToArray();
            for (int index = 0; index < transmitterCount; index++)
            {
                MicrophoneControlScope scope = MicrophoneControlScope.Individual(index);
                await VerifyMicrophoneScopeReadbackScenario(transmitterCount, [index], allIndices, scope,
                    SettingChange.Gain(4), MicrophoneScopeReason.SharedParameters);
                await VerifyMicrophoneScopeReadbackScenario(transmitterCount, allIndices, [index], scope,
                    SettingChange.Mute(index, true), null);
                foreach (SettingChange change in new[] { SettingChange.Gain(4), SettingChange.Mute(index, true) })
                    await VerifyMicrophoneScopeReadbackScenario(transmitterCount, [index], [], scope,
                        change, MicrophoneScopeReason.SelectedUnavailable);
            }

            MicrophoneControlScope group = MicrophoneControlScope.All(allIndices);
            foreach (int[] remaining in new int[][] { [], [0] })
            foreach (SettingChange change in new[] { SettingChange.Gain(4), SettingChange.Mute(0, true) })
                await VerifyMicrophoneScopeReadbackScenario(transmitterCount, allIndices, remaining, group,
                    change, MicrophoneScopeReason.GroupUnavailable);
        }

        (int[] Initial, int[] Remaining)[] changedGroups =
        [
            ([0, 1], [0, 2]),
            ([0, 1], [0, 1, 2]),
            ([0, 1], [0, 1, 2, 3]),
            ([0, 1, 2, 3], [0, 1]),
            ([0, 1, 2, 3], [0, 1, 2])
        ];
        foreach (var scenario in changedGroups)
        foreach (SettingChange change in new[] { SettingChange.Gain(4), SettingChange.Mute(0, true) })
            await VerifyMicrophoneScopeReadbackScenario(4, scenario.Initial, scenario.Remaining,
                MicrophoneControlScope.All(scenario.Initial), change, MicrophoneScopeReason.GroupChanged);
    }

    private static async Task VerifyMicrophoneScopeReadbackScenario(int transmitterCount, int[] initialIndices,
        int[] readbackIndices, MicrophoneControlScope scope, SettingChange change, MicrophoneScopeReason? reason)
    {
        byte[] status = CreateMicrophoneScopeStatus(transmitterCount, initialIndices);
        using var transport = CreateMicrophoneScopeTransport(status, () => SetMicrophoneScopeConnections(status, readbackIndices));
        using var controller = new A1Controller(transport);
        string name = $"Scoped {change.Command} readback [{string.Join(',', initialIndices)}] to [{string.Join(',', readbackIndices)}]";
        if (reason is MicrophoneScopeReason failureReason)
            await ExpectMicrophoneScopeFailure(() => controller.ApplyAsync(change, scope: scope), failureReason, readbackIndices, name,
                writeAttempted: true);
        else
        {
            A1Status confirmed = await controller.ApplyAsync(change, scope: scope);
            Check(change.IsConfirmed(confirmed) && GetMicrophoneScopeIndices(confirmed).SequenceEqual(readbackIndices),
                $"{name} allows an individual mute when only other transmitters change");
        }
        Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
            { A1Command.GetStatus, change.Command, A1Command.GetStatus }) && !transport.Disposed,
            $"{name} validates readback without retries, rollback or additional writes");
        int changedOffset = change.Command == A1Command.SetMute ? MicrophoneScopeMuteOffsets[change.Payload[0]] : 8;
        byte requestedValue = change.Command == A1Command.SetMute ? change.Payload[1] : change.Payload[0];
        Check(status[changedOffset] == requestedValue, $"{name} does not imply that a rejected scoped result undid the write");
    }

    private static async Task VerifyQueuedMicrophoneScope()
    {
        byte[] status = CreateMicrophoneScopeStatus(2, [0]);
        var writeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new DeviceWriteSafetyTransport
        {
            Respond = (source, request) =>
            {
                switch (request.Command)
                {
                    case A1Command.GetStatus:
                        source.Deliver(request.Command, status);
                        break;
                    case A1Command.SetGain:
                        status[8] = request.Payload[0];
                        writeStarted.TrySetResult(true);
                        break;
                    case A1Command.SetLight:
                        status[11] = request.Payload[0];
                        source.Deliver(request.Command, [0]);
                        break;
                    default:
                        throw new InvalidDataException("Unexpected queued microphone-scope test command.");
                }
            }
        };
        using var controller = new A1Controller(transport);
        MicrophoneControlScope scope = MicrophoneControlScope.Individual(0);
        Task<A1Status> first = controller.ApplyAsync(SettingChange.Gain(4), scope: scope);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<A1Status> queued = controller.ApplyAsync(SettingChange.Lights(false), scope: scope);
        Check(!queued.IsCompleted && transport.Requests.Select(request => request.Command)
            .SequenceEqual(new[] { A1Command.GetStatus, A1Command.SetGain }), "Scoped writes wait for the existing transaction lock");
        SetMicrophoneScopeConnections(status, [0, 1]);
        transport.Deliver(A1Command.SetGain, [0]);
        await ExpectMicrophoneScopeFailure(() => first, MicrophoneScopeReason.SharedParameters, [0, 1],
            "A transmitter arriving after sending prevents scoped success even when the setting was applied", writeAttempted: true);
        await ExpectMicrophoneScopeFailure(() => queued, MicrophoneScopeReason.SharedParameters, [0, 1],
            "Queued scope validation sees a transmitter arriving while waiting for the lock");
        Check(transport.Requests.Select(request => request.Command).SequenceEqual(new[]
            { A1Command.GetStatus, A1Command.SetGain, A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
            "Queued scope refusal has its own fresh read without a setting command");
        SetMicrophoneScopeConnections(status, [0]);
        Check(!(await controller.ApplyAsync(SettingChange.Lights(false), scope: scope)).LightsEnabled,
            "Scope rejection releases the transaction lock and allows recovery in the same session");
    }

    private static async Task VerifyMicrophoneScopePayloadSnapshot()
    {
        foreach (int capturedTarget in new[] { 0, 1 })
        {
            byte[] status = CreateMicrophoneScopeStatus(2, [0, 1]);
            byte[] payload = [(byte)capturedTarget, 1];
            int statusRequests = 0;
            var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new DeviceWriteSafetyTransport
            {
                Respond = (source, request) =>
                {
                    if (request.Command == A1Command.GetStatus)
                    {
                        if (++statusRequests == 1) readStarted.TrySetResult(true);
                        else source.Deliver(request.Command, status);
                    }
                    else if (request.Command == A1Command.SetMute)
                    {
                        status[MicrophoneScopeMuteOffsets[request.Payload[0]]] = request.Payload[1];
                        source.Deliver(request.Command, [0]);
                    }
                    else throw new InvalidDataException("Unexpected microphone-scope payload test command.");
                }
            };
            using var controller = new A1Controller(transport);
            Task<A1Status> applying = controller.ApplyAsync(new SettingChange(A1Command.SetMute, payload,
                state => state.Transmitters[0].Muted), scope: MicrophoneControlScope.Individual(0));
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            payload[0] = (byte)(1 - capturedTarget);
            transport.Deliver(A1Command.GetStatus, status);
            if (capturedTarget == 0)
            {
                Check((await applying).Transmitters[0].Muted && transport.Requests.Length == 3 &&
                    transport.Requests[1].Command == A1Command.SetMute && transport.Requests[1].Payload.SequenceEqual(new byte[] { 0, 1 }),
                    "Scope validation and write use the same captured payload despite caller mutation");
            }
            else
            {
                await ThrowsAsync<ArgumentException>(() => applying, "Caller mutation cannot authorize an originally out-of-scope mute");
                Check(transport.Requests.Length == 1 && transport.Requests[0].Command == A1Command.GetStatus,
                    "An out-of-scope captured payload emits no write after caller mutation");
            }
            Check(!transport.Disposed, "Payload snapshot validation preserves the session");
        }
    }

    private static async Task VerifyMicrophoneScopeLegacySafety()
    {
        using (var transport = new FakeTransport())
        using (var controller = new A1Controller(transport))
        {
            Check((await controller.ApplyAsync(SettingChange.Gain(4), CancellationToken.None)).Gain == 4,
                "Original positional cancellation overload permits unscoped receiver-wide writes");
            transport.SetTransmitterConnected(0, false);
            DeviceWriteSafetyException missing = await ExpectDeviceWriteSafetyFailure(
                () => controller.ApplyAsync(SettingChange.Mute(0, true), CancellationToken.None, null),
                "Explicit null scope preserves the disconnected mute safety guard");
            Check(missing.Reason == DeviceWriteSafetyReason.TargetTransmitterDisconnected, "Null scope preserves the original mute reason");
            transport.SetTransmitterConnected(1, false);
            DeviceWriteSafetyException offline = await ExpectDeviceWriteSafetyFailure(
                () => controller.ApplyAsync(SettingChange.Lights(false)), "Omitted scope preserves the no-transmitter safety guard");
            Check(offline.Reason == DeviceWriteSafetyReason.NoConnectedTransmitter && transport.Commands.SequenceEqual(new[]
                { A1Command.GetStatus, A1Command.SetGain, A1Command.GetStatus, A1Command.GetStatus, A1Command.GetStatus }) && !transport.Disposed,
                "Original safety rejections emit no setting writes and retain their reasons");
        }
        using (var transport = new FakeTransport { StatusPayloadLength = 14 })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.All([0, 1])),
                "Invalid fresh status cannot authorize a scoped write");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus }), "Invalid scoped preflight emits no write");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var transport = new FakeTransport())
        using (var controller = new A1Controller(transport))
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => controller.ApplyAsync(SettingChange.Gain(4), cancellation.Token,
                MicrophoneControlScope.All([0, 1])), "Scoped writes preserve positional cancellation");
            Check(transport.Commands.Count == 0, "Cancelled scoped write never reaches the transport");
        }
    }

    private static int[] GetMicrophoneScopeIndices(A1Status status) => Enumerable.Range(0, status.Transmitters.Count)
        .Where(index => status.Transmitters[index].Connected).ToArray();

    private static int[] GetMicrophoneScopeMaskIndices(int transmitterCount, int mask) => Enumerable.Range(0, transmitterCount)
        .Where(index => (mask & (1 << index)) != 0).ToArray();

    private static byte[] CreateMicrophoneScopeStatus(int transmitterCount, int[] connectedIndices)
    {
        byte[] status = transmitterCount == 4 ? [.. ValidStatus, 0, 0, 80, 75, 0, 0] : (byte[])ValidStatus.Clone();
        SetMicrophoneScopeConnections(status, connectedIndices);
        return status;
    }

    private static void SetMicrophoneScopeConnections(byte[] status, int[] connectedIndices)
    {
        int transmitterCount = status.Length >= 23 ? 4 : 2;
        for (int index = 0; index < transmitterCount; index++)
            status[MicrophoneScopeConnectionOffsets[index]] = connectedIndices.Contains(index) ? (byte)1 : (byte)0;
    }

    private static DeviceWriteSafetyTransport CreateMicrophoneScopeTransport(byte[] status, Action? afterWrite = null) => new()
    {
        Respond = (source, request) =>
        {
            switch (request.Command)
            {
                case A1Command.GetStatus:
                    source.Deliver(request.Command, status);
                    return;
                case A1Command.SetGain:
                    status[8] = request.Payload[0];
                    break;
                case A1Command.SetMute:
                    status[MicrophoneScopeMuteOffsets[request.Payload[0]]] = request.Payload[1];
                    break;
                default:
                    throw new InvalidDataException("Unexpected microphone-scope test command.");
            }
            afterWrite?.Invoke();
            source.Deliver(request.Command, [0]);
        }
    };

    private static async Task<MicrophoneScopeException> ExpectMicrophoneScopeFailure(Func<Task> action,
        MicrophoneScopeReason reason, int[] connectedIndices, string name, bool writeAttempted = false)
    {
        try { await action(); }
        catch (MicrophoneScopeException exception)
        {
            Check(exception.Reason == reason && exception.WriteAttempted == writeAttempted
                && GetMicrophoneScopeIndices(exception.Status).SequenceEqual(connectedIndices),
                $"{name} returns {reason} with fresh connected indices");
            return exception;
        }
        throw new InvalidOperationException("FAIL: " + name);
    }
}
