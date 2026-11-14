using System;
using System.Collections.Generic;
using System.Linq;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class RefreshRateCompatibilityTests
{
    internal static void Run()
    {
        var fixedMode = new DisplayRefreshRateMode(120);
        var dynamicMode = new DisplayRefreshRateMode(120, true);
        var modern = 0;
        var legacy = 0;
        void Modern() => modern++;
        void Legacy() => legacy++;
        Check(RefreshRateController.TryApplyWithFallback(fixedMode, false, Modern, Legacy, out _) &&
            modern == 0 && legacy == 1, "Windows 10 fixed refresh still calls the virtual-aware DisplayConfig wrapper.");
        Check(!RefreshRateController.TryApplyWithFallback(dynamicMode, false, Modern, Legacy, out var error) &&
            error.Contains("Windows 11") && modern == 0 && legacy == 1, "Windows 10 dynamic mode invoked a display setter.");
        Check(RefreshRateController.TryApplyWithFallback(fixedMode, true, Modern, Legacy, out _) &&
            modern == 1 && legacy == 1, "Windows 11 lost its working DisplayConfig path.");
        Check(RefreshRateController.TryApplyWithFallback(fixedMode, true,
            () => throw new InvalidOperationException("invalid virtual flags"), Legacy, out _) && legacy == 2,
            "A failed fixed-mode DisplayConfig operation bypassed compatibility fallback.");
        Check(!RefreshRateController.TryApplyWithFallback(dynamicMode, true,
            () => throw new InvalidOperationException("dynamic unsupported"), Legacy, out error) &&
            error.Contains("dynamic unsupported") && legacy == 2, "Dynamic mode silently degraded to fixed refresh.");
        Check(!RefreshRateController.TryApplyWithFallback(fixedMode, true,
            () => throw new InvalidOperationException("modern failure"),
            () => throw new InvalidOperationException("legacy failure"), out error) &&
            error.Contains("modern failure") && error.Contains("legacy failure"), "Failure diagnostics dropped one backend.");

        var flags = new List<uint>();
        var reads = 0;
        var delays = 0;
        RefreshRateController.ApplyFixedRefreshRate(120,
            flag => { flags.Add(flag); return 0; },
            () => ++reads < 3 ? 60u : 120u,
            _ => delays++);
        Check(flags.SequenceEqual([2u, 1u]) && reads == 3 && delays == 2,
            "Compatibility path did not test, apply and confirm delayed readback.");
        flags.Clear();
        Reject(() => RefreshRateController.ApplyFixedRefreshRate(120,
            flag => { flags.Add(flag); return -2; },
            () => throw new Exception("Rejected test must not read back."), _ => { }), "code -2");
        Check(flags.SequenceEqual([2u]), "Unsupported display mode was written after CDS_TEST failed.");
        flags.Clear();
        Reject(() => RefreshRateController.ApplyFixedRefreshRate(120,
            flag => { flags.Add(flag); return flag == 2 ? 0 : 1; },
            () => throw new Exception("Restart-required mode must not be reported as applied."), _ => { }), "code 1");
        Check(flags.SequenceEqual([2u, 1u]), "Restart-required mode did not preserve the error code.");
        reads = 0;
        Reject(() => RefreshRateController.ApplyFixedRefreshRate(120, _ => 0,
            () => { reads++; return 60; }, _ => { }), "60 Hz");
        Check(reads == 10, "Unconfirmed display change was accepted or retried without a bound.");
        Reject(() => RefreshRateController.VerifyFixedRefreshRate(120, () => null, _ => { }), "未知");
        Console.WriteLine("Windows 10 refresh routing, fixed-mode fallback, CDS_TEST and readback checks passed.");
    }

    private static void Reject(Action action, string detail)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(detail)) { return; }
        throw new InvalidOperationException("Expected refresh-rate failure: " + detail);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
