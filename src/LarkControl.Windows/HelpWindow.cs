using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace LarkControl.Windows;

internal sealed class HelpWindow : AuxiliaryWindow
{
    private readonly Action<Uri>? copyAddress;

    internal HelpWindow(Action<Uri>? copyAddress = null) : base(Localization.Text("Help.Title"), "HelpWindow")
    {
        this.copyAddress = copyAddress;
        TextBox help = Readout("HelpReadout", Localization.Text("Help.GuideName"));
        help.BorderThickness = new Thickness(0);
        help.Padding = new Thickness(6);
        help.Margin = new Thickness(0);
        help.VerticalAlignment = VerticalAlignment.Stretch;
        help.VerticalContentAlignment = VerticalAlignment.Top;
        help.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        help.Text = string.Join(Environment.NewLine + Environment.NewLine,
            Localization.Text("Help.Guide.Introduction"),
            Localization.Text("Help.Guide.FirstRun"),
            Localization.Text("Help.Guide.Connection"),
            Localization.Text("Help.Guide.ReceiverSelection"),
            Localization.Text("Help.Guide.MicrophoneSelection"),
            Localization.Text("Help.Guide.ApplyChanges"),
            Localization.Text("Help.Guide.Refresh"),
            Localization.Text("Help.Guide.Keyboard"),
            Localization.Text("Help.Guide.GlobalHotkeys"),
            Localization.Text("Help.Guide.StateAnnouncements"),
            Localization.Text("Help.Guide.Retention"),
            Localization.Text("Help.Guide.Cancellation"),
            Localization.Text("Help.Guide.SafetyGates"),
            Localization.Text("Help.Guide.Battery"),
            Localization.Text("Help.Guide.BatteryAlerts"),
            Localization.Text("Help.Guide.Firmware"),
            Localization.Text("Help.Guide.AudioMeter"),
            Localization.Text("Help.Guide.Settings"),
            Localization.Text("Help.Guide.Language"),
            Localization.Text("Help.Guide.SavingSettings"),
            Localization.Text("Help.Guide.DesktopIntegration"),
            Localization.Text("Help.Guide.Profiles"),
            Localization.Text("Help.Guide.Diagnostics"),
            Localization.Text("Help.Guide.SupportLinks"));
        TextBox licenses = Readout("LicensesReadout", Localization.Text("Help.LicensesName"));
        licenses.Text = ReadLicenses();
        licenses.MaxHeight = 340;
        var instructions = new StackPanel();
        var links = new WrapPanel();
        string manualCaption = Localization.Text("Support.ManualCaption");
        string applicationCaption = Localization.Text("Support.ApplicationCaption");
        Button manual = Button("OpenManualButton", manualCaption,
            Localization.Text("Support.ManualName"));
        Button application = Button("OpenOfficialAppButton", applicationCaption,
            Localization.Text("Support.ApplicationName"));
        manual.Content = new AccessText { Text = manualCaption, TextWrapping = TextWrapping.Wrap };
        application.Content = new AccessText { Text = applicationCaption, TextWrapping = TextWrapping.Wrap };
        manual.ContentTemplate = application.ContentTemplate = null;
        manual.MaxWidth = application.MaxWidth = 360;
        manual.Click += (_, _) => CopySupportLink(OfficialSupportLinks.Manual);
        application.Click += (_, _) => CopySupportLink(OfficialSupportLinks.AndroidApplication);
        UsageHints.SetText(manual, Localization.Text("Support.ManualHint"));
        UsageHints.SetText(application, Localization.Text("Support.ApplicationHint"));
        links.Children.Add(manual);
        links.Children.Add(application);
        instructions.Children.Add(Label(Localization.Text("Help.GuideLabel"), help));
        instructions.Children.Add(help);
        instructions.Children.Add(links);
        instructions.Children.Add(Label(Localization.Text("Help.LicensesLabel"), licenses));
        instructions.Children.Add(licenses);
        Body.Children.Add(Named(new ScrollViewer
        {
            Content = instructions, Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        }, "HelpScroll", Localization.Text("Help.ScrollName")));
        InitialFocus = help;
    }

    private void CopySupportLink(Uri address)
    {
        SupportLinkResult result = OfficialSupportLinks.CopyAddress(address, copyAddress);
        ShowResult(result.Message, result.IsError, announce: true, optional: true);
    }

    private static string ReadLicenses()
    {
        var text = new StringBuilder();
        foreach (string resource in new[] { "HidSharp.LICENSE.txt", "NAudio.LICENSE.txt", "Prism.NOTICES.txt", "Hollylark.NOTICES.txt" })
        {
            using Stream? stream = typeof(MainWindow).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                text.AppendLine(Localization.Text("Help.MissingDocument", resource));
                continue;
            }
            using var reader = new StreamReader(stream);
            text.AppendLine(reader.ReadToEnd());
            text.AppendLine();
        }
        return text.ToString().ReplaceLineEndings(Environment.NewLine);
    }
}
