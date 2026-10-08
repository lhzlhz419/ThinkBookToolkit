using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class AuditFixTests
{
    internal static int Helper(string mode, string marker)
    {
        if (mode == "hang")
        {
            File.WriteAllText(marker, Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite);
        }
        // A sequential stdout-then-stderr reader deadlocks here.
        Console.Error.Write(new string('e', 256 * 1024));
        Console.Out.Write(new string('o', 256 * 1024));
        return 0;
    }

    internal static async Task ProcessesAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "process-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ProcessStartInfo Start(string mode)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!);
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--audit-helper"); start.ArgumentList.Add(mode); start.ArgumentList.Add(Path.Combine(root, "pid.txt"));
            return start;
        }
        var result = await BoundedProcess.RunAsync(Start("streams"), TimeSpan.FromSeconds(10));
        Check(result.ExitCode == 0 && result.Output.Length == 256 * 1024 && result.Error.Length == 256 * 1024,
            "Concurrent process stream capture failed.");
        var watch = Stopwatch.StartNew();
        try { await BoundedProcess.RunAsync(Start("hang"), TimeSpan.FromSeconds(2)); throw new Exception("Hung helper was accepted."); }
        catch (TimeoutException) { }
        Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Execution timeout did not cover output reading.");
        if (File.Exists(Path.Combine(root, "pid.txt")))
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt")));
            try { using var child = Process.GetProcessById(pid); Check(child.WaitForExit(2000), "Timed-out helper was left alive."); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("Bounded process tests passed (dual full pipes, hung child and cleanup).");
    }

    internal static void Run()
    {
        DriversAsync().GetAwaiter().GetResult();
        CompressionAsync().GetAwaiter().GetResult();
        PowerAcceptance();
        PluginIntegrationCoverage();
        Console.WriteLine("Driver queue, background compression, power clamp acceptance and integration removal tests passed.");
    }

    private static async Task DriversAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new DriverInstallationCoordinator(async updates =>
        {
            Interlocked.Increment(ref calls);
            if (updates.Single().PackageId == "A") await release.Task.ConfigureAwait(false);
            return new("Success", false, []);
        });
        DriverUpdateItem Item(string id) => new(id, id, "1", "0", "Driver", "", "0", 1, "2026-01-01");
        var first = service.InstallAsync([Item("A")]);
        try { await service.InstallAsync([Item("a")]); throw new Exception("Duplicate package was queued."); }
        catch (InvalidOperationException) { }
        var second = service.InstallAsync([Item("B")]);
        Check(calls == 1 && service.IsQueued("A") && service.IsQueued("B"), "Installation is not serialized application-wide.");
        release.SetResult();
        await Task.WhenAll(first, second);
        Check(calls == 2 && !service.IsBusy && service.LastResult?.Status == "Success", "Queue state did not finish cleanly.");
        var failure = new DriverInstallationCoordinator(_ => throw new IOException("fixture"));
        try { await failure.InstallAsync([Item("A")]); } catch (IOException) { }
        Check(!failure.IsBusy && failure.LastResult?.FailedPackageIds.Contains("A") == true,
            "Failed installation stranded its reservation or lost its failure status.");
    }

    private static async Task CompressionAsync()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var count = 0;
        var queue = new RecordingCompressionQueue(path =>
        {
            started.Set(); release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Increment(ref count);
            if (path == "failure") throw new IOException("fixture");
            return path + ".gz";
        });
        var watch = Stopwatch.StartNew();
        queue.Enqueue("failure"); queue.Enqueue("next");
        Check(watch.Elapsed < TimeSpan.FromSeconds(1) && started.Wait(2000) && count == 0,
            "Queueing compression blocked the caller.");
        release.Set();
        await queue.Pending;
        Check(count == 2, "Failed compression prevented subsequent work.");
        var directory = Path.Combine(Environment.CurrentDirectory, ".tmp", "compression-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "sensors-20200101-000000-001-" + new string('a', 32) + ".jsonl");
        File.WriteAllText(source, "[2,0]\n[[1,42]]\n");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-90));
        var real = new RecordingCompressionQueue();
        using (var activeWriter = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            real.Enqueue(source); await real.Pending;
            Check(File.Exists(source) && !File.Exists(source + ".gz"), "An active recording was compressed/deleted.");
        }
        real.Enqueue(source); await real.Pending;
        Check(!File.Exists(source) && File.Exists(source + ".gz"), "Background archive was not committed.");
        Check(FileRetentionPolicy.Cleanup(directory, 7, true) == 1, "New unique recording names bypass retention.");
    }

    private static void PowerAcceptance()
    {
        var previous = PowerSettingsController.CurrentProfile;
        PowerSettingsController.SetProfileForTesting(PowerSettingsController.ResolveProfile("ThinkBook 16p G6 IAX"));
        try
        {
            var target = new PowerSettingsState(80, 120, 95, 56, 20, 80, 85, 10, 25);
            var locks = new PowerSettingsLockSelection { CpuPl1 = true };
            var profile = new PowerModeLockSettings { Target = target, Locks = locks };
            var actual = target with { CpuPl1 = 65 };
            Check(PowerLockAcceptancePolicy.Accept(profile, actual, locks), "Valid clamp was not accepted.");
            var effective = PowerLockAcceptancePolicy.Effective(profile)!;
            Check(profile.Target!.CpuPl1 == 80 && effective.CpuPl1 == 65 &&
                !PowerSettingsController.RequiresLockReapply(actual, effective, locks), "Accepted clamp is still being fought or desired value was lost.");
            Check(PowerSettingsController.RequiresLockReapply(actual with { CpuPl1 = 60 }, effective, locks), "Accepted value is not maintained.");
            var missing = actual with { AvailableSettings = PowerSettingAvailability.None };
            Check(!PowerLockAcceptancePolicy.Accept(profile, missing, locks) && profile.AcceptedTarget!.CpuPl1 == 65,
                "Unavailable hardware values replaced the accepted target.");
            var settings = new AppSettings();
            settings.PowerSettingsLocksByMode[ItsMode.Intelligent.ToString()] = profile;
            settings.NvApiPowerSettingsLocksByMode[ItsMode.Performance.ToString()] = new() { Target = target, AcceptedTarget = actual, Locks = locks };
            var normalized = CurveProfileStore.NormalizePowerModeLocks(settings.PowerSettingsLocksByMode);
            Check(normalized[ItsMode.Intelligent.ToString()].AcceptedTarget?.CpuPl1 == 65, "Accepted clamp was lost in persistence normalization.");
            PowerLockAcceptancePolicy.Clear(settings);
            Check(settings.PowerSettingsLocksByMode.Values.Concat(settings.NvApiPowerSettingsLocksByMode.Values).All(p => p.AcceptedTarget is null) &&
                PowerLockAcceptancePolicy.Effective(profile)!.CpuPl1 == 80, "Invalidation did not restore original targets across all Beta profiles.");
            settings.UseIntelMmioCpuPower = true;
            profile.AcceptedTarget = actual;
            var gpuTarget = NvPcfPowerPolicy.FromLegacy(target);
            settings.NvApiPowerSettingsLocksByMode[ItsMode.Performance.ToString()] = new()
            {
                Target = gpuTarget, AcceptedTarget = gpuTarget with { NvPcfAcDefaultGpuLimit = 65 },
                Locks = new() { NvPcfAcDefaultGpuLimit = true }
            };
            CurveProfileStore.SaveSettings(settings);
            var loaded = CurveProfileStore.LoadSettings();
            Check(loaded.PowerSettingsLocksByMode[ItsMode.Intelligent.ToString()].AcceptedTarget?.CpuPl1 == 65,
                "Accepted firmware value did not survive settings reload.");
            using var runtime = new ToolkitRuntimeService(loaded, persistSystemSessionState: false);
            Check(runtime.SetBetaCpuPowerEnabled(true, false) is null &&
                runtime.Settings.PowerSettingsLocksByMode.Values.Concat(runtime.Settings.NvApiPowerSettingsLocksByMode.Values).All(p => p.AcceptedTarget is null),
                "A CPU Beta change did not clear unrelated GPU acceptance too.");
        }
        finally { PowerSettingsController.SetProfileForTesting(previous); }
    }

    private static void PluginIntegrationCoverage()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"ShareDataWithOtherSoftware\":true,\"SoftwareIntegrationMode\":2,\"DataSharingPort\":2975}")!;
        Check(!JsonSerializer.Serialize(settings).Contains("DataSharingPort"), "Removed integration settings are still persisted.");
        using var runtime = new ToolkitRuntimeService(settings, persistSystemSessionState: false);
        var context = PluginHostBridge.Context(runtime, ["sensors.read", "data.read", "host.control"]);
        Check(new[] { "SetItsModeAsync", "SetFanModeAsync", "SetFullSpeedAsync" }.All(id => context.Operations.Any(o => o.Id == id)) &&
            context.Sensors.ContainsKey("toolkit.sensor.cpuTemperatureC") && context.Data.TryGetProperty("Snapshot", out _),
            "Plugin APIs cannot replace the removed integration operations/data.");
        Check(context.Operations.All(o => o.Id is not ("TrySetDataSharing" or "TrySetSoftwareIntegration")), "Removed HTTP service remains callable via host.control.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
