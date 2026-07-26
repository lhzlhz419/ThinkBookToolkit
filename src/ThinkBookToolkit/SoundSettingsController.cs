using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace ThinkBookToolkit;

internal enum DolbyProfile
{
    Movie = 0,
    Music = 1,
    Game = 2,
    Voice = 3,
    Custom = 4,
    Dynamic = 5
}

internal enum MicrophoneNoiseMode
{
    Off = 0,
    MultipleVoices = 1,
    Normal = 2,
    VoiceRecognition = 3,
    OnlyMyVoice = 4
}

internal sealed record DolbyState(
    bool Available,
    bool Enabled,
    DolbyProfile Profile,
    string? Error = null);

internal sealed record MicrophoneNoiseState(
    bool Available,
    MicrophoneNoiseMode Mode,
    bool SupportsMultipleVoices,
    bool SupportsVoiceRecognition,
    bool SupportsOnlyMyVoice,
    bool HasVoiceId,
    int VendorId,
    string? Error = null);

internal sealed record SpeakerNoiseState(
    bool Available,
    bool Enabled,
    string? Error = null);

internal sealed record NoiseSettingsState(
    MicrophoneNoiseState MicrophoneNoise,
    SpeakerNoiseState SpeakerNoise);

internal sealed record SoundSettingsState(
    DolbyState Dolby,
    SpeakerNoiseState SpeakerNoise,
    MicrophoneNoiseState MicrophoneNoise);

internal static class SoundSettingsController
{
    private const string MultimediaAddinName = "MultimediaAddin";
    private const string NoiseAddinName = "SmartNoiseCancelledAddin";
    private static readonly SemaphoreSlim PowerShellLock = new(1, 1);
    private static readonly object ActiveProcessLock = new();
    private static Process? ActivePowerShellProcess;
    private static ChildProcessJob? ActivePowerShellJob;
    private static readonly SemaphoreSlim SoundHelperLock = new(1, 1);
    private static readonly object SoundHelperErrorLock = new();
    private static readonly StringBuilder SoundHelperErrors = new();
    private static Process? SoundHelperProcess;

    static SoundSettingsController()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public static SoundSettingsState ReadState()
    {
        DolbyState dolby;
        NoiseSettingsState noise;

        try
        {
            dolby = ReadDolbyState();
        }
        catch (Exception ex)
        {
            dolby = new(false, false, DolbyProfile.Dynamic, ex.Message);
        }

        try
        {
            noise = ReadNoiseState();
        }
        catch (Exception ex)
        {
            noise = new(
                new(
                    false,
                    MicrophoneNoiseMode.Off,
                    false,
                    false,
                    false,
                    false,
                    0,
                    ex.Message),
                new(false, false, ex.Message));
        }

        return new(dolby, noise.SpeakerNoise, noise.MicrophoneNoise);
    }

    public static DolbyState SetDolbyState(
        bool enabled,
        DolbyProfile profile)
    {
        var dllPath = LenovoVantageAddinLocator.FindLatestFile(
            MultimediaAddinName,
            "DolbyHSASupport.dll")
            ?? throw new NotSupportedException(
                "Lenovo Dolby HSA support is not installed.");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(dllPath)}}')
            $client = New-Object DolbyHSASupport.DolbyHSAClient
            if ($client.Initialize() -ne 0) {
                throw 'Dolby DAX RPC initialization failed.'
            }
            $targetEnabled = ${{enabled.ToString().ToLowerInvariant()}}
            $targetProfile = {{(int)profile}}
            $actualEnabled = $client.GetDolbyEnabled()
            $actualProfile = $client.GetActiveProfile()
            for ($attempt = 0; $attempt -lt 8; $attempt++) {
                $client.SetDolbyEnabled($targetEnabled)
                if ($targetEnabled) {
                    Start-Sleep -Milliseconds 75
                    $client.SetActiveProfile($targetProfile)
                }
                Start-Sleep -Milliseconds 150
                $actualEnabled = $client.GetDolbyEnabled()
                $actualProfile = $client.GetActiveProfile()
                if ($actualEnabled -eq $targetEnabled -and
                    (-not $targetEnabled -or
                     $actualProfile -eq $targetProfile)) {
                    break
                }
            }
            if ($actualEnabled -ne $targetEnabled -or
                ($targetEnabled -and $actualProfile -ne $targetProfile)) {
                throw 'Dolby DAX did not confirm the requested state.'
            }
            [Console]::Out.Write((@{
                Enabled = $actualEnabled
                Profile = $actualProfile
            } | ConvertTo-Json -Compress))
            """;

        return ParseDolbyState(RunWindowsPowerShell(script));
    }

    public static MicrophoneNoiseState SetMicrophoneNoiseMode(
        MicrophoneNoiseMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var confirmed = RunNoiseRequest($$"""
            $setResult = $wrapper.SyncSendMsg(
                'set_microphone_mode',
                '{"microphone_mode":{{(int)mode}}}') | ConvertFrom-Json
            if ($setResult.error_code -ne 0) {
                throw "Lenovo noise cancellation returned error $($setResult.error_code)."
            }
            Start-Sleep -Milliseconds 150
            """).MicrophoneNoise;
        if (confirmed.Mode != mode)
        {
            throw new InvalidOperationException(
                "Lenovo noise cancellation did not confirm the microphone mode.");
        }

        return confirmed;
    }

    public static SpeakerNoiseState SetSpeakerNoiseEnabled(bool enabled)
    {
        var confirmed = RunNoiseRequest($$"""
            $setResult = $wrapper.SyncSendMsg(
                'set_speaker_status',
                '{"speaker_status":{{(enabled ? 1 : 0)}}}') | ConvertFrom-Json
            if ($setResult.error_code -ne 0) {
                throw "Lenovo noise cancellation returned error $($setResult.error_code)."
            }
            Start-Sleep -Milliseconds 150
            """).SpeakerNoise;
        if (confirmed.Enabled != enabled)
        {
            throw new InvalidOperationException(
                "Lenovo noise cancellation did not confirm the speaker mode.");
        }

        return confirmed;
    }

    public static void Shutdown()
    {
        StopSoundHelper();
        lock (ActiveProcessLock)
        {
            ActivePowerShellJob?.Dispose();
            ActivePowerShellJob = null;
            var process = ActivePowerShellProcess;
            ActivePowerShellProcess = null;
            if (process is not { HasExited: false })
                return;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    public static MicrophoneNoiseState RecordMicrophoneVoiceId(
        bool replaceExisting)
    {
        var addinPath = LenovoVantageAddinLocator.FindLatestFile(
            NoiseAddinName,
            "SmartNoiseCancelledAddin.dll")
            ?? throw new NotSupportedException(
                "Lenovo microphone noise cancellation is not installed.");
        var addinDirectory = Path.GetDirectoryName(addinPath)
            ?? throw new InvalidOperationException(
                "The Lenovo noise cancellation path is invalid.");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $agent = $null
            $recording = $false
            try {
                [Environment]::CurrentDirectory = '{{EscapePowerShell(addinDirectory)}}'
                [void][Reflection.Assembly]::LoadFrom(
                    (Join-Path '{{EscapePowerShell(addinDirectory)}}' 'NAudio.dll'))
                [void][Reflection.Assembly]::LoadFrom(
                    (Join-Path '{{EscapePowerShell(addinDirectory)}}' 'Newtonsoft.Json.dll'))
                [void][Reflection.Assembly]::LoadFrom(
                    (Join-Path '{{EscapePowerShell(addinDirectory)}}' 'VoiceRecoding.dll'))
                [void][Reflection.Assembly]::LoadFrom(
                    (Join-Path '{{EscapePowerShell(addinDirectory)}}' 'SERecordingPlugin.dll'))
                [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(addinPath)}}')
                $transport = [SmartNoiseCancelledAddin.SENoiseCancelledAgent]::Instance
                $agent = [SmartNoiseCancelledAddin.ContractHandlers.SmartNoiseCancelledAgent]::GetInstance()
                if (-not $agent.RecordChecked()) {
                    throw 'The microphone is not ready for voice recording.'
                }
                if (${{replaceExisting.ToString().ToLowerInvariant()}} -and
                    -not $agent.RemoveVoiceId()) {
                    throw 'Could not remove the existing voice ID.'
                }
                if (-not $agent.BeginRecordVoiceId()) {
                    throw 'Could not start voice recording.'
                }
                $recording = $true
                Start-Sleep -Seconds 20
                if (-not $agent.StopRecordVoiceId()) {
                    throw 'Could not stop or process the voice recording.'
                }
                $recording = $false
                if (-not $agent.FinshRecordVoiceId()) {
                    throw 'Could not save the voice ID.'
                }
                $ability = $transport.SyncSendMsg(
                    'ability',
                    '') | ConvertFrom-Json
                $status = $transport.SyncSendMsg(
                    'get_nc_status',
                    '') | ConvertFrom-Json
                [Console]::Out.Write((@{
                    Ability = $ability
                    Status = $status
                } | ConvertTo-Json -Compress -Depth 8))
            }
            catch {
                if ($recording -and $null -ne $agent) {
                    [void]$agent.InterruptRecordVocieId()
                }
                [Console]::Error.Write($_.Exception.Message)
            }
            finally {
                [Console]::Out.Flush()
                [Console]::Error.Flush()
                Stop-Process -Id $PID -Force
            }
            """;

        var json = RunWindowsPowerShell(
            script,
            acceptOutputOnFailure: true,
            operation: "Lenovo voice ID recording",
            timeoutMilliseconds: 55000);
        return ParseNoiseState(json).MicrophoneNoise;
    }

    private static DolbyState ReadDolbyState()
    {
        var dllPath = LenovoVantageAddinLocator.FindLatestFile(
            MultimediaAddinName,
            "DolbyHSASupport.dll")
            ?? throw new NotSupportedException(
                "Lenovo Dolby HSA support is not installed.");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(dllPath)}}')
            $client = New-Object DolbyHSASupport.DolbyHSAClient
            if ($client.Initialize() -ne 0) {
                throw 'Dolby DAX RPC initialization failed.'
            }
            [Console]::Out.Write((@{
                Enabled = $client.GetDolbyEnabled()
                Profile = $client.GetActiveProfile()
            } | ConvertTo-Json -Compress))
            """;

        return ParseDolbyState(RunWindowsPowerShell(script));
    }

    private static DolbyState ParseDolbyState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var enabled = root.GetProperty("Enabled").GetBoolean();
        var profileValue = root.GetProperty("Profile").GetInt32();
        var profile = Enum.IsDefined(typeof(DolbyProfile), profileValue)
            ? (DolbyProfile)profileValue
            : DolbyProfile.Dynamic;
        return new(true, enabled, profile);
    }

    private static NoiseSettingsState ReadNoiseState() =>
        RunNoiseRequest(string.Empty);

    private static NoiseSettingsState RunNoiseRequest(string requestScript)
    {
        var addinPath = LenovoVantageAddinLocator.FindLatestFile(
            NoiseAddinName,
            "SmartNoiseCancelledAddin.dll")
            ?? throw new NotSupportedException(
                "Lenovo microphone noise cancellation is not installed.");
        var addinDirectory = Path.GetDirectoryName(addinPath)
            ?? throw new InvalidOperationException(
                "The Lenovo noise cancellation path is invalid.");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                [Environment]::CurrentDirectory = '{{EscapePowerShell(addinDirectory)}}'
                [void][Reflection.Assembly]::LoadFrom('{{EscapePowerShell(addinPath)}}')
                $wrapper = [SmartNoiseCancelledAddin.SENoiseCancellingWrap]::Instance
                $callback = [Func[string,string,string,string]] {
                    param($module, $action, $data)
                    return ''
                }
                $wrapper.RegisterSEMessage($callback)
                [void]$wrapper.SyncSendMsg('init', '')
                {{requestScript}}
                $ability = $wrapper.SyncSendMsg(
                    'ability',
                    '') | ConvertFrom-Json
                $status = $wrapper.SyncSendMsg(
                    'get_nc_status',
                    '') | ConvertFrom-Json
                [Console]::Out.Write((@{
                    Ability = $ability
                    Status = $status
                } | ConvertTo-Json -Compress -Depth 8))
            }
            catch {
                [Console]::Error.Write($_.Exception.Message)
            }
            finally {
                [Console]::Out.Flush()
                [Console]::Error.Flush()
                Stop-Process -Id $PID -Force
            }
            """;

        var json = RunWindowsPowerShell(
            script,
            acceptOutputOnFailure: true,
            operation: "Lenovo noise cancellation");
        return ParseNoiseState(json);
    }

    private static NoiseSettingsState ParseNoiseState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ability = root.GetProperty("Ability");
        if (ReadInt(ability, "ability") != 1)
        {
            return new(
                new(
                    false,
                    MicrophoneNoiseMode.Off,
                    false,
                    false,
                    false,
                    false,
                    0),
                new(false, false));
        }

        var status = root.GetProperty("Status");
        var errorCode = ReadInt(status, "error_code");
        if (errorCode != 0)
        {
            throw new InvalidOperationException(
                $"Lenovo noise cancellation returned error {errorCode}.");
        }

        var modeValue = ReadInt(status, "microphone_mode");
        var mode = Enum.IsDefined(typeof(MicrophoneNoiseMode), modeValue)
            ? (MicrophoneNoiseMode)modeValue
            : MicrophoneNoiseMode.Off;
        var voiceIdMode = ReadInt(status, "voiceid_mode", -1);
        var hasVoiceId =
            voiceIdMode == 1 ||
            ReadBoolean(status, "is_mic_set_voiceID");

        var speakerSupported =
            status.TryGetProperty("speaker_mode", out _) &&
            (!ability.TryGetProperty("items", out var items) ||
             ReadInt(items, "F40061", 1) != 0);

        return new(
            new(
                ReadBoolean(status, "mic_page_enable", true),
                mode,
                ReadInt(status, "is_support_shared_mode") == 1,
                ReadInt(status, "issupport_spacial") == 1,
                ReadInt(status, "voiceid_enabled") == 1 &&
                voiceIdMode >= 0,
                hasVoiceId,
                ReadInt(status, "vendorid")),
            new(
                speakerSupported,
                ReadInt(status, "speaker_mode") == 1));
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

    private static SoundSettingsState ParseSoundHelperState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var dolbyRoot = root.GetProperty("Dolby");
        DolbyState dolby;
        var dolbyError = ReadString(dolbyRoot, "Error");
        if (!string.IsNullOrWhiteSpace(dolbyError) ||
            !ReadBoolean(dolbyRoot, "Available"))
        {
            dolby = new(
                false,
                false,
                DolbyProfile.Dynamic,
                string.IsNullOrWhiteSpace(dolbyError)
                    ? null
                    : dolbyError);
        }
        else
        {
            var profileValue = ReadInt(dolbyRoot, "Profile", 5);
            var profile = Enum.IsDefined(typeof(DolbyProfile), profileValue)
                ? (DolbyProfile)profileValue
                : DolbyProfile.Dynamic;
            dolby = new(
                true,
                ReadBoolean(dolbyRoot, "Enabled"),
                profile);
        }

        var noiseRoot = root.GetProperty("Noise");
        var noiseError = ReadString(noiseRoot, "Error");
        var noise = string.IsNullOrWhiteSpace(noiseError)
            ? ParseNoiseState(noiseRoot.GetRawText())
            : new NoiseSettingsState(
                new(
                    false,
                    MicrophoneNoiseMode.Off,
                    false,
                    false,
                    false,
                    false,
                    0,
                    noiseError),
                new(false, false, noiseError));

        return new(dolby, noise.SpeakerNoise, noise.MicrophoneNoise);
    }

    private static string RunSoundHelperRead(int timeoutMilliseconds = 6000)
    {
        SoundHelperLock.Wait();
        try
        {
            var process = EnsureSoundHelper();
            process.StandardInput.WriteLine(
                JsonSerializer.Serialize(new { Action = "read" }));
            process.StandardInput.Flush();

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                timeoutMilliseconds);
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    StopSoundHelper();
                    throw new TimeoutException(
                        "Lenovo sound settings request timed out.");
                }

                var lineTask = process.StandardOutput.ReadLineAsync();
                if (!lineTask.Wait(remaining))
                {
                    StopSoundHelper();
                    throw new TimeoutException(
                        "Lenovo sound settings request timed out.");
                }

                var line = lineTask.GetAwaiter().GetResult();
                if (line is null)
                {
                    var error = GetAndClearSoundHelperErrors();
                    StopSoundHelper();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? "Lenovo sound settings helper exited."
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
                            "Lenovo sound settings failed."));
                }

                return root.TryGetProperty("Data", out var data)
                    ? data.GetRawText()
                    : "{}";
            }
        }
        catch
        {
            if (SoundHelperProcess is { HasExited: true })
                StopSoundHelper();
            throw;
        }
        finally
        {
            SoundHelperLock.Release();
        }
    }

    private static Process EnsureSoundHelper()
    {
        if (SoundHelperProcess is { HasExited: false } process)
            return process;

        StopSoundHelper();
        var dolbyPath = LenovoVantageAddinLocator.FindLatestFile(
            MultimediaAddinName,
            "DolbyHSASupport.dll");
        var noisePath = LenovoVantageAddinLocator.FindLatestFile(
            NoiseAddinName,
            "SmartNoiseCancelledAddin.dll");
        if (dolbyPath is null && noisePath is null)
        {
            throw new NotSupportedException(
                "Lenovo sound settings addins are not installed.");
        }

        var noiseDirectory = noisePath is null
            ? string.Empty
            : Path.GetDirectoryName(noisePath) ?? string.Empty;
        var helperScript = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            $dolbyPath = '{{EscapePowerShell(dolbyPath ?? string.Empty)}}'
            $noisePath = '{{EscapePowerShell(noisePath ?? string.Empty)}}'
            $noiseDirectory = '{{EscapePowerShell(noiseDirectory)}}'
            $dolbyClient = $null
            $dolbyInitError = ''
            $wrapper = $null
            $noiseInitError = ''

            if (-not [string]::IsNullOrWhiteSpace($dolbyPath)) {
                try {
                    [void][Reflection.Assembly]::LoadFrom($dolbyPath)
                    $dolbyClient = New-Object DolbyHSASupport.DolbyHSAClient
                    if ($dolbyClient.Initialize() -ne 0) {
                        throw 'Dolby DAX RPC initialization failed.'
                    }
                } catch {
                    $dolbyInitError = $_.Exception.Message
                }
            } else {
                $dolbyInitError = 'Lenovo Dolby HSA support is not installed.'
            }

            if (-not [string]::IsNullOrWhiteSpace($noisePath)) {
                try {
                    [Environment]::CurrentDirectory = $noiseDirectory
                    [void][Reflection.Assembly]::LoadFrom($noisePath)
                    $wrapper =
                        [SmartNoiseCancelledAddin.SENoiseCancellingWrap]::Instance
                    $callback = [Func[string,string,string,string]] {
                        param($module, $action, $data)
                        return ''
                    }
                    $wrapper.RegisterSEMessage($callback)
                    [void]$wrapper.SyncSendMsg('init', '')
                } catch {
                    $noiseInitError = $_.Exception.Message
                }
            } else {
                $noiseInitError =
                    'Lenovo microphone noise cancellation is not installed.'
            }

            function Read-DolbyState() {
                if (-not [string]::IsNullOrWhiteSpace($dolbyInitError)) {
                    return @{
                        Available = $false
                        Enabled = $false
                        Profile = 5
                        Error = $dolbyInitError
                    }
                }
                try {
                    return @{
                        Available = $true
                        Enabled = $dolbyClient.GetDolbyEnabled()
                        Profile = $dolbyClient.GetActiveProfile()
                        Error = ''
                    }
                } catch {
                    return @{
                        Available = $false
                        Enabled = $false
                        Profile = 5
                        Error = $_.Exception.Message
                    }
                }
            }

            function Read-NoiseState() {
                if (-not [string]::IsNullOrWhiteSpace($noiseInitError)) {
                    return @{
                        Ability = @{ ability = 0 }
                        Status = @{}
                        Error = $noiseInitError
                    }
                }
                try {
                    $ability = $wrapper.SyncSendMsg(
                        'ability',
                        '') | ConvertFrom-Json
                    $status = $wrapper.SyncSendMsg(
                        'get_nc_status',
                        '') | ConvertFrom-Json
                    return @{
                        Ability = $ability
                        Status = $status
                        Error = ''
                    }
                } catch {
                    return @{
                        Ability = @{ ability = 0 }
                        Status = @{}
                        Error = $_.Exception.Message
                    }
                }
            }

            function Write-Response($ok, $data, $errorText) {
                [Console]::Out.WriteLine((@{
                    Ok = $ok
                    Data = $data
                    Error = $errorText
                } | ConvertTo-Json -Compress -Depth 12))
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
                            Write-Response $true @{
                                Dolby = Read-DolbyState
                                Noise = Read-NoiseState
                            } ''
                        }
                        'exit' {
                            Write-Response $true @{} ''
                            return
                        }
                        default {
                            throw "Unknown sound helper action: $($request.Action)"
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

        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start Windows PowerShell.");
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
                return;

            lock (SoundHelperErrorLock)
                SoundHelperErrors.AppendLine(args.Data);
        };
        process.BeginErrorReadLine();
        SoundHelperProcess = process;
        return process;
    }

    private static string GetAndClearSoundHelperErrors()
    {
        lock (SoundHelperErrorLock)
        {
            var error = NormalizePowerShellError(
                SoundHelperErrors.ToString());
            SoundHelperErrors.Clear();
            return error;
        }
    }

    private static void StopSoundHelper()
    {
        var process = SoundHelperProcess;
        SoundHelperProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine(
                        JsonSerializer.Serialize(new { Action = "exit" }));
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
            process.Dispose();
        }
    }

    private static string RunWindowsPowerShell(
        string script,
        bool acceptOutputOnFailure = false,
        string operation = "Dolby control",
        int timeoutMilliseconds = 15000)
    {
        PowerShellLock.Wait();
        Process? process = null;
        ChildProcessJob? job = null;
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

        try
        {
            job = new ChildProcessJob();
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start Windows PowerShell.");
            try
            {
                job.Assign(process);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
            lock (ActiveProcessLock)
            {
                ActivePowerShellProcess = process;
                ActivePowerShellJob = job;
            }

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
            if (string.IsNullOrWhiteSpace(output) ||
                (process.ExitCode != 0 && !acceptOutputOnFailure))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"{operation} returned no data."
                        : error);
            }

            return output;
        }
        finally
        {
            lock (ActiveProcessLock)
            {
                if (ReferenceEquals(ActivePowerShellProcess, process))
                {
                    ActivePowerShellProcess = null;
                    ActivePowerShellJob = null;
                }
            }
            job?.Dispose();
            process?.Dispose();
            PowerShellLock.Release();
        }
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
                : error;
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

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
