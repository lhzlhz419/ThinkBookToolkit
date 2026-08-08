using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    protected AdaptiveUniformPanel HardwareMonitorCards()
    {
        var panel = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 230,
            Spacing = 8
        };
        panel.Children.Add(HardwareMonitorCard(
            "CPU",
            nameof(HardwareMonitorViewModel.CpuModel),
            new MonitorSection(null,
            [
                new(L("利用率", "Utilization"), nameof(HardwareMonitorViewModel.CpuUtilization)),
                new(L("平均频率", "Average frequency"), nameof(HardwareMonitorViewModel.CpuAverageFrequency)),
                new(L("最高频率", "Maximum frequency"), nameof(HardwareMonitorViewModel.CpuMaximumFrequency)),
                new(L("温度", "Temperature"), nameof(HardwareMonitorViewModel.CpuTemperature)),
                new(L("功耗", "Power"), nameof(HardwareMonitorViewModel.CpuPower))
            ])));
        panel.Children.Add(HardwareMonitorCard(
            "GPU",
            nameof(HardwareMonitorViewModel.GpuModel),
            new MonitorSection(null,
            [
                new(L("利用率", "Utilization"), nameof(HardwareMonitorViewModel.GpuUtilization)),
                new(L("显存利用率", "VRAM utilization"), nameof(HardwareMonitorViewModel.GpuMemoryUtilization)),
                new(
                    L("核心频率", "Core frequency"),
                    nameof(HardwareMonitorViewModel.GpuCoreFrequency),
                    false,
                    L("显存频率", "VRAM frequency"),
                    nameof(HardwareMonitorViewModel.GpuMemoryFrequency)),
                new(
                    L("核心温度", "Core temperature"),
                    nameof(HardwareMonitorViewModel.GpuCoreTemperature),
                    false,
                    L("热点温度", "Hot spot temperature"),
                    nameof(HardwareMonitorViewModel.GpuHotSpotTemperature)),
                new(L("显存温度", "VRAM temperature"), nameof(HardwareMonitorViewModel.GpuMemoryTemperature)),
                new(L("功耗", "Power"), nameof(HardwareMonitorViewModel.GpuPower))
            ])));
        panel.Children.Add(HardwareMonitorCard(
            L("电池", "Battery"),
            null,
            new MonitorSection(null,
            [
                new(L("当前状态", "Status"), nameof(HardwareMonitorViewModel.BatteryState)),
                new(L("电量", "Charge"), nameof(HardwareMonitorViewModel.BatteryCharge)),
                new(L("健康度", "Health"), nameof(HardwareMonitorViewModel.BatteryHealth)),
                new(L("功率", "Power"), nameof(HardwareMonitorViewModel.BatteryPower))
            ])));
        panel.Children.Add(HardwareMonitorCard(
            L("内存与硬盘", "Memory and storage"),
            null,
            new MonitorSection(null,
            [
                new(L("物理内存", "Physical memory"), nameof(HardwareMonitorViewModel.PhysicalMemory)),
                new(L("虚拟内存", "Virtual memory"), nameof(HardwareMonitorViewModel.VirtualMemory)),
                new(L("内存插槽1温度", "Memory slot 1 temperature"), nameof(HardwareMonitorViewModel.MemorySlot1Temperature)),
                new(L("内存插槽2温度", "Memory slot 2 temperature"), nameof(HardwareMonitorViewModel.MemorySlot2Temperature))
            ],
            nameof(HardwareMonitorViewModel.StorageMetrics))));
        panel.Children.Add(HardwareMonitorCard(
            L("风扇", "Fans"),
            null,
            new MonitorSection(L("风扇转速", "Fan speed"),
            [
                new(L("风扇1转速", "Fan 1 speed"), nameof(HardwareMonitorViewModel.Fan1Speed)),
                new(L("风扇2转速", "Fan 2 speed"), nameof(HardwareMonitorViewModel.Fan2Speed))
            ]),
            new MonitorSection(L("转速目标", "Speed target"),
            [
                new(L("风扇1目标", "Fan 1 target"), nameof(HardwareMonitorViewModel.Fan1Target)),
                new(L("风扇2目标", "Fan 2 target"), nameof(HardwareMonitorViewModel.Fan2Target))
            ])));
        return panel;
    }

    private Border HardwareMonitorCard(
        string title,
        string? modelProperty,
        params MonitorSection[] sections)
    {
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(modelProperty))
        {
            var model = new TextBlock
            {
                FontSize = 11,
                Foreground = Brush(Palette.Muted),
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (string.Equals(
                    modelProperty,
                    nameof(HardwareMonitorViewModel.GpuModel),
                    StringComparison.Ordinal))
            {
                var binding = new MultiBinding
                {
                    Converter = new OptionalSuffixWhenNarrowConverter("Laptop GPU")
                };
                binding.Bindings.Add(new Binding(modelProperty));
                binding.Bindings.Add(new Binding(nameof(FrameworkElement.ActualWidth))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.Self)
                });
                binding.Bindings.Add(new Binding(nameof(TextBlock.FontFamily))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.Self)
                });
                binding.Bindings.Add(new Binding(nameof(TextBlock.FontSize))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.Self)
                });
                model.SetBinding(TextBlock.TextProperty, binding);
            }
            else
            {
                model.SetBinding(TextBlock.TextProperty, new Binding(modelProperty));
            }
            model.SetBinding(FrameworkElement.ToolTipProperty, new Binding(modelProperty));
            Grid.SetColumn(model, 1);
            header.Children.Add(model);
        }
        content.Children.Add(header);

        for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            var section = sections[sectionIndex];
            if (sectionIndex > 0)
            {
                content.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush(Palette.Border),
                    Margin = new Thickness(0, 8, 0, 7)
                });
            }
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                content.Children.Add(new TextBlock
                {
                    Text = section.Title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(Palette.Text),
                    Margin = new Thickness(0, sectionIndex == 0 ? 10 : 0, 0, 3)
                });
            }
            else if (sectionIndex == 0)
            {
                content.Children.Add(new Border { Height = 8 });
            }
            foreach (var row in section.Rows)
                content.Children.Add(HardwareMonitorRow(row));
            if (!string.IsNullOrWhiteSpace(section.DynamicRowsProperty))
                content.Children.Add(HardwareMonitorRows(section.DynamicRowsProperty));
        }

        return new Border
        {
            MinHeight = 210,
            Background = Brush(Palette.Surface),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 11, 12, 11),
            Child = content
        };
    }

    private FrameworkElement HardwareMonitorRow(MonitorRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.SecondaryProperty))
            return HardwareMonitorPairRow(row);

        var value = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(Palette.Text),
            TextAlignment = row.WideValue ? TextAlignment.Left : TextAlignment.Right,
            HorizontalAlignment = row.WideValue
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right,
            TextWrapping = row.WideValue ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = row.WideValue ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(row.Property));
        if (row.WideValue)
        {
            value.Margin = new Thickness(0, 2, 0, 0);
            return value;
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.Children.Add(new TextBlock
        {
            Text = row.Label,
            FontSize = 12,
            Foreground = Brush(Palette.Muted),
            Margin = new Thickness(0, 0, 8, 0)
        });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private FrameworkElement HardwareMonitorPairRow(MonitorRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var left = HardwareMonitorPairCell(row.Label, row.Property);
        left.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(left);

        var right = HardwareMonitorPairCell(
            row.SecondaryLabel ?? string.Empty,
            row.SecondaryProperty!);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private FrameworkElement HardwareMonitorPairCell(
        string label,
        string property)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush(Palette.Muted),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 5, 0)
        });
        var value = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(Palette.Text),
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(property));
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private FrameworkElement HardwareMonitorRows(string property)
    {
        var items = new ItemsControl
        {
            Focusable = false
        };
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(property));

        var template = new DataTemplate(typeof(HardwareMonitorMetric));
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        row.SetValue(DockPanel.LastChildFillProperty, true);

        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetValue(DockPanel.DockProperty, Dock.Right);
        value.SetValue(TextBlock.FontSizeProperty, 12d);
        value.SetValue(TextBlock.ForegroundProperty, Brush(Palette.Text));
        value.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        value.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        value.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(HardwareMonitorMetric.Value)));
        row.AppendChild(value);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.FontSizeProperty, 12d);
        label.SetValue(TextBlock.ForegroundProperty, Brush(Palette.Muted));
        label.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(HardwareMonitorMetric.Label)));
        row.AppendChild(label);

        template.VisualTree = row;
        items.ItemTemplate = template;
        return items;
    }

    private sealed class OptionalSuffixWhenNarrowConverter : IMultiValueConverter
    {
        private readonly string _suffix;

        public OptionalSuffixWhenNarrowConverter(string suffix)
        {
            _suffix = suffix;
        }

        public object Convert(
            object[] values,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            var fullText = values.Length > 0 && values[0] is string text
                ? text
                : "--";
            if (!fullText.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
                return fullText;

            var compactText = fullText[..^_suffix.Length].TrimEnd();
            if (values.Length < 2 ||
                values[1] is not double availableWidth ||
                availableWidth <= 0)
            {
                return compactText;
            }

            var fontFamily = values.Length > 2 && values[2] is FontFamily family
                ? family
                : SystemFonts.MessageFontFamily;
            var fontSize = values.Length > 3 && values[3] is double size && size > 0
                ? size
                : 11d;
            var formatted = new FormattedText(
                fullText,
                culture,
                FlowDirection.LeftToRight,
                new Typeface(
                    fontFamily,
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal),
                fontSize,
                Brushes.Black,
                1d);
            return formatted.WidthIncludingTrailingWhitespace <= availableWidth
                ? fullText
                : compactText;
        }

        public object[] ConvertBack(
            object value,
            Type[] targetTypes,
            object parameter,
            CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed record MonitorSection(
        string? Title,
        IReadOnlyList<MonitorRow> Rows,
        string? DynamicRowsProperty = null);

    private sealed record MonitorRow(
        string Label,
        string Property,
        bool WideValue = false,
        string? SecondaryLabel = null,
        string? SecondaryProperty = null);

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
