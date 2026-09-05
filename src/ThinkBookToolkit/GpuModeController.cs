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

internal enum GpuControlProtocol
{
    Unsupported,
    LegacyThreeMode,
    AdvancedBios,
    LegacyGSync
}

internal sealed record GpuModeState(
    GpuWorkingMode CurrentMode,
    IReadOnlyList<GpuWorkingMode> SupportedModes,
    bool UsesDirectGraphicsConfiguration,
    GpuControlProtocol Protocol);

internal sealed record GpuModeApplyResult(
    bool RequiresRestart,
    bool ParentStaged,
    bool ChildStaged,
    GpuControlProtocol Protocol,
    string? Warning = null,
    bool AwaitingLiveConfirmation = false);

internal static class GpuModeController
{
    private const string GameZoneClass = "LENOVO_GAMEZONE_DATA";
    private const string BiosAssistantClass = "LENOVO_BIOS_ASSISTANT";
    private const string GraphicsDevice = "GraphicsDevice";
    private const string UmaGraphics = "UMA Graphics";
    private const string IntegratedGraphics = "Integrated Graphics";
    private const string SwitchableGraphics = "Switchable Graphics";
    private const string DynamicGraphics = "Dynamic Graphics";
    private const string DiscreteGraphics = "Discrete Graphics";
    private const uint GpuModeFunctionId = 3;
    private const uint ExitDiscreteValue = 2;

    public static GpuModeState ReadState()
    {
        var capabilities = ReadCapabilities();
        var graphicsDevice = GetGraphicsDevice();
        if (IsIntegratedDirectValue(graphicsDevice))
            return new(GpuWorkingMode.IntegratedDirect,
                capabilities.SupportedModes, true, capabilities.Protocol);
        if (IsDiscreteGraphics(graphicsDevice))
            return new(GpuWorkingMode.Discrete,
                capabilities.SupportedModes, true, capabilities.Protocol);

        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        var gSync = capabilities.SupportsGSync &&
                    TryReadGSync(gameZone, out var gSyncValue)
            ? gSyncValue
            : 0;
        var igpuMode = capabilities.SupportsIgpuMode
            ? LenovoWmi.InvokeInt(gameZone, "GetIGPUModeStatus", null, "Data")
            : 0;
        var current = capabilities.Protocol == GpuControlProtocol.LegacyThreeMode &&
                      IsSwitchableGraphics(graphicsDevice)
            ? ModeFromIgpuValue(igpuMode)
            : gSync != 0
                ? GpuWorkingMode.Discrete
                : ModeFromIgpuValue(igpuMode);
        return new(current, capabilities.SupportedModes, false,
            capabilities.Protocol);
    }

    public static GpuModeApplyResult SetModeFromEffectiveState(
        GpuWorkingMode effectiveMode,
        bool effectiveUsesDirectGraphicsConfiguration,
        GpuWorkingMode target)
    {
        var targetUsesDirectGraphicsConfiguration =
            UsesDirectGraphicsConfiguration(target);
        var requiresRestart = GpuModeRestartState.RequiresRestart(
            effectiveMode,
            effectiveUsesDirectGraphicsConfiguration,
            target,
            targetUsesDirectGraphicsConfiguration);
        var result = SetMode(target);
        if (ShouldCancelPendingDirectChange(
                requiresRestart,
                result.RequiresRestart,
                targetUsesDirectGraphicsConfiguration))
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                System.Threading.Thread.Sleep(100);
                if (IsDirectGraphicsConfiguration(GetGraphicsDevice()))
                    continue;

                var childResult = SetMode(target);
                if (childResult.RequiresRestart)
                    throw new InvalidOperationException(
                        "The switchable-graphics mode could not be restored " +
                        "while cancelling the pending GPU change.");
                return childResult with
                {
                    RequiresRestart = false,
                    ParentStaged = false
                };
            }
            throw new InvalidOperationException(
                "The pending direct-graphics configuration could not be cancelled.");
        }
        return result with
        {
            RequiresRestart = requiresRestart || result.RequiresRestart
        };
    }

    internal static bool ShouldCancelPendingDirectChange(
        bool transitionRequiresRestart,
        bool configurationWriteRequiresRestart,
        bool targetUsesDirectGraphicsConfiguration) =>
        !transitionRequiresRestart && configurationWriteRequiresRestart &&
        !targetUsesDirectGraphicsConfiguration;

    public static GpuModeApplyResult SetMode(GpuWorkingMode target)
    {
        var capabilities = ReadCapabilities();
        if (!capabilities.SupportedModes.Contains(target))
            throw new NotSupportedException(
                $"GPU working mode {target} is not supported.");

        if (target == GpuWorkingMode.IntegratedDirect)
        {
            var integratedValue = capabilities.IntegratedDirectValue ??
                throw new NotSupportedException(
                    "Integrated graphics direct mode is not supported.");
            SetGraphicsDevice(integratedValue);
            return new(true, true, false, capabilities.Protocol);
        }
        if (target == GpuWorkingMode.Discrete &&
            capabilities.SupportsDiscreteDirect)
        {
            SetGraphicsDevice(DiscreteGraphics);
            return new(true, true, false, capabilities.Protocol);
        }

        var currentGraphicsDevice = GetGraphicsDevice();
        if (IsDirectGraphicsConfiguration(currentGraphicsDevice))
        {
            if (capabilities.Protocol == GpuControlProtocol.LegacyThreeMode)
                return StageLegacyThreeModeExit(target, capabilities);
            if (capabilities.SupportsSwitchableGraphics)
            {
                SetGraphicsDevice(capabilities.SwitchableGraphicsValue ??
                                  SwitchableGraphics);
                return new(true, true, false, capabilities.Protocol);
            }
        }

        var awaitingLiveConfirmation = SetChildMode(target, capabilities);
        return new(
            false,
            false,
            true,
            capabilities.Protocol,
            AwaitingLiveConfirmation: awaitingLiveConfirmation);
    }

    public static bool IsHybridMode(GpuWorkingMode mode) =>
        mode is GpuWorkingMode.Hybrid or
            GpuWorkingMode.IntegratedOnly or
            GpuWorkingMode.HybridAuto;

    internal static GpuControlProtocol ClassifyProtocol(
        uint capabilityData,
        bool capabilityRead,
        bool supportsIgpuMode,
        bool supportsGSync,
        bool hasDirectSelections)
    {
        var supportSwitchGpu = (capabilityData & 0x06u) >> 1;
        var versionByte = (capabilityData >> 16) & 0xFFu;
        var advancedMajor = (int)((versionByte >> 4) & 0x0F);
        if (capabilityRead && supportSwitchGpu is 2 or 3 &&
            advancedMajor < 2 && supportsIgpuMode)
            return GpuControlProtocol.LegacyThreeMode;
        if (capabilityRead && advancedMajor >= 2)
            return GpuControlProtocol.AdvancedBios;
        if (supportsGSync)
            return GpuControlProtocol.LegacyGSync;
        return !capabilityRead && hasDirectSelections
            ? GpuControlProtocol.AdvancedBios
            : GpuControlProtocol.Unsupported;
    }

    internal static GpuWorkingMode ResolveLegacyThreeModeState(
        string graphicsDevice,
        int igpuMode) =>
        IsDiscreteGraphics(graphicsDevice)
            ? GpuWorkingMode.Discrete
            : ModeFromIgpuValue(igpuMode);

    private static GpuModeApplyResult StageLegacyThreeModeExit(
        GpuWorkingMode target,
        GpuModeCapabilities capabilities)
    {
        ExitLegacyDiscreteMode();
        try
        {
            _ = SetChildMode(target, capabilities);
            return new(true, true, true, capabilities.Protocol);
        }
        catch (Exception ex)
        {
            return new(true, true, false, capabilities.Protocol,
                ex.GetBaseException().Message);
        }
    }

    private static void ExitLegacyDiscreteMode()
    {
        using var assistant = LenovoWmi.GetActiveInstance(BiosAssistantClass);
        var returnData = LenovoWmi.InvokeCheckedUInt32(
            assistant,
            "SetValue",
            new Dictionary<string, object>
            {
                ["IndexData"] = GpuModeFunctionId,
                ["ValueData"] = ExitDiscreteValue
            },
            "ReturnData");
        if (!IsBiosAssistantSuccess(returnData))
            throw new InvalidOperationException(
                $"BIOS Assistant rejected GPU change: 0x{returnData:X8}.");
    }

    internal static bool IsBiosAssistantSuccess(uint returnData) =>
        (returnData & 0x80000000u) != 0;

    private static bool SetChildMode(
        GpuWorkingMode target,
        GpuModeCapabilities capabilities)
    {
        if (!IsHybridMode(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        var expected = target switch
        {
            GpuWorkingMode.IntegratedOnly => 1,
            GpuWorkingMode.HybridAuto => 2,
            _ => 0
        };
        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        if (capabilities.SupportsGSync)
        {
            TryDisableGSync(
                gameZone,
                verify: capabilities.Protocol !=
                        GpuControlProtocol.LegacyThreeMode);
        }
        if (!capabilities.SupportsIgpuMode)
            return false;
        if (capabilities.Protocol == GpuControlProtocol.LegacyThreeMode)
        {
            // LLT's old GameZone path treats this as a void setter. Some
            // providers expose an output object that is not meaningful, so
            // do not validate it or perform an immediate status read.
            LenovoWmi.InvokeVoid(gameZone, "SetIGPUModeStatus",
                new Dictionary<string, object> { ["mode"] = expected });
            ToolkitLog.Info(
                $"Legacy GPU mode request {target} was accepted without " +
                "immediate readback validation.");
            return ShouldAwaitLiveConfirmation(
                capabilities.Protocol,
                target);
        }

        LenovoWmi.InvokeChecked(gameZone, "SetIGPUModeStatus",
            new Dictionary<string, object> { ["mode"] = expected });
        var actual = LenovoWmi.InvokeCheckedInt(
            gameZone, "GetIGPUModeStatus", null, "Data");
        if (actual != expected)
            throw new InvalidOperationException(
                $"iGPUMode mismatch: expected={expected}, actual={actual}.");
        return false;
    }

    internal static void RetryLegacyIntegratedOnlyWithoutReadback()
    {
        using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
        LenovoWmi.InvokeVoid(
            gameZone,
            "SetIGPUModeStatus",
            new Dictionary<string, object> { ["mode"] = 1 });
        ToolkitLog.Info(
            "Legacy iGPU-only request was written again without readback.");
    }

    internal static bool ShouldAwaitLiveConfirmation(
        GpuControlProtocol protocol,
        GpuWorkingMode target) =>
        protocol == GpuControlProtocol.LegacyThreeMode &&
        target == GpuWorkingMode.IntegratedOnly;

    private static void TryDisableGSync(
        ManagementObject gameZone,
        bool verify)
    {
        try
        {
            if (verify)
            {
                LenovoWmi.InvokeChecked(gameZone, "SetGSyncStatus",
                    new Dictionary<string, object> { ["Data"] = 0 });
            }
            else
            {
                LenovoWmi.InvokeVoid(gameZone, "SetGSyncStatus",
                    new Dictionary<string, object> { ["Data"] = 0 });
            }
            if (verify &&
                TryReadGSync(gameZone, out var actual) &&
                actual != 0)
            {
                ToolkitLog.Warning(
                    $"GSync remained enabled after the compatibility " +
                    $"write (actual={actual}); continuing with iGPUMode.");
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "GSync compatibility step is unavailable and was ignored: " +
                ex.GetBaseException().Message);
        }
    }

    private static bool TryReadGSync(
        ManagementObject gameZone,
        out int value)
    {
        try
        {
            value = LenovoWmi.InvokeInt(
                gameZone, "GetGSyncStatus", null, "Data");
            return true;
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "GetGSyncStatus is unavailable and was ignored: " +
                ex.GetBaseException().Message);
            value = 0;
            return false;
        }
    }

    private static bool UsesDirectGraphicsConfiguration(GpuWorkingMode mode)
    {
        var capabilities = ReadCapabilities();
        return mode == GpuWorkingMode.IntegratedDirect ||
               mode == GpuWorkingMode.Discrete &&
               capabilities.SupportsDiscreteDirect;
    }

    private static bool IsIntegratedDirectValue(string value) =>
        value.Equals(UmaGraphics, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(IntegratedGraphics, StringComparison.OrdinalIgnoreCase);

    private static bool IsDiscreteGraphics(string value) =>
        value.Equals(DiscreteGraphics, StringComparison.OrdinalIgnoreCase);

    private static bool IsSwitchableGraphics(string value) =>
        value.Equals(SwitchableGraphics, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(DynamicGraphics, StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectGraphicsConfiguration(string value) =>
        IsIntegratedDirectValue(value) || IsDiscreteGraphics(value);

    private static GpuWorkingMode ModeFromIgpuValue(int value) => value switch
    {
        1 => GpuWorkingMode.IntegratedOnly,
        2 => GpuWorkingMode.HybridAuto,
        _ => GpuWorkingMode.Hybrid
    };

    private static GpuModeCapabilities ReadCapabilities()
    {
        var capabilityRead = TryReadBiosCapability(out var capabilityData);
        var supportsGSync = false;
        var supportsIgpuMode = false;
        try
        {
            using var gameZone = LenovoWmi.GetActiveInstance(GameZoneClass);
            supportsGSync = TryInvokeInt(
                gameZone, "IsSupportGSync", "Data") > 0;
            supportsIgpuMode = TryInvokeInt(
                gameZone, "IsSupportIGPUMode", "Data") > 0;
        }
        catch
        {
        }
        if (!supportsIgpuMode && capabilityRead &&
            IndicatesLegacyThreeModeCapability(capabilityData))
        {
            // Direct mode can temporarily hide the GameZone provider. The
            // BIOS capability remains authoritative for protocol routing;
            // the child write is retried after reboot if the provider is not
            // ready yet.
            supportsIgpuMode = true;
        }
        var graphicsSelections = GetGraphicsDeviceSelections();
        var integratedDirectValue = graphicsSelections.FirstOrDefault(
            IsIntegratedDirectValue);
        var supportsDiscreteDirect = graphicsSelections.Any(
            IsDiscreteGraphics);
        var switchableValue = graphicsSelections.FirstOrDefault(
            IsSwitchableGraphics);
        var supportsSwitchableGraphics = switchableValue is not null;
        var protocol = ClassifyProtocol(
            capabilityData, capabilityRead, supportsIgpuMode,
            supportsGSync,
            supportsDiscreteDirect || supportsSwitchableGraphics);

        var modes = new List<GpuWorkingMode>();
        if (supportsGSync || supportsIgpuMode ||
            integratedDirectValue is not null || supportsDiscreteDirect)
            modes.Add(GpuWorkingMode.Hybrid);
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

        return new(protocol, capabilityData, supportsGSync,
            supportsIgpuMode, supportsDiscreteDirect,
            supportsSwitchableGraphics, integratedDirectValue,
            switchableValue, modes);
    }

    private static bool TryReadBiosCapability(out uint data)
    {
        try
        {
            using var assistant = LenovoWmi.GetActiveInstance(
                BiosAssistantClass);
            data = LenovoWmi.InvokeCheckedUInt32(
                assistant, "GetCapabilityValue", null, "Data");
            return true;
        }
        catch
        {
            data = 0;
            return false;
        }
    }

    private static bool IndicatesLegacyThreeModeCapability(uint data)
    {
        var supportSwitchGpu = (data & 0x06u) >> 1;
        var versionByte = (data >> 16) & 0xFFu;
        var advancedMajor = (versionByte >> 4) & 0x0Fu;
        return supportSwitchGpu is 2 or 3 && advancedMajor < 2;
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
                instance, "GetBiosSelections",
                new Dictionary<string, object> { ["Item"] = GraphicsDevice },
                "Selections");
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries |
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
                @"root\WMI", "SELECT * FROM Lenovo_BiosSetting");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var current = Convert.ToString(item["CurrentSetting"]);
                    if (current is null || !current.StartsWith(
                            GraphicsDevice + ",",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    return current.Split(',').ElementAtOrDefault(1) ??
                           string.Empty;
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
        using (var setter = LenovoWmi.GetActiveInstance(
                   "Lenovo_SetBiosSetting"))
        {
            var result = LenovoWmi.InvokeCheckedString(
                setter, "SetBiosSetting",
                new Dictionary<string, object>
                {
                    ["parameter"] = $"{GraphicsDevice},{value},"
                },
                "return");
            RequireBiosSuccess("SetBiosSetting", result);
        }
        using var saver = LenovoWmi.GetActiveInstance(
            "Lenovo_SaveBiosSettings");
        var saveResult = LenovoWmi.InvokeCheckedString(
            saver, "SaveBiosSettings", null, "return");
        RequireBiosSuccess("SaveBiosSettings", saveResult);
    }

    private static void RequireBiosSuccess(string operation, string result)
    {
        if (!result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{operation} was rejected: {result}.");
    }

    private sealed record GpuModeCapabilities(
        GpuControlProtocol Protocol,
        uint BiosCapabilityData,
        bool SupportsGSync,
        bool SupportsIgpuMode,
        bool SupportsDiscreteDirect,
        bool SupportsSwitchableGraphics,
        string? IntegratedDirectValue,
        string? SwitchableGraphicsValue,
        IReadOnlyList<GpuWorkingMode> SupportedModes);
}
