using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;
using Path = System.IO.Path;

namespace SpectraRealtekStudio.App;

public partial class MainWindow
{
    private DispatcherTimer? _djTimer;
    private bool _djIsActiveTab;
    private bool _djCrateCollapsed;
    private GridLength _djSavedCrateHeight = new(1, GridUnitType.Star);

    // 60 FPS WriteableBitmaps for Top Dual Stacked Waveforms, Mini Overviews, Vinyl Platters & Center LED VU Meters
    private WriteableBitmap? _djTopWaveBitmap;
    private uint[] _djTopWavePixels = Array.Empty<uint>();
    private int _djTopWaveW;
    private int _djTopWaveH;

    private WriteableBitmap? _djOvBitmapA;
    private uint[] _djOvPixelsA = Array.Empty<uint>();
    private int _djOvWA, _djOvHA;

    private WriteableBitmap? _djOvBitmapB;
    private uint[] _djOvPixelsB = Array.Empty<uint>();
    private int _djOvWB, _djOvHB;

    private WriteableBitmap? _djPlatterBitmapA;
    private readonly uint[] _djPlatterPixelsA = new uint[116 * 116];
    private WriteableBitmap? _djPlatterBitmapB;
    private readonly uint[] _djPlatterPixelsB = new uint[116 * 116];

    private WriteableBitmap? _djVuBitmap;
    private readonly uint[] _djVuPixels = new uint[24 * 114];

    // Interactive mouse drag states (Top Waveform Scrub, 2-Point BPM Calibrator, Draggable Beat Markers, Platter Scratch, Rotary Mixer Knobs)
    private bool _djDraggingTopWave;
    private DjDeckId _djTopWaveDragDeck;
    private Point _djTopWaveLastPt;
    private DateTime _djLastTopWaveClickUtc = DateTime.MinValue;
    private Point _djLastTopWaveClickPt;

    // Double-click Beat-Grid Marker Unlock & Drag state
    private DjDeckId? _djUnlockedBeatDeck;
    private int _djUnlockedBeatIdx;
    private bool _djDraggingUnlockedBeat;

    // Manual 2-Point BPM Calibrator state (Point 1 & Point 2 across 1 / 4 / 8 / 16 beats)
    private DjDeckId? _djTwoPointDeck;
    private double _djTwoPointSec1;
    private double _djTwoPointSec2;
    private int _djTwoPointBeatSpan = 4;
    private int _djDraggingTwoPointHandle; // 0 = none, 1 = Point 1, 2 = Point 2

    // Top Dual Beat-Waveform Zoom Factor (1.0 = default 9.0s window; 0.4x wide overview up to 16.0x sample-accurate view!)
    private double _djWaveZoomFactor = 1.0;

    // Ultra-fine 0.01% Pitch Fader thumb-drag & wheel state (drift-proof delta dragging + grab glow)
    private bool _suppressPitchSliderSync;
    private bool _djFineDraggingPitchA;
    private bool _djPitchDragMovedA;
    private Point _djPitchDragStartPtA;
    private double _djPitchDragStartValA;
    private bool _djFineDraggingPitchB;
    private bool _djPitchDragMovedB;
    private Point _djPitchDragStartPtB;
    private double _djPitchDragStartValB;

    private bool _djScratchingPlatterA;
    private double _djPlatterLastAngleA;
    private DateTime _djPlatterLastMoveUtcA;

    private bool _djScratchingPlatterB;
    private double _djPlatterLastAngleB;
    private DateTime _djPlatterLastMoveUtcB;

    private Canvas? _activeDjKnob;
    private Point _djKnobDragStartPt;
    private double _djKnobDragStartNorm;
    private DateTime _djLastKnobClickUtc = DateTime.MinValue;

    // Bottom Local Music Browser & Crate state
    private readonly List<DjLibraryTrackItem> _djAllFolderTracks = new();
    private readonly ObservableCollection<DjLibraryTrackItem> _djFilteredTracks = new();
    private CancellationTokenSource? _djAnalyzeCts;
    private string _djCurrentFolderPath = string.Empty;
    private volatile bool _djLoadingDeckA;
    private volatile bool _djLoadingDeckB;
    private string _djLoadingFileA = string.Empty;
    private string _djLoadingFileB = string.Empty;

    private void InitializeDjStudio()
    {
        GridDjTracks.ItemsSource = _djFilteredTracks;

        bool prev = _suppressUiEvents;
        _suppressUiEvents = true;
        ChkDjLoadAsIs.IsChecked = _appSettings.DjLoadAsIsNativeQuality;
        _studio.DjMixer.PureNativeQualityMode = _appSettings.DjLoadAsIsNativeQuality;
        _studio.DjMixer.MasterGainDb = _appSettings.DjMasterGainDb;
        ApplyDjTopToolbarCollapsedState(_appSettings.DjTopToolbarCollapsed);
        UpdateToggleButtonVisual(BtnDjMtA, _studio.DjMixer.DeckA.MasterTempoEnabled, "Cyan");
        UpdateToggleButtonVisual(BtnDjMtB, _studio.DjMixer.DeckB.MasterTempoEnabled, "Red");
        _suppressUiEvents = prev;

        _djPlatterBitmapA = new WriteableBitmap(116, 116, 96, 96, PixelFormats.Bgra32, null);
        ImgDjPlatterA.Source = _djPlatterBitmapA;

        _djPlatterBitmapB = new WriteableBitmap(116, 116, 96, 96, PixelFormats.Bgra32, null);
        ImgDjPlatterB.Source = _djPlatterBitmapB;

        _djVuBitmap = new WriteableBitmap(24, 114, 96, 96, PixelFormats.Bgra32, null);
        ImgDjVuMeters.Source = _djVuBitmap;

        _djTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
        };
        _djTimer.Tick += DjTimer_Tick;

        UpdateAllDjKnobsVisual();
        RenderDjPlatter(DjDeckId.DeckA, 0.0, false, false);
        RenderDjPlatter(DjDeckId.DeckB, 0.0, false, false);
        RenderDjVuMeters(0f, 0f);
        InitializeDjFolderTree();
    }

    private void OnDjTabSelectionChanged(bool isDjTab)
    {
        if (isDjTab == _djIsActiveTab)
            return;

        _djIsActiveTab = isDjTab;
        if (isDjTab)
        {
            _studio.DjMixer.SetMasterEndpoint(_activePlaybackEndpoint?.EndpointId);
            EnsureDjBitmaps();
            UpdateAllDjKnobsVisual();
            _djTimer?.Start();
            RenderDjStaticOverviews();
        }
        else
        {
            // Keep DJ mixer playing in the background across tabs! Only pause UI drawing timer if not playing.
            if (!_studio.DjMixer.IsAnyDeckPlaying)
            {
                _djTimer?.Stop();
            }
        }
    }

    private void OnActivePlaybackEndpointChangedForDj()
    {
        _studio.DjMixer.SetMasterEndpoint(_activePlaybackEndpoint?.EndpointId);
    }

    private void OnDjWindowMinimized()
    {
        // Audio continues playing seamlessly when minimized; pause 60 FPS UI bitmap drawing while minimized
        _djTimer?.Stop();
    }

    private void OnDjWindowRestored()
    {
        if (_djIsActiveTab || _studio.DjMixer.IsAnyDeckPlaying)
        {
            _djTimer?.Start();
        }
    }

    private void ShutdownDjOnAppClose()
    {
        _djAnalyzeCts?.Cancel();
        _djTimer?.Stop();
        _studio.DjMixer.StopEngine();
    }

    // =========================================================================
    // 60 FPS RENDERING LOOP (STACKED BEAT WAVEFORMS, PLATTERS, VU & TELEMETRY)
    // =========================================================================

    private void DjTimer_Tick(object? sender, EventArgs e)
    {
        var snap = _studio.DjMixer.GetSnapshot();

        // Update bottom status bar live badge across all tabs when DJ mix is playing
        if (snap.DeckA.IsPlaying || snap.DeckB.IsPlaying)
        {
            string activeInfo = (snap.DeckA.IsPlaying && snap.DeckB.IsPlaying)
                ? $"🎧 DJ MIX LIVE (A: {snap.DeckA.EffectiveBpm:0.0} | B: {snap.DeckB.EffectiveBpm:0.0} BPM)"
                : snap.DeckA.IsPlaying
                    ? $"🎧 DJ DECK A LIVE ({snap.DeckA.EffectiveBpm:0.0} BPM)"
                    : $"🎧 DJ DECK B LIVE ({snap.DeckB.EffectiveBpm:0.0} BPM)";
            TxtDjLiveBadge.Text = activeInfo;
            TxtDjLiveBadge.Visibility = Visibility.Visible;
        }
        else
        {
            TxtDjLiveBadge.Visibility = Visibility.Collapsed;
            if (!_djIsActiveTab)
            {
                _djTimer?.Stop();
                return;
            }
        }

        if (!_djIsActiveTab || WindowState == WindowState.Minimized || !IsVisible)
            return;

        // Sync crossfader slider during Auto-Mix transitions
        if (snap.IsAutoCrossfading)
        {
            bool prev = _suppressUiEvents;
            _suppressUiEvents = true;
            SldDjCrossfader.Value = snap.Crossfader;
            _suppressUiEvents = prev;
        }

        // Sync ● REC MIX button with Tab 8 Recorder state
        if (_studio.Recorder.IsRecording)
        {
            if (_studio.Recorder.IsWaitingForSound)
            {
                BtnDjRecMix.Content = "◉ REC ARMED";
            }
            else
            {
                var recSnap = _studio.Recorder.GetSnapshot();
                BtnDjRecMix.Content = $"■ STOP REC ({(int)recSnap.Elapsed.TotalMinutes:00}:{recSnap.Elapsed.Seconds:00})";
            }
        }
        else
        {
            BtnDjRecMix.Content = "● REC MIX";
        }

        UpdateDeckTelemetryUi(snap.DeckA, isDeckA: true);
        UpdateDeckTelemetryUi(snap.DeckB, isDeckA: false);

        RenderDjTopWaveforms(snap);
        RenderDjOverview(DjDeckId.DeckA, snap.DeckA);
        RenderDjOverview(DjDeckId.DeckB, snap.DeckB);
        RenderDjPlatter(DjDeckId.DeckA, snap.DeckA.PlatterAngleDeg, snap.DeckA.IsPlaying, snap.DeckA.IsScratching);
        RenderDjPlatter(DjDeckId.DeckB, snap.DeckB.PlatterAngleDeg, snap.DeckB.IsPlaying, snap.DeckB.IsScratching);

        float vuA = Math.Max(snap.DeckA.PeakLeft, snap.DeckA.PeakRight);
        float vuB = Math.Max(snap.DeckB.PeakLeft, snap.DeckB.PeakRight);
        RenderDjVuMeters(vuA, vuB);
    }

    private void UpdateDeckTelemetryUi(DjDeckSnapshot d, bool isDeckA)
    {
        if (isDeckA)
        {
            if (_djLoadingDeckA)
            {
                TxtDjTitleA.Text = $"⏳ Loading {_djLoadingFileA}...";
                TxtDjArtistA.Text = "Decoding audio, waveform pyramid, BPM & musical key...";
                return;
            }
            TxtDjTitleA.Text = d.Title;
            TxtDjArtistA.Text = string.IsNullOrWhiteSpace(d.Remix) ? d.Artist : $"{d.Artist} • {d.Remix}";
            TxtDjKeyA.Text = d.MusicalKey;
            TxtDjBpmA.Text = d.IsLoaded ? $"{d.EffectiveBpm:0.00}" : "128.00";
            TxtDjElapsedA.Text = FormatDjTime(d.CurrentSec);
            TxtDjRemainA.Text = $"-{FormatDjTime(d.RemainingSec)}";
            BtnDjPlayA.Content = d.IsPlaying ? "❚❚ PAUSE" : "▶ PLAY";
            string loopTextA = d.LoopBeats < 1.0 ? "1/2 Beat" : $"{d.LoopBeats:0.#} Beats";
            BtnDjLoopToggleA.Content = d.LoopActive ? $"🔁 {loopTextA}" : loopTextA;
            BtnDjLoopToggleA.Background = d.LoopActive ? new SolidColorBrush(Color.FromRgb(16, 52, 38)) : new SolidColorBrush(Color.FromRgb(26, 33, 64));
            BtnDjLoopToggleA.BorderBrush = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Line");
            BtnDjLoopToggleA.Foreground = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Text");
            TxtDjWaveBadgeA.Text = d.IsLoaded ? $"A • {d.Title} ({d.EffectiveBpm:0.00} BPM)" : "A • DECK 1";
        }
        else
        {
            if (_djLoadingDeckB)
            {
                TxtDjTitleB.Text = $"⏳ Loading {_djLoadingFileB}...";
                TxtDjArtistB.Text = "Decoding audio, waveform pyramid, BPM & musical key...";
                return;
            }
            TxtDjTitleB.Text = d.Title;
            TxtDjArtistB.Text = string.IsNullOrWhiteSpace(d.Remix) ? d.Artist : $"{d.Artist} • {d.Remix}";
            TxtDjKeyB.Text = d.MusicalKey;
            TxtDjBpmB.Text = d.IsLoaded ? $"{d.EffectiveBpm:0.00}" : "128.00";
            TxtDjElapsedB.Text = FormatDjTime(d.CurrentSec);
            TxtDjRemainB.Text = $"-{FormatDjTime(d.RemainingSec)}";
            BtnDjPlayB.Content = d.IsPlaying ? "❚❚ PAUSE" : "▶ PLAY";
            string loopTextB = d.LoopBeats < 1.0 ? "1/2 Beat" : $"{d.LoopBeats:0.#} Beats";
            BtnDjLoopToggleB.Content = d.LoopActive ? $"🔁 {loopTextB}" : loopTextB;
            BtnDjLoopToggleB.Background = d.LoopActive ? new SolidColorBrush(Color.FromRgb(16, 52, 38)) : new SolidColorBrush(Color.FromRgb(26, 33, 64));
            BtnDjLoopToggleB.BorderBrush = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Line");
            BtnDjLoopToggleB.Foreground = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Text");
            TxtDjWaveBadgeB.Text = d.IsLoaded ? $"B • {d.Title} ({d.EffectiveBpm:0.00} BPM)" : "B • DECK 2";
        }
    }

    private static string FormatDjTime(double sec)
    {
        if (sec < 0 || !double.IsFinite(sec)) sec = 0;
        int mins = (int)(sec / 60.0);
        double rem = sec - mins * 60.0;
        return $"{mins}:{rem:00.0}";
    }

    // =========================================================================
    // TOP DUAL STACKED 60 FPS BEAT-MATCHING WAVEFORM RENDERER
    // =========================================================================

    private void DjTopWaveformViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureDjBitmaps();
        if (_djIsActiveTab)
        {
            RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
        }
    }

    private void DjOverviewViewportA_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureDjBitmaps();
        RenderDjOverview(DjDeckId.DeckA, _studio.DjMixer.GetSnapshot().DeckA);
    }

    private void DjOverviewViewportB_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureDjBitmaps();
        RenderDjOverview(DjDeckId.DeckB, _studio.DjMixer.GetSnapshot().DeckB);
    }

    private void EnsureDjBitmaps()
    {
        int topW = Math.Max(320, (int)DjTopWaveformViewport.ActualWidth);
        int topH = Math.Max(64, (int)DjTopWaveformViewport.ActualHeight);
        if (_djTopWaveBitmap == null || topW != _djTopWaveW || topH != _djTopWaveH)
        {
            _djTopWaveW = topW;
            _djTopWaveH = topH;
            _djTopWavePixels = new uint[topW * topH];
            _djTopWaveBitmap = new WriteableBitmap(topW, topH, 96, 96, PixelFormats.Bgra32, null);
            ImgDjTopWaveforms.Source = _djTopWaveBitmap;
        }

        int ovWA = Math.Max(160, (int)DjOverviewViewportA.ActualWidth);
        int ovHA = Math.Max(18, (int)DjOverviewViewportA.ActualHeight);
        if (_djOvBitmapA == null || ovWA != _djOvWA || ovHA != _djOvHA)
        {
            _djOvWA = ovWA;
            _djOvHA = ovHA;
            _djOvPixelsA = new uint[ovWA * ovHA];
            _djOvBitmapA = new WriteableBitmap(ovWA, ovHA, 96, 96, PixelFormats.Bgra32, null);
            ImgDjOverviewA.Source = _djOvBitmapA;
        }

        int ovWB = Math.Max(160, (int)DjOverviewViewportB.ActualWidth);
        int ovHB = Math.Max(18, (int)DjOverviewViewportB.ActualHeight);
        if (_djOvBitmapB == null || ovWB != _djOvWB || ovHB != _djOvHB)
        {
            _djOvWB = ovWB;
            _djOvHB = ovHB;
            _djOvPixelsB = new uint[ovWB * ovHB];
            _djOvBitmapB = new WriteableBitmap(ovWB, ovHB, 96, 96, PixelFormats.Bgra32, null);
            ImgDjOverviewB.Source = _djOvBitmapB;
        }
    }

    private void RenderDjStaticOverviews()
    {
        EnsureDjBitmaps();
        var snap = _studio.DjMixer.GetSnapshot();
        RenderDjTopWaveforms(snap);
        RenderDjOverview(DjDeckId.DeckA, snap.DeckA);
        RenderDjOverview(DjDeckId.DeckB, snap.DeckB);
    }

    private void RenderDjTopWaveforms(DjMixerSnapshot snap)
    {
        if (_djTopWaveBitmap == null || _djTopWaveW < 16 || _djTopWaveH < 16)
            return;

        int w = _djTopWaveW;
        int h = _djTopWaveH;
        int midSplitY = h / 2;

        Array.Fill(_djTopWavePixels, 0xFF050811u);

        // Horizontal lane separator line
        int sepRow = midSplitY * w;
        for (int x = 0; x < w; x++)
        {
            _djTopWavePixels[sepRow + x] = 0xFF1F2B4Du;
        }

        // Render Deck A (Top Half: Cyan / Deep Blue Bass)
        RenderScrollingBeatLane(
            _studio.DjMixer.DeckA.Track,
            snap.DeckA,
            yTop: 2,
            yBottom: midSplitY - 2,
            bassCol: 0xFF125CE0u,
            midCol: 0xFF22E8FFu,
            highCol: 0xFFB8FAFFu,
            beatTickCol: 0xFF2B4D7Eu);

        // Render Deck B (Bottom Half: Crimson / Ruby Bass / Rose Highs)
        RenderScrollingBeatLane(
            _studio.DjMixer.DeckB.Track,
            snap.DeckB,
            yTop: midSplitY + 2,
            yBottom: h - 3,
            bassCol: 0xFFA41236u,
            midCol: 0xFFFF3366u,
            highCol: 0xFFFFA8C2u,
            beatTickCol: 0xFF6B223Eu);

        // Center Vertical White Beat-Alignment Playhead Line + Top/Bottom Triangle Pointers
        int cx = w / 2;
        for (int y = 0; y < h; y++)
        {
            _djTopWavePixels[y * w + cx] = 0xFFFFFFFFu;
            if (cx > 0 && (y < 5 || y >= h - 5 || Math.Abs(y - midSplitY) <= 2))
            {
                _djTopWavePixels[y * w + cx - 1] = 0xFFD0F4FFu;
                if (cx + 1 < w) _djTopWavePixels[y * w + cx + 1] = 0xFFD0F4FFu;
            }
        }

        _djTopWaveBitmap.WritePixels(new Int32Rect(0, 0, w, h), _djTopWavePixels, w * 4, 0);
    }

    private void RenderScrollingBeatLane(
        DjLoadedTrack? track,
        DjDeckSnapshot deckSnap,
        int yTop,
        int yBottom,
        uint bassCol,
        uint midCol,
        uint highCol,
        uint beatTickCol)
    {
        int w = _djTopWaveW;
        int laneH = Math.Max(8, yBottom - yTop);
        int centerY = yTop + laneH / 2;
        int halfH = Math.Max(4, laneH / 2 - 2);

        // Subtle center zero-line
        int cRow = centerY * w;
        for (int x = 0; x < w; x++)
        {
            _djTopWavePixels[cRow + x] = 0xFF111930u;
        }

        if (track == null || track.BeatPeaks.Length == 0)
            return;

        // Visible window: (9.0 / _djWaveZoomFactor) seconds across the viewport width (scaled by speed so beat grids line up visually when synced!)
        double speed = Math.Max(0.4, deckSnap.EffectiveBpm / Math.Max(40.0, deckSnap.BaseBpm));
        double visibleWindowSec = (9.0 / _djWaveZoomFactor) * speed;
        double secPerPixel = visibleWindowSec / w;
        int centerX = w / 2;

        // 1. Highlight Active Loop Region in translucent emerald green (with bright IN/OUT boundary lines!)
        if (deckSnap.LoopActive && deckSnap.LoopEndSec > deckSnap.LoopStartSec)
        {
            int lx0 = centerX + (int)Math.Round((deckSnap.LoopStartSec - deckSnap.CurrentSec) / secPerPixel);
            int lx1 = centerX + (int)Math.Round((deckSnap.LoopEndSec - deckSnap.CurrentSec) / secPerPixel);
            int cl0 = Math.Clamp(lx0, 0, w - 1);
            int cl1 = Math.Clamp(lx1, 0, w - 1);
            if (cl1 > cl0)
            {
                for (int y = yTop; y <= yBottom; y++)
                {
                    int row = y * w;
                    for (int x = cl0; x <= cl1; x++)
                    {
                        _djTopWavePixels[row + x] = 0xFF0B2A22u;
                    }
                    if (lx0 >= 0 && lx0 < w) _djTopWavePixels[row + lx0] = 0xFF22E87Au;
                    if (lx1 >= 0 && lx1 < w) _djTopWavePixels[row + lx1] = 0xFF22E87Au;
                }
            }
        }

        // 2. Draw Periodic Beat-Grid Ticks (every beat, with brighter 4-bar downbeat ticks)
        double beatPeriodSec = 60.0 / Math.Max(40.0, deckSnap.BaseBpm);
        double leftSec = deckSnap.CurrentSec - centerX * secPerPixel;
        double rightSec = deckSnap.CurrentSec + centerX * secPerPixel;
        int firstBeatIdx = (int)Math.Floor((leftSec - track.FirstBeatSec) / beatPeriodSec);
        int lastBeatIdx = (int)Math.Ceiling((rightSec - track.FirstBeatSec) / beatPeriodSec);

        for (int bIdx = firstBeatIdx; bIdx <= lastBeatIdx; bIdx++)
        {
            double beatSec = track.FirstBeatSec + bIdx * beatPeriodSec;
            if (beatSec < 0 || beatSec > track.DurationSec)
                continue;

            int bx = centerX + (int)Math.Round((beatSec - deckSnap.CurrentSec) / secPerPixel);
            if (bx < 1 || bx >= w - 1)
                continue;

            bool isDownbeat = ((bIdx % 4) + 4) % 4 == 0;
            uint tickCol = isDownbeat ? 0xFF4B6BA8u : beatTickCol;
            for (int y = yTop; y <= yBottom; y++)
            {
                if (isDownbeat || (y & 1) == 0)
                {
                    _djTopWavePixels[y * w + bx] = tickCol;
                }
            }
        }

        // 3. Draw Multi-Band Beat-Colored Scrolling Waveform Envelope
        // When zoomed in > 2.0x, sample raw audio frames directly so kick transients have razor-sharp sub-millisecond edges!
        var peaks = track.BeatPeaks;
        var bass = track.BeatBassRatio;
        int nBins = peaks.Length;
        double envRate = DjLoadedTrack.BeatEnvelopeRateHz;
        bool useRawSampleDetail = _djWaveZoomFactor > 2.0 && track.Left.Length > 0 && track.SampleRate > 0;
        var rawLeft = track.Left;
        var rawRight = track.Right;
        int totalRawFrames = Math.Min(rawLeft.Length, rawRight.Length);

        for (int x = 0; x < w; x++)
        {
            double tSec = deckSnap.CurrentSec + (x - centerX) * secPerPixel;
            if (tSec < 0.0 || tSec >= track.DurationSec)
                continue;

            double binPos = tSec * envRate;
            int b0 = Math.Clamp((int)binPos, 0, nBins - 1);
            int b1 = Math.Min(nBins - 1, b0 + 1);
            float frac = (float)Math.Clamp(binPos - b0, 0.0, 1.0);
            float bassRatio = bass[b0] * (1f - frac) + bass[b1] * frac;
            float amp;

            if (useRawSampleDetail && totalRawFrames > 2)
            {
                double fPos0 = tSec * track.SampleRate;
                double fPos1 = (tSec + secPerPixel) * track.SampleRate;
                int f0 = Math.Clamp((int)fPos0, 0, totalRawFrames - 1);
                int f1 = Math.Clamp((int)Math.Ceiling(fPos1), f0 + 1, totalRawFrames);
                int count = f1 - f0;
                if (count <= 2)
                {
                    int fNext = Math.Min(totalRawFrames - 1, f0 + 1);
                    float sFrac = (float)Math.Clamp(fPos0 - f0, 0.0, 1.0);
                    float sL = rawLeft[f0] * (1f - sFrac) + rawLeft[fNext] * sFrac;
                    float sR = rawRight[f0] * (1f - sFrac) + rawRight[fNext] * sFrac;
                    amp = Math.Clamp(Math.Max(Math.Abs(sL), Math.Abs(sR)), 0f, 1f);
                }
                else
                {
                    int step = Math.Max(1, count / 32);
                    float maxPeak = 0f;
                    for (int f = f0; f < f1; f += step)
                    {
                        float p = Math.Max(Math.Abs(rawLeft[f]), Math.Abs(rawRight[f]));
                        if (p > maxPeak) maxPeak = p;
                    }
                    amp = Math.Clamp(maxPeak, 0f, 1f);
                }
            }
            else
            {
                amp = peaks[b0] * (1f - frac) + peaks[b1] * frac;
            }

            int barHalf = (int)Math.Round(amp * halfH);
            if (barHalf < 1) continue;

            int coreHalf = (int)Math.Round(barHalf * (0.45f + 0.45f * bassRatio));
            int innerHalf = Math.Max(1, barHalf / 4);

            for (int dy = -barHalf; dy <= barHalf; dy++)
            {
                int y = centerY + dy;
                if (y < yTop || y > yBottom) continue;
                int absDy = Math.Abs(dy);
                uint col = absDy <= innerHalf
                    ? highCol
                    : absDy <= coreHalf
                        ? midCol
                        : bassCol;
                _djTopWavePixels[y * w + x] = col;
            }
        }

        // 4. Re-draw crisp bottom beat marker teeth ON TOP of waveform bars so double-clickable beat markers are always clearly visible!
        for (int bIdx = firstBeatIdx; bIdx <= lastBeatIdx; bIdx++)
        {
            double beatSec = track.FirstBeatSec + bIdx * beatPeriodSec;
            if (beatSec < 0 || beatSec > track.DurationSec)
                continue;

            int bx = centerX + (int)Math.Round((beatSec - deckSnap.CurrentSec) / secPerPixel);
            if (bx < 2 || bx >= w - 2)
                continue;

            bool isDownbeat = ((bIdx % 4) + 4) % 4 == 0;
            bool isUnlockedMarker = _djUnlockedBeatDeck == deckSnap.DeckId && _djUnlockedBeatIdx == bIdx;

            if (isUnlockedMarker)
            {
                DrawDjCalibrationHandle(bx, yTop, yBottom, w, 0xFFFFD43Bu, drawRightFlag: true);
            }
            else if (isDownbeat)
            {
                for (int dy = 0; dy < 5 && yBottom - dy >= yTop; dy++)
                {
                    _djTopWavePixels[(yBottom - dy) * w + bx - 1] = midCol;
                    _djTopWavePixels[(yBottom - dy) * w + bx] = 0xFFFFFFFFu;
                    _djTopWavePixels[(yBottom - dy) * w + bx + 1] = midCol;
                }
            }
            else
            {
                for (int dy = 0; dy < 3 && yBottom - dy >= yTop; dy++)
                {
                    _djTopWavePixels[(yBottom - dy) * w + bx] = midCol;
                }
            }
        }

        // 5. If 2-Point Manual BPM Calibration is active on this deck, draw Point 1 & Point 2 vertical calipers + span bar!
        if (_djTwoPointDeck == deckSnap.DeckId)
        {
            int px1 = centerX + (int)Math.Round((_djTwoPointSec1 - deckSnap.CurrentSec) / secPerPixel);
            int px2 = centerX + (int)Math.Round((_djTwoPointSec2 - deckSnap.CurrentSec) / secPerPixel);

            int spanLeft = Math.Clamp(Math.Min(px1, px2), 0, w - 1);
            int spanRight = Math.Clamp(Math.Max(px1, px2), 0, w - 1);
            int spanBarY = yTop + 3;
            if (spanRight > spanLeft && spanBarY <= yBottom)
            {
                for (int x = spanLeft; x <= spanRight; x++)
                {
                    _djTopWavePixels[spanBarY * w + x] = 0xFF22E87Au;
                    if (spanBarY + 1 <= yBottom)
                        _djTopWavePixels[(spanBarY + 1) * w + x] = 0x9922E87Au;
                }
            }

            if (px1 >= 1 && px1 < w - 1)
            {
                DrawDjCalibrationHandle(px1, yTop, yBottom, w, 0xFF22E87Au, drawRightFlag: true);
            }
            if (px2 >= 1 && px2 < w - 1)
            {
                DrawDjCalibrationHandle(px2, yTop, yBottom, w, 0xFFFFB020u, drawRightFlag: false);
            }
        }
    }

    private void DrawDjCalibrationHandle(int cx, int yTop, int yBottom, int w, uint color, bool drawRightFlag)
    {
        for (int y = yTop; y <= yBottom; y++)
        {
            int row = y * w;
            if (cx - 1 >= 0) _djTopWavePixels[row + cx - 1] = color;
            _djTopWavePixels[row + cx] = 0xFFFFFFFFu;
            if (cx + 1 < w) _djTopWavePixels[row + cx + 1] = color;
        }

        int flagW = 9;
        int flagH = 7;
        int x0 = drawRightFlag ? cx : cx - flagW;
        int x1 = drawRightFlag ? cx + flagW : cx;

        for (int dy = 0; dy < flagH; dy++)
        {
            int yt = yTop + dy;
            int yb = yBottom - dy;
            for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
            {
                if (yt <= yBottom) _djTopWavePixels[yt * w + x] = color;
                if (yb >= yTop) _djTopWavePixels[yb * w + x] = color;
            }
        }
    }

    // =========================================================================
    // FULL-TRACK MINI OVERVIEW WAVEFORM RENDERER (DECK A & DECK B)
    // =========================================================================

    private void RenderDjOverview(DjDeckId deckId, DjDeckSnapshot snap)
    {
        bool isA = deckId == DjDeckId.DeckA;
        var bmp = isA ? _djOvBitmapA : _djOvBitmapB;
        var pixels = isA ? _djOvPixelsA : _djOvPixelsB;
        int w = isA ? _djOvWA : _djOvWB;
        int h = isA ? _djOvHA : _djOvHB;
        var track = _studio.DjMixer.GetDeck(deckId).Track;

        if (bmp == null || w < 16 || h < 8)
            return;

        Array.Fill(pixels, 0xFF070B16u);
        int midY = h / 2;
        int maxHalf = Math.Max(2, h / 2 - 2);

        if (track != null && track.OverviewPeaks.Length > 0)
        {
            int playheadX = (int)Math.Round(snap.PlayheadNorm * (w - 1));
            uint baseCol = isA ? 0xFF1696C8u : 0xFFC8204Bu;
            uint brightCol = isA ? 0xFF42F2FFu : 0xFFFF5C8Au;
            uint playedCol = isA ? 0xFF0C4866u : 0xFF5E1228u;

            var ov = track.OverviewPeaks;
            int n = ov.Length;

            for (int x = 0; x < w; x++)
            {
                int bin = Math.Clamp(x * n / w, 0, n - 1);
                float amp = ov[bin];
                int bh = (int)Math.Round(amp * maxHalf);
                uint col = x < playheadX ? playedCol : baseCol;

                for (int dy = -bh; dy <= bh; dy++)
                {
                    int y = midY + dy;
                    if (y >= 0 && y < h)
                    {
                        pixels[y * w + x] = Math.Abs(dy) <= bh / 3 && x >= playheadX ? brightCol : col;
                    }
                }
            }

            // Main CUE marker (Emerald Green pin)
            if (snap.CueNorm.IsSet && snap.CueNorm.NormPosition >= 0.0)
            {
                int cueX = Math.Clamp((int)Math.Round(snap.CueNorm.NormPosition * (w - 1)), 0, w - 1);
                for (int y = 0; y < h; y++)
                    pixels[y * w + cueX] = 0xFF22E87Au;
            }

            // Hot Cue markers (Amber pins)
            for (int c = 0; c < snap.HotCueNorms.Length; c++)
            {
                if (snap.HotCueNorms[c] >= 0)
                {
                    int hx = Math.Clamp((int)Math.Round(snap.HotCueNorms[c] * (w - 1)), 0, w - 1);
                    for (int y = 0; y < h; y++)
                        pixels[y * w + hx] = 0xFFFFB830u;
                }
            }

            // Current Playhead White Needle
            int px = Math.Clamp(playheadX, 0, w - 1);
            for (int y = 0; y < h; y++)
            {
                pixels[y * w + px] = 0xFFFFFFFFu;
                if (px + 1 < w) pixels[y * w + px + 1] = 0xFFFFFFFFu;
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    // =========================================================================
    // 60 FPS ROTATING VINYL TURNTABLE PLATTER & CENTER DUAL LED VU METERS
    // =========================================================================

    private void RenderDjPlatter(DjDeckId deckId, double angleDeg, bool isPlaying, bool isScratching)
    {
        bool isA = deckId == DjDeckId.DeckA;
        var bmp = isA ? _djPlatterBitmapA : _djPlatterBitmapB;
        var pixels = isA ? _djPlatterPixelsA : _djPlatterPixelsB;
        if (bmp == null)
            return;

        const int size = 116;
        const double cx = 57.5;
        const double cy = 57.5;
        Array.Fill(pixels, 0x00000000u);

        uint neonCol = isA ? 0xFF22E8FFu : 0xFFFF3366u;
        uint dimNeonCol = isA ? 0xFF145E78u : 0xFF781632u;
        uint labelCol = isA ? 0xFF10324Au : 0xFF4A1024u;

        double rad = (angleDeg - 90.0) * Math.PI / 180.0;
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        for (int y = 0; y < size; y++)
        {
            double dy = y - cy;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                double dx = x - cx;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > 55.5)
                    continue;

                if (dist >= 52.0)
                {
                    // Outer glowing neon ring
                    pixels[row + x] = (isPlaying || isScratching) ? neonCol : dimNeonCol;
                }
                else if (dist >= 19.0)
                {
                    // Vinyl micro-grooves
                    int groove = (int)Math.Round(dist);
                    uint vinylBase = (groove % 3 == 0) ? 0xFF131A2Eu : 0xFF0B0F1Cu;
                    pixels[row + x] = vinylBase;
                }
                else if (dist >= 5.0)
                {
                    // Center record label ring
                    pixels[row + x] = dist >= 16.5 ? neonCol : labelCol;
                }
                else
                {
                    // Spindle hole
                    pixels[row + x] = 0xFF060810u;
                }
            }
        }

        // Draw rotating needle marker line from center label to outer rim (exact style from Image 1!)
        for (int r = 16; r <= 51; r++)
        {
            int nx = (int)Math.Round(cx + cosA * r);
            int ny = (int)Math.Round(cy + sinA * r);
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int px = nx + ox;
                    int py = ny + oy;
                    if (px >= 0 && px < size && py >= 0 && py < size)
                    {
                        pixels[py * size + px] = 0xFFFFFFFFu;
                    }
                }
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
    }

    private void RenderDjVuMeters(float peakA, float peakB)
    {
        if (_djVuBitmap == null)
            return;

        const int w = 24;
        const int h = 114;
        Array.Fill(_djVuPixels, 0xFF070A14u);

        const int segments = 16;
        int segStep = h / segments;
        int litA = (int)Math.Round(Math.Clamp(peakA, 0f, 1f) * segments);
        int litB = (int)Math.Round(Math.Clamp(peakB, 0f, 1f) * segments);

        for (int s = 0; s < segments; s++)
        {
            int yBottom = h - 2 - s * segStep;
            int yTop = Math.Max(1, yBottom - segStep + 2);

            uint activeCol = s >= 14
                ? 0xFFFF3355u // Hot Red Clip
                : s >= 11
                    ? 0xFFFFB830u // Amber
                    : s >= 6
                        ? 0xFF22E8FFu // Cyan
                        : 0xFF22E87Au; // Emerald Green

            uint colA = s < litA ? activeCol : 0xFF12192Eu;
            uint colB = s < litB ? activeCol : 0xFF12192Eu;

            for (int y = yTop; y <= yBottom; y++)
            {
                int row = y * w;
                for (int x = 2; x <= 10; x++) _djVuPixels[row + x] = colA;
                for (int x = 13; x <= 21; x++) _djVuPixels[row + x] = colB;
            }
        }

        _djVuBitmap.WritePixels(new Int32Rect(0, 0, w, h), _djVuPixels, w * 4, 0);
    }

    // =========================================================================
    // ROTARY MIXER KNOBS (HIGH / MID / LOW / FILTER / GAIN / 2-DECK MASTER GAIN)
    // =========================================================================

    private void UpdateAllDjKnobsVisual()
    {
        DrawDjKnobCanvas(KnobDjHighA, GetDjKnobNormalized("A:High"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjMidA, GetDjKnobNormalized("A:Mid"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjLowA, GetDjKnobNormalized("A:Low"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjFilterA, GetDjKnobNormalized("A:Filter"), isDeckA: true, isFilter: true);
        DrawDjKnobCanvas(KnobDjGainA, GetDjKnobNormalized("A:Gain"), isDeckA: true);

        DrawDjKnobCanvas(KnobDjMasterGain, GetDjKnobNormalized("M:MasterGain"), isDeckA: true, isFilter: false, isMaster: true);

        DrawDjKnobCanvas(KnobDjHighB, GetDjKnobNormalized("B:High"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjMidB, GetDjKnobNormalized("B:Mid"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjLowB, GetDjKnobNormalized("B:Low"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjFilterB, GetDjKnobNormalized("B:Filter"), isDeckA: false, isFilter: true);
        DrawDjKnobCanvas(KnobDjGainB, GetDjKnobNormalized("B:Gain"), isDeckA: false);
    }

    private double GetDjKnobNormalized(string tag)
    {
        if (tag == "M:MasterGain")
        {
            return (_studio.DjMixer.MasterGainDb + 24.0) / 36.0;
        }

        var deck = tag.StartsWith("A:", StringComparison.Ordinal) ? _studio.DjMixer.DeckA : _studio.DjMixer.DeckB;
        string param = tag[2..];
        return param switch
        {
            "High" => DbToKnobNorm(deck.HighDb),
            "Mid" => DbToKnobNorm(deck.MidDb),
            "Low" => DbToKnobNorm(deck.LowDb),
            "Gain" => (deck.GainDb + 24.0) / 36.0,
            "Filter" => (deck.FilterKnob + 1.0) * 0.5,
            _ => 0.5
        };
    }

    private void SetDjKnobNormalized(string tag, double norm)
    {
        norm = Math.Clamp(norm, 0.0, 1.0);
        if (tag == "M:MasterGain")
        {
            double masterDb = Math.Round(-24.0 + norm * 36.0, 1);
            _studio.DjMixer.MasterGainDb = masterDb;
            _appSettings.DjMasterGainDb = masterDb;
            _studio.Presets.SaveAppSettings(_appSettings);
            SetStatus($"DJ 2-Deck Master Output GAIN: {masterDb:+0.0;-0.0;0.0} dB{(masterDb < -0.05 ? " (anti-clip mix headroom)" : "")}");
            return;
        }

        bool isA = tag.StartsWith("A:", StringComparison.Ordinal);
        var deck = isA ? _studio.DjMixer.DeckA : _studio.DjMixer.DeckB;
        string param = tag[2..];
        string deckLabel = isA ? "Deck A" : "Deck B";

        switch (param)
        {
            case "High":
                deck.HighDb = KnobNormToDb(norm);
                SetStatus($"{deckLabel} HIGH EQ: {(deck.HighDb <= -34 ? "KILL (-∞ dB)" : $"{deck.HighDb:+0.0;-0.0;0.0} dB")}");
                break;
            case "Mid":
                deck.MidDb = KnobNormToDb(norm);
                SetStatus($"{deckLabel} MID EQ: {(deck.MidDb <= -34 ? "KILL (-∞ dB)" : $"{deck.MidDb:+0.0;-0.0;0.0} dB")}");
                break;
            case "Low":
                deck.LowDb = KnobNormToDb(norm);
                SetStatus($"{deckLabel} LOW Bass EQ: {(deck.LowDb <= -34 ? "KILL (-∞ dB)" : $"{deck.LowDb:+0.0;-0.0;0.0} dB")}");
                break;
            case "Gain":
                deck.GainDb = Math.Round(-24.0 + norm * 36.0, 1);
                if (deck.Track != null)
                {
                    _studio.DjDecoder.SaveTrackGainDb(deck.Track, deck.GainDb);
                    SetStatus($"{deckLabel} Channel GAIN: {deck.GainDb:+0.0;-0.0;0.0} dB (saved to '{deck.Track.Title}')");
                }
                else
                {
                    SetStatus($"{deckLabel} Channel GAIN: {deck.GainDb:+0.0;-0.0;0.0} dB");
                }
                break;
            case "Filter":
                deck.FilterKnob = Math.Round(norm * 2.0 - 1.0, 2);
                string fDesc = Math.Abs(deck.FilterKnob) <= 0.03
                    ? "BYPASS (Center)"
                    : deck.FilterKnob < 0
                        ? $"LOW-PASS ({(int)(deck.FilterKnob * 100)}%)"
                        : $"HIGH-PASS (+{(int)(deck.FilterKnob * 100)}%)";
                SetStatus($"{deckLabel} Color FILTER: {fDesc}");
                break;
        }
    }

    private static double DbToKnobNorm(double db)
    {
        if (db <= 0.0)
            return Math.Clamp((db + 36.0) / 36.0 * 0.5, 0.0, 0.5);
        return Math.Clamp(0.5 + (db / 12.0) * 0.5, 0.5, 1.0);
    }

    private static double KnobNormToDb(double norm)
    {
        if (norm <= 0.5)
            return Math.Round(-36.0 + (norm / 0.5) * 36.0, 1);
        return Math.Round(((norm - 0.5) / 0.5) * 12.0, 1);
    }

    private void DrawDjKnobCanvas(Canvas? canvas, double norm, bool isDeckA, bool isFilter = false, bool isMaster = false)
    {
        if (canvas == null)
            return;

        canvas.Children.Clear();
        double size = canvas.Width > 0 ? canvas.Width : 34.0;
        double cx = size / 2.0;
        double cy = size / 2.0;
        double r = cx - (isMaster ? 2.0 : 3.0);

        Color ringCol = isMaster
            ? Color.FromRgb(28, 138, 86)
            : isFilter
                ? (isDeckA ? Color.FromRgb(34, 232, 255) : Color.FromRgb(255, 51, 102))
                : Color.FromRgb(42, 56, 98);

        var body = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = new SolidColorBrush(Color.FromRgb(13, 18, 34)),
            Stroke = new SolidColorBrush(ringCol),
            StrokeThickness = isFilter ? 1.8 : (isMaster ? 1.5 : 1.4)
        };
        Canvas.SetLeft(body, cx - r);
        Canvas.SetTop(body, cy - r);
        canvas.Children.Add(body);

        // Pointer needle from -135 deg (0.0) to +135 deg (1.0)
        double angleDeg = -135.0 + Math.Clamp(norm, 0.0, 1.0) * 270.0;
        double rad = (angleDeg - 90.0) * Math.PI / 180.0;
        double innerOffset = isMaster ? 2.0 : 3.0;
        double outerOffset = isMaster ? (r - 1.6) : (r - 2.0);
        var line = new Line
        {
            X1 = cx + Math.Cos(rad) * innerOffset,
            Y1 = cy + Math.Sin(rad) * innerOffset,
            X2 = cx + Math.Cos(rad) * outerOffset,
            Y2 = cy + Math.Sin(rad) * outerOffset,
            Stroke = (Brush)FindResource(isMaster ? "Green" : (isDeckA ? "Cyan" : "Red")),
            StrokeThickness = isMaster ? 1.9 : 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(line);
    }

    private void DjKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || canvas.Tag is not string tag)
            return;

        bool isMaster = tag == "M:MasterGain";
        var now = DateTime.UtcNow;
        if ((now - _djLastKnobClickUtc).TotalMilliseconds < 320)
        {
            // Double-click resets knob to 0 dB / center bypass!
            double defaultNorm = (isMaster || tag.EndsWith(":Gain", StringComparison.Ordinal)) ? (24.0 / 36.0) : 0.5;
            SetDjKnobNormalized(tag, defaultNorm);
            DrawDjKnobCanvas(canvas, defaultNorm, tag.StartsWith("A:", StringComparison.Ordinal), tag.EndsWith(":Filter", StringComparison.Ordinal), isMaster);
            _djLastKnobClickUtc = DateTime.MinValue;
            e.Handled = true;
            return;
        }

        _djLastKnobClickUtc = now;
        _activeDjKnob = canvas;
        _djKnobDragStartPt = e.GetPosition(this);
        _djKnobDragStartNorm = GetDjKnobNormalized(tag);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void DjKnob_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeDjKnob == null || sender != _activeDjKnob || _activeDjKnob.Tag is not string tag)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _activeDjKnob.ReleaseMouseCapture();
            _activeDjKnob = null;
            return;
        }

        Point pt = e.GetPosition(this);
        double deltaY = _djKnobDragStartPt.Y - pt.Y;
        double newNorm = Math.Clamp(_djKnobDragStartNorm + deltaY / 110.0, 0.0, 1.0);
        bool isMaster = tag == "M:MasterGain";
        SetDjKnobNormalized(tag, newNorm);
        DrawDjKnobCanvas(_activeDjKnob, newNorm, tag.StartsWith("A:", StringComparison.Ordinal), tag.EndsWith(":Filter", StringComparison.Ordinal), isMaster);
    }

    private void DjKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeDjKnob != null)
        {
            _activeDjKnob.ReleaseMouseCapture();
            _activeDjKnob = null;
        }
    }

    private void DjKnob_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Canvas canvas || canvas.Tag is not string tag)
            return;

        int dir = Math.Sign(e.Delta);
        if (dir == 0)
            return;

        double stepScale = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5.0 : 1.0;

        if (tag == "M:MasterGain")
        {
            double newMaster = Math.Clamp(Math.Round(_studio.DjMixer.MasterGainDb + dir * 0.1 * stepScale, 1), -24.0, 12.0);
            double newNorm = (newMaster + 24.0) / 36.0;
            SetDjKnobNormalized(tag, newNorm);
            DrawDjKnobCanvas(canvas, newNorm, isDeckA: true, isFilter: false, isMaster: true);
            e.Handled = true;
            return;
        }

        bool isA = tag.StartsWith("A:", StringComparison.Ordinal);
        var deck = isA ? _studio.DjMixer.DeckA : _studio.DjMixer.DeckB;
        string param = tag[2..];

        if (param == "Gain")
        {
            // Exact 0.1 dB single-step precision per wheel tick (e.g. 1.2 dB -> 1.3 dB -> 1.4 dB)!
            double newGain = Math.Clamp(Math.Round(deck.GainDb + dir * 0.1 * stepScale, 1), -24.0, 12.0);
            double newNorm = (newGain + 24.0) / 36.0;
            SetDjKnobNormalized(tag, newNorm);
            DrawDjKnobCanvas(canvas, newNorm, isA, isFilter: false);
        }
        else if (param is "High" or "Mid" or "Low")
        {
            double curDb = param == "High" ? deck.HighDb : param == "Mid" ? deck.MidDb : deck.LowDb;
            double newDb = Math.Clamp(Math.Round(curDb + dir * 0.1 * stepScale, 1), -36.0, 12.0);
            double newNorm = DbToKnobNorm(newDb);
            SetDjKnobNormalized(tag, newNorm);
            DrawDjKnobCanvas(canvas, newNorm, isA, isFilter: false);
        }
        else if (param == "Filter")
        {
            double newFilt = Math.Clamp(Math.Round(deck.FilterKnob + dir * 0.01 * stepScale, 2), -1.0, 1.0);
            double newNorm = (newFilt + 1.0) * 0.5;
            SetDjKnobNormalized(tag, newNorm);
            DrawDjKnobCanvas(canvas, newNorm, isA, isFilter: true);
        }
        e.Handled = true;
    }

    // =========================================================================
    // INTERACTIVE WAVEFORM ZOOM, SCRUBBING, 2-POINT BPM CALIBRATOR & BEAT MARKERS
    // =========================================================================

    private void SetDjWaveZoomFactor(double newZoom)
    {
        _djWaveZoomFactor = Math.Clamp(Math.Round(newZoom, 2), 0.4, 16.0);
        if (BtnDjWaveZoomReset != null)
        {
            BtnDjWaveZoomReset.Content = $"{_djWaveZoomFactor:0.0}x";
            bool zoomed = Math.Abs(_djWaveZoomFactor - 1.0) > 0.05;
            BtnDjWaveZoomReset.Foreground = zoomed ? (Brush)FindResource("Cyan") : (Brush)FindResource("Text");
            BtnDjWaveZoomReset.BorderBrush = zoomed ? (Brush)FindResource("Cyan") : (Brush)FindResource("Line");
        }
        RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
    }

    private void DjTopWaveform_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int dir = Math.Sign(e.Delta);
        if (dir == 0) return;

        double factor = dir > 0 ? 1.25 : 0.80;
        double nextZoom = _djWaveZoomFactor * factor;
        if (Math.Abs(nextZoom - 1.0) < 0.08)
            nextZoom = 1.0;

        SetDjWaveZoomFactor(nextZoom);
        e.Handled = true;
    }

    private void BtnDjWaveZoomIn_Click(object sender, RoutedEventArgs e) => SetDjWaveZoomFactor(_djWaveZoomFactor * 1.35);
    private void BtnDjWaveZoomOut_Click(object sender, RoutedEventArgs e) => SetDjWaveZoomFactor(_djWaveZoomFactor / 1.35);
    private void BtnDjWaveZoomReset_Click(object sender, RoutedEventArgs e) => SetDjWaveZoomFactor(1.0);

    private (double secPerPixel, double currentSec, double beatPeriodSec) GetTopLaneMetrics(DjDeckId deckId)
    {
        var snap = _studio.DjMixer.GetSnapshot();
        var deckSnap = deckId == DjDeckId.DeckA ? snap.DeckA : snap.DeckB;
        double w = Math.Max(200.0, DjTopWaveformViewport.ActualWidth);
        double speed = Math.Max(0.4, deckSnap.EffectiveBpm / Math.Max(40.0, deckSnap.BaseBpm));
        double visibleWindowSec = (9.0 / _djWaveZoomFactor) * speed;
        double secPerPixel = visibleWindowSec / w;
        double beatPeriodSec = 60.0 / Math.Max(40.0, deckSnap.BaseBpm);
        return (secPerPixel, deckSnap.CurrentSec, beatPeriodSec);
    }

    private void BtnDjTwoPtA_Click(object sender, RoutedEventArgs e) => ToggleTwoPointBpmCalibrator(DjDeckId.DeckA);
    private void BtnDjTwoPtB_Click(object sender, RoutedEventArgs e) => ToggleTwoPointBpmCalibrator(DjDeckId.DeckB);

    private void ToggleTwoPointBpmCalibrator(DjDeckId deckId)
    {
        var deck = _studio.DjMixer.GetDeck(deckId);
        if (deck.Track == null)
        {
            SetStatus($"Load a song into {(deckId == DjDeckId.DeckA ? "Deck A" : "Deck B")} first to calibrate BPM with 2 points.");
            return;
        }

        if (_djTwoPointDeck == deckId)
        {
            LockAndCloseDjCalibration();
            return;
        }

        _djUnlockedBeatDeck = null;
        _djDraggingUnlockedBeat = false;
        _djTwoPointDeck = deckId;

        var (secPerPixel, curSec, beatPeriodSec) = GetTopLaneMetrics(deckId);
        double w = Math.Max(200.0, DjTopWaveformViewport.ActualWidth);
        double halfVisibleSec = (w * 0.5) * secPerPixel;
        double leftLead = Math.Min(1.8, halfVisibleSec * 0.55);
        double rawStart = Math.Max(0.0, curSec - leftLead);
        int nearestBeat = (int)Math.Round((rawStart - deck.Track.FirstBeatSec) / beatPeriodSec);
        double p1 = Math.Clamp(deck.Track.FirstBeatSec + nearestBeat * beatPeriodSec, 0.0, Math.Max(0.0, deck.Track.DurationSec - 0.5));
        double p2 = Math.Clamp(p1 + _djTwoPointBeatSpan * beatPeriodSec, p1 + 0.15, deck.Track.DurationSec);

        _djTwoPointSec1 = p1;
        _djTwoPointSec2 = p2;

        UpdateToggleButtonVisual(BtnDjTwoPtA, _djTwoPointDeck == DjDeckId.DeckA, "Amber");
        UpdateToggleButtonVisual(BtnDjTwoPtB, _djTwoPointDeck == DjDeckId.DeckB, "Amber");
        ShowDjCalibrationOverlayUi();
        RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());

        string label = deckId == DjDeckId.DeckA ? "Deck A" : "Deck B";
        SetStatus($"{label} 2-Point BPM Calibrator active — Drag green Point 1 & amber Point 2 onto beats ({_djTwoPointBeatSpan} beats apart) to calculate & save exact BPM!");
    }

    private void BtnDjCalibSpan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string s || !int.TryParse(s, out int beats))
            return;

        _djTwoPointBeatSpan = beats;
        UpdateToggleButtonVisual(BtnDjSpan1, beats == 1, "Amber");
        UpdateToggleButtonVisual(BtnDjSpan4, beats == 4, "Amber");
        UpdateToggleButtonVisual(BtnDjSpan8, beats == 8, "Amber");
        UpdateToggleButtonVisual(BtnDjSpan16, beats == 16, "Amber");

        if (_djTwoPointDeck.HasValue)
        {
            ApplyTwoPointBpmCalculation(_djTwoPointDeck.Value, saveToDisk: true);
            ShowDjCalibrationOverlayUi();
            RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
        }
    }

    private void BtnDjCalibLock_Click(object sender, RoutedEventArgs e)
    {
        LockAndCloseDjCalibration();
    }

    private void LockAndCloseDjCalibration()
    {
        if (_djTwoPointDeck.HasValue)
        {
            PersistDeckBeatGridAndBpm(_djTwoPointDeck.Value);
        }
        else if (_djUnlockedBeatDeck.HasValue)
        {
            PersistDeckBeatGridAndBpm(_djUnlockedBeatDeck.Value);
        }

        _djTwoPointDeck = null;
        _djDraggingTwoPointHandle = 0;
        _djUnlockedBeatDeck = null;
        _djDraggingUnlockedBeat = false;

        UpdateToggleButtonVisual(BtnDjTwoPtA, false, "Amber");
        UpdateToggleButtonVisual(BtnDjTwoPtB, false, "Amber");
        if (BorderDjCalibBar != null)
            BorderDjCalibBar.Visibility = Visibility.Collapsed;

        RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
    }

    private void ShowDjCalibrationOverlayUi()
    {
        if (BorderDjCalibBar == null)
            return;

        if (_djTwoPointDeck.HasValue)
        {
            var deck = _studio.DjMixer.GetDeck(_djTwoPointDeck.Value);
            string dName = _djTwoPointDeck.Value == DjDeckId.DeckA ? "Deck A" : "Deck B";
            TxtDjCalibLabel.Text = $"🎯 2-PT BPM ({dName}):";
            PanelDjCalibBeatBtns.Visibility = Visibility.Visible;
            TxtDjCalibReadout.Text = $"{deck.BaseBpm:0.00} BPM";
            BorderDjCalibBar.VerticalAlignment = _djTwoPointDeck.Value == DjDeckId.DeckA ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            BorderDjCalibBar.Margin = new Thickness(95, 4, 0, 4);
            BorderDjCalibBar.Visibility = Visibility.Visible;
        }
        else if (_djUnlockedBeatDeck.HasValue)
        {
            var deck = _studio.DjMixer.GetDeck(_djUnlockedBeatDeck.Value);
            string dName = _djUnlockedBeatDeck.Value == DjDeckId.DeckA ? "Deck A" : "Deck B";
            TxtDjCalibLabel.Text = $"🔓 BEAT START UNLOCKED ({dName}) — Drag gold marker onto beat:";
            PanelDjCalibBeatBtns.Visibility = Visibility.Collapsed;
            TxtDjCalibReadout.Text = $"Start @ {(deck.Track?.FirstBeatSec ?? 0.0):0.000}s";
            BorderDjCalibBar.VerticalAlignment = _djUnlockedBeatDeck.Value == DjDeckId.DeckA ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            BorderDjCalibBar.Margin = new Thickness(95, 4, 0, 4);
            BorderDjCalibBar.Visibility = Visibility.Visible;
        }
        else
        {
            BorderDjCalibBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTwoPointBpmCalculation(DjDeckId deckId, bool saveToDisk)
    {
        var deck = _studio.DjMixer.GetDeck(deckId);
        var track = deck.Track;
        if (track == null)
            return;

        double pStart = Math.Min(_djTwoPointSec1, _djTwoPointSec2);
        double spanSec = Math.Abs(_djTwoPointSec2 - _djTwoPointSec1);
        if (spanSec < 0.08)
            return;

        double calcBpm = (60.0 * _djTwoPointBeatSpan) / spanSec;
        if (calcBpm >= 35.0 && calcBpm <= 250.0)
        {
            double roundedBpm = Math.Round(calcBpm, 2);
            deck.ManualBpmOverride = roundedBpm;
            track.DetectedBpm = roundedBpm;
            track.FirstBeatSec = Math.Clamp(pStart, 0.0, track.DurationSec);

            if (TxtDjCalibReadout != null)
                TxtDjCalibReadout.Text = $"{roundedBpm:0.00} BPM";

            var snap = _studio.DjMixer.GetSnapshot();
            UpdateDeckTelemetryUi(deckId == DjDeckId.DeckA ? snap.DeckA : snap.DeckB, deckId == DjDeckId.DeckA);

            if (saveToDisk)
            {
                PersistDeckBeatGridAndBpm(deckId);
            }
        }
    }

    private void PersistDeckBeatGridAndBpm(DjDeckId deckId)
    {
        var deck = _studio.DjMixer.GetDeck(deckId);
        var track = deck.Track;
        if (track == null)
            return;

        double bpmToSave = deck.BaseBpm;
        _studio.DjDecoder.SaveTrackBeatGridAndBpm(track, bpmToSave, track.FirstBeatSec);

        // Sync matching track row in the bottom DJ crate table immediately!
        bool refreshed = false;
        foreach (var row in _djAllFolderTracks)
        {
            if (string.Equals(row.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                row.Bpm = track.DetectedBpm;
                row.IsAnalyzed = true;
                refreshed = true;
            }
        }
        if (refreshed)
        {
            GridDjTracks.Items.Refresh();
        }

        string dName = deckId == DjDeckId.DeckA ? "Deck A" : "Deck B";
        SetStatus($"Saved {dName} Beat Grid & BPM ({track.DetectedBpm:0.00} BPM • Beat Start {track.FirstBeatSec:0.000}s) linked to '{track.Title}'.");
    }

    private void DjTopWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pt = e.GetPosition(DjTopWaveformViewport);
        double w = Math.Max(200.0, DjTopWaveformViewport.ActualWidth);
        double centerX = w * 0.5;
        var laneDeck = pt.Y < DjTopWaveformViewport.ActualHeight / 2.0 ? DjDeckId.DeckA : DjDeckId.DeckB;
        var deck = _studio.DjMixer.GetDeck(laneDeck);
        var track = deck.Track;

        if (track != null && track.DurationSec > 0.1)
        {
            var (secPerPixel, curSec, beatPeriodSec) = GetTopLaneMetrics(laneDeck);

            // 1. Check if grabbing Point 1 or Point 2 in Manual 2-Point BPM Calibration mode
            if (_djTwoPointDeck == laneDeck)
            {
                double px1 = centerX + (_djTwoPointSec1 - curSec) / secPerPixel;
                double px2 = centerX + (_djTwoPointSec2 - curSec) / secPerPixel;
                double d1 = Math.Abs(pt.X - px1);
                double d2 = Math.Abs(pt.X - px2);
                if (d1 <= 12.0 && d1 <= d2)
                {
                    _djDraggingTwoPointHandle = 1;
                    DjTopWaveformViewport.CaptureMouse();
                    return;
                }
                if (d2 <= 12.0)
                {
                    _djDraggingTwoPointHandle = 2;
                    DjTopWaveformViewport.CaptureMouse();
                    return;
                }
            }

            // 2. Check if grabbing an already-unlocked beat marker
            if (_djUnlockedBeatDeck == laneDeck)
            {
                double unlockedSec = track.FirstBeatSec + _djUnlockedBeatIdx * beatPeriodSec;
                double unlockedPx = centerX + (unlockedSec - curSec) / secPerPixel;
                if (Math.Abs(pt.X - unlockedPx) <= 13.0)
                {
                    // Double-clicking the unlocked marker again locks & saves it!
                    var nowCheck = DateTime.UtcNow;
                    if (e.ClickCount >= 2 || (nowCheck - _djLastTopWaveClickUtc).TotalMilliseconds < 330)
                    {
                        _djLastTopWaveClickUtc = DateTime.MinValue;
                        LockAndCloseDjCalibration();
                        return;
                    }
                    _djLastTopWaveClickUtc = nowCheck;
                    _djDraggingUnlockedBeat = true;
                    DjTopWaveformViewport.CaptureMouse();
                    return;
                }
            }

            // 3. Detect Double-Click on ANY red/cyan beat marker to unlock & make it draggable!
            var now = DateTime.UtcNow;
            bool isDoubleClick = e.ClickCount >= 2
                || ((now - _djLastTopWaveClickUtc).TotalMilliseconds < 350 && Math.Abs(pt.X - _djLastTopWaveClickPt.X) < 15.0 && Math.Abs(pt.Y - _djLastTopWaveClickPt.Y) < 24.0);
            _djLastTopWaveClickUtc = now;
            _djLastTopWaveClickPt = pt;

            if (isDoubleClick)
            {
                double clickSec = curSec + (pt.X - centerX) * secPerPixel;
                int nearestBeatIdx = (int)Math.Round((clickSec - track.FirstBeatSec) / beatPeriodSec);
                double nearestBeatSec = track.FirstBeatSec + nearestBeatIdx * beatPeriodSec;
                double nearestBeatPx = centerX + (nearestBeatSec - curSec) / secPerPixel;

                if (Math.Abs(pt.X - nearestBeatPx) <= 18.0 && nearestBeatSec >= -0.2 && nearestBeatSec <= track.DurationSec + 0.2)
                {
                    if (_djDraggingTopWave)
                    {
                        _studio.DjMixer.EndScratch(_djTopWaveDragDeck);
                        _djDraggingTopWave = false;
                    }

                    _djTwoPointDeck = null;
                    UpdateToggleButtonVisual(BtnDjTwoPtA, false, "Amber");
                    UpdateToggleButtonVisual(BtnDjTwoPtB, false, "Amber");

                    _djUnlockedBeatDeck = laneDeck;
                    _djUnlockedBeatIdx = nearestBeatIdx;
                    _djDraggingUnlockedBeat = true;
                    DjTopWaveformViewport.CaptureMouse();
                    ShowDjCalibrationOverlayUi();
                    RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
                    SetStatus($"Unlocked beat marker on {(laneDeck == DjDeckId.DeckA ? "Deck A" : "Deck B")} — drag left/right onto the exact beat start!");
                    return;
                }
            }

            // Clicking away from an unlocked single beat marker locks & saves it cleanly
            if (_djUnlockedBeatDeck == laneDeck)
            {
                PersistDeckBeatGridAndBpm(laneDeck);
                _djUnlockedBeatDeck = null;
                _djDraggingUnlockedBeat = false;
                ShowDjCalibrationOverlayUi();
            }
        }

        _djTopWaveDragDeck = laneDeck;
        _djTopWaveLastPt = pt;
        _djDraggingTopWave = true;
        _studio.DjMixer.BeginScratch(_djTopWaveDragDeck);
        DjTopWaveformViewport.CaptureMouse();
    }

    private void DjTopWaveform_MouseMove(object sender, MouseEventArgs e)
    {
        Point pt = e.GetPosition(DjTopWaveformViewport);
        double w = Math.Max(200.0, DjTopWaveformViewport.ActualWidth);
        double centerX = w * 0.5;

        // 1. Dragging Point 1 or Point 2 in Manual 2-Point BPM Calibrator
        if (_djDraggingTwoPointHandle != 0 && _djTwoPointDeck.HasValue)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ApplyTwoPointBpmCalculation(_djTwoPointDeck.Value, saveToDisk: true);
                _djDraggingTwoPointHandle = 0;
                DjTopWaveformViewport.ReleaseMouseCapture();
                return;
            }

            var deck = _studio.DjMixer.GetDeck(_djTwoPointDeck.Value);
            if (deck.Track != null)
            {
                var (secPerPixel, curSec, _) = GetTopLaneMetrics(_djTwoPointDeck.Value);
                double targetSec = Math.Clamp(curSec + (pt.X - centerX) * secPerPixel, 0.0, deck.Track.DurationSec);
                if (_djDraggingTwoPointHandle == 1)
                    _djTwoPointSec1 = targetSec;
                else
                    _djTwoPointSec2 = targetSec;

                ApplyTwoPointBpmCalculation(_djTwoPointDeck.Value, saveToDisk: false);
                RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
            }
            return;
        }

        // 2. Dragging an unlocked Beat-Grid Marker onto the exact kick/beat start
        if (_djDraggingUnlockedBeat && _djUnlockedBeatDeck.HasValue)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                PersistDeckBeatGridAndBpm(_djUnlockedBeatDeck.Value);
                _djDraggingUnlockedBeat = false;
                DjTopWaveformViewport.ReleaseMouseCapture();
                return;
            }

            var deck = _studio.DjMixer.GetDeck(_djUnlockedBeatDeck.Value);
            var track = deck.Track;
            if (track != null)
            {
                var (secPerPixel, curSec, beatPeriodSec) = GetTopLaneMetrics(_djUnlockedBeatDeck.Value);
                double targetSec = Math.Clamp(curSec + (pt.X - centerX) * secPerPixel, 0.0, track.DurationSec);

                if (_djUnlockedBeatIdx == 0)
                {
                    track.FirstBeatSec = targetSec;
                }
                else
                {
                    double newFirst = targetSec - _djUnlockedBeatIdx * beatPeriodSec;
                    double fourBeats = 4.0 * beatPeriodSec;
                    while (newFirst < 0.0) newFirst += fourBeats;
                    while (newFirst >= fourBeats && targetSec >= fourBeats) newFirst -= fourBeats;
                    track.FirstBeatSec = Math.Clamp(newFirst, 0.0, track.DurationSec);
                    _djUnlockedBeatIdx = (int)Math.Round((targetSec - track.FirstBeatSec) / beatPeriodSec);
                }

                if (TxtDjCalibReadout != null)
                    TxtDjCalibReadout.Text = $"Start @ {targetSec:0.000}s";

                RenderDjTopWaveforms(_studio.DjMixer.GetSnapshot());
            }
            return;
        }

        // 3. Hover cursor feedback when not dragging
        if (!_djDraggingTopWave)
        {
            var hoverDeck = pt.Y < DjTopWaveformViewport.ActualHeight / 2.0 ? DjDeckId.DeckA : DjDeckId.DeckB;
            var track = _studio.DjMixer.GetDeck(hoverDeck).Track;
            if (track != null)
            {
                var (secPerPixel, curSec, beatPeriodSec) = GetTopLaneMetrics(hoverDeck);
                if (_djTwoPointDeck == hoverDeck)
                {
                    double px1 = centerX + (_djTwoPointSec1 - curSec) / secPerPixel;
                    double px2 = centerX + (_djTwoPointSec2 - curSec) / secPerPixel;
                    if (Math.Abs(pt.X - px1) <= 12.0 || Math.Abs(pt.X - px2) <= 12.0)
                    {
                        DjTopWaveformViewport.Cursor = Cursors.SizeWE;
                        return;
                    }
                }
                if (_djUnlockedBeatDeck == hoverDeck)
                {
                    double uPx = centerX + (track.FirstBeatSec + _djUnlockedBeatIdx * beatPeriodSec - curSec) / secPerPixel;
                    if (Math.Abs(pt.X - uPx) <= 13.0)
                    {
                        DjTopWaveformViewport.Cursor = Cursors.SizeWE;
                        return;
                    }
                }
            }
            DjTopWaveformViewport.Cursor = Cursors.Arrow;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _studio.DjMixer.EndScratch(_djTopWaveDragDeck);
            DjTopWaveformViewport.ReleaseMouseCapture();
            _djDraggingTopWave = false;
            return;
        }

        double dx = pt.X - _djTopWaveLastPt.X;
        _djTopWaveLastPt = pt;

        // Zoom-locked 1:1 scrubbing: kick transients stay glued right under your mouse cursor at any zoom level!
        var (scrubSecPerPx, _, _) = GetTopLaneMetrics(_djTopWaveDragDeck);
        double deltaSec = -dx * scrubSecPerPx;
        _studio.DjMixer.UpdateScratch(_djTopWaveDragDeck, deltaSec, deltaSec * 25.0);
    }

    private void DjTopWaveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_djDraggingTwoPointHandle != 0)
        {
            if (_djTwoPointDeck.HasValue)
            {
                ApplyTwoPointBpmCalculation(_djTwoPointDeck.Value, saveToDisk: true);
            }
            _djDraggingTwoPointHandle = 0;
            DjTopWaveformViewport.ReleaseMouseCapture();
            return;
        }

        if (_djDraggingUnlockedBeat)
        {
            if (_djUnlockedBeatDeck.HasValue)
            {
                PersistDeckBeatGridAndBpm(_djUnlockedBeatDeck.Value);
            }
            _djDraggingUnlockedBeat = false;
            DjTopWaveformViewport.ReleaseMouseCapture();
            return;
        }

        if (_djDraggingTopWave)
        {
            _studio.DjMixer.EndScratch(_djTopWaveDragDeck);
            DjTopWaveformViewport.ReleaseMouseCapture();
            _djDraggingTopWave = false;
        }
    }

    private void DjOverviewA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double norm = e.GetPosition(DjOverviewViewportA).X / Math.Max(1.0, DjOverviewViewportA.ActualWidth);
        _studio.DjMixer.SeekNormalized(DjDeckId.DeckA, norm);
    }

    private void DjOverviewA_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            double norm = e.GetPosition(DjOverviewViewportA).X / Math.Max(1.0, DjOverviewViewportA.ActualWidth);
            _studio.DjMixer.SeekNormalized(DjDeckId.DeckA, norm);
        }
    }

    private void DjOverviewB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double norm = e.GetPosition(DjOverviewViewportB).X / Math.Max(1.0, DjOverviewViewportB.ActualWidth);
        _studio.DjMixer.SeekNormalized(DjDeckId.DeckB, norm);
    }

    private void DjOverviewB_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            double norm = e.GetPosition(DjOverviewViewportB).X / Math.Max(1.0, DjOverviewViewportB.ActualWidth);
            _studio.DjMixer.SeekNormalized(DjDeckId.DeckB, norm);
        }
    }

    private void ImgDjPlatterA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _djScratchingPlatterA = true;
        _djPlatterLastAngleA = GetPlatterMouseAngleDeg(ImgDjPlatterA, e);
        _djPlatterLastMoveUtcA = DateTime.UtcNow;
        _studio.DjMixer.BeginScratch(DjDeckId.DeckA);
        ImgDjPlatterA.CaptureMouse();
    }

    private void ImgDjPlatterA_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_djScratchingPlatterA)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _djScratchingPlatterA = false;
            _studio.DjMixer.EndScratch(DjDeckId.DeckA);
            ImgDjPlatterA.ReleaseMouseCapture();
            return;
        }

        HandlePlatterScratchMove(DjDeckId.DeckA, ImgDjPlatterA, e, ref _djPlatterLastAngleA, ref _djPlatterLastMoveUtcA);
    }

    private void ImgDjPlatterA_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_djScratchingPlatterA)
        {
            _djScratchingPlatterA = false;
            _studio.DjMixer.EndScratch(DjDeckId.DeckA);
            ImgDjPlatterA.ReleaseMouseCapture();
        }
    }

    private void ImgDjPlatterB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _djScratchingPlatterB = true;
        _djPlatterLastAngleB = GetPlatterMouseAngleDeg(ImgDjPlatterB, e);
        _djPlatterLastMoveUtcB = DateTime.UtcNow;
        _studio.DjMixer.BeginScratch(DjDeckId.DeckB);
        ImgDjPlatterB.CaptureMouse();
    }

    private void ImgDjPlatterB_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_djScratchingPlatterB)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _djScratchingPlatterB = false;
            _studio.DjMixer.EndScratch(DjDeckId.DeckB);
            ImgDjPlatterB.ReleaseMouseCapture();
            return;
        }

        HandlePlatterScratchMove(DjDeckId.DeckB, ImgDjPlatterB, e, ref _djPlatterLastAngleB, ref _djPlatterLastMoveUtcB);
    }

    private void ImgDjPlatterB_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_djScratchingPlatterB)
        {
            _djScratchingPlatterB = false;
            _studio.DjMixer.EndScratch(DjDeckId.DeckB);
            ImgDjPlatterB.ReleaseMouseCapture();
        }
    }

    private static double GetPlatterMouseAngleDeg(FrameworkElement el, MouseEventArgs e)
    {
        Point pt = e.GetPosition(el);
        double dx = pt.X - el.ActualWidth * 0.5;
        double dy = pt.Y - el.ActualHeight * 0.5;
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    private void HandlePlatterScratchMove(DjDeckId deckId, FrameworkElement el, MouseEventArgs e, ref double lastAngle, ref DateTime lastUtc)
    {
        double curAngle = GetPlatterMouseAngleDeg(el, e);
        double deltaDeg = curAngle - lastAngle;
        if (deltaDeg > 180.0) deltaDeg -= 360.0;
        else if (deltaDeg < -180.0) deltaDeg += 360.0;

        var now = DateTime.UtcNow;
        double dt = Math.Max(0.004, (now - lastUtc).TotalSeconds);
        lastAngle = curAngle;
        lastUtc = now;

        // 198 deg = 1.0 second of 33.3 RPM vinyl rotation
        double deltaSec = deltaDeg / 198.0;
        double rate = deltaSec / dt;
        _studio.DjMixer.UpdateScratch(deckId, deltaSec, rate);
    }

    // =========================================================================
    // DECK A & DECK B TRANSPORT, PITCH, LOOPS, HOT CUES, FX & STEM PADS
    // =========================================================================

    private void BtnDjPlayA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.TogglePlay(DjDeckId.DeckA);
    private void BtnDjPlayB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.TogglePlay(DjDeckId.DeckB);

    private void BtnDjStopStartA_Click(object sender, RoutedEventArgs e) => StopDeckAndReturnToStartUi(DjDeckId.DeckA);
    private void BtnDjStopStartB_Click(object sender, RoutedEventArgs e) => StopDeckAndReturnToStartUi(DjDeckId.DeckB);

    private void StopDeckAndReturnToStartUi(DjDeckId deckId)
    {
        _studio.DjMixer.StopAndReturnToStart(deckId);
        var snap = _studio.DjMixer.GetSnapshot();
        bool isA = deckId == DjDeckId.DeckA;
        UpdateDeckTelemetryUi(isA ? snap.DeckA : snap.DeckB, isA);
        RenderDjStaticOverviews();
        RenderDjPlatter(deckId, 0.0, isPlaying: false, isScratching: false);
        RenderDjVuMeters(
            Math.Max(snap.DeckA.PeakLeft, snap.DeckA.PeakRight),
            Math.Max(snap.DeckB.PeakLeft, snap.DeckB.PeakRight));
        SetStatus(snap.StatusMessage);
    }

    private static bool IsAltModifierHeld()
    {
        return (Keyboard.Modifiers & ModifierKeys.Alt) != 0
            || Keyboard.IsKeyDown(Key.LeftAlt)
            || Keyboard.IsKeyDown(Key.RightAlt);
    }

    private static bool IsSliderThumbClicked(Slider slider, DependencyObject? originalSource)
    {
        DependencyObject? cur = originalSource;
        while (cur != null && !ReferenceEquals(cur, slider))
        {
            if (cur is System.Windows.Controls.Primitives.Thumb)
                return true;

            cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void BtnDjCueA_Click(object sender, RoutedEventArgs e) => HandleDeckMainCueClick(DjDeckId.DeckA);
    private void BtnDjCueB_Click(object sender, RoutedEventArgs e) => HandleDeckMainCueClick(DjDeckId.DeckB);

    private void HandleDeckMainCueClick(DjDeckId deckId)
    {
        bool setCue = IsAltModifierHeld();
        if (!_studio.DjMixer.TriggerMainCue(deckId, setCuePoint: setCue))
            return;

        var deck = _studio.DjMixer.GetDeck(deckId);
        if (setCue && deck.Track != null)
        {
            _studio.DjDecoder.SaveTrackCues(deck.Track, updateMainCue: true);
        }

        var snap = _studio.DjMixer.GetSnapshot();
        bool isA = deckId == DjDeckId.DeckA;
        UpdateDeckTelemetryUi(isA ? snap.DeckA : snap.DeckB, isA);
        RenderDjStaticOverviews();
        SetStatus(snap.StatusMessage);
    }

    private void BtnDjSyncA_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.SyncDeck(DjDeckId.DeckA);
        SetDeckPitchPercent(DjDeckId.DeckA, _studio.DjMixer.DeckA.PitchPercent, roundToHundredth: false);
        SetStatus(_studio.DjMixer.GetSnapshot().StatusMessage);
    }

    private void BtnDjSyncB_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.SyncDeck(DjDeckId.DeckB);
        SetDeckPitchPercent(DjDeckId.DeckB, _studio.DjMixer.DeckB.PitchPercent, roundToHundredth: false);
        SetStatus(_studio.DjMixer.GetSnapshot().StatusMessage);
    }

    private void BtnDjTapA_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.DeckA.RegisterTapTempo();
        if (_studio.DjMixer.DeckA.ManualBpmOverride > 30.0 && _studio.DjMixer.DeckA.Track != null)
        {
            _studio.DjMixer.DeckA.Track.DetectedBpm = _studio.DjMixer.DeckA.ManualBpmOverride;
            PersistDeckBeatGridAndBpm(DjDeckId.DeckA);
        }
    }

    private void BtnDjTapB_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.DeckB.RegisterTapTempo();
        if (_studio.DjMixer.DeckB.ManualBpmOverride > 30.0 && _studio.DjMixer.DeckB.Track != null)
        {
            _studio.DjMixer.DeckB.Track.DetectedBpm = _studio.DjMixer.DeckB.ManualBpmOverride;
            PersistDeckBeatGridAndBpm(DjDeckId.DeckB);
        }
    }

    private void BtnDjMtA_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckA;
        d.MasterTempoEnabled = !d.MasterTempoEnabled;
        UpdateToggleButtonVisual(BtnDjMtA, d.MasterTempoEnabled, "Cyan");
        SetStatus($"Deck A Master Tempo (Keylock): {(d.MasterTempoEnabled ? "ON" : "OFF (Vinyl Pitch)")}");
    }

    private void BtnDjMtB_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckB;
        d.MasterTempoEnabled = !d.MasterTempoEnabled;
        UpdateToggleButtonVisual(BtnDjMtB, d.MasterTempoEnabled, "Red");
        SetStatus($"Deck B Master Tempo (Keylock): {(d.MasterTempoEnabled ? "ON" : "OFF (Vinyl Pitch)")}");
    }

    private void BtnDjVinylA_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckA;
        d.VinylModeEnabled = !d.VinylModeEnabled;
        UpdateToggleButtonVisual(BtnDjVinylA, d.VinylModeEnabled, "Cyan");
    }

    private void BtnDjVinylB_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckB;
        d.VinylModeEnabled = !d.VinylModeEnabled;
        UpdateToggleButtonVisual(BtnDjVinylB, d.VinylModeEnabled, "Red");
    }

    private void BtnDjSlipA_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckA;
        d.SlipModeEnabled = !d.SlipModeEnabled;
        UpdateToggleButtonVisual(BtnDjSlipA, d.SlipModeEnabled, "Amber");
        SetStatus($"Deck A SLIP Mode: {(d.SlipModeEnabled ? "ENABLED" : "OFF")}");
    }

    private void BtnDjSlipB_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckB;
        d.SlipModeEnabled = !d.SlipModeEnabled;
        UpdateToggleButtonVisual(BtnDjSlipB, d.SlipModeEnabled, "Amber");
        SetStatus($"Deck B SLIP Mode: {(d.SlipModeEnabled ? "ENABLED" : "OFF")}");
    }

    private void SetDeckPitchPercent(DjDeckId deckId, double rawPitchPercent, bool roundToHundredth = true)
    {
        bool isA = deckId == DjDeckId.DeckA;
        var deck = isA ? _studio.DjMixer.DeckA : _studio.DjMixer.DeckB;
        var slider = isA ? SldDjPitchA : SldDjPitchB;
        var label = isA ? TxtDjPitchA : TxtDjPitchB;

        double clamped = Math.Clamp(rawPitchPercent, -deck.PitchRangePercent, deck.PitchRangePercent);
        if (roundToHundredth)
        {
            clamped = Math.Round(clamped, 2);
        }
        deck.PitchPercent = clamped;

        double displayPct = Math.Round(clamped, 2);
        if (Math.Abs(displayPct) < 0.005) displayPct = 0.0;

        if (slider != null)
        {
            if (displayPct < slider.Minimum) slider.Minimum = -Math.Ceiling(Math.Abs(displayPct));
            if (displayPct > slider.Maximum) slider.Maximum = Math.Ceiling(Math.Abs(displayPct));
            if (Math.Abs(slider.Value - displayPct) >= 0.004)
            {
                _suppressPitchSliderSync = true;
                slider.Value = displayPct;
                _suppressPitchSliderSync = false;
            }
        }

        if (label != null)
        {
            label.Text = $"{displayPct:+0.00;-0.00;+0.00}%";
        }

        var snap = _studio.DjMixer.GetSnapshot();
        UpdateDeckTelemetryUi(isA ? snap.DeckA : snap.DeckB, isA);
    }

    private void SldDjPitchA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard against stray WPF layout/resize/coercion events shifting pitch unexpectedly!
        if (_suppressPitchSliderSync || !_djFineDraggingPitchA || TxtDjPitchA == null) return;
        SetDeckPitchPercent(DjDeckId.DeckA, e.NewValue, roundToHundredth: true);
    }

    private void SldDjPitchB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPitchSliderSync || !_djFineDraggingPitchB || TxtDjPitchB == null) return;
        SetDeckPitchPercent(DjDeckId.DeckB, e.NewValue, roundToHundredth: true);
    }

    private void DjPitchBorderA_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.10 : 0.01;
        double baseVal = Math.Round(_studio.DjMixer.DeckA.PitchPercent, 2);
        SetDeckPitchPercent(DjDeckId.DeckA, baseVal + notches * step, roundToHundredth: true);
        e.Handled = true;
    }

    private void DjPitchBorderB_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.10 : 0.01;
        double baseVal = Math.Round(_studio.DjMixer.DeckB.PitchPercent, 2);
        SetDeckPitchPercent(DjDeckId.DeckB, baseVal + notches * step, roundToHundredth: true);
        e.Handled = true;
    }

    // Drift-proof delta-based Pitch Fader dragging + Grab Glow + ALT+Click reset + Accidental Bar-Click Guard
    private void SldDjPitchA_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 || IsAltModifierHeld())
        {
            SetDeckPitchPercent(DjDeckId.DeckA, 0.0, roundToHundredth: true);
            SetStatus("Deck A Pitch reset to +0.00%.");
            e.Handled = true;
            return;
        }

        if (!IsSliderThumbClicked(SldDjPitchA, e.OriginalSource as DependencyObject))
        {
            // Ignore stray clicks on the pitch track bar outside the thumb so pitch never jumps!
            e.Handled = true;
            return;
        }

        _djFineDraggingPitchA = true;
        _djPitchDragMovedA = false;
        _djPitchDragStartPtA = e.GetPosition(this);
        _djPitchDragStartValA = _studio.DjMixer.DeckA.PitchPercent;
        SldDjPitchA.Tag = "GRABBED";
        SldDjPitchA.CaptureMouse();
        e.Handled = true;
    }

    private void SldDjPitchA_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_djFineDraggingPitchA) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPitchDragA();
            return;
        }

        double dy = _djPitchDragStartPtA.Y - e.GetPosition(this).Y;
        if (!_djPitchDragMovedA && Math.Abs(dy) < 1.5)
            return;

        _djPitchDragMovedA = true;
        bool fineMode = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        double pctPerPx = fineMode
            ? 0.01
            : (SldDjPitchA.Maximum - SldDjPitchA.Minimum) / Math.Max(40.0, SldDjPitchA.ActualHeight - 16.0);

        SetDeckPitchPercent(DjDeckId.DeckA, _djPitchDragStartValA + dy * pctPerPx, roundToHundredth: true);
        e.Handled = true;
    }

    private void SldDjPitchA_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_djFineDraggingPitchA)
        {
            EndPitchDragA();
            e.Handled = true;
        }
    }

    private void SldDjPitchA_LostMouseCapture(object sender, MouseEventArgs e) => EndPitchDragA();

    private void EndPitchDragA()
    {
        _djFineDraggingPitchA = false;
        _djPitchDragMovedA = false;
        if (SldDjPitchA != null)
        {
            SldDjPitchA.Tag = null;
            if (SldDjPitchA.IsMouseCaptured)
                SldDjPitchA.ReleaseMouseCapture();
        }
    }

    private void SldDjPitchB_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 || IsAltModifierHeld())
        {
            SetDeckPitchPercent(DjDeckId.DeckB, 0.0, roundToHundredth: true);
            SetStatus("Deck B Pitch reset to +0.00%.");
            e.Handled = true;
            return;
        }

        if (!IsSliderThumbClicked(SldDjPitchB, e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        _djFineDraggingPitchB = true;
        _djPitchDragMovedB = false;
        _djPitchDragStartPtB = e.GetPosition(this);
        _djPitchDragStartValB = _studio.DjMixer.DeckB.PitchPercent;
        SldDjPitchB.Tag = "GRABBED";
        SldDjPitchB.CaptureMouse();
        e.Handled = true;
    }

    private void SldDjPitchB_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_djFineDraggingPitchB) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPitchDragB();
            return;
        }

        double dy = _djPitchDragStartPtB.Y - e.GetPosition(this).Y;
        if (!_djPitchDragMovedB && Math.Abs(dy) < 1.5)
            return;

        _djPitchDragMovedB = true;
        bool fineMode = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        double pctPerPx = fineMode
            ? 0.01
            : (SldDjPitchB.Maximum - SldDjPitchB.Minimum) / Math.Max(40.0, SldDjPitchB.ActualHeight - 16.0);

        SetDeckPitchPercent(DjDeckId.DeckB, _djPitchDragStartValB + dy * pctPerPx, roundToHundredth: true);
        e.Handled = true;
    }

    private void SldDjPitchB_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_djFineDraggingPitchB)
        {
            EndPitchDragB();
            e.Handled = true;
        }
    }

    private void SldDjPitchB_LostMouseCapture(object sender, MouseEventArgs e) => EndPitchDragB();

    private void EndPitchDragB()
    {
        _djFineDraggingPitchB = false;
        _djPitchDragMovedB = false;
        if (SldDjPitchB != null)
        {
            SldDjPitchB.Tag = null;
            if (SldDjPitchB.IsMouseCaptured)
                SldDjPitchB.ReleaseMouseCapture();
        }
    }

    private void SldDjPitchA_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SetDeckPitchPercent(DjDeckId.DeckA, 0.0);
    private void SldDjPitchB_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SetDeckPitchPercent(DjDeckId.DeckB, 0.0);
    private void TxtDjPitchA_ResetClick(object sender, MouseButtonEventArgs e) => SetDeckPitchPercent(DjDeckId.DeckA, 0.0);
    private void TxtDjPitchB_ResetClick(object sender, MouseButtonEventArgs e) => SetDeckPitchPercent(DjDeckId.DeckB, 0.0);

    private void BtnDjBendMinusA_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = -3.5;
    private void BtnDjBendPlusA_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = 3.5;
    private void BtnDjBendA_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = 0.0;

    private void BtnDjBendMinusB_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = -3.5;
    private void BtnDjBendPlusB_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = 3.5;
    private void BtnDjBendB_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = 0.0;

    private void SldDjVolA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _studio.DjMixer.DeckA.ChannelVolume = e.NewValue;
    private void SldDjVolB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _studio.DjMixer.DeckB.ChannelVolume = e.NewValue;

    /// <summary>
    /// Protects vertical DJ channel volume faders during live mixing:
    /// - Normal clicks on the fader bar outside the Thumb are ignored (no accidental jumps to Max/Min!).
    /// - Dragging the Thumb works normally.
    /// - Holding ALT + Click on the fader bar jumps cleanly to MAX (100%) above the thumb or MIN (0%) below the thumb!
    /// </summary>
    private void SldDjVol_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider)
            return;

        bool clickedThumb = false;
        DependencyObject? cur = e.OriginalSource as DependencyObject;
        while (cur != null && !ReferenceEquals(cur, slider))
        {
            if (cur is System.Windows.Controls.Primitives.Thumb)
            {
                clickedThumb = true;
                break;
            }
            cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }

        bool altHeld = (Keyboard.Modifiers & ModifierKeys.Alt) != 0
            || Keyboard.IsKeyDown(Key.LeftAlt)
            || Keyboard.IsKeyDown(Key.RightAlt);

        if (altHeld)
        {
            Point pt = e.GetPosition(slider);
            double h = Math.Max(1.0, slider.ActualHeight);
            double thumbY = (1.0 - slider.Value) * h;

            double targetVal;
            if (slider.Value >= 0.99 && pt.Y > 14.0)
                targetVal = 0.0;
            else if (slider.Value <= 0.01 && pt.Y < h - 14.0)
                targetVal = 1.0;
            else
                targetVal = pt.Y < thumbY ? 1.0 : 0.0;

            slider.Value = targetVal;
            string deckName = ReferenceEquals(slider, SldDjVolA) ? "Deck A" : "Deck B";
            SetStatus($"{deckName} Channel Volume: {(targetVal > 0.5 ? "MAX (100% / 0.0 dB)" : "MIN (0% / Muted)")} [ALT+Click]");
            e.Handled = true;
            return;
        }

        if (!clickedThumb)
        {
            // Ignore plain clicks on the track bar so volume never jumps accidentally mid-mix!
            e.Handled = true;
        }
    }

    private void SldDjCrossfader_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents) return;
        _studio.DjMixer.Crossfader = e.NewValue;
    }

    private void SldDjCrossfader_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SldDjCrossfader.Value = 0.0;

    private void BtnDjPflA_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckA;
        d.HeadphoneCueEnabled = !d.HeadphoneCueEnabled;
        UpdateToggleButtonVisual(BtnDjPflA, d.HeadphoneCueEnabled, "Cyan");
    }

    private void BtnDjPflB_Click(object sender, RoutedEventArgs e)
    {
        var d = _studio.DjMixer.DeckB;
        d.HeadphoneCueEnabled = !d.HeadphoneCueEnabled;
        UpdateToggleButtonVisual(BtnDjPflB, d.HeadphoneCueEnabled, "Red");
    }

    // Loops & Hot Cues
    private void BtnDjLoopToggleA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ToggleAutoLoop(DjDeckId.DeckA);
    private void BtnDjLoopToggleB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ToggleAutoLoop(DjDeckId.DeckB);
    private void BtnDjLoopHalfA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ScaleLoopLength(DjDeckId.DeckA, 0.5);
    private void BtnDjLoopDoubleA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ScaleLoopLength(DjDeckId.DeckA, 2.0);
    private void BtnDjLoopHalfB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ScaleLoopLength(DjDeckId.DeckB, 0.5);
    private void BtnDjLoopDoubleB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.ScaleLoopLength(DjDeckId.DeckB, 2.0);
    private void BtnDjLoopInA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.SetManualLoopIn(DjDeckId.DeckA);
    private void BtnDjLoopOutA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.SetManualLoopOut(DjDeckId.DeckA);
    private void BtnDjLoopInB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.SetManualLoopIn(DjDeckId.DeckB);
    private void BtnDjLoopOutB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.SetManualLoopOut(DjDeckId.DeckB);

    private void BtnDjLoopQuickA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && double.TryParse(s, CultureInfo.InvariantCulture, out double beats))
            _studio.DjMixer.ToggleAutoLoop(DjDeckId.DeckA, beats);
    }

    private void BtnDjLoopQuickB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && double.TryParse(s, CultureInfo.InvariantCulture, out double beats))
            _studio.DjMixer.ToggleAutoLoop(DjDeckId.DeckB, beats);
    }

    private void BtnDjHotCueA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
            HandleDeckHotCueAction(DjDeckId.DeckA, idx, clearOnly: false);
    }

    private void BtnDjHotCueA_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
        {
            HandleDeckHotCueAction(DjDeckId.DeckA, idx, clearOnly: true);
            e.Handled = true;
        }
    }

    private void BtnDjHotCueB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
            HandleDeckHotCueAction(DjDeckId.DeckB, idx, clearOnly: false);
    }

    private void BtnDjHotCueB_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
        {
            HandleDeckHotCueAction(DjDeckId.DeckB, idx, clearOnly: true);
            e.Handled = true;
        }
    }

    private void HandleDeckHotCueAction(DjDeckId deckId, int idx, bool clearOnly)
    {
        bool setCue = !clearOnly && IsAltModifierHeld();
        _studio.DjMixer.TriggerHotCue(deckId, idx, clearOnly: clearOnly, setCuePoint: setCue);

        var deck = _studio.DjMixer.GetDeck(deckId);
        if ((clearOnly || setCue) && deck.Track != null)
        {
            _studio.DjDecoder.SaveTrackCues(deck.Track, updateMainCue: false);
        }

        RefreshDeckHotCueVisuals(deckId);
        var snap = _studio.DjMixer.GetSnapshot();
        bool isA = deckId == DjDeckId.DeckA;
        UpdateDeckTelemetryUi(isA ? snap.DeckA : snap.DeckB, isA);
        RenderDjStaticOverviews();
        SetStatus(snap.StatusMessage);
    }

    private void RefreshDeckHotCueVisuals(DjDeckId deckId)
    {
        bool isA = deckId == DjDeckId.DeckA;
        var track = _studio.DjMixer.GetDeck(deckId).Track;
        string accent = isA ? "Cyan" : "Red";
        Color activeBg = isA ? Color.FromRgb(16, 48, 76) : Color.FromRgb(56, 18, 36);

        Button[] btns = isA
            ? new[] { BtnDjHot1A, BtnDjHot2A, BtnDjHot3A, BtnDjHot4A }
            : new[] { BtnDjHot1B, BtnDjHot2B, BtnDjHot3B, BtnDjHot4B };

        for (int i = 0; i < 4; i++)
        {
            bool hasCue = track != null && track.HotCueFrames[i] >= 0;
            ApplyStemPadVisual(btns[i], hasCue, accent, activeBg);
        }
    }

    // 3-Slot Deck FX Rack
    private void BtnDjFxA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int slot))
        {
            var d = _studio.DjMixer.DeckA;
            d.FxEnabled[slot] = !d.FxEnabled[slot];
            UpdateToggleButtonVisual(btn, d.FxEnabled[slot], "Cyan");
        }
    }

    private void SldDjFxA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider sld && sld.Tag is string s && int.TryParse(s, out int slot) && _studio?.DjMixer != null)
        {
            _studio.DjMixer.DeckA.FxDepth[slot] = e.NewValue;
        }
    }

    private void BtnDjFxB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int slot))
        {
            var d = _studio.DjMixer.DeckB;
            d.FxEnabled[slot] = !d.FxEnabled[slot];
            UpdateToggleButtonVisual(btn, d.FxEnabled[slot], "Red");
        }
    }

    private void SldDjFxB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider sld && sld.Tag is string s && int.TryParse(s, out int slot) && _studio?.DjMixer != null)
        {
            _studio.DjMixer.DeckB.FxDepth[slot] = e.NewValue;
        }
    }

    // 6 Stem / Frequency Isolator Performance Pads
    private void BtnDjStemPadA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pad)
        {
            ToggleDeckStemPad(_studio.DjMixer.DeckA, pad, "Deck A");
            RefreshDeckStemPadVisuals(isDeckA: true);
        }
    }

    private void BtnDjStemPadB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pad)
        {
            ToggleDeckStemPad(_studio.DjMixer.DeckB, pad, "Deck B");
            RefreshDeckStemPadVisuals(isDeckA: false);
        }
    }

    private void ToggleDeckStemPad(DjDeckChannelState deck, string pad, string deckName)
    {
        switch (pad)
        {
            case "Vocal":
                deck.PadVocalActive = !deck.PadVocalActive;
                if (deck.PadVocalActive)
                {
                    deck.PadInstruActive = false;
                }
                SetStatus(deck.PadVocalActive
                    ? $"{deckName} STEMS: Vocal / Acapella Isolator ON {(deck.PadBassActive ? "(+ Bass Stem)" : "(24 dB/oct <210 Hz & >4.3 kHz stripped + 4-band center extraction)")}"
                    : $"{deckName} STEMS: Vocal Isolator OFF");
                break;

            case "Instru":
                deck.PadInstruActive = !deck.PadInstruActive;
                if (deck.PadInstruActive)
                {
                    deck.PadVocalActive = false;
                    deck.PadBassActive = false;
                }
                SetStatus(deck.PadInstruActive
                    ? $"{deckName} STEMS: Instrumental (Vocal Remover) ON — Center vocals cancelled, 100% Kick/Bass & Stereo Instruments preserved"
                    : $"{deckName} STEMS: Instrumental OFF");
                break;

            case "Bass":
                deck.PadBassActive = !deck.PadBassActive;
                if (deck.PadBassActive)
                {
                    deck.PadInstruActive = false;
                    deck.PadKickKillActive = false;
                }
                SetStatus(deck.PadBassActive
                    ? $"{deckName} STEMS: Bass & Kick Stem Solo ON (< 210 Hz 24 dB/oct LR4){(deck.PadVocalActive ? " + Vocal Stem" : "")}"
                    : $"{deckName} STEMS: Bass & Kick Stem Solo OFF");
                break;

            case "Kick":
                deck.PadKickKillActive = !deck.PadKickKillActive;
                if (deck.PadKickKillActive)
                {
                    deck.PadBassActive = false;
                }
                SetStatus($"{deckName} ISOLATOR: Kick & Sub-Bass Kill (< 210 Hz 24 dB/oct): {(deck.PadKickKillActive ? "KILLED (-∞ dB)" : "OFF")}");
                break;

            case "HiHat":
                deck.PadHiHatKillActive = !deck.PadHiHatKillActive;
                SetStatus($"{deckName} ISOLATOR: Hi-Hat & Cymbal Kill (> 2.9 kHz 24 dB/oct): {(deck.PadHiHatKillActive ? "KILLED (-∞ dB)" : "OFF")}");
                break;

            case "Gate":
                deck.PadStemsFxGateActive = !deck.PadStemsFxGateActive;
                SetStatus($"{deckName} STEMS FX: BPM-Synced 1/8-Note Trance Gate: {(deck.PadStemsFxGateActive ? "ACTIVE" : "OFF")}");
                break;
        }
    }

    private void RefreshDeckStemPadVisuals(bool isDeckA)
    {
        var d = isDeckA ? _studio.DjMixer.DeckA : _studio.DjMixer.DeckB;
        string accent = isDeckA ? "Cyan" : "Red";
        Color activeBg = isDeckA ? Color.FromRgb(16, 48, 76) : Color.FromRgb(56, 18, 36);

        ApplyStemPadVisual(isDeckA ? BtnDjPadVocalA : BtnDjPadVocalB, d.PadVocalActive, accent, activeBg);
        ApplyStemPadVisual(isDeckA ? BtnDjPadInstruA : BtnDjPadInstruB, d.PadInstruActive, accent, activeBg);
        ApplyStemPadVisual(isDeckA ? BtnDjPadBassA : BtnDjPadBassB, d.PadBassActive, accent, activeBg);
        ApplyStemPadVisual(isDeckA ? BtnDjPadKickA : BtnDjPadKickB, d.PadKickKillActive, accent, activeBg);
        ApplyStemPadVisual(isDeckA ? BtnDjPadHiHatA : BtnDjPadHiHatB, d.PadHiHatKillActive, accent, activeBg);
        ApplyStemPadVisual(isDeckA ? BtnDjPadGateA : BtnDjPadGateB, d.PadStemsFxGateActive, accent, activeBg);
    }

    private void ApplyStemPadVisual(Button? btn, bool active, string accentResource, Color activeBg)
    {
        if (btn == null) return;
        UpdateToggleButtonVisual(btn, active, accentResource);
        if (active)
            btn.Background = new SolidColorBrush(activeBg);
        else
            btn.ClearValue(Control.BackgroundProperty);
    }

    private void UpdateToggleButtonVisual(Button btn, bool active, string accentResource)
    {
        btn.Foreground = active ? (Brush)FindResource(accentResource) : (Brush)FindResource("Text");
        btn.BorderBrush = active ? (Brush)FindResource(accentResource) : (Brush)FindResource("Line");
    }

    // =========================================================================
    // TOP-RIGHT QUICK ACTIONS: ☑ LOAD AS-IS, ● REC MIX, ⇄ AUTO-MIX, CRATE TOGGLE & ⛶ FOCUS
    // =========================================================================

    private void ChkDjLoadAsIs_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded || ChkDjLoadAsIs == null || _studio?.DjMixer == null)
            return;

        bool loadAsIs = ChkDjLoadAsIs.IsChecked == true;
        _appSettings.DjLoadAsIsNativeQuality = loadAsIs;
        _studio.DjMixer.PureNativeQualityMode = loadAsIs;
        _studio.Presets.SaveAppSettings(_appSettings);

        // If any track is currently loaded in Deck A or Deck B, reload it in the selected mode while preserving playhead position!
        if (_studio.DjMixer.DeckA.Track != null)
        {
            string pathA = _studio.DjMixer.DeckA.Track.FilePath;
            LoadFileIntoDeckAsync(DjDeckId.DeckA, pathA);
        }
        if (_studio.DjMixer.DeckB.Track != null)
        {
            string pathB = _studio.DjMixer.DeckB.Track.FilePath;
            LoadFileIntoDeckAsync(DjDeckId.DeckB, pathB);
        }

        SetStatus(loadAsIs
            ? "DJ Quality Mode: Load As-Is (Pure Native Quality) — 100% original sample rate (up to 192 kHz), 0 dB unity bit-exact pass-through."
            : "DJ Quality Mode: Standard (48 kHz Resampled / RAM-Saver with -4.7 dB studio headroom).");
    }

    private void BtnDjRecMix_Click(object sender, RoutedEventArgs e)
    {
        if (!_studio.Recorder.IsRecording)
        {
            if (string.Equals(TxtRecPrefix.Text.Trim(), "Spectra_Sample", StringComparison.OrdinalIgnoreCase))
            {
                TxtRecPrefix.Text = "Spectra_DJ_Mix";
            }
            BtnRecStart_Click(sender, e);
        }
        else
        {
            BtnRecStop_Click(sender, e);
            // If currently viewing SpectraRecordings in the crate, refresh immediately!
            if (string.Equals(_djCurrentFolderPath, GetRecordingOutputFolder(), StringComparison.OrdinalIgnoreCase))
            {
                LoadDjFolderTracks(_djCurrentFolderPath);
            }
        }
    }

    private void BtnDjAutoMix_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.StartAutoCrossfade(5.0);
        SetStatus("Auto-Mix engaged — smoothly crossfading to incoming deck over 5.0 seconds...");
    }

    private void BtnDjToggleCrate_Click(object sender, RoutedEventArgs e)
    {
        _djCrateCollapsed = !_djCrateCollapsed;
        if (_djCrateCollapsed)
        {
            _djSavedCrateHeight = RowDjBrowser.Height;
            BorderDjBrowser.Visibility = Visibility.Collapsed;
            RowDjBrowser.MinHeight = 0;
            RowDjBrowser.Height = new GridLength(0);
            BtnDjToggleCrate.Content = "Crate ▴";
        }
        else
        {
            BorderDjBrowser.Visibility = Visibility.Visible;
            RowDjBrowser.MinHeight = 95;
            RowDjBrowser.Height = _djSavedCrateHeight.Value > 40 ? _djSavedCrateHeight : new GridLength(1, GridUnitType.Star);
            BtnDjToggleCrate.Content = "Crate ▾";
        }
    }

    private void BtnDjFocus_Click(object sender, RoutedEventArgs e)
    {
        SetVisualizerFocusMode(!_visFocusMode);
        if (BtnDjFocus != null)
            BtnDjFocus.Content = _visFocusMode ? "✕ Exit Focus" : "⛶ Focus";
    }

    private void BtnDjCollapseTopBar_Click(object sender, RoutedEventArgs e)
    {
        bool nextCollapsed = !_appSettings.DjTopToolbarCollapsed;
        _appSettings.DjTopToolbarCollapsed = nextCollapsed;
        _studio.Presets.SaveAppSettings(_appSettings);
        ApplyDjTopToolbarCollapsedState(nextCollapsed);
        SetStatus(nextCollapsed
            ? "Collapsed DJ top toolbar into compact icon button — full beat waveform view clear!"
            : "Expanded DJ top toolbar.");
    }

    private void ApplyDjTopToolbarCollapsedState(bool collapsed)
    {
        if (PanelDjTopBarItems == null || BtnDjCollapseTopBar == null || BorderDjTopBar == null)
            return;

        PanelDjTopBarItems.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BorderDjTopBar.Padding = collapsed ? new Thickness(3, 2, 3, 2) : new Thickness(8, 3, 6, 3);
        BtnDjCollapseTopBar.Content = collapsed ? "« ☰" : "»";
        BtnDjCollapseTopBar.ToolTip = collapsed
            ? "Show DJ Waveform Toolbar (Zoom, Load As-Is, REC MIX, Auto-Mix, Visualizer, Crate, Focus)"
            : "Collapse DJ toolbar into a single compact icon button so the top waveform stays 100% clear!";
    }

    // =========================================================================
    // BOTTOM LOCAL MUSIC FOLDER TREE & DJ CRATE BROWSER (IMAGE 2)
    // =========================================================================

    private void InitializeDjFolderTree()
    {
        TreeDjFolders.Items.Clear();

        string musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloadsDir = Path.Combine(userProfile, "Downloads");
        string recordingsDir = GetRecordingOutputFolder();
        Directory.CreateDirectory(recordingsDir);

        // Quick-access root locations (matching Image 2: Music, Desktop\Music, Desktop\MyDJMusic, SpectraRecordings, Drives)
        AddFolderTreeNode(TreeDjFolders.Items, "🎵 Music", musicDir, expandChildren: true);

        string desktopMusic = Path.Combine(desktopDir, "Music");
        if (Directory.Exists(desktopMusic))
            AddFolderTreeNode(TreeDjFolders.Items, "🎶 Desktop \\ Music", desktopMusic, expandChildren: false);

        string desktopDjMusic = Path.Combine(desktopDir, "MyDJMusic");
        if (Directory.Exists(desktopDjMusic))
            AddFolderTreeNode(TreeDjFolders.Items, "🎧 MyDJMusic", desktopDjMusic, expandChildren: false);

        AddFolderTreeNode(TreeDjFolders.Items, "🔴 SpectraRecordings", recordingsDir, expandChildren: false);
        AddFolderTreeNode(TreeDjFolders.Items, "🖥 Desktop", desktopDir, expandChildren: true);

        if (Directory.Exists(downloadsDir))
            AddFolderTreeNode(TreeDjFolders.Items, "📥 Downloads", downloadsDir, expandChildren: false);

        foreach (string custom in _appSettings.DjCustomFolders.ToList())
        {
            if (Directory.Exists(custom))
                AddFolderTreeNode(TreeDjFolders.Items, $"📁 {Path.GetFileName(custom)}", custom, expandChildren: false);
        }

        // Local Drives node
        var drivesRoot = new TreeViewItem { Header = "💽 Drives", IsExpanded = false };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            AddFolderTreeNode(drivesRoot.Items, $"💽 {drive.Name}", drive.RootDirectory.FullName, expandChildren: false);
        }
        TreeDjFolders.Items.Add(drivesRoot);

        // Select initial folder (saved DjLastFolderPath, or Desktop\MyDJMusic, or Desktop\Music, or Music, or SpectraRecordings)
        string initialFolder = _appSettings.DjLastFolderPath;
        if (string.IsNullOrWhiteSpace(initialFolder) || !Directory.Exists(initialFolder))
        {
            initialFolder = Directory.Exists(desktopDjMusic)
                ? desktopDjMusic
                : Directory.Exists(desktopMusic)
                    ? desktopMusic
                    : Directory.Exists(musicDir) && _studio.DjDecoder.ScanFolder(musicDir).Count > 0
                        ? musicDir
                        : recordingsDir;
        }

        LoadDjFolderTracks(initialFolder);
    }

    private void AddFolderTreeNode(ItemCollection parent, string header, string fullPath, bool expandChildren)
    {
        if (!Directory.Exists(fullPath))
            return;

        var item = new TreeViewItem
        {
            Header = header,
            Tag = fullPath,
            IsExpanded = expandChildren
        };

        try
        {
            var subDirs = Directory.EnumerateDirectories(fullPath)
                .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden))
                .Take(35)
                .ToList();

            foreach (string sub in subDirs)
            {
                var child = new TreeViewItem
                {
                    Header = $"📁 {Path.GetFileName(sub)}",
                    Tag = sub
                };
                item.Items.Add(child);
            }
        }
        catch
        {
            // Ignore permission-restricted folders
        }

        parent.Add(item);
    }

    private void TreeDjFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (TreeDjFolders.SelectedItem is TreeViewItem item && item.Tag is string folderPath && Directory.Exists(folderPath))
        {
            // Lazily populate grandchildren when clicked
            if (item.Items.Count == 0)
            {
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(folderPath).Take(35))
                    {
                        if (!new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.Hidden))
                        {
                            item.Items.Add(new TreeViewItem { Header = $"📁 {Path.GetFileName(sub)}", Tag = sub });
                        }
                    }
                }
                catch { }
            }

            _appSettings.DjLastFolderPath = folderPath;
            _studio.Presets.SaveAppSettings(_appSettings);
            LoadDjFolderTracks(folderPath);
        }
    }

    private void BtnDjAddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder containing music or music videos for your DJ Crate",
            UseDescriptionForTitle = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
        {
            if (!_appSettings.DjCustomFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
            {
                _appSettings.DjCustomFolders.Add(dlg.SelectedPath);
            }
            _appSettings.DjLastFolderPath = dlg.SelectedPath;
            _studio.Presets.SaveAppSettings(_appSettings);
            InitializeDjFolderTree();
            LoadDjFolderTracks(dlg.SelectedPath);
        }
    }

    private void LoadDjFolderTracks(string folderPath)
    {
        _djCurrentFolderPath = folderPath;
        _djAllFolderTracks.Clear();
        _djAllFolderTracks.AddRange(_studio.DjDecoder.ScanFolder(folderPath));
        ApplyDjCrateSearchFilter();
    }

    private void TxtDjSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDjCrateSearchFilter();
    }

    private void ApplyDjCrateSearchFilter()
    {
        if (TxtDjFileCount == null)
            return;

        string q = TxtDjSearch?.Text.Trim() ?? string.Empty;
        _djFilteredTracks.Clear();

        foreach (var t in _djAllFolderTracks)
        {
            if (string.IsNullOrEmpty(q)
                || t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Remix.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.MusicalKey.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.BpmText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _djFilteredTracks.Add(t);
            }
        }

        TxtDjFileCount.Text = $"● {_djFilteredTracks.Count} files";
    }

    private void BtnDjAnalyzeFolder_Click(object sender, RoutedEventArgs e)
    {
        var tracksToAnalyze = _djAllFolderTracks.ToList();
        if (tracksToAnalyze.Count == 0)
        {
            SetStatus("No tracks in the current folder to analyze.");
            return;
        }

        _djAnalyzeCts?.Cancel();
        _djAnalyzeCts = new CancellationTokenSource();
        var token = _djAnalyzeCts.Token;

        BtnDjAnalyzeFolder.IsEnabled = false;
        SetStatus($"Analyzing precision BPM & Musical Key for {tracksToAnalyze.Count} track(s)...");

        Task.Run(() =>
        {
            int done = 0;
            foreach (var item in tracksToAnalyze)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var loaded = _studio.DjDecoder.LoadAndAnalyzeTrack(item.FilePath, forceReanalyze: true);
                    done++;
                    Dispatcher.Invoke(() =>
                    {
                        item.Bpm = loaded.DetectedBpm;
                        item.MusicalKey = loaded.MusicalKey;
                        item.DurationSec = loaded.DurationSec;
                        item.LengthText = DjTrackDecoderService.FormatDuration(loaded.DurationSec);
                        item.IsAnalyzed = true;

                        // If this track is currently loaded in Deck A or Deck B, update its live BPM & Key immediately!
                        if (_studio.DjMixer.DeckA.Track != null &&
                            string.Equals(_studio.DjMixer.DeckA.Track.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            _studio.DjMixer.DeckA.ManualBpmOverride = loaded.DetectedBpm;
                        }
                        if (_studio.DjMixer.DeckB.Track != null &&
                            string.Equals(_studio.DjMixer.DeckB.Track.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            _studio.DjMixer.DeckB.ManualBpmOverride = loaded.DetectedBpm;
                        }

                        GridDjTracks.Items.Refresh();
                        SetStatus($"Analyzed [{done}/{tracksToAnalyze.Count}] {loaded.Artist} — {loaded.Title}: {loaded.DetectedBpm:0.0} BPM • Key {loaded.MusicalKey}");
                    });
                }
                catch
                {
                    // Skip unreadable file
                }
            }

            Dispatcher.Invoke(() =>
            {
                BtnDjAnalyzeFolder.IsEnabled = true;
                if (!token.IsCancellationRequested)
                {
                    SetStatus($"Completed precision BPM & Musical Key analysis for {done} track(s).");
                }
            });
        }, token);
    }

    private void BtnDjOpenSingleFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Audio or Music Video Track to DJ Deck",
            Filter = "All DJ Media (*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.mp4;*.mkv;*.webm)|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.mp4;*.mkv;*.webm|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            var targetDeck = !_studio.DjMixer.DeckA.IsPlaying ? DjDeckId.DeckA : DjDeckId.DeckB;
            LoadFileIntoDeckAsync(targetDeck, dlg.FileName);
        }
    }

    private void BtnDjLoadSelectedA_Click(object sender, RoutedEventArgs e)
    {
        if (GridDjTracks.SelectedItem is DjLibraryTrackItem item)
            LoadFileIntoDeckAsync(DjDeckId.DeckA, item.FilePath, item);
    }

    private void BtnDjLoadSelectedB_Click(object sender, RoutedEventArgs e)
    {
        if (GridDjTracks.SelectedItem is DjLibraryTrackItem item)
            LoadFileIntoDeckAsync(DjDeckId.DeckB, item.FilePath, item);
    }

    private void BtnDjRowLoadA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DjLibraryTrackItem item)
            LoadFileIntoDeckAsync(DjDeckId.DeckA, item.FilePath, item);
    }

    private void BtnDjRowLoadB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DjLibraryTrackItem item)
            LoadFileIntoDeckAsync(DjDeckId.DeckB, item.FilePath, item);
    }

    private void GridDjTracks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridDjTracks.SelectedItem is DjLibraryTrackItem item)
        {
            // Smart double-click: load into whichever deck is not currently playing (or Deck A if both are stopped)
            var targetDeck = !_studio.DjMixer.DeckA.IsPlaying
                ? DjDeckId.DeckA
                : !_studio.DjMixer.DeckB.IsPlaying
                    ? DjDeckId.DeckB
                    : DjDeckId.DeckA;
            LoadFileIntoDeckAsync(targetDeck, item.FilePath, item);
        }
    }

    private void LoadFileIntoDeckAsync(DjDeckId deckId, string filePath, DjLibraryTrackItem? rowItem = null)
    {
        string deckName = deckId == DjDeckId.DeckA ? "Deck A" : "Deck B";
        string shortName = Path.GetFileName(filePath);
        if (deckId == DjDeckId.DeckA)
        {
            _djLoadingFileA = shortName;
            _djLoadingDeckA = true;
            TxtDjTitleA.Text = $"⏳ Loading {shortName}...";
            TxtDjArtistA.Text = "Decoding audio, waveform pyramid, BPM & musical key...";
        }
        else
        {
            _djLoadingFileB = shortName;
            _djLoadingDeckB = true;
            TxtDjTitleB.Text = $"⏳ Loading {shortName}...";
            TxtDjArtistB.Text = "Decoding audio, waveform pyramid, BPM & musical key...";
        }

        bool preserveNative = _appSettings.DjLoadAsIsNativeQuality;
        SetStatus($"Decoding & analyzing '{shortName}' for {deckName} ({(preserveNative ? "Load As-Is Pure Native" : "48 kHz Standard")})...");

        Task.Run(() =>
        {
            try
            {
                var track = _studio.DjDecoder.LoadAndAnalyzeTrack(filePath, forceReanalyze: false, preserveNativeSampleRate: preserveNative);
                Dispatcher.BeginInvoke(() =>
                {
                    if (deckId == DjDeckId.DeckA) _djLoadingDeckA = false;
                    else _djLoadingDeckB = false;

                    _studio.DjMixer.SetMasterEndpoint(_activePlaybackEndpoint?.EndpointId);
                    _studio.DjMixer.LoadTrackToDeck(deckId, track);

                    if (_djTwoPointDeck == deckId || _djUnlockedBeatDeck == deckId)
                    {
                        _djTwoPointDeck = null;
                        _djUnlockedBeatDeck = null;
                        _djDraggingTwoPointHandle = 0;
                        _djDraggingUnlockedBeat = false;
                        UpdateToggleButtonVisual(BtnDjTwoPtA, false, "Amber");
                        UpdateToggleButtonVisual(BtnDjTwoPtB, false, "Amber");
                        ShowDjCalibrationOverlayUi();
                    }

                    // Immediately update rotary GAIN knob needle to reflect this track's automatically restored GainDb!
                    UpdateAllDjKnobsVisual();

                    if (rowItem != null)
                    {
                        rowItem.Bpm = track.DetectedBpm;
                        rowItem.MusicalKey = track.MusicalKey;
                        rowItem.DurationSec = track.DurationSec;
                        rowItem.LengthText = DjTrackDecoderService.FormatDuration(track.DurationSec);
                        rowItem.IsAnalyzed = true;
                        GridDjTracks.Items.Refresh();
                    }

                    // Reset Pitch Fader cleanly to +0.00% and restore saved Hot Cue pad highlights on new track load!
                    if (deckId == DjDeckId.DeckA)
                    {
                        SldDjPitchA.Minimum = -10.0;
                        SldDjPitchA.Maximum = 10.0;
                    }
                    else
                    {
                        SldDjPitchB.Minimum = -10.0;
                        SldDjPitchB.Maximum = 10.0;
                    }
                    SetDeckPitchPercent(deckId, 0.0, roundToHundredth: true);
                    RefreshDeckHotCueVisuals(deckId);

                    var snap = _studio.DjMixer.GetSnapshot();
                    UpdateDeckTelemetryUi(snap.DeckA, isDeckA: true);
                    UpdateDeckTelemetryUi(snap.DeckB, isDeckA: false);
                    RenderDjStaticOverviews();
                    if (_djIsActiveTab && _djTimer != null && !_djTimer.IsEnabled)
                    {
                        _djTimer.Start();
                    }
                    SetStatus(snap.StatusMessage);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (deckId == DjDeckId.DeckA) _djLoadingDeckA = false;
                    else _djLoadingDeckB = false;
                    SetStatus($"Could not load track into {deckName}: {ex.Message}");
                });
            }
        });
    }
}
