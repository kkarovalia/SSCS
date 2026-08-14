# Vendor profile: Dr Pepper

**Vendor:** Dr Pepper Bottling Company of Texas.  **Supplier name written:** `DR PEPPER`.
**Input:** searchable PDF, one invoice per file.

## Current production contract (2026-08-13)

Dr Pepper is Gemini-first, following Bimbo's picture-primary design. Gemini reads the complete
invoice picture and owns every invoice value. OCR is used only to locate exact highlight
coordinates. OCR values must never override, reject, confirm, or modify Gemini values. The legacy
OCR parser runs only after explicit once-per-batch approval when Gemini is unavailable.

The primary full-invoice response returns every physical product row in order, its page, SALES or
CREDITS section, section row order and occurrence, stable row id, item number, description,
12-digit UPC/SKU, WHSLE where printed, CASES, UNITS, NET, TAX, AMOUNT, credit direction and page
orientation. It also returns each package subtotal and its row ids, TOTAL SALE, sales/credit summary
counts and amounts, and AMOUNT DUE. Numeric values remain strings until Core validates them. The
schema has no invoice-number or invoice-date field; both operator boxes always begin empty.

UPCs have three independent AI reads:

1. the primary full-invoice read;
2. a fresh full-page barcode-only read that receives non-barcode row locators and never sees pass
   one's barcode;
3. a fresh read of each isolated, enlarged UPC cell, cropped from Gemini's geometric cell box rather
   than OCR, with no item, description, counts, money or previous barcode.

All three must agree exactly on a valid 12-digit UPC-A. There is no majority vote, manufacturer
prefix, product catalogue, 11-digit padding or repair. A missing or disagreeing pass asks only the
affected UPC. A question is allowed only when OCR located the exact value coordinates; otherwise
the operator is told to rescan. Every question shows only the photographed two-line product row,
the invoice's own three-line section strip (SALES/CREDITS banner plus both header lines), and a box around only
the requested UPC, UNITS or AMOUNT value. There is no synthetic column legend and no scrollable
whole-invoice fallback. On the fixed form, the coordinate-only fallback bands are UPC 0.42-0.69,
UNITS 0.34-0.43 and AMOUNT 0.82-0.99 of page width; they are used only after an exact token cannot
be placed and never supply a value. The box uses the same shared translucent-gold wash and dark-gold
edge as Bimbo because both vendors render through the same `SnipView` palette.

Core independently validates sales `CASES x NET = AMOUNT`, package cases/units/amount, TOTAL SALE,
the sales/credit summary, signed row money against AMOUNT DUE, and final rounded NAXML extensions
against AMOUNT DUE. A failed check never derives or changes a value to make the arithmetic work.
The legacy parser may read AMOUNT DUE for validation but must never ask the operator to type it or
derive missing line values from it.

Credits remain on the one payable as negative unit quantities and positive unit costs. Their
informational warning is routine, so a successfully validated invoice stays green. Gemini replies,
invoice contents, UPCs and amounts are not written to diagnostic logs.

The remainder of this profile records the invoice evidence and the explicitly approved legacy OCR
parser. Statements below that describe OCR as a value source apply only to that fallback.

**Legacy evidence status:** straight-sales OCR parsing was implemented and verified. The
picture-primary path now owns production conversion, and its routine-credit behavior is pinned by
tests as negative quantity with positive cost on the same payable.

Confirmed in live SSCS twice:

- **2026-07-23** via the retired fixed-width output.
- **2026-07-25** via NAXML — example 7 (invoice 4643517800, $532.43) imported as XML at BEST STOP
  #14, seq 731. Vendor resolved to **DR. PEPPER**, date 4/20/2026, all 16 lines present, and SSCS
  split the total across BEVS NTX / BEVS TX / DAIRY-WATER / EXP-FOUNTAIN to exactly $532.43.

Built as `DrPepperPdfParser` (+ `PdfTextExtractor`, `NaxmlBuilder`); all **7** sample invoices, plus
both scans of invoice 5218300220, parse and reconcile to their printed AMOUNT DUE **and to the unit
count the ticket prints for itself** (`PdfConversionTests`). Dr Pepper matters because it is the
store's most important vendor and does **not** send EDI. Framework:
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
- A credit ticket prints **no `TOT SALE` line at all** — see the next section.
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
| `5GBIB DrPep 05490.0710545/10000703` | A **period** inside the barcode. Same rule as the other marks — stripped, then held to the check digit (`054900710545` passes, so it ships). |
| `16z12 SN Frt Pnch 076183003138/10136S14` | A **letter** inside the **SKU**. The SKU is never written to the XML, so it is ignored — unlike a letter in the barcode, which is still refused. |

The last two need a second pattern (`DamagedProductLine`), because neither character can be let into
the general code class. A period is the decimal separator and a letter is most of a description, so
admitting either would let a priced row pose as a product — `38.00 1 12 25.07 0.00 / 25.07` splits
into a nine-digit "barcode" and books a line that was never delivered. The fallback is safe because
it demands the **barcode half be one unbroken run of 11–16 digits and dots**, a length no money
column can reach; only the SKU side is loosened. `APricedRowIsNeverReadAsAProductLine` pins it.

## The twice-scanned ticket: invoice 5218300220 (added 2026-08-10)

The store scanned one 08/10/26 ticket ($1080.22, 37 lines, 585 units) **twice, eleven minutes
apart**, and neither scan converted. Between them they are the reference sample for everything
above and for the quantity rules below — two photographs of one piece of paper, so every figure read
out of them can be checked against the other rather than against us
(`BothScansOfTheSameTicketProduceTheSameInvoice`).

| Scan | Damage | What it cost |
|------|--------|--------------|
| 11:19 | `DATE:. 08/10/26` | The whole invoice — no date, no file. |
| 11:19 | `10136S14` for `10136514` | The line, until the SKU rule above. |
| 11:29 | `05490.0710545` **and** `1.` in UNITS on the same product | $115.95 vanished **in silence**: the barcode damage lost the product line, the units damage lost the priced row, and the only symptom was the total being short. |
| 11:29 | `11` for `12` UNITS, twice | Nothing visible at all — see below. |

The last row is the dangerous one and the reason for the section that follows.

## Quantities: the invoice's own counts (added 2026-08-10)

**The money cannot check a quantity.** A UNITS column misread as `11` where the paper says `12`
leaves WHSLE, CASES, AMOUNT, the package subtotal and AMOUNT DUE all correct, and books a unit short
into SSCS where nothing downstream will ever notice. Two of the 37 lines on 5218300220 did exactly
this. So the quantities are reconciled against the two counts the invoice prints for itself:

- **The package subtotal** — `PEG 2 24 40.00`. Every row in a group is a whole number of cases of
  one pack size, so the subtotal *states* that pack: units ÷ cases. A row whose UNITS is not
  `cases × pack` is the damaged one.
- **`TOT SALE 44 585 1080.22`** — the backstop, for a group whose own subtotal was unreadable
  (`PEG 1 1Z 17.00`). A mismatch here refuses the invoice.
- **The `SALES SUMMARY` block** — what a credit ticket prints *instead of* `TOT SALE`, and the only
  count the returns ever get. See the next section.

**These find the damaged row. They do not supply its figure** (changed 2026-08-11). Working the
count out from `cases × pack`, or from the difference a group is short, rests on the *other* columns
having been read correctly — and a wrong QTY there produces a quantity that agrees with every total
on the page and is simply not what came off the truck. That is the same mistake that let Red Bull
ticket 2035939353 reach its printed total through four wrong rows (see
[RED_BULL.md](./RED_BULL.md) §4). So the subtotal names the row, and the operator reads the count off
the picture of it. Across the twelve Dr Pepper samples that is **8 questions on 182 rows**; seven of
the twelve ask nothing at all. The subtotal labels are OCR-variable
(`PEG`/`PKG`/`PRO`/`RAG`/`PRG`, and `PEG-` with a stroke through it), so a totals line is identified
by **shape** — a label with no digits, then two counts and an amount — not by name. `TOT SALE` is
told apart from a group subtotal by its label.

## The credit ticket: invoice 4643519283 (added 2026-08-10)

The store's next 08/10/26 delivery ($910.98, 34 lines, 544 units) carried returns, and it turned out
to be a **different layout**. A ticket with credits on it prints no `TOT SALE` line; it closes with a
block instead:

```
SALES SUMMARY
Sales    Qty   Amount
Cases     41   916.11
Units    547
Credits
Cases      0   (5.13)
Units      3
SHELLS     7   0.00
PALLETS    1   0.00
Amount Due $910.98
```

`916.11 − 5.13 = 910.98`, and `547 − 3 = 544` units go to SSCS. Read this block the same way as
`TOT SALE`: the `Sales` counts are the invoice-level backstop, and the `Credits` counts are **the
only thing on the page that counts the returns at all**, because the CREDITS section prints no
subtotal of its own. A returns mismatch warns rather than refuses — the money nets out against
AMOUNT DUE either way. None of these rows can be mistaken for a priced row or a subtotal: each
carries a word label and at most two figures.

The same ticket brought three new kinds of scan damage, all now handled:

| Seen | What it would have cost | Handling |
|------|------------------------|----------|
| `20.66 0.00 20.66` for `35.90 1 12 20.66 0.00 20.66` | **The whole product line, silently.** The row was not recognised as priced, so the description and barcode above it were passed over as scenery. The other 33 lines still reconciled against each other, so only AMOUNT DUE noticed — as a refusal that could not say which line had gone. | A row that lost columns is read from its **tail**: NET, TAX and AMOUNT are the last three and all three are money, which is a shape nothing else on the page ends in. That fixes which column is the AMOUNT; whatever is left in front is read only where its own type says what it is, and the package subtotal settles the rest. |
| `20.66-` | A prompt on a figure that is plainly legible. | A mark welded to a **money** figure is taken off, because money is re-added twice over (package subtotal, then AMOUNT DUE) so a wrong strip fails loudly. A mark on a **count** stays damage — a count is what those totals are made of. And a **letter** is never a mark: `1Z` is 12 with the 2 lost, `5b.14` is 50.14. Rubbing a letter out would delete the digit and leave a figure that looks clean. |
| `16z12C4PrfSourGumSp1842595136050` for `...Spl 842595136050` | **A barcode that is not the item.** Thirteen digits passes every length check. | Given back to the description, on the check digit's authority: thirteen digits that **fail** as a GTIN-13 whose last twelve **pass** as a UPC-A. A genuine 13-digit code passes the first test and is left alone. |

## When a figure still cannot be read

**The invoice number is always typed**, the same as every other PDF vendor — decided by the user on
2026-08-10, after the measurement below. Everything else is asked for **only** when the scan actually
destroyed it and the page cannot settle it, so a clean invoice shows exactly one window.

| Lost | What happens | Why |
|------|--------------|-----|
| Invoice number | **Always asked**, box always empty | It is the last figure on the page nothing can check, and this scanner flips digits in short numeric fields — see below |
| Date | Shown in that same window; a box only if destroyed | Being shown while the operator holds the paper is the only check it can have |
| AMOUNT DUE | Validation only | Gemini reads it. The legacy parser may read it for validation, but the operator is never asked to type it and no line value is derived from it. |
| A row's UNITS | Package subtotal first, then asked | See the section above |
| A row's AMOUNT | Asked — **never** derived | See below |
| A barcode (bad check digit, or a product line that did not read at all) | Asked | The money reconciles whatever code goes out, so nothing else can catch it |
| A row's WHSLE or CASES | **Not asked.** Retail falls back to `RetailSeed.FromCost` with a warning | They seed only a brand-new item's shelf price; refusing a delivery over a suggestion is the over-blocking the store feels |
| Column *count* wrong (5 or 7 columns) | Refused | No figure can be identified, so there is nothing to ask about |
| Lines do not sum to AMOUNT DUE | **Asked, row by row** — see below | The rows that cannot be right are named; refused only if it still will not reconcile |
| Nothing readable at all — no date, no AMOUNT DUE, no product row | Refused **before any prompt** | The page went in upside down, or it is not a Dr Pepper invoice |

**Why the number is typed even though the ticket prints one.** Two scans of ticket 5218300220, ten
minutes apart, read its printed `TIME` as **11:29** and **11:19**. The paper cannot have changed, so
one reading is wrong — this scanner does flip digits in exactly the kind of short numeric field the
invoice number is. Nothing on the page can catch it: no check digit, no total to reconcile against,
one printed copy. A wrong number breaks the match with the store's payable, silently.

So `typedNumber` **overrides** what the scan read, while `suppliedDate` and `suppliedTotal` are only
fallbacks for a field the scan destroyed. That asymmetry is deliberate — pinned by
`TheTypedInvoiceNumberWinsOverTheScannedOne` and
`ASuppliedDateOrTotalNeverOverwritesOneTheTicketPrinted`. The box is never pre-filled, for any
vendor, and `InvoiceDetailsRequest` still carries no field that could pre-fill it.

**A damaged AMOUNT is deliberately not worked out from the invoice total.** `AMOUNT DUE` minus the
other rows would give it exactly, and that is the reason not to: the sum-equals-total check is the
only independent statement this invoice makes about itself, and a row back-filled from the total
satisfies it *by construction*. Spend it on one line and the invoice can no longer detect a second
error anywhere on the page — it balances to the penny with the damaged line quietly absorbing
whatever else went wrong. Asking keeps the gate honest, and
`AnAmountTypedWrongIsStillCaughtByTheInvoiceTotal` pins that a mistyped figure is still refused.

With no prompt available — a headless caller, the test suite, or the operator cancelling — every one
of these is **refused**, never shipped with an invented figure.

### When the invoice does not add up (changed 2026-08-11)

Refusing here was throwing away deliveries that were one glyph from perfect. Three of the store's
twelve 08/11 samples hit it, and every one was legible on the paper:

| Ticket | Printed | Scanned | Gap |
|--------|---------|---------|-----|
| 4643014462 | Clamato `21.40` | `11.40` | $10.00 |
| 4642411699 | one Slot row `21.38` | `11.38` | $10.00 |
| 4642413977 | `20z24 AN RtBr PET` UNITS 24 | `14` | 10 units |

So the total is used for the one thing it is good for — it says *something* is wrong — and the rows
are narrowed before anyone is asked, because a question on every line is a question nobody reads:

1. **Rows that contradict themselves.** `AMOUNT = CASES × NET` is the row printing its own money
   twice in different glyphs. Where the two agree the row has checked itself; where they disagree,
   that is the row. On both money tickets above it is exactly one row out of 22 and 29.
2. **Rows nothing checked**, in printed order — a row whose NET or CASES the scan destroyed never
   stated its money twice. NET is the most OCR-prone column on the page, so it is only ever a
   *check*: a NET that did not read says nothing, never that the row is wrong.
3. Still not reconciling → **refused**, as before. What the operator typed is an independent reading
   and AMOUNT DUE still has to accept it, so a page that will not close after every unchecked figure
   has been read by a person has something structural wrong with it.

**A subtotal keeps its group even when the scan destroys its dollar figure.** `PEG 21 504 682.S0`
counts its package perfectly well, and requiring readable money there threw the whole subtotal away —
which took the group *boundary* with it, merged those rows into the next package, and put **eleven**
rows in question for one misread UNITS. The third column now only has to be recognisably a figure
(leading digit, decimal separator), which still keeps the package heading `16 OZ ALUM CAN LS 12
PAL160` out.

**A semicolon is a decimal point.** Invoice 4642413020 prints $13.80 and scans `13;80`; that is the
same slip as the `:` and `,` already allowed, and the figure is re-added by the package subtotal and
by AMOUNT DUE. It used to be the only question on an otherwise perfect page.

### A page scanned upside down (added 2026-08-11)

One sample came back rotated 180°. The OCR read the page line by line and produced a full text layer
of nothing — every glyph is its own upside-down twin, so `AMOUNT DUE $1263.93` arrives as
`£6'£9ZIS an0 J.NnOHiI` (5→S, 2→Z, 3→£, 6→9, 9→6) and a barcode `887725000061` as
`I900005ZLL88`. Nothing matches anything.

It is refused **before any prompt**, on the all-or-nothing test that no date, no AMOUNT DUE **and**
no product row could be read: a badly damaged invoice loses one or two of those and is still worth
converting. The message names both causes it cannot tell apart — upside down, or the wrong vendor
selected — because both are fixed in seconds at the scanner. The digits do map back cleanly enough
to reconstruct the page, and that is deliberately not done: a figure rebuilt through a transform is
a derived figure, and this vendor's rule is that a checksum may confirm one and never supply one.

### Pallets and shells are not goods (added 2026-08-11)

Below `AMOUNT DUE` the ticket lists the returnable equipment that went back on the truck:

```
PALLET PLASTIC 37X37   75001490   1
SHELL PET 1L15         75000013   1
SHELL PET 20Z          75000014   5
```

A name, a vendor number and a count — and no price and no amount, because they are worth nothing.
The `SALES SUMMARY` block prints them as `SHELLS 7 0.00` / `PALLETS 1 0.00`.

The text layer never had to know about them: they carry no `<barcode>/<SKU>` token, so they never
looked like product lines. **A reader working off the picture has no such luck** — a name, a number
and a count is exactly the shape of a product row, and the vision model reported them as products on
five of the store's sixteen no-text samples. Each one then asked the operator for an amount that is
printed nowhere on the page, and refused the invoice when nobody could supply one.

`DrPepperPdfParser.IsReturnableEquipment` drops them, on **both** halves of the test: the name starts
`PALLET` or `SHELL` *and* the row carries no price and no amount. The name alone would throw away a
real product that happens to be called a shell; no-money alone would throw away a genuine row whose
amount the scan destroyed, which is a question for the operator and not something to drop quietly.
Dropping them cannot hide anything, because they are worth `0.00` and the invoice still has to reach
its own `AMOUNT DUE` without them.

### A barcode off the picture still has to pass its own check digit (added 2026-08-11)

Reading the page a different way buys the barcode nothing. The vision model misreads a digit the same
way the scanner does: on the store's first no-text sample it read the printed `810128528191` as
`810128529191` — twelve confident digits that fail their own arithmetic — and the invoice still
reached its printed `AMOUNT DUE` to the cent, with one line booked onto a product that was never
delivered. Money is blind to a wrong code; only the check digit sees it.

The same rule the text layer gets therefore applies to `RowsFromThePicture`: a code that fails is not
a code, `Upc` is left null, and `SettleBarcodes` shows the operator the row. An **11-digit** reading
is treated the same way rather than having its check digit computed — the model dropped a digit and
nothing says which end it came off, and a supplied digit is a derived figure.

Non-deterministic, and worth knowing: the same page read correctly on the next run. The guard turns
an occasional silent wrong barcode into an occasional question.

### TOT SALE has to gate the picture's quantities too (added 2026-08-11)

The worst of the three, because nothing about it is visible. A wrong `UNITS` and a right `AMOUNT`
just give a wrong unit cost, so the invoice still reaches its own `AMOUNT DUE` **to the cent** and no
total on the page moves. Sample `DownloadFile (13)` booked **342 units against the 326 printed on the
paper** and reported `$588.97`, which is exactly what the ticket says, with no warning of any kind.

The gate for this already existed and is a hard refusal — the lines must add up to the ticket's own
`TOT SALE` count — but `printedUnits` was only ever filled from the **text layer**, so on a page with
no text layer it was null and the check never ran. The picture reports the same figure
(`VisionReading.PrintedUnits`, which the prompt asks for by name), and that is now what seeds it.

That is not the picture confirming itself: `TOT SALE` is a separate figure printed at the foot of the
ticket, which is precisely what the text-layer path has always used it as. Red Bull never had this
hole — `RowsFromThePicture` there already pushes `seen.PrintedUnits` into its `PageFigures`.

A refusal rather than a question, deliberately. With no package subtotals on a picture-only page
nothing narrows the mismatch to a row, so the alternative is asking about every line — eighteen
blind questions with no snip to show, since these rows carry line number 0. "Re-scan this one" is
the shorter honest answer.

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

Money:

- Per line: `AMOUNT = CASES × NET`. **Implemented 2026-08-11**, as a check only — it names the row
  when the invoice does not reach AMOUNT DUE, and never supplies a figure.
- Whole invoice: `Σ AMOUNT = AMOUNT DUE` = `Σ` package subtotals. (Example 7 sums to $532.43.)
- After conversion: `Σ round(units × unit cost, 2)` must still equal AMOUNT DUE. Verified 16/16 on
  example 7 even though 14 of its lines do not divide evenly.

Quantities — **these are separate, and the money ones do not imply them**:

- Per group: `Σ UNITS = ` the package subtotal's units, and `Σ CASES = ` its cases. A row that does
  not satisfy `cases × (subtotal units ÷ subtotal cases)` is asked about — the identity is the test,
  not the answer.
- Whole invoice: `Σ UNITS = TOT SALE`'s unit count (585 on 5218300220, 281 on example 7) — or, on a
  ticket carrying returns, the `SALES SUMMARY` block's `Units` (547 sold and 3 back on 4643519283).

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

Two later tickets are pinned there as well — both scans of 5218300220 (`TwiceScannedTicket1129` /
`TwiceScannedTicket1119`) and the credit ticket 4643519283 (`CreditTicket4643519283`) — with the PDFs
themselves in `Fixtures/PDF` so `DrPepperPipelineTests` runs the real files end to end.
