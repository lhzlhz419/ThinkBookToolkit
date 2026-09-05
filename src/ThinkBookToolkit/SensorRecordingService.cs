using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed record SensorRecordingSample(
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, double?> Values);

internal static class SensorRecordingFormat
{
    internal static readonly IReadOnlyList<string> MetricKeys =
    [
        "fps", "fps1Low", "latencyMs", "cpuUtilization", "cpuAverageMhz",
        "cpuPerformanceCoreAverageMhz", "cpuEfficiencyCoreAverageMhz",
        "cpuMaximumMhz", "cpuTemperatureC", "cpuPowerW", "gpuUtilization",
        "vramUtilization", "gpuCoreMhz", "vramMhz", "gpuTemperatureC",
        "gpuHotSpotTemperatureC", "vramTemperatureC", "gpuPowerW",
        "ramUsedGb", "ramUtilization", "committedUsedGb",
        "committedUtilization", "memorySlot1TemperatureC",
        "memorySlot2TemperatureC", "batteryPowerW", "fan1Rpm", "fan2Rpm",
        "disk1TemperatureC", "disk2TemperatureC", "disk3TemperatureC",
        "disk4TemperatureC", "disk5TemperatureC", "disk6TemperatureC",
        "disk7TemperatureC", "disk8TemperatureC", "batteryCapacityWh"
    ];
    private static readonly IReadOnlyDictionary<string, int> MetricIds =
        MetricKeys.Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, StringComparer.Ordinal);

    internal static IReadOnlyList<string> OrderKeys(IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.Ordinal)
            .Where(MetricIds.ContainsKey)
            .OrderBy(key => MetricIds[key])
            .ToArray();

    internal static string Header(IReadOnlyList<string> keys) =>
        "[2" + string.Concat(keys.Select(key => "," + MetricIds[key])) + "]";

    internal static string Batch(
        IReadOnlyList<SensorRecordingSample> samples,
        IReadOnlyList<string> keys)
    {
        var text = new StringBuilder(samples.Count * (keys.Count * 7 + 18));
        text.Append('[');
        for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            if (sampleIndex > 0) text.Append(',');
            var sample = samples[sampleIndex];
            text.Append('[').Append(sample.Timestamp.ToUnixTimeMilliseconds());
            foreach (var key in keys)
            {
                text.Append(',');
                if (!sample.Values.TryGetValue(key, out var value) || !value.HasValue)
                    text.Append("null");
                else
                    text.Append(value.Value.ToString("0.##", CultureInfo.InvariantCulture));
            }
            text.Append(']');
        }
        return text.Append(']').ToString();
    }

    internal static bool TryReadHeader(
        string line,
        out IReadOnlyList<string> keys)
    {
        keys = [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var values = document.RootElement;
            if (values.ValueKind != System.Text.Json.JsonValueKind.Array ||
                values.GetArrayLength() < 1 || values[0].GetInt32() != 2)
                return false;
            var result = new List<string>();
            for (var index = 1; index < values.GetArrayLength(); index++)
            {
                var id = values[index].GetInt32();
                if (id >= 0 && id < MetricKeys.Count)
                    result.Add(MetricKeys[id]);
            }
            keys = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void ReadBatch(
        string line,
        IReadOnlyList<string> keys,
        ICollection<SensorRecordingSample> destination)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Array ||
                    row.GetArrayLength() == 0 ||
                    !row[0].TryGetInt64(out var milliseconds))
                    continue;
                var readings = new Dictionary<string, double?>(
                    keys.Count,
                    StringComparer.Ordinal);
                for (var index = 0; index < keys.Count; index++)
                {
                    var column = index + 1;
                    readings[keys[index]] = column < row.GetArrayLength() &&
                        row[column].ValueKind == System.Text.Json.JsonValueKind.Number &&
                        row[column].TryGetDouble(out var value)
                            ? value
                            : null;
                }
                destination.Add(new SensorRecordingSample(
                    DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
                    readings));
            }
        }
        catch
        {
            // The active writer may not have completed its final line yet.
        }
    }
}

internal sealed class SensorRecordingService : IDisposable
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _flushTimer = new()
    {
        Interval = TimeSpan.FromMinutes(1)
    };
    private readonly List<SensorRecordingSample> _buffer = [];
    private StreamWriter? _writer;
    private IReadOnlyList<string> _metricKeys = [];
    private bool _writing;

    public SensorRecordingService(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _timer.Tick += async (_, _) => await CollectSampleAsync();
        _flushTimer.Tick += (_, _) => FlushBufferedSamples();
    }

    public string CurrentPath { get; private set; } = string.Empty;
    public IReadOnlyList<SensorRecordingSample> BufferedSamples =>
        _buffer.ToArray();
    public event EventHandler? SampleWritten;

    public void Sync()
    {
        if (_runtime.IsSystemSessionEnding)
        {
            Stop();
            return;
        }
        if (_runtime.Settings.SensorRecordingEnabled)
        {
            var desiredKeys = KeysForSelectedSensors(
                _runtime.Settings.SensorRecording.Sensors);
            if (_writer is null || !_metricKeys.SequenceEqual(desiredKeys))
                StartNewFile();
            _timer.Interval = TimeSpan.FromSeconds(
                _runtime.Settings.SensorRecording.IntervalSeconds);
            SyncFpsConsumer();
            _timer.Start();
            _flushTimer.Start();
        }
        else
        {
            Stop();
        }
    }

    public void StartNewFile()
    {
        Stop();
        Directory.CreateDirectory(CurveProfileStore.SensorRecordingDirectory);
        CurrentPath = Path.Combine(
            CurveProfileStore.SensorRecordingDirectory,
            $"sensors-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
        var stream = new FileStream(
            CurrentPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            64 * 1024);
        _metricKeys = KeysForSelectedSensors(
            _runtime.Settings.SensorRecording.Sensors);
        _writer.WriteLine(SensorRecordingFormat.Header(_metricKeys));
        _runtime.Settings.LastSensorRecordingPath = CurrentPath;
        SaveRecordingPath();
        ToolkitLog.Info("Sensor recording started: " + CurrentPath);
    }

    public void Stop()
    {
        _timer.Stop();
        _flushTimer.Stop();
        _runtime.SetFpsMonitoringConsumer("sensor-recording", false);
        FlushBufferedSamples();
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); } catch { }
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            var completedPath = CurrentPath;
            try
            {
                completedPath = SensorRecordingArchive
                    .CompressAndDeleteSource(CurrentPath);
                _runtime.Settings.LastSensorRecordingPath = completedPath;
                SaveRecordingPath();
                ToolkitLog.Info(
                    "Sensor recording compressed: " + completedPath);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error(
                    "Sensor recording could not be compressed; the original file was kept: " +
                    CurrentPath,
                    ex);
            }
            ToolkitLog.Info("Sensor recording stopped: " + completedPath);
        }
        CurrentPath = string.Empty;
        _metricKeys = [];
        _buffer.Clear();
    }

    private void SaveRecordingPath()
    {
        try
        {
            CurveProfileStore.SaveSettings(_runtime.Settings);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "Sensor recording path could not be saved.",
                ex);
        }
    }

    private void SyncFpsConsumer()
    {
        var sensors = _runtime.Settings.SensorRecording.Sensors;
        var required = sensors.Contains(OsdSensor.Fps) ||
                       sensors.Contains(OsdSensor.OnePercentLowFps) ||
                       sensors.Contains(OsdSensor.FrameLatency);
        _runtime.SetFpsMonitoringConsumer("sensor-recording", required);
    }

    private async Task CollectSampleAsync()
    {
        if (_writing || _writer is null)
            return;
        _writing = true;
        var recordingWriter = _writer;
        _timer.Stop();
        try
        {
            var snapshot = await _runtime.ReadOsdSnapshotAsync();
            var sample = BuildSample(
                snapshot,
                _runtime.CurrentFps,
                _runtime.Settings.SensorRecording.Sensors);
            if (!_runtime.Settings.SensorRecordingEnabled ||
                _runtime.IsSystemSessionEnding ||
                !ReferenceEquals(_writer, recordingWriter))
                return;
            _buffer.Add(sample);
            SampleWritten?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("A sensor recording sample could not be written.", ex);
        }
        finally
        {
            _writing = false;
            if (_runtime.Settings.SensorRecordingEnabled && _writer is not null &&
                !_runtime.IsSystemSessionEnding)
                _timer.Start();
        }
    }

    private void FlushBufferedSamples()
    {
        if (_writer is null)
            return;
        try
        {
            if (_buffer.Count > 0)
            {
                _writer.WriteLine(SensorRecordingFormat.Batch(
                    _buffer,
                    _metricKeys));
            }
            _writer.Flush();
            _buffer.Clear();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Buffered sensor data could not be flushed.", ex);
        }
    }

    private static IReadOnlyList<string> KeysForSelectedSensors(
        IReadOnlyCollection<OsdSensor> selected) =>
        SensorRecordingFormat.OrderKeys(BuildSample(
            ToolkitRuntimeSnapshot.Empty,
            FpsTelemetrySnapshot.Empty,
            selected).Values.Keys);

    internal static SensorRecordingSample BuildSample(
        ToolkitRuntimeSnapshot snapshot,
        FpsTelemetrySnapshot fps,
        IReadOnlyCollection<OsdSensor> selected)
    {
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var temperatures = snapshot.Temperatures;
        void Add(string name, double? value) => values[name] = Rounded(value);
        foreach (var sensor in selected.Distinct())
        {
            switch (sensor)
            {
                case OsdSensor.Fps: Add("fps", fps.IsFresh ? fps.Fps : null); break;
                case OsdSensor.OnePercentLowFps: Add("fps1Low", fps.IsFresh ? fps.OnePercentLowFps : null); break;
                case OsdSensor.FrameLatency: Add("latencyMs", fps.IsFresh ? fps.FrameTimeMs : null); break;
                case OsdSensor.CpuUtilization: Add("cpuUtilization", temperatures?.CpuLoadPercent); break;
                case OsdSensor.CpuAverageFrequency: Add("cpuAverageMhz", temperatures?.CpuAverageClockMhz); break;
                case OsdSensor.CpuPerformanceCoreAverageFrequency: Add("cpuPerformanceCoreAverageMhz", temperatures?.CpuPerformanceCoreAverageClockMhz); break;
                case OsdSensor.CpuEfficiencyCoreAverageFrequency: Add("cpuEfficiencyCoreAverageMhz", temperatures?.CpuEfficiencyCoreAverageClockMhz); break;
                case OsdSensor.CpuMaximumFrequency: Add("cpuMaximumMhz", temperatures?.CpuMaximumClockMhz); break;
                case OsdSensor.CpuTemperature: Add("cpuTemperatureC", temperatures?.CpuTempC); break;
                case OsdSensor.CpuPower: Add("cpuPowerW", temperatures?.CpuPowerW); break;
                case OsdSensor.GpuUtilization: Add("gpuUtilization", temperatures?.GpuLoadPercent); break;
                case OsdSensor.GpuVramUtilization: Add("vramUtilization", temperatures?.GpuMemoryLoadPercent); break;
                case OsdSensor.GpuCoreFrequency: Add("gpuCoreMhz", temperatures?.GpuCoreClockMhz); break;
                case OsdSensor.GpuVramFrequency: Add("vramMhz", temperatures?.GpuMemoryClockMhz); break;
                case OsdSensor.GpuCoreTemperature: Add("gpuTemperatureC", temperatures?.GpuTempC); break;
                case OsdSensor.GpuHotSpotTemperature: Add("gpuHotSpotTemperatureC", temperatures?.GpuHotSpotTempC); break;
                case OsdSensor.GpuVramTemperature: Add("vramTemperatureC", MaxOrFallback(temperatures?.VramChipTemperaturesC, temperatures?.VramTempC)); break;
                case OsdSensor.GpuPower: Add("gpuPowerW", temperatures?.GpuPowerW); break;
                case OsdSensor.MemoryUtilization:
                    Add("ramUsedGb", temperatures?.PhysicalMemoryUsedGb);
                    Add("ramUtilization", Percentage(temperatures?.PhysicalMemoryUsedGb, temperatures?.PhysicalMemoryTotalGb));
                    break;
                case OsdSensor.MemoryCommitted:
                    Add("committedUsedGb", temperatures?.VirtualMemoryUsedGb);
                    Add("committedUtilization", Percentage(temperatures?.VirtualMemoryUsedGb, temperatures?.VirtualMemoryTotalGb));
                    break;
                case OsdSensor.MemorySlot1Temperature: Add("memorySlot1TemperatureC", MemorySlot(temperatures, 0)); break;
                case OsdSensor.MemorySlot2Temperature: Add("memorySlot2TemperatureC", MemorySlot(temperatures, 1)); break;
                case OsdSensor.BatteryOutputPower: Add("batteryPowerW", snapshot.Battery?.ChargeDischargePowerW); break;
                case OsdSensor.BatteryCapacity: Add("batteryCapacityWh", snapshot.Battery?.CurrentCapacityWh); break;
                case OsdSensor.Fan1Speed: Add("fan1Rpm", snapshot.Fans?.Fan1Rpm); break;
                case OsdSensor.Fan2Speed: Add("fan2Rpm", DeviceModelDetector.HasSecondFan() ? snapshot.Fans?.Fan2Rpm : null); break;
                default:
                    var index = OsdSensorCatalog.StorageIndex(sensor);
                    if (index >= 0)
                        Add($"disk{index + 1}TemperatureC", StorageMaximum(temperatures, index));
                    break;
            }
        }
        return new SensorRecordingSample(DateTimeOffset.Now, values);
    }

    private static double? Percentage(double? used, double? total) =>
        used.HasValue && total is > 0 ? used.Value * 100 / total.Value : null;

    private static double? MemorySlot(TemperatureSnapshot? value, int slot)
    {
        if (value?.MemorySlotTemperaturesC is not { Count: > 0 } readings)
            return null;
        var index = readings.Count > 6 ? slot * 6 : slot;
        return index < readings.Count ? readings[index] : null;
    }

    private static double? StorageMaximum(TemperatureSnapshot? value, int index) =>
        value?.StorageDevices is { } devices && index < devices.Count &&
        devices[index].TemperaturesC.Count > 0
            ? devices[index].TemperaturesC.Max()
            : null;

    private static double? MaxOrFallback(
        IReadOnlyList<double>? values,
        double? fallback) => values is { Count: > 0 }
        ? values.Max()
        : fallback;

    private static double? Rounded(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero)
            : null;

    public void Dispose() => Stop();
}
