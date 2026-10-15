using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyExactAuxiliaryLayouts()
    {
        foreach (int scale in new[] { 100, 200 })
        {
            var copiedAddresses = new List<Uri>();
            var source = new FakeAudioMeterSource();
            int savedDiagnostics = 0;
            var information = new InfoWindow(() =>
            {
                savedDiagnostics++;
                return "Sprawdzono przycisk bez zapisu pliku.";
            });
            var help = new HelpWindow(copiedAddresses.Add);
            var firmware = new FirmwareWindow(copiedAddresses.Add);
            var meter = new AudioMeterWindow(() => 0, source);
            AuxiliaryWindow[] sections = [information, help, firmware, meter];
            try
            {
                information.UpdateContent("LARK A1\r\nRX: 1.0.2.8\r\nTX1: połączony\r\nTX2: niepołączony",
                    string.Join(Environment.NewLine, Enumerable.Repeat("Odczyt testowy bez połączenia USB.", 40)), true);
                firmware.UpdateContent("RX: 1.0.2.8\r\nTX1: 1.0.2.8\r\nTX2: brak odczytu");
                foreach (AuxiliaryWindow section in sections)
                {
                    Check(section.Width == 840 && section.Height == 760 && section.MinWidth == 520 && section.MinHeight == 400,
                        "Auxiliary dimensions match the reference section shell: " + section.Name);
                    section.ApplyPreferences(new UiPreferences
                    {
                        TextScale = scale, Theme = scale == 200 ? "Dark" : "Light", ShowHints = false,
                        ShowOperationMessages = false, AnnounceStateChanges = false
                    });
                    if (scale == 200)
                    {
                        section.Width = section.MinWidth;
                        section.Height = section.MinHeight;
                    }
                    MoveOffscreen(section);
                    section.Show();
                    Pump();
                    Check(!Descendants(section).OfType<TabControl>().Any(),
                        "Reference auxiliary pages do not add a second layer of tabs: " + section.Name);
                    Check(FindById<Button>(section, "CloseSectionButton").IsVisible,
                        "The shared close action stays outside the scrolling page: " + section.Name);
                    foreach (Label label in Descendants(section).OfType<Label>().Where(label => label.Target is not null))
                    {
                        Check(ReferenceEquals(AutomationProperties.GetLabeledBy(label.Target), label),
                            "Auxiliary labels retain their actual UIA target: " + section.Name);
                        Check(!string.IsNullOrWhiteSpace(GetAccessiblePeer(label.Target!).GetName()),
                            "Every labelled auxiliary control retains a readable name: " + section.Name);
                    }
                }

                VerifyExactInformationPage(information);
                VerifyExactHelpPage(help);
                VerifyExactFirmwarePage(firmware);
                VerifyExactAuxiliaryScroll(meter, "AudioMeterScroll", "ReadAudioLevels", true);
                Check(source.Starts == 0 && !FindById<Button>(meter, "StartAudioMeter").IsEnabled,
                    "Applying reference meter spacing never chooses an input or starts capture");
                Check(FindById<TextBlock>(meter, "SectionResultReadout").Visibility == Visibility.Collapsed,
                    "Reference meter layout preserves the disabled optional scan feedback");
                FindById<Button>(information, "SaveDiagnosticsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(savedDiagnostics == 1, "The relocated diagnostic action still calls its supplied handler exactly once");
                Check(copiedAddresses.Count == 0, "Auxiliary layout validation does not copy or open support links implicitly");
                foreach (AuxiliaryWindow section in sections)
                    RenderWindow(section, $"exact-auxiliary-{section.Name}-{scale}.png");
            }
            finally
            {
                foreach (AuxiliaryWindow section in sections) section.Close();
                Pump();
            }
            Check(source.Disposed && source.Starts == 0, "Meter layout checks dispose their fake source without any capture");
        }
    }

    private static void VerifyExactInformationPage(InfoWindow window)
    {
        var scroll = (ScrollViewer)window.FindName("DiagnosticsScroll");
        Check(scroll.Content is StackPanel, "Information uses the reference's vertically stacked scrollable page");
        var content = (StackPanel)scroll.Content;
        Check(content.Children[0] is TextBlock { Visibility: Visibility.Collapsed, Margin: var margin }
            && margin == new Thickness(0, 0, 0, 12), "The information hint precedes the fields and obeys the hint preference");
        TextBox summary = FindById<TextBox>(window, "DeviceInformationReadout");
        TextBox log = FindById<TextBox>(window, "DiagnosticLogReadout");
        Button identity = FindById<Button>(window, "ReadIdentityButton");
        Button save = FindById<Button>(window, "SaveDiagnosticsButton");
        Check(content.Children.IndexOf(summary) < content.Children.IndexOf(identity)
            && content.Children.IndexOf(identity) < content.Children.IndexOf(log)
            && content.Children.IndexOf(log) < content.Children.IndexOf(save),
            "LARK-specific identification and diagnostic actions follow the fields they concern");
        Check(summary.IsReadOnly && log.IsReadOnly && summary.Text.Contains("TX2: niepołączony")
            && log.Text.Contains("bez połączenia USB"), "Reference information layout preserves actual summary and diagnostic contents");
        VerifyExactAuxiliaryScroll(window, "DiagnosticsScroll", "SaveDiagnosticsButton", true);
    }

    private static void VerifyExactHelpPage(HelpWindow window)
    {
        var scroll = (ScrollViewer)window.FindName("HelpScroll");
        Check(scroll.Content is StackPanel, "Help uses the supplied reference's single stacked page, not custom tabs");
        var content = (StackPanel)scroll.Content;
        TextBox instructions = FindById<TextBox>(window, "HelpReadout");
        TextBox licenses = FindById<TextBox>(window, "LicensesReadout");
        Check(instructions.BorderThickness == new Thickness(0) && instructions.Padding == new Thickness(6)
            && instructions.Margin == new Thickness(0) && instructions.IsReadOnlyCaretVisible
            && instructions.VerticalAlignment == VerticalAlignment.Stretch
            && instructions.VerticalContentAlignment == VerticalAlignment.Top,
            "Help overview matches reference borderless padding and remains keyboard-readable");
        Check(instructions.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "The unbounded help overview uses the page scrollbar rather than a redundant nested scrollbar");
        Check(content.Children.IndexOf(instructions) < content.Children.IndexOf(licenses)
            && licenses.IsReadOnly && licenses.MaxHeight == 340 && licenses.Text.Contains("NAudio"),
            "Library licenses remain available below the instructions as a bounded labelled readout");
        VerifyReferenceReadoutLabel(window, instructions, "Instrukcja i ograniczenia Hollylark");
        VerifyReferenceReadoutLabel(window, licenses, "Licencje bibliotek — oryginalna treść");
        VerifyExactAuxiliaryScroll(window, "HelpScroll", "OpenOfficialAppButton", true);
        licenses.BringIntoView();
        Pump();
        Check(scroll.VerticalOffset > 0, "The reference help page can scroll to the retained license field");
    }

    private static void VerifyExactFirmwarePage(FirmwareWindow window)
    {
        var scroll = (ScrollViewer)window.FindName("FirmwareScroll");
        var content = (Grid)window.FindName("FirmwarePage");
        Check(ReferenceEquals(scroll.Content, content) && content.Margin == new Thickness(12),
            "Firmware uses the reference's inset grid with a scroll fallback for the additional safety section");
        TextBox versions = FindById<TextBox>(window, "FirmwareDeviceReadout");
        TextBox warning = FindById<TextBox>(window, "FirmwareSafetyReadout");
        TextBox guide = FindById<TextBox>(window, "FirmwareGuideReadout");
        Check(content.RowDefinitions.Count == 7 && content.RowDefinitions[5].Height.IsStar
            && Grid.GetRow(versions) == 1 && Grid.GetRow(warning) == 3 && Grid.GetRow(guide) == 5,
            "Firmware follows labelled metadata, retained safety notice, growing guide and support actions");
        Check(guide.VerticalAlignment == VerticalAlignment.Stretch && guide.VerticalContentAlignment == VerticalAlignment.Top
            && guide.AcceptsReturn && guide.IsReadOnly && double.IsPositiveInfinity(guide.MaxHeight),
            "Firmware instructions retain the reference's stretchable multiline readout");
        if (window.Width == 840 && window.Height == 760)
            Check(guide.ActualHeight > 300, "Firmware guide uses available window height instead of leaving capped empty space");
        Check(warning.IsVisible && warning.IsReadOnly && warning.Text.Contains("nie aktualizatorem")
            && warning.Text.Contains("nie sprawdza"), "Reference layout never hides or overstates the firmware limitation");
        VerifyExactAuxiliaryScroll(window, "FirmwareScroll", "OpenFirmwareAppButton", false);
    }

    private static void VerifyExactAuxiliaryScroll(Window window, string identifier, string lastAction, bool padded)
    {
        var scroll = (ScrollViewer)window.FindName(identifier);
        Check(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && !scroll.Focusable,
            "Auxiliary content uses the reference's non-tab-stop vertical scrolling: " + identifier);
        if (padded)
            Check(scroll.Padding == new Thickness(12), "Auxiliary page spacing matches the reference: " + identifier);
        Button action = FindById<Button>(window, lastAction);
        action.BringIntoView();
        Pump();
        Check(scroll.ScrollableWidth <= 1, "Auxiliary page never requires sideways scrolling: " + identifier);
        Rect viewport = scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize));
        Rect actionBounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
        Check(viewport.IntersectsWith(actionBounds), "The page can bring its final action into view: " + identifier);
        Check(action.IsTabStop && !string.IsNullOrWhiteSpace(GetAccessiblePeer(action).GetName()),
            "The final page action remains keyboard-focusable and named: " + identifier);
    }
}
