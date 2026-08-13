# Red Bull Distribution Company — PDF invoice profile

Sixth PDF vendor. **Round-tripped through live SSCS** on the 11/24/2025 ticket (Best Stop #14,
$342.50) — every ID/PLU and quantity on the Line Items tab is reproduced. Parser:
`converter/src/EdiToSscs.Core/RedBullPdfParser.cs`, catalogue in `RedBullCatalog.cs`.

Read §4 before touching anything to do with money on this vendor. Red Bull is the first vendor here
whose **printed total cannot gate the parse**, and the reason is not obvious.

---

## 1. The document

Red Bull Distribution Company Inc. "Load Sheet" delivery tickets, scanned by the store. One PDF is
one delivery, and it may run to several pages.

```
                          |||||||||||||||||||||
                              2030211863

Red Bull Distribution Company Inc.            Best Stop #14
P.O. Box 204750                               17828 FM 1314 Rd
Dallas, TX 75320-4750                         Conroe, TX 77302

Route: 69EA06     Cust#: 3000224741     Invoice: 2030211863
Load Sheet        Terms: COD            PO#
Salesman: Andres Montiel                11/24/2025 1:16 PM

ID        QTY  UNITS  DESCRIPTION        PRICE   DEP    DISC   SUGAR    TOTAL
RB1718     1     24   RED BULL 8.4OZ LS  $40.75  $0.00  $0.00  $0.00   $40.75
                        611269991000                                  ||||||||
RB33049    2     24   RED BULL 16OZ LS   $35.11  $0.00  $0.00  $0.00   $70.22
                        611269546019                                  ||||||||
...
Units Picked Up (0)   Check: #1160          DISCOUNT  $0.00
Number of SKU's 5     ROA: $342.50          INVOICE   $367.00
Cases Delivered 8     C. Applied: ($24.50)  DEPOSIT   $0.00
Units Delivered 156                         TAX       $0.00
                                            TOTAL DUE: $0.00
```

Two stores send these: **Best Stop #14** (Conroe, Cust# 3000224741) and **Best Stop #10** (Porter,
Cust# 3000169305). Routing is by email address, so the store on the paper changes nothing.

## 2. Row shape

Each product is a row plus a barcode line under the description, with the bars printed to the right
of the TOTAL column.

| Column | Meaning |
|--------|---------|
| `ID` | Red Bull's product number, `RB` + 4–6 digits |
| `QTY` | **cases** |
| `UNITS` | **individual sellable units** — this is SSCS's Qty |
| `PRICE` | the cost of **one case** |
| `DEP` / `DISC` / `SUGAR` | always `$0.00` on every sample |
| `TOTAL` | extended amount, `QTY × PRICE` |

**`UNITS` is what SSCS wants and needs no arithmetic.** Verified: the delivery page prints 156
units, the credit page eleven, and SSCS holds 145. There is no case-versus-unit trap here the way
there is on Blue Bell — the ticket has already done the division.

**Unit cost is `TOTAL ÷ UNITS`, to 4dp.** That reproduces every Ea Cost SSCS shows (40.75/24 =
1.6979 displays as 1.70 with an Ex Cost of 40.75) and, unlike `PRICE ÷ pack`, it also works on a
credit row, which prints no case price at all.

### Credit pages

A page of returns prints the same columns with `QTY` as a **fraction of a case** (`0.25`), `UNITS`
in brackets (`(6)`), and the money in brackets. Its `PRICE` is a **per-unit** rate, printed
truncated to the cent: `$2.23` for a real rate of `$53.45 / 24 = $2.2271`.

That truncation matters. Six returned 12oz cans are **$13.36**, not `6 × 2.23 = $13.38`. The exact
rate has to come back off the delivery page — which is what `SettleCreditAmount` does, and what the
live SSCS screen confirms (`-6.00 @ 2.23 = -13.36`).

## 3. Barcodes

Red Bull prints the **whole 12-digit UPC-A, check digit included**, under every row. That is a real
error detector and makes this vendor far safer than Bimbo, where nothing on the page validates
itself. Send the code unchanged and let SSCS drop the last character.

```
611269991000  →  SSCS ID/PLU 61126999100
```

Every code carries GS1 company prefix **611269**.

Two things the check digit alone cannot do, which is why `RedBullCatalog` exists:

- **It says a reading is wrong, not what it should be.** For a code whose prefix already reads
  correctly, changing any one of the last six digits can restore the check digit — six equally valid
  repairs. The catalogue picks between them, and only when exactly one candidate is a product Red
  Bull is on record as selling.
- **The prefix itself gets damaged.** One 300 dpi sample reads *every* barcode on the page as
  `611260...`, so the candidate search anchors on five digits (`61126`) and not six.

The resolution ladder, strongest first — a row that reaches the bottom is handed to the operator:

1. a reading that passes its own check digit and carries the 611269 prefix — used silently;
2. a single-digit repair that passes the check digit **and** is a catalogued product — used
   silently, because two independent things agree and there is nothing for a person to decide;
3. a single-digit repair that is the only one possible — used, with a warning;
4. a catalogued product **two** digits from the reading that also agrees on pack size — used, with a
   warning. Two digits is further than Bimbo is ever allowed to go, and is safe only because the
   candidate still has to satisfy its own check digit;
5. Red Bull's own item number, matched as a **suffix** of the scanned digits (`RB4816` arrives as
   `984816`, `O84816`, `1104816`) — used, with a warning;
6. otherwise the operator is asked.

The catalogue is built only from sample rows whose code passed its own check digit, so it is made of
evidence that validated itself. It is small, and a product it has never seen is asked about rather
than guessed.

## 3b. Reading the code off the bars (added 2026-08-11)

Red Bull prints a real barcode beside every row's TOTAL column, and it can be decoded straight off
the scanned picture with ZXing — `PdfBarcodeReader`. This is the only thing on the page that carries
error correction, and it is decoded by code sharing nothing with the OCR, so **it fails differently**.
That is the property this vendor has lacked everywhere else.

**Measured across the store's twenty tickets on 2026-08-11.**

| | |
|---|---|
| product codes printed | 220 |
| decoded off the bars and right | 168 |
| decoded and **wrong** | 6 |

The six wrong ones **satisfied their own UPC-A check digit**, which is what makes them dangerous
rather than merely useless — and one printed `611269546019` decoded to `611269546010` on three
separate tickets, so this is not random noise. A smudged print decodes wrongly the same way every
time, exactly like the OCR does. Five of the six fail Red Bull's company prefix.

So the bars sit at **rung 2** of `ResolveBarcode`, and where they sit is the whole design:

1. a reading of the digits that passes its own check digit and carries the prefix — **unless the
   bars decode something else, in which case neither is used and the operator settles it**;
2. **the bars**, when exactly one valid prefixed code sits inside that row's own band;
3. onwards, the single-digit repairs and the catalogue, exactly as before.

Ahead of the repairs, because a repair is a *guess*: for a code whose prefix already reads right,
changing any of the last six digits restores the check digit, so a damaged reading has six equally
valid candidates and the catalogue picks between them by what this vendor has been seen to sell.
That is how the store's tickets booked SPRING CHRY as SUMMER LIME and SUGARFREE 16OZ as RED BULL
16OZ — silently, because the money reconciles whatever code goes out.

**What it bought, on the same twenty tickets:** barcode questions **17 → 3**, and product codes that
disagree with the paper **6 → 0** out of 222. Every code taken off the bars is reported in the file
list, because a product identity is the one thing on this invoice no total can check.

Dr Pepper prints no bars — only the digits — so none of this applies there.

## 4. Why the printed total does not gate this parser — read this one

Every other vendor here reconciles its lines against the total the invoice prints for itself, and
refuses the file otherwise. **On Red Bull that test is not available.**

The store's OCR mis-renders individual glyphs, and it does so *consistently*. Two consequences:

**The printed total is damaged as often as the rows are.** `DownloadFile (2).pdf` prints
`Invoice Total $918.33` and the text layer reads `$910.33` — in all three of the places the figure
appears. Chubiwinos' majority-vote trick cannot help, because the same glyph renders the same way
every time it is printed.

**Two columns agreeing does not mean they are right.** `PRICE` and `TOTAL` hold the same number when
`QTY` is 1, so they look like two independent readings. They are the same glyph twice. On the
verified ticket both columns read `$40.76` where the paper says `$40.75` — confirmed by eye at 16×
zoom on the scan, and by SSCS holding that line at an Ex Cost of 40.75.

This is not a 200 dpi problem. `DownloadFile (10).pdf` is a 300 dpi August scan whose paper reads
`$45.99 $38.25 $58.71 $58.71 $58.71` and sums exactly to its printed `$260.37`; its text layer
reads `$46.90 $30.26 $68.71 $68.71 $60.71`.

So what the parser is allowed to trust is arithmetic across **different** glyphs:

| Check | Catches |
|-------|---------|
| `QTY × PRICE = TOTAL` | damage confined to one money column |
| `UNITS = QTY × pack` (catalogue) | a misread unit count, which money can never catch |
| the UPC-A check digit | a misread barcode |
| the rows against the printed `INVOICE`/`ROA` | damage the row-level checks cannot see |

`SettleByPrintedTotal` uses the last of these, and it may only ever **confirm**: where a combination
of the rows' own readings adds up to the printed total, a row whose `TOTAL` column already reads the
value in that combination has been checked against a figure made of different glyphs, and is taken.
A row the search wants to *change* is asked about instead. It used to change them, and the next
section is what that cost.

`ROA` and the per-page `INVOICE` figures are kept as a **set**, because they are damaged
individually: one sample prints `ROA $509.14` against an `INVOICE $599.14`, and its eleven rows come
to $599.14. The parse is in trouble only when the lines match none of them.

### The ticket that added up through the wrong rows — why this parser now confirms rather than derives

**`REDBULL_2035939353_total_reached_wrong_way.pdf`, found 2026-08-10.** The invoice total came out
exactly right and **four of its eight unit costs were wrong**, because the errors cancelled to the
cent. Read off the scan against what the converter wrote:

| # | Item | Paper | Written | |
|---|------|-------|---------|---|
| 2 | RB33049 16OZ | $36.11 | $35.11 | −1.00 |
| 4 | RB249873 SUMMER LIME | $54.70 | $51.70 | −3.00 |
| 5 | RB242607 SEA BLUE | $54.70 | $61.70 | +7.00 |
| 8 | RB4816 12OZ | $54.70 | $51.70 | −3.00 |

Both sides total **$394.84**, which is the printed `ROA`, so the money gate is silent and the
payable is right. Only the per-line Ex Cost is wrong.

The cause is that `SettleByPrintedTotal` can only choose among readings the *scan produced*, and on
two rows the truth is not among them: row 2's `PRICE` reads `$3611` with the decimal point gone, so
it is not money at all and the row offers only its damaged `TOTAL`; row 5 reads `$61.70` in **both**
columns, so nothing on it disagrees and it offers only that. With the truth absent from two rows, a
combination that reaches the target proves nothing — and here exactly one combination reaches it, so
the search reports it with full confidence and settles rows 4 and 8 by it.

**The fix (2026-08-11): the parser may only ever confirm a printed figure, never work one out.**

Every rule that used to *derive* a figure now asks instead:

| Was | Now |
|-----|-----|
| the printed total picks a row's amount out of two readings | it may only **confirm** an amount the TOTAL column already reads |
| a disagreeing PRICE/TOTAL settled by "a rate this ticket charges elsewhere" | asked |
| a missing TOTAL rebuilt from `QTY × PRICE` | asked |
| a missing UNITS rebuilt from `QTY × pack` | asked |
| a UNITS/pack disagreement settled by the page's own unit count | only the **printed** reading may be confirmed that way; otherwise asked |
| a credit amount rebuilt from the delivery page's rate | that rate is used only where the amount column already agrees to the rounding |

A `TOTAL` that reads cleanly but has nothing to check it against is asked about too — which is what
catches row 2, whose `PRICE` came through as `$3611` with the decimal point gone, leaving its
`$35.11` (for a printed $36.11) unquestioned.

On this ticket that means rows 4 and 8 become questions. Answering them from the picture gives
$54.70 each, and the lines then come to $400.84 against a printed $394.84 — so the ticket **still**
does not add up, because rows 2 and 5 read cleanly and wrongly and nothing on the page can tell.

That disagreement is the signal. The six rows nobody had looked at are shown too, the operator reads
$36.11 and $54.70 off the pictures, and the ticket lands on exactly $394.84 with the right money on
every line. Pinned by `TheTicketThatAddedUpThroughTheWrongRowsComesOutRightWhenTheRowsAreChecked`.

So the printed total earns its keep after all — not by choosing a row's figure, which is what got
this wrong, but by saying *something here is wrong* and letting a person find it.

**The barcode ladder in §3 is deliberately untouched by this.** A barcode is not settled by
arithmetic — it carries its own check digit, and `RedBullCatalog` is an independent record of what
this vendor sells. Two unrelated things have to land on the same twelve digits. Dropping the repairs
and asking instead costs 21 questions across the twelve samples against 3, *including two on a
perfectly clean 300 dpi scan*, and a question on a good scan is what teaches people to click through
the questions that matter.

Cost of the whole change, measured across the twelve samples: **64 questions against 118 rows**,
up from about 24. The clean 300 dpi ticket still asks nothing.

### The money gate — it asks, it never refuses (changed 2026-08-11)

`ConversionService.SettleAgainstThePrintedTotal`, run after the operator has answered anything
unreadable:

- **within 5 cents** — flagged and written. The verified ticket lands here, one cent high.
- **more than 5 cents** — every amount **nobody has looked at yet** is shown, one row at a time, with
  the figure marked. Then the total is re-checked.
- **still out, with every amount now read by a person** — written, and the disagreement said out
  loud. At that point the lines are the better evidence: the operator has held the paper and read
  each figure off a picture of it, and a printed total that still disagrees is the damaged figure —
  which on this vendor is routine.

**It used to refuse over 5 cents, and that was wrong.** Every sample it threw away was a ticket a
person could have fixed in seconds. The measured effect across the store's ten Red Bull invoices,
answering every question from the paper:

| | before | after |
|---|---|---|
| convert | 5 | **9** |
| refused | 4 | 0 |
| unreadable (loses a row) | 1 | 1, written with a warning |

And they are not merely *accepted* — they are **right**. Ticket 2035939353 now lands on exactly its
printed $394.84 with the correct money on all eight lines, where before it hit that same total
through four wrong ones.

The one thing this cannot see is a **missing line**, because a row the scan lost takes its dollars
with it. `ConversionService.CheckRedBullUnits` is the guard: the ticket's own `Units Delivered` does
not move when an amount is misread, so a disagreement there means a row is gone or a quantity is
wrong. Sample 2035261008 loses a whole product row and has no readable unit count either — it is
written with a warning and is the one ticket in the set that needs re-scanning.

## 5. What the operator is asked

Three fields, each shown as **the printed row itself, cut out of the scan with the figure boxed**
(`LineFieldDialog`, driven by `LineFieldRequest`, cut by `PdfRowSnipper`). The question is five
words — "Type the number under TOTAL" — because the answer is already on the screen: these PDFs are
photographs with a machine's transcription underneath, and only the transcription has ever been
wrong.

| Field | Asked when |
|-------|-----------|
| **UPC** | neither rung of the ladder in §3 could identify the product |
| **Units** | the UNITS column did not read, or reads against the pack size with the page's own count backing the pack rather than the print |
| **Amount** | `QTY × PRICE` does not confirm the TOTAL column — including when one of them did not read at all |

Two things about the picture are specific to this vendor and both are easy to get backwards:

- **The barcode is printed on the line *below* the priced row.** Dr Pepper prints it on the product
  line *above*. A snip cut by "the line before the one reported" would show a real row of a real
  invoice and mark the wrong figure on it, which is worse than showing nothing. `RedBullLine` carries
  `LineNumber`, `LastLineNumber` and `CodeLineNumber` so the caller never has to guess.
- **The column headings cannot be found by reading them.** They arrive as `PflICE`, `6UDAn`,
  `IDEAL`, `TDTAL` — so the header line is found by *position*, as the row directly above the first
  product, guarded by carrying no money. The picture of that line is perfectly legible, which is the
  argument for the whole feature in one example. It is shown as a legend directly on top of the row,
  so `UNITS` sits over the units and `TOTAL` over the total.

Red Bull's last money column is **TOTAL**, where Dr Pepper's is **AMOUNT**; the wording comes from
`LineFieldRequest.ColumnName` for exactly that reason. Sending someone to look for a column that is
not on their paper is worse than saying nothing.

**Never the description.** SSCS keeps its own wording for any item it already knows and ignores the
file's, so a garbled description costs nothing and the operator can fix it on the rare new item.
Asking about it would be a prompt that changes nothing — and a prompt that changes nothing teaches
people to click past the prompts that matter.

The **invoice number** is always typed and the box is never pre-filled. The ticket prints one
(`2030211863`) but the store's own paidouts use a different numbering entirely — the verified
example is filed in SSCS as `112425112379D`.

The **date is always typed into an empty box**, just like the invoice number. `11/24/2025` reading as
`11/24/2026` is the example that proves why: a plausible wrong year has no checksum and can book the
delivery into the wrong period. The scanned reading cannot reach `InvoiceDetailsRequest`.

## 6. One PDF is one invoice

Every page, deliveries and credits together, becomes a single payable. That is what SSCS did: the
two-page 11/24/2025 ticket carries invoice `2030211863` ($367.00) and invoice `2030211864`
(−$24.50), and SSCS holds **one** $342.50 paidout of ten lines.

Every PDF vendor here works this way now. Blue Bell was the exception until 2026-08-04, when the
store's SSCS showed its 03/19/2026 ticket as one $325.50 paidout — the Returns block prints its own
invoice number, but the store books the pair together, exactly as here.

## 7. Retail

Red Bull prints **no retail column**, so a brand-new item is seeded at a **45% margin** through
`RetailSeed.FromCost`, which rounds up to the next price ending in 9 — the store's rule for every
vendor. It only ever matters for an item SSCS has never seen; the store's real margins on these run
41–51%.

## 8. The live SSCS round trip (11/24/2025, invoice 112425112379D)

Paidout: `RED BULL`, 11/24/2025, **$342.50**, remark `PPT DSD`. Line Items: **10 lines, 145 units.**

| SSCS ID/PLU | Qty | Ea Cost | Ex Cost | From |
|---|---|---|---|---|
| 61126999100 | 24 | 1.70 | 40.75 | RB1718 · 611269991000 |
| 61126954601 | 24 | 2.93 | 70.22 | RB33049 · 611269546019 |
| 61126913344 | 12 | 3.52 | 42.23 | RB36463 · 611269133448 |
| 61126900286 | 48 | 2.23 | 106.90 | RB246729 · 611269002867 |
| 61126981899 | 48 | 2.23 | 106.90 | RB4816 · 611269818994 |
| 61126918246 | −1 | 2.23 | −2.23 | RB203752 · 611269182460 |
| 61126900130 | −1 | 2.23 | −2.23 | RB240073 · 611269001303 |
| 61126900312 | −2 | 2.23 | −4.45 | RB247584 · 611269003123 |
| 61126932121 | −6 | 2.23 | −13.36 | RB221027 · 611269321210 |
| 61126981899 | −1 | 2.23 | −2.23 | RB4816 · 611269818994 |

Note SSCS's descriptions are its own (`RED BULL 12OZ STRAWBE` against the ticket's `RB AMBER 12OZ
LS`) — more evidence that the description field is not worth asking anyone about.

## 9. Samples

Thirteen tickets, 06/2025 – 08/2026, both stores. `DownloadFile (6).pdf` is the SSCS-verified one.

| Fixture | Lines | Notes |
|---|---|---|
| `REDBULL_2030211863_sscs_verified` | 10 | two pages, SSCS round trip, one cent out |
| `REDBULL_2036629370_clean_300dpi` | 6 | exact; whole prefix column reads `611260…` |
| `REDBULL_2036629372_300dpi_damaged_money` | 5 | 300 dpi, money columns still wrong |
| `REDBULL_2034630731_roa_misread` | 11 | exact; `ROA` damaged, `INVOICE` right |
| `REDBULL_2029902123` | 17 | heavy money damage |
| `REDBULL_2036620317_bs10` | 7 | Best Stop #10 |
| `REDBULL_2035260898` | 15 | heavy money damage |
| `REDBULL_2035261008_total_misread` | — | printed total reads $910.33 for $918.33 |
| `REDBULL_2035939350_lost_decimal` | 13 | `$8446` for $84.46; `$10.75` for $40.75 |
| `REDBULL_2035039295_no_total_column` | 5 | two rows lost the TOTAL column outright |
| `REDBULL_2035939353_total_reached_wrong_way` | 8 | **see §4** — right total, four wrong lines |
| `REDBULL_2036620426_price_and_total_differ` | 7 | PRICE and TOTAL damaged differently on one row |
| `REDBULL_2029376829_single_line` | 1 | **one SKU** — the shape the column calibration could not see |
| `REDBULL_2030959925_bars_unreadable` | 6 | two rows whose bars will not decode; still asked about |
| `REDBULL_2031633958_repair_would_be_wrong` | 11 | the repair would book SUGARFREE 16OZ as RED BULL 16OZ |
| `REDBULL_2029327159_bars_disagree` | 11 | digits say `611269616026`, bars say `611269546026` — asked |
| `REDBULL_2030959983_two_lines` | 2 | two SKUs; `$36.11` in PRICE *and* TOTAL for a printed $35.11 |
| `REDBULL_image_only` | — | no text layer; OCR was off |

Across all readable samples, roughly **a third of rows need a prompt** on the older scans, and
**none** on a clean one. The fix for the rest is scanner-side, not parser-side.

## 10. Gotchas

- **A short ticket has no columns to find** (fixed 2026-08-11). The money columns are calibrated
  from where the money on the page stacks up, and a stack used to need three readings before it
  counted as a column. A ticket for one or two SKUs has one or two readings per column, so the only
  qualifying stack was the **TOTALS block** at the bottom — DISCOUNT, INVOICE, DEPOSIT, TAX, TOTAL
  DUE, all in one narrow column — and with PRICE and TOTAL calibrated onto that, the page contained
  no products at all. Two of the store's twenty samples are single-drop tickets and both were
  refused outright. Calibration now runs on the rows **above** the totals block only, and where those
  cannot offer three readings it takes every column it finds — which is safe precisely because the
  totals block is no longer in the running.
- **`Units Delivered` is misread as readily as anything else**, so the missing-line warning has to
  be careful: one sample prints 384 units and scans `364`, another prints 276 and scans `270`, and
  both add up to the penny. A row the scan lost takes its dollars with it, so an invoice landing
  *exactly* on its printed total cannot be missing one — the warning says so rather than sending the
  operator to hunt for a row that is on the paper. This is the one note on this vendor worth stopping
  for, and it must not cry wolf.
- **Do not group prices by pack size.** Pack 24 covers both the 8.4oz at $40.75 and the 12oz at
  $53.45, so "same pack, same price" is false. The parser keeps a set of the case prices the ticket
  proves by arithmetic instead.
- **A row can be split across two baselines.** The tickets are fed through the scanner crooked, so
  one printed row can drift far enough that its money lands on one line and its item number and QTY
  on the next. `MergeSplitRows` reunites them; without it, whole lines vanish.
- **A row's TOTAL column can be destroyed outright** by the barcode printed beside it. A row is
  therefore recognised by QTY + UNITS + a word of description as well as by its money — recognising
  it only by money drops the line, and a missing line is the one error that leaves the invoice
  looking perfectly consistent.
- **An orphaned barcode becomes a line.** If a barcode turns up while the current product already
  has one, its row was missed; it becomes a row of its own and the operator is asked for the rest,
  rather than the line disappearing.
- **`Number of SKU's` is itself OCR'd** — page 2 of the verified ticket prints 5 rows and the text
  layer says 6. It is not used as a gate.
- The column headings are unreadable (`PflICE`, `TdrAo.`, `BESCUIPUON`), so columns are calibrated
  from the **positions** of the money on the page, not from the header text — and the *picture* of
  that same line is shown to the operator as the legend over a snip, because the print is perfectly
  legible even where the transcription is nonsense.
- **The barcode is below the priced row, not above it.** The opposite of Dr Pepper. Anything cutting
  a picture for a Red Bull row must use `RedBullLine.CodeLineNumber` rather than "one line up".
- **Reaching the printed total is not proof** when a row's true reading was never among the ones the
  scan produced. See the defect in §4.
