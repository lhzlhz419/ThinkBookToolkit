using System;
using System.Globalization;
using System.Management;

namespace ThinkBookToolkit;

internal readonly record struct PendingGpuModeTransition(
    GpuWorkingMode Source,
    GpuWorkingMode Target,
    bool SourceUsesDirectGraphicsConfiguration,
    GpuControlProtocol Protocol,
    bool ParentStaged,
    bool ChildStaged,
    int PostBootAttempts);

internal static class GpuModeRestartState
{
    private static readonly Lazy<string> BootSessionId = new(ReadBootSessionId);

    public static string CurrentBootSessionId => BootSessionId.Value;

    public static bool TryParsePendingMode(
        string? value,
        out GpuWorkingMode mode) =>
        Enum.TryParse(value, out mode) && Enum.IsDefined(mode);

    public static bool HasRestartedSince(
        string? pendingBootSessionId,
        string currentBootSessionId) =>
        string.IsNullOrWhiteSpace(pendingBootSessionId) ||
        !string.Equals(
            pendingBootSessionId,
            currentBootSessionId,
            StringComparison.Ordinal);

    public static bool ShouldClearAfterReadback(
        string? pendingMode,
        string? pendingBootSessionId,
        string currentBootSessionId,
        GpuWorkingMode currentMode) =>
        TryParsePendingMode(pendingMode, out var pending) &&
        pending == currentMode &&
        HasRestartedSince(pendingBootSessionId, currentBootSessionId);

    public static bool TryGetTransition(
        AppSettings settings,
        out PendingGpuModeTransition transition)
    {
        if (TryParsePendingMode(settings.PendingGpuModeSource, out var source) &&
            TryParsePendingMode(settings.PendingGpuMode, out var target) &&
            settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration is
                { } sourceUsesDirectGraphicsConfiguration)
        {
            _ = Enum.TryParse<GpuControlProtocol>(
                settings.PendingGpuModeProtocol,
                out var protocol);
            transition = new(
                source,
                target,
                sourceUsesDirectGraphicsConfiguration,
                protocol,
                settings.PendingGpuModeParentStaged,
                settings.PendingGpuModeChildStaged,
                settings.PendingGpuModePostBootAttempts);
            return true;
        }

        transition = default;
        return false;
    }

    public static bool TryGetCurrentBootTransition(
        AppSettings settings,
        string currentBootSessionId,
        out PendingGpuModeTransition transition) =>
        TryGetTransition(settings, out transition) &&
        !HasRestartedSince(
            settings.PendingGpuModeBootSessionId,
            currentBootSessionId);

    public static bool TryGetCurrentBootTarget(
        AppSettings settings,
        string currentBootSessionId,
        out GpuWorkingMode target) =>
        TryParsePendingMode(settings.PendingGpuMode, out target) &&
        !HasRestartedSince(
            settings.PendingGpuModeBootSessionId,
            currentBootSessionId);

    public static bool RequiresRestart(
        GpuWorkingMode effectiveMode,
        bool effectiveUsesDirectGraphicsConfiguration,
        GpuWorkingMode target,
        bool targetUsesDirectGraphicsConfiguration) =>
        effectiveMode != target &&
        (effectiveUsesDirectGraphicsConfiguration ||
         targetUsesDirectGraphicsConfiguration);

    public static void MarkPending(
        AppSettings settings,
        GpuWorkingMode source,
        bool sourceUsesDirectGraphicsConfiguration,
        GpuWorkingMode target,
        string bootSessionId,
        GpuModeApplyResult? result = null)
    {
        settings.PendingGpuMode = target.ToString();
        settings.PendingGpuModeSource = source.ToString();
        settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration =
            sourceUsesDirectGraphicsConfiguration;
        settings.PendingGpuModeBootSessionId = bootSessionId;
        settings.PendingGpuModeProtocol =
            (result?.Protocol ?? GpuControlProtocol.Unsupported).ToString();
        settings.PendingGpuModeParentStaged = result?.ParentStaged ?? true;
        settings.PendingGpuModeChildStaged = result?.ChildStaged ?? false;
        settings.PendingGpuModePostBootAttempts = 0;
        settings.PendingGpuModeLastError = result?.Warning ?? string.Empty;
        settings.LastGpuModeFailure = string.Empty;
    }

    public static void Clear(AppSettings settings)
    {
        settings.PendingGpuMode = string.Empty;
        settings.PendingGpuModeSource = string.Empty;
        settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration = null;
        settings.PendingGpuModeBootSessionId = string.Empty;
        settings.PendingGpuModeProtocol = string.Empty;
        settings.PendingGpuModeParentStaged = false;
        settings.PendingGpuModeChildStaged = false;
        settings.PendingGpuModePostBootAttempts = 0;
        settings.PendingGpuModeLastError = string.Empty;
        settings.LastGpuModeFailure = string.Empty;
    }

    public static void MarkFailed(AppSettings settings, string error)
    {
        Clear(settings);
        settings.LastGpuModeFailure = error;
    }

    private static string ReadBootSessionId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var value = Convert.ToString(item["LastBootUpTime"]);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }
        catch
        {
        }

        var uptime = TimeSpan.FromMilliseconds(
            Math.Max(0L, Environment.TickCount64));
        var estimatedBoot = DateTimeOffset.UtcNow - uptime;
        return "estimated:" +
               (estimatedBoot.ToUnixTimeSeconds() / 60)
               .ToString(CultureInfo.InvariantCulture);
    }
}
