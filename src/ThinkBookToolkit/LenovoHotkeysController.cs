using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal static class LenovoHotkeysController
{
    private const string ServiceName = "LenovoFnAndFunctionKeys";
    private const string UwpStartupRoot =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private static readonly string[] ProcessPrefixes =
    [
        "LenovoUtilityUI",
        "LenovoUtilityService",
        "LenovoSmartKey"
    ];

    internal static Task DisableAsync() => Task.Run(() =>
    {
        SetUwpStartup(enabled: false);
        if (ServiceInstalled())
        {
            ConfigureServiceStart("disabled");
            StopService();
        }
        KillProcesses();
    });

    internal static Task EnableAsync() => Task.Run(() => Enable());

    internal static void Enable(bool startService = true)
    {
        SetUwpStartup(enabled: true);
        if (!ServiceInstalled())
            return;
        ConfigureServiceStart("auto");
        if (startService)
            StartService();
    }

    private static bool ServiceInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(service =>
                string.Equals(
                    service.ServiceName,
                    ServiceName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void StopService()
    {
        using var service = new ServiceController(ServiceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
            return;
        if (!service.CanStop)
            return;
        service.Stop();
        service.WaitForStatus(
            ServiceControllerStatus.Stopped,
            TimeSpan.FromSeconds(10));
    }

    private static void StartService()
    {
        using var service = new ServiceController(ServiceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running)
            return;
        service.Start();
        service.WaitForStatus(
            ServiceControllerStatus.Running,
            TimeSpan.FromSeconds(10));
    }

    private static void ConfigureServiceStart(string value)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add(ServiceName);
        startInfo.ArgumentList.Add("start=");
        startInfo.ArgumentList.Add(value);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start sc.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not configure {ServiceName}: " +
                (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
        }
    }

    private static void KillProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    var matchesKnownProcess = ProcessPrefixes.Any(prefix =>
                        name.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase));
                    var isLegacyUtility =
                        name.Equals("utility", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            process.MainModule?.FileVersionInfo.FileDescription,
                            "Lenovo Hotkeys",
                            StringComparison.OrdinalIgnoreCase);
                    if (!matchesKnownProcess && !isLegacyUtility)
                        continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Some protected processes cannot be inspected. The
                    // service/UWP startup state remains the source of truth.
                }
            }
        }
    }

    private static void SetUwpStartup(bool enabled)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                UwpStartupRoot,
                writable: false);
            var appKeyName = root?.GetSubKeyNames().FirstOrDefault(name =>
                name.Contains(
                    "LenovoUtility",
                    StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(appKeyName))
                return;
            using var startup = Registry.CurrentUser.OpenSubKey(
                $@"{UwpStartupRoot}\{appKeyName}\LenovoUtilityID",
                writable: true);
            startup?.SetValue(
                "State",
                enabled ? 2 : 1,
                RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "Lenovo Utility startup state could not be changed: " +
                ex.Message);
        }
    }
}
