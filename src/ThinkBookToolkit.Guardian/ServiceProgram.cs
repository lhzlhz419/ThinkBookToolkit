using System.ServiceProcess;

namespace ThinkBookToolkit.Guardian;

internal static class ServiceProgram
{
    private static void Main() => ServiceBase.Run(new FanWatchdogService());
}
