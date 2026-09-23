using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class ToolkitDialogTests
{
    internal static void Run()
    {
        var output = Path.Combine(Environment.CurrentDirectory, ".tmp", "toolkit-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        foreach (var dark in new[] { true, false })
        {
            ModernTheme.Apply(Application.Current, dark);
            var language = dark ? "zh-CN" : "en-US";
            foreach (var buttons in new[] { MessageBoxButton.OK, MessageBoxButton.OKCancel, MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
            {
                var choices = ToolkitMessageDialog.Choices(buttons);
                var expectedDefault = choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
                var message = dark ? "已停用插件并安排卸载。需要重启 Toolkit 完成清理。\n\n是否立即重启 Toolkit？这不会重启 Windows。"
                    : "The plugin has been disabled and scheduled for removal. Restart Toolkit to finish cleanup.\n\nRestart Toolkit now? Windows will not restart.";
                var dialog = new ToolkitMessageDialog(message, dark ? "重启 Toolkit" : "Restart Toolkit", buttons,
                    MessageBoxImage.Question, expectedDefault, dark, language);
                dialog.ApplyAvailableSize(new Size(1280, 800));
                var root = (FrameworkElement)dialog.Content;
                Arrange(root, 580, 720);
                Check(dialog.DefaultResult == expectedDefault && dialog.ChoiceButtons.Count == choices.Length &&
                    dialog.ChoiceButtons[expectedDefault].IsDefault && dialog.ChoiceButtons.Values.Count(b => b.IsDefault) == 1,
                    "Themed dialog changed the requested buttons/default choice.");
                Check(dialog.Result == ToolkitMessageDialog.DismissResult(buttons) && dialog.CopyText.Contains(message),
                    "Closing the dialog can approve an action or copy text is incomplete.");
                Check(Descendants(root).OfType<TextBox>().Single().IsReadOnly, "Dialog message is not selectable read-only text.");
                Capture(root, Path.Combine(output, $"{(dark ? "dark" : "light")}-{buttons}.png"));
                dialog.Close();
            }
            var longMessage = string.Join("\n", Enumerable.Repeat(dark ? "自绘页面代码在主程序中运行。请确认权限和插件来源。" : "Custom page code runs inside Toolkit. Review permissions and trust the plugin source.", 80));
            var longDialog = new ToolkitMessageDialog(longMessage, dark ? "插件权限确认" : "Plugin permission review", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning, MessageBoxResult.No, dark, language);
            longDialog.ApplyAvailableSize(new Size(420, 500));
            var longRoot = (FrameworkElement)longDialog.Content; Arrange(longRoot, 370, 410);
            var body = Descendants(longRoot).OfType<TextBox>().Single();
            Check(longDialog.Width <= 388 && longDialog.MaxHeight <= 468 && body.Text == longMessage && body.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "Long dialog content was truncated or exceeds the screen limit.");
            Capture(longRoot, Path.Combine(output, $"{(dark ? "dark" : "light")}-long.png")); longDialog.Close();

            var picker = new ToolkitColorPickerWindow("#7C9CFF", language, dark);
            var colorRoot = (FrameworkElement)picker.Content; Arrange(colorRoot, 450, 450);
            var hex = Descendants(colorRoot).OfType<TextBox>().Single();
            var confirm = Descendants(colorRoot).OfType<Button>().Single(b => b.IsDefault);
            hex.Text = "bad";
            Check(!confirm.IsEnabled && picker.SelectedHex == "7C9CFF", "Invalid color changed the result or enabled confirmation.");
            hex.Text = "#53D69A";
            Check(confirm.IsEnabled && picker.SelectedHex == "53D69A", "HEX color input failed.");
            Descendants(colorRoot).OfType<Slider>().First().Value = 255;
            Check(picker.SelectedHex == "FFD69A" && hex.Text == "#FFD69A", "RGB sliders and HEX input are not synchronized.");
            Capture(colorRoot, Path.Combine(output, $"{(dark ? "dark" : "light")}-color.png")); picker.Close();
        }
        Check(ToolkitColorPickerWindow.TryParseRgb("AABBCC", out _) && !ToolkitColorPickerWindow.TryParseRgb("##AABBCC", out _) &&
            !ToolkitColorPickerWindow.TryParseRgb("#AABBCCDD", out _), "Color validation accepted invalid RGB text.");

        ToolkitMessageBox.SetLanguage("zh-CN");
        foreach (var key in new[] { Key.Escape, Key.Enter })
        {
            var observed = false;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<ToolkitMessageDialog>().Single(w => w.IsVisible);
                var body = Descendants((DependencyObject)dialog.Content).OfType<TextBox>().Single();
                body.Focus(); observed = true;
                dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }));
            var result = ToolkitMessageBox.Show("弹窗键盘行为测试，不执行任何系统操作。", "Dialog test", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No);
            Check(observed && result == MessageBoxResult.No, "Esc/Enter or modal return handling changed the safe default.");
        }
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<ToolkitMessageDialog>().Single(w => w.IsVisible);
            dialog.ChoiceButtons[MessageBoxResult.Yes].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }));
        Check(ToolkitMessageBox.Show("只验证返回值，不执行任何操作。", "Dialog test", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
            "Explicit confirmation did not return Yes.");
        Console.WriteLine("Themed dialogs, keyboard defaults, long messages and color picker verified: " + output);
    }
    private static void Arrange(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, Math.Min(height, root.DesiredSize.Height))); root.UpdateLayout();
    }
    private static void Capture(FrameworkElement root, string path)
    {
        var width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
        var height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var window = Window.GetWindow(root) ?? throw new InvalidOperationException("Preview has no dialog window.");
        // Render the full client surface, not a margin-offset child visual.
        // Showing also flushes TextBox/Slider rendering after the input tests.
        window.WindowStyle = WindowStyle.None; window.ShowActivated = false;
        window.Show();
        if (window is ToolkitMessageDialog messageDialog)
        {
            if (messageDialog.CopyText.Length > 500) messageDialog.ApplyAvailableSize(new Size(width + 32, height + 32));
            window.Width = width;
        }
        else { window.SizeToContent = SizeToContent.Manual; window.Width = width; window.Height = height; }
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        if (window is ToolkitMessageDialog shortDialog && shortDialog.CopyText.Length < 500)
        {
            var message = Descendants(root).OfType<TextBox>().Single();
            Check(message.ExtentHeight <= message.ViewportHeight + 2, "Short messages should fit without hidden lines.");
        }
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
        window.Hide();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var nested in Descendants(VisualTreeHelper.GetChild(root, i))) yield return nested;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
