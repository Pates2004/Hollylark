using System.Text;
using Prismatoid;
using Prismatoid.NativeInterop;
using NativeBackend = Prismatoid.NativeInterop.PrismBackend;
using NativeContext = Prismatoid.NativeInterop.PrismContext;

namespace LarkControl.Windows;

public interface IScreenReaderSpeech : IDisposable
{
    bool TrySpeak(string message);
}

internal sealed class PrismScreenReaderSpeech : IScreenReaderSpeech
{
    internal static IReadOnlyList<PrismBackendId> ReaderIds { get; } = Array.AsReadOnly<PrismBackendId>(
        [PrismBackendId.Nvda, PrismBackendId.Jaws, PrismBackendId.ZoomText, PrismBackendId.Zdsr,
         PrismBackendId.BoyPcReader, PrismBackendId.PcTalker, PrismBackendId.SenseReader,
         PrismBackendId.SystemAccess, PrismBackendId.WindowEyes]);

    private readonly IPrismSpeechApi native;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private nint context;
    private bool disposed;
    private bool callActive;

    internal PrismScreenReaderSpeech(IPrismSpeechApi? native = null) => this.native = native ?? new PrismSpeechApi();

    public bool TrySpeak(string message)
    {
        if (disposed || string.IsNullOrWhiteSpace(message)) return false;
        if (message.Contains('\0')) throw new ArgumentException("Speech cannot contain a null character.", nameof(message));
        VerifyThread();
        if (callActive) return false;
        // Native calls can pump STA callbacks. Keep the context alive until their finally blocks finish.
        callActive = true;
        try
        {
            if (context == 0)
            {
                context = native.Initialize();
                if (disposed) return false;
                if (context == 0) throw new InvalidOperationException("Prism initialization failed.");
            }
            const PrismBackendFeature required = PrismBackendFeature.IsSupportedAtRuntime | PrismBackendFeature.SupportsSpeak;
            foreach (PrismBackendId identifier in ReaderIds)
            {
                if (disposed) return false;
                nint backend = native.CreateBackend(context, identifier);
                if (backend == 0) continue;
                try
                {
                    if (disposed) return false;
                    PrismBackendFeature features = native.GetFeatures(backend);
                    if (disposed) return false;
                    if ((features & required) != required) continue;
                    PrismError initialized = native.InitializeBackend(backend);
                    if (disposed) return false;
                    if (initialized == PrismError.PRISM_ERROR_BACKEND_NOT_AVAILABLE) continue;
                    RequireSuccess(initialized, "initialize");
                    RequireSuccess(native.Speak(backend, message, interrupt: false), "speak");
                }
                finally { native.FreeBackend(backend); }
                return !disposed;
            }
            return false;
        }
        finally
        {
            callActive = false;
            if (disposed) ShutdownContext();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyThread();
        disposed = true;
        if (!callActive) ShutdownContext();
    }

    private void ShutdownContext()
    {
        nint previous = context;
        context = 0;
        if (previous != 0) native.Shutdown(previous);
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Prism must be used and disposed on its owning thread.");
    }

    private static void RequireSuccess(PrismError result, string operation)
    {
        if (result != PrismError.PRISM_OK)
            throw new InvalidOperationException($"Prism {operation}: {result}.");
    }
}

internal interface IPrismSpeechApi
{
    nint Initialize();
    nint CreateBackend(nint context, PrismBackendId identifier);
    PrismBackendFeature GetFeatures(nint backend);
    PrismError InitializeBackend(nint backend);
    PrismError Speak(nint backend, string message, bool interrupt);
    void FreeBackend(nint backend);
    void Shutdown(nint context);
}

internal sealed unsafe class PrismSpeechApi : IPrismSpeechApi
{
    public nint Initialize()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Prism requires an STA thread.");
        PrismConfig config = Methods.prism_config_init();
        return (nint)Methods.prism_init(&config);
    }

    public nint CreateBackend(nint context, PrismBackendId identifier) =>
        (nint)Methods.prism_registry_create((NativeContext*)context, identifier.Value);

    public PrismBackendFeature GetFeatures(nint backend) =>
        (PrismBackendFeature)Methods.prism_backend_get_features((NativeBackend*)backend);

    public PrismError InitializeBackend(nint backend) => Methods.prism_backend_initialize((NativeBackend*)backend);

    public PrismError Speak(nint backend, string message, bool interrupt)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(message + '\0');
        fixed (byte* text = encoded)
            return Methods.prism_backend_speak((NativeBackend*)backend, (sbyte*)text, interrupt);
    }

    public void FreeBackend(nint backend) => Methods.prism_backend_free((NativeBackend*)backend);
    public void Shutdown(nint context) => Methods.prism_shutdown((NativeContext*)context);
}
