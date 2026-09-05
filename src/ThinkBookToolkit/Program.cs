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
        if (Guardian.GuardianEntryPoint.TryRun(args))
            return;
        if (args.Any(argument => string.Equals(
                argument,
                "--exit-for-update",
                StringComparison.OrdinalIgnoreCase)))
        {
            SingleInstanceCoordinator.TrySignalExitForUpdate();
            return;
        }

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
                HardwareAccelerationManager.ApplyForStartup(settings);
                if (settings.StartWithWindows)
                {
                    var startupTaskError =
                        MainWindow.ApplyStartupTaskSetting(settings);
                    if (!string.IsNullOrWhiteSpace(startupTaskError))
                    {
                        ToolkitLog.Warning(
                            "The startup task could not be refreshed: " +
                            startupTaskError);
                    }
                }
                // Select software rendering and suppress NVIDIA telemetry only
                // for startup recovery when the current state already expects
                // the dGPU to be absent. Runtime mode and power transitions
                // continue monitoring until PnP reports the adapter removed.
                HybridAutoGpuManager.PrepareForApplicationStartup();
                ModernTheme.Apply(app, ToolkitRuntimeService.ResolveDarkTheme(settings.Theme));
                var startToTrayRequested = args.Any(argument =>
                    string.Equals(argument, "--startup-tray", StringComparison.OrdinalIgnoreCase));
                var launchedAtStartup = args.Any(argument =>
                    string.Equals(
                        argument,
                        "--startup",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        argument,
                        "--startup-tray",
                        StringComparison.OrdinalIgnoreCase));
                using var runtime = new ToolkitRuntimeService(
                    settings,
                    launchedAtStartup);
                SessionEndingCancelEventHandler sessionEnding = (_, eventArgs) =>
                {
                    try
                    {
                        runtime.PrepareForSystemShutdown(
                            eventArgs.ReasonSessionEnding);
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Error(
                            "System-shutdown cleanup failed.",
                            ex);
                    }
                    finally
                    {
                        // Cleanup runs synchronously, but Toolkit must never
                        // cancel the Windows shutdown request.
                        eventArgs.Cancel = false;
                    }
                };
                app.SessionEnding += sessionEnding;
                try
                {
                    var window = new ToolkitMainWindow(
                        runtime,
                        enableHardwareDetection: true,
                        startToTrayRequested);
                    runtime.AttachWindow(window, startToTrayRequested);
                    singleInstance!.Listen(
                        () => app.Dispatcher.BeginInvoke(
                            new Action(() => runtime.ShowMainWindow())),
                        () => app.Dispatcher.BeginInvoke(
                            new Action(runtime.RequestExit)));
                    app.MainWindow = window;
                    app.Run(window);
                }
                finally
                {
                    app.SessionEnding -= sessionEnding;
                }
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
