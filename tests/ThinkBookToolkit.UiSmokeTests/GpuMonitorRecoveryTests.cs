using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class GpuMonitorRecoveryTests
{
    internal static int RunWorker(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        while (reader.ReadLine() is { } command)
        {
            if (command == "EXIT") return 0;
            if (command == "CRASH") Environment.Exit(123);
            if (command == "HANG") Thread.Sleep(30000);
            writer.WriteLine(command == "MALFORMED" ? "invalid json" :
                JsonSerializer.Serialize(GpuWorkerCommandResponse.Ok(affectedProcesses: Environment.ProcessId)));
            if (command == "NO_READ") Thread.Sleep(30000);
        }
        return 0;
    }

    internal static void Run()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.UiSmokeTests.exe");
        using var client = new GpuMonitorWorkerClient(executable, "--gpu-monitor-test-worker",
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        var first = client.QueryApplications();
        Check(first.Success, "Initial query failed.");

        // Reproduce the reported loop: a dead peer, buffered data whose flush
        // fails, then EnsureWorker trying to dispose that same writer.
        using (var process = Process.GetProcessById(first.AffectedProcesses))
        {
            process.Kill();
            Check(process.WaitForExit(3000), "Fake worker did not exit.");
        }
        var writer = (StreamWriter)Field("_writer").GetValue(client)!;
        writer.AutoFlush = false;
        writer.Write("pending data");
        try { writer.Flush(); throw new Exception("Expected a broken pipe."); }
        catch (IOException) { }
        var recovered = client.QueryApplications();
        Check(recovered.Success && recovered.AffectedProcesses != first.AffectedProcesses,
            "Broken-pipe cleanup prevented automatic recovery.");

        foreach (var command in new[] { "CRASH", "HANG", "MALFORMED", "NO_READ" })
        {
            var pid = client.QueryApplications().AffectedProcesses;
            var request = command;
            if (command == "NO_READ")
            {
                Check(Execute(client, command).Success, "Could not prepare a blocked writer.");
                request = "BLOCKED_WRITE:" + new string('x', 1024 * 1024);
            }
            var elapsed = Stopwatch.StartNew();
            Check(!Execute(client, request).Success, "Worker failure was not returned as a failed result.");
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(6), "Worker failure blocked indefinitely.");
            foreach (var name in new[] { "_process", "_pipe", "_writer", "_reader", "_job" })
                Check(Field(name).GetValue(client) is null, "Cleanup left stale resource: " + name);
            Check(!client.QueryApplications().Success, "Failure backoff was ignored.");
            Thread.Sleep(250);
            var next = client.QueryApplications();
            Check(next.Success && next.AffectedProcesses != pid, "Worker did not reconnect after failure.");
            try
            {
                using var old = Process.GetProcessById(pid);
                Check(old.WaitForExit(3000), "Failed worker was left running.");
            }
            catch (ArgumentException) { }
        }

        // Restart, mode-change shutdown, and repeated disposal must also work
        // with a writer that throws from Dispose.
        foreach (var stop in new Action[] {
            GpuTelemetryControl.RestartWorkers,
            () => GpuTelemetryControl.SetMode(GpuTelemetryMode.Paused, "recovery test"),
            client.Dispose })
        {
            var actualWriter = (StreamWriter)Field("_writer").GetValue(client)!;
            actualWriter.Dispose();
            var poison = new StreamWriter(new FailingFlushStream());
            poison.Write("pending");
            Field("_writer").SetValue(client, poison);
            var elapsed = Stopwatch.StartNew();
            stop();
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(4), "Shutdown blocked on a broken writer.");
            Check(Field("_pipe").GetValue(client) is null && Field("_job").GetValue(client) is null,
                "A failing Dispose skipped pipe/job cleanup.");
            if (stop == (Action)client.Dispose) break;
            GpuTelemetryControl.SetMode(GpuTelemetryMode.Full, "recovery test");
            Check(client.QueryApplications().Success, "Explicit restart could not recover.");
            Check(client.ApplyOverclock(new GpuOverclockSettings(), force: true).Success &&
                client.ResetOverclock().Success && client.KillApplications().Success,
                "GPU operations failed after recovery.");
        }
        client.Dispose();
        Check(!client.QueryApplications().Success, "Disposed client started another worker.");
        using var missing = new GpuMonitorWorkerClient(Path.Combine(AppContext.BaseDirectory, "missing-gpu-worker.exe"));
        Check(!missing.QueryApplications().Success, "Missing executable escaped as an exception.");
    }

    private static GpuWorkerCommandResponse Execute(GpuMonitorWorkerClient client, string command) =>
        (GpuWorkerCommandResponse)typeof(GpuMonitorWorkerClient)
            .GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, new object?[] { command, TimeSpan.FromSeconds(2) })!;

    private static FieldInfo Field(string name) => typeof(GpuMonitorWorkerClient)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class FailingFlushStream : MemoryStream
    {
        public override void Flush() => throw new IOException("Pipe is broken (simulated).");
    }
}
