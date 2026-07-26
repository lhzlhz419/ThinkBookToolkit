param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot "ThinkBookToolkit.sln"
$project = Join-Path $projectRoot "src\ThinkBookToolkit\ThinkBookToolkit.csproj"

if ($Publish) {
    $output = Join-Path $projectRoot "dist\ThinkBookToolkit-win-x64-latest"
    dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $output -m:1 --disable-build-servers
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Publish output: $output"
    exit 0
}

dotnet build $solution -c $Configuration -m:1 --disable-build-servers
exit $LASTEXITCODE
