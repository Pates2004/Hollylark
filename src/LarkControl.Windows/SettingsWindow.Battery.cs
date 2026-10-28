using System.Windows.Controls;
using LarkControl.Core;

namespace LarkControl.Windows;

internal sealed partial class SettingsWindow
{
    private void BuildBatteryAlerts()
    {
        StackPanel panel = Category("BatteryAlerts", Localization.Text("Battery.Settings.Label"), Localization.Text("Battery.Settings.Name"));
        Toggle(panel, "BatteryAlertsEnabledCheckBox", Localization.Text("Battery.Enabled"),
            preferences => preferences.BatteryAlerts.Policy.Enabled,
            (preferences, value) => preferences with
            {
                BatteryAlerts = preferences.BatteryAlerts with { Policy = preferences.BatteryAlerts.Policy with { Enabled = value } }
            });
        AddBatteryLevel(panel, BatteryAlertSeverity.Low);
        AddBatteryLevel(panel, BatteryAlertSeverity.Critical);
        AddReset(panel, "BatteryAlerts", Localization.Text("Battery.Settings.Name"));
    }

    private void AddBatteryLevel(StackPanel panel, BatteryAlertSeverity severity)
    {
        string prefix = "Battery" + severity;
        Choice(panel, prefix + "ThresholdChoice", Localization.Text("Battery." + severity + ".Threshold.Label"),
            Localization.Text("Battery." + severity + ".Threshold"),
            Enumerable.Range(severity == BatteryAlertSeverity.Low ? 2 : 1, 99)
                .Select(value => new SettingsChoice<int>(value, value + "%")).ToArray(),
            preferences => severity == BatteryAlertSeverity.Low ? preferences.BatteryAlerts.Policy.LowThreshold
                : preferences.BatteryAlerts.Policy.CriticalThreshold,
            (preferences, value) => preferences with
            {
                BatteryAlerts = preferences.BatteryAlerts with
                {
                    Policy = severity == BatteryAlertSeverity.Low
                        ? preferences.BatteryAlerts.Policy with { LowThreshold = value }
                        : preferences.BatteryAlerts.Policy with { CriticalThreshold = value }
                }
            });
        Toggle(panel, prefix + "NotificationCheckBox", Localization.Text("Battery." + severity + ".Notification"),
            preferences => Level(preferences.BatteryAlerts, severity).Notification,
            (preferences, value) => ChangeBatteryLevel(preferences, severity, output => output with { Notification = value }));
        Toggle(panel, prefix + "SpeechCheckBox", Localization.Text("Battery." + severity + ".Speech"),
            preferences => Level(preferences.BatteryAlerts, severity).Speech,
            (preferences, value) => ChangeBatteryLevel(preferences, severity, output => output with { Speech = value }));
        Toggle(panel, prefix + "SoundCheckBox", Localization.Text("Battery." + severity + ".Sound"),
            preferences => Level(preferences.BatteryAlerts, severity).Sound,
            (preferences, value) => ChangeBatteryLevel(preferences, severity, output => output with { Sound = value }));
    }

    private static BatteryAlertOutput Level(BatteryAlertPreferences preferences, BatteryAlertSeverity severity) =>
        severity == BatteryAlertSeverity.Low ? preferences.Low : preferences.Critical;

    private static UiPreferences ChangeBatteryLevel(UiPreferences preferences, BatteryAlertSeverity severity,
        Func<BatteryAlertOutput, BatteryAlertOutput> change) => preferences with
        {
            BatteryAlerts = severity == BatteryAlertSeverity.Low
                ? preferences.BatteryAlerts with { Low = change(preferences.BatteryAlerts.Low) }
                : preferences.BatteryAlerts with { Critical = change(preferences.BatteryAlerts.Critical) }
        };

    private bool ValidateBatteryAlertDraft()
    {
        try { draft.BatteryAlerts.Validate(); return true; }
        catch (ArgumentException)
        {
            tabs.SelectedItem = FindName("BatteryAlertsSettingsTab");
            ShowResult(Localization.Text("Battery.InvalidThresholds"));
            tabs.UpdateLayout();
            if (FindName("BatteryLowThresholdChoice") is ComboBox choice) { choice.BringIntoView(); choice.Focus(); }
            return false;
        }
    }
}
