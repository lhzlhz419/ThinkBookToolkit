using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ThinkBookToolkit;

public sealed class GameProcessDetector
{
    private const string GameConfigStorePath = @"System\GameConfigStore\Children";
    private const string MatchedExeFullPathName = "MatchedExeFullPath";

    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost",
        "backgroundtaskhost",
        "cmd",
        "conhost",
        "crashpad_handler",
        "csrss",
        "dllhost",
        "dwm",
        "explorer",
        "lockapp",
        "powershell",
        "searchhost",
        "searchui",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "steamwebhelper",
        "svchost",
        "taskhostw",
        "textinputhost",
        "werfault",
        "wmiapsrv",
        "wmiprvse"
    };

    private readonly HashSet<string> _knownGamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string?>> _knownGamesByName = new(StringComparer.OrdinalIgnoreCase);

    public int KnownGameCount => _knownGamePaths.Count;

    public GameProcessDetector()
    {
        ReloadKnownGames();
    }

    public void ReloadKnownGames()
    {
        _knownGamePaths.Clear();
        _knownGamesByName.Clear();

        using var root = Registry.CurrentUser.OpenSubKey(GameConfigStorePath, writable: false);
        if (root is null)
            return;

        foreach (var childName in root.GetSubKeyNames())
        {
            using var child = root.OpenSubKey(childName, writable: false);
            var path = child?.GetValue(MatchedExeFullPathName) as string;
            AddKnownGamePath(path);
        }
    }

    public bool AreGamesRunning()
    {
        foreach (var process in ReadProcesses())
        {
            if (IsGameProcess(process.ProcessId, process.ParentProcessId, process.Name, process.Path, trackedParents: null))
                return true;
        }

        return false;
    }

    private bool IsGameProcess(int processId, int parentProcessId, string name, string? path, HashSet<int>? trackedParents)
    {
        var normalizedName = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalizedName) || IgnoredProcessNames.Contains(normalizedName))
            return false;

        if (trackedParents is not null && trackedParents.Contains(parentProcessId))
            return true;

        var normalizedPath = NormalizePath(path);
        if (!string.IsNullOrWhiteSpace(normalizedPath) && _knownGamePaths.Contains(normalizedPath))
            return true;

        if (!_knownGamesByName.TryGetValue(normalizedName, out var knownPaths))
            return false;

        return knownPaths.Any(knownPath => knownPath is null ||
                                           (!string.IsNullOrWhiteSpace(normalizedPath) &&
                                            string.Equals(knownPath, normalizedPath, StringComparison.OrdinalIgnoreCase)));
    }

    private void AddKnownGamePath(string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        var name = NormalizeName(Path.GetFileName(normalizedPath));
        if (string.IsNullOrWhiteSpace(name))
            return;

        _knownGamePaths.Add(normalizedPath);
        if (!_knownGamesByName.TryGetValue(name, out var paths))
        {
            paths = [];
            _knownGamesByName[name] = paths;
        }
        paths.Add(normalizedPath);
    }

    private static IEnumerable<ProcessRecord> ReadProcesses()
    {
        var records = new List<ProcessRecord>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                records.Add(new ProcessRecord(
                    process.Id,
                    0,
                    process.ProcessName,
                    path));
            }
        }
        return records;
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        return Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            return path.Trim().TrimEnd('\\').ToLowerInvariant();
        }
    }

    private sealed record ProcessRecord(int ProcessId, int ParentProcessId, string Name, string? Path);
}
