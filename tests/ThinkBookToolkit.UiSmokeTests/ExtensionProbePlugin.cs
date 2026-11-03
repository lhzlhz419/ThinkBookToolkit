using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

// Loaded only by the isolated integration-test worker.
public sealed class ExtensionProbePlugin : IToolkitPlugin
{
    private bool _sent;
    public async ValueTask<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var id = request.PluginSettings["id"].GetString()!;
        var delay = request.PluginSettings.GetValueOrDefault("delay");
        if (delay.ValueKind == JsonValueKind.Number && delay.GetInt32() > 0)
            await Task.Delay(delay.GetInt32(), cancellationToken);
        var mode = request.PluginSettings.GetValueOrDefault("behavior").ToString();
        if (mode == "crash") Environment.Exit(123);
        if (mode == "throw") throw new InvalidOperationException("probe logic failure");
        var values = new Dictionary<string, double?>
        {
            [id + ".value"] = request.Operation == "action" ? request.Argument!.Value.GetProperty("value").GetDouble() : 42,
            [id + ".events"] = request.Events.Length,
            [id + ".receipts"] = request.CommandResults.Length
        };
        HostCommand[]? commands = null;
        if (mode == "commands" && !_sent)
        {
            _sent = true;
            commands = [new("TrySetLogLevel", new Dictionary<string, JsonElement> { ["level"] = JsonSerializer.SerializeToElement("invalid") }) { RequestId = "invalid-setting" },
                new("SetStatus", new Dictionary<string, JsonElement> { ["message"] = JsonSerializer.SerializeToElement("probe success") }) { RequestId = "valid-command" }];
        }
        if (request.Operation == "action" && request.Argument!.Value.GetProperty("value").GetDouble() == 99)
            commands = [new("test.unknown", new Dictionary<string, JsonElement>())];
        return new(values, Commands: commands)
        {
            ConditionValues = new Dictionary<string, bool> { [id + ".ready"] = true },
            TriggerSignals = mode == "signal" ? [id + ".signal"] : null
        };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
