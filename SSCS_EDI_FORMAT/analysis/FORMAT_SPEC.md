# EDI / EDI13 → SSCS Format Specification (verified from fixtures)

All facts below were derived from raw-byte comparison of every supplied input/output pair
(16 pairs total) and proven by a prototype that reproduces every expected SSCS file
**byte-for-byte** (see `prototype.ps1`, PASS=16 FAIL=0).

All positions are 0-based byte offsets within a record (line without its EOL bytes).
All files are ASCII in the fixtures; the converter operates on raw bytes and preserves
any non-ASCII byte that is not at a verified transformation position.

## File-level structure

| Property        | Standard EDI (GHRA/PEPSI/FRITOLAY) | EDI13 (GHRA13)     | EDI13 Ruby Red variant |
|-----------------|------------------------------------|--------------------|------------------------|
| Header length   | 33 (GHRA/FRITOLAY), 27 (PEPSI)     | 33                 | 64                     |
| B record length | 76                        | 78                 | 79                     |
| C record length | 29 (preserved verbatim)   | 29                 | (none in fixture)      |
| Line endings    | CRLF                      | CRLF               | LF                     |
| Final newline   | absent                    | absent             | present                |

Output line endings and final-newline behavior always match the input
(verified: CRLF/no-final-EOL inputs produce CRLF/no-final-EOL outputs; the LF/final-LF
Ruby Red input produces an LF/final-LF output).

Record order: line 1 is always the header; every following line is a `B` (product) or
`C` (charge) record. Any other record type is unsupported.

## Header records

### Vendor-style header (standard EDI and GHRA13 EDI13)

Input layout: `[0..6]` = 7-char vendor field (vendor token left-aligned, space padded),
`[7..]` = numeric payload (digits and `-`; e.g. GHRA: invoice 10 + date 6 + amount 10;
PEPSI: invoice 10 + amount 10). Fixture `EDI_GHRA_118955` proves amounts may contain `-`.

Output layout: `A` + 6-char vendor field + input bytes `[7..]` (with the date rule below).
Total length is preserved.

- `GHRA   0000131454…` → `AGHRA  0000131454…`
- `PEPSI  0080471906…` → `APEPSI 0080471906…`
- `FRITOLA0048690739…` → `AFRITOL0048690739…`
- A `13` suffix on the vendor token is removed **regardless of detected record width**:
  `GHRA13 0000131454…` → `AGHRA  0000131454…`. Hidden fixture 810738 proves this also
  applies when a `GHRA13`-headed file carries standard 76-byte records (the file
  converts as standard EDI, but the header still becomes `AGHRA  `).

**Vendor token longer than 6 characters → truncated to 6** (a character is dropped, and a
`VENDOR_TRUNCATED` warning is recorded). Fixture `EDI_FRITOLAY-48690739` proves this: the
7-char EDI vendor field `FRITOLA` becomes the 6-char SSCS field `FRITOL` (output `AFRITOL…`).
The EDI vendor field is fixed at 7 bytes, so 7→6 is the only truncation that can occur.
(This supersedes the earlier GHRA/PEPSI-only assumption that over-long vendors were rejected;
those fixtures simply never had a vendor wider than 6.)

### Header date reformat (`YYMMDD` → `MMDDYY`)

When the numeric payload is the 26-char `invoice(10) + date(6) + amount(10)` layout, the
6-digit date field (payload bytes `[10..16)`) is rewritten from `YYMMDD` to `MMDDYY` **only
when its first two digits are greater than 12** — a value that cannot be a month, so the field
is unambiguously `YYMMDD`. A `HEADER_DATE_REFORMATTED` warning is recorded.

- Fixture `EDI_FRITOLAY-48690739`: `…0048690739` **`240206`** `0000017899` → `…0048690739` **`020624`** `0000017899` (Feb 6 2024).
- GHRA/PEPSI are untouched: every GHRA date is already `MMDDYY` (month `01`–`12`, so the
  first two digits are `≤ 12`), and PEPSI's payload is 20 chars (no date field). Verified
  against all 16 original + 19 hidden fixture pairs (still byte-for-byte identical).

This rule is derived from a single reference pair. A date whose first two digits are `≤ 12`
is genuinely ambiguous (e.g. `110206` could be Nov 2 2006 **or** Feb 6 2011) and is therefore
left **unchanged**; distinguishing those requires a per-vendor convention we do not yet have a
fixture for.

### Ruby Red header (EDI13 variant)

64 bytes: `GHRA13`(0..5) + company name(6..35, 30 chars) + reference(36..45, 10 chars,
e.g. `IN-3738   `) + date(46..53, 8 digits) + amount(54..63, 10 digits).

Output: `A` + input bytes `[6..63]` unchanged (59 bytes). The record shrinks by 5 bytes.

Detection is structural, never by filename: header length 64, starts `GHRA13`,
byte 6 is not a space, date/amount fields numeric, and all B records are 79 bytes wide
with the fixed zero suffix described below.

## B (product) records

### Field meanings (observed from the SSCS Line Items UI)

The converter treats the record body as **opaque** — it copies bytes verbatim and only ever
transforms the 11-char code field (UPC-E) and, for EDI13/Ruby Red/STRP, the record width. The
meanings below were read back from the live SSCS UI for `Invoice_RT006` and are recorded only to
help a human or tool that needs to **build** a record from scratch (e.g. from a PDF invoice).
They are **not** used by conversion, and exact sub-field widths / decimal scaling are approximate:

- `B` — record type
- code field (1..11) — **PLU / UPC / barcode** (the "ID/PLU" SSCS matches products on)
- description text — **product name**
- a numeric **vendor item # / SKU** (also echoed near the end of the row)
- **extended cost** = qty × unit cost (e.g. `001950` → $19.50)
- **quantity** (e.g. `000005` → 5)
- **each list / retail price** (e.g. → $6.99)
- trailing space **pad** in the final column (col 75)

SSCS **derives** each-cost (extended cost ÷ qty) and margin % — they are not stored in the file.
So the minimum a source document must provide per line is: UPC, description, quantity, a cost
(unit or extended), and the retail/list price; plus header invoice #, vendor, date, and total.

### Standard EDI (76 bytes)

`B`(0) + product-code field(1..11, 11 chars) + remainder(12..75) preserved verbatim.
The only transformation ever applied is UPC-E expansion inside the 11-char code field.

### EDI13 (78 bytes)

`B`(0) + product-code field(1..13, 13 chars) + remainder(14..77).
Observed field contents: 12-digit code+1 space, 11 digits+2 spaces, 13-digit EAN,
10 digits+3 spaces, 8-digit UPC-E+5 spaces — and, in the hidden batch fixtures,
**alphanumeric codes** such as `002ZH7S8J999 ` and `874507008063d`. In every fixture
pair the SSCS code field equals the **first 11 bytes** of the 13-char field (dropping
the 2 EDI13-only trailing bytes), with no digits-only restriction; UPC-E expansion then
applies only if the resulting 11 bytes match the UPC-E pattern (8 digits + 3 spaces).
Remainder bytes `[14..77]` are preserved verbatim (output width 76).

### Ruby Red EDI13 (79 bytes)

`B`(0) + product-code field(1..13) + middle(14..68, 55 bytes) + suffix(69..78, exactly
`00000000  ` = 8 zeros + 2 spaces). Output (67 bytes): `B` + first 11 bytes of the code
field (with UPC-E rule) + middle preserved verbatim. The 10-byte suffix is removed only
after verifying it is exactly `00000000  `.

## UPC-E expansion (verified against 40+ fixture records)

A code field is a UPC-E candidate iff the 11-char SSCS field content is exactly
8 digits followed by 3 spaces (`^\d{8}   $`). The 8 digits are: number system `NS`,
body `D1..D6`, check digit `C`.

Expansion to the 11-digit UPC-A body (without UPC-A check digit):

| D6      | UPC-A body                     |
|---------|--------------------------------|
| 0, 1, 2 | `NS D1 D2 D6 0 0 0 0 D3 D4 D5` |
| 3       | `NS D1 D2 D3 0 0 0 0 0 D4 D5`  |
| 4       | `NS D1 D2 D3 D4 0 0 0 0 0 D5`  |
| 5–9     | `NS D1 D2 D3 D4 D5 0 0 0 0 D6` |

Example: `03432004` → `03400000320` (D6=0 rule).

Validation:
- `NS` must be 0 or 1 (UPC-E is undefined for other number systems).
- `C` must equal the UPC-A check digit computed over the 11-digit expanded body
  (odd positions ×3 + even positions, mod 10). All expanded fixture codes validate.

**Fixture-proven exception:** `SSCS_GHRA_129348` line 104 proves the authoritative
converter handles an unsupported number system (`57026404`, NS=5) by **blanking the
11-char code field to spaces** and preserving the rest of the record. The converter
reproduces this and reports the record as a warning. A candidate with a supported
number system but an invalid check digit has no fixture precedent and causes the file
to be rejected with the record number and reason.

Codes that do not match the 8-digit+3-space pattern (e.g. `7777777778 `, 10 digits +
1 space, or an all-space field) are never expanded.

## C records

Preserved byte-for-byte, including all trailing spaces
(`CSERSERVICE CHARGE           `, 29 bytes in every fixture).

## STRP / Alp 75-byte standard EDI (`StandardEdi75`)

Example input: `Invoice_RT006_EDI.txt` (vendor `STRP`). Detected structurally: a vendor-style
header whose payload is **28** bytes = invoice(10) + **8-digit `MMDDYYYY`** date + amount(10),
paired with **75-byte** `B` records. Both differences are resolved by the same normalization
principle the GHRA set proves (EDI13's 78-byte rows normalize to 76; the header is always 33):

- **Header:** shorten the 8-digit `MMDDYYYY` date to 6-digit `MMDDYY` (drop the century), giving
  the canonical 33-byte header `A` + vendor(6) + invoice(10) + `MMDDYY`(6) + amount(10).
  Example: `STRP   0000000006`**`07212026`**`0000013650` → `ASTRP  0000000006`**`072126`**`0000013650`.
  Records a `HEADER_DATE_REFORMATTED` warning.
- **B records:** column analysis of 7,054 real SSCS rows shows the 76-byte layout ends in a
  trailing pad (column 75 is a space in 99.7% of rows). A 75-byte STRP row aligns with that
  schema through column 74, so it is normalized by **appending one trailing space** (→ 76).
  The 11-char code field is handled exactly like standard EDI (UPC-E rule applies).

**Verification:** derived from the GHRA gold-standard normalization principle (this format had no
byte-reference of its own), then **confirmed end-to-end by SSCS**: the converted `Invoice_RT006`
was ingested by the live SSCS system, which read back invoice #6, date **7/21/2026** (confirming
the `MMDDYYYY`→`MMDDYY` shortening), total **$136.50**, and all seven line items (qty 5, ea list
$6.99, ext cost $19.50, 44.21% margin each; 7 × $19.50 = $136.50). The confirmed output is locked
as a golden-master fixture (`SSCS_Invoice_RT006_EDI.txt`).

## Rejection rules (validated before any output is written)

- First line not a valid vendor-style or Ruby Red header → missing/invalid header.
- Record after line 1 not starting with `B` or `C` → unsupported record type
  (reported with line number; covers duplicate headers and blank lines).
- B record width ≠ expected width for the detected format → unsupported record length
  (reported with line number and actual length).
- NUL or other control bytes (other than CR/LF) → binary content rejected.
- Lone CR (not part of CRLF) → rejected.
- Empty file → rejected.
- UTF-8 BOM: stripped, conversion continues, warning recorded; output has no BOM.
