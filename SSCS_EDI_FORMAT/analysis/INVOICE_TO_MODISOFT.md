# Any invoice → a Modisoft file

This is how to turn **one invoice, from any vendor**, into the EDI file Modisoft imports, by hand,
with an AI doing the reading.

**Since 2026-10-15 the converter does this itself.** The **PDF to Modisoft EDI** tab has no vendor
dropdown: drop any vendor's invoice (PDF or photo) and Gemini reads it in one request
(`AnyInvoiceParser`). Use this guide only when the tab cannot: Google's AI is busy or out of
allowance, the invoice prints no barcodes or only part of them (Faust, Frito-Lay's last five
digits, Mini Melts cut off at "…"), or a file the tab made needs fixing. The tab names every line it
could not write, every barcode it completed and every units-per-case it took from the description,
so the notes on the converted row say what to check.

This guide's table is for every vendor, the five SSCS ones included. (A `HandReading` tool that fed
those five vendors' own readers was removed on 2026-10-15, once the tab stopped using them.)

It was worked out on **Silver Eagle invoice 424753** (2026-10-06, Best Stop 16's opening beer order:
173 lines, 327 cases, $9,357.09). The file this procedure produced went into Modisoft with every
line matched and the total exact. That invoice is kept as a worked example in
`converter/tools/ModisoftFile/examples/SILVER_EAGLE_424753.tsv`.

---

## 1. Get every line off the paper, and prove you did

Capture, for every line: the vendor's item number, **quantity in cases**, description, the **pack
text** under it (`2/12 LNNR`, `24/16 OZ CAN`, `12/24OZ CANS`…), the **UPC**, price, discount, and
the line amount. Keep the lines with 0 cases in your notes. They weren't delivered and get left out.

**Phone photos and scans have no text layer.** Pull the page pictures out of the PDF. The
converter's `PdfPageImages.TryReadAll` does it from a scratch console project that references
`EdiToSscs.Core`. Cut each tall page into overlapping strips about 900 px high (Python PIL works),
and read the strips. Several photos of one long receipt **overlap**. Drop repeated lines by item
number. On Silver Eagle, the third photo began with six lines already on the second.

**Do not go on until all four checks pass.** Together they make a misread nearly impossible to
miss:

- every line: `cases × (price − discount) = amount`, to the cent;
- the cases add up to the printed case count (Silver Eagle: `Cases: 327`);
- the amounts add up to the printed total (`Invoice Total 9,357.09`);
- every UPC passes its check digit.

If one fails, zoom into that part of the photo and read it again. Don't adjust a number to make
a sum work.

## 2. Work out the units in one case — look each barcode up

Modisoft books a line as **Qty** (cases) × **Case Cost**, and divides by **Units** (sellable items per
case) to cost one item. The sellable item is **whatever the printed UPC is**. If the UPC is a
6-pack's, a case of 24 cans is 4 units; if it is one can's, it is 24.

**The pack text is not enough.** It describes what is in the case, not what the barcode is. On
Silver Eagle these would all have been wrong from the text alone:

| Pack text | What the UPC turned out to be | Units per case |
|---|---|---|
| `BUSCH 24/16 OZ CAN` (also Busch Light, Natural Light, Bud Ice) | a **6-pack** of 16oz cans | 4, not 24 |
| `KING COBRA 16 OZ CAN` | a 6-pack of 16oz cans | 4 |
| `BUD LIGHT 7 OZ LNNR` | a 6-pack of 7oz bottles | 4 |
| `COR EXTRA 7 OZ LOOSE PAR` | the whole Coronita **24-pack** | 1 |
| `MICH ULTRA 4/6PK 7OZ LNNR` | the whole **24-pack** of 7oz bottles | 1, not 4 |
| `BUD LIGHT 15/16 ALUM TW` | one **15-pack** of aluminum bottles | 1 |
| `CT BAHAMA MAMA 24/16 CAN` | a **single** Clubtails can | 24 |
| `KEYSTONE LIGHT 15PK CN` (Faust) | a 15-pack of cans; the case is **two** of them | 2 |
| `COORS LIGHT 15PK ALUM` (Faust) | a 15-pack of aluminum bottles; the case is **one** | 1 |
| `COORS LIGHT 9PK ALUM` (Faust) | a 9-pack; the case is two | 2 |
| `COORS LIGHT 18PK CN` (Faust) | one 18-pack per case | 1 |
| `DOS XX 16OZ CN`, `SOL CHELADA 4PK 16OZ CN` (Faust) | a **4-pack** of 16oz cans; 24 cans = 6 of them | 6 |
| `NB VOODO IMPER 19.2OZ CN` (Faust) | a single can; New Belgium ships **15** per case | 15 |

Single 22oz, 23.5oz, 24oz, 32oz and 40oz cans and bottles were 12 per case on Faust; Twisted Tea,
Smirnoff Smash and Mike's Harder were confirmed as `12/24`. New Belgium's 19.2oz is the exception,
and it was the one line where 12 looked right and was wrong.

The Michelob Ultra line was **not** looked up, was guessed from its text, and was the one line
wrong in the import. **Look every line up.** Where that is impractical, look up every line whose
text could mean singles or a pack, plus at least one line per brand family and pack pattern. Say in
the notes which lines were looked up.

How to look one up:

- **Web search the quoted UPC** (`"018200284373"`). Retailer listings — Kroger, Dollar General,
  Brookshire's, H-E-B, Walmart — say "6 cans / 16 fl oz", "24 bottle / 7 fl oz", "Single Can, 25
  fl oz". This is the reliable source.
- **upcitemdb** (`https://api.upcitemdb.com/prod/trial/lookup?upc=…`) is free but allows 100 a day
  and roughly one a minute. Some answers are junk: Bud Light's quart came back as a teen Bible
  book. Treat it as a hint and confirm with a search.
- Distributor pack text, for the lines it does describe: `2/12` = two 12-packs, `4/6` = four
  6-packs, `6/4` = six 4-packs, `3/8` = three 8-packs, `2/15` = two 15-packs, `18 PK` / `18/12` /
  `SUITCASE` / `30PK` = one pack per case, `12/24OZ` `12/32` `QUARTS` `40 OZ` `15/25` `12/19.2`
  `12/500ML` = that many singles.

Sanity check after: cost per unit should look like a wholesale price for that unit, e.g. a 12-pack
around $13–18 and a single 24oz can around $2.75.

### When the invoice prints the pack UPC and PPC (Silver Eagle's emailed `invoice-report.pdf`)

Silver Eagle 428556 (2026-10-05, 41 lines, 45 cases, $1,338.11) came as a real PDF with a text
layer, not a photo. `pdftotext -layout` (in Git Bash's `/mingw64/bin`) reads it exactly. Each line
prints a **CASE UPC**, a **PACK UPC** and **PPC** (packs per case). The pack UPC is the sellable
item, so use it with PPC as the units. No lookups were needed.

- The pack UPC is a GTIN-14 split over two rows: the first digits sit on the item's row, and the
  rest sit two rows down under it. Join them, check there are 14 digits, and drop the leading `00`
  to get the 12-digit UPC-A.
- Every PPC agreed with what the 424753 import proved: Clubtails 24/16 = 24, BeatBox 12/500 = 12,
  15/25 cans = 15, 30-pack = 1, 4/6 = 4, 2/12 = 2.
- Five lines printed the same code as both case and pack: Cayman Jack Margarita 24oz, Cayman
  Jacked Tropical, 4 Loko Camo, Lipton Hard Lemon and Lipton Hard Citrus. Their units are certain,
  but a web search couldn't confirm their barcodes, so check that those lines matched an existing
  item. In the import (2026-10-08) four of them matched. Cayman Jacked Tropical (`635985000464`)
  came in as a new item with $0 retail. Every other line matched, and every quantity and case cost
  was right.
- Item `00997` with 0 quantity is a printed note ("Monday out of sequence new store delivery"),
  not a product. There is no printed case count, so `--cases` is the sum of the lines.
- Table: `tools/ModisoftFile/examples/SILVER_EAGLE_428556.tsv`.

### When the invoice prints no barcodes (Faust Distributing)

Faust 204409 (2026-10-05, 141 lines, 168 cases, $5,079.34) prints only Faust's own item numbers.
The barcodes came from **the store's Modisoft**:

1. The user creates the purchase and scans each item in invoice order.
2. They export it with Purchase Details, which saves `PurchaseDetails.xlsx` with columns
   QtyPurchase, ItemCode, UPC, ItemDescription, ..., UnitsPerCase, ..., UnitRetail.
3. Pair the rows with the invoice lines **in order**. Check that the counts are equal and that
   every UPC passes its check digit.
4. Compare names. Where the store's name disagrees with the invoice line, look the UPC up. On
   Faust, 082000766001 "Smirnoff 6Pk Peach" sat on the line "SMIR PINK LEMN 6PK BT". It cost the
   same, so only the stock went to the wrong flavour.

The export's **UnitsPerCase is just Modisoft's default 1**; ignore it. Its **UnitRetail is the
store's shelf price**, and it is the best units check there is:
`(retail − case cost ÷ units) ÷ retail` should land near the store's usual margin (Faust: 21–55%).
A negative or 80% margin means the units are wrong. That is how Keystone Light 15pk cans came out
as 2 per case.

That purchase already holds the scanned lines and usually a Manual Entry. The user deletes both,
or imports into a fresh purchase, or the invoice counts twice. Remind them to type the **Invoice
No**, which stayed blank on Faust. An item with $0.00 retail needs a shelf price. Take it from a
sibling item at the same cost, or cost ÷ (1 − the usual margin) rounded to .99.

## 3. Fill the table and let the tool write the file

One tab-separated row per line, with a header:

```
upc	description	cases	units_per_case	case_cost	line_amount	note
080660957579	MOD ESP 2/12 LNNR	7	2	35.60	249.20	item 53032; 12-pack; looked up
```

- `case_cost` is the price **after** the discount (36.50 − 0.90 = 35.60).
- `cases` can be negative for a credit or return line (Dr Pepper's −1 lines imported correctly).
- `line_amount` is the printed amount; the tool refuses the file if `cases × case_cost` differs.
- Lines with 0 cases may stay in the table; they are left out.

Then:

```
dotnet run --project converter/tools/ModisoftFile -c Release -- <table.tsv>
    --invoice 424753 --date 2026-10-06 --total 9357.09 --cases 327
    --out "C:\Users\Kashf\Downloads\MODISOFT_<VENDOR>_<INVOICE>.txt"
```

(`DOTNET_ROOT=%LOCALAPPDATA%\dotnet-sdk8` on this PC.) It checks every line, the printed total and
the printed case count, warns on a UPC that fails its check digit or appears twice, and writes the
file with the converter's own `ModisoftEdiBuilder` only when everything adds up. Retail is written
as $0 (the paper has no shelf price) and the item number is left blank.

### Barcodes with 13 digits (EAN-13)

**Since 2026-10-09 every file is one file, imported with Xpress Wholesale.** The tool and the
converter write a 13-character barcode field: a UPC-A as its 12 digits and a blank, and an EAN-13
whole. Xpress matches exactly, so EAN-13s that share 11 digits stay apart. The only thing left to
check is a store item **saved with a shortened code**. Xpress won't find it, and the import creates
a duplicate. **Fix that in the file, not in Modisoft:** write the store's own saved code for that
line (12 digits + a blank matches exactly, as every UPC-A does), and keep the full 13 only for
products the store doesn't have. Yumi's four existing LLB items went in as their saved codes
(`examples/YUMI_29008246_STORE_CODES.tsv`), and Grape and Lychee went in with 13 digits as new items.
The saved codes come from an earlier import's pasted rows or the user's item list.

What follows is how this was found. The two-file procedure in it is **superseded**.

Imported goods (Asian ice cream, Mexican snacks, vapes) carry 13-digit EAN-13 barcodes. The file
has room for 12, and **under Texas Wholesale Modisoft goes further and matches only the first 11
digits**. Two products whose barcodes share their first 11 digits land on **the same item**.

Yumi Ice Cream 29008246 (2026-10-09) showed it. Its six LLB bars went in as their first 12 digits.
Mango and Peach matched their own items. The other four came in as two pairs:

| Line on the paper | Printed barcode | First 11 | Landed on |
|---|---|---|---|
| LLB Grape | 6911308117885 | 69113081178 | Asian **Strawberry** (691130811781) |
| LLB Strawberry | 6911308117861 | 69113081178 | Asian Strawberry |
| LLB Lychee | 6911308119452 | 69113081194 | Asian **Melon** (691130811941) |
| LLB Melon | 6911308119469 | 69113081194 | Asian Melon |

The file had `691130811788` for Grape and `691130811786` for Strawberry, which are different, and
both still matched Strawberry. The money was right because all four cost the same. The stock was
wrong: 48 Strawberry, 48 Melon, no Grape, no Lychee. The store's LLB items were also saved with
12-digit codes (first 11 + a computed check digit, e.g. Mango `694309042047` for `6943090420430`),
so Grape and Lychee had no item of their own to land on.

**What to do with every invoice that has EAN-13s:**

1. **Group the EAN-13 lines by their first 11 digits.** If no two lines share them, the normal
   Texas Wholesale file is fine (Yumi's Mango and Peach matched). Still tell the user these lines
   are EAN-13s and to check they matched the right item.
2. **If two or more lines share their first 11 digits, take those lines out of the Texas Wholesale
   file** except one of each pair, and put them in a **second file with a 13-character barcode
   field**, imported with **Xpress Wholesale** (below). Then `--total` and `--cases` for the main
   file are the paper's less those lines. Tell the user there are two files and which option each
   one takes.
3. A Chubiwinos-style pair also shares its first 11 digits (CRUJITOS `7501011131040` / DORITOS
   NACHOS `7501011131064`), so the same check applies to the converter's own files. The converter
   does not do this yet: its `MODISOFT_SAME_CODE` warning only compares 12 characters.

**Don't send 13 digits under Texas Wholesale.** Tried 2026-10-09: the extra digit pushes every
field one place right, and 1 case of 24 @ $96.72 came in as **Qty 40000, Units 2, Case Cost
$96.70**, still on Strawberry and Melon. Never save that; cancel or delete the purchase.

**Xpress Wholesale reads a 13-character barcode field.** Same day, the same two lines under
**Import Invoice → EDI → Xpress Wholesale** came in exactly: scan codes `6911308117885` and
`6911308119452`, Qty 1, Units 24, Case Cost $96.72, descriptions clean, both flagged orange as new
items (Modisoft creates them on Save). The layout is the 77-byte record with the barcode field
widened to 13, so every byte after it sits one place right (78 bytes):

```
GHRA   00290082461009260000019344
B6911308117885LLB-GRAPE SINGLE 3D            009672  0000240000100000024      
B6911308119452LLB-LYCHEE SINGLE 3D           009672  0000240000100000024      
CSERSERVICE CHARGE           
```

`B`, barcode (13), description (24) + a blank, item number (6, blank), case cost (6, cents), two
spaces, pack (6), cases (5), retail (5), pack's last 3 digits, item number (6). The header total
is the sum of these lines in cents. That file is kept as
`tools/ModisoftFile/examples/YUMI_29008246_13DIGIT.txt`. `ModisoftFile` cannot write this width
yet; until it can, build the records exactly like the example, one per line, CRLF between them.

On the Review & Save screen for the Xpress file: tick the new rows, set the **Department** (Yumi's
LLB bars are ICE CREAM NTX), press **Update department**, then **Save**. Never press "Update
Selected Items Retail from EDI". Afterwards the user sets a shelf price on the new items (the LLB
bars sell at $7.99).

Not yet known: what Xpress Wholesale does with an ordinary 12-digit UPC-A in that 13-character
field, and whether a 13-digit code finds an item the store saved with 12 (the LLB Mango item is
`694309042047`). Don't move whole invoices to Xpress Wholesale until a test import has shown both.
Until then Texas Wholesale stays the option for everything else.

**Result, 2026-10-09 (Review & Save screen, not saved):**

- **A failed.** Every line came in as a new item under its 13-digit `0…` code.
- **B worked for every UPC-A.** All 49 matched the store's own items, with the right department,
  retail, Qty and case cost.
- **In both, all six LLB bars came in as new items**, Mango and Peach included, which Texas
  Wholesale had matched. Xpress matches a 13-digit code exactly. The store saved those items with
  a shortened 12-digit code (`694309042047`), so they are not found.

So: Xpress Wholesale + layout B works once the store's EAN-13 items carry their real 13 digits.
For Yumi that is a one-time scan-code fix on four items: Mango → `6943090420430`, Peach →
`6943090420065`, Asian Strawberry → `6911308117861`, Asian Melon → `6911308119469`. Grape and Lychee
get created by the import.

Still to see before the converter switches: a file with **credits**. The Dr Pepper file that
imported exactly under Texas Wholesale, in layout B, is
`examples/MODISOFT_DRPEPPER_423423_XPRESS_TEST.txt`: 50 lines, 3 credits of −1 @ $6.37, $1,468.73.

**The test (made 2026-10-09):** The whole Yumi invoice in the
Xpress layout, two ways, built from the Texas Wholesale file that imported with every line matched,
so only the barcode field differs:

- `MODISOFT_YUMI_29008246_XPRESS_A.txt`: a 12-digit UPC-A goes in with a leading `0`
  (`0028000065812`), the EAN-13 form.
- `MODISOFT_YUMI_29008246_XPRESS_B.txt`: a 12-digit UPC-A goes in as itself plus a trailing blank
  (`028000065812 `).
- In both, the six LLB bars carry their real 13 digits.

Import A into a throwaway purchase under Xpress Wholesale. If B is needed, use a fresh purchase.
It passes if every line except Grape and Lychee matches the same item as the Texas Wholesale
import, nothing orange beyond those two, and the total is 2,327.08 with Qty 75. If it does,
**Xpress Wholesale with that padding can become the one option for every invoice**, and this
two-file step goes away. Both files are in `tools/ModisoftFile/examples/`.

Also give the user a short sheet of every line with its units and how that was settled, so they
can see the counts. Silver Eagle's was `SILVER_EAGLE_424753_units.csv` in Downloads.

If they want the photo as a **searchable PDF**, there is no OCR engine on this PC and none is
needed. Embed the JPEG as the page and lay the verified reading over each printed row as invisible
text (PDF text render mode 3). Find the rows by a horizontal ink projection down the item-number
column, and separately down the price column, because a photo is skewed. Faust's was
`FAUST_204409.pdf`. Its text is the checked reading, so it has none of a scanner's misreads.

## 4. Import it in Modisoft (the user does this)

1. Create the purchase by hand: date, vendor (payee), invoice number. Modisoft does not read these
   from the file. A photo that cuts off the invoice date means the user types the delivery date.
2. Press **Import Invoice → File Type EDI → "Xpress Wholesale"** and upload the file. It is always
   Xpress Wholesale, for every vendor. Modisoft parses a file by whichever layout is picked in
   that dropdown, and the file is written for that one. Before 2026-10-09 it was Texas Wholesale
   with a 12-character field. An old 77-byte file still needs Texas Wholesale, and a new one under
   Texas Wholesale comes in shifted (Qty 40000).
3. If the purchase already has a **Manual Entry** for the same invoice (a lump sum typed earlier),
   delete it, or the purchase counts the invoice twice.
4. **Never press "Update Selected Items Retail from EDI."** The file's retail is $0.

## 5. Check what Modisoft shows

Ask the user to paste the purchase's rows back, then check:

- **Qty total = printed cases, Cost total = printed total.**
- **Margins.** A line whose margin is far from its neighbours — 86%, or negative — has a
  units-per-case that disagrees with the store's own item. That is how the Michelob Ultra 7oz line
  showed up: Units 4 against an item the store sells as a 24-pack at $31.99. Look that UPC up and
  have the user correct **Units** on the line.
- **New items.** A line with **$0.00 retail** and a name starting with a stray digit (`5ST ARN
  PILSNER 4/6 CANS`) is an item Modisoft created because the store did not have it yet. The user
  fixes the name and sets a price. The digit is the barcode's last digit; see Part 3 of
  `FORMAT_SPEC.md`.
- **Item Code** should be blank. The single letters (`1`, `/`, `C`) seen on Silver Eagle came from an
  older writer and are fixed (2026-10-06).

## The file, for reference

Header `GHRA` + invoice (10 digits) + `MMDDYY` + total in cents; then one 77-byte record per line:
`B`, the barcode (12 characters), the description (24 + a blank), a blank item number, case cost,
pack (units per case), cases, retail, and the charge record `CSERSERVICE CHARGE` at the end. The
byte positions and how they were found are in [FORMAT_SPEC.md](./FORMAT_SPEC.md) Part 3. Don't
hand-write records; the tool and the converter share one writer so the layout can't drift.
