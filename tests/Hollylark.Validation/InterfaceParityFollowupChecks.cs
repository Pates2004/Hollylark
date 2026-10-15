using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyInterfaceParityFollowupApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyInterfaceParityFollowup(); }
        finally
        {
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void VerifyInterfaceParityFollowup()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                using var transport = new FakeTransport();
                MainWindow window = CreatePresentationWindow(transport, "interface-captions-" + language, new MemoryDesktop());
                try
                {
                    RunTask(window.ConnectAsync());
                    FindById<TabControl>(window, "MainTabs").SelectedIndex = 0;
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    foreach (int scale in new[] { 100, 125, 150, 175, 200 })
                    foreach (ValueDisplayMode display in Enum.GetValues<ValueDisplayMode>())
                    foreach (GainControlMode gain in Enum.GetValues<GainControlMode>())
                    {
                        SavePreferences(window, window.CurrentPreferences with
                        {
                            Language = language, TextScale = scale, ValueDisplayMode = display,
                            GainControlMode = gain, ShowHints = false
                        });
                        foreach (SettingRowViewModel setting in AllSettings(window))
                        {
                            string targetId = setting.Id == "Gain" ? gain switch
                            {
                                GainControlMode.Buttons => setting.DecreaseId,
                                GainControlMode.Slider => setting.SliderId,
                                _ => setting.ChoiceId
                            } : setting.ChoiceId;
                            FrameworkElement target = FindById<FrameworkElement>(window, targetId);
                            target.BringIntoView();
                            Pump();
                            VerifyReferenceEditorName(window, target, setting.ChoiceName,
                                setting.Id == "Gain" && gain == GainControlMode.Buttons ? setting.DecreaseName
                                    : setting.Id == "Gain" && gain == GainControlMode.Slider ? Localization.Text("Setting.Gain.SliderName") : null);
                            Label label = Descendants(window).OfType<Label>().Single(candidate =>
                                candidate.IsVisible && ReferenceEquals(candidate.Target, target));
                            Check(label.ActualWidth > 0 && label.ActualWidth + 0.5 >= label.DesiredSize.Width,
                                $"Target caption fits at minimum window size: {language}/{scale}/{display}/{gain}/{setting.Id}");
                            Check(display == ValueDisplayMode.None ? label.Parent is Grid : label.Parent is WrapPanel,
                                "Target caption uses the reference value-dependent column placement");
                        }
                        Check(Descendants(window).OfType<Label>().Where(label => label.IsVisible)
                                .All(label => label.Target is null || label.Target.IsVisible),
                            "Every visible target caption points to a visible control");
                        if (gain == GainControlMode.Choice && scale is 100 or 200)
                        {
                            FindById<ComboBox>(window, "Gain.Choice").BringIntoView();
                            ((ScrollViewer)window.FindName("ControlsScrollViewer")).ScrollToLeftEnd();
                            Pump();
                            RenderWindow(window, $"interface-captions-{language}-{scale}-{display}.png");
                        }
                    }
                    Window settings = window.OpenSettingsWindow();
                    MoveOffscreen(settings);
                    Pump();
                    Button save = FindById<Button>(settings, "SaveSettingsButton");
                    Check(ReferenceCaption(save.Content) == "OK" && GetAccessiblePeer(save).GetName().StartsWith("OK", StringComparison.Ordinal)
                            && save.IsDefault,
                        "Visible and accessible OK names agree and retain default-button semantics: " + language);
                    settings.Close();
                    Pump();
                    Check(CountMockWrites(transport) == 0, "Caption, layout and name checks never write receiver settings");
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }
}
