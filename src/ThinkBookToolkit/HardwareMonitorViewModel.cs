using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed record HardwareMonitorMetric(
    string Label,
    string Value);

internal class HardwareMonitorViewModel : ToolkitViewModelBase
{
    private ToolkitRuntimeSnapshot _snapshot;

    public HardwareMonitorViewModel(ToolkitRuntimeService runtime)
        : base(runtime)
    {
        _snapshot = runtime.Snapshot;
    }

    protected ToolkitRuntimeSnapshot Snapshot => _snapshot;

    public string CpuModel => Model(_snapshot.Temperatures?.CpuName);
    public string CpuUtilization => Percent(_snapshot.Temperatures?.CpuLoadPercent);
    public string CpuAverageFrequency => Frequency(_snapshot.Temperatures?.CpuAverageClockMhz);
    public string CpuMaximumFrequency => Frequency(_snapshot.Temperatures?.CpuMaximumClockMhz);
    public string CpuTemperature => Temperature(_snapshot.Temperatures?.CpuTempC);
    public string CpuPower => Power(_snapshot.Temperatures?.CpuPowerW);

    public string GpuModel => Model(_snapshot.Temperatures?.GpuName);
    public string GpuUtilization => Percent(_snapshot.Temperatures?.GpuLoadPercent);
    public string GpuMemoryUtilization => Percent(_snapshot.Temperatures?.GpuMemoryLoadPercent);
    public string GpuCoreFrequency => Frequency(_snapshot.Temperatures?.GpuCoreClockMhz);
    public string GpuMemoryFrequency => Frequency(_snapshot.Temperatures?.GpuMemoryClockMhz);
    public string GpuCoreTemperature => Temperature(_snapshot.Temperatures?.GpuTempC);
    public string GpuHotSpotTemperature => Temperature(_snapshot.Temperatures?.GpuHotSpotTempC);
    public string GpuMemoryTemperature => FormatGpuMemoryTemperatures(_snapshot.Temperatures);
    public string GpuPower => Power(_snapshot.Temperatures?.GpuPowerW);

    public string BatteryState => _snapshot.Battery is not { } battery
        ? "--"
        : battery.IsAcConnected
            ? Runtime.L("交流电", "AC power")
            : Runtime.L("放电中", "Discharging");
    public string BatteryCharge => _snapshot.Battery is { FullChargeCapacityWh: > 0 } battery
        ? $"{Math.Clamp(battery.CurrentCapacityWh * 100 / battery.FullChargeCapacityWh, 0, 100):0}%"
        : "--";
    public string BatteryHealth => _snapshot.Battery is { } battery
        ? $"{battery.HealthPercent:0.00}%"
        : "--";
    public string BatteryPower => _snapshot.Battery is { } battery
        ? $"{battery.ChargeDischargePowerW:+0.0;-0.0;0.0} W"
        : "--";
    public string BatteryTemperature => Temperature(_snapshot.Battery?.TemperatureC);

    public string PhysicalMemory => MemoryPair(
        _snapshot.Temperatures?.PhysicalMemoryUsedGb,
        _snapshot.Temperatures?.PhysicalMemoryTotalGb);
    public string VirtualMemory => MemoryPair(
        _snapshot.Temperatures?.VirtualMemoryUsedGb,
        _snapshot.Temperatures?.VirtualMemoryTotalGb);
    public string MemorySlot1Temperature => MemorySlotTemperature(0);
    public string MemorySlot2Temperature => MemorySlotTemperature(1);
    public IReadOnlyList<HardwareMonitorMetric> StorageMetrics =>
        BuildStorageMetrics(_snapshot.Temperatures);

    public string Fan1Speed => FanSpeed(_snapshot.Fans?.Fan1Rpm);
    public string Fan2Speed => FanSpeed(_snapshot.Fans?.Fan2Rpm);
    public string Fan1Target => FanTarget(1);
    public string Fan2Target => FanTarget(2);

    public virtual void Update(ToolkitRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        Notify(string.Empty);
    }

    private string FanTarget(int fan)
    {
        if (_snapshot.FullSpeed)
            return "MAX";
        if (!_snapshot.FanControlRunning)
            return Runtime.L("固件自动", "Firmware automatic");
        if (_snapshot.FanTarget is not { } target)
            return "--";
        var rpm = fan == 1 ? target.Fan1Rpm : target.Fan2Rpm;
        if (rpm == 0 &&
            Runtime.FanControlSemantics.ZeroRpmBehavior ==
            FanTargetZeroBehavior.ReleaseFanToFirmwareControl)
        {
            return Runtime.L("固件自动", "Firmware automatic");
        }
        return $"{rpm} RPM";
    }

    private string MemorySlotTemperature(int index)
    {
        var values = _snapshot.Temperatures?.MemorySlotTemperaturesC;
        return values is not null && index * 6 < values.Count
            ? Temperature(values[index * 6])
            : "-";
    }

    private IReadOnlyList<HardwareMonitorMetric> BuildStorageMetrics(
        TemperatureSnapshot? temperatures)
    {
        if (temperatures?.StorageDevices is not { Count: > 0 } storage)
            return [];

        var result = new List<HardwareMonitorMetric>(storage.Count * 2);
        for (var index = 0; index < storage.Count; index++)
        {
            var device = storage[index];
            var number = index + 1;
            // 保留硬盘原有的多个温度值，最多显示前三个。
            var temperature = device.TemperaturesC.Count > 0
                ? string.Join(
                    "/",
                    device.TemperaturesC
                        .Take(3)
                        .Select(value => value.ToString(
                            "0.0",
                            CultureInfo.InvariantCulture))) + " °C"
                : "--";
            var life = device.LifePercent.HasValue
                ? $"{device.LifePercent.Value:0.0}%"
                : "--";

            result.Add(new HardwareMonitorMetric(
                Runtime.IsChinese
                    ? $"硬盘{number} {device.Name}"
                    : $"Disk {number} {device.Name}",
                temperature));
            result.Add(new HardwareMonitorMetric(
                Runtime.IsChinese
                    ? $"硬盘{number}健康度"
                    : $"Disk {number} health",
                life));
        }
        return result;
    }

    private static string FormatGpuMemoryTemperatures(
        TemperatureSnapshot? temperatures)
    {
        if (temperatures?.VramChipTemperaturesC is { Count: > 0 } chips)
        {
            return string.Join("/", chips.Select(value =>
                value.ToString("0.#", CultureInfo.InvariantCulture))) + " °C";
        }
        return Temperature(temperatures?.VramTempC);
    }

    private static string Model(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "--" : value;

    private static string Percent(double? value) =>
        value.HasValue ? $"{value.Value:0.0}%" : "--";

    private static string Frequency(double? value) =>
        value.HasValue ? $"{value.Value:0} MHz" : "--";

    private static string Temperature(double? value) =>
        value.HasValue ? $"{value.Value:0.0} °C" : "--";

    private static string Power(double? value) =>
        value.HasValue ? $"{value.Value:0.0} W" : "--";

    private static string FanSpeed(int? value) =>
        value.HasValue ? $"{value.Value} RPM" : "--";

    private static string MemoryPair(double? used, double? total) =>
        used.HasValue && total.HasValue
            ? $"{used.Value:0.0} / {total.Value:0.0} GB"
            : "--";
}
