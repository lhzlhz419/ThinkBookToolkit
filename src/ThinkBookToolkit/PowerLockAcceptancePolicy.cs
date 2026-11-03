using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

internal static class PowerLockAcceptancePolicy
{
    internal sealed record Issue(PowerSetting Setting, bool Missing, int? Value);
    internal sealed record Readback(PowerSettingsLockSelection Readable, IReadOnlyList<Issue> Issues);

    // Hardware feedback is not user input: firmware may return a value outside
    // our sliders or a turbo duration that is not in the input choices.
    internal static bool IsUsableReadback(PowerSettingsState state, PowerSetting setting)
    {
        if (!state.IsAvailable(setting) || PowerSettingsController.Value(state, setting) is not { } value) return false;
        return setting switch
        {
            PowerSetting.NvPcfDynamicBoost => value is 0 or 1,
            PowerSetting.CpuTemperatureLimit or PowerSetting.GpuTemperatureLimit or PowerSetting.NvApiGpuTemperatureLimit => value is > 0 and <= 150,
            PowerSetting.GpuPowerBoost or PowerSetting.GpuToCpuDynamicBoost or PowerSetting.Atpp => value is >= 0 and < ushort.MaxValue,
            _ => value is > 0 and < ushort.MaxValue
        };
    }

    internal static Readback Inspect(PowerSettingsState state, PowerSettingsLockSelection selection)
    {
        var readable = new PowerSettingsLockSelection();
        var issues = new List<Issue>();
        foreach (var setting in Enum.GetValues<PowerSetting>().Where(selection.IsLocked))
        {
            if (IsUsableReadback(state, setting)) readable = readable.With(setting, true);
            else issues.Add(new(setting, !state.IsAvailable(setting) || PowerSettingsController.Value(state, setting) is null,
                state.IsAvailable(setting) ? PowerSettingsController.Value(state, setting) : null));
        }
        return new(readable, issues);
    }

    internal static PowerSettingsState? NormalizeAccepted(PowerSettingsState? target, PowerSettingsState? accepted, PowerSettingsLockSelection selection)
    {
        if (target is null || accepted is null) return null;
        var readable = Inspect(accepted, selection).Readable;
        return readable.Any ? PowerSettingsController.ApplyLockedValues(target, accepted, readable) : null;
    }

    internal static PowerSettingsState? Effective(PowerModeLockSettings? profile)
    {
        if (profile?.Target is not { } target) return null;
        if (NormalizeAccepted(target, profile.AcceptedTarget, profile.Locks) is not { } accepted) return target;
        return PowerSettingsController.ApplyLockedValues(target, accepted, profile.Locks);
    }

    internal static bool Accept(PowerModeLockSettings profile, PowerSettingsState confirmed, PowerSettingsLockSelection selection)
    {
        if (profile.Target is not { } target) return false;
        var selected = selection with { };
        foreach (var setting in Enum.GetValues<PowerSetting>())
            if (!profile.Locks.IsLocked(setting)) selected = selected.With(setting, false);
        var readback = Inspect(confirmed, selected);
        if (readback.Readable.Any)
            profile.AcceptedTarget = PowerSettingsController.ApplyLockedValues(
                NormalizeAccepted(target, profile.AcceptedTarget, profile.Locks) ?? target, confirmed, readback.Readable);
        return readback.Issues.Count == 0;
    }

    internal static string Describe(IReadOnlyList<Issue> issues, bool chinese, BetaCpuPowerKind? cpuKind = null) => string.Join("；", issues.Select(issue =>
        Label(issue.Setting, chinese, cpuKind) + (issue.Missing
            ? chinese ? "：未读到数值" : ": no reading"
            : chinese ? $"：读回值异常（{issue.Value}）" : $": invalid reading ({issue.Value})")));

    internal static string Label(PowerSetting setting, bool chinese, BetaCpuPowerKind? cpuKind = null)
    {
        if (setting == PowerSetting.CpuPl1) return cpuKind switch { BetaCpuPowerKind.AmdPbo => "CPU PPT", BetaCpuPowerKind.AmdApu => "CPU STAPM", _ => "CPU PL1" };
        if (setting == PowerSetting.CpuPl2) return cpuKind switch { BetaCpuPowerKind.AmdPbo => "CPU TDC", BetaCpuPowerKind.AmdApu => "CPU Fast", _ => "CPU PL2" };
        if (setting == PowerSetting.CpuTurboTimeLimit) return cpuKind switch
            { BetaCpuPowerKind.AmdPbo => "CPU EDC", BetaCpuPowerKind.AmdApu => "CPU Slow", _ => chinese ? "CPU 加速时间" : "CPU Turbo Time Limit" };
        return !chinese ? PowerSettingsController.DisplayName(setting, true) : setting switch
    {
        PowerSetting.CpuTemperatureLimit => "CPU 温度上限",
        PowerSetting.GpuTemperatureLimit or PowerSetting.NvApiGpuTemperatureLimit => "GPU 温度上限",
        PowerSetting.GpuConfigurableTgp => "GPU TGP",
        PowerSetting.GpuPowerBoost => "GPU 功耗提升",
        PowerSetting.GpuToCpuDynamicBoost => "GPU → CPU 动态功耗",
        PowerSetting.Atpp => "ATPP",
        PowerSetting.NvPcfDynamicBoost => "Dynamic Boost",
        _ => PowerSettingsController.DisplayName(setting, true)
    };
    }

    internal static void Clear(AppSettings settings)
    {
        foreach (var profile in settings.PowerSettingsLocksByMode.Values.Concat(settings.NvApiPowerSettingsLocksByMode.Values))
            profile.AcceptedTarget = null;
    }
}
