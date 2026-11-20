using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpectraRealtekStudio.Core.Interop;

[ComImport, Guid("cf743829-4c33-457d-82cb-a00de3308cd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void SetChannelNum(int Num);
    void Init(out int ReturnVal);
    void GetMaxChannelNum(out int ReturnVal);
    void GetChannelNum(out int ReturnVal);
    void GetFrontHeadPhoneOuputMode(out int ReturnVal);
    void GetFrontMicInOutputMode(out int ReturnVal);
    void GetRearLineInOutputMode(out int ReturnVal);
    void GetRearMicInOutputMode(out int ReturnVal);
    void GetFrontHeadPhoneUAJOutputMode(out int ReturnVal);
    void GetFrontMicInUAJOutputMode(out int ReturnVal);
    void SetFrontMicInUAJDeviceMode(int DeviceType);
    void SetFrontHeadPhoneUAJDeviceMode(int DeviceType);
    void SetRearLineInOutputMode(int InOutMode);
    void SetRearMicInOutputMode(int InOutMode);
    void GetCenLfeSwapStatus(out int ReturnVal);
    void SetCenLfeSwapStatus(int bStatus);
    void SetFrontHPTunOnRearSpeaker(int Enable);
    void GetFrontHPTunOnRearSpeaker(out int ReturnVal);
}

[ComImport, Guid("3f8f4aeb-9090-4f5b-98f2-000a08fc4a04"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20065
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetDeviceCount(out uint ulCount);
    void GetDeviceName(uint ulIndex, IntPtr cDeviceName);
    void VistaGetEqEnable(uint ulDeviceIndex, out int bEqEnable);
    void VistaSetEqEnable(uint ulDeviceIndex, int bEqEnable);
    void VistaGetEqLevel(uint ulDeviceIndex, uint ulEqIndex, out int lEqLevel);
    void VistaSetEqLevel(uint ulDeviceIndex, uint ulEqIndex, int lEqLevel);
}

[ComImport, Guid("3f8f4aeb-9090-4f5b-98f2-000a08fc4a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20068
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetVistaD2PowerStateControl(out uint dwPowerControl);
    void SetVistaD2PowerStateControl(uint dwPowerControl);
    void VistaGetEqIndex(uint ulDeviceIndex, out int Index);
    void VistaSetEqIndex(uint ulDeviceIndex, int Index);
    void VistaGetVoiceCancelEnabled(uint ulDeviceIndex, out int bEnabled);
    void VistaSetVoiceCancelEnabled(uint ulDeviceIndex, int bEnabled);
    void VistaGetKeyValue(uint ulDeviceIndex, out int Key);
    void VistaSetKeyValue(uint ulDeviceIndex, int Key);
}

[ComImport, Guid("3f8f4aeb-9090-4f5b-98f2-000a08fc4a07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20069
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void VistaGetMicNoiseSuppression(uint ulDeviceIndex, out int bEnable);
    void VistaSetMicNoiseSuppression(uint ulDeviceIndex, int bEnable);
    void VistaGetMicBeamForming(uint ulDeviceIndex, out int bEnable);
    void VistaSetMicBeamForming(uint ulDeviceIndex, int bEnable);
    void VistaGetMicAEC(uint ulDeviceIndex, out int bEnable);
    void VistaSetMicAEC(uint ulDeviceIndex, int bEnable);
}

[ComImport, Guid("48a11455-e534-4e9d-9630-9c71bb97a58f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20080
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetCodecAddress(out uint dwCodecAddress, out uint fgID);
    uint SendVerb(uint cad, uint WidgetId, uint VerbId, uint Payload);
    uint SendParameter(uint cad, uint WidgetId, uint ParameterId);
}

[ComImport, Guid("bc057e92-289c-422e-a61a-48462d97e089"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20082
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void VistaGetDeviceInfo(uint ulDeviceIndex, out uint DeviceFormFactor, out int bIsPlayback, out int bIsDefDevice, IntPtr wcDeviceID);
}

[ComImport, Guid("8b8ff54b-0054-4918-bcbf-4406a4ab7c54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20083
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void VistaGetRoomCorrectionEnabled(uint ulDeviceIndex, out int bEnabled);
    void VistaSetRoomCorrectionEnabled(uint ulDeviceIndex, int bEnabled);
    void VistaGetRoomCorrectScaleMode(uint ulDeviceIndex, out int ScaleMode);
    void VistaSetRoomCorrectScaleMode(uint ulDeviceIndex, int ScaleMode);
    void VistaGetRoomCorrectGain(uint ulDeviceIndex, int SpeakerIndex, out int Gain);
    void VistaSetRoomCorrectGain(uint ulDeviceIndex, int SpeakerIndex, int Gain);
    void VistaGetRoomCorrectDistance(uint ulDeviceIndex, int SpeakerIndex, out int Distance);
    void VistaSetRoomCorrectDistance(uint ulDeviceIndex, int SpeakerIndex, int Distance);
    void VistaGetRoomCorrectDistRange(uint ulDeviceIndex, int SpeakerIndex, out int MinRange, out int MaxRange);
}

[ComImport, Guid("af97231b-a45c-4b51-8a6c-6e3c8cebe4b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20086
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void VistaGetEaxIndex(uint ulDeviceIndex, out uint EaxIndex);
    void VistaSetEaxIndex(uint ulDeviceIndex, uint EaxIndex);
    void VistaGetEaxEnabled(uint ulDeviceIndex, out int Enabled);
    void VistaSetEaxEnabled(uint ulDeviceIndex, int Enabled);
}

[ComImport, Guid("b19fa640-2789-4162-972e-17919767f08e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20155
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetJackPinCount(out uint getCount);
    void GetJackPinStatus(uint PinIndex, out uint PinWidget, out uint PinDefDeviceType);
    void GetJackPlugStatus(uint getMethod, uint PinIndex, out uint PlugStatus);
}

[ComImport, Guid("22475866-0333-4717-9d93-c1c368002c9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20163
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetDisableFPJD(out uint ulEnable);
    void SetDisableFPJD(uint ulEnable);
}

[ComImport, Guid("88d64301-7aca-4950-a7f2-876cbaa5db87"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPropSet20179
{
    void GetTypeInfoCount(out uint pctinfo);
    void GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    void GetIDsOfNames([In] ref Guid riid, [MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgdispid);
    void Invoke(int dispidMember, [In] ref Guid riid, uint lcid, ushort wFlags, IntPtr pdispparams, IntPtr pvarResult, IntPtr pexcepinfo, IntPtr puArgErr);
    void GetPBMultistream(out uint ulEnable);
    void SetPBMultistream(uint ulEnable);
    void GetRECMultistream(out uint ulEnable);
    void SetRECMultistream(uint ulEnable);
}

/// <summary>
/// Safe managed wrapper around RtCOM64.dll's PropSet COM object ({C6606CA5-2108-4CAF-8E52-1953F2DBF716}).
/// Always executes Init() and GetDeviceCount() before invoking per-device methods.
/// </summary>
public sealed class RealtekComInterop
{
    private static readonly Guid PropSetClsid = new("C6606CA5-2108-4CAF-8E52-1953F2DBF716");

    private object? CreateInitializedPropSet(out Dictionary<string, uint> deviceIndexByEndpointId, out int hwInitResult)
    {
        deviceIndexByEndpointId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        hwInitResult = 0;

        try
        {
            var t = Type.GetTypeFromCLSID(PropSetClsid);
            if (t == null)
                return null;

            object? obj = Activator.CreateInstance(t);
            if (obj is not IRtkPropSet ps)
                return null;

            try { ps.Init(out hwInitResult); } catch { }

            if (obj is IRtkPropSet20065 ps65 && obj is IRtkPropSet20082 ps82)
            {
                ps65.GetDeviceCount(out uint count);
                IntPtr pIdBuf = Marshal.AllocHGlobal(1024);
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        ps82.VistaGetDeviceInfo(i, out _, out _, out _, pIdBuf);
                        string? id = Marshal.PtrToStringUni(pIdBuf);
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            deviceIndexByEndpointId[id] = i;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pIdBuf);
                }
            }

            return obj;
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetRoomCorrection(string endpointId, out bool enabled, out bool isFeet, out int[] gainsDb, out int[] distances)
    {
        enabled = false;
        isFeet = false;
        gainsDb = new int[8];
        distances = new int[8];

        var obj = CreateInitializedPropSet(out var map, out _);
        if (obj is not IRtkPropSet20083 ps83 || !map.TryGetValue(endpointId, out uint idx))
            return false;

        try
        {
            ps83.VistaGetRoomCorrectionEnabled(idx, out int en);
            enabled = en != 0;
            ps83.VistaGetRoomCorrectScaleMode(idx, out int scale);
            isFeet = scale != 0;

            for (int spk = 0; spk < 8; spk++)
            {
                try { ps83.VistaGetRoomCorrectGain(idx, spk, out gainsDb[spk]); } catch { }
                try { ps83.VistaGetRoomCorrectDistance(idx, spk, out distances[spk]); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetRoomCorrection(string endpointId, bool enabled, bool isFeet, int[] gainsDb, int[] distances)
    {
        var obj = CreateInitializedPropSet(out var map, out _);
        if (obj is not IRtkPropSet20083 ps83 || !map.TryGetValue(endpointId, out uint idx))
            return false;

        try
        {
            ps83.VistaSetRoomCorrectionEnabled(idx, enabled ? 1 : 0);
            try { ps83.VistaSetRoomCorrectScaleMode(idx, isFeet ? 1 : 0); } catch { }
            for (int spk = 0; spk < Math.Min(8, gainsDb.Length); spk++)
            {
                try { ps83.VistaSetRoomCorrectGain(idx, spk, gainsDb[spk]); } catch { }
            }
            for (int spk = 0; spk < Math.Min(8, distances.Length); spk++)
            {
                try { ps83.VistaSetRoomCorrectDistance(idx, spk, distances[spk]); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEaxPreset(string endpointId, bool enabled, uint presetIndex)
    {
        var obj = CreateInitializedPropSet(out var map, out _);
        if (obj is not IRtkPropSet20086 ps86 || !map.TryGetValue(endpointId, out uint idx))
            return false;

        try
        {
            ps86.VistaSetEaxEnabled(idx, enabled ? 1 : 0);
            if (presetIndex > 0)
            {
                ps86.VistaSetEaxIndex(idx, presetIndex - 1);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetEaxPreset(string endpointId, out bool enabled, out int presetIndex)
    {
        enabled = false;
        presetIndex = 0;
        var obj = CreateInitializedPropSet(out var map, out _);
        if (obj is not IRtkPropSet20086 ps86 || !map.TryGetValue(endpointId, out uint idx))
            return false;

        try
        {
            ps86.VistaGetEaxEnabled(idx, out int en);
            ps86.VistaGetEaxIndex(idx, out uint eaxIdx);
            enabled = en != 0;
            presetIndex = enabled ? (int)(eaxIdx + 1) : 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySendHdaVerb(uint widgetId, uint verbId, uint payload, out uint response, out uint codecAddress, out string statusMessage)
    {
        response = 0;
        codecAddress = 0;
        var obj = CreateInitializedPropSet(out _, out int hwInit);
        if (obj is not IRtkPropSet20080 ps80)
        {
            statusMessage = "RtCOM64.dll IPropSet20080 interface is not available.";
            return false;
        }

        try
        {
            ps80.GetCodecAddress(out codecAddress, out uint fgId);
            if (verbId == 0xF00)
            {
                response = ps80.SendParameter(codecAddress, widgetId, payload);
            }
            else
            {
                response = ps80.SendVerb(codecAddress, widgetId, verbId, payload);
            }
            statusMessage = $"OK (CAD={codecAddress}, FG={fgId}, HwInit={hwInit}) => 0x{response:X8}";
            return true;
        }
        catch (Exception ex)
        {
            statusMessage = $"Direct HDA Verb call returned: {ex.Message} (HwInit={hwInit}). Note: Kernel HDA filter requires normal desktop execution outside sandbox.";
            return false;
        }
    }

    public Dictionary<byte, bool> TryQueryJackPlugStatesFromCom()
    {
        var result = new Dictionary<byte, bool>();
        var obj = CreateInitializedPropSet(out _, out _);
        if (obj is not IRtkPropSet20155 ps155)
            return result;

        try
        {
            ps155.GetJackPinCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                ps155.GetJackPinStatus(i, out uint widget, out _);
                ps155.GetJackPlugStatus(0, i, out uint plug);
                result[(byte)widget] = plug != 0;
            }
        }
        catch
        {
        }
        return result;
    }

    public bool TrySetFrontPanelJackDetectionDisabled(bool disabled)
    {
        var obj = CreateInitializedPropSet(out _, out _);
        if (obj is not IRtkPropSet20163 ps163)
            return false;

        try
        {
            ps163.SetDisableFPJD(disabled ? 1u : 0u);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetMultiStreaming(bool playbackMultiStream, bool recordMultiStream)
    {
        var obj = CreateInitializedPropSet(out _, out _);
        if (obj is not IRtkPropSet20179 ps179)
            return false;

        try
        {
            ps179.SetPBMultistream(playbackMultiStream ? 1u : 0u);
            ps179.SetRECMultistream(recordMultiStream ? 1u : 0u);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
