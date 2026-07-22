# AGENTS.md — EDI / EDI13 → SSCS Converter

Instructions for coding agents working in this repository.

## Project

Windows desktop utility that converts **fixed-width** EDI and EDI13 invoice text files into SSCS `.txt` files. Deliverable is a **self-contained portable win-x64 EXE** (no install, no admin, no separate runtime, no internet, no console window).

Stack: **C# / .NET 8 WinForms**. Do not switch stacks unless there is a strong technical reason, and document it.

Repo layout:

```
SSCS/                          # GitHub repo root
└── SSCS_EDI_FORMAT/
    ├── converter/             # Solution (Core, App, Tests)
    ├── EDI/                   # Standard EDI inputs
    ├── EDI13/                 # EDI13 inputs
    ├── SSCS (REAL)/           # Authoritative expected SSCS outputs
    └── analysis/FORMAT_SPEC.md
```

Local workspace may be `SSCS_EDI_FORMAT` itself; paths below are relative to that folder.

## Non-negotiable rules

1. **Fixtures are the source of truth.** Never invent format behavior. Derive rules from byte comparison against `SSCS (REAL)/`, document in `analysis/FORMAT_SPEC.md`, then implement.
2. **Byte-oriented conversion.** No `Trim()`, split-on-spaces, whitespace collapsing, or blind UTF-8 re-encoding of record bodies. Prefer raw `byte[]` / spans.
3. **Never modify or delete input files.**
4. **Never silently overwrite SSCS outputs.** Use `SSCS_Name.txt`, then `SSCS_Name (1).txt`, …
5. **Never claim success without byte-for-byte proof** (length + SHA-256 + every byte) when a reference exists.
6. **Do not put invoice/product record contents in logs.**
7. **Do not show stack traces to end users** — plain-language messages only.
8. Keep the GUI simple for nontechnical store staff.

## Architecture

| Project | Role |
|---------|------|
| `converter/src/EdiToSscs.Core` | All conversion, naming, settings, logging, destination validation, atomic writes, queue coordinator |
| `converter/src/EdiToSscs.App` | Thin WinForms shell only |
| `converter/tests/EdiToSscs.Tests` | xUnit: fixtures, format, naming, settings, logging, file I/O, coordinator |

Logic belongs in **Core**. App marshals to the UI thread and shows dialogs.

Settings: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\settings.json` (atomic write).  
Logs: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\Logs` (30-day retention; delete only files **strictly older** than 30 days; never follow reparse points).

## Format summary (see FORMAT_SPEC.md for details)

Detect format from **contents**, not filenames.

| Format | Header | B width | Notes |
|--------|--------|---------|-------|
| Standard EDI | vendor field 7 chars | 76 | UPC-E may expand in 11-char code field |
| EDI13 | same; strip vendor `13` suffix | 78 | Keep first **11** of 13-char code field (alphanumeric OK) |
| Ruby Red | 64-byte `GHRA13`+company… | 79 | Drop verified suffix `00000000  `; output B = 67 |

Header out: `A` + 6-char vendor + rest (`GHRA`/`PEPSI`/`GHRA13`→`GHRA`). Strip vendor `13` **even if** B records are standard width (e.g. 810738).

**UPC-E:** only when the 11-char field is exactly 8 digits + 3 spaces. Expand with official rules; validate check digit. NS must be `0` or `1`. Unsupported NS → **blank field + warning** (fixture-proven). Bad check digit → **reject file**.

**C records:** preserve byte-for-byte. Unknown record types → reject with line number.

Preserve input line endings (CRLF/LF) and final-newline presence/absence.

## Output naming

Strip one leading `EDI13`/`EDI`/`SSCS` token (+ following space/hyphen/underscore), prepend exactly one `SSCS_`, extension `.txt`. Empty stem → `SSCS_Converted.txt`.

## Build / test / publish

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"
$env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
cd converter
dotnet test tests/EdiToSscs.Tests -c Release
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

Publish must yield a **single** `EDI-to-SSCS-Converter.exe` (WPF-only natives stripped in csproj).

When adding fixtures: place inputs in `EDI/` or `EDI13/`, expected outputs in `SSCS (REAL)/`, add/update tests, prove byte-identical.

## Changing conversion logic

1. Diff input vs `SSCS (REAL)` as raw bytes.
2. Update `FORMAT_SPEC.md`.
3. Implement in Core; add failing-then-passing tests.
4. Re-run full suite; re-verify affected fixture pairs.
5. Do not loosen validation without fixture proof.

## Safety / UX reminders

- Revalidate destination before every conversion; if missing/unwritable, ask for a new folder (no silent Desktop/EXE fallback).
- Atomic temp write → flush → verify → `Move` to final name; clean up temps on failure.
- Keep App responsive (background worker); marshal UI with handle/disposed guards.
