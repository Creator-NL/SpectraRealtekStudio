using System;
using System.Collections.Generic;

namespace SpectraRealtekStudio.Core.Models;

public enum DjDeckId
{
    DeckA = 0,
    DeckB = 1
}

public enum DjFxType
{
    Flanger = 0,
    Reverb = 1,
    WahWah = 2,
    Echo = 3,
    Phaser = 4,
    Bitcrush = 5
}

public enum DjCrossfaderCurve
{
    ConstantPower = 0,
    SmoothLinear = 1,
    SharpCut = 2
}

/// <summary>
/// Represents a track row in the bottom DJ Local Music Browser &amp; Crate table.
/// </summary>
public sealed class DjLibraryTrackItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Remix { get; set; } = string.Empty;
    public double DurationSec { get; set; }
    public string LengthText { get; set; } = "--:--";
    public double Bpm { get; set; }
    public string BpmText => Bpm > 1.0 ? $"{Bpm:0.0}" : "—";
    public string MusicalKey { get; set; } = "—";
    public string TrackType { get; set; } = "audio";
    public bool IsAnalyzed { get; set; }
}

/// <summary>
/// Persisted BPM, Key &amp; duration metadata cache entry so tracks in a folder display BPM/Key immediately on revisit.
/// </summary>
public sealed class DjTrackCacheEntry
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Remix { get; set; } = string.Empty;
    public double DurationSec { get; set; }
    public double Bpm { get; set; }
    public double FirstBeatSec { get; set; }
    public string MusicalKey { get; set; } = string.Empty;
}

/// <summary>
/// Fully decoded stereo 32-bit float PCM track in memory with precomputed beat-colored waveform pyramids,
/// automatic BPM &amp; beat-grid offset, and detected musical key.
/// </summary>
public sealed class DjLoadedTrack
{
    public const int OverviewBinCount = 360;
    public const int BeatEnvelopeRateHz = 100; // 100 peaks per second (10 ms resolution) for 60 FPS scrolling waveforms

    public string FilePath { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Remix { get; init; } = string.Empty;
    public int SampleRate { get; init; } = 48000;
    public int TotalFrames { get; init; }
    public double DurationSec => SampleRate > 0 ? (double)TotalFrames / SampleRate : 0.0;

    public float[] Left { get; init; } = Array.Empty<float>();
    public float[] Right { get; init; } = Array.Empty<float>();

    public double DetectedBpm { get; set; } = 128.0;
    public double FirstBeatSec { get; set; }
    public string MusicalKey { get; set; } = "Am";

    // Mini overview bar envelope (360 bins across entire song)
    public float[] OverviewPeaks { get; init; } = Array.Empty<float>();
    public float[] OverviewBassRatio { get; init; } = Array.Empty<float>();

    // High-resolution 100 Hz beat waveform envelope for top dual scrolling display
    public float[] BeatPeaks { get; init; } = Array.Empty<float>();
    public float[] BeatBassRatio { get; init; } = Array.Empty<float>();

    public long CueFrame { get; set; }
    public long[] HotCueFrames { get; } = new long[] { -1, -1, -1, -1 };
}

public sealed class DjDeckSnapshot
{
    public DjDeckId DeckId { get; init; }
    public bool IsLoaded { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsScratching { get; init; }
    public string Title { get; init; } = "No Track Loaded";
    public string Artist { get; init; } = "Load a song from the music browser below";
    public string Remix { get; init; } = string.Empty;
    public string MusicalKey { get; init; } = "—";
    public double BaseBpm { get; init; }
    public double EffectiveBpm { get; init; }
    public double FirstBeatSec { get; init; }
    public double PitchPercent { get; init; }
    public double PitchRangePercent { get; init; } = 16.0;
    public bool MasterTempoEnabled { get; init; } = true;
    public bool VinylModeEnabled { get; init; } = true;
    public bool SlipModeEnabled { get; init; }
    public double CurrentSec { get; init; }
    public double SlipSec { get; init; }
    public double TotalSec { get; init; }
    public double RemainingSec => Math.Max(0.0, TotalSec - CurrentSec);
    public double PlayheadNorm => TotalSec > 0.001 ? Math.Clamp(CurrentSec / TotalSec, 0.0, 1.0) : 0.0;
    public double SlipNorm => TotalSec > 0.001 ? Math.Clamp(SlipSec / TotalSec, 0.0, 1.0) : 0.0;
    public double PlatterAngleDeg { get; init; }

    public doubleCueInfo CueNorm { get; init; }
    public double[] HotCueNorms { get; init; } = new double[] { -1, -1, -1, -1 };

    public bool LoopActive { get; init; }
    public double LoopBeats { get; init; } = 4.0;
    public double LoopStartSec { get; init; }
    public double LoopEndSec { get; init; }
    public double LoopStartNorm => TotalSec > 0.001 ? Math.Clamp(LoopStartSec / TotalSec, 0.0, 1.0) : 0.0;
    public double LoopEndNorm => TotalSec > 0.001 ? Math.Clamp(LoopEndSec / TotalSec, 0.0, 1.0) : 0.0;

    public float PeakLeft { get; init; }
    public float PeakRight { get; init; }
    public bool HeadphoneCueEnabled { get; init; }
}

public readonly record struct doubleCueInfo(double NormPosition, bool IsSet);

public sealed class DjMixerSnapshot
{
    public DjDeckSnapshot DeckA { get; init; } = new() { DeckId = DjDeckId.DeckA };
    public DjDeckSnapshot DeckB { get; init; } = new() { DeckId = DjDeckId.DeckB };
    public double Crossfader { get; init; } // -1.0 (Deck A) .. 0.0 (Center) .. +1.0 (Deck B)
    public bool IsAutoCrossfading { get; init; }
    public float MasterPeakLeft { get; init; }
    public float MasterPeakRight { get; init; }
    public int OutputSampleRate { get; init; } = 48000;
    public bool IsEngineRunning { get; init; }
    public string StatusMessage { get; init; } = "DJ Engine Ready";
}
