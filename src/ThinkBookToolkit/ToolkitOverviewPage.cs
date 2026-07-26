using System;
using System.Globalization;
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
        var metrics = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 260,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 12)
        };
        metrics.Children.Add(MetricCard(
            "CPU",
            BoundText(nameof(OverviewViewModel.Cpu)),
            L("温度与功耗", "Temperature and power"),
            "\uE950",
            Palette.Accent,
            20,
            true));
        metrics.Children.Add(MetricCard(
            "GPU",
            BoundText(nameof(OverviewViewModel.Gpu)),
            L("温度与功耗", "Temperature and power"),
            "\uE7F4",
            "#A984FF",
            20,
            true));
        metrics.Children.Add(MetricCard(
            L("电池", "Battery"),
            BoundText(nameof(OverviewViewModel.Battery)),
            L("电量、健康度与功率", "Charge, health and power"),
            "\uE850",
            Palette.Success,
            20,
            true));
        metrics.Children.Add(MetricCard(
            L("双风扇", "Dual fans"),
            BoundText(nameof(OverviewViewModel.Fans)),
            "FAN1 / FAN2",
            "\uE9CA",
            "#56C2C9",
            20,
            true));
        root.Children.Add(metrics);
        return root;
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
            MinimumItemWidth = 150,
            Spacing = 8,
            Margin = new Thickness(0, 15, 0, 0)
        };
        pills.Children.Add(HeroModeControl(
            L("性能模式", "Performance mode"),
            _itsMode));
        pills.Children.Add(HeroModeControl(
            L("GPU 模式", "GPU mode"),
            _gpuMode));
        pills.Children.Add(HeroModeControl(
            L("风扇控制", "Fan control"),
            _fanMode));
        pills.Children.Add(HeroStatus(
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
        AddChoice(_gpuMode, L("混合模式", "Hybrid mode"), GpuWorkingMode.Hybrid);
        AddChoice(_gpuMode, L("混合核显模式", "iGPU only"), GpuWorkingMode.IntegratedOnly);
        AddChoice(_gpuMode, L("混合自动模式", "Hybrid auto"), GpuWorkingMode.HybridAuto);
        AddChoice(_gpuMode, L("独显直连模式", "Discrete graphics"), GpuWorkingMode.Discrete);
        AddChoice(_gpuMode, L("核显直连模式", "Integrated graphics"), GpuWorkingMode.IntegratedDirect);
        AddChoice(_fanMode, L("固件自动", "Firmware automatic"), FanControlMode.FirmwareAutomatic);
        AddChoice(_fanMode, L("固定转速", "Fixed RPM"), FanControlMode.FixedRpm);
        AddChoice(_fanMode, L("风扇曲线", "Fan curve"), FanControlMode.FanCurve);
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
        if (snapshot.ItsMode != ItsMode.Unknown)
            Select(_itsMode, snapshot.ItsMode);
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
                ? snapshot.FanStrategy == ControlStrategy.FanCurve
                    ? FanControlMode.FanCurve
                    : FanControlMode.FixedRpm
                : FanControlMode.FirmwareAutomatic);
        _itsMode.IsEnabled = !_modeWriteBusy &&
                             Runtime.Report?.IsAvailable(FeatureIds.PerformanceMode) == true;
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

    private static TextBlock BoundText(string property)
    {
        var text = new TextBlock();
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        return text;
    }

    private sealed class OverviewViewModel : ToolkitViewModelBase
    {
        private string _cpu = "--";
        private string _gpu = "--";
        private string _fans = "--";
        private string _battery = "--";
        private string _pendingRestart = "--";

        public OverviewViewModel(ToolkitRuntimeService runtime)
            : base(runtime)
        {
            runtime.SnapshotChanged += OnSnapshotChanged;
            Update();
        }

        public string Cpu { get => _cpu; private set => SetField(ref _cpu, value); }
        public string Gpu { get => _gpu; private set => SetField(ref _gpu, value); }
        public string Fans { get => _fans; private set => SetField(ref _fans, value); }
        public string Battery { get => _battery; private set => SetField(ref _battery, value); }
        public string PendingRestart { get => _pendingRestart; private set => SetField(ref _pendingRestart, value); }

        private void OnSnapshotChanged(object? sender, EventArgs args) => Update();

        private void Update()
        {
            var snapshot = Runtime.Snapshot;
            Cpu = Pair(
                snapshot.Temperatures?.CpuTempC,
                "°C",
                snapshot.Temperatures?.CpuPowerW,
                "W");
            Gpu = Pair(
                snapshot.Temperatures?.GpuTempC,
                "°C",
                snapshot.Temperatures?.GpuPowerW,
                "W");
            Fans = snapshot.Fans is null
                ? "--"
                : $"{snapshot.Fans.Fan1Rpm} / {snapshot.Fans.Fan2Rpm} RPM";
            if (snapshot.Battery is { } battery)
            {
                var charge = battery.FullChargeCapacityWh > 0
                    ? battery.CurrentCapacityWh * 100 / battery.FullChargeCapacityWh
                    : 0;
                Battery = $"{charge:0}% · {battery.HealthPercent:0}% " +
                          Runtime.L("健康", "health") +
                          $" · {battery.ChargeDischargePowerW:+0.0;-0.0;0.0} W";
            }
            else
            {
                Battery = "--";
            }
            PendingRestart = string.IsNullOrWhiteSpace(snapshot.PendingGpuMode)
                ? Runtime.L("无需重启", "No restart pending")
                : Runtime.L("GPU 模式等待重启", "GPU restart pending");
        }

        private static string Pair(
            double? first,
            string firstUnit,
            double? second = null,
            string secondUnit = "")
        {
            if (!first.HasValue && !second.HasValue)
                return "--";
            var firstText = first.HasValue
                ? first.Value.ToString("0.0", CultureInfo.InvariantCulture) + " " + firstUnit
                : "--";
            return second.HasValue
                ? firstText + "   " + second.Value.ToString("0.0", CultureInfo.InvariantCulture) + " " + secondUnit
                : firstText;
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
