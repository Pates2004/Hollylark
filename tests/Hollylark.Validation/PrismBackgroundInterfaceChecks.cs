using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    public static void VerifyPrismBackgroundInterfaceChecks()
    {
        if (Application.Current is null || Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown
            || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run Prism interface checks inside the parent's explicit-shutdown STA WPF application.");

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyPrismBackgroundStates(language);
                VerifyPrismDisabledPreference(language);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
        Console.WriteLine("Prism interface coverage uses owned WPF windows, simulated receivers, memory desktop and fake speech only; no native speech or global input.");
    }

    private static void VerifyPrismBackgroundStates(string language)
    {
        using var fixture = new PrismBackgroundFixture(language, announceChanges: true);
        fixture.Start();
        using var other = new PrismOwnedFocusWindow();
        TextBlock mainFocus = FindById<TextBlock>(fixture.Window, "ConnectionReadout");
        fixture.Window.Activate();
        FocusForKeyboardCheck(mainFocus);
        Check(fixture.Window.IsActive && !other.Window.IsActive, "Prism foreground fixture really activates the main window: " + language);
        VerifyPrismPollChanges(fixture, fixture.Window, mainFocus, gain: 4, muted: true, "active/" + language);

        other.Activate();
        Check(!fixture.Window.OwnedWindows.Cast<Window>().Contains(other.Window) && other.Window.Owner is null,
            "Prism background focus belongs to a separate test-owned window outside the main window's auxiliaries: " + language);
        VerifyPrismPollChanges(fixture, other.Window, other.FocusTarget, gain: 2, muted: false, "inactive/" + language);

        fixture.Window.WindowState = WindowState.Minimized;
        Pump();
        other.Activate();
        Check(fixture.Window.WindowState == WindowState.Minimized && !fixture.Window.IsActive,
            "Prism minimized fixture is genuinely minimized and inactive: " + language);
        VerifyPrismPollChanges(fixture, other.Window, other.FocusTarget, gain: 1, muted: true, "minimized/" + language);

        fixture.Window.WindowState = WindowState.Normal;
        fixture.Window.Hide();
        Pump();
        other.Activate();
        Check(!fixture.Window.IsVisible && !fixture.Window.IsActive, "Prism hidden fixture is genuinely hidden and inactive: " + language);
        VerifyPrismPollChanges(fixture, other.Window, other.FocusTarget, gain: 5, muted: false, "hidden/" + language);

        fixture.PreserveFocus(other.Window, other.FocusTarget, () =>
        {
            int spoken = fixture.Speech.Messages.Count;
            SavePreferences(fixture.Window, fixture.Window.CurrentPreferences with { AnnounceStateChanges = false });
            SetAutomationStatusByte(fixture.Transport, 8, 0);
            SetAutomationStatusByte(fixture.Transport, 4, 1);
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Count == spoken && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 0
                && fixture.Window.Transmitters[0].IsMuted,
                "Disabling announcements silences confirmed hidden gain and mute changes without suppressing state updates: " + language);
            SavePreferences(fixture.Window, fixture.Window.CurrentPreferences with { AnnounceStateChanges = true });
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Count == spoken, "Re-enabling announcements does not replay changes observed while disabled: " + language);
        }, "hidden preference changes/" + language);

        VerifyPrismImportantHotkeyRefusal(fixture, other, language);
        fixture.PreserveFocus(other.Window, other.FocusTarget, fixture.CloseAndCheckCallbacks, "hidden disposal/" + language);
    }

    private static void VerifyPrismPollChanges(PrismBackgroundFixture fixture, Window activeWindow, UIElement focusedElement,
        byte gain, bool muted, string scenario)
    {
        fixture.PreserveFocus(activeWindow, focusedElement, () =>
        {
            int spoken = fixture.Speech.Messages.Count;
            SetAutomationStatusByte(fixture.Transport, 8, gain);
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Skip(spoken).SequenceEqual(new[] { Localization.Text("Main.State.Gain", gain + 1) })
                && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == gain,
                "A confirmed gain change sends exactly one localized direct announcement: " + scenario);

            spoken = fixture.Speech.Messages.Count;
            SetAutomationStatusByte(fixture.Transport, 4, muted ? (byte)1 : (byte)0);
            fixture.PollOnce();
            string expected = Localization.Text("Main.State.Transmitter", 1,
                Localization.Text(muted ? "Transmitter.Muted" : "Transmitter.Active"));
            Check(fixture.Speech.Messages.Skip(spoken).SequenceEqual(new[] { expected })
                && fixture.Window.Transmitters[0].IsMuted == muted,
                "A confirmed mute change sends exactly one localized direct announcement: " + scenario);

            spoken = fixture.Speech.Messages.Count;
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Count == spoken, "Unchanged polling remains silent: " + scenario);
            SetAutomationStatusByte(fixture.Transport, 2, (byte)(gain + 80));
            SetAutomationStatusByte(fixture.Transport, 3, (byte)(gain + 40));
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Count == spoken, "Battery-only polling remains silent: " + scenario);
            Check(fixture.Window.CurrentPreferences.AnnounceStateChanges && !fixture.Window.ShowHints
                && !fixture.Window.CurrentPreferences.ShowOperationMessages,
                "Direct state announcements respect their own preference with hints and operation messages disabled: " + scenario);
        }, scenario);
    }

    private static void VerifyPrismImportantHotkeyRefusal(PrismBackgroundFixture fixture, PrismOwnedFocusWindow other, string language)
    {
        fixture.PreserveFocus(other.Window, other.FocusTarget, () =>
        {
            SavePreferences(fixture.Window, fixture.Window.CurrentPreferences with
            {
                AnnounceStateChanges = false, GlobalHotkeysEnabled = true
            });
            ListBox targets = FindById<ListBox>(fixture.Window, "TransmitterList");
            targets.SelectedItem = fixture.Window.TransmitterTargets.Single(target => target.IsGroup);
            SettingRowViewModel gain = fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain");
            gain.SelectedIndex = (gain.SelectedIndex + 1) % gain.Options.Count;
            Check(gain.IsDirty, "The important shortcut fixture contains a legitimate unapplied group gain draft: " + language);
            int commands = fixture.Transport.Commands.Count;
            int spoken = fixture.Speech.Messages.Count;
            RunTask(fixture.Window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus));
            string expected = Localization.Text("Hotkeys.Drafts");
            Check(fixture.Window.ResultText == expected && fixture.Speech.Messages.Skip(spoken).SequenceEqual(new[] { expected })
                && fixture.Transport.Commands.Count == commands && gain.IsDirty,
                "A hidden important shortcut refusal uses direct speech once with all optional announcements disabled and leaves the draft untouched: " + language);
        }, "hidden important refusal/" + language);
    }

    private static void VerifyPrismDisabledPreference(string language)
    {
        using var fixture = new PrismBackgroundFixture(language, announceChanges: false);
        fixture.Start();
        using var other = new PrismOwnedFocusWindow();
        fixture.Window.Hide();
        other.Activate();
        fixture.PreserveFocus(other.Window, other.FocusTarget, () =>
        {
            SetAutomationStatusByte(fixture.Transport, 8, 2);
            SetAutomationStatusByte(fixture.Transport, 4, 1);
            fixture.PollOnce();
            Check(!fixture.Window.CurrentPreferences.AnnounceStateChanges && fixture.Speech.Messages.Count == 0
                && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 2
                && fixture.Window.Transmitters[0].IsMuted,
                "An existing saved false announcement preference is honored from startup for hidden state changes: " + language);
            SavePreferences(fixture.Window, fixture.Window.CurrentPreferences with { AnnounceStateChanges = true });
            fixture.PollOnce();
            Check(fixture.Speech.Messages.Count == 0, "Enabling the saved-false preference is quiet until another confirmed change: " + language);
            SetAutomationStatusByte(fixture.Transport, 8, 4);
            SetAutomationStatusByte(fixture.Transport, 4, 0);
            fixture.PollOnce();
            string expected = Localization.Text("Main.State.Gain", 5) + ". "
                + Localization.Text("Main.State.Transmitter", 1, Localization.Text("Transmitter.Active"));
            Check(fixture.Speech.Messages.SequenceEqual(new[] { expected }) && !fixture.Window.ShowHints
                && !fixture.Window.CurrentPreferences.ShowOperationMessages,
                "Enabling announcements delivers one combined localized hidden gain/mute message with hints and operation messages still disabled: " + language);
        }, "saved preference/" + language);
        fixture.PreserveFocus(other.Window, other.FocusTarget, fixture.CloseAndCheckCallbacks, "saved preference disposal/" + language);
    }

    private sealed class PrismBackgroundFixture : IDisposable
    {
        private readonly MemoryDesktop desktop = new();
        private readonly FakeHotkeyNativeApi hotkeys = new();
        private readonly List<string> desktopErrors = [];
        private int activations;
        internal FakeTransport Transport { get; } = new();
        internal PrismInterfaceSpeech Speech { get; } = new();
        internal MainWindow Window { get; }

        internal PrismBackgroundFixture(string language, bool announceChanges)
        {
            string directory = Path.Combine(ReportDirectory, "prism-background", language,
                (announceChanges ? "enabled-" : "disabled-") + Guid.NewGuid().ToString("N"));
            var store = new UiPreferencesStore(directory);
            Check(store.TrySave(new UiPreferences
            {
                FirstRunCompleted = true, Language = language, ConfirmExit = false, CloseToTray = false,
                StartWithWindows = false, ShowHints = false, ShowOperationMessages = false,
                AnnounceStateChanges = announceChanges, PreserveVolatileSettings = false,
                GlobalHotkeysEnabled = false, Hotkeys = HotkeyTestBindings()
            }, out string? error), "Prepare isolated Prism interface preferences: " + error);
            var receiver = new ReceiverInfo("Simulated Prism receiver", "simulated://prism-" + Guid.NewGuid().ToString("N"), 0, 64);
            Window = new MainWindow(() => [receiver], requested => Transport, directory, desktop,
                (message, title) => false, reportDesktopError: desktopErrors.Add,
                automaticReceiverManagement: false, hotkeyNativeApi: hotkeys, screenReader: Speech);
            Window.Activated += OnActivated;
            MoveOffscreen(Window);
        }

        internal void Start()
        {
            Window.Show();
            PumpUntil(() => !Window.IsBusy && Window.Receivers.Count == 1 && UsbField<bool>(Window, "loaded"));
            RunTask(Window.ConnectAsync());
            StopTimers();
            Check(Window.IsConnected && Speech.Messages.Count == 0 && Speech.DisposeCalls == 0,
                "The initial connected receiver snapshot never requests direct speech");
            PollOnce();
            Check(Speech.Messages.Count == 0, "The initial unchanged poll never requests direct speech");
        }

        internal void PollOnce()
        {
            StopTimers();
            int reads = Transport.Commands.Count(command => command == A1Command.GetStatus);
            RunTask(Poll(Window));
            StopTimers();
            Check(Window.IsConnected && Transport.Commands.Count(command => command == A1Command.GetStatus) == reads + 1,
                "The Prism fixture observes exactly one fresh simulated status per requested poll");
            Check(Transport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "Prism state polling never writes receiver settings");
        }

        internal void PreserveFocus(Window activeWindow, UIElement focusedElement, Action operation, string scenario)
        {
            Check(activeWindow.IsActive && ReferenceEquals(Keyboard.FocusedElement, focusedElement),
                "Prism focus-preservation precondition holds: " + scenario);
            bool visible = Window.IsVisible;
            WindowState state = Window.WindowState;
            int beforeActivations = activations;
            int foregroundActivations = 0;
            void CountForegroundActivation(object? sender, EventArgs eventArgs) => foregroundActivations++;
            activeWindow.Activated += CountForegroundActivation;
            try { operation(); Pump(); }
            finally { activeWindow.Activated -= CountForegroundActivation; }
            Check(activeWindow.IsActive && ReferenceEquals(Keyboard.FocusedElement, focusedElement)
                && activations == beforeActivations && foregroundActivations == 0
                && Window.IsVisible == visible && Window.WindowState == state,
                "Prism announcements preserve the active window, keyboard focus, visibility and state without transient activation: " + scenario);
        }

        internal void CloseAndCheckCallbacks()
        {
            int messages = Speech.Messages.Count;
            int commands = Transport.Commands.Count;
            Task[] queuedActions = [];
            DispatcherOperation queued = Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                SetAutomationStatusByte(Transport, 8, 5);
                queuedActions = [Poll(Window), Window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus)];
                Window.AnnounceOperationResult("queued after close", important: true);
            }));
            Window.RequestExit();
            Window.RequestExit();
            RunTask(queued.Task);
            RunTask(Task.WhenAll(queuedActions));
            Pump();
            Check(queuedActions.Length == 2 && Speech.DisposeCalls == 1 && Speech.CallsAfterDispose == 0
                && Speech.Messages.Count == messages && Transport.Commands.Count == commands
                && Transport.Disposed && desktop.Disposed && !UsbField<DispatcherTimer>(Window, "pollTimer").IsEnabled
                && !UsbField<DispatcherTimer>(Window, "receiverTimer").IsEnabled,
                "Closing disposes injected speech exactly once and queued polls, shortcuts and operation feedback produce no further callbacks or device reads");
        }

        private void OnActivated(object? sender, EventArgs eventArgs) => activations++;

        private void StopTimers()
        {
            UsbField<DispatcherTimer>(Window, "pollTimer").Stop();
            UsbField<DispatcherTimer>(Window, "receiverTimer").Stop();
            UsbField<DispatcherTimer>(Window, "autoApplyTimer").Stop();
        }

        public void Dispose()
        {
            try
            {
                Window.RequestExit();
                RunTask(Window.ReceiverSessionTask);
                Pump();
            }
            finally
            {
                Window.Activated -= OnActivated;
                Transport.Dispose();
            }
            Check(Speech.DisposeCalls == 1 && Speech.CallsAfterDispose == 0 && desktop.Disposed
                && desktop.StartupWrites == 0 && desktop.TrayShows == 0 && desktopErrors.Count == 0
                && hotkeys.Calls.Count == 0 && hotkeys.Registered.Count == 0,
                "Prism fixture cleanup uses only fake speech, fake shortcuts and memory desktop without startup, tray or native speech effects");
        }
    }

    private sealed class PrismOwnedFocusWindow : IDisposable
    {
        internal TextBox FocusTarget { get; } = new() { Text = "Prism independent focus target", IsReadOnly = true };
        internal Window Window { get; }

        internal PrismOwnedFocusWindow()
        {
            Window = new Window { Title = "Prism owned focus fixture", Content = FocusTarget, Width = 320, Height = 160 };
            MoveOffscreen(Window);
            Window.Show();
            Pump();
        }

        internal void Activate()
        {
            Window.Activate();
            FocusForKeyboardCheck(FocusTarget);
            Check(Window.IsActive && ReferenceEquals(Keyboard.FocusedElement, FocusTarget),
                "The separate Prism test-owned window really holds activation and keyboard focus");
        }

        public void Dispose() { Window.Close(); Pump(); }
    }

    private sealed class PrismInterfaceSpeech : IScreenReaderSpeech
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        internal List<string> Messages { get; } = [];
        internal int DisposeCalls { get; private set; }
        internal int CallsAfterDispose { get; private set; }

        public bool TrySpeak(string message)
        {
            if (DisposeCalls != 0)
            {
                CallsAfterDispose++;
                throw new ObjectDisposedException(nameof(PrismInterfaceSpeech));
            }
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("The fake speech callback left the WPF thread.");
            Messages.Add(message);
            return true;
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("The fake speech service was disposed outside the WPF thread.");
            DisposeCalls++;
        }
    }
}
