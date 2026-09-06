param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = "Stop"
$workerExecutable = (Resolve-Path -LiteralPath $Executable).Path
$pipeName = "ThinkBookToolkit.GpuMonitor.Test." + [Guid]::NewGuid().ToString("N")
$pipeSecurity = [IO.Pipes.PipeSecurity]::new()
$pipeSecurity.AddAccessRule([IO.Pipes.PipeAccessRule]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent().User,
    [IO.Pipes.PipeAccessRights]::FullControl,
    [Security.AccessControl.AccessControlType]::Allow))
$pipeSecurity.AddAccessRule([IO.Pipes.PipeAccessRule]::new(
    [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::WorldSid, $null),
    [IO.Pipes.PipeAccessRights]::ReadWrite,
    [Security.AccessControl.AccessControlType]::Allow))
$pipe = [IO.Pipes.NamedPipeServerStreamAcl]::Create(
    $pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous,
    1024, 1024, $pipeSecurity, [IO.HandleInheritability]::None,
    [IO.Pipes.PipeAccessRights]0)
$workerProcess = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new($workerExecutable, "--gpu-worker " + $pipeName)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $workerProcess = [Diagnostics.Process]::Start($start)
    if (-not $pipe.WaitForConnectionAsync().Wait(10000)) {
        throw "GPU worker did not connect. PID: $($workerProcess.Id)"
    }
    # EXIT does not initialize NVAPI/LHM or read or change GPU hardware.
    $writer = [IO.StreamWriter]::new($pipe)
    $writer.WriteLine("EXIT")
    $writer.Flush()
    if (-not $workerProcess.WaitForExit(10000)) {
        throw "GPU worker did not exit after EXIT."
    }
    if ($workerProcess.ExitCode -ne 0) {
        throw "GPU worker failed: $($workerProcess.ExitCode) $($workerProcess.StandardError.ReadToEnd())"
    }
    Write-Host "GPU worker CreateProcess, pipe handshake, and clean EXIT passed."
} finally {
    if ($null -ne $workerProcess) {
        if (-not $workerProcess.HasExited) { $workerProcess.Kill() }
        $workerProcess.Dispose()
    }
    $pipe.Dispose()
}
