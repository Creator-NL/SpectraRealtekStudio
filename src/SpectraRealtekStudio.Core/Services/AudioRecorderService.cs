using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SpectraRealtekStudio.Core.Interop;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

public sealed class RecorderMonitorSnapshot
{
    public bool IsMonitoring { get; init; }
    public bool IsRecording { get; init; }
    public bool IsWaitingForSound { get; init; }
    public bool IsPaused { get; init; }
    public double TriggerThresholdDb { get; init; } = -40.0;
    public float PeakLeft { get; init; }
    public float PeakRight { get; init; }
    public bool ClippedRecently { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long BytesWritten { get; init; }
    public int SampleRate { get; init; }
    public int InputChannels { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public float[] WaveformPeaks { get; init; } = Array.Empty<float>();
}

/// <summary>
/// Native zero-dependency Windows WASAPI Studio Audio Recorder.
/// Supports BOTH "What You Hear" (WASAPI Loopback on any playback output) AND any hardware capture source
/// (Line-In, Microphone, Stereo Mix, USB audio interface), streaming bit-exact 16-bit PCM, 24-bit PCM,
/// or 32-bit IEEE Float Stereo WAV directly to disk with live level metering and soft-clip protection.
/// </summary>
public sealed class AudioRecorderService : IDisposable
{
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000u;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002u;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _stateLock = new();
    private readonly float[] _waveHistory = new float[240];
    private int _waveWritePos;

    private Thread? _captureThread;
    private volatile bool _stopThreadRequested;
    private volatile bool _isMonitoring;
    private volatile bool _isRecording;
    private volatile bool _isWaitingForSound;
    private volatile bool _isPaused;

    private string _endpointId = string.Empty;
    private bool _isLoopback = true;
    private string _sourceName = string.Empty;
    private RecordingBitFormat _bitFormat = RecordingBitFormat.Pcm24;
    private double _inputGainDb;
    private bool _softLimiterEnabled = true;
    private double _triggerThresholdDb = -40.0;
    private float _triggerThresholdLinear = 0.01f;

    // 30 ms stereo pre-roll buffer while armed so the very first drum transient / attack is never clipped!
    private float[] _preRollL = Array.Empty<float>();
    private float[] _preRollR = Array.Empty<float>();
    private int _preRollWritePos;
    private int _preRollFilled;

    private FileStream? _wavStream;
    private string _currentOutputPath = string.Empty;
    private long _dataBytesWritten;
    private long _framesRecorded;
    private int _sampleRate = 48000;
    private int _inputChannels = 2;
    private float _peakL;
    private float _peakR;
    private DateTime _lastClipUtc = DateTime.MinValue;
    private string _statusMessage = "Recorder Idle";

    public bool IsMonitoring => _isMonitoring;
    public bool IsRecording => _isRecording;
    public bool IsWaitingForSound => _isWaitingForSound;
    public bool IsPaused => _isPaused;
    public string CurrentOutputPath => _currentOutputPath;

    public void SetInputGainDb(double gainDb)
    {
        _inputGainDb = Math.Clamp(gainDb, -12.0, 18.0);
    }

    public void SetSoftLimiter(bool enabled)
    {
        _softLimiterEnabled = enabled;
    }

    public void SetTriggerThresholdDb(double thresholdDb)
    {
        _triggerThresholdDb = Math.Clamp(thresholdDb, -60.0, -6.0);
        _triggerThresholdLinear = (float)Math.Pow(10.0, _triggerThresholdDb / 20.0);
    }

    /// <summary>
    /// Starts live WASAPI input monitoring (without writing to disk) so Tab 8 meters &amp; waveform respond immediately.
    /// </summary>
    public void StartMonitoring(string endpointId, bool isLoopback, string sourceName)
    {
        if (_isRecording)
            return;

        if (_isMonitoring && string.Equals(_endpointId, endpointId, StringComparison.OrdinalIgnoreCase) && _isLoopback == isLoopback)
            return;

        StopMonitoring();

        _endpointId = endpointId;
        _isLoopback = isLoopback;
        _sourceName = sourceName;
        _stopThreadRequested = false;

        lock (_stateLock)
        {
            Array.Clear(_waveHistory, 0, _waveHistory.Length);
            _waveWritePos = 0;
            _peakL = 0f;
            _peakR = 0f;
        }

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "SpectraWasapiRecorder",
            IsBackground = true
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void StopMonitoring()
    {
        if (_isRecording)
            return;

        _stopThreadRequested = true;
        var t = _captureThread;
        _captureThread = null;
        if (t != null && t.IsAlive)
        {
            t.Join(450);
        }
        _isMonitoring = false;
        _peakL = 0f;
        _peakR = 0f;
        _statusMessage = "Recorder Standby (0% CPU)";
    }

    /// <summary>
    /// Begins streaming stereo WAV audio to <paramref name="outputFilePath"/> from the selected source.
    /// When <paramref name="waitForSound"/> is true, arms the recorder with a 30 ms pre-roll ring buffer
    /// and starts writing to disk automatically on the exact first sound crossing <paramref name="triggerThresholdDb"/>.
    /// </summary>
    public bool StartRecording(
        string endpointId,
        bool isLoopback,
        string sourceName,
        string outputFilePath,
        RecordingBitFormat bitFormat,
        out string errorMessage,
        bool waitForSound = false,
        double triggerThresholdDb = -40.0)
    {
        errorMessage = string.Empty;
        if (_isRecording)
        {
            errorMessage = "Recording is already active.";
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            // Ensure monitor thread is attached to the requested endpoint
            if (!_isMonitoring || !string.Equals(_endpointId, endpointId, StringComparison.OrdinalIgnoreCase) || _isLoopback != isLoopback)
            {
                StartMonitoring(endpointId, isLoopback, sourceName);
                // Wait briefly for WASAPI mix format discovery (up to 400 ms)
                for (int i = 0; i < 20 && !_isMonitoring; i++)
                    Thread.Sleep(20);
            }

            if (!_isMonitoring)
            {
                errorMessage = _statusMessage;
                return false;
            }

            SetTriggerThresholdDb(triggerThresholdDb);

            lock (_stateLock)
            {
                _bitFormat = bitFormat;
                _currentOutputPath = outputFilePath;
                _dataBytesWritten = 0;
                _framesRecorded = 0;
                _isPaused = false;

                int preRollCapacity = Math.Clamp((_sampleRate * 30) / 1000, 256, 8192);
                _preRollL = new float[preRollCapacity];
                _preRollR = new float[preRollCapacity];
                _preRollWritePos = 0;
                _preRollFilled = 0;

                if (waitForSound)
                {
                    // Stay armed cleanly without creating a 0-byte WAV file until sound triggers!
                    _wavStream = null;
                    _isWaitingForSound = true;
                    _isRecording = true;
                }
                else
                {
                    _wavStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                    WriteWavHeader(_wavStream, _sampleRate, 2, bitFormat, 0);
                    _isWaitingForSound = false;
                    _isRecording = true;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void TogglePause()
    {
        if (!_isRecording || _isWaitingForSound)
            return;
        _isPaused = !_isPaused;
    }

    public RecordedClipInfo? StopRecording(bool keepMonitoring)
    {
        RecordedClipInfo? clip = null;

        lock (_stateLock)
        {
            if (_isRecording)
            {
                bool wasWaiting = _isWaitingForSound;
                _isRecording = false;
                _isWaitingForSound = false;
                _isPaused = false;

                if (!wasWaiting && _wavStream != null)
                {
                    try
                    {
                        // Patch final RIFF & data chunk sizes cleanly
                        _wavStream.Flush();
                        WriteWavHeader(_wavStream, _sampleRate, 2, _bitFormat, _dataBytesWritten);
                        _wavStream.Flush();
                        long totalBytes = _wavStream.Length;
                        _wavStream.Dispose();
                        _wavStream = null;

                        double seconds = _sampleRate > 0 ? (double)_framesRecorded / _sampleRate : 0.0;
                        string fmtLabel = _bitFormat switch
                        {
                            RecordingBitFormat.Pcm16 => $"{_sampleRate / 1000.0:0.#} kHz • 16-bit WAV",
                            RecordingBitFormat.Float32 => $"{_sampleRate / 1000.0:0.#} kHz • 32-bit Float WAV",
                            _ => $"{_sampleRate / 1000.0:0.#} kHz • 24-bit WAV"
                        };

                        clip = new RecordedClipInfo
                        {
                            FilePath = _currentOutputPath,
                            FileName = Path.GetFileName(_currentOutputPath),
                            SourceName = _sourceName,
                            FormatLabel = fmtLabel,
                            Duration = TimeSpan.FromSeconds(seconds),
                            FileSizeBytes = totalBytes,
                            RecordedAtLocal = DateTime.Now
                        };
                    }
                    catch
                    {
                        try { _wavStream?.Dispose(); } catch { }
                        _wavStream = null;
                    }
                }
            }
        }

        if (!keepMonitoring)
        {
            StopMonitoring();
        }

        return clip;
    }

    /// <summary>
    /// Cancels an active or armed recording, discards and deletes the partial WAV file from disk,
    /// and resets the waveform timeline cleanly so the user can start fresh immediately.
    /// </summary>
    public bool CancelRecording(bool keepMonitoring)
    {
        bool hadActive = false;
        string? fileToDelete = null;

        lock (_stateLock)
        {
            if (_isRecording)
            {
                hadActive = true;
                _isRecording = false;
                _isWaitingForSound = false;
                _isPaused = false;
                _dataBytesWritten = 0;
                _framesRecorded = 0;
                _preRollFilled = 0;
                _preRollWritePos = 0;
                Array.Clear(_waveHistory, 0, _waveHistory.Length);
                _waveWritePos = 0;

                if (_wavStream != null)
                {
                    try { _wavStream.Dispose(); } catch { }
                    _wavStream = null;
                    fileToDelete = _currentOutputPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fileToDelete))
        {
            try
            {
                if (File.Exists(fileToDelete))
                    File.Delete(fileToDelete);
            }
            catch
            {
            }
        }

        if (!keepMonitoring)
        {
            StopMonitoring();
        }

        return hadActive;
    }

    public RecorderMonitorSnapshot GetSnapshot()
    {
        float[] wave = new float[_waveHistory.Length];
        float pL, pR;
        long bytes;
        long frames;
        int sr;

        lock (_stateLock)
        {
            int idx = _waveWritePos;
            for (int i = 0; i < wave.Length; i++)
            {
                wave[i] = _waveHistory[idx];
                idx = (idx + 1) % wave.Length;
            }
            pL = _peakL;
            pR = _peakR;
            // Smooth meter decay between UI frames
            _peakL *= 0.84f;
            _peakR *= 0.84f;
            bytes = _dataBytesWritten;
            frames = _framesRecorded;
            sr = _sampleRate;
        }

        return new RecorderMonitorSnapshot
        {
            IsMonitoring = _isMonitoring,
            IsRecording = _isRecording,
            IsWaitingForSound = _isWaitingForSound,
            IsPaused = _isPaused,
            TriggerThresholdDb = _triggerThresholdDb,
            PeakLeft = pL,
            PeakRight = pR,
            ClippedRecently = (DateTime.UtcNow - _lastClipUtc).TotalMilliseconds < 1200,
            Elapsed = sr > 0 ? TimeSpan.FromSeconds((double)frames / sr) : TimeSpan.Zero,
            BytesWritten = bytes > 0 ? bytes + 44 : 0,
            SampleRate = sr,
            InputChannels = _inputChannels,
            StatusMessage = _statusMessage,
            WaveformPeaks = wave
        };
    }

    private void CaptureLoop()
    {
        while (!_stopThreadRequested)
        {
            bool retry = RunCaptureSessionOnce();
            if (!retry || _stopThreadRequested)
                break;

            for (int i = 0; i < 15 && !_stopThreadRequested; i++)
                Thread.Sleep(25);
        }
    }

    private bool RunCaptureSessionOnce()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr pMixFormat = IntPtr.Zero;
        bool invalidated = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = -1;

            if (!string.IsNullOrWhiteSpace(_endpointId))
            {
                hr = enumerator.GetDevice(_endpointId, out device);
            }
            if (hr != 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(_isLoopback ? 0 : 1, 1, out device);
            }
            if (hr != 0 || device == null)
            {
                _statusMessage = "Selected recording audio source is unavailable.";
                return false;
            }

            Guid iidClient = IidAudioClient;
            hr = device.Activate(ref iidClient, 23, IntPtr.Zero, out object oClient);
            audioClient = oClient as IAudioClient;
            if (hr != 0 || audioClient == null)
            {
                _statusMessage = $"WASAPI AudioClient unavailable (0x{hr:X8}).";
                return false;
            }

            hr = audioClient.GetMixFormat(out pMixFormat);
            if (hr != 0 || pMixFormat == IntPtr.Zero)
            {
                _statusMessage = $"Could not read mix format (0x{hr:X8}).";
                return false;
            }

            ushort formatTag = (ushort)Marshal.ReadInt16(pMixFormat, 0);
            ushort channels = (ushort)Marshal.ReadInt16(pMixFormat, 2);
            int sampleRate = Marshal.ReadInt32(pMixFormat, 4);
            ushort blockAlign = (ushort)Marshal.ReadInt16(pMixFormat, 12);
            ushort bitsPerSample = (ushort)Marshal.ReadInt16(pMixFormat, 14);

            bool isFloat = formatTag == 3;
            if (formatTag == 0xFFFE)
            {
                byte[] guidBytes = new byte[16];
                Marshal.Copy(IntPtr.Add(pMixFormat, 24), guidBytes, 0, 16);
                isFloat = new Guid(guidBytes) == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            }

            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _inputChannels = channels > 0 ? channels : 2;

            uint streamFlags = _isLoopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0u;
            hr = audioClient.Initialize(0, streamFlags, 1_000_000, 0, pMixFormat, IntPtr.Zero);
            if (hr != 0)
            {
                _statusMessage = $"WASAPI capture init failed (0x{hr:X8}).";
                return false;
            }

            Guid iidCapture = IidAudioCaptureClient;
            hr = audioClient.GetService(ref iidCapture, out object oCap);
            captureClient = oCap as IAudioCaptureClient;
            if (hr != 0 || captureClient == null)
            {
                _statusMessage = $"WASAPI CaptureClient unavailable (0x{hr:X8}).";
                return false;
            }

            hr = audioClient.Start();
            if (hr != 0)
            {
                _statusMessage = $"WASAPI capture start failed (0x{hr:X8}).";
                return false;
            }

            _isMonitoring = true;
            string modeTag = _isLoopback ? "What You Hear (Loopback)" : "Hardware Capture";
            _statusMessage = $"{modeTag} • {_sampleRate:N0} Hz • {_inputChannels} ch • {(isFloat ? "32f" : $"{bitsPerSample}b")}";

            byte[] scratchIn = Array.Empty<byte>();
            byte[] scratchOut = Array.Empty<byte>();
            int silentTicks = 0;

            while (!_stopThreadRequested)
            {
                int hrNext = captureClient.GetNextPacketSize(out uint packetFrames);
                if (hrNext != 0)
                {
                    if ((uint)hrNext == 0x88890004u)
                    {
                        invalidated = true;
                        _statusMessage = "Audio format changed — reconnecting recorder...";
                    }
                    break;
                }

                if (packetFrames == 0)
                {
                    silentTicks++;
                    // During loopback recording (once triggered), if no app outputs sound, advance timeline with digital silence
                    if (_isRecording && !_isWaitingForSound && !_isPaused && _isLoopback && silentTicks >= 2)
                    {
                        int silenceFrames = Math.Max(1, (_sampleRate * 10) / 1000);
                        WriteSilenceFrames(silenceFrames, ref scratchOut);
                        PushWavePeak(0f);
                    }
                    else if (silentTicks % 3 == 0)
                    {
                        PushWavePeak(0f);
                    }
                    Thread.Sleep(10);
                    continue;
                }

                silentTicks = 0;

                while (packetFrames > 0 && !_stopThreadRequested)
                {
                    hr = captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
                    if (hr != 0)
                    {
                        if ((uint)hr == 0x88890004u)
                        {
                            invalidated = true;
                            _statusMessage = "Audio format changed — reconnecting recorder...";
                        }
                        break;
                    }

                    try
                    {
                        bool isSilent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || pData == IntPtr.Zero;
                        if (isSilent)
                        {
                            if (_isRecording && !_isWaitingForSound && !_isPaused)
                            {
                                WriteSilenceFrames((int)numFrames, ref scratchOut);
                            }
                            else if (_isRecording && _isWaitingForSound)
                            {
                                ClearPreRoll();
                            }
                            PushWavePeak(0f);
                        }
                        else
                        {
                            int byteCount = (int)(numFrames * blockAlign);
                            if (scratchIn.Length < byteCount)
                                scratchIn = new byte[byteCount];
                            Marshal.Copy(pData, scratchIn, 0, byteCount);

                            ProcessPacket(scratchIn, (int)numFrames, channels, bitsPerSample, blockAlign, isFloat, ref scratchOut);
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }

                    if (captureClient.GetNextPacketSize(out packetFrames) != 0)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Recorder error: {ex.Message}";
        }
        finally
        {
            _isMonitoring = false;
            try { audioClient?.Stop(); } catch { }
            if (pMixFormat != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pMixFormat);
            if (captureClient != null)
                Marshal.ReleaseComObject(captureClient);
            if (audioClient != null)
                Marshal.ReleaseComObject(audioClient);
            if (device != null)
                Marshal.ReleaseComObject(device);
            if (enumerator != null)
                Marshal.ReleaseComObject(enumerator);
        }

        return invalidated;
    }

    private void ProcessPacket(
        byte[] data,
        int numFrames,
        int channels,
        int bitsPerSample,
        int blockAlign,
        bool isFloat,
        ref byte[] outBuf)
    {
        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        double gainLinear = Math.Pow(10.0, _inputGainDb / 20.0);
        bool useLimiter = _softLimiterEnabled;

        float maxL = 0f;
        float maxR = 0f;
        bool clipped = false;

        lock (_stateLock)
        {
            bool writing = _isRecording && !_isWaitingForSound && !_isPaused && _wavStream != null;
            int outBytesPerFrame = _bitFormat switch
            {
                RecordingBitFormat.Pcm16 => 4,
                RecordingBitFormat.Float32 => 8,
                _ => 6 // 24-bit stereo
            };
            int neededOut = (_isRecording && !_isPaused) ? numFrames * outBytesPerFrame : 0;
            if (neededOut > 0 && outBuf.Length < neededOut)
                outBuf = new byte[neededOut];

            int outPos = 0;
            int framesWrittenThisPacket = 0;

            for (int f = 0; f < numFrames; f++)
            {
                int frameOff = f * blockAlign;
                double l, r;

                if (channels == 1)
                {
                    l = r = ReadSample(data, frameOff, bitsPerSample, isFloat);
                }
                else if (channels == 2)
                {
                    l = ReadSample(data, frameOff, bitsPerSample, isFloat);
                    r = ReadSample(data, frameOff + bytesPerSample, bitsPerSample, isFloat);
                }
                else if (channels == 4)
                {
                    // Quad 4.0: preserve exact unity 0 dB for stereo music on Front L/R while blending Rear if active
                    double fl = ReadSample(data, frameOff, bitsPerSample, isFloat);
                    double fr = ReadSample(data, frameOff + bytesPerSample, bitsPerSample, isFloat);
                    double rl = ReadSample(data, frameOff + 2 * bytesPerSample, bitsPerSample, isFloat);
                    double rr = ReadSample(data, frameOff + 3 * bytesPerSample, bitsPerSample, isFloat);
                    l = fl + rl * 0.5;
                    r = fr + rr * 0.5;
                }
                else
                {
                    // 5.1 / 7.1 studio downmix to Stereo
                    double fl = ReadSample(data, frameOff, bitsPerSample, isFloat);
                    double fr = ReadSample(data, frameOff + bytesPerSample, bitsPerSample, isFloat);
                    double c = channels > 2 ? ReadSample(data, frameOff + 2 * bytesPerSample, bitsPerSample, isFloat) : 0.0;
                    double lfe = channels > 3 ? ReadSample(data, frameOff + 3 * bytesPerSample, bitsPerSample, isFloat) : 0.0;
                    double sl = 0.0, sr = 0.0;
                    for (int ch = 4; ch < channels; ch++)
                    {
                        int off = frameOff + ch * bytesPerSample;
                        if (off + bytesPerSample > data.Length) break;
                        double s = ReadSample(data, off, bitsPerSample, isFloat);
                        if ((ch & 1) == 0) sl += s * 0.5;
                        else sr += s * 0.5;
                    }
                    l = fl + c * 0.707 + lfe * 0.35 + sl;
                    r = fr + c * 0.707 + lfe * 0.35 + sr;
                }

                l *= gainLinear;
                r *= gainLinear;

                if (Math.Abs(l) >= 0.995 || Math.Abs(r) >= 0.995)
                    clipped = true;

                if (useLimiter)
                {
                    l = SoftLimit(l);
                    r = SoftLimit(r);
                }
                else
                {
                    l = Math.Clamp(l, -1.0, 1.0);
                    r = Math.Clamp(r, -1.0, 1.0);
                }

                float absL = (float)Math.Abs(l);
                float absR = (float)Math.Abs(r);
                if (absL > maxL) maxL = absL;
                if (absR > maxR) maxR = absR;

                // Armed "Wait for First Sound" trigger check with 30 ms pre-roll look-ahead
                if (_isRecording && _isWaitingForSound)
                {
                    if (Math.Max(absL, absR) >= _triggerThresholdLinear)
                    {
                        if (TriggerArmedRecordingLocked(outBytesPerFrame))
                        {
                            writing = true;
                        }
                    }
                    else
                    {
                        PushPreRollLocked((float)l, (float)r);
                    }
                }

                if (writing)
                {
                    WriteStereoSample(outBuf, ref outPos, (float)l, (float)r, _bitFormat);
                    framesWrittenThisPacket++;
                }
            }

            if (writing && _wavStream != null && outPos > 0)
            {
                _wavStream.Write(outBuf, 0, outPos);
                _dataBytesWritten += outPos;
                _framesRecorded += framesWrittenThisPacket;
            }

            if (maxL > _peakL) _peakL = maxL;
            if (maxR > _peakR) _peakR = maxR;
            if (clipped) _lastClipUtc = DateTime.UtcNow;

            float combinedPeak = Math.Max(maxL, maxR);
            _waveHistory[_waveWritePos] = combinedPeak;
            _waveWritePos = (_waveWritePos + 1) % _waveHistory.Length;
        }
    }

    private void ClearPreRoll()
    {
        lock (_stateLock)
        {
            if (_preRollL.Length > 0)
            {
                Array.Clear(_preRollL, 0, _preRollL.Length);
                Array.Clear(_preRollR, 0, _preRollR.Length);
            }
            _preRollWritePos = 0;
            _preRollFilled = 0;
        }
    }

    private void PushPreRollLocked(float l, float r)
    {
        if (_preRollL.Length == 0)
            return;
        _preRollL[_preRollWritePos] = l;
        _preRollR[_preRollWritePos] = r;
        _preRollWritePos = (_preRollWritePos + 1) % _preRollL.Length;
        if (_preRollFilled < _preRollL.Length)
            _preRollFilled++;
    }

    private bool TriggerArmedRecordingLocked(int outBytesPerFrame)
    {
        try
        {
            _wavStream = new FileStream(_currentOutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            WriteWavHeader(_wavStream, _sampleRate, 2, _bitFormat, 0);
            _isWaitingForSound = false;

            // Flush up to 30 ms pre-roll so the initial transient / drum attack is 100% intact!
            if (_preRollFilled > 0 && _preRollL.Length > 0)
            {
                int startIdx = (_preRollWritePos - _preRollFilled + _preRollL.Length) % _preRollL.Length;

                // Trim pure leading digital silence from the pre-roll while keeping ~8 ms of smooth attack ramp
                int firstNonZero = _preRollFilled;
                float noiseFloor = _triggerThresholdLinear * 0.15f;
                for (int i = 0; i < _preRollFilled; i++)
                {
                    int idx = (startIdx + i) % _preRollL.Length;
                    if (Math.Max(Math.Abs(_preRollL[idx]), Math.Abs(_preRollR[idx])) >= noiseFloor)
                    {
                        firstNonZero = Math.Max(0, i - Math.Min(384, _sampleRate / 125));
                        break;
                    }
                }

                int flushCount = _preRollFilled - firstNonZero;
                if (flushCount > 0)
                {
                    byte[] preBuf = new byte[flushCount * outBytesPerFrame];
                    int pos = 0;
                    for (int i = firstNonZero; i < _preRollFilled; i++)
                    {
                        int idx = (startIdx + i) % _preRollL.Length;
                        WriteStereoSample(preBuf, ref pos, _preRollL[idx], _preRollR[idx], _bitFormat);
                    }
                    _wavStream.Write(preBuf, 0, pos);
                    _dataBytesWritten += pos;
                    _framesRecorded += flushCount;
                }

                _preRollFilled = 0;
                _preRollWritePos = 0;
            }

            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Failed to open recording file on trigger: {ex.Message}";
            _isRecording = false;
            _isWaitingForSound = false;
            return false;
        }
    }

    /// <summary>
    /// Deterministic helper to verify armed "Wait for First Sound" trigger + pre-roll behavior in automated tests.
    /// </summary>
    public static bool EvaluateWaitForSoundTrigger(
        ReadOnlySpan<float> stereoInterleavedFrames,
        double thresholdDb,
        out int triggerFrameIndex)
    {
        float thresh = (float)Math.Pow(10.0, Math.Clamp(thresholdDb, -60.0, -6.0) / 20.0);
        int frames = stereoInterleavedFrames.Length / 2;
        for (int f = 0; f < frames; f++)
        {
            float peak = Math.Max(Math.Abs(stereoInterleavedFrames[f * 2]), Math.Abs(stereoInterleavedFrames[f * 2 + 1]));
            if (peak >= thresh)
            {
                triggerFrameIndex = f;
                return true;
            }
        }
        triggerFrameIndex = -1;
        return false;
    }

    private void WriteSilenceFrames(int numFrames, ref byte[] outBuf)
    {
        lock (_stateLock)
        {
            if (!_isRecording || _isPaused || _wavStream == null || numFrames <= 0)
                return;

            int bytesPerFrame = _bitFormat switch
            {
                RecordingBitFormat.Pcm16 => 4,
                RecordingBitFormat.Float32 => 8,
                _ => 6
            };
            int bytes = numFrames * bytesPerFrame;
            if (outBuf.Length < bytes)
                outBuf = new byte[bytes];
            Array.Clear(outBuf, 0, bytes);
            _wavStream.Write(outBuf, 0, bytes);
            _dataBytesWritten += bytes;
            _framesRecorded += numFrames;
        }
    }

    private void PushWavePeak(float peak)
    {
        lock (_stateLock)
        {
            _waveHistory[_waveWritePos] = peak;
            _waveWritePos = (_waveWritePos + 1) % _waveHistory.Length;
        }
    }

    private static double SoftLimit(double x)
    {
        // Transparent below 0.85 (-1.4 dBFS), smooth tanh knee approaching 0.99 to prevent harsh digital clipping
        double a = Math.Abs(x);
        if (a <= 0.85) return x;
        double excess = (a - 0.85) / 0.145;
        double shaped = 0.85 + 0.145 * Math.Tanh(excess);
        return x >= 0 ? shaped : -shaped;
    }

    public static void WriteStereoSample(byte[] buf, ref int pos, float left, float right, RecordingBitFormat format)
    {
        switch (format)
        {
            case RecordingBitFormat.Pcm16:
            {
                short l16 = (short)Math.Clamp(Math.Round(left * 32767f), short.MinValue, short.MaxValue);
                short r16 = (short)Math.Clamp(Math.Round(right * 32767f), short.MinValue, short.MaxValue);
                buf[pos++] = (byte)(l16 & 0xFF);
                buf[pos++] = (byte)((l16 >> 8) & 0xFF);
                buf[pos++] = (byte)(r16 & 0xFF);
                buf[pos++] = (byte)((r16 >> 8) & 0xFF);
                break;
            }
            case RecordingBitFormat.Float32:
            {
                BitConverter.TryWriteBytes(buf.AsSpan(pos, 4), left);
                pos += 4;
                BitConverter.TryWriteBytes(buf.AsSpan(pos, 4), right);
                pos += 4;
                break;
            }
            default: // 24-bit signed little-endian PCM
            {
                int l24 = (int)Math.Clamp(Math.Round(left * 8388607f), -8388608, 8388607);
                int r24 = (int)Math.Clamp(Math.Round(right * 8388607f), -8388608, 8388607);
                buf[pos++] = (byte)(l24 & 0xFF);
                buf[pos++] = (byte)((l24 >> 8) & 0xFF);
                buf[pos++] = (byte)((l24 >> 16) & 0xFF);
                buf[pos++] = (byte)(r24 & 0xFF);
                buf[pos++] = (byte)((r24 >> 8) & 0xFF);
                buf[pos++] = (byte)((r24 >> 16) & 0xFF);
                break;
            }
        }
    }

    public static void WriteWavHeader(Stream stream, int sampleRate, short channels, RecordingBitFormat format, long dataBytes)
    {
        short formatTag = format == RecordingBitFormat.Float32 ? (short)3 : (short)1;
        short bitsPerSample = (short)format;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        uint safeData = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        uint riffSize = 36u + safeData;

        stream.Seek(0, SeekOrigin.Begin);
        using var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write(riffSize);
        bw.Write("WAVE"u8);

        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write(formatTag);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write("data"u8);
        bw.Write(safeData);
    }

    private static float ReadSample(byte[] buf, int offset, int bitsPerSample, bool isFloat)
    {
        if (isFloat && bitsPerSample == 32)
            return BitConverter.ToSingle(buf, offset);
        if (bitsPerSample == 16)
            return BitConverter.ToInt16(buf, offset) / 32768f;
        if (bitsPerSample == 24)
        {
            int raw = buf[offset] | (buf[offset + 1] << 8) | ((sbyte)buf[offset + 2] << 16);
            return raw / 8388608f;
        }
        if (bitsPerSample == 32)
            return BitConverter.ToInt32(buf, offset) / 2147483648f;
        return 0f;
    }

    public void Dispose()
    {
        StopRecording(keepMonitoring: false);
        StopMonitoring();
    }
}
