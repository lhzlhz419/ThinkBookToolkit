using System;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed class PluginThresholdState
{
    private DateTimeOffset? _since;
    private DateTimeOffset? _lastUpdate;
    internal bool Active { get; private set; }
    internal bool Update(PluginSensorThreshold threshold, double? value, DateTimeOffset now)
    {
        var wasActive = Active;
        if (_lastUpdate is { } last && (now < last || now - last > TimeSpan.FromSeconds(10))) { _since = null; Active = false; }
        _lastUpdate = now;
        if (value is null || !double.IsFinite(value.Value)) { _since = null; Active = false; return false; }
        var above = threshold.Direction == "above";
        if (Active)
        {
            if (above ? value < threshold.Value - threshold.Hysteresis : value > threshold.Value + threshold.Hysteresis)
            { Active = false; _since = null; }
        }
        else if (above ? value >= threshold.Value : value <= threshold.Value)
        {
            _since ??= now;
            if (now - _since >= TimeSpan.FromSeconds(threshold.DurationSeconds)) Active = true;
        }
        else _since = null;
        return !wasActive && Active;
    }
}
