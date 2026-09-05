using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ThinkBookToolkit;

internal sealed class BackgroundImageSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly AnimatedBackgroundImage _preview = new();
    private readonly TextBox _path = new();
    private readonly Slider _scale = new()
    {
        Minimum = 10,
        Maximum = 500,
        TickFrequency = 10,
        IsSnapToTickEnabled = false
    };
    private readonly Slider _opacity = new()
    {
        Minimum = 0,
        Maximum = 100,
        TickFrequency = 5,
        IsSnapToTickEnabled = false
    };
    private readonly Slider _gifSpeed = new()
    {
        Minimum = 10,
        Maximum = 500,
        TickFrequency = 10,
        IsSnapToTickEnabled = false
    };
    private readonly Slider _blur = new()
    {
        Minimum = 0,
        Maximum = 40,
        TickFrequency = 1,
        IsSnapToTickEnabled = false
    };
    private readonly TextBox _scaleValue = NumericBox();
    private readonly TextBox _opacityValue = NumericBox();
    private readonly TextBox _gifSpeedValue = NumericBox();
    private readonly TextBox _blurValue = NumericBox();
    private readonly ComboBox _sizeMode = new()
    {
        MinWidth = 190,
        MinHeight = 36
    };
    private readonly CheckBox _inverted = new();
    private readonly CheckBox _baseColorEnabled = new();
    private readonly TextBox _baseColorValue = new()
    {
        Width = 110,
        MinHeight = 34,
        MaxLength = 6,
        CharacterCasing = CharacterCasing.Upper,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(8, 2, 8, 2)
    };
    private Button _baseColorPicker = null!;
    private readonly TextBlock _status = new();
    private FrameworkElement? _gifSpeedRow;
    private FrameworkElement? _scaleRow;
    private readonly Border _previewDimOverlay = new()
    {
        IsHitTestVisible = false
    };
    private readonly Border _previewBaseLayer = new()
    {
        IsHitTestVisible = false
    };
    private bool _previewValid = true;
    private bool _syncingValues;

    public BackgroundImageSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        Title = runtime.L("背景图像", "Background image");
        Width = 820;
        Height = 820;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Content = Build();
        _preview.PlaybackFailed += (_, error) =>
        {
            _previewValid = false;
            _status.Text = _runtime.L(
                "无法播放背景视频：",
                "The background video could not be played: ") + error;
        };
        Loaded += (_, _) =>
            ModernTheme.RefreshWindow(this, runtime.IsDark);
        Closed += (_, _) =>
        {
            var shouldCleanup = DialogResult != true &&
                                (_preview.SupportsPlaybackSpeed ||
                                 _preview.BlurRadius > 0);
            _preview.Dispose();
            if (shouldCleanup)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => MediaMemoryCleanup.CollectAndTrim(
                        "background settings preview closed")));
            }
        };
        LoadSettings();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L("设置背景图像", "Set background image"),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "支持 BMP、JPG、JPEG、PNG、GIF 和常见视频格式；动画和视频可调整播放速度。",
                "Supports BMP, JPG, JPEG, PNG, GIF, and common video formats. Animation and video playback speed can be adjusted."),
            Foreground = Brush(_palette.Muted),
            Margin = new Thickness(0, 4, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(header);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var previewHost = new Grid
        {
            ClipToBounds = true,
            Background = Brush(_palette.SurfaceRaised)
        };
        previewHost.SizeChanged += (_, _) =>
            _preview.SetViewport(previewHost.RenderSize);
        previewHost.Children.Add(_previewBaseLayer);
        previewHost.Children.Add(_preview);
        _previewDimOverlay.Background = Brush(_palette.Canvas);
        previewHost.Children.Add(_previewDimOverlay);
        content.Children.Add(new Border
        {
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Child = previewHost
        });

        var controls = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var fileRow = new Grid();
        fileRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path.IsReadOnly = true;
        _path.MinHeight = 38;
        _path.VerticalContentAlignment = VerticalAlignment.Center;
        _path.Margin = new Thickness(0, 0, 8, 0);
        fileRow.Children.Add(_path);
        var browse = Button(_runtime.L("选择图像", "Choose image"));
        browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(browse);
        var clear = Button(_runtime.L("清除", "Clear"));
        clear.Margin = new Thickness(8, 0, 0, 0);
        clear.Click += (_, _) => SetPreviewPath(string.Empty);
        Grid.SetColumn(clear, 2);
        fileRow.Children.Add(clear);
        controls.Children.Add(fileRow);
        AddSizeModeChoices();
        controls.Children.Add(ChoiceRow(
            _runtime.L("大小", "Size"),
            _sizeMode));
        _scaleRow = SliderRow(
            _runtime.L("放大率", "Scale"),
            _scale,
            _scaleValue);
        controls.Children.Add(_scaleRow);
        controls.Children.Add(SliderRow(
            _runtime.L("透明度", "Transparency"),
            _opacity,
            _opacityValue));
        controls.Children.Add(SliderRow(
            _runtime.L("模糊", "Blur"),
            _blur,
            _blurValue));
        _gifSpeedRow = SliderRow(
            _runtime.L("动画/视频播放速度（%）", "Animation/video speed (%)"),
            _gifSpeed,
            _gifSpeedValue);
        controls.Children.Add(_gifSpeedRow);
        var invertRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        invertRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(170)
        });
        invertRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var invertControl = new Grid();
        invertControl.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        invertControl.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        invertControl.Children.Add(new TextBlock
        {
            Text = _runtime.L("反色", "Invert colors"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _inverted.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_inverted, 1);
        invertControl.Children.Add(_inverted);
        invertRow.Children.Add(invertControl);

        var baseColor = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        baseColor.Children.Add(new TextBlock
        {
            Text = _runtime.L("基础背景颜色", "Base background color"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 8, 0)
        });
        _baseColorEnabled.VerticalAlignment = VerticalAlignment.Center;
        _baseColorEnabled.Margin = new Thickness(0, 0, 8, 0);
        baseColor.Children.Add(_baseColorEnabled);
        _baseColorPicker = Button(_runtime.L("选择颜色", "Choose color"));
        _baseColorPicker.MinWidth = 90;
        _baseColorPicker.Margin = new Thickness(0, 0, 8, 0);
        baseColor.Children.Add(_baseColorPicker);
        var colorInput = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        colorInput.Children.Add(new TextBlock
        {
            Text = "0x",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(_palette.Muted)
        });
        colorInput.Children.Add(_baseColorValue);
        baseColor.Children.Add(colorInput);
        Grid.SetColumn(baseColor, 1);
        invertRow.Children.Add(baseColor);
        controls.Children.Add(invertRow);
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        controls.Children.Add(_status);
        Grid.SetRow(controls, 1);
        content.Children.Add(controls);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = Button(_runtime.L("取消", "Cancel"));
        cancel.Click += (_, _) => Close();
        var apply = Button(_runtime.L("应用", "Apply"), primary: true);
        apply.Margin = new Thickness(8, 0, 0, 0);
        apply.Click += (_, _) => Apply();
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        _scale.ValueChanged += (_, _) => UpdatePreviewValues();
        _opacity.ValueChanged += (_, _) => UpdatePreviewValues();
        _gifSpeed.ValueChanged += (_, _) => UpdatePreviewValues();
        _blur.ValueChanged += (_, _) => UpdatePreviewValues();
        _sizeMode.SelectionChanged += (_, _) =>
        {
            UpdateSizeModeControls();
            UpdatePreviewValues();
        };
        _inverted.Click += (_, _) => UpdatePreviewValues();
        _baseColorEnabled.Click += (_, _) =>
        {
            UpdateBaseColorControls();
            UpdatePreviewValues();
        };
        _baseColorPicker.Click += (_, _) => PickBaseColor();
        _baseColorValue.PreviewTextInput += (_, args) =>
            args.Handled = args.Text.Any(character => !Uri.IsHexDigit(character));
        _baseColorValue.TextChanged += (_, _) =>
        {
            if (!_syncingValues)
            {
                UpdateBaseColorControls();
                UpdatePreviewValues();
            }
        };
        WireNumericBox(_scaleValue, _scale, 10, 500);
        WireNumericBox(_opacityValue, _opacity, 0, 100);
        WireNumericBox(_gifSpeedValue, _gifSpeed, 10, 500);
        WireNumericBox(_blurValue, _blur, 0, 40);
        return root;
    }

    private FrameworkElement SliderRow(
        string title,
        Slider slider,
        TextBox value)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center
        });
        slider.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        value.TextAlignment = TextAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private FrameworkElement ChoiceRow(string title, ComboBox choice)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(165)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center
        });
        choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(choice, 1);
        row.Children.Add(choice);
        return row;
    }

    private void AddSizeModeChoices()
    {
        AddSizeModeChoice(
            _runtime.L("固定大小", "Fixed size"),
            BackgroundImageSizeMode.Fixed);
        AddSizeModeChoice(
            _runtime.L("长度匹配", "Match length"),
            BackgroundImageSizeMode.MatchLength);
        AddSizeModeChoice(
            _runtime.L("宽度匹配", "Match width"),
            BackgroundImageSizeMode.MatchWidth);
        AddSizeModeChoice(
            _runtime.L("强制拉伸", "Force stretch"),
            BackgroundImageSizeMode.Stretch);
    }

    private void AddSizeModeChoice(
        string text,
        BackgroundImageSizeMode mode) =>
        _sizeMode.Items.Add(new ComboBoxItem
        {
            Content = text,
            Tag = mode
        });

    private void LoadSettings()
    {
        _scale.Value = Math.Clamp(
            _runtime.Settings.BackgroundImageScalePercent,
            10,
            500);
        _opacity.Value = Math.Clamp(
            _runtime.Settings.BackgroundImageOpacityPercent,
            0,
            100);
        _blur.Value = Math.Clamp(
            _runtime.Settings.BackgroundImageBlurRadius,
            0,
            40);
        _gifSpeed.Value = Math.Clamp(
            _runtime.Settings.BackgroundMediaSpeedPercent,
            10,
            500);
        _inverted.IsChecked = _runtime.Settings.BackgroundImageInverted;
        _baseColorEnabled.IsChecked =
            _runtime.Settings.BackgroundBaseColorEnabled;
        _baseColorValue.Text = CurveProfileStore.NormalizeBackgroundColor(
            _runtime.Settings.BackgroundBaseColor);
        SelectSizeMode(_runtime.Settings.BackgroundImageSizeMode);
        SetPreviewPath(_runtime.Settings.BackgroundImagePath);
        UpdateSizeModeControls();
        UpdateBaseColorControls();
        UpdatePreviewValues();
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = _runtime.L("选择背景图像", "Choose a background image"),
            Filter = _runtime.L(
                "背景媒体|*.bmp;*.dib;*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.gif;*.avi;*.m4v;*.mov;*.mp4;*.mpeg;*.mpg;*.wmv|所有文件|*.*",
                "Background media|*.bmp;*.dib;*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.gif;*.avi;*.m4v;*.mov;*.mp4;*.mpeg;*.mpg;*.wmv|All files|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            SetPreviewPath(dialog.FileName);
    }

    private void SetPreviewPath(string path)
    {
        _status.Text = string.Empty;
        _previewValid = true;
        _path.Text = path;
        try
        {
            // Avoid opening a second Media Foundation decoder while the main
            // window is already playing the selected video. Size/speed options
            // remain editable and the applied background provides the live view.
            _preview.LoadFile(path, enableAnimatedPlayback: false);
            UpdateMediaControls();
            UpdatePreviewValues();
        }
        catch (Exception ex)
        {
            _previewValid = false;
            _preview.Clear();
            UpdateMediaControls();
            _status.Text = _runtime.L(
                "无法加载图像：",
                "The image could not be loaded: ") + ex.Message;
        }
    }

    private void UpdatePreviewValues()
    {
        if (!_syncingValues)
        {
            _syncingValues = true;
            _scaleValue.Text = $"{_scale.Value:0}";
            _opacityValue.Text = $"{_opacity.Value:0}";
            _gifSpeedValue.Text = $"{_gifSpeed.Value:0}";
            _blurValue.Text = $"{_blur.Value:0}";
            _syncingValues = false;
        }
        _preview.SetScale(_scale.Value);
        _preview.SetSizeMode(SelectedSizeMode());
        _preview.Opacity = 1;
        _preview.SetPlaybackSpeedPercent(
            (int)Math.Round(_gifSpeed.Value));
        _preview.SetInverted(_inverted.IsChecked == true);
        var blur = Math.Clamp((int)Math.Round(_blur.Value), 0, 40);
        _preview.SetBlurRadius(blur);
        if (TryReadBaseColor(out var color))
            _previewBaseLayer.Background = Brush("#" + color);
        _previewBaseLayer.Opacity =
            _baseColorEnabled.IsChecked == true ? 1 : 0;
        _previewDimOverlay.Opacity =
            string.IsNullOrWhiteSpace(_path.Text) &&
            _baseColorEnabled.IsChecked != true
            ? 0
            : _opacity.Value / 100d;
    }

    private void Apply()
    {
        if (!_previewValid)
            return;
        if (!CommitNumericValue(_scaleValue, _scale, 10, 500) ||
            !CommitNumericValue(_opacityValue, _opacity, 0, 100) ||
            !CommitNumericValue(_gifSpeedValue, _gifSpeed, 10, 500) ||
            !CommitNumericValue(_blurValue, _blur, 0, 40))
        {
            return;
        }
        if (!TryReadBaseColor(out var baseColor))
        {
            _status.Text = _runtime.L(
                "基础背景颜色必须是六位十六进制颜色。",
                "The base background color must be a six-digit hexadecimal color.");
            return;
        }
        if (!_runtime.TrySetBackgroundImage(
                _path.Text,
                _scale.Value,
                _opacity.Value,
                (int)Math.Round(_blur.Value),
                SelectedSizeMode(),
                _inverted.IsChecked == true,
                _baseColorEnabled.IsChecked == true,
                baseColor,
                (int)Math.Round(_gifSpeed.Value),
                out var error))
        {
            _status.Text = error ?? _runtime.L(
                "背景图像设置保存失败。",
                "The background image settings could not be saved.");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void UpdateMediaControls()
    {
        var enabled = _preview.SupportsPlaybackSpeed;
        _gifSpeed.IsEnabled = enabled;
        _gifSpeedValue.IsEnabled = enabled;
        if (_gifSpeedRow is not null)
            _gifSpeedRow.Opacity = enabled ? 1 : .5;
        _inverted.IsEnabled = !_preview.IsVideo;
        _inverted.Opacity = _preview.IsVideo ? .5 : 1;
    }

    private BackgroundImageSizeMode SelectedSizeMode() =>
        _sizeMode.SelectedItem is ComboBoxItem
        {
            Tag: BackgroundImageSizeMode mode
        }
            ? mode
            : BackgroundImageSizeMode.Fixed;

    private void SelectSizeMode(BackgroundImageSizeMode mode)
    {
        _sizeMode.SelectedItem = _sizeMode.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                item.Tag is BackgroundImageSizeMode candidate &&
                candidate == mode) ?? _sizeMode.Items[0];
    }

    private void UpdateSizeModeControls()
    {
        var fixedSize = SelectedSizeMode() == BackgroundImageSizeMode.Fixed;
        _scale.IsEnabled = fixedSize;
        _scaleValue.IsEnabled = fixedSize;
        if (_scaleRow is not null)
            _scaleRow.Opacity = fixedSize ? 1 : .5;
    }

    private void UpdateBaseColorControls()
    {
        var enabled = _baseColorEnabled.IsChecked == true;
        _baseColorPicker.IsEnabled = enabled;
        _baseColorValue.IsEnabled = enabled;
        if (TryReadBaseColor(out var color))
        {
            var brush = Brush("#" + color);
            _baseColorPicker.Background = brush;
            var parsed = Convert.ToInt32(color, 16);
            var red = (parsed >> 16) & 0xFF;
            var green = (parsed >> 8) & 0xFF;
            var blue = parsed & 0xFF;
            var luminance = red * .299 + green * .587 + blue * .114;
            _baseColorPicker.Foreground = luminance >= 150
                ? Brushes.Black
                : Brushes.White;
        }
        _baseColorPicker.Opacity = enabled ? 1 : .5;
        _baseColorValue.Opacity = enabled ? 1 : .5;
    }

    private void PickBaseColor()
    {
        var color = CurveProfileStore.NormalizeBackgroundColor(
            _baseColorValue.Text);
        var parsed = Convert.ToInt32(color, 16);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = Drawing.Color.FromArgb(
                (parsed >> 16) & 0xFF,
                (parsed >> 8) & 0xFF,
                parsed & 0xFF)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        _baseColorValue.Text =
            $"{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateBaseColorControls();
        UpdatePreviewValues();
    }

    private bool TryReadBaseColor(out string color)
    {
        color = _baseColorValue.Text.Trim().ToUpperInvariant();
        return color.Length == 6 && color.All(Uri.IsHexDigit);
    }

    private void WireNumericBox(
        TextBox box,
        Slider slider,
        double minimum,
        double maximum)
    {
        box.LostKeyboardFocus += (_, _) =>
            CommitNumericValue(box, slider, minimum, maximum);
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter)
                return;
            args.Handled = true;
            CommitNumericValue(box, slider, minimum, maximum);
            Keyboard.ClearFocus();
        };
    }

    private bool CommitNumericValue(
        TextBox box,
        Slider slider,
        double minimum,
        double maximum)
    {
        if (_syncingValues || !box.IsEnabled)
            return true;
        if ((!double.TryParse(
                 box.Text,
                 NumberStyles.Float,
                 CultureInfo.CurrentCulture,
                 out var value) &&
             !double.TryParse(
                 box.Text,
                 NumberStyles.Float,
                 CultureInfo.InvariantCulture,
                 out value)) ||
            value < minimum ||
            value > maximum)
        {
            _status.Text = _runtime.L(
                $"请输入 {minimum:0} 到 {maximum:0} 之间的数值。",
                $"Enter a value between {minimum:0} and {maximum:0}.");
            UpdatePreviewValues();
            return false;
        }
        _status.Text = string.Empty;
        slider.Value = value;
        UpdatePreviewValues();
        return true;
    }

    private static TextBox NumericBox() => new()
    {
        Width = 78,
        MinHeight = 36,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 110,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Foreground = Brush(primary ? "#FFFFFF" : _palette.Text),
        Background = Brush(primary ? _palette.Accent : _palette.SurfaceRaised),
        BorderBrush = Brush(primary ? _palette.Accent : _palette.Border),
        BorderThickness = new Thickness(1),
        Template = ModernTheme.RoundedButtonTemplate(11)
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
