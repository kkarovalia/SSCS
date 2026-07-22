# EDI to SSCS Converter

A portable Windows desktop utility that converts fixed-width EDI and EDI13 invoice
files into SSCS text files. Built with C# / .NET 8 WinForms, exactly as prescribed;
no technology deviation was needed.

## The deliverable

`publish\EDI-to-SSCS-Converter.exe` — a single self-contained 64-bit EXE:

- No installation, no administrator rights (`asInvoker` manifest), no separate runtime,
  no internet access. Copy it to any Windows 10/11 x64 desktop and double-click.
- GUI subsystem (no console window), per-monitor DPI aware, long-path aware.
- Drag one or more EDI/EDI13 files onto the window (or use **Choose Files**);
  conversion starts automatically and the format is detected from file contents.
- Settings: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\settings.json` (written atomically).
- Diagnostic logs: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\Logs`, JSON lines, no invoice
  contents, retained 30 days (files strictly older than 30 days are deleted; a file
  exactly 30 days old is kept; reparse points are never followed).

## Layout

| Path | Contents |
|------|----------|
| `src\EdiToSscs.Core` | Byte-oriented conversion core, UPC-E expansion, output naming, settings, destination validation, logging + retention, safe atomic file writing, UI-free queue coordinator |
| `src\EdiToSscs.App` | WinForms shell (thin; all logic lives in Core) |
| `tests\EdiToSscs.Tests` | 160 xUnit tests, including byte-for-byte proofs of every supplied reference pair |
| `..\analysis\FORMAT_SPEC.md` | The verified byte-level format specification derived from the supplied fixtures |

## Format rules (proven, not assumed)

Every rule was derived by raw-byte comparison of all 16 supplied input/output pairs and
is reproduced byte-for-byte by the test suite (length, SHA-256, and every byte asserted;
first differing offset/line/column reported on failure). See `..\analysis\FORMAT_SPEC.md`
for exact byte positions, the UPC-E expansion table, and the fixture-proven handling of
an invalid UPC-E number system (field blanked with a warning, per `SSCS_GHRA_129348`).

## Building

The .NET 8 SDK is required (a per-user copy lives at `%LOCALAPPDATA%\dotnet-sdk8`).

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"; $env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
dotnet test tests/EdiToSscs.Tests -c Release          # run all 160 tests
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish   # produce the single EXE
```

Single-file publish settings live in `EdiToSscs.App.csproj`, including a target that
strips the WPF-only native libraries the Windows Desktop runtime pack would otherwise
leave beside the EXE (this is a WinForms-only app; they are never loaded).
