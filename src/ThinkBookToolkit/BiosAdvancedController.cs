using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ThinkBookToolkit;

internal enum BiosBootFunction
{
    SetupUtility = 1,
    InterruptMenu = 2,
    SecureWipe = 3
}

internal sealed record BiosAdvancedSupport(
    bool LogoDiy,
    bool SetupUtility,
    bool InterruptMenu,
    bool SecureWipe);

internal sealed record BiosLogoInfo(
    bool Enabled,
    byte SupportedFormats,
    uint Width,
    uint Height);

internal sealed record BiosLogoState(
    BiosLogoInfo Info,
    byte[]? CurrentImage,
    bool ShowWindowsLoading);

internal sealed record FirmwareInformation(
    string BiosVersion,
    string MeVersion,
    string AmdPspVersion,
    string SmbiosVersion,
    string AcpiVersion,
    string UefiVersion)
{
    public static FirmwareInformation Empty { get; } =
        new("", "", "", "", "", "");
}

internal static class BiosAdvancedController
{
    private const string AddinName = "LenovoProductivitySystemAddin";
    private const string DllName = "BiosUtility.dll";
    private const long MaximumLogoSize = 40L * 1024 * 1024;
    private const long ReservedEfiSpace = 64L * 1024 * 1024;

    private static readonly object Sync = new();
    private static NativeApi? _api;

    public static BiosAdvancedSupport ReadSupport()
    {
        var json = Api.GetSupportFunction();
        if (string.IsNullOrWhiteSpace(json))
            throw new NotSupportedException("Lenovo firmware support information is unavailable.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(
            IsSupportedLogoVersion(Api.GetLogoDiyVersion()),
            ReadBoolean(root, "setupUtility"),
            ReadBoolean(root, "interruptMenu"),
            ReadBoolean(root, "secureWipe"));
    }

    public static FirmwareInformation ReadFirmwareInformation()
    {
        try
        {
            var json = Api.GetBiosHardwareInfo();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(
                ReadFirmwareValue(root, "biosVersion"),
                ReadFirmwareValue(root, "meVersion"),
                ReadFirmwareValue(root, "amdPspVersion"),
                ReadFirmwareValue(root, "smBiosVersion"),
                ReadFirmwareValue(root, "acpiVersion"),
                ReadFirmwareValue(root, "uefiVersion"));
        }
        catch
        {
            return FirmwareInformation.Empty;
        }
    }

    public static BiosLogoInfo ReadLogoInfo()
    {
        var enabled = false;
        byte formats = 0;
        uint width = 0;
        uint height = 0;
        if (!Api.GetLogoDiyInfo(ref enabled, ref formats, ref width, ref height))
            throw new InvalidOperationException("Lenovo firmware did not return boot logo capabilities.");
        if (width == 0 || height == 0)
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            width = (uint)Math.Max(1, bounds?.Width ?? 1920);
            height = (uint)Math.Max(1, bounds?.Height ?? 1080);
        }
        return new(enabled, formats, width, height);
    }

    public static BiosLogoState ReadLogoState()
    {
        var info = ReadLogoInfo();
        return new(info, ReadCurrentLogo(), ReadWindowsLoading());
    }

    public static string[] GetSupportedLogoFormats(BiosLogoInfo info)
    {
        var formats = new System.Collections.Generic.List<string>();
        if ((info.SupportedFormats & 0x10) != 0) formats.Add("BMP");
        if ((info.SupportedFormats & 0x08) != 0) formats.Add("GIF");
        if ((info.SupportedFormats & 0x20) != 0) formats.Add("PNG");
        if ((info.SupportedFormats & 0x01) != 0) formats.Add("JPEG");
        return formats.ToArray();
    }

    public static void SetBootLogo(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected boot logo does not exist.", sourcePath);

        var file = new FileInfo(sourcePath);
        if (file.Length <= 0 || file.Length > MaximumLogoSize)
            throw new InvalidOperationException("The boot logo must be no larger than 40 MB.");

        var logoInfo = ReadLogoInfo();
        var image = ValidateImage(sourcePath, logoInfo);
        var drive = MountEfiPartition();
        try
        {
            var root = $"{drive}:\\";
            var available = new DriveInfo(root).AvailableFreeSpace - ReservedEfiSpace;
            if (available < file.Length)
                throw new IOException("The EFI system partition does not have enough free space.");

            var logoDirectory = Path.Combine(root, "EFI", "Lenovo", "Logo");
            if (Directory.Exists(logoDirectory))
                Directory.Delete(logoDirectory, recursive: true);
            Directory.CreateDirectory(logoDirectory);

            var target = Path.Combine(
                logoDirectory,
                $"mylogo_{logoInfo.Width}x{logoInfo.Height}{image.Extension}");
            File.Copy(sourcePath, target, overwrite: true);

            var hash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            if (!Api.SetLogoDiySha256(hash) || !Api.SetLogoDiyInfo(true))
            {
                try { Directory.Delete(logoDirectory, recursive: true); }
                catch { }
                throw new InvalidOperationException("Lenovo firmware rejected the custom boot logo.");
            }
        }
        finally
        {
            UnmountEfiPartition(drive);
        }
    }

    public static void ResetBootLogo()
    {
        var drive = MountEfiPartition();
        try
        {
            var logoDirectory = Path.Combine($"{drive}:\\", "EFI", "Lenovo", "Logo");
            if (Directory.Exists(logoDirectory))
                Directory.Delete(logoDirectory, recursive: true);
            if (!Api.SetLogoDiyInfo(false))
                throw new InvalidOperationException("Lenovo firmware rejected the default boot logo.");
        }
        finally
        {
            UnmountEfiPartition(drive);
        }
    }

    public static bool ReadWindowsLoading()
    {
        var output = RunProcess(
            Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            "/enum all",
            captureOutput: true);
        var result = true;
        var inGlobal = false;
        var inCurrent = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("{globalsettings}", StringComparison.OrdinalIgnoreCase)) inGlobal = true;
            if (line.Contains("{current}", StringComparison.OrdinalIgnoreCase)) inCurrent = true;
            if (line.Contains("---", StringComparison.Ordinal))
            {
                inGlobal = false;
                inCurrent = false;
            }
            if ((inCurrent || inGlobal) && line.Contains("bootuxdisabled", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Replace("bootuxdisabled", "", StringComparison.OrdinalIgnoreCase).Trim();
                result = value.Contains("No", StringComparison.OrdinalIgnoreCase) ||
                         value.Contains("\u5426", StringComparison.OrdinalIgnoreCase);
                if (inCurrent) break;
            }
        }
        return result;
    }

    public static void SetWindowsLoading(bool show)
    {
        var value = show ? "off" : "on";
        RunProcess(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            $"-set {{globalsettings}} bootuxdisabled {value}");
        RunProcess(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            $"-set bootuxdisabled {value}");
    }

    public static void SetBootFunction(BiosBootFunction function)
    {
        if (!Enum.IsDefined(function))
            throw new ArgumentOutOfRangeException(nameof(function));
        if (!Api.SetBiosFunction(function))
            throw new InvalidOperationException("Lenovo firmware rejected the requested boot function.");
    }

    public static void RestartComputer()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = "/r /t 0 /f /d p:0:0",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Windows could not start the restart command.");
    }

    public static string BuildLogoFilter(BiosLogoInfo info)
    {
        var patterns = new System.Collections.Generic.List<string>();
        if ((info.SupportedFormats & 0x01) != 0) patterns.Add("*.jpg;*.jpeg");
        if ((info.SupportedFormats & 0x08) != 0) patterns.Add("*.gif");
        if ((info.SupportedFormats & 0x10) != 0) patterns.Add("*.bmp");
        if ((info.SupportedFormats & 0x20) != 0) patterns.Add("*.png");
        return patterns.Count == 0
            ? "Supported images|*.jpg;*.jpeg;*.gif;*.bmp;*.png"
            : $"Supported images|{string.Join(';', patterns)}";
    }

    private static NativeApi Api
    {
        get
        {
            lock (Sync)
            {
                return _api ??= NativeApi.Load();
            }
        }
    }

    private static (string Extension, int Width, int Height) ValidateImage(
        string path,
        BiosLogoInfo support)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < header.Length)
            throw new InvalidOperationException("The selected file is not a valid image.");
        stream.Position = 0;

        var extension = DetectImageExtension(header);
        var requiredFlag = extension switch
        {
            ".jpg" => 0x01,
            ".gif" => 0x08,
            ".bmp" => 0x10,
            ".png" => 0x20,
            _ => 0
        };
        if (requiredFlag == 0 || (support.SupportedFormats & requiredFlag) == 0)
            throw new NotSupportedException("The selected image format is not supported by this firmware.");

        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            throw new InvalidOperationException("The selected file is not a valid image.");
        if ((support.Width > 0 && frame.PixelWidth > support.Width) ||
            (support.Height > 0 && frame.PixelHeight > support.Height))
        {
            throw new InvalidOperationException(
                $"The image resolution must not exceed {support.Width} × {support.Height}.");
        }
        return (extension, frame.PixelWidth, frame.PixelHeight);
    }

    private static string DetectImageExtension(ReadOnlySpan<byte> header)
    {
        if (header[0] == 0xFF && header[1] == 0xD8) return ".jpg";
        if (header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F') return ".gif";
        if (header[0] == (byte)'B' && header[1] == (byte)'M') return ".bmp";
        return "";
    }

    private static byte[]? ReadCurrentLogo()
    {
        var drive = MountEfiPartition();
        try
        {
            var logoDirectory = Path.Combine($"{drive}:\\", "EFI", "Lenovo", "Logo");
            if (!Directory.Exists(logoDirectory)) return null;
            var files = Directory.GetFiles(logoDirectory, "mylogo_*");
            return files.Length == 0 ? null : File.ReadAllBytes(files[0]);
        }
        finally
        {
            UnmountEfiPartition(drive);
        }
    }

    private static char MountEfiPartition()
    {
        for (var drive = 'Z'; drive >= 'D'; drive--)
        {
            if (Directory.Exists($"{drive}:\\"))
                continue;
            RunMountVol($"{drive}:", "/s");
            if (Directory.Exists($"{drive}:\\"))
                return drive;
        }
        throw new IOException("No drive letter is available for the EFI system partition.");
    }

    private static void UnmountEfiPartition(char drive)
    {
        try { RunMountVol($"{drive}:", "/d"); }
        catch { }
    }

    private static void RunMountVol(string drive, string argument)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "mountvol.exe"),
            Arguments = $"{drive} {argument}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Windows could not start mountvol.exe.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? $"mountvol.exe failed with exit code {process.ExitCode}."
                : error.Trim());
    }

    private static string RunProcess(string fileName, string arguments, bool captureOutput = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = captureOutput
        }) ?? throw new InvalidOperationException($"Windows could not start {Path.GetFileName(fileName)}.");
        var output = captureOutput ? process.StandardOutput.ReadToEnd() : "";
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}."
                : error.Trim());
        return output;
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result);

    private static bool IsSupportedLogoVersion(uint version)
    {
        var major = version >> 16;
        var minor = version & 0xFFFF;
        return major switch
        {
            0 or 1 => false,
            2 => minor >= 3,
            _ => true
        };
    }

    private static string ReadFirmwareValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return "";
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
        return string.IsNullOrWhiteSpace(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? ""
            : text.Trim();
    }

    private sealed class NativeApi
    {
        public required GetStringDelegate GetBiosHardwareInfo { get; init; }
        public required GetStringDelegate GetSupportFunction { get; init; }
        public required SetBiosFunctionDelegate SetBiosFunction { get; init; }
        public required GetLogoDiyInfoDelegate GetLogoDiyInfo { get; init; }
        public required GetLogoDiyVersionDelegate GetLogoDiyVersion { get; init; }
        public required SetLogoDiySha256Delegate SetLogoDiySha256 { get; init; }
        public required SetLogoDiyInfoDelegate SetLogoDiyInfo { get; init; }

        public static NativeApi Load()
        {
            var path = LenovoVantageAddinLocator.FindLatestFile(AddinName, DllName)
                ?? throw new NotSupportedException("Lenovo BIOS Assistant is not installed.");
            var handle = NativeLibrary.Load(path);
            return new()
            {
                GetBiosHardwareInfo = Get<GetStringDelegate>(handle, "GetBiosHardwareInfo"),
                GetSupportFunction = Get<GetStringDelegate>(handle, "GetSupportFunction"),
                SetBiosFunction = Get<SetBiosFunctionDelegate>(handle, "SetBiosFunction"),
                GetLogoDiyInfo = Get<GetLogoDiyInfoDelegate>(handle, "GetLogoDIYInfo"),
                GetLogoDiyVersion = Get<GetLogoDiyVersionDelegate>(handle, "GetLogoDIYVersion"),
                SetLogoDiySha256 = Get<SetLogoDiySha256Delegate>(handle, "SetLogoDIYSHA256"),
                SetLogoDiyInfo = Get<SetLogoDiyInfoDelegate>(handle, "SetLogoDIYInfo")
            };
        }

        private static T Get<T>(IntPtr handle, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.BStr)]
    private delegate string GetStringDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetBiosFunctionDelegate(BiosBootFunction function);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetLogoDiyInfoDelegate(
        [MarshalAs(UnmanagedType.Bool)] ref bool enabled,
        ref byte formats,
        ref uint width,
        ref uint height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetLogoDiyVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetLogoDiySha256Delegate([In] byte[] hash);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetLogoDiyInfoDelegate([MarshalAs(UnmanagedType.Bool)] bool enabled);
}
