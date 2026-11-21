using System;
using System.Collections.Generic;

namespace SpectraRealtekStudio.Core.Models;

public enum AudioEndpointFlow
{
    Render = 0,
    Capture = 1
}

public sealed class RealtekEndpointInfo
{
    public string EndpointId { get; set; } = string.Empty;
    public string GuidPart { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceDescription { get; set; } = string.Empty;
    public AudioEndpointFlow DataFlow { get; set; }
    public uint DeviceState { get; set; }
    public string StateText => DeviceState switch
    {
        1 => "Active",
        2 => "Disabled",
        4 => "Not Present",
        8 => "Unplugged",
        0x10000001 => "Disabled (Hidden)",
        _ => $"Hidden (0x{DeviceState:X})"
    };
    public bool IsActive => DeviceState == 1;
    public bool IsRealtek { get; set; }
    public bool IsDefaultMultimedia { get; set; }
    public bool IsDefaultCommunications { get; set; }
    public uint FormFactor { get; set; }
    public string FormFactorName => FormFactor switch
    {
        0 => "Remote Network Device",
        1 => "Speakers",
        2 => "Line Level",
        3 => "Headphones",
        4 => "Microphone",
        5 => "Headset",
        6 => "Handset",
        7 => "Unknown Digital Passthrough",
        8 => "SPDIF Digital",
        9 => "HDMI / DisplayPort",
        _ => "Unknown"
    };
    public string HardwareId { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string WaveDevicePath { get; set; } = string.Empty;
    public string TopologyPath { get; set; } = string.Empty;
    public string EffectProviderName { get; set; } = string.Empty;
    public string SfxClsid { get; set; } = string.Empty;
    public string MfxClsid { get; set; } = string.Empty;
    public string EfxClsid { get; set; } = string.Empty;
    public string UiClsid { get; set; } = string.Empty;
    public int PropertyCount { get; set; }
    public int FxPropertyCount { get; set; }

    public string DisplayLabel =>
        $"{FriendlyName} ({DeviceDescription}){(IsDefaultMultimedia ? " [Default]" : "")}{(IsActive ? "" : $" [{StateText}]")}";
}

public sealed class RealtekPlaybackState
{
    public string EndpointId { get; set; } = string.Empty;
    public bool DisableAllEnhancements { get; set; }
    public bool ImmediateMode { get; set; } = true;

    // 10-Band Equalizer
    public bool EqualizerEnabled { get; set; }
    public double[] EqualizerBandsDb { get; set; } = new double[10];
    public string MatchedPresetName { get; set; } = "Custom";

    // Environment (EAX Reverb)
    public bool EnvironmentEnabled { get; set; }
    public int EnvironmentPresetIndex { get; set; }

    // Karaoke (Voice Cancellation & Pitch Shift)
    public bool VoiceCancellationEnabled { get; set; }
    public int PitchShiftSemitones { get; set; }

    // Spatial / Surround / Loudness
    public bool LoudnessEqualizationEnabled { get; set; }
    public bool SpeakerFillEnabled { get; set; }
    public bool VirtualSurroundEnabled { get; set; }
    public bool HeadphoneVirtualizationEnabled { get; set; }
    public bool BassManagementEnabled { get; set; }
    public bool SpeakerExpansionEnabled { get; set; }
    public bool SwapCenterSubwooferEnabled { get; set; }

    // Room Correction (6/8 speaker channels: FL, FR, C, LFE, RL, RR, SL, SR)
    public bool RoomCorrectionEnabled { get; set; }
    public bool RoomCorrectionUnitIsFeet { get; set; }
    public int[] RoomCorrectionGainDb { get; set; } = new int[8];
    public int[] RoomCorrectionDistance { get; set; } = new int[8];

    // SounzReal / OmniSound / BassBack / MP3+
    public bool OmniSoundEnabled { get; set; }
    public bool OmniHeadphoneEnabled { get; set; }
    public bool VoiceBackEnabled { get; set; }
    public bool BassBackEnabled { get; set; }
    public bool Mp3PlusEnabled { get; set; }
}

public sealed class RealtekCaptureState
{
    public string EndpointId { get; set; } = string.Empty;
    public bool DisableAllEnhancements { get; set; }

    public bool NoiseSuppressionEnabled { get; set; }
    public uint NoiseSuppressionDbLevel { get; set; }
    public bool AcousticEchoCancellationEnabled { get; set; }
    public bool BeamFormingEnabled { get; set; }
    public bool DcOffsetCancellationEnabled { get; set; }
    public bool MonoMicrophoneEnabled { get; set; }
    public bool ConferenceModeEnabled { get; set; }
    public bool KeystrokeSuppressionEnabled { get; set; }
    public uint MicPresetMode { get; set; }

    // Analog Volume & Mic Boost
    public float MasterVolumePercent { get; set; }
    public bool MasterMuted { get; set; }
    public float MicBoostDb { get; set; }
    public float MicBoostMinDb { get; set; }
    public float MicBoostMaxDb { get; set; } = 30.0f;
    public float MicBoostStepDb { get; set; } = 10.0f;
    public bool SupportsMicBoost { get; set; }
}

public sealed class AudioFormatState
{
    public string EndpointId { get; set; } = string.Empty;
    public int Channels { get; set; } = 2;
    public int SampleRate { get; set; } = 48000;
    public int BitsPerSample { get; set; } = 32;
    public int ValidBitsPerSample { get; set; } = 24;
    public uint ChannelMask { get; set; } = 0x3;
    public uint PhysicalSpeakersMask { get; set; } = 0x3;
    public uint FullRangeSpeakersMask { get; set; } = 0x3;
    public bool ExclusiveModeAllowed { get; set; } = true;
    public bool ExclusiveModePriority { get; set; } = true;

    public string FormatSummary => $"{Channels} ch, {ValidBitsPerSample}-bit, {SampleRate:N0} Hz";

    public string SpeakerConfigurationName => PhysicalSpeakersMask switch
    {
        0x3 => "Stereo (2.0)",
        0x33 => "Quadraphonic (4.0)",
        0x3F => "5.1 Surround (Rear)",
        0x60F => "5.1 Surround (Side)",
        0x63F => "7.1 Surround",
        _ => $"Custom (0x{PhysicalSpeakersMask:X})"
    };
}

public sealed class AnalogMixerSubunit
{
    public string IdKey { get; set; } = string.Empty;
    public uint LocalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceDescription { get; set; } = string.Empty;
    public int ChannelCount { get; set; } = 2;
    public float LeftLevelPercent { get; set; } = 100f;
    public float RightLevelPercent { get; set; } = 100f;
    public float LeftLevelDb { get; set; }
    public float RightLevelDb { get; set; }
    public float MinDb { get; set; } = -65.25f;
    public float MaxDb { get; set; } = 0.0f;
    public float StepDb { get; set; } = 0.75f;
    public bool IsMuted { get; set; }
    public bool SupportsVolume { get; set; } = true;
    public bool SupportsMute { get; set; } = true;
    public bool IsBackedByRegistry { get; set; }
}

public sealed class JackPinInfo
{
    public int VerbStartIndex { get; set; }
    public byte PinWidgetId { get; set; }
    public uint RawPinConfig { get; set; }
    public string PanelLocation { get; set; } = string.Empty;
    public string JackColorName { get; set; } = string.Empty;
    public string JackColorHex { get; set; } = "#808080";
    public string ConnectorType { get; set; } = "3.5mm Analog Jack";
    public byte DefaultDeviceCode { get; set; }
    /// <summary>
    /// Realtek driver RtskDevType role code (Settings\Drv...\PinXX &amp; IRtkPropSet20146::SetDeviceTypeWidget):
    /// 0=Line In, 1=Mic In, 2=Headphone Out, 4=Front Speaker, 5=Rear Speaker, 6=Center/Sub, 7=Side Speaker.
    /// </summary>
    public byte RetaskRoleCode { get; set; }
    /// <summary>
    /// Hardware-supported retask role bitmask from Realtek kernel KSProperty (bit N = role code N supported).
    /// </summary>
    public ushort SupportedRolesMask { get; set; } = 0x00F7;
    public string AssignedRoleName { get; set; } = string.Empty;
    public byte ConnectivityCode { get; set; }
    public bool IsPhysicalJack => ConnectivityCode == 0; // 0 = Jack, 1 = Unconnected, 2 = Fixed
    public bool IsPluggedIn { get; set; }
    public string LiveSensorSource { get; set; } = string.Empty;
    public byte Sequence { get; set; }
    public byte DefaultAssociation { get; set; }
}

public sealed class LiveJackHardwareInfo
{
    public byte WidgetId { get; set; }
    public bool IsPluggedIn { get; set; }
    public ushort SupportedRolesMask { get; set; }
    public uint HardwarePinConfig { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed record JackRetaskRoleOption(
    byte RetaskCode,
    string Label,
    byte HdaDefaultDevice,
    byte HdaAssociation,
    byte HdaSequence);

public sealed class DriverHardwareTweaks
{
    public string DriverClassKeyPath { get; set; } = string.Empty;
    public string DriverDescription { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string DriverDate { get; set; } = string.Empty;
    public string InfPath { get; set; } = string.Empty;
    public string CodecHardwareId { get; set; } = string.Empty;
    public string CodecChipName { get; set; } = string.Empty;
    public string MotherboardSubsysId { get; set; } = string.Empty;
    public string SettingsSubkeyName { get; set; } = string.Empty;

    public bool MuteRearWhenFrontHpPlugged { get; set; }
    public bool IndependentFrontRearStreams { get; set; }
    public bool FrontPanelHdAudioMode { get; set; }
    public bool DisableFrontPanelJackDetection { get; set; }
    public bool SwapCenterLfeInDriver { get; set; }
    public bool SpdifEchoesRearRender { get; set; }
    public bool AntiPopDisableIdlePowerDown { get; set; }
    public bool EnableWaveRt { get; set; }
    public bool DcOffsetWorkaround { get; set; }
}

public sealed class PropertyEntryInfo
{
    public string StoreName { get; set; } = string.Empty;
    public string KeyString { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ValueType { get; set; } = string.Empty;
    public string DecodedValue { get; set; } = string.Empty;
    public string RawHex { get; set; } = string.Empty;
}

public sealed class StudioProfileExport
{
    public string ProfileName { get; set; } = "My Realtek Studio Profile";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string CodecHardwareId { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public RealtekPlaybackState? PlaybackState { get; set; }
    public RealtekCaptureState? CaptureState { get; set; }
    public AudioFormatState? PlaybackFormat { get; set; }
}

public sealed class AppUserSettings
{
    public bool TooltipsEnabled { get; set; } = true;
    public double MasterPreampGainDb { get; set; } = 0.0;
    public bool AutoGainEnabled { get; set; } = false;
    public int AutoGainCeilingPercent { get; set; } = 80;
    /// <summary>
    /// "Preamp" (RltkAPO64 digital headroom anti-clip limiter) or "MasterVolume" (Windows Master Volume limiter).
    /// </summary>
    public string AutoGainMode { get; set; } = "Preamp";
}

public sealed class UpdateCheckResult
{
    public bool Success { get; set; }
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseTitle { get; set; } = string.Empty;
    public string ReleaseNotesSummary { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = "https://github.com/Creator-NL/SpectraRealtekStudio/releases";
    public string ErrorMessage { get; set; } = string.Empty;
}
