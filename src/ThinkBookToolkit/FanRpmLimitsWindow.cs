using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed class FanRpmLimitsWindow : Window
{
    private readonly TextBox _fan1Minimum;
    private readonly TextBox _fan1Maximum;
    private readonly TextBox _fan2Minimum;
    private readonly TextBox _fan2Maximum;
    private readonly TextBlock _validation;
    private readonly bool _isChinese;

    public FanRpmLimitsWindow(
        Window? owner,
        FanRpmLimits current,
        FanBackendControlSemantics controlSemantics,
        bool isChinese,
        bool isDark,
        FontFamily fontFamily,
        double fontSize)
    {
        if (owner is not null)
            Owner = owner;
        _isChinese = isChinese;
        Title = T("风扇转速上下限", "Fan RPM limits");
        Width = 660;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Background = Brush(isDark ? "#101827" : "#EEF3F9");
        Foreground = Brush(isDark ? "#F3F6FC" : "#10213D");

        _fan1Minimum = RpmBox(current.Fan1MinimumRpm);
        _fan1Maximum = RpmBox(current.Fan1MaximumRpm);
        _fan2Minimum = RpmBox(current.Fan2MinimumRpm);
        _fan2Maximum = RpmBox(current.Fan2MaximumRpm);
        _validation = new TextBlock
        {
            Foreground = Brush(isDark ? "#FF8B98" : "#C6283D"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };

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
                "未自定义时优先采用设备报告的范围；读取失败则使用 1500–5500 RPM。这里设置的是 Toolkit 允许使用的目标范围，下限可按 100 RPM 步进设为 0。",
                "Until customized, Toolkit prefers the range reported by the device and falls back to 1500–5500 RPM. These values define Toolkit's allowed target range; the minimum may be set to 0 in 100-RPM increments."),
            Foreground = Brush(isDark ? "#AEBBD1" : "#5E6F89"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 16)
        });
        root.Children.Add(new TextBlock
        {
            Text = ZeroRpmDescription(controlSemantics),
            Foreground = controlSemantics.ZeroRpmBehavior ==
                         FanTargetZeroBehavior.StopFanWhileKeepingManualControl
                ? Brush(isDark ? "#FF98A4" : "#B71C36")
                : Brush(isDark ? "#AEBBD1" : "#5E6F89"),
            FontWeight = controlSemantics.ZeroRpmBehavior ==
                         FanTargetZeroBehavior.StopFanWhileKeepingManualControl
                ? FontWeights.SemiBold
                : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var fans = new Grid();
        fans.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        fans.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fans.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        fans.Children.Add(FanCard(
            T("风扇 1", "Fan 1"),
            _fan1Minimum,
            _fan1Maximum,
            isDark));
        var fan2 = FanCard(
            T("风扇 2", "Fan 2"),
            _fan2Minimum,
            _fan2Maximum,
            isDark);
        Grid.SetColumn(fan2, 2);
        fans.Children.Add(fan2);
        root.Children.Add(fans);

        root.Children.Add(new Border
        {
            Background = Brush(isDark ? "#3A2028" : "#FFF0F1"),
            BorderBrush = Brush(isDark ? "#A94757" : "#E7A2AB"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock
            {
                Text = T(
                    "警告：修改风扇转速上下限可能导致散热不足、风扇异常或硬件损坏。由于调整此设置导致的任何硬件损坏，均与 ThinkBook Toolkit 及其开发者无关，开发者概不负责。",
                    "Warning: Changing fan RPM limits may cause insufficient cooling, fan malfunction, or hardware damage. ThinkBook Toolkit and its developers are not responsible for any hardware damage caused by changing this setting."),
                Foreground = Brush(isDark ? "#FF98A4" : "#B71C36"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        });
        root.Children.Add(_validation);

        var cancel = new Button
        {
            Content = T("取消", "Cancel"),
            MinWidth = 104,
            MinHeight = 38,
            IsCancel = true
        };
        var save = new Button
        {
            Content = T("保存", "Save"),
            MinWidth = 104,
            MinHeight = 38,
            IsDefault = true,
            Margin = new Thickness(10, 0, 0, 0)
        };
        save.Click += (_, _) => Save();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        Content = root;
    }

    public FanRpmLimits? Limits { get; private set; }

    private Border FanCard(
        string title,
        TextBox minimum,
        TextBox maximum,
        bool isDark)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        content.Children.Add(Field(T("下限（RPM）", "Minimum (RPM)"), minimum));
        content.Children.Add(Field(T("上限（RPM）", "Maximum (RPM)"), maximum));
        return new Border
        {
            Background = Brush(isDark ? "#172238" : "#FFFFFF"),
            BorderBrush = Brush(isDark ? "#31425F" : "#D6E0EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(15),
            Child = content
        };
    }

    private static Grid Field(string label, TextBox box)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    private static TextBox RpmBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 104,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private void Save()
    {
        if (!TryRead(_fan1Minimum, out var fan1Minimum) ||
            !TryRead(_fan1Maximum, out var fan1Maximum) ||
            !TryRead(_fan2Minimum, out var fan2Minimum) ||
            !TryRead(_fan2Maximum, out var fan2Maximum))
        {
            _validation.Text = T(
                "请输入 0–10000 之间、以 100 RPM 为步进的整数。",
                "Enter integers from 0 to 10000 in 100-RPM increments.");
            return;
        }
        if (fan1Minimum >= fan1Maximum || fan2Minimum >= fan2Maximum)
        {
            _validation.Text = T(
                "每个风扇的上限必须大于下限。",
                "Each fan's maximum must be greater than its minimum.");
            return;
        }

        Limits = new FanRpmLimits
        {
            Fan1MinimumRpm = fan1Minimum,
            Fan1MaximumRpm = fan1Maximum,
            Fan2MinimumRpm = fan2Minimum,
            Fan2MaximumRpm = fan2Maximum
        };
        DialogResult = true;
    }

    private static bool TryRead(TextBox box, out int value) =>
        int.TryParse(
            box.Text.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value) &&
        value is >= CurveProfileStore.AbsoluteMinimumFanRpm
            and <= CurveProfileStore.AbsoluteMaximumFanRpm &&
        value % 100 == 0;

    private string ZeroRpmDescription(FanBackendControlSemantics semantics)
    {
        return semantics.ZeroRpmBehavior ==
               FanTargetZeroBehavior.StopFanWhileKeepingManualControl
            ? T(
                "写入 0 会保持手动控制并关闭对应风扇，不会恢复自动；如需恢复，请切换到“固件自动”。",
                "Writing 0 keeps manual control active and stops that fan; it does not restore automatic control. Select Firmware automatic to restore it.")
            : T(
                "写入 0 会将对应风扇交还固件控制；切换到“固件自动”会恢复全部风扇的自动控制。",
                "Writing 0 returns that fan to firmware control. Select Firmware automatic to restore automatic control for all fans.");
    }

    private string T(string chinese, string english) =>
        _isChinese ? chinese : english;

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
