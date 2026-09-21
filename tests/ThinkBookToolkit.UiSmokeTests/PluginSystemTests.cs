using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginSystemTests
{
    internal static void Run(string? hostPath = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = RunAsync(hostPath); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task RunAsync(string? hostPath)
    {
        await FanBackendPluginTests.RunLifecycleAsync(hostPath);
        await PluginImportTests.RunAsync();
        await OverviewPluginTests.RunAsync(hostPath);
        var repo = Environment.CurrentDirectory;
        var root = Path.Combine(repo, ".tmp", "plugin-tests", Guid.NewGuid().ToString("N"));
        var code = Path.Combine(root, "plugins"); Directory.CreateDirectory(code);
        var bundled = Path.Combine(repo, "src", "ThinkBookToolkit", "bin", "Release", "net9.0-windows", "win-x64");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(repo, "plugins", "ThinkBookToolkit.PluginTest", "plugin.json")))!;
        void Install(PluginManifest definition)
        {
            var directory = Path.Combine(code, definition.Id); Directory.CreateDirectory(directory);
            File.Copy(typeof(AverageFanPlugin).Assembly.Location, Path.Combine(directory, definition.EntryAssembly));
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(definition));
        }
        Install(manifest);
        foreach (var id in new[] { "test.override-a", "test.override-b" })
            Install(manifest with
            {
                Id = id, Name = id, Permissions = ["replace"],
                Pages = [new(id + ".page", new("替换性能页面", "Replacement performance page"), "performance")],
                Settings = [new("replacement", id + ".page", new("替换日志选项", "Replacement log option"), "choice", JsonSerializer.SerializeToElement("ERROR"), "toolkit.setting.LogLevel", Choices: ["INFO", "WARN", "ERROR", "NONE"])],
                Sensors = []
            });
        var options = new PluginManagerOptions(code, Path.Combine(root, "settings"), hostPath is null ? Path.Combine(bundled, "ThinkBookToolkit.PluginHost.exe") : Path.GetFullPath(hostPath), Path.Combine(root, "empty-bundled"));
        using var runtime = new ToolkitRuntimeService(new AppSettings { CloseToTray = false }, persistSystemSessionState: false, pluginOptions: options);
        void Fans(int first, int second) => runtime.SetSnapshotForTesting(ToolkitRuntimeSnapshot.Empty with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Fans = new(DateTimeOffset.UtcNow, first, second, new Dictionary<string, FanLimit>())
        });
        Fans(2000, 4000);
        await runtime.Plugins.InitializeAsync();
        Check(runtime.Plugins.Installations.Count == 3 && runtime.Plugins.Installations.All(p => !p.Enabled), "External plugin code must not execute before approval.");
        var plugin = runtime.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id);
        await runtime.Plugins.SetEnabledAsync(plugin, true);
        Check(plugin.Error is null, "Plugin process did not start: " + plugin.Error);
        Check(!plugin.Settings["show-average"].GetBoolean() && runtime.Plugins.Sensors.Count == 0, "The sample must default to off.");
        runtime.SetReportForTesting(new FeatureAvailabilityReport([new(FeatureIds.PerformanceMode, "性能", "性能模式", true, "test")]));
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false);
        window.Show();
        try
        {
            window.NavigateForTesting("toolkit.plugin-test.page");
            Check(window.CurrentPage is ToolkitPluginPage, "The plugin navigation page was not registered.");
            var toggle = Descendants(window.CurrentPage!).OfType<CheckBox>().Single();
            Check(toggle.Content?.ToString() == "显示平均风扇转速" && toggle.IsChecked == false, "The sample setting was not rendered correctly.");
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement(true));
            var expected = DeviceModelDetector.HasSecondFan() ? 3000 : 2000;
            Check(runtime.Plugins.Sensors.Single().Value == expected, "Average fan RPM is incorrect: " + plugin.Error);
            Check(runtime.Snapshot.PluginSensors.Single().Name == "平均转速", "Plugin data was not published to the runtime/shared snapshot.");
            Check(LocalDataSharingService.BuildSnapshot(runtime.Snapshot).PluginSensors.Single().Value == expected,
                "Plugin readings are absent from the local data sharing payload.");
            window.NavigateForTesting("overview"); window.UpdateLayout();
            Check(Descendants(window.CurrentPage!).OfType<TextBlock>().Any(t => t.Text.Contains("平均转速")), "Average RPM is missing from sensor UI.");
            var screenshot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using (var image = File.Create(Path.Combine(root, "average-rpm.png"))) encoder.Save(image);
            await VerifyOsdAsync(runtime, plugin, manifest.Settings[0], root);
            Fans(2000, 4000);
            await runtime.Plugins.RefreshAsync();
            var merged = SensorRecordingService.MergePluginSample(new(DateTimeOffset.UtcNow, new Dictionary<string, double?> { ["fan1Rpm"] = 2000 }), runtime.Plugins.Sensors);
            Check(merged.Values["plugin:toolkit.plugin-test.average-rpm"] == expected, "Plugin sensor was not included in recording data.");
            var header = SensorRecordingFormat.Header(SensorRecordingFormat.OrderKeys(merged.Values.Keys));
            Check(SensorRecordingFormat.TryReadHeader(header, out var keys) && keys.Contains("plugin:toolkit.plugin-test.average-rpm"), "Extensible recording header could not round-trip.");
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement(false));
            Check(runtime.Plugins.Sensors.Count == 0, "Turning the sample setting off did not remove the sensor.");

            var first = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.override-a");
            var second = runtime.Plugins.Installations.Single(p => p.Manifest.Id == "test.override-b");
            await runtime.Plugins.SetEnabledAsync(first, true);
            window.NavigateForTesting("performance");
            Check(window.CurrentPage is ToolkitPluginPage, "Page replacement did not take effect.");
            window.NavigateForTesting("settings");
            Check(Descendants(window.CurrentPage!).OfType<TextBlock>().Any(t => t.Text == "替换日志选项"), "Registered setting replacement was not rendered.");
            await ExpectFailureAsync(() => runtime.Plugins.SetEnabledAsync(second, true));
            await runtime.Plugins.SetEnabledAsync(first, false);
            Check(runtime.Plugins.Page("performance") is null && runtime.Plugins.Setting("toolkit.setting.LogLevel") is null, "Disabling a plugin did not restore built-in registrations.");
            await ExpectFailureAsync(() => PluginHostBridge.ExecuteAsync(runtime, ["sensors.read"], new("SetItsModeAsync", new Dictionary<string, JsonElement>())));

            Fans(0, 0);
            await runtime.Plugins.SetSettingAsync(plugin, manifest.Settings[0], JsonSerializer.SerializeToElement(true));
            Check(runtime.Plugins.Sensors.Single().Value == 0, "Stopped fans must participate as valid zero readings.");
            var process = (Process)typeof(PluginProcessClient).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin.Client!)!;
            process.Kill(); await process.WaitForExitAsync();
            await runtime.Plugins.RefreshAsync();
            Check(plugin.Error is not null && runtime.Plugins.Sensors.Count == 0 && runtime.Plugins.Page("toolkit.plugin-test.page") is null, "Worker failure did not withdraw its contributions.");
        }
        finally
        {
            typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }
        await using var average = new AverageFanPlugin();
        async Task<double?> Average(params double?[] values)
        {
            var readings = values.Select((value, index) => new SensorReading("toolkit.sensor.fan" + (index + 1) + "Rpm", value, "RPM", DateTimeOffset.UtcNow, "test", value.HasValue ? "valid" : "unavailable")).ToDictionary(x => x.Id);
            var request = new PluginRequest("refresh", new(DateTimeOffset.UtcNow, readings, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), []),
                new Dictionary<string, JsonElement> { ["show-average"] = JsonSerializer.SerializeToElement(true) });
            return (await average.EvaluateAsync(request, CancellationToken.None)).Values.Single().Value;
        }
        Check(await Average(2000, 4000) == 3000 && await Average(0, 2000) == 1000 && await Average(2500) == 2500 &&
              await Average(null, null) is null && await Average(-1, 2000) == 2000, "Average plugin edge cases failed.");
        using var safe = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await safe.Plugins.InitializeAsync(safeMode: true);
        Check(safe.Plugins.Installations.Count == 0, "Safe mode executed plugins.");
        using var restored = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await restored.Plugins.InitializeAsync();
        Check(restored.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id).Settings["show-average"].GetBoolean(),
            "Plugin-owned settings did not survive a restart.");
        restored.Plugins.Dispose();
        File.AppendAllText(Path.Combine(code, manifest.Id, manifest.EntryAssembly), "test modification");
        using var changed = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await changed.Plugins.InitializeAsync();
        Check(!changed.Plugins.Installations.Single(p => p.Manifest.Id == manifest.Id).Enabled,
            "Changed plugin binaries executed using a previous approval.");
        Console.WriteLine("Plugin tests and screenshot: " + root);
    }
    private static async Task VerifyOsdAsync(ToolkitRuntimeService runtime, PluginInstallation plugin, PluginSetting setting, string output)
    {
        var previous = runtime.Settings.Osd;
        runtime.Settings.Osd = new ToolkitOsdSettings
        {
            Orientation = OsdOrientation.Vertical, FontSize = 20,
            Sensors = [OsdSensor.Fan1Speed, OsdSensor.Fan2Speed],
            LabelColor = "#AABBCC", ValueColor = "#FFEEDD", BackgroundColor = "#182030", OpacityPercent = 85
        };
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Fans = new(DateTimeOffset.UtcNow, 2417, 1896, new Dictionary<string, FanLimit>())
        });
        await runtime.Plugins.RefreshAsync();
        var osd = new ToolkitOsdWindow(runtime);
        try
        {
            osd.ApplySettings(); osd.RefreshForTesting();
            var view = (Viewbox)osd.Content;
            var texts = Descendants(view).OfType<TextBlock>().ToArray();
            var label = texts.Single(t => t.Text == "平均转速");
            var expected = ToolkitOsdWindow.FormatPluginValue(runtime.Plugins.Sensors.Single(), true);
            Check(label.Parent is Grid, "OSD plugin label must belong to a row grid.");
            var value = ((Grid)label.Parent).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 1);
            var nativeLabel = texts.Single(t => t.Text is "风扇1转速" or "风扇转速");
            Check(label.Parent is Grid row && ReferenceEquals(row, value.Parent) && Grid.GetColumn(value) == 1,
                "OSD plugin sensor must use a two-column row, not a concatenated text line.");
            Check(ReferenceEquals(LogicalTreeHelper.GetParent((DependencyObject)label.Parent), LogicalTreeHelper.GetParent((DependencyObject)nativeLabel.Parent)),
                "Average fan RPM was not inserted into the native Fans group.");
            Check(value.TextAlignment == TextAlignment.Right && value.FontWeight == FontWeights.SemiBold && value.Text.EndsWith(" 转"),
                "OSD plugin values do not match native alignment, weight, or localized RPM units.");
            Check(((SolidColorBrush)value.Foreground).Color == (Color)ColorConverter.ConvertFromString("#FFEEDD") &&
                  ((SolidColorBrush)label.Foreground).Color == (Color)ColorConverter.ConvertFromString("#AABBCC"),
                "OSD plugin values ignore the configured label/value colors.");
            RenderOsd(osd, Path.Combine(output, "osd-plugin-vertical.png"));
            osd.Show();
            osd.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            ((DispatcherTimer)typeof(ToolkitOsdWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(osd)!).Stop();
            var expandedHeight = osd.Height;
            await runtime.Plugins.SetSettingAsync(plugin, setting, JsonSerializer.SerializeToElement(false));
            Check(osd.Height < expandedHeight, "Native OSD height did not shrink when a plugin row disappeared.");
            await runtime.Plugins.SetSettingAsync(plugin, setting, JsonSerializer.SerializeToElement(true));
            Check(osd.Height >= expandedHeight - 1, "Native OSD did not grow to contain a newly visible plugin row.");
            runtime.Settings.Osd.Orientation = OsdOrientation.Horizontal;
            osd.ApplySettings(); osd.RefreshForTesting();
            RenderOsd(osd, Path.Combine(output, "osd-plugin-horizontal.png"));
            Check(Descendants((Viewbox)osd.Content).OfType<TextBlock>().Count(t => t.Text == "风扇") == 1,
                "Horizontal OSD has duplicate Fans group headers.");
            await runtime.Plugins.SetSettingAsync(plugin, setting, JsonSerializer.SerializeToElement(false));
            Check(!Descendants((Viewbox)osd.Content).OfType<FrameworkElement>().Any(t => t.ToolTip is string hint && hint.Contains("toolkit.plugin-test")),
                "OSD retains plugin readings after their setting is disabled.");
            runtime.Settings.Osd.Orientation = OsdOrientation.Vertical;
            runtime.Settings.Osd.Sensors = [];
            osd.ApplySettings();
            await runtime.Plugins.SetSettingAsync(plugin, setting, JsonSerializer.SerializeToElement(true));
            Check(Descendants((Viewbox)osd.Content).OfType<TextBlock>().Any(t => t.Text == "平均转速"),
                "A plugin-only Fans group is not shown when native fan rows are deselected.");
        }
        finally { osd.Close(); runtime.Settings.Osd = previous; }
    }
    private static void RenderOsd(ToolkitOsdWindow osd, string path)
    {
        osd.ResizeContent(new Size(900, 600), new DpiScale(1, 1));
        var view = (Viewbox)osd.Content;
        view.Measure(new Size(osd.Width, osd.Height)); view.Arrange(new Rect(0, 0, osd.Width, osd.Height)); view.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(osd.Width), (int)Math.Ceiling(osd.Height), 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static async Task ExpectFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe/conflicting plugin operation was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
