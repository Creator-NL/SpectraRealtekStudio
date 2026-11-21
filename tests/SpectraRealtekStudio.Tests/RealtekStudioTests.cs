using System;
using System.IO;
using System.Linq;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;
using Xunit;

namespace SpectraRealtekStudio.Tests;

public class RealtekStudioTests
{
    [Fact]
    public void BuiltInEqPresets_ShouldContain13PresetsWith10ValidBandsEach()
    {
        Assert.Equal(13, RealtekPropertyKeys.BuiltInEqPresets.Count);
        Assert.Equal(10, RealtekPropertyKeys.EqBandFrequenciesHz.Length);
        Assert.Equal(10, RealtekPropertyKeys.EqBandLabels.Length);

        foreach (var preset in RealtekPropertyKeys.BuiltInEqPresets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.True(preset.IsBuiltIn);
            Assert.Equal(10, preset.BandsDb.Length);
            foreach (double bandDb in preset.BandsDb)
            {
                Assert.InRange(bandDb, -12.0, 12.0);
            }
        }
    }

    [Fact]
    public void BuiltInEnvironments_ShouldContain24IndexedAcousticSpaces()
    {
        Assert.Equal(24, RealtekPropertyKeys.BuiltInEnvironments.Count);
        for (int i = 0; i < RealtekPropertyKeys.BuiltInEnvironments.Count; i++)
        {
            Assert.Equal(i, RealtekPropertyKeys.BuiltInEnvironments[i].Index);
            Assert.False(string.IsNullOrWhiteSpace(RealtekPropertyKeys.BuiltInEnvironments[i].Name));
        }
    }

    [Fact]
    public void PropertyKey_ToStringAndKnownMetadata_ShouldMatchReverseEngineeredCatalog()
    {
        string eqBlobKey = RealtekPropertyKeys.EqualizerBandsBlob.ToString();
        Assert.Equal("{4b361010-def7-43a1-a5dc-071d955b62f7},6", eqBlobKey);
        Assert.True(RealtekPropertyKeys.KnownMetadata.ContainsKey(eqBlobKey));

        var meta = RealtekPropertyKeys.KnownMetadata[eqBlobKey];
        Assert.Equal("Equalizer", meta.Category);
        Assert.Contains("10-Band", meta.Name);
    }

    [Theory]
    [InlineData(0x14, 0x01015010u, "Rear Panel", "Lime Green (Front Out)", "Front Speaker Out (L/R)", true)]
    [InlineData(0x18, 0x01A15850u, "Rear Panel", "Pink (Rear Mic In)", "Microphone In", true)]
    [InlineData(0x1A, 0x0181505Fu, "Rear Panel", "Light Blue (Line In)", "Line In", true)]
    [InlineData(0x1B, 0x02214020u, "Front Panel", "Lime Green (Front Headphone)", "Headphone Out", true)]
    [InlineData(0x1E, 0x41454130u, "Rear Panel", "Green", "SPDIF Digital Out", false)]
    public void DecodeHdaPinConfig_ShouldAccuratelyDecodeAlc892PinVerbs(
        byte widgetId,
        uint rawPinConfig,
        string expectedLocation,
        string expectedColor,
        string expectedRole,
        bool expectedPhysicalJack)
    {
        var pin = DriverHardwareService.DecodeHdaPinConfig(0, widgetId, rawPinConfig);

        Assert.Equal(widgetId, pin.PinWidgetId);
        Assert.Equal(rawPinConfig, pin.RawPinConfig);
        Assert.Equal(expectedLocation, pin.PanelLocation);
        Assert.Equal(expectedColor, pin.JackColorName);
        Assert.Equal(expectedRole, pin.AssignedRoleName);
        Assert.Equal(expectedPhysicalJack, pin.IsPhysicalJack);
    }

    [Fact]
    public void JackRetaskRolesAndEndpointFallback_ShouldRespectHardwareBitmasksAndNeverHardcodePlugged()
    {
        // Hardware bitmask 0x0015 on Rear Green (0x14) allows Line In (0), Headphone (2), Front Speaker (4)
        var pin14 = DriverHardwareService.DecodeHdaPinConfig(8, 0x14, 0x01015410u);
        pin14.SupportedRolesMask = 0x0015;
        var roles14 = DriverHardwareService.GetSupportedRolesForPin(pin14);
        Assert.Equal(new byte[] { 4, 2, 0 }, roles14.Select(r => r.RetaskCode).ToArray());

        // Hardware bitmask 0x00F5 on Rear Blue (0x1A) excludes Microphone In (1)
        var pin1A = DriverHardwareService.DecodeHdaPinConfig(32, 0x1A, 0x01811430u);
        pin1A.SupportedRolesMask = 0x00F5;
        var roles1A = DriverHardwareService.GetSupportedRolesForPin(pin1A);
        Assert.DoesNotContain(roles1A, r => r.RetaskCode == 1);
        Assert.Contains(roles1A, r => r.RetaskCode == 0);
        Assert.Contains(roles1A, r => r.RetaskCode == 4);

        // Endpoint-state fallback must report FALSE when Speakers and Line In are unplugged (DeviceState = 8)
        var unpluggedEndpoints = new System.Collections.Generic.List<RealtekEndpointInfo>
        {
            new() { FriendlyName = "Speakers", DataFlow = AudioEndpointFlow.Render, IsRealtek = true, DeviceState = 8 },
            new() { FriendlyName = "Line In", DataFlow = AudioEndpointFlow.Capture, IsRealtek = true, DeviceState = 8 }
        };
        Assert.False(DriverHardwareService.InferPluggedFromEndpoints(pin14, unpluggedEndpoints));
        Assert.False(DriverHardwareService.InferPluggedFromEndpoints(pin1A, unpluggedEndpoints));

        // And TRUE when Speakers / Line In become Active (DeviceState = 1)
        unpluggedEndpoints[0].DeviceState = 1;
        unpluggedEndpoints[1].DeviceState = 1;
        Assert.True(DriverHardwareService.InferPluggedFromEndpoints(pin14, unpluggedEndpoints));
        Assert.True(DriverHardwareService.InferPluggedFromEndpoints(pin1A, unpluggedEndpoints));
    }

    [Fact]
    public void UpdateCheckVersionParser_AndEndpointDiscovery_ShouldBeCleanAndAccurate()
    {
        Assert.True(UpdateCheckService.TryParseSemanticVersion("v1.0.1", out var v101));
        Assert.True(UpdateCheckService.TryParseSemanticVersion("v1.0.0", out var v100));
        Assert.True(UpdateCheckService.TryParseSemanticVersion("1.2.0-beta", out var v120));
        Assert.True(v101! > v100!);
        Assert.True(v120! > v101!);
        Assert.Equal("v1.0.1", UpdateCheckService.FormatVersion(new Version(1, 0, 1, 0)));

        // Capture discovery must never expose historical NOTPRESENT (0x00000004 / 0x10000004) ghost duplicates
        var studio = new RealtekStudioService();
        var captureEndpoints = studio.DiscoverEndpoints(true)
            .Where(e => e.DataFlow == AudioEndpointFlow.Capture)
            .ToList();
        Assert.DoesNotContain(captureEndpoints, e => (e.DeviceState & 0x00000004u) != 0);
    }

    [Fact]
    public void WasapiLoopbackAnalyzer_FftAnd256BandSpectrum_ShouldDetect1kHzSineAccurately()
    {
        using var analyzer = new WasapiLoopbackAnalyzer();
        const int sampleRate = 48000;
        const double freqHz = 1000.0;
        float[] left = new float[4096];
        float[] right = new float[4096];

        for (int i = 0; i < left.Length; i++)
        {
            float s = (float)(0.5 * Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate));
            left[i] = s;
            right[i] = s;
        }

        analyzer.InjectStereoSamplesForTest(left, right, sampleRate);
        var frame = analyzer.ComputeFrame(256, sensitivityGain: 1.0, smoothing: 0.25, minFreqHz: 5.0, maxFreqHz: 48000.0);

        Assert.Equal(256, frame.Spectrum.Length);
        Assert.Equal(256, frame.PeakHold.Length);
        Assert.True(frame.HasSignal);
        Assert.InRange(frame.DominantFrequencyHz, 985.0, 1015.0);
        Assert.InRange(frame.PhaseCorrelation, 0.98, 1.0);
        Assert.InRange(frame.RmsDb, -10.5, -8.0); // 0.5 amplitude sine = -9.03 dBFS

        // Peak band in 5 Hz..48 kHz log scale for 1000 Hz is around band ~148 of 256
        int peakIdx = Array.IndexOf(frame.Spectrum, frame.Spectrum.Max());
        Assert.InRange(peakIdx, 142, 154);
        Assert.True(frame.Spectrum[peakIdx] > 0.55);

        // Verify 96 kHz ultrasonic response (36 kHz tone) up to 48 kHz ceiling
        using var hiRes = new WasapiLoopbackAnalyzer();
        float[] ultra = new float[8192];
        for (int i = 0; i < ultra.Length; i++)
            ultra[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 36000.0 * i / 96000.0));
        hiRes.InjectStereoSamplesForTest(ultra, ultra, 96000);
        var ultraFrame = hiRes.ComputeFrame(256, 1.0, 0.25, 5.0, 48000.0);
        Assert.InRange(ultraFrame.DominantFrequencyHz, 35900.0, 36100.0);
        int ultraPeakIdx = Array.IndexOf(ultraFrame.Spectrum, ultraFrame.Spectrum.Max());
        Assert.InRange(ultraPeakIdx, 245, 250);
        Assert.True(ultraFrame.Spectrum[ultraPeakIdx] > 0.55);
    }

    [Fact]
    public void PresetAndProfileService_ShouldMatchPresetsAndRoundtripJsonProfile()
    {
        var svc = new PresetAndProfileService();

        var rock = RealtekPropertyKeys.BuiltInEqPresets.First(p => p.Name == "Rock");
        Assert.Equal("Rock", svc.MatchPresetName(rock.BandsDb));

        double[] customCurve = { 3.5, 2.1, -1.4, 0.0, 1.2, 2.8, 3.3, 4.1, 2.0, 1.5 };
        Assert.Equal("Custom", svc.MatchPresetName(customCurve));

        string tempFile = Path.Combine(Path.GetTempPath(), $"spectra_test_profile_{Guid.NewGuid():N}.json");
        try
        {
            var export = new StudioProfileExport
            {
                ProfileName = "UnitTestStudioProfile",
                CodecHardwareId = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0892",
                DriverVersion = "6.0.1.8186",
                PlaybackState = new RealtekPlaybackState
                {
                    EqualizerEnabled = true,
                    EqualizerBandsDb = customCurve,
                    LoudnessEqualizationEnabled = true,
                    PitchShiftSemitones = -2
                }
            };

            svc.ExportProfileToFile(tempFile, export);
            var imported = svc.ImportProfileFromFile(tempFile);

            Assert.NotNull(imported);
            Assert.Equal("UnitTestStudioProfile", imported!.ProfileName);
            Assert.NotNull(imported.PlaybackState);
            Assert.True(imported.PlaybackState!.EqualizerEnabled);
            Assert.True(imported.PlaybackState.LoudnessEqualizationEnabled);
            Assert.Equal(-2, imported.PlaybackState.PitchShiftSemitones);
            Assert.Equal(customCurve, imported.PlaybackState.EqualizerBandsDb);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void LiveRealtekEndpoint_ShouldDiscoverAndRoundtrip10BandEqualizerNonAdmin()
    {
        var studio = new RealtekStudioService();
        var endpoints = studio.DiscoverEndpoints(true);

        var speakers = endpoints.FirstOrDefault(e => e.IsRealtek && e.DataFlow == AudioEndpointFlow.Render && e.IsActive);
        if (speakers == null)
        {
            // Headless CI runners (e.g., GitHub Actions windows-latest) do not attach a physical Realtek HDA codec.
            return;
        }

        var original = studio.ReadPlaybackState(speakers.EndpointId);
        try
        {
            double[] testBands = { 4.2, 3.1, 1.5, -0.8, -2.4, -1.0, 0.7, 2.3, 3.8, 5.1 };
            bool applied = studio.ApplyEqualizer(speakers.EndpointId, true, testBands);
            Assert.True(applied);

            var readBack = studio.ReadPlaybackState(speakers.EndpointId);
            Assert.True(readBack.EqualizerEnabled);
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(testBands[i], readBack.EqualizerBandsDb[i], 1);
            }

            var props = studio.InspectEndpointProperties(speakers);
            Assert.Contains(props, p => p.KeyString.Equals(RealtekPropertyKeys.EqualizerBandsBlob.ToString(), StringComparison.OrdinalIgnoreCase)
                                     && p.DecodedValue.Contains("31 Hz=+4.2dB", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Restore original EQ state
            studio.ApplyEqualizer(speakers.EndpointId, original.EqualizerEnabled, original.EqualizerBandsDb);
        }
    }
}
