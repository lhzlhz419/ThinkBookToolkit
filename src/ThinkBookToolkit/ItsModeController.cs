using System;
using System.ServiceProcess;

namespace ThinkBookToolkit;

internal static class ItsModeController
{
    public static void SetMode(ItsMode mode)
    {
        var backend = new ItsModeDetector().DetectSwitchBackend();
        if (!TryResolveCommand(backend, mode, out var serviceName, out var command))
        {
            throw new NotSupportedException(
                $"The {backend} backend does not support {mode} mode.");
        }

        using var service = new ServiceController(serviceName);
        service.ExecuteCommand(command);
    }

    internal static bool TryResolveCommand(
        ItsModeBackend backend,
        ItsMode mode,
        out string serviceName,
        out int command)
    {
        serviceName = backend switch
        {
            ItsModeBackend.LenovoProcessManagement =>
                "LenovoProcessManagement",
            ItsModeBackend.LegacyItsService => "LITSSVC",
            _ => string.Empty
        };
        command = (backend, mode) switch
        {
            (ItsModeBackend.LenovoProcessManagement, ItsMode.Intelligent) => 163,
            (ItsModeBackend.LenovoProcessManagement, ItsMode.PowerSaving) => 164,
            (ItsModeBackend.LenovoProcessManagement, ItsMode.Performance) => 165,
            (ItsModeBackend.LenovoProcessManagement, ItsMode.Geek) => 172,
            (ItsModeBackend.LegacyItsService, ItsMode.Intelligent) => 0x87,
            (ItsModeBackend.LegacyItsService, ItsMode.PowerSaving) => 0x92,
            (ItsModeBackend.LegacyItsService, ItsMode.Performance) => 0x94,
            _ => 0
        };
        return serviceName.Length > 0 && command > 0;
    }
}
