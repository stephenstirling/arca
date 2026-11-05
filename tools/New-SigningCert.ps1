<#
  Makes Arca's self-signed code-signing certificate. Run this ONCE, on your own PC.

  It creates, outside the project (never committed):
    %USERPROFILE%\ArcaSigning\Arca-signing.pfx   the PRIVATE key + certificate, protected by a random password
    %USERPROFILE%\ArcaSigning\Arca-signing.cer   the PUBLIC certificate (safe to share; goes in the releases)
    Documents\Arca-signing-password.txt           the password (move it into your password manager, then delete it)

  The password is never shown on screen. The certificate is NOT installed on this PC or marked as trusted.

  GitHub secrets, two ways:
    -SetSecrets   sends both to GitHub for you with the GitHub CLI (`gh`), nothing printed or pasted
    (default)     walks you through pasting them on the GitHub website
#>
param(
    [switch] $SetSecrets,
    [string] $Repo = 'stephenstirling/arca'
)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'ArcaSigning'
$pfx = Join-Path $dir 'Arca-signing.pfx'
$cer = Join-Path $dir 'Arca-signing.cer'
$pwFile = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Arca-signing-password.txt'
if (Test-Path $pfx) { throw "$pfx already exists. Keep it - making a new certificate changes Arca's signing identity. Move it away first if you really want a new one." }
if (Test-Path $pwFile) { throw "$pwFile already exists. Move it into your password manager (or rename it) first." }
New-Item -ItemType Directory -Force $dir | Out-Null

# A random 28-character password (letters and digits only, so it pastes cleanly).
$chars = [char[]]([char]'a'..[char]'z' + [char]'A'..[char]'Z' + [char]'0'..[char]'9')
$bytes = New-Object byte[] 28
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
$secure = ConvertTo-SecureString $password -AsPlainText -Force

# Made in the current user's store only to get an exportable key; removed again below.
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=Arca, O=Stephen Stirling' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(10) `
    -CertStoreLocation Cert:\CurrentUser\My
try {
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
} finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
}
# Written without a trailing newline, so it can be pasted or sent as-is.
[IO.File]::WriteAllText($pwFile, $password, (New-Object Text.UTF8Encoding $false))

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx))

Write-Host ''
Write-Host 'Certificate made.' -ForegroundColor Green
Write-Host "  Files:       $dir"
Write-Host "  Password in: $pwFile   <- move it into your password manager, then delete the file"
Write-Host "  Thumbprint:  $($cert.Thumbprint)    <- public; goes in the README"
Write-Host "  .cer SHA256: $((Get-FileHash $cer -Algorithm SHA256).Hash)"
Write-Host ''

if ($SetSecrets) {
    # Values go to `gh` through a temporary file on stdin: not on the command line, not on screen.
    $tmp = Join-Path $env:TEMP ('arca-secret-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($tmp, $b64, (New-Object Text.UTF8Encoding $false))
        cmd /c "gh secret set ARCA_SIGNING_PFX_BASE64 -R $Repo < `"$tmp`""
        if ($LASTEXITCODE -ne 0) { throw 'Setting ARCA_SIGNING_PFX_BASE64 failed.' }
        cmd /c "gh secret set ARCA_SIGNING_PFX_PASSWORD -R $Repo < `"$pwFile`""
        if ($LASTEXITCODE -ne 0) { throw 'Setting ARCA_SIGNING_PFX_PASSWORD failed.' }
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Both GitHub secrets are set.' -ForegroundColor Green
} else {
    Write-Host 'Now the two GitHub secrets. Open this page in your browser first:'
    Write-Host "  https://github.com/$Repo/settings/secrets/actions/new" -ForegroundColor Cyan
    Write-Host ''
    Set-Clipboard -Value $b64
    Write-Host 'SECRET 1 is on your clipboard.' -ForegroundColor Yellow
    Write-Host '  Name:   ARCA_SIGNING_PFX_BASE64'
    Write-Host '  Secret: press Ctrl+V   (a very long block of letters - that is right)'
    Write-Host '  Then click "Add secret".'
    Read-Host 'Press Enter here once SECRET 1 is saved on GitHub' | Out-Null

    Set-Clipboard -Value $password
    Write-Host ''
    Write-Host 'SECRET 2 is on your clipboard.' -ForegroundColor Yellow
    Write-Host '  Click "New repository secret" again.'
    Write-Host '  Name:   ARCA_SIGNING_PFX_PASSWORD'
    Write-Host '  Secret: press Ctrl+V'
    Write-Host '  Then click "Add secret".'
    Read-Host 'Press Enter here once SECRET 2 is saved on GitHub' | Out-Null
    Set-Clipboard -Value ' '   # don't leave the password sitting on the clipboard
}

Write-Host ''
Write-Host 'Done. Back up Arca-signing.pfx and the password file somewhere safe and private' -ForegroundColor Green
Write-Host '(GitHub never shows a secret again, so these are the only copy of the key).'
