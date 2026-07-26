param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish,
    [switch]$Installer,
    [switch]$SelfContained,
    [switch]$IncludeLocalProprietaryDependencies
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot "ThinkBookToolkit.sln"
$project = Join-Path $projectRoot "src\ThinkBookToolkit\ThinkBookToolkit.csproj"

if ($Installer -and $SelfContained) {
    throw "The online installer requires a framework-dependent publish. Do not combine -Installer and -SelfContained."
}

if ($Publish -or $Installer) {
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

    $publishKind = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
    $output = Join-Path $projectRoot "dist\ThinkBookToolkit-$version-win-x64-$publishKind"
    $publishArguments = @(
        "publish",
        $project,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
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

    if ($Installer) {
        $compilerCandidates = @(
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )
        $compiler = $compilerCandidates |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($compiler)) {
            throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run this command again."
        }

        $translationDirectory = Join-Path $projectRoot ".tmp"
        $translation = Join-Path $translationDirectory "ChineseSimplified.isl"
        $translationHash = "6753BE2C5E2740D859900FD902824DB2EC568DA5C5B52486524C9762D778B0B0"
        $translationUrl = "https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl"
        $translationIsValid =
            (Test-Path -LiteralPath $translation) -and
            ((Get-FileHash -LiteralPath $translation -Algorithm SHA256).Hash -eq $translationHash)
        if (-not $translationIsValid) {
            New-Item -ItemType Directory -Path $translationDirectory -Force | Out-Null
            Invoke-WebRequest -Uri $translationUrl -OutFile $translation
            if ((Get-FileHash -LiteralPath $translation -Algorithm SHA256).Hash -ne $translationHash) {
                throw "The downloaded Inno Setup Chinese translation did not match the expected SHA-256."
            }
        }

        $installerScript = Join-Path $projectRoot "installer\ThinkBookToolkit.iss"
        $installerOutput = Join-Path $projectRoot "dist"
        & $compiler `
            "/DAppVersion=$version" `
            "/DSourceDir=$output" `
            "/DOutputDir=$installerOutput" `
            "/DChineseMessagesFile=$translation" `
            $installerScript
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "Installer output: $(Join-Path $installerOutput "ThinkBookToolkit-$version-Setup.exe")"
    }
    exit 0
}

dotnet build $solution -c $Configuration -m:1 --disable-build-servers
exit $LASTEXITCODE
