using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;
using Path = System.IO.Path;

namespace SpectraRealtekStudio.App;

public partial class AudioSampleEditorWindow : Window
{
    private enum DragMode
    {
        None,
        DragInHandle,
        DragOutHandle,
        SweepNewSelection,
        SeekPlayhead
    }

    private readonly AudioSampleEditorService _editor = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly string _defaultSaveFolder;
    private bool _suppressUiEvents = true;

    private WriteableBitmap? _waveBitmap;
    private uint[] _wavePixels = Array.Empty<uint>();
    private int _waveWidth;
    private int _waveHeight;

    // Viewport zoom state: [_viewStartFrame .. _viewEndFrame]
    private int _viewStartFrame;
    private int _viewEndFrame;

    // Mouse interaction state
    private DragMode _dragMode = DragMode.None;
    private int _sweepAnchorFrame;
    private int _lastInPixelX = -100;
    private int _lastOutPixelX = -100;

    /// <summary>
    /// Fired whenever the user saves a selected loop/part, saves a full edited file, or overwrites an existing clip.
    /// Second parameter is true when an existing file path was overwritten in place.
    /// </summary>
    public event Action<RecordedClipInfo, bool>? ClipSaved;

    public AudioSampleEditorWindow(
        string? initialFilePath,
        string defaultSaveFolder,
        string? playbackEndpointId)
    {
        _defaultSaveFolder = string.IsNullOrWhiteSpace(defaultSaveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SpectraRecordings")
            : defaultSaveFolder;

        _suppressUiEvents = true;
        InitializeComponent();
        _suppressUiEvents = false;

        _editor.SetPlaybackEndpoint(playbackEndpointId);

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // 60 FPS smooth playhead & loop audition
        };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialFilePath) && File.Exists(initialFilePath))
            {
                TryLoadAudioFile(initialFilePath);
            }
            else
            {
                UpdateAllReadouts();
                RenderWaveform();
            }
        };
    }

    public bool PromptAndLoadFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Audio Track into Spectra Sample & Loop Studio",
            Filter = "All Supported Audio & Media (*.wav;*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.wma;*.opus;*.aiff;*.mp4;*.mkv)|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.wma;*.opus;*.aif;*.aiff;*.mp4;*.mkv|WAV Lossless Audio (*.wav)|*.wav|FLAC Lossless Audio (*.flac)|*.flac|MP3 / AAC / M4A (*.mp3;*.m4a;*.aac)|*.mp3;*.m4a;*.aac|All Files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_defaultSaveFolder)
                ? _defaultSaveFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog(this) == true)
        {
            return TryLoadAudioFile(dlg.FileName);
        }
        return false;
    }

    public bool TryLoadAudioFile(string filePath)
    {
        try
        {
            Cursor = Cursors.Wait;
            _editor.LoadFile(filePath);

            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);

            // Match Save Format ComboBox to the loaded file's native bit depth
            _suppressUiEvents = true;
            CmbSaveBitFormat.SelectedIndex = _editor.DetectedBitFormat switch
            {
                RecordingBitFormat.Pcm16 => 0,
                RecordingBitFormat.Float32 => 2,
                _ => 1
            };
            _suppressUiEvents = false;

            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            UpdateLoopButtonVisual();
            RenderWaveform();

            TxtEditorStatus.Text = $"Loaded '{_editor.TrackTitle}' at native {_editor.SampleRate:N0} Hz ({FormatTime(_editor.TotalDurationSeconds)}). Drag IN/OUT handles or click 🔁 Loop Selection!";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not load audio file:\n{ex.Message}",
                "Spectra Sample & Loop Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Cursor = Cursors.Arrow;
        }
    }

    private RecordingBitFormat GetSelectedSaveBitFormat()
    {
        return CmbSaveBitFormat?.SelectedIndex switch
        {
            0 => RecordingBitFormat.Pcm16,
            2 => RecordingBitFormat.Float32,
            _ => RecordingBitFormat.Pcm24
        };
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        PbEditorPeakL.Value = Math.Clamp(_editor.PeakLeft * 100.0, 0.0, 100.0);
        PbEditorPeakR.Value = Math.Clamp(_editor.PeakRight * 100.0, 0.0, 100.0);

        UpdateLoopButtonVisual();
        UpdateLoopBpmAndPlayheadText();

        if (_editor.IsPlaying)
        {
            RenderWaveform();
        }
    }

    private void UpdateLoopButtonVisual()
    {
        bool loopingActive = _editor.IsPlaying && _editor.LoopEnabled;
        if (loopingActive)
        {
            BtnLoopSelection.Content = "🔁 Looping Active (Click to Stop)";
            BtnLoopSelection.Background = new SolidColorBrush(Color.FromRgb(34, 232, 122));
            BtnLoopSelection.Foreground = new SolidColorBrush(Color.FromRgb(5, 16, 10));
        }
        else
        {
            BtnLoopSelection.Content = "🔁 Loop Selection";
            BtnLoopSelection.Background = new SolidColorBrush(Color.FromRgb(22, 68, 46));
            BtnLoopSelection.Foreground = (Brush)FindResource("Green");
        }
    }

    private void UpdateAllReadouts()
    {
        bool hasAudio = _editor.HasAudio;
        BtnUndo.IsEnabled = _editor.CanUndo;
        BtnRedo.IsEnabled = _editor.CanRedo;
        BtnResetOriginal.IsEnabled = hasAudio;
        BtnLoopSelection.IsEnabled = hasAudio;
        BtnPlaySelectionOnce.IsEnabled = hasAudio;
        BtnPlayFull.IsEnabled = hasAudio;
        BtnStop.IsEnabled = hasAudio;
        BtnCropStartAndEnd.IsEnabled = hasAudio;
        BtnCutStartOnly.IsEnabled = hasAudio;
        BtnCutEndOnly.IsEnabled = hasAudio;
        BtnDeleteMiddle.IsEnabled = hasAudio;
        BtnFadeIn.IsEnabled = hasAudio;
        BtnFadeOut.IsEnabled = hasAudio;
        BtnNormalize.IsEnabled = hasAudio;
        BtnSaveSelectedLoopAs.IsEnabled = hasAudio;
        BtnSaveFullTrackAs.IsEnabled = hasAudio;

        bool isCurrentWav = hasAudio
            && !string.IsNullOrWhiteSpace(_editor.CurrentFilePath)
            && string.Equals(Path.GetExtension(_editor.CurrentFilePath), ".wav", StringComparison.OrdinalIgnoreCase)
            && File.Exists(_editor.CurrentFilePath);
        BtnOverwriteCurrentWav.IsEnabled = isCurrentWav;

        if (!hasAudio)
        {
            TxtInTime.Text = "00:00.000";
            TxtOutTime.Text = "00:00.000";
            TxtSelectedDuration.Text = "00:00.000";
            TxtLoopBpmEstimate.Text = "Playhead: 00:00.000 • Loop BPM: —";
            return;
        }

        string bitLabel = _editor.DetectedBitFormat switch
        {
            RecordingBitFormat.Pcm16 => "16-bit PCM",
            RecordingBitFormat.Float32 => "32-bit IEEE Float",
            _ => "24-bit Studio PCM"
        };

        TxtTrackTitle.Text = _editor.TrackTitle;
        TxtFormatBadge.Text = $"{_editor.SampleRate:N0} Hz • Stereo • {bitLabel} • Total Length: {FormatTime(_editor.TotalDurationSeconds)} ({_editor.TotalFrames:N0} frames)";

        TxtInTime.Text = FormatTime(_editor.SelectionStartSeconds);
        TxtOutTime.Text = FormatTime(_editor.SelectionEndSeconds);
        TxtSelectedDuration.Text = FormatTime(_editor.SelectionLengthSeconds);
        UpdateLoopBpmAndPlayheadText();
    }

    private void UpdateLoopBpmAndPlayheadText()
    {
        double selSec = _editor.SelectionLengthSeconds;
        string bpmHint = "—";
        if (selSec >= 0.25 && selSec <= 32.0)
        {
            // Estimate common 4-beat (1 bar), 8-beat (2 bars), or 16-beat (4 bars) loop BPM in [70 .. 175] range
            double bpm4 = 240.0 / selSec;
            double bpm8 = 480.0 / selSec;
            double bpm16 = 960.0 / selSec;

            if (bpm4 >= 68.0 && bpm4 <= 180.0)
                bpmHint = $"{bpm4:0.0} BPM (1 Bar / 4 Beats)";
            else if (bpm8 >= 68.0 && bpm8 <= 180.0)
                bpmHint = $"{bpm8:0.0} BPM (2 Bars / 8 Beats)";
            else if (bpm16 >= 68.0 && bpm16 <= 180.0)
                bpmHint = $"{bpm16:0.0} BPM (4 Bars / 16 Beats)";
            else
                bpmHint = $"{bpm4:0.0} BPM (4 Beats)";
        }

        TxtLoopBpmEstimate.Text = $"Playhead: {FormatTime(_editor.PlayheadSeconds)} • Loop Tempo: {bpmHint}";
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    // =========================================================================
    // 60 FPS STEREO WAVEFORM & INTERACTIVE LOOP HANDLE RENDERER
    // =========================================================================

    private void WaveCanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureWaveformBitmap();
        RenderWaveform();
    }

    private void EnsureWaveformBitmap()
    {
        int w = Math.Max(320, (int)WaveCanvasHost.ActualWidth);
        int h = Math.Max(160, (int)WaveCanvasHost.ActualHeight);
        if (_waveBitmap != null && _waveWidth == w && _waveHeight == h)
            return;

        _waveWidth = w;
        _waveHeight = h;
        _wavePixels = new uint[w * h];
        _waveBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        ImgEditorWaveform.Source = _waveBitmap;
    }

    private void RenderWaveform()
    {
        EnsureWaveformBitmap();
        if (_waveBitmap == null || _waveWidth <= 0 || _waveHeight <= 0)
            return;

        int w = _waveWidth;
        int h = _waveHeight;
        const int rulerH = 22;

        Array.Fill(_wavePixels, 0xFF070A14u);

        // Draw top time ruler bar background
        for (int y = 0; y < rulerH; y++)
        {
            int rowOff = y * w;
            uint rulerBg = y == rulerH - 1 ? 0xFF24325Au : 0xFF0E1426u;
            for (int x = 0; x < w; x++)
                _wavePixels[rowOff + x] = rulerBg;
        }

        _editor.GetBuffers(out float[] left, out float[] right, out int sampleRate);
        int totalFrames = left.Length;
        if (totalFrames <= 0)
        {
            FlushWaveformBitmap();
            return;
        }

        int viewStart = Math.Clamp(_viewStartFrame, 0, Math.Max(0, totalFrames - 1));
        int viewEnd = Math.Clamp(_viewEndFrame, viewStart + 1, totalFrames);
        int viewLen = Math.Max(1, viewEnd - viewStart);

        int selStart = _editor.SelectionStartFrame;
        int selEnd = _editor.SelectionEndFrame;

        int xIn = FrameToPixelX(selStart, viewStart, viewLen, w);
        int xOut = FrameToPixelX(selEnd, viewStart, viewLen, w);
        _lastInPixelX = xIn;
        _lastOutPixelX = xOut;

        int bodyTop = rulerH;
        int bodyH = Math.Max(40, h - rulerH - 22);
        int midL = bodyTop + bodyH / 4;
        int midR = bodyTop + (bodyH * 3) / 4;
        int chanHalfH = Math.Max(12, bodyH / 4 - 6);
        int sepY = bodyTop + bodyH / 2;

        // 1. Highlight active [IN .. OUT] selection region backdrop & dim trimmed outer zones
        int selX0 = Math.Clamp(xIn, 0, w - 1);
        int selX1 = Math.Clamp(xOut, selX0, w - 1);
        bool looping = _editor.IsPlaying && _editor.LoopEnabled;
        uint selBg = looping ? 0xFF0E2422u : 0xFF0D1D31u;
        uint cutZoneBg = 0xFF05070Eu;

        for (int y = bodyTop; y < h; y++)
        {
            int rowOff = y * w;
            for (int x = 0; x < selX0; x++)
                _wavePixels[rowOff + x] = cutZoneBg;
            for (int x = selX0; x <= selX1; x++)
                _wavePixels[rowOff + x] = selBg;
            for (int x = selX1 + 1; x < w; x++)
                _wavePixels[rowOff + x] = cutZoneBg;
        }

        // 2. Horizontal L/R center lines & channel divider
        for (int x = 0; x < w; x++)
        {
            _wavePixels[midL * w + x] = 0xFF1A294Cu;
            _wavePixels[sepY * w + x] = 0xFF141E38u;
            _wavePixels[midR * w + x] = 0xFF1A294Cu;
        }

        // 3. Top Ruler Time Ticks
        int numTicks = 10;
        for (int t = 0; t <= numTicks; t++)
        {
            int tx = (t * (w - 1)) / numTicks;
            for (int y = rulerH - 7; y < rulerH - 1; y++)
                _wavePixels[y * w + tx] = 0xFF4B649Au;
        }

        // 4. Stereo L (Top, Cyan/Green) & R (Bottom, Magenta/Violet) Waveform Bars
        double framesPerPixel = (double)viewLen / w;
        for (int x = 0; x < w; x++)
        {
            int f0 = viewStart + (int)(x * framesPerPixel);
            int f1 = viewStart + (int)((x + 1) * framesPerPixel);
            if (f0 >= totalFrames) break;
            if (f1 <= f0) f1 = f0 + 1;
            if (f1 > totalFrames) f1 = totalFrames;

            // Sub-sample step when zoomed out on huge 192 kHz files so rendering stays 60 FPS
            int step = Math.Max(1, (f1 - f0) / 48);
            float minL = 0f, maxL = 0f;
            float minR = 0f, maxR = 0f;

            for (int f = f0; f < f1; f += step)
            {
                float vL = left[f];
                float vR = right[f];
                if (vL < minL) minL = vL;
                if (vL > maxL) maxL = vL;
                if (vR < minR) minR = vR;
                if (vR > maxR) maxR = vR;
            }

            bool insideSelection = x >= selX0 && x <= selX1;
            uint colL = insideSelection
                ? (looping ? 0xFF22E87Au : 0xFF22E8FFu)
                : 0xFF244358u;
            uint colR = insideSelection
                ? (looping ? 0xFF34D2FFu : 0xFFFF2BD6u)
                : 0xFF47274Du;

            int yLTop = Math.Clamp(midL - (int)Math.Round(Math.Clamp(maxL, 0f, 1f) * chanHalfH), bodyTop + 2, midL);
            int yLBot = Math.Clamp(midL - (int)Math.Round(Math.Clamp(minL, -1f, 0f) * chanHalfH), midL, sepY - 2);
            for (int y = yLTop; y <= yLBot; y++)
            {
                _wavePixels[y * w + x] = colL;
            }

            int yRTop = Math.Clamp(midR - (int)Math.Round(Math.Clamp(maxR, 0f, 1f) * chanHalfH), sepY + 2, midR);
            int yRBot = Math.Clamp(midR - (int)Math.Round(Math.Clamp(minR, -1f, 0f) * chanHalfH), midR, bodyTop + bodyH - 2);
            for (int y = yRTop; y <= yRBot; y++)
            {
                _wavePixels[y * w + x] = colR;
            }
        }

        // 5. Draw IN (Start) Handle — Bright Neon Green (#22E87A) with top/bottom grab flags
        if (xIn >= 0 && xIn < w)
        {
            DrawVerticalHandle(xIn, w, h, 0xFF22E87Au, isRightFlag: true);
        }

        // 6. Draw OUT (End) Handle — Bright Studio Amber (#FFB020) with top/bottom grab flags
        if (xOut >= 0 && xOut < w)
        {
            DrawVerticalHandle(xOut, w, h, 0xFFFFB020u, isRightFlag: false);
        }

        // 7. Draw Playhead Needle — Crisp White (#FFFFFF) with top triangle pointer
        int xPlay = FrameToPixelX((int)Math.Round(_editor.PlayheadFrame), viewStart, viewLen, w);
        if (xPlay >= 0 && xPlay < w)
        {
            for (int y = 0; y < h; y++)
            {
                _wavePixels[y * w + xPlay] = 0xFFFFFFFFu;
                if (xPlay + 1 < w)
                    _wavePixels[y * w + xPlay + 1] = 0xCC22E8FFu;
            }

            // Playhead triangle pointer in top ruler
            for (int dy = 0; dy < 10; dy++)
            {
                int halfSpan = 5 - dy / 2;
                int y = 2 + dy;
                for (int dx = -halfSpan; dx <= halfSpan; dx++)
                {
                    int px = xPlay + dx;
                    if (px >= 0 && px < w && y < rulerH)
                        _wavePixels[y * w + px] = 0xFFFFFFFFu;
                }
            }
        }

        FlushWaveformBitmap();
    }

    private void DrawVerticalHandle(int centerX, int w, int h, uint color, bool isRightFlag)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            int px = centerX + dx;
            if (px < 0 || px >= w) continue;
            for (int y = 0; y < h; y++)
            {
                _wavePixels[y * w + px] = color;
            }
        }

        // Top and bottom grab tab flags (14 px wide x 16 px tall)
        int flagWidth = 14;
        int flagHeight = 16;
        int xStart = isRightFlag ? centerX : centerX - flagWidth;
        int xEnd = isRightFlag ? centerX + flagWidth : centerX;

        for (int y = 2; y < 2 + flagHeight && y < h; y++)
        {
            int rowOff = y * w;
            for (int x = Math.Max(0, xStart); x <= Math.Min(w - 1, xEnd); x++)
            {
                _wavePixels[rowOff + x] = color;
            }
        }

        for (int y = Math.Max(0, h - flagHeight - 2); y < h - 2; y++)
        {
            int rowOff = y * w;
            for (int x = Math.Max(0, xStart); x <= Math.Min(w - 1, xEnd); x++)
            {
                _wavePixels[rowOff + x] = color;
            }
        }
    }

    private void FlushWaveformBitmap()
    {
        if (_waveBitmap == null)
            return;
        _waveBitmap.WritePixels(
            new Int32Rect(0, 0, _waveWidth, _waveHeight),
            _wavePixels,
            _waveWidth * 4,
            0);
    }

    private static int FrameToPixelX(int frame, int viewStart, int viewLen, int width)
    {
        if (width <= 1 || viewLen <= 0)
            return 0;
        double norm = (double)(frame - viewStart) / viewLen;
        return Math.Clamp((int)Math.Round(norm * (width - 1)), 0, width - 1);
    }

    private int PixelXToFrame(double mouseX)
    {
        int total = _editor.TotalFrames;
        if (total <= 0 || _waveWidth <= 1)
            return 0;

        int viewStart = Math.Clamp(_viewStartFrame, 0, Math.Max(0, total - 1));
        int viewEnd = Math.Clamp(_viewEndFrame, viewStart + 1, total);
        int viewLen = Math.Max(1, viewEnd - viewStart);

        double norm = Math.Clamp(mouseX / (_waveWidth - 1), 0.0, 1.0);
        return Math.Clamp(viewStart + (int)Math.Round(norm * viewLen), 0, total);
    }

    // =========================================================================
    // INTERACTIVE WAVEFORM MOUSE DRAGGING (IN/OUT HANDLES, REGION SWEEP, ZOOM)
    // =========================================================================

    private void ImgEditorWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        Point pt = e.GetPosition(ImgEditorWaveform);
        int clickedFrame = PixelXToFrame(pt.X);

        // Check if user grabbed the IN or OUT handle (within 11 px horizontally)
        double distIn = Math.Abs(pt.X - _lastInPixelX);
        double distOut = Math.Abs(pt.X - _lastOutPixelX);

        if (distIn <= 11 && distIn <= distOut)
        {
            _dragMode = DragMode.DragInHandle;
        }
        else if (distOut <= 11)
        {
            _dragMode = DragMode.DragOutHandle;
        }
        else if (pt.Y <= 24)
        {
            // Clicking inside top time ruler seeks playhead directly
            _dragMode = DragMode.SeekPlayhead;
            _editor.PlayheadFrame = clickedFrame;
        }
        else
        {
            // Clicking & dragging inside waveform body sweeps a new selection / loop range
            _dragMode = DragMode.SweepNewSelection;
            _sweepAnchorFrame = clickedFrame;
            _editor.PlayheadFrame = clickedFrame;
        }

        ImgEditorWaveform.CaptureMouse();
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void ImgEditorWaveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        Point pt = e.GetPosition(ImgEditorWaveform);

        if (_dragMode == DragMode.None)
        {
            double distIn = Math.Abs(pt.X - _lastInPixelX);
            double distOut = Math.Abs(pt.X - _lastOutPixelX);
            if (distIn <= 11 || distOut <= 11)
                ImgEditorWaveform.Cursor = Cursors.SizeWE;
            else if (pt.Y <= 24)
                ImgEditorWaveform.Cursor = Cursors.Hand;
            else
                ImgEditorWaveform.Cursor = Cursors.IBeam;
            return;
        }

        int frame = PixelXToFrame(pt.X);
        switch (_dragMode)
        {
            case DragMode.DragInHandle:
                _editor.SetSelectionStart(frame, applyZeroCrossingSnap: true);
                break;

            case DragMode.DragOutHandle:
                _editor.SetSelectionEnd(frame, applyZeroCrossingSnap: true);
                break;

            case DragMode.SweepNewSelection:
                if (Math.Abs(frame - _sweepAnchorFrame) > _editor.SampleRate / 200)
                {
                    _editor.SetSelectionRange(_sweepAnchorFrame, frame, applyZeroCrossingSnap: true);
                }
                break;

            case DragMode.SeekPlayhead:
                _editor.PlayheadFrame = frame;
                break;
        }

        UpdateAllReadouts();
        RenderWaveform();
    }

    private void ImgEditorWaveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            ImgEditorWaveform.ReleaseMouseCapture();
            UpdateAllReadouts();
            RenderWaveform();
        }
    }

    private void ImgEditorWaveform_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        Point pt = e.GetPosition(ImgEditorWaveform);
        int frame = PixelXToFrame(pt.X);
        _editor.PlayheadFrame = frame;
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void ImgEditorWaveform_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        Point pt = e.GetPosition(ImgEditorWaveform);
        int centerFrame = PixelXToFrame(pt.X);
        double factor = e.Delta > 0 ? 0.65 : 1.55;
        ZoomAroundFrame(centerFrame, factor);
    }

    // =========================================================================
    // ZOOM & SCROLL CONTROLS
    // =========================================================================

    private void ZoomAroundFrame(int anchorFrame, double scaleFactor)
    {
        int total = _editor.TotalFrames;
        if (total <= 64)
            return;

        int curLen = Math.Max(64, _viewEndFrame - _viewStartFrame);
        int minViewLen = Math.Max(128, _editor.SampleRate / 25); // down to ~40 ms zoom
        int newLen = (int)Math.Clamp(Math.Round(curLen * scaleFactor), minViewLen, total);

        double anchorRatio = (double)(anchorFrame - _viewStartFrame) / curLen;
        anchorRatio = Math.Clamp(anchorRatio, 0.0, 1.0);

        int newStart = (int)Math.Round(anchorFrame - anchorRatio * newLen);
        newStart = Math.Clamp(newStart, 0, Math.Max(0, total - newLen));

        _viewStartFrame = newStart;
        _viewEndFrame = Math.Min(total, newStart + newLen);

        SyncZoomSliderFromViewport();
        RenderWaveform();
    }

    private void SyncZoomSliderFromViewport()
    {
        if (SldZoomScroll == null)
            return;

        int total = _editor.TotalFrames;
        int viewLen = Math.Max(1, _viewEndFrame - _viewStartFrame);
        bool isZoomed = total > 0 && viewLen < total;

        bool prev = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            SldZoomScroll.IsEnabled = isZoomed;
            if (!isZoomed)
            {
                SldZoomScroll.Value = 0;
            }
            else
            {
                int maxStart = Math.Max(1, total - viewLen);
                SldZoomScroll.Value = Math.Clamp((double)_viewStartFrame / maxStart, 0.0, 1.0);
            }
        }
        finally
        {
            _suppressUiEvents = prev;
        }
    }

    private void SldZoomScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded || !_editor.HasAudio)
            return;

        int total = _editor.TotalFrames;
        int viewLen = Math.Clamp(_viewEndFrame - _viewStartFrame, 1, total);
        int maxStart = Math.Max(0, total - viewLen);
        _viewStartFrame = (int)Math.Round(SldZoomScroll.Value * maxStart);
        _viewEndFrame = Math.Min(total, _viewStartFrame + viewLen);
        RenderWaveform();
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int center = (_editor.SelectionStartFrame + _editor.SelectionEndFrame) / 2;
        ZoomAroundFrame(center, 0.6);
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int center = (_editor.SelectionStartFrame + _editor.SelectionEndFrame) / 2;
        ZoomAroundFrame(center, 1.65);
    }

    private void BtnZoomSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int pad = Math.Max(64, _editor.SelectionLengthFrames / 12);
        _viewStartFrame = Math.Max(0, _editor.SelectionStartFrame - pad);
        _viewEndFrame = Math.Min(_editor.TotalFrames, _editor.SelectionEndFrame + pad);
        SyncZoomSliderFromViewport();
        RenderWaveform();
    }

    private void BtnZoomAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        _viewStartFrame = 0;
        _viewEndFrame = Math.Max(1, _editor.TotalFrames);
        SyncZoomSliderFromViewport();
        RenderWaveform();
    }

    // =========================================================================
    // PRECISION IN/OUT NUDGE, ZERO-CROSSING SNAP & QUICK LOOP BUTTONS
    // =========================================================================

    private void ChkZeroCrossSnap_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        _editor.ZeroCrossingSnap = ChkZeroCrossSnap.IsChecked == true;
        if (_editor.ZeroCrossingSnap && _editor.HasAudio)
        {
            _editor.SetSelectionRange(_editor.SelectionStartFrame, _editor.SelectionEndFrame, applyZeroCrossingSnap: true);
            UpdateAllReadouts();
            RenderWaveform();
        }
    }

    private void BtnNudgeIn_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio || sender is not Button btn || btn.Tag is not string tagStr)
            return;

        if (double.TryParse(tagStr, out double deltaMs))
        {
            int deltaFrames = (int)Math.Round((deltaMs / 1000.0) * _editor.SampleRate);
            _editor.SetSelectionStart(_editor.SelectionStartFrame + deltaFrames, applyZeroCrossingSnap: true);
            UpdateAllReadouts();
            RenderWaveform();
        }
    }

    private void BtnNudgeOut_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio || sender is not Button btn || btn.Tag is not string tagStr)
            return;

        if (double.TryParse(tagStr, out double deltaMs))
        {
            int deltaFrames = (int)Math.Round((deltaMs / 1000.0) * _editor.SampleRate);
            _editor.SetSelectionEnd(_editor.SelectionEndFrame + deltaFrames, applyZeroCrossingSnap: true);
            UpdateAllReadouts();
            RenderWaveform();
        }
    }

    private void BtnSetInAtPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int playFrame = (int)Math.Round(_editor.PlayheadFrame);
        if (playFrame >= _editor.SelectionEndFrame)
        {
            _editor.SetSelectionRange(playFrame, _editor.TotalFrames, applyZeroCrossingSnap: true);
        }
        else
        {
            _editor.SetSelectionStart(playFrame, applyZeroCrossingSnap: true);
        }
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void BtnSetOutAtPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int playFrame = (int)Math.Round(_editor.PlayheadFrame);
        if (playFrame <= _editor.SelectionStartFrame)
        {
            _editor.SetSelectionRange(0, playFrame, applyZeroCrossingSnap: true);
        }
        else
        {
            _editor.SetSelectionEnd(playFrame, applyZeroCrossingSnap: true);
        }
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void BtnHalveLoop_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int halfLen = Math.Max(_editor.SampleRate / 20, _editor.SelectionLengthFrames / 2);
        _editor.SetSelectionEnd(_editor.SelectionStartFrame + halfLen, applyZeroCrossingSnap: true);
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void BtnDoubleLoop_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        int doubleLen = Math.Min(_editor.TotalFrames - _editor.SelectionStartFrame, _editor.SelectionLengthFrames * 2);
        _editor.SetSelectionEnd(_editor.SelectionStartFrame + doubleLen, applyZeroCrossingSnap: true);
        UpdateAllReadouts();
        RenderWaveform();
    }

    private void BtnQuickLoopSec_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio || sender is not Button btn || btn.Tag is not string secStr)
            return;

        if (double.TryParse(secStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            int frames = (int)Math.Round(seconds * _editor.SampleRate);
            int targetEnd = Math.Min(_editor.TotalFrames, _editor.SelectionStartFrame + frames);
            _editor.SetSelectionEnd(targetEnd, applyZeroCrossingSnap: true);
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = $"Selected {FormatTime(_editor.SelectionLengthSeconds)} loop from IN marker. Click '🔁 Loop Selection' to audition!";
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        _editor.SelectAll();
        UpdateAllReadouts();
        RenderWaveform();
    }

    // =========================================================================
    // AUDITION & SEAMLESS LOOP TRANSPORT
    // =========================================================================

    private void BtnLoopSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;

        if (_editor.IsPlaying && _editor.LoopEnabled)
        {
            _editor.StopPlayback();
            TxtEditorStatus.Text = "Loop playback stopped. You can save the selected loop or continue fine-tuning IN/OUT.";
        }
        else
        {
            _editor.StartPlayback(loopSelection: true, playSelectionOnly: true);
            TxtEditorStatus.Text = $"🔁 Looping [{FormatTime(_editor.SelectionStartSeconds)} .. {FormatTime(_editor.SelectionEndSeconds)}] ({FormatTime(_editor.SelectionLengthSeconds)}) — Drag IN/OUT handles or click Fine-Tune buttons live while playing!";
        }

        UpdateLoopButtonVisual();
        RenderWaveform();
    }

    private void BtnPlaySelectionOnce_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        _editor.StartPlayback(loopSelection: false, playSelectionOnly: true);
        UpdateLoopButtonVisual();
        TxtEditorStatus.Text = $"Playing selected region [{FormatTime(_editor.SelectionStartSeconds)} .. {FormatTime(_editor.SelectionEndSeconds)}] once.";
    }

    private void BtnPlayFull_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        _editor.StartPlayback(loopSelection: false, playSelectionOnly: false);
        UpdateLoopButtonVisual();
        TxtEditorStatus.Text = "Playing full track from current playhead.";
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _editor.StopPlayback();
        UpdateLoopButtonVisual();
        RenderWaveform();
        TxtEditorStatus.Text = "Playback stopped.";
    }

    // =========================================================================
    // NON-DESTRUCTIVE AUDIO EDITING (CUT START/END, MIDDLE, FADES, UNDO/REDO)
    // =========================================================================

    private void BtnCropStartAndEnd_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.CropToSelection())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = $"✂ Cut Start & End complete — kept selected {FormatTime(_editor.TotalDurationSeconds)} audio clip (Ctrl+Z to Undo).";
        }
        else
        {
            TxtEditorStatus.Text = "Move the green IN or amber OUT handles inward first to cut the start and/or end.";
        }
    }

    private void BtnCutStartOnly_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.TrimBeforeStart())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = $"✂ Cut start before IN marker — new duration: {FormatTime(_editor.TotalDurationSeconds)} (Ctrl+Z to Undo).";
        }
        else
        {
            TxtEditorStatus.Text = "Move the green IN handle to the point where you want the track to start.";
        }
    }

    private void BtnCutEndOnly_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.TrimAfterEnd())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = $"✂ Cut end after OUT marker — new duration: {FormatTime(_editor.TotalDurationSeconds)} (Ctrl+Z to Undo).";
        }
        else
        {
            TxtEditorStatus.Text = "Move the amber OUT handle left to the point where you want the track to end.";
        }
    }

    private void BtnDeleteMiddle_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.DeleteSelectedRegion())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = $"🗑 Deleted selected middle section with smooth zero-crossing splice — new duration: {FormatTime(_editor.TotalDurationSeconds)}.";
        }
        else
        {
            TxtEditorStatus.Text = "Select a middle subsection between IN and OUT to cut out.";
        }
    }

    private void BtnFadeIn_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.ApplyFadeIn())
        {
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "◢ Applied smooth studio Fade-In (Ctrl+Z to Undo).";
        }
    }

    private void BtnFadeOut_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.ApplyFadeOut())
        {
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "◣ Applied smooth studio Fade-Out (Ctrl+Z to Undo).";
        }
    }

    private void BtnNormalize_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.NormalizeSelection())
        {
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "⚡ Normalized peak level to -0.2 dBFS (Ctrl+Z to Undo).";
        }
        else
        {
            TxtEditorStatus.Text = "Audio is already normalized at optimal peak level.";
        }
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor.Undo())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "↶ Undid last edit.";
        }
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor.Redo())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "↷ Redid edit.";
        }
    }

    private void BtnResetOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;
        if (_editor.ResetToOriginal())
        {
            _viewStartFrame = 0;
            _viewEndFrame = Math.Max(1, _editor.TotalFrames);
            SyncZoomSliderFromViewport();
            UpdateAllReadouts();
            RenderWaveform();
            TxtEditorStatus.Text = "↺ Restored original unedited audio buffer.";
        }
    }

    // =========================================================================
    // LOAD TRACK & SAVE SELECTED LOOP / FULL EDITED AUDIO
    // =========================================================================

    private void BtnLoadTrack_Click(object sender, RoutedEventArgs e)
    {
        PromptAndLoadFile();
    }

    private void BtnSaveSelectedLoopAs_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;

        Directory.CreateDirectory(_defaultSaveFolder);
        string baseName = Path.GetFileNameWithoutExtension(_editor.TrackTitle);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Spectra_Loop";

        string suggestedName = $"{baseName}_Loop_{DateTime.Now:HHmmss}.wav";
        var dlg = new SaveFileDialog
        {
            Title = "Save Selected Loop / Audio Part As Lossless WAV",
            Filter = "Lossless WAV Audio (*.wav)|*.wav",
            InitialDirectory = _defaultSaveFolder,
            FileName = suggestedName,
            DefaultExt = ".wav"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var fmt = GetSelectedSaveBitFormat();
                var clip = _editor.SaveSelectionToWav(dlg.FileName, fmt, "Sample & Loop Studio (Saved Loop)");
                ClipSaved?.Invoke(clip, false);
                TxtEditorStatus.Text = $"💾 Saved selected loop '{clip.FileName}' ({clip.DurationLabel} • {clip.FormatLabel}) and added to Sample Deck!";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save selected loop:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnSaveFullTrackAs_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio) return;

        Directory.CreateDirectory(_defaultSaveFolder);
        string baseName = Path.GetFileNameWithoutExtension(_editor.TrackTitle);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Spectra_Edited";

        string suggestedName = $"{baseName}_Edited_{DateTime.Now:HHmmss}.wav";
        var dlg = new SaveFileDialog
        {
            Title = "Save Full Edited Audio Track As Lossless WAV",
            Filter = "Lossless WAV Audio (*.wav)|*.wav",
            InitialDirectory = _defaultSaveFolder,
            FileName = suggestedName,
            DefaultExt = ".wav"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var fmt = GetSelectedSaveBitFormat();
                var clip = _editor.SaveFullBufferToWav(dlg.FileName, fmt, "Sample & Loop Studio (Edited Track)");
                ClipSaved?.Invoke(clip, false);
                TxtEditorStatus.Text = $"💾 Saved full edited track '{clip.FileName}' ({clip.DurationLabel} • {clip.FormatLabel}) and added to Sample Deck!";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save edited track:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnOverwriteCurrentWav_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasAudio || string.IsNullOrWhiteSpace(_editor.CurrentFilePath))
            return;

        if (!string.Equals(Path.GetExtension(_editor.CurrentFilePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            BtnSaveFullTrackAs_Click(sender, e);
            return;
        }

        try
        {
            _editor.StopPlayback();
            var fmt = GetSelectedSaveBitFormat();
            var clip = _editor.SaveFullBufferToWav(_editor.CurrentFilePath, fmt, "Sample & Loop Studio (Edited)");
            ClipSaved?.Invoke(clip, true);
            UpdateAllReadouts();
            TxtEditorStatus.Text = $"💾 Overwrote '{clip.FileName}' ({clip.DurationLabel} • {clip.FormatLabel}) cleanly on disk!";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to overwrite WAV file:\n{ex.Message}", "Overwrite Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_editor.HasAudio)
            return;

        if (e.Key == Key.Space || e.Key == Key.L)
        {
            BtnLoopSelection_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.None)
        {
            BtnSetInAtPlayhead_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None)
        {
            BtnSetOutAtPlayhead_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            BtnUndo_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            BtnRedo_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            BtnSelectAll_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        _editor.Dispose();
    }
}
