using LarkControl.Core;

internal static partial class Program
{
    private static void VerifyBatteryAlertChecks()
    {
        VerifyBatteryAlertPolicies();
        VerifyBatteryAlertThresholds();
        VerifyBatteryAlertEpisodes();
        VerifyBatteryAlertTransmittersAndReceivers();
        VerifyBatteryAlertMissingReadings();
        VerifyBatteryAlertPolicyChanges();
        VerifyBatteryAlertBoundedState();
        VerifyBatteryAlertSnapshots();
    }

    private static void VerifyBatteryAlertPolicies()
    {
        var defaults = new BatteryAlertPolicy();
        defaults.Validate();
        Check(defaults.Enabled && defaults.LowThreshold == 20 && defaults.CriticalThreshold == 10,
            "Battery alerts default to enabled with low 20 and critical 10");
        Check(defaults == new BatteryAlertPolicy() && defaults == (defaults with { }),
            "Battery policies compare by value");
        Check(new BatteryAlert(3, 0, BatteryAlertSeverity.Critical) == new BatteryAlert(3, 0, BatteryAlertSeverity.Critical),
            "Battery alerts compare by index, percentage and severity");

        int[] thresholds = [int.MinValue, -1, 0, 1, 2, 10, 20, 95, 99, 100, 101, int.MaxValue];
        foreach (int low in thresholds)
        {
            foreach (int critical in thresholds)
            {
                var policy = new BatteryAlertPolicy { LowThreshold = low, CriticalThreshold = critical };
                string name = $"Battery policy low={low}, critical={critical} validates its range and ordering";
                if (critical >= 1 && low <= 100 && critical < low)
                {
                    policy.Validate();
                    Check(true, name);
                }
                else
                    Throws<ArgumentException>(policy.Validate, name);
            }
        }
        Throws<ArgumentException>(() => (defaults with { Enabled = false, CriticalThreshold = 0 }).Validate(),
            "Disabled battery policies still have valid thresholds");
        var monitor = new BatteryAlertMonitor();
        Throws<ArgumentException>(() => monitor.Observe("rx", BatteryAlertReading(5), defaults with { LowThreshold = 10 }),
            "Battery observations validate their policy");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(5), defaults),
            "Rejected policy does not consume the initial critical alert", new BatteryAlert(0, 5, BatteryAlertSeverity.Critical));
    }

    private static void VerifyBatteryAlertThresholds()
    {
        (int Low, int Critical)[] thresholds = [(20, 10), (2, 1), (21, 20), (96, 95), (99, 98), (100, 99)];
        foreach ((int low, int critical) in thresholds)
        {
            var policy = new BatteryAlertPolicy { LowThreshold = low, CriticalThreshold = critical };
            for (int battery = 0; battery <= 100; battery++)
            {
                var monitor = new BatteryAlertMonitor();
                IReadOnlyList<BatteryAlert> alerts = monitor.Observe("rx", BatteryAlertReading(battery), policy);
                string name = $"Initial battery {battery}% with low={low}, critical={critical} selects one inclusive band";
                if (battery <= critical)
                    CheckBatteryAlerts(alerts, name, new BatteryAlert(0, battery, BatteryAlertSeverity.Critical));
                else if (battery <= low)
                    CheckBatteryAlerts(alerts, name, new BatteryAlert(0, battery, BatteryAlertSeverity.Low));
                else
                    CheckBatteryAlerts(alerts, name);
                CheckBatteryAlerts(monitor.Observe("RX", BatteryAlertReading(battery), policy with { }),
                    $"Unchanged {battery}% with low={low}, critical={critical} never repeats, even with a new equal policy");
            }
        }
    }

    private static void VerifyBatteryAlertEpisodes()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        IReadOnlyList<BatteryAlert> Observe(int battery) => monitor.Observe("rx", BatteryAlertReading(battery), policy);

        CheckBatteryAlerts(Observe(21), "A healthy initial battery is silent");
        CheckBatteryAlerts(Observe(20), "Low threshold equality alerts", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        foreach (int battery in new[] { 19, 20, 24, 21, 20, 11 })
            CheckBatteryAlerts(Observe(battery), $"Low episode stays consumed at {battery}% below recovery");
        CheckBatteryAlerts(Observe(10), "Critical threshold equality escalates once", new BatteryAlert(0, 10, BatteryAlertSeverity.Critical));
        foreach (int battery in new[] { 9, 0, 10, 14, 10, 11, 20 })
            CheckBatteryAlerts(Observe(battery), $"Critical episode does not emit low or critical while recovering to {battery}%");
        CheckBatteryAlerts(Observe(10), "Critical recovery at 20 rearms critical independently", new BatteryAlert(0, 10, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(Observe(15), "Exact critical recovery is silent and leaves low consumed");
        CheckBatteryAlerts(Observe(10), "Exact critical recovery rearms critical", new BatteryAlert(0, 10, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(Observe(24), "One below low recovery is silent");
        CheckBatteryAlerts(Observe(20), "One below low recovery does not rearm low");
        CheckBatteryAlerts(Observe(25), "Exact low recovery is silent");
        CheckBatteryAlerts(Observe(20), "Exact low recovery rearms low", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(Observe(10), "Both severities work again in the next episode", new BatteryAlert(0, 10, BatteryAlertSeverity.Critical));

        monitor.Reset();
        CheckBatteryAlerts(Observe(5), "Initial critical emits no additional low alert", new BatteryAlert(0, 5, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(Observe(14), "Initial critical consumes low through partial recovery");
        CheckBatteryAlerts(Observe(5), "Partial critical recovery cannot repeat critical");
        CheckBatteryAlerts(Observe(20), "Initial critical consumes low even after critical recovery");

        foreach ((int low, int critical) in new[] { (96, 95), (99, 98), (100, 99) })
        {
            monitor.Reset();
            policy = new BatteryAlertPolicy { LowThreshold = low, CriticalThreshold = critical };
            CheckBatteryAlerts(Observe(critical), $"High-threshold {low}/{critical} starts at critical",
                new BatteryAlert(0, critical, BatteryAlertSeverity.Critical));
            CheckBatteryAlerts(Observe(99), $"99% cannot rearm thresholds {low}/{critical} with capped recovery");
            CheckBatteryAlerts(Observe(critical), $"High-threshold {low}/{critical} remains consumed before full charge");
            CheckBatteryAlerts(Observe(100), $"100% rearms thresholds {low}/{critical} without an alert on recovery");
            CheckBatteryAlerts(Observe(100), $"Repeated full charge is silent for thresholds {low}/{critical}");
            CheckBatteryAlerts(Observe(critical), $"Full charge rearms critical for thresholds {low}/{critical}",
                new BatteryAlert(0, critical, BatteryAlertSeverity.Critical));
            CheckBatteryAlerts(Observe(100), $"A second full charge rearms low for thresholds {low}/{critical}");
            if (low == 100)
            {
                CheckBatteryAlerts(Observe(100), "Low 100 stays silent at the recovery sample");
                CheckBatteryAlerts(Observe(99), "Low 100 critical 99 descends directly to critical after full recovery",
                    new BatteryAlert(0, 99, BatteryAlertSeverity.Critical));
            }
            else
                CheckBatteryAlerts(Observe(low), $"Full charge rearms low for thresholds {low}/{critical}",
                    new BatteryAlert(0, low, BatteryAlertSeverity.Low));
        }

        monitor.Reset();
        policy = new BatteryAlertPolicy { LowThreshold = 100, CriticalThreshold = 10 };
        CheckBatteryAlerts(Observe(100), "Initial full charge still meets a configured low threshold of 100",
            new BatteryAlert(0, 100, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(Observe(100), "Repeated full charge rearms without repeating low 100");
        CheckBatteryAlerts(Observe(99), "A drop after full recovery can alert low 100 again", new BatteryAlert(0, 99, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(Observe(99), "Repeated low 100 band stays consumed below full charge");
    }

    private static void VerifyBatteryAlertTransmittersAndReceivers()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        A1Status status = CreateBatteryAlertStatus(new TransmitterState(true, 20, false), new TransmitterState(true, 10, true), new TransmitterState(true, 21, false), new TransmitterState(true, 0, false));
        CheckBatteryAlerts(monitor.Observe("Receiver-A", status, policy),
            "TX1 through TX4 alert independently in index order, including muted and zero-percent TXs",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low), new BatteryAlert(1, 10, BatteryAlertSeverity.Critical), new BatteryAlert(3, 0, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(monitor.Observe("RECEIVER-a", status, policy), "Receiver identity ignores path casing");
        CheckBatteryAlerts(monitor.Observe("Receiver-B", status, policy), "Another receiver has independent TX episodes",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low), new BatteryAlert(1, 10, BatteryAlertSeverity.Critical), new BatteryAlert(3, 0, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(monitor.Observe("receiver-A", status, policy), "Switching back preserves all receiver A episodes");

        CheckBatteryAlerts(monitor.Observe("Receiver-A", CreateBatteryAlertStatus(
            new TransmitterState(true, 25, false), new TransmitterState(true, 10, false), new TransmitterState(true, 20, false), new TransmitterState(true, 0, false)), policy),
            "Only previously healthy TX3 alerts while TX1 recovers", new BatteryAlert(2, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("Receiver-A", status, policy), "TX1 recovery rearms only TX1",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("Receiver-B", status, policy), "Recovery on A does not rearm receiver B");

        A1Status fewerSlots = CreateBatteryAlertStatus(new TransmitterState(true, 20, false), new TransmitterState(true, 10, false));
        CheckBatteryAlerts(monitor.Observe("Receiver-A", fewerSlots, policy), "A shorter topology does not clear missing TX slots");
        CheckBatteryAlerts(monitor.Observe("Receiver-A", status, policy), "Restoring TX3 and TX4 slots retains their episodes");
        CheckBatteryAlerts(monitor.Observe("Receiver-A", CreateBatteryAlertStatus(), policy), "An empty topology is silent");
        CheckBatteryAlerts(monitor.Observe("Receiver-A", status, policy), "An empty topology does not reset the receiver");

        var fresh = new BatteryAlertMonitor();
        CheckBatteryAlerts(fresh.Observe("Receiver-A", BatteryAlertReading(20), policy),
            "A separate monitor does not share or persist episodes", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        Throws<ArgumentException>(() => monitor.Observe("Receiver-A", CreateBatteryAlertStatus(
            new TransmitterState(true, 0, false), new TransmitterState(true, 0, false), new TransmitterState(true, 0, false), new TransmitterState(true, 0, false), new TransmitterState(true, 0, false)), policy),
            "More than four transmitter slots are rejected");
        CheckBatteryAlerts(monitor.Observe("Receiver-A", status, policy), "Rejected topology does not reset valid TX episodes");
    }

    private static void VerifyBatteryAlertMissingReadings()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        int?[] unknown = [null, -1, int.MinValue, 101, 255, int.MaxValue];
        foreach (int? battery in unknown)
        {
            CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(battery), policy),
                $"Unknown or invalid connected battery {battery} is not invented as zero");
        }
        foreach (int? battery in new int?[] { null, 0, 5, 20, 25, 100, 255 })
        {
            CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(battery, connected: false), policy),
                $"Disconnected battery {battery} cannot alert");
        }
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(20), policy),
            "Unknown and disconnected readings do not consume the first low alert", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        foreach (int? battery in unknown)
            CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(battery), policy), "Unknown readings preserve an existing low episode");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(100, connected: false), policy),
            "Disconnected high battery cannot rearm low");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(20), policy), "Low reconnect does not spam");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(5), policy),
            "Known critical still escalates after missing readings", new BatteryAlert(0, 5, BatteryAlertSeverity.Critical));
        foreach (int? battery in unknown)
            CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(battery), policy), "Unknown readings preserve a critical episode");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(100, connected: false), policy),
            "Disconnected high battery cannot rearm critical");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(5), policy), "Critical reconnect does not spam");
        CheckBatteryAlerts(monitor.Observe("rx", CreateBatteryAlertStatus(
            new TransmitterState(true, null, false), new TransmitterState(true, 5, false), new TransmitterState(false, 0, false), new TransmitterState(true, 101, false)), policy),
            "Unknown and disconnected TXs do not block an independent known TX", new BatteryAlert(1, 5, BatteryAlertSeverity.Critical));
    }

    private static void VerifyBatteryAlertPolicyChanges()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        A1Status status = BatteryAlertReading(15);
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Policy reset scenario starts with low A", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("b", status, policy), "Policy reset scenario starts with low B", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("a", status, new BatteryAlertPolicy()), "Equal settings preserve consumed episodes");

        policy = policy with { CriticalThreshold = 15 };
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Changing critical threshold reevaluates the current reading",
            new BatteryAlert(0, 15, BatteryAlertSeverity.Critical));
        CheckBatteryAlerts(monitor.Observe("b", status, policy), "A policy change resets other receivers too",
            new BatteryAlert(0, 15, BatteryAlertSeverity.Critical));
        policy = policy with { LowThreshold = 30 };
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Changing only low threshold also resets consumed critical",
            new BatteryAlert(0, 15, BatteryAlertSeverity.Critical));
        policy = new BatteryAlertPolicy { LowThreshold = 10, CriticalThreshold = 5 };
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Lower thresholds reevaluate into silence");
        policy = new BatteryAlertPolicy();
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Returning to old thresholds starts fresh", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("b", status, policy), "Both receivers have consumed alerts before disabling",
            new BatteryAlert(0, 15, BatteryAlertSeverity.Low));

        var disabled = policy with { Enabled = false };
        CheckBatteryAlerts(monitor.Observe("a", BatteryAlertReading(0), disabled), "Disabled alerts are empty even at zero percent");
        CheckBatteryAlerts(monitor.Observe("b", status, disabled), "Repeated disabled observations stay empty");
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Reenabling reevaluates A after disabled reset", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("b", status, policy), "Reenabling reevaluates all receiver histories", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        monitor.Reset();
        monitor.Reset();
        CheckBatteryAlerts(monitor.Observe("a", status, policy), "Explicit Reset is repeatable and clears A", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("b", status, policy), "Explicit Reset clears every receiver", new BatteryAlert(0, 15, BatteryAlertSeverity.Low));
    }

    private static void VerifyBatteryAlertBoundedState()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        A1Status status = BatteryAlertReading(20);
        for (int receiver = 0; receiver < 32; receiver++)
            CheckBatteryAlerts(monitor.Observe($"rx-{receiver}", status, policy), $"Receiver cache admits receiver {receiver}",
                new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        for (int receiver = 0; receiver < 32; receiver++)
            CheckBatteryAlerts(monitor.Observe($"RX-{receiver}", status, policy), $"Receiver cache retains all 32 episodes including {receiver}");
        CheckBatteryAlerts(monitor.Observe("rx-32", status, policy), "Receiver cache admits a 33rd receiver", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        for (int receiver = 1; receiver <= 32; receiver++)
            CheckBatteryAlerts(monitor.Observe($"rx-{receiver}", status, policy), $"Eviction preserves newer receiver {receiver}");
        CheckBatteryAlerts(monitor.Observe("rx-0", status, policy), "The oldest receiver was evicted at the 32-receiver bound",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("rx-2", status, policy), "Reinserting an evicted receiver preserves other retained histories");
        CheckBatteryAlerts(monitor.Observe("rx-1", status, policy), "Eviction continues in first-observed order",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low));

        monitor.Reset();
        for (int receiver = 0; receiver < 32; receiver++)
            CheckBatteryAlerts(monitor.Observe($"fresh-{receiver}", status, policy), $"Reset clears cache and eviction order for {receiver}",
                new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("FRESH-0", status, policy), "Reset leaves room for 32 fresh receiver identities");
        CheckBatteryAlerts(monitor.Observe("fresh-32", status, policy), "The receiver bound still applies after Reset",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(monitor.Observe("fresh-1", status, policy), "Post-reset eviction retains the next receiver");
        CheckBatteryAlerts(monitor.Observe("fresh-0", status, policy), "Post-reset eviction uses the fresh FIFO order",
            new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
    }

    private static void VerifyBatteryAlertSnapshots()
    {
        var monitor = new BatteryAlertMonitor();
        var policy = new BatteryAlertPolicy();
        IReadOnlyList<BatteryAlert> first = monitor.Observe("rx", BatteryAlertReading(20), policy);
        Check(first is not BatteryAlert[], "Nonempty battery results do not expose a writable array");
        if (first is IList<BatteryAlert> list)
        {
            Check(list.IsReadOnly, "Battery result list reports read-only access");
            Throws<NotSupportedException>(() => list[0] = new BatteryAlert(3, 0, BatteryAlertSeverity.Critical), "Battery result elements cannot be replaced");
            Throws<NotSupportedException>(list.Clear, "Battery results cannot be cleared");
            Throws<NotSupportedException>(() => list.Add(new BatteryAlert(3, 0, BatteryAlertSeverity.Critical)), "Battery results cannot be extended");
        }
        IReadOnlyList<BatteryAlert> empty = monitor.Observe("rx", BatteryAlertReading(20), policy);
        CheckBatteryAlerts(empty, "A deduplicated result is empty");
        CheckBatteryAlerts(monitor.Observe("rx", BatteryAlertReading(10), policy), "A later critical has its own result",
            new BatteryAlert(0, 10, BatteryAlertSeverity.Critical));
        monitor.Reset();
        CheckBatteryAlerts(first, "Later observations and Reset cannot mutate a retained alert result", new BatteryAlert(0, 20, BatteryAlertSeverity.Low));
        CheckBatteryAlerts(empty, "Later observations and Reset cannot populate an earlier empty result");
    }

    private static A1Status CreateBatteryAlertStatus(params TransmitterState[] transmitters) =>
        new(transmitters, false, 0, 3, 0, 0, true, 1, false, 0, false);

    private static A1Status BatteryAlertReading(int? battery, bool connected = true) =>
        CreateBatteryAlertStatus(new TransmitterState(connected, battery, false));

    private static void CheckBatteryAlerts(IReadOnlyList<BatteryAlert> actual, string name, params BatteryAlert[] expected) =>
        Check(actual.SequenceEqual(expected), name);
}
