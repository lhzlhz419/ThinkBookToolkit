using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

internal enum ItsModeBackend
{
    None,
    LenovoProcessManagement,
    LegacyItsService
}

public sealed class ItsModeDetector
{
    private const string ModernPath = @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";
    private const string LegacyPath = @"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC\MMC";
    private const string LegacyBasePath = @"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC";
    private const int DispatcherVersion3 = 8192;
    private const int LegacyItsVersion = 0x4000;

    public bool IsModeSwitchSupported() =>
        DetectSwitchBackend() != ItsModeBackend.None;

    internal ItsModeBackend DetectSwitchBackend()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(
                   ModernPath,
                   writable: false))
        {
            if (key is not null &&
                ReadInt(key, "Version", -1) >= DispatcherVersion3)
            {
                return ItsModeBackend.LenovoProcessManagement;
            }
        }

        using var baseKey = Registry.LocalMachine.OpenSubKey(
            LegacyBasePath,
            writable: false);
        using var modeKey = Registry.LocalMachine.OpenSubKey(
            LegacyPath,
            writable: false);
        if (baseKey is not null &&
            modeKey is not null &&
            ReadInt(baseKey, "Version", -1) >= LegacyItsVersion &&
            LegacySupportedModes(ReadInt(modeKey, "Capability", 0)).Count > 1)
        {
            return ItsModeBackend.LegacyItsService;
        }

        return ItsModeBackend.None;
    }

    internal bool IsModeSupported(ItsMode mode) =>
        DetectSwitchBackend() switch
        {
            ItsModeBackend.LenovoProcessManagement =>
                PerformanceModeCycle.IsSelectableMode(mode),
            ItsModeBackend.LegacyItsService => ReadLegacySupportedModes()
                .Contains(mode),
            _ => false
        };

    public ItsMode ReadMode()
    {
        var backend = DetectSwitchBackend();
        if (backend == ItsModeBackend.LenovoProcessManagement)
            return ReadModernMode();
        if (backend == ItsModeBackend.LegacyItsService)
            return ReadLegacyMode();

        var modern = ReadModernMode();
        return modern != ItsMode.Unknown ? modern : ReadLegacyMode();
    }

    private static IReadOnlyList<ItsMode> ReadLegacySupportedModes()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            LegacyPath,
            writable: false);
        return key is null
            ? []
            : LegacySupportedModes(ReadInt(key, "Capability", 0));
    }

    internal static IReadOnlyList<ItsMode> LegacySupportedModes(int capability)
    {
        var modes = new List<ItsMode>();
        if ((capability & 1) == 0)
            modes.Add(ItsMode.Intelligent);
        if ((capability & 2) != 0)
            modes.Add(ItsMode.PowerSaving);
        if ((capability & 8) != 0)
            modes.Add(ItsMode.Performance);
        return modes;
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
