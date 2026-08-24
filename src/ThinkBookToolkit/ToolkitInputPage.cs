using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ThinkBookToolkit;

internal sealed class ToolkitInputPage : ToolkitPageBase,
    IControlStateRefreshable
{
    private readonly InputViewModel _viewModel;
    private readonly ComboBox _backlight = new() { MinWidth = 170 };
    private readonly CheckBox _autoOff = new();
    private readonly Dictionary<InputSettingKind, CheckBox> _toggles = [];
    private readonly TextBlock _status;
    private bool _syncing;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _disposed;

    public ToolkitInputPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new InputViewModel(runtime);
        DataContext = _viewModel;
        _status = StatusText();
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        AddChoice(_backlight, L("自动", "Auto"), KeyboardBacklightLevel.Auto);
        AddChoice(_backlight, L("低", "Low"), KeyboardBacklightLevel.Low);
        AddChoice(_backlight, L("高", "High"), KeyboardBacklightLevel.High);
        AddChoice(_backlight, L("关闭", "Off"), KeyboardBacklightLevel.Off);
        _backlight.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<KeyboardBacklightLevel>(_backlight) is { } value)
                await RunAsync(() => _viewModel.SetBacklightAsync(value));
        };
        _autoOff.Click += async (_, _) =>
        {
            if (!_syncing)
                await RunAsync(() => _viewModel.SetAutoOffAsync(_autoOff.IsChecked == true));
        };

        var root = new StackPanel();
        var keyboard = new StackPanel();
        if (Show(FeatureIds.KeyboardBacklight))
        {
            keyboard.Children.Add(SettingRow(
                L("键盘背光亮度", "Keyboard backlight"),
                L("选择自动、低、高或关闭。", "Choose auto, low, high, or off."),
                _backlight,
                "\uE765"));
        }
        if (Show(FeatureIds.KeyboardBacklightAutoOff))
        {
            keyboard.Children.Add(SettingRow(
                L("30 秒无操作后关闭背光", "Turn off after 30 seconds"),
                L("键盘或触摸板有操作时重新点亮。", "Keyboard or touchpad activity turns it back on."),
                _autoOff,
                "\uE823"));
        }
        if (keyboard.Children.Count > 0)
            root.Children.Add(Card(L("键盘背光", "Keyboard backlight"), keyboard, null, "\uE765"));

        var input = new StackPanel();
        AddToggle(input, InputSettingKind.FunctionLock, FeatureIds.FunctionLock,
            L("功能锁定（Fn + FnLock）", "Function lock (Fn + FnLock)"),
            L("切换功能键与多媒体键的默认行为。", "Change the default behavior of function and media keys."));
        AddToggle(input, InputSettingKind.CapsLockOsd, FeatureIds.CapsLockOsd,
            "CapsLock OSD",
            L("切换 CapsLock 时显示屏幕提示。", "Show an on-screen indicator when CapsLock changes."));
        AddToggle(input, InputSettingKind.NumLockOsd, FeatureIds.NumLockOsd,
            "NumLock OSD",
            L("切换 NumLock 时显示屏幕提示。", "Show an on-screen indicator when NumLock changes."));
        AddToggle(input, InputSettingKind.FnCtrlSwap, FeatureIds.FnCtrlSwap,
            L("Fn 键和 Ctrl 键互换", "Swap Fn and Ctrl"),
            L("设置后会自动确认设备状态。", "The device state is checked after each change."));
        AddToggle(input, InputSettingKind.Touchpad, FeatureIds.Touchpad,
            L("触摸板", "Touchpad"),
            L("启用或禁用精确式触摸板。", "Enable or disable the precision touchpad."));
        if (input.Children.Count > 0)
            root.Children.Add(Card(L("按键与触摸板", "Keys and touchpad"), input, null, "\uE7C9"));

        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        if (root.Children.Count == 1)
            root.Children.Insert(0, EmptyState(L("此设备没有可用的输入设备调节功能。", "No input controls are available on this device.")));
        return root;
    }

    private void AddToggle(
        Panel panel,
        InputSettingKind kind,
        string featureId,
        string title,
        string description)
    {
        if (!Show(featureId))
            return;
        var toggle = new CheckBox();
        toggle.Click += async (_, _) =>
        {
            if (!_syncing)
                await RunAsync(() => _viewModel.SetInputAsync(kind, toggle.IsChecked == true));
        };
        _toggles[kind] = toggle;
        panel.Children.Add(SettingRow(title, description, toggle));
    }

    private bool Show(string featureId) => Runtime.Report?.IsAvailable(featureId) != false;

    private async Task LoadAsync()
    {
        if (!_viewModel.Loaded)
            await _viewModel.LoadAsync();
        SyncControls();
    }

    public async Task RefreshControlStateAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            await _viewModel.LoadAsync();
            if (!_disposed)
                SyncControls();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        SetEnabled(false);
        await action();
        SyncControls();
    }

    private void SyncControls()
    {
        _syncing = true;
        var keyboard = _viewModel.Keyboard;
        if (keyboard is not null)
        {
            Select(_backlight, keyboard.Level);
            _autoOff.IsChecked = keyboard.AutoOffEnabled == true;
        }
        if (_viewModel.Input is { } input)
        {
            foreach (var pair in _toggles)
            {
                var state = input.Get(pair.Key);
                pair.Value.IsChecked = state.Enabled;
                pair.Value.ToolTip = state.Error;
            }
        }
        _status.Text = _viewModel.Status;
        _syncing = false;
        SetEnabled(true);
    }

    private void SetEnabled(bool value)
    {
        _backlight.IsEnabled = value && _viewModel.Keyboard?.Level.HasValue == true;
        _autoOff.IsEnabled = value && _viewModel.Keyboard?.AutoOffSupported == true;
        foreach (var pair in _toggles)
            pair.Value.IsEnabled = value && _viewModel.Input?.Get(pair.Key).Supported == true;
    }

    private static void AddChoice<T>(ComboBox combo, string label, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static void Select<T>(ComboBox combo, T? value) where T : struct =>
        combo.SelectedItem = value.HasValue
            ? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value.Value))
            : null;

    private sealed class InputViewModel : ToolkitViewModelBase
    {
        public InputViewModel(ToolkitRuntimeService runtime) : base(runtime) { }

        public KeyboardBacklightState? Keyboard { get; private set; }
        public InputSettingsState? Input { get; private set; }
        public bool Loaded { get; private set; }

        public async Task LoadAsync()
        {
            IsBusy = true;
            var errors = new List<string>();
            if (Runtime.Report?.AnyAvailable(
                    FeatureIds.KeyboardBacklight,
                    FeatureIds.KeyboardBacklightAutoOff) != false)
            {
                try
                {
                    Keyboard = await Task.Run(KeyboardBacklightController.ReadState);
                }
                catch (Exception ex)
                {
                    errors.Add(Runtime.L("键盘背光：", "Keyboard backlight: ") + ex.Message);
                }
            }

            if (Runtime.Report?.AnyAvailable(
                    FeatureIds.FunctionLock,
                    FeatureIds.CapsLockOsd,
                    FeatureIds.NumLockOsd,
                    FeatureIds.FnCtrlSwap,
                    FeatureIds.Touchpad) != false)
            {
                try
                {
                    Input = await Task.Run(() =>
                        InputSettingsController.ReadState(
                            refreshWmiState: true));
                    if (Runtime.Settings.TakeOverFnKeys)
                    {
                        Input = Input
                            .With(
                                InputSettingKind.CapsLockOsd,
                                new ToggleSettingState(
                                    true,
                                    Runtime.Settings.ShowCapsLockOsd))
                            .With(
                                InputSettingKind.NumLockOsd,
                                new ToggleSettingState(
                                    true,
                                    Runtime.Settings.ShowNumLockOsd));
                    }
                }
                catch (Exception ex)
                {
                    if (Runtime.Settings.TakeOverFnKeys)
                    {
                        var failed = ToggleSettingState.Failed(ex);
                        Input = new InputSettingsState(
                            failed,
                            new ToggleSettingState(
                                true,
                                Runtime.Settings.ShowCapsLockOsd),
                            new ToggleSettingState(
                                true,
                                Runtime.Settings.ShowNumLockOsd),
                            failed,
                            failed);
                    }
                    errors.Add(Runtime.L("按键与触摸板：", "Keys and touchpad: ") + ex.Message);
                }
            }

            Status = errors.Count == 0
                ? string.Empty
                : Runtime.L("部分状态读取失败：", "Some states could not be read: ") + string.Join(" · ", errors);
            Loaded = true;
            IsBusy = false;
        }

        public async Task SetBacklightAsync(KeyboardBacklightLevel value)
        {
            var previous = Keyboard;
            await WriteAsync(
                async () => Keyboard = await Task.Run(() => KeyboardBacklightController.SetBrightness(value)),
                () => Keyboard = previous);
        }

        public async Task SetAutoOffAsync(bool value)
        {
            var previous = Keyboard;
            await WriteAsync(
                async () => Keyboard = await Task.Run(() => KeyboardBacklightController.SetAutoOff(value)),
                () => Keyboard = previous);
        }

        public async Task SetInputAsync(InputSettingKind kind, bool value)
        {
            var previous = Input;
            await WriteAsync(
                async () =>
                {
                    if (Runtime.Settings.TakeOverFnKeys &&
                        kind is InputSettingKind.CapsLockOsd or
                            InputSettingKind.NumLockOsd)
                    {
                        if (!Runtime.TrySetLockKeyOsd(
                                kind,
                                value,
                                out var saveError))
                        {
                            throw new InvalidOperationException(saveError);
                        }
                        Input = (Input ??
                            InputSettingsController.ReadState())
                            .With(
                                kind,
                                new ToggleSettingState(true, value));
                        return;
                    }
                    var confirmed = await Task.Run(() => InputSettingsController.SetState(kind, value));
                    if (!confirmed.Supported || confirmed.Enabled != value)
                        throw new InvalidOperationException(Runtime.L("硬件未确认新的状态。", "Hardware did not confirm the new state."));
                    Input = (Input ?? InputSettingsController.ReadState()).With(kind, confirmed);
                    if (kind is InputSettingKind.CapsLockOsd or
                        InputSettingKind.NumLockOsd)
                    {
                        if (!Runtime.TrySetLockKeyOsd(
                                kind,
                                value,
                                out var saveError))
                        {
                            throw new InvalidOperationException(saveError);
                        }
                    }
                },
                () => Input = previous);
        }

        private async Task WriteAsync(Func<Task> action, Action rollback)
        {
            IsBusy = true;
            try
            {
                await action();
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

    public override void Dispose()
    {
        _disposed = true;
        base.Dispose();
    }
}
