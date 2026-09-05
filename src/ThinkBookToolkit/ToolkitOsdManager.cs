using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed class ToolkitOsdManager : IDisposable
{
    private readonly ToolkitRuntimeService _runtime;
    private ToolkitOsdWindow? _window;

    public ToolkitOsdManager(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
    }

    public void Sync()
    {
        if (_runtime.Settings.OsdEnabled)
        {
            _window ??= new ToolkitOsdWindow(_runtime);
            _window.ApplySettings();
            _window.ShowIfSessionUnlocked();
        }
        else if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
    }

    public void ApplySettings()
    {
        if (!_runtime.Settings.OsdEnabled)
            return;
        Sync();
    }

    public void Dispose()
    {
        if (_window is null)
            return;
        _window.Close();
        _window = null;
    }
}

internal sealed class ToolkitOsdWindow : UiAccessOverlayWindow
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<OsdSensor, SensorVisual> _visuals = [];
    private readonly Dictionary<string, GroupVisual> _groups = [];
    private Border _background = new();
    private TextBlock _empty = new();
    private bool _settingPosition;
    private OsdOrientation _orientation;
    private ToolkitRuntimeSnapshot _latestSnapshot;
    private FpsTelemetrySnapshot _latestFps = FpsTelemetrySnapshot.Empty;
    private Brush _valueBrush = Brushes.White;
    private Brush _warningBrush = Brushes.Yellow;
    private Brush _criticalBrush = Brushes.Red;
    private bool _sessionLocked;
    private bool _restoreAfterUnlock;

    public ToolkitOsdWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _latestFps = runtime.CurrentFps;
        _runtime.FpsTelemetryUpdated += OnFpsTelemetryUpdated;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _latestSnapshot = runtime.Snapshot;
        Title = "ThinkBook Toolkit OSD";
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = runtime.Settings.Osd.FontSize;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        LocationChanged += (_, _) => CapturePosition();
        Loaded += (_, _) =>
        {
            UpdateLayout();
            RestoreOrSetDefaultPosition();
            RefreshValues();
            SyncFpsMonitoring(_runtime.Settings.Osd);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _runtime.SetFpsMonitoringConsumer("osd", false);
            _runtime.FpsTelemetryUpdated -= OnFpsTelemetryUpdated;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        };
        _timer.Tick += async (_, _) => await RefreshFromRuntimeAsync();
    }

    private void OnFpsTelemetryUpdated(
        object? sender,
        FpsTelemetrySnapshot value) => _latestFps = value;

    internal void ShowIfSessionUnlocked()
    {
        if (_sessionLocked)
            return;
        if (!IsVisible)
            Show();
        SyncFpsMonitoring(_runtime.Settings.Osd);
        if (IsLoaded)
            _timer.Start();
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
                OnSessionSwitch(sender, args)));
            return;
        }
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            _sessionLocked = true;
            _restoreAfterUnlock = IsVisible;
            _timer.Stop();
            _runtime.SetFpsMonitoringConsumer("osd", false);
            if (IsVisible)
                Hide();
        }
        else if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            _sessionLocked = false;
            if (_restoreAfterUnlock && _runtime.Settings.OsdEnabled)
            {
                _restoreAfterUnlock = false;
                Show();
                ApplySettings();
                SyncFpsMonitoring(_runtime.Settings.Osd);
                _timer.Start();
                _ = RefreshFromRuntimeAsync();
                EscalateZOrder();
            }
        }
    }

    public void ApplySettings()
    {
        var settings = _runtime.Settings.Osd;
        var orientationChanged = _orientation != settings.Orientation;
        _orientation = settings.Orientation;
        FontSize = Math.Clamp(settings.FontSize, 8, 24);
        _timer.Interval = TimeSpan.FromSeconds(
            Math.Clamp(settings.RefreshIntervalSeconds, .5, 5));
        Content = BuildContent(settings);
        SyncFpsMonitoring(settings);
        SetOverlayClickThrough(settings.FixedPosition);
        if (IsLoaded)
        {
            UpdateLayout();
            if (orientationChanged)
                RestoreOrSetDefaultPosition();
            RefreshValues();
            EscalateZOrder();
        }
    }

    private void SyncFpsMonitoring(ToolkitOsdSettings settings)
    {
        var selected = settings.Sensors ?? [];
        if (IsVisible &&
            (selected.Contains(OsdSensor.Fps) ||
            selected.Contains(OsdSensor.OnePercentLowFps) ||
            selected.Contains(OsdSensor.FrameLatency)))
        {
            _runtime.SetFpsMonitoringConsumer("osd", true);
        }
        else
        {
            _runtime.SetFpsMonitoringConsumer("osd", false);
            _latestFps = FpsTelemetrySnapshot.Empty;
        }
    }

    internal void RefreshForTesting()
    {
        _latestSnapshot = _runtime.Snapshot;
        RefreshValues();
    }
    internal int VisibleSensorCountForTesting => _visuals.Values.Count(
        visual => visual.Row.Visibility == Visibility.Visible);
    internal void SetFpsForTesting(FpsTelemetrySnapshot value)
    {
        _latestFps = value;
        RefreshValues();
    }
    internal string? SensorValueForTesting(OsdSensor sensor) =>
        _visuals.TryGetValue(sensor, out var visual)
            ? visual.Value.Text
            : null;
    internal Color? SensorColorForTesting(OsdSensor sensor) =>
        _visuals.TryGetValue(sensor, out var visual) &&
        visual.Value.Foreground is SolidColorBrush brush
            ? brush.Color
            : null;

    private UIElement BuildContent(ToolkitOsdSettings settings)
    {
        _visuals.Clear();
        _groups.Clear();
        _valueBrush = Brush(settings.ValueColor);
        _warningBrush = Brush(settings.WarningColor);
        _criticalBrush = Brush(settings.CriticalColor);
        var palette = ToolkitPalette.For(isDark: true);
        var content = new StackPanel
        {
            Orientation = settings.Orientation == OsdOrientation.Horizontal
                ? Orientation.Horizontal
                : Orientation.Vertical
        };
        var selected = new HashSet<OsdSensor>(settings.Sensors ?? []);
        foreach (var group in OsdSensorCatalog.Groups)
        {
            var sensors = group.Sensors.Where(selected.Contains).ToArray();
            if (sensors.Length == 0)
                continue;
            var groupPanel = new StackPanel
            {
                Orientation = settings.Orientation == OsdOrientation.Horizontal
                    ? Orientation.Horizontal
                    : Orientation.Vertical
            };
            var title = new TextBlock
            {
                Text = _runtime.L(group.Chinese, group.English),
                Foreground = Brush(settings.CategoryColor),
                FontWeight = FontWeights.Bold,
                Margin = settings.Orientation == OsdOrientation.Horizontal
                    ? new Thickness(0, 0, 7, 0)
                    : new Thickness(0, 0, 0, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            groupPanel.Children.Add(title);
            var rows = new List<FrameworkElement>();
            foreach (var sensor in sensors)
            {
                var visual = settings.Orientation == OsdOrientation.Horizontal
                    ? HorizontalSensor(sensor, settings)
                    : VerticalSensor(sensor, settings);
                groupPanel.Children.Add(visual.Row);
                rows.Add(visual.Row);
                _visuals[sensor] = visual;
            }
            var wrapper = new Border
            {
                Padding = settings.Orientation == OsdOrientation.Horizontal
                    ? new Thickness(0)
                    : new Thickness(0, 0, 0, 8),
                Margin = settings.Orientation == OsdOrientation.Horizontal
                    ? new Thickness(0, 0, 12, 0)
                    : new Thickness(0),
                Child = groupPanel
            };
            content.Children.Add(wrapper);
            _groups[group.Id] = new(wrapper, title, rows);
        }
        _empty = new TextBlock
        {
            Text = _runtime.L("等待传感器数据…", "Waiting for sensor data…"),
            Foreground = Brush(palette.Muted),
            Margin = new Thickness(4),
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(_empty);
        var alpha = (byte)Math.Clamp(
            settings.OpacityPercent * 255 / 100,
            0,
            255);
        _background = new Border
        {
            Background = BrushWithAlpha(settings.BackgroundColor, alpha),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = settings.Orientation == OsdOrientation.Horizontal
                ? new Thickness(12, 7, 2, 7)
                : new Thickness(12, 11, 12, 3),
            Child = content
        };
        return _background;
    }

    private SensorVisual HorizontalSensor(
        OsdSensor sensor,
        ToolkitOsdSettings settings)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var value = new TextBlock
        {
            Foreground = Brush(settings.ValueColor),
            FontWeight = FontWeights.SemiBold
        };
        row.Children.Add(value);
        return new(row, value);
    }

    private SensorVisual VerticalSensor(
        OsdSensor sensor,
        ToolkitOsdSettings settings)
    {
        var row = new Grid { MinWidth = 205, Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                OsdSensorCatalog.Chinese(
                    sensor,
                    DeviceModelDetector.HasSecondFan()),
                OsdSensorCatalog.English(
                    sensor,
                    DeviceModelDetector.HasSecondFan())),
            Foreground = Brush(settings.LabelColor),
            Margin = new Thickness(0, 0, 14, 0)
        });
        var value = new TextBlock
        {
            Foreground = Brush(settings.ValueColor),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return new(row, value);
    }

    private void RefreshValues()
    {
        var snapshot = _latestSnapshot;
        var fps = _latestFps.IsFresh
            ? _latestFps
            : FpsTelemetrySnapshot.Empty;
        var settings = _runtime.Settings.Osd;
        var any = false;
        foreach (var (sensor, visual) in _visuals)
        {
            var value = OsdSensorCatalog.Value(
                sensor,
                snapshot,
                DeviceModelDetector.HasSecondFan(),
                settings.MultipleTemperatureMode,
                settings.MemoryDisplayMode,
                _runtime.IsChinese,
                fps);
            visual.Row.Visibility = string.IsNullOrWhiteSpace(value)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(value))
            {
                visual.Value.Text = value;
                visual.Value.Foreground =
                    OsdSensorCatalog.Severity(
                        sensor,
                        snapshot,
                        settings,
                        fps) switch
                    {
                        OsdValueSeverity.Warning => _warningBrush,
                        OsdValueSeverity.Critical => _criticalBrush,
                        _ => _valueBrush
                    };
                any = true;
            }
        }
        foreach (var group in _groups.Values)
        {
            group.Wrapper.Visibility = group.Rows.Any(row =>
                    row.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private async System.Threading.Tasks.Task RefreshFromRuntimeAsync()
    {
        _timer.Stop();
        try
        {
            _latestSnapshot = await _runtime.ReadOsdSnapshotAsync();
            RefreshValues();
            EscalateZOrder();
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning("OSD sensor refresh failed: " + ex.Message);
        }
        finally
        {
            if (IsVisible)
                _timer.Start();
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (_runtime.Settings.Osd.FixedPosition ||
            args.ChangedButton != MouseButton.Left)
            return;
        DragMove();
        SnapToCurrentScreen();
        CapturePosition();
        _runtime.SaveOsdPosition();
    }

    private void SnapToCurrentScreen()
    {
        var threshold = Math.Clamp(
            _runtime.Settings.Osd.SnapThreshold,
            0,
            100);
        if (threshold <= 0)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window))
            return;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;

        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var left = window.Left;
        var top = window.Top;
        var work = info.WorkArea;

        if (Math.Abs(left - work.Left) < threshold)
            left = work.Left;
        else if (Math.Abs(work.Right - (left + width)) < threshold)
            left = work.Right - width;
        if (Math.Abs(top - work.Top) < threshold)
            top = work.Top;
        else if (Math.Abs(work.Bottom - (top + height)) < threshold)
            top = work.Bottom - height;

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void CapturePosition()
    {
        if (_settingPosition || !IsLoaded)
            return;
        var settings = _runtime.Settings.Osd;
        if (_orientation == OsdOrientation.Horizontal)
        {
            settings.HorizontalX = Left;
            settings.HorizontalY = Top;
        }
        else
        {
            settings.VerticalX = Left;
            settings.VerticalY = Top;
        }
    }

    private void RestoreOrSetDefaultPosition()
    {
        var settings = _runtime.Settings.Osd;
        var x = _orientation == OsdOrientation.Horizontal
            ? settings.HorizontalX
            : settings.VerticalX;
        var y = _orientation == OsdOrientation.Horizontal
            ? settings.HorizontalY
            : settings.VerticalY;
        _settingPosition = true;
        try
        {
            if (x.HasValue && y.HasValue && IsOnScreen(x.Value, y.Value))
            {
                Left = x.Value;
                Top = y.Value;
            }
            else
            {
                var work = SystemParameters.WorkArea;
                if (_orientation == OsdOrientation.Horizontal)
                {
                    Left = work.Left + (work.Width - ActualWidth) / 2;
                    Top = work.Top + 10;
                }
                else
                {
                    Left = work.Left + 10;
                    Top = work.Top + (work.Height - ActualHeight) / 2;
                }
            }
        }
        finally
        {
            _settingPosition = false;
        }
    }

    private static bool IsOnScreen(double x, double y)
    {
        var area = SystemParameters.VirtualScreenWidth;
        return x >= SystemParameters.VirtualScreenLeft - 100 &&
               x <= SystemParameters.VirtualScreenLeft + area &&
               y >= SystemParameters.VirtualScreenTop - 100 &&
               y <= SystemParameters.VirtualScreenTop +
                   SystemParameters.VirtualScreenHeight;
    }

    private static SolidColorBrush Brush(string value) =>
        new(ParseColor(value));

    private static SolidColorBrush BrushWithAlpha(string value, byte alpha)
    {
        var color = ParseColor(value);
        color.A = alpha;
        return new SolidColorBrush(color);
    }

    private static Color ParseColor(string value)
    {
        var normalized = value.Trim();
        if (!normalized.StartsWith("#", StringComparison.Ordinal))
            normalized = "#" + normalized;
        return (Color)ColorConverter.ConvertFromString(normalized);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed record SensorVisual(FrameworkElement Row, TextBlock Value);
    private sealed record GroupVisual(
        FrameworkElement Wrapper,
        TextBlock Title,
        IReadOnlyList<FrameworkElement> Rows);
}

internal enum OsdValueSeverity
{
    Normal,
    Warning,
    Critical
}

internal static class OsdSensorCatalog
{
    internal sealed record Group(
        string Id,
        string Chinese,
        string English,
        IReadOnlyList<OsdSensor> Sensors);

    public static IReadOnlyList<Group> Groups { get; } =
    [
        new("fps", "FPS", "FPS",
        [
            OsdSensor.Fps,
            OsdSensor.OnePercentLowFps,
            OsdSensor.FrameLatency
        ]),
        new("cpu", "CPU", "CPU",
        [
            OsdSensor.CpuUtilization,
            OsdSensor.CpuAverageFrequency,
            OsdSensor.CpuPerformanceCoreAverageFrequency,
            OsdSensor.CpuEfficiencyCoreAverageFrequency,
            OsdSensor.CpuMaximumFrequency,
            OsdSensor.CpuTemperature,
            OsdSensor.CpuPower
        ]),
        new("gpu", "GPU", "GPU",
        [
            OsdSensor.GpuUtilization,
            OsdSensor.GpuCoreFrequency,
            OsdSensor.GpuCoreTemperature,
            OsdSensor.GpuHotSpotTemperature,
            OsdSensor.GpuPower
        ]),
        new("vram", "显存", "VRAM",
        [
            OsdSensor.GpuVramUtilization,
            OsdSensor.GpuVramFrequency,
            OsdSensor.GpuVramTemperature
        ]),
        new("battery", "电池", "Battery",
        [
            OsdSensor.BatteryOutputPower,
            OsdSensor.BatteryCapacity
        ]),
        new("memory", "内存", "RAM",
        [
            OsdSensor.MemoryUtilization,
            OsdSensor.MemoryCommitted,
            OsdSensor.MemorySlot1Temperature,
            OsdSensor.MemorySlot2Temperature
        ]),
        new("storage", "硬盘", "Storage",
        [
            OsdSensor.Storage1Temperature,
            OsdSensor.Storage2Temperature,
            OsdSensor.Storage3Temperature,
            OsdSensor.Storage4Temperature,
            OsdSensor.Storage5Temperature,
            OsdSensor.Storage6Temperature,
            OsdSensor.Storage7Temperature,
            OsdSensor.Storage8Temperature
        ]),
        new("fans", "风扇", "Fans",
        [
            OsdSensor.Fan1Speed,
            OsdSensor.Fan2Speed
        ])
    ];

    public static int StorageIndex(OsdSensor sensor) => sensor switch
    {
        OsdSensor.Storage1Temperature => 0,
        OsdSensor.Storage2Temperature => 1,
        OsdSensor.Storage3Temperature => 2,
        OsdSensor.Storage4Temperature => 3,
        OsdSensor.Storage5Temperature => 4,
        OsdSensor.Storage6Temperature => 5,
        OsdSensor.Storage7Temperature => 6,
        OsdSensor.Storage8Temperature => 7,
        _ => -1
    };

    public static string Chinese(
        OsdSensor sensor,
        bool hasSecondFan = false) => sensor switch
    {
        OsdSensor.Fps => "FPS",
        OsdSensor.OnePercentLowFps => "1% Low",
        OsdSensor.FrameLatency => "延迟",
        OsdSensor.CpuUtilization => "利用率",
        OsdSensor.CpuAverageFrequency => "平均频率",
        OsdSensor.CpuPerformanceCoreAverageFrequency => "性能核平均频率",
        OsdSensor.CpuEfficiencyCoreAverageFrequency => "能效核平均频率",
        OsdSensor.CpuMaximumFrequency => "最高频率",
        OsdSensor.CpuTemperature => "温度",
        OsdSensor.CpuPower => "功耗",
        OsdSensor.GpuUtilization => "利用率",
        OsdSensor.GpuVramUtilization => "利用率",
        OsdSensor.GpuCoreFrequency => "核心频率",
        OsdSensor.GpuVramFrequency => "频率",
        OsdSensor.GpuCoreTemperature => "核心温度",
        OsdSensor.GpuHotSpotTemperature => "热点温度",
        OsdSensor.GpuVramTemperature => "温度",
        OsdSensor.GpuPower => "功耗",
        OsdSensor.BatteryOutputPower => "功率",
        OsdSensor.BatteryCapacity => "容量",
        OsdSensor.MemoryUtilization => "利用率",
        OsdSensor.MemoryCommitted => "已提交",
        OsdSensor.MemorySlot1Temperature => "插槽1温度",
        OsdSensor.MemorySlot2Temperature => "插槽2温度",
        OsdSensor.Storage1Temperature => "硬盘1温度",
        OsdSensor.Storage2Temperature => "硬盘2温度",
        OsdSensor.Storage3Temperature => "硬盘3温度",
        OsdSensor.Storage4Temperature => "硬盘4温度",
        OsdSensor.Storage5Temperature => "硬盘5温度",
        OsdSensor.Storage6Temperature => "硬盘6温度",
        OsdSensor.Storage7Temperature => "硬盘7温度",
        OsdSensor.Storage8Temperature => "硬盘8温度",
        OsdSensor.Fan1Speed => hasSecondFan ? "风扇1转速" : "风扇转速",
        OsdSensor.Fan2Speed => "风扇2转速",
        _ => sensor.ToString()
    };

    public static string English(
        OsdSensor sensor,
        bool hasSecondFan = false) => sensor switch
    {
        OsdSensor.Fps => "FPS",
        OsdSensor.OnePercentLowFps => "1% Low",
        OsdSensor.FrameLatency => "Latency",
        OsdSensor.CpuUtilization => "Utilization",
        OsdSensor.CpuAverageFrequency => "Average clock",
        OsdSensor.CpuPerformanceCoreAverageFrequency => "P-core average clock",
        OsdSensor.CpuEfficiencyCoreAverageFrequency => "E-core average clock",
        OsdSensor.CpuMaximumFrequency => "Maximum clock",
        OsdSensor.CpuTemperature => "Temperature",
        OsdSensor.CpuPower => "Power",
        OsdSensor.GpuUtilization => "Utilization",
        OsdSensor.GpuVramUtilization => "Utilization",
        OsdSensor.GpuCoreFrequency => "Core clock",
        OsdSensor.GpuVramFrequency => "Clock",
        OsdSensor.GpuCoreTemperature => "Core temperature",
        OsdSensor.GpuHotSpotTemperature => "Hot spot",
        OsdSensor.GpuVramTemperature => "Temperature",
        OsdSensor.GpuPower => "Power",
        OsdSensor.BatteryOutputPower => "Power",
        OsdSensor.BatteryCapacity => "Capacity",
        OsdSensor.MemoryUtilization => "Utilization",
        OsdSensor.MemoryCommitted => "Committed",
        OsdSensor.MemorySlot1Temperature => "Slot 1 temperature",
        OsdSensor.MemorySlot2Temperature => "Slot 2 temperature",
        OsdSensor.Storage1Temperature => "Disk 1 temperature",
        OsdSensor.Storage2Temperature => "Disk 2 temperature",
        OsdSensor.Storage3Temperature => "Disk 3 temperature",
        OsdSensor.Storage4Temperature => "Disk 4 temperature",
        OsdSensor.Storage5Temperature => "Disk 5 temperature",
        OsdSensor.Storage6Temperature => "Disk 6 temperature",
        OsdSensor.Storage7Temperature => "Disk 7 temperature",
        OsdSensor.Storage8Temperature => "Disk 8 temperature",
        OsdSensor.Fan1Speed => hasSecondFan ? "Fan 1 speed" : "Fan speed",
        OsdSensor.Fan2Speed => "Fan 2 speed",
        _ => sensor.ToString()
    };

    public static string? Value(
        OsdSensor sensor,
        ToolkitRuntimeSnapshot snapshot,
        bool hasSecondFan,
        OsdMultipleTemperatureMode multipleTemperatureMode,
        OsdMemoryDisplayMode memoryDisplayMode,
        bool isChinese,
        FpsTelemetrySnapshot fps)
    {
        var value = snapshot.Temperatures;
        return sensor switch
        {
            OsdSensor.Fps => Fps(fps.Fps),
            OsdSensor.OnePercentLowFps => Fps(fps.OnePercentLowFps),
            OsdSensor.FrameLatency => Latency(fps.FrameTimeMs),
            OsdSensor.CpuUtilization => Percent(value?.CpuLoadPercent),
            OsdSensor.CpuAverageFrequency => Frequency(value?.CpuAverageClockMhz),
            OsdSensor.CpuPerformanceCoreAverageFrequency => Frequency(
                value?.CpuPerformanceCoreAverageClockMhz),
            OsdSensor.CpuEfficiencyCoreAverageFrequency => Frequency(
                value?.CpuEfficiencyCoreAverageClockMhz),
            OsdSensor.CpuMaximumFrequency => Frequency(value?.CpuMaximumClockMhz),
            OsdSensor.CpuTemperature => Temperature(value?.CpuTempC),
            OsdSensor.CpuPower => Power(value?.CpuPowerW),
            OsdSensor.GpuUtilization => Percent(value?.GpuLoadPercent),
            OsdSensor.GpuVramUtilization => Percent(value?.GpuMemoryLoadPercent),
            OsdSensor.GpuCoreFrequency => Frequency(value?.GpuCoreClockMhz),
            OsdSensor.GpuVramFrequency => Frequency(value?.GpuMemoryClockMhz),
            OsdSensor.GpuCoreTemperature => Temperature(value?.GpuTempC),
            OsdSensor.GpuHotSpotTemperature => Temperature(value?.GpuHotSpotTempC),
            OsdSensor.GpuVramTemperature => VramTemperature(
                value,
                multipleTemperatureMode),
            OsdSensor.GpuPower => Power(value?.GpuPowerW),
            OsdSensor.BatteryOutputPower => Power(
                snapshot.Battery?.ChargeDischargePowerW),
            OsdSensor.BatteryCapacity => Capacity(
                snapshot.Battery?.CurrentCapacityWh),
            OsdSensor.MemoryUtilization => MemoryPair(
                value?.PhysicalMemoryUsedGb,
                value?.PhysicalMemoryTotalGb,
                memoryDisplayMode),
            OsdSensor.MemoryCommitted => MemoryPair(
                value?.VirtualMemoryUsedGb,
                value?.VirtualMemoryTotalGb,
                memoryDisplayMode),
            OsdSensor.MemorySlot1Temperature => MemorySlotTemperature(
                value,
                0),
            OsdSensor.MemorySlot2Temperature => MemorySlotTemperature(
                value,
                1),
            OsdSensor.Storage1Temperature => StorageTemperature(value, 0, multipleTemperatureMode),
            OsdSensor.Storage2Temperature => StorageTemperature(value, 1, multipleTemperatureMode),
            OsdSensor.Storage3Temperature => StorageTemperature(value, 2, multipleTemperatureMode),
            OsdSensor.Storage4Temperature => StorageTemperature(value, 3, multipleTemperatureMode),
            OsdSensor.Storage5Temperature => StorageTemperature(value, 4, multipleTemperatureMode),
            OsdSensor.Storage6Temperature => StorageTemperature(value, 5, multipleTemperatureMode),
            OsdSensor.Storage7Temperature => StorageTemperature(value, 6, multipleTemperatureMode),
            OsdSensor.Storage8Temperature => StorageTemperature(value, 7, multipleTemperatureMode),
            OsdSensor.Fan1Speed => FanSpeed(
                snapshot.Fans?.Fan1Rpm,
                isChinese),
            OsdSensor.Fan2Speed => hasSecondFan
                ? FanSpeed(snapshot.Fans?.Fan2Rpm, isChinese)
                : null,
            _ => null
        };
    }

    public static OsdValueSeverity Severity(
        OsdSensor sensor,
        ToolkitRuntimeSnapshot snapshot,
        ToolkitOsdSettings settings,
        FpsTelemetrySnapshot fps)
    {
        var value = snapshot.Temperatures;
        return sensor switch
        {
            OsdSensor.Fps => LowSeverity(
                fps.Fps,
                settings.FpsWarningThreshold,
                settings.FpsCriticalThreshold),
            OsdSensor.OnePercentLowFps => LowFpsSeverity(fps, settings),
            OsdSensor.FrameLatency => HighSeverity(
                fps.FrameTimeMs,
                settings.FpsWarningThreshold > 0
                    ? 1000d / settings.FpsWarningThreshold
                    : double.PositiveInfinity,
                settings.FpsCriticalThreshold > 0
                    ? 1000d / settings.FpsCriticalThreshold
                    : double.PositiveInfinity),
            OsdSensor.CpuUtilization => UsageSeverity(
                value?.CpuLoadPercent,
                settings),
            OsdSensor.GpuUtilization => UsageSeverity(
                value?.GpuLoadPercent,
                settings),
            OsdSensor.MemoryUtilization => UsageSeverity(
                Percentage(
                    value?.PhysicalMemoryUsedGb,
                    value?.PhysicalMemoryTotalGb),
                settings),
            OsdSensor.MemoryCommitted => UsageSeverity(
                Percentage(
                    value?.VirtualMemoryUsedGb,
                    value?.VirtualMemoryTotalGb),
                settings),
            OsdSensor.CpuTemperature => HighSeverity(
                value?.CpuTempC,
                settings.CpuTemperatureWarning,
                settings.CpuTemperatureCritical),
            OsdSensor.GpuHotSpotTemperature => HighSeverity(
                value?.GpuHotSpotTempC,
                settings.GpuHotSpotTemperatureWarning,
                settings.GpuHotSpotTemperatureCritical),
            OsdSensor.GpuCoreTemperature => HighSeverity(
                value?.GpuTempC,
                settings.GpuTemperatureWarning,
                settings.GpuTemperatureCritical),
            OsdSensor.GpuVramTemperature => HighSeverity(
                VramTemperatureValue(value, settings.MultipleTemperatureMode),
                settings.VramTemperatureWarning,
                settings.VramTemperatureCritical),
            OsdSensor.MemorySlot1Temperature => HighSeverity(
                MemorySlotTemperatureValue(value, 0),
                settings.MemoryTemperatureWarning,
                settings.MemoryTemperatureCritical),
            OsdSensor.MemorySlot2Temperature => HighSeverity(
                MemorySlotTemperatureValue(value, 1),
                settings.MemoryTemperatureWarning,
                settings.MemoryTemperatureCritical),
            OsdSensor.Storage1Temperature => StorageSeverity(value, 0, settings),
            OsdSensor.Storage2Temperature => StorageSeverity(value, 1, settings),
            OsdSensor.Storage3Temperature => StorageSeverity(value, 2, settings),
            OsdSensor.Storage4Temperature => StorageSeverity(value, 3, settings),
            OsdSensor.Storage5Temperature => StorageSeverity(value, 4, settings),
            OsdSensor.Storage6Temperature => StorageSeverity(value, 5, settings),
            OsdSensor.Storage7Temperature => StorageSeverity(value, 6, settings),
            OsdSensor.Storage8Temperature => StorageSeverity(value, 7, settings),
            OsdSensor.BatteryOutputPower => LowSeverity(
                snapshot.Battery?.ChargeDischargePowerW,
                settings.BatteryOutputPowerWarning,
                settings.BatteryOutputPowerCritical),
            _ => OsdValueSeverity.Normal
        };
    }

    private static OsdValueSeverity LowFpsSeverity(
        FpsTelemetrySnapshot fps,
        ToolkitOsdSettings settings)
    {
        if (!fps.Fps.HasValue || !fps.OnePercentLowFps.HasValue ||
            fps.Fps.Value <= 0)
        {
            return OsdValueSeverity.Normal;
        }
        if (settings.LowFpsThresholdMode ==
            OsdLowFpsThresholdMode.DifferenceFromFps)
        {
            return HighSeverity(
                fps.Fps.Value - fps.OnePercentLowFps.Value,
                settings.LowFpsWarningDelta,
                settings.LowFpsCriticalDelta);
        }
        return LowSeverity(
            fps.OnePercentLowFps.Value * 100 / fps.Fps.Value,
            settings.LowFpsWarningPercentage,
            settings.LowFpsCriticalPercentage);
    }

    private static OsdValueSeverity UsageSeverity(
        double? value,
        ToolkitOsdSettings settings) => HighSeverity(
        value,
        settings.UsageWarningThreshold,
        settings.UsageCriticalThreshold);

    private static OsdValueSeverity StorageSeverity(
        TemperatureSnapshot? value,
        int index,
        ToolkitOsdSettings settings) => HighSeverity(
        StorageTemperatureValue(
            value,
            index,
            settings.MultipleTemperatureMode),
        settings.StorageTemperatureWarning,
        settings.StorageTemperatureCritical);

    private static OsdValueSeverity HighSeverity(
        double? value,
        double warning,
        double critical)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return OsdValueSeverity.Normal;
        if (value.Value > critical)
            return OsdValueSeverity.Critical;
        if (value.Value > warning)
            return OsdValueSeverity.Warning;
        return OsdValueSeverity.Normal;
    }

    private static OsdValueSeverity LowSeverity(
        double? value,
        double warning,
        double critical)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return OsdValueSeverity.Normal;
        if (value.Value < critical)
            return OsdValueSeverity.Critical;
        if (value.Value < warning)
            return OsdValueSeverity.Warning;
        return OsdValueSeverity.Normal;
    }

    private static double? Percentage(double? used, double? total) =>
        used.HasValue && total is > 0
            ? used.Value * 100 / total.Value
            : null;

    private static string? Percent(double? value) => value.HasValue
        ? $"{value.Value:F0}%"
        : null;
    private static string? Frequency(double? value) => value.HasValue
        ? $"{value.Value:F0} MHz"
        : null;
    private static string? Temperature(double? value) => value.HasValue
        ? $"{value.Value:F1} °C"
        : null;
    private static string? Power(double? value) => value.HasValue
        ? $"{value.Value:F1} W"
        : null;
    private static string? Capacity(double? value) => value.HasValue
        ? $"{value.Value:F2} Wh"
        : null;
    private static string? Fps(double? value) => value.HasValue
        ? $"{value.Value:F0}"
        : null;
    private static string? Latency(double? value) => value.HasValue
        ? $"{value.Value:F1} ms"
        : null;

    private static string? VramTemperature(
        TemperatureSnapshot? value,
        OsdMultipleTemperatureMode mode)
    {
        if (value?.VramChipTemperaturesC is { Count: > 0 } chips)
            return MultipleTemperatures(chips, mode);
        return Temperature(value?.VramTempC);
    }

    private static double? VramTemperatureValue(
        TemperatureSnapshot? value,
        OsdMultipleTemperatureMode mode)
    {
        if (value?.VramChipTemperaturesC is { Count: > 0 } chips)
            return MultipleTemperatureValue(chips, mode);
        return value?.VramTempC;
    }

    private static string? MemoryPair(
        double? used,
        double? total,
        OsdMemoryDisplayMode mode)
    {
        if (!used.HasValue || !total.HasValue || total.Value <= 0)
            return null;
        var values = $"{used.Value:F1}/{total.Value:F1} GB";
        var percentage = $"{used.Value / total.Value * 100:F0}%";
        return mode switch
        {
            OsdMemoryDisplayMode.Values => values,
            OsdMemoryDisplayMode.Percentage => percentage,
            _ => $"{values} ({percentage})"
        };
    }

    private static string? MemorySlotTemperature(
        TemperatureSnapshot? value,
        int slot)
    {
        if (value?.MemorySlotTemperaturesC is not { Count: > 0 } temperatures)
            return null;
        // Keep this identical to the overview page: the telemetry reader may
        // expose up to six readings for each DIMM, but the slot row displays
        // only the first one (indices 0 and 6).
        var index = temperatures.Count > 6 ? slot * 6 : slot;
        return index < temperatures.Count
            ? Temperature(temperatures[index])
            : null;
    }

    private static double? MemorySlotTemperatureValue(
        TemperatureSnapshot? value,
        int slot)
    {
        if (value?.MemorySlotTemperaturesC is not { Count: > 0 } temperatures)
            return null;
        var index = temperatures.Count > 6 ? slot * 6 : slot;
        return index < temperatures.Count ? temperatures[index] : null;
    }

    private static string? StorageTemperature(
        TemperatureSnapshot? value,
        int index,
        OsdMultipleTemperatureMode mode)
    {
        if (value?.StorageDevices is not { } devices || index >= devices.Count)
            return null;
        return MultipleTemperatures(devices[index].TemperaturesC, mode);
    }

    private static double? StorageTemperatureValue(
        TemperatureSnapshot? value,
        int index,
        OsdMultipleTemperatureMode mode)
    {
        if (value?.StorageDevices is not { } devices || index >= devices.Count)
            return null;
        return MultipleTemperatureValue(devices[index].TemperaturesC, mode);
    }

    private static string? MultipleTemperatures(
        IReadOnlyList<double>? values,
        OsdMultipleTemperatureMode mode)
    {
        if (values is not { Count: > 0 })
            return null;
        return mode switch
        {
            OsdMultipleTemperatureMode.Average =>
                $"{values.Average():F1} °C",
            OsdMultipleTemperatureMode.Maximum =>
                $"{values.Max():F1} °C",
            _ => string.Join("/", values.Select(item => $"{item:F0}")) +
                 " °C"
        };
    }

    private static double? MultipleTemperatureValue(
        IReadOnlyList<double>? values,
        OsdMultipleTemperatureMode mode)
    {
        if (values is not { Count: > 0 })
            return null;
        return mode == OsdMultipleTemperatureMode.Average
            ? values.Average()
            : values.Max();
    }

    private static string? FanSpeed(int? value, bool isChinese) => value.HasValue
        ? isChinese ? $"{value.Value} 转" : $"{value.Value} RPM"
        : null;
}
