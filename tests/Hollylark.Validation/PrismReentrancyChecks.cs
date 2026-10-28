using LarkControl.Windows;
using Prismatoid;
using Prismatoid.NativeInterop;

internal static partial class Program
{
    private static readonly string[] PrismReentrantPhases =
        ["initialize", "create", "features", "initialize-backend", "speak", "free"];

    public static void VerifyPrismReentrancyChecks()
    {
        foreach (string phase in PrismReentrantPhases)
        {
            foreach (bool beforeCall in new[] { true, false })
            {
                VerifyPrismReentrantDisposal(phase, beforeCall, new PrismBackendScenario());
                VerifyPrismReentrantSpeech(phase, beforeCall);
            }
        }
        foreach (bool beforeCall in new[] { true, false })
        {
            VerifyPrismReentrantDisposal("create", beforeCall, null);
            VerifyPrismReentrantDisposal("features", beforeCall, new PrismBackendScenario { Features = 0 });
            VerifyPrismReentrantDisposal("initialize-backend", beforeCall,
                new PrismBackendScenario { InitializeResult = PrismError.PRISM_ERROR_BACKEND_NOT_AVAILABLE });
            VerifyPrismReentrantDisposal("free", beforeCall, new PrismBackendScenario { Features = 0 });
            VerifyPrismReentrantDisposal("free", beforeCall,
                new PrismBackendScenario { InitializeResult = PrismError.PRISM_ERROR_BACKEND_NOT_AVAILABLE });
        }
        VerifyPrismReentrantExceptions();
        VerifyPrismReentrantLazyInitialization();
        VerifyPrismReentrantOwnerThread();
        VerifyPrismReentrantShutdown();
        Console.WriteLine("Prism reentrancy checks use synchronous fake callbacks only; actual native COM message pumping and speech are not exercised.");
    }

    private static void VerifyPrismReentrantDisposal(string phase, bool beforeCall, PrismBackendScenario? scenario)
    {
        var native = new ReentrantPrismSpeechApi(scenario);
        using var speech = new PrismScreenReaderSpeech(native);
        string label = phase + (beforeCall ? "/before" : "/after")
            + (scenario is null ? "/missing" : "/" + scenario.Features + "/" + scenario.InitializeResult);
        bool reentered = false;
        void DisposeDuringCall(string current)
        {
            if (current != phase || reentered) return;
            reentered = true;
            int calls = native.Inner.Calls.Count;
            speech.Dispose();
            speech.Dispose();
            Check(native.Inner.ShutdownContexts.Count == 0 && native.ActiveCalls == 1,
                "Reentrant disposal defers shutdown until the native call exits: " + label);
            Check(!speech.TrySpeak("closed callback") && !speech.TrySpeak("closed\0callback")
                && native.Inner.Calls.Count == calls,
                "Reentrant disposal immediately refuses subsequent speech without native calls: " + label);
        }
        if (beforeCall) native.BeforeCall = DisposeDuringCall;
        else native.AfterCall = DisposeDuringCall;

        Check(!speech.TrySpeak("dispose while active") && reentered,
            "The outer speech request reports cancellation, including disposal during free: " + label);
        int phaseIndex = Array.IndexOf(PrismReentrantPhases, phase);
        IEnumerable<string> expected = PrismReentrantPhases.Take(phaseIndex + 1);
        if (phase == "free" && scenario!.Features != PrismSpeechFeatures)
            expected = ["initialize", "create", "features", "free"];
        else if (phase == "free" && scenario!.InitializeResult == PrismError.PRISM_ERROR_BACKEND_NOT_AVAILABLE)
            expected = ["initialize", "create", "features", "initialize-backend", "free"];
        else if (phaseIndex > 0 && phase != "free" && scenario is not null)
            expected = expected.Append("free");
        string[] calls = expected.Append("shutdown").ToArray();
        Check(native.Inner.Calls.SequenceEqual(calls)
            && native.Events.SequenceEqual(calls.SelectMany(call => new[] { call + ":enter", call + ":exit" })),
            "Disposal stops later reader stages and shuts down only after native return and backend release: " + label);
        Check(native.Inner.FreedBackends.SequenceEqual(native.Inner.CreatedBackends)
            && native.Inner.LiveBackends.Count == 0
            && native.Inner.ShutdownContexts.SequenceEqual(new nint[] { native.Inner.Context })
            && native.Inner.RequestedReaders.Count <= 1 && native.MaximumActiveCalls == 1
            && native.ThreadIds.All(thread => thread == Environment.CurrentManagedThreadId),
            "Deferred disposal releases every backend and its context exactly once on the owner thread: " + label);
        int completed = native.Events.Count;
        speech.Dispose();
        speech.Dispose();
        Check(!speech.TrySpeak("after deferred shutdown") && native.Events.Count == completed,
            "Disposed speech cannot reinitialize or repeat shutdown: " + label);
    }

    private static void VerifyPrismReentrantSpeech(string phase, bool beforeCall)
    {
        var native = new ReentrantPrismSpeechApi(new PrismBackendScenario());
        using var speech = new PrismScreenReaderSpeech(native);
        string label = phase + (beforeCall ? "/before" : "/after");
        bool reentered = false;
        int fallbacks = 0;
        var reports = new List<Exception>();
        void SpeakDuringCall(string current)
        {
            if (current != phase || reentered) return;
            reentered = true;
            int calls = native.Inner.Calls.Count;
            UiNotificationRouting.Announce(speech, "nested request", () => fallbacks++, reports.Add);
            Check(native.Inner.Calls.Count == calls && native.ActiveCalls == 1,
                "Nested speech never recursively enters a native phase: " + label);
        }
        if (beforeCall) native.BeforeCall = SpeakDuringCall;
        else native.AfterCall = SpeakDuringCall;

        Check(speech.TrySpeak("outer request") && reentered && fallbacks == 1 && reports.Count == 0,
            "Reentrant speech returns false for the available UIA fallback while the outer request succeeds: " + label);
        Check(speech.TrySpeak("next request") && native.Inner.InitializeCalls == 1
            && native.Inner.RequestedReaders.SequenceEqual(new[] { PrismBackendId.Nvda, PrismBackendId.Nvda })
            && native.Inner.Spoken.Select(call => call.Message).SequenceEqual(new[] { "outer request", "next request" })
            && native.Inner.Spoken.All(call => !call.Interrupt) && native.MaximumActiveCalls == 1,
            "The active-call guard resets after success without changing reader priority or interrupt policy: " + label);
        speech.Dispose();
        Check(native.Inner.FreedBackends.SequenceEqual(native.Inner.CreatedBackends)
            && native.Inner.LiveBackends.Count == 0 && native.Inner.ShutdownContexts.Count == 1,
            "Serialized requests retain normal per-message cleanup and one context shutdown: " + label);
    }

    private static void VerifyPrismReentrantExceptions()
    {
        foreach (string phase in PrismReentrantPhases)
        foreach (bool dispose in new[] { false, true })
        {
            var native = new ReentrantPrismSpeechApi(new PrismBackendScenario());
            using var speech = new PrismScreenReaderSpeech(native);
            var failure = new InvalidOperationException("Controlled Prism failure at " + phase);
            string label = phase + "/dispose=" + dispose;
            void FailDuringCall(string current)
            {
                if (current != phase) return;
                if (dispose) speech.Dispose();
                throw failure;
            }
            // Allocation failures occur before acquisition; free failures occur after releasing the fake handle.
            if (phase is "initialize" or "create") native.BeforeCall = FailDuringCall;
            else native.AfterCall = FailDuringCall;
            Exception? observed = null;
            try { speech.TrySpeak("failing request"); }
            catch (Exception exception) { observed = exception; }
            Check(ReferenceEquals(observed, failure) && native.ActiveCalls == 0 && native.MaximumActiveCalls == 1,
                "Exceptions propagate unchanged after unwinding active native calls: " + label);
            Check(native.Inner.LiveBackends.Count == 0
                && native.Inner.FreedBackends.SequenceEqual(native.Inner.CreatedBackends)
                && native.Inner.ShutdownContexts.Count == (dispose && phase != "initialize" ? 1 : 0),
                "Exception cleanup releases backends before any deferred context shutdown: " + label);
            if (dispose && phase != "initialize")
                Check(native.Events[^2] == "shutdown:enter" && native.Events[^1] == "shutdown:exit",
                    "Deferred shutdown runs after the failing phase and its release finally: " + label);
            native.BeforeCall = null;
            native.AfterCall = null;
            if (dispose)
            {
                int calls = native.Events.Count;
                speech.Dispose();
                Check(!speech.TrySpeak("closed after failure") && native.Events.Count == calls,
                    "A failed disposed request stays closed without retrying native cleanup: " + label);
            }
            else
            {
                Check(speech.TrySpeak("retry after failure") && native.Inner.InitializeCalls == 1,
                    "Exception unwinding clears the active guard and preserves lazy context reuse: " + label);
                speech.Dispose();
                Check(native.Inner.ShutdownContexts.Count == 1 && native.Inner.LiveBackends.Count == 0,
                    "A successful retry still shuts down once without leaked handles: " + label);
            }
        }
    }

    private static void VerifyPrismReentrantLazyInitialization()
    {
        foreach (bool dispose in new[] { false, true })
        {
            var native = new ReentrantPrismSpeechApi(new PrismBackendScenario());
            native.Inner.InitializeResult = 0;
            using var speech = new PrismScreenReaderSpeech(native);
            if (dispose) native.AfterCall = phase => { if (phase == "initialize") speech.Dispose(); };
            if (dispose)
                Check(!speech.TrySpeak("cancelled null context"), "Disposal during null initialization cancels the request");
            else
                Throws<InvalidOperationException>(() => speech.TrySpeak("null context"), "Null initialization still reports its original failure");
            Check(native.Inner.InitializeCalls == 1 && native.Inner.RequestedReaders.Count == 0
                && native.Inner.ShutdownContexts.Count == 0 && native.ActiveCalls == 0,
                "A null context is never used or shut down, even after reentrant disposal: " + dispose);
            native.AfterCall = null;
            native.Inner.InitializeResult = native.Inner.Context;
            if (dispose)
                Check(!speech.TrySpeak("closed lazy context") && native.Inner.InitializeCalls == 1,
                    "Disposal during lazy initialization prevents future initialization");
            else
            {
                Check(speech.TrySpeak("retry lazy initialization") && native.Inner.InitializeCalls == 2,
                    "Null initialization clears the active guard and permits a fresh lazy attempt");
                speech.Dispose();
                Check(native.Inner.ShutdownContexts.Count == 1, "Only the successfully retried lazy context is shut down");
            }
        }
    }

    private static void VerifyPrismReentrantOwnerThread()
    {
        var native = new ReentrantPrismSpeechApi(new PrismBackendScenario());
        using var speech = new PrismScreenReaderSpeech(native);
        bool reentered = false;
        native.BeforeCall = phase =>
        {
            if (phase != "features" || reentered) return;
            reentered = true;
            int calls = native.Inner.Calls.Count;
            Throws<InvalidOperationException>(() => Task.Run(() => speech.TrySpeak("worker during active call")).GetAwaiter().GetResult(),
                "An active native call does not bypass the owning-thread speech guard");
            Throws<InvalidOperationException>(() => Task.Run(speech.Dispose).GetAwaiter().GetResult(),
                "An active native call does not permit worker-thread disposal");
            Check(!speech.TrySpeak("nested on owner") && native.Inner.Calls.Count == calls
                && native.Inner.ShutdownContexts.Count == 0,
                "Rejected worker calls preserve the owner and nested speech remains serialized");
        };
        Check(speech.TrySpeak("owner request") && reentered && speech.TrySpeak("owner remains usable")
            && native.ThreadIds.All(thread => thread == Environment.CurrentManagedThreadId),
            "Only the original owner enters native calls after rejected cross-thread reentrancy");
        speech.Dispose();
        Check(native.Inner.ShutdownContexts.Count == 1 && native.Inner.LiveBackends.Count == 0,
            "The owner retains responsibility for final backend and context cleanup");
    }

    private static void VerifyPrismReentrantShutdown()
    {
        foreach (bool fail in new[] { false, true })
        {
            var native = new ReentrantPrismSpeechApi(new PrismBackendScenario());
            using var speech = new PrismScreenReaderSpeech(native);
            Check(speech.TrySpeak("before shutdown"), "Prepare a fake context for reentrant shutdown: " + fail);
            var failure = new InvalidOperationException("Controlled shutdown failure");
            bool reentered = false;
            native.AfterCall = phase =>
            {
                if (phase != "shutdown") return;
                reentered = true;
                speech.Dispose();
                Check(!speech.TrySpeak("during shutdown"), "Shutdown callbacks cannot reopen the disposed context: " + fail);
                if (fail) throw failure;
            };
            Exception? observed = null;
            try { speech.Dispose(); }
            catch (Exception exception) { observed = exception; }
            Check(reentered && (fail ? ReferenceEquals(observed, failure) : observed is null),
                "Shutdown remains idempotent during callbacks and preserves exceptions: " + fail);
            int calls = native.Events.Count;
            speech.Dispose();
            Check(!speech.TrySpeak("after shutdown") && native.Events.Count == calls
                && native.Inner.ShutdownContexts.Count == 1 && native.MaximumActiveCalls == 1,
                "The context is cleared before shutdown, including its exception path: " + fail);
        }
    }

    private sealed class ReentrantPrismSpeechApi : IPrismSpeechApi
    {
        internal FakePrismSpeechApi Inner { get; } = new();
        internal Action<string>? BeforeCall { get; set; }
        internal Action<string>? AfterCall { get; set; }
        internal List<string> Events { get; } = [];
        internal List<int> ThreadIds { get; } = [];
        internal int ActiveCalls { get; private set; }
        internal int MaximumActiveCalls { get; private set; }

        internal ReentrantPrismSpeechApi(PrismBackendScenario? scenario)
        {
            if (scenario is not null) Inner.Readers.Add(PrismBackendId.Nvda, scenario);
            Inner.Readers.Add(PrismBackendId.Jaws, new PrismBackendScenario());
        }

        public nint Initialize() => Invoke("initialize", Inner.Initialize);
        public nint CreateBackend(nint context, PrismBackendId identifier) =>
            Invoke("create", () => Inner.CreateBackend(context, identifier));
        public PrismBackendFeature GetFeatures(nint backend) => Invoke("features", () => Inner.GetFeatures(backend));
        public PrismError InitializeBackend(nint backend) => Invoke("initialize-backend", () => Inner.InitializeBackend(backend));
        public PrismError Speak(nint backend, string message, bool interrupt) => Invoke("speak", () => Inner.Speak(backend, message, interrupt));
        public void FreeBackend(nint backend) => Invoke("free", () => { Inner.FreeBackend(backend); return 0; });

        public void Shutdown(nint context)
        {
            if (ActiveCalls != 0 || Inner.LiveBackends.Count != 0)
                throw new InvalidOperationException("Fake Prism shutdown occurred during an active call or before backend release.");
            Invoke("shutdown", () => { Inner.Shutdown(context); return 0; });
        }

        private T Invoke<T>(string phase, Func<T> action)
        {
            Events.Add(phase + ":enter");
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            ActiveCalls++;
            MaximumActiveCalls = Math.Max(MaximumActiveCalls, ActiveCalls);
            try
            {
                BeforeCall?.Invoke(phase);
                T result = action();
                AfterCall?.Invoke(phase);
                return result;
            }
            finally
            {
                ActiveCalls--;
                Events.Add(phase + ":exit");
            }
        }
    }
}
