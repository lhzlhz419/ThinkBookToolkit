using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal abstract class ToolkitViewModelBase : INotifyPropertyChanged, IDisposable
{
    private string _status = string.Empty;
    private bool _isBusy;

    protected ToolkitViewModelBase(ToolkitRuntimeService runtime)
    {
        Runtime = runtime;
    }

    protected ToolkitRuntimeService Runtime { get; }

    public string Status
    {
        get => _status;
        protected set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetField(ref _isBusy, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(name);
        return true;
    }

    public virtual void Dispose() { }
}

internal abstract class ToolkitPageBase : UserControl, IDisposable
{
    protected ToolkitPageBase(ToolkitRuntimeService runtime)
    {
        Runtime = runtime;
        Palette = ToolkitPalette.For(runtime.IsDark);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        Foreground = Brush(Palette.Text);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
    }

    protected ToolkitRuntimeService Runtime { get; }

    protected ToolkitPalette Palette { get; }

    protected string L(string chinese, string english) =>
        Runtime.L(chinese, english);

    protected Border Card(
        string title,
        UIElement content,
        string? description = null,
        string? glyph = null,
        string? accent = null,
        UIElement? headerAction = null)
    {
        var body = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        if (headerAction is not null)
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            heading.Children.Add(IconTile(glyph, accent ?? Palette.Accent));
        }
        var text = new StackPanel
        {
            Margin = string.IsNullOrWhiteSpace(glyph)
                ? new Thickness(0)
                : new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text)
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brush(Palette.Muted),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        Grid.SetColumn(text, 1);
        heading.Children.Add(text);
        if (headerAction is not null)
        {
            if (headerAction is FrameworkElement actionElement)
            {
                actionElement.HorizontalAlignment = HorizontalAlignment.Right;
                actionElement.VerticalAlignment = VerticalAlignment.Center;
                actionElement.Margin = new Thickness(16, 0, 0, 0);
            }
            Grid.SetColumn(headerAction, 2);
            heading.Children.Add(headerAction);
        }
        body.Children.Add(heading);
        if (content is FrameworkElement element)
            element.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(content);
        return new Border
        {
            Background = Brush(Palette.Surface),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 12),
            Child = body
        };
    }

    protected Border SettingRow(
        string title,
        string description,
        UIElement control,
        string? glyph = null)
    {
        var row = new Grid { MinHeight = 62 };
        row.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(glyph))
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var offset = string.IsNullOrWhiteSpace(glyph) ? 0 : 1;
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            row.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brush(Palette.Accent),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            labels.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 14, 0)
            });
        }
        Grid.SetColumn(labels, offset);
        row.Children.Add(labels);
        if (control is FrameworkElement controlElement)
        {
            controlElement.VerticalAlignment = VerticalAlignment.Center;
            controlElement.Margin = new Thickness(14, 0, 0, 0);
        }
        Grid.SetColumn(control, offset + 1);
        row.Children.Add(control);
        void ApplyResponsiveRow(bool compact)
        {
            Grid.SetRow(control, compact ? 1 : 0);
            Grid.SetColumn(control, compact ? offset : offset + 1);
            Grid.SetColumnSpan(control, compact
                ? Math.Max(1, row.ColumnDefinitions.Count - offset)
                : 1);
            if (control is FrameworkElement responsiveControl)
            {
                responsiveControl.HorizontalAlignment = compact
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Right;
                responsiveControl.Margin = compact
                    ? new Thickness(0, 10, 0, 0)
                    : new Thickness(14, 0, 0, 0);
            }
        }
        row.SizeChanged += (_, _) => ApplyResponsiveRow(row.ActualWidth < 610);
        ApplyResponsiveRow(compact: false);
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row
        };
    }

    protected Border MetricCard(
        string title,
        TextBlock value,
        string detail,
        string glyph,
        string accent,
        double valueFontSize = 22,
        bool keepValueOnOneLine = false)
    {
        value.Foreground = Brush(Palette.Text);
        value.FontSize = valueFontSize;
        value.FontWeight = FontWeights.SemiBold;
        value.TextWrapping = keepValueOnOneLine
            ? TextWrapping.NoWrap
            : TextWrapping.Wrap;
        value.TextTrimming = keepValueOnOneLine
            ? TextTrimming.CharacterEllipsis
            : TextTrimming.None;
        value.Margin = new Thickness(0, 13, 0, 0);
        var content = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(IconTile(glyph, accent, 34, 14));
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(Palette.Muted),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        content.Children.Add(header);
        content.Children.Add(value);
        content.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = Brush(Palette.Muted),
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0)
        });
        return new Border
        {
            MinHeight = 126,
            Background = Brush(Palette.Surface),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Child = content
        };
    }

    protected Button ActionButton(
        string text,
        bool primary = false,
        bool danger = false)
    {
        var color = danger ? Palette.Danger : Palette.Accent;
        return new Button
        {
            Content = text,
            MinWidth = 108,
            MinHeight = 38,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = primary || danger
                ? Brush(color)
                : Brush(Palette.SurfaceRaised),
            Foreground = primary || danger
                ? Brushes.White
                : Brush(Palette.Text),
            BorderBrush = Brush(primary || danger ? color : Palette.Border),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Template = ModernTheme.RoundedButtonTemplate(10)
        };
    }

    protected TextBlock StatusText()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        var emptyText = new Trigger
        {
            Property = TextBlock.TextProperty,
            Value = string.Empty
        };
        emptyText.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        style.Triggers.Add(emptyText);

        return new TextBlock
        {
            Foreground = Brush(Palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Style = style
        };
    }

    protected Border EmptyState(string message) => new()
    {
        Background = Brush(Palette.Surface),
        BorderBrush = Brush(Palette.Border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18),
        Padding = new Thickness(28),
        Child = new TextBlock
        {
            Text = message,
            Foreground = Brush(Palette.Muted),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        }
    };

    protected Border IconTile(
        string glyph,
        string accent,
        double size = 38,
        double fontSize = 15) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(Math.Max(8, size * .3)),
        Background = Tint(accent),
        Child = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = fontSize,
            Foreground = Brush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    protected SolidColorBrush Tint(string color)
    {
        var source = ColorFrom(color);
        var background = Runtime.IsDark ? ColorFrom("#172033") : Colors.White;
        var ratio = Runtime.IsDark ? .22 : .11;
        return Frozen(new Color
        {
            A = 255,
            R = (byte)(background.R + (source.R - background.R) * ratio),
            G = (byte)(background.G + (source.G - background.G) * ratio),
            B = (byte)(background.B + (source.B - background.B) * ratio)
        });
    }

    protected static SolidColorBrush Brush(string color) =>
        Frozen(ColorFrom(color));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected static Color ColorFrom(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    public virtual void Dispose()
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}

/// <summary>
/// Equal-width responsive panel. It changes column count from the available
/// width, so pages remain free of horizontal scrolling and nested scrollbars.
/// </summary>
internal sealed class AdaptiveUniformPanel : Panel
{
    public double MinimumItemWidth { get; set; } = 270;

    public double Spacing { get; set; } = 10;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? Math.Max(MinimumItemWidth, Children.Count * MinimumItemWidth)
            : Math.Max(0, availableSize.Width);
        var columns = ColumnCount(width);
        var itemWidth = Math.Max(0, (width - (columns - 1) * Spacing) / columns);
        var rowHeights = new List<double>();
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            var row = i / columns;
            if (row == rowHeights.Count)
                rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], Children[i].DesiredSize.Height);
        }
        return new Size(
            width,
            rowHeights.Count == 0
                ? 0
                : rowHeights.Sum() + (rowHeights.Count - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width);
        var itemWidth = Math.Max(
            0,
            (finalSize.Width - (columns - 1) * Spacing) / columns);
        var rowHeights = new double[(Children.Count + columns - 1) / columns];
        for (var i = 0; i < Children.Count; i++)
            rowHeights[i / columns] = Math.Max(
                rowHeights[i / columns],
                Children[i].DesiredSize.Height);
        var y = 0d;
        for (var row = 0; row < rowHeights.Length; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= Children.Count)
                    break;
                Children[index].Arrange(new Rect(
                    column * (itemWidth + Spacing),
                    y,
                    itemWidth,
                    rowHeights[row]));
            }
            y += rowHeights[row] + Spacing;
        }
        return finalSize;
    }

    private int ColumnCount(double width)
    {
        if (Children.Count == 0)
            return 1;
        return Math.Max(
            1,
            Math.Min(
                Children.Count,
                (int)Math.Floor((width + Spacing) / (MinimumItemWidth + Spacing))));
    }
}
