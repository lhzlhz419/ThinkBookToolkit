using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ThinkBookToolkit;

public static class CurveProfileStore
{
    public const string CurrentConfigurationVersion = "1.0";
    public static readonly int[] CpuTemps = Enumerable.Range(0, 15).Select(i => 30 + i * 5).ToArray();
    public static readonly int[] GpuTemps = Enumerable.Range(0, 13).Select(i => 30 + i * 5).ToArray();

    private const int ProfileCount = 5;
    public const int DefaultMinimumFanRpm = 1500;
    public const int DefaultMaximumFanRpm = 5500;
    public const int AbsoluteMinimumFanRpm = 0;
    public const int AbsoluteMaximumFanRpm = 10000;

    public static FanRpmLimits DefaultFanRpmLimitsForCurrentDevice() =>
        DefaultFanRpmLimitsForModel(DeviceModelDetector.CurrentIdentity.Model);

    internal static FanRpmLimits DefaultFanRpmLimitsForModel(string model) =>
        DeviceModelDetector.ModelMatches(model, DeviceModelDetector.ThinkBook14G6PlusImh)
            ? new FanRpmLimits
            {
                Fan1MinimumRpm = DefaultMinimumFanRpm,
                Fan1MaximumRpm = 6400,
                Fan2MinimumRpm = DefaultMinimumFanRpm,
                Fan2MaximumRpm = 6400
            }
            : new FanRpmLimits();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ProfilePath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".thinkbook_toolkit", "fan_curve_profiles.csharp.json");
        }
    }

    public static string SettingsPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".thinkbook_toolkit", "app_settings.csharp.json");
        }
    }

    public static string PendingInstallerSettingsPath => Path.Combine(
        Path.GetDirectoryName(SettingsPath)!,
        "pending_installer_settings.json");

    public static string SensorRecordingDirectory => Path.Combine(
        Path.GetDirectoryName(SettingsPath)!,
        "sensor-recordings");

    public static string DefaultProfilePath =>
        Path.Combine(AppContext.BaseDirectory, "default_fan_curve_profiles.json");

    public static string DefaultSettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "default_app_settings.json");

    public static List<FanProfile> Load()
    {
        var defaults = LoadDefaultProfiles();
        if (!File.Exists(ProfilePath))
            return defaults;

        try
        {
            var loaded = DeserializeProfiles(File.ReadAllText(ProfilePath));
            if (loaded is null)
                return defaults;

            for (var i = 0; i < Math.Min(ProfileCount, loaded.Count); i++)
            {
                defaults[i].Name = string.IsNullOrWhiteSpace(loaded[i].Name) ? $"Profile {i + 1}" : loaded[i].Name;
                defaults[i].TemperatureSmoothing = NormalizeSmoothingSamples(loaded[i].TemperatureSmoothing, defaults[i].TemperatureSmoothing);
                defaults[i].RampUpRpmPerSecond = PickAllowed(loaded[i].RampUpRpmPerSecond, [0, 10, 20, 50, 100], defaults[i].RampUpRpmPerSecond);
                defaults[i].FullRangeRampDownRpmPerSecond = PickAllowed(loaded[i].FullRangeRampDownRpmPerSecond, [0, 10, 20, 50, 100], defaults[i].FullRangeRampDownRpmPerSecond);
                defaults[i].RampDownRpmPerSecond = PickAllowed(loaded[i].RampDownRpmPerSecond, [0, 10, 20, 50, 100], defaults[i].RampDownRpmPerSecond);
                defaults[i].CpuFan1Curve = NormalizeProfileCurve(loaded[i].CpuFan1Curve, loaded[i].CpuCurve, CpuTemps.Length, defaults[i].CpuFan1Curve);
                defaults[i].CpuFan2Curve = NormalizeProfileCurve(loaded[i].CpuFan2Curve, loaded[i].CpuCurve, CpuTemps.Length, defaults[i].CpuFan2Curve);
                defaults[i].GpuFan1Curve = NormalizeProfileCurve(loaded[i].GpuFan1Curve, loaded[i].GpuCurve, GpuTemps.Length, defaults[i].GpuFan1Curve);
                defaults[i].GpuFan2Curve = NormalizeProfileCurve(loaded[i].GpuFan2Curve, loaded[i].GpuCurve, GpuTemps.Length, defaults[i].GpuFan2Curve);
                defaults[i].CpuCurve = [.. defaults[i].CpuFan1Curve];
                defaults[i].GpuCurve = [.. defaults[i].GpuFan1Curve];
            }
            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public static void Save(IReadOnlyList<FanProfile> profiles)
    {
        var document = new FanProfileConfigurationFile
        {
            Profiles = profiles.ToList()
        };
        WriteTextAtomically(
            ProfilePath,
            JsonSerializer.Serialize(document, JsonOptions));
    }

    public static AppSettings LoadSettings()
    {
        var defaults = LoadDefaultSettings();
        ApplyDeviceDefaults(defaults, settingsJson: null);
        if (!File.Exists(SettingsPath))
            return defaults;

        try
        {
            var settingsJson = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(settingsJson, JsonOptions);
            if (loaded is null || !IsSupportedConfigurationVersion(loaded.ConfigurationVersion))
                return defaults;

            defaults.ConfigurationVersion = CurrentConfigurationVersion;
            defaults.Language = loaded.Language is "en-US" or "zh-CN" ? loaded.Language : defaults.Language;
            defaults.Theme = loaded.Theme is "dark" or "light" or "system"
                ? loaded.Theme
                : defaults.Theme;
            defaults.BackgroundImagePath =
                loaded.BackgroundImagePath?.Trim() ?? string.Empty;
            defaults.BackgroundImageScalePercent =
                loaded.BackgroundImageScalePercent is >= 10 and <= 500
                    ? loaded.BackgroundImageScalePercent
                    : 100;
            defaults.BackgroundImageOpacityPercent =
                loaded.BackgroundImageOpacityPercent is >= 0 and <= 100
                    ? loaded.BackgroundImageOpacityPercent
                    : 30;
            defaults.BackgroundImageBlurRadius = Math.Clamp(
                loaded.BackgroundImageBlurRadius,
                0,
                40);
            defaults.BackgroundImageSizeMode = Enum.IsDefined(
                    loaded.BackgroundImageSizeMode)
                ? loaded.BackgroundImageSizeMode
                : BackgroundImageSizeMode.Fixed;
            defaults.BackgroundImageInverted = loaded.BackgroundImageInverted;
            defaults.BackgroundBaseColorEnabled =
                loaded.BackgroundBaseColorEnabled;
            defaults.BackgroundBaseColor = NormalizeBackgroundColor(
                loaded.BackgroundBaseColor);
            defaults.BackgroundMediaSpeedPercent =
                loaded.BackgroundMediaSpeedPercent is >= 10 and <= 500
                    ? loaded.BackgroundMediaSpeedPercent
                    : 100;
            defaults.HardwareAccelerationMode = Enum.IsDefined(
                    loaded.HardwareAccelerationMode)
                ? loaded.HardwareAccelerationMode
                : HardwareAccelerationMode.Disabled;
            defaults.OsdEnabled = loaded.OsdEnabled;
            defaults.Osd = NormalizeOsdSettings(loaded.Osd);
            defaults.HybridCoreDisplayDefaultsInitialized =
                loaded.HybridCoreDisplayDefaultsInitialized;
            defaults.BatteryCapacityDisplayDefaultsInitialized =
                loaded.BatteryCapacityDisplayDefaultsInitialized;
            defaults.SensorRecordingEnabled = loaded.SensorRecordingEnabled;
            defaults.SensorRecording = settingsJson.Contains(
                    $"\"{nameof(AppSettings.SensorRecording)}\"",
                    StringComparison.OrdinalIgnoreCase)
                ? NormalizeSensorRecordingSettings(loaded.SensorRecording)
                : new SensorRecordingSettings
                {
                    Sensors = defaults.Osd.Sensors
                        .Where(sensor => sensor !=
                            OsdSensor.BatteryOutputPower)
                        .ToList()
                };
            defaults.LastSensorRecordingPath =
                loaded.LastSensorRecordingPath?.Trim() ?? string.Empty;
            if (!settingsJson.Contains(
                    nameof(AppSettings.BatteryCapacityDisplayDefaultsInitialized),
                    StringComparison.OrdinalIgnoreCase) ||
                !loaded.BatteryCapacityDisplayDefaultsInitialized)
            {
                if (!defaults.Osd.Sensors.Contains(OsdSensor.BatteryCapacity))
                    defaults.Osd.Sensors.Add(OsdSensor.BatteryCapacity);
                defaults.SensorRecording.Sensors.Remove(
                    OsdSensor.BatteryOutputPower);
                if (!defaults.SensorRecording.Sensors.Contains(
                        OsdSensor.BatteryCapacity))
                    defaults.SensorRecording.Sensors.Add(
                        OsdSensor.BatteryCapacity);
                defaults.BatteryCapacityDisplayDefaultsInitialized = true;
            }
            if (!settingsJson.Contains(
                    nameof(ToolkitOsdSettings.FpsWarningThreshold),
                    StringComparison.OrdinalIgnoreCase))
            {
                defaults.Osd.Sensors.AddRange(
                [
                    OsdSensor.Fps,
                    OsdSensor.OnePercentLowFps,
                    OsdSensor.FrameLatency,
                    OsdSensor.BatteryOutputPower
                ]);
                defaults.Osd.Sensors = defaults.Osd.Sensors
                    .Distinct()
                    .ToList();
                if (!settingsJson.Contains(
                        $"\"{nameof(AppSettings.SensorRecording)}\"",
                        StringComparison.OrdinalIgnoreCase))
                {
                    defaults.SensorRecording.Sensors =
                        defaults.Osd.Sensors
                            .Where(sensor => sensor !=
                                OsdSensor.BatteryOutputPower)
                            .ToList();
                }
            }
            defaults.UseCustomLenovoDllDirectory =
                loaded.UseCustomLenovoDllDirectory;
            defaults.CustomLenovoDllDirectory =
                LenovoDependencyDirectory.Normalize(
                    loaded.CustomLenovoDllDirectory);
            defaults.IntervalSeconds = PickAllowed(loaded.IntervalSeconds, [0.5, 1, 2, 3, 5], defaults.IntervalSeconds);
            defaults.LastProfileIndex = Math.Max(0, Math.Min(ProfileCount - 1, loaded.LastProfileIndex));
            defaults.EditFan = loaded.EditFan == 2 ? 2 : 1;
            defaults.SyncFanSpeeds = loaded.SyncFanSpeeds;
            defaults.ControlStrategy = Enum.IsDefined(loaded.ControlStrategy) ? loaded.ControlStrategy : ControlStrategy.FixedRpm;
            defaults.FanCurveWarningAccepted = loaded.FanCurveWarningAccepted;
            defaults.GameExitHoldSeconds = PickAllowed(loaded.GameExitHoldSeconds, [0, 10, 20, 30, 60], defaults.GameExitHoldSeconds);
            defaults.ManualGameMode = loaded.ManualGameMode;
            defaults.FixedGameModeOverride = Enum.IsDefined(loaded.FixedGameModeOverride)
                ? loaded.FixedGameModeOverride
                : (loaded.ManualGameMode ? FixedGameModeOverride.GameUntilGamesEnd : FixedGameModeOverride.None);
            defaults.FixedModeHotkey = NormalizeHotkey(loaded.FixedModeHotkey);
            defaults.AutoDetectGames = !settingsJson.Contains(nameof(AppSettings.AutoDetectGames), StringComparison.OrdinalIgnoreCase) || loaded.AutoDetectGames;
            defaults.IncludedGamePaths = NormalizeApplicationPaths(
                loaded.IncludedGamePaths);
            defaults.ExcludedGamePaths = NormalizeApplicationPaths(
                loaded.ExcludedGamePaths);
            defaults.FixedSyncFanSpeeds = !settingsJson.Contains(nameof(AppSettings.FixedSyncFanSpeeds), StringComparison.OrdinalIgnoreCase) || loaded.FixedSyncFanSpeeds;
            defaults.FanRpmLimitsCustomized =
                settingsJson.Contains(
                    nameof(AppSettings.FanRpmLimitsCustomized),
                    StringComparison.OrdinalIgnoreCase) &&
                loaded.FanRpmLimitsCustomized;
            defaults.FanRpmLimits = defaults.FanRpmLimitsCustomized
                ? NormalizeFanRpmLimits(loaded.FanRpmLimits)
                : DefaultFanRpmLimitsForCurrentDevice();
            defaults.FixedRpm = NormalizeFixedRpmSettings(
                MigrateLegacyFixedRpm(settingsJson, loaded.FixedRpm ?? defaults.FixedRpm),
                defaults.FanRpmLimits);
            defaults.AdvancedFanCurve = AdvancedFanCurve.Normalize(
                MigrateOldAdvancedFanCurveDefaults(loaded.AdvancedFanCurve),
                defaults.FanRpmLimits);
            defaults.ResumeFanControlOnNextStart = loaded.ResumeFanControlOnNextStart || loaded.FanControlWasRunning;
            defaults.StartWithWindows = loaded.StartWithWindows;
            defaults.DelayStartup = loaded.StartWithWindows && loaded.DelayStartup;
            defaults.StartToTray = loaded.StartToTray;
            defaults.MinimizeToTray = loaded.MinimizeToTray;
            defaults.CloseToTray = loaded.CloseToTray;
            defaults.TakeOverFnKeys = loaded.TakeOverFnKeys;
            defaults.ShowCapsLockOsd =
                !settingsJson.Contains(
                    nameof(AppSettings.ShowCapsLockOsd),
                    StringComparison.OrdinalIgnoreCase) ||
                loaded.ShowCapsLockOsd;
            defaults.ShowNumLockOsd =
                !settingsJson.Contains(
                    nameof(AppSettings.ShowNumLockOsd),
                    StringComparison.OrdinalIgnoreCase) ||
                loaded.ShowNumLockOsd;
            defaults.RefreshRateCycleHz =
                RefreshRateController.NormalizeConfiguredRates(
                    loaded.RefreshRateCycleHz);
            defaults.IncludeDynamicRefreshRateInCycle =
                loaded.IncludeDynamicRefreshRateInCycle;
            defaults.FnPerformanceModeOrder =
                PerformanceModeCycle.NormalizeOrder(
                    loaded.FnPerformanceModeOrder);
            defaults.FnPerformanceModeEnabled =
                settingsJson.Contains(
                    nameof(AppSettings.FnPerformanceModeEnabled),
                    StringComparison.OrdinalIgnoreCase)
                    ? PerformanceModeCycle.NormalizeEnabled(
                        loaded.FnPerformanceModeEnabled)
                    : PerformanceModeCycle.DefaultOrder.ToList();
            defaults.ShutdownPerformanceMode =
                PerformanceModeCycle.TryParseSelectableMode(
                    loaded.ShutdownPerformanceMode,
                    out var shutdownMode)
                    ? shutdownMode.ToString()
                    : string.Empty;
            defaults.DisableControlOnSleep =
                !settingsJson.Contains(nameof(AppSettings.DisableControlOnSleep), StringComparison.OrdinalIgnoreCase) ||
                loaded.DisableControlOnSleep;
            defaults.AttemptDisableControlOnSleepWhenUnsupported =
                loaded.AttemptDisableControlOnSleepWhenUnsupported;
            if (IsValidFanIoIntervalOverride(
                    loaded.FanReadMinimumIntervalSeconds))
            {
                defaults.FanReadMinimumIntervalSeconds =
                    loaded.FanReadMinimumIntervalSeconds;
            }
            if (IsValidFanIoIntervalOverride(
                    loaded.FanWriteMinimumIntervalSeconds))
            {
                defaults.FanWriteMinimumIntervalSeconds =
                    loaded.FanWriteMinimumIntervalSeconds;
            }
            defaults.UseAlternativeFullSpeedMethod = settingsJson.Contains(
                    nameof(AppSettings.UseAlternativeFullSpeedMethod),
                    StringComparison.OrdinalIgnoreCase)
                ? loaded.UseAlternativeFullSpeedMethod
                : DeviceModelDetector.UsesAlternativeFullSpeedByDefault();
            defaults.AlternativeFullSpeedMethodInitialized =
                loaded.AlternativeFullSpeedMethodInitialized;
            defaults.ContinuouslyWriteFanTargets =
                loaded.ContinuouslyWriteFanTargets;
            defaults.UseNvApiGpuPower = loaded.UseNvApiGpuPower;
            defaults.UseIntelMmioCpuPower = loaded.UseIntelMmioCpuPower;
            defaults.UseAmdZenStatesCpuPower = loaded.UseAmdZenStatesCpuPower;
            defaults.SoftwareIntegrationMode = settingsJson.Contains(
                    nameof(AppSettings.SoftwareIntegrationMode),
                    StringComparison.OrdinalIgnoreCase) &&
                Enum.IsDefined(loaded.SoftwareIntegrationMode)
                    ? loaded.SoftwareIntegrationMode
                    : loaded.ShareDataWithOtherSoftware
                        ? SoftwareIntegrationMode.ShareDataOnly
                        : SoftwareIntegrationMode.Disabled;
            defaults.ShareDataWithOtherSoftware =
                defaults.SoftwareIntegrationMode !=
                SoftwareIntegrationMode.Disabled;
            defaults.DataSharingPort = IsValidDataSharingPort(
                    loaded.DataSharingPort)
                ? loaded.DataSharingPort
                : 2975;
            defaults.OverviewPageMode = Enum.IsDefined(
                    loaded.OverviewPageMode)
                ? loaded.OverviewPageMode
                : OverviewPageMode.Detailed;
            defaults.OverviewLayout = OverviewLayoutDefaults.Normalize(
                loaded.OverviewLayout);
            defaults.PerformanceFanLink =
                PerformanceFanLinkDefaults.Normalize(
                    loaded.PerformanceFanLink);
            defaults.PowerSettingsLockIntervalSeconds =
                PowerSettingsController.IsSupportedLockInterval(
                    loaded.PowerSettingsLockIntervalSeconds)
                    ? loaded.PowerSettingsLockIntervalSeconds
                    : 2;
            defaults.PowerSettingsLockTarget =
                PowerSettingsController.IsValidState(
                    loaded.PowerSettingsLockTarget)
                    ? loaded.PowerSettingsLockTarget
                    : null;
            var hasPerSettingLocks = settingsJson.Contains(
                "PowerSettingsLocks",
                StringComparison.OrdinalIgnoreCase);
            defaults.PowerSettingsLocks = hasPerSettingLocks
                ? NormalizePowerSettingsLocks(
                    loaded.PowerSettingsLocks,
                    defaults.PowerSettingsLockTarget)
                : IsLegacyPowerSettingsLockEnabled(settingsJson) &&
                  defaults.PowerSettingsLockTarget is not null
                    ? PowerSettingsLockSelection.All(
                        defaults.PowerSettingsLockTarget.Atpp.HasValue)
                    : new PowerSettingsLockSelection();
            if (!PowerSettingsController.IsValidLockConfiguration(
                    defaults.PowerSettingsLocks,
                    defaults.PowerSettingsLockTarget))
            {
                defaults.PowerSettingsLocks = new PowerSettingsLockSelection();
                defaults.PowerSettingsLockTarget = null;
            }
            defaults.PowerSettingsLocksByMode = NormalizePowerModeLocks(
                loaded.PowerSettingsLocksByMode);
            defaults.NvApiPowerSettingsLocksByMode = NormalizePowerModeLocks(
                loaded.NvApiPowerSettingsLocksByMode);
            if (loaded.UseNvApiGpuPower &&
                defaults.NvApiPowerSettingsLocksByMode.Count == 0 &&
                !settingsJson.Contains(
                    "NvApiPowerSettingsLocksByMode",
                    StringComparison.OrdinalIgnoreCase))
            {
                defaults.NvApiPowerSettingsLocksByMode =
                    NormalizePowerModeLocks(loaded.PowerSettingsLocksByMode);
                defaults.PowerSettingsLocksByMode =
                    MigrateLegacyPowerModeLocksFromNvApi(
                        loaded.PowerSettingsLocksByMode);
            }
            defaults.GpuOverclock = GpuOverclockPolicy.Normalize(
                loaded.GpuOverclock);
            defaults.AutoEnableGpuOverclockOnStartup =
                loaded.AutoEnableGpuOverclockOnStartup;
            defaults.Automations = AutomationSettingsDefaults.Normalize(
                loaded.Automations);
            defaults.AutomationEnabled = loaded.AutomationEnabled;
            defaults.Macros = KeyboardMacroDefaults.Normalize(loaded.Macros);
            defaults.MacroEnabled = loaded.MacroEnabled;
            defaults.CustomFnKeyNames =
                AutomationSettingsDefaults.NormalizeCustomFnKeyNames(
                    loaded.CustomFnKeyNames);
            defaults.FnKeyAutomationBindings =
                AutomationSettingsDefaults.NormalizeFnBindings(
                    loaded.FnKeyAutomationBindings,
                    defaults.Automations,
                    defaults.CustomFnKeyNames);
            defaults.FnKeyDoublePressAutomationBindings =
                AutomationSettingsDefaults.NormalizeFnBindings(
                    loaded.FnKeyDoublePressAutomationBindings,
                    defaults.Automations,
                    defaults.CustomFnKeyNames);
            defaults.AcceptedDisclaimerVersion =
                loaded.AcceptedDisclaimerVersion ?? string.Empty;
            defaults.LastFanBackendIdentity =
                loaded.LastFanBackendIdentity ?? string.Empty;
            defaults.SuppressedFanBackendStartupNoticeIdentity =
                loaded.SuppressedFanBackendStartupNoticeIdentity ??
                string.Empty;
            defaults.PendingGpuMode = Enum.TryParse<GpuWorkingMode>(
                loaded.PendingGpuMode,
                out _)
                ? loaded.PendingGpuMode
                : string.Empty;
            defaults.PendingGpuModeBootSessionId =
                string.IsNullOrEmpty(defaults.PendingGpuMode)
                    ? string.Empty
                    : loaded.PendingGpuModeBootSessionId ?? string.Empty;
            defaults.PendingGpuModeSource =
                !string.IsNullOrEmpty(defaults.PendingGpuMode) &&
                Enum.TryParse<GpuWorkingMode>(
                    loaded.PendingGpuModeSource,
                    out _)
                    ? loaded.PendingGpuModeSource
                    : string.Empty;
            defaults.PendingGpuModeSourceUsesDirectGraphicsConfiguration =
                string.IsNullOrEmpty(defaults.PendingGpuModeSource)
                    ? null
                    : loaded.PendingGpuModeSourceUsesDirectGraphicsConfiguration;
            defaults.PendingGpuModeProtocol = Enum.TryParse<GpuControlProtocol>(
                loaded.PendingGpuModeProtocol,
                out _)
                ? loaded.PendingGpuModeProtocol
                : string.Empty;
            defaults.PendingGpuModeParentStaged =
                !string.IsNullOrEmpty(defaults.PendingGpuMode) &&
                loaded.PendingGpuModeParentStaged;
            defaults.PendingGpuModeChildStaged =
                !string.IsNullOrEmpty(defaults.PendingGpuMode) &&
                loaded.PendingGpuModeChildStaged;
            defaults.PendingGpuModePostBootAttempts = Math.Clamp(
                loaded.PendingGpuModePostBootAttempts,
                0,
                30);
            defaults.PendingGpuModeLastError =
                loaded.PendingGpuModeLastError ?? string.Empty;
            defaults.LastGpuModeFailure =
                loaded.LastGpuModeFailure ?? string.Empty;
            defaults.PcManagerNormalDefaultTemperature =
                NormalizeColorTemperature(
                    loaded.PcManagerNormalDefaultTemperature,
                    defaults.PcManagerNormalDefaultTemperature);
            defaults.PcManagerEyeCareDefaultTemperature =
                NormalizeColorTemperature(
                    loaded.PcManagerEyeCareDefaultTemperature,
                    defaults.PcManagerEyeCareDefaultTemperature);
            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        settings.ConfigurationVersion = CurrentConfigurationVersion;
        settings.IncludedGamePaths = NormalizeApplicationPaths(
            settings.IncludedGamePaths);
        settings.ExcludedGamePaths = NormalizeApplicationPaths(
            settings.ExcludedGamePaths);
        if (!PowerSettingsController.IsSupportedLockInterval(
                settings.PowerSettingsLockIntervalSeconds))
        {
            settings.PowerSettingsLockIntervalSeconds = 2;
        }
        settings.PowerSettingsLocks = NormalizePowerSettingsLocks(
            settings.PowerSettingsLocks,
            settings.PowerSettingsLockTarget);
        if (!PowerSettingsController.IsValidLockConfiguration(
                settings.PowerSettingsLocks,
                settings.PowerSettingsLockTarget))
        {
            settings.PowerSettingsLocks = new PowerSettingsLockSelection();
            settings.PowerSettingsLockTarget = null;
        }
        settings.PowerSettingsLocksByMode = NormalizePowerModeLocks(
            settings.PowerSettingsLocksByMode);
        settings.NvApiPowerSettingsLocksByMode = NormalizePowerModeLocks(
            settings.NvApiPowerSettingsLocksByMode);
        if (!IsValidDataSharingPort(settings.DataSharingPort))
            settings.DataSharingPort = 2975;
        settings.GpuOverclock = GpuOverclockPolicy.Normalize(
            settings.GpuOverclock);
        settings.Automations = AutomationSettingsDefaults.Normalize(
            settings.Automations);
        settings.Macros = KeyboardMacroDefaults.Normalize(settings.Macros);
        settings.CustomFnKeyNames =
            AutomationSettingsDefaults.NormalizeCustomFnKeyNames(
                settings.CustomFnKeyNames);
        settings.FnKeyAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                settings.FnKeyAutomationBindings,
                settings.Automations,
                settings.CustomFnKeyNames);
        settings.FnKeyDoublePressAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                settings.FnKeyDoublePressAutomationBindings,
                settings.Automations,
                settings.CustomFnKeyNames);
        settings.FnPerformanceModeOrder =
            PerformanceModeCycle.NormalizeOrder(
                settings.FnPerformanceModeOrder);
        settings.FnPerformanceModeEnabled =
            PerformanceModeCycle.NormalizeEnabled(
                settings.FnPerformanceModeEnabled);
        settings.RefreshRateCycleHz =
            RefreshRateController.NormalizeConfiguredRates(
                settings.RefreshRateCycleHz);
        if (!settings.StartWithWindows)
            settings.DelayStartup = false;
        if (!PerformanceModeCycle.TryParseSelectableMode(
                settings.ShutdownPerformanceMode,
                out _))
        {
            settings.ShutdownPerformanceMode = string.Empty;
        }
        settings.AdvancedFanCurve = AdvancedFanCurve.Normalize(
            settings.AdvancedFanCurve,
            NormalizeFanRpmLimits(settings.FanRpmLimits));
        settings.OverviewLayout = OverviewLayoutDefaults.Normalize(
            settings.OverviewLayout);
        settings.PerformanceFanLink =
            PerformanceFanLinkDefaults.Normalize(
                settings.PerformanceFanLink);
        WriteTextAtomically(
            SettingsPath,
            JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void ApplyDeviceDefaults(
        AppSettings settings,
        string? settingsJson)
    {
        if (!settings.FanRpmLimitsCustomized)
            settings.FanRpmLimits = DefaultFanRpmLimitsForCurrentDevice();
        if (settingsJson is null ||
            !settingsJson.Contains(
                nameof(AppSettings.UseAlternativeFullSpeedMethod),
                StringComparison.OrdinalIgnoreCase))
        {
            settings.UseAlternativeFullSpeedMethod =
                DeviceModelDetector.UsesAlternativeFullSpeedByDefault();
        }
        settings.OverviewLayout = OverviewLayoutDefaults.Normalize(
            settings.OverviewLayout);
        settings.PerformanceFanLink =
            PerformanceFanLinkDefaults.Normalize(
                settings.PerformanceFanLink);
    }

    internal static PowerSettingsLockSelection NormalizePowerSettingsLocks(
        PowerSettingsLockSelection? selection,
        PowerSettingsState? target)
    {
        var normalized = selection is null
            ? new PowerSettingsLockSelection()
            : selection with { };
        foreach (var setting in Enum.GetValues<PowerSetting>())
        {
            if (normalized.IsLocked(setting) &&
                (target is null ||
                 !target.IsAvailable(setting) ||
                 !PowerSettingsController.Value(target, setting).HasValue))
            {
                normalized = normalized.With(setting, false);
            }
        }
        return normalized;
    }

    internal static Dictionary<string, PowerModeLockSettings>
        NormalizePowerModeLocks(
            IReadOnlyDictionary<string, PowerModeLockSettings>? profiles)
    {
        var normalized = new Dictionary<string, PowerModeLockSettings>(
            StringComparer.OrdinalIgnoreCase);
        if (profiles is null)
            return normalized;
        foreach (var pair in profiles)
        {
            if (!Enum.TryParse<ItsMode>(pair.Key, ignoreCase: true, out var mode) ||
                mode == ItsMode.Unknown || pair.Value is null)
            {
                continue;
            }
            var locks = NormalizePowerSettingsLocks(
                pair.Value.Locks,
                pair.Value.Target);
            if (!PowerSettingsController.IsValidLockConfiguration(
                    locks,
                    pair.Value.Target))
            {
                continue;
            }
            normalized[mode.ToString()] = new PowerModeLockSettings
            {
                Locks = locks,
                Target = pair.Value.Target
            };
        }
        return normalized;
    }

    internal static Dictionary<string, PowerModeLockSettings>
        MigrateLegacyPowerModeLocksFromNvApi(
            IReadOnlyDictionary<string, PowerModeLockSettings>? profiles)
    {
        var migrated = new Dictionary<string, PowerModeLockSettings>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profiles ??
                 new Dictionary<string, PowerModeLockSettings>())
        {
            if (!Enum.TryParse<ItsMode>(
                    pair.Key,
                    ignoreCase: true,
                    out var mode) ||
                mode == ItsMode.Unknown || pair.Value?.Target is null)
                continue;
            var sourceLocks = pair.Value.Locks;
            var locks = sourceLocks with
            {
                GpuPowerBoost = sourceLocks.GpuPowerBoost ||
                                sourceLocks.NvPcfAcMaxGpuLimit,
                GpuConfigurableTgp = sourceLocks.GpuConfigurableTgp ||
                                     sourceLocks.NvPcfAcDefaultGpuLimit,
                GpuToCpuDynamicBoost = sourceLocks.GpuToCpuDynamicBoost ||
                                       sourceLocks.NvPcfAcMinGpuLimit,
                Atpp = sourceLocks.Atpp ||
                       sourceLocks.NvPcfAcTargetTppLimit,
                GpuTemperatureLimit = sourceLocks.GpuTemperatureLimit ||
                                      sourceLocks.NvApiGpuTemperatureLimit,
                NvPcfAcTargetTppLimit = false,
                NvPcfAcDefaultGpuLimit = false,
                NvPcfAcMinGpuLimit = false,
                NvPcfAcMaxGpuLimit = false,
                NvPcfDynamicBoost = false,
                NvApiGpuTemperatureLimit = false
            };
            var target = NvPcfPowerPolicy.ToLegacy(pair.Value.Target);
            locks = NormalizePowerSettingsLocks(locks, target);
            if (PowerSettingsController.IsValidLockConfiguration(
                    locks,
                    target))
            {
                migrated[mode.ToString()] = new PowerModeLockSettings
                {
                    Locks = locks,
                    Target = target
                };
            }
        }
        return migrated;
    }

    internal static bool IsLegacyPowerSettingsLockEnabled(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals(
                    "PowerSettingsLockEnabled",
                    StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or
                    JsonValueKind.False)
            {
                return property.Value.GetBoolean();
            }
        }
        return false;
    }

    public static void StageInstallerSettings(
        bool useCustomLenovoDllDirectory,
        string? customLenovoDllDirectory) =>
        WriteTextAtomically(
            PendingInstallerSettingsPath,
            JsonSerializer.Serialize(
                new PendingInstallerSettings
                {
                    UseCustomLenovoDllDirectory =
                        useCustomLenovoDllDirectory,
                    CustomLenovoDllDirectory =
                        LenovoDependencyDirectory.Normalize(
                            customLenovoDllDirectory)
                },
                JsonOptions));

    public static void ApplyPendingInstallerSettings(AppSettings settings)
    {
        if (!File.Exists(PendingInstallerSettingsPath))
            return;

        var pending = JsonSerializer.Deserialize<PendingInstallerSettings>(
            File.ReadAllText(PendingInstallerSettingsPath),
            JsonOptions);
        if (pending is not null)
        {
            settings.UseCustomLenovoDllDirectory =
                pending.UseCustomLenovoDllDirectory;
            settings.CustomLenovoDllDirectory =
                LenovoDependencyDirectory.Normalize(
                    pending.CustomLenovoDllDirectory);
            SaveSettings(settings);
        }
        File.Delete(PendingInstallerSettingsPath);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The configuration directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A stale temporary file is safer than replacing a valid
                // configuration with partially written JSON.
            }
        }
    }

    public static void WriteBuiltInProfiles() => Save(Defaults());

    public static void WriteBuiltInSettings() => SaveSettings(new AppSettings());

    private static List<FanProfile> LoadDefaultProfiles()
    {
        try
        {
            if (!File.Exists(DefaultProfilePath))
                return Defaults();
            var profiles = DeserializeProfiles(File.ReadAllText(DefaultProfilePath));
            return profiles is { Count: ProfileCount } ? profiles : Defaults();
        }
        catch
        {
            return Defaults();
        }
    }

    private static AppSettings LoadDefaultSettings()
    {
        try
        {
            if (!File.Exists(DefaultSettingsPath))
                return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(DefaultSettingsPath),
                JsonOptions);
            return settings is not null &&
                   IsSupportedConfigurationVersion(settings.ConfigurationVersion)
                ? settings
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static int SnapRpm(double value) => (int)Math.Round(value / 100.0) * 100;

    public static bool IsValidFanIoIntervalOverride(double? seconds) =>
        seconds is null ||
        double.IsFinite(seconds.Value) &&
        seconds.Value >= 1 &&
        seconds.Value == Math.Truncate(seconds.Value) &&
        seconds.Value <= TimeSpan.MaxValue.TotalSeconds;

    public static bool IsValidDataSharingPort(int port) =>
        port is >= 1 and <= 65535;

    public static bool IsSupportedConfigurationVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ||
        string.Equals(
            version,
            CurrentConfigurationVersion,
            StringComparison.Ordinal);

    internal static AdvancedFanCurveSettings? MigrateOldAdvancedFanCurveDefaults(
        AdvancedFanCurveSettings? value)
    {
        if (value is null)
            return null;

        var currentDefaults = AdvancedFanCurve.CreateDefaultPoints();
        if (value.Points.Count != currentDefaults.Count)
            return value;

        var matchesOriginalDefaults = true;
        var matchesIntermediateDefaults = true;
        for (var index = 0; index < value.Points.Count; index++)
        {
            var point = value.Points[index];
            var expected = currentDefaults[index];
            if (point.Fan1Rpm != expected.Fan1Rpm ||
                point.Fan2Rpm != expected.Fan2Rpm ||
                point.CpuRampUpTemperatureC != expected.CpuRampUpTemperatureC ||
                point.CpuRampDownTemperatureC != expected.CpuRampDownTemperatureC ||
                point.GpuRampUpTemperatureC != expected.GpuRampUpTemperatureC ||
                point.GpuRampDownTemperatureC != expected.GpuRampDownTemperatureC)
            {
                return value;
            }

            matchesOriginalDefaults &=
                point.RampUpRpmPerSecond == (index < 4 ? 20d : 50d) &&
                point.RampDownRpmPerSecond == (index < 4 ? 10d : 20d);
            matchesIntermediateDefaults &=
                point.RampUpRpmPerSecond == (index < 4 ? 100d : 0d) &&
                point.RampDownRpmPerSecond == (index < 4 ? 20d : 100d);
        }

        if (!matchesOriginalDefaults && !matchesIntermediateDefaults)
            return value;

        return new AdvancedFanCurveSettings
        {
            TemperatureSmoothing = value.TemperatureSmoothing,
            Points = currentDefaults
        };
    }

    private static List<FanProfile>? DeserializeProfiles(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<FanProfile>>(
                json,
                JsonOptions);
        }

        var configuration =
            JsonSerializer.Deserialize<FanProfileConfigurationFile>(
                json,
                JsonOptions);
        return configuration is not null &&
               IsSupportedConfigurationVersion(
                   configuration.ConfigurationVersion)
            ? configuration.Profiles
            : null;
    }

    internal static string NormalizeBackgroundColor(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.StartsWith("0X", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Length == 6 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : "FFFFFF";
    }

    internal static ToolkitOsdSettings NormalizeOsdSettings(
        ToolkitOsdSettings? value)
    {
        value ??= new ToolkitOsdSettings();
        var allowedIntervals = new[] { .5d, 1d, 2d, 3d, 5d };
        var sensors = (value.Sensors ?? [])
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();

        // Expand the aggregate rows used by the original OSD into the new
        // per-device rows. Numeric enum values are persisted in JSON, so the
        // legacy members must be migrated explicitly rather than reused.
        if (sensors.Remove(OsdSensor.MemoryTemperature))
        {
            sensors.Add(OsdSensor.MemoryCommitted);
            sensors.Add(OsdSensor.MemorySlot1Temperature);
            sensors.Add(OsdSensor.MemorySlot2Temperature);
        }
        if (sensors.Remove(OsdSensor.StorageTemperatures))
        {
            sensors.AddRange(
            [
                OsdSensor.Storage1Temperature,
                OsdSensor.Storage2Temperature,
                OsdSensor.Storage3Temperature,
                OsdSensor.Storage4Temperature,
                OsdSensor.Storage5Temperature,
                OsdSensor.Storage6Temperature,
                OsdSensor.Storage7Temperature,
                OsdSensor.Storage8Temperature
            ]);
        }
        if (sensors.Remove(OsdSensor.FanSpeeds))
        {
            sensors.Add(OsdSensor.Fan1Speed);
            sensors.Add(OsdSensor.Fan2Speed);
        }
        var fps = NormalizeDescendingThresholds(
            value.FpsWarningThreshold,
            value.FpsCriticalThreshold,
            45,
            30,
            0,
            1000);
        var lowFpsPercentage = NormalizeDescendingThresholds(
            value.LowFpsWarningPercentage,
            value.LowFpsCriticalPercentage,
            75,
            50,
            0,
            100);
        var lowFpsDelta = NormalizeAscendingThresholds(
            value.LowFpsWarningDelta,
            value.LowFpsCriticalDelta,
            15,
            30,
            0,
            1000);
        var cpuTemperature = NormalizeAscendingThresholds(
            value.CpuTemperatureWarning,
            value.CpuTemperatureCritical,
            80,
            95,
            0,
            120);
        var gpuHotSpotTemperature = NormalizeAscendingThresholds(
            value.GpuHotSpotTemperatureWarning,
            value.GpuHotSpotTemperatureCritical,
            80,
            95,
            0,
            120);
        var gpuTemperature = NormalizeAscendingThresholds(
            value.GpuTemperatureWarning,
            value.GpuTemperatureCritical,
            75,
            85,
            0,
            120);
        var vramTemperature = NormalizeAscendingThresholds(
            value.VramTemperatureWarning,
            value.VramTemperatureCritical,
            75,
            85,
            0,
            120);
        var memoryTemperature = NormalizeAscendingThresholds(
            value.MemoryTemperatureWarning,
            value.MemoryTemperatureCritical,
            65,
            75,
            0,
            120);
        var storageTemperature = NormalizeAscendingThresholds(
            value.StorageTemperatureWarning,
            value.StorageTemperatureCritical,
            60,
            90,
            0,
            120);
        var usage = NormalizeAscendingThresholds(
            value.UsageWarningThreshold,
            value.UsageCriticalThreshold,
            70,
            90,
            0,
            100);
        var batteryOutput = NormalizeDescendingThresholds(
            -Math.Clamp(-value.BatteryOutputPowerWarning, 1, 500),
            -Math.Clamp(-value.BatteryOutputPowerCritical, 1, 500),
            -1,
            -30,
            -500,
            -1);
        return new ToolkitOsdSettings
        {
            Orientation = Enum.IsDefined(value.Orientation)
                ? value.Orientation
                : OsdOrientation.Vertical,
            RefreshIntervalSeconds = allowedIntervals.Contains(
                    value.RefreshIntervalSeconds)
                ? value.RefreshIntervalSeconds
                : 1,
            FixedPosition = value.FixedPosition,
            OpacityPercent = Math.Clamp(value.OpacityPercent, 0, 100),
            FontSize = Math.Clamp(value.FontSize, 8, 24),
            SnapThreshold = Math.Clamp(value.SnapThreshold, 0, 100),
            MultipleTemperatureMode = Enum.IsDefined(
                    value.MultipleTemperatureMode)
                ? value.MultipleTemperatureMode
                : OsdMultipleTemperatureMode.All,
            MemoryDisplayMode = Enum.IsDefined(value.MemoryDisplayMode)
                ? value.MemoryDisplayMode
                : OsdMemoryDisplayMode.All,
            BackgroundColor = NormalizeOsdColor(value.BackgroundColor, "0E131D"),
            CategoryColor = NormalizeOsdColor(value.CategoryColor, "7C9CFF"),
            LabelColor = NormalizeOsdColor(value.LabelColor, "B6C2D8"),
            ValueColor = NormalizeOsdColor(value.ValueColor, "FFFFFF"),
            WarningColor = NormalizeOsdColor(value.WarningColor, "FFFF00"),
            CriticalColor = NormalizeOsdColor(value.CriticalColor, "FF0000"),
            FpsWarningThreshold = fps.Warning,
            FpsCriticalThreshold = fps.Critical,
            LowFpsThresholdMode = Enum.IsDefined(value.LowFpsThresholdMode)
                ? value.LowFpsThresholdMode
                : OsdLowFpsThresholdMode.PercentageOfFps,
            LowFpsWarningPercentage = lowFpsPercentage.Warning,
            LowFpsCriticalPercentage = lowFpsPercentage.Critical,
            LowFpsWarningDelta = lowFpsDelta.Warning,
            LowFpsCriticalDelta = lowFpsDelta.Critical,
            CpuTemperatureWarning = cpuTemperature.Warning,
            CpuTemperatureCritical = cpuTemperature.Critical,
            GpuHotSpotTemperatureWarning = gpuHotSpotTemperature.Warning,
            GpuHotSpotTemperatureCritical = gpuHotSpotTemperature.Critical,
            GpuTemperatureWarning = gpuTemperature.Warning,
            GpuTemperatureCritical = gpuTemperature.Critical,
            VramTemperatureWarning = vramTemperature.Warning,
            VramTemperatureCritical = vramTemperature.Critical,
            MemoryTemperatureWarning = memoryTemperature.Warning,
            MemoryTemperatureCritical = memoryTemperature.Critical,
            StorageTemperatureWarning = storageTemperature.Warning,
            StorageTemperatureCritical = storageTemperature.Critical,
            UsageWarningThreshold = usage.Warning,
            UsageCriticalThreshold = usage.Critical,
            BatteryOutputPowerWarning = batteryOutput.Warning,
            BatteryOutputPowerCritical = batteryOutput.Critical,
            Sensors = sensors.Distinct().ToList(),
            HorizontalX = value.HorizontalX,
            HorizontalY = value.HorizontalY,
            VerticalX = value.VerticalX,
            VerticalY = value.VerticalY
        };
    }

    internal static SensorRecordingSettings NormalizeSensorRecordingSettings(
        SensorRecordingSettings? value)
    {
        value ??= new SensorRecordingSettings();
        var interval = new[] { .5d, 1d, 2d, 3d, 5d }
            .Contains(value.IntervalSeconds)
            ? value.IntervalSeconds
            : 1;
        var sensors = NormalizeOsdSettings(new ToolkitOsdSettings
        {
            Sensors = value.Sensors ?? []
        }).Sensors;
        return new SensorRecordingSettings
        {
            IntervalSeconds = interval,
            MaximumPlotPoints = Math.Clamp(
                value.MaximumPlotPoints,
                100,
                10_000),
            Sensors = sensors
        };
    }

    private static (int Warning, int Critical) NormalizeAscendingThresholds(
        int warning,
        int critical,
        int fallbackWarning,
        int fallbackCritical,
        int minimum,
        int maximum)
    {
        warning = Math.Clamp(warning, minimum, maximum);
        critical = Math.Clamp(critical, minimum, maximum);
        return warning < critical
            ? (warning, critical)
            : (fallbackWarning, fallbackCritical);
    }

    private static string NormalizeOsdColor(string? value, string fallback)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.StartsWith("#", StringComparison.Ordinal))
            normalized = normalized[1..];
        if (normalized.StartsWith("0X", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Length == 6 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : fallback;
    }

    private static (int Warning, int Critical) NormalizeDescendingThresholds(
        int warning,
        int critical,
        int fallbackWarning,
        int fallbackCritical,
        int minimum,
        int maximum)
    {
        warning = Math.Clamp(warning, minimum, maximum);
        critical = Math.Clamp(critical, minimum, maximum);
        return warning > critical
            ? (warning, critical)
            : (fallbackWarning, fallbackCritical);
    }

    private sealed class FanProfileConfigurationFile
    {
        public string ConfigurationVersion { get; set; } =
            CurrentConfigurationVersion;

        public List<FanProfile> Profiles { get; set; } = [];
    }

    private sealed class PendingInstallerSettings
    {
        public bool UseCustomLenovoDllDirectory { get; set; }

        public string CustomLenovoDllDirectory { get; set; } = string.Empty;
    }

    private static int NormalizeColorTemperature(int value, int fallback) =>
        value is >= 2000 and <= 11200 ? value : fallback;

    public static int ClampRpm(double value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, SnapRpm(value)));
    }

    public static int ClampFixedRpm(double value, int minimum, int maximum)
    {
        if (Math.Abs(value) < 0.1)
            return 0;
        return ClampRpm(value, minimum, maximum);
    }

    public static FixedRpmSettings NormalizeFixedRpmSettings(FixedRpmSettings settings, int minimum, int maximum)
        => NormalizeFixedRpmSettings(
            settings,
            new FanRpmLimits
            {
                Fan1MinimumRpm = minimum,
                Fan1MaximumRpm = maximum,
                Fan2MinimumRpm = minimum,
                Fan2MaximumRpm = maximum
            });

    public static FixedRpmSettings NormalizeFixedRpmSettings(
        FixedRpmSettings settings,
        FanRpmLimits limits)
    {
        limits = NormalizeFanRpmLimits(limits);
        var result = new FixedRpmSettings
        {
            PowerSavingNormalFan1Rpm = ClampFixedRpm(settings.PowerSavingNormalFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            PowerSavingNormalFan2Rpm = ClampFixedRpm(settings.PowerSavingNormalFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            PowerSavingGameFan1Rpm = ClampFixedRpm(settings.PowerSavingGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            PowerSavingGameFan2Rpm = ClampFixedRpm(settings.PowerSavingGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            IntelligentNormalFan1Rpm = ClampFixedRpm(settings.IntelligentNormalFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            IntelligentNormalFan2Rpm = ClampFixedRpm(settings.IntelligentNormalFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            IntelligentGameFan1Rpm = ClampFixedRpm(settings.IntelligentGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            IntelligentGameFan2Rpm = ClampFixedRpm(settings.IntelligentGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            PerformanceNormalFan1Rpm = ClampFixedRpm(settings.PerformanceNormalFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            PerformanceNormalFan2Rpm = ClampFixedRpm(settings.PerformanceNormalFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            PerformanceGameFan1Rpm = ClampFixedRpm(settings.PerformanceGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            PerformanceGameFan2Rpm = ClampFixedRpm(settings.PerformanceGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            GeekNormalFan1Rpm = ClampFixedRpm(settings.GeekNormalFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            GeekNormalFan2Rpm = ClampFixedRpm(settings.GeekNormalFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
            GeekGameFan1Rpm = ClampFixedRpm(settings.GeekGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm),
            GeekGameFan2Rpm = ClampFixedRpm(settings.GeekGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm),
        };

        result.PowerSavingGameFan1Rpm = EnsureGameAtLeastNormal(result.PowerSavingNormalFan1Rpm, result.PowerSavingGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm);
        result.PowerSavingGameFan2Rpm = EnsureGameAtLeastNormal(result.PowerSavingNormalFan2Rpm, result.PowerSavingGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm);
        result.IntelligentGameFan1Rpm = EnsureGameAtLeastNormal(result.IntelligentNormalFan1Rpm, result.IntelligentGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm);
        result.IntelligentGameFan2Rpm = EnsureGameAtLeastNormal(result.IntelligentNormalFan2Rpm, result.IntelligentGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm);
        result.PerformanceGameFan1Rpm = EnsureGameAtLeastNormal(result.PerformanceNormalFan1Rpm, result.PerformanceGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm);
        result.PerformanceGameFan2Rpm = EnsureGameAtLeastNormal(result.PerformanceNormalFan2Rpm, result.PerformanceGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm);
        result.GeekGameFan1Rpm = EnsureGameAtLeastNormal(result.GeekNormalFan1Rpm, result.GeekGameFan1Rpm, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm);
        result.GeekGameFan2Rpm = EnsureGameAtLeastNormal(result.GeekNormalFan2Rpm, result.GeekGameFan2Rpm, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm);
        return result;
    }

    public static FanRpmLimits NormalizeFanRpmLimits(FanRpmLimits? limits)
    {
        var value = limits ?? new FanRpmLimits();
        return new FanRpmLimits
        {
            Fan1MinimumRpm = NormalizeMinimum(value.Fan1MinimumRpm),
            Fan1MaximumRpm = NormalizeMaximum(
                value.Fan1MinimumRpm,
                value.Fan1MaximumRpm),
            Fan2MinimumRpm = NormalizeMinimum(value.Fan2MinimumRpm),
            Fan2MaximumRpm = NormalizeMaximum(
                value.Fan2MinimumRpm,
                value.Fan2MaximumRpm)
        };
    }

    public static List<int> ClampCurve(IEnumerable<int> values, int minimum, int maximum)
    {
        return EnforceNonDecreasing(values.Select(value => ClampRpm(value, minimum, maximum)).ToList());
    }

    public static List<int> EnforceNonDecreasing(IReadOnlyList<int> values)
    {
        var result = values.Select(value => SnapRpm(value)).ToList();
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i] < result[i - 1])
                result[i] = result[i - 1];
        }
        return result;
    }

    public static int Interpolate(int[] temps, IReadOnlyList<int> curve, double? tempC)
    {
        if (tempC is null)
            return 0;
        if (tempC <= temps[0])
            return curve[0];
        if (tempC >= temps[^1])
            return curve[^1];

        for (var i = 0; i < temps.Length - 1; i++)
        {
            if (temps[i] <= tempC && tempC <= temps[i + 1])
            {
                var ratio = (tempC.Value - temps[i]) / (temps[i + 1] - temps[i]);
                return SnapRpm(curve[i] + (curve[i + 1] - curve[i]) * ratio);
            }
        }

        return curve[^1];
    }

    private static List<FanProfile> Defaults()
    {
        var cpuBase = CurveFromAnchors(CpuTemps, [(30, DefaultMinimumFanRpm), (45, 1800), (60, 2600), (75, 3800), (90, 5000), (100, DefaultMaximumFanRpm)]);
        var gpuBase = CurveFromAnchors(GpuTemps, [(30, DefaultMinimumFanRpm), (45, 1800), (60, 2700), (75, 4200), (90, DefaultMaximumFanRpm)]);
        var profiles = new List<FanProfile>();

        for (var i = 0; i < ProfileCount; i++)
        {
            profiles.Add(new FanProfile
            {
                Name = $"Profile {i + 1}",
                CpuFan1Curve = [.. cpuBase],
                CpuFan2Curve = [.. cpuBase],
                GpuFan1Curve = [.. gpuBase],
                GpuFan2Curve = [.. gpuBase],
                CpuCurve = [.. cpuBase],
                GpuCurve = [.. gpuBase]
            });
        }

        SetBothCpuCurves(profiles[1], cpuBase.Select(value => Math.Max(DefaultMinimumFanRpm, value - 300)).ToList());
        SetBothGpuCurves(profiles[1], gpuBase.Select(value => Math.Max(DefaultMinimumFanRpm, value - 300)).ToList());
        SetBothCpuCurves(profiles[2], cpuBase.Select(value => Math.Min(DefaultMaximumFanRpm, value + 500)).ToList());
        SetBothGpuCurves(profiles[2], gpuBase.Select(value => Math.Min(DefaultMaximumFanRpm, value + 500)).ToList());
        SetBothGpuCurves(profiles[3], gpuBase.Select(value => Math.Min(DefaultMaximumFanRpm, value + 700)).ToList());
        return profiles;
    }

    private static void SetBothCpuCurves(FanProfile profile, List<int> curve)
    {
        profile.CpuFan1Curve = [.. curve];
        profile.CpuFan2Curve = [.. curve];
        profile.CpuCurve = [.. curve];
    }

    private static void SetBothGpuCurves(FanProfile profile, List<int> curve)
    {
        profile.GpuFan1Curve = [.. curve];
        profile.GpuFan2Curve = [.. curve];
        profile.GpuCurve = [.. curve];
    }

    private static List<int> NormalizeProfileCurve(IReadOnlyList<int>? values, IReadOnlyList<int>? legacyValues, int expectedLength, IReadOnlyList<int> fallback)
    {
        var source = values is { Count: > 0 } ? values : legacyValues;
        return EnforceNonDecreasing(NormalizeCurve(source, expectedLength, fallback));
    }

    private static List<int> NormalizeCurve(IReadOnlyList<int>? values, int expectedLength, IReadOnlyList<int> fallback)
    {
        if (values is null || values.Count != expectedLength)
            return [.. fallback];
        return values.Select(value => SnapRpm(value)).ToList();
    }

    private static double PickAllowed(double value, IReadOnlyList<double> allowed, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return fallback;

        return allowed
            .OrderBy(candidate => Math.Abs(candidate - value))
            .FirstOrDefault();
    }

    private static int NormalizeMinimum(int value) =>
        Math.Clamp(
            SnapRpm(value),
            AbsoluteMinimumFanRpm,
            AbsoluteMaximumFanRpm - 100);

    private static int NormalizeMaximum(int minimum, int maximum)
    {
        var normalizedMinimum = NormalizeMinimum(minimum);
        var normalizedMaximum = Math.Clamp(
            SnapRpm(maximum),
            AbsoluteMinimumFanRpm + 100,
            AbsoluteMaximumFanRpm);
        return normalizedMaximum > normalizedMinimum
            ? normalizedMaximum
            : Math.Min(
                AbsoluteMaximumFanRpm,
                normalizedMinimum + 100);
    }

    private static int EnsureGameAtLeastNormal(int normal, int game, int minimum, int maximum)
    {
        if (game == 0 || normal == 0 || game >= normal)
            return game;
        return ClampFixedRpm(normal, minimum, maximum);
    }

    private static FixedRpmSettings MigrateLegacyFixedRpm(string settingsJson, FixedRpmSettings current)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.FixedRpm), out var fixedRpm))
                return current;

            CopyLegacyPair(fixedRpm, "PowerSavingNormalRpm", value =>
            {
                current.PowerSavingNormalFan1Rpm = value;
                current.PowerSavingNormalFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "PowerSavingGameRpm", value =>
            {
                current.PowerSavingGameFan1Rpm = value;
                current.PowerSavingGameFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "IntelligentNormalRpm", value =>
            {
                current.IntelligentNormalFan1Rpm = value;
                current.IntelligentNormalFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "IntelligentGameRpm", value =>
            {
                current.IntelligentGameFan1Rpm = value;
                current.IntelligentGameFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "PerformanceNormalRpm", value =>
            {
                current.PerformanceNormalFan1Rpm = value;
                current.PerformanceNormalFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "PerformanceGameRpm", value =>
            {
                current.PerformanceGameFan1Rpm = value;
                current.PerformanceGameFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "GeekNormalRpm", value =>
            {
                current.GeekNormalFan1Rpm = value;
                current.GeekNormalFan2Rpm = value;
            });
            CopyLegacyPair(fixedRpm, "GeekGameRpm", value =>
            {
                current.GeekGameFan1Rpm = value;
                current.GeekGameFan2Rpm = value;
            });
        }
        catch
        {
        }

        return current;
    }

    private static void CopyLegacyPair(JsonElement fixedRpm, string propertyName, Action<int> setter)
    {
        if (fixedRpm.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value))
            setter(value);
    }

    private static double NormalizeSmoothingSamples(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return fallback;

        if (value < 1)
        {
            var oldAlpha = 1 - Math.Max(0, Math.Min(0.95, value));
            value = (2.0 / oldAlpha) - 1.0;
        }

        return PickAllowed(value, [1, 2, 3, 5, 10], fallback);
    }

    private static string NormalizeHotkey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    internal static List<string> NormalizeApplicationPaths(
        IEnumerable<string>? paths) =>
        (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try { return Path.GetFullPath(path.Trim()); }
                catch { return path.Trim(); }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<int> CurveFromAnchors(int[] temps, IReadOnlyList<(int Temp, int Rpm)> anchors)
    {
        return temps.Select(temp => Interpolate(anchors.Select(item => item.Temp).ToArray(), anchors.Select(item => item.Rpm).ToArray(), temp)).ToList();
    }
}
