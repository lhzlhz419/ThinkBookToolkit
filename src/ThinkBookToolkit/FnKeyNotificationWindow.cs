using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class FnKeyNotificationWindow : UiAccessOverlayWindow
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _message;

    internal FnKeyNotificationWindow(bool isDark)
    {
        var palette = ToolkitPalette.For(isDark);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var text = new StackPanel
        {
            Margin = new Thickness(18, 13, 18, 13),
            MinWidth = 250,
            MaxWidth = 430
        };
        _message = new TextBlock
        {
            Foreground = Brush(palette.Text),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        text.Children.Add(_message);
        Content = new Border
        {
            Background = Brush(palette.Surface),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = isDark ? 0.48 : 0.22
            },
            Child = text
        };

        SetOverlayClickThrough(true);
        Loaded += (_, _) => PositionWindow();
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.8)
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };
        Closed += (_, _) => _timer.Stop();
    }

    internal void ShowTemporarily(string title, string detail)
    {
        _message.Text = string.Join(
            " ",
            new[] { title, detail }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!IsVisible)
            Show();
        UpdateLayout();
        PositionWindow();
        EscalateZOrder();
        _timer.Stop();
        _timer.Start();
    }

    private void PositionWindow()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Bottom - ActualHeight - 54;
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

}
