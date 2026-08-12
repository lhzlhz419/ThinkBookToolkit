using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class PerformanceModeOrderWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly List<ItsMode> _order;
    private readonly HashSet<ItsMode> _enabled;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    internal PerformanceModeOrderWindow(
        Window? owner,
        ToolkitRuntimeService runtime,
        FontFamily fontFamily,
        double fontSize)
    {
        if (owner is not null)
            Owner = owner;
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        _order = PerformanceModeCycle.NormalizeOrder(
            runtime.Settings.FnPerformanceModeOrder);
        _enabled = PerformanceModeCycle.NormalizeEnabled(
                runtime.Settings.FnPerformanceModeEnabled)
            .ToHashSet();
        Title = T("性能模式切换顺序", "Performance-mode switch order");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T(
                "Fn+Q 按从上到下的顺序循环；关闭的模式会被跳过，使用电池时也会自动跳过极客模式。",
                "Fn+Q cycles from top to bottom. Disabled modes are skipped, and Geek mode is also skipped automatically on battery power."),
            Foreground = Brush(_palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 15)
        });
        root.Children.Add(_rows);
        _status.Foreground = Brush(_palette.Danger);
        _status.Margin = new Thickness(0, 9, 0, 0);
        root.Children.Add(_status);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button
        {
            Content = T("取消", "Cancel"),
            MinWidth = 106,
            MinHeight = 38
        };
        var apply = new Button
        {
            Content = T("应用", "Apply"),
            MinWidth = 106,
            MinHeight = 38,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true
        };
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => Apply();
        footer.Children.Add(cancel);
        footer.Children.Add(apply);
        root.Children.Add(footer);
        Content = root;
        RebuildRows();
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        for (var index = 0; index < _order.Count; index++)
        {
            var rowIndex = index;
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8),
                MinHeight = 58
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(54)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            row.Children.Add(new TextBlock
            {
                Text = (index + 1).ToString(),
                Foreground = Brush(_palette.Muted),
                FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            var mode = new TextBlock
            {
                Text = ModeName(_order[index]),
                Foreground = Brush(_palette.Text),
                FontWeight = FontWeights.SemiBold,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(mode, 1);
            row.Children.Add(mode);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var enabled = new CheckBox
            {
                IsChecked = _enabled.Contains(_order[index]),
                ToolTip = T("在 Fn+Q 循环中启用", "Include in the Fn+Q cycle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var modeValue = _order[index];
            enabled.Checked += (_, _) =>
            {
                _enabled.Add(modeValue);
                _status.Text = string.Empty;
            };
            enabled.Unchecked += (_, _) =>
            {
                if (_enabled.Count <= 1 && _enabled.Contains(modeValue))
                {
                    enabled.IsChecked = true;
                    _status.Text = T(
                        "至少需要启用一个性能模式。",
                        "At least one performance mode must remain enabled.");
                    return;
                }
                _enabled.Remove(modeValue);
                _status.Text = string.Empty;
            };
            var up = MoveButton("↑", index > 0);
            var down = MoveButton("↓", index + 1 < _order.Count);
            down.Margin = new Thickness(6, 0, 0, 0);
            up.Click += (_, _) => Move(rowIndex, -1);
            down.Click += (_, _) => Move(rowIndex, 1);
            actions.Children.Add(enabled);
            actions.Children.Add(up);
            actions.Children.Add(down);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            _rows.Children.Add(new Border
            {
                Background = Brush(_palette.SurfaceRaised),
                BorderBrush = Brush(_palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 3, 10, 3),
                Child = row
            });
        }
    }

    private Button MoveButton(string text, bool enabled) => new()
    {
        Content = text,
        Width = 42,
        Height = 36,
        IsEnabled = enabled,
        FontSize = 17
    };

    private void Move(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= _order.Count)
            return;
        (_order[index], _order[target]) = (_order[target], _order[index]);
        RebuildRows();
    }

    private void Apply()
    {
        if (!_runtime.TrySetPerformanceModeConfiguration(
                _order,
                _enabled,
                out var error))
        {
            _status.Text = T(
                "无法保存切换顺序：",
                "Could not save the switch order: ") + error;
            return;
        }
        DialogResult = true;
        Close();
    }

    private string ModeName(ItsMode mode) => mode switch
    {
        ItsMode.PowerSaving => T("省电模式", "Cool"),
        ItsMode.Intelligent => T("智能模式", "Auto"),
        ItsMode.Performance => T("性能模式", "Performance"),
        ItsMode.Geek => T("极客模式", "Geek"),
        _ => T("未知", "Unknown")
    };

    private string T(string chinese, string english) =>
        _runtime.IsChinese ? chinese : english;

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
