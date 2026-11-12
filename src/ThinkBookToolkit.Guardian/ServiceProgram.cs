using System;
using System.ServiceProcess;

namespace ThinkBookToolkit.Guardian;

internal static class ServiceProgram
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--restore-fans")
            return FanWatchdogService.RunRecoveryWorkerAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length != 0) return 2;
        ServiceBase.Run(new FanWatchdogService());
        return 0;
    }
}
