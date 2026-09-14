using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class ItsModeTransitionTests
{
    internal static void Run()
    {
        var legacy = ItsModeDetector.ResolveControlPath(8191, true);
        var modernReads = 0;
        foreach (var target in new[] { ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Geek })
        {
            Check(ItsModeDetector.ReadModeUsingPath(legacy,
                    () => { modernReads++; return ItsMode.Intelligent; }, () => target) == target,
                "Legacy writes are still being read through the stale modern interface.");
        }
        Check(modernReads == 0 &&
              ItsModeDetector.ReadModeUsingPath(legacy, () => ItsMode.Intelligent, () => ItsMode.Unknown) == ItsMode.Unknown,
            "Missing legacy state must not fall back to a stale modern state.");
        Check(ItsModeDetector.ResolveControlPath(8192, true, ItsModeControlPath.LegacyLitssvc) == legacy &&
              ItsModeDetector.ReadModeUsingPath(ItsModeControlPath.ModernDispatcher,
                  () => ItsMode.Performance, () => throw new Exception("Wrong interface")) == ItsMode.Performance,
            "Successful legacy fallback or the valid modern path was not retained.");

        foreach (var source in new[] { ItsMode.Intelligent, ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Geek })
        {
            var operations = new List<string>();
            var overlay = false;
            var pendingPerformanceWrite = false;
            var current = source;
            ItsModeController.ExecuteLegacyTransition(ItsMode.Geek, () => current,
                command => { operations.Add($"service:{command:X}"); pendingPerformanceWrite = true; },
                enabled => { operations.Add("overlay:" + enabled); overlay = enabled; },
                () =>
                {
                    operations.Add("wait");
                    Check(pendingPerformanceWrite, "Wait was requested without a base-mode write.");
                    current = ItsMode.Performance;
                    pendingPerformanceWrite = false;
                    overlay = false; // A delayed LITSSVC write clears the overlay.
                });
            if (pendingPerformanceWrite) overlay = false;
            Check(overlay, "A delayed performance write overwrote the Geek overlay.");
            Check(source is ItsMode.Performance or ItsMode.Geek
                ? operations.SequenceEqual(new[] { "overlay:True" })
                : operations.SequenceEqual(new[] { "service:86", "service:94", "wait", "overlay:True" }),
                "Legacy Geek transition ordering is incorrect.");
        }
        var sequence = new Queue<ItsMode>([ItsMode.PowerSaving, ItsMode.Performance,
            ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Performance]);
        var delays = 0;
        ItsModeController.WaitForLegacyPerformance(() => sequence.Dequeue(), _ => delays++);
        Check(delays == 4, "Base-mode settling did not require consecutive confirmations.");
        var overlayApplied = false;
        try
        {
            ItsModeController.ExecuteLegacyTransition(ItsMode.Geek, () => ItsMode.Intelligent, _ => { },
                _ => overlayApplied = true,
                () => ItsModeController.WaitForLegacyPerformance(() => ItsMode.Intelligent, _ => { }));
            throw new Exception("Unconfirmed base transition was accepted.");
        }
        catch (TimeoutException) { }
        Check(!overlayApplied, "Geek overlay was enabled before the base mode was confirmed.");
        var normal = new List<string>();
        ItsModeController.ExecuteLegacyTransition(ItsMode.Performance, () => ItsMode.Performance,
            command => normal.Add($"service:{command:X}"), value => normal.Add("overlay:" + value), () => { });
        Check(normal.SequenceEqual(new[] { "overlay:False", "service:86", "service:94" }),
            "Exiting Geek mode did not clear the overlay before applying Performance.");

        using var runtime = new ToolkitRuntimeService(new AppSettings());
        var type = typeof(ToolkitRuntimeService);
        type.GetField("_lastConfirmedItsMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, ItsMode.Geek);
        type.GetField("_itsModeReadGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, 2);
        var resolve = type.GetMethod("ResolveItsModeForRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.Intelligent, 1])! == ItsMode.Geek,
            "An in-flight stale refresh overwrote the newly confirmed mode.");
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.PowerSaving, 2])! == ItsMode.PowerSaving,
            "Normal subsequent observations of external mode changes were suppressed.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with { ItsMode = ItsMode.Intelligent });
        type.GetField("_switchingPerformanceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, true);
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.Performance, 2])! == ItsMode.Intelligent,
            "The intermediate Performance base mode leaked into mode/fan-link state during a Geek transition.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
