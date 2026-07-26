using System;
using System.IO;

namespace ThinkBookToolkit;

internal static class LenovoDependencyDirectory
{
    private static readonly object SyncRoot = new();
    private static bool _enabled;
    private static string _configuredRoot = string.Empty;

    public static void Configure(AppSettings settings) =>
        Configure(
            settings.UseCustomLenovoDllDirectory,
            settings.CustomLenovoDllDirectory);

    public static void Configure(bool enabled, string? configuredRoot)
    {
        lock (SyncRoot)
        {
            _enabled = enabled;
            _configuredRoot = Normalize(configuredRoot);
        }
    }

    public static string? GetEnabledRoot()
    {
        lock (SyncRoot)
        {
            return _enabled && Directory.Exists(_configuredRoot)
                ? _configuredRoot
                : null;
        }
    }

    internal static string Normalize(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return string.Empty;

        try
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configuredRoot.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string? FindExistingFile(
        string? customRoot,
        string relativePath,
        params string[] fallbackPaths)
    {
        var normalizedRoot = Normalize(customRoot);
        if (!string.IsNullOrWhiteSpace(normalizedRoot))
        {
            var customPath = Path.Combine(normalizedRoot, relativePath);
            if (File.Exists(customPath))
                return customPath;
        }

        return Array.Find(fallbackPaths, File.Exists);
    }
}
