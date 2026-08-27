using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record SharedHardwareSnapshot(
    double? CpuTemperatureC,
    double? CpuPowerW,
    double? GpuTemperatureC,
    double? GpuPowerW,
    int? Fan1Rpm,
    int? Fan2Rpm,
    string? PerformanceMode);

internal sealed class LocalDataSharingService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<ToolkitRuntimeSnapshot> _snapshotProvider;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _listenerTask;

    public LocalDataSharingService(Func<ToolkitRuntimeSnapshot> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _listener?.IsListening == true;
        }
    }

    public int? Port { get; private set; }

    public void Start(int port)
    {
        ValidatePort(port);
        lock (_gate)
        {
            if (_listener?.IsListening == true && Port == port)
                return;
            StopCore();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch
            {
                listener.Close();
                throw;
            }

            var cancellation = new CancellationTokenSource();
            _listener = listener;
            _cancellation = cancellation;
            Port = port;
            _listenerTask = Task.Run(
                () => ListenAsync(listener, cancellation.Token));
            ToolkitLog.Info(
                $"Local data sharing started at http://127.0.0.1:{port}/.");
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopCore();
    }

    public static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "The data-sharing port must be between 1 and 65535.");
    }

    internal static SharedHardwareSnapshot BuildSnapshot(
        ToolkitRuntimeSnapshot snapshot)
    {
        var temperatures = snapshot.Temperatures;
        var fans = snapshot.Fans;
        return new SharedHardwareSnapshot(
            Finite(temperatures?.CpuTempC),
            Finite(temperatures?.CpuPowerW),
            Finite(temperatures?.GpuTempC),
            Finite(temperatures?.GpuPowerW),
            fans?.Fan1Rpm,
            fans is not null && DeviceModelDetector.HasSecondFan()
                ? fans.Fan2Rpm
                : null,
            snapshot.ItsMode == ItsMode.Unknown
                ? null
                : snapshot.ItsMode.ToString());
    }

    private async Task ListenAsync(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync()
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (
                    cancellationToken.IsCancellationRequested ||
                    !listener.IsListening)
                {
                    break;
                }

                _ = Task.Run(
                    () => HandleRequestAsync(context, cancellationToken),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Local data-sharing listener stopped unexpectedly.", ex);
        }
    }

    private async Task HandleRequestAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = context.Response;
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Cache-Control"] = "no-store";
            if (context.Request.HttpMethod.Equals(
                    "OPTIONS",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (!context.Request.HttpMethod.Equals(
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.Close();
                return;
            }

            var payload = BuildSnapshot(_snapshotProvider());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
            response.Close();
        }
        catch (OperationCanceledException)
        {
            try { context.Response.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("A local data-sharing request failed.", ex);
            try
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch { }
        }
    }

    private void StopCore()
    {
        var listener = _listener;
        var cancellation = _cancellation;
        if (listener is null && cancellation is null)
            return;
        _listener = null;
        _cancellation = null;
        _listenerTask = null;
        Port = null;
        try { cancellation?.Cancel(); } catch { }
        try { listener?.Close(); } catch { }
        cancellation?.Dispose();
        ToolkitLog.Info("Local data sharing stopped.");
    }

    private static double? Finite(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    public void Dispose() => Stop();
}
