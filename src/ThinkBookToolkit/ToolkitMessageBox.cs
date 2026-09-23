using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ThinkBookToolkit;

/// <summary>Toolkit-styled replacement for application message/confirmation boxes.</summary>
internal static class ToolkitMessageBox
{
    private static string _language = "zh-CN";
    internal static void SetLanguage(string language) => _language = language;

    internal static MessageBoxResult Show(string message, string caption = "ThinkBook Toolkit",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, IReadOnlyDictionary<MessageBoxResult, string>? labels = null) => Show(null, message, caption, buttons, image, defaultResult, labels);

    internal static MessageBoxResult Show(Window? owner, string message, string caption = "ThinkBook Toolkit",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, IReadOnlyDictionary<MessageBoxResult, string>? labels = null)
    {
        try
        {
            var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
                return dispatcher.CheckAccess() ? ShowCore(owner, message, caption, buttons, image, defaultResult, labels)
                    : dispatcher.Invoke(() => ShowCore(owner, message, caption, buttons, image, defaultResult, labels));
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return ShowCore(null, message, caption, buttons, image, defaultResult, labels);
            // Startup/shutdown errors can arrive before an Application exists or
            // after its dispatcher has stopped. A fresh STA can still show WPF.
            var result = ToolkitMessageDialog.DismissResult(buttons);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { result = ShowCore(null, message, caption, buttons, image, defaultResult, labels); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); thread.Join();
            if (failure is not null) throw failure;
            return result;
        }
        catch (Exception ex)
        {
            // Never recurse into the global UI exception handler, and never
            // treat a dialog which could not be shown as approval.
            ToolkitLog.Error("Toolkit message dialog could not be displayed.", ex);
            return ToolkitMessageDialog.DismissResult(buttons);
        }
    }

    private static MessageBoxResult ShowCore(Window? owner, string message, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult, IReadOnlyDictionary<MessageBoxResult, string>? labels)
    {
        if (owner is not null && (!owner.Dispatcher.CheckAccess() || !owner.IsLoaded || !owner.IsVisible)) owner = null;
        if (owner is null && Application.Current?.Dispatcher.CheckAccess() == true)
            owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is ToolkitMainWindow && w.IsVisible);
        var dialog = new ToolkitMessageDialog(message, caption, buttons, image, defaultResult, ModernTheme.CurrentIsDark, _language, labels);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }
}

internal sealed class ToolkitMessageDialog : Window
{
    private readonly bool _isDark;
    private readonly MessageBoxButton _buttons;
    private readonly TextBox _message;
    private readonly Button _copy;
    private readonly bool _chinese;
    internal MessageBoxResult Result { get; private set; }
    internal MessageBoxResult DefaultResult { get; }
    internal IReadOnlyDictionary<MessageBoxResult, Button> ChoiceButtons { get; }
    internal string CopyText => Title + Environment.NewLine + Environment.NewLine + _message.Text;

    internal ToolkitMessageDialog(string message, string caption, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult, bool isDark, string language, IReadOnlyDictionary<MessageBoxResult, string>? labels = null)
    {
        _buttons = buttons; _isDark = isDark; _chinese = !language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var choices = Choices(buttons);
        DefaultResult = choices.Contains(defaultResult) ? defaultResult : choices[0];
        Result = DismissResult(buttons);
        var palette = ToolkitPalette.For(isDark);
        Title = string.IsNullOrWhiteSpace(caption) ? "ThinkBook Toolkit" : caption;
        Width = 600; MinWidth = 320;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        FontFamily = UiTypography.FontFamilyFor(language); FontSize = 14;
        Background = Brush(palette.Canvas); Foreground = Brush(palette.Text);
        ModernTheme.ApplyWindowSurfaceStyles(this, isDark, hasCustomBackground: false);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        var accent = image switch { MessageBoxImage.Error => palette.Danger, MessageBoxImage.Warning => palette.Warning, _ => palette.Accent };
        if (image != MessageBoxImage.None)
        {
            var glyph = image switch { MessageBoxImage.Error => "\uEA39", MessageBoxImage.Warning => "\uE7BA", MessageBoxImage.Question => "\uE897", _ => "\uE946" };
            heading.Children.Add(new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Background = Brush(palette.SurfaceRaised),
                Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 22, Foreground = Brush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        }
        var title = new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 84,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = Title };
        Grid.SetColumn(title, 1); heading.Children.Add(title); root.Children.Add(heading);

        _message = new TextBox
        {
            Text = message ?? "", IsReadOnly = true, IsUndoEnabled = false, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brush(palette.Text), Padding = new Thickness(0),
            MinHeight = 38, MaxHeight = 460
        };
        var body = new Border { Background = Brush(palette.Surface), BorderBrush = Brush(palette.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = _message };
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition());
        _copy = new Button { Content = L("复制内容", "Copy text"), MinHeight = 36, Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Top };
        _copy.Click += (_, _) => CopyMessage(); footer.Children.Add(_copy);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var buttonMap = new Dictionary<MessageBoxResult, Button>();
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = labels?.TryGetValue(choice, out var label) == true ? label : choice switch { MessageBoxResult.Yes => L("是", "Yes"), MessageBoxResult.No => L("否", "No"), MessageBoxResult.Cancel => L("取消", "Cancel"), _ => L("确定", "OK") },
                Tag = choice, MinWidth = 84, MinHeight = 36, Margin = new Thickness(8, 0, 0, 6),
                IsDefault = choice == DefaultResult, IsCancel = choice == Result
            };
            if (choice == DefaultResult) { button.Background = Brush(palette.Accent); button.Foreground = Brushes.White; }
            button.Click += (_, _) => Complete(choice);
            actions.Children.Add(button); buttonMap[choice] = button;
        }
        ChoiceButtons = buttonMap;
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        SourceInitialized += (_, _) => ModernTheme.ApplyWindowTitleBar(this, _isDark);
        Loaded += (_, _) => ModernTheme.RefreshWindow(this, _isDark);
        ContentRendered += (_, _) => ChoiceButtons[DefaultResult].Focus();
        PreviewKeyDown += OnKeyDown;
        ApplyAvailableSize(new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height));
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var work = SystemParameters.WorkArea;
        ApplyAvailableSize(new Size(work.Width, work.Height));
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner is null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; }
        var reference = Owner ?? this;
        var handle = new WindowInteropHelper(reference).Handle;
        if (handle != IntPtr.Zero)
        {
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(reference);
            ApplyAvailableSize(new Size(work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY));
        }
    }
    internal void ApplyAvailableSize(Size workArea)
    {
        var availableWidth = Math.Max(240, workArea.Width - 32);
        MaxWidth = availableWidth; MinWidth = Math.Min(320, availableWidth); Width = Math.Min(600, availableWidth);
        MaxHeight = Math.Max(200, workArea.Height - 32);
        if (_message is not null) _message.MaxHeight = Math.Max(48, MaxHeight - 244);
    }
    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { args.Handled = true; Complete(DismissResult(_buttons)); }
        else if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not Button)
        { args.Handled = true; Complete(DefaultResult); }
        else if (args.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _message.SelectionLength == 0)
        { args.Handled = true; CopyMessage(); }
    }
    private void CopyMessage()
    {
        try { Clipboard.SetText(CopyText); _copy.Content = L("已复制", "Copied"); }
        catch { _copy.Content = L("复制失败", "Copy failed"); }
    }
    internal void Complete(MessageBoxResult result) { Result = result; Close(); }
    internal static MessageBoxResult[] Choices(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => [MessageBoxResult.OK], MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
        MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
        MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
        _ => throw new ArgumentOutOfRangeException(nameof(buttons))
    };
    internal static MessageBoxResult DismissResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => MessageBoxResult.OK, MessageBoxButton.YesNo => MessageBoxResult.No, _ => MessageBoxResult.Cancel
    };
    private string L(string chinese, string english) => _chinese ? chinese : english;
    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
