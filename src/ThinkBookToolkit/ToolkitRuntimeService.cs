using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    bool GamesRunning,
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
    private readonly AutomationRunner _automationRunner;
    private readonly KeyboardMacroService _macroService;
    private readonly LocalDataSharingService _dataSharing;
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
    private bool? _lastAutomationAcConnected;
    private bool? _lastAutomationGamesRunning;
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
    private bool _fnDiscoveryOwnsListener;
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
        _automationRunner = new AutomationRunner(this);
        _macroService = new KeyboardMacroService(
            settings,
            _fnKeyManager.HandleStandardKeyboardEvent);
        _bootSessionId = GpuModeRestartState.CurrentBootSessionId;
        LenovoDependencyDirectory.Configure(settings);
        Snapshot = ToolkitRuntimeSnapshot.Empty;
        _dataSharing = new LocalDataSharingService(() => Snapshot);
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _powerSettingsLockTimer.Tick += async (_, _) =>
            await EnforcePowerSettingsLockAsync();
        _hybridAutoGpu.PresenceChanged += OnDiscreteGpuPresenceChanged;
        GpuTelemetryControl.ModeChanged += OnGpuTelemetryModeChanged;
        SyncPollingInterval();
        SyncSystemThemeSubscription();
        if (settings.ShareDataWithOtherSoftware)
        {
            try
            {
                _dataSharing.Start(settings.DataSharingPort);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error(
                    "Local data sharing could not be started from the saved settings.",
                    ex);
                settings.ShareDataWithOtherSoftware = false;
                try { CurveProfileStore.SaveSettings(settings); } catch { }
            }
        }
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

    public ToolkitRuntimeSnapshot Snapshot { get; private set; }

    public MainWindow? FanRuntime => _fanRuntime;

    internal bool NvApiGpuPowerEnabled =>
        Settings.UseNvApiGpuPower &&
        Report?.IsAvailable(FeatureIds.NvApiGpuPower) == true;
    internal bool IntelMmioCpuPowerEnabled =>
        Settings.UseIntelMmioCpuPower &&
        Report?.IsAvailable(FeatureIds.IntelMmioCpuPower) == true;
    internal bool AmdZenStatesCpuPowerEnabled =>
        Settings.UseAmdZenStatesCpuPower &&
        Report?.IsAvailable(FeatureIds.AmdZenStatesCpuPower) == true;
    internal BetaCpuPowerKind? BetaCpuPowerKind => IntelMmioCpuPowerEnabled
        ? ThinkBookToolkit.BetaCpuPowerKind.IntelMmio
        : AmdZenStatesCpuPowerEnabled
            ? AmdZenStatesPowerController.CachedKind
            : null;

    internal bool CanReadPowerSettings =>
        Report?.IsAvailable(FeatureIds.PowerSettings) == true ||
        NvApiGpuPowerEnabled || IntelMmioCpuPowerEnabled ||
        AmdZenStatesCpuPowerEnabled;

    internal bool CanWritePowerSettings =>
        PowerSettingsController.CurrentProfile.Writable &&
        Report?.IsAvailable(FeatureIds.PowerSettings) == true ||
        NvApiGpuPowerEnabled || IntelMmioCpuPowerEnabled ||
        AmdZenStatesCpuPowerEnabled;

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

    public bool NativeFanFullSpeedAvailable =>
        Report?.IsAvailable(FeatureIds.FanFullSpeed) == true;

    public bool CanUseFanFullSpeed =>
        NativeFanFullSpeedAvailable ||
        Settings.UseAlternativeFullSpeedMethod;

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

    public event EventHandler? ControlStateChanged;

    public event EventHandler? AutomationChanged;

    public event EventHandler? MacroChanged;

    public event EventHandler<FnKeyDiscoveredInfo>? FnKeyDiscovered;

    internal async Task<string?> SetFnKeyDiscoveryModeAsync(bool enabled)
    {
        if (enabled)
        {
            if (!_fnKeyManager.IsRunning)
            {
                var error = await _fnKeyManager.StartAsync();
                if (!string.IsNullOrWhiteSpace(error))
                    return error;
                _fnDiscoveryOwnsListener = !Settings.TakeOverFnKeys;
            }
            _fnKeyManager.DiscoveryMode = true;
            return null;
        }
        _fnKeyManager.DiscoveryMode = false;
        if (_fnDiscoveryOwnsListener)
        {
            _fnDiscoveryOwnsListener = false;
            return await _fnKeyManager.StopAsync(
                restoreLenovoHotkeys: true);
        }
        return null;
    }

    internal void PublishFnKeyDiscovered(FnKeyDiscoveredInfo info) =>
        FnKeyDiscovered?.Invoke(this, info);

    internal async Task<AutomationRunResult> RunAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return await (await dispatcher.InvokeAsync(() =>
                _automationRunner.RunAsync(
                    automationId,
                    cancellationToken)));
        }
        return await _automationRunner.RunAsync(
            automationId,
            cancellationToken);
    }

    internal bool TrySaveAutomations(
        IEnumerable<AutomationDefinition> automations,
        out string? error)
    {
        var values = automations.ToArray();
        if (UniqueDefinitionNames.HasDuplicates(
                values.Select(item => item.Name)))
        {
            error = L(
                "自动化名称不能重复。",
                "Automation names must be unique.");
            return false;
        }
        var previousAutomations = Settings.Automations;
        var previousBindings = Settings.FnKeyAutomationBindings;
        var previousDoubleBindings =
            Settings.FnKeyDoublePressAutomationBindings;
        Settings.Automations = AutomationSettingsDefaults.Normalize(values);
        Settings.FnKeyAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                previousBindings,
                Settings.Automations,
                Settings.CustomFnKeyNames);
        Settings.FnKeyDoublePressAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                previousDoubleBindings,
                Settings.Automations,
                Settings.CustomFnKeyNames);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            AutomationChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.Automations = previousAutomations;
            Settings.FnKeyAutomationBindings = previousBindings;
            Settings.FnKeyDoublePressAutomationBindings =
                previousDoubleBindings;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySetAutomationEnabled(bool enabled, out string? error)
    {
        var previous = Settings.AutomationEnabled;
        Settings.AutomationEnabled = enabled;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            AutomationChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.AutomationEnabled = previous;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySaveMacros(
        IEnumerable<KeyboardMacroDefinition> macros,
        out string? error)
    {
        var values = macros.ToArray();
        if (UniqueDefinitionNames.HasDuplicates(
                values.Select(item => item.Name)))
        {
            error = L(
                "宏名称不能重复。",
                "Macro names must be unique.");
            return false;
        }
        var duplicateTrigger = values
            .Where(item => item.TriggerVirtualKey.HasValue)
            .GroupBy(item => item.TriggerVirtualKey!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTrigger is not null)
        {
            error = L(
                $"按键 {KeyboardMacroKeyNames.Format(duplicateTrigger.Key)} 已绑定到多个宏。",
                $"Key {KeyboardMacroKeyNames.Format(duplicateTrigger.Key)} is assigned to more than one macro.");
            return false;
        }
        var previous = Settings.Macros;
        Settings.Macros = KeyboardMacroDefaults.Normalize(values);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            MacroChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.Macros = previous;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySetMacroEnabled(bool enabled, out string? error)
    {
        if (enabled)
        {
            var startError = _macroService.Start();
            if (!string.IsNullOrWhiteSpace(startError))
            {
                error = startError;
                return false;
            }
        }
        var previous = Settings.MacroEnabled;
        Settings.MacroEnabled = enabled;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            MacroChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.MacroEnabled = previous;
            error = ex.Message;
            return false;
        }
    }

    internal string? StartMacroRecording(
        Action<KeyboardMacroEvent> recorded,
        Action stopped) =>
        _macroService.StartRecording(recorded, stopped);

    internal void StopMacroRecording() => _macroService.StopRecording();

    internal string? StartMacroTriggerCapture(Action<int?> captured) =>
        _macroService.StartTriggerCapture(captured);

    internal void CancelMacroTriggerCapture() =>
        _macroService.CancelTriggerCapture();

    internal Task<string?> RunMacroAsync(
        string macroId,
        CancellationToken cancellationToken = default,
        string executionSource = "direct") =>
        _macroService.PlayAsync(
            macroId,
            cancellationToken,
            executionSource);

    internal bool TrySaveGameDetectionPaths(
        IEnumerable<string> included,
        IEnumerable<string> excluded,
        out string? error)
    {
        var previousIncluded = Settings.IncludedGamePaths;
        var previousExcluded = Settings.ExcludedGamePaths;
        Settings.IncludedGamePaths =
            CurveProfileStore.NormalizeApplicationPaths(included);
        Settings.ExcludedGamePaths =
            CurveProfileStore.NormalizeApplicationPaths(excluded);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            _fanRuntime?.RuntimeReloadGameDetection();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.IncludedGamePaths = previousIncluded;
            Settings.ExcludedGamePaths = previousExcluded;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySaveFnAutomationBindings(
        IReadOnlyDictionary<string, string> singlePressBindings,
        IReadOnlyDictionary<string, string> doublePressBindings,
        out string? error)
    {
        var previous = Settings.FnKeyAutomationBindings;
        var previousDouble = Settings.FnKeyDoublePressAutomationBindings;
        Settings.FnKeyAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                singlePressBindings,
                Settings.Automations,
                Settings.CustomFnKeyNames);
        Settings.FnKeyDoublePressAutomationBindings =
            AutomationSettingsDefaults.NormalizeFnBindings(
                doublePressBindings,
                Settings.Automations,
                Settings.CustomFnKeyNames);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            AutomationChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.FnKeyAutomationBindings = previous;
            Settings.FnKeyDoublePressAutomationBindings = previousDouble;
            error = ex.Message;
            return false;
        }
    }

    internal async Task<bool> TryRunFnAutomationAsync(
        string keyId,
        bool doublePress = false)
    {
        if (!Settings.AutomationEnabled)
            return false;
        var bindings = doublePress
            ? Settings.FnKeyDoublePressAutomationBindings
            : Settings.FnKeyAutomationBindings;
        if (!bindings.TryGetValue(
                keyId,
                out var automationId))
        {
            return false;
        }
        var automation = Settings.Automations.FirstOrDefault(item =>
            item.Id.Equals(
                automationId,
                StringComparison.OrdinalIgnoreCase));
        if (automation is null)
            return false;
        ShowFnKeyNotification(
            L("自动化", "Automation"),
            automation.Name);
        var result = await RunAutomationAsync(automation.Id);
        if (!result.Success)
            SetStatus(result.Error);
        return true;
    }

    internal bool TryAddDiscoveredFnKey(
        FnKeyDiscoveredInfo info,
        out string? error)
    {
        var previous = Settings.CustomFnKeyNames;
        var updated = new Dictionary<string, string>(
            previous,
            StringComparer.OrdinalIgnoreCase)
        {
            [info.BindingId] = string.IsNullOrWhiteSpace(info.Name)
                ? $"Fn + 0x{unchecked((uint)info.Code):X} ({info.Channel})"
                : info.Name.Trim()
        };
        Settings.CustomFnKeyNames =
            AutomationSettingsDefaults.NormalizeCustomFnKeyNames(updated);
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            AutomationChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.CustomFnKeyNames = previous;
            error = ex.Message;
            return false;
        }
    }

    internal bool HasFnAutomationBinding(string keyId) =>
        Settings.AutomationEnabled &&
        (HasFnAutomationBinding(keyId, doublePress: false) ||
         HasFnAutomationBinding(keyId, doublePress: true));

    internal bool HasFnAutomationBinding(string keyId, bool doublePress)
    {
        if (!Settings.AutomationEnabled)
            return false;
        var bindings = doublePress
            ? Settings.FnKeyDoublePressAutomationBindings
            : Settings.FnKeyAutomationBindings;
        return bindings.TryGetValue(keyId, out var automationId) &&
               Settings.Automations.Any(item => item.Id.Equals(
                   automationId,
                   StringComparison.OrdinalIgnoreCase));
    }

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
        var startup = Stopwatch.StartNew();
        var previousStage = TimeSpan.Zero;
        void LogStage(string stage)
        {
            var elapsed = startup.Elapsed;
            ToolkitLog.Info(
                $"Startup stage completed: {stage}; " +
                $"step={(elapsed - previousStage).TotalMilliseconds:0} ms; " +
                $"total={elapsed.TotalMilliseconds:0} ms.");
            previousStage = elapsed;
        }

        var macroHookError = _macroService.Start();
        if (!string.IsNullOrWhiteSpace(macroHookError))
        {
            ToolkitLog.Warning(
                "The keyboard macro listener could not be started: " +
                macroHookError);
        }
        Report = await FeatureAvailabilityService.DetectAsync();
        ToolkitLog.Info(
            $"Feature detection completed: {Report.Items.Count(item => item.Usable)}/{Report.Items.Count} usable.");
        FeatureAvailabilityDiagnostics.LogIssues(Report);
        FeatureAvailabilityCache.Current = Report;
        InitializeAlternativeFullSpeedMethod();
        LogStage("feature detection");

        // This must run before MainWindow raises Loaded: the embedded fan
        // runtime creates its TemperatureReader from that event.
        await RefreshHybridGpuProtectionAsync(forceGpuModeRefresh: true);
        LogStage("hybrid GPU protection initialization");
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
        LogStage("fan and hardware runtime creation");

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        await Task.Yield();
        LogStage("overview availability notification");

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
        LogStage("startup settings and Fn-key takeover");
        await ApplyPendingGpuModeAsync();
        LogStage("pending GPU mode handling");
        await RefreshAsync(force: true);
        LogStage("initial hardware refresh");
        _ = RefreshWarrantyAsync();
        _pollTimer.Start();
        SyncPowerSettingsLockTimer();
        LogStage("background refresh scheduling");
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

    private void InitializeAlternativeFullSpeedMethod()
    {
        if (!ShouldInitializeAlternativeFullSpeedMethod(Report, Settings))
        {
            return;
        }

        Settings.UseAlternativeFullSpeedMethod = true;
        Settings.AlternativeFullSpeedMethodInitialized = true;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            ToolkitLog.Info(
                "The fan backend cannot provide native full speed; the " +
                "alternative maximum-RPM method was enabled by default.");
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "The default alternative full-speed setting could not be saved.",
                ex);
        }
    }

    internal static bool ShouldInitializeAlternativeFullSpeedMethod(
        FeatureAvailabilityReport? report,
        AppSettings settings) =>
        report?.IsAvailable(FeatureIds.FanControl) == true &&
        !report.IsAvailable(FeatureIds.FanFullSpeed) &&
        !settings.AlternativeFullSpeedMethodInitialized;

    public void ShowMainWindow()
    {
        if (_window is null)
            return;
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            return;
        }
        _window.Show();
        _window.ShowInTaskbar = true;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    internal void MinimizeMainWindow()
    {
        if (_window is null)
            return;
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(MinimizeMainWindow));
            return;
        }
        _window.WindowState = WindowState.Minimized;
    }

    internal void ToggleMainWindow()
    {
        if (_window is null)
            return;
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(ToggleMainWindow));
            return;
        }
        if (_window.IsVisible &&
            _window.WindowState != WindowState.Minimized &&
            _window.IsActive)
            MinimizeMainWindow();
        else
            ShowMainWindow();
    }

    internal void NotifyControlStateChanged() =>
        ControlStateChanged?.Invoke(this, EventArgs.Empty);

    internal async Task<string?> SetFixedRpmGameModeAsync(bool gameMode)
    {
        if (_fanRuntime is null)
            return L("风扇功能不可用", "Fan controls are unavailable");
        try
        {
            _fanRuntime.RuntimeSetManualFixedMode(gameMode);
            await RefreshAsync(force: true);
            NotifyControlStateChanged();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal async Task<string?> SetInputSettingAsync(
        InputSettingKind kind,
        bool enabled)
    {
        try
        {
            if (Settings.TakeOverFnKeys &&
                (kind is InputSettingKind.CapsLockOsd or
                    InputSettingKind.NumLockOsd))
            {
                if (!TrySetLockKeyOsd(kind, enabled, out var saveError))
                    return saveError;
            }
            else
            {
                var confirmed = await Task.Run(() =>
                    InputSettingsController.SetState(kind, enabled));
                if (!confirmed.Supported || confirmed.Enabled != enabled)
                {
                    return L(
                        "硬件未确认新的状态。",
                        "Hardware did not confirm the new state.");
                }
                if ((kind is InputSettingKind.CapsLockOsd or
                    InputSettingKind.NumLockOsd) &&
                    !TrySetLockKeyOsd(kind, enabled, out var saveError))
                {
                    return saveError;
                }
            }
            NotifyControlStateChanged();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
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
            return await Task.Run(ReadPowerSettingsCore);
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
            var confirmed = await Task.Run(() =>
                ApplyPowerSettingsCore(state));
            UpdatePowerLockTargetAfterApply(confirmed);
            return confirmed;
        }
        finally
        {
            _powerSettingsGate.Release();
        }
    }

    internal PowerSettingsState? GetDefaultPowerSettingsState(ItsMode mode)
    {
        var state = PowerSettingsController.GetDefaultState(mode);
        return state is null || !NvApiGpuPowerEnabled
            ? state
            : NvPcfPowerPolicy.FromLegacy(state);
    }

    public async Task<PowerSettingsState> RestoreDefaultPowerSettingsAsync(
        ItsMode mode)
    {
        if (!NvApiGpuPowerEnabled)
            throw new NotSupportedException(
                "NVAPI GPU power control is not enabled.");
        await _powerSettingsGate.WaitAsync();
        try
        {
            var confirmed = await Task.Run(() =>
            {
                NvPcfPowerController.ResetToDefaults();
                PowerSettingsState? wmi = null;
                var defaults = PowerSettingsController.GetDefaultState(mode);
                if (defaults is not null &&
                    PowerSettingsController.CurrentProfile.Writable)
                {
                    try
                    {
                        var wmiDefaults = defaults with
                        {
                            AvailableSettings =
                                defaults.AvailableSettings &
                                ~NvPcfPowerPolicy.LegacyGpuMask
                        };
                        wmi = PowerSettingsController.WriteAndReadState(
                            wmiDefaults);
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Warning(
                            "nvpcf was reset, but Lenovo WMI defaults could " +
                            "not be restored: " + ex.Message);
                        wmi = TryReadWmiPowerSettings();
                    }
                }
                else
                {
                    wmi = TryReadWmiPowerSettings();
                }
                return NvPcfPowerPolicy.Merge(
                    wmi,
                    NvPcfPowerController.ReadAfterReset());
            });
            _cachedPowerSettings = confirmed;
            UpdatePowerLockTargetAfterApply(confirmed);
            return confirmed;
        }
        finally
        {
            _powerSettingsGate.Release();
        }
    }

    public async Task<string?> SetNvApiGpuPowerEnabledAsync(bool enabled)
    {
        if (enabled == Settings.UseNvApiGpuPower)
            return null;
        if (enabled &&
            Report?.IsAvailable(FeatureIds.NvApiGpuPower) != true)
        {
            return L(
                "当前设备无法读取全部四项 NVPCF 功耗参数。",
                "All four NVPCF power values are unavailable on this device.");
        }

        _powerSettingsLockTimer.Stop();
        await _powerSettingsGate.WaitAsync();
        var changed = false;
        string? failure = null;
        _ = CurrentPowerModeLock(create: false);
        var previousEnabled = Settings.UseNvApiGpuPower;
        var previousLegacyProfiles = ClonePowerModeLocks(
            Settings.PowerSettingsLocksByMode);
        var previousNvApiProfiles = ClonePowerModeLocks(
            Settings.NvApiPowerSettingsLocksByMode);
        var previousLocks = Settings.PowerSettingsLocks;
        var previousTarget = Settings.PowerSettingsLockTarget;
        try
        {
            var source = await Task.Run(ReadPowerSettingsCore);
            PowerSettingsState confirmed;
            if (enabled)
                confirmed = await EnableNvApiGpuPowerCoreAsync(source);
            else
                confirmed = await DisableNvApiGpuPowerCoreAsync(source);
            Settings.UseNvApiGpuPower = enabled;
            _cachedPowerSettings = confirmed;
            SyncLegacyPowerLockFields();
            CurveProfileStore.SaveSettings(Settings);
            changed = true;
            ToolkitLog.Info(
                $"NVAPI GPU power control changed: enabled={enabled}.");
        }
        catch (Exception ex)
        {
            Settings.UseNvApiGpuPower = previousEnabled;
            Settings.PowerSettingsLocksByMode = previousLegacyProfiles;
            Settings.NvApiPowerSettingsLocksByMode = previousNvApiProfiles;
            Settings.PowerSettingsLocks = previousLocks;
            Settings.PowerSettingsLockTarget = previousTarget;
            ToolkitLog.Error(
                "NVAPI GPU power control could not be changed.",
                ex);
            failure = ex.GetBaseException().Message;
        }
        finally
        {
            _powerSettingsGate.Release();
            SyncPowerSettingsLockTimer();
        }

        if (changed)
        {
            OverviewLayoutChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(force: true);
        }
        return failure;
    }

    public string? SetBetaCpuPowerEnabled(bool intel, bool enabled)
    {
        var feature = intel ? FeatureIds.IntelMmioCpuPower :
            FeatureIds.AmdZenStatesCpuPower;
        if (enabled && Report?.IsAvailable(feature) != true)
        {
            var detail = Report?.Items.FirstOrDefault(item =>
                item.Id == feature)?.Detail;
            return L("当前 CPU Beta 功耗接口不可用：",
                       "The CPU Beta power interface is unavailable: ") +
                   (string.IsNullOrWhiteSpace(detail)
                       ? L("未通过功能检测。", "capability detection failed.")
                       : detail);
        }
        var oldIntel = Settings.UseIntelMmioCpuPower;
        var oldAmd = Settings.UseAmdZenStatesCpuPower;
        try
        {
            Settings.UseIntelMmioCpuPower = intel && enabled;
            Settings.UseAmdZenStatesCpuPower = !intel && enabled;
            CurveProfileStore.SaveSettings(Settings);
            _cachedPowerSettings = null;
            OverviewLayoutChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception ex)
        {
            Settings.UseIntelMmioCpuPower = oldIntel;
            Settings.UseAmdZenStatesCpuPower = oldAmd;
            return ex.Message;
        }
    }

    private async Task<PowerSettingsState> EnableNvApiGpuPowerCoreAsync(
        PowerSettingsState legacy)
    {
        if (legacy.Atpp is null &&
            PowerSettingsController.GetDefaultState(Snapshot.ItsMode) is
            { Atpp: { } defaultAtpp })
        {
            legacy = legacy with { Atpp = defaultAtpp };
        }
        var nvSnapshot = await Task.Run(NvPcfPowerController.Read);
        var current = NvPcfPowerPolicy.Merge(
            legacy,
            nvSnapshot);
        var converted = NvPcfPowerPolicy.FromLegacy(legacy);
        var profile = PowerModeLock(
            Settings.NvApiPowerSettingsLocksByMode,
            Snapshot.ItsMode,
            create: true) ?? new PowerModeLockSettings();
        var settings = new[]
        {
            PowerSetting.NvPcfAcTargetTppLimit,
            PowerSetting.NvPcfAcDefaultGpuLimit,
            PowerSetting.NvPcfAcMinGpuLimit,
            PowerSetting.NvPcfAcMaxGpuLimit,
            PowerSetting.NvApiGpuTemperatureLimit
        };
        var desired = ApplyConvertedOrLockedValues(
            current,
            converted,
            profile,
            settings,
            out var selection);
        var written = selection.Any
            ? await Task.Run(() =>
                NvPcfPowerController.WriteAndRead(desired, selection))
            : nvSnapshot;
        var confirmed = NvPcfPowerPolicy.Merge(
            legacy,
            written);
        profile.Target = UpdateProfileTarget(
            profile.Target,
            confirmed,
            settings,
            profile.Locks);
        return confirmed;
    }

    private async Task<PowerSettingsState> DisableNvApiGpuPowerCoreAsync(
        PowerSettingsState nvState)
    {
        var converted = NvPcfPowerPolicy.ToLegacy(nvState);
        try
        {
            await Task.Run(NvPcfPowerController.ResetAllPowerOverrides);
        }
        catch (Exception ex)
        {
            NvPcfPowerController.Shutdown();
            ToolkitLog.Error(
                "NVAPI GPU power overrides could not be reset while " +
                "disabling the feature; the feature will still be disabled.",
                ex);
            SetStatus(L(
                "NVAPI 功耗调整已关闭，但恢复 NVIDIA 默认功耗设置失败：",
                "NVAPI power control was disabled, but NVIDIA defaults could not be restored: ") +
                ex.GetBaseException().Message);
        }
        return TryReadWmiPowerSettings() ?? converted;
    }

    private static PowerSettingsState ApplyConvertedOrLockedValues(
        PowerSettingsState current,
        PowerSettingsState converted,
        PowerModeLockSettings profile,
        IEnumerable<PowerSetting> settings,
        out PowerSettingsLockSelection selection)
    {
        var desired = current;
        selection = new PowerSettingsLockSelection();
        foreach (var setting in settings)
        {
            if (!current.IsAvailable(setting))
                continue;
            var source = profile.Locks.IsLocked(setting) &&
                         profile.Target is { } target &&
                         target.IsAvailable(setting) &&
                         PowerSettingsController.Value(target, setting).HasValue
                ? target
                : converted;
            if (!source.IsAvailable(setting) ||
                !PowerSettingsController.Value(source, setting).HasValue)
                continue;
            desired = PowerSettingsController.WithSetting(
                desired,
                source,
                setting);
            selection = selection.With(setting, true);
        }
        return desired;
    }

    private static PowerSettingsState? UpdateProfileTarget(
        PowerSettingsState? previous,
        PowerSettingsState current,
        IEnumerable<PowerSetting> settings,
        PowerSettingsLockSelection locks)
    {
        if (!locks.Any)
            return null;
        var target = PowerSettingsController.IsValidState(previous)
            ? previous!
            : current;
        foreach (var setting in settings)
        {
            if (!locks.IsLocked(setting) && current.IsAvailable(setting))
                target = PowerSettingsController.WithSetting(
                    target,
                    current,
                    setting);
        }
        return target;
    }

    private PowerSettingsState ReadPowerSettingsCore()
    {
        var wmi = TryReadWmiPowerSettings();
        if (IntelMmioCpuPowerEnabled)
            wmi = MergeBetaCpu(wmi, IntelMmioPowerController.Read());
        else if (AmdZenStatesCpuPowerEnabled)
            wmi = MergeBetaCpu(wmi, AmdZenStatesPowerController.Read());
        if (NvApiGpuPowerEnabled)
        {
            if (GpuTelemetryControl.Mode != GpuTelemetryMode.Full)
            {
                if (_cachedPowerSettings is { } cached &&
                    NvPcfPowerPolicy.TryValues(
                        cached,
                        out var target,
                        out var @default,
                        out var minimum,
                        out var maximum))
                {
                    var bounds = NvPcfPowerController.CachedSliderBounds ??
                                 (@default, maximum);
                    return NvPcfPowerPolicy.Merge(
                        wmi,
                        new NvPcfPowerSnapshot(
                            target,
                            @default,
                            minimum,
                            maximum,
                            bounds.MinimumW,
                            bounds.MaximumW,
                            cached.NvPcfDynamicBoostEnabled,
                            cached.NvApiGpuTemperatureLimit,
                            NvPcfPowerController.CachedTemperatureBounds?.MinimumC,
                            NvPcfPowerController.CachedTemperatureBounds?.MaximumC,
                            "Cached while dGPU is unavailable"));
                }
                return wmi ?? throw new InvalidOperationException(
                    "The discrete GPU is unavailable, so NVPCF values cannot be read.");
            }
            return NvPcfPowerPolicy.Merge(wmi, NvPcfPowerController.Read());
        }
        return wmi ?? throw new InvalidOperationException(
            "No power values could be read.");
    }

    private PowerSettingsState ApplyPowerSettingsCore(
        PowerSettingsState state)
    {
        BetaCpuPowerSnapshot? beta = null;
        if (IntelMmioCpuPowerEnabled)
            beta = IntelMmioPowerController.Write(
                state.CpuPl1, state.CpuPl2, state.CpuTurboTimeLimit);
        else if (AmdZenStatesCpuPowerEnabled)
        {
            var names = BetaCpuPowerKind == ThinkBookToolkit.BetaCpuPowerKind.AmdPbo
                ? new[] { "ppt", "tdc", "edc" }
                : new[] { "stapm", "fast", "slow" };
            _ = AmdZenStatesPowerController.Write(names[0], state.CpuPl1);
            _ = AmdZenStatesPowerController.Write(names[1], state.CpuPl2);
            _ = AmdZenStatesPowerController.Write(names[2], state.CpuTurboTimeLimit);
            if (state.IsAvailable(PowerSetting.CpuTemperatureLimit))
                _ = AmdZenStatesPowerController.Write("tctlmax", state.CpuTemperatureLimit);
            beta = AmdZenStatesPowerController.Read();
        }
        if (!NvApiGpuPowerEnabled && beta is null)
            return PowerSettingsController.WriteAndReadState(state);
        if (!NvApiGpuPowerEnabled)
        {
            var draft = state with { AvailableSettings =
                state.AvailableSettings & ~BetaCpuMask() };
            var wmiOnly = PowerSettingsController.CurrentProfile.Writable &&
                          Report?.IsAvailable(FeatureIds.PowerSettings) == true
                ? PowerSettingsController.WriteAndReadState(draft)
                : TryReadWmiPowerSettings();
            return MergeBetaCpu(wmiOnly, beta!);
        }
        if (GpuTelemetryControl.Mode != GpuTelemetryMode.Full)
            throw new InvalidOperationException(
                "The discrete GPU is unavailable, so NVPCF values cannot be written.");
        if (!NvPcfPowerPolicy.IsValid(state))
            throw new ArgumentException(
                "All four NVPCF power values must be positive integers.",
                nameof(state));

        PowerSettingsState? wmi = null;
        if (PowerSettingsController.CurrentProfile.Writable &&
            Report?.IsAvailable(FeatureIds.PowerSettings) == true)
        {
            try
            {
                var wmiDraft = state with
                {
                    AvailableSettings =
                        state.AvailableSettings &
                        ~NvPcfPowerPolicy.NvPcfMask &
                        ~NvPcfPowerPolicy.LegacyGpuMask &
                        ~BetaCpuMask()
                };
                wmi = PowerSettingsController.WriteAndReadState(wmiDraft);
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "Lenovo WMI power values could not be written while " +
                    "NVAPI GPU power control remains active: " + ex.Message);
                SetStatus(L(
                    "联想功耗接口写入失败；NVAPI GPU 功耗参数将继续应用。",
                    "Lenovo power values could not be written; the NVAPI GPU power values will still be applied."));
                wmi = TryReadWmiPowerSettings();
            }
        }
        else
        {
            wmi = TryReadWmiPowerSettings();
        }
        var nvPcf = NvPcfPowerController.WriteAndRead(state);
        var result = NvPcfPowerPolicy.Merge(wmi, nvPcf);
        return beta is null ? result : MergeBetaCpu(result, beta);
    }

    private PowerSettingAvailability BetaCpuMask() =>
        IntelMmioCpuPowerEnabled
            ? PowerSettingsController.Flag(PowerSetting.CpuPl1) |
              PowerSettingsController.Flag(PowerSetting.CpuPl2) |
              PowerSettingsController.Flag(PowerSetting.CpuTurboTimeLimit)
            : AmdZenStatesCpuPowerEnabled
                ? PowerSettingsController.Flag(PowerSetting.CpuPl1) |
                  PowerSettingsController.Flag(PowerSetting.CpuPl2) |
                  PowerSettingsController.Flag(PowerSetting.CpuTurboTimeLimit) |
                  PowerSettingsController.Flag(PowerSetting.CpuTemperatureLimit)
                : PowerSettingAvailability.None;

    private static PowerSettingsState MergeBetaCpu(
        PowerSettingsState? state,
        BetaCpuPowerSnapshot beta)
    {
        state ??= new PowerSettingsState(0, 0, 0, 0, 0, 0, 0, 0)
        { AvailableSettings = PowerSettingAvailability.None };
        var names = beta.Kind == ThinkBookToolkit.BetaCpuPowerKind.AmdPbo
            ? new[] { "ppt", "tdc", "edc" }
            : beta.Kind == ThinkBookToolkit.BetaCpuPowerKind.AmdApu
                ? new[] { "stapm", "fast", "slow" }
                : new[] { "pl1", "pl2", "turbo" };
        var available = state.AvailableSettings |
            PowerSettingsController.Flag(PowerSetting.CpuPl1) |
            PowerSettingsController.Flag(PowerSetting.CpuPl2) |
            PowerSettingsController.Flag(PowerSetting.CpuTurboTimeLimit);
        if (beta.TctlMax.HasValue)
            available |= PowerSettingsController.Flag(PowerSetting.CpuTemperatureLimit);
        return state with
        {
            CpuPl1 = beta.Values[names[0]], CpuPl2 = beta.Values[names[1]],
            CpuTurboTimeLimit = beta.Values[names[2]],
            CpuTemperatureLimit = beta.TctlMax ?? state.CpuTemperatureLimit,
            AvailableSettings = available,
            BetaCpuPowerKind = beta.Kind
        };
    }

    private PowerSettingsState? TryReadWmiPowerSettings()
    {
        if (Report?.IsAvailable(FeatureIds.PowerSettings) != true)
            return null;
        try
        {
            return PowerSettingsController.ReadState();
        }
        catch (Exception ex)
        {
            if (!NvApiGpuPowerEnabled)
                throw;
            ToolkitLog.Warning(
                "WMI power values are unavailable while NVAPI GPU power " +
                "control remains usable: " + ex.Message);
            return null;
        }
    }

    private void UpdatePowerLockTargetAfterApply(
        PowerSettingsState confirmed)
    {
        var modeLock = CurrentPowerModeLock(create: false);
        if (modeLock?.Locks is not { Any: true })
            return;
        modeLock.Target = confirmed;
        var inactiveProfiles = Settings.UseNvApiGpuPower
            ? Settings.PowerSettingsLocksByMode
            : Settings.NvApiPowerSettingsLocksByMode;
        var inactive = PowerModeLock(
            inactiveProfiles,
            Snapshot.ItsMode,
            create: false);
        if (inactive is not null)
        {
            var inactiveTarget = PowerSettingsController.IsValidState(
                inactive.Target)
                ? inactive.Target!
                : confirmed;
            foreach (var setting in new[]
                     {
                         PowerSetting.CpuPl1,
                         PowerSetting.CpuPl2,
                         PowerSetting.CpuTemperatureLimit,
                         PowerSetting.CpuTurboTimeLimit
                     })
            {
                if (inactive.Locks.IsLocked(setting))
                    inactiveTarget = PowerSettingsController.WithSetting(
                        inactiveTarget,
                        confirmed,
                        setting);
            }
            inactive.Target = inactive.Locks.Any ? inactiveTarget : null;
        }
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

    private static Dictionary<string, PowerModeLockSettings> ClonePowerModeLocks(
        Dictionary<string, PowerModeLockSettings>? source)
    {
        var result = new Dictionary<string, PowerModeLockSettings>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? [])
        {
            result[pair.Key] = new PowerModeLockSettings
            {
                Locks = pair.Value.Locks with { },
                Target = pair.Value.Target
            };
        }
        return result;
    }

    private static PowerModeLockSettings? PowerModeLock(
        Dictionary<string, PowerModeLockSettings> profiles,
        ItsMode mode,
        bool create)
    {
        if (mode == ItsMode.Unknown)
            return null;
        var key = mode.ToString();
        if (profiles.TryGetValue(key, out var profile))
            return profile;
        if (!create)
            return null;
        profile = new PowerModeLockSettings();
        profiles[key] = profile;
        return profile;
    }

    private void SyncLegacyPowerLockFields()
    {
        var modeLock = CurrentPowerModeLock(create: false);
        if (modeLock is null)
            return;
        Settings.PowerSettingsLocks = modeLock.Locks with { };
        Settings.PowerSettingsLockTarget = modeLock.Target;
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
        var previousLegacyProfiles = ClonePowerModeLocks(
            Settings.PowerSettingsLocksByMode);
        var previousNvApiProfiles = ClonePowerModeLocks(
            Settings.NvApiPowerSettingsLocksByMode);
        if (enabled &&
            (!PowerSettingsController.IsValidState(target) ||
             !target!.IsAvailable(setting) ||
             !PowerSettingsController.Value(target, setting).HasValue))
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
        MirrorCommonPowerLock(setting, enabled, target);
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
            Settings.PowerSettingsLocksByMode = previousLegacyProfiles;
            Settings.NvApiPowerSettingsLocksByMode = previousNvApiProfiles;
            Settings.PowerSettingsLocks = previousSelection;
            Settings.PowerSettingsLockTarget = previousTarget;
            SyncPowerSettingsLockTimer();
            error = ex.Message;
            return false;
        }
    }

    private void MirrorCommonPowerLock(
        PowerSetting setting,
        bool enabled,
        PowerSettingsState? target)
    {
        if (setting is not (PowerSetting.CpuPl1 or PowerSetting.CpuPl2 or
            PowerSetting.CpuTemperatureLimit or
            PowerSetting.CpuTurboTimeLimit))
            return;
        var inactiveProfiles = Settings.UseNvApiGpuPower
            ? Settings.PowerSettingsLocksByMode
            : Settings.NvApiPowerSettingsLocksByMode;
        var inactive = PowerModeLock(
            inactiveProfiles,
            Snapshot.ItsMode,
            create: enabled);
        if (inactive is null)
            return;
        inactive.Locks = inactive.Locks.With(setting, enabled);
        if (enabled && target is not null)
        {
            inactive.Target = PowerSettingsController.IsValidState(inactive.Target)
                ? PowerSettingsController.WithSetting(
                    inactive.Target!, target, setting)
                : target;
        }
        else if (!inactive.Locks.Any)
        {
            inactive.Target = null;
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
            if (CanReadPowerSettings)
            {
                if (await _powerSettingsGate.WaitAsync(0))
                {
                    try
                    {
                        _cachedPowerSettings = await Task.Run(
                            ReadPowerSettingsCore);
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Warning(
                            "Power values could not be refreshed: " +
                            ex.Message);
                    }
                    finally
                    {
                        _powerSettingsGate.Release();
                    }
                }
            }

            var itsMode = _confirmedPerformanceModeDuringRefresh ??
                          performance?.ItsMode ??
                          ItsMode.Unknown;
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
            ProcessAutomationTriggerStates(
                battery?.IsAcConnected,
                performance?.GamesRunning);
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

    private void ProcessAutomationTriggerStates(
        bool? acConnected,
        bool? gamesRunning)
    {
        foreach (var trigger in ResolveAutomationTransitions(
                     _lastAutomationAcConnected,
                     acConnected,
                     _lastAutomationGamesRunning,
                     gamesRunning))
            _ = RunAutomationTriggersAsync(trigger);
        if (acConnected.HasValue)
            _lastAutomationAcConnected = acConnected.Value;
        if (gamesRunning.HasValue)
            _lastAutomationGamesRunning = gamesRunning.Value;
    }

    internal static IReadOnlyList<AutomationTriggerKind>
        ResolveAutomationTransitions(
            bool? previousAcConnected,
            bool? acConnected,
            bool? previousGamesRunning,
            bool? gamesRunning)
    {
        var result = new List<AutomationTriggerKind>(2);
        if (previousAcConnected.HasValue && acConnected.HasValue &&
            previousAcConnected.Value != acConnected.Value)
        {
            result.Add(acConnected.Value
                ? AutomationTriggerKind.AcAdapterConnected
                : AutomationTriggerKind.AcAdapterDisconnected);
        }
        if (previousGamesRunning.HasValue && gamesRunning.HasValue &&
            previousGamesRunning.Value != gamesRunning.Value)
        {
            result.Add(gamesRunning.Value
                ? AutomationTriggerKind.GameStarted
                : AutomationTriggerKind.GameStopped);
        }
        return result;
    }

    private async Task RunAutomationTriggersAsync(
        AutomationTriggerKind trigger)
    {
        if (!Settings.AutomationEnabled)
            return;
        var automations = Settings.Automations
            .Where(automation => automation.Triggers.Contains(trigger))
            .Select(automation => automation.Id)
            .ToArray();
        foreach (var automationId in automations)
        {
            ToolkitLog.Info(
                $"Automation trigger {trigger} matched {automationId}.");
            var result = await RunAutomationAsync(automationId);
            if (!result.Success)
                SetStatus(result.Error);
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
        var detector = new ItsModeDetector();
        if (!detector.IsModeSupported(mode))
        {
            return detector.GetControlPath() ==
                   ItsModeControlPath.LegacyLitssvc &&
                   mode == ItsMode.Geek
                ? L(
                    "旧版 LITSSVC 接口不支持极客模式",
                    "The legacy LITSSVC interface does not support Geek mode")
                : L(
                    "当前设备没有可用于此模式的 ITS 切换接口",
                    "No ITS interface for this mode is available on this device");
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
                if (await Task.Run(detector.ReadMode) == mode)
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
                    _powerSettingsLockTimer.Stop();
                    try
                    {
                        await ResetNvPcfAfterPerformanceModeChangeAsync(mode);
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Error(
                            "NVPCF defaults could not be restored " +
                            $"after switching to {mode}.",
                            ex);
                        SetStatus(L(
                            "性能模式已切换，但 NVAPI GPU 功耗恢复默认失败：",
                            "The performance mode changed, but the NVAPI GPU power defaults could not be restored: ") +
                            ex.GetBaseException().Message);
                    }
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

    private async Task ResetNvPcfAfterPerformanceModeChangeAsync(
        ItsMode mode)
    {
        if (!NvApiGpuPowerEnabled ||
            GpuTelemetryControl.Mode != GpuTelemetryMode.Full)
        {
            return;
        }
        await _powerSettingsGate.WaitAsync();
        try
        {
            var confirmed = await Task.Run(() =>
            {
                NvPcfPowerController.ResetToDefaults();
                return NvPcfPowerPolicy.Merge(
                    TryReadWmiPowerSettings(),
                    NvPcfPowerController.ReadAfterReset());
            });
            _cachedPowerSettings = confirmed;
            Snapshot = Snapshot with { PowerSettings = confirmed };
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            ToolkitLog.Info(
                $"NVPCF defaults were restored after switching to {mode}; " +
                "locked values will now be reapplied.");
        }
        finally
        {
            _powerSettingsGate.Release();
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
            Settings.FnPerformanceModeOrder,
            Settings.FnPerformanceModeEnabled,
            current,
            isAcConnected,
            ItsModeController.IsModeSupported);
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
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
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
        bool includeDynamic,
        out string? error)
    {
        var normalized = RefreshRateController.NormalizeConfiguredRates(
            rates);
        if (normalized.Count == 0 && !includeDynamic)
        {
            error = L(
                "至少需要启用一个刷新率。",
                "At least one refresh rate must remain enabled.");
            return false;
        }
        var previous = Settings.RefreshRateCycleHz;
        var previousDynamic = Settings.IncludeDynamicRefreshRateInCycle;
        Settings.RefreshRateCycleHz = normalized;
        Settings.IncludeDynamicRefreshRateInCycle = includeDynamic;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.RefreshRateCycleHz = previous;
            Settings.IncludeDynamicRefreshRateInCycle = previousDynamic;
            error = ex.Message;
            return false;
        }
    }

    internal bool TrySetRefreshRateCycle(
        IEnumerable<uint> rates,
        out string? error) =>
        TrySetRefreshRateCycle(
            rates,
            Settings.IncludeDynamicRefreshRateInCycle,
            out error);

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
            var result = await Task.Run(() =>
                GpuModeController.SetModeFromEffectiveState(
                    source,
                    sourceUsesDirectGraphicsConfiguration,
                    target));
            if (result.RequiresRestart)
            {
                GpuModeRestartState.MarkPending(
                    Settings,
                    source,
                    sourceUsesDirectGraphicsConfiguration,
                    target,
                    _bootSessionId,
                    result);
                if (!string.IsNullOrWhiteSpace(result.Warning))
                    ToolkitLog.Warning(
                        "GPU parent mode was staged, but the child mode " +
                        "will need post-boot completion: " + result.Warning);
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
        if (enabled && !CanUseFanFullSpeed)
        {
            return L(
                "当前风扇后端不支持风扇拉满；请先在设置中启用替代方案。",
                "The current fan backend does not support full speed. Enable the alternative method in Settings first.");
        }
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

    public bool TrySetAlternativeFullSpeedMethod(bool value, out string? error)
    {
        var previousValue = Settings.UseAlternativeFullSpeedMethod;
        var previousInitialized =
            Settings.AlternativeFullSpeedMethodInitialized;
        Settings.UseAlternativeFullSpeedMethod = value;
        Settings.AlternativeFullSpeedMethodInitialized = true;
        try
        {
            CurveProfileStore.SaveSettings(Settings);
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.UseAlternativeFullSpeedMethod = previousValue;
            Settings.AlternativeFullSpeedMethodInitialized =
                previousInitialized;
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetContinuouslyWriteFanTargets(bool value, out string? error) =>
        TrySaveSetting(
            () => Settings.ContinuouslyWriteFanTargets,
            current => Settings.ContinuouslyWriteFanTargets = current,
            value,
            out error);

    public bool TrySetDataSharing(
        bool enabled,
        int port,
        out string? error)
    {
        if (!CurveProfileStore.IsValidDataSharingPort(port))
        {
            error = L(
                "端口号必须为 1 到 65535 之间的整数。",
                "The port must be an integer between 1 and 65535.");
            return false;
        }

        var previousEnabled = Settings.ShareDataWithOtherSoftware;
        var previousPort = Settings.DataSharingPort;
        try
        {
            if (enabled)
                _dataSharing.Start(port);
            else
                _dataSharing.Stop();
            Settings.ShareDataWithOtherSoftware = enabled;
            Settings.DataSharingPort = port;
            CurveProfileStore.SaveSettings(Settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Settings.ShareDataWithOtherSoftware = previousEnabled;
            Settings.DataSharingPort = previousPort;
            try
            {
                if (previousEnabled)
                    _dataSharing.Start(previousPort);
                else
                    _dataSharing.Stop();
            }
            catch (Exception rollbackException)
            {
                ToolkitLog.Error(
                    "Local data sharing could not be rolled back.",
                    rollbackException);
            }
            error = ex.GetBaseException().Message;
            return false;
        }
    }

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

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var state = await Task.Run(GpuModeController.ReadState);
                if (state.CurrentMode == pending)
                {
                    GpuModeRestartState.Clear(Settings);
                    CurveProfileStore.SaveSettings(Settings);
                    return;
                }
                if (!GpuModeController.IsHybridMode(pending))
                {
                    FailPendingGpuMode(
                        $"GPU mode remained {state.CurrentMode} after restart.");
                    return;
                }
                if (state.UsesDirectGraphicsConfiguration)
                {
                    FailPendingGpuMode(
                        "GPU mode transition failed: the system is still " +
                        "using a direct graphics configuration after restart.");
                    return;
                }

                Settings.PendingGpuModePostBootAttempts++;
                var result = await Task.Run(() =>
                    GpuModeController.SetMode(pending));
                if (result.RequiresRestart)
                {
                    FailPendingGpuMode(
                        "GPU child mode could not be applied without " +
                        "requesting another restart.");
                    return;
                }
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Settings.PendingGpuModeLastError =
                    ex.GetBaseException().Message;
                await Task.Delay(750);
            }
        }
        FailPendingGpuMode(
            "GPU post-boot mode application timed out" +
            (lastError is null
                ? "."
                : ": " + lastError.GetBaseException().Message));
    }

    private void FailPendingGpuMode(string error)
    {
        ToolkitLog.Error("Pending GPU mode transition failed: " + error);
        GpuModeRestartState.MarkFailed(Settings, error);
        CurveProfileStore.SaveSettings(Settings);
        PublishGpuTransitionState();
        SetStatus(L("GPU 模式切换失败：", "GPU mode switch failed: ") +
                  error);
    }

    private async Task EnforcePowerSettingsLockAsync()
    {
        if (_disposed ||
            _powerSettingsLockBusy ||
            !CanWritePowerSettings)
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
            var effectiveSelection = selection;
            if (GpuTelemetryControl.Mode != GpuTelemetryMode.Full)
            {
                effectiveSelection = effectiveSelection with
                {
                    NvPcfAcTargetTppLimit = false,
                    NvPcfAcDefaultGpuLimit = false,
                    NvPcfAcMinGpuLimit = false,
                    NvPcfAcMaxGpuLimit = false,
                    NvPcfDynamicBoost = false,
                    NvApiGpuTemperatureLimit = false
                };
            }
            if (!PowerSettingsController.IsValidLockConfiguration(
                    effectiveSelection,
                    target))
            {
                return;
            }

            var current = await Task.Run(ReadPowerSettingsCore);
            var active = CurrentPowerModeLock(create: false);
            if (active?.Locks != selection || active?.Target != target)
            {
                return;
            }

            if (PowerSettingsController.RequiresLockReapply(
                    current,
                    target!,
                    effectiveSelection))
            {
                var restored = PowerSettingsController.ApplyLockedValues(
                    current,
                    target!,
                    effectiveSelection);
                Exception? wmiLockError = null;
                BetaCpuPowerSnapshot? betaReadback = null;
                await Task.Run(() =>
                {
                    var wmiSelection = effectiveSelection;
                    if (IntelMmioCpuPowerEnabled || AmdZenStatesCpuPowerEnabled)
                        wmiSelection = wmiSelection with
                        {
                            CpuPl1 = false, CpuPl2 = false,
                            CpuTurboTimeLimit = false,
                            CpuTemperatureLimit = IntelMmioCpuPowerEnabled &&
                                effectiveSelection.CpuTemperatureLimit
                        };
                    if (PowerSettingsController.CurrentProfile.Writable &&
                        Report?.IsAvailable(FeatureIds.PowerSettings) == true &&
                        wmiSelection.Any)
                    {
                        try
                        {
                            PowerSettingsController.WriteLockedState(
                                current,
                                target!,
                                wmiSelection);
                        }
                        catch (Exception ex)
                        {
                            wmiLockError = ex;
                        }
                    }
                    if (IntelMmioCpuPowerEnabled &&
                        (effectiveSelection.CpuPl1 || effectiveSelection.CpuPl2 ||
                         effectiveSelection.CpuTurboTimeLimit))
                        betaReadback = IntelMmioPowerController.Write(
                            restored.CpuPl1, restored.CpuPl2,
                            restored.CpuTurboTimeLimit);
                    else if (AmdZenStatesCpuPowerEnabled &&
                             (effectiveSelection.CpuPl1 || effectiveSelection.CpuPl2 ||
                              effectiveSelection.CpuTurboTimeLimit ||
                              effectiveSelection.CpuTemperatureLimit))
                    {
                        var names = BetaCpuPowerKind == ThinkBookToolkit.BetaCpuPowerKind.AmdPbo
                            ? new[] { "ppt", "tdc", "edc" }
                            : new[] { "stapm", "fast", "slow" };
                        _ = AmdZenStatesPowerController.Write(names[0], restored.CpuPl1);
                        _ = AmdZenStatesPowerController.Write(names[1], restored.CpuPl2);
                        _ = AmdZenStatesPowerController.Write(names[2], restored.CpuTurboTimeLimit);
                        if (effectiveSelection.CpuTemperatureLimit)
                            _ = AmdZenStatesPowerController.Write("tctlmax", restored.CpuTemperatureLimit);
                        betaReadback = AmdZenStatesPowerController.Read();
                    }
                    if (NvApiGpuPowerEnabled &&
                        (effectiveSelection.NvPcfAcTargetTppLimit ||
                         effectiveSelection.NvPcfAcDefaultGpuLimit ||
                         effectiveSelection.NvPcfAcMinGpuLimit ||
                         effectiveSelection.NvPcfAcMaxGpuLimit ||
                         effectiveSelection.NvPcfDynamicBoost ||
                         effectiveSelection.NvApiGpuTemperatureLimit))
                    {
                        _ = NvPcfPowerController.WriteAndRead(
                            restored,
                            effectiveSelection);
                    }
                });
                if (wmiLockError is not null)
                {
                    throw new InvalidOperationException(
                        "NVAPI GPU locks were applied, but Lenovo WMI power " +
                        "locks could not be restored.",
                        wmiLockError);
                }
                if (betaReadback is not null)
                    restored = MergeBetaCpu(restored, betaReadback);
                _cachedPowerSettings = restored;
                Snapshot = Snapshot with { PowerSettings = restored };
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
                UpdatePowerLockTargetAfterApply(restored);
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
        var hasLocksUsableWithCurrentGpuState = modeLock is not null &&
            (GpuTelemetryControl.Mode == GpuTelemetryMode.Full ||
             modeLock.Locks.CpuPl1 || modeLock.Locks.CpuPl2 ||
             modeLock.Locks.CpuTemperatureLimit ||
             modeLock.Locks.CpuTurboTimeLimit ||
             modeLock.Locks.GpuPowerBoost ||
             modeLock.Locks.GpuConfigurableTgp ||
             modeLock.Locks.GpuTemperatureLimit ||
             modeLock.Locks.GpuToCpuDynamicBoost ||
             modeLock.Locks.Atpp);
        if (!_disposed && modeLock is not null &&
            hasLocksUsableWithCurrentGpuState &&
            PowerSettingsController.IsValidLockConfiguration(
                modeLock.Locks,
                modeLock.Target) &&
            CanWritePowerSettings)
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
        Settings.NvApiPowerSettingsLocksByMode ??=
            new Dictionary<string, PowerModeLockSettings>(
                StringComparer.OrdinalIgnoreCase);
        var profiles = Settings.UseNvApiGpuPower
            ? Settings.NvApiPowerSettingsLocksByMode
            : Settings.PowerSettingsLocksByMode;
        var key = mode.ToString();
        if (profiles.TryGetValue(key, out var profile))
            return profile;
        var migrateLegacy = profiles.Count == 0 &&
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
        profiles[key] = profile;
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

    private static void OnGpuTelemetryModeChanged(GpuTelemetryMode mode)
    {
        if (mode != GpuTelemetryMode.Full)
            NvPcfPowerController.Shutdown();
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
        var fan = DeviceModelDetector.HasSecondFan()
            ? $"F1 {Snapshot.Fans?.Fan1Rpm ?? 0} F2 {Snapshot.Fans?.Fan2Rpm ?? 0}"
            : $"FAN {Snapshot.Fans?.Fan1Rpm ?? 0}";
        var text = $"CPU {FormatTemperature(Snapshot.Temperatures?.CpuTempC)} " +
                   $"GPU {FormatTemperature(Snapshot.Temperatures?.GpuTempC)} " +
                   fan;
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
        GpuTelemetryControl.ModeChanged -= OnGpuTelemetryModeChanged;
        _fnKeyManager.Dispose();
        _macroService.Dispose();
        _dataSharing.Dispose();
        try { _fnKeyNotification?.Close(); } catch { }
        _fnKeyNotification = null;
        _fnKeyNotificationDark = null;
        _temperatureReader?.Dispose();
        NvPcfPowerController.Shutdown();
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
