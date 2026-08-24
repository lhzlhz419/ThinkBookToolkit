using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal static class ApplicationDisclaimerPreference
{
    public const string ChineseConfirmation =
        "我了解风险，并自行承担全部后果，同时会在售后前卸载此软件。";

    public const string EnglishConfirmation =
        "I understand the risks, accept all consequences, and will uninstall this software before requesting after-sales service.";

    public static string CurrentVersion =>
        ApplicationUpdateService.CurrentVersionText;

    public static bool RequiresConfirmation(AppSettings settings) =>
        !string.Equals(
            settings.AcceptedDisclaimerVersion,
            CurrentVersion,
            StringComparison.OrdinalIgnoreCase);

    public static bool Accept(AppSettings settings, out string? error)
    {
        var previous = settings.AcceptedDisclaimerVersion;
        settings.AcceptedDisclaimerVersion = CurrentVersion;
        try
        {
            CurveProfileStore.SaveSettings(settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            settings.AcceptedDisclaimerVersion = previous;
            error = ex.Message;
            return false;
        }
    }
}

internal sealed class ApplicationDisclaimerWindow : Window
{
    private readonly bool _isDark;
    private readonly ComboBox _language = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _body = new();
    private readonly TextBlock _instruction = new();
    private readonly TextBox _confirmation = new();
    private readonly TextBlock _status = new();
    private readonly Button _continue = new();
    private readonly Button _exit = new();
    private ToolkitPalette _palette;
    private bool _isChinese;

    public ApplicationDisclaimerWindow(
        string language,
        bool isDark)
    {
        _isDark = isDark;
        _isChinese = language != "en-US";
        _palette = ToolkitPalette.For(isDark);
        Width = 780;
        Height = 650;
        MinWidth = 620;
        MinHeight = 520;
        MaxHeight = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, isDark);
        Content = BuildLayout();
        ApplyLanguage();
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

        _title.FontSize = 24;
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(_title);

        var content = new StackPanel();
        _body.FontSize = 15;
        _body.LineHeight = 25;
        _body.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(_body);
        _instruction.FontWeight = FontWeights.SemiBold;
        _instruction.TextWrapping = TextWrapping.Wrap;
        _instruction.Margin = new Thickness(0, 20, 0, 8);
        content.Children.Add(_instruction);
        _confirmation.MinHeight = 42;
        _confirmation.Padding = new Thickness(10, 7, 10, 7);
        _confirmation.AllowDrop = false;
        DataObject.AddPastingHandler(
            _confirmation,
            (_, args) => args.CancelCommand());
        _confirmation.TextChanged += (_, _) => SyncContinueState();
        content.Children.Add(_confirmation);
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(_status);
        var bodyHost = new Border
        {
            Background = Brush(_palette.Surface),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            }
        };
        Grid.SetRow(bodyHost, 1);
        root.Children.Add(bodyHost);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _language.Width = 130;
        _language.HorizontalAlignment = HorizontalAlignment.Left;
        _language.Items.Add(new ComboBoxItem { Content = "中文", Tag = true });
        _language.Items.Add(new ComboBoxItem { Content = "English", Tag = false });
        _language.SelectionChanged += (_, _) =>
        {
            if (_language.SelectedItem is ComboBoxItem { Tag: bool chinese })
            {
                _isChinese = chinese;
                ApplyLanguage();
            }
        };
        footer.Children.Add(_language);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _exit.MinWidth = 110;
        _exit.MinHeight = 40;
        _exit.Margin = new Thickness(0, 0, 10, 0);
        _exit.Click += (_, _) => DialogResult = false;
        _continue.MinWidth = 110;
        _continue.MinHeight = 40;
        _continue.IsDefault = true;
        _continue.Background = Brush(_palette.Accent);
        _continue.BorderBrush = Brush(_palette.Accent);
        _continue.Foreground = Brushes.White;
        _continue.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(_exit);
        buttons.Children.Add(_continue);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void ApplyLanguage()
    {
        Title = _isChinese ? "ThinkBook Toolkit 使用风险确认" :
            "ThinkBook Toolkit Risk Acknowledgement";
        _title.Text = Title;
        _body.Text = _isChinese
            ? "ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，不是联想官方项目，也未获得联想的认可、支持或赞助。\n\n" +
              "本软件会读取并写入硬件和固件设置。不正确或不兼容的设置可能影响散热、性能、稳定性、硬件寿命、保修状态或数据安全。使用者须自行判断并承担全部风险和后果。如果您不了解或不接受这些风险，请勿安装或使用本软件。\n\n" +
              "去售后前，请把toolkit软件卸载或者拔掉硬盘，避免不必要的麻烦。"
            : "ThinkBook Toolkit is an independently developed experimental project. It is not affiliated with Lenovo, is not an official Lenovo project, and is not endorsed, supported, or sponsored by Lenovo.\n\n" +
              "This software reads and writes hardware and firmware settings. Incorrect or incompatible settings may affect cooling, performance, stability, hardware lifespan, warranty coverage, or data safety. You must evaluate and accept all risks and consequences yourself. If you do not understand or accept these risks, do not install or use this software.\n\n" +
              "Before requesting after-sales service, uninstall ThinkBook Toolkit or remove the drive containing it to avoid unnecessary complications.";
        var required = RequiredConfirmation;
        _instruction.Text = _isChinese
            ? $"请完整手动输入以下文字后继续：\n{required}"
            : $"Type the following sentence in full to continue:\n{required}";
        _exit.Content = _isChinese ? "退出软件" : "Exit";
        _continue.Content = _isChinese ? "继续" : "Continue";
        _language.SelectedIndex = _isChinese ? 0 : 1;
        FontFamily = UiTypography.FontFamilyFor(_isChinese ? "zh-CN" : "en-US");
        SyncContinueState();
    }

    private string RequiredConfirmation => _isChinese
        ? ApplicationDisclaimerPreference.ChineseConfirmation
        : ApplicationDisclaimerPreference.EnglishConfirmation;

    private void SyncContinueState()
    {
        _continue.IsEnabled = string.Equals(
            _confirmation.Text,
            RequiredConfirmation,
            StringComparison.Ordinal);
        _status.Text = string.Empty;
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
