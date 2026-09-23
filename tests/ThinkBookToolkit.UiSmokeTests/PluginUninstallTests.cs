using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ThinkBookToolkit;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginUninstallTests
{
    internal static async Task RunAsync(string? hostPath)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-uninstall-tests", Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(root, "plugins"); var state = Path.Combine(root, "state"); var bundled = Path.Combine(root, "bundled");
        PluginManifest Install(string id, bool fan = false)
        {
            var folder = Path.Combine(plugins, id); Directory.CreateDirectory(folder);
            File.Copy(typeof(AverageFanPlugin).Assembly.Location, Path.Combine(folder, "logic.dll"));
            var manifest = new PluginManifest(id, id, "1", 1, "logic.dll", typeof(AverageFanPlugin).FullName!, fan ? ["fan.backend"] : [],
                [new(id + ".page", new("页面", "Page"))], [new("value", id + ".page", new("设置", "Setting"), "string", JsonSerializer.SerializeToElement("default"))], []);
            if (fan)
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "ThinkBookToolkit.FanBackend.dll"), Path.Combine(folder, "backend.dll"));
                manifest = manifest with { FanBackend = new("backend.dll", "ThinkBookToolkit.Tests.FakeFanBackend") };
            }
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(manifest)); return manifest;
        }
        Install("test.immediate"); Install("test.pending"); var backend = Install("test.backend", true); Install("test.changed");
        var options = new PluginManagerOptions(plugins, state, hostPath is null ? Path.Combine(Environment.CurrentDirectory,
            "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe") : Path.GetFullPath(hostPath), bundled);
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await runtime.Plugins.InitializeAsync();
        var immediate = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.immediate");
        await runtime.Plugins.SetEnabledAsync(immediate, true);
        await runtime.Plugins.SetSettingAsync(immediate, immediate.Manifest.Settings[0], JsonSerializer.SerializeToElement("keep me"));
        var reinstallArchive = Path.Combine(root, "reinstall.zip"); ZipFile.CreateFromDirectory(immediate.Directory, reinstallArchive);
        await runtime.Plugins.SuspendForExitAsync();
        await runtime.Plugins.RefreshAsync();
        Check(immediate.Client is null && immediate.Error is null, "Exit suspension restarted or faulted the worker.");
        await RejectAsync(() => PluginHostBridge.ExecuteAsync(runtime, ["host.control"], new("SetStatus", new Dictionary<string, JsonElement> { ["message"] = JsonSerializer.SerializeToElement("not allowed during exit") })));
        runtime.Plugins.ResumeAfterCancelledExit();
        await runtime.Plugins.RefreshAsync();
        Check(immediate.Client is not null && immediate.Error is null, "Cancelled exit could not resume the plugin worker.");
        Check(!await runtime.Plugins.UninstallAsync(immediate), "A worker-only plugin incorrectly requires restart.");
        Check(!Directory.Exists(immediate.Directory) && !runtime.Plugins.Installations.Contains(immediate) && runtime.Plugins.Page("test.immediate.page") is null,
            "Immediate uninstall left files or contributions behind.");
        Check(File.ReadAllText(Path.Combine(state, "test.immediate.json")).Contains("keep me"), "Uninstall erased retained plugin settings.");
        var reinstalled = await runtime.Plugins.ImportAsync(reinstallArchive);
        Check(!reinstalled.Enabled && reinstalled.Settings["value"].GetString() == "keep me", "Reinstall did not retain settings while resetting enable approval.");
        Check(!await runtime.Plugins.UninstallAsync(reinstalled), "Reinstalled worker-only plugin should uninstall without restart.");

        var pending = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.pending");
        await runtime.Plugins.SetEnabledAsync(pending, true);
        Check(await runtime.Plugins.UninstallAsync(pending, loadedForTesting: true) && pending.PendingUninstall && !pending.Enabled && Directory.Exists(pending.Directory),
            "Loaded-plugin uninstall was not deferred with code disabled.");
        await RejectAsync(() => runtime.Plugins.SetEnabledAsync(pending, true));
        var archive = Path.Combine(root, "pending.zip"); ZipFile.CreateFromDirectory(pending.Directory, archive);
        try { await runtime.Plugins.ImportAsync(archive); throw new Exception("Pending uninstall import succeeded."); }
        catch (PluginRestartRequiredException) { }

        var fan = runtime.Plugins.Installations.Single(p => p.Manifest.Id == backend.Id);
        Check(!runtime.Plugins.RequiresRestart(fan), "A disabled newly installed fan plugin should not request a pointless restart.");
        await runtime.Plugins.SetEnabledAsync(fan, true);
        Check(runtime.Plugins.RequiresRestart(fan), "Enabling a new backend must request restart.");
        var selected = new PluginFanBackendSelection(backend.Id, fan.Fingerprint, "backend.dll", backend.FanBackend!.Type);
        typeof(ToolkitPluginManager).GetProperty("ActiveFanBackend", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runtime.Plugins, selected);
        Check(await runtime.Plugins.UninstallAsync(fan) && runtime.Plugins.ActiveFanBackend == selected && Directory.Exists(fan.Directory),
            "Uninstall switched or removed the active fan backend before safe exit.");

        var changed = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.changed");
        await runtime.Plugins.UninstallAsync(changed, loadedForTesting: true);
        File.WriteAllText(Path.Combine(changed.Directory, "new-file.txt"), "new user contents");
        using (var restarted = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
        {
            await restarted.Plugins.InitializeAsync(safeMode: true);
            Check(!Directory.Exists(pending.Directory) && !Directory.Exists(fan.Directory), "Next startup did not finish pending removal.");
            Check(File.Exists(Path.Combine(changed.Directory, "new-file.txt")) && restarted.Plugins.HasPendingUninstalls,
                "Pending cleanup deleted files added after uninstall was scheduled.");
        }
        var store = new PluginRemovalStore(plugins, bundled, state);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep"), "keep");
        foreach (var folder in new[] { "..", "../outside", ".", ".imports", ".uninstalled", outside })
            Reject(() => store.Remove(new("user", folder, "bad", "bad", Guid.NewGuid().ToString("N"))));
        Check(File.Exists(Path.Combine(outside, "keep")), "Removal escaped the managed plugin folder.");

        var start = ToolkitApplicationRestart.CreateStartInfo(@"C:\Program Files\Toolkit\ThinkBookToolkit.exe", null, 123, 456, false);
        Check(start.FileName.EndsWith("ThinkBookToolkit.exe") && start.ArgumentList.SequenceEqual(new[] { "--restart-after-exit", "123", "456" }), "Restart arguments are incorrect.");
        var dotnet = ToolkitApplicationRestart.CreateStartInfo(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Toolkit\ThinkBookToolkit.dll", 123, 456, true);
        Check(dotnet.ArgumentList.First().EndsWith("ThinkBookToolkit.dll") && dotnet.ArgumentList.Last() == "--disable-plugins", "Restart lost DLL launch or safe mode.");
        var invalid = new[] { "--restart-after-exit", "0", "0" };
        Check(!ToolkitApplicationRestart.WaitForPreviousInstance(ref invalid), "Invalid restart target was accepted.");
        Console.WriteLine("Plugin uninstall/deferred cleanup/restart policy tests passed: " + root);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Unsafe uninstall path accepted."); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch { return; } throw new InvalidOperationException("Pending plugin could be enabled."); }
}
