using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal enum PluginSensorSurface { Overview, Osd, Recording }

internal static class PluginSensorSelection
{
    internal static List<string> Normalize(IEnumerable<string>? ids) => (ids ?? []).Where(id =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 180).Distinct(StringComparer.Ordinal).ToList();
    internal static bool Enabled(IEnumerable<string>? disabled, string id) => disabled?.Contains(id, StringComparer.Ordinal) != true;
    internal static void SetEnabled(List<string> disabled, string id, bool enabled)
    {
        disabled.RemoveAll(value => value == id);
        if (!enabled) disabled.Add(id);
    }
    internal static bool Enabled(ToolkitRuntimeService runtime, PluginSensorSurface surface, string id) => Enabled(surface switch
    {
        PluginSensorSurface.Overview => runtime.Settings.OverviewLayout.DisabledPluginSensors,
        PluginSensorSurface.Osd => runtime.Settings.Osd.DisabledPluginSensors,
        _ => runtime.Settings.SensorRecording.DisabledPluginSensors
    }, id);

    private static IEnumerable<(PluginInstallation Plugin, PluginSensor Sensor)> Catalog(ToolkitRuntimeService runtime) =>
        runtime.Plugins.Installations.Where(p => !p.PendingUninstall).SelectMany(p =>
            p.Manifest.Sensors.Where(s => s.Replaces is null).Select(s => (Plugin: p, Sensor: s)));
    private static string? Group(PluginInstallation plugin, PluginSensor sensor, PluginSensorSurface surface) =>
        (surface == PluginSensorSurface.Overview ? PluginSensorPlacement.OverviewCard(sensor.Category) : PluginSensorPlacement.OsdGroup(sensor.Category)) ??
        (plugin.Manifest.SensorCategories.Any(c => c.Id == sensor.Category) ? sensor.Category :
            surface == PluginSensorSurface.Recording ? "other" : null);
    internal static (PluginInstallation Plugin, PluginSensor Sensor)[] Options(ToolkitRuntimeService runtime, PluginSensorSurface surface, string group) =>
        Catalog(runtime).Where(x => Group(x.Plugin, x.Sensor, surface) == group).OrderBy(x => x.Sensor.Id, StringComparer.Ordinal).ToArray();

    internal static IEnumerable<OsdSensorCatalog.Group> Groups(ToolkitRuntimeService runtime, PluginSensorSurface surface)
    {
        var standard = surface == PluginSensorSurface.Overview
            ? OverviewLayoutDefaults.CardDefinitions.Keys.Select(id => new OsdSensorCatalog.Group(id, id, id, [])).ToArray()
            : OsdSensorCatalog.Groups.ToArray();
        foreach (var group in standard) yield return group;
        var known = standard.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Catalog(runtime))
        {
            var id = Group(item.Plugin, item.Sensor, surface);
            if (id is null || !known.Add(id)) continue;
            var category = item.Plugin.Manifest.SensorCategories.FirstOrDefault(c => c.Id == id);
            yield return new(id, category?.Title.Chinese ?? "其他传感器", category?.Title.English ?? "Other sensors", []);
        }
    }

    internal static void AddToggles(Panel panel, ToolkitRuntimeService runtime, PluginSensorSurface surface, string group,
        Func<List<string>> disabled, Action<string, bool> change)
    {
        foreach (var (plugin, sensor) in Options(runtime, surface, group))
        {
            var toggle = new CheckBox
            {
                Content = sensor.Name.Resolve(runtime.IsChinese), Tag = "plugin:" + sensor.Id,
                IsChecked = Enabled(disabled(), sensor.Id), Margin = new Thickness(0, 5, 0, 5),
                ToolTip = plugin.Manifest.Name + " · " + sensor.Id + (plugin.Enabled ? "" : runtime.L("（插件未启用）", " (plugin disabled)"))
            };
            toggle.Click += (_, _) => { change(sensor.Id, toggle.IsChecked == true); toggle.IsChecked = Enabled(disabled(), sensor.Id); };
            panel.Children.Add(toggle);
        }
    }
    internal static bool HasOverviewReading(ToolkitRuntimeService runtime, string card, OverviewLayoutSettings layout) =>
        Options(runtime, PluginSensorSurface.Overview, card).Any(x => x.Plugin.Enabled && x.Plugin.Error is null && Enabled(layout.DisabledPluginSensors, x.Sensor.Id));

    internal static IReadOnlyList<PublishedPluginSensor> RecordingSensors(ToolkitRuntimeService runtime) =>
        runtime.Plugins.Sensors.Where(s => s.Replaces is not null || Enabled(runtime, PluginSensorSurface.Recording, s.Id)).ToArray();
}
