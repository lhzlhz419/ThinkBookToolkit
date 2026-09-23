using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed record PendingPluginRemoval(string Root, string Folder, string PluginId, string Fingerprint, string Trash);
internal sealed class PluginRestartRequiredException(string message) : InvalidOperationException(message);

/// <summary>Removal journal contains root identifiers and leaf names, never arbitrary delete paths.</summary>
internal sealed class PluginRemovalStore(string userRoot, string bundledRoot, string stateRoot)
{
    private string Journal => Path.Combine(stateRoot, "pending-uninstall.json");
    private string Root(string key) => Path.GetFullPath(key switch
    {
        "user" => userRoot, "bundled" => bundledRoot, _ => throw new InvalidDataException("Invalid plugin removal root.")
    });
    internal List<PendingPluginRemoval> Read()
    {
        if (!File.Exists(Journal)) return [];
        if (new FileInfo(Journal).Length > 1024 * 1024) throw new InvalidDataException("Plugin removal journal is too large.");
        var items = JsonSerializer.Deserialize<List<PendingPluginRemoval>>(File.ReadAllText(Journal));
        if (items is null || items.Count > 256 || items.Any(item => item is null)) throw new InvalidDataException("Invalid plugin removal journal.");
        return items;
    }
    internal void Save(IReadOnlyList<PendingPluginRemoval> items)
    {
        if (items.Count > 256) throw new InvalidOperationException("Too many pending uninstalls; restart Toolkit first.");
        Directory.CreateDirectory(stateRoot);
        File.WriteAllText(Journal + ".tmp", JsonSerializer.Serialize(items));
        File.Move(Journal + ".tmp", Journal, true);
    }
    internal PendingPluginRemoval Plan(PluginInstallation plugin)
    {
        var directory = Path.GetFullPath(plugin.Directory).TrimEnd(Path.DirectorySeparatorChar);
        var key = new[] { "user", "bundled" }.FirstOrDefault(k =>
            string.Equals(Path.GetDirectoryName(directory), Root(k).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("只能卸载插件目录内的插件。 / Plugin is outside the managed plugin roots.");
        var record = new PendingPluginRemoval(key, Path.GetFileName(directory), plugin.Manifest.Id,
            PluginFanBackendPackage.Fingerprint(directory), Guid.NewGuid().ToString("N"));
        ValidateOriginal(record, directory);
        return record;
    }
    private static bool Leaf(string name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or ".." or ".imports" or ".uninstalled") &&
        Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
    private string Source(PendingPluginRemoval record)
    {
        if (!Leaf(record.Folder) || !Guid.TryParseExact(record.Trash, "N", out _)) throw new InvalidDataException("Unsafe plugin removal path.");
        return Path.Combine(Root(record.Root), record.Folder);
    }
    private static void ValidateOriginal(PendingPluginRemoval record, string directory)
    {
        PluginArchiveImporter.EnsureNoLinks(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "plugin.json")));
        if (manifest.RootElement.GetProperty("Id").GetString() != record.PluginId ||
            PluginFanBackendPackage.Fingerprint(directory) != record.Fingerprint)
            throw new InvalidDataException("插件文件已改变，未删除新内容。 / Plugin files changed; new contents were not deleted.");
    }
    internal bool Matches(PendingPluginRemoval record, string directory) =>
        string.Equals(Source(record), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);

    internal void Remove(PendingPluginRemoval record)
    {
        var source = Source(record);
        var trash = Path.Combine(Root(record.Root), ".uninstalled", record.Trash);
        PluginArchiveImporter.EnsureNoLinks(source); PluginArchiveImporter.EnsureNoLinks(trash);
        if (Directory.Exists(source))
        {
            ValidateOriginal(record, source);
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            // Move the exact verified package out of discovery atomically.
            // If deletion is interrupted, retry only this generated quarantine.
            Directory.Move(source, trash);
        }
        if (!Directory.Exists(trash)) return;
        VerifyTree(trash);
        Directory.Delete(trash, recursive: true);
    }
    private static void VerifyTree(string directory)
    {
        PluginArchiveImporter.EnsureNoLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links cannot be removed recursively as plugin contents.");
            if (Directory.Exists(entry)) VerifyTree(entry);
        }
    }
}
