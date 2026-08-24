using System;
using System.Collections.Generic;

namespace ThinkBookToolkit;

public sealed class GpuOverclockSettings
{
    public bool Enabled { get; set; }
    public bool CoreFrequencyOffsetEnabled { get; set; } = true;
    public bool MemoryFrequencyOffsetEnabled { get; set; } = true;
    public bool CoreFrequencyLimitEnabled { get; set; } = true;
    public bool MemoryFrequencyLimitEnabled { get; set; } = true;
    public int CoreFrequencyOffsetMhz { get; set; }
    public int MemoryFrequencyOffsetMhz { get; set; }
    public int? MinimumCoreFrequencyMhz { get; set; }
    public int? MaximumCoreFrequencyMhz { get; set; }
    public uint? MinimumMemoryFrequencyMhz { get; set; }
    public uint? MaximumMemoryFrequencyMhz { get; set; }
}

internal static class GpuOverclockPolicy
{
    public const int MinimumCoreOffsetMhz = -500;
    public const int MaximumCoreOffsetMhz = 500;
    public const int MinimumMemoryOffsetMhz = -1000;
    public const int MaximumMemoryOffsetMhz = 3000;
    public const int MinimumLockedCoreFrequencyMhz = 0;
    public const int MaximumLockedCoreFrequencyMhz = 3500;

    public static GpuOverclockSettings Normalize(
        GpuOverclockSettings? value)
    {
        value ??= new GpuOverclockSettings();
        var minimum = value.MinimumCoreFrequencyMhz;
        var maximum = value.MaximumCoreFrequencyMhz;
        if (!IsValidClockLimit(minimum, maximum))
        {
            minimum = null;
            maximum = null;
        }
        var minimumMemory = value.MinimumMemoryFrequencyMhz;
        var maximumMemory = value.MaximumMemoryFrequencyMhz;
        if (!IsValidMemoryClockLimit(minimumMemory, maximumMemory))
        {
            minimumMemory = null;
            maximumMemory = null;
        }

        return new GpuOverclockSettings
        {
            Enabled = value.Enabled,
            CoreFrequencyOffsetEnabled = value.CoreFrequencyOffsetEnabled,
            MemoryFrequencyOffsetEnabled = value.MemoryFrequencyOffsetEnabled,
            CoreFrequencyLimitEnabled = value.CoreFrequencyLimitEnabled,
            MemoryFrequencyLimitEnabled = value.MemoryFrequencyLimitEnabled,
            CoreFrequencyOffsetMhz = Math.Clamp(
                value.CoreFrequencyOffsetMhz,
                MinimumCoreOffsetMhz,
                MaximumCoreOffsetMhz),
            MemoryFrequencyOffsetMhz = Math.Clamp(
                value.MemoryFrequencyOffsetMhz,
                MinimumMemoryOffsetMhz,
                MaximumMemoryOffsetMhz),
            MinimumCoreFrequencyMhz = minimum,
            MaximumCoreFrequencyMhz = maximum,
            MinimumMemoryFrequencyMhz = minimumMemory,
            MaximumMemoryFrequencyMhz = maximumMemory
        };
    }

    public static bool TryValidate(
        GpuOverclockSettings? value,
        out string error)
    {
        if (value is null)
        {
            error = "GPU overclock settings are missing.";
            return false;
        }
        if (value.CoreFrequencyOffsetMhz is < MinimumCoreOffsetMhz or
            > MaximumCoreOffsetMhz)
        {
            error = $"Core offset must be between {MinimumCoreOffsetMhz} and +{MaximumCoreOffsetMhz} MHz.";
            return false;
        }
        if (value.MemoryFrequencyOffsetMhz is < MinimumMemoryOffsetMhz or
            > MaximumMemoryOffsetMhz)
        {
            error = $"Memory offset must be between {MinimumMemoryOffsetMhz} and +{MaximumMemoryOffsetMhz} MHz.";
            return false;
        }
        if (!IsValidClockLimit(
                value.MinimumCoreFrequencyMhz,
                value.MaximumCoreFrequencyMhz))
        {
            error = "Core clock limits must both be blank, or both be between 0 and 3500 MHz with the maximum greater than or equal to the minimum.";
            return false;
        }
        if (!IsValidMemoryClockLimit(
                value.MinimumMemoryFrequencyMhz,
                value.MaximumMemoryFrequencyMhz))
        {
            error = "Memory clock limits must both be blank, or both be positive integers with the maximum greater than or equal to the minimum.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsDefault(GpuOverclockSettings value) =>
        value.CoreFrequencyOffsetEnabled &&
        value.MemoryFrequencyOffsetEnabled &&
        value.CoreFrequencyLimitEnabled &&
        value.MemoryFrequencyLimitEnabled &&
        value.CoreFrequencyOffsetMhz == 0 &&
        value.MemoryFrequencyOffsetMhz == 0 &&
        value.MinimumCoreFrequencyMhz is null &&
        value.MaximumCoreFrequencyMhz is null &&
        value.MinimumMemoryFrequencyMhz is null &&
        value.MaximumMemoryFrequencyMhz is null;

    public static string Signature(GpuOverclockSettings value) =>
        string.Join(
            ":",
            value.CoreFrequencyOffsetEnabled,
            value.MemoryFrequencyOffsetEnabled,
            value.CoreFrequencyLimitEnabled,
            value.MemoryFrequencyLimitEnabled,
            value.CoreFrequencyOffsetMhz,
            value.MemoryFrequencyOffsetMhz,
            value.MinimumCoreFrequencyMhz?.ToString() ?? "",
            value.MaximumCoreFrequencyMhz?.ToString() ?? "",
            value.MinimumMemoryFrequencyMhz?.ToString() ?? "",
            value.MaximumMemoryFrequencyMhz?.ToString() ?? "");

    private static bool IsValidClockLimit(int? minimum, int? maximum)
    {
        if (!minimum.HasValue && !maximum.HasValue)
            return true;
        return minimum is >= MinimumLockedCoreFrequencyMhz and
                   <= MaximumLockedCoreFrequencyMhz &&
               maximum is >= MinimumLockedCoreFrequencyMhz and
                   <= MaximumLockedCoreFrequencyMhz &&
               maximum.Value >= minimum.Value;
    }

    private static bool IsValidMemoryClockLimit(
        uint? minimum,
        uint? maximum)
    {
        if (!minimum.HasValue && !maximum.HasValue)
            return true;
        return minimum is > 0 && maximum is > 0 &&
               maximum.Value >= minimum.Value;
    }
}

internal static class DiscreteGpuStatusFormatter
{
    public static string Format(
        DiscreteGpuActivityState state,
        string? performanceState,
        bool isChinese)
    {
        var text = state switch
        {
            DiscreteGpuActivityState.Active =>
                isChinese ? "活跃" : "Active",
            DiscreteGpuActivityState.Inactive =>
                isChinese ? "不活跃" : "Inactive",
            DiscreteGpuActivityState.Off => isChinese ? "关闭" : "Off",
            _ => "--"
        };
        return (state is DiscreteGpuActivityState.Active or
                   DiscreteGpuActivityState.Inactive) &&
               !string.IsNullOrWhiteSpace(performanceState)
            ? text + " · " + performanceState
            : text;
    }
}

internal sealed record DiscreteGpuApplication(
    int ProcessId,
    string Name,
    string ExecutablePath);

internal sealed record GpuWorkerCommandResponse(
    bool Success,
    string Error,
    IReadOnlyList<DiscreteGpuApplication> Applications,
    int AffectedProcesses = 0)
{
    public static GpuWorkerCommandResponse Failure(string error) =>
        new(false, error, [], 0);

    public static GpuWorkerCommandResponse Ok(
        IReadOnlyList<DiscreteGpuApplication>? applications = null,
        int affectedProcesses = 0) =>
        new(true, string.Empty, applications ?? [], affectedProcesses);
}
