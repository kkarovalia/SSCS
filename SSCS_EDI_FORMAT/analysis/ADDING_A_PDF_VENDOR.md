# Adding a PDF vendor — complete standalone guide

**Read this if you are an AI or developer adding support for a new vendor's PDF invoice.**
It is self-contained: everything you need to go from a vendor PDF to a file SSCS accepts is here.
You do not need to read any other document first.

---

## 1. What you are building

A store receives paper/PDF invoices from vendors. SSCS (Computerized Daily Book, `sunrayasp.com`)
is the accounting system that has to book them. Your job:

```
vendor PDF  →  extract 4 header fields + 5 fields per line  →  NAXML XML  →  emailed to SSCS
```

**You only write the extraction.** The XML writing is already done — call `NaxmlBuilder.Build()`.
Never hand-format XML in a vendor parser.

### How the file reaches SSCS

Every store has its own **EDI email address**. You email the `.xml` to that address and SSCS picks
it up. **Which store the invoice books against is decided entirely by which address it was sent
to** — nothing inside the file selects a store. (Verified 2026-07-25: files carrying store 19491
were emailed to store 14's address and both landed in store 14.)

So do **not** spend effort extracting the ship-to address. Leave `ShipToInfo.Unknown`.

---

## 2. The only data you must extract

### Header — 4 fields

| Field | Notes |
|---|---|
| **Supplier name** | A short, **stable** string. Reuse the vendor's existing 6-char code if it already has one in SSCS (`DRPEP`, `FRITOL`, `GSC`). SSCS maps it to a Vendor ID via its *Map Supplier to Vendor* screen. An unknown name still imports; the operator picks from a dropdown. |
| **Invoice number** | Free text. Letters are fine (`IN-112`). SSCS does not validate it. |
| **Invoice date** | Real date. If the PDF genuinely has none, pass `null` — SSCS then dates it on the import day. **Never guess a date.** |
| **Invoice total** | The printed grand total. Used as your checksum and written into the file. Negative for a credit invoice. |

### Line — 5 fields

| Field | Notes |
|---|---|
| **GTIN** | The whole barcode, check digit included. See §3. |
| **Description** | Free text, any length. Only seeds new items. |
| **Units** | Individual units. **Never cases.** Negative on a credit line. |
| **Unit cost** | Cost of **one unit**, 4 decimals. **Never case cost.** See §4. |
| **Retail price** | What it **sells** for — not cost. Never zero if avoidable. See §5. |

That is the entire model (`Invoice` / `InvoiceLine` in `EdiToSscs.Core`).

---

## 3. Barcodes — SSCS drops the last character itself

**Verified in live SSCS, 2026-07-25.** On import SSCS stores the barcode **minus its last
character** as the item's ID/PLU:

```
you send 600609100271   (UPC-A, 12 digits)  →  SSCS stores 60060910027    (11 chars)
you send 6917673180912  (EAN-13, 13 digits) →  SSCS stores 691767318091   (12 chars)
```

### Rules

- **Send the barcode exactly as printed.** Do not drop the check digit. Do not strip a leading zero.
- If a source only gives you the 11-digit body (no check digit), call `Upc.AppendCheckDigit()` to
  put it back — otherwise SSCS drops a real digit and you land on a nonexistent item.
- Validate with `Upc.IsValidUpcA12()` and warn when it fails. The check digit is a free error
  detector: a single mis-scanned digit almost always breaks it.
- If a code is not a barcode at all (a store PLU, an alphanumeric vendor code), append one `0` so
  SSCS's drop returns the original code.

### If the PDF prints barcodes but the digits are unreadable

Decode the bars. It is more reliable than OCR, because the check digit makes it self-validating —
it either reads correctly or fails loudly, where OCR silently gives you a wrong digit.

Measured on real invoices:

```
native PDF, barcode images   58 / 58 decoded, 0 wrong
400 dpi scan                 71 / 72 decoded, 0 wrong
```

On one line the barcode disagreed with the vendor's own data file — the **barcode was right** and
the vendor's typed digit was wrong.

---

## 4. Money — unit cost only, never case cost

**This is the single easiest thing to get wrong.** Invoices print costs per *case*, and SSCS wants
per *unit*.

### Convert whatever the invoice gives you

| The PDF prints | Do this |
|---|---|
| unit cost directly | use it |
| **case cost + pack size** | `unit cost = case cost ÷ pack size` |
| **extended (line total) + units** | `unit cost = extended ÷ units` |
| case cost + cases, no pack size | ⚠️ you cannot compute units — see below |

And for quantity:

| The PDF prints | Do this |
|---|---|
| units directly | use it |
| **cases + pack size** | `units = cases × pack size` |
| cases only, no pack size | ⚠️ see below |

⚠️ **If the invoice gives cases but never tells you how many units are in a case, stop.** Do not
assume 1. Ask the user for a pack-size source (a vendor item list, or the description text like
`20z24` = 24-pack). Guessing here silently doubles or halves the store's inventory.

### Rounding — 4 decimals is safe

**Verified 16/16 against live SSCS.** SSCS rounds `units × unit cost` to the cent **per line**,
then sums. Errors do not accumulate:

```
96 × 1.3542 = 130.0032   →  SSCS shows 130.00
24 × 0.7708 =  18.4992   →  SSCS shows  18.50
12 × 1.7833 =  21.3996   →  SSCS shows  21.40
```

Round unit cost to 4 decimals with `MidpointRounding.AwayFromZero`. A Dr Pepper invoice where 14
of 16 lines did not divide evenly still totalled exactly $532.43.

A cent of drift is acceptable — record a warning naming the difference and write the file anyway.
Store staff can see a cent on the invoice and fix it in SSCS. Do **not** refuse an invoice over it.

### Credits

Negative quantity, positive unit cost. A whole credit invoice is just every line negative plus a
negative total. Nothing else changes.

---

## 5. Retail price — a fallback, and never zero

`RetailPrice` is **what the item sells for**, not what it costs. It has nothing to do with cost.

SSCS already holds the store's own selling prices and **ignores this field for any item it already
knows**. It only matters as the starting price of a **brand-new** item.

**But you must still always send a real number.** A `0.00` would let an unpriced new item ring up
free at the register. So use the best available fallback:

```
1. a printed retail / SRP / "each" column          ← best
2. the vendor's list price per case ÷ units        ← e.g. Dr Pepper's WHSLE column
3. any suggested-retail figure on the invoice
4. nothing available?  →  ask the user (see §9)
```

Fallback figures are often wrong — Dr Pepper's WHSLE-derived retail came out $2.33 against a real
shelf price of $3.19 — and that is fine. It is a placeholder that SSCS overrides for known items
and that staff correct for new ones. **An approximate price is safe; zero is not.**

There is no upper cap. (The retired fixed-width format had a $99.99 limit; XML does not.)

---

## 6. Checksums — mandatory

A parser that cannot detect its own misreading is not finished. Every vendor parser must verify:

```
Σ (line extended amounts)  ==  printed invoice total
```

and **refuse the file** if it does not reconcile beyond a cent per line. A mismatch means a page is
missing, duplicated, or misread — emitting a wrong invoice is far worse than refusing.

Use any second checksum the invoice offers (per-line `amount = cases × case cost`, package
subtotals, a printed total unit count).

**The checksum will not catch a wrong barcode.** Only the check digit does that.

---

## 7. Reading the PDF

Text comes from **PdfPig** via `PdfTextExtractor.ExtractLines()`, which reconstructs visual lines.

### Check what kind of PDF you have first

| Signal | Meaning |
|---|---|
| Text is selectable and clean | Native PDF — extraction is reliable |
| Text is selectable but garbled (`Battling` for `Bottling`, `SALES ROUTE;`) | It is an **OCR'd scan** — expect digit errors |
| No selectable text | Image-only — needs OCR or barcode decoding |

Dr Pepper's "searchable" PDFs are OCR scans. Build tolerance in:

- digits with inserted spaces: `07 181700 1320/10090` → strip spaces before parsing
- money separators OCR'd as `:` or `,`: `36:00` → `36.00`
- columns you do not use (NET, TAX) — parse them loosely, they are the most corrupted
- non-ASCII junk (`16z12C4Pr£Frznfmscl`) — `NaxmlBuilder` strips it, but do not key logic off it

### Skip non-product rows

Typical things that look like line items but are not: package-group headers, subtotal rows
(`PEG`/`PKG`/`PRO`/`RAG`), equipment lines (`PALLET`, `SHELL`), and appended barcode/scan sheets.
Stop parsing at the invoice total.

---

## 8. Where the code goes

```
PdfTextExtractor.ExtractLines(bytes)        → List<string> visual lines   (shared, done)
YourVendorPdfParser.Parse(lines, warnings)  → Invoice                     ← YOU WRITE THIS
NaxmlBuilder.Build(invoice)                 → byte[] XML                  (shared, done)
```

1. Write `converter/src/EdiToSscs.Core/<Vendor>PdfParser.cs`, modelled on `DrPepperPdfParser.cs`.
2. Give it a `public const string SupplierName`.
3. Wire it into `ConversionService.ConvertPdf` and add a dropdown entry in `MainForm`.
4. Write a profile in `analysis/vendors/<VENDOR>.md`: row shape, column meanings, skip rules,
   gotchas.
5. Add tests in `PdfConversionTests` using **real extracted text pasted verbatim** — not
   hand-written samples. Assert the header, the line count, and the money.

**Do not** build a user-configurable vendor→code mapping. That was built once and deliberately
removed; each PDF vendor gets its own parser and a hardcoded supplier name.

---

## 9. Verifying a new vendor

SSCS **consumes** these files; it never produces a reference you can diff against. So verification
is a live round-trip:

1. Convert a real invoice.
2. Have the user email the `.xml` to a store's EDI address.
3. Confirm on screen:
   - **Paidout tab** — invoice #, vendor, date, total, and the Verify Total matching
   - **Line Items tab** — ID/PLU, Qty, Ea Cost, Ex Cost on a few lines
4. **Pin the confirmed numbers in a test.** `PdfConversionTests` stores the 16 Ex Cost figures live
   SSCS displayed for Dr Pepper example 7 — that is the pattern to copy.

⚠️ Importing creates a **real payable** in a live accounting system. Use a test invoice number and
tell the user to delete it afterwards.

### Ask the user when

- the invoice gives cases but no pack size (§4)
- there is no retail/SRP column anywhere (§5)
- the invoice has a fee or deposit **with a dollar amount** — those must become line items, or the
  total will not reconcile. (Dr Pepper has none; FritoLay's `CSERVICE CHARGE` is a label carrying
  no money at all.)
- anything at all is ambiguous about which column is cost vs retail

---

## 10. The output format, in full

For reference only — `NaxmlBuilder` writes this. ASCII, CRLF, trailing newline.
`ident="otrack"` and `ident="sscs1"` are fixed constants, not per-vendor.

```xml
<?xml version="1.0" ?>
<NAXML-BusDoc version="1.5">
  <Parties>
    <Supplier><Name identType="CUSTOM" ident="otrack">DR PEPPER</Name></Supplier>
    <Buyer><Name identType="CUSTOM" ident="sscs1">SSCS</Name></Buyer>
    <ShipTo><Name ident="0">SSCS</Name></ShipTo>
  </Parties>
  <Invoice>
    <Supplier><Name identType="CUSTOM" ident="otrack">DR PEPPER</Name></Supplier>
    <Location>
      <Name>No name</Name>
      <Address></Address><City></City><State></State><PostalCode></PostalCode>
      <OrganizationId identType="Store Number" assignedBy="Seller">0</OrganizationId>
    </Location>
    <InvoiceNumber>4643517800</InvoiceNumber>
    <InvoiceDate>2026-04-20</InvoiceDate>
    <InvoiceDetail>
      <LineItem count="1">
        <InvoiceUnit>
          <InvoiceUnitId identType="GTIN">078000082401</InvoiceUnitId>
          <InvoiceUnitDescription>20z24 DrPep</InvoiceUnitDescription>
          <InvoiceUnitQty cstoreUOMBasis="EA">96</InvoiceUnitQty>
          <InvoiceUnitCost>1.3542</InvoiceUnitCost>
        </InvoiceUnit>
        <RetailUnitPricing>
          <RetailUnitId identType="GTIN">078000082401</RetailUnitId>
          <RetailUnitQty identType="GTIN">1</RetailUnitQty>
          <RetailPrice>2.33</RetailPrice>
        </RetailUnitPricing>
      </LineItem>
      <!-- LineItem count runs 1..n in order -->
      <InvoiceSummary><InvoiceTotals>
        <TotalLineItemNetAmt identType="Debit">532.43</TotalLineItemNetAmt>
        <TotalInvoiceDueAmt identType="Debit">532.43</TotalInvoiceDueAmt>
      </InvoiceTotals></InvoiceSummary>
    </InvoiceDetail>
  </Invoice>
</NAXML-BusDoc>
```

`<InvoiceDate>` is **omitted entirely** when there is no date. `RetailUnitQty` is always `1`.

---

## 11. Quick checklist before you call it done

- [ ] Units are individual units, never cases
- [ ] Unit cost is per unit, never per case
- [ ] Barcodes go out whole, check digit included
- [ ] Retail is populated on every line and is never `0.00`
- [ ] Lines reconcile to the printed total, and the parser refuses when they do not
- [ ] Non-product rows and any appended scan sheet are skipped
- [ ] Real extracted text is pinned in tests
- [ ] A real invoice has been imported into live SSCS and read back correctly
- [ ] The confirmed SSCS figures are asserted in a test
- [ ] `dotnet test tests/EdiToSscs.Tests -c Release` passes
