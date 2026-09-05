using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

internal enum AutomationStepInputKind
{
    None,
    Options,
    Integer,
    ApplicationPath
}

internal sealed record AutomationOption(string Value, string Chinese, string English);

internal sealed record AutomationStepMetadata(
    AutomationStepKind Kind,
    string CategoryChinese,
    string CategoryEnglish,
    string NameChinese,
    string NameEnglish,
    AutomationStepInputKind InputKind);

internal static class AutomationStepCatalog
{
    public static IReadOnlyList<AutomationStepMetadata> Items { get; } =
    [
        M(AutomationStepKind.PerformanceMode, "性能", "Performance", "性能模式", "Performance mode", AutomationStepInputKind.Options),
        M(AutomationStepKind.GpuMode, "性能", "Performance", "GPU 模式", "GPU mode", AutomationStepInputKind.Options),
        M(AutomationStepKind.GpuOverclockEnabled, "性能", "Performance", "独立显卡超频开关", "Discrete GPU overclock", AutomationStepInputKind.Options),
        M(AutomationStepKind.KillGpuApplications, "性能", "Performance", "强制关闭独显占用应用", "Force-close dGPU applications", AutomationStepInputKind.None),

        M(AutomationStepKind.FanFullSpeed, "散热", "Cooling", "风扇拉满", "Full fan speed", AutomationStepInputKind.Options),
        M(AutomationStepKind.FanStrategy, "散热", "Cooling", "风扇控制策略", "Fan control strategy", AutomationStepInputKind.Options),
        M(AutomationStepKind.FixedRpmGameMode, "散热", "Cooling", "固定转速状态", "Fixed-RPM state", AutomationStepInputKind.Options),

        M(AutomationStepKind.BatteryChargeMode, "电源", "Power", "充电模式", "Charging mode", AutomationStepInputKind.Options),
        M(AutomationStepKind.OvernightCharging, "电源", "Power", "隔夜电池充电", "Overnight charging", AutomationStepInputKind.Options),
        M(AutomationStepKind.AlwaysOnUsb, "电源", "Power", "保持 USB 供电", "Always-on USB", AutomationStepInputKind.Options),
        M(AutomationStepKind.FlipToStart, "电源", "Power", "开盖启动", "Flip to start", AutomationStepInputKind.Options),

        M(AutomationStepKind.RefreshRate, "显示", "Display", "刷新率", "Refresh rate", AutomationStepInputKind.Options),
        M(AutomationStepKind.VantageEyeCare, "显示", "Display", "Vantage 护眼", "Vantage eye care", AutomationStepInputKind.Options),
        M(AutomationStepKind.PcManagerEyeCare, "显示", "Display", "电脑管家护眼", "PC Manager eye care", AutomationStepInputKind.Options),
        M(AutomationStepKind.ColorManagement, "显示", "Display", "色域调整", "Color gamut", AutomationStepInputKind.Options),
        M(AutomationStepKind.OsdEnabled, "传感器", "Sensors", "OSD 开关", "OSD", AutomationStepInputKind.Options),
        M(AutomationStepKind.OsdLockPosition, "传感器", "Sensors", "OSD 锁定位置", "Lock OSD position", AutomationStepInputKind.Options),
        M(AutomationStepKind.SensorRecordingEnabled, "传感器", "Sensors", "记录传感器信息", "Record sensor data", AutomationStepInputKind.Options),

        M(AutomationStepKind.DolbyEnabled, "声音", "Sound", "Dolby Atmos", "Dolby Atmos", AutomationStepInputKind.Options),
        M(AutomationStepKind.DolbyProfile, "声音", "Sound", "杜比音效", "Dolby profile", AutomationStepInputKind.Options),
        M(AutomationStepKind.SpeakerNoiseCancellation, "声音", "Sound", "扬声器降噪", "Speaker noise cancellation", AutomationStepInputKind.Options),
        M(AutomationStepKind.MicrophoneNoiseMode, "声音", "Sound", "麦克风降噪模式", "Microphone noise mode", AutomationStepInputKind.Options),

        M(AutomationStepKind.KeyboardBacklight, "输入", "Input", "键盘背光亮度", "Keyboard backlight", AutomationStepInputKind.Options),
        M(AutomationStepKind.KeyboardBacklightAutoOff, "输入", "Input", "30 秒无操作后关闭背光", "Turn off backlight after 30 seconds", AutomationStepInputKind.Options),
        M(AutomationStepKind.FunctionLock, "输入", "Input", "功能锁定（FnLock）", "Function lock (FnLock)", AutomationStepInputKind.Options),
        M(AutomationStepKind.CapsLockOsd, "输入", "Input", "CapsLock OSD", "CapsLock OSD", AutomationStepInputKind.Options),
        M(AutomationStepKind.NumLockOsd, "输入", "Input", "NumLock OSD", "NumLock OSD", AutomationStepInputKind.Options),
        M(AutomationStepKind.FnCtrlSwap, "输入", "Input", "Fn/Ctrl 互换", "Swap Fn/Ctrl", AutomationStepInputKind.Options),
        M(AutomationStepKind.Touchpad, "输入", "Input", "触摸板", "Touchpad", AutomationStepInputKind.Options),

        M(AutomationStepKind.ShowToolkitWindow, "应用", "Application", "唤出 ThinkBook Toolkit 窗口", "Show ThinkBook Toolkit window", AutomationStepInputKind.None),
        M(AutomationStepKind.MinimizeToolkitWindow, "应用", "Application", "最小化 ThinkBook Toolkit 窗口", "Minimize ThinkBook Toolkit window", AutomationStepInputKind.None),
        M(AutomationStepKind.ToggleToolkitWindow, "应用", "Application", "唤出或最小化 ThinkBook Toolkit", "Show or minimize ThinkBook Toolkit", AutomationStepInputKind.None),
        M(AutomationStepKind.OpenApplication, "应用", "Application", "打开应用", "Open application", AutomationStepInputKind.ApplicationPath),

        M(AutomationStepKind.Delay, "延迟", "Delay", "延迟", "Delay", AutomationStepInputKind.Integer),

        M(AutomationStepKind.RunMacro, "宏", "Macro", "运行键盘宏", "Run keyboard macro", AutomationStepInputKind.Options)
    ];

    public static AutomationStepMetadata Metadata(AutomationStepKind kind) =>
        Items.First(item => item.Kind == kind);

    public static IReadOnlyList<AutomationOption> Options(
        AutomationStepKind kind,
        ToolkitRuntimeService runtime) => kind switch
    {
        AutomationStepKind.PerformanceMode => EnumOptions<ItsMode>(
            (value, chinese) => value switch
            {
                ItsMode.PowerSaving => chinese ? "省电模式" : "Cool",
                ItsMode.Intelligent => chinese ? "智能模式" : "Auto",
                ItsMode.Performance => chinese ? "性能模式" : "Performance",
                ItsMode.Geek => chinese ? "极客模式" : "Geek",
                _ => value.ToString()
            },
            value => value != ItsMode.Unknown &&
                     ItsModeController.IsModeSupported(value)),
        AutomationStepKind.GpuMode => EnumOptions<GpuWorkingMode>(
            (value, chinese) => GpuModeText.Name(value, chinese),
            _ => true),
        AutomationStepKind.GpuOverclockEnabled or
        AutomationStepKind.FanFullSpeed or
        AutomationStepKind.OvernightCharging or
        AutomationStepKind.FlipToStart or
        AutomationStepKind.VantageEyeCare or
        AutomationStepKind.PcManagerEyeCare or
        AutomationStepKind.DolbyEnabled or
        AutomationStepKind.SpeakerNoiseCancellation or
        AutomationStepKind.KeyboardBacklightAutoOff or
        AutomationStepKind.FunctionLock or
        AutomationStepKind.CapsLockOsd or
        AutomationStepKind.NumLockOsd or
        AutomationStepKind.FnCtrlSwap or
        AutomationStepKind.Touchpad => BooleanOptions(),
        AutomationStepKind.OsdEnabled or
        AutomationStepKind.OsdLockPosition or
        AutomationStepKind.SensorRecordingEnabled => BooleanOptions(),
        AutomationStepKind.FixedRpmGameMode =>
        [
            new("false", "普通", "Normal"),
            new("true", "游戏", "Game")
        ],
        AutomationStepKind.FanStrategy => FanStrategyOptions(),
        AutomationStepKind.BatteryChargeMode => EnumOptions<BatteryChargeMode>(
            (value, chinese) => value switch
            {
                BatteryChargeMode.Conservation => chinese ? "养护" : "Conservation",
                BatteryChargeMode.Normal => chinese ? "普通" : "Normal",
                BatteryChargeMode.RapidCharge => chinese ? "快充" : "Rapid charge",
                _ => value.ToString()
            }, _ => true),
        AutomationStepKind.AlwaysOnUsb => EnumOptions<AlwaysOnUsbMode>(
            (value, chinese) => value switch
            {
                AlwaysOnUsbMode.Off => chinese ? "关闭" : "Off",
                AlwaysOnUsbMode.OnWhenSleeping => chinese ? "睡眠时开启" : "On while sleeping",
                AlwaysOnUsbMode.OnAlways => chinese ? "始终开启" : "Always on",
                _ => value.ToString()
            }, _ => true),
        AutomationStepKind.RefreshRate => RefreshRateOptions(),
        AutomationStepKind.ColorManagement => EnumOptions<ColorManagementMode>(
            (value, _) => value switch
            {
                ColorManagementMode.Default => "Default",
                ColorManagementMode.Srgb => "sRGB",
                ColorManagementMode.AdobeRgb => "Adobe RGB",
                ColorManagementMode.DisplayP3 => "Display P3",
                ColorManagementMode.Native => "Native",
                ColorManagementMode.Auto => "Auto",
                ColorManagementMode.Rec709 => "Rec.709",
                ColorManagementMode.DciP3 => "DCI-P3",
                ColorManagementMode.DicomDim => "DICOM DIM",
                ColorManagementMode.DicomOffice => "DICOM Office",
                _ => value.ToString()
            }, _ => true),
        AutomationStepKind.DolbyProfile => EnumOptions<DolbyProfile>(
            (value, chinese) => chinese ? value switch
            {
                DolbyProfile.Movie => "电影",
                DolbyProfile.Music => "音乐",
                DolbyProfile.Game => "游戏",
                DolbyProfile.Voice => "语音",
                DolbyProfile.Custom => "自定义",
                DolbyProfile.Dynamic => "动态",
                _ => value.ToString()
            } : value.ToString(), _ => true),
        AutomationStepKind.MicrophoneNoiseMode =>
            EnumOptions<MicrophoneNoiseMode>(
                (value, chinese) => chinese ? value switch
                {
                    MicrophoneNoiseMode.Off => "关闭",
                    MicrophoneNoiseMode.MultipleVoices => "多人语音",
                    MicrophoneNoiseMode.Normal => "普通",
                    MicrophoneNoiseMode.VoiceRecognition => "语音识别",
                    MicrophoneNoiseMode.OnlyMyVoice => "仅我的声音",
                    _ => value.ToString()
                } : value.ToString(), _ => true),
        AutomationStepKind.KeyboardBacklight =>
            EnumOptions<KeyboardBacklightLevel>(
                (value, chinese) => chinese ? value switch
                {
                    KeyboardBacklightLevel.Off => "关闭",
                    KeyboardBacklightLevel.Low => "低",
                    KeyboardBacklightLevel.High => "高",
                    KeyboardBacklightLevel.Auto => "自动",
                    _ => value.ToString()
                } : value.ToString(), _ => true),
        AutomationStepKind.RunMacro => runtime.Settings.Macros
            .Select(macro => new AutomationOption(
                macro.Id,
                macro.Name,
                macro.Name))
            .ToArray(),
        _ => []
    };

    public static string DisplayName(
        AutomationStep step,
        ToolkitRuntimeService runtime)
    {
        var metadata = Metadata(step.Kind);
        var name = runtime.IsChinese
            ? metadata.NameChinese
            : metadata.NameEnglish;
        if (metadata.InputKind == AutomationStepInputKind.None)
            return name;
        if (metadata.InputKind == AutomationStepInputKind.Integer)
            return $"{name}: {step.Value} s";
        if (metadata.InputKind == AutomationStepInputKind.ApplicationPath)
            return $"{name}: {step.Value}";
        var option = Options(step.Kind, runtime).FirstOrDefault(item =>
            item.Value.Equals(step.Value, StringComparison.OrdinalIgnoreCase));
        return option is null
            ? $"{name}: {step.Value}"
            : $"{name}: {(runtime.IsChinese ? option.Chinese : option.English)}";
    }

    private static AutomationStepMetadata M(
        AutomationStepKind kind,
        string categoryChinese,
        string categoryEnglish,
        string nameChinese,
        string nameEnglish,
        AutomationStepInputKind inputKind) =>
        new(kind, categoryChinese, categoryEnglish, nameChinese, nameEnglish, inputKind);

    private static IReadOnlyList<AutomationOption> BooleanOptions() =>
    [
        new("true", "开启", "On"),
        new("false", "关闭", "Off"),
        new("toggle", "切换开关", "Toggle")
    ];

    private static IReadOnlyList<AutomationOption> FanStrategyOptions()
    {
        var result = new List<AutomationOption>
        {
            new("FirmwareAutomatic", "固件自动", "Firmware automatic"),
            new("FixedRpm", "固定转速", "Fixed RPM")
        };
        var profiles = CurveProfileStore.Load();
        for (var index = 0; index < profiles.Count; index++)
        {
            result.Add(new(
                $"FanCurve:{index}",
                $"风扇曲线：{profiles[index].Name}",
                $"Fan curve: {profiles[index].Name}"));
        }
        result.Add(new("AdvancedCurve", "高级曲线", "Advanced curve"));
        return result;
    }

    private static IReadOnlyList<AutomationOption> RefreshRateOptions()
    {
        if (!RefreshRateController.TryReadState(out var state, out _) ||
            state is null)
            return [];
        return state.AvailableModes.Select(mode => new AutomationOption(
            mode.IsDynamic
                ? $"dynamic:{mode.Frequency}"
                : $"fixed:{mode.Frequency}",
            mode.DisplayName,
            mode.DisplayName)).ToArray();
    }

    private static IReadOnlyList<AutomationOption> EnumOptions<T>(
        Func<T, bool, string> label,
        Func<T, bool> include)
        where T : struct, Enum =>
        Enum.GetValues<T>()
            .Where(include)
            .Select(value => new AutomationOption(
                value.ToString(),
                label(value, true),
                label(value, false)))
            .ToArray();
}
