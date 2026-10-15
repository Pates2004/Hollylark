using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifySliderAutomationProbe()
    {
        if (Application.Current is not null)
            throw new InvalidOperationException("Run the slider automation probe in a fresh process before creating any WPF application.");
        var results = new List<object>();
        var failures = new List<string>();
        string? failureDetails = null;
        string reportPath = Path.Combine(ReportDirectory, "slider-automation-probe.json");
        string previousLanguage = Localization.CurrentLanguage;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            VerifyUnboundSliderAutomationProbe(results, failures);
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                VerifyBoundSliderAutomationProbe(language, results, failures);
            }
            Check(failures.Count == 0, "Slider probe observes each requested value and its genuine UIA event; failures=["
                + string.Join(" | ", failures) + "]; report=" + reportPath);
        }
        catch (Exception exception)
        {
            failureDetails = exception.ToString();
            throw;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    Runtime = Environment.Version.ToString(), Results = results, Failures = failures, Failure = failureDetails
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Slider automation probe report: " + reportPath);
            }
            finally
            {
                Localization.SetLanguage(previousLanguage);
                application.Shutdown();
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    private static void VerifyUnboundSliderAutomationProbe(List<object> results, List<string> failures)
    {
        Slider native = CreateSliderProbeControl(new Slider(), "SliderProbe.Native", "Native slider");
        Slider guarded = CreateSliderProbeControl(new GuardedSettingSlider(), "SliderProbe.Guarded", "Guarded slider");
        guarded.SetResourceReference(FrameworkElement.StyleProperty, typeof(Slider));
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Native slider" });
        panel.Children.Add(native);
        panel.Children.Add(new TextBlock { Text = "Guarded slider without a view model" });
        panel.Children.Add(guarded);
        var window = new Window
        {
            Title = "Slider automation comparison", Width = 420, Height = 250,
            ShowInTaskbar = false, Content = panel
        };
        try
        {
            MoveOffscreen(window);
            window.Show();
            Pump();
            Check(native.DataContext is null && guarded.DataContext is null, "Both side-by-side probe sliders have no view model or binding");
            using var observer = new ExternalArrowObserver(window, "slider-probe-unbound");
            foreach (Slider slider in new[] { native, guarded })
            {
                RecordSliderAutomationProbeCase(observer, slider, slider.GetType().Name + "/SetCurrentValue", false, results, failures);
                RecordSliderAutomationProbeCase(observer, slider, slider.GetType().Name + "/RangeValueProvider", true, results, failures);
            }
        }
        finally { window.Close(); Pump(); }
    }

    private static Slider CreateSliderProbeControl(Slider slider, string identifier, string name)
    {
        slider.Minimum = 1;
        slider.Maximum = 6;
        slider.SmallChange = 1;
        slider.LargeChange = 1;
        slider.TickFrequency = 1;
        slider.IsSnapToTickEnabled = true;
        slider.Width = 320;
        slider.Height = 48;
        slider.Value = 4;
        AutomationProperties.SetAutomationId(slider, identifier);
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static void VerifyBoundSliderAutomationProbe(string language, List<object> results, List<string> failures)
    {
        using var transport = new FakeTransport();
        MainWindow window = CreatePresentationWindow(transport, "slider-probe-main-" + language, new MemoryDesktop());
        try
        {
            RunTask(window.ConnectAsync());
            SavePreferences(window, window.CurrentPreferences with
            {
                AutoApplySelections = false, GainControlMode = GainControlMode.Slider, HiddenRows = [],
                ShowHints = false, ShowOperationMessages = false, ConfirmExit = false, CloseToTray = false
            });
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
            Pump();
            Slider slider = FindById<GuardedSettingSlider>(window, "Gain.Slider");
            SettingRowViewModel gain = window.AudioSettings.Single(setting => setting.Id == "Gain");
            Check(window.IsConnected && gain.IsEnabled && window.Transmitters.Any(transmitter => transmitter.IsConnected),
                "Bound slider probe stays online with simulated transmitters: " + language);
            int writes = CountMockWrites(transport);
            using (var observer = new ExternalArrowObserver(window, "slider-probe-main-" + language))
                RecordSliderAutomationProbeCase(observer, slider, "MainWindow/" + language + "/RangeValueProvider", true, results, failures);
            Check(CountMockWrites(transport) == writes, "Online slider probe sends no setting writes: " + language);
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void RecordSliderAutomationProbeCase(ExternalArrowObserver observer, Slider slider, string scenario,
        bool useProvider, List<object> results, List<string> failures)
    {
        string identifier = AutomationProperties.GetAutomationId(slider);
        slider.SetCurrentValue(Slider.ValueProperty, 4.0);
        FocusForKeyboardCheck(slider);
        Pump();
        ArrowAutomationMessage baseline = observer.Snapshot(identifier);
        DrainSliderProbeEvents(TimeSpan.FromMilliseconds(250));
        AutomationPeer peer = GetAccessiblePeer(slider);
        var provider = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
        bool boundBefore = BindingOperations.IsDataBound(slider, Slider.ValueProperty);
        var transitions = new List<object>();
        var physicalInput = new List<object>();
        RoutedPropertyChangedEventHandler<double> valueChanged = (_, arguments) => transitions.Add(new
        {
            Time = DateTimeOffset.UtcNow, arguments.OldValue, arguments.NewValue, EffectiveValue = slider.Value,
            ModelValue = (slider.DataContext as SettingRowViewModel)?.SelectedLevel,
            SamePeer = ReferenceEquals(peer, UIElementAutomationPeer.FromElement(slider))
        });
        KeyEventHandler keyObserved = (_, arguments) => physicalInput.Add(new
        {
            Time = DateTimeOffset.UtcNow, Key = arguments.Key.ToString(), SystemKey = arguments.SystemKey.ToString(),
            Modifiers = arguments.KeyboardDevice.Modifiers.ToString(), arguments.IsRepeat
        });
        slider.ValueChanged += valueChanged;
        slider.PreviewKeyDown += keyObserved;
        int marker = observer.Mark();
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        try
        {
            if (useProvider) provider.SetValue(3.0);
            else slider.SetCurrentValue(Slider.ValueProperty, 3.0);
            Pump();
            ArrowAutomationMessage changed = observer.Snapshot(identifier);
            bool HasRequestedEvent() => observer.Since(marker).Any(message => message.Kind == "property"
                && message.AutomationId == identifier && message.Value == "3"
                && message.Detail.StartsWith("RangeValuePatternIdentifiers.ValueProperty=", StringComparison.Ordinal));
            var waiting = Stopwatch.StartNew();
            while (!HasRequestedEvent() && waiting.Elapsed < TimeSpan.FromSeconds(3))
            {
                Pump();
                Thread.Sleep(5);
            }
            DrainSliderProbeEvents(TimeSpan.FromMilliseconds(150));
            ArrowAutomationMessage final = observer.Snapshot(identifier);
            DrainSliderProbeEvents(TimeSpan.FromMilliseconds(150));
            ArrowAutomationMessage[] observed = observer.Since(marker);
            bool eventReceived = HasRequestedEvent();
            bool valueCorrect = baseline.Value == "4" && changed.Value == "3" && final.Value == "3" && slider.Value == 3.0;
            bool bindingPreserved = BindingOperations.IsDataBound(slider, Slider.ValueProperty) == boundBefore;
            results.Add(new
            {
                Scenario = scenario, Identifier = identifier, RequestedAt = requestedAt, CompletedAt = DateTimeOffset.UtcNow,
                ControlType = slider.GetType().FullName, PeerType = peer.GetType().FullName,
                PeerIdentity = RuntimeHelpers.GetHashCode(peer), SamePeerAfter = ReferenceEquals(peer, UIElementAutomationPeer.FromElement(slider)),
                HasDataContext = slider.DataContext is not null, BoundBefore = boundBefore, BindingPreserved = bindingPreserved,
                slider.IsVisible, slider.IsEnabled, slider.IsKeyboardFocused, PhysicalModifiers = Keyboard.PrimaryDevice.Modifiers.ToString(),
                FinalValue = slider.Value, ModelValue = (slider.DataContext as SettingRowViewModel)?.SelectedLevel,
                ValueCorrect = valueCorrect, RequestedEventReceived = eventReceived,
                Baseline = baseline, Changed = changed, Final = final, Events = observed,
                ValueChangedTransitions = transitions, ObservedKeyboardInput = physicalInput
            });
            Console.WriteLine("SLIDER PROBE: " + scenario + "; value-correct=" + valueCorrect + "; expected-event=" + eventReceived
                + "; value-changed-count=" + transitions.Count + "; physical-keys=" + physicalInput.Count
                + "; final=" + slider.Value.ToString(CultureInfo.InvariantCulture));
            if (!valueCorrect || !eventReceived || !bindingPreserved)
                failures.Add(scenario + "; value-correct=" + valueCorrect + "; expected-event=" + eventReceived
                    + "; binding-preserved=" + bindingPreserved + "; physical-keys=" + physicalInput.Count);
        }
        finally
        {
            slider.ValueChanged -= valueChanged;
            slider.PreviewKeyDown -= keyObserved;
        }
    }

    private static void DrainSliderProbeEvents(TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration)
        {
            Pump();
            Thread.Sleep(5);
        }
    }
}
