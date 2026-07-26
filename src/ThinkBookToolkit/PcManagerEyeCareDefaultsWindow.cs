using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class PcManagerEyeCareDefaultsWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly bool _embeddedMode;
    private readonly Action<PcManagerEyeCareDefaults>? _saveDefaults;
    private readonly TemperatureEditor _normalTemperature = new()
    {
        Width = 300
    };
    private readonly TemperatureEditor _eyeCareTemperature = new()
    {
        Width = 300
    };

    public PcManagerEyeCareDefaultsWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        PcManagerEyeCareDefaults defaults,
        bool embeddedMode = false,
        Action<PcManagerEyeCareDefaults>? saveDefaults = null)
    {
        _t = translate;
        _embeddedMode = embeddedMode;
        _saveDefaults = saveDefaults;
        defaults = defaults.Normalize();
        Defaults = defaults;
        _normalTemperature.Value = defaults.NormalTemperature;
        _eyeCareTemperature.Value = defaults.EyeCareTemperature;

        Title = _t("PcManagerEyeCareDefaultSettings");
        Width = 600;
        Height = 330;
        MinWidth = 560;
        MinHeight = 310;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Content = BuildLayout();
        ApplyTheme(isDark);
    }

    public PcManagerEyeCareDefaults Defaults { get; private set; }

    private UIElement BuildLayout()
    {
        var settings = new StackPanel();
        settings.Children.Add(BuildTemperatureRow(
            _t("PcManagerNormalDefault"),
            _t("PcManagerNormalDefaultDescription"),
            _normalTemperature));
        settings.Children.Add(BuildTemperatureRow(
            _t("PcManagerEyeCareDefault"),
            _t("PcManagerEyeCareDefaultDescription"),
            _eyeCareTemperature));

        var restoreButton = new Button
        {
            Content = _t("Restore"),
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        restoreButton.Click += (_, _) =>
        {
            _normalTemperature.Value =
                PcManagerEyeCareController.FactoryNormalTemperature;
            _eyeCareTemperature.Value =
                PcManagerEyeCareController.FactoryEyeCareTemperature;
        };

        var okButton = new Button
        {
            Content = _t(_embeddedMode ? "Save" : "OK"),
            MinWidth = 76,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okButton.Click += (_, _) =>
        {
            Defaults = new(
                _normalTemperature.Value,
                _eyeCareTemperature.Value);
            if (_embeddedMode)
                _saveDefaults?.Invoke(Defaults.Normalize());
            else
                DialogResult = true;
        };

        var cancelButton = new Button
        {
            Content = _t("Cancel"),
            MinWidth = 76,
            IsCancel = true
        };
        cancelButton.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(restoreButton);
        buttons.Children.Add(okButton);
        if (!_embeddedMode)
            buttons.Children.Add(cancelButton);

        var root = new Grid
        {
            Margin = _embeddedMode ? new Thickness(0) : new Thickness(18)
        };
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.Children.Add(settings);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        return root;
    }

    private static Grid BuildTemperatureRow(
        string title,
        string description,
        UIElement editor)
    {
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 16, 0)
        });

        var row = new Grid { Margin = new Thickness(0, 4, 0, 18) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        row.Children.Add(text);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private void ApplyTheme(bool isDark)
    {
        Background = Brush(isDark ? "#111827" : "#ffffff");
        Foreground = Brush(isDark ? "#f9fafb" : "#111827");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
