using System.Media;
using LarkControl.Core;

namespace LarkControl.Windows;

public partial class MainWindow
{
    private readonly BatteryAlertMonitor batteryMonitor = new();
    private readonly Action<BatteryAlertSeverity> playBatterySound;
    private bool batteryNotificationTray;
    private long batteryAlertRevision;

    private void ObserveBatteryLevels(A1Status status)
    {
        if (closed || sessionReceiver is null) return;
        IReadOnlyList<BatteryAlert> alerts = batteryMonitor.Observe(sessionReceiver.Path, status, preferences.BatteryAlerts.Policy);
        var deliveries = alerts.GroupBy(alert => alert.Severity).OrderByDescending(group => group.Key)
            .Select(group => new BatteryDelivery(group.Key,
                group.Key == BatteryAlertSeverity.Low ? preferences.BatteryAlerts.Low : preferences.BatteryAlerts.Critical,
                Localization.Text("Battery." + group.Key + ".Message", string.Join(". ", group.Select(alert =>
                    Localization.Text("Battery.Microphone", alert.TransmitterIndex + 1, alert.BatteryPercent)))))).ToArray();
        if (deliveries.Length == 0) return;
        batteryAlertRevision++;
        DeliverBatteryAlerts(deliveries);
    }

    private void DeliverBatteryAlerts(IReadOnlyList<BatteryDelivery> deliveries)
    {
        BatteryDelivery[] notifications = deliveries.Where(delivery => delivery.Output.Notification).ToArray();
        SetResult(string.Join(". ", deliveries.Select(delivery => delivery.Message)), true, announce: false, batteryWarning: true);
        if (notifications.Length > 0)
        {
            string message = string.Join(". ", notifications.Select(delivery => delivery.Message));
            try
            {
                EnsureTray();
                batteryNotificationTray = !preferences.CloseToTray;
                if (!desktopIntegration.ShowNotification(Localization.Text("Battery." + notifications[0].Severity + ".Title"), message,
                    notifications[0].Severity == BatteryAlertSeverity.Critical))
                    Log(Localization.Text("Battery.NotificationUnavailable"));
            }
            catch (Exception)
            {
                Log(Localization.Text("Battery.NotificationUnavailable"));
            }
        }
        if (closed) return;
        string speech = string.Join(". ", deliveries.Where(delivery => delivery.Output.Speech).Select(delivery => delivery.Message));
        if (speech.Length > 0)
        {
            try { AnnounceBackgroundNotification(speech, "Hollylark.Battery"); }
            catch (Exception exception) { Log(Localization.Text("Battery.SpeechUnavailable", exception.Message)); }
        }
        if (closed) return;
        if (deliveries.FirstOrDefault(delivery => delivery.Output.Sound) is { } sound)
        {
            try { playBatterySound(sound.Severity); }
            catch (Exception)
            {
                Log(Localization.Text("Battery.SoundUnavailable"));
            }
        }
    }

    private sealed record BatteryDelivery(BatteryAlertSeverity Severity, BatteryAlertOutput Output, string Message);

    private static void PlayBatterySystemSound(BatteryAlertSeverity severity)
    {
        if (severity == BatteryAlertSeverity.Critical) SystemSounds.Hand.Play();
        else SystemSounds.Exclamation.Play();
    }
}
