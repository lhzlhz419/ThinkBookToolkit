using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ThinkBookToolkit;

internal sealed partial class OsdSettingsWindow
{
    private readonly List<ColorEditorBinding> _colorEditors = [];
    private readonly List<ThresholdEditorBinding> _thresholdEditors = [];
    private readonly ComboBox _lowFpsThresholdMode = new() { MinWidth = 210 };
    private ThresholdEditorBinding? _lowFpsThresholdEditor;

    private UIElement BuildColors()
    {
        var content = new StackPanel { Margin = new Thickness(8, 12, 8, 8) };
        content.Children.Add(ColorRow(
            _runtime.L("背景色", "Background color"),
            () => _draft.BackgroundColor,
            value => _draft.BackgroundColor = value));
        content.Children.Add(ColorRow(
            _runtime.L("类别颜色", "Category color"),
            () => _draft.CategoryColor,
            value => _draft.CategoryColor = value));
        content.Children.Add(ColorRow(
            _runtime.L("标签颜色", "Label color"),
            () => _draft.LabelColor,
            value => _draft.LabelColor = value));
        content.Children.Add(ColorRow(
            _runtime.L("数值颜色", "Value color"),
            () => _draft.ValueColor,
            value => _draft.ValueColor = value));
        content.Children.Add(ColorRow(
            _runtime.L("警告值颜色", "Warning value color"),
            () => _draft.WarningColor,
            value => _draft.WarningColor = value));
        content.Children.Add(ColorRow(
            _runtime.L("报警值颜色", "Critical value color"),
            () => _draft.CriticalColor,
            value => _draft.CriticalColor = value));
        return content;
    }

    private Border ColorRow(
        string title,
        Func<string> read,
        Action<string> write)
    {
        var editor = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        editor.Children.Add(new TextBlock
        {
            Text = "0x",
            VerticalAlignment = VerticalAlignment.Center
        });
        var value = new TextBox
        {
            Width = 110,
            MinHeight = 36,
            MaxLength = 6,
            CharacterCasing = CharacterCasing.Upper,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(2, 0, 8, 0)
        };
        editor.Children.Add(value);
        var picker = Button(_runtime.L("选择颜色", "Choose color"));
        picker.MinWidth = 100;
        editor.Children.Add(picker);
        var binding = new ColorEditorBinding(read, write, picker, value);
        _colorEditors.Add(binding);
        picker.Click += (_, _) => PickColor(binding);
        value.LostKeyboardFocus += (_, _) => CommitColor(binding);
        value.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            CommitColor(binding);
            Keyboard.ClearFocus();
        };
        SyncColor(binding);
        return Row(
            title,
            _runtime.L(
                "可从色图选择，或输入六位十六进制颜色。",
                "Choose from the color dialog or enter six hexadecimal digits."),
            editor);
    }

    private void PickColor(ColorEditorBinding binding)
    {
        var normalized = NormalizeHex(binding.Read(), "FFFFFF");
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = Drawing.Color.FromArgb(
                Convert.ToInt32(normalized[..2], 16),
                Convert.ToInt32(normalized.Substring(2, 2), 16),
                Convert.ToInt32(normalized.Substring(4, 2), 16))
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        var selected = $"{dialog.Color.R:X2}{dialog.Color.G:X2}" +
                       $"{dialog.Color.B:X2}";
        binding.Write(selected);
        Save();
        SyncColor(binding);
    }

    private void CommitColor(ColorEditorBinding binding)
    {
        if (_syncing) return;
        var value = binding.Value.Text.Trim().ToUpperInvariant();
        if (value.Length != 6 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            _status.Text = _runtime.L(
                "颜色必须是六位十六进制数。",
                "Colors must contain six hexadecimal digits.");
            SyncColor(binding);
            return;
        }
        _status.Text = string.Empty;
        binding.Write(value);
        Save();
        SyncColor(binding);
    }

    private void SyncColor(ColorEditorBinding binding)
    {
        var value = NormalizeHex(binding.Read(), "FFFFFF");
        binding.Value.Text = value;
        var color = (Color)ColorConverter.ConvertFromString("#" + value);
        binding.Picker.Background = new SolidColorBrush(color);
        var luminance = color.R * .299 + color.G * .587 + color.B * .114;
        binding.Picker.Foreground = luminance >= 150
            ? Brushes.Black
            : Brushes.White;
    }

    private UIElement BuildThresholds()
    {
        AddChoice(
            _lowFpsThresholdMode,
            _runtime.L("FPS 的百分比", "Percentage of FPS"),
            OsdLowFpsThresholdMode.PercentageOfFps);
        AddChoice(
            _lowFpsThresholdMode,
            _runtime.L("与 FPS 的差值", "Difference from FPS"),
            OsdLowFpsThresholdMode.DifferenceFromFps);
        _lowFpsThresholdMode.SelectionChanged += (_, _) =>
        {
            if (_syncing ||
                Selected<OsdLowFpsThresholdMode>(_lowFpsThresholdMode) is
                    not { } mode)
            {
                return;
            }
            _draft.LowFpsThresholdMode = mode;
            Save();
            SyncLowFpsEditor();
        };

        var content = new StackPanel { Margin = new Thickness(8, 12, 8, 8) };
        content.Children.Add(ThresholdRow(
            _runtime.L("FPS", "FPS"),
            () => _draft.FpsWarningThreshold,
            value => _draft.FpsWarningThreshold = value,
            () => _draft.FpsCriticalThreshold,
            value => _draft.FpsCriticalThreshold = value,
            0,
            1000,
            "FPS",
            descending: true));
        content.Children.Add(Row(
            _runtime.L("1% Low 计算方式", "1% Low calculation"),
            _runtime.L(
                "按平均 FPS 的比例或与平均 FPS 的差值判断颜色。",
                "Color by a percentage of average FPS or by the difference from it."),
            _lowFpsThresholdMode));
        _lowFpsThresholdEditor = CreateThresholdEditor(
            () => _draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps
                ? _draft.LowFpsWarningPercentage
                : _draft.LowFpsWarningDelta,
            value =>
            {
                if (_draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps)
                    _draft.LowFpsWarningPercentage = value;
                else
                    _draft.LowFpsWarningDelta = value;
            },
            () => _draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps
                ? _draft.LowFpsCriticalPercentage
                : _draft.LowFpsCriticalDelta,
            value =>
            {
                if (_draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps)
                    _draft.LowFpsCriticalPercentage = value;
                else
                    _draft.LowFpsCriticalDelta = value;
            },
            0,
            () => _draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps
                ? 100
                : 1000,
            () => _draft.LowFpsThresholdMode ==
                OsdLowFpsThresholdMode.PercentageOfFps,
            () => _draft.LowFpsThresholdMode ==
                    OsdLowFpsThresholdMode.PercentageOfFps
                ? "%"
                : "FPS");
        content.Children.Add(Row(
            "1% Low",
            _runtime.L(
                "警告和报警范围随上方计算方式切换。",
                "Warning and critical ranges follow the selected calculation."),
            _lowFpsThresholdEditor.Control));

        AddHighThreshold(content, "CPU " + _runtime.L("温度", "temperature"),
            () => _draft.CpuTemperatureWarning, v => _draft.CpuTemperatureWarning = v,
            () => _draft.CpuTemperatureCritical, v => _draft.CpuTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content, "GPU " + _runtime.L("热点温度", "hot-spot temperature"),
            () => _draft.GpuHotSpotTemperatureWarning, v => _draft.GpuHotSpotTemperatureWarning = v,
            () => _draft.GpuHotSpotTemperatureCritical, v => _draft.GpuHotSpotTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content, "GPU " + _runtime.L("温度", "temperature"),
            () => _draft.GpuTemperatureWarning, v => _draft.GpuTemperatureWarning = v,
            () => _draft.GpuTemperatureCritical, v => _draft.GpuTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content, _runtime.L("显存温度", "VRAM temperature"),
            () => _draft.VramTemperatureWarning, v => _draft.VramTemperatureWarning = v,
            () => _draft.VramTemperatureCritical, v => _draft.VramTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content, _runtime.L("内存温度", "RAM temperature"),
            () => _draft.MemoryTemperatureWarning, v => _draft.MemoryTemperatureWarning = v,
            () => _draft.MemoryTemperatureCritical, v => _draft.MemoryTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content, _runtime.L("硬盘温度", "Storage temperature"),
            () => _draft.StorageTemperatureWarning, v => _draft.StorageTemperatureWarning = v,
            () => _draft.StorageTemperatureCritical, v => _draft.StorageTemperatureCritical = v, "°C", 120);
        AddHighThreshold(content,
            _runtime.L(
                "CPU/GPU/内存利用率与已提交",
                "CPU/GPU/RAM utilization and committed"),
            () => _draft.UsageWarningThreshold, v => _draft.UsageWarningThreshold = v,
            () => _draft.UsageCriticalThreshold, v => _draft.UsageCriticalThreshold = v, "%", 100);
        content.Children.Add(ThresholdRow(
            _runtime.L("电池输出功率", "Battery output power"),
            () => _draft.BatteryOutputPowerWarning,
            value => _draft.BatteryOutputPowerWarning = value,
            () => _draft.BatteryOutputPowerCritical,
            value => _draft.BatteryOutputPowerCritical = value,
            -500,
            -1,
            "W",
            descending: true,
            fixedNegativeSign: true));
        return content;
    }

    private void AddHighThreshold(
        Panel content,
        string title,
        Func<int> readWarning,
        Action<int> writeWarning,
        Func<int> readCritical,
        Action<int> writeCritical,
        string unit,
        int maximum) => content.Children.Add(ThresholdRow(
        title,
        readWarning,
        writeWarning,
        readCritical,
        writeCritical,
        0,
        maximum,
        unit,
        descending: false));

    private Border ThresholdRow(
        string title,
        Func<int> readWarning,
        Action<int> writeWarning,
        Func<int> readCritical,
        Action<int> writeCritical,
        int minimum,
        int maximum,
        string unit,
        bool descending,
        bool fixedNegativeSign = false)
    {
        var binding = CreateThresholdEditor(
            readWarning,
            writeWarning,
            readCritical,
            writeCritical,
            minimum,
            () => maximum,
            () => descending,
            () => unit,
            fixedNegativeSign);
        return Row(
            title,
            descending
                ? _runtime.L(
                    "数值低于警告或报警值时改变颜色。",
                    "The color changes below the warning or critical value.")
                : _runtime.L(
                    "数值高于警告或报警值时改变颜色。",
                    "The color changes above the warning or critical value."),
            binding.Control);
    }

    private ThresholdEditorBinding CreateThresholdEditor(
        Func<int> readWarning,
        Action<int> writeWarning,
        Func<int> readCritical,
        Action<int> writeCritical,
        int minimum,
        Func<int> maximum,
        Func<bool> descending,
        Func<string> unit,
        bool fixedNegativeSign = false)
    {
        var warning = NumericBox();
        var critical = NumericBox();
        var warningUnit = new TextBlock();
        var criticalUnit = new TextBlock();
        var control = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        control.Children.Add(ThresholdValue(
            _runtime.L("警告值", "Warning"),
            warning,
            warningUnit,
            fixedNegativeSign));
        control.Children.Add(ThresholdValue(
            _runtime.L("报警值", "Critical"),
            critical,
            criticalUnit,
            fixedNegativeSign));
        var binding = new ThresholdEditorBinding(
            readWarning,
            writeWarning,
            readCritical,
            writeCritical,
            minimum,
            maximum,
            descending,
            unit,
            fixedNegativeSign,
            warning,
            critical,
            warningUnit,
            criticalUnit,
            control);
        _thresholdEditors.Add(binding);
        warning.LostKeyboardFocus += (_, _) => CommitThreshold(binding);
        critical.LostKeyboardFocus += (_, _) => CommitThreshold(binding);
        warning.KeyDown += (_, args) => CommitThresholdOnEnter(binding, args);
        critical.KeyDown += (_, args) => CommitThresholdOnEnter(binding, args);
        SyncThreshold(binding);
        return binding;
    }

    private FrameworkElement ThresholdValue(
        string title,
        TextBox box,
        TextBlock unit,
        bool negative)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(8, 0, 8, 0)
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = Brush(_palette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var value = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (negative)
        {
            value.Children.Add(new TextBlock
            {
                Text = "-",
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        value.Children.Add(box);
        unit.Margin = new Thickness(4, 0, 0, 0);
        unit.VerticalAlignment = VerticalAlignment.Center;
        value.Children.Add(unit);
        stack.Children.Add(value);
        return stack;
    }

    private static TextBox NumericBox() => new()
    {
        Width = 64,
        MinHeight = 34,
        MaxLength = 4,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private void CommitThresholdOnEnter(
        ThresholdEditorBinding binding,
        KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        CommitThreshold(binding);
        Keyboard.ClearFocus();
    }

    private void CommitThreshold(ThresholdEditorBinding binding)
    {
        if (_syncing) return;
        if (!int.TryParse(binding.Warning.Text, out var warningDisplay) ||
            !int.TryParse(binding.Critical.Text, out var criticalDisplay))
        {
            ThresholdError();
            SyncThreshold(binding);
            return;
        }
        var warning = binding.FixedNegativeSign
            ? -Math.Abs(warningDisplay)
            : warningDisplay;
        var critical = binding.FixedNegativeSign
            ? -Math.Abs(criticalDisplay)
            : criticalDisplay;
        var maximum = binding.Maximum();
        if (warning < binding.Minimum || warning > maximum ||
            critical < binding.Minimum || critical > maximum ||
            (binding.Descending()
                ? warning <= critical
                : warning >= critical))
        {
            ThresholdError();
            SyncThreshold(binding);
            return;
        }
        _status.Text = string.Empty;
        binding.WriteWarning(warning);
        binding.WriteCritical(critical);
        Save();
        SyncThreshold(binding);
    }

    private void ThresholdError() => _status.Text = _runtime.L(
        "临界值格式或顺序无效；警告值必须先于报警值触发。",
        "The threshold format or order is invalid; warning must trigger before critical.");

    private void SyncExtendedControls()
    {
        foreach (var binding in _colorEditors)
            SyncColor(binding);
        Select(_lowFpsThresholdMode, _draft.LowFpsThresholdMode);
        foreach (var binding in _thresholdEditors)
            SyncThreshold(binding);
    }

    private void SyncLowFpsEditor()
    {
        if (_lowFpsThresholdEditor is not null)
            SyncThreshold(_lowFpsThresholdEditor);
    }

    private static void SyncThreshold(ThresholdEditorBinding binding)
    {
        binding.Warning.Text = Math.Abs(binding.ReadWarning()).ToString(
            CultureInfo.InvariantCulture);
        binding.Critical.Text = Math.Abs(binding.ReadCritical()).ToString(
            CultureInfo.InvariantCulture);
        binding.WarningUnit.Text = binding.Unit();
        binding.CriticalUnit.Text = binding.Unit();
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.StartsWith("#", StringComparison.Ordinal))
            normalized = normalized[1..];
        if (normalized.StartsWith("0X", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Length == 6 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : fallback;
    }

    private sealed record ColorEditorBinding(
        Func<string> Read,
        Action<string> Write,
        Button Picker,
        TextBox Value);

    private sealed record ThresholdEditorBinding(
        Func<int> ReadWarning,
        Action<int> WriteWarning,
        Func<int> ReadCritical,
        Action<int> WriteCritical,
        int Minimum,
        Func<int> Maximum,
        Func<bool> Descending,
        Func<string> Unit,
        bool FixedNegativeSign,
        TextBox Warning,
        TextBox Critical,
        TextBlock WarningUnit,
        TextBlock CriticalUnit,
        FrameworkElement Control);
}
