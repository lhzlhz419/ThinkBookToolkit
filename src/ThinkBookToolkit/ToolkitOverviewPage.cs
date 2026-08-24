using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class ToolkitOverviewPage : ToolkitPageBase
{
    private readonly OverviewViewModel _viewModel;
    private readonly ComboBox _itsMode = new() { MinWidth = 140 };
    private readonly ComboBox _gpuMode = new() { MinWidth = 160 };
    private readonly ComboBox _fanMode = new() { MinWidth = 150 };
    private bool _syncingModes;
    private bool _modeWriteBusy;

    public ToolkitOverviewPage(ToolkitRuntimeService runtime)
        : base(runtime)
    {
        _viewModel = new OverviewViewModel(runtime);
        DataContext = _viewModel;
        PopulateModeChoices();
        Content = BuildLayout();
        WireModeControls();
        runtime.SnapshotChanged += OnSnapshotChanged;
        SyncModeControls();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();
        root.Children.Add(BuildHero());
        if (Runtime.Settings.OverviewPageMode == OverviewPageMode.Compact)
        {
            root.Children.Add(BuildCompactMetrics());
        }
        else
        {
            var metrics = HardwareMonitorCards(
                layout: Runtime.Settings.OverviewLayout);
            metrics.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(metrics);
        }
        return root;
    }

    private UIElement BuildCompactMetrics()
    {
        var layout = Runtime.Settings.OverviewLayout;
        var metrics = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 260,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Add(
            OverviewCardIds.Cpu,
            ["temperature", "power"],
            "CPU",
            nameof(OverviewViewModel.CompactCpu),
            Detail(
                OverviewCardIds.Cpu,
                ("temperature", L("温度", "Temperature")),
                ("power", L("功耗", "Power"))),
            "\uE950",
            Palette.Accent);
        Add(
            OverviewCardIds.Gpu,
            ["core-temperature", "power"],
            "GPU",
            nameof(OverviewViewModel.CompactGpu),
            Detail(
                OverviewCardIds.Gpu,
                ("core-temperature", L("温度", "Temperature")),
                ("power", L("功耗", "Power"))),
            "\uE7F4",
            "#A984FF");
        Add(
            OverviewCardIds.Battery,
            ["charge", "health", "power"],
            L("电池", "Battery"),
            nameof(OverviewViewModel.CompactBattery),
            Detail(
                OverviewCardIds.Battery,
                ("charge", L("电量", "Charge")),
                ("health", L("健康度", "Health")),
                ("power", L("功率", "Power"))),
            "\uE850",
            Palette.Success);
        Add(
            OverviewCardIds.MemoryStorage,
            ["utilization", "average-temperature"],
            L("内存", "Memory"),
            nameof(OverviewViewModel.CompactMemory),
            Detail(
                OverviewCardIds.MemoryStorage,
                ("utilization", L("利用率", "Utilization")),
                ("average-temperature", L("温度", "Temperature"))),
            "\uE950",
            "#49BCE8");
        Add(
            OverviewCardIds.Fans,
            ["fan1-speed", "fan2-speed"],
            L("双风扇", "Dual fans"),
            nameof(OverviewViewModel.CompactFans),
            SlashDetail(
                OverviewCardIds.Fans,
                ("fan1-speed", "FAN1"),
                ("fan2-speed", "FAN2")),
            "\uE9CA",
            "#56C2C9");
        Add(
            OverviewCardIds.Warranty,
            ["status", "remaining-days"],
            L("保修信息", "Warranty"),
            nameof(OverviewViewModel.CompactWarranty),
            Detail(
                OverviewCardIds.Warranty,
                ("status", L("保修状态", "Warranty status")),
                ("remaining-days", L("剩余天数", "Days remaining"))),
            "\uE73E",
            Palette.Warning);
        return metrics;

        string Detail(
            string cardId,
            params (string ItemId, string Label)[] values) =>
            BuildDetail(cardId, values, null);

        string SlashDetail(
            string cardId,
            params (string ItemId, string Label)[] values) =>
            BuildDetail(cardId, values, " / ");

        string BuildDetail(
            string cardId,
            IEnumerable<(string ItemId, string Label)> values,
            string? separator)
        {
            var labels = values
                .Where(value => OverviewLayoutDefaults.IsItemEnabled(
                    layout,
                    cardId,
                    value.ItemId))
                .Select(value => value.Label)
                .ToArray();
            if (separator is not null)
                return string.Join(separator, labels);
            if (labels.Length < 2)
                return labels.FirstOrDefault() ?? string.Empty;
            if (Runtime.IsChinese)
                return labels.Length == 2
                    ? string.Join("与", labels)
                    : string.Join("、", labels[..^1]) + "与" + labels[^1];
            return labels.Length == 2
                ? string.Join(" and ", labels)
                : string.Join(", ", labels[..^1]) + " and " + labels[^1];
        }

        void Add(
            string cardId,
            string[] items,
            string title,
            string property,
            string detail,
            string glyph,
            string accent)
        {
            if (!OverviewLayoutDefaults.AnyItemEnabled(
                    layout,
                    cardId,
                    items))
            {
                return;
            }
            var value = new TextBlock();
            value.SetBinding(TextBlock.TextProperty, new Binding(property));
            metrics.Children.Add(MetricCard(
                title,
                value,
                detail,
                glyph,
                accent,
                20,
                true));
        }
    }

    private Border BuildHero()
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = L("设备控制中心", "DEVICE CONTROL CENTER"),
            FontFamily = UiTypography.FontFamilyFor(Runtime.Settings.Language),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#D7E1FF")
        });
        left.Children.Add(new TextBlock
        {
            Text = DeviceModelDetector.CurrentIdentity.Model,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 7, 0, 3)
        });
        left.Children.Add(new TextBlock
        {
            Text = DeviceModelDetector.CurrentIdentity.ProductNumber,
            Foreground = Brush("#DDE5FF"),
            FontSize = 12
        });
        var pills = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 135,
            Spacing = 8,
            Margin = new Thickness(0, 15, 0, 0)
        };
        var layout = Runtime.Settings.OverviewLayout;
        AddHero(
            OverviewHeroIds.PerformanceMode,
            HeroModeControl(
                L("性能模式", "Performance mode"),
                _itsMode));
        AddHero(
            OverviewHeroIds.GpuMode,
            HeroModeControl(
                L("GPU 模式", "GPU mode"),
                _gpuMode));
        AddHero(
            OverviewHeroIds.FanControl,
            HeroModeControl(
                L("风扇控制", "Fan control"),
                _fanMode));
        AddHero(
            OverviewHeroIds.DiscreteGpuStatus,
            HeroStatus(
                L("独显状态", "Discrete GPU"),
                nameof(OverviewViewModel.DiscreteGpuStatus),
                16));
        AddHero(
            OverviewHeroIds.RestartStatus,
            HeroStatus(
                L("重启状态", "Restart state"),
                nameof(OverviewViewModel.PendingRestart),
                16));
        left.Children.Add(pills);
        content.Children.Add(left);

        var decoration = new TextBlock
        {
            Text = "\uE770",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 72,
            Foreground = Brush("#80FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28, 0, 8, 0)
        };
        Grid.SetColumn(decoration, 1);
        content.Children.Add(decoration);
        return new Border
        {
            MinHeight = 172,
            CornerRadius = new CornerRadius(20),
            Background = new LinearGradientBrush(
                ColorFrom("#5178F3"),
                ColorFrom("#7955D9"),
                0),
            Padding = new Thickness(24, 20, 22, 20),
            Margin = new Thickness(0, 0, 0, 16),
            ClipToBounds = true,
            Child = content
        };

        void AddHero(string id, UIElement element)
        {
            if (OverviewLayoutDefaults.IsHeroCardEnabled(layout, id))
                pills.Children.Add(element);
        }
    }

    private Border HeroStatus(string label, string property, double valueFontSize = 14)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush("#CFD8FA"),
            FontSize = 12
        });
        var value = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = valueFontSize,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(property));
        stack.Children.Add(value);
        return new Border
        {
            Background = Brush("#24FFFFFF"),
            BorderBrush = Brush("#35FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 7),
            Child = stack
        };
    }

    private Border HeroModeControl(string label, ComboBox selector)
    {
        selector.HorizontalAlignment = HorizontalAlignment.Stretch;
        selector.Margin = new Thickness(0, 5, 0, 0);
        selector.MinHeight = 32;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush("#E3E9FF"),
            FontSize = 12
        });
        stack.Children.Add(selector);
        return new Border
        {
            Background = Brush("#24FFFFFF"),
            BorderBrush = Brush("#35FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 8),
            Child = stack
        };
    }

    private void PopulateModeChoices()
    {
        AddChoice(_itsMode, L("智能模式", "Auto"), ItsMode.Intelligent);
        AddChoice(_itsMode, L("省电模式", "Cool"), ItsMode.PowerSaving);
        AddChoice(_itsMode, L("性能模式", "Performance"), ItsMode.Performance);
        AddChoice(_itsMode, L("极客模式", "Geek"), ItsMode.Geek);
        foreach (GpuWorkingMode mode in Enum.GetValues<GpuWorkingMode>())
        {
            AddChoice(
                _gpuMode,
                GpuModeText.Name(mode, Runtime.IsChinese),
                mode);
        }
        AddChoice(_fanMode, L("固件自动", "Firmware automatic"), FanControlMode.FirmwareAutomatic);
        AddChoice(_fanMode, L("固定转速", "Fixed RPM"), FanControlMode.FixedRpm);
        AddChoice(_fanMode, L("风扇曲线", "Fan curve"), FanControlMode.FanCurve);
        AddChoice(_fanMode, L("高级曲线", "Advanced curve"), FanControlMode.AdvancedCurve);
    }

    private void WireModeControls()
    {
        _itsMode.SelectionChanged += async (_, _) =>
        {
            if (_syncingModes ||
                _modeWriteBusy ||
                Selected<ItsMode>(_itsMode) is not { } mode)
            {
                return;
            }
            _modeWriteBusy = true;
            SyncModeControls();
            var error = await Runtime.SetItsModeAsync(mode);
            if (!string.IsNullOrWhiteSpace(error))
                Runtime.SetStatus(L("性能模式切换失败：", "Performance-mode change failed: ") + error);
            _modeWriteBusy = false;
            SyncModeControls();
        };
        _gpuMode.SelectionChanged += async (_, _) =>
        {
            if (_syncingModes ||
                _modeWriteBusy ||
                Selected<GpuWorkingMode>(_gpuMode) is not { } mode)
            {
                return;
            }
            _modeWriteBusy = true;
            SyncModeControls();
            var error = await Runtime.SetGpuModeAsync(mode);
            if (!string.IsNullOrWhiteSpace(error))
                Runtime.SetStatus(L("GPU 模式切换失败：", "GPU-mode change failed: ") + error);
            _modeWriteBusy = false;
            SyncModeControls();
        };
        _fanMode.SelectionChanged += async (_, _) =>
        {
            if (_syncingModes ||
                _modeWriteBusy ||
                Selected<FanControlMode>(_fanMode) is not { } mode)
            {
                return;
            }
            _modeWriteBusy = true;
            SyncModeControls();
            var error = await Runtime.SetFanModeAsync(mode);
            if (!string.IsNullOrWhiteSpace(error))
                Runtime.SetStatus(L("风扇策略切换失败：", "Fan-strategy change failed: ") + error);
            _modeWriteBusy = false;
            SyncModeControls();
        };
    }

    private void OnSnapshotChanged(object? sender, EventArgs args) =>
        SyncModeControls();

    private void SyncModeControls()
    {
        _syncingModes = true;
        var snapshot = Runtime.Snapshot;
        var isAcConnected = snapshot.Battery?.IsAcConnected;
        foreach (var item in _itsMode.Items.OfType<ComboBoxItem>())
        {
            item.Visibility = item.Tag is ItsMode mode &&
                              PerformanceModeAvailability.CanSelect(
                                  mode,
                                  isAcConnected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (snapshot.ItsMode != ItsMode.Unknown &&
            PerformanceModeAvailability.CanSelect(
                snapshot.ItsMode,
                isAcConnected))
            Select(_itsMode, snapshot.ItsMode);
        else
            _itsMode.SelectedItem = null;
        foreach (var item in _gpuMode.Items.OfType<ComboBoxItem>())
        {
            item.Visibility = snapshot.SupportedGpuModes.Count == 0 ||
                              item.Tag is GpuWorkingMode mode &&
                              snapshot.SupportedGpuModes.Contains(mode)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (Enum.TryParse<GpuWorkingMode>(
                snapshot.PendingGpuMode,
                out var pendingGpuMode))
        {
            Select(_gpuMode, pendingGpuMode);
        }
        else if (snapshot.GpuMode.HasValue)
        {
            Select(_gpuMode, snapshot.GpuMode.Value);
        }
        Select(
            _fanMode,
            snapshot.FanControlRunning
                ? snapshot.FanStrategy switch
                {
                    ControlStrategy.FanCurve => FanControlMode.FanCurve,
                    ControlStrategy.AdvancedCurve => FanControlMode.AdvancedCurve,
                    _ => FanControlMode.FixedRpm
                }
                : FanControlMode.FirmwareAutomatic);
        _itsMode.IsEnabled = !_modeWriteBusy &&
                             Runtime.CanSwitchItsMode;
        _gpuMode.IsEnabled = !_modeWriteBusy &&
                             Runtime.Report?.IsAvailable(FeatureIds.GpuMode) == true &&
                             snapshot.SupportedGpuModes.Count > 0;
        _fanMode.IsEnabled = !_modeWriteBusy &&
                             Runtime.Report?.IsAvailable(FeatureIds.FanControl) == true;
        _syncingModes = false;
    }

    private static void AddChoice<T>(ComboBox combo, string label, T value) where T : struct =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static void Select<T>(ComboBox combo, T value) where T : struct =>
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, value));

    private sealed class OverviewViewModel : HardwareMonitorViewModel
    {
        private string _pendingRestart = "--";
        private string _discreteGpuStatus = "--";

        public OverviewViewModel(ToolkitRuntimeService runtime)
            : base(runtime)
        {
            runtime.SnapshotChanged += OnSnapshotChanged;
            Update(runtime.Snapshot);
        }

        public string PendingRestart { get => _pendingRestart; private set => SetField(ref _pendingRestart, value); }
        public string DiscreteGpuStatus { get => _discreteGpuStatus; private set => SetField(ref _discreteGpuStatus, value); }

        private void OnSnapshotChanged(object? sender, EventArgs args) =>
            Update(Runtime.Snapshot);

        public override void Update(ToolkitRuntimeSnapshot snapshot)
        {
            base.Update(snapshot);
            PendingRestart = string.IsNullOrWhiteSpace(
                    snapshot.PendingGpuMode)
                ? Runtime.L("无需重启", "No restart required")
                : Runtime.L("需要重启", "Restart required");
            DiscreteGpuStatus = DiscreteGpuStatusFormatter.Format(
                snapshot.Temperatures?.DiscreteGpuState ??
                    DiscreteGpuActivityState.Unknown,
                snapshot.Temperatures?.GpuPerformanceState,
                Runtime.IsChinese);
        }

        public override void Dispose()
        {
            Runtime.SnapshotChanged -= OnSnapshotChanged;
        }
    }

    public override void Dispose()
    {
        Runtime.SnapshotChanged -= OnSnapshotChanged;
        base.Dispose();
    }
}
