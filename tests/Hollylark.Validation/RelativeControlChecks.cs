using System.IO;
using System.Windows;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyRelativeControlChecks()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (int capacity in new[] { 2, 4 })
                {
                    VerifyFreshRelativeGain(language, capacity);
                    VerifyFreshRelativeMute(language, capacity);
                    VerifyRelativeControlRaces(language, capacity);
                }
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyFreshRelativeGain(string language, int capacity)
    {
        using var fixture = new RelativeControlFixture(language, capacity, "gain");
        foreach ((int cached, int fresh, int direction, int expected) in new[]
        {
            (1, 4, 1, 5), (4, 1, -1, 0), (2, 5, 1, 5), (3, 0, -1, 0)
        })
        {
            fixture.Status[8] = (byte)cached;
            fixture.Refresh();
            fixture.Status[8] = (byte)fresh;
            fixture.ResetRequests();
            RunTask(fixture.Desktop.Adjust!(direction));
            A1Frame[] writes = fixture.Writes;
            Check(fixture.Status[8] == expected && fixture.Window.AudioSettings.Single(row => row.Id == "Gain").SelectedIndex == expected
                && writes.Length == (fresh == expected ? 0 : 1)
                && writes.All(write => write.Command == A1Command.SetGain && write.Payload.SequenceEqual(new[] { (byte)expected })),
                $"Tray gain {direction:+0;-0} uses fresh {fresh}, not cached {cached}, including boundary no-ops: {language}/{capacity}");
            Check(!fixture.Window.AudioSettings.Single(row => row.Id == "Gain").IsDirty && fixture.Window.IsConnected,
                "Relative tray gain leaves confirmed state without a queued draft or session failure");
        }
        fixture.Status[8] = 2;
        fixture.Refresh();
        fixture.ResetRequests();
        fixture.AfterRead = read => { if (read == 1) fixture.Status[12] = 2; };
        RunTask(fixture.Desktop.Adjust!(1));
        Check(fixture.Status[8] == 3 && fixture.Status[12] == 2 && fixture.Writes.Length == 1,
            "An unrelated EQ change does not prevent relative gain or get overwritten by it");
    }

    private static void VerifyFreshRelativeMute(string language, int capacity)
    {
        using var fixture = new RelativeControlFixture(language, capacity, "mute");
        foreach (int index in Enumerable.Range(0, capacity))
        foreach (bool useTray in new[] { false, true })
        {
            fixture.SetAllMuted(false);
            fixture.Refresh();
            fixture.SetMuted(index, true);
            fixture.ResetRequests();
            RunTask(useTray ? fixture.Desktop.Mute!(index) : fixture.Window.ToggleMuteAsync(index));
            Check(fixture.Writes.Length == 1 && fixture.Writes[0].Command == A1Command.SetMute
                && fixture.Writes[0].Payload.SequenceEqual(new[] { (byte)index, (byte)0 })
                && fixture.Window.Transmitters.All(transmitter => !transmitter.IsMuted)
                && fixture.Window.ResultText == Localization.Text("Main.Mute.Unmuted", index + 1),
                $"Individual toggle unmutes a freshly muted TX with correct indexed write and message: {language}/{capacity}/{index}/{useTray}");

            fixture.SetAllMuted(true);
            fixture.Refresh();
            fixture.SetMuted(index, false);
            fixture.ResetRequests();
            RunTask(useTray ? fixture.Desktop.Mute!(index) : fixture.Window.ToggleMuteAsync(index));
            Check(fixture.Writes.Length == 1 && fixture.Writes[0].Payload.SequenceEqual(new[] { (byte)index, (byte)1 })
                && fixture.Window.Transmitters.All(transmitter => transmitter.IsMuted)
                && fixture.Window.ResultText == Localization.Text("Main.Mute.Confirmed", index + 1),
                "Individual toggle also mutes a freshly unmuted TX without changing another microphone");
        }
        foreach (bool freshMuted in new[] { true, false })
        {
            fixture.SetAllMuted(!freshMuted);
            fixture.Refresh();
            fixture.SetAllMuted(freshMuted);
            fixture.ResetRequests();
            RunTask(fixture.Window.ToggleAllMicrophonesMuteAsync());
            Check(fixture.Writes.Length == capacity && fixture.Writes.All(write => write.Command == A1Command.SetMute
                    && write.Payload[1] == (freshMuted ? 0 : 1))
                && fixture.Writes.Select(write => (int)write.Payload[0]).SequenceEqual(Enumerable.Range(0, capacity))
                && fixture.Window.Transmitters.All(transmitter => transmitter.IsMuted != freshMuted)
                && fixture.Window.ResultText == Localization.Text(freshMuted ? "Target.All.UnmutedConfirmed" : "Target.All.MutedConfirmed"),
                "Group toggle derives its direction and confirmation from the fresh entire group");
        }
    }

    private static void VerifyRelativeControlRaces(string language, int capacity)
    {
        using var fixture = new RelativeControlFixture(language, capacity, "races");
        fixture.Status[8] = 1;
        fixture.Refresh();
        fixture.ResetRequests();
        fixture.AfterRead = read => { if (read == 1) fixture.Status[8] = 4; };
        RunTask(fixture.Desktop.Adjust!(1));
        Check(fixture.Writes.Length == 0 && fixture.Status[8] == 4 && fixture.Window.IsConnected
            && fixture.Window.ResultText.Contains(Localization.Text("Main.Change.Concurrent"), StringComparison.Ordinal),
            "A second gain change after the fresh snapshot refuses without writing or closing USB");

        fixture.AfterRead = null;
        fixture.SetAllMuted(false);
        fixture.Refresh();
        fixture.ResetRequests();
        fixture.AfterRead = read => { if (read == 1) fixture.SetMuted(0, true); };
        RunTask(fixture.Window.ToggleMuteAsync(0));
        Check(fixture.Writes.Length == 0 && fixture.Window.Transmitters[0].IsMuted && fixture.Window.IsConnected
            && fixture.Window.ResultText.Contains(Localization.Text("Main.Change.Concurrent"), StringComparison.Ordinal),
            "A target mute race after the fresh snapshot refuses without a redundant write");

        fixture.AfterRead = null;
        fixture.SetAllMuted(false);
        fixture.Refresh();
        fixture.ResetRequests();
        fixture.AfterRead = read => { if (read == 1) fixture.SetMuted(capacity - 1, true); };
        RunTask(fixture.Window.ToggleAllMicrophonesMuteAsync());
        Check(fixture.Writes.Length == 0 && fixture.Window.IsConnected
            && fixture.Window.ResultText.Contains(Localization.Text("Main.Change.Concurrent"), StringComparison.Ordinal),
            "A group mute race is checked before the first write");

        fixture.AfterRead = null;
        fixture.SetAllMuted(false);
        fixture.Refresh();
        fixture.ResetRequests();
        fixture.AfterRead = read => { if (read == 3) fixture.SetMuted(capacity - 1, true); };
        RunTask(fixture.Window.ToggleAllMicrophonesMuteAsync());
        Check(fixture.Writes.Length == 1 && fixture.Writes[0].Payload[0] == 0 && fixture.Window.IsConnected
            && fixture.Window.ResultText.Contains(Localization.Text("Main.Change.Concurrent"), StringComparison.Ordinal)
            && fixture.Window.ResultText.Contains(Localization.Text("Target.All.PartialFailure"), StringComparison.Ordinal),
            "A race after the first confirmed group write stops the remaining writes and reports partial execution");
    }

    private sealed class RelativeControlFixture : IDisposable
    {
        private readonly DeviceWriteSafetyTransport transport;
        private readonly FakeHotkeyNativeApi hotkeys = new();
        private int initialRequestCount;
        internal byte[] Status { get; }
        internal MainWindow Window { get; }
        internal ControlDesktop Desktop { get; } = new();
        internal Action<int>? AfterRead { get; set; }
        private int statusReads;
        internal A1Frame[] Writes => transport.Requests.Skip(initialRequestCount)
            .Where(request => request.Command is A1Command.SetGain or A1Command.SetMute).ToArray();

        internal RelativeControlFixture(string language, int capacity, string scenario)
        {
            Status = CreateMicrophoneScopeStatus(capacity, Enumerable.Range(0, capacity).ToArray());
            SetAllMuted(false);
            transport = new DeviceWriteSafetyTransport
            {
                Respond = (source, request) =>
                {
                    switch (request.Command)
                    {
                        case A1Command.GetStatus:
                            source.Deliver(request.Command, Status);
                            AfterRead?.Invoke(++statusReads);
                            break;
                        case A1Command.GetVersion:
                            source.Deliver(request.Command, [request.Payload[0], 1, capacity == 4 ? (byte)1 : (byte)0, 2, 8]);
                            break;
                        case A1Command.SetGain:
                            Status[8] = request.Payload[0];
                            source.Deliver(request.Command, [0]);
                            break;
                        case A1Command.SetMute:
                            SetMuted(request.Payload[0], request.Payload[1] == 1);
                            source.Deliver(request.Command, [0]);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected relative-control fixture request: " + request.Command);
                    }
                }
            };
            string directory = Path.Combine(ReportDirectory, "relative-controls", language + "-" + capacity + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            var store = new UiPreferencesStore(directory);
            Check(store.TrySave(new UiPreferences
            {
                FirstRunCompleted = true, Language = language, ConfirmExit = false, PreserveVolatileSettings = false,
                AnnounceStateChanges = false, ShowOperationMessages = false,
                BatteryAlerts = new BatteryAlertPreferences { Policy = new BatteryAlertPolicy { Enabled = false } }
            }, out _), "Prepare isolated relative-control preferences");
            var receiver = new ReceiverInfo("Simulated relative-control receiver", "simulated://relative-" + Guid.NewGuid().ToString("N"), 0, 64);
            Window = new MainWindow(() => [receiver], _ => transport, directory, Desktop, (_, _) => false,
                automaticReceiverManagement: false, hotkeyNativeApi: hotkeys);
            MoveOffscreen(Window);
            Window.Show();
            PumpUntil(() => !Window.IsBusy && Window.Receivers.Count == 1);
            RunTask(Window.ConnectAsync());
            StopTimers();
            Check(Window.IsConnected && Window.Transmitters.Count == capacity, "Relative controls use a connected simulated topology");
            ResetRequests();
        }

        internal void SetMuted(int index, bool muted) => Status[MicrophoneScopeMuteOffsets[index]] = muted ? (byte)1 : (byte)0;
        internal void SetAllMuted(bool muted)
        {
            for (int index = 0; index < (Status.Length >= 23 ? 4 : 2); index++) SetMuted(index, muted);
        }
        internal void ResetRequests() { initialRequestCount = transport.Requests.Length; statusReads = 0; }
        internal void Refresh() { RunTask(Window.RefreshAsync()); StopTimers(); }
        private void StopTimers()
        {
            foreach (string name in new[] { "pollTimer", "receiverTimer", "autoApplyTimer" })
                UsbField<DispatcherTimer>(Window, name).Stop();
        }
        public void Dispose()
        {
            AfterRead = null;
            Window.RequestExit();
            RunTask(Window.ReceiverSessionTask);
            Pump();
            Check(transport.Disposed && hotkeys.Registered.Count == 0 && hotkeys.Calls.Count == 0,
                "Relative fixture closes its owned transport without native shortcuts, audio or device access");
        }
    }
}
