using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LarkControl.Windows;
using Prismatoid;
using Prismatoid.NativeInterop;

internal static partial class Program
{
    private const string PrismNativeHash = "99A73CD24AC777EDE4A5742E62CC3615EC7F5722862C0D9B5E4B3672DCF46CC4";

    private static void VerifyNativePrismChecks(string? libraryPath)
    {
        string path = Path.GetFullPath(libraryPath ?? Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "prism.dll"));
        byte[] contents = File.ReadAllBytes(path);
        Check(Convert.ToHexString(SHA256.HashData(contents)) == PrismNativeHash, "Native Prism matches the pinned Prismatoid 0.4.0 x64 asset");
        int header = BitConverter.ToInt32(contents, 0x3c);
        Check(BitConverter.ToUInt32(contents, header) == 0x00004550 && BitConverter.ToUInt16(contents, header + 4) == 0x8664,
            "Pinned Prism has a native AMD64 PE header");
        nint module = 0;
        NativeLibrary.SetDllImportResolver(typeof(PrismBackendId).Assembly, (name, _, _) =>
            name == "prism" ? module == 0 ? module = NativeLibrary.Load(path) : module : 0);
        var native = new PrismSpeechApi();
        nint context = 0;
        nint backend = 0;
        try
        {
            context = native.Initialize();
            Check(context != 0, "Actual Prism context initializes on the owning STA thread");
            using var process = Process.GetCurrentProcess();
            Check(process.Modules.Cast<ProcessModule>().Any(item =>
                string.Equals(item.FileName, path, StringComparison.OrdinalIgnoreCase)),
                "The native probe loads the exact requested library, not another search-path copy");
            backend = native.CreateBackend(context, PrismBackendId.Nvda);
            Check(backend != 0, "Pinned Prism registers its direct NVDA backend");
            PrismBackendFeature features = native.GetFeatures(backend);
            Check((features & PrismBackendFeature.SupportsSpeak) != 0, "The actual NVDA backend exposes direct speech");
            bool available = (features & PrismBackendFeature.IsSupportedAtRuntime) != 0;
            Console.WriteLine("Native NVDA availability on this desktop: " + available);
            if (available)
            {
                Check(native.InitializeBackend(backend) == PrismError.PRISM_OK,
                    "The available NVDA backend initializes without sending speech, cancellation or input");
            }
        }
        finally
        {
            if (backend != 0) native.FreeBackend(backend);
            if (context != 0) native.Shutdown(context);
            if (module != 0) NativeLibrary.Free(module);
        }
        Check(true, "Native backend and context close without a speech or stop request");
    }
}
