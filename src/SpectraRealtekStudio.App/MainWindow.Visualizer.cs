using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;

namespace SpectraRealtekStudio.App;

public partial class MainWindow
{
    private DispatcherTimer? _visualizerTimer;
    private WriteableBitmap? _visBitmap;
    private uint[] _visPixels = Array.Empty<uint>();
    private int _visWidth;
    private int _visHeight;
    private bool _visPaused;
    private bool _visIsActiveTab;
    private int _visLastAxisSampleRate;

    // 256-entry precomputed color lookup table (BGRA32) for blazing-fast 60 FPS rendering
    private readonly uint[] _visPaletteLut = new uint[256];
    private string _visCurrentPalette = "Spectra";

    private const uint VisBgColor = 0xFF070A14u;
    private const uint VisGridColor = 0xFF131B33u;
    private const uint VisGridZeroColor = 0xFF23325Cu;

    private void InitializeVisualizer()
    {
        BuildVisualizerPaletteLut("Spectra");
        _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
        };
        _visualizerTimer.Tick += VisualizerTimer_Tick;
    }

    private void StartVisualizer()
    {
        if (_visualizerTimer == null || WindowState == WindowState.Minimized || !IsVisible)
            return;

        EnsureVisualizerBitmap();
        if (!_studio.Visualizer.IsCapturing)
        {
            _studio.Visualizer.Start(_activePlaybackEndpoint?.EndpointId);
        }
        if (!_visualizerTimer.IsEnabled)
        {
            _visualizerTimer.Start();
        }
        UpdateVisualizerHeaderLabels();
        RenderVisualizerFreqAxis();
    }

    private void StopVisualizer()
    {
        _visualizerTimer?.Stop();
        _studio.Visualizer.Stop();
    }

    private void OnActivePlaybackEndpointChangedForVisualizer()
    {
        if (_visIsActiveTab && !_visPaused && IsVisible && WindowState != WindowState.Minimized)
        {
            _studio.Visualizer.Start(_activePlaybackEndpoint?.EndpointId);
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != MainTabControl)
            return;

        bool nowOnVis = MainTabControl.SelectedItem == TabVisualizer;
        if (nowOnVis == _visIsActiveTab)
            return;

        _visIsActiveTab = nowOnVis;
        if (nowOnVis)
        {
            _visPaused = false;
            if (BtnVisPause != null)
                BtnVisPause.Content = "Pause";
            StartVisualizer();
        }
        else
        {
            _visPaused = false;
            if (BtnVisPause != null)
                BtnVisPause.Content = "Pause";
            StopVisualizer();
        }
    }

    private void VisualizerViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureVisualizerBitmap();
        RenderVisualizerFreqAxis();
    }

    private void CanvasVisFreqAxis_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderVisualizerFreqAxis();
    }

    private void VisSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbVisPalette == null || CmbVisMode == null || CmbVisBands == null)
            return;

        string palette = GetSelectedComboTag(CmbVisPalette, "Spectra");
        if (!string.Equals(palette, _visCurrentPalette, StringComparison.OrdinalIgnoreCase))
        {
            BuildVisualizerPaletteLut(palette);
        }

        // Clear buffer cleanly on mode/range change so Waterfall / Oscilloscope / Vectorscope start fresh
        if (_visPixels.Length > 0)
        {
            Array.Fill(_visPixels, VisBgColor);
            FlushVisualizerBitmap();
        }

        UpdateVisualizerHeaderLabels();
        RenderVisualizerFreqAxis();
    }

    private void BtnVisPause_Click(object sender, RoutedEventArgs e)
    {
        _visPaused = !_visPaused;
        BtnVisPause.Content = _visPaused ? "Resume" : "Pause";
        if (_visPaused)
        {
            // Stop both timer and WASAPI capture thread while frozen (0% CPU)
            StopVisualizer();
            TxtVisStreamBadge.Text = "❚❚ PAUSED";
            TxtVisStreamBadge.Foreground = (Brush)FindResource("Amber");
        }
        else
        {
            StartVisualizer();
            TxtVisStreamBadge.Text = "● LIVE 60 FPS";
            TxtVisStreamBadge.Foreground = (Brush)FindResource("Green");
        }
    }

    private void UpdateVisualizerHeaderLabels()
    {
        if (TxtVisHeaderTag == null || CmbVisMode == null || CmbVisBands == null)
            return;

        string mode = GetSelectedComboTag(CmbVisMode, "Bars");
        int bands = GetSelectedVisBands();
        var (minHz, maxHz, linear) = GetSelectedVisRange();
        string rangeLabel = $"{FormatAxisHz(minHz)} — {FormatAxisHz(maxHz)}";
        string scaleWord = linear ? "LINEAR" : "LOGARITHMIC";
        double nyquist = _studio.Visualizer.SampleRate * 0.5;
        string nyqNote = maxHz > nyquist + 500.0 ? $" • NYQUIST {FormatAxisHz(nyquist)}" : string.Empty;

        TxtVisHeaderTag.Text = mode switch
        {
            "Mirror" => $"{bands}-BAND SYMMETRICAL MIRROR FFT SPECTRUM ({rangeLabel} • {scaleWord}{nyqNote})",
            "Oscilloscope" => "LIVE STEREO WAVEFORM OSCILLOSCOPE (CYAN = LEFT • MAGENTA = RIGHT)",
            "Waterfall" => $"{bands}-BAND REAL-TIME SPECTROGRAM WATERFALL ({rangeLabel} • {scaleWord}{nyqNote})",
            "Vectorscope" => "STEREO PHASE VECTORSCOPE (LISSAJOUS XY • MONO VERTICAL / WIDE HORIZONTAL)",
            "Radial" => $"{bands}-BAND 360° RADIAL CYBER HALO SPECTRUM ({rangeLabel})",
            _ => $"{bands}-BAND {scaleWord} FFT SPECTRUM ANALYZER ({rangeLabel}{nyqNote})"
        };

        bool showFreqAxis = mode is "Bars" or "Mirror" or "Waterfall";
        if (CanvasVisFreqAxis != null)
        {
            CanvasVisFreqAxis.Visibility = showFreqAxis ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsureVisualizerBitmap()
    {
        if (VisualizerViewport == null || ImgVisualizer == null)
            return;

        int w = Math.Clamp((int)Math.Round(VisualizerViewport.ActualWidth), 512, 1920);
        int h = Math.Clamp((int)Math.Round(VisualizerViewport.ActualHeight), 260, 1080);
        if (w == _visWidth && h == _visHeight && _visBitmap != null)
            return;

        _visWidth = w;
        _visHeight = h;
        _visPixels = new uint[w * h];
        Array.Fill(_visPixels, VisBgColor);
        _visBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        ImgVisualizer.Source = _visBitmap;
        FlushVisualizerBitmap();
    }

    private void FlushVisualizerBitmap()
    {
        if (_visBitmap == null || _visPixels.Length != _visWidth * _visHeight)
            return;

        _visBitmap.WritePixels(
            new Int32Rect(0, 0, _visWidth, _visHeight),
            _visPixels,
            _visWidth * 4,
            0);
    }

    private void VisualizerTimer_Tick(object? sender, EventArgs e)
    {
        if (!_visIsActiveTab || _visPaused || WindowState == WindowState.Minimized || !IsVisible)
            return;

        if (_visBitmap == null || _visWidth <= 0 || _visHeight <= 0)
        {
            EnsureVisualizerBitmap();
            if (_visBitmap == null)
                return;
        }

        int bandCount = GetSelectedVisBands();
        var (minHz, maxHz, linearScale) = GetSelectedVisRange();
        double sensitivity = SldVisSensitivity?.Value ?? 1.0;
        double smoothing = SldVisSmoothing?.Value ?? 0.72;
        bool peakHold = ChkVisPeakHold?.IsChecked == true;
        string mode = GetSelectedComboTag(CmbVisMode, "Bars");

        var frame = _studio.Visualizer.ComputeFrame(bandCount, sensitivity, smoothing, minHz, maxHz, linearScale);

        switch (mode)
        {
            case "Mirror":
                RenderMirrorSpectrum(frame, peakHold);
                break;
            case "Oscilloscope":
                RenderStereoOscilloscope(frame);
                break;
            case "Waterfall":
                RenderSpectrogramWaterfall(frame);
                break;
            case "Vectorscope":
                RenderStereoVectorscope(frame);
                break;
            case "Radial":
                RenderRadialHaloSpectrum(frame, peakHold);
                break;
            default:
                RenderSpectrumBars(frame, peakHold);
                break;
        }

        FlushVisualizerBitmap();
        if (frame.SampleRate != _visLastAxisSampleRate)
        {
            _visLastAxisSampleRate = frame.SampleRate;
            UpdateVisualizerHeaderLabels();
            RenderVisualizerFreqAxis();
        }
        UpdateVisualizerTelemetry(frame);
    }

    private void UpdateVisualizerTelemetry(AudioVisualizerFrame frame)
    {
        TxtVisIdleHint.Visibility = frame.HasSignal ? Visibility.Collapsed : Visibility.Visible;

        if (frame.IsCapturing)
        {
            TxtVisStreamBadge.Text = frame.HasSignal ? "● LIVE 60 FPS" : "● LISTENING";
            TxtVisStreamBadge.Foreground = frame.HasSignal ? (Brush)FindResource("Green") : (Brush)FindResource("Cyan");
        }
        else
        {
            TxtVisStreamBadge.Text = "○ STANDBY";
            TxtVisStreamBadge.Foreground = (Brush)FindResource("Amber");
        }

        if (frame.DominantFrequencyHz >= 5.0 && frame.HasSignal)
        {
            string bandTag = frame.DominantFrequencyHz switch
            {
                < 20 => "Infrasonic",
                < 80 => "Sub-Bass",
                < 250 => "Bass",
                < 600 => "Low-Mid",
                < 2000 => "Midrange",
                < 6000 => "Presence",
                <= 20000 => "Treble",
                _ => "Ultrasonic"
            };
            string hzLabel = frame.DominantFrequencyHz >= 1000.0
                ? $"{frame.DominantFrequencyHz / 1000.0:0.00} kHz"
                : $"{frame.DominantFrequencyHz:0} Hz";
            TxtVisDomFreq.Text = $"{hzLabel} ({bandTag})";
        }
        else
        {
            TxtVisDomFreq.Text = "—";
        }

        TxtVisRmsDb.Text = $"{frame.RmsDb:0.0} dBFS";
        TxtVisRmsDb.Foreground = frame.RmsDb > -6.0
            ? (Brush)FindResource("Red")
            : frame.RmsDb > -14.0
                ? (Brush)FindResource("Amber")
                : (Brush)FindResource("Green");

        string phaseDesc = frame.PhaseCorrelation switch
        {
            > 0.85 => "Centered / Mono",
            > 0.35 => "Balanced Stereo",
            >= 0.0 => "Wide Stereo",
            _ => "Out-of-Phase"
        };
        TxtVisPhase.Text = $"{frame.PhaseCorrelation:+0.00;-0.00;0.00} ({phaseDesc})";
        TxtVisFormatInfo.Text = frame.StatusMessage;
    }

    // =========================================================================
    // MODE 1: HIGH-RES LOGARITHMIC SPECTRUM BARS (64 / 128 / 256 BANDS + PEAKS)
    // =========================================================================

    private void RenderSpectrumBars(AudioVisualizerFrame frame, bool showPeakHold)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int padTop = 26;
        int floorY = h - 34; // Leave clean reflection zone at bottom
        int barAreaH = Math.Max(40, floorY - padTop);

        DrawStudioSpectrumGrid(padTop, floorY, frame);

        var spec = frame.Spectrum;
        var peaks = frame.PeakHold;
        int bands = spec.Length;
        if (bands == 0)
            return;

        double stepX = (double)(w - 8) / bands;
        int gap = bands >= 200 ? 1 : (bands >= 100 ? 1 : 2);

        for (int b = 0; b < bands; b++)
        {
            int x0 = 4 + (int)Math.Round(b * stepX);
            int x1 = 4 + (int)Math.Round((b + 1) * stepX) - gap;
            if (x1 <= x0) x1 = x0 + 1;
            if (x1 >= w) x1 = w - 1;

            double val = Math.Clamp(spec[b], 0.0, 1.0);
            int barH = (int)Math.Round(val * barAreaH);
            int topY = floorY - barH;

            // Color progression across frequency (0..1) blended with vertical energy brightness
            double freqPos = (double)b / Math.Max(1, bands - 1);

            if (barH > 0)
            {
                for (int y = topY; y < floorY; y++)
                {
                    double heightFrac = (double)(floorY - y) / barAreaH;
                    uint col = SampleSpectrumBarColor(freqPos, heightFrac);
                    int rowOff = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        _visPixels[rowOff + x] = col;
                    }
                }

                // Subtle glowing floor reflection underneath each bar
                int reflH = Math.Min(h - floorY - 2, Math.Max(2, barH / 5));
                uint baseCol = SampleSpectrumBarColor(freqPos, val * 0.6);
                for (int r = 1; r <= reflH; r++)
                {
                    int ry = floorY + r;
                    if (ry >= h) break;
                    double fade = (1.0 - (double)r / reflH) * 0.28;
                    uint rCol = ScaleColor(baseCol, fade);
                    int rowOff = ry * w;
                    for (int x = x0; x < x1; x++)
                    {
                        _visPixels[rowOff + x] = rCol;
                    }
                }
            }

            // Floating Peak Hold cap
            if (showPeakHold)
            {
                double pk = Math.Clamp(peaks[b], 0.0, 1.0);
                if (pk > 0.015)
                {
                    int py = Math.Clamp(floorY - (int)Math.Round(pk * barAreaH) - 2, padTop, floorY - 2);
                    uint pkCol = BlendToWhite(SampleSpectrumBarColor(freqPos, pk), 0.55);
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int rowOff = (py + dy) * w;
                        for (int x = x0; x < x1; x++)
                        {
                            _visPixels[rowOff + x] = pkCol;
                        }
                    }
                }
            }
        }
    }

    // =========================================================================
    // MODE 2: SYMMETRICAL MIRROR SPECTRUM WAVE
    // =========================================================================

    private void RenderMirrorSpectrum(AudioVisualizerFrame frame, bool showPeakHold)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int midY = h / 2;
        int maxHalfH = Math.Max(30, midY - 26);

        // Subtle center horizon line
        int midOff = midY * w;
        for (int x = 4; x < w - 4; x++)
            _visPixels[midOff + x] = VisGridZeroColor;

        var spec = frame.Spectrum;
        var peaks = frame.PeakHold;
        int bands = spec.Length;
        if (bands == 0)
            return;

        double stepX = (double)(w - 8) / bands;
        int gap = bands >= 200 ? 1 : 2;

        for (int b = 0; b < bands; b++)
        {
            int x0 = 4 + (int)Math.Round(b * stepX);
            int x1 = 4 + (int)Math.Round((b + 1) * stepX) - gap;
            if (x1 <= x0) x1 = x0 + 1;
            if (x1 >= w) x1 = w - 1;

            double val = Math.Clamp(spec[b], 0.0, 1.0);
            int halfH = (int)Math.Round(val * maxHalfH);
            double freqPos = (double)b / Math.Max(1, bands - 1);

            for (int dy = 0; dy < halfH; dy++)
            {
                double frac = (double)dy / maxHalfH;
                uint topCol = SampleSpectrumBarColor(freqPos, Math.Min(1.0, frac * 1.15 + 0.15));
                uint botCol = ScaleColor(topCol, 0.55);

                int yUp = midY - dy;
                int yDn = midY + dy;
                if (yUp >= 24)
                {
                    int offUp = yUp * w;
                    for (int x = x0; x < x1; x++) _visPixels[offUp + x] = topCol;
                }
                if (yDn < h - 4)
                {
                    int offDn = yDn * w;
                    for (int x = x0; x < x1; x++) _visPixels[offDn + x] = botCol;
                }
            }

            if (showPeakHold && peaks[b] > 0.02)
            {
                int pkDy = (int)Math.Round(Math.Clamp(peaks[b], 0.0, 1.0) * maxHalfH);
                uint pkCol = BlendToWhite(SampleSpectrumBarColor(freqPos, peaks[b]), 0.6);
                int yUp = Math.Clamp(midY - pkDy, 24, midY);
                int yDn = Math.Clamp(midY + pkDy, midY, h - 5);
                for (int x = x0; x < x1; x++)
                {
                    _visPixels[yUp * w + x] = pkCol;
                    _visPixels[yDn * w + x] = ScaleColor(pkCol, 0.5);
                }
            }
        }
    }

    // =========================================================================
    // MODE 3: LIVE STEREO OSCILLOSCOPE (PHOSPHOR PERSISTENCE)
    // =========================================================================

    private void RenderStereoOscilloscope(AudioVisualizerFrame frame)
    {
        int w = _visWidth;
        int h = _visHeight;

        // Smooth phosphor fade instead of hard clear
        FadeBuffer(0.70);

        int midY = h / 2;
        int midL = h / 3;
        int midR = (h * 2) / 3;
        double ampScale = (h * 0.24) * Math.Clamp(SldVisSensitivity?.Value ?? 1.0, 0.4, 2.0);

        // Reference baselines
        for (int x = 8; x < w - 8; x += 2)
        {
            _visPixels[midL * w + x] = VisGridColor;
            _visPixels[midY * w + x] = VisGridZeroColor;
            _visPixels[midR * w + x] = VisGridColor;
        }

        var waveL = frame.WaveLeft;
        var waveR = frame.WaveRight;
        int n = Math.Min(waveL.Length, waveR.Length);
        if (n < 2)
            return;

        uint cyanCore = 0xFF22E8FFu;
        uint cyanGlow = 0xFF0F6B88u;
        uint magCore = 0xFFFF2BD6u;
        uint magGlow = 0xFF7D1670u;

        int prevX = 8;
        int prevYL = Math.Clamp(midL - (int)Math.Round(waveL[0] * ampScale), 26, h - 10);
        int prevYR = Math.Clamp(midR - (int)Math.Round(waveR[0] * ampScale), 26, h - 10);

        for (int i = 1; i < n; i++)
        {
            int x = 8 + (int)Math.Round((double)i / (n - 1) * (w - 16));
            int yL = Math.Clamp(midL - (int)Math.Round(waveL[i] * ampScale), 26, h - 10);
            int yR = Math.Clamp(midR - (int)Math.Round(waveR[i] * ampScale), 26, h - 10);

            DrawGlowingLine(prevX, prevYL, x, yL, cyanCore, cyanGlow);
            DrawGlowingLine(prevX, prevYR, x, yR, magCore, magGlow);

            prevX = x;
            prevYL = yL;
            prevYR = yR;
        }
    }

    // =========================================================================
    // MODE 4: REAL-TIME SCROLLING SPECTROGRAM WATERFALL (HEAT MAP)
    // =========================================================================

    private void RenderSpectrogramWaterfall(AudioVisualizerFrame frame)
    {
        int w = _visWidth;
        int h = _visHeight;
        int topStart = 24;
        int scrollStep = 2;

        // Scroll existing spectrogram rows downward cleanly
        if (h > topStart + scrollStep)
        {
            int copyPixels = (h - topStart - scrollStep) * w;
            Array.Copy(_visPixels, topStart * w, _visPixels, (topStart + scrollStep) * w, copyPixels);
        }

        // Clear header strip
        Array.Fill(_visPixels, VisBgColor, 0, topStart * w);

        var spec = frame.Spectrum;
        int bands = spec.Length;
        if (bands == 0)
            return;

        for (int x = 0; x < w; x++)
        {
            double pos = (double)x / Math.Max(1, w - 1) * (bands - 1);
            int b0 = (int)pos;
            int b1 = Math.Min(bands - 1, b0 + 1);
            double frac = pos - b0;
            double v = Math.Clamp(spec[b0] * (1.0 - frac) + spec[b1] * frac, 0.0, 1.0);

            int lutIdx = Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
            uint col = lutIdx < 4 ? VisBgColor : _visPaletteLut[lutIdx];

            for (int r = 0; r < scrollStep; r++)
            {
                _visPixels[(topStart + r) * w + x] = col;
            }
        }
    }

    // =========================================================================
    // MODE 5: PRO MASTERING STEREO VECTORSCOPE (PHASE XY LISSAJOUS SCOPE)
    // =========================================================================

    private void RenderStereoVectorscope(AudioVisualizerFrame frame)
    {
        int w = _visWidth;
        int h = _visHeight;
        FadeBuffer(0.80);

        int cx = w / 2;
        int cy = h / 2 + 6;
        int radius = Math.Max(40, Math.Min(w, h) / 2 - 28);

        // Draw studio graticule (circle + Mono vertical + Side horizontal + L/R 45° axes)
        DrawCircleOutline(cx, cy, radius, VisGridColor);
        DrawCircleOutline(cx, cy, radius / 2, VisGridColor);
        for (int dy = -radius; dy <= radius; dy += 2)
            PlotPixelSafe(cx, cy + dy, VisGridZeroColor);
        for (int dx = -radius; dx <= radius; dx += 2)
            PlotPixelSafe(cx + dx, cy, VisGridColor);

        int diag = (int)(radius * 0.7071);
        DrawLine(cx - diag, cy - diag, cx + diag, cy + diag, VisGridColor);
        DrawLine(cx - diag, cy + diag, cx + diag, cy - diag, VisGridColor);

        var waveL = frame.WaveLeft;
        var waveR = frame.WaveRight;
        int n = Math.Min(waveL.Length, waveR.Length);
        if (n < 2)
            return;

        double gain = Math.Clamp(SldVisSensitivity?.Value ?? 1.0, 0.4, 2.0) * 1.15;
        int prevX = cx;
        int prevY = cy;

        for (int i = 0; i < n; i++)
        {
            // Rotate 45° so Mono (L==R) is vertical axis and Stereo Side (L-R) is horizontal axis!
            double side = (waveR[i] - waveL[i]) * 0.7071 * gain;
            double mid = (waveL[i] + waveR[i]) * 0.7071 * gain;

            int px = Math.Clamp(cx + (int)Math.Round(side * radius), 6, w - 7);
            int py = Math.Clamp(cy - (int)Math.Round(mid * radius), 26, h - 7);

            double energy = Math.Clamp(Math.Sqrt(side * side + mid * mid), 0.0, 1.0);
            int lutIdx = Math.Clamp((int)(60 + energy * 195), 0, 255);
            uint col = _visPaletteLut[lutIdx];

            if (i > 0)
            {
                DrawLine(prevX, prevY, px, py, ScaleColor(col, 0.55));
            }
            PlotPixelSafe(px, py, BlendToWhite(col, 0.35));
            PlotPixelSafe(px + 1, py, col);
            PlotPixelSafe(px, py + 1, col);

            prevX = px;
            prevY = py;
        }
    }

    // =========================================================================
    // MODE 6: RADIAL CYBER HALO (360° BASS-REACTIVE RING SPECTRUM)
    // =========================================================================

    private void RenderRadialHaloSpectrum(AudioVisualizerFrame frame, bool showPeakHold)
    {
        int w = _visWidth;
        int h = _visHeight;
        FadeBuffer(0.65);

        int cx = w / 2;
        int cy = h / 2 + 6;
        int maxR = Math.Max(50, Math.Min(w, h) / 2 - 22);

        var spec = frame.Spectrum;
        var peaks = frame.PeakHold;
        int bands = spec.Length;
        if (bands == 0)
            return;

        // Bass pulse average from lowest 10% of bands
        int bassCount = Math.Max(1, bands / 10);
        double bassAvg = 0;
        for (int i = 0; i < bassCount; i++) bassAvg += spec[i];
        bassAvg /= bassCount;

        double innerR = maxR * (0.28 + bassAvg * 0.14);
        double maxBarLen = maxR - innerR;

        DrawCircleOutline(cx, cy, (int)innerR, 0xFF22E8FFu);
        DrawCircleOutline(cx, cy, (int)(innerR * 0.75), VisGridZeroColor);

        for (int b = 0; b < bands; b++)
        {
            double frac = (double)b / bands;
            double angle = frac * Math.PI * 2.0 - Math.PI / 2.0;
            double cosA = Math.Cos(angle);
            double sinA = Math.Sin(angle);

            double val = Math.Clamp(spec[b], 0.0, 1.0);
            double outerR = innerR + val * maxBarLen;

            int x0 = cx + (int)Math.Round(cosA * innerR);
            int y0 = cy + (int)Math.Round(sinA * innerR);
            int x1 = cx + (int)Math.Round(cosA * outerR);
            int y1 = cy + (int)Math.Round(sinA * outerR);

            uint col = SampleSpectrumBarColor(frac, Math.Min(1.0, val * 1.1 + 0.1));
            DrawLine(x0, y0, x1, y1, col);

            if (showPeakHold && peaks[b] > 0.02)
            {
                double pkR = innerR + Math.Clamp(peaks[b], 0.0, 1.0) * maxBarLen + 3.0;
                int px = cx + (int)Math.Round(cosA * pkR);
                int py = cy + (int)Math.Round(sinA * pkR);
                uint pkCol = BlendToWhite(col, 0.65);
                PlotPixelSafe(px, py, pkCol);
                PlotPixelSafe(px + 1, py, pkCol);
                PlotPixelSafe(px, py + 1, pkCol);
            }
        }
    }

    // =========================================================================
    // FAST PIXEL GRAPHICS & COLOR PALETTE HELPERS
    // =========================================================================

    private void DrawStudioSpectrumGrid(int padTop, int floorY, AudioVisualizerFrame frame)
    {
        int w = _visWidth;
        int areaH = floorY - padTop;

        // Horizontal dB reference lines (25%, 50%, 75%, 100%)
        for (int g = 0; g <= 4; g++)
        {
            int y = padTop + (areaH * g) / 4;
            uint c = g == 4 ? VisGridZeroColor : VisGridColor;
            int off = y * w;
            for (int x = 4; x < w - 4; x++)
            {
                _visPixels[off + x] = c;
            }
        }

        // Vertical frequency tick guide lines with clean uniform octave/linear spacing
        double[] ticks = GetVisAxisTicks(frame.MinFreqHz, frame.MaxFreqHz, frame.LinearScale);
        foreach (double hz in ticks)
        {
            double norm = FrequencyToNormX(hz, frame.MinFreqHz, frame.MaxFreqHz, frame.LinearScale);
            int x = 4 + (int)Math.Round(norm * (w - 8));
            if (x <= 8 || x >= w - 8) continue;
            for (int y = padTop; y < floorY; y += 2)
            {
                _visPixels[y * w + x] = VisGridColor;
            }
        }
    }

    private void RenderVisualizerFreqAxis()
    {
        if (CanvasVisFreqAxis == null)
            return;

        double w = CanvasVisFreqAxis.ActualWidth;
        if (w < 100)
            return;

        CanvasVisFreqAxis.Children.Clear();
        var (minHz, maxHz, linear) = GetSelectedVisRange();
        double[] ticks = GetVisAxisTicks(minHz, maxHz, linear);
        double nyquist = _studio.Visualizer.SampleRate * 0.5;

        for (int i = 0; i < ticks.Length; i++)
        {
            double hz = ticks[i];
            double norm = FrequencyToNormX(hz, minHz, maxHz, linear);
            double x = 4.0 + norm * (w - 8.0);

            // Smooth Cyan -> Electric Violet -> Magenta color progression across the axis
            (byte r, byte g, byte b) = norm < 0.5
                ? LerpRgb(34, 232, 255, 136, 152, 255, norm / 0.5)
                : LerpRgb(136, 152, 255, 255, 43, 214, (norm - 0.5) / 0.5);

            // Slightly mute labels above the active stream's Nyquist ceiling so users see 24 kHz vs 48 kHz clearly
            bool aboveNyquist = hz > nyquist + 100.0;
            var brush = aboveNyquist
                ? new SolidColorBrush(Color.FromRgb(120, 74, 130))
                : new SolidColorBrush(Color.FromRgb(r, g, b));

            var lbl = new TextBlock
            {
                Text = FormatAxisHz(hz),
                Foreground = brush,
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = aboveNyquist ? FontWeights.Normal : FontWeights.SemiBold
            };

            lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textW = lbl.DesiredSize.Width;
            double left = i == 0
                ? 4.0
                : Math.Clamp(x - textW / 2.0, 4.0, Math.Max(4.0, w - textW - 4.0));

            Canvas.SetLeft(lbl, left);
            Canvas.SetTop(lbl, 2.0);
            CanvasVisFreqAxis.Children.Add(lbl);
        }
    }

    private static double FrequencyToNormX(double hz, double minHz, double maxHz, bool linear)
    {
        if (linear)
        {
            return Math.Clamp((hz - minHz) / Math.Max(1.0, maxHz - minHz), 0.0, 1.0);
        }

        double lo = Math.Max(1.0, minHz);
        double hi = Math.Max(lo * 2.0, maxHz);
        double f = Math.Clamp(hz, lo, hi);
        return Math.Clamp(Math.Log(f / lo) / Math.Log(hi / lo), 0.0, 1.0);
    }

    private static double[] GetVisAxisTicks(double minHz, double maxHz, bool linear)
    {
        if (linear)
        {
            // Exact uniform 6 kHz grid columns from 0 Hz to 48 kHz
            return new double[] { 0, 6000, 12000, 18000, 24000, 30000, 36000, 42000, 48000 };
        }

        if (minHz <= 6.0 && maxHz >= 40000.0)
        {
            // Clean uniform 1-octave columns across the full 5 Hz -> 48 kHz range
            return new double[] { 5, 10, 20, 40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 20000, 40000 };
        }

        if (minHz <= 6.0)
        {
            // Clean uniform 1-octave columns across 5 Hz -> 24 kHz
            return new double[] { 5, 10, 20, 40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 20000 };
        }

        // Exact 10-band Realtek EQ octaves (20 Hz -> 20 kHz)
        return new double[] { 20, 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    }

    private static string FormatAxisHz(double hz)
    {
        if (hz >= 1000.0)
        {
            double khz = hz / 1000.0;
            return Math.Abs(khz - Math.Round(khz)) < 0.05
                ? $"{Math.Round(khz):0} kHz"
                : $"{khz:0.#} kHz";
        }
        return $"{Math.Round(hz):0} Hz";
    }

    private (double minHz, double maxHz, bool linear) GetSelectedVisRange()
    {
        string tag = GetSelectedComboTag(CmbVisRange, "5:48000:Log");
        string[] parts = tag.Split(':');
        if (parts.Length == 3
            && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double minHz)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double maxHz))
        {
            bool linear = string.Equals(parts[2], "Lin", StringComparison.OrdinalIgnoreCase);
            return (minHz, maxHz, linear);
        }
        return (5.0, 48000.0, false);
    }

    private void BuildVisualizerPaletteLut(string paletteName)
    {
        _visCurrentPalette = paletteName;
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            (byte r, byte g, byte b) = paletteName switch
            {
                "Studio" => t < 0.55
                    ? LerpRgb(16, 180, 120, 34, 232, 255, t / 0.55)
                    : t < 0.82
                        ? LerpRgb(34, 232, 255, 255, 184, 32, (t - 0.55) / 0.27)
                        : LerpRgb(255, 184, 32, 255, 68, 102, (t - 0.82) / 0.18),
                "Ice" => t < 0.5
                    ? LerpRgb(20, 56, 148, 34, 232, 255, t / 0.5)
                    : LerpRgb(34, 232, 255, 236, 252, 255, (t - 0.5) / 0.5),
                "Fire" => t < 0.4
                    ? LerpRgb(62, 16, 118, 255, 43, 180, t / 0.4)
                    : t < 0.78
                        ? LerpRgb(255, 43, 180, 255, 138, 28, (t - 0.4) / 0.38)
                        : LerpRgb(255, 138, 28, 255, 245, 190, (t - 0.78) / 0.22),
                _ => t < 0.50 // Spectra Neon (Cyan -> Electric Violet -> Hot Magenta)
                    ? LerpRgb(34, 232, 255, 126, 118, 255, t / 0.50)
                    : LerpRgb(126, 118, 255, 255, 43, 214, (t - 0.50) / 0.50)
            };
            _visPaletteLut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    private uint SampleSpectrumBarColor(double freqPos, double heightFrac)
    {
        // For Studio RTA palette, color is primarily energy height (Green -> Amber -> Red);
        // For Spectra Neon / Ice / Fire, sweep smoothly across frequency AND brighten toward the tip!
        if (string.Equals(_visCurrentPalette, "Studio", StringComparison.OrdinalIgnoreCase))
        {
            int idx = Math.Clamp((int)Math.Round(heightFrac * 255.0), 0, 255);
            return _visPaletteLut[idx];
        }

        double combined = Math.Clamp(freqPos * 0.78 + heightFrac * 0.22, 0.0, 1.0);
        uint baseCol = _visPaletteLut[Math.Clamp((int)Math.Round(combined * 255.0), 0, 255)];
        return ScaleColor(baseCol, 0.48 + heightFrac * 0.52);
    }

    private static (byte r, byte g, byte b) LerpRgb(int r0, int g0, int b0, int r1, int g1, int b1, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return (
            (byte)Math.Clamp(r0 + (r1 - r0) * t, 0, 255),
            (byte)Math.Clamp(g0 + (g1 - g0) * t, 0, 255),
            (byte)Math.Clamp(b0 + (b1 - b0) * t, 0, 255));
    }

    private static uint ScaleColor(uint argb, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        uint r = (uint)(((argb >> 16) & 0xFF) * factor);
        uint g = (uint)(((argb >> 8) & 0xFF) * factor);
        uint b = (uint)((argb & 0xFF) * factor);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    private static uint BlendToWhite(uint argb, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        uint r = (uint)(((argb >> 16) & 0xFF) * (1.0 - amount) + 255.0 * amount);
        uint g = (uint)(((argb >> 8) & 0xFF) * (1.0 - amount) + 255.0 * amount);
        uint b = (uint)((argb & 0xFF) * (1.0 - amount) + 255.0 * amount);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    private void FadeBuffer(double keepFactor)
    {
        const uint bgR = 7, bgG = 10, bgB = 20;
        double inv = 1.0 - keepFactor;
        for (int i = 0; i < _visPixels.Length; i++)
        {
            uint p = _visPixels[i];
            if (p == VisBgColor) continue;
            uint r = (uint)(((p >> 16) & 0xFF) * keepFactor + bgR * inv);
            uint g = (uint)(((p >> 8) & 0xFF) * keepFactor + bgG * inv);
            uint b = (uint)((p & 0xFF) * keepFactor + bgB * inv);
            _visPixels[i] = (r <= bgR + 1 && g <= bgG + 1 && b <= bgB + 1)
                ? VisBgColor
                : (0xFF000000u | (r << 16) | (g << 8) | b);
        }
    }

    private void PlotPixelSafe(int x, int y, uint col)
    {
        if ((uint)x < (uint)_visWidth && (uint)y < (uint)_visHeight)
        {
            _visPixels[y * _visWidth + x] = col;
        }
    }

    private void DrawLine(int x0, int y0, int x1, int y1, uint col)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            PlotPixelSafe(x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void DrawGlowingLine(int x0, int y0, int x1, int y1, uint coreCol, uint glowCol)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            PlotPixelSafe(x0, y0 - 1, glowCol);
            PlotPixelSafe(x0, y0 + 1, glowCol);
            PlotPixelSafe(x0, y0, coreCol);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void DrawCircleOutline(int cx, int cy, int r, uint col)
    {
        int x = r, y = 0, err = 1 - r;
        while (x >= y)
        {
            PlotPixelSafe(cx + x, cy + y, col);
            PlotPixelSafe(cx + y, cy + x, col);
            PlotPixelSafe(cx - y, cy + x, col);
            PlotPixelSafe(cx - x, cy + y, col);
            PlotPixelSafe(cx - x, cy - y, col);
            PlotPixelSafe(cx - y, cy - x, col);
            PlotPixelSafe(cx + y, cy - x, col);
            PlotPixelSafe(cx + x, cy - y, col);
            y++;
            if (err < 0)
            {
                err += 2 * y + 1;
            }
            else
            {
                x--;
                err += 2 * (y - x + 1);
            }
        }
    }

    private int GetSelectedVisBands()
    {
        string tag = GetSelectedComboTag(CmbVisBands, "256");
        return int.TryParse(tag, out int b) ? Math.Clamp(b, 32, 256) : 256;
    }

    private static string GetSelectedComboTag(ComboBox? cmb, string fallback)
    {
        if (cmb?.SelectedItem is ComboBoxItem cbi && cbi.Tag is string s && !string.IsNullOrWhiteSpace(s))
            return s;
        return fallback;
    }
}
