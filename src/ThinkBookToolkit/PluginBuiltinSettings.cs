using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal static class PluginBuiltinSettings
{
    internal static bool ValidExternalValue(string id, JsonElement value) => id switch
    {
        "toolkit.setting.IntervalSeconds" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds is >= 0.5 and <= 3600,
        "toolkit.setting.LogRetentionDays" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var days) && FileRetentionPolicy.Days.Contains(days),
        "toolkit.setting.Language" => value.ValueKind == JsonValueKind.String && value.GetString() is "zh-CN" or "en-US",
        "toolkit.setting.Theme" => value.ValueKind == JsonValueKind.String && value.GetString() is "light" or "dark" or "system",
        "toolkit.setting.LogLevel" => value.ValueKind == JsonValueKind.String && value.GetString() is "INFO" or "WARN" or "ERROR" or "NONE",
        "toolkit.setting.OverviewPageMode" => value.ValueKind == JsonValueKind.String && Enum.TryParse<OverviewPageMode>(value.GetString(), out var mode) && Enum.IsDefined(mode),
        _ => false
    };
    internal static string? Kind(string id) => id switch
    {
        "toolkit.setting.IntervalSeconds" or "toolkit.setting.LogRetentionDays" => "number",
        "toolkit.setting.Language" or "toolkit.setting.Theme" or "toolkit.setting.LogLevel" or "toolkit.setting.OverviewPageMode" => "choice",
        _ => null
    };
    internal static JsonElement Read(ToolkitRuntimeService runtime, string id) => JsonSerializer.SerializeToElement<object>(id switch
    {
        "toolkit.setting.IntervalSeconds" => runtime.Settings.IntervalSeconds,
        "toolkit.setting.LogRetentionDays" => runtime.Settings.LogRetentionDays,
        "toolkit.setting.Language" => runtime.Settings.Language,
        "toolkit.setting.Theme" => runtime.Settings.Theme,
        "toolkit.setting.LogLevel" => runtime.Settings.LogLevel,
        "toolkit.setting.OverviewPageMode" => runtime.Settings.OverviewPageMode.ToString(),
        _ => throw new ArgumentException("Unknown setting slot.")
    });
    internal static Task ApplyAsync(ToolkitRuntimeService runtime, string id, JsonElement value)
    {
        string? error;
        var success = id switch
        {
            "toolkit.setting.IntervalSeconds" => runtime.TrySetRefreshInterval(value.GetDouble(), out error),
            "toolkit.setting.Language" => runtime.TrySetLanguage(value.GetString()!, out error),
            "toolkit.setting.Theme" => runtime.TrySetTheme(value.GetString()!, out error),
            "toolkit.setting.OverviewPageMode" => runtime.TrySetOverviewPageMode(Enum.Parse<OverviewPageMode>(value.GetString()!), out error),
            "toolkit.setting.LogLevel" => runtime.TrySetLogLevel(value.GetString()!, out error),
            "toolkit.setting.LogRetentionDays" => runtime.TrySetLogRetentionDays(value.GetInt32(), out error),
            _ => throw new ArgumentException("Unknown setting slot.")
        };
        if (!success) throw new InvalidOperationException(error);
        return Task.CompletedTask;
    }
}
