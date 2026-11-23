using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SpectraRealtekStudio.Core.Services;

public enum SpeakerTestChannel
{
    FrontLeft = 0,
    FrontRight = 1,
    Center = 2,
    Subwoofer = 3,
    RearLeft = 4,
    RearRight = 5,
    SideLeft = 6,
    SideRight = 7,
    AllStereoSweep = 8
}

/// <summary>
/// Synthesizes clean harmonic test chimes in-memory and plays them through the active Windows audio engine
/// to verify individual speaker channels, EQ response, and EAX environment reverb.
/// </summary>
public sealed class SpeakerTestToneService
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? pszSound, IntPtr hmod, uint fdwSound);

    private byte[]? _activeWavBuffer;

    public void Stop()
    {
        try
        {
            PlaySound(null, IntPtr.Zero, 0);
        }
        catch
        {
        }
        _activeWavBuffer = null;
    }

    public Task PlayChannelTestToneAsync(SpeakerTestChannel channel)
    {
        return Task.Run(() =>
        {
            double freqHz = channel switch
            {
                SpeakerTestChannel.Subwoofer => 68.0, // Deep LFE bass pulse
                SpeakerTestChannel.Center => 523.25,  // C5 bell chime
                SpeakerTestChannel.FrontLeft => 440.0, // A4 bell chime
                SpeakerTestChannel.FrontRight => 554.37, // C#5 bell chime
                SpeakerTestChannel.RearLeft => 329.63, // E4 bell chime
                SpeakerTestChannel.RearRight => 392.00, // G4 bell chime
                SpeakerTestChannel.SideLeft => 493.88, // B4 bell chime
                SpeakerTestChannel.SideRight => 587.33, // D5 bell chime
                _ => 440.0
            };

            double leftGain = channel switch
            {
                SpeakerTestChannel.FrontLeft or SpeakerTestChannel.RearLeft or SpeakerTestChannel.SideLeft => 1.0,
                SpeakerTestChannel.FrontRight or SpeakerTestChannel.RearRight or SpeakerTestChannel.SideRight => 0.0,
                _ => 0.8
            };

            double rightGain = channel switch
            {
                SpeakerTestChannel.FrontRight or SpeakerTestChannel.RearRight or SpeakerTestChannel.SideRight => 1.0,
                SpeakerTestChannel.FrontLeft or SpeakerTestChannel.RearLeft or SpeakerTestChannel.SideLeft => 0.0,
                _ => 0.8
            };

            _activeWavBuffer = GenerateStereoTestChimeWav(freqHz, 0.70, leftGain, rightGain, channel == SpeakerTestChannel.AllStereoSweep);
            PlaySound(_activeWavBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        });
    }

    /// <summary>
    /// Plays a smooth logarithmic sine sweep from <paramref name="startHz"/> to <paramref name="endHz"/>
    /// to audition full speaker, room, and hi-res DAC frequency response (up to 48 kHz at 96/192 kHz Windows output).
    /// </summary>
    public Task PlayFrequencySweepAsync(double startHz, double endHz, double durationSeconds = 10.0, int sampleRate = 96000)
    {
        return Task.Run(() =>
        {
            int sr = Math.Clamp(sampleRate, 44100, 192000);
            if (endHz > 22000.0 && sr < 96000)
                sr = 96000;
            _activeWavBuffer = GenerateLogFrequencySweepWav(startHz, endHz, durationSeconds, sr);
            PlaySound(_activeWavBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        });
    }

    /// <summary>
    /// Plays a pure clean sine reference tone at any target frequency (5 Hz – 48,000 Hz).
    /// </summary>
    public Task PlayPureToneAsync(double freqHz, double durationSeconds = 2.5, int sampleRate = 96000)
    {
        return Task.Run(() =>
        {
            int sr = Math.Clamp(sampleRate, 44100, 192000);
            if (freqHz > 22000.0 && sr < 96000)
                sr = 96000;
            _activeWavBuffer = GeneratePureToneWav(freqHz, durationSeconds, sr);
            PlaySound(_activeWavBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        });
    }

    /// <summary>
    /// Plays studio-calibrated Pink Noise (-3 dB/octave, flat per octave) or full-bandwidth White Noise.
    /// </summary>
    public Task PlayNoiseAsync(bool pinkNoise, double durationSeconds = 6.0, int sampleRate = 96000)
    {
        return Task.Run(() =>
        {
            int sr = Math.Clamp(sampleRate, 44100, 192000);
            _activeWavBuffer = GenerateNoiseWav(pinkNoise, durationSeconds, sr);
            PlaySound(_activeWavBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        });
    }

    public static byte[] GenerateLogFrequencySweepWav(double startHz, double endHz, double durationSeconds, int sampleRate = 96000)
    {
        double f0 = Math.Clamp(startHz, 1.0, sampleRate * 0.49);
        double f1 = Math.Clamp(endHz, f0 + 1.0, sampleRate * 0.495);
        double dur = Math.Clamp(durationSeconds, 1.0, 30.0);

        int totalFrames = (int)(sampleRate * dur);
        double k = Math.Log(f1 / f0);
        double phaseCoeff = 2.0 * Math.PI * f0 * dur / k;

        return WriteStereoPcm16Wav(sampleRate, totalFrames, (n, _) =>
        {
            double t = (double)n / sampleRate;
            double fadeIn = Math.Min(1.0, t / 0.04);
            double fadeOut = Math.Min(1.0, (dur - t) / 0.06);
            double env = Math.Clamp(fadeIn * fadeOut, 0.0, 1.0);
            double phase = phaseCoeff * (Math.Exp(k * t / dur) - 1.0);
            double s = Math.Sin(phase) * env * 0.42;
            return (s, s);
        });
    }

    public static byte[] GeneratePureToneWav(double freqHz, double durationSeconds, int sampleRate = 96000)
    {
        double f = Math.Clamp(freqHz, 1.0, sampleRate * 0.495);
        double dur = Math.Clamp(durationSeconds, 0.25, 15.0);
        int totalFrames = (int)(sampleRate * dur);

        return WriteStereoPcm16Wav(sampleRate, totalFrames, (n, _) =>
        {
            double t = (double)n / sampleRate;
            double fadeIn = Math.Min(1.0, t / 0.025);
            double fadeOut = Math.Min(1.0, (dur - t) / 0.04);
            double env = Math.Clamp(fadeIn * fadeOut, 0.0, 1.0);
            double s = Math.Sin(2.0 * Math.PI * f * t) * env * 0.42;
            return (s, s);
        });
    }

    public static byte[] GenerateNoiseWav(bool pinkNoise, double durationSeconds, int sampleRate = 96000)
    {
        double dur = Math.Clamp(durationSeconds, 0.5, 20.0);
        int totalFrames = (int)(sampleRate * dur);
        var rng = new Random(1337);

        // Paul Kellet's high-accuracy -3 dB/octave Pink Noise filter state (stereo uncorrelated!)
        double b0L = 0, b1L = 0, b2L = 0, b3L = 0, b4L = 0, b5L = 0, b6L = 0;
        double b0R = 0, b1R = 0, b2R = 0, b3R = 0, b4R = 0, b5R = 0, b6R = 0;

        return WriteStereoPcm16Wav(sampleRate, totalFrames, (n, _) =>
        {
            double t = (double)n / sampleRate;
            double fadeIn = Math.Min(1.0, t / 0.05);
            double fadeOut = Math.Min(1.0, (dur - t) / 0.08);
            double env = Math.Clamp(fadeIn * fadeOut, 0.0, 1.0);

            double wL = rng.NextDouble() * 2.0 - 1.0;
            double wR = rng.NextDouble() * 2.0 - 1.0;

            if (!pinkNoise)
            {
                return (wL * env * 0.22, wR * env * 0.22);
            }

            b0L = 0.99886 * b0L + wL * 0.0555179;
            b1L = 0.99332 * b1L + wL * 0.0750759;
            b2L = 0.96900 * b2L + wL * 0.1538520;
            b3L = 0.86650 * b3L + wL * 0.3104856;
            b4L = 0.55000 * b4L + wL * 0.5329522;
            b5L = -0.7616 * b5L - wL * 0.0168980;
            double pL = (b0L + b1L + b2L + b3L + b4L + b5L + b6L + wL * 0.5362) * 0.06;
            b6L = wL * 0.115926;

            b0R = 0.99886 * b0R + wR * 0.0555179;
            b1R = 0.99332 * b1R + wR * 0.0750759;
            b2R = 0.96900 * b2R + wR * 0.1538520;
            b3R = 0.86650 * b3R + wR * 0.3104856;
            b4R = 0.55000 * b4R + wR * 0.5329522;
            b5R = -0.7616 * b5R - wR * 0.0168980;
            double pR = (b0R + b1R + b2R + b3R + b4R + b5R + b6R + wR * 0.5362) * 0.06;
            b6R = wR * 0.115926;

            return (Math.Clamp(pL, -1.0, 1.0) * env, Math.Clamp(pR, -1.0, 1.0) * env);
        });
    }

    private static byte[] WriteStereoPcm16Wav(int sampleRate, int totalFrames, Func<int, int, (double left, double right)> generator)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        int dataBytes = totalFrames * channels * (bitsPerSample / 8);

        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);

        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);

        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (int n = 0; n < totalFrames; n++)
        {
            var (l, r) = generator(n, totalFrames);
            short sL = (short)Math.Clamp(l * 32767.0, short.MinValue, short.MaxValue);
            short sR = (short)Math.Clamp(r * 32767.0, short.MinValue, short.MaxValue);
            bw.Write(sL);
            bw.Write(sR);
        }

        return ms.ToArray();
    }

    private static byte[] GenerateStereoTestChimeWav(double baseFreqHz, double durationSeconds, double leftGain, double rightGain, bool isChord)
    {
        const int sampleRate = 48000;
        const short channels = 2;
        const short bitsPerSample = 16;
        int totalFrames = (int)(sampleRate * durationSeconds);
        int dataBytes = totalFrames * channels * (bitsPerSample / 8);

        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (int n = 0; n < totalFrames; n++)
        {
            double t = (double)n / sampleRate;
            // Smooth attack (15ms) and exponential decay envelope
            double attack = Math.Min(1.0, t / 0.015);
            double decay = Math.Exp(-3.2 * t / durationSeconds);
            double env = attack * decay;

            double sample;
            if (isChord)
            {
                // Rich A-major arpeggio + sub bass to test full 31Hz..16kHz EQ spectrum
                sample = 0.30 * Math.Sin(2 * Math.PI * 62.0 * t)
                       + 0.30 * Math.Sin(2 * Math.PI * 440.0 * t)
                       + 0.22 * Math.Sin(2 * Math.PI * 554.37 * t)
                       + 0.18 * Math.Sin(2 * Math.PI * 659.25 * t)
                       + 0.12 * Math.Sin(2 * Math.PI * 3520.0 * t);
            }
            else if (baseFreqHz < 100.0)
            {
                // Subwoofer tone with gentle 2nd harmonic
                sample = 0.75 * Math.Sin(2 * Math.PI * baseFreqHz * t)
                       + 0.25 * Math.Sin(2 * Math.PI * (baseFreqHz * 2.0) * t);
            }
            else
            {
                // Studio chime with warm harmonics
                sample = 0.65 * Math.Sin(2 * Math.PI * baseFreqHz * t)
                       + 0.22 * Math.Sin(2 * Math.PI * (baseFreqHz * 2.0) * t)
                       + 0.10 * Math.Sin(2 * Math.PI * (baseFreqHz * 3.0) * t);
            }

            double val = sample * env * 0.72;
            short sL = (short)Math.Clamp(val * leftGain * 32767.0, short.MinValue, short.MaxValue);
            short sR = (short)Math.Clamp(val * rightGain * 32767.0, short.MinValue, short.MaxValue);

            bw.Write(sL);
            bw.Write(sR);
        }

        return ms.ToArray();
    }
}
