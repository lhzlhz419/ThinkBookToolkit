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
$nvApiWrapperProject = Join-Path $projectRoot "external\NvAPIWrapper\NvAPIWrapper\NvAPIWrapper.csproj"

$amdPowerHelperProject = Join-Path $projectRoot "src\ThinkBookToolkit.AmdPowerHelper\ThinkBookToolkit.AmdPowerHelper.csproj"

$zenStatesProject = Join-Path $projectRoot "external\ZenStates-Core\ZenStates-Core.csproj"

if (-not (Test-Path -LiteralPath $nvApiWrapperProject)) {
    throw "NvAPIWrapper source dependency is missing. Run: git submodule update --init --recursive"
}

if (-not (Test-Path -LiteralPath $amdPowerHelperProject)) {
    throw "AMD Power Helper project is missing."
}

if (-not (Test-Path -LiteralPath $zenStatesProject)) {
    throw "ZenStates-Core source dependency is missing. Run: git submodule update --init --recursive"
}

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
    $amdHelperOutput = Join-Path $projectRoot ".tmp\amd-power-helper-$Configuration-$publishKind"
    if (Test-Path -LiteralPath $amdHelperOutput) {
        Remove-Item -LiteralPath $amdHelperOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $amdHelperOutput -Force | Out-Null
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
    $localDependencies = Join-Path $projectRoot "..\ThinkBookToolkit.Dependencies"
    $intelPowerSource = Join-Path $localDependencies "IntelPower"
    $intelPowerDownloader = Join-Path $PSScriptRoot `
        "download_intel_power_dependencies.ps1"
    & $intelPowerDownloader -Destination $intelPowerSource
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $IncludeLocalProprietaryDependencies) {
        $cleanDependencies = Join-Path $projectRoot ".tmp\public-release-no-proprietary-dependencies"
        if (Test-Path -LiteralPath $cleanDependencies) {
            Remove-Item -LiteralPath $cleanDependencies -Recurse -Force
        }
        New-Item -ItemType Directory -Path $cleanDependencies -Force | Out-Null
        if (Test-Path -LiteralPath $intelPowerSource) {
            Copy-Item -LiteralPath $intelPowerSource `
                -Destination (Join-Path $cleanDependencies "IntelPower") `
                -Recurse -Force
        }
        $publishArguments += "-p:ExternalDependenciesRoot=$cleanDependencies"
    }
    if ($Configuration -eq "Release") {
        $publishArguments += "-p:DebugType=None"
        $publishArguments += "-p:DebugSymbols=false"
    }

    Write-Host "Publishing AMD Power Helper..."

    $amdHelperPublishArguments = @(
        "publish",
        $amdPowerHelperProject,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
        "-o", $amdHelperOutput,
        "-m:1",
        "--disable-build-servers"
    )

    if ($Configuration -eq "Release") {
        $amdHelperPublishArguments += "-p:DebugType=None"
        $amdHelperPublishArguments += "-p:DebugSymbols=false"
    }

    & dotnet @amdHelperPublishArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $inpOutX64 = Join-Path $intelPowerSource "InpOutx64.dll"

    if (-not (Test-Path -LiteralPath $inpOutX64)) {
        throw "InpOutx64.dll is missing: $inpOutX64"
    }

    Copy-Item `
        -LiteralPath $inpOutX64 `
        -Destination (Join-Path $amdHelperOutput "inpoutx64.dll") `
        -Force

    Write-Host "Copied inpoutx64.dll for AMD ZenStates helper."

    $requiredAmdHelperFiles = @(
        "ThinkBookToolkit.AmdPowerHelper.exe",
        "ThinkBookToolkit.AmdPowerHelper.dll",
        "ThinkBookToolkit.AmdPowerHelper.deps.json",
        "ThinkBookToolkit.AmdPowerHelper.runtimeconfig.json",
        "ZenStates-Core.dll",
        "inpoutx64.dll"
    )

    foreach ($file in $requiredAmdHelperFiles) {
        $path = Join-Path $amdHelperOutput $file

        if (-not (Test-Path -LiteralPath $path)) {
            throw "AMD Power Helper publish is incomplete. Missing: $file"
        }
    }

    $zenStatesLicense = Join-Path $amdHelperOutput "LICENSE"

    if (Test-Path -LiteralPath $zenStatesLicense) {
        $renamedZenStatesLicense = Join-Path $amdHelperOutput `
            "ZenStates-Core.LICENSE.txt"

        if (Test-Path -LiteralPath $renamedZenStatesLicense) {
            Remove-Item -LiteralPath $renamedZenStatesLicense -Force
        }

        Move-Item `
            -LiteralPath $zenStatesLicense `
            -Destination $renamedZenStatesLicense

        Write-Host "Renamed ZenStates-Core LICENSE -> ZenStates-Core.LICENSE.txt"
    }

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Publish output: $output"
    Write-Host "Installing AMD Power Helper into isolated subdirectory..."

    $amdHelperDestination = Join-Path $output "AmdPowerHelper"

    if (Test-Path -LiteralPath $amdHelperDestination) {
        Remove-Item `
            -LiteralPath $amdHelperDestination `
            -Recurse `
            -Force
    }

    New-Item `
        -ItemType Directory `
        -Path $amdHelperDestination `
        -Force | Out-Null

    Copy-Item `
        -Path (Join-Path $amdHelperOutput "*") `
        -Destination $amdHelperDestination `
        -Recurse `
        -Force

    Write-Host "AMD Power Helper output: $amdHelperDestination"
    if (-not $IncludeLocalProprietaryDependencies) {
        Write-Host "Public release mode: proprietary Lenovo dependencies were excluded; redistributable IntelPower dependencies were retained when available."
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
