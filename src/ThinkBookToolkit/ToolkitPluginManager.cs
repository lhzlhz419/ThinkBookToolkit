using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    public bool PendingUninstall { get; set; }
    public string? Error { get; set; }
    internal PluginProcessClient? Client;
    internal Dictionary<string, JsonElement> Settings = new();
    internal PublishedPluginSensor[] Values = [];
    internal IReadOnlyDictionary<string, string?> OverviewValues = new Dictionary<string, string?>();
    internal DateTimeOffset LastResultAt;
    internal long LastToastTimestamp;
    internal string? LastToastKey;
}
internal sealed record PluginToastNotification(string PluginId, string PluginName, string Message, bool IsError);
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
    private bool _exitSuspended;
    internal bool IsSuspendedForExit => _exitSuspended;
    private List<PendingPluginRemoval> _pendingRemovals = [];
    private PluginRemovalStore Removals => new(Root, _options.BundledRoot ?? Path.Combine(AppContext.BaseDirectory, "Plugins"), StateRoot);
    internal bool HasPendingUninstalls => _pendingRemovals.Count > 0;
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
        ProcessPendingRemovals();
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
                    LoadPluginSettings(plugin);
                    plugin.Enabled = _preferences.TryGetValue(manifest.Id, out var pref)
                        && pref.Enabled && pref.Fingerprint == fingerprint;
                    plugin.PendingUninstall = _pendingRemovals.Any(r => r.PluginId == manifest.Id && Removals.Matches(r, directory));
                    if (plugin.PendingUninstall) plugin.Enabled = false;
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
            if (_pendingRemovals.Any(r => r.PluginId == package.Manifest.Id))
                throw new PluginRestartRequiredException("此插件尚未完成卸载，请重启 Toolkit 后再次导入。 / Restart Toolkit to finish uninstalling this plugin, then import it again.");
            if (_plugins.Any(p => p.Manifest.Id == package.Manifest.Id) || HasInstalledId(package.Manifest.Id))
                throw new InvalidOperationException("相同 ID 的插件已安装，本次不会覆盖。 / A plugin with this ID is already installed; no files were replaced.");
            var destination = Path.GetFullPath(Path.Combine(Root, package.Manifest.Id));
            PluginArchiveImporter.EnsureNoLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("目标文件夹已存在，本次不会覆盖。 / The destination already exists; no files were replaced.");
            plugin = new PluginInstallation(destination, package.Manifest, package.Fingerprint);
            LoadPluginSettings(plugin);
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

    private void ProcessPendingRemovals()
    {
        try { _pendingRemovals = Removals.Read(); }
        catch (Exception ex) { _discoveryErrors.Add("无法读取待卸载任务 / Cannot read pending uninstalls: " + ex.Message); return; }
        foreach (var pending in _pendingRemovals.ToArray())
        {
            try { Removals.Remove(pending); _pendingRemovals.Remove(pending); }
            catch (Exception ex) { _discoveryErrors.Add(pending.PluginId + " 卸载尚未完成 / Uninstall pending: " + ex.Message); }
        }
        try { if (File.Exists(Path.Combine(StateRoot, "pending-uninstall.json"))) Removals.Save(_pendingRemovals); }
        catch (Exception ex) { _discoveryErrors.Add("Cannot update uninstall journal: " + ex.Message); }
    }

    internal bool RequiresRestart(PluginInstallation plugin) => plugin.PendingUninstall ||
        plugin.Manifest.FanBackend is not null && plugin.Enabled != (ActiveFanBackend?.PluginId == plugin.Manifest.Id);

    internal async Task<bool> UninstallAsync(PluginInstallation plugin, bool? loadedForTesting = null)
    {
        await _gate.WaitAsync(_stop.Token);
        PendingPluginRemoval record;
        bool restart;
        try
        {
            if (!_plugins.Contains(plugin)) throw new InvalidOperationException("Plugin is not installed.");
            if (plugin.PendingUninstall) return true;
            record = Removals.Plan(plugin);
            restart = loadedForTesting ?? (PluginUiLoader.IsLoaded(plugin.Directory) || ActiveFanBackend?.PluginId == plugin.Manifest.Id);
            var previous = new Dictionary<string, PluginPreference>(_preferences);
            var next = new Dictionary<string, PluginPreference>(previous) { [plugin.Manifest.Id] = new(false, plugin.Fingerprint) };
            SaveJson(Path.Combine(StateRoot, "enabled.json"), next);
            try { Removals.Save(_pendingRemovals.Append(record).ToArray()); }
            catch { SaveJson(Path.Combine(StateRoot, "enabled.json"), previous); throw; }
            _preferences = next; _pendingRemovals.Add(record);
            plugin.Enabled = false; plugin.PendingUninstall = true; plugin.Error = null;
            plugin.Client?.Dispose(); plugin.Client = null; plugin.Values = [];
            plugin.OverviewValues = new Dictionary<string, string?>();
        }
        finally { _gate.Release(); }
        // Dispose visible custom views and withdraw registrations before file removal.
        Publish(); CatalogChanged?.Invoke(this, EventArgs.Empty);
        if (restart) return true;
        try { await Task.Run(() => Removals.Remove(record)); }
        catch (Exception ex)
        {
            plugin.Error = "文件仍被占用或无法删除，请重启后重试。 / Files could not be removed; restart to retry. " + ex.Message;
            CatalogChanged?.Invoke(this, EventArgs.Empty); return true;
        }
        await _gate.WaitAsync();
        try
        {
            var remaining = _pendingRemovals.Where(r => r != record).ToList();
            Removals.Save(remaining); _pendingRemovals = remaining; _plugins.Remove(plugin);
        }
        finally { _gate.Release(); }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return false;
    }

    internal async Task SuspendForExitAsync()
    {
        _timer.Stop(); _exitSuspended = true;
        await _gate.WaitAsync();
        try { foreach (var plugin in _plugins) { plugin.Client?.Dispose(); plugin.Client = null; } }
        finally { _gate.Release(); }
    }
    internal void ResumeAfterCancelledExit() { if (!_disposed) { _exitSuspended = false; if (_initialized) _timer.Start(); } }

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
        if (manifest.Sensors.Any(s => !PluginSensorChartPolicy.ValidBounds(s.ChartMinimum, s.ChartMaximum)))
            throw new InvalidDataException("传感器绘图范围无效：上下限必须是有限数字，且下限小于上限。 / Invalid sensor chart range: bounds must be finite and minimum must be less than maximum.");
        if (manifest.Permissions.Except(new[] { "sensors.read", "data.read", "settings.read", "host.control", "replace", "fan.backend", "ui.custom", "ui.toast" }).Any()) throw new InvalidDataException("Unknown permission.");
        foreach (var page in manifest.Pages.Where(p => p.View is not null))
        {
            var view = page.View!;
            if (!manifest.Permissions.Contains("ui.custom") || string.IsNullOrWhiteSpace(view.Assembly) ||
                view.Assembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(view.Assembly) != view.Assembly ||
                !view.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, view.Assembly)) ||
                string.IsNullOrWhiteSpace(view.Type) || view.Type.Length > 256)
                throw new InvalidDataException("Custom pages require ui.custom, a local DLL, and an entry type.");
        }
        if (manifest.FanBackend is { } backend)
        {
            PluginFanBackendPackage.ValidateSelection(new(manifest.Id, new string('0', 64), backend.Assembly, backend.Type));
            if (!manifest.Permissions.Contains("fan.backend") || !File.Exists(Path.Combine(directory, backend.Assembly)))
                throw new InvalidDataException("Fan backend requires fan.backend permission and its assembly.");
        }
        ValidateOverviewItems(manifest);
        ValidateSettingGroups(manifest);
        if (manifest.SensorCategories is null || manifest.SensorCategories.Length > 32 || manifest.SensorCategories.Any(c => c is null ||
            string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 180 || c.Title is null || string.IsNullOrWhiteSpace(c.Title.Chinese) ||
            string.IsNullOrWhiteSpace(c.Title.English) || c.Title.Chinese.Length > 160 || c.Title.English.Length > 160))
            throw new InvalidDataException("Invalid sensor category declaration.");
        var ids = manifest.Pages.Select(x => x.Id).Concat(manifest.Sensors.Select(x => x.Id)).Concat(manifest.OverviewItems.Select(x => x.Id))
            .Concat(manifest.SensorCategories.Select(x => x.Id)).Concat(manifest.SettingGroups.Select(x => x.Id)).ToArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(id => !id.StartsWith(manifest.Id + ".", StringComparison.Ordinal))) throw new InvalidDataException("IDs must belong to the plugin namespace.");
        if (manifest.Settings.Select(s => s.Id).Distinct().Count() != manifest.Settings.Length) throw new InvalidDataException("Duplicate setting ID.");
        foreach (var setting in manifest.Settings)
        {
            if (setting.GroupId is { } groupId && (setting.Replaces is not null ||
                !manifest.SettingGroups.Any(g => g.Id == groupId && g.PageId == setting.PageId)))
                throw new InvalidDataException("Setting group must exist on the same page and cannot be used with a replacement.");
            if (!ValidSetting(setting, setting.DefaultValue) || !(manifest.Pages.Any(p => p.Id == setting.PageId) || BuiltinPages.Contains(setting.PageId))) throw new InvalidDataException("Invalid setting definition: " + setting.Id);
            if (setting.Replaces is { } slot && (PluginBuiltinSettings.Kind(slot) is not { } kind || kind != setting.Kind)) throw new InvalidDataException("Unknown or incompatible setting replacement: " + slot);
        }
        if (manifest.Sensors.Any(s => s.Id.Length > 180 || s.Replaces is { } target &&
            (!target.StartsWith("toolkit.sensor.", StringComparison.Ordinal) || !SensorRecordingFormat.MetricKeys.Contains(target["toolkit.sensor.".Length..])))) throw new InvalidDataException("Invalid sensor replacement.");
        if (Targets(manifest).Distinct(StringComparer.Ordinal).Count() != Targets(manifest).Count()) throw new InvalidDataException("Duplicate replacement target.");
        if (manifest.Pages.Any(p => p.Replaces is "plugins") || manifest.Pages.Any(p => p.Replaces is not null && !BuiltinPages.Contains(p.Replaces))) throw new InvalidDataException("Invalid page replacement.");
        if (Targets(manifest).Any(t => t != "fan-backend") && !manifest.Permissions.Contains("replace")) throw new InvalidDataException("Replacement permission was not declared.");
    }
    private static void ValidateSettingGroups(PluginManifest manifest)
    {
        static bool TextValid(PluginText? text, int maximum) => text is not null &&
            !string.IsNullOrWhiteSpace(text.Chinese) && !string.IsNullOrWhiteSpace(text.English) &&
            text.Chinese.Length <= maximum && text.English.Length <= maximum;
        if (manifest.SettingGroups is null || manifest.SettingGroups.Length > 32 || manifest.SettingGroups.Any(g =>
            g is null || string.IsNullOrWhiteSpace(g.Id) || g.Id.Length > 180 || !TextValid(g.Title, 160) ||
            g.Description is not null && !TextValid(g.Description, 1024) || g.Glyph?.Length > 8 ||
            !(BuiltinPages.Contains(g.PageId) || manifest.Pages.Any(p => p.Id == g.PageId))))
            throw new InvalidDataException("Invalid setting group declaration.");
        if (manifest.Settings.Any(s => s.Description is not null && !TextValid(s.Description, 1024) || s.Glyph?.Length > 8))
            throw new InvalidDataException("Invalid setting presentation metadata.");
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
            if (plugin.PendingUninstall || !_plugins.Contains(plugin)) throw new InvalidOperationException("插件待卸载或已移除，无法启用。 / Plugin is pending uninstall or has been removed.");
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
            if (_exitSuspended || !plugin.Enabled || plugin.PendingUninstall || !_plugins.Contains(plugin)) throw new InvalidOperationException("Plugin is no longer active.");
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
        if (_disposed || _busy || _exitSuspended) return;
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
                    if (_exitSuspended || !plugin.Enabled || plugin.Error is not null) continue;
                    plugin.Values = ValidateResult(plugin, result, context.Timestamp);
                    plugin.OverviewValues = result.OverviewValues ?? new Dictionary<string, string?>();
                    plugin.LastResultAt = context.Timestamp;
                    foreach (var command in result.Commands ?? [])
                        await PluginHostBridge.ExecuteAsync(_runtime, plugin.Manifest.Permissions, command);
                    if (result.Toast is { } toast) ShowToast(plugin, toast);
                }
                catch (Exception ex)
                {
                    if (_exitSuspended) continue;
                    plugin.Error ??= ex.GetBaseException().Message; plugin.Values = [];
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
        if (result.Toast is { } toast) ValidateToast(plugin, toast);
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
    private static void ValidateToast(PluginInstallation plugin, PluginToast toast)
    {
        if (!plugin.Manifest.Permissions.Contains("ui.toast")) throw new UnauthorizedAccessException("Plugin has no ui.toast permission.");
        if (string.IsNullOrWhiteSpace(toast.Message) || toast.Message.Length > 512)
            throw new ArgumentException("Toast text must contain 1–512 characters.");
    }
    internal bool ShowToast(PluginInstallation plugin, PluginToast toast)
    {
        ValidateToast(plugin, toast);
        if (_disposed || _exitSuspended || !plugin.Enabled || plugin.PendingUninstall || plugin.Error is not null || !_plugins.Contains(plugin))
            throw new InvalidOperationException("Plugin is not active.");
        var now = Stopwatch.GetTimestamp();
        var key = toast.IsError + ":" + toast.Message.Trim();
        if (plugin.LastToastTimestamp != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(plugin.LastToastTimestamp, now);
            if (elapsed < TimeSpan.FromSeconds(1) || plugin.LastToastKey == key && elapsed < TimeSpan.FromSeconds(10)) return false;
        }
        plugin.LastToastTimestamp = now; plugin.LastToastKey = key;
        _runtime.PublishPluginToast(new(plugin.Manifest.Id, plugin.Manifest.Name.Replace('\r', ' ').Replace('\n', ' '), toast.Message.Trim(), toast.IsError));
        return true;
    }
    internal void ReportUiFailure(PluginInstallation plugin, Exception error)
    {
        if (_disposed || plugin.Error is not null) return;
        plugin.Error = "自绘页面异常 / Custom page failed: " + error.GetBaseException().Message;
        plugin.Client?.Dispose(); plugin.Client = null; plugin.Values = [];
        plugin.OverviewValues = new Dictionary<string, string?>();
        ToolkitLog.Error(plugin.Error, error);
        // Do not tear down navigation in the middle of CreatePage/Update/Dispose.
        if (_timer.Dispatcher.HasShutdownStarted || _timer.Dispatcher.HasShutdownFinished) return;
        _timer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            Publish(); CatalogChanged?.Invoke(this, EventArgs.Empty);
            _runtime.SetStatus(plugin.Error ?? "");
        }));
    }
    private string SettingsPath(PluginInstallation plugin) => Path.Combine(StateRoot, plugin.Manifest.Id + ".json");
    private void LoadPluginSettings(PluginInstallation plugin)
    {
        foreach (var setting in plugin.Manifest.Settings) plugin.Settings[setting.Id] = setting.DefaultValue.Clone();
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath(plugin)));
            foreach (var pair in saved ?? [])
                if (plugin.Manifest.Settings.FirstOrDefault(s => s.Id == pair.Key) is { } descriptor && ValidSetting(descriptor, pair.Value))
                    plugin.Settings[pair.Key] = pair.Value.Clone();
        }
        catch { }
    }
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
