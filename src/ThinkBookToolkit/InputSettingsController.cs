using System;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

internal enum InputSettingKind
{
    FunctionLock,
    CapsLockOsd,
    NumLockOsd,
    FnCtrlSwap,
    Touchpad
}

internal sealed record ToggleSettingState(bool Supported, bool Enabled, string? Error = null)
{
    public static ToggleSettingState Unsupported() => new(false, false);

    public static ToggleSettingState Failed(Exception exception) =>
        new(false, false, exception.Message);
}

internal sealed record InputSettingsState(
    ToggleSettingState FunctionLock,
    ToggleSettingState CapsLockOsd,
    ToggleSettingState NumLockOsd,
    ToggleSettingState FnCtrlSwap,
    ToggleSettingState Touchpad)
{
    public ToggleSettingState Get(InputSettingKind kind) => kind switch
    {
        InputSettingKind.FunctionLock => FunctionLock,
        InputSettingKind.CapsLockOsd => CapsLockOsd,
        InputSettingKind.NumLockOsd => NumLockOsd,
        InputSettingKind.FnCtrlSwap => FnCtrlSwap,
        InputSettingKind.Touchpad => Touchpad,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public InputSettingsState With(InputSettingKind kind, ToggleSettingState state) => kind switch
    {
        InputSettingKind.FunctionLock => this with { FunctionLock = state },
        InputSettingKind.CapsLockOsd => this with { CapsLockOsd = state },
        InputSettingKind.NumLockOsd => this with { NumLockOsd = state },
        InputSettingKind.FnCtrlSwap => this with { FnCtrlSwap = state },
        InputSettingKind.Touchpad => this with { Touchpad = state },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

internal static class InputSettingsController
{
    private const string EnergyDriverPath = @"\\.\EnergyDrv";
    private const uint IoctlEnergyKeyboard = 0x831020E8;
    private const uint FnLockCapabilityMask = 0x200;
    private const uint FnLockEnabledMask = 0x400;

    private const string BiosAssistantScope = @"root\WMI";
    private const string BiosAssistantClass = "LENOVO_BIOS_ASSISTANT";
    private const uint WmiSuccessMask = 0x80000000;
    private const uint FnCtrlCapabilityMask = 1u << 3;
    private const uint FnCtrlIndex = 1;

    private const string HotkeyServiceName = "LenovoFnAndFunctionKeys";
    private const string HotkeyServiceRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\LenovoFnAndFunctionKeys";
    private const string HotkeyToastRegistryPath =
        HotkeyServiceRegistryPath + @"\VantageToast";
    private const string HotkeyCapabilityRegistryPath =
        HotkeyServiceRegistryPath + @"\Capbility";

    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceUserDefinedControl = 0x0100;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceRunning = 0x00000004;

    private const string PrecisionTouchpadRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";
    private const string LenovoUtilityClass = "LENOVO_UTILITY_DATA";
    private const uint PrecisionTouchpadDataType = 0x12;
    private const uint PrecisionTouchpadMinimumVersion = 0x18;
    private static readonly TimeSpan WmiFailureCacheDuration =
        TimeSpan.FromSeconds(30);
    private static readonly object WmiCacheLock = new();
    private static bool? _fnCtrlSwapSupported;
    private static ToggleSettingState? _cachedFnCtrlSwapState;
    private static DateTimeOffset _fnCtrlSwapCacheExpiresAt;
    private static bool? _precisionTouchpadSupported;
    private static ToggleSettingState? _cachedTouchpadCapabilityFailure;
    private static DateTimeOffset _touchpadFailureCacheExpiresAt;

    public static InputSettingsState ReadState(bool refreshWmiState = false) => new(
        TryRead(ReadFunctionLock),
        TryRead(() => ReadOsdState(capsLock: true)),
        TryRead(() => ReadOsdState(capsLock: false)),
        TryRead(() => ReadFnCtrlSwap(refreshWmiState)),
        TryRead(ReadTouchpad));

    public static ToggleSettingState SetState(InputSettingKind kind, bool enabled) => kind switch
    {
        InputSettingKind.FunctionLock => SetFunctionLock(enabled),
        InputSettingKind.CapsLockOsd => SetOsdState(capsLock: true, enabled),
        InputSettingKind.NumLockOsd => SetOsdState(capsLock: false, enabled),
        InputSettingKind.FnCtrlSwap => SetFnCtrlSwap(enabled),
        InputSettingKind.Touchpad => SetTouchpad(enabled),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static ToggleSettingState TryRead(Func<ToggleSettingState> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return ToggleSettingState.Failed(ex);
        }
    }

    private static ToggleSettingState ReadFunctionLock()
    {
        using var driver = OpenEnergyDriver();
        var value = CallEnergyDriver(driver, 2);
        if ((value & FnLockCapabilityMask) == 0)
            return ToggleSettingState.Unsupported();

        return new(true, (value & FnLockEnabledMask) != 0);
    }

    private static ToggleSettingState SetFunctionLock(bool enabled)
    {
        using var driver = OpenEnergyDriver();
        var current = CallEnergyDriver(driver, 2);
        if ((current & FnLockCapabilityMask) == 0)
            return ToggleSettingState.Unsupported();

        _ = CallEnergyDriver(driver, enabled ? 14u : 15u);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(50);
            var confirmed = CallEnergyDriver(driver, 2);
            if ((confirmed & FnLockCapabilityMask) != 0 &&
                ((confirmed & FnLockEnabledMask) != 0) == enabled)
            {
                return new(true, enabled);
            }
        }

        throw new InvalidOperationException("The Fn Lock state did not change.");
    }

    private static ToggleSettingState ReadFnCtrlSwap(bool forceRefresh)
    {
        lock (WmiCacheLock)
        {
            if (_cachedFnCtrlSwapState is not null &&
                !forceRefresh &&
                (_cachedFnCtrlSwapState.Error is null ||
                 DateTimeOffset.UtcNow < _fnCtrlSwapCacheExpiresAt))
            {
                return _cachedFnCtrlSwapState;
            }

            try
            {
                using var instance = GetBiosAssistantInstance();
                return CacheFnCtrlSwapState(ReadFnCtrlSwap(instance));
            }
            catch (Exception ex)
            {
                return CacheFnCtrlSwapState(
                    ToggleSettingState.Failed(ex),
                    WmiFailureCacheDuration);
            }
        }
    }

    private static ToggleSettingState ReadFnCtrlSwap(
        ManagementObject instance)
    {
        if (!IsFnCtrlSwapSupported(instance))
            return ToggleSettingState.Unsupported();

        using var input = instance.GetMethodParameters("GetValue");
        input["IndexData"] = FnCtrlIndex;
        var value = InvokeUInt32(instance, "GetValue", input, "Data");
        if ((value & WmiSuccessMask) == 0)
            throw new InvalidOperationException("LENOVO_BIOS_ASSISTANT.GetValue failed.");

        return new(true, (value & 1) != 0);
    }

    private static ToggleSettingState SetFnCtrlSwap(bool enabled)
    {
        lock (WmiCacheLock)
        {
            using var instance = GetBiosAssistantInstance();
            if (!IsFnCtrlSwapSupported(instance))
                return CacheFnCtrlSwapState(ToggleSettingState.Unsupported());

            using var input = instance.GetMethodParameters("SetValue");
            input["IndexData"] = FnCtrlIndex;
            input["ValueData"] = enabled ? 1u : 0u;
            var result = InvokeUInt32(instance, "SetValue", input, "ReturnData");
            if ((result & WmiSuccessMask) == 0)
            {
                throw new InvalidOperationException(
                    "LENOVO_BIOS_ASSISTANT.SetValue failed.");
            }

            Thread.Sleep(100);
            return CacheFnCtrlSwapState(ReadFnCtrlSwap(instance));
        }
    }

    private static bool IsFnCtrlSwapSupported(ManagementObject instance)
    {
        if (_fnCtrlSwapSupported.HasValue)
            return _fnCtrlSwapSupported.Value;

        var capability = InvokeUInt32(
            instance,
            "GetCapabilityValue",
            null,
            "Data");
        _fnCtrlSwapSupported =
            (capability & WmiSuccessMask) != 0 &&
            (capability & FnCtrlCapabilityMask) != 0;
        return _fnCtrlSwapSupported.Value;
    }

    private static ToggleSettingState CacheFnCtrlSwapState(
        ToggleSettingState state,
        TimeSpan? duration = null)
    {
        _cachedFnCtrlSwapState = state;
        _fnCtrlSwapCacheExpiresAt = duration.HasValue
            ? DateTimeOffset.UtcNow + duration.Value
            : DateTimeOffset.MaxValue;
        return state;
    }

    private static ManagementObject GetBiosAssistantInstance() =>
        LenovoWmi.GetActiveInstance(BiosAssistantClass);

    private static uint InvokeUInt32(
        ManagementObject instance,
        string method,
        ManagementBaseObject? input,
        string outputProperty)
    {
        using var output = instance.InvokeMethod(method, input, null)
            ?? throw new InvalidOperationException($"{BiosAssistantClass}.{method} returned no data.");
        return Convert.ToUInt32(output[outputProperty]);
    }

    private static ToggleSettingState ReadOsdState(bool capsLock)
    {
        if (!IsHotkeyServiceRunning())
            return ToggleSettingState.Unsupported();

        using var toastKey = Registry.LocalMachine.OpenSubKey(HotkeyToastRegistryPath, false);
        var valueName = capsLock ? "ShowCapslkOSD" : "ShowNumlkOSD";
        var value = toastKey?.GetValue(valueName);
        if (value is null)
            return ToggleSettingState.Unsupported();

        if (!capsLock)
        {
            using var capabilityKey = Registry.LocalMachine.OpenSubKey(
                HotkeyCapabilityRegistryPath,
                false);
            if (Convert.ToUInt32(capabilityKey?.GetValue("SupportNumlkKey") ?? 0) != 1)
                return ToggleSettingState.Unsupported();
        }

        return new(true, Convert.ToUInt32(value) == 1);
    }

    private static ToggleSettingState SetOsdState(bool capsLock, bool enabled)
    {
        var current = ReadOsdState(capsLock);
        if (!current.Supported)
            return current;
        if (current.Enabled == enabled)
            return current;

        var command = (capsLock, enabled) switch
        {
            (true, true) => 146u,
            (true, false) => 147u,
            (false, true) => 148u,
            (false, false) => 149u
        };
        SendHotkeyServiceControl(command);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(200);
            var confirmed = ReadOsdState(capsLock);
            if (confirmed.Supported && confirmed.Enabled == enabled)
                return confirmed;
        }

        throw new InvalidOperationException("The Lenovo Hotkeys OSD state did not change.");
    }

    private static bool IsHotkeyServiceRunning()
    {
        using var manager = Native.OpenSCManager(
            null,
            null,
            ScManagerConnect);
        if (manager.IsInvalid)
            return false;

        using var service = Native.OpenService(
            manager,
            HotkeyServiceName,
            ServiceQueryStatus);
        if (service.IsInvalid)
            return false;

        return Native.QueryServiceStatus(service, out var status) &&
               status.CurrentState == ServiceRunning;
    }

    private static void SendHotkeyServiceControl(uint command)
    {
        using var manager = Native.OpenSCManager(
            null,
            null,
            ScManagerConnect);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        using var service = Native.OpenService(
            manager,
            HotkeyServiceName,
            ServiceQueryStatus | ServiceUserDefinedControl);
        if (service.IsInvalid)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to open the {HotkeyServiceName} service.");

        if (!Native.ControlService(service, command, out _))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Lenovo Hotkeys service control {command} failed.");
    }

    private static ToggleSettingState ReadTouchpad()
    {
        lock (WmiCacheLock)
        {
            if (_cachedTouchpadCapabilityFailure is not null &&
                DateTimeOffset.UtcNow < _touchpadFailureCacheExpiresAt)
            {
                return _cachedTouchpadCapabilityFailure;
            }

            try
            {
                if (!IsPrecisionTouchpadSupported())
                    return ToggleSettingState.Unsupported();
                _cachedTouchpadCapabilityFailure = null;
            }
            catch (Exception ex)
            {
                _cachedTouchpadCapabilityFailure =
                    ToggleSettingState.Failed(ex);
                _touchpadFailureCacheExpiresAt =
                    DateTimeOffset.UtcNow + WmiFailureCacheDuration;
                return _cachedTouchpadCapabilityFailure;
            }
        }

        return ReadTouchpadRegistry();
    }

    private static ToggleSettingState ReadTouchpadRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            PrecisionTouchpadRegistryPath,
            false);
        var value = key?.GetValue("Enabled");
        return value is null
            ? ToggleSettingState.Unsupported()
            : new(true, Convert.ToInt32(value) != 0);
    }

    private static ToggleSettingState SetTouchpad(bool enabled)
    {
        var current = ReadTouchpad();
        if (!current.Supported)
            return current;
        if (current.Enabled == enabled)
            return current;

        SendCtrlWinF24();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(100);
            var confirmed = ReadTouchpadRegistry();
            if (confirmed.Supported && confirmed.Enabled == enabled)
                return confirmed;
        }

        throw new InvalidOperationException("The Precision Touchpad state did not change.");
    }

    private static bool IsPrecisionTouchpadSupported()
    {
        lock (WmiCacheLock)
        {
            if (_precisionTouchpadSupported.HasValue)
                return _precisionTouchpadSupported.Value;

            using var searcher = new ManagementObjectSearcher(
                BiosAssistantScope,
                $"SELECT * FROM {LenovoUtilityClass}");
            using var instances = searcher.Get();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                using (var input = instance.GetMethodParameters(
                           "GetIfSupportOrVersion"))
                {
                    input["datatype"] = PrecisionTouchpadDataType;
                    var version = InvokeUInt32(
                        instance,
                        "GetIfSupportOrVersion",
                        input,
                        "Data");
                    _precisionTouchpadSupported =
                        version >= PrecisionTouchpadMinimumVersion;
                    return _precisionTouchpadSupported.Value;
                }
            }

            _precisionTouchpadSupported = false;
            return false;
        }
    }

    private static void SendCtrlWinF24()
    {
        const ushort vkControl = 0x11;
        const ushort vkLeftWindows = 0x5B;
        const ushort vkF24 = 0x87;

        var inputs = new[]
        {
            KeyboardInput(vkControl, keyUp: false),
            KeyboardInput(vkLeftWindows, keyUp: false),
            KeyboardInput(vkF24, keyUp: false),
            KeyboardInput(vkF24, keyUp: true),
            KeyboardInput(vkLeftWindows, keyUp: true),
            KeyboardInput(vkControl, keyUp: true)
        };

        var sent = Native.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<Native.Input>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput Ctrl+Win+F24 failed.");
    }

    private static Native.Input KeyboardInput(ushort key, bool keyUp) => new()
    {
        Type = Native.InputKeyboard,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput
            {
                VirtualKey = key,
                Flags = keyUp ? Native.KeyEventKeyUp : 0
            }
        }
    };

    private static SafeFileHandle OpenEnergyDriver()
    {
        var handle = Native.CreateFile(
            EnergyDriverPath,
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero,
            Native.OpenExisting,
            Native.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to open {EnergyDriverPath}. Is Lenovo EnergyDrv installed and are you elevated?");
        }

        return handle;
    }

    private static uint CallEnergyDriver(SafeFileHandle handle, uint command)
    {
        var input = command;
        if (!Native.DeviceIoControl(
                handle,
                IoctlEnergyKeyboard,
                ref input,
                sizeof(uint),
                out var output,
                sizeof(uint),
                out _,
                IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"DeviceIoControl 0x{IoctlEnergyKeyboard:X8} failed.");
        }

        return output;
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;

        public const uint InputKeyboard = 1;
        public const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;

            [FieldOffset(0)]
            public KeyboardInput Keyboard;

            [FieldOffset(0)]
            public HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HardwareInput
        {
            public uint Message;
            public ushort ParameterLow;
            public ushort ParameterHigh;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref uint lpInBuffer,
            int nInBufferSize,
            out uint lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeServiceHandle OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeServiceHandle OpenService(
            SafeServiceHandle serviceManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatus(
            SafeServiceHandle service,
            out ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(
            SafeServiceHandle service,
            uint control,
            out ServiceStatus serviceStatus);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);
    }

    private sealed class SafeServiceHandle : SafeHandle
    {
        private SafeServiceHandle()
            : base(IntPtr.Zero, true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero ||
                                          handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr serviceHandle);
    }
}
