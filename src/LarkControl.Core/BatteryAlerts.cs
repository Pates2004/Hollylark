namespace LarkControl.Core;

public enum BatteryAlertSeverity
{
    Low,
    Critical
}

public sealed record BatteryAlertPolicy
{
    public bool Enabled { get; init; } = true;
    public int LowThreshold { get; init; } = 20;
    public int CriticalThreshold { get; init; } = 10;

    public void Validate()
    {
        if (CriticalThreshold < 1 || LowThreshold > 100 || CriticalThreshold >= LowThreshold)
            throw new ArgumentException("Battery thresholds must satisfy 1 <= critical < low <= 100.");
    }
}

public sealed record BatteryAlert(int TransmitterIndex, int BatteryPercent, BatteryAlertSeverity Severity);

public sealed class BatteryAlertMonitor
{
    private const int MaximumReceivers = 32;
    private const int MaximumTransmitters = 4;
    private readonly Dictionary<string, TransmitterAlerts[]> receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> receiverOrder = new();
    private BatteryAlertPolicy? currentPolicy;

    public IReadOnlyList<BatteryAlert> Observe(string receiverPath, A1Status status, BatteryAlertPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverPath);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!policy.Enabled)
        {
            Reset();
            return Array.Empty<BatteryAlert>();
        }
        if (status.Transmitters.Count > MaximumTransmitters)
            throw new ArgumentException("Battery alerts support at most four transmitters.", nameof(status));

        if (policy != currentPolicy)
        {
            Reset();
            currentPolicy = policy;
        }
        if (!receivers.TryGetValue(receiverPath, out TransmitterAlerts[]? states))
        {
            if (receivers.Count == MaximumReceivers)
                receivers.Remove(receiverOrder.Dequeue());
            states = new TransmitterAlerts[MaximumTransmitters];
            receivers.Add(receiverPath, states);
            receiverOrder.Enqueue(receiverPath);
        }

        int lowRecovery = Math.Min(100, policy.LowThreshold + 5);
        int criticalRecovery = Math.Min(100, policy.CriticalThreshold + 5);
        List<BatteryAlert> alerts = [];
        for (int index = 0; index < status.Transmitters.Count; index++)
        {
            TransmitterState transmitter = status.Transmitters[index];
            if (!transmitter.Connected || transmitter.BatteryPercent is not int battery || battery is < 0 or > 100)
                continue;

            ref TransmitterAlerts state = ref states[index];
            if (battery >= lowRecovery)
                state.LowConsumed = false;
            if (battery >= criticalRecovery)
                state.CriticalConsumed = false;

            if (battery <= policy.CriticalThreshold)
            {
                if (!state.CriticalConsumed)
                    alerts.Add(new BatteryAlert(index, battery, BatteryAlertSeverity.Critical));
                state.CriticalConsumed = true;
                state.LowConsumed = true;
            }
            else if (battery <= policy.LowThreshold && !state.LowConsumed &&
                (battery < lowRecovery || !state.HasKnownBattery))
            {
                alerts.Add(new BatteryAlert(index, battery, BatteryAlertSeverity.Low));
                state.LowConsumed = true;
            }
            // At a low threshold of 100, later full-charge readings only rearm; they do not alert.
            state.HasKnownBattery = true;
        }
        return alerts.AsReadOnly();
    }

    public void Reset()
    {
        receivers.Clear();
        receiverOrder.Clear();
        currentPolicy = null;
    }

    private struct TransmitterAlerts
    {
        public bool LowConsumed;
        public bool CriticalConsumed;
        public bool HasKnownBattery;
    }
}
