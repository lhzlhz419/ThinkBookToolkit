using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class TrayStartupTests
{
    // Separate process: exercise the same Application.Run() path as production,
    // without initializing any real hardware controllers.
    internal static int Run(bool openEarly)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = new AppSettings { StartWithWindows = true, StartToTray = true, Theme = "dark" };
        Check(ToolkitMainWindow.ShouldStartHidden(settings, true) && !ToolkitMainWindow.ShouldStartHidden(settings, false) &&
            !ToolkitMainWindow.ShouldStartHidden(new AppSettings { StartWithWindows = true }, true) &&
            !ToolkitMainWindow.ShouldStartHidden(new AppSettings { StartToTray = true }, true), "Tray-start policy changed manual-launch behavior.");
        using var runtime = new ToolkitRuntimeService(settings, persistSystemSessionState: false);
        ModernTheme.Apply(application, true);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false, startToTrayRequested: true);
        runtime.AttachWindow(window, startToTrayRequested: true);
        application.MainWindow = window;
        string? failure = null;
        var stage = 0;
        var allowVisible = false;
        var shown = 0;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        void Fail(Exception ex)
        {
            failure = ex.ToString();
            typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            application.Shutdown();
        }
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible) return;
            shown++;
            if (!allowVisible) Fail(new InvalidOperationException("Main window appeared during tray startup."));
        };
        application.DispatcherUnhandledException += (_, args) => { args.Handled = true; Fail(args.Exception); };
        if (openEarly) application.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            allowVisible = true;
            runtime.ShowMainWindow();
        }));
        window.QueueHiddenStartup();
        timer.Tick += (_, _) =>
        {
            try
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Startup/exit test timed out.");
                var handle = new WindowInteropHelper(window).Handle;
                switch (stage++)
                {
                    case 0:
                        Check((bool)typeof(ToolkitMainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                            "Hidden startup never initialized.");
                        if (openEarly)
                            Check(window.IsVisible && window.ShowInTaskbar && shown == 1, "Explicit early open was hidden by startup callbacks.");
                        else
                        {
                            Check(!window.IsVisible && !window.ShowInTaskbar && !IsWindowVisible(handle) && shown == 0,
                                "Hidden startup displayed a window or taskbar entry.");
                            allowVisible = true;
                            runtime.ShowMainWindow();
                        }
                        break;
                    case 1:
                        Check(window.IsVisible && IsWindowVisible(handle) && window.ShowInTaskbar, "Tray open did not show the window normally.");
                        CheckCaption(window, handle);
                        // Simulate a native-caption reset without changing WPF's
                        // title. Restoring from the tray must resynchronize it.
                        Check(SetWindowText(handle, ""), "Could not prepare stale native caption.");
                        Check(window.Title == $"ThinkBook Toolkit v{ApplicationUpdateService.CurrentVersionText}", "Fixture unexpectedly changed the managed title.");
                        runtime.HideToTray();
                        break;
                    case 2:
                        Check(!window.IsVisible && !IsWindowVisible(handle), "Hide-to-tray stopped working.");
                        runtime.ShowMainWindow();
                        break;
                    case 3:
                        Check(window.IsVisible && shown == 2, "Window could not be reopened.");
                        CheckCaption(window, handle);
                        runtime.HideToTray();
                        runtime.RequestExit();
                        break;
                }
            }
            catch (Exception ex) { Fail(ex); }
        };
        timer.Start();
        application.Run();
        timer.Stop();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Check(stage >= 4, "Application exited before the visibility/exit checks finished.");
        Console.WriteLine(openEarly ? "Early explicit open survives hidden startup; tray reopen/exit passed."
            : "Tray startup initialized without showing an HWND/taskbar entry; explicit open/reopen/exit passed.");
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void CheckCaption(ToolkitMainWindow window, IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetWindowText(handle, text, text.Capacity);
        Check(text.ToString() == window.Title, $"Native window title is missing: WPF='{window.Title}', HWND='{text}'.");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr handle, string text);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
}
