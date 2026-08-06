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
- **PDF to SSCS XML** — vendor PDF invoices (**Dr Pepper**, **Blue Bell**, **Chubiwinos S&G**,
  **Bimbo Bakeries**, **Red Bull**), read with **PdfPig**. The tab
  has a vendor dropdown; each vendor has its own parser.

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

A fourth, verified 2026-07-26, follows from the first: **SSCS does not strip leading zeros.** Blue
Bell prints its UPC column as the 11-digit body padded to 12 (`007189905101`). Sent as printed it
became a *New Item* under `00718990510`; sent as the real UPC-A `071899051015` it matched the
store's existing `07189905101` in department ICE CREAM. Restore the check digit — see
[analysis/vendors/BLUE_BELL.md](./analysis/vendors/BLUE_BELL.md) §3.

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
- `DrPepperPdfParser.cs` — Dr Pepper PDF → `Invoice`. Two things beyond the plain layout. **Ink:**
  invoices come back marked up in pen and OCR turns a stroke into a stray token, so a token with no
  digit in it is discarded — but only white space and the unused NET/TAX columns may absorb one. A
  mark on WHSLE/CASES/UNITS/AMOUNT, or on a barcode, is `PDF_UNREADABLE_LINE`. The barcode case
  cannot be caught by the total gate (the money still adds up), so the tell is **a priced row no
  product line claimed**. **Credits:** a `CREDITS` banner switches the page to a shorter column set
  with no WHSLE and money in brackets; they are line items with **negative units at a positive
  cost** — the invoice does not reconcile without them.
- `BlueBellPdfParser.cs` — Blue Bell PDF → one or more `BlueBellSection`s (a ticket can carry a
  Sales *and* a Returns invoice); `ToGtin` holds the check-digit rule, `SeedRetail` the 40% margin,
  `PackFor` the units rule — **a `DZ` line is a carton of twelve, so its QTY is not units**
  (verified in live SSCS: QTY 2 @ $6.48 is held as **24 @ $0.54**). A box is a case, not a unit.
- `ChubiwinosPdfParser.cs` — Chubiwinos S&G PDF → `ChubiwinosTicket`. Its scans damage the **money
  columns**, so a row that does not multiply out is settled by which reading makes the invoice's own
  total (`Resolve`), and the printed `Totals: <units>` count is a **hard gate** on the quantities —
  a misread QTY balances to the penny and still books the wrong stock. The grand total is a
  **majority vote** of the three places it is printed. `ChubiwinosCatalog.cs` holds item number →
  barcode and is consulted **only** for a code that already failed its check digit; a product it has
  never seen goes to the operator. **Not yet round-tripped through live SSCS** — see
  [analysis/vendors/CHUBIWINOS.md](./analysis/vendors/CHUBIWINOS.md) §9.
- `BimboPdfParser.cs` — Bimbo Bakeries delivery ticket → `BimboTicket`. **Round-tripped through live
  SSCS 2026-07-28**, which booked its DELIVERIES *and* RETURNS as **one** $335.07 paidout — so returns
  are negative lines on the same invoice, the same as Blue Bell and Red Bull. QTY is already individual units (`SS2P` is
  one two-pack, not two) and PRICE is already the unit cost, so there is no case arithmetic at all.
  Its UPC column is a **body with both the leading zero and the check digit removed**
  (`74323-09183` → send `074323091830`), and **nothing on the page validates itself** — so
  `BimboCatalog.cs` cross-checks Bimbo's item-number column against the barcode, which is the only
  thing that catches the four mis-reads the money cannot see. Three checksums (row, band, ticket); a
  band's unit count is overruled by the ticket's own when the two disagree. **Do not use its printed
  `SUGG RETAIL`** — Bimbo suggests 1.99 where the store rings 3.59; retail is seeded at 45%.
  See [analysis/vendors/BIMBO.md](./analysis/vendors/BIMBO.md).
- `RedBullPdfParser.cs` — Red Bull delivery ticket → `RedBullTicket`. **Round-tripped through live
  SSCS 2025-11-24**, which booked a two-page ticket (a $367.00 delivery invoice and a −$24.50 credit
  invoice with its own number) as **one** $342.50 paidout of ten lines — so the whole PDF is one
  payable, however many pages. `UNITS` is already individual units and is SSCS's Qty directly; unit
  cost is `TOTAL ÷ UNITS`, which also works on a credit row where no case price is printed.
  Barcodes are full 12-digit UPC-A **with a real check digit**, so unlike Bimbo the page validates
  itself; `RedBullCatalog.cs` only picks between the six single-digit repairs a bad check digit
  allows. **Its printed total does NOT gate the parse** — the scanner mis-renders a glyph the same
  way everywhere, so one sample's $918.33 reads $910.33 in all three places it appears, and PRICE
  and TOTAL agreeing means nothing because they are the same glyph twice. Read
  [analysis/vendors/RED_BULL.md](./analysis/vendors/RED_BULL.md) §4 before touching its money.
  Reads positioned words (`PdfTextExtractor.ExtractPages`) because its columns cannot be told apart
  by reading order once the scan has split or dropped one.
- `RetailSeed.cs` — the each-list seed for vendors whose invoices print no retail column. 40% for
  Blue Bell; `FromCost(cost, margin)` takes the vendor's own margin, which is how
  Chubiwinos gets the 50% that reproduces the Ea List 5.59 SSCS shows against a 2.79 cost
- `ConversionService.cs` — read / convert / atomic write (**writes N files for N invoices**);
  `ConversionCoordinator.cs` — queue; `IInvoiceDetailsPrompt` — asks the operator for the Blue Bell
  and Chubiwinos invoice number, for the date when the scan destroyed it (the ticket's own date
  always wins), and for a Chubiwinos barcode that neither read nor is on record. `AskLineField`
  asks for **one figure on one Red Bull row** — its UPC, units or amount — named the way the
  operator will find it ("The 5th product's UPC is blurry"). **Never the description:** SSCS
  overrides it for a known item, so asking would be a prompt that changes nothing. **The invoice-number
  box is never pre-filled from a scan, for any vendor** — `InvoiceDetailsRequest` deliberately has no
  suggestion field, so it cannot be
- `OutputNaming.cs` — `SSCS_*.xml` filenames; `BuildInvoiceOutputFileName` names by invoice number
- `EmailSender.cs` — SMTP send of the converted XML to the store, which is what actually routes an
  invoice (fact 3). **One invoice per message.** Conversion always saves to the output folder;
  sending is the separate **Send Email** button, which sends every converted invoice on the tab with
  no row selection. `QueuedFile.MarkEmailed`/`WasEmailed` stops a second press double-sending — a
  duplicate books the delivery twice in SSCS and nothing downstream catches it.
- `SecretProtector.cs` (DPAPI) / `AdminPin.cs` (PBKDF2) — the app password and the PIN gating the
  email settings. Both fail closed and neither is ever shown back. Needs a **Gmail App Password**
  (so 2FA on the sending mailbox). A credential-free Gmail-compose-link version was built and
  removed on 2026-07-27 — a web link cannot attach a file. Don't re-propose it.
- `converter/src/EdiToSscs.App/MainForm.cs` — UI only (Settings + PDF tab + EDI tab, in that order);
  `XmlViewerDialog` reads the in-memory documents, so it still shows the XML if the saved file moved
- `Assets/app.ico` — the shell icon (`ApplicationIcon` in the csproj) and, via `AppIcon.cs`, the
  window icon. It holds **different artwork per size**: lettered PDF→XML chips at 96px and above,
  and at 16–24px just the red and navy blocks, because the lettering is illegible there
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
- **`MatchesWhatLiveSscsDidWithBothBarcodeForms`** pins the Blue Bell round trip: the as-printed
  code created a new item, the check-digit form matched the store's own. Both Blue Bell suites run
  against the **real** OCR text of the sample scans, damage included.

## Scope discipline

- Prefer small, proven changes over broad refactors.
- Do not commit `bin/`, `obj/`, or the published EXE.
- Do not change git config; commit/push only when asked.
