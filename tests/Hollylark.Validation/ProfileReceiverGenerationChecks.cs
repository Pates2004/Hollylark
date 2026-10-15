using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyProfileReceiverGenerationChecks()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (bool applying in new[] { false, true })
                {
                    foreach (ProfileReceiverReadCompletion completion in Enum.GetValues<ProfileReceiverReadCompletion>())
                        VerifyDelayedProfileReceiverGeneration(language, applying, true, completion);
                    VerifyDelayedProfileReceiverGeneration(language, applying, false, ProfileReceiverReadCompletion.Status);
                    VerifyDelayedProfileReceiverGeneration(language, applying, true, ProfileReceiverReadCompletion.Status,
                        trackGeneration: false);
                }
                foreach (bool approved in new[] { false, true })
                    VerifyProfileReceiverChangeDuringConfirmation(language, approved);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyDelayedProfileReceiverGeneration(string language, bool applying, bool changeGeneration,
        ProfileReceiverReadCompletion completion, bool trackGeneration = true)
    {
        string scenario = $"{language}/{(applying ? "apply-refresh" : "capture-refresh")}/{changeGeneration}/{completion}/{trackGeneration}";
        A1Status cachedStatus = A1Status.Parse(ValidStatus);
        A1Status freshStatus = cachedStatus with { Gain = (cachedStatus.Gain + 1) % 6 };
        AudioProfile[] profiles = [AudioProfile.FromStatus("First receiver profile", cachedStatus),
            AudioProfile.FromStatus("Selected receiver profile", cachedStatus)];
        bool expectGenerationCancellation = changeGeneration && trackGeneration;
        var preferences = new UiPreferences
        {
            Language = language, FirstRunCompleted = true, ShowHints = false,
            ShowOperationMessages = !expectGenerationCancellation, AnnounceStateChanges = false,
            HiddenRows = ["TX4"], AudioProfiles = profiles
        };
        string callerBefore = JsonSerializer.Serialize(preferences);
        var refreshCompletion = new TaskCompletionSource<A1Status?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long generation = 40;
        int refreshes = 0;
        int cachedReads = 0;
        int applications = 0;
        int confirmations = 0;
        AudioProfile? appliedProfile = null;
        Func<long>? receiverGeneration = trackGeneration ? () => generation : null;
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "profile-receiver-generation", scenario));
        var window = new SettingsWindow(preferences, store,
            (_, _) => throw new InvalidOperationException("Receiver-generation checks must not save preferences."),
            apply: _ => throw new InvalidOperationException("Receiver-generation checks must not commit preferences."),
            readStatus: () => { cachedReads++; return cachedStatus; },
            refreshStatus: ReadFreshStatusAsync,
            applyProfile: async selected =>
            {
                applications++;
                appliedProfile = selected;
                A1Status? refreshed = await ReadFreshStatusAsync();
                return refreshed is null ? Localization.Text("Settings.Profiles.NoStatus") : null;
            },
            confirm: (_, title) =>
            {
                if (title == Localization.Text("Settings.Profiles.Apply.Title")) confirmations++;
                return title != Localization.Text("Settings.Save.Title");
            },
            canUseReceiver: () => true,
            receiverGeneration: receiverGeneration);
        var save = (Button)window.FindName("SaveSettingsButton");
        try
        {
            ProfileReceiverDraftSnapshot before = PrepareProfileReceiverGenerationWindow(window);
            string actionId = applying ? "ApplyAudioProfileButton" : "SaveAudioProfileButton";
            ClickSettings(window, actionId);
            Check(!save.IsEnabled && !((ListBox)window.FindName("AudioProfilesList")).IsEnabled
                && !((Button)window.FindName(actionId)).IsEnabled && refreshes == 1 && cachedReads == 0,
                "The profile operation waits for a fresh reading without using cached status: " + scenario);
            Check(applications == (applying ? 1 : 0) && confirmations == (applying ? 1 : 0)
                && (!applying || ReferenceEquals(appliedProfile, before.SelectedProfile)),
                "Only the requested profile operation and its explicit confirmation begin: " + scenario);

            if (changeGeneration) generation++;
            window.RefreshReceiverAvailability();
            VerifyProfileReceiverDraftPreserved(window, before, scenario + "/pending");
            Check(!save.IsEnabled && refreshes == 1,
                "Receiver availability refresh cannot complete or repeat the pending profile read: " + scenario);

            switch (completion)
            {
                case ProfileReceiverReadCompletion.Status:
                    refreshCompletion.SetResult(freshStatus);
                    break;
                case ProfileReceiverReadCompletion.MissingStatus:
                    refreshCompletion.SetResult(null);
                    break;
                case ProfileReceiverReadCompletion.Failure:
                    refreshCompletion.SetException(new IOException("The simulated receiver changed during its fresh read."));
                    break;
                case ProfileReceiverReadCompletion.Cancelled:
                    refreshCompletion.SetCanceled();
                    break;
            }
            PumpUntil(() => save.IsEnabled);
            Check(((ListBox)window.FindName("AudioProfilesList")).IsEnabled
                && ((Button)window.FindName("SaveAudioProfileButton")).IsEnabled
                && ((Button)window.FindName("ApplyAudioProfileButton")).IsEnabled,
                "Completion restores profile editing and availability for the current receiver: " + scenario);
            Check(refreshes == 1 && cachedReads == 0 && applications == (applying ? 1 : 0),
                "Completion never restarts capture or application against the newly available receiver: " + scenario);

            if (expectGenerationCancellation)
            {
                VerifyProfileReceiverDraftPreserved(window, before, scenario);
                VerifyProfileReceiverSelectionChangedResult(window, language, scenario);
            }
            else if (applying)
            {
                VerifyProfileReceiverDraftPreserved(window, before, scenario);
                Check(((TextBlock)window.FindName("SectionResultReadout")).Text == Localization.Text("Settings.Profiles.Applied"),
                    "Same-generation or legacy-delegate application still reports success: " + scenario);
            }
            else
            {
                UiPreferences actual = ReadProfileReceiverDraft(window);
                AudioProfile expected = AudioProfile.FromStatus(before.Name, freshStatus);
                UiPreferences expectedDraft = before.Preferences with { AudioProfiles = [.. before.Items, expected] };
                var list = (ListBox)window.FindName("AudioProfilesList");
                Check(JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expectedDraft)
                    && list.Items.Count == before.Items.Length + 1 && Equals(list.SelectedItem, expected)
                    && ((TextBox)window.FindName("AudioProfileNameTextBox")).Text.Length == 0,
                    "Same-generation or legacy-delegate capture adds only the fresh profile and clears its name: " + scenario);
                Check(expected.Gain != cachedStatus.Gain
                    && ((TextBlock)window.FindName("SectionResultReadout")).Text == Localization.Text("Settings.Profiles.Added"),
                    "Successful capture uses the awaited status and reports the localized result: " + scenario);
            }
            Check(JsonSerializer.Serialize(preferences) == callerBefore && !File.Exists(store.FilePath),
                "Receiver-generation checks leave caller preferences and persisted settings unchanged: " + scenario);
        }
        finally
        {
            refreshCompletion.TrySetResult(null);
            try { PumpUntil(() => save.IsEnabled); }
            finally
            {
                window.Close();
                Pump();
            }
        }

        Task<A1Status?> ReadFreshStatusAsync()
        {
            refreshes++;
            return refreshCompletion.Task;
        }
    }

    private static void VerifyProfileReceiverChangeDuringConfirmation(string language, bool approved)
    {
        string scenario = $"{language}/confirmation/{approved}";
        A1Status status = A1Status.Parse(ValidStatus);
        var preferences = new UiPreferences
        {
            Language = language, FirstRunCompleted = true, ShowHints = false, ShowOperationMessages = false,
            AnnounceStateChanges = false, AudioProfiles = [AudioProfile.FromStatus("First receiver profile", status),
                AudioProfile.FromStatus("Selected receiver profile", status)]
        };
        string callerBefore = JsonSerializer.Serialize(preferences);
        long generation = 80;
        int confirmations = 0;
        int applications = 0;
        int reads = 0;
        string? confirmationMessage = null;
        var store = new UiPreferencesStore(Path.Combine(ReportDirectory, "profile-receiver-generation", scenario));
        var window = new SettingsWindow(preferences, store,
            (_, _) => throw new InvalidOperationException("Receiver confirmation checks must not save preferences."),
            apply: _ => throw new InvalidOperationException("Receiver confirmation checks must not commit preferences."),
            readStatus: () => { reads++; return status; },
            refreshStatus: () => { reads++; return Task.FromResult<A1Status?>(status); },
            applyProfile: _ => { applications++; return Task.FromResult<string?>(null); },
            confirm: (message, title) =>
            {
                if (title != Localization.Text("Settings.Profiles.Apply.Title"))
                    return title != Localization.Text("Settings.Save.Title");
                confirmations++;
                confirmationMessage = message;
                generation++;
                return approved;
            },
            canUseReceiver: () => true,
            receiverGeneration: () => generation);
        try
        {
            ProfileReceiverDraftSnapshot before = PrepareProfileReceiverGenerationWindow(window);
            ClickSettings(window, "ApplyAudioProfileButton");
            Check(confirmations == 1 && generation == 81
                && confirmationMessage == Localization.Text("Settings.Profiles.Apply.Confirmation", before.SelectedProfile.Name),
                "The selected profile confirmation crosses a receiver-generation boundary: " + scenario);
            Check(applications == 0 && reads == 0 && ((Button)window.FindName("SaveSettingsButton")).IsEnabled,
                "Neither accepted nor declined stale confirmation starts a profile operation on the new receiver: " + scenario);
            VerifyProfileReceiverDraftPreserved(window, before, scenario);
            VerifyProfileReceiverSelectionChangedResult(window, language, scenario);
            Check(JsonSerializer.Serialize(preferences) == callerBefore && !File.Exists(store.FilePath),
                "A receiver switch during confirmation does not commit or mutate caller preferences: " + scenario);
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static ProfileReceiverDraftSnapshot PrepareProfileReceiverGenerationWindow(SettingsWindow window)
    {
        MoveOffscreen(window);
        window.Show();
        Pump();
        SelectSettingsCategory(window, "AppearanceSettingsTab");
        SetChoice(window, "SettingsThemeChoice", "Dark");
        SetChoice(window, "SettingsTextScaleChoice", 150);
        SelectSettingsCategory(window, "ProfilesSettingsTab");
        var list = (ListBox)window.FindName("AudioProfilesList");
        list.SelectedIndex = 1;
        var name = (TextBox)window.FindName("AudioProfileNameTextBox");
        name.Text = "Pending receiver profile";
        Pump();
        UiPreferences preferences = ReadProfileReceiverDraft(window);
        Check(preferences.Theme == "Dark" && preferences.TextScale == 150 && list.Items.Count == 2
            && list.SelectedIndex == 1, "Receiver-generation fixture stages unsaved settings and selects the second profile");
        return new(preferences, JsonSerializer.Serialize(preferences), name.Text, list.Items.Cast<AudioProfile>().ToArray(),
            (AudioProfile)list.SelectedItem, list.ItemsSource, ((TextBlock)window.FindName("AudioProfileSummaryReadout")).Text);
    }

    private static void VerifyProfileReceiverDraftPreserved(SettingsWindow window, ProfileReceiverDraftSnapshot before,
        string scenario)
    {
        var list = (ListBox)window.FindName("AudioProfilesList");
        Check(JsonSerializer.Serialize(ReadProfileReceiverDraft(window)) == before.SerializedPreferences
            && ((TextBox)window.FindName("AudioProfileNameTextBox")).Text == before.Name,
            "Receiver switching preserves the complete unsaved draft and uncaptured profile name: " + scenario);
        Check(ReferenceEquals(list.ItemsSource, before.ItemsSource) && list.Items.Cast<AudioProfile>().SequenceEqual(before.Items)
            && ReferenceEquals(list.SelectedItem, before.SelectedProfile)
            && ((TextBlock)window.FindName("AudioProfileSummaryReadout")).Text == before.Summary,
            "Receiver switching preserves the profile collection, selection and summary: " + scenario);
    }

    private static void VerifyProfileReceiverSelectionChangedResult(SettingsWindow window, string language, string scenario)
    {
        var result = (TextBlock)window.FindName("SectionResultReadout");
        string expected = Localization.Text("Settings.Profiles.ContextChanged");
        Check(result.Text == expected
            && expected.Contains(language == "pl" ? "Zmieniono odbiornik" : "The receiver", StringComparison.Ordinal)
            && expected.Contains(language == "pl" ? "nie są cofane" : "are not undone", StringComparison.Ordinal),
            "Receiver changes report the localized cause and warn that sent changes were not undone: " + scenario);
        Check(result.Visibility == Visibility.Visible && GetAccessiblePeer(result).GetPattern(PatternInterface.Value) is null && result.Focusable
            && AutomationProperties.GetName(result) == Localization.Text("Section.Result.WithMessage", expected)
            && !UsageHints.GetEnabled(window) && !ReadProfileReceiverDraft(window).ShowOperationMessages,
            "The receiver-change error remains named and available with optional hints and messages disabled: " + scenario);
    }

    private static UiPreferences ReadProfileReceiverDraft(SettingsWindow window) =>
        typeof(SettingsWindow).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as UiPreferences
            ?? throw new InvalidOperationException("The settings draft is unavailable to receiver-generation checks.");

    private sealed record ProfileReceiverDraftSnapshot(UiPreferences Preferences, string SerializedPreferences,
        string Name, AudioProfile[] Items, AudioProfile SelectedProfile, object? ItemsSource, string Summary);

    private enum ProfileReceiverReadCompletion
    {
        Status,
        MissingStatus,
        Failure,
        Cancelled
    }
}
