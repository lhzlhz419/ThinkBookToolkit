using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ThinkBookToolkit;

internal sealed partial class ToolkitDisplayPage
{
    private sealed class ExtraCard(bool oled)
    {
        internal bool Oled { get; } = oled;
        internal Border Card = null!;
        internal readonly TextBlock Status = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        internal readonly TextBlock Hint = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        internal readonly Dictionary<string, (FrameworkElement Row, Control Editor)> Rows = new();
        internal readonly Dictionary<string, Expander> Advanced = new();
        internal FrameworkElement? UnitsRow, PowerSettings;
        internal bool Imperial;
        internal DisplayExtraState? State;
        internal bool Syncing, Writing;
        internal int Generation;
    }
    private readonly ExtraCard _presenceCard = new(false), _oledCard = new(true);
    private bool _extrasDisposed;

    private void BuildExtraCard(Panel root, ExtraCard group)
    {
        var body = new StackPanel();
        var definitions = group.Oled ? new[] {
            ("TaskbarDimmer", "任务栏调暗", "Dim taskbar"), ("BackgroundDimmer", "背景窗口调暗", "Dim background windows"),
            ("DisplayDimmer", "整屏调暗", "Dim entire screen") } : new[] {
            ("Approach", "靠近时唤醒并通过 Windows Hello 登录", "Wake and sign in with Windows Hello on approach"),
            ("AutoAdjust", "距离灵敏度调整", "Detection distance adjustment"),
            ("ApproachDistance", "最大检测距离", "Maximum detection distance"),
            ("PresentLeave", "离开时自动锁屏", "Lock when you leave"),
            ("LeaveWait", "自动屏幕锁定计时器", "Automatic screen lock timer"),
            ("CameraAssist", "面部识别", "Facial recognition"),
            ("Video", "零触摸视频播放", "Pause and resume video automatically") };
        foreach (var (key, cn, en) in definitions)
        {
            Control editor;
            if (group.Oled || key is "ApproachDistance" or "AutoAdjust" or "LeaveWait")
            {
                var combo = new ComboBox { MinWidth = 150 };
                if (key == "AutoAdjust")
                {
                    AddChoice(combo, L("自动", "Automatic"), 1);
                    AddChoice(combo, L("手动", "Manual"), 0);
                }
                if (group.Oled) foreach (var seconds in DisplayExtras.Timers)
                    AddChoice(combo, seconds switch { -1 => L("始终调暗", "Always dim"), 0 => L("永不调暗", "Never dim"),
                        30 => L("闲置 30 秒", "Idle for 30 seconds"), _ => L($"闲置 {seconds / 60} 分钟", $"Idle for {seconds / 60} min") }, seconds);
                combo.SelectionChanged += async (_, _) =>
                {
                    if (!group.Syncing && Selected<int>(combo) is int value) await ChangeExtra(group, key, value);
                };
                editor = combo;
            }
            else
            {
                var toggle = new CheckBox();
                toggle.Click += async (_, _) => await ChangeExtra(group, key, toggle.IsChecked == true ? 1 : 0);
                editor = toggle;
            }
            var description = key switch
            {
                "LeaveWait" => L("此设置会覆盖 Windows 的屏幕和睡眠设置。", "This overrides the Windows screen and sleep timeout."),
                "CameraAssist" => L("使用摄像头在设备上识别人脸，减少意外锁屏。需要摄像头权限。", "Recognize faces on this device to reduce unintended locks. Requires camera access."),
                _ => ""
            };
            var row = SettingRow(L(cn, en), description, editor);
            group.Rows.Add(key, (row, editor));
            row.Visibility = Visibility.Collapsed;
            var parent = key switch { "AutoAdjust" or "ApproachDistance" => "Approach", "LeaveWait" or "CameraAssist" => "PresentLeave", _ => null };
            if (parent is null) { body.Children.Add(row); continue; }
            if (!group.Advanced.TryGetValue(parent, out var expander))
            {
                expander = new Expander { Header = L("高级设置", "Advanced settings"), Content = new StackPanel(),
                    Margin = new Thickness(12, 0, 0, 8), Visibility = Visibility.Collapsed };
                group.Advanced[parent] = expander;
                body.Children.Add(expander);
            }
            var advanced = (StackPanel)expander.Content;
            advanced.Children.Add(row);
            if (key == "ApproachDistance")
            {
                var units = new ComboBox { MinWidth = 100 };
                AddChoice(units, L("公制（米）", "Metric (m)"), 0);
                AddChoice(units, L("英制（英尺）", "Imperial (ft)"), 1);
                units.SelectedIndex = 0;
                units.SelectionChanged += (_, _) =>
                {
                    group.Imperial = units.SelectedIndex == 1;
                    if (group.State is { } state && !group.Syncing)
                    {
                        group.Syncing = true;
                        try { UpdatePresenceChoices(group, state, key, (ComboBox)editor); }
                        finally { group.Syncing = false; }
                    }
                };
                advanced.Children.Add(group.UnitsRow = SettingRow(L("距离单位", "Distance units"), "", units));
            }
            if (key == "LeaveWait")
            {
                var link = new Button { Content = L("Windows 电源和电池设置", "Windows power and sleep settings"),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
                link.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); }
                    catch (Exception ex) { group.Status.Text = ex.Message; group.Status.Visibility = Visibility.Visible; }
                };
                advanced.Children.Add(group.PowerSettings = link);
            }
        }
        body.Children.Add(group.Hint);
        group.Card = Card(group.Oled ? L("OLED屏幕保护", "OLED screen protection") : L("智能感知", "Intelligent sensing"), body);
        group.Card.Visibility = Visibility.Collapsed;
        root.Children.Add(group.Card);
        root.Children.Add(group.Status);
    }

    private void InvalidateExtras()
    {
        foreach (var group in new[] { _presenceCard, _oledCard })
        {
            Interlocked.Increment(ref group.Generation);
            group.State = null;
            group.Writing = false;
            group.Card.Visibility = group.Status.Visibility = Visibility.Collapsed;
        }
    }

    public override void Dispose()
    {
        _extrasDisposed = true;
        InvalidateExtras();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _displayChangeTimer.Stop();
        _displayChangeTimer.Tick -= OnDisplayChangeTimerTick;
        UnsubscribeFromDisplayChanges();
        base.Dispose();
    }

    private Task RefreshExtraCards() => Task.WhenAll(RefreshExtra(_presenceCard), RefreshExtra(_oledCard));

    private async Task RefreshExtra(ExtraCard group)
    {
        if (group.Writing || _extrasDisposed || !IsLoaded) return;
        var generation = Interlocked.Increment(ref group.Generation);
        var state = await DisplayExtras.Shared.ReadAsync(group.Oled);
        if (generation != group.Generation || !IsLoaded) return;
        var error = group.State?.Available == true && !state.Available ? state.Detail : null;
        RenderExtra(group, state, error);
    }

    private async Task ChangeExtra(ExtraCard group, string key, int value)
    {
        if (group.Syncing || group.Writing || group.State is not { } previous ||
            !previous.Values.TryGetValue(key, out var row) || row.Value == value || !row.Writable) return;
        var generation = Interlocked.Increment(ref group.Generation);
        group.Writing = true;
        group.Card.IsEnabled = false;
        var (actual, error) = await DisplayExtras.Shared.SetAsync(previous.Source, key, value,
            () => Volatile.Read(ref group.Generation) == generation);
        if (error is not null) ToolkitLog.Warning($"Display {previous.Source}/{key} requested={value}: {error}");
        if (generation != group.Generation || !IsLoaded) return;
        group.Writing = false;
        RenderExtra(group, actual, error);
    }

    private void RenderExtra(ExtraCard group, DisplayExtraState state, string? error)
    {
        group.State = state;
        group.Syncing = true;
        try
        {
            group.Card.IsEnabled = true;
            foreach (var (key, (row, editor)) in group.Rows)
            {
                var visible = state.Values.TryGetValue(key, out var value);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                editor.IsEnabled = value?.Writable == true;
                if (editor is CheckBox toggle) toggle.IsChecked = value?.Value == 1;
                if (editor is ComboBox combo)
                {
                    if (key is "ApproachDistance" or "LeaveWait") UpdatePresenceChoices(group, state, key, combo);
                    else Select(combo, value?.Value ?? int.MinValue);
                }
            }
            if (!group.Oled)
            {
                Visibility Show(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
                group.Advanced["Approach"].Visibility = Show(state.Values.ContainsKey("ApproachDistance") || state.Values.ContainsKey("AutoAdjust"));
                group.Advanced["PresentLeave"].Visibility = Show(state.Values.ContainsKey("LeaveWait") || state.Values.ContainsKey("CameraAssist"));
                group.UnitsRow!.Visibility = Show(state.Values.ContainsKey("ApproachDistance") && state.DistanceMillimeters is not null);
                group.PowerSettings!.Visibility = Show(state.Values.ContainsKey("LeaveWait"));
            }
            var hints = new List<string>();
            if (state.NeedsHello && state.Values.ContainsKey("Approach"))
                hints.Add(L("请在 Windows 设置 → 账户 → 登录选项中设置人脸识别。", "Set up facial recognition in Windows Settings → Accounts → Sign-in options."));
            if (state.CameraBlocked) hints.Add(L("请检查摄像头遮挡和摄像头权限。", "Check the camera shutter and camera permissions."));
            if (group.Oled && state.Values.TryGetValue("TaskbarDimmer", out var taskbar) && !taskbar.Writable)
                hints.Add(L("当前主题或面板设置限制了任务栏调暗。", "The current theme or panel settings restrict taskbar dimming."));
            group.Hint.Text = string.Join(Environment.NewLine, hints);
            group.Hint.Visibility = hints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            group.Card.Visibility = state.Available ? Visibility.Visible : Visibility.Collapsed;
            group.Status.Text = error is null ? "" : error + L(" 可重新检测后刷新。", " Refresh to check again.");
            group.Status.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { group.Syncing = false; }
    }

    private void UpdatePresenceChoices(ExtraCard group, DisplayExtraState state, string key, ComboBox combo)
    {
        combo.Items.Clear();
        if (!state.Values.TryGetValue(key, out var current)) return;
        var distance = key == "ApproachDistance";
        var labels = distance ? new[] { L("近", "Near"), L("中", "Medium"), L("远", "Far") } :
            new[] { L("快", "Fast"), L("中", "Medium"), L("慢", "Slow") };
        if (!distance && state.LockSeconds is { } times)
            labels = times.Select(seconds => L($"{seconds} 秒", $"{seconds} s")).ToArray();
        for (int i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (distance && state.DistanceMillimeters is { } mm)
                label += group.Imperial ? L($"（最多 {mm[i] / 304.8:0.0} 英尺）", $" (up to {mm[i] / 304.8:0.0} ft)") :
                    L($"（最多 {mm[i] / 1000d:0.##} 米）", $" (up to {mm[i] / 1000d:0.##} m)");
            AddChoice(combo, label, i);
        }
        Select(combo, current.Value);
    }
}
