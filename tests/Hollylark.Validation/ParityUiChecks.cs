using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyParityUi()
    {
        VerifyOfficialSupportLinks();
        VerifyFocusableStaticReadout();
        VerifyParityNotificationRouting();
        VerifyParityOperationFeedback();
        VerifySettingsOperationFeedback();
        VerifySupportWindows();
        VerifyProfileNoiseAvailability();
        VerifySettingsProfileNoiseFeedback();
    }

    private static void VerifyOfficialSupportLinks()
    {
        Check(OfficialSupportLinks.Manual.Scheme == Uri.UriSchemeHttps
            && OfficialSupportLinks.Manual.Host == "download.hollyland.com"
            && OfficialSupportLinks.Manual.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase),
            "Manual link targets the official HTTPS PDF");
        Check(OfficialSupportLinks.AndroidApplication.Scheme == Uri.UriSchemeHttps
            && OfficialSupportLinks.AndroidApplication.Host == "play.google.com"
            && OfficialSupportLinks.AndroidApplication.Query == "?id=com.hollyland.larkc1",
            "App link targets Hollyland's official HollyAudio package");
        var opened = new List<Uri>();
        foreach (Uri address in new[] { OfficialSupportLinks.Manual, OfficialSupportLinks.AndroidApplication })
        {
            SupportLinkResult result = OfficialSupportLinks.CopyAddress(address, opened.Add);
            Check(!result.IsError && opened.Last() == address, "Support action copies exactly the official address");
            Check(result.Message.Contains(address.AbsoluteUri, StringComparison.Ordinal), "Support result exposes a copyable address");
            SupportLinkResult failure = OfficialSupportLinks.CopyAddress(address, _ => throw new Win32Exception(2));
            Check(failure.IsError && failure.Message.Contains(address.AbsoluteUri, StringComparison.Ordinal),
                "Clipboard failure preserves a selectable official address");
        }
        Check(opened.Count == 2, "Support link tests never touch the real clipboard or launch a browser");
    }

    private static void VerifyFocusableStaticReadout()
    {
        var text = new TextBlock
        {
            Text = "Wzmocnienie: 4",
            Style = (Style)UiSupport.CreateResources()["FocusableReadoutStyle"]
        };
        text.Measure(new Size(320, 200));
        text.Arrange(new Rect(0, 0, 320, 200));
        Pump();
        Check(text.Focusable && KeyboardNavigation.GetIsTabStop(text), "Static values participate in keyboard navigation");
        Check(text.FocusVisualStyle is not null, "Static values expose a visible keyboard focus style");
        Check(AutomationProperties.GetName(text) == text.Text, "Static value accessible name includes its current text");
        UsageHints.SetEnabled(text, false);
        Check(text.Visibility == Visibility.Visible && text.Focusable && AutomationProperties.GetName(text) == text.Text,
            "Disabling optional hints preserves the static value and its accessible name");
        text.Text = "Wzmocnienie: 5";
        Pump();
        Check(AutomationProperties.GetName(text) == text.Text, "Static value accessible name tracks subsequent changes");
    }

    private static void VerifyParityNotificationRouting()
    {
        var main = new Window();
        var help = new HelpWindow(_ => { });
        var firmware = new FirmwareWindow(_ => { });
        try
        {
            AuxiliaryWindow[] sections = [help, firmware];
            Check(ReferenceEquals(UiNotificationRouting.SelectTarget(main, sections, window => ReferenceEquals(window, help)), help),
                "State notification targets the active help window");
            Check(ReferenceEquals(UiNotificationRouting.SelectTarget(main, sections, window => ReferenceEquals(window, firmware)), firmware),
                "State notification targets a new auxiliary window without a fixed window list");
            Check(ReferenceEquals(UiNotificationRouting.SelectTarget(main, sections, window => ReferenceEquals(window, main)), main),
                "State notification falls back to the active main window");
            Check(ReferenceEquals(UiNotificationRouting.SelectTarget(main, sections, _ => true), help),
                "Active auxiliary window has routing priority");
            Check(UiNotificationRouting.SelectTarget(main, sections, _ => false) is null,
                "No active application window means no unsolicited background announcement");
            Check(UiNotificationRouting.SelectTarget(main, sections) is null, "Unshown windows are not notification targets");
            Check(ReferenceEquals(UiNotificationRouting.SelectStateTarget(main, sections, _ => false), main),
                "Opt-in device-state notifications retain a background provider without activating a window");
            Check(ReferenceEquals(UiNotificationRouting.SelectStateTarget(main, sections, window => ReferenceEquals(window, help)), help),
                "Device-state notifications prefer the active auxiliary provider when available");
        }
        finally
        {
            firmware.Close();
            help.Close();
            main.Close();
        }
    }

    private static void VerifyParityOperationFeedback()
    {
        var window = new MainWindow(() => [], _ => throw new InvalidOperationException("Unexpected device access"),
            Path.Combine(ReportDirectory, "parity-feedback"), new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo preferencesField = typeof(MainWindow).GetField("preferences", flags)!;
        FieldInfo importantField = typeof(MainWindow).GetField("resultIsImportant", flags)!;
        MethodInfo update = typeof(MainWindow).GetMethod("UpdateResultPresentation", flags)!;
        try
        {
            TextBlock result = (TextBlock)window.FindName("ResultReadout");
            Check(result.Text.Length == 0 && result.Visibility == Visibility.Collapsed,
                "An empty main result never creates an instructional footer or focus stop");
            result.Text = "Simulated action result";
            foreach (bool showOperations in new[] { false, true })
            foreach (bool announceChanges in new[] { false, true })
            foreach (bool important in new[] { false, true })
            {
                preferencesField.SetValue(window, new UiPreferences
                {
                    ShowHints = false, ShowOperationMessages = showOperations, AnnounceStateChanges = announceChanges
                });
                importantField.SetValue(window, important);
                update.Invoke(window, null);
                bool expectedVisible = important || showOperations;
                Check((result.Visibility == Visibility.Visible) == expectedVisible, "Operation visibility is independent of state announcements");
                Check(AutomationProperties.GetLiveSetting(result) == (expectedVisible ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off),
                    "Visible operation results and important errors retain polite announcement metadata");
                Check(result.Focusable && GetAccessiblePeer(result).GetPattern(PatternInterface.Value) is null,
                    "Operation and error messages are focusable static text rather than edit fields");
            }
        }
        finally { window.Close(); }
    }

    private static void VerifySettingsOperationFeedback()
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile profile = AudioProfile.FromStatus("Profil komunikatów", status);
        foreach (bool showOperations in new[] { false, true })
        foreach (bool announceChanges in new[] { false, true })
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int applications = 0;
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory,
                $"settings-operation-feedback-{showOperations}-{announceChanges}"));
            var preferences = new UiPreferences
            {
                ShowOperationMessages = showOperations, AnnounceStateChanges = announceChanges,
                AudioProfiles = [profile]
            };
            var settings = new SettingsWindow(preferences, store,
                (_, _) => throw new InvalidOperationException("Feedback tests must not save preferences."),
                readStatus: () => status,
                applyProfile: _ => ++applications == 1 ? completion.Task
                    : Task.FromException<string?>(new IOException("Simulated uncertain profile failure.")),
                confirm: (_, title) => title != LarkControl.Windows.Localization.Text("Settings.Save.Title"));
            MoveOffscreen(settings);
            settings.Show();
            Pump();
            var result = (TextBlock)settings.FindName("SectionResultReadout");
            var cancel = (Button)settings.FindName("CancelSettingsButton");
            try
            {
                SelectSettingsCategory(settings, "ProfilesSettingsTab");
                ClickSettings(settings, "ApplyAudioProfileButton");
                Check(applications == 1 && !cancel.IsEnabled, "The actual settings profile action awaits its simulated result");
                Check(result.IsVisible && result.Text.Contains("Trwa stosowanie profilu")
                    && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "Explicit profile progress remains announced independently of automatic state announcements");
                completion.SetResult(null);
                PumpUntil(() => cancel.IsEnabled);
                CheckSettingsRoutineFeedback(result, showOperations, "Zastosowano profil w odbiorniku");

                ClickSettings(settings, "DeleteAudioProfileButton");
                var profiles = (ListBox)settings.FindName("AudioProfilesList");
                Check(profiles.Items.Count == 0, "The actual delete action removes the selected profile draft");
                CheckSettingsRoutineFeedback(result, showOperations, "Usunięto profil ze szkicu");

                ((TextBox)settings.FindName("AudioProfileNameTextBox")).Text = "Nowy profil komunikatów";
                ClickSettings(settings, "SaveAudioProfileButton");
                Check(profiles.Items.Count == 1, "The actual capture action adds the simulated confirmed profile");
                CheckSettingsRoutineFeedback(result, showOperations, "Dodano profil do szkicu");

                ClickSettings(settings, "ApplyAudioProfileButton");
                Check(applications == 2 && cancel.IsEnabled && result.IsVisible
                    && result.Text.Contains("Część zmian mogła trafić do odbiornika")
                    && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "An uncertain exception retains the important partial-application warning under every feedback preference");
                Check(!File.Exists(store.FilePath), "Feedback actions do not persist their profile or preference drafts");
            }
            finally
            {
                completion.TrySetResult(null);
                PumpUntil(() => cancel.IsEnabled);
                settings.Close();
                Pump();
            }
        }
    }

    private static void CheckSettingsRoutineFeedback(TextBlock result, bool visible, string expectedText)
    {
        Check(result.IsVisible == visible, "Settings operation visibility follows the operation-message preference: " + expectedText);
        if (!visible) return;
        Check(result.Text.Contains(expectedText) && result.Focusable
            && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite
            && AutomationProperties.GetName(result).Contains(expectedText),
            "Visible settings results retain accessible text and polite announcements independently of state announcements: " + expectedText);
    }

    private static void VerifySettingsProfileNoiseFeedback()
    {
        AudioProfile profile = AudioProfile.FromStatus("Profil blokady szumu", A1Status.Parse(ValidStatus))
            with { Gain = 0, NoiseEnabled = false };
        foreach (bool partiallyApplied in new[] { false, true })
        {
            using var transport = new ProfileNoiseTransport();
            MainWindow owner = CreateProfileNoiseWindow(transport, "settings-profile-noise-" + partiallyApplied);
            try
            {
                SavePreferences(owner, owner.CurrentPreferences with
                {
                    AudioProfiles = [profile], ShowOperationMessages = false, AnnounceStateChanges = false
                });
                SettingsWindow settings = OpenTransactionSettings(owner);
                SelectSettingsCategory(settings, "ProfilesSettingsTab");
                if (partiallyApplied) transport.MuteOnStatusRead(3);
                else transport.AllMuted = true;
                ClickSettings(settings, "ApplyAudioProfileButton");
                PumpUntil(() => !owner.IsBusy && ((Button)settings.FindName("CancelSettingsButton")).IsEnabled);
                var result = (TextBlock)settings.FindName("SectionResultReadout");
                Check(result.IsVisible && result.Text == owner.ResultText
                    && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "The actual settings path preserves the controller's important refusal without appending speculation");
                if (partiallyApplied)
                {
                    Check(result.Text.Contains("Część wcześniejszych ustawień została zmieniona.")
                        && transport.SettingCommands.SequenceEqual(new[] { A1Command.SetGain }),
                        "A mid-profile noise restriction still reports the confirmed partial change without a noise or unmute write");
                }
                else
                {
                    Check(result.Text.Contains("Nie wysłano ustawień")
                        && !result.Text.Contains("częściow", StringComparison.OrdinalIgnoreCase)
                        && transport.SettingCommands.Length == 0,
                        "All-muted preflight refusal reaches the settings UI as a zero-write outcome, never a partial application");
                }
                Check(owner.IsConnected, "A settings profile restriction leaves the simulated receiver connected");
            }
            finally { owner.RequestExit(); Pump(); }
        }
    }

    private static void VerifyProfileNoiseAvailability()
    {
        var original = AudioProfile.FromStatus("Noise guard", A1Status.Parse(ValidStatus));
        using (var transport = new ProfileNoiseTransport())
        {
            MainWindow window = CreateProfileNoiseWindow(transport, "profile-noise-preflight");
            try
            {
                Check(window.Transmitters.Any(transmitter => transmitter.IsConnected && !transmitter.IsMuted),
                    "Noise preflight fixture starts with stale but permissive UI state");
                transport.AllMuted = true;
                Task<string?> applying = window.ApplyAudioProfileAsync(original with { Gain = 0, NoiseEnabled = false });
                RunTask(applying);
                Check(applying.Result is not null && applying.Result.Contains("Nie wysłano ustawień"),
                    "Fresh all-muted state rejects the entire profile before its first write");
                Check(transport.SettingCommands.Length == 0, "Noise preflight refusal sends neither audio changes nor unmute");
                Check(window.IsConnected, "A profile availability refusal does not discard a healthy receiver session");
            }
            finally { window.RequestExit(); }
        }
        using (var transport = new ProfileNoiseTransport())
        {
            MainWindow window = CreateProfileNoiseWindow(transport, "profile-noise-recheck");
            try
            {
                transport.MuteOnStatusRead(3);
                Task<string?> applying = window.ApplyAudioProfileAsync(original with { Gain = 0, NoiseEnabled = false });
                RunTask(applying);
                Check(applying.Result is not null && applying.Result.Contains("Część"),
                    "Mute appearing after the gain readback reports an honestly partial profile");
                Check(transport.SettingCommands.SequenceEqual(new[] { A1Command.SetGain }),
                    "A fresh read immediately before the noise write blocks noise without unmuting");
                Check(transport.StatusReads >= 3 && window.IsConnected,
                    "Pre-noise restriction uses a new read and retains the confirmed partial state");
            }
            finally { window.RequestExit(); }
        }
        using (var transport = new ProfileNoiseTransport())
        {
            MainWindow window = CreateProfileNoiseWindow(transport, "profile-noise-unchanged");
            try
            {
                transport.AllMuted = true;
                Task<string?> applying = window.ApplyAudioProfileAsync(original with { Gain = 0 });
                RunTask(applying);
                Check(applying.Result is null, "All-muted profile remains usable when its noise value already matches");
                Check(transport.SettingCommands.SequenceEqual(new[] { A1Command.SetGain }),
                    "An unchanged noise setting does not cause noise or mute writes");
            }
            finally { window.RequestExit(); }
        }
    }

    private static MainWindow CreateProfileNoiseWindow(ProfileNoiseTransport transport, string name)
    {
        var receiver = new ReceiverInfo("Simulated LARK A1", "simulated://profile-noise", 0, 64);
        var window = new MainWindow(() => [receiver], _ => transport, Path.Combine(ReportDirectory, name),
            new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        RunTask(window.ConnectAsync());
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        timer.Stop();
        return window;
    }

    private sealed class ProfileNoiseTransport : IA1Transport
    {
        private readonly FakeTransport inner = new();
        private int muteAtStatusRead = int.MaxValue;
        public bool AllMuted { get; set; }
        public int StatusReads { get; private set; }
        public A1Command[] SettingCommands => inner.Commands.Where(command => command.ToString().StartsWith("Set", StringComparison.Ordinal)).ToArray();
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed
        {
            add => inner.Failed += value;
            remove => inner.Failed -= value;
        }

        public ProfileNoiseTransport()
        {
            inner.ReportReceived += report =>
            {
                if (A1Protocol.TryDecode(report, out A1Frame? frame) && frame is { Command: A1Command.GetStatus })
                {
                    StatusReads++;
                    if (AllMuted || StatusReads >= muteAtStatusRead)
                    {
                        byte[] payload = (byte[])frame.Payload.Clone();
                        payload[4] = payload[5] = 1;
                        report = Reply(A1Command.GetStatus, payload);
                    }
                }
                ReportReceived?.Invoke(report);
            };
        }

        public void MuteOnStatusRead(int number)
        {
            StatusReads = 0;
            muteAtStatusRead = number;
        }

        public void SendFeature(byte[] report) => inner.SendFeature(report);
        public void Dispose() => inner.Dispose();
    }

    private static void VerifySupportWindows()
    {
        var opened = new List<Uri>();
        var help = new HelpWindow(opened.Add);
        var firmware = new FirmwareWindow(opened.Add);
        var failed = new FirmwareWindow(_ => throw new Win32Exception(2));
        try
        {
            help.ApplyPreferences(new UiPreferences { ShowHints = false });
            firmware.ApplyPreferences(new UiPreferences { ShowHints = false, Theme = "Dark", TextScale = 200 });
            Check(firmware.ThemeMode == ThemeMode.Dark && firmware.FontSize == SystemFonts.MessageFontSize * 2,
                "Firmware window participates in shared theme and text scaling");
            foreach ((AuxiliaryWindow Window, string Identifier) entry in new[]
            {
                ((AuxiliaryWindow)help, "OpenManualButton"), ((AuxiliaryWindow)help, "OpenOfficialAppButton"),
                ((AuxiliaryWindow)firmware, "OpenFirmwareManualButton"), ((AuxiliaryWindow)firmware, "OpenFirmwareAppButton")
            })
            {
                Button button = (Button)entry.Window.FindName(entry.Identifier);
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), "Support button retains an essential accessible name");
                Check(AutomationProperties.GetHelpText(button) == string.Empty, "Disabled hints also remove optional support-button HelpText");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Check(opened.SequenceEqual(new[] { OfficialSupportLinks.Manual, OfficialSupportLinks.AndroidApplication,
                OfficialSupportLinks.Manual, OfficialSupportLinks.AndroidApplication }), "Both windows wire their buttons to the correct official links");
            string helpText = ((TextBox)help.FindName("HelpReadout")).Text;
            Check(helpText.Contains("jawnego wyboru") && !helpText.Contains("nie mierzy jego poziomu"),
                "Help describes explicitly selected metering rather than the old missing-meter limitation");
            string licenses = ((TextBox)help.FindName("LicensesReadout")).Text;
            Check(licenses.Contains("NAudio") && licenses.Contains("MIT") && !licenses.Contains("Brak osadzonego dokumentu"),
                "Help reads the actual embedded NAudio license and existing notices");
            Check(!licenses.Contains("Hollylark 0.2.0"), "Third-party heading does not advertise an obsolete release");
            TextBox summary = (TextBox)firmware.FindName("FirmwareDeviceReadout");
            Check(summary.Text.Contains("Brak aktualnego odczytu"), "Firmware window does not invent an initial version");
            firmware.UpdateContent("RX: 1.0.2.8\nTX1: 1.0.2.8");
            summary.Select(3, 7);
            firmware.UpdateContent("RX: 1.0.2.8\nTX1: 1.0.2.9");
            Check(summary.Text.Contains("TX1: 1.0.2.9") && summary.SelectionStart == 3 && summary.SelectionLength == 7,
                "Firmware metadata refresh preserves readout selection");
            firmware.UpdateContent(string.Empty);
            Check(summary.Text.Contains("Brak aktualnego odczytu"), "Firmware disconnect removes a previous version display");
            TextBox warning = (TextBox)firmware.FindName("FirmwareSafetyReadout");
            Check(warning.IsReadOnly && warning.Visibility == Visibility.Visible && warning.Text.Contains("nie aktualizatorem"),
                "Disabling hints never hides the explicit no-updater safety warning");
            failed.ApplyPreferences(new UiPreferences { ShowHints = false, AnnounceStateChanges = false, ShowOperationMessages = false });
            ((Button)failed.FindName("OpenFirmwareManualButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextBlock failure = (TextBlock)failed.FindName("SectionResultReadout");
            Check(failure.Focusable && GetAccessiblePeer(failure).GetPattern(PatternInterface.Value) is null && failure.Visibility == Visibility.Visible
                && AutomationProperties.GetLiveSetting(failure) == AutomationLiveSetting.Polite,
                "Clipboard failure remains a visible read-only error even with optional feedback disabled");
        }
        finally
        {
            failed.Close();
            firmware.Close();
            help.Close();
        }
    }
}
