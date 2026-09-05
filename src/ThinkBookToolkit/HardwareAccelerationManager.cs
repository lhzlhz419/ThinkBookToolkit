using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows.Interop;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed record HardwareAccelerationAvailability(
    bool HasIntegratedGpu,
    bool HasDiscreteGpu,
    IReadOnlyList<string> AdapterNames);

internal static class HardwareAccelerationManager
{
    private const string GpuPreferenceRegistryPath =
        @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";

    public static HardwareAccelerationMode CurrentMode { get; private set; } =
        HardwareAccelerationMode.Disabled;

    public static void ApplyForStartup(AppSettings settings)
    {
        var mode = settings.HardwareAccelerationMode;
        try
        {
            SetWindowsGpuPreference(mode);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "The saved GPU rendering preference could not be applied: " +
                ex.Message);
        }

        CurrentMode = mode;
        RenderOptions.ProcessRenderMode =
            mode == HardwareAccelerationMode.Disabled
                ? RenderMode.SoftwareOnly
                : RenderMode.Default;
        ToolkitLog.Info(
            $"WPF hardware acceleration startup mode: {mode}; " +
            $"renderMode={RenderOptions.ProcessRenderMode}.");
    }

    public static void SetWindowsGpuPreference(
        HardwareAccelerationMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException(
                "The application executable path is unavailable.");
        using var key = Registry.CurrentUser.CreateSubKey(
            GpuPreferenceRegistryPath,
            writable: true) ?? throw new InvalidOperationException(
            "The Windows GPU preference registry key could not be opened.");
        switch (mode)
        {
            case HardwareAccelerationMode.PowerSaving:
                key.SetValue(
                    executable,
                    GpuPreferenceValue(mode)!,
                    RegistryValueKind.String);
                break;
            case HardwareAccelerationMode.HighPerformance:
                key.SetValue(
                    executable,
                    GpuPreferenceValue(mode)!,
                    RegistryValueKind.String);
                break;
            default:
                key.DeleteValue(executable, throwOnMissingValue: false);
                break;
        }
    }

    internal static string? GpuPreferenceValue(
        HardwareAccelerationMode mode) => mode switch
    {
        HardwareAccelerationMode.PowerSaving => "GpuPreference=1;",
        HardwareAccelerationMode.HighPerformance => "GpuPreference=2;",
        _ => null
    };

    public static HardwareAccelerationAvailability DetectAvailability()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_VideoController");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var errorCode = Convert.ToUInt32(
                        item["ConfigManagerErrorCode"] ?? 0u);
                    var name = Convert.ToString(item["Name"]);
                    if (errorCode == 0 && !string.IsNullOrWhiteSpace(name))
                        names.Add(name.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "Display adapters could not be detected for hardware acceleration settings: " +
                ex.Message);
        }
        return Classify(names);
    }

    internal static HardwareAccelerationAvailability Classify(
        IEnumerable<string> adapterNames)
    {
        var names = adapterNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var integrated = names.Any(IsIntegratedAdapter);
        var discrete = names.Any(IsDiscreteAdapter);
        return new(integrated, discrete, names);
    }

    private static bool IsIntegratedAdapter(string name) =>
        name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiscreteAdapter(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("AMD Radeon RX", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Intel Arc", StringComparison.OrdinalIgnoreCase);
}
