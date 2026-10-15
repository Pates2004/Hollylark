using System.IO;
using LarkControl.Core;

internal static partial class Program
{
    public static async Task VerifyStatusTopologySafetyChecks()
    {
        await VerifyTwoTransmitterStatusTopology();
        await VerifyStatusTopologyGrowth();
        await VerifyPartialStatusTopology();
        await VerifyStatusTopologyPreflight();
        await VerifyStatusTopologyReadback();
        await VerifyStatusTopologyEstablishedByReadback();
        await VerifyQueuedStatusTopology();
        await VerifyStatusTopologyReceiverVersion();
        await VerifyStatusTopologyVersionBoundaries();
        await VerifyStatusTopologySessionReset();
    }

    private static async Task VerifyTwoTransmitterStatusTopology()
    {
        int[] lengths = [15, 16, 17, 16, 15];
        var transport = new StatusTopologySafetyTransport(lengths.Select(length => CreateStatusTopologyPayload(length)).ToArray());
        using (var controller = new A1Controller(transport))
        {
            foreach (int length in lengths)
                Check((await controller.ReadStatusAsync()).Transmitters.Count == 2,
                    $"A {length}-byte legacy status preserves two-TX topology when optional fields vary");
            Check(transport.Commands.SequenceEqual(Enumerable.Repeat(A1Command.GetStatus, lengths.Length)),
                "Legacy topology reads issue only status requests");
        }

        foreach (int length in new[] { 15, 16, 17 })
        {
            var writeTransport = new StatusTopologySafetyTransport(
                CreateStatusTopologyPayload(length), CreateStatusTopologyPayload(length, gain: 4));
            using var controller = new A1Controller(writeTransport);
            A1Status status = await controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.All([0, 1]));
            Check(status.Transmitters.Count == 2 && status.Gain == 4,
                $"A {length}-byte legacy status supports scoped setting preflight and confirmation");
            Check(writeTransport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.SetGain, A1Command.GetStatus }),
                $"A {length}-byte two-TX setting uses one write and one confirming status");
        }
    }

    private static async Task VerifyStatusTopologyGrowth()
    {
        byte[] disconnectedExtras = CreateStatusTopologyPayload(23);
        disconnectedExtras[17] = disconnectedExtras[18] = 0;
        byte[] allDisconnected = CreateStatusTopologyPayload(23);
        allDisconnected[0] = allDisconnected[1] = allDisconnected[17] = allDisconnected[18] = 0;
        var transport = new StatusTopologySafetyTransport(
            CreateStatusTopologyPayload(17), CreateStatusTopologyPayload(23), disconnectedExtras, allDisconnected,
            CreateStatusTopologyPayload(15), CreateStatusTopologyPayload(16), CreateStatusTopologyPayload(17),
            CreateStatusTopologyPayload(23));
        using var controller = new A1Controller(transport);
        Check((await controller.ReadStatusAsync()).Transmitters.Count == 2, "A session can initially report two TX slots");
        Check((await controller.ReadStatusAsync()).Transmitters.Count == 4, "A complete extended status upgrades the session to four TX slots");
        A1Status disconnected = await controller.ReadStatusAsync();
        Check(disconnected.Transmitters.Count == 4 && disconnected.Transmitters.Count(transmitter => transmitter.Connected) == 2,
            "Disconnecting extra transmitters does not shrink established topology");
        A1Status offline = await controller.ReadStatusAsync();
        Check(offline.Transmitters.Count == 4 && !offline.Transmitters.Any(transmitter => transmitter.Connected),
            "A complete receiver-only status preserves all established slots");
        foreach (int length in new[] { 15, 16, 17 })
            await ThrowsAsync<InvalidDataException>(() => controller.ReadStatusAsync(),
                $"A four-TX session rejects a later {length}-byte status rather than losing omitted slots");
        Check((await controller.ReadStatusAsync()).Transmitters.Count == 4,
            "Rejected short statuses do not erase the controller's established topology");
        Check(transport.Commands.SequenceEqual(Enumerable.Repeat(A1Command.GetStatus, 8)),
            "Topology growth and invalid reads require no setting or firmware commands");
    }

    private static async Task VerifyPartialStatusTopology()
    {
        foreach (int length in Enumerable.Range(18, 5))
        {
            var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(length), CreateStatusTopologyPayload(17));
            using var controller = new A1Controller(transport);
            await ThrowsAsync<InvalidDataException>(() => controller.ReadStatusAsync(),
                $"A {length}-byte partial transmitter extension is rejected even without prior four-TX status");
            Check((await controller.ReadStatusAsync()).Transmitters.Count == 2,
                $"A rejected {length}-byte fragment does not establish an invented four-TX mode");
        }

        byte[] invalidExtended = CreateStatusTopologyPayload(23);
        invalidExtended[21] = 2;
        var invalidTransport = new StatusTopologySafetyTransport(invalidExtended, CreateStatusTopologyPayload(17));
        using var invalidController = new A1Controller(invalidTransport);
        await ThrowsAsync<InvalidDataException>(() => invalidController.ReadStatusAsync(),
            "A full-length extended status must parse successfully before establishing topology");
        Check((await invalidController.ReadStatusAsync()).Transmitters.Count == 2,
            "An invalid full-length response cannot raise the session topology high-water mark");
    }

    private static async Task VerifyStatusTopologyPreflight()
    {
        MicrophoneControlScope?[] scopes = [null, MicrophoneControlScope.All([0, 1])];
        foreach (int length in Enumerable.Range(15, 8))
        foreach (MicrophoneControlScope? scope in scopes)
        {
            var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(23),
                CreateStatusTopologyPayload(length), CreateStatusTopologyPayload(length, gain: 4));
            using var controller = new A1Controller(transport);
            Check((await controller.ReadStatusAsync()).Transmitters.Count == 4,
                "Preflight regression establishes four slots before a shortened response");
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(SettingChange.Gain(4), scope: scope),
                $"A {length}-byte preflight is rejected before writing with {(scope is null ? "legacy" : "captured two-TX group")} scope");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.GetStatus }),
                $"A {length}-byte short preflight sends zero settings writes and no confirmation retry");
        }
    }

    private static async Task VerifyStatusTopologyReadback()
    {
        foreach (int length in Enumerable.Range(15, 8))
        {
            var transport = new StatusTopologySafetyTransport(
                CreateStatusTopologyPayload(23), CreateStatusTopologyPayload(length, gain: 4));
            using var controller = new A1Controller(transport);
            bool confirmationChecked = false;
            SettingChange requested = SettingChange.Gain(4);
            SettingChange change = requested with
            {
                IsConfirmed = status =>
                {
                    confirmationChecked = true;
                    return requested.IsConfirmed(status);
                }
            };
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(change),
                $"A {length}-byte readback cannot confirm a write even when its gain matches the request");
            Check(!confirmationChecked, "Incomplete topology is rejected before the setting confirmation predicate");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.SetGain, A1Command.GetStatus }),
                $"A {length}-byte invalid readback stops after the attempted write without replay or rollback");
        }
    }

    private static async Task VerifyStatusTopologyEstablishedByReadback()
    {
        var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(17),
            CreateStatusTopologyPayload(23, gain: 4), CreateStatusTopologyPayload(17, gain: 4));
        using var controller = new A1Controller(transport);
        A1Status confirmed = await controller.ApplyAsync(SettingChange.Gain(4));
        Check(confirmed.Transmitters.Count == 4 && confirmed.Gain == 4,
            "A full four-TX write confirmation can establish increased topology after two-TX preflight");
        await ThrowsAsync<InvalidDataException>(() => controller.ReadStatusAsync(),
            "Topology first established by write readback also protects subsequent status reads");
    }

    private static async Task VerifyQueuedStatusTopology()
    {
        var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(23), CreateStatusTopologyPayload(17));
        using var controller = new A1Controller(transport);
        Task<A1Status> establishingRead = controller.ReadStatusAsync();
        Task<A1Status> queuedRead = controller.ReadStatusAsync();
        Check((await establishingRead).Transmitters.Count == 4, "The first serialized status read establishes four TX slots");
        await ThrowsAsync<InvalidDataException>(() => queuedRead,
            "A queued read observes topology established while holding the transaction lock");
        Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus, A1Command.GetStatus }),
            "Queued topology validation does not issue extra commands");
    }

    private static async Task VerifyStatusTopologyReceiverVersion()
    {
        byte[][] firmwareVersions = [[1, 1, 0, 0], [2, 1, 7, 9]];
        foreach (byte[] firmware in firmwareVersions)
        foreach (bool readInitialShortStatus in new[] { false, true })
        foreach (int length in new[] { 15, 16, 17 })
        {
            byte[] complete = CreateStatusTopologyPayload(23);
            complete[17] = complete[18] = 0;
            byte[] confirmedPayload = (byte[])complete.Clone();
            confirmedPayload[8] = 4;
            var replies = new List<byte[]>();
            if (readInitialShortStatus) replies.Add(CreateStatusTopologyPayload(length));
            replies.Add(CreateStatusTopologyPayload(length));
            replies.Add(CreateStatusTopologyPayload(length));
            replies.Add(complete);
            replies.Add(confirmedPayload);
            var transport = new StatusTopologySafetyTransport(replies.ToArray()) { VersionComponents = firmware };
            using var controller = new A1Controller(transport);
            if (readInitialShortStatus)
                Check((await controller.ReadStatusAsync()).Transmitters.Count == 2,
                    "Initial legacy status is accepted before four-TX receiver evidence is known");
            Version expectedVersion = Version.Parse(string.Join('.', firmware));
            Check(await controller.ReadVersionAsync(0) == expectedVersion,
                $"Receiver version {expectedVersion} establishes the documented four-slot expectation");
            await ThrowsAsync<InvalidDataException>(() => controller.ReadStatusAsync(),
                $"Known four-TX receiver {expectedVersion} rejects a {length}-byte snapshot");
            await ThrowsAsync<InvalidDataException>(
                () => controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.All([0, 1])),
                $"Known four-TX receiver {expectedVersion} rejects {length}-byte preflight before its first complete snapshot");
            Check(!transport.Commands.Contains(A1Command.SetGain),
                "Documented RX capability blocks all setting writes while status remains incomplete");
            A1Status confirmed = await controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.All([0, 1]));
            Check(confirmed.Transmitters.Count == 4 && confirmed.Gain == 4 &&
                confirmed.Transmitters.Count(transmitter => transmitter.Connected) == 2,
                "A complete four-slot snapshot permits control of all two active microphones without requiring four active TXs");
            Check(transport.Commands.Count(command => command == A1Command.GetVersion) == 1 &&
                transport.Commands.Count(command => command == A1Command.SetGain) == 1,
                "Firmware evidence adds no mode-switch command or automatic setting replay");
        }
    }

    private static async Task VerifyStatusTopologyVersionBoundaries()
    {
        (int Device, byte[] Components)[] otherVersions =
        [
            (0, [1, 1]), (0, [1, 1, 0]), (0, [1, 0, 2, 8]), (0, [1, 2, 0, 0]),
            (1, [1, 1, 0, 0]), (2, [1, 1, 0, 0]), (3, [1, 1, 0, 0]), (4, [1, 1, 0, 0])
        ];
        foreach ((int device, byte[] components) in otherVersions)
        {
            var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(17), CreateStatusTopologyPayload(17, gain: 4))
            {
                VersionComponents = components
            };
            using var controller = new A1Controller(transport);
            Version version = await controller.ReadVersionAsync(device);
            A1Status confirmed = await controller.ApplyAsync(SettingChange.Gain(4), scope: MicrophoneControlScope.All([0, 1]));
            Check(confirmed.Transmitters.Count == 2 && confirmed.Gain == 4,
                $"Device {device} version {version} is not invented four-TX receiver evidence");
        }

        var establishedTransport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(23), CreateStatusTopologyPayload(17));
        using var establishedController = new A1Controller(establishedTransport);
        Check((await establishedController.ReadStatusAsync()).Transmitters.Count == 4,
            "A complete status establishes topology independently of the reported firmware family");
        Check(await establishedController.ReadVersionAsync(0) == new Version(1, 0, 2, 8),
            "A later ordinary firmware version remains readable");
        await ThrowsAsync<InvalidDataException>(() => establishedController.ReadStatusAsync(),
            "An ordinary firmware version cannot lower topology already established by complete status");
    }

    private static async Task VerifyStatusTopologySessionReset()
    {
        var previousTransport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(23), CreateStatusTopologyPayload(17));
        using (var previousController = new A1Controller(previousTransport))
        {
            Check((await previousController.ReadStatusAsync()).Transmitters.Count == 4,
                "The previous receiver session establishes four TX slots");
            await ThrowsAsync<InvalidDataException>(() => previousController.ReadStatusAsync(),
                "A shorter status is invalid until a new session establishes its own topology");
        }
        Check(previousTransport.Disposed, "The old transport is disposed before simulating reconnect");
        foreach (int length in new[] { 15, 16, 17 })
        {
            var transport = new StatusTopologySafetyTransport(CreateStatusTopologyPayload(length));
            using var controller = new A1Controller(transport);
            Check((await controller.ReadStatusAsync()).Transmitters.Count == 2,
                $"A fresh session accepts {length}-byte two-TX status without inheriting the old maximum");
            Check(transport.Commands.SequenceEqual(new[] { A1Command.GetStatus }),
                "Reconnection establishes topology from status without inferring a firmware mode");
        }
    }

    private static byte[] CreateStatusTopologyPayload(int length, int gain = 3)
    {
        byte[] status = [1, 1, 90, 50, 0, 0, 1, 2, 3, 0, 0, 0, 3, 1, 2, 0, 0, 1, 1, 80, 75, 0, 0];
        status[8] = (byte)gain;
        return status.AsSpan(0, length).ToArray();
    }

    private sealed class StatusTopologySafetyTransport(params byte[][] statuses) : IA1Transport
    {
        private readonly Queue<byte[]> replies = new(statuses);
        private readonly List<A1Command> commands = [];
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;
        public byte[] VersionComponents { get; init; } = [1, 0, 2, 8];
        public bool Disposed { get; private set; }
        public A1Command[] Commands { get { lock (commands) return commands.ToArray(); } }

        public void SendFeature(byte[] report)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (!A1Protocol.TryDecodeRequest(report, out A1Frame? request) || request is null)
                throw new InvalidOperationException("Invalid status-topology test request.");
            try
            {
                byte[] payload;
                lock (commands)
                {
                    commands.Add(request.Command);
                    if (request.Command == A1Command.GetStatus)
                    {
                        if (request.Payload.Length != 0 || replies.Count == 0)
                            throw new InvalidOperationException("Unexpected status-topology read request.");
                        payload = replies.Dequeue();
                    }
                    else if (request.Command == A1Command.GetVersion && request.Payload.Length == 1 && request.Payload[0] <= 4)
                        payload = [request.Payload[0], .. VersionComponents];
                    else if (request.Command == A1Command.SetGain && request.Payload.SequenceEqual(new byte[] { 4 }))
                        payload = [0];
                    else
                        throw new InvalidOperationException("Unexpected status-topology test command.");
                }
                ReportReceived?.Invoke(Reply(request.Command, payload));
            }
            catch (Exception exception)
            {
                Failed?.Invoke(exception);
                throw;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
