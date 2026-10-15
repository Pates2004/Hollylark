using System.ComponentModel;
using System.IO;
using System.Security;
using System.Runtime.InteropServices;
using System.Windows;

namespace LarkControl.Windows;

internal readonly record struct SupportLinkResult(string Message, bool IsError);

internal static class OfficialSupportLinks
{
    internal static Uri Manual { get; } = new(
        "https://download.hollyland.com/User_manual/LARK A1/User Manual/LARK A1 - User Manual【English】.pdf");
    internal static Uri AndroidApplication { get; } = new(
        "https://play.google.com/store/apps/details?id=com.hollyland.larkc1");

    internal static SupportLinkResult CopyAddress(Uri address, Action<Uri>? copyAddress = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address != Manual && address != AndroidApplication)
            return new(Localization.Text("Support.AddressNotAllowed"), true);
        try
        {
            if (copyAddress is not null) copyAddress(address);
            else Clipboard.SetText(address.AbsoluteUri);
            return new(Localization.Text("Support.AddressCopied", address.AbsoluteUri), false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException
            or SecurityException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException)
        {
            return new(Localization.Text("Support.ClipboardFailed", address.AbsoluteUri), true);
        }
    }

}
