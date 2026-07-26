using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThinkBookToolkit;

internal static class FlipToStartController
{
    private const uint CapabilityId = 0x00030000;
    private const string VariableName = "FBSWIF";
    private const string VariableGuid = "{D743491E-F484-4952-A87D-8D5DD189B70C}";
    private const uint VariableAttributes = 0x00000001 | 0x00000002 | 0x00000004;
    private const string SystemEnvironmentPrivilege = "SeSystemEnvironmentPrivilege";
    private static readonly object StateLock = new();
    private static Backend? _backend;
    private static bool? _cachedState;
    private static Exception? _cachedFailure;
    private static DateTimeOffset _failureCacheExpiresAt;

    public static bool ReadState(bool forceRefresh = false)
    {
        lock (StateLock)
        {
            if (_cachedState.HasValue && !forceRefresh)
                return _cachedState.Value;
            if (!forceRefresh &&
                _cachedFailure is not null &&
                DateTimeOffset.UtcNow < _failureCacheExpiresAt)
            {
                throw new InvalidOperationException(
                    _cachedFailure.Message,
                    _cachedFailure);
            }

            try
            {
                return CacheState(ReadCurrentState());
            }
            catch (Exception ex)
            {
                _cachedFailure = ex;
                _failureCacheExpiresAt =
                    DateTimeOffset.UtcNow.AddSeconds(30);
                throw;
            }
        }
    }

    public static bool SetState(bool enabled)
    {
        lock (StateLock)
        {
            if (!_backend.HasValue)
                _ = ReadCurrentState();

            if (_backend == Backend.Wmi)
            {
                LenovoWmi.SetFeatureValue(CapabilityId, enabled ? 1 : 0);
            }
            else
            {
                WriteUefiState(enabled);
            }

            var confirmed = _backend == Backend.Wmi
                ? LenovoWmi.GetFeatureValue(CapabilityId) != 0
                : ReadUefiState();
            return CacheState(confirmed);
        }
    }

    private static bool ReadCurrentState()
    {
        if (_backend != Backend.Uefi)
        {
            try
            {
                var state = LenovoWmi.GetFeatureValue(CapabilityId) != 0;
                _backend = Backend.Wmi;
                return state;
            }
            catch
            {
                var state = ReadUefiState();
                _backend = Backend.Uefi;
                return state;
            }
        }

        return ReadUefiState();
    }

    private static bool CacheState(bool state)
    {
        _cachedState = state;
        _cachedFailure = null;
        return state;
    }

    private static bool ReadUefiState()
    {
        using var privilege = EnableSystemEnvironmentPrivilege();
        var buffer = new byte[4];
        var size = Native.GetFirmwareEnvironmentVariableEx(
            VariableName,
            VariableGuid,
            buffer,
            (uint)buffer.Length,
            out _);
        if (size == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to read UEFI variable {VariableName}.");
        }

        return buffer[0] != 0;
    }

    private static void WriteUefiState(bool enabled)
    {
        using var privilege = EnableSystemEnvironmentPrivilege();
        var buffer = new byte[4];
        buffer[0] = enabled ? (byte)1 : (byte)0;
        if (!Native.SetFirmwareEnvironmentVariableEx(
                VariableName,
                VariableGuid,
                buffer,
                (uint)buffer.Length,
                VariableAttributes))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to write UEFI variable {VariableName}.");
        }
    }

    private static PrivilegeScope EnableSystemEnvironmentPrivilege()
    {
        if (!Native.OpenProcessToken(
                Process.GetCurrentProcess().Handle,
                Native.TokenAdjustPrivileges | Native.TokenQuery,
                out var token))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to open the process token.");
        }

        if (!Native.LookupPrivilegeValue(
                null,
                SystemEnvironmentPrivilege,
                out var luid))
        {
            Native.CloseHandle(token);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to look up {SystemEnvironmentPrivilege}.");
        }

        var privileges = new Native.TokenPrivileges
        {
            PrivilegeCount = 1,
            Privileges = new Native.LuidAndAttributes
            {
                Luid = luid,
                Attributes = Native.SePrivilegeEnabled
            }
        };
        if (!Native.AdjustTokenPrivileges(
                token,
                false,
                ref privileges,
                0,
                IntPtr.Zero,
                IntPtr.Zero) ||
            Marshal.GetLastWin32Error() != 0)
        {
            var error = Marshal.GetLastWin32Error();
            Native.CloseHandle(token);
            throw new Win32Exception(
                error,
                $"Failed to enable {SystemEnvironmentPrivilege}.");
        }

        return new PrivilegeScope(token, luid);
    }

    private sealed class PrivilegeScope(IntPtr token, Native.Luid luid) : IDisposable
    {
        public void Dispose()
        {
            var privileges = new Native.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new Native.LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = 0
                }
            };
            _ = Native.AdjustTokenPrivileges(
                token,
                false,
                ref privileges,
                0,
                IntPtr.Zero,
                IntPtr.Zero);
            Native.CloseHandle(token);
        }
    }

    private enum Backend
    {
        Wmi,
        Uefi
    }

    private static class Native
    {
        public const uint TokenAdjustPrivileges = 0x0020;
        public const uint TokenQuery = 0x0008;
        public const uint SePrivilegeEnabled = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LuidAndAttributes
        {
            public Luid Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public LuidAndAttributes Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(
            string? systemName,
            string name,
            out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFirmwareEnvironmentVariableEx(
            string name,
            string guid,
            [Out] byte[] buffer,
            uint size,
            out uint attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFirmwareEnvironmentVariableEx(
            string name,
            string guid,
            byte[] buffer,
            uint size,
            uint attributes);
    }
}
