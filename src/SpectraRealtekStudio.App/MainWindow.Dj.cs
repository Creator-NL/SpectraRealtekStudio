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

    // Interactive mouse drag states (Top Waveform Scrub, Platter Scratch, Rotary Mixer Knobs)
    private bool _djDraggingTopWave;
    private DjDeckId _djTopWaveDragDeck;
    private Point _djTopWaveLastPt;

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
            BtnDjLoopToggleA.Content = d.LoopBeats < 1.0 ? "1/2 Beat" : $"{d.LoopBeats:0.#} Beats";
            BtnDjLoopToggleA.BorderBrush = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Line");
            BtnDjLoopToggleA.Foreground = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Text");
            TxtDjWaveBadgeA.Text = d.IsLoaded ? $"A • {d.Title} ({d.EffectiveBpm:0.0} BPM)" : "A • DECK 1";
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
            BtnDjLoopToggleB.Content = d.LoopBeats < 1.0 ? "1/2 Beat" : $"{d.LoopBeats:0.#} Beats";
            BtnDjLoopToggleB.BorderBrush = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Line");
            BtnDjLoopToggleB.Foreground = d.LoopActive ? (Brush)FindResource("Green") : (Brush)FindResource("Text");
            TxtDjWaveBadgeB.Text = d.IsLoaded ? $"B • {d.Title} ({d.EffectiveBpm:0.0} BPM)" : "B • DECK 2";
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

        // Visible window: 10 seconds of audio across the viewport width (scaled by speed so beat grids line up visually when synced!)
        double speed = Math.Max(0.4, deckSnap.EffectiveBpm / Math.Max(40.0, deckSnap.BaseBpm));
        double visibleWindowSec = 9.0 * speed;
        double secPerPixel = visibleWindowSec / w;
        int centerX = w / 2;

        // 1. Highlight Active Loop Region in translucent emerald green
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
                        _djTopWavePixels[row + x] = 0xFF0A241Eu;
                    }
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

            // Little red/cyan downbeat marker tooth at bottom of lane (just like VirtualDJ in Image 1!)
            if (isDownbeat)
            {
                for (int dy = 0; dy < 4 && yBottom - dy >= yTop; dy++)
                {
                    _djTopWavePixels[(yBottom - dy) * w + bx] = midCol;
                    if (bx + 1 < w) _djTopWavePixels[(yBottom - dy) * w + bx + 1] = midCol;
                }
            }
        }

        // 3. Draw Multi-Band Beat-Colored Scrolling Waveform Envelope
        var peaks = track.BeatPeaks;
        var bass = track.BeatBassRatio;
        int nBins = peaks.Length;
        double envRate = DjLoadedTrack.BeatEnvelopeRateHz;

        for (int x = 0; x < w; x++)
        {
            double tSec = deckSnap.CurrentSec + (x - centerX) * secPerPixel;
            if (tSec < 0.0 || tSec >= track.DurationSec)
                continue;

            double binPos = tSec * envRate;
            int b0 = (int)binPos;
            if (b0 < 0 || b0 >= nBins)
                continue;

            int b1 = Math.Min(nBins - 1, b0 + 1);
            float frac = (float)(binPos - b0);
            float amp = peaks[b0] * (1f - frac) + peaks[b1] * frac;
            float bassRatio = bass[b0] * (1f - frac) + bass[b1] * frac;

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
    // ROTARY MIXER KNOBS (HIGH / MID / LOW / FILTER / GAIN FOR DECK A & DECK B)
    // =========================================================================

    private void UpdateAllDjKnobsVisual()
    {
        DrawDjKnobCanvas(KnobDjHighA, GetDjKnobNormalized("A:High"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjMidA, GetDjKnobNormalized("A:Mid"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjLowA, GetDjKnobNormalized("A:Low"), isDeckA: true);
        DrawDjKnobCanvas(KnobDjFilterA, GetDjKnobNormalized("A:Filter"), isDeckA: true, isFilter: true);
        DrawDjKnobCanvas(KnobDjGainA, GetDjKnobNormalized("A:Gain"), isDeckA: true);

        DrawDjKnobCanvas(KnobDjHighB, GetDjKnobNormalized("B:High"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjMidB, GetDjKnobNormalized("B:Mid"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjLowB, GetDjKnobNormalized("B:Low"), isDeckA: false);
        DrawDjKnobCanvas(KnobDjFilterB, GetDjKnobNormalized("B:Filter"), isDeckA: false, isFilter: true);
        DrawDjKnobCanvas(KnobDjGainB, GetDjKnobNormalized("B:Gain"), isDeckA: false);
    }

    private double GetDjKnobNormalized(string tag)
    {
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
                SetStatus($"{deckLabel} Channel GAIN: {deck.GainDb:+0.0;-0.0;0.0} dB");
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

    private void DrawDjKnobCanvas(Canvas? canvas, double norm, bool isDeckA, bool isFilter = false)
    {
        if (canvas == null)
            return;

        canvas.Children.Clear();
        double size = canvas.Width > 0 ? canvas.Width : 34.0;
        double cx = size / 2.0;
        double cy = size / 2.0;
        double r = cx - 3.0;

        var body = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = new SolidColorBrush(Color.FromRgb(13, 18, 34)),
            Stroke = new SolidColorBrush(isFilter
                ? (isDeckA ? Color.FromRgb(34, 232, 255) : Color.FromRgb(255, 51, 102))
                : Color.FromRgb(42, 56, 98)),
            StrokeThickness = isFilter ? 1.8 : 1.4
        };
        Canvas.SetLeft(body, cx - r);
        Canvas.SetTop(body, cy - r);
        canvas.Children.Add(body);

        // Pointer needle from -135 deg (0.0) to +135 deg (1.0)
        double angleDeg = -135.0 + Math.Clamp(norm, 0.0, 1.0) * 270.0;
        double rad = (angleDeg - 90.0) * Math.PI / 180.0;
        var line = new Line
        {
            X1 = cx + Math.Cos(rad) * 3.0,
            Y1 = cy + Math.Sin(rad) * 3.0,
            X2 = cx + Math.Cos(rad) * (r - 2.0),
            Y2 = cy + Math.Sin(rad) * (r - 2.0),
            Stroke = (Brush)FindResource(isDeckA ? "Cyan" : "Red"),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(line);
    }

    private void DjKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || canvas.Tag is not string tag)
            return;

        var now = DateTime.UtcNow;
        if ((now - _djLastKnobClickUtc).TotalMilliseconds < 320)
        {
            // Double-click resets knob to 0 dB / center bypass!
            double defaultNorm = tag.EndsWith(":Gain", StringComparison.Ordinal) ? (24.0 / 36.0) : 0.5;
            SetDjKnobNormalized(tag, defaultNorm);
            DrawDjKnobCanvas(canvas, defaultNorm, tag.StartsWith("A:", StringComparison.Ordinal), tag.EndsWith(":Filter", StringComparison.Ordinal));
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
        SetDjKnobNormalized(tag, newNorm);
        DrawDjKnobCanvas(_activeDjKnob, newNorm, tag.StartsWith("A:", StringComparison.Ordinal), tag.EndsWith(":Filter", StringComparison.Ordinal));
    }

    private void DjKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeDjKnob != null)
        {
            _activeDjKnob.ReleaseMouseCapture();
            _activeDjKnob = null;
        }
    }

    // =========================================================================
    // INTERACTIVE WAVEFORM SCRUBBING & VINYL PLATTER SCRATCHING
    // =========================================================================

    private void DjTopWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pt = e.GetPosition(DjTopWaveformViewport);
        _djTopWaveDragDeck = pt.Y < DjTopWaveformViewport.ActualHeight / 2.0 ? DjDeckId.DeckA : DjDeckId.DeckB;
        _djTopWaveLastPt = pt;
        _djDraggingTopWave = true;
        _studio.DjMixer.BeginScratch(_djTopWaveDragDeck);
        DjTopWaveformViewport.CaptureMouse();
    }

    private void DjTopWaveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_djDraggingTopWave)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _studio.DjMixer.EndScratch(_djTopWaveDragDeck);
            DjTopWaveformViewport.ReleaseMouseCapture();
            _djDraggingTopWave = false;
            return;
        }

        Point pt = e.GetPosition(DjTopWaveformViewport);
        double dx = pt.X - _djTopWaveLastPt.X;
        _djTopWaveLastPt = pt;

        double secPerPixel = 9.0 / Math.Max(200.0, DjTopWaveformViewport.ActualWidth);
        double deltaSec = -dx * secPerPixel;
        _studio.DjMixer.UpdateScratch(_djTopWaveDragDeck, deltaSec, deltaSec * 25.0);
    }

    private void DjTopWaveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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

    private void BtnDjCueA_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.CueDown(DjDeckId.DeckA);
    private void BtnDjCueA_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.CueUp(DjDeckId.DeckA);
    private void BtnDjCueB_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.CueDown(DjDeckId.DeckB);
    private void BtnDjCueB_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.CueUp(DjDeckId.DeckB);

    private void BtnDjSyncA_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.SyncDeck(DjDeckId.DeckA);
        double reqRange = _studio.DjMixer.DeckA.PitchRangePercent;
        if (SldDjPitchA.Maximum < reqRange)
        {
            SldDjPitchA.Minimum = -reqRange;
            SldDjPitchA.Maximum = reqRange;
        }
        SldDjPitchA.Value = _studio.DjMixer.DeckA.PitchPercent;
        SetStatus(_studio.DjMixer.GetSnapshot().StatusMessage);
    }

    private void BtnDjSyncB_Click(object sender, RoutedEventArgs e)
    {
        _studio.DjMixer.SyncDeck(DjDeckId.DeckB);
        double reqRange = _studio.DjMixer.DeckB.PitchRangePercent;
        if (SldDjPitchB.Maximum < reqRange)
        {
            SldDjPitchB.Minimum = -reqRange;
            SldDjPitchB.Maximum = reqRange;
        }
        SldDjPitchB.Value = _studio.DjMixer.DeckB.PitchPercent;
        SetStatus(_studio.DjMixer.GetSnapshot().StatusMessage);
    }

    private void BtnDjTapA_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.DeckA.RegisterTapTempo();
    private void BtnDjTapB_Click(object sender, RoutedEventArgs e) => _studio.DjMixer.DeckB.RegisterTapTempo();

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

    private void SldDjPitchA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtDjPitchA == null) return;
        _studio.DjMixer.DeckA.PitchPercent = Math.Round(e.NewValue, 2);
        TxtDjPitchA.Text = $"{_studio.DjMixer.DeckA.PitchPercent:+0.0;-0.0;+0.0}%";
    }

    private void SldDjPitchB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtDjPitchB == null) return;
        _studio.DjMixer.DeckB.PitchPercent = Math.Round(e.NewValue, 2);
        TxtDjPitchB.Text = $"{_studio.DjMixer.DeckB.PitchPercent:+0.0;-0.0;+0.0}%";
    }

    private void SldDjPitchA_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SldDjPitchA.Value = 0.0;
    private void SldDjPitchB_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SldDjPitchB.Value = 0.0;
    private void TxtDjPitchA_ResetClick(object sender, MouseButtonEventArgs e) => SldDjPitchA.Value = 0.0;
    private void TxtDjPitchB_ResetClick(object sender, MouseButtonEventArgs e) => SldDjPitchB.Value = 0.0;

    private void BtnDjBendMinusA_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = -3.5;
    private void BtnDjBendPlusA_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = 3.5;
    private void BtnDjBendA_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckA.TempNudgePercent = 0.0;

    private void BtnDjBendMinusB_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = -3.5;
    private void BtnDjBendPlusB_Down(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = 3.5;
    private void BtnDjBendB_Up(object sender, MouseButtonEventArgs e) => _studio.DjMixer.DeckB.TempNudgePercent = 0.0;

    private void SldDjVolA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _studio.DjMixer.DeckA.ChannelVolume = e.NewValue;
    private void SldDjVolB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _studio.DjMixer.DeckB.ChannelVolume = e.NewValue;

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
        {
            _studio.DjMixer.TriggerHotCue(DjDeckId.DeckA, idx, clearOnly: false);
            UpdateToggleButtonVisual(btn, true, "Cyan");
        }
    }

    private void BtnDjHotCueA_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
        {
            _studio.DjMixer.TriggerHotCue(DjDeckId.DeckA, idx, clearOnly: true);
            UpdateToggleButtonVisual(btn, false, "Cyan");
        }
    }

    private void BtnDjHotCueB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
        {
            _studio.DjMixer.TriggerHotCue(DjDeckId.DeckB, idx, clearOnly: false);
            UpdateToggleButtonVisual(btn, true, "Red");
        }
    }

    private void BtnDjHotCueB_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int idx))
        {
            _studio.DjMixer.TriggerHotCue(DjDeckId.DeckB, idx, clearOnly: true);
            UpdateToggleButtonVisual(btn, false, "Red");
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
            bool active = ToggleDeckStemPad(_studio.DjMixer.DeckA, pad);
            UpdateToggleButtonVisual(btn, active, "Cyan");
        }
    }

    private void BtnDjStemPadB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pad)
        {
            bool active = ToggleDeckStemPad(_studio.DjMixer.DeckB, pad);
            UpdateToggleButtonVisual(btn, active, "Red");
        }
    }

    private static bool ToggleDeckStemPad(DjDeckChannelState deck, string pad)
    {
        switch (pad)
        {
            case "Vocal":
                deck.PadVocalActive = !deck.PadVocalActive;
                if (deck.PadVocalActive) deck.PadInstruActive = false;
                return deck.PadVocalActive;
            case "Instru":
                deck.PadInstruActive = !deck.PadInstruActive;
                if (deck.PadInstruActive) deck.PadVocalActive = false;
                return deck.PadInstruActive;
            case "Bass":
                deck.PadBassActive = !deck.PadBassActive;
                return deck.PadBassActive;
            case "Kick":
                deck.PadKickKillActive = !deck.PadKickKillActive;
                return deck.PadKickKillActive;
            case "HiHat":
                deck.PadHiHatKillActive = !deck.PadHiHatKillActive;
                return deck.PadHiHatKillActive;
            case "Gate":
                deck.PadStemsFxGateActive = !deck.PadStemsFxGateActive;
                return deck.PadStemsFxGateActive;
            default:
                return false;
        }
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

                    if (rowItem != null)
                    {
                        rowItem.Bpm = track.DetectedBpm;
                        rowItem.MusicalKey = track.MusicalKey;
                        rowItem.DurationSec = track.DurationSec;
                        rowItem.LengthText = DjTrackDecoderService.FormatDuration(track.DurationSec);
                        rowItem.IsAnalyzed = true;
                        GridDjTracks.Items.Refresh();
                    }

                    // Reset Hot Cue button highlights on new track load
                    if (deckId == DjDeckId.DeckA)
                    {
                        UpdateToggleButtonVisual(BtnDjHot1A, false, "Cyan");
                        UpdateToggleButtonVisual(BtnDjHot2A, false, "Cyan");
                        UpdateToggleButtonVisual(BtnDjHot3A, false, "Cyan");
                        UpdateToggleButtonVisual(BtnDjHot4A, false, "Cyan");
                    }
                    else
                    {
                        UpdateToggleButtonVisual(BtnDjHot1B, false, "Red");
                        UpdateToggleButtonVisual(BtnDjHot2B, false, "Red");
                        UpdateToggleButtonVisual(BtnDjHot3B, false, "Red");
                        UpdateToggleButtonVisual(BtnDjHot4B, false, "Red");
                    }

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
