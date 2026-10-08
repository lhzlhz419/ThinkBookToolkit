using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginToastTests
{
    internal static void Run()
    {
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false);
        var manifest = Manifest("test.toast", ["ui.toast"]);
        var plugin = new PluginInstallation("unused", manifest, "fixture") { Enabled = true };
        ((List<PluginInstallation>)runtime.Plugins.Installations).Add(plugin);
        var denied = new PluginInstallation("unused", Manifest("test.denied", []), "fixture") { Enabled = true };
        ((List<PluginInstallation>)runtime.Plugins.Installations).Add(denied);
        Reject(() => runtime.Plugins.ShowToast(denied, new("Denied")));
        Reject(() => ToolkitPluginManager.ValidateResult(denied, new(new Dictionary<string, double?>()) { Toast = new("Denied") }, DateTimeOffset.UtcNow));
        Reject(() => runtime.Plugins.ShowToast(plugin, new("   ")));
        Reject(() => runtime.Plugins.ShowToast(plugin, new(new string('a', 513))));
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false);
        try
        {
            Check(runtime.Plugins.ShowToast(plugin, new(" Saved ")) && window.ToastVisibleForTesting && window.ToastTextForTesting == "[Toast fixture] Saved",
                "Plugin toast did not reach Toolkit's attributed notification.");
            Check(!window.IsVisible, "Toast brought the window to the foreground.");
            Check(!runtime.Plugins.ShowToast(plugin, new("Saved")), "Duplicate toast was not suppressed.");
            plugin.LastToastTimestamp = 0;
            Check(runtime.Plugins.ShowToast(plugin, new("Failed", true)), "Error toast was rejected.");
            var border = (Border)typeof(ToolkitMainWindow).GetField("_toast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check(((SolidColorBrush)border.BorderBrush).Color == (Color)ColorConverter.ConvertFromString(ToolkitPalette.For(runtime.IsDark).Danger) &&
                border.ToolTip as string == manifest.Id, "Error styling or source identity is missing.");
            plugin.Enabled = false;
            Reject(() => runtime.Plugins.ShowToast(plugin, new("Inactive")));
            var restored = JsonSerializer.Deserialize<PluginResult>(JsonSerializer.Serialize(new PluginResult(new Dictionary<string, double?>()) { Toast = new("Message", true) }));
            Check(restored?.Toast is { Message: "Message", IsError: true }, "Toast protocol serialization failed.");
        }
        finally
        {
            typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            var closed = typeof(ToolkitMainWindow).GetMethod("OnClosed", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, closed);
            typeof(ToolkitMainWindow).GetMethod("DisposeWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null); window.Close();
        }
        Console.WriteLine("Plugin toast permissions, attribution, styling and suppression tests passed.");
    }

    internal static async Task RunWorkerAsync(string? hostPath)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-toast-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "toast"); Directory.CreateDirectory(directory);
        var manifest = Manifest("test.worker-toast", ["ui.toast"]);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "ThinkBookToolkit.FanBackend.dll"), Path.Combine(directory, "fixture.dll"));
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
        var options = new PluginManagerOptions(Path.Combine(root, "plugins"), Path.Combine(root, "settings"),
            hostPath is null ? Path.Combine(Environment.CurrentDirectory, "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe") : Path.GetFullPath(hostPath), Path.Combine(root, "bundled"));
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        var received = new List<PluginToastNotification>(); runtime.PluginToastRequested += (_, message) => received.Add(message);
        await runtime.Plugins.InitializeAsync();
        var plugin = runtime.Plugins.Installations.Single();
        await runtime.Plugins.SetEnabledAsync(plugin, true);
        await runtime.Plugins.RefreshAsync();
        Check(plugin.Error is null && received.Count == 1 && received[0] is { Message: "Worker toast", IsError: true } &&
            received[0].PluginId == manifest.Id, "Worker toast failed: " + plugin.Error);
        await runtime.Plugins.SuspendForExitAsync();
        Reject(() => runtime.Plugins.ShowToast(plugin, new("During exit")));
        Console.WriteLine("Worker plugin toast delivery and exit suppression tests passed.");
    }
    private static PluginManifest Manifest(string id, string[] permissions) => new(id, "Toast fixture", "1", 1, "fixture.dll", "ThinkBookToolkit.Tests.ToastPlugin", permissions, [], [], []);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return; } throw new Exception("Invalid toast request accepted."); }
}
