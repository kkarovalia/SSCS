# EDI / EDI13 → SSCS Format Specification (verified from fixtures)

All facts below were derived from raw-byte comparison of every supplied input/output pair
(16 pairs total) and proven by a prototype that reproduces every expected SSCS file
**byte-for-byte** (see `prototype.ps1`, PASS=16 FAIL=0).

All positions are 0-based byte offsets within a record (line without its EOL bytes).
All files are ASCII in the fixtures; the converter operates on raw bytes and preserves
any non-ASCII byte that is not at a verified transformation position.

## File-level structure

| Property        | Standard EDI (GHRA/PEPSI) | EDI13 (GHRA13)     | EDI13 Ruby Red variant |
|-----------------|---------------------------|--------------------|------------------------|
| Header length   | 33 (GHRA), 27 (PEPSI)     | 33                 | 64                     |
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

Output layout: `A` + 6-char vendor field + input bytes `[7..]` unchanged.
Total length is preserved.

- `GHRA   0000131454…` → `AGHRA  0000131454…`
- `PEPSI  0080471906…` → `APEPSI 0080471906…`
- A `13` suffix on the vendor token is removed **regardless of detected record width**:
  `GHRA13 0000131454…` → `AGHRA  0000131454…`. Hidden fixture 810738 proves this also
  applies when a `GHRA13`-headed file carries standard 76-byte records (the file
  converts as standard EDI, but the header still becomes `AGHRA  `).

A vendor token longer than 6 characters cannot be represented in the output field and is
rejected (never truncated silently).

### Ruby Red header (EDI13 variant)

64 bytes: `GHRA13`(0..5) + company name(6..35, 30 chars) + reference(36..45, 10 chars,
e.g. `IN-3738   `) + date(46..53, 8 digits) + amount(54..63, 10 digits).

Output: `A` + input bytes `[6..63]` unchanged (59 bytes). The record shrinks by 5 bytes.

Detection is structural, never by filename: header length 64, starts `GHRA13`,
byte 6 is not a space, date/amount fields numeric, and all B records are 79 bytes wide
with the fixed zero suffix described below.

## B (product) records

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
