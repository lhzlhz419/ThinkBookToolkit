using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

/// <summary>Extracts data only. Plugin assemblies are never loaded during import.</summary>
internal sealed class PluginArchiveImporter : IDisposable
{
    private const long MaximumBytes = 256L * 1024 * 1024;
    private readonly string _staging;
    internal string Directory { get; private set; } = "";
    internal PluginManifest Manifest { get; private set; } = null!;
    internal string Fingerprint { get; private set; } = "";

    private PluginArchiveImporter(string staging) => _staging = staging;

    internal static PluginArchiveImporter Prepare(string archivePath, string pluginRoot, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 ZIP 插件压缩包。 / Select a ZIP plugin archive.");
        var root = Path.GetFullPath(pluginRoot);
        EnsureNoLinks(root);
        var imports = Path.Combine(root, ".imports");
        EnsureNoLinks(imports);
        System.IO.Directory.CreateDirectory(imports);
        var package = new PluginArchiveImporter(Path.Combine(imports, Guid.NewGuid().ToString("N")));
        try
        {
            using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("插件压缩包超过 256 MB。 / Plugin archive exceeds 256 MB.");
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count is 0 or > 4096) throw new InvalidDataException("压缩包条目数量无效。 / Invalid archive entry count.");
            var entries = new List<(ZipArchiveEntry Entry, string Name, bool IsDirectory)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0; var files = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                var isDirectory = name.EndsWith('/');
                var parts = (isDirectory ? name[..^1] : name).Split('/');
                if (name.Length > 1024 || parts.Any(p => !ValidSegment(p)))
                    throw new InvalidDataException("压缩包包含不安全的路径。 / Archive contains an unsafe path: " + entry.FullName);
                var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不允许链接或特殊文件。 / Archive links and special files are not allowed.");
                var normalized = string.Join('/', parts);
                if (!names.Add(normalized)) throw new InvalidDataException("压缩包含重复路径。 / Duplicate archive path: " + normalized);
                if (isDirectory && entry.Length != 0) throw new InvalidDataException("Invalid directory entry.");
                if (!isDirectory)
                {
                    total = checked(total + entry.Length);
                    if (++files > 2048 || total > MaximumBytes) throw new InvalidDataException("解压后最多 2048 个文件、256 MB。 / Expanded package limit: 2048 files, 256 MB.");
                }
                entries.Add((entry, normalized, isDirectory));
            }
            var manifests = entries.Where(e => !e.IsDirectory && Path.GetFileName(e.Name).Equals("plugin.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length != 1 || manifests[0].Entry.Length > 1024 * 1024)
                throw new InvalidDataException("压缩包必须包含唯一的 plugin.json（不超过 1 MB）。 / Archive must contain exactly one plugin.json, at most 1 MB.");
            var manifestName = manifests[0].Name;
            var slash = manifestName.LastIndexOf('/');
            var prefix = slash < 0 ? "" : manifestName[..(slash + 1)];
            if (entries.Any(e => !e.IsDirectory && !e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("所有插件文件必须位于 plugin.json 所在文件夹内。 / All plugin files must be inside the manifest folder.");

            System.IO.Directory.CreateDirectory(package._staging);
            total = 0;
            foreach (var (entry, name, isDirectory) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(package._staging, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(package._staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archive path escapes staging.");
                EnsureNoLinks(target);
                if (isDirectory) { System.IO.Directory.CreateDirectory(target); continue; }
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; int read; long written = 0;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read; written += read;
                    if (total > MaximumBytes || written > entry.Length) throw new InvalidDataException("Expanded plugin size exceeds its declared limit.");
                    destination.Write(buffer, 0, read);
                }
                if (written != entry.Length) throw new InvalidDataException("Incomplete archive entry.");
            }
            var manifestPath = Path.Combine(package._staging, manifestName.Replace('/', Path.DirectorySeparatorChar));
            package.Directory = Path.GetDirectoryName(manifestPath)!;
            package.Manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Empty plugin manifest.");
            ToolkitPluginManager.ValidateManifest(package.Manifest, package.Directory);
            if (!ValidSegment(package.Manifest.Id)) throw new InvalidDataException("插件 ID 不能用作文件夹名称。 / Plugin ID is not a valid directory name.");
            package.Fingerprint = PluginFanBackendPackage.Fingerprint(package.Directory);
            cancellationToken.ThrowIfCancellationRequested();
            return package;
        }
        catch { package.Dispose(); throw; }
    }

    private static bool ValidSegment(string part) => !string.IsNullOrWhiteSpace(part) && part is not ("." or "..") &&
        !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase);

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((System.IO.Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("插件导入路径不允许目录链接。 / Plugin import paths cannot contain links.");
    }

    internal void Commit(string destination)
    {
        EnsureNoLinks(Directory); EnsureNoLinks(destination);
        // The destination is new: never merge with or overwrite an existing plugin.
        System.IO.Directory.Move(Directory, destination);
    }

    public void Dispose()
    {
        try
        {
            if (!System.IO.Directory.Exists(_staging)) return;
            EnsureNoLinks(_staging);
            // Only this import's generated staging directory is disposable.
            // Directory.Delete removes nested reparse points themselves, not their targets.
            System.IO.Directory.Delete(_staging, recursive: true);
        }
        catch (Exception ex) { ToolkitLog.Warning("Could not clean plugin import staging directory: " + ex.Message); }
    }
}
