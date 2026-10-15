using System.IO;
using System.Text;
using LarkControl.Core;

internal static partial class Program
{
    private static async Task VerifyIdentityAsync()
    {
        VerifyIdentityRequests();
        VerifyIdentityPayloads();
        VerifyIdentityFirmwareMode();
        await VerifyIdentityControllerAsync();
        await VerifyIdentityFailuresAsync();
    }

    private static void VerifyIdentityRequests()
    {
        Check((byte)A1Command.GetSerialNumber == 0x12 &&
            (byte)A1Command.GetReceiverMacAddress == 0x30 &&
            (byte)A1Command.GetTransmitterMacAddress == 0x31, "Identity command values match A1 handlers");
        for (int device = 0; device <= 4; device++)
        {
            byte[] request = A1Protocol.Encode(A1Command.GetSerialNumber, [(byte)device]);
            Check(A1Protocol.TryDecodeRequest(request, out A1Frame? decoded) && decoded is not null &&
                decoded.Command == A1Command.GetSerialNumber && decoded.Payload.SequenceEqual(new[] { (byte)device }),
                $"Serial number request targets device {device}");
            if (device == 0) continue;
            request = A1Protocol.Encode(A1Command.GetTransmitterMacAddress, [(byte)(device - 1)]);
            Check(A1Protocol.TryDecodeRequest(request, out decoded) && decoded is not null &&
                decoded.Payload.SequenceEqual(new[] { (byte)(device - 1) }), $"MAC request targets TX index {device - 1}");
        }
        byte[] receiverRequest = A1Protocol.Encode(A1Command.GetReceiverMacAddress, []);
        Check(A1Protocol.TryDecodeRequest(receiverRequest, out A1Frame? receiver) && receiver is not null &&
            receiver.Payload.Length == 0, "Receiver MAC request has no selector");

        (A1Command Command, byte[] Payload)[] invalidRequests =
        [
            (A1Command.GetSerialNumber, []), (A1Command.GetSerialNumber, [0, 0]),
            (A1Command.GetSerialNumber, [5]), (A1Command.GetSerialNumber, [255]),
            (A1Command.GetReceiverMacAddress, [0]),
            (A1Command.GetTransmitterMacAddress, []), (A1Command.GetTransmitterMacAddress, [0, 0]),
            (A1Command.GetTransmitterMacAddress, [4]), (A1Command.GetTransmitterMacAddress, [255])
        ];
        foreach (var invalid in invalidRequests)
        {
            Throws<ArgumentException>(() => A1Protocol.Encode(invalid.Command, invalid.Payload),
                $"Invalid identity request rejected: {invalid.Command} {Convert.ToHexString(invalid.Payload)}");
            byte[] forged = Reply(invalid.Command, invalid.Payload);
            forged[2] = 0xAA;
            forged[7 + invalid.Payload.Length] = 0xEF;
            Check(!A1Protocol.TryDecodeRequest(forged, out A1Frame? decoded) && decoded is null,
                "Transport request decoder rejects malformed identity requests");
        }
    }

    private static void VerifyIdentityPayloads()
    {
        for (int device = 0; device <= 4; device++)
        {
            Check(A1DeviceIdentity.ParseSerialNumber(device, [(byte)device, 65, 49]) == "A1",
                $"Serial number response routes device {device}");
            Check(A1DeviceIdentity.ParseSerialNumber(device, [(byte)device, 0, 0xFF]) is null,
                $"Empty padded serial number is unavailable for device {device}");
            byte[] address = [0, 0x11, 0x22, 0xAB, 0xCD, 0];
            byte[] payload = device == 0 ? address : [(byte)(device - 1), .. address];
            Check(A1DeviceIdentity.ParseMacAddress(device, payload) == "00:11:22:AB:CD:00",
                $"MAC response preserves zero octets for device {device}");
            byte[] unavailable = device == 0 ? new byte[6] : [(byte)(device - 1), 0, 0, 0, 0, 0, 0];
            Check(A1DeviceIdentity.ParseMacAddress(device, unavailable) is null,
                $"All-zero MAC is unavailable for device {device}");
        }
        Check(A1DeviceIdentity.ParseSerialNumber(0, [0, 0, 65, 0xFF, 49, 0, 66, 0xFF]) == "A1B",
            "Serial padding is removed throughout the field, not treated as a terminator");
        Check(A1DeviceIdentity.ParseSerialNumber(0, [0, 0xC3, 0xA9]) == "\u00E9",
            "Serial number follows the handler's UTF-8 conversion, not an invented ASCII-only format");
        Check(A1DeviceIdentity.ParseSerialNumber(0, [0, 32, 65, 32]) == " A ",
            "Serial number is not silently trimmed");
        Throws<InvalidDataException>(() => A1DeviceIdentity.ParseSerialNumber(0, [0, 0xC3, 40]),
            "Malformed UTF-8 cannot become a replacement-character serial number");
        IEnumerable<char> prohibitedCharacters = Enumerable.Range(1, 31).Concat(Enumerable.Range(127, 33))
            .Select(value => (char)value).Concat(new[] { '\u2028', '\u2029' });
        foreach (char character in prohibitedCharacters)
        {
            byte[] payload = [0, .. Encoding.UTF8.GetBytes($"A{character}B")];
            Throws<InvalidDataException>(() => A1DeviceIdentity.ParseSerialNumber(0, payload),
                $"SN rejects control or line separator U+{(int)character:X4}");
        }
        Check(A1DeviceIdentity.ParseSerialNumber(4, [4, .. Enumerable.Repeat((byte)65, 55)]) == new string('A', 55),
            "Largest normal serial number payload is accepted");

        for (int length = 3; length <= 56; length++)
        {
            byte[] address = Enumerable.Repeat((byte)0xA5, length).ToArray();
            string expected = string.Join(':', Enumerable.Repeat("A5", length));
            Check(A1DeviceIdentity.ParseMacAddress(0, address) == expected,
                $"Receiver MAC preserves handler-supported length {length}");
            if (length < 56)
                Check(A1DeviceIdentity.ParseMacAddress(4, [3, .. address]) == expected,
                    $"Transmitter MAC preserves handler-supported length {length}");
        }
        Check(A1DeviceIdentity.ParseMacAddress(0, [255, 255, 255]) == "FF:FF:FF",
            "MAC FF octets are not guessed to be unavailable serial-number padding");
        foreach (int length in new[] { 0, 1, 2, 57 })
        {
            byte[] payload = new byte[length];
            Throws<InvalidDataException>(() => A1DeviceIdentity.ParseSerialNumber(0, payload),
                $"Invalid serial number payload length {length}");
            Throws<InvalidDataException>(() => A1DeviceIdentity.ParseMacAddress(0, payload),
                $"Invalid receiver MAC payload length {length}");
        }
        foreach (int length in new[] { 0, 1, 2, 3, 57 })
            Throws<InvalidDataException>(() => A1DeviceIdentity.ParseMacAddress(1, new byte[length]),
                $"Invalid transmitter MAC payload length {length}");
        for (int selector = 0; selector <= byte.MaxValue; selector++)
        {
            byte actual = (byte)selector;
            if (selector != 4)
                Throws<InvalidDataException>(() => A1DeviceIdentity.ParseSerialNumber(4, [actual, 65, 49]),
                    $"SN selector {selector} is not clamped to TX4");
            if (selector != 3)
                Throws<InvalidDataException>(() => A1DeviceIdentity.ParseMacAddress(4, [actual, 1, 2, 3]),
                    $"MAC selector {selector} is not clamped to TX4");
        }
        foreach (int device in new[] { int.MinValue, -1, 5, 255, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => A1DeviceIdentity.ParseSerialNumber(device, [0, 65, 49]),
                $"Invalid serial number device {device}");
            Throws<ArgumentOutOfRangeException>(() => A1DeviceIdentity.ParseMacAddress(device, [1, 2, 3]),
                $"Invalid MAC device {device}");
        }
    }

    private static void VerifyIdentityFirmwareMode()
    {
        Check(!A1DeviceIdentity.IsFourTransmitterFirmware(null), "Unknown receiver firmware is not positive four-TX evidence");
        Check(!A1DeviceIdentity.IsFourTransmitterFirmware(new Version(1, 1)), "Two-component firmware is not four-TX evidence");
        Check(!A1DeviceIdentity.IsFourTransmitterFirmware(new Version(1, 1, 0)), "Three-component firmware is not four-TX evidence");
        Check(A1DeviceIdentity.IsFourTransmitterFirmware(new Version(1, 1, 0, 0)), "Four-component minor-one firmware identifies four-TX mode");
        Check(A1DeviceIdentity.IsFourTransmitterFirmware(new Version(2, 1, 7, 9)), "Mode identity does not invent a major-version restriction");
        Check(!A1DeviceIdentity.IsFourTransmitterFirmware(new Version(1, 0, 2, 8)), "Standard receiver firmware is not four-TX mode");
        Check(!A1DeviceIdentity.IsFourTransmitterFirmware(new Version(1, 2, 0, 0)), "Other firmware families are not guessed to be four-TX mode");
    }

    private static async Task VerifyIdentityControllerAsync()
    {
        using var transport = new IdentityTransport { Respond = RespondWithIdentity };
        using var controller = new A1Controller(transport);
        for (int device = 0; device <= 4; device++)
        {
            Check(await controller.ReadSerialNumberAsync(device) == $"SN{device}", $"Controller reads SN for device {device}");
            A1Frame request = transport.Requests[^1];
            Check(request.Command == A1Command.GetSerialNumber && request.Payload.SequenceEqual(new[] { (byte)device }),
                "Controller sends the SN device selector");
            Check(await controller.ReadMacAddressAsync(device) == "00:11:22:AB:CD:00", $"Controller reads MAC for device {device}");
            request = transport.Requests[^1];
            Check(device == 0
                ? request.Command == A1Command.GetReceiverMacAddress && request.Payload.Length == 0
                : request.Command == A1Command.GetTransmitterMacAddress && request.Payload.SequenceEqual(new[] { (byte)(device - 1) }),
                "Controller sends the correct receiver/transmitter MAC request");
        }
        int requestCount = transport.Requests.Count;
        foreach (int device in new[] { -1, 5, int.MaxValue })
        {
            await ThrowsAsync<ArgumentOutOfRangeException>(() => controller.ReadSerialNumberAsync(device), "Invalid controller SN target rejected");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => controller.ReadMacAddressAsync(device), "Invalid controller MAC target rejected");
        }
        Check(transport.Requests.Count == requestCount, "Invalid identity targets do not reach transport");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => controller.ReadSerialNumberAsync(0, cancellation.Token),
                "Pre-cancelled metadata request is rejected before transmission");
            Check(transport.Requests.Count == requestCount && !transport.Disposed, "Pre-cancelled queued read preserves the session");
        }
        Task[] concurrent = Enumerable.Range(0, 5).SelectMany(device => new Task[]
        {
            controller.ReadSerialNumberAsync(device), controller.ReadMacAddressAsync(device),
            controller.ReadVersionAsync(device), controller.ReadStatusAsync()
        }).ToArray();
        await Task.WhenAll(concurrent);
        Check(transport.MaximumConcurrentRequests == 1, "Identity, version and status reads share the existing transaction lock");
        Check(transport.Requests.All(request => request.Command is A1Command.GetSerialNumber or A1Command.GetReceiverMacAddress or
            A1Command.GetTransmitterMacAddress or A1Command.GetVersion or A1Command.GetStatus), "Metadata tests send read commands only");
    }

    private static void RespondWithIdentity(IdentityTransport transport, A1Frame request)
    {
        Thread.Sleep(2);
        switch (request.Command)
        {
            case A1Command.GetSerialNumber:
                transport.Deliver(A1Command.GetTransmitterMacAddress, [0, 1, 2, 3]);
                transport.Deliver(request.Command, [(byte)((request.Payload[0] + 1) % 5), 88, 88]);
                transport.Deliver(request.Command, [request.Payload[0], .. Encoding.UTF8.GetBytes($"SN{request.Payload[0]}")]);
                break;
            case A1Command.GetReceiverMacAddress:
                transport.Deliver(A1Command.GetTransmitterMacAddress, [0, 1, 2, 3]);
                transport.Deliver(request.Command, [0, 0x11, 0x22, 0xAB, 0xCD, 0]);
                break;
            case A1Command.GetTransmitterMacAddress:
                transport.Deliver(A1Command.GetReceiverMacAddress, [1, 2, 3]);
                transport.Deliver(request.Command, [(byte)((request.Payload[0] + 1) % 4), 1, 2, 3]);
                transport.Deliver(request.Command, [request.Payload[0], 0, 0x11, 0x22, 0xAB, 0xCD, 0]);
                break;
            case A1Command.GetVersion:
                transport.Deliver(request.Command, [request.Payload[0], 1, 0, 2, 8]);
                break;
            case A1Command.GetStatus:
                transport.Deliver(request.Command, ValidStatus);
                break;
            default:
                throw new InvalidOperationException("Unexpected non-read command in identity tests.");
        }
    }

    private static async Task VerifyIdentityFailuresAsync()
    {
        using (var transport = new IdentityTransport
        {
            Respond = (source, request) => source.Deliver(request.Command,
                request.Command == A1Command.GetStatus ? ValidStatus : [request.Payload[0], 65, 10, 66])
        })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<InvalidDataException>(() => controller.ReadSerialNumberAsync(0),
                "Controller rejects injected multiline serial labels");
            Check((await controller.ReadStatusAsync()).Gain == 3 && !transport.Disposed,
                "Rejected serial text does not invalidate a completed transport exchange");
        }
        using (var transport = new IdentityTransport
        {
            Respond = (source, request) => source.Deliver(request.Command, request.Command switch
            {
                A1Command.GetSerialNumber => [request.Payload[0], 0, 0xFF],
                A1Command.GetReceiverMacAddress => [0, 0, 0],
                A1Command.GetTransmitterMacAddress => [request.Payload[0], 0, 0, 0],
                _ => ValidStatus
            })
        })
        using (var controller = new A1Controller(transport))
        {
            Check(await controller.ReadSerialNumberAsync(2) is null, "Controller preserves unavailable SN as null");
            Check(await controller.ReadMacAddressAsync(0) is null && await controller.ReadMacAddressAsync(2) is null,
                "Controller preserves unavailable receiver/transmitter MAC as null");
            Check((await controller.ReadStatusAsync()).Gain == 3 && !transport.Disposed,
                "Unavailable metadata does not invalidate the working control session");
        }
        foreach (A1Command command in new[] { A1Command.GetSerialNumber, A1Command.GetReceiverMacAddress, A1Command.GetTransmitterMacAddress })
        {
            using var transport = new IdentityTransport
            {
                Respond = (source, request) => source.Deliver(request.Command,
                    request.Command == A1Command.GetStatus ? ValidStatus : request.Payload)
            };
            using var controller = new A1Controller(transport);
            await ThrowsAsync<InvalidDataException>(() => ReadIdentityForCheck(controller, command),
                $"Malformed matching {command} response is an error, not an unavailable field");
            Check((await controller.ReadStatusAsync()).Gain == 3 && !transport.Disposed,
                "Metadata parsing errors preserve existing post-exchange controller semantics");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var transport = new IdentityTransport
        {
            Respond = (source, request) =>
            {
                source.Deliver(request.Command, [255, 65, 49]);
                cancellation.Cancel();
            }
        })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<OperationCanceledException>(() => controller.ReadSerialNumberAsync(4, cancellation.Token),
                "Invalid SN response selector is ignored rather than clamped to TX4");
            Check(transport.Disposed, "Cancellation during metadata exchange closes transport to exclude late replies");
        }
        using (var transport = new IdentityTransport { Respond = (source, _) => source.Fail(new IOException("Synthetic read failure.")) })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<IOException>(() => controller.ReadMacAddressAsync(1), "Metadata transport failures propagate");
            Check(transport.Disposed, "Metadata transport failure closes the session");
        }
        using (var transport = new IdentityTransport())
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<TimeoutException>(() => controller.ReadMacAddressAsync(0), "Missing metadata response times out instead of inventing a value");
            Check(transport.Disposed, "Metadata timeout preserves existing session disposal semantics");
        }
    }

    private static Task<string?> ReadIdentityForCheck(A1Controller controller, A1Command command) => command switch
    {
        A1Command.GetSerialNumber => controller.ReadSerialNumberAsync(0),
        A1Command.GetReceiverMacAddress => controller.ReadMacAddressAsync(0),
        A1Command.GetTransmitterMacAddress => controller.ReadMacAddressAsync(1),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private sealed class IdentityTransport : IA1Transport
    {
        private int activeRequests;
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;
        public Action<IdentityTransport, A1Frame>? Respond { get; init; }
        public List<A1Frame> Requests { get; } = [];
        public bool Disposed { get; private set; }
        public int MaximumConcurrentRequests { get; private set; }

        public void SendFeature(byte[] report)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (!A1Protocol.TryDecodeRequest(report, out A1Frame? request) || request is null)
                throw new InvalidDataException("Invalid identity test request.");
            int active = Interlocked.Increment(ref activeRequests);
            try
            {
                lock (Requests)
                {
                    MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, active);
                    Requests.Add(request);
                }
                Respond?.Invoke(this, request);
            }
            finally { Interlocked.Decrement(ref activeRequests); }
        }

        public void Deliver(A1Command command, byte[] payload) => ReportReceived?.Invoke(Reply(command, payload));
        public void Fail(Exception exception) => Failed?.Invoke(exception);
        public void Dispose() => Disposed = true;
    }
}
