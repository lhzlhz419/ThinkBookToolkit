using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record DriverUpdateInstallPlan(
    Uri InstallerUri,
    string FileName,
    string Sha256,
    string Arguments,
    IReadOnlyList<int> SuccessExitCodes);

internal sealed record DriverUpdateItem(
    string PackageId,
    string Name,
    string Version,
    string CurrentVersion,
    string Category,
    string Severity,
    string RebootType,
    long SizeBytes,
    string ReleaseDate,
    bool IsUpdateRequired = true,
    DriverUpdateInstallPlan? InstallPlan = null);

internal sealed record DriverUpdateScanResult(
    string Status,
    IReadOnlyList<DriverUpdateItem> Updates);

internal sealed record DriverUpdateInstallResult(
    string Status,
    bool RebootNeeded,
    IReadOnlyList<string> FailedPackageIds);

internal static class DriverUpdateController
{
    public static bool IsAvailable(out string detail) =>
        LenovoDriverCatalogService.IsAvailable(out detail);

    public static Task<DriverUpdateScanResult> ScanAsync(
        string language,
        CancellationToken cancellationToken = default) =>
        LenovoDriverCatalogService.ScanAsync(language, cancellationToken);

    public static Task<DriverUpdateInstallResult> InstallAsync(
        IReadOnlyCollection<DriverUpdateItem> updates,
        CancellationToken cancellationToken = default) =>
        LenovoDriverCatalogService.InstallAsync(updates, cancellationToken);

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "—";
        var units = new[] { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return value.ToString(
                   value >= 10 ? "0" : "0.0",
                   CultureInfo.InvariantCulture) +
               " " + units[index];
    }

    internal static string FormatRebootType(string rebootType, bool chinese)
    {
        var normalized = rebootType.Trim();
        if (normalized.Length == 0)
            return string.Empty;

        return normalized.ToLowerInvariant() switch
        {
            "0" or "noreboot" or "norebootrequired" =>
                chinese ? "无需重启" : "No restart",
            "1" or "rebootforced" =>
                chinese ? "必须重启" : "Restart required",
            "2" or "reserved" =>
                chinese ? "可能需要重启" : "Restart may be required",
            "3" or "rebootrequested" or "rebootrequired" =>
                chinese ? "需要重启" : "Restart required",
            "4" or "poweroffforced" or "shutdownrequested" =>
                chinese ? "需要关机" : "Shutdown required",
            "5" or "rebootdelayed" =>
                chinese ? "需要重启（可稍后）" :
                    "Restart required (can be deferred)",
            _ => normalized
        };
    }

    internal static bool RequiresRestart(string rebootType) =>
        rebootType.Trim().ToLowerInvariant() is not
            ("" or "0" or "noreboot" or "norebootrequired");
}
