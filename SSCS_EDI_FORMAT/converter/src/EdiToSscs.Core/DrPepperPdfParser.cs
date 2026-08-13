using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// Parses a Dr Pepper Bottling Company of Texas invoice PDF (as reconstructed text lines from
/// <see cref="PdfTextExtractor"/>) into an <see cref="Invoice"/>. Rules are documented in
/// analysis/vendors/DR_PEPPER.md and confirmed end-to-end against live SSCS.
///
/// Layout: each product is two lines — "&lt;description&gt; &lt;UPC(12)&gt;/&lt;SKU&gt;" then
/// "&lt;WHSLE&gt; &lt;CASES&gt; &lt;UNITS&gt; &lt;NET&gt; &lt;TAX&gt; &lt;AMOUNT&gt;". Package-group
/// headers, PEG/PKG/PRO/RAG subtotal lines and non-priced PALLET/SHELL equipment lines are skipped
/// naturally (they match neither pattern).
///
/// Mapping: units = UNITS (never cases); unit cost = AMOUNT ÷ UNITS to 4 decimals; barcode = the
/// printed 12-digit UPC **whole, check digit included** — SSCS drops that digit itself on import.
/// The printed AMOUNT DUE must equal the sum of the line amounts or the file is refused.
///
/// Handwriting: these invoices come back marked up, and OCR turns a pen stroke into a stray token
/// wherever it crossed the page. A mark on white space or on the unused NET/TAX columns is dropped
/// silently; a mark on a barcode, quantity or price is refused with the line quoted, because those
/// decide what is booked into stock and at what price. See ReadAmounts and the unclaimed-row scan.
/// </summary>
public static partial class DrPepperPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. SSCS resolves it to its own Vendor ID through the
    /// "Map Supplier to Vendor" screen; an unrecognised name still imports and the operator can
    /// pick the vendor from the dropdown.
    /// </summary>
    public const string SupplierName = "DR PEPPER";

    // Product line 1: "<description> <UPC>/<SKU>". The code token is anchored at the end of the line.
    //
    // Two things the scans do to it, both of which the pattern absorbs and StripCodeNoise undoes:
    //  - it inserts spaces and punctuation inside the digits ("07 181700 1320/10090 16 1",
    //    "071817033291/100,00862", "071817`000453/10000069");
    //  - it loses the space between description and barcode entirely, printing them welded
    //    ("0.5L12 VC PureCocoDb898999010007/20000642").
    //
    // Hence the separator is "whitespace, or none at all provided the character before the code is a
    // letter". A letter is required because it is the one boundary the description cannot fake: were
    // a bare digit allowed to end the description, the code could start part-way through the barcode
    // and produce a shorter, wrong UPC that still passed every length check below.
    [GeneratedRegex(@"^(?<desc>.*?\S)(?:\s+|(?<=\p{L}))(?<code>\d[\d ,'`]*/[\d ,'`]*\d)\s*$")]
    private static partial Regex ProductLine();

    /// <summary>
    /// The same line, for the two kinds of damage the pattern above deliberately cannot admit —
    /// both of them from invoice 5218300220, which is why neither of its two scans would convert:
    ///
    ///  * a **period inside the barcode**: "5GBIB DrPep 05490.0710545/10000703";
    ///  * a **letter inside the SKU**: "16z12 SN Frt Pnch 076183003138/10136S14".
    ///
    /// Both are safe to read and neither can be let into the general class. A period is also the
    /// decimal separator, so admitting it there would let a priced row pose as a product —
    /// "38.00 1 12 25.07 0.00 / 25.07" would split into a nine-digit "barcode" and book a line that
    /// does not exist. A letter is worse, because the class allows spaces and a description is
    /// mostly letters.
    ///
    /// What makes this pattern safe instead is the barcode half: **one unbroken run of eleven to
    /// sixteen digits and dots**, a length no money column can reach, since each is at most six
    /// characters and they are separated by spaces. Only the SKU side is loosened, and the SKU is
    /// never written to the XML — while the barcode side still admits no letter, so the 7 that
    /// scanned as an "i" in "0i8000200.287" is refused exactly as before. A repaired barcode is
    /// held to its check digit either way.
    /// </summary>
    [GeneratedRegex(@"^(?<desc>.*?\S)(?:\s+|(?<=\p{L}))(?<code>\d[\d.]{10,15}/\S*\d)\s*$")]
    private static partial Regex DamagedProductLine();

    /// <summary>
    /// Punctuation the scanner sprinkles through the code token. Unlike a space — which only ever
    /// splits digits apart and cannot hide one — a mark like these sits *on* the barcode, and may
    /// have taken a digit with it. Codes that needed one removed are held to their check digit.
    /// </summary>
    private static readonly char[] CodeInk = [',', '\'', '`', '.'];

    private static string StripCodeNoise(string code)
    {
        code = code.Replace(" ", "");
        foreach (char c in CodeInk) code = code.Replace(c.ToString(), "");
        return code;
    }

    /// <summary>
    /// Gives back the last letter of a description that the scan turned into a digit and welded to
    /// the front of the barcode. "16z12C4PrfSourGumSpl 842595136050" came through as
    /// "16z12C4PrfSourGumSp1842595136050", and thirteen digits is a length the code checks accept, so
    /// the line would have shipped a barcode that is not the item.
    ///
    /// The check digit is what makes this arithmetic rather than a guess, and both halves of the test
    /// are needed: the thirteen digits must **fail** as a GTIN-13 and the last twelve must **pass**
    /// as a UPC-A. A genuine 13-digit code passes the first test and is left alone — and one that
    /// happens to be a UPC-A behind a leading zero is the same twelve digits either way, which is
    /// what SSCS wants regardless.
    /// </summary>
    private static string TrimDescriptionDigit(string upc) =>
        upc.Length == 13 && !Upc.IsValidGtin(upc) && Upc.IsValidUpcA12(upc[1..]) ? upc[1..] : upc;

    /// <summary>The barcode half of a raw code token, as the scanner printed it.</summary>
    private static string BarcodeHalf(string rawCode)
    {
        int slash = rawCode.IndexOf('/');
        return (slash >= 0 ? rawCode[..slash] : rawCode).Trim();
    }

    // Product line 2 is read column by column rather than with one regex — see ReadAmounts.
    // A money figure: the separator tolerates common OCR slips. A decimal point comes back as ':',
    // ',' or ';' — invoice 4646105209 prints $13.80 and scans it "13;80" — and every one of those
    // is re-added by the package subtotal above it and by AMOUNT DUE, so reading one wrongly fails
    // loudly rather than shipping.
    [GeneratedRegex(@"^\d+[.,:;]\d{2}$")]
    private static partial Regex MoneyToken();

    // A plain count (CASES, UNITS).
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex CountToken();

    /// <summary>
    /// A **money** figure with a mark stuck to one end — "20.66-", "|36.00", "$910.98". A stroke that
    /// lands beside a column is already dropped as ink; one that touches a digit used to cost the
    /// whole column, and with it either a prompt or the entire product line.
    ///
    /// Three things keep this from becoming a guess.
    ///
    ///  * **Money only.** A dollar figure is re-checked twice over — by the package subtotal above it
    ///    and by AMOUNT DUE — so a mark taken off wrongly fails loudly instead of shipping. A count
    ///    has no such backing; it is what those totals are made of. So a marked count stays damage,
    ///    and is settled from a subtotal or asked about, exactly as before.
    ///  * **No letters.** A letter inside a number is a mis-read digit, not a mark: "1Z" is 12 with
    ///    the 2 lost, "5b.14" is 50.14. Rubbing it out would delete the digit and leave a figure that
    ///    looks perfectly clean. Only marks come off — a stroke, a bar, a stray quote.
    ///  * **No parentheses.** Dr Pepper prints a negative in brackets, "(1.72)" — see
    ///    <see cref="ReadCredit"/> and the summary's own "Cases 0 (5.13)" — and never with a sign
    ///    character, so a '-' welded to a sales figure came off the paper and not out of the vendor's
    ///    system. The bracket is left alone so a credit still reads as a credit.
    /// </summary>
    [GeneratedRegex(@"^[^\w(]*(?<money>\d+[.,:;]\d{2})[^\w.,:;)]*$")]
    private static partial Regex MoneyWithInk();

    /// <summary>
    /// Splits a row into the columns that survived: a whole token carrying no digit at all is ink and
    /// goes (a pen stroke crossing the page reads as a stray "/" or "|"), and a mark welded onto a
    /// money figure is taken off it.
    /// </summary>
    private static string[] Columns(string line) => line
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Where(t => !IsInk(t))
        .Select(t => MoneyWithInk().Match(t) is { Success: true } m ? m.Groups["money"].Value : t)
        .ToArray();

    // "DATE: 08/10/26". The scanner drops punctuation next to the label as readily as anywhere else
    // — the 08/10/26 ticket reads "DATE:. 08/10/26" on one scan and "DATE: 08/10/26" on the other —
    // so any run of punctuation and space is allowed between the word and the digits. Nothing but
    // punctuation, though: a stray word there would mean this is not the date field at all.
    [GeneratedRegex(@"\bDATE[;:.,\-_'`\s]*(\d{2})/(\d{2})/(\d{2})")]
    private static partial Regex DateLine();

    /// <summary>
    /// A group subtotal or the invoice's own TOT SALE line: a label carrying no digits, then cases,
    /// units and an amount. The label is OCR-variable (PEG / PKG / PRO / RAG / PRG, and "PEG-" with
    /// a stroke through it), so it is identified by shape rather than by name — see ReadTotals.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<label>[^\d\s]\S*)")]
    private static partial Regex TotalsLabel();

    [GeneratedRegex(@"\bAMOUNT\s+DUE\s*\$?\s*(?<amt>[\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AmountDueLine();

    /// <summary>How a line read as the WHSLE/CASES/UNITS/NET/TAX/AMOUNT row of a product.</summary>
    private enum RowKind
    {
        /// <summary>Not a priced row at all — a package header, a subtotal, an address.</summary>
        NotAmounts,
        /// <summary>
        /// Meant to be a priced row, but the wrong number of columns survived, so which figure is
        /// which can no longer be told. A stroke that split UNITS into "1" and "2/" leaves seven
        /// columns; a column lost outright leaves five. Neither can be recovered or even asked
        /// about, because nothing on the row says what the operator should be looking at.
        /// </summary>
        Unreadable,
        /// <summary>
        /// Six columns, so every figure is where it belongs — though any one of them may still be
        /// damaged, which the nulls on <see cref="AmountsRow"/> carry.
        /// </summary>
        Ok,
    }

    /// <summary>
    /// The four columns of a priced row that matter, once ink has been discarded. Each is null when
    /// the scan destroyed that column, and the matching Printed… string then holds what it made of
    /// it, so the operator can be shown what is on the page.
    ///
    /// Reading column by column rather than all-or-nothing is what lets each one be recovered on its
    /// own terms: a damaged AMOUNT can be taken from the invoice total, a damaged UNITS from the
    /// package subtotal, and a damaged WHSLE is not worth anyone's time because it only ever seeds
    /// the shelf price of an item SSCS has never seen.
    /// </summary>
    /// <param name="Net">
    /// The per-case price, which nothing is ever built from and which is exactly why it is kept:
    /// <c>CASES x NET = AMOUNT</c> is the row stating its own money a second time, in different
    /// glyphs, so a row where the two agree has checked itself. It is the most OCR-prone column on
    /// the page ("32.S0"), so a NET that did not read says nothing at all — never that the row is
    /// wrong. See <see cref="SettleTotal"/>, which is the only thing that reads it.
    /// </param>
    private readonly record struct AmountsRow(
        decimal? Whsle, int? Cases, int? Units, decimal? Amount,
        string PrintedWhsle, string PrintedCases, string PrintedUnits, string PrintedAmount,
        decimal? Net = null);

    /// <summary>
    /// A token carrying no digit at all is ink, not data. Invoices come back from the store marked
    /// up by hand, and OCR transcribes a pen stroke as a stray "/" or "|" wherever it crossed the
    /// page — including on two consecutive lines, when the stroke was long enough to cross both.
    /// </summary>
    private static bool IsInk(string token) => !token.Any(char.IsAsciiDigit);

    /// <summary>
    /// How a message should describe what the scan made of one column. A column can come through
    /// damaged ("1Z") or not come through at all, and "the scan read UNITS as ''" tells the operator
    /// nothing they can act on.
    /// </summary>
    private static string Scan(string column, string printed) =>
        printed.Length == 0
            ? $"{column} did not read"
            : $"{column} reads \"{printed}\"";

    /// <summary>
    /// Reads a priced row by column instead of by one fixed pattern, so a mark that landed in white
    /// space or on the unused NET/TAX columns is simply dropped. A mark that touches WHSLE, CASES,
    /// UNITS or AMOUNT leaves that column null for the caller to recover, ask about, or refuse over
    /// — each of the four is worth a different amount of trouble.
    ///
    /// A row that lost a column outright is read anyway, as long as its NET/TAX/AMOUNT tail survived
    /// — see below for why that tail is trustworthy. What still cannot be read is a row with *more*
    /// columns than the layout has, because a stroke has split one in two and there is no telling
    /// which: "1" and "2/" where the paper says 12. Those come back <see cref="RowKind.Unreadable"/>.
    /// </summary>
    private static RowKind ReadAmounts(string line, out AmountsRow row)
    {
        row = default;

        string[] cols = Columns(line);

        // WHSLE CASES UNITS NET TAX AMOUNT. TAX is unused; NET is nothing is built from either, but
        // it is carried, because CASES x NET is the row's own second statement of its AMOUNT and it
        // is the only thing on the page that can say *which* row is wrong when the invoice does not
        // add up. Both are the most OCR-prone money columns there are ("32.S0", "0.00'"), so a NET
        // that did not read is simply silent.
        if (cols.Length == 6)
        {
            row = Row(cols[0], cols[1], cols[2], cols[5], cols[3]);
            return RowKind.Ok;
        }

        // Fewer columns than the layout has: the scan lost one outright, as on invoice 4643519283,
        // where "35.90 1 12 20.66 0.00 20.66" came through as "20.66 0.00 20.66".
        //
        // The row is still readable, because NET, TAX and AMOUNT are the last three columns and all
        // three are money — a shape nothing else on the page ends in. A PEG subtotal ends in one
        // money column behind two counts; a package header carries no money at all. So the tail
        // fixes which column is the AMOUNT, whatever went missing in front of it.
        //
        // Reading it matters more than it looks: an unrecognised priced row takes its whole product
        // line with it, and every remaining line still reconciles against every other, so only
        // AMOUNT DUE ever notices — as a flat refusal that cannot say which line went missing.
        if (cols.Length is 3 or 4 or 5
            && MoneyToken().IsMatch(cols[^1])
            && MoneyToken().IsMatch(cols[^2])
            && MoneyToken().IsMatch(cols[^3]))
        {
            // Whatever is left in front of the tail is read only where its own type says what it is:
            // WHSLE is money, CASES and UNITS are counts. A lone survivor could be any of the three
            // with equal claim, so it is left for the package subtotal to settle rather than guessed
            // at by position — and so is anything else still unknown, exactly as damage would be.
            string[] head = cols[..^3];
            string whsle = "", cases = "", units = "";
            if (head.Length == 2 && MoneyToken().IsMatch(head[0])) (whsle, cases) = (head[0], head[1]);
            else if (head.Length == 2) (cases, units) = (head[0], head[1]);
            else if (head.Length == 1 && MoneyToken().IsMatch(head[0])) whsle = head[0];

            row = Row(whsle, cases, units, cols[^1], cols[^3]);
            return RowKind.Ok;
        }

        // The tail itself took the mark. A priced row is then identified by its looser shape —
        // six-ish columns bracketed by money, and one end surviving is enough — so a row damaged
        // this far is still reported as damage rather than quietly demoted to "not a product".
        // Package headers carry two or three columns and PEG/PKG subtotals three, so neither ever
        // gets past here.
        if (cols.Length < 4) return RowKind.NotAmounts;
        if (!MoneyToken().IsMatch(cols[0]) && !MoneyToken().IsMatch(cols[^1])) return RowKind.NotAmounts;
        return RowKind.Unreadable;
    }

    /// <summary>
    /// Builds a row from the text of its four columns, keeping each one only where it reads as its
    /// own type. An empty string means the column never reached the row at all.
    /// </summary>
    private static AmountsRow Row(string whsle, string cases, string units, string amount, string net = "") =>
        new(MoneyToken().IsMatch(whsle) ? Money(whsle) : null,
            CountToken().IsMatch(cases) ? int.Parse(cases, CultureInfo.InvariantCulture) : null,
            CountToken().IsMatch(units) ? int.Parse(units, CultureInfo.InvariantCulture) : null,
            MoneyToken().IsMatch(amount) ? Money(amount) : null,
            whsle, cases, units, amount,
            MoneyToken().IsMatch(net) ? Money(net) : null);

    /// <summary>
    /// Reads a group subtotal — "PEG 4 8 51.00" — or the invoice's own "TOT SALE 44 585 1080.22".
    /// Both are a label with no digits in it, then cases, units and an amount.
    ///
    /// These are the only independent statement the invoice makes about its quantities, and the
    /// money cannot stand in for them: a UNITS column misread as 11 where the paper says 12 leaves
    /// every dollar figure on the page correct and still books a unit short into SSCS.
    ///
    /// The amount is accepted loosely (one scan prints the invoice's own as "108022") because the
    /// AMOUNT DUE gate already covers the money. Only the two counts are used.
    /// </summary>
    private static bool ReadTotals(string line, out int cases, out int units, out bool isInvoiceTotal)
    {
        cases = units = 0;
        isInvoiceTotal = false;

        // A label, not a figure: this is what keeps a priced row (which starts with WHSLE) out.
        var label = TotalsLabel().Match(line);
        if (!label.Success) return false;

        string[] cols = Columns(line);

        if (cols.Length != 3) return false;
        if (!CountToken().IsMatch(cols[0]) || !CountToken().IsMatch(cols[1])) return false;
        if (!LooksLikeSubtotalAmount(cols[2])) return false;

        cases = int.Parse(cols[0], CultureInfo.InvariantCulture);
        units = int.Parse(cols[1], CultureInfo.InvariantCulture);
        isInvoiceTotal = line.TrimStart().StartsWith("TOT", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// The third column of a subtotal — the package's own dollar figure, which nothing here reads.
    /// The counts beside it are what the group is wanted for, and the dollars are checked against
    /// AMOUNT DUE instead. So it only has to be recognisably *a figure*, however the scan mangled
    /// it: "PEG 21 504 682.S0" and "PEG 11 132 23S.18" both count their package perfectly well.
    ///
    /// Requiring readable money there cost far more than it looked. A subtotal thrown away takes the
    /// group boundary with it, so the rows above merge into the next package, whose printed counts
    /// describe only some of them — and then every row of that package is asked about instead of the
    /// one row that was wrong. Invoice 4642411685 put eleven questions to the operator that way,
    /// for a single UNITS column misread as 14 for 24.
    ///
    /// It still has to be a figure. "16 OZ ALUM CAN LS 12 PAL160" is a package *heading* with two
    /// counts and a word after them, and reading that as a subtotal would put the boundary in the
    /// wrong place — which is why the leading digit is required rather than just "not a count".
    /// </summary>
    private static bool LooksLikeSubtotalAmount(string token) =>
        MoneyToken().IsMatch(token)
        || CountToken().IsMatch(token)
        || (char.IsAsciiDigit(token[0]) && token.Any(c => c is '.' or ',' or ':' or ';'));

    /// <summary>
    /// A line that was meant to be a package subtotal but whose figures did not survive the scan —
    /// "PEG L 15 28.12", where the case count came through as a letter.
    ///
    /// Told apart from the equipment lines that share its shape (<c>SHELLS 7 0.00</c>,
    /// <c>PALLETS 1 0.00</c>) by the length of the label: the subtotal labels are all three
    /// characters, OCR-variable though they are (PEG / PKG / PRO / RAG / PRG, and PEG- with a stroke
    /// through it). Getting this wrong costs nothing worse than a group boundary in the wrong place,
    /// but the equipment lines sit among the credits and would raise a note on every credit ticket.
    /// </summary>
    private static bool LooksLikeDamagedSubtotal(string line)
    {
        var label = TotalsLabel().Match(line);
        if (!label.Success || label.Groups["label"].Value.Length > 4) return false;

        string[] cols = Columns(line);
        return cols.Length is 2 or 3
            && MoneyToken().IsMatch(cols[^1])
            && cols[..^1].Any(c => CountToken().IsMatch(c));
    }

    /// <summary>A row of the SALES SUMMARY block: the word Cases or Units, then the count.</summary>
    [GeneratedRegex(@"^\s*(?<label>Cases|Units)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRow();

    /// <summary>
    /// The invoice's own count of itself, as printed by a delivery that carries returns. Those
    /// tickets have no TOT SALE line at all; they close with a block instead:
    ///
    /// <code>
    /// SALES SUMMARY
    /// Sales    Qty   Amount
    /// Cases     41   916.11
    /// Units    547
    /// Credits
    /// Cases      0   (5.13)
    /// Units      3
    /// </code>
    ///
    /// Reading it is what keeps a credit invoice checkable. The package subtotals still cover every
    /// sales row, but the CREDITS section prints no subtotal of its own — so without this block,
    /// nothing on the page would notice a returned quantity read wrong, and the money would balance
    /// regardless.
    /// </summary>
    private static void ReadSalesSummary(
        IReadOnlyList<string> lines, out int? salesCases, out int? salesUnits, out int? creditUnits)
    {
        salesCases = salesUnits = creditUnits = null;

        int start = -1;
        for (int i = 0; i < lines.Count && start < 0; i++)
            if (lines[i].Trim().Equals("SALES SUMMARY", StringComparison.OrdinalIgnoreCase)) start = i;
        if (start < 0) return;

        bool inCredits = false;
        for (int i = start + 1; i < lines.Count; i++)
        {
            if (AmountDueLine().IsMatch(lines[i])) break;
            if (IsCreditsHeader(lines[i])) { inCredits = true; continue; }

            var m = SummaryRow().Match(lines[i]);
            if (!m.Success) continue;

            // The label is a word, so it drops out as ink and the count leads what is left.
            string[] cols = Columns(lines[i]);
            if (cols.Length == 0 || !CountToken().IsMatch(cols[0])) continue;

            int n = int.Parse(cols[0], CultureInfo.InvariantCulture);
            bool isUnits = m.Groups["label"].Value.Equals("Units", StringComparison.OrdinalIgnoreCase);

            if (inCredits) { if (isUnits) creditUnits = n; }
            else if (isUnits) salesUnits = n;
            else salesCases = n;
        }
    }

    // A credit's AMOUNT, printed in parentheses: "(6.37)". A leading minus is accepted too.
    [GeneratedRegex(@"^(?:\(|-)(?<amt>\d+[.,:;]\d{2})\)?$")]
    private static partial Regex CreditAmountToken();

    /// <summary>
    /// Reads a row of the CREDITS section — goods that went back on the truck. Credits print under
    /// their own, shorter header: <c>CASES UNITS NET TAX AMOUNT</c>, with **no WHSLE column** and
    /// the amount in parentheses. Same tolerance as <see cref="ReadAmounts"/>: ink is discarded,
    /// damage to a column that matters is refused.
    /// </summary>
    private static RowKind ReadCredit(string line, out AmountsRow row)
    {
        row = default;

        string[] cols = Columns(line);

        // Identified by the bracket at the end — the one thing on the page shaped like this. The
        // SALES SUMMARY prints "Cases 0 (19.11)", but that is two columns, not five. The digits
        // inside the bracket may still be damaged; that is a question, not a reason to look away.
        if (cols.Length < 3 || !LooksLikeCreditAmount(cols[^1])) return RowKind.NotAmounts;

        // CASES UNITS NET TAX AMOUNT — a credit carries no WHSLE column of its own.
        if (cols.Length == 5)
        {
            row = CreditRow(cols[0], cols[1], cols[4]);
            return RowKind.Ok;
        }

        // A column lost outright, read from the NET/TAX/AMOUNT tail exactly as a sales row is.
        if (cols.Length is 3 or 4
            && MoneyToken().IsMatch(cols[^2]) && MoneyToken().IsMatch(cols[^3]))
        {
            string[] head = cols[..^3];
            row = CreditRow(
                head.Length == 2 ? head[0] : "",
                head.Length == 2 ? head[1] : "",
                cols[^1]);
            return RowKind.Ok;
        }

        return cols.Length < 4 ? RowKind.NotAmounts : RowKind.Unreadable;
    }

    /// <summary>
    /// A bracket where the AMOUNT belongs. Kept separate from <see cref="CreditAmountToken"/> so a
    /// credit whose figure the scan damaged is still recognised as a credit — it then goes to the
    /// operator like any other damaged amount, instead of taking its product line down with it.
    /// </summary>
    private static bool LooksLikeCreditAmount(string token) =>
        token.StartsWith('(') || token.StartsWith('-');

    /// <summary>
    /// Builds a credit row, keeping whichever columns read as their own type.
    ///
    /// Credits print no subtotal beneath them, so nothing on the page can settle a figure the scan
    /// destroyed here — which used to mean refusing the whole invoice. Now the nulls travel, and the
    /// operator is asked for that one figure with a picture of the printed row, exactly as they are
    /// for a sales row. There is no WHSLE column to lose either way.
    /// </summary>
    private static AmountsRow CreditRow(string cases, string units, string amount)
    {
        var m = CreditAmountToken().Match(amount);
        return new AmountsRow(
            null,
            CountToken().IsMatch(cases) ? int.Parse(cases, CultureInfo.InvariantCulture) : null,
            CountToken().IsMatch(units) ? int.Parse(units, CultureInfo.InvariantCulture) : null,
            m.Success ? -Money(m.Groups["amt"].Value) : null,
            "", cases, units, amount);
    }

    /// <summary>The CREDITS banner that switches the page over to the shorter column layout.</summary>
    private static bool IsCreditsHeader(string line) =>
        line.Trim().Equals("CREDITS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One product row, held back until the package group it belongs to has been read, because the
    /// group's own printed subtotal is what settles a damaged UNITS figure.
    /// </summary>
    private sealed class PendingRow
    {
        public required string Description { get; init; }
        public required bool IsCredit { get; init; }
        /// <summary>1-based line number of the priced row, for a message that quotes the text.</summary>
        public required int LineNo { get; init; }
        /// <summary>1-based position among the invoice's products, which is how a person counts.</summary>
        public required int Position { get; init; }
        /// <summary>What the scan made of each column, for the operator to compare.</summary>
        public required AmountsRow Scanned { get; init; }
        /// <summary>What the scan made of the code token, or empty when it found none at all.</summary>
        public required string PrintedCode { get; init; }

        /// <summary>
        /// Which page of the PDF this row is on, 1-based, for a row that came off the picture and so
        /// has no line number to cut a band around. 0 for a row the text layer read - that one has a
        /// line number, which is better.
        /// </summary>
        public int Page { get; init; }

        /// <summary>Whether that page was scanned the wrong way up, so its picture needs turning.</summary>
        public bool PageUpsideDown { get; init; }


        /// <summary>
        /// What a vision model read on this row, where one was consulted and its reading could be
        /// tied here beyond doubt. Null is the normal case and must leave every rule unchanged.
        /// </summary>
        public VisionRow? Vision { get; set; }

        // Null until the scan, the invoice's own arithmetic, or the operator supplies each one.
        public string? Upc { get; set; }
        public int? Units { get; set; }
        public decimal? Amount { get; set; }
        public int? Cases { get; set; }
        public decimal? Whsle { get; set; }
    }

    /// <summary>A package group and the case and unit counts its own subtotal line prints.</summary>
    private readonly record struct Group(int First, int Count, int Cases, int Units, int LineNo);

    /// <summary>
    /// Turns a reading taken off the picture into rows, for an invoice whose text layer gave up
    /// nothing at all.
    ///
    /// **Line number 0 throughout, deliberately.** These rows have no place on the page as far as the
    /// text layer is concerned, so no question about one can cut its picture out — and a line number
    /// invented here would land on whatever stray text the OCR did manage, putting a real row of a
    /// real invoice on screen with the wrong figure marked on it. A question with no picture is worth
    /// having; a question with the wrong picture is not.
    ///
    /// There are no package groups either, because a subtotal is a printed line and none were read.
    /// The rows are therefore checked by AMOUNT DUE alone, which is the invoice's strongest test and
    /// the one it always ends on.
    ///
    /// **The barcode still has to pass its own check digit.** Coming off the picture buys it nothing
    /// here: the model misreads a digit the same way the scanner does, and on the store's very first
    /// no-text sample it read the printed 810128528191 as 810128529191 — twelve confident digits that
    /// fail their own arithmetic. Nothing downstream can see that. The money reconciles whatever code
    /// goes out, so the invoice reached its printed AMOUNT DUE to the cent with one line booked onto a
    /// product that is not the one delivered. So the same rule the text layer gets applies: a code
    /// that fails is not a code, and the operator is shown the row.
    ///
    /// **And it has to be twelve digits.** The check digit alone is not enough here, because a model
    /// that inserts a digit can land on a code that passes: the printed `014800511758` came back as
    /// `0148000511758`, one extra zero, and that is a perfectly valid GTIN-13. Our own guard waved it
    /// through, and SSCS would have opened a new item under it. Dr Pepper prints UPC-A and nothing
    /// else — 428 of 429 codes across the store's thirty samples were twelve digits and the odd one
    /// out was this fabrication — so a length that is not twelve is damage, whatever it adds up to.
    /// The cost of being wrong about that is one question; the cost of being right is a phantom item.
    /// </summary>
    private static List<PendingRow> RowsFromThePicture(VisionReading seen)
    {
        var built = new List<PendingRow>(seen.Rows.Count);
        int position = 0;
        foreach (var v in seen.Rows)
        {
            if (IsReturnableEquipment(v)) continue;

            string units = v.Units?.ToString(CultureInfo.InvariantCulture) ?? "";
            string amount = v.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
            int? cases = v.Qty is decimal q && q > 0m && decimal.Truncate(q) == q ? (int)q : null;
            string? upc = v.Barcode is { Length: > 0 } code ? TrimDescriptionDigit(code) : null;

            built.Add(new PendingRow
            {
                Description = v.Description,
                IsCredit = v.IsCredit,
                LineNo = 0,
                Page = v.Page,
                PageUpsideDown = v.PageUpsideDown,
                Position = ++position,
                PrintedCode = v.Barcode ?? "",
                Upc = upc is { Length: 12 } && Upc.IsValidGtin(upc) ? upc : null,
                Units = v.Units is int u ? (v.IsCredit ? -Math.Abs(u) : u) : null,
                Amount = v.Amount is decimal a ? (v.IsCredit ? -Math.Abs(a) : a) : null,
                Cases = cases,
                Scanned = new AmountsRow(
                    null, cases, v.Units, v.Amount, "", cases?.ToString(CultureInfo.InvariantCulture) ?? "",
                    units, amount, v.Price),
            });
        }
        return built;
    }

    /// <summary>
    /// The pallets and shells Dr Pepper tracks at the foot of the ticket, below AMOUNT DUE:
    ///
    ///     PALLET PLASTIC 37X37   75001490   1
    ///     SHELL PET 1L15         75000013   1
    ///     SHELL PET 20Z          75000014   5
    ///
    /// Crates and pallets going back and forth on the truck, not goods. They are worth nothing —
    /// the SALES SUMMARY prints them at 0.00 — and the store does not stock them, so one reaching
    /// SSCS is a phantom item on the shelf file.
    ///
    /// The text layer never had to know: these lines carry no "&lt;barcode&gt;/&lt;SKU&gt;" token, so they
    /// never looked like products. The picture is a different matter — a name, a number and a count
    /// is exactly the shape of a product row, and the model reported them as such on five of the
    /// store's sixteen Dr Pepper samples, which then asked for an amount that is not printed anywhere
    /// and refused the invoice when nobody could supply one.
    ///
    /// Both halves are required. The name alone would throw away a real product that happens to be
    /// called a shell, and no-money alone would throw away a genuine row whose amount the scan
    /// destroyed — which is a question for the operator, not something to drop quietly. Dropping
    /// these cannot hide anything either: they are worth 0.00, so the invoice still has to reach its
    /// own AMOUNT DUE without them.
    /// </summary>
    private static bool IsReturnableEquipment(VisionRow row) =>
        row.Price is null && row.Amount is null
        && (row.Description.StartsWith("PALLET", StringComparison.OrdinalIgnoreCase)
            || row.Description.StartsWith("SHELL", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the invoice. Adds non-fatal notes (OCR-suspect UPCs, quantities the invoice's own
    /// subtotals corrected) to <paramref name="warnings"/>; throws
    /// <see cref="EdiConversionException"/> for problems that make the result untrustworthy (no
    /// header fields, no items, or figures that do not reconcile).
    ///
    /// <paramref name="ask"/> is called for a figure the scan destroyed that the invoice's own
    /// arithmetic could not recover. With no prompt available the invoice is refused instead, which
    /// is what a headless caller and the test suite get.
    ///
    /// The header fields divide in two, and the difference matters:
    ///
    ///  * <paramref name="typedNumber"/> **overrides** what the scan read. The invoice number is the
    ///    one field on the page nothing can check — no check digit, no total to reconcile against,
    ///    one printed copy — and this scanner does flip digits in short numeric fields (two scans of
    ///    ticket 5218300220 read its printed TIME as 11:29 and 11:19). So it is read off the paper
    ///    by a person, exactly as every other PDF vendor already does.
    ///  * <paramref name="suppliedDate"/> **overrides** the printed date, for the same reason: it
    ///    is typed on every invoice with the scan's reading offered in the box. A wrong year is the
    ///    one thing this scanner produces that looks entirely plausible - one sample books itself
    ///    into 2016 for 2026 - and nothing in the file can catch it.
    ///  * <paramref name="suppliedTotal"/> is a **fallback**, used only where the scan destroyed the
    ///    printed AMOUNT DUE. That figure is re-added by every package subtotal on the page, so it
    ///    checks itself.
    /// </summary>
    /// <param name="vision">
    /// The invoice as a vision model read it off the picture, when one was available. A second
    /// reader made of different mistakes from the OCR, so the two agreeing is corroboration the page
    /// cannot otherwise give. An empty reading leaves the parse exactly as it was.
    /// </param>
    public static Invoice Parse(
        IReadOnlyList<string> lines,
        List<ConversionWarning> warnings,
        AskDrPepperField? ask = null,
        DateOnly? suppliedDate = null,
        string? typedNumber = null,
        decimal? suppliedTotal = null,
        VisionReading? vision = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        string invoiceNumber = (string.IsNullOrWhiteSpace(typedNumber) ? null : typedNumber.Trim())
            ?? FindInvoiceNumber(lines)
            ?? throw new EdiConversionException(PdfReasonCodes.NoInvoiceNumber,
                "Could not read the invoice number on this Dr Pepper invoice. The PDF may be an unexpected layout.");
        DateOnly date = suppliedDate ?? FindDate(lines)
            ?? throw new EdiConversionException(PdfReasonCodes.NoDate,
                "Could not read the date on this Dr Pepper invoice.");
        decimal total = FindAmountDue(lines) ?? suppliedTotal
            ?? throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not read the AMOUNT DUE total on this Dr Pepper invoice.");

        var rows = new List<PendingRow>();
        var groups = new List<Group>();
        int groupStart = 0;               // index into rows where the open package group begins
        int? printedCases = null, printedUnits = null; // the invoice's own TOT SALE counts
        bool inCredits = false;

        for (int i = 0; i < lines.Count; i++)
        {
            // The invoice total ends the line-item section; anything after it (equipment, notices,
            // scan sheet) is not a product row.
            if (AmountDueLine().IsMatch(lines[i])) break;

            // Past this banner the columns change shape and the money turns negative.
            if (IsCreditsHeader(lines[i])) { inCredits = true; continue; }

            // A subtotal closes the package group above it; TOT SALE states the whole invoice.
            if (ReadTotals(lines[i], out int tCases, out int tUnits, out bool isInvoiceTotal))
            {
                if (isInvoiceTotal) { printedCases = tCases; printedUnits = tUnits; }
                else
                {
                    groups.Add(new Group(groupStart, rows.Count - groupStart, tCases, tUnits, i + 1));
                    groupStart = rows.Count;
                }
                continue;
            }

            // A subtotal whose own figures did not read is still a subtotal, and still the end of
            // its package. Closing the group here keeps the damage local. Without it the rows above
            // merge into the next package, whose printed counts describe only half of them, so that
            // group cannot reconcile and the operator is asked about lines nothing was ever wrong
            // with — one scanner slip on invoice 4643510004's "PEG L 15 28.12" put two clean rows
            // into question. Closed with no counts, those rows are simply reported as unchecked.
            if (LooksLikeDamagedSubtotal(lines[i]))
            {
                groupStart = rows.Count;
                continue;
            }

            var pm = MatchProductLine(lines[i]);
            string desc = "";
            string upc = "";
            string raw = "";
            bool haveProduct = false;

            if (pm is not null)
            {
                // Undo the scanner's damage to the code token, then split UPC/SKU.
                raw = pm.Groups["code"].Value;
                string code = StripCodeNoise(raw);
                int slash = code.IndexOf('/');
                upc = TrimDescriptionDigit(slash >= 0 ? code[..slash] : "");
                string sku = slash >= 0 ? code[(slash + 1)..] : "";
                haveProduct = upc.Length is >= 6 and <= 14 && sku.Length is >= 3 and <= 12;
                if (haveProduct) desc = pm.Groups["desc"].Value.Trim();
            }

            // A line that is itself a priced row, reached here rather than consumed as some
            // product's second line, is an **orphan**: the description and barcode above it did not
            // read. The money would still reconcile, so no total check can see this — the invoice
            // would go out short an item, or with the wrong barcode on one.
            if (!haveProduct)
            {
                var loose = inCredits ? ReadCredit(lines[i], out var lrow) : ReadAmounts(lines[i], out lrow);
                if (loose == RowKind.NotAmounts) continue;

                string above = i > 0 ? lines[i - 1].Trim() : "";
                if (loose == RowKind.Unreadable || ask is null)
                    throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                        $"Line {i + 1} of this Dr Pepper invoice has a price on it, but the product line above it "
                        + $"could not be read: \"{above}\". The barcode there is damaged - a pen mark, a staple, or a "
                        + "bad scan. Compare it with the paper; if the barcode is covered there too, this invoice has "
                        + "to be keyed in by hand.");

                rows.Add(new PendingRow
                {
                    Description = above.Length > 0 ? above : "the item above this price",
                    IsCredit = inCredits,
                    LineNo = i + 1,
                    Position = rows.Count + 1,
                    Scanned = lrow,
                    PrintedCode = "",           // the scan found no code here at all
                    Upc = null,                 // asked for below, once the row is otherwise settled
                    Cases = lrow.Cases,
                    Whsle = lrow.Whsle,
                    Amount = lrow.Amount,
                    Units = lrow.Units is int lu ? (inCredits ? -Math.Abs(lu) : lu) : null,
                });
                continue;
            }

            if (i + 1 >= lines.Count) break;
            var kind = inCredits
                ? ReadCredit(lines[i + 1], out var row)
                : ReadAmounts(lines[i + 1], out row);
            if (kind == RowKind.NotAmounts) continue; // a "<...>/<...>" line with no amounts row beneath it is not a product

            int lineNo = i + 2; // 1-based line number of the amounts row
            i++;                // consume the amounts row

            if (kind == RowKind.Unreadable)
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"Line {lineNo} of this Dr Pepper invoice ({desc}) could not be read: \"{lines[lineNo - 1].Trim()}\". " +
                    "The columns do not line up, so there is no telling which figure is which. Something is sitting "
                    + "across the row - a pen mark, a staple, or a bad scan. Compare that line with the paper; if it "
                    + "is covered there too, this invoice has to be keyed in by hand.");

            // The check digit is arithmetic on the other eleven digits, so a code that fails it has a
            // wrong digit in it. That is what the test *means* — not "might" — and it holds however
            // the digit went wrong: a mark we stripped may have taken one out with it, or the scan
            // may simply have misread one off perfectly clean paper. Invoice 4643510001 read Evian's
            // printed 079298000078 as 079198000078 — twelve clean digits, nothing sitting on them.
            //
            // Nothing else on the page can see this. The money reconciles whatever code goes out, so
            // a wrong barcode books the delivery onto an item that is not this one and stays wrong
            // all the way into SSCS. So it is asked, with the printed row shown beside the question.
            // It used to ship with a warning that said "may be wrong" — true, and ignorable.
            bool barcodeLost = !Upc.IsValidGtin(upc);
            if (barcodeLost && ask is null)
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"Line {lineNo - 1} of this Dr Pepper invoice ({desc}) has a barcode that fails its own "
                    + $"check digit: it scanned as \"{raw.Trim()}\", which reads as '{upc}'. A digit in it is "
                    + "wrong. Compare it with the paper; this invoice has to be keyed in by hand.");

            rows.Add(new PendingRow
            {
                Description = desc,
                IsCredit = inCredits,
                LineNo = lineNo,
                Position = rows.Count + 1,
                Scanned = row,
                // The barcode half only. The SKU rides along in the same token but is not written to
                // the XML and is not what is being asked about, so showing it is noise on a prompt
                // whose whole job is to point at one number on the paper.
                PrintedCode = BarcodeHalf(raw),
                Upc = barcodeLost ? null : upc,
                Cases = row.Cases,
                Whsle = row.Whsle,
                Amount = row.Amount,
                // Credits are goods going back. SSCS wants the sign on the count, not the money, so
                // units x cost is still the printed amount.
                Units = row.Units is int u ? (inCredits ? -Math.Abs(u) : u) : null,
            });
        }

        // A ticket carrying returns prints a SALES SUMMARY block where a plain one prints TOT SALE,
        // so this is the same backstop reached a second way — and the only count the credit rows get.
        ReadSalesSummary(lines, out int? summaryCases, out int? summaryUnits, out int? summaryCredits);
        printedCases ??= summaryCases;
        printedUnits ??= summaryUnits;

        var seen = vision ?? VisionReading.Empty;

        // A page the OCR could not read - crooked through the scanner, a pen mark across it - is
        // exactly the page a person can read at a glance, and until now it was a total loss. Where
        // the text layer found no rows and the picture did, the picture IS the invoice.
        //
        // These rows get no second reader and are not pretended to have one. What checks them is the
        // invoice's own arithmetic: CASES x NET against AMOUNT on each row, and AMOUNT DUE across all
        // of them. A row that cannot pass those still goes to the operator.
        bool fromThePicture = rows.Count == 0 && seen.Rows.Count > 0;
        if (fromThePicture)
        {
            rows.AddRange(RowsFromThePicture(seen));
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"Nothing could be read from the text in this PDF, so all {rows.Count} lines were read "
                + "off the picture of the page instead. Check them against the paper before sending."));

            // TOT SALE, read off the same picture — and the only thing on the page that can see a
            // misread UNITS. The money cannot: 342 units against the printed 326 reached this
            // invoice's own AMOUNT DUE to the cent (sample DownloadFile (13), 2026-08-11), because
            // a wrong count and a right amount just give a wrong unit cost. Without this the rows
            // are checked by their dollars alone and the stock goes into SSCS wrong in silence.
            //
            // Not "confirmation" of anything the rows say, and not derived from them: it is a
            // separate figure printed at the foot of the ticket, which is exactly what the text-layer
            // path uses it as.
            printedUnits ??= seen.PrintedUnits;
        }
        else if (seen.Rows.Count > rows.Count)
        {
            // Both read rows, but not the same number. The text layer has lost one, and a lost row
            // takes its money with it so no total can notice. Said out loud rather than patched - the
            // rows that did read are each backed by two readings, and trading all of that away to
            // recover one row would be the worse deal.
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"The text in this PDF gives {rows.Count} lines but the picture of the page shows "
                + $"{seen.Rows.Count}. A line is probably missing - count them against the paper "
                + "before sending."));
        }

        // Tie the second reader's rows to ours before anything is settled. Dr Pepper's priced row
        // carries no item number of its own, so the UPC off the product line above it is the anchor.
        // Rows that came off the picture are skipped: one reader agreeing with itself is not a check,
        // and treating it as one would wave every figure straight through.
        if (!fromThePicture)
        {
            var aligned = seen.AlignTo(
                [.. rows.Select(r => new VisionAnchor(r.Upc, r.Units, r.Amount))], v => v.Barcode);
            for (int i = 0; i < rows.Count; i++) rows[i].Vision = aligned[i];
        }

        SettleRows(rows, groups, printedCases, printedUnits, summaryCredits, warnings, ask);
        SettleTotal(rows, total, warnings, ask);

        var items = new List<InvoiceLine>();
        decimal sum = 0m;
        foreach (var r in rows)
        {
            int units = r.Units!.Value;
            decimal amount = r.Amount!.Value;
            int unitsSafe = units != 0 ? Math.Abs(units) : 1;
            decimal unitCost = decimal.Round(Math.Abs(amount) / unitsSafe, 4, MidpointRounding.AwayFromZero);

            // The WHSLE column is Dr Pepper's own list price per case, which is above the discounted
            // price actually billed. Per unit it is the closest thing the invoice gives us to a
            // suggested selling price, and it only ever seeds a **brand-new** item — SSCS keeps its
            // own for anything it already stocks.
            //
            // So a mark on WHSLE or CASES is not worth a person's time and certainly not worth
            // refusing a delivery over: the shared cost-plus seed stands in, exactly as it does for
            // a credit, which has no WHSLE column at all. It is still said out loud, because on a
            // new item it is the price that reaches the shelf.
            bool seedOnly = !r.IsCredit && (r.Whsle is null || r.Cases is null);

            // Said once per row where the scan lost the column, and **not at all** where the row came
            // off the picture. The reader looking at the picture is deliberately not asked for WHSLE
            // — it is a list price nothing is built from — so on a page with no text layer this is
            // absent by design on every row, and reporting it printed twenty-two identical lines that
            // pushed the one warning that mattered off the top of the box. A note nobody can act on,
            // repeated per row, teaches the operator to close the notes unread.
            if (seedOnly && r.LineNo > 0)
                warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UnreadableLine,
                    $"{r.Description} - "
                    + (r.Whsle is null
                        ? Scan("WHSLE", r.Scanned.PrintedWhsle)
                        : Scan("CASES", r.Scanned.PrintedCases))
                    + ". Only a new item's shelf price is affected."));

            decimal retail = r.IsCredit || seedOnly
                ? RetailSeed.FromCost(unitCost)
                : decimal.Round(r.Whsle!.Value * r.Cases!.Value / unitsSafe, 2, MidpointRounding.AwayFromZero);

            items.Add(new InvoiceLine
            {
                // Whole printed barcode. SSCS removes the check digit on import, landing on exactly
                // the item the retired EDI output addressed.
                Gtin = r.Upc!,
                Description = r.Description,
                Units = units,
                UnitCost = unitCost,
                RetailPrice = retail,
            });
            sum += amount;
        }

        // Returns are normal vendor activity. Keep the count in the conversion details and log, but
        // do not turn an otherwise verified invoice amber merely because stock moved back.
        int creditCount = items.Count(l => l.Units < 0);
        if (creditCount > 0)
        {
            var numbers = FindInvoiceNumbers(lines);
            string alsoNumbered = numbers.Count > 1
                ? $" The ticket prints {numbers.Count} invoice numbers ({string.Join(" and ", numbers)}); "
                  + $"all of it went out under {invoiceNumber}, which is how the printed Amount Due is totalled."
                : "";
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This invoice has {creditCount} credit line{(creditCount == 1 ? "" : "s")} — goods that went back on "
                + "the truck. They go to SSCS as negative quantities at a positive cost, so the delivery nets out to "
                + $"the printed total.{alsoNumbered}",
                Routine: true));
        }

        if (items.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No product line items were found on this Dr Pepper invoice.");

        // Hard gate: the parsed line items must reconcile to the printed AMOUNT DUE. A mismatch means
        // a page is missing, duplicated, or misread — refuse rather than emit a wrong file.
        if (Math.Abs(sum - total) > items.Count * 0.01m)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"The line items add up to {sum:0.00} but the invoice AMOUNT DUE is {total:0.00}. " +
                "A page may be missing or misread. Please re-scan the whole invoice as one file and try again.");

        return new Invoice
        {
            SupplierName = SupplierName,
            InvoiceNumber = invoiceNumber,
            Date = date,
            Total = total,
            Lines = items,
        };
    }

    /// <summary>
    /// The product line, falling back to the damaged-code pattern. The strict one is tried first, so
    /// the fallback is only ever reached by a line nothing else claimed.
    /// </summary>
    private static Match? MatchProductLine(string line)
    {
        var m = ProductLine().Match(line);
        if (m.Success) return m;
        m = DamagedProductLine().Match(line);
        return m.Success ? m : null;
    }

    /// <summary>
    /// Settles every row's UNITS against what the invoice says about itself, because that is the one
    /// figure no money check can vouch for. A row misread as 11 units where the paper says 12 leaves
    /// WHSLE, CASES, AMOUNT and the invoice total all correct, and books a unit short into SSCS
    /// where nothing downstream will ever notice.
    ///
    /// Two printed statements are available and both are used:
    ///
    ///  * **The package group's own subtotal** ("PEG 2 24 40.00"). Every row in a group is a whole
    ///    number of cases of one pack size, so the subtotal states that pack: units ÷ cases. A row
    ///    whose UNITS is not cases × pack is the damaged one, and correcting exactly those rows has
    ///    to land the group back on its printed count — which it does only if the case column adds
    ///    up too. That makes the repair arithmetic rather than a guess, and it fills a UNITS column
    ///    the scan destroyed outright by the same rule.
    ///  * **TOT SALE** ("TOT SALE 44 585 1080.22"), as the backstop for a group whose own subtotal
    ///    was unreadable.
    ///
    /// Where the arithmetic cannot decide, the operator is asked for that one number. Where there is
    /// no prompt, the invoice is refused rather than shipped with a quantity nobody stands behind.
    /// </summary>
    private static void SettleRows(
        List<PendingRow> rows,
        List<Group> groups,
        int? printedCases,
        int? printedUnits,
        int? printedCreditUnits,
        List<ConversionWarning> warnings,
        AskDrPepperField? ask)
    {
        SettleAmounts(rows, warnings, ask);
        SettleBarcodes(rows, warnings, ask);

        foreach (var g in groups)
        {
            if (g.Count <= 0) continue;
            var members = rows.GetRange(g.First, g.Count);

            // Credits print no subtotal of their own, so a group is sales only; a stray one is left
            // alone rather than reconciled against counts that do not describe it.
            if (members.Any(r => r.IsCredit)) continue;

            int? pack = g.Cases > 0 && g.Units % g.Cases == 0 ? g.Units / g.Cases : null;

            // A damaged CASES is worth nothing on its own — it feeds only the shelf price seeded for
            // an item SSCS has never seen — but the pack size turns it back into a real figure, and
            // the group cannot be reconciled with a hole in it.
            if (pack is int pc)
                foreach (var r in members.Where(r => r.Cases is null && r.Units is not null))
                    r.Cases = Math.Max(1, r.Units!.Value / pc);

            bool casesAgree = members.All(r => r.Cases is not null)
                && members.Sum(r => r.Cases!.Value) == g.Cases;

            // Nothing to do: every row read, and the group adds up to its own subtotal.
            if (members.All(r => r.Units is not null) && members.Sum(r => r.Units!.Value) == g.Units)
                continue;

            // The package subtotal has done its whole job the moment it disagrees: it says one of
            // these rows is wrong, and it does not say what the right figure is. Working the count
            // out from cases x pack, or from the difference the group is short, is arithmetic
            // resting on the other columns having been read correctly - and a wrong QTY there gives
            // a quantity that agrees with every total on the page and is simply not what came off
            // the truck. So the subtotal names the row, and the operator reads it off the picture.
            //
            // Which rows: the ones with no readable count, plus - where the pack size is known and
            // the cases add up - any row whose count contradicts it. Failing both, the whole group,
            // because nothing has narrowed it down.
            var holes = members.Where(r => r.Units is null).ToList();
            var suspects = pack is int p && casesAgree
                ? members.Where(r => r.Units is null || r.Units != r.Cases!.Value * p).ToList()
                : holes.Count > 0 ? holes : members;

            // The picture read a count for every row the subtotal suspects, and putting those counts
            // in lands the package on the unit total it prints for itself. Figures that were read,
            // confirmed by a printed total neither reader had a hand in — the same standard the
            // operator's own answers are held to, so it needs no question.
            if (TakeGroupUnitsFromPicture(members, suspects, g, warnings)) continue;

            foreach (var r in suspects)
                Settle(r, AskUnits(r, rows.Count, ask, warnings,
                    $"the invoice totals this package as {g.Cases} case{(g.Cases == 1 ? "" : "s")} / {g.Units} "
                    + "units, which the rows do not add up to"));
        }

        // Anything still unread belonged to no group with a readable subtotal - and every credit,
        // since returns never print one.
        foreach (var r in rows.Where(r => r.Units is null))
        {
            // The scan lost the count and the picture read one. There is no disagreement to settle
            // here, only a hole - so it is taken and flagged rather than asked about. A count read
            // once beats a count nobody read at all, and the note puts it in front of a person.
            if (r.Vision?.Units is int seen && seen > 0)
            {
                warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.QtyMismatch,
                    $"{r.Description} - {Scan("UNITS", r.Scanned.PrintedUnits)}. Read off the picture "
                    + $"as {seen}. No package total on this invoice can confirm it - check this one "
                    + "against the paper."));
                Settle(r, seen);
                continue;
            }

            Settle(r, AskUnits(r, rows.Count, ask, warnings,
                r.IsCredit
                    ? "returns print no package total to check them against"
                    : "the invoice prints no package total that could settle it"));
        }

        // Lines no package total covered. Nothing here is wrong — but nothing checked it either, and
        // a quantity is the one figure the money cannot vouch for, so it is said out loud rather
        // than left looking as verified as every other line on the invoice. This is the gap that
        // used to be entirely silent: a subtotal the scan destroyed is not asked about and not
        // reported, it simply stops being a check.
        //
        // Only worth saying on an invoice that prints package totals at all — otherwise there is no
        // missing one to point at.
        var covered = new bool[rows.Count];
        foreach (var g in groups)
            for (int i = g.First; i < Math.Min(g.First + g.Count, rows.Count); i++) covered[i] = true;

        for (int i = 0; groups.Count > 0 && i < rows.Count; i++)
            if (!covered[i] && !rows[i].IsCredit)
                warnings.Add(new ConversionWarning(rows[i].LineNo, PdfReasonCodes.QtyMismatch,
                    $"{rows[i].Description} - no package total to check this line. "
                    + $"Used {Math.Abs(rows[i].Units!.Value)} as scanned."));

        // Credits print no subtotal of their own, so the summary block is the only line on the page
        // that counts them at all. A warning and not a refusal: the returns still net out against
        // AMOUNT DUE either way, and a handful of units going back on a delivery of hundreds is worth
        // a person's eyes on the paper, not worth sending the whole invoice away.
        var credits = rows.Where(r => r.IsCredit).ToList();
        if (printedCreditUnits is int wantCredits && credits.Count > 0
            && credits.Sum(r => Math.Abs(r.Units!.Value)) is var gotCredits && gotCredits != wantCredits)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The credit lines add up to {gotCredits} units, but this invoice's own summary says {wantCredits}. "
                + "The dollars still add up, so what SSCS books for the sale is right - please check the CREDITS "
                + "section against the paper."));

        // Backstop: the invoice's own count of itself. This is what catches a group whose subtotal
        // was unreadable, and it is checked against the sales rows because the invoice's own total
        // counts the sale only — credits are totalled separately, above.
        var sales = rows.Where(r => !r.IsCredit).ToList();
        if (sales.Count == 0) return;

        if (printedUnits is int want && sales.Sum(r => r.Units!.Value) is var got && got != want)
            throw new EdiConversionException(PdfReasonCodes.QtyMismatch,
                $"The lines add up to {got} units, but this invoice's own total says {want}. A quantity was "
                + "misread. The dollars still add up, so nothing later would catch it - the delivery would go into "
                + "SSCS short. Please re-scan the invoice and try again.");

        if (printedCases is int wantCases
            && sales.All(r => r.Cases is not null)
            && sales.Sum(r => r.Cases!.Value) != wantCases)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The lines add up to {sales.Sum(r => r.Cases!.Value)} cases, but this invoice's own total says "
                + $"{wantCases}. The unit counts agree, so what SSCS books is right - only the shelf price it "
                + "suggests for a brand-new item could be off. Worth a glance at the paper."));
    }

    /// <summary>
    /// Settles a barcode the scan destroyed — either a mark that took a digit out of it, or a
    /// product line that did not read at all above a row that is plainly priced.
    ///
    /// Nothing on the page can recover this one. The money reconciles whatever code goes out, so the
    /// total gate is blind to it, and an item landing on the wrong PLU is silent all the way into
    /// SSCS. So it is the operator or nothing — and where there is no prompt, nothing means refusing
    /// the invoice rather than shipping a line under a barcode we do not believe.
    /// </summary>
    private static void SettleBarcodes(
        List<PendingRow> rows, List<ConversionWarning> warnings, AskDrPepperField? ask)
    {
        foreach (var r in rows.Where(r => r.Upc is null))
        {
            // Guarded at the point of reading, where the row and its raw text are still to hand.
            if (ask is null) throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The barcode for {r.Description} could not be read and there is no way to ask for it.");

            string typed = ask(new DrPepperUnknownField(
                r.Position, rows.Count, r.Description, r.PrintedCode, LineField.Barcode,
                r.PrintedCode, r.Units, r.Amount, r.LineNo, r.Page, r.PageUpsideDown, r.IsCredit));

            // The invoice prints "<barcode>/<SKU>", so someone copying the whole token is reading the
            // paper right, not wrong. The slash says where the barcode ends; without cutting there,
            // DigitsOnly would weld both halves into one long number and the line would be refused.
            // Done here as well as in the dialog because the parser takes nothing on trust from a
            // caller it does not control.
            string answer = typed ?? "";
            int slash = answer.IndexOf('/');
            if (slash > 0) answer = answer[..slash];

            string digits = Upc.DigitsOnly(answer);
            // An 11-digit body is a whole barcode with the check digit left off; the digit is
            // arithmetic on the other eleven, so putting it back is exact rather than a guess.
            if (digits.Length == 11) digits = Upc.AppendCheckDigit(digits);

            if (digits.Length is < 11 or > 14)
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"\"{typed}\" is not a barcode, so {r.Description} could not be filled in. "
                    + "The original file was not changed.");

            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UpcCheckDigit,
                Upc.IsValidGtin(digits)
                    ? $"{r.Description} - the barcode did not scan. You typed {digits}."
                    : $"{r.Description} - you typed {digits}, which fails its own check digit. Used as "
                      + "typed - check this line in SSCS."));
            r.Upc = digits;
        }
    }

    /// <summary>
    /// Settles a damaged AMOUNT column by asking, and **deliberately not by arithmetic**.
    ///
    /// AMOUNT DUE minus every other row would give the missing figure exactly, and that is precisely
    /// why it must not be used: the sum-equals-total gate is the only independent check this invoice
    /// has, and a row back-filled from the total satisfies it by construction. Spend it on one line
    /// and the invoice can no longer detect a second error anywhere on the page — it would balance
    /// to the penny with the damaged line quietly absorbing whatever else went wrong.
    ///
    /// Asking keeps the gate honest: what the operator reads off the paper is an independent figure,
    /// and the total then checks it. Without a prompt the invoice is refused, as before.
    /// </summary>
    private static void SettleAmounts(
        List<PendingRow> rows, List<ConversionWarning> warnings, AskDrPepperField? ask)
    {
        foreach (var r in rows.Where(r => r.Amount is null))
        {
            // The picture read the column the scan lost, and the row's own CASES x NET — different
            // glyphs, damaged separately — comes to the same figure. That is an amount confirmed
            // rather than filled in, which is the only way one may go in without a person.
            if (TakeAmountFromPicture(r, warnings)) continue;

            if (ask is null)
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"Could not read the amount for {r.Description} - the scan says \"{r.Scanned.PrintedAmount}\". "
                    + $"It is line {r.LineNo} on the invoice. Something is sitting on that figure: a pen mark, a "
                    + "staple, or a bad scan. Compare it with the paper; if it is covered there too, this invoice "
                    + "has to be keyed in by hand.");

            AskAmount(r, rows.Count, ask, warnings);
        }
    }

    /// <summary>
    /// Takes a row's AMOUNT off the picture, but only where the row's own arithmetic says the same
    /// thing. True when it was taken.
    ///
    /// The vision reading and CASES x NET are independent of each other in the way that matters: one
    /// comes off the photograph, the other out of the text layer, so they do not fail together. A
    /// vision reading nothing agrees with is left alone and the operator is asked, because a second
    /// reader is still one reader.
    /// </summary>
    private static bool TakeAmountFromPicture(PendingRow r, List<ConversionWarning> warnings)
    {
        if (r.Vision?.Amount is not decimal seen || seen <= 0m) return false;

        bool confirmed = r.Scanned.Cases is > 0 && r.Scanned.Net is not null
                         && Math.Abs(r.Scanned.Cases.Value * r.Scanned.Net.Value - seen) <= 0.005m;

        r.Amount = r.IsCredit ? -Math.Abs(seen) : seen;
        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.LineMathMismatch,
            $"{r.Description} - {Scan("AMOUNT", r.Scanned.PrintedAmount)}. Read off the picture as "
            + (confirmed
                ? $"{seen:0.00}, which is what CASES x NET on that row comes to. Check it before sending."
                : $"{seen:0.00}. Nothing on the row confirms it - check this one against the paper.")));
        return true;
    }

    /// <summary>
    /// Asks the operator for one row's AMOUNT and puts it on the row, with the sign SSCS wants.
    /// Shared by the column that did not read at all and by the one the invoice's own total says
    /// cannot be right.
    /// </summary>
    private static void AskAmount(
        PendingRow r, int lineCount, AskDrPepperField ask, List<ConversionWarning> warnings)
    {
        string typed = ask(new DrPepperUnknownField(
            r.Position, lineCount, r.Description, r.Upc ?? "", LineField.Amount,
            r.Scanned.PrintedAmount, r.Units, null, r.LineNo, r.Page, r.PageUpsideDown, r.IsCredit));

        string cleaned = new((typed ?? "").Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount <= 0m)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"\"{typed}\" is not a dollar amount, so {r.Description} could not be filled in. "
                + "The original file was not changed.");

        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.LineMathMismatch,
            $"{r.Description} - {Scan("AMOUNT", r.Scanned.PrintedAmount)}. You typed {amount:0.00}."));
        r.Amount = r.IsCredit ? -Math.Abs(amount) : amount;
    }

    /// <summary>
    /// Settles an invoice whose rows do not add up to its own AMOUNT DUE — by asking, and never by
    /// arithmetic.
    ///
    /// This was a flat refusal, and refusing was throwing away deliveries that were one glyph away
    /// from perfect. Invoice 4642411699 bills nine cases of Slot at $21.38 and scans one of them as
    /// `11.38`; invoice 4642411685 bills a $21.40 Clamato and scans it `11.40`. Every other figure on
    /// both pages is right, both are plainly legible on the paper, and "please re-scan the whole
    /// invoice" is not something a second scan of the same paper fixes.
    ///
    /// The rows are narrowed before anybody is asked, because a question on every line is a question
    /// nobody reads. A sales row states its own money twice — the AMOUNT column, and CASES x NET —
    /// and those are separate glyphs, so a row where the two agree has checked itself and a row where
    /// they disagree is the row that is wrong. On both invoices above that is a single line. NET is
    /// the most OCR-prone column on the page, so it is never a **source** here: where it did not
    /// read, the row is merely unconfirmed, and it is only asked about once the rows that plainly
    /// contradict themselves have failed to account for the difference.
    ///
    /// The gate itself does not move. What the operator types is an independent reading of the paper
    /// and AMOUNT DUE still has to accept it, so an invoice that will not reconcile even after every
    /// unchecked figure has been read by a person has something structural wrong with it — a missing
    /// page, a duplicated one — and is still refused by the caller.
    /// </summary>
    private static void SettleTotal(
        List<PendingRow> rows, decimal total, List<ConversionWarning> warnings, AskDrPepperField? ask)
    {
        if (ask is null) return;   // no prompt: the caller's own gate refuses, exactly as it always did

        bool Reconciles() => Math.Abs(rows.Sum(r => r.Amount!.Value) - total) <= rows.Count * 0.01m;
        if (Reconciles()) return;

        // Rung 0: a row the picture read differently from the text layer. Putting the picture's
        // figure in and finding the invoice then lands on its own AMOUNT DUE is that reading
        // confirmed by the one genuinely independent check this page has — so the row is corrected
        // and nobody is asked. Tried one row at a time first, since one damaged figure is the usual
        // case and a single substitution says exactly which row it was.
        if (SettleTotalFromPicture(rows, Reconciles, warnings)) return;

        // Rung 1: the rows whose own two readings of their money disagree.
        foreach (var r in rows.Where(ContradictsItsOwnRow).ToList())
        {
            AskAmount(r, rows.Count, ask, warnings);
            if (Reconciles()) return;
        }

        // Rung 2: the rows nothing checked at all — no CASES, or no NET, so the row never stated its
        // money twice. Asked in the order they are printed, so the operator works down the page.
        foreach (var r in rows.Where(r => !ConfirmsItsOwnRow(r) && !ContradictsItsOwnRow(r)).ToList())
        {
            AskAmount(r, rows.Count, ask, warnings);
            if (Reconciles()) return;
        }
    }

    /// <summary>
    /// Tries to reconcile the invoice using what the picture read, and reports whether it worked.
    ///
    /// Every substitution is a figure a reader actually took off the paper, never one worked out
    /// from the total — so AMOUNT DUE stays an independent check rather than becoming a thing the
    /// answer was derived from. Where no combination reconciles, every row is put back exactly as it
    /// was and the operator is asked in the usual order.
    /// </summary>
    private static bool SettleTotalFromPicture(
        List<PendingRow> rows, Func<bool> reconciles, List<ConversionWarning> warnings)
    {
        var disputed = rows.Where(DisagreesWithPicture).ToList();
        if (disputed.Count == 0) return false;

        var original = disputed.ToDictionary(r => r, r => r.Amount!.Value);
        void Restore() { foreach (var (r, was) in original) r.Amount = was; }
        void TakePicture(PendingRow r)
        {
            decimal seen = r.Vision!.Amount!.Value;
            r.Amount = r.IsCredit ? -Math.Abs(seen) : seen;
        }
        void NoteIt(PendingRow r) =>
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.LineMathMismatch,
                $"{r.Description} - {Scan("AMOUNT", r.Scanned.PrintedAmount)}. Read off the picture as "
                + $"{Math.Abs(r.Amount!.Value):0.00}, which is what the invoice's own total needs. "
                + "Check it before sending."));

        foreach (var r in disputed)
        {
            TakePicture(r);
            if (reconciles()) { NoteIt(r); return true; }
            r.Amount = original[r];
        }

        // More than one figure damaged, which happens on the worse scans. Only worth trying once the
        // single substitutions have all failed, because it says less about which row was wrong.
        if (disputed.Count > 1)
        {
            foreach (var r in disputed) TakePicture(r);
            if (reconciles())
            {
                foreach (var r in disputed) NoteIt(r);
                return true;
            }
            Restore();
        }

        return false;
    }

    /// <summary>
    /// True when both readers have an opinion about this row's money and they differ. Silent where
    /// the picture read nothing, which is every row when no vision reader is configured.
    /// </summary>
    private static bool DisagreesWithPicture(PendingRow r) =>
        r.Amount is not null && r.Vision?.Amount is decimal seen && seen > 0m
        && Math.Abs(Math.Abs(r.Amount.Value) - seen) > 0.005m;

    /// <summary>
    /// True when the row's AMOUNT column and its own CASES x NET disagree — the row saying two
    /// different things about its own money. Both are printed and the scan damaged one of them.
    ///
    /// The scanned CASES is used rather than the settled one: a case count the package subtotal
    /// filled in is arithmetic, and checking a figure against arithmetic derived from the same page
    /// is not a second opinion. Silent wherever either column is missing.
    /// </summary>
    private static bool ContradictsItsOwnRow(PendingRow r) =>
        RowStatesItsMoneyTwice(r)
        && Math.Abs(r.Scanned.Cases!.Value * r.Scanned.Net!.Value - r.Amount!.Value) > 0.005m;

    /// <summary>True when those two readings agree, which is the row checking itself.</summary>
    private static bool ConfirmsItsOwnRow(PendingRow r) =>
        RowStatesItsMoneyTwice(r)
        && Math.Abs(r.Scanned.Cases!.Value * r.Scanned.Net!.Value - r.Amount!.Value) <= 0.005m;

    /// <summary>
    /// Whether the row can be checked against itself at all. Credits are excluded: they print under
    /// a shorter header whose NET is the unit rate rather than the case price, so the identity is
    /// simply not the same one.
    /// </summary>
    private static bool RowStatesItsMoneyTwice(PendingRow r) =>
        !r.IsCredit && r.Scanned.Cases is > 0 && r.Scanned.Net is not null && r.Amount is not null;

    /// <summary>
    /// Settles a package group's disputed unit counts from the picture, and reports whether it
    /// worked. All of them or none: a group is only reconciled when every row the subtotal put in
    /// question has a reading and the group then lands exactly on its printed count.
    ///
    /// The printed subtotal is doing here what it always does — confirming figures somebody read,
    /// never supplying one. Filling a count in from the difference the group is short would satisfy
    /// the check by construction and book whatever else went wrong onto that row.
    /// </summary>
    private static bool TakeGroupUnitsFromPicture(
        List<PendingRow> members, List<PendingRow> suspects, Group g, List<ConversionWarning> warnings)
    {
        if (suspects.Count == 0) return false;

        var proposed = new Dictionary<PendingRow, int>();
        foreach (var r in suspects)
        {
            if (r.Vision?.Units is not int seen || seen <= 0) return false;
            proposed[r] = seen;
        }

        // A row outside the suspects with no count at all leaves a hole the sum cannot see past.
        if (members.Any(r => !proposed.ContainsKey(r) && r.Units is null)) return false;
        if (members.Sum(r => proposed.TryGetValue(r, out int v) ? v : r.Units!.Value) != g.Units)
            return false;

        foreach (var (r, v) in proposed)
        {
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.QtyMismatch,
                $"{r.Description} - {Scan("UNITS", r.Scanned.PrintedUnits)}. Read off the picture as "
                + $"{v}, which lands this package on the {g.Units} units the invoice prints for it."));
            Settle(r, v);
        }
        return true;
    }

    /// <summary>
    /// Puts a settled count on a row, with the sign SSCS wants: returns are negative quantities at a
    /// positive cost, so goods going back on the truck subtract from stock.
    /// </summary>
    private static void Settle(PendingRow row, int units) =>
        row.Units = row.IsCredit ? -Math.Abs(units) : units;

    /// <summary>
    /// Asks the operator for one row's UNITS. Refuses when there is no prompt — the same outcome the
    /// invoice had before, but reached with the row named instead of a total that does not add up.
    /// </summary>
    private static int AskUnits(
        PendingRow row, int lineCount, AskDrPepperField? ask, List<ConversionWarning> warnings, string why)
    {
        if (ask is null)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Could not read how many units {row.Description} is for - {Scan("UNITS", row.Scanned.PrintedUnits)}, "
                + $"and {why}. It is line {row.LineNo} on the invoice. Something is "
                + "sitting on that number: a pen mark, a staple, or a bad scan. If it is covered on the paper too, "
                + "this invoice has to be keyed in by hand.");

        string typed = ask(new DrPepperUnknownField(
            row.Position, lineCount, row.Description, row.Upc ?? "", LineField.Units,
            row.Scanned.PrintedUnits, null, row.Amount, row.LineNo, row.Page, row.PageUpsideDown,
            row.IsCredit));

        // Whatever the prompt collected still has to be a plain count; the dialog checks this too,
        // but the parser does not take a figure on trust from a caller it does not control.
        string digits = new((typed ?? "").Where(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int units) || units < 1)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"\"{typed}\" is not a number of units, so {row.Description} could not be filled in. "
                + "The original file was not changed.");

        warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.QtyMismatch,
            $"{row.Description} - {Scan("UNITS", row.Scanned.PrintedUnits)}. You typed {units}."));
        return units;
    }

    /// <summary>
    /// Checks that this really is a Dr Pepper invoice the right way up, before anybody is asked
    /// anything about it.
    ///
    /// One of the store's scans came back **upside down**. The OCR read the rotated page line by
    /// line and produced a full text layer of nothing: "AMOUNT DUE $1263.93" reaches us as
    /// "£6'£9ZIS an0 J.NnOHiI", because every glyph is its own 180-degree twin — 5 for S, 2 for Z,
    /// 6 for 9. Nothing on the page matches anything, so the invoice number prompt opened, then the
    /// total prompt, and only then did it fail — three windows for a page nobody could have read.
    ///
    /// The test is deliberately all-or-nothing: no date, no AMOUNT DUE, **and** no product row. A
    /// badly damaged invoice loses one or two of those and is still worth converting; losing all
    /// three means the page is not what the operator thinks it is. It cannot tell upside-down from
    /// wrong-vendor, so it says both — and both are fixed in a few seconds at the scanner.
    /// </summary>
    public static void RequireAReadableInvoice(IReadOnlyList<string> lines)
    {
        if (LooksReadable(lines)) return;

        throw new EdiConversionException(PdfReasonCodes.NoLineItems,
            "Nothing on this invoice could be read - no date, no amount due, and no product lines - "
            + "even though the PDF does have text in it. The page was probably scanned upside down, "
            + "or this is not a Dr Pepper invoice. Check the vendor above the file list, then put the "
            + "paper in the scanner the right way up and scan it again.");
    }

    /// <summary>
    /// Whether the text layer is worth reading at all — a date, an amount due, or a product line.
    ///
    /// Public because a caller with a second reader needs the answer rather than the refusal. A page
    /// that fails this is not merely damaged, it is **noise**: the upside-down scan matched garbage
    /// like "SO'6Z ZI T 9Yd" as a product row, so leaving its rows in place alongside a reading taken
    /// off the picture would put both in the invoice. The text layer has to be discarded whole.
    /// </summary>
    public static bool LooksReadable(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return FindDate(lines) is not null
               || FindAmountDue(lines) is not null
               || lines.Any(l => MatchProductLine(l) is not null);
    }

    /// <summary>
    /// The number printed under the INVOICE banner, or null when the scan destroyed it. Public so
    /// the caller can get one from the operator before parsing.
    /// </summary>
    public static string? FindInvoiceNumber(IReadOnlyList<string> lines) =>
        FindInvoiceNumbers(lines).FirstOrDefault();

    /// <summary>
    /// Every number printed under the INVOICE banner. A delivery that carries returns prints two —
    /// 4646105204 and 4646105205 on the 07/24/26 ticket — evidently one document for the sale and
    /// one for the credit, though the ticket totals them into a single Amount Due. We file the whole
    /// thing under the first and say so, rather than guess at splitting one payable in two.
    /// </summary>
    private static List<string> FindInvoiceNumbers(IReadOnlyList<string> lines)
    {
        var found = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals("INVOICE", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
            {
                string t = lines[j].Trim();
                if (t.Length is >= 6 and <= 12 && t.All(char.IsAsciiDigit))
                    found.Add(t);
            }
            if (found.Count > 0) break;
        }
        return found;
    }

    /// <summary>
    /// The printed DATE, or null when the scan destroyed it. Public so the caller can get one from
    /// the operator before parsing, rather than parsing twice or prompting from inside the parser.
    /// </summary>
    public static DateOnly? FindDate(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = DateLine().Match(line);
            if (!m.Success) continue;
            int mm = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int dd = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int yy = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (mm is < 1 or > 12) continue;
            int year = 2000 + yy;
            if (dd < 1 || dd > DateTime.DaysInMonth(year, mm)) continue;
            return new DateOnly(year, mm, dd);
        }
        return null;
    }

    /// <summary>
    /// The printed AMOUNT DUE, or null when it could not be read. Public so a prompt raised before
    /// parsing can show the operator the ticket's own total and let them match the paper.
    /// </summary>
    public static decimal? FindAmountDue(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = AmountDueLine().Match(line);
            if (m.Success)
                return decimal.Parse(m.Groups["amt"].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>Parses a money token, normalizing an OCR-slipped ':' or ',' decimal separator to '.'.</summary>
    private static decimal Money(string s) =>
        decimal.Parse(s.Replace(':', '.').Replace(',', '.').Replace(';', '.'), CultureInfo.InvariantCulture);
}

/// <summary>
/// One figure on one Dr Pepper row that the scan destroyed and the invoice's own subtotals could not
/// recover. Described well enough for the operator to find the row on the paper without counting.
/// </summary>
public sealed record DrPepperUnknownField(
    /// <summary>1-based position among the invoice's products — how the operator counts down it.</summary>
    int Position,
    /// <summary>How many products the invoice has, so the prompt can say "3 of 37".</summary>
    int LineCount,
    string Description,
    /// <summary>The barcode, which is what the operator will match against the paper.</summary>
    string Gtin,
    LineField Field,
    /// <summary>What the scan made of the field, so the operator can see what it got wrong.</summary>
    string PrintedValue,
    /// <summary>The row's units, when they read — context, never the field being asked about.</summary>
    int? Units,
    /// <summary>The row's amount, when it read — context, never the field being asked about.</summary>
    decimal? Amount,
    /// <summary>
    /// 1-based line number of the priced row in the extracted text, so the caller can cut that row
    /// out of the scanned picture and show the operator the paper rather than describe it.
    /// </summary>
    int LineNo = 0,
    /// <summary>
    /// 1-based page this row is on, for a row read off the picture. With no line number there is no
    /// band to cut, and the whole page is shown instead - so the caller has to know which page.
    /// </summary>
    int Page = 0,
    /// <summary>Whether that page went through the scanner the wrong way up.</summary>
    bool PageUpsideDown = false,
    /// <summary>
    /// Whether this row sits under the CREDITS banner, which prints a **different column set** —
    /// no WHSLE — so the legend shown above the row has to follow it. Sales columns over a credit
    /// row would have the operator counting across to the figure next door.
    /// </summary>
    bool IsCredit = false);

/// <summary>Asks for one figure on one Dr Pepper row. Returns what the operator typed.</summary>
public delegate string AskDrPepperField(DrPepperUnknownField field);
