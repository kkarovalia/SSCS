# PDF invoice → SSCS (NAXML)

> **Adding a new vendor? Read [ADDING_A_PDF_VENDOR.md](./ADDING_A_PDF_VENDOR.md) instead.** It is a
> complete standalone guide — every rule, the full output format, and the traps — written so someone
> with no context can work from it alone. This page is the shorter architectural summary.

The second input path, for vendors who don't send EDI. Vendors: **Dr Pepper**, **Blue Bell**,
**Bimbo Bakeries** and **Red Bull**, all verified end-to-end in live SSCS; and **Chubiwinos S&G**,
which reconciles against eight and whose line figures are pinned against a hand-keyed SSCS screen but
which has not been round-tripped either. Per-vendor detail lives in [vendors/](./vendors/); the
output format is in [FORMAT_SPEC.md](./FORMAT_SPEC.md).

The PDF tab has a **vendor dropdown** — the choice selects the parser, so a ticket dropped under the
wrong vendor is refused rather than mis-read.

## Architecture: two stages, shared backend

1. **Per-vendor extraction** (the only vendor-specific work): read the PDF's text layer into a
   header plus a list of line items.
   - header: `{ supplier name, invoice no, date, total }`
   - line: `{ gtin, description, units, unit cost, retail }`
2. **Shared writer**: `NaxmlBuilder` turns that structure into XML bytes — the same target the EDI
   path produces.

Only stage 1 differs per vendor. **Never hand-format XML inside a vendor parser** — always go
through the one builder.

### Dr Pepper and Bimbo are picture-primary

Dr Pepper and Bimbo do not use the PDF OCR text layer as a value reader. Gemini reads and
cross-checks the invoice picture. OCR is used only to locate exact highlight coordinates. OCR
values must never override, reject, confirm, or modify Gemini values. The legacy OCR parser runs
only after explicit once-per-batch approval when Gemini is unavailable; there is no silent fallback.

Dr Pepper UPCs are read three independent times: primary full invoice, fresh full-page barcode-only
verification with the first barcode withheld, and an isolated enlarged UPC-cell read cropped from
Gemini's geometric cell box rather than OCR. All three
must agree exactly and pass the UPC-A check digit. There is no product catalogue, manufacturer
prefix, majority vote, padding or repair. Invoice number and date are not present in either AI
schema and are typed into empty boxes. The legacy parser may read AMOUNT DUE for validation but
must never ask the operator to type it or derive missing line values from it.

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

## What Blue Bell added to the architecture

Blue Bell (2026-07-26) broke two assumptions that were true while Dr Pepper was the only vendor:

- **One PDF can carry several documents.** A ticket carries a Sales block and a Returns block with
  separate printed numbers. `ConversionService` therefore writes a *list* of documents, not one.
  (What that ticket is not is two payables — see the correction below. The plural write path stayed,
  because nothing guarantees the next vendor will be singular.)
- **Not every header field can be parsed.** Blue Bell's invoice number has no check digit and the
  scanner mis-reads it silently (one sample says `033105035263` where the paper says
  `033103035263`). `IInvoiceDetailsPrompt` asks the operator instead of guessing. Reach for this
  only where a field genuinely cannot be verified — everything else on that ticket reconciles
  against its printed TOTAL and is parsed normally.

Invoice number and date now follow a stricter rule for every scanned vendor: both are typed into
empty boxes on every invoice. Neither carries a checksum, and a plausible wrong scanned date can
silently book the delivery into the wrong accounting period. `InvoiceDetailsRequest` carries no
invoice-number or date value at all, so a future caller cannot reconnect a scanned suggestion.
Nothing supplied still refuses the file; a placeholder is worse than no file.

This applies to header fields only. A field that **proves** the parse — the printed TOTAL — can
never be typed, because accepting a keyed figure removes the only check between a misread ticket
and the register.

Blue Bell also showed that **a "UPC" column need not hold a UPC.** Its printed code is the 11-digit
UPC-A body padded with a leading zero; sending it unchanged created a duplicate item in live SSCS.
Check a new vendor's codes against `Upc.IsValidUpcA12` before assuming they are barcodes — if far
fewer than all of them pass, they are not.

### Correction, 2026-08-04: a printed invoice number is not a payable

Blue Bell prints a separate invoice number on its Returns block, and this path was built on the
assumption that a separate number meant a separate payable — one XML per block, two invoice-number
prompts. The store's live SSCS holds the 03/19/2026 ticket as a **single $325.50 paidout of seven
lines**, six Sales rows and one −$2.98 return. It is one payable, and the converter now writes one
file, the same shape as Bimbo and Red Bull.

The general rule this leaves behind, and the one to apply to the *next* vendor: **a printed invoice
number identifies a document; only the store's own books say what a payable is.** That is not
something a PDF can tell you, and it is not something to infer from the paper's own numbering — it
has to be checked against SSCS, on a real ticket, before the shape of the output is decided.

## What Chubiwinos added to the architecture

Chubiwinos (2026-07-26) is the first vendor whose scans damage the **money columns** often enough
that a row cannot simply be read. Six rows across eight samples have a mangled price or extended.
See [vendors/CHUBIWINOS.md](./vendors/CHUBIWINOS.md) §4; the three ideas worth reusing:

- **Let the invoice's own total choose between readings.** A row offers `QTY × PRICE` and the
  printed `TOTAL`; when they disagree, try every combination across the damaged rows and accept only
  when exactly one combination reproduces the invoice total. That is arithmetic, not a preference
  for one column, and it refuses rather than guessing when the damage is genuinely ambiguous.
- **A printed unit count is worth more than it looks.** Quantity is the one field money cannot
  check — misread it and the unit cost absorbs the difference, so the invoice balances to the penny
  while the store's stock is wrong. Chubiwinos prints `Totals: <units>` per section; it is a hard
  gate, and it is what makes repairing an amount from the leftover safe.
- **Vote on a figure printed more than once.** The grand total appears five times; three are read
  and the majority wins, which silently corrected a `$684.82` for `$584.82`.

It also added the first **product catalogue** (`ChubiwinosCatalog`): item number → barcode, used
only to replace a code that has already failed its own check digit. A code that passes is always
sent as printed. That is a narrow, evidence-backed use — every one of the seven damaged codes was a
single wrong digit in a code the same vendor prints correctly elsewhere — and it is **not** a
reintroduction of the configurable vendor→code map, which stays deleted. An item the catalogue has
never seen goes to the operator (`BarcodeDialog`), because guessing a barcode is the one error
nothing downstream catches.

## What Bimbo added to the architecture

Bimbo (2026-08-03) is the first vendor whose **barcodes cannot check themselves at all**: it prints
the UPC-A body with the leading zero *and* the check digit removed, so a mis-read digit is completely
silent — the money reconciles, the unit counts agree, and SSCS opens a new item under an id belonging
to nothing. The OCR parser also proved unable to preserve every visibly readable row on these noisy
receipt scans. Bimbo therefore became the first **picture-primary** vendor on 2026-08-13. See
[vendors/BIMBO.md](./vendors/BIMBO.md). Four ideas are meant to be reused:

- **Have the model read the whole invoice, then make Core prove it.** The first request copies every
  row and the delivery, return, ticket and cash totals. Its vendor-specific prompt tells the model to
  inspect each row twice and cross-check all four levels without changing a printed value. Core then
  strictly parses the returned strings and independently repeats row, band and ticket arithmetic.
  A disputed value is shown to the operator; model output is not serialized blindly.
- **Verify check-digit-less UPCs with two different follow-up reads.** The second pass receives the
  original page plus non-UPC row locators, never the first UPC. The third receives only one enlarged
  UPC cell per row: no full invoice, description, item number, quantity, amount or prior answer. A
  UPC is accepted only when all three independently return the same ten printed digits. Even a
  two-to-one majority asks for that exact highlighted row; the August batch proved that two
  full-page reads can repeat the same ambiguous glyph.
- **Do not hardcode a product catalogue.** Bimbo buys new items continuously. Product-number → UPC
  data in source made every new item a code change and rebuild, and stale mappings were another way
  to turn a good read into the wrong product. The table was deleted; new products flow through the
  three-read rule without a release.
- **Keep fallback modes explicit.** If AI is unavailable before reading the page, the app asks once
  per queue batch whether to use the legacy PDF text parser. If AI reaches the page but its result is
  still incomplete or inconsistent after one fresh read, the page is rescanned. OCR values are never
  mixed into a partial picture reading; its positioned text is used only to locate the row image for
  an operator question.

Finally, it settled that **no scanned invoice number or date may reach the prompt, for any vendor.**
Both boxes start empty every time, and `InvoiceDetailsRequest` has neither field, so a scanned value
cannot be wired in as a suggestion by accident. See
[vendors/CHUBIWINOS.md](./vendors/CHUBIWINOS.md) §8.

## What Red Bull added to the architecture

Red Bull (2026-08-04) is the first vendor whose **own printed total cannot gate the parse**, and the
reason generalises: a checksum is only worth having if it is independent of the thing it checks.
See [vendors/RED_BULL.md](./vendors/RED_BULL.md) §4. Four ideas worth reusing:

- **Ask whether two readings are really two readings.** `PRICE` and `TOTAL` hold the same number
  when QTY is 1, so a row that multiplies out looks self-confirming. It is the same glyph printed
  twice, and the scanner mis-renders it identically in both places — the verified ticket reads
  $40.76 in both columns where the paper says $40.75. Chubiwinos' majority vote fails here for
  exactly the same reason: one sample's $918.33 comes back as $910.33 in all three of the places the
  figure is printed. Redundancy only helps when the copies can fail independently.
- **So cross *different* glyphs.** What survives is `QTY x PRICE = TOTAL` (different columns),
  `UNITS = QTY x pack` (a catalogue, not the page), the UPC-A check digit, and the rows against the
  printed total (a different figure entirely). `SettleByPrintedTotal` runs the last of these as a
  search: each row offers at most two amounts, and the printed total picks the combination Red Bull
  actually billed. Where several combinations reach it, only the rows they **all agree on** are
  settled and the rest go to the operator — an answer every solution shares is not a guess.
- **Keep a vendor's self-declared totals as a set.** Red Bull prints ROA and INVOICE; one sample
  reads $509.14 and $599.14 and the rows prove the second. Collapsing them to one number would have
  rejected a perfectly good ticket.
- **A dropped line is worse than a wrong one.** A short invoice is internally consistent, so nothing
  downstream questions it. Red Bull's barcode is printed beside the TOTAL column and on bad scans
  destroys it, so a row is recognised by QTY + UNITS + a word of description as well as by its
  money; a barcode arriving when the current product already has one becomes a row of its own with
  everything else asked for; and rows the scan's skew split across two baselines are reunited before
  anything is read.

It also settled what to ask a person for. **Barcode, units and amount get a prompt; the description
never does** — SSCS overrides the description for any item it already knows, so asking about it
would be a prompt that changes nothing, and a prompt that changes nothing teaches people to click
past the prompts that matter. Same logic for the silent barcode repairs: a single-digit fix that
satisfies both the check digit and the catalogue is not warned about, because one 300 dpi sample
reads its entire barcode column as `611260...` for `611269...` and warning on every row of every
ticket is the same as warning on none.

Finally, it is the first vendor read from **positioned words** rather than joined lines
(`PdfTextExtractor.ExtractPages`). Once a scan has split the ID column in two or dropped a money
column, no amount of counting tokens says which figure is the price and which is the total; column
x-positions survive damage that reading order does not, and they are calibrated from where the money
sits on the page because the column headings themselves read `PflICE` and `TdrAo.`.

## Supplier name

There is **no vendor→code mapping UI** — it was built and removed as unnecessary. Each PDF vendor's
parser carries a hardcoded `SupplierName` (`DR PEPPER`, `BLUE BELL`, `CHUBIWINOS`,
`BIMBO`, `RED BULL`); the EDI tab passes
each file's own vendor code through. SSCS resolves the name to one of its Vendor IDs through its
*Map Supplier to Vendor* screen, and an operator can override it from a dropdown.

A further PDF vendor gets its own parser and hardcoded name — do not reintroduce a configurable map.
