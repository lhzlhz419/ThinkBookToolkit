using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

internal sealed class LenovoEnergyDriver : IDisposable
{
    private const string DriverPath = @"\\.\EnergyDrv";
    private readonly SafeFileHandle _handle;

    public LenovoEnergyDriver()
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
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to open {DriverPath}. Is Lenovo EnergyDrv installed and are you elevated?");
        }
    }

    public uint Call(uint ioctl, uint input) => Call<uint>(ioctl, input);

    public T Call<T>(uint ioctl, uint input) where T : struct
    {
        var inputPointer = Marshal.AllocHGlobal(sizeof(uint));
        var outputSize = Marshal.SizeOf<T>();
        var outputPointer = Marshal.AllocHGlobal(outputSize);
        try
        {
            Marshal.WriteInt32(inputPointer, unchecked((int)input));
            for (var offset = 0; offset < outputSize; offset++)
                Marshal.WriteByte(outputPointer, offset, 0);

            if (!Native.DeviceIoControl(
                    _handle,
                    ioctl,
                    inputPointer,
                    sizeof(uint),
                    outputPointer,
                    outputSize,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"DeviceIoControl 0x{ioctl:X8} failed.");
            }

            return Marshal.PtrToStructure<T>(outputPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPointer);
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    public void Dispose() => _handle.Dispose();

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
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }
}
