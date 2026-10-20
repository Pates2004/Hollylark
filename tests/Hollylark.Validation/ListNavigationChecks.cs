using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyListNavigationApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var reference = new ComboBox { ItemsSource = new[] { new ComboBoxItem { Content = "First" }, new ComboBoxItem { Content = "Second" }, new ComboBoxItem { Content = "Third" } } };
            AutomationProperties.SetAutomationId(reference, "ReferenceExpandedCombo");
            AutomationProperties.SetName(reference, "Reference expanded combo");
            var referenceWindow = new Window { Content = reference, Width = 400, Height = 200, ThemeMode = ThemeMode.System };
            MoveOffscreen(referenceWindow);
            try
            {
                referenceWindow.Show();
                Pump();
                using var observer = new ExternalArrowObserver(referenceWindow, "expanded-reference", includeFocus: true);
                VerifyExpandedNavigation(referenceWindow, reference, observer);
            }
            finally { referenceWindow.Close(); Pump(); }
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                var preferences = new UiPreferences
                {
                    Language = language,
                    AudioProfiles = [new("First LARK profile", 0, 0, false, 0, 1, false, 0),
                        new("Second LARK profile", 1, 1, true, 1, 1, true, 1),
                        new("Third LARK profile", 5, 1, true, 2, 2, true, 2)]
                };
                var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "list-navigation-" + language));
                var setup = new FirstRunWindow(preferences, store);
                MoveOffscreen(setup);
                try
                {
                    setup.Show();
                    Pump();
                    using var observer = new ExternalArrowObserver(setup, "expanded-setup-" + language, includeFocus: true);
                    foreach (string identifier in new[] { "FirstRunScaleChoice", "FirstRunValueDisplayChoice", "FirstRunThemeChoice", "FirstRunLanguageChoice" })
                        VerifyExpandedNavigation(setup, FindById<ComboBox>(setup, identifier), observer);
                }
                finally { setup.Close(); Pump(); }
                Localization.SetLanguage(language);
                var settings = new SettingsWindow(preferences, store, (_, _) => throw new InvalidOperationException("Navigation must not save settings."), confirm: (_, _) => false);
                MoveOffscreen(settings);
                try
                {
                    settings.Show();
                    Pump();
                    using var observer = new ExternalArrowObserver(settings, "expanded-settings-" + language, includeFocus: true);
                    foreach (TabItem tab in FindById<TabControl>(settings, "SettingsTabs").Items)
                    {
                        tab.IsSelected = true;
                        Pump();
                        foreach (ComboBox choice in Descendants(settings).OfType<ComboBox>().Where(control => control.IsVisible && control.IsEnabled).ToArray())
                            VerifyExpandedNavigation(settings, choice, observer);
                        foreach (ListBox list in Descendants(settings).OfType<ListBox>().Where(control => control.IsVisible && control.IsEnabled && control.HasItems).ToArray())
                            VerifyListItemNavigation(list, observer);
                    }
                }
                finally { settings.Close(); Pump(); }
                VerifyMainListNavigation(language);
            }
        }
        finally { application.Shutdown(); Localization.SetLanguage("pl"); }
        Console.WriteLine("Validation reports: " + ReportDirectory);
        Console.WriteLine("Scope: in-process routed keys and external provider events, not physical keyboard input or NVDA speech.");
    }

    private static void VerifyExpandedNavigation(Window window, ComboBox choice, ExternalArrowObserver observer)
    {
        using var transitionProbe = new SelectorTransitionProbe(window, choice);
        if (choice.Items.Count < 2) return;
        string identifier = AutomationProperties.GetAutomationId(choice);
        choice.SelectedIndex = 0;
        FocusForKeyboardCheck(choice);
        Console.WriteLine("BEFORE OPEN: " + observer.Snapshot("__focus"));
        choice.IsDropDownOpen = true;
        Pump();
        observer.Snapshot("__focus");
        foreach (int index in Enumerable.Range(1, choice.Items.Count - 1).Concat(Enumerable.Range(0, choice.Items.Count - 1).Reverse()))
        {
            int marker = observer.Mark();
            string expected = choice.Items[index] is ComboBoxItem item ? item.Content.ToString()! : choice.Items[index].ToString()!;
            UIElement target = Keyboard.FocusedElement as UIElement ?? throw new InvalidOperationException("No dropdown keyboard focus.");
            int previousIndex = choice.ItemContainerGenerator.IndexFromContainer(target);
            transitionProbe.Record("before-routed-arrow");
            RaiseLocalKey(target, index > previousIndex ? Key.Down : Key.Up);
            transitionProbe.Record("after-routed-arrow");
            PumpUntil(() => observer.Since(marker).Any(message => message.Kind == "focus" && message.Name == expected));
            transitionProbe.Record("after-uia-event-wait");
            var focused = (ComboBoxItem)choice.ItemContainerGenerator.ContainerFromIndex(index);
            CheckKeyboardFocus(focused, "Expanded dropdown focuses its highlighted item: " + identifier);
            Check(GetAccessiblePeer(focused).GetName() == expected, "Highlighted item has its visible caption, not the previous selection: " + identifier);
            Check(observer.Since(marker).Any(message => message.Kind == "focus" && message.Name == expected && message.Detail.EndsWith("focused=True", StringComparison.Ordinal)),
                "External UIA focus event identifies the highlighted dropdown item: " + identifier + "/" + expected);
            VerifyArrowFocusMetadata(observer, marker, expected);
            Check(choice.SelectedIndex == 0, "Exploring a dropdown does not yet commit its highlighted value: " + identifier
                + $"; actualIndex={choice.SelectedIndex}; open={choice.IsDropDownOpen}; active={window.IsActive}; modifiers={Keyboard.Modifiers}");
            Check(choice.IsDropDownOpen && window.IsVisible, "Arrow navigation preserves the open dropdown and window");
        }
        RaiseLocalKey((UIElement)Keyboard.FocusedElement, Key.Escape);
        Check(!choice.IsDropDownOpen && choice.SelectedIndex == 0, "Escape dismisses exploration without committing a different item");
    }

    private static void VerifyListItemNavigation(ListBox list, ExternalArrowObserver observer)
    {
        list.SelectedIndex = 0;
        list.ScrollIntoView(list.Items[0]);
        list.UpdateLayout();
        FocusForKeyboardCheck((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0));
        foreach (int index in Enumerable.Range(1, list.Items.Count - 1).Concat(Enumerable.Range(0, list.Items.Count - 1).Reverse()))
        {
            int marker = observer.Mark();
            int previousIndex = list.SelectedIndex;
            RaiseLocalKey((UIElement)Keyboard.FocusedElement, index > previousIndex ? Key.Down : Key.Up);
            var focused = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            string expected = list.Items[index] switch
            {
                AudioProfile profile => profile.Name,
                ReceiverInfo receiver => receiver.Name,
                TransmitterViewModel target => target.Summary,
                ListBoxItem { Content: string caption } => caption,
                _ => throw new InvalidOperationException("Unexpected navigation fixture item")
            };
            Check(list.SelectedIndex == index, "List arrow synchronizes its selection: " + expected);
            CheckKeyboardFocus(focused, "List arrow focuses its selected item: " + expected);
            Check(GetAccessiblePeer(focused).GetName() == expected, "List item name describes the current item: " + expected);
            PumpUntil(() => observer.Since(marker).Any(message => message.Kind == "focus" && message.Name == expected));
            Check(observer.Since(marker).Any(message => message.Kind == "focus" && message.Name == expected && message.Detail.EndsWith("focused=True", StringComparison.Ordinal)),
                "External UIA focus event identifies the current list item: " + expected);
            VerifyArrowFocusMetadata(observer, marker, expected);
            Check(observer.Snapshot(AutomationProperties.GetAutomationId(list)).Selection.SequenceEqual(new[] { expected }),
                "External UIA selection agrees with keyboard focus: " + expected);
        }
    }

    private static void VerifyMainListNavigation(string language)
    {
        Check(Localization.CurrentLanguage == language, "Main navigation uses the requested catalog: " + language);
        using var transport = new FakeTransport();
        ReceiverInfo[] receivers = [new("First LARK receiver", "simulated://list-first", 0, 64),
            new("Second LARK receiver", "simulated://list-second", 0, 64),
            new("Third LARK receiver", "simulated://list-third", 0, 64)];
        var window = new MainWindow(() => receivers, _ => transport, Path.Combine(ReportDirectory, "main-list-" + language),
            new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
        MoveOffscreen(window);
        try
        {
            window.Show();
            PumpUntil(() => !window.IsBusy && window.Receivers.Count == 3);
            using var observer = new ExternalArrowObserver(window, "expanded-main-" + language, includeFocus: true);
            ((TabItem)window.FindName("ReceiverTab")).IsSelected = true;
            Pump();
            VerifyListItemNavigation(FindById<ListBox>(window, "ReceiverList"), observer);
            RunTask(window.ConnectAsync());
            ((TabItem)window.FindName("ControlTab")).IsSelected = true;
            Pump();
            foreach (ComboBox choice in Descendants(window).OfType<ComboBox>().Where(control => control.IsVisible && control.IsEnabled).ToArray())
                VerifyExpandedNavigation(window, choice, observer);
            Check(CountMockWrites(transport) == 0, "Exploring receiver choices never sends setting commands");
        }
        finally { window.Close(); Pump(); }
    }
}
