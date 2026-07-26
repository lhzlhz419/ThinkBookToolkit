using System;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

internal sealed record BatteryInformationSnapshot(
    double? TemperatureC,
    double ChargeDischargePowerW,
    double MinimumPowerW,
    double MaximumPowerW,
    double CurrentCapacityWh,
    double FullChargeCapacityWh,
    double DesignCapacityWh,
    double HealthPercent,
    DateTime? OnBatterySince,
    uint CycleCount,
    DateTime? ManufactureDate,
    DateTime? FirstUseDate,
    bool IsAcConnected);

internal static class BatteryInformationReader
{
    private const uint IoctlEnergyBatteryInformation = 0x83102138;
    private static readonly object RateLock = new();
    private static int _minimumRate = int.MaxValue;
    private static int _maximumRate;
    private static DateTime _lastOnBatteryLookup;
    private static DateTime? _cachedOnBatterySince;

    public static BatteryInformationSnapshot Read()
    {
        using var battery = new BatteryDevice();
        var tag = battery.QueryTag();
        var information = battery.QueryInformation(tag);
        var status = battery.QueryStatus(tag);
        var powerStatus = Native.GetSystemPowerStatus(out var systemPowerStatus)
            ? systemPowerStatus
            : throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetSystemPowerStatus failed.");

        UpdateRateExtremes(status.Rate);
        int minimumRate;
        int maximumRate;
        lock (RateLock)
        {
            minimumRate = status.Rate == 0 ? 0 : _minimumRate;
            maximumRate = _maximumRate;
        }

        var (temperature, manufactureDate, firstUseDate) =
            ReadLenovoBatteryInformation();
        var isAcConnected = powerStatus.ACLineStatus == 1;
        var onBatterySince = isAcConnected ? null : GetOnBatterySince();
        var designCapacity = information.DesignedCapacity;
        var fullChargeCapacity = information.FullChargedCapacity;
        var health = designCapacity == 0
            ? 0
            : (double)fullChargeCapacity / designCapacity * 100.0;

        return new(
            temperature,
            status.Rate / 1000.0,
            minimumRate / 1000.0,
            maximumRate / 1000.0,
            status.Capacity / 1000.0,
            fullChargeCapacity / 1000.0,
            designCapacity / 1000.0,
            health,
            onBatterySince,
            information.CycleCount,
            manufactureDate,
            firstUseDate,
            isAcConnected);
    }

    private static void UpdateRateExtremes(int rate)
    {
        lock (RateLock)
        {
            if (rate == 0 ||
                (rate > 0 && (_minimumRate < 0 || _maximumRate < 0)) ||
                (rate < 0 && (_minimumRate > 0 || _maximumRate > 0)))
            {
                _minimumRate = int.MaxValue;
                _maximumRate = 0;
            }

            if (rate == 0)
                return;

            if (Math.Abs(rate) < Math.Abs(_minimumRate))
                _minimumRate = rate;
            if (Math.Abs(rate) > Math.Abs(_maximumRate))
                _maximumRate = rate;
        }
    }

    private static (
        double? Temperature,
        DateTime? ManufactureDate,
        DateTime? FirstUseDate) ReadLenovoBatteryInformation()
    {
        try
        {
            using var driver = new LenovoEnergyDriver();
            for (uint index = 0; index < 3; index++)
            {
                var information = driver.Call<LenovoBatteryInformation>(
                    IoctlEnergyBatteryInformation,
                    index);
                if (information.Temperature is ushort.MinValue or ushort.MaxValue)
                    continue;

                return (
                    DecodeTemperature(information.Temperature),
                    DecodeDate(information.ManufactureDate),
                    DecodeDate(information.FirstUseDate));
            }
        }
        catch
        {
        }

        return (null, null, null);
    }

    private static DateTime? GetOnBatterySince()
    {
        if (DateTime.Now - _lastOnBatteryLookup < TimeSpan.FromSeconds(30))
            return _cachedOnBatterySince;

        _lastOnBatteryLookup = DateTime.Now;
        try
        {
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                "*[System[EventID=105]]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var selector = new EventLogPropertySelector(
                ["Event/EventData/Data[@Name='AcOnline']"]);
            while (reader.ReadEvent() is EventLogRecord record)
            {
                var values = record.GetPropertyValues(selector);
                if (values.Count == 0 || values[0] is not bool isAcOnline)
                    continue;

                if (!isAcOnline && record.TimeCreated.HasValue)
                {
                    _cachedOnBatterySince = record.TimeCreated.Value;
                    return _cachedOnBatterySince;
                }

                if (isAcOnline)
                    break;
            }
        }
        catch
        {
        }

        _cachedOnBatterySince = null;
        return null;
    }

    private static double? DecodeTemperature(ushort raw)
    {
        var value = (raw - 2731.6) / 10.0;
        return value < 0 ? null : value;
    }

    private static DateTime? DecodeDate(ushort raw)
    {
        try
        {
            if (raw == 0)
                return null;

            var date = new DateTime(
                (raw >> 9) + 1980,
                (raw >> 5) & 0x0F,
                raw & 0x1F);
            return date.Year is >= 2018 and <= 2030 ? date : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LenovoBatteryInformation
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 13)]
        public byte[] Prefix;
        public ushort Temperature;
        public ushort ManufactureDate;
        public ushort FirstUseDate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] Suffix;
    }

    private sealed class BatteryDevice : IDisposable
    {
        private const uint IoctlQueryTag = 0x294040;
        private const uint IoctlQueryInformation = 0x294044;
        private const uint IoctlQueryStatus = 0x29404C;
        private static readonly Guid BatteryInterfaceGuid =
            new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
        private readonly SafeFileHandle _handle;

        public BatteryDevice()
        {
            _handle = OpenBattery();
        }

        public uint QueryTag() =>
            Call<uint, uint>(IoctlQueryTag, 0);

        public BatteryInformation QueryInformation(uint tag) =>
            Call<BatteryQueryInformation, BatteryInformation>(
                IoctlQueryInformation,
                new BatteryQueryInformation
                {
                    BatteryTag = tag,
                    InformationLevel = 0,
                    AtRate = 0
                });

        public BatteryStatus QueryStatus(uint tag) =>
            Call<BatteryWaitStatus, BatteryStatus>(
                IoctlQueryStatus,
                new BatteryWaitStatus
                {
                    BatteryTag = tag
                });

        public void Dispose() => _handle.Dispose();

        private TOutput Call<TInput, TOutput>(uint ioctl, TInput input)
            where TInput : struct
            where TOutput : struct
        {
            var inputSize = Marshal.SizeOf<TInput>();
            var outputSize = Marshal.SizeOf<TOutput>();
            var inputPointer = Marshal.AllocHGlobal(inputSize);
            var outputPointer = Marshal.AllocHGlobal(outputSize);
            try
            {
                Marshal.StructureToPtr(input, inputPointer, false);
                for (var offset = 0; offset < outputSize; offset++)
                    Marshal.WriteByte(outputPointer, offset, 0);

                if (!Native.DeviceIoControl(
                        _handle,
                        ioctl,
                        inputPointer,
                        inputSize,
                        outputPointer,
                        outputSize,
                        out _,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Battery DeviceIoControl 0x{ioctl:X8} failed.");
                }

                return Marshal.PtrToStructure<TOutput>(outputPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(outputPointer);
                Marshal.FreeHGlobal(inputPointer);
            }
        }

        private static SafeFileHandle OpenBattery()
        {
            var guid = BatteryInterfaceGuid;
            var deviceInfoSet = Native.SetupDiGetClassDevs(
                ref guid,
                null,
                IntPtr.Zero,
                Native.DigcfPresent | Native.DigcfDeviceInterface);
            if (deviceInfoSet == Native.InvalidHandleValue)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetupDiGetClassDevs for the battery failed.");
            }

            try
            {
                var interfaceData = new Native.SpDeviceInterfaceData
                {
                    Size = Marshal.SizeOf<Native.SpDeviceInterfaceData>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref guid,
                        0,
                        ref interfaceData))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "No battery device interface was found.");
                }

                _ = Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredSize,
                    IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            detail,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "SetupDiGetDeviceInterfaceDetail failed.");
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ??
                               throw new InvalidOperationException(
                                   "The battery device path was empty.");
                    var handle = Native.CreateFile(
                        path,
                        Native.GenericRead | Native.GenericWrite,
                        Native.FileShareRead | Native.FileShareWrite,
                        IntPtr.Zero,
                        Native.OpenExisting,
                        Native.FileAttributeNormal,
                        IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Opening the battery device failed.");
                    }

                    return handle;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    private static class Native
    {
        public static readonly IntPtr InvalidHandleValue = new(-1);
        public const uint DigcfPresent = 0x00000002;
        public const uint DigcfDeviceInterface = 0x00000010;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            IntPtr outputBuffer,
            int outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(
            out SystemPowerStatus systemPowerStatus);
    }
}
