using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace LarkControl.Windows;

internal sealed partial class SettingsWindow
{
    private readonly ListBox hotkeyActions = new() { MinHeight = 150, MaxHeight = 280 };
    private readonly TextBox hotkeyGesture = new() { MinHeight = 30, MaxLength = 80 };
    private Button? captureHotkey;
    private HotkeyAction? capturingAction;
    private bool populatingHotkey;
    private bool hotkeyCaptureAvailable = true;

    internal void SetHotkeyCaptureAvailable(bool available)
    {
        hotkeyCaptureAvailable = available;
        if (captureHotkey is not null) captureHotkey.IsEnabled = available;
        if (!available)
        {
            CancelHotkeyCapture();
            ShowResult(Localization.Text("Hotkeys.Capture.Unavailable"));
        }
    }

    private void BuildHotkeys()
    {
        StackPanel panel = Category("Hotkeys", Localization.Text("Hotkeys.Tab.Label"), Localization.Text("Hotkeys.Tab.Name"));
        Toggle(panel, "EnableGlobalHotkeysCheckBox", Localization.Text("Hotkeys.Enabled.Label"),
            preferences => preferences.GlobalHotkeysEnabled,
            (preferences, value) => preferences with { GlobalHotkeysEnabled = value });
        Named(hotkeyActions, "HotkeyActionsList", Localization.Text("Hotkeys.Actions.Name"));
        hotkeyActions.Style = (Style)FindResource("DeviceListStyle");
        ScrollViewer.SetHorizontalScrollBarVisibility(hotkeyActions, ScrollBarVisibility.Disabled);
        hotkeyActions.GotKeyboardFocus += (_, eventArgs) =>
        {
            if (ReferenceEquals(eventArgs.NewFocus, hotkeyActions) && hotkeyActions.SelectedItem is ListBoxItem item)
                item.Focus();
        };
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
            hotkeyActions.Items.Add(new ListBoxItem
            {
                Tag = action, Content = HotkeyActionName(action), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContentTemplate = (DataTemplate)FindResource("HotkeyActionTextTemplate")
            });
        hotkeyActions.SelectionChanged += (_, _) =>
        {
            CancelHotkeyCapture();
            PopulateHotkeyEditor();
        };
        panel.Children.Add(Label(Localization.Text("Hotkeys.Actions.Label"), hotkeyActions));
        panel.Children.Add(hotkeyActions);
        Named(hotkeyGesture, "HotkeyGestureTextBox", Localization.Text("Hotkeys.Gesture.Name"));
        UsageHints.SetText(hotkeyGesture, Localization.Text("Hotkeys.Gesture.Hint"));
        panel.Children.Add(Label(Localization.Text("Hotkeys.Gesture.Label"), hotkeyGesture));
        panel.Children.Add(hotkeyGesture);
        hotkeyGesture.TextChanged += (_, _) =>
        {
            if (populating || populatingHotkey || SelectedHotkeyAction is not { } action) return;
            ChangePreferences(preferences => preferences with
            {
                Hotkeys = preferences.Hotkeys.Select(binding => binding.Action == action
                    ? new HotkeyBinding(action, hotkeyGesture.Text.Trim()) : binding).ToArray()
            });
        };
        captureHotkey = SettingsButton("CaptureHotkeyButton", Localization.Text("Hotkeys.Capture.Label"), Localization.Text("Hotkeys.Capture.Name"));
        captureHotkey.Click += (_, _) =>
        {
            if (!hotkeyCaptureAvailable || SelectedHotkeyAction is not { } action) return;
            capturingAction = action;
            ShowResult(Localization.Text("Hotkeys.Capture.Waiting", HotkeyActionName(action)), false);
            hotkeyGesture.Focus();
        };
        panel.Children.Add(captureHotkey);
        PreviewKeyDown += CaptureHotkeyKey;
        Deactivated += (_, _) => CancelHotkeyCapture();
        tabs.SelectionChanged += (_, _) =>
        {
            if (!ReferenceEquals(tabs.SelectedItem, FindName("HotkeysSettingsTab"))) CancelHotkeyCapture();
        };
        AddReset(panel, "Hotkeys", Localization.Text("Hotkeys.ResetScope"));
        populateControls.Add(_ =>
        {
            CancelHotkeyCapture();
            PopulateHotkeyEditor();
        });
        hotkeyActions.SelectedIndex = 0;
    }

    private HotkeyAction? SelectedHotkeyAction => hotkeyActions.SelectedItem is ListBoxItem { Tag: HotkeyAction action } ? action : null;

    internal static string HotkeyActionName(HotkeyAction action) => Localization.Text("Hotkeys.Action." + action);

    private void PopulateHotkeyEditor()
    {
        if (SelectedHotkeyAction is not { } action) return;
        populatingHotkey = true;
        try
        {
            hotkeyGesture.Text = draft.Hotkeys.Single(binding => binding.Action == action).Gesture;
            string name = Localization.Text("Hotkeys.Gesture.ForAction", HotkeyActionName(action));
            AutomationProperties.SetName(hotkeyGesture, name);
            if (AutomationProperties.GetLabeledBy(hotkeyGesture) is Label label) AutomationProperties.SetName(label, name);
            if (captureHotkey is not null)
                AutomationProperties.SetName(captureHotkey, Localization.Text("Hotkeys.Capture.ForAction", HotkeyActionName(action)));
        }
        finally { populatingHotkey = false; }
    }

    private void CancelHotkeyCapture()
    {
        if (capturingAction is null) return;
        capturingAction = null;
        ClearResult();
    }

    private void CaptureHotkeyKey(object sender, KeyEventArgs eventArgs)
    {
        if (capturingAction is not { } action) return;
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        if (key == Key.Tab)
        {
            CancelHotkeyCapture();
            return;
        }
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            CancelHotkeyCapture();
            return;
        }
        eventArgs.Handled = true;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        TryCaptureHotkey(Keyboard.Modifiers, key);
    }

    internal bool TryCaptureHotkey(ModifierKeys modifiers, Key key)
    {
        if (capturingAction is not { } action) return false;
        if (!HotkeyBinding.TryFormatGesture(modifiers, key, out string canonical, out _))
        {
            ShowResult(Localization.Text("Hotkeys.Invalid", HotkeyActionName(action)));
            return false;
        }
        HotkeyBinding[] candidate = draft.Hotkeys.Select(binding => binding.Action == action
            ? new HotkeyBinding(action, canonical) : binding).ToArray();
        try { HotkeyBinding.ValidateBindings(candidate); }
        catch (ArgumentException)
        {
            ShowResult(Localization.Text("Hotkeys.Invalid", HotkeyActionName(action)));
            return false;
        }
        capturingAction = null;
        ChangePreferences(preferences => preferences with { Hotkeys = candidate });
        PopulateHotkeyEditor();
        ShowResult(Localization.Text("Hotkeys.Captured", hotkeyGesture.Text), false);
        return true;
    }

    private bool ValidateHotkeyDraft()
    {
        CancelHotkeyCapture();
        if (HotkeyBinding.TryNormalize(draft.Hotkeys, out HotkeyBinding[] normalized, out HotkeyError? error))
        {
            draft = draft with { Hotkeys = normalized };
            return true;
        }
        tabs.SelectedItem = FindName("HotkeysSettingsTab");
        if (error?.Action is { } invalidAction)
            hotkeyActions.SelectedItem = hotkeyActions.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, invalidAction));
        ShowResult(error?.Action is { } namedAction ? Localization.Text("Hotkeys.Invalid", HotkeyActionName(namedAction))
            : Localization.Text("Hotkeys.InvalidList"));
        hotkeyGesture.Focus();
        return false;
    }
}
