# CLAUDE.md — vendor invoices → SSCS NAXML converter

Claude-specific guidance for this repo. Full agent rules: **[AGENTS.md](./AGENTS.md)**.
Format detail: **[analysis/FORMAT_SPEC.md](./analysis/FORMAT_SPEC.md)**.
PDF input path: **[analysis/PDF_TO_SSCS.md](./analysis/PDF_TO_SSCS.md)** + per-vendor profiles in
**[analysis/vendors/](./analysis/vendors/)**.
**Adding a PDF vendor: [analysis/ADDING_A_PDF_VENDOR.md](./analysis/ADDING_A_PDF_VENDOR.md)** —
standalone, needs no other reading.

## What this is

Production **.NET 8 WinForms** app that turns vendor invoices into **NAXML-BusDoc 1.5** files for
SSCS. Two input paths, one output format:

- **EDI to SSCS XML** — the fixed-width `.txt`/`.edi`/`.edi13` files vendors email (four layouts:
  standard EDI, EDI13, Ruby Red EDI13, STRP/Alp).
- **PDF to SSCS XML** — vendor PDF invoices (**Dr Pepper**, **Blue Bell**, **Chubiwinos S&G**,
  **Bimbo Bakeries**, **Red Bull**), read with **PdfPig**. The tab
  has a vendor dropdown; each vendor has its own parser.

Output is a portable single-file `win-x64` EXE under `converter/publish/`.

**The fixed-width "SSCS EDI" output was retired on 2026-07-25.** Its 11-character product-code
field could not hold a 13-digit EAN-13, which collapsed 72 VM Novelties line items onto 40 IDs and
got the file rejected. NAXML has no such limit. A snapshot of the old version lives outside the
repo at `C:\Users\Kashf\Desktop\EDI version SSCS`, and in git history.

Work inside `SSCS_EDI_FORMAT/` (or that folder as the workspace root).

## The three facts that govern everything

Verified in live SSCS on 2026-07-25. Re-read these before touching conversion logic.

1. **SSCS drops the last character of the GTIN itself.** Send `600609100271`, SSCS stores
   `60060910027`. Send the 13-digit `6917673180912`, it stores `691767318091`. Always send the
   whole barcode; never pre-drop a check digit, never strip a leading zero.
2. **SSCS rounds `units × unit cost` to the cent per line, then sums.** So 4-decimal unit costs are
   safe — `96 × 1.3542 = 130.0032` showed as `130.00`, and a 16-line invoice still totalled exactly
   $532.43. Errors do not accumulate.
3. **Store routing is the email address the file is sent to,** not anything inside the file. Every
   store has its own EDI email address. The `ShipTo`/`Location` fields are decoration.

A fourth, verified 2026-07-26, follows from the first: **SSCS does not strip leading zeros.** Blue
Bell prints its UPC column as the 11-digit body padded to 12 (`007189905101`). Sent as printed it
became a *New Item* under `00718990510`; sent as the real UPC-A `071899051015` it matched the
store's existing `07189905101` in department ICE CREAM. Restore the check digit — see
[analysis/vendors/BLUE_BELL.md](./analysis/vendors/BLUE_BELL.md) §3.

**A sixth, 2026-08-11: a second *reader* is not a second calculation.** `QTY x PRICE` checked against
`TOTAL` is the same OCR pass twice, which is why it cannot see a glyph the scanner renders wrongly in
both columns. A vision model reading the **picture** fails differently. It confirms the established
Red Bull's parser; Bimbo and Dr Pepper are now picture-primary and use three independent picture
passes for every UPC. See `GeminiVisionReader.cs`.

**A fifth, learned the hard way on 2026-08-11: a checksum may confirm a figure, never supply one.**
Red Bull ticket 2035939353 reached its own printed $394.84 exactly, through four wrong rows whose
errors cancelled to the cent, and reported it with complete confidence. A calculation whose input was
misread returns a confident wrong answer, and `QTY × PRICE` is not a second opinion about `TOTAL` —
it is the same scan damage wearing a different hat. So where two *different* readings of a figure
agree it is taken; where they do not, or where there is only one, the operator is shown the printed
row and asked. Dr Pepper no longer uses OCR agreement at all: Gemini owns its values and OCR only
locates the exact highlight for a question raised by Gemini's own checks. **A barcode is the
exception** — Red Bull's carries its own check digit, while Bimbo and Dr Pepper require agreement
across three independent picture requests. That is verification rather than arithmetic.


## Do / don't

| Do | Don't |
|----|--------|
| **Confirm a printed figure, or ask** | **Work a figure out and use it** |
| Send whole barcodes and let SSCS drop the check digit | Pre-truncate a code to 11 characters |
| Prove a barcode change with `EveryBarcodeLandsOnTheSameSscsItem` | Assume an id change is harmless |
| Use a checksum to say *something is wrong* | Refuse a delivery a person could fix in seconds |
| Fail closed on a bad UPC-E check digit | Guess at a mistyped barcode |
| Warn only when a human must act | Open a window after the work is done |
| Mark a note `Routine` when it fires on nearly every file | Let a status colour mean "this vendor has returns" |
| Populate each-list from the vendor's own figure | Write `0.00` and risk a free sale |
| Edit conversion in `EdiToSscs.Core` | Put business logic in WinForms |

## Key code map

- `EdiConverter.cs` — reads all four fixed-width layouts into `Invoice`; header parsing, the
  case-cost/pack/cases money model, barcode → GTIN rules
- `Invoice.cs` — the shared model (`Invoice`, `InvoiceLine`, `ShipToInfo`) both paths produce
- `NaxmlBuilder.cs` — the one place XML bytes are written
- `Upc.cs` — check-digit maths; `UpcE.cs` — UPC-E expansion
- `PdfTextExtractor.cs` — PdfPig text layer → visual lines. `ExtractPositionedLines` is the one pass;
  `ExtractLines` is projected off it so a line number reported by a parser and the band of paper a
  snip cuts can never drift apart.
- `PdfRowSnipper.cs` — cuts the picture of a printed row out of a scanned invoice. Every vendor PDF is
  a photograph with a machine's transcription underneath, and **only the transcription has ever been
  wrong**, so a question shows the row rather than describing it. The question is five words —
  "Type the number under
  UNITS" — naming the column from `LineFieldRequest.ColumnName`, because the vendors disagree about
  it (Dr Pepper's last column is AMOUNT, Red Bull's is TOTAL, and Bimbo calls its count QTY where the
  other two call theirs UNITS) and sending someone to look for a
  column that is not on their paper is worse than saying nothing. The ticket's own headings are cut
  from the same page at the same width and shown **directly on top of** the row, so UNITS sits over
  the units. That strip is what makes a snip readable at all: a cut is two printed lines out of the
  middle of a ticket, and by item 16 the printed header is a foot further up the page. Dr Pepper is
  strict: the prompt requires the invoice's photographed three-line section strip (SALES/CREDITS
  banner plus both column-header lines), a crop of
  both lines of the requested product, and a box around only the requested value. It never uses
  `ColumnLegend`, never opens a whole scrollable page, and refuses the question when any of those
  coordinates are missing. This matters because CREDITS has no WHSLE column and a drawn sales legend
  can shift the operator onto the neighbouring value. Other vendors may still use the deliberately
  weaker typed fallback. **`IsColumnHeader` needs a pattern per vendor and it is not obvious that it does**
  (2026-08-12): it matched on `UNITS` + `AMOUNT`, which are Dr Pepper's words, so every Bimbo question
  silently fell through to the typed legend and shipped that way. Bimbo prints QTY rather than UNITS,
  and its scans render AMOUNT as `ANOLNT`, `AHRUNT`, `AH00NT`, `AHUUNI`, `ANOUNT`, `ANO NT`, `AMONT`
  and `ANDUTI` — matching on it finds the header on four samples out of eleven. `RETAIL` + `PRICE`
  survives on all eleven and appears on no product row. Bimbo also splits its headings over **two**
  printed lines (`ITEM / SUGG RETAIL / WHOLESALE` above `UPC No QTY RETAIL AMOUNT PRICE AMOUNT`), and
  the lower one alone reads "RETAIL AMOUNT PRICE AMOUNT" over four figures — naming two of them twice
  on the one vendor with two adjacent AMOUNT columns. The band is extended upward when the line above
  carries RETAIL without PRICE. Dr Pepper's two-line heading is extended upward separately when the
  preceding line contains DESCRIPTION and UPC/SKU. The failure mode to remember is that a missing
  cut **looks fine** if a typed legend renders, even though it cannot line up; Dr Pepper therefore
  has no such fallback. Where another vendor permits one, it is deliberately second best and looks
  it (grey ground, our own font), because
  it cannot align with a scan and must not invite anyone to read straight down off it. **Neither one
  is ever marked** — the box goes on the figure being typed, never on a column name. **Which lines a row spans is the parser's to say, never the snipper's:** Dr Pepper
  prints the barcode on the product line *above* the priced row and Red Bull prints it on the line
  *below*, so "one line up" is right half the time and produces a real row of a real invoice with the
  wrong figure marked on it the other half. Nothing but a question ever opens a window — a note goes
  to the log and to the file list, because by the time one is written there is nothing left to do
  about it. Best-effort by design: `TryCut` never throws and
  returns null for a PDF that was never a scan, because the message it decorates has to stand on its
  own. Dr Pepper's picture-primary rows are located from page, SALES/CREDITS section and page-local
  row order in the OCR geometry; OCR text never supplies or checks the value. A Dr Pepper question
  requires an exact value box rendered through the same `SnipView` colour palette as Bimbo, and
  missing coordinates require a rescan rather than an unmarked
  whole page. The model's old `verticalPosition` is not used. Red Bull's image-only fallback can
  still open an unmarked whole page with the row named above it. A WinForms note that cost a day: a panel's
  `AutoScrollPosition` set **before layout** is silently clamped to zero (its maximum is still the
  default 100) and must be set again on `HandleCreated`. **Turn the page over is gone** (2026-08-12,
  at the user's request): a page nobody can read is a page to re-feed at the scanner in five seconds,
  not one to read sideways off a screen and type from. The picture is still turned automatically
  where the reading says the sheet is inverted, which costs nobody a click.
  The text-layer marker was rendered for all 170 questions the fixtures raise
  and 31 of them were looked at (every Dr Pepper one, and Red Bull sampled across every fixture and
  every field); 30 were right and one was not. That one is worth knowing about, because Red Bull's OCR
  transcribes the printed **bars** into a token of ones (`1111'1"III511111` above `611269332827`) and
  boxing it pointed confidently at something unreadable. A token that does not carry twelve digits is
  not the code and gets no box (`RedBullSnipTests.TheBarsAreNeverMarkedAsIfTheyWereTheCode`).
- `DrPepperVisionReader.cs`, `GeminiVisionReader.cs`, and `DrPepperVisionParser.cs` — **one picture
  request, then XML** (rewritten 2026-08-13). Gemini owns every value; the parser maps them and
  never argues. OCR is not consulted at all on this path any more — Dr Pepper asks no line
  questions, so there is nothing to draw a highlight on.

  **It does not refuse invoices.** The old parser had twenty-three ways to, and across a full day of
  the store's real tickets not one caught a real error while every refusal that day traced back to
  our own handling. A figure that does not reconcile is now written into the file with a note on it.
  The only refusal left is a reading with no postable row in it — a row needs a barcode, a unit
  count and an amount, and a row missing one is dropped and named with its money so it can be added
  by hand. `ParseDrPepperReply` is the matching gate on the AI side and requires exactly two things:
  one usable row, and AMOUNT DUE. **Do not add fields back to it.** It used to demand CASES, NET,
  TAX and WHSLE on every row plus seven top-level figures, and refused a flawless 49-row reading
  because `totalSale*` was empty — a ticket carrying returns prints a SALES SUMMARY block *instead
  of* a TOT SALE line, so that figure is not on the paper.

  **Two checks survive, and neither blocks.** The UPC-A check digit (arithmetic on twelve digits, no
  request, no prompt) and the printed unit count. They are kept because these are the two mistakes
  nothing downstream can see: a wrong code posts the delivery to a duplicate item nobody will ever
  scan, so the real product shows nothing received; a wrong count balances to the penny and leaves
  the shelf short for good. Both produce a note naming the row. A code that fails its check digit
  **ships as read** — never padded, repaired, or replaced by a catalogue lookup, because a repaired
  code looks perfect and is the one failure that hides.

  **The two extra barcode passes are gone.** They were Bimbo's design, and Bimbo needs them: it
  prints a UPC body with no check digit. Dr Pepper prints a whole UPC-A. On the day they were
  removed the check digit had caught three misreads out of three while the extra passes caught
  nothing it had not — and one of them slid a row out of step and turned a thirty-row ticket whose
  money was exact into twenty-nine questions. They also cost two thirds of the free allowance, which
  is what decides whether an invoice is read by Flash or by Flash-Lite.

  The prompt is where a misread is actually caught, because the model can look at the page again and
  we cannot — hence the explicit final pass in `DrPepperPrimaryPrompt` (count the digits, verify the
  check digit, multiply the row out, add the subtotals up). Those checks say *where to look again*,
  never what to write. Invoice number and date stay absent from the schema and typed into empty
  boxes. If Gemini is unavailable the legacy parser still requires explicit once-per-batch approval;
  "unreadable" is a rescan and never a quiet OCR read. Routine credits remain green as negative
  quantities at positive costs.
- `DrPepperPdfParser.cs` - explicitly approved legacy Dr Pepper OCR parser. Three things beyond the plain layout. **Ink:**
  invoices come back marked up in pen and OCR turns a stroke into a stray token, so a token with no
  digit in it is discarded — but only white space and the unused NET/TAX columns may absorb one. A
  mark *welded to* a figure comes off only when the figure is **money** (`20.66-`), because money is
  re-added by the package subtotal and again by AMOUNT DUE, so a wrong strip fails loudly. A mark on
  a **count** stays damage: a count is what those totals are made of. A **letter is never a mark** —
  `1Z` is 12 with the 2 lost, `5b.14` is 50.14 — so rubbing one out would delete a digit and leave a
  figure that looks clean. A mark on a barcode cannot be caught by the total gate (the money still
  adds up), so the tell is **a priced row no product line claimed** — including a *damaged* one,
  which is how invoice 5218300220 lost $115.95 in silence. A row that **lost a column** is read from
  its tail, since NET/TAX/AMOUNT are the last three and all three are money; before that, such a row
  went unrecognised and took its whole product line with it (invoice 4643519283). **Quantities:** the
  money cannot check a UNITS column — 11 for 12 leaves every dollar on the page correct and books a
  unit short — so units are reconciled against the **package subtotal** (which states the pack size,
  `units ÷ cases`, and so names the damaged row) and against the invoice's own count of itself:
  **TOT SALE**, or the **SALES SUMMARY** block a ticket carrying returns prints instead. Those are
  **checks, not sources**: a subtotal that disagrees has said its whole piece — this row is wrong —
  and filling the count in from `cases × pack` would rest on QTY having been read right, so the row
  is shown to the operator instead. With no prompt available the invoice is refused, never guessed. A damaged **AMOUNT is never back-filled from AMOUNT DUE**: that
  would satisfy the only checksum by construction. WHSLE and CASES are the exception that does *not*
  ask, because they seed nothing but a new item's shelf price. Its **invoice number is typed like
  every other vendor's** (2026-08-10) and overrides the printed one. In production the date is also
  typed into an empty box and AMOUNT DUE is validation-only; the legacy parser never asks for it. **An invoice
  that does not reach its own AMOUNT DUE is no longer refused** (2026-08-11): the total says
  *something* is wrong, so the rows that cannot be right are asked about first — `CASES x NET` is
  the row printing its own money a second time in different glyphs, and on the two samples that hit
  this it names one row out of 22 and 29 — then the rows nothing checked, and only then is it
  refused. A subtotal keeps its group when the scan destroys only its **dollar figure**
  (`PEG 21 504 682.S0`); losing it took the group boundary too and put eleven rows in question for
  one misread UNITS. A page that reads as *nothing at all* — no date, no total, no product row — is
  refused **before any prompt**, because it went through the scanner upside down. **Credits:** a
  `CREDITS` banner switches the page to a shorter column set with no WHSLE and money in brackets;
  they are line items with **negative units at a positive cost** — the invoice does not reconcile
  without them, and the summary block is the only count they get. Read
  [analysis/vendors/DR_PEPPER.md](./analysis/vendors/DR_PEPPER.md) before touching its code token: a
  period and a letter each need a second, deliberately stricter pattern.
- `BlueBellPdfParser.cs` — Blue Bell PDF → one or more `BlueBellSection`s (a ticket can carry a
  Sales *and* a Returns invoice); `ToGtin` holds the check-digit rule, `SeedRetail` the 40% margin,
  `PackFor` the units rule — **a `DZ` line is a carton of twelve, so its QTY is not units**
  (verified in live SSCS: QTY 2 @ $6.48 is held as **24 @ $0.54**). A box is a case, not a unit.
- `ChubiwinosPdfParser.cs` — Chubiwinos S&G PDF → `ChubiwinosTicket`. Its scans damage the **money
  columns**, so a row that does not multiply out is settled by which reading makes the invoice's own
  total (`Resolve`), and the printed `Totals: <units>` count is a **hard gate** on the quantities —
  a misread QTY balances to the penny and still books the wrong stock. The grand total is a
  **majority vote** of the three places it is printed. `ChubiwinosCatalog.cs` holds item number →
  barcode and is consulted **only** for a code that already failed its check digit; a product it has
  never seen goes to the operator. **Not yet round-tripped through live SSCS** — see
  [analysis/vendors/CHUBIWINOS.md](./analysis/vendors/CHUBIWINOS.md) §9.
- `BimboVisionParser.cs` — Bimbo Bakeries delivery ticket → `BimboTicket`, with
  `GeminiVisionReader.ReadBimbo` as the normal source. **Round-tripped through live SSCS 2026-07-28**,
  which booked DELIVERIES and RETURNS as **one** $335.07 paidout; returns are negative lines on that
  invoice. QTY is already individual units (`SS2P` is one sellable two-pack) and PRICE is already unit
  cost. The first picture request copies every row plus the delivery, return, ticket and cash totals,
  checks each row twice, and reconciles those independent totals before returning. Core parses the
  returned strings strictly, redoes every row/band/ticket check, and boxes only a disputed UPC, QTY,
  PRICE or AMOUNT for the operator. OCR text supplies **coordinates only** for that box; none of its
  product values may enter the invoice.

  Bimbo prints a ten-digit UPC body without the leading zero or check digit
  (`74323-09183` → `074323091830`), so it cannot validate itself. A second focused picture request is
  given row locators but **not the first UPC**. The code is accepted only when the two reads agree and
  the known Bimbo manufacturer prefix is present; otherwise the exact row is asked. There is no
  hardcoded product catalogue: new Bimbo items require no source change or rebuild. If AI is
  unavailable before reading, the app offers the legacy `BimboPdfParser` once for the whole queue
  batch. If AI reaches the page but still cannot return a structurally consistent invoice after its
  retry, the page must be rescanned; OCR figures are never mixed into a partial AI invoice. **Do not
  use SUGG RETAIL** — retail is seeded at 45%.
  See [analysis/vendors/BIMBO.md](./analysis/vendors/BIMBO.md).
- `RedBullPdfParser.cs` — Red Bull delivery ticket → `RedBullTicket`. **Round-tripped through live
  SSCS 2025-11-24**, which booked a two-page ticket (a $367.00 delivery invoice and a −$24.50 credit
  invoice with its own number) as **one** $342.50 paidout of ten lines — so the whole PDF is one
  payable, however many pages. `UNITS` is already individual units and is SSCS's Qty directly; unit
  cost is `TOTAL ÷ UNITS`, which also works on a credit row where no case price is printed.
  Barcodes are full 12-digit UPC-A **with a real check digit**, so unlike Bimbo the page validates
  itself; `RedBullCatalog.cs` only picks between the six single-digit repairs a bad check digit
  allows. **Its printed total does NOT gate the parse** — the scanner mis-renders a glyph the same
  way everywhere, so one sample's $918.33 reads $910.33 in all three places it appears, and PRICE
  and TOTAL agreeing means nothing because they are the same glyph twice. Read
  [analysis/vendors/RED_BULL.md](./analysis/vendors/RED_BULL.md) §4 before touching its money.
  **Its columns are calibrated from the product rows only** (2026-08-11): the TOTALS block is a
  narrow stack of five money figures, and on a one- or two-SKU ticket that was the only stack dense
  enough to qualify as a column, so those tickets found no products at all and were refused.
  Reads positioned words (`PdfTextExtractor.ExtractPages`) because its columns cannot be told apart
  by reading order once the scan has split or dropped one — which is why `PdfPositionedWord` carries
  a **line number**: a vendor read by column has none of its own, and without one no question about
  a row could show that row. Its column headings are OCR debris (`PflICE`, `IDEAL`), so the header
  line is found by **position** — the row above the first product, guarded by carrying no money —
  and its *picture* is the legend. A ticket that does not add up is never refused: every amount
  nobody has looked at is shown, and once a person has read them all the lines are used and the
  disagreement is said out loud (`SettleAgainstThePrintedTotal`). That took the store's ten samples
  from 5 converting to 9. `CheckRedBullUnits` is the backstop, because a row the scan lost takes its
  dollars with it and only the printed unit count can see it. Its printed total may only **confirm**
  a row's own reading,
  never replace it — `REDBULL_2035939353_total_reached_wrong_way` reached the printed $394.84 exactly
  through four wrong rows whose errors cancelled, so reaching the total proves nothing when the truth
  was never among the readings the scan produced. That ticket is refused now. §4 of the vendor doc
  has the table.
- `VisionInvoiceReader.cs` — the contract for reading an invoice off the **picture** rather than the
  text layer: `VisionRow`/`VisionReading`, and `AlignTo`, which decides which parsed row a vision row
  belongs to. That alignment is the dangerous part — a reading tied to the wrong row puts one
  product's figures onto another and every total still adds up — so it goes by **position**, checked
  first against the rows themselves (a pair agrees on its item number, its count or its amount;
  more than half must agree, and a one-row ticket is forced). Position rather than the item number
  because that column is OCR debris on these scans (`O84815`, `9/221027`), so a key match would hold
  only on the rows nobody needed help with. Failing that, rows tie by key. There is deliberately
  **no "last one standing" rule**: pairing off the single unclaimed row looks forced but is satisfied
  exactly by a model that dropped one row and invented another.
- `GeminiVisionReader.cs` — the Gemini implementation. One request per page, `temperature 0`, money
  returned as **text** so a cent never goes through a float, and a prompt whose first rule is *never
  guess* — a blank sends the row to a person, a guess sends it to the register. **The barcode rules
  are per vendor, not shared** (2026-08-12, when Bimbo was added): Red Bull and Dr Pepper print a
  whole UPC-A and are told to count to twelve and check the last digit against the other eleven, and
  giving Bimbo the same instructions would be worse than saying nothing — it prints the body only, so
  a model told to reach twelve digits pads one into a plausible code and §3 of its vendor doc is why
  nothing downstream would notice. Splitting that block out left the other two prompts character for
  character as they were, which `SplittingTheBarcodeRulesOutLeftTheOtherTwoPromptsUntouched` pins;
  both were tuned against the store's own scans and neither may drift by accident. The blocks
  concatenate with no separator in code — each opens with its own blank line — so every line ending
  in a prompt comes from the source file and they are all the same. **Every worked
  example in that prompt has to be a real row that satisfies the rule printed under it** (checked
  2026-08-12): the Dr Pepper example had the code before the name, which is backwards, and showed 2
  cases × 21.38 landing on 21.38 directly beneath "CASES x NET = AMOUNT". And the row arithmetic is
  now scoped to **deliveries only**, because it does not hold on a credit either way round — Dr
  Pepper's credits print `CASES 0` against a real amount (`0 1 6.37 0.00 (6.37)`) and Red Bull's are
  priced per unit off a fractional QTY (`0.08 (2) ... $2.22 ... ($4.45)`). A model told to check a
  rule that cannot hold goes back and "corrects" the row, on exactly the lines the store watches. Every failure (no
  key, no network, refusal, throttling, unparseable reply) is `VisionReading.Empty`, exactly like
  `PdfBarcodeReader`'s empty list. `CheckTheKeyWorks` is the Test button, for the same reason Test
  Email exists. Key lives in `VisionSettings`, DPAPI-encrypted, behind the admin PIN; **off by
  default**, and off means the converter behaves exactly as it did before this existed.
  **The response schema is per vendor too, and for a sharper reason than the prompt**
  (2026-08-12). A schema field whose name matches a column heading the vendor does not mean by it
  **silently steals that column**, and the prompt loses the argument — the schema is what the reply
  is validated against. Bimbo's one count column is headed `QTY` and holds individual units, which
  this contract calls `units`; while a field spelled `qty` was also on offer the model filled that
  instead, `units` came back null on every row, and nothing settled. Ticket 84294890013049 asked
  **eleven questions on eleven rows**, each one already carrying the description and the amount the
  model had read perfectly well. Bimbo is now offered exactly one count field; Red Bull and Dr Pepper
  bill in cases and keep both. Pinned by
  `OnlyTheVendorsThatBillInCasesAreOfferedACaseCountField`. The general rule when adding a vendor:
  **read the schema's field names next to that vendor's printed column headings and look for a pair
  that collide.** Bimbo's other near-miss is `amount` against its *two* columns headed AMOUNT (retail
  and wholesale) — that one is disambiguated in the prompt by position and by PRICE being the row's
  only four-decimal figure, and it fails safe besides, since a retail amount fails
  `QTY x PRICE = AMOUNT` and sends the row to a person rather than to the register.
  **What a read cost goes to the log** (`VisionReading.Note` → `LogEntry.visionRead`): pages,
  requests, rows, how many came back throttled, how many ran out the 90-second clock, and seconds
  spent asleep on the free allowance. The second reader runs *before* the first prompt opens, so its
  time is time the operator spends watching a window that has not appeared yet — and "the AI took two
  minutes" has four causes that need telling apart and cannot be told apart from outside: a long
  reply, a request that hit the timeout **and was then retried** (a timeout is counted as throttled,
  so one slow page can cost 90s twice over), a genuine 429 costing a 20- or 50-second sleep, or a
  slow line. A read that comes back empty still writes its note, because two minutes and nothing is a
  different fault from two minutes and forty rows.
- **A page the text layer cannot read is no longer a lost file.** Dr Pepper sends the complete page
  pictures to its dedicated Gemini-primary reader even when no OCR text exists. OCR is optional
  geometry for exact operator highlights only; it never contributes a row or figure. The primary
  response must carry every physical product, package subtotal, TOTAL SALE, sales/credit summary and
  AMOUNT DUE, and Core validates those independent checks. Bimbo has the same picture-primary value
  boundary. Red Bull retains its separate `RowsFromThePicture` second-reader path.
- How a Red Bull vision reading is used: it is a **second reader, never an oracle**. It joins the existing
  settle ladders as another candidate — in Red Bull's `SettleByPrintedTotal` the printed total may
  now confirm a *picture* reading as well as a TOTAL column, which is what repairs a row that
  reconciles with itself (one ticket prints $54.70 and the scan renders `$64.70` in PRICE and TOTAL
  both). Where the two readers agree, the figure is taken. Where they disagree, only arithmetic over
  genuinely different glyphs may overrule the picture — so a **QTY 1 row may not**, because its
  `QTY x PRICE` is the same figure twice. Where nothing independent separates them, the operator is
  still asked. Dr Pepper does not enter this settle ladder: Gemini is authoritative, and its own
  row/subtotal/summary checks can raise an exact highlighted question but never accept an OCR value.
- `PdfBarcodeReader.cs` — decodes the printed **bars** off a scanned page (ZXing), tied to the text
  line they sit on. Red Bull only; Dr Pepper prints no bars. This is the one encoding on these pages
  with error correction in it, read by code that shares nothing with the OCR — so it fails
  differently, which is the whole point. It is **not** clean: 6 of 174 decodes were wrong and
  satisfied their own check digit, so it sits at rung 2 of `ResolveBarcode` — ahead of the
  single-digit repairs, because a repair is a guess among six equally valid candidates, and behind a
  clean reading of the digits, which it may only ever *disagree* with (that goes to the operator).
  Took the store's twenty tickets from 17 barcode questions to 3, and from 6 wrong product codes to
  0 out of 222. Best-effort: an empty list changes nothing.
- `RetailSeed.cs` — the each-list seed for vendors whose invoices print no retail column. 40% for
  Blue Bell; `FromCost(cost, margin)` takes the vendor's own margin, which is how
  Chubiwinos gets the 50% that reproduces the Ea List 5.59 SSCS shows against a 2.79 cost
- `ConversionService.cs` — read / convert / atomic write (**writes N files for N invoices**);
  `ConversionCoordinator.cs` — queue; `IInvoiceDetailsPrompt` — asks the operator for the Blue Bell
  and Chubiwinos invoice number, for **the date on every invoice**, and for a Chubiwinos barcode that
  neither read nor is on record. **The date box is empty, exactly like the invoice number**
  (2026-08-12): same weakness — no check digit, printed once — and a wrong year is the one thing
  these scanners produce that looks entirely plausible, two of twenty-two samples booking themselves
  into 2016 and 2020 for 2026. It took three goes. Asking only when the scan failed missed the
  plausible-but-wrong case entirely; asking with the scan's reading *in the box* is an Enter
  keypress; and underneath both, `InvoiceNumberDialog.Date` returned `_knownDate ?? typed`, so a
  corrected year **was discarded** and a real invoice went out dated 2016 with the guard nominally
  working. Now: empty box, typed every time, what is typed is what is used, and a blank is refused
  rather than falling back to the scan. `InvoiceDetailsRequest` carries **no date property at all**,
  for the same reason it carries no suggested invoice number — a suggestion the
  prompt cannot receive cannot be wired back in by accident
  (`NoScannedInvoiceNumberOrDateCanReachThePromptForAnyVendor`). `AskLineField`
  asks for **one figure on one Dr Pepper, Red Bull or Bimbo row** — its UPC, units, unit price or
  amount — by
  showing the printed row with that figure boxed, rather than by describing it. Bimbo prints **one
  line per product**, so its snip is a single line where the other two cut a two-line band and have
  to say which of the two carries the barcode; and it asks about `QTY`, not the `UNITS` both the
  others print, which is exactly what `LineFieldRequest.ColumnName` exists for. **Never the description:** SSCS
  overrides it for a known item, so asking would be a prompt that changes nothing. **The invoice-number
  box is never pre-filled from a scan, for any vendor** — `InvoiceDetailsRequest` deliberately has no
  suggestion field, so it cannot be
- `OutputNaming.cs` — `SSCS_*.xml` filenames; `BuildInvoiceOutputFileName` names by invoice number
- `EmailSender.cs` — SMTP send of the converted XML to the store, which is what actually routes an
  invoice (fact 3). **One invoice per message.** Conversion always saves to the output folder;
  sending is the separate **Send Email** button, which sends every converted invoice on the tab with
  no row selection. `QueuedFile.MarkEmailed`/`WasEmailed` stops a second press double-sending — a
  duplicate books the delivery twice in SSCS and nothing downstream catches it.
- `SecretProtector.cs` (DPAPI) / `AdminPin.cs` (PBKDF2) — the app password and the PIN gating the
  email settings. Both fail closed and neither is ever shown back. Needs a **Gmail App Password**
  (so 2FA on the sending mailbox). A credential-free Gmail-compose-link version was built and
  removed on 2026-07-27 — a web link cannot attach a file. Don't re-propose it.
- `converter/src/EdiToSscs.App/MainForm.cs` — UI only (Settings + PDF tab + EDI tab, in that order);
  `XmlViewerDialog` reads the in-memory documents, so it still shows the XML if the saved file moved
- `Assets/app.ico` — the shell icon (`ApplicationIcon` in the csproj) and, via `AppIcon.cs`, the
  window icon. It holds **different artwork per size**: lettered PDF→XML chips at 96px and above,
  and at 16–24px just the red and navy blocks, because the lettering is illegible there
- `converter/tests/EdiToSscs.Tests/` — `XmlConversionTests` is the primary proof suite

## Commands

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk8"; $env:PATH="$env:LOCALAPPDATA\dotnet-sdk8;$env:PATH"
cd converter
dotnet test tests/EdiToSscs.Tests -c Release
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

## Testing without a reference file

There is no byte-reference for XML — SSCS consumes these files, it does not produce them. So the
suite proves behaviour instead of bytes:

- **`EveryBarcodeLandsOnTheSameSscsItem`** is the important one. For every input fixture with a
  paired retired-output file, each GTIN minus its last character must equal the code the old
  converter wrote. The only permitted difference is a 13-digit EAN keeping one extra character.
  This is what guarantees moving a vendor to XML creates no duplicate items.
- Totals reconcile line by line, output is ASCII/CRLF and well-formed, and the Dr Pepper fixture is
  checked against the 16 Ex Cost figures live SSCS displayed.
- **`MatchesWhatLiveSscsDidWithBothBarcodeForms`** pins the Blue Bell round trip: the as-printed
  code created a new item, the check-digit form matched the store's own. Both Blue Bell suites run
  against the **real** OCR text of the sample scans, damage included.

## Scope discipline

- Prefer small, proven changes over broad refactors.
- Do not commit `bin/`, `obj/`, or the published EXE.
- Do not change git config; commit/push only when asked.
