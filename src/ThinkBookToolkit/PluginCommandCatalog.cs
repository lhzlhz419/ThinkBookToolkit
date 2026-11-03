using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ThinkBookToolkit;

// Frozen v1 entry points. Adding a Runtime method does not expose it to plugins.
internal static class PluginCommandCatalog
{
    private static readonly Type Error = typeof(string).MakeByRefType();
    internal static readonly MethodInfo[] Methods =
    [
        M("ApplyPowerSettingsAsync", typeof(PowerSettingsState)),
        M("SetBetaCpuPowerEnabled", typeof(bool), typeof(bool)),
        M("SetFanControlAsync", typeof(bool)), M("SetFanModeAsync", typeof(FanControlMode)),
        M("SetFanRpmLimitsAsync", typeof(FanRpmLimits)), M("SetFixedRpmGameModeAsync", typeof(bool)),
        M("SetFnKeyDiscoveryModeAsync", typeof(bool)), M("SetFnKeyTakeoverAsync", typeof(bool)),
        M("SetFpsMonitoringConsumer", typeof(string), typeof(bool)), M("SetFullSpeedAsync", typeof(bool)),
        M("SetGpuModeAsync", typeof(GpuWorkingMode)), M("SetGpuOverclockEnabledAsync", typeof(bool)),
        M("SetInputSettingAsync", typeof(InputSettingKind), typeof(bool)), M("SetItsModeAsync", typeof(ItsMode)),
        M("SetNvApiGpuPowerEnabledAsync", typeof(bool)), M("SetStatus", typeof(string)),
        M("TrySetAlternativeFullSpeedMethod", typeof(bool), Error), M("TrySetAutomationEnabled", typeof(bool), Error),
        M("TrySetBackgroundImage", typeof(string), typeof(double), typeof(double), typeof(int), typeof(BackgroundImageSizeMode), typeof(bool), typeof(bool), typeof(string), typeof(int), Error),
        M("TrySetCloseToTray", typeof(bool), Error), M("TrySetContinuouslyWriteFanTargets", typeof(bool), Error),
        M("TrySetDisableControlOnSleep", typeof(bool), Error), M("TrySetFanIoMinimumIntervals", typeof(double?), typeof(double?), Error),
        M("TrySetGpuOverclockStartupEnabled", typeof(bool), Error), M("TrySetHardwareAccelerationMode", typeof(HardwareAccelerationMode), Error),
        M("TrySetKeyboardBacklightRestoreOnStartup", typeof(bool), Error), M("TrySetLanguage", typeof(string), Error),
        M("TrySetLockKeyOsd", typeof(InputSettingKind), typeof(bool), Error), M("TrySetLogLevel", typeof(string), Error),
        M("TrySetLogRetentionDays", typeof(int), Error), M("TrySetMacroEnabled", typeof(bool), Error),
        M("TrySetMinimizeToTray", typeof(bool), Error), M("TrySetOsdEnabled", typeof(bool), Error),
        M("TrySetOsdSettings", typeof(ToolkitOsdSettings), Error), M("TrySetOverviewLayout", typeof(OverviewLayoutSettings), Error),
        M("TrySetOverviewPageMode", typeof(OverviewPageMode), Error), M("TrySetPerformanceFanLink", typeof(PerformanceFanLinkSettings), Error),
        M("TrySetPerformanceModeConfiguration", typeof(IReadOnlyList<ItsMode>), typeof(IReadOnlyCollection<ItsMode>), Error),
        M("TrySetPerformanceModeOrder", typeof(IReadOnlyList<ItsMode>), Error),
        M("TrySetPowerSettingLock", typeof(PowerSetting), typeof(bool), typeof(PowerSettingsState), Error),
        M("TrySetPowerSettingsLockInterval", typeof(int), Error), M("TrySetRefreshInterval", typeof(double), Error),
        M("TrySetRefreshRateCycle", typeof(IEnumerable<uint>), typeof(bool), Error), M("TrySetRefreshRateCycle", typeof(IEnumerable<uint>), Error),
        M("TrySetSensorRecordingEnabled", typeof(bool), Error), M("TrySetSensorRecordingSettings", typeof(SensorRecordingSettings), Error),
        M("TrySetStartToTray", typeof(bool), Error), M("TrySetStartupMode", typeof(StartupLaunchMode), Error),
        M("TrySetStartWithWindows", typeof(bool), Error), M("TrySetTheme", typeof(string), Error)
    ];

    internal static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["toolkit.performance.mode.set"] = "SetItsModeAsync",
        ["toolkit.gpu.mode.set"] = "SetGpuModeAsync",
        ["toolkit.power.apply"] = "ApplyPowerSettingsAsync",
        ["toolkit.fans.mode.set"] = "SetFanModeAsync",
        ["toolkit.fans.full-speed.set"] = "SetFullSpeedAsync",
        ["toolkit.osd.enabled.set"] = "TrySetOsdEnabled",
        ["toolkit.recording.enabled.set"] = "TrySetSensorRecordingEnabled"
    };

    private static MethodInfo M(string name, params Type[] parameters) =>
        typeof(ToolkitRuntimeService).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, parameters, null) ?? throw new MissingMethodException("A compatible plugin entry point was removed: " + name);
}
