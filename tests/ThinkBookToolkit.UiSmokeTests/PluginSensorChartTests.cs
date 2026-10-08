using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginSensorChartTests
{
    internal static void Run()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-chart-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "fixture.dll"), [0]);
        var sensors = new[]
        {
            new PluginSensor("test.chart.both", new("双端固定", "Fixed bounds"), "W", "cpu") { ChartMinimum = -20, ChartMaximum = 120 },
            new PluginSensor("test.chart.lower", new("仅下限", "Minimum only"), "W", "cpu") { ChartMinimum = 50 },
            new PluginSensor("test.chart.upper", new("仅上限", "Maximum only"), "W", "cpu") { ChartMaximum = -10 },
            new PluginSensor("test.chart.auto", new("自动范围", "Automatic"), "W", "cpu"),
            new PluginSensor("test.chart.fan", new("替换风扇转速", "Replacement fan speed"), "RPM", "fans", "toolkit.sensor.fan1Rpm") { ChartMinimum = 800, ChartMaximum = 4500 }
        };
        var manifest = new PluginManifest("test.chart", "Chart fixture", "1", 1, "fixture.dll", "Fixture", ["replace"], [], [], sensors);
        ToolkitPluginManager.ValidateManifest(manifest, root);
        foreach (var bad in new[]
        {
            sensors[0] with { ChartMinimum = 120, ChartMaximum = 120 }, sensors[0] with { ChartMinimum = 121 },
            sensors[0] with { ChartMinimum = double.NaN }, sensors[0] with { ChartMaximum = double.PositiveInfinity },
            sensors[1] with { ChartMinimum = double.MaxValue }, sensors[2] with { ChartMaximum = double.MinValue }
        })
            Reject(() => ToolkitPluginManager.ValidateManifest(manifest with { Sensors = [bad] }, root));
        var serialized = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(manifest))!;
        Check(serialized.Sensors[0].ChartMinimum == -20 && serialized.Sensors[1].ChartMaximum is null && serialized.Sensors[2].ChartMinimum is null,
            "Optional chart limits did not round-trip independently.");
        var legacy = JsonSerializer.Deserialize<PluginSensor>("{\"Id\":\"test.chart.old\",\"Name\":{\"Chinese\":\"旧传感器\",\"English\":\"Old\"},\"Unit\":\"W\"}")!;
        Check(legacy.ChartMinimum is null && legacy.ChartMaximum is null, "Old sensor manifests acquired fixed bounds.");

        var plugin = new PluginInstallation(root, manifest, "fixture") { Enabled = true };
        var start = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 8).Select(i => new SensorRecordingSample(start.AddSeconds(i), new Dictionary<string, double?>
        {
            ["plugin:test.chart.both"] = -50 + i * 30, ["plugin:test.chart.lower"] = 45 + i * 8,
            ["plugin:test.chart.upper"] = -80 + i * 13, ["plugin:test.chart.auto"] = -30 + i * 8,
            ["fan1Rpm"] = 7000, ["fan2Rpm"] = 2000
        })).ToArray();
        var groups = SensorRecordingViewerWindow.BuildChartGroups(samples, [plugin], true);
        SensorRecordingViewerWindow.ChartDefinition Chart(string key) => groups.SelectMany(g => g.Charts).Single(c => c.Series.Any(s => s.Key == key));
        Check(Chart("plugin:test.chart.both").Minimum == -20 && Chart("plugin:test.chart.both").Maximum == 120 &&
            Chart("plugin:test.chart.lower").Minimum == 50 && Chart("plugin:test.chart.lower").Maximum is null &&
            Chart("plugin:test.chart.upper").Minimum is null && Chart("plugin:test.chart.upper").Maximum == -10 &&
            Chart("plugin:test.chart.auto").Minimum is null && Chart("plugin:test.chart.auto").Maximum is null, "Added sensor charts lost their declared/automatic limits.");
        Check(Chart("fan1Rpm").Minimum == 800 && Chart("fan1Rpm").Maximum == 4500 && Chart("fan1Rpm").Series.Count == 1 &&
            Chart("fan2Rpm").Minimum == 0 && Chart("fan2Rpm").Maximum is null && Chart("fan2Rpm").Series.Count == 1,
            "A replacement sensor changed the other fan's plot range.");
        plugin.Enabled = false;
        Check(PluginSensorChartPolicy.Replacement("fan1Rpm", [plugin]) == sensors[4], "The sole installed replacement lost its historical metadata when disabled.");
        var competitor = new PluginInstallation(root, manifest with { Id = "test.other", Sensors = [sensors[4] with { Id = "test.other.fan", ChartMaximum = 6000 }] }, "fixture");
        Check(PluginSensorChartPolicy.Replacement("fan1Rpm", [plugin, competitor]) is null, "Ambiguous inactive replacement providers were guessed.");
        competitor.Enabled = true;
        Check(PluginSensorChartPolicy.Replacement("fan1Rpm", [plugin, competitor])?.ChartMaximum == 6000, "Active replacement metadata did not win.");
        var without = SensorRecordingViewerWindow.BuildChartGroups(samples, [], true).SelectMany(g => g.Charts).ToArray();
        Check(without.Single(c => c.Series.Any(s => s.Key == "plugin:test.chart.both")).Minimum is null &&
            without.Single(c => c.Series.Any(s => s.Key == "fan1Rpm")).Series.Count == 2, "Missing plugins did not restore automatic/built-in chart behavior.");

        Check(SensorHistoryChart.ResolveAxisBounds([-500, 1000], 0, 100) == (0, 100), "Fixed bounds expanded to fit outliers.");
        var upper = SensorHistoryChart.ResolveAxisBounds([20, 80], null, -10);
        var lower = SensorHistoryChart.ResolveAxisBounds([-80, -20], 100, null);
        var auto = SensorHistoryChart.ResolveAxisBounds([-30, 10], null, null);
        Check(upper.Maximum == -10 && upper.Minimum < -10 && lower.Minimum == 100 && lower.Maximum > 100 &&
            auto.Minimum < -30 && auto.Maximum > 10, "Single-sided bounds moved the fixed endpoint or automatic scaling failed.");
        var empty = SensorHistoryChart.ResolveAxisBounds([], null, -10);
        var constant = SensorHistoryChart.ResolveAxisBounds([42, 42], null, null);
        var extreme = SensorHistoryChart.ResolveAxisBounds([double.MaxValue], null, null);
        Check(empty.Maximum == -10 && empty.Minimum < -10 && constant.Minimum < 42 && constant.Maximum > 42 &&
            double.IsFinite(extreme.Minimum) && double.IsFinite(extreme.Maximum) && extreme.Minimum < extreme.Maximum &&
            SensorHistoryChart.NormalizeReading(0, double.MinValue, double.MaxValue) == .5, "Empty/constant/extreme readings produced an invalid axis.");
        var original = SensorRecordingFormat.Batch(samples, SensorRecordingFormat.OrderKeys(samples[0].Values.Keys));
        Check(original.Contains("7000") && SensorHistoryChart.NormalizeReading(7000, 800, 4500) == 1, "Plot limits altered recorded readings instead of only the display.");

        var preview = new UniformGrid { Columns = 2, Background = new SolidColorBrush(Color.FromRgb(10, 16, 32)) };
        foreach (var sensor in sensors.Take(4))
        {
            var chart = Chart("plugin:" + sensor.Id);
            preview.Children.Add(new SensorHistoryChart(chart.Chinese, chart.Series, samples, true, true, chart.Minimum, chart.Maximum) { Margin = new Thickness(10) });
        }
        preview.Measure(new Size(940, 560)); preview.Arrange(new Rect(0, 0, 940, 560)); preview.UpdateLayout();
        var bitmap = new RenderTargetBitmap(940, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "chart-bounds.png"))) encoder.Save(file);
        Console.WriteLine("Plugin chart bounds validation, replacement routing and plotting tests passed: " + root);
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid chart limits accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
