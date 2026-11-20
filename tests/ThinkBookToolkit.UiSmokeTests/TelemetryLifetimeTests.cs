using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class TelemetryLifetimeTests
{
    internal static void Run()
    {
        int polls = 0, starts = 0, stopped = 0;
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new FpsSensorController(foregroundPid: () => 100 + Interlocked.Increment(ref polls) % 2,
            captureSession: async token =>
            {
                Interlocked.Increment(ref starts);
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { await allowCleanup.Task; Interlocked.Increment(ref stopped); }
            });
        monitor.Start();
        try
        {
            Wait(() => Volatile.Read(ref polls) >= 4);
            if (Volatile.Read(ref starts) != 1) throw new Exception("Switching foreground processes restarted the FPS trace.");
            monitor.Stop();
            monitor.Start();
            if (Volatile.Read(ref starts) != 1) throw new Exception("A second trace overlapped pending cleanup.");
        }
        finally { allowCleanup.TrySetResult(); }
        Wait(() => Volatile.Read(ref starts) == 2 && Volatile.Read(ref stopped) == 1);
        monitor.Stop();
        Wait(() => Volatile.Read(ref stopped) == 2);

        var decoder = new LowMemoryVideoPlayer();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(LowMemoryVideoPlayer).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(decoder, blocked.Task);
        var timer = Stopwatch.StartNew();
        try { decoder.Dispose(); }
        finally { blocked.TrySetResult(); }
        if (timer.Elapsed > TimeSpan.FromSeconds(1)) throw new Exception("Window teardown waited for a blocked decoder.");
        Console.WriteLine("FPS target switching, serialized trace lifetime and nonblocking video teardown passed.");
    }

    private static void Wait(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, 5000)) throw new TimeoutException("Telemetry lifecycle fixture timed out.");
    }
}
