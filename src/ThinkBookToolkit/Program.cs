using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace ThinkBookToolkit;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, args) =>
            {
                LogException(args.Exception);
                MessageBox.Show(
                    FormatExceptionForDisplay(args.Exception),
                    "ThinkBook Toolkit error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };
            if (TryApplyInstallerConfiguration(args))
                return;
            ConfigurationMigrationService.EnsureInitialized();
            var settings = CurveProfileStore.LoadSettings();
            CurveProfileStore.ApplyPendingInstallerSettings(settings);
            ModernTheme.Apply(app, ToolkitRuntimeService.ResolveDarkTheme(settings.Theme));
            var startToTrayRequested = args.Any(argument =>
                string.Equals(argument, "--startup-tray", StringComparison.OrdinalIgnoreCase));
            using var runtime = new ToolkitRuntimeService(settings);
            var window = new ToolkitMainWindow(
                runtime,
                enableHardwareDetection: true,
                startToTrayRequested);
            runtime.AttachWindow(window, startToTrayRequested);
            app.MainWindow = window;
            app.Run(window);
        }
        catch (Exception ex)
        {
            LogException(ex);
            MessageBox.Show(
                FormatExceptionForDisplay(ex),
                "ThinkBook Toolkit startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    internal static string FormatExceptionForDisplay(Exception exception)
    {
        var error = exception.GetBaseException();
        return
            $"{error.GetType().Name}: {error.Message}\r\n\r\n" +
            "详细诊断信息已写入 ~/.thinkbook_toolkit/csharp-crash.log。\r\n" +
            "Diagnostic details were written to ~/.thinkbook_toolkit/csharp-crash.log.";
    }

    private static bool TryApplyInstallerConfiguration(
        string[] args)
    {
        const string option = "--configure-lenovo-dll-directory";
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        var directory = index + 1 < args.Length
            ? LenovoDependencyDirectory.Normalize(args[index + 1])
            : string.Empty;
        CurveProfileStore.StageInstallerSettings(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            directory);
        return true;
    }

    private static void LogException(Exception? exception)
    {
        if (exception is null)
            return;

        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thinkbook_toolkit");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "csharp-crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]\r\n{exception}\r\n\r\n");
        }
        catch
        {
        }
    }
}
