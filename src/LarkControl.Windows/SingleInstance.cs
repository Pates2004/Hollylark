using System.Security.Principal;

namespace LarkControl.Windows;

internal sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle activation;
    private readonly Mutex mutex;
    private RegisteredWaitHandle? listener;
    private int disposed;

    internal SingleInstance(string? name = null)
    {
        name ??= GetDefaultName();
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activation");
        try
        {
            mutex = new Mutex(false, name + ".Mutex");
            try { IsOwner = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsOwner = true; }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        catch
        {
            activation.Dispose();
            throw;
        }
    }

    internal bool IsOwner { get; }

    private static string GetDefaultName()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return @"Local\Hollylark." + (identity.User?.Value
            ?? throw new InvalidOperationException("Cannot identify the current Windows user."));
    }

    internal void RequestActivation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        activation.Set();
    }

    internal void Listen(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!IsOwner || listener is not null) throw new InvalidOperationException("Only the owner can register one activation listener.");
        listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
        {
            if (Volatile.Read(ref disposed) == 0) activate();
        }, null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            listener?.Unregister(null);
            activation.Dispose();
        }
        finally
        {
            try
            {
                if (IsOwner) mutex.ReleaseMutex();
            }
            finally { mutex.Dispose(); }
        }
    }
}
