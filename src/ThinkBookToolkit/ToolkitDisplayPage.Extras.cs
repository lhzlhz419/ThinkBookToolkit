using System;
using System.Collections.Generic;
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
            ("PresentLeave", "离开时自动锁屏", "Lock when you leave"), ("Video", "零触摸视频播放", "Pause and resume video automatically") };
        foreach (var (key, cn, en) in definitions)
        {
            Control editor;
            if (group.Oled)
            {
                var combo = new ComboBox { MinWidth = 150 };
                foreach (var seconds in DisplayExtras.Timers)
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
            var row = SettingRow(L(cn, en), "", editor);
            group.Rows.Add(key, (row, editor));
            row.Visibility = Visibility.Collapsed;
            body.Children.Add(row);
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
                if (editor is ComboBox combo) Select(combo, value?.Value ?? int.MinValue);
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
}
