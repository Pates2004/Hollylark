using System.Windows;
using System.Windows.Controls;

namespace LarkControl.Windows;

internal sealed class FirmwareWindow : AuxiliaryWindow
{
    private readonly TextBox summary;
    private readonly Action<Uri>? copyAddress;

    internal FirmwareWindow(Action<Uri>? copyAddress = null) : base(Localization.Text("Firmware.Title"), "FirmwareWindow")
    {
        this.copyAddress = copyAddress;
        var content = Named(new Grid { Margin = new Thickness(12) }, "FirmwarePage", Localization.Text("Firmware.PageName"));
        for (int row = 0; row < 7; row++)
            content.RowDefinitions.Add(new RowDefinition { Height = row == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        summary = Readout("FirmwareDeviceReadout", Localization.Text("Firmware.SummaryName"));
        summary.MinHeight = 72;
        summary.MaxHeight = 180;
        content.Children.Add(Label(Localization.Text("Firmware.SummaryLabel"), summary));
        Grid.SetRow(summary, 1);
        content.Children.Add(summary);
        UpdateContent(string.Empty);

        TextBox warning = Readout("FirmwareSafetyReadout", Localization.Text("Firmware.WarningName"));
        warning.Text = Localization.Text("Firmware.Warning");
        Label warningLabel = Label(Localization.Text("Firmware.WarningLabel"), warning);
        Grid.SetRow(warningLabel, 2);
        Grid.SetRow(warning, 3);
        content.Children.Add(warningLabel);
        content.Children.Add(warning);

        TextBox guide = Readout("FirmwareGuideReadout", Localization.Text("Firmware.GuideName"));
        guide.MinHeight = 150;
        guide.VerticalAlignment = VerticalAlignment.Stretch;
        guide.VerticalContentAlignment = VerticalAlignment.Top;
        guide.AcceptsReturn = true;
        guide.Text = string.Join(Environment.NewLine + Environment.NewLine,
            Localization.Text("Firmware.Guide.RecordVersions"),
            Localization.Text("Firmware.Guide.ReadManual"),
            Localization.Text("Firmware.Guide.UseOfficialApplication"),
            Localization.Text("Firmware.Guide.KeepConnected"),
            Localization.Text("Firmware.Guide.Failure"),
            Localization.Text("Firmware.Guide.Reconnect"));
        Label guideLabel = Label(Localization.Text("Firmware.GuideLabel"), guide);
        Grid.SetRow(guideLabel, 4);
        Grid.SetRow(guide, 5);
        content.Children.Add(guideLabel);
        content.Children.Add(guide);

        var links = new WrapPanel();
        string manualCaption = Localization.Text("Support.ManualCaption");
        string applicationCaption = Localization.Text("Support.ApplicationCaption");
        Button manual = Button("OpenFirmwareManualButton", manualCaption,
            Localization.Text("Support.ManualName"));
        Button application = Button("OpenFirmwareAppButton", applicationCaption,
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
        Grid.SetRow(links, 6);
        content.Children.Add(links);
        Body.Children.Add(Named(new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Stretch, Focusable = false
        }, "FirmwareScroll", Localization.Text("Firmware.ScrollName")));
        InitialFocus = summary;
    }

    internal void UpdateContent(string firmwareSummary)
    {
        UiSupport.UpdateReadout(summary, string.IsNullOrWhiteSpace(firmwareSummary)
            ? Localization.Text("Firmware.NoVersion")
            : firmwareSummary);
    }

    private void CopySupportLink(Uri address)
    {
        SupportLinkResult result = OfficialSupportLinks.CopyAddress(address, copyAddress);
        ShowResult(result.Message, result.IsError, announce: true, optional: true);
    }
}
