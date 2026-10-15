# AGENTS.md — vendor invoices → SSCS NAXML converter

Instructions for coding agents working in this repository.

## Project

Windows desktop utility that converts vendor invoices — **fixed-width EDI/EDI13 text files** and
**vendor PDFs** — into **NAXML-BusDoc 1.5** invoice files for SSCS. Since 2026-10-05 a vendor PDF
can also become a **GHRA-style EDI file for Modisoft**, with a 13-character barcode field (since
2026-10-09; 12 before), imported with Modisoft's EDI option "Xpress Wholesale" for every vendor (the
PDF to Modisoft EDI tab;
`ModisoftEdiBuilder`, `analysis/FORMAT_SPEC.md` Part 3). **Since 2026-10-15 that tab has no vendor
dropdown and takes any vendor's invoice** — a PDF or a photo — read by one generic Gemini request
(`AnyInvoiceParser`, `GeminiVisionReader.ReadAnyInvoice`, `PdfVendor.Any`); Modisoft takes only the
lines and the vendor is picked on the purchase. The PDF to SSCS XML tab keeps its five vendors and
their tuned readers. When the tab cannot do an invoice (Gemini down, no barcodes printed), do it by
hand with `analysis/INVOICE_TO_MODISOFT.md` and `converter/tools/ModisoftFile`. The old Texas Wholesale
option matched barcodes on 11 digits and merged EAN-13s (Yumi, 2026-10-09). That is why the field
is 13 and the option is Xpress Wholesale. For one of the five
converter vendors while Gemini is unavailable, use `converter/tools/HandReading`. Deliverable is a
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
        └── vendors/                # one profile per PDF vendor (DR_PEPPER.md, BLUE_BELL.md,
                                    #   CHUBIWINOS.md, BIMBO.md, RED_BULL.md)
```

**One input is normally one invoice, but the writer supports several.** Every PDF vendor here now
produces a single payable per file — including Blue Bell, whose ticket prints a *separate invoice
number* on its Returns block and was assumed for that reason to be two payables. It is not: live
SSCS holds the 03/19/2026 ticket as one $325.50 paidout of seven lines. A printed invoice number
identifies a document; it does not decide what the store books. Only the store's own books do.
`ConversionService` still writes N files for N invoices, and all documents are written and verified
as temp files before any of them claims a final name, so a ticket never lands half-converted.

## Non-negotiable rules

1. **Never invent format behaviour.** Derive it from real files, document it in
   `analysis/FORMAT_SPEC.md`, then implement. Where a claim is verified against live SSCS, say so
   and date it.
2. **Every parse needs a checksum.** Reconcile the lines against the invoice's own printed total.
   A parser with no way to detect its own misreading is not finished. Take any *second* checksum the
   paper offers, too — a printed unit count catches the one error the money cannot, because a misread
   quantity is absorbed by the unit cost and still balances to the penny (Chubiwinos, `Totals: 206`).
   **Check that the checksum is independent of what it is checking.** On Red Bull it is not: the
   scanner mis-renders a glyph identically everywhere it appears, so its printed total is wrong
   exactly when the rows are, and two money columns holding the same number are one reading and not
   two. There the arithmetic has to cross *different* glyphs — see `analysis/vendors/RED_BULL.md` §4.
3. **Never modify or delete input files.**
4. **Never silently overwrite output.** Use `SSCS_Name.xml`, then `SSCS_Name (1).xml`, …
5. **Never claim success without running the tests.**
6. **Do not put invoice/product record contents in logs.**
7. **Do not show stack traces to end users** — plain-language messages only.
8. **Warn only when a human must act.** A warning that fires on every conversion trains people to
   ignore warnings. Charge records are counted, not warned about, for exactly this reason.
9. Keep the GUI simple for nontechnical store staff.
10. **Refuse a file only when the money is in doubt.** A header field the scan destroyed — a date,
    an invoice number — is asked for, not fatal; the ticket still has to reconcile against its own
    printed total either way, so nothing is lost by accepting it and a whole delivery is saved.
    Never accept a keyed value for the **total** itself: that is what proves the parse. And never
    silently repair a header field instead of asking — offer the repaired reading as a suggestion
    for the operator to check against the paper.

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

### Delivery: SMTP behind an admin PIN, on an explicit button

Because fact 3 below is true — a store is chosen by the address a file arrives at — emailing the
file is what actually files an invoice.

**Conversion always writes to the output folder. Sending is a separate, explicit act.**

- `EmailSender.cs` — `IEmailDelivery` + `SmtpEmailSender`. **One invoice per message**, attachment
  built from a `MemoryStream` over the converted bytes. A Blue Bell ticket carrying Sales *and*
  Returns therefore sends two emails, and one failing never takes the good one down with it.
- **Send Email** on a convert tab sends every converted invoice on that tab, with **no row
  selection** and no confirmation — one click. It runs on a background thread (a dead network costs
  45s per message).
- **Modisoft files go to a second address** (`EmailSettings.ModisoftToAddress`, the store's own
  mailbox), sent from the same mailbox by their own `SmtpEmailSender`. Modisoft does not route by
  address: somebody downloads the file and imports it into a purchase they create by hand. A store
  may set the SSCS address, the Modisoft address or both.
- `QueuedFile.MarkEmailed` / `WasEmailed` is what makes one-click safe: an invoice already sent is
  skipped, so pressing the button twice cannot double-send. **A duplicate invoice email books the
  delivery twice in SSCS, the money balances both times, and nothing downstream flags it** — this
  guard is the only place it can be caught.
- `SecretProtector.cs` (DPAPI, current Windows user) holds the app password; `AdminPin.cs` (PBKDF2)
  gates the settings screen. Both fail closed: an unreadable secret means "not configured", and a
  malformed PIN hash refuses every PIN. Neither is ever displayed back. Converting and sending never
  ask for the PIN — only changing *where files go* does.
- **Setup requires a Gmail App Password, which requires 2-Step Verification on the sending
  mailbox.** A Gmail-compose-link alternative that needed no credentials was built on 2026-07-27 and
  removed the same day: it cannot attach a file (browsers forbid a page from reaching a local file;
  Gmail's compose params are only `to`/`su`/`body`/`cc`/`bcc`), and the manual attach step was not
  wanted. Don't re-propose it.
- `ConversionOutcome.Documents` / `QueuedFile.Documents` keep the XML in memory for the session, so
  the attachment is provably the converted bytes and `XmlViewerDialog` works even if the saved file
  moved. `QueuedFile.OutputPaths` carries every file written.

## The verified facts (live SSCS)

Read these before touching conversion logic. Full evidence in `FORMAT_SPEC.md`.

1. **SSCS drops the last character of the GTIN itself** and stores the rest as the item's ID/PLU.
   `600609100271` → `60060910027`; `6917673180912` → `691767318091`. Always send the whole
   barcode. Because a 12-digit UPC-A therefore reaches the same id the retired output wrote, a
   vendor can move from the old path to XML without creating duplicate items.
   **It drops nothing else — leading zeros are kept** (verified 2026-07-26). Blue Bell prints the
   11-digit UPC-A body padded to 12; sent as printed it made a *New Item*, sent as the real UPC-A
   it matched the store's own item. If a source gives you a body, put the check digit back.
2. **SSCS rounds `units × unit cost` to the cent per line, then sums.** 4-decimal unit costs are
   safe; errors do not accumulate. Verified 16/16 on a Dr Pepper invoice totalling $532.43.
3. **Store routing is the email address the file is sent to.** Every store has its own EDI email
   address; nothing inside the file selects a store.
4. **SSCS counts in individual sellable units. A box or case is never a unit** (verified
   2026-07-26). Its Qty column is the number of things the store sells: one pint, one ice cream
   sandwich. A vendor line reading `QTY 2 @ $6.48` for a twelve-bar carton is held as
   **24 @ $0.54**. A vendor can mix both in one column — Blue Bell counts pints individually and
   `DZ` cartons by the carton on the same ticket — so establish what a unit is **per product**.
   The money reconciles either way, so no checksum catches this; the tell is an absurd retail
   (a 40% margin on the carton priced one sandwich at $11.49 instead of $0.99).
5. **For an item it already knows, SSCS keeps its own description, retail and department** and
   honours only the file's GTIN, quantity and cost. Verified twice: it showed
   `JANJOO 8PIN 5FT CABLE` over the file's wording, and `BLUE BELL PT GR SUPREM` in department
   ICE CREAM while the file said otherwise. So GTIN, units and unit cost must be exact;
   description and retail only ever seed a **brand-new** item — but retail must still never be
   `0.00`, or an unpriced new item rings up free.

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
