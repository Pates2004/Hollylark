using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed record ArrowAutomationMessage(string Kind, string AutomationId, string Name,
        string? Value, string[] Selection, string Detail)
    {
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReceivedAt { get; init; }
        public ArrowElementMetadata? Element { get; init; }
        public ArrowSelectionMetadata[] SelectedElements { get; init; } = [];
    }

    private sealed record ArrowElementMetadata(int[] RuntimeId, string Name, string Framework, string ClassName,
        string ControlType, bool IsControlElement, bool IsContentElement, bool IsKeyboardFocusable,
        bool HasKeyboardFocus, bool IsOffscreen, bool IsEnabled, bool HasSelectionItemPattern, bool? IsSelected);

    private sealed record ArrowSelectionMetadata(int[] RuntimeId, string Name, bool IsSelected,
        int[] SelectionContainerRuntimeId, ArrowElementMetadata? Element, string? UnavailableDetails);

    private static TResult ReadArrowValue<TResult>(string operation, Func<TResult> read, Action<string>? reading)
    {
        reading?.Invoke(operation);
        return read();
    }

    private static ArrowSelectionMetadata ReadArrowSelection(AutomationElement element, Action<string>? reading = null)
    {
        TResult Read<TResult>(string operation, Func<TResult> read) => ReadArrowValue(operation, read, reading);
        var selection = Read("SelectionItemPattern.Pattern", () => (SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern));
        int[] runtimeId = Read("GetRuntimeId", element.GetRuntimeId);
        string name = Read("Name", () => element.Current.Name);
        bool selected = Read("SelectionItemPattern.IsSelected", () => selection.Current.IsSelected);
        AutomationElement container = Read("SelectionItemPattern.SelectionContainer", () => selection.Current.SelectionContainer);
        int[] containerRuntimeId = Read("SelectionItemPattern.SelectionContainer.GetRuntimeId", container.GetRuntimeId);
        ArrowElementMetadata? details = null;
        string? unavailable = null;
        try { details = ReadArrowMetadata(element, cached: false, reading: reading); }
        catch (ElementNotAvailableException exception) { unavailable = exception.ToString(); }
        return new(runtimeId, name, selected, containerRuntimeId, details, unavailable);
    }

    private static ArrowElementMetadata ReadArrowMetadata(AutomationElement element, bool cached, Action<string>? reading = null)
    {
        AutomationElement.AutomationElementInformation information = cached ? element.Cached : element.Current;
        TResult Read<TResult>(AutomationProperty property, Func<TResult> read) => ReadArrowValue(property.ProgrammaticName, read, reading);
        object Property(AutomationProperty property) => Read(property,
            () => cached ? element.GetCachedPropertyValue(property) : element.GetCurrentPropertyValue(property));
        bool selectionItem = Property(AutomationElement.IsSelectionItemPatternAvailableProperty) is true;
        return new(Property(AutomationElement.RuntimeIdProperty) as int[] ?? [],
            Read(AutomationElement.NameProperty, () => information.Name),
            Read(AutomationElement.FrameworkIdProperty, () => information.FrameworkId),
            Read(AutomationElement.ClassNameProperty, () => information.ClassName),
            Read(AutomationElement.ControlTypeProperty, () => information.ControlType).ProgrammaticName,
            Read(AutomationElement.IsControlElementProperty, () => information.IsControlElement),
            Read(AutomationElement.IsContentElementProperty, () => information.IsContentElement),
            Read(AutomationElement.IsKeyboardFocusableProperty, () => information.IsKeyboardFocusable),
            Read(AutomationElement.HasKeyboardFocusProperty, () => information.HasKeyboardFocus),
            Read(AutomationElement.IsOffscreenProperty, () => information.IsOffscreen),
            Read(AutomationElement.IsEnabledProperty, () => information.IsEnabled),
            selectionItem, selectionItem ? Property(SelectionItemPattern.IsSelectedProperty) as bool? : null);
    }

    private static void VerifyArrowFocusMetadata(ExternalArrowObserver observer, int marker, string expected)
    {
        Check(observer.WaitForEvent(() => HasNativeArrowFocus(observer.Since(marker), expected), "native WPF focus metadata: " + expected),
            "External focused item has a native WPF identity, control/content exposure and selection support: " + expected);
    }

    private static bool HasNativeArrowFocus(IEnumerable<ArrowAutomationMessage> messages, string expected) =>
        messages.Any(message => message.Kind == "focus" && message.Name == expected
            && message.Element is { Framework: "WPF", ControlType: "ControlType.ListItem", IsControlElement: true,
                IsContentElement: true, IsKeyboardFocusable: true, HasKeyboardFocus: true, IsEnabled: true,
                HasSelectionItemPattern: true, RuntimeId.Length: > 0 });

    private static void VerifyArrowFocusMetadataGuards()
    {
        const string expected = "Microphone 2";
        var metadata = new ArrowElementMetadata([7, 1, 2], expected, "WPF", "ListBoxItem", "ControlType.ListItem",
            true, true, true, true, false, true, true, false);
        var native = new ArrowAutomationMessage("focus", "TX2.Item", expected, null, [], "native") { Element = metadata };
        var proxy = native with { Element = metadata with { Framework = "", ClassName = "", IsKeyboardFocusable = false } };
        Check(!HasNativeArrowFocus([proxy], expected) && HasNativeArrowFocus([proxy, native], expected),
            "An early compatibility-proxy focus event cannot replace the later native WPF event");
        Check(HasNativeArrowFocus([native], expected), "Native focus may precede selection without weakening role or pattern checks");
        foreach (ArrowElementMetadata rejected in new[]
        {
            metadata with { Framework = "" }, metadata with { ControlType = "ControlType.Text" },
            metadata with { IsControlElement = false }, metadata with { IsContentElement = false },
            metadata with { IsKeyboardFocusable = false }, metadata with { HasKeyboardFocus = false },
            metadata with { IsEnabled = false }, metadata with { HasSelectionItemPattern = false }, metadata with { RuntimeId = [] }
        })
            Check(!HasNativeArrowFocus([native with { Element = rejected }], expected),
                "Native focus still requires every original identity, role, focus and selection-pattern guard");
        Check(!HasNativeArrowFocus([native with { Kind = "selection" }], expected)
            && !HasNativeArrowFocus([native with { Name = "previous item" }], expected)
            && !HasNativeArrowFocus([native with { Element = null }], expected),
            "Selection-only, previous-name and absent-metadata records cannot satisfy native focus");
    }

    private static int ObserveArrowAutomation(string handleText, bool includeFocus)
    {
        int exitCode = 0;
        var worker = new Thread(() =>
        {
            var outputLock = new object();
            void Write(ArrowAutomationMessage message)
            {
                lock (outputLock)
                {
                    Console.WriteLine(JsonSerializer.Serialize(message));
                    Console.Out.Flush();
                }
            }
            void HandleEvent(Action action, string eventKind)
            {
                try { action(); }
                catch (ElementNotAvailableException exception)
                {
                    Write(new("eventUnavailable", string.Empty, string.Empty, null, [], eventKind + ": " + exception));
                }
                catch (Exception exception)
                {
                    Write(new("error", string.Empty, string.Empty, null, [], eventKind + ": " + exception));
                }
            }
            void InitializationStage(string stage) => Write(new("stage", "__startup", string.Empty, null, [], stage));
            AutomationElement? EventSource(object sender, string eventKind)
            {
                if (sender is AutomationElement element) return element;
                Write(new("eventUnavailable", string.Empty, string.Empty, null, [], eventKind + ": native callback has no element source"));
                return null;
            }
            ArrowAutomationMessage Capture(string kind, AutomationElement element, string detail, string identifier)
            {
                string? lastRead = null;
                void Reading(string operation)
                {
                    lastRead = operation;
                    Write(new("stage", identifier, string.Empty, null, [], "capture-read:" + operation));
                }
                TResult Read<TResult>(string operation, Func<TResult> read) => ReadArrowValue(operation, read, Reading);
                try
                {
                    Reading("ValuePattern.Pattern");
                    string? value = element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePattern)
                        ? Read("ValuePattern.Value", () => ((ValuePattern)valuePattern).Current.Value) : null;
                    Reading("RangeValuePattern.Pattern");
                    if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? rangePattern))
                        value = Read("RangeValuePattern.Value", () => ((RangeValuePattern)rangePattern).Current.Value).ToString(CultureInfo.InvariantCulture);
                    Reading("SelectionPattern.Pattern");
                    AutomationElement[] selection = element.TryGetCurrentPattern(SelectionPattern.Pattern, out object? selectionPattern)
                        ? Read("SelectionPattern.GetSelection", () => ((SelectionPattern)selectionPattern).Current.GetSelection()) : [];
                    return new(kind, Read("AutomationId", () => element.Current.AutomationId), Read("Name", () => element.Current.Name), value,
                        selection.Select((item, index) => Read("selection[" + index + "].Name", () => item.Current.Name)).ToArray(), detail)
                    {
                        Element = ReadArrowMetadata(element, cached: false, reading: operation => Reading("element." + operation)),
                        SelectedElements = selection.Select((item, index) => ReadArrowSelection(item,
                            operation => Reading("selection[" + index + "]." + operation))).ToArray()
                    };
                }
                catch (Exception exception)
                {
                    return new("error", identifier, string.Empty, null, [], "capture-read:" + lastRead + "; " + exception);
                }
            }
            try
            {
                InitializationStage("worker-start; helper=" + Environment.ProcessId + "; target-handle=" + handleText);
                InitializationStage("from-handle-start");
                var root = AutomationElement.FromHandle(new IntPtr(long.Parse(handleText, CultureInfo.InvariantCulture)));
                InitializationStage("from-handle-complete");
                InitializationStage("target-process-id-start");
                int processId = root.Current.ProcessId;
                InitializationStage("target-process-id-complete; target-process=" + processId);
                AutomationFocusChangedEventHandler focusChanged = (sender, _) => HandleEvent(() =>
                {
                    AutomationElement? element = EventSource(sender, "focus callback");
                    if (element is null) return;
                    if (element.Cached.ProcessId != processId) return;
                    Write(new("focus", element.Cached.AutomationId, element.Cached.Name, null, [],
                        element.Cached.ControlType.ProgrammaticName + "; focused=" + element.Cached.HasKeyboardFocus)
                    { Element = ReadArrowMetadata(element, cached: true) });
                }, "focus callback");
                AutomationPropertyChangedEventHandler propertyChanged = (sender, eventArgs) => HandleEvent(() =>
                {
                    AutomationElement? element = EventSource(sender, "property callback " + eventArgs.Property.ProgrammaticName);
                    if (element is null) return;
                    string? value = Convert.ToString(eventArgs.NewValue, CultureInfo.InvariantCulture);
                    Write(new("property", element.Cached.AutomationId, element.Cached.Name, value, [], eventArgs.Property.ProgrammaticName + "=" + value)
                    { Element = ReadArrowMetadata(element, cached: true) });
                }, "property callback");
                AutomationEventHandler selected = (sender, eventArgs) => HandleEvent(() =>
                {
                    AutomationElement? element = EventSource(sender, "selection callback");
                    if (element is null) return;
                    string kind = eventArgs.EventId == SelectionPattern.InvalidatedEvent ? "selectionInvalidated" : "selection";
                    Write(new(kind, element.Cached.AutomationId, element.Cached.Name, null, [], eventArgs.EventId.ProgrammaticName)
                    { Element = ReadArrowMetadata(element, cached: true) });
                }, "selection callback");
                InitializationStage("cache-configuration-start");
                var cache = new CacheRequest();
                cache.Add(AutomationElement.AutomationIdProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ProcessIdProperty);
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.HasKeyboardFocusProperty);
                cache.Add(AutomationElement.RuntimeIdProperty);
                cache.Add(AutomationElement.FrameworkIdProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.IsControlElementProperty);
                cache.Add(AutomationElement.IsContentElementProperty);
                cache.Add(AutomationElement.IsKeyboardFocusableProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.IsEnabledProperty);
                cache.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
                cache.Add(SelectionItemPattern.IsSelectedProperty);
                InitializationStage("cache-configuration-complete");
                using (cache.Activate())
                {
                    if (includeFocus)
                    {
                        InitializationStage("subscribe-focus-start");
                        Automation.AddAutomationFocusChangedEventHandler(focusChanged);
                        InitializationStage("subscribe-focus-complete");
                    }
                    InitializationStage("subscribe-properties-start");
                    Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, propertyChanged,
                        ValuePattern.ValueProperty, RangeValuePattern.ValueProperty, SelectionItemPattern.IsSelectedProperty);
                    InitializationStage("subscribe-properties-complete");
                    InitializationStage("subscribe-selection-start");
                    Automation.AddAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, root, TreeScope.Subtree, selected);
                    InitializationStage("subscribe-selection-complete");
                    InitializationStage("subscribe-selection-invalidated-start");
                    Automation.AddAutomationEventHandler(SelectionPattern.InvalidatedEvent, root, TreeScope.Subtree, selected);
                    InitializationStage("subscribe-selection-invalidated-complete");
                }
                try
                {
                    InitializationStage("initialization-complete");
                    Write(new("ready", string.Empty, string.Empty, null, [], string.Empty));
                    var elements = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
                    while (Console.ReadLine() is string command && command != "quit")
                    {
                        Write(new("stage", command, string.Empty, null, [], "command-received"));
                        if (command == "__focus")
                        {
                            AutomationElement? focused = AutomationElement.FocusedElement;
                            Write(focused is not null && focused.Current.ProcessId == processId
                                ? Capture("snapshot", focused, focused.Current.ControlType.ProgrammaticName, command)
                                : new("snapshot", string.Empty, string.Empty, null, [], "Focus outside fixture: " + focused?.Current.ProcessId));
                            continue;
                        }
                        if (!elements.TryGetValue(command, out AutomationElement? element))
                        {
                            Write(new("stage", command, string.Empty, null, [], "lookup-start"));
                            element = root.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.AutomationIdProperty, command));
                            Write(new("stage", command, string.Empty, null, [], "lookup-end"));
                            if (element is not null) elements.Add(command, element);
                        }
                        Write(new("stage", command, string.Empty, null, [], "capture-start"));
                        ArrowAutomationMessage snapshot = element is null ? new("error", command, string.Empty, null, [], "Element not found")
                            : Capture("snapshot", element, string.Empty, command);
                        Write(new("stage", command, string.Empty, null, [], "capture-end"));
                        Write(snapshot);
                        Write(new("stage", command, string.Empty, null, [], "snapshot-written"));
                    }
                }
                finally
                {
                    if (includeFocus) Automation.RemoveAutomationFocusChangedEventHandler(focusChanged);
                    Automation.RemoveAutomationPropertyChangedEventHandler(root, propertyChanged);
                    Automation.RemoveAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, root, selected);
                    Automation.RemoveAutomationEventHandler(SelectionPattern.InvalidatedEvent, root, selected);
                }
            }
            catch (Exception exception)
            {
                Write(new("error", string.Empty, string.Empty, null, [], exception.ToString()));
                exitCode = 1;
            }
        });
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
        return exitCode;
    }

    private sealed class ExternalArrowObserver : IDisposable
    {
        private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(12);
        private readonly Process process;
        private readonly ConcurrentQueue<ArrowAutomationMessage> messages = new();
        private readonly ConcurrentQueue<string> errors = new();
        private readonly List<object> snapshotRequests = [];
        private readonly List<object> waitMeasurements = [];
        private readonly List<string> cleanupSteps = [];
        private readonly string reportPath;
        private readonly string targetHandle;
        private readonly DateTimeOffset initializationStartedAt = DateTimeOffset.UtcNow;
        private DateTimeOffset? initializationCompletedAt;
        private int? helperProcessId;
        private int? exitCode;
        private bool processStarted;
        private bool outputReading;
        private bool errorReading;
        private volatile bool outputCompleted;
        private volatile bool errorCompleted;
        private bool forcedTermination;
        private bool disposed;
        private bool snapshotFailed;
        private Exception? operationFailure;

        internal ExternalArrowObserver(Window window, string reportName, bool includeFocus = false)
        {
            reportPath = Path.Combine(ReportDirectory, reportName + "-uia-events.json");
            targetHandle = new WindowInteropHelper(window).Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--observe-arrow-automation");
            start.ArgumentList.Add(targetHandle);
            if (includeFocus) start.ArgumentList.Add("--include-focus");
            process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not string line) { outputCompleted = true; return; }
                try
                {
                    if (JsonSerializer.Deserialize<ArrowAutomationMessage>(line) is { } message)
                        messages.Enqueue(message with { ReceivedAt = DateTimeOffset.UtcNow });
                }
                catch (JsonException exception) { errors.Enqueue(exception.Message + ": " + line); }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is string error) errors.Enqueue(error);
                else errorCompleted = true;
            };
            try
            {
                processStarted = process.Start();
                if (!processStarted) throw new InvalidOperationException("The owned UIA observer process did not start.");
                helperProcessId = process.Id;
                process.BeginOutputReadLine();
                outputReading = true;
                process.BeginErrorReadLine();
                errorReading = true;
                WaitForSignal(() => messages.Any(message => message.Kind is "ready" or "error") || process.HasExited,
                    InitializationTimeout, "initialization handshake");
                if (messages.FirstOrDefault(message => message.Kind == "error") is { } error)
                    throw new InvalidOperationException(Context("initialization error") + "; " + error.Detail);
                if (!messages.Any(message => message.Kind == "ready"))
                    throw new InvalidOperationException(Context("helper exited before initialization completed"));
                initializationCompletedAt = DateTimeOffset.UtcNow;
                Check(messages.Any(message => message.Kind == "ready"), "External UIA client subscribes to the test window");
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                var cleanupFailures = new List<Exception>();
                try { WriteReport(cleanupFailures); }
                catch (Exception reportFailure) { cleanupFailures.Add(reportFailure); }
                cleanupFailures.AddRange(StopOwnedHelper());
                PersistAndDispose(cleanupFailures);
                if (cleanupFailures.Count > 0)
                    throw new AggregateException("UIA observer initialization and cleanup failed; report=" + reportPath,
                        new[] { exception }.Concat(cleanupFailures));
                throw;
            }
        }

        internal int Mark() => messages.Count;

        internal bool WaitForEvent(Func<bool> condition, string operation) => WaitForEvent(condition, ActionTimeout, operation);

        internal bool WaitForEvent(Func<bool> condition, TimeSpan timeout, string operation)
        {
            try
            {
                WaitForSignal(condition, timeout, operation);
                return true;
            }
            catch (TimeoutException) { return false; }
        }

        internal ArrowAutomationMessage Snapshot(string identifier)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            bool completed = false;
            try
            {
                int marker = Mark();
                process.StandardInput.WriteLine(identifier);
                process.StandardInput.Flush();
                WaitForSignal(() => messages.Skip(marker).Any(message => message.Kind is "snapshot" or "error") || process.HasExited,
                    ActionTimeout, "snapshot " + identifier);
                ArrowAutomationMessage[] received = messages.Skip(marker).ToArray();
                if (received.FirstOrDefault(message => message.Kind == "error") is { } error)
                    throw new InvalidOperationException("External UIA snapshot failed: " + error.Detail);
                ArrowAutomationMessage snapshot = received.LastOrDefault(message => message.Kind == "snapshot"
                    && (identifier == "__focus" || message.AutomationId == identifier))
                    ?? throw new InvalidOperationException(Context("helper exited before snapshot " + identifier));
                completed = true;
                return snapshot;
            }
            catch (Exception exception) { snapshotFailed = true; operationFailure = exception; throw; }
            finally { snapshotRequests.Add(new { Identifier = identifier, RequestedAt = requested, CompletedAt = DateTimeOffset.UtcNow, Success = completed }); }
        }

        internal ArrowAutomationMessage[] Since(int marker) => messages.Skip(marker).ToArray();

        public void Dispose()
        {
            if (disposed) return;
            List<Exception> cleanupFailures = StopOwnedHelper();
            PersistAndDispose(cleanupFailures);
            if (cleanupFailures.Count > 0)
            {
                if (operationFailure is not null) cleanupFailures.Insert(0, operationFailure);
                throw new AggregateException("UIA observer cleanup failed; report=" + reportPath,
                    cleanupFailures);
            }
            if (!snapshotFailed)
                Check(exitCode == 0 && !forcedTermination && errors.IsEmpty && !messages.Any(message => message.Kind == "error"),
                    "External UIA observer exits without errors: " + reportPath);
        }

        private string Context(string operation)
        {
            ArrowAutomationMessage? lastStage = messages.LastOrDefault(message => message.Kind == "stage");
            return "External UIA observer " + operation + "; helper=" + helperProcessId + "; target-handle=" + targetHandle
                + "; last-stage=" + (lastStage is null ? "no helper stage received" : lastStage.AutomationId + ":" + lastStage.Detail)
                + "; report=" + reportPath;
        }

        private void WaitForSignal(Func<bool> condition, TimeSpan timeout, string operation)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            int signalChecks = 0;
            bool completed = false;
            try
            {
                if (condition())
                {
                    completed = true;
                    return;
                }
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
                Exception? failure = null;
                void CheckSignal(object? sender, EventArgs eventArgs)
                {
                    signalChecks++;
                    try
                    {
                        if (condition()) completed = true;
                        else if (elapsed.Elapsed >= timeout)
                            failure = new TimeoutException(Context(operation) + "; timeout-seconds=" + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception exception) { failure = exception; }
                    if (completed || failure is not null) frame.Continue = false;
                }
                timer.Tick += CheckSignal;
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally
                {
                    timer.Stop();
                    timer.Tick -= CheckSignal;
                }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                if (!completed) throw new InvalidOperationException(Context("dispatcher stopped before " + operation));
            }
            finally
            {
                ArrowAutomationMessage? lastStage = messages.LastOrDefault(message => message.Kind == "stage");
                waitMeasurements.Add(new
                {
                    Operation = operation, StartedAt = startedAt, CompletedAt = DateTimeOffset.UtcNow,
                    ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, TimeoutSeconds = timeout.TotalSeconds,
                    Success = completed, DispatcherMode = "ContinuousFrame", SignalChecks = signalChecks,
                    LastStage = lastStage is null ? null : lastStage.AutomationId + ":" + lastStage.Detail
                });
            }
        }

        private List<Exception> StopOwnedHelper()
        {
            var failures = new List<Exception>();
            if (!processStarted) return failures;
            try
            {
                if (!process.HasExited)
                {
                    cleanupSteps.Add("quit-requested; helper=" + helperProcessId);
                    try
                    {
                        process.StandardInput.WriteLine("quit");
                        process.StandardInput.Flush();
                    }
                    catch (Exception exception)
                    {
                        cleanupSteps.Add("quit-request-failed: " + exception);
                        failures.Add(exception);
                    }
                    try { WaitForSignal(() => process.HasExited, TimeSpan.FromSeconds(5), "graceful shutdown"); }
                    catch (TimeoutException exception) { cleanupSteps.Add(exception.ToString()); }
                    if (!process.HasExited)
                    {
                        forcedTermination = true;
                        cleanupSteps.Add("terminating-owned-helper-only; helper=" + helperProcessId);
                        process.Kill(entireProcessTree: false);
                        WaitForSignal(() => process.HasExited, TimeSpan.FromSeconds(5), "owned helper termination");
                    }
                }
                exitCode = process.ExitCode;
                cleanupSteps.Add("helper-exited; exit-code=" + exitCode);
                WaitForSignal(() => (!outputReading || outputCompleted) && (!errorReading || errorCompleted),
                    TimeSpan.FromSeconds(5), "output drain");
            }
            catch (Exception exception) { failures.Add(exception); }
            return failures;
        }

        private void WriteReport(IReadOnlyList<Exception> cleanupFailures)
        {
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Events = messages.ToArray(), SnapshotRequests = snapshotRequests, WaitMeasurements = waitMeasurements,
                Errors = errors.ToArray(), ExitCode = exitCode,
                TargetHandle = targetHandle, HelperProcessId = helperProcessId,
                InitializationStartedAt = initializationStartedAt, InitializationCompletedAt = initializationCompletedAt,
                InitializationTimeoutSeconds = InitializationTimeout.TotalSeconds, ActionTimeoutSeconds = ActionTimeout.TotalSeconds,
                Failure = operationFailure?.ToString(), ForcedTermination = forcedTermination,
                CleanupSteps = cleanupSteps, CleanupFailures = cleanupFailures.Select(exception => exception.ToString()).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void PersistAndDispose(List<Exception> cleanupFailures)
        {
            try { WriteReport(cleanupFailures); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            finally
            {
                try { process.Dispose(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                disposed = true;
            }
        }
    }
}
