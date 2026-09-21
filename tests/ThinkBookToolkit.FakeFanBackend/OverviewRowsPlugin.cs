using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.Tests;

public sealed class OverviewRowsPlugin : IToolkitPlugin
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, double?>
        {
            ["test.overview.sensor"] = 3210, ["test.overview.cpu"] = 45, ["test.overview.sensor-gpu"] = 55,
            ["test.overview.vram"] = 60, ["test.overview.battery"] = 40, ["test.overview.memory"] = 35,
            ["test.overview.storage"] = 38, ["test.overview.sensor-power"] = 120, ["test.overview.warranty"] = 365,
            ["test.overview.ambient"] = 25
        };
        return ValueTask.FromResult(new PluginResult(values,
            request.PluginSettings["text"].GetString() == "hide-category" ? values.Keys.Where(id => id != "test.overview.ambient").ToArray() : null)
        {
            OverviewValues = new Dictionary<string, string?> { ["test.overview.dynamic"] = request.PluginSettings["text"].GetString() }
        });
    }
}
