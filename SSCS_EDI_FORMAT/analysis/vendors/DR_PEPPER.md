# Vendor profile: Dr Pepper

**Vendor:** Dr Pepper Bottling Company of Texas.  **Supplier name written:** `DR PEPPER`.
**Input:** searchable PDF, one invoice per file.

**Status: IMPLEMENTED & VERIFIED.** Confirmed in live SSCS twice:

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
