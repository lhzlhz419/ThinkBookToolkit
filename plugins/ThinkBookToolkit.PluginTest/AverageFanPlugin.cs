using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.PluginTest;

public sealed class AverageFanPlugin : IToolkitPlugin
{
    public ValueTask<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var enabled = request.PluginSettings.TryGetValue("show-average", out var setting) && setting.ValueKind == System.Text.Json.JsonValueKind.True;
        if (!enabled) return ValueTask.FromResult(new PluginResult(new Dictionary<string, double?>(), []));
        var speeds = request.Context.Sensors.Values.Where(sensor =>
            sensor.Id is "toolkit.sensor.fan1Rpm" or "toolkit.sensor.fan2Rpm" &&
            sensor.Quality == "valid" && sensor.Value is >= 0 && double.IsFinite(sensor.Value.Value))
            .Select(sensor => sensor.Value!.Value).ToArray();
        double? average = speeds.Length == 0 ? null : speeds.Average();
        return ValueTask.FromResult(new PluginResult(new Dictionary<string, double?> { ["toolkit.plugin-test.average-rpm"] = average },
            ["toolkit.plugin-test.average-rpm"]));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
