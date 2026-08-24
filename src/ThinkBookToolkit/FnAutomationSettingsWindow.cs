using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class FnAutomationSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly Dictionary<string, ComboBox> _selectors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _doubleSelectors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _status = new();
    private ToolkitPalette Palette => ToolkitPalette.For(_runtime.IsDark);

    public FnAutomationSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        Title = runtime.L(
            "自定义 Fn 快捷键",
            "Customize Fn keys");
        Width = 720;
        Height = 720;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        Background = Brush(Palette.Canvas);
        Foreground = Brush(Palette.Text);
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "自定义 Fn 快捷键",
                "Customize Fn keys"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "“默认功能”保留 Toolkit 当前行为；选择自动化后，该按键改为运行对应的有序步骤。",
                "Default function keeps Toolkit's current behavior. Selecting an automation runs its ordered steps instead."),
            Foreground = Brush(Palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        });
        root.Children.Add(header);

        var rows = new StackPanel();
        foreach (var keyId in FnAutomationKeyIds.AllForSettings(
                     _runtime.Settings.CustomFnKeyNames))
            rows.Children.Add(BuildRow(keyId));
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        _status.Foreground = Brush(Palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Button(_runtime.L("取消", "Cancel"));
        var save = Button(_runtime.L("保存", "Save"), primary: true);
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Border BuildRow(string keyId)
    {
        var grid = new Grid { MinHeight = 58 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.Children.Add(new TextBlock
        {
            Text = KeyName(keyId),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var selector = BuildSelector(
            keyId,
            _runtime.Settings.FnKeyAutomationBindings,
            _runtime.L("单击", "Single press"));
        _selectors[keyId] = selector;
        var singleHost = SelectorHost(
            selector,
            _runtime.L("单击", "Single press"));
        Grid.SetColumn(singleHost, 1);
        grid.Children.Add(singleHost);
        var doubleSelector = BuildSelector(
            keyId,
            _runtime.Settings.FnKeyDoublePressAutomationBindings,
            _runtime.L("双击", "Double press"));
        _doubleSelectors[keyId] = doubleSelector;
        var doubleHost = SelectorHost(
            doubleSelector,
            _runtime.L("双击", "Double press"));
        Grid.SetColumn(doubleHost, 2);
        grid.Children.Add(doubleHost);
        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private ComboBox BuildSelector(
        string keyId,
        IReadOnlyDictionary<string, string> bindings,
        string tooltip)
    {
        var selector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = tooltip
        };
        selector.Items.Add(new ComboBoxItem
        {
            Content = _runtime.L("默认功能", "Default function"),
            Tag = string.Empty
        });
        foreach (var automation in _runtime.Settings.Automations)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Content = automation.Name,
                Tag = automation.Id
            });
        }
        bindings.TryGetValue(
            keyId,
            out var selectedId);
        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                selectedId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)) ?? selector.Items[0];
        return selector;
    }

    private TextBlock SelectorLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush(Palette.Muted),
        Margin = new Thickness(8, 0, 0, 3)
    };

    private StackPanel SelectorHost(ComboBox selector, string label)
    {
        var host = new StackPanel();
        host.Children.Add(SelectorLabel(label));
        host.Children.Add(selector);
        return host;
    }

    private void Save()
    {
        var bindings = _selectors
            .Select(pair => (
                pair.Key,
                Value: (pair.Value.SelectedItem as ComboBoxItem)?.Tag
                    ?.ToString() ?? string.Empty))
            .Where(pair => pair.Value.Length > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        var doubleBindings = SelectedBindings(_doubleSelectors);
        if (!_runtime.TrySaveFnAutomationBindings(
                bindings,
                doubleBindings,
                out var error))
        {
            _status.Text = _runtime.L(
                "保存失败：",
                "Save failed: ") + error;
            return;
        }
        DialogResult = true;
    }

    private string KeyName(string keyId) => keyId switch
    {
        FnAutomationKeyIds.FnQ => "Fn + Q",
        FnAutomationKeyIds.FnSpace => "Fn + Space",
        FnAutomationKeyIds.FnF4 => "Fn + F4",
        FnAutomationKeyIds.FnF8 => "Fn + F8",
        FnAutomationKeyIds.FnF10 => "Fn + F10",
        FnAutomationKeyIds.FnLock => "FnLock",
        FnAutomationKeyIds.PrintScreen => "Fn + PrtSc",
        FnAutomationKeyIds.Touchpad => _runtime.L("Fn + M / 触摸板键", "Fn + M / Touchpad key"),
        FnAutomationKeyIds.RefreshRate => "Fn + R",
        FnAutomationKeyIds.FnF9 => "Fn + F9",
        FnAutomationKeyIds.FnN => "Fn + N",
        _ when _runtime.Settings.CustomFnKeyNames.TryGetValue(
                   keyId,
                   out var customName) &&
               FnAutomationKeyIds.TryGetCustomDetails(
                   keyId,
                   out var channel,
                   out var code) =>
            $"{customName} · {channel} · 0x{code:X}",
        _ => keyId
    };

    private static Dictionary<string, string> SelectedBindings(
        IReadOnlyDictionary<string, ComboBox> selectors) =>
        selectors.Select(pair => (
                pair.Key,
                Value: (pair.Value.SelectedItem as ComboBoxItem)?.Tag
                    ?.ToString() ?? string.Empty))
            .Where(pair => pair.Value.Length > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 100,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Margin = new Thickness(8, 0, 0, 0),
        Background = Brush(primary ? Palette.Accent : Palette.SurfaceRaised),
        Foreground = Brush(primary ? "#FFFFFF" : Palette.Text),
        BorderBrush = Brush(primary ? Palette.Accent : Palette.Border)
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
