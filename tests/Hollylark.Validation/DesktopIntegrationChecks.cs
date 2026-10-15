using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LarkControl.Windows;
using Microsoft.Win32;

internal static partial class Program
{
    private const string DesktopExecutablePath = @"C:\Program Files\Hollylark\Hollylark.exe";

    private static void VerifyDesktopIntegration()
    {
        Check(DesktopIntegration.StartupValueName == "Hollylark"
            && DesktopIntegration.RunKeyPath == @"Software\Microsoft\Windows\CurrentVersion\Run",
            "Startup registration is restricted to Hollylark's own Run entry");
        VerifyDesktopStartupLifecycle();
        VerifyDesktopStartupSnapshots();
        VerifyDesktopStartupPaths();
        VerifyDesktopStartupFailures();
        Check(DesktopIntegration.IsRestoreNotification(0x401), "Tray keyboard activation restores the window");
        Check(DesktopIntegration.IsRestoreNotification(0x203), "Tray double-click restores the window");
        Check(DesktopIntegration.IsRestoreNotification(0x400), "Tray select notification restores the window");
        Check(!DesktopIntegration.IsRestoreNotification(0x7b) && !DesktopIntegration.IsRestoreNotification(0x200),
            "Context menu and pointer movement do not restore the window");

        Type nativeMethods = typeof(DesktopIntegration).GetNestedType("NativeMethods", BindingFlags.NonPublic)!;
        Type iconData = nativeMethods.GetNestedType("NotifyIconData", BindingFlags.NonPublic)!;
        Type identifier = nativeMethods.GetNestedType("NotifyIconIdentifier", BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(iconData) == (IntPtr.Size == 8 ? 976 : 956), "Native Unicode tray structure has the Windows ABI size");
        Check(Marshal.SizeOf(identifier) == (IntPtr.Size == 8 ? 40 : 28), "Native tray identifier has the Windows ABI size");
        Check(!typeof(DesktopIntegration).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == "System.Windows.Forms"),
            "Desktop integration does not introduce a Windows Forms dependency");
    }

    private static void VerifyDesktopStartupLifecycle()
    {
        var registry = new DesktopStartupStore();
        int attributeReads = 0;
        using var integration = new DesktopIntegration(DesktopExecutablePath, registry, path =>
        {
            attributeReads++;
            Check(path == DesktopExecutablePath, "Startup verifies the configured executable rather than a CWD-relative path");
            return FileAttributes.Normal;
        });
        Check(registry.ReadCount == 0 && registry.WriteCount == 0 && registry.DeleteCount == 0 && attributeReads == 0,
            "Desktop integration construction does not access the registry, executable or tray");
        StartupRegistrationSnapshot before = integration.CaptureStartup();
        Check(!before.Exists && before.RawValue is null && before.ValueKind == RegistryValueKind.Unknown,
            "Missing startup entry is represented independently from empty data");
        Check(registry.WriteCount == 0 && registry.DeleteCount == 0 && attributeReads == 0,
            "Startup capture is read-only and needs no executable or WPF application");
        integration.SetStartup(false);
        Check(registry.DeleteCount == 0 && attributeReads == 0, "Disabling absent startup performs no write or executable lookup");
        integration.SetStartup(true);
        StartupRegistrationSnapshot enabled = integration.CaptureStartup();
        Check(enabled.Exists && enabled.ValueKind == RegistryValueKind.String
            && Equals(enabled.RawValue, "\"" + DesktopExecutablePath + "\""),
            "Startup is the quoted Hollylark apphost only, with no minimized or other arguments");
        integration.SetStartup(true);
        Check(registry.WriteCount == 1, "Enabling the same startup command is idempotent");
        integration.HideTray();
        integration.HideTray();
        Check(integration.CaptureStartup() == enabled, "Hiding an uncreated tray does not change startup");
        integration.RestoreStartup(before);
        Check(integration.CaptureStartup() == before && registry.DeleteCount == 1,
            "Rolling back a newly created startup value restores its absence");
        integration.SetStartup(true);
        integration.SetStartup(false);
        Check(!integration.CaptureStartup().Exists, "Disabling startup removes Hollylark's value");
        integration.RestoreStartup(enabled);
        Check(integration.CaptureStartup() == enabled, "Rolling back disabled startup restores its value and kind");
        registry.CheckUnrelatedEntries();

        Throws<ArgumentNullException>(() => integration.RestoreStartup(null!), "Null startup snapshots are rejected");
        Throws<ArgumentNullException>(() => integration.ShowTray(null!, () => { }, () => { }, () => { }, () => { }),
            "A missing tray owner is rejected before creating native resources");
        integration.Dispose();
        integration.Dispose();
        Check(registry.Read() == enabled, "Disposal leaves explicitly configured startup intact");
        Throws<ObjectDisposedException>(() => integration.CaptureStartup(), "Disposed integration rejects capture");
        Throws<ObjectDisposedException>(() => integration.SetStartup(true), "Disposed integration rejects startup changes");
        Throws<ObjectDisposedException>(() => integration.RestoreStartup(before), "Disposed integration rejects rollback");
        integration.HideTray();
        integration.HideTray();
        Check(registry.Read() == enabled, "Repeated tray hiding remains harmless after disposal and preserves startup");
        Throws<ObjectDisposedException>(() => integration.ShowTray(null!, () => { }, () => { }, () => { }, () => { }),
            "Disposed integration cannot create a tray icon");
    }

    private static void VerifyDesktopStartupSnapshots()
    {
        StartupRegistrationSnapshot[] originals =
        [
            StartupRegistrationSnapshot.Missing,
            new(true, string.Empty, RegistryValueKind.String),
            new(true, "\"C:\\Old installation\\Hollylark.exe\" --old-option", RegistryValueKind.String),
            new(true, "%HOLLYLARK_HOME%\\Hollylark.exe", RegistryValueKind.ExpandString),
            new(true, new byte[] { 0, 1, 255, 0 }, RegistryValueKind.Binary),
            new(true, Array.Empty<byte>(), RegistryValueKind.None),
            new(true, new byte[] { 9, 0, 7 }, RegistryValueKind.None),
            new(true, new string[] { "first", "%RAW_VALUE%", "zażółć" }, RegistryValueKind.MultiString),
            new(true, Array.Empty<string>(), RegistryValueKind.MultiString),
            new(true, int.MinValue, RegistryValueKind.DWord),
            new(true, long.MinValue, RegistryValueKind.QWord)
        ];
        foreach (StartupRegistrationSnapshot original in originals)
        {
            var registry = new DesktopStartupStore(original);
            using var integration = new DesktopIntegration(DesktopExecutablePath, registry, _ => FileAttributes.Normal);
            StartupRegistrationSnapshot captured = integration.CaptureStartup();
            Check(captured == original, $"Capture preserves raw {original.ValueKind} data and existence");
            integration.SetStartup(true);
            integration.RestoreStartup(captured);
            Check(integration.CaptureStartup() == original, $"Failed preference-save rollback restores {original.ValueKind} exactly");
            integration.SetStartup(false);
            integration.RestoreStartup(captured);
            Check(integration.CaptureStartup() == original, $"Disabled-startup rollback restores {original.ValueKind} exactly");
            registry.CheckUnrelatedEntries();
        }

        byte[] inputBytes = [1, 2, 3];
        var binary = new StartupRegistrationSnapshot(true, inputBytes, RegistryValueKind.Binary);
        var expectedBinary = new StartupRegistrationSnapshot(true, new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        int binaryHash = binary.GetHashCode();
        inputBytes[0] = 9;
        ((byte[])binary.RawValue!)[1] = 8;
        StartupRegistrationSnapshot binaryCopy = binary with { };
        ((byte[])binaryCopy.RawValue!)[2] = 7;
        Check(binary == expectedBinary && binaryCopy == expectedBinary && binary.GetHashCode() == binaryHash
            && binary.GetHashCode() == expectedBinary.GetHashCode(), "Binary snapshots and record copies are deeply immutable with value equality");
        string[] inputStrings = ["original", "%UNEXPANDED%"];
        var multiple = new StartupRegistrationSnapshot(true, inputStrings, RegistryValueKind.MultiString);
        inputStrings[0] = "changed";
        ((string[])multiple.RawValue!)[1] = "expanded";
        Check(((string[])multiple.RawValue!).SequenceEqual(new[] { "original", "%UNEXPANDED%" }),
            "Multi-string snapshots defensively copy constructor input and returned raw data");
        Check(new StartupRegistrationSnapshot(true, string.Empty, RegistryValueKind.String) != StartupRegistrationSnapshot.Missing,
            "An empty existing registry string is not a missing entry");
        Check(new StartupRegistrationSnapshot(true, "value", RegistryValueKind.String)
            != new StartupRegistrationSnapshot(true, "value", RegistryValueKind.ExpandString), "Snapshot equality includes the exact registry kind");
        Throws<ArgumentException>(() => new StartupRegistrationSnapshot(false, "unexpected", RegistryValueKind.Unknown),
            "Missing snapshots cannot hide existing data");
        Throws<ArgumentException>(() => new StartupRegistrationSnapshot(true, "wrong type", RegistryValueKind.DWord),
            "Mismatched snapshot types cannot reach registry restoration");
        Throws<ArgumentException>(() => new StartupRegistrationSnapshot(true, new string[] { null! }, RegistryValueKind.MultiString),
            "Invalid mutable snapshot contents are rejected");
        Throws<NotSupportedException>(() => new StartupRegistrationSnapshot(true, new byte[] { 1 }, RegistryValueKind.Unknown),
            "Unsupported existing registry kinds fail instead of being coerced");
    }

    private static void VerifyDesktopStartupPaths()
    {
        string?[] invalidPaths =
        [
            null, "", " ", "Hollylark.exe", @"C:Hollylark.exe", @"\Hollylark.exe",
            @"C:\Apps\dotnet.exe", @"C:\Apps\testhost.exe", @"C:\Apps\Hollylark.Validation.exe",
            @"C:\Apps\Hollylark.dll", @"C:\Apps\Other.exe", "\"C:\\Apps\\Hollylark.exe\"",
            @"C:\Apps\Hollylark.exe --minimized", "C:\\Apps\\\nHollylark.exe",
            @"\\?\C:\Apps\Hollylark.exe", @"\\.\C:\Apps\Hollylark.exe",
            @"C:\Apps\stream:Hollylark.exe", @"C:\Apps?\Hollylark.exe", @"C:\Apps\Hollylark.exe\",
            "C:\\" + new string('a', 250) + "\\Hollylark.exe"
        ];
        foreach (string? path in invalidPaths)
        {
            var registry = new DesktopStartupStore();
            using var integration = new DesktopIntegration(path, registry, _ => throw new InvalidOperationException("Unexpected file lookup."));
            Check(registry.ReadCount == 0 && registry.WriteCount == 0 && registry.DeleteCount == 0,
                "Even a test-host or unavailable executable path permits inert construction");
            Throws<ArgumentException>(() => integration.SetStartup(true), $"Unsafe or non-production startup path is rejected: {path ?? "<null>"}");
            Check(registry.ReadCount == 0 && registry.WriteCount == 0 && registry.DeleteCount == 0,
                "Invalid executable path fails before touching startup registration");
        }
        foreach (string path in new[] { @"C:\Zażółć gęślą\HOLLYLARK.EXE", @"\\server\share\Hollylark.exe" })
        {
            var registry = new DesktopStartupStore();
            using var integration = new DesktopIntegration(path, registry, _ => FileAttributes.Normal);
            integration.SetStartup(true);
            Check(Equals(integration.CaptureStartup().RawValue, "\"" + path + "\""),
                "Absolute Unicode and UNC apphost paths remain quoted and unchanged");
        }
        string longestPath = "C:\\" + new string('a', 241) + "\\Hollylark.exe";
        Check(longestPath.Length + 2 == 260, "Startup command boundary fixture is exactly 260 characters");
        using (var boundary = new DesktopIntegration(longestPath, new DesktopStartupStore(), _ => FileAttributes.Normal))
        {
            boundary.SetStartup(true);
            Check(Equals(boundary.CaptureStartup().RawValue, "\"" + longestPath + "\""),
                "A quoted startup command exactly at the Windows Run limit is accepted");
        }
        using (var directory = new DesktopIntegration(DesktopExecutablePath, new DesktopStartupStore(), _ => FileAttributes.Directory))
            Throws<IOException>(() => directory.SetStartup(true), "A directory named Hollylark.exe is not a startup executable");

        var existing = new StartupRegistrationSnapshot(true, "prior command", RegistryValueKind.String);
        var unavailableRegistry = new DesktopStartupStore(existing);
        using var unavailable = new DesktopIntegration(null, unavailableRegistry, _ => throw new FileNotFoundException());
        unavailable.SetStartup(false);
        unavailable.RestoreStartup(existing);
        Check(unavailable.CaptureStartup() == existing, "Disabling and restoring startup do not require an available executable");
    }

    private static void VerifyDesktopStartupFailures()
    {
        var registry = new DesktopStartupStore();
        using var integration = new DesktopIntegration(DesktopExecutablePath, registry, _ => FileAttributes.Normal);
        registry.FailRead = true;
        Throws<UnauthorizedAccessException>(() => integration.CaptureStartup(), "Registry read denial is surfaced");
        Throws<UnauthorizedAccessException>(() => integration.SetStartup(true), "Unreadable startup state is not overwritten");
        Check(registry.WriteCount == 0 && registry.DeleteCount == 0, "Failed startup capture makes no changes");
        registry.FailRead = false;
        registry.FailWrite = true;
        Throws<IOException>(() => integration.SetStartup(true), "Registry write failure is surfaced");
        Check(!integration.CaptureStartup().Exists, "Rejected registry write preserves absence");
        registry.FailWrite = false;
        StartupRegistrationSnapshot before = integration.CaptureStartup();
        registry.FailAfterWriteOnce = true;
        Throws<IOException>(() => integration.SetStartup(true), "A failure reported after mutation is not hidden");
        integration.RestoreStartup(before);
        Check(integration.CaptureStartup() == before, "Captured state can undo a partially completed startup operation");
        integration.SetStartup(true);
        registry.FailDelete = true;
        Throws<IOException>(() => integration.SetStartup(false), "Registry delete failure is surfaced");
        Throws<IOException>(() => integration.RestoreStartup(before), "Rollback failure is surfaced for the parent to report");
        Check(integration.CaptureStartup().Exists, "Failed deletion preserves the existing startup entry");
        registry.FailDelete = false;
        integration.RestoreStartup(before);
        registry.CheckUnrelatedEntries();

        var missingRegistry = new DesktopStartupStore();
        using var missingFile = new DesktopIntegration(DesktopExecutablePath, missingRegistry,
            _ => throw new FileNotFoundException("The Hollylark apphost no longer exists."));
        Throws<FileNotFoundException>(() => missingFile.SetStartup(true), "Missing startup apphost is reported without guessing another path");
        Check(missingRegistry.ReadCount == 0 && missingRegistry.WriteCount == 0, "Missing executable cannot create a startup entry");
    }

    private sealed class DesktopStartupStore : IStartupRegistrationStore
    {
        private readonly Dictionary<string, StartupRegistrationSnapshot> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly StartupRegistrationSnapshot unrelated = new(true, "\"C:\\Other\\Other.exe\"", RegistryValueKind.String);
        internal int ReadCount { get; private set; }
        internal int WriteCount { get; private set; }
        internal int DeleteCount { get; private set; }
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailDelete { get; set; }
        internal bool FailAfterWriteOnce { get; set; }

        internal DesktopStartupStore(StartupRegistrationSnapshot? original = null)
        {
            entries.Add("OtherApplication", unrelated);
            entries.Add("HollylarkHelper", unrelated);
            if (original is { Exists: true })
                entries.Add(DesktopIntegration.StartupValueName, original);
        }

        public StartupRegistrationSnapshot Read()
        {
            ReadCount++;
            if (FailRead)
                throw new UnauthorizedAccessException("Simulated startup registry read denial.");
            return entries.GetValueOrDefault(DesktopIntegration.StartupValueName, StartupRegistrationSnapshot.Missing);
        }

        public void Write(object rawValue, RegistryValueKind valueKind)
        {
            if (FailWrite)
                throw new IOException("Simulated startup registry write failure.");
            WriteCount++;
            entries[DesktopIntegration.StartupValueName] = new StartupRegistrationSnapshot(true, rawValue, valueKind);
            if (FailAfterWriteOnce)
            {
                FailAfterWriteOnce = false;
                throw new IOException("Simulated failure after the startup entry changed.");
            }
        }

        public void Delete()
        {
            if (FailDelete)
                throw new IOException("Simulated startup registry delete failure.");
            DeleteCount++;
            entries.Remove(DesktopIntegration.StartupValueName);
        }

        internal void CheckUnrelatedEntries() => Check(entries["OtherApplication"] == unrelated
            && entries["HollylarkHelper"] == unrelated && entries.Count is 2 or 3,
            "Startup operations preserve unrelated entries, including names sharing Hollylark's prefix");
    }
}
