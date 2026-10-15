using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyExpandedSettingsInvestigationApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (string language in new[] { "pl", "en" })
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Localization.SetLanguage(language);
                string scenario = $"settings-selector-{language}-{iteration}";
                var settings = new SettingsWindow(new UiPreferences { Language = language },
                    new UiPreferencesStore(Path.Combine(ReportDirectory, scenario)), (_, _) => { },
                    confirm: (_, _) => false);
                MoveOffscreen(settings);
                try
                {
                    settings.Show();
                    Pump();
                    using var observer = new ExternalArrowObserver(settings, scenario, includeFocus: true);
                    VerifyExpandedNavigation(settings, (ComboBox)settings.FindName("SettingsLanguageChoice"), observer);
                }
                finally { settings.Close(); Pump(); }
            }
        }
        finally { application.Shutdown(); }
    }

    private sealed class SelectorTransitionProbe : IDisposable
    {
        private readonly Window window;
        private readonly ComboBox choice;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<string> entries = [];
        private readonly string path;

        internal SelectorTransitionProbe(Window window, ComboBox choice)
        {
            this.window = window;
            this.choice = choice;
            path = Path.Combine(ReportDirectory, "selector-" + Localization.CurrentLanguage + "-"
                + AutomationProperties.GetAutomationId(choice) + "-" + Guid.NewGuid().ToString("N") + ".txt");
            choice.SelectionChanged += SelectionChanged;
            choice.DropDownOpened += DropDownChanged;
            choice.DropDownClosed += DropDownChanged;
            window.Activated += ActivationChanged;
            window.Deactivated += ActivationChanged;
            window.GotKeyboardFocus += FocusChanged;
            window.LostKeyboardFocus += FocusChanged;
            window.PreviewKeyDown += KeyInteraction;
            Record("probe-start");
        }

        internal void Record(string phase)
        {
            if (entries.Count >= 2000) return;
            entries.Add(clock.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " " + phase + "; selectedIndex=" + choice.SelectedIndex + "; open=" + choice.IsDropDownOpen
                + "; active=" + window.IsActive + "; modifiers=" + Keyboard.Modifiers
                + "; focusWithin=" + choice.IsKeyboardFocusWithin + "; mouseCapture=" + choice.IsMouseCaptureWithin
                + "; focus=" + DescribeFocus(Keyboard.FocusedElement));
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs arguments)
        {
            Record("selection-changed");
            if (entries.Count < 2000) entries.Add(Environment.StackTrace);
        }

        private void DropDownChanged(object? sender, EventArgs arguments)
        {
            Record("dropdown-changed");
            if (entries.Count < 2000) entries.Add(Environment.StackTrace);
        }

        private void ActivationChanged(object? sender, EventArgs arguments) => Record("activation-changed");
        private void FocusChanged(object sender, KeyboardFocusChangedEventArgs arguments) => Record(arguments.RoutedEvent.Name);
        private void KeyInteraction(object sender, KeyEventArgs arguments) => Record("key-interaction");

        public void Dispose()
        {
            Record("probe-end");
            choice.SelectionChanged -= SelectionChanged;
            choice.DropDownOpened -= DropDownChanged;
            choice.DropDownClosed -= DropDownChanged;
            window.Activated -= ActivationChanged;
            window.Deactivated -= ActivationChanged;
            window.GotKeyboardFocus -= FocusChanged;
            window.LostKeyboardFocus -= FocusChanged;
            window.PreviewKeyDown -= KeyInteraction;
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllLines(path, entries);
        }
    }
}
