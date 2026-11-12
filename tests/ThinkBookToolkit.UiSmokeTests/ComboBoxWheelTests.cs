using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class ComboBoxWheelTests
{
    internal static void Run()
    {
        ModernTheme.Apply(Application.Current, false);
        foreach (var editable in new[] { false, true })
        {
            var selector = new ComboBox { ItemsSource = Enumerable.Range(0, 60).ToArray(),
                SelectedIndex = 10, IsEditable = editable, MaxDropDownHeight = 120, Margin = new Thickness(8) };
            // Explicit styles used by plugins must have the same input behavior.
            if (editable) selector.Style = new Style(typeof(ComboBox));
            var panel = new StackPanel();
            panel.Children.Add(selector);
            panel.Children.Add(new Border { Height = 1500 });
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window { Content = scroll, Width = 400, Height = 300 };
            try
            {
                window.Show();
                window.Activate();
                selector.Focus();
                Keyboard.Focus(selector);
                Pump(window);
                Check(selector.IsKeyboardFocusWithin, "Fixture did not focus the selector.");
                var changes = 0;
                selector.SelectionChanged += (_, _) => changes++;
                Wheel(selector, -120);
                Pump(window);
                Check(selector.SelectedIndex == 10 && changes == 0, "Scrolling a closed selector changed its setting.");
                Check(scroll.VerticalOffset > 0, "Scrolling over a closed selector stopped page scrolling.");
                Wheel(selector, 120);
                Pump(window);
                Check(selector.SelectedIndex == 10 && changes == 0, "Upward scrolling changed selection.");

                scroll.ScrollToTop();
                selector.IsDropDownOpen = true;
                Pump(window);
                var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
                var listScroll = FindScroll(popup.Child);
                Check(listScroll is not null && listScroll.ScrollableHeight > 0, "Popup has no scrollable items.");
                listScroll!.ScrollToTop();
                Pump(window);
                Wheel(listScroll, -120);
                Pump(window);
                Check(listScroll.VerticalOffset > 0 && selector.SelectedIndex == 10 && changes == 0,
                    "Expanded list scrolling was blocked or changed the saved selection.");
                selector.IsDropDownOpen = false;
            }
            finally { window.Close(); }
        }
        Console.WriteLine("Closed combo wheel suppression and expanded list/page scrolling passed.");
    }

    private static void Wheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(preview);
        // Mirror WPF's promotion of an unhandled preview input into a bubbling event.
        if (!preview.Handled)
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, preview.Timestamp, delta)
            { RoutedEvent = Mouse.MouseWheelEvent, Source = target });
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
