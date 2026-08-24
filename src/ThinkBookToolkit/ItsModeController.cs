using System;
using System.ServiceProcess;

namespace ThinkBookToolkit;

internal static class ItsModeController
{
    private const string ModernServiceName = "LenovoProcessManagement";
    private const string LegacyServiceName = "LITSSVC";

    public static void SetMode(ItsMode mode)
    {
        var path = new ItsModeDetector().GetControlPath();
        if (path == ItsModeControlPath.Unavailable)
            throw new NotSupportedException(
                "No supported Lenovo ITS control service was detected.");
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
            _ => throw Unsupported(mode, path)
        },
        _ => throw Unsupported(mode, path)
    };

    private static Exception Unsupported(
        ItsMode mode,
        ItsModeControlPath path) =>
        new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            $"ITS mode {mode} is unsupported by {path}.");
}
