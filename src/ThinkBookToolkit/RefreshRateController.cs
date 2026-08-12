using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace ThinkBookToolkit;

internal sealed record DisplayRefreshRateState(
    string DeviceName,
    uint CurrentHz,
    IReadOnlyList<uint> AvailableHz);

internal static class RefreshRateController
{
    internal static bool TryReadState(
        out DisplayRefreshRateState? state,
        out string error)
    {
        state = null;
        try
        {
            var deviceName = FindInternalDisplayDeviceName() ??
                throw new InvalidOperationException(
                    "No active display was found.");
            var current = CreateDevMode();
            if (!EnumDisplaySettings(
                    deviceName,
                    CurrentSettings,
                    ref current))
            {
                throw new InvalidOperationException(
                    "The current display settings could not be read.");
            }

            var frequencies = EnumerateFrequencies(deviceName, current);
            if (frequencies.Count == 0)
            {
                throw new InvalidOperationException(
                    "No refresh rate is available for the current display mode.");
            }

            state = new DisplayRefreshRateState(
                deviceName,
                current.dmDisplayFrequency,
                frequencies);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    internal static bool TrySetRefreshRate(
        uint refreshRate,
        out string error)
    {
        if (!TryReadState(out var state, out error) || state is null)
            return false;
        if (!state.AvailableHz.Contains(refreshRate))
        {
            error = $"{refreshRate} Hz is not available for the current display mode.";
            return false;
        }
        return TryApply(state.DeviceName, refreshRate, out error);
    }

    internal static bool TryCycleInternalDisplay(
        IEnumerable<uint>? configuredRates,
        out uint refreshRate,
        out string error)
    {
        refreshRate = 0;
        if (!TryReadState(out var state, out error) || state is null)
            return false;

        var cycle = EffectiveCycleRates(
            state.AvailableHz,
            configuredRates);
        if (cycle.Count == 0)
        {
            error = "No enabled refresh rate is available for the current display mode.";
            return false;
        }

        refreshRate = SelectNextRefreshRate(cycle, state.CurrentHz);
        return TryApply(state.DeviceName, refreshRate, out error);
    }

    internal static bool TryCycleInternalDisplay(
        out uint refreshRate,
        out string error) =>
        TryCycleInternalDisplay(null, out refreshRate, out error);

    internal static IReadOnlyList<uint> EffectiveCycleRates(
        IEnumerable<uint> availableRates,
        IEnumerable<uint>? configuredRates)
    {
        var available = NormalizeConfiguredRates(availableRates);
        var configured = NormalizeConfiguredRates(configuredRates);
        if (configured.Count > 0)
        {
            var enabled = configured
                .Where(available.Contains)
                .ToArray();
            if (enabled.Length > 0)
                return enabled;
        }
        if (available.Count == 0)
            return [];

        var maximum = available[^1];
        return available
            .Where(rate => rate == 60 || rate == maximum)
            .Distinct()
            .OrderBy(rate => rate)
            .ToArray();
    }

    internal static List<uint> NormalizeConfiguredRates(
        IEnumerable<uint>? values) =>
        (values ?? [])
            .Where(value => value > 1)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

    internal static uint SelectNextRefreshRate(
        IEnumerable<uint> frequencies,
        uint current)
    {
        var available = NormalizeConfiguredRates(frequencies);
        if (available.Count == 0)
            return 0;
        var next = available.FirstOrDefault(
            frequency => frequency > current);
        return next > 0 ? next : available[0];
    }

    private static IReadOnlyList<uint> EnumerateFrequencies(
        string deviceName,
        DevMode current)
    {
        var frequencies = new SortedSet<uint>();
        for (var index = 0; ; index++)
        {
            var candidate = CreateDevMode();
            if (!EnumDisplaySettings(deviceName, index, ref candidate))
                break;
            if (candidate.dmPelsWidth == current.dmPelsWidth &&
                candidate.dmPelsHeight == current.dmPelsHeight &&
                candidate.dmBitsPerPel == current.dmBitsPerPel &&
                candidate.dmDisplayFlags == current.dmDisplayFlags &&
                candidate.dmDisplayFrequency > 1)
            {
                frequencies.Add(candidate.dmDisplayFrequency);
            }
        }
        return frequencies.ToArray();
    }

    private static bool TryApply(
        string deviceName,
        uint refreshRate,
        out string error)
    {
        var current = CreateDevMode();
        if (!EnumDisplaySettings(
                deviceName,
                CurrentSettings,
                ref current))
        {
            error = "The current display settings could not be read.";
            return false;
        }
        current.dmDisplayFrequency = refreshRate;
        current.dmFields |= DisplayFrequencyField;
        var result = ChangeDisplaySettingsEx(
            deviceName,
            ref current,
            IntPtr.Zero,
            UpdateRegistry,
            IntPtr.Zero);
        if (result != Successful)
        {
            error = $"Windows rejected the refresh-rate change (code {result}).";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static string? FindInternalDisplayDeviceName()
    {
        var internalIds = ReadInternalMonitorIds();
        string? primary = null;
        string? firstActive = null;
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = CreateDisplayDevice();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
                break;
            if ((adapter.StateFlags & DisplayDeviceActive) == 0)
                continue;
            firstActive ??= adapter.DeviceName;
            if ((adapter.StateFlags & DisplayDevicePrimary) != 0)
                primary = adapter.DeviceName;
            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = CreateDisplayDevice();
                if (!EnumDisplayDevices(
                        adapter.DeviceName,
                        monitorIndex,
                        ref monitor,
                        0))
                {
                    break;
                }
                var id = MonitorHardwareId(monitor.DeviceID);
                if ((monitor.StateFlags & DisplayDeviceActive) != 0 &&
                    internalIds.Contains(id))
                {
                    return adapter.DeviceName;
                }
            }
        }
        return primary ?? firstActive;
    }

    private static HashSet<string> ReadInternalMonitorIds()
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, Active, VideoOutputTechnology " +
                "FROM WmiMonitorConnectionParams");
            using var items = searcher.Get();
            foreach (ManagementObject item in items)
            {
                using (item)
                {
                    if (item["Active"] is bool active && !active)
                        continue;
                    if (Convert.ToUInt32(item["VideoOutputTechnology"]) !=
                        InternalOutputTechnology)
                    {
                        continue;
                    }
                    var id = MonitorHardwareId(
                        Convert.ToString(item["InstanceName"]));
                    if (!string.IsNullOrWhiteSpace(id))
                        result.Add(id);
                }
            }
        }
        catch
        {
        }
        return result;
    }

    private static string MonitorHardwareId(string? value)
    {
        var parts = (value ?? string.Empty).Split('\\');
        return parts.Length > 1 ? parts[1] : value ?? string.Empty;
    }

    private static DisplayDevice CreateDisplayDevice() => new()
    {
        cb = Marshal.SizeOf<DisplayDevice>()
    };

    private static DevMode CreateDevMode() => new()
    {
        dmSize = (short)Marshal.SizeOf<DevMode>()
    };

    private const int CurrentSettings = -1;
    private const int DisplayDeviceActive = 0x1;
    private const int DisplayDevicePrimary = 0x4;
    private const int Successful = 0;
    private const uint UpdateRegistry = 0x1;
    private const int DisplayFrequencyField = 0x00400000;
    private const uint InternalOutputTechnology = 0x80000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint number,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string deviceName,
        int modeNumber,
        ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string deviceName,
        ref DevMode devMode,
        IntPtr window,
        uint flags,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public int dmDisplayFlags;
        public uint dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
