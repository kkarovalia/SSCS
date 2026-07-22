# CLAUDE.md — EDI / EDI13 → SSCS Converter

Claude-specific guidance for this repo. Full agent rules: **[AGENTS.md](./AGENTS.md)**. Format detail: **[analysis/FORMAT_SPEC.md](./analysis/FORMAT_SPEC.md)**.

## What this is

Production **.NET 8 WinForms** app converting fixed-width EDI/EDI13 invoices to SSCS text. Output is a portable single-file `win-x64` EXE under `converter/publish/`.

Work inside `SSCS_EDI_FORMAT/` (or that folder as the workspace root).

## Do / don’t

| Do | Don’t |
|----|--------|
| Treat `SSCS (REAL)/` as authoritative | Guess X12/CSV/generic EDI behavior |
| Edit conversion in `EdiToSscs.Core` | Put business logic in WinForms |
| Prove changes with byte-for-byte tests | Claim “looks the same” success |
| Preserve spaces, widths, EOLs | `Trim()`, split-on-space, reformat text |
| Fail closed on bad UPC-E check digits | Silently leave suspicious barcodes |
| Blank + warn on unsupported UPC-E NS (fixtures) | Invent new barcode policies |

## Key code map

- `converter/src/EdiToSscs.Core/EdiConverter.cs` — conversion
- `UpcE.cs` — expansion / check digit
- `OutputNaming.cs` — `SSCS_` filenames
- `ConversionService.cs` — read/convert/atomic write
- `ConversionCoordinator.cs` — queue + statuses
- `converter/src/EdiToSscs.App/MainForm.cs` — UI only
- `converter/tests/EdiToSscs.Tests/` — mandatory proofs

## Commands

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"; $env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
cd converter
dotnet test tests/EdiToSscs.Tests -c Release
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

## When fixing a fixture mismatch

1. Compare input and `SSCS (REAL)` bytes (first differing offset/line/col).
2. Update `FORMAT_SPEC.md` if a new rule is proven.
3. Fix Core + add a regression test.
4. Re-run the suite; confirm the pair is identical.

## Scope discipline

- Prefer small, proven format fixes over broad refactors.
- Do not commit secrets or large rebuild artifacts (`bin/`, `obj/`, `publish/` EXE).
- Do not change git config; commit/push only when asked.
