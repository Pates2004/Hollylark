using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyPeakReviewApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyPeakReviewChecks(); }
        finally { application.Shutdown(); }
    }

    private static void VerifyPeakReviewChecks()
    {
        if (Application.Current is null || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run peak review checks inside the initialized STA WPF validation suite.");

        var failures = new List<Exception>();
        foreach (bool applyProfile in new[] { false, true })
        {
            try { VerifyPeakReviewSupersededProfileCompletion(applyProfile); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Same-session profile completion regressions failed.", failures);
    }

    private static void VerifyPeakReviewSupersededProfileCompletion(bool applyProfile)
    {
        using var transport = new PeakReviewPausedTransport();
        var receiver = new ReceiverInfo("Simulated peak-review receiver", "simulated://peak-review", 0, 64);
        string directory = Path.Combine(ReportDirectory, "peak-review-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(() => [receiver], _ => transport, directory, new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        var context = new PeakReviewStepContext();
        Task? previousOperation = null;
        try
        {
            RunTask(window.ScanAsync());
            RunTask(window.ConnectAsync());
            ((DispatcherTimer)typeof(MainWindow).GetField("pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!).Stop();
            Check(window.IsConnected && !window.IsBusy && !window.IsVisible,
                "Peak review connects an unshown window exclusively to the simulated receiver");

            AudioProfile originalProfile = AudioProfile.FromStatus("Earlier profile", A1Status.Parse(ValidStatus));
            transport.PauseNextStatusRead();
            SynchronizationContext? normalContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                previousOperation = applyProfile
                    ? window.ApplyAudioProfileAsync(originalProfile)
                    : (Task<A1Status?>)typeof(MainWindow).GetMethod("ReadProfileStatusAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            }
            finally { SynchronizationContext.SetSynchronizationContext(normalContext); }

            PumpUntil(() => transport.HasPausedStatusRead);
            Check(window.IsBusy && !previousOperation.IsCompleted,
                "An explicitly withheld response prevents synchronous profile completion: " + applyProfile);
            transport.ReleaseStatusRead();
            PumpUntil(() =>
            {
                if (window.IsBusy) context.RunNext();
                return !window.IsBusy;
            });
            Check(!previousOperation.IsCompleted && context.HasCallbacks,
                "The receiver operation completes before its queued outer profile continuation: " + applyProfile);

            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            int replacementGain = (originalProfile.Gain + 1) % 6;
            gain.SelectedIndex = replacementGain;
            RunTask(window.ApplySettingAsync(gain));
            Check(window.IsConnected && !window.IsBusy && !transport.Inner.Disposed
                && !gain.IsDirty && gain.SelectedIndex == replacementGain
                && transport.Inner.Commands.Count(command => command == A1Command.SetGain) == 1,
                "A newer gain operation is confirmed without replacing or disconnecting the original session");
            string newerResult = window.ResultText;
            PumpUntil(() =>
            {
                context.RunNext();
                return previousOperation.IsCompleted;
            });
            previousOperation.GetAwaiter().GetResult();
            Check(!Directory.Exists(directory)
                && transport.Inner.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion or A1Command.SetGain),
                "Peak review writes no preference files and sends only the intended commands to its fake transport");

            if (applyProfile)
            {
                Check(window.ResultText == newerResult,
                    "An older profile completion must not overwrite a newer same-session gain result");
            }
            else
            {
                A1Status? captured = ((Task<A1Status?>)previousOperation).GetAwaiter().GetResult();
                Check(captured is null || captured.Gain == originalProfile.Gain,
                    "A superseded profile read must return its own snapshot or no snapshot, never the newer operation's gain");
            }
        }
        finally
        {
            window.Close();
            if (previousOperation is not null && !previousOperation.IsCompleted)
                PumpUntil(() =>
                {
                    context.RunNext();
                    return previousOperation.IsCompleted;
                });
            Pump();
        }
    }

    private sealed class PeakReviewStepContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks;

        internal PeakReviewStepContext() : this(new()) { }

        private PeakReviewStepContext(ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks)
        {
            this.callbacks = callbacks;
        }

        internal bool HasCallbacks => !callbacks.IsEmpty;

        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue((callback, state));

        internal void RunNext()
        {
            if (!callbacks.TryDequeue(out var item)) return;
            SynchronizationContext? previous = Current;
            try
            {
                SetSynchronizationContext(new PeakReviewStepContext(callbacks));
                item.Callback(item.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private sealed class PeakReviewPausedTransport : IA1Transport
    {
        private readonly ConcurrentQueue<byte[]> pausedRequests = new();
        private int pauseNextStatus;
        internal FakeTransport Inner { get; } = new();
        internal bool HasPausedStatusRead => !pausedRequests.IsEmpty;

        public event Action<byte[]>? ReportReceived
        {
            add => Inner.ReportReceived += value;
            remove => Inner.ReportReceived -= value;
        }

        public event Action<Exception>? Failed
        {
            add => Inner.Failed += value;
            remove => Inner.Failed -= value;
        }

        internal void PauseNextStatusRead() => Interlocked.Exchange(ref pauseNextStatus, 1);

        internal void ReleaseStatusRead()
        {
            if (!pausedRequests.TryDequeue(out byte[]? report))
                throw new InvalidOperationException("No simulated status request is waiting for release.");
            Inner.SendFeature(report);
        }

        public void SendFeature(byte[] report)
        {
            if (A1Protocol.TryDecodeRequest(report, out A1Frame? frame) && frame?.Command == A1Command.GetStatus
                && Interlocked.Exchange(ref pauseNextStatus, 0) == 1)
            {
                pausedRequests.Enqueue((byte[])report.Clone());
                return;
            }
            Inner.SendFeature(report);
        }

        public void Dispose() => Inner.Dispose();
    }
}
