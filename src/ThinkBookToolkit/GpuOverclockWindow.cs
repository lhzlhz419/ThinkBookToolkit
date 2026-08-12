using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class GpuOverclockWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly bool _isChinese;
    private readonly ToolkitPalette _palette;
    private readonly NumericEditor _coreOffset;
    private readonly NumericEditor _memoryOffset;
    private readonly TextBox _minimumCoreClock = ClockBox();
    private readonly TextBox _maximumCoreClock = ClockBox();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _restore;
    private readonly Button _apply;
    private readonly Button _applyAndClose;

    public GpuOverclockWindow(
        Window? owner,
        ToolkitRuntimeService runtime,
        FontFamily fontFamily,
        double fontSize)
    {
        if (owner is not null)
            Owner = owner;
        _runtime = runtime;
        _isChinese = runtime.IsChinese;
        _palette = ToolkitPalette.For(runtime.IsDark);
        Title = T("独立显卡超频设置", "Discrete GPU overclock settings");
        Width = 780;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);

        var settings = GpuOverclockPolicy.Normalize(
            runtime.Settings.GpuOverclock);
        _coreOffset = new NumericEditor(
            GpuOverclockPolicy.MinimumCoreOffsetMhz,
            GpuOverclockPolicy.MaximumCoreOffsetMhz,
            settings.CoreFrequencyOffsetMhz);
        _memoryOffset = new NumericEditor(
            GpuOverclockPolicy.MinimumMemoryOffsetMhz,
            GpuOverclockPolicy.MaximumMemoryOffsetMhz,
            settings.MemoryFrequencyOffsetMhz);
        _minimumCoreClock.Text =
            settings.MinimumCoreFrequencyMhz?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty;
        _maximumCoreClock.Text =
            settings.MaximumCoreFrequencyMhz?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T(
                "超频和锁频可能造成不稳定、数据丢失或硬件损坏。不要与其它超频软件一起使用；如需更完整的功能，建议改用 MSI Afterburner。",
                "Overclocking and clock locking may cause instability, data loss, or hardware damage. Do not use Toolkit together with other overclocking software; use MSI Afterburner instead for more complete features."),
            Foreground = Brush(_palette.Danger),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16)
        });
        root.Children.Add(Field(
            T("核心频率偏移量", "Core frequency offset"),
            T("范围 -500 到 +500 MHz", "Range: -500 to +500 MHz"),
            _coreOffset.View));
        root.Children.Add(Field(
            T("显存频率偏移量", "Memory frequency offset"),
            T("范围 -1000 到 +3000 MHz", "Range: -1000 to +3000 MHz"),
            _memoryOffset.View));
        root.Children.Add(Field(
            T("限制核心频率", "Limit core frequency"),
            T(
                "下限和上限均为 0–3500 MHz；全部留空代表不限制。",
                "Both limits must be 0–3500 MHz; leave both blank for no limit."),
            ClockLimitEditor()));

        _status.Foreground = Brush(_palette.Danger);
        _status.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_status);

        _restore = new Button
        {
            Content = T("恢复默认", "Restore defaults"),
            MinWidth = 116,
            MinHeight = 38,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var enabled = runtime.Settings.GpuOverclock.Enabled;
        _apply = new Button
        {
            Content = enabled ? T("应用", "Apply") : T("保存", "Save"),
            MinWidth = 104,
            MinHeight = 38
        };
        _applyAndClose = new Button
        {
            Content = enabled
                ? T("应用并关闭", "Apply and close")
                : T("保存并关闭", "Save and close"),
            MinWidth = 126,
            MinHeight = 38,
            IsDefault = true,
            Margin = new Thickness(10, 0, 0, 0)
        };
        _restore.Click += async (_, _) => await RestoreDefaultsAsync();
        _apply.Click += async (_, _) => await SaveAsync(close: false);
        _applyAndClose.Click += async (_, _) => await SaveAsync(close: true);

        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(_restore);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_apply);
        right.Children.Add(_applyAndClose);
        Grid.SetColumn(right, 1);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        Content = root;
    }

    private FrameworkElement ClockLimitEditor()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(_minimumCoreClock);
        panel.Children.Add(new TextBlock
        {
            Text = "~",
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        panel.Children.Add(_maximumCoreClock);
        panel.Children.Add(new TextBlock
        {
            Text = "MHz",
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        return panel;
    }

    private Border Field(
        string title,
        string description,
        UIElement editor)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0)
        };
        label.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold
        });
        label.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush(_palette.Muted),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0)
        });
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return new Border
        {
            Background = Brush(_palette.SurfaceRaised),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 9),
            Child = grid
        };
    }

    private async Task RestoreDefaultsAsync()
    {
        _coreOffset.SetValue(0);
        _memoryOffset.SetValue(0);
        _minimumCoreClock.Clear();
        _maximumCoreClock.Clear();
        SetButtonsEnabled(false);
        _status.Text = T("正在恢复默认设置……", "Restoring defaults…");
        var error = await _runtime.SaveGpuOverclockSettingsAsync(
            new GpuOverclockSettings(),
            applyEvenIfDisabled: true);
        _status.Text = error ?? T(
            "已恢复默认设置。",
            "Default settings were restored.");
        _status.Foreground = Brush(
            error is null ? _palette.Success : _palette.Danger);
        SetButtonsEnabled(true);
    }

    private async Task SaveAsync(bool close)
    {
        if (!TryCollect(out var settings, out var validation))
        {
            _status.Foreground = Brush(_palette.Danger);
            _status.Text = validation;
            return;
        }
        SetButtonsEnabled(false);
        _status.Foreground = Brush(_palette.Muted);
        _status.Text = _runtime.Settings.GpuOverclock.Enabled
            ? T("正在应用……", "Applying…")
            : T("正在保存……", "Saving…");
        var error = await _runtime.SaveGpuOverclockSettingsAsync(settings);
        if (error is not null)
        {
            _status.Foreground = Brush(_palette.Danger);
            _status.Text = error;
            SetButtonsEnabled(true);
            return;
        }
        if (close)
        {
            Close();
            return;
        }
        _status.Foreground = Brush(_palette.Success);
        _status.Text = _runtime.Settings.GpuOverclock.Enabled
            ? T("设置已应用。", "Settings applied.")
            : T("设置已保存。", "Settings saved.");
        SetButtonsEnabled(true);
    }

    private bool TryCollect(
        out GpuOverclockSettings settings,
        out string error)
    {
        settings = new GpuOverclockSettings();
        if (!_coreOffset.TryGetValue(out var core))
        {
            error = T(
                "核心频率偏移量必须在 -500 到 +500 之间。",
                "Core frequency offset must be between -500 and +500.");
            return false;
        }
        if (!_memoryOffset.TryGetValue(out var memory))
        {
            error = T(
                $"显存频率偏移量必须在 {GpuOverclockPolicy.MinimumMemoryOffsetMhz} 到 +{GpuOverclockPolicy.MaximumMemoryOffsetMhz} 之间。",
                $"Memory frequency offset must be between {GpuOverclockPolicy.MinimumMemoryOffsetMhz} and +{GpuOverclockPolicy.MaximumMemoryOffsetMhz}.");
            return false;
        }

        var minimumText = _minimumCoreClock.Text.Trim();
        var maximumText = _maximumCoreClock.Text.Trim();
        int? minimum = null;
        int? maximum = null;
        if (minimumText.Length != 0 || maximumText.Length != 0)
        {
            if (!int.TryParse(
                    minimumText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var minimumValue) ||
                !int.TryParse(
                    maximumText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var maximumValue) ||
                minimumValue is < 0 or > 3500 ||
                maximumValue is < 0 or > 3500 ||
                maximumValue < minimumValue)
            {
                error = T(
                    "核心频率上下限必须同时填写为 0–3500 的整数，且上限不得小于下限。",
                    "Enter both core clock limits as integers from 0 to 3500; the maximum cannot be lower than the minimum.");
                return false;
            }
            minimum = minimumValue;
            maximum = maximumValue;
        }

        settings = new GpuOverclockSettings
        {
            CoreFrequencyOffsetMhz = core,
            MemoryFrequencyOffsetMhz = memory,
            MinimumCoreFrequencyMhz = minimum,
            MaximumCoreFrequencyMhz = maximum
        };
        error = string.Empty;
        return true;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _restore.IsEnabled = enabled;
        _apply.IsEnabled = enabled;
        _applyAndClose.IsEnabled = enabled;
    }

    private string T(string chinese, string english) =>
        _isChinese ? chinese : english;

    private static TextBox ClockBox() => new()
    {
        Width = 100,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private sealed class NumericEditor
    {
        private bool _syncing;

        public NumericEditor(int minimum, int maximum, int value)
        {
            Slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Width = 300,
                Value = value
            };
            TextBox = new TextBox
            {
                Width = 86,
                Text = value.ToString(CultureInfo.InvariantCulture),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            View = new StackPanel { Orientation = Orientation.Horizontal };
            View.Children.Add(Slider);
            View.Children.Add(TextBox);
            Slider.ValueChanged += (_, _) =>
            {
                if (_syncing)
                    return;
                _syncing = true;
                TextBox.Text = ((int)Slider.Value).ToString(
                    CultureInfo.InvariantCulture);
                _syncing = false;
            };
            TextBox.TextChanged += (_, _) =>
            {
                if (_syncing ||
                    !int.TryParse(
                        TextBox.Text,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    return;
                }
                _syncing = true;
                Slider.Value = Math.Clamp(parsed, minimum, maximum);
                _syncing = false;
            };
        }

        public Slider Slider { get; }
        public TextBox TextBox { get; }
        public StackPanel View { get; }

        public bool TryGetValue(out int value) =>
            int.TryParse(
                TextBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value) &&
            value >= Slider.Minimum &&
            value <= Slider.Maximum;

        public void SetValue(int value)
        {
            _syncing = true;
            Slider.Value = value;
            TextBox.Text = value.ToString(CultureInfo.InvariantCulture);
            _syncing = false;
        }
    }
}
