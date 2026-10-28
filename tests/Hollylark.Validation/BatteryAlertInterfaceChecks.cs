using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    // The parent owns the STA Application and isolated desktop. Every external output is injected.
    public static void VerifyBatteryAlertInterfaceChecks()
    {
        if (Application.Current is null || Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown
            || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Battery interface checks require the parent's explicit-shutdown STA WPF application.");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyBatterySettingsTransactions(language);
                VerifyBatteryInitialAndReceiverEpisodes(language);
                VerifyBatteryBatchedChannels(language);
                VerifyBatteryChannelFailures(language);
                VerifyBatteryCompletionFeedback(language);
                VerifyBatteryBackgroundAndClose(language);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static BatteryAlertOutput BatteryAllChannels() => new() { Notification = true, Speech = true, Sound = true };

    private static string BatteryText(string key, params object?[] arguments)
    {
        string text = Localization.Text(key, arguments);
        Check(!string.IsNullOrWhiteSpace(text) && text != key && !text.Contains("Battery.", StringComparison.Ordinal),
            "Battery text resolves from the active catalog: " + key + "/" + Localization.CurrentLanguage);
        return text;
    }

    private static string BatteryMessage(BatteryAlertSeverity severity, params (int Index, int Percent)[] microphones)
    {
        return BatteryText("Battery." + severity + ".Message", string.Join(". ", microphones.Select(
            microphone => BatteryText("Battery.Microphone", microphone.Index, microphone.Percent))));
    }

    private static void VerifyBatterySettingsTransactions(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "settings");
        fixture.Start();
        SettingsWindow settings = OpenTransactionSettings(fixture.Window);
        SelectSettingsCategory(settings, "BatteryAlertsSettingsTab");
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var tab = (TabItem)settings.FindName("BatteryAlertsSettingsTab");
        Check(tabs.Items.IndexOf(tab) == tabs.Items.IndexOf(settings.FindName("HotkeysSettingsTab")) + 1,
            "Battery settings follow keyboard shortcuts: " + language);
        VerifyBatteryPeer(tab, "BatteryAlertsSettingsTab", BatteryText("Battery.Settings.Name"), AutomationControlType.TabItem);
        VerifyBatteryPeer((CheckBox)settings.FindName("BatteryAlertsEnabledCheckBox"), "BatteryAlertsEnabledCheckBox",
            BatteryText("Battery.Enabled").Replace("_", ""), AutomationControlType.CheckBox);
        var low = (ComboBox)settings.FindName("BatteryLowThresholdChoice");
        var critical = (ComboBox)settings.FindName("BatteryCriticalThresholdChoice");
        foreach ((ComboBox choice, string level) in new[] { (low, "Low"), (critical, "Critical") })
        {
            VerifyBatteryPeer(choice, "Battery" + level + "ThresholdChoice", BatteryText("Battery." + level + ".Threshold"),
                AutomationControlType.ComboBox);
            Check(!choice.IsEditable && choice.SelectedValuePath == nameof(FrameworkElement.Tag)
                && choice.Items.Count >= 99 && choice.Items.Cast<object>().All(item => item is ComboBoxItem { Tag: int value } && value is >= 1 and <= 100)
                && choice.Items.Cast<ComboBoxItem>().Select(item => (int)item.Tag).Distinct().Count() == choice.Items.Count,
                "Battery threshold choices contain distinct native integer percent items: " + level);
            foreach (ComboBoxItem item in choice.Items)
                Check(item.Content as string == item.Tag + "%" && item.HasContent,
                    "Battery threshold item presents its numeric value: " + level + "/" + item.Tag);
            VerifyNativeComboKeyboard(choice);
            foreach (string channel in new[] { "Notification", "Speech", "Sound" })
            {
                string identifier = "Battery" + level + channel + "CheckBox";
                var checkBox = (CheckBox)settings.FindName(identifier);
                VerifyBatteryPeer(checkBox, identifier, BatteryText("Battery." + level + "." + channel).Replace("_", ""),
                    AutomationControlType.CheckBox);
                Check(GetAccessiblePeer(checkBox).GetPattern(PatternInterface.Toggle) is IToggleProvider,
                    "Battery output exposes native UIA toggle semantics: " + identifier);
                Check(checkBox.IsChecked == (channel == "Speech"), "Battery output defaults avoid OS balloon speech duplication: " + identifier);
            }
        }
        Check(Equals(low.SelectedValue, 20) && Equals(critical.SelectedValue, 10)
            && GetAccessiblePeer(low).GetName() != GetAccessiblePeer(critical).GetName(),
            "Low and critical controls retain distinct essential names and defaults with hints off");
        var reset = (Button)settings.FindName("ResetBatteryAlertsSettingsButton");
        VerifyBatteryPeer(reset, reset.Name, Localization.Text("Settings.ResetCategory.Name", BatteryText("Battery.Settings.Name")),
            AutomationControlType.Button, shortLabel: false);

        byte[] before = File.ReadAllBytes(fixture.Store.FilePath);
        UiPreferences baseline = fixture.Window.CurrentPreferences;
        ((CheckBox)settings.FindName("BatteryLowSoundCheckBox")).IsChecked = true;
        foreach ((int lowValue, int criticalValue) in new[] { (20, 20), (10, 20) })
        {
            SetChoice(settings, low.Name, lowValue);
            SetChoice(settings, critical.Name, criticalValue);
            ClickSettings(settings, "SaveSettingsButton");
            TextBlock result = FindById<TextBlock>(settings, "SectionResultReadout");
            Check(settings.IsVisible && tabs.SelectedItem == tab && result.Text == BatteryText("Battery.InvalidThresholds")
                && result.IsVisible && Keyboard.FocusedElement == low && fixture.Window.CurrentPreferences == baseline
                && before.SequenceEqual(File.ReadAllBytes(fixture.Store.FilePath))
                && Equals(low.SelectedValue, lowValue) && Equals(critical.SelectedValue, criticalValue)
                && ((CheckBox)settings.FindName("BatteryLowSoundCheckBox")).IsChecked == true,
                "Invalid thresholds keep Settings open, focus the error, preserve the draft and leave the profile untouched: " + language);
        }
        SetChoice(settings, low.Name, 35);
        SetChoice(settings, critical.Name, 12);
        ((CheckBox)settings.FindName("BatteryLowNotificationCheckBox")).IsChecked = true;
        ((CheckBox)settings.FindName("BatteryLowSpeechCheckBox")).IsChecked = false;
        ((CheckBox)settings.FindName("BatteryCriticalSoundCheckBox")).IsChecked = true;
        ClickSettings(settings, "SaveSettingsButton");
        var expected = new BatteryAlertPreferences
        {
            Policy = new BatteryAlertPolicy { LowThreshold = 35, CriticalThreshold = 12 },
            Low = new BatteryAlertOutput { Notification = true, Speech = false, Sound = true },
            Critical = new BatteryAlertOutput { Notification = false, Speech = true, Sound = true }
        };
        UiPreferences saved = new UiPreferencesStore(fixture.Store.BaseDirectory).Load(out string? error);
        Check(error is null && !settings.IsVisible && saved.BatteryAlerts == expected && fixture.Window.CurrentPreferences.BatteryAlerts == expected
            && saved.AudioProfiles.SequenceEqual(baseline.AudioProfiles) && saved.Theme == baseline.Theme
            && !saved.ShowHints && !saved.AnnounceStateChanges,
            "OK commits threshold and output drafts together and preserves unrelated preferences: " + language);

        settings = OpenTransactionSettings(fixture.Window);
        SelectSettingsCategory(settings, "BatteryAlertsSettingsTab");
        before = File.ReadAllBytes(fixture.Store.FilePath);
        SetChoice(settings, "BatteryLowThresholdChoice", 45);
        fixture.Confirm = true;
        ClickSettings(settings, "ResetBatteryAlertsSettingsButton");
        Check(Equals(((ComboBox)settings.FindName("BatteryLowThresholdChoice")).SelectedValue, 20)
            && Equals(((ComboBox)settings.FindName("BatteryCriticalThresholdChoice")).SelectedValue, 10)
            && ((CheckBox)settings.FindName("BatteryAlertsEnabledCheckBox")).IsChecked == true
            && new[] { "Low", "Critical" }.All(level => ((CheckBox)settings.FindName("Battery" + level + "SpeechCheckBox")).IsChecked == true
                && ((CheckBox)settings.FindName("Battery" + level + "NotificationCheckBox")).IsChecked == false
                && ((CheckBox)settings.FindName("Battery" + level + "SoundCheckBox")).IsChecked == false)
            && fixture.Window.CurrentPreferences.BatteryAlerts == expected && before.SequenceEqual(File.ReadAllBytes(fixture.Store.FilePath)),
            "Category reset restores every battery default only in the draft: " + language);
        fixture.Confirm = false;
        var resetLow = (ComboBox)settings.FindName("BatteryLowThresholdChoice");
        VerifyEscapeClosesDropDown(settings, resetLow);
        FocusForKeyboardCheck(resetLow);
        RaiseEscapeBehaviorKeyDown(resetLow);
        Check(!settings.IsVisible && fixture.Window.CurrentPreferences.BatteryAlerts == expected
            && before.SequenceEqual(File.ReadAllBytes(fixture.Store.FilePath)),
            "Escape with discard abandons a category reset without changing the saved profile: " + language);
        settings = OpenTransactionSettings(fixture.Window);
        SelectSettingsCategory(settings, "BatteryAlertsSettingsTab");
        Check(Equals(((ComboBox)settings.FindName("BatteryLowThresholdChoice")).SelectedValue, 35),
            "Reopening Settings restores committed battery values after Escape discard");
        fixture.Confirm = true;
        ClickSettings(settings, "ResetBatteryAlertsSettingsButton");
        ClickSettings(settings, "SaveSettingsButton");
        Check(fixture.Window.CurrentPreferences.BatteryAlerts == new BatteryAlertPreferences()
            && new UiPreferencesStore(fixture.Store.BaseDirectory).Load(out _).AudioProfiles.SequenceEqual(baseline.AudioProfiles),
            "OK can persist a battery category reset without resetting audio profiles");
    }

    private static void VerifyBatteryPeer(UIElement control, string identifier, string expectedName,
        AutomationControlType role, bool shortLabel = true)
    {
        AutomationPeer peer = control is TabItem tab && ItemsControl.ItemsControlFromItemContainer(tab) is TabControl tabs
            ? GetAccessiblePeer(tabs).GetChildren().Where(child => child.GetAutomationControlType() == AutomationControlType.TabItem)
                .ElementAt(tabs.Items.IndexOf(tab))
            : GetAccessiblePeer(control);
        Check(peer.GetAutomationId() == identifier && peer.GetName() == expectedName && peer.GetAutomationControlType() == role
            && !string.IsNullOrWhiteSpace(expectedName) && (!shortLabel || expectedName.Length <= 90)
            && string.IsNullOrEmpty(AutomationProperties.GetHelpText(control)),
            "Actual localized UIA name and native role survive disabled hints: " + identifier + "/" + Localization.CurrentLanguage
                + $"; actual id={peer.GetAutomationId()}, name={peer.GetName()}, role={peer.GetAutomationControlType()}, help={AutomationProperties.GetHelpText(control)}");
    }

    private static void VerifyBatteryInitialAndReceiverEpisodes(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "episodes", new BatteryAlertPreferences
        {
            Low = BatteryAllChannels(), Critical = BatteryAllChannels()
        });
        fixture.Start([20, 80, 70, 60]);
        string low = BatteryMessage(BatteryAlertSeverity.Low, (1, 20));
        Check(fixture.Speech.Messages.SequenceEqual(new[] { low }) && fixture.Speech.BusyDuringRequests.SequenceEqual(new[] { true })
            && fixture.Desktop.Notifications.Single().Message == low && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Low })
            && fixture.Window.ResultText == low && FindById<TextBlock>(fixture.Window, "ResultReadout").IsVisible
            && AutomationProperties.GetLiveSetting(FindById<TextBlock>(fixture.Window, "ResultReadout")) == AutomationLiveSetting.Off
            && UsbLog(fixture.Window).Any(line => line.Contains(low, StringComparison.Ordinal)),
            "The first low snapshot alerts while Connect is busy and its silent visual warning survives connection completion: " + language);
        fixture.ClearOutputs();
        fixture.PollOnce();
        fixture.SetReadings(19, 80, 70, 60);
        fixture.PollOnce();
        Check(fixture.OutputCount == 0, "Unchanged and declining readings inside a consumed low episode are silent");
        fixture.SetReadings(10, 80, 70, 60);
        fixture.PollOnce();
        Check(fixture.Speech.Messages.SequenceEqual(new[] { BatteryMessage(BatteryAlertSeverity.Critical, (1, 10)) })
            && fixture.Desktop.Notifications.Single().Critical && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Critical }),
            "A low-to-critical crossing escalates once through each enabled output");
        fixture.ClearOutputs();
        fixture.SetReadings(null, 0, 255, 101);
        fixture.Transport.SetTransmitterConnected(1, false);
        fixture.PollOnce();
        Check(fixture.OutputCount == 0, "Unknown percentages and a disconnected zero-percent transmitter produce no warning");
        fixture.SetReadings(10, 20, 70, 10);
        fixture.Transport.SetTransmitterConnected(1, true);
        fixture.PollOnce();
        string expected = BatteryMessage(BatteryAlertSeverity.Critical, (4, 10)) + ". " + BatteryMessage(BatteryAlertSeverity.Low, (2, 20));
        Check(fixture.Speech.Messages.SequenceEqual(new[] { expected }) && fixture.Desktop.Notifications.Count == 1
            && fixture.Desktop.Notifications[0].Message == expected && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Critical }),
            "TX2 and TX4 have independent episodes; missing readings did not rearm TX1 and a mixed poll is batched critical first");

        fixture.ClearOutputs();
        fixture.ConnectReceiver(fixture.SecondReceiver, [10, 80, 70, 60]);
        Check(fixture.Speech.Messages.SequenceEqual(new[] { BatteryMessage(BatteryAlertSeverity.Critical, (1, 10)) }),
            "A distinct receiver gets its own first critical warning");
        fixture.ClearOutputs();
        fixture.ConnectReceiver(fixture.FirstReceiver, [10, 20, 70, 10]);
        fixture.PollOnce();
        Check(fixture.OutputCount == 0, "Returning to a consumed receiver preserves its per-TX history");

        BatteryAlertPreferences preferences = fixture.Window.CurrentPreferences.BatteryAlerts;
        fixture.SaveAlerts(preferences with { Low = preferences.Low with { Sound = false } });
        fixture.PollOnce();
        Check(fixture.OutputCount == 0, "Changing only an output channel does not replay consumed episodes");
        fixture.SaveAlerts(preferences with { Policy = preferences.Policy with { Enabled = false } });
        Check(!fixture.Desktop.TrayVisible, "Disabling master removes the notification-created tray when close-to-tray is false");
        fixture.SaveAlerts(preferences); // Deliberately no intervening poll while disabled.
        fixture.PollOnce();
        Check(fixture.Desktop.Notifications.Count == 1 && fixture.Speech.Messages.Count == 1 && fixture.Sounds.Count == 1,
            "Disable then re-enable before a poll resets episodes and reevaluates the current readings once");
        fixture.ClearOutputs();
        fixture.SaveAlerts(preferences with { Policy = preferences.Policy with { Enabled = false } });
        fixture.SetReadings(0, 0, 0, 0);
        fixture.PollOnce();
        Check(fixture.OutputCount == 0 && !fixture.Desktop.TrayVisible,
            "Master off suppresses every warning channel even at zero percent");
    }

    private static void VerifyBatteryBatchedChannels(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "channels");
        fixture.Start();
        string critical = BatteryMessage(BatteryAlertSeverity.Critical, (2, 0), (4, 10));
        string low = BatteryMessage(BatteryAlertSeverity.Low, (1, 20), (3, 19));
        string batch = critical + ". " + low;
        for (int mask = 0; mask < 8; mask++)
        {
            fixture.SetReadings(90, 80, 70, 60);
            fixture.PollOnce();
            var output = new BatteryAlertOutput { Notification = (mask & 1) != 0, Speech = (mask & 2) != 0, Sound = (mask & 4) != 0 };
            fixture.SaveAlerts(new BatteryAlertPreferences { Low = output, Critical = output });
            fixture.ClearOutputs();
            TextBlock result = FindById<TextBlock>(fixture.Window, "ResultReadout");
            result.Text = "Previous operation";
            AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
            bool updatedSilently = false;
            DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))!;
            void ObserveResult(object? sender, EventArgs args)
            {
                if (result.Text == batch) updatedSilently = AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off;
            }
            descriptor.AddValueChanged(result, ObserveResult);
            try { fixture.SetReadings(20, 0, 19, 10); fixture.PollOnce(); }
            finally { descriptor.RemoveValueChanged(result, ObserveResult); }
            Check(fixture.Desktop.Notifications.Count == (output.Notification ? 1 : 0)
                && fixture.Speech.Messages.SequenceEqual(output.Speech ? new[] { batch } : Array.Empty<string>())
                && fixture.Sounds.SequenceEqual(output.Sound ? new[] { BatteryAlertSeverity.Critical } : Array.Empty<BatteryAlertSeverity>())
                && result.Text == batch && result.IsVisible && updatedSilently
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off
                && !fixture.Window.CurrentPreferences.AnnounceStateChanges && !fixture.Window.ShowHints
                && !fixture.Window.CurrentPreferences.ShowOperationMessages,
                "All three channels are independent; four TXs produce one silent visual summary and at most one callback per channel: " + mask + "/" + language);
            if (output.Notification)
            {
                BatteryNotice notice = fixture.Desktop.Notifications.Single();
                Check(notice.Message == batch && notice.Title == BatteryText("Battery.Critical.Title") && notice.Critical
                    && notice.Message.Length <= 255 && notice.Title.Length <= 63,
                    "Mixed four-transmitter notification fits native UTF16 message255/title63 without losing a transmitter: " + language);
            }
            int outputs = fixture.OutputCount;
            fixture.PollOnce();
            Check(fixture.OutputCount == outputs, "A mixed four-TX poll cannot duplicate an unchanged batch");
        }
        fixture.SetReadings(90, 80, 70, 60);
        fixture.PollOnce();
        fixture.SaveAlerts(new BatteryAlertPreferences
        {
            Low = new BatteryAlertOutput { Notification = true, Speech = false, Sound = true },
            Critical = new BatteryAlertOutput { Notification = false, Speech = true, Sound = false }
        });
        fixture.ClearOutputs();
        fixture.SetReadings(20, 0, 19, 10);
        fixture.PollOnce();
        Check(fixture.Desktop.Notifications.Single() == new BatteryNotice(BatteryText("Battery.Low.Title"), low, false)
            && fixture.Speech.Messages.SequenceEqual(new[] { critical }) && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Low })
            && fixture.Window.ResultText == batch,
            "Each channel filters severity independently; sound selects the highest configured severity rather than the highest observed severity");
        BatteryAlertPreferences preferences = fixture.Window.CurrentPreferences.BatteryAlerts;
        fixture.SaveAlerts(preferences with { Low = preferences.Low with { Notification = false } });
        Check(!fixture.Desktop.TrayVisible && !fixture.Window.CurrentPreferences.CloseToTray,
            "Removing both native-notification channels removes their tray without disabling direct speech");

        fixture.SaveAlerts(new BatteryAlertPreferences
        {
            Policy = new BatteryAlertPolicy { LowThreshold = 100, CriticalThreshold = 99 },
            Low = BatteryAllChannels(), Critical = BatteryAllChannels()
        });
        fixture.ClearOutputs();
        fixture.SetReadings(100, 99, 100, 99);
        fixture.PollOnce();
        BatteryNotice maximum = fixture.Desktop.Notifications.Single();
        string maximumBatch = BatteryMessage(BatteryAlertSeverity.Critical, (2, 99), (4, 99)) + ". "
            + BatteryMessage(BatteryAlertSeverity.Low, (1, 100), (3, 100));
        Check(maximum.Message == maximumBatch && maximum.Message.Length <= 255 && maximum.Title.Length <= 63
            && fixture.Speech.Messages.SequenceEqual(new[] { maximumBatch })
            && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Critical }),
            "A mixed four-TX warning with maximum-width100/99 percentages also fits native UTF16 limits: " + language);
    }

    private static void VerifyBatteryChannelFailures(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "failures", new BatteryAlertPreferences
        {
            Low = BatteryAllChannels(), Critical = BatteryAllChannels()
        });
        fixture.Start();
        foreach (string failure in new[] { "notification-false", "notification-throw", "tray-throw", "sound-throw", "speech-false", "speech-throw" })
        {
            fixture.SetReadings(90, 80, 70, 60);
            fixture.PollOnce();
            fixture.ClearOutputs();
            fixture.Desktop.NotificationAvailable = failure != "notification-false";
            fixture.Desktop.NotificationFailure = failure == "notification-throw" ? new IOException("Fake notification failure") : null;
            fixture.Desktop.TrayFailure = failure == "tray-throw" ? new IOException("Fake tray failure") : null;
            fixture.SoundFailure = failure == "sound-throw" ? new IOException("Fake sound failure") : null;
            fixture.Speech.Available = failure != "speech-false";
            fixture.Speech.Failure = failure == "speech-throw" ? new IOException("Fake reader failure") : null;
            fixture.SetReadings(20, 0, 19, 10);
            fixture.PollOnce();
            string batch = BatteryMessage(BatteryAlertSeverity.Critical, (2, 0), (4, 10)) + ". " + BatteryMessage(BatteryAlertSeverity.Low, (1, 20), (3, 19));
            Check(fixture.Window.IsConnected && fixture.Speech.Messages.SequenceEqual(new[] { batch })
                && fixture.Sounds.SequenceEqual(new[] { BatteryAlertSeverity.Critical })
                && fixture.Desktop.Notifications.Count == (failure == "tray-throw" ? 0 : 1)
                && fixture.Window.ResultText == batch,
                "One unavailable or throwing output cannot stop the other battery channels or status processing: " + failure + "/" + language);
            if (failure.StartsWith("notification", StringComparison.Ordinal) || failure == "tray-throw")
                Check(UsbLog(fixture.Window).Any(line => line.Contains(BatteryText("Battery.NotificationUnavailable"), StringComparison.Ordinal)),
                    "Unavailable notification is reported in diagnostics without retrying the episode");
            if (failure == "sound-throw")
                Check(UsbLog(fixture.Window).Any(line => line.Contains(BatteryText("Battery.SoundUnavailable"), StringComparison.Ordinal)),
                    "Sound callback failure leaves a diagnostic warning");
            if (failure == "speech-throw")
                Check(UsbLog(fixture.Window).Any(line => line.Contains(BatteryText("Battery.SpeechUnavailable", "Fake reader failure"), StringComparison.Ordinal))
                    && fixture.Sounds.Count == 1,
                    "An unexpected speech exception is logged locally and cannot prevent the later sound callback");
            int outputs = fixture.OutputCount;
            fixture.PollOnce();
            Check(fixture.OutputCount == outputs, "A failing channel is not retried on every unchanged poll: " + failure);
            fixture.Desktop.NotificationFailure = fixture.Desktop.TrayFailure = fixture.SoundFailure = null;
            fixture.Desktop.NotificationAvailable = fixture.Speech.Available = true;
            fixture.Speech.Failure = null;
        }
    }

    private static void VerifyBatteryBackgroundAndClose(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "background", new BatteryAlertPreferences
        {
            Low = BatteryAllChannels(), Critical = BatteryAllChannels()
        });
        fixture.Start();
        using var other = new PrismOwnedFocusWindow();
        TextBlock mainFocus = FindById<TextBlock>(fixture.Window, "ConnectionReadout");
        fixture.Window.Activate();
        FocusForKeyboardCheck(mainFocus);
        foreach (string state in new[] { "active", "inactive", "minimized", "hidden" })
        {
            fixture.SetReadings(90, 80, 70, 60);
            fixture.PollOnce();
            if (state == "minimized") fixture.Window.WindowState = WindowState.Minimized;
            if (state == "hidden") { fixture.Window.WindowState = WindowState.Normal; fixture.Window.Hide(); }
            Pump();
            if (state != "active") other.Activate();
            Window foreground = state == "active" ? fixture.Window : other.Window;
            IInputElement focus = state == "active" ? mainFocus : other.FocusTarget;
            Check(foreground.IsActive && ReferenceEquals(Keyboard.FocusedElement, focus), "Battery focus precondition: " + state);
            bool visible = fixture.Window.IsVisible;
            WindowState windowState = fixture.Window.WindowState;
            int activations = fixture.Activations;
            int otherActivations = 0;
            void Activated(object? sender, EventArgs args) => otherActivations++;
            other.Window.Activated += Activated;
            try { fixture.ClearOutputs(); fixture.SetReadings(20, 0, 19, 10); fixture.PollOnce(); }
            finally { other.Window.Activated -= Activated; }
            Check(fixture.Desktop.Notifications.Count == 1 && fixture.Speech.Messages.Count == 1 && fixture.Sounds.Count == 1
                && foreground.IsActive && ReferenceEquals(Keyboard.FocusedElement, focus)
                && fixture.Activations == activations && otherActivations == 0 && fixture.Window.IsVisible == visible
                && fixture.Window.WindowState == windowState,
                "Batched warnings preserve focus, activation, visibility and window state: " + state + "/" + language);
        }
        fixture.CloseAndCheckCallbacks();
        Check(other.Window.IsActive && ReferenceEquals(Keyboard.FocusedElement, other.FocusTarget),
            "Closing a hidden alert window leaves the independent foreground window focused");
    }

    private static void VerifyBatteryCompletionFeedback(string language)
    {
        using var fixture = new BatteryInterfaceFixture(language, "completion-feedback");
        fixture.Start();
        using var other = new PrismOwnedFocusWindow();
        other.Activate();
        foreach (bool speech in new[] { false, true })
        {
            fixture.SaveAlerts(new BatteryAlertPreferences { Low = new BatteryAlertOutput { Speech = speech } });
            fixture.SetReadings(90, 80, 70, 60);
            fixture.PollOnce();
            fixture.ClearOutputs();
            fixture.SetReadings(20, 80, 70, 60);
            RunTask(fixture.Window.RefreshAsync());
            string warning = BatteryMessage(BatteryAlertSeverity.Low, (1, 20));
            typeof(MainWindow).GetMethod("HotkeyFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, [fixture.Window.ResultText, true]);
            Check(fixture.Window.ResultText == warning && fixture.Speech.Messages.SequenceEqual(speech ? new[] { warning } : Array.Empty<string>())
                && fixture.Desktop.Notifications.Count == 0 && fixture.Sounds.Count == 0 && fixture.Window.IsConnected,
                "Read completion preserves the warning and shortcut feedback neither duplicates it nor bypasses battery speech opt-out: " + speech + "/" + language);

            fixture.SetReadings(90, 80, 70, 60);
            fixture.PollOnce();
            fixture.ClearOutputs();
            fixture.SetReadings(20, 80, 70, 60);
            AudioProfile unchanged = AudioProfile.FromStatus("Battery completion profile", A1Status.Parse(ValidStatus));
            string? error = RunTaskResult(fixture.Window.ApplyAudioProfileAsync(unchanged));
            Check(error is null && fixture.Window.ResultText == warning
                && fixture.Speech.Messages.SequenceEqual(speech ? new[] { warning } : Array.Empty<string>())
                && fixture.Desktop.Notifications.Count == 0 && fixture.Sounds.Count == 0,
                "Profile completion preserves a new battery warning with its own speech preference: " + speech + "/" + language);
            Check(other.Window.IsActive && ReferenceEquals(Keyboard.FocusedElement, other.FocusTarget),
                "Read, shortcut feedback and unchanged-profile validation preserve the independent foreground window");
        }
        fixture.SaveAlerts(new BatteryAlertPreferences
        {
            Low = new BatteryAlertOutput { Notification = true, Speech = false },
            Critical = new BatteryAlertOutput { Speech = false }
        });
        fixture.SetReadings(90, 80, 70, 60);
        fixture.PollOnce();
        fixture.SetReadings(20, 80, 70, 60);
        fixture.PollOnce();
        Check(fixture.Desktop.TrayVisible && UsbField<bool>(fixture.Window, "batteryNotificationTray")
            && !fixture.Window.CurrentPreferences.CloseToTray,
            "Background recovery fixture owns an icon created only for battery notifications");
        foreach (bool hidden in new[] { false, true })
        {
            if (hidden) fixture.Window.Hide();
            else fixture.Window.WindowState = WindowState.Minimized;
            other.Activate();
            int activations = fixture.Activations;
            bool visible = fixture.Window.IsVisible;
            WindowState state = fixture.Window.WindowState;
            typeof(MainWindow).GetMethod("OnDesktopFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, [new InvalidOperationException("Simulated notification tray recovery failure")]);
            Pump();
            Check(fixture.Window.IsConnected && fixture.Activations == activations && fixture.Window.IsVisible == visible
                && fixture.Window.WindowState == state && other.Window.IsActive && ReferenceEquals(Keyboard.FocusedElement, other.FocusTarget),
                "A background notification-tray failure cannot restore or activate a window without CloseToTray: " + hidden + "/" + language);
        }
    }

    private static TValue RunTaskResult<TValue>(Task<TValue> task)
    {
        RunTask(task);
        return task.GetAwaiter().GetResult();
    }

    private sealed record BatteryNotice(string Title, string Message, bool Critical);

    private sealed class BatteryMemoryDesktop : IDesktopIntegration
    {
        internal List<BatteryNotice> Notifications { get; } = [];
        internal bool NotificationAvailable { get; set; } = true;
        internal Exception? NotificationFailure { get; set; }
        internal Exception? TrayFailure { get; set; }
        internal bool TrayVisible { get; private set; }
        internal int StartupCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int CallsAfterDispose { get; private set; }

        private void CheckAlive()
        {
            if (DisposeCalls == 0) return;
            CallsAfterDispose++;
            throw new ObjectDisposedException(nameof(BatteryMemoryDesktop));
        }
        public StartupRegistrationSnapshot CaptureStartup() { CheckAlive(); StartupCalls++; return StartupRegistrationSnapshot.Missing; }
        public void SetStartup(bool enabled) { CheckAlive(); StartupCalls++; }
        public void RestoreStartup(StartupRegistrationSnapshot snapshot) { CheckAlive(); StartupCalls++; }
        public void ConfigureTrayControls(Func<TrayControlSnapshot> readSnapshot, Func<int, Task> adjustGain, Func<int, Task> toggleMute) => CheckAlive();
        public void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit)
        {
            CheckAlive();
            if (TrayFailure is not null) throw TrayFailure;
            TrayVisible = true;
        }
        public bool ShowNotification(string title, string message, bool critical)
        {
            CheckAlive();
            Notifications.Add(new(title, message, critical));
            if (NotificationFailure is not null) throw NotificationFailure;
            return NotificationAvailable;
        }
        public void HideTray() { CheckAlive(); TrayVisible = false; }
        public void Dispose() { DisposeCalls++; TrayVisible = false; }
    }

    private sealed class BatteryInterfaceSpeech : IScreenReaderSpeech
    {
        private readonly PrismInterfaceSpeech recorder = new();
        internal List<string> Messages => recorder.Messages;
        internal List<bool> BusyDuringRequests { get; } = [];
        internal Func<bool> IsBusy { get; set; } = () => false;
        internal bool Available { get; set; } = true;
        internal Exception? Failure { get; set; }
        internal int DisposeCalls => recorder.DisposeCalls;
        internal int CallsAfterDispose => recorder.CallsAfterDispose;
        public bool TrySpeak(string message)
        {
            recorder.TrySpeak(message);
            BusyDuringRequests.Add(IsBusy());
            if (Failure is not null) throw Failure;
            return Available;
        }
        public void Dispose() => recorder.Dispose();
    }

    private sealed class BatteryInterfaceFixture : IDisposable
    {
        private readonly FakeHotkeyNativeApi hotkeys = new();
        private readonly List<string> desktopErrors = [];
        private readonly List<FakeTransport> transports = [];
        private int?[] nextReadings = [90, 80, 70, 60];
        internal ReceiverInfo FirstReceiver { get; } = new("Battery receiver A", "simulated://battery-a-" + Guid.NewGuid().ToString("N"), 0, 64);
        internal ReceiverInfo SecondReceiver { get; } = new("Battery receiver B", "simulated://battery-b-" + Guid.NewGuid().ToString("N"), 0, 64);
        internal FakeTransport Transport { get; private set; } = null!;
        internal BatteryMemoryDesktop Desktop { get; } = new();
        internal BatteryInterfaceSpeech Speech { get; } = new();
        internal List<BatteryAlertSeverity> Sounds { get; } = [];
        internal Exception? SoundFailure { get; set; }
        internal bool Confirm { get; set; }
        internal int Activations { get; private set; }
        internal UiPreferencesStore Store { get; }
        internal MainWindow Window { get; }
        internal int OutputCount => Desktop.Notifications.Count + Speech.Messages.Count + Sounds.Count;

        internal BatteryInterfaceFixture(string language, string scenario, BatteryAlertPreferences? alerts = null)
        {
            Store = new UiPreferencesStore(Path.Combine(ReportDirectory, "battery-interface", language, scenario + "-" + Guid.NewGuid().ToString("N")));
            Check(Store.TrySave(new UiPreferences
            {
                FirstRunCompleted = true, Language = language, ConfirmExit = false, CloseToTray = false,
                StartWithWindows = false, ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false,
                PreserveVolatileSettings = false, GlobalHotkeysEnabled = false, BatteryAlerts = alerts ?? new BatteryAlertPreferences(),
                AudioProfiles = [AudioProfile.FromStatus("Keep battery profile", A1Status.Parse(ValidStatus))]
            }, out string? error) && error is null, "Prepare an isolated battery interface profile: " + error);
            Window = new MainWindow(() => [FirstReceiver, SecondReceiver], _ => CreateTransport(), Store.BaseDirectory,
                Desktop, (_, _) => Confirm, reportDesktopError: desktopErrors.Add, automaticReceiverManagement: false,
                hotkeyNativeApi: hotkeys, screenReader: Speech, playBatterySound: severity =>
                {
                    Sounds.Add(severity);
                    if (SoundFailure is not null) throw SoundFailure;
                });
            Speech.IsBusy = () => Window.IsBusy;
            Window.Activated += CountActivation;
            MoveOffscreen(Window);
        }

        private FakeTransport CreateTransport()
        {
            var transport = new FakeTransport { StatusPayloadLength = 23 };
            SetReadings(transport, nextReadings);
            transports.Add(transport);
            Transport = transport;
            return transport;
        }
        internal void Start(int?[]? initial = null)
        {
            nextReadings = initial ?? [90, 80, 70, 60];
            Window.Show();
            PumpUntil(() => UsbField<bool>(Window, "loaded") && !Window.IsBusy && Window.Receivers.Count == 2);
            ((ListBox)Window.FindName("ReceiverList")).SelectedItem = FirstReceiver;
            RunTask(Window.ConnectAsync());
            StopTimers();
            Check(Window.IsConnected && Window.ActiveReceiverPath == FirstReceiver.Path && Window.Transmitters.Count == 4,
                "Battery fixture connects only a simulated four-transmitter receiver");
        }
        internal void ConnectReceiver(ReceiverInfo receiver, int?[] readings)
        {
            StopTimers();
            typeof(MainWindow).GetMethod("EndSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, [false]);
            nextReadings = readings;
            ((ListBox)Window.FindName("ReceiverList")).SelectedItem = receiver;
            RunTask(Window.ConnectAsync());
            StopTimers();
            Check(Window.IsConnected && Window.ActiveReceiverPath == receiver.Path, "Explicit simulated receiver switch completed");
        }
        internal void SetReadings(params int?[] readings) => SetReadings(Transport, readings);
        private static void SetReadings(FakeTransport transport, int?[] readings)
        {
            if (readings.Length != 4) throw new ArgumentException("Battery fixtures require exactly four TX readings.");
            for (int index = 0; index < 4; index++)
            {
                transport.SetTransmitterConnected(index, true);
                SetAutomationStatusByte(transport, index switch { 0 => 2, 1 => 3, 2 => 19, _ => 20 }, (byte)(readings[index] ?? 255));
            }
        }
        internal void PollOnce()
        {
            StopTimers();
            int reads = Transport.Commands.Count(command => command == A1Command.GetStatus);
            RunTask(Poll(Window));
            StopTimers();
            Check(Window.IsConnected && Transport.Commands.Count(command => command == A1Command.GetStatus) == reads + 1,
                "Each battery poll consumes exactly one fresh simulated status");
            CheckReadOnly();
        }
        internal void SaveAlerts(BatteryAlertPreferences alerts) => SavePreferences(Window, Window.CurrentPreferences with { BatteryAlerts = alerts });
        internal void ClearOutputs() { Desktop.Notifications.Clear(); Speech.Messages.Clear(); Speech.BusyDuringRequests.Clear(); Sounds.Clear(); }
        private void CountActivation(object? sender, EventArgs args) => Activations++;
        private void StopTimers()
        {
            foreach (string name in new[] { "pollTimer", "receiverTimer", "autoApplyTimer" }) UsbField<DispatcherTimer>(Window, name).Stop();
        }
        private void CheckReadOnly() => Check(transports.All(transport => transport.Commands.All(
            command => command is A1Command.GetStatus or A1Command.GetVersion)),
            "Battery warnings never send SetMute, SetAutoOff, reboot or any other physical setting command");
        internal void CloseAndCheckCallbacks()
        {
            int outputs = OutputCount;
            int commands = transports.Sum(transport => transport.Commands.Count);
            Task[] pending = [];
            DispatcherOperation queued = Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (Transport is not null) SetReadings(0, 0, 0, 0);
                pending = [Poll(Window), Window.ConnectAsync(), Window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus)];
            }));
            Window.RequestExit();
            Window.RequestExit();
            RunTask(queued.Task);
            RunTask(Task.WhenAll(pending));
            Pump();
            Check(pending.Length == 3 && OutputCount == outputs && transports.Sum(transport => transport.Commands.Count) == commands
                && Speech.DisposeCalls == 1 && Speech.CallsAfterDispose == 0 && Desktop.DisposeCalls == 1 && Desktop.CallsAfterDispose == 0
                && transports.All(transport => transport.Disposed)
                && !UsbField<DispatcherTimer>(Window, "pollTimer").IsEnabled && !UsbField<DispatcherTimer>(Window, "receiverTimer").IsEnabled,
                "After close queued polls, reconnects and shortcuts cannot deliver warnings, read devices or reuse disposed outputs");
        }
        public void Dispose()
        {
            try { if (!UsbField<bool>(Window, "closed")) CloseAndCheckCallbacks(); RunTask(Window.ReceiverSessionTask); Pump(); }
            finally { Window.Activated -= CountActivation; foreach (FakeTransport transport in transports) transport.Dispose(); }
            CheckReadOnly();
            Check(Desktop.StartupCalls == 0 && Desktop.DisposeCalls == 1 && Desktop.CallsAfterDispose == 0
                && Speech.DisposeCalls == 1 && Speech.CallsAfterDispose == 0 && desktopErrors.Count == 0
                && hotkeys.Calls.Count == 0 && hotkeys.Registered.Count == 0,
                "Battery fixture uses only injected outputs and releases its windows and fake services without startup or hotkey changes");
        }
    }
}
