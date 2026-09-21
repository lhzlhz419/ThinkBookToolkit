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
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class OverviewPluginTests
{
    internal static async Task RunAsync(string? hostPath)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "overview-plugin-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "test"); Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "ThinkBookToolkit.FanBackend.dll"), Path.Combine(directory, "ThinkBookToolkit.FanBackend.dll"));
        var manifest = new PluginManifest("test.overview", "Overview rows test", "1", 1,
            "ThinkBookToolkit.FanBackend.dll", "ThinkBookToolkit.Tests.OverviewRowsPlugin", ["replace"], [],
            [new("text", "plugins", new("文本", "Text"), "string", JsonSerializer.SerializeToElement("first value"))],
            [new("test.overview.sensor", new("测试传感器", "Test sensor"), "RPM", "fans"),
             new("test.overview.cpu", new("CPU 插件温度", "CPU plugin temperature"), "°C", "cpu"),
             new("test.overview.sensor-gpu", new("GPU 插件温度", "GPU plugin temperature"), "°C", "gpu"),
             new("test.overview.vram", new("显存插件温度", "VRAM plugin temperature"), "°C", "vram"),
             new("test.overview.battery", new("电池插件温度", "Battery plugin temperature"), "°C", "battery"),
             new("test.overview.memory", new("内存插件温度", "Memory plugin temperature"), "°C", "memory"),
             new("test.overview.storage", new("硬盘插件温度", "Disk plugin temperature"), "°C", "storage"),
             new("test.overview.sensor-power", new("插件功耗限制", "Plugin power limit"), "W", "power"),
             new("test.overview.warranty", new("插件保修天数", "Plugin warranty days"), "days", "warranty"),
             new("test.overview.ambient", new("室内温度", "Ambient temperature"), "°C", "test.overview.environment")])
        {
            SensorCategories = [new("test.overview.environment", new("环境", "Environment"))],
            Settings = [new("text", "overview", new("文本", "Text"), "string", JsonSerializer.SerializeToElement("first value"))],
            OverviewItems = [
                new("test.overview.dynamic", "cpu", Label: new("插件动态内容", "Dynamic plugin row")),
                new("test.overview.fan", "fans", "replace", "fan1-speed", new("替换风扇行", "Replacement fan"), SensorId: "test.overview.sensor"),
                new("test.overview.remove", "gpu", "remove", "core-frequency"),
                new("test.overview.gpu", "gpu", "replace", "core-temperature", new("替换温度", "Replacement temperature"), new("88 °C", "88 °C")),
                new("test.overview.gpu-note", "gpu", Label: new("额外显卡信息", "Additional GPU information"), Text: new("仅完整概览", "Detailed overview only")),
                new("test.overview.gpu-remove-power", "gpu", "remove", "power"),
                new("test.overview.disks", "memory-storage", "replace", "disk-temperatures", new("磁盘摘要", "Disk summary"), new("自定义磁盘信息", "Custom disk information")),
                new("test.overview.power", "power", "remove", "cpu-pl1")]
        };
        var manifestPath = Path.Combine(directory, "plugin.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        ToolkitPluginManager.ValidateManifest(manifest, directory);
        ToolkitPluginManager.ValidateManifest(manifest with { Author = "Example Developer" }, directory);
        ToolkitPluginManager.ValidateManifest(manifest with { Author = "   " }, directory);
        Check(JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(manifest with { Author = "Example Developer" }))?.Author == "Example Developer",
            "Optional author did not survive manifest serialization.");
        Check(JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath))?.Author is null, "Legacy manifest requires an author.");
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Author = new string('a', 161) }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { SensorCategories = [new("cpu", new("冒用内置", "Override builtin"))] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { SensorCategories = [new("test.overview.empty", new("", ""))] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Permissions = [] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { OverviewItems = [manifest.OverviewItems[0] with { CardId = "missing" }] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { OverviewItems = [manifest.OverviewItems[1] with { Target = "missing" }] }, directory));
        Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { OverviewItems = [manifest.OverviewItems[1], manifest.OverviewItems[1] with { Id = "test.overview.duplicate" }] }, directory));
        var invalid = new PluginInstallation(directory, manifest, "test");
        Reject(() => ToolkitPluginManager.ValidateResult(invalid, new(new Dictionary<string, double?>())
            { OverviewValues = new Dictionary<string, string?> { ["undeclared"] = "text" } }, DateTimeOffset.UtcNow));
        Reject(() => ToolkitPluginManager.ValidateResult(invalid, new(new Dictionary<string, double?>())
            { OverviewValues = new Dictionary<string, string?> { ["test.overview.dynamic"] = new string('a', 4097) } }, DateTimeOffset.UtcNow));

        var conflictDirectory = Path.Combine(root, "plugins", "conflict"); Directory.CreateDirectory(conflictDirectory);
        File.Copy(Path.Combine(directory, manifest.EntryAssembly), Path.Combine(conflictDirectory, manifest.EntryAssembly));
        File.WriteAllText(Path.Combine(conflictDirectory, "plugin.json"), JsonSerializer.Serialize(manifest with
        {
            Id = "test.conflict", Settings = [], Sensors = [], SensorCategories = [],
            OverviewItems = [new("test.conflict.row", "fans", "remove", "fan1-speed")]
        }));
        var options = new PluginManagerOptions(Path.Combine(root, "plugins"), Path.Combine(root, "state"),
            hostPath is null ? Path.Combine(Environment.CurrentDirectory, "src/ThinkBookToolkit/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.PluginHost.exe") : Path.GetFullPath(hostPath), Path.Combine(root, "empty"));
        using var runtime = new ToolkitRuntimeService(new AppSettings { CloseToTray = false, Theme = "system" }, persistSystemSessionState: false, pluginOptions: options);
        var report = new FeatureAvailabilityReport([new(FeatureIds.PerformanceMode, "性能", "模式", true, "initial")]);
        runtime.SetReportForTesting(report);
        await runtime.Plugins.InitializeAsync();
        var plugin = runtime.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false);
        window.Show(); window.UpdateLayout();
        try
        {
            var shell = window.Content;
            var scroll = window.MainScrollViewer;
            var page = window.CurrentPage;
            for (var i = 0; i < 8; i++)
            {
                runtime.SetReportForTesting(new FeatureAvailabilityReport(report.Items.Select(x => x with { Detail = "probe " + i })));
                runtime.SetSnapshotForTesting(ToolkitRuntimeSnapshot.Empty);
                await runtime.Plugins.RefreshAsync();
            }
            Check(ReferenceEquals(page, window.CurrentPage) && ReferenceEquals(shell, window.Content) && ReferenceEquals(scroll, window.MainScrollViewer),
                "Periodic availability/data updates rebuilt the shell or page.");
            var appearanceEvents = 0;
            runtime.AppearanceChanged += (_, _) => appearanceEvents++;
            var preferences = typeof(ToolkitRuntimeService).GetMethod("OnUserPreferenceChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (var i = 0; i < 5; i++) preferences.Invoke(runtime, [runtime, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.General)]);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(appearanceEvents == 0 && ReferenceEquals(page, window.CurrentPage), "Unchanged Windows preferences rebuilt the theme.");
            runtime.SetReportForTesting(new FeatureAvailabilityReport(report.Items.Append(new FeatureAvailability(FeatureIds.NvApiGpuPower, "性能", "GPU", true, "changed"))));
            Check(ReferenceEquals(shell, window.Content) && ReferenceEquals(page, window.CurrentPage), "Real capability changes must not reset the overview or background.");

            await runtime.Plugins.SetEnabledAsync(plugin, true);
            Check(plugin.Error is null, "Overview plugin failed: " + plugin.Error);
            Check(ReferenceEquals(shell, window.Content) && ReferenceEquals(scroll, window.MainScrollViewer), "Plugin catalog changes recreated the entire window.");
            window.UpdateLayout();
            VerifySensorPlacement(window, compact: false);
            var cpu = Card(window, "cpu");
            Check(Texts(cpu).Contains("插件动态内容") && Texts(cpu).Contains("first value"), "Added/dynamic overview row missing.");
            Check(Texts(Card(window, "fans")).Contains("替换风扇行") && Texts(Card(window, "fans")).Contains("3210 RPM"), "Sensor-backed replacement missing.");
            var gpu = Card(window, "gpu");
            Check(!Texts(gpu).Contains("核心频率") && Texts(gpu).Contains("显存频率") && Texts(gpu).Contains("88 °C") && Texts(gpu).Contains("热点温度"),
                "Removing/replacing one half lost the other half of a paired row.");
            Check(Texts(gpu).Contains("额外显卡信息"), "Detailed overview must retain additions beside replacements.");
            Check(Texts(Card(window, "memory-storage")).Contains("自定义磁盘信息"), "Dynamic built-in rows cannot be replaced.");
            var dynamicValue = Descendants(cpu).OfType<TextBlock>().Single(t => t.Text == "first value");
            var sensorPanel = Descendants(window.CurrentPage!).OfType<PluginSensorPanel>().First(p => p.Children.Count > 0);
            var sensorRow = sensorPanel.Children[0];
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement("updated value"));
            window.UpdateLayout();
            Check(dynamicValue.Text == "updated value" && ReferenceEquals(cpu, Card(window, "cpu")) && ReferenceEquals(sensorRow, sensorPanel.Children[0]),
                "Value update rebuilt rows/cards instead of updating text in place.");
            var customCard = Card(window, "test.overview.environment");
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement("hide-category"));
            window.UpdateLayout();
            Check(customCard.Visibility == Visibility.Collapsed, "Custom category must hide when it has no visible readings.");
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement("updated value"));
            window.UpdateLayout();
            Check(ReferenceEquals(customCard, Card(window, "test.overview.environment")) && customCard.Visibility == Visibility.Visible,
                "Reappearing readings rebuilt or failed to restore the custom category.");
            var osd = new ToolkitOsdWindow(runtime);
            try
            {
                runtime.Settings.Osd.Sensors = [];
                runtime.Settings.Osd.Orientation = OsdOrientation.Vertical;
                osd.ApplySettings(); osd.RefreshForTesting();
                var osdLabels = LogicalDescendants(osd.Content as DependencyObject ?? osd).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Check(osdLabels.Contains("环境") && osdLabels.Contains("室内温度") && !osdLabels.Contains("插件传感器"),
                    "OSD did not use the declared custom category.");
            }
            finally { osd.Close(); }
            Capture(window, Path.Combine(root, "detailed.png"));
            await RejectAsync(() => runtime.Plugins.SetEnabledAsync(runtime.Plugins.Installations.Single(p => p != plugin), true));

            runtime.Settings.OverviewPageMode = OverviewPageMode.Compact;
            window.NavigateForTesting("settings"); window.NavigateForTesting("overview"); window.UpdateLayout();
            Check(!Texts(Card(window, "cpu")).Contains("updated value") && Texts(Card(window, "fans")).Contains("替换风扇行") &&
                Texts(Card(window, "gpu")).Contains("88 °C"), "Compact overview did not apply row contributions.");
            Check(!Texts(Card(window, "gpu")).Contains("功耗") && !Texts(Card(window, "gpu")).Contains("额外显卡信息"),
                "Compact overview must apply removals and ignore additions even on a replaced card.");
            Check(!Descendants(window.CurrentPage!).OfType<PluginSensorPanel>().Any() &&
                !Texts(window.CurrentPage!).Contains("插件动态内容") && !Texts(window.CurrentPage!).Contains("环境") &&
                !Texts(window.CurrentPage!).Contains("文本"), "Compact overview leaked added rows, categories or settings.");
            Check(Descendants(Card(window, "cpu")).OfType<TextBlock>().Any(t => t.FontSize == 20),
                "An add-only contribution changed the original compact card layout.");
            Capture(window, Path.Combine(root, "compact.png"));
            runtime.Settings.OverviewPageMode = OverviewPageMode.Detailed;
            window.NavigateForTesting("settings"); window.NavigateForTesting("overview"); window.UpdateLayout();
            VerifySensorPlacement(window, compact: false);
            Check(Texts(Card(window, "cpu")).Contains("updated value"), "Switching back to detailed mode lost additions.");
            runtime.Settings.OverviewPageMode = OverviewPageMode.Compact;
            window.NavigateForTesting("settings"); window.NavigateForTesting("overview"); window.UpdateLayout();
            await runtime.Plugins.SetEnabledAsync(plugin, false);
            Check(!Texts(window.CurrentPage!).Contains("替换风扇行"), "Disabling did not restore compact content.");
            Check(!Texts(window.CurrentPage!).Contains("CPU 插件温度"), "Disabled sensor remained in its hardware card.");
            Check(!Texts(window.CurrentPage!).Contains("环境"), "Disabled custom category remained visible.");
            runtime.Settings.OverviewPageMode = OverviewPageMode.Detailed;
            window.NavigateForTesting("settings"); window.NavigateForTesting("overview"); window.UpdateLayout();
            Check(Texts(Card(window, "gpu")).Contains("核心频率") && !Texts(Card(window, "cpu")).Contains("插件动态内容"), "Built-in rows were not restored.");
        }
        finally
        {
            typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            // This is one of several windows in the same test Application; do
            // not let the production main-window handler end the test process.
            var closed = typeof(ToolkitMainWindow).GetMethod("OnClosed", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, closed);
            typeof(ToolkitMainWindow).GetMethod("DisposeWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            window.Close();
        }
        Console.WriteLine("Overview contributions and no-flicker regressions passed: " + root);
    }

    private static void VerifySensorPlacement(ToolkitMainWindow window, bool compact)
    {
        foreach (var (card, labels) in new (string, string[])[]
        {
            ("cpu", ["CPU 插件温度"]), ("gpu", ["GPU 插件温度", "显存插件温度"]),
            ("battery", ["电池插件温度"]), ("memory-storage", ["内存插件温度", "硬盘插件温度"]),
            ("fans", ["测试传感器"]), ("warranty", ["插件保修天数"]), ("test.overview.environment", ["室内温度"])
        })
            foreach (var label in labels)
            {
                Check(Texts(Card(window, card)).Contains(label), "Plugin sensor did not join card " + card + ": " + label);
                Check(Texts(window.CurrentPage!).Count(text => text == label) == 1, "Plugin sensor displayed more than once: " + label);
            }
        if (!compact) Check(Texts(Card(window, "power")).Contains("插件功耗限制"), "Power sensor did not join power limits.");
        Check(!Texts(window.CurrentPage!).Contains("插件传感器"), "Separate plugin sensor card returned.");
        Check(PluginSensorPlacement.OsdGroup("memory-storage") == "memory" && PluginSensorPlacement.OsdGroup("vram") == "vram" &&
            PluginSensorPlacement.OverviewCard("storage") == "memory-storage" && PluginSensorPlacement.OverviewCard("plugin") is null &&
            PluginSensorPlacement.HistoryGroup("fans") == "Fans" && PluginSensorPlacement.HistoryGroup("vram") == "GPU and VRAM" &&
            PluginSensorPlacement.HistoryGroup("battery") == "RAM, storage and battery", "Sensor category mappings are inconsistent.");
    }

    private static Border Card(ToolkitMainWindow window, string id) => Descendants(window.CurrentPage!).OfType<Border>().Single(b => b.Tag as string == id);
    private static string[] Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in LogicalDescendants(child)) yield return nested;
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Invalid overview input accepted."); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch { return; } throw new InvalidOperationException("Conflicting overview item accepted."); }
}
