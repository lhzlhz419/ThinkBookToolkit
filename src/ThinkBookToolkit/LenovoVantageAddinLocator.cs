using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ThinkBookToolkit;

internal static class LenovoVantageAddinLocator
{
    private const string InstalledAddinsRoot =
        @"C:\ProgramData\Lenovo\Vantage\Addins";
    private const string LocalAddinsDirectory = "VantageAddins";

    public static string? FindLatestFile(
        string addinName,
        string fileName)
    {
        var roots = new List<string>();
        var customRoot = LenovoDependencyDirectory.GetEnabledRoot();
        if (customRoot is not null)
        {
            roots.Add(Path.Combine(
                customRoot,
                LocalAddinsDirectory,
                addinName));
        }
        var localRoot = Path.Combine(
            AppContext.BaseDirectory,
            LocalAddinsDirectory,
            addinName);
        var installedRoot = Path.Combine(
            InstalledAddinsRoot,
            addinName);

        roots.Add(localRoot);
        roots.Add(installedRoot);
        return FindLatestFileInRoots(roots, fileName);
    }

    internal static string? FindLatestFileInRoots(
        IEnumerable<string> roots,
        string fileName) =>
        roots.Select(root => FindLatestFileInRoot(root, fileName))
            .FirstOrDefault(path => path is not null);

    private static string? FindLatestFileInRoot(
        string root,
        string fileName)
    {
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateDirectories(root)
            .Select(directory => new
            {
                Directory = directory,
                Version = ParseVersion(Path.GetFileName(directory))
            })
            .OrderByDescending(item => item.Version)
            .Select(item => Path.Combine(item.Directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version)
            ? version
            : new Version(0, 0);
}
