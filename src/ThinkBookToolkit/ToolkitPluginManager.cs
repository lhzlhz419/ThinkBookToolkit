using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed record PublishedPluginSensor(string Id, string Name, string EnglishName, string Unit,
    string Category, double? Value, string PluginId, string? Replaces, DateTimeOffset Timestamp)
{
    public string Text => Value is { } value ? value.ToString("0.##") + " " + Unit : "--";
}
internal sealed class PluginInstallation(string directory, PluginManifest manifest, string fingerprint)
{
    public string Directory { get; } = directory;
    public PluginManifest Manifest { get; } = manifest;
    public string Fingerprint { get; } = fingerprint;
    public bool Enabled { get; set; }
    public string? Error { get; set; }
    internal PluginProcessClient? Client;
    internal Dictionary<string, JsonElement> Settings = new();
    internal PublishedPluginSensor[] Values = [];
    internal IReadOnlyDictionary<string, string?> OverviewValues = new Dictionary<string, string?>();
    internal DateTimeOffset LastResultAt;
}
internal sealed record PluginPreference(bool Enabled, string Fingerprint);
internal sealed record PluginManagerOptions(string? PluginRoot = null, string? StateRoot = null, string? HostPath = null, string? BundledRoot = null);

internal sealed class ToolkitPluginManager : IDisposable, INotifyPropertyChanged
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<PluginInstallation> _plugins = [];
    private readonly List<string> _discoveryErrors = [];
    private Dictionary<string, PluginPreference> _preferences = new();
    private bool _disposed;
    private bool _busy;
    private bool _initialized;
    private bool _discovered;
    private Task? _discoveryTask;
    private bool _fanBackendPrepared;
    internal PluginFanBackendSelection? ActiveFanBackend { get; private set; }
    private readonly PluginManagerOptions _options;
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CatalogChanged;
    public event EventHandler? ValuesChanged;
    public IReadOnlyList<PluginInstallation> Installations => _plugins;
    public IReadOnlyList<string> DiscoveryErrors => _discoveryErrors;
    public IReadOnlyList<PublishedPluginSensor> Sensors => _plugins.Where(p => p.Enabled && p.Error is null).SelectMany(p => p.Values)
        .Select(sensor => DateTimeOffset.UtcNow - sensor.Timestamp > TimeSpan.FromSeconds(10) ? sensor with { Value = null } : sensor).ToArray();
    internal string Root => _options.PluginRoot ?? Path.Combine(ToolkitStoragePaths.Configuration, "plugins");
    internal string StateRoot => _options.StateRoot ?? Path.Combine(ToolkitStoragePaths.Configuration, "plugin-settings");
    internal ToolkitPluginManager(ToolkitRuntimeService runtime, PluginManagerOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? new();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    internal async Task InitializeAsync(bool safeMode = false)
    {
        if (_initialized) return;
        _initialized = true;
        await DiscoverAsync(safeMode);
        if (safeMode) return;
        await RefreshAsync();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    // Discovery never executes plugin code. The hardware provider must be pinned
    // before feature detection constructs the first FanController.
    internal Task DiscoverAsync(bool safeMode = false) => _discoveryTask ??= DiscoverCoreAsync(safeMode);

    private async Task DiscoverCoreAsync(bool safeMode)
    {
        if (_discovered) return;
        _discovered = true;
        if (safeMode) return;
        try { _preferences = JsonSerializer.Deserialize<Dictionary<string, PluginPreference>>(File.ReadAllText(Path.Combine(StateRoot, "enabled.json"))) ?? new(); } catch { }
        foreach (var root in new[] { _options.BundledRoot ?? Path.Combine(AppContext.BaseDirectory, "Plugins"), Root }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var file = Path.Combine(directory, "plugin.json");
                if (!File.Exists(file)) continue;
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Manifest too large.");
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty manifest.");
                    ValidateManifest(manifest, directory);
                    if (_plugins.Any(p => p.Manifest.Id == manifest.Id)) throw new InvalidDataException("Duplicate plugin ID: " + manifest.Id);
                    var fingerprint = await Task.Run(() => Fingerprint(directory));
                    var plugin = new PluginInstallation(directory, manifest, fingerprint);
                    foreach (var setting in manifest.Settings) plugin.Settings[setting.Id] = setting.DefaultValue.Clone();
                    try
                    {
                        var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath(plugin)));
                        foreach (var pair in saved ?? [])
                            if (manifest.Settings.FirstOrDefault(s => s.Id == pair.Key) is { } descriptor && ValidSetting(descriptor, pair.Value))
                                plugin.Settings[pair.Key] = pair.Value.Clone();
                    }
                    catch { }
                    plugin.Enabled = _preferences.TryGetValue(manifest.Id, out var pref)
                        && pref.Enabled && pref.Fingerprint == fingerprint;
                    _plugins.Add(plugin);
                    if (plugin.Enabled && FindConflict(plugin) is { } conflict) { plugin.Enabled = false; plugin.Error = conflict; }
                }
                catch (Exception ex)
                {
                    _discoveryErrors.Add(Path.GetFileName(directory) + ": " + ex.GetBaseException().Message);
                    ToolkitLog.Warning("Plugin discovery rejected " + file + ": " + ex.Message);
                }
            }
        }
    }

    internal async Task<PluginInstallation> ImportAsync(string archivePath)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ToolkitPluginManager));
        await DiscoverAsync();
        using var package = await Task.Run(() => PluginArchiveImporter.Prepare(archivePath, Root, _stop.Token), _stop.Token);
        await _gate.WaitAsync(_stop.Token);
        PluginInstallation plugin;
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_plugins.Any(p => p.Manifest.Id == package.Manifest.Id) || HasInstalledId(package.Manifest.Id))
                throw new InvalidOperationException("相同 ID 的插件已安装，本次不会覆盖。 / A plugin with this ID is already installed; no files were replaced.");
            var destination = Path.GetFullPath(Path.Combine(Root, package.Manifest.Id));
            PluginArchiveImporter.EnsureNoLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("目标文件夹已存在，本次不会覆盖。 / The destination already exists; no files were replaced.");
            plugin = new PluginInstallation(destination, package.Manifest, package.Fingerprint);
            foreach (var setting in plugin.Manifest.Settings) plugin.Settings[setting.Id] = setting.DefaultValue.Clone();
            var preferencePath = Path.Combine(StateRoot, "enabled.json");
            // Safe startup intentionally skips discovery/preferences. Preserve
            // other approvals even when importing from that mode.
            var previous = File.Exists(preferencePath)
                ? JsonSerializer.Deserialize<Dictionary<string, PluginPreference>>(File.ReadAllText(preferencePath))
                    ?? throw new InvalidDataException("Invalid plugin preferences.")
                : new Dictionary<string, PluginPreference>(_preferences);
            var preferences = new Dictionary<string, PluginPreference>(previous) { [plugin.Manifest.Id] = new(false, plugin.Fingerprint) };
            SaveJson(Path.Combine(StateRoot, "enabled.json"), preferences);
            try { package.Commit(destination); }
            catch
            {
                try { SaveJson(preferencePath, previous); }
                catch (Exception ex) { ToolkitLog.Warning("Could not restore plugin preferences after failed import: " + ex.Message); }
                throw;
            }
            _preferences = preferences;
            _plugins.Add(plugin);
        }
        finally { _gate.Release(); }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return plugin;
    }

    private bool HasInstalledId(string id)
    {
        foreach (var root in new[] { _options.BundledRoot ?? Path.Combine(AppContext.BaseDirectory, "Plugins"), Root }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(directory, "plugin.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) continue;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    if (document.RootElement.TryGetProperty("Id", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == id) return true;
                }
                catch (JsonException) { }
            }
        }
        return false;
    }

    internal async Task PrepareFanBackendAsync(bool safeMode = false, Action<string, PluginFanBackendSelection>? stageForTesting = null)
    {
        if (_fanBackendPrepared) return;
        _fanBackendPrepared = true;
        await DiscoverAsync(safeMode);
        var plugin = safeMode ? null : _plugins.SingleOrDefault(p => p.Enabled && p.Manifest.FanBackend is not null);
        ActiveFanBackend = plugin is null ? null : new(plugin.Manifest.Id, plugin.Fingerprint,
            plugin.Manifest.FanBackend!.Assembly, plugin.Manifest.FanBackend.Type);
        FanController.SelectedPlugin = ActiveFanBackend;
        FanController.PluginPreparationError = null;
        if (plugin is null) return;
        try { await Task.Run(() => (stageForTesting ?? PluginFanBackendPackage.Stage)(plugin.Directory, ActiveFanBackend!)); }
        catch (Exception ex)
        {
            plugin.Error = "风扇后端准备失败 / Fan backend preparation failed: " + ex.GetBaseException().Message;
            // Keep the selection pinned: never silently write through another transport.
            FanController.PluginPreparationError = plugin.Error;
            ToolkitLog.Error(plugin.Error, ex);
        }
    }

    internal string FanBackendStatus(PluginInstallation plugin) => plugin.Manifest.FanBackend is null ? "" :
        ActiveFanBackend?.PluginId == plugin.Manifest.Id
            ? plugin.Enabled ? _runtime.L("风扇后端：本次运行已选中", "Fan backend: selected for this session")
                : _runtime.L("风扇后端：重启后停用，本次运行仍使用此后端", "Fan backend: disabled after restart; still selected for this session")
            : plugin.Enabled ? _runtime.L("风扇后端：重启后生效", "Fan backend: restart to activate")
                : _runtime.L("风扇后端：未启用", "Fan backend: disabled");

    internal static void ValidateManifest(PluginManifest manifest, string directory)
    {
        if (manifest.ApiVersion != PluginApiVersion.Current) throw new InvalidDataException("Unsupported plugin API version.");
        if (!Regex.IsMatch(manifest.Id, @"^[a-z0-9][a-z0-9._-]{0,100}$")) throw new InvalidDataException("Invalid plugin ID.");
        if (Path.GetFileName(manifest.EntryAssembly) != manifest.EntryAssembly || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory, manifest.EntryAssembly))) throw new InvalidDataException("Invalid entry assembly.");
        if (manifest.Pages is null || manifest.Settings is null || manifest.Sensors is null || manifest.Permissions is null) throw new InvalidDataException("Missing declarations.");
        if (manifest.Pages.Length > 32 || manifest.Settings.Length > 256 || manifest.Sensors.Length > 256 ||
            string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 256) throw new InvalidDataException("Manifest limits exceeded.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 160 || manifest.Author?.Length > 160 || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64 ||
            manifest.Pages.Any(p => p.Id.Length > 180 || p.Title.Chinese.Length > 160 || p.Title.English.Length > 160) ||
            manifest.Settings.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 100 || s.Title.Chinese.Length > 160 || s.Title.English.Length > 160) ||
            manifest.Sensors.Any(s => s.Name.Chinese.Length > 160 || s.Name.English.Length > 160 || s.Unit.Length > 32)) throw new InvalidDataException("Invalid metadata length.");
        if (manifest.Permissions.Except(new[] { "sensors.read", "data.read", "settings.read", "host.control", "replace", "fan.backend" }).Any()) throw new InvalidDataException("Unknown permission.");
        if (manifest.FanBackend is { } backend)
        {
            PluginFanBackendPackage.ValidateSelection(new(manifest.Id, new string('0', 64), backend.Assembly, backend.Type));
            if (!manifest.Permissions.Contains("fan.backend") || !File.Exists(Path.Combine(directory, backend.Assembly)))
                throw new InvalidDataException("Fan backend requires fan.backend permission and its assembly.");
        }
        ValidateOverviewItems(manifest);
        if (manifest.SensorCategories is null || manifest.SensorCategories.Length > 32 || manifest.SensorCategories.Any(c => c is null ||
            string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 180 || c.Title is null || string.IsNullOrWhiteSpace(c.Title.Chinese) ||
            string.IsNullOrWhiteSpace(c.Title.English) || c.Title.Chinese.Length > 160 || c.Title.English.Length > 160))
            throw new InvalidDataException("Invalid sensor category declaration.");
        var ids = manifest.Pages.Select(x => x.Id).Concat(manifest.Sensors.Select(x => x.Id)).Concat(manifest.OverviewItems.Select(x => x.Id))
            .Concat(manifest.SensorCategories.Select(x => x.Id)).ToArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(id => !id.StartsWith(manifest.Id + ".", StringComparison.Ordinal))) throw new InvalidDataException("IDs must belong to the plugin namespace.");
        if (manifest.Settings.Select(s => s.Id).Distinct().Count() != manifest.Settings.Length) throw new InvalidDataException("Duplicate setting ID.");
        foreach (var setting in manifest.Settings)
        {
            if (!ValidSetting(setting, setting.DefaultValue) || !(manifest.Pages.Any(p => p.Id == setting.PageId) || BuiltinPages.Contains(setting.PageId))) throw new InvalidDataException("Invalid setting definition: " + setting.Id);
            if (setting.Replaces is { } slot && (PluginBuiltinSettings.Kind(slot) is not { } kind || kind != setting.Kind)) throw new InvalidDataException("Unknown or incompatible setting replacement: " + slot);
        }
        if (manifest.Sensors.Any(s => s.Id.Length > 180 || s.Replaces is { } target &&
            (!target.StartsWith("toolkit.sensor.", StringComparison.Ordinal) || !SensorRecordingFormat.MetricKeys.Contains(target["toolkit.sensor.".Length..])))) throw new InvalidDataException("Invalid sensor replacement.");
        if (Targets(manifest).Distinct(StringComparer.Ordinal).Count() != Targets(manifest).Count()) throw new InvalidDataException("Duplicate replacement target.");
        if (manifest.Pages.Any(p => p.Replaces is "plugins") || manifest.Pages.Any(p => p.Replaces is not null && !BuiltinPages.Contains(p.Replaces))) throw new InvalidDataException("Invalid page replacement.");
        if (Targets(manifest).Any(t => t != "fan-backend") && !manifest.Permissions.Contains("replace")) throw new InvalidDataException("Replacement permission was not declared.");
    }
    internal static bool ValidSetting(PluginSetting setting, JsonElement value) => setting.Kind switch
    {
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) &&
            (!setting.Minimum.HasValue || number >= setting.Minimum) && (!setting.Maximum.HasValue || number <= setting.Maximum),
        "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096,
        "choice" => value.ValueKind == JsonValueKind.String && setting.Choices?.Contains(value.GetString()) == true,
        _ => false
    };
    private static void ValidateOverviewItems(PluginManifest manifest)
    {
        if (manifest.OverviewItems is null || manifest.OverviewItems.Length > 128)
            throw new InvalidDataException("Invalid overview declarations.");
        foreach (var item in manifest.OverviewItems)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 180 ||
                !OverviewLayoutDefaults.CardDefinitions.TryGetValue(item.CardId, out var slots) ||
                item.Action is not ("add" or "replace" or "remove") ||
                (item.Action == "add" ? item.Target is not null : item.Target is null || !slots.Contains(item.Target)) ||
                (item.Action != "remove" && (item.Label is null || !ValidText(item.Label, 160))) ||
                (item.Text is not null && !ValidText(item.Text, 4096)) ||
                (item.SensorId is not null && !manifest.Sensors.Any(s => s.Id == item.SensorId)) ||
                (item.Action == "remove" && (item.Text is not null || item.SensorId is not null)))
                throw new InvalidDataException("Invalid overview item: " + item?.Id);
        }
        static bool ValidText(PluginText text, int limit) => text.Chinese is not null && text.English is not null &&
            text.Chinese.Length <= limit && text.English.Length <= limit;
    }

    internal (PluginInstallation Plugin, PluginOverviewItem Item)[] OverviewItems(string cardId) =>
        _plugins.Where(p => p.Enabled && p.Error is null)
            .SelectMany(p => p.Manifest.OverviewItems.Where(i => i.CardId == cardId).Select(i => (Plugin: p, Item: i)))
            .OrderBy(x => x.Item.Order).ThenBy(x => x.Item.Id, StringComparer.Ordinal).ToArray();

    internal (PluginInstallation Plugin, PluginSensorCategory Category)[] SensorCategories =>
        _plugins.Where(p => p.Enabled && p.Error is null)
            .SelectMany(p => p.Manifest.SensorCategories.Where(c => p.Manifest.Sensors.Any(s => s.Category == c.Id && s.Replaces is null))
                .Select(c => (Plugin: p, Category: c)))
            .OrderBy(x => x.Category.Order).ThenBy(x => x.Category.Id, StringComparer.Ordinal).ToArray();

    internal string OverviewText(PluginInstallation plugin, PluginOverviewItem item)
    {
        if (!plugin.Enabled || plugin.Error is not null) return "--";
        if (item.SensorId is { } sensorId) return Sensors.FirstOrDefault(s => s.Id == sensorId)?.Text ?? "--";
        if (plugin.OverviewValues.TryGetValue(item.Id, out var value))
            return DateTimeOffset.UtcNow - plugin.LastResultAt <= TimeSpan.FromSeconds(10) ? value ?? "--" : "--";
        return item.Text?.Resolve(_runtime.IsChinese) ?? "--";
    }
    internal static readonly string[] BuiltinPages = ["overview", "performance", "cooling", "battery", "display", "sound", "input", "automation", "sensors-integration", "device", "driver-update", "advanced", "settings"];
    private static IEnumerable<string> Targets(PluginManifest manifest) => manifest.Pages.Where(x => x.Replaces is not null).Select(x => "page:" + x.Replaces)
        .Concat(manifest.Settings.Where(x => x.Replaces is not null).Select(x => "setting:" + x.Replaces))
        .Concat(manifest.Sensors.Where(x => x.Replaces is not null).Select(x => "sensor:" + x.Replaces))
        .Concat(manifest.OverviewItems.Where(x => x.Action != "add").Select(x => "overview:" + x.CardId + "/" + x.Target))
        .Concat(manifest.FanBackend is null ? Array.Empty<string>() : new[] { "fan-backend" });
    private string? FindConflict(PluginInstallation candidate) => _plugins.Where(p => p != candidate && p.Enabled)
        .Any(p => Targets(p.Manifest).Intersect(Targets(candidate.Manifest)).Any()) ? "替换目标已被另一插件占用。 / Replacement target already has an owner." : null;

    internal async Task SetEnabledAsync(PluginInstallation plugin, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (enabled && FindConflict(plugin) is { } conflict) throw new InvalidOperationException(conflict);
            if (enabled && Fingerprint(plugin.Directory) != plugin.Fingerprint) throw new InvalidOperationException("插件文件已改变，请重启后重新授权。 / Plugin files changed; restart and review permissions again.");
            _preferences[plugin.Manifest.Id] = new(enabled, plugin.Fingerprint);
            SaveJson(Path.Combine(StateRoot, "enabled.json"), _preferences);
            plugin.Enabled = enabled; plugin.Error = null; plugin.Values = [];
            plugin.OverviewValues = new Dictionary<string, string?>();
            plugin.Client?.Dispose(); plugin.Client = null;
        }
        finally { _gate.Release(); }
        Publish(); CatalogChanged?.Invoke(this, EventArgs.Empty);
        await RefreshAsync();
    }
    internal async Task SetSettingAsync(PluginInstallation plugin, PluginSetting setting, JsonElement value)
    {
        if (!plugin.Enabled || !ValidSetting(setting, value)) throw new ArgumentException("Invalid plugin setting.");
        await _gate.WaitAsync();
        try
        {
            if (setting.Replaces is { } slot) await PluginBuiltinSettings.ApplyAsync(_runtime, slot, value);
            else
            {
                var next = new Dictionary<string, JsonElement>(plugin.Settings) { [setting.Id] = value.Clone() };
                SaveJson(SettingsPath(plugin), next); plugin.Settings = next;
            }
        }
        finally { _gate.Release(); }
        await RefreshAsync();
    }
    internal JsonElement SettingValue(PluginInstallation plugin, PluginSetting setting) =>
        setting.Replaces is { } slot ? PluginBuiltinSettings.Read(_runtime, slot) : plugin.Settings[setting.Id];
    internal (PluginInstallation Plugin, PluginPage Page)? Page(string id)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled && p.Error is null))
            foreach (var page in plugin.Manifest.Pages)
                if ((page.Replaces ?? page.Id) == id) return (plugin, page);
        return null;
    }
    internal (PluginInstallation Plugin, PluginSetting Setting)? Setting(string id)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled && p.Error is null))
            if (plugin.Manifest.Settings.FirstOrDefault(s => s.Replaces == id) is { } setting) return (plugin, setting);
        return null;
    }
    internal string OverrideText(string sensorId, string original) => Sensors.FirstOrDefault(s => s.Replaces == sensorId && s.Value.HasValue)?.Text ?? original;

    internal async Task RefreshAsync()
    {
        if (_disposed || _busy) return;
        _busy = true;
        await _gate.WaitAsync();
        var catalogChanged = false;
        try
        {
            foreach (var plugin in _plugins.Where(p => p.Enabled && p.Error is null))
            {
                try
                {
                    plugin.Client ??= new(plugin.Directory, plugin.Manifest, _options.HostPath);
                    var context = PluginHostBridge.Context(_runtime, plugin.Manifest.Permissions);
                    var settings = plugin.Manifest.Settings.ToDictionary(s => s.Id, s => SettingValue(plugin, s));
                    var result = await plugin.Client.EvaluateAsync(new("refresh", context, settings), _stop.Token);
                    plugin.Values = ValidateResult(plugin, result, context.Timestamp);
                    plugin.OverviewValues = result.OverviewValues ?? new Dictionary<string, string?>();
                    plugin.LastResultAt = context.Timestamp;
                    foreach (var command in result.Commands ?? [])
                        await PluginHostBridge.ExecuteAsync(_runtime, plugin.Manifest.Permissions, command);
                }
                catch (Exception ex)
                {
                    plugin.Error = ex.GetBaseException().Message; plugin.Values = [];
                    plugin.OverviewValues = new Dictionary<string, string?>();
                    plugin.Client?.Dispose(); plugin.Client = null; catalogChanged = true;
                    ToolkitLog.Error("Plugin suspended: " + plugin.Manifest.Id, ex);
                }
            }
        }
        finally { _gate.Release(); _busy = false; }
        if (_disposed) return;
        Publish();
        if (catalogChanged) CatalogChanged?.Invoke(this, EventArgs.Empty);
    }
    internal static PublishedPluginSensor[] ValidateResult(PluginInstallation plugin, PluginResult result, DateTimeOffset timestamp)
    {
        if ((result.OverviewValues?.Count ?? 0) > 128 || result.OverviewValues?.Any(pair =>
            pair.Value?.Length > 4096 || !plugin.Manifest.OverviewItems.Any(i => i.Id == pair.Key && i.Action != "remove")) == true)
            throw new InvalidDataException("Invalid or undeclared overview value.");
        if (result.Values.Count > 256 || (result.Commands?.Length ?? 0) > 8) throw new InvalidDataException("Plugin output limit exceeded.");
        if (result.Values.Keys.Any(id => !plugin.Manifest.Sensors.Any(s => s.Id == id)) ||
            (result.VisibleSensors ?? []).Any(id => !plugin.Manifest.Sensors.Any(s => s.Id == id))) throw new InvalidDataException("Undeclared sensor.");
        return plugin.Manifest.Sensors.Where(s => result.VisibleSensors?.Contains(s.Id) ?? result.Values.ContainsKey(s.Id)).Select(s =>
        {
            var value = result.Values.GetValueOrDefault(s.Id);
            if (value.HasValue && !double.IsFinite(value.Value)) throw new InvalidDataException("Non-finite sensor value.");
            return new PublishedPluginSensor(s.Id, s.Name.Chinese, s.Name.English, s.Unit, s.Category, value, plugin.Manifest.Id, s.Replaces, timestamp);
        }).ToArray();
    }
    private void Publish()
    {
        Revision++; PropertyChanged?.Invoke(this, new(nameof(Revision)));
        ValuesChanged?.Invoke(this, EventArgs.Empty);
        _runtime.PublishPluginSensors(Sensors);
    }
    private string SettingsPath(PluginInstallation plugin) => Path.Combine(StateRoot, plugin.Manifest.Id + ".json");
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static string Fingerprint(string directory) => PluginFanBackendPackage.Fingerprint(directory);
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _stop.Cancel();
        foreach (var plugin in _plugins) plugin.Client?.Dispose();
    }
}
