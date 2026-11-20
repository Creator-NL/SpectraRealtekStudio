using System;
using System.Runtime.InteropServices;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct PropVariantNative
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public long val1;
    public IntPtr val2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WaveFormatExtensible
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

[ComImport, Guid("d39dffb3-3f1b-4341-97c3-e8415128a834"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRtkPolicyConfigClient
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, out IntPtr ppwfxMixFormat);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int Format, out IntPtr ppwfxDeviceFormat);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, IntPtr pwfxDeviceFormat, uint iType);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int keyid, ref PropertyKey key, out PropVariantNative pv);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int keyid, ref PropertyKey key, ref PropVariantNative pv);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int fVisible);
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWindowsPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, out IntPtr ppwfxMixFormat);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int bDefault, out IntPtr ppwfxDeviceFormat);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, IntPtr pEndpointFormat, IntPtr pMixFormat);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, IntPtr pmftPeriod);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, out int pMode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, IntPtr mode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int bFxStore, ref PropertyKey key, out PropVariantNative pv);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int bFxStore, ref PropertyKey key, ref PropVariantNative pv);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int bVisible);
}

/// <summary>
/// Non-admin RPC client for reading and writing Realtek FxProperties, endpoint Properties,
/// shared-mode formats, default endpoints, and endpoint visibility via RtkCfg64.dll (with Windows IPolicyConfig fallback).
/// </summary>
public sealed class PolicyConfigInterop
{
    private static readonly Guid RtkCfgClsid = new("AF099A7C-45D2-4CE6-9327-EEAAB0395444");
    private static readonly Guid WinPolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariantNative pvar);

    private IRtkPolicyConfigClient? CreateRtkClient()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(RtkCfgClsid);
            if (t != null)
            {
                return Activator.CreateInstance(t) as IRtkPolicyConfigClient;
            }
        }
        catch
        {
            // Fallback to Windows PolicyConfig
        }
        return null;
    }

    private IWindowsPolicyConfig? CreateWinClient()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(WinPolicyConfigClsid);
            if (t != null)
            {
                return Activator.CreateInstance(t) as IWindowsPolicyConfig;
            }
        }
        catch
        {
        }
        return null;
    }

    public int GetPropertyRaw(string endpointId, bool fxStore, PropertyKey key, out PropVariantNative pv)
    {
        pv = default;
        int storeId = fxStore ? 1 : 0;
        var rtk = CreateRtkClient();
        if (rtk != null)
        {
            return rtk.GetPropertyValue(endpointId, storeId, ref key, out pv);
        }

        var win = CreateWinClient();
        if (win != null)
        {
            return win.GetPropertyValue(endpointId, storeId, ref key, out pv);
        }

        return unchecked((int)0x80004005);
    }

    public int SetPropertyRaw(string endpointId, bool fxStore, PropertyKey key, ref PropVariantNative pv)
    {
        int storeId = fxStore ? 1 : 0;
        var rtk = CreateRtkClient();
        if (rtk != null)
        {
            return rtk.SetPropertyValue(endpointId, storeId, ref key, ref pv);
        }

        var win = CreateWinClient();
        if (win != null)
        {
            return win.SetPropertyValue(endpointId, storeId, ref key, ref pv);
        }

        return unchecked((int)0x80004005);
    }

    public bool TryGetUInt32(string endpointId, bool fxStore, PropertyKey key, out uint value)
    {
        value = 0;
        if (GetPropertyRaw(endpointId, fxStore, key, out var pv) != 0)
            return false;

        try
        {
            if (pv.vt == 19 /* VT_UI4 */ || pv.vt == 3 /* VT_I4 */ || pv.vt == 18 /* VT_UI2 */ || pv.vt == 2 /* VT_I2 */)
            {
                value = unchecked((uint)(pv.val1 & 0xFFFFFFFF));
                return true;
            }
            if (pv.vt == 11 /* VT_BOOL */)
            {
                value = (pv.val1 & 0xFFFF) != 0 ? 1u : 0u;
                return true;
            }
            if (pv.vt == 0x41 /* VT_BLOB */ && pv.val1 >= 4 && pv.val2 != IntPtr.Zero)
            {
                value = unchecked((uint)Marshal.ReadInt32(pv.val2));
                return true;
            }
            return false;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    public bool TrySetUInt32(string endpointId, bool fxStore, PropertyKey key, uint value)
    {
        var pv = new PropVariantNative
        {
            vt = 19, // VT_UI4
            val1 = value
        };
        return SetPropertyRaw(endpointId, fxStore, key, ref pv) == 0;
    }

    public bool TryGetInt32(string endpointId, bool fxStore, PropertyKey key, out int value)
    {
        value = 0;
        if (GetPropertyRaw(endpointId, fxStore, key, out var pv) != 0)
            return false;

        try
        {
            if (pv.vt == 3 /* VT_I4 */ || pv.vt == 19 /* VT_UI4 */)
            {
                value = unchecked((int)(pv.val1 & 0xFFFFFFFF));
                return true;
            }
            if (pv.vt == 2 /* VT_I2 */)
            {
                value = (short)(pv.val1 & 0xFFFF);
                return true;
            }
            if (pv.vt == 0x41 /* VT_BLOB */ && pv.val1 >= 4 && pv.val2 != IntPtr.Zero)
            {
                value = Marshal.ReadInt32(pv.val2);
                return true;
            }
            return false;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    public bool TrySetInt32(string endpointId, bool fxStore, PropertyKey key, int value)
    {
        var pv = new PropVariantNative
        {
            vt = 3, // VT_I4
            val1 = unchecked((uint)value)
        };
        return SetPropertyRaw(endpointId, fxStore, key, ref pv) == 0;
    }

    public bool TryGetBool(string endpointId, bool fxStore, PropertyKey key, out bool value)
    {
        value = false;
        if (GetPropertyRaw(endpointId, fxStore, key, out var pv) != 0)
            return false;

        try
        {
            if (pv.vt == 11 /* VT_BOOL */)
            {
                value = (short)(pv.val1 & 0xFFFF) != 0;
                return true;
            }
            if (pv.vt == 19 || pv.vt == 3)
            {
                value = (pv.val1 & 0xFFFFFFFF) != 0;
                return true;
            }
            return false;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    public bool TrySetBool(string endpointId, bool fxStore, PropertyKey key, bool value)
    {
        var pv = new PropVariantNative
        {
            vt = 11, // VT_BOOL (-1 = VARIANT_TRUE, 0 = VARIANT_FALSE)
            val1 = value ? -1 : 0
        };
        return SetPropertyRaw(endpointId, fxStore, key, ref pv) == 0;
    }

    public bool TryGetBlob(string endpointId, bool fxStore, PropertyKey key, out byte[] blob)
    {
        blob = Array.Empty<byte>();
        if (GetPropertyRaw(endpointId, fxStore, key, out var pv) != 0)
            return false;

        try
        {
            if (pv.vt == 0x41 /* VT_BLOB */ && pv.val1 > 0 && pv.val1 <= 65536 && pv.val2 != IntPtr.Zero)
            {
                int size = (int)pv.val1;
                blob = new byte[size];
                Marshal.Copy(pv.val2, blob, 0, size);
                return true;
            }
            return false;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    public bool TrySetBlob(string endpointId, bool fxStore, PropertyKey key, byte[] blob)
    {
        if (blob == null || blob.Length == 0)
            return false;

        IntPtr pMem = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pMem, blob.Length);
            var pv = new PropVariantNative
            {
                vt = 0x41, // VT_BLOB
                val1 = blob.Length,
                val2 = pMem
            };
            return SetPropertyRaw(endpointId, fxStore, key, ref pv) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pMem);
        }
    }

    public bool TryGetIntArrayBlob(string endpointId, bool fxStore, PropertyKey key, out int[] values)
    {
        values = Array.Empty<int>();
        if (!TryGetBlob(endpointId, fxStore, key, out var bytes) || bytes.Length < 4)
            return false;

        int count = bytes.Length / 4;
        values = new int[count];
        Buffer.BlockCopy(bytes, 0, values, 0, count * 4);
        return true;
    }

    public bool TrySetIntArrayBlob(string endpointId, bool fxStore, PropertyKey key, int[] values)
    {
        if (values == null || values.Length == 0)
            return false;

        byte[] bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return TrySetBlob(endpointId, fxStore, key, bytes);
    }

    public bool TryGetDeviceFormat(string endpointId, out WaveFormatExtensible format)
    {
        format = default;
        IntPtr pFormat = IntPtr.Zero;
        try
        {
            var rtk = CreateRtkClient();
            if (rtk != null && rtk.GetDeviceFormat(endpointId, 0, out pFormat) == 0 && pFormat != IntPtr.Zero)
            {
                format = Marshal.PtrToStructure<WaveFormatExtensible>(pFormat);
                return true;
            }

            var win = CreateWinClient();
            if (win != null && win.GetDeviceFormat(endpointId, 0, out pFormat) == 0 && pFormat != IntPtr.Zero)
            {
                format = Marshal.PtrToStructure<WaveFormatExtensible>(pFormat);
                return true;
            }
            return false;
        }
        finally
        {
            if (pFormat != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pFormat);
        }
    }

    public bool TrySetDeviceFormat(string endpointId, int channels, int sampleRate, int validBitsPerSample, uint channelMask)
    {
        ushort containerBits = validBitsPerSample > 16 ? (ushort)32 : (ushort)16;
        ushort blockAlign = (ushort)(channels * (containerBits / 8));
        uint avgBytesPerSec = (uint)(sampleRate * blockAlign);

        var wfx = new WaveFormatExtensible
        {
            wFormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = avgBytesPerSec,
            nBlockAlign = blockAlign,
            wBitsPerSample = containerBits,
            cbSize = 22,
            wValidBitsPerSample = (ushort)validBitsPerSample,
            dwChannelMask = channelMask,
            SubFormat = KsDataFormatSubtypePcm
        };

        int structSize = Marshal.SizeOf<WaveFormatExtensible>();
        IntPtr pWfx = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(wfx, pWfx, false);
            var rtk = CreateRtkClient();
            if (rtk != null)
            {
                if (rtk.SetDeviceFormat(endpointId, pWfx, 0) == 0)
                    return true;
            }

            var win = CreateWinClient();
            if (win != null)
            {
                if (win.SetDeviceFormat(endpointId, pWfx, IntPtr.Zero) == 0)
                    return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pWfx);
        }
    }

    public bool TrySetDefaultEndpoint(string endpointId)
    {
        var rtk = CreateRtkClient();
        if (rtk != null)
        {
            int hr0 = rtk.SetDefaultEndpoint(endpointId, 0); // eConsole
            int hr1 = rtk.SetDefaultEndpoint(endpointId, 1); // eMultimedia
            int hr2 = rtk.SetDefaultEndpoint(endpointId, 2); // eCommunications
            return hr0 == 0 || hr1 == 0 || hr2 == 0;
        }

        var win = CreateWinClient();
        if (win != null)
        {
            int hr0 = win.SetDefaultEndpoint(endpointId, 0);
            int hr1 = win.SetDefaultEndpoint(endpointId, 1);
            int hr2 = win.SetDefaultEndpoint(endpointId, 2);
            return hr0 == 0 || hr1 == 0 || hr2 == 0;
        }
        return false;
    }

    public bool TrySetEndpointVisibility(string endpointId, bool visible)
    {
        int vis = visible ? 1 : 0;
        var rtk = CreateRtkClient();
        if (rtk != null)
            return rtk.SetEndpointVisibility(endpointId, vis) == 0;

        var win = CreateWinClient();
        if (win != null)
            return win.SetEndpointVisibility(endpointId, vis) == 0;

        return false;
    }
}
