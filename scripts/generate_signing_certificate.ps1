param(
    [string]$Password = $env:TBT_CERT_PASSWORD,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$signingDirectory = Join-Path $projectRoot "signing"
$pfxPath = Join-Path $signingDirectory "ThinkBookToolkit.pfx"
$cerPath = Join-Path $signingDirectory "ThinkBookToolkit.cer"
$subject = "CN=ThinkBookToolkit"

if ([string]::IsNullOrWhiteSpace($Password)) {
    throw "A password is required. Pass -Password or set TBT_CERT_PASSWORD."
}

New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
if (-not $Force -and
    (Test-Path -LiteralPath $pfxPath) -and
    (Test-Path -LiteralPath $cerPath)) {
    Write-Host "Signing certificate already exists: $cerPath"
    exit 0
}

$certificate = $null
try {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "ThinkBook Toolkit UIAccess Code Signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )
    $securePassword = ConvertTo-SecureString `
        -String $Password `
        -Force `
        -AsPlainText
    Export-PfxCertificate `
        -Cert $certificate `
        -FilePath $pfxPath `
        -Password $securePassword | Out-Null
    Export-Certificate `
        -Cert $certificate `
        -FilePath $cerPath | Out-Null
    Write-Host "Created public certificate: $cerPath"
    Write-Host "Created private certificate: $pfxPath"
    Write-Warning "Never commit, publish, or distribute the PFX or its password."
}
finally {
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath $certificate.PSPath -Force -ErrorAction SilentlyContinue
    }
}
