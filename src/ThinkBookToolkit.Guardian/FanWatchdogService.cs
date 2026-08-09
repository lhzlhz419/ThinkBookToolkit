using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit.Guardian;

internal sealed class FanWatchdogService : ServiceBase
{
    public const string ServiceNameValue = "ThinkBookToolkitGuardian";
    public const string MarkerExtension = ".armed.json";
    private readonly CancellationTokenSource _stop = new();

    public FanWatchdogService()
    {
        ServiceName = ServiceNameValue;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _ = Task.Run(() => RunAsync(args, _stop.Token));
    }

    protected override void OnStop() => _stop.Cancel();

    protected override void OnShutdown() => _stop.Cancel();

    private async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        GuardianLog? log = null;
        try
        {
            var markerPath = ResolveMarkerPath(args);
            var marker = ReadMarker(markerPath);
            log = new GuardianLog(marker.LogDirectory);
            log.Info(
                $"Guardian started for Toolkit PID {marker.ProcessId}; backend {marker.BackendIdentity}; marker {Path.GetFileName(markerPath)}.");

            await WaitForProcessExitAsync(marker, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (!File.Exists(markerPath))
            {
                log.Info("Toolkit exited normally; watchdog was disarmed.");
                return;
            }

            log.Info("Toolkit disappeared while watchdog remained armed; restoring firmware automatic fan control.");
            await RestoreWithRetryAsync(log, cancellationToken);
            File.Delete(markerPath);
            log.Info("Firmware automatic fan control was restored; watchdog is stopping.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log ??= new GuardianLog(DefaultLogDirectory());
            log.Error("Fan watchdog failed.", ex);
        }
        finally
        {
            log?.Dispose();
            try
            {
                Stop();
            }
            catch
            {
            }
        }
    }

    private static async Task WaitForProcessExitAsync(
        FanWatchdogMarker marker,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(marker.ProcessId);
            var actualStart = process.StartTime.ToUniversalTime().Ticks;
            if (actualStart != marker.ProcessStartUtcTicks)
                return;
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
            // The process can exit after GetProcessById succeeds but before
            // StartTime or WaitForExitAsync observes it. That is the same
            // condition the watchdog is waiting for, not a service failure.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveMarkerPath(string[] args)
    {
        if (args.Length != 1 ||
            string.IsNullOrWhiteSpace(args[0]) ||
            !string.Equals(args[0], Path.GetFileName(args[0]), StringComparison.Ordinal) ||
            !args[0].EndsWith(MarkerExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid watchdog marker filename is required.");
        }

        return Path.Combine(MarkerDirectory(), args[0]);
    }

    private static FanWatchdogMarker ReadMarker(string path)
    {
        var marker = JsonSerializer.Deserialize<FanWatchdogMarker>(
            File.ReadAllText(path));
        if (marker is null || marker.ProcessId <= 0 || marker.ProcessStartUtcTicks <= 0)
            throw new InvalidDataException("The watchdog marker is invalid.");
        return marker;
    }

    private static void RestoreFirmwareAutomatic(GuardianLog log)
    {
        var backend = LoadBackend();
        try
        {
            backend.SetFullSpeed(false);
        }
        catch (Exception ex)
        {
            log.Error("Disabling the backend full-speed operation failed; RestoreAuto will still be attempted.", ex);
        }

        backend.RestoreAuto();
    }

    private static async Task RestoreWithRetryAsync(
        GuardianLog log,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                RestoreFirmwareAutomatic(log);
                return;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                log.Error(
                    $"Firmware automatic fan-control restore attempt {attempt}/3 failed.",
                    ex);
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Firmware automatic fan control could not be restored after three attempts.",
            lastFailure);
    }

    private static IFanBackend LoadBackend()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "ThinkBookToolkit.FanBackend.dll");
        var assembly = Assembly.LoadFrom(path);
        var type = assembly.GetTypes().FirstOrDefault(candidate =>
            !candidate.IsAbstract &&
            !candidate.IsInterface &&
            typeof(IFanBackend).IsAssignableFrom(candidate));
        if (type is null)
            throw new InvalidOperationException("The installed fan backend does not implement IFanBackend.");

        try
        {
            var backend = (IFanBackend)(Activator.CreateInstance(type) ??
                throw new InvalidOperationException("The installed fan backend could not be created."));
            if (backend.ApiVersion != FanBackendContract.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"Fan backend API {backend.ApiVersion} is incompatible with {FanBackendContract.CurrentVersion}.");
            }
            return backend;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public static string MarkerDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ThinkBookToolkit",
        "watchdog");

    private static string DefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ThinkBookToolkit",
        "log");
}
