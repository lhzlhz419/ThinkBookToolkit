using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed partial class ToolkitDisplayPage
{
    private readonly ComboBox _panelResolution = new() { MinWidth = 170 };
    private readonly ComboBox _panelScale = new() { MinWidth = 140 };
    private readonly TextBox _customScale = new() { Width = 80, Text = "150", MaxLength = 3, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _panelStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private Button _resolutionApply = null!, _scaleApply = null!, _customScaleApply = null!, _customScaleDisable = null!, _panelReload = null!;
    private LaptopDisplayLayout? _panelLayout;
    private bool _panelBusy, _panelReading, _customScalePending;

    private UIElement BuildPanelLayoutCard()
    {
        var rows = new StackPanel();
        _resolutionApply = ActionButton(L("应用", "Apply"));
        _scaleApply = ActionButton(L("应用", "Apply"));
        _customScaleApply = ActionButton(L("应用自定义", "Apply custom"));
        _customScaleDisable = ActionButton(L("关闭自定义缩放", "Disable custom scaling"));
        _panelReload = ActionButton(L("重新读取", "Reload"));
        _panelReload.Click += async (_, _) =>
        {
            await ReloadPanelLayoutAsync();
            if (Runtime.Report?.IsAvailable(FeatureIds.DisplayRefreshRate) != false)
                await _viewModel.ReloadRefreshRateAsync();
            SyncControls();
        };
        static UIElement Controls(params UIElement[] elements)
        {
            var panel = new WrapPanel();
            foreach (var element in elements)
            {
                if (element is FrameworkElement control) control.Margin = new Thickness(4, 2, 4, 2);
                panel.Children.Add(element);
            }
            return panel;
        }
        if (Runtime.Report?.IsAvailable(FeatureIds.DisplayRefreshRate) != false)
        {
            var refreshControls = new Grid { MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
            refreshControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            refreshControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _refreshRate.HorizontalAlignment = HorizontalAlignment.Stretch;
            refreshControls.Children.Add(_refreshRate);
            _refreshRateSettings.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(_refreshRateSettings, 1);
            refreshControls.Children.Add(_refreshRateSettings);
            rows.Children.Add(SettingRow(L("刷新率", "Refresh rate"),
                L("直接切换笔记本内置屏幕的刷新率；设置中的选项也用于 Fn+R 循环。",
                    "Switch the laptop panel refresh rate. Enabled rates are also used by the Fn+R cycle."),
                refreshControls));
        }
        rows.Children.Add(SettingRow(L("分辨率", "Resolution"),
            L("仅调整内置屏幕；尽量保留当前刷新率，不支持时选择最接近的档位。15 秒未确认会还原。",
                "Internal panel only. Keep the refresh rate when supported, otherwise use the nearest. Reverts unless confirmed within 15 seconds."),
            Controls(_panelResolution, _resolutionApply)));
        rows.Children.Add(SettingRow(L("DPI 缩放", "DPI scaling"),
            L("使用当前内置屏幕支持的固定缩放档位；15 秒未确认会还原。",
                "Use a supported scaling preset for the internal panel. Reverts unless confirmed within 15 seconds."),
            Controls(_panelScale, _scaleApply)));
        rows.Children.Add(SettingRow(L("自定义缩放（系统级）", "Custom scaling (system-wide)"),
            L("100–500%。Windows 自定义缩放作用于当前账户的所有屏幕，注销后生效，不是仅修改笔记本屏幕。不会自动注销。",
                "100–500%. Windows custom scaling affects all displays for this account after sign-out, not just the laptop panel. No automatic sign-out."),
            Controls(_customScale, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center }, _customScaleApply, _customScaleDisable)));
        _panelStatus.Foreground = Brush(Palette.Muted);
        rows.Children.Add(_panelStatus);
        _resolutionApply.Click += async (_, _) => await ApplyPanelChangeAsync(scale: false);
        _scaleApply.Click += async (_, _) => await ApplyPanelChangeAsync(scale: true);
        _customScaleApply.Click += async (_, _) => await ApplyCustomScaleAsync(disable: false);
        _customScaleDisable.Click += async (_, _) => await ApplyCustomScaleAsync(disable: true);
        SetPanelEnabled();
        return Card(L("笔记本屏幕", "Laptop display"), rows,
            L("调整刷新率、分辨率与 DPI 缩放；自定义缩放为系统级设置。", "Adjust refresh rate, resolution, and DPI scaling. Custom scaling is system-wide."),
            "\uE7F4", headerAction: _panelReload);
    }

    private void SetPanelEnabled()
    {
        var enabled = !_panelBusy && !_panelReading && !_customScalePending;
        _panelResolution.IsEnabled = _resolutionApply.IsEnabled = enabled && _panelLayout is not null;
        _panelScale.IsEnabled = _scaleApply.IsEnabled = enabled && _panelLayout?.Scales.Count > 0;
        _customScale.IsEnabled = _customScaleApply.IsEnabled = !_panelBusy && !_panelReading && _panelLayout is not null;
        _customScaleDisable.IsEnabled = !_panelBusy && !_panelReading;
        _panelReload.IsEnabled = !_panelBusy && !_panelReading;
    }

    private async Task ReloadPanelLayoutAsync()
    {
        if (_panelBusy || _panelReading) return;
        _panelReading = true; SetPanelEnabled();
        try
        {
            _panelLayout = await Task.Run(RefreshRateController.ReadLaptopLayout);
            _panelResolution.ItemsSource = _panelLayout.Resolutions;
            _panelResolution.SelectedItem = _panelLayout.Current;
            _panelScale.Items.Clear();
            foreach (var scale in _panelLayout.Scales) AddChoice(_panelScale, $"{scale}%", scale);
            if (_panelLayout.Scale is { } current) Select(_panelScale, current);
            var custom = CustomDisplayScaling.ReadPercent();
            SetPanelStatus(_customScalePending
                ? L("缩放配置已保存，请自行注销并重新登录后确认效果。", "Scaling saved. Sign out and back in to apply it.")
                : custom is { } percent ? L($"系统自定义缩放配置：{percent}%；固定档位需先关闭自定义并注销。", $"Custom scaling configured: {percent}%. Disable it and sign out before using per-display presets.")
                : NormalPanelStatus(_panelLayout));
        }
        catch (Exception ex)
        {
            _panelLayout = null;
            _panelResolution.ItemsSource = null; _panelScale.Items.Clear();
            SetPanelStatus(ex.GetBaseException().Message);
        }
        finally { _panelReading = false; SetPanelEnabled(); }
    }

    private async Task ApplyPanelChangeAsync(bool scale)
    {
        if (_panelBusy || _panelLayout is not { } layout) return;
        var percent = Selected<int>(_panelScale);
        var resolution = _panelResolution.SelectedItem as PanelResolution?;
        if (scale ? percent is null : resolution is null) return;
        _panelBusy = true; SetPanelEnabled();
        string? failure = null;
        try
        {
            using var change = await Task.Run(() => scale
                ? RefreshRateController.BeginLaptopScale(layout.DeviceName, percent!.Value)
                : RefreshRateController.BeginLaptopResolution(layout.DeviceName, resolution!.Value));
            var dialog = new DisplayChangeConfirmationWindow(Runtime) { Owner = Window.GetWindow(this) };
            var keep = dialog.ShowDialog() == true;
            await Task.Run(() => change.Finish(keep));
        }
        catch (Exception ex) { failure = ex.GetBaseException().Message; }
        finally { _panelBusy = false; }
        await ReloadPanelLayoutAsync();
        await _viewModel.ReloadRefreshRateAsync();
        SyncControls();
        if (failure is not null) SetPanelStatus(failure);
    }

    private async Task ApplyCustomScaleAsync(bool disable)
    {
        if (_panelBusy) return;
        int? percent = null;
        if (!disable)
        {
            if (!int.TryParse(_customScale.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value is < 100 or > 500)
            {
                SetPanelStatus(L("请输入 100–500 的整数百分比。", "Enter an integer percentage from 100 to 500."));
                return;
            }
            percent = value;
        }
        if (ToolkitMessageBox.Show(Window.GetWindow(this),
            disable ? L("关闭系统自定义缩放？这会影响当前账户的所有屏幕，需自行注销后生效。", "Disable custom scaling for all displays on this account? Sign out manually to apply it.")
                : L($"将当前账户所有屏幕的自定义缩放设为 {percent}%，而不仅是笔记本屏幕。需要注销后生效；请先保存工作。继续吗？",
                    $"Set custom scaling to {percent}% for ALL displays on this account, not only the laptop panel? Sign-out is required; save your work first. Continue?"),
            L("系统自定义缩放", "System custom scaling"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _panelBusy = true; SetPanelEnabled();
        try { CustomDisplayScaling.Save(percent); _customScalePending = true; }
        catch (Exception ex) { SetPanelStatus(ex.GetBaseException().Message); return; }
        finally { _panelBusy = false; SetPanelEnabled(); }
        await ReloadPanelLayoutAsync();
    }

    internal static string NormalPanelStatus(LaptopDisplayLayout layout) => layout.ScaleError ?? string.Empty;
    private void SetPanelStatus(string text)
    {
        _panelStatus.Text = text;
        _panelStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}

internal sealed class DisplayChangeConfirmationWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    internal DisplayChangeConfirmationWindow(ToolkitRuntimeService runtime, TimeSpan? timeoutForTesting = null)
    {
        Title = runtime.L("保留显示设置？", "Keep display settings?");
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var palette = ToolkitPalette.For(runtime.IsDark);
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Canvas));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Text));
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language); FontSize = 14;
        var root = new StackPanel { Margin = new Thickness(24) };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        root.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var undo = new Button { Content = runtime.L("还原", "Revert"), IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 0, 10, 0) };
        var keep = new Button { Content = runtime.L("保留", "Keep"), MinWidth = 100 };
        undo.Click += (_, _) => DialogResult = false; keep.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(undo); buttons.Children.Add(keep); root.Children.Add(buttons); Content = root;
        var deadline = Stopwatch.StartNew();
        var duration = timeoutForTesting ?? TimeSpan.FromSeconds(15);
        void Tick()
        {
            var remaining = (int)Math.Ceiling((duration - deadline.Elapsed).TotalSeconds);
            if (remaining <= 0) { DialogResult = false; return; }
            text.Text = runtime.L($"是否保留新的显示设置？{remaining} 秒后自动还原。", $"Keep the new display settings? Reverting in {remaining} seconds.");
        }
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { ModernTheme.RefreshWindow(this, runtime.IsDark); Tick(); _timer.Start(); undo.Focus(); };
        Closed += (_, _) => _timer.Stop();
    }
}
