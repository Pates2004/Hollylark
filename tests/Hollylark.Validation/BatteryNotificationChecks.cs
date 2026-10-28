using System.Reflection;
using System.Runtime.InteropServices;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyBatteryNotificationInfrastructure()
    {
        MethodInfo create = typeof(DesktopIntegration).GetMethod("CreateNotificationData",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        IntPtr window = new(42);
        object? Create(string? title, string? message, bool critical = false) =>
            create.Invoke(null, [window, title, message, critical]);

        foreach (bool critical in new[] { false, true })
        {
            const string title = "Bateria \U0001F50B";
            const string message = "Mikrofon 1: 10%.\nMikrofon 2: 5%.";
            object data = Create(title, message, critical)!;
            Check(ReadBatteryNotificationField<uint>(data, "Size") == (uint)Marshal.SizeOf(data)
                && ReadBatteryNotificationField<IntPtr>(data, "Window") == window
                && ReadBatteryNotificationField<uint>(data, "Id") == 1,
                "Battery notifications identify the existing tray window and icon with the Unicode ABI size");
            Check(ReadBatteryNotificationField<uint>(data, "Flags") == (0x10u | 0x40u)
                && ReadBatteryNotificationField<uint>(data, "InfoFlags") == ((critical ? 3u : 2u) | 0x10u),
                "Battery notifications are realtime warning/error messages with independent sound suppressed");
            Check(ReadBatteryNotificationField<string>(data, "InfoTitle") == title
                && ReadBatteryNotificationField<string>(data, "Info") == message,
                "Battery notification data preserves caller-provided Unicode and line breaks exactly");
            Check(ReadBatteryNotificationField<string>(data, "Tip") == string.Empty
                && ReadBatteryNotificationField<uint>(data, "CallbackMessage") == 0
                && ReadBatteryNotificationField<IntPtr>(data, "Icon") == IntPtr.Zero
                && ReadBatteryNotificationField<uint>(data, "Version") == 0
                && ReadBatteryNotificationField<uint>(data, "State") == 0
                && ReadBatteryNotificationField<uint>(data, "StateMask") == 0
                && ReadBatteryNotificationField<Guid>(data, "Guid") == Guid.Empty
                && ReadBatteryNotificationField<IntPtr>(data, "BalloonIcon") == IntPtr.Zero,
                "Battery notification data leaves tray lifecycle, tooltip, callbacks and focus untouched");
        }

        string longestTitle = new('t', 63);
        string longestMessage = new('m', 255);
        Check(Create(longestTitle, longestMessage) is not null,
            "Notification text accepts exactly 63 title and 255 message UTF-16 code units");
        Check(Create(longestTitle + "t", longestMessage) is null
            && Create(longestTitle, longestMessage + "m") is null,
            "Oversized notification text is refused rather than silently truncated");

        string pairedTitle = new string('t', 61) + "\U0001F50B";
        string pairedMessage = new string('m', 253) + "\U0001F50B";
        object paired = Create(pairedTitle, pairedMessage)!;
        Check(ReadBatteryNotificationField<string>(paired, "InfoTitle") == pairedTitle
            && ReadBatteryNotificationField<string>(paired, "Info") == pairedMessage,
            "Surrogate pairs at both native buffer boundaries remain intact");
        Check(Create(new string('t', 62) + "\U0001F50B", longestMessage) is null
            && Create(longestTitle, new string('m', 254) + "\U0001F50B") is null,
            "Surrogate pairs that would exceed either native buffer are refused intact");

        string?[] invalid = [null, string.Empty, " \t\r\n", "\0", "before\0after", "\uD83D", "\uDD0B",
            "before\uD83Dafter", "before\uDD0Bafter", "\uDD0B\uD83D"];
        foreach (string? text in invalid)
            Check(Create(text, "message") is null && Create("title", text) is null,
                "Empty, blank, embedded-NUL and malformed UTF-16 notification text is refused");
        Check(create.Invoke(null, [IntPtr.Zero, "title", "message", false]) is null,
            "Notification data refuses a missing tray window handle");

        var memory = new MemoryDesktop();
        IDesktopIntegration legacy = memory;
        Check(!legacy.ShowNotification("title", "message", false)
            && !legacy.ShowNotification(null!, null!, true)
            && memory.TrayShows == 0 && memory.StartupWrites == 0 && !memory.Disposed && !memory.TrayVisible,
            "Default notification delivery on MemoryDesktop stays inert and reports unsupported delivery");

        var registry = new DesktopStartupStore();
        using var integration = new DesktopIntegration(null, registry,
            _ => throw new InvalidOperationException("Notification checks must not inspect the executable."));
        Check(!integration.ShowNotification("title", "message", false)
            && !integration.ShowNotification("title", "message", true)
            && registry.ReadCount == 0 && registry.WriteCount == 0 && registry.DeleteCount == 0,
            "Notification delivery without an existing tray returns false without creating native or startup resources");
        integration.Dispose();
        Throws<ObjectDisposedException>(() => integration.ShowNotification("title", "message", false),
            "Disposed desktop integration rejects notification delivery before native work");
    }

    private static T ReadBatteryNotificationField<T>(object data, string name) =>
        (T)data.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(data)!;
}
