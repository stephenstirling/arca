<#
  Writes the release notes: the commit titles since the previous release tag, then how to verify the download.
  Run from inside the repository.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Thumbprint,
    [Parameter(Mandatory)] [string] $OutFile,
    [string] $Repo = 'stephenstirling/arca',
    # Set when the commit being released already carries the new release tag, so the "previous" one is found from its parent.
    [switch] $FromParent
)

$ErrorActionPreference = 'Stop'

$start = if ($FromParent) { 'HEAD^' } else { 'HEAD' }
# Newest release tag reachable from $start (none yet for the first release).
$previous = git tag --merged $start --list 'v[0-9]*' --sort=-v:refname | Select-Object -First 1
$range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }

# `git log` format %s is the commit title (first line) only.
$titles = @(git log --no-merges --format='%s' $range)
if ($LASTEXITCODE -ne 0) { throw 'git log failed.' }
$changes = if ($titles.Count) { ($titles | ForEach-Object { "- $_" }) -join "`n" } else { '- No changes recorded.' }
$since = if ($previous) { "since $previous" } else { 'in this first release' }

$zip = "Arca-$Version-win-x64.zip"
$notes = @"
## What's changed $since

$changes

## Download and run

1. Download **$zip** below and unzip it.
2. Run **Arca.exe**. If Windows SmartScreen says "Windows protected your PC", click **More info**, then **Run anyway**.

Arca is signed with a **self-made certificate**, not a paid one, so SmartScreen will still warn. That is expected. You do **not** need to install the certificate or mark it as trusted, and you should not.

To remove Arca, use **Integra** (Uninstall), or delete the Arca folder. Your settings in `%LocalAppData%\Arca` are left alone.

## Verify your download

- **Checksum:** in PowerShell, run ``Get-FileHash .\$zip -Algorithm SHA256`` and compare it with the line for $zip in **SHA256SUMS.txt**.
- **Who built it:** with the [GitHub CLI](https://cli.github.com/), run ``gh attestation verify .\$zip -R $Repo``. This proves the zip was built by this repository's release workflow.
- **Signature:** right-click ``Arca.exe`` > Properties > Digital Signatures > Details > View Certificate. The certificate thumbprint should be:

``````
$Thumbprint
``````

  The public certificate is attached as **Arca-signing.cer** (for comparing only). Windows will say the signature's root is not trusted; that is expected for a self-signed certificate.
"@

[IO.File]::WriteAllText($OutFile, ($notes -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
Write-Host "Release notes written to $OutFile (range: $range)"
