using System;
using System.IO;
using System.Text;

namespace ThinkBookToolkit;

internal static class ToolkitLog
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;

    public static string? CurrentPath { get; private set; }

    public static void Initialize()
    {
        try
        {
            lock (Sync)
            {
                if (_writer is not null)
                    return;
                var directory = Path.Combine(
                    Path.GetDirectoryName(CurveProfileStore.SettingsPath)!,
                    "log");
                Directory.CreateDirectory(directory);
                CurrentPath = Path.Combine(
                    directory,
                    DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") +
                    $"_{Environment.ProcessId}.log");
                _writer = new StreamWriter(
                    new FileStream(CurrentPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.Read),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                Info("ThinkBook Toolkit started.");
                Info($"Process: {Environment.ProcessId}; OS: {Environment.OSVersion}; " +
                     $"64-bit process: {Environment.Is64BitProcess}");
            }
        }
        catch
        {
            CurrentPath = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + Environment.NewLine + exception);

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [INFO] ThinkBook Toolkit stopped.");
                _writer.Dispose();
            }
            catch
            {
            }
            _writer = null;
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
                _writer?.WriteLine($"[{DateTimeOffset.Now:O}] [{level}] {message}");
        }
        catch
        {
        }
    }
}
