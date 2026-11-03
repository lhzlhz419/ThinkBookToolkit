using System.Text.Json;

namespace ThinkBookToolkit.PluginApi;

public sealed record HostCommandResult(string RequestId, string CommandId, string Status,
    string? Error = null, JsonElement? Result = null);
public sealed record PluginEvent(long Sequence, DateTimeOffset Timestamp, string Kind, string Source, JsonElement Data);
public sealed record PluginDependency(string Id, string? MinimumVersion = null, string? MaximumVersionExclusive = null);
public sealed record PluginSettingAccess(string PluginId, string SettingId);
public sealed record PluginSettingWrite(string PluginId, string SettingId, JsonElement Value, JsonElement? ExpectedValue = null);
public sealed record PluginSettingCondition(string SettingId, JsonElement EqualsValue);
public sealed record PluginSettingRename(string From, string To);
public sealed record PluginActionParameter(string Id, PluginText Title, string Kind, JsonElement DefaultValue,
    double? Minimum = null, double? Maximum = null, string[]? Choices = null);
public sealed record PluginAction(string Id, PluginText Title, PluginActionParameter[] Parameters)
{
    public string? PageId { get; init; }
    public string[] QuickAccess { get; init; } = [];
}
public sealed record PluginSignal(string Id, PluginText Title)
{
    public int CooldownSeconds { get; init; } = 5;
    public PluginSensorThreshold? Threshold { get; init; }
}
public sealed record PluginSensorThreshold(string SensorId, string Direction, double Value,
    double Hysteresis = 0, double DurationSeconds = 0);
