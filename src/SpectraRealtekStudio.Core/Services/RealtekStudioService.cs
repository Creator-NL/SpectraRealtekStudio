using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using SpectraRealtekStudio.Core.Interop;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

/// <summary>
/// Central service coordinating non-admin RPC property reads/writes (RtkCfg64.dll / IPolicyConfig),
/// Realtek COM APIs (RtCOM64.dll), Windows CoreAudio topology/meters, and driver registry inspection.
/// </summary>
public sealed class RealtekStudioService
{
    private const string MmDevicesBaseKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

    public PolicyConfigInterop PolicyConfig { get; } = new();
    public RealtekComInterop RealtekCom { get; } = new();
    public CoreAudioInterop CoreAudio { get; } = new();
    public DriverHardwareService DriverHardware { get; } = new();
    public PresetAndProfileService Presets { get; } = new();
    public SpeakerTestToneService TestTones { get; } = new();

    public List<RealtekEndpointInfo> DiscoverEndpoints(bool includeInactiveRealtekPins = true)
    {
        var list = new List<RealtekEndpointInfo>();
        string? defRenderMm = CoreAudio.TryGetDefaultEndpointId(AudioEndpointFlow.Render, 1);
        string? defRenderComm = CoreAudio.TryGetDefaultEndpointId(AudioEndpointFlow.Render, 2);
        string? defCaptureMm = CoreAudio.TryGetDefaultEndpointId(AudioEndpointFlow.Capture, 1);
        string? defCaptureComm = CoreAudio.TryGetDefaultEndpointId(AudioEndpointFlow.Capture, 2);

        ScanFlowRegistry(AudioEndpointFlow.Render, "0.0.0.00000000", defRenderMm, defRenderComm, includeInactiveRealtekPins, list);
        ScanFlowRegistry(AudioEndpointFlow.Capture, "0.0.1.00000000", defCaptureMm, defCaptureComm, includeInactiveRealtekPins, list);

        return list
            .OrderByDescending(e => e.IsRealtek)
            .ThenByDescending(e => e.IsActive)
            .ThenByDescending(e => e.IsDefaultMultimedia)
            .ThenBy(e => e.DataFlow)
            .ThenBy(e => e.FriendlyName)
            .ToList();
    }

    private static void ScanFlowRegistry(
        AudioEndpointFlow flow,
        string prefix,
        string? defMmId,
        string? defCommId,
        bool includeInactiveRealtekPins,
        List<RealtekEndpointInfo> list)
    {
        string subPath = $@"{MmDevicesBaseKey}\{(flow == AudioEndpointFlow.Render ? "Render" : "Capture")}";
        try
        {
            using var flowKey = Registry.LocalMachine.OpenSubKey(subPath, false);
            if (flowKey == null)
                return;

            foreach (string guidName in flowKey.GetSubKeyNames())
            {
                using var epKey = flowKey.OpenSubKey(guidName, false);
                if (epKey == null)
                    continue;

                uint state = 0;
                if (epKey.GetValue("DeviceState") is int dsInt)
                    state = unchecked((uint)dsInt);

                using var propsKey = epKey.OpenSubKey("Properties", false);
                using var fxKey = epKey.OpenSubKey("FxProperties", false);
                if (propsKey == null)
                    continue;

                string friendlyName = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2")?.ToString() ?? guidName;
                string deviceDesc = propsKey.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")?.ToString() ?? string.Empty;
                string hwId = propsKey.GetValue("{9dad2fed-3c19-4cde-b3c9-1bd56be25698},0")?.ToString()
                           ?? propsKey.GetValue("{a8b865dd-2e3d-4094-ad97-e593a70c75d6},8")?.ToString()
                           ?? string.Empty;
                string drvInfo = propsKey.GetValue("{83da6326-97a6-4088-9453-a1923f573b29},3")?.ToString() ?? string.Empty;

                uint formFactor = 0;
                if (propsKey.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0") is int ffInt)
                    formFactor = unchecked((uint)ffInt);

                int propCount = propsKey.ValueCount;
                int fxCount = fxKey?.ValueCount ?? 0;

                string fxProvider = fxKey?.GetValue("{b725f130-47ef-101a-a5f1-02608c9eebac},10")?.ToString() ?? string.Empty;
                string sfxClsid = fxKey?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5")?.ToString() ?? string.Empty;
                string mfxClsid = fxKey?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6")?.ToString() ?? string.Empty;
                string efxClsid = fxKey?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7")?.ToString() ?? string.Empty;
                string uiClsid = fxKey?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},3")?.ToString() ?? string.Empty;

                bool isRealtek = deviceDesc.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
                              || fxProvider.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
                              || hwId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase)
                              || fxCount > 20;

                if (!isRealtek && state != 1)
                    continue;

                if (isRealtek && !includeInactiveRealtekPins && state != 1 && state != 8 && !friendlyName.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase))
                    continue;

                string fullEndpointId = $"{{{prefix}}}.{guidName}";
                list.Add(new RealtekEndpointInfo
                {
                    EndpointId = fullEndpointId,
                    GuidPart = guidName,
                    FriendlyName = friendlyName,
                    DeviceDescription = deviceDesc,
                    DataFlow = flow,
                    DeviceState = state,
                    IsRealtek = isRealtek,
                    IsDefaultMultimedia = string.Equals(fullEndpointId, defMmId, StringComparison.OrdinalIgnoreCase),
                    IsDefaultCommunications = string.Equals(fullEndpointId, defCommId, StringComparison.OrdinalIgnoreCase),
                    FormFactor = formFactor,
                    HardwareId = hwId,
                    DriverVersion = drvInfo,
                    EffectProviderName = fxProvider,
                    SfxClsid = sfxClsid,
                    MfxClsid = mfxClsid,
                    EfxClsid = efxClsid,
                    UiClsid = uiClsid,
                    PropertyCount = propCount,
                    FxPropertyCount = fxCount
                });
            }
        }
        catch
        {
        }
    }

    public RealtekPlaybackState ReadPlaybackState(string endpointId)
    {
        var state = new RealtekPlaybackState { EndpointId = endpointId };

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.DisableSysFx, out uint dis1) ||
            PolicyConfig.TryGetUInt32(endpointId, false, RealtekPropertyKeys.DisableSysFx, out dis1))
        {
            state.DisableAllEnhancements = dis1 != 0;
        }
        else if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.DisableSysFxAlt, out uint dis2))
        {
            state.DisableAllEnhancements = dis2 != 0;
        }

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.ImmediateMode, out uint imm))
            state.ImmediateMode = imm != 0;

        // 10-Band Equalizer
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.EqualizerEnable, out uint eqEn))
            state.EqualizerEnabled = eqEn != 0;

        if (PolicyConfig.TryGetIntArrayBlob(endpointId, true, RealtekPropertyKeys.EqualizerBandsBlob, out int[] rawBands) && rawBands.Length >= 10)
        {
            for (int i = 0; i < 10; i++)
            {
                state.EqualizerBandsDb[i] = Math.Round(Math.Clamp(rawBands[i] / 100.0, -12.0, 12.0), 1);
            }
        }
        state.MatchedPresetName = Presets.MatchPresetName(state.EqualizerBandsDb);

        // Environment (EAX Reverb)
        if (RealtekCom.TryGetEaxPreset(endpointId, out bool eaxEn, out int eaxPresetIdx))
        {
            state.EnvironmentEnabled = eaxEn;
            state.EnvironmentPresetIndex = Math.Clamp(eaxPresetIdx, 0, RealtekPropertyKeys.BuiltInEnvironments.Count - 1);
        }
        else
        {
            if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.EnvironmentEnable, out uint envEn))
                state.EnvironmentEnabled = envEn != 0;
            if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.EnvironmentPreset, out uint envIdx))
                state.EnvironmentPresetIndex = state.EnvironmentEnabled ? (int)Math.Clamp(envIdx + 1, 1, 23) : 0;
        }

        // Karaoke: Voice Cancellation & Pitch Shift
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.VoiceCancellationEnable, out uint vc))
            state.VoiceCancellationEnabled = vc != 0;

        if (PolicyConfig.TryGetInt32(endpointId, true, RealtekPropertyKeys.PitchShiftKey, out int pitch))
            state.PitchShiftSemitones = Math.Clamp(pitch, -4, 4);

        // Spatial / Surround / Loudness
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.LoudnessEqualizationEnable, out uint loud))
            state.LoudnessEqualizationEnabled = loud != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SpeakerFillEnable, out uint spkFill))
            state.SpeakerFillEnabled = spkFill != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.VirtualSurroundEnable, out uint vSurr))
            state.VirtualSurroundEnabled = vSurr != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.HeadphoneVirtualizationEnable, out uint hpVirt))
            state.HeadphoneVirtualizationEnabled = hpVirt != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.BassManagementEnable, out uint bassMgt))
            state.BassManagementEnabled = bassMgt != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SpeakerExpansionEnable, out uint spkExp))
            state.SpeakerExpansionEnabled = spkExp != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SwapCenterSubwooferEnable, out uint swapSub))
            state.SwapCenterSubwooferEnabled = swapSub != 0;

        // Room Correction
        if (RealtekCom.TryGetRoomCorrection(endpointId, out bool rcEn, out bool rcFeet, out int[] rcGains, out int[] rcDists))
        {
            state.RoomCorrectionEnabled = rcEn;
            state.RoomCorrectionUnitIsFeet = rcFeet;
            state.RoomCorrectionGainDb = rcGains;
            state.RoomCorrectionDistance = rcDists;
        }
        else if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.RoomCorrectionEnable, out uint rcEnProp))
        {
            state.RoomCorrectionEnabled = rcEnProp != 0;
        }

        // SounzReal / OmniSound / BassBack / MP3+
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.OmniSoundEnableCore, out uint omni))
            state.OmniSoundEnabled = omni != 0;
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SounzRealOmniHeadphoneEnable, out uint omniHp))
            state.OmniHeadphoneEnabled = omniHp != 0;
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SounzRealVoiceBackEnable, out uint vb))
            state.VoiceBackEnabled = vb != 0;
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.BassBackEnableCore, out uint bb))
            state.BassBackEnabled = bb != 0;
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.SounzRealMp3PlusEnable, out uint mp3))
            state.Mp3PlusEnabled = mp3 != 0;

        return state;
    }

    public bool ApplyEqualizer(string endpointId, bool enabled, double[] bandsDb)
    {
        int[] rawBands = new int[10];
        for (int i = 0; i < 10; i++)
        {
            double db = i < bandsDb.Length ? Math.Clamp(bandsDb[i], -12.0, 12.0) : 0.0;
            rawBands[i] = (int)Math.Round(db * 100.0);
        }

        bool ok1 = PolicyConfig.TrySetIntArrayBlob(endpointId, true, RealtekPropertyKeys.EqualizerBandsBlob, rawBands);
        PolicyConfig.TrySetIntArrayBlob(endpointId, true, RealtekPropertyKeys.EqualizerBandsBlobSpk, rawBands);
        PolicyConfig.TrySetIntArrayBlob(endpointId, true, RealtekPropertyKeys.EqualizerBandsBlobHp, rawBands);

        uint enVal = enabled ? 1u : 0u;
        bool ok2 = PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.EqualizerEnable, enVal);
        PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.EqualizerEnableSpk, enVal);
        PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.EqualizerEnableHp, enVal);

        // Trigger RltkAPO64 notification refresh
        TouchSysFxNotification(endpointId);
        return ok1 && ok2;
    }

    public bool ApplyEnvironmentReverb(string endpointId, bool enabled, int environmentIndex)
    {
        bool actualEnable = enabled && environmentIndex > 0;
        if (RealtekCom.TrySetEaxPreset(endpointId, actualEnable, (uint)Math.Max(0, environmentIndex)))
        {
            TouchSysFxNotification(endpointId);
            return true;
        }

        bool ok = PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.EnvironmentEnable, actualEnable ? 1u : 0u);
        if (environmentIndex > 0)
        {
            PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.EnvironmentPreset, (uint)(environmentIndex - 1));
        }
        TouchSysFxNotification(endpointId);
        return ok;
    }

    public bool ApplyPlaybackState(RealtekPlaybackState state)
    {
        string id = state.EndpointId;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.DisableSysFx, state.DisableAllEnhancements ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, false, RealtekPropertyKeys.DisableSysFx, state.DisableAllEnhancements ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.ImmediateMode, state.ImmediateMode ? 2u : 0u);

        ApplyEqualizer(id, state.EqualizerEnabled, state.EqualizerBandsDb);
        ApplyEnvironmentReverb(id, state.EnvironmentEnabled, state.EnvironmentPresetIndex);

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.VoiceCancellationEnable, state.VoiceCancellationEnabled ? 1u : 0u);
        PolicyConfig.TrySetInt32(id, true, RealtekPropertyKeys.PitchShiftKey, Math.Clamp(state.PitchShiftSemitones, -4, 4));

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.LoudnessEqualizationEnable, state.LoudnessEqualizationEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SpeakerFillEnable, state.SpeakerFillEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.VirtualSurroundEnable, state.VirtualSurroundEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.HeadphoneVirtualizationEnable, state.HeadphoneVirtualizationEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.BassManagementEnable, state.BassManagementEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SpeakerExpansionEnable, state.SpeakerExpansionEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SwapCenterSubwooferEnable, state.SwapCenterSubwooferEnabled ? 1u : 0u);

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.RoomCorrectionEnable, state.RoomCorrectionEnabled ? 1u : 0u);
        RealtekCom.TrySetRoomCorrection(id, state.RoomCorrectionEnabled, state.RoomCorrectionUnitIsFeet, state.RoomCorrectionGainDb, state.RoomCorrectionDistance);

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.OmniSoundEnableCore, state.OmniSoundEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SounzRealOmniHeadphoneEnable, state.OmniHeadphoneEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SounzRealVoiceBackEnable, state.VoiceBackEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.BassBackEnableCore, state.BassBackEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.SounzRealMp3PlusEnable, state.Mp3PlusEnabled ? 1u : 0u);

        TouchSysFxNotification(id);
        return true;
    }

    public RealtekCaptureState ReadCaptureState(string endpointId)
    {
        var state = new RealtekCaptureState { EndpointId = endpointId };

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.DisableSysFx, out uint dis))
            state.DisableAllEnhancements = dis != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicNoiseSuppressionEnable, out uint ns1) ||
            PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicVoiceNsEnable, out ns1))
        {
            state.NoiseSuppressionEnabled = ns1 != 0;
        }

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicAecEnable, out uint aec1) ||
            PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicVoiceAecEnable, out aec1))
        {
            state.AcousticEchoCancellationEnabled = aec1 != 0;
        }

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicVoiceBeamFormingEnable, out uint bf1) ||
            PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicBeamFormingLegacy, out bf1))
        {
            state.BeamFormingEnabled = bf1 != 0;
        }

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicDcOffsetCancellationEnable, out uint dcOff))
            state.DcOffsetCancellationEnabled = dcOff != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicMonoMicrophoneEnable, out uint mono))
            state.MonoMicrophoneEnabled = mono != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicConferenceMode, out uint conf))
            state.ConferenceModeEnabled = conf != 0;

        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.MicKeystrokeSuppressionEnable, out uint kns))
            state.KeystrokeSuppressionEnabled = kns != 0;

        if (CoreAudio.TryGetEndpointVolume(endpointId, out float masterVol, out _, out _, out bool muted))
        {
            state.MasterVolumePercent = masterVol;
            state.MasterMuted = muted;
        }

        var topoNodes = CoreAudio.QueryTopologySubunits(endpointId);
        var boostNode = topoNodes.FirstOrDefault(n =>
            n.Name.Contains("Boost", StringComparison.OrdinalIgnoreCase) ||
            (n.MinDb >= 0f && n.MaxDb >= 20f && n.StepDb >= 6f));

        if (boostNode != null)
        {
            state.SupportsMicBoost = true;
            state.MicBoostDb = boostNode.LeftLevelDb;
            state.MicBoostMinDb = boostNode.MinDb;
            state.MicBoostMaxDb = boostNode.MaxDb;
            state.MicBoostStepDb = boostNode.StepDb;
        }
        else
        {
            state.SupportsMicBoost = true;
            state.MicBoostMinDb = 0f;
            state.MicBoostMaxDb = 30f;
            state.MicBoostStepDb = 10f;
        }

        return state;
    }

    public bool ApplyCaptureState(RealtekCaptureState state)
    {
        string id = state.EndpointId;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.DisableSysFx, state.DisableAllEnhancements ? 1u : 0u);

        uint ns = state.NoiseSuppressionEnabled ? 1u : 0u;
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicNoiseSuppressionEnable, ns);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicVoiceNsEnable, ns);

        uint aec = state.AcousticEchoCancellationEnabled ? 1u : 0u;
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicAecEnable, aec);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicVoiceAecEnable, aec);

        uint bf = state.BeamFormingEnabled ? 1u : 0u;
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicVoiceBeamFormingEnable, bf);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicBeamFormingLegacy, bf);

        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicDcOffsetCancellationEnable, state.DcOffsetCancellationEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicMonoMicrophoneEnable, state.MonoMicrophoneEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicConferenceMode, state.ConferenceModeEnabled ? 1u : 0u);
        PolicyConfig.TrySetUInt32(id, true, RealtekPropertyKeys.MicKeystrokeSuppressionEnable, state.KeystrokeSuppressionEnabled ? 1u : 0u);

        CoreAudio.TrySetEndpointMasterVolume(id, state.MasterVolumePercent, state.MasterMuted);

        var topoNodes = CoreAudio.QueryTopologySubunits(id);
        var boostNode = topoNodes.FirstOrDefault(n =>
            n.Name.Contains("Boost", StringComparison.OrdinalIgnoreCase) ||
            (n.MinDb >= 0f && n.MaxDb >= 20f && n.StepDb >= 6f));
        if (boostNode != null)
        {
            CoreAudio.TrySetTopologySubunit(id, boostNode.LocalId, state.MicBoostDb, state.MicBoostDb, false);
        }

        TouchSysFxNotification(id);
        return true;
    }

    public AudioFormatState ReadFormatState(string endpointId)
    {
        var fmt = new AudioFormatState { EndpointId = endpointId };
        if (PolicyConfig.TryGetDeviceFormat(endpointId, out var wfx))
        {
            fmt.Channels = wfx.nChannels;
            fmt.SampleRate = (int)wfx.nSamplesPerSec;
            fmt.BitsPerSample = wfx.wBitsPerSample;
            fmt.ValidBitsPerSample = wfx.wValidBitsPerSample > 0 ? wfx.wValidBitsPerSample : wfx.wBitsPerSample;
            fmt.ChannelMask = wfx.dwChannelMask != 0 ? wfx.dwChannelMask : 0x3u;
        }

        if (PolicyConfig.TryGetUInt32(endpointId, false, RealtekPropertyKeys.EndpointPhysicalSpeakers, out uint physMask) && physMask != 0)
            fmt.PhysicalSpeakersMask = physMask;
        else
            fmt.PhysicalSpeakersMask = fmt.ChannelMask;

        if (PolicyConfig.TryGetUInt32(endpointId, false, RealtekPropertyKeys.EndpointFullRangeSpeakers, out uint fullMask) && fullMask != 0)
            fmt.FullRangeSpeakersMask = fullMask;
        else
            fmt.FullRangeSpeakersMask = fmt.PhysicalSpeakersMask;

        if (PolicyConfig.TryGetBool(endpointId, false, RealtekPropertyKeys.ExclusiveModeAllowed, out bool exAllowed))
            fmt.ExclusiveModeAllowed = exAllowed;

        if (PolicyConfig.TryGetBool(endpointId, false, RealtekPropertyKeys.ExclusiveModePriority, out bool exPrio))
            fmt.ExclusiveModePriority = exPrio;

        return fmt;
    }

    public bool ApplyFormatAndSpeakerConfig(AudioFormatState fmt)
    {
        string id = fmt.EndpointId;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        PolicyConfig.TrySetUInt32(id, false, RealtekPropertyKeys.EndpointPhysicalSpeakers, fmt.PhysicalSpeakersMask);
        PolicyConfig.TrySetUInt32(id, false, RealtekPropertyKeys.EndpointFullRangeSpeakers, fmt.FullRangeSpeakersMask);
        PolicyConfig.TrySetBool(id, false, RealtekPropertyKeys.ExclusiveModeAllowed, fmt.ExclusiveModeAllowed);
        PolicyConfig.TrySetBool(id, false, RealtekPropertyKeys.ExclusiveModePriority, fmt.ExclusiveModePriority);

        int chCount = fmt.PhysicalSpeakersMask switch
        {
            0x3 => 2,
            0x33 => 4,
            0x3F or 0x60F => 6,
            0x63F => 8,
            _ => fmt.Channels
        };

        return PolicyConfig.TrySetDeviceFormat(id, chCount, fmt.SampleRate, fmt.ValidBitsPerSample, fmt.PhysicalSpeakersMask);
    }

    public List<AnalogMixerSubunit> GetAnalogMixerSubunits(string endpointId)
    {
        var liveNodes = CoreAudio.QueryTopologySubunits(endpointId);
        if (liveNodes.Count > 0)
            return liveNodes;

        return DriverHardware.ReadRegistryFallbackMixerSubunits();
    }

    public List<PropertyEntryInfo> InspectEndpointProperties(RealtekEndpointInfo endpoint)
    {
        var entries = new List<PropertyEntryInfo>();
        string flowFolder = endpoint.DataFlow == AudioEndpointFlow.Render ? "Render" : "Capture";
        string epRegPath = $@"{MmDevicesBaseKey}\{flowFolder}\{endpoint.GuidPart}";

        ReadStoreFromRegistry(epRegPath, "FxProperties", entries);
        ReadStoreFromRegistry(epRegPath, "Properties", entries);

        return entries
            .OrderByDescending(e => e.StoreName == "FxProperties")
            .ThenBy(e => e.Category == "Uncategorized")
            .ThenBy(e => e.Category)
            .ThenBy(e => e.FriendlyName)
            .ToList();
    }

    private static void ReadStoreFromRegistry(string epRegPath, string storeSubkey, List<PropertyEntryInfo> entries)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{epRegPath}\{storeSubkey}", false);
            if (key == null)
                return;

            foreach (string valName in key.GetValueNames())
            {
                object? rawVal = key.GetValue(valName);
                if (rawVal == null)
                    continue;

                string normKey = valName.ToLowerInvariant();
                RealtekPropertyKeys.KnownMetadata.TryGetValue(normKey, out var meta);

                string valueType;
                string decoded;
                string rawHex = string.Empty;

                if (rawVal is int iVal)
                {
                    valueType = "REG_DWORD";
                    decoded = $"{iVal} (0x{iVal:X})";
                }
                else if (rawVal is string sVal)
                {
                    valueType = "REG_SZ";
                    decoded = sVal;
                }
                else if (rawVal is byte[] bytes)
                {
                    valueType = $"REG_BINARY ({bytes.Length} B)";
                    rawHex = string.Join(" ", bytes.Take(48).Select(b => b.ToString("X2"))) + (bytes.Length > 48 ? " ..." : "");
                    decoded = DecodeSerializedPropVariant(normKey, bytes);
                }
                else
                {
                    valueType = rawVal.GetType().Name;
                    decoded = rawVal.ToString() ?? string.Empty;
                }

                entries.Add(new PropertyEntryInfo
                {
                    StoreName = storeSubkey,
                    KeyString = valName,
                    FriendlyName = meta?.Name ?? valName,
                    Category = meta?.Category ?? "Uncategorized",
                    Description = meta?.Description ?? "Endpoint / APO property value",
                    ValueType = valueType,
                    DecodedValue = decoded,
                    RawHex = rawHex
                });
            }
        }
        catch
        {
        }
    }

    private static string DecodeSerializedPropVariant(string normKey, byte[] bytes)
    {
        if (bytes.Length < 8)
            return $"Bytes[{bytes.Length}]";

        ushort vt = BitConverter.ToUInt16(bytes, 0);

        // Check if this is the 10-band EQ blob ({4b361010-def7-43a1-a5dc-071d955b62f7},6)
        if (normKey == RealtekPropertyKeys.EqualizerBandsBlob.ToString() && vt == 0x41 && bytes.Length >= 48)
        {
            var sb = new StringBuilder("EQ 10-Band: ");
            for (int b = 0; b < 10; b++)
            {
                int val = BitConverter.ToInt32(bytes, 8 + b * 4);
                if (b > 0) sb.Append(", ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0}={1:+0.0;-0.0;0.0}dB", RealtekPropertyKeys.EqBandLabels[b], val / 100.0);
            }
            return sb.ToString();
        }

        return vt switch
        {
            3 when bytes.Length >= 12 => $"VT_I4 = {BitConverter.ToInt32(bytes, 8)}",
            19 when bytes.Length >= 12 => $"VT_UI4 = {BitConverter.ToUInt32(bytes, 8)}",
            11 when bytes.Length >= 10 => $"VT_BOOL = {(BitConverter.ToInt16(bytes, 8) != 0 ? "True" : "False")}",
            0x41 when bytes.Length >= 8 => $"VT_BLOB (cbSize={BitConverter.ToInt32(bytes, 4)})",
            _ => $"PROPVARIANT (vt=0x{vt:X}, len={bytes.Length})"
        };
    }

    private void TouchSysFxNotification(string endpointId)
    {
        // Re-asserting DisableSysFx=0 (when enabled) triggers RltkAPO64.dll's IMMNotificationClient::OnPropertyValueChanged handler
        if (PolicyConfig.TryGetUInt32(endpointId, true, RealtekPropertyKeys.DisableSysFx, out uint cur))
        {
            PolicyConfig.TrySetUInt32(endpointId, true, RealtekPropertyKeys.DisableSysFx, cur);
        }
    }
}
