using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LarkControl.Windows;
using Prismatoid;
using Prismatoid.NativeInterop;

internal static partial class Program
{
    private static readonly PrismBackendId[] ExpectedPrismReaders =
    [
        PrismBackendId.Nvda, PrismBackendId.Jaws, PrismBackendId.ZoomText, PrismBackendId.Zdsr,
        PrismBackendId.BoyPcReader, PrismBackendId.PcTalker, PrismBackendId.SenseReader,
        PrismBackendId.SystemAccess, PrismBackendId.WindowEyes
    ];

    private const PrismBackendFeature PrismSpeechFeatures =
        PrismBackendFeature.IsSupportedAtRuntime | PrismBackendFeature.SupportsSpeak;

    public static void VerifyPrismSpeechChecks()
    {
        using (Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Prism.NOTICES.txt")
            ?? throw new InvalidOperationException("Prism license resource is missing."))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
        {
            string notices = reader.ReadToEnd();
            Check(!notices.Contains('\uFFFD') && notices.Contains("LICENSES/prism/mpl-2.0.txt", StringComparison.Ordinal),
                "Embedded Prism license text preserves valid Unicode and the canonical upstream MPL document");
            Check(notices.Contains("LICENSES/nvdaController/lgpl-2.1.txt", StringComparison.Ordinal)
                && notices.Contains("LICENSES/simdutf/apache-2.0.txt", StringComparison.Ordinal)
                && notices.Contains("LICENSES/moderncom/AUTHORS.md", StringComparison.Ordinal),
                "Embedded Prism notices retain native dependency licenses and attribution");
        }
        VerifyPrismLazyLifetime();
        VerifyPrismReaderSelection();
        VerifyPrismReaderAvailability();
        VerifyPrismBackendFailures();
        VerifyPrismContextFailures();
        VerifyPrismThreadOwnership();
        VerifyPrismReaderRestart();
        VerifyPrismNotificationRouting();
        Console.WriteLine("Prism speech coverage uses fake APIs only; native UTF-8 marshalling and audible screen-reader output are not exercised.");
    }

    private static void VerifyPrismLazyLifetime()
    {
        var native = new FakePrismSpeechApi();
        using var speech = new PrismScreenReaderSpeech(native);
        Check(native.Calls.Count == 0, "Constructing Prism speech does not initialize native speech");
        foreach (string message in new[] { null!, "", " \t\r\n" })
            Check(!speech.TrySpeak(message), "Empty Prism messages are rejected without initialization");
        Throws<ArgumentException>(() => speech.TrySpeak("before\0after"), "Embedded nulls cannot truncate a Prism announcement");
        speech.Dispose();
        speech.Dispose();
        Check(!speech.TrySpeak("after disposal") && !speech.TrySpeak("ignored\0after disposal")
            && native.Calls.Count == 0, "Unused and repeatedly disposed Prism speech never touches the native API");
    }

    private static void VerifyPrismReaderSelection()
    {
        Check(PrismScreenReaderSpeech.ReaderIds.SequenceEqual(ExpectedPrismReaders)
            && !PrismScreenReaderSpeech.ReaderIds.Contains(PrismBackendId.Sapi)
            && !PrismScreenReaderSpeech.ReaderIds.Contains(PrismBackendId.OneCore),
            "Prism permits exactly the nine direct readers, excluding SAPI, OneCore, UIA and unknown backends");
        foreach (PrismBackendId selected in ExpectedPrismReaders)
        {
            var native = new FakePrismSpeechApi();
            foreach (PrismBackendId identifier in ExpectedPrismReaders)
                native.Readers.Add(identifier, new PrismBackendScenario
                {
                    Features = Array.IndexOf(ExpectedPrismReaders, identifier) < Array.IndexOf(ExpectedPrismReaders, selected)
                        ? 0 : PrismSpeechFeatures
                });
            using var speech = new PrismScreenReaderSpeech(native);
            Check(speech.TrySpeak("one reader only"), "An allowed direct reader accepts speech: " + selected);
            PrismBackendId[] attempted = ExpectedPrismReaders.Take(Array.IndexOf(ExpectedPrismReaders, selected) + 1).ToArray();
            Check(native.RequestedReaders.SequenceEqual(attempted) && native.InitializedBackends.Count == 1
                && native.Spoken.Count == 1 && native.Spoken[0].Identifier == selected,
                "Prism stops at the first successful reader and requests speech exactly once: " + selected);
            Check(native.FreedBackends.SequenceEqual(native.CreatedBackends) && native.LiveBackends.Count == 0,
                "Prism frees skipped and successful reader handles before returning: " + selected);
            speech.Dispose();
            speech.Dispose();
            int calls = native.Calls.Count;
            Check(!speech.TrySpeak("closed") && native.Calls.Count == calls && native.ShutdownContexts.SequenceEqual(new nint[] { native.Context }),
                "Prism shuts down its valid context once and remains silent after disposal: " + selected);
        }
    }

    private static void VerifyPrismReaderAvailability()
    {
        foreach (PrismBackendFeature features in new[]
        {
            (PrismBackendFeature)0, PrismBackendFeature.IsSupportedAtRuntime, PrismBackendFeature.SupportsSpeak
        })
        {
            var native = new FakePrismSpeechApi();
            native.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario { Features = features });
            native.Readers.Add(PrismBackendId.Jaws, new PrismBackendScenario());
            using var speech = new PrismScreenReaderSpeech(native);
            Check(speech.TrySpeak("feature filtered") && native.Spoken.Count == 1
                && native.Spoken[0].Identifier == PrismBackendId.Jaws
                && native.InitializedBackends.SequenceEqual(new[] { native.CreatedBackends[1] })
                && native.FreedBackends.SequenceEqual(native.CreatedBackends),
                "A reader must support both runtime availability and Speak before initialization: " + features);
        }

        var absent = new FakePrismSpeechApi();
        using (var speech = new PrismScreenReaderSpeech(absent))
            Check(!speech.TrySpeak("no reader") && absent.RequestedReaders.SequenceEqual(ExpectedPrismReaders)
                && absent.CreatedBackends.Count == 0 && absent.Spoken.Count == 0,
                "Missing reader handles are skipped without standalone speech or invalid frees");
        Check(absent.ShutdownContexts.SequenceEqual(new nint[] { absent.Context }), "Even an unavailable-reader search releases its valid context");

        var racing = new FakePrismSpeechApi();
        racing.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario
        {
            InitializeResult = PrismError.PRISM_ERROR_BACKEND_NOT_AVAILABLE
        });
        racing.Readers.Add(PrismBackendId.ZoomText, new PrismBackendScenario());
        using (var speech = new PrismScreenReaderSpeech(racing))
            Check(speech.TrySpeak("reader stopped during initialization")
                && racing.RequestedReaders.SequenceEqual(ExpectedPrismReaders.Take(3))
                && racing.InitializedBackends.SequenceEqual(racing.CreatedBackends)
                && racing.Spoken.Count == 1 && racing.Spoken[0].Identifier == PrismBackendId.ZoomText
                && racing.FreedBackends.SequenceEqual(racing.CreatedBackends),
                "A reader disappearing between feature inspection and initialization is freed and skipped");
        Check(racing.ShutdownContexts.Count == 1 && racing.LiveBackends.Count == 0, "Reader-availability races leave no owned native handles");
    }

    private static void VerifyPrismBackendFailures()
    {
        foreach (string stage in new[] { "create", "features", "initialize", "speak", "initialize-result", "speak-result" })
        {
            var failure = new InvalidOperationException("Simulated Prism " + stage);
            var scenario = new PrismBackendScenario
            {
                CreateFailure = stage == "create" ? failure : null,
                FeatureFailure = stage == "features" ? failure : null,
                InitializeFailure = stage == "initialize" ? failure : null,
                SpeechFailure = stage == "speak" ? failure : null,
                InitializeResult = stage == "initialize-result" ? PrismError.PRISM_ERROR_INTERNAL : PrismError.PRISM_OK,
                SpeechResult = stage == "speak-result" ? PrismError.PRISM_ERROR_SPEAK_FAILURE : PrismError.PRISM_OK
            };
            var native = new FakePrismSpeechApi();
            native.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario { Features = 0 });
            native.Readers.Add(PrismBackendId.Jaws, scenario);
            native.Readers.Add(PrismBackendId.ZoomText, new PrismBackendScenario());
            using var speech = new PrismScreenReaderSpeech(native);
            int fallbacks = 0;
            var reports = new List<Exception>();
            UiNotificationRouting.Announce(speech, "failed request", () => fallbacks++, reports.Add);
            Check(fallbacks == 1 && reports.Count == 1 && reports[0] is InvalidOperationException
                && (stage.EndsWith("-result", StringComparison.Ordinal) || ReferenceEquals(reports[0], failure)),
                "Prism backend failure is reported and routed to fallback once: " + stage);
            Check(native.RequestedReaders.SequenceEqual(ExpectedPrismReaders.Take(2))
                && native.FreedBackends.SequenceEqual(native.CreatedBackends) && native.LiveBackends.Count == 0
                && native.ShutdownContexts.Count == 0,
                "Backend failures free every acquired handle and preserve context ownership until disposal: " + stage);
            Check(native.Spoken.Count == (stage.StartsWith("speak", StringComparison.Ordinal) ? 1 : 0),
                "Backend failures never duplicate or prematurely request speech: " + stage);
            speech.Dispose();
            speech.Dispose();
            Check(native.ShutdownContexts.SequenceEqual(new nint[] { native.Context }), "Backend failure cleanup shuts down once: " + stage);
        }
    }

    private static void VerifyPrismContextFailures()
    {
        foreach (Exception? failure in new Exception?[]
        {
            null, new DllNotFoundException("Simulated missing Prism"), new BadImageFormatException("Simulated incompatible Prism"),
            new EntryPointNotFoundException("Simulated missing Prism entrypoint"), new COMException("Simulated Prism context failure")
        })
        {
            var native = new FakePrismSpeechApi { InitializeResult = 0, InitializeFailure = failure };
            using var speech = new PrismScreenReaderSpeech(native);
            int fallbacks = 0;
            var reports = new List<Exception>();
            UiNotificationRouting.Announce(speech, "initialization failed", () => fallbacks++, reports.Add);
            speech.Dispose();
            speech.Dispose();
            Check(fallbacks == 1 && reports.Count == 1 && (failure is null
                    ? reports[0] is InvalidOperationException : ReferenceEquals(reports[0], failure))
                && native.InitializeCalls == 1 && native.RequestedReaders.Count == 0
                && native.FreedBackends.Count == 0 && native.ShutdownContexts.Count == 0,
                "Failed Prism context initialization reports fallback without freeing or shutting down an invalid context: " + failure?.GetType().Name);
        }

        var recoverable = new FakePrismSpeechApi { InitializeResult = 0 };
        recoverable.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario());
        using (var speech = new PrismScreenReaderSpeech(recoverable))
        {
            Throws<InvalidOperationException>(() => speech.TrySpeak("first initialization failed"), "A null Prism context cannot accept speech");
            recoverable.InitializeResult = recoverable.Context;
            Check(speech.TrySpeak("retry initialization") && recoverable.InitializeCalls == 2 && recoverable.Spoken.Count == 1,
                "An initialization failure does not poison later lazy context initialization");
        }
        Check(recoverable.ShutdownContexts.SequenceEqual(new nint[] { recoverable.Context }), "Only the successfully retried context is shut down");
    }

    private static void VerifyPrismThreadOwnership()
    {
        var native = new FakePrismSpeechApi();
        native.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario());
        using var speech = new PrismScreenReaderSpeech(native);
        Throws<InvalidOperationException>(() => Task.Run(() => speech.TrySpeak("wrong thread before initialization")).GetAwaiter().GetResult(),
            "Prism rejects worker-thread speech before lazy initialization");
        Throws<InvalidOperationException>(() => Task.Run(speech.Dispose).GetAwaiter().GetResult(),
            "Prism rejects worker-thread disposal without disposing the owning service");
        Check(native.Calls.Count == 0 && speech.TrySpeak("owning thread"), "Rejected worker calls leave Prism usable on its owning thread");
        int calls = native.Calls.Count;
        Throws<InvalidOperationException>(() => Task.Run(() => speech.TrySpeak("wrong thread with context")).GetAwaiter().GetResult(),
            "An existing Prism context cannot be used by another thread");
        Throws<InvalidOperationException>(() => Task.Run(speech.Dispose).GetAwaiter().GetResult(),
            "An existing Prism context cannot be shut down by another thread");
        Check(native.Calls.Count == calls && native.ShutdownContexts.Count == 0, "Rejected worker calls never reach an initialized native API");
        speech.Dispose();
        Check(native.ShutdownContexts.Count == 1, "Owning-thread disposal still succeeds after thread violations");
    }

    private static void VerifyPrismReaderRestart()
    {
        const string message = "Zażółć gęślą jaźń — gain 6, mikrofon 🎙️ 日本語";
        var native = new FakePrismSpeechApi();
        native.Readers.Add(PrismBackendId.Nvda, new PrismBackendScenario());
        using var speech = new PrismScreenReaderSpeech(native);
        Check(speech.TrySpeak(message), "Prism accepts a multilingual announcement through the injected API");
        native.Readers[PrismBackendId.Nvda] = new PrismBackendScenario { Features = PrismBackendFeature.SupportsSpeak };
        Check(!speech.TrySpeak("reader stopped") && native.Spoken.Count == 1, "A stopped reader is rechecked and receives no speech");
        native.Readers[PrismBackendId.Nvda] = new PrismBackendScenario();
        Check(speech.TrySpeak(message) && speech.TrySpeak("next message") && native.InitializeCalls == 1
            && native.CreatedBackends.Count == 4 && native.CreatedBackends.Distinct().Count() == 4
            && native.Spoken.Select(call => call.Backend).Distinct().Count() == 3
            && native.FreedBackends.SequenceEqual(native.CreatedBackends) && native.LiveBackends.Count == 0,
            "Every message acquires a fresh reader handle, allowing reader restart while reusing its context");
        Check(native.Spoken.Select(call => call.Message).SequenceEqual(new[] { message, message, "next message" })
            && native.Spoken.All(call => !call.Interrupt),
            "Unicode reaches the Prism API unchanged and every speech request uses interrupt=false");
    }

    private static void VerifyPrismNotificationRouting()
    {
        foreach (bool accepted in new[] { true, false })
        {
            var speech = new PrismRoutingSpeech { Accepted = accepted };
            int fallbacks = 0;
            var reports = new List<Exception>();
            UiNotificationRouting.Announce(speech, "route exactly once", () => fallbacks++, reports.Add);
            Check(speech.Messages.SequenceEqual(new[] { "route exactly once" }) && reports.Count == 0
                && fallbacks == (accepted ? 0 : 1),
                "Direct Prism success suppresses fallback; refusal falls back once: " + accepted);
        }
        foreach (Exception failure in new Exception[]
        {
            new DllNotFoundException("missing native library"), new BadImageFormatException("wrong native architecture"),
            new EntryPointNotFoundException("missing native function"), new InvalidOperationException("native request failed"),
            new COMException("reader COM unavailable")
        })
        {
            var events = new List<string>();
            var speech = new PrismRoutingSpeech { Failure = failure };
            UiNotificationRouting.Announce(speech, "recoverable native failure", () => events.Add("fallback"), exception =>
            {
                Check(ReferenceEquals(exception, failure), "Routing reports the original native speech error: " + failure.GetType().Name);
                events.Add("report");
            });
            Check(speech.Messages.Count == 1 && events.SequenceEqual(new[] { "report", "fallback" })
                && UiNotificationRouting.IsSpeechFailure(failure),
                "Known native errors report and fall back once without escaping routing: " + failure.GetType().Name);
        }
        var quiet = new PrismRoutingSpeech { Failure = new InvalidOperationException("must remain uncalled") };
        int unexpected = 0;
        foreach (string message in new[] { null!, "", " \r\n\t" })
            UiNotificationRouting.Announce(quiet, message, () => unexpected++, exception => unexpected++);
        Check(quiet.Messages.Count == 0 && unexpected == 0, "Blank routing requests touch neither speech, error reporting nor fallback");
        int noReaderFallbacks = 0;
        UiNotificationRouting.Announce(null, "no injected reader", () => noReaderFallbacks++, exception => unexpected++);
        Check(noReaderFallbacks == 1 && unexpected == 0, "A missing speech service falls back exactly once without reporting an error");
        var programmingFailure = new ArgumentException("unexpected caller error");
        var broken = new PrismRoutingSpeech { Failure = programmingFailure };
        Throws<ArgumentException>(() => UiNotificationRouting.Announce(broken, "unexpected failure", () => unexpected++, exception => unexpected++),
            "Routing does not hide errors outside its explicit native speech failure policy");
        Check(unexpected == 0 && broken.Messages.Count == 1, "Unexpected errors are not misreported as native failures or retried through fallback");
    }

    private sealed class PrismBackendScenario
    {
        internal PrismBackendFeature Features { get; init; } = PrismSpeechFeatures;
        internal PrismError InitializeResult { get; init; } = PrismError.PRISM_OK;
        internal PrismError SpeechResult { get; init; } = PrismError.PRISM_OK;
        internal Exception? CreateFailure { get; init; }
        internal Exception? FeatureFailure { get; init; }
        internal Exception? InitializeFailure { get; init; }
        internal Exception? SpeechFailure { get; init; }
    }

    private sealed record PrismSpeechCall(nint Backend, PrismBackendId Identifier, string Message, bool Interrupt);

    private sealed class FakePrismSpeechApi : IPrismSpeechApi
    {
        private readonly Dictionary<nint, (PrismBackendId Identifier, PrismBackendScenario Scenario)> backends = [];
        private bool contextLive;
        private nint nextBackend = 100;
        internal nint Context { get; } = 41;
        internal nint InitializeResult { get; set; } = 41;
        internal Exception? InitializeFailure { get; init; }
        internal int InitializeCalls { get; private set; }
        internal Dictionary<PrismBackendId, PrismBackendScenario> Readers { get; } = [];
        internal List<string> Calls { get; } = [];
        internal List<PrismBackendId> RequestedReaders { get; } = [];
        internal List<nint> CreatedBackends { get; } = [];
        internal List<nint> InitializedBackends { get; } = [];
        internal List<nint> FreedBackends { get; } = [];
        internal HashSet<nint> LiveBackends { get; } = [];
        internal List<PrismSpeechCall> Spoken { get; } = [];
        internal List<nint> ShutdownContexts { get; } = [];

        public nint Initialize()
        {
            Calls.Add("initialize");
            InitializeCalls++;
            if (contextLive) throw new InvalidOperationException("The fake context is already initialized.");
            if (InitializeFailure is not null) throw InitializeFailure;
            contextLive = InitializeResult != 0;
            return InitializeResult;
        }

        public nint CreateBackend(nint context, PrismBackendId identifier)
        {
            Calls.Add("create");
            if (!contextLive || context != Context) throw new InvalidOperationException("Invalid fake Prism context.");
            RequestedReaders.Add(identifier);
            if (!Readers.TryGetValue(identifier, out PrismBackendScenario? scenario)) return 0;
            if (scenario.CreateFailure is not null) throw scenario.CreateFailure;
            nint backend = ++nextBackend;
            backends.Add(backend, (identifier, scenario));
            CreatedBackends.Add(backend);
            LiveBackends.Add(backend);
            return backend;
        }

        public PrismBackendFeature GetFeatures(nint backend)
        {
            Calls.Add("features");
            PrismBackendScenario scenario = GetLiveBackend(backend).Scenario;
            if (scenario.FeatureFailure is not null) throw scenario.FeatureFailure;
            return scenario.Features;
        }

        public PrismError InitializeBackend(nint backend)
        {
            Calls.Add("initialize-backend");
            PrismBackendScenario scenario = GetLiveBackend(backend).Scenario;
            InitializedBackends.Add(backend);
            if (scenario.InitializeFailure is not null) throw scenario.InitializeFailure;
            return scenario.InitializeResult;
        }

        public PrismError Speak(nint backend, string message, bool interrupt)
        {
            Calls.Add("speak");
            var reader = GetLiveBackend(backend);
            if (!InitializedBackends.Contains(backend) || reader.Scenario.InitializeResult != PrismError.PRISM_OK)
                throw new InvalidOperationException("Fake reader was not successfully initialized.");
            Spoken.Add(new(backend, reader.Identifier, message, interrupt));
            if (reader.Scenario.SpeechFailure is not null) throw reader.Scenario.SpeechFailure;
            return reader.Scenario.SpeechResult;
        }

        public void FreeBackend(nint backend)
        {
            Calls.Add("free");
            GetLiveBackend(backend);
            LiveBackends.Remove(backend);
            FreedBackends.Add(backend);
        }

        public void Shutdown(nint context)
        {
            Calls.Add("shutdown");
            if (!contextLive || context == 0 || context != Context || LiveBackends.Count != 0)
                throw new InvalidOperationException("Invalid fake Prism shutdown or leaked backend handles.");
            contextLive = false;
            ShutdownContexts.Add(context);
        }

        private (PrismBackendId Identifier, PrismBackendScenario Scenario) GetLiveBackend(nint backend)
        {
            if (!contextLive || !LiveBackends.Contains(backend)) throw new InvalidOperationException("Fake reader handle is not live.");
            return backends[backend];
        }
    }

    private sealed class PrismRoutingSpeech : IScreenReaderSpeech
    {
        internal bool Accepted { get; init; }
        internal Exception? Failure { get; init; }
        internal List<string> Messages { get; } = [];

        public bool TrySpeak(string message)
        {
            Messages.Add(message);
            if (Failure is not null) throw Failure;
            return Accepted;
        }

        public void Dispose() { }
    }
}
