using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThinkBookToolkit.PluginUi;

/// <summary>Optional WPF helpers; uses the host's actual palette, including alpha.</summary>
public static class PluginControls
{
    public static Border Card(PluginTheme theme, string title, UIElement content)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Foreground = Brush(theme.Text), FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(content);
        return new Border { Background = Brush(theme.Surface), BorderBrush = Brush(theme.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12), Child = body };
    }

    public static Border SettingRow(PluginTheme theme, string title, string description, FrameworkElement control)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(new TextBlock { Text = title, Foreground = Brush(theme.Text), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        labels.Children.Add(new TextBlock { Text = description, Foreground = Brush(theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(labels); grid.Children.Add(control); Grid.SetColumn(control, 1);
        grid.SizeChanged += (_, _) =>
        {
            var narrow = grid.ActualWidth < 500;
            Grid.SetColumn(control, narrow ? 0 : 1); Grid.SetRow(control, narrow ? 1 : 0); Grid.SetColumnSpan(control, narrow ? 2 : 1);
            Grid.SetColumnSpan(labels, narrow ? 2 : 1); control.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        };
        return new Border { Background = Brush(theme.SurfaceRaised), BorderBrush = Brush(theme.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 8), Child = grid };
    }

    public static TextBlock Status(PluginTheme theme, string text) => new() { Text = text, Foreground = Brush(theme.Muted), TextWrapping = TextWrapping.Wrap };
    public static DataGrid Table(IEnumerable rows) => new() { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, MaxHeight = 400 };
    public static FrameworkElement Sparkline(PluginTheme theme, IReadOnlyList<double?> samples, double? minimum = null, double? maximum = null)
    {
        var canvas = new Canvas { Height = 100, ClipToBounds = true };
        void Render()
        {
            canvas.Children.Clear();
            var values = samples.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            if (values.Length == 0 || samples.Count < 2) return;
            var low = minimum ?? values.Min(); var high = maximum ?? values.Max();
            if (!double.IsFinite(low) || !double.IsFinite(high) || high < low) return;
            if (high == low) high = low + 1;
            Polyline? line = null;
            for (var i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } v || !double.IsFinite(v)) { line = null; continue; }
                if (line is null) { line = new Polyline { Stroke = Brush(theme.Accent), StrokeThickness = 2 }; canvas.Children.Add(line); }
                line.Points.Add(new Point(i * canvas.ActualWidth / (samples.Count - 1), canvas.Height * (1 - Math.Clamp((v - low) / (high - low), 0, 1))));
            }
        }
        canvas.SizeChanged += (_, _) => Render(); return canvas;
    }
    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
}
