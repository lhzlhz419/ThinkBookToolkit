using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed class KeyboardMacroService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private static readonly UIntPtr Magic = new(0x5442544D);

    private readonly AppSettings _settings;
    private readonly HookProc _hookProc;
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly HashSet<int> _triggerKeysDown = [];
    private readonly HashSet<int> _recordingKeysDown = [];
    private IntPtr _hook;
    private Action<KeyboardMacroEvent>? _recorded;
    private Action? _recordingStopped;
    private Action<int?>? _triggerCaptured;
    private int? _capturedKeyAwaitingUp;
    private long _lastRecordingTick;
    private bool _hasRecordedEvent;
    private bool _recording;
    private bool _disposed;

    public KeyboardMacroService(AppSettings settings)
    {
        _settings = settings;
        _hookProc = HookCallback;
    }

    public bool IsRecording => _recording;

    public string? Start()
    {
        if (_hook != IntPtr.Zero)
            return null;
        _hook = SetWindowsHookEx(
            WhKeyboardLl,
            _hookProc,
            GetModuleHandle(null),
            0);
        if (_hook != IntPtr.Zero)
            return null;
        return new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    public string? StartRecording(
        Action<KeyboardMacroEvent> recorded,
        Action stopped)
    {
        var error = Start();
        if (!string.IsNullOrWhiteSpace(error))
            return error;
        _recorded = recorded;
        _recordingStopped = stopped;
        _lastRecordingTick = Environment.TickCount64;
        _hasRecordedEvent = false;
        _recordingKeysDown.Clear();
        _recording = true;
        return null;
    }

    public void StopRecording()
    {
        if (!_recording)
            return;
        _recording = false;
        _recordingKeysDown.Clear();
        _recorded = null;
        var stopped = _recordingStopped;
        _recordingStopped = null;
        stopped?.Invoke();
    }

    public string? StartTriggerCapture(Action<int?> captured)
    {
        var error = Start();
        if (!string.IsNullOrWhiteSpace(error))
            return error;
        _triggerCaptured = captured;
        return null;
    }

    public void CancelTriggerCapture()
    {
        _triggerCaptured = null;
        _capturedKeyAwaitingUp = null;
    }

    public async Task<string?> PlayAsync(
        string macroId,
        CancellationToken cancellationToken = default,
        string executionSource = "direct")
    {
        var macro = _settings.Macros.FirstOrDefault(item =>
            item.Id.Equals(macroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
        {
            ToolkitLog.Warning(
                $"Macro request rejected: source={executionSource}; " +
                $"macro={macroId}; reason=not found.");
            return "Macro not found.";
        }
        if (macro.Events.Count == 0)
        {
            ToolkitLog.Warning(
                $"Macro request rejected: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}); reason=no events.");
            return "Macro has no keyboard events.";
        }
        if (_playGate.CurrentCount == 0)
        {
            ToolkitLog.Info(
                $"Macro queued: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}).");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        try
        {
            await _playGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ToolkitLog.Warning(
                $"Macro cancelled while queued: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}).");
            return "Macro playback was cancelled.";
        }
        var timer = Stopwatch.StartNew();
        var pressed = new HashSet<int>();
        try
        {
            ToolkitLog.Info(
                $"Macro started: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}); " +
                $"events={macro.Events.Count}.");
            for (var index = 0; index < macro.Events.Count; index++)
            {
                var item = macro.Events[index];
                await Task.Delay(
                        item.DelayMilliseconds,
                        linked.Token)
                    .ConfigureAwait(false);
                ToolkitLog.Info(
                    $"Macro event: source={executionSource}; " +
                    $"macro={macro.Id}; event={index + 1}/" +
                    $"{macro.Events.Count}; {FormatEventForLog(item)}.");
                Send(item.VirtualKey, item.Direction);
                if (item.Direction == KeyboardMacroDirection.Down)
                    pressed.Add(item.VirtualKey);
                else
                    pressed.Remove(item.VirtualKey);
            }
            ToolkitLog.Info(
                $"Macro completed: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}); " +
                $"elapsed={timer.Elapsed.TotalMilliseconds:0} ms.");
            return null;
        }
        catch (OperationCanceledException)
        {
            var closing = _disposeCancellation.IsCancellationRequested;
            ToolkitLog.Warning(
                $"Macro cancelled: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}); " +
                $"reason={(closing ? "Toolkit closing" : "request cancelled")}; " +
                $"elapsed={timer.Elapsed.TotalMilliseconds:0} ms.");
            return closing
                ? "Macro playback was cancelled because Toolkit is closing."
                : "Macro playback was cancelled.";
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                $"Macro failed: source={executionSource}; " +
                $"macro={macro.Name} ({macro.Id}); " +
                $"elapsed={timer.Elapsed.TotalMilliseconds:0} ms.",
                ex);
            return ex.GetBaseException().Message;
        }
        finally
        {
            foreach (var virtualKey in pressed)
            {
                try { Send(virtualKey, KeyboardMacroDirection.Up); }
                catch { }
            }
            if (pressed.Count > 0)
            {
                ToolkitLog.Warning(
                    $"Macro cleanup released {pressed.Count} key(s): " +
                    $"source={executionSource}; macro={macro.Id}.");
            }
            _playGate.Release();
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code != HcAction || data == IntPtr.Zero)
            return CallNextHookEx(_hook, code, message, data);
        var value = Marshal.PtrToStructure<KbdLlHookStruct>(data);
        if ((value.Flags & LlkhfInjected) != 0 || value.ExtraInfo == Magic)
            return CallNextHookEx(_hook, code, message, data);
        var isDown = message.ToInt64() is WmKeyDown or WmSysKeyDown;
        var isUp = message.ToInt64() is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
            return CallNextHookEx(_hook, code, message, data);
        var virtualKey = unchecked((int)value.VirtualKey);
        if (_capturedKeyAwaitingUp == virtualKey && isUp)
        {
            _capturedKeyAwaitingUp = null;
            return new IntPtr(1);
        }
        if (_triggerCaptured is { } triggerCaptured && isDown)
        {
            _triggerCaptured = null;
            _capturedKeyAwaitingUp = virtualKey;
            triggerCaptured(virtualKey);
            return new IntPtr(1);
        }
        if (_recording)
        {
            if (isDown && !_recordingKeysDown.Add(virtualKey))
                return new IntPtr(1);
            if (isUp)
                _recordingKeysDown.Remove(virtualKey);
            var now = Environment.TickCount64;
            var delay = _hasRecordedEvent
                ? (int)Math.Clamp(
                    now - _lastRecordingTick,
                    0,
                    KeyboardMacroDefaults.MaximumDelayMilliseconds)
                : 0;
            _lastRecordingTick = now;
            _hasRecordedEvent = true;
            _recorded?.Invoke(new KeyboardMacroEvent
            {
                VirtualKey = virtualKey,
                Direction = isDown
                    ? KeyboardMacroDirection.Down
                    : KeyboardMacroDirection.Up,
                DelayMilliseconds = delay
            });
            return new IntPtr(1);
        }
        if (!_settings.MacroEnabled)
            return CallNextHookEx(_hook, code, message, data);
        var macro = _settings.Macros.FirstOrDefault(item =>
            item.TriggerVirtualKey == virtualKey && item.Events.Count > 0);
        if (macro is null)
            return CallNextHookEx(_hook, code, message, data);
        if (isDown)
        {
            if (_triggerKeysDown.Add(virtualKey))
                _ = PlayAndReportAsync(macro.Id);
        }
        else
        {
            _triggerKeysDown.Remove(virtualKey);
        }
        return new IntPtr(1);
    }

    private async Task PlayAndReportAsync(string macroId)
    {
        var error = await PlayAsync(
                macroId,
                executionSource: "key-binding")
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
            ToolkitLog.Warning("A bound keyboard macro failed: " + error);
    }

    internal static string FormatEventForLog(KeyboardMacroEvent item) =>
        $"key={KeyboardMacroKeyNames.Format(item.VirtualKey)}; " +
        $"direction={item.Direction}; delay={item.DelayMilliseconds} ms";

    private static void Send(int virtualKey, KeyboardMacroDirection direction)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = unchecked((ushort)virtualKey),
                    Flags = direction == KeyboardMacroDirection.Up
                        ? KeyeventfKeyup
                        : 0,
                    ExtraInfo = Magic
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SendInput did not inject the keyboard event.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopRecording();
        CancelTriggerCapture();
        _disposeCancellation.Cancel();
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _disposeCancellation.Dispose();
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint count,
        Input[] inputs,
        int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
