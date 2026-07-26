using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace ThinkBookToolkit;

internal sealed record DeviceIdentity(
    string Model,
    string ProductNumber,
    string DeviceCode,
    string SerialNumber,
    string BiosVersion,
    string SmbiosVersion);

internal sealed record CpuInfo(string Name, int Cores, int Threads);
internal sealed record GpuInfo(string Name, ulong? DedicatedMemoryBytes, bool UsesSharedMemory);
internal sealed record MemoryInfo(string Locator, ulong Capacity, uint Speed, string Type, string Manufacturer, string PartNumber);
internal sealed record MotherboardInfo(string Manufacturer, string Product, string Version, string SerialNumber);
internal sealed record DisplayInfo(string Name, uint Width, uint Height, uint RefreshRate);
internal sealed record PartitionInfo(string Name, string VolumeLabel, ulong UsedBytes, ulong TotalBytes, string DiskModel);

internal sealed record DeviceInformationSnapshot(
    DeviceIdentity Identity,
    FirmwareInformation Firmware,
    string WindowsVersion,
    string DeviceName,
    string DeviceId,
    string WindowsProductId,
    ulong InstalledMemoryBytes,
    ulong UsableMemoryBytes,
    CpuInfo? Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<PartitionInfo> Partitions,
    IReadOnlyList<MemoryInfo> Memory,
    MotherboardInfo? Motherboard,
    IReadOnlyList<DisplayInfo> Displays);

internal static class DeviceInformationService
{
    public static DeviceIdentity ReadIdentity()
    {
        var smbios = RawSmbios.Read();
        var model = QueryFirst("Win32_ComputerSystemProduct", "Version");
        if (IsEmptyFirmwareValue(model))
            model = QueryFirst("Win32_ComputerSystem", "Model");

        var productNumber = smbios.SystemSku;
        if (string.IsNullOrWhiteSpace(productNumber))
            productNumber = QueryFirst("Win32_ComputerSystemProduct", "Name");

        var serial = smbios.SystemSerial;
        if (string.IsNullOrWhiteSpace(serial))
            serial = QueryFirst("Win32_BIOS", "SerialNumber");

        var bios = smbios.BiosVersion;
        if (string.IsNullOrWhiteSpace(bios))
            bios = QueryFirst("Win32_BIOS", "SMBIOSBIOSVersion");

        return new(
            Clean(model),
            Clean(productNumber),
            ProductCode(productNumber),
            Clean(serial),
            Clean(bios),
            Clean(smbios.SmbiosVersion));
    }

    public static DeviceInformationSnapshot ReadAll()
    {
        var identity = ReadIdentity();
        var firmware = BiosAdvancedController.ReadFirmwareInformation();
        var memory = Safe(ReadMemory, Array.Empty<MemoryInfo>());
        var installed = memory.Aggregate<MemoryInfo, ulong>(0, (total, item) => total + item.Capacity);
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        GlobalMemoryStatusEx(ref status);

        return new(
            identity,
            firmware,
            ReadWindowsVersion(),
            Environment.MachineName,
            ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Microsoft\SQMClient", "MachineId").Trim('{', '}'),
            ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId"),
            installed,
            status.TotalPhysical,
            Safe<CpuInfo?>(ReadCpu, null),
            Safe(ReadGpus, Array.Empty<GpuInfo>()),
            Safe(ReadPartitions, Array.Empty<PartitionInfo>()),
            memory,
            Safe<MotherboardInfo?>(ReadMotherboard, null),
            Safe(ReadDisplays, Array.Empty<DisplayInfo>()));
    }

    private static CpuInfo? ReadCpu()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
            return new(Clean(item["Name"]), ToInt(item["NumberOfCores"]), ToInt(item["NumberOfLogicalProcessors"]));
        return null;
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var exactMemory = ReadDriverVideoMemory();
        var result = new List<GpuInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var name = Clean(item["Name"]);
            var shared = name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase);
            ulong? bytes = shared
                ? null
                : exactMemory.TryGetValue(name, out var exact)
                ? exact
                : ToNullableUlong(item["AdapterRAM"]);
            result.Add(new(name, bytes, shared));
        }
        return result;
    }

    private static Dictionary<string, ulong> ReadDriverVideoMemory()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
        if (video is null)
            return result;
        foreach (var adapter in video.GetSubKeyNames())
        foreach (var index in new[] { "0000", "0001", "0002" })
        {
            using var key = video.OpenSubKey(adapter + "\\" + index);
            var name = key?.GetValue("DriverDesc") as string;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var value = key!.GetValue("HardwareInformation.qwMemorySize");
            if (value is long signed && signed > 0)
                result[name] = (ulong)signed;
            else if (value is byte[] data && data.Length >= 8)
                result[name] = BitConverter.ToUInt64(data, 0);
        }
        return result;
    }

    private static IReadOnlyList<MemoryInfo> ReadMemory()
    {
        var result = new List<MemoryInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var speed = ToUInt(item["ConfiguredClockSpeed"]);
            if (speed == 0) speed = ToUInt(item["Speed"]);
            result.Add(new(
                Clean(item["DeviceLocator"]),
                ToUlong(item["Capacity"]),
                speed,
                MemoryType(ToInt(item["SMBIOSMemoryType"])),
                Clean(item["Manufacturer"]),
                Clean(item["PartNumber"])));
        }
        return result;
    }

    private static MotherboardInfo? ReadMotherboard()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
            return new(Clean(item["Manufacturer"]), Clean(item["Product"]), Clean(item["Version"]), Clean(item["SerialNumber"]));
        return null;
    }

    private static IReadOnlyList<PartitionInfo> ReadPartitions()
    {
        var result = new List<PartitionInfo>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var disk = "";
            try
            {
                var logicalId = drive.Name.TrimEnd('\\').Replace("'", "''");
                using var partitions = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logicalId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition").Get();
                foreach (ManagementObject partition in partitions)
                {
                    var partitionId = Clean(partition["DeviceID"]).Replace("'", "''");
                    using var disks = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition").Get();
                    foreach (ManagementObject physical in disks)
                    {
                        disk = Clean(physical["Model"]);
                        break;
                    }
                    if (!string.IsNullOrWhiteSpace(disk)) break;
                }
            }
            catch { }
            result.Add(new(
                drive.Name.TrimEnd('\\'),
                drive.VolumeLabel,
                (ulong)(drive.TotalSize - drive.AvailableFreeSpace),
                (ulong)drive.TotalSize,
                disk));
        }
        return result;
    }

    private static IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        var friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, UserFriendlyName, Active FROM WmiMonitorID");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                if (item["Active"] is bool active && !active) continue;
                var instance = Clean(item["InstanceName"]);
                var parts = instance.Split('\\');
                var id = parts.Length > 1 ? parts[1] : instance;
                var name = DecodeUshortString(item["UserFriendlyName"]);
                if (!string.IsNullOrWhiteSpace(name)) friendlyNames[id] = name;
            }
        }
        catch { }

        var output = new List<DisplayInfo>();
        var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & DisplayDeviceActive) == 0) { adapter.cb = Marshal.SizeOf<DisplayDevice>(); continue; }
            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 0)) break;
                if ((monitor.StateFlags & DisplayDeviceActive) == 0) continue;
                var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
                if (EnumDisplaySettings(adapter.DeviceName, CurrentSettings, ref mode))
                {
                    var id = monitor.DeviceID.Split('\\').ElementAtOrDefault(1) ?? "";
                    var name = friendlyNames.TryGetValue(id, out var friendly) ? friendly : monitor.DeviceString;
                    var refreshRate = mode.dmDisplayFrequency <= 1 ? 60u : mode.dmDisplayFrequency;
                    if (!output.Any(d => d.Name == name && d.Width == mode.dmPelsWidth && d.Height == mode.dmPelsHeight && d.RefreshRate == refreshRate))
                        output.Add(new(Clean(name), mode.dmPelsWidth, mode.dmPelsHeight, refreshRate));
                }
            }
            adapter.cb = Marshal.SizeOf<DisplayDevice>();
        }
        return output;
    }

    private static string QueryFirst(string className, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results) return Clean(item[property]);
        }
        catch { }
        return "";
    }

    private static string ReadWindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = Clean(key?.GetValue("ProductName"));
            var displayVersion = Clean(key?.GetValue("DisplayVersion"));
            var buildText = Clean(key?.GetValue("CurrentBuildNumber"));
            if (int.TryParse(buildText, out var build) && build >= 22000)
                productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            return JoinNonEmpty(productName, displayVersion);
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static string ProductCode(string productNumber)
    {
        var normalized = Clean(productNumber);
        return normalized.Length >= 4 ? normalized[..4] : normalized;
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string ReadRegistryString(RegistryKey root, string path, string name)
    {
        try { using var key = root.OpenSubKey(path); return Clean(key?.GetValue(name)); }
        catch { return ""; }
    }

    private static string DecodeUshortString(object? value)
    {
        if (value is ushort[] words) return new string(words.TakeWhile(x => x != 0).Select(x => (char)x).ToArray());
        if (value is byte[] bytes) return Encoding.ASCII.GetString(bytes.TakeWhile(x => x != 0).ToArray());
        return "";
    }

    private static string MemoryType(int value) => value switch { 20 or 21 or 22 => "DDR", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", _ => "RAM" };
    private static bool IsEmptyFirmwareValue(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("INVALID", StringComparison.OrdinalIgnoreCase) || value.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase);
    private static string Clean(object? value) => value?.ToString()?.Trim().TrimEnd('\0') ?? "";
    private static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);
    private static uint ToUInt(object? value) => value is null ? 0 : Convert.ToUInt32(value);
    private static ulong ToUlong(object? value) => value is null ? 0 : Convert.ToUInt64(value);
    private static ulong? ToNullableUlong(object? value) => value is null ? null : Convert.ToUInt64(value);

    private const int CurrentSettings = -1;
    private const int DisplayDeviceActive = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint number, ref DisplayDevice displayDevice, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode devMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short SpecificationVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel; public uint dmPelsWidth, dmPelsHeight; public int dmDisplayFlags; public uint dmDisplayFrequency; public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx { public uint Length; public uint MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }

    private sealed record RawSmbios(string BiosVersion, string SmbiosVersion, string SystemSerial, string SystemSku)
    {
        public static RawSmbios Read()
        {
            try
            {
                var size = GetSystemFirmwareTable(Rsmb, 0, IntPtr.Zero, 0);
                if (size == 0) return new("", "", "", "");
                var ptr = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetSystemFirmwareTable(Rsmb, 0, ptr, size) != size) return new("", "", "", "");
                    var data = new byte[size]; Marshal.Copy(ptr, data, 0, data.Length);
                    var smbiosVersion = data.Length >= 3 ? $"{data[1]}.{data[2]}" : "";
                    var index = 8; string bios = "", serial = "", sku = "";
                    while (index + 4 <= data.Length)
                    {
                        var type = data[index]; var length = data[index + 1];
                        if (length < 4 || index + length > data.Length) break;
                        var stringsStart = index + length; var end = stringsStart;
                        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end++;
                        string Str(int offset) => offset < length ? GetSmbiosString(data, stringsStart, end, data[index + offset]) : "";
                        if (type == 0)
                        {
                            bios = Str(5);
                        }
                        else if (type == 1)
                        {
                            serial = Str(7);
                            sku = Str(25);
                        }
                        else if (type == 200)
                        {
                            // Lenovo IdeaPad/ThinkBook firmware stores the full
                            // machine type model (for example 21R0A003CD) here.
                            var lenovoProductNumber = Str(5);
                            if (!string.IsNullOrWhiteSpace(lenovoProductNumber))
                                sku = lenovoProductNumber;
                        }
                        if (type == 127) break;
                        index = Math.Min(data.Length, end + 2);
                    }
                    return new(bios, smbiosVersion, serial, sku);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { return new("", "", "", ""); }
        }

        private static string GetSmbiosString(byte[] data, int start, int end, int number)
        {
            if (number <= 0) return "";
            var current = 1; var pos = start;
            while (pos < end)
            {
                var stop = Array.IndexOf(data, (byte)0, pos, end - pos);
                if (stop < 0) stop = end;
                if (current == number) return Encoding.ASCII.GetString(data, pos, stop - pos).Trim();
                current++; pos = stop + 1;
            }
            return "";
        }

        private const uint Rsmb = 0x52534D42;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, IntPtr buffer, uint size);
    }
}
