using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginUi;

namespace ThinkBookToolkit.PluginTest.Ui;

/// <summary>A real custom WPF page; no Toolkit implementation assembly reference.</summary>
public sealed class AverageFanPage : IToolkitPluginPage
{
    private IPluginPageContext? _context;
    private readonly StackPanel _root = new();
    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _intro = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 18) };
    private readonly CheckBox _enabled = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _description = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly UniformGrid _readings = new() { Columns = 3, Margin = new Thickness(0, 16, -10, 0) };
    private readonly List<Border> _cards = [];
    private readonly TextBlock[] _labels = [new(), new(), new()];
    private readonly TextBlock[] _values = [new(), new(), new()];
    private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _disposed, _saving, _chinese;
    private bool? _dark;

    public FrameworkElement CreateView(IPluginPageContext context)
    {
        _context = context;
        _root.Children.Add(_title); _root.Children.Add(_intro);
        var settings = new StackPanel();
        settings.Children.Add(_enabled); settings.Children.Add(_description); settings.Children.Add(_status); settings.Children.Add(_error);
        _root.Children.Add(Card(settings));
        for (var i = 0; i < 3; i++)
        {
            _labels[i].FontSize = 12;
            _values[i].FontSize = 24; _values[i].FontWeight = FontWeights.SemiBold;
            _values[i].Margin = new Thickness(0, 10, 0, 0);
            var body = new StackPanel(); body.Children.Add(_labels[i]); body.Children.Add(_values[i]);
            var card = Card(body); card.Margin = new Thickness(0, 0, 10, 10); card.MinHeight = 104;
            _readings.Children.Add(card);
        }
        _root.Children.Add(_readings); _root.Children.Add(_note);
        _enabled.Click += OnToggle;
        _root.SizeChanged += OnSizeChanged;
        Update(context.State);
        return _root;
    }

    public void Update(PluginPageState state)
    {
        if (_disposed) return;
        _chinese = !state.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        _title.Text = L("风扇转速预览", "Fan speed preview");
        _intro.Text = L("此页面由测试插件自行绘制，数据来自 Toolkit 的传感器接口。", "This page is drawn by the test plugin using Toolkit sensor data.");
        _enabled.Content = L("显示平均风扇转速", "Show average fan speed");
        _description.Text = L("默认关闭。开启后，将“平均转速”加入完整概览、OSD、传感器记录和数据共享。", "Off by default. Enable to publish average speed to the detailed overview, OSD, recordings and data sharing.");
        var enabled = state.Request.PluginSettings.TryGetValue("show-average", out var setting) && setting.ValueKind == JsonValueKind.True;
        _enabled.IsChecked = enabled; _enabled.IsEnabled = !_saving;
        _status.Text = _saving ? L("正在保存…", "Saving…") : enabled ? L("已启用 · 平均转速正在发布", "Enabled · Publishing average speed") : L("已关闭 · 不添加平均转速读数", "Disabled · No average reading is published");
        _labels[0].Text = L("风扇1转速", "Fan 1 speed"); _labels[1].Text = L("风扇2转速", "Fan 2 speed"); _labels[2].Text = L("平均转速", "Average fan speed");
        _values[0].Text = Format(Read(state.Request.Context, "toolkit.sensor.fan1Rpm"));
        _values[1].Text = Format(Read(state.Request.Context, "toolkit.sensor.fan2Rpm"));
        _values[2].Text = enabled ? Format(AverageFanPlugin.AverageRpm(state.Request.Context)) : "--";
        _readings.Children[1].Visibility = state.Request.Context.Sensors.ContainsKey("toolkit.sensor.fan2Rpm") ? Visibility.Visible : Visibility.Collapsed;
        ArrangeReadings();
        _note.Text = L("停转的 0 转参与平均计算；缺失、过期和无效读数不参与。本插件不改变风扇控制。", "Stopped fans (0 RPM) count toward the average; missing, stale and invalid readings do not. This plugin does not control fans.");
        if (_dark != state.IsDark)
        {
            _dark = state.IsDark;
            var text = Brush(state.IsDark ? "#F7F9FC" : "#172033");
            var muted = Brush(state.IsDark ? "#9BAAC2" : "#6D7A90");
            _title.Foreground = text; _enabled.Foreground = text;
            foreach (var label in _labels.Append(_intro).Append(_description).Append(_note)) label.Foreground = muted;
            foreach (var value in _values) value.Foreground = text;
            foreach (var card in _cards)
            {
                card.Background = Brush(state.IsDark ? "#59151F33" : "#40FFFFFF");
                card.BorderBrush = Brush(state.IsDark ? "#2A3852" : "#E2E9F2");
            }
            _error.Foreground = Brush(state.IsDark ? "#FF7B86" : "#D94A59");
        }
        _status.Foreground = Brush(enabled ? state.IsDark ? "#53D69A" : "#168354" : state.IsDark ? "#9BAAC2" : "#6D7A90");
    }

    private async void OnToggle(object sender, RoutedEventArgs args)
    {
        var context = _context;
        if (_disposed || _saving || context is null || context.Lifetime.IsCancellationRequested) return;
        var enabled = _enabled.IsChecked == true;
        _saving = true; _enabled.IsEnabled = false; _error.Visibility = Visibility.Collapsed;
        try { await context.SetSettingAsync("show-average", JsonSerializer.SerializeToElement(enabled)); }
        catch (Exception ex)
        {
            if (!_disposed && !context.Lifetime.IsCancellationRequested)
            { _error.Text = L("保存失败：", "Save failed: ") + ex.GetBaseException().Message; _error.Visibility = Visibility.Visible; }
        }
        finally
        {
            _saving = false;
            if (!_disposed && !context.Lifetime.IsCancellationRequested) Update(context.State);
        }
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => ArrangeReadings();
    private void ArrangeReadings() => _readings.Columns = _root.ActualWidth < 660 ? 1 : _readings.Children.OfType<UIElement>().Count(c => c.Visibility == Visibility.Visible);
    private Border Card(UIElement content)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(18), Child = content };
        _cards.Add(card); return card;
    }
    private static double? Read(PluginContext context, string id) => context.Sensors.TryGetValue(id, out var reading) &&
        reading.Quality == "valid" && reading.Value is >= 0 && double.IsFinite(reading.Value.Value) ? reading.Value : null;
    private string Format(double? value) => value.HasValue ? value.Value.ToString("0.##") + L(" 转", " RPM") : "--";
    private string L(string chinese, string english) => _chinese ? chinese : english;
    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _enabled.Click -= OnToggle; _root.SizeChanged -= OnSizeChanged; _context = null;
    }
}
