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
