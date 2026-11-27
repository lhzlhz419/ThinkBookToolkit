using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
    private readonly Dictionary<(string Name, string Identifier), CoreClockKind> _coreClockClasses = new();

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
                            (_hybridCoreTopology.LowPowerIndices.Contains(pair.Key) ? "LPE" : pair.Value ==
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

    internal GpuWorkerCommandResponse ReadGpuOverclockRanges() =>
        _gpuMonitor?.ReadOverclockRanges() ?? GpuWorkerCommandResponse.Failure("GPU monitoring is unavailable.");

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
                CoreClockKind.Performance))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleClock)
            .ToArray();
        var efficiencyCoreClocks = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Clock)
            .Where(sensor => IsHybridCoreClock(
                sensor,
                CoreClockKind.Efficiency))
            .Select(sensor => sensor.Value)
            .Where(IsPlausibleClock)
            .ToArray();

        var lowPowerClocks = cpuSensors.Where(sensor => sensor.SensorType == SensorType.Clock &&
                IsHybridCoreClock(sensor, CoreClockKind.LowPowerEfficiency) && IsPlausibleClock(sensor.Value))
            .Select(sensor => sensor.Value).ToArray();

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
            CpuLpeAverageClockMhz = lowPowerClocks.Length > 0 ? lowPowerClocks.Average() : null,
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
        CoreClockKind kind)
    {
        var key = (sensor.Name, sensor.Identifier);
        if (!_coreClockClasses.TryGetValue(key, out var classification))
        {
            classification = ClassifyCoreClock(sensor);
            _coreClockClasses[key] = classification;
        }
        return classification == kind;
    }

    private enum CoreClockKind { Unknown, Performance, Efficiency, LowPowerEfficiency }

    private CoreClockKind ClassifyCoreClock(SensorReading sensor)
    {
        string label = sensor.Name;
        if (Regex.IsMatch(label, @"\b(?:lp[ -]?e[ -]?core|low[ -]power (?:e|efficient|efficiency)[ -]core)", RegexOptions.IgnoreCase))
            return CoreClockKind.LowPowerEfficiency;
        if (Regex.IsMatch(label, @"\b(?:p[ -]core|performance core)", RegexOptions.IgnoreCase))
            return CoreClockKind.Performance;
        if ((!label.Contains("core", StringComparison.OrdinalIgnoreCase) && !label.Contains("核心")) ||
            label.Contains("uncore", StringComparison.OrdinalIgnoreCase))
            return CoreClockKind.Unknown;
        if (TryParseCpuCoreIndex(label, sensor.Identifier, out var index) && _hybridCoreTopology.LowPowerIndices.Contains(index))
            return CoreClockKind.LowPowerEfficiency;
        if (Regex.IsMatch(label, @"\b(?:e[ -]core|efficien(?:t|cy) core)", RegexOptions.IgnoreCase))
            return CoreClockKind.Efficiency;
        if (!_hybridCoreTopology.IsHybrid || !_hybridCoreTopology.EfficiencyClasses.TryGetValue(index, out var value))
            return CoreClockKind.Unknown;
        return value == _hybridCoreTopology.PerformanceClass ? CoreClockKind.Performance : CoreClockKind.Efficiency;
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

    internal static HashSet<int> FindLowPowerCores(
        (ushort Group, byte Processor, byte EfficiencyClass, byte CacheIndex)[] cores,
        int signature, Func<ushort, byte, int> readCacheLevel)
    {
        var classes = cores.Select(x => x.EfficiencyClass).Distinct().Order().ToArray();
        if (classes.Length < 2) return [];
        var efficient = Enumerable.Range(0, cores.Length).Where(i => cores[i].EfficiencyClass == classes[0]).ToArray();
        if (classes.Length == 3) return efficient.ToHashSet();

        // Meteor Lake and Arrow Lake-H have two SoC E-cores outside the compute tile LLC.
        var model = ((signature >> 12) & 0xf0) | ((signature >> 4) & 15);
        if (classes.Length != 2 || ((signature >> 8) & 15) != 6 || model is not (0xaa or 0xac or 0xc5) || efficient.Length <= 2) return [];
        var computeCaches = cores.Where(x => x.EfficiencyClass == classes[1]).Select(x => (x.Group, x.CacheIndex)).ToHashSet();
        var isolated = efficient.Where(i => !computeCaches.Contains((cores[i].Group, cores[i].CacheIndex))).ToArray();
        if (isolated.Length == 2) return isolated.ToHashSet();

        // When Windows combines the cache groups, check CPUID.4 on each E-core.
        var levels = efficient.Select(i => readCacheLevel(cores[i].Group, cores[i].Processor)).ToArray();
        if (levels.Count(level => level == 2) != 2 || !levels.Contains(3) || levels.Any(level => level is not (2 or 3))) return [];
        return efficient.Where((_, index) => levels[index] == 2).ToHashSet();
    }

    private sealed record HybridCoreTopology(
        IReadOnlyDictionary<int, byte> EfficiencyClasses,
        byte PerformanceClass, HashSet<int> LowPowerIndices)
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
                        (byte FirstLogicalProcessor, byte EfficiencyClass, byte CacheIndex)>();
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
                                    efficiencyClass, Marshal.ReadByte(address, 16));
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
                    var ordered = physicalCores.OrderBy(pair => pair.Key.Group)
                        .ThenBy(pair => pair.Value.FirstLogicalProcessor).ToArray();
                    var classes = ordered.Select((pair, index) => (index, pair.Value.EfficiencyClass))
                        .ToDictionary(x => x.index, x => x.EfficiencyClass);
                    var distinct = classes.Values.Distinct().Order().ToArray();
                    if (distinct.Length < 2) return Empty;
                    var cores = ordered.Select(x => (x.Key.Group, x.Value.FirstLogicalProcessor,
                        x.Value.EfficiencyClass, x.Value.CacheIndex)).ToArray();
                    var intel = X86Base.IsSupported && X86Base.CpuId(0, 0).Ebx == 0x756e6547;
                    var lowPower = intel ? FindLowPowerCores(cores, X86Base.CpuId(1, 0).Eax, HighestDataCacheLevel) : [];
                    return new(classes, distinct[^1], lowPower);
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
            new(new Dictionary<int, byte>(), 0, []);

        private static int HighestDataCacheLevel(ushort group, byte processor)
        {
            if (processor >= 64) return 0;
            var target = new GroupAffinity { Mask = (nuint)1 << processor, Group = group };
            var thread = GetCurrentThread();
            if (!SetThreadGroupAffinity(thread, ref target, out var previous)) return 0;
            try
            {
                int highest = 0;
                for (int i = 0; i < 32; i++)
                {
                    var eax = X86Base.CpuId(4, i).Eax;
                    if ((eax & 31) == 0) break;
                    if ((eax & 31) is 1 or 3) highest = Math.Max(highest, (eax >> 5) & 7);
                }
                return highest;
            }
            finally { SetThreadGroupAffinity(thread, ref previous, out _); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity { public nuint Mask; public ushort Group, Reserved1, Reserved2, Reserved3; }
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity target, out GroupAffinity previous);
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
