using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed record FeatureAvailability(
    string Id,
    string Category,
    string Name,
    bool Available,
    string Detail,
    bool PartiallyAvailable = false,
    string? EnglishDetail = null)
{
    public bool Usable => Available || PartiallyAvailable;
}

internal sealed class FeatureAvailabilityReport
{
    private readonly IReadOnlyDictionary<string, FeatureAvailability> _features;

    public FeatureAvailabilityReport(IEnumerable<FeatureAvailability> features)
    {
        Items = features.ToArray();
        _features = Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<FeatureAvailability> Items { get; }

    public bool IsAvailable(string id) =>
        _features.TryGetValue(id, out var feature) && feature.Usable;

    public bool IsFullyAvailable(string id) =>
        _features.TryGetValue(id, out var feature) && feature.Available;

    public bool IsPartiallyAvailable(string id) =>
        _features.TryGetValue(id, out var feature) &&
        feature.PartiallyAvailable;

    public bool AnyAvailable(params string[] ids) => ids.Any(IsAvailable);
}

internal static class FeatureIds
{
    public const string TemperatureMonitoring = "monitor.temperature";
    public const string FanControl = "performance.fan";
    public const string SleepFanControl = "performance.fan.sleep";
    public const string PerformanceMode = "performance.its";
    public const string GpuMode = "performance.gpu";
    public const string DiscreteGpuManagement =
        "performance.discrete-gpu-management";
    public const string GpuOverclock = "performance.gpu-overclock";
    public const string PowerSettings = "performance.power";
    public const string BatteryChargeMode = "battery.charge";
    public const string OvernightCharging = "battery.overnight";
    public const string AlwaysOnUsb = "battery.usb";
    public const string FlipToStart = "battery.flip";
    public const string BatteryInformation = "battery.info";
    public const string VantageEyeCare = "display.vantage-eye-care";
    public const string PcManagerEyeCare = "display.pc-manager-eye-care";
    public const string ColorManagement = "display.color";
    public const string DisplayRefreshRate = "display.refresh-rate";
    public const string DolbyAtmos = "sound.dolby";
    public const string SpeakerNoiseCancellation = "sound.speaker-noise";
    public const string MicrophoneNoiseCancellation = "sound.microphone-noise";
    public const string KeyboardBacklight = "input.keyboard-backlight";
    public const string KeyboardBacklightAutoOff = "input.keyboard-auto-off";
    public const string FunctionLock = "input.fn-lock";
    public const string CapsLockOsd = "input.caps-osd";
    public const string NumLockOsd = "input.num-osd";
    public const string FnCtrlSwap = "input.fn-ctrl";
    public const string Touchpad = "input.touchpad";
    public const string FnKeyTakeover = "input.fn-key-takeover";
    public const string DeviceInformation = "device.information";
    public const string WarrantyInformation = "device.warranty";
    public const string BootLogo = "advanced.boot-logo";
    public const string BiosSetup = "advanced.bios-setup";
    public const string StartupInterrupt = "advanced.startup-interrupt";
    public const string SecureWipe = "advanced.secure-wipe";
    public const string UpdateCheck = "settings.update-check";
}

internal static class FeatureAvailabilityCache
{
    public static FeatureAvailabilityReport? Current { get; set; }
}

internal static class FeatureAvailabilityService
{
    public static Task<FeatureAvailabilityReport> DetectAsync() =>
        Task.Run(Detect);

    private static FeatureAvailabilityReport Detect()
    {
        var result = new List<FeatureAvailability>();

        Probe(result, FeatureIds.TemperatureMonitoring, "监控", "温度与功耗监控", () =>
        {
            // CPU and memory probing is sufficient here. Starting the GPU
            // worker before Hybrid Auto protection is initialized could keep
            // the discrete adapter connected during an unplug transition.
            using var reader = new TemperatureReader(enableGpuTelemetry: false);
            _ = reader.Read();
            return "LibreHardwareMonitor 可用";
        });

        FanController? fanController = null;
        try
        {
            fanController = new FanController();
            _ = fanController.ReadSnapshot();
            result.Add(new(
                FeatureIds.FanControl,
                "散热",
                "风扇监控与控制",
                true,
                "风扇后端已连接",
                EnglishDetail: "Fan backend connected"));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.FanControl,
                "散热",
                "风扇监控与控制",
                false,
                ex.Message));
        }

        result.Add(new(
            FeatureIds.SleepFanControl,
            "散热",
            "睡眠时关闭风扇控制",
            fanController?.SupportsDisableControlOnSleep == true,
            fanController is null
                ? "风扇后端不可用"
                : fanController.SupportsDisableControlOnSleep
                    ? "当前风扇控制方式支持"
                    : "当前风扇控制方式不支持"));

        try
        {
            var detector = new ItsModeDetector();
            result.Add(CreatePerformanceModeAvailability(
                detector.ReadMode(),
                detector.IsModeSwitchSupported()));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.PerformanceMode,
                "性能",
                "性能模式",
                false,
                ex.Message));
        }

        Probe(result, FeatureIds.GpuMode, "性能", "GPU 工作模式", () =>
        {
            var state = GpuModeController.ReadState();
            if (state.SupportedModes.Count == 0)
                throw new NotSupportedException("没有检测到可切换的 GPU 模式");
            return $"支持 {state.SupportedModes.Count} 种模式";
        });

        var hasNvidiaGpu = HasNvidiaDisplayAdapter();
        AddState(
            result,
            FeatureIds.DiscreteGpuManagement,
            "性能",
            "独立显卡状态与占用应用",
            hasNvidiaGpu,
            hasNvidiaGpu
                ? "检测到 NVIDIA 独立显卡"
                : "未检测到 NVIDIA 独立显卡");
        AddState(
            result,
            FeatureIds.GpuOverclock,
            "性能",
            "独立显卡超频",
            hasNvidiaGpu,
            hasNvidiaGpu
                ? "NVAPI 控制组件可用"
                : "未检测到 NVIDIA 独立显卡");

        try
        {
            var power = PowerSettingsController.ReadState();
            var profile = PowerSettingsController.CurrentProfile;
            var readableCount = Enum.GetValues<PowerSetting>()
                .Count(power.IsAvailable);
            var required =
                PowerSettingsController.RequiredSettingsForFullAvailability(profile);
            var fullyWritable = profile.Writable &&
                (power.AvailableSettings & required) == required;
            var atppAvailable = power.IsAvailable(PowerSetting.Atpp);
            result.Add(new(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                fullyWritable,
                fullyWritable
                    ? atppAvailable ? "功耗设置可用，ATPP 可调整。" : "功耗设置可用。"
                    : $"{readableCount} 项功耗参数可读取。",
                PartiallyAvailable: readableCount > 0 && !fullyWritable,
                EnglishDetail: fullyWritable && atppAvailable
                    ? "ATPP is adjustable."
                    : null));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                false,
                ex.Message));
        }

        try
        {
            var battery = BatterySettingsController.ReadState(refreshFlipToStart: true);
            AddNullable(result, FeatureIds.BatteryChargeMode, "电池与电源", "充电模式", battery.ChargeMode, "EnergyDrv 不支持");
            AddNullable(result, FeatureIds.OvernightCharging, "电池与电源", "隔夜充电", battery.OvernightCharging, "EnergyDrv 不支持");
            AddNullable(result, FeatureIds.AlwaysOnUsb, "电池与电源", "保持 USB 供电", battery.AlwaysOnUsb, "EnergyDrv 不支持");
            AddNullable(result, FeatureIds.FlipToStart, "电池与电源", "开盖启动", battery.FlipToStart, "固件接口不支持");
        }
        catch (Exception ex)
        {
            foreach (var (id, name) in new[]
                     {
                         (FeatureIds.BatteryChargeMode, "充电模式"),
                         (FeatureIds.OvernightCharging, "隔夜充电"),
                         (FeatureIds.AlwaysOnUsb, "保持 USB 供电"),
                         (FeatureIds.FlipToStart, "开盖启动")
                     })
            {
                result.Add(new(id, "电池与电源", name, false, ex.Message));
            }
        }

        Probe(result, FeatureIds.BatteryInformation, "电池与电源", "电池详细信息", () =>
        {
            _ = BatteryInformationReader.Read();
            return "电池设备信息可读";
        });

        var defaults = new PcManagerEyeCareDefaults(
            PcManagerEyeCareController.FactoryNormalTemperature,
            PcManagerEyeCareController.FactoryEyeCareTemperature);
        try
        {
            var display = DisplaySettingsController.ReadState(defaults);
            AddState(result, FeatureIds.VantageEyeCare, "显示", "Vantage 护眼模式", display.EyeCare.Available, display.EyeCare.Error);
            AddState(result, FeatureIds.PcManagerEyeCare, "显示", "电脑管家护眼模式", display.PcManagerEyeCare.Available, display.PcManagerEyeCare.Error);
            AddState(result, FeatureIds.ColorManagement, "显示", "色彩管理", display.ColorManagement.Available, display.ColorManagement.Error);
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "显示", ex.Message,
                (FeatureIds.VantageEyeCare, "Vantage 护眼模式"),
                (FeatureIds.PcManagerEyeCare, "电脑管家护眼模式"),
                (FeatureIds.ColorManagement, "色彩管理"));
        }
        finally
        {
            DisplaySettingsController.Shutdown();
        }

        if (RefreshRateController.TryReadState(
                out var refreshRate,
                out var refreshRateError) &&
            refreshRate is not null)
        {
            AddState(
                result,
                FeatureIds.DisplayRefreshRate,
                "显示",
                "笔记本屏幕刷新率切换",
                refreshRate.AvailableHz.Count > 1,
                refreshRate.AvailableHz.Count > 1
                    ? $"支持 {refreshRate.AvailableHz.Count} 种刷新率"
                    : "当前显示模式只有一种刷新率");
        }
        else
        {
            AddState(
                result,
                FeatureIds.DisplayRefreshRate,
                "显示",
                "笔记本屏幕刷新率切换",
                false,
                refreshRateError);
        }

        try
        {
            var sound = SoundSettingsController.ReadState();
            AddState(result, FeatureIds.DolbyAtmos, "声音", "Dolby Atmos", sound.Dolby.Available, sound.Dolby.Error);
            AddState(result, FeatureIds.SpeakerNoiseCancellation, "声音", "扬声器降噪", sound.SpeakerNoise.Available, sound.SpeakerNoise.Error);
            AddState(result, FeatureIds.MicrophoneNoiseCancellation, "声音", "麦克风降噪", sound.MicrophoneNoise.Available, sound.MicrophoneNoise.Error);
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "声音", ex.Message,
                (FeatureIds.DolbyAtmos, "Dolby Atmos"),
                (FeatureIds.SpeakerNoiseCancellation, "扬声器降噪"),
                (FeatureIds.MicrophoneNoiseCancellation, "麦克风降噪"));
        }
        finally
        {
            SoundSettingsController.Shutdown();
        }

        try
        {
            var keyboard = KeyboardBacklightController.ReadState();
            result.Add(new(
                FeatureIds.KeyboardBacklight,
                "输入设备",
                "键盘背光",
                keyboard.Level.HasValue,
                keyboard.Level.HasValue
                    ? $"当前亮度：{keyboard.Level}"
                    : "未检测到可用亮度档位"));
            result.Add(new(
                FeatureIds.KeyboardBacklightAutoOff,
                "输入设备",
                "键盘背光自动关闭",
                keyboard.AutoOffSupported,
                keyboard.AutoOffSupported ? "固件支持" : "固件不支持"));
        }
        catch (Exception ex)
        {
            result.Add(new(FeatureIds.KeyboardBacklight, "输入设备", "键盘背光", false, ex.Message));
            result.Add(new(FeatureIds.KeyboardBacklightAutoOff, "输入设备", "键盘背光自动关闭", false, ex.Message));
        }

        var fnKeyTakeoverAvailable = false;
        try
        {
            using var energyDriver = new LenovoEnergyDriver();
            fnKeyTakeoverAvailable = true;
            AddState(
                result,
                FeatureIds.FnKeyTakeover,
                "输入设备",
                "Fn 快捷键接管",
                true,
                "EnergyDrv 与 Toolkit OSD 可用");
        }
        catch (Exception ex)
        {
            AddState(
                result,
                FeatureIds.FnKeyTakeover,
                "输入设备",
                "Fn 快捷键接管",
                false,
                ex.Message);
        }

        try
        {
            var input = InputSettingsController.ReadState(refreshWmiState: true);
            AddToggle(result, FeatureIds.FunctionLock, "功能锁定", input.FunctionLock);
            AddState(
                result,
                FeatureIds.CapsLockOsd,
                "输入设备",
                "CapsLock OSD",
                input.CapsLockOsd.Supported || fnKeyTakeoverAvailable,
                input.CapsLockOsd.Supported
                    ? input.CapsLockOsd.Error
                    : fnKeyTakeoverAvailable
                        ? "可由 Toolkit 快捷键接管提供"
                        : input.CapsLockOsd.Error);
            AddState(
                result,
                FeatureIds.NumLockOsd,
                "输入设备",
                "NumLock OSD",
                input.NumLockOsd.Supported || fnKeyTakeoverAvailable,
                input.NumLockOsd.Supported
                    ? input.NumLockOsd.Error
                    : fnKeyTakeoverAvailable
                        ? "可由 Toolkit 快捷键接管提供"
                        : input.NumLockOsd.Error);
            AddToggle(result, FeatureIds.FnCtrlSwap, "Fn/Ctrl 互换", input.FnCtrlSwap);
            AddToggle(result, FeatureIds.Touchpad, "触摸板", input.Touchpad);
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "输入设备", ex.Message,
                (FeatureIds.FunctionLock, "功能锁定"),
                (FeatureIds.FnCtrlSwap, "Fn/Ctrl 互换"),
                (FeatureIds.Touchpad, "触摸板"));
            AddState(
                result,
                FeatureIds.CapsLockOsd,
                "输入设备",
                "CapsLock OSD",
                fnKeyTakeoverAvailable,
                fnKeyTakeoverAvailable
                    ? "可由 Toolkit 快捷键接管提供"
                    : ex.Message);
            AddState(
                result,
                FeatureIds.NumLockOsd,
                "输入设备",
                "NumLock OSD",
                fnKeyTakeoverAvailable,
                fnKeyTakeoverAvailable
                    ? "可由 Toolkit 快捷键接管提供"
                    : ex.Message);
        }

        try
        {
            var identity = DeviceInformationService.ReadIdentity();
            AddState(result, FeatureIds.DeviceInformation, "设备", "设备详细信息", !string.IsNullOrWhiteSpace(identity.Model), identity.Model);
            AddState(result, FeatureIds.WarrantyInformation, "设备", "保修信息", !string.IsNullOrWhiteSpace(identity.SerialNumber), "需要设备序列号及联网查询");
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "设备", ex.Message,
                (FeatureIds.DeviceInformation, "设备详细信息"),
                (FeatureIds.WarrantyInformation, "保修信息"));
        }

        try
        {
            var bios = BiosAdvancedController.ReadSupport();
            AddState(result, FeatureIds.BootLogo, "高级工具", "开机画面自定义", bios.LogoDiy, "固件能力");
            AddState(result, FeatureIds.BiosSetup, "高级工具", "进入 BIOS", bios.SetupUtility, "固件能力");
            AddState(result, FeatureIds.StartupInterrupt, "高级工具", "启动中断菜单", bios.InterruptMenu, "固件能力");
            AddState(result, FeatureIds.SecureWipe, "高级工具", "安全擦除", bios.SecureWipe, "固件能力");
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "高级工具", ex.Message,
                (FeatureIds.BootLogo, "开机画面自定义"),
                (FeatureIds.BiosSetup, "进入 BIOS"),
                (FeatureIds.StartupInterrupt, "启动中断菜单"),
                (FeatureIds.SecureWipe, "安全擦除"));
        }

        AddState(
            result,
            FeatureIds.UpdateCheck,
            "设置",
            "软件更新检查",
            true,
            "通过 GitHub Releases 检查最新正式版本");

        return new FeatureAvailabilityReport(result);
    }

    internal static FeatureAvailability CreatePerformanceModeAvailability(
        ItsMode currentMode,
        bool switchSupported)
    {
        if (currentMode == ItsMode.Unknown)
        {
            return new(
                FeatureIds.PerformanceMode,
                "性能",
                "性能模式",
                false,
                "无法读取当前性能模式",
                EnglishDetail: "The current performance mode could not be read");
        }

        return new(
            FeatureIds.PerformanceMode,
            "性能",
            "性能模式",
            switchSupported,
            switchSupported
                ? $"当前模式：{currentMode}"
                : $"当前模式：{currentMode}；切换接口不可用",
            PartiallyAvailable: !switchSupported,
            EnglishDetail: switchSupported
                ? $"Current mode: {currentMode}"
                : $"Current mode: {currentMode}; switching is unavailable");
    }

    private static bool HasNvidiaDisplayAdapter()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity " +
                "WHERE PNPClass = 'Display'");
            using var items = searcher.Get();
            foreach (System.Management.ManagementObject item in items)
            {
                using (item)
                {
                    if (Convert.ToString(item["Name"])?.Contains(
                            "NVIDIA",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static void Probe(
        ICollection<FeatureAvailability> result,
        string id,
        string category,
        string name,
        Func<string> probe)
    {
        try
        {
            result.Add(new(id, category, name, true, probe()));
        }
        catch (Exception ex)
        {
            result.Add(new(id, category, name, false, ex.Message));
        }
    }

    private static void AddNullable<T>(
        ICollection<FeatureAvailability> result,
        string id,
        string category,
        string name,
        T? value,
        string unavailableDetail)
        where T : struct =>
        result.Add(new(
            id,
            category,
            name,
            value.HasValue,
            value.HasValue ? "可用" : unavailableDetail));

    private static void AddState(
        ICollection<FeatureAvailability> result,
        string id,
        string category,
        string name,
        bool available,
        string? detail) =>
        result.Add(new(
            id,
            category,
            name,
            available,
            string.IsNullOrWhiteSpace(detail)
                ? available ? "可用" : "不可用"
                : detail));

    private static void AddToggle(
        ICollection<FeatureAvailability> result,
        string id,
        string name,
        ToggleSettingState state) =>
        AddState(result, id, "输入设备", name, state.Supported, state.Error);

    private static void AddFailureGroup(
        ICollection<FeatureAvailability> result,
        string category,
        string detail,
        params (string Id, string Name)[] features)
    {
        foreach (var feature in features)
            result.Add(new(feature.Id, category, feature.Name, false, detail));
    }
}
