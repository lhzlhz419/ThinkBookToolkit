using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed record RecordedPluginSensor(string PluginId, string Version, PluginSensor Sensor);
internal sealed record PluginRecordingMetadata(string Kind, IReadOnlyDictionary<string, RecordedPluginSensor> Sensors, PluginSensorCategory[] Categories)
{
    internal static PluginRecordingMetadata Capture(IReadOnlyList<PluginInstallation> plugins, IReadOnlyList<string> keys)
    {
        var sensors = new Dictionary<string, RecordedPluginSensor>();
        foreach (var plugin in plugins.OrderBy(p => p.Manifest.Id))
            foreach (var sensor in plugin.Manifest.Sensors)
            {
                var key = sensor.Replaces is { } target ? target["toolkit.sensor.".Length..] : "plugin:" + sensor.Id;
                if (!keys.Contains(key) || sensor.Replaces is not null && (!plugin.Enabled || plugin.Error is not null)) continue;
                sensors[key] = new(plugin.Manifest.Id, plugin.Manifest.Version, sensor);
            }
        var categories = plugins.SelectMany(p => p.Manifest.SensorCategories).Where(c => sensors.Values.Any(s => s.Sensor.Category == c.Id)).OrderBy(c => c.Id).ToArray();
        return new("toolkit.plugin-metadata.v1", sensors, categories);
    }
    internal static PluginRecordingMetadata? Parse(string line)
    {
        if (!line.StartsWith('{') || line.Length > 1024 * 1024) return null;
        try
        {
            var value = JsonSerializer.Deserialize<PluginRecordingMetadata>(line);
            return value is { Kind: "toolkit.plugin-metadata.v1", Sensors: not null, Categories: not null } &&
                value.Sensors.Count <= 4096 && value.Categories.Length <= 1024 && value.Sensors.All(s => s.Value?.Sensor is { } sensor &&
                    sensor.Name is not null && sensor.Name.Chinese?.Length <= 160 && sensor.Name.English?.Length <= 160 &&
                    sensor.Unit?.Length <= 32 && PluginSensorChartPolicy.ValidBounds(sensor.ChartMinimum, sensor.ChartMaximum)) &&
                value.Categories.All(c => c is not null && c.Title is not null && c.Id?.Length <= 180) ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
