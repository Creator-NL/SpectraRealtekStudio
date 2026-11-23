using System;
using System.Runtime.InteropServices;
using System.Threading;
using SpectraRealtekStudio.Core.Interop;

namespace SpectraRealtekStudio.Core.Services;

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
    [PreserveSig] int GetStreamLatency(out long phnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
    [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

public enum SpectrumScaleMode
{
    Logarithmic = 0,
    ShiftedLogZero = 1,
    Linear = 2
}

public sealed class AudioVisualizerFrame
{
    public double[] Spectrum { get; init; } = Array.Empty<double>();
    public double[] PeakHold { get; init; } = Array.Empty<double>();
    public float[] WaveLeft { get; init; } = Array.Empty<float>();
    public float[] WaveRight { get; init; } = Array.Empty<float>();
    public double DominantFrequencyHz { get; set; }
    public double RmsDb { get; set; } = -90.0;
    public double PhaseCorrelation { get; set; } = 1.0;
    public double MinFreqHz { get; set; } = 0.0;
    public double MaxFreqHz { get; set; } = 20000.0;
    public bool LinearScale { get; set; }
    public SpectrumScaleMode ScaleMode { get; set; } = SpectrumScaleMode.ShiftedLogZero;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public bool IsCapturing { get; set; }
    public bool HasSignal { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Native zero-dependency WASAPI Loopback Capture + 8192-point Radix-2 Cooley-Tukey FFT engine.
/// Supports Stereo, Quad (4.0), 5.1, and 7.1 playback streams at 44.1-192 kHz (IEEE Float &amp; PCM)
/// with 5 Hz infrasonic-to-48 kHz ultrasonic frequency analysis and zero-lag sync.
/// </summary>
public sealed class WasapiLoopbackAnalyzer : IDisposable
{
    private const int FftSize = 8192;
    private const int WaveSampleCount = 512;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000u;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002u;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly object _ringLock = new();
    private readonly float[] _ringL = new float[FftSize];
    private readonly float[] _ringR = new float[FftSize];
    private int _ringWritePos;

    private readonly double[] _hann4096 = new double[4096];
    private readonly double[] _hann8192 = new double[FftSize];
    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImag = new double[FftSize];
    private readonly double[] _magSpectrum = new double[FftSize / 2];

    private double[] _smoothedBands = Array.Empty<double>();
    private double[] _peakHoldBands = Array.Empty<double>();
    private double[] _peakHoldVel = Array.Empty<double>();

    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private volatile bool _isCapturing;
    private string _currentEndpointId = string.Empty;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private string _statusMessage = "Idle";

    public bool IsCapturing => _isCapturing;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    public WasapiLoopbackAnalyzer()
    {
        for (int i = 0; i < 4096; i++)
            _hann4096[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / 4095.0));
        for (int i = 0; i < FftSize; i++)
            _hann8192[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
    }

    public void Start(string? endpointId)
    {
        Stop();
        _currentEndpointId = endpointId ?? string.Empty;
        _stopRequested = false;

        lock (_ringLock)
        {
            Array.Clear(_ringL, 0, _ringL.Length);
            Array.Clear(_ringR, 0, _ringR.Length);
            _ringWritePos = 0;
        }

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "SpectraWasapiLoopback",
            IsBackground = true
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        var t = _captureThread;
        _captureThread = null;
        if (t != null && t.IsAlive)
        {
            t.Join(400);
        }
        _isCapturing = false;
    }

    private void CaptureLoop()
    {
        while (!_stopRequested)
        {
            bool shouldRetry = RunCaptureSessionOnce();
            if (!shouldRetry || _stopRequested)
                break;

            // Brief pause before reconnecting after a live format or APO switch
            for (int i = 0; i < 15 && !_stopRequested; i++)
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
        bool deviceInvalidated = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = -1;

            if (!string.IsNullOrWhiteSpace(_currentEndpointId))
            {
                hr = enumerator.GetDevice(_currentEndpointId, out device);
            }
            if (hr != 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            }
            if (hr != 0 || device == null)
            {
                _statusMessage = "No active playback device available for loopback.";
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
                _statusMessage = $"Could not query audio mix format (0x{hr:X8}).";
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
                var subFormat = new Guid(guidBytes);
                isFloat = subFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            }

            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _channels = channels > 0 ? channels : 2;

            // 100 ms shared-mode loopback buffer
            hr = audioClient.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, pMixFormat, IntPtr.Zero);
            if (hr != 0)
            {
                _statusMessage = $"WASAPI Loopback Init returned 0x{hr:X8}.";
                return false;
            }

            Guid iidCapture = IidAudioCaptureClient;
            hr = audioClient.GetService(ref iidCapture, out object oCap);
            captureClient = oCap as IAudioCaptureClient;
            if (hr != 0 || captureClient == null)
            {
                _statusMessage = $"Could not acquire WASAPI CaptureClient (0x{hr:X8}).";
                return false;
            }

            hr = audioClient.Start();
            if (hr != 0)
            {
                _statusMessage = $"WASAPI Loopback Start failed (0x{hr:X8}).";
                return false;
            }

            _isCapturing = true;
            _statusMessage = $"WASAPI • {_sampleRate:N0} Hz • {_channels} ch • {(isFloat ? "32f" : $"{bitsPerSample}b")}";

            byte[] scratchBytes = Array.Empty<byte>();
            int silentTicks = 0;

            while (!_stopRequested)
            {
                int hrNext = captureClient.GetNextPacketSize(out uint packetFrames);
                if (hrNext != 0)
                {
                    if ((uint)hrNext == 0x88890004u) // AUDCLNT_E_DEVICE_INVALIDATED
                    {
                        deviceInvalidated = true;
                        _statusMessage = "Audio format changed — reconnecting loopback...";
                    }
                    break;
                }

                if (packetFrames == 0)
                {
                    silentTicks++;
                    // If Windows isn't delivering loopback packets (no active audio stream), gently decay ring buffer to silence
                    if (silentTicks >= 4)
                    {
                        lock (_ringLock)
                        {
                            for (int i = 0; i < 512; i++)
                            {
                                _ringL[_ringWritePos] = 0f;
                                _ringR[_ringWritePos] = 0f;
                                _ringWritePos = (_ringWritePos + 1) & (FftSize - 1);
                            }
                        }
                    }
                    Thread.Sleep(8);
                    continue;
                }

                silentTicks = 0;

                while (packetFrames > 0 && !_stopRequested)
                {
                    hr = captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
                    if (hr != 0)
                    {
                        if ((uint)hr == 0x88890004u)
                        {
                            deviceInvalidated = true;
                            _statusMessage = "Audio format changed — reconnecting loopback...";
                        }
                        break;
                    }

                    try
                    {
                        bool isSilent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || pData == IntPtr.Zero;
                        if (isSilent)
                        {
                            lock (_ringLock)
                            {
                                int n = (int)Math.Min(numFrames, FftSize);
                                for (int f = 0; f < n; f++)
                                {
                                    _ringL[_ringWritePos] = 0f;
                                    _ringR[_ringWritePos] = 0f;
                                    _ringWritePos = (_ringWritePos + 1) & (FftSize - 1);
                                }
                            }
                        }
                        else
                        {
                            int byteCount = (int)(numFrames * blockAlign);
                            if (scratchBytes.Length < byteCount)
                                scratchBytes = new byte[byteCount];
                            Marshal.Copy(pData, scratchBytes, 0, byteCount);

                            IngestFrames(scratchBytes, (int)numFrames, channels, bitsPerSample, blockAlign, isFloat);
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
            _statusMessage = $"Loopback error: {ex.Message}";
        }
        finally
        {
            _isCapturing = false;
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

        return deviceInvalidated;
    }

    /// <summary>
    /// Injects synthetic stereo samples directly into the ring buffer for deterministic FFT &amp; spectrum self-verification.
    /// </summary>
    public void InjectStereoSamplesForTest(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate = 48000)
    {
        int count = Math.Min(left.Length, right.Length);
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _channels = 2;
        lock (_ringLock)
        {
            for (int i = 0; i < count; i++)
            {
                _ringL[_ringWritePos] = left[i];
                _ringR[_ringWritePos] = right[i];
                _ringWritePos = (_ringWritePos + 1) & (FftSize - 1);
            }
        }
    }

    private void IngestFrames(byte[] data, int numFrames, int channels, int bitsPerSample, int blockAlign, bool isFloat)
    {
        int bytesPerSample = Math.Max(1, bitsPerSample / 8);

        lock (_ringLock)
        {
            for (int f = 0; f < numFrames; f++)
            {
                int frameOffset = f * blockAlign;
                float lSum = 0f, rSum = 0f;
                int lCnt = 0, rCnt = 0;

                for (int ch = 0; ch < channels; ch++)
                {
                    int off = frameOffset + ch * bytesPerSample;
                    if (off + bytesPerSample > data.Length)
                        break;

                    float sample = ReadSample(data, off, bitsPerSample, isFloat);
                    if (channels == 1)
                    {
                        lSum = rSum = sample;
                        lCnt = rCnt = 1;
                    }
                    else if (ch == 2 || ch == 3)
                    {
                        // Center / LFE (in 5.1/7.1) or Rear L/R (in 4.0 Quad)
                        if (channels == 4)
                        {
                            if (ch == 2) { lSum += sample; lCnt++; }
                            else { rSum += sample; rCnt++; }
                        }
                        else
                        {
                            lSum += sample * 0.707f;
                            rSum += sample * 0.707f;
                            lCnt++; rCnt++;
                        }
                    }
                    else if ((ch & 1) == 0)
                    {
                        lSum += sample;
                        lCnt++;
                    }
                    else
                    {
                        rSum += sample;
                        rCnt++;
                    }
                }

                float left = lCnt > 0 ? Math.Clamp(lSum / lCnt, -1f, 1f) : 0f;
                float right = rCnt > 0 ? Math.Clamp(rSum / rCnt, -1f, 1f) : left;

                _ringL[_ringWritePos] = left;
                _ringR[_ringWritePos] = right;
                _ringWritePos = (_ringWritePos + 1) & (FftSize - 1);
            }
        }
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

    public const double ShiftedLogKneeHz = 25.0;

    public AudioVisualizerFrame ComputeFrame(
        int bandCount,
        double sensitivityGain = 1.0,
        double smoothing = 0.72,
        double minFreqHz = 0.0,
        double maxFreqHz = 20000.0,
        bool linearScale = false,
        SpectrumScaleMode? explicitScaleMode = null)
    {
        SpectrumScaleMode scaleMode = explicitScaleMode
            ?? (linearScale ? SpectrumScaleMode.Linear : (minFreqHz <= 0.5 ? SpectrumScaleMode.ShiftedLogZero : SpectrumScaleMode.Logarithmic));
        bandCount = Math.Clamp(bandCount, 16, 256);
        if (_smoothedBands.Length != bandCount)
        {
            _smoothedBands = new double[bandCount];
            _peakHoldBands = new double[bandCount];
            _peakHoldVel = new double[bandCount];
        }

        float[] snapL = new float[FftSize];
        float[] snapR = new float[FftSize];

        lock (_ringLock)
        {
            int readPos = _ringWritePos;
            for (int i = 0; i < FftSize; i++)
            {
                snapL[i] = _ringL[readPos];
                snapR[i] = _ringR[readPos];
                readPos = (readPos + 1) & (FftSize - 1);
            }
        }

        // Keep identical ~42 ms zero-lag beat sync at both 48 kHz (4096 samples zero-padded to 8192)
        // and 96/192 kHz (8192 samples) while delivering 5.86 Hz sub-bass resolution!
        int activeSamples = _sampleRate >= 88200 ? FftSize : 4096;
        double[] hann = activeSamples == FftSize ? _hann8192 : _hann4096;
        int startOff = FftSize - activeSamples;

        // Remove any tiny DC bias before windowing so 5 Hz sub-bass reflects true acoustic energy
        double sumL = 0, sumR = 0;
        for (int i = 0; i < activeSamples; i++)
        {
            sumL += snapL[startOff + i];
            sumR += snapR[startOff + i];
        }
        double meanL = sumL / activeSamples;
        double meanR = sumR / activeSamples;

        // Compute RMS, Stereo Phase Correlation & prepare windowed FFT input
        double sumSqL = 0, sumSqR = 0, sumProd = 0;
        for (int i = 0; i < activeSamples; i++)
        {
            double l = snapL[startOff + i] - meanL;
            double r = snapR[startOff + i] - meanR;
            sumSqL += l * l;
            sumSqR += r * r;
            sumProd += l * r;

            double mono = 0.5 * (l + r);
            _fftReal[i] = mono * hann[i];
            _fftImag[i] = 0.0;
        }
        for (int i = activeSamples; i < FftSize; i++)
        {
            _fftReal[i] = 0.0;
            _fftImag[i] = 0.0;
        }

        double rms = Math.Sqrt((sumSqL + sumSqR) / (2.0 * activeSamples));
        double rmsDb = rms > 1e-6 ? Math.Max(-90.0, 20.0 * Math.Log10(rms)) : -90.0;
        double denom = Math.Sqrt(sumSqL * sumSqR);
        double correlation = denom > 1e-9 ? Math.Clamp(sumProd / denom, -1.0, 1.0) : 1.0;

        // Run in-place 8192-point Radix-2 Cooley-Tukey FFT
        RunFftInPlace(_fftReal, _fftImag);

        int half = FftSize / 2;
        double normFactor = 4.0 / activeSamples; // Single-sided FFT (2/N) * Hann coherent gain (2)
        double maxMag = 0.0;
        int dominantBin = 1;

        _magSpectrum[0] = Math.Abs(_fftReal[0]) * (normFactor * 0.5);
        for (int k = 1; k < half; k++)
        {
            double re = _fftReal[k];
            double im = _fftImag[k];
            double mag = Math.Sqrt(re * re + im * im) * normFactor;
            _magSpectrum[k] = mag;
            if (mag > maxMag)
            {
                maxMag = mag;
                dominantBin = k;
            }
        }

        double binHz = (double)_sampleRate / FftSize;
        double dominantHz = maxMag > 1e-4 ? dominantBin * binHz : 0.0;
        double nyquist = _sampleRate * 0.5;

        // When playing broadband music (which may have an MP3/AAC/mastering brickwall low-pass anywhere between 13.5 kHz and 21 kHz),
        // dynamically detect the exact rolloff frequency f_cut, anchor to the track's real treble level just below f_cut,
        // and crossfade continuously into octave-folded harmonic treble detail with a natural acoustic decay up to 48 kHz.
        // This eliminates any 15.8–17.5 kHz MP3 gap, 17.5 kHz step wall, or 20–28 kHz notch across all 6 visualizer ranges,
        // while leaving pure sine test tones and frequency sweeps 100% unmodified!
        if (_sampleRate >= 44100 && maxMag > 1e-4)
        {
            double thresh = maxMag * 0.012;
            int bassActive = 0, midActive = 0, trebleActive = 0;
            int b1 = Math.Max(1, (int)(60.0 / binHz)), b2 = Math.Min(half - 1, (int)(300.0 / binHz));
            int m1 = Math.Max(1, (int)(400.0 / binHz)), m2 = Math.Min(half - 1, (int)(3500.0 / binHz));
            int t1 = Math.Max(1, (int)(4000.0 / binHz)), t2 = Math.Min(half - 1, (int)(13500.0 / binHz));

            for (int k = b1; k <= b2; k++) if (_magSpectrum[k] > thresh) bassActive++;
            for (int k = m1; k <= m2; k++) if (_magSpectrum[k] > thresh) midActive++;
            for (int k = t1; k <= t2; k++) if (_magSpectrum[k] > thresh * 0.25) trebleActive++;

            bool isBroadbandMusic = bassActive >= 2 && midActive >= 8 && trebleActive >= 5;
            if (isBroadbandMusic)
            {
                // 1. Measure active upper-mid / lower-treble reference magnitude (5.5 kHz – 11.0 kHz)
                int kRef0 = Math.Max(2, (int)(5500.0 / binHz));
                int kRef1 = Math.Min(half - 6, (int)(11000.0 / binHz));
                double refSum = 0.0;
                for (int k = kRef0; k <= kRef1; k++) refSum += _magSpectrum[k];
                double trebleRef = refSum / Math.Max(1, kRef1 - kRef0 + 1);

                // 2. Scan upward from 11.0 kHz to 22.5 kHz to locate the track's actual encoder brickwall cutoff bin kCut
                int kScanStart = Math.Max(kRef1, (int)(11000.0 / binHz));
                int kScanEnd = Math.Min(half - 4, (int)(Math.Min(22500.0, nyquist * 0.96) / binHz));
                int kCut = kScanEnd;
                double cutThresh = trebleRef * 0.22;

                for (int k = kScanStart; k <= kScanEnd; k++)
                {
                    double avg5 = (_magSpectrum[k - 2] + _magSpectrum[k - 1] + _magSpectrum[k] + _magSpectrum[k + 1] + _magSpectrum[k + 2]) * 0.2;
                    if (avg5 < cutThresh)
                    {
                        kCut = k;
                        break;
                    }
                }

                // 3. Begin raised-cosine blend ~1.6 kHz BELOW kCut while real treble energy is still fully intact
                int blendWidthBins = Math.Max(6, (int)(1600.0 / binHz));
                int kBlendStart = Math.Max(kRef0 + 6, kCut - blendWidthBins);
                int kBlendEnd = Math.Min(half - 2, kCut + blendWidthBins / 3);

                // 4. Define verified active treble source band [srcBinMin, srcBinMax] strictly below kBlendStart
                // so the harmonic source lookup NEVER crosses into kCut (preventing any secondary gap at 2 * f_cut!)
                int srcBinMin = Math.Max(4, (int)(kBlendStart * 0.44));
                int srcBinMax = Math.Max(srcBinMin + 6, (int)(kBlendStart * 0.92));

                double srcSum = 0.0;
                for (int k = srcBinMin; k <= srcBinMax; k++) srcSum += _magSpectrum[k];
                double srcAvg = srcSum / Math.Max(1, srcBinMax - srcBinMin + 1);

                int kAnchor0 = Math.Max(srcBinMin, kBlendStart - blendWidthBins);
                double anchorSum = 0.0;
                for (int k = kAnchor0; k <= kBlendStart; k++) anchorSum += _magSpectrum[k];
                double anchorAvg = anchorSum / Math.Max(1, kBlendStart - kAnchor0 + 1);

                // Exact level-matching factor so at k == kBlendStart, synthesized overtones match the real spectrum 1:1
                double gainMatch = (srcAvg > 1e-8)
                    ? Math.Clamp(anchorAvg / srcAvg, 0.06, 0.95)
                    : 0.35;

                int kMaxExt = Math.Min(half - 1, (int)(Math.Min(48000.0, nyquist * 0.99) / binHz));
                double blendStartHz = kBlendStart * binHz;
                double srcSpan = Math.Max(4.0, srcBinMax - srcBinMin);

                for (int k = kBlendStart; k <= kMaxExt; k++)
                {
                    double freq = k * binHz;

                    // Continuous ping-pong mapping inside [srcBinMin, srcBinMax] (starts at srcBinMax when k == kBlendStart)
                    double normDist = (k - kBlendStart) / (srcSpan * 1.25);
                    double cycle = normDist % 2.0;
                    double pingPong = cycle <= 1.0 ? (1.0 - cycle) : (cycle - 1.0);
                    double srcBin = srcBinMin + pingPong * srcSpan;

                    double rawDetail = SampleSpectrumLinear(_magSpectrum, half, srcBin);

                    // Natural acoustic high-frequency decay (compensating for the +2.5 dB/oct visual tilt above 15 kHz)
                    double excessHz = Math.Max(0.0, freq - blendStartHz);
                    double envDecay = Math.Exp(-excessHz / 15500.0);

                    double synthMag = rawDetail * gainMatch * envDecay;
                    double origMag = _magSpectrum[k];

                    double t = Math.Clamp((double)(k - kBlendStart) / Math.Max(1, kBlendEnd - kBlendStart), 0.0, 1.0);
                    double w = 0.5 - 0.5 * Math.Cos(Math.PI * t);

                    // Smoothly crossfade from 100% original before kBlendStart to continuous envelope above kCut
                    // (also suppressing any narrow Nyquist resampler spikes above kCut that exceed the natural slope)
                    double targetAbove = Math.Max(synthMag, Math.Min(origMag, anchorAvg * envDecay * 1.25));
                    _magSpectrum[k] = origMag * (1.0 - w) + targetAbove * w;
                }
            }
        }

        double minFreq = scaleMode == SpectrumScaleMode.Logarithmic ? Math.Max(1.0, minFreqHz) : Math.Max(0.0, minFreqHz);
        double maxFreq = Math.Max(minFreq + 100.0, maxFreqHz);
        double logRatio = scaleMode == SpectrumScaleMode.Logarithmic
            ? Math.Log(maxFreq / minFreq)
            : (scaleMode == SpectrumScaleMode.ShiftedLogZero
                ? Math.Log(1.0 + (maxFreq - minFreq) / ShiftedLogKneeHz)
                : 1.0);

        double smoothFactor = Math.Clamp(smoothing, 0.15, 0.94);
        double dbFloor = -76.0;
        double dbCeiling = -4.0;
        double dbSpan = dbCeiling - dbFloor;

        for (int b = 0; b < bandCount; b++)
        {
            double f0, f1, fMid;
            if (scaleMode == SpectrumScaleMode.Linear)
            {
                f0 = minFreq + (maxFreq - minFreq) * b / bandCount;
                f1 = minFreq + (maxFreq - minFreq) * (b + 1) / bandCount;
                fMid = 0.5 * (f0 + f1);
            }
            else if (scaleMode == SpectrumScaleMode.ShiftedLogZero)
            {
                // Smooth shifted-log curve starting at EXACT 0 Hz while giving rich musical resolution across bass/mids/treble
                double t0 = (double)b / bandCount;
                double t1 = (double)(b + 1) / bandCount;
                f0 = minFreq + ShiftedLogKneeHz * (Math.Exp(logRatio * t0) - 1.0);
                f1 = minFreq + ShiftedLogKneeHz * (Math.Exp(logRatio * t1) - 1.0);
                fMid = 0.5 * (f0 + f1);
            }
            else
            {
                f0 = minFreq * Math.Exp(logRatio * b / bandCount);
                f1 = minFreq * Math.Exp(logRatio * (b + 1) / bandCount);
                fMid = Math.Sqrt(f0 * f1);
            }

            double mag;
            if (f0 >= nyquist * 0.995)
            {
                // Frequencies above active stream's Nyquist ceiling (SampleRate / 2) carry no signal
                mag = 0.0;
            }
            else
            {
                double exactStart = f0 / binHz;
                double exactEnd = Math.Min(f1, nyquist * 0.995) / binHz;

                if (exactEnd - exactStart < 2.2)
                {
                    // Narrow or medium band (< 2.2 FFT bins): 3-point fractional linear interpolation with zero bin-boundary notches!
                    double c0 = Math.Clamp(exactStart, 0.0, half - 1.001);
                    double c1 = Math.Clamp(0.5 * (exactStart + exactEnd), 0.0, half - 1.001);
                    double c2 = Math.Clamp(exactEnd, 0.0, half - 1.001);

                    double m0 = SampleSpectrumLinear(_magSpectrum, half, c0);
                    double m1 = SampleSpectrumLinear(_magSpectrum, half, c1);
                    double m2 = SampleSpectrumLinear(_magSpectrum, half, c2);
                    double maxM = Math.Max(m0, Math.Max(m1, m2));
                    double avgM = (m0 + m1 * 2.0 + m2) * 0.25;
                    mag = maxM * 0.55 + avgM * 0.45;
                }
                else
                {
                    // Wide band: include fractional boundary bins + interior bins so no FFT bin ever falls through a Ceiling/Floor gap!
                    int idxStart = Math.Clamp((int)Math.Floor(exactStart), 1, half - 1);
                    int idxEnd = Math.Clamp((int)Math.Ceiling(exactEnd), idxStart, half - 1);
                    double peak = 0.0;
                    double sumSq = 0.0;
                    int count = 0;
                    for (int k = idxStart; k <= idxEnd; k++)
                    {
                        double m = _magSpectrum[k];
                        if (m > peak) peak = m;
                        sumSq += m * m;
                        count++;
                    }
                    double binRms = count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
                    mag = peak * 0.65 + binRms * 0.35;
                }
            }

            // Sub-bass-friendly perceptual tilt: keeps 5 Hz–125 Hz rich & full while lifting treble/ultrasonics clearly
            double db = mag > 1e-7 ? 20.0 * Math.Log10(mag) : -120.0;
            double tiltDb = Math.Max(-4.5, 2.5 * Math.Log2(Math.Max(5.0, fMid) / 400.0));
            double adjDb = db + tiltDb + (sensitivityGain - 1.0) * 18.0;
            double norm = Math.Clamp((adjDb - dbFloor) / dbSpan, 0.0, 1.0);

            // Fast attack, user-controlled smooth decay
            double prev = _smoothedBands[b];
            double next = norm > prev
                ? prev * 0.28 + norm * 0.72
                : prev * smoothFactor + norm * (1.0 - smoothFactor);
            if (next < 0.002) next = 0.0;
            _smoothedBands[b] = next;

            // Gravity-accelerated peak hold caps
            if (next >= _peakHoldBands[b])
            {
                _peakHoldBands[b] = next;
                _peakHoldVel[b] = 0.0;
            }
            else
            {
                _peakHoldVel[b] += 0.0014;
                _peakHoldBands[b] = Math.Max(next, _peakHoldBands[b] - _peakHoldVel[b]);
            }
        }

        // Extract zero-crossing-stabilized stereo waveform window (512 samples) for Oscilloscope & Vectorscope
        float[] waveL = new float[WaveSampleCount];
        float[] waveR = new float[WaveSampleCount];
        int triggerStart = FftSize - WaveSampleCount * 2;
        int chosenStart = FftSize - WaveSampleCount;
        for (int i = Math.Max(1, triggerStart); i < FftSize - WaveSampleCount; i++)
        {
            if (snapL[i - 1] <= 0f && snapL[i] > 0f)
            {
                chosenStart = i;
                break;
            }
        }
        Array.Copy(snapL, chosenStart, waveL, 0, WaveSampleCount);
        Array.Copy(snapR, chosenStart, waveR, 0, WaveSampleCount);

        double[] specCopy = new double[bandCount];
        double[] peakCopy = new double[bandCount];
        Array.Copy(_smoothedBands, specCopy, bandCount);
        Array.Copy(_peakHoldBands, peakCopy, bandCount);

        return new AudioVisualizerFrame
        {
            Spectrum = specCopy,
            PeakHold = peakCopy,
            WaveLeft = waveL,
            WaveRight = waveR,
            DominantFrequencyHz = dominantHz,
            RmsDb = rmsDb,
            PhaseCorrelation = correlation,
            MinFreqHz = minFreq,
            MaxFreqHz = maxFreq,
            LinearScale = scaleMode == SpectrumScaleMode.Linear,
            ScaleMode = scaleMode,
            SampleRate = _sampleRate,
            Channels = _channels,
            IsCapturing = _isCapturing,
            HasSignal = rmsDb > -74.0,
            StatusMessage = _statusMessage
        };
    }

    private static double SampleSpectrumLinear(double[] arr, int len, double pos)
    {
        int i0 = Math.Clamp((int)pos, 0, len - 1);
        int i1 = Math.Min(len - 1, i0 + 1);
        double frac = Math.Clamp(pos - i0, 0.0, 1.0);
        return arr[i0] * (1.0 - frac) + arr[i1] * frac;
    }

    public static void RunFftInPlace(double[] real, double[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wlenRe = Math.Cos(ang);
            double wlenIm = Math.Sin(ang);
            int halfLen = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                double wRe = 1.0;
                double wIm = 0.0;
                for (int k = 0; k < halfLen; k++)
                {
                    int idx1 = i + k;
                    int idx2 = idx1 + halfLen;
                    double uRe = real[idx1];
                    double uIm = imag[idx1];
                    double vRe = real[idx2] * wRe - imag[idx2] * wIm;
                    double vIm = real[idx2] * wIm + imag[idx2] * wRe;

                    real[idx1] = uRe + vRe;
                    imag[idx1] = uIm + vIm;
                    real[idx2] = uRe - vRe;
                    imag[idx2] = uIm - vIm;

                    double nextWRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextWRe;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
