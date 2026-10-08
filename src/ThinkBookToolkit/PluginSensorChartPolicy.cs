using System.Collections.Generic;
using System.Linq;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal static class PluginSensorChartPolicy
{
    internal static bool HasBounds(PluginSensor sensor) => sensor.ChartMinimum.HasValue || sensor.ChartMaximum.HasValue;
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
