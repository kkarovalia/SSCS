# Chubiwinos S&G — PDF invoice profile

Fourth PDF vendor. Parser: `converter/src/EdiToSscs.Core/ChubiwinosPdfParser.cs`,
catalogue: `converter/src/EdiToSscs.Core/ChubiwinosCatalog.cs`.
Read [../ADDING_A_PDF_VENDOR.md](../ADDING_A_PDF_VENDOR.md) first if you are new to this path.

CHUBIWINOS S&G INC. of Willis, TX — a DSD snack route (Sabritas / Barcel / Winnuts lines), route
2948, delivering to BEST STOP #14 roughly every two weeks. Paid on delivery; the store books it as a
paidout with remark `PPT DSD` against Vendor ID `CHUBIWINOS`.

**Status: parsed and reconciled against nine real invoices (11/27/25 – 07/03/26); NOT yet
round-tripped through live SSCS.** The line-level figures are nevertheless pinned against live SSCS,
because the store had already keyed invoice **6V9389** by hand and its Line Items tab was
photographed — see §8. Two open questions before this is called done: §9.

---

## 1. What the invoice looks like

One page, one invoice per PDF, scanned to a searchable PDF from what looks like a phone photo of a
thermal or laser ticket. Arrives by email under a meaningless name (`DownloadFile (3).pdf`).

```
Invoice: 6V9389
Date: 06/15/26 8:54 AM
Route #: 2948
Created By: Jerry V.

CHUBIWINOS S&G INC.
WILLIS, TX 77318
Phone: (281) 772-2753

BEST STOP #14
17828 FM 1314 RD
CONROE, TX 77302
Terms: DUE ON RECEIPT

PRODUCT                          QTY      PRICE     TOTAL

                          SALES SECTION
220 CHEETO COLMILLO -100 g        20      $2.79    $55.80
7-500478-044207
224 TOSTITOS SALSA VERDE 175 g    24      $2.79    $66.96
7-501011-130869
...
                          Totals: 158              $463.02

                          DUMP SECTION
205 PAPAS ADOBADAS 105 g           1    ($2.79)   ($2.79)
7-501011161667
                          Totals: 1                ($2.79)

                             SALES:      $463.02
                           CREDITS:       ($2.79)
                        NET AMOUNT:       $460.23
                         SALES TAX:         $0.00
                         TOTAL DUE:       $460.23
                     TOTAL PAYMENT:         $0.00
                   INVOICE BALANCE:       $460.23

Signature
```

Each product is **two visual lines**: the row, then its barcode underneath. A long product name
wraps onto a third line (`900 WINNUTS PAPAS CASERAS CON` / `SAL- 6oz`), which sits *between* the row
and its barcode.

| Column | Meaning | Used as |
|---|---|---|
| (first) | Chubiwinos's own 3-digit product number, `200`–`299` snacks, `8xx`/`9xx` other brands | catalogue key; not sent |
| PRODUCT | Product name | `Description` |
| QTY | **Individual units.** Never cases — see §3 | `Units` |
| PRICE | **Cost of one unit** | `UnitCost` |
| TOTAL | `QTY × PRICE` | per-line checksum |
| (next line) | The whole barcode, 12 or 13 digits, check digit included | `Gtin` |

**DUMP SECTION** is credits — goods that went back on the truck. Its money is printed in
parentheses. It becomes negative units at a positive cost, exactly as the store keys it by hand.

**No case arithmetic anywhere.** There are no pack sizes, no case costs and no `DZ`-style traps.
The numbers inside a description (`-180 g`, `-225gm`, `3.53oz`) are packaging text.

---

## 2. These are heavily damaged OCR scans

Every one of the eight samples has something wrong with its text layer. What matters is that the
damage is *localised* and every field has something else to check it against:

| Field | Damage across the 8 samples | What catches it |
|---|---|---|
| QTY | **none** | the printed `Totals: <units>` row, per section |
| PRICE / TOTAL | 6 rows (`$03.70` for $83.70, `$46.66` for $46.68, `$169` for $3.89, `$2.19 $63.70` for $2.79 $83.70) | `QTY × PRICE = TOTAL`, then the invoice total (§4) |
| grand total | 1 file (`SALES: $446.44` for $448.44, `MET AMOUNT: $684.82` for $584.82) | printed 5×; majority of three of them (§5) |
| date | 6 of 8 — the `/` scans as `1` (`07103126`, `06115/26`) | fixed shape + calendar validity (§6) |
| barcode | 7 of 35 distinct codes | the check digit, then the catalogue (§7) |
| **invoice number** | 2 of 8 (`0V9293`, `6)9141`) | **nothing** — so it is typed (§8) |

Labels rot too, and none of them are matched literally: `Dale:`/`Bate:` for `Date:`, `PRlK1UCT`,
`PROOUCT` and `PR000CT` for `PRODUCT`, `UTY`/`UTV`/`ON`/`DT4`/`UT?` for `QTY`, `OUMP` for `DUMP`,
`SALESS ECTION` for `SALES SECTION`, `Crealed By` for `Created By`.

**Assume any single label can be destroyed outright.** Two of them have been:

- `TOTAL OIIE:` for `TOTAL DUE:` on invoice 0V8790 — no pattern is going to match that. It costs one
  of the three votes for the grand total and nothing else, which is the whole point of §5.
- The **vendor's own name**, on the same invoice, reads `111181W INOS S&G INC.` — `CHUBI` gone and a
  space through the middle of `WINOS`. It also scans as `CHUBI WINOS`, `CHUDIWINOS` and
  `CHt18IWINOS` elsewhere. This is why the vendor check counts six markers and needs two (§11), and
  is not allowed to depend on the name.

**Use PdfPig, not a flat text dump.** `pdftotext -layout` scrambles these pages badly — the price
and total columns float into separate blocks several rows away from their product. PdfPig's
baseline grouping (`PdfTextExtractor.ExtractLines`) reconstructs one clean row per product on all
eight samples. The whole parser depends on that.

---

## 3. QTY is units, verified

Not assumed. The store keyed invoice 6V9389 by hand and SSCS holds its first line as:

```
ID/PLU 750047804420   Pack Size 1   Qty 20.00   Ea Cost 2.79   Ex Cost 55.80
```

against a printed `220 CHEETO COLMILLO -100 g   20   $2.79   $55.80`. Quantity, unit cost and
extended all pass through unchanged. Same for the credit: printed `1 ($2.79) ($2.79)`, held as
`-1.00 @ 2.79 = -2.79`.

---

## 4. How a damaged row is repaired

A row offers two readings of what it charged: `QTY × PRICE`, and the printed `TOTAL`. When they
agree the row is certain. When they do not — or when only one money column survived at all — the
row is **unsettled**, and which reading is right is decided by arithmetic, never by preferring one
column over the other:

1. Add up the certain rows.
2. The leftover is what the unsettled rows must come to.
3. Try every combination of their possible readings. **Exactly one** combination must hit the
   leftover, or the file is refused.
4. If none does and exactly **one** row is unsettled, give it the leftover directly — provided the
   result divides by its QTY into a whole number of cents.

Worked on the real samples:

```
6V9430   two rows read "$03.70" for $83.70.  83.70+83.70 is the only pair that makes 623.14.
6V9293   one row lost its price ("$169").    46.68 as the TOTAL is the only reading that makes 431.70.
6V9335   one row read "$46.66" for $46.68.   46.68 is the only reading that makes 558.65.
6V9244   one row lost BOTH ("$2.19 $63.70"). No reading fits; the leftover is 83.70, over 30 units
                                             that is exactly the printed $2.79.  ← rule 4
```

Every repair records a warning naming the line and the recovered figures.

Rule 4 is safe only because **QTY is independently proven**. The printed `Totals: <units>` row is a
hard gate: if the quantities do not add up to it the file is refused, because a misread quantity is
absorbed by the unit cost — the invoice would still balance to the penny while the store's stock
came out wrong. It matched on all eight samples (206, 198, 181, 162, 159, 156, 150, 150).

---

## 5. The grand total is a vote

Chubiwinos prints the same figure in five places. Three of them are read — `NET AMOUNT`,
`TOTAL DUE`, `INVOICE BALANCE` — and the majority wins. Sample 6V9141's text layer says
`MET AMOUNT: $684.82` for a $584.82 invoice; the other two outvote it, the lines add up to 584.82,
and the operator is told the three disagreed.

`SALES TAX` has been `$0.00` on every sample. It is not currently added as a line; if it is ever
non-zero the invoice will simply fail to reconcile and be refused, which is the safe direction.

---

## 6. The date

`Date: 06/15/26 8:54 AM`, mm/dd/yy. The scanner reads the slashes as ones in six of eight samples,
so either character is accepted. What makes the shape unambiguous is the **clock time after it** —
no other line on the invoice carries one — and the result must still be a real calendar date. The
label itself is never matched.

The parser may read this for its own checks, but it never supplies the invoice date. The operator
types the date into an empty box on every invoice, beside the always-empty invoice-number box;
`InvoiceDetailsRequest` has no field capable of carrying either scanned value.

---

## 7. Barcodes — valid ones as printed, damaged ones from the catalogue

The codes are whole GTINs: 12-digit UPC-A for the `8xx`/`9xx` brands, 13-digit EAN-13 starting `750`
(Mexico) for the Sabritas/Barcel lines. **28 of the 35 distinct codes pass their own check digit**,
which per [ADDING_A_PDF_VENDOR.md §3](../ADDING_A_PDF_VENDOR.md) settles that they are sent
untouched and SSCS drops the check digit itself.

The other seven are scan damage, and every one of them is a **single wrong digit in a code that
appears correctly on another invoice**, because the product number beside it never changed:

```
1501011151062  →  7501011151062   FRITOS CHORIZO        (leading 7 read as 1)
7501011130669  →  7501011130869   TOSTITOS SALSA VERDE
7501011130676  →  7501011130876   TOSTITOS FLAMING HOT
7501011154665  →  7501011154865   PAKETAXO BOTANERO
7501011161667  →  7501011167667   PAPAS ADOBADAS
7500478012136  →  7500478012138   DORITO INCOGNITO
```

So `ChubiwinosCatalog` records what this vendor prints for each product number, and a code that
**fails** its check digit is replaced from it. A code that **passes** is always sent as printed and
never looked up — the check digit is arithmetic on the code itself, where the catalogue is only a
record of the past. If a valid printed code disagrees with the catalogue, the printed one wins and a
warning says the list may need updating.

**The catalogue is deliberately incomplete.** Two products have no reading good enough to record:

| Item | Product | Why not |
|---|---|---|
| 905 | WINNUTS PAPAS DIABLA 6oz | only ever scanned as 11 digits (`85088300206`) |
| 918 | WINNUTS CHICHARRONES CHILE-LIMON | **the number printed on the paper does not validate** — see below |

### Item 918 — the invoice's own printed barcode fails its check digit

Worth recording, because it will look like a parser bug and is not. Two independent readings of the
same product, one from the OCR text layer and one from a photograph of the paper at high
magnification, disagree — and **both fail**:

```
OCR text layer   6-50156-00733-1  →  650156007331   check digit should be 2, printed 1   INVALID
photo of paper   8-50158-00733-1  →  850158007331   check digit should be 4, printed 1   INVALID
```

So either the vendor's software prints a wrong number under the bars, or a body digit is being
misread in both. The arithmetic cannot say which: several single-digit corrections give a valid
code, and there is no way to choose between them. (`850158007334` — the same body with the correct
check digit — is the most economical guess, and is exactly the kind of guess §7 says not to make.)

Contrast the sibling item 900, `8-50883-00261-1` = `850883002611`, which validates cleanly. The
brand is not the problem.

**The fix is to scan the product**, not to read harder. The store's own scanner gives the
authoritative number in a couple of seconds; put that in the catalogue and item 918 stops prompting
forever. Until then the operator can type the printed number and confirm past the check-digit
warning — which is why that warning names scanning as the remedy rather than saying "you mistyped".

Those go to the operator, who is shown which product it is and types the barcode off the paper
(`BarcodeDialog`). Typing an 11-digit body is fine — the check digit is computed back. A typed code
that fails its check digit needs a confirmation before it is used. Skipping the prompt skips the
whole file: a line with no barcode has nowhere safe to go, and an invoice short of a line would book
the wrong money.

Do **not** guess an entry into the catalogue. A wrong one silently redirects every future invoice
for that product, and a wrong barcode is the one error the money cannot catch.

---

## 8. The invoice number is typed, and never pre-filled

The PDF prints `6V9389`. The store books it under its own number — `061526070552D` on the SSCS
screen, which starts with the date. The two have nothing to do with each other, so the number is
asked for and the operator's answer is what names the output file (`SSCS_061526070552D.xml`).

Both number and date boxes **start empty**, for this vendor and every other PDF vendor.
`InvoiceDetailsRequest` carries neither scanned value, which makes that structural rather than a
habit (`NoScannedInvoiceNumberOrDateCanReachThePromptForAnyVendor`). The reasoning: the scanners get this field
wrong silently (`0V9293`, `6)9141` here; `033105035263` for `033103035263` on Blue Bell), it carries
no check digit, and nothing downstream can catch it — so a filled-in box would turn the one field
that needs a person's eyes into an Enter keypress.

### What live SSCS holds for 6V9389

Read off the Line Items tab after the store keyed it by hand, and pinned in
`ChubiwinosPdfTests.MatchesWhatLiveSscsHoldsForInvoice6V9389`:

| ID/PLU | Description | Qty | Ea Cost | Ea List | Ex Cost |
|---|---|---|---|---|---|
| 750047804420 | COLMILLOS | 20 | 2.79 | 5.59 | 55.80 |
| 750101113086 | TOSTITOS VERDE MEXICAN | 24 | 2.79 | 5.59 | 66.96 |
| 750101111451 | RANCHERITOS MEXICAN | 30 | 2.79 | 5.59 | 83.70 |
| 750101113106 | DORITOS MEXICAN | 24 | 2.79 | 5.59 | 66.96 |
| 750047804595 | RECETA CRUJIENTE | 30 | 2.79 | 5.29 | 83.70 |
| 750047802555 | CHEETOS MIX 1905 | 18 | 3.89 | 6.99 | 70.02 |
| 60472200818 | LEO CHI CHA 3.53 | 12 | 2.99 | 6.99 | 35.88 |
| 750101116766 | SABRITAS ADOBADAS | −1 | 2.79 | 5.59 | −2.79 |

Six of the eight ids are exactly what the printed barcode leads to. **Two are not** — see §9.

---

## 9. Open questions

**a) Two lines the store keys under a different id than the barcode leads to.**

```
245 PAPA CRUJIENTE XTRA HOT   barcode 7501011180499 → SSCS 750101118049
                              store keyed            → SSCS 750047804595 "RECETA CRUJIENTE"
299 DORITOS FLAMING HOT       barcode 7500478012114 → SSCS 750047801211
                              store keyed            → SSCS 750047802555 "CHEETOS MIX 1905"
```

Both printed barcodes pass their check digit, appear on three and one invoices respectively, and are
consistent with the item numbers beside them. So this is not scan damage — the store's item file
simply holds those two products under older codes (or the operator picked the nearest existing item
by hand). The parser sends the printed barcode, so those two lines will arrive as **New Item** the
first time.

Nothing in the data can settle which is right. It needs a person to look at the two items in SSCS
and say either "merge them onto the store's existing id" (which would mean two catalogue overrides)
or "let the new items be created". Until then, do not add overrides on a hunch.

**b) The live round trip has not been done.** Convert one invoice, email it to the store's EDI
address, and read back the Paidout and Line Items tabs per
[ADDING_A_PDF_VENDOR.md §9](../ADDING_A_PDF_VENDOR.md). Use a test invoice number and delete it
afterwards. Whatever SSCS shows goes into `MatchesWhatLiveSscsHoldsForInvoice6V9389` as observed
fact.

---

## 10. Retail seed — 50%, not 40%

The invoice prints no retail column, so the each-list price is seeded from cost via
`RetailSeed.FromCost(cost, 0.50m)` — the same rule Blue Bell uses, at a different
margin. It is not a guess: SSCS shows **Ea List 5.59 against Ea Cost 2.79** on this vendor's lines,
and 2.79 at a 50% margin, rounded up to the next price ending in 9 within its dime, is exactly 5.59.

As always this only ever matters for a brand-new item — SSCS ignores it for anything it already
knows — but it must never be 0.00.

---

## 11. The vendor check is two markers out of six, not the name

The first version required the literal string `WINOS` on the page, and **a real invoice broke it
within a day** — 0V8790's header reads `111181W INOS S&G INC.`, so `CHUBI` is gone and a space has
split `WINOS` in half. The invoice was otherwise perfect: 167 units, $431.43, reconciling exactly.

These are photographs of a thermal ticket and **the header is the faintest part of the page**, while
everything the parser actually needs sits lower down and is legible. Anchoring the whole decision on
five characters up there was the mistake.

So six independent markers are counted and **two** are enough:

| Marker | Looks for |
|---|---|
| vendor name | `WINOS` or `CHUBI` |
| section banner | `SALES`/`DUMP` + `SECTION`, spaced out to survive `SALESS ECTION` |
| route | a line starting `Route` |
| driver | a line starting `Created` |
| balance | `INVOICE BALANCE` |
| totals | a `Totals:` row |

Measured across every PDF fixture in the repo: all nine Chubiwinos samples carry **all six** —
including 0V8790, whose name is unreadable but whose route, driver, section heading, totals row and
invoice balance are all intact — while the Blue Bell invoices carry **none**. No other
vendor prints a route, a driver, sectioned line items or an "INVOICE BALANCE".
`OnlyChubiwinosInvoicesAreAccepted` sweeps the whole fixture folder and is what keeps that true as
vendors are added.

If a real Chubiwinos invoice is ever still rejected, the scan is too faint to read at all — the
message says so, and says to re-scan.

## 12. Skip rules and gotchas

- A product row is a **3-digit item number at the very start of the line**, followed by a space.
  That single rule excludes the store's street address (`17828 FM 1314 RD` — no word break after
  three digits), every barcode line, the phone numbers, and the `Totals:` rows.
- The money columns are found by scanning **from the right**, because descriptions are full of
  numbers (`CHEETOS BOLITA -110 g 30 $2.79 $83.70` has three before the columns even start).
- A money token must carry its two decimal places. That is what keeps `-180`, `$169` and `602.` out
  of the money columns. A `,` or `:` in place of the point is accepted (`$50,22`).
- A quantity token with a `$` in it is never a quantity, which is how a damaged price is skipped
  rather than counted.
- The barcode line under a row tolerates **one** stray letter (`x 7-501011131040`); two means real
  words, so it is a wrapped description instead.
- A wrapped description needs two characters of content, so a lone speckle (`Y`, `l`) is not glued
  onto a product name.
- Parsing stops at `SALES:` — the first line of the summary band. One sample prints `SALES;`.
- Sample 6V9141 has a **second, truncated copy of its own header** on a following page. It falls
  after the summary band and is never reached.

---

## 13. Files

```
converter/src/EdiToSscs.Core/ChubiwinosPdfParser.cs      the parser
converter/src/EdiToSscs.Core/ChubiwinosCatalog.cs        item number → barcode
converter/src/EdiToSscs.App/BarcodeDialog.cs             the "type this barcode" prompt
converter/tests/EdiToSscs.Tests/ChubiwinosPdfTests.cs        parser proofs, all 9 samples
converter/tests/EdiToSscs.Tests/ChubiwinosPipelineTests.cs   end to end, prompts included
converter/tests/EdiToSscs.Tests/Fixtures/PDF/CHUBIWINOS_*.pdf   the 9 real invoices; the
    _faint_header one is 0V8790, whose vendor name is unreadable - keep it, it is the regression
    test for the vendor check
```
