using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThinkBookToolkit;

internal sealed class DeviceInformationWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly bool _isDark;
    private readonly bool _embeddedMode;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(16, 2, 16, 16) };
    private readonly TextBlock _loading = new();
    private readonly List<Border> _cards = [];

    public DeviceInformationWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        bool embeddedMode = false)
    {
        _t = translate;
        _isDark = isDark;
        _embeddedMode = embeddedMode;
        Title = _t("DeviceInformation");
        Width = 860;
        Height = 720;
        MinWidth = 700;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Content = BuildLayout();
        ApplyTheme();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private UIElement BuildLayout()
    {
        if (_embeddedMode)
            _content.Margin = new Thickness(0);

        _loading.Text = _t("ReadingDeviceInformation");
        _loading.HorizontalAlignment = HorizontalAlignment.Center;
        _loading.VerticalAlignment = VerticalAlignment.Center;

        var closeButton = new Button
        {
            Content = _t("Close"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => Close();
        closeButton.Visibility = _embeddedMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        var footer = new Grid
        {
            Margin = _embeddedMode
                ? new Thickness(0)
                : new Thickness(16, 8, 16, 12),
            Visibility = _embeddedMode
                ? Visibility.Collapsed
                : Visibility.Visible
        };
        footer.Children.Add(closeButton);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = _embeddedMode
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (_embeddedMode)
        {
            root.Children.Add(_content);
        }
        else
        {
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _content
            });
        }
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Grid.SetRowSpan(_loading, 2); root.Children.Add(_loading);
        return root;
    }

    private async Task LoadAsync()
    {
        try
        {
            var info = await Task.Run(DeviceInformationService.ReadAll, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            var warrantyCard = Render(info);
            _loading.Visibility = Visibility.Collapsed;
            await warrantyCard.LoadAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _loading.Text = string.Format(_t("DeviceInformationReadFailedFormat"), ex.Message);
            _loading.Visibility = Visibility.Visible;
        }
    }

    private WarrantyCard Render(DeviceInformationSnapshot info)
    {
        var warranty = new WarrantyCard(_t, _isDark);
        _content.Children.Add(BuildDeviceCard(info));
        _content.Children.Add(warranty);

        if (info.Cpu is not null)
            _content.Children.Add(BuildCard("CPU",
                [(info.Cpu.Name, string.Format(_t("CpuTopologyFormat"), info.Cpu.Cores, info.Cpu.Threads))]));
        _content.Children.Add(BuildCard("GPU", info.Gpus.Select(g =>
            (g.Name, g.UsesSharedMemory
                ? _t("SharedGraphicsMemory")
                : g.DedicatedMemoryBytes.HasValue
                    ? string.Format(_t("VideoMemoryFormat"), FormatBytes(g.DedicatedMemoryBytes.Value))
                    : _t("NoInformation")))));
        _content.Children.Add(BuildStorageCard(info.Partitions));

        var installed = string.Format(
            _t("InstalledMemoryFormat"),
            FormatBytes(info.InstalledMemoryBytes),
            FormatBytes(info.UsableMemoryBytes));
        _content.Children.Add(BuildCard(_t("Memory"), info.Memory.Select(m =>
                ($"{FormatBytes(m.Capacity)}  {m.Type}  {m.Speed} MHz",
                    JoinNonEmpty(m.Locator, m.Manufacturer, m.PartNumber))), installed));
        if (info.Motherboard is not null)
            _content.Children.Add(BuildCard(_t("Motherboard"),
                [(EmptyAsNoInfo(info.Motherboard.Product),
                    JoinNonEmpty(info.Motherboard.Manufacturer, info.Motherboard.Version))]));
        _content.Children.Add(BuildCard(_t("Displays"), info.Displays.Select((d, i) =>
            ($"{_t("Display")} {i + 1}: {d.Name}",
                $"{d.Width} × {d.Height} @ {d.RefreshRate} Hz"))));
        ApplyTheme();
        return warranty;
    }

    private Border BuildDeviceCard(DeviceInformationSnapshot info)
    {
        var content = CardContent(_t("Device"));
        content.Children.Add(DeviceRow(_t("WindowsVersion"), info.WindowsVersion));
        content.Children.Add(DeviceRow(_t("DeviceName"), info.DeviceName));
        content.Children.Add(DeviceRow(_t("DeviceModel"), info.Identity.Model));
        content.Children.Add(DeviceRow(_t("ProductNumber"), info.Identity.ProductNumber));
        content.Children.Add(DeviceRow(_t("DeviceCode"), info.Identity.DeviceCode));
        content.Children.Add(DeviceRow(_t("SerialNumber"), info.Identity.SerialNumber, hideByDefault: true));
        content.Children.Add(DeviceRow(_t("BiosVersion"), info.Identity.BiosVersion));
        AddFirmwareRow(content, _t("MeVersion"), info.Firmware.MeVersion);
        AddFirmwareRow(content, _t("AmdPspVersion"), info.Firmware.AmdPspVersion);
        content.Children.Add(DeviceRow(_t("SmbiosVersion"), info.Identity.SmbiosVersion));
        AddFirmwareRow(content, _t("AcpiVersion"), info.Firmware.AcpiVersion);
        AddFirmwareRow(content, _t("UefiVersion"), info.Firmware.UefiVersion);
        content.Children.Add(DeviceRow(_t("DeviceId"), info.DeviceId));
        content.Children.Add(DeviceRow(_t("ProductId"), info.WindowsProductId));
        return NewCard(content);
    }

    private void AddFirmwareRow(Panel content, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            content.Children.Add(DeviceRow(label, value));
    }

    private UIElement DeviceRow(string label, string value, bool hideByDefault = false)
    {
        var actualValue = EmptyAsNoInfo(value);
        var valueText = new TextBlock
        {
            Text = hideByDefault && !string.IsNullOrWhiteSpace(value)
                ? new string('*', value.Length)
                : actualValue,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelText = new TextBlock
        {
            Text = label + ":",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0)
        };
        row.Children.Add(labelText);
        Grid.SetColumn(valueText, 1); row.Children.Add(valueText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 4, 0)
        };
        if (hideByDefault && !string.IsNullOrWhiteSpace(value))
        {
            var revealed = false;
            var eye = IconButton("\uE890", _t("ShowSerialNumber"));
            eye.Content = EyeIcon(slashed: false);
            eye.Click += (_, _) =>
            {
                revealed = !revealed;
                valueText.Text = revealed ? value : new string('*', value.Length);
                eye.Content = EyeIcon(slashed: revealed);
                eye.ToolTip = _t(revealed ? "HideSerialNumber" : "ShowSerialNumber");
            };
            actions.Children.Add(eye);
        }
        var copy = IconButton("\uE8C8", _t("Copy"));
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(actualValue); }
            catch { }
        };
        actions.Children.Add(copy);
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        return row;
    }

    private Border BuildStorageCard(IReadOnlyList<PartitionInfo> partitions)
    {
        var total = partitions.Aggregate<PartitionInfo, ulong>(0, (sum, item) => sum + item.TotalBytes);
        var content = CardContent(_t("Storage"), string.Format(_t("TotalStorageFormat"), FormatBytes(total)));
        foreach (var partition in partitions)
        {
            var title = string.IsNullOrWhiteSpace(partition.VolumeLabel)
                ? partition.Name
                : $"{partition.Name}  {partition.VolumeLabel}";
            var percent = partition.TotalBytes == 0
                ? 0
                : Math.Clamp(partition.UsedBytes * 100.0 / partition.TotalBytes, 0, 100);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var usage = new TextBlock
            {
                Text = string.Format(_t("UsedTotalFormat"), FormatBytes(partition.UsedBytes), FormatBytes(partition.TotalBytes)),
                Opacity = .72
            };
            Grid.SetColumn(usage, 1); header.Children.Add(usage);
            row.Children.Add(header);
            row.Children.Add(new ProgressBar
            {
                Height = 7,
                Minimum = 0,
                Maximum = 100,
                Value = percent,
                Foreground = Brush("#5898fd"),
                Background = Brush(_isDark ? "#374151" : "#e5e7eb"),
                Margin = new Thickness(0, 7, 0, 5)
            });
            row.Children.Add(new TextBlock { Text = EmptyAsNoInfo(partition.DiskModel), Opacity = .72 });
            content.Children.Add(row);
        }
        if (partitions.Count == 0)
            content.Children.Add(new TextBlock { Text = _t("NoInformation"), Opacity = .72 });
        return NewCard(content);
    }

    private Border BuildCard(
        string title,
        IEnumerable<(string Primary, string Secondary)> rows,
        string? subtitle = null)
    {
        var content = CardContent(title, subtitle);
        var any = false;
        foreach (var (primary, secondary) in rows)
        {
            any = true;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock
            {
                Text = EmptyAsNoInfo(primary),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(new TextBlock
            {
                Text = EmptyAsNoInfo(secondary),
                Opacity = .72,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(row);
        }
        if (!any) content.Children.Add(new TextBlock { Text = _t("NoInformation"), Opacity = .72 });
        return NewCard(content);
    }

    private StackPanel CardContent(string title, string? subtitle = null)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, subtitle is null ? 13 : 3)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            content.Children.Add(new TextBlock
            {
                Text = subtitle,
                Opacity = .72,
                Margin = new Thickness(0, 0, 0, 13),
                TextWrapping = TextWrapping.Wrap
            });
        return content;
    }

    private Button IconButton(string glyph, string tooltip) => new()
    {
        Content = glyph,
        ToolTip = tooltip,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 16,
        Width = 40,
        Height = 36,
        MinWidth = 40,
        MinHeight = 36,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 0, 0),
        Foreground = Brush(_isDark ? "#f9fafb" : "#374151"),
        Background = Brush(_isDark ? "#253044" : "#f3f4f6"),
        BorderBrush = Brush(_isDark ? "#526178" : "#9ca3af"),
        BorderThickness = new Thickness(1)
    };

    private UIElement EyeIcon(bool slashed)
    {
        var foreground = Brush(_isDark ? "#f9fafb" : "#374151");
        var icon = new Grid { Width = 20, Height = 20 };
        icon.Children.Add(new TextBlock
        {
            Text = "\uE890",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (slashed)
        {
            icon.Children.Add(new Line
            {
                X1 = 3,
                Y1 = 3,
                X2 = 17,
                Y2 = 17,
                Stroke = foreground,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
        return icon;
    }

    private Border NewCard(UIElement child)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = child
        };
        _cards.Add(card);
        return card;
    }

    private void ApplyTheme()
    {
        Background = Brush(_isDark ? "#111827" : "#f5f7fa");
        Foreground = Brush(_isDark ? "#f9fafb" : "#111827");
        foreach (var card in _cards)
        {
            card.Background = Brush(_isDark ? "#1f2937" : "#ffffff");
            card.BorderBrush = Brush(_isDark ? "#374151" : "#d9dee7");
        }
    }

    private string EmptyAsNoInfo(string? value) =>
        string.IsNullOrWhiteSpace(value) ? _t("NoInformation") : value;

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
