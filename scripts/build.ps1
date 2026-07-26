param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish,
    [switch]$IncludeLocalProprietaryDependencies
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot "ThinkBookToolkit.sln"
$project = Join-Path $projectRoot "src\ThinkBookToolkit\ThinkBookToolkit.csproj"

if ($Publish) {
    $versionOutput = dotnet msbuild $project -nologo -getProperty:Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $versionText = ($versionOutput | Out-String).Trim()
    $version = if ($versionText.StartsWith("{")) {
        ($versionText | ConvertFrom-Json).Properties.Version
    }
    else {
        $versionText
    }
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Unable to resolve the application version."
    }

    $output = Join-Path $projectRoot "dist\ThinkBookToolkit-$version-win-x64"
    $publishArguments = @(
        "publish",
        $project,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $output,
        "-m:1",
        "--disable-build-servers"
    )
    if (-not $IncludeLocalProprietaryDependencies) {
        $cleanDependencies = Join-Path $projectRoot ".tmp\public-release-no-proprietary-dependencies"
        $publishArguments += "-p:ExternalDependenciesRoot=$cleanDependencies"
    }

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Publish output: $output"
    if (-not $IncludeLocalProprietaryDependencies) {
        Write-Host "Public release mode: proprietary Lenovo dependencies were excluded."
    }
    exit 0
}

dotnet build $solution -c $Configuration -m:1 --disable-build-servers
exit $LASTEXITCODE
