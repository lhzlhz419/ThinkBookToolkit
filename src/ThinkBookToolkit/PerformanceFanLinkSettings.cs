using System;
using System.Collections.Generic;

namespace ThinkBookToolkit;

internal static class PerformanceFanLinkDefaults
{
    public static readonly ItsMode[] SupportedModes =
    [
        ItsMode.Intelligent,
        ItsMode.PowerSaving,
        ItsMode.Performance,
        ItsMode.Geek
    ];

    public static Dictionary<string, FanStrategySelection>
        CreateFanStrategies()
    {
        var result = new Dictionary<string, FanStrategySelection>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mode in SupportedModes)
        {
            result[mode.ToString()] = new FanStrategySelection();
        }
        return result;
    }

    public static Dictionary<string, bool> CreateNoSwitchModes()
    {
        var result = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mode in SupportedModes)
            result[mode.ToString()] = false;
        return result;
    }

    public static PerformanceFanLinkSettings Normalize(
        PerformanceFanLinkSettings? value)
    {
        var normalized = new PerformanceFanLinkSettings
        {
            SwitchFanStrategyWithPerformanceMode =
                value?.SwitchFanStrategyWithPerformanceMode == true,
            FanControlTargetMode = IsSupported(value?.FanControlTargetMode)
                ? value!.FanControlTargetMode
                : ItsMode.Unknown,
            FanStrategiesByMode = CreateFanStrategies(),
            NoSwitchModes = CreateNoSwitchModes()
        };

        foreach (var mode in SupportedModes)
        {
            var key = mode.ToString();
            if (value?.FanStrategiesByMode?.TryGetValue(
                    key,
                    out var source) == true &&
                source is not null)
            {
                normalized.FanStrategiesByMode[key] =
                    NormalizeSelection(source);
            }
            if (value?.NoSwitchModes?.TryGetValue(
                    key,
                    out var noSwitch) == true)
            {
                normalized.NoSwitchModes[key] = noSwitch;
            }
        }

        if (normalized.FanControlTargetMode != ItsMode.Unknown)
        {
            normalized.NoSwitchModes[
                normalized.FanControlTargetMode.ToString()] = true;
        }
        return normalized;
    }

    public static PerformanceFanLinkSettings Clone(
        PerformanceFanLinkSettings value) => Normalize(value);

    public static FanStrategySelection SelectionFor(
        PerformanceFanLinkSettings? settings,
        ItsMode mode)
    {
        var normalized = Normalize(settings);
        return normalized.FanStrategiesByMode.TryGetValue(
                mode.ToString(),
                out var selection)
            ? selection
            : new FanStrategySelection();
    }

    public static bool IsNoSwitchMode(
        PerformanceFanLinkSettings? settings,
        ItsMode mode)
    {
        var normalized = Normalize(settings);
        return normalized.NoSwitchModes.TryGetValue(
                   mode.ToString(),
                   out var value) &&
               value;
    }

    private static FanStrategySelection NormalizeSelection(
        FanStrategySelection value) => new()
    {
        Mode = Enum.IsDefined(value.Mode)
            ? value.Mode
            : FanControlMode.FirmwareAutomatic,
        ProfileIndex = Math.Clamp(value.ProfileIndex, 0, 4)
    };

    private static bool IsSupported(ItsMode? mode) =>
        mode.HasValue && Array.IndexOf(SupportedModes, mode.Value) >= 0;
}
