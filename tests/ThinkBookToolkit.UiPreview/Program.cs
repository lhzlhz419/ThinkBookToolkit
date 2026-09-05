using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiPreview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--configure-probe-gpu" &&
            Enum.TryParse<HardwareAccelerationMode>(args[1], true, out var mode))
        {
            HardwareAccelerationManager.SetWindowsGpuPreference(mode);
            return;
        }
        if (args.Length >= 2 && args[0] == "--background-memory-probe")
        {
            RunBackgroundMemoryProbe(
                args[1],
                args.Length >= 3 && int.TryParse(args[2], out var blur)
                    ? blur
                    : 0,
                args.Length >= 4 && int.TryParse(args[3], out var seconds)
                    ? seconds
                    : 5);
            return;
        }
        if (args.Length >= 2 && args[0] == "--toolkit-background-memory-probe")
        {
            RunToolkitBackgroundMemoryProbe(
                args[1],
                args.Length >= 3 && int.TryParse(args[2], out var blur)
                    ? blur
                    : 0);
            return;
        }
        if (args.Length >= 2 && args[0] == "--background-first-frame-probe")
        {
            RunBackgroundFirstFrameProbe(args[1]);
            return;
        }
        if (args.Length >= 1 && args[0] == "--cpu-frequency-probe")
        {
            var classes = TemperatureReader
                .HybridCoreEfficiencyClassesForTesting();
            Console.WriteLine(
                "PROBE cpu-classes=" + string.Join(
                    ",",
                    classes.OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key}:{pair.Value}")));
            using var reader = new TemperatureReader(enableGpuTelemetry: false);
            for (var index = 0; index < 5; index++)
            {
                var snapshot = reader.Read();
                Console.WriteLine(
                    $"PROBE cpu-frequency average={snapshot.CpuAverageClockMhz:0.##}; " +
                    $"performance={snapshot.CpuPerformanceCoreAverageClockMhz:0.##}; " +
                    $"efficiency={snapshot.CpuEfficiencyCoreAverageClockMhz:0.##}; " +
                    $"maximum={snapshot.CpuMaximumClockMhz:0.##}");
                System.Threading.Thread.Sleep(500);
            }
            return;
        }
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

    private static void RunBackgroundMemoryProbe(
        string path,
        int blur,
        int activeSeconds)
    {
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        var background = new AnimatedBackgroundImage();
        background.PlaybackFailed += (_, error) =>
            Console.WriteLine("PROBE video-error=" + error);
        var host = new Grid { ClipToBounds = true };
        host.Children.Add(background);
        var window = new Window
        {
            Title = $"Background memory probe · blur {blur}",
            Width = 1600,
            Height = 900,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = host
        };
        window.Loaded += async (_, _) =>
        {
            await Task.Delay(1000);
            PrintMemory("empty-window", blur, 0);
            background.SetViewport(new Size(1600, 900));
            background.SetSizeMode(BackgroundImageSizeMode.Stretch);
            background.LoadFile(path);
            background.SetBlurRadius(blur);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(activeSeconds, 1, 60)));
            PrintMemory("active", blur, background.EstimatedManagedGifBytesForTesting);
            Console.WriteLine(
                "PROBE video=" + background.VideoDiagnosticsForTesting);
            SaveProbeFrame(background.Source);
            SaveProbeWindow(window);
            background.Clear();
            host.Children.Clear();
            window.Content = null;
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            await window.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, false);
            if (PresentationSource.FromVisual(window) is HwndSource restored)
                restored.CompositionTarget.RenderMode = RenderMode.Default;
            await Task.Delay(1000);
            PrintMemory("cleared", blur, 0);
            window.Close();
        };
        PrintMemory("baseline", blur, 0);
        application.Run(window);
    }

    private static void RunToolkitBackgroundMemoryProbe(string path, int blur)
    {
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "dark",
            HardwareAccelerationMode = HardwareAccelerationMode.PowerSaving,
            BackgroundImageSizeMode = BackgroundImageSizeMode.Stretch,
            BackgroundImageOpacityPercent = 30,
            CloseToTray = false
        };
        ModernTheme.Apply(application, true);
        using var runtime = new ToolkitRuntimeService(settings);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false)
        {
            Title = $"Toolkit background probe · blur {blur}",
            Width = 1600,
            Height = 900,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        runtime.SetReportForTesting(CreateReport());
        window.Loaded += async (_, _) =>
        {
            await Task.Delay(1500);
            PrintMemory("toolkit-empty", blur, 0);
            settings.BackgroundImagePath = path;
            settings.BackgroundImageBlurRadius = blur;
            typeof(ToolkitMainWindow).GetMethod(
                    "OnBackgroundImageChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            await Task.Delay(5000);
            var background = typeof(ToolkitMainWindow).GetField(
                    "_backgroundImage",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window) as AnimatedBackgroundImage;
            PrintMemory(
                "toolkit-active",
                blur,
                background?.EstimatedManagedGifBytesForTesting ?? 0);
            settings.BackgroundImagePath = string.Empty;
            settings.BackgroundImageBlurRadius = 0;
            typeof(ToolkitMainWindow).GetMethod(
                    "OnBackgroundImageChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            await Task.Delay(2000);
            PrintMemory("toolkit-cleared", blur, 0);
            runtime.RequestExit();
            window.Close();
        };
        application.Run(window);
    }

    private static void RunBackgroundFirstFrameProbe(string path)
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        using var background = new AnimatedBackgroundImage();
        background.PlaybackFailed += (_, error) =>
            Console.WriteLine("PROBE video-error=" + error);
        var host = new Grid { ClipToBounds = true };
        host.Children.Add(background);
        var window = new Window
        {
            Title = "Background first-frame probe",
            Width = 960,
            Height = 600,
            Content = host,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Loaded += async (_, _) =>
        {
            background.SetViewport(window.RenderSize);
            background.SetSizeMode(BackgroundImageSizeMode.Stretch);
            background.LoadFile(path, enableAnimatedPlayback: false);
            await Task.Delay(3000);
            SaveProbeFrame(background.Source);
            SaveProbeWindow(window);
            Console.WriteLine(
                "PROBE preview=" +
                (background.Source is BitmapSource bitmap
                    ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}"
                    : "missing"));
            window.Close();
        };
        application.Run(window);
    }

    private static void PrintMemory(string phase, int blur, long gifBytes)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(
            $"PROBE phase={phase}; blur={blur}; " +
            $"private={process.PrivateMemorySize64 / 1048576d:0.0} MB; " +
            $"working={process.WorkingSet64 / 1048576d:0.0} MB; " +
            $"managed={GC.GetTotalMemory(false) / 1048576d:0.0} MB; " +
            $"gifBuffers={gifBytes / 1048576d:0.0} MB; " +
            $"cpu={process.TotalProcessorTime.TotalMilliseconds:0} ms");
    }

    private static void SaveProbeFrame(ImageSource? source)
    {
        if (source is not BitmapSource bitmap)
            return;
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ThinkBookToolkit-background-probe.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("PROBE frame=" + path);
    }

    private static void SaveProbeWindow(Window window)
    {
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ThinkBookToolkit-background-window-probe.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("PROBE window=" + path);
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
        "performance" when id is FeatureIds.FanControl or FeatureIds.SleepFanControl => "散热",
        "performance" => "性能",
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
