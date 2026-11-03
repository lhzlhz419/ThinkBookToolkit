using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed partial class ToolkitPluginManager
{
    private long _eventSequence;
    private string? _previousModes;
    private readonly AsyncLocal<bool> _scopedHostSettingWrite = new();
    internal event Action<string>? AutomationSignal;

    private bool Active(PluginInstallation plugin, long generation) => !_disposed && !_exitSuspended &&
        plugin.Generation == generation && plugin.Enabled && !plugin.PendingUninstall && plugin.Error is null && _plugins.Contains(plugin);

    internal async Task RefreshAsync(bool force = true, PluginInstallation? only = null)
    {
        if (_disposed || _exitSuspended) return;
        ObserveState();
        var contexts = new Dictionary<string, PluginContext>();
        var tasks = new List<Task>();
        foreach (var plugin in _plugins.Where(p => (only is null || p == only) && p.Enabled && !p.PendingUninstall && p.Error is null).ToArray())
        {
            if (plugin.RefreshTask is { IsCompleted: false } running)
            {
                if (force) tasks.Add(RefreshAfterAsync(plugin, running));
                continue;
            }
            if (DateTimeOffset.UtcNow < plugin.NextRefresh && (!force || plugin.ConsecutiveFailures > 0)) continue;
            var key = string.Join('|', plugin.Manifest.Permissions.Order()) + ":" +
                (plugin.Manifest.DataSubscriptions is null ? "legacy" : string.Join('|', plugin.Manifest.DataSubscriptions.Order()));
            try
            {
                if (!contexts.TryGetValue(key, out var context))
                    contexts[key] = context = PluginHostBridge.Context(_runtime, plugin.Manifest.Permissions, plugin.Manifest.DataSubscriptions);
                plugin.RefreshTask = RefreshPluginAsync(plugin, context);
                tasks.Add(plugin.RefreshTask);
            }
            catch (Exception ex) { ReportUiFailure(plugin, ex); }
        }
        await Task.WhenAll(tasks);
    }

    private async Task RefreshAfterAsync(PluginInstallation plugin, Task running)
    {
        var generation = plugin.Generation;
        await running;
        if (Active(plugin, generation) && plugin.ConsecutiveFailures == 0)
        {
            var context = PluginHostBridge.Context(_runtime, plugin.Manifest.Permissions, plugin.Manifest.DataSubscriptions);
            plugin.RefreshTask = RefreshPluginAsync(plugin, context);
            await plugin.RefreshTask;
        }
    }

    private async Task RefreshPluginAsync(PluginInstallation plugin, PluginContext context)
    {
        var generation = plugin.Generation;
        var timer = Stopwatch.StartNew();
        await plugin.IoGate.WaitAsync();
        await _evaluationSlots.WaitAsync();
        var holdsSlot = true;
        try
        {
            if (!Active(plugin, generation)) return;
            ValidateDependencies(plugin);
            var request = Request(plugin, "refresh", context);
            var client = await GetVerifiedClientAsync(plugin, generation, _stop.Token);
            var result = await client.EvaluateAsync(request, _stop.Token);
            _evaluationSlots.Release(); holdsSlot = false;
            if (!Active(plugin, generation)) return;
            var acknowledged = request.Events.LastOrDefault()?.Sequence ?? 0;
            while (plugin.Events.TryPeek(out var pendingEvent) && pendingEvent.Sequence <= acknowledged) plugin.Events.Dequeue();
            foreach (var _ in request.CommandResults) if (plugin.Results.Count > 0) plugin.Results.Dequeue();
            await AcceptResultAsync(plugin, result, context.Timestamp, generation);
            plugin.ConsecutiveFailures = 0;
            plugin.NextRefresh = DateTimeOffset.UtcNow.AddMilliseconds(plugin.Manifest.RefreshIntervalMilliseconds);
        }
        catch (Exception ex)
        {
            if (!Active(plugin, generation)) return;
            plugin.LastFailure = ex.GetBaseException().Message;
            plugin.Values = []; plugin.Conditions.Clear(); plugin.Thresholds.Clear(); plugin.OverviewValues = new Dictionary<string, string?>();
            plugin.Client?.Dispose(); plugin.Client = null;
            if (ex is IOException or TimeoutException || ex is OperationCanceledException && !_stop.IsCancellationRequested)
            {
                plugin.ConsecutiveFailures++;
                if (plugin.ConsecutiveFailures <= plugin.Manifest.TransientRetryLimit)
                {
                    plugin.RestartCount++;
                    plugin.NextRefresh = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, plugin.ConsecutiveFailures)));
                    ToolkitLog.Warning($"Plugin retry scheduled: {plugin.Manifest.Id}; {plugin.LastFailure}");
                    return;
                }
            }
            plugin.Error = plugin.LastFailure;
            ToolkitLog.Error("Plugin suspended: " + plugin.Manifest.Id, ex);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            plugin.LastDurationMs = timer.Elapsed.TotalMilliseconds;
            if (holdsSlot) _evaluationSlots.Release();
            plugin.IoGate.Release();
            if (!_disposed && plugin.Generation == generation) Publish();
        }
    }

    private async Task<PluginProcessClient> GetVerifiedClientAsync(PluginInstallation plugin, long generation, CancellationToken token)
    {
        if (plugin.Client is not null) return plugin.Client;
        var fingerprint = await Task.Run(() => Fingerprint(plugin.Directory), token);
        if (!Active(plugin, generation)) throw new OperationCanceledException("Plugin session ended.");
        if (fingerprint != plugin.Fingerprint) throw new InvalidDataException("Plugin files changed. Restart Toolkit and review the new package before running it.");
        return plugin.Client = new(plugin.Directory, plugin.Manifest, _options.HostPath);
    }

    private PluginRequest Request(PluginInstallation plugin, string operation, PluginContext? context = null,
        string? action = null, JsonElement? argument = null) => new(operation,
            context ?? PluginHostBridge.Context(_runtime, plugin.Manifest.Permissions, plugin.Manifest.DataSubscriptions),
            plugin.Manifest.Settings.ToDictionary(s => s.Id, s => SettingValue(plugin, s)), action, argument)
        { Events = plugin.Events.ToArray(), CommandResults = plugin.Results.ToArray(), RequestId = Guid.NewGuid().ToString("N") };

    private async Task<string?> AcceptResultAsync(PluginInstallation plugin, PluginResult result, DateTimeOffset timestamp, long generation,
        bool isAction = false, CancellationToken cancellationToken = default)
    {
        var values = ValidateResult(plugin, result, timestamp);
        if (result.AvailableChoices is { } options && (options.Count > 256 || options.Any(pair =>
            plugin.Manifest.Settings.FirstOrDefault(s => s.Id == pair.Key && s.Kind == "choice") is not { } setting ||
            pair.Value is null || pair.Value.Length > 128 || pair.Value.Except(setting.Choices ?? []).Any())))
            throw new InvalidDataException("Dynamic choices must be a subset of the declared setting choices.");
        plugin.AvailableChoices = result.AvailableChoices ?? new Dictionary<string, string[]>();
        if ((result.TriggerSignals?.Length ?? 0) > 64 || result.TriggerSignals?.Any(id => !plugin.Manifest.Triggers.Any(t => t.Id == id)) == true ||
            (result.ConditionValues?.Count ?? 0) > 64 || result.ConditionValues?.Keys.Any(id => !plugin.Manifest.Conditions.Any(c => c.Id == id)) == true)
            throw new InvalidDataException("Undeclared plugin automation signal.");
        plugin.Values = values;
        plugin.OverviewValues = result.OverviewValues ?? new Dictionary<string, string?>();
        plugin.LastResultAt = timestamp;
        plugin.Conditions.Clear();
        foreach (var pair in result.ConditionValues ?? new Dictionary<string, bool>()) plugin.Conditions[pair.Key] = pair.Value;
        var signals = new HashSet<string>(result.TriggerSignals ?? []);
        var raw = plugin.Manifest.Triggers.Concat(plugin.Manifest.Conditions).Any(s => s.Threshold?.SensorId.StartsWith("toolkit.sensor.", StringComparison.Ordinal) == true)
            ? PluginHostBridge.Context(_runtime, ["sensors.read"], []).Sensors : null;
        foreach (var descriptor in plugin.Manifest.Triggers.Concat(plugin.Manifest.Conditions).Where(s => s.Threshold is not null))
        {
            var threshold = descriptor.Threshold!;
            double? value = threshold.SensorId.StartsWith("toolkit.sensor.", StringComparison.Ordinal)
                ? raw is not null && raw.TryGetValue(threshold.SensorId, out var reading) && reading.Quality == "valid" ? reading.Value : null
                : plugin.Values.FirstOrDefault(v => v.Id == threshold.SensorId)?.Value;
            if (!plugin.Thresholds.TryGetValue(descriptor.Id, out var state)) plugin.Thresholds[descriptor.Id] = state = new();
            var rose = state.Update(threshold, value, timestamp);
            if (plugin.Manifest.Conditions.Any(c => c.Id == descriptor.Id)) plugin.Conditions[descriptor.Id] = state.Active;
            else { signals.Remove(descriptor.Id); if (rose) signals.Add(descriptor.Id); }
        }
        foreach (var command in result.Commands ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Active(plugin, generation)) return "Plugin is inactive.";
            var receipt = await ExecuteCommandAsync(plugin, command, cancellationToken);
            if (!Active(plugin, generation)) return "Plugin is inactive.";
            plugin.Results.Enqueue(receipt);
            while (plugin.Results.Count > 64) plugin.Results.Dequeue();
            if (isAction && receipt.Status != "completed") return receipt.Error ?? $"Command {command.Id}: {receipt.Status}";
            if (receipt.Status is "timed-out" or "cancelled") break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!Active(plugin, generation)) return "Plugin is inactive.";
        if (result.Toast is { } toast) ShowToast(plugin, toast);
        foreach (var signal in signals)
        {
            var now = DateTimeOffset.UtcNow;
            var descriptor = plugin.Manifest.Triggers.Single(t => t.Id == signal);
            if (plugin.LastSignals.TryGetValue(signal, out var previous) && now - previous < TimeSpan.FromSeconds(descriptor.CooldownSeconds)) continue;
            plugin.LastSignals[signal] = now;
            AutomationSignal?.Invoke(signal);
        }
        return null;
    }

    internal async Task<HostCommandResult> ExecuteCommandAsync(PluginInstallation plugin, HostCommand command, CancellationToken cancellationToken = default)
    {
        var id = command.RequestId ?? Guid.NewGuid().ToString("N");
        var generation = plugin.Generation;
        if (!Active(plugin, plugin.Generation)) return new(id, command.Id, "cancelled", "Plugin is inactive.");
        if (id.Length is 0 or > 128) return new(id, command.Id, "failed", "Invalid request ID.");
        if (plugin.CompletedCommands.TryGetValue(id, out var completed)) return completed;
        // Reserve before awaiting, so a custom page and background worker cannot replay the same command.
        plugin.CompletedCommands[id] = new(id, command.Id, "running");
        HostCommandResult result;
        if (command.Id == "toolkit.recording.query")
        {
            if (!plugin.Manifest.Permissions.Contains("data.read")) result = new(id, command.Id, "denied", "data.read required.");
            else
            {
                var count = command.Arguments.TryGetValue("count", out var countValue) && countValue.TryGetInt32(out var n) ? n : 100;
                if (count is < 1 or > 1000) result = new(id, command.Id, "failed", "Count must be 1–1000.");
                else result = new(id, command.Id, "completed", Result: JsonSerializer.SerializeToElement(_runtime.CurrentBufferedSensorRecordingSamples.TakeLast(count)));
            }
        }
        else if (command.Id is "toolkit.settings.write" or "toolkit.settings.writeBatch")
        {
            var applied = new List<PluginSettingAccess>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var writes = command.Id == "toolkit.settings.writeBatch" ? command.Arguments["changes"].Deserialize<PluginSettingWrite[]>() ?? [] :
                    [new PluginSettingWrite(command.Arguments["pluginId"].GetString()!, command.Arguments["settingId"].GetString()!, command.Arguments["value"], command.Arguments.TryGetValue("expectedValue", out var expected) ? expected : null)];
                if (writes.Length is < 1 or > 32 || writes.Select(w => (w.PluginId, w.SettingId)).Distinct().Count() != writes.Length)
                    throw new ArgumentException("A settings batch needs 1–32 distinct targets.");
                foreach (var write in writes) ValidateExternalWrite(plugin, write);
                foreach (var write in writes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Active(plugin, generation)) throw new OperationCanceledException("Plugin stopped during settings update.");
                    await WriteExternalSettingAsync(plugin, write.PluginId, write.SettingId, write.Value);
                    applied.Add(new(write.PluginId, write.SettingId));
                }
                result = new(id, command.Id, "completed", Result: JsonSerializer.SerializeToElement(applied));
            }
            catch (Exception ex) { result = new(id, command.Id, ex is UnauthorizedAccessException ? "denied" : ex is OperationCanceledException ? "cancelled" : "failed", ex.GetBaseException().Message, JsonSerializer.SerializeToElement(applied)); }
        }
        else result = await PluginHostBridge.ExecuteWithResultAsync(_runtime, plugin.Manifest.Permissions, command with { RequestId = id }, plugin.Manifest.Id, cancellationToken);
        if (!Active(plugin, generation)) return result;
        plugin.CompletedCommands[id] = result;
        while (plugin.CompletedCommands.Count > 128)
        {
            var old = plugin.CompletedCommands.FirstOrDefault(p => p.Value.Status != "running" && p.Key != id).Key;
            if (old is null) break;
            plugin.CompletedCommands.Remove(old);
        }
        return result;
    }

    internal void NotifyEvent(string kind, string source = "toolkit", object? data = null)
    {
        if (!_timer.Dispatcher.CheckAccess())
        {
            if (!_timer.Dispatcher.HasShutdownStarted) _timer.Dispatcher.BeginInvoke(new Action(() => NotifyEvent(kind, source, data)));
            return;
        }
        if (_disposed) return;
        if (kind == "host.resume")
            foreach (var plugin in _plugins) { plugin.Events.Clear(); plugin.Thresholds.Clear(); plugin.NextRefresh = default; }
        var item = new PluginEvent(++_eventSequence, DateTimeOffset.UtcNow, kind, source, JsonSerializer.SerializeToElement(data ?? new { }));
        foreach (var plugin in _plugins.Where(p => p.Enabled && p.Error is null && p.Manifest.EventSubscriptions.Contains(kind)))
        {
            if (kind == "host.resume") plugin.Events.Clear();
            plugin.Events.Enqueue(item);
            while (plugin.Events.Count > 64) plugin.Events.Dequeue();
            if (plugin.ConsecutiveFailures == 0) plugin.NextRefresh = default;
        }
        if (kind is "host.resume" or "plugin.enabled") NotifyEvent("state.resync", source);
    }

    private void OnHostSettingsSaved(AppSettings settings)
    {
        if (ReferenceEquals(settings, _runtime.Settings) && !_scopedHostSettingWrite.Value)
            NotifyEvent("settings.changed");
    }

    private void ObserveState()
    {
        var state = _runtime.Snapshot;
        var modes = state.PerformanceMode + ":" + state.GpuMode;
        if (_previousModes is { } previous && previous != modes)
        {
            var parts = previous.Split(':');
            if (parts[0] != state.PerformanceMode.ToString()) NotifyEvent("performance.changed");
            if (parts[1] != state.GpuMode.ToString()) NotifyEvent("gpu.changed");
        }
        _previousModes = modes;
    }

    private async Task DeliverExitEventAsync(PluginInstallation plugin)
    {
        if (!plugin.Manifest.EventSubscriptions.Contains("host.exiting") || plugin.Client is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var acquired = false;
        try
        {
            await plugin.IoGate.WaitAsync(deadline.Token); acquired = true;
            if (plugin.Client is not null) await plugin.Client.EvaluateAsync(Request(plugin, "event"), deadline.Token);
            // Exit notifications cannot submit commands or revive contributions.
        }
        catch (Exception ex) { ToolkitLog.Warning("Plugin exit notification: " + plugin.Manifest.Id + ": " + ex.GetBaseException().Message); }
        finally { if (acquired) plugin.IoGate.Release(); }
    }

    private void ValidateDependencies(PluginInstallation plugin, HashSet<string>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(plugin.Manifest.Id)) throw new InvalidOperationException("Circular plugin dependency: " + plugin.Manifest.Id);
        foreach (var dependency in plugin.Manifest.Dependencies)
        {
            var installed = _plugins.FirstOrDefault(p => p.Manifest.Id == dependency.Id && p.Enabled && !p.PendingUninstall && p.Error is null);
            if (installed is null || !Version.TryParse(installed.Manifest.Version, out var version) ||
                dependency.MinimumVersion is { } min && version < Version.Parse(min) ||
                dependency.MaximumVersionExclusive is { } max && version >= Version.Parse(max))
                throw new InvalidOperationException("Missing, disabled or incompatible dependency: " + dependency.Id);
            ValidateDependencies(installed, visited);
        }
        visited.Remove(plugin.Manifest.Id);
    }

    internal IEnumerable<(PluginInstallation Plugin, PluginAction Action)> Actions => _plugins.Where(p => p.Enabled && p.Error is null && !p.PendingUninstall)
        .SelectMany(p => p.Manifest.Actions.Select(a => (p, a)));

    internal async Task<string?> InvokeActionAsync(string id, JsonElement argument, CancellationToken cancellationToken = default)
    {
        var entry = Actions.FirstOrDefault(a => a.Action.Id == id);
        if (entry.Plugin is null) return "插件动作不可用 / Plugin action unavailable: " + id;
        var plugin = entry.Plugin; var generation = plugin.Generation;
        var parameters = new Dictionary<string, JsonElement>();
        if (argument.ValueKind != JsonValueKind.Object) return "Action arguments must be an object.";
        foreach (var property in argument.EnumerateObject())
            if (!entry.Action.Parameters.Any(p => p.Id == property.Name)) return "Unknown action parameter: " + property.Name;
        foreach (var p in entry.Action.Parameters)
        {
            var value = argument.TryGetProperty(p.Id, out var v) ? v : p.DefaultValue;
            if (!ValidSetting(PluginExtensionPolicy.Parameter(p), value)) return "Invalid action parameter: " + p.Id;
            parameters[p.Id] = value;
        }
        await plugin.IoGate.WaitAsync(cancellationToken);
        var holdsSlot = false;
        try
        {
            await _evaluationSlots.WaitAsync(cancellationToken); holdsSlot = true;
            if (!Active(plugin, generation)) return "Plugin is inactive.";
            ValidateDependencies(plugin);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            var request = Request(plugin, "action", action: id, argument: JsonSerializer.SerializeToElement(parameters));
            var client = await GetVerifiedClientAsync(plugin, generation, linked.Token);
            var result = await client.EvaluateAsync(request, linked.Token);
            _evaluationSlots.Release(); holdsSlot = false;
            if (!Active(plugin, generation)) return "Plugin was disabled during the action.";
            if (result.ActionError is { Length: > 0 } error) return error;
            cancellationToken.ThrowIfCancellationRequested();
            return await AcceptResultAsync(plugin, result, DateTimeOffset.UtcNow, generation, isAction: true, cancellationToken: linked.Token);
        }
        catch (OperationCanceledException) { plugin.Client?.Dispose(); plugin.Client = null; throw; }
        catch (Exception ex) { plugin.Client?.Dispose(); plugin.Client = null; return ex.GetBaseException().Message; }
        finally { if (holdsSlot) _evaluationSlots.Release(); plugin.IoGate.Release(); if (!_disposed) Publish(); }
    }

    internal bool Condition(string id) => _plugins.Any(p => p.Enabled && p.Error is null && !p.PendingUninstall &&
        DateTimeOffset.UtcNow - p.LastResultAt < TimeSpan.FromSeconds(10) && p.Conditions.GetValueOrDefault(id));

    private readonly Dictionary<string, DateTimeOffset> _settingWrites = new();
    private void ValidateExternalWrite(PluginInstallation source, PluginSettingWrite write)
    {
        if (!source.Manifest.Permissions.Contains("settings.write") || !source.Manifest.WritableSettings.Any(s => s.PluginId == write.PluginId && s.SettingId == write.SettingId))
            throw new UnauthorizedAccessException("This setting is outside the plugin's approved write scope.");
        if (_settingWrites.TryGetValue(write.PluginId + ":" + write.SettingId, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("This setting changed recently; suppressing a possible feedback loop.");
        JsonElement current;
        if (write.PluginId == "toolkit")
        {
            if (!PluginBuiltinSettings.ValidExternalValue(write.SettingId, write.Value)) throw new ArgumentException("Invalid or unsupported host setting.");
            current = PluginBuiltinSettings.Read(_runtime, write.SettingId);
        }
        else
        {
            var target = _plugins.FirstOrDefault(p => p.Manifest.Id == write.PluginId && p.Enabled && p.Error is null && !p.PendingUninstall && !p.PendingUpdate)
                ?? throw new InvalidOperationException("Target plugin is unavailable.");
            var setting = target.Manifest.Settings.FirstOrDefault(s => s.Id == write.SettingId && s.AllowExternalWrite && s.Replaces is null)
                ?? throw new UnauthorizedAccessException("Target plugin does not export this setting.");
            if (!ValidSetting(setting, write.Value)) throw new ArgumentException("Invalid setting value.");
            if (target.AvailableChoices.TryGetValue(setting.Id, out var choices) && !choices.Contains(write.Value.GetString()))
                throw new ArgumentException("Target option is currently unavailable.");
            current = SettingValue(target, setting);
        }
        if (write.ExpectedValue is { } expected && !JsonElement.DeepEquals(current, expected))
            throw new InvalidOperationException("Setting changed since it was read; refusing to overwrite a newer value.");
    }
    private async Task WriteExternalSettingAsync(PluginInstallation source, string pluginId, string settingId, JsonElement value)
    {
        if (!source.Manifest.Permissions.Contains("settings.write") || !source.Manifest.WritableSettings.Any(s => s.PluginId == pluginId && s.SettingId == settingId))
            throw new UnauthorizedAccessException("This setting is outside the plugin's approved write scope.");
        var key = pluginId + ":" + settingId;
        // A target-wide rate limit also bounds feedback loops between two plugins.
        if (_settingWrites.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("This setting changed recently; suppressing a possible feedback loop.");
        _settingWrites[key] = DateTimeOffset.UtcNow;
        if (pluginId == "toolkit")
        {
            _scopedHostSettingWrite.Value = true;
            try { await PluginBuiltinSettings.ApplyAsync(_runtime, settingId, value); }
            finally { _scopedHostSettingWrite.Value = false; }
        }
        else
        {
            var target = _plugins.FirstOrDefault(p => p.Manifest.Id == pluginId && p.Enabled && p.Error is null && !p.PendingUninstall)
                ?? throw new InvalidOperationException("Target plugin is unavailable.");
            var setting = target.Manifest.Settings.FirstOrDefault(s => s.Id == settingId && s.AllowExternalWrite && s.Replaces is null)
                ?? throw new UnauthorizedAccessException("Target plugin does not allow external writes to this setting.");
            if (!ValidSetting(setting, value)) throw new ArgumentException("Invalid setting value.");
            var next = new Dictionary<string, JsonElement>(target.Settings) { [settingId] = value.Clone() };
            SaveJson(SettingsPath(target), next); target.Settings = next; target.NextRefresh = default;
        }
        NotifyEvent("settings.changed", source.Manifest.Id, new { PluginId = pluginId, SettingId = settingId });
        ToolkitLog.Info($"Plugin setting write: source={source.Manifest.Id}; target={key}.");
    }
}
