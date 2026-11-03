using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal static class PluginHostBridge
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { Converters = { new FiniteDoubleConverter() } };
    internal static string Unit(string key) => key switch
    {
        "fps" or "fps1Low" => "FPS", "latencyMs" => "ms", "batteryCapacityWh" => "Wh",
        _ when key.EndsWith("Rpm", StringComparison.Ordinal) => "RPM",
        _ when key.EndsWith("Mhz", StringComparison.Ordinal) => "MHz",
        _ when key.EndsWith("Gb", StringComparison.Ordinal) => "GB",
        _ when key.EndsWith("C", StringComparison.Ordinal) => "°C",
        _ when key.EndsWith("W", StringComparison.Ordinal) => "W",
        _ when key.Contains("Utilization", StringComparison.OrdinalIgnoreCase) => "%",
        _ => ""
    };
    private static readonly MethodInfo[] Commands = PluginCommandCatalog.Methods;
    internal static PluginContext Context(ToolkitRuntimeService runtime, string[] permissions, string[]? subscriptions = null)
    {
        var now = DateTimeOffset.UtcNow;
        var sensors = new Dictionary<string, SensorReading>();
        if (permissions.Contains("sensors.read") || permissions.Contains("data.read"))
        {
            var raw = SensorRecordingService.BuildSample(runtime.Snapshot, runtime.CurrentFps, Enum.GetValues<OsdSensor>());
            foreach (var pair in raw.Values)
            {
                if (pair.Key == "fan2Rpm" && !DeviceModelDetector.HasSecondFan()) continue;
                var timestamp = pair.Key.StartsWith("fan", StringComparison.Ordinal) ? runtime.Snapshot.Fans?.Timestamp ?? DateTimeOffset.MinValue : runtime.Snapshot.UpdatedAt;
                var value = pair.Value is { } number && double.IsFinite(number) ? pair.Value : null;
                var quality = value is null ? "unavailable" : now - timestamp > TimeSpan.FromSeconds(10) ? "stale" : "valid";
                sensors["toolkit.sensor." + pair.Key] = new("toolkit.sensor." + pair.Key, value,
                    Unit(pair.Key), timestamp, "toolkit", quality);
            }
        }
        var data = new Dictionary<string, object?>();
        bool Wants(string key) => permissions.Contains("data.read") && (subscriptions is null || subscriptions.Contains(key));
        if (Wants("Snapshot")) data["Snapshot"] = runtime.Snapshot;
        if (Wants("Features")) data["Features"] = runtime.Report?.Items;
        if (Wants("Fps")) data["Fps"] = runtime.CurrentFps;
        if (Wants("Profiles")) data["Profiles"] = CurveProfileStore.Load();
        if (Wants("Paths")) data["Paths"] = ToolkitStoragePaths.Current;
        if (Wants("Recording")) data["Recording"] = runtime.CurrentBufferedSensorRecordingSamples;
        return new(now, sensors, JsonSerializer.SerializeToElement(data, SnapshotJson),
            permissions.Contains("settings.read") ? JsonSerializer.SerializeToElement(runtime.Settings, SnapshotJson) : JsonSerializer.SerializeToElement(new { }),
            Commands.Select(m => new HostOperation(m.Name, m.GetParameters().Where(p => !p.IsOut)
                .ToDictionary(p => p.Name!, p => p.ParameterType.Name))
                { OptionalParameters = m.GetParameters().Where(p => p.HasDefaultValue).Select(p => p.Name!).ToArray() })
                .Concat(PluginCommandCatalog.Aliases.Select(a => new HostOperation(a.Key,
                    Commands.First(m => m.Name == a.Value).GetParameters().Where(p => !p.IsOut).ToDictionary(p => p.Name!, p => p.ParameterType.Name))))
                .Concat(new HostOperation[] {
                    new("toolkit.settings.write", new Dictionary<string, string> { ["pluginId"] = "String", ["settingId"] = "String", ["value"] = "JsonElement", ["expectedValue"] = "JsonElement" }) { Permission = "settings.write", OptionalParameters = ["expectedValue"] },
                    new("toolkit.settings.writeBatch", new Dictionary<string, string> { ["changes"] = "PluginSettingWrite[]" }) { Permission = "settings.write" },
                    new("toolkit.recording.query", new Dictionary<string, string> { ["count"] = "Int32" }) { Permission = "data.read", OptionalParameters = ["count"] }
                })
                .ToArray()) { Capabilities = PluginExtensionPolicy.Capabilities };
    }
    internal static async Task ExecuteAsync(ToolkitRuntimeService runtime, string[] permissions, HostCommand command)
        => _ = await ExecuteCoreAsync(runtime, permissions, command);

    internal static async Task<HostCommandResult> ExecuteWithResultAsync(ToolkitRuntimeService runtime, string[] permissions,
        HostCommand command, string source, CancellationToken cancellationToken = default)
    {
        var id = command.RequestId ?? Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = ExecuteCoreAsync(runtime, permissions, command);
            _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            ToolkitLog.Info($"Plugin command completed: plugin={source}; request={id}; command={command.Id}.");
            return new(id, command.Id, "completed", Result: result is null ? null : JsonSerializer.SerializeToElement(result, SnapshotJson));
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning($"Plugin command failed: plugin={source}; request={id}; command={command.Id}; {ex.GetBaseException().Message}");
            return new(id, command.Id, ex is TimeoutException ? "timed-out" : ex is OperationCanceledException ? "cancelled" : ex is UnauthorizedAccessException ? "denied" : "failed",
                ex is TimeoutException or OperationCanceledException ? "Waiting stopped. An already-started hardware operation may still complete; do not automatically replay it." : ex.GetBaseException().Message);
        }
    }

    private static async Task<object?> ExecuteCoreAsync(ToolkitRuntimeService runtime, string[] permissions, HostCommand command)
    {
        if (runtime.Plugins.IsSuspendedForExit) throw new InvalidOperationException("Toolkit is exiting; plugin commands are suspended.");
        if (!permissions.Contains("host.control")) throw new UnauthorizedAccessException("Plugin has no host.control permission.");
        var methodId = PluginCommandCatalog.Aliases.TryGetValue(command.Id, out var alias) ? alias : command.Id;
        var method = Commands.FirstOrDefault(m => m.Name == methodId &&
            m.GetParameters().Where(p => !p.IsOut && !p.HasDefaultValue).All(p => command.Arguments.ContainsKey(p.Name!)) &&
            command.Arguments.Keys.All(key => m.GetParameters().Any(p => !p.IsOut && p.Name == key)))
            ?? throw new InvalidOperationException("Unknown host operation or arguments: " + command.Id);
        var parameters = method.GetParameters();
        var arguments = parameters.Select(p => p.IsOut ? null : command.Arguments.TryGetValue(p.Name!, out var value)
            ? JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
            : p.DefaultValue).ToArray();
        object? result;
        try { result = method.Invoke(runtime, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        if (result is Task task)
        {
            await task;
            result = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        if (result is string error && !string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
        if (result is false) throw new InvalidOperationException(arguments.LastOrDefault() as string ?? "Host operation rejected.");
        return result;
    }
    internal static string? MetricKey(OsdSensor sensor) => SensorRecordingService.BuildSample(
        ToolkitRuntimeSnapshot.Empty, FpsTelemetrySnapshot.Empty, [sensor]).Values.Keys.FirstOrDefault();
    private sealed class FiniteDoubleConverter : System.Text.Json.Serialization.JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        { if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue(); }
    }
}
