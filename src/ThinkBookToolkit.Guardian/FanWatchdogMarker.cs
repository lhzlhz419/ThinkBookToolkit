namespace ThinkBookToolkit.Guardian;

internal sealed record FanWatchdogMarker(
    int ProcessId,
    long ProcessStartUtcTicks,
    string LogDirectory,
    string BackendIdentity);
