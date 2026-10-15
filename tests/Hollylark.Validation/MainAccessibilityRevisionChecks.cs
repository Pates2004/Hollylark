using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyMainAccessibilityRevisionChecks()
    {
        VerifyGuardedSliderInitialization();
        Check(new UiPreferences().ConfirmExit, "Fresh preferences enable exit confirmation");
        Check(new UiPreferences().ValueDisplayMode == ValueDisplayMode.StaticText,
            "Fresh preferences use native static values, never read-only edit fields");
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "revision-defaults"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"SchemaVersion\":3,\"ConfirmExit\":false,\"ValueDisplayMode\":\"ReadOnlyFields\"}");
        UiPreferences legacy = store.Load(out string? error);
        Check(error is null && !legacy.ConfirmExit && legacy.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields,
            "An explicit saved exit preference and legacy display token remain readable");
        File.WriteAllText(store.FilePath, "{\"SchemaVersion\":3}");
        Check(store.Load(out error).ConfirmExit && error is null, "Missing exit preference receives the safe default");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                using var transport = new FakeTransport();
                MainWindow window = CreatePresentationWindow(transport, "main-accessibility-revision-" + language, new MemoryDesktop());
                try
                {
                    Window information = window.OpenInformationWindow();
                    string startupLog = FindById<TextBox>(information, "DiagnosticLogReadout").Text;
                    Check(new[] { "Main.Change.TransmitterRequired", "Target.SelectedUnavailable", "Target.SharedParameters", "Target.GroupUnavailable" }
                        .All(key => !startupLog.Contains(Localization.Text(key), StringComparison.Ordinal)),
                        "Startup initializes controls without a false microphone-edit refusal: " + language);
                    information.Close();
                    Pump();
                    RunTask(window.ConnectAsync());
                    SavePreferences(window, window.CurrentPreferences with { ShowHints = false, ShowOperationMessages = false });
                    VerifySelectedMicrophoneRevision(window);
                    VerifyRevisionMainThemeBrushes(window);
                    VerifyOfflinePrechangeChecks(window, transport);
                    foreach (ValueDisplayMode display in Enum.GetValues<ValueDisplayMode>())
                    foreach (GainControlMode gainMode in Enum.GetValues<GainControlMode>())
                    {
                        SavePreferences(window, window.CurrentPreferences with { ValueDisplayMode = display, GainControlMode = gainMode });
                        Check(Descendants(window).OfType<TextBox>().Where(field => field.IsVisible).All(field => field.IsReadOnly),
                            "Additional informational fields are never editable: " + display + "/" + gainMode);
                        foreach (SettingRowViewModel setting in AllSettings(window))
                        {
                            Check(setting.ChoiceName == setting.DisplayName
                                && GetAccessiblePeer(FindById<ComboBox>(window, setting.ChoiceId)).GetName() == setting.DisplayName,
                                "Editor names use only the setting name without a redundant new-value suffix: " + setting.Id);
                            Check(FindById<TextBlock>(window, setting.StaticCurrentId).IsVisible == (display == ValueDisplayMode.StaticText),
                                "Additional current values obey display preference: " + setting.Id + "/" + display);
                            TextBox currentField = FindById<TextBox>(window, setting.CurrentId);
                            Check(currentField.IsVisible == (display == ValueDisplayMode.ReadOnlyFields)
                                && currentField.Text == setting.ConfirmedValue && currentField.IsReadOnly,
                                "Read-only mode shows the actual confirmed value only: " + setting.Id + "/" + display);
                            Check(GetAccessiblePeer(currentField).GetName() == setting.CurrentName,
                                "Confirmed field has its essential setting name: " + setting.Id);
                            Check(setting.ToString() == setting.DisplayName, "Setting items never default to CLR names: " + setting.Id);
                            Check(AutomationProperties.GetHelpText(FindById<ComboBox>(window, setting.ChoiceId)).Length == 0,
                                "Optional choice guidance is absent with hints disabled: " + setting.Id);
                        }
                        Check(FindById<TextBlock>(window, "Gain.DraftValue").IsVisible
                            == (display == ValueDisplayMode.StaticText && gainMode == GainControlMode.Buttons),
                            "Only button mode may show an optional draft reading: " + display + "/" + gainMode);
                        TextBox draftField = FindById<TextBox>(window, "Gain.DraftField");
                        Check(draftField.IsVisible == (display == ValueDisplayMode.ReadOnlyFields && gainMode == GainControlMode.Buttons)
                            && draftField.Text == window.AudioSettings[0].DraftValueText,
                            "Button draft field follows read-only presentation: " + display + "/" + gainMode);
                        Check(GetAccessiblePeer(draftField).GetName() == window.AudioSettings[0].DisplayName,
                            "Optional gain field has the plain setting name, not a new-value instruction");
                        Check(GetAccessiblePeer(FindById<TextBlock>(window, "Gain.DraftValue")).GetName()
                            == window.AudioSettings.Single(setting => setting.Id == "Gain").DraftValueText,
                            "The optional button-mode draft reading exposes its actual value, not only a caption");
                        Slider slider = FindById<Slider>(window, "Gain.Slider");
                        Check(GetAccessiblePeer(slider).GetName() == Localization.Text("Setting.Gain.SliderName")
                            && AutomationProperties.GetHelpText(slider).Length == 0, "Slider has a short name and no hidden range guidance");
                    }
                    Check(AutomationProperties.GetHelpText(FindById<Button>(window, "RefreshButton")).Length == 0
                        && GetAccessiblePeer(FindById<Button>(window, "RefreshButton")).GetName() == Localization.Text("Main.Refresh.Name"),
                        "Refresh has a short name and obeys disabled hints");
                    Check(!((TextBlock)window.FindName("AudioHeading")).IsVisible
                        && !((TextBlock)window.FindName("DeviceHeading")).IsVisible,
                        "Optional main headings disappear with hints disabled");
                    VerifyNoClrItemNames(GetAccessiblePeer(window));
                    transport.SetTransmitterConnected(0, false);
                    transport.SetTransmitterConnected(1, false);
                    int writes = CountMockWrites(transport);
                    SettingRowViewModel staleGain = window.AudioSettings.Single(setting => setting.Id == "Gain");
                    staleGain.SelectedIndex = (staleGain.SelectedIndex + 1) % staleGain.Options.Count;
                    RunTask(window.ApplySettingAsync(staleGain));
                    Check(window.IsConnected && !window.IsBusy && !staleGain.IsDirty
                        && window.Transmitters.All(transmitter => !transmitter.IsConnected)
                        && window.ResultText == Localization.Text("Main.Change.TransmitterRequired")
                        && CountMockWrites(transport) == writes,
                        "A transmitter lost after UI readback produces a localized refusal without closing the receiver session");
                    foreach (SettingRowViewModel setting in AllSettings(window))
                    {
                        int original = setting.SelectedIndex;
                        setting.SelectedIndex = (original + 1) % setting.Options.Count;
                        Check(!setting.IsDirty && setting.SelectedIndex == original && !setting.CanApply,
                            "Offline changes are rejected and drafts restored: " + setting.Id);
                        RunTask(window.ApplySettingAsync(setting));
                        Check(window.ResultText == Localization.Text("Main.Change.TransmitterRequired"),
                            "Every offline apply provides actionable feedback: " + setting.Id);
                    }
                    RunTask(window.ToggleMuteAsync(0));
                    Check(window.ResultText == Localization.Text("Main.Change.TransmitterRequired")
                        && FindById<TextBlock>(window, "ResultReadout").IsVisible,
                        "Offline errors remain available even when routine messages and hints are disabled");
                    Check(CountMockWrites(transport) == writes, "Offline UI attempts never send settings to the receiver");
                    ComboBox lights = FindById<ComboBox>(window, "Lights.Choice");
                    int confirmedLights = lights.SelectedIndex;
                    lights.SelectedIndex = confirmedLights == 0 ? 1 : 0;
                    Pump();
                    Check(lights.SelectedIndex == confirmedLights && !window.DeviceSettings.Single(setting => setting.Id == "Lights").IsDirty,
                        "An actual offline ComboBox interaction restores the confirmed selection");
                    Slider gainSlider = FindById<Slider>(window, "Gain.Slider");
                    double confirmedGain = gainSlider.Value;
                    gainSlider.Value = confirmedGain == 1 ? 2 : 1;
                    Pump();
                    Check(gainSlider.Value == confirmedGain && !staleGain.IsDirty,
                        "An actual offline Slider interaction restores the confirmed value");
                    Check(CountMockWrites(transport) == writes, "Rejected ComboBox and Slider interactions send no device writes");
                    SavePreferences(window, window.CurrentPreferences with { ShowHints = true });
                    Check(AutomationProperties.GetHelpText(FindById<Slider>(window, "Gain.Slider")).Contains(Localization.Text("Setting.Gain.RangeHint")),
                        "Re-enabling hints restores optional range guidance without a device write");
                }
                finally { window.RequestExit(); Pump(); }
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifySelectedMicrophoneRevision(MainWindow window)
    {
        ListBox list = FindById<ListBox>(window, "TransmitterList");
        Check(list.GetType() == typeof(ListBox) && ReferenceEquals(list.SelectedItem, window.TransmitterTargets.Last()),
            "The microphone selector is a native ListBox initially targeting all connected microphones");
        list.SelectedItem = window.Transmitters[0];
        list.UpdateLayout();
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        first.Focus();
        RaiseLocalKey(first, Key.Down);
        Pump();
        Check(ReferenceEquals(list.SelectedItem, window.Transmitters[1]), "Native Down selects the second microphone");
        Check(GetAccessiblePeer((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).GetName() == window.Transmitters[1].Summary,
            "Selected microphone UIA name contains its actual summary, never its CLR type");
        string[] actions = Descendants(window).OfType<Button>().Where(button => button.IsVisible)
            .Select(AutomationProperties.GetAutomationId).Where(identifier => identifier.StartsWith("TX") && identifier.EndsWith(".Mute")).ToArray();
        Check(actions.SequenceEqual(new[] { "TX2.Mute" }), "Only the selected microphone action exists in the visible Tab surface");
        RunTask(Poll(window));
        Check(ReferenceEquals(list.SelectedItem, window.Transmitters[1]), "Polling retains microphone selection");
        SavePreferences(window, window.CurrentPreferences with { HiddenRows = ["TX2"] });
        Check(ReferenceEquals(list.SelectedItem, window.TransmitterTargets.Last()) && FindById<Button>(window, "AllTX.Mute").IsVisible,
            "Hiding the selected microphone moves selection and options to the visible group");
        SavePreferences(window, window.CurrentPreferences with { HiddenRows = [] });
    }

    private static void VerifyNoClrItemNames(AutomationPeer peer)
    {
        Check(!peer.GetName().Contains("LarkControl.Windows.", StringComparison.Ordinal),
            "UIA peer name is user-facing: " + peer.GetAutomationId());
        Check(peer.GetAutomationControlType() != AutomationControlType.DataItem,
            "The main UI uses native controls and list items, never generic data items: " + peer.GetAutomationId());
        foreach (AutomationPeer child in peer.GetChildren() ?? []) VerifyNoClrItemNames(child);
    }
}
