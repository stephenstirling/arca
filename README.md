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
| Multi-part archives: `.part1.rar` / `.rar` + `.r00`, `.7z.001` (any split), `.z01` + `.zip` | Read and extract, opened from any part |
| Zstandard, Brotli, LZ4, ZPAQ, ISO, WIM, CAB, VHD, DMG, SquashFS, CPIO, RPM, DEB, MSI, CBZ/CBR/CB7 | Planned (ISO, CAB and Zstandard are already recognised and reported as "not supported yet") |
| Creating ZIP, 7z, TAR, TAR.GZ, TAR.BZ2 | Yes (Add button, or drop files on the window) |
| Passwords on new ZIP and 7z archives (AES-256; a 7z also hides its file names) | Yes |
| Creating Zstandard, creating split archives | Planned |
| Creating RAR | Not possible without a RARLAB licence. Planned: use the user's own `rar.exe` if WinRAR is installed |

RAR is read with SharpCompress's own reader, so no RARLAB UnRAR source is involved. 7z and password-protected
archives are created with 7-Zip's own `7za.exe` (LGPL), which ships beside Arca; see `third-party/7-Zip/README.md`.

## Roadmap (from the WinRAR / 7-Zip gap list)

1. Preview files inside an archive without extracting; thumbnail grid for photo archives (pairs with Ocula)
2. Windows 11 context-menu entry, Defender scan before extraction
3. Background queue with taskbar progress; presets (email-friendly, max compression, backup)
4. Create split archives, repair damaged archives, compare two archives
5. Open from OneDrive / SMB / WebDAV without a full download

## Download

The easiest way to get Arca is through **Integra**, which installs and updates it without Windows' "unrecognized app"
warning. Releases are also on the [Releases page](https://github.com/stephenstirling/arca/releases).

Releases are signed with a self-made certificate (no paid signing). To check one: right-click `Arca.exe` > Properties >
Digital Signatures > Details > View Certificate > Details > Thumbprint. The certificate is named **Arca** (Stephen
Stirling) and its thumbprint is:

`1F9679C588ECBC69AAD04C296DF662759C6ADC48`

Windows will say the certificate isn't trusted. That's expected. The public certificate (**Arca-signing.cer**) is on each
release, only so you can compare it.

## For Integra

`integra-app.json` describes Arca the same way Porta's does. Switches: `Arca.exe --version`, `--status`, `--quit`
(see `src/Arca/Cli.cs`). Settings live in `%LocalAppData%\Arca`, which updates and uninstalls leave alone.
Releases are signed by the release workflow with the certificate from `tools/New-SigningCert.ps1`, kept in the
`ARCA_SIGNING_PFX_BASE64` and `ARCA_SIGNING_PFX_PASSWORD` repository secrets. Its thumbprint is in `integra-app.json`
and above; a release whose signature doesn't match is refused.

## Build

Needs the .NET 8 SDK. `dotnet test tests/Arca.Core.Tests` runs the core tests on any OS;
`dotnet build src/Arca -c Debug` builds the app (Windows only).

## Licence

TBD by the owner (the other suite repos' licence is the natural choice).
