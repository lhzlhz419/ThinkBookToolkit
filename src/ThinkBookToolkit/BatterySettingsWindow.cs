using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class BatterySettingsWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly bool _isDark;
    private readonly bool _embeddedMode;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly ComboBox _chargeModeCombo = SettingCombo();
    private readonly CheckBox _overnightChargingToggle = SettingToggle();
    private readonly ComboBox _alwaysOnUsbCombo = SettingCombo();
    private readonly CheckBox _flipToStartToggle = SettingToggle();
    private BatterySettingsState? _state;
    private bool _loading = true;
    private bool _refreshing;
    private bool _writing;

    public BatterySettingsWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        bool embeddedMode = false)
    {
        _t = translate;
        _isDark = isDark;
        _embeddedMode = embeddedMode;
        Title = _t("BatterySettings");
        Width = 650;
        Height = 430;
        MinWidth = 590;
        MinHeight = 390;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        var comboItemStyle = CreateComboItemStyle();
        _chargeModeCombo.ItemContainerStyle = comboItemStyle;
        _alwaysOnUsbCombo.ItemContainerStyle = comboItemStyle;
        Content = BuildLayout();
        ApplyTheme();

        _refreshTimer.Tick += async (_, _) => await LoadStateAsync();
        Loaded += async (_, _) =>
        {
            _loading = false;
            await LoadStateAsync(showError: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private UIElement BuildLayout()
    {
        AddComboItem(
            _chargeModeCombo,
            _t("BatteryConservationMode"),
            BatteryChargeMode.Conservation);
        AddComboItem(
            _chargeModeCombo,
            _t("BatteryNormalMode"),
            BatteryChargeMode.Normal);
        AddComboItem(
            _chargeModeCombo,
            _t("BatteryRapidChargeMode"),
            BatteryChargeMode.RapidCharge);
        _chargeModeCombo.SelectionChanged +=
            async (_, _) => await ChangeChargeModeAsync();

        _overnightChargingToggle.Click +=
            async (_, _) => await ChangeOvernightChargingAsync();

        AddComboItem(
            _alwaysOnUsbCombo,
            _t("AlwaysOnUsbOff"),
            AlwaysOnUsbMode.Off);
        AddComboItem(
            _alwaysOnUsbCombo,
            _t("AlwaysOnUsbSleeping"),
            AlwaysOnUsbMode.OnWhenSleeping);
        AddComboItem(
            _alwaysOnUsbCombo,
            _t("AlwaysOnUsbAlways"),
            AlwaysOnUsbMode.OnAlways);
        _alwaysOnUsbCombo.SelectionChanged +=
            async (_, _) => await ChangeAlwaysOnUsbAsync();

        _flipToStartToggle.Click +=
            async (_, _) => await ChangeFlipToStartAsync();

        var settings = new StackPanel();
        void AddAvailable(string featureId, UIElement row)
        {
            var report = FeatureAvailabilityCache.Current;
            if (report is not null && !report.IsAvailable(featureId))
                return;
            if (settings.Children.Count > 0)
                settings.Children.Add(BuildSeparator());
            settings.Children.Add(row);
        }

        AddAvailable(
            FeatureIds.BatteryChargeMode,
            BuildSettingRow(
                _t("BatteryChargeMode"),
                _t("BatteryChargeModeDescription"),
                _chargeModeCombo));
        AddAvailable(
            FeatureIds.OvernightCharging,
            BuildSettingRow(
                _t("OvernightBatteryCharging"),
                _t("OvernightBatteryChargingDescription"),
                _overnightChargingToggle));
        AddAvailable(
            FeatureIds.AlwaysOnUsb,
            BuildSettingRow(
                _t("AlwaysOnUsb"),
                _t("AlwaysOnUsbDescription"),
                _alwaysOnUsbCombo));
        AddAvailable(
            FeatureIds.FlipToStart,
            BuildSettingRow(
                _t("FlipToStart"),
                _t("FlipToStartDescription"),
                _flipToStartToggle));

        var batteryInfoButton = new Button
        {
            Content = _t("BatteryInformation"),
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        batteryInfoButton.Visibility =
            FeatureAvailabilityCache.Current is { } report &&
            !report.IsAvailable(FeatureIds.BatteryInformation)
                ? Visibility.Collapsed
                : Visibility.Visible;
        batteryInfoButton.Click += (_, _) =>
        {
            var window = new BatteryInformationWindow(
                _t,
                _isDark,
                FontFamily,
                FontSize)
            {
                Owner = this
            };
            window.ShowDialog();
        };

        var closeButton = new Button
        {
            Content = _t("Close"),
            MinWidth = 76,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => Close();

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        footer.Children.Add(batteryInfoButton);
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);
        footer.Visibility = _embeddedMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        var root = new Grid
        {
            Margin = _embeddedMode ? new Thickness(0) : new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = _embeddedMode
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        if (_embeddedMode)
        {
            root.Children.Add(settings);
        }
        else
        {
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = settings
            });
        }
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        return root;
    }

    private async Task LoadStateAsync(bool showError = false)
    {
        if (_refreshing || _writing)
            return;

        _refreshing = true;
        try
        {
            var state = await Task.Run(
                () => BatterySettingsController.ReadState(
                    refreshFlipToStart: showError));
            _state = state;
            ApplyState(state);
        }
        catch (Exception ex)
        {
            if (showError)
            {
                MessageBox.Show(
                    this,
                    string.Format(_t("SettingsReadFailedFormat"), ex.Message),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ChangeChargeModeAsync()
    {
        if (!CanWrite() ||
            SelectedTag<BatteryChargeMode>(_chargeModeCombo) is not { } mode)
        {
            return;
        }

        await WriteAsync(
            () => BatterySettingsController.SetChargeMode(mode));
    }

    private async Task ChangeOvernightChargingAsync()
    {
        if (!CanWrite() || _overnightChargingToggle.IsChecked is not { } enabled)
            return;

        await WriteAsync(
            () => BatterySettingsController.SetOvernightCharging(enabled));
    }

    private async Task ChangeAlwaysOnUsbAsync()
    {
        if (!CanWrite() ||
            SelectedTag<AlwaysOnUsbMode>(_alwaysOnUsbCombo) is not { } mode)
        {
            return;
        }

        await WriteAsync(
            () => BatterySettingsController.SetAlwaysOnUsb(mode));
    }

    private async Task ChangeFlipToStartAsync()
    {
        if (!CanWrite() || _flipToStartToggle.IsChecked is not { } enabled)
            return;

        await WriteAsync(
            () => BatterySettingsController.SetFlipToStart(enabled));
    }

    private async Task WriteAsync(Action write)
    {
        _writing = true;
        SetControlsEnabled(false);
        try
        {
            await Task.Run(write);
            var state = await Task.Run(
                () => BatterySettingsController.ReadState());
            _state = state;
            ApplyState(state);
        }
        catch (Exception ex)
        {
            if (_state is not null)
                ApplyState(_state);
            MessageBox.Show(
                this,
                string.Format(_t("SettingWriteFailedFormat"), ex.Message),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _writing = false;
            if (_state is not null)
                ApplyState(_state);
        }
    }

    private bool CanWrite() =>
        !_loading && !_refreshing && !_writing && _state is not null;

    private void ApplyState(BatterySettingsState state)
    {
        _loading = true;
        try
        {
            SelectTag(_chargeModeCombo, state.ChargeMode);
            ApplyToggle(_overnightChargingToggle, state.OvernightCharging);
            SelectTag(_alwaysOnUsbCombo, state.AlwaysOnUsb);
            ApplyToggle(_flipToStartToggle, state.FlipToStart);
        }
        finally
        {
            _loading = false;
        }

        SetControlsEnabled(true);
    }

    private void SetControlsEnabled(bool enabled)
    {
        _chargeModeCombo.IsEnabled = enabled && _state?.ChargeMode is not null;
        _overnightChargingToggle.IsEnabled =
            enabled && _state?.OvernightCharging is not null;
        _alwaysOnUsbCombo.IsEnabled =
            enabled && _state?.AlwaysOnUsb is not null;
        _flipToStartToggle.IsEnabled =
            enabled && _state?.FlipToStart is not null;
    }

    private void ApplyToggle(CheckBox toggle, bool? value)
    {
        toggle.IsChecked = value ?? false;
        toggle.Content = value.HasValue
            ? _t(value.Value ? "On" : "Off")
            : _t("NotSupported");
    }

    private static Border BuildSeparator() => new()
    {
        Height = 1,
        Background = Brush("#4b5563"),
        Opacity = 0.55,
        Margin = new Thickness(0, 8, 0, 8)
    };

    private static Grid BuildSettingRow(
        string title,
        string description,
        UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        var text = new StackPanel
        {
            Margin = new Thickness(0, 0, 18, 0)
        };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 3, 0, 0)
        });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static ComboBox SettingCombo() => new()
    {
        Width = 170,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Style CreateComboItemStyle()
    {
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(
            ContentPresenter.HorizontalAlignmentProperty,
            HorizontalAlignment.Stretch);
        content.SetValue(
            ContentPresenter.VerticalAlignmentProperty,
            VerticalAlignment.Center);

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(
            Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Background))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
        border.AppendChild(content);

        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            Brushes.Transparent));
        style.Setters.Add(new Setter(
            Control.ForegroundProperty,
            SystemColors.ControlTextBrush));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            new ControlTemplate(typeof(ComboBoxItem))
            {
                VisualTree = border
            }));
        style.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters =
            {
                new Setter(
                    Control.BackgroundProperty,
                    Brush("#c7e9f8"))
            }
        });
        return style;
    }

    private static CheckBox SettingToggle() => new()
    {
        MinWidth = 90,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void AddComboItem<T>(
        ComboBox comboBox,
        string label,
        T value) where T : struct =>
        comboBox.Items.Add(new ComboBoxItem
        {
            Content = label,
            Tag = value
        });

    private static T? SelectedTag<T>(ComboBox comboBox) where T : struct =>
        comboBox.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static void SelectTag<T>(ComboBox comboBox, T? value)
        where T : struct
    {
        comboBox.SelectedIndex = -1;
        if (!value.HasValue)
            return;

        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem { Tag: T tag } &&
                tag.Equals(value.Value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ApplyTheme()
    {
        Background = Brush(_isDark ? "#111827" : "#ffffff");
        Foreground = Brush(_isDark ? "#f9fafb" : "#111827");
        _chargeModeCombo.Foreground = SystemColors.ControlTextBrush;
        _alwaysOnUsbCombo.Foreground = SystemColors.ControlTextBrush;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
