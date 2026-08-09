using System;
using System.IO;
using System.Text;

namespace ThinkBookToolkit.Guardian;

internal sealed class GuardianLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;

    public GuardianLog(string? directory, string component = "watchdog")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") +
                $"_{component}_{Environment.ProcessId}.log");
            _writer = new StreamWriter(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + Environment.NewLine + exception);

    private void Write(string level, string message)
    {
        try
        {
            lock (_sync)
                _writer?.WriteLine($"[{DateTimeOffset.Now:O}] [{level}] {message}");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
            _writer?.Dispose();
    }
}
