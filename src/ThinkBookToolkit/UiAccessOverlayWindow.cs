using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThinkBookToolkit;

/// <summary>
/// Shared native-window behavior for UIAccess overlays. It mirrors LLT's OSD
/// handling: overlays never activate, stay out of the taskbar, preserve their
/// extended styles when Windows recreates the taskbar and reassert TOPMOST
/// without stealing focus.
/// </summary>
internal abstract class UiAccessOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmStyleChanging = 0x007C;
    private const int WmWindowPosChanging = 0x0046;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly uint TaskbarCreatedMessage =
        RegisterWindowMessage("TaskbarCreated");

    private HwndSource? _source;
    private bool _clickThrough;

    protected UiAccessOverlayWindow()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => EscalateZOrder();
        Closed += (_, _) =>
        {
            if (_source is not null)
                _source.RemoveHook(WndProc);
            _source = null;
        };
    }

    protected void SetOverlayClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyOverlayStyles();
    }

    protected void EscalateZOrder()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);
        ApplyOverlayStyles();
        EscalateZOrder();
    }

    private void ApplyOverlayStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate;
        if (_clickThrough)
            style |= WsExTransparent;
        else
            style &= ~WsExTransparent;
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private IntPtr WndProc(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmStyleChanging &&
            wParam.ToInt32() == GwlExStyle)
        {
            var value = Marshal.PtrToStructure<StyleStruct>(lParam);
            value.StyleNew |= WsExToolWindow | WsExNoActivate;
            if (_clickThrough)
                value.StyleNew |= WsExTransparent;
            Marshal.StructureToPtr(value, lParam, false);
            handled = true;
        }
        else if (message == WmWindowPosChanging)
        {
            var value = Marshal.PtrToStructure<WindowPosition>(lParam);
            value.Flags |= SwpNoActivate;
            Marshal.StructureToPtr(value, lParam, false);
            handled = true;
        }
        else if ((uint)message == TaskbarCreatedMessage)
        {
            ApplyOverlayStyles();
            EscalateZOrder();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public int StyleOld;
        public int StyleNew;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr window,
        int index,
        IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
