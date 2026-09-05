using System;
using System.IO;
using System.IO.Compression;

namespace ThinkBookToolkit;

internal static class SensorRecordingArchive
{
    public const string Extension = ".jsonl.gz";

    public static bool IsCompressed(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static string CompressAndDeleteSource(string sourcePath)
    {
        var archivePath = sourcePath.EndsWith(
            ".jsonl",
            StringComparison.OrdinalIgnoreCase)
            ? sourcePath + ".gz"
            : sourcePath + Extension;
        var temporaryArchive = archivePath + ".tmp-" +
                               Guid.NewGuid().ToString("N");
        try
        {
            using (var source = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       128 * 1024,
                       FileOptions.SequentialScan))
            using (var target = new FileStream(
                       temporaryArchive,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.SequentialScan))
            using (var gzip = new GZipStream(
                       target,
                       CompressionLevel.Optimal,
                       leaveOpen: false))
            {
                source.CopyTo(gzip, 128 * 1024);
            }
            File.Move(temporaryArchive, archivePath, overwrite: true);
            File.Delete(sourcePath);
            return archivePath;
        }
        catch
        {
            try { File.Delete(temporaryArchive); } catch { }
            throw;
        }
    }

    public static string ExtractToTemporary(string archivePath)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ThinkBookToolkit",
            "sensor-recordings");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            "sensors-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var source = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            using var gzip = new GZipStream(
                source,
                CompressionMode.Decompress,
                leaveOpen: false);
            using var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan);
            gzip.CopyTo(target, 128 * 1024);
            return temporaryPath;
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static void DeleteTemporary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { File.Delete(path); } catch { }
    }
}
