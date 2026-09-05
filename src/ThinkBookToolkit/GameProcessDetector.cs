using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace ThinkBookToolkit;

internal sealed record GameProcessCandidate(
    int ProcessId,
    string ProcessName,
    string? Path);

public sealed class GameProcessDetector : IDisposable
{
    private const string GameConfigStorePath = @"System\GameConfigStore\Children";
    private const string MatchedExeFullPathName = "MatchedExeFullPath";
    private static readonly TimeSpan ResultCacheDuration =
        TimeSpan.FromMilliseconds(500);

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
        "wmiprvse",
        "thinkbooktoolkit",
        "hwinfo32",
        "hwinfo64",
        "nvidia-smi"
    };

    private readonly HashSet<string> _knownGamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitGamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitGameNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string?>> _knownGamesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _pinnedProcessIds = [];
    private readonly EffectiveGameModeMonitor _effectiveGameMode = new();
    private readonly AppSettings? _settings;
    private readonly object _sync = new();
    private DateTimeOffset _nextGameConfigReload;
    private DateTimeOffset _lastDetection;
    private bool _lastDetectionResult;
    private DateTimeOffset _lastFpsCandidateDetection;
    private GameProcessCandidate? _lastFpsCandidate;
    private int? _lastFpsExcludedProcessId;

    public int KnownGameCount => _knownGamePaths.Count;

    public GameProcessDetector(AppSettings? settings = null)
    {
        _settings = settings;
        ReloadKnownGames();
        _effectiveGameMode.Start();
    }

    public void ReloadKnownGames()
    {
        lock (_sync)
            ReloadKnownGamesCore();
    }

    private void ReloadKnownGamesCore()
    {
        _knownGamePaths.Clear();
        _explicitGamePaths.Clear();
        _explicitGameNames.Clear();
        _knownGamesByName.Clear();
        _pinnedProcessIds.Clear();
        _lastDetection = DateTimeOffset.MinValue;
        _nextGameConfigReload = DateTimeOffset.UtcNow.AddSeconds(30);

        using var root = Registry.CurrentUser.OpenSubKey(GameConfigStorePath, writable: false);
        if (root is not null)
        {
            foreach (var childName in root.GetSubKeyNames())
            {
                using var child = root.OpenSubKey(childName, writable: false);
                var path = child?.GetValue(MatchedExeFullPathName) as string;
                AddKnownGamePath(path);
            }
        }
        foreach (var path in _settings?.IncludedGamePaths ?? [])
        {
            AddKnownGamePath(path);
            var normalized = NormalizePath(path);
            if (normalized is null)
                continue;
            _explicitGamePaths.Add(normalized);
            _explicitGameNames.Add(NormalizeName(normalized));
        }
        _lastFpsCandidateDetection = DateTimeOffset.MinValue;
        _lastFpsCandidate = null;
        _lastFpsExcludedProcessId = null;
    }

    internal GameProcessCandidate? FindRunningGameProcessForFps(
        int? excludedProcessId = null)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFpsCandidateDetection < TimeSpan.FromSeconds(1) &&
                _lastFpsExcludedProcessId == excludedProcessId)
                return _lastFpsCandidate;
            if (now >= _nextGameConfigReload)
                ReloadKnownGamesCore();
            var foregroundId = EffectiveGameModeMonitor.TryGetForegroundProcessId(
                out var value)
                ? value
                : 0;
            var candidates = new List<(GameProcessCandidate Candidate,
                bool Foreground, bool Explicit)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try { name = NormalizeName(process.ProcessName); }
                    catch { continue; }
                    string? path = null;
                    try { path = NormalizePath(process.MainModule?.FileName); }
                    catch { }
                    if (IsExcluded(path))
                        continue;
                    var explicitPath =
                        path is not null && _explicitGamePaths.Contains(path);
                    var nameKnown = _knownGamesByName.TryGetValue(
                        name,
                        out var knownPaths);
                    if (!explicitPath && !nameKnown)
                        continue;
                    var explicitGame = explicitPath ||
                        path is null && _explicitGameNames.Contains(name);
                    var pathMatches = explicitPath || knownPaths?.Any(knownPath =>
                        knownPath is null ||
                        path is not null && string.Equals(
                            knownPath,
                            path,
                            StringComparison.OrdinalIgnoreCase)) == true;
                    if (!pathMatches && !explicitGame)
                        continue;
                    candidates.Add((
                        new GameProcessCandidate(
                            process.Id,
                            process.ProcessName,
                            path),
                        process.Id == foregroundId,
                        explicitGame));
                }
            }
            _lastFpsCandidate = candidates
                .Where(candidate =>
                    candidate.Candidate.ProcessId != excludedProcessId)
                .OrderByDescending(candidate => candidate.Foreground)
                .ThenByDescending(candidate => candidate.Explicit)
                .Select(candidate => candidate.Candidate)
                .FirstOrDefault();
            _lastFpsCandidateDetection = now;
            _lastFpsExcludedProcessId = excludedProcessId;
            return _lastFpsCandidate;
        }
    }

    public bool AreGamesRunning()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastDetection < ResultCacheDuration)
                return _lastDetectionResult;
            if (now >= _nextGameConfigReload)
                ReloadKnownGamesCore();
            _lastDetectionResult = DetectGamesRunning();
            _lastDetection = now;
            return _lastDetectionResult;
        }
    }

    private bool DetectGamesRunning()
    {
        var processes = ReadProcesses().ToArray();
        var excludedIds = processes
            .Where(process => IsExcluded(process.Path))
            .Select(process => process.ProcessId)
            .ToHashSet();
        var activeIds = processes.Select(process => process.ProcessId).ToHashSet();
        _pinnedProcessIds.RemoveWhere(id =>
            !activeIds.Contains(id) || excludedIds.Contains(id));
        var tracked = new HashSet<int>(_pinnedProcessIds);
        foreach (var process in processes)
        {
            if (IsGameProcess(
                    process.ProcessId,
                    process.ParentProcessId,
                    process.Name,
                    process.Path,
                    trackedParents: null))
                tracked.Add(process.ProcessId);
        }
        if (_effectiveGameMode.IsActive &&
            EffectiveGameModeMonitor.TryGetForegroundProcessId(
                out var foregroundId) &&
            processes.FirstOrDefault(process =>
                process.ProcessId == foregroundId) is { } foreground &&
            !IsExcluded(foreground.Path) &&
            !IgnoredProcessNames.Contains(NormalizeName(foreground.Name)))
        {
            tracked.Add(foregroundId);
            _pinnedProcessIds.Add(foregroundId);
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var process in processes)
            {
                if (tracked.Contains(process.ProcessId) ||
                    !tracked.Contains(process.ParentProcessId) ||
                    IsExcluded(process.Path) ||
                    IgnoredProcessNames.Contains(NormalizeName(process.Name)))
                    continue;
                tracked.Add(process.ProcessId);
                _pinnedProcessIds.Add(process.ProcessId);
                changed = true;
            }
        } while (changed);
        return tracked.Count > 0;
    }

    private bool IsGameProcess(int processId, int parentProcessId, string name, string? path, HashSet<int>? trackedParents)
    {
        if (IsExcluded(path))
            return false;
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
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath " +
                "FROM Win32_Process");
            using var values = searcher.Get();
            foreach (ManagementObject process in values)
            {
                using (process)
                {
                    records.Add(new ProcessRecord(
                        Convert.ToInt32(process["ProcessId"]),
                        Convert.ToInt32(process["ParentProcessId"]),
                        Convert.ToString(process["Name"]) ?? string.Empty,
                        Convert.ToString(process["ExecutablePath"])));
                }
            }
        }
        catch
        {
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
        }
        return records;
    }

    private bool IsExcluded(string? path)
    {
        var normalized = NormalizePath(path);
        return normalized is not null &&
               (_settings?.ExcludedGamePaths ?? []).Any(item =>
                   string.Equals(
                       NormalizePath(item),
                       normalized,
                       StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _effectiveGameMode.Dispose();

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

internal sealed class EffectiveGameModeMonitor : IDisposable
{
    private const uint NotificationVersion = 2;
    private const int GameMode = 5;
    private readonly EffectivePowerModeCallback _callback;
    private IntPtr _handle;
    private int _active;

    public EffectiveGameModeMonitor()
    {
        _callback = OnChanged;
    }

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void Start()
    {
        try
        {
            if (PowerRegisterForEffectivePowerModeNotifications(
                    NotificationVersion,
                    _callback,
                    IntPtr.Zero,
                    out var handle) == 0)
                _handle = handle;
        }
        catch
        {
            _handle = IntPtr.Zero;
        }
    }

    private void OnChanged(int mode, IntPtr context) =>
        Volatile.Write(ref _active, mode == GameMode ? 1 : 0);

    public static bool TryGetForegroundProcessId(out int processId)
    {
        processId = 0;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        _ = GetWindowThreadProcessId(window, out var raw);
        processId = unchecked((int)raw);
        return processId > 0;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;
        try { _ = PowerUnregisterFromEffectivePowerModeNotifications(_handle); }
        catch { }
        _handle = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EffectivePowerModeCallback(int mode, IntPtr context);

    [DllImport("powrprof.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint PowerRegisterForEffectivePowerModeNotifications(
        uint version,
        EffectivePowerModeCallback callback,
        IntPtr context,
        out IntPtr registrationHandle);

    [DllImport("powrprof.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint PowerUnregisterFromEffectivePowerModeNotifications(
        IntPtr registrationHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
