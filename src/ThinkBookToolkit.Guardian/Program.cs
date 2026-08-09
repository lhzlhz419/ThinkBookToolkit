using System;
using System.ServiceProcess;

namespace ThinkBookToolkit.Guardian;

internal static class GuardianEntryPoint
{
    public static bool TryRun(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(args[0], "--gpu-worker", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = GpuMonitorWorker.Run(
                args.Length > 1 ? args[1] : string.Empty);
            return true;
        }

        if (args.Length > 0 &&
            string.Equals(
                args[0],
                "--fan-watchdog-service",
                StringComparison.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new FanWatchdogService());
            return true;
        }

        return false;
    }
}
