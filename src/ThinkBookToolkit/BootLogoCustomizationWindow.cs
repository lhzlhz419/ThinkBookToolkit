using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThinkBookToolkit;

internal sealed class BootLogoCustomizationWindow : Window
{
    private readonly Func<string, string> _t;
    private readonly bool _isDark;
    private readonly bool _embeddedMode;
    private readonly Grid _preview = new();
    private readonly TextBlock _resolution = new();
    private readonly TextBlock _formats = new();
    private readonly CheckBox _showLoading = new();
    private readonly Button _customize = new();
    private readonly Button _reset = new();
    private readonly Button _confirm = new();
    private readonly Button _close = new();
    private readonly FrameworkElement _busy;
    private BiosLogoState? _state;
    private string? _selectedPath;
    private byte[]? _selectedImage;
    private bool _resetPending;
    private bool _loading;

    public BootLogoCustomizationWindow(
        Window? owner,
        Func<string, string> translate,
        bool isDark,
        FontFamily fontFamily,
        double fontSize,
        bool embeddedMode = false)
    {
        _t = translate;
        _isDark = isDark;
        _embeddedMode = embeddedMode;
        if (!_embeddedMode && owner is not null)
            Owner = owner;
        Title = _t("BootLogoCustomizationTitle");
        Width = Math.Min(760, Math.Max(520, SystemParameters.WorkArea.Width - 32));
        Height = Math.Min(720, Math.Max(520, SystemParameters.WorkArea.Height - 32));
        MinWidth = Math.Min(680, Width);
        MinHeight = Math.Min(520, Height);
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = fontFamily;
        FontSize = fontSize;
        _busy = CreateSpinner(44, Brush("#8b95a5"));
        Content = BuildLayout();
        ApplyTheme();
        Loaded += async (_, _) => await LoadStateAsync();
    }

    private UIElement BuildLayout()
    {
        var title = new TextBlock
        {
            Text = _t("BootLogoCustomizationTitle"),
            FontSize = FontSize + 8,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 14)
        };

        _preview.Width = 640;
        _preview.Height = 400;
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.Background = Brushes.Black;
        _preview.ClipToBounds = true;

        _busy.Visibility = Visibility.Collapsed;
        _busy.HorizontalAlignment = HorizontalAlignment.Center;
        _busy.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(_busy);

        _resolution.FontWeight = FontWeights.SemiBold;
        _resolution.Margin = new Thickness(0, 16, 0, 8);
        _formats.FontWeight = FontWeights.SemiBold;
        _formats.Margin = new Thickness(0, 0, 0, 10);

        _showLoading.Content = _t("ShowWindowsLoadingIcon");
        _showLoading.Margin = new Thickness(0, 8, 0, 0);
        _showLoading.Checked += (_, _) => UpdateDirtyState();
        _showLoading.Unchecked += (_, _) => UpdateDirtyState();

        ConfigureButton(_customize, _t("Customize"));
        _customize.Click += (_, _) => SelectImage();
        ConfigureButton(_reset, _t("ResetToDefault"));
        _reset.Click += (_, _) => SelectDefault();
        ConfigureButton(_confirm, _t("Confirm"));
        _confirm.Click += async (_, _) => await ApplyAsync();
        _confirm.IsEnabled = false;
        _confirm.IsDefault = true;

        ConfigureButton(_close, _t("Close"));
        _close.MinWidth = 90;
        _close.Margin = new Thickness(8, 0, 0, 0);
        _close.IsCancel = true;
        _close.Click += (_, _) => Close();

        var customizeRow = new Grid { Margin = new Thickness(0, 10, 0, 16) };
        customizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_customize, 1);
        customizeRow.Children.Add(_customize);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _reset.HorizontalAlignment = HorizontalAlignment.Left;
        _reset.MinWidth = 152;
        footer.Children.Add(_reset);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (!_embeddedMode)
            right.Children.Add(_close);
        right.Children.Add(_confirm);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);

        var content = new Grid
        {
            Margin = _embeddedMode ? new Thickness(0) : new Thickness(24)
        };
        for (var index = 0; index < 8; index++)
        {
            content.RowDefinitions.Add(new RowDefinition
            {
                Height = index == 1
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto
            });
        }

        var previewHost = new Viewbox
        {
            Child = _preview,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 160
        };
        var previewDisclaimer = new TextBlock
        {
            Text = _t("BootLogoPreviewDisclaimer"),
            Opacity = 0.72,
            FontSize = Math.Max(11, FontSize - 1),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        if (!_embeddedMode)
        {
            Grid.SetRow(title, 0);
            content.Children.Add(title);
        }
        Grid.SetRow(previewHost, 1);
        content.Children.Add(previewHost);
        Grid.SetRow(previewDisclaimer, 2);
        content.Children.Add(previewDisclaimer);
        Grid.SetRow(_resolution, 3);
        content.Children.Add(_resolution);
        Grid.SetRow(_formats, 4);
        content.Children.Add(_formats);
        Grid.SetRow(_showLoading, 5);
        content.Children.Add(_showLoading);
        Grid.SetRow(customizeRow, 6);
        content.Children.Add(customizeRow);
        Grid.SetRow(footer, 7);
        content.Children.Add(footer);
        return content;
    }

    private async Task LoadStateAsync()
    {
        SetBusy(true);
        try
        {
            _state = await Task.Run(BiosAdvancedController.ReadLogoState);
            _showLoading.IsChecked = _state.ShowWindowsLoading;
            _resolution.Text = string.Format(
                _t("BootLogoResolutionFormat"),
                _state.Info.Width,
                _state.Info.Height);
            _formats.Text = string.Format(
                _t("BootLogoFormatsFormat"),
                string.Join(", ", BiosAdvancedController.GetSupportedLogoFormats(_state.Info)));
            ShowPreview(_state.CurrentImage);
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(_t("AdvancedToolkitFailedFormat"), ex.Message),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (!_embeddedMode)
                Close();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SelectImage()
    {
        if (_state is null) return;
        var dialog = new OpenFileDialog
        {
            Title = _t("BootLogoCustomizationTitle"),
            Filter = BiosAdvancedController.BuildLogoFilter(_state.Info),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            ShowPreview(bytes);
            _selectedPath = dialog.FileName;
            _selectedImage = bytes;
            _resetPending = false;
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectDefault()
    {
        if (_state is null) return;
        _selectedPath = null;
        _selectedImage = null;
        _resetPending = true;
        _showLoading.IsChecked = true;
        ShowPreview(null);
        UpdateDirtyState();
    }

    private async Task ApplyAsync()
    {
        if (_state is null || !_confirm.IsEnabled) return;
        var firstText = _resetPending
            ? _t("BootLogoResetConfirmFirst")
            : _t("BootLogoApplyConfirmFirst");
        if (!Confirm(firstText)) return;
        var secondText = _resetPending
            ? _t("BootLogoResetConfirmSecond")
            : _t("BootLogoApplyConfirmSecond");
        if (!Confirm(secondText)) return;

        SetBusy(true);
        try
        {
            var showLoading = _showLoading.IsChecked == true;
            await Task.Run(() =>
            {
                if (_resetPending)
                    BiosAdvancedController.ResetBootLogo();
                else if (!string.IsNullOrWhiteSpace(_selectedPath))
                    BiosAdvancedController.SetBootLogo(_selectedPath);
                BiosAdvancedController.SetWindowsLoading(showLoading);
            });
            MessageBox.Show(this, _t("BootLogoSuccess"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            if (_embeddedMode)
            {
                _selectedPath = null;
                _selectedImage = null;
                _resetPending = false;
                await LoadStateAsync();
            }
            else
            {
                DialogResult = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(_t("AdvancedToolkitFailedFormat"), ex.Message),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, Title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowPreview(byte[]? imageBytes)
    {
        _preview.Children.Clear();
        if (imageBytes is { Length: > 0 })
        {
            using var stream = new MemoryStream(imageBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _preview.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(28)
            });
        }
        else
        {
            var lenovo = new Border
            {
                Width = 230,
                Height = 86,
                Background = Brush("#e2231a"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Lenovo",
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 49,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _preview.Children.Add(lenovo);
        }

        if (_showLoading.IsChecked == true)
        {
            // Match Vantage's MUI CircularProgress overlay exactly: size=32,
            // left=46%, bottom=10%, margin-bottom=-1rem (10 px in Vantage).
            var bootSpinner = CreateSpinner(32, Brush(_isDark ? "#18181b" : "#ffffff"));
            bootSpinner.HorizontalAlignment = HorizontalAlignment.Left;
            bootSpinner.VerticalAlignment = VerticalAlignment.Bottom;
            var previewWidth = _preview.ActualWidth > 0 ? _preview.ActualWidth : 704;
            var bottom = Math.Max(0, _preview.Height * 0.10 - 10);
            bootSpinner.Margin = new Thickness(previewWidth * 0.46, 0, 0, bottom);
            _preview.Children.Add(bootSpinner);
        }
        _preview.Children.Add(_busy);
    }

    private void UpdateDirtyState()
    {
        if (_state is null) return;
        ShowPreview(_resetPending ? null : _selectedImage ?? _state.CurrentImage);
        _confirm.IsEnabled = !_loading &&
            (_resetPending || _selectedPath is not null ||
             _showLoading.IsChecked != _state.ShowWindowsLoading);
        _reset.IsEnabled = !_loading && (_state.Info.Enabled || _state.CurrentImage is not null || !_state.ShowWindowsLoading);
    }

    private void SetBusy(bool busy)
    {
        _loading = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _customize.IsEnabled = !busy;
        _showLoading.IsEnabled = !busy;
        _confirm.IsEnabled = !busy && _confirm.IsEnabled;
        _reset.IsEnabled = !busy && _reset.IsEnabled;
        _close.IsEnabled = !busy;
        if (!busy) UpdateDirtyState();
    }

    private void ConfigureButton(Button button, string text)
    {
        button.Content = text;
        button.MinWidth = 112;
        button.Height = 40;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(18, 4, 18, 4);
    }

    private void ApplyTheme()
    {
        var palette = ToolkitPalette.For(_isDark);
        Background = Brush(palette.Canvas);
        Foreground = Brush(palette.Text);
        _showLoading.Foreground = Foreground;
        if (Content is DependencyObject content)
            ModernTheme.ApplyEmbeddedWorkspace(content, _isDark);
        _confirm.Background = Brush(palette.Accent);
        _confirm.BorderBrush = Brush(palette.Accent);
        _confirm.Foreground = Brushes.White;
    }

    private static FrameworkElement CreateSpinner(double size, Brush color)
        => new MuiCircularProgress(size, color);

    /// <summary>
    /// Pixel-equivalent WPF rendering of the MUI CircularProgress used by
    /// Vantage's LogoDiyDialog. MUI uses a 44-unit SVG, thickness 5.5,
    /// a 1.4-second root rotation, and a 1.4-second ease-in-out dash animation.
    /// </summary>
    private sealed class MuiCircularProgress : FrameworkElement
    {
        private const double SvgSize = 44;
        private const double SvgThickness = 5.5;
        private const double SvgRadius = (SvgSize - SvgThickness) / 2;
        private const double Circumference = 2 * Math.PI * SvgRadius;
        private readonly Stopwatch _clock = new();
        private readonly Pen _pen;

        public MuiCircularProgress(double size, Brush color)
        {
            Width = size;
            Height = size;
            _pen = new Pen(color, SvgThickness * size / SvgSize)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            _pen.Freeze();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
        }

        private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            var seconds = _clock.Elapsed.TotalSeconds;
            var rootRotation = seconds % 1.4 / 1.4 * 360.0;
            var dashProgress = seconds % 1.4 / 1.4;
            double dashLength;
            double dashOffset;
            if (dashProgress <= 0.5)
            {
                var eased = EaseInOut(dashProgress * 2);
                dashLength = Lerp(1, 100, eased);
                dashOffset = Lerp(0, -15, eased);
            }
            else
            {
                var eased = EaseInOut((dashProgress - 0.5) * 2);
                dashLength = Lerp(100, 1, eased);
                dashOffset = Lerp(-15, -126, eased);
            }

            var sweep = Math.Clamp(dashLength / Circumference * 360, 0.1, 359.9);
            var start = rootRotation - dashOffset / Circumference * 360;
            var radius = Math.Min(ActualWidth, ActualHeight) / 2 - _pen.Thickness / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var startPoint = PointOnCircle(center, radius, start);
            var endPoint = PointOnCircle(center, radius, start + sweep);

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(startPoint, false, false);
                context.ArcTo(
                    endPoint,
                    new Size(radius, radius),
                    0,
                    sweep > 180,
                    SweepDirection.Clockwise,
                    true,
                    false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, _pen, geometry);
        }

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(
                center.X + Math.Cos(radians) * radius,
                center.Y + Math.Sin(radians) * radius);
        }

        private static double Lerp(double from, double to, double amount) =>
            from + (to - from) * amount;

        // CSS ease-in-out = cubic-bezier(.42, 0, .58, 1).
        private static double EaseInOut(double progress)
        {
            var parameter = progress;
            for (var index = 0; index < 6; index++)
            {
                var x = Cubic(parameter, 0.42, 0.58) - progress;
                var derivative = CubicDerivative(parameter, 0.42, 0.58);
                if (Math.Abs(derivative) < 1e-7) break;
                parameter = Math.Clamp(parameter - x / derivative, 0, 1);
            }
            return Cubic(parameter, 0, 1);
        }

        private static double Cubic(double value, double first, double second)
        {
            var inverse = 1 - value;
            return 3 * inverse * inverse * value * first +
                   3 * inverse * value * value * second + value * value * value;
        }

        private static double CubicDerivative(double value, double first, double second)
        {
            var inverse = 1 - value;
            return 3 * inverse * inverse * first +
                   6 * inverse * value * (second - first) +
                   3 * value * value * (1 - second);
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
