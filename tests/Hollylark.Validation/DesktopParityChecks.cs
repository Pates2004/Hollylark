using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Microsoft.Win32;

internal static partial class Program
{
    private static void VerifyDesktopParity()
    {
        VerifyDesktopParityPreferences();
        VerifyTrayControlSnapshots();
        VerifyTrayControlDispatch();
        VerifyFirstRunSetup();
    }

    private static void VerifyDesktopParityPreferences()
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "new"));
        Check(!store.ShouldOfferFirstRun, "First-run eligibility is unknown until preferences are loaded");
        UiPreferences defaults = store.Load(out string? error);
        Check(error is null && !defaults.FirstRunCompleted && defaults.PreferredReceiverPath is null
            && store.ShouldOfferFirstRun && !Directory.Exists(store.BaseDirectory),
            "A genuinely missing settings file offers first-run setup without creating data");

        string[] existingDocuments =
        [
            "{}", "{\"SchemaVersion\":1,\"ShowHints\":false}",
            "{\"SchemaVersion\":2,\"Theme\":\"Dark\",\"ShowHints\":false}",
            "{\"SchemaVersion\":2,\"FirstRunCompleted\":false}",
            "{\"SchemaVersion\":2,\"FirstRunCompleted\":true,\"PreferredReceiverPath\":null}"
        ];
        for (int index = 0; index < existingDocuments.Length; index++)
        {
            var existing = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "existing-" + index));
            Directory.CreateDirectory(Path.GetDirectoryName(existing.FilePath)!);
            File.WriteAllText(existing.FilePath, existingDocuments[index]);
            byte[] original = File.ReadAllBytes(existing.FilePath);
            UiPreferences loaded = existing.Load(out error);
            Check(error is null && existing.ShouldOfferFirstRun == !loaded.FirstRunCompleted && loaded.PreferredReceiverPath is null,
                $"Configuration {index} uses explicit setup completion, not mere file existence, and defaults to no remembered receiver");
            Check(original.SequenceEqual(File.ReadAllBytes(existing.FilePath)), $"Loading existing configuration {index} is read-only");
            if (index is 1 or 2) Check(!loaded.ShowHints, $"Legacy hints choice {index} is preserved");
            if (index == 2) Check(loaded.Theme == "Dark", "Existing appearance is preserved without a first-run flag");
        }

        const string receiverPath = @"\\?\hid#vid_3547&pid_0407#Exact-Receiver-A#{00000000-0000-0000-0000-000000000001}";
        UiPreferences remembered = defaults with
        {
            FirstRunCompleted = true, PreferredReceiverPath = receiverPath, HiddenRows = ["TX1", "TX2", "TX3", "TX4"]
        };
        Check(store.TrySave(remembered, out error) && error is null && !store.ShouldOfferFirstRun,
            "Successful setup persistence clears first-run eligibility");
        UiPreferences roundTrip = new UiPreferencesStore(store.BaseDirectory).Load(out error);
        Check(error is null && EquivalentExpandedPreferences(remembered, roundTrip),
            "Setup completion, all four TX rows and the exact HID path survive reload");
        Check(store.TrySave(remembered with { PreferredReceiverPath = null }, out error) && error is null
            && new UiPreferencesStore(store.BaseDirectory).Load(out error).PreferredReceiverPath is null && error is null,
            "A null legacy receiver path round-trips without invalidating the configuration");
        Check(store.TrySave(remembered with { PreferredReceiverPath = new string('a', 4096) }, out error) && error is null,
            "The maximum supported receiver path length is accepted");
        byte[] lastGood = File.ReadAllBytes(store.FilePath);
        foreach (string invalidPath in new[] { "", " ", "receiver\0path", "receiver\npath", "receiver\tpath", "receiver\u007fpath", new string('a', 4097) })
        {
            Check(!store.TrySave(remembered with { PreferredReceiverPath = invalidPath }, out error) && error is not null,
                "Empty, control-containing and oversized receiver paths are rejected");
            Check(lastGood.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Invalid receiver metadata cannot replace valid preferences");
        }
        foreach (string[] invalidRows in new[] { new[] { "TX5" }, new[] { "tx1" }, new[] { "TX1", "TX1" } })
            Check(!store.TrySave(remembered with { HiddenRows = invalidRows }, out error), "TX row validation remains strict and duplicate-free");

        string[] invalidDocuments =
        [
            "{broken", "{\"SchemaVersion\":2,\"FirstRunCompleted\":null}",
            "{\"SchemaVersion\":2,\"FirstRunCompleted\":\"true\"}",
            "{\"SchemaVersion\":2,\"PreferredReceiverPath\":17}",
            "{\"SchemaVersion\":2,\"PreferredReceiverPath\":null,\"PreferredReceiverPath\":null}",
            "{\"SchemaVersion\":2,\"ShowHints\":null}",
            JsonSerializer.Serialize(new { SchemaVersion = 2, PreferredReceiverPath = "receiver\npath" }),
            JsonSerializer.Serialize(new { SchemaVersion = 2, PreferredReceiverPath = new string('a', 4097) })
        ];
        for (int index = 0; index < invalidDocuments.Length; index++)
        {
            var invalid = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "invalid-" + index));
            Directory.CreateDirectory(Path.GetDirectoryName(invalid.FilePath)!);
            File.WriteAllText(invalid.FilePath, invalidDocuments[index]);
            invalid.Load(out error);
            Check(error is not null && !invalid.ShouldOfferFirstRun, $"Unreadable existing configuration {index} does not open setup");
            Check(!invalid.TrySave(defaults with { FirstRunCompleted = true }, out error)
                && File.ReadAllText(invalid.FilePath) == invalidDocuments[index],
                $"Setup cannot replace unreadable configuration {index}");
        }
        using (var lockedFile = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedStore = new UiPreferencesStore(store.BaseDirectory);
            lockedStore.Load(out error);
            Check(error is not null && !lockedStore.ShouldOfferFirstRun
                && !lockedStore.TrySave(defaults, out error), "Locked settings are protected and never mistaken for a new installation");
        }
    }

    private static TrayControlSnapshot DesktopParitySnapshot() => new(true, 3, true,
    [
        new(0, true, false, true), new(1, true, true, true),
        new(2, false, true, true), new(3, true, false, false)
    ]);

    private static TrayMenuEntry TrayEntry(IReadOnlyList<TrayMenuEntry> entries, TrayCommand command) =>
        entries.Single(entry => entry.Command == command && !entry.IsSeparator);

    private static void VerifyTrayControlSnapshots()
    {
        IReadOnlyList<TrayMenuEntry> legacy = DesktopIntegration.CreateTrayMenu(null);
        Check(legacy.Where(entry => !entry.IsSeparator).Select(entry => entry.Command).SequenceEqual(
            new[] { TrayCommand.Show, TrayCommand.Settings, TrayCommand.Refresh, TrayCommand.Exit }),
            "Unconfigured integrations preserve all four original tray commands");
        TrayControlSnapshot snapshot = DesktopParitySnapshot();
        IReadOnlyList<TrayMenuEntry> entries = DesktopIntegration.CreateTrayMenu(snapshot);
        Check(TrayEntry(entries, TrayCommand.DecreaseGain).Label.Contains("4/6")
            && TrayEntry(entries, TrayCommand.IncreaseGain).Label.Contains("4/6"), "Gain commands display the confirmed one-based LARK level");
        Check(!TrayEntry(entries, TrayCommand.ToggleMute1).Checked && TrayEntry(entries, TrayCommand.ToggleMute2).Checked,
            "Native checkmarks represent confirmed per-transmitter mute state");
        Check(TrayEntry(entries, TrayCommand.ToggleMute2).NativeFlags == 0x8
            && TrayEntry(entries, TrayCommand.ToggleMute3).NativeFlags == 0x3
            && entries.Where(entry => entry.IsSeparator).All(entry => entry.NativeFlags == 0x800),
            "Native menu flags distinguish checked, disabled and separator items");
        Check(!TrayEntry(entries, TrayCommand.ToggleMute3).Checked && !TrayEntry(entries, TrayCommand.ToggleMute3).Enabled
            && TrayEntry(entries, TrayCommand.ToggleMute3).Label.Contains("niepołączony"),
            "Disconnected TX does not expose stale mute state or an action even if the provider gate is true");
        Check(!TrayEntry(entries, TrayCommand.ToggleMute4).Enabled, "Connected TX still respects the main-window firmware gate");
        Check(entries.Where(entry => !entry.IsSeparator).Select(entry => entry.Command).Distinct().Count() == 10,
            "Gain and four independent TX actions use distinct native command identifiers");
        for (int gain = 0; gain < 6; gain++)
        {
            entries = DesktopIntegration.CreateTrayMenu(snapshot with { ConfirmedGain = gain });
            Check(TrayEntry(entries, TrayCommand.DecreaseGain).Enabled == (gain > 0)
                && TrayEntry(entries, TrayCommand.IncreaseGain).Enabled == (gain < 5), $"Tray gain boundaries at confirmed level {gain + 1}");
        }
        foreach (int? gain in new int?[] { null, -1, 6, int.MinValue, int.MaxValue })
        {
            entries = DesktopIntegration.CreateTrayMenu(snapshot with { ConfirmedGain = gain });
            Check(!TrayEntry(entries, TrayCommand.DecreaseGain).Enabled && !TrayEntry(entries, TrayCommand.IncreaseGain).Enabled
                && TrayEntry(entries, TrayCommand.IncreaseGain).Label.Contains("brak aktualnego odczytu"),
                "Unknown or invalid gain never becomes a writable tray value");
        }
        entries = DesktopIntegration.CreateTrayMenu(snapshot, pending: true);
        Check(entries.Where(entry => !entry.IsSeparator && entry.Command is not (TrayCommand.Show or TrayCommand.Settings or TrayCommand.Exit))
            .All(entry => !entry.Enabled), "A pending action disables hardware commands and refresh without trapping navigation");
        entries = DesktopIntegration.CreateTrayMenu(snapshot with { CanRefresh = false, CanAdjustGain = false });
        Check(!TrayEntry(entries, TrayCommand.Refresh).Enabled && !TrayEntry(entries, TrayCommand.DecreaseGain).Enabled
            && !TrayEntry(entries, TrayCommand.IncreaseGain).Enabled, "Main-window busy/disconnected gates reach the native menu");
        Throws<ArgumentException>(() => DesktopIntegration.CreateTrayMenu(snapshot with { Transmitters = [new(4, true, false, true)] }),
            "Unsupported TX index cannot become a tray command");
        Throws<ArgumentException>(() => DesktopIntegration.CreateTrayMenu(snapshot with { Transmitters = [new(0, true, false, true), new(0, true, true, true)] }),
            "Duplicate TX indexes cannot share a native command");
    }

    private static void VerifyTrayControlDispatch()
    {
        TrayControlSnapshot snapshot = DesktopParitySnapshot();
        int reads = 0;
        var gains = new List<int>();
        var transmitters = new List<int>();
        using var desktop = new DesktopIntegration(DesktopExecutablePath, new DesktopParityStartupStore());
        desktop.ConfigureTrayControls(() => { reads++; return snapshot; },
            step => { gains.Add(step); return Task.CompletedTask; },
            index => { transmitters.Add(index); return Task.CompletedTask; });
        Check(reads == 0 && gains.Count == 0 && transmitters.Count == 0, "Binding tray controls performs no operation or state read");
        IDesktopIntegration legacy = new DesktopParityLegacyIntegration();
        legacy.ConfigureTrayControls(() => throw new InvalidOperationException("Default interface method must not evaluate the provider."),
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        legacy.Dispose();
        Check(true, "Existing desktop fakes need not implement the optional tray-controls method");

        Check(desktop.ExecuteTrayControlAsync(TrayCommand.DecreaseGain).GetAwaiter().GetResult()
            && desktop.ExecuteTrayControlAsync(TrayCommand.IncreaseGain).GetAwaiter().GetResult()
            && gains.SequenceEqual(new[] { -1, 1 }), "Tray dispatch delegates single gain steps to main APIs");
        Check(desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute1).GetAwaiter().GetResult()
            && desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute2).GetAwaiter().GetResult()
            && transmitters.SequenceEqual(new[] { 0, 1 }), "Tray dispatch delegates exact zero-based TX indexes");
        desktop.GetTrayMenuEntries();
        int previousReads = reads;
        snapshot = snapshot with { CanRefresh = false, CanAdjustGain = false, Transmitters = [new(0, false, false, false)] };
        Check(!desktop.ExecuteTrayControlAsync(TrayCommand.DecreaseGain).GetAwaiter().GetResult()
            && !desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute1).GetAwaiter().GetResult()
            && !desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute2).GetAwaiter().GetResult()
            && reads == previousReads + 3 && gains.Count == 2 && transmitters.Count == 2,
            "A selection rechecks fresh state instead of acting on a stale open-menu snapshot");
        Check(!desktop.ExecuteTrayControlAsync((TrayCommand)999).GetAwaiter().GetResult()
            && !desktop.ExecuteTrayControlAsync(TrayCommand.Refresh).GetAwaiter().GetResult(), "Only hardware commands reach the hardware callback dispatcher");

        snapshot = DesktopParitySnapshot();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.ConfigureTrayControls(() => snapshot, _ => completion.Task, _ => Task.CompletedTask);
        Task<bool> pending = desktop.ExecuteTrayControlAsync(TrayCommand.IncreaseGain);
        Check(!pending.IsCompleted && !desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute1).GetAwaiter().GetResult()
            && !TrayEntry(desktop.GetTrayMenuEntries(), TrayCommand.Refresh).Enabled, "Asynchronous tray actions are awaited and serialized");
        completion.SetResult();
        RunTask(pending);
        Check(pending.Result && TrayEntry(desktop.GetTrayMenuEntries(), TrayCommand.IncreaseGain).Label.Contains("4/6"),
            "Finishing a callback does not fabricate a confirmed gain update");
        desktop.ConfigureTrayControls(() => snapshot, _ => Task.FromException(new IOException("Simulated main action failure.")), _ => Task.CompletedTask);
        Throws<IOException>(() => RunTask(desktop.ExecuteTrayControlAsync(TrayCommand.DecreaseGain)), "Asynchronous callback errors are observed");
        Check(TrayEntry(desktop.GetTrayMenuEntries(), TrayCommand.IncreaseGain).Enabled, "Failed asynchronous action releases the pending gate");
        desktop.HideTray();
        Check(TrayEntry(desktop.GetTrayMenuEntries(), TrayCommand.ToggleMute1).Enabled, "Hiding the icon retains control bindings for a later show");
        desktop.Dispose();
        Check(!desktop.ExecuteTrayControlAsync(TrayCommand.ToggleMute1).GetAwaiter().GetResult(), "Disposed tray integration never invokes a device callback");
    }

    private static void VerifyFirstRunSetup()
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "wizard-save"));
        UiPreferences original = store.Load(out _) with
        {
            TextScale = 137, StartWithWindows = true, AutoConnectAtStartup = true, AutoApplySelections = true,
            CloseToTray = true, ConfirmExit = true, PreferredReceiverPath = @"\\?\hid#remembered-receiver",
            HiddenRows = ["Lights", "TX2"], AudioProfiles = [AudioProfile.FromStatus("Profil zachowany", A1Status.Parse(ValidStatus))]
        };
        var wizard = new FirstRunWindow(original, store);
        Check(!Directory.Exists(store.BaseDirectory) && wizard.SavedPreferences is null,
            "Constructing first-run setup never persists preferences, opts into startup or completes setup");
        Check(wizard.ShowInTaskbar && wizard.Owner is null && wizard.WindowStartupLocation == WindowStartupLocation.CenterScreen
            && wizard.Width == 660 && wizard.Height == 590 && wizard.MinWidth == 480 && wizard.MinHeight == 430,
            "Before test positioning, setup is an independent reference-sized centered startup window");
        MoveOffscreen(wizard);
        wizard.Show();
        Pump();
        try
        {
            VerifyFirstRunAccessibility(wizard);
            RenderWindow(wizard, "first-run-reference-137.png");
            var theme = (ComboBox)wizard.FindName("FirstRunThemeChoice");
            var scale = (ComboBox)wizard.FindName("FirstRunScaleChoice");
            Check(scale.SelectedValue is 137, "Setup preserves a valid non-preset text scale");
            Check(theme.SelectedIndex == -1 && !((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled,
                "Reference-style setup requires an explicit theme choice before Continue");
            Check(AutomationProperties.GetName((Button)wizard.FindName("SaveFirstRunButton")) == "Kontynuuj"
                && AutomationProperties.GetName((Button)wizard.FindName("CancelFirstRunButton")) == "Anuluj",
                "Setup buttons have the same concise visible and accessible captions as the reference workflow");
            Check(Descendants(wizard).OfType<AccessText>().All(text => !text.Text.Contains("System.Windows") && !text.Text.Contains("SetupChoice")),
                "Setup checkbox and button templates render captions rather than CLR object names");
            Check(theme.Items.Cast<ComboBoxItem>().Select(item => item.Content as string).SequenceEqual(new[] { "Jasny", "Ciemny" }),
                "Setup offers exactly the reference Light/Dark choices used by Settings");
            theme.SelectedValue = "Dark";
            Check(!((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled, "A theme alone does not skip explicit language selection");
            ((ComboBox)wizard.FindName("FirstRunLanguageChoice")).SelectedValue = "pl";
            Check(((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled, "Choosing language and theme enables Continue");
            scale.SelectedValue = 200;
            ((ComboBox)wizard.FindName("FirstRunValueDisplayChoice")).SelectedValue = ValueDisplayMode.StaticText;
            var hints = (CheckBox)wizard.FindName("FirstRunHintsCheckBox");
            hints.IsChecked = false;
            ((CheckBox)wizard.FindName("FirstRunMessagesCheckBox")).IsChecked = false;
            ((CheckBox)wizard.FindName("FirstRunAnnouncementsCheckBox")).IsChecked = false;
            Pump();
            Check(wizard.ThemeMode == ThemeMode.Light && Math.Abs(wizard.FontSize - SystemFonts.MessageFontSize * 2) < 0.01,
                "Theme remains staged while text scale previews only in the setup window");
            Check(!UsageHints.GetEnabled(wizard) && AutomationProperties.GetHelpText(theme) == string.Empty,
                "Disabling setup hints removes optional theme guidance immediately");
            RenderWindow(wizard, "first-run-reference-200.png");
            foreach (string identifier in new[] { "FirstRunHintsCheckBox", "FirstRunMessagesCheckBox", "FirstRunAnnouncementsCheckBox" })
            {
                var control = (CheckBox)wizard.FindName(identifier);
                Check(AutomationProperties.GetHelpText(control) == string.Empty && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)),
                    "Disabling setup hints removes optional spoken help but preserves the essential name: " + identifier);
            }
            Check(!((TextBlock)wizard.FindName("FirstRunScope")).IsVisible, "Optional setup instructions follow the hints preference");
            hints.IsChecked = true;
            Pump();
            Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(theme)) && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(hints)),
                "Setup guidance can be restored before saving without adding non-reference introduction blocks");
            hints.IsChecked = false;
            Check(original.Theme == "Light" && original.TextScale == 137 && original.ShowHints
                && original.ShowOperationMessages && original.AnnounceStateChanges && !original.FirstRunCompleted
                && !File.Exists(store.FilePath), "All setup choices remain a local draft before Save");
            ClickFirstRun(wizard, "SaveFirstRunButton");
            UiPreferences expected = original with
            {
                Theme = "Dark", TextScale = 200, ShowHints = false, ShowOperationMessages = false,
                AnnounceStateChanges = false, ValueDisplayMode = ValueDisplayMode.StaticText, FirstRunCompleted = true
            };
            Check(!wizard.IsVisible && wizard.SavedPreferences is UiPreferences saved && EquivalentExpandedPreferences(expected, saved),
                "Save exposes exactly the completed preferences for the lead's in-memory integration");
            Check(wizard.SaveWarning is null && !store.ShouldOfferFirstRun
                && EquivalentExpandedPreferences(expected, new UiPreferencesStore(store.BaseDirectory).Load(out string? error)) && error is null,
                "Setup saves all appearance/feedback choices including value presentation without altering device/startup preferences");
            Check(original.HiddenRows.SequenceEqual(new[] { "Lights", "TX2" }) && original.AudioProfiles.Length == 1,
                "First-run draft never mutates the caller's preference collections");
        }
        finally { if (wizard.IsVisible) wizard.Close(); Pump(); }

        VerifyFirstRunCancellation(original);
        VerifyFirstRunSaveFailure();
    }

    private static void VerifyFirstRunAccessibility(FirstRunWindow wizard)
    {
        var layout = (Grid)wizard.Content;
        var introduction = (TextBlock)wizard.FindName("FirstRunScope");
        var result = (TextBlock)wizard.FindName("SectionResultReadout");
        var theme = (ComboBox)wizard.FindName("FirstRunThemeChoice");
        Check(layout.Margin == new Thickness(20) && layout.RowDefinitions.Count == 4
            && ReferenceEquals(layout.Children[0], introduction) && Grid.GetRow(introduction) == 0
            && introduction.Margin == new Thickness(0, 0, 0, 14)
            && introduction.FontWeight == FontWeights.Normal,
            "Setup uses the reference single plain introduction outside its scrolling body");
        Check(theme.Parent is StackPanel panel && panel.Margin == new Thickness(0)
            && panel.Children.OfType<TextBlock>().Count() == 0 && panel.Parent is ScrollViewer scroll
            && scroll.Parent is DockPanel body && Grid.GetRow(body) == 1,
            "Setup scrolling content starts directly with labelled choices, without extra heading or hint blocks");
        Check(Grid.GetRow(result) == 2 && result.Focusable && KeyboardNavigation.GetIsTabStop(result)
            && result.Margin == new Thickness(0, 12, 0, 12) && result.Padding == new Thickness(0)
            && result.MinHeight == 0 && double.IsPositiveInfinity(result.MaxHeight),
            "Setup errors use a focusable, auto-sized static readout rather than an edit field");
        Check(result.Text.Length == 0 && !result.IsVisible && !Descendants(wizard).OfType<TextBox>().Any(),
            "Initial setup adds neither instructional result text nor read-only edit fields");
        Check(Grid.GetRow((UIElement)((Button)wizard.FindName("SaveFirstRunButton")).Parent) == 3,
            "Setup action buttons remain in the reference footer outside the scrollable choices");
        foreach (string identifier in new[] { "FirstRunLanguageChoice", "FirstRunThemeChoice", "FirstRunScaleChoice", "FirstRunValueDisplayChoice" })
            Check(((ComboBox)wizard.FindName(identifier)).Margin == new Thickness(3), "Setup dropdown uses reference spacing: " + identifier);
        Check(((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).Margin == new Thickness(3, 16, 3, 16)
            && ((CheckBox)wizard.FindName("FirstRunMessagesCheckBox")).Margin == new Thickness(3, 8, 3, 8)
            && ((CheckBox)wizard.FindName("FirstRunAnnouncementsCheckBox")).Margin == new Thickness(3, 8, 3, 8),
            "Setup hint and feedback checkboxes use their distinct reference vertical margins");
        Check(wizard.Owner is null && wizard.Width == 660 && wizard.Height == 590 && wizard.MinWidth == 480 && wizard.MinHeight == 430
            && string.Equals(wizard.Language.IetfLanguageTag, "pl-PL", StringComparison.OrdinalIgnoreCase)
            && KeyboardNavigation.GetTabNavigation(wizard) == KeyboardNavigationMode.Cycle,
            "Setup is an independent reference-sized Polish startup window, not an owned main-window section");
        string[] identifiers =
        [
            "FirstRunLanguageChoice", "FirstRunThemeChoice", "FirstRunScaleChoice", "FirstRunHintsCheckBox", "FirstRunMessagesCheckBox",
            "FirstRunValueDisplayChoice", "FirstRunAnnouncementsCheckBox", "SaveFirstRunButton", "CancelFirstRunButton"
        ];
        foreach (string identifier in identifiers)
        {
            var control = (Control)wizard.FindName(identifier);
            Check(control.IsVisible && (control.IsEnabled || identifier == "SaveFirstRunButton") && control.Focusable && control.IsTabStop
                && AutomationProperties.GetAutomationId(control) == identifier
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), "Setup control is named and keyboard-reachable: " + identifier);
            AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(control)!;
            PatternInterface pattern = control is ComboBox ? PatternInterface.Selection
                : control is CheckBox ? PatternInterface.Toggle : PatternInterface.Invoke;
            Check(peer.GetPattern(pattern) is not null, "Setup exposes the expected UIA interaction pattern: " + identifier);
            if (control is ComboBox)
                Check(AutomationProperties.GetLabeledBy(control) is Label label && ReferenceEquals(label.Target, control),
                    "Setup list label targets its editor: " + identifier);
        }
        Check(Descendants(wizard).OfType<Control>().Where(control => identifiers.Contains(control.Name))
            .Select(control => control.Name).SequenceEqual(identifiers), "Setup traversal follows reference appearance, hints, messages, value display, announcements, Continue and Cancel");
        Check(((Button)wizard.FindName("SaveFirstRunButton")).IsDefault, "Setup has an Enter/default save action");
        Check(Descendants(wizard).OfType<ScrollViewer>().Any(scroll => scroll.Content is StackPanel
            && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto), "Setup remains scrollable at large text scales");
    }

    private static void VerifyFirstRunCancellation(UiPreferences original)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "wizard-discard"));
        Check(store.TrySave(original, out _), "A pre-existing configuration is available for setup cancellation checks");
        byte[] before = File.ReadAllBytes(store.FilePath);
        foreach (string action in new[] { "cancel", "escape", "close" })
        {
            var wizard = new FirstRunWindow(original, store);
            MoveOffscreen(wizard);
            wizard.Show();
            Pump();
            try
            {
                ((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedValue = "Light";
                ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked = false;
                if (action == "cancel") ClickFirstRun(wizard, "CancelFirstRunButton");
                else if (action == "escape")
                {
                    var eventArgs = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(wizard), 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    };
                    wizard.RaiseEvent(eventArgs);
                    Check(!eventArgs.Handled && wizard.IsVisible && wizard.SavedPreferences is null && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                        "Setup Escape leaves the wizard and its unsaved draft open like the reference");
                    ClickFirstRun(wizard, "CancelFirstRunButton");
                }
                else wizard.Close();
                Pump();
                Check(!wizard.IsVisible && wizard.SavedPreferences is null && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
                    "Setup discard leaves existing settings byte-for-byte unchanged: " + action);
            }
            finally { if (wizard.IsVisible) wizard.Close(); Pump(); }
        }
        var absentStore = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "wizard-cancel-new"));
        var cancelled = new FirstRunWindow(absentStore.Load(out _), absentStore);
        cancelled.Close();
        Check(cancelled.SavedPreferences is null && absentStore.ShouldOfferFirstRun && !Directory.Exists(absentStore.BaseDirectory),
            "Cancelling a new setup creates no file and does not mark first run completed");
    }

    private static void VerifyFirstRunSaveFailure()
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "desktop-parity", "wizard-blocked"));
        UiPreferences preferences = store.Load(out _) with { ShowHints = false, ShowOperationMessages = false, AnnounceStateChanges = false };
        Directory.CreateDirectory(store.BaseDirectory);
        string blocker = Path.Combine(store.BaseDirectory, "data");
        File.WriteAllText(blocker, "This diagnostic fixture intentionally blocks the settings directory.");
        var wizard = new FirstRunWindow(preferences, store);
        MoveOffscreen(wizard);
        wizard.Show();
        Pump();
        try
        {
            ((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedValue = "Dark";
            ((ComboBox)wizard.FindName("FirstRunLanguageChoice")).SelectedValue = "pl";
            ClickFirstRun(wizard, "SaveFirstRunButton");
            var result = (TextBlock)wizard.FindName("SectionResultReadout");
            Check(wizard.IsVisible && wizard.SavedPreferences is null && store.ShouldOfferFirstRun
                && !File.Exists(store.FilePath), "An unsuccessful first-run save leaves the draft open and incomplete");
            Check(result.IsVisible && result.Focusable && GetAccessiblePeer(result).GetPattern(PatternInterface.Value) is null && !string.IsNullOrWhiteSpace(result.Text)
                && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                "Setup save errors remain visible, readable and announced despite all optional feedback being disabled");
            Check(((ComboBox)wizard.FindName("FirstRunThemeChoice")).SelectedValue is "Dark"
                && ((Button)wizard.FindName("SaveFirstRunButton")).IsEnabled, "A save failure preserves edits and allows retry or cancellation");
            ClickFirstRun(wizard, "CancelFirstRunButton");
            Check(File.ReadAllText(blocker) == "This diagnostic fixture intentionally blocks the settings directory."
                && wizard.SavedPreferences is null, "Failed-save cancellation neither changes the blocker nor reports completion");
        }
        finally { if (wizard.IsVisible) wizard.Close(); Pump(); }
    }

    private static void ClickFirstRun(FirstRunWindow window, string identifier)
    {
        ((Button)window.FindName(identifier)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private sealed class DesktopParityStartupStore : IStartupRegistrationStore
    {
        public StartupRegistrationSnapshot Read() => throw new InvalidOperationException("Tray tests must not read startup registration.");
        public void Write(object rawValue, RegistryValueKind valueKind) => throw new InvalidOperationException("Tray tests must not write startup registration.");
        public void Delete() => throw new InvalidOperationException("Tray tests must not delete startup registration.");
    }

    private sealed class DesktopParityLegacyIntegration : IDesktopIntegration
    {
        public StartupRegistrationSnapshot CaptureStartup() => throw new InvalidOperationException();
        public void SetStartup(bool enabled) => throw new InvalidOperationException();
        public void RestoreStartup(StartupRegistrationSnapshot snapshot) => throw new InvalidOperationException();
        public void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit) => throw new InvalidOperationException();
        public void HideTray() { }
        public void Dispose() { }
    }
}
