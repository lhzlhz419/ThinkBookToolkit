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
    public double? CpuPerformanceCoreAverageClockMhz { get; init; }
    public double? CpuEfficiencyCoreAverageClockMhz { get; init; }
    public double? CpuMaximumClockMhz { get; init; }
    public string GpuName { get; init; } = string.Empty;
    public double? GpuLoadPercent { get; init; }
    public double? GpuMemoryLoadPercent { get; init; }
    public double? GpuCoreClockMhz { get; init; }
    public double? GpuMemoryClockMhz { get; init; }
    public double? GpuHotSpotTempC { get; init; }
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
    Atpp,
    NvPcfAcTargetTppLimit,
    NvPcfAcDefaultGpuLimit,
    NvPcfAcMinGpuLimit,
    NvPcfAcMaxGpuLimit,
    NvPcfDynamicBoost,
    NvApiGpuTemperatureLimit
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
    public bool NvPcfAcTargetTppLimit { get; set; }
    public bool NvPcfAcDefaultGpuLimit { get; set; }
    public bool NvPcfAcMinGpuLimit { get; set; }
    public bool NvPcfAcMaxGpuLimit { get; set; }
    public bool NvPcfDynamicBoost { get; set; }
    public bool NvApiGpuTemperatureLimit { get; set; }

    [JsonIgnore]
    public bool Any =>
        CpuPl1 || CpuPl2 || CpuTemperatureLimit || CpuTurboTimeLimit ||
        GpuPowerBoost || GpuConfigurableTgp || GpuTemperatureLimit ||
        GpuToCpuDynamicBoost || Atpp || NvPcfAcTargetTppLimit ||
        NvPcfAcDefaultGpuLimit || NvPcfAcMinGpuLimit ||
        NvPcfAcMaxGpuLimit || NvPcfDynamicBoost ||
        NvApiGpuTemperatureLimit;

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
        PowerSetting.NvPcfAcTargetTppLimit => NvPcfAcTargetTppLimit,
        PowerSetting.NvPcfAcDefaultGpuLimit => NvPcfAcDefaultGpuLimit,
        PowerSetting.NvPcfAcMinGpuLimit => NvPcfAcMinGpuLimit,
        PowerSetting.NvPcfAcMaxGpuLimit => NvPcfAcMaxGpuLimit,
        PowerSetting.NvPcfDynamicBoost => NvPcfDynamicBoost,
        PowerSetting.NvApiGpuTemperatureLimit => NvApiGpuTemperatureLimit,
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
            case PowerSetting.NvPcfAcTargetTppLimit: copy.NvPcfAcTargetTppLimit = value; break;
            case PowerSetting.NvPcfAcDefaultGpuLimit: copy.NvPcfAcDefaultGpuLimit = value; break;
            case PowerSetting.NvPcfAcMinGpuLimit: copy.NvPcfAcMinGpuLimit = value; break;
            case PowerSetting.NvPcfAcMaxGpuLimit: copy.NvPcfAcMaxGpuLimit = value; break;
            case PowerSetting.NvPcfDynamicBoost: copy.NvPcfDynamicBoost = value; break;
            case PowerSetting.NvApiGpuTemperatureLimit: copy.NvApiGpuTemperatureLimit = value; break;
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

    public static PowerSettingsLockSelection AllNvPcf() => new()
    {
        NvPcfAcTargetTppLimit = true,
        NvPcfAcDefaultGpuLimit = true,
        NvPcfAcMinGpuLimit = true,
        NvPcfAcMaxGpuLimit = true
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
    public string BackgroundImagePath { get; set; } = "";
    public double BackgroundImageScalePercent { get; set; } = 100;
    public double BackgroundImageOpacityPercent { get; set; } = 30;
    public int BackgroundImageBlurRadius { get; set; }
    public BackgroundImageSizeMode BackgroundImageSizeMode { get; set; } =
        BackgroundImageSizeMode.Fixed;
    public bool BackgroundImageInverted { get; set; }
    public bool BackgroundBaseColorEnabled { get; set; }
    public string BackgroundBaseColor { get; set; } = "FFFFFF";
    public int BackgroundMediaSpeedPercent { get; set; } = 100;
    public HardwareAccelerationMode HardwareAccelerationMode { get; set; } =
        HardwareAccelerationMode.Disabled;
    public bool OsdEnabled { get; set; }
    public ToolkitOsdSettings Osd { get; set; } = new();
    public bool HybridCoreDisplayDefaultsInitialized { get; set; }
    public bool BatteryCapacityDisplayDefaultsInitialized { get; set; } = true;
    public bool SensorRecordingEnabled { get; set; }
    public SensorRecordingSettings SensorRecording { get; set; } = new();
    public string LastSensorRecordingPath { get; set; } = string.Empty;
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
    public List<string> IncludedGamePaths { get; set; } = [];
    public List<string> ExcludedGamePaths { get; set; } = [];
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
    public bool IncludeDynamicRefreshRateInCycle { get; set; }
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
    public bool AlternativeFullSpeedMethodInitialized { get; set; }
    public bool ContinuouslyWriteFanTargets { get; set; }
    public bool UseNvApiGpuPower { get; set; }
    public bool UseIntelMmioCpuPower { get; set; }
    public bool UseAmdZenStatesCpuPower { get; set; }
    public bool ShareDataWithOtherSoftware { get; set; }
    public SoftwareIntegrationMode SoftwareIntegrationMode { get; set; }
    public int DataSharingPort { get; set; } = 2975;
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
    public Dictionary<string, PowerModeLockSettings> NvApiPowerSettingsLocksByMode
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GpuOverclockSettings GpuOverclock { get; set; } = new();
    public bool AutoEnableGpuOverclockOnStartup { get; set; }
    public List<AutomationDefinition> Automations { get; set; } = [];
    public bool AutomationEnabled { get; set; }
    public List<KeyboardMacroDefinition> Macros { get; set; } = [];
    public bool MacroEnabled { get; set; }
    public Dictionary<string, string> FnKeyAutomationBindings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FnKeyDoublePressAutomationBindings
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomFnKeyNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string AcceptedDisclaimerVersion { get; set; } = "";
    public string LastFanBackendIdentity { get; set; } = "";
    public string SuppressedFanBackendStartupNoticeIdentity { get; set; } = "";
    public string PendingGpuMode { get; set; } = "";
    public string PendingGpuModeSource { get; set; } = "";
    public bool? PendingGpuModeSourceUsesDirectGraphicsConfiguration { get; set; }
    public string PendingGpuModeBootSessionId { get; set; } = "";
    public string PendingGpuModeProtocol { get; set; } = "";
    public bool PendingGpuModeParentStaged { get; set; }
    public bool PendingGpuModeChildStaged { get; set; }
    public int PendingGpuModePostBootAttempts { get; set; }
    public string PendingGpuModeLastError { get; set; } = "";
    public string LastGpuModeFailure { get; set; } = "";
    public int PcManagerNormalDefaultTemperature { get; set; } = 6600;
    public int PcManagerEyeCareDefaultTemperature { get; set; } = 3500;
}

public enum BackgroundImageSizeMode
{
    Fixed,
    MatchLength,
    MatchWidth,
    Stretch
}

public enum HardwareAccelerationMode
{
    Disabled,
    Automatic,
    PowerSaving,
    HighPerformance
}

public enum SoftwareIntegrationMode
{
    Disabled,
    ShareDataOnly,
    ShareDataAndControl
}

public enum OsdOrientation
{
    Horizontal,
    Vertical
}

public enum OsdSensor
{
    CpuUtilization = 0,
    CpuAverageFrequency = 1,
    CpuMaximumFrequency = 2,
    CpuTemperature = 3,
    CpuPower = 4,
    GpuUtilization = 5,
    GpuVramUtilization = 6,
    GpuCoreFrequency = 7,
    GpuVramFrequency = 8,
    GpuCoreTemperature = 9,
    GpuHotSpotTemperature = 10,
    GpuVramTemperature = 11,
    GpuPower = 12,
    MemoryUtilization = 13,

    // Values 14-16 were aggregate rows in v1.0.2 and earlier. Keep them
    // reserved so numeric JSON settings can be migrated without being
    // interpreted as a different sensor.
    MemoryTemperature = 14,
    StorageTemperatures = 15,
    FanSpeeds = 16,

    MemoryCommitted = 17,
    MemorySlot1Temperature = 18,
    MemorySlot2Temperature = 19,
    Storage1Temperature = 20,
    Storage2Temperature = 21,
    Storage3Temperature = 22,
    Storage4Temperature = 23,
    Storage5Temperature = 24,
    Storage6Temperature = 25,
    Storage7Temperature = 26,
    Storage8Temperature = 27,
    Fan1Speed = 28,
    Fan2Speed = 29,
    Fps = 30,
    OnePercentLowFps = 31,
    FrameLatency = 32,
    BatteryOutputPower = 33,
    CpuPerformanceCoreAverageFrequency = 34,
    CpuEfficiencyCoreAverageFrequency = 35,
    BatteryCapacity = 36
}

public enum OsdMultipleTemperatureMode
{
    Average,
    Maximum,
    All
}

public enum OsdLowFpsThresholdMode
{
    PercentageOfFps,
    DifferenceFromFps
}

public enum OsdMemoryDisplayMode
{
    Values,
    Percentage,
    All
}

public sealed class ToolkitOsdSettings
{
    public OsdOrientation Orientation { get; set; } = OsdOrientation.Vertical;
    public double RefreshIntervalSeconds { get; set; } = 1;
    public bool FixedPosition { get; set; }
    public int OpacityPercent { get; set; } = 50;
    public int FontSize { get; set; } = 13;
    public int SnapThreshold { get; set; } = 20;
    public OsdMultipleTemperatureMode MultipleTemperatureMode { get; set; } =
        OsdMultipleTemperatureMode.All;
    public OsdMemoryDisplayMode MemoryDisplayMode { get; set; } =
        OsdMemoryDisplayMode.All;
    public string BackgroundColor { get; set; } = "0E131D";
    public string CategoryColor { get; set; } = "7C9CFF";
    public string LabelColor { get; set; } = "B6C2D8";
    public string ValueColor { get; set; } = "FFFFFF";
    public string WarningColor { get; set; } = "FFFF00";
    public string CriticalColor { get; set; } = "FF0000";
    public int FpsWarningThreshold { get; set; } = 45;
    public int FpsCriticalThreshold { get; set; } = 30;
    public OsdLowFpsThresholdMode LowFpsThresholdMode { get; set; } =
        OsdLowFpsThresholdMode.PercentageOfFps;
    public int LowFpsWarningPercentage { get; set; } = 75;
    public int LowFpsCriticalPercentage { get; set; } = 50;
    public int LowFpsWarningDelta { get; set; } = 15;
    public int LowFpsCriticalDelta { get; set; } = 30;
    public int CpuTemperatureWarning { get; set; } = 80;
    public int CpuTemperatureCritical { get; set; } = 95;
    public int GpuHotSpotTemperatureWarning { get; set; } = 80;
    public int GpuHotSpotTemperatureCritical { get; set; } = 95;
    public int GpuTemperatureWarning { get; set; } = 75;
    public int GpuTemperatureCritical { get; set; } = 85;
    public int VramTemperatureWarning { get; set; } = 75;
    public int VramTemperatureCritical { get; set; } = 85;
    public int MemoryTemperatureWarning { get; set; } = 65;
    public int MemoryTemperatureCritical { get; set; } = 75;
    public int StorageTemperatureWarning { get; set; } = 60;
    public int StorageTemperatureCritical { get; set; } = 90;
    public int UsageWarningThreshold { get; set; } = 70;
    public int UsageCriticalThreshold { get; set; } = 90;
    public int BatteryOutputPowerWarning { get; set; } = -1;
    public int BatteryOutputPowerCritical { get; set; } = -30;
    public List<OsdSensor> Sensors { get; set; } =
    [
        OsdSensor.CpuUtilization,
        OsdSensor.CpuAverageFrequency,
        OsdSensor.CpuMaximumFrequency,
        OsdSensor.CpuTemperature,
        OsdSensor.CpuPower,
        OsdSensor.GpuUtilization,
        OsdSensor.GpuCoreFrequency,
        OsdSensor.GpuCoreTemperature,
        OsdSensor.GpuHotSpotTemperature,
        OsdSensor.GpuPower,
        OsdSensor.GpuVramUtilization,
        OsdSensor.GpuVramFrequency,
        OsdSensor.GpuVramTemperature,
        OsdSensor.MemoryUtilization,
        OsdSensor.MemoryCommitted,
        OsdSensor.MemorySlot1Temperature,
        OsdSensor.MemorySlot2Temperature,
        OsdSensor.Storage1Temperature,
        OsdSensor.Storage2Temperature,
        OsdSensor.Storage3Temperature,
        OsdSensor.Storage4Temperature,
        OsdSensor.Storage5Temperature,
        OsdSensor.Storage6Temperature,
        OsdSensor.Storage7Temperature,
        OsdSensor.Storage8Temperature,
        OsdSensor.Fan1Speed,
        OsdSensor.Fan2Speed,
        OsdSensor.Fps,
        OsdSensor.OnePercentLowFps,
        OsdSensor.FrameLatency,
        OsdSensor.BatteryOutputPower,
        OsdSensor.BatteryCapacity
    ];
    public double? HorizontalX { get; set; }
    public double? HorizontalY { get; set; }
    public double? VerticalX { get; set; }
    public double? VerticalY { get; set; }
}

public sealed class SensorRecordingSettings
{
    public double IntervalSeconds { get; set; } = 1;
    public int MaximumPlotPoints { get; set; } = 300;
    public List<OsdSensor> Sensors { get; set; } =
        new ToolkitOsdSettings().Sensors
            .Where(sensor => sensor != OsdSensor.BatteryOutputPower)
            .ToList();
}

internal enum StartupLaunchMode
{
    Disabled,
    Enabled,
    Delayed
}
