using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyEscapeBehaviorApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifyEscapeBehaviorChecks(); }
        finally
        {
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void VerifyEscapeBehaviorChecks()
    {
        if (Application.Current is null || Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown
            || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run Escape checks inside the initialized explicit-shutdown STA WPF suite.");

        string previousLanguage = Localization.CurrentLanguage;
        var failures = new List<Exception>();
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (bool existingConfiguration in new[] { false, true })
                foreach (string action in new[] { "Escape", "Cancel", "AltF4" })
                    VerifyCase(() => VerifyFirstRunEscapeCase(language, existingConfiguration, action),
                        $"Setup/{language}/{existingConfiguration}/{action}");
                if (skipNativeMenuSmoke)
                    Console.WriteLine("SKIP: Native main-menu Escape case on this desktop; run --escape-behavior-only separately: " + language);
                else
                    VerifyCase(() => VerifyMainEscapeBehavior(language), "Main/" + language);
                VerifyCase(() => VerifyAuxiliaryEscapeBehavior(language), "Auxiliary/" + language);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
        if (failures.Count > 0) throw new AggregateException("Escape behavior regressions failed.", failures);

        void VerifyCase(Action verify, string name)
        {
            try { verify(); }
            catch (Exception exception) { failures.Add(new InvalidOperationException(name, exception)); }
        }
    }

    private static void VerifyFirstRunEscapeCase(string language, bool existingConfiguration, string action)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "escape-behavior", language,
            $"setup-{action}-{existingConfiguration}-{Guid.NewGuid():N}"));
        UiPreferences original = store.Load(out string? error) with { Language = language, Theme = "Dark" };
        Check(error is null && store.ShouldOfferFirstRun, "Escape fixture requires incomplete setup");
        if (existingConfiguration)
            Check(store.TrySave(original, out error) && error is null, "Create isolated incomplete settings for Escape checks");
        byte[]? before = existingConfiguration ? File.ReadAllBytes(store.FilePath) : null;
        MainWindow[] mainWindows = Application.Current.Windows.OfType<MainWindow>().ToArray();
        var wizard = new FirstRunWindow(original, store);
        int closed = 0;
        bool callbackRan = false;
        Exception? failure = null;
        wizard.Closed += (_, _) => closed++;
        MoveOffscreen(wizard);
        DispatcherOperation callback = wizard.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            callbackRan = true;
            try
            {
                var languageChoice = (ComboBox)wizard.FindName("FirstRunLanguageChoice");
                var themeChoice = (ComboBox)wizard.FindName("FirstRunThemeChoice");
                var continueButton = (Button)wizard.FindName("SaveFirstRunButton");
                var cancel = (Button)wizard.FindName("CancelFirstRunButton");
                Check(!cancel.IsCancel, "First-run Cancel is explicit, not an Escape shortcut");
                if (action == "Escape")
                {
                    Check(!((TextBlock)wizard.FindName("SectionResultReadout")).IsVisible,
                        "Initial setup instructions are not a focusable result");
                    VerifyEscapeKeepsWindow(wizard, languageChoice);
                    Check(languageChoice.SelectedIndex == -1 && themeChoice.SelectedIndex == -1 && !continueButton.IsEnabled,
                        "Escape leaves required setup choices unselected and Continue disabled");
                }
                languageChoice.SelectedValue = language;
                themeChoice.SelectedValue = "Light";
                ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked = !original.ShowHints;
                Pump();
                Check(continueButton.IsEnabled, "Setup Escape/exit checks have an unsaved, valid draft");
                if (action == "Escape")
                {
                    foreach (string identifier in new[]
                    {
                        "FirstRunLanguageChoice", "FirstRunThemeChoice", "FirstRunScaleChoice", "FirstRunValueDisplayChoice"
                    })
                    {
                        var choice = (ComboBox)wizard.FindName(identifier);
                        VerifyEscapeClosesDropDown(wizard, choice);
                        VerifyEscapeKeepsWindow(wizard, choice);
                    }
                    VerifyEscapeKeepsWindow(wizard, continueButton);
                    VerifyEscapeKeepsWindow(wizard, cancel);
                    Check(closed == 0 && wizard.SavedPreferences is null && store.ShouldOfferFirstRun,
                        "Escape never closes or completes modal setup");
                    Check(themeChoice.SelectedValue is "Light"
                        && ((CheckBox)wizard.FindName("FirstRunHintsCheckBox")).IsChecked == !original.ShowHints,
                        "Escape preserves the unsaved first-run draft");
                    VerifyEscapeSettingsUnchanged(store, before);
                }
                Check(Application.Current.Windows.OfType<MainWindow>().SequenceEqual(mainWindows),
                    "Setup Escape/edits do not create a main window");
                if (action == "AltF4")
                {
                    SystemCommands.CloseWindow(wizard);
                    PumpUntil(() => !wizard.IsVisible);
                }
                else ClickFirstRun(wizard, "CancelFirstRunButton");
                Check(!wizard.IsVisible && closed == 1,
                    "Explicit Cancel/native Alt+F4 close command dismisses setup, including after ignored Escape: " + action);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (wizard.IsVisible) wizard.Close(); }
        }));
        bool? result;
        try { result = wizard.ShowDialog(); }
        finally
        {
            callback.Abort();
            if (wizard.IsVisible) wizard.Close();
            Pump();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Check(callbackRan && closed == 1 && result != true && wizard.SavedPreferences is null && store.ShouldOfferFirstRun,
            "Explicit setup exit returns no successful result or saved preferences: " + action);
        Check(Application.Current.Windows.OfType<MainWindow>().SequenceEqual(mainWindows),
            "Explicit setup exit does not construct a main window in the modal fixture: " + action);
        VerifyEscapeSettingsUnchanged(store, before);
    }

    private static void VerifyMainEscapeBehavior(string language)
    {
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        string directory = Path.Combine("escape-behavior", language, "main-" + Guid.NewGuid().ToString("N"));
        MainWindow window = CreatePresentationWindow(transport, directory, desktop);
        int closed = 0;
        window.Closed += (_, _) => closed++;
        try
        {
            VerifyEscapeKeepsWindow(window, FindById<TextBlock>(window, "ConnectionReadout"));
            RunTask(window.ConnectAsync());
            Check(window.IsConnected, "Main Escape test uses only the connected fake receiver");
            var gain = FindById<ComboBox>(window, "Gain.Choice");
            gain.SelectedIndex = gain.SelectedIndex == 0 ? 1 : 0;
            Pump();
            SettingRowViewModel setting = window.AudioSettings.Single(candidate => candidate.Id == "Gain");
            Check(setting.IsDirty && !window.CurrentPreferences.AutoApplySelections,
                "Main Escape test has an unapplied gain draft");
            int writes = CountMockWrites(transport);
            int selectedIndex = gain.SelectedIndex;
            VerifyEscapeClosesDropDown(window, gain);
            VerifyEscapeKeepsWindow(window, gain);
            Check(gain.SelectedIndex == selectedIndex && setting.IsDirty && CountMockWrites(transport) == writes,
                "Main Escape neither applies nor discards the existing gain draft");

            var menu = (Menu)window.FindName("MainMenu");
            var header = (MenuItem)menu.Items[0];
            var settingsItem = (MenuItem)window.FindName("SettingsMenuItem");
            Window[] ownedWindows = window.OwnedWindows.Cast<Window>().ToArray();
            FocusForKeyboardCheck(header);
            header.IsSubmenuOpen = true;
            Pump();
            Check(header.IsSubmenuOpen, "Main submenu is open before Escape");
            FocusForKeyboardCheck(settingsItem);
            bool handled = RaiseEscapeBehaviorKeyDown(settingsItem);
            Check(handled && !header.IsSubmenuOpen && window.IsVisible && closed == 0,
                "Native menu handling consumes Escape and closes the submenu, not main");
            Check(window.OwnedWindows.Cast<Window>().SequenceEqual(ownedWindows),
                "Menu Escape does not invoke the focused Settings item");
            VerifyEscapeKeepsWindow(window, header);
            VerifyEscapeKeepsWindow(window, FindById<Button>(window, "RefreshButton"));
            Check(window.IsConnected && closed == 0 && !desktop.Disposed && !desktop.TrayVisible
                && desktop.StartupWrites == 0 && CountMockWrites(transport) == writes,
                "Main Escape preserves the connection, window and fake desktop without setting writes");
            Check(!File.Exists(new UiPreferencesStore(Path.Combine(ReportDirectory, directory)).FilePath),
                "Main Escape does not persist a configuration");
        }
        finally { window.RequestExit(); Pump(); }
    }

    private static void VerifyAuxiliaryEscapeBehavior(string language)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "escape-behavior", language,
            "auxiliary-" + Guid.NewGuid().ToString("N")));
        UiPreferences preferences = store.Load(out string? error) with { Language = language };
        Check(error is null, "Auxiliary Escape fixture loads isolated preferences");
        int confirmations = 0;
        int saves = 0;
        var window = new SettingsWindow(preferences, store, (_, _) => saves++,
            confirm: (_, _) => { confirmations++; return false; });
        MoveOffscreen(window);
        try
        {
            window.Show();
            Pump();
            var theme = (ComboBox)window.FindName("SettingsThemeChoice");
            theme.SelectedValue = "Dark";
            Pump();
            VerifyEscapeClosesDropDown(window, theme);
            Check(confirmations == 0 && saves == 0,
                "Dropdown Escape never requests auxiliary discard or saves the draft");
            FocusForKeyboardCheck(theme);
            RaiseEscapeBehaviorKeyDown(theme);
            Check(!window.IsVisible && confirmations == 1 && saves == 0 && !File.Exists(store.FilePath),
                "No on subsequent ordinary auxiliary Escape discards settings and closes without saving");
        }
        finally { if (window.IsVisible) window.Close(); Pump(); }
    }

    private static void VerifyEscapeKeepsWindow(Window window, UIElement target)
    {
        FocusForKeyboardCheck(target);
        RaiseEscapeBehaviorKeyDown(target);
        Check(window.IsVisible, "Ordinary Escape keeps first-run/main open: " + DescribeFocus(target));
    }

    private static void VerifyEscapeClosesDropDown(Window window, ComboBox choice)
    {
        FocusForKeyboardCheck(choice);
        int selectedIndex = choice.SelectedIndex;
        choice.IsDropDownOpen = true;
        Pump();
        Check(choice.IsDropDownOpen, "Dropdown is open before Escape: " + choice.Name);
        UIElement target = Keyboard.FocusedElement as UIElement
            ?? throw new InvalidOperationException("The open dropdown has no focused UIElement.");
        Check(ReferenceEquals(target, choice)
            || target is ComboBoxItem item && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), choice),
            "Escape targets the dropdown or its actual focused item");
        bool handled = RaiseEscapeBehaviorKeyDown(target);
        Check(handled && !choice.IsDropDownOpen && window.IsVisible && choice.SelectedIndex == selectedIndex,
            "Native dropdown Escape closes only the popup and preserves selection: " + choice.Name);
    }

    private static bool RaiseEscapeBehaviorKeyDown(UIElement target)
    {
        WaitForUnmodifiedInput();
        Check(ReferenceEquals(Keyboard.FocusedElement, target), "Escape is routed to the focused test control");
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("Escape target has no WPF presentation source.");
        var keyDown = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(keyDown);
        if (!keyDown.Handled)
        {
            keyDown.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(keyDown);
        }
        Pump();
        return keyDown.Handled;
    }

    private static void VerifyEscapeSettingsUnchanged(UiPreferencesStore store, byte[]? before)
    {
        Check(before is null
                ? !Directory.Exists(store.BaseDirectory)
                : File.Exists(store.FilePath) && before.SequenceEqual(File.ReadAllBytes(store.FilePath)),
            "Setup Escape/explicit cancellation preserves existing settings byte-for-byte or their original absence");
    }
}
