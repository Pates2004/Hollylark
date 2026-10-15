using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyFocusInvestigationApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            for (int iteration = 0; iteration < 30; iteration++)
            {
                VerifyMainOperationFocus("-investigation-" + iteration);
            }
        }
        finally { application.Shutdown(); }
    }

    private sealed class FocusTransitionProbe : IDisposable
    {
        private readonly MainWindow window;
        private readonly ListBox receivers;
        private readonly TextBlock connection;
        private readonly TabControl tabs;
        private readonly string report;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<string> entries = new();
        private readonly FieldInfo interaction = typeof(MainWindow).GetField("focusInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!;

        internal FocusTransitionProbe(MainWindow window, string name)
        {
            this.window = window;
            receivers = (ListBox)window.FindName("ReceiverList");
            connection = (TextBlock)window.FindName("ConnectionReadout");
            tabs = (TabControl)window.FindName("MainTabs");
            report = Path.Combine(ReportDirectory, name + "-focus-transitions.txt");
            window.GotKeyboardFocus += FocusChanged;
            window.LostKeyboardFocus += FocusChanged;
            window.Activated += ActivationChanged;
            window.Deactivated += ActivationChanged;
            window.PreviewKeyDown += KeyInteraction;
            window.PreviewMouseDown += MouseInteraction;
            receivers.IsEnabledChanged += EnabledChanged;
            window.Dispatcher.Hooks.OperationPosted += OperationChanged;
            window.Dispatcher.Hooks.OperationStarted += OperationChanged;
            window.Dispatcher.Hooks.OperationCompleted += OperationChanged;
            Record("probe-start");
        }

        internal void Record(string phase) => Add(phase + "; focus=" + DescribeFocus(Keyboard.FocusedElement)
            + "; ownWindow=" + (Keyboard.FocusedElement is DependencyObject focused && Window.GetWindow(focused) == window)
            + "; active=" + window.IsActive + "; foreground=" + (GetForegroundWindow() == new WindowInteropHelper(window).Handle)
            + "; interaction=" + interaction.GetValue(window) + "; modifiers=" + Keyboard.Modifiers
            + "; receiversEnabled=" + receivers.IsEnabled + "; receiversVisible=" + receivers.IsVisible
            + "; selectedReceiver=" + receivers.SelectedIndex + "; receiverCount=" + receivers.Items.Count
            + "; tabsAncestor=" + tabs.IsAncestorOf(receivers) + "; connectionVisible=" + connection.IsVisible);

        private void Add(string entry)
        {
            if (entries.Count < 5000) entries.Enqueue(clock.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " " + entry);
        }

        private void FocusChanged(object sender, KeyboardFocusChangedEventArgs arguments)
        {
            Record(arguments.RoutedEvent.Name + ": " + DescribeFocus(arguments.OldFocus) + " -> " + DescribeFocus(arguments.NewFocus));
            Add(Environment.StackTrace);
        }

        private void ActivationChanged(object? sender, EventArgs arguments) => Record("activation-changed");
        private void KeyInteraction(object sender, KeyEventArgs arguments) => Record("key-interaction");
        private void MouseInteraction(object sender, MouseButtonEventArgs arguments) => Record("mouse-interaction");

        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs arguments)
        {
            Record("receivers-enabled: " + arguments.OldValue + " -> " + arguments.NewValue);
            Add(Environment.StackTrace);
        }

        private void OperationChanged(object? sender, DispatcherHookEventArgs arguments) =>
            Add("dispatcher-operation id=" + RuntimeHelpers.GetHashCode(arguments.Operation)
                + "; priority=" + arguments.Operation.Priority + "; status=" + arguments.Operation.Status);

        public void Dispose()
        {
            Record("probe-end");
            window.GotKeyboardFocus -= FocusChanged;
            window.LostKeyboardFocus -= FocusChanged;
            window.Activated -= ActivationChanged;
            window.Deactivated -= ActivationChanged;
            window.PreviewKeyDown -= KeyInteraction;
            window.PreviewMouseDown -= MouseInteraction;
            receivers.IsEnabledChanged -= EnabledChanged;
            window.Dispatcher.Hooks.OperationPosted -= OperationChanged;
            window.Dispatcher.Hooks.OperationStarted -= OperationChanged;
            window.Dispatcher.Hooks.OperationCompleted -= OperationChanged;
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllLines(report, entries);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
