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
