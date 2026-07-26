using System;
using System.Collections.Generic;

namespace ThinkBookToolkit;

public sealed record FanLimit(string Fan, uint Id, int MinRpm, int MaxRpm);

public sealed record FanSnapshot(
    DateTimeOffset Timestamp,
    int Fan1Rpm,
    int Fan2Rpm,
    IReadOnlyDictionary<string, FanLimit> Limits);

public sealed record TemperatureSnapshot(
    double? CpuTempC,
    double? GpuTempC,
    double? VramTempC,
    double? CpuPowerW,
    double? GpuPowerW,
    string CpuSensor,
    string GpuSensor,
    string VramSensor);

public sealed record FanTargets(int Fan1Rpm, int Fan2Rpm);

public enum ControlStrategy
{
    FixedRpm,
    FanCurve
}

public enum FanControlMode
{
    FirmwareAutomatic,
    FixedRpm,
    FanCurve
}

public enum ItsMode
{
    Unknown,
    PowerSaving,
    Intelligent,
    Performance,
    Geek
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
    public double RampDownRpmPerSecond { get; set; } = 20;
    public List<int> CpuFan1Curve { get; set; } = [];
    public List<int> CpuFan2Curve { get; set; } = [];
    public List<int> GpuFan1Curve { get; set; } = [];
    public List<int> GpuFan2Curve { get; set; } = [];
    public List<int> CpuCurve { get; set; } = [];
    public List<int> GpuCurve { get; set; } = [];
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
    public bool SyncFanSpeeds { get; set; }
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
    public bool ResumeFanControlOnNextStart { get; set; }
    public bool FanControlWasRunning { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartToTray { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }
    public bool DisableControlOnSleep { get; set; } = true;
    public bool AttemptDisableControlOnSleepWhenUnsupported { get; set; }
    public double? FanReadMinimumIntervalSeconds { get; set; }
    public double? FanWriteMinimumIntervalSeconds { get; set; }
    public string PendingGpuMode { get; set; } = "";
    public int PcManagerNormalDefaultTemperature { get; set; } = 6600;
    public int PcManagerEyeCareDefaultTemperature { get; set; } = 3500;
}
