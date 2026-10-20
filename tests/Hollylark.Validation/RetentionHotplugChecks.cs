using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyRetentionHotplugChecks()
    {
        Check(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(),
            "Retention hotplug checks reuse the validation application's UI dispatcher");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyRetentionReadFailureHotplug(false);
                VerifyRetentionReadFailureHotplug(true);
                VerifyRetentionHotplugPartialFailure();
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyRetentionReadFailureHotplug(bool visitDifferentReceiver)
    {
        using var fixture = new RetentionHotplugFixture("read-failure-" + visitDifferentReceiver);
        fixture.StartAndFailRead();
        byte[] preserved = File.ReadAllBytes(fixture.RetainedFilePath);
        fixture.RemoveReceivers();
        Check(preserved.SequenceEqual(File.ReadAllBytes(fixture.RetainedFilePath)),
            "Discovery removal after the failed session preserves the original confirmed baseline");
        if (visitDifferentReceiver)
        {
            fixture.Discovery.Set(fixture.OtherReceiver);
            fixture.ConnectDiscovered();
            Check(fixture.Window.ActiveReceiverPath == fixture.OtherReceiver.Path
                && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 1
                && fixture.Window.DeviceSettings.Single(setting => setting.Id == "Lights").SelectedIndex == 0
                && fixture.Other.SettingFrames.Length == 0,
                "A different receiver reads its own gain and lights without receiving the removed receiver's settings");
            fixture.PollOnce();
            Check(fixture.Other.SettingFrames.Length == 0 && fixture.ReadRetained(fixture.Receiver.Path).Gain == 5
                && fixture.ReadRetained(fixture.OtherReceiver.Path).Gain == 1,
                "Polling the other receiver keeps both persisted baselines independent without copying device settings");
            fixture.RemoveReceivers();
        }
        fixture.Discovery.Set(fixture.Receiver);
        fixture.ConnectDiscovered();
        A1Frame[] writes = fixture.Returned.SettingFrames;
        Check(fixture.Window.IsConnected && !fixture.Window.IsBusy && fixture.Window.ActiveReceiverPath == fixture.Receiver.Path
            && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 5
            && fixture.Window.DeviceSettings.Single(setting => setting.Id == "Lights").SelectedIndex == 1,
            "The same HID path restores its active baseline after an I/O failure precedes discovery removal");
        Check(writes.Length == 2 && writes[0].Command == A1Command.SetGain
            && writes[0].Payload.SequenceEqual(new byte[] { 5 }) && writes[1].Command == A1Command.SetLight
            && writes[1].Payload.SequenceEqual(new byte[] { 1 }) && fixture.Initial.SettingFrames.Length == 0,
            "Only the reset transport receives the two necessary retained-setting writes, with lights correctly inverted");
        Check(fixture.Window.ResultText == Localization.Text("Retention.Restored"),
            "Automatic connection preserves its confirmed restoration result instead of replacing it with a routine ready message");
        fixture.PollOnce();
        fixture.PollOnce();
        Check(fixture.Returned.SettingFrames.Length == 2 && fixture.Other.SettingFrames.Length == 0
            && fixture.Transports.SelectMany(transport => transport.SettingFrames).All(frame => frame.Command != A1Command.SetMute),
            "Stable polling never repeats restoration and no original, returned or foreign receiver receives mute or unmute");
        RetainedSettings retained = fixture.ReadRetained(fixture.Receiver.Path);
        Check(retained.Gain == 5 && !retained.LightsEnabled
            && fixture.OpenedPaths.Count(path => path == fixture.Receiver.Path) == 2
            && fixture.OpenedPaths.Count(path => path == fixture.OtherReceiver.Path) == (visitDifferentReceiver ? 1 : 0),
            "The original baseline survives replug with exactly one initial and one replacement connection for its path");
    }

    private static void VerifyRetentionHotplugPartialFailure()
    {
        using var fixture = new RetentionHotplugFixture("partial-failure", rejectAfterWrites: 1);
        fixture.StartAndFailRead();
        fixture.RemoveReceivers();
        fixture.Discovery.Set(fixture.Receiver);
        fixture.ConnectDiscovered();
        A1Frame[] writes = fixture.Returned.SettingFrames;
        Check(writes.Length == 2 && writes[0].Command == A1Command.SetGain && writes[1].Command == A1Command.SetLight
            && !fixture.Window.IsConnected && !fixture.Window.IsBusy && fixture.Returned.Disposed,
            "A rejected second restoration write stops the session after the already-confirmed first setting");
        Check(fixture.Window.ResultText.Contains(Localization.Text("Retention.Interrupted"), StringComparison.Ordinal),
            "A partial reconnect restore explicitly reports interruption instead of claiming full success");
        fixture.ConnectDiscovered();
        Check(fixture.Window.IsConnected && fixture.Window.ActiveReceiverPath == fixture.Receiver.Path
            && fixture.Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 5
            && fixture.Window.DeviceSettings.Single(setting => setting.Id == "Lights").SelectedIndex == 0
            && fixture.AfterPartialFailure.SettingFrames.Length == 0,
            "A read retry without another physical removal adopts the honest partially restored state without replaying writes");
        fixture.PollOnce();
        fixture.PollOnce();
        RetainedSettings actual = fixture.ReadRetained(fixture.Receiver.Path);
        Check(actual.Gain == 5 && actual.LightsEnabled && fixture.AfterPartialFailure.SettingFrames.Length == 0
            && fixture.Returned.SettingFrames.Length == 2 && fixture.OpenedPaths.Length == 3,
            "The confirmed partial result becomes the new persisted baseline and does not cause reconnect or polling retry loops");
    }

    private sealed class RetentionHotplugFixture : IDisposable
    {
        private readonly MemoryDesktop desktop = new();
        private readonly FakeHotkeyNativeApi native = new();
        private readonly List<string> desktopErrors = [];
        private readonly ConcurrentQueue<string> openedPaths = new();
        private readonly Dictionary<string, Queue<RetentionHotplugTransport>> plannedTransports;
        private readonly string baseDirectory;

        internal ReceiverInfo Receiver { get; } = UsbReceiver("retention-hotplug-original");
        internal ReceiverInfo OtherReceiver { get; } = UsbReceiver("retention-hotplug-foreign");
        internal UsbMutableDiscovery Discovery { get; } = new();
        internal RetentionHotplugTransport Initial { get; } = new(gain: 5, lightsDisabled: true);
        internal RetentionHotplugTransport Returned { get; }
        internal RetentionHotplugTransport Other { get; } = new(gain: 1);
        internal RetentionHotplugTransport AfterPartialFailure { get; } = new(gain: 5);
        internal MainWindow Window { get; }
        internal string[] OpenedPaths => openedPaths.ToArray();
        internal string RetainedFilePath => new DeviceSettingsRetentionStore(baseDirectory).FilePath;
        internal RetentionHotplugTransport[] Transports => [Initial, Returned, Other, AfterPartialFailure];

        internal RetentionHotplugFixture(string scenario, int rejectAfterWrites = int.MaxValue)
        {
            baseDirectory = Path.Combine(ReportDirectory, "retention-hotplug", Localization.CurrentLanguage,
                scenario + "-" + Guid.NewGuid().ToString("N"));
            var store = new UiPreferencesStore(baseDirectory);
            Check(store.TrySave(new UiPreferences
            {
                FirstRunCompleted = true, Language = Localization.CurrentLanguage, ConfirmExit = false,
                CloseToTray = false, StartWithWindows = false, GlobalHotkeysEnabled = false,
                PreserveVolatileSettings = true, ShowHints = false, ShowOperationMessages = false
            }, out string? error), "Prepare isolated hotplug preferences: " + error);
            Returned = new RetentionHotplugTransport(gain: 0, rejectAfterWrites: rejectAfterWrites);
            plannedTransports = new(StringComparer.OrdinalIgnoreCase)
            {
                [Receiver.Path] = new([Initial, Returned, AfterPartialFailure]),
                [OtherReceiver.Path] = new([Other])
            };
            Discovery.Set(Receiver);
            Window = new MainWindow(Discovery.Discover, receiver =>
            {
                openedPaths.Enqueue(receiver.Path);
                if (!plannedTransports.TryGetValue(receiver.Path, out Queue<RetentionHotplugTransport>? planned)
                    || !planned.TryDequeue(out RetentionHotplugTransport? transport) || transport is null)
                    throw new IOException("Unexpected simulated receiver connection: " + receiver.Path);
                return transport;
            }, baseDirectory, desktop, (_, _) => true, _ => { }, desktopErrors.Add, hotkeyNativeApi: native);
        }

        internal void ConnectDiscovered()
        {
            RunTask(Window.ScanAsync());
            UsbField<DispatcherTimer>(Window, "pollTimer").Stop();
        }

        internal void PollOnce()
        {
            RunTask(Poll(Window));
            UsbField<DispatcherTimer>(Window, "pollTimer").Stop();
        }

        internal void StartAndFailRead()
        {
            ConnectDiscovered();
            Check(Window.IsConnected && Window.ActiveReceiverPath == Receiver.Path && Initial.SettingFrames.Length == 0
                && Window.AudioSettings.Single(setting => setting.Id == "Gain").SelectedIndex == 5,
                "Initial active hotplug connection learns actual settings without issuing device writes");
            Check(!Window.IsVisible && !UsbField<bool>(Window, "loaded") && native.Calls.Count == 0
                && !UsbField<DispatcherTimer>(Window, "receiverTimer").IsEnabled,
                "The hotplug fixture never shows a window, starts real monitoring or registers native shortcuts");
            int discoveryCalls = Discovery.Calls;
            byte[] beforeFailure = File.ReadAllBytes(RetainedFilePath);
            Initial.FailNextStatusRead();
            PollOnce();
            Check(Initial.ReadFailures == 1 && Initial.Disposed && !Window.IsConnected && !Window.IsBusy
                && UsbField<ReceiverInfo?>(Window, "sessionReceiver") is null
                && UsbField<string?>(Window, "failedReceiverPath") == Receiver.Path,
                "A genuine simulated IOException closes and resets the session before discovery notices removal");
            Check(Discovery.Calls == discoveryCalls && Window.Receivers.Count == 1
                && Window.Receivers[0].Path == Receiver.Path && OpenedPaths.Length == 1
                && beforeFailure.SequenceEqual(File.ReadAllBytes(RetainedFilePath)),
                "The critical failure-first ordering preserves both the previous discovery list and the persisted baseline");
        }

        internal void RemoveReceivers()
        {
            Discovery.Set();
            RunTask(Window.RefreshUsbAsync());
            Check(Window.Receivers.Count == 0 && !Window.IsConnected
                && UsbField<string?>(Window, "failedReceiverPath") is null,
                "A later discovery absence reconciles the removed path even when its session already ended");
        }

        internal RetainedSettings ReadRetained(string path)
        {
            IReadOnlyList<RetainedReceiverSettings> saved = new DeviceSettingsRetentionStore(baseDirectory).Load(out string? error);
            Check(error is null, "Hotplug observations keep the retained settings file readable");
            return saved.Single(receiver => StringComparer.OrdinalIgnoreCase.Equals(receiver.ReceiverPath, path)).Settings;
        }

        public void Dispose()
        {
            UsbField<DispatcherTimer>(Window, "pollTimer").Stop();
            UsbField<DispatcherTimer>(Window, "receiverTimer").Stop();
            Window.RequestExit();
            RunTask(Window.ReceiverSessionTask);
            PumpUntil(() => Transports.All(transport => transport.InFlight == 0));
            foreach (RetentionHotplugTransport transport in Transports) transport.Dispose();
            Check(desktop.Disposed && desktop.StartupWrites == 0 && desktop.TrayShows == 0
                && desktopErrors.Count == 0 && native.Calls.Count == 0,
                "Retention hotplug cleanup uses only memory desktop and fake shortcut APIs, without registry or native registration");
        }
    }

    private sealed class RetentionHotplugTransport : IA1Transport
    {
        private readonly FakeTransport inner;
        private readonly ConcurrentQueue<A1Frame> frames = new();
        private int failNextStatusRead;
        private int readFailures;
        private int inFlight;
        internal bool Disposed => inner.Disposed;
        internal int ReadFailures => Volatile.Read(ref readFailures);
        internal int InFlight => Volatile.Read(ref inFlight);
        internal A1Frame[] SettingFrames => frames.Where(frame => frame.Command is not (A1Command.GetStatus or A1Command.GetVersion)).ToArray();
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;

        internal RetentionHotplugTransport(int gain, bool lightsDisabled = false, int rejectAfterWrites = int.MaxValue)
        {
            inner = new FakeTransport { RejectAfterWrites = rejectAfterWrites };
            SetAutomationStatusByte(inner, 8, (byte)gain);
            SetAutomationStatusByte(inner, 11, lightsDisabled ? (byte)1 : (byte)0);
            inner.ReportReceived += report => ReportReceived?.Invoke(report);
            inner.Failed += exception => Failed?.Invoke(exception);
        }

        internal void FailNextStatusRead() => Interlocked.Exchange(ref failNextStatusRead, 1);

        public void SendFeature(byte[] report)
        {
            Interlocked.Increment(ref inFlight);
            try
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (!A1Protocol.TryDecodeRequest(report, out A1Frame? frame) || frame is null)
                    throw new InvalidDataException("Malformed simulated hotplug request.");
                frames.Enqueue(frame);
                if (frame.Command == A1Command.GetStatus && Interlocked.Exchange(ref failNextStatusRead, 0) == 1)
                {
                    Interlocked.Increment(ref readFailures);
                    var exception = new IOException("Simulated receiver read failure before discovery removal.");
                    Failed?.Invoke(exception);
                    throw exception;
                }
                inner.SendFeature(report);
            }
            finally { Interlocked.Decrement(ref inFlight); }
        }

        public void Dispose() => inner.Dispose();
    }
}
