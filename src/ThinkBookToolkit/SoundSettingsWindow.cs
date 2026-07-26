using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class SoundSettingsWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly bool _embeddedMode;
    private readonly ComboBox _dolbyCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly ComboBox _noiseCombo = new()
    {
        Width = 190,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly CheckBox _speakerNoiseToggle = new()
    {
        MinWidth = 96,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _dolbyStatus = StatusText();
    private readonly TextBlock _speakerNoiseStatus = StatusText();
    private readonly TextBlock _noiseStatus = StatusText();
    private readonly TextBlock _voiceRecordStatus = StatusText();
    private readonly Button _recordVoiceButton = new() { MinWidth = 150 };
    private readonly Button _refreshButton = new() { MinWidth = 76 };
    private readonly Button _closeButton = new()
    {
        MinWidth = 76,
        IsCancel = true
    };
    private readonly DispatcherTimer _recordTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly DispatcherTimer _refreshTimer;
    private SoundSettingsState? _state;
    private bool _loading = true;
    private bool _refreshing;
    private bool _recording;
    private int _recordSecondsRemaining;

    public SoundSettingsWindow(
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        Func<TimeSpan> refreshInterval,
        bool embeddedMode = false)
    {
        _t = translate;
        _refreshInterval = refreshInterval;
        _embeddedMode = embeddedMode;
        _refreshTimer = new DispatcherTimer
        {
            Interval = CurrentRefreshInterval()
        };
        Title = _t("SoundSettings");
        Width = 620;
        Height = 510;
        MinWidth = 560;
        MinHeight = 400;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Content = BuildLayout();
        ApplyTheme(isDark);
        _recordTimer.Tick += (_, _) => UpdateRecordCountdown();
        _refreshTimer.Tick += async (_, _) =>
        {
            SyncRefreshTimerInterval();
            await LoadStateAsync();
        };
        Loaded += async (_, _) =>
        {
            _loading = false;
            SyncRefreshTimerInterval();
            await LoadStateAsync(showReading: true);
            _refreshTimer.Start();
        };
        if (!_embeddedMode)
        {
            Closing += (_, args) =>
            {
                if (_recording)
                    args.Cancel = true;
            };
        }
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _recordTimer.Stop();
            SoundSettingsController.Shutdown();
        };
    }

    private UIElement BuildLayout()
    {
        _dolbyCombo.SelectionChanged +=
            async (_, _) => await ChangeDolbyAsync();
        _noiseCombo.SelectionChanged +=
            async (_, _) => await ChangeNoiseModeAsync();
        _speakerNoiseToggle.Click +=
            async (_, _) => await ChangeSpeakerNoiseAsync();
        _recordVoiceButton.Click +=
            async (_, _) => await RecordVoiceIdAsync();
        _refreshButton.Content = _t("Refresh");
        _refreshButton.Click += async (_, _) =>
            await LoadStateAsync(showReading: true);
        _closeButton.Content = _t("Close");
        _closeButton.Click += (_, _) => Close();

        var content = new StackPanel();
        var report = FeatureAvailabilityCache.Current;
        if (report is null || report.IsAvailable(FeatureIds.DolbyAtmos))
        {
            content.Children.Add(BuildSettingGroup(
                _t("DolbyAtmos"),
                _t("DolbyAtmosDescription"),
                _dolbyCombo,
                _dolbyStatus));
        }
        if (report is null ||
            report.IsAvailable(FeatureIds.SpeakerNoiseCancellation))
        {
            content.Children.Add(BuildSettingGroup(
                _t("SpeakerNoiseCancellation"),
                _t("SpeakerNoiseCancellationDescription"),
                _speakerNoiseToggle,
                _speakerNoiseStatus));
        }
        if (report is null ||
            report.IsAvailable(FeatureIds.MicrophoneNoiseCancellation))
        {
            content.Children.Add(BuildSettingGroup(
                _t("MicrophoneNoiseCancellation"),
                _t("MicrophoneNoiseCancellationDescription"),
                _noiseCombo,
                _noiseStatus,
                BuildVoiceRecordPanel()));
        }

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
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.Children.Add(content);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        return root;
    }

    private Border BuildSettingGroup(
        string title,
        string description,
        UIElement control,
        TextBlock status,
        UIElement? footer = null)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = FontSize + 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        row.Children.Add(heading);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);

        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });
        panel.Children.Add(status);
        if (footer is not null)
            panel.Children.Add(footer);

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

    private UIElement BuildVoiceRecordPanel()
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = _t("RecordMyVoice"),
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = _t("RecordMyVoiceDescription"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 12, 0)
        });
        row.Children.Add(text);

        _recordVoiceButton.Content = _t("Record");
        _recordVoiceButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_recordVoiceButton, 1);
        row.Children.Add(_recordVoiceButton);

        var panel = new StackPanel();
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("#4b5563"),
            Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0)
        });
        panel.Children.Add(row);
        panel.Children.Add(_voiceRecordStatus);
        return panel;
    }

    private async Task LoadStateAsync(bool showReading = false)
    {
        if (_loading || _refreshing || _recording)
            return;

        _refreshing = true;
        if (showReading)
        {
            SetBusy(true);
            _dolbyStatus.Text = _t("ReadingSettings");
            _speakerNoiseStatus.Text = _t("ReadingSettings");
            _noiseStatus.Text = _t("ReadingSettings");
        }

        try
        {
            _state = await Task.Run(SoundSettingsController.ReadState);
            ApplyState(_state);
        }
        finally
        {
            if (showReading)
                SetBusy(false);
            _refreshing = false;
            SyncRefreshTimerInterval();
        }
    }

    private async Task ChangeDolbyAsync()
    {
        if (_loading ||
            _refreshing ||
            _state is null ||
            _dolbyCombo.SelectedItem is not ComboBoxItem { Tag: int value })
        {
            return;
        }

        var previous = _state.Dolby;
        SetBusy(true);
        try
        {
            var enabled = value >= 0;
            var profile = enabled
                ? (DolbyProfile)value
                : previous.Profile;
            var confirmed = await Task.Run(
                () => SoundSettingsController.SetDolbyState(
                    enabled,
                    profile));
            _state = _state with { Dolby = confirmed };
            ApplyDolbyState(confirmed);
        }
        catch (Exception ex)
        {
            ApplyDolbyState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ChangeNoiseModeAsync()
    {
        if (_loading ||
            _refreshing ||
            _state is null ||
            _noiseCombo.SelectedItem is not ComboBoxItem { Tag: int value })
        {
            return;
        }

        var previous = _state.MicrophoneNoise;
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(
                () => SoundSettingsController.SetMicrophoneNoiseMode(
                    (MicrophoneNoiseMode)value));
            _state = _state with { MicrophoneNoise = confirmed };
            ApplyNoiseState(confirmed);
        }
        catch (Exception ex)
        {
            ApplyNoiseState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ChangeSpeakerNoiseAsync()
    {
        if (_loading || _refreshing || _state is null)
            return;

        var previous = _state.SpeakerNoise;
        var enabled = _speakerNoiseToggle.IsChecked == true;
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(
                () => SoundSettingsController.SetSpeakerNoiseEnabled(
                    enabled));
            _state = _state with { SpeakerNoise = confirmed };
            ApplySpeakerNoiseState(confirmed);
        }
        catch (Exception ex)
        {
            ApplySpeakerNoiseState(previous);
            ShowWriteError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RecordVoiceIdAsync()
    {
        if (_loading ||
            _refreshing ||
            _recording ||
            _state?.MicrophoneNoise is not { Available: true } noiseState ||
            !noiseState.SupportsOnlyMyVoice)
        {
            return;
        }

        if (noiseState.HasVoiceId)
        {
            var result = MessageBox.Show(
                this,
                _t("ReplaceVoiceIdWarning"),
                _t("RecordMyVoice"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        var previous = noiseState;
        _recording = true;
        _recordSecondsRemaining = 20;
        UpdateRecordStatus();
        _recordTimer.Start();
        SetBusy(true);
        try
        {
            var confirmed = await Task.Run(
                () => SoundSettingsController.RecordMicrophoneVoiceId(
                    previous.HasVoiceId));
            _state = _state with { MicrophoneNoise = confirmed };
            ApplyNoiseState(confirmed);
            _voiceRecordStatus.Text = _t("VoiceRecordComplete");
        }
        catch (Exception ex)
        {
            try
            {
                var refreshed = await Task.Run(
                    SoundSettingsController.ReadState);
                _state = refreshed;
                ApplyNoiseState(refreshed.MicrophoneNoise);
            }
            catch
            {
                ApplyNoiseState(previous);
            }
            _voiceRecordStatus.Text = string.Format(
                _t("VoiceRecordFailedFormat"),
                ex.Message);
            ShowWriteError(ex);
        }
        finally
        {
            _recordTimer.Stop();
            _recording = false;
            SetBusy(false);
        }
    }

    private void UpdateRecordCountdown()
    {
        if (!_recording)
            return;

        if (_recordSecondsRemaining > 0)
            _recordSecondsRemaining--;
        UpdateRecordStatus();
    }

    private void UpdateRecordStatus()
    {
        _voiceRecordStatus.Text = _recordSecondsRemaining > 0
            ? string.Format(
                _t("VoiceRecordingCountdownFormat"),
                _recordSecondsRemaining)
            : _t("VoiceRecordProcessing");
    }

    private void ApplyState(SoundSettingsState state)
    {
        ApplyDolbyState(state.Dolby);
        ApplySpeakerNoiseState(state.SpeakerNoise);
        ApplyNoiseState(state.MicrophoneNoise);
    }

    private void ApplyDolbyState(DolbyState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _dolbyCombo.Items.Clear();
        AddChoice(_dolbyCombo, _t("DolbyDynamic"), (int)DolbyProfile.Dynamic);
        AddChoice(_dolbyCombo, _t("DolbyMovie"), (int)DolbyProfile.Movie);
        AddChoice(_dolbyCombo, _t("DolbyMusic"), (int)DolbyProfile.Music);
        AddChoice(_dolbyCombo, _t("DolbyGame"), (int)DolbyProfile.Game);
        AddChoice(_dolbyCombo, _t("DolbyVoice"), (int)DolbyProfile.Voice);
        AddChoice(_dolbyCombo, _t("DolbyCustom"), (int)DolbyProfile.Custom);
        AddChoice(_dolbyCombo, _t("Off"), -1);

        var selectedValue = state.Enabled ? (int)state.Profile : -1;
        SelectChoice(_dolbyCombo, selectedValue);
        _dolbyStatus.Text = state.Error is not null
            ? string.Format(_t("SettingsReadFailedFormat"), state.Error)
            : state.Available
                ? _t("DolbyDriverControlled")
                : _t("NotSupported");
        _loading = wasLoading;
    }

    private void ApplySpeakerNoiseState(SpeakerNoiseState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _speakerNoiseToggle.IsChecked = state.Available && state.Enabled;
        _speakerNoiseToggle.Content = _t(state.Enabled ? "On" : "Off");
        _speakerNoiseToggle.ToolTip = state.Error;
        _speakerNoiseStatus.Text = state.Error is not null
            ? string.Format(_t("SettingsReadFailedFormat"), state.Error)
            : state.Available
                ? _t("SpeakerNoiseDriverControlled")
                : _t("NotSupported");
        _loading = wasLoading;
    }

    private void ApplyNoiseState(MicrophoneNoiseState state)
    {
        var wasLoading = _loading;
        _loading = true;
        _noiseCombo.Items.Clear();
        AddChoice(
            _noiseCombo,
            _t("NoiseNormal"),
            (int)MicrophoneNoiseMode.Normal);
        AddChoice(
            _noiseCombo,
            _t("NoiseVoiceRecognition"),
            (int)MicrophoneNoiseMode.VoiceRecognition,
            state.SupportsVoiceRecognition);
        AddChoice(
            _noiseCombo,
            state.HasVoiceId
                ? _t("NoiseOnlyMyVoice")
                : _t("NoiseOnlyMyVoiceNeedsEnrollment"),
            (int)MicrophoneNoiseMode.OnlyMyVoice,
            state.SupportsOnlyMyVoice && state.HasVoiceId);
        AddChoice(
            _noiseCombo,
            _t("NoiseMultipleVoices"),
            (int)MicrophoneNoiseMode.MultipleVoices,
            state.SupportsMultipleVoices);
        AddChoice(
            _noiseCombo,
            _t("Off"),
            (int)MicrophoneNoiseMode.Off);
        SelectChoice(_noiseCombo, (int)state.Mode);

        _noiseStatus.Text = state.Error is not null
            ? string.Format(_t("SettingsReadFailedFormat"), state.Error)
            : state.Available
                ? string.Format(
                    _t("NoiseVendorFormat"),
                    NoiseVendorName(state.VendorId))
                : _t("NotSupported");
        _recordVoiceButton.Content = state.HasVoiceId
            ? _t("RecordAgain")
            : _t("Record");
        if (!_recording)
        {
            _voiceRecordStatus.Text = state.SupportsOnlyMyVoice
                ? _t(state.HasVoiceId
                    ? "VoiceIdRecorded"
                    : "VoiceIdNotRecorded")
                : _t("NotSupported");
        }
        _loading = wasLoading;
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

    private void SetBusy(bool busy)
    {
        _loading = busy;
        _dolbyCombo.IsEnabled =
            !busy && _state?.Dolby.Available == true;
        _speakerNoiseToggle.IsEnabled =
            !busy && _state?.SpeakerNoise.Available == true;
        _noiseCombo.IsEnabled =
            !busy && _state?.MicrophoneNoise.Available == true;
        _recordVoiceButton.IsEnabled =
            !busy &&
            _state?.MicrophoneNoise is
            {
                Available: true,
                SupportsOnlyMyVoice: true
            };
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

    private void ShowWriteError(Exception exception)
    {
        MessageBox.Show(
            this,
            string.Format(_t("SettingWriteFailedFormat"), exception.Message),
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private string NoiseVendorName(int vendorId) => vendorId switch
    {
        1 => "Elevoc",
        2 => "ForteMedia",
        3 => "AISpeech",
        _ => _t("Unknown")
    };

    private void ApplyTheme(bool isDark)
    {
        Background = Brush(isDark ? "#111827" : "#ffffff");
        Foreground = Brush(isDark ? "#f9fafb" : "#111827");
        _speakerNoiseToggle.Foreground = Foreground;
        _dolbyCombo.Foreground = SystemColors.ControlTextBrush;
        _noiseCombo.Foreground = SystemColors.ControlTextBrush;
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
