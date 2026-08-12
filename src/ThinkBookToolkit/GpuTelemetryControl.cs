using System;

namespace ThinkBookToolkit;

internal enum GpuTelemetryMode
{
    Full,
    Quiescing,
    Paused,
    IntegratedOnly
}

internal static class GpuTelemetryControl
{
    private static readonly object Sync = new();
    private static GpuTelemetryMode _mode = GpuTelemetryMode.Full;

    public static event Action<GpuTelemetryMode>? ModeChanged;

    public static GpuTelemetryMode Mode
    {
        get
        {
            lock (Sync)
                return _mode;
        }
    }

    public static void SetMode(GpuTelemetryMode mode, string reason)
    {
        Action<GpuTelemetryMode>? changed;
        lock (Sync)
        {
            if (_mode == mode)
                return;

            ToolkitLog.Info(
                $"GPU telemetry mode changed from {_mode} to {mode}: {reason}");
            _mode = mode;
            changed = ModeChanged;
        }

        // Subscribers switch or restart their worker synchronously so that
        // PnP disappearance never leaves a stale native telemetry process.
        changed?.Invoke(mode);
    }
}
