using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace ThinkBookToolkit;

internal enum EyeCareColorEffect
{
    Vivid = 1,
    VisionCare = 2,
    Amber = 3,
    Custom = 4
}

internal enum EyeCareScheduleMode
{
    Always = 1,
    Night = 2
}

internal enum ColorManagementMode
{
    Srgb = 1,
    AdobeRgb = 2,
    DisplayP3 = 3,
    Default = 4,
    Auto = 5,
    Native = 7,
    Rec709 = 8,
    DciP3 = 9,
    DicomDim = 10,
    DicomOffice = 11
}

internal sealed record EyeCareState(
    bool Available,
    bool Enabled,
    EyeCareColorEffect ColorEffect,
    EyeCareScheduleMode ScheduleMode,
    int CustomTemperature,
    bool ApiCapability,
    int OobeTemperature,
    string? Error = null);

internal sealed record ColorManagementState(
    bool Available,
    ColorManagementMode Mode,
    IReadOnlyDictionary<ColorManagementMode, bool> SupportedModes,
    int ColorType,
    string ColorTypeDetail,
    string OptionsColor,
    bool Is24H2OrLater,
    string? Error = null)
{
    public bool IsSupported(ColorManagementMode mode) =>
        SupportedModes.TryGetValue(mode, out var supported) && supported;
}

internal sealed record DisplaySettingsState(
    EyeCareState EyeCare,
    PcManagerEyeCareState PcManagerEyeCare,
    ColorManagementState ColorManagement);

internal static class DisplaySettingsController
{
    private const string SmartInteractAddinName = "SmartInteractAddin";
    private const string SmartColorAddinName = "SmartColorAddin";
    private static readonly SemaphoreSlim EyeCareHelperLock = new(1, 1);
    private static readonly object EyeCareHelperErrorLock = new();
    private static readonly StringBuilder EyeCareHelperErrors = new();
    private static Process? EyeCareHelperProcess;
    private static ChildProcessJob? EyeCareHelperJob;
    private static readonly SemaphoreSlim ColorHelperLock = new(1, 1);
    private static readonly object ColorHelperErrorLock = new();
    private static readonly StringBuilder ColorHelperErrors = new();
    private static Process? ColorHelperProcess;
    private static ChildProcessJob? ColorHelperJob;

    static DisplaySettingsController()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public static void Shutdown()
    {
        StopEyeCareHelper();
        StopColorHelper();
    }

    public static DisplaySettingsState ReadState(
        PcManagerEyeCareDefaults defaults)
    {
        EyeCareState eyeCare;
        PcManagerEyeCareState pcManagerEyeCare;
        ColorManagementState colorManagement;

        try
        {
            eyeCare = ReadEyeCareState();
        }
        catch (Exception ex)
        {
            eyeCare = new(
                false,
                false,
                EyeCareColorEffect.Vivid,
                EyeCareScheduleMode.Always,
                0,
                false,
                6500,
                ex.Message);
        }

        pcManagerEyeCare = PcManagerEyeCareController.ReadState(defaults);

        try
        {
            colorManagement = ReadColorManagementState();
        }
        catch (Exception ex)
        {
            colorManagement = new(
                false,
                ColorManagementMode.Default,
                DefaultColorModeSupport(false),
                0,
                string.Empty,
                string.Empty,
                false,
                ex.Message);
        }

        return new(eyeCare, pcManagerEyeCare, colorManagement);
    }

    public static EyeCareState SetEyeCareState(
        bool enabled,
        EyeCareColorEffect colorEffect,
        EyeCareScheduleMode scheduleMode,
        int customTemperature)
    {
        if (!Enum.IsDefined(colorEffect))
            throw new ArgumentOutOfRangeException(nameof(colorEffect));
        if (!Enum.IsDefined(scheduleMode))
            throw new ArgumentOutOfRangeException(nameof(scheduleMode));

        customTemperature = NormalizeCustomTemperature(customTemperature);
        return ParseEyeCareState(RunEyeCareHelper(
            "set",
            new Dictionary<string, string>
            {
                ["ECMSwitch"] = enabled ? "True" : "False",
                ["ColorEffects"] = ((int)colorEffect).ToString(),
                ["ScheduleMode"] = ((int)scheduleMode).ToString(),
                ["CustomModeCT"] = customTemperature.ToString()
            }));
    }

    public static EyeCareState SetEyeCareEnabled(
        bool enabled,
        EyeCareState current) =>
        SetEyeCareValue(
            "ECMSwitch",
            enabled ? "True" : "False",
            current);

    public static EyeCareState SetEyeCareColorEffect(
        EyeCareColorEffect colorEffect,
        EyeCareState current)
    {
        if (!Enum.IsDefined(colorEffect))
            throw new ArgumentOutOfRangeException(nameof(colorEffect));

        return SetEyeCareValue(
            "ColorEffects",
            ((int)colorEffect).ToString(),
            current);
    }

    public static EyeCareState SetEyeCareScheduleMode(
        EyeCareScheduleMode scheduleMode,
        EyeCareState current)
    {
        if (!Enum.IsDefined(scheduleMode))
            throw new ArgumentOutOfRangeException(nameof(scheduleMode));

        return SetEyeCareValue(
            "ScheduleMode",
            ((int)scheduleMode).ToString(),
            current);
    }

    public static EyeCareState SetEyeCareCustomTemperature(
        int customTemperature,
        EyeCareState current) =>
        SetEyeCareValue(
            "CustomModeCT",
            NormalizeCustomTemperature(customTemperature).ToString(),
            current);

    public static ColorManagementState SetColorManagementMode(
        ColorManagementMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        return ParseColorManagementState(
            RunColorHelper("set", (int)mode));
    }

    private static EyeCareState ReadEyeCareState()
    {
        return ParseEyeCareState(RunEyeCareHelper("read"));
    }

    private static ColorManagementState ReadColorManagementState()
    {
        return ParseColorManagementState(RunColorHelper("read"));
    }

    private static EyeCareState SetEyeCareValue(
        string key,
        string value,
        EyeCareState current)
    {
        var fallback = EyeCareFallbackAfterSet(key, value, current);
        return ParseEyeCareState(
            RunEyeCareHelper(
                "set",
                new Dictionary<string, string> { [key] = value }),
            fallback);
    }

    private static EyeCareState EyeCareFallbackAfterSet(
        string key,
        string value,
        EyeCareState current) =>
        key switch
        {
            "ECMSwitch" => current with
            {
                Enabled = string.Equals(
                    value,
                    "True",
                    StringComparison.OrdinalIgnoreCase)
            },
            "ColorEffects" when int.TryParse(value, out var colorEffect) &&
                                Enum.IsDefined(
                                    typeof(EyeCareColorEffect),
                                    colorEffect) => current with
            {
                ColorEffect = (EyeCareColorEffect)colorEffect
            },
            "ScheduleMode" when int.TryParse(value, out var scheduleMode) &&
                                Enum.IsDefined(
                                    typeof(EyeCareScheduleMode),
                                    scheduleMode) => current with
            {
                ScheduleMode = (EyeCareScheduleMode)scheduleMode
            },
            "CustomModeCT" when int.TryParse(
                value,
                out var customTemperature) => current with
            {
                CustomTemperature = NormalizeCustomTemperature(
                    customTemperature)
            },
            _ => current
        };

    private static EyeCareState ParseEyeCareState(
        string json,
        EyeCareState? fallback = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var available = root.TryGetProperty("Capability", out _)
            ? string.Equals(
                  ReadString(root, "Capability"),
                  "Support",
                  StringComparison.OrdinalIgnoreCase) &&
              ReadBoolean(root, "APICapability", true)
            : fallback?.Available ?? true;
        var colorEffectValue = ReadInt(root, "ColorEffects", 1);
        if (!root.TryGetProperty("ColorEffects", out _) &&
            fallback is not null)
        {
            colorEffectValue = (int)fallback.ColorEffect;
        }

        var colorEffect = Enum.IsDefined(
            typeof(EyeCareColorEffect),
            colorEffectValue)
            ? (EyeCareColorEffect)colorEffectValue
            : EyeCareColorEffect.Vivid;
        var scheduleValue = ReadInt(root, "ScheduleMode", 1);
        if (!root.TryGetProperty("ScheduleMode", out _) &&
            fallback is not null)
        {
            scheduleValue = (int)fallback.ScheduleMode;
        }

        var scheduleMode = Enum.IsDefined(
            typeof(EyeCareScheduleMode),
            scheduleValue)
            ? (EyeCareScheduleMode)scheduleValue
            : EyeCareScheduleMode.Always;
        var customTemperature = NormalizeCustomTemperature(
            ReadInt(root, "CustomModeCT", fallback?.CustomTemperature ?? 0));

        return new(
            available,
            ReadBoolean(root, "ECMSwitch", fallback?.Enabled ?? false),
            colorEffect,
            scheduleMode,
            customTemperature,
            ReadBoolean(root, "APICapability",
                fallback?.ApiCapability ?? true),
            ReadInt(root, "OOBEValue",
                fallback?.OobeTemperature ?? 6500));
    }

    private static ColorManagementState ParseColorManagementState(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        using var abilityDocument = JsonDocument.Parse(
            root.GetProperty("Ability").GetString() ?? "{}");
        using var typeDocument = JsonDocument.Parse(
            root.GetProperty("Type").GetString() ?? "{}");
        using var settingsDocument = JsonDocument.Parse(
            root.GetProperty("Settings").GetString() ?? "{}");
        using var osDocument = JsonDocument.Parse(
            root.GetProperty("Is24H2OrLater").GetString() ?? "{}");

        var abilityRoot = abilityDocument.RootElement;
        var typeRoot = typeDocument.RootElement;
        var settingsRoot = settingsDocument.RootElement;
        var osRoot = osDocument.RootElement;

        var available =
            ReadBoolean(abilityRoot, "ability") &&
            ReadInt(abilityRoot, "error_code") == 0;
        var modeValue = ReadInt(settingsRoot, "ColorState", 4);
        var mode = Enum.IsDefined(typeof(ColorManagementMode), modeValue)
            ? (ColorManagementMode)modeValue
            : ColorManagementMode.Default;
        var optionsColor = ReadString(settingsRoot, "OptionsColor");

        return new(
            available,
            mode,
            ParseColorModeSupport(optionsColor, abilityRoot, available),
            ReadInt(typeRoot, "color_type"),
            ReadString(settingsRoot, "ColorTypeDetail"),
            optionsColor,
            ReadInt(osRoot, "result") == 1);
    }

    private static IReadOnlyDictionary<ColorManagementMode, bool>
        ParseColorModeSupport(
            string optionsColor,
            JsonElement ability,
            bool available)
    {
        var supported = DefaultColorModeSupport(false);
        if (!available)
            return supported;

        foreach (ColorManagementMode mode in Enum.GetValues(
                     typeof(ColorManagementMode)))
        {
            var index = (int)mode - 1;
            if (index >= 0 && index < optionsColor.Length)
                supported[mode] = optionsColor[index] == '1';
        }

        supported[ColorManagementMode.Native] =
            supported[ColorManagementMode.Native] ||
            ReadInt(ability, "NATIVE_TYPE") == 1;
        supported[ColorManagementMode.Rec709] =
            supported[ColorManagementMode.Rec709] ||
            ReadInt(ability, "REC709_TYPE") == 1;
        supported[ColorManagementMode.DciP3] =
            supported[ColorManagementMode.DciP3] ||
            ReadInt(ability, "DCIP3_TYPE") == 1 ||
            ReadInt(ability, "DCI_P3_TYPE") == 1;
        supported[ColorManagementMode.DicomDim] =
            supported[ColorManagementMode.DicomDim] ||
            ReadInt(ability, "DICOM_Dim") == 1;
        supported[ColorManagementMode.DicomOffice] =
            supported[ColorManagementMode.DicomOffice] ||
            ReadInt(ability, "DICOM_Office") == 1;

        return supported;
    }

    private static Dictionary<ColorManagementMode, bool>
        DefaultColorModeSupport(bool value)
    {
        var supported = new Dictionary<ColorManagementMode, bool>();
        foreach (ColorManagementMode mode in Enum.GetValues(
                     typeof(ColorManagementMode)))
        {
            supported[mode] = value;
        }

        return supported;
    }

    private static int NormalizeCustomTemperature(int value)
    {
        if (value <= 0)
            return 0;

        value = Math.Max(2700, Math.Min(6500, value));
        return (int)Math.Round(value / 100.0) * 100;
    }

    private static int ReadInt(
        JsonElement root,
        string propertyName,
        int defaultValue = 0)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) =>
                number,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                out var number) => number,
            _ => defaultValue
        };
    }

    private static string ReadString(
        JsonElement root,
        string propertyName,
        string defaultValue = "")
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? defaultValue,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => defaultValue
        };
    }

    private static bool ReadBoolean(
        JsonElement root,
        string propertyName,
        bool defaultValue = false)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) &&
                                    number != 0,
            JsonValueKind.String => bool.TryParse(
                value.GetString(),
                out var boolean) && boolean,
            _ => defaultValue
        };
    }

    private static string RunEyeCareHelper(
        string action,
        IReadOnlyDictionary<string, string>? settings = null,
        int timeoutMilliseconds = 6000)
    {
        EyeCareHelperLock.Wait();
        try
        {
            var process = EnsureEyeCareHelper();
            var request = JsonSerializer.Serialize(new
            {
                Action = action,
                Settings = settings ?? new Dictionary<string, string>()
            });
            process.StandardInput.WriteLine(request);
            process.StandardInput.Flush();

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                timeoutMilliseconds);
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    StopEyeCareHelper();
                    throw new TimeoutException(
                        "Lenovo eye care display control request timed out.");
                }

                var lineTask = process.StandardOutput.ReadLineAsync();
                if (!lineTask.Wait(remaining))
                {
                    StopEyeCareHelper();
                    throw new TimeoutException(
                        "Lenovo eye care display control request timed out.");
                }

                var line = lineTask.GetAwaiter().GetResult();
                if (line is null)
                {
                    var error = GetAndClearEyeCareHelperErrors();
                    StopEyeCareHelper();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? "Lenovo eye care display control helper exited."
                            : error);
                }

                line = line.Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal))
                    continue;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!ReadBoolean(root, "Ok"))
                {
                    var error = ReadString(
                        root,
                        "Error",
                        "Lenovo eye care display control failed.");
                    throw new InvalidOperationException(error);
                }

                return root.TryGetProperty("Data", out var data)
                    ? data.GetRawText()
                    : "{}";
            }
        }
        catch
        {
            if (EyeCareHelperProcess is { HasExited: true })
                StopEyeCareHelper();
            throw;
        }
        finally
        {
            EyeCareHelperLock.Release();
        }
    }

    private static Process EnsureEyeCareHelper()
    {
        if (EyeCareHelperProcess is { HasExited: false } existingProcess)
            return existingProcess;

        StopEyeCareHelper();
        Process? process = null;
        var (addinPath, addinDirectory) = FindRequiredAddin(
            SmartInteractAddinName,
            "SmartInteractAddin.dll",
            "Lenovo eye care display control is not installed.");
        var helperScript = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            [Environment]::CurrentDirectory = '{{EscapePowerShell(addinDirectory)}}'
            [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(addinPath)}}')
            $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
            $type = [SmartInteractAddin.ContractHandlers.EyeCareMode.Utils.NightLight]
            $night = $type.GetProperty(
                'Instance',
                [Reflection.BindingFlags]'Public,Static').GetValue($null)
            $settingType =
                [SmartInteractAddin.PayloadTypes.EyeCareMode.Setting]
            $settingListType =
                [SmartInteractAddin.PayloadTypes.EyeCareMode.SettingList]
            $genericListType =
                [System.Collections.Generic.List``1].MakeGenericType(
                    $settingType)

            function ConvertTo-Map($list) {
                $items = @{}
                if ($list -and $list.Items) {
                    foreach ($item in $list.Items) {
                        $items[$item.Key] = $item.Value
                    }
                }
                return $items
            }

            function New-SettingList($settings) {
                $list = [Activator]::CreateInstance($settingListType)
                $list.Items = [Activator]::CreateInstance($genericListType)
                $hasSource = $false
                foreach ($property in $settings.PSObject.Properties) {
                    if ($property.Name -eq 'Source') {
                        $hasSource = $true
                    }
                    $setting = [Activator]::CreateInstance($settingType)
                    $setting.Key = [string]$property.Name
                    $setting.Value = [string]$property.Value
                    $setting.Preview = ''
                    [void]$list.Items.Add($setting)
                }
                if (-not $hasSource) {
                    $setting = [Activator]::CreateInstance($settingType)
                    $setting.Key = 'Source'
                    $setting.Value = '5.x'
                    $setting.Preview = ''
                    [void]$list.Items.Add($setting)
                }
                return $list
            }

            function Sync-ECM5XStatus() {
                $method = $type.GetMethod('ECM5XSyncECMStatus', $flags)
                if ($method) {
                    [void]$method.Invoke($night, @())
                }
            }

            function Write-Response($ok, $data, $errorText) {
                [Console]::Out.WriteLine((@{
                    Ok = $ok
                    Data = $data
                    Error = $errorText
                } | ConvertTo-Json -Compress -Depth 10))
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                try {
                    if ([string]::IsNullOrWhiteSpace($line)) {
                        continue
                    }
                    $request = $line | ConvertFrom-Json
                    switch ($request.Action) {
                        'read' {
                            Sync-ECM5XStatus
                            $current = $type.GetMethod(
                                'GetECM5XCurrentSettings',
                                $flags).Invoke($night, @())
                            Write-Response $true (ConvertTo-Map $current) ''
                        }
                        'set' {
                            $list = New-SettingList $request.Settings
                            $result = $type.GetMethod(
                                'ECM5XSetCurrentSettings',
                                $flags).Invoke($night, @($list))
                            Start-Sleep -Milliseconds 300
                            Write-Response $true (ConvertTo-Map $result) ''
                        }
                        'exit' {
                            Write-Response $true @{} ''
                            return
                        }
                        default {
                            throw "Unknown eye care helper action: $($request.Action)"
                        }
                    }
                } catch {
                    Write-Response $false @{} $_.Exception.Message
                }
            }
            """;
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(helperScript));
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        var job = new ChildProcessJob();
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start Windows PowerShell.");
            job.Assign(process);
            EyeCareHelperJob = job;
        }
        catch
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            job.Dispose();
            throw;
        }
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
                return;

            lock (EyeCareHelperErrorLock)
                EyeCareHelperErrors.AppendLine(args.Data);
        };
        process.BeginErrorReadLine();
        EyeCareHelperProcess = process;
        return process;
    }

    private static string GetAndClearEyeCareHelperErrors()
    {
        lock (EyeCareHelperErrorLock)
        {
            var error = NormalizePowerShellError(
                EyeCareHelperErrors.ToString());
            EyeCareHelperErrors.Clear();
            return error;
        }
    }

    private static void StopEyeCareHelper()
    {
        var process = EyeCareHelperProcess;
        EyeCareHelperProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine(
                        JsonSerializer.Serialize(new
                        {
                            Action = "exit",
                            Settings = new Dictionary<string, string>()
                        }));
                    process.StandardInput.Flush();
                    process.WaitForExit(500);
                }
                catch
                {
                }

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            EyeCareHelperJob?.Dispose();
            EyeCareHelperJob = null;
            process.Dispose();
        }
    }

    private static string RunColorHelper(
        string action,
        int? mode = null,
        int timeoutMilliseconds = 6000)
    {
        ColorHelperLock.Wait();
        try
        {
            var process = EnsureColorHelper();
            var request = JsonSerializer.Serialize(new
            {
                Action = action,
                Mode = mode
            });
            process.StandardInput.WriteLine(request);
            process.StandardInput.Flush();

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                timeoutMilliseconds);
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    StopColorHelper();
                    throw new TimeoutException(
                        "Lenovo color management request timed out.");
                }

                var lineTask = process.StandardOutput.ReadLineAsync();
                if (!lineTask.Wait(remaining))
                {
                    StopColorHelper();
                    throw new TimeoutException(
                        "Lenovo color management request timed out.");
                }

                var line = lineTask.GetAwaiter().GetResult();
                if (line is null)
                {
                    var error = GetAndClearColorHelperErrors();
                    StopColorHelper();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? "Lenovo color management helper exited."
                            : error);
                }

                line = line.Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal))
                    continue;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!ReadBoolean(root, "Ok"))
                {
                    throw new InvalidOperationException(
                        ReadString(
                            root,
                            "Error",
                            "Lenovo color management failed."));
                }

                return root.TryGetProperty("Data", out var data)
                    ? data.GetRawText()
                    : "{}";
            }
        }
        catch
        {
            if (ColorHelperProcess is { HasExited: true })
                StopColorHelper();
            throw;
        }
        finally
        {
            ColorHelperLock.Release();
        }
    }

    private static Process EnsureColorHelper()
    {
        if (ColorHelperProcess is { HasExited: false } existingProcess)
            return existingProcess;

        StopColorHelper();
        Process? process = null;
        var (addinPath, addinDirectory) = FindRequiredAddin(
            SmartColorAddinName,
            "SmartColorAddin.dll",
            "Lenovo color management is not installed.");
        var helperScript = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            [Environment]::CurrentDirectory = '{{EscapePowerShell(addinDirectory)}}'
            [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(addinPath)}}')
            $agent =
                [SmartColorAddin.ContractHandlers.SmartColorManagerAgent]::GetInstance()

            function Read-ColorState() {
                return @{
                    Ability = $agent.GetAbility()
                    Type = $agent.GetColorManagerType()
                    Settings = $agent.GetColorManagerSettings()
                    Is24H2OrLater = $agent.GetIs24H2OrLater()
                }
            }

            function Write-Response($ok, $data, $errorText) {
                [Console]::Out.WriteLine((@{
                    Ok = $ok
                    Data = $data
                    Error = $errorText
                } | ConvertTo-Json -Compress -Depth 10))
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                try {
                    if ([string]::IsNullOrWhiteSpace($line)) {
                        continue
                    }
                    $request = $line | ConvertFrom-Json
                    switch ($request.Action) {
                        'read' {
                            Write-Response $true (Read-ColorState) ''
                        }
                        'set' {
                            $agent.SetColorManagerState([int]$request.Mode)
                            Start-Sleep -Milliseconds 150
                            Write-Response $true (Read-ColorState) ''
                        }
                        'exit' {
                            Write-Response $true @{} ''
                            return
                        }
                        default {
                            throw "Unknown color helper action: $($request.Action)"
                        }
                    }
                } catch {
                    Write-Response $false @{} $_.Exception.Message
                }
            }
            """;
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(helperScript));
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        var job = new ChildProcessJob();
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start Windows PowerShell.");
            job.Assign(process);
            ColorHelperJob = job;
        }
        catch
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            job.Dispose();
            throw;
        }
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
                return;

            lock (ColorHelperErrorLock)
                ColorHelperErrors.AppendLine(args.Data);
        };
        process.BeginErrorReadLine();
        ColorHelperProcess = process;
        return process;
    }

    private static string GetAndClearColorHelperErrors()
    {
        lock (ColorHelperErrorLock)
        {
            var error = NormalizePowerShellError(
                ColorHelperErrors.ToString());
            ColorHelperErrors.Clear();
            return error;
        }
    }

    private static void StopColorHelper()
    {
        var process = ColorHelperProcess;
        ColorHelperProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine(
                        JsonSerializer.Serialize(new
                        {
                            Action = "exit",
                            Mode = (int?)null
                        }));
                    process.StandardInput.Flush();
                    process.WaitForExit(500);
                }
                catch
                {
                }

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            ColorHelperJob?.Dispose();
            ColorHelperJob = null;
            process.Dispose();
        }
    }

    private static string RunWindowsPowerShell(
        string script,
        string operation,
        int timeoutMilliseconds = 20000)
    {
        script = "$ProgressPreference = 'SilentlyContinue'\r\n" + script;
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start Windows PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{operation} request timed out.");
        }

        var output = outputTask.GetAwaiter().GetResult().Trim();
        var error = NormalizePowerShellError(
            errorTask.GetAwaiter().GetResult());
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"{operation} returned no data."
                    : error);
        }

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error);

        return output;
    }

    private static string NormalizePowerShellError(string error)
    {
        error = error.Trim();
        var xmlStart = error.IndexOf("<Objs", StringComparison.Ordinal);
        if (!error.StartsWith("#< CLIXML", StringComparison.Ordinal) ||
            xmlStart < 0)
        {
            return error;
        }

        try
        {
            var document = XDocument.Parse(error[xmlStart..]);
            var messages = document
                .Descendants()
                .Where(element =>
                    element.Name.LocalName == "S" &&
                    string.Equals(
                        element.Attribute("S")?.Value,
                        "Error",
                        StringComparison.OrdinalIgnoreCase))
                .Select(element => DecodePowerShellXml(element.Value))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return messages.Length > 0
                ? string.Join(Environment.NewLine, messages)
                : string.Empty;
        }
        catch
        {
            return error;
        }
    }

    private static string DecodePowerShellXml(string value) =>
        Regex.Replace(
            value,
            "_x([0-9A-Fa-f]{4})_",
            match => ((char)Convert.ToInt32(
                match.Groups[1].Value,
                16)).ToString());

    private static (string AddinPath, string AddinDirectory)
        FindRequiredAddin(
            string addinName,
            string fileName,
            string notSupportedMessage)
    {
        var addinPath = LenovoVantageAddinLocator.FindLatestFile(
                addinName,
                fileName)
            ?? throw new NotSupportedException(notSupportedMessage);
        var addinDirectory = Path.GetDirectoryName(addinPath)
            ?? throw new InvalidOperationException(
                "The Lenovo addin path is invalid.");
        return (addinPath, addinDirectory);
    }

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
