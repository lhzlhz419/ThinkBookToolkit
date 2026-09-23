using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ThinkBookToolkit;

internal static class ToolkitApplicationRestart
{
    private const string WaitOption = "--restart-after-exit";
    internal static bool WaitForPreviousInstance(ref string[] args)
    {
        if (args.FirstOrDefault() != WaitOption) return true;
        if (args.Length < 3 || !int.TryParse(args[1], out var pid) || pid <= 0 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0) return false;
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks == ticks && !previous.WaitForExit(60000)) return false;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch { return false; }
        args = args.Skip(3).ToArray();
        return true;
    }
    internal static ProcessStartInfo CreateStartInfo(string executable, string? entryAssembly, int processId, long startTicks, bool safeMode)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryAssembly ?? throw new InvalidOperationException("The Toolkit entry assembly is unavailable."));
        start.ArgumentList.Add(WaitOption);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        if (safeMode) start.ArgumentList.Add("--disable-plugins");
        return start;
    }
    internal static void Launch(bool safeMode)
    {
        using var current = Process.GetCurrentProcess();
        var start = CreateStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Toolkit executable path is unavailable."),
            Assembly.GetEntryAssembly()?.Location, current.Id, current.StartTime.ToUniversalTime().Ticks, safeMode);
        using var next = Process.Start(start) ?? throw new InvalidOperationException("Toolkit could not be restarted.");
    }
}
