using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

internal enum KeyboardBacklightLevel
{
    Auto,
    Low,
    High,
    Off
}

internal sealed record KeyboardBacklightState(
    KeyboardBacklightLevel? Level,
    byte BrightnessStatus,
    bool AutoOffSupported,
    bool? AutoOffEnabled,
    byte AutoOffValue,
    byte AutoOffStatus);

internal static class KeyboardBacklightController
{
    private const uint IoctlEnergyKblc = 0x83102144;
    private const uint IoctlEnergyKbla = 0x83102158;
    private const string DriverPath = @"\\.\EnergyDrv";

    public static KeyboardBacklightState ReadState()
    {
        using var driver = new EnergyDriver();
        return ReadState(driver);
    }

    public static KeyboardBacklightState SetBrightness(KeyboardBacklightLevel level)
    {
        using var driver = new EnergyDriver();
        var config = driver.Kblc(0x01) & 0xFFFFFFFEu;
        var arg = BuildKblcArg(config, 0x03, BrightnessPayload(level));
        _ = driver.Kblc(arg);
        Thread.Sleep(300);
        return ReadState(driver);
    }

    public static KeyboardBacklightState SetAutoOff(bool enabled)
    {
        using var driver = new EnergyDriver();
        if (!IsAutoOffSupported(driver))
            throw new NotSupportedException("KBLA AutoDim is not supported.");

        _ = driver.Kbla(enabled ? 0x13u : 0x03u);
        Thread.Sleep(300);
        return ReadState(driver);
    }

    private static KeyboardBacklightState ReadState(EnergyDriver driver)
    {
        var brightnessStatus = unchecked((byte)(driver.Kblc(BuildKblcStatusArg(driver)) & 0xFF));
        var autoOffSupported = IsAutoOffSupported(driver);
        var autoOffStatus = autoOffSupported
            ? unchecked((byte)(driver.Kbla(0x02) & 0xFF))
            : (byte)0;
        var autoOffEnabled = DecodeAutoOff(autoOffStatus);
        return new KeyboardBacklightState(
            DecodeBrightness(brightnessStatus),
            brightnessStatus,
            autoOffSupported,
            autoOffEnabled,
            AutoOffValue(autoOffEnabled, autoOffStatus),
            autoOffStatus);
    }

    private static bool IsAutoOffSupported(EnergyDriver driver)
    {
        try
        {
            return driver.Kbla(0x01) != 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static uint BuildKblcStatusArg(EnergyDriver driver)
    {
        var config = driver.Kblc(0x01) & 0xFFFFFFFEu;
        return BuildKblcArg(config, 0x02, 0);
    }

    private static uint BuildKblcArg(uint kbgcWithoutValidBit, uint command, int level)
    {
        if ((command & ~0x0Fu) != 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Command must fit in the low nibble.");

        var token = checked(kbgcWithoutValidBit << 3);
        if ((token & ~0xFFF0u) != 0)
            throw new InvalidOperationException($"KBGC token 0x{kbgcWithoutValidBit:X8} does not fit KBLC Arg0 bits 4..15.");

        return token | command | ((uint)level << 16);
    }

    private static int BrightnessPayload(KeyboardBacklightLevel level) => level switch
    {
        KeyboardBacklightLevel.Auto => 3,
        KeyboardBacklightLevel.Low => 1,
        KeyboardBacklightLevel.High => 2,
        KeyboardBacklightLevel.Off => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
    };

    private static KeyboardBacklightLevel? DecodeBrightness(byte raw) => (raw & 0x07) switch
    {
        0x07 => KeyboardBacklightLevel.Auto,
        0x03 => KeyboardBacklightLevel.Low,
        0x05 => KeyboardBacklightLevel.High,
        0x01 => KeyboardBacklightLevel.Off,
        _ => null
    };

    private static bool? DecodeAutoOff(byte raw)
    {
        var valid = (raw & 0x01) != 0;
        if (!valid)
            return null;

        return (raw & 0x02) != 0;
    }

    private static byte AutoOffValue(bool? enabled, byte status) => enabled switch
    {
        true => 0,
        false => 1,
        null => status
    };

    private sealed class EnergyDriver : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public EnergyDriver()
        {
            _handle = Native.CreateFile(
                DriverPath,
                Native.GenericRead | Native.GenericWrite,
                Native.FileShareRead | Native.FileShareWrite,
                IntPtr.Zero,
                Native.OpenExisting,
                Native.FileAttributeNormal,
                IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open {DriverPath}. Is Lenovo EnergyDrv installed and are you elevated?");
        }

        public uint Kblc(uint arg) => Call(IoctlEnergyKblc, arg);

        public uint Kbla(uint arg) => Call(IoctlEnergyKbla, arg);

        public void Dispose() => _handle.Dispose();

        private uint Call(uint ioctl, uint arg)
        {
            var input = arg;
            if (!Native.DeviceIoControl(
                    _handle,
                    ioctl,
                    ref input,
                    sizeof(uint),
                    out var output,
                    sizeof(uint),
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl 0x{ioctl:X8} failed.");
            }

            return output;
        }
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;

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
    }
}
