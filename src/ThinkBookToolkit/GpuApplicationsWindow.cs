using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class GpuApplicationsWindow : Window
{
    public GpuApplicationsWindow(
        Window? owner,
        IReadOnlyList<DiscreteGpuApplication> applications,
        bool isChinese,
        bool isDark,
        FontFamily fontFamily,
        double fontSize)
    {
        if (owner is not null)
            Owner = owner;
        Title = isChinese
            ? "占用独立显卡的应用"
            : "Applications using the discrete GPU";
        Width = 760;
        Height = 480;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;

        var palette = ToolkitPalette.For(isDark);
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = isChinese
                ? "以下为 NVIDIA 驱动当前报告的非系统应用。"
                : "These are the non-system applications currently reported by the NVIDIA driver.",
            Foreground = Brush(palette.Muted),
            Margin = new Thickness(0, 5, 0, 14)
        });
        root.Children.Add(heading);

        var content = new StackPanel();
        if (applications.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = isChinese
                    ? "没有检测到占用独立显卡的非系统应用。"
                    : "No non-system application using the discrete GPU was detected.",
                Foreground = Brush(palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 10, 4, 10)
            });
        }
        else
        {
            foreach (var application in applications)
                content.Children.Add(ApplicationRow(application, palette));
        }
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var close = new Button
        {
            Content = isChinese ? "关闭" : "Close",
            MinWidth = 104,
            MinHeight = 38,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        Content = root;
    }

    private static Border ApplicationRow(
        DiscreteGpuApplication application,
        ToolkitPalette palette)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = application.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Text)
        });
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(application.ExecutablePath)
                ? "--"
                : application.ExecutablePath,
            Foreground = Brush(palette.Muted),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 12, 0)
        });
        panel.Children.Add(text);
        var pid = new TextBlock
        {
            Text = $"PID {application.ProcessId}",
            Foreground = Brush(palette.Muted),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pid, 1);
        panel.Children.Add(pid);
        return new Border
        {
            Background = Brush(palette.SurfaceRaised),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel
        };
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
