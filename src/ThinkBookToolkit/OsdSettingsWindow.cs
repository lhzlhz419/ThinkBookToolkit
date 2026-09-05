using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed partial class OsdSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly ComboBox _orientation = new() { MinWidth = 180 };
    private readonly ComboBox _refresh = new() { MinWidth = 150 };
    private readonly ComboBox _temperatureMode = new() { MinWidth = 180 };
    private readonly ComboBox _memoryDisplayMode = new() { MinWidth = 180 };
    private readonly CheckBox _fixed = new();
    private readonly Slider _opacity = new()
    {
        Minimum = 0,
        Maximum = 100,
        TickFrequency = 5
    };
    private readonly Slider _fontSize = new()
    {
        Minimum = 8,
        Maximum = 24,
        TickFrequency = 1
    };
    private readonly TextBox _fontSizeValue = new()
    {
        Width = 72,
        MinHeight = 36,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBox _opacityValue = new()
    {
        Width = 72,
        MinHeight = 36,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new();
    private readonly Slider _snap = new()
    {
        Minimum = 0,
        Maximum = 100,
        TickFrequency = 1
    };
    private readonly TextBox _snapValue = new()
    {
        Width = 72,
        MinHeight = 36,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private ToolkitOsdSettings _draft;
    private bool _syncing;

    public OsdSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        _draft = Clone(runtime.Settings.Osd);
        Title = runtime.L("OSD 设置", "OSD settings");
        Width = 820;
        Height = 700;
        MinWidth = 620;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Content = Build();
        Loaded += (_, _) => ModernTheme.RefreshWindow(this, runtime.IsDark);
        SyncControls();
    }

    private UIElement Build()
    {
        AddChoice(
            _orientation,
            _runtime.L("横向", "Horizontal"),
            OsdOrientation.Horizontal);
        AddChoice(
            _orientation,
            _runtime.L("纵向", "Vertical"),
            OsdOrientation.Vertical);
        foreach (var interval in new[] { .5d, 1d, 2d, 3d, 5d })
        {
            AddChoice(
                _refresh,
                _runtime.L($"{interval:0.#} 秒", $"{interval:0.#} seconds"),
                interval);
        }
        AddChoice(
            _temperatureMode,
            _runtime.L("平均值", "Average"),
            OsdMultipleTemperatureMode.Average);
        AddChoice(
            _temperatureMode,
            _runtime.L("最大值", "Maximum"),
            OsdMultipleTemperatureMode.Maximum);
        AddChoice(
            _temperatureMode,
            _runtime.L("全部", "All"),
            OsdMultipleTemperatureMode.All);
        AddChoice(
            _memoryDisplayMode,
            _runtime.L("数值", "Values"),
            OsdMemoryDisplayMode.Values);
        AddChoice(
            _memoryDisplayMode,
            _runtime.L("百分比", "Percentage"),
            OsdMemoryDisplayMode.Percentage);
        AddChoice(
            _memoryDisplayMode,
            _runtime.L("全部", "All"),
            OsdMemoryDisplayMode.All);

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = _runtime.L("OSD 设置", "OSD settings"),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
        });

        var tabArea = new Grid();
        tabArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabArea.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        var tabHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var generalTab = TabButton(_runtime.L("常规", "General"));
        var sensorTab = TabButton(_runtime.L("传感器", "Sensors"));
        var colorTab = TabButton(_runtime.L("颜色", "Colors"));
        var thresholdTab = TabButton(_runtime.L("临界值", "Thresholds"));
        generalTab.Margin = new Thickness(0, 0, 8, 0);
        sensorTab.Margin = new Thickness(0, 0, 8, 0);
        colorTab.Margin = new Thickness(0, 0, 8, 0);
        tabHeader.Children.Add(generalTab);
        tabHeader.Children.Add(sensorTab);
        tabHeader.Children.Add(colorTab);
        tabHeader.Children.Add(thresholdTab);
        tabArea.Children.Add(tabHeader);

        var pages = new Grid();
        var generalPage = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildGeneral()
        };
        var sensorPage = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildSensors(),
            Visibility = Visibility.Collapsed
        };
        var colorPage = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildColors(),
            Visibility = Visibility.Collapsed
        };
        var thresholdPage = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildThresholds(),
            Visibility = Visibility.Collapsed
        };
        pages.Children.Add(generalPage);
        pages.Children.Add(sensorPage);
        pages.Children.Add(colorPage);
        pages.Children.Add(thresholdPage);
        Grid.SetRow(pages, 1);
        tabArea.Children.Add(pages);
        var tabs = new[]
        {
            (Button: generalTab, Page: (UIElement)generalPage),
            (Button: sensorTab, Page: (UIElement)sensorPage),
            (Button: colorTab, Page: (UIElement)colorPage),
            (Button: thresholdTab, Page: (UIElement)thresholdPage)
        };
        SelectTab(generalPage, tabs);
        generalTab.Click += (_, _) => SelectTab(generalPage, tabs);
        sensorTab.Click += (_, _) => SelectTab(sensorPage, tabs);
        colorTab.Click += (_, _) => SelectTab(colorPage, tabs);
        thresholdTab.Click += (_, _) => SelectTab(thresholdPage, tabs);
        Grid.SetRow(tabArea, 1);
        root.Children.Add(tabArea);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        var close = Button(_runtime.L("关闭", "Close"), primary: true);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _orientation.SelectionChanged += (_, _) =>
        {
            if (_syncing || Selected<OsdOrientation>(_orientation) is not { } value)
                return;
            _draft.Orientation = value;
            Save();
        };
        _refresh.SelectionChanged += (_, _) =>
        {
            if (_syncing || Selected<double>(_refresh) is not { } value)
                return;
            _draft.RefreshIntervalSeconds = value;
            Save();
        };
        _fixed.Click += (_, _) =>
        {
            if (_syncing) return;
            _draft.FixedPosition = _fixed.IsChecked == true;
            Save();
        };
        _temperatureMode.SelectionChanged += (_, _) =>
        {
            if (_syncing ||
                Selected<OsdMultipleTemperatureMode>(_temperatureMode) is
                    not { } value)
            {
                return;
            }
            _draft.MultipleTemperatureMode = value;
            Save();
        };
        _memoryDisplayMode.SelectionChanged += (_, _) =>
        {
            if (_syncing ||
                Selected<OsdMemoryDisplayMode>(_memoryDisplayMode) is
                    not { } value)
            {
                return;
            }
            _draft.MemoryDisplayMode = value;
            Save();
        };
        _opacity.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _draft.OpacityPercent = (int)Math.Round(_opacity.Value);
            _opacityValue.Text = _draft.OpacityPercent.ToString(
                CultureInfo.InvariantCulture);
            Save();
        };
        _opacityValue.LostKeyboardFocus += (_, _) => CommitOpacity();
        _opacityValue.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            CommitOpacity();
            Keyboard.ClearFocus();
        };
        _fontSize.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _draft.FontSize = (int)Math.Round(_fontSize.Value);
            _fontSizeValue.Text = _draft.FontSize.ToString(
                CultureInfo.InvariantCulture);
            Save();
        };
        _fontSizeValue.LostKeyboardFocus += (_, _) => CommitFontSize();
        _fontSizeValue.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            CommitFontSize();
            Keyboard.ClearFocus();
        };
        _snap.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _draft.SnapThreshold = (int)Math.Round(_snap.Value);
            _snapValue.Text = _draft.SnapThreshold.ToString(
                CultureInfo.InvariantCulture);
            Save();
        };
        _snapValue.LostKeyboardFocus += (_, _) => CommitSnapThreshold();
        _snapValue.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            CommitSnapThreshold();
            Keyboard.ClearFocus();
        };
        return root;
    }

    private UIElement BuildGeneral()
    {
        var content = new StackPanel { Margin = new Thickness(8, 12, 8, 8) };
        content.Children.Add(Row(
            _runtime.L("方向", "Orientation"),
            _runtime.L("选择横向信息条或纵向面板。", "Choose a horizontal bar or vertical panel."),
            _orientation));
        content.Children.Add(Row(
            _runtime.L("刷新时间", "Refresh interval"),
            _runtime.L("设置 OSD 读数更新间隔。", "Choose how often OSD readings are updated."),
            _refresh));
        var snapping = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _snap.Width = 210;
        snapping.Children.Add(_snap);
        snapping.Children.Add(_snapValue);
        snapping.Children.Add(new TextBlock
        {
            Text = "px",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        content.Children.Add(Row(
            _runtime.L("吸附阈值", "Snapping threshold"),
            _runtime.L(
                "拖动到屏幕工作区边缘时，在此距离内自动贴边。",
                "Snap to the screen work-area edge within this distance."),
            snapping));
        content.Children.Add(Row(
            _runtime.L("固定位置", "Lock position"),
            _runtime.L("关闭时可拖动；开启后锁定位置并允许鼠标穿透。", "When off, the OSD can be dragged. When on, it is locked and click-through."),
            _fixed));
        var opacity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _opacity.Width = 210;
        opacity.Children.Add(_opacity);
        opacity.Children.Add(_opacityValue);
        opacity.Children.Add(new TextBlock
        {
            Text = "%",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        content.Children.Add(Row(
            _runtime.L("不透明度", "Opacity"),
            _runtime.L("调整 OSD 背景的不透明度。", "Adjust OSD background opacity."),
            opacity));
        var fontSize = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _fontSize.Width = 210;
        fontSize.Children.Add(_fontSize);
        fontSize.Children.Add(_fontSizeValue);
        content.Children.Add(Row(
            _runtime.L("文字大小", "Text size"),
            _runtime.L(
                "调整 OSD 的文字大小，范围为 8 到 24。",
                "Adjust OSD text size from 8 to 24."),
            fontSize));
        return content;
    }

    private UIElement BuildSensors()
    {
        var content = new StackPanel { Margin = new Thickness(8, 12, 8, 8) };
        content.Children.Add(Row(
            _runtime.L(
                "对于有多个温度的设备，显示",
                "For devices with multiple temperatures, show"),
            _runtime.L(
                "选择聚合方式；“全部”会保留每一个可用温度。",
                "Choose aggregation; All preserves every available temperature."),
            _temperatureMode));
        content.Children.Add(Row(
            _runtime.L(
                "对于内存利用率，显示",
                "For RAM utilization, show"),
            _runtime.L(
                "选择容量数值、百分比或同时显示两者。",
                "Show used/total capacity, percentage, or both."),
            _memoryDisplayMode));
        foreach (var group in OsdSensorCatalog.Groups)
        {
            var items = new StackPanel();
            foreach (var sensor in group.Sensors)
            {
                var hasHybridCores =
                    _runtime.Snapshot.Temperatures?
                        .CpuPerformanceCoreAverageClockMhz.HasValue == true &&
                    _runtime.Snapshot.Temperatures?
                        .CpuEfficiencyCoreAverageClockMhz.HasValue == true;
                if ((sensor is
                         OsdSensor.CpuPerformanceCoreAverageFrequency or
                         OsdSensor.CpuEfficiencyCoreAverageFrequency) &&
                    !hasHybridCores)
                {
                    continue;
                }
                if (sensor == OsdSensor.Fan2Speed &&
                    !DeviceModelDetector.HasSecondFan())
                {
                    continue;
                }
                var storageIndex = OsdSensorCatalog.StorageIndex(sensor);
                if (storageIndex >= 0 &&
                    storageIndex >= (_runtime.Snapshot.Temperatures?
                        .StorageDevices.Count ?? 0))
                {
                    continue;
                }
                var toggle = new CheckBox
                {
                    Content = _runtime.L(
                        OsdSensorCatalog.Chinese(
                            sensor,
                            DeviceModelDetector.HasSecondFan()),
                        OsdSensorCatalog.English(
                            sensor,
                            DeviceModelDetector.HasSecondFan())),
                    IsChecked = _draft.Sensors.Contains(sensor),
                    Margin = new Thickness(0, 5, 0, 5),
                    Tag = sensor
                };
                toggle.Click += (_, _) =>
                {
                    if (_syncing) return;
                    if (toggle.IsChecked == true)
                    {
                        if (!_draft.Sensors.Contains(sensor))
                            _draft.Sensors.Add(sensor);
                    }
                    else
                    {
                        _draft.Sensors.Remove(sensor);
                    }
                    Save();
                };
                items.Children.Add(toggle);
            }
            content.Children.Add(Card(
                _runtime.L(group.Chinese, group.English),
                items));
        }
        return content;
    }

    private void SyncControls()
    {
        _syncing = true;
        Select(_orientation, _draft.Orientation);
        Select(_refresh, _draft.RefreshIntervalSeconds);
        Select(_temperatureMode, _draft.MultipleTemperatureMode);
        Select(_memoryDisplayMode, _draft.MemoryDisplayMode);
        _fixed.IsChecked = _draft.FixedPosition;
        _opacity.Value = _draft.OpacityPercent;
        _opacityValue.Text = _draft.OpacityPercent.ToString(
            CultureInfo.InvariantCulture);
        _fontSize.Value = _draft.FontSize;
        _fontSizeValue.Text = _draft.FontSize.ToString(
            CultureInfo.InvariantCulture);
        _snap.Value = _draft.SnapThreshold;
        _snapValue.Text = _draft.SnapThreshold.ToString(
            CultureInfo.InvariantCulture);
        SyncExtendedControls();
        _syncing = false;
    }

    private void CommitOpacity()
    {
        if (_syncing) return;
        if (!int.TryParse(
                _opacityValue.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) || value is < 0 or > 100)
        {
            _status.Text = _runtime.L(
                "不透明度必须为 0 到 100 之间的整数。",
                "Opacity must be an integer between 0 and 100.");
            _opacityValue.Text = _draft.OpacityPercent.ToString(
                CultureInfo.InvariantCulture);
            return;
        }
        _status.Text = string.Empty;
        _draft.OpacityPercent = value;
        _opacity.Value = value;
        Save();
    }

    private void CommitSnapThreshold()
    {
        if (_syncing) return;
        if (!int.TryParse(
                _snapValue.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) || value is < 0 or > 100)
        {
            _status.Text = _runtime.L(
                "吸附阈值必须为 0 到 100 之间的整数。",
                "The snapping threshold must be an integer from 0 to 100.");
            _snapValue.Text = _draft.SnapThreshold.ToString(
                CultureInfo.InvariantCulture);
            return;
        }
        _status.Text = string.Empty;
        _draft.SnapThreshold = value;
        _snap.Value = value;
        Save();
    }

    private void CommitFontSize()
    {
        if (_syncing) return;
        if (!int.TryParse(
                _fontSizeValue.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) || value is < 8 or > 24)
        {
            _status.Text = _runtime.L(
                "文字大小必须为 8 到 24 之间的整数。",
                "Text size must be an integer from 8 to 24.");
            _fontSizeValue.Text = _draft.FontSize.ToString(
                CultureInfo.InvariantCulture);
            return;
        }
        _status.Text = string.Empty;
        _draft.FontSize = value;
        _fontSize.Value = value;
        Save();
    }

    private void Save()
    {
        _status.Text = _runtime.TrySetOsdSettings(_draft, out var error)
            ? string.Empty
            : _runtime.L("OSD 设置保存失败：", "OSD settings could not be saved: ") +
              error;
        _draft = Clone(_runtime.Settings.Osd);
    }

    private Border Row(string title, string description, UIElement control)
    {
        var grid = new Grid { MinHeight = 70 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush(_palette.Muted),
            FontSize = 12,
            Margin = new Thickness(0, 3, 14, 0),
            TextWrapping = TextWrapping.Wrap
        });
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(16, 0, 0, 0);
        }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border
        {
            Background = Brush(_palette.SurfaceRaised),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private Border Card(string title, UIElement child)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(child);
        return new Border
        {
            Background = Brush(_palette.SurfaceRaised),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content
        };
    }

    private Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 120,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Background = Brush(primary ? _palette.Accent : _palette.SurfaceRaised),
        Foreground = primary ? Brushes.White : Brush(_palette.Text),
        BorderBrush = Brush(primary ? _palette.Accent : _palette.Border),
        BorderThickness = new Thickness(1),
        Template = ModernTheme.RoundedButtonTemplate(10)
    };

    private Button TabButton(string text) => new()
    {
        Content = text,
        MinWidth = 112,
        MinHeight = 42,
        Padding = new Thickness(18, 8, 18, 8),
        FontWeight = FontWeights.SemiBold,
        BorderThickness = new Thickness(1),
        Template = ModernTheme.RoundedButtonTemplate(11)
    };

    private void SelectTab(
        UIElement selectedPage,
        params (Button Button, UIElement Page)[] tabs)
    {
        foreach (var tab in tabs)
        {
            var selected = ReferenceEquals(tab.Page, selectedPage);
            tab.Page.Visibility = selected
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyTabState(tab.Button, selected);
        }
    }

    private void ApplyTabState(Button button, bool selected)
    {
        button.Background = Brush(selected
            ? _palette.AccentSoft
            : _palette.SurfaceRaised);
        button.Foreground = selected
            ? Brush(_palette.Accent)
            : Brush(_palette.Text);
        button.BorderBrush = Brush(selected
            ? _palette.Accent
            : _palette.Border);
    }

    private static ToolkitOsdSettings Clone(ToolkitOsdSettings source) => new()
    {
        Orientation = source.Orientation,
        RefreshIntervalSeconds = source.RefreshIntervalSeconds,
        FixedPosition = source.FixedPosition,
        OpacityPercent = source.OpacityPercent,
        FontSize = source.FontSize,
        SnapThreshold = source.SnapThreshold,
        MultipleTemperatureMode = source.MultipleTemperatureMode,
        MemoryDisplayMode = source.MemoryDisplayMode,
        BackgroundColor = source.BackgroundColor,
        CategoryColor = source.CategoryColor,
        LabelColor = source.LabelColor,
        ValueColor = source.ValueColor,
        WarningColor = source.WarningColor,
        CriticalColor = source.CriticalColor,
        FpsWarningThreshold = source.FpsWarningThreshold,
        FpsCriticalThreshold = source.FpsCriticalThreshold,
        LowFpsThresholdMode = source.LowFpsThresholdMode,
        LowFpsWarningPercentage = source.LowFpsWarningPercentage,
        LowFpsCriticalPercentage = source.LowFpsCriticalPercentage,
        LowFpsWarningDelta = source.LowFpsWarningDelta,
        LowFpsCriticalDelta = source.LowFpsCriticalDelta,
        CpuTemperatureWarning = source.CpuTemperatureWarning,
        CpuTemperatureCritical = source.CpuTemperatureCritical,
        GpuHotSpotTemperatureWarning = source.GpuHotSpotTemperatureWarning,
        GpuHotSpotTemperatureCritical = source.GpuHotSpotTemperatureCritical,
        GpuTemperatureWarning = source.GpuTemperatureWarning,
        GpuTemperatureCritical = source.GpuTemperatureCritical,
        VramTemperatureWarning = source.VramTemperatureWarning,
        VramTemperatureCritical = source.VramTemperatureCritical,
        MemoryTemperatureWarning = source.MemoryTemperatureWarning,
        MemoryTemperatureCritical = source.MemoryTemperatureCritical,
        StorageTemperatureWarning = source.StorageTemperatureWarning,
        StorageTemperatureCritical = source.StorageTemperatureCritical,
        UsageWarningThreshold = source.UsageWarningThreshold,
        UsageCriticalThreshold = source.UsageCriticalThreshold,
        BatteryOutputPowerWarning = source.BatteryOutputPowerWarning,
        BatteryOutputPowerCritical = source.BatteryOutputPowerCritical,
        Sensors = source.Sensors.ToList(),
        HorizontalX = source.HorizontalX,
        HorizontalY = source.HorizontalY,
        VerticalX = source.VerticalX,
        VerticalY = source.VerticalY
    };

    private static void AddChoice<T>(ComboBox combo, string text, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, value));

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
