using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiPreview;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "dark",
            IntervalSeconds = 2,
            CloseToTray = false
        };
        ModernTheme.Apply(application, ToolkitRuntimeService.ResolveDarkTheme(settings.Theme));
        using var runtime = new ToolkitRuntimeService(settings);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false)
        {
            Title = "ThinkBook Toolkit · UI Preview"
        };
        runtime.SetReportForTesting(CreateReport());
        runtime.SetSnapshotForTesting(new ToolkitRuntimeSnapshot(
            new TemperatureSnapshot(63, 51, 58, 28.4, 18.7, "Preview CPU", "Preview GPU", "Preview VRAM")
            {
                CpuName = "Intel Core Ultra 7 255HX",
                CpuLoadPercent = 18.4,
                CpuAverageClockMhz = 2388,
                CpuMaximumClockMhz = 4812,
                GpuName = "NVIDIA GeForce RTX 5060 Laptop GPU",
                GpuLoadPercent = 24.6,
                GpuMemoryLoadPercent = 12.8,
                GpuCoreClockMhz = 1552,
                GpuMemoryClockMhz = 6001,
                GpuHotSpotTempC = 57.4,
                VramChipTemperaturesC = [56, 58, 56, 54],
                PhysicalMemoryUsedGb = 16.5,
                PhysicalMemoryTotalGb = 31.4,
                VirtualMemoryUsedGb = 25.9,
                VirtualMemoryTotalGb = 37.2,
                MemorySlotTemperaturesC = [47],
                StorageDevices =
                [
                    new StorageTemperatureSnapshot("YMTC PC411-1024GB-B", [30, 40, 30]),
                    new StorageTemperatureSnapshot("Samsung SSD 990 PRO", [32, 43, 35])
                ]
            },
            new FanSnapshot(DateTimeOffset.Now, 2200, 2100, new Dictionary<string, FanLimit>()),
            new BatteryInformationSnapshot(
                32.8,
                -11.4,
                -22,
                -65,
                69.1,
                86.9,
                85.8,
                101.23,
                DateTime.Now.AddHours(-1),
                32,
                null,
                null,
                false),
            ItsMode.Intelligent,
            GpuWorkingMode.HybridAuto,
            [GpuWorkingMode.Hybrid, GpuWorkingMode.HybridAuto, GpuWorkingMode.Discrete],
            true,
            false,
            ControlStrategy.FanCurve,
            new FanTargets(2500, 2400),
            string.Empty,
            DateTimeOffset.Now,
            null));
        application.Run(window);
    }

    private static FeatureAvailabilityReport CreateReport() => new(
        typeof(FeatureIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new FeatureAvailability(
                id,
                CategoryFor(id),
                FriendlyName(id),
                true,
                "预览模式：接口可用")));

    private static string CategoryFor(string id) => id.Split('.')[0] switch
    {
        "monitor" => "监控",
        "performance" => "性能与散热",
        "battery" => "电池与电源",
        "display" => "显示",
        "sound" => "声音",
        "input" => "输入设备",
        "device" => "设备",
        "advanced" => "高级工具",
        _ => "其他"
    };

    private static string FriendlyName(string id) => id switch
    {
        FeatureIds.TemperatureMonitoring => "温度与功耗监控",
        FeatureIds.FanControl => "风扇监控与控制",
        FeatureIds.SleepFanControl => "睡眠时关闭风扇控制",
        FeatureIds.PerformanceMode => "性能模式切换",
        FeatureIds.GpuMode => "GPU 工作模式",
        FeatureIds.PowerSettings => "功耗设置",
        FeatureIds.BatteryChargeMode => "充电模式",
        FeatureIds.OvernightCharging => "隔夜充电",
        FeatureIds.AlwaysOnUsb => "保持 USB 供电",
        FeatureIds.FlipToStart => "开盖启动",
        FeatureIds.BatteryInformation => "电池详细信息",
        FeatureIds.VantageEyeCare => "Vantage 护眼模式",
        FeatureIds.PcManagerEyeCare => "电脑管家护眼模式",
        FeatureIds.ColorManagement => "色彩管理",
        FeatureIds.DolbyAtmos => "Dolby Atmos",
        FeatureIds.SpeakerNoiseCancellation => "扬声器降噪",
        FeatureIds.MicrophoneNoiseCancellation => "麦克风降噪",
        FeatureIds.KeyboardBacklight => "键盘背光",
        FeatureIds.KeyboardBacklightAutoOff => "键盘背光自动关闭",
        FeatureIds.FunctionLock => "功能锁定",
        FeatureIds.CapsLockOsd => "CapsLock OSD",
        FeatureIds.NumLockOsd => "NumLock OSD",
        FeatureIds.FnCtrlSwap => "Fn/Ctrl 互换",
        FeatureIds.Touchpad => "触摸板",
        FeatureIds.DeviceInformation => "设备详细信息",
        FeatureIds.WarrantyInformation => "保修信息",
        FeatureIds.BootLogo => "开机画面",
        FeatureIds.BiosSetup => "进入 BIOS",
        FeatureIds.StartupInterrupt => "启动中断菜单",
        FeatureIds.SecureWipe => "安全擦除",
        _ => id
    };
}
