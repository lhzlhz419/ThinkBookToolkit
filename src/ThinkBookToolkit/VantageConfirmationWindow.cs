using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class VantageConfirmationWindow : Window
{
    private bool _confirmed;

    private VantageConfirmationWindow(
        Window owner,
        string title,
        string message,
        string cancelText,
        string actionText,
        bool isDark)
    {
        Owner = owner;
        Title = title;
        Width = 600;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        Background = Brush(isDark ? "#18181b" : "#ffffff");
        Foreground = Brush(isDark ? "#e5e7eb" : "#111827");

        var heading = new TextBlock
        {
            Text = title,
            FontSize = FontSize + 8,
            FontWeight = FontWeights.Bold
        };
        var body = new TextBlock
        {
            Text = message,
            FontSize = FontSize + 1,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 30, 0, 28)
        };
        var cancel = new Button
        {
            Content = cancelText,
            MinWidth = 110,
            Height = 42,
            IsCancel = true,
            Background = Brush(isDark ? "#34343c" : "#e5e7eb"),
            Foreground = Foreground
        };
        var action = new Button
        {
            Content = actionText,
            MinWidth = 145,
            Height = 42,
            Margin = new Thickness(12, 0, 0, 0),
            Background = Brush("#86bdf8"),
            Foreground = Brush("#111827"),
            IsDefault = true
        };
        action.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(action);
        var root = new StackPanel { Margin = new Thickness(30) };
        root.Children.Add(heading);
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
    }

    public static bool Show(
        Window owner,
        string title,
        string message,
        string cancelText,
        string actionText,
        bool isDark)
    {
        var dialog = new VantageConfirmationWindow(
            owner, title, message, cancelText, actionText, isDark);
        dialog.ShowDialog();
        return dialog._confirmed;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
