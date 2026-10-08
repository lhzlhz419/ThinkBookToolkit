using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal static class BoundedProcess
{
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var deadline = new CancellationTokenSource(timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Helper process could not start.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        var completion = Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
        try
        {
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            _ = completion.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("Helper process exceeded its execution timeout.");
            throw;
        }
    }
}
