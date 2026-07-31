using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class FanBackendStartupNoticeWindow : Window
{
    public FanBackendStartupNoticeWindow(
        string title,
        string content,
        string language,
        bool isDark)
    {
        var isChinese = language != "en-US";
        var palette = ToolkitPalette.For(isDark);

        Title = title;
        Width = 700;
        Height = 520;
        MinWidth = 560;
        MinHeight = 400;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = UiTypography.FontFamilyFor(language);
        FontSize = 14;
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);

        var root = new Grid
        {
            Margin = new Thickness(24)
        };
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });

        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Text),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var body = new Border
        {
            Background = Brush(palette.Surface),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20)
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = content,
                Foreground = Brush(palette.Text),
                FontSize = 15,
                LineHeight = 25,
                TextWrapping = TextWrapping.Wrap
            }
        };
        body.Child = scroll;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var confirm = new Button
        {
            Content = isChinese ? "确定" : "OK",
            MinWidth = 96,
            Height = 38,
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = true
        };
        confirm.Click += (_, _) => Close();

        var suppress = new Button
        {
            Content = isChinese
                ? "确定并不再显示"
                : "OK and don't show again",
            MinWidth = isChinese ? 154 : 190,
            Height = 38,
            Background = Brush(palette.Accent),
            BorderBrush = Brush(palette.Accent),
            Foreground = Brushes.White
        };
        suppress.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(confirm);
        buttons.Children.Add(suppress);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
