using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyParityInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            VerifyAudioMeterInterface();
            VerifyDesktopParity();
            VerifyParityIntegration();
            VerifyParityUi();
            VerifyReferenceSettingsLabels();
            VerifyReferenceLabels();
        }
        finally { application.Shutdown(); }
        Console.WriteLine("Validation reports: " + ReportDirectory);
    }

    private static partial void VerifyInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        VerifyPreferences();
        VerifyExpandedPreferences();
        using var transport = new FakeTransport();
        MainWindow window = CreateWindow(transport, "normal");
        try
        {
            Check(window.Title == "Hollylark — LARK A1", "Hollylark branding");
            Check(window.AudioSettings.Count == 5 && window.DeviceSettings.Count == 3, "Eight relevant setting rows");
            Check(window.Transmitters.Count == 2, "Basic view does not invent TX3 and TX4");
            Check(AllSettings(window).All(setting => !setting.IsEnabled && !setting.CanApply), "Disconnected controls disabled");
            Check(transport.Commands.Count == 0, "Discovery does not send device commands");
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && !window.IsBusy, "UI connects and obtains a confirmed snapshot");
            Check(window.Transmitters.All(transmitter => transmitter.CanMute), "Connected TXs on supported firmware enable mute");
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion), "Connect performs only reads");
            Check(AllSettings(window).All(setting => setting.CanApply && !setting.IsDirty), "Confirmed snapshot enables valid settings");
            Check(Descendants(window).OfType<TabControl>().Single().Items.Count == 2, "Two streamlined main sections");
            VerifyAccessibleControls(window);
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            Check(gain.SelectedIndex == 3, "Gain selection reflects actual readback");
            gain.SelectedIndex = 1;
            Check(gain.IsDirty && gain.StatusText.Contains("Niezastosowana"), "Unapplied choice is visibly marked");
            string previousResult = window.ResultText;
            TextBlock result = FindById<TextBlock>(window, "ResultReadout");
            FocusManager.SetFocusedElement(window, result);
            RunTask(Poll(window));
            Check(gain.SelectedIndex == 1 && gain.IsDirty, "Polling preserves unapplied choice");
            Check(window.ResultText == previousResult, "Polling does not replace actionable result");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(window), result), "Polling preserves logical focus");
            RunTask(window.ApplySettingAsync(gain));
            Check(gain.SelectedIndex == 1 && !gain.IsDirty && gain.ConfirmedValue == "2", "Apply updates current value only after readback");
            Check(window.ResultText.Contains("potwierdziło"), "Confirmed action produces a success result");
            gain.SelectedIndex = 4;
            RunTask(window.RefreshAsync());
            Check(gain.SelectedIndex == 1 && !gain.IsDirty, "Explicit refresh discards draft without writing it");
            RunTask(window.ToggleMuteAsync(0));
            Check(window.Transmitters[0].IsMuted, "Mute UI waits for confirmed state");
            transport.SetTransmitterConnected(1, false);
            RunTask(Poll(window));
            Check(!window.Transmitters[1].CanMute, "Offline transmitter cannot be muted");
            Check(!window.DeviceSettings.Single(setting => setting.Id == "Identification").CanApply, "Recognition needs two connected transmitters");
            transport.SetTransmitterConnected(1, true);
            int versionsBefore = transport.Commands.Count(command => command == A1Command.GetVersion);
            RunTask(Poll(window));
            Check(transport.Commands.Count(command => command == A1Command.GetVersion) > versionsBefore, "Polling refreshes firmware when a transmitter rejoins");
            RenderWindow(window, "Hollylark-controls.png");
            window.Width = 720;
            window.Height = 520;
            Pump();
            RenderWindow(window, "Hollylark-minimum-size.png");
            Check(FindById<TextBlock>(window, "ResultReadout").ActualWidth > 150, "Minimum layout retains usable result readout");
            window.Width = 960;
            window.Height = 740;
            VerifyAuxiliaryWindows(window);
            VerifySettingsInteraction(window);
            window.Disconnect();
            Check(!window.IsConnected && gain.SelectedIndex == -1 && !gain.IsEnabled, "Disconnect clears stale values");
            Check(transport.Disposed, "Disconnect closes transport");
        }
        finally { window.Close(); Pump(); }

        VerifyRejectedWrites();
        VerifyInterruptedConnection();
        VerifyOldFirmware();
        VerifyKeyboardAccessibility();
        VerifyDesktopIntegration();
        VerifyPresentationPreferences();
        VerifyAudioMeterInterface();
        VerifyDesktopParity();
        VerifyParityIntegration();
        VerifyParityUi();
        VerifyReferenceSettingsLabels();
        VerifyReferenceLabels();
        VerifyLocalizationInterface();
        VerifyMainSessionFollowup();
        VerifySettingsFollowup();
        VerifyAudioFollowupChecks();
        VerifyAudioSourceFormats();
        VerifyAudioPeakInterface();
        VerifyPeakReviewChecks();
        VerifyEscapeBehaviorChecks();
        VerifyPresentationStability();
        VerifyNativeListParityChecks();
        VerifyInterfaceParityFollowup();
        VerifyConfirmationOwnershipChecks();
        VerifyAutomaticUsbChecks();
        VerifyProfileReceiverGenerationChecks();
        VerifyFirstRunSettingsRevisionChecks();
        VerifyMainAccessibilityRevisionChecks();
        VerifyMicrophoneTargetInterfaceChecks();
        VerifyProfileMicrophoneTargetChecks();
        VerifyAutomatedControlInterface();
        VerifyPrismBackgroundInterfaceChecks();
        VerifyBatteryAlertInterfaceChecks();
        if (!skipNativeMenuSmoke) VerifySingleInstanceActivationChecks();
        else Console.WriteLine("SKIP: Window restoration/focus requires the interactive desktop; run --single-instance-ui-only separately.");
        application.Shutdown();
        Console.WriteLine("In-process WPF focus traversal, routed keys, UIA providers and rendered layouts checked. No OS input was injected. Actual NVDA listening, physical keyboard traversal and multi-DPI/high-contrast checks remain manual.");
        Console.WriteLine("Validation reports: " + ReportDirectory);
    }

    private static MainWindow CreateWindow(FakeTransport transport, string name)
    {
        var receiver = new ReceiverInfo("Simulated LARK A1", "simulated://receiver", 0, 64);
        var window = new MainWindow(() => new[] { receiver }, _ => transport, Path.Combine(ReportDirectory, name),
            confirm: (_, title) => title != LarkControl.Windows.Localization.Text("Settings.Save.Title"), automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        return window;
    }

    private static IEnumerable<SettingRowViewModel> AllSettings(MainWindow window) => window.AudioSettings.Concat(window.DeviceSettings);

    private static void VerifyRejectedWrites()
    {
        using var transport = new FakeTransport { RejectWrite = true };
        MainWindow window = CreateWindow(transport, "rejection");
        try
        {
            RunTask(window.ConnectAsync());
            SettingRowViewModel gain = window.AudioSettings.First();
            gain.SelectedIndex = 5;
            RunTask(window.ApplySettingAsync(gain));
            Check(!window.IsConnected && !gain.CanApply, "Rejected write closes session and clears availability");
            Check(window.ResultText.Contains("Nie potwierdzono"), "Rejected write never becomes a success message");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyInterruptedConnection()
    {
        using var transport = new FakeTransport { DelayMilliseconds = 250 };
        MainWindow window = CreateWindow(transport, "cancellation");
        try
        {
            Task connection = window.ConnectAsync();
            PumpUntil(() => window.IsBusy && transport.Commands.Count > 0);
            Check(MainWindow.CancelOperationCommand.CanExecute(null, window), "Cancel remains available during a delayed backend connection");
            window.Disconnect();
            string cancelledResult = window.ResultText;
            RunTask(connection);
            Check(!window.IsConnected && !window.IsBusy && transport.Disposed, "Late connection completion cannot resurrect a cancelled session");
            Check(window.ResultText == cancelledResult, "Late completion cannot overwrite disconnect result");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyOldFirmware()
    {
        using var transport = new FakeTransport { VersionBytes = [1, 0, 1, 2] };
        MainWindow window = CreateWindow(transport, "old-firmware");
        try
        {
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && window.Transmitters.All(transmitter => !transmitter.CanMute), "Old receiver firmware retains mute guard");
            Check(window.Transmitters.All(transmitter => transmitter.Summary.Contains("1.0.1.3")), "Disabled mute has an accessible explanation");
            foreach (TransmitterViewModel transmitter in window.Transmitters)
            {
                SelectTransmitter(window, transmitter.Index + 1);
                InvokeAccessible(FindById<Button>(window, transmitter.ActionId));
                Check(window.ResultText == Localization.Text("Main.Mute.Unavailable"),
                    "A native mute request explains unsupported firmware without writing a setting");
                Check(ReadTransmitterSummary(window, transmitter.Index + 1).Contains("1.0.1.3"),
                    "The native microphone item exposes its firmware guard: " + transmitter.ItemId);
            }
            Check(transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "UIA cannot invoke mute on unsupported mock firmware");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyAuxiliaryWindows(MainWindow owner)
    {
        foreach (Func<Window> open in new Func<Window>[] { owner.OpenSettingsWindow, owner.OpenInformationWindow, owner.OpenHelpWindow })
        {
            Window window = open();
            MoveOffscreen(window);
            Pump();
            Check(ReferenceEquals(window.Owner, owner), "Auxiliary window has correct owner");
            VerifyAccessibleControls(window);
            window.Close();
            Pump();
        }
    }

    private static void VerifySettingsInteraction(MainWindow owner)
    {
        ComboBox initialGain = FindById<ComboBox>(owner, "Gain.Choice");
        Check(AutomationProperties.GetHelpText(initialGain).Length > 0, "Template-defined optional hint is initially accessible");
        Window settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        SelectSettingsControlsTab(settings);
        FindById<CheckBox>(settings, "ShowHintsCheckBox").IsChecked = false;
        FindById<Button>(settings, "CancelSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(owner.ShowHints, "Settings Cancel preserves previous preference");
        settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        SelectSettingsControlsTab(settings);
        FindById<CheckBox>(settings, "ShowHintsCheckBox").IsChecked = false;
        FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(!owner.ShowHints, "Settings OK applies disabled hints immediately");
        Check(!new UiPreferencesStore(Path.Combine(ReportDirectory, "normal")).Load(out _).ShowHints,
            "Settings dialog persists actual selected value");
        ComboBox gain = FindById<ComboBox>(owner, "Gain.Choice");
        Check(AutomationProperties.GetHelpText(gain) == string.Empty, "Actual setting control loses optional UIA hints");
        Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(gain)), "Actual setting retains its accessible label");
        Check(AutomationProperties.GetHelpText(FindById<Button>(owner, "RefreshButton")) == string.Empty,
            "Disabling optional hints removes the refresh explanation without removing its action name");
        Check(Descendants(owner).OfType<TextBlock>().Where(block => block.Style == owner.FindResource("HintStyle"))
            .All(block => block.Visibility == Visibility.Collapsed), "Visible optional hints are hidden");
        settings = owner.OpenSettingsWindow();
        MoveOffscreen(settings);
        Pump();
        SelectSettingsControlsTab(settings);
        Check(FindById<CheckBox>(settings, "ShowHintsCheckBox").IsChecked == false, "Reopened settings dialog restores saved choice");
        FindById<CheckBox>(settings, "ShowHintsCheckBox").IsChecked = true;
        FindById<Button>(settings, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        ComboBox restoredGain = FindById<ComboBox>(owner, "Gain.Choice");
        Check(owner.ShowHints && AutomationProperties.GetHelpText(restoredGain).Length > 0, "Hints can be restored from the UI");

        Window information = owner.OpenInformationWindow();
        MoveOffscreen(information);
        Pump();
        FindById<Button>(information, "SaveDiagnosticsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string exported = Directory.GetFiles(Path.Combine(ReportDirectory, "normal", "reports"), "Hollylark-*.txt").Single();
        Check(File.ReadAllText(exported).Contains("Firmware odbiornika: 1.0.1.3"), "Diagnostic export contains the actual read firmware");
        information.Close();
        Pump();
    }

    private static partial void VerifyHardwareInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(HidTransport.FindReceivers, receiver => new HidTransport(receiver),
            baseDirectory: Path.Combine(ReportDirectory, "hardware-read-only"), confirm: (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        window.Show();
        try
        {
            PumpUntil(() => !window.IsBusy && window.Receivers.Count > 0);
            Check(window.Receivers.Count == 1, "Real GUI discovers one normal receiver interface");
            RunTask(window.ConnectAsync());
            Check(window.IsConnected, "Real GUI connects through production HID/controller");
            for (int iteration = 0; iteration < 5; iteration++) RunTask(window.RefreshAsync());
            Check(AllSettings(window).All(setting => setting.ConfirmedValue != "Brak danych"), "Real receiver provides all eight setting readouts");
            Window information = window.OpenInformationWindow();
            MoveOffscreen(information);
            Pump();
            string summary = FindById<TextBox>(information, "DeviceInformationReadout").Text;
            string trace = FindById<TextBox>(information, "DiagnosticLogReadout").Text;
            MatchCollection commands = Regex.Matches(trace, "TX 0503AADD(?<command>[0-9A-F]{2})");
            Check(commands.Count >= 7 && commands.All(command => command.Groups["command"].Value is "1F" or "0D"),
                "Physical test trace contains only status/version queries");
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(Path.Combine(ReportDirectory, "hardware-ui.json"), JsonSerializer.Serialize(new
            {
                Time = DateTimeOffset.Now,
                SettingsWritten = false,
                Summary = summary,
                Trace = trace,
                Settings = AllSettings(window).Select(setting => new { setting.Id, setting.ConfirmedValue, setting.IsEnabled }),
                Transmitters = window.Transmitters.Select(transmitter => new { transmitter.Index, transmitter.Summary })
            }, new JsonSerializerOptions { WriteIndented = true }));
            information.Close();
            Pump();
            RenderWindow(window, "Hollylark-real-receiver.png");
            window.Disconnect();
            Check(!window.IsConnected, "Real HID session closes cleanly");
            Console.WriteLine(summary);
            Console.WriteLine("Hardware UI reports: " + ReportDirectory);
        }
        finally { window.Close(); Pump(); application.Shutdown(); }
    }

    private static void VerifyAccessibleControls(Window window)
    {
        Pump();
        Control[] controls = Descendants(window).OfType<Control>().Where(control =>
            !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control)) && control is Button or ComboBox or ListBox or TextBox or CheckBox).ToArray();
        Check(controls.Length > 0, "Window exposes named interactive controls");
        foreach (Control control in controls)
        {
            AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(control);
            Check(peer is not null && !string.IsNullOrWhiteSpace(peer.GetName()), "UIA accessible name: " + AutomationProperties.GetAutomationId(control));
            if (control is TextBox textBox)
            {
                if (window is SettingsWindow && AutomationProperties.GetAutomationId(textBox) is "AudioProfileNameTextBox" or "HotkeyGestureTextBox")
                    Check(!textBox.IsReadOnly && textBox.IsTabStop && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(textBox)),
                        "Profile name and shortcut inputs are explicitly named, editable and reachable by Tab");
                else if (window is MainWindow main)
                {
                    string identifier = AutomationProperties.GetAutomationId(textBox);
                    bool settingValue = AllSettings(main).Any(setting => identifier == setting.CurrentId);
                    bool draftValue = AllSettings(main).Any(setting => identifier == setting.DraftFieldId);
                    Check((settingValue || draftValue) && textBox.IsReadOnly && textBox.IsTabStop,
                        "Main read-only fields are limited to actual setting information: " + identifier);
                    Check(!textBox.IsVisible || main.CurrentPreferences.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields,
                        "Informational edit fields are shown only when explicitly selected: " + identifier);
                    Check(!draftValue || !textBox.IsVisible || identifier == "Gain.DraftField"
                        && main.CurrentPreferences.GainControlMode == GainControlMode.Buttons,
                        "Only button-mode gain exposes a visible draft field from the shared setting template: " + identifier);
                }
                else
                    Check(window is not MainWindow and not FirstRunWindow and not SettingsWindow && textBox.IsReadOnly && textBox.IsTabStop,
                        "Outside main value fields, only consciously opened documents retain keyboard-readable edit output: " + AutomationProperties.GetAutomationId(control));
            }
        }
        foreach (TextBlock readout in Descendants(window).OfType<TextBlock>().Where(block => block.IsVisible && block.Focusable))
        {
            Check(KeyboardNavigation.GetIsTabStop(readout) && GetAccessiblePeer(readout).GetAutomationControlType() == AutomationControlType.Text,
                "Visible focusable readouts expose native static-text semantics: " + AutomationProperties.GetAutomationId(readout));
            ReadAccessibleValue(readout);
        }
    }

    private static Task Poll(MainWindow window) => (Task)typeof(MainWindow)
        .GetMethod("PollAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;

    private static void MoveOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -16000;
        window.Top = -16000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    private static void RenderWindow(Window window, string name)
    {
        Pump();
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bool lightForeground = window.Foreground is SolidColorBrush foreground && foreground.Color.R > 128;
        var backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(lightForeground ? Color.FromRgb(32, 32, 32) : Colors.White), null,
                new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(ReportDirectory);
        using var output = File.Create(Path.Combine(ReportDirectory, name));
        encoder.Save(output);
        Check(output.Length > 2000, "Rendered layout capture: " + name);
    }

    private static TControl FindById<TControl>(DependencyObject root, string id) where TControl : DependencyObject =>
        Descendants(root).OfType<TControl>().Single(control => AutomationProperties.GetAutomationId(control) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> TabContentElements(TabItem tab)
    {
        if (tab.Content is not DependencyObject content)
            throw new InvalidOperationException("The tab has no dependency-object content: " + tab.Name);
        yield return content;
        foreach (DependencyObject descendant in Descendants(content)) yield return descendant;
    }

    private static void RunTask(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Pump();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            Pump();
            if (condition()) return;
            Thread.Sleep(5);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(12));
        throw new TimeoutException("WPF validation condition was not reached.");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
