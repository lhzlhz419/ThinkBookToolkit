using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginManagerLayoutTests
{
    internal static void Run()
    {
        var output = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-management-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        foreach (var chinese in new[] { true, false })
        {
            using var runtime = new ToolkitRuntimeService(new AppSettings
            {
                Theme = chinese ? "dark" : "light", Language = chinese ? "zh-CN" : "en-US", BackgroundBaseColorEnabled = true
            }, persistSystemSessionState: false);
            ModernTheme.Apply(Application.Current, runtime.IsDark);
            var manifest = new PluginManifest("test.ec", "ThinkBook EC 传感器与风扇后端", "1.0.4", 1,
                "ThinkBookToolkit.EcControl.dll", "ThinkBookToolkit.EcControl.EcPlugin", ["fan.backend", "sensors.read", "replace"], [], [],
                [new("test.ec.fan1", new("风扇1转速", "Fan 1 speed"), "RPM", "fans", "toolkit.sensor.fan1Rpm"),
                 new("test.ec.fan2", new("风扇2转速", "Fan 2 speed"), "RPM", "fans", "toolkit.sensor.fan2Rpm"),
                 new("test.ec.battery", new("电池功率", "Battery power"), "W", "battery", "toolkit.sensor.batteryPowerW")])
            { Author = "  Example Developer  ", FanBackend = new("ThinkBookToolkit.EcControl.dll", "ThinkBookToolkit.EcControl.EcFanBackend") };
            var plugins = (List<PluginInstallation>)runtime.Plugins.Installations;
            plugins.Add(new(output, manifest, "fixture") { Enabled = true });
            plugins.Add(new(output, manifest with { Id = "test.disabled", Name = chinese ? "自定义传感器扩展" : "Custom sensor extension",
                Version = "2.0.0", Author = null, FanBackend = null, Permissions = ["sensors.read"], Sensors = [] }, "fixture"));
            plugins.Add(new(output, manifest with { Id = "test.error", Name = chinese ? "一个较长的插件名称：用于检查窄窗口中的布局与文字换行" : "A longer plugin name for checking responsive layout and readable wrapping",
                Author = "   ", FanBackend = null, Permissions = ["data.read", "settings.read", "host.control"], Sensors = [],
                Pages = [new("test.error.page", new("扩展页", "Extension"))],
                Settings = [new("option", "test.error.page", new("选项", "Option"), "boolean", JsonSerializer.SerializeToElement(false))] }, "fixture")
            { Enabled = true, Error = chinese ? "连接插件进程超时。请检查插件依赖，然后停用并重新启用。" : "The plugin process timed out. Check its dependencies, then disable and enable it again." });
            typeof(ToolkitPluginManager).GetProperty("ActiveFanBackend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(runtime.Plugins, new PluginFanBackendSelection(manifest.Id, new string('0', 64), manifest.FanBackend.Assembly, manifest.FanBackend.Type));
            using var page = new ToolkitPluginsPage(runtime);
            foreach (var width in new[] { 1040d, 500d })
            {
                Layout(page, width);
                var cards = Descendants(page).OfType<Border>().Where(b => b.Tag is string tag && tag.StartsWith("plugin-card:")).ToArray();
                var expected = (Color)ColorConverter.ConvertFromString(ToolkitPalette.For(runtime.IsDark, runtime.HasCustomBackground).Surface);
                Check(cards.Length == 3 && cards.All(c => c.Background is SolidColorBrush brush && brush.Color == expected), "Plugin cards must use the same transparency as other Toolkit cards.");
                var authorPrefix = chinese ? "作者：" : "Author: ";
                Check(Descendants(cards[0]).OfType<TextBlock>().Any(t => t.Text == authorPrefix + "Example Developer"), "Declared author is missing or not trimmed.");
                Check(cards.Skip(1).All(c => !Descendants(c).OfType<TextBlock>().Any(t => t.Text.StartsWith(authorPrefix, StringComparison.Ordinal))), "Missing/blank authors must not reserve an author row.");
                Check(Descendants(page).OfType<Expander>().All(e => !e.IsExpanded), "Technical identifiers should be collapsed by default.");
                foreach (var card in cards)
                {
                    var toggle = Descendants(card).OfType<CheckBox>().Single();
                    Check(Grid.GetRow(toggle) == (width < 650 ? 1 : 0), "Enable toggle did not adapt to narrow layout.");
                    var bounds = toggle.TransformToAncestor(card).TransformBounds(new Rect(toggle.RenderSize));
                    Check(bounds.Left >= 0 && bounds.Right <= card.ActualWidth + 1, "Enable toggle overflowed its card.");
                }
                var open = Descendants(page).OfType<Button>().Single(b => b.Content?.ToString() == (chinese ? "打开插件目录" : "Open plugin folder"));
                Check(open.ActualWidth < 250, "The folder action should not stretch across the page.");
                var import = Descendants(page).OfType<Button>().Single(b => b.Content?.ToString() == (chinese ? "导入插件" : "Import plugin"));
                Check(import.ActualWidth < 250 && import.IsEnabled, "The import action is missing, disabled or oversized.");
                Render(page, Path.Combine(output, $"{(chinese ? "dark-zh" : "light-en")}-{width}.png"));
            }
            var details = Descendants(page).OfType<Expander>().First(); details.IsExpanded = true;
            Layout(page, 500);
            Check(Descendants(details).OfType<TextBlock>().Any(t => t.Text.Contains("toolkit.sensor.fan1Rpm") && t.Text.Contains("EcFanBackend")), "Technical details lost backend or replacement identifiers.");
            Render(page, Path.Combine(output, $"{(chinese ? "dark-zh" : "light-en")}-details.png"));
            runtime.Settings.BackgroundBaseColorEnabled = false;
            using var plain = new ToolkitPluginsPage(runtime);
            Layout(plain, 1040);
            Check(Descendants(plain).OfType<Border>().Where(b => b.Tag is string tag && tag.StartsWith("plugin-card:"))
                .All(b => b.Background is SolidColorBrush brush && brush.Color.A == 255), "Cards without a custom background should use the regular opaque palette.");
        }
        Console.WriteLine("Plugin management responsive layout previews: " + output);
    }
    private static void Layout(FrameworkElement page, double width)
    {
        for (var i = 0; i < 3; i++)
        {
            page.Measure(new Size(width, double.PositiveInfinity));
            page.Arrange(new Rect(0, 0, width, page.DesiredSize.Height)); page.UpdateLayout();
        }
    }
    private static void Render(FrameworkElement page, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(page); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
