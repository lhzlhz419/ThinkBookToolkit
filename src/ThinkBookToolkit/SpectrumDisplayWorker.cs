using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

// Spectrum's native monitor lives in this process. Keep it alive across page navigation.
internal static class SpectrumDisplayWorker
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static Process? _process;
    private static ChildProcessJob? _job;
    private static string? _library;
    private static bool _vendorMonitor;
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $initialized = $false
        $monitor = $null
        function Get-SpectrumCapability {
            # The add-in merges these machine tag stores from both registry views.
            $tag = 'absent'
            foreach ($path in 'SOFTWARE\Lenovo\ImController\Applicability\Tags',
                    'SOFTWARE\WOW6432Node\Lenovo\ImController\Applicability\Tags',
                    'SOFTWARE\WOW6432Node\Lenovo\VantageService\DeviceTags') {
                $key = $null
                try {
                    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
                    if (!$key) { continue }
                    foreach ($name in $key.GetValueNames()) {
                        if ($name -eq 'System.Profile.OLED' -or $key.GetValue($name) -eq 'System.Profile.OLED') {
                            return @{supported=$true;detail='System.Profile.OLED'}
                        }
                    }
                } catch { $tag = 'unavailable' }
                finally { if ($key) { $key.Dispose() } }
            }
            $panels = [Collections.Generic.List[string]]::new()
            $bios = ''
            $policy = 'unavailable'
            try {
                $entries = $null
                try {
                    $json = [Lenovo.VantageService.Utilities.Addin.AddinEnvironment]::CloudSettings['Spectrum']
                    if ($json) { $entries = ($json | ConvertFrom-Json).WhiteList; $policy = 'cloud' }
                } catch {}
                if ($null -eq $entries) {
                    $manifest = [IO.Path]::Combine([IO.Directory]::GetParent($dir).FullName,'AddinManifest.xml')
                    if ([IO.File]::Exists($manifest)) {
                        $xml = [Xml.XmlDocument]::new()
                        $xml.XmlResolver = $null
                        $xml.Load($manifest)
                        $setting = $xml.SelectSingleNode('/Addin[@name="LenovoDisplayAddin"]/Settings/Setting[@key="Spectrum"]')
                        if ($setting) { $entries = ($setting.InnerText | ConvertFrom-Json).WhiteList; $policy = 'manifest' }
                    }
                }
                [void]$core::GetPanelInfoAndBiosHeader($panels,[ref]$bios)
                $matched = [Collections.Generic.List[string]]::new()
                if ($bios) {
                    foreach ($id in $panels) {
                        if (!$id) { continue }
                        foreach ($item in $entries) {
                            if ($item.PANELID -eq $id -or ($item.EDID -eq $id -and $item.BIOS -contains $bios)) {
                                $matched.Add($id)
                                break
                            }
                        }
                    }
                }
                if ($matched.Count) {
                    [void]$core::SetPanelInfos($matched)
                    return @{supported=$true;detail="$policy panel=$($matched -join ',') BIOS=$bios"}
                }
            } catch { $policy = "$policy ($($_.Exception.GetBaseException().Message))" }
            $native = $core::IsCapable()
            return @{supported=$native;detail="OLED tag=$tag; $policy panel=$($panels -join ',') BIOS=$bios; native=$native"}
        }
        while ($null -ne ($line = [Console]::ReadLine())) {
            try {
                if (!$initialized) {
                    $dir = [IO.Path]::GetDirectoryName($env:TBT_SPECTRUM_LIBRARY)
                    [Environment]::CurrentDirectory = $dir
                    foreach ($name in 'Newtonsoft.Json.dll','Lenovo.VantageService.Utilities.dll','Spectrum_Contract.dll','Spectrum_CoreWrap.dll','LenovoDisplayAddin.dll') {
                        [void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($dir,$name))
                    }
                    $core = [Spectrum_CoreWrap.Spectrum_CoreAccessor]
                    $initialized = $true
                }
                $request = $line | ConvertFrom-Json
                $probe = Get-SpectrumCapability
                $capable = $probe.supported
                if ($request.command -eq 'Get-Capability') {
                    $data = @{detail=$probe.detail;CapabilityList=@{Items=@(@{key='BurningProtectionControl';value=$capable.ToString()})}}
                } elseif (!$capable) { throw 'OLED capability is no longer available.' }
                elseif ($request.command -eq 'Get-DisplaySettings') {
                    $raw = $core::GetDisplaySettings()
                    if ($null -eq $raw.List.Items) { throw 'No readable Spectrum settings.' }
                    $response = [LenovoDisplayAddin.PayloadTypes.Spectrum.DisplaySettingsResponsePayload]::new()
                    $response.List = $raw.List
                    $data = $response.ToJson() | ConvertFrom-Json
                } elseif ($request.command -eq 'Set-DisplaySettings') {
                    $body = [Newtonsoft.Json.JsonConvert]::DeserializeObject([string]$request.payload,
                        [LenovoDisplayAddin.PayloadTypes.Spectrum.DisplaySettingsRequestPayload])
                    if ($null -eq $body.List.Items) { throw 'Missing Spectrum timer list.' }
                    $typed = [Spectrum.Contract.DisplaySettingsRequest]::new()
                    $typed.List = $body.List
                    $result = $core::SetDisplaySettings($typed)
                    $data = @{ErrorCode=$result.ErrorCode}
                } else { throw 'Unknown Spectrum command.' }
                if ($capable -and $env:TBT_VENDOR_MONITOR -ne '1' -and $request.command -eq 'Get-DisplaySettings' -and $data.SettingList.Items.Count -gt 0) {
                    if ($null -eq $monitor) {
                        $run = [Delegate]::CreateDelegate([Action],$core,'StartMonitor')
                        $monitor = [Threading.Tasks.Task]::Factory.StartNew($run,[Threading.CancellationToken]::None,
                            [Threading.Tasks.TaskCreationOptions]::LongRunning,[Threading.Tasks.TaskScheduler]::Default)
                    }
                    if ($null -ne $monitor -and $monitor.IsFaulted) {throw $monitor.Exception.GetBaseException()}
                }
                $reply = @{ok=$true;data=$data}
            } catch { $reply = @{ok=$false;error=$_.Exception.GetBaseException().Message} }
            [Console]::WriteLine(($reply | ConvertTo-Json -Depth 32 -Compress))
        }
        """;

    internal static async Task<JsonObject> CallAsync(string library, string command, JsonObject? payload)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var vendorMonitor = VendorMonitorActive();
            if (_process is null || _process.HasExited || _vendorMonitor != vendorMonitor || !string.Equals(library, _library, StringComparison.OrdinalIgnoreCase))
            {
                Stop();
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    WorkingDirectory = Path.GetDirectoryName(library)!, CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
                };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
                    start.ArgumentList.Add(arg);
                start.Environment["TBT_SPECTRUM_LIBRARY"] = library;
                start.Environment["TBT_VENDOR_MONITOR"] = vendorMonitor ? "1" : "0";
                _job = new ChildProcessJob();
                _process = Process.Start(start) ?? throw new IOException("Spectrum worker did not start.");
                _job.Assign(_process);
                _process.BeginErrorReadLine();
                _library = library;
                _vendorMonitor = vendorMonitor;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var line = new JsonObject { ["command"] = command, ["payload"] = payload?.ToJsonString() }.ToJsonString();
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var output = await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            var reply = JsonNode.Parse(output ?? throw new EndOfStreamException())!.AsObject();
            if (reply["ok"]?.GetValue<bool>() != true) throw new IOException(reply["error"]?.ToString() ?? "Spectrum worker failed.");
            var result = reply["data"]!.AsObject();
            if (command == "Get-Capability" && string.Equals(result["CapabilityList"]?["Items"]?[0]?["value"]?.ToString(), "False", StringComparison.OrdinalIgnoreCase))
                Stop();
            return result;
        }
        catch (Exception ex)
        {
            Stop();
            throw new IOException($"Local Spectrum/{command} ({Path.GetFileName(Path.GetDirectoryName(library))}): {ex.GetBaseException().Message}", ex);
        }
        finally { Gate.Release(); }
    }

    internal static void Shutdown()
    {
        if (!Gate.Wait(0)) return; // The process-owned job also closes on parent exit.
        try { Stop(); } finally { Gate.Release(); }
    }

    internal static bool VendorMonitorActive()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName("LenovoVantage-(LenovoDisplayAddin)");
        try
        {
            foreach (var process in others)
            {
                try { if (process.SessionId == self.SessionId) return true; }
                catch (InvalidOperationException) { } // Already exited.
            }
            return false;
        }
        finally { foreach (var process in others) process.Dispose(); }
    }

    private static void Stop()
    {
        var child = _process;
        _process = null;
        _library = null;
        _job?.Dispose();
        _job = null;
        if (child is null) return;
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { child.Dispose(); }
    }
}
