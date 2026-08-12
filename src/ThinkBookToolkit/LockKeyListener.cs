using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal enum LockKeyKind
{
    CapsLock,
    NumLock
}

internal sealed class LockKeyListener : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<LockKeyKind, bool> _handler;
    private readonly HookProc _hookProc;
    private IntPtr _hook;

    internal LockKeyListener(
        Dispatcher dispatcher,
        Action<LockKeyKind, bool> handler)
    {
        _dispatcher = dispatcher;
        _handler = handler;
        _hookProc = KeyboardHook;
    }

    internal void Start()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Start);
            return;
        }
        if (_hook != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _hook = SetWindowsHookEx(
            WhKeyboardLl,
            _hookProc,
            moduleHandle,
            0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The lock-key listener could not be installed.");
        }
    }

    private IntPtr KeyboardHook(
        int code,
        IntPtr message,
        IntPtr data)
    {
        if (code >= 0 &&
            (message == new IntPtr(WmKeyUp) ||
             message == new IntPtr(WmSysKeyUp)))
        {
            var key = Marshal.ReadInt32(data);
            if (key is VkCapital or VkNumLock)
            {
                var kind = key == VkCapital
                    ? LockKeyKind.CapsLock
                    : LockKeyKind.NumLock;
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                    {
                        var enabled = (GetKeyState(key) & 1) != 0;
                        _handler(kind, enabled);
                    }));
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }
        if (_hook == IntPtr.Zero)
            return;
        _ = UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(
        int code,
        IntPtr message,
        IntPtr data);

    private const int WhKeyboardLl = 13;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int VkCapital = 0x14;
    private const int VkNumLock = 0x90;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        HookProc hook,
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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
