using System;
using System.IO;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal static class PluginExtensionPolicy
{
    internal static readonly string[] Capabilities = ["commands.results", "events.v1", "scheduling.v1", "automation.v1", "settings.external", "ui.editors", "packages.v1"];
    internal static readonly string[] Events = ["plugin.enabled", "plugin.disabled", "host.started", "host.suspend", "host.resume", "host.exiting", "session.locked", "session.unlocked", "power.connected", "power.disconnected", "game.started", "game.stopped", "performance.changed", "gpu.changed", "settings.changed", "state.resync"];

    internal static void Validate(PluginManifest manifest)
    {
        void Require([DoesNotReturnIf(false)] bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        bool Own(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 180 && id.StartsWith(manifest.Id + ".", StringComparison.Ordinal);
        Require(manifest.RequiredCapabilities is not null && !manifest.RequiredCapabilities.Except(Capabilities).Any(), "Toolkit does not support a required plugin capability.");
        Require(manifest.MinimumHostVersion is null || Version.TryParse(manifest.MinimumHostVersion, out var minimum) && Version.TryParse(ApplicationUpdateService.CurrentVersionText, out var current) && current >= minimum, "This plugin requires a newer Toolkit version.");
        Require(manifest.RefreshIntervalMilliseconds is >= 250 and <= 3600000 && manifest.TransientRetryLimit is >= 0 and <= 3, "Invalid plugin scheduling limits.");
        Require(manifest.EventSubscriptions is not null && manifest.EventSubscriptions.Length <= 32 && !manifest.EventSubscriptions.Except(Events).Any(), "Unknown plugin event subscription.");
        Require(manifest.DataSubscriptions is null || !manifest.DataSubscriptions.Except(new[] { "Snapshot", "Features", "Fps", "Profiles", "Paths", "Recording" }).Any(), "Unknown plugin data subscription.");
        Require(manifest.Dependencies is not null && manifest.Dependencies.Length <= 16 && manifest.Dependencies.All(d => d.Id != manifest.Id && !string.IsNullOrWhiteSpace(d.Id) && (d.MinimumVersion is null || Version.TryParse(d.MinimumVersion, out _)) && (d.MaximumVersionExclusive is null || Version.TryParse(d.MaximumVersionExclusive, out _))), "Invalid plugin dependencies.");
        Require(manifest.Dependencies.Select(d => d.Id).Distinct().Count() == manifest.Dependencies.Length, "Duplicate plugin dependency.");
        Require(manifest.WritableSettings is not null && manifest.WritableSettings.Length <= 64 && manifest.WritableSettings.All(s => !string.IsNullOrWhiteSpace(s.PluginId) && !string.IsNullOrWhiteSpace(s.SettingId)), "Invalid setting access declaration.");
        Require(manifest.WritableSettings.Length == 0 || manifest.Permissions.Contains("settings.write"), "External setting writes require settings.write.");
        Require(manifest.SettingsVersion >= 0 && manifest.SettingRenames is not null && manifest.SettingRenames.Length <= 256 && manifest.SettingRenames.All(r => r.From != r.To && !string.IsNullOrWhiteSpace(r.From) && manifest.Settings.Any(s => s.Id == r.To)), "Invalid settings migration.");
        Require(manifest.SettingRenames.Select(r => r.From).Distinct().Count() == manifest.SettingRenames.Length && manifest.SettingRenames.Select(r => r.To).Distinct().Count() == manifest.SettingRenames.Length, "Ambiguous settings migration.");
        Require(manifest.Actions is not null && manifest.Actions.Length <= 64 && manifest.Triggers is not null && manifest.Triggers.Length <= 64 && manifest.Conditions is not null && manifest.Conditions.Length <= 64, "Too many plugin automation declarations.");
        var ids = manifest.Actions.Select(a => a.Id).Concat(manifest.Triggers.Select(t => t.Id)).Concat(manifest.Conditions.Select(c => c.Id)).ToArray();
        Require(ids.All(Own) && ids.Distinct().Count() == ids.Length, "Invalid or duplicate plugin action/signal ID.");
        foreach (var action in manifest.Actions)
        {
            Require(action.Title is not null && action.Title.Chinese.Length <= 160 && action.Title.English.Length <= 160 && action.Parameters is not null && action.Parameters.Length <= 16, "Invalid action metadata.");
            Require(action.Parameters.Select(p => p.Id).Distinct().Count() == action.Parameters.Length, "Duplicate action parameter.");
            Require(action.QuickAccess is not null && !action.QuickAccess.Except(new[] { "overview", "tray" }).Any(), "Unknown quick action location.");
            Require(action.PageId is null || ToolkitPluginManager.BuiltinPages.Contains(action.PageId) ||
                manifest.Pages.Any(p => p.Id == action.PageId && p.View is null), "Action target must be a built-in or declarative plugin page.");
            foreach (var p in action.Parameters)
            {
                var setting = Parameter(p);
                Require(!string.IsNullOrWhiteSpace(p.Id) && p.Id.Length <= 100 && ToolkitPluginManager.ValidSetting(setting, p.DefaultValue), "Invalid action parameter/default.");
            }
        }
        Require(manifest.Triggers.Concat(manifest.Conditions).All(s => s.Title is not null && s.Title.Chinese.Length <= 160 && s.Title.English.Length <= 160 && s.CooldownSeconds is >= 1 and <= 3600), "Invalid signal metadata.");
        foreach (var threshold in manifest.Triggers.Concat(manifest.Conditions).Select(s => s.Threshold).OfType<PluginSensorThreshold>())
        {
            Require(threshold.Direction is "above" or "below" && double.IsFinite(threshold.Value) && double.IsFinite(threshold.Hysteresis) && threshold.Hysteresis >= 0 &&
                double.IsFinite(threshold.DurationSeconds) && threshold.DurationSeconds is >= 0 and <= 3600, "Invalid sensor threshold.");
            Require(manifest.Sensors.Any(s => s.Id == threshold.SensorId) || (manifest.Permissions.Contains("sensors.read") || manifest.Permissions.Contains("data.read")) &&
                threshold.SensorId.StartsWith("toolkit.sensor.", StringComparison.Ordinal) && SensorRecordingFormat.MetricKeys.Contains(threshold.SensorId[15..]), "Threshold sensor is unavailable or unapproved.");
        }
        foreach (var setting in manifest.Settings)
        {
            Require(setting.Editor is null or "slider" or "folder" or "file" or "color" or "multiline", "Unsupported setting editor.");
            Require(setting.Editor != "slider" || setting.Kind == "number" && setting.Minimum.HasValue && setting.Maximum.HasValue, "Slider requires bounded numeric setting.");
            Require(setting.Editor is not ("folder" or "file" or "color" or "multiline") || setting.Kind == "string", "Text editor requires a string setting.");
            foreach (var condition in new[] { setting.VisibleWhen, setting.EnabledWhen }.OfType<PluginSettingCondition>())
                Require(manifest.Settings.Any(s => s.Id == condition.SettingId && ToolkitPluginManager.ValidSetting(s, condition.EqualsValue)), "Unknown setting condition or invalid comparison value.");
        }
    }

    internal static PluginSetting Parameter(PluginActionParameter p) => new(p.Id, "", p.Title, p.Kind, p.DefaultValue,
        Minimum: p.Minimum, Maximum: p.Maximum, Choices: p.Choices);
}
