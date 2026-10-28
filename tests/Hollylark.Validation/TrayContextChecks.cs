using System.Reflection;
using LarkControl.Windows;
using Microsoft.Win32;

internal static partial class Program
{
    public static void VerifyTrayContextChecks()
    {
        string originalLanguage = Localization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl", "en" })
            {
                Localization.SetLanguage(language);
                Check(Localization.Text("Desktop.TrayContextChanged") != "Desktop.TrayContextChanged",
                    "Tray context failures have a translated message: " + language);
                VerifyTrayContextChanges();
                VerifyTrayContextAvailability();
                VerifyTrayContextDispatch();
                VerifyTrayContextCapture();
                VerifyTrayContextFailureRouting();
            }
        }
        finally { Localization.SetLanguage(originalLanguage); }
    }

    private static TrayControlSnapshot TrayContextSnapshot(long generation = 10) => new(true, 3, true,
        [new(0, true, false, true), new(1, true, true, true), new(2, true, false, true), new(3, true, false, true)])
    {
        ContextGeneration = generation
    };

    private static TrayCommand[] TrayContextHardwareCommands() =>
        [TrayCommand.DecreaseGain, TrayCommand.IncreaseGain, TrayCommand.ToggleMute1,
         TrayCommand.ToggleMute2, TrayCommand.ToggleMute3, TrayCommand.ToggleMute4];

    private static void VerifyTrayContextChanges()
    {
        using var fixture = new TrayContextFixture();
        TrayControlSnapshot expected = fixture.Capture();
        foreach (string change in new[] { "receiver", "selected target", "active membership" })
        {
            fixture.Snapshot = expected with
            {
                ContextGeneration = expected.ContextGeneration + 1,
                Transmitters = change == "active membership"
                    ? [new(0, true, false, true), new(1, false, false, false)] : expected.Transmitters
            };
            foreach (TrayCommand command in TrayContextHardwareCommands())
            {
                ExpectTrayContextFailure(() => fixture.Execute(command, expected),
                    "Changed " + change + " rejects the old tray command: " + command);
                Check(fixture.Gains.Count == 0 && fixture.Mutes.Count == 0,
                    "Changed tray context never calls a device delegate: " + change + "/" + command);
            }
        }
        fixture.Snapshot = expected;
        Check(fixture.Execute(TrayCommand.IncreaseGain, expected) && fixture.Gains.SequenceEqual(new[] { 1 }),
            "A rejected tray context leaves dispatch available for a valid command");
    }

    private static void VerifyTrayContextAvailability()
    {
        using var fixture = new TrayContextFixture();
        TrayControlSnapshot enabled = fixture.Snapshot;
        (TrayCommand Command, TrayControlSnapshot Disabled)[] cases =
        [
            (TrayCommand.DecreaseGain, enabled with { CanAdjustGain = false }),
            (TrayCommand.IncreaseGain, enabled with { CanAdjustGain = false }),
            (TrayCommand.DecreaseGain, enabled with { ConfirmedGain = 0 }),
            (TrayCommand.IncreaseGain, enabled with { ConfirmedGain = 5 }),
            (TrayCommand.ToggleMute1, enabled with { Transmitters = [new(0, false, false, true)] }),
            (TrayCommand.ToggleMute1, enabled with { Transmitters = [new(0, true, false, false)] }),
            (TrayCommand.ToggleMute4, enabled with { Transmitters = [new(0, true, false, true)] })
        ];
        foreach ((TrayCommand command, TrayControlSnapshot disabled) in cases)
        {
            fixture.Snapshot = enabled;
            Check(!fixture.Execute(command, disabled),
                "An initially disabled or absent tray entry cannot become actionable: " + command);
            fixture.Snapshot = disabled;
            Check(!fixture.Execute(command, enabled),
                "An initially enabled tray entry respects current availability: " + command);
        }
        Check(fixture.Gains.Count == 0 && fixture.Mutes.Count == 0,
            "Neither old nor current disabled tray entries call device delegates");
    }

    private static void VerifyTrayContextDispatch()
    {
        using var fixture = new TrayContextFixture();
        TrayControlSnapshot expected = fixture.Capture();
        foreach (TrayCommand command in TrayContextHardwareCommands())
        {
            int reads = fixture.SnapshotReads;
            Task<bool> execution = fixture.Desktop.ExecuteTrayControlAsync(command, expected);
            Check(execution.IsCompletedSuccessfully && execution.GetAwaiter().GetResult()
                && fixture.SnapshotReads == reads + 1,
                "Same-context tray dispatch validates one current snapshot and invokes synchronously: " + command);
        }
        Check(fixture.Gains.SequenceEqual(new[] { -1, 1 }) && fixture.Mutes.SequenceEqual(new[] { 0, 1, 2, 3 }),
            "Every same-context hardware command reaches its delegate exactly once");

        fixture.Snapshot = expected with { ContextGeneration = expected.ContextGeneration + 1 };
        Check(fixture.Execute(TrayCommand.DecreaseGain) && fixture.Gains.SequenceEqual(new[] { -1, 1, -1 }),
            "Existing direct tray dispatch without an expected snapshot remains compatible");
        int callbacks = fixture.Gains.Count + fixture.Mutes.Count;
        foreach (TrayCommand command in new[] { TrayCommand.Show, TrayCommand.Settings, TrayCommand.Exit,
            TrayCommand.Refresh, TrayCommand.None, (TrayCommand)999 })
            Check(!fixture.Execute(command, expected),
                "Nonhardware commands are not subjected to the hardware context guard or dispatched as hardware: " + command);
        Check(fixture.Gains.Count + fixture.Mutes.Count == callbacks,
            "Nonhardware tray commands never reach device delegates");
        Check(new TrayControlSnapshot(true, 3, true, []).ContextGeneration == 0,
            "Existing positional tray snapshots default to generation zero");
    }

    private static void VerifyTrayContextCapture()
    {
        using var fixture = new TrayContextFixture();
        TrayTransmitterSnapshot[] transmitters = [new(0, false, false, false)];
        fixture.Snapshot = fixture.Snapshot with { Transmitters = transmitters };
        int reads = fixture.SnapshotReads;
        TrayControlSnapshot expected = fixture.Capture();
        IReadOnlyList<TrayMenuEntry> entries = DesktopIntegration.CreateTrayMenu(expected);
        Check(fixture.SnapshotReads == reads + 1
            && !entries.Single(entry => entry.Command == TrayCommand.ToggleMute1).Enabled,
            "Menu capture reads the provider once and supplies the snapshot used to render entries");
        Func<bool> queuedMute = () => fixture.Execute(TrayCommand.ToggleMute1, expected);
        transmitters[0] = new(0, true, false, true);
        Check(!expected.Transmitters[0].IsConnected && !queuedMute() && fixture.Mutes.Count == 0,
            "Mutating the provider's transmitter array cannot enable an old queued tray entry");

        fixture.Snapshot = TrayContextSnapshot();
        expected = fixture.Capture();
        Func<bool> queuedGain = () => fixture.Execute(TrayCommand.IncreaseGain, expected);
        fixture.Snapshot = fixture.Snapshot with { ContextGeneration = expected.ContextGeneration + 1 };
        ExpectTrayContextFailure(() => queuedGain(),
            "A context change after menu selection is queued rejects the captured gain command");
        Check(fixture.Gains.Count == 0 && fixture.Mutes.Count == 0,
            "Queued tray commands cannot silently target a changed receiver, target or membership");

        transmitters = [new(0, true, false, true)];
        fixture.Snapshot = TrayContextSnapshot() with { Transmitters = transmitters };
        expected = fixture.Capture();
        transmitters[0] = new(0, false, false, false);
        Check(expected.Transmitters[0].IsConnected && !fixture.Execute(TrayCommand.ToggleMute1, expected)
            && fixture.Mutes.Count == 0,
            "A frozen enabled menu entry still checks current mutable transmitter availability");
    }

    private static void VerifyTrayContextFailureRouting()
    {
        using var fixture = new TrayContextFixture();
        TrayControlSnapshot expected = fixture.Capture();
        fixture.Snapshot = expected with { ContextGeneration = expected.ContextGeneration + 1 };
        var failures = new List<Exception>();
        fixture.Desktop.Failed += failures.Add;
        MethodInfo invoke = typeof(DesktopIntegration).GetMethod("InvokeTrayControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        invoke.Invoke(fixture.Desktop, [TrayCommand.IncreaseGain, expected]);
        Check(failures.Count == 1 && failures[0] is InvalidOperationException
            && failures[0].Message == Localization.Text("Desktop.TrayContextChanged")
            && fixture.Gains.Count == 0 && fixture.Mutes.Count == 0,
            "Native command dispatch reports the localized context failure through Failed without a device callback or native tray");
    }

    private static void ExpectTrayContextFailure(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message == Localization.Text("Desktop.TrayContextChanged"), name);
            return;
        }
        Check(false, name);
    }

    private sealed class TrayContextFixture : IDisposable
    {
        internal TrayControlSnapshot Snapshot { get; set; } = TrayContextSnapshot();
        internal DesktopIntegration Desktop { get; } = new(DesktopExecutablePath, new TrayContextStartupStore());
        internal List<int> Gains { get; } = [];
        internal List<int> Mutes { get; } = [];
        internal int SnapshotReads { get; private set; }

        internal TrayContextFixture() => Desktop.ConfigureTrayControls(
            () => { SnapshotReads++; return Snapshot; },
            step => { Gains.Add(step); return Task.CompletedTask; },
            index => { Mutes.Add(index); return Task.CompletedTask; });

        internal TrayControlSnapshot Capture() => Desktop.CaptureTrayControlSnapshot()!;
        internal bool Execute(TrayCommand command, TrayControlSnapshot? expected = null) =>
            Desktop.ExecuteTrayControlAsync(command, expected).GetAwaiter().GetResult();
        public void Dispose() => Desktop.Dispose();
    }

    private sealed class TrayContextStartupStore : IStartupRegistrationStore
    {
        public StartupRegistrationSnapshot Read() => throw new InvalidOperationException("Tray context checks must not access startup registration.");
        public void Write(object rawValue, RegistryValueKind valueKind) => throw new InvalidOperationException("Tray context checks must not write startup registration.");
        public void Delete() => throw new InvalidOperationException("Tray context checks must not delete startup registration.");
    }
}
