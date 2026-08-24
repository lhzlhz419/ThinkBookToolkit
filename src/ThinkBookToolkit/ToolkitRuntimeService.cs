using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ThinkBookToolkit.FanBackend;
using Forms = System.Windows.Forms;

namespace ThinkBookToolkit;

internal sealed record PerformanceRuntimeSnapshot(
    TemperatureSnapshot? Temperatures,
    FanSnapshot? Fans,
    FanTargets? Target,
    bool IsRunning,
    bool IsFullSpeed,
    ControlStrategy Strategy,
    int ProfileIndex,
    ItsMode ItsMode,
    GpuWorkingMode? GpuMode,
    string PendingGpuMode,
    int MinimumFanRpm,
    int MaximumFanRpm,
    FanRpmLimits FanRpmLimits,
    bool EffectiveGameMode,
    string Status);

internal sealed record ToolkitRuntimeSnapshot(
    TemperatureSnapshot? Temperatures,
    FanSnapshot? Fans,
    BatteryInformationSnapshot? Battery,
    ItsMode ItsMode,
    GpuWorkingMode? GpuMode,
    IReadOnlyList<GpuWorkingMode> SupportedGpuModes,
    bool FanControlRunning,
    bool FullSpeed,
    ControlStrategy FanStrategy,
    FanTargets? FanTarget,
    string PendingGpuMode,
    DateTimeOffset UpdatedAt,
    string? Error)
{
    public string PendingGpuModeSource { get; init; } = string.Empty;
    public PowerSettingsState? PowerSettings { get; init; }
    public WarrantySnapshot? Warranty { get; init; }

    public static ToolkitRuntimeSnapshot Empty { get; } = new(
        null,
        null,
        null,
        ItsMode.Unknown,
        null,
        [],
        false,
        false,
        ControlStrategy.FixedRpm,
        null,
        string.Empty,
        DateTimeOffset.MinValue,
        null);
}

internal sealed class ToolkitRuntimeService : IDisposable
{
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _powerSettingsLockTimer = new();
    private readonly SemaphoreSlim _powerSettingsGate = new(1, 1);
    private readonly FanWatchdogClient _fanWatchdog = new();
    private readonly HybridAutoGpuManager _hybridAutoGpu = new();
    private readonly bool _launchedAtStartup;
    private readonly bool _persistSystemSessionState;
    private readonly LenovoFnKeyManager _fnKeyManager;
    private MainWindow? _fanRuntime;
    private TemperatureReader? _temperatureReader;
    private ToolkitMainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private BatteryInformationSnapshot? _cachedBattery;
    private PowerSettingsState? _cachedPowerSettings;
    private WarrantySnapshot? _cachedWarranty;
    private GpuModeState? _cachedGpuMode;
    private DateTimeOffset _lastBatteryRefresh;
    private DateTimeOffset _lastGpuRefresh;
    private DateTimeOffset _nextGpuOverclockRetry;
    private readonly string _bootSessionId;
    private bool _polling;
    private bool _powerSettingsLockBusy;
    private bool _fanPerformanceLinkBusy;
    private int _fanPerformanceModeChangeGeneration;
    private int _systemSessionEnding;
    private ItsMode _lastFanLinkedPerformanceMode = ItsMode.Unknown;
    private ItsMode? _confirmedPerformanceModeDuringRefresh;
    private string _lastPowerSettingsLockError = string.Empty;
    private bool _disposed;
    private bool _systemThemeSubscribed;
    private bool _powerModeSubscribed;
    private FnKeyNotificationWindow? _fnKeyNotification;
    private bool? _fnKeyNotificationDark;

    public ToolkitRuntimeService(
        AppSettings settings,
        bool launchedAtStartup = false,
        bool persistSystemSessionState = true)
    {
        Settings = settings;
        _launchedAtStartup = launchedAtStartup;
        _persistSystemSessionState = persistSystemSessionState;
        _fnKeyManager = new LenovoFnKeyManager(this);
        _bootSessionId = GpuModeRestartState.CurrentBootSessionId;
        LenovoDependencyDirectory.Configure(settings);
        Snapshot = ToolkitRuntimeSnapshot.Empty;
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _powerSettingsLockTimer.Tick += async (_, _) =>
            await EnforcePowerSettingsLockAsync();
        _hybridAutoGpu.PresenceChanged += OnDiscreteGpuPresenceChanged;
        SyncPollingInterval();
        SyncSystemThemeSubscription();
    }

    public AppSettings Settings { get; }

    internal static TimeSpan PerformanceFanStrategyApplyDelay { get; } =
        TimeSpan.FromSeconds(2);

    internal PowerSettingsLockSelection CurrentPowerSettingsLocks =>
        CurrentPowerModeLock(create: false)?.Locks ??
        new PowerSettingsLockSelection();

    internal PowerSettingsState? CurrentPowerSettingsLockTarget =>
        CurrentPowerModeLock(create: false)?.Target;

    public FeatureAvailabilityReport? Report { get; private set; }

    internal bool CanSwitchItsMode =>
        Report?.IsFullyAvailable(FeatureIds.PerformanceMode) == true;

    internal bool IsItsModeSupported(ItsMode mode) =>
        new ItsModeDetector().IsModeSupported(mode);

    public ToolkitRuntimeSnapshot Snapshot { get; private set; }

    public MainWindow? FanRuntime => _fanRuntime;

    public TimeSpan? FanBackendMinimumReadInterval =>
        _fanRuntime?.RuntimeBackendMinimumReadInterval;

    public TimeSpan? FanBackendMinimumWriteInterval =>
        _fanRuntime?.RuntimeBackendMinimumWriteInterval;

    internal PendingFanBackendStartupNotice?
        PrepareFanBackendStartupNotice()
    {
        if (_fanRuntime is null)
            return null;

        var identity = _fanRuntime.RuntimeBackendIdentity;
        if (FanBackendStartupNoticePreference.ReconcileBackend(
                Settings,
                identity))
        {
            TrySaveFanBackendNoticePreference();
        }

        return FanBackendStartupNoticePreference.GetPending(
            Settings,
            identity,
            _fanRuntime.RuntimeBackendStartupNotice,
            Settings.Language);
    }

    internal void SuppressFanBackendStartupNotice(
        string backendIdentity)
    {
        if (!FanBackendStartupNoticePreference.Suppress(
                Settings,
                backendIdentity))
        {
            return;
        }

        TrySaveFanBackendNoticePreference();
    }

    public bool FanBackendSupportsDisableControlOnSleep =>
        _fanRuntime?.RuntimeBackendSupportsDisableControlOnSleep ??
        Report?.IsAvailable(FeatureIds.SleepFanControl) == true;

    public bool CanConfigureSleepFanControl =>
        _fanRuntime?.RuntimeCanAttemptDisableControlOnSleep ??
        Report?.IsAvailable(FeatureIds.SleepFanControl) == true;

    public FanBackendControlSemantics FanControlSemantics =>
        _fanRuntime?.RuntimeFanControlSemantics ??
        new(
            FanTargetZeroBehavior.ReleaseFanToFirmwareControl,
            FanAutomaticControlRestoreMechanism.WriteZeroToBothTargets,
            "IFanBackend.RestoreAuto()",
            new(
                FanFullSpeedControlMechanism.DedicatedBackendOperation,
                "IFanBackend.SetFullSpeed(true)",
                "IFanBackend.SetFullSpeed(false)"));

    public bool IsChinese => Settings.Language != "en-US";

    public bool IsDark => ResolveDarkTheme(Settings.Theme);

    public bool ExitRequested { get; private set; }

    internal bool IsSystemSessionEnding =>
        Volatile.Read(ref _systemSessionEnding) != 0;

    public event EventHandler? SnapshotChanged;

    public event EventHandler? AvailabilityChanged;

    public event EventHandler? AppearanceChanged;

    public event EventHandler? OverviewLayoutChanged;

    public event EventHandler? FnKeyTakeoverChanged;

    public event EventHandler<string>? StatusChanged;

    internal void SetReportForTesting(FeatureAvailabilityReport report)
    {
        Report = report;
        FeatureAvailabilityCache.Current = report;
        SyncPowerSettingsLockTimer();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetSnapshotForTesting(ToolkitRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public string L(string chinese, string english) =>
        IsChinese ? chinese : english;

    private void TrySaveFanBackendNoticePreference()
    {
        try
        {
            CurveProfileStore.SaveSettings(Settings);
        }
        catch
        {
            StatusChanged?.Invoke(
                this,
                L(
                    "无法保存风扇插件提示设置。",
                    "The fan plug-in notice preference could not be saved."));
        }
    }

    public static bool ResolveDarkTheme(string theme)
    {
        if (theme == "dark")
            return true;
        if (theme == "light")
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task InitializeAsync()
    {
        Report = await FeatureAvailabilityService.DetectAsync();
        ToolkitLog.Info(
            $"Feature detection completed: {Report.Items.Count(item => item.Usable)}/{Report.Items.Count} usable.");
        FeatureAvailabilityCache.Current = Report;

        // This must run before MainWindow raises Loaded: the embedded fan
        // runtime creates its TemperatureReader from that event.
        await RefreshHybridGpuProtectionAsync(forceGpuModeRefresh: true);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _powerModeSubscribed = true;

        if (Report.IsAvailable(FeatureIds.FanControl))
        {
            _fanRuntime = new MainWindow(
                startToTrayRequested: false,
                embeddedMode: true,
                sharedSettings: Settings)
            {
                ShowInTaskbar = false
            };
            _ = new WindowInteropHelper(_fanRuntime).EnsureHandle();
            _fanWatchdog.TryArm(
                _fanRuntime.RuntimeBackendIdentity,
                out _);
            _fanRuntime.RaiseEvent(new RoutedEventArgs(
                FrameworkElement.LoadedEvent,
                _fanRuntime));
        }
        else
        {
            if (Report.IsAvailable(FeatureIds.TemperatureMonitoring))
                _temperatureReader = new TemperatureReader();
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);

        await RestoreShutdownPerformanceModeAsync();
        if (Settings.TakeOverFnKeys)
        {
            var error = await _fnKeyManager.StartAsync();
            if (!string.IsNullOrWhiteSpace(error))
            {
                Settings.TakeOverFnKeys = false;
                TrySaveSettingsAfterBackgroundChange(
                    "Fn-key takeover startup rollback");
                SetStatus(L(
                    "Fn 快捷键接管启动失败：",
                    "Fn-key takeover could not be started: ") + error);
            }
        }
        await ApplyPendingGpuModeAsync();
        await RefreshAsync(force: true);
        _ = RefreshWarrantyAsync();
        _pollTimer.Start();
        SyncPowerSettingsLockTimer();
    }

    public void AttachWindow(ToolkitMainWindow window, bool startToTrayRequested)
    {
        _window = window;
        InitializeTray();
        if (startToTrayRequested && Settings.StartWithWindows && Settings.StartToTray)
        {
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(HideToTray));
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.ShowInTaskbar = true;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void HideToTray()
    {
        if (_window is null)
            return;
        _window.ShowInTaskbar = false;
        _window.Hide();
    }

    public void RequestExit()
    {
        ExitRequested = true;
        _window?.Close();
    }

    internal void PrepareForSystemShutdown(ReasonSessionEnding reason)
    {
        if (Interlocked.Exchange(ref _systemSessionEnding, 1) != 0)
            return;

        ExitRequested = true;
        _pollTimer.Stop();
        _powerSettingsLockTimer.Stop();
        _hybridAutoGpu.Suspend();
        ToolkitLog.Info(
            $"Windows session is ending ({reason}); stopping background controls.");

        if (_persistSystemSessionState &&
            ShouldRecordShutdownPerformanceMode(reason))
            RecordShutdownPerformanceMode();
        var hotkeysRestoreError =
            _fnKeyManager.StopAndRestoreSynchronously(startService: false);
        if (!string.IsNullOrWhiteSpace(hotkeysRestoreError))
        {
            ToolkitLog.Error(
                "Lenovo Hotkeys could not be restored during Windows shutdown.",
                new InvalidOperationException(hotkeysRestoreError));
        }

        var fansRestored = true;
        if (_fanRuntime is not null &&
            !_fanRuntime.RuntimePrepareForSystemShutdown(out var error))
        {
            fansRestored = false;
            ToolkitLog.Error(
                "Firmware automatic fan control could not be restored during Windows shutdown.",
                new InvalidOperationException(error));
        }
        if (fansRestored)
            _fanWatchdog.TryDisarm(out _);

        Snapshot = Snapshot with
        {
            FanControlRunning = false,
            FullSpeed = false,
            FanTarget = null,
            UpdatedAt = DateTimeOffset.Now
        };
        DisplaySettingsController.Shutdown();
        SoundSettingsController.Shutdown();
    }

    public async Task RestoreForExitAsync()
    {
        if (IsSystemSessionEnding)
            return;
        _pollTimer.Stop();
        _powerSettingsLockTimer.Stop();
        var hotkeysRestoreError = await _fnKeyManager.StopAsync(
            restoreLenovoHotkeys: true);
        if (!string.IsNullOrWhiteSpace(hotkeysRestoreError))
        {
            ToolkitLog.Warning(
                "Lenovo Hotkeys could not be restored before exit: " +
                hotkeysRestoreError);
        }
        if (_fanRuntime is not null)
            await _fanRuntime.RuntimeRestoreFirmwareAutoAsync();
        _fanWatchdog.TryDisarm(out _);
    }

    public async Task<PowerSettingsState> ReadPowerSettingsAsync()
    {
        await _powerSettingsGate.WaitAsync();
        try
        {
            return await Task.Run(PowerSettingsController.ReadState);
        }
        finally
        {
            _powerSettingsGate.Release();
        }
    }

    public async Task<PowerSettingsState> ApplyPowerSettingsAsync(
        PowerSettingsState state)
    {
        await _powerSettingsGate.WaitAsync();
        try
        {
            var confirmed = await Task.Run(
                () => PowerSettingsController.WriteAndReadState(state));
            var modeLock = CurrentPowerModeLock(create: false);
            if (modeLock?.Locks is { Any: true })
            {
                modeLock.Target = confirmed;
                Settings.PowerSettingsLocks = modeLock.Locks;
                Settings.PowerSettingsLockTarget = confirmed;
                try
                {
                    CurveProfileStore.SaveSettings(Settings);
                }
                catch (Exception ex)
                {
                    SetStatus(L(
                        "功耗设置已应用，但新的锁定目标无法保存：",
                        "Power settings were applied, but the new lock target could not be saved: ") + ex.Message);
                }
            }
            return confirmed;
        }
        finally
        {
            _powerSettingsGate.Release();
        }
    }

    public Task<GpuWorkerCommandResponse>
        QueryDiscreteGpuApplicationsAsync() =>
        Task.Run(() => _fanRuntime is not null
            ? _fanRuntime.RuntimeQueryDiscreteGpuApplications()
            : _temperatureReader?.QueryDiscreteGpuApplications() ??
              GpuWorkerCommandResponse.Failure(
                  L("独立显卡监控不可用。", "Discrete GPU monitoring is unavailable.")));

    public async Task<GpuWorkerCommandResponse>
        KillDiscreteGpuApplicationsAsync()
    {
        var result = await Task.Run(() => _fanRuntime is not null
            ? _fanRuntime.RuntimeKillDiscreteGpuApplications()
            : _temperatureReader?.KillDiscreteGpuApplications() ??
              GpuWorkerCommandResponse.Failure(
                  L("独立显卡监控不可用。", "Discrete GPU monitoring is unavailable.")));
        if (result.AffectedProcesses > 0)
            await RefreshAsync(force: true);
        return result;
    }

    public async Task<string?> SetGpuOverclockEnabledAsync(bool enabled)
    {
        if (Snapshot.Temperatures?.DiscreteGpuState ==
            DiscreteGpuActivityState.Off)
        {
            return L(
                "独立显卡已关闭，无法更改超频状态。",
                "The discrete GPU is off, so its overclock state cannot be changed.");
        }

        var previous = GpuOverclockPolicy.Normalize(Settings.GpuOverclock);
        var result = enabled
            ? await ExecuteGpuOverclockAsync(previous, force: true)
            : await ResetGpuOverclockAsync();
        if (!result.Success)
            return result.Error;

        previous.Enabled = enabled;
        try
        {
            Settings.GpuOverclock = previous;
            CurveProfileStore.SaveSettings(Settings);
            _nextGpuOverclockRetry = DateTimeOffset.MinValue;
            return null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("GPU overclock state could not be saved.", ex);
            return ex.Message;
        }
    }

    public async Task<string?> SaveGpuOverclockSettingsAsync(
        GpuOverclockSettings settings,
        bool applyEvenIfDisabled = false)
    {
        if (!GpuOverclockPolicy.TryValidate(settings, out var error))
            return error;
        settings = GpuOverclockPolicy.Normalize(settings);

        var enabled = Settings.GpuOverclock.Enabled;
        settings.Enabled = enabled;
        if (enabled || applyEvenIfDisabled)
        {
            if (Snapshot.Temperatures?.DiscreteGpuState ==
                DiscreteGpuActivityState.Off)
            {
                return L(
                    "独立显卡已关闭，当前设置无法应用。",
                    "The discrete GPU is off, so these settings cannot be applied.");
            }
            var result = await ExecuteGpuOverclockAsync(
                settings,
                force: true);
            if (!result.Success)
                return result.Error;
        }

        var previous = Settings.GpuOverclock;
        try
        {
            Settings.GpuOverclock = settings;
            CurveProfileStore.SaveSettings(Settings);
            _nextGpuOverclockRetry = DateTimeOffset.MinValue;
            return null;
        }
        catch (Exception ex)
        {
            Settings.GpuOverclock = previous;
            ToolkitLog.Error("GPU overclock settings could not be saved.", ex);
            return ex.Message;
        }
    }

    private Task<GpuWorkerCommandResponse> ExecuteGpuOverclockAsync(
        GpuOverclockSettings settings,
        bool force = false) =>
        Task.Run(() => _fanRuntime is not null
            ? _fanRuntime.RuntimeApplyGpuOverclock(settings, force)
            : _temperatureReader?.ApplyGpuOverclock(settings, force) ??
              GpuWorkerCommandResponse.Failure(
                  L("独立显卡监控不可用。", "Discrete GPU monitoring is unavailable.")));

    private Task<GpuWorkerCommandResponse> ResetGpuOverclockAsync() =>
        Task.Run(() => _fanRuntime is not null
            ? _fanRuntime.RuntimeResetGpuOverclock()
            : _temperatureReader?.ResetGpuOverclock() ??
              GpuWorkerCommandResponse.Failure(
                  L("独立显卡监控不可用。", "Discrete GPU monitoring is unavailable.")));

    public bool TrySetPowerSettingLock(
        PowerSetting setting,
        bool enabled,
        PowerSettingsState? target,
        out string? error)
    {
        if (enabled &&
            (!PowerSettingsController.IsValidState(target) ||
             setting == PowerSetting.Atpp && !target!.Atpp.HasValue))
        {
            error = L(
                "请先成功读取当前功耗设置。",
                "Read the current power settings successfully before enabling the lock.");
            return false;
        }

        var modeLock = CurrentPowerModeLock(create: enabled);
        if (modeLock is null)
        {
            error = L(
                "无法确定当前性能模式。",
                "The current performance mode could not be determined.");
            return false;
        }
        var previousSelection = modeLock.Locks with { };
        var previousTarget = modeLock.Target;
        var selection = previousSelection.With(setting, enabled);
        modeLock.Locks = selection;
        if (enabled)
        {
            modeLock.Target =
                PowerSettingsController.IsValidState(previousTarget)
                    ? PowerSettingsController.WithSetting(
                        previousTarget!,
                        target!,
                        setting)
                    : target;
        }
        else if (!selection.Any)
        {
            modeLock.Target = null;
        }
        Settings.PowerSettingsLocks = modeLock.Locks;
        Settings.PowerSettingsLockTarget = modeLock.Target;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            SyncPowerSettingsLockTimer();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            modeLock.Locks = previousSelection;
            modeLock.Target = previousTarget;
            Settings.PowerSettingsLocks = previousSelection;
            Settings.PowerSettingsLockTarget = previousTarget;
            SyncPowerSettingsLockTimer();
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetPowerSettingsLockInterval(
        int seconds,
        out string? error)
    {
        if (!PowerSettingsController.IsSupportedLockInterval(seconds))
        {
            error = L(
                "功耗锁定间隔无效。",
                "The power lock interval is invalid.");
            return false;
        }

        var previous = Settings.PowerSettingsLockIntervalSeconds;
        Settings.PowerSettingsLockIntervalSeconds = seconds;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            SyncPowerSettingsLockTimer();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.PowerSettingsLockIntervalSeconds = previous;
            SyncPowerSettingsLockTimer();
            error = ex.Message;
            return false;
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_polling || _disposed)
            return;
        ItsMode? modeToLink = null;
        int? modeToLinkGeneration = null;
        _polling = true;
        try
        {
            await RefreshHybridGpuProtectionAsync(forceGpuModeRefresh: force);

            if (_fanRuntime is not null && force)
                await _fanRuntime.RuntimeRefreshAsync();

            var performance = _fanRuntime?.RuntimeSnapshot();
            var temperatures = performance?.Temperatures;
            var fans = performance?.Fans;
            if (temperatures is null && _temperatureReader is not null)
                temperatures = await Task.Run(_temperatureReader.Read);
            if (Settings.GpuOverclock.Enabled &&
                GpuTelemetryControl.Mode == GpuTelemetryMode.Full &&
                (temperatures?.DiscreteGpuState is
                    DiscreteGpuActivityState.Active or
                    DiscreteGpuActivityState.Inactive) &&
                DateTimeOffset.UtcNow >= _nextGpuOverclockRetry)
            {
                var result = await ExecuteGpuOverclockAsync(
                    Settings.GpuOverclock);
                if (!result.Success)
                {
                    ToolkitLog.Warning(
                        "Saved GPU overclock settings could not be reapplied: " +
                        result.Error);
                    _nextGpuOverclockRetry =
                        DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
            var now = DateTimeOffset.UtcNow;
            BatteryInformationSnapshot? battery = _cachedBattery;
            if (Report?.IsAvailable(FeatureIds.BatteryInformation) == true)
            {
                if (force ||
                    _cachedBattery is null ||
                    now - _lastBatteryRefresh >= TimeSpan.FromSeconds(10))
                {
                    _cachedBattery = await Task.Run(BatteryInformationReader.Read);
                    _lastBatteryRefresh = now;
                }
                battery = _cachedBattery;
            }
            if (Report?.IsAvailable(FeatureIds.PowerSettings) == true)
            {
                try
                {
                    _cachedPowerSettings = await Task.Run(
                        PowerSettingsController.ReadState);
                }
                catch (Exception ex)
                {
                    ToolkitLog.Warning("Power values could not be refreshed: " + ex.Message);
                }
            }

            var itsMode = _confirmedPerformanceModeDuringRefresh ??
                          ItsMode.Unknown;
            if (!_confirmedPerformanceModeDuringRefresh.HasValue &&
                Report?.IsAvailable(FeatureIds.PerformanceMode) == true)
            {
                itsMode = await Task.Run(() => new ItsModeDetector().ReadMode());
            }
            if (itsMode == ItsMode.Unknown)
                itsMode = performance?.ItsMode ?? ItsMode.Unknown;

            GpuWorkingMode? gpuMode = performance?.GpuMode;
            IReadOnlyList<GpuWorkingMode> gpuModes =
                _cachedGpuMode?.SupportedModes ?? [];
            if (Report?.IsAvailable(FeatureIds.GpuMode) == true)
            {
                if (_cachedGpuMode is null ||
                    now - _lastGpuRefresh >= TimeSpan.FromSeconds(5))
                {
                    _cachedGpuMode = await Task.Run(GpuModeController.ReadState);
                    _lastGpuRefresh = now;
                }
                var gpu = _cachedGpuMode!;
                gpuMode = gpu.CurrentMode;
                gpuModes = gpu.SupportedModes;
            }

            if (battery is not null)
                await _hybridAutoGpu.UpdateAsync(gpuMode, battery.IsAcConnected);
            else
                await _hybridAutoGpu.ObserveAsync();

            Snapshot = new(
                temperatures,
                fans,
                battery,
                itsMode,
                gpuMode,
                gpuModes,
                performance?.IsRunning == true,
                performance?.IsFullSpeed == true,
                performance?.Strategy ?? Settings.ControlStrategy,
                performance?.Target,
                Settings.PendingGpuMode,
                DateTimeOffset.Now,
                null)
            {
                PendingGpuModeSource = Settings.PendingGpuModeSource,
                PowerSettings = _cachedPowerSettings,
                Warranty = _cachedWarranty
            };
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            UpdateTrayText();
            SyncPowerSettingsLockTimer();
            if (itsMode != ItsMode.Unknown &&
                itsMode != _lastFanLinkedPerformanceMode)
            {
                _lastFanLinkedPerformanceMode = itsMode;
                modeToLink = itsMode;
                modeToLinkGeneration = ++_fanPerformanceModeChangeGeneration;
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Runtime status refresh failed.", ex);
            Snapshot = Snapshot with
            {
                UpdatedAt = DateTimeOffset.Now,
                Error = ex.Message
            };
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            SetStatus(L("状态刷新失败：", "Refresh failed: ") + ex.Message);
        }
        finally
        {
            _polling = false;
        }
        if (modeToLink.HasValue && modeToLinkGeneration.HasValue)
        {
            _ = ApplyFanStrategyForPerformanceModeAsync(
                modeToLink.Value,
                modeToLinkGeneration.Value);
        }
    }

    private async Task RefreshWarrantyAsync()
    {
        if (_disposed ||
            Report?.IsAvailable(FeatureIds.WarrantyInformation) != true)
            return;
        try
        {
            _cachedWarranty = await WarrantyService.GetWarrantyAsync(
                CancellationToken.None);
            Snapshot = Snapshot with { Warranty = _cachedWarranty };
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning("Warranty information could not be refreshed: " + ex.Message);
        }
    }

    private async Task RestoreShutdownPerformanceModeAsync()
    {
        if (!_launchedAtStartup ||
            !PerformanceModeCycle.TryParseSelectableMode(
                Settings.ShutdownPerformanceMode,
                out var savedMode))
        {
            return;
        }

        var current = await Task.Run(() => new ItsModeDetector().ReadMode());
        string? error = null;
        if (current != savedMode)
            error = await SetItsModeAsync(savedMode);
        if (!string.IsNullOrWhiteSpace(error))
        {
            ToolkitLog.Warning(
                $"The shutdown performance mode {savedMode} could not be restored: {error}");
            return;
        }

        Settings.ShutdownPerformanceMode = string.Empty;
        TrySaveSettingsAfterBackgroundChange(
            "shutdown performance-mode restore");
        ToolkitLog.Info(
            $"Restored the shutdown performance mode: {savedMode}.");
    }

    private void RecordShutdownPerformanceMode()
    {
        var mode = Snapshot.ItsMode;
        if (!PerformanceModeCycle.IsSelectableMode(mode))
        {
            try
            {
                mode = new ItsModeDetector().ReadMode();
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "The performance mode could not be read during shutdown: " +
                    ex.Message);
            }
        }
        if (!PerformanceModeCycle.IsSelectableMode(mode))
            return;
        Settings.ShutdownPerformanceMode = mode.ToString();
        TrySaveSettingsAfterBackgroundChange(
            "shutdown performance-mode capture");
        ToolkitLog.Info(
            $"Recorded the shutdown performance mode: {mode}.");
    }

    internal static bool ShouldRecordShutdownPerformanceMode(
        ReasonSessionEnding reason) =>
        reason == ReasonSessionEnding.Shutdown;

    private void TrySaveSettingsAfterBackgroundChange(string operation)
    {
        try
        {
            CurveProfileStore.SaveSettings(Settings);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                $"Settings could not be saved after {operation}.",
                ex);
        }
    }

    public async Task<string?> SetItsModeAsync(ItsMode mode)
    {
        if (!CanSwitchItsMode)
        {
            return L(
                "当前设备只能读取性能模式，无法通过 Toolkit 切换",
                "This device exposes performance mode as read-only to Toolkit");
        }
        if (!IsItsModeSupported(mode))
        {
            return L(
                "当前性能模式接口不支持所选模式",
                "The selected mode is unsupported by the active performance-mode interface");
        }
        var isAcConnected = Snapshot.Battery?.IsAcConnected;
        if (BatteryInformationReader.TryGetAcConnectionState(
                out var currentAcState))
        {
            isAcConnected = currentAcState;
        }
        if (!PerformanceModeAvailability.CanSelect(
                mode,
                isAcConnected))
        {
            return L(
                "使用电池时无法选择极客模式",
                "Geek mode is unavailable while running on battery");
        }
        try
        {
            await Task.Run(() => ItsModeController.SetMode(mode));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await Task.Run(() => new ItsModeDetector().ReadMode()) == mode)
                {
                    Snapshot = Snapshot with { ItsMode = mode };
                    SnapshotChanged?.Invoke(this, EventArgs.Empty);
                    _confirmedPerformanceModeDuringRefresh = mode;
                    _lastFanLinkedPerformanceMode = mode;
                    var generation = ++_fanPerformanceModeChangeGeneration;
                    _ = ApplyFanStrategyForPerformanceModeAsync(
                        mode,
                        generation);
                    try
                    {
                        await RefreshAsync(force: true);
                    }
                    finally
                    {
                        _confirmedPerformanceModeDuringRefresh = null;
                    }
                    SyncPowerSettingsLockTimer();
                    await EnforcePowerSettingsLockAsync();
                    ToolkitLog.Info(
                        $"Performance mode switch confirmed: {mode} via " +
                        $"{new ItsModeDetector().DetectSwitchBackend()}.");
                    return null;
                }
                await Task.Delay(200);
            }
            return L("固件未确认新的性能模式", "Firmware did not confirm the new mode");
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Performance-mode switch failed.", ex);
            return ex.Message;
        }
    }

    internal async Task TogglePerformanceModeFromFnAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null &&
            !dispatcher.HasShutdownStarted &&
            !dispatcher.CheckAccess())
        {
            await dispatcher
                .InvokeAsync(TogglePerformanceModeFromFnAsync)
                .Task
                .Unwrap();
            return;
        }
        if (!CanSwitchItsMode)
            return;
        var isAcConnected = Snapshot.Battery?.IsAcConnected ?? true;
        if (BatteryInformationReader.TryGetAcConnectionState(
                out var currentAcState))
        {
            isAcConnected = currentAcState;
        }
        var current = Snapshot.ItsMode;
        if (!PerformanceModeCycle.IsSelectableMode(current))
            current = await Task.Run(() => new ItsModeDetector().ReadMode());
        var next = PerformanceModeCycle.Next(
            Settings.FnPerformanceModeOrder.Where(IsItsModeSupported),
            Settings.FnPerformanceModeEnabled.Where(IsItsModeSupported),
            current,
            isAcConnected);
        if (!PerformanceModeCycle.IsSelectableMode(next))
            return;
        ShowFnKeyNotification(
            string.Empty,
            PerformanceModeDisplayName(next));
        var error = await SetItsModeAsync(next);
        if (!string.IsNullOrWhiteSpace(error))
        {
            SetStatus(
                L("性能模式切换失败：", "Performance-mode switch failed: ") +
                error);
        }
    }

    internal void ShowFnKeyNotification(string title, string detail)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() =>
                ShowFnKeyNotification(title, detail)));
            return;
        }
        if (_fnKeyNotification is null || _fnKeyNotificationDark != IsDark)
        {
            try { _fnKeyNotification?.Close(); } catch { }
            _fnKeyNotification = new FnKeyNotificationWindow(IsDark);
            _fnKeyNotificationDark = IsDark;
        }
        _fnKeyNotification.ShowTemporarily(title, detail);
    }

    internal async Task<string?> SetFnKeyTakeoverAsync(bool enabled)
    {
        if (enabled == Settings.TakeOverFnKeys &&
            enabled == _fnKeyManager.IsRunning)
        {
            return null;
        }
        if (enabled)
        {
            var startError = await _fnKeyManager.StartAsync();
            if (!string.IsNullOrWhiteSpace(startError))
                return startError;
            Settings.TakeOverFnKeys = true;
            try
            {
                CurveProfileStore.SaveSettings(Settings);
            }
            catch (Exception ex)
            {
                Settings.TakeOverFnKeys = false;
                await _fnKeyManager.StopAsync(restoreLenovoHotkeys: true);
                return ex.Message;
            }
        }
        else
        {
            var stopError = await _fnKeyManager.StopAsync(
                restoreLenovoHotkeys: true);
            if (!string.IsNullOrWhiteSpace(stopError))
                return stopError;
            Settings.TakeOverFnKeys = false;
            try
            {
                CurveProfileStore.SaveSettings(Settings);
            }
            catch (Exception ex)
            {
                Settings.TakeOverFnKeys = true;
                await _fnKeyManager.StartAsync();
                return ex.Message;
            }
        }
        FnKeyTakeoverChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    internal bool TrySetPerformanceModeOrder(
        IReadOnlyList<ItsMode> order,
        out string? error)
        => TrySetPerformanceModeConfiguration(
            order,
            Settings.FnPerformanceModeEnabled,
            out error);

    internal bool TrySetPerformanceModeConfiguration(
        IReadOnlyList<ItsMode> order,
        IReadOnlyCollection<ItsMode> enabled,
        out string? error)
    {
        if (!enabled.Any(PerformanceModeCycle.IsSelectableMode))
        {
            error = L(
                "至少需要启用一个性能模式。",
                "At least one performance mode must remain enabled.");
            return false;
        }
        var previousOrder = Settings.FnPerformanceModeOrder;
        var previousEnabled = Settings.FnPerformanceModeEnabled;
        Settings.FnPerformanceModeOrder =
            PerformanceModeCycle.NormalizeOrder(order);
        Settings.FnPerformanceModeEnabled =
            PerformanceModeCycle.NormalizeEnabled(enabled);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.FnPerformanceModeOrder = previousOrder;
            Settings.FnPerformanceModeEnabled = previousEnabled;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySetLockKeyOsd(
        InputSettingKind kind,
        bool enabled,
        out string? error)
    {
        if (kind is not InputSettingKind.CapsLockOsd and
            not InputSettingKind.NumLockOsd)
        {
            error = "The requested setting is not a lock-key OSD setting.";
            return false;
        }
        var previous = kind == InputSettingKind.CapsLockOsd
            ? Settings.ShowCapsLockOsd
            : Settings.ShowNumLockOsd;
        if (kind == InputSettingKind.CapsLockOsd)
            Settings.ShowCapsLockOsd = enabled;
        else
            Settings.ShowNumLockOsd = enabled;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (kind == InputSettingKind.CapsLockOsd)
                Settings.ShowCapsLockOsd = previous;
            else
                Settings.ShowNumLockOsd = previous;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySetRefreshRateCycle(
        IEnumerable<uint> rates,
        out string? error)
    {
        var normalized = RefreshRateController.NormalizeConfiguredRates(
            rates);
        if (normalized.Count == 0)
        {
            error = L(
                "至少需要启用一个刷新率。",
                "At least one refresh rate must remain enabled.");
            return false;
        }
        var previous = Settings.RefreshRateCycleHz;
        Settings.RefreshRateCycleHz = normalized;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.RefreshRateCycleHz = previous;
            error = ex.Message;
            return false;
        }
    }

    private string PerformanceModeDisplayName(ItsMode mode) => mode switch
    {
        ItsMode.PowerSaving => L("省电模式", "Cool"),
        ItsMode.Intelligent => L("智能模式", "Auto"),
        ItsMode.Performance => L("性能模式", "Performance"),
        ItsMode.Geek => L("极客模式", "Geek"),
        _ => L("未知", "Unknown")
    };

    public async Task<string?> SetGpuModeAsync(GpuWorkingMode target)
    {
        try
        {
            var hasCurrentBootTarget =
                GpuModeRestartState.TryGetCurrentBootTarget(
                    Settings,
                    _bootSessionId,
                    out var pendingTarget);
            var hasCurrentBootTransition =
                GpuModeRestartState.TryGetCurrentBootTransition(
                    Settings,
                    _bootSessionId,
                    out var transition);

            if (hasCurrentBootTarget && target == pendingTarget)
            {
                PublishGpuTransitionState();
                await RefreshAsync(force: true);
                return null;
            }

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
            var requiresRestart = await Task.Run(() =>
                GpuModeController.SetModeFromEffectiveState(
                    source,
                    sourceUsesDirectGraphicsConfiguration,
                    target));
            if (requiresRestart)
            {
                GpuModeRestartState.MarkPending(
                    Settings,
                    source,
                    sourceUsesDirectGraphicsConfiguration,
                    target,
                    _bootSessionId);
            }
            else
            {
                GpuModeRestartState.Clear(Settings);
            }
            CurveProfileStore.SaveSettings(Settings);
            PublishGpuTransitionState();
            await RefreshAsync(force: true);
            return null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("GPU working-mode switch failed.", ex);
            return ex.Message;
        }
    }

    public async Task<string?> SetFanControlAsync(bool enabled)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");
        try
        {
            async Task<string?> ApplyAsync()
            {
                await _fanRuntime.RuntimeSetControlEnabledAsync(enabled);
                await RefreshAsync(force: true);
                return null;
            }

            if (!enabled)
                return await ApplyAsync();

            var state = _fanRuntime.RuntimeSnapshot();
            var mode = state.Strategy switch
            {
                ControlStrategy.FanCurve => FanControlMode.FanCurve,
                ControlStrategy.AdvancedCurve =>
                    FanControlMode.AdvancedCurve,
                _ => FanControlMode.FixedRpm
            };
            return await RunFanActionAfterLinkedPerformanceModeAsync(
                _fanRuntime.RuntimeWouldUseFanControl(
                    mode,
                    Snapshot.ItsMode),
                ApplyAsync);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> SetFanModeAsync(FanControlMode mode)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");

        if (mode == FanControlMode.FirmwareAutomatic)
            return await RestoreFirmwareAutoAsync();

        return await RunFanActionAfterLinkedPerformanceModeAsync(
            _fanRuntime.RuntimeWouldUseFanControl(mode, Snapshot.ItsMode),
            () => ApplyFanModeCoreAsync(mode));
    }

    private async Task<string?> ApplyFanModeCoreAsync(FanControlMode mode)
    {
        var fanRuntime = _fanRuntime;
        if (fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");

        try
        {
            if (Snapshot.FanControlRunning || Snapshot.FullSpeed)
                await fanRuntime.RuntimeRestoreFirmwareAutoAsync();

            var strategy = mode switch
            {
                FanControlMode.FanCurve => ControlStrategy.FanCurve,
                FanControlMode.AdvancedCurve => ControlStrategy.AdvancedCurve,
                _ => ControlStrategy.FixedRpm
            };
            if (!fanRuntime.RuntimeSetStrategy(strategy))
            {
                return L(
                    "后台未接受新的控制策略。",
                    "The background controller did not accept the new strategy.");
            }

            Settings.FanCurveWarningAccepted |=
                strategy is ControlStrategy.FanCurve or
                    ControlStrategy.AdvancedCurve;
            CurveProfileStore.SaveSettings(Settings);
            await fanRuntime.RuntimeSetControlEnabledAsync(true);
            await RefreshAsync(force: true);
            return null;
        }
        catch (Exception ex)
        {
            await RefreshAsync(force: true);
            return ex.Message;
        }
    }

    public async Task<string?> SelectFanProfileAsync(int index)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");

        try
        {
            _fanRuntime.RuntimeSelectProfile(index);
            var runtimeState = _fanRuntime.RuntimeSnapshot();
            if (runtimeState.IsRunning &&
                runtimeState.Strategy == ControlStrategy.FanCurve)
            {
                await _fanRuntime.RuntimeRefreshAsync();
            }
            await RefreshAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> SetFanRpmLimitsAsync(FanRpmLimits limits)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");
        if (Snapshot.FullSpeed)
        {
            return L(
                "请先关闭风扇拉满，再调整转速上下限。",
                "Turn off full fan speed before changing RPM limits.");
        }

        var resumeControl = Snapshot.FanControlRunning;
        var resumeStrategy = Snapshot.FanStrategy;
        try
        {
            if (resumeControl)
                await _fanRuntime.RuntimeRestoreFirmwareAutoAsync();

            _fanRuntime.RuntimeSetFanRpmLimits(limits);

            if (resumeControl)
            {
                if (!_fanRuntime.RuntimeSetStrategy(resumeStrategy))
                    throw new InvalidOperationException(L(
                        "无法恢复原风扇控制策略。",
                        "Could not restore the previous fan strategy."));
                await _fanRuntime.RuntimeSetControlEnabledAsync(true);
            }

            await RefreshAsync(force: true);
            return null;
        }
        catch (Exception ex)
        {
            if (resumeControl)
            {
                try
                {
                    if (_fanRuntime.RuntimeSetStrategy(resumeStrategy))
                        await _fanRuntime.RuntimeSetControlEnabledAsync(true);
                }
                catch
                {
                }
            }
            await RefreshAsync(force: true);
            return ex.Message;
        }
    }

    public async Task<string?> RestoreFirmwareAutoAsync()
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");
        try
        {
            await _fanRuntime.RuntimeRestoreFirmwareAutoAsync();
            await RefreshAsync(force: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> SetFullSpeedAsync(bool enabled)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");
        try
        {
            async Task<string?> ApplyAsync()
            {
                await _fanRuntime.RuntimeSetFullSpeedAsync(enabled);
                await RefreshAsync(force: true);
                return null;
            }

            return enabled
                ? await RunFanActionAfterLinkedPerformanceModeAsync(
                    willUseFanControl: true,
                    ApplyAsync)
                : await ApplyAsync();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal StartupLaunchMode CurrentStartupMode =>
        !Settings.StartWithWindows
            ? StartupLaunchMode.Disabled
            : Settings.DelayStartup
                ? StartupLaunchMode.Delayed
                : StartupLaunchMode.Enabled;

    public bool TrySetStartupMode(
        StartupLaunchMode value,
        out string? error)
    {
        var oldEnabled = Settings.StartWithWindows;
        var oldDelayed = Settings.DelayStartup;
        Settings.StartWithWindows = value != StartupLaunchMode.Disabled;
        Settings.DelayStartup = value == StartupLaunchMode.Delayed;
        error = MainWindow.ApplyStartupTaskSetting(Settings);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Settings.StartWithWindows = oldEnabled;
            Settings.DelayStartup = oldDelayed;
            var rollbackError = MainWindow.ApplyStartupTaskSetting(Settings);
            if (!string.IsNullOrWhiteSpace(rollbackError))
                error += L("；系统任务回滚失败：", "; scheduled-task rollback failed: ") + rollbackError;
            return false;
        }
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            return true;
        }
        catch (Exception ex)
        {
            Settings.StartWithWindows = oldEnabled;
            Settings.DelayStartup = oldDelayed;
            var rollbackError = MainWindow.ApplyStartupTaskSetting(Settings);
            error = ex.Message + (string.IsNullOrWhiteSpace(rollbackError)
                ? string.Empty
                : L("；系统任务回滚失败：", "; scheduled-task rollback failed: ") + rollbackError);
            return false;
        }
    }

    public bool TrySetStartWithWindows(bool value, out string? error) =>
        TrySetStartupMode(
            value ? StartupLaunchMode.Enabled : StartupLaunchMode.Disabled,
            out error);

    public bool TrySetStartToTray(bool value, out string? error)
    {
        var old = Settings.StartToTray;
        Settings.StartToTray = value;
        error = MainWindow.ApplyStartupTaskSetting(Settings);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Settings.StartToTray = old;
            var rollbackError = MainWindow.ApplyStartupTaskSetting(Settings);
            if (!string.IsNullOrWhiteSpace(rollbackError))
                error += L("；系统任务回滚失败：", "; scheduled-task rollback failed: ") + rollbackError;
            return false;
        }
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            return true;
        }
        catch (Exception ex)
        {
            Settings.StartToTray = old;
            var rollbackError = MainWindow.ApplyStartupTaskSetting(Settings);
            error = ex.Message + (string.IsNullOrWhiteSpace(rollbackError)
                ? string.Empty
                : L("；系统任务回滚失败：", "; scheduled-task rollback failed: ") + rollbackError);
            return false;
        }
    }

    public bool TrySetMinimizeToTray(bool value, out string? error) =>
        TrySaveSetting(
            () => Settings.MinimizeToTray,
            current => Settings.MinimizeToTray = current,
            value,
            out error);

    public bool TrySetCloseToTray(bool value, out string? error) =>
        TrySaveSetting(
            () => Settings.CloseToTray,
            current => Settings.CloseToTray = current,
            value,
            out error);

    public bool TrySetAlternativeFullSpeedMethod(bool value, out string? error) =>
        TrySaveSetting(
            () => Settings.UseAlternativeFullSpeedMethod,
            current => Settings.UseAlternativeFullSpeedMethod = current,
            value,
            out error);

    public bool TrySetContinuouslyWriteFanTargets(bool value, out string? error) =>
        TrySaveSetting(
            () => Settings.ContinuouslyWriteFanTargets,
            current => Settings.ContinuouslyWriteFanTargets = current,
            value,
            out error);

    public bool TrySetOverviewLayout(
        OverviewLayoutSettings layout,
        out string? error)
    {
        var previous = Settings.OverviewLayout;
        Settings.OverviewLayout = OverviewLayoutDefaults.Normalize(layout);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            OverviewLayoutChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.OverviewLayout = previous;
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetOverviewPageMode(
        OverviewPageMode mode,
        out string? error)
    {
        if (!Enum.IsDefined(mode))
        {
            error = L("概览页模式无效。", "The overview mode is invalid.");
            return false;
        }
        var previous = Settings.OverviewPageMode;
        Settings.OverviewPageMode = mode;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            OverviewLayoutChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.OverviewPageMode = previous;
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetPerformanceFanLink(
        PerformanceFanLinkSettings value,
        out string? error)
    {
        var previous = Settings.PerformanceFanLink;
        Settings.PerformanceFanLink =
            PerformanceFanLinkDefaults.Normalize(value);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.PerformanceFanLink = previous;
            error = ex.Message;
            return false;
        }
    }

    private async Task ApplyFanStrategyForPerformanceModeAsync(
        ItsMode mode,
        int generation)
    {
        try
        {
            await ApplyFanStrategyForPerformanceModeCoreAsync(
                mode,
                generation);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "Delayed performance-mode fan linkage failed.",
                ex);
        }
    }

    private async Task ApplyFanStrategyForPerformanceModeCoreAsync(
        ItsMode mode,
        int generation)
    {
        var initialLink = PerformanceFanLinkDefaults.Normalize(
            Settings.PerformanceFanLink);
        if (_fanPerformanceLinkBusy ||
            !initialLink.SwitchFanStrategyWithPerformanceMode ||
            Array.IndexOf(
                PerformanceFanLinkDefaults.SupportedModes,
                mode) < 0 ||
            _fanRuntime is null)
        {
            return;
        }

        ToolkitLog.Info(
            $"Performance mode {mode} was detected; delaying its linked fan strategy by {PerformanceFanStrategyApplyDelay.TotalSeconds:0} seconds.");
        await Task.Delay(PerformanceFanStrategyApplyDelay);
        if (_disposed ||
            generation != _fanPerformanceModeChangeGeneration ||
            _fanPerformanceLinkBusy)
        {
            return;
        }
        if (!await IsPerformanceModeConfirmedAsync(mode))
        {
            ToolkitLog.Warning(
                $"Linked fan strategy for {mode} was cancelled because the performance mode was no longer confirmed after the delay.");
            return;
        }

        var link = PerformanceFanLinkDefaults.Normalize(
            Settings.PerformanceFanLink);
        if (!link.SwitchFanStrategyWithPerformanceMode)
            return;

        var selection = PerformanceFanLinkDefaults.SelectionFor(link, mode);
        _fanPerformanceLinkBusy = true;
        try
        {
            if (selection.Mode == FanControlMode.FanCurve)
            {
                var profileError = await SelectFanProfileAsync(
                    selection.ProfileIndex);
                if (!string.IsNullOrWhiteSpace(profileError))
                {
                    ToolkitLog.Warning(
                        "Linked fan profile could not be selected: " +
                        profileError);
                    return;
                }
            }
            var error = selection.Mode == FanControlMode.FirmwareAutomatic
                ? await RestoreFirmwareAutoAsync()
                : await ApplyFanModeCoreAsync(selection.Mode);
            if (!string.IsNullOrWhiteSpace(error))
            {
                ToolkitLog.Warning(
                    "Linked fan strategy could not be applied: " + error);
                SetStatus(L(
                    "性能模式已切换，但关联的风扇策略未能应用：",
                    "The performance mode changed, but its linked fan strategy could not be applied: ") + error);
            }
        }
        finally
        {
            _fanPerformanceLinkBusy = false;
        }
    }

    private async Task<string?> RunFanActionAfterLinkedPerformanceModeAsync(
        bool willUseFanControl,
        Func<Task<string?>> applyFanAction)
    {
        var link = PerformanceFanLinkDefaults.Normalize(
            Settings.PerformanceFanLink);
        if (link.FanControlTargetMode == ItsMode.Unknown ||
            !willUseFanControl)
        {
            return await applyFanAction();
        }

        var current = Snapshot.ItsMode;
        if (PerformanceFanLinkDefaults.IsNoSwitchMode(link, current))
            return await applyFanAction();

        if (_fanPerformanceLinkBusy)
        {
            return L(
                "另一个性能模式与风扇联动操作正在进行，请稍候。",
                "Another performance-mode and fan linkage operation is in progress.");
        }

        _fanPerformanceLinkBusy = true;
        try
        {
            var error = await SetItsModeAsync(link.FanControlTargetMode);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return L(
                    "关联的性能模式未能应用，风扇设置未更改：",
                    "The linked performance mode could not be applied, so the fan setting was not changed: ") + error;
            }

            ToolkitLog.Info(
                $"Linked performance mode {link.FanControlTargetMode} was confirmed; delaying the requested fan action by {PerformanceFanStrategyApplyDelay.TotalSeconds:0} seconds.");
            await Task.Delay(PerformanceFanStrategyApplyDelay);
            var currentLink = PerformanceFanLinkDefaults.Normalize(
                Settings.PerformanceFanLink);
            if (_disposed ||
                currentLink.FanControlTargetMode !=
                    link.FanControlTargetMode ||
                !await IsPerformanceModeConfirmedAsync(
                    link.FanControlTargetMode))
            {
                return L(
                    "等待期间性能模式发生变化，风扇设置未更改。",
                    "The performance mode changed during the delay, so the fan setting was not changed.");
            }

            return await applyFanAction();
        }
        finally
        {
            _fanPerformanceLinkBusy = false;
        }
    }

    private static async Task<bool> IsPerformanceModeConfirmedAsync(
        ItsMode expectedMode)
    {
        try
        {
            return await Task.Run(
                       () => new ItsModeDetector().ReadMode()) ==
                   expectedMode;
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"Performance mode could not be confirmed before applying a linked fan action: {ex.Message}");
            return false;
        }
    }

    internal bool IsUsingFanControl() => IsUsingFanControl(
        Snapshot,
        FanControlSemantics);

    internal static bool IsUsingFanControl(
        ToolkitRuntimeSnapshot snapshot,
        FanBackendControlSemantics semantics)
    {
        if (snapshot.FullSpeed)
            return true;
        if (!snapshot.FanControlRunning)
            return false;
        if (snapshot.FanStrategy != ControlStrategy.FixedRpm)
            return true;
        return snapshot.FanTarget is not { Fan1Rpm: 0, Fan2Rpm: 0 } ||
               semantics.ZeroRpmBehavior !=
               FanTargetZeroBehavior.ReleaseFanToFirmwareControl;
    }

    public async Task<string?> RestartDataReadersAsync()
    {
        try
        {
            _pollTimer.Stop();
            if (_fanRuntime is not null)
                await _fanRuntime.RuntimeRestartDataReadersAsync();
            else
            {
                _temperatureReader?.Dispose();
                _temperatureReader = Report?.IsAvailable(
                    FeatureIds.TemperatureMonitoring) == true
                    ? new TemperatureReader()
                    : null;
            }
            _cachedBattery = null;
            _cachedGpuMode = null;
            _cachedPowerSettings = null;
            _lastBatteryRefresh = DateTimeOffset.MinValue;
            _lastGpuRefresh = DateTimeOffset.MinValue;
            _nextGpuOverclockRetry = DateTimeOffset.MinValue;
            await RefreshAsync(force: true);
            ToolkitLog.Info("Data readers were restarted by the user.");
            return null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Data readers could not be restarted.", ex);
            return ex.Message;
        }
        finally
        {
            if (!_disposed)
                _pollTimer.Start();
        }
    }

    public bool TrySetDisableControlOnSleep(bool value, out string? error)
    {
        if (FanBackendSupportsDisableControlOnSleep)
        {
            return TrySaveSetting(
                () => Settings.DisableControlOnSleep,
                current => Settings.DisableControlOnSleep = current,
                value,
                out error);
        }

        return TrySaveSetting(
            () => Settings.AttemptDisableControlOnSleepWhenUnsupported,
            current => Settings.AttemptDisableControlOnSleepWhenUnsupported = current,
            value,
            out error);
    }

    public bool TrySetFanIoMinimumIntervals(
        double? readSeconds,
        double? writeSeconds,
        out string? error)
    {
        if (!CurveProfileStore.IsValidFanIoIntervalOverride(readSeconds) ||
            !CurveProfileStore.IsValidFanIoIntervalOverride(writeSeconds))
        {
            error = L(
                "间隔必须留空或填写正整数秒数。",
                "Intervals must be blank or positive whole seconds.");
            return false;
        }

        var previousRead = Settings.FanReadMinimumIntervalSeconds;
        var previousWrite = Settings.FanWriteMinimumIntervalSeconds;
        Settings.FanReadMinimumIntervalSeconds = readSeconds;
        Settings.FanWriteMinimumIntervalSeconds = writeSeconds;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            _fanRuntime?.RuntimeRefreshSleepControlCapability();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.FanReadMinimumIntervalSeconds = previousRead;
            Settings.FanWriteMinimumIntervalSeconds = previousWrite;
            _fanRuntime?.RuntimeRefreshSleepControlCapability();
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetRefreshInterval(double seconds, out string? error)
    {
        if (!TrySaveSetting(
                () => Settings.IntervalSeconds,
                current => Settings.IntervalSeconds = current,
                seconds,
                out error))
        {
            return false;
        }
        SyncPollingInterval();
        _fanRuntime?.RefreshSharedPollingInterval();
        return true;
    }

    public bool TrySetLanguage(string language, out string? error)
    {
        if (Settings.Language == language)
        {
            error = null;
            return true;
        }
        if (!TrySaveSetting(
                () => Settings.Language,
                current => Settings.Language = current,
                language,
                out error))
        {
            return false;
        }
        RaiseAppearanceChanged();
        UpdateTrayMenu();
        return true;
    }

    public bool TrySetTheme(string theme, out string? error)
    {
        if (Settings.Theme == theme)
        {
            error = null;
            return true;
        }
        if (!TrySaveSetting(
                () => Settings.Theme,
                current => Settings.Theme = current,
                theme,
                out error))
        {
            return false;
        }
        SyncSystemThemeSubscription();
        RaiseAppearanceChanged();
        return true;
    }

    private bool TrySaveSetting<T>(
        Func<T> read,
        Action<T> write,
        T value,
        out string? error)
    {
        var previous = read();
        write(value);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            write(previous);
            error = ex.Message;
            return false;
        }
    }

    public void SetStatus(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            ToolkitLog.Info(message);
        StatusChanged?.Invoke(this, message);
    }

    private void PublishGpuTransitionState()
    {
        Snapshot = Snapshot with
        {
            PendingGpuMode = Settings.PendingGpuMode,
            PendingGpuModeSource = Settings.PendingGpuModeSource,
            UpdatedAt = DateTimeOffset.Now
        };
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyPendingGpuModeAsync()
    {
        if (Report?.IsAvailable(FeatureIds.GpuMode) != true ||
            !GpuModeRestartState.TryParsePendingMode(
                Settings.PendingGpuMode,
                out var pending) ||
            !GpuModeRestartState.HasRestartedSince(
                Settings.PendingGpuModeBootSessionId,
                _bootSessionId))
        {
            return;
        }

        try
        {
            var state = await Task.Run(GpuModeController.ReadState);
            if (GpuModeRestartState.ShouldClearAfterReadback(
                    Settings.PendingGpuMode,
                    Settings.PendingGpuModeBootSessionId,
                    _bootSessionId,
                    state.CurrentMode))
            {
                GpuModeRestartState.Clear(Settings);
                CurveProfileStore.SaveSettings(Settings);
                return;
            }

            if (GpuModeController.IsHybridMode(pending) &&
                !state.UsesDirectGraphicsConfiguration)
            {
                var requiresAnotherRestart = await Task.Run(
                    () => GpuModeController.SetMode(pending));
                if (requiresAnotherRestart)
                {
                    GpuModeRestartState.MarkPending(
                        Settings,
                        state.CurrentMode,
                        state.UsesDirectGraphicsConfiguration,
                        pending,
                        _bootSessionId);
                    CurveProfileStore.SaveSettings(Settings);
                    return;
                }

                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                do
                {
                    await Task.Delay(500);
                    state = await Task.Run(GpuModeController.ReadState);
                    if (state.CurrentMode == pending)
                    {
                        GpuModeRestartState.Clear(Settings);
                        CurveProfileStore.SaveSettings(Settings);
                        return;
                    }
                } while (DateTimeOffset.UtcNow < deadline);
            }
        }
        catch (Exception ex)
        {
            SetStatus(L("等待重启的 GPU 模式应用失败：", "Pending GPU mode failed: ") + ex.Message);
        }
    }

    private async Task EnforcePowerSettingsLockAsync()
    {
        if (_disposed ||
            _powerSettingsLockBusy ||
            !PowerSettingsController.CurrentProfile.Writable ||
            Report?.IsAvailable(FeatureIds.PowerSettings) != true)
        {
            return;
        }

        _powerSettingsLockBusy = true;
        await _powerSettingsGate.WaitAsync();
        try
        {
            var modeLock = CurrentPowerModeLock(create: false);
            var target = modeLock?.Target;
            var selection = modeLock is null
                ? new PowerSettingsLockSelection()
                : modeLock.Locks with { };
            if (!PowerSettingsController.IsValidLockConfiguration(
                    selection,
                    target))
            {
                return;
            }

            var current = await Task.Run(PowerSettingsController.ReadState);
            var active = CurrentPowerModeLock(create: false);
            if (active?.Locks != selection || active?.Target != target)
            {
                return;
            }

            if (PowerSettingsController.RequiresLockReapply(
                    current,
                    target!,
                    selection))
            {
                await Task.Run(() => PowerSettingsController.WriteLockedState(
                    current,
                    target!,
                    selection));
                var restored = PowerSettingsController.ApplyLockedValues(
                    current,
                    target!,
                    selection);
                _cachedPowerSettings = restored;
                Snapshot = Snapshot with { PowerSettings = restored };
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
            }
            _lastPowerSettingsLockError = string.Empty;
        }
        catch (Exception ex)
        {
            var message = L(
                "恢复锁定的功耗参数失败：",
                "Failed to restore locked power parameters: ") + ex.Message;
            if (!string.Equals(
                    message,
                    _lastPowerSettingsLockError,
                    StringComparison.Ordinal))
            {
                _lastPowerSettingsLockError = message;
                SetStatus(message);
            }
        }
        finally
        {
            _powerSettingsGate.Release();
            _powerSettingsLockBusy = false;
        }
    }

    private void SyncPowerSettingsLockTimer()
    {
        _powerSettingsLockTimer.Stop();
        _powerSettingsLockTimer.Interval = TimeSpan.FromSeconds(
            PowerSettingsController.IsSupportedLockInterval(
                Settings.PowerSettingsLockIntervalSeconds)
                ? Settings.PowerSettingsLockIntervalSeconds
                : 2);
        var modeLock = CurrentPowerModeLock(create: false);
        if (!_disposed && modeLock is not null &&
            PowerSettingsController.IsValidLockConfiguration(
                modeLock.Locks,
                modeLock.Target) &&
            PowerSettingsController.CurrentProfile.Writable &&
            Report?.IsAvailable(FeatureIds.PowerSettings) == true)
        {
            _powerSettingsLockTimer.Start();
        }
    }

    private PowerModeLockSettings? CurrentPowerModeLock(bool create)
    {
        var mode = Snapshot.ItsMode;
        if (mode == ItsMode.Unknown)
            return null;
        Settings.PowerSettingsLocksByMode ??=
            new Dictionary<string, PowerModeLockSettings>(
                StringComparer.OrdinalIgnoreCase);
        var key = mode.ToString();
        if (Settings.PowerSettingsLocksByMode.TryGetValue(key, out var profile))
            return profile;
        var migrateLegacy = Settings.PowerSettingsLocksByMode.Count == 0 &&
                            Settings.PowerSettingsLocks is { Any: true };
        if (!create && !migrateLegacy)
            return null;
        profile = new PowerModeLockSettings
        {
            Locks = migrateLegacy
                ? Settings.PowerSettingsLocks with { }
                : new PowerSettingsLockSelection(),
            Target = migrateLegacy
                ? Settings.PowerSettingsLockTarget
                : null
        };
        Settings.PowerSettingsLocksByMode[key] = profile;
        return profile;
    }

    private void SyncPollingInterval()
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(
            Math.Max(0.5, Settings.IntervalSeconds));
    }

    private void SyncSystemThemeSubscription()
    {
        var shouldSubscribe = Settings.Theme == "system";
        if (shouldSubscribe == _systemThemeSubscribed)
            return;
        if (shouldSubscribe)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        else
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _systemThemeSubscribed = shouldSubscribe;
    }

    private async Task RefreshHybridGpuProtectionAsync(
        bool forceGpuModeRefresh)
    {
        GpuWorkingMode? mode = _cachedGpuMode?.CurrentMode;
        var now = DateTimeOffset.UtcNow;
        if (Report?.IsAvailable(FeatureIds.GpuMode) == true &&
            (forceGpuModeRefresh ||
             _cachedGpuMode is null ||
             now - _lastGpuRefresh >= TimeSpan.FromSeconds(5)))
        {
            try
            {
                _cachedGpuMode = await Task.Run(GpuModeController.ReadState);
                _lastGpuRefresh = now;
                mode = _cachedGpuMode.CurrentMode;
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "GPU working mode could not be refreshed for hybrid GPU protection: " +
                    ex.Message);
            }
        }

        var isAcConnected = _cachedBattery?.IsAcConnected ?? true;
        if (BatteryInformationReader.TryGetAcConnectionState(
                out var currentAcState))
        {
            isAcConnected = currentAcState;
        }

        await _hybridAutoGpu.UpdateAsync(mode, isAcConnected);
    }

    private void OnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs args)
    {
        if (_disposed)
            return;

        if (args.Mode == PowerModes.Suspend)
        {
            ToolkitLog.Info(
                "Windows suspend event received; pausing GPU telemetry.");
            _hybridAutoGpu.Suspend();
            return;
        }

        if (args.Mode is not (PowerModes.StatusChange or PowerModes.Resume))
            return;

        ToolkitLog.Info("Windows power event received: " + args.Mode + ".");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                _ = HandlePowerModeChangedAsync();
            }));
    }

    private async Task HandlePowerModeChangedAsync()
    {
        try
        {
            await RefreshHybridGpuProtectionAsync(
                forceGpuModeRefresh: true);
            await RefreshAsync(force: true);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "Hybrid GPU power-event handling failed.",
                ex);
        }
    }

    private void OnDiscreteGpuPresenceChanged(
        object? sender,
        bool connected)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                var message = connected
                    ? L("独立显卡已连接", "The discrete GPU is connected")
                    : L("独立显卡已断开连接", "The discrete GPU is disconnected");
                SetStatus(message);
                _trayIcon?.ShowBalloonTip(
                    3000,
                    "ThinkBook Toolkit",
                    message,
                    Forms.ToolTipIcon.Info);
            }));
    }

    private void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs args)
    {
        if (Settings.Theme != "system")
            return;
        RaiseAppearanceChanged();
    }

    private void RaiseAppearanceChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Defer rebuilding the shell until the current selector event has
        // completed. This avoids replacing a page while its SelectionChanged
        // handler is still walking the old visual tree.
        dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => AppearanceChanged?.Invoke(this, EventArgs.Empty)));
    }

    private void InitializeTray()
    {
        if (_trayIcon is not null)
            return;
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadSystemApplicationIcon(),
            Visible = true,
            ContextMenuStrip = _trayMenu,
            Text = "ThinkBook Toolkit"
        };
        _trayIcon.DoubleClick += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_trayMenu is null)
            return;
        _trayMenu.Items.Clear();
        var state = Snapshot.FanControlRunning
            ? L("Toolkit 正在控制风扇", "Toolkit controls the fans")
            : L("固件自动控制", "Firmware automatic control");
        _trayMenu.Items.Add(new Forms.ToolStripMenuItem(state) { Enabled = false });
        var metrics = Snapshot.Temperatures is { } temperatures
            ? $"CPU {FormatTemperature(temperatures.CpuTempC)}  GPU {FormatTemperature(temperatures.GpuTempC)}"
            : "CPU --  GPU --";
        _trayMenu.Items.Add(new Forms.ToolStripMenuItem(metrics) { Enabled = false });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        if (_fanRuntime is not null)
        {
            var strategy = new Forms.ToolStripMenuItem(
                L("风扇策略", "Fan strategy"));
            strategy.DropDownItems.Add(CreateTrayFanModeItem(
                L("固件自动", "Firmware automatic"),
                FanControlMode.FirmwareAutomatic));
            strategy.DropDownItems.Add(CreateTrayFanModeItem(
                L("固定转速", "Fixed RPM"),
                FanControlMode.FixedRpm));
            strategy.DropDownItems.Add(CreateTrayFanModeItem(
                L("风扇曲线", "Fan curve"),
                FanControlMode.FanCurve));
            strategy.DropDownItems.Add(CreateTrayFanModeItem(
                L("高级曲线", "Advanced curve"),
                FanControlMode.AdvancedCurve));
            _trayMenu.Items.Add(strategy);
        }

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        var show = new Forms.ToolStripMenuItem(L("显示主窗口", "Show main window"));
        show.Click += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        _trayMenu.Items.Add(show);
        var exit = new Forms.ToolStripMenuItem(L("退出", "Exit"));
        exit.Click += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(RequestExit));
        _trayMenu.Items.Add(exit);
    }

    private Forms.ToolStripMenuItem CreateTrayFanModeItem(
        string text,
        FanControlMode mode)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Checked = CurrentTrayFanMode() == mode,
            CheckOnClick = false
        };
        item.Click += async (_, _) =>
        {
            if (CurrentTrayFanMode() == mode)
                return;

            var error = await SetFanModeAsync(mode);
            if (!string.IsNullOrWhiteSpace(error))
            {
                SetStatus(
                    L("风扇策略切换失败：", "Fan strategy switch failed: ") +
                    error);
            }
            UpdateTrayMenu();
        };
        return item;
    }

    private FanControlMode CurrentTrayFanMode()
        => ResolveTrayFanMode(Snapshot);

    internal static FanControlMode ResolveTrayFanMode(
        ToolkitRuntimeSnapshot snapshot)
    {
        if (!snapshot.FanControlRunning && !snapshot.FullSpeed)
            return FanControlMode.FirmwareAutomatic;
        return snapshot.FanStrategy switch
        {
            ControlStrategy.FanCurve => FanControlMode.FanCurve,
            ControlStrategy.AdvancedCurve => FanControlMode.AdvancedCurve,
            _ => FanControlMode.FixedRpm
        };
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null)
            return;
        var text = $"CPU {FormatTemperature(Snapshot.Temperatures?.CpuTempC)} " +
                   $"GPU {FormatTemperature(Snapshot.Temperatures?.GpuTempC)} " +
                   $"F1 {Snapshot.Fans?.Fan1Rpm ?? 0} F2 {Snapshot.Fans?.Fan2Rpm ?? 0}";
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private static string FormatTemperature(double? value) =>
        value.HasValue ? $"{value.Value:0}°C" : "--";

    private static Icon LoadSystemApplicationIcon()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (icon is not null)
                return icon;
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pollTimer.Stop();
        _powerSettingsLockTimer.Stop();
        if (_systemThemeSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _systemThemeSubscribed = false;
        }
        if (_powerModeSubscribed)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _powerModeSubscribed = false;
        }
        _hybridAutoGpu.PresenceChanged -= OnDiscreteGpuPresenceChanged;
        _fnKeyManager.Dispose();
        try { _fnKeyNotification?.Close(); } catch { }
        _fnKeyNotification = null;
        _fnKeyNotificationDark = null;
        _temperatureReader?.Dispose();
        _hybridAutoGpu.Dispose();
        try { _fanRuntime?.Close(); } catch { }
        _fanRuntime = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
        DisplaySettingsController.Shutdown();
        SoundSettingsController.Shutdown();
    }
}
