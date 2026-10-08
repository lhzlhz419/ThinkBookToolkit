using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed record OsdMonitor(string DeviceName, string DeviceId, Rect WorkArea)
{
    internal string? PhysicalId { get; init; }
}

internal static class OsdMonitorPolicy
{
    internal static OsdMonitor? Find(OsdMonitorPlacement placement, IReadOnlyList<OsdMonitor> monitors,
        Func<string, string?>? readPhysicalId = null)
    {
        // A missing external monitor is not permission to replace it with
        // the primary screen during a sleep/resume topology transition.
        if (!string.IsNullOrWhiteSpace(placement.DeviceId))
        {
            var exact = monitors.FirstOrDefault(m => string.Equals(m.DeviceId, placement.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        // A MUX switch can give the same panel a different adapter-dependent
        // device path. EDID survives that change. Also migrate old saved paths
        // using their retained PnP registry entry, without guessing by DISPLAYn.
        var physicalId = placement.PhysicalId ?? (readPhysicalId ?? ReadPhysicalId)(placement.DeviceId);
        if (!string.IsNullOrWhiteSpace(physicalId))
        {
            var matches = monitors.Where(m => string.Equals(m.PhysicalId, physicalId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            return null; // Identical EDIDs must not silently select another screen.
        }
        return string.IsNullOrWhiteSpace(placement.DeviceId)
            ? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, placement.DeviceName, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    internal static OsdMonitor? ForWindow(IntPtr handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)
            ? Capture().FirstOrDefault(m => m.DeviceName == info.DeviceName) : null;
    }

    internal static void ResetPosition(ToolkitOsdSettings settings, OsdMonitor monitor)
    {
        var placement = new OsdMonitorPlacement(monitor.DeviceName, monitor.DeviceId, 10, 10)
            { PhysicalId = monitor.PhysicalId };
        if (settings.Orientation == OsdOrientation.Horizontal)
        {
            settings.HorizontalMonitor = placement;
            settings.HorizontalX = settings.HorizontalY = null;
            settings.HorizontalXAnchor = OsdSnapAnchor.Center;
            settings.HorizontalYAnchor = OsdSnapAnchor.None;
        }
        else
        {
            settings.VerticalMonitor = placement;
            settings.VerticalX = settings.VerticalY = null;
            settings.VerticalXAnchor = OsdSnapAnchor.None;
            settings.VerticalYAnchor = OsdSnapAnchor.Center;
        }
    }

    private static string? ReadPhysicalId(string? deviceId)
    {
        // EDD_GET_DEVICE_INTERFACE_NAME: \\?\DISPLAY#model#instance#{guid}
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var parts = deviceId.Split('#');
        if (parts.Length != 4 || !parts[0].Equals(@"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase) ||
            parts[1].IndexOfAny(['\\', '/']) >= 0 || parts[2].IndexOfAny(['\\', '/']) >= 0)
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters");
            return key?.GetValue("EDID") is byte[] edid ? PhysicalIdFromEdid(edid) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    internal static string? PhysicalIdFromEdid(byte[] edid)
    {
        // Hash only the base block: driver-supplied extension blocks may vary.
        if (edid.Length < 128 || !edid.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }) ||
            (edid.Take(128).Sum(b => (int)b) & 255) != 0) return null;
        return Convert.ToHexString(SHA256.HashData(edid.AsSpan(0, 128)));
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
                        new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top))
                        { PhysicalId = ReadPhysicalId(id ?? string.Empty) });
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
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

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
