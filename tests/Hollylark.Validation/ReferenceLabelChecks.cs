using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyReferenceLabels()
    {
        VerifyLarkAssemblyContents();
        VerifyReferenceSettingLabels();
        VerifyReferenceSectionLabels();
        VerifyAuxiliaryOperationPreference();
        VerifyEffectDisablePreservesLevel();
        VerifyExactAuxiliaryLayouts();
        VerifyExactMainReference();
        VerifyExactProfileReference();
    }

    private static void VerifyReferenceSettingLabels()
    {
        using var transport = new FakeTransport();
        MainWindow window = CreatePresentationWindow(transport, "reference-labels", new MemoryDesktop());
        try
        {
            RunTask(window.ConnectAsync());
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 0, "Sterowanie");
            foreach (SettingRowViewModel setting in AllSettings(window))
            {
                TextBlock current = FindById<TextBlock>(window, setting.Id + ".StaticCurrent");
                ComboBox choice = FindById<ComboBox>(window, setting.ChoiceId);
                VerifyNamedPeer(current, setting.StaticValueText, AutomationControlType.Text);
                Check(ReadAccessibleValue(current) == setting.StaticValueText,
                    "The static current value identifies its setting without an additional edit field");
                VerifyReferenceEditorName(window, choice, setting.ChoiceName);
                foreach (int selectedIndex in Enumerable.Range(0, setting.Options.Count))
                {
                    choice.SelectedIndex = selectedIndex;
                    Pump();
                    VerifyReferenceSelection(choice, setting.Options[selectedIndex]);
                }
            }
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "Inspecting every selector label changes only drafts, never the receiver");

            SavePreferences(window, window.CurrentPreferences with { TextScale = 200, ShowHints = false });
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Pump();
            foreach (GainControlMode mode in Enum.GetValues<GainControlMode>())
            {
                SavePreferences(window, window.CurrentPreferences with { GainControlMode = mode });
                string targetId = mode switch
                {
                    GainControlMode.Buttons => "Gain.Decrease",
                    GainControlMode.Slider => "Gain.Slider",
                    _ => "Gain.Choice"
                };
                FrameworkElement target = FindById<FrameworkElement>(window, targetId);
                SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
                VerifyReferenceEditorName(window, target, gain.ChoiceName,
                    mode == GainControlMode.Buttons ? gain.DecreaseName
                        : mode == GainControlMode.Slider ? LarkControl.Windows.Localization.Text("Setting.Gain.SliderName") : null);
                Check(target.IsVisible, "Gain label targets the visible editor/readout in " + mode);
                Check(Descendants(window).OfType<Label>().Where(label => label.IsVisible)
                    .All(label => label.Target is null || label.Target.IsVisible),
                    "Visible setting labels do not point to hidden controls in " + mode);
            }
            SavePreferences(window, window.CurrentPreferences with { GainControlMode = GainControlMode.Choice });
            ComboBox autoOff = FindById<ComboBox>(window, "AutoOff.Choice");
            autoOff.SelectedIndex = 0;
            autoOff.BringIntoView();
            Pump();
            TextBlock selectedText = Descendants(autoOff).OfType<TextBlock>()
                .First(block => block.Text == "Po 15 minutach bez sparowania");
            Check(autoOff.Text == selectedText.Text && autoOff.SelectedItem is ComboBoxItem native
                && GetAccessiblePeer(native).GetName() == selectedText.Text,
                "Native LARK selector retains the complete selected caption and accessible name at 200 percent");
            Check(autoOff.ActualWidth > selectedText.DesiredSize.Width,
                "Native selector fits the complete long caption at 200 percent without a custom wrapping template");
            RenderWindow(window, "reference-labels-main-200.png");
            SavePreferences(window, window.CurrentPreferences with { ValueDisplayMode = ValueDisplayMode.StaticText });
            TextBlock staticGain = Descendants(window).OfType<TextBlock>().First(block =>
                block.IsVisible && block.Text.StartsWith("Wzmocnienie:", StringComparison.Ordinal));
            Check(Grid.GetColumnSpan(staticGain) == 2, "Static readout reuses both label/value columns like the reference");
            SavePreferences(window, window.CurrentPreferences with { ValueDisplayMode = ValueDisplayMode.None });
            VerifyReferenceEditorName(window, FindById<ComboBox>(window, "Gain.Choice"),
                window.AudioSettings.Single(setting => setting.Id == "Gain").ChoiceName);
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 1, "Odbiornik USB");
            ListBox receivers = FindById<ListBox>(window, "ReceiverList");
            VerifyReferenceReadoutLabel(window, receivers, "Wykryte odbiorniki LARK A1 USB");
            VerifyReferenceSelection(receivers, ((ReceiverInfo)receivers.SelectedItem).Name);
            VerifyLarkOnlyPresentation(window);
            SelectAccessibleTab(FindById<TabControl>(window, "MainTabs"), 0, "Sterowanie");
            VerifyLarkOnlyPresentation(window);
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyReferenceSectionLabels()
    {
        using var source = new FakeAudioMeterSource();
        var information = new InfoWindow(() => "Nie zapisano pliku w teście etykiet.");
        var help = new HelpWindow(_ => throw new InvalidOperationException("Label validation must not copy an address."));
        var firmware = new FirmwareWindow(_ => throw new InvalidOperationException("Label validation must not copy an address."));
        var meter = new AudioMeterWindow(() => 1, source);
        AuxiliaryWindow[] sections = [information, help, firmware, meter];
        try
        {
            foreach (AuxiliaryWindow section in sections)
            {
                section.ApplyPreferences(new UiPreferences { ShowHints = false, TextScale = 200 });
                section.Width = section.MinWidth;
                section.Height = section.MinHeight;
                MoveOffscreen(section);
                section.Show();
                Pump();
                VerifyLarkOnlyPresentation(section);
                foreach (Label label in Descendants(section).OfType<Label>().Where(label => label.IsVisible && label.Target is not null))
                {
                    string caption = ReferenceCaption(label.Content).TrimEnd(':');
                    Check(AutomationProperties.GetName(label.Target) == (label.Target is TextBlock readout ? readout.Text : caption),
                        "Visible and accessible section labels agree: " + section.Name + " / " + caption);
                    Check(ReferenceEquals(AutomationProperties.GetLabeledBy(label.Target), label),
                        "Section readout exposes its visible label to UIA: " + caption);
                }
                foreach (Button button in Descendants(section).OfType<Button>().Where(button => button.IsVisible && button.Content is string or AccessText))
                {
                    string caption = ReferenceCaption(button.Content);
                    Check(GetAccessiblePeer(button).GetName().StartsWith(caption, StringComparison.Ordinal),
                        "Section action's UIA name starts with the visible caption: " + caption);
                }
                RenderWindow(section, "reference-labels-" + section.Name + "-200.png");
            }
            VerifyReferenceReadoutLabel(help, FindById<TextBox>(help, "LicensesReadout"), "Licencje bibliotek — oryginalna treść");
            string instructions = FindById<TextBox>(help, "HelpReadout").Text;
            Check(instructions.Contains(LarkControl.Windows.Localization.Text("Help.Guide.Refresh"), StringComparison.Ordinal),
                "Help retains the complete explanation of refresh discarding drafts even with optional hints disabled");
            Check(instructions.Contains("kopiują do schowka", StringComparison.Ordinal)
                && !instructions.Contains("Przyciski pod instrukcją otwierają", StringComparison.Ordinal),
                "Support instructions describe copying, not an unimplemented browser launch");
            ComboBox inputs = FindById<ComboBox>(meter, "AudioInputChoice");
            foreach (int selectedIndex in Enumerable.Range(0, inputs.Items.Count))
            {
                inputs.SelectedIndex = selectedIndex;
                Pump();
                VerifyReferenceSelection(inputs, ((AudioInput)inputs.SelectedItem).Name);
            }
            Check(source.Starts == 0, "Audio label and selection validation never starts capture");
        }
        finally
        {
            foreach (AuxiliaryWindow section in sections) section.Close();
            Pump();
        }
    }

    private static void VerifyReferenceReadoutLabel(Window window, FrameworkElement target, string expectedName)
    {
        Label label = Descendants(window).OfType<Label>().Single(candidate =>
            candidate.IsVisible && ReferenceEquals(candidate.Target, target));
        Check(ReferenceCaption(label.Content).TrimEnd(':') == expectedName,
            "Visible label identifies the actual current/draft field: " + expectedName);
        Check(ReferenceEquals(AutomationProperties.GetLabeledBy(target), label),
            "UIA label relation identifies the same visible field: " + expectedName);
        Check(target is TextBlock readout ? GetAccessiblePeer(target).GetName() == readout.Text
            : GetAccessiblePeer(target).GetName().StartsWith(expectedName, StringComparison.Ordinal),
            "UIA exposes the static value or labelled document name without losing the native label relation: " + expectedName);
    }

    private static void VerifyReferenceEditorName(Window window, FrameworkElement target, string expectedName, string? expectedAccessibleName = null)
    {
        Check(GetAccessiblePeer(target).GetName().StartsWith(expectedAccessibleName ?? expectedName, StringComparison.Ordinal),
            "Reference-style editor retains its explicit accessible setting name: " + expectedName);
        Label[] labels = Descendants(window).OfType<Label>()
            .Where(label => label.IsVisible && ReferenceEquals(label.Target, target)).ToArray();
        Check(labels.Length == 1, "Exactly one visible caption labels the current editor: " + expectedName);
        Check(ReferenceCaption(labels[0].Content).TrimEnd(':') == expectedName,
            "Visible editor caption agrees with the accessible name in every value presentation mode");
    }

    private static void VerifyReferenceSelection(System.Windows.Controls.Primitives.Selector selector, string expectedName)
    {
        var selection = RequireAccessiblePattern<ISelectionProvider>(selector, PatternInterface.Selection);
        IRawElementProviderSimple[] selected = selection.GetSelection();
        Check(selected.Length == 1
            && Equals(selected[0].GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id), expectedName),
            "Selector announces its readable item caption, not the CLR record: " + expectedName);
    }

    private static string ReferenceCaption(object content) => (content switch
    {
        string text => text,
        AccessText access => access.Text,
        TextBlock text => text.Text,
        _ => throw new InvalidOperationException("Unexpected reference caption type: " + content.GetType().Name)
    }).Replace("_", string.Empty, StringComparison.Ordinal).Trim();
}
