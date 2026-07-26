# PDF invoice → SSCS (NAXML)

> **Adding a new vendor? Read [ADDING_A_PDF_VENDOR.md](./ADDING_A_PDF_VENDOR.md) instead.** It is a
> complete standalone guide — every rule, the full output format, and the traps — written so someone
> with no context can work from it alone. This page is the shorter architectural summary.

The second input path, for vendors who don't send EDI. First and currently only vendor:
**Dr Pepper**, verified end-to-end in live SSCS. Per-vendor detail lives in
[vendors/](./vendors/); the output format is in [FORMAT_SPEC.md](./FORMAT_SPEC.md).

## Architecture: two stages, shared backend

1. **Per-vendor extraction** (the only vendor-specific work): read the PDF's text layer into a
   header plus a list of line items.
   - header: `{ supplier name, invoice no, date, total }`
   - line: `{ gtin, description, units, unit cost, retail }`
2. **Shared writer**: `NaxmlBuilder` turns that structure into XML bytes — the same target the EDI
   path produces.

Only stage 1 differs per vendor. **Never hand-format XML inside a vendor parser** — always go
through the one builder.

## What SSCS actually consumes

SSCS (Computerized Daily Book, `sunrayasp.com`) is the **consumer**. Per line it reads
**GTIN, description, units, unit cost, retail**, and derives Ea Cost, Ex Cost, Margin and
Department itself. For items already in its master it uses **its own** description, retail and
department and ignores the file's.

> Verified 2026-07-25 (Dr Pepper example 7 imported as XML): SSCS showed its own descriptions
> (`JANJOO 8PIN 5FT CABLE` where the file said `JANJOO 8-PIN CABLE 5FT`) and its own retails,
> while honouring the file's GTINs, quantities and costs — Ex Cost matched on all 16 lines and the
> invoice totalled $532.43 against a Verify Total of $532.43.

**Implication:** get **GTIN, units and unit cost** exactly right. Description and retail are
best-effort seeds that only matter for brand-new items.

## Barcodes are the strong part of a PDF

The check digit makes a barcode self-validating: a mis-scan almost always fails it, so a bad UPC
announces itself instead of silently pointing at the wrong product. `Upc.IsValidUpcA12` runs on
every parsed code and records a `PDF_UPC_CHECK_DIGIT` warning when it fails.

Two findings worth keeping in mind for future vendors:

- **A "searchable" PDF may be an OCR scan.** Dr Pepper's are: the text layer contains `Battling`
  for `Bottling`, `SALES ROUTE;` for `:`, and `16z12C4Pr£Frznfmscl`. The parser tolerates the
  common OCR slips (`.` scanned as `:` or `,`) and the XML writer strips non-ASCII.
- **Barcode images decode more reliably than OCR'd digits.** On a VM Novelties invoice, 58 of 58
  embedded barcodes decoded cleanly, and on a 400 dpi scan 71 of 72 decoded with **zero** wrong
  reads. One of them disagreed with the vendor's own data file — the barcode was right and the
  vendor's typed digit was wrong. If a future vendor prints barcodes but no readable UPC text,
  decoding the bars is a better source than OCR.

## Adding a new vendor

1. Extract the text layer and write a profile in `vendors/` recording the row shape, which columns
   mean what, and which lines to skip.
2. Write a parser producing `Invoice`. It must have its **own checksum** — Dr Pepper's is that the
   line amounts sum to the printed `AMOUNT DUE`, and the parser refuses the file otherwise rather
   than emitting something wrong.
3. Add fixture-based tests using real extracted text.
4. Convert a real invoice and have the user email it to a store's EDI address, then confirm the
   **Paidout** tab (invoice #, vendor, date, total) and **Line Items** tab (ids, qty, ea cost,
   ex cost) read back correctly.
5. Record the confirmed figures in the tests, the way `PdfConversionTests` pins the 16 Ex Cost
   values SSCS displayed.

## Supplier name

There is **no vendor→code mapping UI** — it was built and removed as unnecessary. The PDF tab
converts Dr Pepper and writes the fixed name `DR PEPPER`; the EDI tab passes each file's own
vendor code through. SSCS resolves the name to one of its Vendor IDs through its
*Map Supplier to Vendor* screen, and an operator can override it from a dropdown.

If a second PDF vendor is added, give it its own parser and a hardcoded name rather than
reintroducing a configurable map.
