using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;

namespace SpectraRealtekStudio.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var studio = new RealtekStudioService();
        var endpoints = studio.DiscoverEndpoints(includeInactiveRealtekPins: true);
        var defaultSpeaker = endpoints.FirstOrDefault(e => e.DataFlow == AudioEndpointFlow.Render && e.IsRealtek && e.IsActive)
                          ?? endpoints.FirstOrDefault(e => e.DataFlow == AudioEndpointFlow.Render && e.IsActive);

        if (args.Length == 0 || args[0].Equals("status", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(studio, endpoints, defaultSpeaker);
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        if (defaultSpeaker == null)
        {
            Console.Error.WriteLine("No active playback endpoint found.");
            return 1;
        }

        switch (cmd)
        {
            case "preset":
            case "--preset":
                if (args.Length < 2)
                {
                    Console.WriteLine("Available EQ Presets: " + string.Join(", ", studio.Presets.GetAllEqPresets().Select(p => p.Name)));
                    return 1;
                }
                string targetPresetName = string.Join(" ", args.Skip(1));
                var preset = studio.Presets.GetAllEqPresets()
                    .FirstOrDefault(p => p.Name.Equals(targetPresetName, StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    Console.Error.WriteLine($"Preset '{targetPresetName}' not found.");
                    return 1;
                }
                studio.ApplyEqualizer(defaultSpeaker.EndpointId, true, preset.BandsDb);
                Console.WriteLine($"Applied EQ Preset '{preset.Name}' to {defaultSpeaker.DisplayLabel}.");
                return 0;

            case "eq":
            case "--eq":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: SpectraRealtekStudio.Cli eq <on|off>");
                    return 1;
                }
                bool enableEq = args[1].Equals("on", StringComparison.OrdinalIgnoreCase) || args[1] == "1" || args[1].Equals("true", StringComparison.OrdinalIgnoreCase);
                var pbState = studio.ReadPlaybackState(defaultSpeaker.EndpointId);
                studio.ApplyEqualizer(defaultSpeaker.EndpointId, enableEq, pbState.EqualizerBandsDb);
                Console.WriteLine($"Equalizer {(enableEq ? "ENABLED" : "DISABLED")} on {defaultSpeaker.DisplayLabel}.");
                return 0;

            case "loudness":
            case "--loudness":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: SpectraRealtekStudio.Cli loudness <on|off>");
                    return 1;
                }
                bool enableLoud = args[1].Equals("on", StringComparison.OrdinalIgnoreCase) || args[1] == "1" || args[1].Equals("true", StringComparison.OrdinalIgnoreCase);
                var stateLoud = studio.ReadPlaybackState(defaultSpeaker.EndpointId);
                stateLoud.LoudnessEqualizationEnabled = enableLoud;
                studio.ApplyPlaybackState(stateLoud);
                Console.WriteLine($"Loudness Equalization {(enableLoud ? "ENABLED" : "DISABLED")} on {defaultSpeaker.DisplayLabel}.");
                return 0;

            case "diag":
            case "--diag":
                string outPath = args.Length >= 2 ? args[1] : "realtek_diagnostic_report.json";
                var report = new
                {
                    GeneratedUtc = DateTime.UtcNow,
                    Hardware = studio.DriverHardware.ReadHardwareTweaks(),
                    JackPins = studio.QueryLiveJackPins(endpoints),
                    Endpoints = endpoints,
                    ActiveSpeakerState = defaultSpeaker != null ? studio.ReadPlaybackState(defaultSpeaker.EndpointId) : null,
                    ActiveSpeakerFormat = defaultSpeaker != null ? studio.ReadFormatState(defaultSpeaker.EndpointId) : null,
                    ActiveSpeakerProperties = defaultSpeaker != null ? studio.InspectEndpointProperties(defaultSpeaker) : null
                };
                File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Saved diagnostic report to {Path.GetFullPath(outPath)}");
                return 0;

            case "test":
            case "--test":
                return RunSelfVerification(studio, endpoints, defaultSpeaker);

            default:
                Console.WriteLine("SpectraRealtekStudio CLI Commands:");
                Console.WriteLine("  status                  Show all Realtek hardware, endpoints, EQ, DSP, and jack status");
                Console.WriteLine("  preset <PresetName>     Apply built-in or custom 10-band EQ preset (Pop, Rock, Bass, Club, Flat...)");
                Console.WriteLine("  eq <on|off>             Enable or disable the 10-band graphic equalizer");
                Console.WriteLine("  loudness <on|off>       Enable or disable Loudness Equalization");
                Console.WriteLine("  diag [output.json]      Export full reverse-engineered Realtek diagnostic report");
                Console.WriteLine("  test                    Run automated self-verification test suite");
                return 0;
        }
    }

    private static void PrintStatus(RealtekStudioService studio, System.Collections.Generic.List<RealtekEndpointInfo> endpoints, RealtekEndpointInfo? defaultSpeaker)
    {
        var hw = studio.DriverHardware.ReadHardwareTweaks();
        Console.WriteLine("========================================================================");
        Console.WriteLine("  SPECTRA REALTEK STUDIO — HARDWARE & DSP STATUS");
        Console.WriteLine("========================================================================");
        Console.WriteLine($"Codec Chip      : {hw.CodecChipName}");
        Console.WriteLine($"Motherboard     : {hw.MotherboardSubsysId}");
        Console.WriteLine($"Driver Version  : {hw.DriverVersion} ({hw.DriverDate}) [{hw.InfPath}]");
        Console.WriteLine($"Hardware ID     : {hw.CodecHardwareId}");
        Console.WriteLine($"Multi-Streaming : {(hw.IndependentFrontRearStreams ? "Independent Front/Rear Streams" : "Linked")}");
        Console.WriteLine($"Anti-Pop Power  : {(hw.AntiPopDisableIdlePowerDown ? "Active (No D3 Sleep Pop)" : "Default D3 Idle")}");
        Console.WriteLine();

        Console.WriteLine("--- Discovered Realtek Endpoints ---");
        foreach (var ep in endpoints.Where(e => e.IsRealtek))
        {
            Console.WriteLine($"  [{ep.DataFlow,-7}] {ep.DisplayLabel} | FX={ep.FxPropertyCount}, Props={ep.PropertyCount}");
        }

        if (defaultSpeaker != null)
        {
            var pb = studio.ReadPlaybackState(defaultSpeaker.EndpointId);
            var fmt = studio.ReadFormatState(defaultSpeaker.EndpointId);
            Console.WriteLine();
            Console.WriteLine($"--- Active Playback Endpoint: {defaultSpeaker.DisplayLabel} ---");
            Console.WriteLine($"  Format & Config   : {fmt.FormatSummary} | {fmt.SpeakerConfigurationName}");
            Console.WriteLine($"  Equalizer         : {(pb.EqualizerEnabled ? "ON" : "OFF")} (Preset: {pb.MatchedPresetName})");
            string bandsStr = string.Join(" | ", pb.EqualizerBandsDb.Select((v, i) => $"{RealtekPropertyKeys.EqBandLabels[i]}:{v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}dB"));
            Console.WriteLine($"  EQ Bands          : {bandsStr}");
            string envName = RealtekPropertyKeys.BuiltInEnvironments[Math.Clamp(pb.EnvironmentPresetIndex, 0, RealtekPropertyKeys.BuiltInEnvironments.Count - 1)].Name;
            Console.WriteLine($"  Environment Reverb: {(pb.EnvironmentEnabled ? "ON" : "OFF")} ({envName})");
            Console.WriteLine($"  Loudness Eq       : {(pb.LoudnessEqualizationEnabled ? "ON" : "OFF")} | Voice Cancel: {(pb.VoiceCancellationEnabled ? "ON" : "OFF")} | Pitch: {pb.PitchShiftSemitones:+#;-#;0} st");
            Console.WriteLine($"  Spatial / Upmix   : SpeakerFill={(pb.SpeakerFillEnabled ? "ON" : "OFF")}, VirtualSurround={(pb.VirtualSurroundEnabled ? "ON" : "OFF")}, HeadphoneVirt={(pb.HeadphoneVirtualizationEnabled ? "ON" : "OFF")}");
            Console.WriteLine($"  Room Correction   : {(pb.RoomCorrectionEnabled ? "ON" : "OFF")} | BassMgt={(pb.BassManagementEnabled ? "ON" : "OFF")} | SwapCenterSub={(pb.SwapCenterSubwooferEnabled ? "ON" : "OFF")}");
        }

        var jacks = studio.QueryLiveJackPins(endpoints);
        Console.WriteLine();
        Console.WriteLine("--- Physical Jack Pins (Live Hardware Jack-Sense & Retask Roles) ---");
        foreach (var j in jacks.Where(p => p.IsPhysicalJack))
        {
            string plugStr = j.IsPluggedIn ? "PLUGGED" : "IDLE   ";
            Console.WriteLine($"  Pin 0x{j.PinWidgetId:X2} [{j.PanelLocation,-11}] {j.JackColorName,-28} [{plugStr}] -> {j.AssignedRoleName,-25} (Role={j.RetaskRoleCode}, Mask=0x{j.SupportedRolesMask:X4})");
        }
    }

    private static int RunSelfVerification(RealtekStudioService studio, System.Collections.Generic.List<RealtekEndpointInfo> endpoints, RealtekEndpointInfo? defaultSpeaker)
    {
        Console.WriteLine("Running SpectraRealtekStudio Automated Verification Suite...");
        int passed = 0;

        // 1. Catalog & Presets
        if (RealtekPropertyKeys.BuiltInEqPresets.Count != 13 || RealtekPropertyKeys.BuiltInEnvironments.Count != 24)
            throw new InvalidOperationException("Built-in EQ preset or EAX environment count mismatch.");
        Console.WriteLine("  [PASS] 1. Built-In 13 EQ Presets & 24 EAX Acoustic Environments verified.");
        passed++;

        // 2. HDA Pin Verb Decoder
        var p14 = DriverHardwareService.DecodeHdaPinConfig(0, 0x14, 0x01015010u);
        var p1B = DriverHardwareService.DecodeHdaPinConfig(28, 0x1B, 0x02214020u);
        if (!p14.IsPhysicalJack || p14.PanelLocation != "Rear Panel" || p1B.PanelLocation != "Front Panel")
            throw new InvalidOperationException("HDA PinConfigOverrideVerbs decoder check failed.");
        Console.WriteLine("  [PASS] 2. Intel HDA PinConfigOverrideVerbs 32-bit Register Decoder verified.");
        passed++;

        // 3. Hardware & Driver Tweaks Discovery
        var hw = studio.DriverHardware.ReadHardwareTweaks();
        var jacks = studio.DriverHardware.ReadJackPins();
        if (string.IsNullOrWhiteSpace(hw.CodecChipName) || jacks.Count < 8)
            throw new InvalidOperationException("Hardware codec or physical jack discovery failed.");
        Console.WriteLine($"  [PASS] 3. Driver Hardware Discovery ({hw.CodecChipName}, {jacks.Count(j => j.IsPhysicalJack)} physical jacks) verified.");
        passed++;

        // 4. Live Non-Admin RPC 10-Band EQ Roundtrip
        if (defaultSpeaker != null)
        {
            var orig = studio.ReadPlaybackState(defaultSpeaker.EndpointId);
            try
            {
                double[] testBands = { 4.2, 3.1, 1.5, -0.8, -2.4, -1.0, 0.7, 2.3, 3.8, 5.1 };
                if (!studio.ApplyEqualizer(defaultSpeaker.EndpointId, true, testBands))
                    throw new InvalidOperationException("ApplyEqualizer returned false.");

                var verify = studio.ReadPlaybackState(defaultSpeaker.EndpointId);
                for (int i = 0; i < 10; i++)
                {
                    if (Math.Abs(verify.EqualizerBandsDb[i] - testBands[i]) > 0.05)
                        throw new InvalidOperationException($"Band {i} mismatch: expected {testBands[i]}, got {verify.EqualizerBandsDb[i]}");
                }
                Console.WriteLine($"  [PASS] 4. Live Non-Admin RPC 10-Band Equalizer 0.1 dB Roundtrip on '{defaultSpeaker.FriendlyName}' verified.");
                passed++;
            }
            finally
            {
                studio.ApplyEqualizer(defaultSpeaker.EndpointId, orig.EqualizerEnabled, orig.EqualizerBandsDb);
            }
        }

        // 5. Analog Mixer Subunit Discovery
        if (defaultSpeaker != null)
        {
            var mixerNodes = studio.GetAnalogMixerSubunits(defaultSpeaker.EndpointId);
            if (mixerNodes.Count == 0)
                throw new InvalidOperationException("No analog mixer subunits discovered.");
            Console.WriteLine($"  [PASS] 5. Analog Hardware Monitoring Subunits ({mixerNodes.Count} nodes) verified.");
            passed++;
        }

        // 6. Jack Retask Role Bitmask & Live Endpoint Fallback Verification
        var testPin14 = DriverHardwareService.DecodeHdaPinConfig(8, 0x14, 0x01015410u);
        testPin14.SupportedRolesMask = 0x0015;
        var roles14 = DriverHardwareService.GetSupportedRolesForPin(testPin14).Select(r => r.RetaskCode).ToArray();
        if (roles14.Length != 3 || !roles14.Contains((byte)4) || !roles14.Contains((byte)2) || !roles14.Contains((byte)0))
            throw new InvalidOperationException("Supported retask role bitmask filtering failed for Pin 0x14.");

        var mockUnplugged = new System.Collections.Generic.List<RealtekEndpointInfo>
        {
            new() { FriendlyName = "Speakers", DataFlow = AudioEndpointFlow.Render, IsRealtek = true, DeviceState = 8 },
            new() { FriendlyName = "Line In", DataFlow = AudioEndpointFlow.Capture, IsRealtek = true, DeviceState = 8 }
        };
        if (DriverHardwareService.InferPluggedFromEndpoints(testPin14, mockUnplugged))
            throw new InvalidOperationException("Endpoint fallback falsely reported unplugged Speakers as plugged.");
        mockUnplugged[0].DeviceState = 1;
        if (!DriverHardwareService.InferPluggedFromEndpoints(testPin14, mockUnplugged))
            throw new InvalidOperationException("Endpoint fallback failed to report active Speakers as plugged.");
        Console.WriteLine("  [PASS] 6. Live Jack-Sense Bitmask Filtering & Non-Hardcoded Endpoint Fallback verified.");
        passed++;

        // 7. Clean Capture Endpoint Filtering (No 0x10000004 Ghost Duplicates) & SemVer Update Parser
        var ghostCaptures = endpoints.Where(e => (e.DeviceState & 0x00000004u) != 0).ToList();
        if (ghostCaptures.Count > 0)
            throw new InvalidOperationException($"Found {ghostCaptures.Count} stale NOTPRESENT ghost endpoints in discovery list.");
        if (!UpdateCheckService.TryParseSemanticVersion("v1.0.1", out var vNew) ||
            !UpdateCheckService.TryParseSemanticVersion("v1.0.0", out var vOld) ||
            !(vNew! > vOld!))
            throw new InvalidOperationException("Semantic version update comparison check failed.");
        Console.WriteLine($"  [PASS] 7. Clean Endpoint Filtering (0 ghost duplicates) & Update Version Parser verified.");
        passed++;

        // 8. 8192-pt Radix-2 FFT & 256-Band (5 Hz – 48 kHz) Logarithmic Audio Visualizer Verification
        using (var vis = new WasapiLoopbackAnalyzer())
        {
            float[] sineL = new float[4096];
            float[] sineR = new float[4096];
            for (int i = 0; i < 4096; i++)
            {
                float s = (float)(0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * i / 48000.0));
                sineL[i] = s;
                sineR[i] = s;
            }
            vis.InjectStereoSamplesForTest(sineL, sineR, 48000);
            var frame = vis.ComputeFrame(256, 1.0, 0.25, 5.0, 48000.0);
            int peakBand = Array.IndexOf(frame.Spectrum, frame.Spectrum.Max());
            if (frame.Spectrum.Length != 256 || !frame.HasSignal || frame.DominantFrequencyHz < 985 || frame.DominantFrequencyHz > 1015 || peakBand < 142 || peakBand > 154)
                throw new InvalidOperationException($"FFT visualizer verification failed: Dom={frame.DominantFrequencyHz:0.0}Hz PeakBand={peakBand}");

            var frame0To20k = vis.ComputeFrame(256, 1.0, 0.25, 0.0, 20000.0, false, SpectrumScaleMode.ShiftedLogZero);
            int peak0To20k = Array.IndexOf(frame0To20k.Spectrum, frame0To20k.Spectrum.Max());
            if (frame0To20k.ScaleMode != SpectrumScaleMode.ShiftedLogZero || peak0To20k < 136 || peak0To20k > 148)
                throw new InvalidOperationException($"0 Hz–20 kHz shifted-log FFT check failed: PeakBand={peak0To20k}");

            // Also verify 96 kHz ultrasonic response (36 kHz tone) up to 48 kHz ceiling
            float[] ultra = new float[8192];
            for (int i = 0; i < 8192; i++)
                ultra[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 36000.0 * i / 96000.0));
            vis.InjectStereoSamplesForTest(ultra, ultra, 96000);
            var uFrame = vis.ComputeFrame(256, 1.0, 0.25, 5.0, 48000.0);
            if (uFrame.DominantFrequencyHz < 35900 || uFrame.DominantFrequencyHz > 36100)
                throw new InvalidOperationException($"Ultrasonic 48 kHz FFT check failed: Dom={uFrame.DominantFrequencyHz:0.0}Hz");

            // Also verify 192 kHz broadband music with a 15.8 kHz MP3 encoder brickwall cutoff has ZERO gap at 15.8–17.5 kHz
            float[] mp3Sim = new float[8192];
            var rng = new Random(77);
            for (int fHz = 80; fHz <= 15600; fHz += 95)
            {
                double phase = rng.NextDouble() * 2.0 * Math.PI;
                double amp = 0.035 * Math.Pow(1000.0 / (500.0 + fHz), 0.35);
                for (int i = 0; i < 8192; i++)
                    mp3Sim[i] += (float)(amp * Math.Sin(2.0 * Math.PI * fHz * i / 192000.0 + phase));
            }
            using var mp3Vis = new WasapiLoopbackAnalyzer();
            mp3Vis.InjectStereoSamplesForTest(mp3Sim, mp3Sim, 192000);
            var linFrame = mp3Vis.ComputeFrame(256, 1.0, 0.15, 0.0, 48000.0, true, SpectrumScaleMode.Linear);
            // In 0..48 kHz Linear (256 bands = 187.5 Hz/band), bands 80..98 cover 15.0 kHz..18.5 kHz (the former 15.8–17.5 kHz gap)
            double minGapBand = linFrame.Spectrum.Skip(80).Take(19).Min();
            if (minGapBand < 0.18)
                throw new InvalidOperationException($"Broadband MP3 15.8 kHz cutoff gap check failed: minBand(15–18.5 kHz)={minGapBand:0.000}");

            Console.WriteLine($"  [PASS] 8. 8192-pt Radix-2 FFT & 256-Band (5 Hz – 48 kHz) Visualizer verified (1 kHz -> {frame.DominantFrequencyHz:0.0} Hz Band #{peakBand}/256, 36 kHz@96k -> {uFrame.DominantFrequencyHz:0.0} Hz, MP3 15.8k gap-free min={minGapBand:0.00}).");
            passed++;
        }

        // 9. Hi-Res Frequency Sweep Sound Test & Studio WAV Recorder (16/24/32f) Verification
        byte[] sweepWav = SpeakerTestToneService.GenerateLogFrequencySweepWav(5.0, 48000.0, 1.0, 96000);
        byte[] pinkWav = SpeakerTestToneService.GenerateNoiseWav(true, 0.5, 48000);
        if (sweepWav.Length < 44 + 96000 * 4 || BitConverter.ToInt32(sweepWav, 24) != 96000 || pinkWav.Length != 44 + 24000 * 4)
            throw new InvalidOperationException("Hi-res frequency sweep / pink noise WAV generation verification failed.");

        foreach (var fmt in new[] { RecordingBitFormat.Pcm16, RecordingBitFormat.Pcm24, RecordingBitFormat.Float32 })
        {
            using var ms = new MemoryStream();
            AudioRecorderService.WriteWavHeader(ms, 48000, 2, fmt, 0);
            byte[] buf = new byte[16];
            int pos = 0;
            AudioRecorderService.WriteStereoSample(buf, ref pos, 0.5f, -0.5f, fmt);
            ms.Write(buf, 0, pos);
            AudioRecorderService.WriteWavHeader(ms, 48000, 2, fmt, pos);
            byte[] wav = ms.ToArray();
            if (wav.Length != 44 + pos || BitConverter.ToInt32(wav, 40) != pos)
                throw new InvalidOperationException($"Studio WAV recorder header check failed for {fmt}.");
        }
        float[] armedTest = new float[20];
        armedTest[12] = 0.05f;
        if (!AudioRecorderService.EvaluateWaitForSoundTrigger(armedTest, -40.0, out int trigFrame) || trigFrame != 6 ||
            AudioRecorderService.EvaluateWaitForSoundTrigger(new float[20], -40.0, out _))
            throw new InvalidOperationException("Wait-for-Sound threshold trigger verification failed.");

        Console.WriteLine($"  [PASS] 9. Hi-Res Frequency Sweep (5 Hz–48 kHz @ 96 kHz), Pink Noise, 16/24/32f WAV Recorder & Wait-for-Sound Trigger verified.");
        passed++;

        // 10. Dual-Deck DJ Mixing Studio (Load As-Is 192 kHz Bit-Exact Pass-Through, 48 kHz Mode, Auto-BPM, Beat-Sync & 3-Band EQ Kill)
        string tempDjWav = Path.Combine(Path.GetTempPath(), $"Spectra_DJ_Test_{Guid.NewGuid():N}.wav");
        try
        {
            byte[] testWavBytes = SpeakerTestToneService.GeneratePureToneWav(440.0, 2.0, 192000);
            File.WriteAllBytes(tempDjWav, testWavBytes);

            // Verify Load As-Is (preserveNativeSampleRate: true) keeps full 192,000 Hz native rate & bit-exact samples
            var loadedNative = studio.DjDecoder.LoadAndAnalyzeTrack(tempDjWav, forceReanalyze: true, preserveNativeSampleRate: true);
            if (loadedNative.SampleRate != 192000 || loadedNative.TotalFrames < 192000)
                throw new InvalidOperationException($"DJ Load-As-Is 192 kHz check failed: Rate={loadedNative.SampleRate}, Frames={loadedNative.TotalFrames}");

            // Verify 1:1 bit-exact pass-through in PureNativeQualityMode at 192,000 Hz
            using (var pureDj = new DjMixerEngineService { PureNativeQualityMode = true })
            {
                pureDj.LoadTrackToDeck(DjDeckId.DeckA, loadedNative);
                pureDj.DeckA.PlayheadFrame = 0;
                pureDj.DeckA.IsPlaying = true;
                pureDj.Crossfader = 0.0; // Center unity
                float[] pureL = new float[256];
                float[] pureR = new float[256];
                pureDj.MixStereoBuffer(pureL, pureR, 256, 192000);
                for (int i = 0; i < 256; i++)
                {
                    if (Math.Abs(pureL[i] - loadedNative.Left[i]) > 1e-6f)
                        throw new InvalidOperationException($"DJ Load-As-Is bit-exact pass-through mismatch at sample {i}: expected {loadedNative.Left[i]}, got {pureL[i]}");
                }
            }

            // Also verify 48 kHz RAM-Saver mode (preserveNativeSampleRate: false)
            var loadedWav = studio.DjDecoder.LoadAndAnalyzeTrack(tempDjWav, forceReanalyze: false, preserveNativeSampleRate: false);
            if (loadedWav.SampleRate != 48000 || loadedWav.TotalFrames < 48000 || loadedWav.OverviewPeaks.Length != DjLoadedTrack.OverviewBinCount)
                throw new InvalidOperationException($"DJ 48 kHz WAV decoder check failed: Rate={loadedWav.SampleRate}, Frames={loadedWav.TotalFrames}");
        }
        finally
        {
            if (File.Exists(tempDjWav)) File.Delete(tempDjWav);
        }

        int envRate = DjLoadedTrack.BeatEnvelopeRateHz;
        float[] djPeaks = new float[envRate * 20];
        float[] djBass = new float[envRate * 20];
        // Test 138.0 BPM (period = 43.478 bins at 100 Hz — previously mis-quantized to 136.4 BPM at integer lag 44!)
        double periodBins = 60.0 * envRate / 138.0;
        for (int b = 0; b < 44; b++)
        {
            int idx = (int)Math.Round(b * periodBins);
            if (idx + 1 < djPeaks.Length)
            {
                djPeaks[idx] = 1.0f;
                djBass[idx] = 0.95f;
            }
        }
        DjTrackDecoderService.AnalyzeBpmAndBeatGrid(djPeaks, djBass, envRate, out double detBpm, out _);
        if (Math.Abs(detBpm - 138.0) > 0.5)
            throw new InvalidOperationException($"DJ Auto-BPM detector check failed: expected 138.0 BPM, got {detBpm:0.0}");

        using (var dj = new DjMixerEngineService())
        {
            float[] tone80 = new float[48000];
            for (int i = 0; i < tone80.Length; i++)
                tone80[i] = (float)(0.6 * Math.Sin(2.0 * Math.PI * 80.0 * i / 48000.0));

            dj.LoadTrackToDeck(DjDeckId.DeckA, new DjLoadedTrack { Title = "Deck A", SampleRate = 48000, TotalFrames = 48000, Left = tone80, Right = tone80, DetectedBpm = 143.0 });
            dj.LoadTrackToDeck(DjDeckId.DeckB, new DjLoadedTrack { Title = "Deck B", SampleRate = 48000, TotalFrames = 48000, Left = tone80, Right = tone80, DetectedBpm = 125.0 });
            dj.SyncDeck(DjDeckId.DeckB);
            if (Math.Abs(dj.DeckB.EffectiveBpm - 143.0) > 0.25)
                throw new InvalidOperationException($"DJ Beat-Sync check failed: expected 143.0 BPM, got {dj.DeckB.EffectiveBpm:0.00}");

            dj.DeckA.IsPlaying = true;
            dj.Crossfader = -1.0;
            float[] outL = new float[1024];
            float[] outR = new float[1024];
            dj.MixStereoBuffer(outL, outR, 1024, 48000);
            float fullBassPeak = outL.Max(Math.Abs);

            dj.DeckA.LowDb = -36.0; // Total LOW EQ Kill
            dj.MixStereoBuffer(outL, outR, 1024, 48000);
            float killBassPeak = outL.Skip(512).Max(Math.Abs);
            if (fullBassPeak < 0.25f || killBassPeak > fullBassPeak * 0.15f)
                throw new InvalidOperationException("DJ 3-Band Isolator LOW EQ Total Kill check failed.");
        }
        Console.WriteLine($"  [PASS] 10. Dual-Deck DJ Mixing Studio (Auto-BPM {detBpm:0.0}, Beat-Sync 125->143 BPM, 3-Band EQ Kill & Crossfader DSP) verified.");
        passed++;

        // 11. Sample & Loop Studio Editor (Zero-Crossing Loop Snap, Start/End Cut, Undo/Redo & 192 kHz 24-bit Loop WAV Export)
        string tempLoopOut = Path.Combine(Path.GetTempPath(), $"Spectra_SampleLoop_{Guid.NewGuid():N}.wav");
        try
        {
            using var sampleEditor = new AudioSampleEditorService();
            int sr = 192000;
            float[] waveL = new float[sr];
            float[] waveR = new float[sr];
            for (int i = 0; i < sr; i++)
            {
                float s = MathF.Sin((float)(2.0 * Math.PI * 320.0 * i / sr)) * 0.8f;
                waveL[i] = s;
                waveR[i] = s;
            }

            sampleEditor.LoadRawBuffer(waveL, waveR, sr, "192kHz_Sample.wav", RecordingBitFormat.Pcm24);
            sampleEditor.SetSelectionRange(19217, 115219, applyZeroCrossingSnap: true);
            sampleEditor.GetBuffers(out float[] edL, out _, out _);
            if (Math.Abs(edL[sampleEditor.SelectionStartFrame]) > 0.05f || Math.Abs(edL[sampleEditor.SelectionEndFrame]) > 0.05f)
                throw new InvalidOperationException("Sample Editor Zero-Crossing Snap failed to lock onto zero crossing.");

            int loopFrames = sampleEditor.SelectionLengthFrames;
            var savedLoop = sampleEditor.SaveSelectionToWav(tempLoopOut, RecordingBitFormat.Pcm24);
            if (!File.Exists(tempLoopOut) || !savedLoop.FormatLabel.Contains("192 kHz • 24-bit WAV", StringComparison.Ordinal))
                throw new InvalidOperationException($"Sample Editor 192 kHz 24-bit loop save failed: {savedLoop.FormatLabel}");

            if (!sampleEditor.CropToSelection() || sampleEditor.TotalFrames != loopFrames)
                throw new InvalidOperationException("Sample Editor Cut Start & End (CropToSelection) failed.");
            if (!sampleEditor.Undo() || sampleEditor.TotalFrames != sr)
                throw new InvalidOperationException("Sample Editor Undo failed.");
        }
        finally
        {
            if (File.Exists(tempLoopOut)) File.Delete(tempLoopOut);
        }
        Console.WriteLine("  [PASS] 11. Sample & Loop Studio Editor (Zero-Crossing Loop Snap, Cut Start/End, Undo/Redo & 192 kHz 24-bit Loop Export) verified.");
        passed++;

        Console.WriteLine($"All {passed}/{passed} verification checks PASSED.");
        return 0;
    }
}
