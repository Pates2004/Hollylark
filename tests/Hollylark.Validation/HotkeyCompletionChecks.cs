using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyHotkeyCompletionChecks()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (HotkeyAction action in new[] { HotkeyAction.ReadStatus, HotkeyAction.IncreaseGain,
                    HotkeyAction.ToggleSelectedMute, HotkeyAction.ToggleAllMute })
                    foreach (string completion in new[] { "normal", "operation", "receiver" })
                        VerifyControlledHotkeyCompletion(action, completion);
                VerifyHotkeyInitialDiscoveryCompletion();
                VerifyHotkeyReadFailureCompletion();
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyControlledHotkeyCompletion(HotkeyAction action, string completion)
    {
        string scenario = Localization.CurrentLanguage + "/" + action + "/" + completion;
        using var fixture = new HotkeyCompletionFixture(action == HotkeyAction.ToggleSelectedMute);
        MainWindow window = fixture.Window;
        fixture.Start();
        if (action == HotkeyAction.ToggleSelectedMute)
            ((ListBox)window.FindName("TransmitterList")).SelectedItem = window.Transmitters[3];
        var context = new PeakReviewStepContext();
        Task? pending = null;
        try
        {
            fixture.Initial.PauseNextStatusRead();
            SynchronizationContext? normalContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                pending = window.ExecuteHotkeyAsync(action);
            }
            finally { SynchronizationContext.SetSynchronizationContext(normalContext); }
            PumpUntil(() => fixture.Initial.HasPausedStatusRead);
            Check(window.IsBusy && !pending.IsCompleted,
                "The shortcut awaits a real withheld simulated response: " + scenario);
            fixture.Initial.ReleaseStatusRead();
            PumpUntil(() =>
            {
                if (window.IsBusy) context.RunNext();
                return !window.IsBusy;
            });
            Check(!pending.IsCompleted && context.HasCallbacks,
                "The device operation completes before the shortcut feedback continuation: " + scenario);
            int ownWrites = CountMockWrites(fixture.Initial.Inner);
            Check(ownWrites == (action == HotkeyAction.ReadStatus ? 0 : 1),
                "The shortcut confirms only its intended setting or read before feedback: " + scenario);

            string expectedResult;
            if (completion == "operation")
            {
                long generationBefore = HotkeyCompletionField<long>(window, "generation");
                long operationBefore = HotkeyCompletionField<long>(window, "operation");
                ((ListBox)window.FindName("TransmitterList")).SelectedItem = window.TransmitterTargets.Last();
                SettingRowViewModel gain = window.AudioSettings.Single(row => row.Id == "Gain");
                gain.SelectedIndex = 1;
                RunTask(window.ApplySettingAsync(gain));
                Check(HotkeyCompletionField<long>(window, "generation") == generationBefore
                    && HotkeyCompletionField<long>(window, "operation") == operationBefore + 1
                    && CountMockWrites(fixture.Initial.Inner) == ownWrites + 1,
                    "The newer operation supersedes feedback within the same receiver session: " + scenario);
                expectedResult = window.ResultText;
            }
            else if (completion == "receiver")
            {
                fixture.ReplaceReceiver();
                Check(window.IsConnected && window.Transmitters.Count == 2 && fixture.Initial.Inner.Disposed
                    && window.AudioSettings.Single(row => row.Id == "Gain").SelectedIndex == 1,
                    "A different two-slot receiver is active before the old shortcut resumes: " + scenario);
                expectedResult = window.ResultText;
            }
            else
            {
                SettingRowViewModel gain = window.AudioSettings.Single(row => row.Id == "Gain");
                expectedResult = action switch
                {
                    HotkeyAction.IncreaseGain => Localization.Text("Setting.Value", gain.DisplayName, gain.ConfirmedValue),
                    HotkeyAction.ToggleSelectedMute => Localization.Text("Main.Mute.Confirmed", 4),
                    HotkeyAction.ToggleAllMute => Localization.Text("Target.All.MutedConfirmed"),
                    _ => window.ResultText
                };
            }
            int messagesBefore = fixture.Messages.Count;
            PumpUntil(() => { context.RunNext(); return pending.IsCompleted; });
            pending.GetAwaiter().GetResult();
            Check(window.ResultText == expectedResult,
                "Shortcut completion preserves the result belonging to the current operation: " + scenario);
            Check(fixture.Messages.Count == messagesBefore + (completion == "normal" ? 1 : 0)
                && (completion != "normal" || fixture.Messages[^1] == expectedResult),
                "Only a current shortcut emits its normal background feedback: " + scenario);
            Check(CountMockWrites(fixture.Initial.Inner) == ownWrites + (completion == "operation" ? 1 : 0)
                && CountMockWrites(fixture.Replacement) == 0,
                "Resuming old feedback neither repeats commands nor writes to the replacement receiver: " + scenario);
        }
        finally
        {
            window.RequestExit();
            if (pending is not null && !pending.IsCompleted)
                PumpUntil(() => { context.RunNext(); return pending.IsCompleted; });
            Pump();
        }
    }

    private static void VerifyHotkeyInitialDiscoveryCompletion()
    {
        using var fixture = new HotkeyCompletionFixture(false, automaticReceiverManagement: true);
        MainWindow window = fixture.Window;
        var context = new PeakReviewStepContext();
        Task? pending = null;
        try
        {
            Check(!window.IsConnected && !window.IsVisible,
                "Initial read shortcut starts without a session or a native window");
            fixture.Initial.PauseNextStatusRead();
            SynchronizationContext? normalContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                pending = window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus);
            }
            finally { SynchronizationContext.SetSynchronizationContext(normalContext); }
            PumpUntil(() => { context.RunNext(); return fixture.Initial.HasPausedStatusRead; });
            Check(window.IsBusy && !pending.IsCompleted,
                "The initial shortcut discovers the fake receiver and waits for its first status");
            fixture.Initial.ReleaseStatusRead();
            PumpUntil(() => { context.RunNext(); return pending.IsCompleted; });
            pending.GetAwaiter().GetResult();
            fixture.StopTimers();
            string expected = Localization.Text("Main.Usb.Ready", fixture.InitialReceiver.Name, 2, 2);
            Check(window.IsConnected && !window.IsBusy && window.ResultText == expected
                && fixture.Messages.Count == 1 && fixture.Messages[0] == expected,
                "The read shortcut accepts and announces the first session established by its own discovery");
            Check(CountMockWrites(fixture.Initial.Inner) == 0,
                "Initial shortcut discovery performs only status and firmware reads");
        }
        finally
        {
            window.RequestExit();
            if (pending is not null && !pending.IsCompleted)
                PumpUntil(() => { context.RunNext(); return pending.IsCompleted; });
            Pump();
        }
    }

    private static TValue HotkeyCompletionField<TValue>(MainWindow window, string name) =>
        (TValue)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void VerifyHotkeyReadFailureCompletion()
    {
        using var transport = new RetentionHotplugTransport(gain: 3);
        using var fixture = new HotkeyCompletionFixture(false, initialTransport: transport);
        fixture.Start();
        transport.FailNextStatusRead();
        RunTask(fixture.Window.ExecuteHotkeyAsync(HotkeyAction.ReadStatus));
        string expected = Localization.Text("Main.Session.Closed", Localization.Text("Main.Session.Failed"));
        Check(transport.ReadFailures == 1 && transport.Disposed && !fixture.Window.IsConnected && !fixture.Window.IsBusy
            && fixture.Window.ResultText == expected,
            "An owned shortcut read failure closes only its failed session and reports the actual error");
        Check(fixture.Messages.Count == 1 && fixture.Messages[0] == expected && transport.SettingFrames.Length == 0,
            "Closing the failed session does not suppress or duplicate its essential background shortcut notification");
    }

    private sealed class HotkeyCompletionFixture : IDisposable
    {
        private readonly UiPreferencesStore store;
        private readonly byte[] originalPreferences;
        private readonly FakeHotkeyNativeApi native = new();
        private readonly MemoryDesktop desktop = new();
        private readonly IA1Transport initialConnection;
        private ReceiverInfo[] discovered;

        internal PeakReviewPausedTransport Initial { get; } = new();
        internal FakeTransport Replacement { get; } = new();
        internal ReceiverInfo InitialReceiver { get; } = new("Simulated original shortcut receiver", "simulated://hotkey-original", 0, 64);
        private ReceiverInfo ReplacementReceiver { get; } = new("Simulated replacement shortcut receiver", "simulated://hotkey-replacement", 0, 64);
        internal MainWindow Window { get; }
        internal List<string> Messages { get; } = [];

        internal HotkeyCompletionFixture(bool fourTransmitters, bool automaticReceiverManagement = false,
            IA1Transport? initialTransport = null)
        {
            initialConnection = initialTransport ?? Initial;
            if (fourTransmitters)
            {
                Initial.Inner.StatusPayloadLength = 23;
                Initial.Inner.SetTransmitterConnected(2, true);
                Initial.Inner.SetTransmitterConnected(3, true);
                SetAutomationStatusByte(Initial.Inner, 19, 90);
                SetAutomationStatusByte(Initial.Inner, 20, 90);
            }
            SetAutomationStatusByte(Replacement, 8, 1);
            discovered = [InitialReceiver];
            store = new UiPreferencesStore(Path.Combine(ReportDirectory, "hotkey-completion-" + Guid.NewGuid().ToString("N")));
            Check(store.TrySave(new UiPreferences
            {
                FirstRunCompleted = true, Language = Localization.CurrentLanguage, ConfirmExit = false,
                GlobalHotkeysEnabled = true, PreserveVolatileSettings = false, ShowHints = false,
                ShowOperationMessages = false, AnnounceStateChanges = true
            }, out string? error), "Prepare isolated hotkey completion preferences: " + error);
            originalPreferences = File.ReadAllBytes(store.FilePath);
            Window = new MainWindow(() => discovered, receiver => receiver.Path == InitialReceiver.Path ? initialConnection : Replacement,
                store.BaseDirectory, desktop, (_, _) => true, announceState: Messages.Add,
                automaticReceiverManagement: automaticReceiverManagement, hotkeyNativeApi: native);
        }

        internal void Start()
        {
            RunTask(Window.ScanAsync());
            RunTask(Window.ConnectAsync());
            StopTimers();
            Check(Window.IsConnected && !Window.IsBusy && !Window.IsVisible,
                "Hotkey completion fixture connects without showing a native window");
        }

        internal void ReplaceReceiver()
        {
            discovered = [ReplacementReceiver];
            RunTask(Window.ScanAsync());
            RunTask(Window.ConnectAsync());
            StopTimers();
        }

        internal void StopTimers()
        {
            HotkeyCompletionField<DispatcherTimer>(Window, "pollTimer").Stop();
            HotkeyCompletionField<DispatcherTimer>(Window, "receiverTimer").Stop();
        }

        public void Dispose()
        {
            StopTimers();
            Window.RequestExit();
            initialConnection.Dispose();
            Initial.Dispose();
            Replacement.Dispose();
            Check(native.Calls.Count == 0 && desktop.StartupWrites == 0 && desktop.TrayShows == 0
                && File.ReadAllBytes(store.FilePath).SequenceEqual(originalPreferences),
                "Hotkey completion fixtures use no native registrations, tray, startup or user preferences");
        }
    }
}
