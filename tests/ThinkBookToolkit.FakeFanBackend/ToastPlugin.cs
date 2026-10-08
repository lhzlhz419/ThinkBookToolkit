using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.Tests;

public sealed class ToastPlugin : IToolkitPlugin
{
    private bool _sent;
    public ValueTask<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var result = new PluginResult(new Dictionary<string, double?>()) { Toast = _sent ? null : new("Worker toast", true) };
        _sent = true; return ValueTask.FromResult(result);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
