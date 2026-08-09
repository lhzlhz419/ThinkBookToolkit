using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;

namespace ThinkBookToolkit;

internal sealed class FanWatchdogClient
{
    internal const string ServiceName = "ThinkBookToolkitGuardian";
    private const string MarkerExtension = ".armed.json";
    private string? _markerPath;

    public bool IsArmed => !string.IsNullOrWhiteSpace(_markerPath);

    public bool TryArm(string backendIdentity, out string error)
    {
        error = string.Empty;
        if (IsArmed)
            return true;

        try
        {
            using var current = Process.GetCurrentProcess();
            var startTicks = current.StartTime.ToUniversalTime().Ticks;
            var markerDirectory = MarkerDirectory();
            Directory.CreateDirectory(markerDirectory);
            var markerName = $"{current.Id}-{startTicks}{MarkerExtension}";
            var markerPath = Path.Combine(markerDirectory, markerName);
            var temporaryPath = markerPath + ".tmp";
            var marker = new WatchdogMarker(
                current.Id,
                startTicks,
                Path.Combine(
                    Path.GetDirectoryName(CurveProfileStore.SettingsPath)!,
                    "log"),
                backendIdentity);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(marker));
            File.Move(temporaryPath, markerPath, true);

            try
            {
                using var service = new ServiceController(ServiceName);
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.WaitForStatus(
                        ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(10));
                }
                service.Start([markerName]);
                service.WaitForStatus(
                    ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                File.Delete(markerPath);
                throw;
            }

            _markerPath = markerPath;
            ToolkitLog.Info(
                $"Fan watchdog armed with service {ServiceName} for PID {current.Id}.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            ToolkitLog.Warning(
                "Fan watchdog could not be armed. Forced process termination will not be able to restore firmware automatic fan control: " +
                ex.Message);
            return false;
        }
    }

    public bool TryDisarm(out string error)
    {
        error = string.Empty;
        if (_markerPath is null)
            return true;
        try
        {
            File.Delete(_markerPath);
            ToolkitLog.Info("Fan watchdog disarmed after firmware automatic control was restored.");
            _markerPath = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            ToolkitLog.Error("Fan watchdog could not be disarmed.", ex);
            return false;
        }
    }

    internal static string MarkerDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ThinkBookToolkit",
        "watchdog");

    private sealed record WatchdogMarker(
        int ProcessId,
        long ProcessStartUtcTicks,
        string LogDirectory,
        string BackendIdentity);
}
