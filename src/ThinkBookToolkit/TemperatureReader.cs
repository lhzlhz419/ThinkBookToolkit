using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace ThinkBookToolkit;

public sealed class TemperatureReader : IDisposable
{
    private const double BytesPerGiB = 1024d * 1024d * 1024d;
    private readonly Computer _computer;
    private readonly GpuMonitorWorkerClient? _gpuMonitor;
    private readonly object _gpuReadSync = new();
    private readonly HybridCoreTopology _hybridCoreTopology =
        HybridCoreTopology.Detect();
    private Task<GpuMonitorWorkerSnapshot?>? _gpuReadTask;
    private GpuMonitorWorkerSnapshot? _latestGpuSnapshot;
    private StorageTemperatureReader? _storageTelemetry;
    private Task<StorageTemperatureReader?>? _storageInitialization;
    private TemperatureSnapshot? _lastSnapshot;
    private readonly object _sampleLock = new();
    private long _lastSuccessfulSample;
    private readonly Dictionary<(string Name, string Identifier), int> _coreClockClasses = new();

    internal static IReadOnlyDictionary<int, byte>
        HybridCoreEfficiencyClassesForTesting() =>
        HybridCoreTopology.Detect().EfficiencyClasses;

    public TemperatureReader(bool enableGpuTelemetry = true)
    {
        var stopwatch = Stopwatch.StartNew();
        _gpuMonitor = enableGpuTelemetry
            ? new GpuMonitorWorkerClient()
            : null;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            // GPU vendors expose native telemetry APIs that can terminate the
            // CLR while a discrete adapter is being removed or reconnected.
            // GPU monitoring therefore runs in ThinkBookToolkit.Guardian.
            IsGpuEnabled = false,
            IsMemoryEnabled = true
        };
        _computer.Open();
        ToolkitLog.Info(
            "LibreHardwareMonitor CPU/memory reader opened in " +
            $"{stopwatch.Elapsed.TotalMilliseconds:0} ms.");
        if (_hybridCoreTopology.IsHybrid)
        {
            ToolkitLog.Info(
                "Hybrid CPU core classes (LHM physical-core order): " +
                string.Join(
                    ", ",
                    _hybridCoreTopology.EfficiencyClasses
                        .OrderBy(pair => pair.Key)
                        .Select(pair =>
                            $"{pair.Key}=" +
                            (pair.Value ==
                             _hybridCoreTopology.PerformanceClass
                                ? "P"
                                : "E"))) + ".");
        }
        if (_gpuMonitor is not null)
            _gpuReadTask = Task.Run(_gpuMonitor.Read);

    }

    public TemperatureSnapshot Read()
    {
        lock (_sampleLock)
        {
            if (_lastSnapshot is not null && _lastSuccessfulSample != 0 &&
                Stopwatch.GetElapsedTime(_lastSuccessfulSample) < TimeSpan.FromMilliseconds(200))
                return _lastSnapshot;
            return ReadUncached();
        }
    }

    private TemperatureSnapshot ReadUncached()
    {
        try
        {
            var snapshot = ReadCore();
            _lastSnapshot = snapshot;
            _lastSuccessfulSample = Stopwatch.GetTimestamp();
            return snapshot;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Hardware monitoring refresh failed.", ex);
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

    internal GpuWorkerCommandResponse QueryDiscreteGpuApplications() =>
        _gpuMonitor?.QueryApplications() ??
        GpuWorkerCommandResponse.Failure(
            "GPU monitoring is unavailable.");

    internal GpuWorkerCommandResponse KillDiscreteGpuApplications() =>
        _gpuMonitor?.KillApplications() ??
        GpuWorkerCommandResponse.Failure(
            "GPU monitoring is unavailable.");

    internal GpuWorkerCommandResponse ApplyGpuOverclock(
        GpuOverclockSettings settings,
        bool force = false) =>
        _gpuMonitor?.ApplyOverclock(settings, force) ??
        GpuWorkerCommandResponse.Failure(
            "GPU monitoring is unavailable.");

    internal GpuWorkerCommandResponse ResetGpuOverclock() =>
        _gpuMonitor?.ResetOverclock() ??
        GpuWorkerCommandResponse.Failure(
            "GPU monitoring is unavailable.");

    private TemperatureSnapshot ReadCore()
    {
        var isolatedGpu = ReadGpuSnapshotWithoutBlocking();
        var retainedGpu = isolatedGpu is null &&
                          GpuTelemetryControl.Mode == GpuTelemetryMode.Paused
            ? _lastSnapshot
            : null;
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
        var cpuSensors = SensorsFor(sensors, cpuHardware);

        var cpuTemperature = PickTemperature(
            cpuSensors,
            ["cpu package", "package", "core max", "cpu core", "tctl", "tdie"],
            fallbackToAny: true);
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
        var performanceCoreClocks = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Clock)
            .Where(sensor => IsHybridCoreClock(
                sensor,
                performance: true))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleClock)
            .ToArray();
        var efficiencyCoreClocks = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Clock)
            .Where(sensor => IsHybridCoreClock(
                sensor,
                performance: false))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleClock)
            .ToArray();

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
        var cpuLoad = PickValue(
            cpuSensors,
            SensorType.Load,
            ["cpu total", "total"]) ??
            AverageValue(
                cpuSensors,
                SensorType.Load,
                ["cpu core", "core"]);
        return new TemperatureSnapshot(
            cpuTemperature.Sensor?.Value,
            isolatedGpu?.CoreTemperatureC,
            isolatedGpu?.MemoryTemperatureC,
            cpuPower,
            isolatedGpu?.PowerW,
            cpuTemperature.Name,
            isolatedGpu?.CoreTemperatureSensor ?? "not found",
            isolatedGpu?.MemoryTemperatureSensor ?? "not found")
        {
            CpuName = cpuHardware?.Name ?? string.Empty,
            CpuLoadPercent = cpuLoad,
            CpuAverageClockMhz = cpuClocks.Length > 0
                ? cpuClocks.Average()
                : null,
            CpuPerformanceCoreAverageClockMhz =
                performanceCoreClocks.Length > 0
                    ? performanceCoreClocks.Average()
                    : null,
            CpuEfficiencyCoreAverageClockMhz =
                efficiencyCoreClocks.Length > 0
                    ? efficiencyCoreClocks.Average()
                    : null,
            CpuMaximumClockMhz = cpuClocks.Length > 0
                ? cpuClocks.Max()
                : null,
            GpuName = isolatedGpu?.Name ?? retainedGpu?.GpuName ?? string.Empty,
            GpuLoadPercent = isolatedGpu?.LoadPercent,
            GpuMemoryLoadPercent = isolatedGpu?.MemoryLoadPercent,
            GpuCoreClockMhz = isolatedGpu?.CoreClockMhz,
            GpuMemoryClockMhz = isolatedGpu?.MemoryClockMhz,
            GpuHotSpotTempC = isolatedGpu?.HotSpotTemperatureC,
            VramChipTemperaturesC = isolatedGpu?.MemoryChipTemperaturesC ?? [],
            DiscreteGpuState = isolatedGpu?.DiscreteGpuState ??
                retainedGpu?.DiscreteGpuState ??
                DiscreteGpuActivityState.Unknown,
            GpuPerformanceState = isolatedGpu?.PerformanceState ??
                retainedGpu?.GpuPerformanceState ??
                string.Empty,
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
        _gpuMonitor?.Dispose();
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
            if (_storageTelemetry is null)
            {
                _storageInitialization ??= Task.Run(
                    StorageTemperatureReader.TryCreate);
                if (!_storageInitialization.IsCompleted)
                    return [];
                _storageTelemetry = _storageInitialization
                    .GetAwaiter()
                    .GetResult();
            }
            return _storageTelemetry?.Read() ?? [];
        }
        catch
        {
            // 硬盘读取失败不能影响 CPU、GPU、内存和风扇数据。
            return [];
        }
    }

    private GpuMonitorWorkerSnapshot? ReadGpuSnapshotWithoutBlocking()
    {
        if (_gpuMonitor is null ||
            GpuTelemetryControl.Mode == GpuTelemetryMode.Paused)
        {
            return null;
        }

        lock (_gpuReadSync)
        {
            if (_gpuReadTask is null)
            {
                _gpuReadTask = Task.Run(_gpuMonitor.Read);
                return _latestGpuSnapshot;
            }
            if (!_gpuReadTask.IsCompleted)
                return _latestGpuSnapshot;
            try
            {
                _latestGpuSnapshot = _gpuReadTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "Background GPU telemetry refresh failed: " + ex.Message);
            }
            _gpuReadTask = Task.Run(_gpuMonitor.Read);
            return _latestGpuSnapshot;
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
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"Hardware update failed for {rootName}: {ex.Message}");
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

    private bool IsHybridCoreClock(
        SensorReading sensor,
        bool performance)
    {
        var key = (sensor.Name, sensor.Identifier);
        if (!_coreClockClasses.TryGetValue(key, out var classification))
        {
            classification = ClassifyCoreClock(sensor);
            _coreClockClasses[key] = classification;
        }
        return classification == (performance ? 1 : 2);
    }

    private int ClassifyCoreClock(SensorReading sensor)
    {
        var explicitlyPerformance = ContainsAny(
            sensor,
            ["p-core", "p core", "performance core"]);
        var explicitlyEfficient = ContainsAny(
            sensor,
            ["e-core", "e core", "efficiency core", "efficient core"]);
        // A labelled core must never fall through and be accepted by the
        // opposite group because of a firmware/core-index mismatch.
        if (explicitlyPerformance || explicitlyEfficient)
            return explicitlyPerformance ? 1 : 2;
        if (!_hybridCoreTopology.IsHybrid ||
            !TryParseCpuCoreIndex(sensor.Name, sensor.Identifier, out var index) ||
            !_hybridCoreTopology.EfficiencyClasses.TryGetValue(index, out var value))
            return 0;
        return value == _hybridCoreTopology.PerformanceClass ? 1 : 2;
    }

    internal static bool TryParseCpuCoreIndex(
        string? name,
        string? identifier,
        out int index)
    {
        var match = Regex.Match(
            name ?? string.Empty,
            @"(?:core|核心)\s*#?\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var displayed) &&
            displayed > 0)
        {
            index = displayed - 1;
            return true;
        }
        var tail = (identifier ?? string.Empty)
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (int.TryParse(tail, out displayed) && displayed > 0)
        {
            index = displayed - 1;
            return true;
        }
        index = -1;
        return false;
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

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

    private sealed record HybridCoreTopology(
        IReadOnlyDictionary<int, byte> EfficiencyClasses,
        byte PerformanceClass)
    {
        public bool IsHybrid =>
            EfficiencyClasses.Count > 0 &&
            EfficiencyClasses.Values.Any(value =>
                value != PerformanceClass);

        public static HybridCoreTopology Detect()
        {
            if (!OperatingSystem.IsWindows())
                return Empty;
            try
            {
                uint length = 0;
                _ = GetSystemCpuSetInformation(
                    IntPtr.Zero,
                    0,
                    out length,
                    IntPtr.Zero,
                    0);
                if (length == 0)
                    return Empty;
                var buffer = Marshal.AllocHGlobal(checked((int)length));
                try
                {
                    if (!GetSystemCpuSetInformation(
                            buffer,
                            length,
                            out length,
                            IntPtr.Zero,
                            0))
                        return Empty;
                    var physicalCores = new Dictionary<
                        (ushort Group, byte CoreIndex),
                        (byte FirstLogicalProcessor, byte EfficiencyClass)>();
                    var offset = 0;
                    while (offset + 20 <= length)
                    {
                        var address = IntPtr.Add(buffer, offset);
                        var size = Marshal.ReadInt32(address, 0);
                        if (size < 20 || offset + size > length)
                            break;
                        var type = Marshal.ReadInt32(address, 4);
                        if (type == 0)
                        {
                            var group = unchecked((ushort)
                                Marshal.ReadInt16(address, 12));
                            var logicalProcessorIndex =
                                Marshal.ReadByte(address, 14);
                            var coreIndex = Marshal.ReadByte(address, 15);
                            var efficiencyClass = Marshal.ReadByte(address, 18);
                            var key = (group, coreIndex);
                            if (!physicalCores.TryGetValue(key, out var current) ||
                                logicalProcessorIndex <
                                current.FirstLogicalProcessor)
                            {
                                physicalCores[key] = (
                                    logicalProcessorIndex,
                                    efficiencyClass);
                            }
                        }
                        offset += size;
                    }
                    // LibreHardwareMonitor numbers its per-core clock sensors
                    // by the order of CpuId core groups, which follows processor
                    // group/logical-processor order. Windows CoreIndex is a
                    // topology identifier and can be non-contiguous or ordered
                    // very differently on hybrid CPUs, so it must not be used
                    // directly as the sensor index.
                    var classes = physicalCores
                        .OrderBy(pair => pair.Key.Group)
                        .ThenBy(pair => pair.Value.FirstLogicalProcessor)
                        .Select((pair, index) => (
                            Index: index,
                            pair.Value.EfficiencyClass))
                        .ToDictionary(
                            pair => pair.Index,
                            pair => pair.EfficiencyClass);
                    var distinct = classes.Values.Distinct().OrderBy(value => value)
                        .ToArray();
                    return distinct.Length > 1
                        ? new HybridCoreTopology(
                            classes,
                            distinct[^1])
                        : Empty;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return Empty;
            }
        }

        private static HybridCoreTopology Empty { get; } =
            new(new Dictionary<int, byte>(), 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);
}
