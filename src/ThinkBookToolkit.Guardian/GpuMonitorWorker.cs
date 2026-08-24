using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

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
    double? PowerLimitW,
    string CoreTemperatureSensor,
    string MemoryTemperatureSensor,
    DiscreteGpuActivityState DiscreteGpuState,
    string PerformanceState);

internal static class GpuMonitorWorker
{
    internal const string ReadCommand = "READ";
    internal const string ReadQuiescingCommand = "READ_QUIESCING";
    internal const string ReadNonNvidiaCommand = "READ_NON_NVIDIA";
    internal const string ReadNonNvidiaFallbackCommand =
        "READ_NON_NVIDIA_FALLBACK";
    internal const string ListApplicationsCommand = "LIST_APPLICATIONS";
    internal const string KillApplicationsCommand = "KILL_APPLICATIONS";
    internal const string ApplyOverclockCommand = "APPLY_OVERCLOCK:";
    internal const string ResetOverclockCommand = "RESET_OVERCLOCK";

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
        GuardianNvidiaStateReader? stateReader = null;
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
                    if (computer is not null)
                        CloseComputer(computer);
                    stateReader?.Dispose();
                    computer = null;
                    privateTelemetry = null;
                    stateReader = null;
                    output.WriteLine("OK");
                    continue;
                }
                if (string.Equals(
                        command,
                        ListApplicationsCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stateReader ??= new GuardianNvidiaStateReader(log);
                    output.WriteLine(JsonSerializer.Serialize(
                        stateReader.QueryApplications()));
                    continue;
                }
                if (string.Equals(
                        command,
                        KillApplicationsCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stateReader ??= new GuardianNvidiaStateReader(log);
                    output.WriteLine(JsonSerializer.Serialize(
                        stateReader.KillApplications()));
                    continue;
                }
                if (command.StartsWith(
                        ApplyOverclockCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stateReader ??= new GuardianNvidiaStateReader(log);
                    output.WriteLine(JsonSerializer.Serialize(
                        ApplyOverclock(command, stateReader)));
                    continue;
                }
                if (string.Equals(
                        command,
                        ResetOverclockCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stateReader ??= new GuardianNvidiaStateReader(log);
                    output.WriteLine(JsonSerializer.Serialize(
                        stateReader.ApplyOverclock(
                            new GpuOverclockSettings())));
                    continue;
                }
                var integratedOnly = string.Equals(
                    command,
                    ReadNonNvidiaCommand,
                    StringComparison.OrdinalIgnoreCase);
                var nonNvidiaFallback = integratedOnly || string.Equals(
                    command,
                    ReadNonNvidiaFallbackCommand,
                    StringComparison.OrdinalIgnoreCase);
                var quiescing = string.Equals(
                    command,
                    ReadQuiescingCommand,
                    StringComparison.OrdinalIgnoreCase);
                if (!nonNvidiaFallback && !quiescing &&
                    !string.Equals(command, ReadCommand, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine("null");
                    continue;
                }

                GpuMonitorSnapshot? snapshot;
                if (nonNvidiaFallback)
                {
                    var activity = integratedOnly
                        ? NvidiaActivitySnapshot.Off
                        : new NvidiaActivitySnapshot(
                            string.Empty,
                            DiscreteGpuActivityState.Unknown,
                            string.Empty);
                    computer ??= OpenComputer();
                    snapshot = Read(
                        computer,
                        null,
                        log,
                        hardwareFailures,
                        nonNvidiaFallback: true,
                        activity,
                        powerLimitW: null) ??
                        StateOnlySnapshot(activity);
                }
                else
                {
                    stateReader ??= new GuardianNvidiaStateReader(log);
                    var activity = stateReader.Read(
                        includeDisplayActivity: !quiescing);
                    var canReadNvidiaTelemetry =
                        activity.State == DiscreteGpuActivityState.Active ||
                        !quiescing &&
                        activity.State == DiscreteGpuActivityState.Unknown;
                    if (canReadNvidiaTelemetry)
                    {
                        computer ??= OpenComputer();
                        privateTelemetry ??=
                            GuardianNvidiaTelemetryReader.TryCreate();
                        snapshot = Read(
                            computer,
                            privateTelemetry,
                            log,
                            hardwareFailures,
                            nonNvidiaFallback: false,
                            activity,
                            stateReader.ReadPowerLimitW());
                    }
                    else
                    {
                        // Return the final inactive/off state without another
                        // NVIDIA LHM update. The parent immediately ends this
                        // worker after receiving it, which also releases the
                        // NVAPI handle before firmware performs PnP removal.
                        snapshot = StateOnlySnapshot(activity);
                    }
                }
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
            stateReader?.Dispose();
        }
    }

    private static GpuWorkerCommandResponse ApplyOverclock(
        string command,
        GuardianNvidiaStateReader stateReader)
    {
        try
        {
            var encoded = command[ApplyOverclockCommand.Length..];
            var json = Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded));
            var settings = JsonSerializer.Deserialize<GpuOverclockSettings>(
                json);
            return settings is null
                ? GpuWorkerCommandResponse.Failure(
                    "GPU overclock settings could not be decoded.")
                : stateReader.ApplyOverclock(settings);
        }
        catch (Exception ex)
        {
            return GpuWorkerCommandResponse.Failure(ex.Message);
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
        bool nonNvidiaFallback,
        NvidiaActivitySnapshot activity,
        double? powerLimitW)
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
            powerLimitW,
            coreTemperature.Name,
            memoryTemperature.Name,
            activity.State,
            activity.PerformanceState);
    }

    private static GpuMonitorSnapshot StateOnlySnapshot(
        NvidiaActivitySnapshot activity) =>
        new(
            activity.Name,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            null,
            "not found",
            "not found",
            activity.State,
            activity.PerformanceState);

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

internal sealed record NvidiaActivitySnapshot(
    string Name,
    DiscreteGpuActivityState State,
    string PerformanceState)
{
    public static NvidiaActivitySnapshot Off { get; } =
        new(string.Empty, DiscreteGpuActivityState.Off, string.Empty);
}

internal sealed class GuardianNvidiaStateReader : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] ExcludedApplications =
        ["dwm.exe", "explorer.exe"];
    private readonly GuardianLog _log;
    private PhysicalGPU? _gpu;
    private string _gpuName = string.Empty;
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset _nextPowerLimitRefresh;
    private bool? _lastIncludeDisplayActivity;
    private double? _lastPowerLimitW;
    private bool _powerLimitFailureLogged;
    private NvidiaActivitySnapshot _last =
        new(string.Empty, DiscreteGpuActivityState.Unknown, string.Empty);

    public GuardianNvidiaStateReader(GuardianLog log)
    {
        _log = log;
    }

    public NvidiaActivitySnapshot Read(bool includeDisplayActivity)
    {
        if (_lastIncludeDisplayActivity == includeDisplayActivity &&
            DateTimeOffset.UtcNow < _nextRefresh)
        {
            return _last;
        }

        _lastIncludeDisplayActivity = includeDisplayActivity;
        _nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
        var next = ReadCore(includeDisplayActivity);
        if (next.State != _last.State ||
            !string.Equals(
                next.PerformanceState,
                _last.PerformanceState,
                StringComparison.OrdinalIgnoreCase))
        {
            _log.Info(
                $"Discrete GPU state changed from {_last.State} " +
                $"to {next.State}" +
                (string.IsNullOrWhiteSpace(next.PerformanceState)
                    ? "."
                    : $" ({next.PerformanceState})."));
        }
        _last = next;
        return next;
    }

    public double? ReadPowerLimitW()
    {
        if (DateTimeOffset.UtcNow < _nextPowerLimitRefresh)
            return _lastPowerLimitW;

        _nextPowerLimitRefresh = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        try
        {
            _gpu ??= FindLaptopGpu();
            if (_gpu is null)
                return null;
            _lastPowerLimitW = NvidiaLockedClockApi.ReadEnforcedPowerLimitW(
                _gpu.BusInformation.BusId,
                _gpu.BusInformation.BusSlot);
            _powerLimitFailureLogged = false;
            return _lastPowerLimitW;
        }
        catch (Exception ex)
        {
            if (!_powerLimitFailureLogged)
            {
                _log.Error("NVIDIA enforced power-limit query failed.", ex);
                _powerLimitFailureLogged = true;
            }
            _nextPowerLimitRefresh = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            return null;
        }
    }

    private NvidiaActivitySnapshot ReadCore(bool includeDisplayActivity)
    {
        try
        {
            _gpu ??= FindLaptopGpu();
            if (_gpu is null)
                return NvidiaActivitySnapshot.Off;

            if (string.IsNullOrWhiteSpace(_gpuName))
                _gpuName = _gpu.FullName ?? string.Empty;
            var name = _gpuName;
            var performanceState = ReadPerformanceState(_gpu, out var off);
            if (off)
            {
                return new NvidiaActivitySnapshot(
                    name,
                    DiscreteGpuActivityState.Off,
                    string.Empty);
            }

            var applications = GPUApi.QueryActiveApps(_gpu.Handle);
            var active = applications.Any(application =>
                !ExcludedApplications.Contains(
                    application.ProcessName,
                    StringComparer.OrdinalIgnoreCase));
            if (!active && includeDisplayActivity)
                active = HasConnectedDisplay(_gpu);
            return new NvidiaActivitySnapshot(
                name,
                active
                    ? DiscreteGpuActivityState.Active
                    : DiscreteGpuActivityState.Inactive,
                performanceState);
        }
        catch (NVIDIAApiException ex) when (MeansPoweredOff(ex))
        {
            return new NvidiaActivitySnapshot(
                _gpuName,
                DiscreteGpuActivityState.Off,
                string.Empty);
        }
        catch (NVIDIAApiException ex)
        {
            _log.Error(
                $"NVAPI discrete GPU state query failed ({(int)ex.Status}).",
                ex);
            _gpu = null;
            return new NvidiaActivitySnapshot(
                _last.Name,
                DiscreteGpuActivityState.Unknown,
                string.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("Discrete GPU state query failed.", ex);
            _gpu = null;
            return new NvidiaActivitySnapshot(
                _last.Name,
                DiscreteGpuActivityState.Unknown,
                string.Empty);
        }
    }

    public GpuWorkerCommandResponse QueryApplications()
    {
        try
        {
            _gpu ??= FindLaptopGpu();
            if (_gpu is null)
                return GpuWorkerCommandResponse.Failure(
                    "The discrete GPU is unavailable.");

            var applications = new List<DiscreteGpuApplication>();
            foreach (var application in GPUApi.QueryActiveApps(_gpu.Handle))
            {
                if (application.ProcessId == Environment.ProcessId ||
                    ExcludedApplications.Contains(
                        application.ProcessName,
                        StringComparer.OrdinalIgnoreCase) ||
                    IsToolkitProcessName(application.ProcessName))
                {
                    continue;
                }

                try
                {
                    using var process = Process.GetProcessById(
                        application.ProcessId);
                    var name = string.IsNullOrWhiteSpace(
                            application.ProcessName)
                        ? process.ProcessName
                        : application.ProcessName;
                    var path = string.Empty;
                    try
                    {
                        path = process.MainModule?.FileName ?? string.Empty;
                    }
                    catch
                    {
                    }
                    applications.Add(new DiscreteGpuApplication(
                        process.Id,
                        name,
                        path));
                }
                catch (ArgumentException)
                {
                }
            }

            return GpuWorkerCommandResponse.Ok(applications
                .DistinctBy(item => item.ProcessId)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ProcessId)
                .ToArray());
        }
        catch (Exception ex)
        {
            _log.Error("Discrete GPU application query failed.", ex);
            return GpuWorkerCommandResponse.Failure(ex.Message);
        }
    }

    public GpuWorkerCommandResponse KillApplications()
    {
        var query = QueryApplications();
        if (!query.Success)
            return query;

        var killed = 0;
        var failures = new List<string>();
        foreach (var application in query.Applications)
        {
            try
            {
                using var process = Process.GetProcessById(
                    application.ProcessId);
                if (process.Id == Environment.ProcessId ||
                    IsToolkitProcessName(process.ProcessName))
                {
                    continue;
                }
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(3000))
                {
                    failures.Add($"{application.Name} ({application.ProcessId}) did not exit in time");
                    continue;
                }
                killed++;
            }
            catch (ArgumentException)
            {
                // The process already exited between enumeration and kill.
                killed++;
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"{application.Name} ({application.ProcessId}): {ex.Message}");
            }
        }

        _nextRefresh = DateTimeOffset.MinValue;
        return failures.Count == 0
            ? GpuWorkerCommandResponse.Ok(affectedProcesses: killed)
            : new GpuWorkerCommandResponse(
                false,
                string.Join(Environment.NewLine, failures),
                [],
                killed);
    }

    public GpuWorkerCommandResponse ApplyOverclock(
        GpuOverclockSettings settings)
    {
        if (!GpuOverclockPolicy.TryValidate(settings, out var error))
            return GpuWorkerCommandResponse.Failure(error);
        try
        {
            _gpu ??= FindLaptopGpu();
            if (_gpu is null)
                return GpuWorkerCommandResponse.Failure(
                    "The discrete GPU is unavailable.");
            GuardianNvidiaOverclockController.Apply(_gpu, settings);
            return GpuWorkerCommandResponse.Ok();
        }
        catch (Exception ex)
        {
            _log.Error("Discrete GPU overclock apply failed.", ex);
            return GpuWorkerCommandResponse.Failure(ex.Message);
        }
    }

    private static PhysicalGPU? FindLaptopGpu()
    {
        NVIDIA.Initialize();
        return PhysicalGPU.GetPhysicalGPUs()
            .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop) ??
            PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
    }

    private static bool IsToolkitProcessName(string? name)
    {
        var normalized = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        return string.Equals(
            normalized,
            "ThinkBookToolkit",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadPerformanceState(
        PhysicalGPU gpu,
        out bool poweredOff)
    {
        poweredOff = false;
        try
        {
            var value = gpu.PerformanceStatesInfo
                .CurrentPerformanceState
                .StateId
                .ToString();
            var separator = value.IndexOf('_');
            return separator > 0 ? value[..separator] : value;
        }
        catch (NVIDIAApiException ex) when (MeansPoweredOff(ex))
        {
            poweredOff = true;
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool HasConnectedDisplay(PhysicalGPU gpu)
    {
        try
        {
            return Display.GetDisplays().Any(display =>
                display.PhysicalGPUs.Any(candidate =>
                    candidate.GPUId == gpu.GPUId));
        }
        catch
        {
            return false;
        }
    }

    private static bool MeansPoweredOff(NVIDIAApiException exception)
    {
        var value = (int)exception.Status;
        return value is -105 or -220 or -10 or -8 or -6 or -2 ||
               value == -156;
    }

    public void Dispose()
    {
        _gpu = null;
        try
        {
            NVIDIA.Unload();
        }
        catch
        {
        }
    }
}

internal static class GuardianNvidiaOverclockController
{
    public static void Apply(
        PhysicalGPU gpu,
        GpuOverclockSettings settings)
    {
        var clockEntries = new[]
        {
            new PerformanceStates20ClockEntryV1(
                PublicClockDomain.Graphics,
                new PerformanceStates20ParameterDelta(
                    settings.CoreFrequencyOffsetMhz * 1000)),
            new PerformanceStates20ClockEntryV1(
                PublicClockDomain.Memory,
                new PerformanceStates20ParameterDelta(
                    settings.MemoryFrequencyOffsetMhz * 1000))
        };
        var performanceState = new[]
        {
            new PerformanceStates20InfoV1.PerformanceState20(
                PerformanceStateId.P0_3DPerformance,
                clockEntries,
                [])
        };
        GPUApi.SetPerformanceStates20(
            gpu.Handle,
            new PerformanceStates20InfoV1(performanceState, 2, 0));

        if (settings.MinimumCoreFrequencyMhz.HasValue &&
            settings.MaximumCoreFrequencyMhz.HasValue)
        {
            NvidiaLockedClockApi.Set(
                gpu.BusInformation.BusId,
                gpu.BusInformation.BusSlot,
                (uint)settings.MinimumCoreFrequencyMhz.Value,
                (uint)settings.MaximumCoreFrequencyMhz.Value);
        }
        else
        {
            NvidiaLockedClockApi.Reset(
                gpu.BusInformation.BusId,
                gpu.BusInformation.BusSlot);
        }
    }
}

internal static class NvidiaLockedClockApi
{
    private const int Success = 0;

    public static void Set(
        int busId,
        int busSlot,
        uint minimumMhz,
        uint maximumMhz) =>
        WithDevice(
            busId,
            busSlot,
            device => Check(
                NvmlDeviceSetGpuLockedClocks(
                    device,
                    minimumMhz,
                    maximumMhz),
                "lock core clocks"));

    public static void Reset(int busId, int busSlot) =>
        WithDevice(
            busId,
            busSlot,
            device => Check(
                NvmlDeviceResetGpuLockedClocks(device),
                "reset core clock limits"));

    public static double ReadEnforcedPowerLimitW(int busId, int busSlot)
    {
        uint milliwatts = 0;
        WithDevice(
            busId,
            busSlot,
            device => Check(
                NvmlDeviceGetEnforcedPowerLimit(device, out milliwatts),
                "read the enforced power limit"));
        return milliwatts / 1000d;
    }

    private static void WithDevice(
        int busId,
        int busSlot,
        Action<IntPtr> action)
    {
        Check(NvmlInit(), "initialize NVIDIA management API");
        try
        {
            var pciAddress = $"00000000:{busId:X2}:{busSlot:X2}.0";
            var status = NvmlDeviceGetHandleByPciBusId(
                pciAddress,
                out var device);
            if (status != Success)
                status = NvmlDeviceGetHandleByIndex(0, out device);
            Check(status, "locate the NVIDIA GPU");
            action(device);
        }
        finally
        {
            _ = NvmlShutdown();
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != Success)
        {
            throw new InvalidOperationException(
                $"NVIDIA could not {operation} (status {status}).");
        }
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi,
        EntryPoint = "nvmlDeviceGetHandleByPciBusId_v2")]
    private static extern int NvmlDeviceGetHandleByPciBusId(
        string pciBusId,
        out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(
        uint index,
        out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceSetGpuLockedClocks")]
    private static extern int NvmlDeviceSetGpuLockedClocks(
        IntPtr device,
        uint minimumMhz,
        uint maximumMhz);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
    private static extern int NvmlDeviceResetGpuLockedClocks(IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int NvmlDeviceGetEnforcedPowerLimit(
        IntPtr device,
        out uint limit);
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
