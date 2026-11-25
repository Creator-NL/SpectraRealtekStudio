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

        // Verify default 0 Hz..20 kHz ShiftedLogZero range places 1 kHz accurately around band ~142/256
        var zeroTo20k = analyzer.ComputeFrame(256, 1.0, 0.25, 0.0, 20000.0, linearScale: false, explicitScaleMode: SpectrumScaleMode.ShiftedLogZero);
        int zPeakIdx = Array.IndexOf(zeroTo20k.Spectrum, zeroTo20k.Spectrum.Max());
        Assert.Equal(SpectrumScaleMode.ShiftedLogZero, zeroTo20k.ScaleMode);
        Assert.InRange(zPeakIdx, 136, 148);

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

        // Verify broadband music with a 15.8 kHz MP3 encoder cutoff has zero gap across 15.0–18.5 kHz on 0–48 kHz Linear
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
        double minGapBand = linFrame.Spectrum.Skip(80).Take(19).Min();
        Assert.True(minGapBand > 0.18);
    }

    [Fact]
    public void SoundTestGenerators_AndAudioRecorderWavSerializer_ShouldProduceValidHiResWavStreams()
    {
        // Verify 5 Hz -> 48 kHz Hi-Res 96 kHz Log Frequency Sweep WAV
        byte[] sweepWav = SpeakerTestToneService.GenerateLogFrequencySweepWav(5.0, 48000.0, 1.0, 96000);
        Assert.True(sweepWav.Length > 44 + 96000 * 4 - 64);
        Assert.Equal((byte)'R', sweepWav[0]);
        Assert.Equal((byte)'I', sweepWav[1]);
        Assert.Equal((byte)'F', sweepWav[2]);
        Assert.Equal((byte)'F', sweepWav[3]);
        Assert.Equal(96000, BitConverter.ToInt32(sweepWav, 24));

        // Verify Pink Noise WAV synthesis
        byte[] pinkWav = SpeakerTestToneService.GenerateNoiseWav(true, 0.5, 48000);
        Assert.Equal(44 + 24000 * 4, pinkWav.Length);

        // Verify AudioRecorderService 16-bit PCM, 24-bit PCM, and 32-bit IEEE Float WAV serialization
        foreach (var fmt in new[] { RecordingBitFormat.Pcm16, RecordingBitFormat.Pcm24, RecordingBitFormat.Float32 })
        {
            using var ms = new MemoryStream();
            AudioRecorderService.WriteWavHeader(ms, 48000, 2, fmt, 0);
            byte[] sampleBuf = new byte[16];
            int pos = 0;
            AudioRecorderService.WriteStereoSample(sampleBuf, ref pos, 0.5f, -0.5f, fmt);
            ms.Write(sampleBuf, 0, pos);
            AudioRecorderService.WriteWavHeader(ms, 48000, 2, fmt, pos);

            byte[] bytes = ms.ToArray();
            Assert.Equal(44 + pos, bytes.Length);
            short expectedTag = fmt == RecordingBitFormat.Float32 ? (short)3 : (short)1;
            Assert.Equal(expectedTag, BitConverter.ToInt16(bytes, 20));
            Assert.Equal((short)fmt, BitConverter.ToInt16(bytes, 34));
            Assert.Equal(pos, BitConverter.ToInt32(bytes, 40));
        }

        // Verify Wait-for-Sound threshold trigger ignores silence and fires on exact first frame above -40 dB
        float[] armedBuf = new float[20]; // 10 stereo frames
        armedBuf[12] = 0.05f; // Frame #6 crosses -40 dB (0.01)
        Assert.True(AudioRecorderService.EvaluateWaitForSoundTrigger(armedBuf, -40.0, out int trigIdx));
        Assert.Equal(6, trigIdx);
        Assert.False(AudioRecorderService.EvaluateWaitForSoundTrigger(new float[20], -40.0, out _));
    }

    [Fact]
    public void DjMixerEngine_AndAutoBpmKeyAnalyzer_ShouldDetect128BpmAndSyncDecksAndApplyIsolatorEq()
    {
        // 0. Verify full end-to-end WAV file decoding in both Load As-Is (192 kHz bit-exact) and 48 kHz RAM-Saver modes
        string tempDjWav = Path.Combine(Path.GetTempPath(), $"Spectra_DJ_UnitTest_{Guid.NewGuid():N}.wav");
        try
        {
            byte[] wavBytes = SpeakerTestToneService.GeneratePureToneWav(440.0, 2.0, 192000);
            File.WriteAllBytes(tempDjWav, wavBytes);
            var decoder = new DjTrackDecoderService();

            var nativeTrack = decoder.LoadAndAnalyzeTrack(tempDjWav, forceReanalyze: true, preserveNativeSampleRate: true);
            Assert.Equal(192000, nativeTrack.SampleRate);
            Assert.True(nativeTrack.TotalFrames >= 380000);

            using var pureDj = new DjMixerEngineService { PureNativeQualityMode = true };
            pureDj.LoadTrackToDeck(DjDeckId.DeckA, nativeTrack);
            pureDj.DeckA.PlayheadFrame = 0;
            pureDj.DeckA.IsPlaying = true;
            float[] pureL = new float[256];
            float[] pureR = new float[256];
            pureDj.MixStereoBuffer(pureL, pureR, 256, 192000);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(nativeTrack.Left[i], pureL[i], 6);
            }

            var decoded48 = decoder.LoadAndAnalyzeTrack(tempDjWav, forceReanalyze: false, preserveNativeSampleRate: false);
            Assert.Equal(48000, decoded48.SampleRate);
            Assert.True(decoded48.TotalFrames >= 90000);
            Assert.Equal(DjLoadedTrack.OverviewBinCount, decoded48.OverviewPeaks.Length);
        }
        finally
        {
            if (File.Exists(tempDjWav)) File.Delete(tempDjWav);
        }

        // 1. Verify 138.0 BPM kick-drum onset detector (fractional 43.478-bin period at 100 Hz, immune to integer-lag quantization)
        int envRate = DjLoadedTrack.BeatEnvelopeRateHz; // 100 Hz
        int totalBins = envRate * 20; // 20 seconds
        float[] beatPeaks = new float[totalBins];
        float[] beatBass = new float[totalBins];
        double periodBins = 60.0 * envRate / 138.0;

        for (int beat = 0; beat < 44; beat++)
        {
            int idx = (int)Math.Round(beat * periodBins);
            if (idx + 2 < totalBins)
            {
                beatPeaks[idx] = 1.0f;
                beatPeaks[idx + 1] = 0.65f;
                beatBass[idx] = 0.95f;
                beatBass[idx + 1] = 0.85f;
            }
        }

        DjTrackDecoderService.AnalyzeBpmAndBeatGrid(beatPeaks, beatBass, envRate, out double detectedBpm, out _);
        Assert.InRange(detectedBpm, 137.5, 138.5);

        // 2. Verify Dual-Deck Beat Sync, 3-Band EQ Kill & Crossfader DSP mixing
        using var dj = new DjMixerEngineService();
        int sr = 48000;
        float[] subBass = new float[sr];
        for (int i = 0; i < sr; i++)
            subBass[i] = (float)(0.6 * Math.Sin(2.0 * Math.PI * 80.0 * i / sr)); // 80 Hz sub-bass tone

        var trackA = new DjLoadedTrack
        {
            Title = "Club Anthem A",
            Artist = "Spectra DJ",
            SampleRate = sr,
            TotalFrames = sr,
            Left = subBass,
            Right = subBass,
            DetectedBpm = 140.0,
            MusicalKey = "Am"
        };
        var trackB = new DjLoadedTrack
        {
            Title = "Techno Groove B",
            Artist = "Spectra DJ",
            SampleRate = sr,
            TotalFrames = sr,
            Left = subBass,
            Right = subBass,
            DetectedBpm = 125.0,
            MusicalKey = "Gm"
        };

        dj.LoadTrackToDeck(DjDeckId.DeckA, trackA);
        dj.LoadTrackToDeck(DjDeckId.DeckB, trackB);
        dj.SyncDeck(DjDeckId.DeckB);
        Assert.Equal(140.0, dj.DeckB.EffectiveBpm, 1);

        // Play Deck A with normal EQ vs Total LOW Kill (-36 dB)
        dj.DeckA.IsPlaying = true;
        dj.Crossfader = -1.0; // Full Deck A
        float[] outL = new float[1024];
        float[] outR = new float[1024];
        dj.MixStereoBuffer(outL, outR, 1024, sr);
        float normalPeak = outL.Max(Math.Abs);
        Assert.True(normalPeak > 0.25f);

        dj.DeckA.LowDb = -36.0; // Total LOW EQ Kill
        dj.MixStereoBuffer(outL, outR, 1024, sr);
        float killedPeak = outL.Skip(512).Max(Math.Abs);
        Assert.True(killedPeak < normalPeak * 0.15f);
    }

    [Fact]
    public void AudioSampleEditorService_ShouldSnapZeroCrossingsCropStartEndAndSaveSelectedLoopAtNativeRate()
    {
        using var editor = new AudioSampleEditorService();
        int sr = 192000;
        int totalFrames = sr; // 1.0 second at 192 kHz
        float[] left = new float[totalFrames];
        float[] right = new float[totalFrames];
        for (int i = 0; i < totalFrames; i++)
        {
            float s = MathF.Sin((float)(2.0 * Math.PI * 400.0 * i / sr));
            left[i] = s * 0.75f;
            right[i] = s * 0.75f;
        }

        editor.LoadRawBuffer(left, right, sr, "192kHz_Loop_Test.wav", RecordingBitFormat.Pcm24);
        Assert.Equal(sr, editor.SampleRate);
        Assert.Equal(totalFrames, editor.TotalFrames);

        // Select a ~0.500s loop region and verify Zero-Crossing Snap locks IN & OUT near 0.0 amplitude
        editor.SetSelectionRange(24113, 120113, applyZeroCrossingSnap: true);
        editor.GetBuffers(out float[] bufL, out _, out _);
        Assert.True(Math.Abs(bufL[editor.SelectionStartFrame]) < 0.05f);
        Assert.True(Math.Abs(bufL[editor.SelectionEndFrame]) < 0.05f);

        int selectedLen = editor.SelectionLengthFrames;
        Assert.InRange(selectedLen, 95000, 97000);

        // Save just the selected loop region to a 192 kHz 24-bit WAV file
        string tempLoopWav = Path.Combine(Path.GetTempPath(), $"spectra_loop_test_{Guid.NewGuid():N}.wav");
        try
        {
            var clip = editor.SaveSelectionToWav(tempLoopWav, RecordingBitFormat.Pcm24);
            Assert.True(File.Exists(tempLoopWav));
            Assert.Contains("192 kHz • 24-bit WAV", clip.FormatLabel);

            // Re-load the saved loop and verify 100% native 192 kHz sample rate and exact length
            using var verifyEditor = new AudioSampleEditorService();
            verifyEditor.LoadFile(tempLoopWav);
            Assert.Equal(192000, verifyEditor.SampleRate);
            Assert.Equal(selectedLen, verifyEditor.TotalFrames);
            Assert.Equal(RecordingBitFormat.Pcm24, verifyEditor.DetectedBitFormat);
        }
        finally
        {
            if (File.Exists(tempLoopWav))
                File.Delete(tempLoopWav);
        }

        // Verify Cut Start & End (CropToSelection) + Undo / Redo
        Assert.True(editor.CropToSelection());
        Assert.Equal(selectedLen, editor.TotalFrames);
        Assert.True(editor.CanUndo);
        Assert.True(editor.Undo());
        Assert.Equal(totalFrames, editor.TotalFrames);
        Assert.True(editor.Redo());
        Assert.Equal(selectedLen, editor.TotalFrames);
    }

    [Fact]
    public void DjTrackCalibration_ShouldPersistCustom2PointBpmBeatStartAndTrackGainAndSupport001Pitch()
    {
        string tempWav = Path.Combine(Path.GetTempPath(), $"spectra_calib_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(tempWav, SpeakerTestToneService.GeneratePureToneWav(220.0, 2.5, 48000));

            var decoder = new DjTrackDecoderService();
            var track = decoder.LoadAndAnalyzeTrack(tempWav, forceReanalyze: true, preserveNativeSampleRate: true);

            // 1. 2-Point BPM calculation across 4 beats: P1 = 0.245s, P2 = 0.245s + (240.0 / 136.0)s -> 136.00 BPM
            double p1 = 0.245;
            double p2 = p1 + (240.0 / 136.0);
            double calcBpm = Math.Round((60.0 * 4) / (p2 - p1), 2);
            Assert.Equal(136.00, calcBpm, 2);

            // 2. Save custom BPM, dragged beat-grid start, and per-track Gain (-3.4 dB)
            decoder.SaveTrackBeatGridAndBpm(track, calcBpm, p1);
            decoder.SaveTrackGainDb(track, -3.4);

            // 3. Re-load track (even with forceReanalyze: true!) and verify custom BPM, beat start & Gain are preserved!
            var reloaded = decoder.LoadAndAnalyzeTrack(tempWav, forceReanalyze: true, preserveNativeSampleRate: true);
            Assert.Equal(136.00, reloaded.DetectedBpm, 2);
            Assert.Equal(0.245, reloaded.FirstBeatSec, 3);
            Assert.True(reloaded.HasSavedGain);
            Assert.Equal(-3.4, reloaded.SavedGainDb, 1);

            // 4. Verify LoadTrackToDeck automatically restores per-track GainDb & supports 0.01% pitch steps (6.31% -> 6.32%)
            using var mixer = new DjMixerEngineService();
            mixer.LoadTrackToDeck(DjDeckId.DeckB, reloaded);
            Assert.Equal(-3.4, mixer.DeckB.GainDb, 1);

            mixer.DeckB.PitchPercent = 6.31;
            Assert.Equal(1.0631, mixer.DeckB.SpeedMultiplier, 6);
            mixer.DeckB.PitchPercent = 6.32;
            Assert.Equal(1.0632, mixer.DeckB.SpeedMultiplier, 6);

            // 5. Verify Beat-Quantized Loop snaps LoopStartFrame exactly to the nearest Beat Marker even when triggered mid-beat!
            double beatPeriodSec = 60.0 / 136.0;
            double beat2ExactFrames = (0.245 + 2.0 * beatPeriodSec) * reloaded.SampleRate;
            mixer.DeckB.IsPlaying = true;
            mixer.DeckB.PlayheadFrame = beat2ExactFrames + 0.15 * beatPeriodSec * reloaded.SampleRate; // slightly past Beat 2
            mixer.ToggleAutoLoop(DjDeckId.DeckB, 1.0);
            Assert.True(mixer.DeckB.LoopActive);
            Assert.Equal(beat2ExactFrames, mixer.DeckB.LoopStartFrame, 1);

            // 6. Verify StopAndReturnToStart stops playback, clears active loop, and rewinds PlayheadFrame to 0.0
            mixer.StopAndReturnToStart(DjDeckId.DeckB);
            Assert.False(mixer.DeckB.IsPlaying);
            Assert.False(mixer.DeckB.LoopActive);
            Assert.Equal(0.0, mixer.DeckB.PlayheadFrame, 4);

            // 6b. Verify Pitch resets to 0.00% on new track load + ALT+Click Cue Set & Normal Click Jump-and-Pause + Per-Track Cue Persistence
            mixer.DeckB.PitchPercent = -4.23;
            mixer.LoadTrackToDeck(DjDeckId.DeckB, reloaded);
            Assert.Equal(0.0, mixer.DeckB.PitchPercent, 6);

            mixer.DeckB.PlayheadFrame = 15000;
            mixer.DeckB.IsPlaying = true;
            Assert.True(mixer.TriggerHotCue(DjDeckId.DeckB, 0, clearOnly: false, setCuePoint: true)); // ALT+Click sets CUE 1
            decoder.SaveTrackCues(reloaded, updateMainCue: false);
            mixer.DeckB.PlayheadFrame = 38000;
            Assert.True(mixer.TriggerHotCue(DjDeckId.DeckB, 0, clearOnly: false, setCuePoint: false)); // Click jumps to CUE 1 & pauses
            Assert.False(mixer.DeckB.IsPlaying);
            Assert.Equal(15000.0, mixer.DeckB.PlayheadFrame, 2);

            var reloadedWithCues = decoder.LoadAndAnalyzeTrack(tempWav, forceReanalyze: false, preserveNativeSampleRate: true);
            Assert.Equal(15000L, reloadedWithCues.HotCueFrames[0]);

            // 7. Verify 2-Deck MasterGainDb prevents dual-deck clipping when summing two hot tracks
            float[] hotBuf = new float[256];
            Array.Fill(hotBuf, 0.85f);
            using var masterMixer = new DjMixerEngineService { PureNativeQualityMode = true };
            masterMixer.LoadTrackToDeck(DjDeckId.DeckA, new DjLoadedTrack { Title = "Hot A", SampleRate = 48000, TotalFrames = 256, Left = hotBuf, Right = hotBuf, DetectedBpm = 128 });
            masterMixer.LoadTrackToDeck(DjDeckId.DeckB, new DjLoadedTrack { Title = "Hot B", SampleRate = 48000, TotalFrames = 256, Left = hotBuf, Right = hotBuf, DetectedBpm = 128 });
            masterMixer.DeckA.IsPlaying = true;
            masterMixer.DeckB.IsPlaying = true;
            masterMixer.MasterGainDb = -6.02; // ~0.50x master trim -> (0.85 + 0.85) * 0.50 = ~0.85 unclipped!
            float[] mixL = new float[64];
            float[] mixR = new float[64];
            masterMixer.MixStereoBuffer(mixL, mixR, 64, 48000);
            Assert.InRange(mixL[0], 0.83f, 0.87f);
            // Deck A & Deck B channel VU meters must remain at 100% of their true deck level (0.85), completely unaffected by MasterGainDb!
            Assert.Equal(0.85f, masterMixer.DeckA.PeakLeft, 2);
            Assert.Equal(0.85f, masterMixer.DeckB.PeakLeft, 2);
        }
        finally
        {
            if (File.Exists(tempWav))
                File.Delete(tempWav);
        }
    }

    [Fact]
    public void DjStemsAndIsolatorPads_ShouldIsolateVocalsPreserveFullBassInInstruSoloBassAndKillKickAndHiHatsAt24DbPerOct()
    {
        int sr = 192000;
        int n = sr / 2;
        float[] bass70 = new float[n];
        float[] vocal1400 = new float[n];
        float[] synthSideL = new float[n];
        float[] synthSideR = new float[n];
        float[] hihat9000 = new float[n];
        for (int i = 0; i < n; i++)
        {
            bass70[i] = 0.5f * MathF.Sin((float)(2.0 * Math.PI * 70.0 * i / sr));
            vocal1400[i] = 0.5f * MathF.Sin((float)(2.0 * Math.PI * 1400.0 * i / sr));
            float syn = 0.5f * MathF.Sin((float)(2.0 * Math.PI * 750.0 * i / sr));
            synthSideL[i] = syn;
            synthSideR[i] = -syn;
            hihat9000[i] = 0.5f * MathF.Sin((float)(2.0 * Math.PI * 9000.0 * i / sr));
        }

        static float MeasureSteady(float[] inL, float[] inR, int rate, Action<DjDeckChannelState> cfg)
        {
            using var m = new DjMixerEngineService { PureNativeQualityMode = true };
            m.LoadTrackToDeck(DjDeckId.DeckA, new DjLoadedTrack { Title = "Stem", SampleRate = rate, TotalFrames = inL.Length, Left = inL, Right = inR, DetectedBpm = 128 });
            m.DeckA.IsPlaying = true;
            cfg(m.DeckA);
            int frames = rate / 4;
            float[] oL = new float[frames];
            float[] oR = new float[frames];
            m.MixStereoBuffer(oL, oR, frames, rate);
            float pk = 0f;
            for (int i = frames / 2; i < frames; i++)
                pk = Math.Max(pk, Math.Max(Math.Abs(oL[i]), Math.Abs(oR[i])));
            return pk;
        }

        // Vocal Stem: passes center vocal, strips 70 Hz kick/bass, strips 9 kHz hi-hat, and rejects stereo synth
        Assert.True(MeasureSteady(vocal1400, vocal1400, sr, d => d.PadVocalActive = true) > 0.45f);
        Assert.True(MeasureSteady(bass70, bass70, sr, d => d.PadVocalActive = true) < 0.035f);
        Assert.True(MeasureSteady(synthSideL, synthSideR, sr, d => d.PadVocalActive = true) < 0.03f);
        Assert.True(MeasureSteady(hihat9000, hihat9000, sr, d => d.PadVocalActive = true) < 0.04f);

        // Instru Stem: preserves full 70 Hz kick/bass & stereo synth while cancelling center vocal
        Assert.True(MeasureSteady(bass70, bass70, sr, d => d.PadInstruActive = true) > 0.47f);
        Assert.True(MeasureSteady(synthSideL, synthSideR, sr, d => d.PadInstruActive = true) > 0.45f);
        Assert.True(MeasureSteady(vocal1400, vocal1400, sr, d => d.PadInstruActive = true) < 0.035f);

        // Bass Solo, 24 dB/oct Kick Kill & 24 dB/oct HiHat Kill
        Assert.True(MeasureSteady(bass70, bass70, sr, d => d.PadBassActive = true) > 0.50f);
        Assert.True(MeasureSteady(vocal1400, vocal1400, sr, d => d.PadBassActive = true) < 0.02f);
        Assert.True(MeasureSteady(bass70, bass70, sr, d => d.PadKickKillActive = true) < 0.035f);
        Assert.True(MeasureSteady(hihat9000, hihat9000, sr, d => d.PadHiHatKillActive = true) < 0.035f);
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
