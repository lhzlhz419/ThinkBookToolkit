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
    public string MemoryUtilization => MemoryUtilizationValue(
        _snapshot.Temperatures?.PhysicalMemoryUsedGb,
        _snapshot.Temperatures?.PhysicalMemoryTotalGb);
    public string MemoryAverageTemperature => AverageTemperature(
        _snapshot.Temperatures?.MemorySlotTemperaturesC);
    public IReadOnlyList<HardwareMonitorMetric> StorageMetrics =>
        BuildStorageMetrics(_snapshot.Temperatures);
    public IReadOnlyList<HardwareMonitorMetric> StorageTemperatureMetrics =>
        BuildStorageMetrics(_snapshot.Temperatures, includeHealth: false);
    public IReadOnlyList<HardwareMonitorMetric> StorageHealthMetrics =>
        BuildStorageMetrics(_snapshot.Temperatures, includeTemperatures: false);

    public string Fan1Speed => FanSpeed(_snapshot.Fans?.Fan1Rpm);
    public string Fan2Speed => FanSpeed(_snapshot.Fans?.Fan2Rpm);
    public string Fan1Target => FanTarget(1);
    public string Fan2Target => FanTarget(2);

    public string CompactCpu => CompactValues(
        (Show(OverviewCardIds.Cpu, "temperature"), CpuTemperature),
        (Show(OverviewCardIds.Cpu, "power"), CpuPower));
    public string CompactGpu => CompactValues(
        (Show(OverviewCardIds.Gpu, "core-temperature"), GpuCoreTemperature),
        (Show(OverviewCardIds.Gpu, "power"), GpuPower));
    public string CompactBattery => CompactValues(
        (Show(OverviewCardIds.Battery, "charge"), BatteryCharge),
        (Show(OverviewCardIds.Battery, "health"), BatteryHealth),
        (Show(OverviewCardIds.Battery, "power"), BatteryPower));
    public string CompactMemory => CompactValues(
        (Show(OverviewCardIds.MemoryStorage, "utilization"), MemoryUtilization),
        (Show(OverviewCardIds.MemoryStorage, "average-temperature"), MemoryAverageTemperature));
    public string CompactFans
    {
        get
        {
            var first = Show(OverviewCardIds.Fans, "fan1-speed")
                ? _snapshot.Fans?.Fan1Rpm
                : null;
            var second = Show(OverviewCardIds.Fans, "fan2-speed")
                ? _snapshot.Fans?.Fan2Rpm
                : null;
            return RpmPair(first, second);
        }
    }
    public string CompactFanTargets
    {
        get
        {
            var firstVisible = Show(OverviewCardIds.Fans, "fan1-target");
            var secondVisible = Show(OverviewCardIds.Fans, "fan2-target");
            if (firstVisible && secondVisible)
                return $"{WithoutRpmSuffix(Fan1Target)} / {Fan2Target}";
            if (firstVisible)
                return Fan1Target;
            if (secondVisible)
                return Fan2Target;
            return "--";
        }
    }
    public string CompactVramAndHotSpot => CompactValues(
        [
            (Show(OverviewCardIds.Gpu, "vram-temperature"),
                Temperature(_snapshot.Temperatures?.VramTempC)),
            (Show(OverviewCardIds.Gpu, "hotspot-temperature"),
                GpuHotSpotTemperature)
        ],
        " / ");

    public string PowerCpuPl1 => PowerValue(PowerSetting.CpuPl1, value => value.CpuPl1);
    public string PowerCpuPl2 => PowerValue(PowerSetting.CpuPl2, value => value.CpuPl2);
    public string PowerCpuTemperature => PowerValue(PowerSetting.CpuTemperatureLimit, value => value.CpuTemperatureLimit, "°C");
    public string PowerTurboTime => PowerValue(PowerSetting.CpuTurboTimeLimit, value => value.CpuTurboTimeLimit, "s");
    public string PowerGpuBoost => PowerValue(PowerSetting.GpuPowerBoost, value => value.GpuPowerBoost);
    public string PowerGpuTgp => PowerValue(PowerSetting.GpuConfigurableTgp, value => value.GpuConfigurableTgp);
    public string GpuPowerLimit => Power(_snapshot.Temperatures?.GpuPowerLimitW);
    public string PowerGpuTemperature => PowerValue(PowerSetting.GpuTemperatureLimit, value => value.GpuTemperatureLimit, "°C");
    public string PowerGpuToCpu => PowerValue(PowerSetting.GpuToCpuDynamicBoost, value => value.GpuToCpuDynamicBoost);
    public string PowerAtpp => _snapshot.PowerSettings is { } power &&
                               power.IsAvailable(PowerSetting.Atpp) &&
                               power.Atpp.HasValue
        ? $"{power.Atpp.Value} W"
        : "--";

    public bool PowerCpuPl1Visible => PowerVisible(PowerSetting.CpuPl1, "cpu-pl1");
    public bool PowerCpuPl2Visible => PowerVisible(PowerSetting.CpuPl2, "cpu-pl2");
    public bool PowerCpuTemperatureVisible => PowerVisible(PowerSetting.CpuTemperatureLimit, "cpu-temperature");
    public bool PowerTurboTimeVisible => PowerVisible(PowerSetting.CpuTurboTimeLimit, "turbo-time");
    public bool PowerGpuBoostVisible => PowerVisible(PowerSetting.GpuPowerBoost, "gpu-boost");
    public bool PowerGpuTgpVisible => PowerVisible(PowerSetting.GpuConfigurableTgp, "gpu-tgp");
    public bool GpuPowerLimitVisible =>
        Show(OverviewCardIds.Power, "gpu-power-limit") &&
        _snapshot.Temperatures?.GpuPowerLimitW.HasValue == true;
    public bool PowerGpuTemperatureVisible => PowerVisible(PowerSetting.GpuTemperatureLimit, "gpu-temperature");
    public bool PowerGpuToCpuVisible => PowerVisible(PowerSetting.GpuToCpuDynamicBoost, "gpu-to-cpu");
    public bool PowerAtppVisible => PowerVisible(PowerSetting.Atpp, "atpp") &&
                                    _snapshot.PowerSettings?.Atpp.HasValue == true;

    public string WarrantyStatus => _snapshot.Warranty?.State switch
    {
        WarrantyState.InWarranty => Runtime.L("在保", "Covered"),
        WarrantyState.Expired => Runtime.L("已过保", "Expired"),
        WarrantyState.NotStarted => Runtime.L("尚未开始", "Not started"),
        _ => Runtime.L("暂无信息", "Unavailable")
    };
    public string WarrantyStart => WarrantyDate(_snapshot.Warranty?.StartDate);
    public string WarrantyEnd => WarrantyDate(_snapshot.Warranty?.EndDate);
    public string WarrantyRemainingDays => _snapshot.Warranty is
        {
            State: WarrantyState.InWarranty,
            EndDate: { } endDate
        }
            ? $"{Math.Max(0, endDate.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber)} {Runtime.L("天", "days")}"
            : "--";
    public bool WarrantyRemainingDaysVisible =>
        Show(OverviewCardIds.Warranty, "remaining-days") &&
        _snapshot.Warranty is
        {
            State: WarrantyState.InWarranty,
            EndDate: not null
        };
    public string WarrantyProgress => _snapshot.Warranty is { State: not WarrantyState.Unavailable } warranty
        ? $"{warranty.ProgressPercentage}%"
        : "--";
    public string CompactWarranty
    {
        get
        {
            var statusVisible = Show(OverviewCardIds.Warranty, "status");
            var remainingVisible = Show(OverviewCardIds.Warranty, "remaining-days") &&
                                   _snapshot.Warranty?.State == WarrantyState.InWarranty;
            var status = _snapshot.Warranty?.State switch
            {
                WarrantyState.InWarranty => Runtime.L("在保", "Covered"),
                WarrantyState.Expired => Runtime.L("过保", "Expired"),
                WarrantyState.NotStarted => Runtime.L("尚未开始", "Not started"),
                _ => Runtime.L("暂无信息", "Unavailable")
            };
            return CompactValues(
                (statusVisible, status),
                (remainingVisible, WarrantyRemainingDays));
        }
    }

    public virtual void Update(ToolkitRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        Notify(string.Empty);
    }

    private bool Show(string cardId, string itemId) =>
        OverviewLayoutDefaults.IsItemEnabled(
            Runtime.Settings.OverviewLayout,
            cardId,
            itemId);

    private static string CompactValues(
        params (bool Visible, string Value)[] values) =>
        CompactValues(values, " · ");

    private static string CompactValues(
        IEnumerable<(bool Visible, string Value)> values,
        string separator)
    {
        var visible = values
            .Where(value => value.Visible)
            .Select(value => value.Value)
            .ToArray();
        return visible.Length == 0
            ? "--"
            : string.Join(separator, visible);
    }

    private static string RpmPair(int? first, int? second)
    {
        if (!first.HasValue && !second.HasValue)
            return "--";
        if (first.HasValue && second.HasValue)
            return $"{first.Value} / {second.Value} RPM";
        return $"{first ?? second} RPM";
    }

    private static string WithoutRpmSuffix(string value) =>
        value.EndsWith(" RPM", StringComparison.Ordinal)
            ? value[..^4]
            : value;

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

    private string PowerValue(
        PowerSetting setting,
        Func<PowerSettingsState, int> value,
        string unit = "W") =>
        _snapshot.PowerSettings is { } power && power.IsAvailable(setting)
            ? $"{value(power)} {unit}"
            : "--";

    private bool PowerVisible(PowerSetting setting, string itemId) =>
        OverviewLayoutDefaults.IsItemEnabled(
            Runtime.Settings.OverviewLayout,
            OverviewCardIds.Power,
            itemId) &&
        _snapshot.PowerSettings?.IsAvailable(setting) == true;

    private static string WarrantyDate(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "--";

    private string MemorySlotTemperature(int index)
    {
        var values = _snapshot.Temperatures?.MemorySlotTemperaturesC;
        return values is not null && index < values.Count
            ? Temperature(values[index])
            : "-";
    }

    private static string MemoryUtilizationValue(double? used, double? total) =>
        used.HasValue && total is > 0
            ? $"{Math.Clamp(used.Value * 100 / total.Value, 0, 100):0.0}%"
            : "--";

    private static string AverageTemperature(IReadOnlyList<double>? values) =>
        values is { Count: > 0 }
            ? $"{values.Average():0.0} °C"
            : "--";

    private IReadOnlyList<HardwareMonitorMetric> BuildStorageMetrics(
        TemperatureSnapshot? temperatures,
        bool includeTemperatures = true,
        bool includeHealth = true)
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

            if (includeTemperatures)
                result.Add(new HardwareMonitorMetric(
                    Runtime.IsChinese
                        ? $"硬盘{number} {device.Name}"
                        : $"Disk {number} {device.Name}",
                    temperature));
            if (includeHealth)
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
        value is > 0 and < 2000 ? $"{value.Value:0.0} W" : "--";

    private static string FanSpeed(int? value) =>
        value.HasValue ? $"{value.Value} RPM" : "--";

    private static string MemoryPair(double? used, double? total) =>
        used.HasValue && total.HasValue
            ? $"{used.Value:0.0} / {total.Value:0.0} GB"
            : "--";
}
