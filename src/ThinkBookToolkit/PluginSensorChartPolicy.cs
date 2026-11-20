using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal static class PluginSensorChartPolicy
{
    internal static bool HasBounds(PluginSensor sensor) => sensor.ChartMinimum.HasValue || sensor.ChartMaximum.HasValue;
    internal static bool ValidRecordingCharts(IEnumerable<PluginSensor> sensors)
    {
        var members = sensors.Where(sensor => sensor.RecordingChart is not null).ToArray();
        if (members.Any(sensor => sensor.RecordingChart is not { Id: { Length: > 0 and <= 100 } id,
                Title: { Chinese.Length: > 0 and <= 160, English.Length: > 0 and <= 160 } } ||
                !Regex.IsMatch(id, @"\A[a-zA-Z0-9._-]+\z"))) return false;
        return members.GroupBy(sensor => sensor.RecordingChart!.Id).All(group =>
            group.Select(sensor => sensor.RecordingChart!.Title).Distinct().Count() == 1);
    }
    internal static bool ValidBounds(double? minimum, double? maximum) =>
        (!minimum.HasValue || double.IsFinite(minimum.Value)) && (!maximum.HasValue || double.IsFinite(maximum.Value)) &&
        (!minimum.HasValue || !maximum.HasValue || minimum.Value < maximum.Value) &&
        (maximum.HasValue || minimum != double.MaxValue) && (minimum.HasValue || maximum != double.MinValue);

    internal static PluginSensor? Replacement(string key, IReadOnlyList<PluginInstallation> plugins)
    {
        var candidates = plugins.SelectMany(p => p.Manifest.Sensors.Where(s => s.Replaces == "toolkit.sensor." + key)
            .Select(s => (Plugin: p, Sensor: s))).ToArray();
        var active = candidates.Where(c => c.Plugin.Enabled && c.Plugin.Error is null).ToArray();
        // Recordings currently store metric IDs, not replacement-provider IDs.
        // Prefer the active owner, or the sole installed descriptor; never guess
        // between several disabled providers when looking at historical data.
        return active.Length == 1 ? active[0].Sensor : candidates.Length == 1 ? candidates[0].Sensor : null;
    }
}
