using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginSensorSelectionTests
{
    internal static void Run()
    {
        var cpu = new PluginSensor("test.selection.cpu", new("新增 CPU 读数", "Added CPU reading"), "W", "cpu");
        var fan = new PluginSensor("test.selection.fan", new("新增风扇读数", "Added fan reading"), "RPM", "fans");
        var custom = new PluginSensor("test.selection.custom", new("自定义读数", "Custom reading"), "°C", "test.selection.group");
        var unavailable = new PluginSensor("test.selection.unavailable", new("暂时无数据", "No data yet"), "W", "cpu");
        var replacement = new PluginSensor("test.selection.replacement", new("替换风扇", "Replacement fan"), "RPM", "fans", "toolkit.sensor.fan1Rpm");
        var manifest = new PluginManifest("test.selection", "Selection test", "1", 1, "unused.dll", "Unused", [], [], [],
            [cpu, fan, custom, unavailable, replacement]) { SensorCategories = [new("test.selection.group", new("测试分类", "Test category"))] };
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false);
        var plugin = new PluginInstallation("unused", manifest, "fixture") { Enabled = true };
        ((List<PluginInstallation>)runtime.Plugins.Installations).Add(plugin);
        plugin.Values = new[] { cpu, fan, custom, replacement }.Select(s => new PublishedPluginSensor(s.Id, s.Name.Chinese, s.Name.English, s.Unit,
            s.Category, 3000, manifest.Id, s.Replaces, DateTimeOffset.UtcNow)).ToArray();

        var osdSettings = new OsdSettingsWindow(runtime);
        var recordingSettings = new SensorRecordingSettingsWindow(runtime);
        using var settings = new ToolkitSettingsPage(runtime);
        var editor = (DependencyObject)typeof(ToolkitSettingsPage).GetMethod("BuildOverviewEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, null)!;
        try
        {
            foreach (var root in new DependencyObject[] { osdSettings, recordingSettings, editor })
            {
                var ids = Descendants(root).OfType<CheckBox>().Select(c => c.Tag as string).ToArray();
                foreach (var sensor in new[] { cpu, fan, custom, unavailable })
                    Check(ids.Contains("plugin:" + sensor.Id), "Plugin sensor is missing from a selection editor: " + sensor.Id);
                Check(!ids.Contains("plugin:" + replacement.Id), "Replacement duplicated the existing built-in sensor switch.");
            }
            var local = new List<string>(); var options = new StackPanel();
            PluginSensorSelection.AddToggles(options, runtime, PluginSensorSurface.Overview, "cpu", () => local,
                (id, enabled) => PluginSensorSelection.SetEnabled(local, id, enabled));
            var toggle = options.Children.OfType<CheckBox>().Single(c => c.Tag as string == "plugin:" + cpu.Id);
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(local.Contains(cpu.Id), "The generated switch did not update selection.");
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!local.Contains(cpu.Id), "The generated switch could not re-enable a sensor.");
        }
        finally { osdSettings.Close(); recordingSettings.Close(); }

        var panel = new PluginSensorPanel(runtime, "cpu"); Check(panel.Children.Count == 1, "Initial overview plugin reading missing.");
        runtime.Settings.OverviewLayout.DisabledPluginSensors.Add(cpu.Id); panel.Refresh();
        Check(panel.Children.Count == 0 && panel.Visibility == Visibility.Collapsed, "Overview did not withdraw an unchecked sensor.");
        Check(PluginSensorSelection.Enabled(runtime, PluginSensorSurface.Osd, cpu.Id) && PluginSensorSelection.Enabled(runtime, PluginSensorSurface.Recording, cpu.Id),
            "Overview selection changed other surfaces.");
        var ownPage = new PluginSensorPanel(runtime, pluginId: manifest.Id);
        Check(Descendants(ownPage).OfType<TextBlock>().Any(t => t.Text == cpu.Name.Chinese), "Overview selection incorrectly changed the plugin's own page.");

        runtime.Settings.Osd.Sensors = []; runtime.Settings.Osd.Orientation = OsdOrientation.Vertical;
        var osd = new ToolkitOsdWindow(runtime);
        try
        {
            osd.ApplySettings(); osd.RefreshForTesting();
            Check(Texts(osd.Content).Contains(cpu.Name.Chinese), "Overview-only disable removed the OSD sensor.");
            runtime.Settings.Osd.DisabledPluginSensors.Add(cpu.Id);
            runtime.Settings.Osd.DisabledPluginSensors.Add(custom.Id);
            osd.ApplySettings(); osd.RefreshForTesting();
            Check(!Texts(osd.Content).Contains(cpu.Name.Chinese) && !Texts(osd.Content).Contains("测试分类") && Texts(osd.Content).Contains(fan.Name.Chinese),
                "OSD selections or empty custom-category hiding failed.");
        }
        finally { osd.Close(); }

        runtime.Settings.SensorRecording.DisabledPluginSensors.Add(fan.Id);
        var sample = new SensorRecordingSample(DateTimeOffset.UtcNow, new Dictionary<string, double?> { ["fan1Rpm"] = 1 });
        var merged = SensorRecordingService.MergePluginSample(sample, PluginSensorSelection.RecordingSensors(runtime));
        Check(!merged.Values.ContainsKey("plugin:" + fan.Id) && merged.Values.ContainsKey("plugin:" + cpu.Id) && merged.Values.ContainsKey("plugin:" + custom.Id) &&
            merged.Values["fan1Rpm"] == 3000, "Recording selection removed the wrong values or broke replacement behavior.");
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(runtime.Settings))!;
        Check(OverviewLayoutDefaults.Normalize(roundTrip.OverviewLayout).DisabledPluginSensors.SequenceEqual([cpu.Id]) &&
            CurveProfileStore.NormalizeOsdSettings(roundTrip.Osd).DisabledPluginSensors.SequenceEqual([cpu.Id, custom.Id]) &&
            CurveProfileStore.NormalizeSensorRecordingSettings(roundTrip.SensorRecording).DisabledPluginSensors.SequenceEqual([fan.Id]),
            "Per-surface plugin choices were lost during save/normalization.");
        var clone = OverviewLayoutDefaults.Clone(runtime.Settings.OverviewLayout); clone.DisabledPluginSensors.Clear();
        Check(runtime.Settings.OverviewLayout.DisabledPluginSensors.Contains(cpu.Id), "Cancelling an overview draft can change live preferences.");
        runtime.Settings.OverviewLayout.DisabledPluginSensors.Clear();
        foreach (var key in runtime.Settings.OverviewLayout.Cards["cpu"].Items.Keys.ToArray()) runtime.Settings.OverviewLayout.Cards["cpu"].Items[key] = false;
        runtime.Settings.OverviewLayout = OverviewLayoutDefaults.Normalize(runtime.Settings.OverviewLayout);
        using var overview = new ToolkitOverviewPage(runtime);
        Check(Descendants(overview).OfType<Border>().Any(b => b.Tag as string == "cpu"), "Selected plugin-only content cannot keep its native card visible.");
        runtime.Settings.OverviewLayout.DisabledPluginSensors.Add(custom.Id);
        var customPanel = new PluginSensorPanel(runtime, category: "test.selection.group");
        Check(customPanel.Children.Count == 0, "Custom overview categories ignored selection.");
        Check(new AppSettings().Osd.DisabledPluginSensors.Count == 0 && new AppSettings().SensorRecording.DisabledPluginSensors.Count == 0,
            "Legacy/default settings must keep plugin readings enabled.");
        Console.WriteLine("Plugin sensor selection editors, isolation, persistence and filtering tests passed.");
    }
    private static string[] Texts(object root) => Descendants((DependencyObject)root).OfType<TextBlock>().Select(t => t.Text).ToArray();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
