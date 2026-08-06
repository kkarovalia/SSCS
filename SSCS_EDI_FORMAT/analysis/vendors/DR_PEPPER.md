# Vendor profile: Dr Pepper

**Vendor:** Dr Pepper Bottling Company of Texas.  **Supplier name written:** `DR PEPPER`.
**Input:** searchable PDF, one invoice per file.

**Status: IMPLEMENTED & VERIFIED** for straight sales. **Credits are NOT yet round-tripped through
live SSCS** — see the CREDITS section below; what SSCS does with a negative `InvoiceUnitQty` remains
an open question.

Confirmed in live SSCS twice:

- **2026-07-23** via the retired fixed-width output.
- **2026-07-25** via NAXML — example 7 (invoice 4643517800, $532.43) imported as XML at BEST STOP
  #14, seq 731. Vendor resolved to **DR. PEPPER**, date 4/20/2026, all 16 lines present, and SSCS
  split the total across BEVS NTX / BEVS TX / DAIRY-WATER / EXP-FOUNTAIN to exactly $532.43.

Built as `DrPepperPdfParser` (+ `PdfTextExtractor`, `NaxmlBuilder`); all **7** sample invoices parse
and reconcile to their printed AMOUNT DUE (`PdfConversionTests`). Dr Pepper matters because it is
the store's most important vendor and does **not** send EDI. Framework:
[PDF_TO_SSCS.md](../PDF_TO_SSCS.md).

**OCR tolerance built in** (the older 240-DPI scans exercised these): the code token may carry
spaces inside its digits (`07 181700 1320/10090 16 1` → stripped); the money separator may be
`:`/`,` not `.` (`36:00` → 36.00); and the NET/TAX columns (unused, most OCR-prone, e.g. `32.S0`)
are read loosely. The invoice-total reconciliation remains the hard gate — a scan that loses a line
is refused, not shipped.

## Handwriting on the paper (added 2026-07-27)

Invoices come back from the store marked up in pen, and OCR transcribes a stroke as a stray token
wherever it crossed the page — **including on two consecutive lines**, when the stroke was long
enough to cross both. Invoice **4646600671** (07/27/26, $1317.21) had a stroke down the AMOUNT
column; PdfPig read `38.00 1 12 25.07 0.00 / 25.07` and `PEG 8 96 / 200.56`. That seventh column
broke the row, the $25.07 line vanished, and the invoice was refused for a total that was short by
exactly that line.

The rule now, in `ReadAmounts` and the unclaimed-row scan:

- The priced row is read **column by column**, not with one fixed pattern. A token carrying **no
  digit at all** is ink, not data, and is discarded before the columns are counted.
- A mark that lands in white space or on the unused **NET/TAX** columns is dropped **silently** —
  it hides nothing, so it is not the operator's problem and raises no warning.
- A mark touching **WHSLE, CASES, UNITS or AMOUNT** leaves a column that is not clean money or a
  clean integer. That is `PDF_UNREADABLE_LINE`: refused, with the item and the raw line quoted.
- A mark through the **barcode** is the dangerous one — the money still reconciles, so the total
  gate is blind to it. The tell is a **priced row no product line claimed**; the parser scans for
  those after the loop and refuses with the damaged line quoted.

Re-scanning does not help here, because the ink is on the paper. The message says to compare the
line with the invoice and key it by hand if the figure is genuinely covered.

## Page handling (do this first)

Parse **only the invoice page(s)**. **Ignore** any page titled **"SKIS DEBIT SCAN SHEET"** — a
barcode sheet appended *after* the invoice (e.g. page 2 of example 7). Do not read past it; its
"invoice #" and barcodes are OCR-garbled and must not feed line items.

**Multi-page invoices:** the scanner normally fits the whole invoice on one page, but a very long
invoice may be cut by staff into 2–3 searchable pages. Concatenate the invoice pages, take the
single header (page 1) and single `AMOUNT DUE` (last invoice page, before any scan sheet), and let
the total checksum catch a missing page. Preferred fix is still to train staff to scan the invoice
as one page.

## Extra charges

**None.** Across all 7 samples Dr Pepper invoices carry no fees or deposits with dollar amounts.
The non-priced `PALLET`/`SHELL` equipment lines are not charges and are skipped.

## CREDITS section (added 2026-07-29, invoice 4646105204)

A delivery that takes goods back prints a second section under a **`CREDITS`** banner, with its own
header and **a different set of columns**:

```
CREDITS
DESCRIPTION UPC/SKU
CASES UNITS NET TAX AMOUNT        <- no WHSLE column
12z12p Zsgr DP 078000035261/10136728
0 1 6.37 0.00 (6.37)              <- amount in brackets, i.e. negative
```

Rules:

- Credits are **line items**, not a footer. Without them the invoice cannot reconcile — 4646105204's
  sales come to $1487.84 against a printed **Amount Due $1468.73**, the difference being 3 credits.
- The sign goes on the **count**, not the money: `Units = -1` at a positive `UnitCost` of 6.37.
- A credit has **no WHSLE column**, so its retail seed comes from `RetailSeed.FromCost` (40%) rather
  than the WHSLE rule. Returns are of items the store already stocks, so SSCS overrides it anyway —
  it exists only so the field is never `0.00`.
- The `SALES SUMMARY` under the credits also prints a bracketed figure (`Cases 0 (19.11)`). It has
  two columns, not five, so it is not mistaken for a credit row.
- This ticket prints **two invoice numbers** under `INVOICE` (`4646105204` *and* `4646105205`) —
  evidently one document for the sale and one for the credit, though a single Amount Due totals
  them. Everything goes out under the **first**, and a `PDF_CREDIT_LINES` warning says so.
  **Open question: whether SSCS wants the credit filed under its own number instead.**

## Scan damage to the code token (added 2026-07-29)

Three things the 2026-07 scans do to the `<UPC>/<SKU>` token that the older samples never did:

| Seen | Handling |
|------|----------|
| `0.5L12 VC PureCocoDb898999010007/20000642` | The space before the barcode is **lost**. The split is allowed with no space *only when a letter sits immediately before the code* — a bare digit would let the split land part-way through the barcode and yield a shorter, wrong UPC that still passes every length check. |
| `071817033291/100,00862` | Punctuation dropped into the **SKU**. The SKU is never written to the XML, so this is ignored outright. |
| `071817` + `` ` `` + `000453/10000069` | A mark inside the **barcode**. Stripped — but then the **check digit must pass**, because a mark that lands on a barcode may have taken a digit out with it. `071817000453` checks out, so it ships. If it did not, the invoice stops (`PDF_UNREADABLE_LINE`). This is stricter than the plain-digit case, which only warns. |
| `600m112 PnFiel Limda0i8000200.287/...` | A **letter** inside the barcode (the 7 read as an `i`). Letters are not noise and are never stripped: refused. |

## Header

| XML | Source on PDF |
|-----|---------------|
| `Supplier/Name` | fixed `DR PEPPER` |
| `InvoiceNumber` | the number under `INVOICE` (10 digits, e.g. `4643517800`) |
| `InvoiceDate` | `DATE: MM/DD/YY` → ISO `yyyy-MM-dd` (samples span 2016–2026; always MM/DD/YY) |
| `TotalInvoiceDueAmt` | `AMOUNT DUE $NNNN.NN` (also appears as `TOT SALE <cases> <units> <total>`) |

## Line items

Column header on the invoice: `WHSLE  CASES  UNITS  NET  TAX  AMOUNT`.
Each product spans **two text lines**:

1. `<description> <UPC(12)>/<SKU>`   e.g. `20z24 DrPep          078000082401/10001195`
2. `<WHSLE> <CASES> <UNITS> <NET> <TAX> <AMOUNT>`   e.g. `56.00  4 96 32.50 0.00  130.00`

**Skip** (not line items):
- package-group headers, e.g. `20 OZ PLASTIC BTL LS 24`
- subtotal lines labelled **PEG / PKG / PRO / RAG / PRG** (label is OCR-variable)
- non-priced equipment lines: `PALLET…`, `SHELL PET…`, `SHELL PLASTIC…`

## Field mapping (PDF → XML)

| XML field | From | Rule |
|-----------|------|------|
| `InvoiceUnitId` | the 12-digit UPC before the `/` | sent **whole, check digit included** — SSCS drops it itself |
| `InvoiceUnitDescription` | text before the UPC | as-is; non-ASCII stripped. No length limit |
| `InvoiceUnitQty` | **UNITS** | **never CASES** |
| `InvoiceUnitCost` | **AMOUNT ÷ UNITS** | 4 decimals |
| `RetailPrice` | **WHSLE × CASES ÷ UNITS** | 2 decimals, no cap |

The SKU after the `/` is no longer written — SSCS matches on the barcode and the field was cosmetic.

SSCS **derives** Ex Cost (units × unit cost, rounded per line) and Margin, and for **existing**
items replaces the description and retail from its own master. Observed 2026-07-25: the 20oz
DrPep line showed SSCS's own Ea List **$3.19**, not the file's WHSLE-derived $2.33. So the
WHSLE-derived retail is only a **seed for new items** — but keep sending it, because a $0.00 would
let an unpriced new item ring up free.

## Checksums (use to catch extraction errors)

- Per line: `AMOUNT = CASES × NET`.
- Whole invoice: `Σ AMOUNT = AMOUNT DUE` = `Σ` package subtotals. (Example 7 sums to $532.43.)
- After conversion: `Σ round(units × unit cost, 2)` must still equal AMOUNT DUE. Verified 16/16 on
  example 7 even though 14 of its lines do not divide evenly.

## Gotchas

- **BIB** = "bag-in-box" fountain syrup (e.g. `5GBIB DrPep`): high cost (~$115.95), SSCS books it
  under **EXP-FOUNTAIN**. Its retail now goes out as $115.95 — the retired output's 4-digit field
  forced anything over $99.99 to `0000`, and XML has no such cap.
- **OCR digit errors:** example 5's Evian UPC came out `079198…` vs the correct `079298…`;
  example 3's Squirt SKU was off by a digit. The check-digit test catches most of these and records
  a `PDF_UPC_CHECK_DIGIT` warning, but a mis-scan that still computes is possible — the total
  checksum will NOT catch a wrong UPC.
- Company name sometimes OCRs as "Dr Pepper **Battling** Company" — don't key off it; the layout is
  the identifier, and the supplier name we write is fixed.

## Reference sample

`~/Downloads/Dr pepper example*.pdf` (7 samples). The example 7 text is pinned verbatim in
`PdfConversionTests`, together with the 16 Ex Cost figures live SSCS displayed for it.
