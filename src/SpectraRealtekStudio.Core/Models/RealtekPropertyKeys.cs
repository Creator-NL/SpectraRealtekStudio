using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpectraRealtekStudio.Core.Models;

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey : IEquatable<PropertyKey>
{
    public Guid FmtId;
    public uint Pid;

    public PropertyKey(string guid, uint pid)
    {
        FmtId = new Guid(guid);
        Pid = pid;
    }

    public PropertyKey(Guid fmtId, uint pid)
    {
        FmtId = fmtId;
        Pid = pid;
    }

    public override string ToString() => $"{{{FmtId.ToString().ToLowerInvariant()}}},{Pid}";

    public bool Equals(PropertyKey other) => FmtId.Equals(other.FmtId) && Pid == other.Pid;
    public override bool Equals(object? obj) => obj is PropertyKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(FmtId, Pid);
}

public sealed record EqPresetDefinition(string Name, bool IsBuiltIn, double[] BandsDb);

public sealed record EaxEnvironmentDefinition(int Index, string Name, string Category);

public sealed record PropertyKeyMetadata(
    string Name,
    string Category,
    string Description,
    string ExpectedType);

/// <summary>
/// Reverse-engineered catalog of Realtek High Definition Audio (RltkAPO64.dll, RtPgEx64.dll, RtCOM64.dll, RtkApi64.dll)
/// and Windows CoreAudio PROPERTYKEYs, built-in EQ presets, and EAX environment definitions.
/// </summary>
public static class RealtekPropertyKeys
{
    // =========================================================================
    // Core Realtek Playback Effect Property Set: {4b361010-def7-43a1-a5dc-071d955b62f7}
    // Reverse-engineered from RtPgEx64.dll (RVA 0x60300..0x64C00) & RtCOM64.dll (RVA 0x2D590..0x31510)
    // =========================================================================
    public static readonly Guid RealtekCoreFxGuid = new("4b361010-def7-43a1-a5dc-071d955b62f7");

    public static readonly PropertyKey EnvironmentEnable = new(RealtekCoreFxGuid, 1);
    public static readonly PropertyKey EnvironmentPreset = new(RealtekCoreFxGuid, 2);
    public static readonly PropertyKey VoiceCancellationEnable = new(RealtekCoreFxGuid, 3);
    public static readonly PropertyKey PitchShiftKey = new(RealtekCoreFxGuid, 4);

    public static readonly PropertyKey EqualizerEnable = new(RealtekCoreFxGuid, 5);
    public static readonly PropertyKey EqualizerEnableSpk = new(RealtekCoreFxGuid, 501);
    public static readonly PropertyKey EqualizerEnableHp = new(RealtekCoreFxGuid, 502);
    public static readonly PropertyKey EqualizerEnableExt = new(RealtekCoreFxGuid, 503);

    /// <summary>
    /// 40-byte VT_BLOB (int[10]) containing 10-band EQ gains in hundredths of a dB (-1200 to +1200).
    /// Bands: 31Hz, 62Hz, 125Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz, 8kHz, 16kHz.
    /// </summary>
    public static readonly PropertyKey EqualizerBandsBlob = new(RealtekCoreFxGuid, 6);
    public static readonly PropertyKey EqualizerBandsBlobSpk = new(RealtekCoreFxGuid, 601);
    public static readonly PropertyKey EqualizerBandsBlobHp = new(RealtekCoreFxGuid, 602);
    public static readonly PropertyKey EqualizerBandsBlobExt = new(RealtekCoreFxGuid, 603);
    public static readonly PropertyKey EqualizerCustomPresetsBlob = new(RealtekCoreFxGuid, 7);

    public static readonly PropertyKey BassManagementEnable = new(RealtekCoreFxGuid, 9);
    public static readonly PropertyKey SwapCenterSubwooferEnable = new(RealtekCoreFxGuid, 12);

    public static readonly PropertyKey RoomCorrectionEnable = new(RealtekCoreFxGuid, 14);
    public static readonly PropertyKey RoomCorrectionGainBlob = new(RealtekCoreFxGuid, 15);
    public static readonly PropertyKey RoomCorrectionDistanceBlob = new(RealtekCoreFxGuid, 16);

    public static readonly PropertyKey BassBackEnableCore = new(RealtekCoreFxGuid, 17);
    public static readonly PropertyKey OmniSoundEnableCore = new(RealtekCoreFxGuid, 31);
    public static readonly PropertyKey HeadphoneRedirectMode32 = new(RealtekCoreFxGuid, 32);
    public static readonly PropertyKey HeadphoneRedirectMode33 = new(RealtekCoreFxGuid, 33);

    // =========================================================================
    // Realtek Spatial / Home Theater / Loudness Property Set: {e0a941a0-88a2-4df5-8d6b-dd20bb06e8fb}
    // Reverse-engineered from RtPgEx64.dll (RVA 0x65625..0x664A8)
    // =========================================================================
    public static readonly Guid RealtekSpatialFxGuid = new("e0a941a0-88a2-4df5-8d6b-dd20bb06e8fb");

    public static readonly PropertyKey SpeakerFillEnable = new(RealtekSpatialFxGuid, 1);
    public static readonly PropertyKey VirtualSurroundEnable = new(RealtekSpatialFxGuid, 2);
    public static readonly PropertyKey HeadphoneVirtualizationEnable = new(RealtekSpatialFxGuid, 3);
    public static readonly PropertyKey LoudnessEqualizationEnable = new(RealtekSpatialFxGuid, 4);

    // =========================================================================
    // Realtek Speaker Expansion & Pitch Shift Extended Sets
    // =========================================================================
    public static readonly PropertyKey SpeakerExpansionEnable = new("3de4d38a-db75-4536-9017-2cbec1122ba1", 1);
    public static readonly PropertyKey PitchShiftExtEnable = new("9cc064e5-7fdc-4f03-9994-f24d4908aa26", 1);
    public static readonly PropertyKey PitchShiftExtValue = new("9cc064e5-7fdc-4f03-9994-f24d4908aa26", 2);

    // =========================================================================
    // Realtek 2nd EQ / DRC / SounzReal / Speaker Diff Set: {3ba0cd54-830f-4551-a6eb-f3eab68e3700}
    // =========================================================================
    public static readonly Guid RealtekAdvDspGuid = new("3ba0cd54-830f-4551-a6eb-f3eab68e3700");

    public static readonly PropertyKey AdvDspEnable1 = new(RealtekAdvDspGuid, 1);
    public static readonly PropertyKey AdvDspEnable2 = new(RealtekAdvDspGuid, 2);
    public static readonly PropertyKey ParametricEq2ndBlob = new(RealtekAdvDspGuid, 3); // BYTE[172] _EQ2ndSetting
    public static readonly PropertyKey AdvDspMode4 = new(RealtekAdvDspGuid, 4);
    public static readonly PropertyKey AdvDspMode6 = new(RealtekAdvDspGuid, 6);
    public static readonly PropertyKey DrcControlBlob = new(RealtekAdvDspGuid, 7); // BYTE[344] DRCCTRLSETTING
    public static readonly PropertyKey MicPresetMode = new(RealtekAdvDspGuid, 17);
    public static readonly PropertyKey SpeakerDiffGainLatency = new(RealtekAdvDspGuid, 43);

    // =========================================================================
    // SounzReal / OmniSound / BassBack / MP3Plus Set: {0547889b-ef4e-4801-86ac-80ef1d19d680}
    // =========================================================================
    public static readonly Guid RealtekSounzRealGuid = new("0547889b-ef4e-4801-86ac-80ef1d19d680");

    public static readonly PropertyKey SounzRealMp3PlusEnable = new(RealtekSounzRealGuid, 0);
    public static readonly PropertyKey SounzRealMode1 = new(RealtekSounzRealGuid, 1);
    public static readonly PropertyKey SounzRealVoiceBackEnable = new(RealtekSounzRealGuid, 2);
    public static readonly PropertyKey SounzRealOmniHeadphoneEnable = new(RealtekSounzRealGuid, 3);
    public static readonly PropertyKey SounzRealMode4 = new(RealtekSounzRealGuid, 4);

    // =========================================================================
    // Realtek Recording / Microphone DSP Property Sets
    // Reverse-engineered from RtCOM64.dll (IPropSet20069) & RtPgEx64.dll (RVA 0x651B5, 0x6680D, 0xB5477..0xB5D4B)
    // =========================================================================
    public static readonly Guid RealtekMicNsAecGuid = new("07b93a42-c29c-4df7-8899-64e4754f119a");
    public static readonly PropertyKey MicNoiseSuppressionEnable = new(RealtekMicNsAecGuid, 0);
    public static readonly PropertyKey MicNsParam1 = new(RealtekMicNsAecGuid, 1);
    public static readonly PropertyKey MicAecEnable = new(RealtekMicNsAecGuid, 2);

    public static readonly Guid RealtekMicVoiceGuid = new("911dff54-0b79-4e96-b3de-577f235b619b");
    public static readonly PropertyKey MicVoiceNsEnable = new(RealtekMicVoiceGuid, 0);
    public static readonly PropertyKey MicVoiceBeamFormingEnable = new(RealtekMicVoiceGuid, 1);
    public static readonly PropertyKey MicVoiceAecEnable = new(RealtekMicVoiceGuid, 2);
    public static readonly PropertyKey MicConferenceMode = new(RealtekMicVoiceGuid, 15);
    public static readonly PropertyKey MicVoiceMode20 = new(RealtekMicVoiceGuid, 20);
    public static readonly PropertyKey MicVoiceMode21 = new(RealtekMicVoiceGuid, 21);
    public static readonly PropertyKey MicVoiceMode22 = new(RealtekMicVoiceGuid, 22);
    public static readonly PropertyKey MicVoiceMode31 = new(RealtekMicVoiceGuid, 31);

    public static readonly PropertyKey MicBeamFormingLegacy = new("2b24be42-a892-11dc-8314-0800200c9a66", 2);
    public static readonly PropertyKey MicDcOffsetCancellationEnable = new("83f774ae-6ad4-4737-98cc-6092e12453cc", 1);
    public static readonly PropertyKey MicMonoMicrophoneEnable = new("83f774ae-6ad4-4737-98cc-6092e12453cc", 6);
    public static readonly PropertyKey MicKeystrokeSuppressionEnable = new("f5c73ad1-eb18-446f-a6f5-3802e10c4ba2", 0);
    public static readonly PropertyKey MicVoipMode = new("f5c73ad1-eb18-446f-a6f5-3802e10c4ba2", 42);

    // =========================================================================
    // Standard Windows / Realtek Endpoint & System Effect Control Keys
    // =========================================================================
    public static readonly PropertyKey DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);
    public static readonly PropertyKey DisableSysFxAlt = new("233164c8-1b2c-4c7d-bc68-b671687a2567", 1);
    public static readonly PropertyKey ImmediateMode = new("5642a375-d640-4d88-bb34-cc1c865ba48d", 2);

    public static readonly PropertyKey EndpointFormFactor = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);
    public static readonly PropertyKey EndpointPhysicalSpeakers = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 3);
    public static readonly PropertyKey EndpointFullRangeSpeakers = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 6);
    public static readonly PropertyKey EndpointSupportsEventDriven = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 7);

    public static readonly PropertyKey AudioEngineDeviceFormat = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0);
    public static readonly PropertyKey AudioEngineOemFormat = new("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04", 0);

    public static readonly PropertyKey DeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly PropertyKey EndpointFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly PropertyKey InterfaceFriendlyName = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);
    public static readonly PropertyKey ExclusiveModeAllowed = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 24);
    public static readonly PropertyKey ExclusiveModePriority = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 27);

    public static readonly PropertyKey FxFriendlyName = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
    public static readonly PropertyKey FxUiClsid = new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 3);
    public static readonly PropertyKey FxSfxClsid = new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 5);
    public static readonly PropertyKey FxMfxClsid = new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 6);
    public static readonly PropertyKey FxEfxClsid = new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 7);

    // =========================================================================
    // 10-Band Equalizer Frequencies & Official Realtek Presets (from RtPgEx64.dll @ RVA 0xDEF34)
    // =========================================================================
    public static readonly string[] EqBandLabels =
    [
        "31 Hz", "62 Hz", "125 Hz", "250 Hz", "500 Hz",
        "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"
    ];

    public static readonly int[] EqBandFrequenciesHz =
    [
        31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    ];

    public static readonly IReadOnlyList<EqPresetDefinition> BuiltInEqPresets =
    [
        new("Flat (Reset)", true, [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
        new("Pop",          true, [-2.0, 2.0, 4.0, 5.8, 4.0, -2.0, -3.5, -3.5, -3.5, -3.5]),
        new("Live",         true, [-3.6, 0.0, 4.2, 5.3, 5.9, 5.3, 2.5, 2.0, 1.0, 0.5]),
        new("Club",         true, [0.0, 0.0, 2.0, 5.0, 5.0, 5.0, 2.0, 0.0, 0.0, 0.0]),
        new("Rock",         true, [4.0, 2.0, -2.5, -5.3, -2.5, 2.5, 5.3, 6.5, 6.5, 6.5]),
        new("Bass",         true, [10.0, 9.0, 8.0, 3.2, 0.0, -3.2, -5.0, -6.7, -8.0, -8.0]),
        new("Treble",       true, [-8.0, -8.0, -8.0, -4.5, 0.0, 3.2, 9.0, 9.0, 9.0, 9.0]),
        new("Vocal",        true, [-3.0, 5.0, 5.0, 5.0, -3.0, -3.0, -3.0, -3.0, -3.0, -3.0]),
        new("Powerful",     true, [8.0, 7.9, 4.4, -4.4, -2.6, 1.5, 6.8, 7.9, 9.1, 9.7]),
        new("Dance",        true, [7.0, 4.8, 1.4, 0.0, 0.0, -3.7, -5.3, -5.3, 0.0, 0.0]),
        new("Soft",         true, [2.0, 0.0, -1.3, -3.5, -1.3, 1.9, 4.0, 5.8, 6.8, 7.5]),
        new("Party",        true, [5.5, 5.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 5.5, 5.5]),
        new("Classical",    true, [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, -5.0, -5.0, -5.0, -6.0])
    ];

    // =========================================================================
    // 24 Official Realtek EAX Environment Reverb Presets (from RtPgEx64.dll Strings 101..124)
    // =========================================================================
    public static readonly IReadOnlyList<EaxEnvironmentDefinition> BuiltInEnvironments =
    [
        new(0,  "None",             "Bypass"),
        new(1,  "Generic",          "Standard"),
        new(2,  "Padded Cell",      "Small Room"),
        new(3,  "Room",             "Small Room"),
        new(4,  "Bathroom",         "Small Room"),
        new(5,  "Living Room",      "Medium Room"),
        new(6,  "Stone Room",       "Medium Room"),
        new(7,  "Auditorium",       "Large Hall"),
        new(8,  "Concert Hall",     "Large Hall"),
        new(9,  "Cave",             "Cavern"),
        new(10, "Arena",            "Large Hall"),
        new(11, "Hangar",           "Industrial"),
        new(12, "Carpeted Hallway", "Corridor"),
        new(13, "Hallway",          "Corridor"),
        new(14, "Stone Corridor",   "Corridor"),
        new(15, "Alley",            "Outdoor"),
        new(16, "Forest",           "Outdoor"),
        new(17, "City",             "Outdoor"),
        new(18, "Mountains",        "Outdoor"),
        new(19, "Quarry",           "Outdoor"),
        new(20, "Plain",            "Outdoor"),
        new(21, "Parking Lot",      "Industrial"),
        new(22, "Sewer Pipe",       "Special"),
        new(23, "Under Water",      "Special")
    ];

    // =========================================================================
    // Metadata Catalog for Diagnostic Inspector Tab
    // =========================================================================
    public static readonly IReadOnlyDictionary<string, PropertyKeyMetadata> KnownMetadata =
        new Dictionary<string, PropertyKeyMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [EnvironmentEnable.ToString()] = new("Environment (EAX Reverb) Enable", "Playback DSP", "Enables EAX acoustic environment simulation", "VT_UI4 (0/1)"),
            [EnvironmentPreset.ToString()] = new("Environment (EAX Reverb) Preset / Params", "Playback DSP", "Active EAX environment index or 56-byte reverb parameters", "VT_UI4 / VT_BLOB"),
            [VoiceCancellationEnable.ToString()] = new("Voice Cancellation (Karaoke)", "Playback DSP", "Mutes center-panned vocals for Karaoke playback", "VT_UI4 (0/1)"),
            [PitchShiftKey.ToString()] = new("Pitch Shift (Karaoke Key)", "Playback DSP", "Shifts playback pitch from -4 to +4 semitones", "VT_I4 (-4..+4)"),
            [EqualizerEnable.ToString()] = new("10-Band Equalizer Enable", "Equalizer", "Master enable switch for the 10-band graphic equalizer", "VT_UI4 (0/1)"),
            [EqualizerEnableSpk.ToString()] = new("Equalizer Enable (Speaker Mode)", "Equalizer", "Per-role equalizer enable for Speakers", "VT_UI4 (0/1)"),
            [EqualizerEnableHp.ToString()] = new("Equalizer Enable (Headphone Mode)", "Equalizer", "Per-role equalizer enable for Headphones", "VT_UI4 (0/1)"),
            [EqualizerEnableExt.ToString()] = new("Equalizer Enable (External Mode)", "Equalizer", "Per-role equalizer enable for External Output", "VT_UI4 (0/1)"),
            [EqualizerBandsBlob.ToString()] = new("10-Band Equalizer Gains (31Hz..16kHz)", "Equalizer", "40-byte int[10] array in 0.01 dB units (-1200 to +1200)", "VT_BLOB (40 bytes)"),
            [EqualizerBandsBlobSpk.ToString()] = new("10-Band EQ Gains (Speaker Mode)", "Equalizer", "40-byte int[10] array for Speaker profile", "VT_BLOB (40 bytes)"),
            [EqualizerBandsBlobHp.ToString()] = new("10-Band EQ Gains (Headphone Mode)", "Equalizer", "40-byte int[10] array for Headphone profile", "VT_BLOB (40 bytes)"),
            [EqualizerBandsBlobExt.ToString()] = new("10-Band EQ Gains (External Mode)", "Equalizer", "40-byte int[10] array for External profile", "VT_BLOB (40 bytes)"),
            [EqualizerCustomPresetsBlob.ToString()] = new("Realtek Saved Custom EQ Presets", "Equalizer", "Binary array of user-defined EQ presets saved in RtPgEx64", "VT_BLOB"),
            [BassManagementEnable.ToString()] = new("Bass Management Enable", "Spatial & Speaker", "Redirects low frequencies below crossover point to subwoofer", "VT_UI4 (0/1)"),
            [SwapCenterSubwooferEnable.ToString()] = new("Swap Center / Subwoofer Speaker", "Spatial & Speaker", "Swaps Center and LFE Subwoofer output channels", "VT_UI4 (0/1)"),
            [RoomCorrectionEnable.ToString()] = new("Room Correction Enable", "Room Correction", "Compensates speaker distances and gain trims", "VT_UI4 (0/1)"),
            [RoomCorrectionGainBlob.ToString()] = new("Room Correction Per-Speaker Gains", "Room Correction", "Per-speaker gain offset array in dB", "VT_BLOB"),
            [RoomCorrectionDistanceBlob.ToString()] = new("Room Correction Per-Speaker Distances", "Room Correction", "Per-speaker distance array and unit scale (Meters/Feet)", "VT_BLOB"),
            [SpeakerFillEnable.ToString()] = new("Speaker Fill Enable", "Spatial & Speaker", "Upmixes stereo audio sources across surround speaker systems", "VT_UI4 (0/1)"),
            [VirtualSurroundEnable.ToString()] = new("Virtual Surround Enable", "Spatial & Speaker", "Encodes surround audio for stereo output / matrix receivers", "VT_UI4 (0/1)"),
            [HeadphoneVirtualizationEnable.ToString()] = new("Headphone Virtualization Enable", "Spatial & Speaker", "Creates home theater surround illusion on stereo headphones", "VT_UI4 (0/1)"),
            [LoudnessEqualizationEnable.ToString()] = new("Loudness Equalization Enable", "Playback DSP", "Reduces perceived volume differences using human hearing curves", "VT_UI4 (0/1)"),
            [SpeakerExpansionEnable.ToString()] = new("Speaker Expansion Enable", "Spatial & Speaker", "Expands stereo soundstage to multi-channel speakers", "VT_UI4 (0/1)"),
            [ParametricEq2ndBlob.ToString()] = new("2nd Parametric / Hardware EQ (_EQ2ndSetting)", "Advanced DSP", "172-byte parametric biquad EQ configuration for speaker/mic", "VT_BLOB (172 bytes)"),
            [DrcControlBlob.ToString()] = new("Dynamic Range Compression (DRCCTRLSETTING)", "Advanced DSP", "344-byte compressor/expander/limiter/noise-gate thresholds", "VT_BLOB (344 bytes)"),
            [SounzRealMp3PlusEnable.ToString()] = new("SounzReal MP3+ Harmonic Enhancer", "Advanced DSP", "Restores high-frequency harmonics in compressed audio", "VT_UI4 (0/1)"),
            [SounzRealVoiceBackEnable.ToString()] = new("SounzReal VoiceBack Clarity", "Advanced DSP", "Enhances vocal dialog clarity", "VT_UI4 (0/1)"),
            [SounzRealOmniHeadphoneEnable.ToString()] = new("SounzReal OmniHeadphone 3D", "Advanced DSP", "OmniSound 3D headphone angle virtualization", "VT_UI4 (0/1)"),
            [BassBackEnableCore.ToString()] = new("Realtek BassBack Boost", "Advanced DSP", "Psychoacoustic bass enhancement for small speakers", "VT_UI4 (0/1)"),
            [OmniSoundEnableCore.ToString()] = new("Realtek OmniSound Surround", "Advanced DSP", "OmniSound center and surround soundstage expansion", "VT_UI4 (0/1)"),
            [MicNoiseSuppressionEnable.ToString()] = new("Microphone Noise Suppression", "Recording DSP", "Reduces static background noise during recording", "VT_UI4 (0/1)"),
            [MicAecEnable.ToString()] = new("Acoustic Echo Cancellation (AEC)", "Recording DSP", "Removes speaker playback echo from microphone input", "VT_UI4 (0/1)"),
            [MicVoiceNsEnable.ToString()] = new("Voice Noise Reduction (Forte/Realtek)", "Recording DSP", "Adaptive voice noise suppression", "VT_UI4 (0/1)"),
            [MicVoiceBeamFormingEnable.ToString()] = new("Microphone Beam Forming (BF)", "Recording DSP", "Directional cone pickup for stereo microphone array", "VT_UI4 (0/1)"),
            [MicVoiceAecEnable.ToString()] = new("Voice Acoustic Echo Cancellation", "Recording DSP", "Voice chat echo cancellation", "VT_UI4 (0/1)"),
            [MicBeamFormingLegacy.ToString()] = new("Beam Forming (Directional Cone)", "Recording DSP", "Focuses pickup inside a 30-degree cone in front of mic", "VT_UI4 (0/1)"),
            [MicDcOffsetCancellationEnable.ToString()] = new("DC Offset Cancellation", "Recording DSP", "Removes hardware DC voltage offset from microphone signal", "VT_UI4 (0/1)"),
            [MicMonoMicrophoneEnable.ToString()] = new("Mono Microphone Mode", "Recording DSP", "Enable when a mono microphone is connected", "VT_UI4 (0/1)"),
            [MicConferenceMode.ToString()] = new("Microphone Conference / Multi-User Mode", "Recording DSP", "Wide-angle pickup for multi-person conference calls", "VT_UI4 (0/1)"),
            [MicKeystrokeSuppressionEnable.ToString()] = new("Keystroke Noise Suppression (KNS)", "Recording DSP", "Suppresses keyboard typing noise during voice calls", "VT_UI4 (0/1)"),
            [DisableSysFx.ToString()] = new("Disable All Sound Effects (SysFx)", "System", "Master bypass for Windows Audio Processing Objects", "VT_UI4 (0/1)"),
            [DisableSysFxAlt.ToString()] = new("Disable All Sound Effects (Endpoint)", "System", "Endpoint-level system effects disable flag", "VT_UI4 (0/1)"),
            [ImmediateMode.ToString()] = new("Immediate Mode", "System", "Applies sound effect changes immediately without stream restart", "VT_UI4"),
            [FxFriendlyName.ToString()] = new("System Effect Provider Name", "APO Registration", "Display name of attached Audio Processing Object", "VT_LPWSTR"),
            [FxSfxClsid.ToString()] = new("Stream Effect (SFX) APO CLSID", "APO Registration", "Realtek RtkAPOSFX ({DA2C9ECE-7418-4906-B4FA-0A00B3EB88AA})", "VT_LPWSTR"),
            [FxMfxClsid.ToString()] = new("Mode Effect (MFX) APO CLSID", "APO Registration", "Realtek RtkAPOMFX ({A296D363-EE83-4af9-9BE7-729C1296150A})", "VT_LPWSTR"),
            [FxEfxClsid.ToString()] = new("Endpoint Effect (EFX) APO CLSID", "APO Registration", "Realtek RtkAPOEFX ({A29EB043-6CE2-4ee2-B38C-F58719E0D88F})", "VT_LPWSTR"),
            [FxUiClsid.ToString()] = new("Property Page UI CLSID", "APO Registration", "Realtek RtkAdvPropPage ({6861CFDC-0461-49d5-A8DF-BE5ACD02692F})", "VT_LPWSTR"),
            [EndpointFormFactor.ToString()] = new("Endpoint Form Factor", "Endpoint", "1=Speakers, 2=LineLevel, 3=Headphones, 4=Microphone, 8=SPDIF, 9=HDMI", "VT_UI4"),
            [EndpointPhysicalSpeakers.ToString()] = new("Physical Speaker Channel Mask", "Endpoint", "0x3=Stereo, 0x33=Quad, 0x3F=5.1 Surround, 0x63F=7.1 Surround", "VT_UI4"),
            [EndpointFullRangeSpeakers.ToString()] = new("Full-Range Speaker Channel Mask", "Endpoint", "Bitmask of speakers capable of full-range bass playback", "VT_UI4"),
            [AudioEngineDeviceFormat.ToString()] = new("Shared-Mode Engine Device Format", "Format", "WAVEFORMATEXTENSIBLE binary structure (Channels, SampleRate, BitDepth)", "VT_BLOB"),
            [AudioEngineOemFormat.ToString()] = new("OEM Default Device Format", "Format", "Default WAVEFORMATEXTENSIBLE defined by driver INF", "VT_BLOB"),
            [ExclusiveModeAllowed.ToString()] = new("Allow Exclusive Mode", "Format", "Allow applications to take exclusive control of this device", "VT_BOOL"),
            [ExclusiveModePriority.ToString()] = new("Exclusive Mode Priority", "Format", "Give exclusive mode applications priority", "VT_BOOL")
        };
}
