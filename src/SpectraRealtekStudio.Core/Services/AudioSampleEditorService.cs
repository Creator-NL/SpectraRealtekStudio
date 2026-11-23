using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SpectraRealtekStudio.Core.Interop;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

public sealed class AudioEditorBufferSnapshot
{
    public required float[] Left { get; init; }
    public required float[] Right { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionEnd { get; init; }
    public required string ActionLabel { get; init; }
}

/// <summary>
/// Bit-pure native Studio Audio Sample &amp; Loop Editor Engine.
/// Loads WAV, FLAC, MP3, M4A, AAC, OGG, WMA, and MP4 tracks at 100% original sample rate (up to 192 kHz),
/// supports zero-crossing loop snapping, real-time gapless WASAPI loop auditioning while live-adjusting IN/OUT markers,
/// non-destructive start/end trimming, middle cuts, fades, normalization, undo/redo, and lossless 16/24/32f WAV export.
/// </summary>
public sealed class AudioSampleEditorService : IDisposable
{
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _bufferLock = new();
    private readonly Stack<AudioEditorBufferSnapshot> _undoStack = new();
    private readonly Stack<AudioEditorBufferSnapshot> _redoStack = new();

    private float[] _originalLeft = Array.Empty<float>();
    private float[] _originalRight = Array.Empty<float>();
    private float[] _left = Array.Empty<float>();
    private float[] _right = Array.Empty<float>();
    private int _sampleRate = 48000;
    private RecordingBitFormat _detectedBitFormat = RecordingBitFormat.Pcm24;
    private string _currentFilePath = string.Empty;
    private string _trackTitle = "Untitled Sample";

    private int _selectionStartFrame;
    private int _selectionEndFrame;
    private double _playheadFrame;
    private bool _zeroCrossingSnap = true;

    private volatile bool _isPlaying;
    private volatile bool _loopEnabled;
    private volatile bool _playSelectionOnly;
    private volatile bool _stopThreadRequested;
    private Thread? _renderThread;
    private string? _endpointId;
    private int _outputSampleRate = 48000;
    private float _peakL;
    private float _peakR;

    public string CurrentFilePath => _currentFilePath;
    public string TrackTitle => _trackTitle;
    public int SampleRate => _sampleRate;
    public int TotalFrames => _left.Length;
    public double TotalDurationSeconds => _sampleRate > 0 ? (double)_left.Length / _sampleRate : 0.0;
    public RecordingBitFormat DetectedBitFormat => _detectedBitFormat;

    public int SelectionStartFrame => _selectionStartFrame;
    public int SelectionEndFrame => _selectionEndFrame;
    public int SelectionLengthFrames => Math.Max(0, _selectionEndFrame - _selectionStartFrame);
    public double SelectionStartSeconds => _sampleRate > 0 ? (double)_selectionStartFrame / _sampleRate : 0.0;
    public double SelectionEndSeconds => _sampleRate > 0 ? (double)_selectionEndFrame / _sampleRate : 0.0;
    public double SelectionLengthSeconds => _sampleRate > 0 ? (double)SelectionLengthFrames / _sampleRate : 0.0;

    public double PlayheadFrame
    {
        get => _playheadFrame;
        set
        {
            lock (_bufferLock)
            {
                _playheadFrame = Math.Clamp(value, 0.0, Math.Max(0, _left.Length - 1));
            }
        }
    }

    public double PlayheadSeconds => _sampleRate > 0 ? _playheadFrame / _sampleRate : 0.0;

    public bool ZeroCrossingSnap
    {
        get => _zeroCrossingSnap;
        set => _zeroCrossingSnap = value;
    }

    public bool IsPlaying => _isPlaying;

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set => _loopEnabled = value;
    }

    public float PeakLeft => _peakL;
    public float PeakRight => _peakR;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public bool HasAudio => _left.Length > 0;

    public void SetPlaybackEndpoint(string? endpointId)
    {
        _endpointId = endpointId;
    }

    /// <summary>
    /// Loads any supported audio file (.wav, .flac, .mp3, .m4a, .aac, .ogg, .wma, .mp4) at 100% original sample rate.
    /// </summary>
    public void LoadFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found.", filePath);

        StopPlayback();

        string ext = Path.GetExtension(filePath);
        float[] left;
        float[] right;
        int sampleRate;

        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
            && DjTrackDecoderService.TryDecodeWavNative(filePath, out left, out right, out sampleRate, preserveNativeSampleRate: true))
        {
            _detectedBitFormat = DjTrackDecoderService.DetectWavBitFormat(filePath);
        }
        else
        {
            DjTrackDecoderService.DecodeViaMediaFoundation(filePath, out left, out right, out sampleRate, preserveNativeSampleRate: true);
            _detectedBitFormat = RecordingBitFormat.Pcm24;
        }

        int frames = Math.Min(left.Length, right.Length);
        if (frames <= 0)
            throw new InvalidOperationException("Decoded audio stream contains zero samples.");

        if (left.Length != frames) Array.Resize(ref left, frames);
        if (right.Length != frames) Array.Resize(ref right, frames);

        lock (_bufferLock)
        {
            _currentFilePath = filePath;
            _trackTitle = Path.GetFileName(filePath);
            _sampleRate = Math.Max(8000, sampleRate);
            _originalLeft = (float[])left.Clone();
            _originalRight = (float[])right.Clone();
            _left = left;
            _right = right;
            _selectionStartFrame = 0;
            _selectionEndFrame = frames;
            _playheadFrame = 0;
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    /// <summary>
    /// Loads an in-memory stereo buffer directly (used for unit tests or synthetic buffers).
    /// </summary>
    public void LoadRawBuffer(float[] left, float[] right, int sampleRate, string title = "In-Memory Sample", RecordingBitFormat bitFormat = RecordingBitFormat.Pcm24)
    {
        StopPlayback();
        int frames = Math.Min(left.Length, right.Length);
        if (frames <= 0)
            throw new ArgumentException("Audio buffer cannot be empty.");

        float[] lCopy = new float[frames];
        float[] rCopy = new float[frames];
        Array.Copy(left, lCopy, frames);
        Array.Copy(right, rCopy, frames);

        lock (_bufferLock)
        {
            _currentFilePath = string.Empty;
            _trackTitle = title;
            _sampleRate = Math.Max(8000, sampleRate);
            _detectedBitFormat = bitFormat;
            _originalLeft = (float[])lCopy.Clone();
            _originalRight = (float[])rCopy.Clone();
            _left = lCopy;
            _right = rCopy;
            _selectionStartFrame = 0;
            _selectionEndFrame = frames;
            _playheadFrame = 0;
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    public void GetBuffers(out float[] left, out float[] right, out int sampleRate)
    {
        lock (_bufferLock)
        {
            left = _left;
            right = _right;
            sampleRate = _sampleRate;
        }
    }

    // =========================================================================
    // SELECTION & ZERO-CROSSING SNAP FOR CLICK-FREE SEAMLESS LOOPS
    // =========================================================================

    public void SetSelectionRange(int startFrame, int endFrame, bool applyZeroCrossingSnap = true)
    {
        lock (_bufferLock)
        {
            int total = _left.Length;
            if (total <= 0)
            {
                _selectionStartFrame = 0;
                _selectionEndFrame = 0;
                return;
            }

            int s = Math.Clamp(Math.Min(startFrame, endFrame), 0, total - 1);
            int e = Math.Clamp(Math.Max(startFrame, endFrame), s + 1, total);

            if (applyZeroCrossingSnap && _zeroCrossingSnap && total > 64)
            {
                int radius = Math.Clamp(_sampleRate / 100, 32, 2400); // ~10 ms search radius
                if (s > 0 && s < total - 1)
                    s = SnapToNearestZeroCrossingLocked(s, radius);
                if (e > s + 16 && e < total)
                    e = SnapToNearestZeroCrossingLocked(e, radius);
                if (e <= s)
                    e = Math.Min(total, s + 1);
            }

            _selectionStartFrame = s;
            _selectionEndFrame = e;
        }
    }

    public void SetSelectionStart(int startFrame, bool applyZeroCrossingSnap = true)
    {
        lock (_bufferLock)
        {
            int total = _left.Length;
            if (total <= 1) return;

            int maxStart = Math.Max(0, _selectionEndFrame - Math.Max(16, _sampleRate / 1000));
            int s = Math.Clamp(startFrame, 0, maxStart);
            if (applyZeroCrossingSnap && _zeroCrossingSnap && s > 0 && s < total - 1)
            {
                int radius = Math.Clamp(_sampleRate / 120, 24, 1800);
                int snapped = SnapToNearestZeroCrossingLocked(s, radius);
                if (snapped < _selectionEndFrame - 8)
                    s = snapped;
            }
            _selectionStartFrame = s;
            if (_isPlaying && _playSelectionOnly && _playheadFrame < _selectionStartFrame)
            {
                _playheadFrame = _selectionStartFrame;
            }
        }
    }

    public void SetSelectionEnd(int endFrame, bool applyZeroCrossingSnap = true)
    {
        lock (_bufferLock)
        {
            int total = _left.Length;
            if (total <= 1) return;

            int minEnd = Math.Min(total, _selectionStartFrame + Math.Max(16, _sampleRate / 1000));
            int e = Math.Clamp(endFrame, minEnd, total);
            if (applyZeroCrossingSnap && _zeroCrossingSnap && e > minEnd && e < total)
            {
                int radius = Math.Clamp(_sampleRate / 120, 24, 1800);
                int snapped = SnapToNearestZeroCrossingLocked(e, radius);
                if (snapped > _selectionStartFrame + 8)
                    e = snapped;
            }
            _selectionEndFrame = e;
            if (_isPlaying && _playSelectionOnly && _playheadFrame >= _selectionEndFrame)
            {
                _playheadFrame = _selectionStartFrame;
            }
        }
    }

    public void SelectAll()
    {
        lock (_bufferLock)
        {
            _selectionStartFrame = 0;
            _selectionEndFrame = _left.Length;
        }
    }

    /// <summary>
    /// Finds the nearest positive-slope zero-crossing around <paramref name="targetFrame"/> so loop points
    /// and cut edges join at 0.0 amplitude with matching phase slope (zero clicks or pops!).
    /// </summary>
    public int SnapToNearestZeroCrossing(int targetFrame, int radiusFrames = 512)
    {
        lock (_bufferLock)
        {
            return SnapToNearestZeroCrossingLocked(targetFrame, radiusFrames);
        }
    }

    private int SnapToNearestZeroCrossingLocked(int targetFrame, int radiusFrames)
    {
        int total = _left.Length;
        if (total < 4)
            return Math.Clamp(targetFrame, 0, Math.Max(0, total - 1));

        int center = Math.Clamp(targetFrame, 1, total - 2);
        int minIdx = Math.Max(1, center - radiusFrames);
        int maxIdx = Math.Min(total - 2, center + radiusFrames);

        int bestRisingIdx = -1;
        int bestRisingDist = int.MaxValue;

        int bestAnyZeroIdx = -1;
        int bestAnyZeroDist = int.MaxValue;

        int quietestIdx = center;
        float quietestEnergy = Math.Abs(_left[center]) + Math.Abs(_right[center]);

        for (int i = minIdx; i <= maxIdx; i++)
        {
            float lPrev = _left[i - 1];
            float lCur = _left[i];
            float rPrev = _right[i - 1];
            float rCur = _right[i];

            int dist = Math.Abs(i - center);

            // Preferred: positive-slope zero crossing on Left channel where Right channel is also low
            if (lPrev <= 0f && lCur >= 0f && (lCur > lPrev || Math.Abs(lCur) < 1e-4f))
            {
                if (dist < bestRisingDist && Math.Abs(rCur) < 0.25f)
                {
                    bestRisingDist = dist;
                    bestRisingIdx = i;
                }
                if (dist < bestAnyZeroDist)
                {
                    bestAnyZeroDist = dist;
                    bestAnyZeroIdx = i;
                }
            }
            else if ((lPrev >= 0f && lCur <= 0f) || (rPrev <= 0f && rCur >= 0f))
            {
                if (dist < bestAnyZeroDist)
                {
                    bestAnyZeroDist = dist;
                    bestAnyZeroIdx = i;
                }
            }

            float energy = Math.Abs(lCur) + Math.Abs(rCur);
            if (energy < quietestEnergy)
            {
                quietestEnergy = energy;
                quietestIdx = i;
            }
        }

        if (bestRisingIdx >= 0)
            return bestRisingIdx;
        if (bestAnyZeroIdx >= 0)
            return bestAnyZeroIdx;
        return quietestIdx;
    }

    // =========================================================================
    // NON-DESTRUCTIVE AUDIO EDITING (CUT START/END, DELETE MIDDLE, FADES, UNDO)
    // =========================================================================

    private void PushUndoSnapshotLocked(string actionLabel)
    {
        if (_undoStack.Count >= 18)
        {
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = Math.Min(14, arr.Length - 1); i >= 0; i--)
                _undoStack.Push(arr[i]);
        }

        _undoStack.Push(new AudioEditorBufferSnapshot
        {
            Left = (float[])_left.Clone(),
            Right = (float[])_right.Clone(),
            SelectionStart = _selectionStartFrame,
            SelectionEnd = _selectionEndFrame,
            ActionLabel = actionLabel
        });
        _redoStack.Clear();
    }

    /// <summary>
    /// Cuts both the start (before IN) and end (after OUT), keeping ONLY the selected audio region [IN .. OUT].
    /// Applies a 32-sample (~0.16 ms at 192 kHz) micro de-click ramp on the boundaries if non-zero.
    /// </summary>
    public bool CropToSelection()
    {
        lock (_bufferLock)
        {
            int len = _selectionEndFrame - _selectionStartFrame;
            if (len <= 0 || (_selectionStartFrame == 0 && _selectionEndFrame == _left.Length))
                return false;

            PushUndoSnapshotLocked("Cut Start & End (Crop)");

            float[] newL = new float[len];
            float[] newR = new float[len];
            Array.Copy(_left, _selectionStartFrame, newL, 0, len);
            Array.Copy(_right, _selectionStartFrame, newR, 0, len);

            ApplyMicroEdgeDeclick(newL, newR);

            _left = newL;
            _right = newR;
            _selectionStartFrame = 0;
            _selectionEndFrame = len;
            _playheadFrame = 0;
            return true;
        }
    }

    /// <summary>
    /// Cuts from the start of the file up to the IN marker [0 .. IN), keeping [IN .. TotalFrames].
    /// </summary>
    public bool TrimBeforeStart()
    {
        lock (_bufferLock)
        {
            if (_selectionStartFrame <= 0 || _selectionStartFrame >= _left.Length)
                return false;

            int cutFrames = _selectionStartFrame;
            int newLen = _left.Length - cutFrames;
            if (newLen <= 16)
                return false;

            PushUndoSnapshotLocked("Cut Start (Before IN)");

            float[] newL = new float[newLen];
            float[] newR = new float[newLen];
            Array.Copy(_left, cutFrames, newL, 0, newLen);
            Array.Copy(_right, cutFrames, newR, 0, newLen);

            ApplyMicroEdgeDeclick(newL, newR, startOnly: true);

            _left = newL;
            _right = newR;
            _selectionEndFrame = Math.Clamp(_selectionEndFrame - cutFrames, 1, newLen);
            _selectionStartFrame = 0;
            _playheadFrame = 0;
            return true;
        }
    }

    /// <summary>
    /// Cuts everything after the OUT marker (OUT .. TotalFrames], keeping [0 .. OUT].
    /// </summary>
    public bool TrimAfterEnd()
    {
        lock (_bufferLock)
        {
            if (_selectionEndFrame <= 0 || _selectionEndFrame >= _left.Length)
                return false;

            int newLen = _selectionEndFrame;
            if (newLen <= 16)
                return false;

            PushUndoSnapshotLocked("Cut End (After OUT)");

            float[] newL = new float[newLen];
            float[] newR = new float[newLen];
            Array.Copy(_left, 0, newL, 0, newLen);
            Array.Copy(_right, 0, newR, 0, newLen);

            ApplyMicroEdgeDeclick(newL, newR, endOnly: true);

            _left = newL;
            _right = newR;
            _selectionEndFrame = newLen;
            _selectionStartFrame = Math.Clamp(_selectionStartFrame, 0, newLen - 1);
            _playheadFrame = Math.Clamp(_playheadFrame, 0, newLen - 1);
            return true;
        }
    }

    /// <summary>
    /// Deletes the selected middle region [IN .. OUT] and joins the remaining left and right sections
    /// with a smooth 2 ms micro-crossfade so there is no click at the splice point.
    /// </summary>
    public bool DeleteSelectedRegion()
    {
        lock (_bufferLock)
        {
            int selLen = _selectionEndFrame - _selectionStartFrame;
            int total = _left.Length;
            if (selLen <= 0 || selLen >= total - 16)
                return false;

            PushUndoSnapshotLocked("Delete Selection");

            int newLen = total - selLen;
            float[] newL = new float[newLen];
            float[] newR = new float[newLen];

            int headCount = _selectionStartFrame;
            int tailCount = total - _selectionEndFrame;

            if (headCount > 0)
            {
                Array.Copy(_left, 0, newL, 0, headCount);
                Array.Copy(_right, 0, newR, 0, headCount);
            }
            if (tailCount > 0)
            {
                Array.Copy(_left, _selectionEndFrame, newL, headCount, tailCount);
                Array.Copy(_right, _selectionEndFrame, newR, headCount, tailCount);
            }

            // Smooth 2 ms raised-cosine splice around headCount if both sides exist
            int xfade = Math.Min(Math.Min(headCount, tailCount), Math.Max(16, _sampleRate / 500));
            for (int i = 0; i < xfade; i++)
            {
                int dstIdx = headCount - xfade + i;
                int srcBeforeIdx = _selectionEndFrame - xfade + i;
                float t = (float)(i + 1) / (xfade + 1);
                float w = 0.5f * (1.0f - MathF.Cos(t * MathF.PI));
                newL[dstIdx] = newL[dstIdx] * (1.0f - w) + _left[srcBeforeIdx] * w;
                newR[dstIdx] = newR[dstIdx] * (1.0f - w) + _right[srcBeforeIdx] * w;
            }

            _left = newL;
            _right = newR;
            _selectionStartFrame = Math.Clamp(headCount, 0, Math.Max(0, newLen - 1));
            _selectionEndFrame = newLen;
            _playheadFrame = _selectionStartFrame;
            return true;
        }
    }

    public bool ApplyFadeIn()
    {
        lock (_bufferLock)
        {
            int total = _left.Length;
            if (total < 16) return false;

            int s = _selectionStartFrame;
            int e = _selectionEndFrame;
            // If full buffer is selected, apply fade-in over first 1.5 seconds (or 25% of track)
            if (s == 0 && e == total)
            {
                e = Math.Min(total, Math.Max(64, Math.Min((int)(_sampleRate * 1.5), total / 4)));
            }

            int len = e - s;
            if (len < 8) return false;

            PushUndoSnapshotLocked("Fade In");
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / (len - 1);
                // Smooth equal-power / S-curve fade in
                float gain = t * t * (3.0f - 2.0f * t);
                _left[s + i] *= gain;
                _right[s + i] *= gain;
            }
            return true;
        }
    }

    public bool ApplyFadeOut()
    {
        lock (_bufferLock)
        {
            int total = _left.Length;
            if (total < 16) return false;

            int s = _selectionStartFrame;
            int e = _selectionEndFrame;
            // If full buffer is selected, apply fade-out over last 1.5 seconds (or 25% of track)
            if (s == 0 && e == total)
            {
                int fadeFrames = Math.Min(total, Math.Max(64, Math.Min((int)(_sampleRate * 1.5), total / 4)));
                s = total - fadeFrames;
            }

            int len = e - s;
            if (len < 8) return false;

            PushUndoSnapshotLocked("Fade Out");
            for (int i = 0; i < len; i++)
            {
                float t = 1.0f - ((float)i / (len - 1));
                float gain = t * t * (3.0f - 2.0f * t);
                _left[s + i] *= gain;
                _right[s + i] *= gain;
            }
            return true;
        }
    }

    public bool NormalizeSelection(float targetPeak = 0.977f)
    {
        lock (_bufferLock)
        {
            int s = _selectionStartFrame;
            int e = _selectionEndFrame;
            if (e - s < 4) return false;

            float peak = 0f;
            for (int i = s; i < e; i++)
            {
                float p = Math.Max(Math.Abs(_left[i]), Math.Abs(_right[i]));
                if (p > peak) peak = p;
            }

            if (peak < 1e-5f || Math.Abs(peak - targetPeak) < 0.005f)
                return false;

            PushUndoSnapshotLocked("Normalize");
            float scale = targetPeak / peak;
            for (int i = s; i < e; i++)
            {
                _left[i] = Math.Clamp(_left[i] * scale, -1.0f, 1.0f);
                _right[i] = Math.Clamp(_right[i] * scale, -1.0f, 1.0f);
            }
            return true;
        }
    }

    public bool Undo()
    {
        lock (_bufferLock)
        {
            if (_undoStack.Count == 0)
                return false;

            _redoStack.Push(new AudioEditorBufferSnapshot
            {
                Left = (float[])_left.Clone(),
                Right = (float[])_right.Clone(),
                SelectionStart = _selectionStartFrame,
                SelectionEnd = _selectionEndFrame,
                ActionLabel = "Redo"
            });

            var snap = _undoStack.Pop();
            _left = snap.Left;
            _right = snap.Right;
            _selectionStartFrame = Math.Clamp(snap.SelectionStart, 0, Math.Max(0, _left.Length - 1));
            _selectionEndFrame = Math.Clamp(snap.SelectionEnd, _selectionStartFrame + 1, _left.Length);
            _playheadFrame = _selectionStartFrame;
            return true;
        }
    }

    public bool Redo()
    {
        lock (_bufferLock)
        {
            if (_redoStack.Count == 0)
                return false;

            _undoStack.Push(new AudioEditorBufferSnapshot
            {
                Left = (float[])_left.Clone(),
                Right = (float[])_right.Clone(),
                SelectionStart = _selectionStartFrame,
                SelectionEnd = _selectionEndFrame,
                ActionLabel = "Undo"
            });

            var snap = _redoStack.Pop();
            _left = snap.Left;
            _right = snap.Right;
            _selectionStartFrame = Math.Clamp(snap.SelectionStart, 0, Math.Max(0, _left.Length - 1));
            _selectionEndFrame = Math.Clamp(snap.SelectionEnd, _selectionStartFrame + 1, _left.Length);
            _playheadFrame = _selectionStartFrame;
            return true;
        }
    }

    public bool ResetToOriginal()
    {
        lock (_bufferLock)
        {
            if (_originalLeft.Length == 0)
                return false;

            PushUndoSnapshotLocked("Reset to Original");
            _left = (float[])_originalLeft.Clone();
            _right = (float[])_originalRight.Clone();
            _selectionStartFrame = 0;
            _selectionEndFrame = _left.Length;
            _playheadFrame = 0;
            return true;
        }
    }

    private static void ApplyMicroEdgeDeclick(float[] left, float[] right, bool startOnly = false, bool endOnly = false)
    {
        int n = Math.Min(left.Length, right.Length);
        int ramp = Math.Min(24, n / 8);
        if (ramp < 2) return;

        if (!endOnly)
        {
            for (int i = 0; i < ramp; i++)
            {
                float g = (float)i / ramp;
                left[i] *= g;
                right[i] *= g;
            }
        }

        if (!startOnly)
        {
            for (int i = 0; i < ramp; i++)
            {
                float g = (float)i / ramp;
                int idx = n - 1 - i;
                left[idx] *= g;
                right[idx] *= g;
            }
        }
    }

    // =========================================================================
    // LOSSLESS WAV EXPORT (SAVE SELECTED LOOP / PART OR FULL EDITED FILE)
    // =========================================================================

    public RecordedClipInfo SaveSelectionToWav(string outputPath, RecordingBitFormat bitFormat, string sourceName = "Sample & Loop Editor (Selected Loop)")
    {
        lock (_bufferLock)
        {
            return SaveRangeToWavLocked(outputPath, _selectionStartFrame, _selectionEndFrame, bitFormat, sourceName);
        }
    }

    public RecordedClipInfo SaveFullBufferToWav(string outputPath, RecordingBitFormat bitFormat, string sourceName = "Sample & Loop Editor (Full Track)")
    {
        lock (_bufferLock)
        {
            return SaveRangeToWavLocked(outputPath, 0, _left.Length, bitFormat, sourceName);
        }
    }

    private RecordedClipInfo SaveRangeToWavLocked(
        string outputPath,
        int startFrame,
        int endFrame,
        RecordingBitFormat bitFormat,
        string sourceName)
    {
        int total = _left.Length;
        int s = Math.Clamp(startFrame, 0, Math.Max(0, total - 1));
        int e = Math.Clamp(endFrame, s + 1, total);
        int frames = e - s;
        if (frames <= 0)
            throw new InvalidOperationException("Selected audio range is empty.");

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        int bytesPerFrame = bitFormat switch
        {
            RecordingBitFormat.Pcm16 => 4,
            RecordingBitFormat.Float32 => 8,
            _ => 6
        };

        long dataBytes = (long)frames * bytesPerFrame;

        // Write via a temporary file when overwriting the active file on disk
        string tempPath = outputPath + ".tmp_spectra";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            AudioRecorderService.WriteWavHeader(fs, _sampleRate, 2, bitFormat, dataBytes);

            const int chunkFrames = 8192;
            byte[] chunkBuf = new byte[chunkFrames * bytesPerFrame];
            int remaining = frames;
            int offset = s;

            while (remaining > 0)
            {
                int batch = Math.Min(chunkFrames, remaining);
                int pos = 0;
                for (int i = 0; i < batch; i++)
                {
                    AudioRecorderService.WriteStereoSample(chunkBuf, ref pos, _left[offset + i], _right[offset + i], bitFormat);
                }
                fs.Write(chunkBuf, 0, pos);
                offset += batch;
                remaining -= batch;
            }
            fs.Flush();
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPath, outputPath);

        var fi = new FileInfo(outputPath);
        double seconds = _sampleRate > 0 ? (double)frames / _sampleRate : 0.0;
        string fmtLabel = bitFormat switch
        {
            RecordingBitFormat.Pcm16 => $"{_sampleRate / 1000.0:0.#} kHz • 16-bit WAV",
            RecordingBitFormat.Float32 => $"{_sampleRate / 1000.0:0.#} kHz • 32-bit Float WAV",
            _ => $"{_sampleRate / 1000.0:0.#} kHz • 24-bit WAV"
        };

        return new RecordedClipInfo
        {
            FilePath = outputPath,
            FileName = Path.GetFileName(outputPath),
            SourceName = sourceName,
            FormatLabel = fmtLabel,
            Duration = TimeSpan.FromSeconds(seconds),
            FileSizeBytes = fi.Exists ? fi.Length : dataBytes + 44,
            RecordedAtLocal = DateTime.Now
        };
    }

    // =========================================================================
    // REAL-TIME GAPLESS WASAPI LOOP & AUDITION PLAYER
    // =========================================================================

    public void StartPlayback(bool loopSelection, bool playSelectionOnly)
    {
        lock (_bufferLock)
        {
            if (_left.Length == 0)
                return;

            _loopEnabled = loopSelection;
            _playSelectionOnly = playSelectionOnly;

            if (playSelectionOnly)
            {
                _playheadFrame = _selectionStartFrame;
            }
            else if (_playheadFrame >= _left.Length - 32 || _playheadFrame < 0)
            {
                _playheadFrame = 0;
            }
        }

        _isPlaying = true;
        EnsureRenderThreadRunning();
    }

    public void StopPlayback()
    {
        _isPlaying = false;
        _stopThreadRequested = true;
        var t = _renderThread;
        _renderThread = null;
        if (t != null && t.IsAlive)
        {
            t.Join(350);
        }
        _peakL = 0f;
        _peakR = 0f;
    }

    private void EnsureRenderThreadRunning()
    {
        if (_renderThread != null && _renderThread.IsAlive)
            return;

        _stopThreadRequested = false;
        _renderThread = new Thread(WasapiLoopPlayerThread)
        {
            Name = "SpectraSampleLoopPlayer",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();
    }

    private void WasapiLoopPlayerThread()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;
        IntPtr pMixFormat = IntPtr.Zero;

        timeBeginPeriod(1);
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = -1;
            if (!string.IsNullOrWhiteSpace(_endpointId))
            {
                hr = enumerator.GetDevice(_endpointId, out device);
            }
            if (hr < 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
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

            int initialFrames = Math.Min((int)bufferFrames, (_outputSampleRate * 40) / 1000);
            float[] leftBuf = new float[bufferFrames];
            float[] rightBuf = new float[bufferFrames];
            byte[] rawOut = new byte[bufferFrames * blockAlign];

            if (initialFrames > 0 && renderClient.GetBuffer((uint)initialFrames, out IntPtr pInitData) >= 0 && pInitData != IntPtr.Zero)
            {
                RenderAudioChunk(leftBuf, rightBuf, initialFrames, _outputSampleRate);
                PackOutputFrames(leftBuf, rightBuf, initialFrames, channels, bitsPerSample, blockAlign, isFloat, rawOut);
                Marshal.Copy(rawOut, 0, pInitData, initialFrames * blockAlign);
                renderClient.ReleaseBuffer((uint)initialFrames, 0);
            }

            hr = audioClient.Start();
            if (hr < 0)
                return;

            int targetFill = Math.Min((int)bufferFrames, Math.Max(1024, (_outputSampleRate * 50) / 1000));
            int minWriteBatch = Math.Max(64, (_outputSampleRate * 3) / 1000);

            while (!_stopThreadRequested && _isPlaying)
            {
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

                RenderAudioChunk(leftBuf, rightBuf, framesToWrite, _outputSampleRate);

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
        }
        finally
        {
            timeEndPeriod(1);
            _isPlaying = false;
            _peakL = 0f;
            _peakR = 0f;
            if (pMixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pMixFormat);
            if (renderClient != null) Marshal.ReleaseComObject(renderClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private void RenderAudioChunk(float[] outL, float[] outR, int frames, int outputSampleRate)
    {
        Array.Clear(outL, 0, frames);
        Array.Clear(outR, 0, frames);

        lock (_bufferLock)
        {
            int total = _left.Length;
            if (!_isPlaying || total == 0)
                return;

            double step = (double)_sampleRate / Math.Max(8000, outputSampleRate);
            bool bitExact1To1 = _sampleRate == outputSampleRate;

            int loopStart = _playSelectionOnly || _loopEnabled ? _selectionStartFrame : 0;
            int loopEnd = _playSelectionOnly || _loopEnabled ? _selectionEndFrame : total;
            loopStart = Math.Clamp(loopStart, 0, Math.Max(0, total - 1));
            loopEnd = Math.Clamp(loopEnd, loopStart + 1, total);

            // 24-sample micro crossfade at loop wrap seam to guarantee zero click even if user turned off Zero-Crossing Snap
            int seamRamp = Math.Min(24, (loopEnd - loopStart) / 8);

            float maxL = 0f;
            float maxR = 0f;

            for (int i = 0; i < frames; i++)
            {
                if (_playheadFrame >= loopEnd)
                {
                    if (_loopEnabled)
                    {
                        _playheadFrame = loopStart + (_playheadFrame - loopEnd);
                        if (_playheadFrame < loopStart || _playheadFrame >= loopEnd)
                            _playheadFrame = loopStart;
                    }
                    else
                    {
                        _playheadFrame = loopStart;
                        _isPlaying = false;
                        break;
                    }
                }

                float sL;
                float sR;

                if (bitExact1To1)
                {
                    int idx = Math.Clamp((int)_playheadFrame, 0, total - 1);
                    sL = _left[idx];
                    sR = _right[idx];
                }
                else
                {
                    int idx0 = Math.Clamp((int)_playheadFrame, 0, total - 1);
                    int idx1 = Math.Min(total - 1, idx0 + 1);
                    float frac = (float)(_playheadFrame - idx0);
                    sL = _left[idx0] + (_left[idx1] - _left[idx0]) * frac;
                    sR = _right[idx0] + (_right[idx1] - _right[idx0]) * frac;
                }

                // Micro seam blend right before loop wrap when looping is active
                if (_loopEnabled && seamRamp >= 4)
                {
                    double distToEnd = loopEnd - _playheadFrame;
                    if (distToEnd > 0 && distToEnd <= seamRamp)
                    {
                        float t = (float)(1.0 - (distToEnd / seamRamp)); // 0 -> 1 approaching loopEnd
                        int headIdx = Math.Clamp(loopStart + (int)(seamRamp - distToEnd), 0, total - 1);
                        sL = sL * (1.0f - t * 0.5f) + _left[headIdx] * (t * 0.5f);
                        sR = sR * (1.0f - t * 0.5f) + _right[headIdx] * (t * 0.5f);
                    }
                }

                outL[i] = sL;
                outR[i] = sR;

                float aL = Math.Abs(sL);
                float aR = Math.Abs(sR);
                if (aL > maxL) maxL = aL;
                if (aR > maxR) maxR = aR;

                _playheadFrame += bitExact1To1 ? 1.0 : step;
            }

            _peakL = maxL;
            _peakR = maxR;
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
        StopPlayback();
    }
}
