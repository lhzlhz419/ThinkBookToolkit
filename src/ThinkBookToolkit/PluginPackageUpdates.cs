using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed record PluginPackageChange(string Id, string Destination, string OriginalFingerprint,
    string NewFingerprint, string StagedDirectory, string BackupDirectory, string SettingsBackup, string? RestoreSettings);
internal sealed record PluginPackageBackup(string Id, string Directory, string Fingerprint, string SettingsBackup);

internal sealed partial class ToolkitPluginManager
{
    private string UpdatesPath => Path.Combine(StateRoot, "package-state", "pending-updates.json");
    private string BackupsPath => Path.Combine(StateRoot, "package-state", "backups.json");
    private string SchemaPath(string id) => Path.Combine(StateRoot, "package-state", "schemas", id + ".json");
    private List<PluginPackageChange> _updates = [];
    private List<PluginPackageBackup> _backups = [];
    internal bool HasPendingUpdates => _updates.Count > 0;
    internal bool CanRollback(PluginInstallation plugin) => _backups.Any(b => b.Id == plugin.Manifest.Id);

    internal async Task CancelPendingUpdateAsync(PluginInstallation plugin)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            var change = _updates.SingleOrDefault(u => u.Id == plugin.Manifest.Id);
            if (change is null) return;
            if (Directory.Exists(change.BackupDirectory) || !Directory.Exists(plugin.Directory))
                throw new InvalidOperationException("文件切换已开始，请重启以完成恢复。 / File switching has begun. Restart to finish recovery.");
            // Revoke the task first. Never touch the installed plugin or grant a new approval.
            var remaining = _updates.Where(u => u != change).ToList();
            SaveJson(UpdatesPath, remaining);
            _updates = remaining;
            plugin.PendingUpdate = false;
            var stagingRoot = Path.Combine(Root, ".updates");
            var stage = Path.GetFullPath(change.StagedDirectory);
            if (string.Equals(Path.GetDirectoryName(stage), Path.GetFullPath(stagingRoot), StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(stage), "N", out _) && Directory.Exists(stage))
            {
                try
                {
                    EnsurePackagePath(stage);
                    if (Fingerprint(stage) == change.NewFingerprint) Directory.Delete(stage, recursive: true);
                }
                catch (Exception ex) { ToolkitLog.Warning("Could not remove cancelled update staging: " + ex.Message); }
            }
        }
        finally { _gate.Release(); }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<bool> StageUpdateAsync(PluginInstallation plugin, string archive,
        Func<PluginManifest, bool> confirm)
    {
        using var package = await Task.Run(() => PluginArchiveImporter.Prepare(archive, Root, _stop.Token));
        if (package.Manifest.Id != plugin.Manifest.Id) throw new InvalidDataException("更新包 ID 与已安装插件不同。 / Update package ID does not match.");
        if (package.Fingerprint == plugin.Fingerprint) throw new InvalidOperationException("已安装相同插件包，无需更新。 / This exact package is already installed.");
        if (!confirm(package.Manifest)) return false;
        var stage = Path.Combine(Root, ".updates", Guid.NewGuid().ToString("N"));
        EnsurePackagePath(stage); Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        package.Commit(stage);
        await StagePackageChangeAsync(plugin, stage, package.Fingerprint, null);
        return true;
    }

    internal async Task<bool> StageRollbackAsync(PluginInstallation plugin, Func<PluginManifest, bool> confirm)
    {
        var backup = _backups.LastOrDefault(b => b.Id == plugin.Manifest.Id) ?? throw new InvalidOperationException("没有可回退的版本。 / No rollback version.");
        EnsurePackagePath(backup.Directory);
        if (Fingerprint(backup.Directory) != backup.Fingerprint) throw new InvalidDataException("Rollback package changed.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(backup.Directory, "plugin.json")))!;
        if (manifest.Id != plugin.Manifest.Id) throw new InvalidDataException("Rollback package ID does not match.");
        ValidateManifest(manifest, backup.Directory);
        if (!confirm(manifest)) return false;
        var stage = Path.Combine(Root, ".updates", Guid.NewGuid().ToString("N"));
        EnsurePackagePath(stage); Directory.CreateDirectory(stage);
        // Fingerprint has already rejected links. Never move/delete the backup.
        foreach (var file in Directory.EnumerateFiles(backup.Directory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(stage, Path.GetRelativePath(backup.Directory, file));
            EnsurePackagePath(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        if (Fingerprint(stage) != backup.Fingerprint) throw new InvalidDataException("Rollback copy verification failed.");
        await StagePackageChangeAsync(plugin, stage, backup.Fingerprint, backup.SettingsBackup);
        return true;
    }

    private async Task StagePackageChangeAsync(PluginInstallation plugin, string staged, string fingerprint, string? restoreSettings)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            if (!_plugins.Contains(plugin) || plugin.PendingUninstall || plugin.PendingUpdate || _exitSuspended) throw new InvalidOperationException("Plugin cannot be updated now.");
            EnsurePackagePath(plugin.Directory); EnsurePackagePath(staged);
            if (Fingerprint(plugin.Directory) != plugin.Fingerprint || Fingerprint(staged) != fingerprint) throw new InvalidDataException("Plugin files changed during update.");
            var ticket = Guid.NewGuid().ToString("N");
            var backupDirectory = Path.Combine(Root, ".rollback", plugin.Manifest.Id, ticket);
            var settingsBackup = Path.Combine(StateRoot, "backups", plugin.Manifest.Id, ticket + ".json");
            EnsureStatePath(settingsBackup); Directory.CreateDirectory(Path.GetDirectoryName(settingsBackup)!);
            // The snapshot is independent of the executable package and survives later migrations.
            File.WriteAllText(settingsBackup, File.Exists(SettingsPath(plugin)) ? File.ReadAllText(SettingsPath(plugin)) : "{}");
            var change = new PluginPackageChange(plugin.Manifest.Id, plugin.Directory, plugin.Fingerprint, fingerprint,
                staged, backupDirectory, settingsBackup, restoreSettings);
            var next = _updates.Append(change).ToList();
            SaveJson(UpdatesPath, next);
            _updates = next;
            _preferences[plugin.Manifest.Id] = new(false, plugin.Fingerprint);
            SaveJson(Path.Combine(StateRoot, "enabled.json"), _preferences);
            plugin.Generation++; plugin.PendingUpdate = true; plugin.Enabled = false; plugin.Values = [];
            plugin.Conditions.Clear(); plugin.Client?.Dispose(); plugin.Client = null;
        }
        finally { _gate.Release(); }
        Publish(); CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProcessPackageUpdates(bool safeMode)
    {
        try
        {
            _updates = File.Exists(UpdatesPath) ? JsonSerializer.Deserialize<List<PluginPackageChange>>(File.ReadAllText(UpdatesPath)) ?? [] : [];
            _backups = File.Exists(BackupsPath) ? JsonSerializer.Deserialize<List<PluginPackageBackup>>(File.ReadAllText(BackupsPath)) ?? [] : [];
            if (safeMode) return;
            foreach (var change in _updates.ToArray())
            {
                try
                {
                    EnsurePackagePath(change.Destination); EnsurePackagePath(change.StagedDirectory); EnsurePackagePath(change.BackupDirectory);
                    EnsureStatePath(change.SettingsBackup);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(change.Id, @"^[a-z0-9][a-z0-9._-]{0,100}$") ||
                        !string.Equals(Path.GetDirectoryName(Path.GetFullPath(change.Destination)), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(change.Destination).StartsWith('.'))
                        throw new InvalidDataException("Invalid plugin update destination.");
                    if (change.RestoreSettings is not null) EnsureStatePath(change.RestoreSettings);
                    if (Path.GetFileName(Path.GetDirectoryName(change.BackupDirectory)) != change.Id ||
                        !change.BackupDirectory.StartsWith(Path.Combine(Root, ".rollback") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        !change.StagedDirectory.StartsWith(Path.Combine(Root, ".updates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid plugin update journal paths.");
                    if (Directory.Exists(change.Destination) && Fingerprint(change.Destination) == change.NewFingerprint)
                    { FinishPackageChange(change); continue; }
                    if (Directory.Exists(change.Destination) && (Fingerprint(change.Destination) != change.OriginalFingerprint ||
                        ReadPackageId(change.Destination) != change.Id))
                        throw new InvalidDataException("Installed plugin changed; pending update was not applied.");
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(change.StagedDirectory, "plugin.json")))!;
                    if (manifest.Id != change.Id || Fingerprint(change.StagedDirectory) != change.NewFingerprint) throw new InvalidDataException("Staged update changed.");
                    ValidateManifest(manifest, change.StagedDirectory);
                    Directory.CreateDirectory(Path.GetDirectoryName(change.BackupDirectory)!);
                    if (Directory.Exists(change.Destination))
                    {
                        if (Directory.Exists(change.BackupDirectory)) throw new IOException("Backup destination already exists.");
                        Directory.Move(change.Destination, change.BackupDirectory);
                    }
                    else if (!Directory.Exists(change.BackupDirectory) || Fingerprint(change.BackupDirectory) != change.OriginalFingerprint)
                        throw new IOException("Cannot recover interrupted plugin update.");
                    try { Directory.Move(change.StagedDirectory, change.Destination); }
                    catch
                    {
                        if (!Directory.Exists(change.Destination)) Directory.Move(change.BackupDirectory, change.Destination);
                        throw;
                    }
                    FinishPackageChange(change);
                }
                catch (Exception ex) { _discoveryErrors.Add(change.Id + " 更新未完成 / Update pending: " + ex.GetBaseException().Message); }
            }
        }
        catch (Exception ex) { _discoveryErrors.Add("Cannot load plugin update journal: " + ex.GetBaseException().Message); }
    }

    private void FinishPackageChange(PluginPackageChange change)
    {
        if (ReadPackageId(change.Destination) != change.Id || !Directory.Exists(change.BackupDirectory) ||
            ReadPackageId(change.BackupDirectory) != change.Id || Fingerprint(change.BackupDirectory) != change.OriginalFingerprint)
            throw new InvalidDataException("Original package backup could not be verified.");
        if (change.RestoreSettings is not null)
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(change.RestoreSettings)) ?? throw new InvalidDataException("Invalid settings backup.");
            SaveJson(SettingsPath(change.Id), saved);
            var restored = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(change.Destination, "plugin.json")))!;
            SaveJson(SchemaPath(change.Id), restored.SettingsVersion);
        }
        var backup = new PluginPackageBackup(change.Id, change.BackupDirectory, change.OriginalFingerprint, change.SettingsBackup);
        if (!_backups.Any(b => b.Directory == backup.Directory)) _backups.Add(backup);
        SaveJson(BackupsPath, _backups);
        var preferencesPath = Path.Combine(StateRoot, "enabled.json");
        var preferences = File.Exists(preferencesPath) ? JsonSerializer.Deserialize<Dictionary<string, PluginPreference>>(File.ReadAllText(preferencesPath)) ?? [] : new();
        preferences[change.Id] = new(false, change.NewFingerprint);
        SaveJson(preferencesPath, preferences);
        var remaining = _updates.Where(u => u != change).ToList();
        SaveJson(UpdatesPath, remaining); _updates = remaining;
    }

    private void EnsurePackagePath(string path) => EnsureContainedPath(Root, path);
    private static string? ReadPackageId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "plugin.json")));
        return document.RootElement.GetProperty("Id").GetString();
    }
    private void EnsureStatePath(string path) => EnsureContainedPath(StateRoot, path);
    private static void EnsureContainedPath(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin update path escapes its storage root.");
        PluginArchiveImporter.EnsureNoLinks(full);
    }
}
