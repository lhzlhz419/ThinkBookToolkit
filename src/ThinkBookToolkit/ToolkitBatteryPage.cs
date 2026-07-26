using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ThinkBookToolkit;

internal sealed class ToolkitBatteryPage : ToolkitPageBase
{
    private readonly BatteryViewModel _viewModel;
    private readonly ComboBox _chargeMode = new() { MinWidth = 180 };
    private readonly ComboBox _usbMode = new() { MinWidth = 180 };
    private readonly CheckBox _overnight = new() { MinWidth = 48 };
    private readonly CheckBox _flip = new() { MinWidth = 48 };
    private readonly TextBlock _status;
    private bool _syncing;

    public ToolkitBatteryPage(ToolkitRuntimeService runtime)
        : base(runtime)
    {
        _viewModel = new BatteryViewModel(runtime);
        DataContext = _viewModel;
        _status = StatusText();
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
        runtime.SnapshotChanged += OnSnapshotChanged;
    }

    private UIElement BuildLayout()
    {
        AddChoice(_chargeMode, L("养护", "Conservation"), BatteryChargeMode.Conservation);
        AddChoice(_chargeMode, L("普通", "Normal"), BatteryChargeMode.Normal);
        AddChoice(_chargeMode, L("快充", "Rapid charge"), BatteryChargeMode.RapidCharge);
        AddChoice(_usbMode, L("关闭", "Off"), AlwaysOnUsbMode.Off);
        AddChoice(_usbMode, L("仅睡眠时开启", "On while sleeping"), AlwaysOnUsbMode.OnWhenSleeping);
        AddChoice(_usbMode, L("保持开启", "Always on"), AlwaysOnUsbMode.OnAlways);
        _chargeMode.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && _chargeMode.SelectedItem is ComboBoxItem { Tag: BatteryChargeMode value })
                await WriteAsync(() => _viewModel.SetChargeModeAsync(value));
        };
        _usbMode.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && _usbMode.SelectedItem is ComboBoxItem { Tag: AlwaysOnUsbMode value })
                await WriteAsync(() => _viewModel.SetUsbModeAsync(value));
        };
        _overnight.Click += async (_, _) =>
        {
            if (!_syncing)
                await WriteAsync(() => _viewModel.SetOvernightAsync(_overnight.IsChecked == true));
        };
        _flip.Click += async (_, _) =>
        {
            if (!_syncing)
                await WriteAsync(() => _viewModel.SetFlipAsync(_flip.IsChecked == true));
        };

        var root = new StackPanel();
        var settings = new StackPanel();
        AddIfAvailable(settings, FeatureIds.BatteryChargeMode, SettingRow(
            L("充电模式", "Charging mode"),
            L("养护模式限制充电量，快速充电提高充电功率。", "Choose conservation, normal or rapid charging."),
            _chargeMode,
            "\uE83F"));
        AddIfAvailable(settings, FeatureIds.OvernightCharging, SettingRow(
            L("隔夜电池充电", "Overnight charging"),
            L("插电过夜时先充至 80%，早晨再充满。", "Pause near 80% overnight and finish in the morning."),
            _overnight,
            "\uE708"));
        AddIfAvailable(settings, FeatureIds.AlwaysOnUsb, SettingRow(
            L("保持 USB 供电", "Always-on USB"),
            L("设置关机或睡眠时的 USB 供电行为。", "Keep selected USB ports powered while sleeping or off."),
            _usbMode,
            "\uE88E"));
        AddIfAvailable(settings, FeatureIds.FlipToStart, SettingRow(
            L("开盖启动", "Flip to start"),
            L("打开屏幕上盖时自动启动电脑。", "Start the notebook automatically when opening the lid."),
            _flip,
            "\uE7E8"));
        settings.Children.Add(_status);
        if (settings.Children.Count > 1)
        {
            root.Children.Add(Card(
                L("充电与供电", "Charging and power"),
                settings,
                L("所有项目均使用当前硬件状态并在写入后回读。", "Every change is confirmed by reading the hardware state back."),
                "\uE8C8"));
        }

        if (Runtime.Report?.IsAvailable(FeatureIds.BatteryInformation) == true)
            root.Children.Add(BuildBatteryDetails());
        if (root.Children.Count == 0)
            root.Children.Add(EmptyState(L("此设备没有可用的电池功能。", "No battery features are available on this device.")));
        return root;
    }

    private UIElement BuildBatteryDetails()
    {
        var metrics = new AdaptiveUniformPanel { MinimumItemWidth = 220, Spacing = 10 };
        metrics.Children.Add(DetailMetric(L("当前电量", "Charge"), nameof(BatteryViewModel.Charge)));
        metrics.Children.Add(DetailMetric(L("温度", "Temperature"), nameof(BatteryViewModel.Temperature)));
        metrics.Children.Add(DetailMetric(L("当前功率", "Current power"), nameof(BatteryViewModel.Power)));
        metrics.Children.Add(DetailMetric(L("最小功率", "Minimum power"), nameof(BatteryViewModel.MinimumPower)));
        metrics.Children.Add(DetailMetric(L("最大功率", "Maximum power"), nameof(BatteryViewModel.MaximumPower)));
        metrics.Children.Add(DetailMetric(L("当前容量", "Current capacity"), nameof(BatteryViewModel.CurrentCapacity)));
        metrics.Children.Add(DetailMetric(L("满充容量", "Full-charge capacity"), nameof(BatteryViewModel.FullChargeCapacity)));
        metrics.Children.Add(DetailMetric(L("设计容量", "Design capacity"), nameof(BatteryViewModel.DesignCapacity)));
        metrics.Children.Add(DetailMetric(L("健康度", "Health"), nameof(BatteryViewModel.Health)));
        metrics.Children.Add(DetailMetric(L("本次电池使用", "On battery since"), nameof(BatteryViewModel.OnBatterySince)));
        metrics.Children.Add(DetailMetric(L("循环次数", "Cycle count"), nameof(BatteryViewModel.Cycles)));
        metrics.Children.Add(DetailMetric(L("生产日期", "Manufacture date"), nameof(BatteryViewModel.ManufactureDate)));
        metrics.Children.Add(DetailMetric(L("首次使用日期", "First-use date"), nameof(BatteryViewModel.FirstUseDate)));
        return Card(
            L("电池详情", "Battery details"),
            metrics,
            L("正功率表示充电，负功率表示放电；功率范围从本次运行期间的样本累计。", "Positive power means charging and negative power means discharging; the power range accumulates during this run."),
            "\uE9D2");
    }

    private Border DetailMetric(string title, string property)
    {
        var value = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        value.SetBinding(TextBlock.TextProperty, property);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush(Palette.Muted) });
        panel.Children.Add(value);
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = panel
        };
    }

    private async Task LoadAsync()
    {
        await _viewModel.LoadAsync();
        SyncControls();
    }

    private async Task WriteAsync(Func<Task> action)
    {
        SetControlsEnabled(false);
        await action();
        SyncControls();
        SetControlsEnabled(true);
    }

    private void SyncControls()
    {
        _syncing = true;
        Select(_chargeMode, _viewModel.State?.ChargeMode);
        Select(_usbMode, _viewModel.State?.AlwaysOnUsb);
        _overnight.IsChecked = _viewModel.State?.OvernightCharging == true;
        _flip.IsChecked = _viewModel.State?.FlipToStart == true;
        _status.Text = _viewModel.Status;
        SetControlsEnabled(true);
        _syncing = false;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _chargeMode.IsEnabled = enabled && _viewModel.State?.ChargeMode is not null;
        _usbMode.IsEnabled = enabled && _viewModel.State?.AlwaysOnUsb is not null;
        _overnight.IsEnabled = enabled && _viewModel.State?.OvernightCharging is not null;
        _flip.IsEnabled = enabled && _viewModel.State?.FlipToStart is not null;
    }

    private void AddIfAvailable(Panel panel, string featureId, UIElement row)
    {
        if (Runtime.Report?.IsAvailable(featureId) != false)
            panel.Children.Add(row);
    }

    private static void AddChoice<T>(ComboBox combo, string text, T value) where T : struct =>
        combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });

    private static void Select<T>(ComboBox combo, T? value) where T : struct
    {
        combo.SelectedItem = value.HasValue
            ? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value.Value))
            : null;
    }

    private void OnSnapshotChanged(object? sender, EventArgs args) =>
        _viewModel.UpdateBattery(Runtime.Snapshot.Battery);

    public override void Dispose()
    {
        Runtime.SnapshotChanged -= OnSnapshotChanged;
        base.Dispose();
    }

    private sealed class BatteryViewModel : ToolkitViewModelBase
    {
        private BatteryInformationSnapshot? _battery;
        public BatteryViewModel(ToolkitRuntimeService runtime) : base(runtime)
        {
            UpdateBattery(runtime.Snapshot.Battery);
        }

        public BatterySettingsState? State { get; private set; }
        public string Charge => _battery is null || _battery.FullChargeCapacityWh <= 0
            ? "--"
            : $"{_battery.CurrentCapacityWh * 100 / _battery.FullChargeCapacityWh:0}%";
        public string Health => _battery is null ? "--" : $"{_battery.HealthPercent:0.00}%";
        public string Power => _battery is null ? "--" : $"{_battery.ChargeDischargePowerW:+0.0;-0.0;0.0} W";
        public string MinimumPower => _battery is null ? "--" : $"{_battery.MinimumPowerW:+0.0;-0.0;0.0} W";
        public string MaximumPower => _battery is null ? "--" : $"{_battery.MaximumPowerW:+0.0;-0.0;0.0} W";
        public string Temperature => _battery?.TemperatureC is { } value ? $"{value:0.0} °C" : "--";
        public string CurrentCapacity => _battery is null ? "--" : $"{_battery.CurrentCapacityWh:0.00} Wh";
        public string FullChargeCapacity => _battery is null ? "--" : $"{_battery.FullChargeCapacityWh:0.00} Wh";
        public string DesignCapacity => _battery is null ? "--" : $"{_battery.DesignCapacityWh:0.00} Wh";
        public string OnBatterySince => FormatOnBatterySince(_battery);
        public string Cycles => _battery is null ? "--" : _battery.CycleCount.ToString(CultureInfo.InvariantCulture);
        public string ManufactureDate => FormatDate(_battery?.ManufactureDate);
        public string FirstUseDate => FormatDate(_battery?.FirstUseDate);

        public async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                State = await Task.Run(() => BatterySettingsController.ReadState(refreshFlipToStart: true));
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                Status = Runtime.L("读取失败：", "Read failed: ") + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public Task SetChargeModeAsync(BatteryChargeMode value) =>
            WriteAsync(() => BatterySettingsController.SetChargeMode(value));
        public Task SetUsbModeAsync(AlwaysOnUsbMode value) =>
            WriteAsync(() => BatterySettingsController.SetAlwaysOnUsb(value));
        public Task SetOvernightAsync(bool value) =>
            WriteAsync(() => BatterySettingsController.SetOvernightCharging(value));
        public Task SetFlipAsync(bool value) =>
            WriteAsync(() => BatterySettingsController.SetFlipToStart(value));

        private async Task WriteAsync(Action action)
        {
            var previous = State;
            IsBusy = true;
            try
            {
                await Task.Run(action);
                State = await Task.Run(() => BatterySettingsController.ReadState());
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                State = previous;
                Status = Runtime.L("写入失败，已恢复原状态：", "Write failed; previous state restored: ") + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void UpdateBattery(BatteryInformationSnapshot? value)
        {
            _battery = value;
            Notify(nameof(Charge));
            Notify(nameof(Health));
            Notify(nameof(Power));
            Notify(nameof(MinimumPower));
            Notify(nameof(MaximumPower));
            Notify(nameof(Temperature));
            Notify(nameof(CurrentCapacity));
            Notify(nameof(FullChargeCapacity));
            Notify(nameof(DesignCapacity));
            Notify(nameof(OnBatterySince));
            Notify(nameof(Cycles));
            Notify(nameof(ManufactureDate));
            Notify(nameof(FirstUseDate));
        }

        private static string FormatOnBatterySince(BatteryInformationSnapshot? battery)
        {
            if (battery is null ||
                battery.IsAcConnected ||
                !battery.OnBatterySince.HasValue)
            {
                return "--";
            }
            var since = battery.OnBatterySince.Value;
            var elapsed = DateTime.Now - since;
            var duration = elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}"
                : elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            return $"{since:g} · {duration}";
        }

        private static string FormatDate(DateTime? value) =>
            value?.ToString("d", CultureInfo.CurrentCulture) ?? "--";
    }
}
