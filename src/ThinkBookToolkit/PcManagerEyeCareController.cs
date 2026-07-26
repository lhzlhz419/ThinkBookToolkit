using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed record PcManagerEyeCareDefaults(
    int NormalTemperature,
    int EyeCareTemperature)
{
    public PcManagerEyeCareDefaults Normalize() => new(
        PcManagerEyeCareController.NormalizeTemperature(
            NormalTemperature,
            PcManagerEyeCareController.FactoryNormalTemperature),
        PcManagerEyeCareController.NormalizeTemperature(
            EyeCareTemperature,
            PcManagerEyeCareController.FactoryEyeCareTemperature));
}

internal sealed record PcManagerEyeCareState(
    bool Available,
    bool Enabled,
    int CurrentTemperature,
    int NormalDefaultTemperature,
    int EyeCareDefaultTemperature,
    bool DllCapability,
    string? Error = null);

internal static class PcManagerEyeCareController
{
    public const int MinimumTemperature = 2000;
    public const int MaximumTemperature = 11200;
    public const int FactoryNormalTemperature = 6600;
    public const int FactoryEyeCareTemperature = 3500;

    private const string ConfigRegistryPath =
        @"SOFTWARE\Lenovo\PcManager\Configs";
    private const string EyeCareFlagValue = "EyeCareFlag";
    private const string IniSection = "ColorTemperature";
    private const string IniTemperatureKey = "ColorTemperatureValue";
    private const string IniCurrentColorKey = "CurrentColor";
    private const uint DisplayDeviceActive = 0x00000001;
    private const int GammaEntryCount = 256;
    private const int GammaChannelCount = 3;
    private const int GammaBufferBytes =
        GammaEntryCount * GammaChannelCount * sizeof(short);
    private static readonly object CapabilityLock = new();
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);
    private static bool _capabilityChecked;
    private static bool _gammaCapability;
    private static bool _dllCapability;
    private static string? _capabilityError;

    public static PcManagerEyeCareState ReadState(
        PcManagerEyeCareDefaults defaults)
    {
        defaults = defaults.Normalize();
        EnsureCapability();

        try
        {
            var enabled = ReadEyeCareFlag();
            var fallback = enabled
                ? defaults.EyeCareTemperature
                : defaults.NormalTemperature;
            var temperature = ReadTemperature(fallback);
            return new(
                _gammaCapability && _dllCapability,
                enabled,
                temperature,
                defaults.NormalTemperature,
                defaults.EyeCareTemperature,
                _dllCapability,
                _capabilityError);
        }
        catch (Exception ex)
        {
            return new(
                false,
                false,
                defaults.NormalTemperature,
                defaults.NormalTemperature,
                defaults.EyeCareTemperature,
                _dllCapability,
                ex.Message);
        }
    }

    public static PcManagerEyeCareState SetEnabled(
        bool enabled,
        PcManagerEyeCareDefaults defaults)
    {
        defaults = defaults.Normalize();
        var current = ReadState(defaults);
        EnsureAvailable(current);
        var target = enabled
            ? defaults.EyeCareTemperature
            : defaults.NormalTemperature;
        ApplyAndPersist(current.CurrentTemperature, target, enabled);
        return ReadState(defaults);
    }

    public static PcManagerEyeCareState SetTemperature(
        int temperature,
        PcManagerEyeCareDefaults defaults)
    {
        defaults = defaults.Normalize();
        var current = ReadState(defaults);
        EnsureAvailable(current);
        if (current.Enabled)
        {
            throw new InvalidOperationException(
                "Color temperature cannot be changed while eye care mode is enabled.");
        }

        temperature = NormalizeTemperature(
            temperature,
            defaults.NormalTemperature);
        ApplyAndPersist(
            current.CurrentTemperature,
            temperature,
            enabled: false);
        return ReadState(defaults);
    }

    public static PcManagerEyeCareState RestoreConfiguredDefault(
        PcManagerEyeCareDefaults defaults)
    {
        defaults = defaults.Normalize();
        var current = ReadState(defaults);
        EnsureAvailable(current);
        var target = current.Enabled
            ? defaults.EyeCareTemperature
            : defaults.NormalTemperature;
        ApplyAndPersist(current.CurrentTemperature, target, current.Enabled);
        return ReadState(defaults);
    }

    public static int NormalizeTemperature(int value, int fallback) =>
        value is >= MinimumTemperature and <= MaximumTemperature
            ? value
            : fallback;

    private static void ApplyAndPersist(
        int currentTemperature,
        int targetTemperature,
        bool enabled)
    {
        currentTemperature = NormalizeTemperature(
            currentTemperature,
            FactoryNormalTemperature);
        targetTemperature = NormalizeTemperature(
            targetTemperature,
            FactoryNormalTemperature);

        ApplyLock.Wait();
        try
        {
            WriteTemperature(targetTemperature);
            WriteEyeCareFlag(enabled);
            ApplyTemperatureTransition(currentTemperature, targetTemperature);
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    private static void ApplyTemperatureTransition(
        int currentTemperature,
        int targetTemperature)
    {
        var displayContexts = OpenActiveDisplayContexts();
        if (displayContexts.Count == 0)
            throw new InvalidOperationException("No active display was found.");

        var gammaBuffer = Marshal.AllocHGlobal(GammaBufferBytes);
        try
        {
            var temperature = currentTemperature;
            while (true)
            {
                if (temperature < targetTemperature)
                {
                    temperature = Math.Min(
                        targetTemperature,
                        temperature + 50);
                }
                else if (temperature > targetTemperature)
                {
                    temperature = Math.Max(
                        targetTemperature,
                        temperature - 50);
                }

                var ramp = BuildGammaRamp(temperature);
                Marshal.Copy(ramp, 0, gammaBuffer, ramp.Length);
                foreach (var displayContext in displayContexts)
                {
                    if (!SetDeviceGammaRamp(displayContext, gammaBuffer))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "SetDeviceGammaRamp failed.");
                    }
                }

                if (temperature == targetTemperature)
                    break;

                Thread.Sleep(1);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(gammaBuffer);
            CloseDisplayContexts(displayContexts);
        }
    }

    private static short[] BuildGammaRamp(int temperature)
    {
        var (red, green, blue) = ColorFromTemperature(temperature);
        var coefficients = new[]
        {
            128.0 + 0.5 * red,
            128.0 + 0.5 * green,
            128.0 + 0.5 * blue
        };
        var ramp = new short[GammaEntryCount * GammaChannelCount];

        for (var channel = 0; channel < GammaChannelCount; channel++)
        {
            var offset = channel * GammaEntryCount;
            for (var index = 0; index < GammaEntryCount; index++)
            {
                var value = Math.Min(
                    ushort.MaxValue,
                    Math.Floor(index * coefficients[channel]));
                ramp[offset + index] = unchecked((short)(ushort)value);
            }
        }

        return ramp;
    }

    private static (double Red, double Green, double Blue)
        ColorFromTemperature(int temperature)
    {
        var scaledTemperature = Math.Clamp(temperature, 1000, 40000) / 100;
        double red;
        double green;
        double blue;

        if (scaledTemperature <= 66)
        {
            red = 255;
            green = 99.4708025861 * Math.Log(scaledTemperature) -
                    161.1195681661;
            blue = scaledTemperature >= 66
                ? 255
                : scaledTemperature <= 19
                ? 0
                : 138.5177312231 *
                  Math.Log(scaledTemperature - 10) -
                  305.0447927307;
        }
        else
        {
            red = 329.698727446 *
                  Math.Pow(scaledTemperature - 60, -0.1332047592);
            green = 288.1221695283 *
                    Math.Pow(scaledTemperature - 60, -0.0755148492);
            blue = 255;
        }

        return (
            Math.Clamp(red, 0, 255),
            Math.Clamp(green, 0, 255),
            Math.Clamp(blue, 0, 255));
    }

    private static void WriteTemperature(int temperature)
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var ramp = BuildGammaRamp(temperature);
        var currentColor = string.Concat(
            unchecked((ushort)ramp[1]),
            unchecked((ushort)ramp[GammaEntryCount + 1]),
            unchecked((ushort)ramp[GammaEntryCount * 2 + 1]));

        if (!WritePrivateProfileString(
                IniSection,
                IniTemperatureKey,
                temperature.ToString(),
                path) ||
            !WritePrivateProfileString(
                IniSection,
                IniCurrentColorKey,
                currentColor,
                path))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to save Lenovo PC Manager color temperature.");
        }
    }

    private static int ReadTemperature(int fallback)
    {
        var path = ConfigPath;
        if (!File.Exists(path))
            return fallback;

        var builder = new StringBuilder(32);
        GetPrivateProfileString(
            IniSection,
            IniTemperatureKey,
            fallback.ToString(),
            builder,
            builder.Capacity,
            path);
        return int.TryParse(builder.ToString(), out var value)
            ? NormalizeTemperature(value, fallback)
            : fallback;
    }

    private static bool ReadEyeCareFlag()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ConfigRegistryPath);
        return key?.GetValue(EyeCareFlagValue) switch
        {
            int value => value != 0,
            long value => value != 0,
            string value when int.TryParse(value, out var parsed) =>
                parsed != 0,
            _ => false
        };
    }

    private static void WriteEyeCareFlag(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            ConfigRegistryPath,
            writable: true);
        key?.SetValue(
            EyeCareFlagValue,
            enabled ? 1 : 0,
            RegistryValueKind.DWord);
    }

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Lenovo",
        "devicecenter",
        "config",
        "config.ini");

    private static void EnsureAvailable(PcManagerEyeCareState state)
    {
        if (!state.Available)
        {
            throw new InvalidOperationException(
                state.Error ?? "Lenovo PC Manager eye care is not supported.");
        }
    }

    private static void EnsureCapability()
    {
        if (_capabilityChecked)
            return;

        lock (CapabilityLock)
        {
            if (_capabilityChecked)
                return;

            try
            {
                _dllCapability = ReadDllCapability();
                if (!_dllCapability)
                {
                    _capabilityError =
                        "Lenovo PC Manager reports that color temperature is not supported.";
                }
            }
            catch (Exception ex)
            {
                _capabilityError =
                    $"WrapPlugin capability check failed: {ex.Message}";
            }

            try
            {
                _gammaCapability = ProbeGammaCapability();
                if (!_gammaCapability && _capabilityError is null)
                {
                    _capabilityError =
                        "The active display does not accept a GDI gamma ramp.";
                }
            }
            catch (Exception ex)
            {
                _gammaCapability = false;
                _capabilityError = ex.Message;
            }

            _capabilityChecked = true;
        }
    }

    private static bool ReadDllCapability()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(PcManagerEyeCareController).Assembly.Location);
        var applicationPath = Path.Combine(
            string.IsNullOrWhiteSpace(assemblyDirectory)
                ? AppContext.BaseDirectory
                : assemblyDirectory,
            "LenovoPcManager",
            "WrapPlugin.dll");
        var path = LenovoDependencyDirectory.FindExistingFile(
            LenovoDependencyDirectory.GetEnabledRoot(),
            Path.Combine("LenovoPcManager", "WrapPlugin.dll"),
            applicationPath) ?? applicationPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Lenovo PC Manager WrapPlugin.dll was not found.",
                path);
        }

        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powerShell))
        {
            throw new FileNotFoundException(
                "The 32-bit Windows PowerShell host was not found.",
                powerShell);
        }

        var escapedPath = path.Replace("\"", "\"\"");
        var source =
            "using System.Runtime.InteropServices; " +
            "public static class PcManagerEyeCareNative { " +
            $"[DllImport(@\"{escapedPath}\", " +
            "CallingConvention=CallingConvention.Winapi)] " +
            "public static extern int IsSupportColorTemperature(); }";
        var script =
            "$ProgressPreference='SilentlyContinue';" +
            "$ErrorActionPreference='Stop';" +
            $"Add-Type -TypeDefinition '{source.Replace("'", "''")}';" +
            "[PcManagerEyeCareNative]::IsSupportColorTemperature()";
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException(
                                "Failed to start the 32-bit capability helper.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "Lenovo PC Manager capability check timed out.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "Lenovo PC Manager capability check failed."
                    : error.Trim());
        }

        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), out var value))
                return value != 0;
        }

        throw new InvalidOperationException(
            "Lenovo PC Manager capability check returned no result.");
    }

    private static bool ProbeGammaCapability()
    {
        var displayContexts = OpenActiveDisplayContexts();
        if (displayContexts.Count == 0)
            return false;

        var buffer = Marshal.AllocHGlobal(GammaBufferBytes);
        try
        {
            foreach (var displayContext in displayContexts)
            {
                if (!GetDeviceGammaRamp(displayContext, buffer) ||
                    !SetDeviceGammaRamp(displayContext, buffer))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseDisplayContexts(displayContexts);
        }
    }

    private static List<IntPtr> OpenActiveDisplayContexts()
    {
        var contexts = new List<IntPtr>();
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice
            {
                Size = Marshal.SizeOf<DisplayDevice>()
            };
            if (!EnumDisplayDevices(null, index, ref device, 0))
                break;

            if ((device.StateFlags & DisplayDeviceActive) == 0)
                continue;

            var context = CreateDC(
                null,
                device.DeviceName,
                null,
                IntPtr.Zero);
            if (context != IntPtr.Zero)
                contexts.Add(context);
        }

        return contexts;
    }

    private static void CloseDisplayContexts(IEnumerable<IntPtr> contexts)
    {
        foreach (var context in contexts)
            DeleteDC(context);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(
        string? driver,
        string device,
        string? output,
        IntPtr initializationData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(
        IntPtr deviceContext,
        IntPtr ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(
        IntPtr deviceContext,
        IntPtr ramp);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(
        string section,
        string key,
        string value,
        string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(
        string section,
        string key,
        string defaultValue,
        StringBuilder returnedString,
        int size,
        string filePath);
}
