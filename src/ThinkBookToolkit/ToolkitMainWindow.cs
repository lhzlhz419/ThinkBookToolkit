using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class ToolkitMainWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly bool _ownsRuntime;
    private readonly bool _enableHardwareDetection;
    private readonly bool _startToTrayRequested;
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Dictionary<string, Border> _indicators = [];
    private readonly Dictionary<string, ToolkitPageBase> _pages = [];
    private readonly List<TextBlock> _navigationLabels = [];
    private TextBlock _pageTitle = new();
    private TextBlock _pageSubtitle = new();
    private TextBlock _pageGlyph = new();
    private TextBlock _toastText = new();
    private Border _toast = new();
    private readonly DispatcherTimer _toastTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };
    private ContentControl _pageHost = new();
    private ScrollViewer _mainScroll = new();
    private ColumnDefinition? _navigationColumn;
    private Border? _navigationSurface;
    private StackPanel? _brandText;
    private Grid? _mainArea;
    private string _selectedPage = "overview";
    private bool _sidebarCollapsed;
    private bool _initialized;
    private bool _preparingExit;
    private bool _forceClose;
    private bool _disposed;

    public ToolkitMainWindow(bool enableHardwareDetection = true)
        : this(
            new ToolkitRuntimeService(enableHardwareDetection
                ? CurveProfileStore.LoadSettings()
                : new AppSettings()),
            enableHardwareDetection,
            startToTrayRequested: false,
            ownsRuntime: true)
    {
    }

    public ToolkitMainWindow(
        ToolkitRuntimeService runtime,
        bool enableHardwareDetection = true,
        bool startToTrayRequested = false)
        : this(runtime, enableHardwareDetection, startToTrayRequested, ownsRuntime: false)
    {
    }

    private ToolkitMainWindow(
        ToolkitRuntimeService runtime,
        bool enableHardwareDetection,
        bool startToTrayRequested,
        bool ownsRuntime)
    {
        _runtime = runtime;
        _ownsRuntime = ownsRuntime;
        _enableHardwareDetection = enableHardwareDetection;
        _startToTrayRequested = startToTrayRequested;
        Title = "ThinkBook Toolkit";
        Width = 1380;
        Height = 880;
        MinWidth = 780;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ApplyAppearance();
        Content = BuildLayout();

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Visibility = Visibility.Collapsed;
        };

        _runtime.SnapshotChanged += OnSnapshotChanged;
        _runtime.AvailabilityChanged += OnAvailabilityChanged;
        _runtime.AppearanceChanged += OnAppearanceChanged;
        _runtime.OverviewLayoutChanged += OnOverviewLayoutChanged;
        _runtime.StatusChanged += OnStatusChanged;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal ToolkitPageBase? CurrentPage => _pageHost.Content as ToolkitPageBase;
    internal ScrollViewer MainScrollViewer => _mainScroll;
    internal bool SidebarCollapsed => _sidebarCollapsed;
    internal string SelectedPage => _selectedPage;
    internal bool ToastVisibleForTesting => _toast.Visibility == Visibility.Visible;
    internal string ToastTextForTesting => _toastText.Text;

    internal void NavigateForTesting(string page) => NavigateTo(page);
    internal void UpdateResponsiveForTesting(double width) =>
        UpdateResponsiveLayout(width);

    private ToolkitPalette Palette => ToolkitPalette.For(_runtime.IsDark);
    private string L(string chinese, string english) => _runtime.L(chinese, english);

    private void ApplyAppearance()
    {
        ModernTheme.Apply(Application.Current, _runtime.IsDark);
        FontFamily = UiTypography.FontFamilyFor(_runtime.Settings.Language);
        FontSize = 14;
        Background = Brush(Palette.Canvas);
        Foreground = Brush(Palette.Text);
    }

    private UIElement BuildLayout()
    {
        _navigation.Clear();
        _indicators.Clear();
        _navigationLabels.Clear();

        var root = new Grid { Background = CanvasBrush() };
        _navigationColumn = new ColumnDefinition { Width = new GridLength(238) };
        root.ColumnDefinitions.Add(_navigationColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.SizeChanged += (_, _) => UpdateResponsiveLayout(root.ActualWidth);

        _navigationSurface = BuildNavigation();
        Grid.SetColumn(_navigationSurface, 0);
        root.Children.Add(_navigationSurface);

        _mainArea = new Grid { Margin = new Thickness(6, 18, 22, 16) };
        _mainArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mainArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _mainArea.Children.Add(BuildHeader());

        _pageHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _mainScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0),
            Content = _pageHost
        };
        Grid.SetRow(_mainScroll, 1);
        _mainArea.Children.Add(_mainScroll);

        Grid.SetColumn(_mainArea, 1);
        root.Children.Add(_mainArea);
        var toast = BuildToast();
        Grid.SetColumn(toast, 1);
        Panel.SetZIndex(toast, 20);
        root.Children.Add(toast);
        RenderPage();
        return root;
    }

    private Border BuildNavigation()
    {
        var panel = new StackPanel();
        var brand = new Grid { Margin = new Thickness(5, 4, 5, 22) };
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brand.Children.Add(new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(13),
            Background = Brush("#161A20"),
            ClipToBounds = true,
            Child = new Image
            {
                Source = LoadBrandIcon(),
                Width = 42,
                Height = 42,
                Stretch = Stretch.UniformToFill
            }
        });
        _brandText = new StackPanel
        {
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _brandText.Children.Add(new TextBlock
        {
            Text = "ThinkBook Toolkit",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text)
        });
        _brandText.Children.Add(new TextBlock
        {
            Text = L("设备控制中心", "Device control center"),
            FontSize = 12,
            Foreground = Brush(Palette.Muted),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(_brandText, 1);
        brand.Children.Add(_brandText);
        panel.Children.Add(brand);

        AddNavigation(panel, "overview", "\uE80F", L("概览", "Overview"));
        AddNavigationIf(panel, "performance", "\uE945", L("性能", "Performance"));
        AddNavigationIf(panel, "cooling", "\uE9D9", L("散热", "Cooling"));
        AddNavigationIf(panel, "battery", "\uE850", L("电池与电源", "Battery and power"));
        AddNavigationIf(panel, "display", "\uE7F4", L("显示", "Display"));
        AddNavigationIf(panel, "sound", "\uE767", L("声音", "Sound"));
        AddNavigationIf(panel, "input", "\uE765", L("输入设备", "Input devices"));
        AddNavigationIf(panel, "device", "\uE772", L("设备信息", "Device information"));
        AddNavigationIf(panel, "advanced", "\uE90F", L("高级工具", "Advanced tools"));
        AddNavigation(panel, "settings", "\uE713", L("设置", "Settings"));
        UpdateNavigationSelection();

        return new Border
        {
            Background = Brush(Palette.Sidebar),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(21),
            Padding = new Thickness(9, 13, 9, 11),
            Margin = new Thickness(14, 17, 12, 17),
            Effect = new DropShadowEffect
            {
                BlurRadius = 23,
                ShadowDepth = 5,
                Opacity = _runtime.IsDark ? .2 : .08,
                Color = Colors.Black
            },
            Child = panel
        };
    }

    private void AddNavigationIf(Panel panel, string id, string glyph, string text)
    {
        if (_runtime.Report is not null && PageAvailable(id))
            AddNavigation(panel, id, glyph, text);
    }

    private void AddNavigation(Panel panel, string id, string glyph, string text)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var indicator = new Border
        {
            Width = 3,
            Height = 22,
            CornerRadius = new CornerRadius(2),
            Background = Brush(Palette.Accent),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        content.Children.Add(indicator);
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        content.Children.Add(icon);
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _navigationLabels.Add(label);
        Grid.SetColumn(label, 2);
        content.Children.Add(label);
        var button = new Button
        {
            Content = content,
            Tag = id,
            ToolTip = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 48,
            Padding = new Thickness(8, 10, 10, 10),
            Margin = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brush(Palette.Muted),
            Template = ModernTheme.RoundedButtonTemplate(12)
        };
        button.Click += (_, _) => NavigateTo(id);
        _navigation[id] = button;
        _indicators[id] = indicator;
        panel.Children.Add(button);
    }

    private UIElement BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(2, 1, 3, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pageGlyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 21,
            Foreground = Brush(Palette.Accent),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(15),
            Background = Brush(Palette.AccentSoft),
            Child = _pageGlyph
        });
        _pageGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        var titles = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _pageTitle = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text)
        };
        _pageSubtitle = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(Palette.Muted),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        titles.Children.Add(_pageTitle);
        titles.Children.Add(_pageSubtitle);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        return header;
    }

    private Border BuildToast()
    {
        _toastText = new TextBlock
        {
            Foreground = Brush(Palette.Text),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 440
        };
        _toast = new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = _runtime.IsDark ? .28 : .14,
                Color = Colors.Black
            },
            Child = _toastText
        };
        return _toast;
    }

    private void ShowToast(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        _toastTimer.Stop();
        _toastText.Text = message;
        _toast.BorderBrush = Brush(isError ? Palette.Danger : Palette.Border);
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Start();
    }

    private void NavigateTo(string page)
    {
        if (!_navigation.ContainsKey(page) && page is not "overview" and not "settings")
            return;
        if (_selectedPage == page && _pageHost.Content is not null)
            return;
        _selectedPage = page;
        UpdateNavigationSelection();
        RenderPage();
    }

    private void RenderPage()
    {
        var descriptor = PageDescriptor(_selectedPage);
        _pageTitle.Text = descriptor.Title;
        _pageSubtitle.Text = descriptor.Subtitle;
        _pageGlyph.Text = descriptor.Glyph;
        if (_runtime.Report is null &&
            _enableHardwareDetection &&
            _selectedPage is not "overview" and not "settings")
        {
            _pageHost.Content = LoadingPage();
            return;
        }
        if (!_pages.TryGetValue(_selectedPage, out var page))
        {
            page = CreatePage(_selectedPage);
            _pages[_selectedPage] = page;
        }
        _pageHost.Content = page;
        _mainScroll.ScrollToTop();
    }

    private ToolkitPageBase CreatePage(string page) => page switch
    {
        "performance" => new ToolkitPerformancePage(_runtime),
        "cooling" => new ToolkitPerformancePage(_runtime, coolingOnly: true),
        "battery" => new ToolkitBatteryPage(_runtime),
        "display" => new ToolkitDisplayPage(_runtime),
        "sound" => new ToolkitSoundPage(_runtime),
        "input" => new ToolkitInputPage(_runtime),
        "device" => new ToolkitDevicePage(_runtime),
        "advanced" => new ToolkitAdvancedPage(_runtime),
        "settings" => new ToolkitSettingsPage(_runtime),
        _ => new ToolkitOverviewPage(_runtime)
    };

    private ToolkitPageBase LoadingPage() => new LoadingToolkitPage(_runtime);

    private (string Title, string Subtitle, string Glyph) PageDescriptor(string page) => page switch
    {
        "performance" => (L("性能", "Performance"), L("性能模式、GPU 和功耗", "Performance modes, GPU, and power limits"), "\uE945"),
        "cooling" => (L("散热", "Cooling"), L("风扇策略、曲线和转速控制", "Fan strategies, curves, and RPM control"), "\uE9D9"),
        "battery" => (L("电池与电源", "Battery and power"), L("充电、供电与电池健康", "Charging, power delivery, and battery health"), "\uE850"),
        "display" => (L("显示", "Display"), L("护眼与色彩管理", "Eye care and color management"), "\uE7F4"),
        "sound" => (L("声音", "Sound"), L("Dolby 音效与智能降噪", "Dolby audio and intelligent noise cancellation"), "\uE767"),
        "input" => (L("输入设备", "Input devices"), L("键盘、功能键与触摸板", "Keyboard, function keys, and touchpad"), "\uE765"),
        "device" => (L("设备信息", "Device information"), L("硬件、固件与保修状态", "Hardware, firmware, and warranty"), "\uE772"),
        "advanced" => (L("高级工具", "Advanced tools"), L("固件启动与维护工具", "Firmware startup and maintenance tools"), "\uE90F"),
        "settings" => (L("设置", "Settings"), L("全局偏好、窗口行为与功能检测", "Global preferences, window behavior, and availability"), "\uE713"),
        _ => (L("概览", "Overview"), L("设备状态与当前控制模式", "Device status and current control modes"), "\uE80F")
    };

    private bool PageAvailable(string page)
    {
        var report = _runtime.Report;
        if (report is null) return true;
        return page switch
        {
            "performance" => report.AnyAvailable(FeatureIds.TemperatureMonitoring, FeatureIds.PerformanceMode, FeatureIds.GpuMode, FeatureIds.PowerSettings),
            "cooling" => report.AnyAvailable(FeatureIds.FanControl),
            "battery" => report.AnyAvailable(FeatureIds.BatteryChargeMode, FeatureIds.OvernightCharging, FeatureIds.AlwaysOnUsb, FeatureIds.FlipToStart, FeatureIds.BatteryInformation),
            "display" => report.AnyAvailable(FeatureIds.VantageEyeCare, FeatureIds.PcManagerEyeCare, FeatureIds.ColorManagement),
            "sound" => report.AnyAvailable(FeatureIds.DolbyAtmos, FeatureIds.SpeakerNoiseCancellation, FeatureIds.MicrophoneNoiseCancellation),
            "input" => report.AnyAvailable(FeatureIds.KeyboardBacklight, FeatureIds.KeyboardBacklightAutoOff, FeatureIds.FunctionLock, FeatureIds.CapsLockOsd, FeatureIds.NumLockOsd, FeatureIds.FnCtrlSwap, FeatureIds.Touchpad),
            "device" => report.AnyAvailable(FeatureIds.DeviceInformation, FeatureIds.WarrantyInformation),
            "advanced" => report.AnyAvailable(FeatureIds.BootLogo, FeatureIds.BiosSetup, FeatureIds.StartupInterrupt, FeatureIds.SecureWipe),
            _ => true
        };
    }

    private void UpdateNavigationSelection()
    {
        foreach (var pair in _navigation)
        {
            var selected = pair.Key == _selectedPage;
            pair.Value.Background = Brush(selected ? Palette.AccentSoft : "#00000000");
            pair.Value.Foreground = Brush(selected ? Palette.Accent : Palette.Muted);
            _indicators[pair.Key].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateResponsiveLayout(double width)
    {
        var collapse = width < 1020;
        if (_navigationColumn is null || _navigationSurface is null || _mainArea is null)
            return;
        _sidebarCollapsed = collapse;
        _navigationColumn.Width = new GridLength(collapse ? 78 : 238);
        _navigationSurface.Margin = collapse
            ? new Thickness(10, 14, 8, 14)
            : new Thickness(14, 17, 12, 17);
        _navigationSurface.Padding = collapse
            ? new Thickness(8, 12, 8, 10)
            : new Thickness(9, 13, 9, 11);
        if (_brandText is not null)
            _brandText.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in _navigationLabels)
            label.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in _navigation.Values)
        {
            button.Padding = collapse
                ? new Thickness(7, 10, 4, 10)
                : new Thickness(8, 10, 10, 10);
        }
        _mainArea.Margin = collapse
            ? new Thickness(4, 14, 14, 14)
            : new Thickness(6, 18, 22, 16);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        if (!_enableHardwareDetection)
            return;
        try
        {
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);
            await _runtime.InitializeAsync();
            ShowFanBackendStartupNotice();
            if (_startToTrayRequested && _runtime.Settings.StartWithWindows && _runtime.Settings.StartToTray)
                _runtime.HideToTray();
        }
        catch (Exception ex)
        {
            ShowToast(L("初始化失败：", "Initialization failed: ") + ex.Message, isError: true);
        }
    }

    private void ShowFanBackendStartupNotice()
    {
        var notice = _runtime.PrepareFanBackendStartupNotice();
        if (notice is null)
            return;

        var dialog = new FanBackendStartupNoticeWindow(
            notice.Title,
            notice.Content,
            _runtime.Settings.Language,
            _runtime.IsDark)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _runtime.SuppressFanBackendStartupNotice(
                notice.BackendIdentity);
        }
    }

    private void OnAvailabilityChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAvailabilityChanged(sender, args)));
            return;
        }
        if (!PageAvailable(_selectedPage)) _selectedPage = "overview";
        RebuildShell(disposePages: true);
    }

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAppearanceChanged(sender, args)));
            return;
        }
        ApplyAppearance();
        RebuildShell(disposePages: true);
    }

    private void OnOverviewLayoutChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnOverviewLayoutChanged(sender, args)));
            return;
        }
        foreach (var pageId in new[] { "overview", "performance", "cooling" })
        {
            if (_pages.Remove(pageId, out var page))
                page.Dispose();
        }
        if (_selectedPage is "overview" or "performance" or "cooling")
            RenderPage();
    }

    private void RebuildShell(bool disposePages)
    {
        if (disposePages) DisposePages();
        Content = BuildLayout();
        UpdateResponsiveLayout(ActualWidth);
    }

    private void OnSnapshotChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSnapshotChanged(sender, args)));
            return;
        }
        if (!string.IsNullOrWhiteSpace(_runtime.Snapshot.Error))
            ShowToast(L("状态刷新失败：", "Status refresh failed: ") + _runtime.Snapshot.Error, isError: true);
    }

    private void OnStatusChanged(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnStatusChanged(sender, message)));
            return;
        }
        ShowToast(message);
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized && _runtime.Settings.MinimizeToTray)
            _runtime.HideToTray();
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_forceClose) return;
        if (_runtime.IsSystemSessionEnding)
        {
            _forceClose = true;
            return;
        }
        if (_runtime.Settings.CloseToTray && !_runtime.ExitRequested)
        {
            args.Cancel = true;
            _runtime.HideToTray();
            return;
        }
        if (_preparingExit)
        {
            args.Cancel = true;
            return;
        }
        args.Cancel = true;
        _preparingExit = true;
        IsEnabled = false;
        try
        {
            await _runtime.RestoreForExitAsync();
        }
        catch (Exception ex)
        {
            _runtime.SetStatus(L("退出前恢复风扇失败：", "Failed to restore fans before exit: ") + ex.Message);
        }
        _forceClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        DisposeWindow();
        if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
            Application.Current.Shutdown();
    }

    private void DisposePages()
    {
        _pageHost.Content = null;
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
    }

    private void DisposeWindow()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePages();
        _runtime.SnapshotChanged -= OnSnapshotChanged;
        _runtime.AvailabilityChanged -= OnAvailabilityChanged;
        _runtime.AppearanceChanged -= OnAppearanceChanged;
        _runtime.OverviewLayoutChanged -= OnOverviewLayoutChanged;
        _runtime.StatusChanged -= OnStatusChanged;
        _toastTimer.Stop();
        if (_ownsRuntime) _runtime.Dispose();
    }

    private Brush CanvasBrush() => new LinearGradientBrush(
        ColorFrom(Palette.Canvas),
        ColorFrom(Palette.CanvasAlt),
        new Point(0, 0),
        new Point(1, 1));

    private Brush AccentGradient() => new LinearGradientBrush(
        ColorFrom(Palette.Accent),
        ColorFrom("#6B7CFF"),
        new Point(0, 0),
        new Point(1, 1));

    private static ImageSource LoadBrandIcon()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.UriSource = new Uri(
            "pack://application:,,,/ThinkBookToolkit;component/Assets/app-icon-tb.png",
            UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(ColorFrom(value));
        brush.Freeze();
        return brush;
    }

    private static Color ColorFrom(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private sealed class LoadingToolkitPage : ToolkitPageBase
    {
        public LoadingToolkitPage(ToolkitRuntimeService runtime) : base(runtime)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "\uE895",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = Brush(ToolkitPalette.For(runtime.IsDark).Accent),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = runtime.L("正在检测设备功能……", "Detecting device features…"),
                Foreground = Brush(ToolkitPalette.For(runtime.IsDark).Muted),
                FontSize = 15,
                Margin = new Thickness(0, 13, 0, 0)
            });
            Content = panel;
        }
    }
}
