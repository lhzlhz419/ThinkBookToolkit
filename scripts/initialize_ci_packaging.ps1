# Shared setup for Actions test installers and official releases.
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_ENV -or -not $env:RUNNER_TEMP) {
    throw 'This script must run in GitHub Actions.'
}
if ([string]::IsNullOrWhiteSpace($env:TBT_CERT_PFX_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:TBT_CERT_PASSWORD)) {
    throw 'Configure TBT_CERT_PFX_BASE64 and TBT_CERT_PASSWORD in the release-signing environment.'
}
$env:TBT_CERT_PATH = Join-Path $env:RUNNER_TEMP 'tbt-signing.pfx'
$env:TBT_CERT_PUBLIC_PATH = Join-Path $env:RUNNER_TEMP 'tbt-signing.cer'
"TBT_CERT_PATH=$env:TBT_CERT_PATH" >> $env:GITHUB_ENV
"TBT_CERT_PUBLIC_PATH=$env:TBT_CERT_PUBLIC_PATH" >> $env:GITHUB_ENV
[IO.File]::WriteAllBytes($env:TBT_CERT_PATH, [Convert]::FromBase64String($env:TBT_CERT_PFX_BASE64))
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $env:TBT_CERT_PATH, $env:TBT_CERT_PASSWORD,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (-not $certificate.HasPrivateKey) { throw 'PFX does not contain a private key.' }
    if ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
        throw 'Signing certificate is outside its validity period.'
    }
    [IO.File]::WriteAllBytes($env:TBT_CERT_PUBLIC_PATH,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
} finally { $certificate.Dispose() }

$compilerCandidates = @(
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'))
if (-not ($compilerCandidates | Where-Object { Test-Path -LiteralPath $_ })) {
    choco install innosetup --version=6.4.3 --yes --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup installation failed.' }
}
