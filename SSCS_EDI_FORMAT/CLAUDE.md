# CLAUDE.md — vendor invoices → SSCS NAXML converter

Claude-specific guidance for this repo. Full agent rules: **[AGENTS.md](./AGENTS.md)**.
Format detail: **[analysis/FORMAT_SPEC.md](./analysis/FORMAT_SPEC.md)**.
PDF input path: **[analysis/PDF_TO_SSCS.md](./analysis/PDF_TO_SSCS.md)** + per-vendor profiles in
**[analysis/vendors/](./analysis/vendors/)**.
**Adding a PDF vendor: [analysis/ADDING_A_PDF_VENDOR.md](./analysis/ADDING_A_PDF_VENDOR.md)** —
standalone, needs no other reading.

## What this is

Production **.NET 8 WinForms** app that turns vendor invoices into **NAXML-BusDoc 1.5** files for
SSCS. Two input paths, one output format:

- **EDI to SSCS XML** — the fixed-width `.txt`/`.edi`/`.edi13` files vendors email (four layouts:
  standard EDI, EDI13, Ruby Red EDI13, STRP/Alp).
- **PDF to SSCS XML** — vendor PDF invoices, currently Dr Pepper, read with **PdfPig**.

Output is a portable single-file `win-x64` EXE under `converter/publish/`.

**The fixed-width "SSCS EDI" output was retired on 2026-07-25.** Its 11-character product-code
field could not hold a 13-digit EAN-13, which collapsed 72 VM Novelties line items onto 40 IDs and
got the file rejected. NAXML has no such limit. A snapshot of the old version lives outside the
repo at `C:\Users\Kashf\Desktop\EDI version SSCS`, and in git history.

Work inside `SSCS_EDI_FORMAT/` (or that folder as the workspace root).

## The three facts that govern everything

Verified in live SSCS on 2026-07-25. Re-read these before touching conversion logic.

1. **SSCS drops the last character of the GTIN itself.** Send `600609100271`, SSCS stores
   `60060910027`. Send the 13-digit `6917673180912`, it stores `691767318091`. Always send the
   whole barcode; never pre-drop a check digit, never strip a leading zero.
2. **SSCS rounds `units × unit cost` to the cent per line, then sums.** So 4-decimal unit costs are
   safe — `96 × 1.3542 = 130.0032` showed as `130.00`, and a 16-line invoice still totalled exactly
   $532.43. Errors do not accumulate.
3. **Store routing is the email address the file is sent to,** not anything inside the file. Every
   store has its own EDI email address. The `ShipTo`/`Location` fields are decoration.

## Do / don't

| Do | Don't |
|----|--------|
| Send whole barcodes and let SSCS drop the check digit | Pre-truncate a code to 11 characters |
| Prove a barcode change with `EveryBarcodeLandsOnTheSameSscsItem` | Assume an id change is harmless |
| Reconcile lines against the invoice's own printed total | Trust a parse with no checksum |
| Fail closed on a bad UPC-E check digit | Guess at a mistyped barcode |
| Warn only when a human must act | Add warnings that fire on every file |
| Populate each-list from the vendor's own figure | Write `0.00` and risk a free sale |
| Edit conversion in `EdiToSscs.Core` | Put business logic in WinForms |

## Key code map

- `EdiConverter.cs` — reads all four fixed-width layouts into `Invoice`; header parsing, the
  case-cost/pack/cases money model, barcode → GTIN rules
- `Invoice.cs` — the shared model (`Invoice`, `InvoiceLine`, `ShipToInfo`) both paths produce
- `NaxmlBuilder.cs` — the one place XML bytes are written
- `Upc.cs` — check-digit maths; `UpcE.cs` — UPC-E expansion
- `PdfTextExtractor.cs` — PdfPig text layer → visual lines
- `DrPepperPdfParser.cs` — Dr Pepper PDF → `Invoice`
- `ConversionService.cs` — read / convert / atomic write; `ConversionCoordinator.cs` — queue
- `OutputNaming.cs` — `SSCS_*.xml` filenames
- `converter/src/EdiToSscs.App/MainForm.cs` — UI only (2 convert tabs + Settings)
- `converter/tests/EdiToSscs.Tests/` — `XmlConversionTests` is the primary proof suite

## Commands

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"; $env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
cd converter
dotnet test tests/EdiToSscs.Tests -c Release
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

## Testing without a reference file

There is no byte-reference for XML — SSCS consumes these files, it does not produce them. So the
suite proves behaviour instead of bytes:

- **`EveryBarcodeLandsOnTheSameSscsItem`** is the important one. For every input fixture with a
  paired retired-output file, each GTIN minus its last character must equal the code the old
  converter wrote. The only permitted difference is a 13-digit EAN keeping one extra character.
  This is what guarantees moving a vendor to XML creates no duplicate items.
- Totals reconcile line by line, output is ASCII/CRLF and well-formed, and the Dr Pepper fixture is
  checked against the 16 Ex Cost figures live SSCS displayed.

## Scope discipline

- Prefer small, proven changes over broad refactors.
- Do not commit `bin/`, `obj/`, or the published EXE.
- Do not change git config; commit/push only when asked.
