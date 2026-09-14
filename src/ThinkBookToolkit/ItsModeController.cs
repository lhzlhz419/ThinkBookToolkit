using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private static readonly object ModeSwitchSync = new();

    internal static bool LegacyGeekOverlayActive =>
        System.Threading.Volatile.Read(ref _legacyGeekOverlayActive) != 0;

    public static void SetMode(ItsMode mode) =>
        SetMode(mode, new ItsModeDetector().GetControlPath());

    internal static void SetMode(ItsMode mode, ItsModeControlPath path)
    {
        lock (ModeSwitchSync)
            SetModeCore(mode, path);
    }

    private static void SetModeCore(ItsMode mode, ItsModeControlPath path)
    {
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
        ItsModeDetector.SelectControlPathForCurrentProcess(path);
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

    internal static void SetLegacyMode(ItsMode mode)
    {
        lock (ModeSwitchSync)
        {
            ExecuteLegacyTransition(mode, ItsModeDetector.ReadLegacyBaseMode,
                command =>
                {
                    SendLegacyServiceControl(command);
                    ItsModeDetector.PreferLegacyPathForCurrentProcess();
                }, enabled =>
                {
                    if (!enabled) { TryDisableLegacyGeekOverlay(); return; }
                    using var energy = new LenovoEnergyDriver();
                    var output = energy.Call(LegacyEnergyIoctl, LegacyFullSpeedEnable);
                    ItsModeDetector.PreferLegacyPathForCurrentProcess();
                    System.Threading.Volatile.Write(ref _legacyGeekOverlayActive, 1);
                    ToolkitLog.Info($"Legacy Geek overlay enabled: output=0x{output:X8}.");
                }, () => WaitForLegacyPerformance(ItsModeDetector.ReadLegacyBaseMode,
                    System.Threading.Thread.Sleep));
            ItsModeDetector.PreferLegacyPathForCurrentProcess();
            ToolkitLog.Info($"Legacy ITS mode applied: mode={mode}.");
        }
    }

    internal static void ExecuteLegacyTransition(ItsMode mode, Func<ItsMode> readBaseMode,
        Action<uint> sendService, Action<bool> setOverlay, Action waitForPerformance)
    {
        var commands = LegacyServiceCommandsForMode(mode);
        if (mode == ItsMode.Geek)
        {
            if (readBaseMode() is not (ItsMode.Performance or ItsMode.Geek))
            {
                foreach (var command in commands) sendService((uint)command);
                waitForPerformance();
            }
            // Both modes share legacy CurrentSetting=3. Reissuing LITSSVC's
            // performance command here can asynchronously undo the overlay.
            setOverlay(true);
            return;
        }
        setOverlay(false);
        foreach (var command in commands) sendService((uint)command);
    }

    internal static void WaitForLegacyPerformance(Func<ItsMode> readBaseMode, Action<int> delay)
    {
        var consecutive = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            consecutive = readBaseMode() is ItsMode.Performance or ItsMode.Geek ? consecutive + 1 : 0;
            if (consecutive >= 2) return;
            delay(100);
        }
        throw new System.TimeoutException("旧版接口未确认性能基础模式，未启用极客扩展。 / Legacy performance base mode was not confirmed; Geek overlay was not enabled.");
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

    private static void SendLegacyServiceControl(uint controlCode)
    {
        var manager = OpenSCManager(
            null,
            null,
            ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenSCManager failed.");
        }
        try
        {
            var service = OpenService(
                manager,
                LegacyServiceName,
                ServiceQueryStatus | ServiceUserDefinedControl);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"OpenService({LegacyServiceName}) failed.");
            }
            try
            {
                if (!ControlService(
                        service,
                        controlCode,
                        out var status))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"ControlService({LegacyServiceName}, " +
                        $"0x{controlCode:X2}) failed.");
                }
                ToolkitLog.Info(
                    $"Legacy service control succeeded: " +
                    $"service={LegacyServiceName}; command=0x{controlCode:X2}; " +
                    $"state={status.CurrentState}; win32={status.Win32ExitCode}; " +
                    $"serviceExit={status.ServiceSpecificExitCode}.");
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceUserDefinedControl = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(
        IntPtr manager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        IntPtr service,
        uint control,
        out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static Exception Unsupported(
        ItsMode mode,
        ItsModeControlPath path) =>
        new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            $"ITS mode {mode} is unsupported by {path}.");
}
