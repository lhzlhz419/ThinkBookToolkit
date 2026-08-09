using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

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
    string MemoryTemperatureSensor);

internal sealed class GpuMonitorWorkerClient : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NonNvidiaFallbackDuration =
        TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
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

    public GpuMonitorWorkerSnapshot? Read()
    {
        lock (_sync)
        {
            if (_disposed)
                return null;

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

            if (!EnsureWorker())
                return null;

            try
            {
                _writer!.WriteLine(
                    DateTimeOffset.UtcNow < _nonNvidiaFallbackUntil
                        ? Guardian.GpuMonitorWorker.ReadNonNvidiaCommand
                        : Guardian.GpuMonitorWorker.ReadCommand);
                _writer.Flush();
                var responseTask = _reader!.ReadLineAsync();
                if (!responseTask.Wait(ResponseTimeout))
                    throw new TimeoutException("The isolated GPU monitor did not respond within 5 seconds.");
                var response = responseTask.Result;
                if (response is null)
                    throw new EndOfStreamException(
                        "The isolated GPU monitor closed its pipe unexpectedly.");
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidDataException(
                        "The isolated GPU monitor returned an empty response.");
                if (response == "null")
                    return null;
                return JsonSerializer.Deserialize<GpuMonitorWorkerSnapshot>(response);
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
                StopWorker();
                _nextStart = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
        }
    }

    private bool EnsureWorker()
    {
        if (_process is { HasExited: false })
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

        StopWorker();
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "ThinkBookToolkit.exe");
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
                    Arguments = "--gpu-worker " + pipeName,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            if (!process.Start())
                throw new InvalidOperationException("The isolated GPU monitor process did not start.");
            _process = process;
            var job = new ChildProcessJob();
            job.Assign(process);
            _job = job;
            var connectionTask = pipe.WaitForConnectionAsync();
            if (!connectionTask.Wait(ResponseTimeout))
                throw new TimeoutException("The isolated GPU monitor did not connect within 5 seconds.");
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
            StopWorker();
            _nextStart = DateTimeOffset.UtcNow + RestartDelay;
            return false;
        }
    }

    private void StopWorker()
    {
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    _writer?.WriteLine("EXIT");
                    _writer?.Flush();
                    if (!process.WaitForExit(750))
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
            process.Dispose();
        }
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
        _job?.Dispose();
        _job = null;
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
    }
}
