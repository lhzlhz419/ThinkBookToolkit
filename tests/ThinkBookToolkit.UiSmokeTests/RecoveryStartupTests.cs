using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit;
using ThinkBookToolkit.Guardian;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class RecoveryStartupTests
{
    internal static int Helper(string mode, string pidFile)
    {
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());
        if (mode == "hang") Thread.Sleep(Timeout.Infinite);
        return mode == "fail" ? 7 : 0;
    }

    internal static void VerifyPointerStartup()
    {
        var productionConfig = Path.Combine(Environment.CurrentDirectory,
            "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.runtimeconfig.json");
        using var config = JsonDocument.Parse(File.ReadAllText(productionConfig));
        const string pointerSwitch = "Switch.System.Windows.Input.Stylus.EnablePointerSupport";
        Check(config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").GetProperty(pointerSwitch).GetBoolean(),
            "The production runtime configuration did not enable pointer input before WPF startup.");
        Check(AppContext.TryGetSwitch(pointerSwitch, out var enabled) && enabled, "Test host did not use production input configuration.");
        var type = typeof(System.Windows.Input.InputManager).Assembly.GetType("System.Windows.Input.StylusLogic", throwOnError: true)!;
        var pointer = (bool)type.GetProperty("IsPointerStackEnabled", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Check(pointer, "WPF selected the legacy PenIMC stack.");
        var window = new Window { Content = new StackPanel { Children = { new TextBox { Text = "Input smoke" }, new Button { Content = "Button" } } },
            Width = 320, Height = 160, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var logic = type.GetProperty("CurrentStylusLogic", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Check(logic.GetType().Name == "PointerLogic", "A real HWND did not use pointer input.");
            using var process = Process.GetCurrentProcess();
            Check(!process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("PenImc_cor3.dll", StringComparison.OrdinalIgnoreCase)),
                "Opening a Toolkit-style WPF window loaded the legacy PenIMC component.");
        }
        finally { window.Close(); }
        Console.WriteLine("WPF pointer startup and window input-stack checks passed.");
    }

    internal static async Task VerifyRecoveryWorkerAsync()
    {
        using var current = Process.GetCurrentProcess();
        var marker = new FanWatchdogMarker(current.Id, current.StartTime.ToUniversalTime().Ticks, "", "test");
        Check(FanWatchdogService.IsMonitoredProcessAlive(marker), "Live parent was not protected from recovery.");
        Check(!FanWatchdogService.IsMonitoredProcessAlive(marker with { ProcessStartUtcTicks = 1 }), "Reused PID was mistaken for the old parent.");
        var command = FanWatchdogService.CreateRecoveryStart("123-456.armed.json");
        Check(command.FileName == Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.Guardian.exe") &&
            command.ArgumentList.SequenceEqual(["--restore-fans", "123-456.armed.json"]) &&
            !command.UseShellExecute && command.CreateNoWindow, "Guardian recovery command is not pinned to its installed executable.");
        try { FanWatchdogService.CreateRecoveryStart("../outside.armed.json"); throw new Exception("Escaping marker path accepted."); }
        catch (ArgumentException) { }

        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "recovery-startup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ProcessStartInfo Start(string mode)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.UiSmokeTests.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--recovery-test-helper");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(Path.Combine(root, mode + ".pid"));
            return start;
        }
        await FanWatchdogService.RunRecoveryProcessAsync(Start("success"), TimeSpan.FromSeconds(5), CancellationToken.None);
        try
        {
            await FanWatchdogService.RunRecoveryProcessAsync(Start("fail"), TimeSpan.FromSeconds(5), CancellationToken.None);
            throw new Exception("A failed recovery worker was reported as success.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("code 7")) { }
        var timer = Stopwatch.StartNew();
        try
        {
            await FanWatchdogService.RunRecoveryProcessAsync(Start("hang"), TimeSpan.FromSeconds(2), CancellationToken.None);
            throw new Exception("Hung recovery worker did not time out.");
        }
        catch (OperationCanceledException) { }
        Check(timer.Elapsed < TimeSpan.FromSeconds(6), "Hung recovery worker blocked service shutdown indefinitely.");
        var pid = int.Parse(File.ReadAllText(Path.Combine(root, "hang.pid")));
        try
        {
            using var hung = Process.GetProcessById(pid);
            Check(hung.WaitForExit(3000), "Timed-out recovery worker was left running.");
        }
        catch (ArgumentException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await FanWatchdogService.RunRecoveryProcessAsync(Start("cancelled"), TimeSpan.FromSeconds(5), cancelled.Token);
            throw new Exception("Cancelled request launched a worker.");
        }
        catch (OperationCanceledException) { }
        Check(!File.Exists(Path.Combine(root, "cancelled.pid")), "Cancelled service request started a recovery process.");
        Console.WriteLine("Fresh guardian worker success, failure, timeout and parent-identity checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
