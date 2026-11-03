# Arca

A modern archive manager for Windows 11: a cleaner alternative to WinRAR and 7-Zip, and part of the Stirling suite
that [Integra](https://github.com/stephenstirling/integra) installs and updates. *"Arca" is a working name.*

- **No account, no telemetry, no network calls.**
- **Safe by default.** Every name inside an archive is checked before anything is written: `../` climbs, absolute
  paths, Windows device names (`CON`, `NUL`), hidden data streams and links in the destination folder are refused.
  Output is counted as it is written, so a zip bomb stops at a size or compression-ratio limit instead of filling the disk.
- **Keeps the "downloaded from the internet" mark** (Mark-of-the-Web) on extracted files, so SmartScreen and Office still check them.
- **Smart extract.** Several top-level items go into a folder named after the archive; a single folder is left as it is.

## Formats

| | Status in this scaffold |
|---|---|
| ZIP, 7z, RAR, RAR5, TAR (+ gz, bz2, xz) | Read and extract |
| Zstandard, Brotli, LZ4, ZPAQ, ISO, WIM, CAB, VHD, DMG, SquashFS, CPIO, RPM, DEB, MSI, CBZ/CBR/CB7 | Planned (ISO, CAB and Zstandard are already recognised and reported as "not supported yet") |
| Creating ZIP, 7z, TAR, Zstandard | Planned |
| Creating RAR | Not possible without a RARLAB licence. Planned: use the user's own `rar.exe` if WinRAR is installed |

RAR is read with SharpCompress's own reader, so no RARLAB UnRAR source is involved.

## Roadmap (from the WinRAR / 7-Zip gap list)

1. Preview files inside an archive without extracting; thumbnail grid for photo archives (pairs with Ocula)
2. Windows 11 context-menu entry, Defender scan before extraction
3. Background queue with taskbar progress; presets (email-friendly, max compression, backup)
4. Split / merge multi-part archives, repair damaged archives, compare two archives
5. Open from OneDrive / SMB / WebDAV without a full download

## Download

Releases are signed with a self-made certificate (no paid signing). Its SHA-1 thumbprint will be listed here once the
certificate exists: `TODO: paste the thumbprint printed by tools/New-SigningCert.ps1`. The release workflow refuses to
publish until this section shows the real thumbprint.

## For Integra

`integra-app.json` describes Arca the same way Porta's does. Switches: `Arca.exe --version`, `--status`, `--quit`
(see `src/Arca/Cli.cs`). Settings live in `%LocalAppData%\Arca`, which updates and uninstalls leave alone.
Before the first release: run `tools/New-SigningCert.ps1`, put its thumbprint in `integra-app.json` (replacing the
all-zeros placeholder) and here, add the `ARCA_SIGNING_PFX_BASE64` and `ARCA_SIGNING_PFX_PASSWORD` repository secrets.

## Build

Needs the .NET 8 SDK. `dotnet test tests/Arca.Core.Tests` runs the core tests on any OS;
`dotnet build src/Arca -c Debug` builds the app (Windows only).

## Licence

TBD by the owner (the other suite repos' licence is the natural choice).
