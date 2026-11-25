using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _visSweepActive;
    private bool _visFocusMode;
    private bool _visPopoutMinimalMode;
    private VisualizerPopoutWindow? _visPopoutWindow;
    private bool _visDockingInProgress;
    private DateTime _visLastViewportClickUtc = DateTime.MinValue;
    private int _visLastAxisSampleRate;
    private bool _visIdleHintConsumedThisSession;
    private DateTime _visIdleHintExpiresUtc = DateTime.MinValue;

    public bool IsVisualizerPoppedOut => _visPopoutWindow != null;

    private bool IsVisualizerActiveAndVisible()
    {
        if (_visPaused)
            return false;

        if (_visPopoutWindow != null)
            return _visPopoutWindow.IsVisible && _visPopoutWindow.WindowState != WindowState.Minimized;

        return _visIsActiveTab && IsVisible && WindowState != WindowState.Minimized;
    }

    // 256-entry precomputed color lookup table (BGRA32) + cyclic LUT for seamless sideways Color Flow
    private readonly uint[] _visPaletteLut = new uint[256];
    private readonly uint[] _visCyclicLut = new uint[256];
    private string _visCurrentPalette = "Spectra";
    private double _visColorPhase;
    private double _visPrevBassEnergy;

    // User-customizable 3-stop gradient colors (persisted in settings.json)
    private (byte r, byte g, byte b) _visCustomC1 = (34, 232, 255);
    private (byte r, byte g, byte b) _visCustomC2 = (140, 82, 255);
    private (byte r, byte g, byte b) _visCustomC3 = (255, 43, 214);

    // Lightweight zero-allocation particle & 3D starfield pools for Aurora & Bass Tunnel
    private struct VisSpark
    {
        public double X, Y, Vx, Vy, Life;
        public uint Color;
    }

    private struct VisStar
    {
        public double X, Y, Z;
        public double HueOffset;
    }

    private struct VisShockRing
    {
        public double Radius, Speed, Alpha;
        public uint Color;
    }

    private readonly VisSpark[] _visSparks = new VisSpark[110];
    private int _visSparkCursor;
    private readonly VisStar[] _visStars = new VisStar[160];
    private readonly VisShockRing[] _visRings = new VisShockRing[8];
    private int _visRingCursor;
    private readonly Random _visRng = new(42);

    private const uint VisBgColor = 0xFF070A14u;
    private const uint VisGridColor = 0xFF131B33u;
    private const uint VisGridZeroColor = 0xFF23325Cu;

    private void InitializeVisualizer()
    {
        _visCustomC1 = ParseHexColor(_appSettings.VisCustomColor1Hex, (34, 232, 255));
        _visCustomC2 = ParseHexColor(_appSettings.VisCustomColor2Hex, (140, 82, 255));
        _visCustomC3 = ParseHexColor(_appSettings.VisCustomColor3Hex, (255, 43, 214));
        UpdateCustomColorSwatchButtons();

        for (int i = 0; i < _visStars.Length; i++)
        {
            _visStars[i] = new VisStar
            {
                X = (_visRng.NextDouble() * 2.0 - 1.0),
                Y = (_visRng.NextDouble() * 2.0 - 1.0),
                Z = _visRng.NextDouble() * 0.95 + 0.05,
                HueOffset = _visRng.NextDouble()
            };
        }

        BuildVisualizerPaletteLut("Spectra");
        _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
        };
        _visualizerTimer.Tick += VisualizerTimer_Tick;
    }

    private void StartVisualizer()
    {
        if (_visualizerTimer == null)
            return;

        if (_visPopoutWindow == null && (WindowState == WindowState.Minimized || !IsVisible))
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
        if (!_visIdleHintConsumedThisSession && _appSettings.ShowIdleHints)
        {
            _visIdleHintConsumedThisSession = true;
            _visIdleHintExpiresUtc = DateTime.UtcNow.AddSeconds(4.0);
        }
        else if (TxtVisIdleHint != null)
        {
            TxtVisIdleHint.Visibility = Visibility.Collapsed;
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
        if (IsVisualizerActiveAndVisible())
        {
            _studio.Visualizer.Start(_activePlaybackEndpoint?.EndpointId);
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != MainTabControl)
            return;

        bool nowOnVis = MainTabControl.SelectedItem == TabVisualizer;
        if (nowOnVis != _visIsActiveTab)
        {
            _visIsActiveTab = nowOnVis;
            if (nowOnVis)
            {
                if (_visPopoutWindow == null)
                {
                    _visPaused = false;
                    if (BtnVisPause != null)
                        BtnVisPause.Content = "Pause";
                    StartVisualizer();
                }
            }
            else
            {
                if (_visFocusMode)
                    SetVisualizerFocusMode(false);

                // Keep the 60 FPS Visualizer running continuously while popped out (e.g. while mixing on Tab 9 DJ)!
                if (_visPopoutWindow == null)
                {
                    _visPaused = false;
                    if (BtnVisPause != null)
                        BtnVisPause.Content = "Pause";
                    StopVisualizer();
                }
            }
        }

        bool nowOnRec = MainTabControl.SelectedItem == TabRecord;
        OnRecorderTabSelectionChanged(nowOnRec);

        bool nowOnDj = MainTabControl.SelectedItem == TabDj;
        OnDjTabSelectionChanged(nowOnDj);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_djTwoPointDeck.HasValue || _djUnlockedBeatDeck.HasValue)
            {
                LockAndCloseDjCalibration();
                e.Handled = true;
                return;
            }

            if (_visFocusMode)
            {
                SetVisualizerFocusMode(false);
                e.Handled = true;
            }
        }
    }

    private void BtnVisFocus_Click(object sender, RoutedEventArgs e)
    {
        if (_visPopoutWindow != null)
        {
            SetPopoutMinimalMode(!_visPopoutMinimalMode);
        }
        else
        {
            SetVisualizerFocusMode(!_visFocusMode);
        }
    }

    private void VisualizerViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (e.ClickCount >= 2 || (now - _visLastViewportClickUtc).TotalMilliseconds < 320)
        {
            _visLastViewportClickUtc = DateTime.MinValue;
            if (_visPopoutWindow != null)
                SetPopoutMinimalMode(!_visPopoutMinimalMode);
            else
                SetVisualizerFocusMode(!_visFocusMode);
            e.Handled = true;
            return;
        }
        _visLastViewportClickUtc = now;
    }

    private void SetPopoutMinimalMode(bool minimal)
    {
        _visPopoutMinimalMode = minimal;
        if (BorderVisToolbar != null)
            BorderVisToolbar.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        if (BorderVisTelemetry != null)
            BorderVisTelemetry.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        if (GridVisualizerContent != null)
            GridVisualizerContent.Margin = minimal ? new Thickness(6, 6, 6, 4) : new Thickness(12, 10, 12, 10);
        if (BtnVisFocus != null)
            BtnVisFocus.Content = minimal ? "✕ Toolbar" : "⛶ Focus";
    }

    private void BtnVisPopout_Click(object sender, RoutedEventArgs e)
    {
        if (_visPopoutWindow == null)
        {
            OpenVisualizerPopoutWindow();
        }
        else
        {
            DockVisualizerBackToTab();
        }
    }

    private void BtnVisBringPopoutFront_Click(object sender, RoutedEventArgs e)
    {
        if (_visPopoutWindow != null)
        {
            if (_visPopoutWindow.WindowState == WindowState.Minimized)
                _visPopoutWindow.WindowState = WindowState.Normal;
            _visPopoutWindow.Activate();
        }
    }

    private void BtnVisPinTop_Click(object sender, RoutedEventArgs e)
    {
        if (_visPopoutWindow == null || BtnVisPinTop == null)
            return;

        _visPopoutWindow.Topmost = !_visPopoutWindow.Topmost;
        BtnVisPinTop.Content = _visPopoutWindow.Topmost ? "📌 Pinned" : "📌 Pin";
        BtnVisPinTop.BorderBrush = _visPopoutWindow.Topmost ? (Brush)FindResource("Green") : (Brush)FindResource("Line");
        BtnVisPinTop.Foreground = _visPopoutWindow.Topmost ? (Brush)FindResource("Green") : (Brush)FindResource("Cyan");
    }

    private void OpenVisualizerPopoutWindow()
    {
        if (_visPopoutWindow != null || TabVisualizerHost == null || GridVisualizerContent == null)
            return;

        if (_visFocusMode)
            SetVisualizerFocusMode(false);

        TabVisualizerHost.Children.Remove(GridVisualizerContent);
        GridVisualizerContent.Margin = new Thickness(12, 10, 12, 10);
        BorderVisPopoutPlaceholder.Visibility = Visibility.Visible;

        var popout = new VisualizerPopoutWindow
        {
            Owner = this
        };
        popout.AttachVisualizerView(GridVisualizerContent);
        popout.DockRequested += () =>
        {
            if (!_visDockingInProgress)
                DockVisualizerBackToTab(fromWindowClosing: true);
        };
        popout.EscapePressed += () =>
        {
            if (_visPopoutMinimalMode)
                SetPopoutMinimalMode(false);
        };
        popout.WindowStateUpdated += () =>
        {
            if (popout.WindowState == WindowState.Minimized)
            {
                if (!_visIsActiveTab)
                    StopVisualizer();
            }
            else if (!_visPaused)
            {
                StartVisualizer();
            }
        };

        _visPopoutWindow = popout;
        BtnVisPopout.Content = "↙ Dock";
        BtnVisPinTop.Visibility = Visibility.Visible;
        BtnVisPinTop.Content = "📌 Pin";
        if (BtnDjPopoutVis != null)
        {
            BtnDjPopoutVis.Content = "📊 Dock Vis";
            BtnDjPopoutVis.BorderBrush = (Brush)FindResource("Green");
            BtnDjPopoutVis.Foreground = (Brush)FindResource("Green");
        }

        _visPaused = false;
        if (BtnVisPause != null)
            BtnVisPause.Content = "Pause";

        popout.Show();
        StartVisualizer();
        SetStatus("Popped out Live 60 FPS Visualizer — resize & place anywhere on screen while mixing on Tab 9 DJ! (Double-click spectrum for borderless Focus view)");
    }

    private void DockVisualizerBackToTab(bool fromWindowClosing = false)
    {
        if (_visPopoutWindow == null || _visDockingInProgress)
            return;

        _visDockingInProgress = true;
        try
        {
            var popout = _visPopoutWindow;
            _visPopoutWindow = null;

            if (_visPopoutMinimalMode)
                SetPopoutMinimalMode(false);

            popout.DetachVisualizerView();
            if (!fromWindowClosing)
            {
                popout.Close();
            }

            if (TabVisualizerHost != null && GridVisualizerContent != null && !TabVisualizerHost.Children.Contains(GridVisualizerContent))
            {
                GridVisualizerContent.Margin = new Thickness(14, 12, 14, 12);
                TabVisualizerHost.Children.Add(GridVisualizerContent);
            }

            if (BorderVisPopoutPlaceholder != null)
                BorderVisPopoutPlaceholder.Visibility = Visibility.Collapsed;

            if (BtnVisPopout != null)
                BtnVisPopout.Content = "⧉ Popout";
            if (BtnVisPinTop != null)
                BtnVisPinTop.Visibility = Visibility.Collapsed;
            if (BtnDjPopoutVis != null)
            {
                BtnDjPopoutVis.Content = "📊 Visualizer";
                BtnDjPopoutVis.BorderBrush = (Brush)FindResource("Line");
                BtnDjPopoutVis.Foreground = (Brush)FindResource("Cyan");
            }

            if (_visIsActiveTab && IsVisible && WindowState != WindowState.Minimized && !_visPaused)
            {
                EnsureVisualizerBitmap();
                RenderVisualizerFreqAxis();
                StartVisualizer();
            }
            else
            {
                StopVisualizer();
            }

            SetStatus("Docked Live 60 FPS Visualizer back into Tab 7.");
        }
        finally
        {
            _visDockingInProgress = false;
        }
    }

    private void CloseVisualizerPopoutOnAppExit()
    {
        if (_visPopoutWindow != null)
        {
            _visDockingInProgress = true;
            try
            {
                _visPopoutWindow.DetachVisualizerView();
                _visPopoutWindow.Close();
            }
            catch { }
            _visPopoutWindow = null;
            _visDockingInProgress = false;
        }
    }

    private void SetVisualizerFocusMode(bool enable)
    {
        _visFocusMode = enable;
        Visibility barVis = enable ? Visibility.Collapsed : Visibility.Visible;
        if (BorderAppHeader != null)
            BorderAppHeader.Visibility = barVis;
        if (BorderEndpointBar != null)
            BorderEndpointBar.Visibility = barVis;
        if (BtnVisFocus != null)
            BtnVisFocus.Content = enable ? "✕ Exit Focus" : "⛶ Focus";
        if (BtnDjFocus != null)
            BtnDjFocus.Content = enable ? "✕ Exit Focus" : "⛶ Focus";
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
        if (PanelVisCustomColors != null)
        {
            PanelVisCustomColors.Visibility = string.Equals(palette, "Custom", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!string.Equals(palette, _visCurrentPalette, StringComparison.OrdinalIgnoreCase))
        {
            BuildVisualizerPaletteLut(palette);
        }

        // Clear buffer cleanly on mode/range change so Waterfall / Oscilloscope / Vectorscope / Tunnel start fresh
        if (_visPixels.Length > 0)
        {
            Array.Fill(_visPixels, VisBgColor);
            FlushVisualizerBitmap();
        }

        UpdateVisualizerHeaderLabels();
        RenderVisualizerFreqAxis();
    }

    private void BtnVisCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string slot)
            return;

        var current = slot switch
        {
            "2" => _visCustomC2,
            "3" => _visCustomC3,
            _ => _visCustomC1
        };

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.r, current.g, current.b)
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var picked = (dlg.Color.R, dlg.Color.G, dlg.Color.B);
        string hex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";

        if (slot == "2")
        {
            _visCustomC2 = picked;
            _appSettings.VisCustomColor2Hex = hex;
        }
        else if (slot == "3")
        {
            _visCustomC3 = picked;
            _appSettings.VisCustomColor3Hex = hex;
        }
        else
        {
            _visCustomC1 = picked;
            _appSettings.VisCustomColor1Hex = hex;
        }

        UpdateCustomColorSwatchButtons();
        _studio.Presets.SaveAppSettings(_appSettings);
        BuildVisualizerPaletteLut("Custom");
        SetStatus($"Updated custom visualizer color #{slot} to {hex}.");
    }

    private void UpdateCustomColorSwatchButtons()
    {
        if (BtnVisCol1 != null)
            BtnVisCol1.Background = new SolidColorBrush(Color.FromRgb(_visCustomC1.r, _visCustomC1.g, _visCustomC1.b));
        if (BtnVisCol2 != null)
            BtnVisCol2.Background = new SolidColorBrush(Color.FromRgb(_visCustomC2.r, _visCustomC2.g, _visCustomC2.b));
        if (BtnVisCol3 != null)
            BtnVisCol3.Background = new SolidColorBrush(Color.FromRgb(_visCustomC3.r, _visCustomC3.g, _visCustomC3.b));
    }

    private void BtnVisPause_Click(object sender, RoutedEventArgs e)
    {
        _visPaused = !_visPaused;
        BtnVisPause.Content = _visPaused ? "Resume" : "Pause";
        if (_visPaused)
        {
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

    private async void BtnVisSweep_Click(object sender, RoutedEventArgs e)
    {
        if (_visSweepActive)
        {
            _visSweepActive = false;
            _studio.TestTones.Stop();
            if (BtnVisSweep != null)
                BtnVisSweep.Content = "▶ Sweep";
            SetStatus("Stopped frequency sweep sound test.");
            return;
        }

        var (minHz, maxHz, _) = GetSelectedVisRange();
        double startHz = Math.Max(5.0, minHz);
        int activeSr = Math.Max(48000, _studio.Visualizer.SampleRate);
        double endHz = maxHz >= 40000.0 ? 48000.0 : Math.Min(maxHz, 24000.0);
        int genSr = endHz > 22000.0 ? Math.Max(96000, activeSr) : activeSr;
        double durationSec = endHz >= 40000.0 ? 12.0 : 10.0;

        _visSweepActive = true;
        if (BtnVisSweep != null)
            BtnVisSweep.Content = "■ Stop";

        SetStatus($"Playing {FormatAxisHz(startHz)} → {FormatAxisHz(endHz)} logarithmic frequency sweep ({durationSec:0}s) — watch every band light up live!");
        await _studio.TestTones.PlayFrequencySweepAsync(startHz, endHz, durationSec, genSr);

        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(durationSec + 0.25));
        if (_visSweepActive)
        {
            _visSweepActive = false;
            if (BtnVisSweep != null)
                BtnVisSweep.Content = "▶ Sweep";
        }
    }

    private void UpdateVisualizerHeaderLabels()
    {
        if (TxtVisHeaderTag == null || CmbVisMode == null || CmbVisBands == null)
            return;

        string mode = GetSelectedComboTag(CmbVisMode, "Bars");
        int bands = GetSelectedVisBands();
        var (minHz, maxHz, scaleMode) = GetSelectedVisRange();
        string rangeLabel = $"{FormatAxisHz(minHz)} — {FormatAxisHz(maxHz)}";
        string scaleWord = scaleMode == SpectrumScaleMode.Linear ? "LINEAR" : "LOG";
        int sr = _studio.Visualizer.SampleRate;
        double nyquist = sr * 0.5;
        string nyqNote = maxHz > nyquist + 500.0
            ? $" • NYQUIST {FormatAxisHz(nyquist)} (@ {sr / 1000.0:0.#} kHz)"
            : string.Empty;

        TxtVisHeaderTag.Text = mode switch
        {
            "WaveBars" => $"{bands}-BAND COLOR WAVEFORM + SPECTRUM BARS ({rangeLabel} • {scaleWord}{nyqNote})",
            "Aurora" => $"{bands}-BAND NEON LIQUID AURORA + SPARK PARTICLES ({rangeLabel} • {scaleWord}{nyqNote})",
            "PeaksOnly" => $"{bands}-BAND PEAK CONTOUR (PEAKS ONLY • {rangeLabel} • {scaleWord}{nyqNote})",
            "Tunnel" => "3D STARFIELD BASS TUNNEL + BEAT SHOCKWAVES",
            "Mirror" => $"{bands}-BAND SYMMETRICAL MIRROR FFT SPECTRUM ({rangeLabel} • {scaleWord}{nyqNote})",
            "Oscilloscope" => "LIVE STEREO WAVEFORM OSCILLOSCOPE (PALETTE + PHOSPHOR PERSISTENCE)",
            "Waterfall" => $"{bands}-BAND REAL-TIME SPECTROGRAM WATERFALL ({rangeLabel} • {scaleWord}{nyqNote})",
            "Vectorscope" => "STEREO PHASE VECTORSCOPE (LISSAJOUS XY • MONO VERTICAL / WIDE HORIZONTAL)",
            "Radial" => $"{bands}-BAND 360° RADIAL CYBER HALO SPECTRUM ({rangeLabel})",
            _ => $"{bands}-BAND {scaleWord} FFT SPECTRUM ANALYZER ({rangeLabel}{nyqNote})"
        };

        bool showFreqAxis = mode is "Bars" or "WaveBars" or "Aurora" or "PeaksOnly" or "Mirror" or "Waterfall";
        if (CanvasVisFreqAxis != null)
        {
            CanvasVisFreqAxis.Visibility = showFreqAxis ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsureVisualizerBitmap()
    {
        if (VisualizerViewport == null || ImgVisualizer == null)
            return;

        int w = Math.Clamp((int)Math.Round(VisualizerViewport.ActualWidth), 240, 3840);
        int h = Math.Clamp((int)Math.Round(VisualizerViewport.ActualHeight), 120, 2160);
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
        if (!IsVisualizerActiveAndVisible())
            return;

        if (_visBitmap == null || _visWidth <= 0 || _visHeight <= 0)
        {
            EnsureVisualizerBitmap();
            if (_visBitmap == null)
                return;
        }

        int bandCount = GetSelectedVisBands();
        var (minHz, maxHz, scaleMode) = GetSelectedVisRange();
        double sensitivity = SldVisSensitivity?.Value ?? 1.0;
        double smoothing = SldVisSmoothing?.Value ?? 0.72;
        bool peakHold = ChkVisPeakHold?.IsChecked == true;
        bool colorFlow = ChkVisColorFlow?.IsChecked == true;
        string mode = GetSelectedComboTag(CmbVisMode, "Bars");

        var frame = _studio.Visualizer.ComputeFrame(
            bandCount,
            sensitivity,
            smoothing,
            minHz,
            maxHz,
            linearScale: scaleMode == SpectrumScaleMode.Linear,
            explicitScaleMode: scaleMode);

        // Compute low-end bass energy for beat-reactive Color Flow & 3D Bass Tunnel shockwaves
        double bassEnergy = ComputeBassEnergy(frame.Spectrum);
        if (colorFlow)
        {
            // Smooth sideways color scrolling that subtly surges forward on kick drums!
            _visColorPhase = (_visColorPhase + 0.0024 + bassEnergy * 0.0075) % 1.0;
        }

        switch (mode)
        {
            case "WaveBars":
                RenderColorWaveformBars(frame, peakHold, colorFlow);
                break;
            case "Aurora":
                RenderLiquidAurora(frame, peakHold, colorFlow, bassEnergy);
                break;
            case "PeaksOnly":
                RenderPeaksOnlySpectrum(frame, colorFlow);
                break;
            case "Tunnel":
                RenderStarfieldBassTunnel(frame, colorFlow, bassEnergy);
                break;
            case "Mirror":
                RenderMirrorSpectrum(frame, peakHold, colorFlow);
                break;
            case "Oscilloscope":
                RenderStereoOscilloscope(frame, colorFlow);
                break;
            case "Waterfall":
                RenderSpectrogramWaterfall(frame, colorFlow);
                break;
            case "Vectorscope":
                RenderStereoVectorscope(frame, colorFlow);
                break;
            case "Radial":
                RenderRadialHaloSpectrum(frame, peakHold, colorFlow);
                break;
            default:
                RenderSpectrumBars(frame, peakHold, colorFlow);
                break;
        }

        _visPrevBassEnergy = bassEnergy;
        FlushVisualizerBitmap();
        if (frame.SampleRate != _visLastAxisSampleRate)
        {
            _visLastAxisSampleRate = frame.SampleRate;
            UpdateVisualizerHeaderLabels();
            RenderVisualizerFreqAxis();
        }
        UpdateVisualizerTelemetry(frame);
    }

    private static double ComputeBassEnergy(double[] spec)
    {
        if (spec.Length == 0) return 0.0;
        int n = Math.Max(2, spec.Length / 9);
        double sum = 0.0;
        for (int i = 0; i < n; i++) sum += spec[i];
        return Math.Clamp(sum / n, 0.0, 1.0);
    }

    private void UpdateVisualizerTelemetry(AudioVisualizerFrame frame)
    {
        if (frame.HasSignal || !_appSettings.ShowIdleHints || DateTime.UtcNow >= _visIdleHintExpiresUtc)
        {
            _visIdleHintExpiresUtc = DateTime.MinValue;
            TxtVisIdleHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtVisIdleHint.Visibility = Visibility.Visible;
        }

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

    private void RenderSpectrumBars(AudioVisualizerFrame frame, bool showPeakHold, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int padTop = 26;
        int floorY = h - 34;
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
            double freqPos = (double)b / Math.Max(1, bands - 1);

            if (barH > 0)
            {
                for (int y = topY; y < floorY; y++)
                {
                    double heightFrac = (double)(floorY - y) / barAreaH;
                    uint col = SampleSpectrumBarColor(freqPos, heightFrac, colorFlow);
                    int rowOff = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        _visPixels[rowOff + x] = col;
                    }
                }

                // Subtle glowing floor reflection underneath each bar
                int reflH = Math.Min(h - floorY - 2, Math.Max(2, barH / 5));
                uint baseCol = SampleSpectrumBarColor(freqPos, val * 0.6, colorFlow);
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

            if (showPeakHold)
            {
                double pk = Math.Clamp(peaks[b], 0.0, 1.0);
                if (pk > 0.015)
                {
                    int py = Math.Clamp(floorY - (int)Math.Round(pk * barAreaH) - 2, padTop, floorY - 2);
                    uint pkCol = BlendToWhite(SampleSpectrumBarColor(freqPos, pk, colorFlow), 0.55);
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
    // MODE 2: COLOR WAVEFORM BARS (SPECTRUM BARS FUSED INSIDE A LIVE COLOR WAVEFORM!)
    // =========================================================================

    private void RenderColorWaveformBars(AudioVisualizerFrame frame, bool showPeakHold, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int padTop = 26;
        int floorY = h - 30;
        int barAreaH = Math.Max(40, floorY - padTop);
        int midY = padTop + barAreaH / 2;

        DrawStudioSpectrumGrid(padTop, floorY, frame);

        // 1. Vibrant translucent-feel full-color spectrum bars rising smoothly from the base
        var spec = frame.Spectrum;
        var peaks = frame.PeakHold;
        int bands = spec.Length;
        if (bands > 0)
        {
            double stepX = (double)(w - 8) / bands;
            int gap = bands >= 200 ? 1 : 2;

            for (int b = 0; b < bands; b++)
            {
                int x0 = 4 + (int)Math.Round(b * stepX);
                int x1 = 4 + (int)Math.Round((b + 1) * stepX) - gap;
                if (x1 <= x0) x1 = x0 + 1;
                if (x1 >= w) x1 = w - 1;

                double val = Math.Clamp(spec[b], 0.0, 1.0);
                int barH = (int)Math.Round(val * barAreaH * 0.92);
                int topY = floorY - barH;
                double freqPos = (double)b / Math.Max(1, bands - 1);

                if (barH > 0)
                {
                    for (int y = topY; y < floorY; y++)
                    {
                        double heightFrac = (double)(floorY - y) / barAreaH;
                        // Slightly deeper jewel-toned bars so the glowing color waveform pops vibrantly right over them!
                        uint col = ScaleColor(SampleSpectrumBarColor(freqPos, heightFrac, colorFlow), 0.72);
                        int rowOff = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            _visPixels[rowOff + x] = col;
                        }
                    }
                }

                if (showPeakHold && peaks[b] > 0.02)
                {
                    int py = Math.Clamp(floorY - (int)Math.Round(Math.Clamp(peaks[b], 0.0, 1.0) * barAreaH * 0.92) - 2, padTop, floorY - 2);
                    uint pkCol = BlendToWhite(SampleSpectrumBarColor(freqPos, peaks[b], colorFlow), 0.65);
                    for (int x = x0; x < x1; x++)
                    {
                        PlotPixelSafe(x, py, pkCol);
                        PlotPixelSafe(x, py + 1, pkCol);
                    }
                }
            }
        }

        // 2. Glowing multi-color live stereo Waveform ribbon slicing directly through the bars!
        var waveL = frame.WaveLeft;
        var waveR = frame.WaveRight;
        int n = Math.Min(waveL.Length, waveR.Length);
        if (n < 2)
            return;

        double ampScale = (barAreaH * 0.42) * Math.Clamp(SldVisSensitivity?.Value ?? 1.0, 0.4, 2.0);
        int prevX = 4;
        int prevY = Math.Clamp(midY - (int)Math.Round((waveL[0] + waveR[0]) * 0.5 * ampScale), padTop + 4, floorY - 4);
        int prevY2 = Math.Clamp(midY - (int)Math.Round((waveL[0] - waveR[0]) * 0.5 * ampScale), padTop + 4, floorY - 4);

        for (int i = 1; i < n; i++)
        {
            double frac = (double)i / (n - 1);
            int x = 4 + (int)Math.Round(frac * (w - 8));
            double mono = (waveL[i] + waveR[i]) * 0.5;
            double side = (waveL[i] - waveR[i]) * 0.5;
            int y = Math.Clamp(midY - (int)Math.Round(mono * ampScale), padTop + 4, floorY - 4);
            int y2 = Math.Clamp(midY - (int)Math.Round(side * ampScale), padTop + 4, floorY - 4);

            // Complementary shifted phase color for the glowing waveform ribbon so it shimmers against the bars!
            uint waveCol = SamplePaletteAt(frac + 0.35, colorFlow);
            uint waveCore = BlendToWhite(waveCol, 0.62);
            uint waveGlow = ScaleColor(waveCol, 0.70);
            uint sideCol = ScaleColor(SamplePaletteAt(frac + 0.65, colorFlow), 0.65);

            // Secondary stereo-width ribbon
            DrawLineAdditive(prevX, prevY2, x, y2, sideCol);
            // Primary luminous Color Waveform ribbon with soft halo + white-hot core
            DrawGlowingRibbonLine(prevX, prevY, x, y, waveCore, waveGlow);

            prevX = x;
            prevY = y;
            prevY2 = y2;
        }
    }

    // =========================================================================
    // MODE 3: NEON LIQUID AURORA WAVE + RISING PEAK SPARK PARTICLES
    // =========================================================================

    private void RenderLiquidAurora(AudioVisualizerFrame frame, bool showPeakHold, bool colorFlow, double bassEnergy)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int padTop = 26;
        int floorY = h - 34;
        int areaH = Math.Max(40, floorY - padTop);

        DrawStudioSpectrumGrid(padTop, floorY, frame);

        var spec = frame.Spectrum;
        var peaks = frame.PeakHold;
        int bands = spec.Length;
        if (bands < 2)
            return;

        int plotW = Math.Max(16, w - 8);
        int prevY = floorY;
        int prevPkY = floorY;

        for (int px = 0; px < plotW; px++)
        {
            double frac = (double)px / (plotW - 1);
            double bandPos = frac * (bands - 1);
            int b0 = (int)bandPos;
            int b1 = Math.Min(bands - 1, b0 + 1);
            double t = bandPos - b0;
            // Smooth cosine interpolation for liquid silk curve!
            double st = 0.5 - 0.5 * Math.Cos(t * Math.PI);

            double val = Math.Clamp(spec[b0] * (1.0 - st) + spec[b1] * st, 0.0, 1.0);
            int topY = Math.Clamp(floorY - (int)Math.Round(val * areaH), padTop, floorY);
            int x = 4 + px;

            uint crestCol = SamplePaletteAt(frac, colorFlow);
            uint whiteCrest = BlendToWhite(crestCol, 0.68);

            // Liquid translucent aurora gradient fill down to floor
            int span = floorY - topY;
            if (span > 0)
            {
                for (int y = topY; y < floorY; y++)
                {
                    double depth = (double)(y - topY) / Math.Max(1, span); // 0 at crest -> 1 at floor
                    double intensity = Math.Pow(1.0 - depth, 1.45) * (0.28 + val * 0.72);
                    uint col = BlendWithBg(crestCol, intensity);
                    _visPixels[y * w + x] = col;
                }

                // Glassy mirrored floor reflection
                int reflH = Math.Min(h - floorY - 2, Math.Max(2, span / 4));
                for (int r = 1; r <= reflH; r++)
                {
                    int ry = floorY + r;
                    if (ry >= h) break;
                    double fade = (1.0 - (double)r / reflH) * (0.20 + val * 0.16);
                    _visPixels[ry * w + x] = BlendWithBg(crestCol, fade);
                }
            }

            // Glowing white-hot liquid crest line
            if (px > 0 && (val > 0.01 || prevY < floorY - 1))
            {
                DrawGlowingLine(x - 1, prevY, x, topY, whiteCrest, crestCol);
            }

            // Floating Peak Hold aurora ribbon above crest
            if (showPeakHold)
            {
                double pk = Math.Clamp(peaks[b0] * (1.0 - st) + peaks[b1] * st, 0.0, 1.0);
                int pkY = Math.Clamp(floorY - (int)Math.Round(pk * areaH) - 3, padTop, floorY - 2);
                if (px > 0 && pk > 0.025)
                {
                    DrawLine(x - 1, prevPkY, x, pkY, ScaleColor(whiteCrest, 0.80));
                }
                prevPkY = pkY;
            }

            // Emit rising glowing spark particles from energetic peaks!
            if (val > 0.45 && _visRng.NextDouble() < (0.018 + bassEnergy * 0.03))
            {
                _visSparks[_visSparkCursor] = new VisSpark
                {
                    X = x,
                    Y = topY - 2,
                    Vx = (_visRng.NextDouble() - 0.5) * 1.4,
                    Vy = -(0.8 + _visRng.NextDouble() * 2.2),
                    Life = 1.0,
                    Color = whiteCrest
                };
                _visSparkCursor = (_visSparkCursor + 1) % _visSparks.Length;
            }

            prevY = topY;
        }

        // Update & render floating neon sparks
        for (int i = 0; i < _visSparks.Length; i++)
        {
            ref var sp = ref _visSparks[i];
            if (sp.Life <= 0.02) continue;
            sp.X += sp.Vx;
            sp.Y += sp.Vy;
            sp.Vy *= 0.985;
            sp.Life -= 0.022;
            if (sp.Life <= 0.02) continue;

            int sx = (int)Math.Round(sp.X);
            int sy = (int)Math.Round(sp.Y);
            uint c = BlendWithBg(sp.Color, sp.Life);
            uint halo = BlendWithBg(sp.Color, sp.Life * 0.45);
            PlotPixelSafe(sx, sy, c);
            PlotPixelSafe(sx + 1, sy, halo);
            PlotPixelSafe(sx - 1, sy, halo);
            PlotPixelSafe(sx, sy + 1, halo);
            PlotPixelSafe(sx, sy - 1, halo);
        }
    }

    // =========================================================================
    // MODE 4: PEAK CONTOUR (PEAKS ONLY — MINIMALIST FLOATING NEON PEAKS)
    // =========================================================================

    private void RenderPeaksOnlySpectrum(AudioVisualizerFrame frame, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int padTop = 26;
        int floorY = h - 34;
        int barAreaH = Math.Max(40, floorY - padTop);

        DrawStudioSpectrumGrid(padTop, floorY, frame);

        var peaks = frame.PeakHold;
        int bands = peaks.Length;
        if (bands == 0)
            return;

        double stepX = (double)(w - 8) / bands;
        int gap = bands >= 200 ? 1 : (bands >= 100 ? 1 : 2);

        int prevCx = -1;
        int prevPy = -1;
        bool prevActive = false;

        for (int b = 0; b < bands; b++)
        {
            int x0 = 4 + (int)Math.Round(b * stepX);
            int x1 = 4 + (int)Math.Round((b + 1) * stepX) - gap;
            if (x1 <= x0) x1 = x0 + 1;
            if (x1 >= w) x1 = w - 1;
            int cx = (x0 + x1) / 2;

            double pk = Math.Clamp(peaks[b], 0.0, 1.0);
            double freqPos = (double)b / Math.Max(1, bands - 1);
            int py = Math.Clamp(floorY - (int)Math.Round(pk * barAreaH) - 2, padTop, floorY - 2);
            bool active = pk > 0.012;

            uint baseCol = SampleSpectrumBarColor(freqPos, Math.Clamp(pk * 0.9 + 0.15, 0.0, 1.0), colorFlow);
            uint coreCol = BlendToWhite(baseCol, 0.55);
            uint glowCol = ScaleColor(baseCol, 0.48);

            if (active && prevActive && prevCx >= 0)
            {
                DrawGlowingLine(prevCx, prevPy, cx, py, ScaleColor(baseCol, 0.85), ScaleColor(baseCol, 0.28));
            }

            if (active)
            {
                for (int x = x0; x < x1; x++)
                {
                    PlotPixelSafe(x, py - 1, glowCol);
                    PlotPixelSafe(x, py, coreCol);
                    PlotPixelSafe(x, py + 1, coreCol);
                    PlotPixelSafe(x, py + 2, glowCol);
                }

                int ry = floorY + Math.Min(h - floorY - 3, Math.Max(2, (floorY - py) / 7));
                uint reflCol = ScaleColor(baseCol, 0.24);
                for (int x = x0; x < x1; x++)
                {
                    PlotPixelSafe(x, ry, reflCol);
                }
            }

            prevCx = cx;
            prevPy = py;
            prevActive = active;
        }
    }

    // =========================================================================
    // MODE 5: 3D STARFIELD BASS TUNNEL + BEAT SHOCKWAVES
    // =========================================================================

    private void RenderStarfieldBassTunnel(AudioVisualizerFrame frame, bool colorFlow, double bassEnergy)
    {
        int w = _visWidth;
        int h = _visHeight;
        FadeBuffer(0.76);

        int cx = w / 2;
        int cy = h / 2 + 6;
        double maxR = Math.Max(60, Math.Min(w, h) * 0.52);

        // Spawn a neon shockwave ring whenever a bass kick transient hits!
        if (bassEnergy > 0.34 && bassEnergy - _visPrevBassEnergy > 0.06)
        {
            _visRings[_visRingCursor] = new VisShockRing
            {
                Radius = maxR * 0.16,
                Speed = 3.2 + bassEnergy * 5.8,
                Alpha = 1.0,
                Color = SamplePaletteAt(_visRng.NextDouble(), colorFlow)
            };
            _visRingCursor = (_visRingCursor + 1) % _visRings.Length;
        }

        // 1. 3D Starfield flying toward the viewer, accelerating on bass!
        double warpSpeed = 0.008 + bassEnergy * 0.042;
        for (int i = 0; i < _visStars.Length; i++)
        {
            ref var st = ref _visStars[i];
            double oldZ = st.Z;
            st.Z -= warpSpeed;
            if (st.Z <= 0.03)
            {
                st.X = _visRng.NextDouble() * 2.0 - 1.0;
                st.Y = _visRng.NextDouble() * 2.0 - 1.0;
                st.Z = 1.0;
                st.HueOffset = _visRng.NextDouble();
                continue;
            }

            int x0 = cx + (int)Math.Round((st.X / oldZ) * maxR * 0.85);
            int y0 = cy + (int)Math.Round((st.Y / oldZ) * maxR * 0.85);
            int x1 = cx + (int)Math.Round((st.X / st.Z) * maxR * 0.85);
            int y1 = cy + (int)Math.Round((st.Y / st.Z) * maxR * 0.85);

            if ((uint)x1 >= (uint)w || (uint)y1 >= (uint)h)
            {
                st.Z = 1.0;
                continue;
            }

            double brightness = Math.Clamp(1.0 - st.Z, 0.15, 1.0);
            uint col = BlendToWhite(SamplePaletteAt(st.HueOffset, colorFlow), brightness * 0.55);
            DrawLine(x0, y0, x1, y1, ScaleColor(col, brightness));
            PlotPixelSafe(x1, y1, BlendToWhite(col, 0.6));
        }

        // 2. Expanding beat shockwave rings
        for (int i = 0; i < _visRings.Length; i++)
        {
            ref var ring = ref _visRings[i];
            if (ring.Alpha <= 0.04) continue;
            ring.Radius += ring.Speed;
            ring.Alpha *= 0.94;
            if (ring.Radius > Math.Max(w, h) * 0.7 || ring.Alpha <= 0.04)
            {
                ring.Alpha = 0;
                continue;
            }
            uint rc = BlendWithBg(ring.Color, ring.Alpha);
            DrawCircleOutline(cx, cy, (int)ring.Radius, rc);
        }

        // 3. Center pulsing cyber-portal spectrum core
        var spec = frame.Spectrum;
        int bands = spec.Length;
        if (bands == 0) return;

        double coreR = maxR * (0.20 + bassEnergy * 0.16);
        double spikeMax = maxR * 0.45;
        DrawCircleOutline(cx, cy, (int)coreR, BlendToWhite(SamplePaletteAt(0.0, colorFlow), 0.5));
        DrawCircleOutline(cx, cy, (int)(coreR * 0.72), ScaleColor(SamplePaletteAt(0.5, colorFlow), 0.55));

        int spokes = Math.Min(bands, 128);
        for (int s = 0; s < spokes; s++)
        {
            int bIdx = (s * bands) / spokes;
            double frac = (double)s / spokes;
            double ang = frac * Math.PI * 2.0 - Math.PI / 2.0 + _visColorPhase * Math.PI * 0.5;
            double val = Math.Clamp(spec[bIdx], 0.0, 1.0);
            double rOut = coreR + val * spikeMax;

            int x0 = cx + (int)Math.Round(Math.Cos(ang) * coreR);
            int y0 = cy + (int)Math.Round(Math.Sin(ang) * coreR);
            int x1 = cx + (int)Math.Round(Math.Cos(ang) * rOut);
            int y1 = cy + (int)Math.Round(Math.Sin(ang) * rOut);

            uint col = SampleSpectrumBarColor(frac, Math.Min(1.0, val * 1.1 + 0.15), colorFlow);
            DrawLine(x0, y0, x1, y1, col);
        }
    }

    // =========================================================================
    // MODE 6: SYMMETRICAL MIRROR SPECTRUM WAVE
    // =========================================================================

    private void RenderMirrorSpectrum(AudioVisualizerFrame frame, bool showPeakHold, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        Array.Fill(_visPixels, VisBgColor);

        int midY = h / 2;
        int maxHalfH = Math.Max(30, midY - 26);

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
                uint topCol = SampleSpectrumBarColor(freqPos, Math.Min(1.0, frac * 1.15 + 0.15), colorFlow);
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
                uint pkCol = BlendToWhite(SampleSpectrumBarColor(freqPos, peaks[b], colorFlow), 0.6);
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
    // MODE 7: LIVE STEREO OSCILLOSCOPE (PALETTE-AWARE PHOSPHOR PERSISTENCE)
    // =========================================================================

    private void RenderStereoOscilloscope(AudioVisualizerFrame frame, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        FadeBuffer(0.70);

        int midY = h / 2;
        int midL = h / 3;
        int midR = (h * 2) / 3;
        double ampScale = (h * 0.24) * Math.Clamp(SldVisSensitivity?.Value ?? 1.0, 0.4, 2.0);

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

        int prevX = 8;
        int prevYL = Math.Clamp(midL - (int)Math.Round(waveL[0] * ampScale), 26, h - 10);
        int prevYR = Math.Clamp(midR - (int)Math.Round(waveR[0] * ampScale), 26, h - 10);

        for (int i = 1; i < n; i++)
        {
            double frac = (double)i / (n - 1);
            int x = 8 + (int)Math.Round(frac * (w - 16));
            int yL = Math.Clamp(midL - (int)Math.Round(waveL[i] * ampScale), 26, h - 10);
            int yR = Math.Clamp(midR - (int)Math.Round(waveR[i] * ampScale), 26, h - 10);

            uint colL = colorFlow ? SamplePaletteAt(frac, true) : SamplePaletteAt(0.08, false);
            uint colR = colorFlow ? SamplePaletteAt(frac + 0.5, true) : SamplePaletteAt(0.92, false);

            DrawGlowingLine(prevX, prevYL, x, yL, BlendToWhite(colL, 0.25), ScaleColor(colL, 0.45));
            DrawGlowingLine(prevX, prevYR, x, yR, BlendToWhite(colR, 0.25), ScaleColor(colR, 0.45));

            prevX = x;
            prevYL = yL;
            prevYR = yR;
        }
    }

    // =========================================================================
    // MODE 8: REAL-TIME SCROLLING SPECTROGRAM WATERFALL (HEAT MAP)
    // =========================================================================

    private void RenderSpectrogramWaterfall(AudioVisualizerFrame frame, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        int topStart = 24;
        int scrollStep = 2;

        if (h > topStart + scrollStep)
        {
            int copyPixels = (h - topStart - scrollStep) * w;
            Array.Copy(_visPixels, topStart * w, _visPixels, (topStart + scrollStep) * w, copyPixels);
        }

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

            uint col = v < 0.015
                ? VisBgColor
                : ScaleColor(SamplePaletteAt(v, colorFlow), Math.Clamp(0.25 + v * 0.9, 0.0, 1.0));

            for (int r = 0; r < scrollStep; r++)
            {
                _visPixels[(topStart + r) * w + x] = col;
            }
        }
    }

    // =========================================================================
    // MODE 9: PRO MASTERING STEREO VECTORSCOPE (PHASE XY LISSAJOUS SCOPE)
    // =========================================================================

    private void RenderStereoVectorscope(AudioVisualizerFrame frame, bool colorFlow)
    {
        int w = _visWidth;
        int h = _visHeight;
        FadeBuffer(0.80);

        int cx = w / 2;
        int cy = h / 2 + 6;
        int radius = Math.Max(40, Math.Min(w, h) / 2 - 28);

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
            double side = (waveR[i] - waveL[i]) * 0.7071 * gain;
            double mid = (waveL[i] + waveR[i]) * 0.7071 * gain;

            int px = Math.Clamp(cx + (int)Math.Round(side * radius), 6, w - 7);
            int py = Math.Clamp(cy - (int)Math.Round(mid * radius), 26, h - 7);

            double energy = Math.Clamp(Math.Sqrt(side * side + mid * mid), 0.0, 1.0);
            uint col = SamplePaletteAt(0.2 + energy * 0.75, colorFlow);

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
    // MODE 10: RADIAL CYBER HALO (360° BASS-REACTIVE RING SPECTRUM)
    // =========================================================================

    private void RenderRadialHaloSpectrum(AudioVisualizerFrame frame, bool showPeakHold, bool colorFlow)
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

        int bassCount = Math.Max(1, bands / 10);
        double bassAvg = 0;
        for (int i = 0; i < bassCount; i++) bassAvg += spec[i];
        bassAvg /= bassCount;

        double innerR = maxR * (0.28 + bassAvg * 0.14);
        double maxBarLen = maxR - innerR;

        DrawCircleOutline(cx, cy, (int)innerR, SamplePaletteAt(0.0, colorFlow));
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

            uint col = SampleSpectrumBarColor(frac, Math.Min(1.0, val * 1.1 + 0.1), colorFlow);
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
    // GRID, FREQUENCY AXIS & RICH CYCLIC PALETTE ENGINE
    // =========================================================================

    private void DrawStudioSpectrumGrid(int padTop, int floorY, AudioVisualizerFrame frame)
    {
        int w = _visWidth;
        int areaH = floorY - padTop;

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

        double[] ticks = GetVisAxisTicks(frame.MinFreqHz, frame.MaxFreqHz, frame.ScaleMode);
        foreach (double hz in ticks)
        {
            double norm = FrequencyToNormX(hz, frame.MinFreqHz, frame.MaxFreqHz, frame.ScaleMode);
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
        var (minHz, maxHz, scaleMode) = GetSelectedVisRange();
        double[] ticks = GetVisAxisTicks(minHz, maxHz, scaleMode);
        double nyquist = _studio.Visualizer.SampleRate * 0.5;

        double lastRight = -100.0;
        // Pre-measure last label so interior labels never collide with the right-edge frequency label!
        double lastLabelLeft = w;
        if (ticks.Length > 1)
        {
            var probe = new TextBlock
            {
                Text = FormatAxisHz(ticks[^1]),
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lastNorm = FrequencyToNormX(ticks[^1], minHz, maxHz, scaleMode);
            double lastX = 4.0 + lastNorm * (w - 8.0);
            lastLabelLeft = Math.Clamp(lastX - probe.DesiredSize.Width / 2.0, 4.0, Math.Max(4.0, w - probe.DesiredSize.Width - 4.0));
        }

        for (int i = 0; i < ticks.Length; i++)
        {
            double hz = ticks[i];
            double norm = FrequencyToNormX(hz, minHz, maxHz, scaleMode);
            double x = 4.0 + norm * (w - 8.0);

            uint lutCol = _visPaletteLut[Math.Clamp((int)Math.Round(norm * 255.0), 0, 255)];
            byte r = (byte)((lutCol >> 16) & 0xFF);
            byte g = (byte)((lutCol >> 8) & 0xFF);
            byte b = (byte)(lutCol & 0xFF);

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

            // Anti-collision guard: never overlap neighboring frequency labels at any window width!
            bool isLast = i == ticks.Length - 1;
            if (i > 0 && left < lastRight + 8.0)
                continue;
            if (!isLast && i > 0 && left + textW + 8.0 > lastLabelLeft)
                continue;

            Canvas.SetLeft(lbl, left);
            Canvas.SetTop(lbl, 2.0);
            CanvasVisFreqAxis.Children.Add(lbl);
            lastRight = left + textW;
        }
    }

    private static double FrequencyToNormX(double hz, double minHz, double maxHz, SpectrumScaleMode scaleMode)
    {
        if (scaleMode == SpectrumScaleMode.Linear)
        {
            return Math.Clamp((hz - minHz) / Math.Max(1.0, maxHz - minHz), 0.0, 1.0);
        }

        if (scaleMode == SpectrumScaleMode.ShiftedLogZero)
        {
            double f = Math.Clamp(hz - minHz, 0.0, Math.Max(1.0, maxHz - minHz));
            double span = Math.Max(1.0, maxHz - minHz);
            return Math.Clamp(
                Math.Log(1.0 + f / WasapiLoopbackAnalyzer.ShiftedLogKneeHz) /
                Math.Log(1.0 + span / WasapiLoopbackAnalyzer.ShiftedLogKneeHz),
                0.0, 1.0);
        }

        double lo = Math.Max(1.0, minHz);
        double hi = Math.Max(lo * 2.0, maxHz);
        double fc = Math.Clamp(hz, lo, hi);
        return Math.Clamp(Math.Log(fc / lo) / Math.Log(hi / lo), 0.0, 1.0);
    }

    private static double[] GetVisAxisTicks(double minHz, double maxHz, SpectrumScaleMode scaleMode)
    {
        if (scaleMode == SpectrumScaleMode.Linear)
        {
            if (maxHz <= 24000.0)
            {
                return new double[] { 0, 2000, 4000, 6000, 8000, 10000, 12000, 14000, 16000, 18000, 20000 };
            }
            return new double[] { 0, 6000, 12000, 18000, 24000, 30000, 36000, 42000, 48000 };
        }

        if (scaleMode == SpectrumScaleMode.ShiftedLogZero)
        {
            // Cleanly spaced musical ticks starting at literal 0 Hz up to 20 kHz (zero overlap at right edge!)
            return new double[] { 0, 20, 50, 100, 250, 500, 1000, 2000, 4000, 8000, 13000, 20000 };
        }

        if (minHz <= 6.0 && maxHz >= 40000.0)
        {
            return new double[] { 5, 10, 20, 40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 20000, 40000 };
        }

        if (minHz <= 6.0)
        {
            return new double[] { 5, 10, 20, 40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 20000 };
        }

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

    private (double minHz, double maxHz, SpectrumScaleMode scaleMode) GetSelectedVisRange()
    {
        string tag = GetSelectedComboTag(CmbVisRange, "0:20000:Log0");
        string[] parts = tag.Split(':');
        if (parts.Length == 3
            && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double minHz)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double maxHz))
        {
            SpectrumScaleMode mode = parts[2] switch
            {
                "Lin" => SpectrumScaleMode.Linear,
                "Log0" => SpectrumScaleMode.ShiftedLogZero,
                _ => SpectrumScaleMode.Logarithmic
            };
            return (minHz, maxHz, mode);
        }
        return (0.0, 20000.0, SpectrumScaleMode.ShiftedLogZero);
    }

    private void BuildVisualizerPaletteLut(string paletteName)
    {
        _visCurrentPalette = paletteName;

        // Build standard 0..1 gradient LUT
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            var rgb = EvaluatePaletteRgb(paletteName, t);
            _visPaletteLut[i] = 0xFF000000u | ((uint)rgb.r << 16) | ((uint)rgb.g << 8) | rgb.b;
        }

        // Build seamless cyclic LUT (loops cleanly from 1.0 -> 0.0 with zero harsh seam during Color Flow!)
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            (byte r, byte g, byte b) rgb;
            if (string.Equals(paletteName, "Rainbow", StringComparison.OrdinalIgnoreCase))
            {
                // Full 360° HSV spectrum is naturally seamless!
                rgb = HsvToRgb(t * 360.0, 0.94, 1.0);
            }
            else
            {
                // Mirror smoothly: 0..0.5 goes forward (0->1), 0.5..1.0 glides back (1->0) for liquid continuous flow
                double pingPong = t <= 0.5 ? (t * 2.0) : ((1.0 - t) * 2.0);
                rgb = EvaluatePaletteRgb(paletteName, pingPong);
            }
            _visCyclicLut[i] = 0xFF000000u | ((uint)rgb.r << 16) | ((uint)rgb.g << 8) | rgb.b;
        }

        RenderVisualizerFreqAxis();
    }

    private (byte r, byte g, byte b) EvaluatePaletteRgb(string paletteName, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return paletteName switch
        {
            "Rainbow" => HsvToRgb(t * 320.0, 0.94, 1.0),
            "Cyberpunk" => t < 0.35
                ? LerpRgb(255, 36, 152, 148, 66, 255, t / 0.35)
                : t < 0.72
                    ? LerpRgb(148, 66, 255, 24, 236, 255, (t - 0.35) / 0.37)
                    : LerpRgb(24, 236, 255, 62, 255, 178, (t - 0.72) / 0.28),
            "Aurora" => t < 0.35
                ? LerpRgb(48, 62, 220, 20, 235, 172, t / 0.35)
                : t < 0.70
                    ? LerpRgb(20, 235, 172, 46, 218, 255, (t - 0.35) / 0.35)
                    : LerpRgb(46, 218, 255, 198, 92, 255, (t - 0.70) / 0.30),
            "Studio" => t < 0.55
                ? LerpRgb(16, 188, 124, 34, 232, 255, t / 0.55)
                : t < 0.82
                    ? LerpRgb(34, 232, 255, 255, 184, 32, (t - 0.55) / 0.27)
                    : LerpRgb(255, 184, 32, 255, 68, 102, (t - 0.82) / 0.18),
            "Ice" => t < 0.5
                ? LerpRgb(20, 62, 168, 34, 232, 255, t / 0.5)
                : LerpRgb(34, 232, 255, 236, 252, 255, (t - 0.5) / 0.5),
            "Fire" => t < 0.4
                ? LerpRgb(72, 18, 132, 255, 43, 180, t / 0.4)
                : t < 0.78
                    ? LerpRgb(255, 43, 180, 255, 138, 28, (t - 0.4) / 0.38)
                    : LerpRgb(255, 138, 28, 255, 248, 196, (t - 0.78) / 0.22),
            "Emerald" => t < 0.55
                ? LerpRgb(10, 96, 58, 34, 232, 122, t / 0.55)
                : LerpRgb(34, 232, 122, 196, 255, 214, (t - 0.55) / 0.45),
            "Custom" => t < 0.5
                ? LerpRgb(_visCustomC1.r, _visCustomC1.g, _visCustomC1.b, _visCustomC2.r, _visCustomC2.g, _visCustomC2.b, t / 0.5)
                : LerpRgb(_visCustomC2.r, _visCustomC2.g, _visCustomC2.b, _visCustomC3.r, _visCustomC3.g, _visCustomC3.b, (t - 0.5) / 0.5),
            _ => t < 0.50 // Spectra Neon
                ? LerpRgb(34, 232, 255, 126, 118, 255, t / 0.50)
                : LerpRgb(126, 118, 255, 255, 43, 214, (t - 0.50) / 0.50)
        };
    }

    private uint SamplePaletteAt(double pos, bool colorFlow)
    {
        if (colorFlow)
        {
            double wrapped = (pos - _visColorPhase) % 1.0;
            if (wrapped < 0.0) wrapped += 1.0;
            int idx = Math.Clamp((int)Math.Round(wrapped * 255.0), 0, 255);
            return _visCyclicLut[idx];
        }
        int staticIdx = Math.Clamp((int)Math.Round(Math.Clamp(pos, 0.0, 1.0) * 255.0), 0, 255);
        return _visPaletteLut[staticIdx];
    }

    private uint SampleSpectrumBarColor(double freqPos, double heightFrac, bool colorFlow)
    {
        if (!colorFlow && string.Equals(_visCurrentPalette, "Studio", StringComparison.OrdinalIgnoreCase))
        {
            int idx = Math.Clamp((int)Math.Round(heightFrac * 255.0), 0, 255);
            return _visPaletteLut[idx];
        }

        uint baseCol = colorFlow
            ? SamplePaletteAt(freqPos + heightFrac * 0.12, true)
            : SamplePaletteAt(freqPos * 0.80 + heightFrac * 0.20, false);

        return ScaleColor(baseCol, 0.46 + heightFrac * 0.54);
    }

    private static (byte r, byte g, byte b) HsvToRgb(double hDeg, double s, double v)
    {
        double h = ((hDeg % 360.0) + 360.0) % 360.0;
        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;
        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return (
            (byte)Math.Clamp(Math.Round((r1 + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((g1 + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((b1 + m) * 255.0), 0, 255));
    }

    private static (byte r, byte g, byte b) ParseHexColor(string? hex, (byte r, byte g, byte b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6
            && byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }
        return fallback;
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

    private static uint BlendWithBg(uint argb, double alpha)
    {
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        double inv = 1.0 - alpha;
        uint r = (uint)(((argb >> 16) & 0xFF) * alpha + 7.0 * inv);
        uint g = (uint)(((argb >> 8) & 0xFF) * alpha + 10.0 * inv);
        uint b = (uint)((argb & 0xFF) * alpha + 20.0 * inv);
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

    private void PlotPixelAdditive(int x, int y, uint col)
    {
        if ((uint)x >= (uint)_visWidth || (uint)y >= (uint)_visHeight)
            return;
        int idx = y * _visWidth + x;
        uint dst = _visPixels[idx];
        uint r = Math.Min(255u, ((dst >> 16) & 0xFF) + ((col >> 16) & 0xFF));
        uint g = Math.Min(255u, ((dst >> 8) & 0xFF) + ((col >> 8) & 0xFF));
        uint b = Math.Min(255u, (dst & 0xFF) + (col & 0xFF));
        _visPixels[idx] = 0xFF000000u | (r << 16) | (g << 8) | b;
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

    private void DrawLineAdditive(int x0, int y0, int x1, int y1, uint col)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            PlotPixelAdditive(x0, y0, col);
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

    private void DrawGlowingRibbonLine(int x0, int y0, int x1, int y1, uint coreCol, uint glowCol)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        uint softGlow = ScaleColor(glowCol, 0.50);

        while (true)
        {
            PlotPixelAdditive(x0, y0 - 2, softGlow);
            PlotPixelAdditive(x0, y0 + 2, softGlow);
            PlotPixelAdditive(x0, y0 - 1, glowCol);
            PlotPixelAdditive(x0, y0 + 1, glowCol);
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
