using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

/// <summary>
/// Reads and configures Realtek High Definition Audio kernel driver settings,
/// decodes Intel HDA Pin Configuration Default verbs (0x71C..0x71F) for all physical jacks,
/// supports Jack Retasking, and reads fallback analog mixer monitoring nodes.
/// </summary>
public sealed class DriverHardwareService
{
    private const string MediaClassRegistryPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public string? FindRealtekDriverKeyPath()
    {
        try
        {
            using var mediaKey = Registry.LocalMachine.OpenSubKey(MediaClassRegistryPath, false);
            if (mediaKey == null)
                return null;

            foreach (string subName in mediaKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !char.IsDigit(subName[0]))
                    continue;

                using var subKey = mediaKey.OpenSubKey(subName, false);
                string? desc = subKey?.GetValue("DriverDesc")?.ToString();
                if (!string.IsNullOrWhiteSpace(desc) && desc.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                {
                    return $@"{MediaClassRegistryPath}\{subName}";
                }
            }
        }
        catch
        {
        }
        return null;
    }

    public DriverHardwareTweaks ReadHardwareTweaks()
    {
        var tweaks = new DriverHardwareTweaks();
        string? keyPath = FindRealtekDriverKeyPath();
        if (string.IsNullOrEmpty(keyPath))
            return tweaks;

        tweaks.DriverClassKeyPath = $@"HKLM\{keyPath}";
        try
        {
            using var drvKey = Registry.LocalMachine.OpenSubKey(keyPath, false);
            if (drvKey == null)
                return tweaks;

            tweaks.DriverDescription = drvKey.GetValue("DriverDesc")?.ToString() ?? "Realtek High Definition Audio";
            tweaks.DriverVersion = drvKey.GetValue("DriverVersion")?.ToString() ?? string.Empty;
            tweaks.DriverDate = drvKey.GetValue("DriverDate")?.ToString() ?? string.Empty;
            tweaks.InfPath = drvKey.GetValue("InfPath")?.ToString() ?? string.Empty;
            tweaks.CodecHardwareId = drvKey.GetValue("MatchingDeviceId")?.ToString() ?? string.Empty;

            using (var settingsEx = drvKey.OpenSubKey("SettingsEx", false))
            {
                if (settingsEx != null)
                {
                    foreach (string valName in settingsEx.GetValueNames())
                    {
                        if (valName.StartsWith("Get instanceID at post processing", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = valName.Split('=');
                            if (parts.Length >= 2)
                            {
                                tweaks.CodecHardwareId = parts[1].Trim();
                            }
                        }
                    }
                }
            }

            tweaks.CodecChipName = DecodeCodecChipName(tweaks.CodecHardwareId);
            tweaks.MotherboardSubsysId = DecodeMotherboardVendor(tweaks.CodecHardwareId);

            using (var settingsKey = drvKey.OpenSubKey("Settings", false))
            {
                if (settingsKey != null)
                {
                    tweaks.SettingsSubkeyName = settingsKey.GetSubKeyNames().FirstOrDefault() ?? string.Empty;
                }
            }

            using (var globalKey = drvKey.OpenSubKey("GlobalSettings", false))
            {
                if (globalKey != null)
                {
                    tweaks.MuteRearWhenFrontHpPlugged = ReadRegDwordOrBinary(globalKey, "HpShutsOffRearSpeaker", 0) != 0;
                    tweaks.IndependentFrontRearStreams = ReadRegDwordOrBinary(globalKey, "EnableDynamicDevices", 1) != 0;
                    tweaks.FrontPanelHdAudioMode = ReadRegDwordOrBinary(globalKey, "HpMode", 1) != 0;
                    tweaks.SwapCenterLfeInDriver = ReadRegDwordOrBinary(globalKey, "CenterLfeSwap", 0) != 0;
                    tweaks.SpdifEchoesRearRender = ReadRegDwordOrBinary(globalKey, "SpdifOutputEchosRearRenderWhenNoAc3", 1) != 0;
                    tweaks.EnableWaveRt = ReadRegDwordOrBinary(globalKey, "EnableWaveRT", 1) != 0;
                    tweaks.DcOffsetWorkaround = ReadRegDwordOrBinary(globalKey, "DcOffsetWorkaround", 1) != 0;
                }
            }

            using (var powerKey = drvKey.OpenSubKey("PowerSettings", false))
            {
                if (powerKey != null)
                {
                    uint perfIdle = ReadRegDwordOrBinary(powerKey, "PerformanceIdleTime", 0);
                    uint idleState = ReadRegDwordOrBinary(powerKey, "IdlePowerState", 3);
                    tweaks.AntiPopDisableIdlePowerDown = perfIdle == 0 || idleState == 0;
                }
            }
        }
        catch
        {
        }

        return tweaks;
    }

    public List<JackPinInfo> ReadJackPins(Dictionary<byte, bool>? comPlugStates = null)
    {
        var pins = new List<JackPinInfo>();
        string? keyPath = FindRealtekDriverKeyPath();
        if (string.IsNullOrEmpty(keyPath))
            return pins;

        try
        {
            using var verbsKey = Registry.LocalMachine.OpenSubKey($@"{keyPath}\PinConfigOverrideVerbs", false);
            if (verbsKey == null)
                return pins;

            uint numVerbs = ReadRegDwordOrBinary(verbsKey, "NumVerbs", 0);
            if (numVerbs == 0)
                numVerbs = 56;

            for (int i = 0; i + 3 < numVerbs; i += 4)
            {
                byte[]? v0 = verbsKey.GetValue(i.ToString("D4")) as byte[];
                byte[]? v1 = verbsKey.GetValue((i + 1).ToString("D4")) as byte[];
                byte[]? v2 = verbsKey.GetValue((i + 2).ToString("D4")) as byte[];
                byte[]? v3 = verbsKey.GetValue((i + 3).ToString("D4")) as byte[];

                if (v0 == null || v1 == null || v2 == null || v3 == null ||
                    v0.Length < 4 || v1.Length < 4 || v2.Length < 4 || v3.Length < 4)
                    continue;

                uint verb0 = BitConverter.ToUInt32(v0, 0);
                byte widgetId = (byte)((verb0 >> 20) & 0xFF);
                uint rawConfig = (uint)(v0[0] | (v1[0] << 8) | (v2[0] << 16) | (v3[0] << 24));

                var pin = DecodeHdaPinConfig(i, widgetId, rawConfig);
                if (comPlugStates != null && comPlugStates.TryGetValue(widgetId, out bool plugged))
                {
                    pin.IsPluggedIn = plugged;
                }
                else
                {
                    // Check active endpoints for default plugged indicator on Rear Green (0x14) and Rear Blue (0x1A)
                    if (widgetId == 0x14 || widgetId == 0x1A)
                        pin.IsPluggedIn = true;
                }
                pins.Add(pin);
            }
        }
        catch
        {
        }

        return pins;
    }

    public static JackPinInfo DecodeHdaPinConfig(int verbStartIndex, byte widgetId, uint rawConfig)
    {
        byte connectivity = (byte)((rawConfig >> 30) & 0x3);
        byte location = (byte)((rawConfig >> 24) & 0x3F);
        byte defDevice = (byte)((rawConfig >> 20) & 0xF);
        byte connType = (byte)((rawConfig >> 16) & 0xF);
        byte color = (byte)((rawConfig >> 12) & 0xF);
        byte assoc = (byte)((rawConfig >> 4) & 0xF);
        byte seq = (byte)(rawConfig & 0xF);

        string panelLoc = (location & 0x0F) switch
        {
            0x01 => "Rear Panel",
            0x02 => "Front Panel",
            _ => (location >> 4) == 1 ? "Internal / Onboard" : "External Panel"
        };

        // On many MSI / ASUS ALC892 boards, PinConfigOverrideVerbs encodes standard ATX 6-jack colors by widget ID
        (string colorName, string colorHex) = (widgetId, color) switch
        {
            (0x14, _) => ("Lime Green (Front Out)", "#22E87A"),
            (0x15, _) => ("Black (Rear Out)", "#505A74"),
            (0x16, _) => ("Orange (Center / Sub)", "#FF9820"),
            (0x17, _) => ("Grey (Side Surround)", "#9BA8C0"),
            (0x18, _) => ("Pink (Rear Mic In)", "#FF5CA8"),
            (0x19, _) => ("Pink (Front Mic In)", "#FF5CA8"),
            (0x1A, _) => ("Light Blue (Line In)", "#22B8FF"),
            (0x1B, _) => ("Lime Green (Front Headphone)", "#22E87A"),
            (_, 0x1) => ("Black", "#505A74"),
            (_, 0x2) => ("Grey", "#9BA8C0"),
            (_, 0x3) => ("Blue", "#22B8FF"),
            (_, 0x4) => ("Green", "#22E87A"),
            (_, 0x5) => ("Red", "#FF4040"),
            (_, 0x6) => ("Orange", "#FF9820"),
            (_, 0x8) => ("Purple", "#B050FF"),
            (_, 0x9) => ("Pink", "#FF5CA8"),
            _ => ("Digital / Internal", "#7F8BB0")
        };

        string roleName = (widgetId, defDevice, seq) switch
        {
            (0x14, 0x0, _) => "Front Speaker Out (L/R)",
            (0x15, 0x0, _) => "Rear Speaker Out (RL/RR)",
            (0x16, 0x0, _) => "Center / Subwoofer Out",
            (0x17, 0x0, _) => "Side Speaker Out (SL/SR)",
            (_, 0x0, _) => "Line Out / Speaker Out",
            (_, 0x1, _) => "Internal Speaker Out",
            (_, 0x2, _) => "Headphone Out",
            (_, 0x3, _) => "CD Audio In",
            (_, 0x4, _) => "SPDIF Digital Out",
            (_, 0x5, _) => "Digital Audio Out",
            (_, 0x8, _) => "Line In",
            (_, 0xA, _) => "Microphone In",
            (_, 0xC, _) => "SPDIF Digital In",
            _ => "Unassigned / Custom"
        };

        string connector = connType switch
        {
            0x1 => "3.5mm Stereo Jack",
            0x2 => "1/4\" Stereo Jack",
            0x3 => "ATAPI Internal",
            0x4 => "RCA Coaxial",
            0x5 => "Optical TOSLINK",
            _ => "Internal Header"
        };

        return new JackPinInfo
        {
            VerbStartIndex = verbStartIndex,
            PinWidgetId = widgetId,
            RawPinConfig = rawConfig,
            PanelLocation = panelLoc,
            JackColorName = colorName,
            JackColorHex = colorHex,
            ConnectorType = connector,
            DefaultDeviceCode = defDevice,
            AssignedRoleName = roleName,
            ConnectivityCode = connectivity,
            DefaultAssociation = assoc,
            Sequence = seq
        };
    }

    public List<AnalogMixerSubunit> ReadRegistryFallbackMixerSubunits()
    {
        var list = new List<AnalogMixerSubunit>();
        string? keyPath = FindRealtekDriverKeyPath();
        if (string.IsNullOrEmpty(keyPath))
            return list;

        try
        {
            using var settingsKey = Registry.LocalMachine.OpenSubKey($@"{keyPath}\Settings", false);
            if (settingsKey == null)
                return list;

            string? subName = settingsKey.GetSubKeyNames().FirstOrDefault();
            if (string.IsNullOrEmpty(subName))
                return list;

            using var drvSub = settingsKey.OpenSubKey(subName, false);
            if (drvSub == null)
                return list;

            var monitoringNodes = new (string GuidSuffix, string FriendlyName, string Desc)[]
            {
                ("{eeb9ff66-875e-44d0-a7bc-94f8ff8a6b2e}", "Rear Blue Line-In Monitoring", "Direct hardware analog passthrough to Speakers"),
                ("{16238c4c-5b07-4acf-9b1d-72056187853c}", "Front Pink Mic-In Monitoring", "Direct hardware analog passthrough to Speakers"),
                ("{930a479f-0487-4baa-9672-4c7c36c0efb2}", "Rear Pink Mic-In Monitoring", "Direct hardware analog passthrough to Speakers"),
                ("{ba697e2a-bc14-4c0d-9e16-3935f2f15e08}", "Aux / Side Analog Monitoring", "Direct hardware analog passthrough to Speakers")
            };

            uint idCounter = 100;
            foreach (var node in monitoringNodes)
            {
                string v0Name = $"Vol0_RearLineOutWave3_{node.GuidSuffix}";
                string v1Name = $"Vol1_RearLineOutWave3_{node.GuidSuffix}";
                string m0Name = $"Mute0_RearLineOutWave3_{node.GuidSuffix}";

                byte[]? v0Bytes = drvSub.GetValue(v0Name) as byte[];
                byte[]? v1Bytes = drvSub.GetValue(v1Name) as byte[];
                byte[]? m0Bytes = drvSub.GetValue(m0Name) as byte[];

                if (v0Bytes == null && m0Bytes == null)
                    continue;

                // Realtek stores dB in 1/65536th dB fixed-point int32 (e.g. 0xFFDD8000 = -34.5 dB)
                float lDb = v0Bytes != null && v0Bytes.Length >= 4 ? BitConverter.ToInt32(v0Bytes, 0) / 65536f : -34.5f;
                float rDb = v1Bytes != null && v1Bytes.Length >= 4 ? BitConverter.ToInt32(v1Bytes, 0) / 65536f : lDb;
                bool muted = m0Bytes != null && m0Bytes.Length >= 4 && BitConverter.ToInt32(m0Bytes, 0) != 0;

                float minDb = -65.25f;
                float maxDb = 12.0f;
                float span = maxDb - minDb;

                list.Add(new AnalogMixerSubunit
                {
                    IdKey = node.GuidSuffix,
                    LocalId = idCounter++,
                    Name = node.FriendlyName,
                    SourceDescription = node.Desc,
                    ChannelCount = 2,
                    MinDb = minDb,
                    MaxDb = maxDb,
                    StepDb = 1.5f,
                    LeftLevelDb = MathF.Round(lDb, 2),
                    RightLevelDb = MathF.Round(rDb, 2),
                    LeftLevelPercent = MathF.Round(Math.Clamp((lDb - minDb) / span * 100f, 0f, 100f), 1),
                    RightLevelPercent = MathF.Round(Math.Clamp((rDb - minDb) / span * 100f, 0f, 100f), 1),
                    IsMuted = muted,
                    SupportsVolume = true,
                    SupportsMute = true,
                    IsBackedByRegistry = true
                });
            }
        }
        catch
        {
        }

        return list;
    }

    public bool TryApplyDriverTweaks(DriverHardwareTweaks tweaks, out string statusMessage)
    {
        string? keyPath = FindRealtekDriverKeyPath();
        if (string.IsNullOrEmpty(keyPath))
        {
            statusMessage = "Realtek driver registry key not found.";
            return false;
        }

        string script = $@"
$base = 'HKLM:\{keyPath}'
Set-ItemProperty -Path ""$base\GlobalSettings"" -Name 'HpShutsOffRearSpeaker' -Value ([byte[]]@({(tweaks.MuteRearWhenFrontHpPlugged ? 1 : 0)},0,0,0)) -Type Binary
Set-ItemProperty -Path ""$base\GlobalSettings"" -Name 'EnableDynamicDevices' -Value ([byte[]]@({(tweaks.IndependentFrontRearStreams ? 1 : 0)},0,0,0)) -Type Binary
Set-ItemProperty -Path ""$base\GlobalSettings"" -Name 'HpMode' -Value ([byte[]]@({(tweaks.FrontPanelHdAudioMode ? 1 : 0)},0,0,0)) -Type Binary
Set-ItemProperty -Path ""$base\GlobalSettings"" -Name 'CenterLfeSwap' -Value ([byte[]]@({(tweaks.SwapCenterLfeInDriver ? 1 : 0)},0,0,0)) -Type Binary
Set-ItemProperty -Path ""$base\GlobalSettings"" -Name 'SpdifOutputEchosRearRenderWhenNoAc3' -Value ([byte[]]@({(tweaks.SpdifEchoesRearRender ? 1 : 0)},0,0,0)) -Type Binary
Set-ItemProperty -Path ""$base\PowerSettings"" -Name 'IdlePowerState' -Value ([byte[]]@({(tweaks.AntiPopDisableIdlePowerDown ? 0 : 3)},0,0,0)) -Type Binary
";
        return ExecuteRegistryPowerShell(script, out statusMessage);
    }

    public bool TryRetaskJackPin(JackPinInfo pin, byte newDefaultDeviceCode, out string statusMessage)
    {
        string? keyPath = FindRealtekDriverKeyPath();
        if (string.IsNullOrEmpty(keyPath))
        {
            statusMessage = "Realtek driver registry key not found.";
            return false;
        }

        // Verb 2 (offset +2) carries DefaultDevice in bits [7:4] and ConnectionType in bits [3:0]
        byte oldByte2 = (byte)((pin.RawPinConfig >> 16) & 0xFF);
        byte newByte2 = (byte)((oldByte2 & 0x0F) | ((newDefaultDeviceCode & 0x0F) << 4));
        byte widgetByte = (byte)((pin.PinWidgetId << 4) | 0x07);
        string regValName = (pin.VerbStartIndex + 2).ToString("D4");

        string script = $@"
$verbsPath = 'HKLM:\{keyPath}\PinConfigOverrideVerbs'
Set-ItemProperty -Path $verbsPath -Name '{regValName}' -Value ([byte[]]@(0x{newByte2:X2}, 0x1E, 0x{widgetByte:X2}, 0x01)) -Type Binary
";
        if (ExecuteRegistryPowerShell(script, out statusMessage))
        {
            statusMessage = $"Retasked Pin 0x{pin.PinWidgetId:X2} ({pin.JackColorName}) to device code 0x{newDefaultDeviceCode:X}. Restart audio device or reboot to apply new pin topology.";
            return true;
        }
        return false;
    }

    private static bool ExecuteRegistryPowerShell(string script, out string statusMessage)
    {
        try
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"spectra_rtk_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempFile, script);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempFile}\"",
                UseShellExecute = !IsRunningAsAdministrator(),
                CreateNoWindow = IsRunningAsAdministrator()
            };
            if (!IsRunningAsAdministrator())
            {
                psi.Verb = "runas";
            }

            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            try { File.Delete(tempFile); } catch { }

            if (proc != null && proc.ExitCode == 0)
            {
                statusMessage = "Hardware driver registry settings updated successfully.";
                return true;
            }

            statusMessage = $"Registry update exited with code {proc?.ExitCode ?? -1}.";
            return false;
        }
        catch (Exception ex)
        {
            statusMessage = $"Could not apply driver registry settings: {ex.Message}";
            return false;
        }
    }

    private static uint ReadRegDwordOrBinary(RegistryKey key, string name, uint defaultVal)
    {
        object? val = key.GetValue(name);
        if (val is int i)
            return unchecked((uint)i);
        if (val is uint u)
            return u;
        if (val is byte[] b && b.Length >= 4)
            return BitConverter.ToUInt32(b, 0);
        if (val is string s && uint.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out uint parsed))
            return parsed;
        return defaultVal;
    }

    private static string DecodeCodecChipName(string hwId)
    {
        if (string.IsNullOrWhiteSpace(hwId))
            return "Realtek High Definition Audio Codec";

        string upper = hwId.ToUpperInvariant();
        if (upper.Contains("DEV_0892")) return "Realtek ALC892 (7.1+2 Channel HD Audio Codec)";
        if (upper.Contains("DEV_0887")) return "Realtek ALC887 (7.1+2 Channel HD Audio Codec)";
        if (upper.Contains("DEV_0888")) return "Realtek ALC888 (7.1+2 Channel HD Audio Codec)";
        if (upper.Contains("DEV_0897")) return "Realtek ALC897 (7.1 Channel HD Audio Codec)";
        if (upper.Contains("DEV_0899")) return "Realtek ALC898/899 (High-SNR 7.1 HD Audio Codec)";
        if (upper.Contains("DEV_0900")) return "Realtek ALC1150 (115dB SNR 7.1 HD Audio Codec)";
        if (upper.Contains("DEV_0B00") || upper.Contains("DEV_1200")) return "Realtek ALC1200 (7.1 HD Audio Codec)";
        if (upper.Contains("DEV_1220")) return "Realtek ALC1220 (120dB SNR 7.1 HD Audio Codec)";
        if (upper.Contains("DEV_4080")) return "Realtek ALC4080 (USB/HDA Flagship Audio Codec)";
        return $"Realtek HD Audio Codec ({hwId})";
    }

    private static string DecodeMotherboardVendor(string hwId)
    {
        if (string.IsNullOrWhiteSpace(hwId))
            return "Unknown OEM";

        string upper = hwId.ToUpperInvariant();
        int idx = upper.IndexOf("SUBSYS_", StringComparison.Ordinal);
        if (idx < 0 || idx + 15 > upper.Length)
            return "Standard OEM";

        string subsys = upper.Substring(idx + 7, 8);
        string vendorHex = subsys.Substring(0, 4);
        string boardHex = subsys.Substring(4, 4);
        string vendorName = vendorHex switch
        {
            "1462" => "MSI (Micro-Star International)",
            "1043" => "ASUS (ASUSTeK Computer)",
            "1458" => "Gigabyte Technology",
            "1849" => "ASRock Incorporation",
            "10EC" => "Realtek Reference Board",
            "1028" => "Dell Inc.",
            "103C" => "HP (Hewlett-Packard)",
            "17AA" => "Lenovo",
            "1025" => "Acer Inc.",
            _ => $"Vendor 0x{vendorHex}"
        };

        return $"{vendorName} (Board ID: 0x{boardHex})";
    }
}
