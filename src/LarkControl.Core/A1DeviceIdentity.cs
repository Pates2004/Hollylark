using System.Text;

namespace LarkControl.Core;

public static class A1DeviceIdentity
{
    private static readonly Encoding SerialEncoding = new UTF8Encoding(false, true);

    public static bool IsFourTransmitterFirmware(Version? receiverVersion) =>
        receiverVersion is { Minor: 1, Revision: >= 0 };

    public static string? ParseSerialNumber(int device, ReadOnlySpan<byte> payload)
    {
        ValidateDevice(device);
        ValidatePayload(payload, 3);
        if (payload[0] != device)
            throw new InvalidDataException("Serial number response does not match the requested device.");

        Span<byte> content = stackalloc byte[payload.Length - 1];
        int length = 0;
        foreach (byte value in payload[1..])
            if (value is not 0 and not 0xFF)
                content[length++] = value;
        if (length == 0) return null;
        try
        {
            string serialNumber = SerialEncoding.GetString(content[..length]);
            if (serialNumber.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
                throw new InvalidDataException("Serial number contains control characters or line separators.");
            return serialNumber;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Invalid serial number encoding.", exception);
        }
    }

    public static string? ParseMacAddress(int device, ReadOnlySpan<byte> payload)
    {
        ValidateDevice(device);
        ValidatePayload(payload, device == 0 ? 3 : 4);
        if (device != 0 && payload[0] != device - 1)
            throw new InvalidDataException("MAC address response does not match the requested transmitter.");

        ReadOnlySpan<byte> address = device == 0 ? payload : payload[1..];
        bool available = false;
        foreach (byte value in address)
            available |= value != 0;
        if (!available) return null;

        string hexadecimal = Convert.ToHexString(address);
        var formatted = new StringBuilder(address.Length * 3 - 1);
        for (int index = 0; index < hexadecimal.Length; index += 2)
        {
            if (index != 0) formatted.Append(':');
            formatted.Append(hexadecimal, index, 2);
        }
        return formatted.ToString();
    }

    internal static void ValidateDevice(int device)
    {
        if (device is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(device));
    }

    private static void ValidatePayload(ReadOnlySpan<byte> payload, int minimumLength)
    {
        if (payload.Length < minimumLength || payload.Length > A1Protocol.ReportLength - 8)
            throw new InvalidDataException("Invalid identity response payload length.");
    }
}
