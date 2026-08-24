using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ThinkBookToolkit;

public sealed record FanLimit(string Fan, uint Id, int MinRpm, int MaxRpm);

public sealed record FanSnapshot(
    DateTimeOffset Timestamp,
    int Fan1Rpm,
    int Fan2Rpm,
    IReadOnlyDictionary<string, FanLimit> Limits);

public enum DiscreteGpuActivityState
{
    Unknown,
    Active,
    Inactive,
    Off
}

public sealed record TemperatureSnapshot(
    double? CpuTempC,
    double? GpuTempC,
    double? VramTempC,
    double? CpuPowerW,
    double? GpuPowerW,
    string CpuSensor,
    string GpuSensor,
    string VramSensor)
{
    public string CpuName { get; init; } = string.Empty;
    public double? CpuLoadPercent { get; init; }
    public double? CpuAverageClockMhz { get; init; }
    public double? CpuMaximumClockMhz { get; init; }
    public string GpuName { get; init; } = string.Empty;
    public double? GpuLoadPercent { get; init; }
    public double? GpuMemoryLoadPercent { get; init; }
    public double? GpuCoreClockMhz { get; init; }
    public double? GpuMemoryClockMhz { get; init; }
    public double? GpuHotSpotTempC { get; init; }
    public double? GpuPowerLimitW { get; init; }
    public IReadOnlyList<double> VramChipTemperaturesC { get; init; } = [];
    public DiscreteGpuActivityState DiscreteGpuState { get; init; } =
        DiscreteGpuActivityState.Unknown;
    public string GpuPerformanceState { get; init; } = string.Empty;

    public double? PhysicalMemoryUsedGb { get; init; }
    public double? PhysicalMemoryTotalGb { get; init; }
    public double? VirtualMemoryUsedGb { get; init; }
    public double? VirtualMemoryTotalGb { get; init; }
    public IReadOnlyList<double> MemorySlotTemperaturesC { get; init; } = [];
    public IReadOnlyList<StorageTemperatureSnapshot> StorageDevices { get; init; } = [];
}

public sealed record StorageTemperatureSnapshot(
    string Name,
    IReadOnlyList<double> TemperaturesC,
    double? LifePercent = null);

public sealed record FanTargets(int Fan1Rpm, int Fan2Rpm);

public enum ControlStrategy
{
    FixedRpm,
    FanCurve,
    AdvancedCurve
}

public enum FanControlMode
{
    FirmwareAutomatic,
    FixedRpm,
    FanCurve,
    AdvancedCurve
}

public enum ItsMode
{
    Unknown,
    PowerSaving,
    Intelligent,
    Performance,
    Geek
}

public enum OverviewPageMode
{
    Compact,
    Detailed
}

public sealed class FanStrategySelection
{
    public FanControlMode Mode { get; set; } =
        FanControlMode.FirmwareAutomatic;
    public int ProfileIndex { get; set; }
}

public sealed class PerformanceFanLinkSettings
{
    public bool SwitchFanStrategyWithPerformanceMode { get; set; }
    public Dictionary<string, FanStrategySelection> FanStrategiesByMode
        { get; set; } = PerformanceFanLinkDefaults.CreateFanStrategies();
    public ItsMode FanControlTargetMode { get; set; } = ItsMode.Unknown;
    public Dictionary<string, bool> NoSwitchModes { get; set; } =
        PerformanceFanLinkDefaults.CreateNoSwitchModes();
}

public enum FixedGameModeOverride
{
    None,
    NormalUntilGameStarts,
    GameUntilGamesEnd
}

public sealed class FanRpmLimits
{
    public int Fan1MinimumRpm { get; set; } = 1500;
    public int Fan1MaximumRpm { get; set; } = 5500;
    public int Fan2MinimumRpm { get; set; } = 1500;
    public int Fan2MaximumRpm { get; set; } = 5500;
}

public sealed class FixedRpmSettings
{
    public int PowerSavingNormalFan1Rpm { get; set; } = 1500;
    public int PowerSavingNormalFan2Rpm { get; set; } = 1500;
    public int PowerSavingGameFan1Rpm { get; set; } = 2500;
    public int PowerSavingGameFan2Rpm { get; set; } = 2500;
    public int IntelligentNormalFan1Rpm { get; set; } = 1800;
    public int IntelligentNormalFan2Rpm { get; set; } = 1800;
    public int IntelligentGameFan1Rpm { get; set; } = 3000;
    public int IntelligentGameFan2Rpm { get; set; } = 3000;
    public int PerformanceNormalFan1Rpm { get; set; } = 2200;
    public int PerformanceNormalFan2Rpm { get; set; } = 2200;
    public int PerformanceGameFan1Rpm { get; set; } = 3600;
    public int PerformanceGameFan2Rpm { get; set; } = 3600;
    public int GeekNormalFan1Rpm { get; set; } = 2600;
    public int GeekNormalFan2Rpm { get; set; } = 2600;
    public int GeekGameFan1Rpm { get; set; } = 4200;
    public int GeekGameFan2Rpm { get; set; } = 4200;
}

public sealed class FanProfile
{
    public string Name { get; set; } = "";
    public double TemperatureSmoothing { get; set; } = 3;
    public double RampUpRpmPerSecond { get; set; }
    public double FullRangeRampDownRpmPerSecond { get; set; }
    public double RampDownRpmPerSecond { get; set; } = 20;
    public List<int> CpuFan1Curve { get; set; } = [];
    public List<int> CpuFan2Curve { get; set; } = [];
    public List<int> GpuFan1Curve { get; set; } = [];
    public List<int> GpuFan2Curve { get; set; } = [];
    public List<int> CpuCurve { get; set; } = [];
    public List<int> GpuCurve { get; set; } = [];
}

public sealed class AdvancedFanCurvePoint
{
    public int Fan1Rpm { get; set; }
    public int Fan2Rpm { get; set; }
    public int? CpuRampUpTemperatureC { get; set; }
    public int? CpuRampDownTemperatureC { get; set; }
    public int? GpuRampUpTemperatureC { get; set; }
    public int? GpuRampDownTemperatureC { get; set; }
    public double RampUpRpmPerSecond { get; set; }
    public double RampDownRpmPerSecond { get; set; }
}

public sealed class AdvancedFanCurveSettings
{
    public double TemperatureSmoothing { get; set; } = 3;
    public List<AdvancedFanCurvePoint> Points { get; set; } =
        AdvancedFanCurve.CreateDefaultPoints();
}

public enum PowerSetting
{
    CpuPl1,
    CpuPl2,
    CpuTemperatureLimit,
    CpuTurboTimeLimit,
    GpuPowerBoost,
    GpuConfigurableTgp,
    GpuTemperatureLimit,
    GpuToCpuDynamicBoost,
    Atpp
}

public sealed record PowerSettingsLockSelection
{
    public bool CpuPl1 { get; set; }
    public bool CpuPl2 { get; set; }
    public bool CpuTemperatureLimit { get; set; }
    public bool CpuTurboTimeLimit { get; set; }
    public bool GpuPowerBoost { get; set; }
    public bool GpuConfigurableTgp { get; set; }
    public bool GpuTemperatureLimit { get; set; }
    public bool GpuToCpuDynamicBoost { get; set; }
    public bool Atpp { get; set; }

    [JsonIgnore]
    public bool Any =>
        CpuPl1 || CpuPl2 || CpuTemperatureLimit || CpuTurboTimeLimit ||
        GpuPowerBoost || GpuConfigurableTgp || GpuTemperatureLimit ||
        GpuToCpuDynamicBoost || Atpp;

    public bool IsLocked(PowerSetting setting) => setting switch
    {
        PowerSetting.CpuPl1 => CpuPl1,
        PowerSetting.CpuPl2 => CpuPl2,
        PowerSetting.CpuTemperatureLimit => CpuTemperatureLimit,
        PowerSetting.CpuTurboTimeLimit => CpuTurboTimeLimit,
        PowerSetting.GpuPowerBoost => GpuPowerBoost,
        PowerSetting.GpuConfigurableTgp => GpuConfigurableTgp,
        PowerSetting.GpuTemperatureLimit => GpuTemperatureLimit,
        PowerSetting.GpuToCpuDynamicBoost => GpuToCpuDynamicBoost,
        PowerSetting.Atpp => Atpp,
        _ => false
    };

    public PowerSettingsLockSelection With(PowerSetting setting, bool value)
    {
        var copy = this with { };
        switch (setting)
        {
            case PowerSetting.CpuPl1: copy.CpuPl1 = value; break;
            case PowerSetting.CpuPl2: copy.CpuPl2 = value; break;
            case PowerSetting.CpuTemperatureLimit: copy.CpuTemperatureLimit = value; break;
            case PowerSetting.CpuTurboTimeLimit: copy.CpuTurboTimeLimit = value; break;
            case PowerSetting.GpuPowerBoost: copy.GpuPowerBoost = value; break;
            case PowerSetting.GpuConfigurableTgp: copy.GpuConfigurableTgp = value; break;
            case PowerSetting.GpuTemperatureLimit: copy.GpuTemperatureLimit = value; break;
            case PowerSetting.GpuToCpuDynamicBoost: copy.GpuToCpuDynamicBoost = value; break;
            case PowerSetting.Atpp: copy.Atpp = value; break;
        }
        return copy;
    }

    public static PowerSettingsLockSelection All(bool includeAtpp) => new()
    {
        CpuPl1 = true,
        CpuPl2 = true,
        CpuTemperatureLimit = true,
        CpuTurboTimeLimit = true,
        GpuPowerBoost = true,
        GpuConfigurableTgp = true,
        GpuTemperatureLimit = true,
        GpuToCpuDynamicBoost = true,
        Atpp = includeAtpp
    };
}

public sealed class PowerModeLockSettings
{
    public PowerSettingsLockSelection Locks { get; set; } = new();
    public PowerSettingsState? Target { get; set; }
}

public sealed class AppSettings
{
    public string ConfigurationVersion { get; set; } = CurveProfileStore.CurrentConfigurationVersion;
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "light";
    public bool UseCustomLenovoDllDirectory { get; set; }
    public string CustomLenovoDllDirectory { get; set; } = "";
    public double IntervalSeconds { get; set; } = 2.0;
    public int LastProfileIndex { get; set; }
    public int EditFan { get; set; } = 1;
    public bool SyncFanSpeeds { get; set; } = true;
    public ControlStrategy ControlStrategy { get; set; } = ControlStrategy.FixedRpm;
    public bool FanCurveWarningAccepted { get; set; }
    public double GameExitHoldSeconds { get; set; } = 20;
    public bool ManualGameMode { get; set; }
    public FixedGameModeOverride FixedGameModeOverride { get; set; }
    public string FixedModeHotkey { get; set; } = "";
    public bool AutoDetectGames { get; set; } = true;
    public bool FixedSyncFanSpeeds { get; set; } = true;
    public bool FanRpmLimitsCustomized { get; set; }
    public FanRpmLimits FanRpmLimits { get; set; } = new();
    public FixedRpmSettings FixedRpm { get; set; } = new();
    public AdvancedFanCurveSettings AdvancedFanCurve { get; set; } = new();
    public bool ResumeFanControlOnNextStart { get; set; }
    public bool FanControlWasRunning { get; set; }
    public bool StartWithWindows { get; set; }
    public bool DelayStartup { get; set; }
    public bool StartToTray { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }
    public bool TakeOverFnKeys { get; set; }
    public bool ShowCapsLockOsd { get; set; } = true;
    public bool ShowNumLockOsd { get; set; } = true;
    public List<uint> RefreshRateCycleHz { get; set; } = [];
    public List<ItsMode> FnPerformanceModeOrder { get; set; } =
        PerformanceModeCycle.DefaultOrder.ToList();
    public List<ItsMode> FnPerformanceModeEnabled { get; set; } =
        PerformanceModeCycle.DefaultOrder.ToList();
    public string ShutdownPerformanceMode { get; set; } = "";
    public bool DisableControlOnSleep { get; set; } = true;
    public bool AttemptDisableControlOnSleepWhenUnsupported { get; set; }
    public double? FanReadMinimumIntervalSeconds { get; set; }
    public double? FanWriteMinimumIntervalSeconds { get; set; }
    public bool UseAlternativeFullSpeedMethod { get; set; }
    public bool ContinuouslyWriteFanTargets { get; set; }
    public OverviewPageMode OverviewPageMode { get; set; } =
        OverviewPageMode.Detailed;
    public OverviewLayoutSettings OverviewLayout { get; set; } =
        new();
    public PerformanceFanLinkSettings PerformanceFanLink { get; set; } =
        new();
    public PowerSettingsLockSelection PowerSettingsLocks { get; set; } = new();
    public int PowerSettingsLockIntervalSeconds { get; set; } = 2;
    public PowerSettingsState? PowerSettingsLockTarget { get; set; }
    public Dictionary<string, PowerModeLockSettings> PowerSettingsLocksByMode
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GpuOverclockSettings GpuOverclock { get; set; } = new();
    public string LastFanBackendIdentity { get; set; } = "";
    public string SuppressedFanBackendStartupNoticeIdentity { get; set; } = "";
    public string PendingGpuMode { get; set; } = "";
    public string PendingGpuModeSource { get; set; } = "";
    public bool? PendingGpuModeSourceUsesDirectGraphicsConfiguration { get; set; }
    public string PendingGpuModeBootSessionId { get; set; } = "";
    public int PcManagerNormalDefaultTemperature { get; set; } = 6600;
    public int PcManagerEyeCareDefaultTemperature { get; set; } = 3500;
}

internal enum StartupLaunchMode
{
    Disabled,
    Enabled,
    Delayed
}
