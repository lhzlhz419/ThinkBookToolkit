using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;
using ThinkBookToolkit.FanBackend;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ThinkBookToolkit;

public sealed class MainWindow : Window
{
    private const string StartupTaskName = "ThinkBookToolkit";
    internal const int StartupDelaySeconds = 30;
    private const double HeatSoakEnterTempC = 75;
    private const double HeatSoakExitTempC = 65;
    private static readonly TimeSpan HeatSoakDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeatSoakExitAverageDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GpuModeRefreshInterval = TimeSpan.FromSeconds(5);
    private const int FanTargetChangeThresholdRpm = 100;
    private const int FixedModeHotkeyId = 0x54424643;
    private const int WmHotkey = 0x0312;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeCritical = 0x0006;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const uint DeviceNotifyWindowHandle = 0x00000000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly bool _embeddedMode;
    private readonly FanController _fanController = new();
    private TemperatureReader? _temperatureReader;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _trayMenuTimer = new();
    private readonly DispatcherTimer _fixedControlTimer = new();
    private readonly DispatcherTimer _itsModeTimer = new();
    private readonly DispatcherTimer _gpuModeTimer = new();
    private readonly List<FanProfile> _profiles;
    private readonly AppSettings _settings;
    private readonly ItsModeDetector _itsModeDetector = new();
    private readonly GameProcessDetector _gameProcessDetector;
    private static readonly ItsMode[] SwitchableItsModes =
        [ItsMode.Intelligent, ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Geek];

    private readonly TextBlock _cpuMetricTitle = new();
    private readonly TextBlock _gpuMetricTitle = new();
    private readonly TextBlock _vramMetricTitle = new();
    private readonly TextBlock _fan1MetricTitle = new();
    private readonly TextBlock _fan2MetricTitle = new();
    private readonly TextBlock _targetMetricTitle = new();
    private readonly TextBlock _cpuTempText = MetricValue();
    private readonly TextBlock _cpuPowerText = MetricValue();
    private readonly TextBlock _gpuTempText = MetricValue();
    private readonly TextBlock _gpuPowerText = MetricValue();
    private readonly TextBlock _vramTempText = MetricValue();
    private readonly TextBlock _fan1Text = MetricValue();
    private readonly TextBlock _fan2Text = MetricValue();
    private readonly TextBlock _targetText = MetricValue();
    private readonly TextBlock _statusText = new() { Text = "Idle", VerticalAlignment = VerticalAlignment.Center };

    private readonly ComboBox _profileCombo = new() { Width = 150 };
    private readonly TextBox _nameBox = new() { Width = 130 };
    private readonly ComboBox _intervalCombo = OptionCombo("1", "2", "5");
    private readonly ComboBox _smoothingCombo = OptionCombo("1", "2", "3", "5", "10");
    private readonly ComboBox _rampDownCombo = OptionCombo("10", "20", "50", "100", "inf");
    private readonly ComboBox _gameExitHoldCombo = OptionCombo("0", "10", "20", "30", "60");
    private readonly ComboBox _fixedModeCombo = OptionCombo("Normal", "Game");
    private readonly ComboBox _editFanCombo = OptionCombo("Fan 1", "Fan 2");
    private readonly ComboBox _languageCombo = OptionCombo("\u4e2d\u6587", "English");
    private readonly ComboBox _themeCombo = OptionCombo("Light", "Dark");
    private readonly ComboBox _itsModeCombo = OptionCombo("Auto", "Cool", "Performance", "Geek");
    private readonly ComboBox _gpuModeCombo = OptionCombo("Hybrid");
    private readonly Button _startButton = new() { Content = "Start", MinWidth = 76 };
    private readonly Button _saveButton = new() { MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _refreshButton = new() { MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _powerSettingsButton = new() { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _batterySettingsButton = new() { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _displaySettingsButton = new() { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _soundSettingsButton = new() { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _otherSettingsButton = new() { MinWidth = 96 };
    private readonly Button _deviceModelButton = new()
    {
        Padding = new Thickness(4, 1, 4, 3),
        Margin = new Thickness(12, 0, 0, 0),
        FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Right,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };
    private readonly Button _fixedModeHotkeyButton = new() { MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _fullSpeedCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _syncFanSpeedsCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _fixedSyncFanSpeedsCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _autoDetectGamesCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _startupCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
    private readonly CheckBox _startToTrayCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _minimizeToTrayCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _closeToTrayCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _disableControlOnSleepCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

    private readonly CurveEditor _cpuChart;
    private readonly CurveEditor _gpuChart;
    private readonly TextBlock _profileLabel = Label("");
    private readonly TextBlock _nameLabel = Label("");
    private readonly TextBlock _intervalLabel = Label("");
    private readonly TextBlock _smoothingLabel = Label("");
    private readonly TextBlock _rampDownLabel = Label("");
    private readonly TextBlock _gameExitHoldLabel = Label("");
    private readonly TextBlock _fixedModeLabel = Label("");
    private readonly TextBlock _fixedModeHotkeyLabel = Label("");
    private readonly TextBlock _editFanLabel = Label("");
    private readonly TextBlock _languageLabel = Label("");
    private readonly TextBlock _themeLabel = Label("");
    private readonly TextBlock _itsModeLabel = Label("");
    private readonly TextBlock _gpuModeLabel = Label("");
    private TabControl? _strategyTabs;
    private TabItem? _fixedStrategyTab;
    private TabItem? _fanCurveStrategyTab;
    private TabItem? _cpuTab;
    private TabItem? _gpuTab;
    private Grid? _root;
    private TabControl? _curveTabs;
    private Border? _fixedRpmPanel;
    private TextBlock? _fixedRpmHeader;
    private TextBlock? _fixedRpmNote;
    private readonly Dictionary<(bool Game, int Fan), TextBlock> _fixedColumnLabels = [];
    private Border? _bottomBorder;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayCpuGpuItem;
    private Forms.ToolStripMenuItem? _trayVramItem;
    private Forms.ToolStripMenuItem? _trayFanItem;
    private bool _exitRequested;
    private string _lastCpuText = "--";
    private string _lastGpuText = "--";
    private string _lastVramText = "--";
    private string _lastFan1Text = "--";
    private string _lastFan2Text = "--";
    private readonly List<Border> _metricBorders = [];
    private readonly List<TextBlock> _labels = [];

    private int _profileIndex;
    private bool _loadingProfile;
    private bool _loadingSettings;
    private bool _running;
    private bool _closingAfterRestore;
    private bool _exitRestoreInProgress;
    private bool _temperatureSampling;
    private bool _forcedSamplePending;
    private bool _fanSnapshotSampling;
    private bool _fixedControlSampling;
    private bool _fanWriteInProgress;
    private int _systemShutdownPending;
    private bool _fullSpeedEnabled;
    private bool _fullSpeedUsesAlternativeMethod;
    private bool _fullSpeedSwitching;
    private bool _resumeFanControlAfterFullSpeed;
    private bool _sleepTransitionPending;
    private bool _resumingAfterSleep;
    private bool _resumeFanControlAfterSleep;
    private bool _resumeFullSpeedAfterSleep;
    private readonly SemaphoreSlim _fanIoLock = new(1, 1);
    private readonly object _fanHardwareLock = new();
    private FanTargets? _lastTarget;
    private FanTargets? _lastAppliedTarget;
    private FanTargets? _queuedTarget;
    private int _fanProfileRevision;
    private int _queuedTargetProfileRevision;
    private ItsMode _currentItsMode = ItsMode.Unknown;
    private bool _gamesRunning;
    private bool _effectiveGameMode;
    private bool _overrideGameSeenSinceArmed;
    private bool _overrideNormalSawNoGamesSinceArmed;
    private bool _updatingFixedModeCombo;
    private bool _capturingFixedModeHotkey;
    private HwndSource? _hotkeySource;
    private IntPtr _suspendResumeNotification;
    private bool _globalFixedModeHotkeyRegistered;
    private bool _fanCurveWarningShownThisRun;
    private bool _hasConfirmedFixedState;
    private ItsMode _confirmedItsMode = ItsMode.Unknown;
    private bool _confirmedGamesRunning;
    private ItsMode _pendingItsMode = ItsMode.Unknown;
    private bool _pendingGamesRunning;
    private bool _hasPendingFixedState;
    private bool _updatingFixedRpmBoxes;
    private bool _updatingItsModeCombo;
    private bool _refreshingItsMode;
    private bool _switchingItsMode;
    private ItsMode _displayedItsMode = ItsMode.Unknown;
    private readonly List<GpuWorkingMode> _gpuModes = [];
    private bool _updatingGpuModeCombo;
    private bool _refreshingGpuMode;
    private bool _switchingGpuMode;
    private GpuWorkingMode? _displayedGpuMode;
    private GpuWorkingMode? _pendingGpuMode;
    private readonly string _bootSessionId =
        GpuModeRestartState.CurrentBootSessionId;
    private bool _pendingGpuModeLoadedAtStartup;
    private bool _pendingGpuModeApplyAttempted;
    private DateTimeOffset? _lastGameStopTime;
    private int _fanMinRpm = 1500;
    private int _fanMaxRpm = 5500;
    private bool _fanRangeDetected;
    private bool _defaultFanRangeResolved;
    private double? _smoothedCpuTempC;
    private double? _smoothedGpuTempC;
    private DateTimeOffset? _lastFanSnapshotTime;
    private DateTimeOffset? _lastFanWriteTime;
    private TemperatureSnapshot? _latestTemperatureSnapshot;
    private FanSnapshot? _latestFanSnapshot;
    private DateTimeOffset? _highTempSince;
    private bool _heatSoaked;
    private readonly Queue<(DateTimeOffset Timestamp, double TempC)> _heatSoakExitSamples = [];
    private readonly Dictionary<ItsMode, TextBlock> _fixedModeLabels = [];
    private readonly Dictionary<(ItsMode Mode, bool Game, int Fan), TextBox> _fixedRpmBoxes = [];
    private List<int> _cpuFan1Curve;
    private List<int> _cpuFan2Curve;
    private List<int> _gpuFan1Curve;
    private List<int> _gpuFan2Curve;
    private readonly FanRateLimiter _fanRateLimiter = new();
    private int _advancedCurvePointIndex;

    public MainWindow(
        bool startToTrayRequested = false,
        bool embeddedMode = false,
        AppSettings? sharedSettings = null)
    {
        _embeddedMode = embeddedMode;
        Title = "ThinkBook Toolkit - Performance & Cooling";
        Width = 1220;
        Height = 840;
        MinWidth = 1160;
        MinHeight = 620;
        _gameExitHoldCombo.Width = 58;
        _languageCombo.Width = 72;
        _themeCombo.Width = 64;
        _itsModeCombo.Width = 112;
        _itsModeCombo.IsEnabled = false;
        _itsModeCombo.VerticalAlignment = VerticalAlignment.Center;
        _gpuModeCombo.Width = 120;
        _gpuModeCombo.IsEnabled = false;
        _gpuModeCombo.VerticalAlignment = VerticalAlignment.Center;
        _settings = sharedSettings ?? CurveProfileStore.LoadSettings();
        _gameProcessDetector = new GameProcessDetector(_settings);
        if (!_settings.FanRpmLimitsCustomized)
            _settings.FanRpmLimits =
                CurveProfileStore.DefaultFanRpmLimitsForCurrentDevice();
        _defaultFanRangeResolved = _settings.FanRpmLimitsCustomized;
        FontFamily = UiTypography.FontFamilyFor(_settings.Language);
        _pendingGpuMode = ParsePendingGpuMode(_settings.PendingGpuMode);
        _pendingGpuModeLoadedAtStartup = _pendingGpuMode.HasValue;
        _profiles = CurveProfileStore.Load();
        _cpuFan1Curve = [.. _profiles[0].CpuFan1Curve];
        _cpuFan2Curve = [.. _profiles[0].CpuFan2Curve];
        _gpuFan1Curve = [.. _profiles[0].GpuFan1Curve];
        _gpuFan2Curve = [.. _profiles[0].GpuFan2Curve];
        _cpuChart = new CurveEditor("CPU fan curve", CurveProfileStore.CpuTemps, _cpuFan1Curve, _cpuFan2Curve);
        _gpuChart = new CurveEditor("GPU fan curve", CurveProfileStore.GpuTemps, _gpuFan1Curve, _gpuFan2Curve);
        _cpuChart.ValuesChanged += (fan1Values, fan2Values) =>
        {
            _cpuFan1Curve = fan1Values;
            _cpuFan2Curve = fan2Values;
        };
        _gpuChart.ValuesChanged += (fan1Values, fan2Values) =>
        {
            _gpuFan1Curve = fan1Values;
            _gpuFan2Curve = fan2Values;
        };

        Content = BuildLayout();
        ApplyConfiguredFanRange();
        ApplyDetectedFeatureVisibility();
        ApplyEmbeddedVisibility();
        HookSettingsControls();
        LoadSettingsControls();
        LoadProfile(Math.Max(0, Math.Min(_profiles.Count - 1, _settings.LastProfileIndex)));
        ApplyCurveEditSettings();
        ApplyLanguage();
        ApplyTheme();
        if (!_embeddedMode)
        {
            InitializeTrayIcon();
            ApplyStartupSetting();
        }

        SyncTimerIntervals();
        _timer.Tick += async (_, _) => await SampleAsync();

        _trayMenuTimer.Tick += async (_, _) => await RefreshTrayMenuAsync();

        _fixedControlTimer.Interval = TimeSpan.FromSeconds(2);
        _fixedControlTimer.Tick += async (_, _) => await EvaluateFixedRpmControlAsync();

        _itsModeCombo.SelectionChanged += OnItsModeSelectionChanged;
        _itsModeTimer.Interval = TimeSpan.FromSeconds(1);
        _itsModeTimer.Tick += async (_, _) => await RefreshItsModeAsync();

        _gpuModeCombo.SelectionChanged += OnGpuModeSelectionChanged;
        _gpuModeTimer.Interval = GpuModeRefreshInterval;
        _gpuModeTimer.Tick += async (_, _) => await RefreshGpuModeAsync();

        if (!_embeddedMode)
            StateChanged += (_, _) => OnStateChanged();
        SourceInitialized += (_, _) => InitializeGlobalHotkey();
        PreviewKeyDown += OnPreviewKeyDown;
        if (!_embeddedMode)
            Closing += OnClosing;
        Closed += (_, _) => OnClosed();
        Loaded += async (_, _) => await RefreshItsModeAsync();
        Loaded += async (_, _) => await LoadDeviceModelAsync();
        Loaded += async (_, _) =>
        {
            if (_embeddedMode)
                return;
            await RefreshGpuModeAsync();
            UpdateGpuModePolling();
        };

        if (_embeddedMode)
        {
            Loaded += (_, _) =>
            {
                _timer.Start();
                _fixedControlTimer.Start();
                _itsModeTimer.Start();
            };
        }
        else
        {
            _timer.Start();
            _trayMenuTimer.Start();
            _fixedControlTimer.Start();
            _itsModeTimer.Start();
        }

        if (!_embeddedMode && startToTrayRequested &&
            _settings.StartWithWindows && _settings.StartToTray)
            Loaded += (_, _) => HideWindowToTray();

        if (_settings.ResumeFanControlOnNextStart)
        {
            if (_embeddedMode)
            {
                Loaded += (_, _) => Dispatcher.BeginInvoke(
                    new Action(async () => await ResumeFanControlAsync()));
            }
            else
            {
                Dispatcher.BeginInvoke(
                    new Action(async () => await ResumeFanControlAsync()));
            }
        }
    }

    internal PerformanceRuntimeSnapshot RuntimeSnapshot() => new(
        _latestTemperatureSnapshot,
        _latestFanSnapshot,
        _lastTarget,
        _running,
        _fullSpeedEnabled,
        _settings.ControlStrategy,
        _profileIndex,
        _displayedItsMode,
        _displayedGpuMode,
        _settings.PendingGpuMode,
        _fanMinRpm,
        _fanMaxRpm,
        CurveProfileStore.NormalizeFanRpmLimits(_settings.FanRpmLimits),
        _effectiveGameMode,
        _confirmedGamesRunning,
        _statusText.Text);

    internal FanBackendControlSemantics RuntimeFanControlSemantics =>
        _fanController.ControlSemantics;

    internal bool RuntimeWouldUseFanControl(
        FanControlMode mode,
        ItsMode performanceMode)
    {
        if (mode == FanControlMode.FirmwareAutomatic)
            return false;
        if (mode != FanControlMode.FixedRpm)
            return true;

        var target = GetFixedTarget(performanceMode, _effectiveGameMode);
        return target is not { Fan1Rpm: 0, Fan2Rpm: 0 } ||
               _fanController.ControlSemantics.ZeroRpmBehavior !=
               FanTargetZeroBehavior.ReleaseFanToFirmwareControl;
    }

    internal IReadOnlyList<FanProfile> RuntimeProfiles() =>
        _profiles.Select(CloneProfile).ToArray();

    internal async Task RuntimeSetControlEnabledAsync(bool enabled)
    {
        if (enabled == _running)
            return;
        await ToggleRunningAsync();
        await SampleAsync(force: true);
        if (_running != enabled)
            throw new InvalidOperationException("The fan controller did not enter the requested state.");
    }

    internal async Task RuntimeRestoreFirmwareAutoAsync()
    {
        if (_fullSpeedEnabled)
        {
            _fullSpeedCheck.IsChecked = false;
            await ToggleFullSpeedAsync();
        }

        if (_running)
        {
            await RestoreAutoAsync();
        }
        else
        {
            await RestoreAutoWithLockAsync();
            SetResumeFanControlOnNextStart(false);
            _statusText.Text = T("AutoRestored");
        }

        await SampleAsync(force: true);
        if (_running || _fullSpeedEnabled)
            throw new InvalidOperationException("Firmware automatic fan control was not restored.");
    }

    internal bool RuntimePrepareForSystemShutdown(out string error)
    {
        if (Interlocked.Exchange(ref _systemShutdownPending, 1) != 0)
        {
            error = string.Empty;
            return true;
        }

        StopFanRuntimeTimers();
        var resumeFanControlOnNextStart = ShouldResumeFanControlAfterSystemShutdown(
            _running,
            _resumeFanControlAfterFullSpeed,
            _resumeFanControlAfterSleep);
        var fullSpeedWasEnabled = _fullSpeedEnabled || _fullSpeedSwitching;
        var fullSpeedUsedAlternativeMethod = _fullSpeedUsesAlternativeMethod;
        _running = false;
        _fullSpeedEnabled = false;
        _fullSpeedSwitching = true;
        _resumeFanControlAfterFullSpeed = false;
        _resumeFanControlAfterSleep = false;
        _resumeFullSpeedAfterSleep = false;
        ResetFanTargetState();

        Exception? failure = null;
        ExecuteFanHardwareOperation(() =>
        {
            if (fullSpeedWasEnabled && !fullSpeedUsedAlternativeMethod)
            {
                try
                {
                    _fanController.SetFullSpeed(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            try
            {
                _fanController.RestoreAuto();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });

        _fullSpeedUsesAlternativeMethod = false;
        _fullSpeedSwitching = false;
        _settings.ResumeFanControlOnNextStart = resumeFanControlOnNextStart;
        try
        {
            CurveProfileStore.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "Could not save the fan-resume state during system shutdown.",
                ex);
        }

        error = failure?.Message ?? string.Empty;
        return failure is null;
    }

    internal static bool ShouldResumeFanControlAfterSystemShutdown(
        bool controlRunning,
        bool resumeAfterFullSpeed,
        bool resumeAfterSleep) =>
        controlRunning || resumeAfterFullSpeed || resumeAfterSleep;

    internal async Task RuntimeSetFullSpeedAsync(bool enabled)
    {
        if (enabled == _fullSpeedEnabled)
            return;
        _fullSpeedCheck.IsChecked = enabled;
        await ToggleFullSpeedAsync();
        await SampleAsync(force: true);
        if (_fullSpeedEnabled != enabled)
            throw new InvalidOperationException("The firmware did not confirm the requested full-speed state.");
    }

    internal Task RuntimeRefreshAsync() => SampleAsync(force: true);

    internal void RuntimeReloadGameDetection() =>
        _gameProcessDetector.ReloadKnownGames();

    internal async Task RuntimeRestartDataReadersAsync()
    {
        _timer.Stop();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (_temperatureSampling && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        _temperatureReader?.Dispose();
        _gameProcessDetector.ReloadKnownGames();
        _temperatureReader = new TemperatureReader();
        _latestTemperatureSnapshot = null;
        _smoothedCpuTempC = null;
        _smoothedGpuTempC = null;
        _timer.Start();
        await SampleAsync(force: true);
    }

    internal GpuWorkerCommandResponse RuntimeQueryDiscreteGpuApplications()
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.QueryDiscreteGpuApplications();
    }

    internal GpuWorkerCommandResponse RuntimeKillDiscreteGpuApplications()
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.KillDiscreteGpuApplications();
    }

    internal GpuWorkerCommandResponse RuntimeApplyGpuOverclock(
        GpuOverclockSettings settings,
        bool force = false)
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.ApplyGpuOverclock(settings, force);
    }

    internal GpuWorkerCommandResponse RuntimeResetGpuOverclock()
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.ResetGpuOverclock();
    }

    internal void RuntimeSelectProfile(int index)
    {
        if (index < 0 || index >= _profiles.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        LoadProfile(index);
        if (_running && _settings.ControlStrategy == ControlStrategy.FanCurve)
        {
            unchecked { _fanProfileRevision++; }
            _queuedTarget = null;
        }
        _settings.LastProfileIndex = index;
        CurveProfileStore.SaveSettings(_settings);
    }

    internal bool RuntimeApplyProfile(int index, FanProfile profile)
    {
        if (_running || index < 0 || index >= _profiles.Count)
            return false;

        var normalized = CloneProfile(profile);
        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name)
            ? $"Profile {index + 1}"
            : normalized.Name.Trim();
        ClampProfileToConfiguredLimits(normalized);
        normalized.CpuCurve = [.. normalized.CpuFan1Curve];
        normalized.GpuCurve = [.. normalized.GpuFan1Curve];
        _profiles[index] = normalized;
        CurveProfileStore.Save(_profiles);
        LoadProfile(index);
        _settings.LastProfileIndex = index;
        CurveProfileStore.SaveSettings(_settings);
        _statusText.Text = T("Saved");
        return true;
    }

    internal bool RuntimeSetStrategy(ControlStrategy strategy)
    {
        if (_running || _fullSpeedEnabled)
            return false;
        _settings.ControlStrategy = strategy;
        if (_strategyTabs is not null)
        {
            var wasLoading = _loadingSettings;
            _loadingSettings = true;
            _strategyTabs.SelectedIndex = strategy == ControlStrategy.FixedRpm ? 0 : 1;
            _loadingSettings = wasLoading;
        }
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
        ApplyStrategyVisibility();
        CurveProfileStore.SaveSettings(_settings);
        return true;
    }

    internal void RuntimeSetManualFixedMode(bool gameMode) =>
        SetManualFixedMode(gameMode);

    internal static bool RuntimeIsValidHotkey(string hotkey) =>
        string.IsNullOrWhiteSpace(hotkey) ||
        TryParseHotkey(hotkey.Trim(), out _, out _);

    internal void RuntimeSaveFanPreferences(
        bool syncCurveFans,
        bool syncFixedFans,
        bool autoDetectGames,
        double gameExitHoldSeconds,
        int editFan,
        string hotkey,
        FixedRpmSettings fixedRpm)
    {
        var autoDetectionChanged = _settings.AutoDetectGames != autoDetectGames;
        _settings.SyncFanSpeeds = syncCurveFans;
        _settings.FixedSyncFanSpeeds = syncFixedFans;
        _settings.AutoDetectGames = autoDetectGames;
        _settings.GameExitHoldSeconds = gameExitHoldSeconds;
        _settings.EditFan = editFan == 2 ? 2 : 1;
        _settings.FixedModeHotkey = hotkey?.Trim() ?? string.Empty;
        _settings.FixedRpm = CurveProfileStore.NormalizeFixedRpmSettings(
            fixedRpm,
            _settings.FanRpmLimits);
        if (syncFixedFans)
        {
            CopyFixedFan1ToFan2(_settings.FixedRpm);
            _settings.FixedRpm = CurveProfileStore.NormalizeFixedRpmSettings(
                _settings.FixedRpm,
                _settings.FanRpmLimits);
        }

        if (autoDetectionChanged && autoDetectGames)
        {
            _settings.FixedGameModeOverride = FixedGameModeOverride.None;
            _settings.ManualGameMode = false;
            _overrideGameSeenSinceArmed = false;
            _overrideNormalSawNoGamesSinceArmed = false;
            _hasConfirmedFixedState = false;
            _hasPendingFixedState = false;
        }
        else if (autoDetectionChanged)
        {
            SetManualFixedMode(_effectiveGameMode);
        }
        CurveProfileStore.SaveSettings(_settings);
        RefreshFixedRpmBoxes();
        ApplyCurveEditSettings();
        RegisterFixedModeHotkey();
    }

    internal TimeSpan RuntimeBackendMinimumReadInterval =>
        _fanController.MinimumReadInterval;

    internal TimeSpan RuntimeBackendMinimumWriteInterval =>
        _fanController.MinimumWriteInterval;

    internal string RuntimeBackendIdentity =>
        _fanController.BackendIdentity;

    internal FanBackendStartupNotice? RuntimeBackendStartupNotice =>
        _fanController.StartupNotice;

    internal TimeSpan RuntimeEffectiveMinimumReadInterval =>
        EffectiveFanReadMinimumInterval;

    internal TimeSpan RuntimeEffectiveMinimumWriteInterval =>
        EffectiveFanWriteMinimumInterval;

    internal bool RuntimeBackendSupportsDisableControlOnSleep =>
        _fanController.SupportsDisableControlOnSleep;

    internal bool RuntimeCanAttemptDisableControlOnSleep =>
        CanAttemptDisableControlOnSleep(
            _fanController.SupportsDisableControlOnSleep,
            EffectiveFanReadMinimumInterval,
            EffectiveFanWriteMinimumInterval);

    internal void RuntimeRefreshSleepControlCapability()
    {
        _disableControlOnSleepCheck.Visibility =
            RuntimeCanAttemptDisableControlOnSleep
                ? Visibility.Visible
                : Visibility.Collapsed;
        _disableControlOnSleepCheck.Content =
            _fanController.SupportsDisableControlOnSleep
                ? T("DisableControlOnSleep")
                : T("AttemptDisableControlOnSleep");

        UnregisterSuspendResumeNotifications();
        RegisterSuspendResumeNotifications();
    }

    internal void RuntimeSetFanRpmLimits(FanRpmLimits limits)
    {
        if (_running || _fullSpeedEnabled)
            throw new InvalidOperationException("Stop fan control and full speed before changing fan RPM limits.");

        var previousLimits = CurveProfileStore.NormalizeFanRpmLimits(
            _settings.FanRpmLimits);
        var previousCustomized = _settings.FanRpmLimitsCustomized;
        var previousResolved = _defaultFanRangeResolved;
        var previousFixed = CloneFixedRpm(_settings.FixedRpm);
        var previousAdvanced = AdvancedFanCurve.Clone(
            _settings.AdvancedFanCurve);
        var previousProfiles = _profiles.Select(CloneProfile).ToArray();
        try
        {
            _settings.FanRpmLimits = CurveProfileStore.NormalizeFanRpmLimits(limits);
            _settings.FanRpmLimitsCustomized = true;
            _defaultFanRangeResolved = true;
            foreach (var profile in _profiles)
                ClampProfileToConfiguredLimits(profile);
            _settings.AdvancedFanCurve = AdvancedFanCurve.Normalize(
                _settings.AdvancedFanCurve,
                _settings.FanRpmLimits);

            CurveProfileStore.Save(_profiles);
            ApplyConfiguredFanRange();
            LoadProfile(_profileIndex);
            CurveProfileStore.SaveSettings(_settings);
        }
        catch
        {
            _settings.FanRpmLimits = previousLimits;
            _settings.FanRpmLimitsCustomized = previousCustomized;
            _settings.FixedRpm = previousFixed;
            _settings.AdvancedFanCurve = previousAdvanced;
            _defaultFanRangeResolved = previousResolved;
            for (var index = 0; index < _profiles.Count; index++)
                _profiles[index] = previousProfiles[index];
            ApplyConfiguredFanRange();
            LoadProfile(_profileIndex);
            try
            {
                CurveProfileStore.Save(_profiles);
                CurveProfileStore.SaveSettings(_settings);
            }
            catch
            {
            }
            throw;
        }
    }

    internal void RuntimeApplyFanConfiguration(
        int index,
        FanProfile profile,
        bool syncCurveFans,
        bool syncFixedFans,
        bool autoDetectGames,
        double gameExitHoldSeconds,
        int editFan,
        string hotkey,
        FixedRpmSettings fixedRpm,
        AdvancedFanCurveSettings advancedFanCurve)
    {
        if (_running || _fullSpeedEnabled)
            throw new InvalidOperationException("Stop fan control and full speed before applying fan settings.");
        if (index < 0 || index >= _profiles.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var previousProfile = CloneProfile(_profiles[index]);
        var previousActiveProfile = _profileIndex;
        var previousLastProfile = _settings.LastProfileIndex;
        var previousSyncCurve = _settings.SyncFanSpeeds;
        var previousSyncFixed = _settings.FixedSyncFanSpeeds;
        var previousAutoGames = _settings.AutoDetectGames;
        var previousHold = _settings.GameExitHoldSeconds;
        var previousEditFan = _settings.EditFan;
        var previousHotkey = _settings.FixedModeHotkey;
        var previousFixed = CloneFixedRpm(_settings.FixedRpm);
        var previousAdvanced = AdvancedFanCurve.Clone(
            _settings.AdvancedFanCurve);
        var previousManualGame = _settings.ManualGameMode;
        var previousOverride = _settings.FixedGameModeOverride;
        var previousOverrideGameSeen = _overrideGameSeenSinceArmed;
        var previousOverrideNormalSawNoGames = _overrideNormalSawNoGamesSinceArmed;
        var previousHasConfirmed = _hasConfirmedFixedState;
        var previousHasPending = _hasPendingFixedState;

        try
        {
            RuntimeSaveFanPreferences(
                syncCurveFans,
                syncFixedFans,
                autoDetectGames,
                gameExitHoldSeconds,
                editFan,
                hotkey,
                fixedRpm);
            _settings.AdvancedFanCurve = AdvancedFanCurve.Normalize(
                advancedFanCurve,
                _settings.FanRpmLimits);
            CurveProfileStore.SaveSettings(_settings);
            if (!RuntimeApplyProfile(index, profile))
                throw new InvalidOperationException("The fan runtime rejected the profile update.");
        }
        catch (Exception applyError)
        {
            try
            {
                _settings.SyncFanSpeeds = previousSyncCurve;
                _settings.FixedSyncFanSpeeds = previousSyncFixed;
                _settings.AutoDetectGames = previousAutoGames;
                _settings.GameExitHoldSeconds = previousHold;
                _settings.EditFan = previousEditFan;
                _settings.FixedModeHotkey = previousHotkey;
                _settings.FixedRpm = previousFixed;
                _settings.AdvancedFanCurve = previousAdvanced;
                _settings.ManualGameMode = previousManualGame;
                _settings.FixedGameModeOverride = previousOverride;
                _settings.LastProfileIndex = previousLastProfile;
                _overrideGameSeenSinceArmed = previousOverrideGameSeen;
                _overrideNormalSawNoGamesSinceArmed = previousOverrideNormalSawNoGames;
                _hasConfirmedFixedState = previousHasConfirmed;
                _hasPendingFixedState = previousHasPending;
                _profiles[index] = previousProfile;
                CurveProfileStore.Save(_profiles);
                CurveProfileStore.SaveSettings(_settings);
                LoadProfile(previousActiveProfile);
                RefreshFixedRpmBoxes();
                ApplyCurveEditSettings();
                RegisterFixedModeHotkey();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Applying fan settings failed and rollback was incomplete.",
                    applyError,
                    rollbackError);
            }
            throw;
        }
    }

    private static FixedRpmSettings CloneFixedRpm(FixedRpmSettings value) => new()
    {
        PowerSavingNormalFan1Rpm = value.PowerSavingNormalFan1Rpm,
        PowerSavingNormalFan2Rpm = value.PowerSavingNormalFan2Rpm,
        PowerSavingGameFan1Rpm = value.PowerSavingGameFan1Rpm,
        PowerSavingGameFan2Rpm = value.PowerSavingGameFan2Rpm,
        IntelligentNormalFan1Rpm = value.IntelligentNormalFan1Rpm,
        IntelligentNormalFan2Rpm = value.IntelligentNormalFan2Rpm,
        IntelligentGameFan1Rpm = value.IntelligentGameFan1Rpm,
        IntelligentGameFan2Rpm = value.IntelligentGameFan2Rpm,
        PerformanceNormalFan1Rpm = value.PerformanceNormalFan1Rpm,
        PerformanceNormalFan2Rpm = value.PerformanceNormalFan2Rpm,
        PerformanceGameFan1Rpm = value.PerformanceGameFan1Rpm,
        PerformanceGameFan2Rpm = value.PerformanceGameFan2Rpm,
        GeekNormalFan1Rpm = value.GeekNormalFan1Rpm,
        GeekNormalFan2Rpm = value.GeekNormalFan2Rpm,
        GeekGameFan1Rpm = value.GeekGameFan1Rpm,
        GeekGameFan2Rpm = value.GeekGameFan2Rpm
    };

    private static FanProfile CloneProfile(FanProfile profile) => new()
    {
        Name = profile.Name,
        TemperatureSmoothing = profile.TemperatureSmoothing,
        RampUpRpmPerSecond = profile.RampUpRpmPerSecond,
        FullRangeRampDownRpmPerSecond = profile.FullRangeRampDownRpmPerSecond,
        RampDownRpmPerSecond = profile.RampDownRpmPerSecond,
        CpuFan1Curve = [.. profile.CpuFan1Curve],
        CpuFan2Curve = [.. profile.CpuFan2Curve],
        GpuFan1Curve = [.. profile.GpuFan1Curve],
        GpuFan2Curve = [.. profile.GpuFan2Curve],
        CpuCurve = [.. profile.CpuCurve],
        GpuCurve = [.. profile.GpuCurve]
    };

    internal void RefreshSharedPollingInterval()
    {
        _loadingSettings = true;
        SelectComboValue(_intervalCombo, _settings.IntervalSeconds);
        _loadingSettings = false;
        SyncTimerIntervals();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        _root = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metrics = new UniformGrid
        {
            Columns = _embeddedMode ? 3 : 6,
            Margin = new Thickness(12, 10, 12, 8)
        };
        metrics.Children.Add(Metric(_cpuMetricTitle, MetricPair(_cpuTempText, _cpuPowerText)));
        metrics.Children.Add(Metric(_gpuMetricTitle, MetricPair(_gpuTempText, _gpuPowerText)));
        metrics.Children.Add(Metric(_vramMetricTitle, _vramTempText));
        metrics.Children.Add(Metric(_fan1MetricTitle, _fan1Text));
        metrics.Children.Add(Metric(_fan2MetricTitle, _fan2Text));
        metrics.Children.Add(Metric(_targetMetricTitle, _targetText));
        Grid.SetRow(metrics, 0);
        root.Children.Add(metrics);

        var controls = BuildControls();
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var strategyTabs = new TabControl { Margin = new Thickness(12, 0, 12, 8) };
        _strategyTabs = strategyTabs;
        _fixedRpmPanel = BuildFixedRpmPanel();
        _fixedStrategyTab = new TabItem { Content = _fixedRpmPanel };
        _fanCurveStrategyTab = new TabItem { Content = BuildFanCurvePanel() };
        strategyTabs.Items.Add(_fixedStrategyTab);
        strategyTabs.Items.Add(_fanCurveStrategyTab);
        strategyTabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == strategyTabs)
                OnStrategyTabChanged();
        };
        Grid.SetRow(strategyTabs, 2);
        root.Children.Add(strategyTabs);

        var bottom = new Border { Padding = new Thickness(12, 0, 12, 12), Child = _statusText };
        _bottomBorder = bottom;
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
    }

    private void ApplyDetectedFeatureVisibility()
    {
        var report = FeatureAvailabilityCache.Current;
        if (report is null)
            return;

        _itsModeCombo.Visibility = report.IsAvailable(FeatureIds.PerformanceMode)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _itsModeLabel.Visibility = _itsModeCombo.Visibility;
        _gpuModeCombo.Visibility = report.IsAvailable(FeatureIds.GpuMode)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _gpuModeLabel.Visibility = _gpuModeCombo.Visibility;
        _powerSettingsButton.Visibility = report.IsAvailable(FeatureIds.PowerSettings)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _batterySettingsButton.Visibility = report.AnyAvailable(
            FeatureIds.BatteryChargeMode,
            FeatureIds.OvernightCharging,
            FeatureIds.AlwaysOnUsb,
            FeatureIds.FlipToStart,
            FeatureIds.BatteryInformation)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _displaySettingsButton.Visibility = report.AnyAvailable(
            FeatureIds.VantageEyeCare,
            FeatureIds.PcManagerEyeCare,
            FeatureIds.ColorManagement)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _soundSettingsButton.Visibility = report.AnyAvailable(
            FeatureIds.DolbyAtmos,
            FeatureIds.SpeakerNoiseCancellation,
            FeatureIds.MicrophoneNoiseCancellation)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _otherSettingsButton.Visibility = report.AnyAvailable(
            FeatureIds.KeyboardBacklight,
            FeatureIds.KeyboardBacklightAutoOff,
            FeatureIds.FunctionLock,
            FeatureIds.CapsLockOsd,
            FeatureIds.NumLockOsd,
            FeatureIds.FnCtrlSwap,
            FeatureIds.Touchpad,
            FeatureIds.BootLogo,
            FeatureIds.BiosSetup,
            FeatureIds.StartupInterrupt,
            FeatureIds.SecureWipe,
            FeatureIds.WarrantyInformation)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _deviceModelButton.Visibility = report.AnyAvailable(
            FeatureIds.DeviceInformation,
            FeatureIds.WarrantyInformation)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyEmbeddedVisibility()
    {
        if (!_embeddedMode)
            return;

        // These buttons only exist to launch the legacy child windows. Their
        // controls are rendered as sibling sections by ToolkitMainWindow.
        _powerSettingsButton.Visibility = Visibility.Collapsed;
        _batterySettingsButton.Visibility = Visibility.Collapsed;
        _displaySettingsButton.Visibility = Visibility.Collapsed;
        _soundSettingsButton.Visibility = Visibility.Collapsed;
        _otherSettingsButton.Visibility = Visibility.Collapsed;
        _deviceModelButton.Visibility = Visibility.Collapsed;
    }

    private UIElement BuildControls()
    {
        var panel = new StackPanel { Margin = new Thickness(12, 0, 12, 8) };

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var generalControls = new WrapPanel { Orientation = Orientation.Horizontal };
        AddLabeledControl(generalControls, _intervalLabel, _intervalCombo);
        AddLabeledControl(generalControls, _languageLabel, _languageCombo);
        AddLabeledControl(generalControls, _themeLabel, _themeCombo);
        generalControls.Children.Add(_startupCheck);
        generalControls.Children.Add(_startToTrayCheck);
        generalControls.Children.Add(_minimizeToTrayCheck);
        generalControls.Children.Add(_closeToTrayCheck);
        generalControls.Children.Add(_disableControlOnSleepCheck);
        _disableControlOnSleepCheck.Visibility =
            RuntimeCanAttemptDisableControlOnSleep
                ? Visibility.Visible
                : Visibility.Collapsed;
        row1.Children.Add(generalControls);
        _deviceModelButton.Click += (_, _) => ShowDeviceInformationWindow();
        _deviceModelButton.ToolTip = T("OpenDeviceInformation");
        Grid.SetColumn(_deviceModelButton, 1);
        row1.Children.Add(_deviceModelButton);
        if (!_embeddedMode)
        {
            panel.Children.Add(BuildWorkspacePanel(
                row1,
                new Thickness(0, 0, 0, 8)));
        }

        var row3 = new Grid();
        row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var primaryActions = new StackPanel { Orientation = Orientation.Horizontal };
        _saveButton.Click += (_, _) => SaveCurrentProfile(requireStopped: true);
        primaryActions.Children.Add(_saveButton);

        _refreshButton.Click += async (_, _) => await SampleAsync(force: true);
        primaryActions.Children.Add(_refreshButton);

        _startButton.Click += async (_, _) => await ToggleRunningAsync();
        _startButton.Margin = new Thickness(0, 0, 6, 0);
        var palette = ToolkitPalette.For(IsDark);
        _startButton.Background = Brush(palette.Accent);
        _startButton.Foreground = Brushes.White;
        _startButton.BorderBrush = Brush(palette.Accent);
        primaryActions.Children.Add(_startButton);

        _fullSpeedCheck.Click += async (_, _) => await ToggleFullSpeedAsync();
        primaryActions.Children.Add(_fullSpeedCheck);
        row3.Children.Add(primaryActions);

        var settingsActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AddLabeledControl(settingsActions, _itsModeLabel, _itsModeCombo);
        AddLabeledControl(settingsActions, _gpuModeLabel, _gpuModeCombo);

        _powerSettingsButton.Click += (_, _) => ShowPowerSettingsWindow();
        _powerSettingsButton.Visibility = DeviceModelDetector.IsThinkBook16pG6Iax()
            ? Visibility.Visible
            : Visibility.Collapsed;
        settingsActions.Children.Add(_powerSettingsButton);

        _batterySettingsButton.Click += (_, _) => ShowBatterySettingsWindow();
        settingsActions.Children.Add(_batterySettingsButton);

        _displaySettingsButton.Click += (_, _) => ShowDisplaySettingsWindow();
        settingsActions.Children.Add(_displaySettingsButton);

        _soundSettingsButton.Click += (_, _) => ShowSoundSettingsWindow();
        settingsActions.Children.Add(_soundSettingsButton);

        _otherSettingsButton.Click += (_, _) => ShowOtherSettingsWindow();
        settingsActions.Children.Add(_otherSettingsButton);
        Grid.SetColumn(settingsActions, 1);
        row3.Children.Add(settingsActions);
        panel.Children.Add(BuildWorkspacePanel(row3, new Thickness(0)));

        return panel;
    }

    private Border BuildWorkspacePanel(UIElement content, Thickness margin)
    {
        var palette = ToolkitPalette.For(IsDark);
        return new Border
        {
            Background = Brush(palette.SurfaceRaised),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = margin,
            Child = content
        };
    }

    private void ShowPowerSettingsWindow()
    {
        if (!DeviceModelDetector.IsThinkBook16pG6Iax())
            return;

        var window = new PowerSettingsWindow(T, SelectedItsMode, IsDark, FontFamily, FontSize)
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private void ShowBatterySettingsWindow()
    {
        var window = new BatterySettingsWindow(
            T,
            IsDark,
            FontFamily,
            FontSize)
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private void ShowOtherSettingsWindow()
    {
        var window = new OtherSettingsWindow(
            T,
            IsDark,
            FontFamily,
            FontSize,
            GetSettingsRefreshInterval)
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private void ShowDeviceInformationWindow()
    {
        var window = new DeviceInformationWindow(T, IsDark, FontFamily, FontSize)
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private async Task LoadDeviceModelAsync()
    {
        try
        {
            var identity = await Task.Run(DeviceInformationService.ReadIdentity);
            _deviceModelButton.Content = string.IsNullOrWhiteSpace(identity.Model)
                ? T("DeviceInformation")
                : identity.Model;
        }
        catch
        {
            _deviceModelButton.Content = T("DeviceInformation");
        }
    }

    private void ShowDisplaySettingsWindow()
    {
        var window = new DisplaySettingsWindow(
            T,
            IsDark,
            FontFamily,
            FontSize,
            GetSettingsRefreshInterval,
            () => new PcManagerEyeCareDefaults(
                _settings.PcManagerNormalDefaultTemperature,
                _settings.PcManagerEyeCareDefaultTemperature),
            defaults =>
            {
                _settings.PcManagerNormalDefaultTemperature =
                    defaults.NormalTemperature;
                _settings.PcManagerEyeCareDefaultTemperature =
                    defaults.EyeCareTemperature;
                CurveProfileStore.SaveSettings(_settings);
            })
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private void ShowSoundSettingsWindow()
    {
        var window = new SoundSettingsWindow(
            T,
            IsDark,
            FontFamily,
            FontSize,
            GetSettingsRefreshInterval)
        {
            Owner = this
        };
        ShowSettingsDialog(window);
    }

    private void ShowSettingsDialog(Window window)
    {
        _gpuModeTimer.Stop();
        try
        {
            window.ShowDialog();
        }
        finally
        {
            UpdateGpuModePolling();
            _ = RefreshGpuModeAsync();
        }
    }

    private UIElement BuildFanCurvePanel()
    {
        var panel = new DockPanel();

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 6) };
        _profileCombo.ItemsSource = ProfileLabels();
        _profileCombo.SelectionChanged += (_, _) =>
        {
            if (!_loadingProfile && _profileCombo.SelectedIndex >= 0)
                ChangeProfile(_profileCombo.SelectedIndex);
        };
        AddLabeledControl(controls, _profileLabel, _profileCombo);
        AddLabeledControl(controls, _nameLabel, _nameBox);
        AddLabeledControl(controls, _editFanLabel, _editFanCombo);
        AddStandaloneControl(controls, _syncFanSpeedsCheck);
        AddLabeledControl(controls, _smoothingLabel, _smoothingCombo);
        AddLabeledControl(controls, _rampDownLabel, _rampDownCombo);
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls);

        var tabs = new TabControl { Margin = new Thickness(8, 0, 8, 8) };
        _curveTabs = tabs;
        _cpuTab = new TabItem { Content = _cpuChart };
        _gpuTab = new TabItem { Content = _gpuChart };
        tabs.Items.Add(_cpuTab);
        tabs.Items.Add(_gpuTab);
        panel.Children.Add(tabs);
        return panel;
    }

    private Border BuildFixedRpmPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(12, 12, 12, 8), HorizontalAlignment = HorizontalAlignment.Center };
        _fixedRpmHeader = new TextBlock
        {
            Text = "Fixed RPM",
            FontWeight = FontWeights.SemiBold,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 10),
            TextAlignment = TextAlignment.Center
        };
        _labels.Add(_fixedRpmHeader);
        panel.Children.Add(_fixedRpmHeader);

        var options = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddLabeledControl(options, _gameExitHoldLabel, _gameExitHoldCombo);
        AddStandaloneControl(options, _fixedSyncFanSpeedsCheck);
        AddStandaloneControl(options, _autoDetectGamesCheck);
        AddLabeledControl(options, _fixedModeLabel, _fixedModeCombo);
        AddLabeledControl(options, _fixedModeHotkeyLabel, _fixedModeHotkeyButton);
        panel.Children.Add(options);

        var grid = new Grid { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });

        for (var i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddFixedText(grid, "", 0, 0, true);
        _fixedColumnLabels[(false, 1)] = AddFixedText(grid, "Normal F1", 0, 1, true);
        _fixedColumnLabels[(false, 2)] = AddFixedText(grid, "Normal F2", 0, 2, true);
        _fixedColumnLabels[(true, 1)] = AddFixedText(grid, "Game F1", 0, 3, true);
        _fixedColumnLabels[(true, 2)] = AddFixedText(grid, "Game F2", 0, 4, true);

        AddFixedRpmRow(grid, 1, ItsMode.PowerSaving, "Cool");
        AddFixedRpmRow(grid, 2, ItsMode.Intelligent, "Auto");
        AddFixedRpmRow(grid, 3, ItsMode.Performance, "Performance");
        AddFixedRpmRow(grid, 4, ItsMode.Geek, "Geek");

        panel.Children.Add(grid);
        _fixedRpmNote = new TextBlock
        {
            Text = "0 = firmware auto. Non-zero RPM values are clamped to the detected fan range.",
            FontSize = 15,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _labels.Add(_fixedRpmNote);
        panel.Children.Add(_fixedRpmNote);

        return new Border
        {
            Margin = new Thickness(12, 0, 12, 8),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = panel
        };
    }

    private void AddFixedRpmRow(Grid grid, int row, ItsMode mode, string label)
    {
        _fixedModeLabels[mode] = AddFixedText(grid, label, row, 0, false);
        AddFixedRpmBox(grid, row, 1, mode, false, 1);
        AddFixedRpmBox(grid, row, 2, mode, false, 2);
        AddFixedRpmBox(grid, row, 3, mode, true, 1);
        AddFixedRpmBox(grid, row, 4, mode, true, 2);
    }

    private TextBlock AddFixedText(Grid grid, string text, int row, int column, bool bold)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 8, 8)
        };
        _labels.Add(block);
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
        return block;
    }

    private void AddFixedRpmBox(Grid grid, int row, int column, ItsMode mode, bool game, int fan)
    {
        var box = new TextBox
        {
            Width = 108,
            FontSize = 17,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8),
            Text = GetFixedRpmValue(mode, game, fan).ToString(CultureInfo.InvariantCulture)
        };
        box.TextChanged += (_, _) => SyncFixedRpmTextBox(mode, game, fan, box.Text);
        box.LostFocus += (_, _) => SaveFixedRpmSettingsFromUi();
        box.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
            {
                SaveFixedRpmSettingsFromUi();
                args.Handled = true;
            }
        };
        _fixedRpmBoxes[(mode, game, fan)] = box;
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column);
        grid.Children.Add(box);
    }

    private async Task SampleAsync(bool force = false)
    {
        if (_temperatureSampling)
        {
            if (force)
                _forcedSamplePending = true;
            return;
        }

        var profile = UiToProfile();
        SyncTimerIntervals();

        _temperatureSampling = true;
        try
        {
            var temps = await Task.Run(ReadTemperatures);
            _latestTemperatureSnapshot = temps;
            _ = RefreshFanSnapshotAsync();
            UpdateHeatSoak(temps);

            var smoothing = _settings.ControlStrategy == ControlStrategy.AdvancedCurve
                ? _settings.AdvancedFanCurve.TemperatureSmoothing
                : profile.TemperatureSmoothing;
            _smoothedCpuTempC = SmoothTemperature(_smoothedCpuTempC, temps.CpuTempC, smoothing);
            _smoothedGpuTempC = SmoothTemperature(_smoothedGpuTempC, temps.GpuTempC, smoothing);

            if (_fullSpeedEnabled)
            {
                if (_fullSpeedUsesAlternativeMethod &&
                    ShouldContinuouslyRewriteFanTarget())
                {
                    await WriteAlternativeFullSpeedWithLockAsync();
                }
                UpdateTemperatureUi(temps);
                _targetText.Text = T("FullSpeed");
                _cpuChart.SetCurrentTemp(temps.CpuTempC);
                _gpuChart.SetCurrentTemp(temps.GpuTempC);
                _statusText.Text = T("FullSpeedEnabled");
                UpdateTrayText();
                return;
            }

            if (_settings.ControlStrategy == ControlStrategy.FixedRpm)
            {
                if (!_running)
                    ResetFanTargetState();

                UpdateTemperatureUi(temps);
                _targetText.Text = FormatTarget(_lastTarget);
                _cpuChart.SetCurrentTemp(temps.CpuTempC);
                _gpuChart.SetCurrentTemp(temps.GpuTempC);
                _statusText.Text = $"{(_running ? T("Running") : T("Monitoring"))} | {T("Strategy")}: {T("FixedRpm")} | {T("Game")}: {FormatGameState()}";
                UpdateTrayText();
                return;
            }

            if (_smoothedCpuTempC is null && _smoothedGpuTempC is null)
            {
                UpdateTemperatureUi(temps);
                _targetText.Text = FormatTarget(_lastTarget);
                _cpuChart.SetCurrentTemp(temps.CpuTempC);
                _gpuChart.SetCurrentTemp(temps.GpuTempC);
                UpdateTrayText();
                return;
            }

            FanTargets rawTarget;
            double rampUp;
            double rampDown;
            if (_settings.ControlStrategy == ControlStrategy.AdvancedCurve)
            {
                var evaluated = AdvancedFanCurve.Evaluate(
                    _settings.AdvancedFanCurve.Points,
                    _advancedCurvePointIndex,
                    _smoothedCpuTempC,
                    _smoothedGpuTempC);
                _advancedCurvePointIndex = evaluated.Index;
                rawTarget = new FanTargets(
                    ClampCurveTarget(evaluated.Target.Fan1Rpm, 1),
                    ClampCurveTarget(evaluated.Target.Fan2Rpm, 2));
                rampUp = evaluated.RampUp;
                rampDown = evaluated.RampDown;
            }
            else
            {
                var cpuFan1Target = CurveProfileStore.Interpolate(CurveProfileStore.CpuTemps, _cpuFan1Curve, _smoothedCpuTempC);
                var gpuFan1Target = CurveProfileStore.Interpolate(CurveProfileStore.GpuTemps, _gpuFan1Curve, _smoothedGpuTempC);
                var cpuFan2Target = CurveProfileStore.Interpolate(CurveProfileStore.CpuTemps, _cpuFan2Curve, _smoothedCpuTempC);
                var gpuFan2Target = CurveProfileStore.Interpolate(CurveProfileStore.GpuTemps, _gpuFan2Curve, _smoothedGpuTempC);
                rawTarget = new FanTargets(
                    ClampCurveTarget(Math.Max(cpuFan1Target, gpuFan1Target), 1),
                    ClampCurveTarget(Math.Max(cpuFan2Target, gpuFan2Target), 2));
                rampUp = profile.RampUpRpmPerSecond;
                rampDown = EffectiveRampDownRate(
                    profile.FullRangeRampDownRpmPerSecond,
                    _heatSoaked ? profile.RampDownRpmPerSecond : 0);
            }
            var target = _fanRateLimiter.Apply(
                rawTarget,
                rampUp,
                rampDown,
                DateTimeOffset.Now,
                _running);

            if (_running)
            {
                if (target != _lastTarget)
                    _lastTarget = target;

                var targetToApply = SuppressSmallTargetChanges(target);
                if (targetToApply != _lastAppliedTarget ||
                    ShouldContinuouslyRewriteFanTarget())
                    QueueTargetApply(targetToApply);
            }
            if (!_running)
            {
                ResetFanTargetState();
            }

            UpdateTemperatureUi(temps);
            _targetText.Text = FormatTarget(target);
            _cpuChart.SetCurrentTemp(temps.CpuTempC);
            _gpuChart.SetCurrentTemp(temps.GpuTempC);
            _statusText.Text = $"{(_running ? T("Running") : T("Monitoring"))} | {T("HeatSoak")}: {(_heatSoaked ? T("On") : T("Off"))} | CPU: {temps.CpuSensor} | GPU: {temps.GpuSensor} | VRAM: {temps.VramSensor}";
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Fan runtime temperature sample failed.", ex);
            if (_running)
            {
                _running = false;
                _startButton.Content = T("Start");
                _queuedTarget = null;
                try
                {
                    await RestoreAutoWithLockAsync();
                    SetResumeFanControlOnNextStart(false);
                }
                catch { }
            }
            _statusText.Text = T("MonitorError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _temperatureSampling = false;
            if (_forcedSamplePending)
            {
                _forcedSamplePending = false;
                _ = SampleAsync(force: true);
            }
        }
    }

    private async Task RefreshFanSnapshotAsync()
    {
        if (_fanSnapshotSampling)
            return;

        var now = DateTimeOffset.Now;
        if (_lastFanSnapshotTime is DateTimeOffset lastSnapshotTime &&
            now - lastSnapshotTime < EffectiveFanReadMinimumInterval)
        {
            return;
        }

        _fanSnapshotSampling = true;
        try
        {
            await _fanIoLock.WaitAsync();
            FanSnapshot fans;
            try
            {
                _lastFanSnapshotTime = DateTimeOffset.Now;
                fans = await Task.Run(() => ExecuteFanHardwareOperation(
                    _fanController.ReadSnapshot));
            }
            finally
            {
                _fanIoLock.Release();
            }

            _latestFanSnapshot = fans;
            UpdateFanRange(fans.Limits);
            _fan1Text.Text = $"{fans.Fan1Rpm} RPM";
            _fan2Text.Text = $"{fans.Fan2Rpm} RPM";
            _lastFan1Text = _fan1Text.Text;
            _lastFan2Text = _fan2Text.Text;
            UpdateTrayMenuMetrics();
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Fan snapshot refresh failed.", ex);
            _statusText.Text = T("FanReadError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _fanSnapshotSampling = false;
        }
    }

    private int ClampForCurrentRange(int rpm, int fan)
    {
        var limits = _settings.FanRpmLimits;
        var minimum = fan == 2 ? limits.Fan2MinimumRpm : limits.Fan1MinimumRpm;
        var maximum = fan == 2 ? limits.Fan2MaximumRpm : limits.Fan1MaximumRpm;
        return Math.Max(minimum, Math.Min(maximum, rpm));
    }

    private int ClampCurveTarget(int rpm, int fan) =>
        rpm == 0
            ? 0
            : CurveProfileStore.SnapRpm(ClampForCurrentRange(rpm, fan));

    internal static double EffectiveRampDownRate(
        double fullRange,
        double highTemperature)
    {
        if (fullRange <= 0)
            return highTemperature;
        if (highTemperature <= 0)
            return fullRange;
        return Math.Min(fullRange, highTemperature);
    }

    private void QueueTargetApply(FanTargets target)
    {
        _queuedTarget = target;
        _queuedTargetProfileRevision = _fanProfileRevision;
        if (!_fanWriteInProgress)
            _ = ApplyQueuedTargetsAsync();
    }

    private FanTargets SuppressSmallTargetChanges(FanTargets target)
        => SuppressSmallTargetChanges(target, _lastAppliedTarget);

    internal static FanTargets SuppressSmallTargetChanges(
        FanTargets target,
        FanTargets? lastAppliedTarget)
    {
        if (lastAppliedTarget is not FanTargets appliedTarget)
            return target;

        return new FanTargets(
            Math.Abs(target.Fan1Rpm - appliedTarget.Fan1Rpm) <
                FanTargetChangeThresholdRpm
                ? appliedTarget.Fan1Rpm
                : target.Fan1Rpm,
            Math.Abs(target.Fan2Rpm - appliedTarget.Fan2Rpm) <
                FanTargetChangeThresholdRpm
                ? appliedTarget.Fan2Rpm
                : target.Fan2Rpm);
    }

    private async Task ApplyQueuedTargetsAsync()
    {
        _fanWriteInProgress = true;
        try
        {
            while (_running && _queuedTarget is FanTargets target)
            {
                var profileRevision = _queuedTargetProfileRevision;
                _queuedTarget = null;
                await WaitForOrdinaryFanWriteIntervalAsync();
                if (!_running)
                    break;
                if (profileRevision != _fanProfileRevision)
                    continue;

                if (_lastTarget is FanTargets latestTarget)
                    target = SuppressSmallTargetChanges(latestTarget);
                if (target == _lastAppliedTarget &&
                    !ShouldContinuouslyRewriteFanTarget())
                    continue;

                await _fanIoLock.WaitAsync();
                try
                {
                    if (!_running || _fullSpeedEnabled || _fullSpeedSwitching)
                        break;

                    await Task.Run(() => ExecuteFanHardwareOperation(() =>
                    {
                        if (SystemShutdownPending)
                            return;
                        _fanController.Apply(target.Fan1Rpm, target.Fan2Rpm);
                    }));
                    _lastAppliedTarget = target;
                    _lastFanWriteTime = DateTimeOffset.Now;
                }
                finally
                {
                    _fanIoLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _running = false;
            _queuedTarget = null;
            _lastTarget = null;
            _lastAppliedTarget = null;
            _startButton.Content = T("Start");
            _statusText.Text = T("FanWriteError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _fanWriteInProgress = false;
            if (_running && _queuedTarget is not null)
                _ = ApplyQueuedTargetsAsync();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindowFromTray();
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();
        UpdateTrayMenu();
        UpdateTrayText();
    }

    private async Task RefreshTrayMenuAsync()
    {
        if (_trayMenu?.Visible == true)
            await SampleAsync();

        UpdateTrayMenuMetrics();
        UpdateTrayText();
    }

    private void UpdateTrayMenu()
    {
        if (_trayMenu is null)
            return;

        _trayMenu.Items.Clear();
        _trayCpuGpuItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayVramItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayFanItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayMenu.Items.Add(_trayCpuGpuItem);
        _trayMenu.Items.Add(_trayVramItem);
        _trayMenu.Items.Add(_trayFanItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var toggleItem = new Forms.ToolStripMenuItem(_running ? T("Stop") : T("Start"))
        {
            Enabled = !_fullSpeedEnabled && !_fullSpeedSwitching
        };
        toggleItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ToggleRunningAsync()));
        _trayMenu.Items.Add(toggleItem);

        var strategyMenu = new Forms.ToolStripMenuItem(T("Strategy"));
        var fixedRpmItem = new Forms.ToolStripMenuItem(T("FixedRpm"))
        {
            Checked = _settings.ControlStrategy == ControlStrategy.FixedRpm
        };
        fixedRpmItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => TryChangeControlStrategy(ControlStrategy.FixedRpm)));
        strategyMenu.DropDownItems.Add(fixedRpmItem);

        var fanCurveItem = new Forms.ToolStripMenuItem(T("FanCurve"))
        {
            Checked = _settings.ControlStrategy == ControlStrategy.FanCurve
        };
        fanCurveItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => TryChangeControlStrategy(ControlStrategy.FanCurve)));
        strategyMenu.DropDownItems.Add(fanCurveItem);
        var advancedCurveItem = new Forms.ToolStripMenuItem(T("AdvancedCurve"))
        {
            Checked = _settings.ControlStrategy == ControlStrategy.AdvancedCurve
        };
        advancedCurveItem.Click += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => TryChangeControlStrategy(
                ControlStrategy.AdvancedCurve)));
        strategyMenu.DropDownItems.Add(advancedCurveItem);
        _trayMenu.Items.Add(strategyMenu);

        var profilesMenu = new Forms.ToolStripMenuItem(T("Profile"));
        profilesMenu.Enabled = _settings.ControlStrategy == ControlStrategy.FanCurve;
        for (var i = 0; i < _profiles.Count; i++)
        {
            var index = i;
            var item = new Forms.ToolStripMenuItem($"{i + 1}: {_profiles[i].Name}") { Checked = i == _profileIndex };
            item.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ChangeProfile(index)));
            profilesMenu.DropDownItems.Add(item);
        }
        _trayMenu.Items.Add(profilesMenu);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var showItem = new Forms.ToolStripMenuItem(T("ShowWindow"));
        showItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindowFromTray));
        _trayMenu.Items.Add(showItem);

        var exitItem = new Forms.ToolStripMenuItem(T("Exit"));
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _exitRequested = true;
            Close();
        }));
        _trayMenu.Items.Add(exitItem);
        UpdateTrayMenuMetrics();
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null)
            return;

        var text = $"CPU {_lastCpuText} GPU {_lastGpuText} F1 {_lastFan1Text} F2 {_lastFan2Text}";
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void UpdateTrayMenuMetrics()
    {
        if (_trayCpuGpuItem is not null)
            _trayCpuGpuItem.Text = $"CPU: {_lastCpuText}   GPU: {_lastGpuText}";
        if (_trayVramItem is not null)
            _trayVramItem.Text = $"VRAM: {_lastVramText}";
        if (_trayFanItem is not null)
            _trayFanItem.Text = $"{T("Fan1")}: {_lastFan1Text}   {T("Fan2")}: {_lastFan2Text}";
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var associatedIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (associatedIcon is not null)
                return associatedIcon;
        }

        return Drawing.SystemIcons.Application;
    }

    private void ShowWindowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        UpdateGpuModePolling();
        _ = RefreshGpuModeAsync();
    }

    private void HideWindowToTray()
    {
        _gpuModeTimer.Stop();
        ShowInTaskbar = false;
        Hide();
    }

    private void OnStateChanged()
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            HideWindowToTray();

        UpdateGpuModePolling();
        if (WindowState != WindowState.Minimized && IsVisible)
            _ = RefreshGpuModeAsync();
    }

    private void UpdateGpuModePolling()
    {
        if (_embeddedMode)
        {
            _gpuModeTimer.Stop();
            return;
        }

        if (IsVisible && WindowState != WindowState.Minimized)
            _gpuModeTimer.Start();
        else
            _gpuModeTimer.Stop();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exitRequested)
        {
            e.Cancel = true;
            HideWindowToTray();
            return;
        }

        if (_fullSpeedSwitching)
        {
            e.Cancel = true;
            return;
        }

        if (_closingAfterRestore || (!_running && !_fullSpeedEnabled))
            return;

        e.Cancel = true;
        if (_exitRestoreInProgress)
            return;

        _exitRestoreInProgress = true;
        _exitRequested = true;
        var wasRunning = _running;
        _startButton.IsEnabled = false;
        _fullSpeedCheck.IsEnabled = false;
        _startButton.Content = T("Stopping");
        _statusText.Text = T("RestoringAuto");
        try
        {
            await RestoreAutoForExitAsync();
            _closingAfterRestore = true;
            Close();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Firmware automatic restore during close failed.", ex);
            _running = wasRunning;
            _exitRequested = false;
            _exitRestoreInProgress = false;
            _startButton.IsEnabled = !_fullSpeedEnabled;
            _fullSpeedCheck.IsEnabled = true;
            _fullSpeedCheck.IsChecked = _fullSpeedEnabled;
            _startButton.Content = _running ? T("Stop") : T("Start");
            MessageBox.Show(this, ex.Message, T("RestoreAutoFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyStartupSetting()
    {
        var error = ApplyStartupTaskSetting(_settings);
        if (!string.IsNullOrWhiteSpace(error))
            _statusText.Text = error;
    }

    internal static string? ApplyStartupTaskSetting(AppSettings settings)
    {
        try
        {
            DeleteLegacyStartupRunEntry();

            if (settings.StartWithWindows)
                CreateStartupTask(
                    settings.StartToTray,
                    settings.DelayStartup);
            else
                DeleteStartupTask();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task WaitForOrdinaryFanWriteIntervalAsync()
    {
        while (_lastFanWriteTime is DateTimeOffset lastFanWriteTime)
        {
            var delay =
                EffectiveFanWriteMinimumInterval -
                (DateTimeOffset.Now - lastFanWriteTime);
            if (delay <= TimeSpan.Zero)
                return;
            await Task.Delay(delay);
        }
    }

    private static void DeleteLegacyStartupRunEntry()
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(StartupTaskName, throwOnMissingValue: false);
    }

    private static void CreateStartupTask(
        bool startToTray,
        bool delayStartup)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Cannot determine executable path for startup task.");

        var xmlPath = Path.Combine(Path.GetTempPath(), StartupTaskName + ".xml");
        try
        {
            File.WriteAllText(
                xmlPath,
                BuildStartupTaskXml(
                    executablePath,
                    startToTray,
                    delayStartup),
                Encoding.Unicode);
            RunSchtasks(false, "/Create", "/TN", StartupTaskName, "/XML", xmlPath, "/F");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static void DeleteStartupTask()
    {
        RunSchtasks(true, "/Delete", "/TN", StartupTaskName, "/F");
    }

    internal static string BuildStartupTaskXml(
        string executablePath,
        bool startToTray,
        bool delayStartup = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("Cannot determine current user SID for startup task.");

        var escapedSid = SecurityElement.Escape(sid);
        var escapedPath = SecurityElement.Escape(executablePath);
        var arguments = startToTray
            ? "      <Arguments>--startup --startup-tray</Arguments>\r\n"
            : "      <Arguments>--startup</Arguments>\r\n";
        var delay = delayStartup
            ? $"      <Delay>PT{StartupDelaySeconds}S</Delay>\r\n"
            : string.Empty;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{escapedSid}</Author>
    <Description>Start ThinkBook Toolkit at user logon.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedSid}</UserId>
{delay}    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{escapedSid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedPath}</Command>
{arguments}    </Exec>
  </Actions>
</Task>
""";
    }

    private static void RunSchtasks(bool ignoreFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!ignoreFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"schtasks.exe failed with exit code {process.ExitCode}. {output} {error}".Trim());
    }

    private void UpdateFanRange(IReadOnlyDictionary<string, FanLimit> limits)
    {
        if (_defaultFanRangeResolved)
            return;

        _defaultFanRangeResolved = true;
        if (!TryReadDetectedFanLimits(limits, out var detected))
            return;

        _settings.FanRpmLimits = detected;
        foreach (var profile in _profiles)
            ClampProfileToConfiguredLimits(profile);
        CurveProfileStore.Save(_profiles);
        ApplyConfiguredFanRange();
    }

    private static bool TryReadDetectedFanLimits(
        IReadOnlyDictionary<string, FanLimit> limits,
        out FanRpmLimits detected)
    {
        detected = new FanRpmLimits();
        var fan1 = limits
            .FirstOrDefault(pair => string.Equals(
                pair.Key,
                "fan1",
                StringComparison.OrdinalIgnoreCase))
            .Value;
        var fan2 = limits
            .FirstOrDefault(pair => string.Equals(
                pair.Key,
                "fan2",
                StringComparison.OrdinalIgnoreCase))
            .Value;
        if (fan1 is null ||
            fan2 is null ||
            fan1.MinRpm < 0 ||
            fan2.MinRpm < 0 ||
            fan1.MaxRpm <= fan1.MinRpm ||
            fan2.MaxRpm <= fan2.MinRpm)
        {
            return false;
        }

        detected = CurveProfileStore.NormalizeFanRpmLimits(new FanRpmLimits
        {
            Fan1MinimumRpm = fan1.MinRpm,
            Fan1MaximumRpm = fan1.MaxRpm,
            Fan2MinimumRpm = fan2.MinRpm,
            Fan2MaximumRpm = fan2.MaxRpm
        });
        return true;
    }

    private void ApplyConfiguredFanRange()
    {
        var limits = CurveProfileStore.NormalizeFanRpmLimits(_settings.FanRpmLimits);
        _settings.FanRpmLimits = limits;
        _fanRangeDetected = true;
        _fanMinRpm = Math.Min(limits.Fan1MinimumRpm, limits.Fan2MinimumRpm);
        _fanMaxRpm = Math.Max(limits.Fan1MaximumRpm, limits.Fan2MaximumRpm);
        _cpuFan1Curve = CurveProfileStore.ClampCurve(
            _cpuFan1Curve,
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm);
        _cpuFan2Curve = CurveProfileStore.ClampCurve(
            _cpuFan2Curve,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        _gpuFan1Curve = CurveProfileStore.ClampCurve(
            _gpuFan1Curve,
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm);
        _gpuFan2Curve = CurveProfileStore.ClampCurve(
            _gpuFan2Curve,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        _cpuChart.SetRpmRanges(
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        _gpuChart.SetRpmRanges(
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        _cpuChart.SetValues(_cpuFan1Curve, _cpuFan2Curve);
        _gpuChart.SetValues(_gpuFan1Curve, _gpuFan2Curve);
        _settings.FixedRpm = CurveProfileStore.NormalizeFixedRpmSettings(
            _settings.FixedRpm,
            limits);
        RefreshFixedRpmBoxes();
    }

    private void ClampProfileToConfiguredLimits(FanProfile profile)
    {
        var limits = _settings.FanRpmLimits;
        profile.CpuFan1Curve = CurveProfileStore.ClampCurve(
            profile.CpuFan1Curve,
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm);
        profile.CpuFan2Curve = CurveProfileStore.ClampCurve(
            profile.CpuFan2Curve,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        profile.GpuFan1Curve = CurveProfileStore.ClampCurve(
            profile.GpuFan1Curve,
            limits.Fan1MinimumRpm,
            limits.Fan1MaximumRpm);
        profile.GpuFan2Curve = CurveProfileStore.ClampCurve(
            profile.GpuFan2Curve,
            limits.Fan2MinimumRpm,
            limits.Fan2MaximumRpm);
        profile.CpuCurve = [.. profile.CpuFan1Curve];
        profile.GpuCurve = [.. profile.GpuFan1Curve];
    }

    private void ChangeProfile(int index)
    {
        try
        {
            RuntimeSelectProfile(index);
            if (_running &&
                _settings.ControlStrategy == ControlStrategy.FanCurve)
            {
                _ = SampleAsync(force: true);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    private void LoadProfile(int index)
    {
        _loadingProfile = true;
        _profileIndex = index;
        var profile = _profiles[index];
        _profileCombo.SelectedIndex = index;
        _nameBox.Text = profile.Name;
        SelectComboValue(_smoothingCombo, profile.TemperatureSmoothing);
        SelectComboValue(_rampDownCombo, profile.RampDownRpmPerSecond);
        var limits = _settings.FanRpmLimits;
        _cpuFan1Curve = _fanRangeDetected
            ? CurveProfileStore.ClampCurve(profile.CpuFan1Curve, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm)
            : [.. profile.CpuFan1Curve];
        _cpuFan2Curve = _fanRangeDetected
            ? CurveProfileStore.ClampCurve(profile.CpuFan2Curve, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm)
            : [.. profile.CpuFan2Curve];
        _gpuFan1Curve = _fanRangeDetected
            ? CurveProfileStore.ClampCurve(profile.GpuFan1Curve, limits.Fan1MinimumRpm, limits.Fan1MaximumRpm)
            : [.. profile.GpuFan1Curve];
        _gpuFan2Curve = _fanRangeDetected
            ? CurveProfileStore.ClampCurve(profile.GpuFan2Curve, limits.Fan2MinimumRpm, limits.Fan2MaximumRpm)
            : [.. profile.GpuFan2Curve];
        _cpuChart.SetValues(_cpuFan1Curve, _cpuFan2Curve);
        _gpuChart.SetValues(_gpuFan1Curve, _gpuFan2Curve);
        _loadingProfile = false;
        UpdateTrayMenu();
    }

    private FanProfile UiToProfile()
    {
        return new FanProfile
        {
            Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? $"Profile {_profileIndex + 1}" : _nameBox.Text.Trim(),
            TemperatureSmoothing = SelectedNumber(_smoothingCombo, 3),
            RampUpRpmPerSecond = _profiles[_profileIndex].RampUpRpmPerSecond,
            FullRangeRampDownRpmPerSecond = _profiles[_profileIndex].FullRangeRampDownRpmPerSecond,
            RampDownRpmPerSecond = SelectedRampDown(),
            CpuFan1Curve = [.. _cpuFan1Curve],
            CpuFan2Curve = [.. _cpuFan2Curve],
            GpuFan1Curve = [.. _gpuFan1Curve],
            GpuFan2Curve = [.. _gpuFan2Curve],
            CpuCurve = [.. _cpuFan1Curve],
            GpuCurve = [.. _gpuFan1Curve],
        };
    }

    private bool SaveCurrentProfile(bool requireStopped = true)
    {
        if (requireStopped && _running)
        {
            ShowStopFirstWarning();
            return false;
        }

        SaveFixedRpmSettingsFromUi();
        _profiles[_profileIndex] = UiToProfile();
        CurveProfileStore.Save(_profiles);
        CurveProfileStore.SaveSettings(_settings);
        _profileCombo.ItemsSource = ProfileLabels();
        _profileCombo.SelectedIndex = _profileIndex;
        _statusText.Text = T("Saved") + " " + CurveProfileStore.ProfilePath;
        UpdateTrayMenu();
        return true;
    }

    private async Task ToggleRunningAsync()
    {
        if (_fullSpeedEnabled || _fullSpeedSwitching)
            return;

        if (_running)
        {
            await RestoreAutoAsync();
            return;
        }

        SaveCurrentProfile(requireStopped: false);
        StartFanControl();
    }

    private async Task ToggleFullSpeedAsync()
    {
        if (_fullSpeedSwitching)
        {
            _fullSpeedCheck.IsChecked = _fullSpeedEnabled;
            return;
        }

        var enable = _fullSpeedCheck.IsChecked == true;
        var wasRunning = _running;
        _fullSpeedSwitching = true;
        _fullSpeedCheck.IsEnabled = false;
        _startButton.IsEnabled = false;

        try
        {
            if (enable)
            {
                _resumeFanControlAfterFullSpeed = wasRunning;
                SuspendFanControlForFullSpeed();
                _statusText.Text = T("EnablingFullSpeed");

                // Clear manual targets before enabling the firmware full-speed
                // mode so stale backend targets cannot remain active.
                await RestoreAutoWithLockAsync();
                await SetFullSpeedWithLockAsync(true);
                _fullSpeedEnabled = true;
                SetResumeFanControlOnNextStart(false);
                _targetText.Text = T("FullSpeed");
                _statusText.Text = T("FullSpeedEnabled");
            }
            else
            {
                _statusText.Text = T("DisablingFullSpeed");
                await SetFullSpeedWithLockAsync(false);
                _fullSpeedEnabled = false;
                _targetText.Text = "--";

                var shouldResume = _resumeFanControlAfterFullSpeed;
                _resumeFanControlAfterFullSpeed = false;
                if (shouldResume)
                {
                    StartFanControl();
                }
                else
                {
                    SetResumeFanControlOnNextStart(false);
                    _statusText.Text = T("AutoRestored");
                }
            }
        }
        catch (Exception ex)
        {
            if (enable)
            {
                try
                {
                    await SetFullSpeedWithLockAsync(false);
                }
                catch
                {
                    // Preserve the original failure for the user.
                }

                _fullSpeedEnabled = false;
                _fullSpeedCheck.IsChecked = false;
                if (wasRunning)
                    StartFanControl();
                else
                    SetResumeFanControlOnNextStart(false);
            }
            else
            {
                _fullSpeedCheck.IsChecked = _fullSpeedEnabled;
            }

            _statusText.Text = T("FullSpeedFailed") + ": " + ex.GetType().Name + ": " + ex.Message;
            if (_embeddedMode)
                throw;
            MessageBox.Show(this, ex.Message, T("FullSpeedFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _fullSpeedSwitching = false;
            _fullSpeedCheck.IsEnabled = true;
            _startButton.IsEnabled = !_fullSpeedEnabled;
            _startButton.Content = _running ? T("Stop") : T("Start");
            UpdateTrayMenu();
        }
    }

    private void SuspendFanControlForFullSpeed()
    {
        _running = false;
        ResetFanTargetState();
        _gamesRunning = false;
        _effectiveGameMode = false;
        _lastGameStopTime = null;
        _startButton.Content = T("Start");
        UpdateFixedModeCombo();
        UpdateTrayMenu();
    }

    private async Task ResumeFanControlAsync()
    {
        if (_running)
            return;

        StartFanControl();
        _statusText.Text = T("ControllerResumed");
        await SampleAsync(force: true);
    }

    private void StartFanControl()
    {
        _running = true;
        SetResumeFanControlOnNextStart(true);
        _lastTarget = null;
        _lastAppliedTarget = null;
        _lastFanWriteTime = null;
        _smoothedCpuTempC = null;
        _smoothedGpuTempC = null;
        _highTempSince = null;
        _heatSoaked = false;
        _heatSoakExitSamples.Clear();
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
        _currentItsMode = ItsMode.Unknown;
        _gamesRunning = false;
        _effectiveGameMode = _settings.FixedGameModeOverride == FixedGameModeOverride.GameUntilGamesEnd;
        _overrideGameSeenSinceArmed = false;
        _overrideNormalSawNoGamesSinceArmed = false;
        _hasConfirmedFixedState = false;
        _hasPendingFixedState = false;
        _lastGameStopTime = null;
        UpdateFixedModeCombo();
        _startButton.Content = T("Stop");
        _statusText.Text = T("ControllerEnabled");
        UpdateTrayMenu();
    }

    private async Task RestoreAutoAsync()
    {
        _running = false;
        _lastTarget = null;
        _lastAppliedTarget = null;
        _queuedTarget = null;
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
        _gamesRunning = false;
        _effectiveGameMode = false;
        _settings.FixedGameModeOverride = FixedGameModeOverride.None;
        _overrideGameSeenSinceArmed = false;
        _overrideNormalSawNoGamesSinceArmed = false;
        UpdateFixedModeCombo();
        _lastGameStopTime = null;
        _startButton.IsEnabled = false;
        _startButton.Content = T("Stopping");
        _statusText.Text = T("RestoringAuto");
        try
        {
            await RestoreAutoWithLockAsync();
            SetResumeFanControlOnNextStart(false);
            _startButton.Content = T("Start");
            _statusText.Text = T("AutoRestored");
        }
        catch (Exception ex)
        {
            _startButton.Content = T("Start");
            if (_embeddedMode)
            {
                StartFanControl();
                throw;
            }
            MessageBox.Show(this, ex.Message, T("RestoreAutoFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _startButton.IsEnabled = true;
            UpdateTrayMenu();
        }
    }

    private void OnClosed()
    {
        StopFanRuntimeTimers();
        UnregisterFixedModeHotkey();
        UnregisterSuspendResumeNotifications();
        _hotkeySource?.RemoveHook(WndProc);
        if (_running || _fullSpeedEnabled)
        {
            try
            {
                _fanIoLock.Wait();
                try
                {
                    if (_fullSpeedEnabled)
                    {
                        try
                        {
                            ExecuteFanHardwareOperation(() => _fanController.SetFullSpeed(false));
                        }
                        catch
                        {
                            // Still attempt to clear manual backend targets below.
                        }
                    }

                    try
                    {
                        ExecuteFanHardwareOperation(_fanController.RestoreAuto);
                    }
                    catch
                    {
                        // The window is already closing; this is a last resort.
                    }
                }
                finally { _fanIoLock.Release(); }
            }
            catch { }
        }
        _temperatureReader?.Dispose();
        DisplaySettingsController.Shutdown();
        SoundSettingsController.Shutdown();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
    }

    private async Task RestoreAutoWithLockAsync()
    {
        await _fanIoLock.WaitAsync();
        try
        {
            await Task.Run(() => ExecuteFanHardwareOperation(_fanController.RestoreAuto));
        }
        finally
        {
            _fanIoLock.Release();
        }
    }

    private void ExecuteFanHardwareOperation(Action operation)
    {
        lock (_fanHardwareLock)
            operation();
    }

    private T ExecuteFanHardwareOperation<T>(Func<T> operation)
    {
        lock (_fanHardwareLock)
            return operation();
    }

    private bool SystemShutdownPending =>
        Volatile.Read(ref _systemShutdownPending) != 0;

    private void StopFanRuntimeTimers()
    {
        _timer.Stop();
        _trayMenuTimer.Stop();
        _fixedControlTimer.Stop();
        _itsModeTimer.Stop();
        _gpuModeTimer.Stop();
    }

    private void BeginSuspendFanControlForSleep()
    {
        if (!RuntimeCanAttemptDisableControlOnSleep)
            return;

        var fullSpeedActiveOrEnabling = _fullSpeedEnabled ||
                                        (_fullSpeedSwitching && _fullSpeedCheck.IsChecked == true);
        if (!DisableControlOnSleepRequested ||
            _sleepTransitionPending ||
            (!_running && !fullSpeedActiveOrEnabling))
            return;

        _sleepTransitionPending = true;
        _resumeFanControlAfterSleep = _running;
        _resumeFullSpeedAfterSleep = fullSpeedActiveOrEnabling;
        SuspendFanControlForSleep();
    }

    private void SuspendFanControlForSleep()
    {
        _running = false;
        _fullSpeedEnabled = false;
        ResetFanTargetState();
        _startButton.Content = T("Start");
        _fullSpeedCheck.IsChecked = false;
        _targetText.Text = "--";

        Exception? failure = null;
        ExecuteFanHardwareOperation(() =>
        {
            if (_resumeFullSpeedAfterSleep)
            {
                try
                {
                    if (_fullSpeedUsesAlternativeMethod)
                        _fanController.RestoreAuto();
                    else
                        _fanController.SetFullSpeed(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            try
            {
                _fanController.RestoreAuto();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });

        _statusText.Text = failure is null
            ? T("AutoRestored")
            : $"{T("SleepControlFailed")}: {failure.Message}";
        UpdateTrayMenu();
    }

    private async Task ResumeFanControlAfterSleepAsync()
    {
        if (!_sleepTransitionPending || _resumingAfterSleep)
            return;

        _resumingAfterSleep = true;
        try
        {
            if (_resumeFullSpeedAfterSleep)
            {
                await SetFullSpeedWithLockAsync(true);
                _fullSpeedEnabled = true;
                _fullSpeedCheck.IsChecked = true;
                _startButton.IsEnabled = false;
                _targetText.Text = T("FullSpeed");
                _statusText.Text = T("FullSpeedEnabled");
            }
            else if (_resumeFanControlAfterSleep)
            {
                StartFanControl();
                _statusText.Text = T("ControlResumedAfterSleep");
                await SampleAsync(force: true);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"{T("RestoreAutoFailed")}: {ex.Message}";
        }
        finally
        {
            _resumeFanControlAfterSleep = false;
            _resumeFullSpeedAfterSleep = false;
            _sleepTransitionPending = false;
            _resumingAfterSleep = false;
            _startButton.IsEnabled = !_fullSpeedEnabled;
            _fullSpeedCheck.IsEnabled = true;
            UpdateTrayMenu();
        }
    }

    private async Task RestoreAutoForExitAsync()
    {
        _running = false;
        _queuedTarget = null;
        _lastTarget = null;
        _lastAppliedTarget = null;
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
        _gamesRunning = false;
        _effectiveGameMode = false;
        _lastGameStopTime = null;

        if (_fullSpeedEnabled)
        {
            await SetFullSpeedWithLockAsync(false);
            _fullSpeedEnabled = false;
            _fullSpeedCheck.IsChecked = false;
            _resumeFanControlAfterFullSpeed = false;
        }

        await RestoreAutoWithLockAsync();
    }

    private async Task EvaluateFixedRpmControlAsync()
    {
        if (_fixedControlSampling || !_running || _settings.ControlStrategy != ControlStrategy.FixedRpm)
            return;

        _fixedControlSampling = true;
        try
        {
            var mode = SelectedItsMode();
            var gamesRunning = _settings.AutoDetectGames &&
                               await Task.Run(
                                   () => _gameProcessDetector.AreGamesRunning());

            if (!UpdateConfirmedFixedState(mode, gamesRunning))
            {
                _statusText.Text = $"{T("Running")} | {T("PendingChange")} | {T("Game")}: {FormatGameState()}";
                return;
            }

            var target = GetFixedTarget(_currentItsMode, _effectiveGameMode);
            _lastTarget = target;
            _targetText.Text = FormatTarget(target);
            _statusText.Text = $"{T("Running")} | {T("Strategy")}: {T("FixedRpm")} | {T("Game")}: {FormatGameState()}";

            await ApplyFixedTargetAsync(target);
        }
        catch (Exception ex)
        {
            _running = false;
            ResetFanTargetState();
            _startButton.Content = T("Start");
            _statusText.Text = T("FanWriteError") + ": " + ex.GetType().Name + ": " + ex.Message;
            UpdateTrayMenu();
        }
        finally
        {
            _fixedControlSampling = false;
        }
    }

    private bool UpdateConfirmedFixedState(ItsMode sampledMode, bool sampledGamesRunning)
    {
        sampledGamesRunning = _settings.AutoDetectGames && sampledGamesRunning;

        if (!_hasConfirmedFixedState)
        {
            ConfirmFixedState(sampledMode, sampledGamesRunning);
            return true;
        }

        if (sampledMode == _confirmedItsMode && sampledGamesRunning == _confirmedGamesRunning)
        {
            _hasPendingFixedState = false;
            UpdateGameState(sampledGamesRunning);
            return true;
        }

        if (_hasPendingFixedState &&
            sampledMode == _pendingItsMode &&
            sampledGamesRunning == _pendingGamesRunning)
        {
            ConfirmFixedState(sampledMode, sampledGamesRunning);
            return true;
        }

        _pendingItsMode = sampledMode;
        _pendingGamesRunning = sampledGamesRunning;
        _hasPendingFixedState = true;
        return false;
    }

    private void ConfirmFixedState(ItsMode mode, bool gamesRunning)
    {
        _confirmedItsMode = mode;
        _confirmedGamesRunning = gamesRunning;
        _hasConfirmedFixedState = true;
        _hasPendingFixedState = false;
        _currentItsMode = mode;
        UpdateGameState(gamesRunning);
    }

    private void UpdateGameState(bool gamesRunning)
    {
        var now = DateTimeOffset.Now;
        if (gamesRunning)
        {
            _gamesRunning = true;
            if (_settings.FixedGameModeOverride == FixedGameModeOverride.GameUntilGamesEnd)
                _overrideGameSeenSinceArmed = true;
            if (_settings.FixedGameModeOverride == FixedGameModeOverride.NormalUntilGameStarts)
            {
                if (_overrideNormalSawNoGamesSinceArmed)
                {
                    _settings.FixedGameModeOverride = FixedGameModeOverride.None;
                    _settings.ManualGameMode = false;
                    CurveProfileStore.SaveSettings(_settings);
                }
                else
                {
                    _effectiveGameMode = false;
                    UpdateFixedModeCombo();
                    return;
                }
            }
            _effectiveGameMode = true;
            _lastGameStopTime = null;
            UpdateFixedModeCombo();
            return;
        }

        if (_gamesRunning)
            _lastGameStopTime = now;

        _gamesRunning = false;
        if (_settings.FixedGameModeOverride == FixedGameModeOverride.GameUntilGamesEnd && !_overrideGameSeenSinceArmed)
        {
            _effectiveGameMode = true;
            UpdateFixedModeCombo();
            return;
        }

        if (_settings.FixedGameModeOverride == FixedGameModeOverride.GameUntilGamesEnd && _overrideGameSeenSinceArmed)
        {
            _settings.FixedGameModeOverride = FixedGameModeOverride.None;
            _settings.ManualGameMode = false;
            _overrideGameSeenSinceArmed = false;
            CurveProfileStore.SaveSettings(_settings);
        }

        if (_settings.FixedGameModeOverride == FixedGameModeOverride.NormalUntilGameStarts)
        {
            _overrideNormalSawNoGamesSinceArmed = true;
            _effectiveGameMode = false;
            _lastGameStopTime = null;
            UpdateFixedModeCombo();
            return;
        }

        var holdSeconds = Math.Max(0, _settings.GameExitHoldSeconds);
        _effectiveGameMode = _lastGameStopTime is DateTimeOffset stoppedAt &&
                             now - stoppedAt < TimeSpan.FromSeconds(holdSeconds);
        UpdateFixedModeCombo();
    }

    private FanTargets GetFixedTarget(ItsMode mode, bool game)
    {
        var effectiveMode = mode == ItsMode.Unknown ? ItsMode.Intelligent : mode;
        var limits = _settings.FanRpmLimits;
        return new FanTargets(
            CurveProfileStore.ClampFixedRpm(
                GetFixedRpmValue(effectiveMode, game, 1),
                limits.Fan1MinimumRpm,
                limits.Fan1MaximumRpm),
            CurveProfileStore.ClampFixedRpm(
                GetFixedRpmValue(effectiveMode, game, 2),
                limits.Fan2MinimumRpm,
                limits.Fan2MaximumRpm));
    }

    private async Task ApplyFixedTargetAsync(FanTargets target)
    {
        var restoreAutomatic =
            target.Fan1Rpm == 0 &&
            target.Fan2Rpm == 0;
        if (!restoreAutomatic)
            target = SuppressSmallTargetChanges(target);
        if (target == _lastAppliedTarget &&
            !ShouldContinuouslyRewriteFanTarget())
            return;

        if (!restoreAutomatic &&
            _lastFanWriteTime is DateTimeOffset lastFanWriteTime &&
            DateTimeOffset.Now - lastFanWriteTime <
                EffectiveFanWriteMinimumInterval)
        {
            return;
        }

        await _fanIoLock.WaitAsync();
        try
        {
            if (!_running || _fullSpeedEnabled || _fullSpeedSwitching)
                return;

            await Task.Run(() =>
            {
                ExecuteFanHardwareOperation(() =>
                {
                    if (SystemShutdownPending)
                        return;
                    if (restoreAutomatic)
                        _fanController.RestoreAuto();
                    else
                        _fanController.Apply(target.Fan1Rpm, target.Fan2Rpm);
                });
            });
            _lastAppliedTarget = target;
            if (!restoreAutomatic)
                _lastFanWriteTime = DateTimeOffset.Now;
        }
        finally
        {
            _fanIoLock.Release();
        }
    }

    private int GetFixedRpmValue(ItsMode mode, bool game, int fan)
    {
        var settings = _settings.FixedRpm;
        return (mode, game, fan) switch
        {
            (ItsMode.PowerSaving, false, 1) => settings.PowerSavingNormalFan1Rpm,
            (ItsMode.PowerSaving, false, 2) => settings.PowerSavingNormalFan2Rpm,
            (ItsMode.PowerSaving, true, 1) => settings.PowerSavingGameFan1Rpm,
            (ItsMode.PowerSaving, true, 2) => settings.PowerSavingGameFan2Rpm,
            (ItsMode.Intelligent, false, 1) => settings.IntelligentNormalFan1Rpm,
            (ItsMode.Intelligent, false, 2) => settings.IntelligentNormalFan2Rpm,
            (ItsMode.Intelligent, true, 1) => settings.IntelligentGameFan1Rpm,
            (ItsMode.Intelligent, true, 2) => settings.IntelligentGameFan2Rpm,
            (ItsMode.Performance, false, 1) => settings.PerformanceNormalFan1Rpm,
            (ItsMode.Performance, false, 2) => settings.PerformanceNormalFan2Rpm,
            (ItsMode.Performance, true, 1) => settings.PerformanceGameFan1Rpm,
            (ItsMode.Performance, true, 2) => settings.PerformanceGameFan2Rpm,
            (ItsMode.Geek, false, 1) => settings.GeekNormalFan1Rpm,
            (ItsMode.Geek, false, 2) => settings.GeekNormalFan2Rpm,
            (ItsMode.Geek, true, 1) => settings.GeekGameFan1Rpm,
            (ItsMode.Geek, true, 2) => settings.GeekGameFan2Rpm,
            _ => GetFixedRpmValue(ItsMode.Intelligent, game, fan)
        };
    }

    private void SetFixedRpmValue(FixedRpmSettings settings, ItsMode mode, bool game, int fan, int value)
    {
        switch (mode, game, fan)
        {
            case (ItsMode.PowerSaving, false, 1):
                settings.PowerSavingNormalFan1Rpm = value;
                break;
            case (ItsMode.PowerSaving, false, 2):
                settings.PowerSavingNormalFan2Rpm = value;
                break;
            case (ItsMode.PowerSaving, true, 1):
                settings.PowerSavingGameFan1Rpm = value;
                break;
            case (ItsMode.PowerSaving, true, 2):
                settings.PowerSavingGameFan2Rpm = value;
                break;
            case (ItsMode.Intelligent, false, 1):
                settings.IntelligentNormalFan1Rpm = value;
                break;
            case (ItsMode.Intelligent, false, 2):
                settings.IntelligentNormalFan2Rpm = value;
                break;
            case (ItsMode.Intelligent, true, 1):
                settings.IntelligentGameFan1Rpm = value;
                break;
            case (ItsMode.Intelligent, true, 2):
                settings.IntelligentGameFan2Rpm = value;
                break;
            case (ItsMode.Performance, false, 1):
                settings.PerformanceNormalFan1Rpm = value;
                break;
            case (ItsMode.Performance, false, 2):
                settings.PerformanceNormalFan2Rpm = value;
                break;
            case (ItsMode.Performance, true, 1):
                settings.PerformanceGameFan1Rpm = value;
                break;
            case (ItsMode.Performance, true, 2):
                settings.PerformanceGameFan2Rpm = value;
                break;
            case (ItsMode.Geek, false, 1):
                settings.GeekNormalFan1Rpm = value;
                break;
            case (ItsMode.Geek, false, 2):
                settings.GeekNormalFan2Rpm = value;
                break;
            case (ItsMode.Geek, true, 1):
                settings.GeekGameFan1Rpm = value;
                break;
            case (ItsMode.Geek, true, 2):
                settings.GeekGameFan2Rpm = value;
                break;
        }
    }

    private void SaveFixedRpmSettingsFromUi()
    {
        if (_loadingSettings)
            return;

        var settings = new FixedRpmSettings();
        foreach (var mode in new[] { ItsMode.PowerSaving, ItsMode.Intelligent, ItsMode.Performance, ItsMode.Geek })
        {
            SetFixedRpmValue(settings, mode, false, 1, ParseFixedRpmBox(mode, false, 1));
            SetFixedRpmValue(settings, mode, false, 2, ParseFixedRpmBox(mode, false, 2));
            SetFixedRpmValue(settings, mode, true, 1, ParseFixedRpmBox(mode, true, 1));
            SetFixedRpmValue(settings, mode, true, 2, ParseFixedRpmBox(mode, true, 2));
        }

        _settings.FixedRpm = CurveProfileStore.NormalizeFixedRpmSettings(
            settings,
            _settings.FanRpmLimits);
        if (_settings.FixedSyncFanSpeeds)
        {
            CopyFixedFan1ToFan2(_settings.FixedRpm);
            _settings.FixedRpm = CurveProfileStore.NormalizeFixedRpmSettings(
                _settings.FixedRpm,
                _settings.FanRpmLimits);
        }
        RefreshFixedRpmBoxes();
        CurveProfileStore.SaveSettings(_settings);
    }

    private int ParseFixedRpmBox(ItsMode mode, bool game, int fan)
    {
        if (_fixedRpmBoxes.TryGetValue((mode, game, fan), out var box) &&
            int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return GetFixedRpmValue(mode, game, fan);
    }

    private void RefreshFixedRpmBoxes()
    {
        _updatingFixedRpmBoxes = true;
        try
        {
            foreach (var ((mode, game, fan), box) in _fixedRpmBoxes)
                box.Text = GetFixedRpmValue(mode, game, fan).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updatingFixedRpmBoxes = false;
        }
    }

    private void SyncFixedRpmTextBox(ItsMode mode, bool game, int fan, string text)
    {
        if (_updatingFixedRpmBoxes || !_settings.FixedSyncFanSpeeds)
            return;

        var otherFan = fan == 1 ? 2 : 1;
        if (!_fixedRpmBoxes.TryGetValue((mode, game, otherFan), out var otherBox))
            return;

        _updatingFixedRpmBoxes = true;
        try
        {
            otherBox.Text = text;
        }
        finally
        {
            _updatingFixedRpmBoxes = false;
        }
    }

    private static void CopyFixedFan1ToFan2(FixedRpmSettings settings)
    {
        settings.PowerSavingNormalFan2Rpm = settings.PowerSavingNormalFan1Rpm;
        settings.PowerSavingGameFan2Rpm = settings.PowerSavingGameFan1Rpm;
        settings.IntelligentNormalFan2Rpm = settings.IntelligentNormalFan1Rpm;
        settings.IntelligentGameFan2Rpm = settings.IntelligentGameFan1Rpm;
        settings.PerformanceNormalFan2Rpm = settings.PerformanceNormalFan1Rpm;
        settings.PerformanceGameFan2Rpm = settings.PerformanceGameFan1Rpm;
        settings.GeekNormalFan2Rpm = settings.GeekNormalFan1Rpm;
        settings.GeekGameFan2Rpm = settings.GeekGameFan1Rpm;
    }

    private void UpdateFixedModeCombo()
    {
        _updatingFixedModeCombo = true;
        try
        {
            _fixedModeCombo.SelectedIndex = _effectiveGameMode ? 1 : 0;
        }
        finally
        {
            _updatingFixedModeCombo = false;
        }
    }

    private void SetManualFixedMode(bool gameMode)
    {
        _settings.FixedGameModeOverride = gameMode
            ? FixedGameModeOverride.GameUntilGamesEnd
            : FixedGameModeOverride.NormalUntilGameStarts;
        _settings.ManualGameMode = gameMode;
        _overrideGameSeenSinceArmed = false;
        _overrideNormalSawNoGamesSinceArmed = !gameMode && !_confirmedGamesRunning;
        _effectiveGameMode = gameMode;
        if (!gameMode)
            _lastGameStopTime = null;
        UpdateFixedModeCombo();
        CurveProfileStore.SaveSettings(_settings);
    }

    private void ToggleManualFixedMode()
    {
        SetManualFixedMode(!_effectiveGameMode);
    }

    private void UpdateFixedModeHotkeyButton()
    {
        _fixedModeHotkeyButton.Content = string.IsNullOrWhiteSpace(_settings.FixedModeHotkey)
            ? T("None")
            : _settings.FixedModeHotkey;
    }

    private void BeginFixedModeHotkeyCapture()
    {
        _capturingFixedModeHotkey = true;
        _fixedModeHotkeyButton.Content = T("PressShortcut");
        _fixedModeHotkeyButton.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (_capturingFixedModeHotkey)
        {
            CaptureFixedModeHotkey(args);
        }
    }

    private void CaptureFixedModeHotkey(KeyEventArgs args)
    {
        args.Handled = true;
        var key = RealKey(args);
        if (key is Key.Escape or Key.Back or Key.Delete)
        {
            _settings.FixedModeHotkey = "";
            _capturingFixedModeHotkey = false;
            UpdateFixedModeHotkeyButton();
            RegisterFixedModeHotkey();
            CurveProfileStore.SaveSettings(_settings);
            return;
        }

        if (IsModifierKey(key))
            return;

        var hotkey = FormatHotkey(Keyboard.Modifiers, key);
        if (!IsAllowedGlobalHotkey(Keyboard.Modifiers, key))
        {
            _statusText.Text = T("HotkeyNeedsModifier");
            return;
        }

        _settings.FixedModeHotkey = hotkey;
        _capturingFixedModeHotkey = false;
        UpdateFixedModeHotkeyButton();
        RegisterFixedModeHotkey();
        CurveProfileStore.SaveSettings(_settings);
    }

    private static Key RealKey(KeyEventArgs args)
    {
        return args.Key switch
        {
            Key.System => args.SystemKey,
            Key.ImeProcessed => args.ImeProcessedKey,
            _ => args.Key
        };
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private void InitializeGlobalHotkey()
    {
        _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hotkeySource?.AddHook(WndProc);
        RegisterSuspendResumeNotifications();
        RegisterFixedModeHotkey();
    }

    private void RegisterSuspendResumeNotifications()
    {
        if (!RuntimeCanAttemptDisableControlOnSleep ||
            _hotkeySource is null ||
            _suspendResumeNotification != IntPtr.Zero)
            return;

        _suspendResumeNotification = RegisterSuspendResumeNotification(
            _hotkeySource.Handle,
            DeviceNotifyWindowHandle);
        if (_suspendResumeNotification == IntPtr.Zero)
            _statusText.Text = $"Power notification registration failed: {Marshal.GetLastWin32Error()}";
    }

    private void UnregisterSuspendResumeNotifications()
    {
        if (_suspendResumeNotification == IntPtr.Zero)
            return;

        UnregisterSuspendResumeNotification(_suspendResumeNotification);
        _suspendResumeNotification = IntPtr.Zero;
    }

    private void RegisterFixedModeHotkey()
    {
        if (_hotkeySource is null)
            return;

        UnregisterFixedModeHotkey();
        if (string.IsNullOrWhiteSpace(_settings.FixedModeHotkey))
            return;

        if (!TryParseHotkey(_settings.FixedModeHotkey, out var modifiers, out var key))
        {
            _statusText.Text = T("HotkeyInvalid");
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || !RegisterHotKey(_hotkeySource.Handle, FixedModeHotkeyId, modifiers | ModNoRepeat, (uint)virtualKey))
        {
            _statusText.Text = $"{T("HotkeyRegisterFailed")}: {_settings.FixedModeHotkey}";
            return;
        }

        _globalFixedModeHotkeyRegistered = true;
    }

    private void UnregisterFixedModeHotkey()
    {
        if (!_globalFixedModeHotkeyRegistered || _hotkeySource is null)
            return;

        UnregisterHotKey(_hotkeySource.Handle, FixedModeHotkeyId);
        _globalFixedModeHotkeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == FixedModeHotkeyId)
        {
            ToggleManualFixedMode();
            handled = true;
        }
        else if (msg == WmPowerBroadcast)
        {
            switch (wParam.ToInt32())
            {
                case PbtApmSuspend:
                    BeginSuspendFanControlForSleep();
                    handled = true;
                    return new IntPtr(1);
                case PbtApmResumeCritical:
                case PbtApmResumeSuspend:
                case PbtApmResumeAutomatic:
                    _ = ResumeFanControlAfterSleepAsync();
                    handled = true;
                    return new IntPtr(1);
            }
        }

        return IntPtr.Zero;
    }

    private static bool TryParseHotkey(string text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }

        return key != Key.None && IsAllowedGlobalHotkey(ToWpfModifiers(modifiers), key);
    }

    private static ModifierKeys ToWpfModifiers(uint modifiers)
    {
        var result = ModifierKeys.None;
        if ((modifiers & ModControl) != 0)
            result |= ModifierKeys.Control;
        if ((modifiers & ModAlt) != 0)
            result |= ModifierKeys.Alt;
        if ((modifiers & ModShift) != 0)
            result |= ModifierKeys.Shift;
        if ((modifiers & ModWin) != 0)
            result |= ModifierKeys.Windows;
        return result;
    }

    private static bool IsAllowedGlobalHotkey(ModifierKeys modifiers, Key key)
    {
        return modifiers != ModifierKeys.None || key is >= Key.F1 and <= Key.F24;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    private void ApplyStrategyVisibility()
    {
        var fixedMode = _settings.ControlStrategy == ControlStrategy.FixedRpm;
        if (_strategyTabs is not null)
        {
            var wasLoading = _loadingSettings;
            _loadingSettings = true;
            _strategyTabs.SelectedIndex = fixedMode ? 0 : 1;
            _loadingSettings = wasLoading;
        }

        if (fixedMode)
        {
            _queuedTarget = null;
        }
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
    }

    private void OnStrategyTabChanged()
    {
        if (_loadingSettings || _strategyTabs is null)
            return;

        var selectedStrategy = _strategyTabs.SelectedIndex == 1 ? ControlStrategy.FanCurve : ControlStrategy.FixedRpm;
        if (TryChangeControlStrategy(selectedStrategy))
            return;

        _loadingSettings = true;
        _strategyTabs.SelectedIndex = _settings.ControlStrategy == ControlStrategy.FixedRpm ? 0 : 1;
        _loadingSettings = false;
    }

    private bool TryChangeControlStrategy(ControlStrategy selectedStrategy)
    {
        var strategyChanged = selectedStrategy != _settings.ControlStrategy;
        if (_running && strategyChanged)
        {
            ShowStopFirstWarning();
            return false;
        }

        if (strategyChanged &&
            (selectedStrategy is ControlStrategy.FanCurve or
                ControlStrategy.AdvancedCurve) &&
            !_settings.FanCurveWarningAccepted &&
            !_fanCurveWarningShownThisRun)
        {
            if (!ShowFanCurveWarningDialog())
            {
                return false;
            }
            _settings.FanCurveWarningAccepted = true;
            _fanCurveWarningShownThisRun = true;
        }

        _settings.ControlStrategy = selectedStrategy;
        CurveProfileStore.SaveSettings(_settings);
        ApplyStrategyVisibility();
        UpdateTrayMenu();
        return true;
    }

    private void ShowStopFirstWarning()
    {
        MessageBox.Show(this, T("StopFanControlFirst"), T("Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool ShowFanCurveWarningDialog()
    {
        var dialog = new Window
        {
            Title = T("FanCurveWarningTitle"),
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = T("FanCurveWarning"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var continueButton = new Button { Content = T("Continue"), MinWidth = 86, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new Button { Content = T("Cancel"), MinWidth = 86, IsCancel = true };
        continueButton.Click += (_, _) => { dialog.DialogResult = true; };
        cancelButton.Click += (_, _) => { dialog.DialogResult = false; };
        buttons.Children.Add(continueButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private void ResetFanTargetState()
    {
        _lastTarget = null;
        _lastAppliedTarget = null;
        _queuedTarget = null;
        _fanRateLimiter.Reset();
        _advancedCurvePointIndex = 0;
    }

    private void UpdateTemperatureUi(TemperatureSnapshot temps)
    {
        _cpuTempText.Text = FormatTemp(temps.CpuTempC);
        _cpuPowerText.Text = FormatPower(temps.CpuPowerW);
        _gpuTempText.Text = FormatTemp(temps.GpuTempC);
        _gpuPowerText.Text = FormatPower(temps.GpuPowerW);
        _vramTempText.Text = FormatTemp(temps.VramTempC);
        _lastCpuText = _cpuTempText.Text;
        _lastGpuText = _gpuTempText.Text;
        _lastVramText = _vramTempText.Text;
        UpdateTrayMenuMetrics();
    }

    private string FormatTarget(FanTargets? target)
    {
        if (target is null)
            return "--";

        if (target.Fan1Rpm == 0 && target.Fan2Rpm == 0)
            return T("FirmwareAuto");

        return $"F1 {target.Fan1Rpm} / F2 {target.Fan2Rpm} RPM";
    }

    private string FormatGameState()
    {
        if (_gamesRunning)
            return T("On");
        if (_effectiveGameMode)
            return T("Holding");
        return T("Off");
    }

    private static string ModeKey(ItsMode mode)
    {
        return mode switch
        {
            ItsMode.PowerSaving => "PowerSaving",
            ItsMode.Intelligent => "Intelligent",
            ItsMode.Performance => "Performance",
            ItsMode.Geek => "Geek",
            _ => "Unknown"
        };
    }

    private ItsMode SelectedItsMode()
    {
        var index = _itsModeCombo.SelectedIndex;
        return index >= 0 && index < SwitchableItsModes.Length
            ? SwitchableItsModes[index]
            : ItsMode.Unknown;
    }

    private string ItsModeComboLabel(ItsMode mode)
    {
        var label = T(ModeKey(mode));
        return IsChinese ? label + "\u6a21\u5f0f" : label;
    }

    private async void OnItsModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingItsModeCombo || _switchingItsMode ||
            _itsModeCombo.SelectedIndex < 0 ||
            _itsModeCombo.SelectedIndex >= SwitchableItsModes.Length)
        {
            return;
        }

        var requestedMode = SwitchableItsModes[_itsModeCombo.SelectedIndex];
        UpdateItsModeCombo(_displayedItsMode);
        _switchingItsMode = true;
        _itsModeCombo.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                if (!_itsModeDetector.IsModeSwitchSupported())
                    return ItsModeSwitchResult.Unsupported;

                ItsModeController.SetMode(requestedMode);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (_itsModeDetector.ReadMode() == requestedMode)
                        return ItsModeSwitchResult.Confirmed;
                    Thread.Sleep(200);
                }

                return ItsModeSwitchResult.NotConfirmed;
            });

            if (result != ItsModeSwitchResult.Confirmed)
            {
                var messageKey = result == ItsModeSwitchResult.Unsupported
                    ? "ItsModeSwitchUnavailable"
                    : "ItsModeSwitchNotConfirmed";
                MessageBox.Show(this, T(messageKey), T("CurrentMode"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(CultureInfo.CurrentCulture, T("ItsModeSwitchFailedFormat"), ex.Message),
                T("CurrentMode"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _switchingItsMode = false;
            await RefreshItsModeAsync();
        }
    }

    private async Task SetFullSpeedWithLockAsync(bool enabled)
    {
        await _fanIoLock.WaitAsync();
        try
        {
            if (enabled)
            {
                _fullSpeedUsesAlternativeMethod =
                    _settings.UseAlternativeFullSpeedMethod;
                if (_fullSpeedUsesAlternativeMethod)
                    await WriteAlternativeFullSpeedAsync();
                else
                    await Task.Run(() => ExecuteFanHardwareOperation(() =>
                    {
                        if (!SystemShutdownPending)
                            _fanController.SetFullSpeed(true);
                    }));
            }
            else if (_fullSpeedUsesAlternativeMethod)
            {
                await Task.Run(() => ExecuteFanHardwareOperation(
                    _fanController.RestoreAuto));
                _fullSpeedUsesAlternativeMethod = false;
            }
            else
            {
                await Task.Run(() => ExecuteFanHardwareOperation(
                    () => _fanController.SetFullSpeed(false)));
            }
        }
        finally
        {
            _fanIoLock.Release();
        }
    }

    private async Task WriteAlternativeFullSpeedWithLockAsync()
    {
        await _fanIoLock.WaitAsync();
        try { await WriteAlternativeFullSpeedAsync(); }
        finally { _fanIoLock.Release(); }
    }

    private async Task WriteAlternativeFullSpeedAsync()
    {
        var limits = _settings.FanRpmLimits;
        await Task.Run(() => ExecuteFanHardwareOperation(() =>
        {
            if (SystemShutdownPending)
                return;
            _fanController.Apply(
                limits.Fan1MaximumRpm,
                limits.Fan2MaximumRpm);
        }));
        _lastFanWriteTime = DateTimeOffset.Now;
    }

    private bool ShouldContinuouslyRewriteFanTarget()
    {
        if (!_settings.ContinuouslyWriteFanTargets &&
            !_fullSpeedUsesAlternativeMethod)
            return false;
        var interval = ResolveContinuousFanWriteInterval(
            GetSettingsRefreshInterval(),
            EffectiveFanWriteMinimumInterval);
        return _lastFanWriteTime is not DateTimeOffset last ||
               DateTimeOffset.Now - last >= interval;
    }

    internal static TimeSpan ResolveContinuousFanWriteInterval(
        TimeSpan statusRefreshInterval,
        TimeSpan fanWriteInterval) =>
        statusRefreshInterval >= fanWriteInterval
            ? statusRefreshInterval
            : fanWriteInterval;

    private async Task RefreshItsModeAsync()
    {
        if (_refreshingItsMode)
            return;

        _refreshingItsMode = true;
        try
        {
            var state = await Task.Run(() => (
                Supported: _itsModeDetector.IsModeSwitchSupported(),
                Mode: _itsModeDetector.ReadMode()));

            _displayedItsMode = state.Mode;
            UpdateItsModeCombo(state.Mode);
            _itsModeCombo.IsEnabled = state.Supported && !_switchingItsMode;
            _itsModeCombo.ToolTip = state.Supported ? null : T("ItsModeSwitchUnavailable");
        }
        catch
        {
            _displayedItsMode = ItsMode.Unknown;
            UpdateItsModeCombo(ItsMode.Unknown);
            _itsModeCombo.IsEnabled = false;
            _itsModeCombo.ToolTip = T("ItsModeSwitchUnavailable");
        }
        finally
        {
            _refreshingItsMode = false;
        }
    }

    private void UpdateItsModeCombo(ItsMode mode)
    {
        var selectedIndex = Array.IndexOf(SwitchableItsModes, mode);
        if (_itsModeCombo.SelectedIndex == selectedIndex)
            return;

        _updatingItsModeCombo = true;
        try
        {
            _itsModeCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _updatingItsModeCombo = false;
        }
    }

    private async void OnGpuModeSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingGpuModeCombo ||
            _switchingGpuMode ||
            _gpuModeCombo.SelectedIndex < 0 ||
            _gpuModeCombo.SelectedIndex >= _gpuModes.Count)
        {
            return;
        }

        var target = _gpuModes[_gpuModeCombo.SelectedIndex];
        UpdateGpuModeCombo(_displayedGpuMode);

        _switchingGpuMode = true;
        _gpuModeCombo.IsEnabled = false;
        var switched = false;
        var requiresRestart = false;
        var alreadyPending = false;
        try
        {
            var hasCurrentBootTarget =
                GpuModeRestartState.TryGetCurrentBootTarget(
                    _settings,
                    _bootSessionId,
                    out var pendingTarget);
            var hasCurrentBootTransition =
                GpuModeRestartState.TryGetCurrentBootTransition(
                    _settings,
                    _bootSessionId,
                    out var transition);
            if (hasCurrentBootTarget && target == pendingTarget)
            {
                requiresRestart = true;
                alreadyPending = true;
            }
            else
            {
                var state = hasCurrentBootTransition
                    ? null
                    : await Task.Run(GpuModeController.ReadState);
                var source = hasCurrentBootTransition
                    ? transition.Source
                    : state!.CurrentMode;
                var sourceUsesDirectGraphicsConfiguration =
                    hasCurrentBootTransition
                        ? transition.SourceUsesDirectGraphicsConfiguration
                        : state!.UsesDirectGraphicsConfiguration;
                requiresRestart = await Task.Run(() =>
                    GpuModeController.SetModeFromEffectiveState(
                        source,
                        sourceUsesDirectGraphicsConfiguration,
                        target));
                if (requiresRestart)
                {
                    SavePendingGpuMode(
                        source,
                        sourceUsesDirectGraphicsConfiguration,
                        target);
                }
                else
                {
                    ClearPendingGpuMode();
                }
            }
            _displayedGpuMode = target;
            UpdateGpuModeCombo(target);
            switched = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("GpuModeSwitchFailedFormat"),
                    ex.Message),
                T("GpuWorkingMode"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _switchingGpuMode = false;
            await RefreshGpuModeAsync();
        }

        if (switched &&
            requiresRestart &&
            !alreadyPending &&
            ShowGpuRestartPrompt(target))
        {
            try
            {
                RestartComputer();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T("RestartFailedFormat"),
                        ex.Message),
                    T("GpuRestartRequiredTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private async Task RefreshGpuModeAsync()
    {
        if (_refreshingGpuMode)
            return;

        _refreshingGpuMode = true;
        try
        {
            var state = await Task.Run(GpuModeController.ReadState);
            state = await ApplyStartupPendingGpuModeAsync(state);
            UpdateGpuModeItems(state.SupportedModes);
            var displayedMode = _pendingGpuMode ?? state.CurrentMode;
            _displayedGpuMode = displayedMode;
            UpdateGpuModeCombo(displayedMode);
            _gpuModeCombo.IsEnabled =
                state.SupportedModes.Count > 0 && !_switchingGpuMode;
            _gpuModeCombo.ToolTip = null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("GPU working-mode refresh failed.", ex);
            if (_pendingGpuMode is { } pending &&
                _gpuModes.Contains(pending))
            {
                _displayedGpuMode = pending;
                UpdateGpuModeCombo(pending);
            }
            else
            {
                _displayedGpuMode = null;
                UpdateGpuModeItems([]);
                UpdateGpuModeCombo(null);
            }
            _gpuModeCombo.IsEnabled = false;
            _gpuModeCombo.ToolTip = string.Format(
                CultureInfo.CurrentCulture,
                T("GpuModeUnavailableFormat"),
                ex.Message);
        }
        finally
        {
            _refreshingGpuMode = false;
        }
    }

    private async Task<GpuModeState> ApplyStartupPendingGpuModeAsync(
        GpuModeState state)
    {
        if (!_pendingGpuModeLoadedAtStartup ||
            _pendingGpuModeApplyAttempted ||
            _pendingGpuMode is not { } pending ||
            !GpuModeRestartState.HasRestartedSince(
                _settings.PendingGpuModeBootSessionId,
                _bootSessionId))
        {
            return state;
        }

        _pendingGpuModeApplyAttempted = true;
        try
        {
            if (state.CurrentMode == pending)
            {
                ClearPendingGpuMode();
                return state;
            }

            if (GpuModeController.IsHybridMode(pending) &&
                !state.UsesDirectGraphicsConfiguration)
            {
                var requiresAnotherRestart = await Task.Run(
                    () => GpuModeController.SetMode(pending));
                if (requiresAnotherRestart)
                {
                    SavePendingGpuMode(
                        state.CurrentMode,
                        state.UsesDirectGraphicsConfiguration,
                        pending);
                    return state;
                }

                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                do
                {
                    await Task.Delay(500);
                    state = await Task.Run(GpuModeController.ReadState);
                    if (state.CurrentMode == pending)
                        break;
                } while (DateTimeOffset.UtcNow < deadline);
            }

            if (state.CurrentMode == pending)
                ClearPendingGpuMode();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Pending GPU working mode could not be applied.", ex);
            MessageBox.Show(
                this,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("GpuModeSwitchFailedFormat"),
                    ex.Message),
                T("GpuWorkingMode"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return state;
    }

    private void SavePendingGpuMode(
        GpuWorkingMode source,
        bool sourceUsesDirectGraphicsConfiguration,
        GpuWorkingMode target)
    {
        GpuModeRestartState.MarkPending(
            _settings,
            source,
            sourceUsesDirectGraphicsConfiguration,
            target,
            _bootSessionId);
        CurveProfileStore.SaveSettings(_settings);
        _pendingGpuMode = target;
        _pendingGpuModeLoadedAtStartup = false;
        _pendingGpuModeApplyAttempted = true;
    }

    private void ClearPendingGpuMode()
    {
        if (!string.IsNullOrEmpty(_settings.PendingGpuMode) ||
            !string.IsNullOrEmpty(_settings.PendingGpuModeSource) ||
            _settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration.HasValue ||
            !string.IsNullOrEmpty(
                _settings.PendingGpuModeBootSessionId))
        {
            GpuModeRestartState.Clear(_settings);
            CurveProfileStore.SaveSettings(_settings);
        }

        _pendingGpuMode = null;
        _pendingGpuModeLoadedAtStartup = false;
        _pendingGpuModeApplyAttempted = false;
    }

    private static GpuWorkingMode? ParsePendingGpuMode(string value) =>
        GpuModeRestartState.TryParsePendingMode(value, out var mode)
            ? mode
            : null;

    private void UpdateGpuModeItems(
        IReadOnlyList<GpuWorkingMode> modes,
        bool force = false)
    {
        if (!force &&
            _gpuModes.SequenceEqual(modes) &&
            _gpuModeCombo.Items.Count == modes.Count)
        {
            return;
        }

        var newModes = modes.ToArray();
        _updatingGpuModeCombo = true;
        try
        {
            _gpuModes.Clear();
            _gpuModes.AddRange(newModes);
            _gpuModeCombo.Items.Clear();
            foreach (var mode in newModes)
                _gpuModeCombo.Items.Add(T(GpuModeKey(mode)));
            ResizeGpuModeCombo();
        }
        finally
        {
            _updatingGpuModeCombo = false;
        }
    }

    private void UpdateGpuModeCombo(GpuWorkingMode? mode)
    {
        var selectedIndex = mode.HasValue
            ? _gpuModes.IndexOf(mode.Value)
            : -1;
        if (_gpuModeCombo.SelectedIndex == selectedIndex)
            return;

        _updatingGpuModeCombo = true;
        try
        {
            _gpuModeCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _updatingGpuModeCombo = false;
        }
    }

    private static string GpuModeKey(GpuWorkingMode mode) =>
        mode switch
        {
            GpuWorkingMode.Hybrid => "GpuModeHybrid",
            GpuWorkingMode.IntegratedOnly => "GpuModeIntegratedOnly",
            GpuWorkingMode.HybridAuto => "GpuModeHybridAuto",
            GpuWorkingMode.Discrete => "GpuModeDiscrete",
            GpuWorkingMode.IntegratedDirect => "GpuModeIntegratedDirect",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

    private void ResizeGpuModeCombo()
    {
        var labels = _gpuModeCombo.Items
            .Cast<object>()
            .Select(item => Convert.ToString(
                item,
                CultureInfo.CurrentCulture) ?? string.Empty)
            .ToArray();
        if (labels.Length == 0)
            return;

        var typeface = new Typeface(
            _gpuModeCombo.FontFamily,
            _gpuModeCombo.FontStyle,
            _gpuModeCombo.FontWeight,
            _gpuModeCombo.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textWidth = labels.Max(label => new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            _gpuModeCombo.FontSize,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace);
        _gpuModeCombo.Width = Math.Ceiling(textWidth + 38);
    }

    private bool ShowGpuRestartPrompt(GpuWorkingMode target)
    {
        var dialog = new Window
        {
            Title = T("GpuRestartRequiredTitle"),
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
            FontSize = FontSize
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(
                CultureInfo.CurrentCulture,
                T("GpuRestartRequiredMessage"),
                T(GpuModeKey(target))),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var restartNowButton = new Button
        {
            Content = T("RestartNow"),
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var restartLaterButton = new Button
        {
            Content = T("RestartLater"),
            MinWidth = 96,
            IsCancel = true
        };
        restartNowButton.Click += (_, _) => dialog.DialogResult = true;
        restartLaterButton.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(restartNowButton);
        buttons.Children.Add(restartLaterButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private static void RestartComputer()
    {
        var shutdown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "shutdown.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = shutdown,
            Arguments = "/r /t 0",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private void SetResumeFanControlOnNextStart(bool value)
    {
        if (_settings.ResumeFanControlOnNextStart == value)
            return;

        _settings.ResumeFanControlOnNextStart = value;
        CurveProfileStore.SaveSettings(_settings);
    }

    private TemperatureSnapshot ReadTemperatures()
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.Read();
    }

    internal static double? SmoothTemperature(double? previous, double? current, double smoothingSamples)
    {
        if (current is null)
            return null;
        if (previous is null)
            return current;

        var samples = Math.Max(1, Math.Min(10, smoothingSamples));
        var alpha = 2.0 / (samples + 1.0);
        return previous.Value + (current.Value - previous.Value) * alpha;
    }

    private void UpdateHeatSoak(TemperatureSnapshot temps)
    {
        var hottest = new[] { temps.CpuTempC, temps.GpuTempC }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();
        if (double.IsNaN(hottest))
            return;

        var now = DateTimeOffset.Now;
        if (hottest >= HeatSoakEnterTempC)
        {
            _highTempSince ??= now;
            _heatSoakExitSamples.Clear();
            if (now - _highTempSince.Value >= HeatSoakDuration)
                _heatSoaked = true;
            return;
        }

        if (hottest < HeatSoakExitTempC)
        {
            TrackHeatSoakExitSample(now, hottest);
            if (CanExitHeatSoak())
            {
                _highTempSince = null;
                _heatSoaked = false;
                _heatSoakExitSamples.Clear();
            }
            return;
        }

        _heatSoakExitSamples.Clear();
        if (!_heatSoaked)
            _highTempSince = null;
    }

    private void TrackHeatSoakExitSample(DateTimeOffset now, double hottest)
    {
        _heatSoakExitSamples.Enqueue((now, hottest));
        while (_heatSoakExitSamples.Count > 0 &&
               now - _heatSoakExitSamples.Peek().Timestamp > HeatSoakExitAverageDuration)
        {
            _heatSoakExitSamples.Dequeue();
        }
    }

    private bool CanExitHeatSoak()
    {
        if (!_heatSoaked || _heatSoakExitSamples.Count < 2)
            return false;

        var span = _heatSoakExitSamples.Last().Timestamp - _heatSoakExitSamples.Peek().Timestamp;
        if (span < HeatSoakExitAverageDuration)
            return false;

        return _heatSoakExitSamples.Average(sample => sample.TempC) < HeatSoakExitTempC;
    }

    private static string FormatTemp(double? value)
    {
        return value is null ? "-- \u00B0C" : $"{value:F1} \u00B0C";
    }

    private static string FormatPower(double? value)
    {
        return value is null ? "-- W" : $"{value:F1} W";
    }

    private static ComboBox OptionCombo(params string[] options)
    {
        var comboBox = new ComboBox
        {
            Width = 62,
            IsEditable = false,
            Margin = new Thickness(0, 0, 8, 0)
        };
        foreach (var option in options)
            comboBox.Items.Add(option);
        comboBox.SelectedIndex = 0;
        return comboBox;
    }

    private void HookSettingsControls()
    {
        _languageCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.Language = _languageCombo.SelectedIndex == 1 ? "en-US" : "zh-CN";
            CurveProfileStore.SaveSettings(_settings);
            ApplyLanguage();
        };

        _themeCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.Theme = _themeCombo.SelectedIndex == 1 ? "dark" : "light";
            CurveProfileStore.SaveSettings(_settings);
            ApplyTheme();
        };

        _intervalCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.IntervalSeconds = SelectedNumber(_intervalCombo, 2.0);
            CurveProfileStore.SaveSettings(_settings);
            SyncTimerIntervals();
        };

        _gameExitHoldCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.GameExitHoldSeconds = SelectedNumber(_gameExitHoldCombo, 20);
            CurveProfileStore.SaveSettings(_settings);
        };

        _editFanCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.EditFan = _editFanCombo.SelectedIndex == 1 ? 2 : 1;
            CurveProfileStore.SaveSettings(_settings);
            ApplyCurveEditSettings();
        };

        _syncFanSpeedsCheck.Checked += (_, _) => UpdateBooleanSetting("syncFanSpeeds", true);
        _syncFanSpeedsCheck.Unchecked += (_, _) => UpdateBooleanSetting("syncFanSpeeds", false);
        _fixedSyncFanSpeedsCheck.Checked += (_, _) => UpdateBooleanSetting("fixedSyncFanSpeeds", true);
        _fixedSyncFanSpeedsCheck.Unchecked += (_, _) => UpdateBooleanSetting("fixedSyncFanSpeeds", false);
        _autoDetectGamesCheck.Checked += (_, _) => UpdateBooleanSetting("autoDetectGames", true);
        _autoDetectGamesCheck.Unchecked += (_, _) => UpdateBooleanSetting("autoDetectGames", false);
        _fixedModeCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings || _updatingFixedModeCombo || _fixedModeCombo.SelectedIndex < 0)
                return;
            SetManualFixedMode(_fixedModeCombo.SelectedIndex == 1);
        };
        _fixedModeHotkeyButton.Click += (_, _) => BeginFixedModeHotkeyCapture();
        _startupCheck.Checked += (_, _) => UpdateBooleanSetting("startup", true);
        _startupCheck.Unchecked += (_, _) => UpdateBooleanSetting("startup", false);
        _startToTrayCheck.Checked += (_, _) => UpdateBooleanSetting("startToTray", true);
        _startToTrayCheck.Unchecked += (_, _) => UpdateBooleanSetting("startToTray", false);
        _minimizeToTrayCheck.Checked += (_, _) => UpdateBooleanSetting("minimizeToTray", true);
        _minimizeToTrayCheck.Unchecked += (_, _) => UpdateBooleanSetting("minimizeToTray", false);
        _closeToTrayCheck.Checked += (_, _) => UpdateBooleanSetting("closeToTray", true);
        _closeToTrayCheck.Unchecked += (_, _) => UpdateBooleanSetting("closeToTray", false);
        _disableControlOnSleepCheck.Checked += (_, _) => UpdateBooleanSetting("disableControlOnSleep", true);
        _disableControlOnSleepCheck.Unchecked += (_, _) => UpdateBooleanSetting("disableControlOnSleep", false);
    }

    private void LoadSettingsControls()
    {
        _loadingSettings = true;
        SelectComboValue(_intervalCombo, _settings.IntervalSeconds);
        if (_strategyTabs is not null)
            _strategyTabs.SelectedIndex = _settings.ControlStrategy == ControlStrategy.FixedRpm ? 0 : 1;
        SelectComboValue(_gameExitHoldCombo, _settings.GameExitHoldSeconds);
        _editFanCombo.SelectedIndex = _settings.EditFan == 2 ? 1 : 0;
        _syncFanSpeedsCheck.IsChecked = _settings.SyncFanSpeeds;
        _fixedSyncFanSpeedsCheck.IsChecked = _settings.FixedSyncFanSpeeds;
        _autoDetectGamesCheck.IsChecked = _settings.AutoDetectGames;
        _effectiveGameMode = _settings.FixedGameModeOverride == FixedGameModeOverride.GameUntilGamesEnd;
        UpdateFixedModeCombo();
        UpdateFixedModeHotkeyButton();
        _languageCombo.SelectedIndex = _settings.Language == "en-US" ? 1 : 0;
        _themeCombo.SelectedIndex = _settings.Theme == "dark" ? 1 : 0;
        _startupCheck.IsChecked = _settings.StartWithWindows;
        _startToTrayCheck.IsChecked = _settings.StartToTray;
        _minimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        _closeToTrayCheck.IsChecked = _settings.CloseToTray;
        _disableControlOnSleepCheck.IsChecked =
            _fanController.SupportsDisableControlOnSleep
                ? _settings.DisableControlOnSleep
                : _settings.AttemptDisableControlOnSleepWhenUnsupported;
        RefreshFixedRpmBoxes();
        _loadingSettings = false;
        ApplyStrategyVisibility();
    }

    private void UpdateBooleanSetting(string setting, bool value)
    {
        if (_loadingSettings)
            return;

        switch (setting)
        {
            case "startup":
                _settings.StartWithWindows = value;
                _settings.DelayStartup = false;
                ApplyStartupSetting();
                break;
            case "startToTray":
                _settings.StartToTray = value;
                ApplyStartupSetting();
                break;
            case "minimizeToTray":
                _settings.MinimizeToTray = value;
                break;
            case "closeToTray":
                _settings.CloseToTray = value;
                break;
            case "disableControlOnSleep":
                if (_fanController.SupportsDisableControlOnSleep)
                    _settings.DisableControlOnSleep = value;
                else
                    _settings.AttemptDisableControlOnSleepWhenUnsupported = value;
                break;
            case "syncFanSpeeds":
                _settings.SyncFanSpeeds = value;
                ApplyCurveEditSettings();
                break;
            case "fixedSyncFanSpeeds":
                _settings.FixedSyncFanSpeeds = value;
                if (value)
                {
                    SaveFixedRpmSettingsFromUi();
                }
                break;
            case "autoDetectGames":
                _settings.AutoDetectGames = value;
                if (value)
                {
                    _settings.FixedGameModeOverride = FixedGameModeOverride.None;
                    _settings.ManualGameMode = false;
                    _overrideGameSeenSinceArmed = false;
                    _overrideNormalSawNoGamesSinceArmed = false;
                    _hasConfirmedFixedState = false;
                    _hasPendingFixedState = false;
                }
                else
                {
                    SetManualFixedMode(_effectiveGameMode);
                    return;
                }
                break;
        }
        CurveProfileStore.SaveSettings(_settings);
        ApplyLanguage();
        UpdateTrayMenu();
    }

    private void ApplyCurveEditSettings()
    {
        _cpuChart.SetEditFan(_settings.EditFan);
        _gpuChart.SetEditFan(_settings.EditFan);
        _cpuChart.SetSyncFanSpeeds(_settings.SyncFanSpeeds);
        _gpuChart.SetSyncFanSpeeds(_settings.SyncFanSpeeds);
    }

    private void SyncTimerIntervals()
    {
        var interval = GetSettingsRefreshInterval();
        _timer.Interval = interval;
        _trayMenuTimer.Interval = interval;
    }

    private TimeSpan GetSettingsRefreshInterval() =>
        TimeSpan.FromSeconds(Math.Max(0.5, _settings.IntervalSeconds));

    private TimeSpan EffectiveFanReadMinimumInterval =>
        ResolveFanIoMinimumInterval(
            _settings.FanReadMinimumIntervalSeconds,
            _fanController.MinimumReadInterval);

    private TimeSpan EffectiveFanWriteMinimumInterval =>
        ResolveFanIoMinimumInterval(
            _settings.FanWriteMinimumIntervalSeconds,
            _fanController.MinimumWriteInterval);

    private bool DisableControlOnSleepRequested =>
        _fanController.SupportsDisableControlOnSleep
            ? _settings.DisableControlOnSleep
            : _settings.AttemptDisableControlOnSleepWhenUnsupported;

    internal static TimeSpan ResolveFanIoMinimumInterval(
        double? overrideSeconds,
        TimeSpan backendDefault)
    {
        if (!CurveProfileStore.IsValidFanIoIntervalOverride(overrideSeconds))
            return backendDefault;
        return overrideSeconds is double seconds
            ? TimeSpan.FromSeconds(seconds)
            : backendDefault;
    }

    internal static bool CanAttemptDisableControlOnSleep(
        bool backendSupportsDisableControlOnSleep,
        TimeSpan readInterval,
        TimeSpan writeInterval) =>
        backendSupportsDisableControlOnSleep ||
        readInterval <= TimeSpan.FromSeconds(1) &&
        writeInterval <= TimeSpan.FromSeconds(1);

    private bool IsChinese => _settings.Language != "en-US";
    private bool IsDark => _settings.Theme == "dark";

    private string T(string key) => Translate(key, IsChinese);

    internal static string Translate(string key, bool isChinese)
    {
        return isChinese ? key switch
        {
            "AppTitle" => "ThinkBook Toolkit - \u6027\u80fd\u4e0e\u6563\u70ed",
            "Profile" => "\u65b9\u6848",
            "Name" => "\u540d\u79f0",
            "Strategy" => "\u63a7\u5236\u65b9\u5f0f",
            "FixedRpm" => "\u56fa\u5b9a\u8f6c\u901f",
            "Interval" => "\u5237\u65b0\u95f4\u9694",
            "TempSmoothing" => "\u6e29\u5ea6\u5e73\u6ed1",
            "RampDown" => "\u964d\u901f\u9650\u5236",
            "GameExitHold" => "\u6e38\u620f\u9000\u51fa\u5ef6\u65f6",
            "FixedMode" => "\u6a21\u5f0f",
            "FixedModeHotkey" => "\u5feb\u6377\u952e",
            "FixedSyncFanSpeeds" => "\u540c\u6b65\u56fa\u5b9a\u8f6c\u901f",
            "AutoDetectGames" => "\u81ea\u52a8\u68c0\u6d4b\u6e38\u620f",
            "EditFan" => "\u7f16\u8f91",
            "SyncFanSpeeds" => "\u540c\u6b65\u8f6c\u901f",
            "Language" => "\u8bed\u8a00",
            "Theme" => "\u4e3b\u9898",
            "Light" => "\u6d45\u8272",
            "Dark" => "\u6df1\u8272",
            "Fan1" => "\u98ce\u6247 1",
            "Fan2" => "\u98ce\u6247 2",
            "Target" => "\u76ee\u6807",
            "CpuCurve" => "CPU \u66f2\u7ebf",
            "GpuCurve" => "GPU \u66f2\u7ebf",
            "TemperatureAxis" => "\u6e29\u5ea6 (\u00B0C)",
            "Save" => "\u4fdd\u5b58",
            "Refresh" => "\u5237\u65b0",
            "PowerSettings" => "\u529f\u8017\u8bbe\u7f6e",
            "BatterySettings" => "\u7535\u6c60\u8bbe\u7f6e",
            "BatteryInformation" => "\u7535\u6c60\u4fe1\u606f",
            "DisplaySettings" => "\u663e\u793a\u8bbe\u7f6e",
            "SoundSettings" => "\u58f0\u97f3\u8bbe\u7f6e",
            "OtherSettings" => "\u5176\u5b83\u8bbe\u7f6e",
            "DeviceInformation" => "\u8bbe\u5907\u8be6\u7ec6\u4fe1\u606f",
            "OpenDeviceInformation" => "\u6253\u5f00\u8bbe\u5907\u8be6\u7ec6\u4fe1\u606f",
            "ReadingDeviceInformation" => "\u6b63\u5728\u8bfb\u53d6\u8bbe\u5907\u4fe1\u606f\u2026",
            "DeviceInformationReadFailedFormat" => "\u8bbe\u5907\u4fe1\u606f\u8bfb\u53d6\u5931\u8d25\uff1a{0}",
            "Device" => "\u8bbe\u5907",
            "WindowsVersion" => "Windows \u7248\u672c",
            "DeviceName" => "\u8bbe\u5907\u540d\u79f0",
            "DeviceModel" => "\u8bbe\u5907\u578b\u53f7",
            "ProductNumber" => "\u4ea7\u54c1\u7f16\u53f7",
            "DeviceCode" => "\u8bbe\u5907\u4ee3\u53f7",
            "SerialNumber" => "\u5e8f\u5217\u53f7",
            "BiosVersion" => "BIOS \u7248\u672c",
            "SmbiosVersion" => "SMBIOS \u7248\u672c",
            "MeVersion" => "ME \u7248\u672c",
            "AmdPspVersion" => "AMD PSP \u7248\u672c",
            "AcpiVersion" => "ACPI \u7248\u672c",
            "UefiVersion" => "UEFI \u7248\u672c",
            "DeviceId" => "\u8bbe\u5907 ID",
            "ProductId" => "\u4ea7\u54c1 ID",
            "Copy" => "\u590d\u5236",
            "ShowSerialNumber" => "\u663e\u793a\u5e8f\u5217\u53f7",
            "HideSerialNumber" => "\u9690\u85cf\u5e8f\u5217\u53f7",
            "CpuTopologyFormat" => "{0} \u6838\u5fc3 / {1} \u7ebf\u7a0b",
            "VideoMemoryFormat" => "\u663e\u5b58\uff1a{0}",
            "SharedGraphicsMemory" => "\u4f7f\u7528\u5171\u4eab\u7cfb\u7edf\u5185\u5b58",
            "Memory" => "\u5185\u5b58",
            "InstalledMemoryFormat" => "\u5df2\u5b89\u88c5 {0}\uff08\u53ef\u7528 {1}\uff09",
            "Storage" => "\u5b58\u50a8\u5668",
            "UsedTotalFormat" => "\u5df2\u7528 {0} / \u603b\u8ba1 {1}",
            "TotalStorageFormat" => "\u5206\u533a\u603b\u5bb9\u91cf\uff1a{0}",
            "Motherboard" => "\u4e3b\u677f",
            "Displays" => "\u663e\u793a\u5668",
            "Display" => "\u663e\u793a",
            "WarrantyStartDate" => "\u5f00\u59cb\u65e5\u671f",
            "WarrantyEndDate" => "\u7ed3\u675f\u65e5\u671f",
            "WarrantyProgress" => "\u5df2\u7528\u4fdd\u4fee\u671f",
            "WarrantyInformation" => "\u4fdd\u4fee\u4fe1\u606f",
            "WarrantyLoading" => "\u67e5\u8be2\u4e2d",
            "WarrantyInCoverage" => "\u5728\u4fdd",
            "WarrantyExpired" => "\u8fc7\u4fdd",
            "WarrantyNotStarted" => "\u672a\u5f00\u59cb",
            "WarrantyUnavailable" => "\u6682\u65e0\u4fe1\u606f",
            "NoInformation" => "\u6682\u65e0\u4fe1\u606f",
            "WarrantyCached" => "\u8054\u7f51\u67e5\u8be2\u5931\u8d25\uff0c\u6b63\u5728\u663e\u793a\u4e0a\u6b21\u7f13\u5b58\u7684\u4fdd\u4fee\u4fe1\u606f\u3002",
            "WarrantyQueryFailedFormat" => "\u4fdd\u4fee\u4fe1\u606f\u67e5\u8be2\u5931\u8d25\uff1a{0}",
            "WarrantyDateFormat" => "yyyy\u5e74MM\u6708dd\u65e5",
            "AdvancedToolkit" => "\u9ad8\u7ea7\u8bbe\u7f6e",
            "BootLogoCustomization" => "\u66f4\u6362\u5f00\u673a Logo",
            "BootLogoCustomizationTitle" => "\u66f4\u6362\u5f00\u673a Logo",
            "BootLogoResolutionFormat" => "\u652f\u6301\u7684\u6700\u5927\u5206\u8fa8\u7387\uff1a {0} \u00d7 {1}",
            "BootLogoFormatsFormat" => "\u652f\u6301\u7684\u56fe\u50cf\u683c\u5f0f\uff1a {0}",
            "BootLogoPreviewDisclaimer" => "\u9884\u89c8\u7684\u753b\u9762\u4e0e\u5b9e\u9645\u5f00\u673a\u753b\u9762\u4e0d\u4e00\u5b9a\u76f8\u540c\u3002",
            "ShowWindowsLoadingIcon" => "\u663e\u793a\u52a0\u8f7d\u56fe\u6807",
            "Customize" => "\u81ea\u5b9a\u4e49",
            "ResetToDefault" => "\u91cd\u7f6e\u4e3a\u9ed8\u8ba4\u8bbe\u7f6e",
            "Confirm" => "\u786e\u8ba4",
            "Attention" => "\u6ce8\u610f",
            "BiosSetup" => "BIOS \u8bbe\u7f6e",
            "StartupInterrupt" => "\u542f\u52a8\u4e2d\u65ad",
            "SecureWipe" => "\u5b89\u5168\u64e6\u9664",
            "AdvancedToolkitUnavailableFormat" => "\u9ad8\u7ea7\u8bbe\u7f6e\u4e0d\u53ef\u7528\uff1a{0}",
            "AdvancedToolkitFailedFormat" => "\u64cd\u4f5c\u5931\u8d25\uff1a{0}",
            "BootLogoApplyConfirmFirst" => "\u5373\u5c06\u5e94\u7528\u5f53\u524d\u7684\u5f00\u673a\u753b\u9762\u8bbe\u7f6e\uff0c\u662f\u5426\u7ee7\u7eed\uff1f",
            "BootLogoApplyConfirmSecond" => "\u8fd9\u662f\u7b2c\u4e8c\u6b21\u786e\u8ba4\u3002\u7a0b\u5e8f\u5c06\u4fee\u6539 EFI \u7cfb\u7edf\u5206\u533a\u548c\u542f\u52a8\u914d\u7f6e\uff0c\u662f\u5426\u7acb\u5373\u5e94\u7528\uff1f",
            "BootLogoResetConfirmFirst" => "\u5373\u5c06\u6062\u590d Lenovo \u9ed8\u8ba4\u5f00\u673a\u753b\u9762\uff0c\u662f\u5426\u7ee7\u7eed\uff1f",
            "BootLogoResetConfirmSecond" => "\u8fd9\u662f\u7b2c\u4e8c\u6b21\u786e\u8ba4\u3002\u7a0b\u5e8f\u5c06\u5220\u9664 EFI \u5206\u533a\u4e2d\u7684\u81ea\u5b9a\u4e49\u5f00\u673a\u753b\u9762\uff0c\u662f\u5426\u7ee7\u7eed\uff1f",
            "BootLogoSuccess" => "\u5f00\u673a\u753b\u9762\u8bbe\u7f6e\u5df2\u5e94\u7528\uff0c\u5c06\u5728\u4e0b\u6b21\u542f\u52a8\u65f6\u751f\u6548\u3002",
            "BiosSetupConfirmFirst" => "\u7cfb\u7edf\u5c06\u8bbe\u7f6e\u4e0b\u6b21\u542f\u52a8\u76f4\u63a5\u8fdb\u5165 BIOS Setup Utility\u3002\u662f\u5426\u7ee7\u7eed\uff1f",
            "BiosSetupConfirmSecond" => "\u7cfb\u7edf\u5c06\u91cd\u65b0\u542f\u52a8\u5e76\u5f15\u5bfc\u8fdb\u5165 BIOS Setup Utility\u3002\u60a8\u53ef\u4ee5\u901a\u8fc7\u6b64\u529f\u80fd\u66f4\u6539\u9ad8\u7ea7\u8bbe\u7f6e\uff0c\u4f8b\u5982\u7981\u7528\u6216\u542f\u7528\u5b89\u5168\u5f15\u5bfc\u3001\u66f4\u6539\u5f15\u5bfc\u987a\u5e8f\u53ca\u5176\u4ed6\u8bbe\u7f6e\u3002\n\u662f\u5426\u91cd\u65b0\u542f\u52a8\u7cfb\u7edf\u5e76\u8fdb\u5165 BIOS Setup Utility\uff1f",
            "StartupInterruptConfirmFirst" => "\u7cfb\u7edf\u5c06\u8bbe\u7f6e\u4e0b\u6b21\u542f\u52a8\u8fdb\u5165 Lenovo \u542f\u52a8\u4e2d\u65ad\u83dc\u5355\u3002\u662f\u5426\u7ee7\u7eed\uff1f",
            "StartupInterruptConfirmSecond" => "\u7cfb\u7edf\u5c06\u91cd\u65b0\u542f\u52a8\u5e76\u5f15\u5bfc\u8fdb\u5165\u201c\u542f\u52a8\u4e2d\u65ad\u201d\u83dc\u5355\u3002\u60a8\u53ef\u4ee5\u901a\u8fc7\u6b64\u529f\u80fd\uff0c\u6309\u76f8\u5e94\u6309\u952e\u6765\u8fdb\u5165 BIOS Setup Utility\u3001\u8bca\u65ad\u786c\u4ef6\u3001\u6062\u590d\u7cfb\u7edf\u548c\u8fdb\u884c\u5176\u4ed6\u9ad8\u7ea7\u914d\u7f6e\u3002\n\u662f\u5426\u8981\u91cd\u65b0\u542f\u52a8\u7cfb\u7edf\u5e76\u8fdb\u5165\u201c\u542f\u52a8\u4e2d\u65ad\u201d\u83dc\u5355\uff1f",
            "SecureWipeConfirmFirst" => "\u5b89\u5168\u64e6\u9664\u53ef\u4ee5\u6c38\u4e45\u5220\u9664\u5b58\u50a8\u8bbe\u5907\u4e0a\u7684\u6570\u636e\u3002\u662f\u5426\u7ee7\u7eed\u8fdb\u5165\u786e\u8ba4\u6d41\u7a0b\uff1f",
            "SecureWipeConfirmSecond" => "\u7cfb\u7edf\u5c06\u91cd\u65b0\u542f\u52a8\u5e76\u8fdb\u5165\u201c\u5b89\u5168\u64e6\u9664\u201d\u5e94\u7528\u7a0b\u5e8f\u3002\u60a8\u53ef\u4ee5\u901a\u8fc7\u6b64\u529f\u80fd\u9009\u62e9\u67d0\u4e2a\u5b58\u50a8\u8bbe\u5907\u5e76\u901a\u8fc7\u6240\u9009\u7684\u65b9\u5f0f\u64e6\u9664\u6574\u4e2a\u6216\u90e8\u5206\u5b58\u50a8\u3002\n\u662f\u5426\u8981\u91cd\u65b0\u542f\u52a8\u7cfb\u7edf\u5e76\u8fdb\u5165\u201c\u5b89\u5168\u64e6\u9664\u201d\u5e94\u7528\u7a0b\u5e8f\uff1f",
            "BatteryChargeMode" => "\u5145\u7535\u6a21\u5f0f",
            "BatteryChargeModeDescription" => "\u9009\u62e9\u7535\u6c60\u5145\u7535\u7b56\u7565\u3002\u517b\u62a4\u6a21\u5f0f\u9650\u5236\u5145\u7535\u91cf\uff0c\u5feb\u5145\u6a21\u5f0f\u63d0\u9ad8\u5145\u7535\u529f\u7387\u3002",
            "BatteryConservationMode" => "\u517b\u62a4",
            "BatteryNormalMode" => "\u666e\u901a",
            "BatteryRapidChargeMode" => "\u5feb\u5145",
            "OvernightBatteryCharging" => "\u9694\u591c\u7535\u6c60\u5145\u7535",
            "OvernightBatteryChargingDescription" => "\u542f\u7528\u540e\uff0c\u63d2\u7535\u8fc7\u591c\u65f6\u5148\u5145\u81f3 80%\uff0c\u65e9\u4e0a\u518d\u5145\u81f3 100%\u3002",
            "AlwaysOnUsb" => "\u4fdd\u6301 USB \u4f9b\u7535",
            "AlwaysOnUsbDescription" => "\u5728\u5173\u673a\u3001\u7761\u7720\u6216\u4f11\u7720\u65f6\u4fdd\u6301\u6307\u5b9a USB \u63a5\u53e3\u4f9b\u7535\u3002",
            "AlwaysOnUsbOff" => "\u5173\u95ed",
            "AlwaysOnUsbSleeping" => "\u4ec5\u7761\u7720\u65f6\u5f00\u542f",
            "AlwaysOnUsbAlways" => "\u4fdd\u6301\u5f00\u542f",
            "FlipToStart" => "\u5f00\u76d6\u542f\u52a8",
            "FlipToStartDescription" => "\u6253\u5f00\u76d6\u5b50\u65f6\u81ea\u52a8\u542f\u52a8\u7b14\u8bb0\u672c\u3002",
            "BatteryTemperature" => "\u7535\u6c60\u6e29\u5ea6",
            "BatteryTemperatureDescription" => "\u5f53\u524d\u7535\u6c60\u6e29\u5ea6\u3002",
            "BatteryPower" => "\u7535\u6c60\u5145\u653e\u7535\u529f\u7387",
            "BatteryPowerDescription" => "(+) \u4e3a\u5145\u7535\uff0c(-) \u4e3a\u653e\u7535\u3002",
            "BatteryMinimumPower" => "\u6700\u5c0f\u5145\u653e\u7535\u529f\u7387",
            "BatteryMinimumPowerDescription" => "\u5f53\u524d\u8fde\u7eed\u5145\u7535\u6216\u653e\u7535\u9636\u6bb5\u7684\u6700\u5c0f\u529f\u7387\u3002",
            "BatteryMaximumPower" => "\u6700\u5927\u5145\u653e\u7535\u529f\u7387",
            "BatteryMaximumPowerDescription" => "\u5f53\u524d\u8fde\u7eed\u5145\u7535\u6216\u653e\u7535\u9636\u6bb5\u7684\u6700\u5927\u529f\u7387\u3002",
            "BatteryCurrentCapacity" => "\u5f53\u524d\u5bb9\u91cf",
            "BatteryCurrentCapacityDescription" => "\u5f53\u524d\u7535\u6c60\u5bb9\u91cf\u3002",
            "BatteryFullChargeCapacity" => "\u6ee1\u7535\u5bb9\u91cf",
            "BatteryFullChargeCapacityDescription" => "\u5f53\u7535\u6c60\u5145\u6ee1\u65f6\u7684\u5bb9\u91cf\u3002",
            "BatteryDesignCapacity" => "\u8bbe\u8ba1\u5bb9\u91cf",
            "BatteryDesignCapacityDescription" => "\u7535\u6c60\u8bbe\u8ba1\u5bb9\u91cf\u3002",
            "BatteryHealth" => "\u7535\u6c60\u5065\u5eb7\u5ea6",
            "BatteryHealthDescription" => "\u6ee1\u7535\u5bb9\u91cf\u5360\u8bbe\u8ba1\u5bb9\u91cf\u7684\u767e\u5206\u6bd4\u3002",
            "BatteryUsageTime" => "\u7535\u6c60\u4f7f\u7528\u65f6\u95f4",
            "BatteryUsageTimeDescription" => "\u7b14\u8bb0\u672c\u65ad\u5f00\u7535\u6e90\u9002\u914d\u5668\u540e\u7684\u65f6\u95f4\u3002",
            "BatteryUsageTimeFormat" => "{0} ({1})",
            "BatteryCycleCount" => "\u5faa\u73af\u6b21\u6570",
            "BatteryCycleCountDescription" => "\u7535\u6c60\u5145\u653e\u7535\u5faa\u73af\u6b21\u6570\u3002",
            "BatteryManufactureDate" => "\u751f\u4ea7\u65e5\u671f",
            "BatteryManufactureDateDescription" => "\u7535\u6c60\u7684\u751f\u4ea7\u65e5\u671f\u3002",
            "BatteryFirstUseDate" => "\u7535\u6c60\u9996\u6b21\u4f7f\u7528\u65f6\u95f4",
            "BatteryFirstUseDateDescription" => "\u7535\u6c60\u7b2c\u4e00\u6b21\u4f7f\u7528\u7684\u65e5\u671f\u3002",
            "GpuWorkingMode" => "\u663e\u5361\u5de5\u4f5c\u6a21\u5f0f",
            "GpuModeHybrid" => "\u6df7\u5408\u6a21\u5f0f",
            "GpuModeIntegratedOnly" => "\u6df7\u5408\u6838\u663e\u6a21\u5f0f",
            "GpuModeHybridAuto" => "\u6df7\u5408\u81ea\u52a8\u6a21\u5f0f",
            "GpuModeDiscrete" => "\u72ec\u663e\u76f4\u8fde\u6a21\u5f0f",
            "GpuModeIntegratedDirect" => "\u6838\u663e\u76f4\u8fde\u6a21\u5f0f",
            "GpuRestartRequiredTitle" => "\u9700\u8981\u91cd\u542f",
            "GpuRestartRequiredMessage" => "\u5207\u6362\u5230\u201c{0}\u201d\u9700\u8981\u91cd\u542f\u624d\u80fd\u5b8c\u5168\u751f\u6548\u3002\u8bf7\u9009\u62e9\u91cd\u542f\u65f6\u95f4\u3002",
            "RestartNow" => "\u7acb\u5373\u91cd\u542f",
            "RestartLater" => "\u7a0d\u540e\u91cd\u542f",
            "RestartFailedFormat" => "\u542f\u52a8\u91cd\u542f\u5931\u8d25\uff1a{0}",
            "GpuModeSwitchFailedFormat" => "\u663e\u5361\u6a21\u5f0f\u5207\u6362\u5931\u8d25\uff1a{0}",
            "GpuModeUnavailableFormat" => "\u663e\u5361\u6a21\u5f0f\u4e0d\u53ef\u7528\uff1a{0}",
            "Start" => "\u542f\u52a8",
            "Stop" => "\u505c\u6b62",
            "FullSpeed" => "\u98ce\u6247\u62c9\u6ee1",
            "EnablingFullSpeed" => "\u6b63\u5728\u5f00\u542f\u98ce\u6247\u62c9\u6ee1...",
            "DisablingFullSpeed" => "\u6b63\u5728\u5173\u95ed\u98ce\u6247\u62c9\u6ee1...",
            "FullSpeedEnabled" => "\u98ce\u6247\u5df2\u62c9\u6ee1",
            "FullSpeedFailed" => "\u98ce\u6247\u62c9\u6ee1\u5207\u6362\u5931\u8d25",
            "Idle" => "\u7a7a\u95f2",
            "Stopping" => "\u505c\u6b62\u4e2d...",
            "Running" => "\u8fd0\u884c\u4e2d",
            "Monitoring" => "\u76d1\u63a7\u4e2d",
            "HeatSoak" => "\u70ed\u6d78",
            "Auto" => "\u81ea\u52a8",
            "Low" => "\u4f4e",
            "High" => "\u9ad8",
            "On" => "\u5f00",
            "Off" => "\u5173",
            "NotSupported" => "\u4e0d\u652f\u6301",
            "Close" => "\u5173\u95ed",
            "Saved" => "\u5df2\u4fdd\u5b58",
            "ControllerEnabled" => "\u98ce\u6247\u63a7\u5236\u5df2\u542f\u7528",
            "ControllerResumed" => "\u5df2\u6062\u590d\u4e0a\u6b21\u672a\u505c\u6b62\u7684\u98ce\u6247\u63a7\u5236",
            "RestoringAuto" => "\u6b63\u5728\u6062\u590d\u81ea\u52a8\u98ce\u6247\u63a7\u5236...",
            "AutoRestored" => "\u5df2\u6062\u590d\u81ea\u52a8\u98ce\u6247\u63a7\u5236",
            "RestoreAutoFailed" => "\u6062\u590d\u81ea\u52a8\u5931\u8d25",
            "StopBeforeSwitch" => "\u5207\u6362\u65b9\u6848\u524d\u8bf7\u5148\u505c\u6b62\u63a7\u5236\u5668\u3002",
            "FanCurve" => "\u98ce\u6247\u66f2\u7ebf",
            "AdvancedCurve" => "\u9ad8\u7ea7\u66f2\u7ebf",
            "FanCurveWarningTitle" => "\u98ce\u6247\u66f2\u7ebf\u8b66\u544a",
            "FanCurveWarning" => "\u98ce\u6247\u66f2\u7ebf\u4f1a\u9891\u7e41\u8c03\u6574\u8f6c\u901f\uff0c\u8fc7\u77ed\u7684\u95f4\u9694\u53ef\u80fd\u5bfc\u81f4\u5361\u987f\u3002\u8bf7\u8c28\u614e\u4f7f\u7528\u3002",
            "Continue" => "\u7ee7\u7eed",
            "Cancel" => "\u53d6\u6d88",
            "Warning" => "\u8b66\u544a",
            "StopFanControlFirst" => "\u8bf7\u5148\u505c\u6b62\u98ce\u6247\u63a7\u5236\uff01",
            "MonitorError" => "\u76d1\u63a7\u9519\u8bef",
            "FanReadError" => "\u98ce\u6247\u8bfb\u53d6\u9519\u8bef",
            "FanWriteError" => "\u98ce\u6247\u5199\u5165\u9519\u8bef",
            "Startup" => "\u5f00\u673a\u81ea\u542f",
            "StartToTray" => "\u542f\u52a8\u5230\u6258\u76d8",
            "MinimizeToTray" => "\u6700\u5c0f\u5316\u5230\u6258\u76d8",
            "CloseToTray" => "\u5173\u95ed\u65f6\u6700\u5c0f\u5316",
            "DisableControlOnSleep" => "\u7761\u7720\u65f6\u5173\u95ed\u63a7\u5236",
            "AttemptDisableControlOnSleep" => "\u5373\u4f7f\u5f53\u524d\u63a7\u5236\u65b9\u5f0f\u4e0d\u652f\u6301\uff0c\u4e5f\u5c1d\u8bd5\u5728\u7761\u7720\u65f6\u5173\u95ed\u63a7\u5236",
            "ControlResumedAfterSleep" => "\u5524\u9192\u540e\u5df2\u6062\u590d\u98ce\u6247\u63a7\u5236",
            "SleepControlFailed" => "\u7761\u7720\u65f6\u5173\u95ed\u63a7\u5236\u5931\u8d25",
            "ShowWindow" => "\u663e\u793a\u7a97\u53e3",
            "Exit" => "\u9000\u51fa",
            "Normal" => "\u5e73\u65f6",
            "Game" => "\u6e38\u620f",
            "NormalFan1" => "\u5e73\u65f6 FAN1",
            "NormalFan2" => "\u5e73\u65f6 FAN2",
            "GameFan1" => "\u6e38\u620f FAN1",
            "GameFan2" => "\u6e38\u620f FAN2",
            "PowerSaving" => "\u7701\u7535",
            "Intelligent" => "\u667a\u80fd",
            "Performance" => "\u6027\u80fd",
            "Geek" => "\u6781\u5ba2",
            "Unknown" => "\u672a\u77e5",
            "FirmwareAuto" => "\u9ed8\u8ba4\u81ea\u52a8",
            "CurrentMode" => "\u5f53\u524d\u6a21\u5f0f",
            "ItsModeSwitchUnavailable" => "\u672a\u68c0\u6d4b\u5230\u53ef\u7528\u7684 LenovoProcessManagement \u6216\u65e7\u7248 LITSSVC \u6027\u80fd\u6a21\u5f0f\u5207\u6362\u63a5\u53e3\u3002",
            "ItsModeSwitchNotConfirmed" => "\u670d\u52a1\u5df2\u63a5\u6536\u547d\u4ee4\uff0c\u4f46 3 \u79d2\u5185\u672a\u80fd\u4ece\u6ce8\u518c\u8868\u786e\u8ba4\u6a21\u5f0f\u53d8\u66f4\u3002",
            "ItsModeSwitchFailedFormat" => "\u6a21\u5f0f\u5207\u6362\u5931\u8d25\uff1a{0}",
            "Holding" => "\u5ef6\u65f6",
            "PendingChange" => "\u7b49\u5f85\u4e8c\u6b21\u786e\u8ba4",
            "None" => "\u65e0",
            "PressShortcut" => "\u8bf7\u6309\u5feb\u6377\u952e",
            "HotkeyNeedsModifier" => "\u5168\u5c40\u5feb\u6377\u952e\u9700\u8981 Ctrl/Alt/Shift/Win \u7ec4\u5408\uff0c\u6216\u4f7f\u7528 F1-F24\u3002",
            "HotkeyInvalid" => "\u5feb\u6377\u952e\u65e0\u6548",
            "HotkeyRegisterFailed" => "\u5168\u5c40\u5feb\u6377\u952e\u6ce8\u518c\u5931\u8d25",
            "FixedRpmNote" => "0 = \u56fa\u4ef6\u9ed8\u8ba4\u81ea\u52a8\u3002\u975e 0 \u8f6c\u901f\u4f1a\u81ea\u52a8\u9650\u5236\u5230\u68c0\u6d4b\u5230\u7684\u98ce\u6247\u8303\u56f4\u3002",
            "KeyboardBacklightBrightness" => "\u952e\u76d8\u80cc\u5149\u4eae\u5ea6",
            "KeyboardBacklightOff" => "\u5173\u95ed",
            "KeyboardBacklightAutoOff" => "30 \u79d2\u5185\u65e0\u952e\u76d8\u6216\u89e6\u6478\u677f\u64cd\u4f5c\u81ea\u52a8\u5173\u95ed\u952e\u76d8\u80cc\u5149",
            "FunctionLock" => "\u529f\u80fd\u9501\u5b9a\uff08Fn + FnLock\uff09",
            "CapsLockOsd" => "CapsLock OSD \u56fe\u6807",
            "NumLockOsd" => "NumLock OSD \u56fe\u6807",
            "FnCtrlSwap" => "Fn \u952e\u548c Ctrl \u952e\u4e92\u6362",
            "Touchpad" => "\u89e6\u6478\u677f",
            "ReadFailed" => "\u8bfb\u53d6\u5931\u8d25",
            "EyeCareMode" => "\u62a4\u773c\u6a21\u5f0f",
            "EyeCareModeVantage" => "\u62a4\u773c\u6a21\u5f0f(Vantage)",
            "EyeCareModeDescription" => "\u8c03\u8282\u5c4f\u5e55\u8272\u6e29\u4ee5\u5e2e\u52a9\u51cf\u8f7b\u773c\u775b\u75b2\u52b3\u3002",
            "EyeCareModePcManager" => "\u62a4\u773c\u6a21\u5f0f(\u8054\u60f3\u7535\u8111\u7ba1\u5bb6)",
            "EyeCareModePcManagerDescription" => "\u4f7f\u7528\u8054\u60f3\u7535\u8111\u7ba1\u5bb6\u7684 Gamma \u8272\u6e29\u7b97\u6cd5\uff0c\u72ec\u7acb\u4e8e Vantage \u62a4\u773c\u6a21\u5f0f\u3002",
            "ColorTemperature" => "\u8272\u6e29\u8c03\u8282",
            "PcManagerColorTemperatureDescription" => "\u5411\u53f3\u964d\u4f4e\u5f00\u5c14\u6587\u503c\u5e76\u589e\u5f3a\u6696\u8272\uff1b\u62a4\u773c\u6a21\u5f0f\u5f00\u542f\u65f6\u4e0d\u53ef\u8c03\u8282\u3002",
            "RestoreDefault" => "\u6062\u590d\u9ed8\u8ba4",
            "SetDefaultValues" => "\u8bbe\u7f6e\u9ed8\u8ba4\u503c",
            "PcManagerEyeCareDefaultSettings" => "\u8bbe\u7f6e\u7535\u8111\u7ba1\u5bb6\u62a4\u773c\u9ed8\u8ba4\u503c",
            "PcManagerNormalDefault" => "\u5e73\u65f6\u9ed8\u8ba4\u503c",
            "PcManagerNormalDefaultDescription" => "\u5173\u95ed\u62a4\u773c\u6a21\u5f0f\u6216\u5728\u666e\u901a\u72b6\u6001\u6062\u590d\u9ed8\u8ba4\u65f6\u4f7f\u7528\u3002",
            "PcManagerEyeCareDefault" => "\u62a4\u773c\u6a21\u5f0f\u9ed8\u8ba4\u503c",
            "PcManagerEyeCareDefaultDescription" => "\u5f00\u542f\u62a4\u773c\u6a21\u5f0f\u6216\u5728\u62a4\u773c\u72b6\u6001\u6062\u590d\u9ed8\u8ba4\u65f6\u4f7f\u7528\u3002",
            "PcManagerEyeCareStatusFormat" => "\u5f53\u524d\uff1a{0} K\uff1b\u5e73\u65f6\u9ed8\u8ba4\uff1a{1} K\uff1b\u62a4\u773c\u9ed8\u8ba4\uff1a{2} K\uff1b\u7535\u8111\u7ba1\u5bb6 DLL\uff1a{3}",
            "Restore" => "\u8fd8\u539f",
            "ColorEffect" => "\u989c\u8272\u6548\u679c",
            "ColorEffectDescription" => "\u9009\u62e9 Vantage 5.x \u62a4\u773c\u6a21\u5f0f\u7684\u8272\u6e29\u914d\u7f6e\u3002",
            "EyeCareSchedule" => "\u8ba1\u5212",
            "EyeCareScheduleDescription" => "\u9009\u62e9\u59cb\u7ec8\u751f\u6548\uff0c\u6216\u4ec5\u5728\u591c\u95f4\u65f6\u6bb5\u751f\u6548\u3002",
            "CustomColorTemperature" => "\u81ea\u5b9a\u4e49\u8272\u6e29",
            "CustomColorTemperatureDescription" => "\u9009\u62e9\u201c\u81ea\u5b9a\u4e49\u989c\u8272\u201d\u65f6\u4f7f\u7528\u7684\u8272\u6e29\u3002",
            "EyeCareVivid" => "\u751f\u52a8",
            "EyeCareVisionCare" => "\u62a4\u773c",
            "EyeCareAmber" => "\u6cdb\u9ec4",
            "EyeCareCustom" => "\u81ea\u5b9a\u4e49\u989c\u8272",
            "EyeCareAlways" => "\u59cb\u7ec8",
            "EyeCareNight" => "\u591c\u95f4",
            "ColorTemperatureFormat" => "{0} K",
            "EyeCareStatusFormat" => "API\uff1a{0}\uff1b\u51fa\u5382\u8272\u6e29\uff1a{1} K",
            "ApiAvailable" => "\u53ef\u7528",
            "ApiUnavailable" => "\u4e0d\u53ef\u7528",
            "ColorManagement" => "\u989c\u8272\u7ba1\u7406",
            "ColorManagementDescription" => "\u4e3a\u5f53\u524d\u573a\u666f\u5207\u6362\u663e\u793a\u5668\u8272\u57df / ICC \u914d\u7f6e\u3002",
            "ColorManagementStatusFormat" => "\u5f53\u524d\u8def\u5f84\uff1a{0}\uff1bOptionsColor\uff1a{1}\uff1b24H2+\uff1a{2}",
            "ColorModeDefault" => "\u9ed8\u8ba4",
            "ColorModeAdobeRgb" => "Adobe RGB",
            "ColorModeSrgb" => "sRGB",
            "ColorModeDisplayP3" => "Display P3",
            "ColorModeNative" => "Native",
            "ColorModeRec709" => "REC709",
            "ColorModeDciP3" => "DCI P3",
            "ColorModeAuto" => "\u81ea\u52a8",
            "ColorModeDicomDim" => "DICOM Dim",
            "ColorModeDicomOffice" => "DICOM Office",
            "Yes" => "\u662f",
            "No" => "\u5426",
            "DolbyAtmos" => "Dolby Atmos",
            "DolbyAtmosDescription" => "\u5207\u6362 Dolby \u5168\u666f\u58f0\u7684\u8f93\u51fa\u97f3\u6548\u6a21\u5f0f\u3002\u5efa\u8bae\u4f7f\u7528 Dolby Access \u8f6f\u4ef6\u8c03\u8282\u3002",
            "SpeakerNoiseCancellation" => "\u626c\u58f0\u5668\u6d88\u566a",
            "SpeakerNoiseCancellationDescription" => "\u8fc7\u6ee4\u8f93\u51fa\u97f3\u9891\u4e2d\u7684\u5176\u4ed6\u58f0\u97f3\uff0c\u4ec5\u64ad\u653e\u4eba\u58f0\u3002\u5efa\u8bae\u5728\u80cc\u666f\u566a\u97f3\u8f83\u5927\u7684\u7ebf\u4e0a\u4f1a\u8bae\u4e2d\u4f7f\u7528\u3002",
            "SpeakerNoiseDriverControlled" => "\u7531 Lenovo \u667a\u80fd\u6d88\u566a\u63d2\u4ef6\u63a7\u5236\u3002",
            "MicrophoneNoiseCancellation" => "\u9ea6\u514b\u98ce\u964d\u566a",
            "MicrophoneNoiseCancellationDescription" => "\u4f7f\u7528\u8bbe\u5907\u97f3\u9891\u9a71\u52a8\u7684\u9ea6\u514b\u98ce\u964d\u566a\u7b97\u6cd5\u3002",
            "DolbyDynamic" => "\u52a8\u6001",
            "DolbyMovie" => "\u7535\u5f71",
            "DolbyMusic" => "\u97f3\u4e50",
            "DolbyGame" => "\u6e38\u620f",
            "DolbyVoice" => "\u8bed\u97f3",
            "DolbyCustom" => "\u81ea\u5b9a\u4e49",
            "DolbyDriverControlled" => "\u7531\u5df2\u5b89\u88c5\u7684 Dolby DAX \u670d\u52a1\u63a7\u5236\u3002",
            "NoiseNormal" => "\u6b63\u5e38",
            "NoiseVoiceRecognition" => "\u58f0\u97f3\u8bc6\u522b",
            "NoiseOnlyMyVoice" => "\u4ec5\u6211\u7684\u58f0\u97f3",
            "NoiseOnlyMyVoiceNeedsEnrollment" => "\u4ec5\u6211\u7684\u58f0\u97f3\uff08\u8bf7\u5148\u5f55\u5236\u6211\u7684\u58f0\u97f3\uff09",
            "NoiseMultipleVoices" => "\u591a\u4eba\u58f0\u97f3",
            "NoiseVendorFormat" => "\u5f53\u524d\u964d\u566a\u5f15\u64ce\uff1a{0}",
            "RecordMyVoice" => "\u5f55\u5236\u6211\u7684\u58f0\u97f3",
            "RecordMyVoiceDescription" => "\u5f55\u5236 20 \u79d2\u58f0\u7eb9\uff0c\u7528\u4e8e\u201c\u4ec5\u6211\u7684\u58f0\u97f3\u201d\u964d\u566a\u6a21\u5f0f\u3002",
            "Record" => "\u5f55\u5236",
            "RecordAgain" => "\u91cd\u65b0\u5f55\u5236",
            "ReplaceVoiceIdWarning" => "\u91cd\u65b0\u5f55\u5236\u4f1a\u66ff\u6362\u5df2\u6709\u58f0\u7eb9\u3002\u662f\u5426\u7ee7\u7eed\uff1f",
            "VoiceRecordComplete" => "\u58f0\u7eb9\u5f55\u5236\u5b8c\u6210\u3002",
            "VoiceRecordFailedFormat" => "\u58f0\u7eb9\u5f55\u5236\u5931\u8d25\uff1a{0}",
            "VoiceRecordingCountdownFormat" => "\u8bf7\u5728\u5b89\u9759\u73af\u5883\u4e2d\u6301\u7eed\u6e05\u6670\u8bf4\u8bdd\uff0c\u5269\u4f59 {0} \u79d2\u3002",
            "VoiceRecordProcessing" => "\u6b63\u5728\u63d0\u53d6\u5e76\u4fdd\u5b58\u58f0\u7eb9...",
            "VoiceIdRecorded" => "\u5df2\u5f55\u5165\u58f0\u7eb9\u3002",
            "VoiceIdNotRecorded" => "\u5c1a\u672a\u5f55\u5165\u58f0\u7eb9\u3002",
            "OK" => "\u786e\u5b9a",
            "CpuPl1" => "CPU PL1",
            "CpuPl2" => "CPU PL2",
            "CpuTemperatureLimit" => "CPU \u6e29\u5ea6\u5899",
            "CpuTurboTimeLimit" => "CPU Turbo Time Limit",
            "GpuPowerBoost" => "GPU Power Boost",
            "GpuConfigurableTgp" => "GPU Configurable TGP",
            "GpuTemperatureLimit" => "GPU \u6e29\u5ea6\u5899",
            "GpuToCpuDynamicBoost" => "GPU To CPU Dynamic Boost",
            "PowerSettingsReadFailedFormat" => "\u8bfb\u53d6\u529f\u8017\u8bbe\u7f6e\u5931\u8d25\uff1a{0}",
            "PowerSettingsWriteFailedFormat" => "\u5199\u5165\u529f\u8017\u8bbe\u7f6e\u5931\u8d25\uff1a{0}",
            "RestoreCurrentModeDefaults" => "\u6062\u590d\u5f53\u524d\u6a21\u5f0f\u9ed8\u8ba4\u503c\uff08\u63d2\u7535\u65f6\uff09",
            "PowerSettingsCurrentModeUnavailable" => "\u65e0\u6cd5\u786e\u5b9a\u5f53\u524d\u6a21\u5f0f\uff0c\u4e0d\u80fd\u6062\u590d\u9ed8\u8ba4\u529f\u8017\u8bbe\u7f6e\u3002",
            "PowerSettingRangeFormat" => "\u201c{0}\u201d\u5fc5\u987b\u662f {1} \u5230 {2} \u4e4b\u95f4\u7684\u6574\u6570\u3002",
            "PowerSettingPositiveIntegerFormat" => "\u201c{0}\u201d\u5fc5\u987b\u662f\u6b63\u6574\u6570\uff1b\u6ed1\u52a8\u6761\u8303\u56f4\u4e0d\u9650\u5236\u624b\u52a8\u8f93\u5165\u503c\u3002",
            "PowerSettingNonNegativeIntegerFormat" => "\u201c{0}\u201d\u5fc5\u987b\u662f\u975e\u8d1f\u6574\u6570\uff1b\u6ed1\u52a8\u6761\u8303\u56f4\u4e0d\u9650\u5236\u624b\u52a8\u8f93\u5165\u503c\u3002",
            "PowerSettingsTurboRequired" => "\u8bf7\u9009\u62e9 CPU Turbo Time Limit\u3002",
            "ReadingSettings" => "\u6b63\u5728\u8bfb\u53d6\u5f53\u524d\u72b6\u6001...",
            "SettingsReadFailedFormat" => "\u8bfb\u53d6\u5931\u8d25\uff1a{0}",
            "SettingWriteFailedFormat" => "\u5199\u5165\u5931\u8d25\uff1a{0}",
            "UnknownEcValueFormat" => "\u672a\u77e5 0x{0:X2}",
            _ => key
        } : key switch
        {
            "AppTitle" => "ThinkBook Toolkit - Performance & Cooling",
            "Strategy" => "Mode",
            "FixedRpm" => "Fixed RPM",
            "TempSmoothing" => "Temp smoothing",
            "RampDown" => "Ramp down",
            "GameExitHold" => "Game hold",
            "FixedMode" => "Mode",
            "FixedModeHotkey" => "Hotkey",
            "FixedSyncFanSpeeds" => "Sync fixed speeds",
            "AutoDetectGames" => "Auto detect games",
            "EditFan" => "Edit",
            "SyncFanSpeeds" => "Sync speeds",
            "Fan1" => "Fan 1",
            "Fan2" => "Fan 2",
            "CpuCurve" => "CPU Curve",
            "GpuCurve" => "GPU Curve",
            "TemperatureAxis" => "Temperature (\u00B0C)",
            "PowerSettings" => "Power settings",
            "BatterySettings" => "Battery settings",
            "BatteryInformation" => "Battery information",
            "DisplaySettings" => "Display settings",
            "SoundSettings" => "Sound settings",
            "OtherSettings" => "Other settings",
            "DeviceInformation" => "Device information",
            "OpenDeviceInformation" => "Open device information",
            "ReadingDeviceInformation" => "Reading device information...",
            "DeviceInformationReadFailedFormat" => "Failed to read device information: {0}",
            "Device" => "Device",
            "WindowsVersion" => "Windows version",
            "DeviceName" => "Device name",
            "DeviceModel" => "Model",
            "ProductNumber" => "Product number",
            "DeviceCode" => "Device code",
            "SerialNumber" => "Serial number",
            "BiosVersion" => "BIOS version",
            "SmbiosVersion" => "SMBIOS version",
            "MeVersion" => "ME version",
            "AmdPspVersion" => "AMD PSP version",
            "AcpiVersion" => "ACPI version",
            "UefiVersion" => "UEFI version",
            "DeviceId" => "Device ID",
            "ProductId" => "Product ID",
            "Copy" => "Copy",
            "ShowSerialNumber" => "Show serial number",
            "HideSerialNumber" => "Hide serial number",
            "CpuTopologyFormat" => "{0} cores / {1} threads",
            "VideoMemoryFormat" => "VRAM: {0}",
            "SharedGraphicsMemory" => "Uses shared system memory",
            "Memory" => "Memory",
            "InstalledMemoryFormat" => "Installed {0} ({1} usable)",
            "Storage" => "Storage",
            "UsedTotalFormat" => "{0} used / {1} total",
            "TotalStorageFormat" => "Partition capacity: {0}",
            "Motherboard" => "Motherboard",
            "Displays" => "Displays",
            "Display" => "Display",
            "WarrantyStartDate" => "Start date",
            "WarrantyEndDate" => "End date",
            "WarrantyProgress" => "Warranty elapsed",
            "WarrantyInformation" => "Warranty information",
            "WarrantyLoading" => "Checking",
            "WarrantyInCoverage" => "Covered",
            "WarrantyExpired" => "Expired",
            "WarrantyNotStarted" => "Not started",
            "WarrantyUnavailable" => "Unavailable",
            "NoInformation" => "No information",
            "WarrantyCached" => "The online lookup failed. Showing the last cached warranty information.",
            "WarrantyQueryFailedFormat" => "Warranty lookup failed: {0}",
            "WarrantyDateFormat" => "yyyy-MM-dd",
            "AdvancedToolkit" => "Advanced settings",
            "BootLogoCustomization" => "Change boot logo",
            "BootLogoCustomizationTitle" => "Change boot logo",
            "BootLogoResolutionFormat" => "Maximum supported resolution: {0} \u00d7 {1}",
            "BootLogoFormatsFormat" => "Supported image formats: {0}",
            "BootLogoPreviewDisclaimer" => "The preview may not exactly match the actual boot screen.",
            "ShowWindowsLoadingIcon" => "Show loading icon",
            "Customize" => "Customize",
            "ResetToDefault" => "Reset to default",
            "Confirm" => "Confirm",
            "Attention" => "Attention",
            "BiosSetup" => "BIOS setup",
            "StartupInterrupt" => "Startup interrupt",
            "SecureWipe" => "Secure wipe",
            "AdvancedToolkitUnavailableFormat" => "Advanced settings unavailable: {0}",
            "AdvancedToolkitFailedFormat" => "Operation failed: {0}",
            "BootLogoApplyConfirmFirst" => "Apply the current boot logo settings?",
            "BootLogoApplyConfirmSecond" => "This is the second confirmation. The EFI system partition and boot configuration will be modified. Apply now?",
            "BootLogoResetConfirmFirst" => "Restore the default Lenovo boot logo?",
            "BootLogoResetConfirmSecond" => "This is the second confirmation. The custom boot logo will be removed from the EFI partition. Continue?",
            "BootLogoSuccess" => "The boot logo settings were applied and will take effect on the next boot.",
            "BiosSetupConfirmFirst" => "The next boot will be directed to BIOS Setup Utility. Continue?",
            "BiosSetupConfirmSecond" => "The system will restart and boot into BIOS Setup Utility. This function lets you change advanced settings such as Secure Boot, boot order, and other options.\nRestart the system and enter BIOS Setup Utility?",
            "StartupInterruptConfirmFirst" => "The next boot will be directed to the Lenovo startup interrupt menu. Continue?",
            "StartupInterruptConfirmSecond" => "The system will restart and boot into the Startup Interrupt menu. From there you can enter BIOS Setup Utility, diagnose hardware, recover the system, and access other advanced configuration.\nRestart and enter the Startup Interrupt menu?",
            "SecureWipeConfirmFirst" => "Secure wipe can permanently delete data from a storage device. Continue to the confirmation step?",
            "SecureWipeConfirmSecond" => "The system will restart and enter the Secure Wipe application. You can select a storage device and erase all or part of it using the selected method.\nRestart and enter the Secure Wipe application?",
            "BatteryChargeMode" => "Charging mode",
            "BatteryChargeModeDescription" => "Choose the battery charging strategy. Conservation limits charge level, while rapid charge uses more power.",
            "BatteryConservationMode" => "Conservation",
            "BatteryNormalMode" => "Normal",
            "BatteryRapidChargeMode" => "Rapid charge",
            "OvernightBatteryCharging" => "Overnight battery charging",
            "OvernightBatteryChargingDescription" => "When plugged in overnight, charge to 80% first and finish charging to 100% in the morning.",
            "AlwaysOnUsb" => "Always on USB",
            "AlwaysOnUsbDescription" => "Keep the designated USB port powered while shut down, sleeping, or hibernating.",
            "AlwaysOnUsbOff" => "Off",
            "AlwaysOnUsbSleeping" => "On while sleeping",
            "AlwaysOnUsbAlways" => "Always on",
            "FlipToStart" => "Flip to start",
            "FlipToStartDescription" => "Turn the laptop on automatically when the lid is opened.",
            "BatteryTemperature" => "Battery temperature",
            "BatteryTemperatureDescription" => "Current battery temperature.",
            "BatteryPower" => "Battery charge/discharge power",
            "BatteryPowerDescription" => "(+) indicates charging and (-) indicates discharging.",
            "BatteryMinimumPower" => "Minimum charge/discharge power",
            "BatteryMinimumPowerDescription" => "Lowest power in the current continuous charging or discharging period.",
            "BatteryMaximumPower" => "Maximum charge/discharge power",
            "BatteryMaximumPowerDescription" => "Highest power in the current continuous charging or discharging period.",
            "BatteryCurrentCapacity" => "Current capacity",
            "BatteryCurrentCapacityDescription" => "Current battery capacity.",
            "BatteryFullChargeCapacity" => "Full charge capacity",
            "BatteryFullChargeCapacityDescription" => "Battery capacity when fully charged.",
            "BatteryDesignCapacity" => "Design capacity",
            "BatteryDesignCapacityDescription" => "Designed battery capacity.",
            "BatteryHealth" => "Battery health",
            "BatteryHealthDescription" => "Full charge capacity as a percentage of design capacity.",
            "BatteryUsageTime" => "Battery usage time",
            "BatteryUsageTimeDescription" => "Time since the laptop was disconnected from its power adapter.",
            "BatteryUsageTimeFormat" => "{0} ({1})",
            "BatteryCycleCount" => "Cycle count",
            "BatteryCycleCountDescription" => "Battery charge and discharge cycle count.",
            "BatteryManufactureDate" => "Manufacture date",
            "BatteryManufactureDateDescription" => "Battery manufacture date.",
            "BatteryFirstUseDate" => "First use date",
            "BatteryFirstUseDateDescription" => "Date when the battery was first used.",
            "GpuWorkingMode" => "GPU working mode",
            "GpuModeHybrid" => "Hybrid mode",
            "GpuModeIntegratedOnly" => "iGPU only",
            "GpuModeHybridAuto" => "Hybrid auto",
            "GpuModeDiscrete" => "Discrete graphics",
            "GpuModeIntegratedDirect" => "Integrated graphics",
            "GpuRestartRequiredTitle" => "Restart required",
            "GpuRestartRequiredMessage" => "Switching to \"{0}\" requires a restart to take full effect. Choose when to restart.",
            "RestartNow" => "Restart now",
            "RestartLater" => "Restart later",
            "RestartFailedFormat" => "Failed to start the restart: {0}",
            "GpuModeSwitchFailedFormat" => "Failed to switch GPU mode: {0}",
            "GpuModeUnavailableFormat" => "GPU mode is unavailable: {0}",
            "FullSpeed" => "Full speed",
            "EnablingFullSpeed" => "Enabling full speed...",
            "DisablingFullSpeed" => "Disabling full speed...",
            "FullSpeedEnabled" => "Fans at full speed",
            "FullSpeedFailed" => "Full-speed switch failed",
            "Idle" => "Idle",
            "Stopping" => "Stopping...",
            "HeatSoak" => "Heat soak",
            "Auto" => "Auto",
            "Low" => "Low",
            "High" => "High",
            "NotSupported" => "Not supported",
            "Close" => "Close",
            "ControllerEnabled" => "Controller enabled",
            "ControllerResumed" => "Resumed previously active fan control",
            "RestoringAuto" => "Restoring automatic fan control...",
            "AutoRestored" => "Automatic fan control restored",
            "RestoreAutoFailed" => "Restore auto failed",
            "StopBeforeSwitch" => "Stop the controller before switching profiles.",
            "FanCurve" => "Fan curve",
            "AdvancedCurve" => "Advanced curve",
            "FanCurveWarningTitle" => "Fan curve warning",
            "FanCurveWarning" => "Fan curve mode adjusts RPM frequently. Very short intervals may cause stuttering; use it carefully.",
            "Continue" => "Continue",
            "Cancel" => "Cancel",
            "Warning" => "Warning",
            "StopFanControlFirst" => "Please stop fan control first!",
            "MonitorError" => "Monitor error",
            "FanReadError" => "Fan read error",
            "FanWriteError" => "Fan write error",
            "Startup" => "Start with Windows",
            "StartToTray" => "Start to tray",
            "MinimizeToTray" => "Minimize to tray",
            "CloseToTray" => "Close to tray",
            "DisableControlOnSleep" => "Disable control while sleeping",
            "AttemptDisableControlOnSleep" => "Try to release fan control for sleep even when the current control method does not support it",
            "ControlResumedAfterSleep" => "Fan control resumed after sleep",
            "SleepControlFailed" => "Failed to disable control for sleep",
            "ShowWindow" => "Show window",
            "Exit" => "Exit",
            "Normal" => "Normal",
            "Game" => "Game",
            "NormalFan1" => "Normal FAN1",
            "NormalFan2" => "Normal FAN2",
            "GameFan1" => "Game FAN1",
            "GameFan2" => "Game FAN2",
            "PowerSaving" => "Cool",
            "Intelligent" => "Auto",
            "Performance" => "Performance",
            "Geek" => "Geek",
            "Unknown" => "Unknown",
            "FirmwareAuto" => "Firmware auto",
            "CurrentMode" => "Current mode",
            "ItsModeSwitchUnavailable" => "No LenovoProcessManagement or legacy LITSSVC performance-mode interface was detected.",
            "ItsModeSwitchNotConfirmed" => "The service accepted the command, but the registry did not confirm the mode change within 3 seconds.",
            "ItsModeSwitchFailedFormat" => "Failed to switch mode: {0}",
            "Holding" => "Holding",
            "PendingChange" => "Waiting for confirmation",
            "None" => "None",
            "PressShortcut" => "Press shortcut",
            "HotkeyNeedsModifier" => "Global hotkeys need Ctrl/Alt/Shift/Win, or use F1-F24.",
            "HotkeyInvalid" => "Invalid hotkey",
            "HotkeyRegisterFailed" => "Global hotkey registration failed",
            "FixedRpmNote" => "0 = firmware auto. Non-zero RPM values are clamped to the detected fan range.",
            "KeyboardBacklightBrightness" => "Keyboard backlight brightness",
            "KeyboardBacklightOff" => "Off",
            "KeyboardBacklightAutoOff" => "Auto-off after 30 seconds without keyboard or touchpad input",
            "FunctionLock" => "Function lock (Fn + FnLock)",
            "CapsLockOsd" => "CapsLock OSD icon",
            "NumLockOsd" => "NumLock OSD icon",
            "FnCtrlSwap" => "Swap Fn and Ctrl keys",
            "Touchpad" => "Touchpad",
            "ReadFailed" => "Read failed",
            "EyeCareMode" => "Eye care mode",
            "EyeCareModeVantage" => "Eye care mode (Vantage)",
            "EyeCareModeDescription" => "Adjust screen color temperature to help reduce eye strain.",
            "EyeCareModePcManager" => "Eye care mode (Lenovo PC Manager)",
            "EyeCareModePcManagerDescription" => "Uses Lenovo PC Manager's gamma color-temperature algorithm independently of Vantage eye care.",
            "ColorTemperature" => "Color temperature",
            "PcManagerColorTemperatureDescription" => "Moving right lowers the Kelvin value and increases warmth. Adjustment is disabled while eye care is on.",
            "RestoreDefault" => "Restore default",
            "SetDefaultValues" => "Set defaults",
            "PcManagerEyeCareDefaultSettings" => "Lenovo PC Manager eye care defaults",
            "PcManagerNormalDefault" => "Normal default",
            "PcManagerNormalDefaultDescription" => "Used when eye care is turned off or defaults are restored in normal mode.",
            "PcManagerEyeCareDefault" => "Eye care default",
            "PcManagerEyeCareDefaultDescription" => "Used when eye care is turned on or defaults are restored in eye care mode.",
            "PcManagerEyeCareStatusFormat" => "Current: {0} K; normal default: {1} K; eye care default: {2} K; PC Manager DLL: {3}",
            "Restore" => "Restore",
            "ColorEffect" => "Color effect",
            "ColorEffectDescription" => "Choose the Vantage 5.x eye care color temperature profile.",
            "EyeCareSchedule" => "Schedule",
            "EyeCareScheduleDescription" => "Apply the color adjustment always, or only during nighttime.",
            "CustomColorTemperature" => "Custom color temperature",
            "CustomColorTemperatureDescription" => "Color temperature used by the custom color mode.",
            "EyeCareVivid" => "Vivid",
            "EyeCareVisionCare" => "Eye care",
            "EyeCareAmber" => "Amber",
            "EyeCareCustom" => "Custom color",
            "EyeCareAlways" => "Always",
            "EyeCareNight" => "Night",
            "ColorTemperatureFormat" => "{0} K",
            "EyeCareStatusFormat" => "API: {0}; factory color temperature: {1} K",
            "ApiAvailable" => "Available",
            "ApiUnavailable" => "Unavailable",
            "ColorManagement" => "Color management",
            "ColorManagementDescription" => "Switch the display color gamut / ICC profile for the current scenario.",
            "ColorManagementStatusFormat" => "Current path: {0}; OptionsColor: {1}; 24H2+: {2}",
            "ColorModeDefault" => "Default",
            "ColorModeAdobeRgb" => "Adobe RGB",
            "ColorModeSrgb" => "sRGB",
            "ColorModeDisplayP3" => "Display P3",
            "ColorModeNative" => "Native",
            "ColorModeRec709" => "REC709",
            "ColorModeDciP3" => "DCI P3",
            "ColorModeAuto" => "Auto",
            "ColorModeDicomDim" => "DICOM Dim",
            "ColorModeDicomOffice" => "DICOM Office",
            "Yes" => "Yes",
            "No" => "No",
            "DolbyAtmos" => "Dolby Atmos",
            "DolbyAtmosDescription" => "Switch the Dolby Atmos output sound profile. Dolby Access is recommended for adjustments.",
            "SpeakerNoiseCancellation" => "Speaker noise cancellation",
            "SpeakerNoiseCancellationDescription" => "Filter other output sounds and only play human voice. Useful for online meetings with noisy speakers.",
            "SpeakerNoiseDriverControlled" => "Controlled by the Lenovo smart noise cancellation plugin.",
            "MicrophoneNoiseCancellation" => "Microphone noise cancellation",
            "MicrophoneNoiseCancellationDescription" => "Use the audio driver's microphone noise cancellation algorithms.",
            "DolbyDynamic" => "Dynamic",
            "DolbyMovie" => "Movie",
            "DolbyMusic" => "Music",
            "DolbyGame" => "Game",
            "DolbyVoice" => "Voice",
            "DolbyCustom" => "Custom",
            "DolbyDriverControlled" => "Controlled by the installed Dolby DAX service.",
            "NoiseNormal" => "Normal",
            "NoiseVoiceRecognition" => "Voice recognition",
            "NoiseOnlyMyVoice" => "Only my voice",
            "NoiseOnlyMyVoiceNeedsEnrollment" => "Only my voice (record my voice first)",
            "NoiseMultipleVoices" => "Multiple voices",
            "NoiseVendorFormat" => "Active noise cancellation engine: {0}",
            "RecordMyVoice" => "Record my voice",
            "RecordMyVoiceDescription" => "Record a 20-second voice ID for the Only my voice mode.",
            "Record" => "Record",
            "RecordAgain" => "Record again",
            "ReplaceVoiceIdWarning" => "Recording again will replace the existing voice ID. Continue?",
            "VoiceRecordComplete" => "Voice ID recording completed.",
            "VoiceRecordFailedFormat" => "Voice ID recording failed: {0}",
            "VoiceRecordingCountdownFormat" => "Speak continuously and clearly in a quiet room. {0} seconds remaining.",
            "VoiceRecordProcessing" => "Extracting and saving the voice ID...",
            "VoiceIdRecorded" => "A voice ID has been recorded.",
            "VoiceIdNotRecorded" => "No voice ID has been recorded.",
            "OK" => "OK",
            "CpuPl1" => "CPU PL1",
            "CpuPl2" => "CPU PL2",
            "CpuTemperatureLimit" => "CPU temperature limit",
            "CpuTurboTimeLimit" => "CPU Turbo Time Limit",
            "GpuPowerBoost" => "GPU Power Boost",
            "GpuConfigurableTgp" => "GPU Configurable TGP",
            "GpuTemperatureLimit" => "GPU temperature limit",
            "GpuToCpuDynamicBoost" => "GPU To CPU Dynamic Boost",
            "PowerSettingsReadFailedFormat" => "Failed to read power settings: {0}",
            "PowerSettingsWriteFailedFormat" => "Failed to write power settings: {0}",
            "RestoreCurrentModeDefaults" => "Restore current mode defaults (plugged in)",
            "PowerSettingsCurrentModeUnavailable" => "The current mode could not be determined, so its default power settings cannot be restored.",
            "PowerSettingRangeFormat" => "\"{0}\" must be an integer from {1} to {2}.",
            "PowerSettingPositiveIntegerFormat" => "\"{0}\" must be a positive integer; the slider range does not limit manual input.",
            "PowerSettingNonNegativeIntegerFormat" => "\"{0}\" must be a non-negative integer; the slider range does not limit manual input.",
            "PowerSettingsTurboRequired" => "Select a CPU Turbo Time Limit.",
            "ReadingSettings" => "Reading current state...",
            "SettingsReadFailedFormat" => "Read failed: {0}",
            "SettingWriteFailedFormat" => "Write failed: {0}",
            "UnknownEcValueFormat" => "Unknown 0x{0:X2}",
            _ => key
        };
    }

    private void UpdateUnlimitedRateLabel()
    {
        var selectedUnlimited = IsUnlimitedRateText(
            _rampDownCombo.SelectedItem as string);
        var index = _rampDownCombo.Items
            .OfType<string>()
            .Select((item, itemIndex) => (item, itemIndex))
            .FirstOrDefault(pair => IsUnlimitedRateText(pair.item))
            .itemIndex;
        var label = IsChinese ? "无限制" : "inf";
        _rampDownCombo.Items[index] = label;
        if (selectedUnlimited)
            _rampDownCombo.SelectedItem = label;
    }

    private void ApplyLanguage()
    {
        Title = T("AppTitle");
        var fontFamilyName = UiTypography.FontFamilyNameFor(_settings.Language);
        FontFamily = new FontFamily(fontFamilyName);
        _cpuMetricTitle.Text = "CPU";
        _gpuMetricTitle.Text = "GPU";
        _vramMetricTitle.Text = "VRAM";
        _fan1MetricTitle.Text = T("Fan1");
        _fan2MetricTitle.Text = T("Fan2");
        _targetMetricTitle.Text = T("Target");
        _profileLabel.Text = T("Profile");
        _nameLabel.Text = T("Name");
        _intervalLabel.Text = T("Interval");
        _smoothingLabel.Text = T("TempSmoothing");
        _rampDownLabel.Text = T("RampDown");
        UpdateUnlimitedRateLabel();
        _gameExitHoldLabel.Text = T("GameExitHold");
        _fixedModeLabel.Text = T("FixedMode");
        _fixedModeHotkeyLabel.Text = T("FixedModeHotkey");
        _editFanLabel.Text = T("EditFan");
        _languageLabel.Text = T("Language");
        _themeLabel.Text = T("Theme");
        _itsModeLabel.Text = T("CurrentMode");
        _gpuModeLabel.Text = T("GpuWorkingMode");
        _saveButton.Content = T("Save");
        _refreshButton.Content = T("Refresh");
        _powerSettingsButton.Content = T("PowerSettings");
        _batterySettingsButton.Content = T("BatterySettings");
        _displaySettingsButton.Content = T("DisplaySettings");
        _soundSettingsButton.Content = T("SoundSettings");
        _otherSettingsButton.Content = T("OtherSettings");
        _deviceModelButton.ToolTip = T("OpenDeviceInformation");
        if (_deviceModelButton.Content is null ||
            _deviceModelButton.Content.ToString() is "Device information" or "\u8bbe\u5907\u8be6\u7ec6\u4fe1\u606f")
            _deviceModelButton.Content = T("DeviceInformation");
        _syncFanSpeedsCheck.Content = T("SyncFanSpeeds");
        _fixedSyncFanSpeedsCheck.Content = T("FixedSyncFanSpeeds");
        _autoDetectGamesCheck.Content = T("AutoDetectGames");
        UpdateFixedModeHotkeyButton();
        _startupCheck.Content = T("Startup");
        _startToTrayCheck.Content = T("StartToTray");
        _minimizeToTrayCheck.Content = T("MinimizeToTray");
        _closeToTrayCheck.Content = T("CloseToTray");
        _disableControlOnSleepCheck.Content =
            _fanController.SupportsDisableControlOnSleep
                ? T("DisableControlOnSleep")
                : T("AttemptDisableControlOnSleep");
        _fullSpeedCheck.Content = T("FullSpeed");
        _startButton.Content = _running ? T("Stop") : T("Start");
        _fixedStrategyTab!.Header = T("FixedRpm");
        _fanCurveStrategyTab!.Header = T("FanCurve");
        _cpuTab!.Header = T("CpuCurve");
        _gpuTab!.Header = T("GpuCurve");
        if (_fixedRpmHeader is not null)
            _fixedRpmHeader.Text = T("FixedRpm");
        if (_fixedColumnLabels.TryGetValue((false, 1), out var normalFan1Label))
            normalFan1Label.Text = T("NormalFan1");
        if (_fixedColumnLabels.TryGetValue((false, 2), out var normalFan2Label))
            normalFan2Label.Text = T("NormalFan2");
        if (_fixedColumnLabels.TryGetValue((true, 1), out var gameFan1Label))
            gameFan1Label.Text = T("GameFan1");
        if (_fixedColumnLabels.TryGetValue((true, 2), out var gameFan2Label))
            gameFan2Label.Text = T("GameFan2");
        if (_fixedRpmNote is not null)
            _fixedRpmNote.Text = T("FixedRpmNote");
        foreach (var (mode, label) in _fixedModeLabels)
            label.Text = T(ModeKey(mode));
        _cpuChart.SetLabels(T("CpuCurve"), T("TemperatureAxis"));
        _gpuChart.SetLabels(T("GpuCurve"), T("TemperatureAxis"));
        _cpuChart.SetFontFamily(fontFamilyName);
        _gpuChart.SetFontFamily(fontFamilyName);
        if (!_running && (_statusText.Text is "Idle" or "\u7a7a\u95f2"))
            _statusText.Text = T("Idle");

        _loadingSettings = true;
        SetComboItems(_languageCombo, ["\u4e2d\u6587", "English"], IsChinese ? 0 : 1);
        SetComboItems(_themeCombo, [T("Light"), T("Dark")], IsDark ? 1 : 0);
        SetComboItems(_fixedModeCombo, [T("Normal"), T("Game")], _effectiveGameMode ? 1 : 0);
        SetComboItems(_editFanCombo, [T("Fan1"), T("Fan2")], _settings.EditFan == 2 ? 1 : 0);
        _updatingItsModeCombo = true;
        SetComboItems(
            _itsModeCombo,
            SwitchableItsModes.Select(ItsModeComboLabel).ToArray(),
            Math.Max(0, Array.IndexOf(SwitchableItsModes, _displayedItsMode)));
        _itsModeCombo.SelectedIndex = Array.IndexOf(SwitchableItsModes, _displayedItsMode);
        _updatingItsModeCombo = false;
        UpdateGpuModeItems(_gpuModes.ToArray(), force: true);
        UpdateGpuModeCombo(_displayedGpuMode);
        _loadingSettings = false;
        ApplyStrategyVisibility();
        UpdateTrayMenu();
    }

    private void ApplyTheme()
    {
        if (Application.Current is not null)
            ModernTheme.Apply(Application.Current, IsDark);

        var background = Brush(IsDark ? "#111827" : "#ffffff");
        var surface = Brush(IsDark ? "#1f2937" : "#ffffff");
        var border = Brush(IsDark ? "#374151" : "#d1d5db");
        var text = Brush(IsDark ? "#f9fafb" : "#111827");
        var muted = Brush(IsDark ? "#d1d5db" : "#4b5563");

        Background = background;
        if (_root is not null)
            _root.Background = background;
        foreach (var tabs in new[] { _strategyTabs, _curveTabs })
        {
            if (tabs is null)
                continue;
            tabs.Background = background;
            tabs.Foreground = text;
            tabs.BorderBrush = border;
        }
        if (_bottomBorder is not null)
            _bottomBorder.Background = background;
        _statusText.Foreground = muted;

        foreach (var metric in _metricBorders)
        {
            metric.Background = surface;
            metric.BorderBrush = border;
        }

        foreach (var label in _labels)
            label.Foreground = muted;
        foreach (var checkBox in new[] { _syncFanSpeedsCheck, _fixedSyncFanSpeedsCheck, _autoDetectGamesCheck, _startupCheck, _startToTrayCheck, _minimizeToTrayCheck, _closeToTrayCheck, _disableControlOnSleepCheck })
            checkBox.Foreground = muted;
        _fullSpeedCheck.Foreground = IsDark ? Brush("#ffffff") : muted;
        foreach (var value in new[] { _cpuTempText, _cpuPowerText, _gpuTempText, _gpuPowerText, _vramTempText, _fan1Text, _fan2Text, _targetText })
            value.Foreground = text;
        _deviceModelButton.Foreground = muted;

        _cpuChart.SetTheme(IsDark);
        _gpuChart.SetTheme(IsDark);

        if (_embeddedMode && _root is not null)
            ModernTheme.ApplyEmbeddedWorkspace(_root, IsDark);
    }

    private static void SetComboItems(ComboBox comboBox, IReadOnlyList<string> items, int selectedIndex)
    {
        comboBox.Items.Clear();
        foreach (var item in items)
            comboBox.Items.Add(item);
        comboBox.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, selectedIndex));
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void AddLabeledControl(Panel panel, TextBlock label, UIElement control)
    {
        if (!_labels.Contains(label))
            _labels.Add(label);
        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 6)
        };
        group.Children.Add(label);
        group.Children.Add(control);
        panel.Children.Add(group);
    }

    private static void AddStandaloneControl(Panel panel, UIElement control)
    {
        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        group.Children.Add(control);
        panel.Children.Add(group);
    }

    private static void SelectComboValue(ComboBox comboBox, double value)
    {
        var unlimited = comboBox.Items
            .OfType<string>()
            .FirstOrDefault(IsUnlimitedRateText);
        if (value <= 0 && unlimited is not null)
        {
            comboBox.SelectedItem = unlimited;
            return;
        }

        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (comboBox.Items.Contains(text))
        {
            comboBox.SelectedItem = text;
            return;
        }

        var nearest = comboBox.Items
            .OfType<string>()
            .Where(item => double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .OrderBy(item => Math.Abs(double.Parse(item, CultureInfo.InvariantCulture) - value))
            .FirstOrDefault();

        comboBox.SelectedItem = nearest ?? comboBox.Items[0];
    }

    private static double SelectedNumber(ComboBox comboBox, double fallback)
    {
        return comboBox.SelectedItem is string text &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private double SelectedRampDown()
    {
        return IsUnlimitedRateText(_rampDownCombo.SelectedItem as string)
            ? 0
            : SelectedNumber(_rampDownCombo, 20);
    }

    private static bool IsUnlimitedRateText(string? text) =>
        string.Equals(text, "inf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "无限制", StringComparison.Ordinal);

    private Border Metric(TextBlock title, UIElement value)
    {
        var panel = new StackPanel();
        if (!_labels.Contains(title))
            _labels.Add(title);
        panel.Children.Add(title);
        panel.Children.Add(value);
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(4),
            Child = panel
        };
        _metricBorders.Add(border);
        return border;
    }

    private static Grid MetricPair(TextBlock left, TextBlock right)
    {
        left.Margin = new Thickness(0, 2, 8, 0);
        right.Opacity = 0;

        var values = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        values.Children.Add(left);
        values.Children.Add(right);

        var panel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true
        };
        panel.Children.Add(values);

        void UpdatePowerVisibility()
        {
            var requiredWidth =
                left.ActualWidth + left.Margin.Left + left.Margin.Right +
                right.ActualWidth + right.Margin.Left + right.Margin.Right;
            right.Opacity = panel.ActualWidth + 0.5 >= requiredWidth ? 1 : 0;
        }

        panel.Loaded += (_, _) => UpdatePowerVisibility();
        panel.SizeChanged += (_, _) => UpdatePowerVisibility();
        left.SizeChanged += (_, _) => UpdatePowerVisibility();
        right.SizeChanged += (_, _) => UpdatePowerVisibility();
        return panel;
    }

    private static TextBlock MetricValue(double fontSize = 20)
    {
        return new TextBlock
        {
            Text = "--",
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 0)
        };
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
    }

    private List<string> ProfileLabels()
    {
        return _profiles.Select((profile, index) => $"{index + 1}: {profile.Name}").ToList();
    }

    private enum ItsModeSwitchResult
    {
        Confirmed,
        Unsupported,
        NotConfirmed
    }
}
