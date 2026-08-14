# Format specification — vendor invoices in, NAXML out

The converter reads a vendor's fixed-width EDI file (or a vendor PDF) and writes an
**NAXML-BusDoc 1.5 invoice**, which is what SSCS ingests. It no longer produces the fixed-width
"SSCS EDI" text file; that output was retired on 2026-07-25 and the reasoning is at the bottom of
this document.

Everything below was verified against real files and, where it says so, against live SSCS.

---

## Part 1 — the output: NAXML

### Delivery

**SSCS gives every store its own EDI email address, and routes purely on which address the file
arrives at.** Verified 2026-07-25: two files whose store fields said store 19491 were emailed to
store 14's address and both landed in store 14 (`q:\cdbdata\edi\014\`). Nothing inside the file
selects a store, so `ShipTo` / `Location` are written for human readability only.

### Document shape

Copied from a file VM Novelties sends SSCS in production, which was read back out of live SSCS
line by line and matched exactly. It is not inferred from the NAXML standard.

```xml
<?xml version="1.0" ?>
<NAXML-BusDoc version="1.5">
  <Parties>
    <Supplier><Name identType="CUSTOM" ident="otrack">GHRA</Name></Supplier>
    <Buyer><Name identType="CUSTOM" ident="sscs1">SSCS</Name></Buyer>
    <ShipTo><Name ident="0">SSCS</Name></ShipTo>
  </Parties>
  <Invoice>
    <Supplier><Name identType="CUSTOM" ident="otrack">GHRA</Name></Supplier>
    <Location>… address fields …
      <OrganizationId identType="Store Number" assignedBy="Seller">0</OrganizationId>
    </Location>
    <InvoiceNumber>0000131454</InvoiceNumber>
    <InvoiceDate>2018-08-13</InvoiceDate>
    <InvoiceDetail>
      <LineItem count="1">
        <InvoiceUnit>
          <InvoiceUnitId identType="GTIN">012000001574</InvoiceUnitId>
          <InvoiceUnitDescription>1L PL AQUA WTR 1/15</InvoiceUnitDescription>
          <InvoiceUnitQty cstoreUOMBasis="EA">30</InvoiceUnitQty>
          <InvoiceUnitCost>1.1000</InvoiceUnitCost>
        </InvoiceUnit>
        <RetailUnitPricing>
          <RetailUnitId identType="GTIN">012000001574</RetailUnitId>
          <RetailUnitQty identType="GTIN">1</RetailUnitQty>
          <RetailPrice>2.49</RetailPrice>
        </RetailUnitPricing>
      </LineItem>
      …
      <InvoiceSummary><InvoiceTotals>
        <TotalLineItemNetAmt identType="Debit">9509.02</TotalLineItemNetAmt>
        <TotalInvoiceDueAmt identType="Debit">9509.02</TotalInvoiceDueAmt>
      </InvoiceTotals></InvoiceSummary>
    </InvoiceDetail>
  </Invoice>
</NAXML-BusDoc>
```

ASCII, CRLF line endings, trailing newline. `ident="otrack"` and `ident="sscs1"` are constants
copied from the working file; they are not per-vendor.

### Field rules

| Element | Rule |
|---|---|
| `Supplier/Name` | The vendor's own 6-character code from its EDI header (`GHRA`, `PEPSI`, `FRITOL`, `GSC`), or `DR PEPPER` for the PDF path. See *Vendor resolution*. |
| `InvoiceNumber` | Free text — SSCS does not validate it. Letters are fine (`IN-112`). |
| `InvoiceDate` | ISO `yyyy-MM-dd`. **Omitted entirely** when the vendor's file has no date; SSCS then dates the invoice on the day it imports it (verified 2026-07-25). |
| `InvoiceUnitId` / `RetailUnitId` | The **whole** barcode, check digit included. See *Barcodes*. |
| `InvoiceUnitQty` | Individual units, never cases. Negative on a credit line. |
| `InvoiceUnitCost` | Cost of one unit, 4 decimals. See *Money*. |
| `RetailUnitQty` | Always `1`. Shows in SSCS as the Pack Size column. |
| `RetailPrice` | Each-list — what the item **sells** for. See *Each-list*. |
| `TotalLineItemNetAmt` / `TotalInvoiceDueAmt` | The invoice's own printed total, 2 decimals, negative for a credit invoice. |

### Barcodes — SSCS drops the last character itself

**Verified in live SSCS, 2026-07-25.** On import SSCS stores the GTIN it is given **minus its last
character** as the item's ID/PLU:

```
sent 600609100271  (UPC-A, 12 digits)  → ID/PLU 60060910027   (11 chars)
sent 6917673180912 (EAN-13, 13 digits) → ID/PLU 691767318091  (12 chars)
```

Two consequences:

1. A 12-digit UPC-A lands on **exactly the same 11-character ID** the retired fixed-width output
   produced, because that output had to drop the check digit itself to fit an 11-character field.
   So a vendor can be moved from the old path to XML without creating duplicate item records.
   `XmlConversionTests.EveryBarcodeLandsOnTheSameSscsItem` asserts this against every fixture that
   has a paired retired-output file.
2. A 13-digit EAN-13 lands on a 12-character ID, which the old 11-character field could not hold
   at all. That is the bug this rewrite exists to fix — see *Why the fixed-width output was
   retired*.

**Never pre-drop a check digit and never strip a leading zero.** Converting a fixed-width input,
whose code field already had its check digit removed, we compute the check digit back on
(`Upc.AppendCheckDigit`) so SSCS's drop returns the original code.

#### SSCS drops the last character and nothing else

**Verified in live SSCS, 2026-07-26.** Blue Bell prints its UPC column as the 11-digit UPC-A body
padded to 12 with a leading zero (`007189905101` = `0` + `07189905101`), and only 3 of its 21 codes
pass a UPC-A check-digit test — they are not barcodes. A two-line test invoice carrying the same
pint under both encodings was imported into BEST STOP #14:

```
sent 007189905101 (as printed)          → ID/PLU 00718990510 — flagged "New Item",
                                          department "Unknown items"
sent 071899051015 (check digit restored) → ID/PLU 07189905101 — matched the store's existing
                                          item, department "ICE CREAM"
```

So the leading zero is **not** stripped, and a padded body must have its check digit restored or it
lands one digit short and creates a duplicate item. Two lessons for any future vendor:

- Run a vendor's codes through `Upc.IsValidUpcA12` before assuming the "UPC" column holds barcodes.
  If far fewer than all of them pass, it is a body or an internal code, not a GTIN. Measured:
  VM Novelties 6/6 pass (real barcodes), Blue Bell 3/21 (chance) — and 21/21 after the repair.
  **The full decision table — every code shape seen and what to send for it — is in
  [ADDING_A_PDF_VENDOR.md](./ADDING_A_PDF_VENDOR.md) §3. Use it instead of a live test.**
- The cheapest way to settle it is one tiny test invoice carrying both candidate encodings of the
  same product — the Line Items tab shows both resulting IDs side by side, and "New Item" tells you
  which one is wrong. Pinned in `BlueBellPipelineTests.MatchesWhatLiveSscsDidWithBothBarcodeForms`.

### Money — 4-decimal unit costs are safe

**Verified 16/16 against live SSCS, 2026-07-25.** SSCS rounds `units × unit cost` to the cent
**per line**, then sums. Errors do not accumulate:

```
96 × 1.3542 = 130.0032  →  SSCS shows 130.00
24 × 0.7708 =  18.4992  →  SSCS shows  18.50
12 × 1.7833 =  21.3996  →  SSCS shows  21.40
```

A Dr Pepper invoice where 14 of 16 lines do not divide evenly still totalled exactly $532.43.

A case cost that does not divide evenly into its pack can still leave a cent on the line
(2 of 772 real rows). The converter records a `TOTAL_MISMATCH` warning naming the difference and
writes the file anyway — a cent is for the operator to correct in SSCS.

### Quantity — individual sellable units, never cases

**Verified in live SSCS, 2026-07-26.** `InvoiceUnitQty` is the number of things the store actually
sells — one pint, one ice cream sandwich — and `InvoiceUnitCost` is the cost of one of those. **A
box, carton or case is never a unit.**

The trap is that a vendor's own quantity column need not be in units, and one vendor can use both
meanings in the same column. Blue Bell invoice 033100305634 read back off the Line Items tab:

```
invoice line                              SSCS holds
1/2 GAL GR SUP    QTY  4 @ 6.64   26.56   07189903720   4.00   6.64    26.56   (already units)
PT GR SUPREME     QTY 90 @ 2.91  261.90   07189905101  90.00   2.91   261.90   (already units)
DZ VANILLA SAND   QTY  2 @ 6.48   12.96   07189921122  24.00   0.54    12.96   (a carton of 12)
```

Nothing catches this automatically: `EXTENDED` comes off the invoice either way, so the money
reconciles whichever reading you take, and only the quantity and per-unit cost are wrong. The
visible tell is the each-list — a 40% margin on the carton price puts $11.49 on a single ice cream
sandwich instead of $0.99.

So for every vendor, establish what one unit is **per product**, and hold the pack size somewhere
reliable. Blue Bell's lives in its parser's catalogue rather than being read from the printed
description, because "DZ" is exactly what a scanner mangles (it read `07 VANILLA SAND` on one
ticket). See `vendors/BLUE_BELL.md` §1.

A partial case comes back as a fraction — Blue Bell writes `-11/12` for eleven bars out of a
twelve-bar carton — and needs no special handling once the per-unit cost is right:
`-5.94 ÷ 0.54 = -11`.

### What SSCS keeps and what it overrides

For an item already in its master, SSCS honours **only the GTIN, quantity and unit cost** from the
file. It substitutes **its own description, retail and department**. Verified twice: it displayed
`JANJOO 8PIN 5FT CABLE` where the file said `JANJOO 8-PIN CABLE 5FT`, and `BLUE BELL PT GR SUPREM`
in department `ICE CREAM` where the file supplied different text and no department at all.

It derives Ea Cost, Ex Cost and Margin % itself. So description and retail are best-effort seeds
that matter only for a brand-new item — which is also why they must still be sane rather than empty.

### Each-list — always sent, never zero if avoidable

`RetailPrice` is what the item **sells** for, not what it costs. SSCS holds the store's own selling
prices and **ignores this field for any item it already knows**, so it only matters as the starting
price of a brand-new item.

It is still always populated. A zero would let an unpriced new item ring up free, so a
vendor-suggested figure is the safer default even when approximate:

- **EDI path** — the each-list field on the row.
- **PDF path** — Dr Pepper's WHSLE column (its list price per case) divided out per unit.

There is no longer a $99.99 cap; the old 4-digit field forced one, XML does not.

### Scanned PDF headers and picture-primary input

For every scanned PDF vendor, the invoice number and date that reach NAXML are typed by the
operator into empty boxes. Neither field has an invoice checksum; a plausible OCR error can pass
silently and put a delivery under the wrong reference or accounting period. The type passed to the
prompt, `InvoiceDetailsRequest`, intentionally contains neither an invoice-number value nor a date
value. Parsed dates may still separate sections or support parser checks, but cannot pre-fill the UI
or override the keyed date.

Blue Bell's normal source is one Gemini request containing every page picture. Each row keeps
`upc`, `quantity`, `price`, `extended` and `packSize` as strings, so a printed `-11/12` return is
not rounded or normalised before Core sees it. A postable row needs the twelve printed padded UPC
characters, a quantity, and either PRICE or EXTENDED; TOTAL, unit counts, page counts and arithmetic
are not reply gates. Core restores the real UPC-A through `BlueBellPdfParser.ToGtin`, expands a
model-read pack 12, books `n/12` as `n` bars, and seeds retail at 40%. The catalogue is a
warning-only pack cross-check and never repairs an AI value.

Missing rows and disagreements in row math, section TOTAL, printed quantity or known pack size are
warnings on a written file. An unreadable TOTAL uses the signed line sum and warns. The sole
business refusal is no postable row. Sales and Returns remain one payable and all page images are
read together; handwriting and PICK LIST pages are excluded by the vendor prompt. Unavailable AI
may use the unchanged legacy OCR parser only after explicit once-per-batch approval, while an AI
read that reached the page and still found nothing postable requires a rescan.

Chubiwinos' normal source is likewise one Gemini request containing every page picture. Its
vendor-specific row contract contains `itemNumber`, `description`, the printed `barcode`, `quantity`
(QTY), `price` (PRICE), `amount` (rightmost TOTAL) and SALES/DUMP direction. Every numeric field is
text. The schema offers no case, pack or retail field because none exists on the paper. A postable
row needs an 11-digit UPC-A body or a full 12- or 13-digit printed barcode, a positive whole QTY,
and either PRICE or TOTAL; footer
totals, section counts, row arithmetic, vendor markers and the barcode check digit are not reply
gates.

Core removes printed hyphens. A whole UPC-A or EAN-13 is sent unchanged; when the vendor prints an
11-digit UPC-A body, Core appends its one mathematically determined check digit before XML
(`85088300206` → `850883002062`). It never pads or truncates a full code, consults
`ChubiwinosCatalog`, or asks for a barcode on the AI path. A printed failed check digit is sent as
read and warned about. DUMP rows become negative units at positive
cost on the same payable. PRICE is already unit cost; when PRICE alone is unreadable, TOTAL / QTY is
used to four decimals and warned. Retail is seeded through
`RetailSeed.FromCost(cost, ChubiwinosPdfParser.RetailMargin)` at 50%.

Each section's printed `Totals:` quantity and money plus SALES, CREDITS, NET AMOUNT, SALES TAX,
TOTAL DUE, TOTAL PAYMENT and INVOICE BALANCE are checks only. Disagreements warn and never repair a
row or refuse the file. The NAXML invoice total is always the signed sum of the lines being written.
The sole parser refusal is no postable row. A model read that reached the pictures but still has no
postable row after its retry requires a rescan; unavailable AI may use the unchanged legacy OCR
parser and catalogue only after explicit once-per-batch approval.

Bimbo's normal product source is one Gemini request containing every page picture, not the PDF OCR
text layer. The vendor-specific response returns each row's item number, description, printed
ten-digit UPC body, QTY, PRICE, wholesale AMOUNT, delivery/return direction, band totals, TICKET
TOTALS and CASH DUE. Core parses those strings and writes the invoice without opening a line-field
dialog. `QTY × PRICE`, band, ticket-unit and ticket-money disagreements are
`ConversionWarning`s on a file that still gets written. A row with no usable ten-digit UPC body,
QTY or wholesale AMOUNT is dropped and named with its printed money in a warning; only a reading
with no postable row at all is refused. The complete value written to NAXML is
`0 + body + computed check digit` (for example `74323-09183` → `074323091830`). There is no product
or manufacturer-prefix table in source and no second barcode read.

Because the printed Bimbo body has no check digit, this intentionally leaves a wrong ten-digit AI
reading undetectable; it can create a duplicate item in SSCS. That tradeoff was explicitly accepted
by the store owner on 2026-08-13 to avoid questions and whole-ticket refusals. Do not add barcode
verification, suspicious-prefix warnings or a catalogue back. If AI is unavailable before reading,
the operator may approve the unchanged legacy text parser once per queue batch. A successful API
reply is accepted when it contains at least one postable row plus TICKET TOTALS; if it cannot do that
after the bounded retry it is a rescan, never a mixture of AI and OCR figures.

Red Bull uses its own picture-primary contract as of 2026-08-14. Every page picture in one PDF is
sent in one request, and the reply carries the physical product rows in order. The response keeps
`QTY` (`cases`) and `UNITS` as separate text fields, keeps every money field as text, and requires
the delivery/credit direction while retaining a deterministic sign fallback. A postable row is
exactly a twelve-digit barcode, a nonzero whole `UNITS` value and a positive rightmost `TOTAL`.
The reply gate requires one such row and nothing else: neither footer totals nor `Units Delivered`
can buy a retry or refuse a readable ticket.

Core sends `UNITS` directly as Qty and calculates unit cost as `TOTAL / UNITS`, rounded to four
decimals. Credit rows become negative quantities at positive costs. A failed UPC-A check digit is
sent unchanged and warned about; the AI path never consults `RedBullCatalog`, the printed bars or a
single-digit repair. Missing rows, row-math disagreements, total disagreements and unit-count
disagreements are warnings on a written file. The sole business refusal is a reading with no
postable row. Invoice number and date remain absent from the schema and are typed into empty boxes.

If AI is unavailable, the operator may approve the unchanged legacy Red Bull OCR parser once for
the whole queue batch. If the API reached the page but still returned no postable row after its
bounded retry, the file asks for a rescan and never mixes OCR values into the partial AI reading.
The 2025-11-24 live SSCS result still establishes Red Bull's barcode, unit, cost, credit and
one-payable rules; the new AI path itself had not been batch-audited against the sample PDFs when
this contract was recorded on 2026-08-14.

Dr Pepper uses the same value boundary with a vendor-specific schema. Gemini's primary request sees
the complete invoice and returns physical rows, package subtotals, TOTAL SALE, sales/credit summary
and AMOUNT DUE as strings. OCR is used only to locate exact highlight coordinates. OCR values must
never override, reject, confirm, or modify Gemini values. The legacy OCR parser runs only after
explicit once-per-batch approval when Gemini is unavailable.

Every Dr Pepper UPC is read independently three times: full-invoice primary, full-page barcode-only
verification without pass-one UPCs, and isolated enlarged UPC cells cropped from Gemini's geometric
cell boxes rather than OCR. Unanimous exact 12-digit UPC-A
agreement is required; no catalogue, prefix, padding, repair or majority vote is allowed. Invoice
number and date are absent from the AI schema and are always typed into empty boxes. The legacy
parser may read AMOUNT DUE for validation but must never ask the operator to type it or derive
missing line values from it. Routine credits serialize as negative quantities with positive costs
and do not turn a successful conversion amber.

A Dr Pepper value question is permitted only with three visual pieces: the cropped two-line
product, a box around only the requested field, and the invoice's photographed three-line section
strip (SALES/CREDITS banner plus both column-header lines). Dr Pepper has no synthetic header and no
unmarked whole-page fallback; missing geometry is a rescan.

### Vendor resolution

SSCS keeps a **Map Supplier to Vendor** table keyed on the `Supplier/Name` text (truncated to
~30 characters). The retired EDI path fed that same table its 6-character vendor codes, so those
rows already exist:

```
DRPEP → DR. PEPPER      FAUSDI → FAUST      GSC → GSC
FRITOL → …              SEDHOU → SILVER     PEPSI → …
```

Writing the same 6-character code keeps every existing mapping working with nothing to set up.
An unrecognised name still imports — SSCS resolved `DR PEPPER BOTTLING COMPANY OF TEXAS` to
`DR. PEPPER` on its own — and the operator can always pick the vendor from the dropdown.

---

## Part 2 — the inputs: fixed-width EDI

### File-level structure

| Property | Standard EDI | EDI13 | Ruby Red EDI13 | STRP/Alp |
|---|---|---|---|---|
| Header length | 33 (GHRA/FRITOLAY), 27 (PEPSI) | 33 | 64 | 35 |
| B record length | 76 | 78 | 79 | 75 |
| Code field width | 11 | 13 | 13 | 11 |
| Line endings | CRLF | CRLF | LF | CRLF |

Line 1 is always the header; every following line is a `B` (product) or `C` (charge) record.
Detection is structural, never by filename.

### Headers

**Vendor-style** (standard EDI, EDI13, STRP): `[0..6]` is a 7-character vendor field, left-aligned
and space padded; `[7..]` is a numeric payload of `invoice(10) + optional date + amount(10)`.

| Payload length | Date field | Example |
|---|---|---|
| 26 | 6 digits | GHRA `MMDDYY`; FRITOLAY `YYMMDD` |
| 20 | none | PEPSI — SSCS dates the invoice on import |
| 28 | 8 digits `MMDDYYYY` | STRP |

A 6-digit date is read as `MMDDYY` unless its first two digits exceed 12, which cannot be a month
and therefore means `YYMMDD`. A `13` suffix on the vendor token is a format marker and is stripped.
A 7-character token is truncated to 6 (`FRITOLA` → `FRITOL`), matching how SSCS already names it.

**Ruby Red** (64 bytes): `GHRA13` + company name(30) + reference(10) + `YYYYMMDD`(8) + amount(10).
The company name becomes the supplier name and the reference becomes the invoice number
(`VM NOVELTIES LLC`, `IN-112`).

Amounts carry their sign **inside the zero padding**: `0000-22000` is −$220.00.

### B records — the corrected layout

⚠️ **The field names in earlier revisions of this document were wrong.** They called
`[base+31..base+37)` the line's extended cost and `[base+39..base+45)` the quantity. That is only
true when a line happens to be a single case.

With `base = 1 + codeWidth`:

| Bytes | Field |
|---|---|
| `[0]` | `B` |
| `[1 .. base)` | product code |
| `[base+0 .. base+25)` | description |
| `[base+25 .. base+31)` | vendor item # / SKU |
| `[base+31 .. base+37)` | **cost of ONE CASE**, in cents |
| `[base+37 .. base+39)` | two spaces |
| `[base+39 .. base+45)` | **pack size** — units in a case |
| `[base+45 .. base+50)` | **number of cases** (negative on a credit line) |
| `[base+50 .. base+55)` | each-list / retail, in cents |

So **`units = pack × cases`** and **`unit cost = case cost ÷ pack`**.

Proof the old reading was wrong — totalling the extended-cost interpretation against each file's
own header amount:

| File | Header | Old reading | Corrected reading |
|---|---|---|---|
| FRITOLAY-48690739 | $178.99 | $42.60 ✗ | **$178.99** ✓ |
| PEPSI-80471906 | $1,348.05 | $1,013.90 ✗ | **$1,348.05** ✓ |
| GHRA_120511 | $10,032.86 | $4,774.86 ✗ | **$10,032.86** ✓ |

The retired PDF output was unaffected because it always wrote one case per line, which makes both
readings identical — which is why it verified in SSCS despite the wrong field names.

**One known exception.** Hidden fixture `EDI_GHRA_810738` writes each line's *whole* extended
amount in the case-cost field; its lines only reach the printed −$262.11 read that way, and doing
so also yields clean per-unit prices ($4.54, $4.85, $5.34) where the per-case reading yields none.
`EdiConverter.InterpretMoney` therefore tries the normal reading first and falls back to the
whole-line reading **only when that reconciles the invoice exactly**, recording a
`COST_FIELD_IS_WHOLE_LINE` warning. It cannot silently change a file that already balanced.

### Product codes

A code field becomes a GTIN by this one rule, applied to the trimmed content:

| Content | Result |
|---|---|
| 8 digits | UPC-E → expand to the 11-digit UPC-A body, then append the check digit |
| 12 or 13 characters | sent as it stands |
| 11 digits | append the computed check digit |
| anything else | append `0`, so SSCS's drop returns the original code |

The last row covers store PLUs and alphanumeric vendor codes (`002ZH7S8J999`), which the hidden
fixtures prove exist. An empty code field is passed through empty with a `NON_BARCODE_CODE`
warning — a line with no product id is worth a human look.

#### UPC-E expansion (verified against 40+ fixture records)

The 8 digits are number system `NS`, body `D1..D6`, check digit `C`.

| D6 | UPC-A body |
|---|---|
| 0, 1, 2 | `NS D1 D2 D6 0 0 0 0 D3 D4 D5` |
| 3 | `NS D1 D2 D3 0 0 0 0 0 D4 D5` |
| 4 | `NS D1 D2 D3 D4 0 0 0 0 0 D5` |
| 5–9 | `NS D1 D2 D3 D4 D5 0 0 0 0 D6` |

Example: `03432004` → `03400000320`. `NS` must be 0 or 1. A wrong check digit **stops the whole
file** — the barcode is mistyped and guessing would put stock against the wrong item. An
unsupported number system passes the code through with a warning (the retired output blanked the
field to match a byte fixture; an empty id is worse than an unusual one).

### C (charge) records

**Dropped.** `CSERSERVICE CHARGE` is a 29-byte label with **no amount in it** — FRITOLAY's product
lines already sum to its $178.99 header total. They are counted, not warned about: nearly every
file has one, and a warning that fires every time teaches people to ignore warnings.

### Rejections

| Reason | Condition |
|---|---|
| `EMPTY_FILE` | nothing to convert |
| `ALREADY_CONVERTED` | the file is already NAXML, or is retired fixed-width output |
| `INVALID_HEADER` | line 1 is not a recognised header |
| `NO_PRODUCT_RECORDS` | a header with no `B` records |
| `UNSUPPORTED_RECORD_TYPE` | a record that is not `B` or `C` |
| `UNSUPPORTED_RECORD_LENGTH` | a row whose width does not match the detected format |
| `RUBYRED_SUFFIX_MISMATCH` | a Ruby Red row not ending `00000000  ` |
| `UPCE_INVALID_CHECK_DIGIT` | a short barcode whose check digit does not compute |
| `BLANK_LINE`, `BINARY_CONTENT`, `LONE_CARRIAGE_RETURN` | damaged file |

---

## Why the fixed-width output was retired

The old output had an **11-character** product-code field. That is enough for a 12-digit UPC-A
(drop the check digit) but not for a 13-digit EAN-13, which most imported vape and novelty stock
carries.

VM Novelties' IN-112 invoice made the cost concrete: 53 of its 72 lines are genuine EAN-13s.
Truncated to 11 characters, **72 line items collapsed onto 40 distinct IDs** — eight different
GEEK NEXT flavours all became `69176731657`. SSCS rejected it.

The same file through the NAXML path keeps **all 70 distinct barcodes**, totals $3,649.00 exactly,
and carries its 4 credit lines as negative quantities.

Everything else the fixed-width format forced also disappeared: a 6-character vendor code, a
25-character description limit, a numeric-only invoice number, `MMDDYY` date juggling, a $99.99
each-list cap, and four separate byte layouts to reverse-engineer. NAXML is a published c-store
standard that SSCS already accepts from vendors directly.
