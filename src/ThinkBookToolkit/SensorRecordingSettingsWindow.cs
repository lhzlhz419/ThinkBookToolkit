using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class SensorRecordingSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly ComboBox _interval = new() { MinWidth = 170 };
    private readonly Slider _maximumPoints = new()
    {
        Minimum = 100,
        Maximum = 10_000,
        TickFrequency = 100
    };
    private readonly TextBox _maximumPointsValue = new()
    {
        Width = 90,
        MinHeight = 36,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new();
    private SensorRecordingSettings _draft;
    private bool _syncing;

    public SensorRecordingSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        _draft = Clone(runtime.Settings.SensorRecording);
        Title = runtime.L("传感器记录设置", "Sensor recording settings");
        Width = 820;
        Height = 760;
        MinWidth = 620;
        MinHeight = 520;
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
        foreach (var value in new[] { .5d, 1d, 2d, 3d, 5d })
        {
            _interval.Items.Add(new ComboBoxItem
            {
                Content = _runtime.L(
                    $"{value:0.#} 秒",
                    $"{value:0.#} seconds"),
                Tag = value
            });
        }
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = _runtime.L("传感器记录设置", "Sensor recording settings"),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
        });

        var content = new StackPanel { Margin = new Thickness(0, 4, 8, 8) };
        content.Children.Add(Row(
            _runtime.L("刷新间隔", "Refresh interval"),
            _runtime.L(
                "设置采样并实时写入文件的间隔。",
                "Choose how often a sample is written to the file."),
            _interval));
        var maximum = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _maximumPoints.Width = 240;
        maximum.Children.Add(_maximumPoints);
        maximum.Children.Add(_maximumPointsValue);
        maximum.Children.Add(new TextBlock
        {
            Text = _runtime.L("点", "points"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        content.Children.Add(Row(
            _runtime.L("最大绘制点数", "Maximum plot points"),
            _runtime.L(
                "查看记录时按此数量均匀采样，范围为 100 到 10000。",
                "Downsample charts to this many points, from 100 to 10000."),
            maximum));
        foreach (var group in OsdSensorCatalog.Groups)
        {
            var items = new StackPanel();
            foreach (var sensor in group.Sensors)
            {
                if (!SensorAvailable(sensor))
                    continue;
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
                    Margin = new Thickness(0, 5, 0, 5)
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
            if (items.Children.Count > 0)
                content.Children.Add(Card(
                    _runtime.L(group.Chinese, group.English),
                    items));
        }
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_status);
        var close = ActionButton(_runtime.L("关闭", "Close"));
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _interval.SelectionChanged += (_, _) =>
        {
            if (_syncing ||
                _interval.SelectedItem is not ComboBoxItem { Tag: double value })
                return;
            _draft.IntervalSeconds = value;
            Save();
        };
        _maximumPoints.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _draft.MaximumPlotPoints = (int)Math.Round(_maximumPoints.Value);
            _maximumPointsValue.Text = _draft.MaximumPlotPoints.ToString(
                CultureInfo.InvariantCulture);
            Save();
        };
        _maximumPointsValue.LostKeyboardFocus += (_, _) => CommitMaximum();
        _maximumPointsValue.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            CommitMaximum();
            Keyboard.ClearFocus();
        };
        return root;
    }

    private bool SensorAvailable(OsdSensor sensor)
    {
        if (sensor == OsdSensor.Fan2Speed && !DeviceModelDetector.HasSecondFan())
            return false;
        var hybrid = _runtime.Snapshot.Temperatures?
                         .CpuPerformanceCoreAverageClockMhz.HasValue == true &&
                     _runtime.Snapshot.Temperatures?
                         .CpuEfficiencyCoreAverageClockMhz.HasValue == true;
        if ((sensor is OsdSensor.CpuPerformanceCoreAverageFrequency or
                OsdSensor.CpuEfficiencyCoreAverageFrequency) && !hybrid)
            return false;
        var disk = OsdSensorCatalog.StorageIndex(sensor);
        return disk < 0 || disk <
            (_runtime.Snapshot.Temperatures?.StorageDevices.Count ?? 0);
    }

    private void CommitMaximum()
    {
        if (!int.TryParse(_maximumPointsValue.Text, out var value) ||
            value is < 100 or > 10_000)
        {
            _status.Text = _runtime.L(
                "最大绘制点数必须为 100 到 10000 之间的整数。",
                "Maximum plot points must be an integer from 100 to 10000.");
            _maximumPointsValue.Text = _draft.MaximumPlotPoints.ToString();
            return;
        }
        _status.Text = string.Empty;
        _draft.MaximumPlotPoints = value;
        _maximumPoints.Value = value;
        Save();
    }

    private void Save()
    {
        _status.Text = _runtime.TrySetSensorRecordingSettings(_draft, out var error)
            ? string.Empty
            : _runtime.L("保存失败：", "Save failed: ") + error;
        _draft = Clone(_runtime.Settings.SensorRecording);
    }

    private void SyncControls()
    {
        _syncing = true;
        _interval.SelectedItem = _interval.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, _draft.IntervalSeconds));
        _maximumPoints.Value = _draft.MaximumPlotPoints;
        _maximumPointsValue.Text = _draft.MaximumPlotPoints.ToString(
            CultureInfo.InvariantCulture);
        _syncing = false;
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
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
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

    private Button ActionButton(string text) => new()
    {
        Content = text,
        MinWidth = 120,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Background = Brush(_palette.Accent),
        Foreground = Brushes.White,
        BorderBrush = Brush(_palette.Accent),
        BorderThickness = new Thickness(1),
        Template = ModernTheme.RoundedButtonTemplate(10)
    };

    private static SensorRecordingSettings Clone(SensorRecordingSettings source) =>
        new()
        {
            IntervalSeconds = source.IntervalSeconds,
            MaximumPlotPoints = source.MaximumPlotPoints,
            Sensors = source.Sensors.ToList()
        };

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
