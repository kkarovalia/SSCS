# Bimbo Bakeries USA — PDF invoice profile

Fifth PDF vendor. Production reader: `GeminiVisionReader.ReadBimbo` + `BimboVisionParser.cs`.
`BimboPdfParser.cs` is the operator-approved fallback only. Supplier name **`BIMBO`**
(matches the Vendor ID the store's paidouts already use, against the name `BIMBO BAKERIES USA, INC`).

**Round-tripped through live SSCS on 2026-07-28** — see §8. This is the second vendor after Dr Pepper
and Blue Bell with a confirmed on-screen read-back, and the read-back was unusually complete: the
store had already keyed the same ticket by hand, so every item id, quantity and cost could be
compared line by line.

---

## 1. What the document is

A **DSD delivery ticket**, printed on a narrow till roll (≈4.4 in wide, 10–20 in long) and scanned to
PDF by the store. Heading is `BIMBO BAKERIES USA, INC. - CASH STOPS`, footer `InvoiceReport.xml v24`
or `v25`. One ticket per delivery, one page.

Two stores appear in the samples, and they are only ever distinguished for reference — **routing is
the email address the file is sent to**, so `ShipToInfo.Unknown` is correct here as everywhere:

| Store | Sales-centre line | Address | CUST# |
|---|---|---|---|
| BS14 | `GHRA 5813 BEST STOP` | 17828 FM 1314 RD, Conroe TX 77302 | 992515-2 |
| BS10 | `GHRA 5397 BEST STOP 10` | 20110 FM 1314 RD, Porter TX 77365 | 167991-4 |

### Scan quality — the single biggest factor

The store's scanner setting changed on **2026-07-27**, and it matters more than anything else in this
document:

| | Embedded image | Text layer | What the parser sees |
|---|---|---|---|
| **before 07-27** | 879 px wide ≈ **200 dpi** | present but badly damaged | whole QTY column can vanish; digits swap 7↔1 |
| **before 07-27, OCR off** | 879 px, image named `Im1.tiff` | **none at all** | picture-primary path still reads it |
| **from 07-27** | 1323 px wide ≈ **300 dpi** | clean | picture-primary path reads it cleanly |

Two of the thirteen samples have **no text layer whatsoever** — the scanner's OCR was switched off.
That no longer affects the product data: the picture is the normal source. Positioned OCR is used
only to locate a row for a boxed operator question, and a question can still be shown without it.

At 300 dpi the damage largely disappears. Everything below about tolerating damage is written for the
200 dpi backlog; it stays in because those tickets still get converted.

---

## 2. Row shape

```
UPC         ITEM No  DESCRIPTION            QTY  SUGG RETAIL  RETAIL AMOUNT  PRICE   AMOUNT
74323-09183 008788   BIM PANQUE NUE FS FC     3         3.19           9.57  3.0200    9.06
```

Rows sit under a `**DELIVERIES**` banner and, when goods went back on the truck, a `**RETURNS**`
banner where every figure is negative. Each band closes with its own totals row, and the ticket
closes with the grand ones:

```
DELIVERY TOTAL      190     413.10     416.13
RETURNS TOTAL       -39     -82.11     -81.06
TICKET TOTALS       151     330.99     335.07
PRIOR BALANCE                            0.00
CASH DUE                               335.07
```

### A speck of ink on the code band used to delete the row

The two code columns are read as **sixteen digits, counted** rather than pattern-matched, because
the same row comes back as `74323-09513 008799`, `70870- 73400 014153` and `74323-0 951 3 008799`.
Counting handles all three. What it did not handle, until 2026-08-12, was a single character of scan
debris landing in or in front of the band — and then the line was not a product row **at all**.

That is the worst failure this parser has, and the quietest. A row that is not a row takes its units
*and* its money out of the ticket together, so the band count, the band amount and the ticket count
all disagree at once, and the delivery is refused with a message about a quantity being mis-read.
Nothing names the line. Worse, a line that is not a row is never offered to the second reader and
never asked about, so the picture can read it perfectly and change nothing.

Sixteen of the store's tickets were converted in one run on 2026-08-12 and nine were refused. Eight
of those nine were this, across ten rows:

```
I 74323-09164 008785 …      a stray stroke read as a capital I, in front of the band
.J4323-09145 008783 …       the leading 7 read as ".J"
/4323-73791 013529 …        the leading 7 read as a slash — fifteen digits left
70870-00910 ,007908 …       a comma where the column gap belongs
70870=00909 007907 …        the hyphen read as an equals sign
7432r09157 008784 …         the hyphen read as the letter r
74323-09398 (07805 …        an opening bracket eating the item number's leading zero
74323-09164 08785 …         the item number short its leading zero — fifteen digits
57528-0374? 011095 …        the last UPC digit gone entirely — fifteen, unrecoverable
74323-738% 950661 …         two digits gone — fourteen
```

Every one of them is a real delivery line that any person could read off the paper. The strict read
is unchanged and still runs first, so a ticket that parsed cleanly before parses identically now; a
line it rejects gets a second, looser look:

- **any punctuation may separate the digits**, not only the space and hyphen Bimbo prints;
- **up to two characters of debris** may sit in front of the band — enough for every speck above, and
  short enough that no heading survives it, since `CASH CUSTOMER`, `GHRA 5813 BEST STOP` and `SOD:`
  all still have a letter in the third position;
- **one letter may sit between two digits**, which is a hyphen the scan mangled, allowed once per band
  so a product name can never be walked into.

The guard that makes this safe is that the band must **hand over to a letter** — the description. The
closest thing on the page to a false positive is the `SOD:` line, whose date and clock time together
offer sixteen digits (`500: 21312020 10:0649 AM`), and it fails because those digits stop in the
middle of a figure rather than at a word.

**A band one digit short is never guessed at.** This section now describes only the legacy OCR
fallback. A missing UPC digit is left empty and the operator is asked. The normal picture-first path
does not depend on this band parser at all.

### Column meanings

| Column | Meaning | Used? |
|---|---|---|
| **UPC** | UPC-A **body**, leading zero and check digit both removed | yes — §3 |
| **ITEM No** | Bimbo's own six-digit product number | yes — row locator for the independent UPC read |
| **QTY** | **individual sellable units** — never cases | yes |
| **SUGG RETAIL** | Bimbo's suggested shelf price | **no** — §5 |
| **RETAIL AMOUNT** | QTY × SUGG RETAIL | only as a second reading of QTY |
| **PRICE** | **cost of one unit**, 4 decimals | yes → `UnitCost` |
| **AMOUNT** | QTY × PRICE | only as a check; the extended is recomputed |

**There is no case arithmetic anywhere on this vendor.** `SS2P` means a single-serve two-pack, which
is one thing the store sells — not two. Verified against live SSCS on all 26 delivery lines of the
07-28 ticket: SSCS's Qty column equals the printed QTY exactly. This is the opposite of Blue Bell,
where a `DZ` line is a carton of twelve.

---

## 3. Barcodes — nothing on the page can check itself

Bimbo prints `74323-09183`. The real barcode is `074323091830`:

```
printed          74323-09183       10 digits, hyphenated
prepend 0        07432309183       the 11-digit UPC-A body
append check     074323091830      the whole UPC-A
SSCS drops it →  07432309183       ← the id this store already holds the item under
```

The check digit is arithmetic on the other eleven, so putting it back is exact. Confirmed against all
26 delivery ids on the SSCS Line Items tab.

### Two independent reads, no product catalogue

**No code on the page validates itself.** Here a single mis-read digit is completely silent: the
money still reconciles, the unit counts still agree, and SSCS quietly opens a brand-new item under an
id belonging to nothing. A hardcoded 54-product item-number table was tried and removed on
2026-08-13. It made every new Bimbo product a source change and rebuild, and a stale mapping could
replace a correctly read code with the wrong product.

The production rule is now:

1. The primary picture request reads the ten printed digits.
2. A separate focused request reads the UPC column again. It receives the page and row locators —
   row id, position, item number, description, QTY and amount — but never receives the first UPC.
3. Core accepts the UPC only when both reads independently agree on all ten digits. There is no
   product or manufacturer-prefix allow-list; a new product family needs no code change.
4. Any disagreement, missing digit, or malformed code shows that exact row to the operator. The
   operator may type the ten printed digits or the complete valid 12-digit UPC-A.

The four silent OCR errors formerly repaired from the source table remain useful regression evidence,
but they are now caught by disagreement with the focused picture read rather than by product data in
the executable. New products require no release.

---

## 4. Money

`PRICE` is already the cost of one unit, to four decimals. `AMOUNT` is `QTY × PRICE`.

The extended amount is **computed, not read**: `round(units × cost, 2)`, which is exactly what SSCS
does on import. That turns the printed AMOUNT from a figure to copy into one more thing to check —
which matters, because the printed one is sometimes wrong (`23.26` for 23.28 on ticket
84294890013154, where the row had merged into `23.06-1.9400`).

### Three checksums

This vendor is unusually generous, and it is what makes the 200 dpi backlog usable at all:

1. **per row** — `QTY × PRICE = AMOUNT`, and independently `QTY × SUGG RETAIL = RETAIL AMOUNT`;
2. **per band** — the printed unit count and the printed wholesale amount;
3. **per ticket** — `TICKET TOTALS`, whose wholesale figure is the invoice total.

`CASH DUE` equals the ticket total **only when `PRIOR BALANCE` is zero**, which it is on all nine
samples. It is a cross-check, never the answer — a non-zero prior balance would book last week's
money onto this week's invoice.

### Settling a quantity

Three readings, in order of how well each defends itself:

1. `AMOUNT / PRICE`, accepted only when it divides to a whole number;
2. `RETAIL AMOUNT / SUGG RETAIL`, the same trick on the other pair;
3. the printed QTY digit, which defends itself not at all.

The printed digit is deliberately last. On the 200 dpi scans it is the *least* reliable column on the
row: ticket 84294890012926 **lost the entire QTY column on all 22 delivery rows** and printed `1` for
12 and for 16 on two more, while the money columns beside it stayed intact. Every quantity on that
ticket comes back correctly from rule 1 alone, and the section count then proves it.

### A section count is not the last word

Ticket 84294890013154 prints `RETURNS TOTAL -15` where its eight rows come to 16 — and each of those
rows has its retail *and* wholesale columns agreeing on its own quantity. The ticket's own
`TICKET TOTALS 104` is `120 − 16`, which settles it. So a band whose count disagrees with its rows is
overruled by the grand count when the grand count agrees with the rows, with a warning; only when
**both** disagree is the ticket refused. Refusing there would cost a whole delivery over a scanner
reading a 6 as a 5 in the one place it is printed.

### The grand total is one figure, printed once

`TICKET TOTALS` is damaged by the scan like anything else, and unlike a band total nothing beside it
repeats. Two of the store's tickets lost it outright:

```
ticket 84294890010942   TICKET TOTALS 125 241.65 2411.07     the paper says 240.07
ticket 84294890010863   TICKET TOTALS 168 360.72 3511.68     the paper says 350.68
```

Both were refused, and both had the answer printed on them twice over: their `DELIVERY TOTAL` and
`RETURNS TOTAL` each reconcile with the rows underneath them, and the two together come to exactly
what the lines come to. That is two separately printed figures, independently checked, against one.
So where **every** band prints an amount of its own and they cover the ticket, a grand total that
disagrees with them loses and is said out loud. Where a band prints no amount the cover is
incomplete — the rows under it have nothing standing behind them — and the ticket is still refused.

The label itself is scan debris on ticket 84294890011217, which prints **`TICKET IOTALS`**. Only the
word TOTAL was ever matched, with the label classified separately by its first letter, so a damaged
`T` inside the word made the whole row invisible. That cost the ticket the one witness that could
have overruled its own mangled `DELIUERY TOTAL 9~` (90 units printed as `9`), and it was refused for
a quantity mismatch it had the answer to.

### A four-decimal price is a shape, not proof of its digits

`PRICE` is the only four-decimal figure on the row, which makes it the most distinctive token there
and is why a reading that still has four decimals is taken as printed. Ticket 84294890010942 prints
`1.6900` where the paper says `1.8900` — four decimals, entirely plausible, nothing about its shape
gives it away. That row's quantity came off the *retail* pair, so the wholesale columns were never
asked to agree with each other, and eight units at the price it was given is 13.52 against the 15.12
the row prints. The band total was short by exactly that 1.60.

Arithmetic alone cannot fix this, and trying is worse than leaving it. The same run prints `338` for
a real 3.78 and `1.56` for a real 7.56 — a damaged **amount** against a good price, which is the same
shape of disagreement wanting the opposite answer. Recomputing the price from the amount there books
a $3.78 line at $338.

What separates them is a second reader, and specifically **which column it reads the same way the
first one did**. The picture also reads 15.12 on the first row, so the price is the only figure the
two differ on and it is the price that moves; it reads 3.78 on the second, so the amount is the
damaged column and the printed price stands. Nothing is derived — the price used is one the picture
read off the paper.

### Rows carrying no money

Ticket 84294890013091 prints `74323-73965 968338 MLA BARRITA 35P PPS` with a bare `1` and no prices
at all, and Bimbo's own `DELIVERY TOTAL` excludes it from both the 88 units and the $178.28. Such a
row is skipped. That is safe precisely because both band gates would fail if the row had really
carried money.

### A row whose quantity will not come out is no longer dropped

A row that carries money but whose quantity none of the three readings could produce is a different
thing entirely, and until 2026-08-12 it was treated the same way — warned about and dropped. That is
silent damage: the row's units and its money leave the ticket **together**, so all three gates fail
at once and the whole delivery is refused over one figure.

Ticket 84294890010234 is the case. Forty rows, and the scan breaks two of them:

```
line 16   3.0100 comes apart into  3 .01 00     ← no divisible pair, and the QTY column is gone too
line 44   the row loses RETAIL AMOUNT and PRICE, leaving only 2.99 and 16.73
```

Both keep a clean `AMOUNT`. Dropping them left the ticket at 225 units against its own printed 236,
and it was refused. The rows are now kept unsettled and taken to the picture, and failing that to the
operator — one question about one row instead of a lost delivery.

---

## 5. Retail — the printed column is the wrong one

Bimbo prints a `SUGG RETAIL` column, and it must **not** be used. It is Bimbo's suggestion, not this
store's shelf price, and the gap is not small:

| Cost | Bimbo SUGG RETAIL | What SSCS shows the store ringing |
|---|---|---|
| 1.94 | 1.99 *(2.5% margin)* | **3.59** |
| 3.02 | 3.19 | **5.99** |
| 2.59 | 2.99 | **4.99** |

A new item seeded from the printed column would sit a nickel above cost until somebody noticed. So
retail comes from `RetailSeed.FromCost(cost, 0.45m)` — the store's own margin, with the existing
round-up-to-a-price-ending-in-9 rule.

45% reproduces the store's figure exactly on the most common line (`1.94 → 3.59`) and lands *under*
on the rest (`3.02 → 5.59` against a real 5.99; `2.59 → 4.79` against a real 4.99). Under is the safe
direction: staff correct a new item's price, and an under-priced item loses a little margin where an
over-priced one overcharges a customer.

---

## 6. Dates are operator-only

The `SOD:` line carries the date beside a clock time, which is what makes its shape unambiguous — no
other line on the ticket has that pattern. The printed shape is fixed: month, one separator, day, one
separator, four-digit year.

The legacy OCR parser can recognise some clean tokens and identify damaged ones:

```
7!28!2026  → 07-28-2026   both separators survived
711312026  → damaged       separators became digits
612672026  → damaged
6/2012026  → damaged
61212026   → damaged
717/2026   → damaged
```

None of these readings can reach the invoice-details prompt. The picture-primary model is not asked
for a date at all, `InvoiceDetailsRequest` has no date property, and the operator types the printed
date into an empty box on every invoice. Parsed dates remain only for legacy parser diagnostics.

> The token must be taken whole, never mid-way. A run of digits starting halfway along a date reads
> as a perfectly plausible different date: `6/2012026`, scanned from 06-20-2026, offers `2012026`,
> which reconstructs cleanly and **wrongly** as 02-01-2026. That was a real bug, caught by the
> samples.

---

## 7. The invoice number and date are typed

Bimbo prints `INVOICE# 84294890013239`. It is **not** used:

- it carries no check digit, and the scanner mis-reads it silently — ticket 84294890013154 came back
  as `84299890013154`;
- the store's own paidouts use a different numbering entirely (the SSCS screen for this ticket reads
  `072826132008D`, a date-based number the employee keys).

So the operator types it, through `IInvoiceDetailsPrompt`. **Both invoice number and date boxes are
empty every time** — `InvoiceDetailsRequest` deliberately has no field that could carry either
scanned value. The prompt window is titled **"Type invoice for Bimbo"**.

---

## 8. Live SSCS round trip — 2026-07-28

Ticket `84294890013239`, BS14, booked as **one** paidout of **$335.07**, Vendor ID `BIMBO`, name
`BIMBO BAKERIES USA, INC`, remark `PPT DSD`, account `15 SNACK NTX`.

**Deliveries and returns are one payable, not two.** All 26 delivery lines and all 9 return lines sit
on the same Line Items tab, the returns as negative quantities. Blue Bell works the same way — its
ticket also prints a separate invoice number on the Returns block, and live SSCS also books the pair
as one payable (verified 2026-08-04). A separate printed number does not mean a separate payable.

Confirmed on screen: every ID/PLU, every Qty, every Ea Cost. Pinned in
`BimboPdfTests.EveryBarcodeLandsOnTheItemIdLiveSscsShowed`.

### The `*****15 SNACK NTX` line, and why it is not reproduced

The store's paidout has a 36th line — `*****15  SNACK NTX  1 @ 0.80` — that appears on no invoice.
It is a **plug** the employee added to make a hand-scanned entry reach the ticket's total, and its
cause is exactly identifiable:

```
07432373929  SPONGE COOKIES   8 units   SSCS costed 1.89, invoice says 1.9400   → 0.40 short
07432373899  MARINELA TRIKIS  8 units   SSCS costed 1.89, invoice says 1.9400   → 0.40 short
                                                                          plug =  0.80
```

Scanning goods at the counter takes the cost from SSCS's own item record; reading the invoice takes
it from the invoice. So the converter needs no plug — its 35 lines reach $335.07 on their own. Pinned
in `BimboPdfTests.TheLinesReachTheTicketTotalWithNoBalancingEntry`.

This is also a clean demonstration of verified fact 5: for an item it already knows, SSCS keeps its
own description, retail and department, but honours the **file's** cost.

---

## 9. Samples

All thirteen live in `converter/tests/EdiToSscs.Tests/Fixtures/PDF/`, unmodified. The store supplied
four more on 2026-08-12; two of those four had already been filed under different names, and the two
that had not are the two this vendor could not convert.

| Fixture | Date | Total | Units | Lines | What it proves |
|---|---|---|---|---|---|
| `BIMBO_84294890013239_sscs_verified` | 07-28 | 335.07 | 151 | 35 | the live SSCS round trip; 300 dpi reads its own date |
| `BIMBO_84294890013091_zero_money_row` | 07-13 | 151.33 | 77 | 21 | a row with a UPC and no money is skipped |
| `BIMBO_84294890012926_no_qty_column` | 06-26 | 278.86 | 123 | 23 | the whole QTY column lost; prefix repair ×2 |
| `BIMBO_84294890013154_bad_section_count` | 07-20 | 209.32 | 104 | 27 | section count overruled; a destroyed unit price |
| `BIMBO_84294890012769` | 06-12 | 177.41 | 80 | 23 | OCR produced `70870-13400`; focused UPC read guards it |
| `BIMBO_84294890012057` | 06-20 | 174.84 | 81 | 19 | OCR produced `74323-09783`; focused UPC read guards it |
| `BIMBO_84294890012641` | 06-02 | 128.64 | 61 | 18 | prefix repair `70810` ×2 |
| `BIMBO_84294890013046` | 07-07 | 65.96 | 34 | 14 | OCR produced `74323-09635`; focused UPC read guards it |
| `BIMBO_84294890012978_split_upc_column` | 07-01 | 39.27 | 16 | 9 | the UPC column drifts a row clear of the item numbers |
| `BIMBO_84294890012811_displaced_money_row` | 06-16 | 53.46 | 27 | 5 | a row's money lands on the line below; `13.50` for 13.58 |
| `BIMBO_84294890010234_deliveries_only` | 09-08-**2025** | 443.68 | 236 | 40 | §10 — two rows destroyed; **no returns block at all** |
| `BIMBO_84294890013049_image_only_with_returns` | 07-07 | 48.00 | 23 | 11 | §10 — no text layer; deliveries *and* returns off the picture |
| `BIMBO_84294890012930_image_only` | 06-26 | picture | picture | picture | no text layer; normal AI path reads the page image |

Every one reconciles to its own printed `CASH DUE` and `TICKET TOTALS` count.

Two of them are worth calling out. **84294890010234 is the oldest ticket the store has** (September
2025, the other store BS10) and the only sample with **no RETURNS block** — a returns-free ticket
prints no `DELIVERY TOTAL` row either, going straight from its last row to `TICKET TOTALS`, so the
band gates have nothing to check and the ticket's own 236 is the only count there is. And
**84294890013049 carries returns on a page with no text layer**, which is the combination that makes
`isCredit` load-bearing: with no bands to read a row's sign from, the model's own flag is the only
thing saying which way the goods went, and a row facing the wrong way misses the ticket total by
twice its value.

---

## 10. The picture is the invoice source

Changed 2026-08-13 after a full-set trial showed that a human-readable Bimbo scan should not fail
because its OCR layer dropped or rearranged characters. `IBimboVisionReader` is deliberately
separate from the older confirmation interface so the tuned Dr Pepper and Red Bull request bodies
stay byte-for-byte unchanged.

### Primary request

`ReadBimbo` sends the page picture with a Bimbo-only schema. It asks for every product row in order,
the DELIVERY and RETURNS band totals, TICKET TOTALS and CASH DUE. The prompt names the eight columns,
warns about the two adjacent AMOUNT columns, says QTY is individual units, preserves four PRICE
decimals, and makes `isCredit` own the sign. It explicitly ignores invoice number and date; its schema
has no field for either.

The model is told to inspect each row twice, check `QTY × PRICE = wholesale AMOUNT`, sum each band,
and compare the ticket total with CASH DUE. Those instructions help it catch a transcription error,
but the response is still untrusted. `BimboVisionParser` parses numeric strings strictly and repeats
the row, band, ticket-unit and ticket-money checks before building `InvoiceLine`s. It never cleans up
malformed strings such as `-8-` into a plausible number.

### Two independent UPC requests

Every row gets two follow-up picture reads because the printed ten-digit body has no check digit.
The second pass receives a locally generated row id and non-UPC locators only; the first UPC is
absent, which prevents the verifier from merely echoing it. The third pass receives an enlarged crop
of the UPC cell alone. It cannot see the full invoice, description, item number, quantity, amount or
either earlier UPC. All three readings must match on all ten digits. A two-to-one result, blank, or
unavailable crop asks the operator on that exact highlighted row. There is no product or
manufacturer-prefix catalogue and therefore no product addition workflow.

With more than one configured model/key combination the verifier starts on the next combination.
With only one, it is still a fresh, narrowly-scoped request. Normal volume is roughly two requests
for the one daily Bimbo invoice per store. Bulk testing can consume quota faster, so failed or
inconsistent replies use the existing model/key fallback and bounded retry ladder.

### Parser and questions

The parser's job is intentionally narrow: validate the model's fields, normalise the printed UPC body
into the complete UPC-A, produce the ordinary `BimboTicket`, and ask when independent evidence does
not settle a value. A local disagreement asks AMOUNT first (it is not written to XML), then PRICE and
QTY if necessary. A ticket-wide count disagreement asks affected quantities; a money disagreement
asks prices when none of the printed totals supports the rows. UPC disagreement asks only that UPC.

Questions can therefore cover **UPC, QTY, PRICE or wholesale AMOUNT**. They show the real printed row
with the relevant column boxed. Positioned OCR is allowed only inside the locator used by
`PdfRowSnipper`; no OCR barcode, quantity, price, amount, description, sign, band or total is copied
back into the AI invoice.

The ticket is a fixed form, so each of those four fields also has a measured horizontal column band
as a fallback: UPC 2.5-17.0%, QTY 53.5-60.0%, wholesale PRICE 77.5-86.5%, and wholesale AMOUNT
88.5-98.5% of page width. The exact token or printed heading wins when it can be located; the band is
used only when the hidden OCR text damaged the requested value. The vertical position still comes
from the exact product row. This prevents the former whole-row yellow wash from directing an
operator to seven values when the dialog asks for one, without trusting OCR as invoice data.

### Failure and fallback

A structurally invalid successful response gets one immediate fresh read. If the API is unavailable
before it reads the page (no key, network, quota or service response), the operator may approve the
legacy PDF text parser once for the entire queue batch. If the API reached the page but still could
not produce all rows and independent totals, the ticket is not mixed with OCR: the app asks for a
clearer rescan. That distinction prevents a half-AI, half-OCR invoice whose internal agreement says
nothing about either source.

---

## 11. Gotchas

- **`SS2P` is one unit, not two.** No case arithmetic on this vendor at all.
- **Never use `SUGG RETAIL`.** §5.
- **In the legacy OCR fallback, count the code digits rather than pattern-matching them.** PdfPig renders the same row as
  `74323-09513 008799`, `70870- 73400 014153` and `74323-0 951 3 008799`. A regex with lazy digit
  groups partitions the third wrongly — it reads the item number as `3 008799` — and the row is
  dropped without a sound. Taking **sixteen digits**, ignoring spaces and hyphens between them, cannot
  make that mistake. It doubles as the non-product-row filter: the address, the customer number and
  every totals row all fail to produce sixteen leading digits.
- **A number touching a letter is description text, not a column.** Bimbo's product names are full of
  them (`SS2P`, `MB GLZ CIN RL SS 4Z`, `FRST DON SS6P3.25`), and letting them through both truncates
  the description and offers a false reading of the QTY column.
- **In the picture-primary path, require `isCredit` on every row.** The prompt derives it from the
  DELIVERY/RETURNS band and Core refuses a missing direction; a dropped minus cannot silently turn a
  return into a delivery.
- **The banners are letter-spaced** (`**DEL I VER IES**`, `` `"BFTUIINS•" ``) and unreliable. The
  closing `DELIVERY TOTAL` / `RETURNS TOTAL` rows survived every sample and are what the bands are
  split on.
- **The heading is the faintest part of the page** — the samples render the vendor name as
  `BIM 1 BAKERIES`, `BIHBO`, `81100`, `HINDU` and `BII1B0`. Vendor detection needs two of five
  markers, never the name alone.
