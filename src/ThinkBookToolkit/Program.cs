using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ThinkBookToolkit;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            ToolkitLog.Initialize();
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ToolkitLog.Shutdown();
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ToolkitLog.Error("Unobserved background task exception.", args.Exception);
                args.SetObserved();
            };

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
            if (!SingleInstanceCoordinator.TryAcquire(out var singleInstance))
                return;
            using (singleInstance)
            {
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
                singleInstance!.Listen(() => app.Dispatcher.BeginInvoke(
                    new Action(() => runtime.ShowMainWindow())));
                app.MainWindow = window;
                app.Run(window);
            }
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
            "详细诊断信息已写入配置文件夹下的 log 文件夹。\r\n" +
            "Diagnostic details were written to the log folder beside the configuration file.";
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

        ToolkitLog.Error("Unhandled exception.", exception);
    }
}
