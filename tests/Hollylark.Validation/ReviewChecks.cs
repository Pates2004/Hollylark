using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static readonly string[] ForeignDeviceTerms =
        ["Yamaha", "Yamahama", "Soundbar", "Subwoofer", "HDMI", "Clear Voice", "Bass Extension", "Surround"];

    private static bool ContainsForeignDeviceTerm(string text) =>
        ForeignDeviceTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void VerifyLarkAssemblyContents()
    {
        foreach (System.Reflection.Assembly assembly in new[] { typeof(MainWindow).Assembly, typeof(A1Controller).Assembly })
        {
            string[] identities = assembly.GetTypes().Select(type => type.FullName ?? type.Name)
                .Concat(assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty))
                .Concat(assembly.GetManifestResourceNames()).ToArray();
            Check(identities.All(identity => !ContainsForeignDeviceTerm(identity)),
                "Production assembly contains no Yamaha-specific types, dependencies or resource names: " + assembly.GetName().Name);
        }
    }

    private static void VerifyLarkOnlyPresentation(Window window)
    {
        Check(!ContainsForeignDeviceTerm(window.Title), "Window title contains only the intended device branding: " + window.Title);
        foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>().Where(element => element.IsVisible))
        {
            string? text = element switch
            {
                TextBlock block => block.Text,
                TextBox box => box.Text,
                AccessText access => access.Text,
                HeaderedContentControl header => header.Header as string,
                HeaderedItemsControl header => header.Header as string,
                ContentControl content => content.Content as string,
                _ => null
            };
            string name = AutomationProperties.GetName(element);
            Check(!ContainsForeignDeviceTerm(name) && (text is null || !ContainsForeignDeviceTerm(text)),
                "Visible control has no foreign device feature or label: " + element.GetType().Name + " " + AutomationProperties.GetAutomationId(element));
            Check(!(text ?? string.Empty).Contains("System.Windows.Controls.", StringComparison.Ordinal)
                && !name.Contains("LarkControl.Windows.", StringComparison.Ordinal),
                "Visible captions and accessible names do not leak CLR object identifiers");
        }
    }

    private static void VerifyAuxiliaryOperationPreference()
    {
        int copied = 0;
        bool failCopy = false;
        Action<Uri> copy = _ =>
        {
            if (failCopy) throw new InvalidOperationException("Simulated clipboard failure");
            copied++;
        };
        AuxiliaryWindow[] windows = [new HelpWindow(copy), new FirmwareWindow(copy)];
        try
        {
            foreach (AuxiliaryWindow window in windows)
            {
                var quiet = new UiPreferences { ShowOperationMessages = false, AnnounceStateChanges = false };
                window.ApplyPreferences(quiet);
                MoveOffscreen(window); window.Show(); Pump();
                TextBlock result = FindById<TextBlock>(window, "SectionResultReadout");
                Button copyButton = Descendants(window).OfType<Button>().First(button =>
                    AutomationProperties.GetName(button).StartsWith("Kopiuj adres", StringComparison.Ordinal));
                copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(result.Visibility == Visibility.Collapsed && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "Standalone support windows respect hidden success messages, without relying on an owner");
                window.ApplyPreferences(quiet with { ShowOperationMessages = true });
                Pump();
                Check(result.IsVisible && result.Text.Contains("https://", StringComparison.Ordinal),
                    "Re-enabling feedback restores the real copied address result");
                window.ApplyPreferences(quiet);
                Check(result.Visibility == Visibility.Collapsed, "An existing support success message hides immediately when disabled");
                failCopy = true;
                copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(result.IsVisible && result.Text.Contains("https://", StringComparison.Ordinal)
                    && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "Support copy failure retains its selectable address and live error with optional messages disabled");
                failCopy = false;
            }
            Check(copied == 2, "Support feedback tests use only injected callbacks, not the real clipboard or browser");
        }
        finally { foreach (AuxiliaryWindow window in windows) window.Close(); Pump(); }
    }

    private static void VerifyEffectDisablePreservesLevel()
    {
        using var transport = new FakeTransport();
        MainWindow window = CreatePresentationWindow(transport, "effect-disable-level", new MemoryDesktop());
        try
        {
            RunTask(window.ConnectAsync());
            foreach (string identifier in new[] { "Noise", "Reverb" })
            {
                SettingRowViewModel setting = AllSettings(window).Single(candidate => candidate.Id == identifier);
                foreach (int level in new[] { 2, 1, 0 })
                {
                    setting.SelectedIndex = level + 1;
                    RunTask(window.ApplySettingAsync(setting));
                    Check(window.IsConnected && setting.SelectedIndex == level + 1 && !setting.IsDirty,
                        "Effect level is confirmed before disabling: " + identifier + " " + level);
                    setting.SelectedIndex = 0;
                    Check(setting.CreateChange().Payload.SequenceEqual(new byte[] { 0, (byte)level }),
                        "Disabling an effect sends its last confirmed level instead of resetting it: " + identifier);
                    RunTask(window.ApplySettingAsync(setting));
                    A1Status status = (A1Status)typeof(MainWindow).GetField("currentStatus",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
                    Check(identifier == "Noise" ? !status.NoiseEnabled && status.NoiseLevel == level
                        : !status.ReverbEnabled && status.ReverbLevel == level,
                        "Disabled effect readback retains the confirmed level: " + identifier);
                    Check(!setting.IsDirty && setting.SelectedIndex == 0,
                        "Disabled effect still has the correct confirmed UI state");
                    RunTask(window.RefreshAsync());
                    Check(setting.CreateChange().Payload[1] == level,
                        "Repeated disable after refresh preserves the receiver's stored effect level");
                }
            }
        }
        finally { window.Close(); Pump(); }
    }
}
