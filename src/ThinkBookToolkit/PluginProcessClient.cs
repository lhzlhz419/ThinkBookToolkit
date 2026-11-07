using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed class PluginProcessClient : IDisposable
{
    private readonly string _directory;
    private readonly PluginManifest _manifest;
    private readonly string _hostPath;
    private readonly object _sessionLock = new();
    private bool _disposed;
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private ChildProcessJob? _job;
    internal PluginProcessClient(string directory, PluginManifest manifest, string? hostPath = null)
    { _directory = directory; _manifest = manifest; _hostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.PluginHost.exe"); }
    // Only immutable request data crosses this boundary. Pipe framing, including
    // JSON serialization and all continuations, must not wait for a WPF UI turn.
    internal Task<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => ExchangeAsync(request, cancellationToken), cancellationToken);

    private async Task<PluginResult> ExchangeAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stage = "connecting to the worker";
        try
        {
            var (pipe, needsHandshake) = GetSession();
            if (needsHandshake)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await pipe.WaitForConnectionAsync(timeout.Token);
                stage = "reading the worker handshake";
                if (await PluginWire.ReadAsync<string>(pipe, timeout.Token) != "TBT_PLUGIN_1") throw new InvalidDataException("Incompatible plugin host.");
            }
            // Starting a cold worker should not consume the request's I/O budget.
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            stage = "writing the request";
            await PluginWire.WriteAsync(pipe, request, timeout.Token);
            stage = "reading the response";
            var response = await PluginWire.ReadAsync<PluginResponse>(pipe, timeout.Token);
            if (!response.Success && response.ErrorKind == "timeout") throw new TimeoutException(response.Error ?? "Plugin evaluation timed out.");
            return response.Success && response.Result is not null ? response.Result : throw new InvalidOperationException(response.Error ?? "Plugin failed.");
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Dispose();
            throw new TimeoutException($"Plugin {_manifest.Id}: {request.Operation} IPC timed out while {stage} (5 s deadline).", ex);
        }
        catch { Dispose(); throw; }
    }

    private (NamedPipeServerStream Pipe, bool NeedsHandshake) GetSession()
    {
        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipe is not null) return (_pipe, false);
            var name = "ThinkBookToolkit.Plugin." + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(_hostPath)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _directory };
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Path.Combine(_directory, _manifest.EntryAssembly));
            start.ArgumentList.Add(_manifest.EntryType);
            _process = Process.Start(start) ?? throw new IOException("Plugin host did not start.");
            _job = new ChildProcessJob();
            _job.Assign(_process);
            return (_pipe, true);
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream? pipe;
        Process? process;
        ChildProcessJob? job;
        lock (_sessionLock)
        {
            if (_disposed) return;
            _disposed = true;
            pipe = _pipe; _pipe = null;
            process = _process; _process = null;
            job = _job; _job = null;
        }
        try { pipe?.Dispose(); } catch { }
        try { if (process is { HasExited: false } && !process.WaitForExit(250)) process.Kill(entireProcessTree: true); } catch { }
        try { job?.Dispose(); } catch { }
        try { process?.Dispose(); } catch { }
    }
}
