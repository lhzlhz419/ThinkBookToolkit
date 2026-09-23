using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;
using ThinkBookToolkit.PluginUi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class CustomPluginPageTests
{
    internal static async Task RunAsync(string? hostPath)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "custom-page-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "plugins", "custom"); Directory.CreateDirectory(folder);
        File.Copy(typeof(AverageFanPlugin).Assembly.Location, Path.Combine(folder, "logic.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "ThinkBookToolkit.FakePluginUi.dll"), Path.Combine(folder, "views.dll"));
        PluginPage Page(string suffix, string type = "FakePage", string? replaces = null) => new("test.custom." + suffix,
            new("自绘测试 " + suffix, "Custom view " + suffix), replaces) { View = new("views.dll", "ThinkBookToolkit.Tests.Ui." + type) };
        var manifest = new PluginManifest("test.custom", "Custom UI fixture", "1", 1, "logic.dll", typeof(AverageFanPlugin).FullName!, ["ui.custom", "replace"],
            [Page("add"), Page("replace", replaces: "performance"), Page("fail", "FailingCreatePage"), Page("update", "FailingUpdatePage")],
            [new("value", "test.custom.add", new("值", "Value"), "string", JsonSerializer.SerializeToElement("initial"))], []);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(manifest));
        ToolkitPluginManager.ValidateManifest(manifest, folder);
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Permissions = ["replace"] }, folder));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Pages = [Page("bad") with { View = new("../views.dll", "Type") }] }, folder));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Pages = [Page("bad", replaces: "plugins")] }, folder));
        var options = new PluginManagerOptions(Path.Combine(root, "plugins"), Path.Combine(root, "settings"),
            hostPath is null ? Path.Combine(Environment.CurrentDirectory, "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe") : Path.GetFullPath(hostPath),
            Path.Combine(root, "bundled"));
        using var runtime = new ToolkitRuntimeService(new AppSettings { CloseToTray = false, Theme = "dark" }, persistSystemSessionState: false, pluginOptions: options);
        runtime.SetReportForTesting(new([new(FeatureIds.PerformanceMode, "性能", "模式", true, "test")]));
        await runtime.Plugins.InitializeAsync();
        var plugin = runtime.Plugins.Installations.Single();
        Check(!plugin.Enabled && runtime.Plugins.Page("test.custom.add") is null, "Unapproved custom UI is exposed.");
        await runtime.Plugins.SetEnabledAsync(plugin, true);
        Check(plugin.Error is null, "Logic worker failed: " + plugin.Error);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false); window.Show();
        try
        {
            window.NavigateForTesting("test.custom.add"); window.UpdateLayout();
            Check(window.CurrentPage is ToolkitCustomPluginPage, "Navigation did not create custom WPF UI.");
            var view = (StackPanel)window.CurrentPage!.Content;
            var context = (IPluginPageContext)view.Resources["context"];
            Check(context.PluginId == manifest.Id && context.PageId == "test.custom.add" && context.State.Request.Context.Sensors.Count == 0 &&
                context.State.Request.Context.Settings.GetRawText() == "{}", "Custom UI bypassed data permissions or lost page identity.");
            await context.SetSettingAsync("value", JsonSerializer.SerializeToElement("updated"));
            Check(view.Children.OfType<TextBlock>().Any(t => t.Text == "Value: updated"), "Own-setting write did not refresh the custom view.");
            await RejectAsync(() => context.SetSettingAsync("other.plugin.value", JsonSerializer.SerializeToElement("forbidden")));
            await RejectAsync(() => context.ExecuteAsync(new("SetStatus", new Dictionary<string, JsonElement> { ["message"] = JsonSerializer.SerializeToElement("forbidden") })));
            await Task.Delay(ToolkitMainWindow.PageTransitionDuration + TimeSpan.FromMilliseconds(80));
            window.UpdateLayout();
            Check(view.IsVisible && view.ActualHeight > 20 && context.State.IsDark, "Custom view is not visible or lacks theme context.");
            Capture(window, Path.Combine(root, "custom-page.png"));
            window.NavigateForTesting("overview");
            Check(context.Lifetime.IsCancellationRequested && Equals(view.Tag, "disposed"), "Navigating away did not dispose the view or cancel its lifetime.");
            await RejectAsync(() => context.SetSettingAsync("value", JsonSerializer.SerializeToElement("late write")));
            window.NavigateForTesting("test.custom.add");
            Check(!ReferenceEquals(window.CurrentPage!.Content, view), "Navigation reused a disposed custom page.");
            window.NavigateForTesting("performance");
            Check(window.CurrentPage is ToolkitCustomPluginPage, "Custom replacement did not replace a built-in page.");
            var replacedView = (StackPanel)window.CurrentPage.Content;
            await runtime.Plugins.SetEnabledAsync(plugin, false);
            Check(window.CurrentPage is ToolkitPerformancePage && Equals(replacedView.Tag, "disposed") && plugin.Error is null,
                "Disabling custom UI did not cleanly restore the built-in page.");
            await runtime.Plugins.SetEnabledAsync(plugin, true);
            window.NavigateForTesting("test.custom.fail");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(plugin.Error?.Contains("Test UI create failure") == true && window.CurrentPage is not ToolkitCustomPluginPage,
                "CreateView failure did not suspend the plugin and recover navigation.");
            await runtime.Plugins.SetEnabledAsync(plugin, true);
            window.NavigateForTesting("test.custom.update");
            var failingView = (StackPanel)window.CurrentPage!.Content;
            runtime.SetSnapshotForTesting(runtime.Snapshot);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(plugin.Error?.Contains("Test UI update failure") == true && Equals(failingView.Tag, "disposed"),
                "Update failure did not release the page and suspend the plugin.");
            await runtime.Plugins.SetEnabledAsync(plugin, true);
            File.WriteAllText(Path.Combine(folder, "changed.txt"), "changed package");
            window.NavigateForTesting("test.custom.add");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(plugin.Error?.Contains("Plugin files changed") == true, "UI loaded changed files without fresh approval.");
            Check(await runtime.Plugins.UninstallAsync(plugin) && plugin.PendingUninstall && Directory.Exists(folder),
                "An actually loaded WPF plugin must defer uninstall until restart.");
        }
        finally
        {
            typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            var closed = typeof(ToolkitMainWindow).GetMethod("OnClosed", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, closed);
            typeof(ToolkitMainWindow).GetMethod("DisposeWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            window.Close();
        }
        Console.WriteLine("Custom WPF pages, permissions, disposal and fallback tests passed: " + root);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Invalid custom page manifest accepted."); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch { return; } throw new InvalidOperationException("Unauthorized page action succeeded."); }
}
