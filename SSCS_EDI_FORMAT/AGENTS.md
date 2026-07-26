# AGENTS.md — vendor invoices → SSCS NAXML converter

Instructions for coding agents working in this repository.

## Project

Windows desktop utility that converts vendor invoices — **fixed-width EDI/EDI13 text files** and
**vendor PDFs** — into **NAXML-BusDoc 1.5** invoice files for SSCS. Deliverable is a
**self-contained portable win-x64 EXE** (no install, no admin, no separate runtime, no internet,
no console window).

Stack: **C# / .NET 8 WinForms**. Do not switch stacks unless there is a strong technical reason,
and document it.

> **The fixed-width "SSCS EDI" output was retired on 2026-07-25.** Its 11-character product-code
> field cannot hold a 13-digit EAN-13; on one VM Novelties invoice that collapsed 72 line items
> onto 40 distinct ids and SSCS rejected the file. A snapshot of the old version is at
> `C:\Users\Kashf\Desktop\EDI version SSCS` and in git history. Files under `SSCS (REAL)/` and
> `Fixtures/SSCS/` are that retired output — they are **no longer the expected result**, but they
> are still valuable as the reference for which item id each barcode must land on.

Repo layout:

```
SSCS/                          # GitHub repo root
└── SSCS_EDI_FORMAT/
    ├── converter/             # Solution (Core, App, Tests)
    ├── EDI/                   # Standard EDI inputs
    ├── EDI13/                 # EDI13 inputs
    ├── SSCS (REAL)/           # Retired fixed-width output — id reference only
    └── analysis/
        ├── FORMAT_SPEC.md          # NAXML output + fixed-width input layouts
        ├── ADDING_A_PDF_VENDOR.md  # standalone guide: new PDF vendor, start here
        ├── PDF_TO_SSCS.md          # PDF invoice → NAXML architecture summary
        └── vendors/                # one profile per PDF vendor (e.g. DR_PEPPER.md)
```

## Non-negotiable rules

1. **Never invent format behaviour.** Derive it from real files, document it in
   `analysis/FORMAT_SPEC.md`, then implement. Where a claim is verified against live SSCS, say so
   and date it.
2. **Every parse needs a checksum.** Reconcile the lines against the invoice's own printed total.
   A parser with no way to detect its own misreading is not finished.
3. **Never modify or delete input files.**
4. **Never silently overwrite output.** Use `SSCS_Name.xml`, then `SSCS_Name (1).xml`, …
5. **Never claim success without running the tests.**
6. **Do not put invoice/product record contents in logs.**
7. **Do not show stack traces to end users** — plain-language messages only.
8. **Warn only when a human must act.** A warning that fires on every conversion trains people to
   ignore warnings. Charge records are counted, not warned about, for exactly this reason.
9. Keep the GUI simple for nontechnical store staff.

## Architecture

| Project | Role |
|---------|------|
| `converter/src/EdiToSscs.Core` | All conversion, naming, settings, logging, destination validation, atomic writes, queue coordinator |
| `converter/src/EdiToSscs.App` | Thin WinForms shell only |
| `converter/tests/EdiToSscs.Tests` | xUnit: XML proofs, format detection, naming, settings, logging, file I/O, coordinator |

Both input paths produce the same `Invoice` model, and `NaxmlBuilder` is the single place XML bytes
are written. Logic belongs in **Core**; App marshals to the UI thread and shows dialogs.

Settings: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\settings.json` (atomic write).
Logs: `%LOCALAPPDATA%\EDI-to-SSCS-Converter\Logs` (30-day retention; delete only files **strictly
older** than 30 days; never follow reparse points).

## The three verified facts (live SSCS, 2026-07-25)

Read these before touching conversion logic. Full evidence in `FORMAT_SPEC.md`.

1. **SSCS drops the last character of the GTIN itself** and stores the rest as the item's ID/PLU.
   `600609100271` → `60060910027`; `6917673180912` → `691767318091`. Always send the whole
   barcode. Because a 12-digit UPC-A therefore reaches the same id the retired output wrote, a
   vendor can move from the old path to XML without creating duplicate items.
2. **SSCS rounds `units × unit cost` to the cent per line, then sums.** 4-decimal unit costs are
   safe; errors do not accumulate. Verified 16/16 on a Dr Pepper invoice totalling $532.43.
3. **Store routing is the email address the file is sent to.** Every store has its own EDI email
   address; nothing inside the file selects a store.

## Input format summary (see FORMAT_SPEC.md for details)

Detect format from **contents**, not filenames.

| Format | Header | B width | Code field |
|--------|--------|---------|------------|
| Standard EDI | vendor field 7 chars | 76 | 11 |
| EDI13 | same; `13` suffix stripped | 78 | 13 |
| Ruby Red | 64-byte `GHRA13` + company name | 79 | 13 |
| STRP/Alp | vendor + 8-digit `MMDDYYYY` date | 75 | 11 |

**B record money model** — with `base = 1 + codeWidth`, `[base+31..base+37)` is the cost of **one
case**, `[base+39..base+45)` is the **pack size**, `[base+45..base+50)` is the **number of cases**.
So `units = pack × cases` and `unit cost = case cost ÷ pack`. (Earlier docs called these "extended
cost" and "quantity" — that was wrong and only worked for single-case lines.)

**Barcodes:** 8 digits → UPC-E expansion + check digit; 12–13 characters → sent whole; 11 digits →
check digit appended; anything else → `0` appended so SSCS's drop returns the original code. A bad
UPC-E check digit **rejects the file**.

**C records** are dropped — they are labels with no amount.

## Verifying a new vendor

SSCS is the **consuming** system; it does not produce reference outputs, so a new vendor has no
byte-reference. Do not stall waiting for one:

1. Parse the vendor's file into `Invoice`, with a checksum against its printed total.
2. Convert, then have the user email the XML to a store's EDI address.
3. Confirm the **Paidout** tab (invoice #, vendor, date, total) and **Line Items** tab (ID/PLU,
   qty, ea cost, ex cost) read back correctly.
4. Pin the confirmed figures in tests — see how `PdfConversionTests` records the 16 Ex Cost values
   SSCS displayed for Dr Pepper example 7.

## Output naming

Strip one leading `EDI13`/`EDI`/`SSCS` token (+ following space/hyphen/underscore), prepend exactly
one `SSCS_`, extension `.xml`. Empty stem → `SSCS_Converted.xml`.

## Build / test / publish

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"
$env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
cd converter
dotnet test tests/EdiToSscs.Tests -c Release
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

Publish must yield a **single** `EDI-to-SSCS-Converter.exe` (WPF-only natives stripped in csproj).

## Changing conversion logic

1. Establish the behaviour from real files; if it touches barcodes or money, verify against live
   SSCS and date the claim.
2. Update `FORMAT_SPEC.md`.
3. Implement in Core; add failing-then-passing tests.
4. Re-run the full suite. `EveryBarcodeLandsOnTheSameSscsItem` must still pass — it is what
   guarantees no duplicate items are created in the customer's live system.
5. Do not loosen validation without evidence.

## Safety / UX reminders

- Revalidate destination before every conversion; if missing/unwritable, ask for a new folder (no
  silent Desktop/EXE fallback).
- Atomic temp write → flush → verify → `Move` to final name; clean up temps on failure.
- Keep App responsive (background worker); marshal UI with handle/disposed guards.
