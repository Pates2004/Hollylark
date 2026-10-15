using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyReferenceSettingsLabels()
    {
        VerifyReferenceOperationMessageVisibility();
        VerifyReferenceSettingsDraftFeedback();
        VerifyReferenceProfileKeyboardNavigation();
        var expectedChoices = new Dictionary<string, string[]>
        {
            ["SettingsLanguageChoice"] = ["Polski", "English"],
            ["SettingsThemeChoice"] = ["Jasny", "Ciemny"],
            ["SettingsTextScaleChoice"] = ["100%", "125%", "150%", "175%", "200%"],
            ["GainControlModeChoice"] = ["Lista wyboru", "Przyciski", "Suwak"],
            ["ValueDisplayModeChoice"] = ["Pola tylko do odczytu", "Zwykły tekst", "Bez dodatkowych wartości"]
        };
        string[] categoryNames = ["Wygląd", "Ekran główny", "Sterowanie", "Uruchamianie i zamykanie", "Profile audio"];
        foreach (string theme in new[] { "Light", "Dark" })
        foreach (int scale in new[] { 100, 200 })
        {
            AudioProfile[] profiles =
            [
                new("Mowa", 0, 0, false, 0, 1, false, 0),
                new("Profil_roboczy", 1, 1, true, 1, 1, true, 1),
                new("Nagranie " + new string('ą', 71), 5, 1, true, 2, 2, true, 2)
            ];
            var preferences = new UiPreferences { Theme = theme, TextScale = scale, AudioProfiles = profiles };
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "reference-settings", theme + "-" + scale));
            var window = new SettingsWindow(preferences, store,
                (_, _) => throw new InvalidOperationException("Label validation must not save preferences."), confirm: (_, _) => false);
            MoveOffscreen(window);
            if (scale == 200)
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
            }
            try
            {
                window.Show();
                Pump();
                var tabs = (TabControl)window.FindName("SettingsTabs");
                Check(tabs.Items.Count == categoryNames.Length, "Reference settings retain all five LARK categories");
                VerifyReferenceSettingsGeometry(window);
                for (int categoryIndex = 0; categoryIndex < tabs.Items.Count; categoryIndex++)
                {
                    tabs.SelectedIndex = categoryIndex;
                    Pump();
                    var tab = (TabItem)tabs.Items[categoryIndex];
                    Check(((Button)window.FindName("ResetAllSettingsButton")).IsVisible == (categoryIndex == 4),
                        "Reset-all is available only on the Profiles page, never in the common footer: " + categoryNames[categoryIndex]);
                    Check(tab.Header is AccessText { TextWrapping: TextWrapping.Wrap } header && header.AccessKey != '\0',
                        "Settings category renders a wrapped mnemonic rather than a literal underscore: " + categoryNames[categoryIndex]);
                    Check(GetAccessiblePeer(tab).GetName() == categoryNames[categoryIndex],
                        "Settings category UIA name excludes mnemonic markup: " + categoryNames[categoryIndex]);
                    VerifyReferenceSettingsControlNames(window);
                    foreach (ComboBox choice in Descendants(window).OfType<ComboBox>().Where(control => control.IsVisible).ToArray())
                    {
                        string identifier = AutomationProperties.GetAutomationId(choice);
                        Check(expectedChoices.ContainsKey(identifier), "Every settings dropdown has a label regression fixture: " + identifier);
                        VerifyReferenceSettingsChoice(choice, expectedChoices[identifier]);
                    }
                    var scroll = (ScrollViewer)tab.Content;
                    Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                        && scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                        "Settings content wraps inside its category at " + theme + "/" + scale + ": " + categoryNames[categoryIndex]);
                    RenderWindow(window, $"Reference-settings-{theme}-{scale}-{categoryIndex}.png");
                }
                VerifyReferenceProfilePresentation(window, profiles);
                var startupAndClosing = (TabItem)window.FindName("StartupAndClosingSettingsTab");
                var lifecyclePanel = (StackPanel)((ScrollViewer)startupAndClosing.Content).Content;
                Check(GetAccessiblePeer(startupAndClosing).GetName() == Localization.Text("Settings.StartupAndClosing.Name")
                    && lifecyclePanel.Children.OfType<CheckBox>().SequenceEqual(new[]
                    {
                        (CheckBox)window.FindName("StartWithWindowsCheckBox"),
                        (CheckBox)window.FindName("CloseToTrayCheckBox"),
                        (CheckBox)window.FindName("ConfirmExitCheckBox")
                    }) && !lifecyclePanel.Children.OfType<GroupBox>().Any(),
                    "Startup and closing share one native category without an extra single-option group");
                Check(!File.Exists(store.FilePath), "Settings label validation does not write preferences");
            }
            finally
            {
                window.Close();
                Pump();
            }
            Check(!File.Exists(store.FilePath), "Closing reference settings leaves preferences untouched");
        }
    }

    private static void VerifyReferenceSettingsGeometry(SettingsWindow window)
    {
        var layout = (Grid)window.Content;
        var result = (TextBlock)window.FindName("SectionResultReadout");
        var tabs = (TabControl)window.FindName("SettingsTabs");
        var theme = (ComboBox)window.FindName("SettingsThemeChoice");
        var save = (Button)window.FindName("SaveSettingsButton");
        var cancel = (Button)window.FindName("CancelSettingsButton");
        Check(layout.Margin == new Thickness(12) && layout.RowDefinitions.Count == 3
            && Grid.GetRow(result) == 1 && Grid.GetRow((UIElement)save.Parent) == 2,
            "The settings section keeps the reference 12-pixel inset and body/result/action order");
        Check(result.MinHeight == 45 && double.IsPositiveInfinity(result.MaxHeight) && result.VerticalAlignment == VerticalAlignment.Center
            && result.Focusable && KeyboardNavigation.GetIsTabStop(result),
            "The shared static result remains keyboard-readable without clipping long error messages");
        Check(tabs.Parent is DockPanel body && body.Children.Count == 1 && ReferenceEquals(body.Children[0], tabs),
            "The settings body contains only the category selector, not an extra reset footer");
        Check(save.IsDefault && save.Parent is StackPanel actions && actions.Orientation == Orientation.Horizontal
            && actions.HorizontalAlignment == HorizontalAlignment.Right
            && actions.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible).SequenceEqual(new[] { save, cancel }),
            "The settings action footer contains the reference right-aligned OK and Cancel buttons");
        Check(ReferenceEquals(typeof(AuxiliaryWindow).GetProperty("InitialFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), window.FindName("SettingsLanguageChoice")),
            "Settings designate their first available selector as initial focus rather than the Appearance tab");
        Check(tabs.ReadLocalValue(KeyboardNavigation.TabNavigationProperty) == DependencyProperty.UnsetValue,
            "Settings retain native TabControl navigation rather than overriding the reference behavior");
        foreach (string identifier in new[] { "SettingsLanguageChoice", "SettingsThemeChoice", "SettingsTextScaleChoice", "GainControlModeChoice", "ValueDisplayModeChoice" })
            Check(((ComboBox)window.FindName(identifier)).Margin == new Thickness(3), "Settings choice uses the reference spacing: " + identifier);
        foreach (string identifier in new[] { "AutoApplySelectionsCheckBox", "ShowHintsCheckBox", "StartWithWindowsCheckBox",
            "CloseToTrayCheckBox", "ConfirmExitCheckBox" })
            Check(((CheckBox)window.FindName(identifier)).Margin == new Thickness(3, 10, 3, 10),
                "Settings behavior/startup/closing checkbox uses the reference spacing: " + identifier);
        foreach (string identifier in new[] { "ShowOperationMessagesCheckBox", "AnnounceStateChangesCheckBox" })
            Check(((CheckBox)window.FindName(identifier)).Margin == new Thickness(3, 8, 3, 8),
                "Settings feedback checkbox uses the reference spacing: " + identifier);
        var mainScreen = (StackPanel)((ScrollViewer)((TabItem)window.FindName("MainScreenSettingsTab")).Content).Content;
        Check(mainScreen.Children[0] is TextBlock heading && heading.FontWeight == FontWeights.SemiBold
            && heading.Margin == new Thickness(0, 12, 0, 6) && heading.Text == "Widoczne wiersze sterowania",
            "The Main Screen category starts with the reference section heading rather than extra optional prose");
        Check(mainScreen.Children.OfType<CheckBox>().All(control => control.Margin == new Thickness(3, 6, 3, 6)),
            "Visible-row choices use the reference compact six-pixel vertical margins");
        var controls = (StackPanel)((ScrollViewer)((TabItem)window.FindName("ControlsSettingsTab")).Content).Content;
        Check(controls.Children.Contains((UIElement)window.FindName("ResetControlsSettingsButton"))
            && window.FindName("AutoApplySelectionsWarning") is null,
            "Controls retain their reset action without the removed automatic-apply explanation");
        Check(window.FindName("ReceiverUsageHint") is null,
            "Optional automatic USB explanations belong in Help, not a read-only Settings field");
        Check(!Descendants(window).OfType<TextBox>().Any(field => field.IsReadOnly),
            "Settings retain editable profile names but no read-only edit fields");
        Check(window.FindName("AutoConnectAtStartupCheckBox") is null && window.FindName("ReceiverMemoryHint") is null,
            "Startup removes obsolete preference controls without changing its native reference presentation");
    }

    private static void VerifyReferenceProfileKeyboardNavigation()
    {
        AudioProfile[] profiles =
        [
            new("Profil pierwszy", 0, 0, false, 0, 1, false, 0),
            new("Profil drugi", 1, 1, true, 1, 1, true, 1),
            new("Profil trzeci", 5, 1, true, 2, 2, true, 2)
        ];
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "reference-profile-keyboard"));
        var window = new SettingsWindow(new UiPreferences { AudioProfiles = profiles }, store,
            (_, _) => throw new InvalidOperationException("Profile keyboard validation must not save preferences."),
            confirm: (_, title) => title != Localization.Text("Settings.Save.Title"));
        MoveOffscreen(window);
        try
        {
            window.Show();
            Pump();
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            var list = (ListBox)window.FindName("AudioProfilesList");
            var name = (TextBox)window.FindName("AudioProfileNameTextBox");
            var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
            Check(!list.IsTabStop && KeyboardNavigation.GetTabNavigation(list) == KeyboardNavigationMode.Once
                && KeyboardNavigation.GetDirectionalNavigation(list) == KeyboardNavigationMode.Contained,
                "A populated profile list uses the reference single Tab stop and contained arrow navigation");
            list.SelectedItem = profiles[1];
            FocusForKeyboardCheck(name);
            var middle = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var focusTrace = new List<string>();
            KeyboardFocusChangedEventHandler trace = (_, arguments) => focusTrace.Add(
                DescribeFocus(arguments.NewFocus) + Environment.NewLine + Environment.StackTrace);
            window.GotKeyboardFocus += trace;
            try
            {
                Keyboard.Focus(list);
                Pump();
                if (!ReferenceEquals(Keyboard.FocusedElement, middle))
                    Console.Error.WriteLine(string.Join(Environment.NewLine, focusTrace));
                CheckKeyboardFocus(middle, "Focusing the list routes to the selected profile instead of stopping on the list container");
            }
            finally { window.GotKeyboardFocus -= trace; }
            var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            FocusForKeyboardCheck(last);
            Check(ReferenceEquals(list.SelectedItem, profiles[2]) && summary.Text.StartsWith(profiles[2].Name, StringComparison.Ordinal),
                "Keyboard focus on an item synchronizes selection and the LARK profile summary");
            VerifyReferenceProfileArrow(list, Key.Up, 1);
            VerifyReferenceProfileArrow(list, Key.Down, 2);
            MoveKeyboardFocus(last, name, FocusNavigationDirection.Next);
            MoveKeyboardFocus(name, last, FocusNavigationDirection.Previous);
            list.SelectedIndex = -1;
            FocusForKeyboardCheck(name);
            Keyboard.Focus(list);
            Pump();
            CheckKeyboardFocus((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0),
                "Entering a populated list without a selection focuses and selects its first profile");
            Check(list.SelectedIndex == 0, "The first profile fallback synchronizes selection with keyboard focus");
            while (list.HasItems) ClickSettings(window, "DeleteAudioProfileButton");
            Pump();
            Check(list.IsTabStop && !((Button)window.FindName("DeleteAudioProfileButton")).IsEnabled,
                "An empty profile list remains keyboard reachable and its unavailable delete action is disabled");
            FocusForKeyboardCheck(list);
            MoveKeyboardFocus(list, name, FocusNavigationDirection.Next);
            MoveKeyboardFocus(name, list, FocusNavigationDirection.Previous);
            Check(!File.Exists(store.FilePath) && profiles.Length == 3,
                "Profile focus and deletion checks modify only the unsaved draft");
        }
        finally
        {
            window.Close();
            Pump();
        }
        Check(!File.Exists(store.FilePath), "Closing the profile keyboard test discards the draft");
    }

    private static void VerifyReferenceProfileArrow(ListBox list, Key key, int expectedIndex)
    {
        WaitForUnmodifiedInput();
        Check(Keyboard.FocusedElement is ListBoxItem focused && ItemsControl.ItemsControlFromItemContainer(focused) == list,
            "Profile arrow trial requires focus on an item in the tested list: " + DescribeFocus(Keyboard.FocusedElement));
        RaiseLocalKey((UIElement)Keyboard.FocusedElement, key);
        Check(list.SelectedIndex == expectedIndex, "Native profile list arrow navigation updates selection: " + key);
        CheckKeyboardFocus((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(expectedIndex),
            "Native profile list arrow navigation keeps focus on the selected item: " + key);
    }

    private static void VerifyReferenceOperationMessageVisibility()
    {
        foreach (bool showOperations in new[] { false, true })
        foreach (bool announceChanges in new[] { false, true })
        {
            var preferences = new UiPreferences { ShowOperationMessages = showOperations, AnnounceStateChanges = announceChanges };
            var window = new ReferenceOperationMessagesWindow();
            window.ApplyPreferences(preferences);
            MoveOffscreen(window);
            try
            {
                window.Show();
                Pump();
                var result = (TextBlock)window.FindName("SectionResultReadout");
                Check(result.Visibility == Visibility.Collapsed && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "An empty auxiliary result is hidden and has no live announcements");
                window.ShowResult("Zakończono zwykłe działanie.", false, announce: true, optional: true);
                Pump();
                Check(result.IsVisible == showOperations
                    && AutomationProperties.GetLiveSetting(result) == (showOperations ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off),
                    "Optional auxiliary results follow operation visibility independently of automatic state announcements");
                Check(result.Text == "Zakończono zwykłe działanie."
                    && AutomationProperties.GetName(result).Contains(result.Text),
                    "Auxiliary result text and its accessible name remain synchronized");
                window.ApplyPreferences(preferences with { ShowOperationMessages = !showOperations });
                Pump();
                Check(result.IsVisible != showOperations
                    && AutomationProperties.GetLiveSetting(result) == (!showOperations ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off),
                    "Applying preferences updates an existing optional result without replacing its text");

                window.ApplyPreferences(preferences with { ShowOperationMessages = false });
                window.ShowResult("Poziomy odczytane na żądanie.", false, announce: true);
                Pump();
                Check(result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "Explicit requested output remains visible and announced when ordinary operation messages are disabled");
                window.ShowResult("Stan konfiguracji.", false, announce: false);
                Pump();
                Check(result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "Essential silent status retains the existing wizard semantics");
                window.ApplyPreferences(preferences with { ShowOperationMessages = true });
                Pump();
                Check(result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "Preference updates do not turn silent status into a live announcement");
                window.ApplyPreferences(preferences with { ShowOperationMessages = false });
                window.ShowResult("Błąd wymagający uwagi.", important: true, announce: false, optional: true);
                Pump();
                Check(result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Polite,
                    "Important errors cannot be hidden by an optional flag or disabled operation messages");

                window.ShowResult("Ukryty wynik do wyczyszczenia.", false, announce: true, optional: true);
                window.ClearMessage();
                window.ApplyPreferences(preferences with { ShowOperationMessages = true });
                Pump();
                Check(!result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "A cleared hidden result never resurfaces when operation messages are enabled");
                window.ShowResult("Widoczny wynik do wyczyszczenia.", false, announce: true, optional: true);
                window.ClearMessage();
                window.ApplyPreferences(preferences with { ShowOperationMessages = false });
                window.ApplyPreferences(preferences with { ShowOperationMessages = true });
                Pump();
                Check(!result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                    "A cleared visible result remains cleared across preference updates");
            }
            finally
            {
                window.Close();
                Pump();
            }
        }
    }

    private static void VerifyReferenceSettingsDraftFeedback()
    {
        var preferences = new UiPreferences
        {
            ShowOperationMessages = false,
            AudioProfiles = [new("Profil szkicu", 0, 0, false, 0, 1, false, 0)]
        };
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "reference-settings-draft-feedback"));
        var window = new SettingsWindow(preferences, store,
            (_, _) => throw new InvalidOperationException("Draft feedback validation must not save preferences."),
            confirm: (_, title) => title != Localization.Text("Settings.Save.Title"));
        MoveOffscreen(window);
        try
        {
            window.Show();
            Pump();
            SelectSettingsCategory(window, "ControlsSettingsTab");
            var showMessages = (CheckBox)window.FindName("ShowOperationMessagesCheckBox");
            var result = (TextBlock)window.FindName("SectionResultReadout");
            showMessages.IsChecked = true;
            ClickSettings(window, "ResetControlsSettingsButton");
            Check(result.IsVisible && result.Text.Contains("Przywrócono ustawienia w szkicu"),
                "Draft feedback uses the edited operation-message choice instead of the original preference");
            showMessages.IsChecked = false;
            Check(!result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                "Disabling draft operation messages immediately hides a prior optional success");
            SelectSettingsCategory(window, "ProfilesSettingsTab");
            ClickSettings(window, "DeleteAudioProfileButton");
            Check(((ListBox)window.FindName("AudioProfilesList")).Items.Count == 0 && !result.IsVisible,
                "A profile draft operation with feedback disabled clears its result without saving");
            SelectSettingsCategory(window, "ControlsSettingsTab");
            showMessages.IsChecked = true;
            Check(!result.IsVisible && AutomationProperties.GetLiveSetting(result) == AutomationLiveSetting.Off,
                "Re-enabling draft feedback cannot resurrect a cleared operation result");
            Check(!File.Exists(store.FilePath) && !preferences.ShowOperationMessages && preferences.AudioProfiles.Length == 1,
                "Feedback preference edits preserve the original configuration and profile collection");
        }
        finally
        {
            window.Close();
            Pump();
        }
        Check(!File.Exists(store.FilePath), "Closing draft feedback validation does not persist any setting");
    }

    private sealed class ReferenceOperationMessagesWindow : AuxiliaryWindow
    {
        internal ReferenceOperationMessagesWindow() : base("Hollylark — test komunikatów", "ReferenceOperationMessagesWindow") { }

        internal void ClearMessage() => ClearResult();
    }

    private static void VerifyReferenceSettingsControlNames(SettingsWindow window)
    {
        foreach (Control control in Descendants(window).OfType<Control>().Where(control =>
            control.IsVisible && !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control))
            && control is Button or CheckBox or ComboBox or TextBox or ListBox))
        {
            string identifier = AutomationProperties.GetAutomationId(control);
            string name = AutomationProperties.GetName(control);
            Check(!string.IsNullOrWhiteSpace(name) && !name.Contains('_') && GetAccessiblePeer(control).GetName() == name,
                "Settings UIA name survives associated-label precedence without underscores or record dumps: " + identifier);
            if (control is ComboBox or TextBox or ListBox)
            {
                var label = AutomationProperties.GetLabeledBy(control) as Label;
                Check(label is not null && ReferenceEquals(label.Target, control) && GetAccessiblePeer(label).GetName() == name,
                    "Settings field has a visible associated label preserving its full accessible name: " + identifier);
                AccessText? labelText = label is null ? null : Descendants(label).OfType<AccessText>().FirstOrDefault();
                Check(labelText is { TextWrapping: TextWrapping.Wrap } && labelText.AccessKey != '\0',
                    "Associated settings label supports wrapping and its mnemonic: " + identifier);
            }
            if (control is Button or CheckBox)
            {
                AccessText? content = Descendants(control).OfType<AccessText>().FirstOrDefault();
                Check(content is { TextWrapping: TextWrapping.Wrap } && (!content.Text.Contains('_') || content.AccessKey != '\0'),
                    "Settings action wraps its label and interprets any mnemonic: " + identifier);
            }
        }
    }

    private static void VerifyReferenceSettingsChoice(ComboBox choice, string[] expected)
    {
        string identifier = AutomationProperties.GetAutomationId(choice);
        int selectedIndex = choice.SelectedIndex;
        Check(choice.Items.Cast<object>().Select(item => item is ComboBoxItem native ? native.Content as string : item.ToString()).SequenceEqual(expected),
            "Settings choice text is the Polish caption, not the private record representation: " + identifier);
        choice.IsDropDownOpen = true;
        Pump();
        try
        {
            for (int index = 0; index < expected.Length; index++)
            {
                choice.SelectedIndex = index;
                Pump();
                var item = (ComboBoxItem?)choice.ItemContainerGenerator.ContainerFromIndex(index);
                Check(item is not null && GetAccessiblePeer(item).GetName() == expected[index],
                    "Expanded settings option exposes its exact Polish UIA name: " + identifier + "/" + index);
                Check(choice.Text == expected[index], "Selected settings value uses the same readable caption: " + identifier + "/" + index);
                Check(item is not null && Descendants(item).OfType<TextBlock>().Any(text => text.Text == expected[index]),
                    "Native settings dropdown displays the complete option text: " + identifier + "/" + index);
            }
        }
        finally
        {
            choice.SelectedIndex = selectedIndex;
            choice.IsDropDownOpen = false;
            Pump();
        }
    }

    private static void VerifyReferenceProfilePresentation(SettingsWindow window, AudioProfile[] profiles)
    {
        var list = (ListBox)window.FindName("AudioProfilesList");
        var name = (TextBox)window.FindName("AudioProfileNameTextBox");
        var save = (Button)window.FindName("SaveAudioProfileButton");
        var apply = (Button)window.FindName("ApplyAudioProfileButton");
        var delete = (Button)window.FindName("DeleteAudioProfileButton");
        var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
        var panel = (StackPanel)list.Parent;
        Check(panel.Children.IndexOf(list) < panel.Children.IndexOf(name),
            "Reference profile layout presents the existing list before the name input");
        Check(save.Parent is WrapPanel actions && ReferenceEquals(actions, apply.Parent) && ReferenceEquals(actions, delete.Parent)
            && actions.Children.Cast<UIElement>().SequenceEqual(new UIElement[] { save, apply, delete })
            && panel.Children.IndexOf(name) < panel.Children.IndexOf(actions) && panel.Children.IndexOf(actions) < panel.Children.IndexOf(summary),
            "Reference profile actions share a wrapping Save/Apply/Delete row without removing the LARK summary");
        Check(name.MinHeight == 30 && name.Padding == new Thickness(6, 5, 6, 5) && list.MaxHeight == 240,
            "Profile name field and list use the reference dimensions");
        var resetAll = (Button)window.FindName("ResetAllSettingsButton");
        Check(ReferenceEquals(panel.Children[0], AutomationProperties.GetLabeledBy(list))
            && ReferenceEquals(panel.Children[^1], resetAll) && resetAll.Margin == new Thickness(3, 12, 3, 3),
            "Profiles start directly with the list label and finish with the reference reset-all action");
        Check(GetAccessiblePeer(save).GetName() == "Zapisz aktualne ustawienia jako profil",
            "Profile capture is labelled as settings, not an audio recording");
        Check(list.DisplayMemberPath == nameof(AudioProfile.Name) && list.ItemTemplate is null && list.ItemContainerStyle is null,
            "Profiles use native name presentation without custom containers or templates");
        for (int index = 0; index < profiles.Length; index++)
        {
            list.ScrollIntoView(list.Items[index]);
            Pump();
            var item = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(index);
            Check(item is not null && GetAccessiblePeer(item).GetName() == profiles[index].Name,
                "Profile list announces the user name instead of a record: " + index);
            Check(item is not null && Descendants(item).OfType<TextBlock>().Any(text => text.Text == profiles[index].Name),
                "Native profile text retains the complete name and literal underscores: " + index);
        }
    }
}
