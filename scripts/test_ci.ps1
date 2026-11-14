# Run after building ThinkBookToolkit.sln in Release configuration.
# These are executable tests, not VSTest/xUnit projects.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testHost = Join-Path $projectRoot 'tests/ThinkBookToolkit.UiSmokeTests/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.UiSmokeTests.exe'
if (-not (Test-Path -LiteralPath $testHost)) {
    throw 'Build ThinkBookToolkit.sln with -c Release before running CI tests.'
}
$logDirectory = Join-Path $projectRoot '.tmp/ci-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$suites = @(
    @{ Name = 'ui-smoke'; Arguments = @() },
    @{ Name = 'plugins'; Arguments = @('--test-plugins') },
    @{ Name = 'nvpcf-isolation'; Arguments = @('--test-nvpcf-isolation') },
    @{ Name = 'gpu-monitor-recovery'; Arguments = @('--test-gpu-monitor-recovery') },
    @{ Name = 'recovery-startup'; Arguments = @('--test-recovery-startup') }
)
$failed = @()
Push-Location $projectRoot
try {
    foreach ($suite in $suites) {
        Write-Host "Running $($suite.Name)..."
        $suiteArguments = $suite.Arguments
        & $testHost @suiteArguments 2>&1 |
            Tee-Object -FilePath (Join-Path $logDirectory "$($suite.Name).log")
        if ($LASTEXITCODE -ne 0) {
            $failed += "$($suite.Name) (exit $LASTEXITCODE)"
        }
    }
    if ($failed.Count -gt 0) {
        throw "Failed test suites: $($failed -join ', ')"
    }
}
finally {
    Pop-Location
}
