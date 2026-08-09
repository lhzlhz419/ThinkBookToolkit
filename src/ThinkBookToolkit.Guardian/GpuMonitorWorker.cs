using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace ThinkBookToolkit.Guardian;

internal sealed record GpuMonitorSnapshot(
    string Name,
    double? LoadPercent,
    double? MemoryLoadPercent,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? CoreTemperatureC,
    double? HotSpotTemperatureC,
    double? MemoryTemperatureC,
    IReadOnlyList<double> MemoryChipTemperaturesC,
    double? PowerW,
    string CoreTemperatureSensor,
    string MemoryTemperatureSensor);

internal static class GpuMonitorWorker
{
    internal const string ReadCommand = "READ";
    internal const string ReadNonNvidiaCommand = "READ_NON_NVIDIA";

    public static int Run(string pipeName)
    {
        using var log = new GuardianLog(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".thinkbook_toolkit",
                "log"),
            "gpu-worker");
        Computer? computer = null;
        GuardianNvidiaTelemetryReader? privateTelemetry = null;
        try
        {
            if (string.IsNullOrWhiteSpace(pipeName) ||
                !string.Equals(pipeName, Path.GetFileName(pipeName), StringComparison.Ordinal))
            {
                throw new ArgumentException("A valid GPU monitor pipe name is required.");
            }
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(5000);
            using var input = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            using var output = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true
            };
            computer = OpenComputer();
            privateTelemetry = GuardianNvidiaTelemetryReader.TryCreate();
            log.Info($"Isolated GPU monitor started (PID {Environment.ProcessId}).");
            var hardwareFailures = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var selectedGpuName = string.Empty;
            string? command;
            while ((command = input.ReadLine()) is not null)
            {
                if (string.Equals(command, "EXIT", StringComparison.OrdinalIgnoreCase))
                    return 0;
                if (string.Equals(command, "RESTART", StringComparison.OrdinalIgnoreCase))
                {
                    CloseComputer(computer);
                    computer = OpenComputer();
                    privateTelemetry = GuardianNvidiaTelemetryReader.TryCreate();
                    output.WriteLine("OK");
                    continue;
                }
                var nonNvidiaFallback = string.Equals(
                    command,
                    ReadNonNvidiaCommand,
                    StringComparison.OrdinalIgnoreCase);
                if (!nonNvidiaFallback &&
                    !string.Equals(command, ReadCommand, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine("null");
                    continue;
                }

                var snapshot = Read(
                    computer,
                    privateTelemetry,
                    log,
                    hardwareFailures,
                    nonNvidiaFallback);
                var nextGpuName = snapshot?.Name ?? string.Empty;
                if (!string.Equals(
                        selectedGpuName,
                        nextGpuName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    log.Info(string.IsNullOrWhiteSpace(nextGpuName)
                        ? "No usable GPU telemetry source is currently available."
                        : $"GPU telemetry source changed to {nextGpuName}.");
                    selectedGpuName = nextGpuName;
                }
                output.WriteLine(JsonSerializer.Serialize(snapshot));
            }
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("GPU monitor worker failed.", ex);
            return 1;
        }
        finally
        {
            if (computer is not null)
                CloseComputer(computer);
        }
    }

    private static Computer OpenComputer()
    {
        // LibreHardwareMonitor discovers Intel integrated graphics through
        // the Intel CPU group. IsCpuEnabled must therefore remain enabled in
        // this isolated process even though only GPU hardware is sampled.
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
        computer.Open();
        return computer;
    }

    private static void CloseComputer(Computer computer)
    {
        try
        {
            computer.Close();
        }
        catch
        {
        }
    }

    private static GpuMonitorSnapshot? Read(
        Computer computer,
        GuardianNvidiaTelemetryReader? privateTelemetry,
        GuardianLog log,
        HashSet<string> hardwareFailures,
        bool nonNvidiaFallback)
    {
        var candidates = new List<GpuReading>();
        foreach (var hardware in computer.Hardware.Where(item =>
                     IsGpu(item.HardwareType) &&
                     (!nonNvidiaFallback ||
                      item.HardwareType != HardwareType.GpuNvidia)))
        {
            try
            {
                hardware.Update();
                var sensors = new List<SensorReading>();
                CollectSensors(hardware, hardware.Name, sensors);
                candidates.Add(new GpuReading(
                    hardware.Name,
                    hardware.HardwareType,
                    sensors));
                hardwareFailures.Remove(hardware.Name);
            }
            catch (Exception ex)
            {
                if (hardwareFailures.Add(hardware.Name))
                {
                    log.Error(
                        $"GPU telemetry update failed for {hardware.Name}; other adapters will still be considered.",
                        ex);
                }
            }
        }

        var usableCandidates = candidates
            .Where(HasUsefulTelemetry)
            .ToArray();
        IEnumerable<GpuReading> selectableCandidates = usableCandidates.Length > 0
                ? usableCandidates
                : candidates;
        var gpu = selectableCandidates
            .OrderBy(item => Preference(item.Type))
            .ThenByDescending(TelemetryScore)
            .FirstOrDefault();
        if (gpu is null)
            return null;

        var coreTemperature = PickTemperature(
            gpu.Sensors,
            ["gpu core", "core"],
            fallbackToAny: true);
        var memoryTemperature = PickTemperature(
            gpu.Sensors,
            ["gpu memory junction", "memory junction", "gpu memory", "vram"]);
        var lhmHotSpot = PickTemperature(
            gpu.Sensors,
            ["gpu hot spot", "hot spot", "hotspot"]);
        var privateValues = gpu.Type == HardwareType.GpuNvidia
            ? privateTelemetry?.Read(gpu.Name) ?? NvidiaPrivateSnapshot.Empty
            : NvidiaPrivateSnapshot.Empty;
        var memoryLoad = PickValue(
            gpu.Sensors,
            SensorType.Load,
            ["gpu memory", "memory"]) ?? CalculateMemoryLoad(gpu.Sensors);

        return new GpuMonitorSnapshot(
            gpu.Name,
            PickValue(gpu.Sensors, SensorType.Load, ["gpu core", "core"]) ??
                gpu.Sensors
                    .Where(sensor => sensor.Type == SensorType.Load)
                    .Where(sensor => !ContainsAny(sensor, ["memory", "video engine", "bus"]))
                    .Select(sensor => (double?)sensor.Value)
                    .FirstOrDefault(),
            memoryLoad,
            PickValue(gpu.Sensors, SensorType.Clock, ["gpu core", "core"]),
            PickValue(gpu.Sensors, SensorType.Clock, ["gpu memory", "memory"]),
            coreTemperature.Sensor?.Value,
            privateValues.HotSpotTemperatureC ?? lhmHotSpot.Sensor?.Value,
            memoryTemperature.Sensor?.Value,
            privateValues.MemoryChipTemperaturesC,
            PickPower(gpu.Sensors),
            coreTemperature.Name,
            memoryTemperature.Name);
    }

    private static void CollectSensors(
        IHardware hardware,
        string path,
        List<SensorReading> sensors)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value)
                continue;
            sensors.Add(new SensorReading(
                sensor.Name,
                hardware.Name,
                path + "/" + sensor.Name,
                sensor.SensorType,
                value));
        }
        foreach (var child in hardware.SubHardware)
        {
            child.Update();
            CollectSensors(child, path + "/" + child.Name, sensors);
        }
    }

    private static (SensorReading? Sensor, string Name) PickTemperature(
        IEnumerable<SensorReading> sensors,
        string[] patterns,
        bool fallbackToAny = false)
    {
        var candidates = sensors
            .Where(sensor => sensor.Type == SensorType.Temperature)
            .Where(sensor => sensor.Value is > -40 and < 160)
            .ToArray();
        var selected = candidates
            .Where(sensor => ContainsAny(sensor, patterns))
            .OrderByDescending(sensor => sensor.Value)
            .FirstOrDefault();
        if (selected is null && fallbackToAny)
            selected = candidates.OrderByDescending(sensor => sensor.Value).FirstOrDefault();
        return selected is null
            ? (null, "not found")
            : (selected, selected.Identifier);
    }

    private static double? PickValue(
        IEnumerable<SensorReading> sensors,
        SensorType type,
        string[] patterns) => sensors
            .Where(sensor => sensor.Type == type)
            .Where(sensor => ContainsAny(sensor, patterns))
            .OrderBy(sensor => PatternIndex(sensor, patterns))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault();

    private static double? PickPower(IEnumerable<SensorReading> sensors)
    {
        var power = sensors.Where(sensor => sensor.Type == SensorType.Power).ToArray();
        return power
            .Where(sensor => ContainsAny(
                sensor,
                ["gpu power", "gpu package", "package", "total board", "board power"]))
            .OrderByDescending(sensor => sensor.Value)
            .FirstOrDefault()?.Value ??
            power.OrderByDescending(sensor => sensor.Value).FirstOrDefault()?.Value;
    }

    private static double? CalculateMemoryLoad(IReadOnlyList<SensorReading> sensors)
    {
        var used = sensors.FirstOrDefault(sensor =>
            sensor.Type is SensorType.Data or SensorType.SmallData &&
            ContainsAny(sensor, ["memory used", "dedicated memory used"]));
        var total = sensors.FirstOrDefault(sensor =>
            sensor.Type is SensorType.Data or SensorType.SmallData &&
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

    private static int Preference(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 0,
        HardwareType.GpuAmd => 1,
        _ => 2
    };

    private static bool HasUsefulTelemetry(GpuReading gpu) =>
        TelemetryScore(gpu) > 0;

    private static int TelemetryScore(GpuReading gpu) => gpu.Sensors.Count(sensor =>
        sensor.Type switch
        {
            SensorType.Temperature => sensor.Value is > -40 and < 160,
            SensorType.Load => sensor.Value is >= 0 and <= 100,
            SensorType.Clock => sensor.Value is > 0 and < 20000,
            SensorType.Power => sensor.Value is >= 0 and < 2000,
            _ => false
        });

    private sealed record GpuReading(
        string Name,
        HardwareType Type,
        IReadOnlyList<SensorReading> Sensors);

    private sealed record SensorReading(
        string Name,
        string HardwareName,
        string Identifier,
        SensorType Type,
        double Value);
}

internal sealed record NvidiaPrivateSnapshot(
    IReadOnlyList<double> MemoryChipTemperaturesC,
    double? HotSpotTemperatureC)
{
    public static NvidiaPrivateSnapshot Empty { get; } = new([], null);
}

internal sealed class GuardianNvidiaTelemetryReader
{
    private const uint InitializeInterfaceId = 0x0150E828;
    private const uint EnumPhysicalGpusInterfaceId = 0xE5AC921F;
    private const uint GpuGetFullNameInterfaceId = 0xCEEE8E9F;
    private const uint GpuRegisterOpInterfaceId = 0x2EB3C140;
    private const int RequestSize = 0x1808;
    private const int RecordSize = 0x18;
    private const ushort ReadRegisterOpcode = 0x15;
    private readonly IReadOnlyList<GpuHandle> _gpus;
    private readonly RegisterOpDelegate _registerOp;

    private GuardianNvidiaTelemetryReader(
        IReadOnlyList<GpuHandle> gpus,
        RegisterOpDelegate registerOp)
    {
        _gpus = gpus;
        _registerOp = registerOp;
    }

    public static GuardianNvidiaTelemetryReader? TryCreate()
    {
        try
        {
            var initializePointer = QueryInterface(InitializeInterfaceId);
            var enumeratePointer = QueryInterface(EnumPhysicalGpusInterfaceId);
            var registerPointer = QueryInterface(GpuRegisterOpInterfaceId);
            if (initializePointer == IntPtr.Zero ||
                enumeratePointer == IntPtr.Zero ||
                registerPointer == IntPtr.Zero)
            {
                return null;
            }

            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initializePointer);
            if (initialize() != 0)
                return null;
            var handles = new IntPtr[64];
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusDelegate>(enumeratePointer);
            if (enumerate(handles, out var count) != 0 || count <= 0)
                return null;

            var getNamePointer = QueryInterface(GpuGetFullNameInterfaceId);
            GetFullNameDelegate? getName = getNamePointer == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer<GetFullNameDelegate>(getNamePointer);
            var gpus = new List<GpuHandle>();
            for (var index = 0; index < Math.Min(count, handles.Length); index++)
            {
                if (handles[index] == IntPtr.Zero)
                    continue;
                var name = string.Empty;
                if (getName is not null)
                {
                    var text = new StringBuilder(64);
                    if (getName(handles[index], text) == 0)
                        name = text.ToString();
                }
                gpus.Add(new GpuHandle(handles[index], name));
            }
            return gpus.Count == 0
                ? null
                : new GuardianNvidiaTelemetryReader(
                    gpus,
                    Marshal.GetDelegateForFunctionPointer<RegisterOpDelegate>(registerPointer));
        }
        catch
        {
            return null;
        }
    }

    public NvidiaPrivateSnapshot Read(string gpuName)
    {
        if (!SupportsPrivateTelemetry(gpuName))
            return NvidiaPrivateSnapshot.Empty;
        try
        {
            var gpu = SelectGpu(gpuName);
            if (gpu == IntPtr.Zero)
                return NvidiaPrivateSnapshot.Empty;
            return new NvidiaPrivateSnapshot(
                ReadMemoryChipTemperatures(gpu),
                IsRtx50Series(gpuName) ? ReadBlackwellHotSpot(gpu) : null);
        }
        catch
        {
            return NvidiaPrivateSnapshot.Empty;
        }
    }

    private IReadOnlyList<double> ReadMemoryChipTemperatures(IntPtr gpu)
    {
        var layoutValues = ReadRegisters(gpu, [0x00900200u]);
        if (layoutValues.Count == 0 || layoutValues[0] is not { } layout)
            return [];
        var fourLaneGroups = (layout & 0x00400000u) != 0;
        var positions = Enumerable.Range(0, 16)
            .Select(index =>
            {
                var group = fourLaneGroups ? index / 4 : index / 2;
                var lane = fourLaneGroups ? index & 3 : index & 1;
                var block = 0x009024C0u + (uint)group * 0x4000u;
                return new MemoryPosition(lane, block + 0x10u, block + (uint)lane * 4u);
            })
            .ToArray();
        var addresses = positions
            .SelectMany(position => new[] { position.ValidAddress, position.DataAddress })
            .Distinct()
            .ToArray();
        var values = ReadRegisters(gpu, addresses);
        var registers = addresses
            .Select((address, index) => (address, value: values[index]))
            .ToDictionary(item => item.address, item => item.value);
        var result = new List<double>();
        foreach (var position in positions)
        {
            if (!registers.TryGetValue(position.ValidAddress, out var validValue) ||
                validValue is not { } valid ||
                IsPoisoned(valid) ||
                ((valid >> (24 + position.Lane)) & 1u) == 0 ||
                !registers.TryGetValue(position.DataAddress, out var dataValue) ||
                dataValue is not { } data ||
                IsPoisoned(data))
            {
                continue;
            }
            var value = 2d * (((data >> 16) & 0xFF) - 20);
            if (value is > -40 and < 160)
                result.Add(value);
        }
        return result;
    }

    private double? ReadBlackwellHotSpot(IntPtr gpu)
    {
        double? maximum = null;
        foreach (var rawValue in ReadRegisters(
                     gpu,
                     Enumerable.Range(0, 6)
                         .Select(index => 0x00AD0A90u + (uint)index * 4u)
                         .ToArray()))
        {
            if (rawValue is not { } raw || (raw & 0x40000000u) == 0)
                continue;
            var value = ((raw >> 3) & 0x1FFFu) / 32d;
            if ((raw & 0x00010000u) != 0)
                value = -value;
            if (value is <= -40 or >= 160)
                continue;
            maximum = !maximum.HasValue || value > maximum.Value ? value : maximum;
        }
        return maximum;
    }

    private IReadOnlyList<uint?> ReadRegisters(IntPtr gpu, IReadOnlyList<uint> addresses)
    {
        if (addresses.Count is <= 0 or > 256)
            return [];
        var request = new byte[RequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), 0x00011808u);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4, 4), (uint)addresses.Count);
        for (var index = 0; index < addresses.Count; index++)
        {
            var offset = 8 + index * RecordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(offset, 2), ReadRegisterOpcode);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(offset + 4, 4), addresses[index]);
        }

        var pin = GCHandle.Alloc(request, GCHandleType.Pinned);
        try
        {
            if (_registerOp(gpu, pin.AddrOfPinnedObject()) != 0)
                return Enumerable.Repeat<uint?>(null, addresses.Count).ToArray();
        }
        finally
        {
            pin.Free();
        }

        var result = new uint?[addresses.Count];
        for (var index = 0; index < addresses.Count; index++)
        {
            var offset = 8 + index * RecordSize;
            if (BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(offset + 2, 2)) == 0)
                result[index] = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(offset + 0x10, 4));
        }
        return result;
    }

    private IntPtr SelectGpu(string name)
    {
        if (_gpus.Count == 1)
            return _gpus[0].Handle;
        var normalized = Normalize(name);
        var match = _gpus.FirstOrDefault(gpu =>
            Normalize(gpu.Name).Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(Normalize(gpu.Name), StringComparison.OrdinalIgnoreCase));
        return match?.Handle ?? _gpus[0].Handle;
    }

    private static string Normalize(string value) => value
        .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("GeForce", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();

    private static bool SupportsPrivateTelemetry(string name) =>
        name.Contains("RTX 30", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ||
        IsRtx50Series(name);

    private static bool IsRtx50Series(string name) =>
        name.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);

    private static bool IsPoisoned(uint value) =>
        (value & 0xFFFF0000u) == 0xBADF0000u;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFullNameDelegate(IntPtr handle, StringBuilder name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterOpDelegate(IntPtr handle, IntPtr request);

    private sealed record GpuHandle(IntPtr Handle, string Name);
    private sealed record MemoryPosition(int Lane, uint ValidAddress, uint DataAddress);
}
