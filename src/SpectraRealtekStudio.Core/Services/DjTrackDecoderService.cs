using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

/// <summary>
/// Zero-dependency multi-format DJ Track Decoder, Waveform Pyramid Builder, and Auto-BPM / Musical Key Analyzer.
/// Uses native Windows Media Foundation (mfplat.dll / mfreadwrite.dll IMFSourceReader) + fast direct WAV decoding
/// to load MP3, FLAC, WAV, M4A, AAC, OGG, WMA, and audio streams from MP4/MKV music videos.
/// </summary>
public sealed class DjTrackDecoderService
{
    public static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus", ".aif", ".aiff"
    };

    public static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v"
    };

    private const uint MF_VERSION = 0x00020070u; // Windows 7+ Media Foundation SDK version
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFDu;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFEu;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000001u;
    private const uint MF_SOURCE_READERF_ERROR = 0x00000002u;
    private const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020u;

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly ConcurrentDictionary<string, DjTrackCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheSaveLock = new();
    private readonly string _cacheFilePath;

    public DjTrackDecoderService()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpectraRealtekStudio");
        Directory.CreateDirectory(appData);
        // v2 cache invalidates earlier coarse integer-lag BPM estimates
        _cacheFilePath = Path.Combine(appData, "dj_track_cache_v2.json");
        LoadCacheFromDisk();
    }

    public static bool IsSupportedMediaFile(string path)
    {
        string ext = Path.GetExtension(path);
        return SupportedAudioExtensions.Contains(ext) || SupportedVideoExtensions.Contains(ext);
    }

    public static bool IsVideoFile(string path)
    {
        return SupportedVideoExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>
    /// Scans a directory for supported DJ audio and music-video files, populating cached BPM/Key metadata immediately.
    /// </summary>
    public List<DjLibraryTrackItem> ScanFolder(string folderPath)
    {
        var results = new List<DjLibraryTrackItem>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return results;

        try
        {
            var files = Directory.EnumerateFiles(folderPath)
                .Where(IsSupportedMediaFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                results.Add(CreateLibraryItem(file));
            }
        }
        catch
        {
            // Ignore inaccessible folders
        }

        return results;
    }

    public DjLibraryTrackItem CreateLibraryItem(string filePath)
    {
        var fi = new FileInfo(filePath);
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        ParseFilenameMetadata(fileName, out string artist, out string title, out string remix);

        double bpm = 0.0;
        string key = "—";
        double durationSec = 0.0;
        bool analyzed = false;

        // Read quick ID3v2 tags from MP3 header if available (< 0.2 ms)
        if (string.Equals(fi.Extension, ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            TryReadId3v2Metadata(filePath, ref artist, ref title, ref bpm, ref key);
        }
        else if (string.Equals(fi.Extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            durationSec = TryGetQuickWavDurationSec(filePath);
        }

        if (fi.Exists && _cache.TryGetValue(filePath, out var cached)
            && cached.FileSizeBytes == fi.Length
            && cached.LastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks)
        {
            if (!string.IsNullOrWhiteSpace(cached.Artist)) artist = cached.Artist;
            if (!string.IsNullOrWhiteSpace(cached.Title)) title = cached.Title;
            if (!string.IsNullOrWhiteSpace(cached.Remix)) remix = cached.Remix;
            durationSec = cached.DurationSec;
            bpm = cached.Bpm;
            if (!string.IsNullOrWhiteSpace(cached.MusicalKey)) key = cached.MusicalKey;
            analyzed = true;
        }

        return new DjLibraryTrackItem
        {
            FilePath = filePath,
            FileName = fi.Name,
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            Remix = remix,
            DurationSec = durationSec,
            LengthText = durationSec > 0.1 ? FormatDuration(durationSec) : "--:--",
            Bpm = bpm,
            MusicalKey = key,
            TrackType = IsVideoFile(filePath) ? "video" : "audio",
            IsAnalyzed = analyzed
        };
    }

    /// <summary>
    /// Decodes a media file into stereo float PCM, builds 60 FPS beat-colored waveform pyramids,
    /// and detects BPM, beat phase, and musical key.
    /// When <paramref name="preserveNativeSampleRate"/> is true ("Load As-Is"), keeps the exact native
    /// sample rate (e.g. 192,000 Hz) with zero decimation or downsampling.
    /// </summary>
    public DjLoadedTrack LoadAndAnalyzeTrack(string filePath, bool forceReanalyze = false, bool preserveNativeSampleRate = true)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio track not found.", filePath);

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        ParseFilenameMetadata(fileName, out string artist, out string title, out string remix);
        double tagBpm = 0.0;
        string tagKey = string.Empty;

        string ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            TryReadId3v2Metadata(filePath, ref artist, ref title, ref tagBpm, ref tagKey);
        }

        float[] left;
        float[] right;
        int sampleRate;

        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
            && TryDecodeWavNative(filePath, out left, out right, out sampleRate, preserveNativeSampleRate))
        {
            // Fast native WAV decode succeeded
        }
        else
        {
            DecodeViaMediaFoundation(filePath, out left, out right, out sampleRate, preserveNativeSampleRate);
        }

        int totalFrames = Math.Min(left.Length, right.Length);
        if (totalFrames == 0)
        {
            sampleRate = 48000;
            totalFrames = sampleRate;
            left = new float[totalFrames];
            right = new float[totalFrames];
        }

        // Build overview & 100 Hz beat-colored waveform pyramids + dedicated 200 Hz kick-drum onset envelope
        BuildWaveformPyramids(
            left, right, totalFrames, sampleRate,
            out float[] overviewPeaks, out float[] overviewBass,
            out float[] beatPeaks, out float[] beatBass,
            out float[] kickOnset200Hz);

        // Check cache or run high-precision v2 BPM + Beat Phase + Musical Key analysis
        var fi = new FileInfo(filePath);
        double detectedBpm;
        double firstBeatSec;
        string musicalKey;

        if (!forceReanalyze
            && _cache.TryGetValue(filePath, out var cached)
            && cached.FileSizeBytes == fi.Length
            && cached.LastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks
            && cached.Bpm > 30.0
            && !string.IsNullOrWhiteSpace(cached.MusicalKey))
        {
            detectedBpm = cached.Bpm;
            firstBeatSec = cached.FirstBeatSec;
            musicalKey = cached.MusicalKey;
        }
        else
        {
            AnalyzeBpmFromOnsetEnvelope(kickOnset200Hz, 200, out detectedBpm, out firstBeatSec);
            if (tagBpm >= 60.0 && tagBpm <= 200.0)
                detectedBpm = tagBpm;

            musicalKey = !string.IsNullOrWhiteSpace(tagKey) && tagKey != "—"
                ? tagKey
                : DetectMusicalKey(left, right, totalFrames, sampleRate);

            var entry = new DjTrackCacheEntry
            {
                FilePath = filePath,
                FileSizeBytes = fi.Length,
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
                Remix = remix,
                DurationSec = (double)totalFrames / sampleRate,
                Bpm = detectedBpm,
                FirstBeatSec = firstBeatSec,
                MusicalKey = musicalKey
            };
            _cache[filePath] = entry;
            SaveCacheToDisk();
        }

        long initialCueFrame = Math.Clamp((long)Math.Round(firstBeatSec * sampleRate), 0L, Math.Max(0, totalFrames - 1));

        return new DjLoadedTrack
        {
            FilePath = filePath,
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            Remix = remix,
            SampleRate = sampleRate,
            TotalFrames = totalFrames,
            Left = left,
            Right = right,
            DetectedBpm = detectedBpm,
            FirstBeatSec = firstBeatSec,
            MusicalKey = musicalKey,
            OverviewPeaks = overviewPeaks,
            OverviewBassRatio = overviewBass,
            BeatPeaks = beatPeaks,
            BeatBassRatio = beatBass,
            CueFrame = initialCueFrame
        };
    }

    // =========================================================================
    // NATIVE FAST WAV DECODER (16-BIT, 24-BIT, 32-BIT PCM & 32-BIT IEEE FLOAT)
    // =========================================================================

    private static double TryGetQuickWavDurationSec(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 44) return 0.0;
            if (br.ReadUInt32() != 0x46464952u) return 0.0; // "RIFF"
            br.ReadUInt32();
            if (br.ReadUInt32() != 0x45564157u) return 0.0; // "WAVE"

            int byteRate = 0;
            long dataBytes = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                uint chunkId = br.ReadUInt32();
                uint chunkSize = br.ReadUInt32();
                long nextPos = fs.Position + chunkSize + (chunkSize & 1);
                if (chunkId == 0x20746D66u && chunkSize >= 16) // "fmt "
                {
                    br.ReadUInt16();
                    br.ReadUInt16();
                    br.ReadInt32();
                    byteRate = br.ReadInt32();
                }
                else if (chunkId == 0x61746164u) // "data"
                {
                    dataBytes = chunkSize;
                    break;
                }
                fs.Position = Math.Min(fs.Length, nextPos);
            }
            return byteRate > 0 ? (double)dataBytes / byteRate : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    public static bool TryDecodeWavNative(string filePath, out float[] left, out float[] right, out int sampleRate, bool preserveNativeSampleRate = true)
    {
        left = Array.Empty<float>();
        right = Array.Empty<float>();
        sampleRate = 48000;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 44)
                return false;

            if (br.ReadUInt32() != 0x46464952u) // "RIFF"
                return false;
            br.ReadUInt32();
            if (br.ReadUInt32() != 0x45564157u) // "WAVE"
                return false;

            ushort formatTag = 0;
            ushort channels = 2;
            int blockAlign = 0;
            int bitsPerSample = 16;
            bool isFloat = false;
            long dataOffset = -1;
            long dataSize = 0;

            while (fs.Position + 8 <= fs.Length)
            {
                uint chunkId = br.ReadUInt32();
                uint chunkSize = br.ReadUInt32();
                long chunkStart = fs.Position;

                if (chunkId == 0x20746D66u && chunkSize >= 16) // "fmt "
                {
                    formatTag = br.ReadUInt16();
                    channels = br.ReadUInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byteRate
                    blockAlign = br.ReadUInt16();
                    bitsPerSample = br.ReadUInt16();
                    isFloat = formatTag == 3;

                    if (formatTag == 0xFFFE && chunkSize >= 40) // WAVE_FORMAT_EXTENSIBLE
                    {
                        br.ReadUInt16(); // cbSize
                        ushort validBits = br.ReadUInt16();
                        br.ReadUInt32(); // channelMask
                        byte[] subFormat = br.ReadBytes(16);
                        var subGuid = new Guid(subFormat);
                        isFloat = subGuid == MFAudioFormat_Float;
                        if (bitsPerSample == 0 && validBits > 0)
                            bitsPerSample = validBits;
                    }
                }
                else if (chunkId == 0x61746164u) // "data"
                {
                    dataOffset = chunkStart;
                    dataSize = Math.Min((long)chunkSize, fs.Length - chunkStart);
                    break;
                }

                long next = chunkStart + chunkSize + (chunkSize & 1);
                if (next <= chunkStart || next > fs.Length)
                    break;
                fs.Position = next;
            }

            if (dataOffset < 0 || channels < 1 || sampleRate <= 0)
                return false;

            // Only decode uncompressed PCM (1), IEEE Float (3), or WAVE_FORMAT_EXTENSIBLE (0xFFFE) natively;
            // let Media Foundation handle any compressed WAV wrapper (e.g. ADPCM / MP3-in-WAV)
            if (formatTag != 1 && formatTag != 3 && formatTag != 0xFFFE)
                return false;

            int bytesPerSample = (blockAlign > 0 && channels > 0 && (blockAlign % channels) == 0)
                ? (blockAlign / channels)
                : Math.Max(1, bitsPerSample / 8);
            int frameSize = blockAlign >= channels * bytesPerSample ? blockAlign : (bytesPerSample * channels);
            if (frameSize <= 0)
                return false;

            long rawTotalFrames = dataSize / frameSize;
            // Cap to 25 minutes max
            rawTotalFrames = Math.Min(rawTotalFrames, (long)sampleRate * 60 * 25);
            if (rawTotalFrames <= 0)
                return false;

            // When preserveNativeSampleRate ("Load As-Is") is true, keep decim = 1 (100% original sample rate & bit-exact samples!).
            // When false ("48 kHz RAM-Saver"), decimate 96 kHz (2:1) or 192 kHz (4:1) down to 48 kHz.
            int decim = preserveNativeSampleRate
                ? 1
                : (sampleRate >= 176400 ? 4 : (sampleRate >= 88200 ? 2 : 1));
            int outFrames = (int)(rawTotalFrames / decim);
            int outRate = sampleRate / decim;

            left = new float[outFrames];
            right = new float[outFrames];

            fs.Position = dataOffset;
            int framesPerChunk = 8192 * decim;
            byte[] buf = new byte[framesPerChunk * frameSize];
            int outIdx = 0;
            bool checkedFloatAutoDetect = isFloat || bytesPerSample != 4;

            while (outIdx < outFrames)
            {
                int framesToRead = Math.Min(framesPerChunk, (outFrames - outIdx) * decim);
                int bytesToRead = framesToRead * frameSize;
                int bytesRead = fs.Read(buf, 0, bytesToRead);
                if (bytesRead < frameSize * decim)
                    break;

                // Safety auto-detect for 32-bit IEEE Float WAVs that omitted formatTag=3 in non-standard headers
                if (!checkedFloatAutoDetect && bytesRead >= 64)
                {
                    checkedFloatAutoDetect = true;
                    isFloat = LooksLikeIeeeFloat32(buf, bytesRead, frameSize);
                }

                int decimatedChunkFrames = bytesRead / (frameSize * decim);
                int pos = 0;

                if (decim == 1)
                {
                    for (int i = 0; i < decimatedChunkFrames && outIdx < outFrames; i++)
                    {
                        float l = ReadSampleValue(buf, pos, bytesPerSample, isFloat);
                        float r = channels >= 2
                            ? ReadSampleValue(buf, pos + bytesPerSample, bytesPerSample, isFloat)
                            : l;
                        left[outIdx] = l;
                        right[outIdx] = r;
                        outIdx++;
                        pos += frameSize;
                    }
                }
                else
                {
                    float invDecim = 1.0f / decim;
                    for (int i = 0; i < decimatedChunkFrames && outIdx < outFrames; i++)
                    {
                        float sumL = 0f;
                        float sumR = 0f;
                        for (int d = 0; d < decim; d++)
                        {
                            float sL = ReadSampleValue(buf, pos, bytesPerSample, isFloat);
                            float sR = channels >= 2
                                ? ReadSampleValue(buf, pos + bytesPerSample, bytesPerSample, isFloat)
                                : sL;
                            sumL += sL;
                            sumR += sR;
                            pos += frameSize;
                        }
                        left[outIdx] = sumL * invDecim;
                        right[outIdx] = sumR * invDecim;
                        outIdx++;
                    }
                }
            }

            sampleRate = outRate;
            return outIdx > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeIeeeFloat32(byte[] buf, int bytesRead, int frameSize)
    {
        int floatValid = 0;
        int checkedCount = 0;
        for (int off = 0; off + 4 <= Math.Min(bytesRead, 512); off += frameSize)
        {
            uint u = BitConverter.ToUInt32(buf, off);
            if (u == 0) continue;
            checkedCount++;
            float f = BitConverter.ToSingle(buf, off);
            if (float.IsFinite(f) && Math.Abs(f) >= 1e-6f && Math.Abs(f) <= 1.25f)
                floatValid++;
        }
        return checkedCount >= 4 && floatValid >= (checkedCount * 3) / 4;
    }

    private static float ReadSampleValue(byte[] buf, int offset, int bytesPerSample, bool isFloat)
    {
        if (offset + bytesPerSample > buf.Length)
            return 0f;

        if (isFloat && bytesPerSample == 4)
        {
            float v = BitConverter.ToSingle(buf, offset);
            return float.IsFinite(v) ? Math.Clamp(v, -1.2f, 1.2f) : 0f;
        }

        if (bytesPerSample == 2)
        {
            short s = BitConverter.ToInt16(buf, offset);
            return s / 32768f;
        }

        if (bytesPerSample == 3)
        {
            int s24 = (buf[offset] << 8) | (buf[offset + 1] << 16) | (buf[offset + 2] << 24);
            return (s24 >> 8) / 8388608f;
        }

        if (bytesPerSample == 4)
        {
            int s32 = BitConverter.ToInt32(buf, offset);
            return s32 / 2147483648f;
        }

        if (bytesPerSample == 1)
        {
            return (buf[offset] - 128) / 128f;
        }

        return 0f;
    }

    // =========================================================================
    // WINDOWS MEDIA FOUNDATION DECODER (MP3, FLAC, M4A, AAC, OGG, WMA, MP4/MKV)
    // =========================================================================

    public static RecordingBitFormat DetectWavBitFormat(string filePath)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(filePath), ".wav", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(filePath))
            {
                return RecordingBitFormat.Pcm24;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 44)
                return RecordingBitFormat.Pcm24;

            if (br.ReadUInt32() != 0x46464952u) return RecordingBitFormat.Pcm24;
            br.ReadUInt32();
            if (br.ReadUInt32() != 0x45564157u) return RecordingBitFormat.Pcm24;

            while (fs.Position + 8 <= fs.Length)
            {
                uint chunkId = br.ReadUInt32();
                uint chunkSize = br.ReadUInt32();
                long chunkStart = fs.Position;

                if (chunkId == 0x20746D66u && chunkSize >= 16) // "fmt "
                {
                    ushort formatTag = br.ReadUInt16();
                    br.ReadUInt16(); // channels
                    br.ReadInt32(); // sampleRate
                    br.ReadInt32(); // byteRate
                    br.ReadUInt16(); // blockAlign
                    ushort bitsPerSample = br.ReadUInt16();
                    bool isFloat = formatTag == 3;
                    if (formatTag == 0xFFFE && chunkSize >= 40)
                    {
                        br.ReadUInt16();
                        br.ReadUInt16();
                        br.ReadUInt32();
                        var subGuid = new Guid(br.ReadBytes(16));
                        isFloat = subGuid == MFAudioFormat_Float;
                    }

                    if (isFloat && bitsPerSample == 32)
                        return RecordingBitFormat.Float32;
                    if (bitsPerSample == 16)
                        return RecordingBitFormat.Pcm16;
                    return RecordingBitFormat.Pcm24;
                }

                long next = chunkStart + chunkSize + (chunkSize & 1);
                if (next <= chunkStart || next > fs.Length)
                    break;
                fs.Position = next;
            }
        }
        catch
        {
        }

        return RecordingBitFormat.Pcm24;
    }

    public static void DecodeViaMediaFoundation(string filePath, out float[] left, out float[] right, out int sampleRate, bool preserveNativeSampleRate = true)
    {
        sampleRate = 48000;
        IntPtr pReader = IntPtr.Zero;
        IntPtr pTargetType = IntPtr.Zero;
        IntPtr pCurrentType = IntPtr.Zero;

        int hrStart = MFStartup(MF_VERSION, 0);
        if (hrStart < 0)
            throw new InvalidOperationException($"Media Foundation initialization failed (0x{hrStart:X8}).");

        try
        {
            int hr = MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out pReader);
            if (hr < 0 || pReader == IntPtr.Zero)
                throw new InvalidOperationException($"Could not open media file '{Path.GetFileName(filePath)}' (0x{hr:X8}).");

            var reader = (IMFSourceReader)Marshal.GetObjectForIUnknown(pReader);

            // Deselect all streams and select only the first audio stream (works for both audio files & music videos!)
            reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, 0);
            hr = reader.SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 1);
            if (hr < 0)
                throw new InvalidOperationException($"No decodable audio stream found in '{Path.GetFileName(filePath)}'.");

            // Request decoded IEEE Float (or fallback to 16-bit PCM)
            hr = MFCreateMediaType(out pTargetType);
            var targetAttr = (IMFAttributes)Marshal.GetObjectForIUnknown(pTargetType);
            Guid majorAudio = MFMediaType_Audio;
            Guid subFloat = MFAudioFormat_Float;
            Guid keyMajor = MF_MT_MAJOR_TYPE;
            Guid keySub = MF_MT_SUBTYPE;
            targetAttr.SetGUID(ref keyMajor, ref majorAudio);
            targetAttr.SetGUID(ref keySub, ref subFloat);

            bool outputIsFloat = true;
            hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pTargetType);
            if (hr < 0)
            {
                outputIsFloat = false;
                Guid subPcm = MFAudioFormat_PCM;
                targetAttr.SetGUID(ref keySub, ref subPcm);
                hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pTargetType);
                if (hr < 0)
                    throw new InvalidOperationException($"Unsupported audio encoding in '{Path.GetFileName(filePath)}' (0x{hr:X8}).");
            }

            hr = reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, out pCurrentType);
            int channels = 2;
            int bitsPerSample = outputIsFloat ? 32 : 16;
            if (hr >= 0 && pCurrentType != IntPtr.Zero)
            {
                var curAttr = (IMFAttributes)Marshal.GetObjectForIUnknown(pCurrentType);
                Guid keyCh = MF_MT_AUDIO_NUM_CHANNELS;
                Guid keyRate = MF_MT_AUDIO_SAMPLES_PER_SECOND;
                Guid keyBits = MF_MT_AUDIO_BITS_PER_SAMPLE;
                if (curAttr.GetUINT32(ref keyCh, out uint chVal) >= 0 && chVal >= 1)
                    channels = (int)chVal;
                if (curAttr.GetUINT32(ref keyRate, out uint rateVal) >= 0 && rateVal >= 8000)
                    sampleRate = (int)rateVal;
                if (curAttr.GetUINT32(ref keyBits, out uint bitsVal) >= 0 && bitsVal >= 8)
                    bitsPerSample = (int)bitsVal;
            }

            int decim = preserveNativeSampleRate
                ? 1
                : (sampleRate >= 176400 ? 4 : (sampleRate >= 88200 ? 2 : 1));
            int outSampleRate = sampleRate / decim;
            int maxFrames = outSampleRate * 60 * 20; // Up to 20 minutes per track
            var listL = new List<float>(outSampleRate * 240);
            var listR = new List<float>(outSampleRate * 240);
            int bytesPerSample = Math.Max(2, bitsPerSample / 8);
            int frameBytes = bytesPerSample * channels;

            byte[] scratch = new byte[65536];
            int nullSampleCount = 0;
            int decimCounter = 0;

            while (listL.Count < maxFrames)
            {
                hr = reader.ReadSample(
                    MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                    0,
                    out _,
                    out uint streamFlags,
                    out _,
                    out IntPtr pSample);

                if (hr < 0 || (streamFlags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (pSample != IntPtr.Zero) Marshal.Release(pSample);
                    break;
                }

                if (pSample == IntPtr.Zero)
                {
                    if (++nullSampleCount > 64)
                        break;
                    continue;
                }
                nullSampleCount = 0;

                try
                {
                    IntPtr pBuffer = ConvertSampleToContiguousBuffer(pSample);
                    if (pBuffer != IntPtr.Zero)
                    {
                        try
                        {
                            var mediaBuf = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(pBuffer);
                            if (mediaBuf.Lock(out IntPtr pData, out _, out uint cbCurrent) >= 0 && pData != IntPtr.Zero)
                            {
                                try
                                {
                                    int byteCount = (int)cbCurrent;
                                    if (scratch.Length < byteCount)
                                        scratch = new byte[byteCount];
                                    Marshal.Copy(pData, scratch, 0, byteCount);

                                    int frames = byteCount / frameBytes;
                                    int offset = 0;
                                    for (int i = 0; i < frames; i++)
                                    {
                                        if (decim == 1 || (decimCounter++ % decim) == 0)
                                        {
                                            float l = ReadSampleValue(scratch, offset, bytesPerSample, outputIsFloat);
                                            float r = channels >= 2
                                                ? ReadSampleValue(scratch, offset + bytesPerSample, bytesPerSample, outputIsFloat)
                                                : l;
                                            listL.Add(l);
                                            listR.Add(r);
                                        }
                                        offset += frameBytes;
                                    }
                                }
                                finally
                                {
                                    mediaBuf.Unlock();
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(pBuffer);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(pSample);
                }
            }

            sampleRate = outSampleRate;
            left = listL.ToArray();
            right = listR.ToArray();
        }
        finally
        {
            if (pCurrentType != IntPtr.Zero) Marshal.Release(pCurrentType);
            if (pTargetType != IntPtr.Zero) Marshal.Release(pTargetType);
            if (pReader != IntPtr.Zero) Marshal.Release(pReader);
            MFShutdown();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferDelegate(IntPtr pThis, out IntPtr ppBuffer);

    private static IntPtr ConvertSampleToContiguousBuffer(IntPtr pSample)
    {
        // IMFSample inherits IMFAttributes (33 methods: 0..2 IUnknown + 3..32 IMFAttributes).
        // Slot 41 is IMFSample::ConvertToContiguousBuffer(IMFMediaBuffer** ppBuffer).
        IntPtr vtbl = Marshal.ReadIntPtr(pSample);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, 41 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<ConvertToContiguousBufferDelegate>(fnPtr);
        int hr = fn(pSample, out IntPtr pBuffer);
        return hr >= 0 ? pBuffer : IntPtr.Zero;
    }

    // =========================================================================
    // WAVEFORM PYRAMIDS, AUTO-BPM & BEAT-GRID DETECTOR, AND MUSICAL KEY FINDER
    // =========================================================================

    private static void BuildWaveformPyramids(
        float[] left,
        float[] right,
        int totalFrames,
        int sampleRate,
        out float[] overviewPeaks,
        out float[] overviewBass,
        out float[] beatPeaks,
        out float[] beatBass,
        out float[] kickOnset200Hz)
    {
        int ovBins = DjLoadedTrack.OverviewBinCount;
        overviewPeaks = new float[ovBins];
        overviewBass = new float[ovBins];

        int framesPerBeatBin = Math.Max(1, sampleRate / DjLoadedTrack.BeatEnvelopeRateHz);
        int beatBins = Math.Max(1, (totalFrames + framesPerBeatBin - 1) / framesPerBeatBin);
        beatPeaks = new float[beatBins];
        beatBass = new float[beatBins];

        // Dedicated 200 Hz (5 ms resolution) isolated sub-bass kick-drum + percussion transient envelope
        const int onsetRateHz = 200;
        int framesPerOnsetBin = Math.Max(1, sampleRate / onsetRateHz);
        int onsetBins = Math.Max(1, totalFrames / framesPerOnsetBin);
        float[] rawKickEnv = new float[onsetBins];
        float[] rawPercEnv = new float[onsetBins];
        kickOnset200Hz = new float[onsetBins];

        // 2-pole 130 Hz low-pass filter to isolate true 4/4 kick drum punch (ignoring brickwall synth/vocal saturation)
        int pyrStride = Math.Max(2, sampleRate / 24000);
        double effRate = (double)sampleRate / pyrStride;
        double aKick = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * 130.0 / Math.Max(4000.0, effRate)), 0.004, 0.25);
        double aSub = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * 35.0 / Math.Max(4000.0, effRate)), 0.001, 0.08);
        double lpKick1 = 0.0, lpKick2 = 0.0, lpSub = 0.0;
        double prevMono = 0.0;
        float globalMax = 0.01f;

        for (int b = 0; b < beatBins; b++)
        {
            int start = b * framesPerBeatBin;
            int end = Math.Min(totalFrames, start + framesPerBeatBin);
            int mid = start + framesPerBeatBin / 2;
            float peak = 0f;
            double totalEnergy = 1e-9;
            double bassEnergy = 0.0;
            double kickSum0 = 0.0, kickSum1 = 0.0;
            double percSum0 = 0.0, percSum1 = 0.0;
            int c0 = 0, c1 = 0;

            for (int i = start; i < end; i += pyrStride)
            {
                float mono = (left[i] + right[i]) * 0.5f;
                float abs = Math.Abs(mono);
                if (abs > peak) peak = abs;

                lpKick1 += aKick * (mono - lpKick1);
                lpKick2 += aKick * (lpKick1 - lpKick2);
                lpSub += aSub * (lpKick2 - lpSub);
                double kickBand = lpKick2 - lpSub; // 35 Hz – 130 Hz isolated kick-drum band
                double highDiff = mono - prevMono;
                prevMono = mono;

                totalEnergy += mono * mono;
                bassEnergy += lpKick1 * lpKick1;

                if (i < mid)
                {
                    kickSum0 += kickBand * kickBand;
                    percSum0 += highDiff * highDiff;
                    c0++;
                }
                else
                {
                    kickSum1 += kickBand * kickBand;
                    percSum1 += highDiff * highDiff;
                    c1++;
                }
            }

            beatPeaks[b] = peak;
            beatBass[b] = (float)Math.Clamp(Math.Sqrt(bassEnergy / totalEnergy), 0.0, 1.0);
            if (peak > globalMax) globalMax = peak;

            int oIdx0 = b * 2;
            if (oIdx0 < onsetBins && c0 > 0)
            {
                rawKickEnv[oIdx0] = (float)Math.Sqrt(kickSum0 / c0);
                rawPercEnv[oIdx0] = (float)Math.Sqrt(percSum0 / c0);
            }
            int oIdx1 = oIdx0 + 1;
            if (oIdx1 < onsetBins && c1 > 0)
            {
                rawKickEnv[oIdx1] = (float)Math.Sqrt(kickSum1 / c1);
                rawPercEnv[oIdx1] = (float)Math.Sqrt(percSum1 / c1);
            }
        }

        // Convert 200 Hz kick & percussion energy into half-wave rectified log-derivative onset strength
        for (int i = 1; i < onsetBins; i++)
        {
            float dk = MathF.Log(1f + 28f * rawKickEnv[i]) - MathF.Log(1f + 28f * rawKickEnv[i - 1]);
            float dp = MathF.Log(1f + 16f * rawPercEnv[i]) - MathF.Log(1f + 16f * rawPercEnv[i - 1]);
            float flux = (dk > 0f ? dk * 0.82f : 0f) + (dp > 0f ? dp * 0.18f : 0f);
            kickOnset200Hz[i] = flux;
        }

        // Normalize beat peaks to 0..1
        float invMax = 1.0f / globalMax;
        for (int b = 0; b < beatBins; b++)
        {
            beatPeaks[b] = Math.Clamp(beatPeaks[b] * invMax, 0f, 1f);
        }

        // Downsample beatBins into 360 overview bins
        for (int i = 0; i < ovBins; i++)
        {
            int b0 = (int)((long)i * beatBins / ovBins);
            int b1 = Math.Max(b0 + 1, (int)((long)(i + 1) * beatBins / ovBins));
            b1 = Math.Min(beatBins, b1);

            float maxP = 0f;
            float sumBass = 0f;
            for (int b = b0; b < b1; b++)
            {
                if (beatPeaks[b] > maxP) maxP = beatPeaks[b];
                sumBass += beatBass[b];
            }
            overviewPeaks[i] = maxP;
            overviewBass[i] = sumBass / Math.Max(1, b1 - b0);
        }
    }

    /// <summary>
    /// Legacy entry point kept for unit test compatibility; delegates to continuous fractional-period comb analyzer.
    /// </summary>
    public static void AnalyzeBpmAndBeatGrid(
        float[] beatPeaks,
        float[] beatBass,
        int envRateHz,
        out double detectedBpm,
        out double firstBeatSec)
    {
        int n = beatPeaks.Length;
        float[] onset = new float[n];
        for (int i = 1; i < n; i++)
        {
            float curr = beatPeaks[i] * (0.35f + 0.65f * beatBass[i]);
            float prev = beatPeaks[i - 1] * (0.35f + 0.65f * beatBass[i - 1]);
            float diff = curr - prev;
            onset[i] = diff > 0f ? diff : 0f;
        }
        AnalyzeBpmFromOnsetEnvelope(onset, envRateHz, out detectedBpm, out firstBeatSec);
    }

    /// <summary>
    /// High-precision continuous fractional-lag multi-beat comb filter (1T, 2T, 4T, 8T) BPM &amp; beat-phase detector.
    /// Eliminates integer-bin lag quantization (e.g. 6000/44 = 136.36 BPM) and 2/3x triplet sub-harmonic false positives (88.2 BPM).
    /// </summary>
    public static void AnalyzeBpmFromOnsetEnvelope(
        float[] rawOnset,
        int envRateHz,
        out double detectedBpm,
        out double firstBeatSec)
    {
        detectedBpm = 128.0;
        firstBeatSec = 0.0;
        int n = rawOnset.Length;
        if (n < envRateHz * 4)
            return;

        // 3-tap Gaussian smoothing so fractional-period interpolation across 1T..8T has smooth gradients
        float[] onset = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            onset[i] = rawOnset[i - 1] * 0.25f + rawOnset[i] * 0.50f + rawOnset[i + 1] * 0.25f;
        }

        // Pick up to 3 high-energy rhythmic windows (18 seconds each) from the core body of the track,
        // automatically skipping quiet intros, outros, and beatless atmospheric breakdowns!
        int winBins = Math.Min(n, envRateHz * 18);
        int bodyStart = n > envRateHz * 45 ? n / 8 : 0;
        int bodyEnd = n > envRateHz * 45 ? (n * 7) / 8 : n;
        int stepWin = Math.Max(envRateHz * 6, (bodyEnd - bodyStart - winBins) / 8);

        var candidateWindows = new List<(int start, double energy)>();
        for (int s = bodyStart; s + winBins <= bodyEnd; s += Math.Max(1, stepWin))
        {
            double e = 0.0;
            for (int i = s; i < s + winBins; i++) e += onset[i] * onset[i];
            candidateWindows.Add((s, e));
        }
        if (candidateWindows.Count == 0)
        {
            candidateWindows.Add((0, 1.0));
        }

        var activeWindows = candidateWindows
            .OrderByDescending(w => w.energy)
            .Take(3)
            .Select(w => w.start)
            .ToArray();

        // Stage 1: Evaluate continuous candidate BPMs from 78.0 to 172.0 in 0.25 BPM steps
        const double minBpm = 78.0;
        const double maxBpm = 172.0;
        const double coarseStep = 0.25;
        int numCandidates = (int)Math.Round((maxBpm - minBpm) / coarseStep) + 1;
        double[] scores = new double[numCandidates];

        for (int c = 0; c < numCandidates; c++)
        {
            double bpm = minBpm + c * coarseStep;
            scores[c] = EvaluateMultiBeatCombScore(onset, envRateHz, activeWindows, winBins, bpm);
        }

        int bestIdx = 0;
        double bestScore = double.NegativeInfinity;
        for (int c = 0; c < numCandidates; c++)
        {
            if (scores[c] > bestScore)
            {
                bestScore = scores[c];
                bestIdx = c;
            }
        }

        double coarseBpm = minBpm + bestIdx * coarseStep;

        // Guard against 2/3x dotted-note sub-harmonic (e.g., 88.0 BPM for a 132.0 BPM track, or 84.0 for 126.0 BPM)
        // and 1/2x half-tempo sub-harmonic (e.g., 70.0 BPM for 140.0 BPM)
        if (coarseBpm < 108.0)
        {
            double bpm15 = coarseBpm * 1.5; // 3/2x (e.g. 88.0 -> 132.0 BPM)
            if (bpm15 >= 116.0 && bpm15 <= 165.0)
            {
                double score15 = EvaluateMultiBeatCombScore(onset, envRateHz, activeWindows, winBins, bpm15);
                if (score15 >= bestScore * 0.72)
                {
                    coarseBpm = bpm15;
                    bestScore = score15;
                }
            }

            double bpm20 = coarseBpm * 2.0; // 2x
            if (coarseBpm < 92.0 && bpm20 >= 118.0 && bpm20 <= 172.0)
            {
                double score20 = EvaluateMultiBeatCombScore(onset, envRateHz, activeWindows, winBins, bpm20);
                if (score20 >= bestScore * 0.68)
                {
                    coarseBpm = bpm20;
                }
            }
        }

        // Stage 2: Fine 0.05 BPM refinement around winning tempo
        double refinedBpm = coarseBpm;
        double bestFineScore = double.NegativeInfinity;
        for (double b = coarseBpm - 0.60; b <= coarseBpm + 0.60; b += 0.05)
        {
            double sc = EvaluateMultiBeatCombScore(onset, envRateHz, activeWindows, winBins, b);
            if (sc > bestFineScore)
            {
                bestFineScore = sc;
                refinedBpm = b;
            }
        }

        // Snap cleanly to integer BPM if within 0.30 BPM (99% of studio club tracks are produced at whole integer BPMs)
        double nearestInt = Math.Round(refinedBpm);
        double nearestHalf = Math.Round(refinedBpm * 2.0) / 2.0;
        if (Math.Abs(refinedBpm - nearestInt) <= 0.30)
            detectedBpm = nearestInt;
        else if (Math.Abs(refinedBpm - nearestHalf) <= 0.14)
            detectedBpm = nearestHalf;
        else
            detectedBpm = Math.Round(refinedBpm, 1);

        // Stage 3: Find exact beat-grid phase offset in [0, beatPeriodSec)
        double exactPeriodBins = 60.0 * envRateHz / detectedBpm;
        int phaseSteps = 60;
        double bestPhaseScore = -1.0;
        double bestPhaseBins = 0.0;
        int primaryWinStart = activeWindows[0];

        for (int p = 0; p < phaseSteps; p++)
        {
            double phaseOffset = (p * exactPeriodBins) / phaseSteps;
            double pSum = 0.0;
            for (double pos = primaryWinStart + phaseOffset; pos < primaryWinStart + winBins - 2; pos += exactPeriodBins)
            {
                pSum += SampleLinear(onset, pos);
            }
            if (pSum > bestPhaseScore)
            {
                bestPhaseScore = pSum;
                bestPhaseBins = (primaryWinStart + phaseOffset) % exactPeriodBins;
            }
        }

        firstBeatSec = bestPhaseBins / envRateHz;
    }

    private static double EvaluateMultiBeatCombScore(
        float[] onset,
        int envRateHz,
        int[] windowStarts,
        int winBins,
        double candidateBpm)
    {
        double period = 60.0 * envRateHz / candidateBpm;
        double maxLag = period * 8.0;
        double totalSum = 0.0;
        int totalSamples = 0;

        foreach (int start in windowStarts)
        {
            int limit = (int)(start + winBins - maxLag - 2);
            if (limit <= start)
                limit = (int)(start + winBins - period * 4.0 - 2);
            if (limit <= start)
                continue;

            for (int i = start; i < limit; i++)
            {
                float baseVal = onset[i];
                if (baseVal <= 1e-4f)
                    continue;

                // Comb pulse correlation at 1 beat, 2 beats, 4 beats (1 bar), and 8 beats (2 bars)
                double c1 = SampleLinear(onset, i + period);
                double c2 = SampleLinear(onset, i + period * 2.0);
                double c4 = SampleLinear(onset, i + period * 4.0);
                double c8 = (i + maxLag < onset.Length - 2) ? SampleLinear(onset, i + maxLag) : c4;

                totalSum += baseVal * (1.00 * c1 + 0.85 * c2 + 0.95 * c4 + 0.75 * c8);
                totalSamples++;
            }
        }

        if (totalSamples == 0)
            return 0.0;

        // Gentle prior favoring standard club/dance range (118–145 BPM) over extreme sub/super-harmonics
        double prior = 1.0 + 0.14 * Math.Exp(-Math.Pow((candidateBpm - 130.0) / 26.0, 2.0));
        return (totalSum / Math.Max(1, windowStarts.Length)) * prior;
    }

    private static double SampleLinear(float[] arr, double idx)
    {
        int i0 = (int)idx;
        if (i0 < 0 || i0 >= arr.Length - 1)
            return 0.0;
        double frac = idx - i0;
        return arr[i0] * (1.0 - frac) + arr[i0 + 1] * frac;
    }

    private static readonly string[] PitchNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly double[] KrumhanslMajor = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] KrumhanslMinor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    /// <summary>
    /// Detects the track's musical key (e.g. Am, Gm, F#m, C) using a Hann-windowed 12-pitch-class harmonic chroma profile
    /// across Octaves 3–5 (130.8 Hz – 987.8 Hz, above unpitched kick-drum thump) with exact stride=1 sample rate matching.
    /// </summary>
    public static string DetectMusicalKey(float[] left, float[] right, int totalFrames, int sampleRate)
    {
        if (totalFrames < sampleRate)
            return "Am";

        double[] chroma = new double[12];
        int windowSize = 4096;
        int numWindows = 18;
        int usableStart = totalFrames / 6;
        int usableSpan = Math.Max(windowSize, (totalFrames * 2) / 3);

        double[] hann = new double[windowSize];
        for (int i = 0; i < windowSize; i++)
            hann[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (windowSize - 1));

        float[] winBuf = new float[windowSize];

        for (int w = 0; w < numWindows; w++)
        {
            int offset = usableStart + (int)((long)w * usableSpan / numWindows);
            if (offset + windowSize >= totalFrames)
                break;

            double energyCheck = 0.0;
            for (int i = 0; i < windowSize; i++)
            {
                float mono = (left[offset + i] + right[offset + i]) * 0.5f;
                winBuf[i] = (float)(mono * hann[i]);
                energyCheck += mono * mono;
            }
            if (energyCheck < 1e-4)
                continue;

            for (int pitch = 0; pitch < 12; pitch++)
            {
                for (int oct = 3; oct <= 5; oct++) // C3 (130.8 Hz) .. B5 (987.8 Hz)
                {
                    int midi = (oct + 1) * 12 + pitch;
                    double freq = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
                    double k = Math.Round(windowSize * freq / sampleRate);
                    double omega = 2.0 * Math.PI * k / windowSize;
                    double coeff = 2.0 * Math.Cos(omega);
                    double s1 = 0.0, s2 = 0.0;

                    for (int i = 0; i < windowSize; i++)
                    {
                        double s0 = winBuf[i] + coeff * s1 - s2;
                        s2 = s1;
                        s1 = s0;
                    }
                    double power = Math.Max(0.0, s1 * s1 + s2 * s2 - coeff * s1 * s2);
                    // Log-compress magnitude so percussive spikes don't dominate sustained musical chords
                    chroma[pitch] += Math.Log(1.0 + Math.Sqrt(power));
                }
            }
        }

        // Normalize chroma vector (zero-mean for Pearson correlation against Krumhansl profiles)
        double meanC = chroma.Average();
        string bestKey = "Am";
        double bestScore = double.NegativeInfinity;

        double meanMaj = KrumhanslMajor.Average();
        double meanMin = KrumhanslMinor.Average();

        for (int root = 0; root < 12; root++)
        {
            double majNum = 0.0, minNum = 0.0;
            double majDen = 0.0, minDen = 0.0, cDen = 0.0;

            for (int i = 0; i < 12; i++)
            {
                double dc = chroma[(root + i) % 12] - meanC;
                double dMaj = KrumhanslMajor[i] - meanMaj;
                double dMin = KrumhanslMinor[i] - meanMin;

                majNum += dc * dMaj;
                minNum += dc * dMin;
                cDen += dc * dc;
                majDen += dMaj * dMaj;
                minDen += dMin * dMin;
            }

            double majCorr = (cDen > 1e-9 && majDen > 1e-9) ? majNum / Math.Sqrt(cDen * majDen) : 0.0;
            double minCorr = (cDen > 1e-9 && minDen > 1e-9) ? minNum / Math.Sqrt(cDen * minDen) : 0.0;

            if (majCorr > bestScore)
            {
                bestScore = majCorr;
                bestKey = PitchNames[root];
            }
            if (minCorr > bestScore)
            {
                bestScore = minCorr;
                bestKey = PitchNames[root] + "m";
            }
        }

        return bestKey;
    }

    // =========================================================================
    // METADATA PARSING & CACHE PERSISTENCE
    // =========================================================================

    private static void ParseFilenameMetadata(string fileNameWithoutExt, out string artist, out string title, out string remix)
    {
        artist = string.Empty;
        title = fileNameWithoutExt.Trim();
        remix = string.Empty;

        int dashIdx = fileNameWithoutExt.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 && dashIdx + 3 < fileNameWithoutExt.Length)
        {
            artist = fileNameWithoutExt[..dashIdx].Trim();
            title = fileNameWithoutExt[(dashIdx + 3)..].Trim();
        }

        // Extract remix / version tag in parentheses or brackets if present
        int parenOpen = title.LastIndexOf('(');
        int parenClose = title.LastIndexOf(')');
        if (parenOpen > 0 && parenClose > parenOpen)
        {
            remix = title[(parenOpen + 1)..parenClose].Trim();
        }
        else
        {
            int brOpen = title.LastIndexOf('[');
            int brClose = title.LastIndexOf(']');
            if (brOpen > 0 && brClose > brOpen)
            {
                remix = title[(brOpen + 1)..brClose].Trim();
            }
        }
    }

    private static void TryReadId3v2Metadata(string filePath, ref string artist, ref string title, ref double bpm, ref string key)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 128) return;
            byte[] hdr = new byte[10];
            if (fs.Read(hdr, 0, 10) != 10) return;
            if (hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3') return;

            int version = hdr[3];
            int tagSize = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
            tagSize = Math.Min(tagSize, 262144); // Read at most 256 KB of tag headers
            byte[] tagData = new byte[tagSize];
            int read = fs.Read(tagData, 0, tagSize);

            int pos = 0;
            while (pos + 10 < read)
            {
                if (tagData[pos] == 0) break;
                string frameId = Encoding.ASCII.GetString(tagData, pos, 4);
                int frameSize = version == 4
                    ? ((tagData[pos + 4] & 0x7F) << 21) | ((tagData[pos + 5] & 0x7F) << 14) | ((tagData[pos + 6] & 0x7F) << 7) | (tagData[pos + 7] & 0x7F)
                    : (tagData[pos + 4] << 24) | (tagData[pos + 5] << 16) | (tagData[pos + 6] << 8) | tagData[pos + 7];

                pos += 10;
                if (frameSize <= 1 || pos + frameSize > read) break;

                if (frameId is "TIT2" or "TPE1" or "TBPM" or "TKEY")
                {
                    string val = DecodeId3TextFrame(tagData, pos, frameSize);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        switch (frameId)
                        {
                            case "TIT2": title = val; break;
                            case "TPE1": artist = val; break;
                            case "TBPM":
                                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b >= 50 && b <= 220)
                                    bpm = b;
                                break;
                            case "TKEY": key = val; break;
                        }
                    }
                }
                pos += frameSize;
            }
        }
        catch
        {
            // Ignore malformed ID3 tags
        }
    }

    private static string DecodeId3TextFrame(byte[] buf, int offset, int len)
    {
        byte enc = buf[offset];
        int textOff = offset + 1;
        int textLen = len - 1;
        if (textLen <= 0) return string.Empty;

        string raw = enc switch
        {
            1 => Encoding.Unicode.GetString(buf, textOff, textLen),
            2 => Encoding.BigEndianUnicode.GetString(buf, textOff, textLen),
            3 => Encoding.UTF8.GetString(buf, textOff, textLen),
            _ => Encoding.Latin1.GetString(buf, textOff, textLen)
        };
        return raw.Trim('\0', '\uFEFF', ' ', '\t', '\r', '\n');
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || !double.IsFinite(seconds))
            return "00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return;
            string json = File.ReadAllText(_cacheFilePath);
            var list = JsonSerializer.Deserialize<List<DjTrackCacheEntry>>(json);
            if (list == null) return;
            foreach (var item in list)
            {
                if (!string.IsNullOrWhiteSpace(item.FilePath))
                    _cache[item.FilePath] = item;
            }
        }
        catch
        {
            // Ignore cache read error
        }
    }

    private void SaveCacheToDisk()
    {
        lock (_cacheSaveLock)
        {
            try
            {
                var list = _cache.Values.Take(2500).ToList();
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_cacheFilePath, json);
            }
            catch
            {
                // Ignore cache save error
            }
        }
    }

    // =========================================================================
    // MEDIA FOUNDATION P/INVOKE & COM INTERFACES
    // =========================================================================

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IntPtr ppSourceReader);

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint dwStreamIndex, out int pfSelected);
        [PreserveSig] int SetStreamSelection(uint dwStreamIndex, int fSelected);
        [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IntPtr ppMediaType);
        [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IntPtr ppMediaType);
        [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IntPtr pMediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, IntPtr varPosition);
        [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IntPtr ppSample);
        [PreserveSig] int Flush(uint dwStreamIndex);
        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out int pbResult);
        [PreserveSig] int Compare(IntPtr pTheirs, int MatchType, out int pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, IntPtr pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IntPtr pDest);
    }
}
