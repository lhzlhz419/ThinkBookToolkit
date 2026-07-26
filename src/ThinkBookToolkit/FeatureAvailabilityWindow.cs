using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class FeatureAvailabilityWindow : Window
{
    private readonly ToolkitPalette _palette;
    private readonly bool _isDark;
    private readonly bool _embeddedMode;

    public FeatureAvailabilityWindow(
        FeatureAvailabilityReport report,
        bool isDark,
        FontFamily fontFamily,
        bool embeddedMode = false)
    {
        _isDark = isDark;
        _embeddedMode = embeddedMode;
        _palette = ToolkitPalette.For(isDark);
        Title = "功能可用性";
        Width = 840;
        Height = 700;
        MinWidth = 680;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = fontFamily;
        FontSize = 14;
        UseLayoutRounding = true;
        Background = CanvasBrush();
        Foreground = Brush(_palette.Text);
        Content = BuildLayout(report);
    }

    private UIElement BuildLayout(FeatureAvailabilityReport report)
    {
        var root = new Grid
        {
            Margin = _embeddedMode
                ? new Thickness(0)
                : new Thickness(24, 22, 24, 18)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = _embeddedMode
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var available = report.Items.Count(item => item.Available);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = "功能可用性",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text)
        });
        titles.Children.Add(new TextBlock
        {
            Text = "不可用功能会自动从对应调控页面隐藏，原因仍完整保留在这里。",
            Foreground = Brush(_palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        header.Children.Add(titles);

        var summary = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        summary.Children.Add(SummaryPill($"{available} 可用", _palette.Success));
        summary.Children.Add(SummaryPill(
            $"{report.Items.Count - available} 不可用",
            _palette.Danger));
        Grid.SetColumn(summary, 1);
        header.Children.Add(summary);
        root.Children.Add(header);

        var content = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        foreach (var group in report.Items.GroupBy(item => item.Category))
            content.Children.Add(BuildCategoryCard(group.Key, group.ToArray()));

        if (_embeddedMode)
        {
            Grid.SetRow(content, 1);
            root.Children.Add(content);
        }
        else
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
        }

        var footer = new Grid { Margin = new Thickness(0, 12, 8, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "检测结果只用于决定界面显示，不会在此窗口执行硬件写入。",
            Foreground = Brush(_palette.Muted),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true,
            Background = Brush(_palette.Accent),
            Foreground = Brushes.White,
            BorderBrush = Brush(_palette.Accent),
            FontWeight = FontWeights.SemiBold,
            Template = ModernTheme.RoundedButtonTemplate(10)
        };
        close.Click += (_, _) => Close();
        if (!_embeddedMode)
        {
            Grid.SetColumn(close, 1);
            footer.Children.Add(close);
        }
        Grid.SetRow(footer, 2);
        footer.Visibility = _embeddedMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        root.Children.Add(footer);
        return root;
    }

    private Border BuildCategoryCard(
        string category,
        FeatureAvailability[] features)
    {
        var rows = new StackPanel();
        var title = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        title.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = category,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text)
        });
        var count = new TextBlock
        {
            Text = $"{features.Count(item => item.Available)}/{features.Length}",
            Foreground = Brush(_palette.Muted),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 1);
        title.Children.Add(count);
        rows.Children.Add(title);

        for (var index = 0; index < features.Length; index++)
        {
            rows.Children.Add(BuildFeatureRow(features[index]));
            if (index != features.Length - 1)
            {
                rows.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush(_palette.Border),
                    Margin = new Thickness(0, 2, 0, 8)
                });
            }
        }

        return new Border
        {
            Background = Brush(_palette.Surface),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(16, 14, 16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = rows
        };
    }

    private UIElement BuildFeatureRow(FeatureAvailability feature)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(220)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(104)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        row.Children.Add(new TextBlock
        {
            Text = feature.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        var accent = feature.Available ? _palette.Success : _palette.Danger;
        var status = new Border
        {
            Background = TintBrush(accent),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = feature.Available ? "✓ 可用" : "× 不可用",
                Foreground = Brush(accent),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
        Grid.SetColumn(status, 1);
        row.Children.Add(status);

        var detail = new TextBlock
        {
            Text = feature.Detail,
            Foreground = Brush(_palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);
        return row;
    }

    private Border SummaryPill(string text, string accent) => new()
    {
        Background = TintBrush(accent),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(8, 0, 0, 0),
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brush(accent),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        }
    };

    private SolidColorBrush TintBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = _isDark ? (byte)52 : (byte)28;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private System.Windows.Media.Brush CanvasBrush()
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(_palette.Canvas),
            (Color)ColorConverter.ConvertFromString(_palette.CanvasAlt),
            new Point(0, 0),
            new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
