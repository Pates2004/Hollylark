using System.IO;
using System.Windows.Threading;
using LarkControl.Core;
using LarkControl.Windows;
using Localization = LarkControl.Windows.Localization;

internal static partial class Program
{
    private static void VerifyGroupMuteFinalReadback()
    {
        foreach (int capacity in new[] { 2, 4 })
        foreach (bool muted in new[] { false, true })
        foreach (bool changeAfterLastConfirmation in new[] { false, true })
        {
            byte[] status = CreateMicrophoneScopeStatus(capacity, Enumerable.Range(0, capacity).ToArray());
            for (int index = 0; index < capacity; index++)
                status[MicrophoneScopeMuteOffsets[index]] = muted ? (byte)0 : (byte)1;
            int writes = 0;
            int readsAfterLastWrite = 0;
            bool changedEarlierMicrophone = false;
            using var transport = new DeviceWriteSafetyTransport
            {
                Respond = (source, request) =>
                {
                    if (request.Command == A1Command.GetStatus)
                    {
                        if (writes == capacity && ++readsAfterLastWrite == 2 && changeAfterLastConfirmation)
                            ChangeEarlierMicrophone();
                        source.Deliver(request.Command, status);
                    }
                    else if (request.Command == A1Command.GetVersion)
                        source.Deliver(request.Command, [request.Payload[0], 1, capacity == 4 ? (byte)1 : (byte)0, 2, 8]);
                    else if (request.Command == A1Command.SetMute)
                    {
                        status[MicrophoneScopeMuteOffsets[request.Payload[0]]] = request.Payload[1];
                        writes++;
                        if (writes == capacity && !changeAfterLastConfirmation) ChangeEarlierMicrophone();
                        source.Deliver(request.Command, [0]);
                    }
                    else throw new InvalidDataException("Unexpected group confirmation command: " + request.Command);
                }
            };
            var receiver = new ReceiverInfo("Simulated group confirmation receiver", "simulated://group-confirmation", 0, 64);
            var window = new MainWindow(() => [receiver], _ => transport,
                Path.Combine(ReportDirectory, "group-final-readback-" + Guid.NewGuid().ToString("N")),
                new MemoryDesktop(), (_, _) => true, automaticReceiverManagement: false);
            try
            {
                MoveOffscreen(window);
                window.Show();
                PumpUntil(() => !window.IsBusy && window.Receivers.Count == 1);
                SavePreferences(window, window.CurrentPreferences with
                {
                    ConfirmExit = false, ShowHints = false, ShowOperationMessages = false,
                    AnnounceStateChanges = false, AutoApplySelections = false
                });
                RunTask(window.ConnectAsync());
                MicrophoneTargetField<DispatcherTimer>(window, "pollTimer").Stop();
                Check(window.IsConnected && window.Transmitters.Count == capacity,
                    "Final group readback fixture has every intended microphone");
                RunTask(window.ToggleAllMicrophonesMuteAsync());
                string scenario = $"{Localization.CurrentLanguage}/{capacity}/{muted}/{changeAfterLastConfirmation}";
                Check(changedEarlierMicrophone && writes == capacity && readsAfterLastWrite >= 2,
                    "Group completion uses fresh status after the last per-microphone confirmation: " + scenario);
                Check(A1Status.Parse(status).Transmitters[0].Muted != muted
                    && A1Status.Parse(status).Transmitters.Skip(1).All(transmitter => transmitter.Muted == muted),
                    "The simulated last command is confirmed but the complete group is not: " + scenario);
                Check(!window.IsConnected && transport.Disposed
                    && window.ResultText.Contains(Localization.Text("Target.All.PartialFailure"), StringComparison.Ordinal)
                    && !window.ResultText.Contains(Localization.Text(muted ? "Target.All.MutedConfirmed" : "Target.All.UnmutedConfirmed"), StringComparison.Ordinal),
                    "Contradictory final status reports partial failure, never full-group success or rollback: " + scenario);
            }
            finally { window.Close(); Pump(); }

            void ChangeEarlierMicrophone()
            {
                status[MicrophoneScopeMuteOffsets[0]] = muted ? (byte)0 : (byte)1;
                changedEarlierMicrophone = true;
            }
        }
    }
}
