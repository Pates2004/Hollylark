using System.IO;
using LarkControl.Core;

internal static partial class Program
{
    private static int passed;
    private static bool skipNativeTraySmoke;
    private static bool skipNativeMenuSmoke;
    private static readonly byte[] ValidStatus = [1, 1, 90, 50, 0, 1, 1, 2, 3, 0, 0, 0, 3, 1, 2, 0, 0];

    [STAThread]
    private static int Main(string[] arguments)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (arguments.Length > 0 && arguments[0] == "--single-instance-helper")
                return RunSingleInstanceHelper(arguments);
            if (arguments.Contains("--single-instance-only"))
            {
                VerifySingleInstanceChecks();
                Console.WriteLine($"PASS: {passed} single-instance checks. Owned child processes and isolated names only; no GUI, physical USB/audio, registry or global input.");
                return 0;
            }
            if ((arguments.Length == 2 || arguments.Length == 3 && arguments[2] == "--include-focus")
                && arguments[0] == "--observe-arrow-automation")
                return ObserveArrowAutomation(arguments[1], arguments.Length == 3);
            LarkControl.Windows.Localization.Initialize();
            if (arguments.Contains("--single-instance-ui-only"))
            {
                VerifySingleInstanceActivationApplication();
                Console.WriteLine($"PASS: {passed} single-instance WPF activation checks. MemoryDesktop and simulated devices only; no native tray, physical USB/audio, registry or global input.");
                return 0;
            }
            skipNativeMenuSmoke = arguments.Contains("--without-native-shell");
            skipNativeTraySmoke = skipNativeMenuSmoke || arguments.Contains("--without-native-tray");
            if (arguments.Contains("--microphone-targets-only") || arguments.Contains("--microphone-interface-only"))
            {
                if (!arguments.Contains("--microphone-interface-only"))
                {
                    VerifyStatusTopologySafetyChecks().GetAwaiter().GetResult();
                    VerifyMicrophoneScopeChecks().GetAwaiter().GetResult();
                }
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    ThemeMode = System.Windows.ThemeMode.Light
                };
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                try
                {
                    VerifyMicrophoneTargetInterfaceChecks();
                    VerifyProfileMicrophoneTargetChecks();
                }
                finally { application.Shutdown(); }
                Console.WriteLine($"PASS: {passed} microphone scope checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--native-tray-only"))
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    ThemeMode = System.Windows.ThemeMode.Light
                };
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                try { VerifyNativeTrayLifecycle(); }
                finally { application.Shutdown(); }
                Console.WriteLine($"PASS: {passed} native Explorer tray checks. No physical USB/audio or startup registry access.");
                return 0;
            }
            if (arguments.Contains("--slider-probe-only"))
            {
                VerifySliderAutomationProbe();
                Console.WriteLine($"PASS: {passed} native/guarded slider automation probe checks.");
                return 0;
            }
            if (arguments.Contains("--accessibility-revision-only") || arguments.Contains("--main-accessibility-only"))
            {
                VerifyDeviceWriteSafetyChecks().GetAwaiter().GetResult();
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    ThemeMode = System.Windows.ThemeMode.System
                };
                SynchronizationContext? previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                try
                {
                    if (!arguments.Contains("--main-accessibility-only")) VerifyFirstRunSettingsRevisionChecks();
                    VerifyMainAccessibilityRevisionChecks();
                }
                finally
                {
                    application.Shutdown();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
                Console.WriteLine($"PASS: {passed} accessibility revision checks. Simulated devices only; real NVDA speech remains untested.");
                return 0;
            }
            if (arguments.Contains("--automatic-usb-only"))
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    ThemeMode = System.Windows.ThemeMode.System
                };
                SynchronizationContext? previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                try
                {
                    VerifyAutomaticUsbChecks();
                    VerifyProfileReceiverGenerationChecks();
                }
                finally
                {
                    application.Shutdown();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
                Console.WriteLine($"PASS: {passed} automatic USB and profile generation checks. Simulated devices only; NVDA speech and physical unplugging remain untested.");
                return 0;
            }
            if (arguments.Contains("--expanded-settings-investigation-only"))
            {
                VerifyExpandedSettingsInvestigationApplication();
                Console.WriteLine($"PASS: {passed} traced expanded Settings checks. No physical USB/audio, global input or NVDA changes.");
                return 0;
            }
            if (arguments.Contains("--confirmation-ownership-only"))
            {
                VerifyConfirmationOwnershipApplication();
                Console.WriteLine($"PASS: {passed} confirmation ownership and decline checks. Native MessageBox input and NVDA speech remain untested.");
                return 0;
            }
            if (arguments.Contains("--interface-parity-followup-only"))
            {
                VerifyInterfaceParityFollowupApplication();
                Console.WriteLine($"PASS: {passed} interface caption and name checks. No physical USB/audio or global input injection.");
                return 0;
            }
            if (arguments.Contains("--focus-investigation-only"))
            {
                VerifyFocusInvestigationApplication();
                Console.WriteLine($"PASS: {passed} traced focus investigation checks. No physical USB/audio, global input or NVDA changes.");
                return 0;
            }
            if (arguments.Contains("--native-lists-only"))
            {
                VerifyNativeListParityApplication();
                Console.WriteLine($"PASS: {passed} native list parity checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--list-navigation-only"))
            {
                VerifyListNavigationApplication();
                Console.WriteLine($"PASS: {passed} list navigation checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--presentation-stability-only"))
            {
                VerifyPresentationStabilityApplication();
                Console.WriteLine($"PASS: {passed} presentation stability checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--escape-behavior-only"))
            {
                VerifyEscapeBehaviorApplication();
                Console.WriteLine($"PASS: {passed} Escape behavior checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--arrow-announcements-only"))
            {
                VerifyArrowAnnouncementApplication();
                Console.WriteLine($"PASS: {passed} external UIA arrow checks. No physical USB/audio, registry writes or global input injection.");
                return 0;
            }
            if (arguments.Contains("--peak-review-only"))
            {
                VerifyPeakReviewApplication();
                Console.WriteLine($"PASS: {passed} peak review regressions. No physical USB/audio or registry writes.");
                return 0;
            }
            if (arguments.Contains("--audio-peaks-only"))
            {
                VerifyAudioLevels();
                VerifyAudioPeakCalculations();
                VerifyAudioPeakApplication();
                Console.WriteLine($"PASS: {passed} audio peak checks. No physical USB/audio capture or registry writes.");
                return 0;
            }
            if (arguments.Contains("--focus-followup-only"))
            {
                VerifyFocusFollowupInterface();
                Console.WriteLine($"PASS: {passed} focus follow-up checks. No physical USB/audio or registry writes.");
                return 0;
            }
            if (arguments.Contains("--followup-only"))
            {
                VerifyFollowupInterface();
                Console.WriteLine($"PASS: {passed} follow-up checks. No physical USB/audio or registry writes.");
                return 0;
            }
            if (arguments.Contains("--session-followup-only"))
            {
                VerifySessionFollowupInterface();
                Console.WriteLine($"PASS: {passed} session follow-up checks. No physical USB/audio or registry writes.");
                return 0;
            }
            if (arguments.Contains("--localization-only"))
            {
                VerifyLocalizationCore();
                VerifyLocalizationApplication();
                Console.WriteLine($"PASS: {passed} localization checks. No physical USB/audio or registry writes.");
                return 0;
            }
            if (arguments.Contains("--hardware-settings") || arguments.Contains("--hardware-ui-settings"))
            {
                if (!arguments.Contains("--allow-temporary-settings"))
                    throw new ArgumentException("Hardware settings tests require --allow-temporary-settings. Values are temporarily changed and then restored.");
                if (arguments.Contains("--hardware-ui-settings")) VerifyHardwareUserActions();
                else VerifyHardwareSettingsAsync().GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} physical checks. Original settings restored; audio effects not tested.");
                return 0;
            }
            if (arguments.Contains("--hardware-ui"))
            {
                VerifyHardwareInterface();
                Console.WriteLine($"PASS: {passed} checks. Physical USB reads only; no setting changes.");
                return 0;
            }
            if (arguments.Contains("--parity-ui-only"))
            {
                VerifyParityInterface();
                Console.WriteLine($"PASS: {passed} parity UI checks. No physical USB/audio capture or registry writes.");
                return 0;
            }
            if (arguments.Contains("--preferences-ui-only"))
            {
                VerifyPreferencesInterface();
                Console.WriteLine($"PASS: {passed} preference UI checks. No physical USB commands or startup registry writes.");
                return 0;
            }
            VerifySingleInstanceChecks();
            VerifyLocalizationCore();
            VerifyProtocol();
            VerifyAudioLevels();
            VerifyAudioPeakCalculations();
            VerifyIdentityAsync().GetAwaiter().GetResult();
            VerifyController().GetAwaiter().GetResult();
            VerifyDeviceWriteSafetyChecks().GetAwaiter().GetResult();
            VerifyMicrophoneScopeChecks().GetAwaiter().GetResult();
            VerifyStatusTopologySafetyChecks().GetAwaiter().GetResult();
            if (!arguments.Contains("--core-only")) VerifyInterface();
            Console.WriteLine($"PASS: {passed} checks. No physical USB commands sent.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyProtocol()
    {
        byte[] heartbeat = A1Protocol.Encode(A1Command.GetStatus, []);
        Check(heartbeat.Length == 64 && Convert.ToHexString(heartbeat.AsSpan(0, 8)) == "0503AADD1F0000EF", "Status request matches APK vector");
        byte[] gain = A1Protocol.Encode(A1Command.SetGain, [5]);
        Check(Convert.ToHexString(gain.AsSpan(0, 9)) == "0503AADD03000105EF", "Gain request matches APK vector");
        byte[] noise = A1Protocol.Encode(A1Command.SetNoise, [1, 2]);
        Check(Convert.ToHexString(noise.AsSpan(0, 10)) == "0503AADD1300020102EF", "Noise request matches APK vector");
        Check(gain.AsSpan(9).ToArray().All(value => value == 0), "Report padding is zero");
        Check(SettingChange.Lights(true).Payload.SequenceEqual(new byte[] { 0 }), "LED polarity is inverted");
        Check(SettingChange.Identification(true).Payload.SequenceEqual(new byte[] { 1 }), "Identification polarity is not inverted");
        Check(SettingChange.Equalizer(3).Payload.SequenceEqual(new byte[] { 3 }), "EQ is one-based");
        Check(SettingChange.Mute(1, true).Payload.SequenceEqual(new byte[] { 1, 1 }), "Mute addresses the second transmitter");
        Throws<ArgumentOutOfRangeException>(() => SettingChange.Gain(6), "Gain upper bound");
        Throws<ArgumentOutOfRangeException>(() => SettingChange.Gain(-1), "Gain lower bound");
        Throws<ArgumentOutOfRangeException>(() => SettingChange.Equalizer(0), "EQ lower bound");
        Throws<ArgumentOutOfRangeException>(() => SettingChange.Noise(true, 3), "Noise upper bound");
        Throws<ArgumentOutOfRangeException>(() => SettingChange.Mute(4, true), "TX bound");
        Throws<ArgumentOutOfRangeException>(() => A1Protocol.Encode((A1Command)0x1C, []), "Firmware update command rejected");
        Throws<ArgumentOutOfRangeException>(() => A1Protocol.Encode(A1Command.GetStatus, new byte[57]), "Oversized frame rejected");
        byte[] response = Reply(A1Command.GetStatus, ValidStatus);
        Check(A1Protocol.TryDecode(response, out A1Frame? frame) && frame!.Payload.SequenceEqual(ValidStatus), "Decode device response");
        Check(!A1Protocol.TryDecode(heartbeat, out _), "Request echo cannot satisfy a reply");
        Check(A1Protocol.TryDecodeRequest(heartbeat, out _), "Request decoder accepts actual request");
        Check(!A1Protocol.TryDecodeRequest(response, out _), "Reply cannot be sent as a request");
        byte[] capturedStatus = new byte[64];
        Convert.FromHexString("0503BBDD1F001100000000000000000401000001000200006E").CopyTo(capturedStatus, 0);
        Check(A1Protocol.TryDecode(capturedStatus, out A1Frame? physicalFrame), "Real receiver BB header and opaque trailer accepted");
        A1Status physicalState = A1Status.Parse(physicalFrame!.Payload);
        Check(physicalState.Gain == 4 && physicalState.ChannelMode == 1 && physicalState.Transmitters.All(transmitter => !transmitter.Connected), "Real receiver status parses correctly");
        A1Status connectedState = A1Status.Parse(Convert.FromHexString("0101646400000002040100000100020000"));
        Check(connectedState.Transmitters.Count == 2 && connectedState.Transmitters.All(transmitter =>
            transmitter.Connected && transmitter.BatteryPercent == 100 && !transmitter.Muted),
            "Captured online status reports both microphones at 100 percent and unmuted");
        byte[] capturedVersion = new byte[64];
        Convert.FromHexString("0503BBDD0D000500010002086F").CopyTo(capturedVersion, 0);
        Check(A1Protocol.TryDecode(capturedVersion, out A1Frame? physicalVersion) && physicalVersion!.Payload.SequenceEqual(new byte[] { 0, 1, 0, 2, 8 }), "Real receiver 1.0.2.8 version fixture");
        for (int length = 0; length < 8 + ValidStatus.Length; length++)
            Check(!A1Protocol.TryDecode(response.AsSpan(0, length), out _), $"Truncated report {length} rejected");
        foreach (int offset in new[] { 0, 1, 2, 3, 5 })
        {
            byte[] broken = (byte[])response.Clone();
            broken[offset] ^= 0x7F;
            Check(!A1Protocol.TryDecode(broken, out _), $"Malformed frame offset {offset} rejected");
        }
        byte[] brokenRequest = (byte[])heartbeat.Clone();
        brokenRequest[7] = 0;
        Check(!A1Protocol.TryDecodeRequest(brokenRequest, out _), "Outgoing EF terminator enforced");
        brokenRequest = (byte[])heartbeat.Clone();
        brokenRequest[4] = 0x1C;
        Check(!A1Protocol.TryDecodeRequest(brokenRequest, out _), "Transport request guard excludes firmware commands");
        Check(!A1Protocol.TryDecodeRequest(heartbeat.AsSpan(0, 8), out _), "Feature request must be exactly 64 bytes");
        Check(!A1Protocol.TryDecodeRequest([.. heartbeat, 0], out _), "Oversized feature request rejected");
        var random = new Random(1031);
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            byte[] bytes = new byte[random.Next(0, 260)];
            random.NextBytes(bytes);
            A1Protocol.TryDecode(bytes, out _);
        }
        Check(true, "10000 arbitrary reports cause no decoder exception");
        A1Status status = A1Status.Parse(ValidStatus);
        Check(status.Transmitters[0] == new TransmitterState(true, 90, false), "TX1 state");
        Check(status.Transmitters[1] == new TransmitterState(true, 50, true), "TX2 state");
        Check(status.NoiseEnabled && status.NoiseLevel == 2 && status.Gain == 3 && status.Equalizer == 3, "Audio fields");
        Check(status.LightsEnabled && status.ReverbEnabled && status.ReverbLevel == 2, "LED and reverb fields");
        Check(A1Status.Parse(ValidStatus.AsSpan(0, 15)).IdentificationEnabled is null, "Missing optional field remains unknown");
        foreach (int length in new[] { 15, 16 })
            Check(A1Status.Parse(ValidStatus.AsSpan(0, length)).RadioModeCode is null,
                "Missing radio-mode field remains unknown at payload length " + length);
        foreach (int code in new[] { 0, 1, 2, 127, 255 })
        {
            byte[] radioStatus = (byte[])ValidStatus.Clone();
            radioStatus[16] = (byte)code;
            A1Status parsedRadio = A1Status.Parse(radioStatus);
            Check(parsedRadio.RadioModeCode == code && parsedRadio.Gain == 3 && parsedRadio.Transmitters.Count == 2,
                "Passive radio-mode code preserves unknown values without disturbing other fields: " + code);
        }
        Check(physicalState.RadioModeCode == 0 && connectedState.RadioModeCode == 0,
            "Historical receiver fixtures include raw radio-mode code zero, without asserting global RF meaning");
        byte[] extended = [.. ValidStatus, 1, 0, 30, 255, 1, 0];
        A1Status extraStatus = A1Status.Parse(extended);
        Check(extraStatus.Transmitters.Count == 4 && extraStatus.Transmitters[2].Muted, "Extended transmitter status");
        Check(extraStatus.RadioModeCode == 0, "Complete extended TX fields preserve the radio-mode byte");
        Check(extraStatus.Transmitters[3].BatteryPercent is null, "Unknown battery remains unknown");
        Throws<InvalidDataException>(() => A1Status.Parse(new byte[14]), "Short status rejected");
        byte[] invalid = (byte[])ValidStatus.Clone();
        invalid[8] = 6;
        Throws<InvalidDataException>(() => A1Status.Parse(invalid), "Invalid device setting rejected");
        foreach (int offset in new[] { 0, 1, 4, 5, 6, 11, 13, 15 })
        {
            byte[] invalidFlag = (byte[])ValidStatus.Clone();
            invalidFlag[offset] = 2;
            Throws<InvalidDataException>(() => A1Status.Parse(invalidFlag), $"Invalid boolean at offset {offset} rejected");
        }
        byte[] offline = (byte[])ValidStatus.Clone();
        offline[0] = 0;
        Check(!SettingChange.Mute(0, false).IsConfirmed(A1Status.Parse(offline)), "Offline transmitter cannot confirm an unmute");
    }

    private static async Task VerifyController()
    {
        using (var transport = new FakeTransport())
        using (var controller = new A1Controller(transport))
        {
            A1Status state = await controller.ReadStatusAsync();
            Check(state.Gain == 3, "Controller status request");
            Check(await controller.ReadVersionAsync(0) == new Version(1, 0, 1, 3), "Firmware version parsing");
            state = await controller.ApplyAsync(SettingChange.Gain(5));
            Check(state.Gain == 5 && transport.Commands.TakeLast(2).SequenceEqual(new[] { A1Command.SetGain, A1Command.GetStatus }), "Write success requires status readback");
            state = await controller.ApplyAsync(SettingChange.Lights(false));
            Check(!state.LightsEnabled, "LED readback");
            SettingChange[] changes =
            [
                SettingChange.ChannelMode(1), SettingChange.Noise(false, 1), SettingChange.Noise(true, 0),
                SettingChange.Equalizer(1), SettingChange.Equalizer(2), SettingChange.Equalizer(3),
                SettingChange.Reverb(false, 0), SettingChange.Reverb(true, 1),
                SettingChange.AutoOff(1), SettingChange.AutoOff(0),
                SettingChange.Identification(true), SettingChange.Identification(false),
                SettingChange.Mute(0, true), SettingChange.Mute(1, false)
            ];
            foreach (SettingChange change in changes)
            {
                state = await controller.ApplyAsync(change);
                Check(change.IsConfirmed(state), $"Setter readback: {change.Command} {Convert.ToHexString(change.Payload)}");
            }
            Task<A1Status>[] requests = Enumerable.Range(0, 8).Select(_ => controller.ReadStatusAsync()).ToArray();
            await Task.WhenAll(requests);
            Check(transport.MaximumConcurrentWrites == 1, "Concurrent callers are serialized");
        }
        using (var transport = new FakeTransport { RejectWrite = true })
        using (var controller = new A1Controller(transport))
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(SettingChange.Gain(4)), "Negative acknowledgement cannot report success");
        using (var transport = new FakeTransport { IgnoreWrite = true })
        using (var controller = new A1Controller(transport))
            await ThrowsAsync<InvalidDataException>(() => controller.ApplyAsync(SettingChange.Gain(4)), "Positive acknowledgement without changed state cannot report success");
        using (var transport = new FakeTransport { Silent = true })
        using (var controller = new A1Controller(transport))
        {
            using var cancellation = new CancellationTokenSource(80);
            await ThrowsAsync<OperationCanceledException>(() => controller.ReadStatusAsync(cancellation.Token), "Cancellation interrupts wait");
            Check(transport.Disposed, "Cancelled exchange closes transport to exclude late replies");
        }
        using (var transport = new FakeTransport { Fail = true })
        using (var controller = new A1Controller(transport))
            await ThrowsAsync<IOException>(() => controller.ReadStatusAsync(), "USB read failure propagates");
        using (var transport = new FakeTransport { Silent = true })
        using (var controller = new A1Controller(transport))
        {
            await ThrowsAsync<TimeoutException>(() => controller.ReadStatusAsync(), "Missing device response times out");
            Check(transport.Disposed, "Timeout closes the transport");
        }
    }

    private static partial void VerifyInterface();
    private static partial void VerifyHardwareInterface();

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Throws<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); }
        catch (TException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }

    private static byte[] Reply(A1Command command, byte[] payload)
    {
        byte[] report = new byte[64];
        report[0] = 5;
        report[1] = 3;
        report[2] = 0xBB;
        report[3] = 0xDD;
        report[4] = (byte)command;
        report[6] = (byte)payload.Length;
        payload.CopyTo(report, 7);
        report[7 + payload.Length] = 0x6E;
        return report;
    }

    private sealed class FakeTransport : IA1Transport
    {
        private readonly byte[] status = [.. ValidStatus, 0, 0, 0, 0, 0, 0];
        private int concurrentWrites;
        private int settingWrites;
        public bool RejectWrite { get; init; }
        public int RejectAfterWrites { get; init; } = int.MaxValue;
        public bool IgnoreWrite { get; init; }
        public bool Silent { get; init; }
        public bool Fail { get; init; }
        public int DelayMilliseconds { get; set; }
        public int StatusPayloadLength { get; set; } = ValidStatus.Length;
        public byte[] VersionBytes { get; init; } = [1, 0, 1, 3];
        public bool Disposed { get; private set; }
        public int MaximumConcurrentWrites { get; private set; }
        public List<A1Command> Commands { get; } = [];
        public event Action<byte[]>? ReportReceived;
        public event Action<Exception>? Failed;

        public void SetTransmitterConnected(int index, bool connected)
        {
            int offset = index switch { 0 => 0, 1 => 1, 2 => 17, 3 => 18, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
            status[offset] = connected ? (byte)1 : (byte)0;
        }
        public void SetRadioModeCode(byte code) => status[16] = code;

        public void SendFeature(byte[] report)
        {
            int active = Interlocked.Increment(ref concurrentWrites);
            MaximumConcurrentWrites = Math.Max(active, MaximumConcurrentWrites);
            try
            {
                if (!A1Protocol.TryDecodeRequest(report, out A1Frame? frame) || frame is null)
                    throw new InvalidDataException();
                Commands.Add(frame.Command);
                if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
                if (Fail) { Failed?.Invoke(new IOException("Simulated removal")); return; }
                if (Silent) return;
                ReportReceived?.Invoke(Reply((A1Command)0x0D, [4, 1, 0, 0, 0]));
                if (frame.Command == A1Command.GetStatus)
                    ReportReceived?.Invoke(Reply(frame.Command, status.AsSpan(0, StatusPayloadLength).ToArray()));
                else if (frame.Command == A1Command.GetVersion)
                    ReportReceived?.Invoke(Reply(frame.Command, [frame.Payload[0], .. VersionBytes]));
                else if (frame.Command == A1Command.GetSerialNumber)
                    ReportReceived?.Invoke(Reply(frame.Command, [frame.Payload[0], .. System.Text.Encoding.UTF8.GetBytes("TEST-SN-" + frame.Payload[0])]));
                else if (frame.Command == A1Command.GetReceiverMacAddress)
                    ReportReceived?.Invoke(Reply(frame.Command, [1, 2, 3, 4, 5, 6]));
                else if (frame.Command == A1Command.GetTransmitterMacAddress)
                    ReportReceived?.Invoke(Reply(frame.Command, [frame.Payload[0], 1, 2, 3, 4, 5, (byte)(6 + frame.Payload[0])]));
                else
                {
                    bool rejected = RejectWrite || settingWrites++ >= RejectAfterWrites;
                    if (!IgnoreWrite && !rejected)
                    {
                        if (frame.Command == A1Command.SetGain) status[8] = frame.Payload[0];
                        if (frame.Command == A1Command.SetLight) status[11] = frame.Payload[0];
                        if (frame.Command == A1Command.SetChannelMode) status[9] = frame.Payload[0];
                        if (frame.Command == A1Command.SetNoise) { status[6] = frame.Payload[0]; status[7] = frame.Payload[1]; }
                        if (frame.Command == A1Command.SetEqualizer) status[12] = frame.Payload[0];
                        if (frame.Command == A1Command.SetReverb) { status[13] = frame.Payload[0]; status[14] = frame.Payload[1]; }
                        if (frame.Command == A1Command.SetAutoOff) status[10] = frame.Payload[0];
                        if (frame.Command == A1Command.SetIdentification) status[15] = frame.Payload[0];
                        if (frame.Command == A1Command.SetMute)
                            status[frame.Payload[0] < 2 ? 4 + frame.Payload[0] : 19 + frame.Payload[0]] = frame.Payload[1];
                    }
                    ReportReceived?.Invoke(Reply(frame.Command, [rejected ? (byte)1 : (byte)0]));
                }
            }
            finally { Interlocked.Decrement(ref concurrentWrites); }
        }

        public void Dispose() => Disposed = true;
    }
}
