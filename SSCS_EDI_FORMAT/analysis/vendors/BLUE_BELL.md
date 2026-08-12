# Blue Bell Creameries, L.P. — PDF invoice profile

Second PDF vendor. Parser: `BlueBellPdfParser.cs`. Supplier name written into the XML: `BLUE BELL`.

Derived from seven sample delivery tickets (BEST STOP #14, ship-to 369260, branch 33, route 10),
dated 10-30-2025 through 05-28-2026. Examples 2 and 7 are byte-identical, so there are six distinct
files carrying **nine invoices**.

---

## 1. What the ticket looks like

A narrow dot-matrix delivery ticket, photographed or scanned at roughly 200 dpi, with the Blue Bell
cow logo printed as a **watermark across the middle of the page**. One image per page plus an OCR
text layer.

```
                Blue Bell Creameries, L.P.
                General Offices - Brenham, Texas
 DATE 01-08-2026  TIME 13:41  BRANCH# 33  ROUTE# 10  TRUCK# 1404

 SHIP TO # 369260        INVOICE#033100086682
 BEST STOP 14
 17828 FM 1314
 CONROE,TX 77302

                ****    Sales    ****

 PROD UPC          DESCRIPTION
      EA/COST      RETAIL     BX CNT     QTY    PRICE    EXTENDED
 ---------------------------------------------------------------
 0800 007189903720 1/2 GAL GR SUP
                                2.00     8.00    6.85       54.80
 0925 007189905101 PT GR SUPREME
                                8.00    80.00    2.98      238.40
 ---------------------------------------------------------------
              TOTAL            12.00    92.00              319.60
                     BALANCE DUE  319.60
```

Every product occupies **two visual lines**: `PROD UPC DESCRIPTION`, then the amounts row.

### Columns

| Column | Meaning | Used? |
|---|---|---|
| `PROD` | Blue Bell's 4-digit product number | cross-check + catalogue key |
| `UPC` | 12 printed characters — see §3 | **yes**, becomes the GTIN |
| `EA/COST` | **always blank** on every sample | no |
| `RETAIL` | **always blank** on every sample | no — see §5 |
| `BX CNT` | number of boxes/cases | secondary checksum only |
| `QTY` | what Blue Bell is billing — **units for pints and half gallons, cartons for `DZ` items** | **yes**, after the units rule below |
| `PRICE` | cost of one of whatever `QTY` counts | **yes**, after the units rule below |
| `EXTENDED` | `QTY × PRICE` | **yes**, checksum |

### Units: a `DZ` product is a carton of twelve

**Verified in live SSCS on 2026-07-26**, invoice 033100305634 read back off the Line Items tab:

```
invoice line                                    SSCS holds
0800 1/2 GAL GR SUP   QTY  4  @ 6.64   26.56    07189903720   4.00   6.64    26.56
0925 PT GR SUPREME    QTY 90  @ 2.91  261.90    07189905101  90.00   2.91   261.90
0151 DZ VANILLA SAND  QTY  2  @ 6.48   12.96    07189921122  24.00   0.54    12.96
0165 DZ CNC SAND      QTY  2  @ 6.48   12.96    07189921123  24.00   0.54    12.96
```

A pint and a half gallon are single sellable items, so their `QTY` is already the unit count. A
**`DZ` product is a carton of twelve** bars or cups: Blue Bell prices and counts it by the carton,
but the store stocks and sells the individual bar. So for those lines:

```
units     = QTY   × 12
unit cost = PRICE ÷ 12
```

**A box is a case, not a unit.** Getting this wrong does not break the money — `EXTENDED` is taken
from the invoice either way — but it books one twelfth of the real quantity and prices a single
sandwich like a carton ($11.49 instead of $0.99).

The pack size comes from the catalogue in §6, **not** from the printed description, because "DZ" is
exactly the kind of thing the scanner destroys — on the Returns page of that same invoice it read
`07 VANILLA SAND`. For a PROD number not in the catalogue the description is the only clue left, so
a leading `DZ` is treated as twelve and a warning is raised.

### Partial cartons: returns are counted in bars

A store can send back loose bars, and Blue Bell then writes the quantity as a **fraction**:

```
0151 007189921122  DZ VANILLA SAND
                        -11/12   6.48   -5.94        eleven of a twelve-bar carton
        BARS RETURNED   -11.00                       note: BARS, not UNITS
```

This needs no special handling. `-11/12` matches no money pattern and is ignored; the unit count
comes from `-5.94 ÷ 0.54 = -11`, exactly the eleven bars the `BARS RETURNED` line names, and live
SSCS holds that credit as `-11.00 @ 0.54`. Booking `-1` carton would have credited twelve bars when
eleven came back.

### What this means for the two quantity checksums

The ticket's `TOTAL` row counts in the **QTY column's** units (cartons), while a `... RETURNED` row
counts **individual items**. They are therefore compared against different figures — carton count
and unit count respectively — so a perfectly good carton ticket never warns.

---

## 2. Sales and Returns are separate invoices

A delivery where product went back on the truck produces **two tickets with consecutive invoice
numbers** — e.g. `033100786112` for $328.48 (Sales) and `033100786113` for -$2.98 (Returns).

- Example 3: two PDF pages.
- Example 5: **both on one tall page** (293 × 1637 pt).

Each ticket restarts with its own `DATE mm-dd-yyyy` header, and that is what the parser splits on —
not page boundaries, which would miss example 5.

The Returns layout drops two columns: `EA/COST  QTY  PRICE  EXTENDED`, so its amounts row carries
three numbers instead of four. Quantities and the total are negative; **unit cost stays positive**.
A `UNITS RETURNED` row restates the unit count.

### Sales and Returns are ONE payable — corrected 2026-08-04

Blue Bell prints a **separate invoice number** on the Returns block, and this vendor was originally
built on the assumption that a separate number meant a separate payable: one PDF wrote one XML per
block, and the operator was asked for two invoice numbers.

**That was wrong.** The store's live SSCS holds the 03/19/2026 ticket (fixture
`BLUEBELL_033100786112.pdf`) as a *single* paidout:

| | |
|---|---|
| Invoice # | `031926111202D` — the store's own numbering, not either printed number |
| Date | 3/19/2026 |
| Total | **$325.50** = $328.48 Sales − $2.98 Returns |
| Line Items | **7 lines, Total Qty 147** — six Sales rows and one −1.00 return |

So the returns are **negative lines on the same invoice**, one XML per PDF, named after the number
the operator types. `BlueBellPdfParser.ParseAll` still separates the blocks — the parser has to know
which rows are returns and the Returns layout drops two columns — but `BlueBellPdfParser.Merge` is
what builds the invoice.

The general lesson, which cost this vendor a rebuild: **a printed invoice number identifies a
document, not a payable.** Only the store's own books say what a payable is. Bimbo and Red Bull both
behave the same way, and both were checked against SSCS before being written that way.

### Other pages to skip

Example 1 page 1 is a **PICK LIST** packing slip (`PROD DESCRIPTION SELLBOX QTY`, no UPCs, no
prices). It is not an invoice and is dropped. A PDF containing *only* a pick list is refused.

---

## 3. The UPC column is not a UPC-A — restore the check digit

**This is the single most important rule for this vendor, and it is verified in live SSCS.**

The printed 12 characters are **not** a valid UPC-A. Only 3 of the 21 distinct codes in the samples
pass a check-digit test, which is chance:

```
007189905101   PT GR SUPREME     check digit test: FAIL
007189903720   1/2 GAL GR SUP    check digit test: FAIL
```

They are the **11-digit UPC-A body padded to 12 with a leading zero**. Blue Bell's GS1 prefix is
`071899`, so `007189905101` is `0` + the body `07189905101`.

SSCS stores whatever GTIN it is given **minus the last character**, and it does **not** strip
leading zeros (VM Novelties invoice IN-112 kept `09946143239` and `08526819712` intact). So:

| Send | SSCS ID/PLU | Result |
|---|---|---|
| `007189905101` (as printed) | `00718990510` | ❌ **New Item**, department "Unknown items" |
| `071899051015` (check digit restored) | `07189905101` | ✅ matched the store's item, department **ICE CREAM** |

> **Verified in live SSCS on 2026-07-26.** A two-line test invoice carrying the same product under
> both encodings was imported into BEST STOP #14. The Line Items tab flagged the as-printed line
> red as *New Item* with department *Unknown items*, and matched the check-digit line to the
> existing ICE CREAM item. Pinned in `BlueBellPipelineTests.MatchesWhatLiveSscsDidWithBothBarcodeForms`.

### Blue Bell prints the real barcode on its own pick list

Independent confirmation, found on invoice 033101626155 (2026-07-26). That ticket's second page is
a PICK LIST which prints each product's **barcode image with the digits underneath** — and those
digits are the whole UPC-A, check digit included:

```
pick list page          invoice page       reconstructed by ToGtin
071899037316    ←→      007189903731   →   071899037316   ✓
071899051299    ←→      007189905129   →   071899051299   ✓
071899051138    ←→      007189905113   →   071899051138   ✓
071899051176    ←→      007189905117   →   071899051176   ✓
071899051206    ←→      007189905120   →   071899051206   ✓
071899051282    ←→      007189905128   →   071899051282   ✓
071899051169    ←→      007189905116   →   071899051169   ✓
071899061205    ←→      007189906120   →   071899061205   ✓
```

**Eight of eight.** So Blue Bell's own systems agree: the pick list carries the true barcode and the
invoice carries that same barcode with its check digit stripped and a pad zero added. This is
stronger evidence than the SSCS round trip, and it needs no live system at all. Pinned in
`BlueBellLaterTicketsTests.ReconstructedBarcodeMatchesTheOneBlueBellPrintsOnItsPickList`.

So `BlueBellPdfParser.ToGtin` drops the pad zero and calls `Upc.AppendCheckDigit`. **Do not
"simplify" this into passing the printed code through.**

---

## 4. The scan is the hard part

These are photographs of a dot-matrix print with a watermark over the middle columns. The OCR text
layer is materially corrupted — **worse than Dr Pepper's**. Measured against the ground truth read
off the rendered scans by eye:

| Field | Raw text layer | After recovery |
|---|---|---|
| date, TOTAL, line count | 9/9 | 9/9 |
| PRICE, EXTENDED | 45/47 | 47/47 |
| QTY | 36/47 | 47/47 |
| UPC | 39/47 | 47/47 |
| invoice number | **1 silently wrong** | typed by the operator |

The date held on all nine of those, but not for ever: a later ticket read `DATE 12-14-Z023`. That
one is typed too, and only when it happens — see §7.2.

The pattern is positional: **PRICE and EXTENDED sit at the clean right edge and survive; BX CNT and
QTY sit under the cow and do not.** Real examples:

```
paper:  1.00  10.00   2.98   29.80
scan:   .005  1000    2.98   29.80        (example 4, PT HMV)
scan:   1.00  è2 0.00 ®2.98  29.80        (example 6, PT FEATURED FLAVOR)
scan:   r ~00'   00   6.84   13.68        (example 2, DZ CNC SAND — QTY gone entirely)
```

### Recovery rules

1. **QTY is always reconciled against `EXTENDED ÷ PRICE`** and replaced when it disagrees. Every
   Blue Bell quantity is a whole number of units, so this is exact. It also restores the sign: on
   example 5's Returns row the minus is lost (`2.00 2.98 -5.96`) and `-5.96 ÷ 2.98` gives back -2.
   On the 240 dpi ticket 033103175350 a QTY of `2.00` scans as a bare `00`; `12.72 ÷ 6.36` gives it
   back. Booking the `00` at face value would have posted $12.72 of stock that never arrived.
2. **Letter-for-digit substitution** in the code column (`l I L i`→1, `O o Q D`→0, `S s`→5, `B`→8,
   `Z z`→2), then the first 12 digits of that one token. Recovers `00718990373L1` → `007189903731`
   and `007189903733-1A2=O1JThCHO` → `007189903733`.
3. **A code that still will not read is repaired from the PROD number** using the catalogue in §6,
   with a warning. Example 3's `00718992112` (a digit short) becomes `007189921123`.
4. An unknown PROD number whose code will not read is **refused**, not guessed.
5. A row damaged past recognition drops out — and then the TOTAL checksum refuses the ticket.

### Do not rebuild a code from loose digits on the line

A short code is completed **only** from the catalogue, never by gluing on the digits that happen to
follow it. The temptation is real — `0923 007189905 120 PT DUT CHOC` is plainly the code broken
across a space, and `007189905` + `120` is the right answer. But the very next sample reads
`0165 00718992112 1 .D2/ C~N Dj- C` where that stray `1` is scanner debris: gluing it on yields
`007189921121`, a perfectly well-formed code for **the wrong product**, where the catalogue gives
the correct `007189921123`. The two cases are indistinguishable from the text alone, so the line's
own digits are never trusted to finish a code. (Tried and reverted, 2026-07-26.)

### Row-shape damage

Beyond individual digits, the scanner also breaks the row's structure. Three shapes seen so far,
all handled:

| Scanned | Meaning |
|---|---|
| `015100718992112 ZVV_Ai1LTh` | PROD and UPC run together with no space |
| `0923 007189905 120 PT i DUL CHOC` | UPC broken across a space |
| `TOT '/ r ----- . OOs 248.72` | the word TOTAL chewed down to `TOT` |

The code token is therefore matched with an **optional** space after PROD, is accepted from 8
digits up (as long as it carries the whole `0071899` prefix), and the totals row is matched on
`TOT`. `BALANCE DUE` — itself often mauled into `BALA INCE DU~E~` — is kept as a fallback total for
when the TOTAL row is unreadable. None of this weakens the guarantee: whatever total is used, the
line items still have to add up to it or the ticket is refused.

### The header words are not trusted either

At 240 dpi the labels themselves break, so neither the date nor the total depends on its own word:

| Printed | Scanned as | How it is still read |
|---|---|---|
| `DATE 11-13-2025` | `OATS 11-13-2025` | the `mm-dd-yyyy` shape alone, then validated as a real date |
| `TOTAL … 72.32` | label gone, bare `72.32` | falls through to BALANCE DUE |
| `BALANCE DUE 72.32` | `BALANCE DUE 7232` | last two digits are the cents |

The date pattern is safe on its own because the only other hyphenated numbers on the ticket are the
phone number (`281-446-1090`) and the ZIP+4 (`75267-4272`), neither of which matches `dd-dd-dddd`,
and an invalid month or day is rejected anyway.

Rebuilding a BALANCE DUE that lost its decimal point cannot put a wrong figure into a file: it is a
last resort used only when the TOTAL row is unreadable, and whatever it produces still has to equal
the sum of the line items. When the digits themselves are wrong the ticket is refused — pinned in
`BalanceDueThatDisagreesWithTheLines_IsRejected`.

### Money separators

The decimal point scans as `:` `,` or `;`. The money pattern deliberately rejects run-together
debris like `2:001`, which must never be read as 2.00.

---

## 5. Retail — 40% margin, rounded up to a 9

Blue Bell prints a `RETAIL` column and **never fills it in**, so there is nothing to copy. Sending
`0.00` would let an unpriced new item ring up free.

Per the store owner (2026-07-26): **retail = unit cost ÷ 0.60**, then **rounded up to the next
price ending in 9 within the same dime**.

Note the cost used is the **per-unit** cost from §1, not the carton price.

| Item | Unit cost | ÷ 0.60 | Sent |
|---|---|---|---|
| pint | 2.98 | 4.9667 | **4.99** |
| half gallon | 6.85 | 11.4167 | **11.49** |
| 3oz cup (6.36 ÷ 12) | 0.53 | 0.8833 | **0.89** |
| sandwich (6.84 ÷ 12) | 0.57 | 0.9500 | **0.99** |

Live SSCS showed Ea List `4.99` and Margin `40.28%` against the 2.98 cost, confirming both the
formula and that SSCS derives margin from these two numbers.

This only ever seeds a **brand-new** item; SSCS uses the store's own price for anything it knows.

---

## 6. Known products

Read off the sample scans by eye. Used for cross-checking, code repair, item naming and the pack
size in §1 — **never** to invent a code for an unseen PROD number.

**Pack** is how many sellable units one step of the `QTY` column represents: 1 for a pint or half
gallon, 12 for a `DZ` carton.

| PROD | Printed UPC | GTIN sent | SSCS ID/PLU | Pack | Name |
|---|---|---|---|---|---|
| 0020 | 007189906120 | 071899061205 | 07189906120 | 1 | PT FEATURED FLAVOR |
| 0121 | 007189921150 | 071899211501 | 07189921150 | **12** | DZ KRUNCH BAR |
| 0122 | 007189921133 | 071899211334 | 07189921133 | **12** | DZ 3OZ HMV CUPS |
| 0151 | 007189921122 | 071899211228 | 07189921122 | **12** | DZ VANILLA SAND |
| 0165 | 007189921123 | 071899211235 | 07189921123 | **12** | DZ CNC SAND |
| 0777 | 007189903735 | 071899037354 | 07189903735 | 1 | 1/2 HMV |
| 0781 | 007189903733 | 071899037330 | 07189903733 | 1 | 1/2 DUT CHOC |
| 0789 | 007189903731 | 071899037316 | 07189903731 | 1 | 1/2 CNC |
| 0800 | 007189903720 | 071899037200 | 07189903720 | 1 | 1/2 GAL GR SUP |
| 0907 | 007189905126 | 071899051268 | 07189905126 | 1 | PT PISTAL |
| 0914 | 007189905123 | 071899051237 | 07189905123 | 1 | PT MT CHOC CH |
| 0915 | 007189905129 | 071899051299 | 07189905129 | 1 | PT GREAT DIVIDE |
| 0916 | 007189905115 | 071899051152 | 07189905115 | 1 | PT CCC DOUGH |
| 0919 | 007189905113 | 071899051138 | 07189905113 | 1 | PT BUT PEC |
| 0921 | 007189905117 | 071899051176 | 07189905117 | 1 | PT CNC |
| 0923 | 007189905120 | 071899051206 | 07189905120 | 1 | PT DUT CHOC |
| 0924 | 007189905122 | 071899051220 | 07189905122 | 1 | PT HMV |
| 0925 | 007189905101 | 071899051015 | 07189905101 | 1 | PT GR SUPREME |
| 0926 | 007189905128 | 071899051282 | 07189905128 | 1 | PT STRAW |
| 0928 | 007189905127 | 071899051275 | 07189905127 | 1 | PT ROCKY RD |
| 0944 | 007189905124 | 071899051244 | 07189905124 | 1 | PT MOO CRUNCH |
| 0953 | 007189905118 | 071899051183 | 07189905118 | 1 | PT COTTON CAN |
| 7001 | 007189903730 | 071899037309 | 07189903730 | 1 | 1/2 COK 2 STP |
| 8001 | 007189905116 | 071899051169 | 07189905116 | 1 | PT COK 2 STP |

`0020 PT FEATURED FLAVOR` is a rotating slot that keeps the same code. Seasonal flavours will
arrive under PROD numbers not listed here; those convert normally as long as their code scans, and
are refused only if it does not.

---

## 7. Fields the operator types

`InvoiceNumberDialog` collects what the scan cannot supply. Choosing *Skip this file* cancels that
PDF and writes nothing.

### 7.1 The invoice number — always typed

It is the **one field with no checksum**, and the scanner gets it wrong silently. Example 1's text
layer reads `033105035263` where the paper reads `033103035263` — a single digit, undetectable.

Everything else reconciles arithmetically. This does not. So the operator types it, with the
scanned reading shown beside the box to compare against. Observed numbers are 12 digits beginning
`0331`, but that is not enforced — SSCS accepts free text.

### 7.2 The date — typed only when the scan destroyed it

One ticket's header reads `DATE 12-14-Z023`: a single character of the year came out as a letter.
Nothing else on that ticket is wrong, and its money reconciles line by line, so refusing the whole
delivery over one character just sends the operator back to keying the invoice by hand.

So a date that will not read is **not** a parse failure. `BlueBellSection.Date` is nullable, the
date box appears on the dialog, and the operator keys `mm-dd-yyyy`.

Three rules keep that honest:

- **The date is typed on every ticket** (changed 2026-08-11). The box is pre-filled with what the
  scan read and what the operator confirms is what the invoice carries. A date that reads cleanly is
  still not trusted: a wrong year is entirely plausible, checks nothing, and files the delivery into
  the wrong period. `SuggestedDate` is never used on its own - a ticket whose date was destroyed and
  whose operator typed none is still refused.
- **A repaired reading is a suggestion, never the date.** `ReadDamagedDate` maps the letters the
  scanner substitutes (`Z`→2, `O`→0, `l`→1 …) back to digits and insists on a real calendar date in
  2000–2100, then offers the result for the operator to check against the paper. Mapping characters
  is guesswork about a field with no checksum behind it, so it never stands on its own.
- **No date and no answer refuses the file** (`PDF_NO_DATE`). Year 0001 in SSCS would be worse than
  no file at all.

A damaged date still counts as a section break, so a Sales ticket does not merge into the Returns
ticket below it — that would put both invoices' line items against a single `TOTAL` and the
checksum would refuse a delivery that was otherwise fine.

Contrast the `TOTAL`, which can never be typed: it is what *proves* the parse, so accepting a keyed
figure would remove the only thing standing between a misread ticket and the register.

---

## 8. Checksums

Every ticket reconciles **two independent ways**, both enforced:

1. `Σ (units × unit cost)` **must** equal the printed `TOTAL` — a mismatch refuses the file.
2. `Σ units` is compared against the printed unit count (or `UNITS RETURNED`) — a mismatch warns
   but still writes, since the money is what SSCS books.

The `BALANCE DUE` line is printed in a large font the scanner rarely reads (`BAUNGEDU.E34~9)60`,
`BALANC,E_DUE'2%1.16`) and is deliberately not relied on.

---

## 9. Sample ledger

| File | Invoices | Date | Total | Units | Notable |
|---|---|---|---|---|---|
| example 1 | 033103035263 | 10-30-2025 | 149.00 | 50 | PICK LIST page first; invoice number mis-scanned |
| example 2 = 7 | 033100086682 | 01-08-2026 | 319.60 | 92 | cleanest scan; QTY lost on one row |
| example 3 | 033100786112 / …113 | 03-19-2026 | 328.48 / -2.98 | 82 / -1 | two pages; `00718992112` repaired |
| example 4 | 033101486067 | 05-28-2026 | 333.32 | 94 | `.005 1000` QTY; `00718990373L1` |
| example 5 | 033101206774 / …775 | 04-30-2026 | 231.16 / -5.96 | 62 / -2 | both invoices on one page; PROD split from its UPC; Returns minus sign lost |
| example 6 | 033101066688 | 04-16-2026 | 276.12 | 80 | `è2 0.00` QTY |

A second batch arrived 2026-07-26; each one broke the parser differently and each fix is pinned in
`BlueBellLaterTicketsTests`:

| File | Invoice | Date | Total | Units | Broke |
|---|---|---|---|---|---|
| DownloadFile (1) | 033101626155 | 06-11-2026 | 248.72 | 76 | UPC split across a space; `TOTAL` mangled to `TOT`; **pick-list page prints the real barcodes** |
| DownloadFile (2) | 033101766252 | 06-25-2026 | 234.60 | 57 | PROD glued to its UPC; new PROD 0953 and 0121 |
| DownloadFile (3) | 033101906349 | 07-09-2026 | 222.28 | 72 | clean; new PROD 0914 |
| DownloadFile | 033100786112 / …113 | 03-19-2026 | 328.48 / -2.98 | 82 / -1 | byte-identical to example 3 |

All thirteen invoices parse with every barcode, quantity, unit cost, date and total matching the
paper. Note that both failures were **refusals, not wrong output** — the TOTAL checksum caught the
dropped rows in each case, which is exactly what it is there for.

A BEST STOP #10 ticket arrived 2026-07-26 and is pinned in `BlueBellDamagedDateTests`:

| File | Invoice | Date | Total | Units | Broke |
|---|---|---|---|---|---|
| DownloadFile (2) | 033103483286 | 12-14-2023 | 87.30 | 30 | `DATE 12-14-Z023` — see §7.2 |

Not every ticket can be saved. One from the same batch scanned as:

```
0151 007189921122
72.96
0165 00718992112
12.96
```

Both rows lost their PRICE column, so neither could be committed, and the lines came to 684.36
against a printed `TOTAL` of 710.28 — a refusal. The paper says both cartons were 12.96, so the
`72.96` is a misread `1` as well. Nothing in the file could have recovered either figure, and the
checksum is what stopped a 25.92 shortfall reaching SSCS. **Re-scan is the only fix**; there is no
parser change that would help.
