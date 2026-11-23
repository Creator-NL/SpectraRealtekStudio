using System;
using System.Runtime.InteropServices;
using System.Threading;
using SpectraRealtekStudio.Core.Interop;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
}

/// <summary>
/// Internal real-time DSP state for a single DJ turntable deck (Deck A or Deck B).
/// </summary>
public sealed class DjDeckChannelState
{
    public DjDeckId DeckId { get; }
    public DjLoadedTrack? Track;

    public volatile bool IsPlaying;
    public volatile bool IsScratching;
    public volatile bool IsCuePreviewing;
    public volatile bool MasterTempoEnabled = false; // Default OFF for 100% phase-pure analog vinyl pitch; click MT to enable WSOLA keylock
    public volatile bool VinylModeEnabled = true;
    public volatile bool SlipModeEnabled;
    public volatile bool HeadphoneCueEnabled;

    public double PlayheadFrame;
    public double SlipPlayheadFrame;
    public double PlatterAngleDeg;
    public doubleScratchVelocity ScratchRate = 1.0;

    // Tempo / Pitch controls
    public double PitchPercent; // e.g. -16..+16
    public double PitchRangePercent = 16.0;
    public double TempNudgePercent; // momentary pitch bend (-4% / +4%)
    public double ManualBpmOverride;

    // Tap tempo history
    private readonly long[] _tapTicks = new long[6];
    private int _tapCount;

    // Looping
    public volatile bool LoopActive;
    public double LoopBeats = 4.0;
    public double LoopStartFrame;
    public double LoopEndFrame;

    // Mixer channel strip: Gain, 3-Band Isolator EQ, Bipolar Color Filter, Volume Fader
    public double GainDb; // -24..+12 dB
    public double HighDb; // -36 (Kill)..+12 dB
    public double MidDb;  // -36 (Kill)..+12 dB
    public double LowDb;  // -36 (Kill)..+12 dB
    public double FilterKnob; // -1.0 (Low-Pass) .. 0.0 (Bypass) .. +1.0 (High-Pass)
    public double ChannelVolume = 1.0; // 0..1 (1.0 = exact 0 dB unity)

    // 3-Slot Deck FX Rack (Flanger, Reverb/Echo, WahWah)
    public readonly bool[] FxEnabled = new bool[3];
    public readonly double[] FxDepth = new double[] { 0.55, 0.45, 0.60 };
    public readonly DjFxType[] FxSlotType = new DjFxType[] { DjFxType.Flanger, DjFxType.Reverb, DjFxType.WahWah };

    // 6 Stem / Frequency Isolator Performance Pads (Vocal, Instru, Bass, Kick, HiHat, StemsFX)
    public volatile bool PadVocalActive;
    public volatile bool PadInstruActive;
    public volatile bool PadBassActive;
    public volatile bool PadKickKillActive;
    public volatile bool PadHiHatKillActive;
    public volatile bool PadStemsFxGateActive;

    // Live channel peak telemetry
    public float PeakLeft;
    public float PeakRight;

    // Crossover & SVF filter state per channel
    public double Recon1L, Recon1R, Recon2L, Recon2R, Recon3L, Recon3R, Recon4L, Recon4R;
    public double LpLowL, LpLowR;
    public double LpLow2L, LpLow2R;
    public double HpLowLpL, HpLowLpR;
    public double LpHighL, LpHighR;
    public double SvfLowL, SvfBandL, SvfLowR, SvfBandR;
    public double WahLowL, WahBandL, WahLowR, WahBandR;
    public double WahPhase;

    // WSOLA Master Tempo (Keylock) grain state
    public double WsolaGrainPhase;
    public double WsolaOffsetA;
    public double WsolaOffsetB;

    // Stereo Flanger delay buffer (1024 frames)
    public readonly float[] FlangerBufL = new float[1024];
    public readonly float[] FlangerBufR = new float[1024];
    public int FlangerWritePos;
    public double FlangerLfoPhase;

    // Stereo Reverb / Echo multi-tap delay line (32768 frames ~ 680 ms at 48 kHz)
    public readonly float[] ReverbBufL = new float[32768];
    public readonly float[] ReverbBufR = new float[32768];
    public int ReverbWritePos;

    public DjDeckChannelState(DjDeckId id)
    {
        DeckId = id;
    }

    public double BaseBpm => ManualBpmOverride > 30.0
        ? ManualBpmOverride
        : (Track?.DetectedBpm ?? 128.0);

    public double SpeedMultiplier => Math.Clamp(1.0 + (PitchPercent + TempNudgePercent) / 100.0, 0.25, 2.5);
    public double EffectiveBpm => BaseBpm * SpeedMultiplier;

    public void RegisterTapTempo()
    {
        long now = Environment.TickCount64;
        if (_tapCount > 0 && now - _tapTicks[_tapCount - 1] > 2500)
        {
            _tapCount = 0;
        }

        if (_tapCount < _tapTicks.Length)
        {
            _tapTicks[_tapCount++] = now;
        }
        else
        {
            Array.Copy(_tapTicks, 1, _tapTicks, 0, _tapTicks.Length - 1);
            _tapTicks[^1] = now;
        }

        if (_tapCount >= 2)
        {
            double avgMs = (double)(_tapTicks[_tapCount - 1] - _tapTicks[0]) / (_tapCount - 1);
            if (avgMs >= 250 && avgMs <= 1200)
            {
                ManualBpmOverride = Math.Round(60000.0 / avgMs, 1);
            }
        }
    }
}

public struct doubleScratchVelocity
{
    public double Value;
    public static implicit operator double(doubleScratchVelocity v) => v.Value;
    public static implicit operator doubleScratchVelocity(double d) => new() { Value = d };
}

/// <summary>
/// Zero-dependency low-latency Windows WASAPI Dual-Deck DJ Mixing Engine.
/// Runs in the background across tabs whenever a deck is playing, and sleeps at 0% CPU when both decks are stopped.
/// </summary>
public sealed class DjMixerEngineService : IDisposable
{
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _engineLock = new();
    private readonly DjDeckChannelState _deckA = new(DjDeckId.DeckA);
    private readonly DjDeckChannelState _deckB = new(DjDeckId.DeckB);

    private Thread? _renderThread;
    private volatile bool _stopRequested;
    private volatile bool _isRunning;
    private string? _masterEndpointId;

    private double _crossfader; // -1.0 (Deck A) .. 0.0 (Center) .. +1.0 (Deck B)
    private DjCrossfaderCurve _crossfaderCurve = DjCrossfaderCurve.ConstantPower;
    private volatile bool _isAutoCrossfading;
    private double _autoCrossfadeTarget;
    private double _autoCrossfadeStepPerSec = 0.35;

    /// <summary>
    /// When true ("Load As-Is / Pure Native Quality"), plays tracks at their exact native sample rate with
    /// 1:1 bit-exact sample pass-through at 0% pitch, unity 0 dB gain, full-center crossfader, and zero anti-imaging filter coloration.
    /// </summary>
    public volatile bool PureNativeQualityMode = true;

    private float _masterPeakL;
    private float _masterPeakR;
    private int _outputSampleRate = 48000;
    private string _statusMessage = "DJ Dual-Deck Engine Ready";

    public DjDeckChannelState DeckA => _deckA;
    public DjDeckChannelState DeckB => _deckB;
    public DjDeckChannelState GetDeck(DjDeckId id) => id == DjDeckId.DeckA ? _deckA : _deckB;

    public bool IsAnyDeckPlaying => _deckA.IsPlaying || _deckB.IsPlaying || _deckA.IsScratching || _deckB.IsScratching;

    public double Crossfader
    {
        get => _crossfader;
        set
        {
            _isAutoCrossfading = false;
            _crossfader = Math.Clamp(value, -1.0, 1.0);
        }
    }

    public DjCrossfaderCurve CrossfaderCurve
    {
        get => _crossfaderCurve;
        set => _crossfaderCurve = value;
    }

    public void SetMasterEndpoint(string? endpointId)
    {
        if (string.Equals(_masterEndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            return;

        _masterEndpointId = endpointId;
        if (_isRunning)
        {
            StopEngine();
            EnsureEngineRunning();
        }
    }

    public void LoadTrackToDeck(DjDeckId deckId, DjLoadedTrack track)
    {
        var deck = GetDeck(deckId);
        lock (_engineLock)
        {
            deck.IsPlaying = false;
            deck.IsScratching = false;
            deck.LoopActive = false;
            deck.ManualBpmOverride = 0.0;
            deck.Track = track;
            deck.PlayheadFrame = track.CueFrame;
            deck.SlipPlayheadFrame = track.CueFrame;
            deck.PlatterAngleDeg = 0.0;
            deck.PeakLeft = 0f;
            deck.PeakRight = 0f;
        }
        _statusMessage = $"Loaded '{track.Artist} — {track.Title}' ({track.DetectedBpm:0.0} BPM • {track.MusicalKey}) to {(deckId == DjDeckId.DeckA ? "Deck A" : "Deck B")}.";
    }

    public void TogglePlay(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        if (!deck.IsPlaying)
        {
            if (deck.PlayheadFrame >= deck.Track.TotalFrames - 100)
            {
                deck.PlayheadFrame = deck.Track.CueFrame;
                deck.SlipPlayheadFrame = deck.Track.CueFrame;
            }
            deck.IsPlaying = true;
            EnsureEngineRunning();
        }
        else
        {
            deck.IsPlaying = false;
        }
    }

    public void CueDown(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        if (deck.IsPlaying && !deck.IsCuePreviewing)
        {
            // While playing, CUE returns immediately to the cue point and pauses
            deck.IsPlaying = false;
            deck.PlayheadFrame = deck.Track.CueFrame;
            deck.SlipPlayheadFrame = deck.Track.CueFrame;
        }
        else
        {
            // While paused: if moved away from CueFrame, set new CueFrame; otherwise momentary preview play
            if (Math.Abs(deck.PlayheadFrame - deck.Track.CueFrame) > deck.Track.SampleRate * 0.08)
            {
                deck.Track.CueFrame = Math.Clamp((long)Math.Round(deck.PlayheadFrame), 0L, Math.Max(0, deck.Track.TotalFrames - 1));
            }
            deck.IsCuePreviewing = true;
            deck.IsPlaying = true;
            EnsureEngineRunning();
        }
    }

    public void CueUp(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null || !deck.IsCuePreviewing)
            return;

        deck.IsCuePreviewing = false;
        deck.IsPlaying = false;
        deck.PlayheadFrame = deck.Track.CueFrame;
        deck.SlipPlayheadFrame = deck.Track.CueFrame;
    }

    /// <summary>
    /// Synchronizes this deck's BPM and beat-grid phase to the opposite deck.
    /// </summary>
    public void SyncDeck(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        var other = GetDeck(deckId == DjDeckId.DeckA ? DjDeckId.DeckB : DjDeckId.DeckA);
        if (deck.Track == null || other.Track == null)
            return;

        double targetBpm = other.EffectiveBpm;
        double myBase = deck.BaseBpm;
        if (myBase <= 10.0)
            return;

        // Handle double/half tempo matching smoothly
        if (targetBpm > myBase * 1.65) targetBpm *= 0.5;
        else if (targetBpm < myBase * 0.65) targetBpm *= 2.0;

        double requiredPitchPct = ((targetBpm / myBase) - 1.0) * 100.0;
        if (Math.Abs(requiredPitchPct) > deck.PitchRangePercent)
        {
            deck.PitchRangePercent = 50.0;
        }
        deck.PitchPercent = Math.Clamp(requiredPitchPct, -deck.PitchRangePercent, deck.PitchRangePercent);

        // Snap beat phase if both tracks are playing or ready
        double otherBeatPeriodSec = 60.0 / Math.Max(30.0, other.BaseBpm);
        double otherCurSec = other.PlayheadFrame / Math.Max(1, other.Track.SampleRate);
        double otherPhaseNorm = ((otherCurSec - other.Track.FirstBeatSec) / otherBeatPeriodSec) % 1.0;
        if (otherPhaseNorm < 0) otherPhaseNorm += 1.0;

        double myBeatPeriodSec = 60.0 / Math.Max(30.0, myBase);
        double myCurSec = deck.PlayheadFrame / Math.Max(1, deck.Track.SampleRate);
        double myBeatIndex = Math.Round(((myCurSec - deck.Track.FirstBeatSec) / myBeatPeriodSec) - otherPhaseNorm);
        double alignedSec = deck.Track.FirstBeatSec + (myBeatIndex + otherPhaseNorm) * myBeatPeriodSec;

        if (alignedSec >= 0 && alignedSec < deck.Track.DurationSec)
        {
            deck.PlayheadFrame = alignedSec * deck.Track.SampleRate;
            deck.SlipPlayheadFrame = deck.PlayheadFrame;
        }

        _statusMessage = $"Beat-Synced {(deckId == DjDeckId.DeckA ? "Deck A" : "Deck B")} to {deck.EffectiveBpm:0.00} BPM.";
    }

    public void SeekNormalized(DjDeckId deckId, double normPos)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        double target = Math.Clamp(normPos, 0.0, 1.0) * Math.Max(0, deck.Track.TotalFrames - 1);
        deck.PlayheadFrame = target;
        deck.SlipPlayheadFrame = target;
    }

    public void BeginScratch(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        deck.IsScratching = true;
        deck.ScratchRate = 0.0;
        EnsureEngineRunning();
    }

    public void UpdateScratch(DjDeckId deckId, double deltaSeconds, double instantaneousRate)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        if (deck.VinylModeEnabled)
        {
            deck.ScratchRate = Math.Clamp(instantaneousRate, -4.0, 4.0);
            double newFrame = deck.PlayheadFrame + deltaSeconds * deck.Track.SampleRate;
            deck.PlayheadFrame = Math.Clamp(newFrame, 0.0, Math.Max(0, deck.Track.TotalFrames - 1));
            deck.PlatterAngleDeg = (deck.PlatterAngleDeg + deltaSeconds * 198.0 + 3600.0) % 360.0;
        }
        else
        {
            // Rim nudge mode: momentary pitch bend
            deck.TempNudgePercent = Math.Clamp(instantaneousRate * 3.5, -8.0, 8.0);
        }
    }

    public void EndScratch(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        deck.IsScratching = false;
        deck.ScratchRate = 1.0;
        deck.TempNudgePercent = 0.0;
        if (deck.SlipModeEnabled && deck.IsPlaying && deck.Track != null)
        {
            deck.PlayheadFrame = deck.SlipPlayheadFrame;
        }
    }

    // =========================================================================
    // AUTO-LOOP & HOT CUES
    // =========================================================================

    public void ToggleAutoLoop(DjDeckId deckId, double? explicitBeats = null)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        if (explicitBeats.HasValue)
        {
            deck.LoopBeats = explicitBeats.Value;
        }

        if (deck.LoopActive && !explicitBeats.HasValue)
        {
            deck.LoopActive = false;
            if (deck.SlipModeEnabled && deck.IsPlaying)
            {
                deck.PlayheadFrame = deck.SlipPlayheadFrame;
            }
            return;
        }

        double beatFrames = (60.0 / Math.Max(40.0, deck.BaseBpm)) * deck.Track.SampleRate;
        double loopLenFrames = Math.Max(200.0, beatFrames * deck.LoopBeats);

        deck.LoopStartFrame = deck.PlayheadFrame;
        deck.LoopEndFrame = Math.Min(deck.Track.TotalFrames - 1, deck.LoopStartFrame + loopLenFrames);
        deck.LoopActive = deck.LoopEndFrame > deck.LoopStartFrame + 100;
    }

    public void ScaleLoopLength(DjDeckId deckId, double factor)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        deck.LoopBeats = Math.Clamp(deck.LoopBeats * factor, 0.25, 32.0);
        if (deck.LoopActive)
        {
            double beatFrames = (60.0 / Math.Max(40.0, deck.BaseBpm)) * deck.Track.SampleRate;
            double loopLenFrames = Math.Max(200.0, beatFrames * deck.LoopBeats);
            deck.LoopEndFrame = Math.Min(deck.Track.TotalFrames - 1, deck.LoopStartFrame + loopLenFrames);
        }
    }

    public void SetManualLoopIn(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null) return;
        deck.LoopStartFrame = deck.PlayheadFrame;
        if (deck.LoopEndFrame <= deck.LoopStartFrame)
        {
            double beatFrames = (60.0 / Math.Max(40.0, deck.BaseBpm)) * deck.Track.SampleRate;
            deck.LoopEndFrame = Math.Min(deck.Track.TotalFrames - 1, deck.LoopStartFrame + beatFrames * 4.0);
        }
    }

    public void SetManualLoopOut(DjDeckId deckId)
    {
        var deck = GetDeck(deckId);
        if (deck.Track == null) return;
        if (deck.PlayheadFrame > deck.LoopStartFrame + 200)
        {
            deck.LoopEndFrame = deck.PlayheadFrame;
            deck.LoopActive = true;
        }
    }

    public void TriggerHotCue(DjDeckId deckId, int slotIndex, bool clearOnly = false)
    {
        if (slotIndex < 0 || slotIndex >= 4)
            return;

        var deck = GetDeck(deckId);
        if (deck.Track == null)
            return;

        if (clearOnly)
        {
            deck.Track.HotCueFrames[slotIndex] = -1;
            return;
        }

        long frame = deck.Track.HotCueFrames[slotIndex];
        if (frame < 0)
        {
            deck.Track.HotCueFrames[slotIndex] = Math.Clamp((long)Math.Round(deck.PlayheadFrame), 0L, Math.Max(0, deck.Track.TotalFrames - 1));
        }
        else
        {
            deck.PlayheadFrame = frame;
            if (!deck.SlipModeEnabled)
                deck.SlipPlayheadFrame = frame;
            if (!deck.IsPlaying)
            {
                deck.IsPlaying = true;
                EnsureEngineRunning();
            }
        }
    }

    public void StartAutoCrossfade(double durationSeconds = 5.0)
    {
        _autoCrossfadeTarget = _crossfader <= 0.0 ? 1.0 : -1.0;
        _autoCrossfadeStepPerSec = 2.0 / Math.Max(1.0, durationSeconds);
        _isAutoCrossfading = true;

        // Automatically start incoming deck if loaded and paused
        if (_autoCrossfadeTarget > 0 && _deckB.Track != null && !_deckB.IsPlaying)
        {
            _deckB.IsPlaying = true;
        }
        else if (_autoCrossfadeTarget < 0 && _deckA.Track != null && !_deckA.IsPlaying)
        {
            _deckA.IsPlaying = true;
        }
        EnsureEngineRunning();
    }

    // =========================================================================
    // REAL-TIME MIXING & DSP PIPELINE (UNIT-TESTABLE & WASAPI STREAMING)
    // =========================================================================

    /// <summary>
    /// Renders <paramref name="numFrames"/> of stereo mixed audio from Deck A and Deck B at <paramref name="outputRate"/>.
    /// Exposed publicly so automated verification tests can validate DSP, EQ Kill, Filter, and Sync bit-accurately.
    /// </summary>
    public void MixStereoBuffer(float[] outLeft, float[] outRight, int numFrames, int outputRate)
    {
        if (numFrames <= 0)
            return;

        if (_isAutoCrossfading)
        {
            double dt = (double)numFrames / Math.Max(8000, outputRate);
            double step = _autoCrossfadeStepPerSec * dt;
            if (_crossfader < _autoCrossfadeTarget)
            {
                _crossfader = Math.Min(_autoCrossfadeTarget, _crossfader + step);
            }
            else
            {
                _crossfader = Math.Max(_autoCrossfadeTarget, _crossfader - step);
            }

            if (Math.Abs(_crossfader - _autoCrossfadeTarget) < 0.005)
            {
                _crossfader = _autoCrossfadeTarget;
                _isAutoCrossfading = false;
            }
        }

        bool pureNative = PureNativeQualityMode;
        ComputeCrossfaderGains(_crossfader, pureNative ? DjCrossfaderCurve.SharpCut : _crossfaderCurve, out float xfGainA, out float xfGainB);
        if (pureNative)
        {
            // Full-Center Unity Crossfader: 1.00f (0.0 dB) for both decks at center (0.0), smooth linear fade on opposite side
            xfGainA = _crossfader <= 0.0 ? 1.0f : (float)Math.Clamp(1.0 - _crossfader, 0.0, 1.0);
            xfGainB = _crossfader >= 0.0 ? 1.0f : (float)Math.Clamp(1.0 + _crossfader, 0.0, 1.0);
        }

        float masterPeakL = 0f;
        float masterPeakR = 0f;
        float peakAL = 0f, peakAR = 0f;
        float peakBL = 0f, peakBR = 0f;

        for (int i = 0; i < numFrames; i++)
        {
            RenderDeckSample(_deckA, outputRate, pureNative, out float aL, out float aR);
            RenderDeckSample(_deckB, outputRate, pureNative, out float bL, out float bR);

            float absAL = Math.Abs(aL); if (absAL > peakAL) peakAL = absAL;
            float absAR = Math.Abs(aR); if (absAR > peakAR) peakAR = absAR;
            float absBL = Math.Abs(bL); if (absBL > peakBL) peakBL = absBL;
            float absBR = Math.Abs(bR); if (absBR > peakBR) peakBR = absBR;

            // If Headphone Cue (🎧) is toggled on a deck, also allow subtle preview monitoring even if crossfader is closed
            float cueBoostA = _deckA.HeadphoneCueEnabled ? Math.Max(xfGainA, 0.55f) : xfGainA;
            float cueBoostB = _deckB.HeadphoneCueEnabled ? Math.Max(xfGainB, 0.55f) : xfGainB;

            float mixL = aL * cueBoostA + bL * cueBoostB;
            float mixR = aR * cueBoostA + bR * cueBoostB;

            if (pureNative)
            {
                // Bit-transparent pass-through for all normal samples in [-1.0, +1.0]; only clamp if EQ boost exceeds +/-1.0
                if (mixL > 1.0f) mixL = 1.0f;
                else if (mixL < -1.0f) mixL = -1.0f;

                if (mixR > 1.0f) mixR = 1.0f;
                else if (mixR < -1.0f) mixR = -1.0f;
            }
            else
            {
                // Studio soft-knee master limiter so dual-deck drops never hard-clip
                mixL = SoftClipSample(mixL);
                mixR = SoftClipSample(mixR);
            }

            outLeft[i] = mixL;
            outRight[i] = mixR;

            float absML = Math.Abs(mixL); if (absML > masterPeakL) masterPeakL = absML;
            float absMR = Math.Abs(mixR); if (absMR > masterPeakR) masterPeakR = absMR;
        }

        _deckA.PeakLeft = Math.Max(peakAL, _deckA.PeakLeft * 0.82f);
        _deckA.PeakRight = Math.Max(peakAR, _deckA.PeakRight * 0.82f);
        _deckB.PeakLeft = Math.Max(peakBL, _deckB.PeakLeft * 0.82f);
        _deckB.PeakRight = Math.Max(peakBR, _deckB.PeakRight * 0.82f);
        _masterPeakL = Math.Max(masterPeakL, _masterPeakL * 0.82f);
        _masterPeakR = Math.Max(masterPeakR, _masterPeakR * 0.82f);
    }

    public static void ComputeCrossfaderGains(double xf, DjCrossfaderCurve curve, out float gainA, out float gainB)
    {
        double t = Math.Clamp((xf + 1.0) * 0.5, 0.0, 1.0); // 0 = full A, 1 = full B
        switch (curve)
        {
            case DjCrossfaderCurve.SmoothLinear:
                gainA = (float)(1.0 - t);
                gainB = (float)t;
                break;
            case DjCrossfaderCurve.SharpCut:
                gainA = t > 0.92 ? (float)((1.0 - t) / 0.08) : 1.0f;
                gainB = t < 0.08 ? (float)(t / 0.08) : 1.0f;
                break;
            default: // ConstantPower (Club standard 3 dB center)
                gainA = (float)Math.Cos(t * Math.PI * 0.5);
                gainB = (float)Math.Sin(t * Math.PI * 0.5);
                break;
        }
    }

    private static void RenderDeckSample(DjDeckChannelState deck, int outputRate, bool pureNative, out float outL, out float outR)
    {
        outL = 0f;
        outR = 0f;

        var track = deck.Track;
        if (track == null || track.TotalFrames < 4 || (!deck.IsPlaying && !deck.IsScratching))
            return;

        double rateRatio = (double)track.SampleRate / Math.Max(8000, outputRate);
        double speed = deck.IsScratching ? deck.ScratchRate.Value : deck.SpeedMultiplier;
        double step = rateRatio * speed;

        double pos = deck.PlayheadFrame;
        if (pos < 0 || pos >= track.TotalFrames - 2)
        {
            if (!deck.IsScratching)
            {
                deck.IsPlaying = false;
                deck.PlayheadFrame = Math.Clamp(pos, 0, Math.Max(0, track.TotalFrames - 1));
            }
            return;
        }

        float rawL, rawR;

        // In Pure Native ("Load As-Is") mode at 0% pitch when track sample rate matches output rate (e.g. 192 kHz == 192 kHz),
        // fetch exact integer sample indices 1:1 with ZERO interpolation or grain processing!
        if (pureNative && !deck.IsScratching && Math.Abs(step - 1.0) < 1e-6)
        {
            int exactIdx = Math.Clamp((int)Math.Round(pos), 0, track.TotalFrames - 1);
            rawL = track.Left[exactIdx];
            rawR = track.Right[exactIdx];
        }
        // Master Tempo (MT) WSOLA Pitch-Lock when playing forward with non-zero pitch offset
        else if (deck.MasterTempoEnabled && !deck.IsScratching && Math.Abs(speed - 1.0) > 0.003)
        {
            int grainFrames = Math.Max(512, track.SampleRate / 28); // ~35 ms WSOLA grain
            double prevPhase = deck.WsolaGrainPhase;
            deck.WsolaGrainPhase += rateRatio / grainFrames;

            double driftStep = (1.0 - speed) * rateRatio;
            deck.WsolaOffsetA += driftStep;
            deck.WsolaOffsetB += driftStep;

            // Reset Grain A only when Grain A's Hann window is at zero (phase wraps 1.0 -> 0.0)
            if (deck.WsolaGrainPhase >= 1.0)
            {
                deck.WsolaGrainPhase -= 1.0;
                deck.WsolaOffsetA = 0.0;
            }
            // Reset Grain B only when Grain B's Hann window is at zero (phase crosses 0.5)
            else if (prevPhase < 0.5 && deck.WsolaGrainPhase >= 0.5)
            {
                deck.WsolaOffsetB = 0.0;
            }

            double phaseA = deck.WsolaGrainPhase;
            double phaseB = (phaseA + 0.5) % 1.0;
            double winA = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phaseA);
            double winB = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phaseB);

            double posA = Math.Clamp(pos + deck.WsolaOffsetA, 0, track.TotalFrames - 3);
            double posB = Math.Clamp(pos + deck.WsolaOffsetB, 0, track.TotalFrames - 3);

            SampleHermiteStereo(track.Left, track.Right, track.TotalFrames, posA, out float aL, out float aR);
            SampleHermiteStereo(track.Left, track.Right, track.TotalFrames, posB, out float bL, out float bR);

            rawL = (float)(aL * winA + bL * winB);
            rawR = (float)(aR * winA + bR * winB);
        }
        else
        {
            SampleHermiteStereo(track.Left, track.Right, track.TotalFrames, pos, out rawL, out rawR);
        }

        // Advance playhead & slip playhead
        if (!deck.IsScratching)
        {
            deck.PlayheadFrame += step;
            deck.SlipPlayheadFrame = Math.Clamp(deck.SlipPlayheadFrame + step, 0, track.TotalFrames - 1);

            if (deck.LoopActive && deck.LoopEndFrame > deck.LoopStartFrame + 64 && deck.PlayheadFrame >= deck.LoopEndFrame)
            {
                deck.PlayheadFrame = deck.LoopStartFrame + ((deck.PlayheadFrame - deck.LoopStartFrame) % (deck.LoopEndFrame - deck.LoopStartFrame));
            }

            // 33.3 RPM Vinyl platter rotation (198 degrees per second at 1.0x speed)
            deck.PlatterAngleDeg = (deck.PlatterAngleDeg + (198.0 * speed / outputRate) + 360.0) % 360.0;
        }

        // 0. True 4th-Order (24 dB/oct) Cascaded Butterworth Biquad Anti-Imaging Reconstruction Filter
        // Only runs in 48 kHz resampled mode (!pureNative); completely bypassed in "Load As-Is (Pure Native)" mode!
        if (!pureNative && outputRate > track.SampleRate * 1.15)
        {
            double fc = Math.Clamp(Math.Min(19500.0, track.SampleRate * 0.42 * Math.Min(1.25, speed)), 4000.0, outputRate * 0.45);
            double w0 = 2.0 * Math.PI * fc / outputRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);

            // Stage 1 Butterworth Q1 = 0.54119610
            double alpha1 = sinW0 / (2.0 * 0.54119610);
            double invA0_1 = 1.0 / (1.0 + alpha1);
            double b0_1 = ((1.0 - cosW0) * 0.5) * invA0_1;
            double b1_1 = (1.0 - cosW0) * invA0_1;
            double a1_1 = (-2.0 * cosW0) * invA0_1;
            double a2_1 = (1.0 - alpha1) * invA0_1;

            double y1L = b0_1 * rawL + deck.Recon1L;
            deck.Recon1L = b1_1 * rawL - a1_1 * y1L + deck.Recon2L;
            deck.Recon2L = b0_1 * rawL - a2_1 * y1L;

            double y1R = b0_1 * rawR + deck.Recon1R;
            deck.Recon1R = b1_1 * rawR - a1_1 * y1R + deck.Recon2R;
            deck.Recon2R = b0_1 * rawR - a2_1 * y1R;

            // Stage 2 Butterworth Q2 = 1.30656296
            double alpha2 = sinW0 / (2.0 * 1.30656296);
            double invA0_2 = 1.0 / (1.0 + alpha2);
            double b0_2 = ((1.0 - cosW0) * 0.5) * invA0_2;
            double b1_2 = (1.0 - cosW0) * invA0_2;
            double a1_2 = (-2.0 * cosW0) * invA0_2;
            double a2_2 = (1.0 - alpha2) * invA0_2;

            double y2L = b0_2 * y1L + deck.Recon3L;
            deck.Recon3L = b1_2 * y1L - a1_2 * y2L + deck.Recon4L;
            deck.Recon4L = b0_2 * y1L - a2_2 * y2L;

            double y2R = b0_2 * y1R + deck.Recon3R;
            deck.Recon3R = b1_2 * y1R - a1_2 * y2R + deck.Recon4R;
            deck.Recon4R = b0_2 * y1R - a2_2 * y2R;

            rawL = (float)y2L;
            rawR = (float)y2R;
        }

        // 1. Pre-Amp Channel Trim GAIN:
        // In Pure Native ("Load As-Is") mode, 0.0 dB Gain = exact 1.000000f unity pass-through!
        // In Standard Resampled mode, 0.0 dB Gain = 0.58f (-4.7 dB headroom).
        float baseHeadroom = pureNative ? 1.0f : 0.58f;
        float gainLin = Math.Abs(deck.GainDb) < 0.01 ? baseHeadroom : (baseHeadroom * (float)Math.Pow(10.0, deck.GainDb / 20.0));
        double sL = rawL * gainLin;
        double sR = rawR * gainLin;

        // 2. 6 Stem / Frequency Isolator Performance Pads (Vocal, Instru, Bass, Kick, HiHat, StemsFX)
        if (deck.PadInstruActive)
        {
            // Mid/Side Vocal Cancellation: removes center-panned lead vocals while keeping stereo instruments & bass
            double side = (sL - sR) * 0.72;
            double lowMono = (deck.LpLowL + deck.LpLowR) * 0.5;
            sL = lowMono + side;
            sR = lowMono - side;
        }
        else if (deck.PadVocalActive)
        {
            // Center-channel vocal presence focus
            double mid = (sL + sR) * 0.62;
            double side = (sL - sR) * 0.18;
            sL = mid + side;
            sR = mid - side;
        }

        // 3. 3-Band 12 dB/oct Isolator EQ Crossover (Low < 250 Hz, Mid 250..3800 Hz, High > 3800 Hz)
        double aLow = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * 250.0 / outputRate), 0.002, 0.25);
        double aHigh = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * 3800.0 / outputRate), 0.02, 0.65);

        deck.LpLowL += aLow * (sL - deck.LpLowL);
        deck.LpLowR += aLow * (sR - deck.LpLowR);
        double hp1L = sL - deck.LpLowL;
        double hp1R = sR - deck.LpLowR;

        deck.LpLow2L += aLow * (deck.LpLowL - deck.LpLow2L);
        deck.LpLow2R += aLow * (deck.LpLowR - deck.LpLow2R);
        deck.HpLowLpL += aLow * (hp1L - deck.HpLowLpL);
        deck.HpLowLpR += aLow * (hp1R - deck.HpLowLpR);

        double bandLowL = deck.LpLow2L;
        double bandLowR = deck.LpLow2R;
        double midHighL = hp1L - deck.HpLowLpL;
        double midHighR = hp1R - deck.HpLowLpR;

        deck.LpHighL += aHigh * (midHighL - deck.LpHighL);
        deck.LpHighR += aHigh * (midHighR - deck.LpHighR);

        double bandMidL = deck.LpHighL;
        double bandMidR = deck.LpHighR;
        double bandHighL = midHighL - deck.LpHighL;
        double bandHighR = midHighR - deck.LpHighR;

        bool eqActive = Math.Abs(deck.LowDb) > 0.05
                     || Math.Abs(deck.MidDb) > 0.05
                     || Math.Abs(deck.HighDb) > 0.05
                     || deck.PadBassActive
                     || deck.PadKickKillActive
                     || deck.PadHiHatKillActive;

        if (eqActive)
        {
            double gLow = deck.LowDb <= -34.0 ? 0.0 : Math.Pow(10.0, deck.LowDb / 20.0);
            double gMid = deck.MidDb <= -34.0 ? 0.0 : Math.Pow(10.0, deck.MidDb / 20.0);
            double gHigh = deck.HighDb <= -34.0 ? 0.0 : Math.Pow(10.0, deck.HighDb / 20.0);

            if (deck.PadBassActive) gLow *= 1.65;
            if (deck.PadKickKillActive) gLow = 0.0;
            if (deck.PadHiHatKillActive) gHigh = 0.0;

            sL = bandLowL * gLow + bandMidL * gMid + bandHighL * gHigh;
            sR = bandLowR * gLow + bandMidR * gMid + bandHighR * gHigh;
        }

        // 4. Bipolar Resonant DJ Color FILTER Knob (-1 = Low-Pass, 0 = Bypass, +1 = High-Pass)
        double fk = deck.FilterKnob;
        if (Math.Abs(fk) > 0.03)
        {
            double cutoffHz = fk < 0.0
                ? 18000.0 * Math.Pow(110.0 / 18000.0, -fk) // Sweep LPF down from 18 kHz to 110 Hz
                : 25.0 * Math.Pow(4800.0 / 25.0, fk);       // Sweep HPF up from 25 Hz to 4.8 kHz

            double f = Math.Clamp(2.0 * Math.Sin(Math.PI * Math.Min(cutoffHz, outputRate * 0.42) / outputRate), 0.005, 0.88);
            const double q = 0.68; // Warm DJ filter resonance

            deck.SvfLowL += f * deck.SvfBandL;
            double highL = sL - deck.SvfLowL - q * deck.SvfBandL;
            deck.SvfBandL += f * highL;

            deck.SvfLowR += f * deck.SvfBandR;
            double highR = sR - deck.SvfLowR - q * deck.SvfBandR;
            deck.SvfBandR += f * highR;

            if (fk < 0.0)
            {
                sL = deck.SvfLowL;
                sR = deck.SvfLowR;
            }
            else
            {
                sL = highL;
                sR = highR;
            }
        }

        // 5. 3-Slot Deck FX Rack
        // Slot 0: Stereo Flanger
        if (deck.FxEnabled[0])
        {
            double depth = Math.Clamp(deck.FxDepth[0], 0.05, 1.0);
            deck.FlangerLfoPhase = (deck.FlangerLfoPhase + (0.45 / outputRate)) % 1.0;
            double lfo = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * deck.FlangerLfoPhase);
            double delaySamples = (0.0008 + 0.0052 * lfo) * outputRate;
            int dInt = (int)delaySamples;
            double frac = delaySamples - dInt;

            int rIdx1 = (deck.FlangerWritePos - dInt + 1024) & 1023;
            int rIdx2 = (rIdx1 - 1 + 1024) & 1023;
            double delL = deck.FlangerBufL[rIdx1] * (1.0 - frac) + deck.FlangerBufL[rIdx2] * frac;
            double delR = deck.FlangerBufR[rIdx1] * (1.0 - frac) + deck.FlangerBufR[rIdx2] * frac;

            deck.FlangerBufL[deck.FlangerWritePos] = (float)Math.Clamp(sL + delL * 0.52 * depth, -2.0, 2.0);
            deck.FlangerBufR[deck.FlangerWritePos] = (float)Math.Clamp(sR + delR * 0.52 * depth, -2.0, 2.0);
            deck.FlangerWritePos = (deck.FlangerWritePos + 1) & 1023;

            sL = sL * (1.0 - 0.45 * depth) + delL * (0.72 * depth);
            sR = sR * (1.0 - 0.45 * depth) + delR * (0.72 * depth);
        }

        // Slot 1: Stereo Hall Reverb / Beat Echo
        if (deck.FxEnabled[1])
        {
            double depth = Math.Clamp(deck.FxDepth[1], 0.05, 1.0);
            int wPos = deck.ReverbWritePos;
            int tap1 = (wPos - (int)(outputRate * 0.089) + 32768) & 32767;
            int tap2 = (wPos - (int)(outputRate * 0.173) + 32768) & 32767;
            int tap3 = (wPos - (int)(outputRate * 0.281) + 32768) & 32767;

            double revL = (deck.ReverbBufL[tap1] * 0.55 + deck.ReverbBufR[tap2] * 0.35 + deck.ReverbBufL[tap3] * 0.25);
            double revR = (deck.ReverbBufR[tap1] * 0.55 + deck.ReverbBufL[tap2] * 0.35 + deck.ReverbBufR[tap3] * 0.25);

            deck.ReverbBufL[wPos] = (float)Math.Clamp(sL * 0.65 + revL * 0.48, -2.0, 2.0);
            deck.ReverbBufR[wPos] = (float)Math.Clamp(sR * 0.65 + revR * 0.48, -2.0, 2.0);
            deck.ReverbWritePos = (wPos + 1) & 32767;

            sL += revL * depth * 0.78;
            sR += revR * depth * 0.78;
        }

        // Slot 2: Wah-Wah Swept Resonant Band-Pass
        if (deck.FxEnabled[2])
        {
            double depth = Math.Clamp(deck.FxDepth[2], 0.05, 1.0);
            double beatHz = Math.Max(1.0, deck.EffectiveBpm / 60.0);
            deck.WahPhase = (deck.WahPhase + (beatHz / outputRate)) % 1.0;
            double wahLfo = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * deck.WahPhase);
            double wahFreq = 320.0 + 2200.0 * wahLfo;
            double wf = Math.Clamp(2.0 * Math.Sin(Math.PI * wahFreq / outputRate), 0.02, 0.75);

            deck.WahLowL += wf * deck.WahBandL;
            double whL = sL - deck.WahLowL - 0.35 * deck.WahBandL;
            deck.WahBandL += wf * whL;

            deck.WahLowR += wf * deck.WahBandR;
            double whR = sR - deck.WahLowR - 0.35 * deck.WahBandR;
            deck.WahBandR += wf * whR;

            sL = sL * (1.0 - depth * 0.75) + deck.WahBandL * (depth * 1.65);
            sR = sR * (1.0 - depth * 0.75) + deck.WahBandR * (depth * 1.65);
        }

        // Stems FX Rhythmic Beat Gate (1/8 note DJ Chopper synced to track beat grid)
        if (deck.PadStemsFxGateActive)
        {
            double curSec = deck.PlayheadFrame / Math.Max(1, track.SampleRate);
            double eighthPeriod = 30.0 / Math.Max(40.0, deck.BaseBpm);
            double gatePhase = ((curSec - track.FirstBeatSec) / eighthPeriod) % 1.0;
            if (gatePhase < 0) gatePhase += 1.0;
            if (gatePhase > 0.52)
            {
                sL *= 0.08;
                sR *= 0.08;
            }
        }

        // 6. Channel Volume Fader
        float chVol = (float)Math.Clamp(deck.ChannelVolume, 0.0, 1.0);
        outL = (float)(sL * chVol);
        outR = (float)(sR * chVol);
    }

    private static void SampleHermiteStereo(float[] left, float[] right, int totalFrames, double pos, out float outL, out float outR)
    {
        int i1 = (int)pos;
        float frac = (float)(pos - i1);
        int i0 = Math.Max(0, i1 - 1);
        int i2 = Math.Min(totalFrames - 1, i1 + 1);
        int i3 = Math.Min(totalFrames - 1, i1 + 2);

        outL = Hermite4(frac, left[i0], left[i1], left[i2], left[i3]);
        outR = Hermite4(frac, right[i0], right[i1], right[i2], right[i3]);
    }

    private static float Hermite4(float frac, float y0, float y1, float y2, float y3)
    {
        float c0 = y1;
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * frac + c2) * frac + c1) * frac + c0;
    }

    private static float SoftClipSample(float x)
    {
        if (x > 0.92f)
            return 0.92f + 0.08f * MathF.Tanh((x - 0.92f) * 8.0f);
        if (x < -0.92f)
            return -0.92f + 0.08f * MathF.Tanh((x + 0.92f) * 8.0f);
        return x;
    }

    // =========================================================================
    // SNAPSHOT GENERATION FOR 60 FPS UI
    // =========================================================================

    public DjMixerSnapshot GetSnapshot()
    {
        return new DjMixerSnapshot
        {
            DeckA = BuildDeckSnapshot(_deckA),
            DeckB = BuildDeckSnapshot(_deckB),
            Crossfader = _crossfader,
            IsAutoCrossfading = _isAutoCrossfading,
            MasterPeakLeft = _masterPeakL,
            MasterPeakRight = _masterPeakR,
            OutputSampleRate = _outputSampleRate,
            IsEngineRunning = _isRunning,
            StatusMessage = _statusMessage
        };
    }

    private static DjDeckSnapshot BuildDeckSnapshot(DjDeckChannelState deck)
    {
        var t = deck.Track;
        if (t == null || t.TotalFrames <= 0)
        {
            return new DjDeckSnapshot
            {
                DeckId = deck.DeckId,
                IsLoaded = false,
                PitchPercent = deck.PitchPercent,
                PitchRangePercent = deck.PitchRangePercent,
                MasterTempoEnabled = deck.MasterTempoEnabled,
                VinylModeEnabled = deck.VinylModeEnabled,
                SlipModeEnabled = deck.SlipModeEnabled,
                LoopBeats = deck.LoopBeats,
                HeadphoneCueEnabled = deck.HeadphoneCueEnabled
            };
        }

        double totalSec = t.DurationSec;
        double curSec = Math.Clamp(deck.PlayheadFrame / t.SampleRate, 0.0, totalSec);
        double slipSec = Math.Clamp(deck.SlipPlayheadFrame / t.SampleRate, 0.0, totalSec);

        double[] hotNorms = new double[4];
        for (int i = 0; i < 4; i++)
        {
            hotNorms[i] = t.HotCueFrames[i] >= 0 ? Math.Clamp((double)t.HotCueFrames[i] / t.TotalFrames, 0.0, 1.0) : -1.0;
        }

        return new DjDeckSnapshot
        {
            DeckId = deck.DeckId,
            IsLoaded = true,
            IsPlaying = deck.IsPlaying,
            IsScratching = deck.IsScratching,
            Title = t.Title,
            Artist = t.Artist,
            Remix = t.Remix,
            MusicalKey = t.MusicalKey,
            BaseBpm = deck.BaseBpm,
            EffectiveBpm = deck.EffectiveBpm,
            FirstBeatSec = t.FirstBeatSec,
            PitchPercent = deck.PitchPercent,
            PitchRangePercent = deck.PitchRangePercent,
            MasterTempoEnabled = deck.MasterTempoEnabled,
            VinylModeEnabled = deck.VinylModeEnabled,
            SlipModeEnabled = deck.SlipModeEnabled,
            CurrentSec = curSec,
            SlipSec = slipSec,
            TotalSec = totalSec,
            PlatterAngleDeg = deck.PlatterAngleDeg,
            CueNorm = new doubleCueInfo(Math.Clamp((double)t.CueFrame / t.TotalFrames, 0.0, 1.0), true),
            HotCueNorms = hotNorms,
            LoopActive = deck.LoopActive,
            LoopBeats = deck.LoopBeats,
            LoopStartSec = deck.LoopStartFrame / t.SampleRate,
            LoopEndSec = deck.LoopEndFrame / t.SampleRate,
            PeakLeft = deck.PeakLeft,
            PeakRight = deck.PeakRight,
            HeadphoneCueEnabled = deck.HeadphoneCueEnabled
        };
    }

    // =========================================================================
    // LOW-LATENCY WASAPI SHARED-MODE RENDER THREAD
    // =========================================================================

    public void EnsureEngineRunning()
    {
        if (_isRunning)
            return;

        lock (_engineLock)
        {
            if (_isRunning)
                return;

            _stopRequested = false;
            _renderThread = new Thread(WasapiRenderThreadProc)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "SpectraWASAPIDjMixer"
            };
            _renderThread.Start();
        }
    }

    public void StopEngine()
    {
        _stopRequested = true;
        var t = _renderThread;
        if (t != null && t.IsAlive)
        {
            t.Join(400);
        }
        _renderThread = null;
        _isRunning = false;
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    private void WasapiRenderThreadProc()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;
        IntPtr pMixFormat = IntPtr.Zero;

        // Enable 1 ms high-precision Windows scheduler period while DJ audio engine is actively streaming
        // so Thread.Sleep(2) wakes in ~2 ms instead of 15.625 ms and the WASAPI buffer NEVER underruns!
        timeBeginPeriod(1);
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = -1;
            if (!string.IsNullOrWhiteSpace(_masterEndpointId))
            {
                hr = enumerator.GetDevice(_masterEndpointId, out device);
            }
            if (hr < 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device); // eRender, eMultimedia
            }
            if (hr < 0 || device == null)
                return;

            Guid iidClient = IidAudioClient;
            hr = device.Activate(ref iidClient, 23, IntPtr.Zero, out object clientObj);
            if (hr < 0 || clientObj == null)
                return;

            audioClient = (IAudioClient)clientObj;
            hr = audioClient.GetMixFormat(out pMixFormat);
            if (hr < 0 || pMixFormat == IntPtr.Zero)
                return;

            ushort formatTag = (ushort)Marshal.ReadInt16(pMixFormat, 0);
            int channels = Marshal.ReadInt16(pMixFormat, 2);
            int sampleRate = Marshal.ReadInt32(pMixFormat, 4);
            int blockAlign = Marshal.ReadInt16(pMixFormat, 12);
            int bitsPerSample = Marshal.ReadInt16(pMixFormat, 14);
            int cbSize = Marshal.ReadInt16(pMixFormat, 16);

            bool isFloat = formatTag == 3;
            if (formatTag == 0xFFFE && cbSize >= 22)
            {
                byte[] guidBytes = new byte[16];
                Marshal.Copy(pMixFormat + 24, guidBytes, 0, 16);
                var subFormat = new Guid(guidBytes);
                isFloat = subFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            }

            _outputSampleRate = sampleRate > 0 ? sampleRate : 48000;

            // 80 ms shared-mode WASAPI hardware buffer (800,000 * 100ns units)
            hr = audioClient.Initialize(0, 0, 800_000, 0, pMixFormat, IntPtr.Zero);
            if (hr < 0)
                return;

            hr = audioClient.GetBufferSize(out uint bufferFrames);
            if (hr < 0 || bufferFrames == 0)
                return;

            Guid iidRender = IidAudioRenderClient;
            hr = audioClient.GetService(ref iidRender, out object renderObj);
            if (hr < 0 || renderObj == null)
                return;

            renderClient = (IAudioRenderClient)renderObj;

            // Pre-fill initial 40 ms of audio before calling Start() so playback begins with zero startup underrun
            int initialFrames = Math.Min((int)bufferFrames, (_outputSampleRate * 40) / 1000);
            float[] leftBuf = new float[bufferFrames];
            float[] rightBuf = new float[bufferFrames];
            byte[] rawOut = new byte[bufferFrames * blockAlign];

            if (initialFrames > 0 && renderClient.GetBuffer((uint)initialFrames, out IntPtr pInitData) >= 0 && pInitData != IntPtr.Zero)
            {
                MixStereoBuffer(leftBuf, rightBuf, initialFrames, _outputSampleRate);
                PackOutputFrames(leftBuf, rightBuf, initialFrames, channels, bitsPerSample, blockAlign, isFloat, rawOut);
                Marshal.Copy(rawOut, 0, pInitData, initialFrames * blockAlign);
                renderClient.ReleaseBuffer((uint)initialFrames, 0);
            }

            hr = audioClient.Start();
            if (hr < 0)
                return;

            _isRunning = true;
            int idleCounter = 0;
            int targetFill = Math.Min((int)bufferFrames, Math.Max(1024, (_outputSampleRate * 50) / 1000));
            int minWriteBatch = Math.Max(64, (_outputSampleRate * 3) / 1000);

            while (!_stopRequested)
            {
                // Auto-sleep at 0% CPU if neither deck has played or scratched for > 3 seconds
                if (!IsAnyDeckPlaying && !_isAutoCrossfading)
                {
                    idleCounter++;
                    if (idleCounter > 800) // ~3 seconds idle
                        break;
                }
                else
                {
                    idleCounter = 0;
                }

                hr = audioClient.GetCurrentPadding(out uint paddingFrames);
                if (hr < 0)
                    break;

                int framesAvailable = (int)bufferFrames - (int)paddingFrames;
                int framesToWrite = Math.Min(framesAvailable, targetFill - (int)paddingFrames);

                if (framesToWrite < minWriteBatch)
                {
                    Thread.Sleep(2);
                    continue;
                }

                MixStereoBuffer(leftBuf, rightBuf, framesToWrite, _outputSampleRate);

                hr = renderClient.GetBuffer((uint)framesToWrite, out IntPtr pData);
                if (hr < 0 || pData == IntPtr.Zero)
                {
                    Thread.Sleep(2);
                    continue;
                }

                int totalBytes = framesToWrite * blockAlign;
                if (rawOut.Length < totalBytes)
                    rawOut = new byte[totalBytes];

                PackOutputFrames(leftBuf, rightBuf, framesToWrite, channels, bitsPerSample, blockAlign, isFloat, rawOut);
                Marshal.Copy(rawOut, 0, pData, totalBytes);
                renderClient.ReleaseBuffer((uint)framesToWrite, 0);
            }

            audioClient.Stop();
        }
        catch
        {
            // Clean exit on device change
        }
        finally
        {
            timeEndPeriod(1);
            _isRunning = false;
            if (pMixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pMixFormat);
            if (renderClient != null) Marshal.ReleaseComObject(renderClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void PackOutputFrames(
        float[] left,
        float[] right,
        int frames,
        int channels,
        int bitsPerSample,
        int blockAlign,
        bool isFloat,
        byte[] dest)
    {
        int bytesPerSample = Math.Max(2, bitsPerSample / 8);
        Array.Clear(dest, 0, frames * blockAlign);

        for (int i = 0; i < frames; i++)
        {
            int frameOff = i * blockAlign;
            WriteSampleValue(dest, frameOff, left[i], bytesPerSample, isFloat);
            if (channels >= 2)
            {
                WriteSampleValue(dest, frameOff + bytesPerSample, right[i], bytesPerSample, isFloat);
            }
        }
    }

    private static void WriteSampleValue(byte[] dest, int offset, float sample, int bytesPerSample, bool isFloat)
    {
        float clamped = Math.Clamp(sample, -1.0f, 1.0f);
        if (isFloat && bytesPerSample == 4)
        {
            BitConverter.TryWriteBytes(dest.AsSpan(offset, 4), clamped);
        }
        else if (bytesPerSample == 2)
        {
            short s16 = (short)Math.Round(clamped * 32767f);
            BitConverter.TryWriteBytes(dest.AsSpan(offset, 2), s16);
        }
        else if (bytesPerSample == 3)
        {
            int s24 = (int)Math.Round(clamped * 8388607f);
            dest[offset] = (byte)(s24 & 0xFF);
            dest[offset + 1] = (byte)((s24 >> 8) & 0xFF);
            dest[offset + 2] = (byte)((s24 >> 16) & 0xFF);
        }
        else if (bytesPerSample == 4)
        {
            int s32 = (int)Math.Round(clamped * 2147483647.0);
            BitConverter.TryWriteBytes(dest.AsSpan(offset, 4), s32);
        }
    }

    public void Dispose()
    {
        StopEngine();
    }
}
