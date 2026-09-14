using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class NvPcfIsolationTests
{
    internal static int RunFakeWorker(string pipeName) => NvPcfWorker.Run(pipeName, request =>
    {
        if (request.Operation == "CRASH") Environment.Exit(123);
        if (request.Operation == "HANG") Thread.Sleep(30000);
        if (request.Operation == "ERROR") return new(false, Error: "driver unavailable");
        if (request.Operation == "MALFORMED") return new(true);
        if (request.Operation == "WRITE" &&
            (request.State?.NvPcfAcDefaultGpuLimit != 95 || request.Selection?.NvPcfAcDefaultGpuLimit != true))
            return new(false, Error: "write request was not serialized correctly");
        return new(true, new NvPcfPowerSnapshot(150, 95, 30, 115, 80, 115,
            true, 85, 65, 87, Environment.ProcessId.ToString()));
    });

    internal static void Run()
    {
        // The ordinary desktop process must never construct a native PCF
        // controller, even when a future caller bypasses the public facade.
        try
        {
            typeof(NvPcfPowerController).GetMethod("GetController", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null);
            throw new Exception("Native NVPCF initialization was allowed in the desktop process.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }

        var executable = Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.UiSmokeTests.exe");
        using var client = new NvPcfWorkerClient(executable, "--nvpcf-test-worker",
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        var first = client.Execute(new("READ")).Snapshot!;
        Check(first.AcDefaultGpuLimitW == 95 && first.GpuTemperatureMaximumC == 87,
            "Power and thermal snapshot values were not preserved by IPC.");
        var write = client.Execute(new("WRITE", NvPcfPowerPolicy.EmptyState() with
            { NvPcfAcDefaultGpuLimit = 95 }, new() { NvPcfAcDefaultGpuLimit = true }));
        Check(write.Success, "Selective power write IPC failed.");
        Check(client.Execute(new("RESET")).Success && client.Execute(new("RESET_POWER")).Success,
            "Reset requests failed to cross the process boundary.");

        foreach (var operation in new[] { "CRASH", "ERROR", "HANG" })
        {
            var before = client.Execute(new("READ")).Snapshot!.LayoutName;
            var elapsed = Stopwatch.StartNew();
            ExpectFailure(() => client.Execute(new(operation)));
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(6), "An unresponsive worker blocked the caller indefinitely.");
            // Shutdown during a UI/backend transition must not erase backoff.
            client.Shutdown();
            ExpectFailure(() => client.Execute(new("READ")));
            Thread.Sleep(250);
            var after = client.Execute(new("READ")).Snapshot!;
            Check(after.LayoutName != before, "A failed native session was reused instead of starting a fresh process.");
            using var oldProcess = TryGetProcess(int.Parse(before));
            Check(oldProcess is null || oldProcess.HasExited, "Failed or hung worker was left running.");
        }
        client.Shutdown();
        Check(client.Execute(new("READ")).Success, "Clean shutdown prevented a later reconnect.");
        using var missing = new NvPcfWorkerClient(Path.Combine(AppContext.BaseDirectory, "missing-nvpcf-host.exe"));
        ExpectFailure(() => missing.Execute(new("READ")));
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static void ExpectFailure(Action operation)
    {
        try { operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("A driver/worker failure was not safely reported to the caller.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
