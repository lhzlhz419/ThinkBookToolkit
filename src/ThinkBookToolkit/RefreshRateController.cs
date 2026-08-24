using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using WindowsDisplayAPI;
using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DeviceContext;

namespace ThinkBookToolkit;

internal readonly record struct DisplayRefreshRateMode(
    uint Frequency,
    bool IsDynamic = false)
{
    public string DisplayName => IsDynamic
        ? $"Dynamic ({Frequency} Hz)"
        : $"{Frequency} Hz";
}

internal sealed record DisplayRefreshRateState(
    string DeviceName,
    uint CurrentHz,
    IReadOnlyList<uint> AvailableHz,
    bool DynamicSupported = false,
    bool DynamicActive = false,
    uint DynamicMaximumHz = 0)
{
    public DisplayRefreshRateMode CurrentMode => new(
        DynamicActive ? DynamicMaximumHz : CurrentHz,
        DynamicActive);

    public IReadOnlyList<DisplayRefreshRateMode> AvailableModes =>
        AvailableHz
            .Select(rate => new DisplayRefreshRateMode(rate))
            .Concat(DynamicSupported
                ? [new DisplayRefreshRateMode(DynamicMaximumHz, true)]
                : [])
            .ToArray();
}

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

            var dynamic = ReadDynamicState(
                deviceName,
                frequencies[^1]);
            state = new DisplayRefreshRateState(
                deviceName,
                current.dmDisplayFrequency,
                frequencies,
                dynamic.DynamicSupported,
                dynamic.DynamicActive,
                dynamic.DynamicMaximumHz);
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
        out string error) =>
        TrySetRefreshRate(
            new DisplayRefreshRateMode(refreshRate),
            out error);

    internal static bool TrySetRefreshRate(
        DisplayRefreshRateMode mode,
        out string error)
    {
        if (!TryReadState(out var state, out error) || state is null)
            return false;
        if (!state.AvailableModes.Contains(mode))
        {
            error = $"{mode.DisplayName} is not available for the current display mode.";
            return false;
        }
        return TryApply(state.DeviceName, mode, out error);
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
        return TryApply(
            state.DeviceName,
            new DisplayRefreshRateMode(refreshRate),
            out error);
    }

    internal static bool TryCycleInternalDisplay(
        IEnumerable<uint>? configuredRates,
        bool includeDynamic,
        out DisplayRefreshRateMode refreshRate,
        out string error)
    {
        refreshRate = default;
        if (!TryReadState(out var state, out error) || state is null)
            return false;

        var cycle = EffectiveCycleModes(
            state,
            configuredRates,
            includeDynamic);
        if (cycle.Count == 0)
        {
            error = "No enabled refresh rate is available for the current display mode.";
            return false;
        }

        refreshRate = SelectNextRefreshRate(cycle, state.CurrentMode);
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

    internal static IReadOnlyList<DisplayRefreshRateMode> EffectiveCycleModes(
        DisplayRefreshRateState state,
        IEnumerable<uint>? configuredRates,
        bool includeDynamic)
    {
        var result = EffectiveCycleRates(
                state.AvailableHz,
                configuredRates)
            .Select(rate => new DisplayRefreshRateMode(rate))
            .ToList();
        if (includeDynamic && state.DynamicSupported)
            result.Add(new(state.DynamicMaximumHz, true));
        return result;
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

    internal static DisplayRefreshRateMode SelectNextRefreshRate(
        IReadOnlyList<DisplayRefreshRateMode> modes,
        DisplayRefreshRateMode current)
    {
        if (modes.Count == 0)
            return default;
        var index = -1;
        for (var candidate = 0; candidate < modes.Count; candidate++)
        {
            if (modes[candidate] == current)
            {
                index = candidate;
                break;
            }
        }
        return modes[(index + 1 + modes.Count) % modes.Count];
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
        DisplayRefreshRateMode mode,
        out string error)
    {
        try
        {
            var display = FindDisplay(deviceName);
            if (display is not null)
            {
                ApplyUsingDisplayConfig(display, mode);
                error = string.Empty;
                return true;
            }
            if (mode.IsDynamic)
            {
                error = "The internal display path required for Dynamic Refresh Rate was not found.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }

        var current = CreateDevMode();
        if (!EnumDisplaySettings(
                deviceName,
                CurrentSettings,
                ref current))
        {
            error = "The current display settings could not be read.";
            return false;
        }
        current.dmDisplayFrequency = mode.Frequency;
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

    private static (bool DynamicSupported, bool DynamicActive, uint DynamicMaximumHz)
        ReadDynamicState(string deviceName, uint maximumHz)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            maximumHz <= MinimumDynamicRefreshRateHz)
        {
            return (false, false, 0);
        }
        try
        {
            var display = FindDisplay(deviceName);
            if (display is null)
                return (false, false, 0);
            var source = display.DisplayScreen.ToPathDisplaySource();
            var path = PathInfo.GetActivePaths(virtualModeAware: true)
                .FirstOrDefault(candidate => candidate.DisplaySource == source);
            var target = path?.TargetsInfo.FirstOrDefault(candidate =>
                candidate.IsCurrentlyInUse || candidate.IsPathActive);
            var supported = path?.TargetsInfo.Any(candidate =>
                candidate.IsVirtualModeSupportedByPath) == true;
            return supported
                ? (true, target?.IsBoostRefreshRate == true, maximumHz)
                : (false, false, 0);
        }
        catch
        {
            return (false, false, 0);
        }
    }

    private static Display? FindDisplay(string deviceName) =>
        Display.GetDisplays().FirstOrDefault(display =>
            string.Equals(
                display.DisplayScreen.ScreenName,
                deviceName,
                StringComparison.OrdinalIgnoreCase));

    private static void ApplyUsingDisplayConfig(
        Display display,
        DisplayRefreshRateMode mode)
    {
        var screen = display.DisplayScreen;
        var current = screen.CurrentSetting;
        var possible = screen.GetPossibleSettings()
            .Where(setting =>
                setting.Resolution == current.Resolution &&
                setting.ColorDepth == current.ColorDepth &&
                setting.IsInterlaced == current.IsInterlaced)
            .ToArray();
        var targetFrequency = mode.IsDynamic
            ? DynamicLowFrequency(mode.Frequency, possible.Select(setting => setting.Frequency))
            : checked((int)mode.Frequency);
        var candidate = possible.FirstOrDefault(setting =>
            setting.Frequency == targetFrequency)
            ?? throw new InvalidOperationException(
                $"No matching display mode exists for {mode.DisplayName}.");
        var targetSetting = new DisplaySetting(
            candidate,
            current.Position,
            current.Orientation,
            DisplayFixedOutput.Default);
        var source = screen.ToPathDisplaySource();
        var paths = PathInfo.GetActivePaths(virtualModeAware: true);
        var target = paths
            .Where(path => path.DisplaySource == source)
            .SelectMany(path => path.TargetsInfo)
            .FirstOrDefault();
        if (mode.IsDynamic && NeedsDynamicPreInitialization(target, mode.Frequency))
        {
            var physical = possible.FirstOrDefault(setting =>
                setting.Frequency == mode.Frequency)
                ?? throw new InvalidOperationException(
                    $"No physical {mode.Frequency} Hz mode is available for Dynamic Refresh Rate.");
            screen.SetSettings(
                new DisplaySetting(
                    physical,
                    current.Position,
                    current.Orientation,
                    DisplayFixedOutput.Default),
                apply: true);
            WaitForPhysicalRefreshRate(source, mode.Frequency);
            paths = PathInfo.GetActivePaths(virtualModeAware: true);
        }

        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            if (path.DisplaySource != source)
                continue;
            var targetInfos = path.TargetsInfo.Select(pathTarget =>
            {
                if (mode.IsDynamic && pathTarget.IsVirtualModeSupportedByPath)
                {
                    var size = new global::System.Drawing.Size(
                        targetSetting.Resolution.Width,
                        targetSetting.Resolution.Height);
                    var rectangle = new global::System.Drawing.Rectangle(
                        0,
                        0,
                        size.Width,
                        size.Height);
                    return new PathTargetInfo(
                        pathTarget.DisplayTarget,
                        pathTarget.SignalInfo!,
                        new PathTargetDesktopImage(size, rectangle, rectangle),
                        pathTarget.Rotation,
                        pathTarget.Scaling,
                        pathTarget.IsVirtualModeSupportedByPath,
                        true,
                        (ulong)targetSetting.Frequency * 1000);
                }
                return new PathTargetInfo(
                    pathTarget.DisplayTarget,
                    new PathTargetSignalInfo(
                        targetSetting,
                        targetSetting.Resolution),
                    pathTarget.Rotation,
                    pathTarget.Scaling,
                    pathTarget.IsVirtualModeSupportedByPath,
                    false,
                    0);
            }).ToArray();
            paths[index] = new PathInfo(
                path.DisplaySource,
                path.Position,
                targetSetting.Resolution,
                path.PixelFormat,
                targetInfos);
        }

        PathInfo.ApplyPathInfos(paths, saveToDatabase: true);
    }

    private static bool NeedsDynamicPreInitialization(
        PathTargetInfo? target,
        uint physicalFrequency) =>
        target is null ||
        !target.IsDesktopImageInformationAvailable ||
        !target.IsBoostRefreshRate ||
        !target.IsSignalInformationAvailable ||
        Math.Abs(
            (long)(target.SignalInfo?.VerticalSyncFrequencyInMillihertz ?? 0) -
            physicalFrequency * 1000L) >= 1000;

    private static void WaitForPhysicalRefreshRate(
        PathDisplaySource source,
        uint physicalFrequency)
    {
        for (var attempt = 0; attempt < DynamicPreInitializationAttempts; attempt++)
        {
            Thread.Sleep(DynamicPreInitializationDelayMs);
            var target = PathInfo.GetActivePaths(virtualModeAware: true)
                .Where(path => path.DisplaySource == source)
                .SelectMany(path => path.TargetsInfo)
                .FirstOrDefault();
            if (target?.IsSignalInformationAvailable == true &&
                Math.Abs(
                    (long)target.SignalInfo.VerticalSyncFrequencyInMillihertz -
                    physicalFrequency * 1000L) < 1000)
            {
                return;
            }
        }
    }

    private static int DynamicLowFrequency(
        uint maximum,
        IEnumerable<int> availableFrequencies)
    {
        var available = availableFrequencies.Distinct().ToArray();
        var half = checked((int)(maximum / 2));
        if (available.Contains(half))
            return half;
        if (available.Contains(MinimumDynamicRefreshRateHz))
            return MinimumDynamicRefreshRateHz;
        return available.Min();
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
                    var outputTechnology = Convert.ToUInt32(
                        item["VideoOutputTechnology"]);
                    if (outputTechnology != InternalOutputTechnology &&
                        outputTechnology != EmbeddedDisplayPortTechnology)
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
    private const uint EmbeddedDisplayPortTechnology = 11;
    private const int MinimumDynamicRefreshRateHz = 60;
    private const int DynamicPreInitializationAttempts = 20;
    private const int DynamicPreInitializationDelayMs = 50;

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
