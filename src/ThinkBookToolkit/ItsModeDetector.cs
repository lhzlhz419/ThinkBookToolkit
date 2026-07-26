using Microsoft.Win32;
using System;

namespace ThinkBookToolkit;

public sealed class ItsModeDetector
{
    private const string ModernPath = @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";
    private const string LegacyPath = @"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC\MMC";
    private const string LegacyBasePath = @"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC";
    private const int DispatcherVersion3 = 8192;

    public bool IsModeSwitchSupported()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ModernPath, writable: false);
        return key is not null && ReadInt(key, "Version", -1) >= DispatcherVersion3;
    }

    public ItsMode ReadMode()
    {
        var modern = ReadModernMode();
        if (modern != ItsMode.Unknown)
            return modern;

        var legacy = ReadLegacyMode();
        return legacy;
    }

    private static ItsMode ReadModernMode()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ModernPath, writable: false);
        if (key is null)
            return ItsMode.Unknown;

        var capability = ReadInt(key, "ITS_FN_Capability", 0);
        var useVersioned = (capability & 0x10) != 0;
        var value = ReadInt(key, useVersioned ? "ITS_CurrentSettingV" : "ITS_CurrentSetting", -1);

        return value switch
        {
            0 => ItsMode.Intelligent,
            1 => ItsMode.PowerSaving,
            3 => ItsMode.Performance,
            4 => ItsMode.Geek,
            _ => ItsMode.Unknown
        };
    }

    private static ItsMode ReadLegacyMode()
    {
        using var baseKey = Registry.LocalMachine.OpenSubKey(LegacyBasePath, writable: false);
        using var key = Registry.LocalMachine.OpenSubKey(LegacyPath, writable: false);
        if (baseKey is null || key is null)
            return ItsMode.Unknown;

        var auto = ReadInt(key, "AutomaticModeSetting", -1);
        var current = ReadInt(key, "CurrentSetting", -1);

        return (auto, current) switch
        {
            (2, 0) => ItsMode.Intelligent,
            (1, 1) => ItsMode.PowerSaving,
            (1, 3) => ItsMode.Performance,
            (1, 4) => ItsMode.Geek,
            _ => ItsMode.Unknown
        };
    }

    private static int ReadInt(RegistryKey key, string name, int fallback)
    {
        var value = key.GetValue(name, fallback);
        try
        {
            return value switch
            {
                int i => i,
                long l => (int)l,
                uint u => (int)u,
                _ => Convert.ToInt32(value)
            };
        }
        catch
        {
            return fallback;
        }
    }
}
