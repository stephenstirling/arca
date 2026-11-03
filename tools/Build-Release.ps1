<#
  Builds the Arca release files: publish, sign (optional), zip, checksums.
  Used by the release workflow and runnable on your own PC for a dry run.

  Output in -OutDir:
    Arca-<Version>-win-x64.zip
    SHA256SUMS.txt
    Arca-signing.cer        (only when a certificate is given)

  Signing: pass -PfxPath and set the ARCA_PFX_PASSWORD environment variable.
  Without -PfxPath the build is left unsigned.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $PfxPath,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$name = "Arca-$Version-win-x64"

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$stage = Join-Path $OutDir $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "== Publishing $name"
dotnet publish (Join-Path $repo 'src/Arca') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Debug symbols are not needed to run Arca.
Get-ChildItem $stage -Filter *.pdb -File | Remove-Item

$ours = @(Get-ChildItem $stage -File | Where-Object { $_.Name -like 'Arca*.exe' -or $_.Name -like 'Arca*.dll' })
if (-not ($ours | Where-Object Name -eq 'Arca.exe')) { throw 'Arca.exe was not published.' }

$cerPath = $null
if ($PfxPath) {
    if (-not $env:ARCA_PFX_PASSWORD) { throw 'Set ARCA_PFX_PASSWORD to the certificate password.' }

    $signtool = @(
        Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
        Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
    ) | Sort-Object { [version]($_.FullName -replace '.*\\bin\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (needs the Windows SDK).' }
    Write-Host "== Signing with $($signtool.FullName)"

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $PfxPath, $env:ARCA_PFX_PASSWORD
    Write-Host "Certificate: $($cert.Subject)  thumbprint $($cert.Thumbprint)"
    if ($cert.NotAfter -lt (Get-Date)) { throw 'The signing certificate has expired.' }

    foreach ($file in $ours) {
        $signed = $false
        foreach ($try in 1..3) {   # the timestamp server is a free public service and sometimes hiccups
            & $signtool.FullName sign /fd SHA256 /f $PfxPath /p $env:ARCA_PFX_PASSWORD `
                /tr $TimestampUrl /td SHA256 /d 'Arca' $file.FullName
            if ($LASTEXITCODE -eq 0) { $signed = $true; break }
            Start-Sleep -Seconds (5 * $try)
        }
        if (-not $signed) { throw "Signing $($file.Name) failed." }
    }

    # The certificate is self-signed, so Windows reports it as "not trusted". That is expected.
    # What we check: signed by our certificate, and carries a timestamp.
    foreach ($file in $ours) {
        $sig = Get-AuthenticodeSignature $file.FullName
        if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
            throw "$($file.Name) is not signed by the release certificate."
        }
        if (-not $sig.TimeStamperCertificate) { throw "$($file.Name) has no timestamp." }
        Write-Host "Signed and timestamped: $($file.Name)"
    }

    $cerPath = Join-Path $OutDir 'Arca-signing.cer'
    [IO.File]::WriteAllBytes($cerPath, $cert.Export('Cert'))
} else {
    Write-Host '== No certificate given: leaving the build unsigned'
}

Write-Host '== Zipping'
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Everything goes inside one "Arca-<version>-win-x64" folder, so unzipping doesn't scatter files.
# Entry names are written with "/" (what the zip format wants) whichever PowerShell runs this.
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = $name + '/' + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }

Write-Host '== Checksums'
$lines = foreach ($f in @($zip, $cerPath) | Where-Object { $_ }) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path $f -Leaf)"
}
# Plain LF text without a BOM, so `sha256sum -c` also reads it.
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
$lines

Remove-Item $stage -Recurse -Force
Write-Host "== Done: $OutDir"
