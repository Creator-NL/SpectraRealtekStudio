using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Interop;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    int RegisterEndpointNotificationCallback(IntPtr pClient);
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out uint pcDevices);
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    int GetState(out uint pdwState);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr pNotify);
    int UnregisterControlChangeNotify(IntPtr pNotify);
    int GetChannelCount(out uint pnChannelCount);
    int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
    int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
    int GetMasterVolumeLevel(out float pfLevelDB);
    int GetMasterVolumeLevelScalar(out float pfLevel);
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    int VolumeStepUp(ref Guid pguidEventContext);
    int VolumeStepDown(ref Guid pguidEventContext);
    int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    int GetPeakValue(out float pfPeak);
    int GetMeteringChannelCount(out uint pnChannelCount);
    int GetChannelsPeakValues(uint u32ChannelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] afPeakValues);
    int QueryHardwareSupport(out uint pdwHardwareSupportMask);
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    int GetConnectorCount(out uint pCount);
    int GetConnector(uint nIndex, out IConnector ppConnector);
    int GetSubunitCount(out uint pCount);
    int GetSubunit(uint nIndex, out IPart ppSubunit);
    int GetPartById(uint nId, out IPart ppPart);
    int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
}

[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    int GetType(out int pType);
    int GetDataFlow(out int pFlow);
    int ConnectTo(IConnector pConnectTo);
    int Disconnect();
    int IsConnected(out int pbConnected);
    int GetConnectedTo(out IConnector ppConTo);
    int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrConnectorId);
    int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrName);
    int GetLocalId(out uint pnId);
    int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrGlobalId);
    int GetPartType(out int pPartType);
    int GetSubType(out Guid pSubType);
    int GetControlInterfaceCount(out uint pCount);
    int GetControlInterface(uint nIndex, out IntPtr ppInterfaceDesc);
    int EnumPartsIncoming(out IntPtr ppParts);
    int EnumPartsOutgoing(out IntPtr ppParts);
    int GetTopologyObject(out IDeviceTopology ppTopology);
    int Activate(int dwClsCtx, ref Guid refiid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
}

[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioVolumeLevel
{
    int GetChannelCount(out uint pcChannels);
    int GetLevelRange(uint nChannel, out float pfMinLevelDB, out float pfMaxLevelDB, out float pfStepping);
    int GetLevel(uint nChannel, out float pfLevelDB);
    int SetLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
}

[ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMute
{
    int SetMute(int bMuted, ref Guid pguidEventContext);
    int GetMute(out int pbMuted);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int pRetVal);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int pRetVal);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid pRetVal);
    [PreserveSig] int SetGroupingParam(ref Guid @override, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetProcessId(out uint pRetVal);
    [PreserveSig] int IsSystemSoundsSession();
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float fLevel, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float pfLevel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}

public sealed class CoreAudioInterop
{
    private static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IidMeterInfo = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid IidDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid IidVolumeLevel = new("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC");
    private static readonly Guid IidAudioMute = new("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E");
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly object _sessionSync = new();
    private readonly Dictionary<string, (float BaseScalar, float LastAppliedScalar)> _sessionGainTrackers = new(StringComparer.OrdinalIgnoreCase);
    private float _currentPreMixFactor = 1.0f;
    private string _trackedEndpointId = string.Empty;

    public string? TryGetDefaultEndpointId(AudioEndpointFlow flow, int role)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint((int)flow, role, out var dev) == 0 && dev != null)
            {
                if (dev.GetId(out string id) == 0)
                    return id;
            }
        }
        catch
        {
        }
        return null;
    }

    public bool TryGetEndpointVolume(string endpointId, out float masterPercent, out float leftPercent, out float rightPercent, out bool isMuted)
    {
        masterPercent = 100f;
        leftPercent = 100f;
        rightPercent = 100f;
        isMuted = false;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return false;

            Guid iid = IidEndpointVolume;
            if (dev.Activate(ref iid, 1, IntPtr.Zero, out object obj) != 0 || obj is not IAudioEndpointVolume vol)
                return false;

            vol.GetMasterVolumeLevelScalar(out float masterScalar);
            vol.GetMute(out isMuted);
            masterPercent = MathF.Round(masterScalar * 100f, 1);

            vol.GetChannelCount(out uint ch);
            if (ch >= 1)
            {
                vol.GetChannelVolumeLevelScalar(0, out float l);
                leftPercent = MathF.Round(l * 100f, 1);
            }
            if (ch >= 2)
            {
                vol.GetChannelVolumeLevelScalar(1, out float r);
                rightPercent = MathF.Round(r * 100f, 1);
            }
            else
            {
                rightPercent = leftPercent;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEndpointMasterVolume(string endpointId, float masterPercent, bool isMuted)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return false;

            Guid iid = IidEndpointVolume;
            if (dev.Activate(ref iid, 1, IntPtr.Zero, out object obj) != 0 || obj is not IAudioEndpointVolume vol)
                return false;

            Guid ctx = Guid.Empty;
            float scalar = Math.Clamp(masterPercent / 100f, 0f, 1f);
            vol.SetMasterVolumeLevelScalar(scalar, ref ctx);
            vol.SetMute(isMuted, ref ctx);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEndpointChannelBalance(string endpointId, float leftPercent, float rightPercent)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return false;

            Guid iid = IidEndpointVolume;
            if (dev.Activate(ref iid, 1, IntPtr.Zero, out object obj) != 0 || obj is not IAudioEndpointVolume vol)
                return false;

            Guid ctx = Guid.Empty;
            vol.GetChannelCount(out uint ch);
            if (ch >= 1)
                vol.SetChannelVolumeLevelScalar(0, Math.Clamp(leftPercent / 100f, 0f, 1f), ref ctx);
            if (ch >= 2)
                vol.SetChannelVolumeLevelScalar(1, Math.Clamp(rightPercent / 100f, 0f, 1f), ref ctx);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetPeakMeter(string endpointId, out float masterPeak, out float leftPeak, out float rightPeak)
    {
        masterPeak = 0f;
        leftPeak = 0f;
        rightPeak = 0f;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return false;

            Guid iid = IidMeterInfo;
            if (dev.Activate(ref iid, 1, IntPtr.Zero, out object obj) != 0 || obj is not IAudioMeterInformation meter)
                return false;

            meter.GetPeakValue(out masterPeak);
            meter.GetMeteringChannelCount(out uint ch);
            if (ch >= 2)
            {
                float[] peaks = new float[ch];
                if (meter.GetChannelsPeakValues(ch, peaks) == 0)
                {
                    leftPeak = peaks[0];
                    rightPeak = peaks[1];
                }
            }
            else
            {
                leftPeak = masterPeak;
                rightPeak = masterPeak;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public List<AnalogMixerSubunit> QueryTopologySubunits(string endpointId)
    {
        var list = new List<AnalogMixerSubunit>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return list;

            Guid iidTopo = IidDeviceTopology;
            if (dev.Activate(ref iidTopo, 1, IntPtr.Zero, out object oTopo) != 0 || oTopo is not IDeviceTopology epTopo)
                return list;

            if (epTopo.GetConnector(0, out var epConn) != 0 || epConn == null)
                return list;

            if (epConn.GetConnectedTo(out var hwConn) != 0 || hwConn is not IPart hwPart)
                return list;

            if (hwPart.GetTopologyObject(out var filterTopo) != 0 || filterTopo == null)
                return list;

            filterTopo.GetSubunitCount(out uint subCount);
            for (uint i = 0; i < subCount; i++)
            {
                if (filterTopo.GetSubunit(i, out var sub) != 0 || sub == null)
                    continue;

                sub.GetName(out string sName);
                sub.GetLocalId(out uint localId);

                Guid iidVol = IidVolumeLevel;
                Guid iidMute = IidAudioMute;
                sub.Activate(1, ref iidVol, out object? oVol);
                sub.Activate(1, ref iidMute, out object? oMute);

                var vol = oVol as IAudioVolumeLevel;
                var mute = oMute as IAudioMute;
                if (vol == null && mute == null)
                    continue;

                var item = new AnalogMixerSubunit
                {
                    IdKey = $"topo_{localId}",
                    LocalId = localId,
                    Name = string.IsNullOrWhiteSpace(sName) ? $"Node 0x{localId:X}" : sName,
                    SourceDescription = "Hardware Analog Topology Subunit",
                    SupportsVolume = vol != null,
                    SupportsMute = mute != null,
                    IsBackedByRegistry = false
                };

                if (vol != null)
                {
                    vol.GetChannelCount(out uint ch);
                    item.ChannelCount = (int)Math.Max(1, ch);
                    if (ch > 0)
                    {
                        vol.GetLevelRange(0, out float minDb, out float maxDb, out float stepDb);
                        vol.GetLevel(0, out float lDb);
                        float rDb = lDb;
                        if (ch > 1)
                            vol.GetLevel(1, out rDb);

                        item.MinDb = minDb;
                        item.MaxDb = maxDb;
                        item.StepDb = stepDb > 0 ? stepDb : 1f;
                        item.LeftLevelDb = lDb;
                        item.RightLevelDb = rDb;

                        float span = Math.Max(0.1f, maxDb - minDb);
                        item.LeftLevelPercent = MathF.Round(Math.Clamp((lDb - minDb) / span * 100f, 0f, 100f), 1);
                        item.RightLevelPercent = MathF.Round(Math.Clamp((rDb - minDb) / span * 100f, 0f, 100f), 1);
                    }
                }

                if (mute != null)
                {
                    mute.GetMute(out int m);
                    item.IsMuted = m != 0;
                }

                list.Add(item);
            }
        }
        catch
        {
            // Sandbox or restricted topology access falls back to DriverHardwareService registry nodes
        }
        return list;
    }

    public bool TrySetTopologySubunit(string endpointId, uint localId, float leftDb, float rightDb, bool isMuted)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return false;

            Guid iidTopo = IidDeviceTopology;
            if (dev.Activate(ref iidTopo, 1, IntPtr.Zero, out object oTopo) != 0 || oTopo is not IDeviceTopology epTopo)
                return false;

            if (epTopo.GetConnector(0, out var epConn) != 0 || epConn == null)
                return false;

            if (epConn.GetConnectedTo(out var hwConn) != 0 || hwConn is not IPart hwPart)
                return false;

            if (hwPart.GetTopologyObject(out var filterTopo) != 0 || filterTopo == null)
                return false;

            if (filterTopo.GetPartById(localId, out var sub) != 0 || sub == null)
                return false;

            Guid ctx = Guid.Empty;
            Guid iidVol = IidVolumeLevel;
            if (sub.Activate(1, ref iidVol, out object? oVol) == 0 && oVol is IAudioVolumeLevel vol)
            {
                vol.GetChannelCount(out uint ch);
                if (ch >= 1)
                    vol.SetLevel(0, leftDb, ref ctx);
                if (ch >= 2)
                    vol.SetLevel(1, rightDb, ref ctx);
            }

            Guid iidMute = IidAudioMute;
            if (sub.Activate(1, ref iidMute, out object? oMute) == 0 && oMute is IAudioMute mute)
            {
                mute.SetMute(isMuted ? 1 : 0, ref ctx);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies real-time 32-bit floating-point pre-mix digital headroom gain (-24.0 dB to 0.0 dB)
    /// across all active/present WASAPI audio sessions on the endpoint via IAudioSessionManager2.
    /// Runs BEFORE the Windows Mixer, BEFORE peak meters (IAudioMeterInformation), and works
    /// even when Realtek System Effects (RltkAPO64) are bypassed.
    /// Preserves each app's relative Windows Volume Mixer balance.
    /// </summary>
    public int TryApplyPreMixDigitalGainDb(string endpointId, double gainDb)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return 0;

        float targetFactor = gainDb >= -0.02
            ? 1.0f
            : Math.Clamp(MathF.Pow(10f, (float)Math.Clamp(gainDb, -36.0, 0.0) / 20f), 0.01f, 1.0f);

        lock (_sessionSync)
        {
            // If switching endpoints while trimmed, restore previous endpoint's sessions first
            if (!string.IsNullOrEmpty(_trackedEndpointId) &&
                !string.Equals(_trackedEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) &&
                _sessionGainTrackers.Count > 0)
            {
                RestoreSessionsInternal(_trackedEndpointId);
            }

            _trackedEndpointId = endpointId;

            if (targetFactor >= 0.999f && _sessionGainTrackers.Count == 0 && _currentPreMixFactor >= 0.999f)
            {
                return 0;
            }

            int affected = 0;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                    return 0;

                Guid iidMgr = IidAudioSessionManager2;
                if (dev.Activate(ref iidMgr, 23, IntPtr.Zero, out object oMgr) != 0 || oMgr is not IAudioSessionManager2 mgr)
                    return 0;

                if (mgr.GetSessionEnumerator(out var sEnum) != 0 || sEnum == null)
                    return 0;

                sEnum.GetCount(out int count);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Guid ctx = Guid.Empty;
                uint myPid = (uint)Environment.ProcessId;

                for (int i = 0; i < count; i++)
                {
                    if (sEnum.GetSession(i, out var ctrl) != 0 || ctrl == null)
                        continue;

                    ctrl.GetState(out int state);
                    if (state == 2) // AudioSessionStateExpired
                        continue;

                    if (ctrl is not ISimpleAudioVolume vol)
                        continue;

                    string sid = $"idx_{i}";
                    uint pid = 0;
                    if (ctrl is IAudioSessionControl2 ctrl2)
                    {
                        ctrl2.GetProcessId(out pid);
                        if (ctrl2.GetSessionInstanceIdentifier(out string instId) == 0 && !string.IsNullOrWhiteSpace(instId))
                            sid = instId;
                    }

                    // Skip our own test-tone session so speaker test chimes stay predictable
                    if (pid == myPid)
                        continue;

                    seenIds.Add(sid);

                    if (vol.GetMasterVolume(out float curScalar) != 0)
                        continue;

                    float baseScalar;
                    if (_sessionGainTrackers.TryGetValue(sid, out var tracked))
                    {
                        // Detect if the user manually adjusted this app inside Windows Volume Mixer
                        if (MathF.Abs(curScalar - tracked.LastAppliedScalar) > 0.025f)
                        {
                            baseScalar = _currentPreMixFactor > 0.015f
                                ? Math.Clamp(curScalar / _currentPreMixFactor, 0f, 1f)
                                : Math.Clamp(curScalar, 0f, 1f);
                        }
                        else
                        {
                            baseScalar = tracked.BaseScalar;
                        }
                    }
                    else
                    {
                        baseScalar = Math.Clamp(curScalar, 0f, 1f);
                    }

                    float newScalar = Math.Clamp(baseScalar * targetFactor, 0f, 1f);
                    if (MathF.Abs(curScalar - newScalar) > 0.0025f)
                    {
                        vol.SetMasterVolume(newScalar, ref ctx);
                    }

                    _sessionGainTrackers[sid] = (baseScalar, newScalar);
                    affected++;
                }

                // Prune closed/expired sessions
                var stale = new List<string>();
                foreach (string key in _sessionGainTrackers.Keys)
                {
                    if (!seenIds.Contains(key))
                        stale.Add(key);
                }
                foreach (string key in stale)
                    _sessionGainTrackers.Remove(key);

                _currentPreMixFactor = targetFactor;
                if (targetFactor >= 0.999f)
                {
                    _sessionGainTrackers.Clear();
                }
            }
            catch
            {
            }

            return affected;
        }
    }

    public void RestoreAllPreMixSessionGains(string? endpointId = null)
    {
        lock (_sessionSync)
        {
            string targetEp = !string.IsNullOrWhiteSpace(endpointId) ? endpointId : _trackedEndpointId;
            if (!string.IsNullOrWhiteSpace(targetEp))
            {
                RestoreSessionsInternal(targetEp);
            }
            _sessionGainTrackers.Clear();
            _currentPreMixFactor = 1.0f;
        }
    }

    private void RestoreSessionsInternal(string endpointId)
    {
        if (_sessionGainTrackers.Count == 0 && _currentPreMixFactor >= 0.999f)
            return;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out var dev) != 0 || dev == null)
                return;

            Guid iidMgr = IidAudioSessionManager2;
            if (dev.Activate(ref iidMgr, 23, IntPtr.Zero, out object oMgr) != 0 || oMgr is not IAudioSessionManager2 mgr)
                return;

            if (mgr.GetSessionEnumerator(out var sEnum) != 0 || sEnum == null)
                return;

            sEnum.GetCount(out int count);
            Guid ctx = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                if (sEnum.GetSession(i, out var ctrl) != 0 || ctrl is not ISimpleAudioVolume vol)
                    continue;

                string sid = $"idx_{i}";
                if (ctrl is IAudioSessionControl2 ctrl2 &&
                    ctrl2.GetSessionInstanceIdentifier(out string instId) == 0 &&
                    !string.IsNullOrWhiteSpace(instId))
                {
                    sid = instId;
                }

                if (_sessionGainTrackers.TryGetValue(sid, out var tracked))
                {
                    vol.SetMasterVolume(Math.Clamp(tracked.BaseScalar, 0f, 1f), ref ctx);
                }
            }
        }
        catch
        {
        }
    }
}
