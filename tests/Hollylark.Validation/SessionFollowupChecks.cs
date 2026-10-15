using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyFocusFollowupInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Console.WriteLine($"Focus regression iteration {iteration + 1}");
                VerifyReferenceProfileKeyboardNavigation();
                VerifyMainOperationFocus("-repeat-" + iteration);
            }
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyFollowupInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            VerifyMainSessionFollowup();
            VerifySettingsFollowup();
            VerifyAudioFollowupChecks();
        }
        finally { application.Shutdown(); }
    }

    private static void VerifySessionFollowupInterface()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyMainSessionFollowup(); }
        finally { application.Shutdown(); }
    }

    private static void VerifyMainSessionFollowup()
    {
        var failures = new List<Exception>();
        foreach (Action check in new Action[] { VerifyCancelledConnectionCannotReplaceNewSession,
            () => VerifyProfileCompletionCannotUseReplacement(false), () => VerifyProfileCompletionCannotUseReplacement(true) })
        {
            try { check(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException("Session follow-up regressions failed.", failures);
    }

    private static void VerifyProfileCompletionCannotUseReplacement(bool applyProfile)
    {
        using var firstTransport = new FakeTransport();
        using var replacementTransport = new FakeTransport();
        int factoryCalls = 0;
        var receiver = new ReceiverInfo("Simulated receiver", "simulated://profile-generation", 0, 64);
        var window = new MainWindow(() => [receiver], _ => Interlocked.Increment(ref factoryCalls) == 1
            ? firstTransport : replacementTransport,
            Path.Combine(ReportDirectory, applyProfile ? "stale-profile-apply" : "stale-profile-read"),
            new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        var deferred = new DeferredUiContext();
        Task? oldOperation = null;
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        try
        {
            RunTask(window.ConnectAsync());
            int initialReads = firstTransport.Commands.Count;
            SynchronizationContext? normalContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(deferred);
                oldOperation = applyProfile
                    ? window.ApplyAudioProfileAsync(AudioProfile.FromStatus("Cancelled profile", A1Status.Parse(ValidStatus)))
                    : (Task<A1Status?>)typeof(MainWindow).GetMethod("ReadProfileStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            }
            finally { SynchronizationContext.SetSynchronizationContext(normalContext); }
            PumpUntil(() => firstTransport.Commands.Count > initialReads && deferred.HasCallbacks);
            Check(window.IsBusy && !oldOperation.IsCompleted, "Profile operation has a deferred continuation from its original session: " + applyProfile);
            window.CancelCurrentOperation();
            RunTask(window.ConnectAsync());
            Check(window.IsConnected && firstTransport.Disposed && !replacementTransport.Disposed,
                "Replacement receiver session is established before the old profile continuation resumes: " + applyProfile);
            string replacementResult = window.ResultText;
            PumpUntil(() => { deferred.Drain(); return oldOperation.IsCompleted; });
            if (applyProfile)
                Check(((Task<string?>)oldOperation).GetAwaiter().GetResult() is not null,
                    "Cancelled profile application cannot report success from matching values on a replacement session");
            else
                Check(((Task<A1Status?>)oldOperation).GetAwaiter().GetResult() is null,
                    "Cancelled profile capture cannot return the replacement session's status as its own reading");
            Check(window.ResultText == replacementResult, "A stale profile completion never overwrites the replacement session result: " + applyProfile);
            Check(firstTransport.Commands.Concat(replacementTransport.Commands).All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "Deferred profile regression performs snapshot reads only on simulated transports: " + applyProfile);
        }
        finally
        {
            if (oldOperation is not null && !oldOperation.IsCompleted)
            {
                window.Disconnect();
                PumpUntil(() => { deferred.Drain(); return oldOperation.IsCompleted; });
            }
            window.Close();
            Pump();
        }
    }

    private sealed class DeferredUiContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        internal bool HasCallbacks => !callbacks.IsEmpty;

        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue((callback, state));

        internal void Drain()
        {
            SynchronizationContext? previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (callbacks.TryDequeue(out var item)) item.Callback(item.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private static void VerifyCancelledConnectionCannotReplaceNewSession()
    {
        using var blockedFactory = new ManualResetEventSlim(false);
        using var replacementFactoryEntered = new ManualResetEventSlim(false);
        using var firstTransport = new FakeTransport();
        using var replacementTransport = new FakeTransport();
        int factoryCalls = 0;
        int firstDisposedBeforeReplacement = 0;
        var receiver = new ReceiverInfo("Simulated receiver", "simulated://cancelled-connection", 0, 64);
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "cancelled-connection-replacement"));
        var window = new MainWindow(() => [receiver], _ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
            {
                blockedFactory.Wait();
                return firstTransport;
            }
            Volatile.Write(ref firstDisposedBeforeReplacement, firstTransport.Disposed ? 1 : 0);
            replacementFactoryEntered.Set();
            return replacementTransport;
        }, store.BaseDirectory, new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        Task? originalConnection = null;
        Task? replacementConnection = null;
        MoveOffscreen(window);
        window.Show();
        PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
        try
        {
            originalConnection = window.ConnectAsync();
            PumpUntil(() => Volatile.Read(ref factoryCalls) == 1);
            Check(window.IsBusy && !window.IsConnected, "Original backend connection waits for its slow transport factory");
            window.CancelCurrentOperation();
            replacementConnection = window.ConnectAsync();
            Pump();
            Check(!replacementFactoryEntered.Wait(TimeSpan.FromMilliseconds(50))
                && Volatile.Read(ref factoryCalls) == 1 && !replacementConnection.IsCompleted
                && window.IsBusy && !window.IsConnected && replacementTransport.Commands.Count == 0,
                "The replacement connection waits behind the cancelled factory without opening or reading a second transport");
            blockedFactory.Set();
            RunTask(Task.WhenAll(originalConnection, replacementConnection));
            Check(Volatile.Read(ref factoryCalls) == 2 && Volatile.Read(ref firstDisposedBeforeReplacement) == 1,
                "The cancelled factory result is disposed before the serialized replacement factory runs");
            Check(window.IsConnected && !window.IsBusy && !File.Exists(store.FilePath),
                "A replacement backend connection succeeds without saving receiver preferences");
            string replacementResult = window.ResultText;
            Check(replacementResult == LarkControl.Windows.Localization.Text("Main.Usb.Ready", receiver.Name, 2, 2),
                "Only the replacement session publishes its confirmed receiver status after both connections finish");
            Pump();
            Check(firstTransport.Disposed && !replacementTransport.Disposed && window.IsConnected,
                "Late cancelled factory disposes only its own transport");
            Check(window.CurrentPreferences.PreferredReceiverPath is null && !File.Exists(store.FilePath),
                "A cancelled connection never persists receiver preferences for the replacement session");
            Check(window.ResultText == replacementResult, "Late cancelled connection does not replace the new session result");
            Check(firstTransport.Commands.Count == 0 && replacementTransport.Commands.All(command => command is A1Command.GetStatus or A1Command.GetVersion),
                "The cancellation race performs no device setting writes, even on the fake transports");
        }
        finally
        {
            blockedFactory.Set();
            window.Close();
            RunTask(Task.WhenAll(originalConnection ?? Task.CompletedTask, replacementConnection ?? Task.CompletedTask));
            Pump();
        }
    }
}
