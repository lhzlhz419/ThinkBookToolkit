using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed class ToolkitPerformancePage : ToolkitPageBase
{
    private readonly bool _coolingOnly;
    private readonly ComboBox _itsMode = new() { MinWidth = 190 };
    private readonly Button _performanceModeOrderSettings;
    private readonly ComboBox _gpuMode = new() { MinWidth = 210 };
    private readonly TextBlock _modeStatus;
    private readonly TextBlock _pendingRestartText = new();
    private readonly Button _restartNow;
    private Border _pendingRestartRow = new();
    private readonly TextBlock _discreteGpuStatus = new();
    private readonly Button _viewGpuApplications;
    private readonly Button _killGpuApplications;
    private readonly Button _gpuOverclockSettings;
    private readonly CheckBox _gpuOverclockEnabled = new();
    private Border _discreteGpuStatusRow = new();
    private Border _gpuOverclockRow = new();
    private readonly CheckBox _fullSpeed = new();
    private readonly TextBlock _fanStatus;
    private readonly ComboBox _strategy = new() { MinWidth = 180 };
    private readonly ComboBox _fixedMode = new() { MinWidth = 170 };
    private readonly StackPanel _fixedPanel = new();
    private readonly StackPanel _fixedPreferences = new();
    private readonly StackPanel _curvePanel = new();
    private readonly StackPanel _advancedCurvePanel = new();
    private readonly CheckBox _linkFanStrategyToPerformanceMode = new();
    private readonly ComboBox _fanControlTargetMode = new()
        { MinWidth = 170 };
    private readonly Dictionary<ItsMode, ComboBox>
        _fanStrategyByPerformanceMode = [];
    private readonly Dictionary<ItsMode, CheckBox>
        _fanControlNoSwitchModes = [];
    private readonly StackPanel _fanStrategyBindings = new();
    private readonly StackPanel _fanControlWhitelist = new();
    private readonly Dictionary<string, TextBox> _fixedBoxes = [];
    private readonly ComboBox _profile = new() { MinWidth = 200 };
    private readonly TextBox _profileName = new() { MinWidth = 180 };
    private readonly ComboBox _editFan = new() { MinWidth = 120 };
    private readonly CheckBox _independentCurves = new();
    private readonly CheckBox _syncFixed = new();
    private readonly CheckBox _autoGames = new();
    private readonly ComboBox _gameHold = new() { MinWidth = 150 };
    private readonly TextBox _hotkey = new() { Width = 150 };
    private readonly ComboBox _smoothing = new() { MinWidth = 150 };
    private readonly ComboBox _curveRampUp = new() { MinWidth = 150 };
    private readonly ComboBox _curveRampDown = new() { MinWidth = 150 };
    private readonly ComboBox _rampDown = new() { MinWidth = 150 };
    private readonly ComboBox _advancedSmoothing = new() { MinWidth = 150 };
    private readonly AdvancedFanCurveEditor _advancedCurve;
    private readonly CurveEditor _cpuCurve;
    private readonly CurveEditor _gpuCurve;
    private readonly TextBlock _draftStatus;
    private readonly Button _applyFan;
    private readonly Button _discardFan;
    private readonly Button _fanLimitsButton;
    private readonly Dictionary<string, PowerIntegerEditor> _powerEditors = [];
    private readonly ComboBox _turboTime = new() { MinWidth = 150 };
    private readonly TextBlock _powerStatus;
    private readonly Button _applyPower;
    private readonly Button _discardPower;
    private readonly Button _defaultPower;
    private readonly Button _togglePowerEditor;
    private readonly Dictionary<PowerSetting, CheckBox> _powerLockToggles = [];
    private readonly ComboBox _powerLockInterval = new() { MinWidth = 96 };
    private readonly StackPanel _powerEditorPanel = new();
    private readonly Border _powerEditorHost = new();
    private readonly Dictionary<string, TextBlock> _powerReadouts = [];
    private readonly Dictionary<PowerSetting, FrameworkElement> _powerReadoutRows = [];
    private readonly Dictionary<PowerSetting, FrameworkElement> _powerEditorRows = [];
    private FrameworkElement? _atppReadout;
    private FrameworkElement? _atppEditorRow;
    private List<FanProfile> _profiles = [];
    private FanProfile? _draftProfile;
    private PowerSettingsState? _confirmedPower;
    private bool _fanDirty;
    private bool _powerDirty;
    private bool _powerEditorExpanded;
    private bool _powerRefreshInProgress;
    private bool _fanControlsBuilt;
    private bool _disposed;
    private bool _syncing;

    public ToolkitPerformancePage(
        ToolkitRuntimeService runtime,
        bool coolingOnly = false) : base(runtime)
    {
        _coolingOnly = coolingOnly;
        _modeStatus = StatusText();
        _restartNow = ActionButton(L("立即重启", "Restart now"), primary: true);
        _viewGpuApplications = ActionButton(
            L("查看占用应用", "View applications"));
        _killGpuApplications = ActionButton(
            L("强制关闭占用应用", "Force close applications"),
            danger: true);
        _gpuOverclockSettings = ActionButton(L("设置", "Settings"));
        _performanceModeOrderSettings = ActionButton(
            L("设置", "Settings"));
        _fanStatus = StatusText();
        _draftStatus = StatusText();
        _powerStatus = StatusText();
        _applyFan = ActionButton(L("应用风扇设置", "Apply fan settings"), primary: true);
        _discardFan = ActionButton(L("放弃未保存更改", "Discard changes"));
        _fanLimitsButton = ActionButton(L("转速上下限", "RPM limits"));
        _applyPower = ActionButton(L("应用功耗设置", "Apply power settings"), primary: true);
        _discardPower = ActionButton(L("放弃未保存更改", "Discard changes"));
        _defaultPower = ActionButton(L("载入当前模式默认值", "Load current-mode defaults"));
        _togglePowerEditor = ActionButton(L("展开设置", "Expand settings"));
        var fallback = CurveProfileStore.Load().First();
        _cpuCurve = new CurveEditor(
            L("CPU 风扇曲线", "CPU fan curve"),
            CurveProfileStore.CpuTemps,
            fallback.CpuFan1Curve,
            fallback.CpuFan2Curve);
        _gpuCurve = new CurveEditor(
            L("GPU 风扇曲线", "GPU fan curve"),
            CurveProfileStore.GpuTemps,
            fallback.GpuFan1Curve,
            fallback.GpuFan2Curve);
        _advancedCurve = new AdvancedFanCurveEditor(
            runtime.IsChinese,
            runtime.IsDark,
            runtime.Settings.AdvancedFanCurve.Points);
        ConfigureCurves();
        DataContext = new PerformanceViewModel(runtime);
        Content = BuildLayout();
        WireEvents();
        UpdateFanDraftStatus();
        UpdatePowerStatus();
        runtime.SnapshotChanged += OnSnapshotChanged;
        runtime.FnKeyTakeoverChanged += OnFnKeyTakeoverChanged;
        SyncRuntimeControls();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();
        if (_coolingOnly &&
            Runtime.Report?.AnyAvailable(FeatureIds.TemperatureMonitoring) != false)
        {
            root.Children.Add(BuildTelemetry());
        }

        if (!_coolingOnly &&
            Runtime.Report?.AnyAvailable(
                FeatureIds.PerformanceMode,
                FeatureIds.GpuMode,
                FeatureIds.TemperatureMonitoring) != false)
            root.Children.Add(BuildModeCard());
        if (_coolingOnly && Runtime.Report?.IsAvailable(FeatureIds.FanControl) != false)
            root.Children.Add(BuildFanCard());
        if (_coolingOnly &&
            Runtime.Report?.IsAvailable(FeatureIds.FanControl) != false &&
            Runtime.Report?.IsAvailable(FeatureIds.PerformanceMode) != false)
        {
            root.Children.Add(BuildFanPerformanceLinkCard());
        }
        if (!_coolingOnly && Runtime.Report?.IsAvailable(FeatureIds.PowerSettings) == true)
            root.Children.Add(BuildPowerCard());
        if (root.Children.Count == 0)
            root.Children.Add(EmptyState(L("此设备没有可用的性能调节功能。", "No performance controls are available on this device.")));
        return root;
    }

    private UIElement BuildTelemetry()
    {
        var layout = Runtime.Settings.OverviewLayout;
        var panel = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 155,
            Spacing = 8
        };
        Add(
            OverviewCardIds.Cpu,
            ["temperature", "power"],
            "CPU",
            nameof(PerformanceViewModel.CompactCpu),
            L("温度 / 功耗", "Temperature / power"),
            "\uE950",
            Palette.Accent);
        Add(
            OverviewCardIds.Gpu,
            ["core-temperature", "power"],
            "GPU",
            nameof(PerformanceViewModel.CompactGpu),
            L("温度 / 功耗", "Temperature / power"),
            "\uE7F4",
            "#A984FF");
        Add(
            OverviewCardIds.Gpu,
            ["vram-temperature", "hotspot-temperature"],
            L("显存与热点", "VRAM and hot spot"),
            nameof(PerformanceViewModel.CompactVramAndHotSpot),
            L("显存 / 热点", "VRAM / hot spot"),
            "\uE7F4",
            "#49BCE8");
        Add(
            OverviewCardIds.Fans,
            ["fan1-speed", "fan2-speed"],
            L("双风扇", "Dual fans"),
            nameof(PerformanceViewModel.CompactFans),
            "FAN1 / FAN2",
            "\uE9CA",
            "#56C2C9");
        Add(
            OverviewCardIds.Fans,
            ["fan1-target", "fan2-target"],
            L("转速目标", "Speed target"),
            nameof(PerformanceViewModel.CompactFanTargets),
            "FAN1 / FAN2",
            "\uE768",
            Palette.Warning);
        return Card(
            L("实时状态", "Live status"),
            panel,
            L("使用全局刷新间隔更新。", "Updated using the global refresh interval."),
            "\uE9D9");

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
            panel.Children.Add(MetricCard(
                title,
                value,
                detail,
                glyph,
                accent,
                18,
                true));
        }
    }

    private UIElement BuildModeCard()
    {
        AddChoice(_itsMode, L("智能模式", "Auto"), ItsMode.Intelligent);
        AddChoice(_itsMode, L("省电模式", "Cool"), ItsMode.PowerSaving);
        AddChoice(_itsMode, L("性能模式", "Performance"), ItsMode.Performance);
        AddChoice(_itsMode, L("极客模式", "Geek"), ItsMode.Geek);
        foreach (GpuWorkingMode mode in Enum.GetValues<GpuWorkingMode>())
            AddChoice(_gpuMode, GpuModeName(mode), mode);
        var content = new StackPanel();
        var choices = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 400,
            Spacing = 8
        };
        if (Runtime.Report?.IsAvailable(FeatureIds.PerformanceMode) != false)
        {
            var performanceModeControl = new Grid
            {
                MinWidth = 430,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            performanceModeControl.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            performanceModeControl.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            _itsMode.HorizontalAlignment = HorizontalAlignment.Stretch;
            performanceModeControl.Children.Add(_itsMode);
            _performanceModeOrderSettings.Margin =
                new Thickness(10, 0, 0, 0);
            Grid.SetColumn(_performanceModeOrderSettings, 1);
            performanceModeControl.Children.Add(
                _performanceModeOrderSettings);
            choices.Children.Add(SettingRow(
                L("性能模式", "Performance mode"),
                L("切换 Lenovo ITS 固件性能状态。", "Switch the Lenovo ITS firmware performance state."),
                performanceModeControl,
                "\uE945"));
        }
        if (Runtime.Report?.IsAvailable(FeatureIds.GpuMode) != false)
        {
            choices.Children.Add(SettingRow(
                L("GPU 工作模式", "GPU working mode"),
                L("需要重启的切换会显示为等待重启，不会自动重启。", "Changes that require a restart remain pending; Toolkit never restarts automatically."),
                _gpuMode,
                "\uE7F4"));
        }
        content.Children.Add(choices);

        var gpuStatusControl = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _viewGpuApplications.Margin = new Thickness(0, 0, 8, 0);
        _killGpuApplications.Margin = new Thickness(0, 0, 14, 0);
        _discreteGpuStatus.VerticalAlignment = VerticalAlignment.Center;
        _discreteGpuStatus.FontWeight = FontWeights.SemiBold;
        _discreteGpuStatus.Foreground = Brush(Palette.Text);
        gpuStatusControl.Children.Add(_viewGpuApplications);
        gpuStatusControl.Children.Add(_killGpuApplications);
        gpuStatusControl.Children.Add(_discreteGpuStatus);
        _discreteGpuStatusRow = SettingRow(
            L("独立显卡状态", "Discrete GPU status"),
            L(
                "显示独显当前活动状态和性能状态。",
                "Shows the current discrete-GPU activity and performance state."),
            gpuStatusControl,
            "\uE7F4");
        if (Runtime.Report?.IsAvailable(
                FeatureIds.DiscreteGpuManagement) != false)
        {
            content.Children.Add(_discreteGpuStatusRow);
        }

        var overclockControl = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _gpuOverclockSettings.Margin = new Thickness(0, 0, 12, 0);
        overclockControl.Children.Add(_gpuOverclockSettings);
        overclockControl.Children.Add(_gpuOverclockEnabled);
        _gpuOverclockRow = SettingRow(
            L("超频独立显卡", "Overclock discrete GPU"),
            L(
                "超频需谨慎，不要与其它超频软件一起使用；如需更完整的显卡超频功能，建议改用 MSI Afterburner。",
                "Overclock with care. Do not use Toolkit together with other overclocking software; use MSI Afterburner instead for more complete GPU overclocking features."),
            overclockControl,
            "\uE945");
        if (Runtime.Report?.IsAvailable(FeatureIds.GpuOverclock) != false)
            content.Children.Add(_gpuOverclockRow);

        var restartControl = new StackPanel { Orientation = Orientation.Horizontal };
        _pendingRestartText.Foreground = Brush(Palette.Warning);
        _pendingRestartText.VerticalAlignment = VerticalAlignment.Center;
        _pendingRestartText.TextWrapping = TextWrapping.Wrap;
        _pendingRestartText.Margin = new Thickness(0, 0, 12, 0);
        restartControl.Children.Add(_pendingRestartText);
        restartControl.Children.Add(_restartNow);
        _pendingRestartRow = SettingRow(
            L("等待重启", "Restart pending"),
            L("当前会话继续使用原 GPU 模式；可以稍后自行重启。", "The current session continues using the previous GPU mode; you can also restart later."),
            restartControl,
            "\uE777");
        content.Children.Add(_pendingRestartRow);
        content.Children.Add(_modeStatus);
        return Card(
            L("性能与 GPU 模式", "Performance and GPU modes"),
            content,
            null,
            "\uE945");
    }

    private UIElement BuildFanCard()
    {
        PopulateFanPreferenceChoices();
        var content = new StackPanel();
        content.Children.Add(SettingRow(
            L("风扇拉满", "Full fan speed"),
            FanFullSpeedDescription(),
            _fullSpeed));
        content.Children.Add(SettingRow(
            L("控制策略", "Control strategy"),
            L("固件自动不写入转速；固定转速和曲线策略由 Toolkit 接管。", "Firmware automatic writes no RPM target; fixed RPM and fan curves are controlled by Toolkit."),
            _strategy));

        BuildFixedPanel();
        BuildCurvePanel();
        BuildAdvancedCurvePanel();
        content.Children.Add(_fixedPanel);
        content.Children.Add(_curvePanel);
        content.Children.Add(_advancedCurvePanel);

        _fixedPreferences.Children.Add(SettingRow(
            L("自动检测游戏", "Detect games automatically"),
            L("检测游戏进程并使用游戏转速；退出后按保持时间恢复。", "Use game RPM while a game is detected and hold it briefly after exit."),
            _autoGames));
        _fixedPreferences.Children.Add(SettingRow(
            L("游戏退出保持时间", "Game-exit hold"),
            L("游戏结束后继续保持游戏转速的时间。", "How long game RPM remains active after the game exits."),
            _gameHold));
        _fixedPreferences.Children.Add(SettingRow(
            L("固定模式快捷键", "Fixed-mode hotkey"),
            L("例如 Ctrl+Alt+F；留空即关闭。", "For example Ctrl+Alt+F; leave blank to disable."),
            _hotkey));
        _fixedPanel.Children.Add(_fixedPreferences);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_applyFan);
        buttons.Children.Add(_discardFan);
        footer.Children.Add(buttons);
        _fanLimitsButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_fanLimitsButton, 1);
        footer.Children.Add(_fanLimitsButton);
        content.Children.Add(footer);
        content.Children.Add(_draftStatus);
        content.Children.Add(_fanStatus);
        _fanControlsBuilt = true;
        return Card(
            L("风扇控制", "Fan control"),
            content,
            L("固件自动与三种 Toolkit 策略在同一处切换；编辑内容采用集中应用。", "Switch between firmware automatic and all three Toolkit strategies in one place; edits use Apply."),
            "\uE9CA",
            "#56C2C9");
    }

    private UIElement BuildFanPerformanceLinkCard()
    {
        var content = new StackPanel();
        content.Children.Add(SettingRow(
            L(
                "切换性能模式时，自动切换风扇策略",
                "Switch fan strategy with performance mode"),
            L(
                "为四种性能模式分别指定风扇策略；确认模式切换成功并等待 2 秒后应用。",
                "Assign a fan strategy to each performance mode. It is applied two seconds after the mode switch is confirmed."),
            _linkFanStrategyToPerformanceMode));

        var bindingGrid = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 250,
            Spacing = 8
        };
        var profiles = CurveProfileStore.Load();
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            var selector = new ComboBox { MinWidth = 190 };
            AddChoice(
                selector,
                L("固件自动", "Firmware automatic"),
                new FanStrategyChoice(
                    FanControlMode.FirmwareAutomatic,
                    0));
            AddChoice(
                selector,
                L("固定转速", "Fixed RPM"),
                new FanStrategyChoice(FanControlMode.FixedRpm, 0));
            for (var index = 0; index < profiles.Count; index++)
            {
                AddChoice(
                    selector,
                    L(
                        $"风扇曲线 {index + 1}：{profiles[index].Name}",
                        $"Fan curve {index + 1}: {profiles[index].Name}"),
                    new FanStrategyChoice(
                        FanControlMode.FanCurve,
                        index));
            }
            AddChoice(
                selector,
                L("高级曲线", "Advanced curve"),
                new FanStrategyChoice(FanControlMode.AdvancedCurve, 0));
            selector.SelectionChanged += (_, _) =>
            {
                if (!_syncing)
                    SaveFanPerformanceLink();
            };
            _fanStrategyByPerformanceMode[mode] = selector;
            bindingGrid.Children.Add(SettingRow(
                ItsModeDisplayName(mode),
                string.Empty,
                selector));
        }
        _fanStrategyBindings.Children.Add(bindingGrid);
        content.Children.Add(_fanStrategyBindings);

        AddChoice(
            _fanControlTargetMode,
            L("不切换", "Do not switch"),
            ItsMode.Unknown);
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            AddChoice(
                _fanControlTargetMode,
                ItsModeDisplayName(mode),
                mode);
        }
        content.Children.Add(SettingRow(
            L("当使用风扇控制时，切换到", "When fan control is used, switch to"),
            L(
                "需要切换时，确认性能模式切换成功并等待 2 秒后才启用风扇控制；固件自动和双风扇均由固件控制的固定转速不触发。",
                "When a switch is needed, fan control starts two seconds after the performance mode is confirmed. Firmware automatic and fixed RPM with both fans firmware-controlled do not trigger it."),
            _fanControlTargetMode));

        var whitelist = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 150,
            Spacing = 8
        };
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            var toggle = new CheckBox
            {
                Content = ItsModeDisplayName(mode),
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.Click += (_, _) =>
            {
                if (!_syncing)
                    SaveFanPerformanceLink();
            };
            _fanControlNoSwitchModes[mode] = toggle;
            whitelist.Children.Add(new Border
            {
                Background = Brush(Palette.SurfaceRaised),
                BorderBrush = Brush(Palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Child = toggle
            });
        }
        _fanControlWhitelist.Children.Add(new TextBlock
        {
            Text = L("无需切换的模式", "Modes that need no switch"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(2, 4, 2, 8)
        });
        _fanControlWhitelist.Children.Add(whitelist);
        content.Children.Add(_fanControlWhitelist);

        _linkFanStrategyToPerformanceMode.Click += (_, _) =>
        {
            if (!_syncing)
                SaveFanPerformanceLink();
        };
        _fanControlTargetMode.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
                SaveFanPerformanceLink();
        };
        SyncFanPerformanceLinkControls();
        return Card(
            L("与性能模式联动", "Performance-mode linkage"),
            content,
            L(
                "两种联动方向可独立启用，并会避免循环切换。",
                "The two linkage directions can be configured independently without creating a switch loop."),
            "\uE8D4",
            "#8B7CF6");
    }

    private void SaveFanPerformanceLink()
    {
        if (_syncing)
            return;
        var draft = new PerformanceFanLinkSettings
        {
            SwitchFanStrategyWithPerformanceMode =
                _linkFanStrategyToPerformanceMode.IsChecked == true,
            FanControlTargetMode = Selected(
                _fanControlTargetMode,
                ItsMode.Unknown),
            FanStrategiesByMode =
                PerformanceFanLinkDefaults.CreateFanStrategies(),
            NoSwitchModes =
                PerformanceFanLinkDefaults.CreateNoSwitchModes()
        };
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            if (SelectedFanStrategy(
                    _fanStrategyByPerformanceMode[mode]) is { } selection)
            {
                draft.FanStrategiesByMode[mode.ToString()] = new()
                {
                    Mode = selection.Mode,
                    ProfileIndex = selection.ProfileIndex
                };
            }
            draft.NoSwitchModes[mode.ToString()] =
                _fanControlNoSwitchModes[mode].IsChecked == true;
        }
        if (!Runtime.TrySetPerformanceFanLink(draft, out var error))
        {
            _fanStatus.Text = L(
                "性能模式联动设置保存失败：",
                "Could not save performance-mode linkage: ") + error;
        }
        SyncFanPerformanceLinkControls();
    }

    private void SyncFanPerformanceLinkControls()
    {
        if (!_coolingOnly || _fanStrategyByPerformanceMode.Count == 0)
            return;
        var wasSyncing = _syncing;
        _syncing = true;
        var settings = PerformanceFanLinkDefaults.Normalize(
            Runtime.Settings.PerformanceFanLink);
        _linkFanStrategyToPerformanceMode.IsChecked =
            settings.SwitchFanStrategyWithPerformanceMode;
        _fanStrategyBindings.Visibility =
            settings.SwitchFanStrategyWithPerformanceMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            var selection = PerformanceFanLinkDefaults.SelectionFor(
                settings,
                mode);
            SelectFanStrategy(
                _fanStrategyByPerformanceMode[mode],
                new FanStrategyChoice(
                    selection.Mode,
                    selection.ProfileIndex));
        }
        Select(_fanControlTargetMode, settings.FanControlTargetMode);
        var targetSelected = settings.FanControlTargetMode != ItsMode.Unknown;
        _fanControlWhitelist.Visibility = targetSelected
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var mode in PerformanceFanLinkDefaults.SupportedModes)
        {
            var toggle = _fanControlNoSwitchModes[mode];
            toggle.IsChecked = PerformanceFanLinkDefaults.IsNoSwitchMode(
                settings,
                mode);
            toggle.IsEnabled = !targetSelected ||
                               mode != settings.FanControlTargetMode;
        }
        _syncing = wasSyncing;
    }

    private string ItsModeDisplayName(ItsMode mode) => mode switch
    {
        ItsMode.PowerSaving => L("省电模式", "Power saving"),
        ItsMode.Intelligent => L("智能模式", "Intelligent"),
        ItsMode.Performance => L("性能模式", "Performance"),
        ItsMode.Geek => L("极客模式", "Geek"),
        _ => L("不切换", "Do not switch")
    };

    private static FanStrategyChoice? SelectedFanStrategy(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: FanStrategyChoice value }
            ? value
            : null;

    private static void SelectFanStrategy(
        ComboBox combo,
        FanStrategyChoice value) =>
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                item.Tag is FanStrategyChoice choice && choice == value);

    private void PopulateFanPreferenceChoices()
    {
        foreach (var value in new[] { 1d, 2d, 3d, 5d, 10d })
        {
            AddChoice(_smoothing, $"{value:0}", value);
            AddChoice(_advancedSmoothing, $"{value:0}", value);
        }
        foreach (var value in new[] { 10d, 20d, 50d, 100d })
        {
            AddChoice(_curveRampUp, $"{value:0}", value);
            AddChoice(_curveRampDown, $"{value:0}", value);
            AddChoice(_rampDown, $"{value:0}", value);
        }
        var unlimited = L("无限制", "inf");
        AddChoice(_curveRampUp, unlimited, 0d);
        AddChoice(_curveRampDown, unlimited, 0d);
        AddChoice(_rampDown, unlimited, 0d);
        foreach (var value in new[] { 0d, 10d, 20d, 30d, 60d })
            AddChoice(_gameHold, $"{value:0}", value);
    }

    private void BuildFixedPanel()
    {
        AddChoice(_strategy, L("固件自动", "Firmware automatic"), FanControlMode.FirmwareAutomatic);
        AddChoice(_strategy, L("固定转速", "Fixed RPM"), FanControlMode.FixedRpm);
        AddChoice(_strategy, L("风扇曲线", "Fan curve"), FanControlMode.FanCurve);
        AddChoice(_strategy, L("高级曲线", "Advanced curve"), FanControlMode.AdvancedCurve);
        AddChoice(_fixedMode, L("平时", "Normal"), false);
        AddChoice(_fixedMode, L("游戏", "Game"), true);
        _fixedPanel.Children.Add(SettingRow(
            L("当前固定转速状态", "Current fixed-RPM state"),
            L("手动选择会立即生效；快捷键执行相同切换。游戏检测到达下一状态边界后会解除本次手动覆盖。", "The selection takes effect immediately; the hotkey performs the same switch. Automatic detection clears the override at the next game-state boundary."),
            _fixedMode));
        _fixedPanel.Children.Add(new TextBlock
        {
            Text = L("固定转速表", "Fixed RPM table"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(0, 10, 0, 8)
        });
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 92
        });
        for (var index = 0; index < 4; index++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 82
            });
        }
        for (var index = 0; index < 5; index++)
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddFixedHeader(table, L("模式", "Mode"), 0);
        AddFixedHeader(table, L("普通 F1", "Normal F1"), 1);
        AddFixedHeader(table, L("普通 F2", "Normal F2"), 2);
        AddFixedHeader(table, L("游戏 F1", "Game F1"), 3);
        AddFixedHeader(table, L("游戏 F2", "Game F2"), 4);
        AddFixedRow(table, 1, "PowerSaving", L("省电", "Power saving"));
        AddFixedRow(table, 2, "Intelligent", L("智能", "Intelligent"));
        AddFixedRow(table, 3, "Performance", L("性能", "Performance"));
        AddFixedRow(table, 4, "Geek", L("极客", "Geek"));
        _fixedPanel.Children.Add(new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12),
            Child = table
        });
        _syncFixed.Margin = new Thickness(0, 12, 0, 0);
        _syncFixed.Content = L("同步固定转速下的两个风扇", "Synchronize both fans for fixed RPM");
        _fixedPanel.Children.Add(_syncFixed);
        _fixedPanel.Children.Add(new TextBlock
        {
            Text = FanZeroRpmDescription(),
            Foreground = Brush(Palette.Muted),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    private void AddFixedHeader(Grid grid, string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Brush(Palette.Muted),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Center,
            Margin = new Thickness(5, 2, 5, 8)
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private void AddFixedRow(
        Grid grid,
        int row,
        string prefix,
        string title)
    {
        var block = new TextBlock
        {
            Text = title,
            Foreground = Brush(Palette.Text),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5)
        };
        Grid.SetRow(block, row);
        grid.Children.Add(block);
        AddFixedBox(grid, prefix + "NormalFan1Rpm", row, 1);
        AddFixedBox(grid, prefix + "NormalFan2Rpm", row, 2);
        AddFixedBox(grid, prefix + "GameFan1Rpm", row, 3);
        AddFixedBox(grid, prefix + "GameFan2Rpm", row, 4);
    }

    private void AddFixedBox(Grid grid, string key, int row, int column)
    {
        var box = new TextBox { Width = 76, Margin = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center };
        box.TextChanged += (_, _) => OnFixedBoxChanged(key, box.Text);
        _fixedBoxes[key] = box;
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column);
        grid.Children.Add(box);
    }

    private void OnFixedBoxChanged(string key, string text)
    {
        if (_syncing) return;
        if (_syncFixed.IsChecked == true)
        {
            var otherKey = key.Contains("Fan1Rpm", StringComparison.Ordinal)
                ? key.Replace("Fan1Rpm", "Fan2Rpm", StringComparison.Ordinal)
                : key.Replace("Fan2Rpm", "Fan1Rpm", StringComparison.Ordinal);
            if (_fixedBoxes.TryGetValue(otherKey, out var other) && other.Text != text)
            {
                _syncing = true;
                other.Text = text;
                _syncing = false;
            }
        }
        MarkFanDirty();
    }

    private void CopyFixedDraftFan1ToFan2()
    {
        _syncing = true;
        foreach (var key in _fixedBoxes.Keys.Where(key => key.Contains("Fan1Rpm", StringComparison.Ordinal)).ToArray())
        {
            var otherKey = key.Replace("Fan1Rpm", "Fan2Rpm", StringComparison.Ordinal);
            if (_fixedBoxes.TryGetValue(otherKey, out var other))
                other.Text = _fixedBoxes[key].Text;
        }
        _syncing = false;
    }

    private void BuildCurvePanel()
    {
        AddChoice(_editFan, "Fan 1", 1);
        AddChoice(_editFan, "Fan 2", 2);
        var identityContent = new Grid();
        identityContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        identityContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1)
        });
        identityContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var profile = CompactSettingContent(L("方案", "Profile"), _profile);
        profile.Margin = new Thickness(12, 9, 12, 9);
        var divider = new Border { Background = Brush(Palette.Border) };
        Grid.SetColumn(divider, 1);
        var name = CompactSettingContent(L("名称", "Name"), _profileName);
        name.Margin = new Thickness(12, 9, 12, 9);
        Grid.SetColumn(name, 2);
        identityContent.Children.Add(profile);
        identityContent.Children.Add(divider);
        identityContent.Children.Add(name);
        var identity = new Border
        {
            Tag = "FanCurveProfileAndName",
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            ClipToBounds = true,
            Child = identityContent
        };
        _curvePanel.Children.Add(identity);

        var limits = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 220,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        limits.Children.Add(CompactSetting(L("温度平滑（秒）", "Temperature smoothing (s)"), _smoothing));
        limits.Children.Add(CompactSetting(L("升速限制（RPM/s）", "Ramp-up limit (RPM/s)"), _curveRampUp));
        limits.Children.Add(CompactSetting(L("降速限制（RPM/s）", "Ramp-down limit (RPM/s)"), _curveRampDown));
        limits.Children.Add(CompactSetting(L("高温后降速限制（RPM/s）", "Post-high-temperature ramp-down limit (RPM/s)"), _rampDown));
        _curvePanel.Children.Add(limits);

        var editRow = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        editRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _independentCurves.Content = L(
            "独立控制两个风扇的曲线",
            "Control both fan curves independently");
        _independentCurves.VerticalAlignment = VerticalAlignment.Center;
        editRow.Children.Add(_independentCurves);
        var editFan = CompactSetting(
            L("选择要编辑的风扇", "Select fan to edit"),
            _editFan);
        editFan.MinWidth = 330;
        editFan.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(editFan, 1);
        editRow.Children.Add(editFan);
        _curvePanel.Children.Add(editRow);
        _curvePanel.Children.Add(_cpuCurve);
        _gpuCurve.Margin = new Thickness(0, 10, 0, 0);
        _curvePanel.Children.Add(_gpuCurve);
    }

    private void BuildAdvancedCurvePanel()
    {
        var general = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 250,
            Spacing = 8
        };
        general.Children.Add(CompactSetting(
            L("温度平滑（秒）", "Temperature smoothing (s)"),
            _advancedSmoothing));
        _advancedCurvePanel.Children.Add(general);
        _advancedCurve.Margin = new Thickness(0, 10, 0, 0);
        _advancedCurvePanel.Children.Add(_advancedCurve);
        _advancedCurvePanel.Children.Add(new TextBlock
        {
            Text = FanZeroRpmDescription(),
            Foreground = Brush(Palette.Muted),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    private Border CompactSetting(string label, UIElement control)
    {
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 9, 12, 9),
            Child = CompactSettingContent(label, control)
        };
    }

    private Grid CompactSettingContent(string label, UIElement control)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        panel.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0)
        });
        Grid.SetColumn(control, 1);
        panel.Children.Add(control);
        return panel;
    }

    private UIElement BuildPowerCard()
    {
        var profile = PowerSettingsController.CurrentProfile;
        var panel = new StackPanel();
        var current = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 180,
            Spacing = 8
        };
        AddPowerReadout(current, "CpuPl1", PowerSetting.CpuPl1, "CPU PL1", "W");
        AddPowerReadout(current, "CpuPl2", PowerSetting.CpuPl2, "CPU PL2", "W");
        AddPowerReadout(current, "CpuTemperature", PowerSetting.CpuTemperatureLimit, L("CPU 温度上限", "CPU temperature limit"), "°C");
        AddPowerReadout(current, "TurboTime", PowerSetting.CpuTurboTimeLimit, "CPU Turbo Time Limit", "s");
        AddPowerReadout(current, "GpuBoost", PowerSetting.GpuPowerBoost, "GPU Power Boost", "W");
        AddPowerReadout(current, "GpuTgp", PowerSetting.GpuConfigurableTgp, profile.Writable ? "GPU TGP" : "GPU Configurable TGP", "W");
        AddPowerReadout(current, "GpuTemperature", PowerSetting.GpuTemperatureLimit, L("GPU 温度上限", "GPU temperature limit"), "°C");
        AddPowerReadout(current, "GpuToCpu", PowerSetting.GpuToCpuDynamicBoost, "GPU to CPU Dynamic Boost", "W");
        _atppReadout = AddPowerReadout(current, "Atpp", PowerSetting.Atpp, "ATPP", "W");
        _atppReadout.Visibility = Visibility.Collapsed;
        panel.Children.Add(current);
        panel.Children.Add(_powerStatus);

        if (profile.Writable)
        {
            BuildPowerEditorPanel();
            var adjustment = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = "\uE70F",
                FontFamily = new System.Windows.Media.FontFamily(
                    "Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brush(Palette.Accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = L("参数调整", "Parameter adjustment"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Palette.Text)
            });
            labels.Children.Add(new TextBlock
            {
                Text = L(
                    "默认折叠；展开时以刚刚读取的全部当前值作为初始值。",
                    "Collapsed by default; expanding seeds all controls from the current values."),
                FontSize = 12,
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 14, 0)
            });
            Grid.SetColumn(labels, 1);
            header.Children.Add(labels);
            Grid.SetColumn(_togglePowerEditor, 2);
            header.Children.Add(_togglePowerEditor);
            adjustment.Children.Add(header);
            _powerEditorPanel.Visibility = Visibility.Collapsed;
            _powerEditorPanel.Margin = new Thickness(0, 12, 0, 0);
            adjustment.Children.Add(_powerEditorPanel);
            _powerEditorHost.Background = Brush(Palette.SurfaceRaised);
            _powerEditorHost.BorderBrush = Brush(Palette.Border);
            _powerEditorHost.BorderThickness = new Thickness(1);
            _powerEditorHost.CornerRadius = new CornerRadius(14);
            _powerEditorHost.Padding = new Thickness(12);
            _powerEditorHost.Margin = new Thickness(0, 10, 0, 0);
            _powerEditorHost.Child = adjustment;
            panel.Children.Add(_powerEditorHost);
        }
        else
        {
            panel.Children.Add(new Border
            {
                Background = Tint(Palette.Warning),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = L(
                        "当前设备支持查看可读取的参数，但不支持由 Toolkit 修改。",
                        "This device can show readable values, but Toolkit cannot change them."),
                    Foreground = Brush(Palette.Warning),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
        return Card(
            L("功耗设置", "Power settings"),
            panel,
            L("先读取并显示全部可用参数；仅支持机型提供折叠式写入区域。", "All available values are read first; only supported models expose the collapsible editor."),
            "\uE945",
            "#F2A65A");
    }

    private void BuildPowerEditorPanel()
    {
        var profile = PowerSettingsController.CurrentProfile;
        PowerSettingRule Rule(PowerSetting setting) => profile.Rules[setting];
        var rule = Rule(PowerSetting.CpuPl1);
        AddPowerEditor(_powerEditorPanel, "CpuPl1", PowerSetting.CpuPl1, "CPU PL1", rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        rule = Rule(PowerSetting.CpuPl2);
        AddPowerEditor(_powerEditorPanel, "CpuPl2", PowerSetting.CpuPl2, "CPU PL2", rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        rule = Rule(PowerSetting.CpuTemperatureLimit);
        AddPowerEditor(_powerEditorPanel, "CpuTemperature", PowerSetting.CpuTemperatureLimit, L("CPU 温度上限", "CPU temperature limit"), rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        foreach (var value in PowerSettingsController.TurboTimeLimits)
            AddChoice(_turboTime, $"{value}", value);
        var turboRow = SettingRow(
            "CPU Turbo Time Limit",
            L("选择固件支持的持续时间。", "Choose a firmware-supported duration."),
            PowerLockControl(PowerSetting.CpuTurboTimeLimit, _turboTime));
        _powerEditorRows[PowerSetting.CpuTurboTimeLimit] = turboRow;
        _powerEditorPanel.Children.Add(turboRow);
        rule = Rule(PowerSetting.GpuPowerBoost);
        AddPowerEditor(_powerEditorPanel, "GpuBoost", PowerSetting.GpuPowerBoost, "GPU Power Boost", rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        rule = Rule(PowerSetting.GpuConfigurableTgp);
        AddPowerEditor(_powerEditorPanel, "GpuTgp", PowerSetting.GpuConfigurableTgp, "GPU TGP", rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        if (profile.Rules.TryGetValue(PowerSetting.GpuTemperatureLimit, out rule))
            AddPowerEditor(_powerEditorPanel, "GpuTemperature", PowerSetting.GpuTemperatureLimit, L("GPU 温度上限", "GPU temperature limit"), rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        rule = Rule(PowerSetting.GpuToCpuDynamicBoost);
        AddPowerEditor(_powerEditorPanel, "GpuToCpu", PowerSetting.GpuToCpuDynamicBoost, "GPU to CPU Dynamic Boost", rule.SliderMinimum, rule.SliderMaximum, rule.ManualMinimum);
        if (profile.Rules.TryGetValue(PowerSetting.Atpp, out rule))
        _atppEditorRow = AddPowerEditor(
            _powerEditorPanel,
            "Atpp",
            PowerSetting.Atpp,
            "ATPP",
            rule.SliderMinimum,
            rule.SliderMaximum,
            rule.ManualMinimum);
        if (_atppEditorRow is not null)
            _atppEditorRow.Visibility = Visibility.Collapsed;
        foreach (var value in PowerSettingsController.LockIntervals)
        {
            AddChoice(
                _powerLockInterval,
                Runtime.IsChinese ? $"{value} 秒" : $"{value} s",
                value);
        }
        var footer = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        buttons.Children.Add(_applyPower);
        buttons.Children.Add(_discardPower);
        buttons.Children.Add(_defaultPower);
        _defaultPower.Visibility = profile.SupportsDefaults
            ? Visibility.Visible
            : Visibility.Collapsed;
        footer.Children.Add(buttons);

        var lockControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 0, 0, 0)
        };
        lockControls.Children.Add(new TextBlock
        {
            Text = L("锁定检查间隔", "Lock check interval"),
            Foreground = Brush(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        lockControls.Children.Add(_powerLockInterval);
        Grid.SetColumn(lockControls, 1);
        footer.Children.Add(lockControls);

        void ApplyResponsiveFooter(bool compact)
        {
            Grid.SetRow(lockControls, compact ? 1 : 0);
            Grid.SetColumn(lockControls, compact ? 0 : 1);
            Grid.SetColumnSpan(lockControls, compact ? 2 : 1);
            lockControls.HorizontalAlignment = compact
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            lockControls.Margin = compact
                ? new Thickness(0, 10, 0, 0)
                : new Thickness(18, 0, 0, 0);
        }
        footer.SizeChanged += (_, _) =>
            ApplyResponsiveFooter(footer.ActualWidth < 900);
        ApplyResponsiveFooter(compact: false);
        _powerEditorPanel.Children.Add(footer);
        SyncPowerLockControls();
    }

    private Border AddPowerReadout(
        Panel panel,
        string key,
        PowerSetting setting,
        string title,
        string unit)
    {
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            Margin = new Thickness(0, 7, 0, 0)
        };
        _powerReadouts[key] = value;
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(Palette.Muted),
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(value);
        content.Children.Add(new TextBlock
        {
            Text = unit,
            Foreground = Brush(Palette.Muted),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var card = new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Child = content
        };
        panel.Children.Add(card);
        _powerReadoutRows[setting] = card;
        return card;
    }

    private Border AddPowerEditor(
        Panel panel,
        string key,
        PowerSetting setting,
        string title,
        int minimum,
        int maximum,
        int? manualMinimum = null)
    {
        var editor = new PowerIntegerEditor(minimum, maximum, manualMinimum);
        editor.Changed += MarkPowerDirty;
        _powerEditors[key] = editor;
        var row = SettingRow(
            title,
            manualMinimum.HasValue
                ? manualMinimum == 0
                    ? L($"滑块范围 {minimum}–{maximum}；手动输入可超出，但必须是非负整数。", $"Slider range {minimum}–{maximum}; manual input may exceed it but must be non-negative.")
                    : L($"滑块范围 {minimum}–{maximum}；手动输入可超出，但必须是正整数。", $"Slider range {minimum}–{maximum}; manual input may exceed it but must be positive.")
                : L($"允许范围 {minimum}–{maximum}。", $"Allowed range: {minimum}–{maximum}."),
            PowerLockControl(setting, editor.View));
        panel.Children.Add(row);
        _powerEditorRows[setting] = row;
        return row;
    }

    private FrameworkElement PowerLockControl(
        PowerSetting setting,
        UIElement editor)
    {
        var toggle = new CheckBox
        {
            Content = L("锁定", "Lock"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
            ToolTip = L(
                "定期检查并只恢复这一项。",
                "Periodically check and restore only this value.")
        };
        toggle.Click += (_, _) => ChangePowerSettingLock(setting);
        _powerLockToggles[setting] = toggle;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        layout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        layout.Children.Add(editor);
        Grid.SetColumn(toggle, 1);
        layout.Children.Add(toggle);
        return layout;
    }

    private void ConfigureCurves()
    {
        foreach (var curve in new[] { _cpuCurve, _gpuCurve })
        {
            curve.SetTheme(Runtime.IsDark);
            curve.SetFontFamily(UiTypography.FontFamilyNameFor(Runtime.Settings.Language));
        }
        _cpuCurve.SetLabels(L("CPU 风扇曲线", "CPU fan curve"), L("CPU 温度（°C）", "CPU temperature (°C)"));
        _gpuCurve.SetLabels(L("GPU 风扇曲线", "GPU fan curve"), L("GPU 温度（°C）", "GPU temperature (°C)"));
    }

    private void WireEvents()
    {
        _itsMode.SelectionChanged += async (_, _) =>
        {
            if (_syncing || Selected<ItsMode>(_itsMode) is not { } mode) return;
            SetModeControls(false);
            var error = await Runtime.SetItsModeAsync(mode);
            _modeStatus.Text = error ?? string.Empty;
            SetModeControls(true);
            SyncRuntimeControls();
        };
        _gpuMode.SelectionChanged += async (_, _) =>
        {
            if (_syncing || Selected<GpuWorkingMode>(_gpuMode) is not { } mode) return;
            SetModeControls(false);
            var error = await Runtime.SetGpuModeAsync(mode);
            _modeStatus.Text = error ?? string.Empty;
            SetModeControls(true);
            SyncRuntimeControls();
        };
        _performanceModeOrderSettings.Click += (_, _) =>
        {
            var window = new PerformanceModeOrderWindow(
                Window.GetWindow(this),
                Runtime,
                FontFamily,
                FontSize);
            window.ShowDialog();
        };
        _viewGpuApplications.Click += async (_, _) =>
            await ShowGpuApplicationsAsync();
        _killGpuApplications.Click += async (_, _) =>
            await KillGpuApplicationsAsync();
        _gpuOverclockSettings.Click += (_, _) =>
        {
            var window = new GpuOverclockWindow(
                Window.GetWindow(this),
                Runtime,
                FontFamily,
                FontSize);
            window.ShowDialog();
            SyncRuntimeControls();
        };
        _gpuOverclockEnabled.Click += async (_, _) =>
        {
            if (_syncing)
                return;
            var enabled = _gpuOverclockEnabled.IsChecked == true;
            _gpuOverclockEnabled.IsEnabled = false;
            var error = await Runtime.SetGpuOverclockEnabledAsync(enabled);
            _modeStatus.Text = error ?? string.Empty;
            SyncRuntimeControls();
        };
        _restartNow.Click += async (_, _) =>
        {
            if (MessageBox.Show(
                    Window.GetWindow(this),
                    L("将先恢复固件自动风扇控制，然后立即重新启动 Windows。是否继续？", "Toolkit will restore firmware-automatic fan control and restart Windows immediately. Continue?"),
                    "ThinkBook Toolkit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
            _restartNow.IsEnabled = false;
            if (Runtime.FanRuntime is not null &&
                (Runtime.Snapshot.FanControlRunning || Runtime.Snapshot.FullSpeed))
            {
                var restoreError = await Runtime.RestoreFirmwareAutoAsync();
                if (!string.IsNullOrWhiteSpace(restoreError))
                {
                    _modeStatus.Text = L("重启已取消，因为恢复固件自动风扇控制失败：", "Restart cancelled because firmware-automatic fan control could not be restored: ") + restoreError;
                    _restartNow.IsEnabled = true;
                    return;
                }
            }
            try
            {
                _modeStatus.Text = L("正在重新启动……", "Restarting…");
                BiosAdvancedController.RestartComputer();
            }
            catch (Exception ex)
            {
                _modeStatus.Text = L("无法启动重启：", "Could not start the restart: ") + ex.Message;
                _restartNow.IsEnabled = true;
            }
        };
        _fullSpeed.Click += async (_, _) =>
        {
            if (_syncing) return;
            var error = await Runtime.SetFullSpeedAsync(_fullSpeed.IsChecked == true);
            _fanStatus.Text = error ?? string.Empty;
            SyncRuntimeControls();
        };
        _strategy.SelectionChanged += async (_, _) =>
        {
            if (_syncing ||
                Selected<FanControlMode>(_strategy) is not { } mode)
                return;
            await ChangeFanControlModeAsync(mode);
        };
        _fixedMode.SelectionChanged += async (_, _) =>
        {
            if (_syncing || Selected<bool>(_fixedMode) is not { } gameMode || Runtime.FanRuntime is null)
                return;
            Runtime.FanRuntime.RuntimeSetManualFixedMode(gameMode);
            await Runtime.RefreshAsync(force: true);
            _fanStatus.Text = string.Empty;
            SyncRuntimeControls();
        };
        _profile.SelectionChanged += async (_, _) =>
        {
            if (_syncing || _profile.SelectedIndex < 0) return;
            var index = _profile.SelectedIndex;
            LoadProfileDraft(index);
            _fanStatus.Text =
                await Runtime.SelectFanProfileAsync(index) ??
                string.Empty;
        };
        _profileName.TextChanged += (_, _) =>
        {
            if (!_syncing && _draftProfile is not null)
            {
                _draftProfile.Name = _profileName.Text;
                MarkFanDirty();
            }
        };
        _smoothing.SelectionChanged += (_, _) => MarkFanDirty();
        _curveRampUp.SelectionChanged += (_, _) => MarkFanDirty();
        _curveRampDown.SelectionChanged += (_, _) => MarkFanDirty();
        _rampDown.SelectionChanged += (_, _) => MarkFanDirty();
        _advancedSmoothing.SelectionChanged += (_, _) => MarkFanDirty();
        _advancedCurve.ValuesChanged += (_, _) => MarkFanDirty();
        _gameHold.SelectionChanged += (_, _) => MarkFanDirty();
        _hotkey.TextChanged += (_, _) => MarkFanDirty();
        _independentCurves.Click += (_, _) =>
        {
            if (_syncing) return;
            var synchronize = _independentCurves.IsChecked != true;
            _cpuCurve.SetSyncFanSpeeds(synchronize);
            _gpuCurve.SetSyncFanSpeeds(synchronize);
            MarkFanDirty();
        };
        _syncFixed.Click += (_, _) =>
        {
            if (_syncing) return;
            if (_syncFixed.IsChecked == true)
                CopyFixedDraftFan1ToFan2();
            MarkFanDirty();
        };
        _autoGames.Click += (_, _) => MarkFanDirty();
        _editFan.SelectionChanged += (_, _) =>
        {
            if (_syncing || Selected<int>(_editFan) is not { } fan) return;
            _cpuCurve.SetEditFan(fan);
            _gpuCurve.SetEditFan(fan);
            MarkFanDirty();
        };
        _cpuCurve.ValuesChanged += (fan1, fan2) =>
        {
            if (_draftProfile is null) return;
            _draftProfile.CpuFan1Curve = fan1;
            _draftProfile.CpuFan2Curve = fan2;
            MarkFanDirty();
        };
        _gpuCurve.ValuesChanged += (fan1, fan2) =>
        {
            if (_draftProfile is null) return;
            _draftProfile.GpuFan1Curve = fan1;
            _draftProfile.GpuFan2Curve = fan2;
            MarkFanDirty();
        };
        _applyFan.Click += async (_, _) => await ApplyFanDraftAsync();
        _discardFan.Click += (_, _) => ReloadFanDrafts();
        _fanLimitsButton.Click += async (_, _) => await ShowFanLimitsAsync();
        _turboTime.SelectionChanged += (_, _) => MarkPowerDirty();
        _applyPower.Click += async (_, _) => await ApplyPowerAsync();
        _discardPower.Click += (_, _) => ApplyPowerState(_confirmedPower);
        _powerLockInterval.SelectionChanged += (_, _) =>
            ChangePowerSettingsLockInterval();
        _defaultPower.Click += (_, _) =>
        {
            var defaults = PowerSettingsController.GetDefaultState(Runtime.Snapshot.ItsMode);
            if (defaults is null)
            {
                _powerStatus.Text = L("无法确定当前性能模式，不能载入默认值。", "The current performance mode is unknown, so defaults cannot be loaded.");
                return;
            }
            if (_confirmedPower is { } confirmed)
                defaults = defaults with
                {
                    Atpp = confirmed.IsAvailable(PowerSetting.Atpp)
                        ? defaults.Atpp
                        : null,
                    AvailableSettings = confirmed.AvailableSettings
                };
            ApplyPowerState(defaults, confirmed: false);
            MarkPowerDirty();
        };
        _togglePowerEditor.Click += (_, _) =>
        {
            _powerEditorExpanded = !_powerEditorExpanded;
            if (_powerEditorExpanded)
                ApplyPowerState(_confirmedPower);
            _powerEditorPanel.Visibility = _powerEditorExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
            _togglePowerEditor.Content = _powerEditorExpanded
                ? L("收起设置", "Collapse settings")
                : L("展开设置", "Expand settings");
        };
    }

    private async Task LoadAsync()
    {
        if (_coolingOnly)
            ReloadFanDrafts();
        SyncRuntimeControls();
        if (!_coolingOnly &&
            Runtime.Report?.IsAvailable(FeatureIds.PowerSettings) == true)
            await LoadPowerAsync();
    }

    private async Task ShowGpuApplicationsAsync()
    {
        _viewGpuApplications.IsEnabled = false;
        _killGpuApplications.IsEnabled = false;
        try
        {
            var result = await Runtime.QueryDiscreteGpuApplicationsAsync();
            if (!result.Success)
            {
                _modeStatus.Text = L(
                    "无法读取独显占用应用：",
                    "Could not read applications using the discrete GPU: ") +
                    result.Error;
                return;
            }
            var window = new GpuApplicationsWindow(
                Window.GetWindow(this),
                result.Applications,
                Runtime.IsChinese,
                Runtime.IsDark,
                FontFamily,
                FontSize);
            window.ShowDialog();
            _modeStatus.Text = string.Empty;
        }
        finally
        {
            SyncRuntimeControls();
        }
    }

    private async Task KillGpuApplicationsAsync()
    {
        if (MessageBox.Show(
                Window.GetWindow(this),
                L(
                    "将强制结束当前占用独立显卡的所有非系统应用及其子进程。未保存的数据可能丢失。是否继续？",
                    "All non-system applications currently using the discrete GPU and their child processes will be terminated. Unsaved data may be lost. Continue?"),
                "ThinkBook Toolkit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _viewGpuApplications.IsEnabled = false;
        _killGpuApplications.IsEnabled = false;
        try
        {
            var result = await Runtime.KillDiscreteGpuApplicationsAsync();
            var message = result.Success
                ? L(
                    $"已关闭 {result.AffectedProcesses} 个占用独显的应用进程。",
                    $"Closed {result.AffectedProcesses} application processes using the discrete GPU.")
                : L(
                    $"已关闭 {result.AffectedProcesses} 个进程，但部分应用关闭失败：",
                    $"Closed {result.AffectedProcesses} processes, but some applications could not be closed: ") +
                  result.Error;
            _modeStatus.Text = string.Empty;
            Runtime.SetStatus(message);
        }
        finally
        {
            SyncRuntimeControls();
        }
    }

    private async Task ChangeFanControlModeAsync(FanControlMode mode)
    {
        if (Runtime.FanRuntime is null)
            return;
        _strategy.IsEnabled = false;
        try
        {
            var error = await Runtime.SetFanModeAsync(mode);
            _fanStatus.Text = error ?? string.Empty;
        }
        finally
        {
            SyncRuntimeControls();
            _strategy.IsEnabled = true;
        }
    }

    private async Task ShowFanLimitsAsync()
    {
        var current = Runtime.FanRuntime?.RuntimeSnapshot().FanRpmLimits ??
                      CurveProfileStore.NormalizeFanRpmLimits(
                          Runtime.Settings.FanRpmLimits);
        var dialog = new FanRpmLimitsWindow(
            Window.GetWindow(this),
            current,
            Runtime.FanControlSemantics,
            Runtime.IsChinese,
            Runtime.IsDark,
            FontFamily,
            FontSize);
        if (dialog.ShowDialog() != true || dialog.Limits is null)
            return;

        _fanLimitsButton.IsEnabled = false;
        var hadUnsavedDraft = _fanDirty;
        try
        {
            var error = await Runtime.SetFanRpmLimitsAsync(dialog.Limits);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _fanStatus.Text = L(
                    "保存转速上下限失败：",
                    "Could not save RPM limits: ") + error;
                return;
            }

            var limits = CurveProfileStore.NormalizeFanRpmLimits(
                dialog.Limits);
            _cpuCurve.SetRpmRanges(
                limits.Fan1MinimumRpm,
                limits.Fan1MaximumRpm,
                limits.Fan2MinimumRpm,
                limits.Fan2MaximumRpm);
            _gpuCurve.SetRpmRanges(
                limits.Fan1MinimumRpm,
                limits.Fan1MaximumRpm,
                limits.Fan2MinimumRpm,
                limits.Fan2MaximumRpm);

            if (hadUnsavedDraft && _draftProfile is not null)
            {
                _draftProfile.CpuFan1Curve = [.. _cpuCurve.Fan1Values];
                _draftProfile.CpuFan2Curve = [.. _cpuCurve.Fan2Values];
                _draftProfile.GpuFan1Curve = [.. _gpuCurve.Fan1Values];
                _draftProfile.GpuFan2Curve = [.. _gpuCurve.Fan2Values];
                ClampFixedDraftToLimits(limits);
                MarkFanDirty();
            }
            else
            {
                ReloadFanDrafts();
            }

            _fanStatus.Text = string.Empty;
        }
        finally
        {
            _fanLimitsButton.IsEnabled = !Runtime.Snapshot.FullSpeed;
        }
    }

    private string FanFullSpeedDescription()
        => L(
            "开启后风扇以最高转速运行；关闭后恢复此前控制状态。",
            "Run the fans at full speed; disabling it restores the previous control state.");

    private string FanZeroRpmDescription()
    {
        var semantics = Runtime.FanControlSemantics;
        return semantics.ZeroRpmBehavior ==
               FanTargetZeroBehavior.StopFanWhileKeepingManualControl
            ? L(
                "写入 0 会保持手动控制并关闭对应风扇，不会恢复自动；如需恢复，请切换到“固件自动”。非零值必须位于配置的风扇转速范围内。",
                "Writing 0 keeps manual control active and stops that fan; it does not restore automatic control. Select Firmware automatic to restore it. Non-zero values must stay within the configured fan range.")
            : L(
                "写入 0 会将对应风扇交还固件控制；切换到“固件自动”会恢复全部风扇的自动控制。非零值必须位于配置的风扇转速范围内。",
                "Writing 0 returns that fan to firmware control. Select Firmware automatic to restore automatic control for all fans. Non-zero values must stay within the configured fan range.");
    }

    private void ClampFixedDraftToLimits(FanRpmLimits limits)
    {
        _syncing = true;
        foreach (var pair in _fixedBoxes)
        {
            if (!int.TryParse(
                    pair.Value.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }
            var fan2 = pair.Key.Contains("Fan2Rpm", StringComparison.Ordinal);
            var minimum = fan2
                ? limits.Fan2MinimumRpm
                : limits.Fan1MinimumRpm;
            var maximum = fan2
                ? limits.Fan2MaximumRpm
                : limits.Fan1MaximumRpm;
            pair.Value.Text = CurveProfileStore
                .ClampFixedRpm(value, minimum, maximum)
                .ToString(CultureInfo.InvariantCulture);
        }
        _syncing = false;
    }

    private void ReloadFanDrafts()
    {
        if (!_fanControlsBuilt)
            return;

        _profiles = (Runtime.FanRuntime?.RuntimeProfiles() ?? CurveProfileStore.Load()).Select(CloneProfile).ToList();
        _syncing = true;
        _profile.Items.Clear();
        for (var i = 0; i < _profiles.Count; i++)
            _profile.Items.Add(new ComboBoxItem { Content = $"{i + 1}: {_profiles[i].Name}", Tag = i });
        var index = Math.Clamp(Runtime.FanRuntime?.RuntimeSnapshot().ProfileIndex ?? Runtime.Settings.LastProfileIndex, 0, Math.Max(0, _profiles.Count - 1));
        _profile.SelectedIndex = index;
        LoadProfileDraft(index, keepSyncing: true);
        var settings = Runtime.Settings;
        _independentCurves.IsChecked = !settings.SyncFanSpeeds;
        _cpuCurve.SetSyncFanSpeeds(settings.SyncFanSpeeds);
        _gpuCurve.SetSyncFanSpeeds(settings.SyncFanSpeeds);
        _syncFixed.IsChecked = settings.FixedSyncFanSpeeds;
        _autoGames.IsChecked = settings.AutoDetectGames;
        Select(_gameHold, settings.GameExitHoldSeconds);
        _hotkey.Text = settings.FixedModeHotkey;
        Select(_fixedMode, Runtime.FanRuntime?.RuntimeSnapshot().EffectiveGameMode ?? settings.ManualGameMode);
        Select(_editFan, settings.EditFan);
        PopulateFixed(settings.FixedRpm);
        Select(
            _advancedSmoothing,
            settings.AdvancedFanCurve.TemperatureSmoothing);
        _advancedCurve.SetValues(settings.AdvancedFanCurve.Points);
        _syncing = false;
        _fanDirty = false;
        UpdateFanDraftStatus();
    }

    private void LoadProfileDraft(int index, bool keepSyncing = false)
    {
        if (index < 0 || index >= _profiles.Count) return;
        var prior = _syncing;
        _syncing = true;
        _draftProfile = CloneProfile(_profiles[index]);
        _profile.SelectedIndex = index;
        _profileName.Text = _draftProfile.Name;
        Select(_smoothing, _draftProfile.TemperatureSmoothing);
        Select(_curveRampUp, _draftProfile.RampUpRpmPerSecond);
        Select(
            _curveRampDown,
            _draftProfile.FullRangeRampDownRpmPerSecond);
        Select(_rampDown, _draftProfile.RampDownRpmPerSecond);
        _cpuCurve.SetValues(_draftProfile.CpuFan1Curve, _draftProfile.CpuFan2Curve);
        _gpuCurve.SetValues(_draftProfile.GpuFan1Curve, _draftProfile.GpuFan2Curve);
        _fanDirty = false;
        if (!keepSyncing) _syncing = prior;
        UpdateFanDraftStatus();
    }

    private async Task ApplyFanDraftAsync()
    {
        if (Runtime.FanRuntime is null || _draftProfile is null) return;
        if (Runtime.Snapshot.FullSpeed)
        {
            _fanStatus.Text = L(
                "应用设置前请先关闭风扇拉满。",
                "Turn off full fan speed before applying settings.");
            return;
        }
        if (Selected<double>(_smoothing) is not { } smoothing ||
            Selected<double>(_curveRampUp) is not { } curveRampUp ||
            Selected<double>(_curveRampDown) is not { } curveRampDown ||
            Selected<double>(_rampDown) is not { } rampDown ||
            Selected<double>(_advancedSmoothing) is not { } advancedSmoothing ||
            Selected<double>(_gameHold) is not { } hold)
        {
            _fanStatus.Text = L("请选择有效的温度平滑、升降速限制和保持时间。", "Select valid smoothing, rate-limit, and hold-time values.");
            return;
        }
        var fanRpmLimits = Runtime.FanRuntime.RuntimeSnapshot().FanRpmLimits;
        if (!_advancedCurve.TryGetSettings(
                advancedSmoothing,
                fanRpmLimits,
                out var advancedFanCurve,
                out var advancedError))
        {
            _fanStatus.Text = advancedError;
            return;
        }
        if (!MainWindow.RuntimeIsValidHotkey(_hotkey.Text))
        {
            _fanStatus.Text = L("快捷键格式无效。请使用类似 Ctrl+Alt+F 的组合，或留空关闭。", "Invalid hotkey. Use a combination such as Ctrl+Alt+F, or leave it blank.");
            return;
        }
        if (!TryCollectFixed(out var fixedRpm, out var error))
        {
            _fanStatus.Text = error;
            return;
        }
        _applyFan.IsEnabled = false;
        var resumeControl = Runtime.Snapshot.FanControlRunning;
        var resumeStrategy = Runtime.Snapshot.FanStrategy;
        var releasedControl = false;
        try
        {
            if (resumeControl)
            {
                var restoreError = await Runtime.RestoreFirmwareAutoAsync();
                if (!string.IsNullOrWhiteSpace(restoreError))
                    throw new InvalidOperationException(restoreError);
                releasedControl = true;
            }
            _draftProfile.TemperatureSmoothing = smoothing;
            _draftProfile.RampUpRpmPerSecond = curveRampUp;
            _draftProfile.FullRangeRampDownRpmPerSecond = curveRampDown;
            _draftProfile.RampDownRpmPerSecond = rampDown;
            _draftProfile.CpuCurve = [.. _draftProfile.CpuFan1Curve];
            _draftProfile.GpuCurve = [.. _draftProfile.GpuFan1Curve];
            Runtime.FanRuntime.RuntimeApplyFanConfiguration(
                _profile.SelectedIndex,
                _draftProfile,
                _independentCurves.IsChecked != true,
                _syncFixed.IsChecked == true,
                _autoGames.IsChecked == true,
                hold,
                Selected(_editFan, 1),
                _hotkey.Text,
                fixedRpm,
                advancedFanCurve);
            await Runtime.RefreshAsync(force: true);
            _fanStatus.Text = string.Empty;
            ReloadFanDrafts();
        }
        catch (Exception ex)
        {
            _fanStatus.Text = L("应用失败，已尝试恢复原配置：", "Apply failed; the previous configuration was restored: ") + ex.Message;
            ReloadFanDrafts();
        }
        finally
        {
            if (releasedControl)
            {
                if (Runtime.FanRuntime.RuntimeSetStrategy(resumeStrategy))
                {
                    var resumeError = await Runtime.SetFanControlAsync(true);
                    if (!string.IsNullOrWhiteSpace(resumeError))
                    {
                        _fanStatus.Text += L(
                            "；恢复原控制策略失败：",
                            "; restoring the active strategy failed: ") +
                            resumeError;
                    }
                }
                else
                {
                    _fanStatus.Text += L(
                        "；恢复原控制策略失败。",
                        "; restoring the active strategy failed.");
                }
            }
            _applyFan.IsEnabled = true;
        }
    }

    private bool TryCollectFixed(out FixedRpmSettings value, out string error)
    {
        var limits = Runtime.FanRuntime?.RuntimeSnapshot().FanRpmLimits ??
                     CurveProfileStore.NormalizeFanRpmLimits(
                         Runtime.Settings.FanRpmLimits);
        var values = new Dictionary<string, int>();
        foreach (var pair in _fixedBoxes)
        {
            var fan2 = pair.Key.Contains("Fan2Rpm", StringComparison.Ordinal);
            var minimum = fan2
                ? limits.Fan2MinimumRpm
                : limits.Fan1MinimumRpm;
            var maximum = fan2
                ? limits.Fan2MaximumRpm
                : limits.Fan1MaximumRpm;
            if (!int.TryParse(pair.Value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm) ||
                (rpm != 0 && (rpm < minimum || rpm > maximum)))
            {
                value = new FixedRpmSettings();
                error = L(
                    $"固定转速“{pair.Key}”必须为 0，或位于 {minimum}–{maximum} RPM。",
                    $"Fixed RPM '{pair.Key}' must be 0 or within {minimum}–{maximum} RPM.");
                return false;
            }
            values[pair.Key] = rpm;
        }
        value = new FixedRpmSettings
        {
            PowerSavingNormalFan1Rpm = values["PowerSavingNormalFan1Rpm"],
            PowerSavingNormalFan2Rpm = values["PowerSavingNormalFan2Rpm"],
            PowerSavingGameFan1Rpm = values["PowerSavingGameFan1Rpm"],
            PowerSavingGameFan2Rpm = values["PowerSavingGameFan2Rpm"],
            IntelligentNormalFan1Rpm = values["IntelligentNormalFan1Rpm"],
            IntelligentNormalFan2Rpm = values["IntelligentNormalFan2Rpm"],
            IntelligentGameFan1Rpm = values["IntelligentGameFan1Rpm"],
            IntelligentGameFan2Rpm = values["IntelligentGameFan2Rpm"],
            PerformanceNormalFan1Rpm = values["PerformanceNormalFan1Rpm"],
            PerformanceNormalFan2Rpm = values["PerformanceNormalFan2Rpm"],
            PerformanceGameFan1Rpm = values["PerformanceGameFan1Rpm"],
            PerformanceGameFan2Rpm = values["PerformanceGameFan2Rpm"],
            GeekNormalFan1Rpm = values["GeekNormalFan1Rpm"],
            GeekNormalFan2Rpm = values["GeekNormalFan2Rpm"],
            GeekGameFan1Rpm = values["GeekGameFan1Rpm"],
            GeekGameFan2Rpm = values["GeekGameFan2Rpm"]
        };
        value = CurveProfileStore.NormalizeFixedRpmSettings(value, limits);
        error = string.Empty;
        return true;
    }

    private void PopulateFixed(FixedRpmSettings value)
    {
        var map = new Dictionary<string, int>
        {
            ["PowerSavingNormalFan1Rpm"] = value.PowerSavingNormalFan1Rpm,
            ["PowerSavingNormalFan2Rpm"] = value.PowerSavingNormalFan2Rpm,
            ["PowerSavingGameFan1Rpm"] = value.PowerSavingGameFan1Rpm,
            ["PowerSavingGameFan2Rpm"] = value.PowerSavingGameFan2Rpm,
            ["IntelligentNormalFan1Rpm"] = value.IntelligentNormalFan1Rpm,
            ["IntelligentNormalFan2Rpm"] = value.IntelligentNormalFan2Rpm,
            ["IntelligentGameFan1Rpm"] = value.IntelligentGameFan1Rpm,
            ["IntelligentGameFan2Rpm"] = value.IntelligentGameFan2Rpm,
            ["PerformanceNormalFan1Rpm"] = value.PerformanceNormalFan1Rpm,
            ["PerformanceNormalFan2Rpm"] = value.PerformanceNormalFan2Rpm,
            ["PerformanceGameFan1Rpm"] = value.PerformanceGameFan1Rpm,
            ["PerformanceGameFan2Rpm"] = value.PerformanceGameFan2Rpm,
            ["GeekNormalFan1Rpm"] = value.GeekNormalFan1Rpm,
            ["GeekNormalFan2Rpm"] = value.GeekNormalFan2Rpm,
            ["GeekGameFan1Rpm"] = value.GeekGameFan1Rpm,
            ["GeekGameFan2Rpm"] = value.GeekGameFan2Rpm
        };
        foreach (var pair in map)
        {
            if (_fixedBoxes.TryGetValue(pair.Key, out var box))
                box.Text = pair.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task LoadPowerAsync()
    {
        await RefreshPowerReadoutsAsync(showProgress: true);
    }

    private async Task RefreshPowerReadoutsAsync(bool showProgress = false)
    {
        if (_disposed ||
            _powerRefreshInProgress ||
            Runtime.Report?.IsAvailable(FeatureIds.PowerSettings) != true)
        {
            return;
        }

        _powerRefreshInProgress = true;
        if (showProgress)
            _powerStatus.Text = L("正在读取当前功耗设置……", "Reading current power settings…");
        try
        {
            var current = await Runtime.ReadPowerSettingsAsync();
            _confirmedPower = current;
            UpdatePowerReadouts(current);
            if (_powerEditorExpanded && !_powerDirty)
                ApplyPowerState(current);
            else if (!_powerDirty)
                UpdatePowerStatus();
        }
        catch (Exception ex)
        {
            _powerStatus.Text = L("功耗参数刷新失败：", "Power readout refresh failed: ") + ex.Message;
        }
        finally
        {
            _powerRefreshInProgress = false;
            SyncPowerLockControls();
        }
    }

    private async Task ApplyPowerAsync()
    {
        if (_powerRefreshInProgress)
        {
            _powerStatus.Text = L(
                "正在刷新当前值，请稍后重试。",
                "Current values are being refreshed; try again shortly.");
            return;
        }
        if (!TryCollectPower(out var draft, out var error))
        {
            _powerStatus.Text = error;
            return;
        }
        var previous = _confirmedPower;
        _powerRefreshInProgress = true;
        SetPowerEnabled(false);
        try
        {
            var confirmed = await Runtime.ApplyPowerSettingsAsync(draft);
            _confirmedPower = confirmed;
            ApplyPowerState(confirmed);
            _powerStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ApplyPowerState(previous);
            _powerStatus.Text = L("写入失败，界面已恢复到上次确认状态：", "Write failed; UI restored to the last confirmed state: ") + ex.Message;
        }
        finally
        {
            _powerRefreshInProgress = false;
            SetPowerEnabled(true);
        }
    }

    private bool TryCollectPower(out PowerSettingsState state, out string error)
    {
        state = null!;
        if (_confirmedPower is not { } confirmed)
        {
            error = L("请先成功读取当前功耗设置。", "Read the current power settings first.");
            return false;
        }
        foreach (var pair in _powerEditors)
        {
            if (pair.Key == "Atpp" &&
                _atppEditorRow?.Visibility != Visibility.Visible)
            {
                continue;
            }
            if (!pair.Value.TryGetValue(out _))
            {
                error = pair.Value.ManualMinimum.HasValue
                    ? pair.Value.ManualMinimum == 0
                        ? L($"{pair.Key} 必须是非负整数；滑动条范围不限制手动输入。", $"{pair.Key} must be a non-negative integer; the slider range does not limit manual input.")
                        : L($"{pair.Key} 必须是正整数；滑动条范围不限制手动输入。", $"{pair.Key} must be a positive integer; the slider range does not limit manual input.")
                    : L($"{pair.Key} 必须在 {pair.Value.Minimum}–{pair.Value.Maximum} 之间。", $"{pair.Key} must be from {pair.Value.Minimum} to {pair.Value.Maximum}.");
                return false;
            }
        }
        var turboAvailable = confirmed.IsAvailable(PowerSetting.CpuTurboTimeLimit);
        if (turboAvailable && Selected<int>(_turboTime) is not { })
        {
            error = L("请选择 CPU Turbo Time Limit。", "Select a CPU Turbo Time Limit.");
            return false;
        }
        int Editor(string key, int fallback) =>
            _powerEditors.TryGetValue(key, out var editor) && editor.View.Visibility == Visibility.Visible
                ? editor.Value
                : fallback;
        state = new PowerSettingsState(
            Editor("CpuPl1", confirmed.CpuPl1),
            Editor("CpuPl2", confirmed.CpuPl2),
            Editor("CpuTemperature", confirmed.CpuTemperatureLimit),
            turboAvailable ? Selected<int>(_turboTime)!.Value : confirmed.CpuTurboTimeLimit,
            Editor("GpuBoost", confirmed.GpuPowerBoost),
            Editor("GpuTgp", confirmed.GpuConfigurableTgp),
            Editor("GpuTemperature", confirmed.GpuTemperatureLimit),
            Editor("GpuToCpu", confirmed.GpuToCpuDynamicBoost),
            confirmed.IsAvailable(PowerSetting.Atpp) && _powerEditors.TryGetValue("Atpp", out var atpp)
                ? atpp.Value
                : confirmed.Atpp)
        { AvailableSettings = confirmed.AvailableSettings };
        error = string.Empty;
        return true;
    }

    private void ApplyPowerState(PowerSettingsState? state, bool confirmed = true)
    {
        if (state is null) return;
        UpdatePowerReadouts(state);
        if (_powerEditors.Count == 0)
        {
            if (confirmed)
                _confirmedPower = state;
            _powerDirty = false;
            return;
        }
        _syncing = true;
        SetEditor("CpuPl1", PowerSetting.CpuPl1, state.CpuPl1);
        SetEditor("CpuPl2", PowerSetting.CpuPl2, state.CpuPl2);
        SetEditor("CpuTemperature", PowerSetting.CpuTemperatureLimit, state.CpuTemperatureLimit);
        if (state.IsAvailable(PowerSetting.CpuTurboTimeLimit))
            Select(_turboTime, state.CpuTurboTimeLimit);
        SetEditor("GpuBoost", PowerSetting.GpuPowerBoost, state.GpuPowerBoost);
        SetEditor("GpuTgp", PowerSetting.GpuConfigurableTgp, state.GpuConfigurableTgp);
        SetEditor("GpuTemperature", PowerSetting.GpuTemperatureLimit, state.GpuTemperatureLimit);
        SetEditor("GpuToCpu", PowerSetting.GpuToCpuDynamicBoost, state.GpuToCpuDynamicBoost);
        if (state.Atpp.HasValue && _powerEditors.TryGetValue("Atpp", out var atpp))
            atpp.SetValue(state.Atpp.Value);
        _syncing = false;
        if (confirmed) _confirmedPower = state;
        _powerDirty = !confirmed;
        UpdatePowerStatus();

        void SetEditor(string key, PowerSetting setting, int value)
        {
            if (state.IsAvailable(setting) && _powerEditors.TryGetValue(key, out var editor))
                editor.SetValue(value);
        }
    }

    private void UpdatePowerReadouts(PowerSettingsState state)
    {
        _powerReadouts["CpuPl1"].Text = state.CpuPl1.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["CpuPl2"].Text = state.CpuPl2.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["CpuTemperature"].Text = state.CpuTemperatureLimit.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["TurboTime"].Text = state.CpuTurboTimeLimit.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["GpuBoost"].Text = state.GpuPowerBoost.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["GpuTgp"].Text = state.GpuConfigurableTgp.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["GpuTemperature"].Text = state.GpuTemperatureLimit.ToString(CultureInfo.InvariantCulture);
        _powerReadouts["GpuToCpu"].Text = state.GpuToCpuDynamicBoost.ToString(CultureInfo.InvariantCulture);
        foreach (var pair in _powerReadoutRows)
            pair.Value.Visibility = state.IsAvailable(pair.Key)
                ? Visibility.Visible
                : Visibility.Collapsed;
        foreach (var pair in _powerEditorRows)
            pair.Value.Visibility = state.IsAvailable(pair.Key)
                ? Visibility.Visible
                : Visibility.Collapsed;
        var atppAvailable = state.IsAvailable(PowerSetting.Atpp) && state.Atpp.HasValue;
        if (atppAvailable)
            _powerReadouts["Atpp"].Text = state.Atpp!.Value.ToString(CultureInfo.InvariantCulture);
        if (_atppReadout is not null)
            _atppReadout.Visibility = atppAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (_atppEditorRow is not null)
            _atppEditorRow.Visibility = atppAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SyncRuntimeControls()
    {
        _syncing = true;
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
        if (PerformanceModeAvailability.CanSelect(
                snapshot.ItsMode,
                isAcConnected))
            Select(_itsMode, snapshot.ItsMode);
        else
            _itsMode.SelectedItem = null;
        foreach (var item in _gpuMode.Items.OfType<ComboBoxItem>())
            item.Visibility = item.Tag is GpuWorkingMode mode && snapshot.SupportedGpuModes.Contains(mode)
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (GpuModeRestartState.TryParsePendingMode(
                snapshot.PendingGpuMode,
                out var pendingGpuMode))
        {
            Select(_gpuMode, pendingGpuMode);
        }
        else if (snapshot.GpuMode.HasValue)
        {
            Select(_gpuMode, snapshot.GpuMode.Value);
        }
        _pendingRestartRow.Visibility = string.IsNullOrWhiteSpace(snapshot.PendingGpuMode)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _pendingRestartText.Text = PendingGpuModeText(snapshot);
        var gpuState = snapshot.Temperatures?.DiscreteGpuState ??
                       DiscreteGpuActivityState.Unknown;
        var gpuClosed = gpuState == DiscreteGpuActivityState.Off;
        var gpuActive = gpuState == DiscreteGpuActivityState.Active;
        _discreteGpuStatus.Text = DiscreteGpuStatusFormatter.Format(
            gpuState,
            snapshot.Temperatures?.GpuPerformanceState,
            Runtime.IsChinese);
        _discreteGpuStatusRow.Visibility = gpuClosed
            ? Visibility.Collapsed
            : Visibility.Visible;
        _gpuOverclockRow.Visibility = gpuClosed
            ? Visibility.Collapsed
            : Visibility.Visible;
        _viewGpuApplications.Visibility = gpuActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        _killGpuApplications.Visibility = gpuActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        _viewGpuApplications.IsEnabled = gpuActive;
        _killGpuApplications.IsEnabled = gpuActive;
        _gpuOverclockSettings.IsEnabled = !gpuClosed;
        _gpuOverclockEnabled.IsChecked =
            Runtime.Settings.GpuOverclock.Enabled;
        _gpuOverclockEnabled.IsEnabled = !gpuClosed;
        _performanceModeOrderSettings.Visibility =
            Runtime.Settings.TakeOverFnKeys
                ? Visibility.Visible
                : Visibility.Collapsed;
        _fullSpeed.IsChecked = snapshot.FullSpeed;
        var fanMode = snapshot.FanControlRunning || snapshot.FullSpeed
            ? snapshot.FanStrategy switch
            {
                ControlStrategy.FanCurve => FanControlMode.FanCurve,
                ControlStrategy.AdvancedCurve => FanControlMode.AdvancedCurve,
                _ => FanControlMode.FixedRpm
            }
            : FanControlMode.FirmwareAutomatic;
        Select(_strategy, fanMode);
        if (Runtime.FanRuntime?.RuntimeSnapshot() is { } fanRuntime)
            Select(_fixedMode, fanRuntime.EffectiveGameMode);
        ApplyStrategyVisibility(fanMode);
        _cpuCurve.SetCurrentTemp(snapshot.Temperatures?.CpuTempC);
        _gpuCurve.SetCurrentTemp(snapshot.Temperatures?.GpuTempC);
        var runtime = Runtime.FanRuntime?.RuntimeSnapshot();
        if (runtime is not null)
        {
            _cpuCurve.SetRpmRanges(
                runtime.FanRpmLimits.Fan1MinimumRpm,
                runtime.FanRpmLimits.Fan1MaximumRpm,
                runtime.FanRpmLimits.Fan2MinimumRpm,
                runtime.FanRpmLimits.Fan2MaximumRpm);
            _gpuCurve.SetRpmRanges(
                runtime.FanRpmLimits.Fan1MinimumRpm,
                runtime.FanRpmLimits.Fan1MaximumRpm,
                runtime.FanRpmLimits.Fan2MinimumRpm,
                runtime.FanRpmLimits.Fan2MaximumRpm);
        }
        _fanLimitsButton.IsEnabled =
            Runtime.Report?.IsAvailable(FeatureIds.FanControl) == true &&
            !snapshot.FullSpeed;
        _syncing = false;
        SetModeControls(true);
    }

    private void SetModeControls(bool value)
    {
        _itsMode.IsEnabled = value && Runtime.CanSwitchItsMode;
        _performanceModeOrderSettings.IsEnabled =
            value && Runtime.Settings.TakeOverFnKeys && Runtime.CanSwitchItsMode;
        _gpuMode.IsEnabled = value && Runtime.Report?.IsAvailable(FeatureIds.GpuMode) != false;
    }

    private void OnFnKeyTakeoverChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
                OnFnKeyTakeoverChanged(sender, args)));
            return;
        }
        SyncRuntimeControls();
    }

    private void ApplyStrategyVisibility(FanControlMode mode)
    {
        _fixedPanel.Visibility = mode == FanControlMode.FixedRpm
            ? Visibility.Visible
            : Visibility.Collapsed;
        _curvePanel.Visibility = mode == FanControlMode.FanCurve
            ? Visibility.Visible
            : Visibility.Collapsed;
        _advancedCurvePanel.Visibility = mode == FanControlMode.AdvancedCurve
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MarkFanDirty()
    {
        if (_syncing) return;
        _fanDirty = true;
        UpdateFanDraftStatus();
    }

    private void UpdateFanDraftStatus()
    {
        _draftStatus.Text = _fanDirty
            ? L("有未保存的风扇设置", "Fan settings have unsaved changes")
            : string.Empty;
        _draftStatus.Foreground = Brush(_fanDirty ? Palette.Warning : Palette.Muted);
        _applyFan.IsEnabled = _fanDirty;
        _discardFan.IsEnabled = _fanDirty;
    }

    private void MarkPowerDirty()
    {
        if (_syncing) return;
        _powerDirty = true;
        UpdatePowerStatus();
    }

    private void UpdatePowerStatus()
    {
        _powerStatus.Text = _powerDirty
            ? L("有未应用的功耗设置", "Power settings have unapplied changes")
            : string.Empty;
        _powerStatus.Foreground = Brush(_powerDirty ? Palette.Warning : Palette.Muted);
        _applyPower.IsEnabled = _powerDirty;
        _discardPower.IsEnabled = _powerDirty;
    }

    private void ChangePowerSettingLock(PowerSetting setting)
    {
        if (_syncing ||
            !_powerLockToggles.TryGetValue(setting, out var toggle))
        {
            return;
        }
        var enabled = toggle.IsChecked == true;
        if (!Runtime.TrySetPowerSettingLock(
                setting,
                enabled,
                enabled ? _confirmedPower : null,
                out var error))
        {
            _powerStatus.Text = L(
                "无法更改这一项的功耗锁定：",
                "Could not change this power-setting lock: ") + error;
        }
        else
        {
            UpdatePowerStatus();
        }
        SyncPowerLockControls();
    }

    private void ChangePowerSettingsLockInterval()
    {
        if (_syncing ||
            Selected<int>(_powerLockInterval) is not { } seconds)
        {
            return;
        }
        if (!Runtime.TrySetPowerSettingsLockInterval(seconds, out var error))
        {
            _powerStatus.Text = L(
                "无法保存功耗锁定间隔：",
                "Could not save the power lock interval: ") + error;
        }
        else
        {
            UpdatePowerStatus();
        }
        SyncPowerLockControls();
    }

    private void SyncPowerLockControls(bool enabled = true)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        var selection = Runtime.CurrentPowerSettingsLocks;
        foreach (var pair in _powerLockToggles)
            pair.Value.IsChecked = selection.IsLocked(pair.Key);
        Select(
            _powerLockInterval,
            Runtime.Settings.PowerSettingsLockIntervalSeconds);
        _syncing = wasSyncing;
        foreach (var pair in _powerLockToggles)
        {
            pair.Value.IsEnabled = enabled &&
                (selection.IsLocked(pair.Key) ||
                 _confirmedPower is not null &&
                 (pair.Key != PowerSetting.Atpp ||
                  _confirmedPower.Atpp.HasValue));
        }
        _powerLockInterval.IsEnabled = enabled;
    }

    private void SetPowerEnabled(bool value)
    {
        foreach (var editor in _powerEditors.Values) editor.View.IsEnabled = value;
        _turboTime.IsEnabled = value;
        _defaultPower.IsEnabled = value;
        _applyPower.IsEnabled = value && _powerDirty;
        _discardPower.IsEnabled = value && _powerDirty;
        SyncPowerLockControls(value);
    }

    private void OnSnapshotChanged(object? sender, EventArgs args)
    {
        if (DataContext is PerformanceViewModel viewModel)
            viewModel.Update(Runtime.Snapshot);
        SyncRuntimeControls();
        if (!_coolingOnly && IsLoaded && IsVisible)
            _ = RefreshPowerReadoutsAsync();
    }

    private string GpuModeName(GpuWorkingMode mode) =>
        GpuModeText.Name(mode, Runtime.IsChinese);

    private string PendingGpuModeText(ToolkitRuntimeSnapshot snapshot)
    {
        if (!GpuModeRestartState.TryParsePendingMode(
                snapshot.PendingGpuMode,
                out var target))
        {
            return string.Empty;
        }

        return GpuModeRestartState.TryParsePendingMode(
            snapshot.PendingGpuModeSource,
            out var source)
            ? GpuModeText.Transition(
                source,
                target,
                Runtime.IsChinese)
            : L(
                $"切换到“{GpuModeText.Name(target, true)}”后需要重启",
                $"A restart is required to switch to {GpuModeText.Name(target, false)}");
    }

    private static void AddChoice<T>(ComboBox combo, string label, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static T Selected<T>(ComboBox combo, T fallback) where T : struct =>
        Selected<T>(combo) ?? fallback;

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));

    private static FanProfile CloneProfile(FanProfile profile) => new()
    {
        Name = profile.Name,
        TemperatureSmoothing = profile.TemperatureSmoothing,
        RampUpRpmPerSecond = profile.RampUpRpmPerSecond,
        FullRangeRampDownRpmPerSecond = profile.FullRangeRampDownRpmPerSecond,
        RampDownRpmPerSecond = profile.RampDownRpmPerSecond,
        CpuFan1Curve = [.. profile.CpuFan1Curve],
        CpuFan2Curve = [.. profile.CpuFan2Curve],
        GpuFan1Curve = [.. profile.GpuFan1Curve],
        GpuFan2Curve = [.. profile.GpuFan2Curve],
        CpuCurve = [.. profile.CpuCurve],
        GpuCurve = [.. profile.GpuCurve]
    };

    public override void Dispose()
    {
        _disposed = true;
        Runtime.SnapshotChanged -= OnSnapshotChanged;
        Runtime.FnKeyTakeoverChanged -= OnFnKeyTakeoverChanged;
        base.Dispose();
    }

    private sealed class PerformanceViewModel : HardwareMonitorViewModel
    {
        public PerformanceViewModel(ToolkitRuntimeService runtime) : base(runtime)
        {
            Update(runtime.Snapshot);
        }
    }

    private readonly record struct FanStrategyChoice(
        FanControlMode Mode,
        int ProfileIndex);

    private sealed class PowerIntegerEditor
    {
        private bool _syncing;
        public PowerIntegerEditor(int minimum, int maximum, int? manualMinimum)
        {
            Minimum = minimum;
            Maximum = maximum;
            ManualMinimum = manualMinimum;
            Slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBox = new TextBox
            {
                Width = 82,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var view = new StackPanel { Orientation = Orientation.Horizontal };
            view.Children.Add(Slider);
            view.Children.Add(TextBox);
            View = view;
            Slider.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                _syncing = true;
                TextBox.Text = ((int)Slider.Value).ToString(CultureInfo.InvariantCulture);
                _syncing = false;
                Changed?.Invoke();
            };
            TextBox.TextChanged += (_, _) =>
            {
                if (_syncing) return;
                if (int.TryParse(TextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                    (ManualMinimum.HasValue
                        ? value >= ManualMinimum.Value
                        : value >= Minimum && value <= Maximum))
                {
                    _syncing = true;
                    Slider.Value = Math.Clamp(value, Minimum, Maximum);
                    _syncing = false;
                }
                Changed?.Invoke();
            };
        }
        public int Minimum { get; }
        public int Maximum { get; }
        public int? ManualMinimum { get; }
        public Slider Slider { get; }
        public TextBox TextBox { get; }
        public StackPanel View { get; }
        public int Value => int.TryParse(TextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        public event Action? Changed;
        public bool TryGetValue(out int value) =>
            int.TryParse(TextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            (ManualMinimum.HasValue
                ? value >= ManualMinimum.Value
                : value >= Minimum && value <= Maximum);
        public void SetValue(int value)
        {
            _syncing = true;
            Slider.Value = Math.Clamp(value, Minimum, Maximum);
            TextBox.Text = value.ToString(CultureInfo.InvariantCulture);
            _syncing = false;
        }
    }

}
