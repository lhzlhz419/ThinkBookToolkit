using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal enum DisplayBackend { ZeroTouch, DirectHpd, Think, DirectThink, Spectrum, DirectSpectrum }

internal static class DisplayExtrasTransport
{
    // These .NET Framework clients load native siblings. Keep the helper's working
    // directory and managed assembly in the same vendor version directory.
    private const string Helper = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        try {
            $dll = $env:TBT_DISPLAY_LIBRARY
            [Environment]::CurrentDirectory = [IO.Path]::GetDirectoryName($dll)
            [void][Reflection.Assembly]::LoadFrom($dll)
            if ($env:TBT_DISPLAY_BACKEND -eq 'DirectHpd') {
                $hpd = [SmartSenseHsaRpcClient.HumanPresenceDetection]::new()
                try {
                    if ($env:TBT_DISPLAY_COMMAND -eq 'Get-Settings') {
                        $s = $hpd.GetAllSetting()
                        $reply = @{ errorCode=0; capacity=$s.Capacity; capability=[int]$hpd.Capability;
                            approachEnabled=$s.ApproachEnabled; presenceLeaveEnabled=$s.PresenceLeaveEnabled;
                            videoAutoPauseResumeEnabled=$s.VideoAutoPauseResumeEnabled; faceIdRegistered=$s.FaceIdRegistered;
                            sensorType=$(if ($null -ne $s.SensorType) { [int]$s.SensorType }) }
                    } else {
                        $setting = ($env:TBT_DISPLAY_PAYLOAD | ConvertFrom-Json).SettingList.PSObject.Properties | Select-Object -First 1
                        $enabled = [bool]::Parse($setting.Value)
                        switch ($setting.Name) {
                            'SetApproachEnabled' { $hpd.SetApproachEnabled($enabled) }
                            'SetPresenceLeaveEnabled' { $hpd.SetPresenceLeaveEnabled($enabled) }
                            'SetVideoAutoPauseResume' { $hpd.SetVideoAutoPauseResume($enabled) }
                            default { throw 'Unknown HPD setting.' }
                        }
                        $reply = @{ errorCode=0 }
                    }
                    [Console]::WriteLine(($reply | ConvertTo-Json -Compress))
                } finally { $hpd.Dispose() }
            } elseif ($env:TBT_DISPLAY_BACKEND -eq 'DirectThink') {
                $handler = [ThinkSmartSenseAddin.ContractHandlers.HPDCommand]
                if ($env:TBT_DISPLAY_COMMAND -eq 'Get-HPDSettings') { $r = $handler::GetHandler('', $null) }
                elseif ($env:TBT_DISPLAY_COMMAND -eq 'Set-HPDSetting') { $r = $handler::SetHandler($env:TBT_DISPLAY_PAYLOAD, $null) }
                else { throw 'Unknown Think HPD command.' }
                if ($null -eq $r -or $r.ErrorCode -ne 0) { throw "Think HPD error: $($r.ErrorCode) $($r.ErrorDesc)" }
                if ($env:TBT_DISPLAY_COMMAND -eq 'Get-HPDSettings') {
                    # The legacy handler substitutes False for absent driver values.
                    $data = $r.Payload | ConvertFrom-Json
                    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Services\SmartSense\Parameters\HPD')
                    try {
                        $data.SettingList = @(if ($null -ne $key) {
                            $version = $key.GetValue('Version')
                            foreach ($row in $data.SettingList) {
                                if ($row.key -notin 'Version','Capability','Approach','PresentLeave','FacialFeatureRegistered') { continue }
                                $value = $key.GetValue($row.key)
                                if ($value -isnot [int] -or $version -isnot [int]) { continue }
                                if ($version -lt 0x02000100 -and $row.key -ne 'Version' -and $value -notin 0,1) { continue }
                                $row
                            }
                        })
                        [Console]::WriteLine(($data | ConvertTo-Json -Depth 8 -Compress))
                    } finally { if ($null -ne $key) { $key.Dispose() } }
                } else { [Console]::WriteLine($r.Payload) }
            } else {
                $client = [Lenovo.VantageService.Utilities.Messaging.RpcClientWrapper]::new()
                $r = $client.SendRequest($env:TBT_DISPLAY_CONTRACT, $env:TBT_DISPLAY_COMMAND, [string]$env:TBT_DISPLAY_PAYLOAD)
                if ($null -eq $r) { throw 'Empty Vantage host response.' }
                if ($r.ErrorCode -ne 0) { throw "Vantage host $($r.ErrorCode): $($r.ErrorDesc)" }
                [Console]::WriteLine($r.Payload)
            }
        } catch {
            $ex = $_.Exception.GetBaseException()
            if ($ex -is [ComponentModel.Win32Exception]) { [Console]::Error.WriteLine("Win32 $($ex.NativeErrorCode): $($ex.Message)") }
            else { [Console]::Error.WriteLine($ex.Message) }
            exit 1
        }
        """;

    private static (string Addin, string File) Component(DisplayBackend backend) => backend switch
    {
        DisplayBackend.DirectHpd => ("SmartInteractAddin", "Lenovo.Vantage.SmartSenseRpcClient.dll"),
        DisplayBackend.DirectThink => ("ThinkSmartSenseAddin", "ThinkSmartSenseAddin.dll"),
        DisplayBackend.DirectSpectrum => ("LenovoDisplayAddin", "Spectrum_CoreWrap.dll"),
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    private static string? HostLibrary() => LenovoVantageAddinLocator.FindLatestFileInRoots(
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Lenovo", "VantageService")],
        "Lenovo.VantageService.Utilities.dll");

    internal static bool PreferLocal(DisplayBackend backend)
    {
        var (addin, file) = Component(backend);
        return LenovoVantageAddinLocator.FindDependencyFile(addin, file) is not null;
    }

    internal static bool HasHost() => HostLibrary() is not null;

    internal static async Task<JsonObject> CallAsync(DisplayBackend backend, string contract, string command, JsonObject? payload)
    {
        string? library;
        if (backend is DisplayBackend.DirectHpd or DisplayBackend.DirectThink or DisplayBackend.DirectSpectrum)
        {
            var (addin, file) = Component(backend);
            library = LenovoVantageAddinLocator.FindLatestFile(addin, file);
        }
        else
        {
            var addin = backend switch { DisplayBackend.Think => "ThinkSmartSenseAddin",
                DisplayBackend.Spectrum => "LenovoDisplayAddin", _ => "SmartInteractAddin" };
            library = LenovoVantageAddinLocator.FindDependencyFile(addin, "Lenovo.VantageService.Utilities.dll") ?? HostLibrary();
        }
        if (library is null) throw new FileNotFoundException($"未找到 {backend} 客户端组件 / Missing {backend} client.");
        if (backend == DisplayBackend.DirectSpectrum)
            return await SpectrumDisplayWorker.CallAsync(library, command, payload).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = Path.GetDirectoryName(library)!,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Helper)) })
            start.ArgumentList.Add(arg);
        start.Environment["TBT_DISPLAY_LIBRARY"] = library;
        start.Environment["TBT_DISPLAY_BACKEND"] = backend.ToString();
        start.Environment["TBT_DISPLAY_CONTRACT"] = contract;
        start.Environment["TBT_DISPLAY_COMMAND"] = command;
        start.Environment["TBT_DISPLAY_PAYLOAD"] = payload?.ToJsonString() ?? "";
        var result = await BoundedProcess.RunAsync(start, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException($"{backend}/{command} ({Path.GetFileName(start.WorkingDirectory)}): {result.Error.Trim()}");
        return DecodeReply(backend, command, result.Output);
    }

    internal static JsonObject DecodeReply(DisplayBackend backend, string command, string output)
    {
        var payload = JsonNode.Parse(output);
        // ZeroTouch's hosted Set-Settings returns a JSON boolean, unlike the Think setter.
        if (backend == DisplayBackend.ZeroTouch && command == "Set-Settings" &&
            payload is JsonValue value && value.TryGetValue<bool>(out var applied))
            return new JsonObject { ["errorCode"] = applied ? 0 : -1 };
        return payload as JsonObject ?? throw new InvalidDataException($"Invalid {backend}/{command} response.");
    }
}
