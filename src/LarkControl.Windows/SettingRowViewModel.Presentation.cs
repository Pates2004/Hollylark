using System.Windows;

namespace LarkControl.Windows;

public sealed partial class SettingRowViewModel
{
    private UiPreferences presentation = new();

    public event Action<SettingRowViewModel>? DraftChanged;
    public string DecreaseId => Id + ".Decrease";
    public string IncreaseId => Id + ".Increase";
    public string SliderId => Id + ".Slider";
    public string DraftValueId => Id + ".DraftValue";
    public string DraftFieldId => Id + ".DraftField";
    public string StaticCurrentId => Id + ".StaticCurrent";
    public string DraftValueText => Localization.Text("Setting.Gain.Draft", SelectedIndex >= 0
        ? Localization.Text("Setting.Gain.Level", SelectedIndex + 1) : Localization.Text("Setting.Gain.NoReadout"));
    public Visibility DraftValueVisibility => Id == "Gain" && presentation.GainControlMode == GainControlMode.Buttons
        && presentation.ValueDisplayMode == ValueDisplayMode.StaticText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DraftFieldVisibility => Id == "Gain" && presentation.GainControlMode == GainControlMode.Buttons
        && presentation.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => presentation.HiddenRows.Contains(Id) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CurrentFieldVisibility => presentation.ValueDisplayMode == ValueDisplayMode.ReadOnlyFields ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StaticValueVisibility => presentation.ValueDisplayMode == ValueDisplayMode.StaticText ? Visibility.Visible : Visibility.Collapsed;
    public GridLength LabelColumnWidth => new(150 * presentation.TextScale / 100.0);
    public GridLength CurrentColumnWidth => new(presentation.ValueDisplayMode == ValueDisplayMode.None ? 0 : 170 * presentation.TextScale / 100.0);
    public double EditorMinimumWidth => 370 * presentation.TextScale / 100.0;
    public Visibility EditorLabelVisibility => presentation.ValueDisplayMode == ValueDisplayMode.None ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InlineEditorLabelVisibility => presentation.ValueDisplayMode != ValueDisplayMode.None ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChoiceVisibility => Id != "Gain" || presentation.GainControlMode == GainControlMode.Choice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepButtonsVisibility => Id == "Gain" && presentation.GainControlMode == GainControlMode.Buttons ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SliderVisibility => Id == "Gain" && presentation.GainControlMode == GainControlMode.Slider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ApplyVisibility => !presentation.AutoApplySelections || IsDirty ? Visibility.Visible : Visibility.Collapsed;
    public bool CanDecrease => CanEdit && (!IsEnabled || SelectedIndex > 0);
    public bool CanIncrease => CanEdit && (!IsEnabled || SelectedIndex >= 0 && SelectedIndex < Options.Count - 1);
    public string DecreaseName => Localization.Text("Setting.Gain.StepName", Localization.Text("Setting.Gain.Decrease"), DraftValueText);
    public string IncreaseName => Localization.Text("Setting.Gain.StepName", Localization.Text("Setting.Gain.Increase"), DraftValueText);
    public string StaticValueText => Localization.Text("Setting.Value", DisplayName, ConfirmedValue);
    public string ChoiceHint => (Id == "Gain" ? Localization.Text("Setting.Gain.RangeHint") + " " : string.Empty) + (presentation.AutoApplySelections
        ? Localization.Text("Setting.Choice.AutomaticHint")
        : Localization.Text("Setting.Choice.ManualHint"));

    public double SelectedLevel
    {
        get => Math.Clamp(SelectedIndex + 1, 1, 6);
        set
        {
            if (!double.IsFinite(value) || value < 1 || value > 6) throw new ArgumentOutOfRangeException(nameof(value));
            SelectedIndex = (int)Math.Round(value) - 1;
        }
    }

    internal void UpdatePresentation(UiPreferences preferences)
    {
        presentation = preferences;
        NotifyPresentation();
    }

    private void NotifyPresentation()
    {
        foreach (string name in new[]
        {
            nameof(RowVisibility), nameof(CurrentFieldVisibility), nameof(StaticValueVisibility), nameof(CurrentColumnWidth),
            nameof(LabelColumnWidth), nameof(EditorMinimumWidth), nameof(EditorLabelVisibility), nameof(InlineEditorLabelVisibility),
            nameof(ChoiceVisibility), nameof(StepButtonsVisibility), nameof(SliderVisibility), nameof(ApplyVisibility),
            nameof(CanDecrease), nameof(CanIncrease), nameof(StaticValueText), nameof(SelectedLevel), nameof(ChoiceHint),
            nameof(DraftValueText), nameof(DraftValueVisibility), nameof(DraftFieldVisibility), nameof(DecreaseName), nameof(IncreaseName)
        }) RaisePropertyChanged(name);
    }
}
