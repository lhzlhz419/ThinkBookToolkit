using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ThinkBookToolkit;

internal sealed class ToolkitSettingsPage : ToolkitPageBase
{
    private readonly SettingsViewModel _viewModel;
    private readonly ComboBox _refresh = new() { MinWidth = 150 };
    private readonly ComboBox _language = new() { MinWidth = 150 };
    private readonly ComboBox _theme = new() { MinWidth = 170 };
    private readonly ComboBox _overviewMode = new() { MinWidth = 150 };
    private readonly ComboBox _startupMode = new() { MinWidth = 170 };
    private readonly CheckBox _startToTray = new();
    private readonly CheckBox _minimizeToTray = new();
    private readonly CheckBox _closeToTray = new();
    private readonly CheckBox _takeOverFnKeys = new();
    private readonly Button _customizeFnKeys;
    private readonly Button _discoverFnKeys;
    private readonly Button _gameDetectionPaths;
    private readonly CheckBox _disableOnSleep = new();
    private readonly CheckBox _alternativeFullSpeed = new();
    private readonly CheckBox _continuousFanWrites = new();
    private readonly Button _editOverview;
    private readonly Button _restartReaders;
    private readonly Button _checkUpdates;
    private readonly Button _downloadUpdate;
    private readonly TextBlock _updateStatus = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        TextWrapping = TextWrapping.Wrap
    };
    private Uri? _availableUpdatePage;
    private Window? _overviewEditorWindow;
    private readonly Dictionary<string, CheckBox> _overviewHeroToggles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _overviewCardToggles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Card, string Item), CheckBox>
        _overviewItemToggles = new();
    private OverviewLayoutSettings _overviewDraft = new();
    private Border? _disableOnSleepRow;
    private readonly TextBox _fanReadMinimumInterval = new()
    {
        Width = 86,
        MinHeight = 36,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBox _fanWriteMinimumInterval = new()
    {
        Width = 86,
        MinHeight = 36,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status;
    private bool _syncing;

    public ToolkitSettingsPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new SettingsViewModel(runtime);
        DataContext = _viewModel;
        _status = StatusText();
        _editOverview = ActionButton(L("编辑概览页", "Edit overview"));
        _customizeFnKeys = ActionButton(L("自定义", "Customize"));
        _discoverFnKeys = ActionButton(L("发现 Fn 按键", "Discover Fn keys"));
        _gameDetectionPaths = ActionButton(L("设置", "Settings"));
        _restartReaders = ActionButton(L("强制刷新读数", "Restart readers"));
        _checkUpdates = ActionButton(L("检查更新", "Check for updates"));
        _downloadUpdate = ActionButton(L("下载更新", "Download update"), primary: true);
        _downloadUpdate.Visibility = Visibility.Collapsed;
        Content = BuildLayout();
        SyncControls();
    }

    private UIElement BuildLayout()
    {
        AddChoice(_refresh, L("0.5 秒", "0.5 seconds"), 0.5d);
        AddChoice(_refresh, L("1 秒", "1 second"), 1d);
        AddChoice(_refresh, L("2 秒", "2 seconds"), 2d);
        AddChoice(_refresh, L("3 秒", "3 seconds"), 3d);
        AddChoice(_refresh, L("5 秒", "5 seconds"), 5d);
        AddChoice(_language, "中文", "zh-CN");
        AddChoice(_language, "English", "en-US");
        AddChoice(_theme, L("跟随系统", "Follow system"), "system");
        AddChoice(_theme, L("浅色", "Light"), "light");
        AddChoice(_theme, L("深色", "Dark"), "dark");
        AddChoice(
            _overviewMode,
            L("简洁模式", "Compact"),
            OverviewPageMode.Compact);
        AddChoice(
            _overviewMode,
            L("详细模式", "Detailed"),
            OverviewPageMode.Detailed);
        AddChoice(
            _startupMode,
            L("关闭", "Off"),
            StartupLaunchMode.Disabled);
        AddChoice(
            _startupMode,
            L("打开", "On"),
            StartupLaunchMode.Enabled);
        AddChoice(
            _startupMode,
            L("延迟启动", "Delayed start"),
            StartupLaunchMode.Delayed);
        WireEvents();

        var root = new StackPanel();
        var global = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 300,
            Spacing = 8
        };
        global.Children.Add(SettingRow(
            L("状态刷新间隔", "Status refresh interval"),
            L("设置设备状态和概览信息多久更新一次。", "Choose how often device status and overview information update."),
            _refresh,
            "\uE823"));
        global.Children.Add(SettingRow(
            L("界面语言", "Interface language"),
            L("选择后立即应用到当前窗口。", "Applied to the current window immediately."),
            _language,
            "\uE775"));
        global.Children.Add(SettingRow(
            L("主题", "Theme"),
            L("浅色、深色或跟随 Windows。", "Light, dark, or follow Windows."),
            _theme,
            "\uE790"));
        global.Children.Add(SettingRow(
            L("概览页模式选择", "Overview mode"),
            L("在简洁读数卡片和完整硬件信息之间切换。", "Switch between compact reading cards and full hardware details."),
            _overviewMode,
            "\uECA5"));
        global.Children.Add(SettingRow(
            L("概览页内容", "Overview contents"),
            L("选择概览页显示的卡片和数据项。", "Choose the cards and readings shown on the overview page."),
            _editOverview,
            "\uE8A9"));
        global.Children.Add(SettingRow(
            L("强制刷新读数", "Force-refresh readings"),
            L("关闭并重新创建硬件数据读取组件。", "Close and recreate the hardware data readers."),
            _restartReaders,
            "\uE72C"));
        var globalContent = new StackPanel();
        globalContent.Children.Add(SettingRow(
            L("当前版本", "Current version"),
            $"v{ApplicationUpdateService.CurrentVersionText}",
            BuildUpdateActions()));
        globalContent.Children.Add(global);
        root.Children.Add(Card(
            L("全局设置", "Global settings"),
            globalContent,
            L("管理界面语言、外观和信息更新频率。", "Manage language, appearance, and information updates."),
            "\uE713"));

        var startup = new StackPanel();
        var startupPrimary = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 400,
            Spacing = 8
        };
        startupPrimary.Children.Add(SettingRow(
            L("开机自启", "Start with Windows"),
            L(
                $"可关闭、登录后立即启动，或延迟 {MainWindow.StartupDelaySeconds} 秒启动。",
                $"Turn off, start at sign-in, or start after a {MainWindow.StartupDelaySeconds}-second delay."),
            _startupMode));
        startupPrimary.Children.Add(SettingRow(
            L("启动到托盘", "Start in tray"),
            L("仅在开机自启时生效。", "Used when Toolkit is launched at sign-in."),
            _startToTray));
        startupPrimary.Children.Add(SettingRow(
            L("最小化到托盘", "Minimize to tray"),
            L("最小化主窗口时隐藏任务栏按钮。", "Hide the taskbar button when the main window is minimized."),
            _minimizeToTray));
        startupPrimary.Children.Add(SettingRow(
            L("关闭时最小化", "Close to tray"),
            L("关闭按钮隐藏窗口；从托盘菜单选择退出才结束程序。", "The close button hides the window; Exit in the tray menu ends the app."),
            _closeToTray));
        startup.Children.Add(startupPrimary);
        var fnKeyControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "KeepOnRight"
        };
        _customizeFnKeys.Margin = new Thickness(0, 0, 12, 0);
        _discoverFnKeys.Margin = new Thickness(0, 0, 12, 0);
        fnKeyControls.Children.Add(_discoverFnKeys);
        fnKeyControls.Children.Add(_customizeFnKeys);
        fnKeyControls.Children.Add(_takeOverFnKeys);
        startup.Children.Add(SettingRow(
            L(
                "禁用 Lenovo Hotkeys 并接管 Fn 快捷键",
                "Disable Lenovo Hotkeys and take over Fn keys"),
            L(
                "由 Toolkit 处理 ThinkBook 的 Fn 事件并显示全局提示；Fn+Q 按自定义顺序切换性能模式。关闭此项会恢复 Lenovo Hotkeys。",
                "Let Toolkit handle ThinkBook Fn events and show a global OSD. Fn+Q follows the custom performance-mode order. Turning this off restores Lenovo Hotkeys."),
            fnKeyControls));
        startup.Children.Add(SettingRow(
            L("自定义游戏检测路径", "Custom game detection paths"),
            L(
                "使用包含应用和排除应用列表修正 Windows 游戏检测结果；排除应用优先。",
                "Use include and exclude lists to adjust Windows game detection. Exclusions take priority."),
            _gameDetectionPaths));
        if (Runtime.Report?.IsAvailable(FeatureIds.FanControl) == true)
        {
            _disableOnSleepRow = SettingRow(
                Runtime.FanBackendSupportsDisableControlOnSleep
                    ? L("睡眠时关闭风扇控制", "Release fan control while sleeping")
                    : L(
                        "即使风扇后端声明不支持，也尝试在睡眠时关闭控制",
                        "Try to release fan control for sleep even when the current control method does not support it"),
                Runtime.FanBackendSupportsDisableControlOnSleep
                    ? L(
                        "进入睡眠前恢复固件自动控制，唤醒后按原状态恢复。",
                        "Restore firmware automatic control before sleep and resume afterward.")
                    : string.Empty,
                _disableOnSleep);
            startup.Children.Add(_disableOnSleepRow);
            startup.Children.Add(SettingRow(
                L("风扇读写最小间隔", "Minimum fan I/O intervals"),
                FanIoIntervalDescription(),
                BuildFanIoIntervalEditor()));
            var fanBehavior = new AdaptiveUniformPanel
            {
                MinimumItemWidth = 400,
                Spacing = 8
            };
            fanBehavior.Children.Add(SettingRow(
                L("使用替代方案维持风扇满转", "Use alternative full-speed method"),
                Runtime.NativeFanFullSpeedAvailable
                    ? L(
                        "写入设置的风扇上限作为满转手段。",
                        "Use the configured fan maximum as the full-speed target.")
                    : L(
                        "写入设置的风扇上限作为满转手段。若关闭此项，则无法使用风扇满转功能。",
                        "Use the configured fan maximum as the full-speed target. If this is disabled, full fan speed will be unavailable."),
                _alternativeFullSpeed));
            fanBehavior.Children.Add(SettingRow(
                L("持续写入风扇值", "Continuously write fan targets"),
                L(
                    "目标不变时也持续写入；间隔取状态刷新与风扇写入间隔中的较大值。",
                    "Rewrite unchanged targets using the longer of the status-refresh and fan-write intervals."),
                _continuousFanWrites));
            startup.Children.Add(fanBehavior);
        }
        root.Children.Add(Card(
            L("启动与程序行为", "Startup and application behavior"),
            startup,
            L("设置 Toolkit 的启动、托盘和睡眠行为。", "Choose how Toolkit starts, uses the tray, and behaves during sleep."),
            "\uE7E8"));

        _status.Margin = new Thickness(4, 0, 4, 12);
        root.Children.Add(_status);
        root.Children.Add(BuildAvailabilityCard());
        return root;
    }

    private UIElement BuildAvailabilityCard()
    {
        UIElement content;
        UIElement? headerAction = null;
        if (Runtime.Report is null)
        {
            content = new TextBlock
            {
                Text = L("正在检测功能……", "Detecting features…"),
                Foreground = Brush(Palette.Muted)
            };
        }
        else
        {
            var details = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 10, 0, 0)
            };
            foreach (var group in Runtime.Report.Items.GroupBy(item => CategoryName(item.Category)))
            {
                details.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(Palette.Text),
                    Margin = new Thickness(2, details.Children.Count == 0 ? 0 : 14, 2, 8)
                });
                foreach (var feature in group)
                    details.Children.Add(AvailabilityRow(feature));
            }
            var toggle = ActionButton(L("展开检测详情", "Expand detection details"));
            toggle.Click += (_, _) =>
            {
                var expanded = details.Visibility == Visibility.Visible;
                details.Visibility = expanded
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                toggle.Content = expanded
                    ? L("展开检测详情", "Expand detection details")
                    : L("收起检测详情", "Collapse detection details");
            };
            content = details;
            headerAction = toggle;
        }
        var total = Runtime.Report?.Items.Count ?? 0;
        var available = Runtime.Report?.Items.Count(item => item.Usable) ?? 0;
        return Card(
            L("完整功能监测结果", "Complete feature availability"),
            content,
            Runtime.Report is null
                ? null
                : L($"可用/总共：{available}/{total}", $"Available/total: {available}/{total}"),
            "\uE9D9",
            headerAction: headerAction);
    }

    private Border AvailabilityRow(FeatureAvailability feature)
    {
        var row = new Grid { MinHeight = 30, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var statusColor = feature.PartiallyAvailable
            ? Palette.Warning
            : feature.Available
                ? Palette.Success
                : Palette.Muted;
        row.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = Brush(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = FeatureName(feature.Id, feature.Name),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var detail = FeatureDetail(feature);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 12, 0)
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var badge = new Border
        {
            Background = Tint(statusColor),
            CornerRadius = new CornerRadius(12),
            MinWidth = 66,
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = feature.PartiallyAvailable
                    ? L("部分可用", "Partially available")
                    : feature.Available
                        ? L("可用", "Available")
                        : L("不可用", "Unavailable"),
                Foreground = Brush(statusColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 7),
            Child = row
        };
    }

    private void WireEvents()
    {
        _refresh.SelectionChanged += (_, _) =>
        {
            if (!_syncing && Selected<double>(_refresh) is { } value)
            {
                _viewModel.Status = Runtime.TrySetRefreshInterval(value, out var error)
                    ? string.Empty
                    : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
                SyncControls();
            }
        };
        _language.SelectionChanged += (_, _) =>
        {
            if (!_syncing && Selected<string>(_language) is { } value)
            {
                if (!Runtime.TrySetLanguage(value, out var error))
                {
                    _viewModel.Status = L("语言设置失败，已回滚：", "Language setting failed and was rolled back: ") + error;
                    SyncControls();
                }
            }
        };
        _theme.SelectionChanged += (_, _) =>
        {
            if (!_syncing && Selected<string>(_theme) is { } value)
            {
                if (!Runtime.TrySetTheme(value, out var error))
                {
                    _viewModel.Status = L("主题设置失败，已回滚：", "Theme setting failed and was rolled back: ") + error;
                    SyncControls();
                }
            }
        };
        _overviewMode.SelectionChanged += (_, _) =>
        {
            if (!_syncing &&
                Selected<OverviewPageMode>(_overviewMode) is { } value)
            {
                _viewModel.Status = Runtime.TrySetOverviewPageMode(
                        value,
                        out var error)
                    ? string.Empty
                    : L(
                        "概览页模式保存失败：",
                        "Could not save the overview mode: ") + error;
                SyncControls();
            }
        };
        _startupMode.SelectionChanged += (_, _) =>
        {
            if (!_syncing &&
                Selected<StartupLaunchMode>(_startupMode) is { } value)
            {
                _viewModel.SetStartupMode(value);
                SyncControls();
            }
        };
        _startToTray.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.SetStartToTray(_startToTray.IsChecked == true);
            SyncControls();
        };
        _minimizeToTray.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.Status = Runtime.TrySetMinimizeToTray(_minimizeToTray.IsChecked == true, out var error)
                ? string.Empty
                : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
            SyncControls();
        };
        _closeToTray.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.Status = Runtime.TrySetCloseToTray(_closeToTray.IsChecked == true, out var error)
                ? string.Empty
                : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
            SyncControls();
        };
        _takeOverFnKeys.Click += async (_, _) =>
        {
            if (_syncing)
                return;
            _takeOverFnKeys.IsEnabled = false;
            var enabled = _takeOverFnKeys.IsChecked == true;
            var error = await Runtime.SetFnKeyTakeoverAsync(enabled);
            _viewModel.Status = string.IsNullOrWhiteSpace(error)
                ? string.Empty
                : L(
                    "Fn 快捷键接管设置失败，已回滚：",
                    "Fn-key takeover could not be changed and was rolled back: ") +
                  error;
            SyncControls();
        };
        _customizeFnKeys.Click += (_, _) =>
        {
            var window = new FnAutomationSettingsWindow(Runtime)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        };
        _discoverFnKeys.Click += (_, _) =>
        {
            var window = new FnKeyDiscoveryWindow(Runtime)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        };
        _gameDetectionPaths.Click += (_, _) =>
        {
            var window = new GameDetectionSettingsWindow(Runtime)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        };
        _disableOnSleep.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.Status = Runtime.TrySetDisableControlOnSleep(_disableOnSleep.IsChecked == true, out var error)
                ? string.Empty
                : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
            SyncControls();
        };
        _alternativeFullSpeed.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.Status = Runtime.TrySetAlternativeFullSpeedMethod(
                    _alternativeFullSpeed.IsChecked == true,
                    out var error)
                ? string.Empty
                : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
            SyncControls();
        };
        _continuousFanWrites.Click += (_, _) =>
        {
            if (_syncing) return;
            _viewModel.Status = Runtime.TrySetContinuouslyWriteFanTargets(
                    _continuousFanWrites.IsChecked == true,
                    out var error)
                ? string.Empty
                : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
            SyncControls();
        };
        _editOverview.Click += (_, _) =>
        {
            ShowOverviewEditor();
        };
        _restartReaders.Click += async (_, _) =>
        {
            _restartReaders.IsEnabled = false;
            _viewModel.Status = L("正在重新加载读数……", "Restarting readers…");
            var error = await Runtime.RestartDataReadersAsync();
            _viewModel.Status = string.IsNullOrWhiteSpace(error)
                ? L("读数已刷新。", "Readings refreshed.")
                : L("刷新失败：", "Refresh failed: ") + error;
            _restartReaders.IsEnabled = true;
            _status.Text = _viewModel.Status;
        };
        _checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _downloadUpdate.Click += (_, _) => OpenAvailableUpdate();
        _fanReadMinimumInterval.LostKeyboardFocus += (_, _) =>
            SaveFanIoIntervals();
        _fanWriteMinimumInterval.LostKeyboardFocus += (_, _) =>
            SaveFanIoIntervals();
        _fanReadMinimumInterval.KeyDown += CommitFanIoIntervalsOnEnter;
        _fanWriteMinimumInterval.KeyDown += CommitFanIoIntervalsOnEnter;
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        _checkUpdates.IsEnabled = false;
        _availableUpdatePage = null;
        _downloadUpdate.Visibility = Visibility.Collapsed;
        _updateStatus.Text = L("正在检查…", "Checking…");
        try
        {
            ToolkitLog.Info("Checking GitHub Releases for an application update.");
            var release = await ApplicationUpdateService.CheckAsync();
            ApplyUpdateResult(release);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("The application update check failed.", ex);
            _updateStatus.Text = L(
                "检查失败，请稍后重试",
                "Check failed. Try again later");
        }
        finally
        {
            _checkUpdates.IsEnabled = true;
        }
    }

    private UIElement BuildUpdateActions()
    {
        _updateStatus.Foreground = Brush(Palette.Muted);
        _updateStatus.Margin = new Thickness(0, 0, 16, 0);
        _downloadUpdate.Margin = new Thickness(0, 0, 8, 0);
        _checkUpdates.Margin = new Thickness(0);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(_updateStatus);
        actions.Children.Add(_downloadUpdate);
        actions.Children.Add(_checkUpdates);
        return actions;
    }

    private void ApplyUpdateResult(ApplicationRelease release)
    {
        if (ApplicationUpdateService.IsNewer(release))
        {
            _availableUpdatePage = release.PageUri;
            _updateStatus.Text = L(
                $"最新版 {release.TagName}",
                $"Latest {release.TagName}");
            _downloadUpdate.Visibility = Visibility.Visible;
            return;
        }

        _availableUpdatePage = null;
        _updateStatus.Text = L(
            "当前已是最新版",
            "Already up to date");
        _downloadUpdate.Visibility = Visibility.Collapsed;
    }

    private void OpenAvailableUpdate()
    {
        if (_availableUpdatePage is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _availableUpdatePage.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("The GitHub Release page could not be opened.", ex);
            _updateStatus.Text = L(
                "无法打开下载页面",
                "Could not open the download page");
        }
    }

    private void SyncControls()
    {
        _syncing = true;
        var settings = Runtime.Settings;
        Select(_refresh, settings.IntervalSeconds);
        Select(_language, settings.Language);
        Select(_theme, settings.Theme);
        Select(_overviewMode, settings.OverviewPageMode);
        Select(_startupMode, Runtime.CurrentStartupMode);
        _startToTray.IsChecked = settings.StartToTray;
        _startToTray.IsEnabled = settings.StartWithWindows;
        _minimizeToTray.IsChecked = settings.MinimizeToTray;
        _closeToTray.IsChecked = settings.CloseToTray;
        _takeOverFnKeys.IsChecked = settings.TakeOverFnKeys;
        _takeOverFnKeys.IsEnabled = true;
        _disableOnSleep.IsChecked =
            Runtime.FanBackendSupportsDisableControlOnSleep
                ? settings.DisableControlOnSleep
                : settings.AttemptDisableControlOnSleepWhenUnsupported;
        _alternativeFullSpeed.IsChecked = settings.UseAlternativeFullSpeedMethod;
        _continuousFanWrites.IsChecked = settings.ContinuouslyWriteFanTargets;
        if (_disableOnSleepRow is not null)
        {
            _disableOnSleepRow.Visibility = Runtime.CanConfigureSleepFanControl
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        _fanReadMinimumInterval.Text =
            FormatOptionalInterval(settings.FanReadMinimumIntervalSeconds);
        _fanWriteMinimumInterval.Text =
            FormatOptionalInterval(settings.FanWriteMinimumIntervalSeconds);
        _status.Text = _viewModel.Status;
        _syncing = false;
    }

    private UIElement BuildOverviewEditor()
    {
        var content = new StackPanel();
        var heroItems = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var heroId in OverviewLayoutDefaults.HeroCardDefinitions)
        {
            var toggle = new CheckBox
            {
                Content = OverviewHeroName(heroId),
                Margin = new Thickness(0, 4, 0, 4)
            };
            _overviewHeroToggles[heroId] = toggle;
            toggle.Click += (_, _) =>
            {
                if (_syncing) return;
                _overviewDraft.HeroCards[heroId] = toggle.IsChecked == true;
            };
            heroItems.Children.Add(toggle);
        }
        var heroSection = new StackPanel();
        heroSection.Children.Add(new TextBlock
        {
            Text = L("设备控制中心", "Device control center"),
            FontWeight = FontWeights.SemiBold
        });
        heroSection.Children.Add(heroItems);
        content.Children.Add(new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Margin = new Thickness(0, 0, 0, 8),
            Child = heroSection
        });
        var definitions = Runtime.Settings.OverviewPageMode ==
                OverviewPageMode.Compact
            ? OverviewLayoutDefaults.CompactCardDefinitions
            : OverviewLayoutDefaults.DetailedCardDefinitions;
        foreach (var definition in definitions)
        {
            var displayedItems = definition.Value
                .Where(item => DeviceModelDetector.HasSecondFan() ||
                               item is not ("fan2-speed" or "fan2-target"))
                .ToArray();
            var items = new StackPanel { Margin = new Thickness(28, 5, 0, 0) };
            var cardToggle = new CheckBox
            {
                Content = OverviewCardName(definition.Key),
                FontWeight = FontWeights.SemiBold
            };
            _overviewCardToggles[definition.Key] = cardToggle;
            cardToggle.Click += (_, _) =>
            {
                if (_syncing) return;
                var card = _overviewDraft.Cards[definition.Key];
                card.Enabled = cardToggle.IsChecked == true;
                if (card.Enabled && displayedItems.Length > 0 &&
                    displayedItems.All(item => !card.Items[item]))
                {
                    foreach (var item in displayedItems)
                        card.Items[item] = true;
                }
                SyncOverviewEditor();
            };
            foreach (var itemId in displayedItems)
            {
                var itemToggle = new CheckBox
                {
                    Content = OverviewItemName(definition.Key, itemId),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                _overviewItemToggles[(definition.Key, itemId)] = itemToggle;
                itemToggle.Click += (_, _) =>
                {
                    if (_syncing) return;
                    var card = _overviewDraft.Cards[definition.Key];
                    card.Items[itemId] = itemToggle.IsChecked == true;
                    card.Enabled = displayedItems.Any(item =>
                        card.Items[item]);
                    SyncOverviewEditor();
                };
                items.Children.Add(itemToggle);
            }
            var section = new StackPanel();
            section.Children.Add(cardToggle);
            section.Children.Add(items);
            content.Children.Add(new Border
            {
                Background = Brush(Palette.SurfaceRaised),
                BorderBrush = Brush(Palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13),
                Margin = new Thickness(0, 0, 0, 8),
                Child = section
            });
        }
        var apply = ActionButton(L("应用", "Apply"), primary: true);
        var cancel = ActionButton(L("取消", "Cancel"));
        apply.Click += (_, _) =>
        {
            _viewModel.Status = Runtime.TrySetOverviewLayout(_overviewDraft, out var error)
                ? string.Empty
                : L("概览页设置保存失败：", "Could not save overview settings: ") + error;
            _status.Text = _viewModel.Status;
            _overviewEditorWindow?.Close();
        };
        cancel.Click += (_, _) =>
        {
            _overviewEditorWindow?.Close();
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        return Card(
            L("编辑概览页", "Edit overview"),
            content,
            Runtime.Settings.OverviewPageMode == OverviewPageMode.Compact
                ? L(
                    "设置简洁模式中的卡片和读数。",
                    "Choose the cards and readings used in compact mode.")
                : L(
                    "关闭单项后，同一行剩余的数据会自动占满整行。",
                    "When one item in a pair is hidden, the remaining item fills the row."),
            "\uE70F");
    }

    private void ShowOverviewEditor()
    {
        if (_overviewEditorWindow is not null)
        {
            _overviewEditorWindow.Activate();
            return;
        }

        _overviewDraft = OverviewLayoutDefaults.Clone(
            Runtime.Settings.OverviewLayout);
        _overviewHeroToggles.Clear();
        _overviewCardToggles.Clear();
        _overviewItemToggles.Clear();
        var editor = BuildOverviewEditor();
        var window = new Window
        {
            Owner = Window.GetWindow(this),
            Title = L("概览页内容", "Overview contents"),
            Width = 720,
            Height = 760,
            MinWidth = 520,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Brush(Palette.Canvas),
            Foreground = Brush(Palette.Text),
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
                Content = editor
            }
        };
        _overviewEditorWindow = window;
        window.Closed += (_, _) => _overviewEditorWindow = null;
        SyncOverviewEditor();
        window.ShowDialog();
    }

    private void SyncOverviewEditor()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        foreach (var pair in _overviewHeroToggles)
            pair.Value.IsChecked = _overviewDraft.HeroCards[pair.Key];
        foreach (var pair in _overviewCardToggles)
            pair.Value.IsChecked = _overviewDraft.Cards[pair.Key].Enabled;
        foreach (var pair in _overviewItemToggles)
        {
            var card = _overviewDraft.Cards[pair.Key.Card];
            pair.Value.IsChecked = card.Items[pair.Key.Item];
            pair.Value.IsEnabled = card.Enabled;
        }
        _syncing = wasSyncing;
    }

    private string OverviewCardName(string id) => id switch
    {
        OverviewCardIds.Cpu => "CPU",
        OverviewCardIds.Gpu => "GPU",
        OverviewCardIds.Battery => L("电池", "Battery"),
        OverviewCardIds.MemoryStorage => Runtime.Settings.OverviewPageMode ==
            OverviewPageMode.Compact
                ? L("内存", "Memory")
                : L("内存与硬盘", "Memory and storage"),
        OverviewCardIds.Fans => L("风扇", "Fans"),
        OverviewCardIds.Power => L("功耗限制", "Power limits"),
        OverviewCardIds.Warranty => L("保修信息", "Warranty"),
        _ => id
    };

    private string OverviewHeroName(string id) => id switch
    {
        OverviewHeroIds.PerformanceMode => L("性能模式", "Performance mode"),
        OverviewHeroIds.GpuMode => L("GPU 模式", "GPU mode"),
        OverviewHeroIds.FanControl => L("风扇控制", "Fan control"),
        OverviewHeroIds.DiscreteGpuStatus => L("独显状态", "Discrete GPU status"),
        OverviewHeroIds.RestartStatus => L("重启状态", "Restart status"),
        _ => id
    };

    private string OverviewItemName(string card, string item) =>
        (card, item) switch
        {
            (OverviewCardIds.Cpu, "utilization") => L("利用率", "Utilization"),
            (OverviewCardIds.Cpu, "average-frequency") => L("平均频率", "Average frequency"),
            (OverviewCardIds.Cpu, "maximum-frequency") => L("最高频率", "Maximum frequency"),
            (OverviewCardIds.Cpu, "temperature") => L("温度", "Temperature"),
            (OverviewCardIds.Cpu, "power") => L("功耗", "Power"),
            (OverviewCardIds.Gpu, "utilization") => L("利用率", "Utilization"),
            (OverviewCardIds.Gpu, "vram-utilization") => L("显存利用率", "VRAM utilization"),
            (OverviewCardIds.Gpu, "core-frequency") => L("核心频率", "Core frequency"),
            (OverviewCardIds.Gpu, "vram-frequency") => L("显存频率", "VRAM frequency"),
            (OverviewCardIds.Gpu, "core-temperature") => L("核心温度", "Core temperature"),
            (OverviewCardIds.Gpu, "hotspot-temperature") => L("热点温度", "Hot spot temperature"),
            (OverviewCardIds.Gpu, "vram-temperature") => L("显存温度", "VRAM temperature"),
            (OverviewCardIds.Gpu, "power") => L("功耗", "Power"),
            (OverviewCardIds.Battery, "status") => L("当前状态", "Status"),
            (OverviewCardIds.Battery, "charge") => L("电量", "Charge"),
            (OverviewCardIds.Battery, "health") => L("健康度", "Health"),
            (OverviewCardIds.Battery, "power") => L("功率", "Power"),
            (OverviewCardIds.MemoryStorage, "physical-memory") => L("物理内存", "Physical memory"),
            (OverviewCardIds.MemoryStorage, "virtual-memory") => L("虚拟内存", "Virtual memory"),
            (OverviewCardIds.MemoryStorage, "slot1-temperature") => L("内存插槽1温度", "Memory slot 1 temperature"),
            (OverviewCardIds.MemoryStorage, "slot2-temperature") => L("内存插槽2温度", "Memory slot 2 temperature"),
            (OverviewCardIds.MemoryStorage, "disk-temperatures") => L("所有硬盘温度", "All disk temperatures"),
            (OverviewCardIds.MemoryStorage, "disk-health") => L("所有硬盘健康度", "All disk health"),
            (OverviewCardIds.MemoryStorage, "utilization") => L("利用率", "Utilization"),
            (OverviewCardIds.MemoryStorage, "average-temperature") => L("平均温度", "Average temperature"),
            (OverviewCardIds.Fans, "fan1-speed") => L("风扇1转速", "Fan 1 speed"),
            (OverviewCardIds.Fans, "fan2-speed") => L("风扇2转速", "Fan 2 speed"),
            (OverviewCardIds.Fans, "fan1-target") => L("风扇1目标", "Fan 1 target"),
            (OverviewCardIds.Fans, "fan2-target") => L("风扇2目标", "Fan 2 target"),
            (OverviewCardIds.Power, "cpu-pl1") => "CPU PL1",
            (OverviewCardIds.Power, "cpu-pl2") => "CPU PL2",
            (OverviewCardIds.Power, "cpu-temperature") => L("CPU 温度上限", "CPU temperature limit"),
            (OverviewCardIds.Power, "turbo-time") => "CPU Turbo Time Limit",
            (OverviewCardIds.Power, "gpu-boost") => "GPU Power Boost",
            (OverviewCardIds.Power, "gpu-tgp") => "GPU TGP",
            (OverviewCardIds.Power, "gpu-temperature") => L("GPU 温度上限", "GPU temperature limit"),
            (OverviewCardIds.Power, "gpu-to-cpu") => "GPU to CPU Dynamic Boost",
            (OverviewCardIds.Power, "atpp") => "ATPP",
            (OverviewCardIds.Warranty, "status") => L("保修状态", "Warranty status"),
            (OverviewCardIds.Warranty, "start-date") => L("开始日期", "Start date"),
            (OverviewCardIds.Warranty, "end-date") => L("结束日期", "End date"),
            (OverviewCardIds.Warranty, "remaining-days") => L("剩余天数", "Days remaining"),
            (OverviewCardIds.Warranty, "progress") => L("已用保修期", "Warranty elapsed"),
            _ => item
        };

    private UIElement BuildFanIoIntervalEditor()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(IntervalLabel(L("读取", "Read")));
        panel.Children.Add(_fanReadMinimumInterval);
        panel.Children.Add(IntervalUnit());
        panel.Children.Add(IntervalLabel(L("写入", "Write"), 16));
        panel.Children.Add(_fanWriteMinimumInterval);
        panel.Children.Add(IntervalUnit());
        return panel;
    }

    private TextBlock IntervalLabel(string text, double leftMargin = 0) =>
        new()
        {
            Text = text,
            Foreground = Brush(Palette.Text),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 0, 8, 0)
        };

    private TextBlock IntervalUnit() =>
        new()
        {
            Text = L("秒", "s"),
            Foreground = Brush(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };

    private string FanIoIntervalDescription()
    {
        if (Runtime.FanBackendMinimumReadInterval is not TimeSpan read ||
            Runtime.FanBackendMinimumWriteInterval is not TimeSpan write)
        {
            return L(
                "过短的间隔可能造成卡顿。默认间隔由当前风扇控制方式决定。",
                "Intervals that are too short may cause stuttering. Defaults depend on the current fan-control method.");
        }

        return L(
            $"过短的间隔可能造成卡顿。默认间隔：读取 {FormatSeconds(read)} 秒，写入 {FormatSeconds(write)} 秒。",
            $"Intervals that are too short may cause stuttering. Defaults: read {FormatSeconds(read)} s, write {FormatSeconds(write)} s.");
    }

    private void CommitFanIoIntervalsOnEnter(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter)
            return;
        args.Handled = true;
        SaveFanIoIntervals();
    }

    private void SaveFanIoIntervals()
    {
        if (_syncing)
            return;

        if (!TryParseOptionalInterval(
                _fanReadMinimumInterval.Text,
                out var readSeconds) ||
            !TryParseOptionalInterval(
                _fanWriteMinimumInterval.Text,
                out var writeSeconds))
        {
            _viewModel.Status = L(
                "读写间隔必须留空或填写正整数秒数。",
                "Read and write intervals must be blank or positive whole seconds.");
            SyncControls();
            return;
        }

        _viewModel.Status = Runtime.TrySetFanIoMinimumIntervals(
                readSeconds,
                writeSeconds,
                out var error)
            ? string.Empty
            : L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
        SyncControls();
    }

    private static bool TryParseOptionalInterval(
        string text,
        out double? seconds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            seconds = null;
            return true;
        }

        if ((long.TryParse(
                 text,
                 NumberStyles.Integer,
                 CultureInfo.CurrentCulture,
                 out var parsed) ||
             long.TryParse(
                 text,
                 NumberStyles.Integer,
                 CultureInfo.InvariantCulture,
                 out parsed)) &&
            CurveProfileStore.IsValidFanIoIntervalOverride(parsed))
        {
            seconds = parsed;
            return true;
        }

        seconds = null;
        return false;
    }

    private static string FormatOptionalInterval(double? seconds) =>
        seconds?.ToString("0", CultureInfo.CurrentCulture) ??
        string.Empty;

    private static string FormatSeconds(TimeSpan interval) =>
        interval.TotalSeconds.ToString(
            "0.########",
            CultureInfo.CurrentCulture);

    private string CategoryName(string category) => category switch
    {
        "监控" => L("监控", "Monitoring"),
        "性能" => L("性能", "Performance"),
        "散热" => L("散热", "Cooling"),
        "电池与电源" => L("电池与电源", "Battery and power"),
        "显示" => L("显示", "Display"),
        "声音" => L("声音", "Sound"),
        "输入设备" => L("输入设备", "Input devices"),
        "设备" => L("设备", "Device"),
        "驱动更新" => L("驱动更新", "Driver updates"),
        "自动化" => L("自动化", "Automation"),
        "高级工具" => L("高级工具", "Advanced tools"),
        "设置" => L("设置", "Settings"),
        _ => category
    };

    private string FeatureName(string id, string fallback)
    {
        if (Runtime.IsChinese) return fallback;
        return id switch
        {
            FeatureIds.TemperatureMonitoring => "Temperature and power monitoring",
            FeatureIds.FanControl => "Fan monitoring and control",
            FeatureIds.FanFullSpeed => "Full fan speed",
            FeatureIds.SleepFanControl => "Release fan control while sleeping",
            FeatureIds.PerformanceMode => "Performance mode",
            FeatureIds.GpuMode => "GPU working mode",
            FeatureIds.DiscreteGpuManagement =>
                "Discrete GPU status and applications",
            FeatureIds.GpuOverclock => "Discrete GPU overclocking",
            FeatureIds.PowerSettings => "Power limits",
            FeatureIds.BatteryChargeMode => "Charging mode",
            FeatureIds.OvernightCharging => "Overnight charging",
            FeatureIds.AlwaysOnUsb => "Always-on USB",
            FeatureIds.FlipToStart => "Flip to start",
            FeatureIds.BatteryInformation => "Battery information",
            FeatureIds.VantageEyeCare => "Vantage eye care",
            FeatureIds.PcManagerEyeCare => "PC Manager eye care",
            FeatureIds.ColorManagement => "Color management",
            FeatureIds.DisplayRefreshRate =>
                "Laptop display refresh-rate switching",
            FeatureIds.DolbyAtmos => "Dolby Atmos",
            FeatureIds.SpeakerNoiseCancellation => "Speaker noise cancellation",
            FeatureIds.MicrophoneNoiseCancellation => "Microphone noise cancellation",
            FeatureIds.KeyboardBacklight => "Keyboard backlight",
            FeatureIds.KeyboardBacklightAutoOff => "Keyboard backlight auto-off",
            FeatureIds.FunctionLock => "Function lock",
            FeatureIds.CapsLockOsd => "CapsLock OSD",
            FeatureIds.NumLockOsd => "NumLock OSD",
            FeatureIds.FnCtrlSwap => "Swap Fn and Ctrl",
            FeatureIds.Touchpad => "Touchpad",
            FeatureIds.FnKeyTakeover => "Fn-key takeover",
            FeatureIds.DeviceInformation => "Device information",
            FeatureIds.WarrantyInformation => "Warranty information",
            FeatureIds.BootLogo => "Boot logo customization",
            FeatureIds.BiosSetup => "Enter BIOS setup",
            FeatureIds.StartupInterrupt => "Startup interrupt menu",
            FeatureIds.SecureWipe => "Secure wipe",
            FeatureIds.BiosIoControl => "I/O controls",
            FeatureIds.DriverUpdate => "Lenovo driver and firmware updates",
            FeatureIds.Automation => "Automation and Fn-key mappings",
            FeatureIds.KeyboardMacros => "Keyboard macros",
            FeatureIds.UpdateCheck => "Software update check",
            _ => fallback
        };
    }

    private string FeatureDetail(FeatureAvailability feature)
    {
        if (feature.Available && !feature.PartiallyAvailable)
        {
            return feature.Id == FeatureIds.PowerSettings &&
                   !string.IsNullOrWhiteSpace(feature.EnglishDetail)
                ? L(feature.Detail, feature.EnglishDetail)
                : string.Empty;
        }
        if (!feature.Usable)
        {
            return feature.Id switch
            {
                FeatureIds.SleepFanControl => L("当前风扇控制方式不支持此功能。", "The current fan-control method does not support this feature."),
                FeatureIds.PowerSettings => L("当前设备不支持查看或调整功耗参数。", "Power values cannot be viewed or changed on this device."),
                FeatureIds.WarrantyInformation => L("需要有效的序列号和网络连接。", "A valid serial number and network connection are required."),
                FeatureIds.FanControl => L("当前设备上无法使用风扇监控与控制。", "Fan monitoring and control are unavailable on this device."),
                FeatureIds.FanFullSpeed => L("当前风扇后端不支持原生风扇拉满。", "Native full fan speed is unavailable with the current backend."),
                _ => L("当前设备上未检测到此功能。", "This feature was not detected on the current device.")
            };
        }
        return feature.Id switch
        {
            FeatureIds.TemperatureMonitoring => L("部分温度或功耗数据暂时不可用。", "Some temperature or power readings are unavailable."),
            FeatureIds.FanControl => L("部分风扇功能暂时不可用。", "Some fan functions are unavailable."),
            FeatureIds.FanFullSpeed => L("风扇拉满仅可通过替代方案使用。", "Full fan speed is available only through the alternative method."),
            FeatureIds.SleepFanControl => L("睡眠期间的风扇控制仅部分可用。", "Fan handling during sleep is only partially available."),
            FeatureIds.PowerSettings when feature.PartiallyAvailable =>
                PowerSettingsController.CurrentProfile.Writable
                    ? L("可读取并调整已检测到的功耗参数。", "Detected power values can be read and adjusted.")
                    : L("可以查看当前功耗参数，但此设备不支持修改。", "Current power values are available, but this device does not support changing them."),
            _ => L("此功能仅部分可用。", "This feature is only partially available.")
        };
    }

    private static void AddChoice<T>(ComboBox combo, string label, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));

    private sealed class SettingsViewModel : ToolkitViewModelBase
    {
        public SettingsViewModel(ToolkitRuntimeService runtime) : base(runtime)
        {
            Status = string.Empty;
        }

        public new string Status
        {
            get => base.Status;
            set => base.Status = value;
        }

        public void SetStartupMode(StartupLaunchMode value)
        {
            Status = Runtime.TrySetStartupMode(value, out var error)
                ? string.Empty
                : Runtime.L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
        }

        public void SetStartToTray(bool value)
        {
            Status = Runtime.TrySetStartToTray(value, out var error)
                ? string.Empty
                : Runtime.L("设置失败，已回滚：", "Setting failed and was rolled back: ") + error;
        }
    }
}
