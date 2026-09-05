using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal static class ModernTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private static readonly DependencyProperty UsesManagedThemeStyleProperty =
        DependencyProperty.RegisterAttached(
            "UsesManagedThemeStyle",
            typeof(bool),
            typeof(ModernTheme),
            new PropertyMetadata(false));
    private static bool _windowThemeHandlerRegistered;
    private static bool _isDark;

    public static void Apply(Application application, bool isDark)
    {
        _isDark = isDark;
        EnsureWindowThemeHandler();
        var palette = ToolkitPalette.For(isDark);
        application.Resources[typeof(Button)] = ButtonStyle(palette);
        application.Resources[typeof(TextBox)] = TextBoxStyle(palette);
        application.Resources[typeof(ComboBox)] = ComboBoxStyle(palette);
        application.Resources[typeof(ComboBoxItem)] = ComboBoxItemStyle(palette);
        application.Resources[typeof(CheckBox)] = SwitchStyle(palette);
        application.Resources[typeof(RadioButton)] = ToggleStyle<RadioButton>(palette);
        application.Resources[typeof(TabControl)] = TabControlStyle(palette);
        application.Resources[typeof(TabItem)] = TabItemStyle(palette);
        application.Resources[typeof(Slider)] = SliderStyle(palette);
        application.Resources[typeof(ProgressBar)] = ProgressBarStyle(palette);
        application.Resources[typeof(ScrollBar)] = ScrollBarStyle(palette);
        application.Resources[typeof(ToolTip)] = ToolTipStyle(palette);
        foreach (Window window in application.Windows)
            RefreshWindow(window, isDark);
    }

    /// <summary>
    /// Reconnects controls which have already been created to the current
    /// application theme and applies the matching native window chrome.
    /// WPF can otherwise retain an implicit style resolved before a system
    /// theme change, producing a dark page with light selectors.
    /// </summary>
    internal static void RefreshWindow(Window window, bool isDark)
    {
        ApplyManagedControlStyles(window);
        ApplyWindowTitleBar(window, isDark);

        if (window.Dispatcher.HasShutdownStarted)
            return;

        // Windows may initialize or repaint the non-client area after Loaded.
        // Apply the DWM attributes once more after that work has completed.
        _ = window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ApplyWindowTitleBar(window, isDark)));
    }

    internal static void ApplyWindowSurfaceStyles(
        Window window,
        bool isDark,
        bool hasCustomBackground)
    {
        var standard = ToolkitPalette.For(isDark);
        var surfaces = ToolkitPalette.For(isDark, hasCustomBackground);
        // The closed controls should reveal the wallpaper, while popup
        // surfaces stay solid so list items remain readable over other apps.
        var controls = surfaces with { Surface = standard.Surface };
        window.Resources[typeof(Button)] = ButtonStyle(controls);
        window.Resources[typeof(TextBox)] = TextBoxStyle(controls);
        window.Resources[typeof(ComboBox)] = ComboBoxStyle(controls);
        window.Resources[typeof(ComboBoxItem)] = ComboBoxItemStyle(controls);
        window.Resources[typeof(TabControl)] = TabControlStyle(controls);
        window.Resources[typeof(TabItem)] = TabItemStyle(controls);
        window.Resources[typeof(Slider)] = SliderStyle(controls);
        window.Resources[typeof(ToolTip)] = ToolTipStyle(standard);
    }

    internal static void ApplyWindowTitleBar(Window window, bool isDark)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var dark = isDark ? 1 : 0;
        if (DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref dark,
                sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkModeLegacy,
                ref dark,
                sizeof(int));
        }

        var palette = ToolkitPalette.For(isDark);
        SetWindowColor(handle, DwmwaCaptionColor, palette.Canvas);
        SetWindowColor(handle, DwmwaTextColor, palette.Text);
        SetWindowColor(handle, DwmwaBorderColor, palette.Border);
    }

    private static void EnsureWindowThemeHandler()
    {
        if (_windowThemeHandlerRegistered)
            return;
        _windowThemeHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                    RefreshWindow(window, _isDark);
            }));
    }

    private static void ApplyManagedControlStyles(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;

            ApplyManagedControlStyle(current);

            foreach (var child in LogicalTreeHelper.GetChildren(current)
                         .OfType<DependencyObject>())
            {
                pending.Push(child);
            }

            if (current is not Visual)
                continue;

            for (var index = 0;
                 index < VisualTreeHelper.GetChildrenCount(current);
                 index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static void ApplyManagedControlStyle(DependencyObject element)
    {
        if (element is not FrameworkElement frameworkElement)
            return;

        object? resourceKey = element switch
        {
            ComboBox => typeof(ComboBox),
            ComboBoxItem => typeof(ComboBoxItem),
            TextBox => typeof(TextBox),
            Button => typeof(Button),
            CheckBox => typeof(CheckBox),
            RadioButton => typeof(RadioButton),
            TabControl => typeof(TabControl),
            TabItem => typeof(TabItem),
            Slider => typeof(Slider),
            ProgressBar => typeof(ProgressBar),
            ScrollBar => typeof(ScrollBar),
            ToolTip => typeof(ToolTip),
            _ => null
        };
        if (resourceKey is null)
            return;

        // Do not replace deliberately assigned component-specific styles.
        // Controls relying on an implicit style are explicitly connected via
        // DynamicResource so subsequent Follow-system changes update them too.
        if (frameworkElement.ReadLocalValue(FrameworkElement.StyleProperty) ==
                DependencyProperty.UnsetValue ||
            (bool)frameworkElement.GetValue(UsesManagedThemeStyleProperty))
        {
            frameworkElement.SetResourceReference(
                FrameworkElement.StyleProperty,
                resourceKey);
            frameworkElement.SetValue(UsesManagedThemeStyleProperty, true);
        }
    }

    private static void SetWindowColor(
        IntPtr handle,
        int attribute,
        string colorText)
    {
        if (ColorConverter.ConvertFromString(colorText) is not Color color)
            return;
        var colorRef = color.R | color.G << 8 | color.B << 16;
        _ = DwmSetWindowAttribute(
            handle,
            attribute,
            ref colorRef,
            sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);

    public static void ApplyEmbeddedWorkspace(
        DependencyObject root,
        bool isDark)
    {
        var palette = ToolkitPalette.For(isDark);
        if (root is Panel rootPanel)
            rootPanel.Background = Brushes.Transparent;
        ApplyEmbeddedWorkspaceCore(root, palette);
    }

    private static void ApplyEmbeddedWorkspaceCore(
        DependencyObject root,
        ToolkitPalette palette)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;

            ApplyEmbeddedElementTheme(current, palette);

            foreach (var child in LogicalTreeHelper.GetChildren(current)
                         .OfType<DependencyObject>())
            {
                pending.Push(child);
            }

            if (current is not Visual)
                continue;

            for (var index = 0;
                 index < VisualTreeHelper.GetChildrenCount(current);
                 index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static void ApplyEmbeddedElementTheme(
        DependencyObject element,
        ToolkitPalette palette)
    {
        switch (element)
        {
            case CheckBox checkBox:
                checkBox.Style = SwitchStyle(palette);
                checkBox.Foreground = Brush(palette.Text);
                checkBox.FocusVisualStyle = null;
                checkBox.Cursor = Cursors.Hand;
                break;

            case ComboBox comboBox:
                comboBox.Style = ComboBoxStyle(palette);
                comboBox.ItemContainerStyle = ComboBoxItemStyle(palette);
                comboBox.Background = Brush(palette.SurfaceRaised);
                comboBox.Foreground = Brush(palette.Text);
                comboBox.BorderBrush = Brush(palette.Border);
                comboBox.BorderThickness = new Thickness(1);
                comboBox.MinHeight = Math.Max(34, comboBox.MinHeight);
                comboBox.Cursor = Cursors.Hand;
                break;

            case TextBox textBox:
                textBox.Style = TextBoxStyle(palette);
                textBox.Background = Brush(palette.SurfaceRaised);
                textBox.Foreground = Brush(palette.Text);
                textBox.BorderBrush = Brush(palette.Border);
                textBox.BorderThickness = new Thickness(1);
                textBox.CaretBrush = Brush(palette.Accent);
                break;

            case Button button:
                ApplyEmbeddedButtonTheme(button, palette);
                break;

            case TabControl tabControl:
                tabControl.Style = TabControlStyle(palette);
                tabControl.Background = Brushes.Transparent;
                tabControl.Foreground = Brush(palette.Text);
                tabControl.BorderThickness = new Thickness(0);
                break;

            case TabItem tabItem:
                tabItem.Style = TabItemStyle(palette);
                tabItem.Foreground = Brush(palette.Muted);
                tabItem.Background = Brushes.Transparent;
                tabItem.BorderBrush = Brushes.Transparent;
                tabItem.BorderThickness = new Thickness(1);
                break;

            case Slider slider:
                if (slider.ReadLocalValue(Control.TemplateProperty) ==
                    DependencyProperty.UnsetValue)
                {
                    slider.Style = SliderStyle(palette);
                }
                slider.SetCurrentValue(Control.ForegroundProperty, Brush(palette.Accent));
                slider.SetCurrentValue(Control.BackgroundProperty, Brush(palette.Border));
                break;

            case ProgressBar progressBar:
                progressBar.Style = ProgressBarStyle(palette);
                progressBar.Foreground = Brush(palette.Accent);
                progressBar.Background = Brush(palette.Border);
                progressBar.Height = Math.Max(
                    7,
                    double.IsNaN(progressBar.Height) ? 0 : progressBar.Height);
                break;

            case Border border:
                ApplyEmbeddedBorderTheme(border, palette);
                break;
        }
    }

    private static void ApplyEmbeddedButtonTheme(
        Button button,
        ToolkitPalette palette)
    {
        var isDanger = IsDangerAction(button);
        var hasSemanticBackground = IsSemanticBrush(button.Background);
        var background = isDanger
            ? Brush(palette.Danger)
            : hasSemanticBackground
                ? button.Background
                : Brush(palette.SurfaceRaised);

        button.Style = ButtonStyle(palette);
        button.Background = background;
        button.Foreground =
            isDanger || hasSemanticBackground
                ? Brushes.White
                : Brush(palette.Text);
        button.BorderBrush =
            isDanger
                ? Brush(palette.Danger)
                : hasSemanticBackground
                    ? background
                    : Brush(palette.Border);
        button.BorderThickness = new Thickness(1);
        button.MinHeight = Math.Max(34, button.MinHeight);
        button.Cursor = Cursors.Hand;
    }

    private static void ApplyEmbeddedBorderTheme(
        Border border,
        ToolkitPalette palette)
    {
        var thickness = border.BorderThickness;
        var hasBorder = thickness.Left > 0 || thickness.Top > 0 ||
                        thickness.Right > 0 || thickness.Bottom > 0;
        if (!hasBorder)
            return;

        var isSeparator = (!double.IsNaN(border.Height) && border.Height <= 2) ||
                          (!double.IsNaN(border.Width) && border.Width <= 2);
        border.SetCurrentValue(Border.BorderBrushProperty, Brush(palette.Border));
        if (isSeparator)
            return;

        if (!IsSemanticBrush(border.Background))
            border.SetCurrentValue(Border.BackgroundProperty, Brush(palette.SurfaceRaised));

        var radius = Math.Max(12, border.CornerRadius.TopLeft);
        border.SetCurrentValue(Border.CornerRadiusProperty, new CornerRadius(radius));
    }

    private static bool IsDangerAction(Button button)
    {
        var text = button.Content?.ToString() ?? string.Empty;
        return text.Contains("安全擦除", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Secure Wipe", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("写入 BIOS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Flash BIOS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSemanticBrush(Brush? brush)
    {
        if (brush is not SolidColorBrush solid || solid.Color.A == 0)
            return false;

        var color = solid.Color;
        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) -
                     Math.Min(color.R, Math.Min(color.G, color.B));
        return spread >= 34;
    }

    public static ControlTemplate RoundedButtonTemplate(double radius = 9)
    {
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        chrome.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        chrome.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = chrome
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.9, "Chrome"));
        template.Triggers.Add(hover);

        var pressed = new Trigger
        {
            Property = Button.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "Chrome"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "Chrome"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate SwitchTemplate(ToolkitPalette palette)
    {
        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.Name = "SwitchRoot";
        layout.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        layout.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "SwitchTrack";
        track.SetValue(FrameworkElement.WidthProperty, 48.0);
        track.SetValue(FrameworkElement.HeightProperty, 26.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        track.SetValue(Border.BorderBrushProperty,
            Brush(palette.Canvas.StartsWith("#0", StringComparison.OrdinalIgnoreCase)
                ? "#CAD1DC"
                : "#8793A5"));
        track.SetValue(Border.BackgroundProperty,
            Brush(palette.Canvas.StartsWith("#0", StringComparison.OrdinalIgnoreCase)
                ? "#3B465A"
                : "#FFFFFF"));
        track.SetValue(Border.PaddingProperty, new Thickness(3));
        track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.Name = "SwitchThumb";
        thumb.SetValue(FrameworkElement.WidthProperty, 16.0);
        thumb.SetValue(FrameworkElement.HeightProperty, 16.0);
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        thumb.SetValue(Border.BackgroundProperty,
            Brush(palette.Canvas.StartsWith("#0", StringComparison.OrdinalIgnoreCase)
                ? "#E1E6EE"
                : "#8793A5"));
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.AppendChild(thumb);
        layout.AppendChild(track);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.Name = "SwitchContent";
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        layout.AppendChild(presenter);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = layout
        };
        var enabled = new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true
        };
        enabled.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush(palette.Accent),
            "SwitchTrack"));
        enabled.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Accent),
            "SwitchTrack"));
        enabled.Setters.Add(new Setter(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Right,
            "SwitchThumb"));
        enabled.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brushes.White,
            "SwitchThumb"));
        template.Triggers.Add(enabled);

        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.9, "SwitchTrack"));
        template.Triggers.Add(hover);

        var focused = new Trigger
        {
            Property = UIElement.IsKeyboardFocusedProperty,
            Value = true
        };
        focused.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Accent),
            "SwitchTrack"));
        template.Triggers.Add(focused);

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "SwitchRoot"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate TextBoxTemplate(ToolkitPalette palette)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "TextBoxChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));

        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        host.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(host);

        var template = new ControlTemplate(typeof(TextBox))
        {
            VisualTree = border
        };
        var focused = new Trigger
        {
            Property = UIElement.IsKeyboardFocusWithinProperty,
            Value = true
        };
        focused.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Accent),
            "TextBoxChrome"));
        template.Triggers.Add(focused);
        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "TextBoxChrome"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate ComboBoxTemplate(ToolkitPalette palette)
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.Name = "ComboRoot";

        var outer = new FrameworkElementFactory(typeof(Border));
        outer.Name = "ComboChrome";
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        outer.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        outer.SetValue(Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        outer.SetValue(Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));

        var contentGrid = new FrameworkElementFactory(typeof(Grid));
        var selection = new FrameworkElementFactory(typeof(ContentPresenter));
        selection.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        selection.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        selection.SetValue(ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        selection.SetValue(FrameworkElement.MarginProperty, new Thickness(11, 0, 38, 0));
        selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        selection.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        selection.SetValue(UIElement.IsHitTestVisibleProperty, false);
        selection.SetValue(TextElement.ForegroundProperty,
            new TemplateBindingExtension(Control.ForegroundProperty));
        contentGrid.AppendChild(selection);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "DropDownToggle";
        // The whole selector is the hit target. The previous 36 px toggle only
        // made the arrow clickable and left the selected-value area inert.
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetBinding(
            ToggleButton.IsCheckedProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Mode = BindingMode.TwoWay
            });
        toggle.SetValue(Control.TemplateProperty, ComboArrowButtonTemplate(palette));
        contentGrid.AppendChild(toggle);
        outer.AppendChild(contentGrid);
        root.AppendChild(outer);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetValue(Popup.IsOpenProperty,
            new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, Brush(palette.Surface));
        popupBorder.SetValue(Border.BorderBrushProperty, Brush(palette.Border));
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        popupBorder.SetBinding(
            FrameworkElement.MinWidthProperty,
            new Binding(nameof(FrameworkElement.ActualWidth))
            {
                RelativeSource = RelativeSource.TemplatedParent
            });

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(FrameworkElement.MaxHeightProperty, 320.0);
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroll.AppendChild(items);
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(ComboBox))
        {
            VisualTree = root
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Muted),
            "ComboChrome"));
        template.Triggers.Add(hover);
        var focused = new Trigger
        {
            Property = UIElement.IsKeyboardFocusWithinProperty,
            Value = true
        };
        focused.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Accent),
            "ComboChrome"));
        template.Triggers.Add(focused);
        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "ComboRoot"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate ComboArrowButtonTemplate(ToolkitPalette palette)
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.Name = "ComboHitTarget";
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0 0 L 4 4 L 8 0"));
        arrow.SetValue(Shape.StrokeProperty, Brush(palette.Muted));
        arrow.SetValue(Shape.StrokeThicknessProperty, 1.6);
        arrow.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        arrow.SetValue(FrameworkElement.WidthProperty, 8.0);
        arrow.SetValue(FrameworkElement.HeightProperty, 5.0);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 13, 0));
        grid.AppendChild(arrow);
        return new ControlTemplate(typeof(ToggleButton))
        {
            VisualTree = grid
        };
    }

    private static ControlTemplate ComboBoxItemTemplate(ToolkitPalette palette)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ComboBoxItem))
        {
            VisualTree = border
        };
        var highlighted = new Trigger
        {
            Property = ComboBoxItem.IsHighlightedProperty,
            Value = true
        };
        highlighted.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush(palette.AccentSoft),
            "ItemChrome"));
        template.Triggers.Add(highlighted);
        var selected = new Trigger
        {
            Property = Selector.IsSelectedProperty,
            Value = true
        };
        selected.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush(palette.AccentSoft),
            "ItemChrome"));
        selected.Setters.Add(new Setter(
            Control.ForegroundProperty,
            Brush(palette.Accent)));
        template.Triggers.Add(selected);
        return template;
    }

    private static Style ButtonStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.SurfaceRaised)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34.0));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.TemplateProperty, RoundedButtonTemplate()));
        return style;
    }

    private static Style TextBoxStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.SurfaceRaised)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        style.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brush(palette.Accent)));
        style.Setters.Add(new Setter(TextBox.SelectionBrushProperty, Brush(palette.Accent)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, TextBoxTemplate(palette)));
        return style;
    }

    private static Style ComboBoxStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.SurfaceRaised)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 4, 7, 4)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34.0));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, ComboBoxTemplate(palette)));
        return style;
    }

    private static Style ComboBoxItemStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, ComboBoxItemTemplate(palette)));
        return style;
    }

    private static Style SwitchStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 8, 2)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, SwitchTemplate(palette)));
        return style;
    }

    private static Style ToggleStyle<T>(ToolkitPalette palette) where T : Control
    {
        var style = new Style(typeof(T));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 8, 2)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        return style;
    }

    private static Style TabControlStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(TabControl));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.TemplateProperty, TabControlTemplate(palette)));
        return style;
    }

    private static ControlTemplate TabControlTemplate(ToolkitPalette palette)
    {
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(DockPanel.LastChildFillProperty, true);

        var headers = new FrameworkElementFactory(typeof(TabPanel));
        headers.Name = "HeaderPanel";
        headers.SetValue(Panel.IsItemsHostProperty, true);
        headers.SetValue(DockPanel.DockProperty, Dock.Top);
        headers.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 10));
        dock.AppendChild(headers);

        var contentBorder = new FrameworkElementFactory(typeof(Border));
        contentBorder.SetValue(Border.BackgroundProperty, Brush(palette.SurfaceRaised));
        contentBorder.SetValue(Border.BorderBrushProperty, Brush(palette.Border));
        contentBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        contentBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        contentBorder.SetValue(Border.PaddingProperty, new Thickness(12));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.Name = "PART_SelectedContentHost";
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        contentBorder.AppendChild(presenter);
        dock.AppendChild(contentBorder);

        return new ControlTemplate(typeof(TabControl))
        {
            VisualTree = dock
        };
    }

    private static Style TabItemStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Muted)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 9, 16, 9)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, TabItemTemplate(palette)));
        return style;
    }

    private static ControlTemplate TabItemTemplate(ToolkitPalette palette)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "TabChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(TabItem))
        {
            VisualTree = border
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush(palette.SurfaceRaised),
            "TabChrome"));
        template.Triggers.Add(hover);
        var selected = new Trigger
        {
            Property = Selector.IsSelectedProperty,
            Value = true
        };
        selected.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush(palette.AccentSoft),
            "TabChrome"));
        selected.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush(palette.Accent),
            "TabChrome"));
        selected.Setters.Add(new Setter(
            Control.ForegroundProperty,
            Brush(palette.Accent)));
        template.Triggers.Add(selected);
        return template;
    }

    private static Style ScrollBarStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Muted)));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 12.0));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            ScrollBarTemplate(palette, Orientation.Vertical)));

        var horizontal = new Trigger
        {
            Property = ScrollBar.OrientationProperty,
            Value = Orientation.Horizontal
        };
        horizontal.Setters.Add(new Setter(FrameworkElement.WidthProperty, double.NaN));
        horizontal.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        horizontal.Setters.Add(new Setter(FrameworkElement.HeightProperty, 12.0));
        horizontal.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 12.0));
        horizontal.Setters.Add(new Setter(Control.TemplateProperty,
            ScrollBarTemplate(palette, Orientation.Horizontal)));
        style.Triggers.Add(horizontal);
        return style;
    }

    private static ControlTemplate ScrollBarTemplate(
        ToolkitPalette palette,
        Orientation orientation)
    {
        var isVertical = orientation == Orientation.Vertical;
        var decreaseCommand = isVertical ? "PageUpCommand" : "PageLeftCommand";
        var increaseCommand = isVertical ? "PageDownCommand" : "PageRightCommand";
        var directionReversed = isVertical ? "True" : "False";
        var thumbMinWidth = isVertical ? "8" : "32";
        var thumbMinHeight = isVertical ? "32" : "8";
        var orientationName = isVertical ? "Vertical" : "Horizontal";

        var xaml = $$"""
            <ControlTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                TargetType="{x:Type ScrollBar}">
                <Grid Background="Transparent">
                    <Track x:Name="PART_Track"
                           Orientation="{{orientationName}}"
                           IsDirectionReversed="{{directionReversed}}"
                           Minimum="{TemplateBinding Minimum}"
                           Maximum="{TemplateBinding Maximum}"
                           Value="{TemplateBinding Value}"
                           ViewportSize="{TemplateBinding ViewportSize}">
                        <Track.DecreaseRepeatButton>
                            <RepeatButton Command="{x:Static ScrollBar.{{decreaseCommand}}}"
                                          Focusable="False" Background="Transparent">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                                        <Border Background="Transparent" />
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                            <Thumb MinWidth="{{thumbMinWidth}}"
                                   MinHeight="{{thumbMinHeight}}"
                                   Margin="2" Focusable="False">
                                <Thumb.Template>
                                    <ControlTemplate TargetType="{x:Type Thumb}">
                                        <Border x:Name="ThumbChrome"
                                                Background="{{palette.Muted}}"
                                                CornerRadius="4"
                                                Opacity="0.72" />
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter TargetName="ThumbChrome"
                                                        Property="Opacity" Value="1" />
                                            </Trigger>
                                            <Trigger Property="IsDragging" Value="True">
                                                <Setter TargetName="ThumbChrome"
                                                        Property="Background" Value="{{palette.Accent}}" />
                                                <Setter TargetName="ThumbChrome"
                                                        Property="Opacity" Value="1" />
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                            <RepeatButton Command="{x:Static ScrollBar.{{increaseCommand}}}"
                                          Focusable="False" Background="Transparent">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                                        <Border Background="Transparent" />
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.IncreaseRepeatButton>
                    </Track>
                </Grid>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static Style SliderStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(Slider));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Accent)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28.0));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            SliderTemplate(palette, Orientation.Horizontal)));

        var vertical = new Trigger
        {
            Property = Slider.OrientationProperty,
            Value = Orientation.Vertical
        };
        vertical.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 28.0));
        vertical.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        vertical.Setters.Add(new Setter(Control.TemplateProperty,
            SliderTemplate(palette, Orientation.Vertical)));
        style.Triggers.Add(vertical);
        return style;
    }

    private static ControlTemplate SliderTemplate(
        ToolkitPalette palette,
        Orientation orientation)
    {
        var isVertical = orientation == Orientation.Vertical;
        var orientationName = isVertical ? "Vertical" : "Horizontal";
        var rootSizeProperty = isVertical ? "Width" : "Height";
        var trackSizeProperty = isVertical ? "Width" : "Height";
        var trackHorizontalAlignment = isVertical ? "Center" : "Stretch";
        var trackVerticalAlignment = isVertical ? "Stretch" : "Center";

        var xaml = $$"""
            <ControlTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                TargetType="{x:Type Slider}">
                <Grid x:Name="ToolkitSliderRoot"
                      {{rootSizeProperty}}="28" Background="Transparent">
                    <Border {{trackSizeProperty}}="6"
                            Background="{{palette.Border}}"
                            CornerRadius="3"
                            HorizontalAlignment="{{trackHorizontalAlignment}}"
                            VerticalAlignment="{{trackVerticalAlignment}}" />
                    <Track x:Name="PART_Track"
                           Orientation="{{orientationName}}"
                           IsDirectionReversed="{TemplateBinding IsDirectionReversed}"
                           Minimum="{TemplateBinding Minimum}"
                           Maximum="{TemplateBinding Maximum}"
                           Value="{TemplateBinding Value}">
                        <Track.DecreaseRepeatButton>
                            <RepeatButton Command="{x:Static Slider.DecreaseLarge}"
                                          Focusable="False">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                                        <Border {{trackSizeProperty}}="6"
                                                Background="{{palette.Accent}}"
                                                CornerRadius="3"
                                                HorizontalAlignment="{{trackHorizontalAlignment}}"
                                                VerticalAlignment="{{trackVerticalAlignment}}" />
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                            <Thumb Width="18"
                                   Height="18"
                                   Focusable="False">
                                <Thumb.Template>
                                    <ControlTemplate TargetType="{x:Type Thumb}">
                                        <Grid>
                                            <Ellipse x:Name="ThumbHalo"
                                                     Fill="{{palette.AccentSoft}}"
                                                     Opacity="0" />
                                            <Ellipse x:Name="ThumbChrome"
                                                     Width="14" Height="14"
                                                     Fill="{{palette.Surface}}"
                                                     Stroke="{{palette.Accent}}"
                                                     StrokeThickness="3" />
                                        </Grid>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter TargetName="ThumbHalo"
                                                        Property="Opacity" Value="0.75" />
                                            </Trigger>
                                            <Trigger Property="IsDragging" Value="True">
                                                <Setter TargetName="ThumbHalo"
                                                        Property="Opacity" Value="1" />
                                                <Setter TargetName="ThumbChrome"
                                                        Property="Fill" Value="{{palette.Accent}}" />
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                            <RepeatButton Command="{x:Static Slider.IncreaseLarge}"
                                          Focusable="False" Background="Transparent">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                                        <Border Background="Transparent" />
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.IncreaseRepeatButton>
                    </Track>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.42" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static Style ProgressBarStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(ProgressBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Accent)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 7.0));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            ProgressBarTemplate(palette)));
        return style;
    }

    private static ControlTemplate ProgressBarTemplate(ToolkitPalette palette)
    {
        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "PART_Track";
        track.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        track.SetValue(UIElement.ClipToBoundsProperty, true);

        var indicator = new FrameworkElementFactory(typeof(Border));
        indicator.Name = "PART_Indicator";
        indicator.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.ForegroundProperty));
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Left);
        track.AppendChild(indicator);

        var template = new ControlTemplate(typeof(ProgressBar))
        {
            VisualTree = track
        };
        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        return template;
    }

    private static Style ToolTipStyle(ToolkitPalette palette)
    {
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(palette.SurfaceRaised)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(palette.Text)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(palette.Border)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        return style;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
