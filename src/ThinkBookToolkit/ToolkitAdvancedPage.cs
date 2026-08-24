using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ThinkBookToolkit;

internal sealed class ToolkitAdvancedPage : ToolkitPageBase
{
    private readonly AdvancedViewModel _viewModel;
    private readonly Dictionary<BiosBootFunction, Button> _bootButtons = [];
    private readonly Dictionary<string, CheckBox> _ioToggles = [];
    private readonly AdaptiveUniformPanel _ioRows = new()
    {
        MinimumItemWidth = 330,
        MaximumColumns = 3,
        Spacing = 10,
        Tag = "BiosIoResponsiveGrid"
    };
    private readonly Button _logoButton;
    private readonly TextBlock _status;
    private readonly TextBlock _ioStatus;
    private bool _syncingIo;

    public ToolkitAdvancedPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new AdvancedViewModel(runtime);
        DataContext = _viewModel;
        _status = StatusText();
        _ioStatus = StatusText();
        _logoButton = ActionButton(L("更换开机画面", "Customize boot logo"));
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();
        var actions = new AdaptiveUniformPanel { MinimumItemWidth = 300, Spacing = 10 };

        if (Show(FeatureIds.BootLogo))
        {
            _logoButton.Click += (_, _) =>
            {
                var owner = Window.GetWindow(this);
                var dialog = new BootLogoCustomizationWindow(
                    owner,
                    key => MainWindow.Translate(key, Runtime.IsChinese),
                    Runtime.IsDark,
                    FontFamily,
                    FontSize);
                dialog.ShowDialog();
            };
            actions.Children.Add(ActionTile(
                L("开机画面", "Boot logo"),
                L("预览并更换固件开机画面，预览区使用 16:10 屏幕比例。", "Preview or replace the firmware boot logo in a 16:10 viewport."),
                "\uE91B",
                _logoButton));
        }

        AddBootAction(actions, BiosBootFunction.SetupUtility, FeatureIds.BiosSetup,
            L("进入 BIOS", "Enter BIOS setup"),
            L("设置下一次启动进入 BIOS 设置。", "Enter BIOS setup on the next boot."),
            "\uE713",
            danger: false);
        AddBootAction(actions, BiosBootFunction.InterruptMenu, FeatureIds.StartupInterrupt,
            L("启动中断菜单", "Startup interrupt menu"),
            L("设置下一次启动进入 Lenovo 启动中断菜单。", "Open the Lenovo startup interrupt menu on the next boot."),
            "\uE7E8",
            danger: false);
        AddBootAction(actions, BiosBootFunction.SecureWipe, FeatureIds.SecureWipe,
            L("安全擦除", "Secure wipe"),
            L("高风险：重启后进入固件安全擦除流程。", "High risk: restart into the firmware secure-wipe flow."),
            "\uE74D",
            danger: true);

        if (actions.Children.Count > 0)
        {
            root.Children.Add(Card(
                L("固件启动工具", "Firmware startup tools"),
                actions,
                L("启动操作需要两次确认，确认后会立即重启。", "Boot actions require two confirmations and then restart immediately."),
                "\uE90F"));
        }

        if (Show(FeatureIds.BiosIoControl))
        {
            _ioRows.Children.Add(new TextBlock
            {
                Text = L("正在读取 BIOS I/O 设置……", "Reading BIOS I/O settings…"),
                Foreground = Brush(Palette.Muted),
                Margin = new Thickness(2, 2, 2, 4)
            });
            root.Children.Add(Card(
                L("IO控制（重启后生效）", "I/O controls (effective after restart)"),
                _ioRows,
                L(
                    "只显示 BIOS 实际暴露且允许启用/禁用的项目。",
                    "Only settings exposed by the BIOS with both enabled and disabled values are shown."),
                "\uE950"));
            _ioStatus.Margin = new Thickness(4, 2, 4, 10);
            root.Children.Add(_ioStatus);
        }

        if (actions.Children.Count == 0 && !Show(FeatureIds.BiosIoControl))
            root.Children.Add(EmptyState(L("此设备没有可用的高级固件工具。", "No advanced firmware tools are available on this device.")));

        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        return root;
    }

    private Border ActionTile(string title, string description, string glyph, Button action)
    {
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Margin = new Thickness(0, 14, 0, 0);
        var content = new StackPanel();
        content.Children.Add(IconTile(glyph, Palette.Accent, 42, 17));
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(0, 13, 0, 5)
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush(Palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 42
        });
        content.Children.Add(action);
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = content
        };
    }

    private void AddBootAction(
        Panel panel,
        BiosBootFunction function,
        string featureId,
        string title,
        string description,
        string glyph,
        bool danger)
    {
        if (!Show(featureId))
            return;
        var button = ActionButton(
            danger ? L("安全擦除并重启", "Secure wipe and restart") : L("设置并重启", "Set and restart"),
            danger: danger);
        button.Click += async (_, _) => await RunBootFunctionAsync(function);
        _bootButtons[function] = button;
        panel.Children.Add(ActionTile(title, description, glyph, button));
    }

    private bool Show(string featureId) => Runtime.Report?.IsAvailable(featureId) != false;

    private async Task LoadAsync()
    {
        if (_viewModel.Support is null)
            await _viewModel.LoadAsync();
        _status.Text = _viewModel.Status;
        if (Show(FeatureIds.BiosIoControl))
            await LoadIoAsync();
    }

    private async Task LoadIoAsync()
    {
        SetIoEnabled(false);
        try
        {
            var states = await Task.Run(BiosIoController.ReadSupportedStates);
            RenderIoStates(states);
            _ioStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("BIOS I/O settings could not be read.", ex);
            _ioRows.Children.Clear();
            _ioRows.Children.Add(new TextBlock
            {
                Text = L("无法读取 BIOS I/O 设置。", "BIOS I/O settings could not be read."),
                Foreground = Brush(Palette.Muted)
            });
            _ioStatus.Text = L("IO 控制不可用：", "I/O controls unavailable: ") + ex.Message;
        }
        finally
        {
            SetIoEnabled(true);
        }
    }

    private void RenderIoStates(IReadOnlyList<BiosIoState> states)
    {
        _syncingIo = true;
        _ioRows.Children.Clear();
        _ioToggles.Clear();
        foreach (var state in states)
        {
            var toggle = new CheckBox { IsChecked = state.Enabled };
            toggle.Click += async (_, _) =>
            {
                if (!_syncingIo)
                    await SetIoStateAsync(state, toggle.IsChecked == true);
            };
            _ioToggles[state.Definition.Id] = toggle;
            _ioRows.Children.Add(SettingRow(
                Runtime.IsChinese
                    ? state.Definition.ChineseName
                    : state.Definition.EnglishName,
                Runtime.IsChinese
                    ? state.Definition.ChineseDescription
                    : state.Definition.EnglishDescription,
                toggle,
                state.Definition.IsVirtualization ? "\uE968" : "\uE950"));
        }

        if (states.Count == 0)
        {
            _ioRows.Children.Add(new TextBlock
            {
                Text = L(
                    "此设备的 BIOS 没有暴露可由 Toolkit 调整的 I/O 项目。",
                    "This device's BIOS exposes no I/O settings that Toolkit can change."),
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap
            });
        }
        _syncingIo = false;
    }

    private async Task SetIoStateAsync(BiosIoState state, bool enabled)
    {
        SetIoEnabled(false);
        _ioStatus.Text = L("正在保存 BIOS 设置……", "Saving BIOS setting…");
        try
        {
            _ = await Task.Run(() =>
                BiosIoController.SetEnabled(state.Definition.Id, enabled));
            ToolkitLog.Info(
                $"BIOS I/O setting {state.Definition.Id} was set to {(enabled ? "Enable" : "Disable")}.");
            _syncingIo = true;
            _ioToggles[state.Definition.Id].IsChecked = enabled;
            _syncingIo = false;
            _ioStatus.Text = L("设置已保存。", "Setting saved.");
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                $"BIOS I/O setting {state.Definition.Id} could not be changed.",
                ex);
            var failure = L("设置失败：", "Setting failed: ") + ex.Message;
            await LoadIoAsync();
            _ioStatus.Text = failure;
        }
        finally
        {
            SetIoEnabled(true);
        }
    }

    private void SetIoEnabled(bool enabled)
    {
        foreach (var toggle in _ioToggles.Values)
            toggle.IsEnabled = enabled;
    }

    private async Task RunBootFunctionAsync(BiosBootFunction function)
    {
        var name = function switch
        {
            BiosBootFunction.SetupUtility => L("BIOS 设置", "BIOS setup"),
            BiosBootFunction.InterruptMenu => L("启动中断菜单", "startup interrupt menu"),
            BiosBootFunction.SecureWipe => L("安全擦除", "secure wipe"),
            _ => function.ToString()
        };
        var warning = function == BiosBootFunction.SecureWipe
            ? L("安全擦除可能永久删除存储设备上的数据。是否继续？", "Secure wipe may permanently delete storage data. Continue?")
            : L($"是否把下一次启动设置为{name}？", $"Set the next boot to {name}?");
        if (MessageBox.Show(
                Window.GetWindow(this),
                warning,
                "ThinkBook Toolkit",
                MessageBoxButton.YesNo,
                function == BiosBootFunction.SecureWipe ? MessageBoxImage.Warning : MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        if (MessageBox.Show(
                Window.GetWindow(this),
                L("操作完成后计算机会立即重启。现在执行吗？", "The computer will restart immediately after this is set. Continue now?"),
                "ThinkBook Toolkit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SetButtonsEnabled(false);
        var success = await _viewModel.RunBootFunctionAsync(function);
        _status.Text = _viewModel.Status;
        if (success)
            BiosAdvancedController.RestartComputer();
        else
            SetButtonsEnabled(true);
    }

    private void SetButtonsEnabled(bool value)
    {
        _logoButton.IsEnabled = value;
        foreach (var button in _bootButtons.Values)
            button.IsEnabled = value;
    }

    private sealed class AdvancedViewModel : ToolkitViewModelBase
    {
        public AdvancedViewModel(ToolkitRuntimeService runtime) : base(runtime) { }
        public BiosAdvancedSupport? Support { get; private set; }

        public async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                Support = await Task.Run(BiosAdvancedController.ReadSupport);
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                Status = Runtime.L("固件能力读取失败：", "Firmware capability check failed: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }

        public async Task<bool> RunBootFunctionAsync(BiosBootFunction function)
        {
            IsBusy = true;
            try
            {
                await Task.Run(() => BiosAdvancedController.SetBootFunction(function));
                Status = Runtime.L("固件已确认，正在重启……", "Firmware confirmed; restarting…");
                return true;
            }
            catch (Exception ex)
            {
                Status = Runtime.L("操作失败：", "Operation failed: ") + ex.Message;
                return false;
            }
            finally { IsBusy = false; }
        }
    }
}
