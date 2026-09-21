using System.Buffers.Binary;
using System.Text.Json;

namespace ThinkBookToolkit.PluginApi;

public static class PluginApiVersion { public const int Current = 1; }

public sealed record PluginText(string Chinese, string English)
{
    public string Resolve(bool chinese) => chinese ? Chinese : English;
}
public sealed record PluginPage(string Id, PluginText Title, string? Replaces = null, int Order = 100);
public sealed record PluginSetting(string Id, string PageId, PluginText Title, string Kind,
    JsonElement DefaultValue, string? Replaces = null, double? Minimum = null, double? Maximum = null,
    string[]? Choices = null);
public sealed record PluginSensor(string Id, PluginText Name, string Unit, string Category = "plugin", string? Replaces = null);
public sealed record PluginSensorCategory(string Id, PluginText Title, int Order = 100);
public sealed record PluginFanBackend(string Assembly, string Type);
/// <summary>Overview-only row contribution. Action is add, replace or remove; Target is a built-in item ID.</summary>
public sealed record PluginOverviewItem(string Id, string CardId, string Action = "add", string? Target = null,
    PluginText? Label = null, PluginText? Text = null, string? SensorId = null, int Order = 100);
public sealed record PluginManifest(string Id, string Name, string Version, int ApiVersion,
    string EntryAssembly, string EntryType, string[] Permissions,
    PluginPage[] Pages, PluginSetting[] Settings, PluginSensor[] Sensors)
{
    // Additive property preserves the v1 constructor and existing plugin binaries.
    public string? Author { get; init; }
    public PluginFanBackend? FanBackend { get; init; }
    public PluginOverviewItem[] OverviewItems { get; init; } = [];
    public PluginSensorCategory[] SensorCategories { get; init; } = [];
}
public sealed record SensorReading(string Id, double? Value, string Unit, DateTimeOffset Timestamp, string Source,
    string Quality = "valid");
public sealed record HostOperation(string Id, IReadOnlyDictionary<string, string> Parameters);
public sealed record PluginContext(DateTimeOffset Timestamp, IReadOnlyDictionary<string, SensorReading> Sensors,
    JsonElement Data, JsonElement Settings, IReadOnlyList<HostOperation> Operations);
public sealed record PluginRequest(string Operation, PluginContext Context,
    IReadOnlyDictionary<string, JsonElement> PluginSettings, string? Action = null, JsonElement? Argument = null);
public sealed record HostCommand(string Id, IReadOnlyDictionary<string, JsonElement> Arguments);
public sealed record PluginResult(IReadOnlyDictionary<string, double?> Values,
    string[]? VisibleSensors = null, HostCommand[]? Commands = null)
{
    public IReadOnlyDictionary<string, string?>? OverviewValues { get; init; }
}
public sealed record PluginResponse(bool Success, PluginResult? Result = null, string? Error = null);

/// <summary>Logic-only contract. No WPF types or main-application assembly references.</summary>
public interface IToolkitPlugin : IAsyncDisposable
{
    ValueTask<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken);
}

public static class PluginWire
{
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Plugin message is too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid plugin message length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty plugin response.");
    }
}
