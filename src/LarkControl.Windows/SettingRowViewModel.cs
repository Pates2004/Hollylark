namespace LarkControl.Windows;

public sealed partial class SettingRowViewModel
{
    public string CurrentLabel => Localization.Text("Setting.Label", CurrentName);
    public string ChoiceLabel => Localization.Text("Setting.Label", ChoiceName);
}
