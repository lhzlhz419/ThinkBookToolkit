using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

internal enum GpuWorkingMode
{
    Hybrid,
    IntegratedOnly,
    HybridAuto,
    Discrete,
    IntegratedDirect
}

internal sealed record GpuModeState(
    GpuWorkingMode CurrentMode,
    IReadOnlyList<GpuWorkingMode> SupportedModes);

internal static class GpuModeController
{
    private const string GameZoneClass = "LENOVO_GAMEZONE_DATA";
    private const string GraphicsDevice = "GraphicsDevice";
    private const string UmaGraphics = "UMA Graphics";
    private const string IntegratedGraphics = "Integrated Graphics";
    private const string SwitchableGraphics = "Switchable Graphics";
    private const string DiscreteGraphics = "Discrete Graphics";
    private static readonly object CapabilitiesLock = new();
    private static GpuModeCapabilities? _capabilities;

    public static GpuModeState ReadState()
    {
        var capabilities = GetCapabilities();
        var graphicsDevice = GetGraphicsDevice();
        if (IsIntegratedDirectValue(graphicsDevice))
        {
            return new(
                GpuWorkingMode.IntegratedDirect,
                capabilities.SupportedModes);
        }
        if (graphicsDevice.Equals(
                DiscreteGraphics,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(GpuWorkingMode.Discrete, capabilities.SupportedModes);
        }

        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        var gSync = capabilities.SupportsGSync
            ? LenovoWmi.InvokeInt(gameZone, "GetGSyncStatus", null, "Data")
            : 0;
        var igpuMode = capabilities.SupportsIgpuMode
            ? LenovoWmi.InvokeInt(gameZone, "GetIGPUModeStatus", null, "Data")
            : 0;
        var current = gSync != 0
            ? GpuWorkingMode.Discrete
            : igpuMode switch
            {
                1 => GpuWorkingMode.IntegratedOnly,
                2 => GpuWorkingMode.HybridAuto,
                _ => GpuWorkingMode.Hybrid
            };
        return new(current, capabilities.SupportedModes);
    }

    public static bool SetMode(GpuWorkingMode target)
    {
        var capabilities = GetCapabilities();
        if (!capabilities.SupportedModes.Contains(target))
        {
            throw new NotSupportedException(
                $"GPU working mode {target} is not supported.");
        }

        if (target == GpuWorkingMode.IntegratedDirect)
        {
            var integratedValue = capabilities.IntegratedDirectValue;
            if (integratedValue is null)
            {
                throw new NotSupportedException(
                    "Integrated graphics direct mode is not supported.");
            }

            SetGraphicsDevice(integratedValue);
            return true;
        }

        if (target == GpuWorkingMode.Discrete &&
            capabilities.SupportsDiscreteDirect)
        {
            SetGraphicsDevice(DiscreteGraphics);
            return true;
        }

        var currentGraphicsDevice = GetGraphicsDevice();
        var leavingDirectMode =
            IsIntegratedDirectValue(currentGraphicsDevice) ||
            currentGraphicsDevice.Equals(
                DiscreteGraphics,
                StringComparison.OrdinalIgnoreCase);
        if (leavingDirectMode &&
            capabilities.SupportsSwitchableGraphics)
        {
            SetGraphicsDevice(SwitchableGraphics);
            return true;
        }

        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        var gSync = target == GpuWorkingMode.Discrete ? 1 : 0;
        var igpuMode = target switch
        {
            GpuWorkingMode.IntegratedOnly => 1,
            GpuWorkingMode.HybridAuto => 2,
            _ => 0
        };

        if (capabilities.SupportsGSync &&
            LenovoWmi.InvokeInt(gameZone, "GetGSyncStatus", null, "Data") != gSync)
        {
            LenovoWmi.InvokeVoid(
                gameZone,
                "SetGSyncStatus",
                new Dictionary<string, object> { ["Data"] = gSync });
        }

        if (capabilities.SupportsIgpuMode &&
            LenovoWmi.InvokeInt(gameZone, "GetIGPUModeStatus", null, "Data") != igpuMode)
        {
            LenovoWmi.InvokeVoid(
                gameZone,
                "SetIGPUModeStatus",
                new Dictionary<string, object> { ["mode"] = igpuMode });
        }

        return false;
    }

    public static bool RequiresRestart(
        GpuWorkingMode current,
        GpuWorkingMode target) =>
        current != target &&
        (IsDirectMode(current) || IsDirectMode(target));

    public static bool IsDirectMode(GpuWorkingMode mode) =>
        mode is GpuWorkingMode.Discrete or GpuWorkingMode.IntegratedDirect;

    public static bool IsHybridMode(GpuWorkingMode mode) =>
        mode is GpuWorkingMode.Hybrid or
            GpuWorkingMode.IntegratedOnly or
            GpuWorkingMode.HybridAuto;

    private static bool IsIntegratedDirectValue(string value) =>
        value.Equals(UmaGraphics, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(IntegratedGraphics, StringComparison.OrdinalIgnoreCase);

    private static GpuModeCapabilities GetCapabilities()
    {
        lock (CapabilitiesLock)
        {
            return _capabilities ??= ReadCapabilities();
        }
    }

    private static GpuModeCapabilities ReadCapabilities()
    {
        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        var supportsGSync = TryInvokeInt(
            gameZone,
            "IsSupportGSync",
            "Data") > 0;
        var supportsIgpuMode = TryInvokeInt(
            gameZone,
            "IsSupportIGPUMode",
            "Data") > 0;
        var graphicsSelections = GetGraphicsDeviceSelections();
        var integratedDirectValue = graphicsSelections.FirstOrDefault(
            IsIntegratedDirectValue);
        var supportsDiscreteDirect = graphicsSelections.Any(value =>
            value.Equals(DiscreteGraphics, StringComparison.OrdinalIgnoreCase));
        var supportsSwitchableGraphics = graphicsSelections.Any(value =>
            value.Equals(SwitchableGraphics, StringComparison.OrdinalIgnoreCase));

        var modes = new List<GpuWorkingMode>();
        if (supportsGSync || supportsIgpuMode ||
            integratedDirectValue is not null || supportsDiscreteDirect)
        {
            modes.Add(GpuWorkingMode.Hybrid);
        }
        if (supportsIgpuMode)
        {
            modes.Add(GpuWorkingMode.IntegratedOnly);
            modes.Add(GpuWorkingMode.HybridAuto);
        }
        if (supportsDiscreteDirect || supportsGSync)
            modes.Add(GpuWorkingMode.Discrete);
        if (integratedDirectValue is not null)
            modes.Add(GpuWorkingMode.IntegratedDirect);

        if (modes.Count == 0)
            throw new NotSupportedException("GPU working mode is not supported.");

        return new(
            supportsGSync,
            supportsIgpuMode,
            supportsDiscreteDirect,
            supportsSwitchableGraphics,
            integratedDirectValue,
            modes);
    }

    private static int TryInvokeInt(
        ManagementObject instance,
        string method,
        params string[] outputNames)
    {
        try
        {
            return LenovoWmi.InvokeInt(instance, method, null, outputNames);
        }
        catch
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> GetGraphicsDeviceSelections()
    {
        try
        {
            using var instance = LenovoWmi.GetActiveInstance(
                "Lenovo_GetBiosSelections");
            var raw = LenovoWmi.InvokeString(
                instance,
                "GetBiosSelections",
                new Dictionary<string, object> { ["Item"] = GraphicsDevice },
                "Selections");
            return raw.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        }
        catch
        {
            return [];
        }
    }

    private static string GetGraphicsDevice()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT * FROM Lenovo_BiosSetting");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var current = Convert.ToString(item["CurrentSetting"]);
                    if (current is null ||
                        !current.StartsWith(
                            GraphicsDevice + ",",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return current.Split(',').ElementAtOrDefault(1) ?? string.Empty;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static void SetGraphicsDevice(string value)
    {
        using (var setter = LenovoWmi.GetActiveInstance("Lenovo_SetBiosSetting"))
        {
            LenovoWmi.InvokeVoid(
                setter,
                "SetBiosSetting",
                new Dictionary<string, object>
                {
                    ["parameter"] = $"{GraphicsDevice},{value},"
                });
        }

        using var saver = LenovoWmi.GetActiveInstance("Lenovo_SaveBiosSettings");
        LenovoWmi.InvokeVoid(saver, "SaveBiosSettings");
    }

    private sealed record GpuModeCapabilities(
        bool SupportsGSync,
        bool SupportsIgpuMode,
        bool SupportsDiscreteDirect,
        bool SupportsSwitchableGraphics,
        string? IntegratedDirectValue,
        IReadOnlyList<GpuWorkingMode> SupportedModes);
}
