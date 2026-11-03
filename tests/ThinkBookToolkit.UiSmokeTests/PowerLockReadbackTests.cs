using System;
using System.Collections.Generic;
using System.Linq;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PowerLockReadbackTests
{
    internal static void Run()
    {
        var previous = PowerSettingsController.CurrentProfile;
        PowerSettingsController.SetProfileForTesting(PowerSettingsController.ResolveProfile("ThinkBook 16p G6 IAX"));
        try
        {
            var target = new PowerSettingsState(80, 120, 95, 56, 10, 80, 85, 10, 25);
            var selection = new PowerSettingsLockSelection { CpuTemperatureLimit = true, CpuPl1 = true, CpuTurboTimeLimit = true };
            var profile = new PowerModeLockSettings { Target = target, Locks = selection };
            var actual = target with { CpuTemperatureLimit = 70, CpuPl1 = 65, CpuTurboTimeLimit = 25 };
            Check(!PowerSettingsController.IsValidState(actual), "Fixture should be outside user-entry choices.");
            Check(PowerLockAcceptancePolicy.Accept(profile, actual, selection), "Legitimate non-slider feedback was rejected.");
            Check(profile.Target == target && PowerLockAcceptancePolicy.Effective(profile)!.CpuTemperatureLimit == 70,
                "Accepting firmware feedback lost the original target.");
            Check(!PowerSettingsController.RequiresLockReapply(actual, PowerLockAcceptancePolicy.Effective(profile)!, selection),
                "Accepted firmware values still trigger repeated writes.");
            Check(PowerSettingsController.IsValidLockWriteTarget(selection, actual, target) &&
                !PowerSettingsController.IsValidLockWriteTarget(selection, actual),
                "Feedback replay should work without loosening manual-input validation.");
            var normalized = CurveProfileStore.NormalizePowerModeLocks(new Dictionary<string, PowerModeLockSettings>
                { [ItsMode.Performance.ToString()] = profile });
            Check(normalized[ItsMode.Performance.ToString()].AcceptedTarget!.CpuTurboTimeLimit == 25,
                "Saving settings dropped a hardware-supported turbo duration.");
            var saved = new AppSettings { PowerSettingsLocksByMode = normalized };
            CurveProfileStore.SaveSettings(saved);
            var loaded = CurveProfileStore.LoadSettings().PowerSettingsLocksByMode[ItsMode.Performance.ToString()];
            Check(loaded.Target!.CpuTemperatureLimit == 95 && loaded.AcceptedTarget!.CpuTemperatureLimit == 70 &&
                loaded.AcceptedTarget.CpuTurboTimeLimit == 25, "Settings reload lost the original or accepted firmware values.");
            var gpuLocks = new PowerSettingsLockSelection { GpuConfigurableTgp = true };
            var gpu = new PowerModeLockSettings { Target = target, Locks = gpuLocks };
            var gpuClamp = target with { GpuConfigurableTgp = 45 };
            Check(PowerLockAcceptancePolicy.Accept(gpu, gpuClamp, gpuLocks) &&
                PowerSettingsController.IsValidLockWriteTarget(gpuLocks, PowerLockAcceptancePolicy.Effective(gpu)!, target),
                "A GPU wattage below the input slider minimum cannot be retained/reapplied.");

            var partial = actual with { CpuPl1 = 60, AvailableSettings = actual.AvailableSettings & ~PowerSettingsController.Flag(PowerSetting.CpuTemperatureLimit) };
            Check(!PowerLockAcceptancePolicy.Accept(profile, partial, selection) && profile.AcceptedTarget!.CpuPl1 == 60 &&
                profile.AcceptedTarget.CpuTemperatureLimit == 70, "A missing field discarded valid feedback or the previous accepted value.");
            var inspection = PowerLockAcceptancePolicy.Inspect(partial, selection);
            Check(inspection.Readable.CpuPl1 && !inspection.Readable.CpuTemperatureLimit && inspection.Issues.Single().Missing,
                "Missing locked field was not isolated.");
            Check(PowerLockAcceptancePolicy.Describe(inspection.Issues, true).Contains("CPU 温度上限") &&
                PowerLockAcceptancePolicy.Describe(inspection.Issues, false).Contains("no reading"), "Readback errors do not identify the field/reason.");
            foreach (var bad in new[] { -1, 0, 65535, int.MaxValue })
            {
                var invalid = actual with { CpuPl1 = bad };
                Check(!PowerLockAcceptancePolicy.Accept(profile, invalid, selection) && profile.AcceptedTarget!.CpuPl1 == 60,
                    "Invalid/sentinel reading replaced a confirmed power limit.");
                Check(!PowerSettingsController.IsValidLockWriteTarget(selection, invalid, target), "Invalid feedback can be replayed to hardware.");
            }
            var zeroBoost = target with { GpuPowerBoost = 0 };
            Check(PowerLockAcceptancePolicy.IsUsableReadback(zeroBoost, PowerSetting.GpuPowerBoost), "Zero boost is valid.");
            profile.AcceptedTarget = actual with { CpuPl1 = -1 };
            Check(PowerLockAcceptancePolicy.Effective(profile)!.CpuPl1 == target.CpuPl1 &&
                PowerLockAcceptancePolicy.Effective(profile)!.CpuTemperatureLimit == 70, "Corrupt saved feedback was not sanitized per-field.");

            var feedback = new PowerLockFeedback();
            var now = DateTimeOffset.UtcNow;
            Check(feedback.ShouldNotify("missing PL1", now) && !feedback.ShouldNotify("missing PL1", now.AddSeconds(2)), "Stable errors cause repeated notifications.");
            feedback.ShouldNotify("", now.AddSeconds(3));
            Check(!feedback.ShouldNotify("missing PL1", now.AddSeconds(4)), "Flapping readbacks cause rapid repeated notifications.");
            feedback.ShouldNotify("", now.AddSeconds(61));
            Check(feedback.ShouldNotify("missing PL1", now.AddSeconds(62)), "New failure episode is never reported.");
            foreach (var delay in new[] { 5, 10, 20, 30, 30 })
            {
                feedback.Failed(now);
                Check(!feedback.CanAttempt(now.AddSeconds(delay - 1)) && feedback.CanAttempt(now.AddSeconds(delay)), "Retry backoff is incorrect.");
                now = now.AddSeconds(delay);
            }
            feedback.ReadSucceeded(); Check(feedback.CanAttempt(now), "Successful read did not release retry backoff.");
            feedback.Failed(now); feedback.Reset();
            Check(feedback.CanAttempt(now) && feedback.ShouldNotify("missing PL1", now), "User/Beta changes did not reset failure state.");
            VerifyFailureAttribution();
        }
        finally { PowerSettingsController.SetProfileForTesting(previous); }
        Console.WriteLine("Power lock partial readback, non-slider limits, replay validation, persistence and retry/notification tests passed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void VerifyFailureAttribution()
    {
        var cpu = new PowerSettingsLockSelection { CpuPl1 = true };
        var gpu = new PowerSettingsLockSelection { NvPcfAcDefaultGpuLimit = true, NvPcfAcMaxGpuLimit = true };
        var mixed = cpu with { NvPcfAcDefaultGpuLimit = true, NvPcfAcMaxGpuLimit = true };
        Exception fault;
        try
        {
            PowerLockOperationException.Run(() => throw new InvalidOperationException("fixture write"), "Lenovo WMI", cpu, PowerSetting.CpuPl1);
            throw new Exception("The fixture must fail.");
        }
        catch (PowerLockOperationException ex) { fault = ex; }
        var text = PowerLockOperationException.Describe(fault, mixed, PowerLockStage.Read, null, true);
        Check(text.Contains("CPU PL1 写入失败") && text.Contains("Lenovo WMI") && !text.Contains("AC Default"),
            "A known WMI field failure was incorrectly attributed to all locked fields.");
        var batch = new PowerLockOperationException("NVIDIA NVAPI", PowerLockStage.Write, gpu, new TimeoutException("fixture timeout"));
        text = PowerLockOperationException.Describe(batch, mixed, PowerLockStage.Read, null, true);
        Check(text.Contains("AC Default GPU Limit") && text.Contains("AC Max GPU Limit") &&
            text.Contains("未指明具体失败参数") && !text.Contains("CPU PL1"), "Batch failure should identify affected GPU locks without inventing a culprit.");
        var amd = new PowerLockOperationException("AMD ZenStates", PowerLockStage.Write, cpu,
            new InvalidOperationException("fixture companion"), PowerSetting.CpuPl2, BetaCpuPowerKind.AmdPbo);
        text = PowerLockOperationException.Describe(amd, mixed, PowerLockStage.Read, null, true);
        Check(text.Contains("CPU TDC 写入失败") && text.Contains("涉及锁定项：CPU PPT"),
            "A companion write failure must name both the actual field and affected locks.");
        text = PowerLockOperationException.Describe(new AggregateException(fault, batch), mixed, PowerLockStage.Write, null, false);
        Check(text.Contains("CPU PL1") && text.Contains("NVIDIA NVAPI") && text.Contains("affected locks"), "Multiple backend failures were lost.");
        text = PowerLockOperationException.Describe(new InvalidOperationException("fixture read"), mixed, PowerLockStage.Readback, null, true);
        Check(text.Contains("写入后读回失败") && text.Contains("CPU PL1") && text.Contains("AC Max GPU Limit"), "Readback exceptions do not list affected locks.");
        text = PowerLockOperationException.Describe(new InvalidOperationException("fixture save"), cpu, PowerLockStage.Save, null, true);
        Check(text.Contains("配置保存失败") && text.Contains("CPU PL1") && !text.Contains("接口未指明"), "Persistence failure was reported as a hardware field failure.");
        Check(PowerLockAcceptancePolicy.Label(PowerSetting.CpuPl1, true, BetaCpuPowerKind.AmdApu) == "CPU STAPM" &&
            PowerLockAcceptancePolicy.Label(PowerSetting.CpuPl2, false, BetaCpuPowerKind.AmdPbo) == "CPU TDC",
            "Failure labels do not match the active CPU backend.");
    }
}
