param(
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $projectRoot "..\ThinkBookToolkit.Dependencies\IntelPower"
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$required = @("IntelMSR.bin", "IntelMCHBAR.bin")
$missingModules = $required | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $Destination $_))
}
if ($missingModules.Count -gt 0) {
    $headers = @{ "User-Agent" = "ThinkBookToolkit-build" }
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/namazso/PawnIO.Modules/releases/latest" `
        -Headers $headers
    $temporary = Join-Path ([IO.Path]::GetTempPath()) `
        ("ThinkBookToolkit-IntelPower-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        foreach ($name in $missingModules) {
            $direct = $release.assets | Where-Object {
                $_.name -ieq $name
            } | Select-Object -First 1
            if ($direct) {
                Invoke-WebRequest `
                    -Uri $direct.browser_download_url `
                    -OutFile (Join-Path $Destination $name) `
                    -Headers $headers
                continue
            }
            $found = $false
            foreach ($asset in $release.assets | Where-Object {
                         $_.name -match '\.zip$'
                     }) {
                $archive = Join-Path $temporary $asset.name
                Invoke-WebRequest -Uri $asset.browser_download_url `
                    -OutFile $archive -Headers $headers
                $extract = Join-Path $temporary `
                    ([IO.Path]::GetFileNameWithoutExtension($asset.name))
                Expand-Archive -LiteralPath $archive `
                    -DestinationPath $extract -Force
                $module = Get-ChildItem -LiteralPath $extract -Recurse `
                    -File -Filter $name | Select-Object -First 1
                if ($module) {
                    Copy-Item -LiteralPath $module.FullName `
                        -Destination (Join-Path $Destination $name) -Force
                    $found = $true
                    break
                }
            }
            if (-not $found) {
                throw "$name was not found in PawnIO.Modules release $($release.tag_name)."
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $temporary -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}

$inpOut = Join-Path $Destination "InpOutx64.dll"
if (-not (Test-Path -LiteralPath $inpOut)) {
    Invoke-WebRequest `
        -Uri "https://raw.githubusercontent.com/cocafe/physmem/inpoutx64/inpoutx64.dll" `
        -OutFile $inpOut `
        -Headers @{ "User-Agent" = "ThinkBookToolkit-build" }
}

foreach ($name in @($required) + "InpOutx64.dll") {
    $path = Join-Path $Destination $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Intel power dependency was not downloaded: $name"
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Write-Host "IntelPower dependency: $name SHA256=$hash"
}
