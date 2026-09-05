using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
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
    public const string FanFullSpeed = "performance.fan.full-speed";
    public const string SleepFanControl = "performance.fan.sleep";
    public const string PerformanceMode = "performance.its";
    public const string GpuMode = "performance.gpu";
    public const string DiscreteGpuManagement =
        "performance.discrete-gpu-management";
    public const string GpuOverclock = "performance.gpu-overclock";
    public const string PowerSettings = "performance.power";
    public const string NvApiGpuPower = "performance.nvapi-gpu-power";
    public const string IntelMmioCpuPower = "performance.intel-mmio-cpu-power";
    public const string AmdZenStatesCpuPower = "performance.amd-zenstates-cpu-power";
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
    public const string BiosIoControl = "advanced.io-control";
    public const string DriverUpdate = "driver-update.system-update";
    public const string Automation = "automation.editor";
    public const string KeyboardMacros = "automation.keyboard-macros";
    public const string UpdateCheck = "settings.update-check";
    public const string Osd = "settings.osd";
    public const string DataSharing = "settings.data-sharing";
}

internal static class FeatureAvailabilityCache
{
    public static FeatureAvailabilityReport? Current { get; set; }
}

internal static class FeatureAvailabilityDiagnostics
{
    public static IReadOnlyList<string> DescribeIssues(
        FeatureAvailabilityReport report) =>
        report.Items
            .Where(feature =>
                feature.PartiallyAvailable || !feature.Available)
            .Select(feature =>
            {
                var status = feature.PartiallyAvailable
                    ? "partially available"
                    : "unavailable";
                var reason = Normalize(feature.Detail);
                if (reason.Length == 0)
                    reason = "The feature probe returned no reason.";
                return $"Feature {status}: [{feature.Category}] " +
                       $"{feature.Name} ({feature.Id}); reason: {reason}";
            })
            .ToArray();

    public static void LogIssues(FeatureAvailabilityReport report)
    {
        foreach (var message in DescribeIssues(report))
            ToolkitLog.Warning(message);
    }

    private static string Normalize(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty).Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
}

internal static class FeatureAvailabilityService
{
    public static Task<FeatureAvailabilityReport> DetectAsync() =>
        Task.Run(Detect);

    private static FeatureAvailabilityReport Detect()
    {
        ToolkitLog.Info("Feature detection started.");
        var result = new FeatureDetectionCollector();

        Probe(result, FeatureIds.TemperatureMonitoring, "监控", "温度与功耗监控", () =>
        {
            // Opening LibreHardwareMonitor is expensive and the real reader is
            // initialized immediately after the overview becomes available.
            // Loading the assembly here verifies the dependency without doing
            // the same full hardware enumeration twice during startup.
            _ = typeof(Computer).Assembly.GetName();
            return "LibreHardwareMonitor 组件已加载";
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
                ExceptionDetail(ex)));
        }

        if (fanController is null)
        {
            result.Add(new(
                FeatureIds.FanFullSpeed,
                "散热",
                "风扇拉满",
                false,
                "风扇后端不可用",
                EnglishDetail: "The fan backend is unavailable"));
        }
        else
        {
            try
            {
                var fullSpeedAvailable =
                    fanController.TryProbeFullSpeedControl(out var detail);
                result.Add(new(
                    FeatureIds.FanFullSpeed,
                    "散热",
                    "风扇拉满",
                    fullSpeedAvailable,
                    detail,
                    EnglishDetail: detail));
            }
            catch (Exception ex)
            {
                result.Add(new(
                    FeatureIds.FanFullSpeed,
                    "散热",
                    "风扇拉满",
                    false,
                    ExceptionDetail(ex)));
            }
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

        Probe(result, FeatureIds.PerformanceMode, "性能", "性能模式切换", () =>
        {
            var detector = new ItsModeDetector();
            if (!detector.IsModeSwitchSupported())
                throw new NotSupportedException("Lenovo 性能模式切换接口不可用");
            return $"当前模式：{detector.ReadMode()}";
        });

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
            var nvPcf = NvPcfPowerController.Read();
            var optionalChinese = string.Join("、", new[]
            {
                nvPcf.DynamicBoostEnabled.HasValue ? "Dynamic Boost" : null,
                nvPcf.GpuTemperatureLimitC.HasValue ? "GPU 温度墙" : null
            }.Where(value => value is not null));
            var optionalEnglish = string.Join(", ", new[]
            {
                nvPcf.DynamicBoostEnabled.HasValue ? "Dynamic Boost" : null,
                nvPcf.GpuTemperatureLimitC.HasValue ? "GPU thermal limit" : null
            }.Where(value => value is not null));
            result.Add(new(
                FeatureIds.NvApiGpuPower,
                "性能",
                "NVAPI GPU 功耗调整（Beta）",
                true,
                $"已读取 4 项 NVPCF 功耗参数" +
                (optionalChinese.Length > 0
                    ? $"；附加可用：{optionalChinese}"
                    : string.Empty) +
                $"；布局：{nvPcf.LayoutName}",
                EnglishDetail:
                    "All four NVPCF power values are readable" +
                    (optionalEnglish.Length > 0
                        ? $"; additionally available: {optionalEnglish}"
                        : string.Empty) +
                    $"; layout: {nvPcf.LayoutName}"));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.NvApiGpuPower,
                "性能",
                "NVAPI GPU 功耗调整（Beta）",
                false,
                ExceptionDetail(ex)));
        }
        finally
        {
            // Capability detection must not retain an NVAPI client that could
            // prevent Hybrid Auto or Hybrid iGPU mode from ejecting the dGPU.
            NvPcfPowerController.Shutdown();
        }

        if (CpuVendorDetector.IsIntel)
        {
            try
            {
                var cpu = IntelMmioPowerController.Read();
                result.Add(new(FeatureIds.IntelMmioCpuPower, "性能",
                    "直接调整 CPU MMIO 功耗墙（Beta）", true,
                    $"已读取 PL1、PL2 和 Turbo Time：" +
                    string.Join(", ", cpu.Values.Select(x => $"{x.Key}={x.Value}"))));
            }
            catch (Exception ex)
            {
                result.Add(new(FeatureIds.IntelMmioCpuPower, "性能",
                    "直接调整 CPU MMIO 功耗墙（Beta）", false,
                    ExceptionDetail(ex)));
            }
        }
        if (CpuVendorDetector.IsAmd)
        {
            try
            {
                var cpu = AmdZenStatesPowerController.Read();
                result.Add(new(FeatureIds.AmdZenStatesCpuPower, "性能",
                    "使用 ZenStates-Core 调整 CPU 功耗墙（Beta）", true,
                    $"已读取 {cpu.Kind}：" +
                    string.Join(", ", cpu.Values.Select(x => $"{x.Key}={x.Value}"))));
            }
            catch (Exception ex)
            {
                result.Add(new(FeatureIds.AmdZenStatesCpuPower, "性能",
                    "使用 ZenStates-Core 调整 CPU 功耗墙（Beta）", false,
                    ExceptionDetail(ex)));
            }
        }

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
            var missingRequired = Enum.GetValues<PowerSetting>()
                .Where(setting =>
                    (required & PowerSettingsController.Flag(setting)) != 0 &&
                    !power.IsAvailable(setting))
                .ToArray();
            var powerDetail = fullyWritable
                ? atppAvailable
                    ? "功耗设置可用，ATPP offset 可调整。"
                    : "功耗设置可用。"
                : readableCount == 0
                    ? "没有功耗参数可读取。"
                    : !profile.Writable
                        ? $"{readableCount} 项功耗参数可读取，但当前设备配置不支持写入。"
                        : $"{readableCount} 项功耗参数可读取；缺少必需接口：" +
                          string.Join(", ", missingRequired);
            result.Add(new(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                fullyWritable,
                powerDetail,
                PartiallyAvailable: readableCount > 0 && !fullyWritable,
                EnglishDetail: fullyWritable && atppAvailable
                    ? "ATPP offset is adjustable."
                    : null));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                false,
                ExceptionDetail(ex)));
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
                result.Add(new(
                    id,
                    "电池与电源",
                    name,
                    false,
                    ExceptionDetail(ex)));
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
            AddFailureGroup(result, "显示", ExceptionDetail(ex),
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
                refreshRate.AvailableModes.Count > 1,
                refreshRate.AvailableModes.Count > 1
                    ? $"支持 {refreshRate.AvailableModes.Count} 种刷新率"
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
            AddFailureGroup(result, "声音", ExceptionDetail(ex),
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
            var detail = ExceptionDetail(ex);
            result.Add(new(FeatureIds.KeyboardBacklight, "输入设备", "键盘背光", false, detail));
            result.Add(new(FeatureIds.KeyboardBacklightAutoOff, "输入设备", "键盘背光自动关闭", false, detail));
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
                ExceptionDetail(ex));
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
            var detail = ExceptionDetail(ex);
            AddFailureGroup(result, "输入设备", detail,
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
                    : detail);
            AddState(
                result,
                FeatureIds.NumLockOsd,
                "输入设备",
                "NumLock OSD",
                fnKeyTakeoverAvailable,
                fnKeyTakeoverAvailable
                    ? "可由 Toolkit 快捷键接管提供"
                    : detail);
        }

        try
        {
            var identity = DeviceInformationService.ReadIdentity();
            AddState(
                result,
                FeatureIds.DeviceInformation,
                "设备",
                "设备详细信息",
                !string.IsNullOrWhiteSpace(identity.Model),
                string.IsNullOrWhiteSpace(identity.Model)
                    ? "未能从系统读取设备型号"
                    : identity.Model);
            AddState(result, FeatureIds.WarrantyInformation, "设备", "保修信息", !string.IsNullOrWhiteSpace(identity.SerialNumber), "需要设备序列号及联网查询");
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "设备", ExceptionDetail(ex),
                (FeatureIds.DeviceInformation, "设备详细信息"),
                (FeatureIds.WarrantyInformation, "保修信息"));
        }

        try
        {
            var bios = BiosAdvancedController.ReadSupport();
            AddState(result, FeatureIds.BootLogo, "高级工具", "开机画面自定义", bios.LogoDiy,
                bios.LogoDiy ? "固件接口可用" : "固件未提供开机画面自定义接口");
            AddState(result, FeatureIds.BiosSetup, "高级工具", "进入 BIOS", bios.SetupUtility,
                bios.SetupUtility ? "固件接口可用" : "固件未提供进入 BIOS 接口");
            AddState(result, FeatureIds.StartupInterrupt, "高级工具", "启动中断菜单", bios.InterruptMenu,
                bios.InterruptMenu ? "固件接口可用" : "固件未提供启动中断菜单接口");
            AddState(result, FeatureIds.SecureWipe, "高级工具", "安全擦除", bios.SecureWipe,
                bios.SecureWipe ? "固件接口可用" : "固件未提供安全擦除接口");
        }
        catch (Exception ex)
        {
            AddFailureGroup(result, "高级工具", ExceptionDetail(ex),
                (FeatureIds.BootLogo, "开机画面自定义"),
                (FeatureIds.BiosSetup, "进入 BIOS"),
                (FeatureIds.StartupInterrupt, "启动中断菜单"),
                (FeatureIds.SecureWipe, "安全擦除"));
        }

        try
        {
            var states = BiosIoController.ReadSupportedStates();
            var total = BiosIoController.Definitions.Count;
            var availableIds = states
                .Select(state => state.Definition.Id)
                .ToHashSet(StringComparer.Ordinal);
            var missingIds = BiosIoController.Definitions
                .Where(definition => !availableIds.Contains(definition.Id))
                .Select(definition => definition.Id)
                .ToArray();
            result.Add(new(
                FeatureIds.BiosIoControl,
                "高级工具",
                "IO 控制",
                states.Count == total,
                states.Count == total
                    ? $"BIOS 已提供全部 {total} 项可调设置"
                    : $"BIOS 已提供 {states.Count}/{total} 项可调设置；未提供：" +
                      string.Join(", ", missingIds),
                states.Count > 0 && states.Count < total,
                $"BIOS exposes {states.Count}/{total} configurable settings"));
        }
        catch (Exception ex)
        {
            result.Add(new(
                FeatureIds.BiosIoControl,
                "高级工具",
                "IO 控制",
                false,
                ExceptionDetail(ex)));
        }

        var driverUpdatesAvailable = DriverUpdateController.IsAvailable(
            out var driverUpdateDetail);
        AddState(
            result,
            FeatureIds.DriverUpdate,
            "驱动更新",
            "Lenovo 驱动与固件更新",
            driverUpdatesAvailable,
            driverUpdateDetail);

        AddState(
            result,
            FeatureIds.UpdateCheck,
            "设置",
            "软件更新检查",
            true,
            "通过 GitHub Releases 检查最新正式版本");

        AddState(
            result,
            FeatureIds.Osd,
            "设置",
            "OSD",
            OperatingSystem.IsWindows(),
            OperatingSystem.IsWindows()
                ? "置顶透明 OSD 窗口可用"
                : "OSD 仅支持 Windows");

        AddState(
            result,
            FeatureIds.DataSharing,
            "设置",
            "与其它软件联动",
            System.Net.HttpListener.IsSupported,
            System.Net.HttpListener.IsSupported
                ? "本机回环 HTTP JSON 服务可用"
                : "当前运行环境不支持 HTTP 监听器");

        AddState(
            result,
            FeatureIds.Automation,
            "自动化",
            "自动化与 Fn 快捷键映射",
            true,
            "内置有序步骤执行器可用");

        AddState(
            result,
            FeatureIds.KeyboardMacros,
            "自动化",
            "键盘宏",
            true,
            "内置低级键盘录制与 SendInput 播放器可用");

        ToolkitLog.Info(
            $"Feature detection finished in {result.Elapsed.TotalMilliseconds:0} ms.");
        return new FeatureAvailabilityReport(result.Items);
    }

    private sealed class FeatureDetectionCollector :
        ICollection<FeatureAvailability>
    {
        private readonly List<FeatureAvailability> _items = [];
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastCompletion;

        public IReadOnlyList<FeatureAvailability> Items => _items;
        public TimeSpan Elapsed => _stopwatch.Elapsed;
        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(FeatureAvailability item)
        {
            _items.Add(item);
            var elapsed = _stopwatch.Elapsed;
            var duration = elapsed - _lastCompletion;
            _lastCompletion = elapsed;
            var state = item.PartiallyAvailable
                ? "partially available"
                : item.Available
                    ? "available"
                    : "unavailable";
            ToolkitLog.Info(
                "Feature detection item completed: " +
                $"[{item.Category}] {item.Name} ({item.Id}); " +
                $"state={state}; step={duration.TotalMilliseconds:0} ms; " +
                $"total={elapsed.TotalMilliseconds:0} ms.");
        }

        public void Clear() => _items.Clear();
        public bool Contains(FeatureAvailability item) =>
            _items.Contains(item);
        public void CopyTo(FeatureAvailability[] array, int arrayIndex) =>
            _items.CopyTo(array, arrayIndex);
        public bool Remove(FeatureAvailability item) =>
            _items.Remove(item);
        public IEnumerator<FeatureAvailability> GetEnumerator() =>
            _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
            result.Add(new(id, category, name, false, ExceptionDetail(ex)));
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

    private static string ExceptionDetail(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }
        return messages.Count == 0
            ? exception.GetType().Name
            : string.Join(" -> ", messages);
    }
}
