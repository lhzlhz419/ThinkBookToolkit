using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace ThinkBookToolkit;

internal sealed record OsdMonitor(string DeviceName, string DeviceId, Rect WorkArea);

internal static class OsdMonitorPolicy
{
    internal static OsdMonitor? Find(OsdMonitorPlacement placement, IReadOnlyList<OsdMonitor> monitors)
    {
        // A missing external monitor is not permission to replace it with
        // the primary screen during a sleep/resume topology transition.
        if (!string.IsNullOrWhiteSpace(placement.DeviceId))
            return monitors.FirstOrDefault(m => string.Equals(m.DeviceId, placement.DeviceId, StringComparison.OrdinalIgnoreCase));
        return monitors.FirstOrDefault(m => string.Equals(m.DeviceName, placement.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    internal static OsdMonitorPlacement? Normalize(OsdMonitorPlacement? placement) =>
        placement is not null && (!string.IsNullOrWhiteSpace(placement.DeviceId) || !string.IsNullOrWhiteSpace(placement.DeviceName)) &&
        double.IsFinite(placement.OffsetX) && double.IsFinite(placement.OffsetY) ? placement : null;

    internal static IReadOnlyList<OsdMonitor> Capture()
    {
        // Query active monitors afresh rather than relying on Screen.AllScreens
        // cached before sleep or before the latest display-change notification.
        var result = new List<OsdMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr state) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                var id = EnumDisplayDevices(info.DeviceName, 0, ref device, 1) ? device.DeviceId : string.Empty;
                var work = info.WorkArea;
                if (work.Right > work.Left && work.Bottom > work.Top)
                    result.Add(new(info.DeviceName, id ?? string.Empty,
                        new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top)));
                return true;
            }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr state);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
}
