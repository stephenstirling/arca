<#
  Checks integra-app.json against the files just built, then writes the copy that goes on the release.
  Fails (throws) if anything does not line up, so a wrong release is never published.

  Checks: the repo, the zip name against assetPattern, SHA256SUMS.txt exists, the signer thumbprint against the
  certificate used, Arca.exe inside the zip, and that exe's version against the tag.
  The release copy is the same file plus "version", "tag" and "asset" (the root file carries none of these so it never goes stale).
  Its checksum is appended to SHA256SUMS.txt.
#>
param(
    [Parameter(Mandatory)] [string] $Version,      # e.g. 0.1.0
    [Parameter(Mandatory)] [string] $Tag,          # e.g. v0.1.0
    [Parameter(Mandatory)] [string] $Repo,         # e.g. stephenstirling/arca
    [Parameter(Mandatory)] [string] $ReleaseDir,   # holds the zip, Arca-signing.cer, SHA256SUMS.txt
    [string] $Manifest = (Join-Path (Split-Path -Parent $PSScriptRoot) 'integra-app.json')
)

$ErrorActionPreference = 'Stop'
function Fail($msg) { throw "integra-app.json: $msg" }

$m = Get-Content $Manifest -Raw | ConvertFrom-Json
foreach ($field in 'schema','id','name','publisher','repo','assetPattern','checksumAsset','signerThumbprint','exe','minWindows','arch','installDir','dataDir','stopCommand','uninstall') {
    if ($null -eq $m.$field -or "$($m.$field)" -eq '') { Fail "missing '$field'." }
}
if ($m.schema -ne 1) { Fail "schema is $($m.schema), this script knows 1." }
if ($m.PSObject.Properties.Name -contains 'version') { Fail "remove 'version' from the file in the repo; the release workflow adds it." }
if ($m.repo -ne $Repo) { Fail "repo is '$($m.repo)' but this release is from '$Repo'." }
if ($Tag -ne "v$Version") { Fail "tag '$Tag' does not match version '$Version'." }
if ($m.arch -ne 'x64') { Fail "arch is '$($m.arch)', but the build is x64." }
$min = [version]'0.0'
if (-not [version]::TryParse($m.minWindows, [ref]$min)) { Fail "minWindows '$($m.minWindows)' is not a version number." }

# The zip and checksum file.
$zips = @(Get-ChildItem $ReleaseDir -File | Where-Object { $_.Name -like $m.assetPattern })
if ($zips.Count -ne 1) { Fail "assetPattern '$($m.assetPattern)' matches $($zips.Count) files in the release, expected exactly 1." }
$zip = $zips[0]
if ($zip.Name -ne "Arca-$Version-win-x64.zip") { Fail "the zip is '$($zip.Name)', expected 'Arca-$Version-win-x64.zip'." }
$sums = Join-Path $ReleaseDir $m.checksumAsset
if (-not (Test-Path $sums)) { Fail "checksumAsset '$($m.checksumAsset)' is not among the release files." }

# The certificate that actually signed this build.
$cer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (Join-Path $ReleaseDir 'Arca-signing.cer')
if ($m.signerThumbprint -ne $cer.Thumbprint) {
    Fail "signerThumbprint is $($m.signerThumbprint) but the release is signed with $($cer.Thumbprint)."
}

# The exe inside the zip, and its version.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('arca-manifest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -match "^[^/]+/$([regex]::Escape($m.exe))$" } | Select-Object -First 1
        if (-not $entry) { Fail "'$($m.exe)' is not at the top of the zip." }
        $exePath = Join-Path $tmp $m.exe
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $exePath)
    } finally { $archive.Dispose() }
    $exeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
    if ($exeVersion -ne $Version) { Fail "$($m.exe) says version '$exeVersion' in its file properties but the tag is '$Version'." }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# Everything lines up: write the release copy.
$m | Add-Member -NotePropertyName version -NotePropertyValue $Version
$m | Add-Member -NotePropertyName tag -NotePropertyValue $Tag
$m | Add-Member -NotePropertyName asset -NotePropertyValue $zip.Name
$out = Join-Path $ReleaseDir 'integra-app.json'
[IO.File]::WriteAllText($out, (($m | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding $false))

$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
[IO.File]::AppendAllText($sums, "$hash  integra-app.json`n", (New-Object Text.UTF8Encoding $false))
Write-Host "integra-app.json OK for $($zip.Name) (signer $($cer.Thumbprint))"
