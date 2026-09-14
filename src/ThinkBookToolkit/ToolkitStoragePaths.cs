using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ThinkBookToolkit;

internal sealed record StorageLocations(string Dependency, string Configuration, string Logs,
    string Downloads, string Recordings);
internal sealed record StorageLocationManifest(StorageLocations Active, StorageLocations? Pending = null);

internal static class ToolkitStoragePaths
{
    private const string EnvironmentKey = "TBT_STORAGE_LOCATIONS";
    internal static string LocatorDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thinkbook_toolkit");
    internal static string LocatorPath => Path.Combine(LocatorDirectory, "folder_locations.json");
    internal static StorageLocations Defaults => new(Path.Combine(AppContext.BaseDirectory, "dependency"),
        LocatorDirectory, Path.Combine(LocatorDirectory, "log"),
        Path.Combine(LocatorDirectory, "driver_update_cache"), Path.Combine(LocatorDirectory, "sensor-recordings"));
    private static StorageLocations? _current;
    internal static StorageLocations Current => _current ??= LoadCurrent();
    internal static string Configuration => Current.Configuration;
    internal static string Logs => Current.Logs;
    internal static string Downloads => Current.Downloads;
    internal static string Recordings => Current.Recordings;
    internal static string Dependency => Current.Dependency;
    internal static string ResolveDependency(string relativePath)
    {
        var candidates = new[] { Path.Combine(Dependency, relativePath),
            Path.Combine(Dependency, "IntelPower", relativePath), Path.Combine(AppContext.BaseDirectory, relativePath) };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }

    private static StorageLocations LoadCurrent()
    {
        try
        {
            var inherited = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (!string.IsNullOrWhiteSpace(inherited))
                return JsonSerializer.Deserialize<StorageLocations>(inherited) ?? Defaults;
        }
        catch { }
        var current = ReadManifest().Active;
        Environment.SetEnvironmentVariable(EnvironmentKey, JsonSerializer.Serialize(current));
        return current;
    }

    internal static StorageLocationManifest ReadManifest()
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<StorageLocationManifest>(File.ReadAllText(LocatorPath));
            return manifest is null ? new(Defaults) : new(Validate(manifest.Active),
                manifest.Pending is null ? null : Validate(manifest.Pending));
        }
        catch { return new(Defaults); }
    }

    internal static StorageLocations Validate(StorageLocations locations)
    {
        string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("请选择完整的文件夹路径。");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不能将磁盘根目录作为程序数据文件夹。");
            return full;
        }
        var result = new StorageLocations(Normalize(locations.Dependency), Normalize(locations.Configuration),
            Normalize(locations.Logs), Normalize(locations.Downloads), Normalize(locations.Recordings));
        if (Values(result).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            throw new ArgumentException("五种用途不能共用同一个文件夹。");
        return result;
    }

    internal static string[] Values(StorageLocations locations) =>
        [locations.Dependency, locations.Configuration, locations.Logs, locations.Downloads, locations.Recordings];

    internal static void Schedule(StorageLocations pending, string? legacyDependency = null)
    {
        pending = Validate(pending);
        var active = !File.Exists(LocatorPath) && legacyDependency is not null && Directory.Exists(legacyDependency)
            ? Current with { Dependency = Path.GetFullPath(legacyDependency) } : Current;
        var sources = Values(active);
        var destinations = Values(pending);
        for (var i = 0; i < sources.Length; i++)
        {
            if (string.Equals(sources[i], destinations[i], StringComparison.OrdinalIgnoreCase)) continue;
            if (destinations[i].StartsWith(Path.TrimEndingDirectorySeparator(sources[i]) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                sources[i].StartsWith(destinations[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("新旧文件夹不能互相嵌套：" + destinations[i]);
            for (var j = 0; j < sources.Length; j++)
                if (i != j && string.Equals(destinations[i], sources[j], StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("不能使用另一种用途当前占用的文件夹：" + destinations[i]);
        }
        foreach (var directory in Values(pending))
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".tbt-write-test-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        WriteManifest(new(active, pending));
    }

    internal static void CancelPending() => WriteManifest(new(Current));

    // Called only by the desktop app after acquiring its single-instance lock.
    // Child workers inherit the running session's paths, not pending locations.
    internal static void ApplyPendingAtStartup()
    {
        var manifest = ReadManifest();
        var active = manifest.Active;
        if (manifest.Pending is { } next)
        {
            next = Validate(next);
            var source = Values(active);
            var target = Values(next);
            for (var i = 0; i < source.Length; i++)
                CopyPreservingExisting(source[i], target[i], i == 1,
                    Values(active).Where((_, index) => index != i));
            WriteManifest(new(next));
            active = next;
        }
        _current = active;
        Environment.SetEnvironmentVariable(EnvironmentKey, JsonSerializer.Serialize(active));
    }

    internal static void CopyPreservingExisting(string source, string target, bool configuration = false,
        IEnumerable<string>? excludedDirectories = null)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return;
        var excluded = (excludedDirectories ?? []).Select(Path.GetFullPath).Append(target).ToArray();
        void CopyDirectory(string from, string to)
        {
            if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) return;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                    string.Equals(file, LocatorPath, StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(to, Path.GetFileName(file));
                FileStream input;
                try { input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException ex) when (!configuration && (ex.HResult & 0xFFFF) is 32 or 33) { continue; }
                using var inputScope = input;
                if (File.Exists(destination))
                {
                    using var existing = File.OpenRead(destination);
                    if (!System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(
                        System.Security.Cryptography.SHA256.HashData(existing)))
                        throw new IOException("目标存在内容不同的同名文件，未覆盖：" + destination);
                    continue;
                }
                var temporary = destination + ".tbt-copy-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
                    File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(file));
                    File.Move(temporary, destination, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (var child in Directory.EnumerateDirectories(from))
            {
                if (excluded.Contains(child, StringComparer.OrdinalIgnoreCase)) continue;
                CopyDirectory(child, Path.Combine(to, Path.GetFileName(child)));
            }
        }
        CopyDirectory(source, target);
    }

    private static void WriteManifest(StorageLocationManifest manifest)
    {
        Directory.CreateDirectory(LocatorDirectory);
        var temporary = LocatorPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, LocatorPath, true);
    }
}
