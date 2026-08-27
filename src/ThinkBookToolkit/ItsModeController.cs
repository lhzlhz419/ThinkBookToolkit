using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace ThinkBookToolkit;

internal static class ItsModeController
{
    private const string ModernServiceName = "LenovoProcessManagement";
    private const string LegacyServiceName = "LITSSVC";
    private const uint LegacyEnergyIoctl = 0x8310213C;
    private const uint LegacyFullSpeedDisable = 0x000F100B;
    private const uint LegacyFullSpeedEnable = 0x001F100B;
    private static int _legacyGeekOverlayActive;

    internal static bool LegacyGeekOverlayActive =>
        System.Threading.Volatile.Read(ref _legacyGeekOverlayActive) != 0;

    public static void SetMode(ItsMode mode)
    {
        var path = new ItsModeDetector().GetControlPath();
        if (path == ItsModeControlPath.Unavailable)
            throw new NotSupportedException(
                "No supported Lenovo ITS control service was detected.");
        if (path == ItsModeControlPath.LegacyLitssvc)
        {
            SetLegacyMode(mode);
            return;
        }

        var serviceName = ServiceNameForPath(path);
        var command = CommandForMode(mode, path);

        ToolkitLog.Info(
            $"Setting ITS mode through {serviceName}: " +
            $"mode={mode}; command={command}; path={path}.");
        using var service = new ServiceController(serviceName);
        service.ExecuteCommand(command);
    }

    internal static bool IsModeSupported(ItsMode mode) =>
        new ItsModeDetector().IsModeSupported(mode);

    internal static string ServiceNameForPath(ItsModeControlPath path) =>
        path switch
        {
            ItsModeControlPath.ModernDispatcher => ModernServiceName,
            ItsModeControlPath.LegacyLitssvc => LegacyServiceName,
            _ => throw new NotSupportedException(
                "No ITS control service is available.")
        };

    internal static int CommandForMode(
        ItsMode mode,
        ItsModeControlPath path) => path switch
    {
        ItsModeControlPath.ModernDispatcher => mode switch
        {
            ItsMode.Intelligent => 163,
            ItsMode.PowerSaving => 164,
            ItsMode.Performance => 165,
            ItsMode.Geek => 172,
            _ => throw Unsupported(mode, path)
        },
        ItsModeControlPath.LegacyLitssvc => mode switch
        {
            ItsMode.Intelligent => 135,
            ItsMode.PowerSaving => 146,
            ItsMode.Performance => 148,
            ItsMode.Geek => 148,
            _ => throw Unsupported(mode, path)
        },
        _ => throw Unsupported(mode, path)
    };

    internal static IReadOnlyList<int> LegacyServiceCommandsForMode(
        ItsMode mode) => mode switch
    {
        ItsMode.Intelligent => [0x87],
        ItsMode.PowerSaving => [0x86, 0x92],
        ItsMode.Performance or ItsMode.Geek => [0x86, 0x94],
        _ => throw Unsupported(mode, ItsModeControlPath.LegacyLitssvc)
    };

    internal static uint LegacyEnergyCommandForMode(ItsMode mode) =>
        mode == ItsMode.Geek
            ? LegacyFullSpeedEnable
            : LegacyFullSpeedDisable;

    private static void SetLegacyMode(ItsMode mode)
    {
        var serviceCommands = LegacyServiceCommandsForMode(mode);
        if (mode != ItsMode.Geek)
            TryDisableLegacyGeekOverlay();

        using var service = new ServiceController(LegacyServiceName);
        foreach (var command in serviceCommands)
            service.ExecuteCommand(command);

        uint? energyOutput = null;
        if (mode == ItsMode.Geek)
        {
            using var energy = new LenovoEnergyDriver();
            energyOutput = energy.Call(
                LegacyEnergyIoctl,
                LegacyFullSpeedEnable);
            System.Threading.Volatile.Write(
                ref _legacyGeekOverlayActive,
                1);
        }

        ToolkitLog.Info(
            "Legacy ITS mode applied: " +
            $"mode={mode}; LITSSVC=[{string.Join(", ", serviceCommands.Select(command => $"0x{command:X2}"))}]; " +
            $"EnergyDrv={(mode == ItsMode.Geek ? $"0x{LegacyFullSpeedEnable:X8}; output=0x{energyOutput:X8}" : $"0x{LegacyFullSpeedDisable:X8} (best effort)")}.");
    }

    private static void TryDisableLegacyGeekOverlay()
    {
        try
        {
            using var energy = new LenovoEnergyDriver();
            _ = energy.Call(
                LegacyEnergyIoctl,
                LegacyFullSpeedDisable);
            System.Threading.Volatile.Write(
                ref _legacyGeekOverlayActive,
                0);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "Legacy EnergyDrv FullSpeed overlay could not be cleared; " +
                "the LITSSVC mode transition will continue: " + ex.Message);
        }
    }

    private static Exception Unsupported(
        ItsMode mode,
        ItsModeControlPath path) =>
        new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            $"ITS mode {mode} is unsupported by {path}.");
}
