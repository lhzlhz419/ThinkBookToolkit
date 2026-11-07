using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class ThemeColorSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private ThemeColorSettings _draft;
    private readonly List<Action> _refresh = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };

    internal ThemeColorSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _draft = ThemeColorSettings.Normalize(runtime.Settings.ThemeColors);
        var palette = ToolkitPalette.For(runtime.IsDark);
        Title = L("主题颜色", "Theme colors");
        Width = Math.Min(660, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(750, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(360, Width);
        MinHeight = Math.Min(380, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);
        ModernTheme.ApplyWindowSurfaceStyles(this, runtime.IsDark, false);
        Loaded += (_, _) => ModernTheme.RefreshWindow(this, runtime.IsDark);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = L("分别设置浅色和深色外观。选择颜色后可预览，点击“应用”保存。", "Customize light and dark appearances. Preview your colors, then apply to save."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brush(palette.Muted), Margin = new Thickness(0, 8, 0, 18)
        });
        foreach (var dark in new[] { false, true })
        {
            content.Children.Add(new TextBlock { Text = dark ? L("深色模式", "Dark mode") : L("浅色模式", "Light mode"),
                FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) });
            content.Children.Add(ColorRow(L("强调色", "Accent color"),
                () => dark ? _draft.DarkAccent : _draft.LightAccent,
                color => _draft = dark ? _draft with { DarkAccent = color } : _draft with { LightAccent = color }));
            content.Children.Add(ColorRow(L("背景主题色", "Background theme color"),
                () => dark ? _draft.DarkBackground : _draft.LightBackground,
                color => _draft = dark ? _draft with { DarkBackground = color } : _draft with { LightBackground = color }));
            content.Children.Add(ColorRow(L("概览顶部 · 渐变起始色", "Overview header · Gradient start"),
                () => dark ? _draft.DarkHeroStart : _draft.LightHeroStart,
                color => _draft = dark ? _draft with { DarkHeroStart = color } : _draft with { LightHeroStart = color }));
            content.Children.Add(ColorRow(L("概览顶部 · 渐变结束色", "Overview header · Gradient end"),
                () => dark ? _draft.DarkHeroEnd : _draft.LightHeroEnd,
                color => _draft = dark ? _draft with { DarkHeroEnd = color } : _draft with { LightHeroEnd = color }));
            content.Children.Add(Preview(dark));
        }
        _error.Foreground = Brush(palette.Danger);
        content.Children.Add(_error);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var reset = Button(L("恢复默认", "Restore defaults"));
        reset.Click += (_, _) => SetDraft(new ThemeColorSettings());
        var cancel = Button(L("取消", "Cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        var apply = Button(L("应用", "Apply")); apply.IsDefault = true;
        apply.Click += (_, _) =>
        {
            if (runtime.TrySetThemeColors(_draft, out var error)) DialogResult = true;
            else _error.Text = error ?? "";
        };
        footer.Children.Add(reset); footer.Children.Add(cancel); footer.Children.Add(apply);
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
        RefreshPreviews();
    }

    internal void SetDraft(ThemeColorSettings colors)
    {
        _draft = ThemeColorSettings.Normalize(colors);
        RefreshPreviews();
    }

    private void RefreshPreviews()
    {
        _error.Text = "";
        foreach (var update in _refresh) update();
    }

    private FrameworkElement ColorRow(string title, Func<string> read, Action<string> write)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var swatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        var hex = new TextBlock { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(swatch); label.Children.Add(hex);
        var choose = Button(title); choose.Content = label; choose.ToolTip = title;
        choose.Click += (_, _) =>
        {
            var picker = new ToolkitColorPickerWindow(read(), _runtime.Settings.Language, _runtime.IsDark) { Owner = this };
            if (picker.ShowDialog() != true) return;
            write(picker.SelectedHex);
            RefreshPreviews();
        };
        _refresh.Add(() => { swatch.Background = Brush("#" + read()); hex.Text = "#" + read(); });
        Grid.SetColumn(choose, 1); row.Children.Add(choose);
        return row;
    }

    private FrameworkElement Preview(bool dark)
    {
        var headerText = new TextBlock { Text = L("设备控制中心", "DEVICE CONTROL CENTER"), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap };
        var header = new Border { Child = headerText, Padding = new Thickness(16), CornerRadius = new CornerRadius(10) };
        var title = new TextBlock { Text = L("设置卡片", "Settings card"), FontWeight = FontWeights.SemiBold };
        var description = new TextBlock { Text = L("正文与说明文字随背景调整", "Text adapts to the background"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 10) };
        var input = new TextBox { Text = L("示例输入框", "Example input"), IsReadOnly = true, MinWidth = 120,
            IsTabStop = false, IsHitTestVisible = false, Margin = new Thickness(0, 0, 10, 6) };
        var button = Button(L("示例按钮", "Example button"));
        button.IsTabStop = false; button.IsHitTestVisible = false;
        var controls = new WrapPanel(); controls.Children.Add(input); controls.Children.Add(button);
        var cardContent = new StackPanel(); cardContent.Children.Add(title); cardContent.Children.Add(description); cardContent.Children.Add(controls);
        var card = new Border { Child = cardContent, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0) };
        var sideLabel = new TextBlock { Text = L("概览", "Overview"), TextWrapping = TextWrapping.Wrap };
        var side = new Border { Child = sideLabel, CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(side); panel.Children.Add(card);
        var surface = new Border { Child = panel, Padding = new Thickness(12), CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 8, 0, 14) };
        _refresh.Add(() =>
        {
            var palette = ToolkitPalette.For(dark, _draft);
            var colors = _draft.Hero(dark);
            surface.Background = Brush(palette.Canvas);
            side.Background = Brush(palette.Sidebar);
            sideLabel.Foreground = Brush(palette.Accent);
            card.Background = Brush(palette.Surface);
            card.BorderBrush = Brush(palette.Border);
            title.Foreground = Brush(palette.Text);
            description.Foreground = Brush(palette.Muted);
            ModernTheme.ApplyColorPreview(input, button, palette);
            header.Background = new LinearGradientBrush(Color(colors.Start), Color(colors.End), 0);
            headerText.Foreground = Brush(_draft.HeroText(dark));
        });
        return surface;
    }

    private static Button Button(string label) => new()
    {
        Content = label, MinWidth = 90, MinHeight = 36, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 3, 0, 3)
    };
    private string L(string chinese, string english) => _runtime.L(chinese, english);
    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Brush(string hex) => new(Color(hex));
}
