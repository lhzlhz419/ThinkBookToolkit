using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
    private static readonly MethodInfo[] Commands = typeof(ToolkitRuntimeService)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(m => (m.IsPublic || m.IsAssembly) && !m.IsGenericMethod &&
            (m.Name.StartsWith("Set", StringComparison.Ordinal) || m.Name.StartsWith("TrySet", StringComparison.Ordinal) || m.Name.StartsWith("Apply", StringComparison.Ordinal)) &&
            !m.Name.Contains("ForTesting", StringComparison.Ordinal) &&
            m.GetParameters().All(p => p.IsOut || (!typeof(Delegate).IsAssignableFrom(p.ParameterType) &&
                !(p.ParameterType.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) ?? false))))
        .ToArray();
    internal static PluginContext Context(ToolkitRuntimeService runtime, string[] permissions)
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
        return new(now, sensors,
            permissions.Contains("data.read") ? JsonSerializer.SerializeToElement(new
            {
                runtime.Snapshot, Features = runtime.Report?.Items, Fps = runtime.CurrentFps,
                Profiles = CurveProfileStore.Load(), Paths = ToolkitStoragePaths.Current,
                Recording = runtime.CurrentBufferedSensorRecordingSamples
            }, SnapshotJson) : JsonSerializer.SerializeToElement(new { }),
            permissions.Contains("settings.read") ? JsonSerializer.SerializeToElement(runtime.Settings, SnapshotJson) : JsonSerializer.SerializeToElement(new { }),
            Commands.Select(m => new HostOperation(m.Name, m.GetParameters().Where(p => !p.IsOut)
                .ToDictionary(p => p.Name!, p => p.ParameterType.Name))).ToArray());
    }
    internal static async Task ExecuteAsync(ToolkitRuntimeService runtime, string[] permissions, HostCommand command)
    {
        if (runtime.Plugins.IsSuspendedForExit) throw new InvalidOperationException("Toolkit is exiting; plugin commands are suspended.");
        if (!permissions.Contains("host.control")) throw new UnauthorizedAccessException("Plugin has no host.control permission.");
        var method = Commands.FirstOrDefault(m => m.Name == command.Id &&
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
