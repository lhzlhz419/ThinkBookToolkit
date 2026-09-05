using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record FpsTelemetrySnapshot(
    double? Fps,
    double? OnePercentLowFps,
    double? FrameTimeMs,
    DateTimeOffset UpdatedAt)
{
    public static FpsTelemetrySnapshot Empty { get; } = new(
        null,
        null,
        null,
        DateTimeOffset.MinValue);

    public bool IsFresh =>
        UpdatedAt != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow - UpdatedAt <= TimeSpan.FromSeconds(2);
}

/// <summary>
/// Foreground-process FPS monitoring adapted from LLT's FpsSensorController.
/// It uses the same DxgKrnl Present events and the tracing stack delivered by
/// the pinned LLT.PresentMonFps dependency, with a shorter rolling window so
/// FPS can appear before the upstream 500-frame 1% Low window fills.
/// </summary>
internal sealed class FpsSensorController : IDisposable
{
    private static readonly Guid DxgKrnlGuid = new(
        "802ec45a-1e99-4b83-9920-87c98277ba9d");
    private const int PresentEventId = 184;
    private const int FastLowSampleMinimum = 30;
    private const int RollingSampleCapacity = 240;
    private static readonly HashSet<string> Blacklist = new(
        [
            "explorer", "taskmgr", "ApplicationFrameHost", "System",
            "svchost", "csrss", "wininit", "services", "lsass",
            "winlogon", "smss", "spoolsv", "SearchIndexer", "SearchUI",
            "RuntimeBroker", "dwm", "ctfmon", "audiodg", "fontdrvhost",
            "taskhost", "conhost", "sihost", "StartMenuExperienceHost",
            "ShellExperienceHost", "ThinkBookToolkit"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly Func<int?, GameProcessCandidate?>? _fallbackProcessProvider;
    private readonly HashSet<string> _consumers = new(
        StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _monitorCts;
    private CancellationTokenSource? _processCts;
    private Process? _currentProcess;
    private FpsTelemetrySnapshot _current = FpsTelemetrySnapshot.Empty;
    private bool _running;

    public FpsSensorController(
        Func<int?, GameProcessCandidate?>? fallbackProcessProvider = null)
    {
        _fallbackProcessProvider = fallbackProcessProvider;
    }

    public event EventHandler<FpsTelemetrySnapshot>? Updated;

    public FpsTelemetrySnapshot Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public void SetConsumerEnabled(string consumer, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(consumer))
            return;
        bool shouldRun;
        lock (_sync)
        {
            if (enabled)
                _consumers.Add(consumer);
            else
                _consumers.Remove(consumer);
            shouldRun = _consumers.Count > 0;
        }
        if (shouldRun)
            Start();
        else
            Stop();
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _monitorCts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorForegroundProcessAsync(_monitorCts.Token));
    }

    public void Stop()
    {
        _running = false;
        var monitor = _monitorCts;
        _monitorCts = null;
        monitor?.Cancel();
        StopProcessMonitoring();
        monitor?.Dispose();
    }

    private async Task MonitorForegroundProcessAsync(CancellationToken token)
    {
        int? targetProcessId = null;
        var targetProcessName = string.Empty;
        int? lastForegroundProcessId = null;
        var targetStartedAt = DateTimeOffset.MinValue;
        var usingFallback = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var foreground = GetForegroundProcess();
                var foregroundProcessId = foreground?.Id;
                var foregroundChanged =
                    foregroundProcessId != lastForegroundProcessId;
                lastForegroundProcessId = foregroundProcessId;
                int? desiredProcessId = targetProcessId;
                var desiredProcessName = targetProcessName;
                if (foregroundChanged)
                {
                    desiredProcessId = foregroundProcessId;
                    desiredProcessName = foreground?.ProcessName ?? string.Empty;
                    usingFallback = false;
                }
                var needsFallback = !desiredProcessId.HasValue ||
                    !usingFallback &&
                    DateTimeOffset.UtcNow - targetStartedAt >=
                        TimeSpan.FromSeconds(1.5) &&
                    !Current.IsFresh;
                if (needsFallback &&
                    _fallbackProcessProvider?.Invoke(desiredProcessId) is { } game &&
                    game.ProcessId != desiredProcessId)
                {
                    desiredProcessId = game.ProcessId;
                    desiredProcessName = game.ProcessName;
                    usingFallback = true;
                    ToolkitLog.Info(
                        "FPS foreground monitoring produced no usable frames; " +
                        $"using configured game process {game.ProcessName} " +
                        $"(pid={game.ProcessId}) instead.");
                }
                else if (usingFallback)
                {
                    var fallbackGame = _fallbackProcessProvider?.Invoke(
                        foregroundProcessId);
                    if (fallbackGame is null)
                    {
                        desiredProcessId = foregroundProcessId;
                        desiredProcessName = foreground?.ProcessName ?? string.Empty;
                        usingFallback = false;
                    }
                    else if (fallbackGame.ProcessId != desiredProcessId)
                    {
                        desiredProcessId = fallbackGame.ProcessId;
                        desiredProcessName = fallbackGame.ProcessName;
                    }
                }
                var exited = false;
                try { exited = _currentProcess?.HasExited == true; }
                catch { exited = true; }
                var missing = targetProcessId.HasValue && _currentProcess is null;
                if (desiredProcessId != targetProcessId || exited || missing)
                {
                    StopProcessMonitoring();
                    targetProcessId = desiredProcessId;
                    targetProcessName = desiredProcessName;
                    targetStartedAt = DateTimeOffset.UtcNow;
                    if (desiredProcessId.HasValue)
                    {
                        StartProcessMonitoring(
                            desiredProcessId.Value,
                            desiredProcessName);
                    }
                }
                await Task.Delay(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "FPS foreground monitoring failed: " + ex.Message);
                try { await Task.Delay(500, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static Process? GetForegroundProcess()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId is 0 or 4)
                return null;
            var process = Process.GetProcessById((int)processId);
            if (process.HasExited ||
                string.IsNullOrWhiteSpace(process.ProcessName) ||
                Blacklist.Contains(process.ProcessName))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private void StartProcessMonitoring(int processId, string processName)
    {
        try
        {
            _processCts = CancellationTokenSource.CreateLinkedTokenSource(
                _monitorCts?.Token ?? CancellationToken.None);
            _currentProcess = Process.GetProcessById(processId);
            var token = _processCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunFastSessionAsync(processId, token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    ToolkitLog.Warning(
                        $"FPS monitoring failed for {processName}: " +
                        ex.GetBaseException().Message);
                    Publish(FpsTelemetrySnapshot.Empty);
                }
            }, token);
            ToolkitLog.Info(
                $"FPS monitoring started: process={processName}; pid={processId}.");
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"FPS monitoring could not start for {processName}: " +
                ex.Message);
            StopProcessMonitoring();
        }
    }

    private async Task RunFastSessionAsync(
        int processId,
        CancellationToken token)
    {
        var sessionName = "ThinkBookToolkit-Fps-" + Guid.NewGuid().ToString("N");
        using var session = new TraceEventSession(
            sessionName,
            TraceEventSessionOptions.Create);
        var samples = new Queue<long>(RollingSampleCapacity);
        var sync = new object();
        long previousTimestamp = 0;
        long lastPublishTimestamp = 0;
        session.Source.AllEvents += data =>
        {
            if (data.ProviderGuid != DxgKrnlGuid ||
                (int)data.ID != PresentEventId ||
                data.ProcessID != processId)
            {
                return;
            }
            var timestamp = data.TimeStamp.Ticks;
            if (previousTimestamp == 0)
            {
                previousTimestamp = timestamp;
                return;
            }
            var frameTicks = timestamp - previousTimestamp;
            previousTimestamp = timestamp;
            if (frameTicks <= 0 || frameTicks > TimeSpan.TicksPerSecond)
                return;
            lock (sync)
            {
                samples.Enqueue(frameTicks);
                while (samples.Count > RollingSampleCapacity)
                    _ = samples.Dequeue();
                if (timestamp - lastPublishTimestamp <
                    TimeSpan.TicksPerMillisecond * 100)
                {
                    return;
                }
                lastPublishTimestamp = timestamp;
                var values = samples.ToArray();
                var totalTicks = values.Sum();
                var fps = totalTicks > 0
                    ? values.Length * (double)TimeSpan.TicksPerSecond /
                      totalTicks
                    : 0;
                double? low = null;
                if (values.Length >= FastLowSampleMinimum)
                {
                    var instantaneous = values
                        .Select(value => TimeSpan.TicksPerSecond /
                                         (double)value)
                        .OrderBy(value => value)
                        .ToArray();
                    var lowCount = Math.Max(
                        1,
                        (int)Math.Ceiling(instantaneous.Length * .01));
                    low = instantaneous.Take(lowCount).Average();
                }
                Publish(new FpsTelemetrySnapshot(
                    IsFinitePositive(fps) ? fps : null,
                    low is > 0 && double.IsFinite(low.Value) ? low : null,
                    frameTicks / (double)TimeSpan.TicksPerMillisecond,
                    DateTimeOffset.UtcNow));
            }
        };
        session.EnableProvider(
            DxgKrnlGuid,
            TraceEventLevel.Verbose,
            1UL);
        var processing = Task.Factory.StartNew(
            session.Source.Process,
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            session.Source.StopProcessing();
            _ = await Task.WhenAny(processing, Task.Delay(1000));
        }
    }

    private void Publish(FpsTelemetrySnapshot value)
    {
        lock (_sync)
            _current = value;
        Updated?.Invoke(this, value);
    }

    private void StopProcessMonitoring()
    {
        _processCts?.Cancel();
        _processCts?.Dispose();
        _processCts = null;
        _currentProcess?.Dispose();
        _currentProcess = null;
        Publish(FpsTelemetrySnapshot.Empty);
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    public void Dispose()
    {
        lock (_sync)
            _consumers.Clear();
        Stop();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
