# Bimbo Bakeries USA — PDF invoice profile

Fifth PDF vendor. Parser: `BimboPdfParser.cs` + `BimboCatalog.cs`. Supplier name **`BIMBO`**
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
| **before 07-27, OCR off** | 879 px, image named `Im1.tiff` | **none at all** | refused with "turn OCR on" |
| **from 07-27** | 1323 px wide ≈ **300 dpi** | clean | reads straight through, date included |

Two of the thirteen samples have **no text layer whatsoever** — the scanner's OCR was switched off.
That used to be the end of the road, with a plain "re-scan with the OCR setting turned on" message.
Since 2026-08-12 the picture is read instead — see §10 — because those pages are perfectly legible
and the message was asking the store to re-feed paper it had already fed.

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

### Column meanings

| Column | Meaning | Used? |
|---|---|---|
| **UPC** | UPC-A **body**, leading zero and check digit both removed | yes — §3 |
| **ITEM No** | Bimbo's own six-digit product number | yes — as a cross-check on the UPC |
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

### Why this vendor needs `BimboCatalog`

**No code on the page validates itself.** Chubiwinos at least prints whole barcodes, so seven of its
thirty-five failed a check digit and thereby announced themselves. Here a single mis-read digit is
completely silent: the money still reconciles to the penny, the unit counts still agree, and SSCS
quietly opens a brand-new item under an id belonging to nothing.

Two rules stand in for the missing check digit.

**Prefix.** Bimbo bills this store under three manufacturer prefixes — `74323` (Bimbo/Marinela),
`70870` (Mrs Baird's), `71852` (Gold'n). 164 of the 170 sample rows carry one; the six that do not
are `14323` and `70810`, each exactly one digit from a real one (the scanner reading a 7 as a 1).
A unique single-digit match is repaired and reported; an ambiguous one is left alone and warned about.

**Item number.** The ITEM No column is an independent second encoding of the same product, so the two
columns check each other. `BimboCatalog` maps item number → UPC, built by counting every pair across
the nine samples and taking the majority. It catches the four errors the prefix rule cannot see:

| Ticket | Scanned | Item No | Real | Would have become |
|---|---|---|---|---|
| 84294890012057 | `74323-09783` | 008788 | `7432309183` | a new item `07432309783` |
| 84294890013046 | `74323-09635` | 008802 | `7432309535` | a new item `07432309635` |
| 84294890012769 | `70870-13400` | 014153 | `7087073400` | a new item `07087013400` |
| 84294890012926 | `74323-09155` | 008927 | `7432309165` | a new item `07432309155` |

Two limits keep the catalogue from doing harm:

- **Only entries confirmed by two or more separate tickets may correct a code.** 34 of the 54 entries
  qualify; the other 20 can only raise a warning. Every error actually observed sits on a trusted
  entry, so nothing is lost.
- **Only a code one digit away is corrected.** Further apart means the *item number* is the damaged
  column — a mis-read item number lands on some other product's record, and following it would swap a
  good barcode for an unrelated one. The printed barcode wins, with a warning.

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

## 6. Dates

The `SOD:` line carries the date beside a clock time, which is what makes its shape unambiguous — no
other line on the ticket has that pattern. The printed shape is fixed: month, one separator, day, one
separator, four-digit year.

The 200 dpi scanner turns the separators into digits. Holding each separator to exactly one character
is what makes those recoverable — every sample admits exactly one valid calendar date:

```
7!28!2026  → 07-28-2026   both separators survived  → read
711312026  → 07-13-2026   both became digits        → suggestion
612672026  → 06-26-2026                             → suggestion
6/2012026  → 06-20-2026   one survived, one did not → suggestion
61212026   → 06-02-2026                             → suggestion
717/2026   → 07-07-2026                             → suggestion
```

A token whose separators both survived was genuinely **read** and is used. One that had to treat a
digit as a separator is a **repair**, and a repair is only ever offered to the operator as a
suggestion to check against the paper — never chosen for them.

> The token must be taken whole, never mid-way. A run of digits starting halfway along a date reads
> as a perfectly plausible different date: `6/2012026`, scanned from 06-20-2026, offers `2012026`,
> which reconstructs cleanly and **wrongly** as 02-01-2026. That was a real bug, caught by the
> samples.

---

## 7. The invoice number is typed

Bimbo prints `INVOICE# 84294890013239`. It is **not** used:

- it carries no check digit, and the scanner mis-reads it silently — ticket 84294890013154 came back
  as `84299890013154`;
- the store's own paidouts use a different numbering entirely (the SSCS screen for this ticket reads
  `072826132008D`, a date-based number the employee keys).

So the operator types it, through `IInvoiceDetailsPrompt`, and **the box is never pre-filled** —
`InvoiceDetailsRequest` deliberately has no field that could carry a suggestion. The prompt window is
titled **"Type invoice for Bimbo"**.

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
| `BIMBO_84294890012769` | 06-12 | 177.41 | 80 | 23 | catalogue corrects `70870-13400` |
| `BIMBO_84294890012057` | 06-20 | 174.84 | 81 | 19 | catalogue corrects `74323-09783` |
| `BIMBO_84294890012641` | 06-02 | 128.64 | 61 | 18 | prefix repair `70810` ×2 |
| `BIMBO_84294890013046` | 07-07 | 65.96 | 34 | 14 | catalogue corrects `74323-09635` |
| `BIMBO_84294890012978_split_upc_column` | 07-01 | 39.27 | 16 | 9 | the UPC column drifts a row clear of the item numbers |
| `BIMBO_84294890012811_displaced_money_row` | 06-16 | 53.46 | 27 | 5 | a row's money lands on the line below; `13.50` for 13.58 |
| `BIMBO_84294890010234_deliveries_only` | 09-08-**2025** | 443.68 | 236 | 40 | §10 — two rows destroyed; **no returns block at all** |
| `BIMBO_84294890013049_image_only_with_returns` | 07-07 | 48.00 | 23 | 11 | §10 — no text layer; deliveries *and* returns off the picture |
| `BIMBO_84294890012930_image_only` | 06-26 | — | — | — | OCR off, and no second reader configured: still refused |

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

## 10. The picture as a second reader

Added 2026-08-12, after Dr Pepper and Red Bull. Same contract as theirs, and the same posture: it may
**confirm a figure, never supply one**. Off by default, behind the admin PIN, and an empty reading —
no key, no network, a refused request — leaves the parse exactly as it was. `VisionVendor.Bimbo`.

### Bimbo does not get the shared barcode instructions

This is the one thing about this vendor that had to change in `GeminiVisionReader` rather than be
added to it. The prompt the other two share tells the model the barcode is a UPC-A: count to twelve,
work the last digit out from the other eleven, and drop any code that will not add up. **Bimbo prints
`74323-09183` — the body, with the leading zero and the check digit both left off.** Handed those
instructions, a model told to reach twelve digits and make them sum will pad a ten-digit body into a
plausible code, and §3 is the whole argument for why nothing downstream would catch it.

So the barcode rules are now per-vendor. Bimbo's say ten digits, exactly as printed, no leading zero,
no check digit, no padding, and leave the field empty rather than tidy one up. Red Bull's and Dr
Pepper's are the same characters they always were, which is pinned by
`SplittingTheBarcodeRulesOutLeftTheOtherTwoPromptsUntouched` — both prompts were tuned against the
store's own scans and a prompt that drifts while nobody is watching is a reader that quietly starts
making different mistakes.

The rest of the briefing is this vendor's own: eight columns, `QTY x PRICE = AMOUNT`, four decimal
places on `PRICE` that are not padding, no cases anywhere (`SS2P` is one thing, not two), returns
under a letter-spaced `**RETURNS**` banner with a minus on every figure, and `printedTotal` /
`printedUnits` off `TICKET TOTALS`. Unlike Dr Pepper and Red Bull, **the row arithmetic holds on a
credit too** — `-2 × 1.9400 = -3.88` — so the model is told to check those the same way.

### What a reading is allowed to do

**Supply a quantity the text layer lost**, on terms strict enough to be confirmation. Three things
have to hold together: the model read all of `QTY`, `PRICE` and `AMOUNT` on that row; those three
multiply out; and its `AMOUNT` is the figure the text layer already read in that column. Nothing is
worked out — the quantity is *read* by a second reader whose other two figures are corroborated, one
of them by the first reader. That is exactly the shape of both rows 84294890010234 loses.

**Disagree out loud about a quantity the text layer settled.** Never override one: `AMOUNT / PRICE`
dividing to a whole number is a strong reading, and it is what nine tickets and a live SSCS round
trip were checked against. What the picture adds is a voice, and the band and ticket gates are still
what refuse a wrong one.

**Break a tie on a barcode the catalogue could not.** §3 leaves two cases warned-about rather than
corrected: a code more than one digit from the catalogue's, and a catalogue entry only one ticket has
ever confirmed. Where the picture reads the catalogue's code off the paper, two things that share no
machinery agree on digits the OCR does not have, and the recorded code is used. This is the only
corroboration a Bimbo barcode can ever get.

**Be the whole invoice, on a page with no text layer.** Then it is one reader with nothing to check
it, and it is not pretended otherwise — the reading is *not* also used to corroborate itself. What
checks those rows is `QTY x PRICE = AMOUNT` over three separately-read figures and the count and
total the ticket prints for itself lower down the page; a row that does not multiply out goes to the
operator. There are no bands on this path, so a row's sign comes from `isCredit` and one section
carries the lot.

### Questions

Bimbo asks about two things and no more.

**The quantity**, because it is the one figure here the money cannot check — misread it and the unit
cost absorbs the difference, so the invoice balances to the penny while the stock is wrong. The
question names the column (`QTY`, not the `UNITS` both other vendors print) and shows the printed row
cut out of the scan with the figure boxed. **Bimbo prints one line per product**, which is the whole
difference from the other two: Dr Pepper carries the barcode on the line above the priced row and Red
Bull on the line below, so both cut a two-line band and have to say which line to mark. Here the row
is the line. No hint under the box, because there are no cases to convert and nothing to get wrong.

**The barcode**, but only on a row that came off the picture with no code on it. A row read out of the
text layer always carries ten digits — `SplitCode` counted them — and the prefix rule and the
catalogue are what stand behind those.

Not the unit price: it is recovered as `AMOUNT / QTY` once the quantity is known, and the band and
ticket totals then prove it. Not the description: SSCS keeps its own wording for an item it already
has.

### The heading strip, and why it was missing for a day

The strip above the row has to be the **cut from the ticket itself**, not the typed
`ColumnLegend` fallback. On this vendor that matters more than on the other two, because Bimbo prints
**two AMOUNT columns side by side** — a suggested-retail one the store does not use, and the
wholesale one it does — and only an aligned strip says which of the two the question is about.

It shipped falling through to the typed legend. `PdfRowSnipper.IsColumnHeader` matched a line
carrying `UNITS` and `AMOUNT`, which are Dr Pepper's words:

- Bimbo prints **QTY**, not UNITS, so the first half never matched at all;
- and its scans wreck the second half — across the eleven readable samples `AMOUNT` reaches the text
  layer as `ANOLNT`, `AHRUNT`, `AH00NT`, `AHUUNI`, `ANOUNT`, `ANO NT`, `AMONT` and `ANDUTI`.

`RETAIL` and `PRICE` both survive on **all eleven**, on the same line, and neither word appears on a
product row. That is the pattern now.

The heading is also **two printed lines**, which no other vendor's is:

```
             ITEM                              SUGG   RETAIL    WHOLESALE
     UPC     No                          QTY  RETAIL  AMOUNT  PRICE   AMOUNT
```

Cutting the lower line alone gives `RETAIL AMOUNT PRICE AMOUNT` over four figures — it names two of
them twice and resolves nothing. So the band is extended upward one line when that line carries
`RETAIL` **without** `PRICE`, which is true of Bimbo's upper line and of nothing on a Dr Pepper page.
The nearest heading above the row wins, so a returned line is shown the RETURNS band's own copy.

> The lesson worth keeping: **a missing cut looks fine.** The typed legend renders in its place, so
> the dialog is populated and plausible — it simply cannot line up over a scan. It reads as a
> cosmetic complaint rather than as the invoice's own header being absent from the screen.

### A figure the scan destroyed is marked by its column

Where the text layer never read the figure being asked about there is no token to find, and the row
used to be washed end to end. That is common on this vendor rather than rare, because QTY is exactly
the column the 200 dpi scans delete: ticket 84294890010234 lost it on **all forty rows**. A washed
882-pixel line on a row of eight figures, two of them headed AMOUNT, is not a marker.

So the column is marked instead. It is located from the printed heading — specifically as the
heading word **immediately left of `RETAIL`**, because QTY's own heading is what the scan mangles
(`UTY`, `OTY`, `OW`, `UN`, `RTY` across the samples) while `RETAIL` survives on all eleven. The
vertical extent is the row's own line, which is exact; only the horizontal comes from the heading.
The figure is legible in the picture even where the text layer never got it, which is the whole
premise of `PdfRowSnipper`.

The box is the heading's span opened out by one width to the left and three quarters to the right.
Both margins are needed and the right-hand one especially: the column is right-aligned and a
**returned** line runs furthest of all, since the minus sign pushes the digits along. Measured —
heading `OTY` at 162.1–171.9 carries `1` at 168.6–171.9, `8` at 167.6–172.6 and `12` at 165.4–172.5;
the returns band's heading at 165.0–174.8 carries `-1` at 170.7–178.1. The resulting box clears the
description (ends ~150) and the SUGG RETAIL figures (start ~187) at both ends, so it cannot spill
onto a neighbour.

A token the scan *did* read is still marked directly — tighter and safer than its column — so this
is a fallback, never the first choice.

> Beware the scale when checking this by hand. These are till rolls: the page is 315.72 or 317.52
> points wide, not a standard size, and a check that assumes ~306 comes out several pixels adrift.
> That is how a first pass "found" a clipped marker on the returns row that was nothing of the sort.

---

## 11. Gotchas

- **`SS2P` is one unit, not two.** No case arithmetic on this vendor at all.
- **Never use `SUGG RETAIL`.** §5.
- **Count the code digits, do not pattern-match them.** PdfPig renders the same row as
  `74323-09513 008799`, `70870- 73400 014153` and `74323-0 951 3 008799`. A regex with lazy digit
  groups partitions the third wrongly — it reads the item number as `3 008799` — and the row is
  dropped without a sound. Taking **sixteen digits**, ignoring spaces and hyphens between them, cannot
  make that mistake. It doubles as the non-product-row filter: the address, the customer number and
  every totals row all fail to produce sixteen leading digits.
- **A number touching a letter is description text, not a column.** Bimbo's product names are full of
  them (`SS2P`, `MB GLZ CIN RL SS 4Z`, `FRST DON SS6P3.25`), and letting them through both truncates
  the description and offers a false reading of the QTY column.
- **Take a row's sign from its band, not from a printed minus.** OCR drops a minus; the band cannot
  be dropped, and both band gates would catch it if it were.
- **The banners are letter-spaced** (`**DEL I VER IES**`, `` `"BFTUIINS•" ``) and unreliable. The
  closing `DELIVERY TOTAL` / `RETURNS TOTAL` rows survived every sample and are what the bands are
  split on.
- **The heading is the faintest part of the page** — the samples render the vendor name as
  `BIM 1 BAKERIES`, `BIHBO`, `81100`, `HINDU` and `BII1B0`. Vendor detection needs two of five
  markers, never the name alone.
