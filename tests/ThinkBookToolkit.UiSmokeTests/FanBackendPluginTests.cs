using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using ThinkBookToolkit;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.Guardian;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class FanBackendPluginTests
{
    internal static void Run()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".tmp", "fan-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = CreatePackage(directory);
        ToolkitPluginManager.ValidateManifest(manifest, directory);
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Permissions = [] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { FanBackend = new("../outside.dll", "Test") }, directory));
        var selection = Selection(directory, manifest);
        PluginFanBackendPackage.ValidatePackage(directory, selection);
        Reject(() => PluginFanBackendPackage.ValidateSelection(selection with { Fingerprint = "../test" }));
        Reject(() => PluginFanBackendPackage.ValidatePackage(directory, selection with { Type = "Other.Type" }));
        var aclFactory = typeof(PluginFanBackendPackage).GetMethod("DirectorySecurity", BindingFlags.NonPublic | BindingFlags.Static)!;
        var aclValidator = typeof(PluginFanBackendPackage).GetMethod("VerifySecurity", BindingFlags.NonPublic | BindingFlags.Static)!;
        var acl = (DirectorySecurity)aclFactory.Invoke(null, [true])!;
        aclValidator.Invoke(null, [acl]);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        Reject(() => aclValidator.Invoke(null, [acl]));
        acl = (DirectorySecurity)aclFactory.Invoke(null, [false])!;
        aclValidator.Invoke(null, [acl]);
        acl.SetOwner(users);
        Reject(() => aclValidator.Invoke(null, [acl]));

        // Load the real legacy assembly first, without issuing hardware reads/writes.
        _ = typeof(ThinkBookToolkit.FanBackend.Wmi.WmiFanBackend);
        var loader = typeof(PluginFanBackendPackage).GetMethod("LoadFromVerifiedDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        IFanBackend Load(PluginFanBackendSelection selected) => (IFanBackend)loader.Invoke(null, [directory, selected])!;
        FanBackendRuntimeContext.DeclaredFanCount = 2;
        var backend = Load(selection);
        Check(backend.GetType().Assembly == Load(selection).GetType().Assembly, "Probe and runtime must share backend static synchronization.");
        Check(backend.Name == "Plugin test backend" && AssemblyLoadContext.GetLoadContext(backend.GetType().Assembly) != AssemblyLoadContext.Default,
            "Same-name plugin backend resolved to the legacy assembly.");
        backend.Apply(0, 4567);
        Check(backend.ReadSnapshot().Fan1Rpm == 0 && backend.ReadSnapshot().Fan2Rpm == 4567, "Targets must be delegated without interpreting zero as automatic.");
        Check(backend.ControlSemantics.ZeroRpmBehavior == FanTargetZeroBehavior.StopFanWhileKeepingManualControl &&
            backend.MinimumWriteInterval.TotalMilliseconds == 200 && FanController.ProbeFullSpeedControl(backend, out _), "Backend capabilities were lost.");
        backend.SetFullSpeed(true); Check(backend.ReadSnapshot().Fan1Rpm == 6000, "Full speed delegation failed.");
        backend.RestoreAuto(); Check(backend.ReadSnapshot().Fan1Rpm == 1234, "Automatic control restoration failed.");
        FanBackendRuntimeContext.DeclaredFanCount = 1;
        Check(Load(selection).ReadSnapshot().Fan2Rpm == 0, "Declared fan count did not share the host contract assembly.");
        FanBackendRuntimeContext.DeclaredFanCount = 2;
        Reject(() => FanWatchdogService.LoadBackend(new(1, 1, directory, "wrong", PluginBackend: selection)));
        Reject(() => FanWatchdogService.LoadBackend(new(1, 1, directory, selection.Identity)));
        var marker = new FanWatchdogMarker(1, 1, directory, selection.Identity, PluginBackend: selection, DeclaredFanCount: 1);
        Check(JsonSerializer.Deserialize<FanWatchdogMarker>(JsonSerializer.Serialize(marker)) == marker, "Guardian selection did not round-trip.");
        manifest = manifest with { FanBackend = manifest.FanBackend! with { Type = "ThinkBookToolkit.Tests.IncompatibleFanBackend" } };
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
        Reject(() => Load(Selection(directory, manifest)));
        Reject(() => PluginFanBackendPackage.ValidatePackage(directory, selection));
        Console.WriteLine("Fan plugin contract/loader tests passed: " + directory);
    }

    internal static async Task RunLifecycleAsync(string? hostPath)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "fan-plugin-lifecycle", Guid.NewGuid().ToString("N"));
        var code = Path.Combine(root, "plugins");
        var directory = Path.Combine(code, "fan"); Directory.CreateDirectory(directory);
        var manifest = CreatePackage(directory);
        var secondDirectory = Path.Combine(code, "other"); Directory.CreateDirectory(secondDirectory);
        CreatePackage(secondDirectory, "test.other-fan");
        var options = new PluginManagerOptions(code, Path.Combine(root, "settings"), hostPath ?? Path.Combine(Environment.CurrentDirectory,
            "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe"), Path.Combine(root, "empty"));
        try
        {
            using (var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
            {
                await runtime.Plugins.PrepareFanBackendAsync();
                Check(FanController.SelectedPlugin is null, "Unapproved plugin replaced the backend.");
                var plugin = runtime.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id);
                await runtime.Plugins.SetEnabledAsync(plugin, true);
                Check(plugin.Error is null && runtime.Plugins.Page(manifest.Pages[0].Id) is not null,
                    "Fan plugin must also support normal page/settings contributions: " + plugin.Error);
                Check(FanController.SelectedPlugin is null && runtime.Plugins.FanBackendStatus(plugin).Contains("重启"), "Enabling hot-swapped the backend.");
                await RejectAsync(() => runtime.Plugins.SetEnabledAsync(runtime.Plugins.Installations.Single(p => p != plugin), true));
            }
            var staged = false;
            using (var restarted = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
            {
                await restarted.Plugins.PrepareFanBackendAsync(stageForTesting: (path, selected) =>
                { PluginFanBackendPackage.ValidatePackage(path, selected); staged = true; });
                var selected = FanController.SelectedPlugin;
                Check(staged && selected?.PluginId == manifest.Id, "Approved plugin was not selected before hardware detection.");
                var plugin = restarted.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id);
                await restarted.Plugins.SetEnabledAsync(plugin, false);
                Check(FanController.SelectedPlugin == selected, "Disabling changed the active transport before restart.");
                await restarted.Plugins.SetEnabledAsync(plugin, true);
            }
            using (var failed = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
            {
                await failed.Plugins.PrepareFanBackendAsync(stageForTesting: (_, _) => throw new IOException("simulated staging failure"));
                Check(FanController.SelectedPlugin?.PluginId == manifest.Id, "Failure silently switched to the legacy backend.");
                Reject(() => new FanController());
            }
            using var safe = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
            await safe.Plugins.PrepareFanBackendAsync(safeMode: true);
            Check(FanController.SelectedPlugin is null && FanController.PluginPreparationError is null, "Safe startup did not restore legacy selection.");
        }
        finally { FanController.SelectedPlugin = null; FanController.PluginPreparationError = null; }
        Console.WriteLine("Fan plugin priority/restart/conflict/lifecycle tests passed.");
    }

    private static PluginManifest CreatePackage(string directory, string id = "toolkit.plugin-test")
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "ThinkBookToolkit.FanBackend.dll"), Path.Combine(directory, "ThinkBookToolkit.FanBackend.dll"));
        File.Copy(typeof(AverageFanPlugin).Assembly.Location, Path.Combine(directory, "ThinkBookToolkit.PluginTest.dll"));
        var manifest = new PluginManifest(id, "Fan and page test", "1.0", 1, "ThinkBookToolkit.PluginTest.dll", typeof(AverageFanPlugin).FullName!,
            ["fan.backend", "sensors.read"], [new(id + ".page", new("测试", "Test"))],
            [new("show-average", id + ".page", new("平均转速", "Average"), "boolean", JsonSerializer.SerializeToElement(false))],
            [new(id + ".average-rpm", new("平均转速", "Average"), "RPM", "fans")])
        { FanBackend = new("ThinkBookToolkit.FanBackend.dll", "ThinkBookToolkit.Tests.FakeFanBackend") };
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
        return manifest;
    }
    private static PluginFanBackendSelection Selection(string directory, PluginManifest manifest) =>
        new(manifest.Id, PluginFanBackendPackage.Fingerprint(directory), manifest.FanBackend!.Assembly, manifest.FanBackend.Type);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch { return; }
        throw new InvalidOperationException("Invalid fan backend input was accepted.");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch { return; }
        throw new InvalidOperationException("Conflicting fan backend was accepted.");
    }
}
