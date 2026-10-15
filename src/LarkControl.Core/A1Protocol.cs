using System.Buffers.Binary;

namespace LarkControl.Core;

public enum A1Command : byte
{
    SetChannelMode = 0x01,
    SetGain = 0x03,
    SetReverb = 0x05,
    SetEqualizer = 0x07,
    GetVersion = 0x0D,
    GetSerialNumber = 0x12,
    SetNoise = 0x13,
    SetMute = 0x16,
    GetStatus = 0x1F,
    SetLight = 0x21,
    SetIdentification = 0x23,
    SetAutoOff = 0x27,
    GetReceiverMacAddress = 0x30,
    GetTransmitterMacAddress = 0x31
}

public sealed record A1Frame(A1Command Command, byte[] Payload);

public static class A1Protocol
{
    public const int VendorId = 0x3547;
    public const int ProductId = 0x0407;
    public const int ReportLength = 64;
    public const byte ReportId = 5;

    public static byte[] Encode(A1Command command, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(command) || payload.Length > ReportLength - 8)
            throw new ArgumentOutOfRangeException(nameof(command));
        if (!IsValidIdentityRequest(command, payload))
            throw new ArgumentException("Invalid identity request payload.", nameof(payload));

        byte[] report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = 3;
        report[2] = 0xAA;
        report[3] = 0xDD;
        report[4] = (byte)command;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(5), (ushort)payload.Length);
        payload.CopyTo(report.AsSpan(7));
        report[7 + payload.Length] = 0xEF;
        return report;
    }

    public static bool TryDecode(ReadOnlySpan<byte> report, out A1Frame? frame)
        => TryDecodeFrame(report, 0xBB, false, out frame);

    public static bool TryDecodeRequest(ReadOnlySpan<byte> report, out A1Frame? frame)
    {
        frame = null;
        if (report.Length != ReportLength || !TryDecodeFrame(report, 0xAA, true, out frame))
            return false;
        if (Enum.IsDefined(frame!.Command) && IsValidIdentityRequest(frame.Command, frame.Payload)) return true;
        frame = null;
        return false;
    }

    private static bool IsValidIdentityRequest(A1Command command, ReadOnlySpan<byte> payload) => command switch
    {
        A1Command.GetSerialNumber => payload.Length == 1 && payload[0] <= 4,
        A1Command.GetReceiverMacAddress => payload.IsEmpty,
        A1Command.GetTransmitterMacAddress => payload.Length == 1 && payload[0] <= 3,
        _ => true
    };

    private static bool TryDecodeFrame(ReadOnlySpan<byte> report, byte marker, bool request, out A1Frame? frame)
    {
        frame = null;
        if (report.Length < 8 || report[0] != ReportId || report[1] != 3 ||
            report[2] != marker || report[3] != 0xDD)
            return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(report.Slice(5, 2));
        if (length > ReportLength - 8 || length + 8 > report.Length || (request && report[7 + length] != 0xEF))
            return false;
        frame = new A1Frame((A1Command)report[4], report.Slice(7, length).ToArray());
        return true;
    }
}
