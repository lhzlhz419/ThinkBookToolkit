using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class ToolkitColorPickerWindow : Window
{
    private readonly Slider[] _channels = [new(), new(), new()];
    private readonly TextBlock[] _numbers = [new(), new(), new()];
    private readonly TextBox _hex = new() { MinHeight = 36, MaxLength = 7, MinWidth = 130 };
    private readonly Border _preview = new() { Height = 52, CornerRadius = new CornerRadius(8) };
    private readonly Button _accept = new() { IsDefault = true, MinWidth = 88, MinHeight = 36 };
    private readonly TextBlock _error = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly bool _chinese;
    private bool _syncing;
    internal string SelectedHex { get; private set; }

    internal ToolkitColorPickerWindow(string initialHex, string language, bool isDark)
    {
        _chinese = !language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (!TryParseRgb(initialHex, out var initial)) initial = Colors.White;
        SelectedHex = Hex(initial);
        var palette = ToolkitPalette.For(isDark);
        Title = L("选择颜色", "Choose color");
        Width = Math.Min(500, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(530, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(360, Width); MinHeight = Math.Min(340, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; UseLayoutRounding = true; SnapsToDevicePixels = true;
        FontFamily = UiTypography.FontFamilyFor(language); FontSize = 14;
        Background = Brush(palette.Canvas); Foreground = Brush(palette.Text);
        ModernTheme.ApplyWindowSurfaceStyles(this, isDark, false);
        Loaded += (_, _) => ModernTheme.RefreshWindow(this, isDark);
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var previews = new Grid(); previews.ColumnDefinitions.Add(new ColumnDefinition()); previews.ColumnDefinitions.Add(new ColumnDefinition());
        var old = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        old.Children.Add(new Border { Height = 52, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(initial) });
        old.Children.Add(new TextBlock { Text = L("原颜色", "Original"), Foreground = Brush(palette.Muted), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }); previews.Children.Add(old);
        var selected = new StackPanel { Margin = new Thickness(6, 0, 0, 0) }; selected.Children.Add(_preview);
        selected.Children.Add(new TextBlock { Text = L("新颜色", "New color"), Foreground = Brush(palette.Muted), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(selected, 1); previews.Children.Add(selected); content.Children.Add(previews);
        var swatches = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
        foreach (var hex in new[] { "FFFFFF", "000000", "7C9CFF", "53D69A", "F5B94C", "FF7B86", "A984FF", "49BCE8", "151F33", "9BAAC2" })
        {
            var swatch = new Button { Width = 31, MinWidth = 0, MinHeight = 31, Height = 31, Padding = new Thickness(0), Margin = new Thickness(0, 0, 7, 6),
                Background = Brush("#" + hex), ToolTip = "#" + hex };
            swatch.Click += (_, _) => _hex.Text = "#" + hex;
            swatches.Children.Add(swatch);
        }
        content.Children.Add(swatches);
        for (var i = 0; i < 3; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            row.Children.Add(new TextBlock { Text = new[] { L("红 R", "Red R"), L("绿 G", "Green G"), L("蓝 B", "Blue B") }[i], VerticalAlignment = VerticalAlignment.Center });
            var slider = _channels[i]; slider.Minimum = 0; slider.Maximum = 255; slider.TickFrequency = 1; slider.IsSnapToTickEnabled = true;
            slider.Margin = new Thickness(8, 0, 12, 0); Grid.SetColumn(slider, 1); row.Children.Add(slider);
            _numbers[i].TextAlignment = TextAlignment.Right; _numbers[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_numbers[i], 2); row.Children.Add(_numbers[i]); content.Children.Add(row);
            slider.ValueChanged += (_, _) => { if (!_syncing) SetColor(Color.FromRgb((byte)_channels[0].Value, (byte)_channels[1].Value, (byte)_channels[2].Value)); };
        }
        var input = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        input.Children.Add(new TextBlock { Text = "HEX", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        input.Children.Add(_hex); content.Children.Add(input);
        _error.Text = L("请输入六位十六进制颜色，例如 #7C9CFF。", "Enter six hexadecimal digits, for example #7C9CFF.");
        _error.Foreground = Brush(palette.Danger); _error.TextWrapping = TextWrapping.Wrap; content.Children.Add(_error);
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var valid = TryParseRgb(_hex.Text, out var color); _accept.IsEnabled = valid; _error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            if (valid) SetColor(color, updateText: false);
        };
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _accept.Content = L("确定", "OK"); _accept.Background = Brush(palette.Accent); _accept.Foreground = Brushes.White;
        _accept.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L("取消", "Cancel"), IsCancel = true, MinWidth = 88, MinHeight = 36, Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(_accept); buttons.Children.Add(cancel); Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        Content = root; SetColor(initial);
    }
    private void SetColor(Color color, bool updateText = true)
    {
        _syncing = true;
        try
        {
            SelectedHex = Hex(color); _preview.Background = new SolidColorBrush(color);
            var values = new[] { color.R, color.G, color.B };
            for (var i = 0; i < 3; i++) { _channels[i].Value = values[i]; _numbers[i].Text = values[i].ToString(CultureInfo.InvariantCulture); }
            if (updateText) _hex.Text = "#" + SelectedHex;
            _accept.IsEnabled = true; _error.Visibility = Visibility.Collapsed;
        }
        finally { _syncing = false; }
    }
    internal static bool TryParseRgb(string? hex, out Color color)
    {
        color = Colors.White; var value = hex?.Trim();
        if (value?.StartsWith('#') == true) value = value[1..];
        if (value?.Length != 6 || value.Any(c => !Uri.IsHexDigit(c))) return false;
        color = Color.FromRgb(Convert.ToByte(value[..2], 16), Convert.ToByte(value.Substring(2, 2), 16), Convert.ToByte(value.Substring(4, 2), 16)); return true;
    }
    private static string Hex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";
    private string L(string chinese, string english) => _chinese ? chinese : english;
    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
