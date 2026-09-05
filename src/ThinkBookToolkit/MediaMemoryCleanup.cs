using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ThinkBookToolkit;

internal static class MediaMemoryCleanup
{
    public static void CollectAndTrim(string reason)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var workingBefore = process.WorkingSet64;
        var managedBefore = GC.GetTotalMemory(false);
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            2,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            2,
            GCCollectionMode.Optimized,
            blocking: true,
            compacting: false);
        var trimError = EmptyWorkingSet(process.Handle)
            ? 0
            : Marshal.GetLastWin32Error();
        process.Refresh();
        ToolkitLog.Info(
            $"Media memory cleanup ({reason}): " +
            $"managed={Megabytes(managedBefore):0.0}->" +
            $"{Megabytes(GC.GetTotalMemory(false)):0.0} MB; " +
            $"private={Megabytes(privateBefore):0.0}->" +
            $"{Megabytes(process.PrivateMemorySize64):0.0} MB; " +
            $"working={Megabytes(workingBefore):0.0}->" +
            $"{Megabytes(process.WorkingSet64):0.0} MB; " +
            $"trimError={trimError}.");
    }

    private static double Megabytes(long bytes) => bytes / 1048576d;

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
