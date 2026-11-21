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
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $initialized = $false
        $monitor = $null
        while ($null -ne ($line = [Console]::ReadLine())) {
            try {
                if (!$initialized) {
                    $dir = [IO.Path]::GetDirectoryName($env:TBT_SPECTRUM_LIBRARY)
                    [Environment]::CurrentDirectory = $dir
                    foreach ($name in 'Newtonsoft.Json.dll','Spectrum_Contract.dll','Spectrum_CoreWrap.dll','LenovoDisplayAddin.dll') {
                        [void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($dir,$name))
                    }
                    $core = [Spectrum_CoreWrap.Spectrum_CoreAccessor]
                    $initialized = $true
                }
                $request = $line | ConvertFrom-Json
                $capable = $core::IsCapable()
                if ($request.command -eq 'Get-Capability') {
                    $data = @{CapabilityList=@{Items=@(@{key='BurningProtectionControl';value=$capable.ToString()})}}
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
                if ($capable -and $request.command -eq 'Get-DisplaySettings' -and $data.SettingList.Items.Count -gt 0) {
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
            if (VendorMonitorActive())
                throw new InvalidOperationException("Lenovo's display add-in is running in this session. Use the hosted Spectrum backend.");
            if (_process is null || _process.HasExited || !string.Equals(library, _library, StringComparison.OrdinalIgnoreCase))
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
                _job = new ChildProcessJob();
                _process = Process.Start(start) ?? throw new IOException("Spectrum worker did not start.");
                _job.Assign(_process);
                _process.BeginErrorReadLine();
                _library = library;
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
