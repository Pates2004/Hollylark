using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    public static void VerifyProfileMicrophoneTargetChecks()
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && Application.Current is not null && Application.Current.Dispatcher.CheckAccess()
            && SynchronizationContext.Current is DispatcherSynchronizationContext,
            "Profile microphone-target checks reuse the existing STA Application and dispatcher context");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                foreach (bool approved in new[] { false, true })
                    VerifyProfileMicrophoneTargetConfirmation(language, changeDuringConfirmation: true, approved: approved);
                VerifyProfileMicrophoneTargetConfirmation(language, changeDuringConfirmation: false, approved: true);
            }
        }
        finally { Localization.SetLanguage(previousLanguage); }
    }

    private static void VerifyProfileMicrophoneTargetConfirmation(string language, bool changeDuringConfirmation, bool approved)
    {
        string scenario = $"{language}/{(changeDuringConfirmation ? "target-change" : "individual")}/{approved}";
        string fixtureName = "profile-microphone-target-" + scenario.Replace('/', '-') + "-" + Guid.NewGuid().ToString("N");
        using var transport = new FakeTransport();
        transport.SetTransmitterConnected(0, true);
        transport.SetTransmitterConnected(1, true);
        var desktop = new MemoryDesktop();
        int confirmations = 0;
        int applications = 0;
        int saveConfirmations = 0;
        string? confirmationMessage = null;
        MainWindow? owner = null;
        owner = CreatePresentationWindow(transport, fixtureName, desktop, (message, title) =>
        {
            if (title == Localization.Text("Settings.Save.Title"))
            {
                saveConfirmations++;
                return false;
            }
            if (title != Localization.Text("Settings.Profiles.Apply.Title")) return true;
            confirmations++;
            confirmationMessage = message;
            if (changeDuringConfirmation)
                FindById<ListBox>(owner!, "TransmitterList").SelectedItem = owner!.Transmitters[0];
            return approved;
        });
        try
        {
            A1Status status = A1Status.Parse(ValidStatus);
            AudioProfile selectedProfile = AudioProfile.FromStatus("Selected microphone profile", status)
                with { Gain = (status.Gain + 1) % 6 };
            SavePreferences(owner, owner.CurrentPreferences with
            {
                Language = language, FirstRunCompleted = true, Theme = "Light", TextScale = 100,
                HiddenRows = [], AutoApplySelections = false, ShowHints = false, ShowOperationMessages = false,
                AnnounceStateChanges = false, ConfirmExit = false, CloseToTray = false, StartWithWindows = false,
                AudioProfiles = [AudioProfile.FromStatus("First microphone profile", status), selectedProfile]
            });
            RunTask(owner.ConnectAsync());
            ReadProfileMicrophoneTargetField<DispatcherTimer>(owner, "pollTimer").Stop();
            ListBox targets = FindById<ListBox>(owner, "TransmitterList");
            TransmitterViewModel group = owner.TransmitterTargets.Single(target => target.IsGroup);
            targets.SelectedItem = group;
            Pump();
            Check(owner.IsConnected && !owner.IsBusy && owner.Transmitters.Count(target => target.IsConnected) == 2
                && ReferenceEquals(targets.SelectedItem, group) && selectedProfile.Gain != status.Gain,
                "The integrated fixture starts with two active fake microphones, All selected and a non-no-op profile: " + scenario);
            if (!changeDuringConfirmation)
            {
                targets.SelectedItem = owner.Transmitters[0];
                Pump();
            }

            var settings = (SettingsWindow)owner.OpenSettingsWindow();
            ProfileReceiverDraftSnapshot draftBefore = PrepareProfileReceiverGenerationWindow(settings);
            Check(ReferenceEquals(settings.Owner, owner) && ReferenceEquals(draftBefore.SelectedProfile, selectedProfile),
                "The real MainWindow settings path retains the selected profile and its unsaved draft: " + scenario);
            FieldInfo applyProfileField = typeof(SettingsWindow).GetField("applyProfile", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The integrated profile application binding is unavailable.");
            var applyProfile = ReadProfileMicrophoneTargetField<Func<AudioProfile, Task<string?>>>(settings, "applyProfile");
            Check(applyProfile == new Func<AudioProfile, Task<string?>>(owner.ApplyAudioProfileAsync),
                "The application observer forwards the real MainWindow profile binding: " + scenario);
            applyProfileField.SetValue(settings, new Func<AudioProfile, Task<string?>>(profile =>
            {
                applications++;
                return applyProfile(profile);
            }));
            var readContext = ReadProfileMicrophoneTargetField<Func<long>>(settings, "receiverGeneration");
            long contextBefore = ReadProfileMicrophoneTargetField<long>(owner, "controlContextGeneration");
            long receiverBefore = ReadProfileMicrophoneTargetField<long>(owner, "generation");
            Check(readContext() == contextBefore,
                "The actual settings generation delegate reads the MainWindow control context: " + scenario);
            UiPreferences preferencesBefore = owner.CurrentPreferences;
            string serializedPreferences = JsonSerializer.Serialize(preferencesBefore);
            var store = new UiPreferencesStore(Path.Combine(ReportDirectory, fixtureName));
            byte[] persistedBefore = File.ReadAllBytes(store.FilePath);
            A1Command[] commandsBefore = transport.Commands.ToArray();
            int writesBefore = CountMockWrites(transport);
            int startupWritesBefore = desktop.StartupWrites;
            int trayShowsBefore = desktop.TrayShows;
            var applyButton = (Button)settings.FindName("ApplyAudioProfileButton");
            var saveButton = (Button)settings.FindName("SaveSettingsButton");
            Check(applyButton.IsEnabled && saveButton.IsEnabled,
                "The integrated profile action is available before confirmation: " + scenario);

            ClickSettings(settings, "ApplyAudioProfileButton");
            PumpUntil(() => !owner.IsBusy && saveButton.IsEnabled && applyButton.IsEnabled);

            long contextAfter = ReadProfileMicrophoneTargetField<long>(owner, "controlContextGeneration");
            Check(confirmations == 1
                && confirmationMessage == Localization.Text("Settings.Profiles.Apply.Confirmation", selectedProfile.Name)
                && ReferenceEquals(targets.SelectedItem, owner.Transmitters[0]),
                "Confirmation uses the selected profile and leaves the individual microphone selected: " + scenario);
            Check(ReadProfileMicrophoneTargetField<long>(owner, "generation") == receiverBefore
                && (changeDuringConfirmation ? contextAfter > contextBefore : contextAfter == contextBefore)
                && readContext() == contextAfter,
                "Only the microphone target advances the actual settings context; the receiver session stays unchanged: " + scenario);
            Check(applications == (changeDuringConfirmation ? 0 : 1)
                && CountMockWrites(transport) == writesBefore && transport.Commands.SequenceEqual(commandsBefore)
                && owner.IsConnected && !owner.IsBusy,
                "Stale confirmation never invokes profile application; an already-individual target is rejected before any receiver command: " + scenario);
            VerifyProfileReceiverDraftPreserved(settings, draftBefore, scenario);
            string resultKey = changeDuringConfirmation ? "Settings.Profiles.ContextChanged" : "Target.SharedParameters";
            string expected = Localization.Text(resultKey);
            var result = (TextBlock)settings.FindName("SectionResultReadout");
            Check(Localization.CurrentLanguage == language && expected != resultKey && result.Text == expected
                && result.Visibility == Visibility.Visible && result.Focusable
                && AutomationProperties.GetName(result) == Localization.Text("Section.Result.WithMessage", expected)
                && !UsageHints.GetEnabled(settings) && !ReadProfileReceiverDraft(settings).ShowOperationMessages,
                "The localized context-change or shared-parameter error remains accessible with hints and routine messages disabled: " + scenario);
            Check(settings.IsVisible && saveConfirmations == 0
                && ReferenceEquals(owner.CurrentPreferences, preferencesBefore)
                && JsonSerializer.Serialize(owner.CurrentPreferences) == serializedPreferences
                && File.ReadAllBytes(store.FilePath).SequenceEqual(persistedBefore)
                && desktop.StartupWrites == startupWritesBefore && desktop.TrayShows == trayShowsBefore,
                "The rejected profile operation neither applies nor commits the settings draft or desktop preferences: " + scenario);
            settings.Close();
            Pump();
            Check(!settings.IsVisible && saveConfirmations == 1
                && ReferenceEquals(owner.CurrentPreferences, preferencesBefore)
                && File.ReadAllBytes(store.FilePath).SequenceEqual(persistedBefore)
                && applications == (changeDuringConfirmation ? 0 : 1) && transport.Commands.SequenceEqual(commandsBefore),
                "Discarding the unsaved settings fixture performs no deferred profile application or commit: " + scenario);
        }
        finally { owner.RequestExit(); Pump(); }
    }

    private static TValue ReadProfileMicrophoneTargetField<TValue>(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) is TValue value
            ? value : throw new InvalidOperationException("The integrated profile fixture field is unavailable: " + name);
}
