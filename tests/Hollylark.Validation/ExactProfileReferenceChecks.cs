using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using LarkControl.Core;
using LarkControl.Windows;

internal static partial class Program
{
    private static void VerifyExactProfileReference()
    {
        VerifyExactProfileAvailability();
        VerifyExactProfileOptionalAvailability();
        VerifyExactProfileDeleteFocus();
        foreach (bool applying in new[] { false, true })
        foreach (ExactProfileFocusCase focusCase in Enum.GetValues<ExactProfileFocusCase>())
            VerifyExactDelayedProfileOperation(applying, focusCase);
    }

    private static void VerifyExactProfileAvailability()
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile[] profiles = [AudioProfile.FromStatus("First profile", status), AudioProfile.FromStatus("Second profile", status)];
        bool available = false;
        bool disconnectDuringConfirmation = false;
        int reads = 0;
        int captures = 0;
        int applications = 0;
        int confirmations = 0;
        var window = CreateExactProfileWindow("availability", profiles,
            readStatus: () => { reads++; return status; },
            refreshStatus: () => { captures++; return Task.FromResult<A1Status?>(status); },
            applyProfile: _ => { applications++; return Task.FromResult<string?>(null); },
            canUseReceiver: () => available,
            confirm: (_, _) =>
            {
                confirmations++;
                if (disconnectDuringConfirmation) available = false;
                return true;
            });
        try
        {
            ShowExactProfileWindow(window);
            var list = (ListBox)window.FindName("AudioProfilesList");
            var name = (TextBox)window.FindName("AudioProfileNameTextBox");
            var summary = (TextBlock)window.FindName("AudioProfileSummaryReadout");
            var capture = (Button)window.FindName("SaveAudioProfileButton");
            var apply = (Button)window.FindName("ApplyAudioProfileButton");
            var delete = (Button)window.FindName("DeleteAudioProfileButton");
            list.SelectedItem = profiles[1];
            name.Text = "Pending profile";
            AutomationPeer summaryPeer = GetAccessiblePeer(summary);
            string summaryText = summary.Text;
            object? originalItems = list.ItemsSource;
            FocusForKeyboardCheck(name);
            for (int transition = 0; transition < 4; transition++)
            {
                available = transition % 2 != 0;
                window.RefreshReceiverAvailability();
                Pump();
                Check(capture.IsEnabled == available && apply.IsEnabled == available && delete.IsEnabled,
                    "Receiver availability gates only hardware profile actions; local deletion remains available");
                Check(ReferenceEquals(list.ItemsSource, originalItems) && ReferenceEquals(list.SelectedItem, profiles[1])
                    && name.Text == "Pending profile" && summary.Text == summaryText
                    && ReferenceEquals(summaryPeer, GetAccessiblePeer(summary)),
                    "Availability refresh preserves profile selection, draft name, static summary and native peer identity");
                CheckKeyboardFocus(name, "Availability refresh does not steal focus from the profile name");
            }
            Check(reads == 0 && captures == 0 && applications == 0,
                "Availability checks never read or change the receiver");
            available = false;
            Check(capture.IsEnabled && apply.IsEnabled, "The stale-button case starts before availability refresh");
            ClickSettings(window, "SaveAudioProfileButton");
            ClickSettings(window, "ApplyAudioProfileButton");
            Check(reads == 0 && captures == 0 && applications == 0 && confirmations == 0,
                "Profile handlers reject stale or forced invocation while the receiver is unavailable");
            Check(!capture.IsEnabled && !apply.IsEnabled && list.Items.Count == 2 && name.Text == "Pending profile",
                "Rejected offline actions refresh their buttons without changing the draft");
            available = true;
            window.RefreshReceiverAvailability();
            disconnectDuringConfirmation = true;
            ClickSettings(window, "ApplyAudioProfileButton");
            Check(confirmations == 1 && applications == 0 && !apply.IsEnabled,
                "Apply rechecks receiver availability after confirmation before calling the delegate");
            disconnectDuringConfirmation = false;
            ClickSettings(window, "DeleteAudioProfileButton");
            Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, profiles[0]),
                "A saved profile can still be deleted from the draft while offline");
            Check(reads == 0 && captures == 0 && applications == 0,
                "Offline profile management never touches receiver delegates");
            list.SelectedIndex = -1;
            available = true;
            window.RefreshReceiverAvailability();
            Check(list.SelectedItem is null && capture.IsEnabled && !apply.IsEnabled && !delete.IsEnabled,
                "Availability refresh preserves an explicitly cleared selection without enabling profile-specific actions");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyExactProfileOptionalAvailability()
    {
        A1Status status = A1Status.Parse(ValidStatus);
        int reads = 0;
        int applications = 0;
        var window = CreateExactProfileWindow("optional-availability", [AudioProfile.FromStatus("Original profile", status)],
            readStatus: () => { reads++; return status; },
            applyProfile: _ => { applications++; return Task.FromResult<string?>(null); });
        try
        {
            ShowExactProfileWindow(window);
            Check(((Button)window.FindName("SaveAudioProfileButton")).IsEnabled
                && ((Button)window.FindName("ApplyAudioProfileButton")).IsEnabled && reads == 0,
                "An omitted availability predicate preserves existing delegate-based callers without extra reads");
            ((TextBox)window.FindName("AudioProfileNameTextBox")).Text = "Captured profile";
            ClickSettings(window, "SaveAudioProfileButton");
            Check(reads == 1 && ((ListBox)window.FindName("AudioProfilesList")).Items.Count == 2,
                "A legacy caller can capture through readStatus without a refresh or availability delegate");
            ClickSettings(window, "ApplyAudioProfileButton");
            Check(applications == 1, "A legacy caller can apply the selected profile without an availability delegate");
        }
        finally { window.Close(); Pump(); }

        var noDelegates = CreateExactProfileWindow("missing-delegates", [AudioProfile.FromStatus("Stored profile", status)],
            canUseReceiver: () => true);
        try
        {
            ShowExactProfileWindow(noDelegates);
            noDelegates.RefreshReceiverAvailability();
            Check(!((Button)noDelegates.FindName("SaveAudioProfileButton")).IsEnabled
                && !((Button)noDelegates.FindName("ApplyAudioProfileButton")).IsEnabled
                && ((Button)noDelegates.FindName("DeleteAudioProfileButton")).IsEnabled,
                "Receiver availability never enables a missing hardware delegate or blocks local deletion");
        }
        finally { noDelegates.Close(); Pump(); }
    }

    private static void VerifyExactProfileDeleteFocus()
    {
        foreach (bool focusDelete in new[] { true, false })
        {
            bool consent = false;
            AudioProfile profile = AudioProfile.FromStatus("Last profile", A1Status.Parse(ValidStatus));
            var window = CreateExactProfileWindow("delete-focus-" + focusDelete, [profile],
                canUseReceiver: () => false, confirm: (_, _) => consent);
            try
            {
                ShowExactProfileWindow(window);
                var list = (ListBox)window.FindName("AudioProfilesList");
                var delete = (Button)window.FindName("DeleteAudioProfileButton");
                var name = (TextBox)window.FindName("AudioProfileNameTextBox");
                UIElement originalFocus = focusDelete ? delete : name;
                FocusForKeyboardCheck(originalFocus);
                ClickSettings(window, "DeleteAudioProfileButton");
                Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, profile),
                    "Declined deletion preserves the last profile and its selection");
                CheckKeyboardFocus(originalFocus, "Declined deletion preserves keyboard focus");
                consent = true;
                ClickSettings(window, "DeleteAudioProfileButton");
                Check(list.Items.Count == 0 && !delete.IsEnabled && list.IsTabStop,
                    "Deleting the last profile disables Delete and leaves the empty list in keyboard navigation");
                CheckKeyboardFocus(focusDelete ? list : name,
                    focusDelete ? "Deleting the last focused profile action returns focus to the empty list"
                        : "Programmatic deletion never steals focus from another control");
            }
            finally { consent = true; window.Close(); Pump(); }
        }
    }

    private static void VerifyExactDelayedProfileOperation(bool applying, ExactProfileFocusCase focusCase)
    {
        A1Status status = A1Status.Parse(ValidStatus);
        AudioProfile profile = AudioProfile.FromStatus("Original profile", status);
        var captureCompletion = new TaskCompletionSource<A1Status?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool available = true;
        int captures = 0;
        int applications = 0;
        var window = CreateExactProfileWindow((applying ? "apply-" : "capture-") + focusCase, [profile],
            readStatus: () => status,
            refreshStatus: () => { captures++; return captureCompletion.Task; },
            applyProfile: selected =>
            {
                Check(ReferenceEquals(selected, profile), "Delayed apply receives the selected profile");
                applications++;
                return applyCompletion.Task;
            },
            canUseReceiver: () => available);
        var otherReadout = new TextBox { Text = "Separate local test window", IsReadOnly = true };
        var otherWindow = new Window { Title = "Profile focus validation", Content = otherReadout, Width = 360, Height = 240 };
        var accept = (Button)window.FindName("SaveSettingsButton");
        try
        {
            ShowExactProfileWindow(window);
            var list = (ListBox)window.FindName("AudioProfilesList");
            var name = (TextBox)window.FindName("AudioProfileNameTextBox");
            var result = (TextBlock)window.FindName("SectionResultReadout");
            string actionId = applying ? "ApplyAudioProfileButton" : "SaveAudioProfileButton";
            var action = (Button)window.FindName(actionId);
            if (!applying) name.Text = "Delayed profile";
            FocusForKeyboardCheck(action);
            ClickSettings(window, actionId);
            Check(!accept.IsEnabled && !action.IsEnabled && !list.IsEnabled
                && (applying ? applications == 1 && captures == 0 : captures == 1 && applications == 0),
                "Delayed profile operation blocks editing and reaches exactly one delegate: " + actionId);
            window.RefreshReceiverAvailability();
            Check(ReferenceEquals(list.SelectedItem, profile) && !action.IsEnabled,
                "Availability refresh cannot unlock or change selection during a pending profile operation");
            ClickSettings(window, actionId);
            Check(captures + applications == 1, "A second invocation cannot start a concurrent profile operation");

            IInputElement? expectedFocus = null;
            if (focusCase == ExactProfileFocusCase.Readout)
            {
                window.ShowResult("Pending local profile operation.", false);
                FocusForKeyboardCheck(result);
                expectedFocus = result;
            }
            else if (focusCase is ExactProfileFocusCase.Keyboard or ExactProfileFocusCase.Mouse)
            {
                if (focusCase == ExactProfileFocusCase.Keyboard)
                {
                    PresentationSource source = PresentationSource.FromVisual(window)
                        ?? throw new InvalidOperationException("The profile test has no presentation source.");
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F6)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                else
                    window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Pump();
                expectedFocus = Keyboard.FocusedElement;
            }
            else if (focusCase is ExactProfileFocusCase.Inactive or ExactProfileFocusCase.Reactivated)
            {
                MoveOffscreen(otherWindow);
                otherWindow.Show();
                otherWindow.Activate();
                FocusForKeyboardCheck(otherReadout);
                Check(otherWindow.IsActive && !window.IsActive,
                    "The pending profile operation loses activation to another local WPF window");
                if (focusCase == ExactProfileFocusCase.Reactivated)
                {
                    window.Activate();
                    Pump();
                    Keyboard.ClearFocus();
                    Pump();
                    Check(window.IsActive, "The profile window is reactivated before operation completion");
                }
                expectedFocus = Keyboard.FocusedElement;
            }
            else if (focusCase == ExactProfileFocusCase.ReceiverUnavailable)
            {
                available = false;
                window.RefreshReceiverAvailability();
                expectedFocus = Keyboard.FocusedElement;
            }

            if (focusCase == ExactProfileFocusCase.Failure)
            {
                if (applying) applyCompletion.SetException(new InvalidOperationException("Delayed fake apply failure."));
                else captureCompletion.SetException(new InvalidOperationException("Delayed fake capture failure."));
            }
            else if (applying) applyCompletion.SetResult(null);
            else captureCompletion.SetResult(status);
            PumpUntil(() => accept.IsEnabled);
            Check(list.IsEnabled && ((Button)window.FindName("DeleteAudioProfileButton")).IsEnabled,
                "Profile completion re-enables local editing after success or failure");
            if (focusCase is ExactProfileFocusCase.Uninterrupted or ExactProfileFocusCase.Failure)
                CheckKeyboardFocus(action, "Delayed profile completion restores the initiating control: " + actionId + "/" + focusCase);
            else
                Check(ReferenceEquals(Keyboard.FocusedElement, expectedFocus),
                    "Delayed profile completion does not steal focus: " + actionId + "/" + focusCase);
            if (focusCase == ExactProfileFocusCase.Inactive)
            {
                Check(otherWindow.IsActive && !window.IsActive, "Profile completion never activates an inactive settings window");
                CheckKeyboardFocus(otherReadout, "Profile completion preserves the other local window's keyboard focus");
            }
            if (focusCase == ExactProfileFocusCase.ReceiverUnavailable)
                Check(!((Button)window.FindName("SaveAudioProfileButton")).IsEnabled
                    && !((Button)window.FindName("ApplyAudioProfileButton")).IsEnabled,
                    "Profile completion does not re-enable hardware actions after availability is lost");
            if (applying || focusCase == ExactProfileFocusCase.Failure)
                Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, profile),
                    "Applying or failing to capture a profile preserves the existing profile list");
            else
                Check(list.Items.Count == 2 && list.SelectedItem is AudioProfile { Name: "Delayed profile" } && name.Text.Length == 0,
                    "Delayed capture selects its new draft profile and clears only the completed name");
            Check(captures + applications == 1, "Focus restoration never repeats the receiver operation");
        }
        finally
        {
            captureCompletion.TrySetResult(null);
            applyCompletion.TrySetResult(null);
            PumpUntil(() => accept.IsEnabled);
            otherWindow.Close();
            window.Close();
            Pump();
        }
    }

    private static SettingsWindow CreateExactProfileWindow(string scenario, AudioProfile[] profiles,
        Func<A1Status?>? readStatus = null, Func<Task<A1Status?>>? refreshStatus = null,
        Func<AudioProfile, Task<string?>>? applyProfile = null, Func<bool>? canUseReceiver = null,
        Func<string, string, bool>? confirm = null) =>
        new(new UiPreferences { FirstRunCompleted = true, AudioProfiles = profiles },
            new UiPreferencesStore(Path.Combine(ReportDirectory, "exact-profile-reference", scenario)),
            (_, _) => throw new InvalidOperationException("Profile reference checks must not save preferences."),
            apply: _ => throw new InvalidOperationException("Profile reference checks must not apply application preferences."),
            readStatus: readStatus, refreshStatus: refreshStatus, applyProfile: applyProfile,
            canUseReceiver: canUseReceiver,
            confirm: (message, title) => title != LarkControl.Windows.Localization.Text("Settings.Save.Title")
                && (confirm?.Invoke(message, title) ?? true));

    private static void ShowExactProfileWindow(SettingsWindow window)
    {
        MoveOffscreen(window);
        window.Show();
        Pump();
        SelectSettingsCategory(window, "ProfilesSettingsTab");
        window.Activate();
        Pump();
        Check(window.IsActive, "Local profile focus validation starts in the active settings window");
    }

    private enum ExactProfileFocusCase
    {
        Uninterrupted,
        Failure,
        Readout,
        Keyboard,
        Mouse,
        Inactive,
        Reactivated,
        ReceiverUnavailable
    }
}
