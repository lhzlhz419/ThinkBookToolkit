using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class AdvancedFanCurveEditor : Border
{
    private const double LabelColumnWidth = 190;
    private const double PointColumnWidth = 132;
    private static readonly double[] RowHeights = [42, 48, 48, 48, 48, 48, 48, 48, 48, 46];
    private static readonly double TableContentHeight = RowHeights.Sum();

    private readonly bool _isChinese;
    private readonly ToolkitPalette _palette;
    private readonly Grid _tableHost = new();
    private readonly TextBlock _validation = new();
    private readonly List<PointControls> _controls = [];
    private List<AdvancedFanCurvePoint> _points;

    public AdvancedFanCurveEditor(
        bool isChinese,
        bool isDark,
        IReadOnlyList<AdvancedFanCurvePoint> points)
    {
        _isChinese = isChinese;
        _palette = ToolkitPalette.For(isDark);
        _points = points.Select(AdvancedFanCurve.Clone).ToList();

        Background = Brush(_palette.SurfaceRaised);
        BorderBrush = Brush(_palette.Border);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(13);
        Padding = new Thickness(12);

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = L("高级曲线点位", "Advanced curve points"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text)
        });
        root.Children.Add(new TextBlock
        {
            Text = L(
                "达到任一升速阈值时进入下一档；CPU 和 GPU 均低于降速阈值时返回上一档。转速必须为 100 RPM 的整数倍。按住 Shift 滚动鼠标滚轮可左右移动表格。",
                "The next level is selected when either ramp-up threshold is reached; both temperatures must fall below the ramp-down thresholds to move down. RPM values must be multiples of 100. Hold Shift while using the mouse wheel to scroll the table horizontally."),
            Foreground = Brush(_palette.Muted),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });
        root.Children.Add(_tableHost);

        _validation.Foreground = Brush(_palette.Danger);
        _validation.FontSize = 12;
        _validation.TextWrapping = TextWrapping.Wrap;
        _validation.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_validation);
        Child = root;

        RebuildTable();
    }

    public event EventHandler? ValuesChanged;

    public void SetValues(IReadOnlyList<AdvancedFanCurvePoint> points)
    {
        _points = points.Select(AdvancedFanCurve.Clone).ToList();
        RebuildTable();
    }

    public bool TryGetSettings(
        double smoothing,
        FanRpmLimits limits,
        out AdvancedFanCurveSettings settings,
        out string error)
    {
        settings = new AdvancedFanCurveSettings
        {
            TemperatureSmoothing = smoothing,
            Points = []
        };
        if (!TryReadPoints(out var points, out error))
        {
            _validation.Text = error;
            return false;
        }
        if (!AdvancedFanCurve.TryValidate(points, out var validationError))
        {
            error = LocalizeValidation(validationError);
            _validation.Text = error;
            return false;
        }
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point.Fan1Rpm < limits.Fan1MinimumRpm ||
                point.Fan1Rpm > limits.Fan1MaximumRpm ||
                point.Fan2Rpm < limits.Fan2MinimumRpm ||
                point.Fan2Rpm > limits.Fan2MaximumRpm)
            {
                error = L(
                    $"点位 {index + 1} 的 Fan 1 转速必须位于 {limits.Fan1MinimumRpm}–{limits.Fan1MaximumRpm} RPM，Fan 2 转速必须位于 {limits.Fan2MinimumRpm}–{limits.Fan2MaximumRpm} RPM。",
                    $"Point {index + 1}: Fan 1 must be within {limits.Fan1MinimumRpm}–{limits.Fan1MaximumRpm} RPM and Fan 2 within {limits.Fan2MinimumRpm}–{limits.Fan2MaximumRpm} RPM.");
                _validation.Text = error;
                return false;
            }
        }

        settings.Points = points;
        _validation.Text = string.Empty;
        return true;
    }

    private void RebuildTable()
    {
        _tableHost.Children.Clear();
        _tableHost.ColumnDefinitions.Clear();
        _controls.Clear();
        _tableHost.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(LabelColumnWidth)
        });
        _tableHost.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var labels = CreateRowsGrid();
        labels.Tag = "AdvancedFanCurveLabels";
        labels.Height = TableContentHeight;
        labels.VerticalAlignment = VerticalAlignment.Top;
        string[] labelTexts =
        [
            L("点位", "Point"),
            "Fan 1 RPM",
            "Fan 2 RPM",
            L("CPU 升速阈值（°C）", "CPU ramp-up (°C)"),
            L("CPU 降速阈值（°C）", "CPU ramp-down (°C)"),
            L("GPU 升速阈值（°C）", "GPU ramp-up (°C)"),
            L("GPU 降速阈值（°C）", "GPU ramp-down (°C)"),
            L("升速（RPM/s）", "Ramp-up (RPM/s)"),
            L("降速（RPM/s）", "Ramp-down (RPM/s)"),
            L("增删点位", "Insert / remove")
        ];
        for (var row = 0; row < labelTexts.Length; row++)
        {
            labels.Children.Add(Cell(
                new TextBlock
                {
                    Text = labelTexts[row],
                    Foreground = Brush(row == 0 ? _palette.Text : _palette.Muted),
                    FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                },
                row,
                0,
                horizontalPadding: 10));
        }
        _tableHost.Children.Add(labels);

        var pointsGrid = CreateRowsGrid();
        pointsGrid.Tag = "AdvancedFanCurvePoints";
        pointsGrid.Width = Math.Max(1, _points.Count) * PointColumnWidth;
        pointsGrid.Height = TableContentHeight;
        pointsGrid.VerticalAlignment = VerticalAlignment.Top;
        for (var index = 0; index < _points.Count; index++)
        {
            pointsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(PointColumnWidth)
            });
            AddPointColumn(pointsGrid, index, _points[index]);
        }

        var horizontalScroll = new ScrollViewer
        {
            Tag = "AdvancedFanCurveHorizontalScroll",
            Content = pointsGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.HorizontalOnly,
            Focusable = false
        };
        horizontalScroll.PreviewMouseWheel += HorizontalScrollOnPreviewMouseWheel;
        Grid.SetColumn(horizontalScroll, 1);
        _tableHost.Children.Add(horizontalScroll);
        _validation.Text = string.Empty;
    }

    private void AddPointColumn(
        Grid grid,
        int index,
        AdvancedFanCurvePoint point)
    {
        var controls = new PointControls
        {
            Fan1 = IntegerBox(point.Fan1Rpm),
            Fan2 = IntegerBox(point.Fan2Rpm),
            CpuUp = OptionalIntegerBox(point.CpuRampUpTemperatureC),
            CpuDown = OptionalIntegerBox(point.CpuRampDownTemperatureC),
            GpuUp = OptionalIntegerBox(point.GpuRampUpTemperatureC),
            GpuDown = OptionalIntegerBox(point.GpuRampDownTemperatureC),
            RampUp = RateBox(point.RampUpRpmPerSecond),
            RampDown = RateBox(point.RampDownRpmPerSecond)
        };

        controls.CpuDown.IsEnabled = index > 0;
        controls.GpuDown.IsEnabled = index > 0;
        controls.CpuUp.IsEnabled = index < _points.Count - 1;
        controls.GpuUp.IsEnabled = index < _points.Count - 1;

        grid.Children.Add(Cell(
            new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                Foreground = Brush(_palette.Text),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            0,
            index));
        grid.Children.Add(Cell(controls.Fan1, 1, index));
        grid.Children.Add(Cell(controls.Fan2, 2, index));
        grid.Children.Add(Cell(controls.CpuUp, 3, index));
        grid.Children.Add(Cell(controls.CpuDown, 4, index));
        grid.Children.Add(Cell(controls.GpuUp, 5, index));
        grid.Children.Add(Cell(controls.GpuDown, 6, index));
        grid.Children.Add(Cell(controls.RampUp, 7, index));
        grid.Children.Add(Cell(controls.RampDown, 8, index));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var insert = PointActionButton("+", L("在右侧增加点位", "Insert a point to the right"));
        insert.Click += (_, _) => InsertPointAfter(index);
        var remove = PointActionButton("−", L("删除此点位", "Remove this point"));
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.IsEnabled = _points.Count > 2;
        remove.Click += (_, _) => RemovePoint(index);
        actions.Children.Add(insert);
        actions.Children.Add(remove);
        grid.Children.Add(Cell(actions, 9, index));

        _controls.Add(controls);
    }

    private Grid CreateRowsGrid()
    {
        var grid = new Grid
        {
            Background = Brush(_palette.Surface)
        };
        foreach (var height in RowHeights)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(height)
            });
        }
        return grid;
    }

    private Border Cell(
        UIElement content,
        int row,
        int column,
        double horizontalPadding = 6)
    {
        var border = new Border
        {
            Background = Brush(row == 0 ? _palette.SurfaceRaised : _palette.Surface),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(horizontalPadding, 5, horizontalPadding, 5),
            Child = content
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        return border;
    }

    private Button PointActionButton(string text, string toolTip) => new()
    {
        Content = text,
        ToolTip = toolTip,
        Width = 36,
        Height = 30,
        Padding = new Thickness(0),
        FontSize = 17,
        FontWeight = FontWeights.SemiBold
    };

    private TextBox IntegerBox(int value)
    {
        var box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 3, 4, 3)
        };
        box.TextChanged += (_, _) => ValuesChanged?.Invoke(this, EventArgs.Empty);
        return box;
    }

    private TextBox OptionalIntegerBox(int? value)
    {
        var box = IntegerBox(value ?? 0);
        box.Text = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return box;
    }

    private ComboBox RateBox(double value)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        foreach (var rate in AdvancedFanCurve.AllowedRates
                     .OrderBy(rate => rate == 0 ? double.MaxValue : rate))
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = rate <= 0
                    ? L("无限制", "inf")
                    : rate.ToString("0", CultureInfo.InvariantCulture),
                Tag = rate
            });
        }
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, value)) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) => ValuesChanged?.Invoke(this, EventArgs.Empty);
        return combo;
    }

    private void HorizontalScrollOnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            scroll.ScrollToHorizontalOffset(
                Math.Clamp(
                    scroll.HorizontalOffset - e.Delta,
                    0,
                    scroll.ScrollableWidth));
            e.Handled = true;
            return;
        }

        e.Handled = true;
        _tableHost.RaiseEvent(new MouseWheelEventArgs(
            e.MouseDevice,
            e.Timestamp,
            e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = _tableHost
        });
    }

    private void InsertPointAfter(int index)
    {
        if (!TryReadPoints(out var current, out var error))
        {
            _validation.Text = error;
            return;
        }

        var left = current[index];
        AdvancedFanCurvePoint inserted;
        if (index == current.Count - 1)
        {
            var previous = current[Math.Max(0, index - 1)];
            left.CpuRampUpTemperatureC = InteriorUpperThreshold(
                previous.CpuRampUpTemperatureC,
                left.CpuRampDownTemperatureC);
            left.GpuRampUpTemperatureC = InteriorUpperThreshold(
                previous.GpuRampUpTemperatureC,
                left.GpuRampDownTemperatureC);
            inserted = new AdvancedFanCurvePoint
            {
                Fan1Rpm = left.Fan1Rpm,
                Fan2Rpm = left.Fan2Rpm,
                CpuRampUpTemperatureC = null,
                CpuRampDownTemperatureC = TerminalLowerThreshold(
                    left.CpuRampDownTemperatureC,
                    left.CpuRampUpTemperatureC),
                GpuRampUpTemperatureC = null,
                GpuRampDownTemperatureC = TerminalLowerThreshold(
                    left.GpuRampDownTemperatureC,
                    left.GpuRampUpTemperatureC),
                RampUpRpmPerSecond = left.RampUpRpmPerSecond,
                RampDownRpmPerSecond = left.RampDownRpmPerSecond
            };
        }
        else
        {
            var right = current[index + 1];
            inserted = new AdvancedFanCurvePoint
            {
                Fan1Rpm = left.Fan1Rpm,
                Fan2Rpm = left.Fan2Rpm,
                CpuRampUpTemperatureC = left.CpuRampUpTemperatureC ?? right.CpuRampUpTemperatureC,
                CpuRampDownTemperatureC = left.CpuRampDownTemperatureC ?? right.CpuRampDownTemperatureC,
                GpuRampUpTemperatureC = left.GpuRampUpTemperatureC ?? right.GpuRampUpTemperatureC,
                GpuRampDownTemperatureC = left.GpuRampDownTemperatureC ?? right.GpuRampDownTemperatureC,
                RampUpRpmPerSecond = left.RampUpRpmPerSecond,
                RampDownRpmPerSecond = left.RampDownRpmPerSecond
            };
        }

        current.Insert(index + 1, inserted);
        EnforceBoundaryBlanks(current);
        _points = current;
        RebuildTable();
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemovePoint(int index)
    {
        if (_controls.Count <= 2)
            return;
        if (!TryReadPoints(out var current, out var error))
        {
            _validation.Text = error;
            return;
        }

        current.RemoveAt(index);
        EnforceBoundaryBlanks(current);
        _points = current;
        RebuildTable();
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryReadPoints(
        out List<AdvancedFanCurvePoint> points,
        out string error)
    {
        points = [];
        error = string.Empty;
        for (var index = 0; index < _controls.Count; index++)
        {
            var point = _controls[index];
            if (!TryInteger(point.Fan1.Text, out var fan1) ||
                !TryInteger(point.Fan2.Text, out var fan2))
            {
                error = L(
                    $"点位 {index + 1} 的风扇转速必须是整数。",
                    $"Point {index + 1} fan speeds must be integers.");
                return false;
            }
            if (!TryOptionalInteger(point.CpuUp.Text, out var cpuUp) ||
                !TryOptionalInteger(point.CpuDown.Text, out var cpuDown) ||
                !TryOptionalInteger(point.GpuUp.Text, out var gpuUp) ||
                !TryOptionalInteger(point.GpuDown.Text, out var gpuDown) ||
                point.RampUp.SelectedItem is not ComboBoxItem { Tag: double rampUp } ||
                point.RampDown.SelectedItem is not ComboBoxItem { Tag: double rampDown })
            {
                error = L(
                    $"点位 {index + 1} 有未填写或无效的参数。",
                    $"Point {index + 1} has a missing or invalid value.");
                return false;
            }

            points.Add(new AdvancedFanCurvePoint
            {
                Fan1Rpm = fan1,
                Fan2Rpm = fan2,
                CpuRampUpTemperatureC = cpuUp,
                CpuRampDownTemperatureC = cpuDown,
                GpuRampUpTemperatureC = gpuUp,
                GpuRampDownTemperatureC = gpuDown,
                RampUpRpmPerSecond = rampUp,
                RampDownRpmPerSecond = rampDown
            });
        }
        return true;
    }

    private static int InteriorUpperThreshold(int? previousUpper, int? lower) =>
        Math.Min(127, Math.Max(previousUpper ?? 0, lower ?? 0));

    private static int TerminalLowerThreshold(int? currentLower, int? upper) =>
        Math.Min(127, Math.Max(currentLower ?? 0, (upper ?? 3) - 3));

    private static void EnforceBoundaryBlanks(IReadOnlyList<AdvancedFanCurvePoint> points)
    {
        points[0].CpuRampDownTemperatureC = null;
        points[0].GpuRampDownTemperatureC = null;
        points[^1].CpuRampUpTemperatureC = null;
        points[^1].GpuRampUpTemperatureC = null;
    }

    private static bool TryInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryOptionalInteger(string text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!TryInteger(text, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private string LocalizeValidation(string error)
    {
        if (!_isChinese)
            return error;
        if (error.Contains("at least two", StringComparison.Ordinal))
            return "高级曲线至少需要两个点。";
        if (error.Contains("multiples of 100", StringComparison.Ordinal))
            return "所有风扇转速必须为 100 RPM 的整数倍。";
        if (error.Contains("unsupported", StringComparison.Ordinal))
            return "请选择有效的升速和降速限制。";
        if (error.Contains("lowest-temperature", StringComparison.Ordinal))
            return "最低温度点的降速阈值必须留空。";
        if (error.Contains("highest-temperature", StringComparison.Ordinal))
            return "最高温度点的升速阈值必须留空。";
        if (error.Contains("missing", StringComparison.Ordinal))
            return "除首尾强制留空的阈值外，其余阈值都必须填写。";
        if (error.Contains("outside", StringComparison.Ordinal))
            return "温度阈值必须位于 0–127 °C。";
        return "右侧的风扇转速和温度阈值不能小于左侧。";
    }

    private string L(string chinese, string english) =>
        _isChinese ? chinese : english;

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private sealed class PointControls
    {
        public required TextBox Fan1 { get; init; }
        public required TextBox Fan2 { get; init; }
        public required TextBox CpuUp { get; init; }
        public required TextBox CpuDown { get; init; }
        public required TextBox GpuUp { get; init; }
        public required TextBox GpuDown { get; init; }
        public required ComboBox RampUp { get; init; }
        public required ComboBox RampDown { get; init; }
    }
}
