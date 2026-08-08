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
    private readonly string _bootSessionId;
    private bool _polling;
    private bool _powerSettingsLockBusy;
    private string _lastPowerSettingsLockError = string.Empty;
    private bool _disposed;
    private bool _systemThemeSubscribed;

    public ToolkitRuntimeService(AppSettings settings)
    {
        Settings = settings;
        _bootSessionId = GpuModeRestartState.CurrentBootSessionId;
        LenovoDependencyDirectory.Configure(settings);
        Snapshot = ToolkitRuntimeSnapshot.Empty;
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _powerSettingsLockTimer.Tick += async (_, _) =>
            await EnforcePowerSettingsLockAsync();
        SyncPollingInterval();
        SyncSystemThemeSubscription();
    }

    public AppSettings Settings { get; }

    internal PowerSettingsLockSelection CurrentPowerSettingsLocks =>
        CurrentPowerModeLock(create: false)?.Locks ??
        new PowerSettingsLockSelection();

    public FeatureAvailabilityReport? Report { get; private set; }

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

    public event EventHandler? SnapshotChanged;

    public event EventHandler? AvailabilityChanged;

    public event EventHandler? AppearanceChanged;

    public event EventHandler? OverviewLayoutChanged;

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

    public async Task RestoreForExitAsync()
    {
        _pollTimer.Stop();
        _powerSettingsLockTimer.Stop();
        if (_fanRuntime is not null)
            await _fanRuntime.RuntimeRestoreFirmwareAutoAsync();
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
        _polling = true;
        try
        {
            if (_fanRuntime is not null && force)
                await _fanRuntime.RuntimeRefreshAsync();

            var performance = _fanRuntime?.RuntimeSnapshot();
            var temperatures = performance?.Temperatures;
            var fans = performance?.Fans;
            if (temperatures is null && _temperatureReader is not null)
                temperatures = await Task.Run(_temperatureReader.Read);
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

            var itsMode = performance?.ItsMode ?? ItsMode.Unknown;
            if (Report?.IsAvailable(FeatureIds.PerformanceMode) == true &&
                itsMode == ItsMode.Unknown)
            {
                itsMode = await Task.Run(() => new ItsModeDetector().ReadMode());
            }

            GpuWorkingMode? gpuMode = performance?.GpuMode;
            IReadOnlyList<GpuWorkingMode> gpuModes =
                _cachedGpuMode?.SupportedModes ?? [];
            if (Report?.IsAvailable(FeatureIds.GpuMode) == true)
            {
                if (force ||
                    _cachedGpuMode is null ||
                    now - _lastGpuRefresh >= TimeSpan.FromSeconds(5))
                {
                    _cachedGpuMode = await Task.Run(GpuModeController.ReadState);
                    _lastGpuRefresh = now;
                }
                var gpu = _cachedGpuMode!;
                gpuMode = gpu.CurrentMode;
                gpuModes = gpu.SupportedModes;
            }

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

    public async Task<string?> SetItsModeAsync(ItsMode mode)
    {
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
                    await RefreshAsync(force: true);
                    SyncPowerSettingsLockTimer();
                    await EnforcePowerSettingsLockAsync();
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
            await _fanRuntime.RuntimeSetControlEnabledAsync(enabled);
            await RefreshAsync(force: true);
            return null;
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

        try
        {
            if (Snapshot.FanControlRunning || Snapshot.FullSpeed)
                await _fanRuntime.RuntimeRestoreFirmwareAutoAsync();

            var strategy = mode switch
            {
                FanControlMode.FanCurve => ControlStrategy.FanCurve,
                FanControlMode.AdvancedCurve => ControlStrategy.AdvancedCurve,
                _ => ControlStrategy.FixedRpm
            };
            if (!_fanRuntime.RuntimeSetStrategy(strategy))
            {
                return L(
                    "后台未接受新的控制策略。",
                    "The background controller did not accept the new strategy.");
            }

            Settings.FanCurveWarningAccepted |=
                strategy is ControlStrategy.FanCurve or
                    ControlStrategy.AdvancedCurve;
            CurveProfileStore.SaveSettings(Settings);
            await _fanRuntime.RuntimeSetControlEnabledAsync(true);
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
            await _fanRuntime.RuntimeSetFullSpeedAsync(enabled);
            await RefreshAsync(force: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public bool TrySetStartWithWindows(bool value, out string? error)
    {
        var old = Settings.StartWithWindows;
        Settings.StartWithWindows = value;
        error = MainWindow.ApplyStartupTaskSetting(Settings);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Settings.StartWithWindows = old;
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
            Settings.StartWithWindows = old;
            var rollbackError = MainWindow.ApplyStartupTaskSetting(Settings);
            error = ex.Message + (string.IsNullOrWhiteSpace(rollbackError)
                ? string.Empty
                : L("；系统任务回滚失败：", "; scheduled-task rollback failed: ") + rollbackError);
            return false;
        }
    }

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
        _temperatureReader?.Dispose();
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
