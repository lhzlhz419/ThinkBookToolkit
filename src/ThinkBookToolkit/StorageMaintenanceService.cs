using System;
using System.Threading;

namespace ThinkBookToolkit;

internal sealed class StorageMaintenanceService : IDisposable
{
    private readonly Timer _timer;
    private int _running;
    internal StorageMaintenanceService(AppSettings settings, Func<string> currentRecording)
    {
        _timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                FileRetentionPolicy.Cleanup(ToolkitStoragePaths.Logs, settings.LogRetentionDays, false, ToolkitLog.CurrentPath);
                FileRetentionPolicy.Cleanup(ToolkitStoragePaths.Recordings, settings.SensorRecording.RetentionDays, true, currentRecording());
            }
            catch (Exception ex) { ToolkitLog.Warning("Storage cleanup failed: " + ex.Message); }
            finally { Volatile.Write(ref _running, 0); }
        }, null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }
    public void Dispose() => _timer.Dispose();
}
