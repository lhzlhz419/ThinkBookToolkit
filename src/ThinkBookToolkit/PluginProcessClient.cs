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
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private ChildProcessJob? _job;
    internal PluginProcessClient(string directory, PluginManifest manifest, string? hostPath = null)
    { _directory = directory; _manifest = manifest; _hostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "ThinkBookToolkit.PluginHost.exe"); }
    internal async Task<PluginResult> EvaluateAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (_process is null)
            {
                var name = "ThinkBookToolkit.Plugin." + Guid.NewGuid().ToString("N");
                _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var start = new ProcessStartInfo(_hostPath)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _directory };
                start.ArgumentList.Add(name);
                start.ArgumentList.Add(Path.Combine(_directory, _manifest.EntryAssembly));
                start.ArgumentList.Add(_manifest.EntryType);
                _process = Process.Start(start) ?? throw new IOException("Plugin host did not start.");
                _job = new ChildProcessJob(); _job.Assign(_process);
                await _pipe.WaitForConnectionAsync(timeout.Token);
                if (await PluginWire.ReadAsync<string>(_pipe, timeout.Token) != "TBT_PLUGIN_1") throw new IOException("Incompatible plugin host.");
            }
            await PluginWire.WriteAsync(_pipe!, request, timeout.Token);
            var response = await PluginWire.ReadAsync<PluginResponse>(_pipe!, timeout.Token);
            return response.Success && response.Result is not null ? response.Result : throw new IOException(response.Error ?? "Plugin failed.");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        _pipe?.Dispose(); _pipe = null;
        try { if (_process is { HasExited: false } && !_process.WaitForExit(250)) _process.Kill(entireProcessTree: true); } catch { }
        _job?.Dispose(); _job = null;
        _process?.Dispose(); _process = null;
    }
}
