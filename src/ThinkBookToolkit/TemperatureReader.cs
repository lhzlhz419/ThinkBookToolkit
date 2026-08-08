using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace ThinkBookToolkit;

public sealed class TemperatureReader : IDisposable
{
    private const double BytesPerGiB = 1024d * 1024d * 1024d;
    private readonly Computer _computer;
    private readonly NvidiaPrivateTelemetryReader? _nvidiaTelemetry;
    private readonly StorageTemperatureReader? _storageTelemetry;
    private TemperatureSnapshot? _lastSnapshot;

    public TemperatureReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true
        };
        _computer.Open();

        _nvidiaTelemetry = NvidiaPrivateTelemetryReader.TryCreate();
        _storageTelemetry = StorageTemperatureReader.TryCreate();
    }

    public TemperatureSnapshot Read()
    {
        try
        {
            var snapshot = ReadCore();
            _lastSnapshot = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning("Hardware monitoring refresh failed: " + ex.Message);
            return _lastSnapshot is { } previous
                ? previous with
                {
                    GpuTempC = null,
                    VramTempC = null,
                    GpuPowerW = null,
                    GpuSensor = "not found",
                    VramSensor = "not found",
                    GpuName = string.Empty,
                    GpuLoadPercent = null,
                    GpuMemoryLoadPercent = null,
                    GpuCoreClockMhz = null,
                    GpuMemoryClockMhz = null,
                    GpuHotSpotTempC = null,
                    VramChipTemperaturesC = []
                }
                : new TemperatureSnapshot(
                    null, null, null, null, null,
                    "not found", "not found", "not found");
        }
    }

    private TemperatureSnapshot ReadCore()
    {
        var sensors = new List<SensorReading>();
        var hardware = new List<HardwareReading>();
        foreach (var item in _computer.Hardware)
        {
            hardware.Add(new HardwareReading(
                item.Name,
                item.HardwareType,
                item.Identifier.ToString()));
            CollectHardware(
                item,
                item.Name,
                item.HardwareType,
                item.Identifier.ToString(),
                string.Empty,
                sensors);
        }

        var cpuHardware = hardware.FirstOrDefault(item =>
            item.HardwareType == HardwareType.Cpu);
        var gpuHardware = hardware
            .Where(item => IsGpu(item.HardwareType))
            .OrderBy(item => GpuPreference(item.HardwareType))
            .FirstOrDefault();
        var cpuSensors = SensorsFor(sensors, cpuHardware);
        var gpuSensors = SensorsFor(sensors, gpuHardware);

        var cpuTemperature = PickTemperature(
            cpuSensors,
            ["cpu package", "package", "core max", "cpu core", "tctl", "tdie"],
            fallbackToAny: true);
        var gpuTemperature = PickTemperature(
            gpuSensors,
            ["gpu core", "core"],
            fallbackToAny: true);
        var vramTemperature = PickTemperature(
            gpuSensors,
            ["gpu memory junction", "memory junction", "gpu memory", "vram"]);
        var lhmHotSpot = PickTemperature(
            gpuSensors,
            ["gpu hot spot", "hot spot", "hotspot"]);

        var gpuName = gpuHardware?.Name ?? string.Empty;
        var privateTelemetry = _nvidiaTelemetry?.Read(gpuName) ??
            NvidiaPrivateTelemetrySnapshot.Empty;
        var (physicalUsed, physicalTotal, virtualUsed, virtualTotal) =
            ReadMemoryUsage();

        var cpuClocks = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Clock)
            .Where(sensor => ContainsAny(sensor, ["core", "effective clock"]))
            .Where(sensor => !ContainsAny(sensor, ["bus", "uncore", "memory controller"]))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleClock)
            .ToArray();
        if (cpuClocks.Length == 0)
        {
            cpuClocks = cpuSensors
                .Where(sensor => sensor.SensorType == SensorType.Clock)
                .Where(sensor => !ContainsAny(sensor, ["bus", "uncore", "memory controller"]))
                .Select(sensor => sensor.Value)
                .Where(IsPlausibleClock)
                .ToArray();
        }

        var memoryTemperatures = sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .Where(sensor => !IsGpu(sensor.RootHardwareType))
            .Where(sensor =>
                sensor.RootHardwareType == HardwareType.Memory ||
                ContainsAny(sensor, ["dimm", "dram", "memory module", "ddr4", "ddr5"]))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleTemperature)
            .Take(12)
            .ToArray();

        // 硬盘温度与健康度由同一个 DiskInfoToolkit 设备对象、
        // 在同一次 RefreshVolatileData() 后读取，不再创建第二个 LHM Computer。
        var storage = ReadStorageSafely();

        var cpuPower = PickPower(cpuSensors, ["cpu package", "package"]);
        var gpuPower = PickPower(
            gpuSensors,
            ["gpu power", "gpu package", "package", "total board", "board power"]);
        var gpuMemoryLoad = PickValue(
            gpuSensors,
            SensorType.Load,
            ["gpu memory", "memory"]);
        gpuMemoryLoad ??= CalculateGpuMemoryLoad(gpuSensors);
        var cpuLoad = PickValue(
            cpuSensors,
            SensorType.Load,
            ["cpu total", "total"]) ??
            AverageValue(
                cpuSensors,
                SensorType.Load,
                ["cpu core", "core"]);
        var gpuLoad = PickValue(
            gpuSensors,
            SensorType.Load,
            ["gpu core", "core"]) ??
            gpuSensors
                .Where(sensor => sensor.SensorType == SensorType.Load)
                .Where(sensor => !ContainsAny(sensor, ["memory", "video engine", "bus"]))
                .Select(sensor => (double?)sensor.Value)
                .FirstOrDefault();

        return new TemperatureSnapshot(
            cpuTemperature.Sensor?.Value,
            gpuTemperature.Sensor?.Value,
            vramTemperature.Sensor?.Value,
            cpuPower,
            gpuPower,
            cpuTemperature.Name,
            gpuTemperature.Name,
            vramTemperature.Name)
        {
            CpuName = cpuHardware?.Name ?? string.Empty,
            CpuLoadPercent = cpuLoad,
            CpuAverageClockMhz = cpuClocks.Length > 0
                ? cpuClocks.Average()
                : null,
            CpuMaximumClockMhz = cpuClocks.Length > 0
                ? cpuClocks.Max()
                : null,
            GpuName = gpuName,
            GpuLoadPercent = gpuLoad,
            GpuMemoryLoadPercent = gpuMemoryLoad,
            GpuCoreClockMhz = PickValue(
                gpuSensors,
                SensorType.Clock,
                ["gpu core", "core"]),
            GpuMemoryClockMhz = PickValue(
                gpuSensors,
                SensorType.Clock,
                ["gpu memory", "memory"]),
            GpuHotSpotTempC = privateTelemetry.HotSpotTemperatureC ??
                lhmHotSpot.Sensor?.Value,
            VramChipTemperaturesC = privateTelemetry.MemoryChipTemperaturesC,
            PhysicalMemoryUsedGb = physicalUsed,
            PhysicalMemoryTotalGb = physicalTotal,
            VirtualMemoryUsedGb = virtualUsed,
            VirtualMemoryTotalGb = virtualTotal,
            MemorySlotTemperaturesC = memoryTemperatures,
            StorageDevices = storage
        };
    }

    public void Dispose()
    {
        try
        {
            _computer.Close();
        }
        catch
        {
        }
    }

    private IReadOnlyList<StorageTemperatureSnapshot> ReadStorageSafely()
    {
        try
        {
            return _storageTelemetry?.Read() ?? [];
        }
        catch
        {
            // 硬盘读取失败不能影响 CPU、GPU、内存和风扇数据。
            return [];
        }
    }

    private static void CollectHardware(
        IHardware hardware,
        string rootName,
        HardwareType rootType,
        string rootIdentifier,
        string path,
        List<SensorReading> sensors)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            return;
        }

        var hardwarePath = string.IsNullOrWhiteSpace(path)
            ? hardware.Name
            : path + "/" + hardware.Name;
        ISensor[] hardwareSensors;
        IHardware[] subHardwareItems;
        try
        {
            hardwareSensors = hardware.Sensors;
            subHardwareItems = hardware.SubHardware;
        }
        catch
        {
            return;
        }
        foreach (var sensor in hardwareSensors)
        {
            if (sensor.Value is null)
                continue;
            sensors.Add(new SensorReading(
                sensor.Name,
                hardware.Name,
                hardware.HardwareType,
                rootName,
                rootType,
                rootIdentifier,
                hardwarePath + "/" + sensor.Name,
                sensor.SensorType,
                sensor.Value.Value));
        }

        foreach (var subHardware in subHardwareItems)
        {
            CollectHardware(
                subHardware,
                rootName,
                rootType,
                rootIdentifier,
                hardwarePath,
                sensors);
        }
    }

    private static IReadOnlyList<SensorReading> SensorsFor(
        IReadOnlyList<SensorReading> sensors,
        HardwareReading? hardware) =>
        hardware is null
            ? []
            : sensors
                .Where(sensor => string.Equals(
                    sensor.RootIdentifier,
                    hardware.Identifier,
                    StringComparison.Ordinal))
                .ToArray();

    private static (SensorReading? Sensor, string Name) PickTemperature(
        IEnumerable<SensorReading> sensors,
        string[] patterns,
        bool fallbackToAny = false)
    {
        var temperatureSensors = sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .Where(sensor => IsPlausibleTemperature(sensor.Value))
            .ToList();
        var selected = temperatureSensors
            .Where(sensor => ContainsAny(sensor, patterns))
            .OrderByDescending(sensor => sensor.Value)
            .FirstOrDefault();
        if (selected is null && fallbackToAny)
        {
            selected = temperatureSensors
                .OrderByDescending(sensor => sensor.Value)
                .FirstOrDefault();
        }
        return selected is null
            ? (null, "not found")
            : (selected, selected.Identifier);
    }

    private static double? PickPower(
        IEnumerable<SensorReading> sensors,
        string[] patterns)
    {
        var powerSensors = sensors
            .Where(sensor => sensor.SensorType == SensorType.Power)
            .ToList();
        return powerSensors
            .Where(sensor => ContainsAny(sensor, patterns))
            .OrderByDescending(sensor => sensor.Value)
            .FirstOrDefault()?.Value ??
            powerSensors.OrderByDescending(sensor => sensor.Value).FirstOrDefault()?.Value;
    }

    private static double? PickValue(
        IEnumerable<SensorReading> sensors,
        SensorType sensorType,
        string[] patterns) =>
        sensors
            .Where(sensor => sensor.SensorType == sensorType)
            .Where(sensor => ContainsAny(sensor, patterns))
            .OrderBy(pattern => PatternIndex(pattern, patterns))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault();

    private static double? AverageValue(
        IEnumerable<SensorReading> sensors,
        SensorType sensorType,
        string[] patterns)
    {
        var values = sensors
            .Where(sensor => sensor.SensorType == sensorType)
            .Where(sensor => ContainsAny(sensor, patterns))
            .Select(sensor => sensor.Value)
            .ToArray();
        return values.Length > 0 ? values.Average() : null;
    }

    private static double? CalculateGpuMemoryLoad(
        IReadOnlyList<SensorReading> sensors)
    {
        var used = sensors.FirstOrDefault(sensor =>
            sensor.SensorType is SensorType.Data or SensorType.SmallData &&
            ContainsAny(sensor, ["memory used", "dedicated memory used"]));
        var total = sensors.FirstOrDefault(sensor =>
            sensor.SensorType is SensorType.Data or SensorType.SmallData &&
            ContainsAny(sensor, ["memory total", "dedicated memory total"]));
        if (used is null || total is null || total.Value <= 0)
            return null;
        return Math.Clamp(used.Value * 100 / total.Value, 0, 100);
    }

    private static int PatternIndex(SensorReading sensor, string[] patterns)
    {
        for (var index = 0; index < patterns.Length; index++)
        {
            if (ContainsAny(sensor, [patterns[index]]))
                return index;
        }
        return patterns.Length;
    }

    private static bool ContainsAny(SensorReading sensor, string[] patterns)
    {
        var text = sensor.Name + " " + sensor.HardwareName + " " + sensor.Identifier;
        return patterns.Any(pattern =>
            text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static int GpuPreference(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 0,
        HardwareType.GpuAmd => 1,
        _ => 2
    };

    private static bool IsPlausibleClock(double value) =>
        value is > 0 and < 20000;

    private static bool IsPlausibleTemperature(double value) =>
        value is > -40 and < 160;

    private static (double? PhysicalUsed, double? PhysicalTotal,
        double? VirtualUsed, double? VirtualTotal) ReadMemoryUsage()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref status))
            return (null, null, null, null);
        return (
            (status.TotalPhysical - status.AvailablePhysical) / BytesPerGiB,
            status.TotalPhysical / BytesPerGiB,
            (status.TotalPageFile - status.AvailablePageFile) / BytesPerGiB,
            status.TotalPageFile / BytesPerGiB);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record HardwareReading(
        string Name,
        HardwareType HardwareType,
        string Identifier);

    private sealed record SensorReading(
        string Name,
        string HardwareName,
        HardwareType HardwareType,
        string RootHardwareName,
        HardwareType RootHardwareType,
        string RootIdentifier,
        string Identifier,
        SensorType SensorType,
        double Value);
}
