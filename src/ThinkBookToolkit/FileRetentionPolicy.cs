using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ThinkBookToolkit;

internal static class FileRetentionPolicy
{
    internal static readonly int[] Days = [1, 2, 3, 5, 7, 14, 30, 60, 0];
    internal static int Normalize(int days, int fallback) => Days.Contains(days) ? days : fallback;
    private static readonly Regex LogName = new(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}_(?:(?:gpu-worker|watchdog)_)?\d+\.log$", RegexOptions.CultureInvariant);
    private static readonly Regex RecordingName = new(@"^sensors-\d{8}-\d{6}-\d{3}\.jsonl(?:\.gz)?$", RegexOptions.CultureInvariant);

    internal static int Cleanup(string directory, int days, bool recordings, string? currentPath = null,
        DateTime? utcNow = null)
    {
        if (days <= 0 || !Days.Contains(days) || !Directory.Exists(directory)) return 0;
        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-days);
        var removed = 0;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!(recordings ? RecordingName : LogName).IsMatch(Path.GetFileName(path)) ||
                    string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    // Deny other readers/writers while allowing this handle's
                    // delete. Any active writer (including another process)
                    // makes this open fail, even if it allows FileShare.Delete.
                    using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                    if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return removed;
    }
}
