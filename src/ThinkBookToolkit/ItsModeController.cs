using System;
using System.ServiceProcess;

namespace ThinkBookToolkit;

internal static class ItsModeController
{
    private const string ServiceName = "LenovoProcessManagement";

    public static void SetMode(ItsMode mode)
    {
        var command = mode switch
        {
            ItsMode.Intelligent => 163,
            ItsMode.PowerSaving => 164,
            ItsMode.Performance => 165,
            ItsMode.Geek => 172,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported ITS mode.")
        };

        using var service = new ServiceController(ServiceName);
        service.ExecuteCommand(command);
    }
}
