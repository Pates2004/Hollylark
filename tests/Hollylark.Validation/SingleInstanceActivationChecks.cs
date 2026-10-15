using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifySingleInstanceActivationApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Light };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { VerifySingleInstanceActivationChecks(); }
        finally
        {
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void VerifySingleInstanceActivationChecks()
    {
        Application application = Application.Current ?? throw new InvalidOperationException("Activation checks require the existing STA WPF application.");
        Window? previousMain = application.MainWindow;
        string fixture = Path.Combine("single-instance-ui", Guid.NewGuid().ToString("N"));
        try
        {
            VerifySingleInstanceMainActivation(application, fixture);
            VerifySingleInstanceWizardActivation(application, fixture);
        }
        finally { application.MainWindow = previousMain; }
    }

    private static void VerifySingleInstanceMainActivation(Application application, string fixture)
    {
        using var transport = new FakeTransport();
        var desktop = new MemoryDesktop();
        MainWindow main = CreatePresentationWindow(transport, Path.Combine(fixture, "main"), desktop,
            confirm: (_, _) => true, announce: _ => { });
        application.MainWindow = main;
        try
        {
            SavePreferences(main, main.CurrentPreferences with { CloseToTray = true, ConfirmExit = false, AnnounceStateChanges = false });
            main.WindowState = WindowState.Minimized;
            Pump();
            Check(main.WindowState == WindowState.Minimized, "The activation fixture starts with a minimized main window");
            SingleInstanceActivation.Queue(application);
            Check(main.WindowState == WindowState.Minimized, "An activation request is queued rather than restoring synchronously");
            PumpUntil(() => main.WindowState == WindowState.Normal && main.IsActive);
            Check(main.IsVisible && main.ShowInTaskbar, "Dispatcher activation restores the minimized main window and taskbar entry");

            TextBlock focus = FindById<TextBlock>(main, "ConnectionReadout");
            Keyboard.Focus(focus);
            Pump();
            Check(ReferenceEquals(Keyboard.FocusedElement, focus), "The tray fixture captures an actual local keyboard focus target");
            main.Close();
            PumpUntil(() => !main.IsVisible);
            Check(desktop.TrayVisible && desktop.TrayShows > 0 && !main.ShowInTaskbar,
                "Ordinary close hides the main window using only MemoryDesktop");
            SingleInstanceActivation.Queue(application);
            PumpUntil(() => main.IsVisible && main.IsActive && main.IsKeyboardFocusWithin);
            Check(main.ShowInTaskbar && ReferenceEquals(Keyboard.FocusedElement, focus),
                "Dispatcher activation restores a tray-hidden main window and its previous keyboard focus");

            main.OpenSettingsWindow();
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            MoveOffscreen(settings);
            Pump();
            try
            {
                main.Activate();
                Pump();
                SingleInstanceActivation.Queue(application);
                PumpUntil(() => settings.IsActive);
                Check(settings.IsVisible && ReferenceEquals(settings.Owner, main),
                    "A visible Settings auxiliary is activated without closing it or replacing its owner");
            }
            finally { settings.Close(); Pump(); }
            Check(desktop.StartupWrites == 0 && transport.Commands.Count == 0,
                "Activation performs no startup registration or simulated device I/O");
        }
        finally { main.RequestExit(); Pump(); }
        Check(desktop.Disposed, "Activation fixture shutdown disposes its MemoryDesktop");
    }

    private static void VerifySingleInstanceWizardActivation(Application application, string fixture)
    {
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, fixture, "wizard"));
        UiPreferences preferences = store.Load(out string? error);
        Check(error is null && store.ShouldOfferFirstRun && !File.Exists(store.FilePath),
            "The isolated first-run activation fixture has no saved settings");
        var wizard = new FirstRunWindow(preferences, store);
        bool closed = false;
        wizard.Closed += (_, _) => closed = true;
        MoveOffscreen(wizard);
        application.MainWindow = wizard;
        try
        {
            wizard.Show();
            wizard.WindowState = WindowState.Minimized;
            Pump();
            SingleInstanceActivation.Queue(application);
            PumpUntil(() => wizard.WindowState == WindowState.Normal && wizard.IsActive);
            Check(wizard.IsVisible && !closed && wizard.SavedPreferences is null && !File.Exists(store.FilePath),
                "Dispatcher activation restores first-run setup without closing it, accepting choices or saving settings");
        }
        finally { wizard.Close(); Pump(); }
    }
}
