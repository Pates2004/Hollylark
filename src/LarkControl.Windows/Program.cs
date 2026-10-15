using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LarkControl.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        SingleInstance? instance = null;
        bool initializingLanguages = false;
        try
        {
            instance = new SingleInstance();
            if (!instance.IsOwner)
            {
                if (arguments.Length != 0) return 1;
                instance.RequestActivation();
                return 0;
            }
            initializingLanguages = true;
            Localization.Initialize();
            initializingLanguages = false;
            var store = new UiPreferencesStore();
            UiPreferences preferences = store.Load(out string? error);
            if (error is null) Localization.SetLanguage(preferences.Language);
            if (arguments.Length > 0)
            {
                if (arguments[0] != "--diagnose" || arguments.Length > 2)
                    throw new ArgumentException(Localization.Text("Startup.CommandUsage"));
                string destination = arguments.Length == 2 ? Path.GetFullPath(arguments[1])
                    : Path.Combine(AppContext.BaseDirectory, "reports", "receiver-scan.json");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var report = new
                {
                    Timestamp = DateTimeOffset.Now,
                    Application = $"Hollylark {typeof(Program).Assembly.GetName().Version}",
                    SettingsWritten = false,
                    Receivers = HidTransport.FindReceivers()
                };
                File.WriteAllText(destination, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                return 0;
            }
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Light };
            instance.Listen(() => SingleInstanceActivation.Queue(application));
            if (error is not null)
            {
                MessageBox.Show(error, Localization.Text("Startup.SettingsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                application.Shutdown(1);
                return 1;
            }
            Localization.SetLanguage(preferences.Language);
            if (store.ShouldOfferFirstRun)
            {
                var setup = new FirstRunWindow(preferences, store);
                setup.ShowDialog();
                if (setup.SavedPreferences is null)
                {
                    application.Shutdown();
                    return 0;
                }
                if (setup.SaveWarning is string warning)
                    MessageBox.Show(warning, Localization.Text("Startup.SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                Localization.SetLanguage(setup.SavedPreferences.Language);
            }
            var mainWindow = new MainWindow();
            application.MainWindow = mainWindow;
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return application.Run(mainWindow);
        }
        catch (Exception exception)
        {
            string message = Localization.IsInitialized
                ? Localization.Text("Startup.Failed", exception.Message)
                : initializingLanguages
                    ? "Nie można odczytać plików językowych langs/pl.lng i langs/en.lng obok Hollylark.exe. " +
                        "Cannot load the language files langs/pl.lng and langs/en.lng next to Hollylark.exe.\n\n" + exception.Message
                    : "Nie można uruchomić Hollylark. Cannot start Hollylark.\n\n" + exception.Message;
            MessageBox.Show(message, "Hollylark", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { instance?.Dispose(); }
    }
}
