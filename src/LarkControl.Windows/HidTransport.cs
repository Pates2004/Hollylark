using System.IO;
using HidSharp;
using LarkControl.Core;

namespace LarkControl.Windows;

public sealed record ReceiverInfo(string Name, string Path, int InputLength, int FeatureLength)
{
    public override string ToString() => Name;
}

internal sealed class HidTransport : IA1Transport
{
    private readonly HidStream stream;
    private int disposed;

    public event Action<byte[]>? ReportReceived;
    public event Action<Exception>? Failed;

    public static IReadOnlyList<ReceiverInfo> FindReceivers()
    {
        var receivers = new List<ReceiverInfo>();
        foreach (HidDevice device in DeviceList.Local.GetHidDevices(A1Protocol.VendorId, A1Protocol.ProductId))
        {
            try
            {
                if (!SupportsNormalCommands(device)) continue;
                receivers.Add(new ReceiverInfo(Localization.Text("Hid.ReceiverName", receivers.Count + 1),
                    device.DevicePath, device.GetMaxInputReportLength(), device.GetMaxFeatureReportLength()));
            }
            catch (IOException) { }
        }
        return receivers;
    }

    private static bool SupportsNormalCommands(HidDevice device) =>
        device.GetReportDescriptor().FeatureReports.Any(report =>
            report.ReportID == A1Protocol.ReportId && report.Length == A1Protocol.ReportLength);

    public HidTransport(ReceiverInfo receiver)
    {
        HidDevice device = DeviceList.Local.GetHidDevices(A1Protocol.VendorId, A1Protocol.ProductId)
            .FirstOrDefault(candidate => candidate.DevicePath == receiver.Path)
            ?? throw new IOException(Localization.Text("Hid.ReceiverDisconnected"));
        if (!SupportsNormalCommands(device) || device.GetMaxFeatureReportLength() != A1Protocol.ReportLength)
            throw new InvalidDataException(Localization.Text("Hid.UnsupportedCollection"));
        stream = device.Open();
        stream.ReadTimeout = 3000;
        stream.WriteTimeout = 3000;
    }

    public void SendFeature(byte[] report)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!A1Protocol.TryDecodeRequest(report, out _))
            throw new ArgumentException(Localization.Text("Hid.InvalidRequest"), nameof(report));
        try
        {
            stream.SetFeature(report);
            byte[] response = new byte[A1Protocol.ReportLength];
            response[0] = A1Protocol.ReportId;
            stream.GetFeature(response);
            if (Volatile.Read(ref disposed) == 0) ReportReceived?.Invoke(response);
        }
        catch (IOException exception)
        {
            Failed?.Invoke(exception);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) stream.Dispose();
    }
}
