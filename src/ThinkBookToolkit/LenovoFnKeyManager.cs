using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

[Flags]
internal enum LenovoDriverKey : uint
{
    FnQ = 1,
    FnF10 = 32,
    FnF4 = 256,
    FnSpace = 4096,
    FnF8 = 8192
}

internal enum LenovoSpecialKey
{
    FnF9 = 1,
    FnLockOn = 2,
    FnLockOff = 3,
    FnPrtSc = 4,
    CameraOn = 12,
    CameraOff = 13,
    FnR = 16,
    FnR2 = 0x0041002A,
    FnF8ThinkBook = 41,
    FnN = 42,
    FnPrtSc2 = 45,
    FnF4 = 62,
    FnF8 = 63,
    WhiteBacklightOff = 64,
    WhiteBacklightLow = 65,
    WhiteBacklightHigh = 66
}

internal enum LenovoFnKeyEventSource
{
    EnergyDriver,
    UtilityEvent
}

internal enum ScreenSnippingEventSource
{
    EnergyDriver,
    UtilityEvent,
    StandardKeyboard
}

internal sealed class LenovoFnKeyManager : IDisposable
{
    private readonly ToolkitRuntimeService _runtime;
    private EnergyDriverKeyListener? _driverListener;
    private ManagementEventWatcher? _specialKeyWatcher;
    private LockKeyListener? _lockKeyListener;
    private AudioIndicatorMonitor? _audioIndicatorMonitor;
    private bool _running;
    private readonly object _mirrorGate = new();
    private long _lastMicrophoneDriverTick;
    private long _lastRefreshRateEventTick;
    private long _lastScreenSnippingEventTick;
    private ScreenSnippingEventSource _lastScreenSnippingEventSource;
    private bool _standardPrintScreenDown;
    private int _hotkeysDisabledByToolkit;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>>
        _pendingPresses = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly TimeSpan DoublePressInterval =
        TimeSpan.FromMilliseconds(300);

    internal LenovoFnKeyManager(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
    }

    internal bool IsRunning => _running;
    internal bool DiscoveryMode { get; set; }

    internal async Task<string?> StartAsync()
    {
        if (_running)
            return null;
        try
        {
            await LenovoHotkeysController.DisableAsync();
            Volatile.Write(ref _hotkeysDisabledByToolkit, 1);
            _driverListener = new EnergyDriverKeyListener(
                HandleDriverKeysAsync);
            _driverListener.Start();
            StartSpecialKeyWatcher();
            var dispatcher = Application.Current?.Dispatcher ??
                throw new InvalidOperationException(
                    "The application dispatcher is unavailable.");
            _lockKeyListener = new LockKeyListener(
                dispatcher,
                HandleLockKeyChanged);
            _lockKeyListener.Start();
            _audioIndicatorMonitor = new AudioIndicatorMonitor();
            _audioIndicatorMonitor.Start();
            _running = true;
            ToolkitLog.Info(
                "Lenovo Hotkeys was disabled and the Fn-key listeners were started.");
            return null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Fn-key takeover could not be started.", ex);
            StopListeners();
            try
            {
                await LenovoHotkeysController.EnableAsync();
                Volatile.Write(ref _hotkeysDisabledByToolkit, 0);
            }
            catch (Exception restoreException)
            {
                ToolkitLog.Error(
                    "Lenovo Hotkeys could not be restored after Fn-key takeover failed.",
                    restoreException);
            }
            return ex.GetBaseException().Message;
        }
    }

    internal async Task<string?> StopAsync(bool restoreLenovoHotkeys)
    {
        StopListeners();
        if (!restoreLenovoHotkeys)
            return null;
        if (Interlocked.Exchange(ref _hotkeysDisabledByToolkit, 0) == 0)
            return null;
        try
        {
            await LenovoHotkeysController.EnableAsync();
            ToolkitLog.Info("Lenovo Hotkeys was restored.");
            return null;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _hotkeysDisabledByToolkit, 1);
            ToolkitLog.Error("Lenovo Hotkeys could not be restored.", ex);
            return ex.GetBaseException().Message;
        }
    }

    private void StartSpecialKeyWatcher()
    {
        try
        {
            _specialKeyWatcher = new ManagementEventWatcher(
                "root\\WMI",
                "SELECT * FROM LENOVO_UTILITY_EVENT");
            _specialKeyWatcher.EventArrived += OnSpecialKeyArrived;
            _specialKeyWatcher.Start();
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "The Lenovo utility-event Fn-key listener is unavailable: " +
                ex.Message);
            _specialKeyWatcher?.Dispose();
            _specialKeyWatcher = null;
        }
    }

    private void OnSpecialKeyArrived(
        object sender,
        EventArrivedEventArgs args)
    {
        try
        {
            var raw = Convert.ToInt32(
                args.NewEvent.Properties["PressTypeDataVal"]?.Value);
            PublishDiscovery(
                raw,
                "WMI",
                Enum.IsDefined(typeof(LenovoSpecialKey), raw)
                    ? ((LenovoSpecialKey)raw).ToString()
                    : $"Fn + 0x{raw:X}");
            if (DiscoveryMode)
                return;
            _ = HandleSpecialKeyAsync(
                (LenovoSpecialKey)raw,
                FnAutomationKeyIds.FromDiscovered("WMI", raw));
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "A Lenovo utility-event Fn key could not be handled: " +
                ex.Message);
        }
    }

    private async Task HandleDriverKeysAsync(uint rawValue)
    {
        PublishDiscovery(
            unchecked((int)rawValue),
            "IOCTL",
            Enum.IsDefined(typeof(LenovoDriverKey), rawValue)
                ? ((LenovoDriverKey)rawValue).ToString()
                : $"Fn + 0x{rawValue:X} (Driver)");
        if (DiscoveryMode)
            return;
        var value = (LenovoDriverKey)rawValue;
        if (value.HasFlag(LenovoDriverKey.FnF4))
        {
            _ = IsMirroredMicrophoneEvent(
                LenovoFnKeyEventSource.EnergyDriver);
        }
        if (await TryRunCustomFnAsync(
                FnAutomationKeyIds.FromDiscovered(
                    "IOCTL",
                    unchecked((int)rawValue))))
        {
            return;
        }
        foreach (LenovoDriverKey key in Enum.GetValues<LenovoDriverKey>())
        {
            if (!value.HasFlag(key))
                continue;
            if (key == LenovoDriverKey.FnF10 &&
                !TryClaimScreenSnippingEvent(
                    ScreenSnippingEventSource.EnergyDriver))
            {
                continue;
            }
            var keyId = DriverKeyBindingId(key);
            if (keyId.Length > 0 && await TryRunCustomFnAsync(keyId))
                continue;
            switch (key)
            {
                case LenovoDriverKey.FnQ:
                    await _runtime.TogglePerformanceModeFromFnAsync();
                    break;
                case LenovoDriverKey.FnSpace:
                    await NotifyKeyboardBacklightAsync();
                    break;
                case LenovoDriverKey.FnF10:
                    OpenScreenSnippingTool();
                    break;
                case LenovoDriverKey.FnF4:
                    ToggleMicrophone();
                    break;
                case LenovoDriverKey.FnF8:
                    ToggleAirplaneMode();
                    break;
            }
            _runtime.NotifyControlStateChanged();
        }
    }

    private void PublishDiscovery(int code, string channel, string name)
    {
        if (!DiscoveryMode)
            return;
        _runtime.PublishFnKeyDiscovered(new(
            code,
            channel,
            name,
            DateTimeOffset.Now));
    }

    private async Task HandleSpecialKeyAsync(
        LenovoSpecialKey key,
        string discoveredKeyId)
    {
        if (key == LenovoSpecialKey.FnF4)
        {
            await Task.Delay(120).ConfigureAwait(false);
            if (IsMirroredMicrophoneEvent(
                    LenovoFnKeyEventSource.UtilityEvent))
                return;
            if (await TryRunCustomFnAsync(discoveredKeyId))
                return;
            if (await TryRunCustomFnAsync(FnAutomationKeyIds.FnF4))
                return;
            ToggleMicrophone(LenovoFnKeyEventSource.UtilityEvent);
            _runtime.NotifyControlStateChanged();
            return;
        }

        var screenSnippingKey = key is
            LenovoSpecialKey.FnPrtSc or LenovoSpecialKey.FnPrtSc2;
        if (screenSnippingKey &&
            !TryClaimScreenSnippingEvent(
                ScreenSnippingEventSource.UtilityEvent))
            return;

        if (await TryRunCustomFnAsync(discoveredKeyId))
            return;

        var keyId = SpecialKeyBindingId(key);
        if (keyId.Length > 0 && await TryRunCustomFnAsync(keyId))
            return;
        if (screenSnippingKey &&
            await TryRunCustomFnAsync(FnAutomationKeyIds.PrintScreen))
        {
            return;
        }

        switch (key)
        {
            case LenovoSpecialKey.FnLockOn:
            case LenovoSpecialKey.FnLockOff:
                _runtime.ShowFnKeyNotification(
                    "Fn Lock",
                    key == LenovoSpecialKey.FnLockOn
                        ? _runtime.L("已开启", "On")
                        : _runtime.L("已关闭", "Off"));
                break;
            case LenovoSpecialKey.CameraOn:
            case LenovoSpecialKey.CameraOff:
                _runtime.ShowFnKeyNotification(
                    _runtime.L("摄像头", "Camera"),
                    key == LenovoSpecialKey.CameraOn
                        ? _runtime.L("已开启", "On")
                        : _runtime.L("已关闭", "Off"));
                break;
            case LenovoSpecialKey.FnPrtSc:
            case LenovoSpecialKey.FnPrtSc2:
                OpenScreenSnippingTool();
                break;
            case LenovoSpecialKey.FnF8ThinkBook:
                await NotifyTouchpadAsync(toggle: true);
                break;
            case LenovoSpecialKey.WhiteBacklightOff:
            case LenovoSpecialKey.WhiteBacklightLow:
            case LenovoSpecialKey.WhiteBacklightHigh:
                _runtime.ShowFnKeyNotification(
                    _runtime.L("键盘背光", "Keyboard backlight"),
                    key switch
                    {
                        LenovoSpecialKey.WhiteBacklightOff =>
                            _runtime.L("关闭", "Off"),
                        LenovoSpecialKey.WhiteBacklightLow =>
                            _runtime.L("低", "Low"),
                        _ => _runtime.L("高", "High")
                    });
                break;
            case LenovoSpecialKey.FnR:
            case LenovoSpecialKey.FnR2:
                await ToggleRefreshRateAsync();
                break;
            case LenovoSpecialKey.FnF8:
                ToggleAirplaneMode();
                break;
            case LenovoSpecialKey.FnN:
                _runtime.ShowMainWindow();
                break;
        }
        _runtime.NotifyControlStateChanged();
    }

    internal static string DriverKeyBindingId(LenovoDriverKey key) => key switch
    {
        LenovoDriverKey.FnQ => FnAutomationKeyIds.FnQ,
        LenovoDriverKey.FnSpace => FnAutomationKeyIds.FnSpace,
        LenovoDriverKey.FnF10 => FnAutomationKeyIds.FnF10,
        LenovoDriverKey.FnF4 => FnAutomationKeyIds.FnF4,
        LenovoDriverKey.FnF8 => FnAutomationKeyIds.FnF8,
        _ => string.Empty
    };

    internal static string SpecialKeyBindingId(LenovoSpecialKey key) =>
        key switch
        {
            LenovoSpecialKey.FnLockOn or LenovoSpecialKey.FnLockOff =>
                FnAutomationKeyIds.FnLock,
            LenovoSpecialKey.FnPrtSc or LenovoSpecialKey.FnPrtSc2 =>
                FnAutomationKeyIds.FnF10,
            LenovoSpecialKey.FnF8ThinkBook => FnAutomationKeyIds.Touchpad,
            LenovoSpecialKey.FnR or LenovoSpecialKey.FnR2 =>
                FnAutomationKeyIds.RefreshRate,
            LenovoSpecialKey.FnF9 => FnAutomationKeyIds.FnF9,
            LenovoSpecialKey.FnN => FnAutomationKeyIds.FnN,
            LenovoSpecialKey.FnF8 => FnAutomationKeyIds.FnF8,
            _ => string.Empty
        };

    private async Task<bool> TryRunCustomFnAsync(string keyId)
    {
        if (!_runtime.HasFnAutomationBinding(keyId))
            return false;
        if (!_runtime.HasFnAutomationBinding(keyId, doublePress: true))
            return await _runtime.TryRunFnAutomationAsync(keyId);

        if (_pendingPresses.TryGetValue(keyId, out var pending))
        {
            pending.TrySetResult(true);
            return true;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingPresses.TryAdd(keyId, completion))
            return true;
        _ = ResolvePendingFnPressAsync(keyId, completion);
        return true;
    }

    private async Task ResolvePendingFnPressAsync(
        string keyId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            var delay = Task.Delay(DoublePressInterval);
            var completed = await Task.WhenAny(
                completion.Task,
                delay).ConfigureAwait(false);
            _pendingPresses.TryRemove(keyId, out _);
            if (completed == delay)
            {
                if (_runtime.HasFnAutomationBinding(
                        keyId,
                        doublePress: false))
                {
                    await _runtime.TryRunFnAutomationAsync(keyId)
                        .ConfigureAwait(false);
                }
                return;
            }
            await _runtime.TryRunFnAutomationAsync(
                    keyId,
                    doublePress: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                $"The custom Fn-key action for '{keyId}' failed.",
                ex);
        }
    }

    private async Task NotifyTouchpadAsync(bool toggle)
    {
        var state = await Task.Run(() => InputSettingsController.ReadState());
        var touchpad = state.Touchpad;
        if (toggle && touchpad.Supported)
        {
            touchpad = await Task.Run(() =>
                InputSettingsController.SetState(
                    InputSettingKind.Touchpad,
                    !touchpad.Enabled));
        }
        _runtime.ShowFnKeyNotification(
            _runtime.L("触控板", "Touchpad"),
            !touchpad.Supported
                ? _runtime.L("状态不可用", "Status unavailable")
                : touchpad.Enabled
                    ? _runtime.L("已开启", "On")
                    : _runtime.L("已关闭", "Off"));
    }

    /// <summary>
    /// Some ThinkBook firmware exposes the physical Fn+F10 screenshot key as
    /// a normal VK_SNAPSHOT only while Toolkit owns the foreground window.
    /// Feed that path into the same Fn pipeline used by Lenovo WMI events and
    /// consume it so Windows does not take an immediate full-screen capture.
    /// </summary>
    internal bool HandleStandardKeyboardEvent(int virtualKey, bool isDown)
    {
        const int vkSnapshot = 0x2C;
        if (virtualKey != vkSnapshot || !_running)
            return false;

        var shouldDispatch = false;
        lock (_mirrorGate)
        {
            if (isDown)
            {
                if (!_standardPrintScreenDown)
                {
                    _standardPrintScreenDown = true;
                    shouldDispatch = true;
                }
            }
            else
            {
                _standardPrintScreenDown = false;
            }
        }

        if (!shouldDispatch)
            return true;

        PublishDiscovery(
            vkSnapshot,
            "KEYBOARD",
            "Fn + F10 / PrintScreen");
        if (!DiscoveryMode && TryClaimScreenSnippingEvent(
                ScreenSnippingEventSource.StandardKeyboard))
            _ = HandleScreenSnippingKeyAsync();
        return true;
    }

    private async Task HandleScreenSnippingKeyAsync()
    {
        try
        {
            if (await TryRunCustomFnAsync(FnAutomationKeyIds.FnF10))
                return;
            // Preserve bindings saved by builds which exposed this physical
            // key as the separate Fn+PrtSc entry.
            if (await TryRunCustomFnAsync(FnAutomationKeyIds.PrintScreen))
                return;
            OpenScreenSnippingTool();
            _runtime.NotifyControlStateChanged();
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "The standard PrintScreen Fn-key path failed.",
                ex);
        }
    }

    private bool TryClaimScreenSnippingEvent(
        ScreenSnippingEventSource source)
    {
        lock (_mirrorGate)
        {
            var now = Environment.TickCount64;
            // A single physical key can be reported by the keyboard hook and
            // a Lenovo event listener. Suppress only that cross-channel mirror;
            // repeated presses from the same channel must remain available for
            // user-configured double-press actions.
            if (source != _lastScreenSnippingEventSource &&
                now - _lastScreenSnippingEventTick < 250)
                return false;
            _lastScreenSnippingEventTick = now;
            _lastScreenSnippingEventSource = source;
            return true;
        }
    }

    private void OpenScreenSnippingTool()
    {
        try
        {
            Process.Start(CreateScreenSnippingStartInfo());
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "The Windows screen-snipping tool could not be opened.",
                ex);
            _runtime.SetStatus(
                _runtime.L(
                    "无法打开 Windows 截图工具：",
                    "Could not open Windows screen snipping: ") +
                ex.GetBaseException().Message);
        }
    }

    internal static ProcessStartInfo CreateScreenSnippingStartInfo() => new()
    {
        FileName = "explorer.exe",
        Arguments = "ms-screenclip:",
        UseShellExecute = true
    };

    private async Task NotifyKeyboardBacklightAsync()
    {
        var state = await Task.Run(KeyboardBacklightController.ReadState);
        var detail = state.Level switch
        {
            KeyboardBacklightLevel.Off => _runtime.L("关闭", "Off"),
            KeyboardBacklightLevel.Low => _runtime.L("低", "Low"),
            KeyboardBacklightLevel.High => _runtime.L("高", "High"),
            KeyboardBacklightLevel.Auto => _runtime.L("自动", "Auto"),
            _ => _runtime.L("状态不可用", "Status unavailable")
        };
        _runtime.ShowFnKeyNotification(
            _runtime.L("键盘背光", "Keyboard backlight"),
            detail);
    }

    private void ToggleMicrophone(
        LenovoFnKeyEventSource source = LenovoFnKeyEventSource.EnergyDriver)
    {
        if (IsMirroredMicrophoneEvent(source))
            return;
        var success = FnKeySystemActions.TryToggleMicrophones(
            out var muted,
            out var error);
        if (success)
        {
            _runtime.ShowFnKeyNotification(
                _runtime.L("麦克风", "Microphone"),
                muted
                    ? _runtime.L("已静音", "Muted")
                    : _runtime.L("已取消静音", "Unmuted"));
        }
        else
        {
            _runtime.SetStatus(
                _runtime.L(
                    "麦克风切换失败：",
                    "Microphone switch failed: ") +
                error);
        }
    }

    private async Task ToggleRefreshRateAsync()
    {
        lock (_mirrorGate)
        {
            var now = Environment.TickCount64;
            if (now - _lastRefreshRateEventTick < 120)
                return;
            _lastRefreshRateEventTick = now;
        }
        var result = await Task.Run(() =>
        {
            var success = RefreshRateController.TryCycleInternalDisplay(
                _runtime.Settings.RefreshRateCycleHz,
                _runtime.Settings.IncludeDynamicRefreshRateInCycle,
                out var refreshRate,
                out var error);
            return (success, refreshRate, error);
        });
        if (result.success)
        {
            _runtime.ShowFnKeyNotification(
                string.Empty,
                result.refreshRate.DisplayName);
        }
        else
        {
            _runtime.SetStatus(
                _runtime.L(
                    "刷新率切换失败：",
                    "Refresh-rate switch failed: ") +
                result.error);
        }
    }

    private bool IsMirroredMicrophoneEvent(
        LenovoFnKeyEventSource source)
    {
        lock (_mirrorGate)
        {
            var now = Environment.TickCount64;
            if (source == LenovoFnKeyEventSource.UtilityEvent &&
                now - _lastMicrophoneDriverTick < 500)
            {
                return true;
            }
            if (source == LenovoFnKeyEventSource.EnergyDriver)
                _lastMicrophoneDriverTick = now;
            return false;
        }
    }

    private void ToggleAirplaneMode()
    {
        var success = FnKeySystemActions.TryToggleAirplaneMode(
            out var enabled,
            out var error);
        _runtime.ShowFnKeyNotification(
            _runtime.L("飞行模式", "Airplane mode"),
            success
                ? enabled
                    ? _runtime.L("已开启", "On")
                    : _runtime.L("已关闭", "Off")
                : _runtime.L("切换失败：", "Switch failed: ") + error);
    }

    private void HandleLockKeyChanged(
        LockKeyKind kind,
        bool enabled)
    {
        var show = kind == LockKeyKind.CapsLock
            ? _runtime.Settings.ShowCapsLockOsd
            : _runtime.Settings.ShowNumLockOsd;
        if (!show)
            return;
        _runtime.ShowFnKeyNotification(
            kind == LockKeyKind.CapsLock ? "CapsLock" : "NumLock",
            enabled
                ? _runtime.L("已开启", "On")
                : _runtime.L("已关闭", "Off"));
    }

    private void StopListeners()
    {
        _running = false;
        _audioIndicatorMonitor?.Dispose();
        _audioIndicatorMonitor = null;
        _lockKeyListener?.Dispose();
        _lockKeyListener = null;
        if (_specialKeyWatcher is not null)
        {
            try { _specialKeyWatcher.Stop(); } catch { }
            _specialKeyWatcher.EventArrived -= OnSpecialKeyArrived;
            _specialKeyWatcher.Dispose();
            _specialKeyWatcher = null;
        }
        _driverListener?.Dispose();
        _driverListener = null;
    }

    internal string? StopAndRestoreSynchronously(bool startService = true)
    {
        StopListeners();
        if (Interlocked.Exchange(ref _hotkeysDisabledByToolkit, 0) == 0)
            return null;
        try
        {
            LenovoHotkeysController.Enable(startService);
            ToolkitLog.Info("Lenovo Hotkeys was restored synchronously.");
            return null;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _hotkeysDisabledByToolkit, 1);
            ToolkitLog.Error("Lenovo Hotkeys could not be restored.", ex);
            return ex.GetBaseException().Message;
        }
    }

    public void Dispose()
    {
        _ = StopAndRestoreSynchronously();
    }
}

internal sealed class EnergyDriverKeyListener : IDisposable
{
    private const uint IoctlWaitHandle = 0x831020D8;
    private const uint IoctlKeyValue = 0x831020CC;
    private readonly Func<uint, Task> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private LenovoEnergyDriver? _driver;
    private ManualResetEvent? _notification;
    private Task? _listenTask;
    private Task? _processTask;
    private readonly Channel<uint> _values = Channel.CreateUnbounded<uint>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    internal EnergyDriverKeyListener(Func<uint, Task> handler)
    {
        _handler = handler;
    }

    internal void Start()
    {
        _driver = new LenovoEnergyDriver();
        _notification = new ManualResetEvent(false);
        BindNotification(_driver.Handle, _notification);
        _ = _driver.Call(IoctlKeyValue, 0);
        _listenTask = Task.Run(Listen);
        _processTask = Task.Run(ProcessAsync);
    }

    private void Listen()
    {
        var notification = _notification!;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var result = WaitHandle.WaitAny(
                    [notification, _cancellation.Token.WaitHandle]);
                if (result != 0 || _cancellation.IsCancellationRequested)
                    return;
                notification.Reset();
                var value = _driver!.Call(IoctlKeyValue, 0);
                if (value != 0)
                    _values.Writer.TryWrite(value);
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("The EnergyDrv Fn-key listener stopped.", ex);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var value in _values.Reader.ReadAllAsync(
                               _cancellation.Token))
            {
                await _handler(value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("An EnergyDrv Fn-key action failed.", ex);
        }
    }

    private static void BindNotification(
        SafeFileHandle driver,
        WaitHandle notification)
    {
        var input = Marshal.AllocHGlobal(16);
        try
        {
            for (var index = 0; index < 16; index++)
                Marshal.WriteByte(input, index, 0);
            Marshal.WriteInt32(
                input,
                unchecked((int)notification.SafeWaitHandle
                    .DangerousGetHandle().ToInt64()));
            if (!Native.DeviceIoControl(
                    driver,
                    IoctlWaitHandle,
                    input,
                    16,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "EnergyDrv did not accept the Fn-key event handle.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _values.Writer.TryComplete();
        _notification?.Set();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _processTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _notification?.Dispose();
        _driver?.Dispose();
        _cancellation.Dispose();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr input,
            int inputSize,
            IntPtr output,
            int outputSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
