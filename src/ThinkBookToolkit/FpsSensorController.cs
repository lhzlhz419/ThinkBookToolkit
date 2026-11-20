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
    private readonly Func<int?> _foregroundPid;
    private readonly Func<CancellationToken, Task> _captureSession;
    private readonly HashSet<string> _consumers = new(
        StringComparer.OrdinalIgnoreCase);
    private sealed record Target(int ProcessId);
    private Target? _target;
    private CancellationTokenSource? _monitorCts;
    private Task _monitorLifetime = Task.CompletedTask;
    private FpsTelemetrySnapshot _current = FpsTelemetrySnapshot.Empty;

    public FpsSensorController(
        Func<int?, GameProcessCandidate?>? fallbackProcessProvider = null,
        Func<int?>? foregroundPid = null, Func<CancellationToken, Task>? captureSession = null)
    {
        _fallbackProcessProvider = fallbackProcessProvider;
        _foregroundPid = foregroundPid ?? GetForegroundProcessId;
        _captureSession = captureSession ?? RunFastSessionAsync;
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
        lock (_sync)
        {
            if (enabled) _consumers.Add(consumer);
            else _consumers.Remove(consumer);
            if (_consumers.Count > 0) Start();
            else Stop();
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_monitorCts is not null) return;
            var cts = _monitorCts = new CancellationTokenSource();
            var previous = _monitorLifetime;
            _monitorLifetime = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (!cts.IsCancellationRequested) await MonitorForegroundProcessAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch (Exception ex) { ToolkitLog.Warning("FPS monitoring failed: " + ex.GetBaseException().Message); }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_monitorCts, cts)) _monitorCts = null;
                        cts.Dispose();
                    }
                }
            });
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_monitorCts is null) return;
            _monitorCts.Cancel();
            _monitorCts = null;
            SetTarget(null);
        }
    }

    private void SetTarget(int? pid)
    {
        lock (_sync)
        {
            Volatile.Write(ref _target, pid.HasValue ? new Target(pid.Value) : null);
            _current = FpsTelemetrySnapshot.Empty;
        }
        Updated?.Invoke(this, FpsTelemetrySnapshot.Empty);
    }

    private async Task MonitorForegroundProcessAsync(CancellationToken token)
    {
        Task? capture = null;
        var retryAfter = DateTimeOffset.MinValue;
        try
        {
            int? targetProcessId = null;
            int? lastForegroundProcessId = null;
            var targetStartedAt = DateTimeOffset.MinValue;
            var usingFallback = false;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var foregroundProcessId = _foregroundPid();
                    var foregroundChanged =
                        foregroundProcessId != lastForegroundProcessId;
                    lastForegroundProcessId = foregroundProcessId;
                    int? desiredProcessId = targetProcessId;
                    if (foregroundChanged)
                    {
                        desiredProcessId = foregroundProcessId;
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
                            usingFallback = false;
                        }
                        else if (fallbackGame.ProcessId != desiredProcessId)
                        {
                            desiredProcessId = fallbackGame.ProcessId;
                        }
                    }
                    if (desiredProcessId != targetProcessId)
                    {
                        targetProcessId = desiredProcessId;
                        targetStartedAt = DateTimeOffset.UtcNow;
                        lock (_sync)
                        {
                            token.ThrowIfCancellationRequested();
                            SetTarget(targetProcessId);
                        }
                    }
                    if (capture is { IsCompleted: true })
                    {
                        try { await capture; }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            ToolkitLog.Warning("FPS trace ended: " + ex.GetBaseException().Message);
                        }
                        capture = null;
                        retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                    }
                    if (targetProcessId.HasValue && capture is null && DateTimeOffset.UtcNow >= retryAfter)
                        capture = Task.Run(() => _captureSession(token), token);
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
        finally { if (capture is not null) await capture; }
    }

    private static int? GetForegroundProcessId()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId is 0 or 4)
                return null;
            using var process = Process.GetProcessById((int)processId);
            if (process.HasExited ||
                string.IsNullOrWhiteSpace(process.ProcessName) ||
                Blacklist.Contains(process.ProcessName))
            {
                return null;
            }
            return process.Id;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private async Task RunFastSessionAsync(CancellationToken token)
    {
        var sessionName = "ThinkBookToolkit-Fps-" + Guid.NewGuid().ToString("N");
        using var session = new TraceEventSession(
            sessionName,
            TraceEventSessionOptions.Create);
        var samples = new Queue<long>(RollingSampleCapacity);
        Target? sampledTarget = null;
        long previousTimestamp = 0;
        long lastPublishTimestamp = 0;
        session.Source.AllEvents += data =>
        {
            var target = Volatile.Read(ref _target);
            if (target is null || data.ProviderGuid != DxgKrnlGuid ||
                (int)data.ID != PresentEventId ||
                data.ProcessID != target.ProcessId)
            {
                return;
            }
            if (!ReferenceEquals(sampledTarget, target))
            {
                sampledTarget = target;
                samples.Clear();
                previousTimestamp = lastPublishTimestamp = 0;
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
            Publish(target, new FpsTelemetrySnapshot(
                IsFinitePositive(fps) ? fps : null,
                low is > 0 && double.IsFinite(low.Value) ? low : null,
                frameTicks / (double)TimeSpan.TicksPerMillisecond,
                DateTimeOffset.UtcNow));
        };
        session.EnableProvider(
            DxgKrnlGuid,
            TraceEventLevel.Verbose,
            1UL);
        var processing = Task.Factory.StartNew(session.Source.Process, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        try { await Task.WhenAny(processing, Task.Delay(Timeout.Infinite, timeout.Token)); }
        finally
        {
            timeout.Cancel();
            session.Stop();
            await processing;
        }
    }

    private void Publish(Target target, FpsTelemetrySnapshot value)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_target, target)) return;
            _current = value;
        }
        Updated?.Invoke(this, value);
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
