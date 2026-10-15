namespace LarkControl.Core;

public interface IA1Transport : IDisposable
{
    event Action<byte[]>? ReportReceived;
    event Action<Exception>? Failed;
    void SendFeature(byte[] report);
}
