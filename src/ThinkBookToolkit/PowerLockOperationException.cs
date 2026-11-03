using System;
using System.Linq;

namespace ThinkBookToolkit;

internal enum PowerLockStage { Read, Write, Readback, Save }

internal sealed class PowerLockOperationException : Exception
{
    internal string Backend { get; }
    internal PowerLockStage Stage { get; }
    internal PowerSettingsLockSelection Affected { get; }
    internal PowerSetting? Field { get; }
    internal BetaCpuPowerKind? CpuKind { get; }

    internal PowerLockOperationException(string backend, PowerLockStage stage, PowerSettingsLockSelection affected,
        Exception inner, PowerSetting? field = null, BetaCpuPowerKind? cpuKind = null) : base(inner.Message, inner)
    { Backend = backend; Stage = stage; Affected = affected with { }; Field = field; CpuKind = cpuKind; }

    internal static void Run(Action operation, string backend, PowerSettingsLockSelection affected,
        PowerSetting? field = null, BetaCpuPowerKind? cpuKind = null)
    {
        try { operation(); }
        catch (PowerLockOperationException) { throw; }
        catch (Exception ex) { throw new PowerLockOperationException(backend, PowerLockStage.Write, affected, ex, field, cpuKind); }
    }

    internal static PowerSettingsLockSelection Select(PowerSettingsLockSelection selection, params PowerSetting[] fields)
    {
        var result = new PowerSettingsLockSelection();
        foreach (var field in fields) if (selection.IsLocked(field)) result = result.With(field, true);
        return result;
    }

    internal static string Names(PowerSettingsLockSelection selection, bool chinese, BetaCpuPowerKind? cpuKind = null) =>
        string.Join(chinese ? "、" : ", ", Enum.GetValues<PowerSetting>().Where(selection.IsLocked)
            .Select(field => PowerLockAcceptancePolicy.Label(field, chinese, cpuKind)));

    internal static string Describe(Exception error, PowerSettingsLockSelection affected, PowerLockStage stage,
        BetaCpuPowerKind? cpuKind, bool chinese)
    {
        if (error is AggregateException aggregate)
            return string.Join("；", aggregate.InnerExceptions.Select(e => Describe(e, affected, stage, cpuKind, chinese)));
        var detail = error as PowerLockOperationException ?? new PowerLockOperationException(
            stage == PowerLockStage.Save ? chinese ? "配置文件" : "Settings file" : chinese ? "功耗接口" : "Power interface",
            stage, affected, error, cpuKind: cpuKind);
        string Name(PowerSetting field) => PowerLockAcceptancePolicy.Label(field, chinese, detail.CpuKind);
        var names = Names(detail.Affected, chinese, detail.CpuKind);
        if (names.Length == 0) names = chinese ? "当前功耗锁定项" : "current power locks";
        var action = detail.Stage switch
        {
            PowerLockStage.Read => chinese ? "读取失败" : "read failed",
            PowerLockStage.Readback => chinese ? "写入后读回失败" : "post-write readback failed",
            PowerLockStage.Save => chinese ? "配置保存失败" : "settings save failed",
            _ => chinese ? "写入失败" : "write failed"
        };
        var fieldName = detail.Field is { } field ? Name(field) + " " : "";
        var ambiguity = detail.Field is null && detail.Stage != PowerLockStage.Save
            ? chinese ? "；接口未指明具体失败参数" : "; the interface did not identify a specific failed parameter" : "";
        return chinese
            ? $"{fieldName}{action}（{detail.Backend}；涉及锁定项：{names}{ambiguity}）：{detail.InnerException!.Message}"
            : $"{fieldName}{action} ({detail.Backend}; affected locks: {names}{ambiguity}): {detail.InnerException!.Message}";
    }
}
