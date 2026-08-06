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
| **Invoice number** | Free text. Letters are fine (`IN-112`). SSCS does not validate it — which also means nothing catches a mis-read. If the number is scanned rather than machine-readable and has no check digit, have the operator type it (`IInvoiceDetailsPrompt`) instead of guessing; Blue Bell's scanner turned `033103035263` into `033105035263` with no sign of trouble. |
| **Invoice date** | Real date. If the PDF genuinely has none, pass `null` — SSCS then dates it on the import day. **Never guess a date.** If the PDF *has* one but the scan wrecked it (`DATE 12-14-Z023`), ask through the same prompt rather than refusing the ticket — the rest of it still reconciles against its own TOTAL. Rules for doing that safely: the parsed value always wins, a repaired reading is only ever a suggestion for the operator to check, and supplying nothing still refuses the file. |
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
- If a code is not a barcode at all (a store PLU, an alphanumeric vendor code), **SSCS stores it
  whole — the drop does not apply.** Verified 2026-07-26: the placeholder `0000` came back as id
  `0000`, on the same import where `862313000326` became `86231300032` and
  `7406189008579` became `740618900857`. So send such a code as you want the id to read, and expect
  a *New Item* in department "Unknown items". (This corrects the older advice to append one `0` and
  rely on the drop; nothing was verified behind it.)

### What a check digit is, and why you never need the vendor to print it

The last digit of a barcode is not data — it is **arithmetic on the other digits**. So a vendor who
prints only the body has not lost anything: you can always compute the missing digit back.
`Upc.AppendCheckDigit` does this; the maths lives in `UpcE.ComputeUpcACheckDigit`.

For an 11-digit UPC-A body, take every other digit starting at the first, times three, plus the
rest, then round up to the next multiple of ten:

```
body   0 7 1 8 9 9 0 5 1 0 1        (Blue Bell PT GR SUPREME)
       ↑   ↑   ↑   ↑   ↑   ↑        1st, 3rd, 5th, 7th, 9th, 11th
         ↑   ↑   ↑   ↑   ↑          2nd, 4th, 6th, 8th, 10th

  0 + 1 + 9 + 0 + 1 + 1  = 12   × 3 = 36
      7 + 8 + 9 + 5 + 0  = 29       + 29
                                   ────
                                     65

next multiple of 10 is 70  →  check digit = 70 − 65 = 5

full barcode = 071899051015     SSCS then stores 07189905101
```

A 13-digit EAN-13 works the same way with the weights swapped (the rightmost body digit always
carries the ×3). This is the standard GS1 modulo-10 rule — it is not SSCS-specific and it will not
change.

**Consequence:** never ask a vendor for the check digit, and never do a live SSCS test just to
discover one. The only thing a live test can tell you is *which item id the store already uses*, and
§3's decision table below usually answers that too.

### Deciding what a new vendor's code column actually is

Do this **before** writing any parser, with no live SSCS test. Run the vendor's codes through
`Upc.IsValidUpcA12` (and the EAN-13 equivalent) and count how many pass:

| How many pass | What it means | What to send |
|---|---|---|
| **~all of them** | Real, whole barcodes | send unchanged — *Dr Pepper, VM Novelties* |
| **most, but not all, and the failures are single-digit variants of codes that pass elsewhere** | Whole barcodes, damaged by the scan | send the ones that pass unchanged; replace a failure from a catalogue keyed on the vendor's product number, and ask the operator when the catalogue has never seen it — *Chubiwinos, `ChubiwinosCatalog`* |
| **~1 in 10** | Chance. Not barcodes: a body, or an internal code | see the next two rows |
| ~1 in 10, **and** all codes share a fixed prefix + are one digit short of a GTIN | It is the **body**, check digit chopped off | `Upc.AppendCheckDigit(body)` |
| ~1 in 10, **and** the codes are 12 wide with a leading `0` that is padding | Body **padded** to fill a column | strip the pad, then `AppendCheckDigit` — *Blue Bell* |
| Letters, or lengths that match no GTIN | Not a barcode at all (store PLU, vendor SKU) | send it as you want the id to read — SSCS stores a non-GTIN whole, see above |

The giveaway for a padded body is that the digits after the pad are a known GS1 company prefix.
Blue Bell's `007189905101` is `0` + `071899` (Blue Bell's GS1 prefix) + `05101`; the real barcode is
`071899051015`, and the padded form is that with the check digit removed and a zero added in front.

> **Worked comparison, both from real files in this repo.**
>
> ```
> VM NOVELTIES  file says 600609100332   check digit VALID   → send as-is      → SSCS 60060910033
> VM NOVELTIES  file says 0852681971274  check digit VALID   → send as-is      → SSCS 085268197127
> BLUE BELL     PDF says  007189905101   check digit INVALID → send 071899051015 → SSCS 07189905101
> ```
>
> Same store, same SSCS. The difference is only what each vendor's software chose to print.
> Leading zeros survive into the id in every case — `09946143239` and `085268197127` are real ids.

If the table leaves you genuinely unsure which id the store already uses, the cheapest test is one
small invoice carrying the **same product under both candidate encodings**, one unit each. The Line
Items tab shows both resulting ids side by side and flags the wrong one red as *New Item*. That is
how Blue Bell was settled on 2026-07-26 for $5.96 — not two separate round trips.

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

⚠️ **A "quantity" column is not automatically a unit count, and one vendor can mix both.** Blue Bell
counts pints and half gallons individually but counts its `DZ` products by the twelve-bar carton, in
the same `QTY` column on the same ticket — so a line reading `QTY 2 @ $6.48` is 24 units at $0.54.
The money reconciles either way, which is exactly why this hides: only the quantity and the per-unit
cost are wrong, and the retail seed comes out absurd ($11.49 for one ice cream sandwich). If a
vendor's descriptions name a pack (`DZ`, `24PK`, `CS`), check what a unit means **per product**, and
carry the pack size in the vendor's catalogue rather than reading it off the description — OCR
destroys exactly those two letters.

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

1. Write `converter/src/EdiToSscs.Core/<Vendor>PdfParser.cs`, modelled on `DrPepperPdfParser.cs`
   for a one-invoice-per-PDF vendor, `BlueBellPdfParser.cs` when one document can hold several
   invoices or a header field has to be typed in, or `ChubiwinosPdfParser.cs` when the scan damages
   the money columns badly enough that rows have to be reconciled against the invoice's own total.
2. Give it a `public const string SupplierName`.
3. Add a `PdfVendor` enum value, wire it into `ConversionService.ConvertPdf`, and add the dropdown
   entry in `MainForm.BuildVendorRow`.
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
