using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private sealed class ControlLifetimeProbe : IDisposable
    {
        private readonly Window window;
        private readonly FrameworkElement control;
        private readonly List<string> observations;
        private readonly string identifier;

        internal ControlLifetimeProbe(Window window, FrameworkElement control, List<string> observations)
        {
            this.window = window;
            this.control = control;
            this.observations = observations;
            identifier = AutomationProperties.GetAutomationId(control);
            control.Loaded += Loaded;
            control.Unloaded += Unloaded;
            control.DataContextChanged += ContextChanged;
            window.Closed += Closed;
            window.PreviewKeyDown += KeyDown;
            Record("attached");
        }

        internal void VerifyAttached(string stage)
        {
            FrameworkElement? current = Descendants(window).OfType<FrameworkElement>()
                .FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == identifier);
            string state = Describe(stage) + "; current=" + (current is null ? "none" : RuntimeHelpers.GetHashCode(current));
            observations.Add(state);
            Check(ReferenceEquals(control, current) && control.IsLoaded && control.DataContext is not null,
                "Observed control remains attached: " + state);
        }

        private string Describe(string stage) => DateTimeOffset.UtcNow.ToString("O") + " " + identifier + "/" + stage
            + "; instance=" + RuntimeHelpers.GetHashCode(control) + "; loaded=" + control.IsLoaded
            + "; visible=" + control.IsVisible + "; context=" + control.DataContext?.GetType().FullName
            + "; source=" + PresentationSource.FromVisual(control)?.GetType().Name
            + "; items=" + (control is ItemsControl items ? items.Items.Count : -1)
            + "; itemsSource=" + (control as ItemsControl)?.ItemsSource?.GetType().FullName
            + "; tab=" + (window.FindName("MainTabs") as TabControl)?.SelectedIndex
            + "; windowVisible=" + window.IsVisible;

        private void Record(string stage) => observations.Add(Describe(stage));
        private void Loaded(object sender, RoutedEventArgs arguments) => Record("loaded");
        private void Unloaded(object sender, RoutedEventArgs arguments) => Record("unloaded");
        private void ContextChanged(object sender, DependencyPropertyChangedEventArgs arguments) =>
            observations.Add(Describe("context-changed") + Environment.NewLine + Environment.StackTrace);
        private void Closed(object? sender, EventArgs arguments) => Record("window-closed");
        private void KeyDown(object sender, KeyEventArgs arguments) =>
            Record("key=" + arguments.Key + "; system=" + arguments.SystemKey + "; modifiers=" + arguments.KeyboardDevice.Modifiers);

        public void Dispose()
        {
            Record("detached");
            control.Loaded -= Loaded;
            control.Unloaded -= Unloaded;
            control.DataContextChanged -= ContextChanged;
            window.Closed -= Closed;
            window.PreviewKeyDown -= KeyDown;
        }
    }
}
