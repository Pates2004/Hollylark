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
    private static readonly List<string> arrowAnnouncementFailures = [];
    private static void VerifyArrowAnnouncementApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyArrowAnnouncements(); }
        finally { application.Shutdown(); }
        Console.WriteLine("Validation reports: " + ReportDirectory);
    }

    private static void VerifyArrowAnnouncements()
    {
        arrowAnnouncementFailures.Clear();
        VerifyReferenceComboEvents();
        foreach (string language in new[] { "pl", "en" })
        {
            Localization.SetLanguage(language);
            var preferences = new UiPreferences { Language = language };
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "arrows-" + language));
            var setup = new FirstRunWindow(preferences, store);
            MoveOffscreen(setup);
            try
            {
                setup.Show();
                Pump();
                using var observer = new ExternalArrowObserver(setup, "setup-" + language);
                foreach (string identifier in new[] { "FirstRunLanguageChoice", "FirstRunThemeChoice", "FirstRunScaleChoice", "FirstRunValueDisplayChoice" })
                    VerifyComboArrowEvents(setup, FindById<ComboBox>(setup, identifier), observer);
            }
            finally { setup.Close(); Pump(); }
            Localization.SetLanguage(language);
            var settings = new SettingsWindow(preferences, store, (_, _) => throw new InvalidOperationException("Arrow checks must not save settings."), confirm: (_, _) => false);
            MoveOffscreen(settings);
            try
            {
                settings.Show();
                Pump();
                using var observer = new ExternalArrowObserver(settings, "settings-" + language);
                foreach (TabItem tab in FindById<TabControl>(settings, "SettingsTabs").Items)
                {
                    tab.IsSelected = true;
                    Pump();
                    foreach (ComboBox choice in Descendants(settings).OfType<ComboBox>().Where(control => control.IsVisible && control.IsEnabled).ToArray())
                        VerifyComboArrowEvents(settings, choice, observer);
                }
            }
            finally { settings.Close(); Pump(); }
            using var transport = new FakeTransport();
            MainWindow main = CreateWindow(transport, "arrow-main-" + language);
            try
            {
                RunTask(main.ConnectAsync());
                using var observer = new ExternalArrowObserver(main, "main-" + language);
                foreach (ComboBox choice in Descendants(main).OfType<ComboBox>().Where(control => control.IsVisible && control.IsEnabled).ToArray())
                    VerifyComboArrowEvents(main, choice, observer);
                Check(CountMockWrites(transport) == 0, "Arrow choices only edit drafts on the fake receiver");
            }
            finally { main.Close(); Pump(); }
        }
        Localization.SetLanguage("pl");
        Check(arrowAnnouncementFailures.Count == 0, "Arrow values remain selected and reach the external UIA client: " + string.Join("; ", arrowAnnouncementFailures));
    }

    private static void VerifyReferenceComboEvents()
    {
        var choice = new ComboBox { ItemsSource = new[] { new ComboBoxItem { Content = "First" }, new ComboBoxItem { Content = "Second" } } };
        AutomationProperties.SetAutomationId(choice, "ReferenceCombo");
        AutomationProperties.SetName(choice, "Reference combo");
        var window = new Window { Content = choice, Width = 400, Height = 200, ThemeMode = ThemeMode.System };
        MoveOffscreen(window);
        try
        {
            window.Show();
            Pump();
            using var observer = new ExternalArrowObserver(window, "reference-combo");
            VerifyComboArrowEvents(window, choice, observer);
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyComboArrowEvents(Window window, ComboBox choice, ExternalArrowObserver observer)
    {
        string identifier = AutomationProperties.GetAutomationId(choice);
        using FocusTransitionProbe? focusProbe = window is MainWindow main
            ? new FocusTransitionProbe(main, "closed-arrow-" + Localization.CurrentLanguage + "-" + identifier) : null;
        choice.IsDropDownOpen = false;
        if (choice.Items.Count < 2) return;
        choice.SelectedIndex = 0;
        Pump();
        FocusForKeyboardCheck(choice);
        focusProbe?.Record("before-initial-uia-snapshot");
        ArrowAutomationMessage initial = observer.Snapshot(identifier);
        focusProbe?.Record("after-initial-uia-snapshot");
        FocusForKeyboardCheck(choice);
        choice.SelectedIndex = 0;
        int marker = observer.Mark();
        var transitions = new List<string>();
        SelectionChangedEventHandler trace = (_, _) => transitions.Add($"index={choice.SelectedIndex}; text={choice.Text}; selected={choice.SelectedItem}" + Environment.NewLine + Environment.StackTrace);
        choice.SelectionChanged += trace;
        RaiseLocalKey(choice, Key.Down);
        Check(choice.SelectedIndex == 1, "Arrow selects the next native combo item: " + identifier
            + "; index=" + choice.SelectedIndex + "; modifiers=" + Keyboard.Modifiers
            + "; available=" + (choice.DataContext is SettingRowViewModel setting ? setting.IsEnabled : choice.IsEnabled)
            + "; transitions=" + string.Join(" | ", transitions));
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 700)
        {
            Pump();
            Thread.Sleep(5);
        }
        ArrowAutomationMessage changed = observer.Snapshot(identifier);
        Check(changed.Element is { Framework: "WPF", ControlType: "ControlType.ComboBox", IsControlElement: true,
                IsContentElement: true, IsKeyboardFocusable: true, IsEnabled: true, RuntimeId.Length: > 0 }
            && initial.Element is not null && changed.Element.RuntimeId.SequenceEqual(initial.Element.RuntimeId),
            "External native combo retains its identity and control/content exposure: " + identifier);
        Check(changed.SelectedElements is [{ IsSelected: true, RuntimeId.Length: > 0 }]
            && changed.SelectedElements[0].Name == changed.Selection.Single()
            && changed.SelectedElements[0].SelectionContainerRuntimeId.SequenceEqual(changed.Element!.RuntimeId),
            "External selected item exposes its real name and selected state even without ValuePattern: " + identifier);
        choice.SelectionChanged -= trace;
        Directory.CreateDirectory(ReportDirectory);
        File.WriteAllLines(Path.Combine(ReportDirectory, identifier + "-transitions.txt"), transitions);
        string selected = choice.Items[1] is ComboBoxItem item ? item.Content.ToString()! : choice.Items[1].ToString()!;
        bool stable = choice.SelectedIndex == 1 && (changed.Value is null || changed.Value == selected) && changed.Selection.SequenceEqual(new[] { selected });
        bool announced = observer.Since(marker).Any(message => message.Kind == "selection" && message.Name == selected);
        bool valueEvent = observer.Since(marker).Any(message => message.Kind == "property" && message.AutomationId == identifier && message.Detail == "ValuePatternIdentifiers.ValueProperty=" + selected);
        Console.WriteLine($"UIA ARROW: {identifier}, stable={stable}, selectionEvent={announced}, valueEvent={valueEvent}, valuePattern={changed.Value is not null}, expected='{selected}', selected='{string.Join("|", changed.Selection)}'");
        if (!stable || (!announced && !valueEvent)) arrowAnnouncementFailures.Add(identifier);
        Check(window.IsVisible, "Changing a choice keeps its window open: " + identifier);
    }
}
