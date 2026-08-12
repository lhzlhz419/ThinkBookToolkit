using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ThinkBookToolkit;

internal sealed class ToolkitDisplayPage : ToolkitPageBase
{
    private readonly DisplayViewModel _viewModel;
    private readonly ComboBox _refreshRate = new() { MinWidth = 150 };
    private readonly Button _refreshRateSettings;
    private readonly CheckBox _vantageEnabled = new();
    private readonly ComboBox _effect = new() { MinWidth = 180 };
    private readonly ComboBox _schedule = new() { MinWidth = 180 };
    private readonly ComboBox _customTemperature = new() { MinWidth = 180 };
    private readonly CheckBox _pcManagerEnabled = new();
    private readonly Slider _pcTemperature = new()
    {
        Minimum = PcManagerEyeCareController.MinimumTemperature,
        Maximum = PcManagerEyeCareController.MaximumTemperature,
        TickFrequency = 100,
        Width = 230
    };
    private readonly TextBlock _pcTemperatureValue = new() { MinWidth = 70 };
    private readonly ComboBox _colorMode = new() { MinWidth = 210 };
    private readonly TextBox _normalDefault = new() { Width = 90 };
    private readonly TextBox _eyeDefault = new() { Width = 90 };
    private readonly TextBlock _status;
    private bool _syncing;

    public ToolkitDisplayPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new DisplayViewModel(runtime);
        _refreshRateSettings = ActionButton(L("设置", "Settings"));
        DataContext = _viewModel;
        _status = StatusText();
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        PopulateChoices();
        WireEvents();
        var root = new StackPanel();
        var report = Runtime.Report;

        if (report?.IsAvailable(FeatureIds.DisplayRefreshRate) != false)
        {
            var control = new Grid
            {
                MinWidth = 340,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            control.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            control.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            _refreshRate.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.Children.Add(_refreshRate);
            _refreshRateSettings.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(_refreshRateSettings, 1);
            control.Children.Add(_refreshRateSettings);
            root.Children.Add(Card(
                L("笔记本屏幕刷新率", "Laptop display refresh rate"),
                SettingRow(
                    L("刷新率", "Refresh rate"),
                    L(
                        "直接切换笔记本内置屏幕的刷新率；设置中的选项也用于 Fn+R 循环。",
                        "Switch the laptop panel refresh rate. Enabled rates are also used by the Fn+R cycle."),
                    control,
                    "\uE7F4"),
                null,
                "\uE7F4"));
        }

        if (report?.IsAvailable(FeatureIds.VantageEyeCare) != false)
        {
            var content = new StackPanel();
            content.Children.Add(SettingRow(
                L("护眼模式", "Eye care"),
                L("使用 Lenovo Vantage 提供的护眼功能。", "Use the eye-care feature provided by Lenovo Vantage."),
                _vantageEnabled,
                "\uE7F4"));
            content.Children.Add(SettingRow(
                L("色彩效果", "Color effect"),
                L("选择生动、护眼、泛黄或自定义颜色。", "Choose vivid, vision care, amber, or a custom color."),
                _effect));
            content.Children.Add(SettingRow(
                L("启用时段", "Schedule"),
                L("始终启用，或仅在夜间启用。", "Keep it enabled all day or only at night."),
                _schedule));
            content.Children.Add(SettingRow(
                L("自定义色温", "Custom temperature"),
                L("仅在自定义色彩效果下生效。", "Used only with the custom color effect."),
                _customTemperature));
            root.Children.Add(Card(
                L("Vantage 护眼", "Vantage eye care"),
                content,
                L("此功能可能只有在核显模式下可用。", "This feature may only be available in integrated-GPU mode."),
                "\uE706"));
        }

        if (report?.IsAvailable(FeatureIds.PcManagerEyeCare) != false)
        {
            var content = new StackPanel();
            content.Children.Add(SettingRow(
                L("电脑管家护眼", "PC Manager eye care"),
                L("切换电脑管家的 Gamma 色温方案。", "Switch the PC Manager gamma-temperature profile."),
                _pcManagerEnabled,
                "\uE793"));
            var temperatureControl = new StackPanel { Orientation = Orientation.Horizontal };
            temperatureControl.Children.Add(_pcTemperature);
            _pcTemperatureValue.Margin = new Thickness(10, 0, 0, 0);
            _pcTemperatureValue.VerticalAlignment = VerticalAlignment.Center;
            temperatureControl.Children.Add(_pcTemperatureValue);
            content.Children.Add(SettingRow(
                L("当前色温", "Current temperature"),
                L("护眼关闭时可即时调节。", "Adjustable while eye care is off."),
                temperatureControl));

            var defaults = new StackPanel { Orientation = Orientation.Horizontal };
            defaults.Children.Add(new TextBlock
            {
                Text = L("普通", "Normal"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            defaults.Children.Add(_normalDefault);
            defaults.Children.Add(new TextBlock
            {
                Text = L("护眼", "Eye care"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 6, 0)
            });
            defaults.Children.Add(_eyeDefault);
            var saveDefaults = ActionButton(L("保存默认值", "Save defaults"));
            saveDefaults.Margin = new Thickness(12, 0, 0, 0);
            saveDefaults.Click += async (_, _) => await SaveDefaultsAsync();
            defaults.Children.Add(saveDefaults);
            var restore = ActionButton(L("恢复当前默认值", "Restore current default"));
            restore.Margin = new Thickness(8, 0, 0, 0);
            restore.Click += async (_, _) => await RunAsync(_viewModel.RestorePcDefaultAsync);
            defaults.Children.Add(restore);
            content.Children.Add(SettingRow(
                L("默认色温", "Default temperatures"),
                L("这两个值保存到 Toolkit 配置中。", "These values are stored in the Toolkit configuration."),
                defaults));
            root.Children.Add(Card(
                L("电脑管家护眼", "PC Manager eye care"),
                content,
                null,
                "\uE8D7"));
        }

        if (report?.IsAvailable(FeatureIds.ColorManagement) != false)
        {
            root.Children.Add(Card(
                L("色彩管理", "Color management"),
                SettingRow(
                    L("显示色域", "Display color mode"),
                    L("仅列出当前面板实际支持的模式。", "Only modes supported by the current panel are listed."),
                    _colorMode,
                    "\uE790"),
                L("选择后立即应用新的色彩模式。", "The new color mode is applied immediately."),
                "\uE2B2"));
        }

        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        if (root.Children.Count == 1)
            root.Children.Insert(0, EmptyState(L("此设备没有可用的显示调节功能。", "No display controls are available on this device.")));
        return root;
    }

    private void PopulateChoices()
    {
        AddChoice(_effect, L("生动", "Vivid"), EyeCareColorEffect.Vivid);
        AddChoice(_effect, L("护眼", "Eye care"), EyeCareColorEffect.VisionCare);
        AddChoice(_effect, L("泛黄", "Amber"), EyeCareColorEffect.Amber);
        AddChoice(_effect, L("自定义颜色", "Custom color"), EyeCareColorEffect.Custom);
        AddChoice(_schedule, L("始终", "Always"), EyeCareScheduleMode.Always);
        AddChoice(_schedule, L("夜间", "Night"), EyeCareScheduleMode.Night);
        foreach (var value in Enumerable.Range(27, 39).Select(item => item * 100))
            AddChoice(_customTemperature, $"{value} K", value);
        foreach (var mode in new[]
                 {
                     ColorManagementMode.Default,
                     ColorManagementMode.AdobeRgb,
                     ColorManagementMode.Srgb,
                     ColorManagementMode.DisplayP3,
                     ColorManagementMode.Native,
                     ColorManagementMode.Rec709,
                     ColorManagementMode.DciP3,
                     ColorManagementMode.Auto,
                     ColorManagementMode.DicomDim,
                     ColorManagementMode.DicomOffice
                 })
        {
            AddChoice(_colorMode, ColorModeName(mode), mode);
        }
    }

    private void WireEvents()
    {
        _refreshRate.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<uint>(_refreshRate) is { } value)
                await RunAsync(() => _viewModel.SetRefreshRateAsync(value));
        };
        _refreshRateSettings.Click += async (_, _) =>
        {
            if (_viewModel.RefreshRate is not { } state)
                return;
            var window = new RefreshRateSettingsWindow(
                Window.GetWindow(this),
                Runtime,
                state,
                FontFamily,
                FontSize);
            if (window.ShowDialog() == true)
            {
                await _viewModel.ReloadRefreshRateAsync();
                SyncControls();
            }
        };
        _vantageEnabled.Click += async (_, _) =>
        {
            if (!_syncing) await RunAsync(() => _viewModel.SetVantageEnabledAsync(_vantageEnabled.IsChecked == true));
        };
        _effect.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<EyeCareColorEffect>(_effect) is { } value)
                await RunAsync(() => _viewModel.SetEffectAsync(value));
        };
        _schedule.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<EyeCareScheduleMode>(_schedule) is { } value)
                await RunAsync(() => _viewModel.SetScheduleAsync(value));
        };
        _customTemperature.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<int>(_customTemperature) is { } value)
                await RunAsync(() => _viewModel.SetCustomTemperatureAsync(value));
        };
        _pcManagerEnabled.Click += async (_, _) =>
        {
            if (!_syncing) await RunAsync(() => _viewModel.SetPcEnabledAsync(_pcManagerEnabled.IsChecked == true));
        };
        _pcTemperature.ValueChanged += (_, _) =>
        {
            _pcTemperatureValue.Text = $"{_pcTemperature.Value:0} K";
        };
        _pcTemperature.PreviewMouseUp += async (_, _) =>
        {
            if (!_syncing) await RunAsync(() => _viewModel.SetPcTemperatureAsync((int)_pcTemperature.Value));
        };
        _pcTemperature.PreviewKeyUp += async (_, args) =>
        {
            if (!_syncing && args.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                await RunAsync(() => _viewModel.SetPcTemperatureAsync((int)_pcTemperature.Value));
        };
        _colorMode.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<ColorManagementMode>(_colorMode) is { } value)
                await RunAsync(() => _viewModel.SetColorModeAsync(value));
        };
    }

    private async Task LoadAsync()
    {
        if (_viewModel.State is null)
            await _viewModel.LoadAsync();
        SyncControls();
    }

    private async Task RunAsync(Func<Task> action)
    {
        SetEnabled(false);
        await action();
        SyncControls();
    }

    private async Task SaveDefaultsAsync()
    {
        if (!int.TryParse(_normalDefault.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var normal) ||
            !int.TryParse(_eyeDefault.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eye) ||
            normal is < PcManagerEyeCareController.MinimumTemperature or > PcManagerEyeCareController.MaximumTemperature ||
            eye is < PcManagerEyeCareController.MinimumTemperature or > PcManagerEyeCareController.MaximumTemperature)
        {
            _status.Text = L("默认色温必须是 2000–11200 的整数。", "Default temperatures must be integers from 2000 to 11200.");
            return;
        }
        await RunAsync(() => _viewModel.SaveDefaultsAsync(normal, eye));
    }

    private void SyncControls()
    {
        _syncing = true;
        var state = _viewModel.State;
        RefreshRefreshRates(_viewModel.RefreshRate);
        if (state is not null)
        {
            _vantageEnabled.IsChecked = state.EyeCare.Enabled;
            Select(_effect, state.EyeCare.ColorEffect);
            Select(_schedule, state.EyeCare.ScheduleMode);
            Select(
                _customTemperature,
                state.EyeCare.CustomTemperature <= 0
                    ? 5000
                    : state.EyeCare.CustomTemperature);
            _pcManagerEnabled.IsChecked = state.PcManagerEyeCare.Enabled;
            _pcTemperature.Value = state.PcManagerEyeCare.CurrentTemperature;
            _pcTemperatureValue.Text = $"{state.PcManagerEyeCare.CurrentTemperature} K";
            _normalDefault.Text = state.PcManagerEyeCare.NormalDefaultTemperature.ToString(CultureInfo.InvariantCulture);
            _eyeDefault.Text = state.PcManagerEyeCare.EyeCareDefaultTemperature.ToString(CultureInfo.InvariantCulture);
            RefreshColorModes(state.ColorManagement);
        }
        _status.Text = _viewModel.Status;
        _syncing = false;
        SetEnabled(true);
    }

    private void RefreshRefreshRates(DisplayRefreshRateState? state)
    {
        _refreshRate.Items.Clear();
        if (state is null)
            return;
        var enabled = RefreshRateController.EffectiveCycleRates(
            state.AvailableHz,
            Runtime.Settings.RefreshRateCycleHz);
        foreach (var rate in enabled)
            AddChoice(_refreshRate, $"{rate} Hz", rate);
        if (!enabled.Contains(state.CurrentHz))
        {
            _refreshRate.Items.Add(new ComboBoxItem
            {
                Content = $"{state.CurrentHz} Hz",
                Tag = state.CurrentHz,
                IsEnabled = false
            });
        }
        Select(_refreshRate, state.CurrentHz);
    }

    private void RefreshColorModes(ColorManagementState state)
    {
        foreach (var item in _colorMode.Items.OfType<ComboBoxItem>())
            item.Visibility = item.Tag is ColorManagementMode mode && state.IsSupported(mode)
                ? Visibility.Visible
                : Visibility.Collapsed;
        Select(_colorMode, state.Mode);
    }

    private void SetEnabled(bool value)
    {
        var state = _viewModel.State;
        _vantageEnabled.IsEnabled = value && state?.EyeCare.Available == true;
        _effect.IsEnabled = value && state?.EyeCare is { Available: true, Enabled: true };
        _schedule.IsEnabled = value && state?.EyeCare is { Available: true, Enabled: true };
        _customTemperature.IsEnabled = value && state?.EyeCare is
        {
            Available: true,
            Enabled: true,
            ColorEffect: EyeCareColorEffect.Custom
        };
        _pcManagerEnabled.IsEnabled = value && state?.PcManagerEyeCare.Available == true;
        _pcTemperature.IsEnabled = value && state?.PcManagerEyeCare is { Available: true, Enabled: false };
        _colorMode.IsEnabled = value && state?.ColorManagement.Available == true;
        _refreshRate.IsEnabled = value &&
            _viewModel.RefreshRate?.AvailableHz.Count > 0;
        _refreshRateSettings.IsEnabled = value &&
            _viewModel.RefreshRate?.AvailableHz.Count > 0;
    }

    private static void AddChoice<T>(ComboBox combo, string label, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));

    private string ColorModeName(ColorManagementMode mode) => mode switch
    {
        ColorManagementMode.Srgb => "sRGB",
        ColorManagementMode.AdobeRgb => "Adobe RGB",
        ColorManagementMode.DisplayP3 => "Display P3",
        ColorManagementMode.Default => L("默认", "Default"),
        ColorManagementMode.Auto => L("自动", "Auto"),
        ColorManagementMode.Native => "Native",
        ColorManagementMode.Rec709 => "REC709",
        ColorManagementMode.DciP3 => "DCI P3",
        ColorManagementMode.DicomDim => "DICOM Dim",
        ColorManagementMode.DicomOffice => "DICOM Office",
        _ => mode.ToString()
    };

    private sealed class DisplayViewModel : ToolkitViewModelBase
    {
        public DisplayViewModel(ToolkitRuntimeService runtime) : base(runtime) { }
        public DisplaySettingsState? State { get; private set; }
        public DisplayRefreshRateState? RefreshRate { get; private set; }

        private PcManagerEyeCareDefaults Defaults => new PcManagerEyeCareDefaults(
            Runtime.Settings.PcManagerNormalDefaultTemperature,
            Runtime.Settings.PcManagerEyeCareDefaultTemperature).Normalize();

        public async Task LoadAsync()
        {
            IsBusy = true;
            var errors = new List<string>();
            if (Runtime.Report?.IsAvailable(
                    FeatureIds.DisplayRefreshRate) != false)
            {
                await ReloadRefreshRateAsync(errors);
            }
            if (Runtime.Report?.AnyAvailable(
                    FeatureIds.VantageEyeCare,
                    FeatureIds.PcManagerEyeCare,
                    FeatureIds.ColorManagement) != false)
            {
                try
                {
                    State = await Task.Run(() =>
                        DisplaySettingsController.ReadState(Defaults));
                }
                catch (Exception ex)
                {
                    errors.Add(Runtime.L(
                        "显示设置：",
                        "Display settings: ") + ex.Message);
                }
            }
            Status = errors.Count == 0
                ? string.Empty
                : Runtime.L(
                    "部分状态读取失败：",
                    "Some states could not be read: ") +
                  string.Join(" · ", errors);
            IsBusy = false;
        }

        public async Task ReloadRefreshRateAsync()
        {
            await ReloadRefreshRateAsync(null);
            if (RefreshRate is not null)
                Status = string.Empty;
        }

        private async Task ReloadRefreshRateAsync(
            ICollection<string>? errors)
        {
            var result = await Task.Run(() =>
            {
                var success = RefreshRateController.TryReadState(
                    out var state,
                    out var error);
                return (success, state, error);
            });
            if (result.success)
            {
                RefreshRate = result.state;
                return;
            }
            RefreshRate = null;
            if (errors is not null)
            {
                errors.Add(Runtime.L(
                    "刷新率：",
                    "Refresh rate: ") + result.error);
            }
            else
            {
                Status = Runtime.L(
                    "刷新率读取失败：",
                    "Could not read refresh rates: ") + result.error;
            }
        }

        public async Task SetRefreshRateAsync(uint value)
        {
            IsBusy = true;
            var result = await Task.Run(() =>
            {
                var success = RefreshRateController.TrySetRefreshRate(
                    value,
                    out var error);
                return (success, error);
            });
            if (!result.success)
            {
                Status = Runtime.L(
                    "刷新率切换失败：",
                    "Could not change the refresh rate: ") + result.error;
                IsBusy = false;
                return;
            }
            await ReloadRefreshRateAsync();
            if (RefreshRate is not null)
                Status = string.Empty;
            IsBusy = false;
        }

        public Task SetVantageEnabledAsync(bool value) =>
            UpdateEyeAsync(state => DisplaySettingsController.SetEyeCareEnabled(value, state));
        public Task SetEffectAsync(EyeCareColorEffect value) =>
            UpdateEyeAsync(state => DisplaySettingsController.SetEyeCareColorEffect(value, state));
        public Task SetScheduleAsync(EyeCareScheduleMode value) =>
            UpdateEyeAsync(state => DisplaySettingsController.SetEyeCareScheduleMode(value, state));
        public Task SetCustomTemperatureAsync(int value) =>
            UpdateEyeAsync(state => DisplaySettingsController.SetEyeCareCustomTemperature(value, state));

        private async Task UpdateEyeAsync(Func<EyeCareState, EyeCareState> update)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                () => update(previous.EyeCare),
                confirmed => State = previous with { EyeCare = confirmed },
                () => State = previous);
        }

        public async Task SetPcEnabledAsync(bool value) =>
            await UpdatePcAsync(() => PcManagerEyeCareController.SetEnabled(value, Defaults));
        public async Task SetPcTemperatureAsync(int value) =>
            await UpdatePcAsync(() => PcManagerEyeCareController.SetTemperature(value, Defaults));
        public async Task RestorePcDefaultAsync() =>
            await UpdatePcAsync(() => PcManagerEyeCareController.RestoreConfiguredDefault(Defaults));

        private async Task UpdatePcAsync(Func<PcManagerEyeCareState> update)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                update,
                confirmed => State = previous with { PcManagerEyeCare = confirmed },
                () => State = previous);
        }

        public async Task SetColorModeAsync(ColorManagementMode value)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                () => DisplaySettingsController.SetColorManagementMode(value),
                confirmed => State = previous with { ColorManagement = confirmed },
                () => State = previous);
        }

        public async Task SaveDefaultsAsync(int normal, int eye)
        {
            var previousNormal = Runtime.Settings.PcManagerNormalDefaultTemperature;
            var previousEye = Runtime.Settings.PcManagerEyeCareDefaultTemperature;
            Runtime.Settings.PcManagerNormalDefaultTemperature = normal;
            Runtime.Settings.PcManagerEyeCareDefaultTemperature = eye;
            CurveProfileStore.SaveSettings(Runtime.Settings);
            try
            {
                var confirmed = await Task.Run(() => PcManagerEyeCareController.ReadState(Defaults));
                if (State is not null)
                    State = State with { PcManagerEyeCare = confirmed };
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                Runtime.Settings.PcManagerNormalDefaultTemperature = previousNormal;
                Runtime.Settings.PcManagerEyeCareDefaultTemperature = previousEye;
                CurveProfileStore.SaveSettings(Runtime.Settings);
                Status = Runtime.L("保存失败，已回滚：", "Save failed; values rolled back: ") + ex.Message;
            }
        }

        private async Task WriteAsync<T>(Func<T> update, Action<T> commit, Action rollback)
        {
            IsBusy = true;
            try
            {
                var confirmed = await Task.Run(update);
                commit(confirmed);
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                rollback();
                Status = Runtime.L("写入失败，已恢复原状态：", "Write failed; previous state restored: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
