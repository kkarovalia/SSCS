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

### Each-list — always sent, never zero if avoidable

`RetailPrice` is what the item **sells** for, not what it costs. SSCS holds the store's own selling
prices and **ignores this field for any item it already knows**, so it only matters as the starting
price of a brand-new item.

It is still always populated. A zero would let an unpriced new item ring up free, so a
vendor-suggested figure is the safer default even when approximate:

- **EDI path** — the each-list field on the row.
- **PDF path** — Dr Pepper's WHSLE column (its list price per case) divided out per unit.

There is no longer a $99.99 cap; the old 4-digit field forced one, XML does not.

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
