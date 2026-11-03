# Arca

Free Windows 11 archive manager by Stephen Stirling: a modern alternative to WinRAR and 7-Zip.
WinUI 3 on the Windows App SDK, .NET 8, runs unpackaged (plain .exe). Part of the suite that
Integra installs and updates (Ocula, Capta, Porta, Scripta, Integra). "Arca" is a working name.

## Build and run

Needs only the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Visual Studio is not required.

```
dotnet test tests/Arca.Core.Tests
dotnet build src/Arca -c Debug
src\Arca\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Arca.exe  [archive file]
```

## Layout

- `src/Arca.Core/` - plain .NET library, no UI. `ArchiveFormat.cs` (format detection by content),
  `EntryPathGuard.cs` (refuses `../`, absolute paths, device names, streams, links),
  `ArchiveService.cs` (list and extract, with size and zip-bomb limits), `MarkOfTheWeb.cs`, `Models.cs`
- `src/Arca/` - the WinUI 3 app. `Program.cs` entry point, `Cli.cs` (the switches Integra uses),
  `MainWindow.xaml(.cs)` list, drag and drop, extract
- `tests/Arca.Core.Tests/` - xUnit tests for the core (run on any OS)
- `integra-app.json` - tells Integra how to install, update and stop Arca (same contract as Porta's)
- `tools/` - release scripts, copied from Porta (`Build-Release.ps1`, `New-IntegraManifest.ps1`,
  `New-ReleaseNotes.ps1`, one-time `New-SigningCert.ps1`)
- `.github/workflows/` - `build.yml` (build and test on every PR), `release.yml` (tag `vX.Y.Z` = signed release)

## Integra contract (do not break)

- `Arca.exe --version`, `--status`, `--quit` must keep working: exit code 0 on success, `--quit` exit 0 only when
  every Arca window is gone.
- Releases ship `Arca-<version>-win-x64.zip` (one folder holding `Arca.exe`), `SHA256SUMS.txt`, `Arca-signing.cer`
  and `integra-app.json`. The exe's file version must equal the tag.
- Settings and data go in `%LOCALAPPDATA%\Arca`, never next to the exe: updates and uninstalls leave them alone.

## Rules

- Archives are untrusted input. Every entry name goes through `EntryPathGuard`; sizes are counted from bytes
  actually written, never from headers. Add a test with every safety change.
- Commits are small and made after each working step.
- Never push to `main` without asking the owner first; work on branches.
- No paid code signing, no telemetry, no network calls.
- Keep fixes minimal; don't redesign without being asked.
- The owner is not a developer: explain problems in plain words.
