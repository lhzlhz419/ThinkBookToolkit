using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class RefreshRateSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly HashSet<uint> _enabled;
    private bool _dynamicEnabled;
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };

    internal RefreshRateSettingsWindow(
        Window? owner,
        ToolkitRuntimeService runtime,
        DisplayRefreshRateState state,
        FontFamily fontFamily,
        double fontSize)
    {
        if (owner is not null)
            Owner = owner;
        _runtime = runtime;
        var palette = ToolkitPalette.For(runtime.IsDark);
        _enabled = RefreshRateController.EffectiveCycleRates(
                state.AvailableHz,
                runtime.Settings.RefreshRateCycleHz)
            .ToHashSet();
        _dynamicEnabled = state.DynamicSupported &&
            runtime.Settings.IncludeDynamicRefreshRateInCycle;

        Title = T("刷新率切换设置", "Refresh-rate switch settings");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 680;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);

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
                "勾选显示页下拉框和 Fn+R 循环中可以使用的刷新率。至少保留一项。",
                "Choose the refresh rates available in the Display page and the Fn+R cycle. At least one must remain enabled."),
            Foreground = Brush(palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 15)
        });

        foreach (var rate in state.AvailableHz.OrderBy(value => value))
        {
            var value = rate;
            var toggle = new CheckBox
            {
                Content = $"{rate} Hz",
                IsChecked = _enabled.Contains(rate),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 9, 12, 9)
            };
            toggle.Checked += (_, _) =>
            {
                _enabled.Add(value);
                _status.Text = string.Empty;
            };
            toggle.Unchecked += (_, _) =>
            {
                if (EnabledCount <= 1 && _enabled.Contains(value))
                {
                    toggle.IsChecked = true;
                    _status.Text = T(
                        "至少需要启用一个刷新率。",
                        "At least one refresh rate must remain enabled.");
                    return;
                }
                _enabled.Remove(value);
                _status.Text = string.Empty;
            };
            root.Children.Add(new Border
            {
                Background = Brush(palette.SurfaceRaised),
                BorderBrush = Brush(palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = toggle
            });
        }

        if (state.DynamicSupported)
        {
            var toggle = new CheckBox
            {
                Content = new DisplayRefreshRateMode(
                    state.DynamicMaximumHz,
                    true).DisplayName,
                IsChecked = _dynamicEnabled,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 9, 12, 9)
            };
            toggle.Checked += (_, _) =>
            {
                _dynamicEnabled = true;
                _status.Text = string.Empty;
            };
            toggle.Unchecked += (_, _) =>
            {
                if (EnabledCount <= 1 && _dynamicEnabled)
                {
                    toggle.IsChecked = true;
                    _status.Text = T(
                        "至少需要启用一个刷新率。",
                        "At least one refresh rate must remain enabled.");
                    return;
                }
                _dynamicEnabled = false;
                _status.Text = string.Empty;
            };
            root.Children.Add(new Border
            {
                Background = Brush(palette.SurfaceRaised),
                BorderBrush = Brush(palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = toggle
            });
        }

        _status.Foreground = Brush(palette.Danger);
        _status.Margin = new Thickness(0, 5, 0, 0);
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
            MinHeight = 38,
            IsCancel = true
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
    }

    private void Apply()
    {
        if (!_runtime.TrySetRefreshRateCycle(
                _enabled,
                _dynamicEnabled,
                out var error))
        {
            _status.Text = T(
                "无法保存刷新率设置：",
                "Could not save refresh-rate settings: ") + error;
            return;
        }
        DialogResult = true;
        Close();
    }

    private int EnabledCount => _enabled.Count +
        (_dynamicEnabled ? 1 : 0);

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
