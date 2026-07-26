using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class BatteryInformationWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly bool _embeddedMode;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private readonly TextBlock _temperature = ValueText();
    private readonly TextBlock _power = ValueText();
    private readonly TextBlock _minimumPower = ValueText();
    private readonly TextBlock _maximumPower = ValueText();
    private readonly TextBlock _currentCapacity = ValueText();
    private readonly TextBlock _fullChargeCapacity = ValueText();
    private readonly TextBlock _designCapacity = ValueText();
    private readonly TextBlock _health = ValueText();
    private readonly TextBlock _onBatterySince = ValueText();
    private readonly TextBlock _cycleCount = ValueText();
    private readonly TextBlock _manufactureDate = ValueText();
    private readonly TextBlock _firstUseDate = ValueText();
    private bool _refreshing;

    public BatteryInformationWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        bool embeddedMode = false)
    {
        _t = translate;
        _embeddedMode = embeddedMode;
        Title = _t("BatteryInformation");
        Width = 760;
        Height = 700;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Content = BuildLayout();
        Background = Brush(isDark ? "#111827" : "#ffffff");
        Foreground = Brush(isDark ? "#f9fafb" : "#111827");

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync(showError: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private UIElement BuildLayout()
    {
        var information = new StackPanel();
        AddRow(information, "BatteryTemperature", "BatteryTemperatureDescription", _temperature);
        AddRow(information, "BatteryPower", "BatteryPowerDescription", _power);
        AddRow(information, "BatteryMinimumPower", "BatteryMinimumPowerDescription", _minimumPower);
        AddRow(information, "BatteryMaximumPower", "BatteryMaximumPowerDescription", _maximumPower);
        AddRow(information, "BatteryCurrentCapacity", "BatteryCurrentCapacityDescription", _currentCapacity);
        AddRow(information, "BatteryFullChargeCapacity", "BatteryFullChargeCapacityDescription", _fullChargeCapacity);
        AddRow(information, "BatteryDesignCapacity", "BatteryDesignCapacityDescription", _designCapacity);
        AddRow(information, "BatteryHealth", "BatteryHealthDescription", _health);
        AddRow(information, "BatteryUsageTime", "BatteryUsageTimeDescription", _onBatterySince);
        AddRow(information, "BatteryCycleCount", "BatteryCycleCountDescription", _cycleCount);
        AddRow(information, "BatteryManufactureDate", "BatteryManufactureDateDescription", _manufactureDate);
        AddRow(information, "BatteryFirstUseDate", "BatteryFirstUseDateDescription", _firstUseDate);

        var closeButton = new Button
        {
            Content = _t("Close"),
            MinWidth = 76,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        closeButton.Click += (_, _) => Close();
        closeButton.Visibility = _embeddedMode
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
            root.Children.Add(information);
        }
        else
        {
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = information
            });
        }
        Grid.SetRow(closeButton, 1);
        root.Children.Add(closeButton);
        return root;
    }

    private async Task RefreshAsync(bool showError = false)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            var state = await Task.Run(BatteryInformationReader.Read);
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

    private void ApplyState(BatteryInformationSnapshot state)
    {
        _temperature.Text = state.TemperatureC.HasValue
            ? $"{state.TemperatureC.Value:0.0} \u00B0C"
            : "-";
        _power.Text = $"{state.ChargeDischargePowerW:+0.00;-0.00;0.00} W";
        _minimumPower.Text = $"{state.MinimumPowerW:+0.00;-0.00;0.00} W";
        _maximumPower.Text = $"{state.MaximumPowerW:+0.00;-0.00;0.00} W";
        _currentCapacity.Text = $"{state.CurrentCapacityWh:0.00} Wh";
        _fullChargeCapacity.Text = $"{state.FullChargeCapacityWh:0.00} Wh";
        _designCapacity.Text = $"{state.DesignCapacityWh:0.00} Wh";
        _health.Text = $"{state.HealthPercent:0.00} %";
        _onBatterySince.Text = FormatOnBatterySince(state);
        _cycleCount.Text = state.CycleCount.ToString(CultureInfo.CurrentCulture);
        _manufactureDate.Text = FormatDate(state.ManufactureDate);
        _firstUseDate.Text = FormatDate(state.FirstUseDate);
    }

    private string FormatOnBatterySince(BatteryInformationSnapshot state)
    {
        if (state.IsAcConnected || !state.OnBatterySince.HasValue)
            return "-";

        var since = state.OnBatterySince.Value;
        var elapsed = DateTime.Now - since;
        return string.Format(
            _t("BatteryUsageTimeFormat"),
            since.ToString("g", CultureInfo.CurrentCulture),
            FormatDuration(elapsed));
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}";
        return value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(DateTime? value) =>
        value?.ToString("d", CultureInfo.CurrentCulture) ?? "-";

    private void AddRow(
        Panel panel,
        string titleKey,
        string descriptionKey,
        TextBlock value)
    {
        if (panel.Children.Count > 0)
        {
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = Brush("#4b5563"),
                Opacity = 0.45
            });
        }

        var row = new Grid { Margin = new Thickness(6, 10, 6, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        var text = new StackPanel
        {
            Margin = new Thickness(0, 0, 18, 0)
        };
        text.Children.Add(new TextBlock
        {
            Text = _t(titleKey),
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = _t(descriptionKey),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(text);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        panel.Children.Add(row);
    }

    private static TextBlock ValueText() => new()
    {
        Text = "-",
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        MinWidth = 100
    };

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
