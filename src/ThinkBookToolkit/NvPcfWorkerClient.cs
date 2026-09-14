using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ThinkBookToolkit;

internal sealed class NvPcfWorkerClient : IDisposable
{
    private readonly object _sync = new();
    private readonly string _executable;
    private readonly string _argument;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryDelay;
    private Process? _process;
    private ChildProcessJob? _job;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private DateTimeOffset _nextStart;
    private bool _disposed;

    internal NvPcfWorkerClient(string? executable = null, string argument = "--nvpcf-worker",
        TimeSpan? timeout = null, TimeSpan? retryDelay = null)
    {
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.GpuWorker.exe");
        _argument = argument;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    }

    internal NvPcfResponse Execute(NvPcfRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (DateTimeOffset.UtcNow < _nextStart)
                throw new InvalidOperationException("NVAPI 功耗接口暂不可用，等待显卡驱动恢复后重试。");
            try
            {
                EnsureWorker();
                _writer!.WriteLine(JsonSerializer.Serialize(request));
                _writer.Flush();
                var line = ReadLine();
                var response = JsonSerializer.Deserialize<NvPcfResponse>(line)
                    ?? throw new InvalidDataException("Empty NVPCF worker response.");
                if (!response.Success)
                    throw new InvalidOperationException(response.Error ?? "NVPCF driver operation failed.");
                if (request.Operation is "READ" or "WRITE" && response.Snapshot is null)
                    throw new InvalidDataException("NVPCF worker returned no power values.");
                return response;
            }
            catch (Exception ex)
            {
                var exit = _process is { HasExited: true } process
                    ? $" Exit code: 0x{unchecked((uint)process.ExitCode):X8}." : "";
                _nextStart = DateTimeOffset.UtcNow + _retryDelay;
                StopWorker(graceful: false);
                var message = "Isolated NVPCF worker failed during " + request.Operation + "." + exit;
                ToolkitLog.Error(message, ex);
                // In particular, never replay a write whose completion is
                // unknown. A later explicit operation starts a fresh session.
                throw new InvalidOperationException(message + " " + ex.GetBaseException().Message, ex);
            }
        }
    }

    private string ReadLine()
    {
        var read = _reader!.ReadLineAsync();
        if (!read.Wait(_timeout))
            throw new TimeoutException("NVPCF worker did not respond before the timeout.");
        return read.GetAwaiter().GetResult()
            ?? throw new EndOfStreamException("NVPCF worker exited or disconnected.");
    }

    private void EnsureWorker()
    {
        if (_process is { HasExited: false } && _pipe is { IsConnected: true }) return;
        StopWorker(graceful: false);
        if (!File.Exists(_executable))
            throw new FileNotFoundException("The isolated NVPCF worker is missing. Reinstall the complete Toolkit package.", _executable);
        var pipeName = "ThinkBookToolkit.NvPcf." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = Path.GetDirectoryName(_executable)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_argument);
        start.ArgumentList.Add(pipeName);
        _process = Process.Start(start) ?? throw new InvalidOperationException("NVPCF worker did not start.");
        _job = new ChildProcessJob();
        _job.Assign(_process);
        if (!_pipe.WaitForConnectionAsync().Wait(_timeout))
            throw new TimeoutException("NVPCF worker did not connect before the timeout.");
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 1024, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, true);
        if (ReadLine() != NvPcfWorker.Ready)
            throw new InvalidDataException("Incompatible NVPCF worker protocol.");
    }

    internal void Shutdown()
    {
        lock (_sync) StopWorker(graceful: true);
        // Deliberately keep the failure cooldown across visibility changes.
    }

    private void StopWorker(bool graceful)
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                if (graceful && _writer is not null)
                {
                    _writer.WriteLine("EXIT");
                    _writer.Flush();
                    process.WaitForExit(250);
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            // Closing the job also kills a hung native unload or child tool.
            _job?.Dispose();
            _job = null;
            _process?.Dispose();
            _process = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            try { _reader?.Dispose(); } catch { }
            _reader = null;
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopWorker(graceful: false);
        }
    }
}
