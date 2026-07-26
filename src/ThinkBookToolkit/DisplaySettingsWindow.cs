using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class DisplaySettingsWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly Func<PcManagerEyeCareDefaults> _readPcManagerDefaults;
    private readonly Action<PcManagerEyeCareDefaults> _savePcManagerDefaults;
    private readonly bool _isDark;
    private readonly bool _embeddedMode;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _pcManagerRefreshTimer;
    private readonly DispatcherTimer _temperatureApplyTimer;
    private readonly CheckBox _eyeCareToggle = new()
    {
        MinWidth = 96,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox _eyeColorEffectCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox _eyeScheduleCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox _customTemperatureCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox _colorManagementCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly CheckBox _pcManagerEyeCareToggle = new()
    {
        MinWidth = 96,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TemperatureEditor _pcManagerTemperature = new()
    {
        Width = 330,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button _pcManagerRestoreButton = new()
    {
        MinWidth = 88
    };
    private readonly Button _pcManagerDefaultsButton = new()
    {
        MinWidth = 88,
        Margin = new Thickness(8, 0, 0, 0)
    };
    private readonly TextBlock _eyeCareStatus = StatusText();
    private readonly TextBlock _pcManagerEyeCareStatus = StatusText();
    private readonly TextBlock _colorManagementStatus = StatusText();
    private readonly Button _refreshButton = new() { MinWidth = 76 };
    private readonly Button _closeButton = new()
    {
        MinWidth = 76,
        IsCancel = true
    };
    private DisplaySettingsState? _state;
    private int _stateVersion;
    private bool _loading = true;
    private bool _refreshing;
    private bool _refreshingPcManager;
    private int _pendingTemperature;

    public DisplaySettingsWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        Func<TimeSpan> refreshInterval,
        Func<PcManagerEyeCareDefaults> readPcManagerDefaults,
        Action<PcManagerEyeCareDefaults> savePcManagerDefaults,
        bool embeddedMode = false)
    {
        _t = translate;
        _isDark = isDark;
        _refreshInterval = refreshInterval;
        _readPcManagerDefaults = readPcManagerDefaults;
        _savePcManagerDefaults = savePcManagerDefaults;
        _embeddedMode = embeddedMode;
        _refreshTimer = new DispatcherTimer
        {
            Interval = CurrentRefreshInterval()
        };
        _pcManagerRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _temperatureApplyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        Title = _t("DisplaySettings");
        Width = 720;
        Height = 760;
        MinWidth = 660;
        MinHeight = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Content = BuildLayout();
        ApplyTheme(isDark);
        _refreshTimer.Tick += async (_, _) =>
        {
            SyncRefreshTimerInterval();
            await LoadStateAsync();
        };
        _pcManagerRefreshTimer.Tick += async (_, _) =>
            await LoadPcManagerStateAsync();
        _temperatureApplyTimer.Tick += async (_, _) =>
        {
            _temperatureApplyTimer.Stop();
            await ChangePcManagerTemperatureAsync(_pendingTemperature);
        };
        Loaded += async (_, _) =>
        {
            _loading = false;
            SyncRefreshTimerInterval();
            await LoadStateAsync(showReading: true);
            _refreshTimer.Start();
            _pcManagerRefreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _pcManagerRefreshTimer.Stop();
            _temperatureApplyTimer.Stop();
            DisplaySettingsController.Shutdown();
        };
    }

    private UIElement BuildLayout()
    {
        _eyeCareToggle.Click +=
            async (_, _) => await ChangeEyeCareEnabledAsync();
        _eyeColorEffectCombo.SelectionChanged +=
            async (_, _) => await ChangeEyeColorEffectAsync();
        _eyeScheduleCombo.SelectionChanged +=
            async (_, _) => await ChangeEyeScheduleAsync();
        _customTemperatureCombo.SelectionChanged +=
            async (_, _) => await ChangeEyeCustomTemperatureAsync();
        _pcManagerEyeCareToggle.Click +=
            async (_, _) => await ChangePcManagerEyeCareEnabledAsync();
        _pcManagerTemperature.ValueChanged += (_, _) =>
        {
            if (_loading ||
                _state?.PcManagerEyeCare.Enabled != false)
            {
                return;
            }

            _pendingTemperature = _pcManagerTemperature.Value;
            _refreshTimer.Stop();
            _pcManagerRefreshTimer.Stop();
            _temperatureApplyTimer.Stop();
            _temperatureApplyTimer.Start();
        };
        _pcManagerRestoreButton.Content = _t("RestoreDefault");
        _pcManagerRestoreButton.Click +=
            async (_, _) => await RestorePcManagerDefaultAsync();
        _pcManagerDefaultsButton.Content = _t("SetDefaultValues");
        _pcManagerDefaultsButton.Click += (_, _) =>
            ShowPcManagerDefaultsWindow();
        _pcManagerDefaultsButton.Visibility = _embeddedMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        _colorManagementCombo.SelectionChanged +=
            async (_, _) => await ChangeColorManagementAsync();

        _refreshButton.Content = _t("Refresh");
        _refreshButton.Click += async (_, _) =>
            await LoadStateAsync(showReading: true);
        _closeButton.Content = _t("Close");
        _closeButton.Click += (_, _) => Close();

        for (var temperature = 2700; temperature <= 6500; temperature += 100)
        {
            _customTemperatureCombo.Items.Add(new ComboBoxItem
            {
                Content = string.Format(
                    _t("ColorTemperatureFormat"),
                    temperature),
                Tag = temperature
            });
        }

        var content = new StackPanel();
        var report = FeatureAvailabilityCache.Current;
        if (report is null || report.IsAvailable(FeatureIds.VantageEyeCare))
            content.Children.Add(BuildEyeCareGroup());
        if (report is null || report.IsAvailable(FeatureIds.PcManagerEyeCare))
            content.Children.Add(BuildPcManagerEyeCareGroup());
        if (report is null || report.IsAvailable(FeatureIds.ColorManagement))
            content.Children.Add(BuildColorManagementGroup());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        _refreshButton.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_refreshButton);
        if (!_embeddedMode)
            buttons.Children.Add(_closeButton);

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
            root.Children.Add(content);
        }
        else
        {
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            });
        }
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        return root;
    }

    private Border BuildEyeCareGroup()
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildHeaderRow(
            _t("EyeCareModeVantage"),
            _t("EyeCareModeDescription"),
            _eyeCareToggle));
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("#4b5563"),
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 10)
        });
        panel.Children.Add(BuildOptionRow(
            _t("ColorEffect"),
            _t("ColorEffectDescription"),
            _eyeColorEffectCombo));
        panel.Children.Add(BuildOptionRow(
            _t("EyeCareSchedule"),
            _t("EyeCareScheduleDescription"),
            _eyeScheduleCombo));
        panel.Children.Add(BuildOptionRow(
            _t("CustomColorTemperature"),
            _t("CustomColorTemperatureDescription"),
            _customTemperatureCombo));
        panel.Children.Add(_eyeCareStatus);

        return new Border
        {
            BorderBrush = Brush("#4b5563"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Border BuildPcManagerEyeCareGroup()
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildHeaderRow(
            _t("EyeCareModePcManager"),
            _t("EyeCareModePcManagerDescription"),
            _pcManagerEyeCareToggle));
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("#4b5563"),
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 10)
        });
        panel.Children.Add(BuildOptionRow(
            _t("ColorTemperature"),
            _t("PcManagerColorTemperatureDescription"),
            _pcManagerTemperature));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8)
        };
        buttons.Children.Add(_pcManagerRestoreButton);
        buttons.Children.Add(_pcManagerDefaultsButton);
        panel.Children.Add(buttons);
        panel.Children.Add(_pcManagerEyeCareStatus);

        return new Border
        {
            BorderBrush = Brush("#4b5563"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Border BuildColorManagementGroup()
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildHeaderRow(
            _t("ColorManagement"),
            _t("ColorManagementDescription"),
            _colorManagementCombo));
        panel.Children.Add(_colorManagementStatus);

        return new Border
        {
            BorderBrush = Brush("#4b5563"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Grid BuildHeaderRow(
        string title,
        string description,
        UIElement control)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = FontSize + 1
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 12, 0)
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        row.Children.Add(text);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private Grid BuildOptionRow(
        string title,
        string description,
        UIElement control)
    {
        var text = new StackPanel();
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
            Margin = new Thickness(0, 4, 12, 0)
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        row.Children.Add(text);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private async Task LoadStateAsync(bool showReading = false)
    {
        if (_loading || _refreshing)
            return;

        var version = _stateVersion;
        _refreshing = true;
        if (showReading)
        {
            SetBusy(true);
            _eyeCareStatus.Text = _t("ReadingSettings");
            _colorManagementStatus.Text = _t("ReadingSettings");
        }

        try
        {
            var defaults = _readPcManagerDefaults();
            var state = await Task.Run(
                () => DisplaySettingsController.ReadState(defaults));
            if (version == _stateVersion)
            {
                _state = state;
                ApplyState(state);
            }
        }
        finally
        {
            if (showReading)
                SetBusy(false);
            _refreshing = false;
            SyncRefreshTimerInterval();
        }
    }

    private async Task LoadPcManagerStateAsync()
    {
        if (_loading ||
            _refreshingPcManager ||
            _pcManagerTemperature.IsUserInteracting)
        {
            return;
        }

        var version = _stateVersion;
        var defaults = _readPcManagerDefaults();
        _refreshingPcManager = true;
        try
        {
            var state = await Task.Run(
                () => PcManagerEyeCareController.ReadState(defaults));
            if (version != _stateVersion)
                return;

            if (_state is null)
            {
                await LoadStateAsync();
                return;
            }

            _state = _state with { PcManagerEyeCare = state };
            ApplyPcManagerEyeCareState(state);
        }
        finally
        {
            _refreshingPcManager = false;
        }
    }

    private async Task ChangeEyeCareEnabledAsync()
    {
        if (_loading || _state is null)
            return;

        var enabled = _eyeCareToggle.IsChecked == true;
        await ChangeEyeCareSettingAsync(
            previous => DisplaySettingsController.SetEyeCareEnabled(
                enabled,
                previous));
    }

    private async Task ChangeEyeColorEffectAsync()
    {
        if (_loading ||
            _state is null ||
            !_state.EyeCare.Enabled ||
            _eyeColorEffectCombo.SelectedItem is not ComboBoxItem
            {
                Tag: int value
            })
        {
            return;
        }

        await ChangeEyeCareSettingAsync(
            previous => DisplaySettingsController.SetEyeCareColorEffect(
                (EyeCareColorEffect)value,
                previous));
    }

    private async Task ChangeEyeScheduleAsync()
    {
        if (_loading ||
            _state is null ||
            !_state.EyeCare.Enabled ||
            _eyeScheduleCombo.SelectedItem is not ComboBoxItem
            {
                Tag: int value
            })
        {
            return;
        }

        await ChangeEyeCareSettingAsync(
            previous => DisplaySettingsController.SetEyeCareScheduleMode(
                (EyeCareScheduleMode)value,
                previous));
    }

    private async Task ChangeEyeCustomTemperatureAsync()
    {
        if (_loading ||
            _state is null ||
            !_state.EyeCare.Enabled ||
            _eyeColorEffectCombo.SelectedItem is not ComboBoxItem
            {
                Tag: int effectValue
            } ||
            effectValue != (int)EyeCareColorEffect.Custom)
        {
            return;
        }

        var customTemperature = SelectedCustomTemperature(
            _state.EyeCare.CustomTemperature);
        await ChangeEyeCareSettingAsync(
            previous =>
                DisplaySettingsController.SetEyeCareCustomTemperature(
                    customTemperature,
                    previous));
    }

    private async Task ChangeEyeCareSettingAsync(
        Func<EyeCareState, EyeCareState> update)
    {
        if (_state is null)
            return;

        _stateVersion++;
        _refreshTimer.Stop();
        var previous = _state.EyeCare;
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(() => update(previous));
            _state = _state with { EyeCare = confirmed };
            ApplyEyeCareState(confirmed);
        }
        catch (Exception ex)
        {
            ApplyEyeCareState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
            RestartRefreshTimer();
        }
    }

    private async Task ChangePcManagerEyeCareEnabledAsync()
    {
        if (_loading || _state is null)
            return;

        var enabled = _pcManagerEyeCareToggle.IsChecked == true;
        await ChangePcManagerSettingAsync(defaults =>
            PcManagerEyeCareController.SetEnabled(enabled, defaults));
    }

    private async Task ChangePcManagerTemperatureAsync(int temperature)
    {
        if (_loading ||
            _state is null ||
            _state.PcManagerEyeCare.Enabled)
        {
            return;
        }

        await ChangePcManagerSettingAsync(defaults =>
            PcManagerEyeCareController.SetTemperature(
                temperature,
                defaults));
    }

    private async Task RestorePcManagerDefaultAsync()
    {
        if (_loading || _state is null)
            return;

        await ChangePcManagerSettingAsync(
            PcManagerEyeCareController.RestoreConfiguredDefault);
    }

    private async Task ChangePcManagerSettingAsync(
        Func<PcManagerEyeCareDefaults, PcManagerEyeCareState> update)
    {
        if (_state is null)
            return;

        _stateVersion++;
        _temperatureApplyTimer.Stop();
        _refreshTimer.Stop();
        _pcManagerRefreshTimer.Stop();
        var previous = _state.PcManagerEyeCare;
        var defaults = _readPcManagerDefaults();
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(() => update(defaults));
            _state = _state with { PcManagerEyeCare = confirmed };
            ApplyPcManagerEyeCareState(confirmed);
        }
        catch (Exception ex)
        {
            ApplyPcManagerEyeCareState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
            RestartRefreshTimer();
            if (IsVisible || _embeddedMode)
                _pcManagerRefreshTimer.Start();
        }
    }

    private void ShowPcManagerDefaultsWindow()
    {
        var window = new PcManagerEyeCareDefaultsWindow(
            _t,
            _isDark,
            FontFamily,
            FontSize,
            _readPcManagerDefaults())
        {
            Owner = this
        };
        if (window.ShowDialog() != true)
            return;

        var defaults = window.Defaults.Normalize();
        _savePcManagerDefaults(defaults);
        if (_state is null)
            return;

        var state = _state.PcManagerEyeCare with
        {
            NormalDefaultTemperature = defaults.NormalTemperature,
            EyeCareDefaultTemperature = defaults.EyeCareTemperature
        };
        _state = _state with { PcManagerEyeCare = state };
        ApplyPcManagerEyeCareState(state);
    }

    private async Task ChangeColorManagementAsync()
    {
        if (_loading ||
            _state is null ||
            _colorManagementCombo.SelectedItem is not ComboBoxItem
            {
                Tag: int value
            })
        {
            return;
        }

        _stateVersion++;
        _refreshTimer.Stop();
        var previous = _state.ColorManagement;
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(
                () => DisplaySettingsController.SetColorManagementMode(
                    (ColorManagementMode)value));
            _state = _state with { ColorManagement = confirmed };
            ApplyColorManagementState(confirmed);
        }
        catch (Exception ex)
        {
            ApplyColorManagementState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
            RestartRefreshTimer();
        }
    }

    private void ApplyState(DisplaySettingsState state)
    {
        ApplyEyeCareState(state.EyeCare);
        ApplyPcManagerEyeCareState(state.PcManagerEyeCare);
        ApplyColorManagementState(state.ColorManagement);
    }

    private void ApplyEyeCareState(EyeCareState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _eyeCareToggle.IsChecked = state.Available && state.Enabled;
        _eyeCareToggle.Content = _t(state.Enabled ? "On" : "Off");
        _eyeCareToggle.ToolTip = state.Error;

        _eyeColorEffectCombo.Items.Clear();
        AddChoice(
            _eyeColorEffectCombo,
            _t("EyeCareVivid"),
            (int)EyeCareColorEffect.Vivid,
            state.Available);
        AddChoice(
            _eyeColorEffectCombo,
            _t("EyeCareVisionCare"),
            (int)EyeCareColorEffect.VisionCare,
            state.Available);
        AddChoice(
            _eyeColorEffectCombo,
            _t("EyeCareAmber"),
            (int)EyeCareColorEffect.Amber,
            state.Available);
        AddChoice(
            _eyeColorEffectCombo,
            _t("EyeCareCustom"),
            (int)EyeCareColorEffect.Custom,
            state.Available);
        SelectChoice(_eyeColorEffectCombo, (int)state.ColorEffect);

        _eyeScheduleCombo.Items.Clear();
        AddChoice(
            _eyeScheduleCombo,
            _t("EyeCareAlways"),
            (int)EyeCareScheduleMode.Always,
            state.Available);
        AddChoice(
            _eyeScheduleCombo,
            _t("EyeCareNight"),
            (int)EyeCareScheduleMode.Night,
            state.Available);
        SelectChoice(_eyeScheduleCombo, (int)state.ScheduleMode);
        SelectCustomTemperature(state.CustomTemperature);

        _eyeCareStatus.Text = state.Error is not null
            ? string.Format(_t("SettingsReadFailedFormat"), state.Error)
            : state.Available
                ? string.Format(
                    _t("EyeCareStatusFormat"),
                    state.ApiCapability ? _t("ApiAvailable") : _t("ApiUnavailable"),
                    state.OobeTemperature)
                : _t("NotSupported");
        _loading = wasLoading;
        UpdateEyeCareControlAvailability();
    }

    private void ApplyPcManagerEyeCareState(PcManagerEyeCareState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _pcManagerEyeCareToggle.IsChecked = state.Available && state.Enabled;
        _pcManagerEyeCareToggle.Content = _t(state.Enabled ? "On" : "Off");
        _pcManagerEyeCareToggle.ToolTip = state.Error;
        if (!_pcManagerTemperature.IsUserInteracting)
            _pcManagerTemperature.Value = state.CurrentTemperature;

        _pcManagerEyeCareStatus.Text = !state.Available
            ? state.Error is not null
                ? string.Format(
                    _t("SettingsReadFailedFormat"),
                    state.Error)
                : _t("NotSupported")
            : string.Format(
                _t("PcManagerEyeCareStatusFormat"),
                state.CurrentTemperature,
                state.NormalDefaultTemperature,
                state.EyeCareDefaultTemperature,
                state.DllCapability
                    ? _t("ApiAvailable")
                    : _t("ApiUnavailable"));
        _loading = wasLoading;
        UpdatePcManagerControlAvailability();
    }

    private void ApplyColorManagementState(ColorManagementState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _colorManagementCombo.Items.Clear();
        foreach (var mode in ColorManagementDisplayOrder(state))
        {
            AddChoice(
                _colorManagementCombo,
                _t(ColorManagementKey(mode)),
                (int)mode,
                state.Available && state.IsSupported(mode));
        }

        SelectChoice(_colorManagementCombo, (int)state.Mode);
        _colorManagementStatus.Text = state.Error is not null
            ? string.Format(_t("SettingsReadFailedFormat"), state.Error)
            : state.Available
                ? string.Format(
                    _t("ColorManagementStatusFormat"),
                    ColorTypeName(state.ColorTypeDetail),
                    state.OptionsColor,
                    state.Is24H2OrLater ? _t("Yes") : _t("No"))
                : _t("NotSupported");
        _loading = wasLoading;
    }

    private static IEnumerable<ColorManagementMode> ColorManagementDisplayOrder(
        ColorManagementState state)
    {
        var order = new[]
        {
            ColorManagementMode.Default,
            ColorManagementMode.AdobeRgb,
            ColorManagementMode.Srgb,
            ColorManagementMode.DisplayP3,
            ColorManagementMode.Native,
            ColorManagementMode.Rec709,
            ColorManagementMode.DciP3,
            ColorManagementMode.Auto,
            ColorManagementMode.DicomDim,
            ColorManagementMode.DicomOffice
        };
        return order.Where(mode =>
            state.Mode == mode ||
            state.IsSupported(mode) ||
            mode is ColorManagementMode.Default or
                ColorManagementMode.AdobeRgb or
                ColorManagementMode.Srgb or
                ColorManagementMode.DisplayP3 or
                ColorManagementMode.Native or
                ColorManagementMode.Rec709 or
                ColorManagementMode.DciP3);
    }

    private void UpdateEyeCareControlAvailability()
    {
        var eyeAvailable = !_loading &&
                           _state?.EyeCare.Available == true &&
                           _state.EyeCare.Error is null;
        var eyeEnabled = eyeAvailable && _state?.EyeCare.Enabled == true;
        _eyeCareToggle.IsEnabled = eyeAvailable;
        _eyeColorEffectCombo.IsEnabled = eyeEnabled;
        _eyeScheduleCombo.IsEnabled = eyeEnabled;
        _customTemperatureCombo.IsEnabled =
            eyeEnabled &&
            _eyeColorEffectCombo.SelectedItem is ComboBoxItem
            {
                Tag: int value
            } &&
            value == (int)EyeCareColorEffect.Custom;
    }

    private void UpdatePcManagerControlAvailability()
    {
        var available = !_loading &&
                        _state?.PcManagerEyeCare.Available == true;
        var enabled = _state?.PcManagerEyeCare.Enabled == true;
        _pcManagerEyeCareToggle.IsEnabled = available;
        _pcManagerTemperature.IsEnabled = available && !enabled;
        _pcManagerRestoreButton.IsEnabled = available;
        _pcManagerDefaultsButton.IsEnabled = !_loading;
    }

    private void SetBusy(bool busy)
    {
        _loading = busy;
        UpdateEyeCareControlAvailability();
        UpdatePcManagerControlAvailability();
        _colorManagementCombo.IsEnabled =
            !busy &&
            _state?.ColorManagement.Available == true &&
            _state.ColorManagement.Error is null;
        _refreshButton.IsEnabled = !busy;
        _closeButton.IsEnabled = !busy;
    }

    private TimeSpan CurrentRefreshInterval()
    {
        var interval = _refreshInterval();
        return interval < TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(5)
            : interval;
    }

    private void SyncRefreshTimerInterval()
    {
        var interval = CurrentRefreshInterval();
        if (_refreshTimer.Interval != interval)
            _refreshTimer.Interval = interval;
    }

    private void RestartRefreshTimer()
    {
        SyncRefreshTimerInterval();
        if (IsVisible || _embeddedMode)
            _refreshTimer.Start();
    }

    private int SelectedCustomTemperature(int fallback) =>
        _customTemperatureCombo.SelectedItem is ComboBoxItem { Tag: int value }
            ? value
            : fallback;

    private void SelectCustomTemperature(int value)
    {
        if (value <= 0)
            value = 5000;

        foreach (var item in _customTemperatureCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int itemValue } &&
                itemValue == value)
            {
                _customTemperatureCombo.SelectedItem = item;
                return;
            }
        }

        _customTemperatureCombo.SelectedIndex = 23;
    }

    private static void AddChoice(
        ItemsControl combo,
        string text,
        int value,
        bool enabled = true)
    {
        combo.Items.Add(new ComboBoxItem
        {
            Content = text,
            Tag = value,
            IsEnabled = enabled
        });
    }

    private static void SelectChoice(ComboBox combo, int value)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem { Tag: int itemValue } &&
                itemValue == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private string ColorTypeName(string colorTypeDetail)
    {
        if (colorTypeDetail.Length >= 3)
        {
            return colorTypeDetail[2] == '1' ? "TCON" :
                colorTypeDetail[1] == '1' ? "X-Rite" :
                colorTypeDetail[0] == '1' ? "Multi" :
                _t("Unknown");
        }

        return _t("Unknown");
    }

    private static string ColorManagementKey(ColorManagementMode mode) =>
        mode switch
        {
            ColorManagementMode.Default => "ColorModeDefault",
            ColorManagementMode.AdobeRgb => "ColorModeAdobeRgb",
            ColorManagementMode.Srgb => "ColorModeSrgb",
            ColorManagementMode.DisplayP3 => "ColorModeDisplayP3",
            ColorManagementMode.Native => "ColorModeNative",
            ColorManagementMode.Rec709 => "ColorModeRec709",
            ColorManagementMode.DciP3 => "ColorModeDciP3",
            ColorManagementMode.Auto => "ColorModeAuto",
            ColorManagementMode.DicomDim => "ColorModeDicomDim",
            ColorManagementMode.DicomOffice => "ColorModeDicomOffice",
            _ => "Unknown"
        };

    private void ShowWriteError(Exception exception)
    {
        MessageBox.Show(
            this,
            string.Format(_t("SettingWriteFailedFormat"), exception.Message),
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ApplyTheme(bool isDark)
    {
        Background = Brush(isDark ? "#111827" : "#ffffff");
        Foreground = Brush(isDark ? "#f9fafb" : "#111827");
        _eyeCareToggle.Foreground = Foreground;
        _eyeColorEffectCombo.Foreground = SystemColors.ControlTextBrush;
        _eyeScheduleCombo.Foreground = SystemColors.ControlTextBrush;
        _customTemperatureCombo.Foreground = SystemColors.ControlTextBrush;
        _pcManagerEyeCareToggle.Foreground = Foreground;
        _colorManagementCombo.Foreground = SystemColors.ControlTextBrush;
    }

    private static TextBlock StatusText() => new()
    {
        Opacity = 0.72,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
