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

            // Also verify 96 kHz ultrasonic response (36 kHz tone) up to 48 kHz ceiling
            float[] ultra = new float[8192];
            for (int i = 0; i < 8192; i++)
                ultra[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 36000.0 * i / 96000.0));
            vis.InjectStereoSamplesForTest(ultra, ultra, 96000);
            var uFrame = vis.ComputeFrame(256, 1.0, 0.25, 5.0, 48000.0);
            if (uFrame.DominantFrequencyHz < 35900 || uFrame.DominantFrequencyHz > 36100)
                throw new InvalidOperationException($"Ultrasonic 48 kHz FFT check failed: Dom={uFrame.DominantFrequencyHz:0.0}Hz");

            Console.WriteLine($"  [PASS] 8. 8192-pt Radix-2 FFT & 256-Band (5 Hz – 48 kHz) Visualizer verified (1 kHz -> {frame.DominantFrequencyHz:0.0} Hz Band #{peakBand}/256, 36 kHz@96k -> {uFrame.DominantFrequencyHz:0.0} Hz, {frame.RmsDb:0.0} dBFS).");
            passed++;
        }

        Console.WriteLine($"All {passed}/{passed} verification checks PASSED.");
        return 0;
    }
}
