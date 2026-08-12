namespace ThinkBookToolkit;

internal static class PerformanceModeAvailability
{
    public static bool CanSelect(ItsMode mode, bool? isAcConnected) =>
        mode != ItsMode.Geek || isAcConnected != false;
}
