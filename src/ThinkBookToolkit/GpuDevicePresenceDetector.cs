using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

internal sealed record GpuDevicePresenceSnapshot(
    int Generation,
    IReadOnlyList<string> ActiveNames,
    bool Reliable)
{
    public bool IsActive(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
            return false;
        if (!Reliable)
            return true;
        var normalized = GpuDevicePresenceDetector.Normalize(gpuName);
        return ActiveNames.Any(name =>
            GpuDevicePresenceDetector.Normalize(name).Contains(
                normalized,
                StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                GpuDevicePresenceDetector.Normalize(name),
                StringComparison.OrdinalIgnoreCase));
    }
}

internal static class GpuDevicePresenceDetector
{
    private static readonly object Sync = new();
    private static DateTimeOffset _lastRefresh;
    private static string[] _activeNames = [];
    private static string _signature = string.Empty;
    private static bool _hasSuccessfulSnapshot;

    public static int Generation { get; private set; }

    public static bool IsActive(string gpuName)
    {
        return Capture().IsActive(gpuName);
    }

    public static GpuDevicePresenceSnapshot Capture()
    {
        RefreshIfNeeded();
        lock (Sync)
        {
            return new GpuDevicePresenceSnapshot(
                Generation,
                _hasSuccessfulSnapshot ? _activeNames.ToArray() : [],
                _hasSuccessfulSnapshot);
        }
    }

    private static void RefreshIfNeeded()
    {
        lock (Sync)
        {
            if (DateTimeOffset.UtcNow - _lastRefresh < TimeSpan.FromSeconds(1))
                return;
            _lastRefresh = DateTimeOffset.UtcNow;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, ConfigManagerErrorCode FROM Win32_VideoController");
                var names = new List<string>();
                foreach (ManagementObject item in searcher.Get())
                {
                    var errorCode = Convert.ToUInt32(
                        item["ConfigManagerErrorCode"] ?? 0u);
                    var name = item["Name"]?.ToString();
                    if (errorCode == 0 && !string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                _hasSuccessfulSnapshot = true;
                var signature = string.Join("|", names);
                if (!string.Equals(signature, _signature, StringComparison.Ordinal))
                {
                    _signature = signature;
                    _activeNames = names.ToArray();
                    Generation++;
                    ToolkitLog.Info(
                        "Active display adapters changed: " +
                        (_activeNames.Length == 0
                            ? "none detected"
                            : string.Join(", ", _activeNames)));
                }
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "Display-adapter presence check failed: " + ex.Message);
            }
        }
    }

    internal static string Normalize(string value) => value
        .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("GeForce", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Laptop GPU", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();
}
