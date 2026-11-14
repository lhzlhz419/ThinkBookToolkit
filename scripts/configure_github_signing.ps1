param(
    [string]$Repository = 'lhzlhz419/ThinkBookToolkit',
    [string]$CertificatePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'signing/ThinkBookToolkit.pfx')
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be owner/name.'
}
if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Signing PFX was not found: $CertificatePath"
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'Install GitHub CLI and run gh auth login first.'
}
Write-Host "Configuring fixed signing secrets for $Repository / release-signing."
$securePassword = Read-Host 'PFX password (hidden)' -AsSecureString
$password = [Net.NetworkCredential]::new('', $securePassword).Password
$pfxBase64 = $null
try {
    if ([string]::IsNullOrWhiteSpace($password)) { throw 'A PFX password is required.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Get-Item -LiteralPath $CertificatePath).FullName, $password,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if (-not $certificate.HasPrivateKey) { throw 'PFX does not contain a private key.' }
        if ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
            throw 'Signing certificate is outside its validity period.'
        }
    } finally { $certificate.Dispose() }

    # Do not update protection settings on an existing environment.
    gh api "repos/$Repository/environments/release-signing" --silent 2>$null
    if ($LASTEXITCODE -ne 0) {
        gh api --method PUT "repos/$Repository/environments/release-signing" --silent
        if ($LASTEXITCODE -ne 0) { throw 'Could not create/access the release-signing environment.' }
    }
    $pfxBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Get-Item -LiteralPath $CertificatePath).FullName))
    $pfxBase64 | gh secret set TBT_CERT_PFX_BASE64 --repo $Repository --env release-signing
    if ($LASTEXITCODE -ne 0) { throw 'Could not set TBT_CERT_PFX_BASE64.' }
    $password | gh secret set TBT_CERT_PASSWORD --repo $Repository --env release-signing
    if ($LASTEXITCODE -ne 0) { throw 'Could not set TBT_CERT_PASSWORD. Rerun setup to finish configuration.' }
    Write-Host 'Signing secrets configured. Rerun the failed Actions jobs or push a new commit.'
} finally {
    $password = $null
    $pfxBase64 = $null
    $securePassword.Dispose()
}
