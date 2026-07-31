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
$fanBackendProject = Join-Path $projectRoot "src\ThinkBookToolkit.FanBackend.Wmi\ThinkBookToolkit.FanBackend.Wmi.csproj"

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

    $fanBackendVersionOutput = dotnet msbuild $fanBackendProject -nologo -getProperty:FileVersion
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $fanBackendVersionText = ($fanBackendVersionOutput | Out-String).Trim()
    $fanBackendVersion = if ($fanBackendVersionText.StartsWith("{")) {
        ($fanBackendVersionText | ConvertFrom-Json).Properties.FileVersion
    }
    else {
        $fanBackendVersionText
    }
    if ([string]::IsNullOrWhiteSpace($fanBackendVersion)) {
        throw "Unable to resolve the fan backend file version."
    }

    $publishKind = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
    $releaseOutput = Join-Path $projectRoot "dist\v$version"
    $output = Join-Path $releaseOutput "ThinkBookToolkit-$version-win-x64-$publishKind"
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
    if ($Configuration -eq "Release") {
        $publishArguments += "-p:DebugType=None"
        $publishArguments += "-p:DebugSymbols=false"
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

    $archive = Join-Path $releaseOutput "ThinkBookToolkit-$version-win-x64-$publishKind.zip"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive `
        -Path (Join-Path $output "*") `
        -DestinationPath $archive `
        -CompressionLevel Optimal
    Write-Host "Portable archive: $archive"

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
        $installerOutput = $releaseOutput
        & $compiler `
            "/DAppVersion=$version" `
            "/DSourceDir=$output" `
            "/DOutputDir=$installerOutput" `
            "/DChineseMessagesFile=$translation" `
            "/DFanBackendFileVersion=$fanBackendVersion" `
            $installerScript
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "Installer output: $(Join-Path $installerOutput "ThinkBookToolkit-$version-Setup.exe")"
    }

    $releaseAssets = @($archive)
    if ($Installer) {
        $releaseAssets += Join-Path $releaseOutput "ThinkBookToolkit-$version-Setup.exe"
    }
    $checksumPath = Join-Path $releaseOutput "SHA256SUMS-v$version.txt"
    $checksumLines = $releaseAssets | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash, (Split-Path -Leaf $_)
    }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ascii
    Write-Host "Checksums: $checksumPath"
    exit 0
}

dotnet build $solution -c $Configuration -m:1 --disable-build-servers
exit $LASTEXITCODE
