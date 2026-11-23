using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
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
    private DispatcherTimer? _recorderTimer;
    private WriteableBitmap? _recWaveBitmap;
    private uint[] _recWavePixels = Array.Empty<uint>();
    private int _recWaveWidth;
    private int _recWaveHeight;
    private bool _recIsActiveTab;
    private bool _recWasWaitingUiState;
    private string _recPendingFileName = string.Empty;

    private readonly ObservableCollection<RecordedClipInfo> _recentRecordings = new();
    private SoundPlayer? _clipPlayer;

    private const uint RecBgColor = 0xFF070A14u;
    private const uint RecGridColor = 0xFF161F3Bu;
    private const uint RecMidLineColor = 0xFF283968u;

    private void InitializeRecorder()
    {
        string folder = _appSettings.RecordingOutputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "SpectraRecordings");
        }
        TxtRecFolder.Text = folder;
        GridRecentRecordings.ItemsSource = _recentRecordings;

        _studio.Recorder.SetInputGainDb(SldRecGain.Value);
        _studio.Recorder.SetSoftLimiter(ChkRecSoftLimiter.IsChecked == true);

        _recorderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS responsive meters & waveform
        };
        _recorderTimer.Tick += RecorderTimer_Tick;
    }

    private void RefreshRecorderSources()
    {
        if (CmbRecSource == null)
            return;

        string? prevId = (CmbRecSource.SelectedItem as AudioRecordingSource)?.EndpointId;
        bool? prevLoopback = (CmbRecSource.SelectedItem as AudioRecordingSource)?.IsLoopback;

        var sources = new List<AudioRecordingSource>();

        // 1. "What You Hear" (WASAPI Loopback) on all active playback devices — best for DJs & sample grabbing!
        var activePlayback = _allEndpoints
            .Where(e => e.DataFlow == AudioEndpointFlow.Render && e.IsActive)
            .OrderByDescending(e => e.IsDefaultMultimedia)
            .ThenByDescending(e => e.IsRealtek)
            .ThenBy(e => e.FriendlyName);

        foreach (var ep in activePlayback)
        {
            sources.Add(new AudioRecordingSource
            {
                EndpointId = ep.EndpointId,
                FriendlyName = ep.FriendlyName,
                IsLoopback = true,
                IsDefault = ep.IsDefaultMultimedia
            });
        }

        // 2. All active hardware capture inputs (Line In, Microphone, Stereo Mix, USB audio interfaces, etc.)
        var activeCapture = _allEndpoints
            .Where(e => e.DataFlow == AudioEndpointFlow.Capture && e.IsActive)
            .OrderByDescending(e => e.IsDefaultMultimedia)
            .ThenByDescending(e => e.IsRealtek)
            .ThenBy(e => e.FriendlyName);

        foreach (var ep in activeCapture)
        {
            sources.Add(new AudioRecordingSource
            {
                EndpointId = ep.EndpointId,
                FriendlyName = ep.FriendlyName,
                IsLoopback = false,
                IsDefault = ep.IsDefaultMultimedia
            });
        }

        bool prevSup = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            CmbRecSource.ItemsSource = sources;
            AudioRecordingSource? match = null;
            if (!string.IsNullOrWhiteSpace(prevId))
            {
                match = sources.FirstOrDefault(s =>
                    string.Equals(s.EndpointId, prevId, StringComparison.OrdinalIgnoreCase) &&
                    (!prevLoopback.HasValue || s.IsLoopback == prevLoopback.Value));
            }
            CmbRecSource.SelectedItem = match ?? sources.FirstOrDefault();
        }
        finally
        {
            _suppressUiEvents = prevSup;
        }

        if (_recIsActiveTab && !_studio.Recorder.IsRecording && WindowState != WindowState.Minimized && IsVisible)
        {
            StartRecorderMonitoringForSelectedSource();
        }
    }

    private void OnRecorderTabSelectionChanged(bool isRecordTab)
    {
        if (isRecordTab == _recIsActiveTab)
            return;

        _recIsActiveTab = isRecordTab;
        if (isRecordTab)
        {
            EnsureRecWaveformBitmap();
            if (!_studio.Recorder.IsRecording)
            {
                StartRecorderMonitoringForSelectedSource();
            }
            _recorderTimer?.Start();
        }
        else
        {
            // Zero-CPU idle when leaving Tab 8, UNLESS actively recording (recording keeps running seamlessly!)
            if (!_studio.Recorder.IsRecording)
            {
                _recorderTimer?.Stop();
                _studio.Recorder.StopMonitoring();
            }
        }
    }

    private void OnRecorderWindowMinimized()
    {
        if (!_studio.Recorder.IsRecording)
        {
            _recorderTimer?.Stop();
            _studio.Recorder.StopMonitoring();
        }
    }

    private void OnRecorderWindowRestored()
    {
        if (_recIsActiveTab || _studio.Recorder.IsRecording)
        {
            if (_recIsActiveTab && !_studio.Recorder.IsRecording)
            {
                StartRecorderMonitoringForSelectedSource();
            }
            _recorderTimer?.Start();
        }
    }

    private void ShutdownRecorderOnAppClose()
    {
        _recorderTimer?.Stop();
        StopClipPlayback();
        if (_studio.Recorder.IsRecording)
        {
            _studio.Recorder.StopRecording(keepMonitoring: false);
        }
        else
        {
            _studio.Recorder.StopMonitoring();
        }
    }

    private void StartRecorderMonitoringForSelectedSource()
    {
        if (CmbRecSource?.SelectedItem is not AudioRecordingSource src)
            return;

        _studio.Recorder.StartMonitoring(src.EndpointId, src.IsLoopback, src.DisplayLabel);
    }

    private void CmbRecSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _studio.Recorder.IsRecording)
            return;

        if (_recIsActiveTab && IsVisible && WindowState != WindowState.Minimized)
        {
            StartRecorderMonitoringForSelectedSource();
        }
    }

    private void BtnRecRefreshSources_Click(object sender, RoutedEventArgs e)
    {
        _allEndpoints = _studio.DiscoverEndpoints(true);
        RefreshRecorderSources();
        SetStatus($"Refreshed recording audio sources ({CmbRecSource.Items.Count} available).");
    }

    private void SldRecGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtRecGainDb == null)
            return;

        double db = Math.Round(SldRecGain.Value, 1);
        TxtRecGainDb.Text = $"{db:+0.0;-0.0;0.0} dB";
        _studio.Recorder.SetInputGainDb(db);
    }

    private void ChkRecSoftLimiter_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkRecSoftLimiter == null)
            return;

        _studio.Recorder.SetSoftLimiter(ChkRecSoftLimiter.IsChecked == true);
    }

    private void ChkRecWaitForSound_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkRecWaitForSound == null || CmbRecTriggerDb == null || BtnRecStart == null)
            return;

        bool waitMode = ChkRecWaitForSound.IsChecked == true;
        CmbRecTriggerDb.IsEnabled = waitMode;
        if (!_studio.Recorder.IsRecording)
        {
            BtnRecStart.Content = waitMode ? "◉ Arm Record" : "● Record";
        }
    }

    private void CmbRecTriggerDb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbRecTriggerDb == null)
            return;

        double db = GetSelectedRecTriggerDb();
        _studio.Recorder.SetTriggerThresholdDb(db);
    }

    private double GetSelectedRecTriggerDb()
    {
        string tag = GetSelectedComboTag(CmbRecTriggerDb, "-40");
        return double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double db)
            ? Math.Clamp(db, -60.0, -6.0)
            : -40.0;
    }

    private void BtnRecBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose folder to save Spectra Realtek Studio WAV recordings",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(TxtRecFolder.Text) ? TxtRecFolder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            TxtRecFolder.Text = dlg.SelectedPath;
            _appSettings.RecordingOutputFolder = dlg.SelectedPath;
            _studio.Presets.SaveAppSettings(_appSettings);
            SetStatus($"Recording folder set to {dlg.SelectedPath}");
        }
    }

    private void BtnRecOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = GetRecordingOutputFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open recordings folder: {ex.Message}");
        }
    }

    private string GetRecordingOutputFolder()
    {
        string folder = TxtRecFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "SpectraRecordings");
            TxtRecFolder.Text = folder;
        }
        return folder;
    }

    private RecordingBitFormat GetSelectedRecFormat()
    {
        string tag = GetSelectedComboTag(CmbRecFormat, "24");
        return tag switch
        {
            "16" => RecordingBitFormat.Pcm16,
            "32" => RecordingBitFormat.Float32,
            _ => RecordingBitFormat.Pcm24
        };
    }

    private void BtnRecStart_Click(object sender, RoutedEventArgs e)
    {
        if (_studio.Recorder.IsRecording)
            return;

        if (CmbRecSource.SelectedItem is not AudioRecordingSource src)
        {
            SetStatus("Please select an audio source first.");
            return;
        }

        string folder = GetRecordingOutputFolder();
        _appSettings.RecordingOutputFolder = folder;
        _studio.Presets.SaveAppSettings(_appSettings);

        string rawPrefix = TxtRecPrefix.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawPrefix))
            rawPrefix = "Spectra_Sample";
        foreach (char c in Path.GetInvalidFileNameChars())
            rawPrefix = rawPrefix.Replace(c, '_');

        string fileName = $"{rawPrefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav";
        string fullPath = Path.Combine(folder, fileName);
        var fmt = GetSelectedRecFormat();
        bool waitForSound = ChkRecWaitForSound.IsChecked == true;
        double triggerDb = GetSelectedRecTriggerDb();

        _studio.Recorder.SetInputGainDb(SldRecGain.Value);
        _studio.Recorder.SetSoftLimiter(ChkRecSoftLimiter.IsChecked == true);

        if (!_studio.Recorder.StartRecording(src.EndpointId, src.IsLoopback, src.DisplayLabel, fullPath, fmt, out string err, waitForSound, triggerDb))
        {
            SetStatus($"Recording could not start: {err}");
            return;
        }

        _recPendingFileName = fileName;
        _recWasWaitingUiState = waitForSound;

        BtnRecStart.IsEnabled = false;
        BtnRecPause.IsEnabled = !waitForSound;
        BtnRecPause.Content = "❚❚ Pause";
        BtnRecStop.IsEnabled = true;
        BtnRecStop.Content = waitForSound ? "■ Cancel Arm" : "■ Stop & Save";
        BtnRecCancel.IsEnabled = true;
        BtnRecRestart.IsEnabled = true;
        CmbRecSource.IsEnabled = false;
        CmbRecFormat.IsEnabled = false;
        ChkRecWaitForSound.IsEnabled = false;

        if (waitForSound)
        {
            TxtRecStateDot.Text = "◉ ARMED";
            TxtRecStateDot.Foreground = (Brush)FindResource("Amber");
            TxtRecWaveHint.Visibility = Visibility.Visible;
            TxtRecWaveHint.Text = $"◉ ARMED — WAITING FOR FIRST SOUND (> {triggerDb:0} dB)... Play audio to start recording automatically!";
            TxtRecWaveHint.Foreground = (Brush)FindResource("Amber");
            TxtRecLiveBadge.Text = $"◉ REC ARMED (> {triggerDb:0} dB)";
            TxtRecLiveBadge.Foreground = (Brush)FindResource("Amber");
            TxtRecLiveBadge.Visibility = Visibility.Visible;
            SetStatus($"Recorder ARMED ({src.DisplayLabel}) — waiting for first sound above {triggerDb:0} dB (30 ms pre-roll active)...");
        }
        else
        {
            TxtRecStateDot.Text = "● RECORDING";
            TxtRecStateDot.Foreground = (Brush)FindResource("Red");
            TxtRecWaveHint.Visibility = Visibility.Visible;
            TxtRecWaveHint.Text = $"● RECORDING LIVE → {fileName}";
            TxtRecWaveHint.Foreground = (Brush)FindResource("Red");
            TxtRecLiveBadge.Visibility = Visibility.Visible;
            SetStatus($"Recording started ({src.DisplayLabel}) → {fileName}");
        }

        _recorderTimer?.Start();
    }

    private void BtnRecPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_studio.Recorder.IsRecording || _studio.Recorder.IsWaitingForSound)
            return;

        _studio.Recorder.TogglePause();
        bool paused = _studio.Recorder.IsPaused;
        BtnRecPause.Content = paused ? "▶ Resume" : "❚❚ Pause";
        TxtRecStateDot.Text = paused ? "❚❚ PAUSED" : "● RECORDING";
        TxtRecStateDot.Foreground = paused ? (Brush)FindResource("Amber") : (Brush)FindResource("Red");
        SetStatus(paused ? "Recording paused." : "Recording resumed.");
    }

    private void BtnRecStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_studio.Recorder.IsRecording)
            return;

        bool wasStillWaiting = _studio.Recorder.IsWaitingForSound;
        var clip = _studio.Recorder.StopRecording(keepMonitoring: _recIsActiveTab);

        ResetRecorderUiToStandby();

        if (clip != null)
        {
            _recentRecordings.Insert(0, clip);
            GridRecentRecordings.SelectedItem = clip;
            SetStatus($"Saved recording: {clip.FileName} ({clip.DurationLabel}, {clip.SizeLabel})");
        }
        else if (wasStillWaiting)
        {
            SetStatus("Armed recording cancelled before any sound triggered (no empty file created).");
        }
        else
        {
            SetStatus("Recording stopped.");
        }
    }

    private void BtnRecCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_studio.Recorder.IsRecording)
            return;

        _studio.Recorder.CancelRecording(keepMonitoring: _recIsActiveTab);
        ResetRecorderUiToStandby();
        TxtRecTimer.Text = "00:00.0";
        TxtRecSize.Text = "0.00 MB";
        SetStatus("Recording cancelled and discarded cleanly (no file saved).");
    }

    private void BtnRecRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_studio.Recorder.IsRecording)
        {
            _studio.Recorder.CancelRecording(keepMonitoring: true);
            ResetRecorderUiToStandby();
        }
        TxtRecTimer.Text = "00:00.0";
        TxtRecSize.Text = "0.00 MB";
        BtnRecStart_Click(sender, e);
    }

    private void ResetRecorderUiToStandby()
    {
        _recWasWaitingUiState = false;
        bool waitMode = ChkRecWaitForSound.IsChecked == true;
        BtnRecStart.IsEnabled = true;
        BtnRecStart.Content = waitMode ? "◉ Arm Record" : "● Record";
        BtnRecPause.IsEnabled = false;
        BtnRecPause.Content = "❚❚ Pause";
        BtnRecStop.IsEnabled = false;
        BtnRecStop.Content = "■ Stop & Save";
        BtnRecCancel.IsEnabled = false;
        BtnRecRestart.IsEnabled = false;
        CmbRecSource.IsEnabled = true;
        CmbRecFormat.IsEnabled = true;
        ChkRecWaitForSound.IsEnabled = true;

        TxtRecStateDot.Text = "● STANDBY";
        TxtRecStateDot.Foreground = (Brush)FindResource("Muted");
        TxtRecWaveHint.Text = "LIVE INPUT MONITOR & RECORDING WAVEFORM — Select '🔊 What You Hear' above and click ● Record to grab DJ mixes or samples!";
        TxtRecWaveHint.Foreground = new SolidColorBrush(Color.FromRgb(86, 105, 156));
        TxtRecWaveHint.Visibility = _appSettings.ShowIdleHints ? Visibility.Visible : Visibility.Collapsed;
        TxtRecLiveBadge.Visibility = Visibility.Collapsed;
        TxtRecLiveBadge.Text = string.Empty;
    }

    private void BtnRecPlayClip_Click(object sender, RoutedEventArgs e)
    {
        PlaySelectedRecordedClip();
    }

    private void BtnRecEditClip_Click(object sender, RoutedEventArgs e)
    {
        StopClipPlayback();

        RecordedClipInfo? clip = GridRecentRecordings.SelectedItem as RecordedClipInfo;
        if (clip == null && _recentRecordings.Count > 0)
        {
            clip = _recentRecordings[0];
            GridRecentRecordings.SelectedItem = clip;
        }

        if (clip != null && File.Exists(clip.FilePath))
        {
            OpenSampleEditorWindow(clip.FilePath, promptForFileIfEmpty: false);
        }
        else
        {
            OpenSampleEditorWindow(initialFilePath: null, promptForFileIfEmpty: true);
        }
    }

    private void BtnRecLoadAndEditTrack_Click(object sender, RoutedEventArgs e)
    {
        StopClipPlayback();
        OpenSampleEditorWindow(initialFilePath: null, promptForFileIfEmpty: true);
    }

    private void OpenSampleEditorWindow(string? initialFilePath, bool promptForFileIfEmpty)
    {
        string saveFolder = TxtRecFolder?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            saveFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "SpectraRecordings");
        }

        string? playbackEndpointId = _activePlaybackEndpoint?.EndpointId;

        var editorWin = new AudioSampleEditorWindow(initialFilePath, saveFolder, playbackEndpointId)
        {
            Owner = this
        };

        editorWin.ClipSaved += (savedClip, isOverwrite) =>
        {
            Dispatcher.Invoke(() =>
            {
                int existingIdx = -1;
                for (int i = 0; i < _recentRecordings.Count; i++)
                {
                    if (string.Equals(_recentRecordings[i].FilePath, savedClip.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIdx = i;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    _recentRecordings[existingIdx] = savedClip;
                }
                else
                {
                    _recentRecordings.Insert(0, savedClip);
                }

                GridRecentRecordings.SelectedItem = savedClip;
                SetStatus(isOverwrite
                    ? $"Updated '{savedClip.FileName}' ({savedClip.DurationLabel} • {savedClip.FormatLabel}) in Sample Studio."
                    : $"Saved new sample/loop '{savedClip.FileName}' ({savedClip.DurationLabel} • {savedClip.FormatLabel}) to Sample Deck.");
            });
        };

        editorWin.Show();

        if (promptForFileIfEmpty && string.IsNullOrWhiteSpace(initialFilePath))
        {
            editorWin.PromptAndLoadFile();
        }
    }

    private void GridRecentRecordings_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        PlaySelectedRecordedClip();
    }

    private void PlaySelectedRecordedClip()
    {
        if (GridRecentRecordings.SelectedItem is not RecordedClipInfo clip)
        {
            SetStatus("Select a recorded WAV file from the list first.");
            return;
        }

        if (!File.Exists(clip.FilePath))
        {
            SetStatus($"File not found: {clip.FilePath}");
            return;
        }

        try
        {
            StopClipPlayback();
            if (clip.FormatLabel.Contains("32-bit Float", StringComparison.OrdinalIgnoreCase) ||
                clip.FormatLabel.Contains("24-bit", StringComparison.OrdinalIgnoreCase))
            {
                // SoundPlayer natively plays 16-bit PCM; for 24-bit/32-bit float studio WAVs launch default Windows audio player
                Process.Start(new ProcessStartInfo(clip.FilePath) { UseShellExecute = true });
                SetStatus($"Opened {clip.FileName} in your system audio player (24-bit / 32-bit Float studio WAV).");
            }
            else
            {
                _clipPlayer = new SoundPlayer(clip.FilePath);
                _clipPlayer.Play();
                SetStatus($"Playing {clip.FileName} ({clip.DurationLabel})...");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not play {clip.FileName}: {ex.Message}");
        }
    }

    private void BtnRecStopPlay_Click(object sender, RoutedEventArgs e)
    {
        StopClipPlayback();
        _studio.TestTones.Stop();
        SetStatus("Stopped clip playback.");
    }

    private void StopClipPlayback()
    {
        try
        {
            _clipPlayer?.Stop();
            _clipPlayer?.Dispose();
        }
        catch
        {
        }
        _clipPlayer = null;
    }

    private void RecWaveformViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureRecWaveformBitmap();
    }

    private void EnsureRecWaveformBitmap()
    {
        if (RecWaveformViewport == null || ImgRecWave == null)
            return;

        int w = Math.Clamp((int)Math.Round(RecWaveformViewport.ActualWidth), 320, 1920);
        int h = Math.Clamp((int)Math.Round(RecWaveformViewport.ActualHeight), 80, 420);
        if (w == _recWaveWidth && h == _recWaveHeight && _recWaveBitmap != null)
            return;

        _recWaveWidth = w;
        _recWaveHeight = h;
        _recWavePixels = new uint[w * h];
        Array.Fill(_recWavePixels, RecBgColor);
        _recWaveBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        ImgRecWave.Source = _recWaveBitmap;
        FlushRecWaveBitmap();
    }

    private void FlushRecWaveBitmap()
    {
        if (_recWaveBitmap == null || _recWavePixels.Length != _recWaveWidth * _recWaveHeight)
            return;

        _recWaveBitmap.WritePixels(
            new Int32Rect(0, 0, _recWaveWidth, _recWaveHeight),
            _recWavePixels,
            _recWaveWidth * 4,
            0);
    }

    private void RecorderTimer_Tick(object? sender, EventArgs e)
    {
        var snap = _studio.Recorder.GetSnapshot();

        // Detect instant auto-transition from ARMED (Waiting for Sound) -> LIVE RECORDING!
        if (_recWasWaitingUiState && snap.IsRecording && !snap.IsWaitingForSound)
        {
            _recWasWaitingUiState = false;
            BtnRecPause.IsEnabled = true;
            BtnRecStop.Content = "■ Stop & Save";
            TxtRecStateDot.Text = "● RECORDING";
            TxtRecStateDot.Foreground = (Brush)FindResource("Red");
            TxtRecWaveHint.Text = $"● RECORDING LIVE → {_recPendingFileName}";
            TxtRecWaveHint.Foreground = (Brush)FindResource("Red");
            SetStatus($"Sound detected — recording started automatically → {_recPendingFileName}");
        }

        // Update global bottom status bar recording indicator even if user switched to Tab 1/2/7!
        if (snap.IsRecording)
        {
            if (snap.IsWaitingForSound)
            {
                TxtRecLiveBadge.Text = $"◉ REC ARMED (> {snap.TriggerThresholdDb:0} dB)";
                TxtRecLiveBadge.Foreground = (Brush)FindResource("Amber");
            }
            else
            {
                string dur = $"{(int)snap.Elapsed.TotalMinutes:00}:{snap.Elapsed.Seconds:00}.{snap.Elapsed.Milliseconds / 100}";
                TxtRecLiveBadge.Text = snap.IsPaused ? $"❚❚ REC PAUSED {dur}" : $"● REC {dur}";
                TxtRecLiveBadge.Foreground = snap.IsPaused ? (Brush)FindResource("Amber") : (Brush)FindResource("Red");
            }
            TxtRecLiveBadge.Visibility = Visibility.Visible;
        }
        else if (TxtRecLiveBadge.Visibility == Visibility.Visible)
        {
            TxtRecLiveBadge.Visibility = Visibility.Collapsed;
        }

        if (!_recIsActiveTab || WindowState == WindowState.Minimized || !IsVisible)
            return;

        TxtRecTimer.Text = $"{(int)snap.Elapsed.TotalMinutes:00}:{snap.Elapsed.Seconds:00}.{snap.Elapsed.Milliseconds / 100}";
        TxtRecSize.Text = $"{snap.BytesWritten / (1024.0 * 1024.0):0.00} MB";
        TxtRecFormatInfo.Text = snap.StatusMessage;

        double pctL = Math.Clamp(snap.PeakLeft * 100.0, 0.0, 100.0);
        double pctR = Math.Clamp(snap.PeakRight * 100.0, 0.0, 100.0);
        PbRecPeakL.Value = pctL;
        PbRecPeakR.Value = pctR;

        if (snap.ClippedRecently)
        {
            TxtRecClipBadge.Text = "CLIP";
            TxtRecClipBadge.Foreground = (Brush)FindResource("Red");
        }
        else if (Math.Max(pctL, pctR) > 1.0)
        {
            TxtRecClipBadge.Text = "OK";
            TxtRecClipBadge.Foreground = (Brush)FindResource("Green");
        }
        else
        {
            TxtRecClipBadge.Text = "IDLE";
            TxtRecClipBadge.Foreground = (Brush)FindResource("Muted");
        }

        RenderRecorderWaveform(snap);
    }

    private void RenderRecorderWaveform(RecorderMonitorSnapshot snap)
    {
        if (_recWaveBitmap == null || _recWaveWidth <= 0 || _recWaveHeight <= 0)
        {
            EnsureRecWaveformBitmap();
            if (_recWaveBitmap == null)
                return;
        }

        int w = _recWaveWidth;
        int h = _recWaveHeight;
        Array.Fill(_recWavePixels, RecBgColor);

        int midY = h / 2;
        int maxHalfH = Math.Max(16, midY - 12);

        // Subtle horizontal reference grid
        int q1 = midY - maxHalfH / 2;
        int q3 = midY + maxHalfH / 2;
        for (int x = 4; x < w - 4; x++)
        {
            _recWavePixels[q1 * w + x] = RecGridColor;
            _recWavePixels[midY * w + x] = RecMidLineColor;
            _recWavePixels[q3 * w + x] = RecGridColor;
        }

        // While ARMED (Wait for Sound), draw subtle dashed Amber threshold trigger lines so user sees exact trigger level!
        if (snap.IsRecording && snap.IsWaitingForSound)
        {
            double threshLin = Math.Pow(10.0, snap.TriggerThresholdDb / 20.0);
            int thOff = Math.Max(3, (int)Math.Round(threshLin * maxHalfH));
            int tyUp = Math.Clamp(midY - thOff, 18, midY - 2);
            int tyDn = Math.Clamp(midY + thOff, midY + 2, h - 5);
            const uint amberDash = 0xFF8C6318u;
            for (int x = 4; x < w - 4; x++)
            {
                if ((x & 7) < 4)
                {
                    _recWavePixels[tyUp * w + x] = amberDash;
                    _recWavePixels[tyDn * w + x] = amberDash;
                }
            }
        }

        var wave = snap.WaveformPeaks;
        int n = wave.Length;
        if (n == 0)
        {
            FlushRecWaveBitmap();
            return;
        }

        double stepX = (double)(w - 8) / n;
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(wave[i], 0.0, 1.0);
            int halfH = (int)Math.Round(v * maxHalfH);
            if (halfH <= 0)
                continue;

            int x0 = 4 + (int)Math.Round(i * stepX);
            int x1 = 4 + (int)Math.Round((i + 1) * stepX) - 1;
            if (x1 <= x0) x1 = x0 + 1;
            if (x1 >= w) x1 = w - 1;

            // Amber while armed & waiting, Crimson/Hot Magenta while recording, Cyan/Violet while monitoring standby
            uint col = snap.IsRecording
                ? (snap.IsWaitingForSound || snap.IsPaused ? 0xFFFFB020u : (v > 0.85 ? 0xFFFF4D6Du : 0xFFFF2BD6u))
                : (v > 0.85 ? 0xFFFF2BD6u : 0xFF22E8FFu);
            uint dimCol = ScaleColor(col, 0.55);

            for (int dy = 0; dy < halfH; dy++)
            {
                int yUp = midY - dy;
                int yDn = midY + dy;
                uint c = dy > halfH * 0.7 ? col : dimCol;
                if (yUp >= 18)
                {
                    int offUp = yUp * w;
                    for (int x = x0; x < x1; x++) _recWavePixels[offUp + x] = c;
                }
                if (yDn < h - 4)
                {
                    int offDn = yDn * w;
                    for (int x = x0; x < x1; x++) _recWavePixels[offDn + x] = c;
                }
            }
        }

        FlushRecWaveBitmap();
    }
}
