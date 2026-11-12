using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record GpuMonitorWorkerSnapshot(
    string Name,
    double? LoadPercent,
    double? MemoryLoadPercent,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? CoreTemperatureC,
    double? HotSpotTemperatureC,
    double? MemoryTemperatureC,
    IReadOnlyList<double> MemoryChipTemperaturesC,
    double? PowerW,
    string CoreTemperatureSensor,
    string MemoryTemperatureSensor,
    DiscreteGpuActivityState DiscreteGpuState =
        DiscreteGpuActivityState.Unknown,
    string PerformanceState = "");

internal sealed class GpuMonitorWorkerClient : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProcessTerminationTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NonNvidiaFallbackDuration =
        TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly string _executable;
    private readonly string _argument;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _restartDelay;
    private Process? _process;
    private ChildProcessJob? _job;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private DateTimeOffset _nextStart;
    private DateTimeOffset _nonNvidiaFallbackUntil;
    private int _adapterGeneration = -1;
    private bool _disposed;
    private bool _missingExecutableLogged;
    private string _appliedOverclockSignature = string.Empty;

    public GpuMonitorWorkerClient(string? executable = null, string argument = "--gpu-worker",
        TimeSpan? responseTimeout = null, TimeSpan? restartDelay = null)
    {
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.GpuWorker.exe");
        _argument = argument;
        _responseTimeout = responseTimeout ?? ResponseTimeout;
        _restartDelay = restartDelay ?? RestartDelay;
        GpuTelemetryControl.ModeChanged += OnTelemetryModeChanged;
        GpuTelemetryControl.RestartRequested += RestartWorker;
    }

    public GpuMonitorWorkerSnapshot? Read()
    {
        lock (_sync)
        {
            if (_disposed)
                return null;

            var telemetryMode = GpuTelemetryControl.Mode;
            if (telemetryMode == GpuTelemetryMode.Paused)
            {
                StopWorker();
                return null;
            }

            var adapters = GpuDevicePresenceDetector.Capture();
            if (_adapterGeneration >= 0 &&
                adapters.Generation != _adapterGeneration)
            {
                ToolkitLog.Info(
                    $"Restarting isolated GPU monitor after display-adapter generation changed from {_adapterGeneration} to {adapters.Generation}.");
                StopWorker();
                _nextStart = DateTimeOffset.MinValue;
                _nonNvidiaFallbackUntil = DateTimeOffset.MinValue;
            }
            _adapterGeneration = adapters.Generation;

            if (telemetryMode == GpuTelemetryMode.Quiescing &&
                DateTimeOffset.UtcNow < _nonNvidiaFallbackUntil)
            {
                // Do not reopen LHM merely to obtain an iGPU fallback while
                // the dGPU is waiting to be removed. LHM discovery itself can
                // touch NVIDIA even before the first Update() call.
                StopWorker();
                return null;
            }

            try
            {
                if (!EnsureWorker()) return null;
                var response = Exchange(
                    telemetryMode == GpuTelemetryMode.IntegratedOnly
                        ? Guardian.GpuMonitorWorker.ReadNonNvidiaCommand
                        : DateTimeOffset.UtcNow < _nonNvidiaFallbackUntil
                            ? Guardian.GpuMonitorWorker.ReadNonNvidiaFallbackCommand
                        : telemetryMode == GpuTelemetryMode.Quiescing
                            ? Guardian.GpuMonitorWorker.ReadQuiescingCommand
                            : Guardian.GpuMonitorWorker.ReadCommand, _responseTimeout);
                if (response is null)
                    throw new EndOfStreamException(
                        "The isolated GPU monitor closed its pipe unexpectedly.");
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidDataException(
                        "The isolated GPU monitor returned an empty response.");
                if (response == "null")
                    return null;

                var snapshot = JsonSerializer.Deserialize<GpuMonitorWorkerSnapshot>(
                    response);
                if (snapshot is not null &&
                    HybridAutoGpuPolicy.ShouldEnterSilentEjectWindow(
                        telemetryMode,
                        snapshot.DiscreteGpuState))
                {
                    // LLT stops both NVIDIA LHM and NVAPI while the firmware
                    // is trying to remove an idle dGPU.  Keeping even this
                    // isolated process alive leaves native NVIDIA handles in
                    // the system and can prevent the final PnP removal.
                    ToolkitLog.Info(
                        "The discrete GPU is inactive; ending the isolated GPU monitor and entering the silent ejection window.");
                    GpuTelemetryControl.SetMode(
                        GpuTelemetryMode.Paused,
                        "the discrete GPU is inactive and ready for firmware removal");
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                var exit = TryGetExitCode(_process);
                ToolkitLog.Error(
                    "The isolated GPU monitor failed. GPU values will remain unavailable until the worker is restarted." +
                    (exit.HasValue ? $" Exit code: {exit.Value} (0x{unchecked((uint)exit.Value):X8})." : string.Empty) +
                    " Active adapters: " +
                    (adapters.ActiveNames.Count == 0
                        ? (adapters.Reliable ? "none" : "presence check unavailable")
                        : string.Join(", ", adapters.ActiveNames)) + ".",
                    ex);
                _nonNvidiaFallbackUntil =
                    DateTimeOffset.UtcNow + NonNvidiaFallbackDuration;
                StopWorker(graceful: false);
                _nextStart = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
        }
    }

    public GpuWorkerCommandResponse QueryApplications() =>
        ExecuteCommand(Guardian.GpuMonitorWorker.ListApplicationsCommand);

    public GpuWorkerCommandResponse ReadOverclockRanges() =>
        ExecuteCommand(Guardian.GpuMonitorWorker.ReadOverclockRangesCommand);

    public GpuWorkerCommandResponse KillApplications() =>
        ExecuteCommand(
            Guardian.GpuMonitorWorker.KillApplicationsCommand,
            ProcessTerminationTimeout);

    public GpuWorkerCommandResponse ApplyOverclock(
        GpuOverclockSettings settings,
        bool force = false)
    {
        if (!GpuOverclockPolicy.TryValidate(settings, out var error))
            return GpuWorkerCommandResponse.Failure(error);
        var signature = GpuOverclockPolicy.Signature(settings);
        lock (_sync)
        {
            if (!force && signature == _appliedOverclockSignature)
                return GpuWorkerCommandResponse.Ok();
        }
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)));
        var result = ExecuteCommand(
            Guardian.GpuMonitorWorker.ApplyOverclockCommand + payload,
            OperationTimeout);
        if (result.Success)
        {
            lock (_sync)
                _appliedOverclockSignature = signature;
        }
        return result;
    }

    public GpuWorkerCommandResponse ResetOverclock()
    {
        var result = ExecuteCommand(
            Guardian.GpuMonitorWorker.ResetOverclockCommand,
            OperationTimeout);
        if (result.Success)
        {
            lock (_sync)
                _appliedOverclockSignature =
                    GpuOverclockPolicy.Signature(
                        new GpuOverclockSettings());
        }
        return result;
    }

    private GpuWorkerCommandResponse ExecuteCommand(
        string command,
        TimeSpan? timeout = null)
    {
        lock (_sync)
        {
            if (_disposed)
                return GpuWorkerCommandResponse.Failure(
                    "The GPU monitor is not running.");
            if (GpuTelemetryControl.Mode is GpuTelemetryMode.Paused or
                GpuTelemetryMode.IntegratedOnly)
            {
                return GpuWorkerCommandResponse.Failure(
                    "The discrete GPU is unavailable.");
            }
            try
            {
                if (!EnsureWorker())
                    return GpuWorkerCommandResponse.Failure(
                        "The isolated GPU monitor could not be started or is waiting to retry.");
                var response = Exchange(command, timeout ?? _responseTimeout);
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidDataException(
                        "The isolated GPU monitor returned an empty operation response.");
                return JsonSerializer.Deserialize<GpuWorkerCommandResponse>(
                           response) ??
                       GpuWorkerCommandResponse.Failure(
                           "The isolated GPU monitor returned an invalid operation response.");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error(
                    "An isolated GPU operation failed: " + command.Split(':')[0] + ".",
                    ex);
                StopWorker(graceful: false);
                _nextStart = DateTimeOffset.UtcNow + _restartDelay;
                // Do not replay writes or process termination when completion is unknown.
                return GpuWorkerCommandResponse.Failure(ex.GetBaseException().Message);
            }
        }
    }

    private string? Exchange(string command, TimeSpan timeout) =>
        ExchangeAsync(command, timeout).GetAwaiter().GetResult();

    private async Task<string?> ExchangeAsync(string command, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await _writer!.WriteLineAsync(command.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _writer.FlushAsync(deadline.Token).ConfigureAwait(false);
            return await _reader!.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"The isolated GPU monitor did not finish {command.Split(':')[0]} within {timeout.TotalSeconds:g} seconds.");
        }
    }

    private bool EnsureWorker()
    {
        if (_process is { HasExited: false } && _pipe is { IsConnected: true } &&
            _reader is not null && _writer is not null)
            return true;
        if (DateTimeOffset.UtcNow < _nextStart)
            return false;

        if (_process is { HasExited: true } exitedProcess)
        {
            var exit = TryGetExitCode(exitedProcess);
            ToolkitLog.Warning(
                "The isolated GPU monitor exited between refreshes" +
                (exit.HasValue
                    ? $" with code {exit.Value} (0x{unchecked((uint)exit.Value):X8})"
                    : string.Empty) +
                "; temporarily selecting a non-NVIDIA adapter.");
            _nonNvidiaFallbackUntil =
                DateTimeOffset.UtcNow + NonNvidiaFallbackDuration;
        }

        StopWorker(graceful: false);
        var executable = _executable;
        if (!File.Exists(executable))
        {
            if (!_missingExecutableLogged)
            {
                _missingExecutableLogged = true;
                ToolkitLog.Warning(
                    "The isolated GPU monitor executable is missing: " + executable);
            }
            _nextStart = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            return false;
        }

        try
        {
            var pipeName = "ThinkBookToolkit.GpuMonitor." + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            _pipe = pipe;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add(_argument);
            process.StartInfo.ArgumentList.Add(pipeName);
            _process = process;
            if (!process.Start())
                throw new InvalidOperationException("The isolated GPU monitor process did not start.");
            var job = new ChildProcessJob();
            _job = job;
            job.Assign(process);
            using var connectionDeadline = new CancellationTokenSource(_responseTimeout);
            pipe.WaitForConnectionAsync(connectionDeadline.Token).GetAwaiter().GetResult();
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true
            };
            ToolkitLog.Info($"Isolated GPU monitor started (PID {process.Id}).");
            return true;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("The isolated GPU monitor could not be started.", ex);
            StopWorker(graceful: false);
            _nextStart = DateTimeOffset.UtcNow + _restartDelay;
            return false;
        }
    }

    private void OnTelemetryModeChanged(GpuTelemetryMode mode)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            // Full and quiescing modes share the same worker only while the
            // dGPU still has real clients. Once it becomes inactive Read()
            // switches to Paused and this callback ends the entire process,
            // releasing LHM, NVAPI and all native NVIDIA handles together.
            if (mode is GpuTelemetryMode.Full or
                GpuTelemetryMode.Quiescing)
            {
                return;
            }

            StopWorker();
            _nextStart = DateTimeOffset.MinValue;
            _nonNvidiaFallbackUntil = DateTimeOffset.MinValue;
        }
    }

    private void StopWorker(bool graceful = true)
    {
        var process = _process;
        var writer = _writer;
        var reader = _reader;
        var pipe = _pipe;
        var job = _job;
        // Detach first: a failed Dispose must never leave a poisoned session
        // for the next query, restart, or shutdown to reuse.
        _process = null;
        _writer = null;
        _reader = null;
        _pipe = null;
        _job = null;
        _appliedOverclockSignature = string.Empty;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (graceful && writer is not null)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                        writer.WriteLineAsync("EXIT".AsMemory(), deadline.Token).GetAwaiter().GetResult();
                        writer.FlushAsync(deadline.Token).GetAwaiter().GetResult();
                        process.WaitForExit(750);
                    }
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
        // Closing the pipe before the writer prevents Dispose from blocking
        // while flushing to a hung peer. Every resource gets its own attempt.
        DisposeWorkerResource(pipe);
        DisposeWorkerResource(writer);
        DisposeWorkerResource(reader);
        DisposeWorkerResource(job);
        DisposeWorkerResource(process);
    }

    private static void DisposeWorkerResource(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch { /* A disconnected worker must not prevent remaining cleanup. */ }
    }

    private void RestartWorker()
    {
        lock (_sync)
        {
            StopWorker();
            _nextStart = DateTimeOffset.MinValue;
            _nonNvidiaFallbackUntil = DateTimeOffset.MinValue;
        }
    }

    private static int? TryGetExitCode(Process? process)
    {
        try
        {
            return process is { HasExited: true } ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopWorker();
        }
        GpuTelemetryControl.ModeChanged -= OnTelemetryModeChanged;
        GpuTelemetryControl.RestartRequested -= RestartWorker;
    }
}
