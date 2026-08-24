using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed record FnKeyDiscoveredInfo(
    int Code,
    string Channel,
    string Name,
    DateTimeOffset Timestamp)
{
    public string BindingId => FnAutomationKeyIds.FromDiscovered(
        Channel,
        Code);
}

internal sealed class FnKeyDiscoveryWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly StackPanel _events = new();
    private readonly Button _toggle;
    private bool _discovering;

    public FnKeyDiscoveryWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        Title = runtime.L("发现 Fn 按键", "Discover Fn keys");
        Width = 620;
        Height = 560;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        var palette = ToolkitPalette.For(runtime.IsDark);
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        _toggle = new Button
        {
            MinWidth = 120,
            MinHeight = 38,
            Padding = new Thickness(14, 7, 14, 7)
        };
        _toggle.Click += async (_, _) =>
            await SetDiscoveringAsync(!_discovering);
        _runtime.FnKeyDiscovered += OnDiscovered;
        Closed += (_, _) =>
        {
            _ = _runtime.SetFnKeyDiscoveryModeAsync(false);
            _runtime.FnKeyDiscovered -= OnDiscovered;
        };
        Content = BuildLayout(palette);
        Loaded += async (_, _) => await SetDiscoveringAsync(true);
    }

    private UIElement BuildLayout(ToolkitPalette palette)
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L("发现 Fn 按键", "Discover Fn keys"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "按下 Fn 快捷键后，这里会显示事件来源、原始代码和识别名称；每条结果都可以添加到自定义 Fn 快捷键。发现模式下不会执行默认功能或自动化。",
                "Press Fn keys to show their source, raw code, and detected name. Every result can be added to custom Fn keys. Default actions and automations are suppressed during discovery."),
            Foreground = Brush(palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        });
        header.Children.Add(_toggle);
        root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 0),
            Content = _events
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private async System.Threading.Tasks.Task SetDiscoveringAsync(bool value)
    {
        var error = await _runtime.SetFnKeyDiscoveryModeAsync(value);
        _discovering = value && string.IsNullOrWhiteSpace(error);
        _toggle.Content = _discovering
            ? _runtime.L("停止发现", "Stop discovery")
            : _runtime.L("开始发现", "Start discovery");
        if (!string.IsNullOrWhiteSpace(error))
        {
            _events.Children.Insert(0, new TextBlock
            {
                Text = _runtime.L("无法开始发现：", "Could not start discovery: ") + error,
                Foreground = Brushes.IndianRed,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void OnDiscovered(object? sender, FnKeyDiscoveredInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
                OnDiscovered(sender, info)));
            return;
        }
        var palette = ToolkitPalette.For(_runtime.IsDark);
        _events.Children.Insert(0, BuildDiscoveredRow(info, palette));
    }

    private Border BuildDiscoveredRow(
        FnKeyDiscoveredInfo info,
        ToolkitPalette palette)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        grid.Children.Add(new TextBlock
        {
            Text = $"{info.Timestamp:HH:mm:ss} · {info.Channel} · " +
                   $"0x{unchecked((uint)info.Code):X} ({info.Code}) · " +
                   info.Name,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var alreadyAdded = _runtime.Settings.CustomFnKeyNames.ContainsKey(
            info.BindingId);
        var add = new Button
        {
            Content = alreadyAdded
                ? _runtime.L("已添加", "Added")
                : _runtime.L("添加到自定义 Fn 快捷键", "Add to custom Fn keys"),
            IsEnabled = !alreadyAdded,
            MinHeight = 32,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 0, 0)
        };
        add.Click += (_, _) =>
        {
            if (_runtime.TryAddDiscoveredFnKey(info, out var error))
            {
                add.Content = _runtime.L("已添加", "Added");
                add.IsEnabled = false;
            }
            else
            {
                add.ToolTip = error;
                _events.Children.Insert(0, new TextBlock
                {
                    Text = _runtime.L(
                        "无法添加按键：",
                        "Could not add the key: ") + error,
                    Foreground = Brushes.IndianRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        };
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        return new Border
        {
            Background = Brush(palette.SurfaceRaised),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
