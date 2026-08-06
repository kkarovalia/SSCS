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

`SettleByPrintedTotal` is the strongest of these: each row offers at most two amounts, exactly one
combination is what Red Bull billed, and the printed total is the only figure on the page made of
different glyphs from the rows. On the verified ticket that is what picks $40.75 out of a row that
reads $40.76 twice. Where several combinations reach the total, **only the rows they all agree on
are settled** — the rest stay open for the operator. Where none does, nothing is settled that way
and the weaker per-row rules run instead.

`ROA` and the per-page `INVOICE` figures are kept as a **set**, because they are damaged
individually: one sample prints `ROA $509.14` against an `INVOICE $599.14`, and its eleven rows come
to $599.14. The parse is in trouble only when the lines match none of them.

### The money gate

Run after the operator has answered anything unreadable (`ConversionService.CheckRedBullTotal`):

- **within 5 cents** — flagged and written. Red Bull's printed figure is drawn with the same damaged
  glyphs as the rows, so an exact match cannot be demanded, and losing a whole delivery over a
  rounding difference is worse than the difference. The verified ticket lands here, one cent high.
- **more than 5 cents** — refused, naming the gap. That means a line was missed or an amount was
  misread by dollars, and neither is something anyone would spot on a Paidout screen.

## 5. What the operator is asked

Three fields, each named by the row it belongs to — "The 5th product's UPC is blurry — please type
it here." (`LineFieldDialog`, driven by `LineFieldRequest`.)

| Field | Asked when |
|-------|-----------|
| **UPC** | no rung of the ladder in §3 could identify the product |
| **Units** | the UNITS column did not read and the pack size cannot rebuild it |
| **Amount** | the two money readings disagree and nothing on the ticket settles it |

**Never the description.** SSCS keeps its own wording for any item it already knows and ignores the
file's, so a garbled description costs nothing and the operator can fix it on the rare new item.
Asking about it would be a prompt that changes nothing — and a prompt that changes nothing teaches
people to click past the prompts that matter.

The **invoice number** is always typed and the box is never pre-filled. The ticket prints one
(`2030211863`) but the store's own paidouts use a different numbering entirely — the verified
example is filed in SSCS as `112425112379D`.

The **date** is asked for only when the ticket's own is impossible. `11/24/2025` reads as
`11/24/2026`, the same 5-as-6 that turns the 75320 ZIP into 76320; a ticket cannot be dated in the
future, so the parser declines to post it and offers the year before as a suggestion to check.

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
| `REDBULL_image_only` | — | no text layer; OCR was off |

Across all readable samples, roughly **a third of rows need a prompt** on the older scans, and
**none** on a clean one. The fix for the rest is scanner-side, not parser-side.

## 10. Gotchas

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
  from the **positions** of the money on the page, not from the header text.
