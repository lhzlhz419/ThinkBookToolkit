using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ThinkBookToolkit;

internal sealed class ToolkitDevicePage : ToolkitPageBase
{
    private readonly DeviceViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _root = new();
    private readonly TextBlock _loading;

    public ToolkitDevicePage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new DeviceViewModel(runtime);
        DataContext = _viewModel;
        _loading = new TextBlock
        {
            Text = L("正在读取设备信息……", "Reading device information…"),
            Foreground = Brush(Palette.Muted),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0)
        };
        _root.Children.Add(_loading);
        Content = _root;
        Loaded += async (_, _) => await LoadAsync();
    }

    internal void RenderForTesting() => Render();

    private async Task LoadAsync()
    {
        if (!_viewModel.DeviceLoadAttempted &&
            Runtime.Report?.IsAvailable(FeatureIds.DeviceInformation) != false)
        {
            await _viewModel.LoadDeviceAsync(_lifetime.Token);
        }
        Render();

        if (!_viewModel.WarrantyLoadAttempted &&
            Runtime.Report?.IsAvailable(FeatureIds.WarrantyInformation) == true)
        {
            await _viewModel.LoadWarrantyAsync(_lifetime.Token);
            Render();
        }
    }

    private void Render()
    {
        _root.Children.Clear();
        var info = _viewModel.Snapshot;
        if (info is not null)
        {
            _root.Children.Add(BuildIdentityCard(info));
        }
        else if (Runtime.Report?.IsAvailable(FeatureIds.DeviceInformation) != false &&
                 _viewModel.DeviceLoadAttempted)
        {
            _root.Children.Add(EmptyState(string.IsNullOrWhiteSpace(_viewModel.DeviceError)
                ? L("设备信息暂时不可用。", "Device information is temporarily unavailable.")
                : _viewModel.DeviceError));
        }

        if (Runtime.Report?.IsAvailable(FeatureIds.WarrantyInformation) == true)
            _root.Children.Add(BuildWarrantyCard(_viewModel.Warranty));

        if (info is not null)
        {
            var hardware = new AdaptiveUniformPanel { MinimumItemWidth = 330, Spacing = 10 };
            if (info.Cpu is { } cpu)
                hardware.Children.Add(InfoPanel("CPU", [
                    (cpu.Name, L($"{cpu.Cores} 核 / {cpu.Threads} 线程", $"{cpu.Cores} cores / {cpu.Threads} threads"))]));
            hardware.Children.Add(InfoPanel("GPU", info.Gpus.Select(gpu => (
                gpu.Name,
                gpu.UsesSharedMemory
                    ? L("共享显存", "Shared graphics memory")
                    : gpu.DedicatedMemoryBytes.HasValue
                        ? FormatBytes(gpu.DedicatedMemoryBytes.Value)
                        : L("无信息", "No information")))));
            hardware.Children.Add(InfoPanel(L("内存", "Memory"), info.Memory.Select(memory => (
                $"{FormatBytes(memory.Capacity)} · {memory.Type} · {memory.Speed} MHz",
                Join(memory.Locator, memory.Manufacturer, memory.PartNumber)))));
            hardware.Children.Add(InfoPanel(L("显示器", "Displays"), info.Displays.Select((display, index) => (
                $"{L("显示器", "Display")} {index + 1} · {display.Name}",
                $"{display.Width} × {display.Height} @ {display.RefreshRate} Hz"))));
            if (info.Motherboard is { } board)
                hardware.Children.Add(InfoPanel(L("主板", "Motherboard"), [
                    (board.Product, Join(board.Manufacturer, board.Version))]));
            _root.Children.Add(Card(
                L("硬件", "Hardware"),
                hardware,
                L("所有信息共享主页面的滚动条。", "All information uses the main page scrollbar."),
                "\uE950"));

            _root.Children.Add(BuildStorageCard(info.Partitions));
        }

        if (_root.Children.Count == 0)
            _root.Children.Add(EmptyState(L("此设备没有可显示的设备或保修信息。", "No device or warranty information is available.")));
    }

    private Border BuildWarrantyCard(WarrantySnapshot? snapshot)
    {
        var state = snapshot?.State ?? WarrantyState.Unavailable;
        var loading = snapshot is null;
        var statusText = loading
            ? L("查询中", "Loading")
            : state switch
            {
                WarrantyState.InWarranty => L("在保", "Covered"),
                WarrantyState.Expired => L("已过保", "Expired"),
                WarrantyState.NotStarted => L("尚未开始", "Not started"),
                _ => L("不可用", "Unavailable")
            };
        var statusColor = state == WarrantyState.InWarranty
            ? Palette.Success
            : state == WarrantyState.Expired
                ? Palette.Warning
                : Palette.Muted;
        var content = new StackPanel();
        content.Children.Add(new Border
        {
            Background = Tint(statusColor),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = statusText,
                Foreground = Brush(statusColor),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            }
        });
        var dates = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 220,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        dates.Children.Add(WarrantyMetric(
            L("开始日期", "Start date"),
            FormatWarrantyDate(snapshot?.StartDate)));
        dates.Children.Add(WarrantyMetric(
            L("结束日期", "End date"),
            FormatWarrantyDate(snapshot?.EndDate)));
        dates.Children.Add(WarrantyMetric(
            L("已用保修期", "Warranty elapsed"),
            loading ? L("查询中", "Loading") : $"{snapshot!.ProgressPercentage}%"));
        content.Children.Add(dates);
        content.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = snapshot?.ProgressPercentage ?? 0,
            Height = 7,
            Margin = new Thickness(0, 13, 0, 0)
        });
        if (snapshot?.IsStale == true || !string.IsNullOrWhiteSpace(snapshot?.Error))
        {
            content.Children.Add(new TextBlock
            {
                Text = (snapshot.IsStale
                        ? L("当前显示缓存的保修信息。", "Cached warranty information is shown.")
                        : string.Empty) +
                       (string.IsNullOrWhiteSpace(snapshot.Error)
                           ? string.Empty
                           : L(" 查询失败：", " Query failed: ") + snapshot.Error),
                Foreground = Brush(Palette.Warning),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0)
            });
        }
        return Card(
            L("保修信息", "Warranty"),
            content,
            L("保修查询与设备信息独立加载；在线查询失败时可使用匹配本机的缓存。", "Warranty loads independently from device details; a matching cache can be used if the online query fails."),
            "\uE73E",
            statusColor);
    }

    private Border WarrantyMetric(string title, string value)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush(Palette.Muted), FontSize = 12 });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Brush(Palette.Text),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        });
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Child = panel
        };
    }

    private string FormatWarrantyDate(DateOnly? value) => value.HasValue
        ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : L("无信息", "No information");

    private Border BuildIdentityCard(DeviceInformationSnapshot info)
    {
        var rows = new StackPanel();
        AddDeviceRow(rows, L("Windows 版本", "Windows version"), info.WindowsVersion);
        AddDeviceRow(rows, L("设备名称", "Device name"), info.DeviceName);
        AddDeviceRow(rows, L("设备型号", "Device model"), info.Identity.Model);
        AddDeviceRow(rows, L("产品编号", "Product number"), info.Identity.ProductNumber);
        AddDeviceRow(rows, L("设备代号", "Device code"), info.Identity.DeviceCode);
        AddDeviceRow(rows, L("序列号", "Serial number"), info.Identity.SerialNumber, sensitive: true);
        AddDeviceRow(rows, L("BIOS 版本", "BIOS version"), info.Identity.BiosVersion);
        AddIf(rows, "ME", info.Firmware.MeVersion);
        AddIf(rows, "AMD PSP", info.Firmware.AmdPspVersion);
        AddIf(rows, "SMBIOS", info.Identity.SmbiosVersion);
        AddIf(rows, "ACPI", info.Firmware.AcpiVersion);
        AddIf(rows, "UEFI", info.Firmware.UefiVersion);
        AddDeviceRow(rows, L("设备 ID", "Device ID"), info.DeviceId);
        AddDeviceRow(rows, L("产品 ID", "Product ID"), info.WindowsProductId);
        return Card(
            L("设备与固件", "Device and firmware"),
            rows,
            L("敏感信息默认隐藏；复制按钮复制真实值。", "Sensitive values are hidden by default; Copy always copies the real value."),
            "\uE772");
    }

    private void AddIf(Panel rows, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AddDeviceRow(rows, label, value);
    }

    private void AddDeviceRow(Panel panel, string label, string value, bool sensitive = false)
    {
        value = string.IsNullOrWhiteSpace(value) ? L("无信息", "No information") : value;
        var visible = !sensitive;
        var text = new TextBlock
        {
            Text = visible ? value : new string('•', Math.Min(12, Math.Max(6, value.Length))),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(12, 0, 12, 0)
        };
        var row = new Grid { MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush(Palette.Muted),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (sensitive)
        {
            var show = SmallIconButton(
                "\uE890",
                L("显示序列号", "Show serial number"));
            show.Content = EyeIcon(slashed: false);
            show.Click += (_, _) =>
            {
                visible = !visible;
                text.Text = visible ? value : new string('•', Math.Min(12, Math.Max(6, value.Length)));
                show.Content = EyeIcon(slashed: visible);
                show.ToolTip = visible
                    ? L("隐藏序列号", "Hide serial number")
                    : L("显示序列号", "Show serial number");
            };
            actions.Children.Add(show);
        }
        var copy = SmallIconButton("\uE8C8", L("复制", "Copy"));
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(value);
                Runtime.SetStatus(L("已复制到剪贴板", "Copied to clipboard"));
            }
            catch (Exception ex)
            {
                Runtime.SetStatus(L("复制失败：", "Copy failed: ") + ex.Message);
            }
        };
        actions.Children.Add(copy);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        panel.Children.Add(new Border
        {
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 5, 4, 5),
            Child = row
        });
    }

    private Button SmallIconButton(string glyph, string tooltip)
    {
        var button = ActionButton(glyph);
        button.ToolTip = tooltip;
        button.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        button.Width = 40;
        button.MinWidth = 40;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    private UIElement EyeIcon(bool slashed)
    {
        var foreground = Brush(Palette.Text);
        var icon = new Grid { Width = 20, Height = 20 };
        icon.Children.Add(new TextBlock
        {
            Text = "\uE890",
            FontFamily = new System.Windows.Media.FontFamily(
                "Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (slashed)
        {
            icon.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 3,
                Y1 = 3,
                X2 = 17,
                Y2 = 17,
                Stroke = foreground,
                StrokeThickness = 2.2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
            });
        }
        return icon;
    }

    private Border InfoPanel(string title, IEnumerable<(string Primary, string Secondary)> values)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(0, 0, 0, 10)
        });
        var any = false;
        foreach (var value in values)
        {
            any = true;
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value.Primary) ? L("无信息", "No information") : value.Primary,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Palette.Text),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value.Secondary) ? L("无信息", "No information") : value.Secondary,
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 12)
            });
        }
        if (!any)
            content.Children.Add(new TextBlock { Text = L("无信息", "No information"), Foreground = Brush(Palette.Muted) });
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(15),
            Child = content
        };
    }

    private Border BuildStorageCard(IReadOnlyList<PartitionInfo> partitions)
    {
        var content = new StackPanel();
        foreach (var partition in partitions)
        {
            var title = string.IsNullOrWhiteSpace(partition.VolumeLabel)
                ? partition.Name
                : $"{partition.Name} · {partition.VolumeLabel}";
            var used = partition.TotalBytes == 0 ? 0 : partition.UsedBytes * 100d / partition.TotalBytes;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var value = new TextBlock
            {
                Text = $"{FormatBytes(partition.UsedBytes)} / {FormatBytes(partition.TotalBytes)}",
                Foreground = Brush(Palette.Muted)
            };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
            content.Children.Add(header);
            content.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = used,
                Height = 7,
                Margin = new Thickness(0, 8, 0, 5)
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(partition.DiskModel) ? L("无信息", "No information") : partition.DiskModel,
                Foreground = Brush(Palette.Muted),
                Margin = new Thickness(0, 0, 0, 16)
            });
        }
        if (partitions.Count == 0)
            content.Children.Add(new TextBlock { Text = L("无信息", "No information"), Foreground = Brush(Palette.Muted) });
        return Card(L("存储", "Storage"), content, null, "\uEDA2");
    }

    private static string Join(params string[] values) =>
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
        return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.Dispose();
    }

    private sealed class DeviceViewModel : ToolkitViewModelBase
    {
        public DeviceViewModel(ToolkitRuntimeService runtime) : base(runtime) { }
        public DeviceInformationSnapshot? Snapshot { get; private set; }
        public WarrantySnapshot? Warranty { get; private set; }
        public bool DeviceLoadAttempted { get; private set; }
        public bool WarrantyLoadAttempted { get; private set; }
        public string DeviceError { get; private set; } = string.Empty;

        public async Task LoadDeviceAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
            DeviceLoadAttempted = true;
            try
            {
                Snapshot = await Task.Run(DeviceInformationService.ReadAll, cancellationToken);
                Status = string.Empty;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DeviceError = Runtime.L("设备信息读取失败：", "Device information failed: ") + ex.Message;
                Status = DeviceError;
            }
            finally { IsBusy = false; }
        }

        public async Task LoadWarrantyAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
            WarrantyLoadAttempted = true;
            try
            {
                Warranty = await WarrantyService.GetWarrantyAsync(cancellationToken);
                Status = Warranty.State == WarrantyState.Unavailable
                    ? Runtime.L("保修信息不可用", "Warranty information is unavailable")
                    : string.Empty;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Warranty = WarrantySnapshot.Unavailable(ex.Message);
                Status = Runtime.L("保修信息读取失败：", "Warranty information failed: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
