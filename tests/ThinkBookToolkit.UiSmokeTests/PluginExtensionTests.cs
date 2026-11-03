using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginExtensionTests
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task RunAsync(string? hostPath)
    {
        var clock = DateTimeOffset.UtcNow;
        var threshold = new PluginSensorThreshold("sensor", "above", 80, 5, 3);
        var thresholdState = new PluginThresholdState();
        Check(!thresholdState.Update(threshold, 81, clock) && !thresholdState.Update(threshold, 82, clock.AddSeconds(2)) && thresholdState.Update(threshold, 81, clock.AddSeconds(3)), "Threshold duration was not respected.");
        Check(!thresholdState.Update(threshold, 77, clock.AddSeconds(4)) && thresholdState.Active, "Threshold hysteresis was not respected.");
        thresholdState.Update(threshold, null, clock.AddSeconds(5)); Check(!thresholdState.Active, "Missing reading kept threshold active.");
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-extension-tests", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(root, "plugins"); Directory.CreateDirectory(packages);
        var options = new PluginManagerOptions(packages, Path.Combine(root, "settings"), hostPath ?? Path.Combine(Environment.CurrentDirectory,
            "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe"), Path.Combine(root, "empty"));
        PluginManifest Manifest(string id) => new(id, id, "1.0.0", 2, Path.GetFileName(typeof(ExtensionProbePlugin).Assembly.Location), typeof(ExtensionProbePlugin).FullName!,
            ["host.control", "settings.write"], [],
            [new("id", "settings", new("ID", "ID"), "string", J(id)),
             new("delay", "settings", new("延迟", "Delay"), "number", J(0), Minimum: 0, Maximum: 10000),
             new("behavior", "settings", new("行为", "Behavior"), "string", J("normal")) { AllowExternalWrite = true },
             new("external-value", "settings", new("数值", "Value"), "number", J(0), Minimum: 0, Maximum: 100) { AllowExternalWrite = true }],
            [new(id + ".value", new("值", "Value"), "RPM", "fans"), new(id + ".events", new("事件", "Events"), "", "fans"), new(id + ".receipts", new("回执", "Receipts"), "", "fans")])
        {
            EventSubscriptions = ["plugin.enabled", "host.resume", "settings.changed"],
            Actions = [new(id + ".set", new("测试动作", "Test action"), [new("value", new("数值", "Value"), "number", J(20), 0, 100)])],
            Triggers = [new(id + ".signal", new("事件", "Event"))], Conditions = [new(id + ".ready", new("已就绪", "Ready")), new(id + ".high", new("高数值", "High value")) { Threshold = new(id + ".value", "above", 40) }],
            WritableSettings = [new("test.fast", "behavior"), new("test.fast", "external-value")], DataSubscriptions = [], RequiredCapabilities = ["commands.results"]
        };
        void Install(PluginManifest manifest)
        {
            var directory = Path.Combine(packages, manifest.Id); Directory.CreateDirectory(directory);
            File.Copy(typeof(ExtensionProbePlugin).Assembly.Location, Path.Combine(directory, manifest.EntryAssembly));
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
        }
        Install(Manifest("test.fast")); Install(Manifest("test.slow") with { TransientRetryLimit = 1 });
        Install(Manifest("enabled")); Install(Manifest("pending-uninstall"));
        Install(Manifest("test.dependent") with { Dependencies = [new("test.fast", "2.0.0")] });
        Install(Manifest("test.migration") with { SettingsVersion = 1, SettingRenames = [new("old-behavior", "behavior")] });
        Directory.CreateDirectory(options.StateRoot!);
        File.WriteAllText(Path.Combine(options.StateRoot!, "test.migration.json"), "{\"old-behavior\":\"migrated\"}");
        using (var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
        {
            await runtime.Plugins.InitializeAsync();
            foreach (var reservedId in new[] { "enabled", "pending-uninstall" })
            {
                var reserved = runtime.Plugins.Installations.Single(p => p.Manifest.Id == reservedId);
                await runtime.Plugins.SetEnabledAsync(reserved, true);
                var preferences = Path.Combine(options.StateRoot!, "enabled.json");
                var approval = File.ReadAllText(preferences);
                var setting = reserved.Manifest.Settings.Single(s => s.Id == "behavior");
                await runtime.Plugins.SetSettingAsync(reserved, setting, J("saved"));
                Check(File.ReadAllText(preferences) == approval && !File.Exists(Path.Combine(options.StateRoot!, "pending-uninstall.json")) &&
                    File.Exists(Path.Combine(options.StateRoot!, "plugin-data", reservedId + ".json")), "Plugin settings collided with host state: " + reservedId);
                await runtime.Plugins.SetEnabledAsync(reserved, false);
            }
            var fast = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.fast");
            var slow = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.slow");
            Check(runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.migration").Settings["behavior"].GetString() == "migrated" && Directory.GetFiles(Path.Combine(options.StateRoot!, "migration-backups")).Length > 0, "Settings migration did not preserve/backup old value.");
            try { await runtime.Plugins.SetEnabledAsync(runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.dependent"), true); throw new Exception("Incompatible dependency accepted."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("dependency")) { }
            await runtime.Plugins.SetEnabledAsync(fast, true); await runtime.Plugins.SetEnabledAsync(slow, true);
            Check(runtime.Plugins.Condition("test.fast.high"), "Host threshold condition was not evaluated.");
            fast.Settings["behavior"] = J("commands");
            await runtime.Plugins.RefreshAsync();
            Check(fast.Error is null && fast.Results.Any(r => r.RequestId == "invalid-setting" && r.Status == "failed") && fast.Results.Any(r => r.RequestId == "valid-command" && r.Status == "completed"), "Host command rejection suspended the plugin or lost receipts.");
            await runtime.Plugins.RefreshAsync();
            Check(fast.Values.Single(v => v.Id.EndsWith(".receipts")).Value == 2, "Worker did not receive command results.");
            var dedup = await runtime.Plugins.ExecuteCommandAsync(fast, new("SetStatus", new Dictionary<string, JsonElement>()) { RequestId = "valid-command" });
            Check(dedup.Status == "completed", "Command request ID was replayed instead of returning its receipt.");
            runtime.Plugins.NotifyEvent("host.resume");
            await runtime.Plugins.RefreshAsync();
            Check(fast.Values.Single(v => v.Id.EndsWith(".events")).Value >= 1, "Event subscription failed.");
            Check(PluginHostBridge.Context(runtime, ["data.read"], []).Data.EnumerateObject().Count() == 0, "Data subscriptions ignored.");

            slow.Settings["delay"] = J(1500);
            var old = fast.LastResultAt;
            var refresh = runtime.Plugins.RefreshAsync();
            var elapsed = Stopwatch.StartNew();
            while (fast.LastResultAt == old && elapsed.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(20);
            Check(fast.LastResultAt > old && !refresh.IsCompleted, "Slow plugin blocked an independent plugin.");
            var disable = Stopwatch.StartNew();
            await runtime.Plugins.SetEnabledAsync(slow, false);
            Check(disable.Elapsed < TimeSpan.FromSeconds(1), "Management waited on a slow plugin.");
            await refresh;
            Check(slow.Values.Length == 0 && !slow.Enabled, "Late response republished a disabled plugin.");

            Check(await runtime.Plugins.InvokeActionAsync("test.fast.set", J(new { value = 73 })) is null, "Plugin action failed.");
            Check(fast.Values.Single(v => v.Id == "test.fast.value").Value == 73, "Action parameters not passed to worker.");
            Check(await runtime.Plugins.InvokeActionAsync("test.fast.set", J(new { value = 101 })) is not null, "Invalid action parameter accepted.");
            Check(await runtime.Plugins.InvokeActionAsync("test.fast.set", J(new { value = 99 })) is not null, "An action with a rejected host command reported success.");
            runtime.Settings.Automations = [new() { Id = Guid.NewGuid().ToString("D"), Name = "plugin action", Steps = [new() { Kind = AutomationStepKind.PluginAction, Value = "test.fast.set", SecondaryValue = "{\"value\":31}" }], PluginConditions = ["test.fast.ready"] }];
            Check((await runtime.RunAutomationAsync(runtime.Settings.Automations[0].Id)).Success, "Automation did not execute the plugin action.");
            Check(fast.Values.Single(v => v.Id == "test.fast.value").Value == 31, "Automation did not preserve arguments.");
            var external = new HostCommand("toolkit.settings.write", new Dictionary<string, JsonElement> { ["pluginId"] = J("test.fast"), ["settingId"] = J("behavior"), ["value"] = J("changed") });
            Check((await runtime.Plugins.ExecuteCommandAsync(fast, external)).Status == "completed" && fast.Settings["behavior"].GetString() == "changed", "Scoped external setting write failed.");
            Check((await runtime.Plugins.ExecuteCommandAsync(fast, external)).Status == "failed", "Feedback loop cooldown ignored.");
            var denied = external with { Arguments = new Dictionary<string, JsonElement> { ["pluginId"] = J("toolkit"), ["settingId"] = J("toolkit.setting.Theme"), ["value"] = J("dark") } };
            Check((await runtime.Plugins.ExecuteCommandAsync(fast, denied)).Status == "denied", "External writes escaped approved scope.");
            var batch = new HostCommand("toolkit.settings.writeBatch", new Dictionary<string, JsonElement>
            { ["changes"] = J(new[] { new PluginSettingWrite("test.fast", "external-value", J(10)), new PluginSettingWrite("toolkit", "toolkit.setting.Theme", J("dark")) }) });
            Check((await runtime.Plugins.ExecuteCommandAsync(fast, batch)).Status == "denied" && fast.Settings["external-value"].GetInt32() == 0, "Batch changed a value before validating all permissions.");
            var compareWrite = new HostCommand("toolkit.settings.write", new Dictionary<string, JsonElement> { ["pluginId"] = J("test.fast"), ["settingId"] = J("external-value"), ["value"] = J(5), ["expectedValue"] = J(1) });
            Check((await runtime.Plugins.ExecuteCommandAsync(fast, compareWrite)).Status == "failed" && fast.Settings["external-value"].GetInt32() == 0, "Stale write overwrote a newer value.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check((await runtime.Plugins.ExecuteCommandAsync(fast, new("SetStatus", new Dictionary<string, JsonElement> { ["message"] = J("must not run") }), cancelled.Token)).Status == "cancelled", "Cancelled command was executed.");
            }

            slow.Settings["delay"] = J(0); slow.Settings["behavior"] = J("crash");
            await runtime.Plugins.SetEnabledAsync(slow, true);
            Check(slow.Error is null && slow.ConsecutiveFailures == 1, "Transient failure did not enter opt-in retry.");
            slow.Settings["behavior"] = J("normal"); slow.NextRefresh = default;
            await runtime.Plugins.RefreshAsync();
            Check(slow.Error is null && slow.Values.Length > 0 && slow.ConsecutiveFailures == 0, "Transient worker did not recover.");
            slow.Settings["behavior"] = J("throw");
            await runtime.Plugins.RefreshAsync();
            Check(slow.Error is not null, "Logic exceptions should not be retried as transport faults.");

            var metadata = PluginRecordingMetadata.Capture(runtime.Plugins.Installations, ["plugin:test.fast.value"]);
            var parsed = PluginRecordingMetadata.Parse(JsonSerializer.Serialize(metadata));
            Check(parsed?.Sensors["plugin:test.fast.value"].Version == "1.0.0", "Recording metadata lost version/source.");
            var charts = SensorRecordingViewerWindow.BuildChartGroups([new(DateTimeOffset.UtcNow, new Dictionary<string, double?> { ["plugin:test.fast.value"] = 42 })], [], true, parsed);
            Check(charts.Any(c => c.Charts.Any(ch => ch.Chinese == "值")), "Historical labels depend on installed plugin.");

            var update = Manifest("test.fast") with { Version = "2.0.0", SettingsVersion = 1, SettingRenames = [new("old-behavior", "behavior")] };
            var archiveRoot = Path.Combine(root, "archive"); Directory.CreateDirectory(archiveRoot);
            File.Copy(typeof(ExtensionProbePlugin).Assembly.Location, Path.Combine(archiveRoot, update.EntryAssembly));
            File.WriteAllText(Path.Combine(archiveRoot, "plugin.json"), JsonSerializer.Serialize(update));
            var zip = Path.Combine(root, "update.zip"); ZipFile.CreateFromDirectory(archiveRoot, zip);
            Check(await runtime.Plugins.StageUpdateAsync(fast, zip, _ => true), "Update was not staged.");
            await runtime.Plugins.CancelPendingUpdateAsync(fast);
            Check(!fast.PendingUpdate && !fast.Enabled && fast.Manifest.Version == "1.0.0", "Cancelling a staged update replaced or enabled a plugin.");
            Check(await runtime.Plugins.StageUpdateAsync(fast, zip, _ => true), "Cancelled update could not be restaged.");
            Check(fast.PendingUpdate && !fast.Enabled && File.Exists(Path.Combine(fast.Directory, "plugin.json")), "Update replaced live package or kept it enabled.");
        }
        using (var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
        {
            await runtime.Plugins.DiscoverAsync();
            var plugin = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.fast");
            Check(plugin.Manifest.Version == "2.0.0" && !plugin.Enabled && runtime.Plugins.CanRollback(plugin), "Startup update did not preserve backup/revoke approval.");
            Check(await runtime.Plugins.StageRollbackAsync(plugin, _ => true), "Rollback was not staged.");
        }
        using (var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options))
        {
            await runtime.Plugins.DiscoverAsync();
            var plugin = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.fast");
            Check(plugin.Manifest.Version == "1.0.0" && !plugin.Enabled && plugin.Settings["behavior"].GetString() == "changed", "Rollback lost old package/settings or silently enabled it.");
        }
        Console.WriteLine("Plugin extension commands, scheduling, events, automation, settings, recording metadata and package update/rollback tests passed: " + root);
    }
}
