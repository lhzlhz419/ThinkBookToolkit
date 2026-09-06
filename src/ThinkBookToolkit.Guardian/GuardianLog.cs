using System;
using System.IO;
using System.Text;

namespace ThinkBookToolkit.Guardian;

internal sealed class GuardianLog : IDisposable
{
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private readonly string? _directory;
    private readonly string _component;

    public GuardianLog(string? directory, string component = "watchdog")
    {
        _directory = directory;
        _component = component;
    }

    private void OpenWriter()
    {
        var directory = _directory;
        var component = _component;
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

    private static bool Allows(string configured, string level) => configured switch
    {
        "INFO" => true,
        "WARN" => level is "WARN" or "ERROR",
        "ERROR" => level == "ERROR",
        _ => false
    };

    private void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                var configured = "ERROR";
                try
                {
                    var path = Path.Combine(Path.GetDirectoryName(_directory!)!, "app_settings.csharp.json");
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    if (json.RootElement.TryGetProperty("LogLevel", out var value))
                        configured = value.GetString() ?? "ERROR";
                }
                catch { }
                if (!Allows(configured, level)) return;
                if (_writer is null) OpenWriter();
                _writer?.WriteLine($"[{DateTimeOffset.Now:O}] [{level}] {message}");
            }
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
