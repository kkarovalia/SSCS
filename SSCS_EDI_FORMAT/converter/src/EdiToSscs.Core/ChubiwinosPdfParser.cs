using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// One product row of a Chubiwinos invoice, after the arithmetic has been settled but before the
/// operator has supplied anything. <see cref="Gtin"/> is null when the scan destroyed the barcode
/// and the catalogue does not hold the item — the only case where a person has to be asked.
/// </summary>
public sealed class ChubiwinosLine
{
    /// <summary>1-based position among the invoice's product rows, so a prompt can say which one.</summary>
    public required int Position { get; init; }

    /// <summary>Chubiwinos's own three-digit product number, the first column of the row.</summary>
    public required string ItemNumber { get; init; }

    public required string Description { get; init; }

    /// <summary>Individual units. Negative on a DUMP SECTION row (goods that went back on the truck).</summary>
    public required int Units { get; init; }

    /// <summary>Cost of one unit, always positive.</summary>
    public required decimal UnitCost { get; init; }

    public required decimal RetailPrice { get; init; }

    /// <summary>
    /// The barcode to send, or null when neither the scan nor the catalogue could supply a
    /// trustworthy one and the operator must type it.
    /// </summary>
    public required string? Gtin { get; init; }

    /// <summary>What the barcode line under this row actually read, shown when asking for a retype.</summary>
    public required string PrintedCode { get; init; }

    public bool NeedsBarcode => Gtin is null;

    /// <summary>What SSCS will show as this line's Ex Cost.</summary>
    public decimal ExtendedCost => decimal.Round(Units * UnitCost, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A parsed Chubiwinos invoice, before the operator types its invoice number. The number is never
/// taken from the scan — see <see cref="ChubiwinosPdfParser"/>.
/// </summary>
public sealed class ChubiwinosTicket
{
    /// <summary>The printed date, or null when the scan destroyed it and the operator must key one.</summary>
    public required DateOnly? Date { get; init; }

    /// <summary>The printed invoice total (NET AMOUNT / TOTAL DUE / INVOICE BALANCE, majority vote).</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<ChubiwinosLine> Lines { get; init; }

    /// <summary>
    /// Builds the final invoice. <paramref name="date"/> is only consulted when the ticket carried
    /// no readable date of its own — the paper always wins. <paramref name="resolveGtin"/> supplies
    /// a barcode for the rows that need one; it is never called for a row that already has one.
    /// </summary>
    public Invoice ToInvoice(string invoiceNumber, DateOnly date, Func<ChubiwinosLine, string> resolveGtin)
    {
        ArgumentNullException.ThrowIfNull(resolveGtin);
        return new Invoice
        {
            SupplierName = ChubiwinosPdfParser.SupplierName,
            InvoiceNumber = invoiceNumber,
            // The date the operator confirmed. It is typed on every invoice now, with what the scan
            // read offered in the box, because a plausible wrong date is what this scanner produces
            // and nothing in the file can catch one - see InvoiceDetailsRequest.NeedsDate.
            Date = date,
            Total = Total,
            Lines = Lines.Select(l => new InvoiceLine
            {
                Gtin = l.Gtin ?? resolveGtin(l),
                Description = l.Description,
                Units = l.Units,
                UnitCost = l.UnitCost,
                RetailPrice = l.RetailPrice,
            }).ToList(),
        };
    }
}

/// <summary>
/// Parses a CHUBIWINOS S&amp;G INC. invoice PDF into a <see cref="ChubiwinosTicket"/>. Rules and the
/// sample evidence behind them are documented in analysis/vendors/CHUBIWINOS.md.
///
/// Layout — each product is two visual lines, the row and then its barcode:
///
///   220 CIIETTO COLMILLO -100 g          20     $2.79    $55.80
///   7-500478-044207
///
/// which is item number, description, QTY, unit price, extended. Rows sit under a SALES SECTION
/// banner and, when goods went back on the truck, a DUMP SECTION whose money is printed in
/// parentheses. Each section closes with its own "Totals: &lt;units&gt; $&lt;amount&gt;" row.
///
/// QTY is already individual units and PRICE is already the cost of one unit — verified against
/// live SSCS, which holds invoice 6V9389's first line as Pack Size 1, Qty 20.00, Ea Cost 2.79 — so
/// EXTENDED = QTY x PRICE and no case arithmetic is needed.
///
/// These are scans with an OCR text layer and the damage is real but *localised*: across the eight
/// samples the QTY column was never once wrong, while six rows had a mangled price or extended
/// ("$03.70" for $83.70, "$169" for $3.89), one invoice's SALES line read $446.44 for $448.44, and
/// seven of thirty-five barcodes failed their check digit. Nothing here is trusted on its own:
///
///   * the grand total is the majority of the three places it is printed;
///   * a row whose QTY x PRICE does not equal its EXTENDED is resolved by which reading makes the
///     invoice add up (<see cref="Resolve"/>), never by preferring one column over the other;
///   * the printed "Totals:" unit count is a hard gate on the quantities;
///   * a barcode that fails its check digit is replaced from <see cref="ChubiwinosCatalog"/>, and
///     if the item is not in there the operator is asked to type it.
///
/// The invoice number is the one field with no arithmetic behind it, and the scan mangled two of
/// the eight samples ("0V9293", "6)9141") with nothing to catch it, so it is always typed in.
/// </summary>
public static partial class ChubiwinosPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. Matches the Vendor ID the store already uses on this
    /// vendor's paidouts, so SSCS's "Map Supplier to Vendor" screen needs setting up only once.
    /// </summary>
    public const string SupplierName = "CHUBIWINOS";

    /// <summary>
    /// Margin used to seed the each-list price of an item SSCS has never seen. The invoice prints no
    /// retail column at all, and 50% is what this store actually runs on these lines: SSCS shows Ea
    /// List 5.59 against Ea Cost 2.79 on the COLMILLOS line of invoice 6V9389, which is exactly what
    /// <see cref="RetailSeed.FromCost(decimal, decimal)"/> produces at this margin.
    /// </summary>
    public const decimal RetailMargin = 0.50m;

    /// <summary>How far past a product row to look for its barcode, over any wrapped description.</summary>
    private const int BarcodeSearchDepth = 3;

    /// <summary>
    /// The vendor's name, which heads every page. Only "WINOS" or "CHUBI" is required of it: the
    /// scans render the whole as "CHUBIWINOS", "CHUBI WINOS", "CHUDIWINOS" and "CHt18IWINOS" across
    /// the samples. It is one of several signals — see <see cref="LooksLikeChubiwinos"/> — because a
    /// scan bad enough to destroy both halves of the name would otherwise lose the invoice.
    /// </summary>
    [GeneratedRegex(@"WINOS|CHUBI", RegexOptions.IgnoreCase)]
    private static partial Regex VendorName();

    /// <summary>"Route #: 2948" — this vendor is a DSD route and always prints one.</summary>
    [GeneratedRegex(@"^\W*R[o0]ute\b", RegexOptions.IgnoreCase)]
    private static partial Regex RouteLine();

    /// <summary>"Created By: Jerry V." — the driver who keyed the ticket.</summary>
    [GeneratedRegex(@"^\W*Created\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedByLine();

    /// <summary>
    /// "Date: 06/15/26 8:54 AM". The scanner reads the slashes as ones in six of the eight samples
    /// ("07103126", "04101/26"), so either character is accepted; the clock time that follows is what
    /// makes the shape unambiguous, since it appears on no other line. The label itself is not
    /// matched — it reads "Dale:" and "Bate:" on two of the samples.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<m>\d{2})[/1lI|](?<d>\d{2})[/1lI|](?<y>\d{2})\s+\d{1,2}[:;.]\d{2}\s*[AP]", RegexOptions.IgnoreCase)]
    private static partial Regex DateLine();

    /// <summary>
    /// A product row: a three-digit item number at the very start of the line, followed by a space.
    /// Leading scan debris is stepped over ("—298 PAKETAXO ..."). The trailing space is what keeps
    /// the store's street address ("17828 FM 1314 RD") and every barcode line out — neither has a
    /// word break after three digits.
    /// </summary>
    [GeneratedRegex(@"^[^\dA-Za-z]*(?<item>\d{3})\s+(?<rest>\S.*)$")]
    private static partial Regex ProductRow();

    /// <summary>A section banner. "SALES SECTION" also scans as "SALESS ECTION", DUMP as "OUMP".</summary>
    [GeneratedRegex(@"\bS\s*E\s*C\s*T\s*I\s*O\s*N\b", RegexOptions.IgnoreCase)]
    private static partial Regex SectionBanner();

    /// <summary>"INVOICE BALANCE:", the last line of the summary band.</summary>
    [GeneratedRegex(@"INV[O0]ICE\s+BALANCE", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceBalanceLine();

    /// <summary>A section's closing "Totals: 206 $623.14" row; the amount is read separately.</summary>
    [GeneratedRegex(@"^[^\dA-Za-z]*T[o0]tals?\s*[:;.,]?\s*(?<qty>\d{1,5})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalsRow();

    /// <summary>
    /// "SALES: $623.14" — the first line of the summary band, and therefore the end of the product
    /// rows. The colon is what separates it from the "SALES SECTION" banner; one sample prints a
    /// semicolon instead.
    /// </summary>
    [GeneratedRegex(@"^[^\dA-Za-z]*SALES\s*[:;.,]", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryStart();

    /// <summary>"NET AMOUNT:" — one sample reads "NET AMWNT", another "MET AMOUNT".</summary>
    [GeneratedRegex(@"^[^\dA-Za-z]*[A-Z]ET\s+AM\w*\s*[:;.,]", RegexOptions.IgnoreCase)]
    private static partial Regex NetAmountLine();

    /// <summary>"TOTAL DUE:" — one sample reads "TOTAL OUE".</summary>
    [GeneratedRegex(@"^[^\dA-Za-z]*T[O0]TAL\s+[DO0]UE\s*[:;.,]", RegexOptions.IgnoreCase)]
    private static partial Regex TotalDueLine();

    /// <summary>"INVOICE BALANCE:". The word BALANCE survived every sample intact.</summary>
    [GeneratedRegex(@"BALANCE\s*[:;.,]", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceLine();

    /// <summary>A money value once the currency sign and scan debris are stripped: "83.70", "50,22".</summary>
    [GeneratedRegex(@"^(?<whole>\d{1,6})[.,:;](?<cents>\d{2})$")]
    private static partial Regex MoneyShape();

    /// <summary>"1,234.56" — a thousands separator, so the comma is not the decimal point.</summary>
    [GeneratedRegex(@"^\d{1,3}(,\d{3})+\.\d{2}$")]
    private static partial Regex ThousandsShape();

    /// <summary>
    /// Parses the invoice. Throws <see cref="EdiConversionException"/> when the result would be
    /// untrustworthy — wrong vendor, no items, quantities that do not add up, or a total that does
    /// not reconcile — and records everything else a person should look at in
    /// <paramref name="warnings"/>.
    /// </summary>
    public static ChubiwinosTicket Parse(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        if (!LooksLikeChubiwinos(lines))
            throw new EdiConversionException(PdfReasonCodes.VendorMismatch,
                "This PDF does not look like a Chubiwinos invoice - none of the things one always has "
                + "(the vendor's name, a Route number, a Created By line, a SALES SECTION heading, a "
                + "Totals row, an INVOICE BALANCE) could be found on it. Check that the right vendor is "
                + "selected in the dropdown. If it really is a Chubiwinos invoice, the scan is too faint "
                + "to read - re-scan the whole page and try again.");

        decimal total = FindTotal(lines, warnings)
            ?? throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not read the total on this Chubiwinos invoice. Re-scan the whole page, "
                + "including NET AMOUNT and TOTAL DUE at the bottom, and try again.");

        // A date that will not read is worth one prompt, not a lost delivery: everything else on the
        // ticket still reconciles against its own printed total, and the operator is being asked for
        // the invoice number anyway.
        DateOnly? date = FindDate(lines);

        var groups = ReadSections(lines, warnings);
        var rows = groups.SelectMany(g => g.Rows).ToList();
        if (rows.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No line items were found on this Chubiwinos invoice. If the PDF is a photo or a scan "
                + "with no searchable text, re-scan it with the scanner's OCR setting turned on.");

        CheckUnitCounts(groups, warnings);
        Resolve(rows, total, warnings);
        CheckSectionAmounts(groups, warnings);

        var items = new List<ChubiwinosLine>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            items.Add(BuildLine(rows[i], i + 1, warnings));

        return new ChubiwinosTicket { Date = date, Total = total, Lines = items };
    }

    /// <summary>
    /// Whether this document is one of this vendor's invoices, decided on **two** of six independent
    /// markers rather than on the vendor's name alone.
    ///
    /// The name alone was the first attempt and it is too brittle: these are photographs of a
    /// thermal ticket, the header is the faintest part of the page, and the samples already render
    /// the name as "CHUDIWINOS" and "CHt18IWINOS". A scan that loses both halves of it would lose
    /// the whole invoice — while everything the parser actually needs (the rows, the section
    /// headings, the totals) sits lower down the page and is perfectly readable.
    ///
    /// Two is enough to be safe. Measured over every PDF fixture in the repo: all eight Chubiwinos
    /// samples carry all six markers, and the Blue Bell invoices carry **none** — no
    /// other vendor prints a route, a driver, sectioned line items or an "INVOICE BALANCE". Pinned by
    /// <c>OnlyChubiwinosInvoicesAreAccepted</c>.
    /// </summary>
    private static bool LooksLikeChubiwinos(IReadOnlyList<string> lines)
    {
        int markers = 0;
        if (lines.Any(l => VendorName().IsMatch(l))) markers++;
        if (lines.Any(l => SectionBanner().IsMatch(l))) markers++;
        if (lines.Any(l => RouteLine().IsMatch(l))) markers++;
        if (lines.Any(l => CreatedByLine().IsMatch(l))) markers++;
        if (lines.Any(l => InvoiceBalanceLine().IsMatch(l))) markers++;
        if (lines.Any(l => TotalsRow().IsMatch(l))) markers++;
        return markers >= 2;
    }

    // ───────────────────────────────────────────────────────────────────── rows

    /// <summary>
    /// One product row as printed. <see cref="Extended"/> is filled in by <see cref="Resolve"/>:
    /// until then the row carries only the readings the scan offered.
    /// </summary>
    private sealed class Row
    {
        public required int LineNo { get; init; }
        public required string ItemNumber { get; init; }
        public required string Description { get; set; }
        /// <summary>Units as printed, already signed for the section.</summary>
        public required int Units { get; init; }
        /// <summary>The distinct extended amounts this row could be, signed. One means it is certain.</summary>
        public required IReadOnlyList<decimal> Candidates { get; init; }
        public required string PrintedCode { get; set; }
        public decimal Extended { get; set; }
        /// <summary>True when the amount was recovered rather than read straight off the paper.</summary>
        public bool Repaired { get; set; }
    }

    /// <summary>The rows of one printed section, with the "Totals:" row that closes it.</summary>
    private sealed class Group
    {
        public required bool IsCredit { get; init; }
        public List<Row> Rows { get; } = [];
        public int? PrintedUnits { get; set; }
        public decimal? PrintedAmount { get; set; }
    }

    /// <summary>
    /// Walks the invoice, collecting product rows into the section they were printed under and
    /// stopping at the summary band. A wrapped description ("SAL- 602", "228g") and the barcode
    /// underneath each row are attached to the row above rather than treated as lines of their own.
    /// </summary>
    private static List<Group> ReadSections(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        var groups = new List<Group>();
        var current = new Group { IsCredit = false };
        bool inCredit = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string s = lines[i].Trim();
            if (s.Length == 0) continue;
            if (SummaryStart().IsMatch(s)) break;

            if (SectionBanner().IsMatch(s))
            {
                // "DUMP SECTION" scans as "OUMP SECTION"; the three letters that survive are UMP.
                bool credit = s.Contains("UMP", StringComparison.OrdinalIgnoreCase);
                if (credit != inCredit)
                {
                    if (current.Rows.Count > 0 || current.PrintedUnits is not null) groups.Add(current);
                    current = new Group { IsCredit = credit };
                    inCredit = credit;
                }
                continue;
            }

            var tr = TotalsRow().Match(s);
            if (tr.Success)
            {
                current.PrintedUnits = int.Parse(tr.Groups["qty"].Value, CultureInfo.InvariantCulture);
                current.PrintedAmount = LastMoney(s);
                groups.Add(current);
                current = new Group { IsCredit = inCredit };
                continue;
            }

            if (ReadRow(s, i + 1, inCredit, warnings) is not Row row)
            {
                // Not a row of its own. A long product name wraps ("900 WIPAUTS PAPAS CASERAS CON"
                // / "SAL- 602"), and the barcode always sits under the row it belongs to.
                Attach(current.Rows, s);
                continue;
            }

            current.Rows.Add(row);
        }

        if (current.Rows.Count > 0 || current.PrintedUnits is not null) groups.Add(current);

        // The barcode line is the first thing under a row that is all digits and punctuation; a
        // wrapped description reaches the same place, so both are handled by Attach above.
        return groups.Where(g => g.Rows.Count > 0 || g.PrintedUnits is not null).ToList();
    }

    /// <summary>
    /// Reads one product row, or returns null when the line is not one. QTY, PRICE and EXTENDED are
    /// found by scanning from the right: the last two money tokens are PRICE and EXTENDED, and QTY is
    /// the last plain integer before them. Scanning right-to-left is what survives the numbers inside
    /// a description ("CHEETOS BOLITA -110 g 30 $2.79 $83.70" has three of them before the columns
    /// even start).
    /// </summary>
    private static Row? ReadRow(string s, int lineNo, bool inCredit, List<ConversionWarning> warnings)
    {
        var pm = ProductRow().Match(s);
        if (!pm.Success) return null;

        var tokens = pm.Groups["rest"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // The money columns, right-hand end first.
        var money = new List<(int Index, decimal Value, bool Parenthesized)>();
        for (int i = tokens.Length - 1; i >= 0 && money.Count < 2; i--)
            if (TryMoney(tokens[i], out decimal v, out bool paren))
                money.Insert(0, (i, v, paren));

        if (money.Count == 0) return null;   // a wrapped description, or the barcode line

        int firstMoney = money[0].Index;
        int? units = null;
        int qtyIndex = firstMoney;
        for (int i = firstMoney - 1; i >= 0; i--)
        {
            if (!TryCount(tokens[i], out int v)) continue;
            units = v;
            qtyIndex = i;
            break;
        }

        if (units is null || units == 0)
        {
            // Money where a product row should be but no quantity to divide it by. Saying so beats
            // dropping it silently; the total check would refuse the file anyway, without the line.
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.LineMathMismatch,
                $"Line {lineNo} looks like a product row but no quantity could be read from it, so it "
                + $"was skipped: \"{s}\". Please check it against the paper."));
            return null;
        }

        // The sign lives in the DUMP SECTION banner and in the parentheses Chubiwinos prints round a
        // credit's money. They agree on every sample; if a scan ever loses one, the banner wins,
        // because a missing bracket is far likelier than a missing section heading.
        bool credit = inCredit || money.Any(m => m.Parenthesized);
        if (credit != inCredit)
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.LineMathMismatch,
                $"Line {lineNo} prints its amounts in brackets but is not under the DUMP SECTION heading. "
                + "It was booked as a credit. Please check this line against the paper."));

        int sign = credit ? -1 : 1;
        int signedUnits = sign * Math.Abs(units.Value);

        // What this row's extended amount could be. Two money columns that multiply out leave one
        // answer; anything else leaves the invoice total to choose (see Resolve).
        var candidates = new List<decimal>();
        if (money.Count == 2)
        {
            decimal fromPrice = decimal.Round(
                Math.Abs(units.Value) * Math.Abs(money[0].Value), 2, MidpointRounding.AwayFromZero) * sign;
            decimal printed = sign * Math.Abs(money[1].Value);
            candidates.Add(fromPrice);
            if (Math.Abs(printed - fromPrice) > 0.005m) candidates.Add(printed);
        }
        else
        {
            // One column survived. Whether it is the price or the extended is not knowable here —
            // sample 6V9293 lost its price ("$169" for $3.89) and sample 6V9430 lost two extendeds
            // ("$03.70" for $83.70) — so both readings go forward as candidates.
            decimal m = Math.Abs(money[0].Value);
            candidates.Add(sign * m);
            decimal asPrice = decimal.Round(Math.Abs(units.Value) * m, 2, MidpointRounding.AwayFromZero) * sign;
            if (Math.Abs(asPrice - sign * m) > 0.005m) candidates.Add(asPrice);
        }

        return new Row
        {
            LineNo = lineNo,
            ItemNumber = pm.Groups["item"].Value,
            Description = string.Join(" ", tokens[..qtyIndex]).Trim(),
            Units = signedUnits,
            Candidates = candidates,
            PrintedCode = "",
        };
    }

    /// <summary>
    /// Attaches a line that is not a row of its own to the row above it: the barcode when it is all
    /// digits and punctuation, otherwise the tail of a wrapped description.
    /// </summary>
    private static void Attach(List<Row> rows, string s)
    {
        if (rows.Count == 0) return;
        var row = rows[^1];

        // The barcode line. A single stray letter is tolerated because the scanner drops one beside
        // the code often enough to matter ("x 7-501011131040"); a second one means real words, which
        // is a wrapped description rather than a barcode.
        string digits = OnlyDigits(s);
        if (digits.Length >= 10 && !s.Contains('$', StringComparison.Ordinal) && s.Count(char.IsAsciiLetter) <= 1)
        {
            if (row.PrintedCode.Length == 0) row.PrintedCode = digits;
            return;
        }

        // Short fragments of pack sizes and flavours: "228g", "SAL- 602", "CHILE-LIMON 3.53oz". Two
        // characters of content are required so that a lone speckle ("Y", "l") is not glued onto a
        // product name.
        if (s.Length <= 40 && s.Any(char.IsAsciiLetter) && s.Count(char.IsAsciiLetterOrDigit) >= 2)
            row.Description = (row.Description + " " + s).Trim();
    }

    // ─────────────────────────────────────────────────────────────── reconciling

    /// <summary>
    /// Hard gate on the quantities. The unit count is the one figure the money cannot check — a
    /// misread QTY is absorbed by the unit cost, so the invoice still balances to the penny while
    /// the store's stock is wrong — but Chubiwinos prints its own count at the foot of every
    /// section, and it matched on all eight samples.
    /// </summary>
    private static void CheckUnitCounts(List<Group> groups, List<ConversionWarning> warnings)
    {
        foreach (var g in groups)
        {
            if (g.PrintedUnits is not int printed)
            {
                if (g.Rows.Count > 0)
                    warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                        "The \"Totals:\" unit count under one section of this Chubiwinos invoice could not "
                        + "be read, so the quantities could not be checked against it. Please compare the "
                        + "quantities in SSCS against the paper."));
                continue;
            }

            int counted = g.Rows.Sum(r => Math.Abs(r.Units));
            if (counted != printed)
                throw new EdiConversionException(PdfReasonCodes.QtyMismatch,
                    $"The quantities on this Chubiwinos invoice add up to {counted} units but the printed "
                    + $"\"Totals: {printed}\" says {printed}. A quantity was misread, which would put the "
                    + "wrong stock into SSCS even though the money balances. Please re-scan the page and try again.");
        }
    }

    /// <summary>
    /// Settles every row's extended amount against the printed invoice total.
    ///
    /// Rows whose two money columns multiply out are certain and are simply added up. What is left
    /// over has to be covered by the rows that do not, and there are never many: two at most across
    /// the eight samples. Their possible readings are tried in combination, and the file is only
    /// written when exactly one combination reproduces the invoice's own total — so a repair is
    /// arithmetic, not a preference for one column over another.
    ///
    /// When no combination fits and exactly one row is unsettled, that row is given the leftover
    /// directly: sample 6V9244 lost both of its columns on one line ("$2.19 $63.70" for $2.79 and
    /// $83.70) and the leftover, $83.70 over 30 units, comes back to the printed $2.79 exactly.
    /// </summary>
    private static void Resolve(List<Row> rows, decimal total, List<ConversionWarning> warnings)
    {
        var unsettled = new List<Row>();
        decimal certain = 0m;
        foreach (var r in rows)
        {
            if (r.Candidates.Count == 1) { r.Extended = r.Candidates[0]; certain += r.Extended; }
            else unsettled.Add(r);
        }

        if (unsettled.Count == 0)
        {
            RequireReconciliation(rows, total);
            return;
        }

        if (unsettled.Count > 8)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"{unsettled.Count} lines on this Chubiwinos invoice have a price and an extended amount that "
                + "do not multiply out, which is far more scan damage than can be reconciled safely. "
                + "Please re-scan the invoice and try again.");

        decimal residual = total - certain;
        var solutions = new List<decimal[]>();
        Search(unsettled, 0, 0m, residual, new decimal[unsettled.Count], solutions);

        if (solutions.Count == 1)
        {
            for (int i = 0; i < unsettled.Count; i++)
            {
                unsettled[i].Extended = solutions[0][i];
                unsettled[i].Repaired = true;
            }
        }
        else if (solutions.Count == 0 && unsettled.Count == 1 && IsPlausibleAmount(unsettled[0], residual))
        {
            unsettled[0].Extended = residual;
            unsettled[0].Repaired = true;
        }
        else
        {
            string where = string.Join(", ", unsettled.Select(r => $"line {r.LineNo}"));
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"The scan damaged the price or amount on {where} of this Chubiwinos invoice, and no reading "
                + $"of them adds up to the printed total of {total:0.00}. Emitting a guess would book the wrong "
                + "money, so the file was not written. Please re-scan the page and try again.");
        }

        foreach (var r in unsettled)
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.LineMathMismatch,
                $"Line {r.LineNo} (\"{r.Description}\"): the scan damaged this row's price or amount. It was "
                + $"recovered as {Math.Abs(r.Units)} x {UnitCostOf(r):0.0000} = {r.Extended:0.00}, which is the only "
                + $"reading that makes this invoice add up to {total:0.00}. Please check this line in SSCS."));

        RequireReconciliation(rows, total);
    }

    /// <summary>
    /// Tries every combination of the unsettled rows' possible amounts, keeping the ones that hit the
    /// leftover. Stops at two solutions: a second one means the damage is genuinely ambiguous and the
    /// invoice has to be refused rather than guessed at.
    /// </summary>
    private static void Search(
        List<Row> rows, int index, decimal running, decimal target, decimal[] pick, List<decimal[]> found)
    {
        if (found.Count > 1) return;
        if (index == rows.Count)
        {
            if (Math.Abs(running - target) <= 0.005m) found.Add((decimal[])pick.Clone());
            return;
        }

        foreach (decimal c in rows[index].Candidates)
        {
            pick[index] = c;
            Search(rows, index + 1, running + c, target, pick, found);
            if (found.Count > 1) return;
        }
    }

    /// <summary>
    /// Whether the leftover can be this row's amount: it must have the row's sign, divide by the
    /// row's units into a cost that is a whole number of cents, and be a price a snack could carry.
    /// </summary>
    private static bool IsPlausibleAmount(Row row, decimal residual)
    {
        if (row.Units == 0) return false;
        if (Math.Sign(residual) != Math.Sign(row.Units)) return false;

        decimal cost = Math.Abs(residual) / Math.Abs(row.Units);
        if (cost is <= 0m or > 100m) return false;
        return Math.Abs(decimal.Round(cost, 2, MidpointRounding.AwayFromZero) - cost) <= 0.0005m;
    }

    /// <summary>
    /// The hard checksum every parser owes: what the lines add up to must be the invoice's own
    /// printed total, give or take the cent per line SSCS itself rounds away.
    /// </summary>
    private static void RequireReconciliation(List<Row> rows, decimal total)
    {
        decimal sum = rows.Sum(r => r.Extended);
        if (Math.Abs(sum - total) > rows.Count * 0.01m)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"The line items add up to {sum:0.00} but this Chubiwinos invoice's total is {total:0.00}. "
                + "A line may be missing or misread. Please re-scan the whole page and try again.");
    }

    /// <summary>
    /// Checks each section against its own printed "Totals:" amount. It cannot put a wrong figure
    /// into the file — the grand total already proved the lines — but a section that does not add up
    /// says which half of the page to look at.
    /// </summary>
    private static void CheckSectionAmounts(List<Group> groups, List<ConversionWarning> warnings)
    {
        foreach (var g in groups)
        {
            if (g.PrintedAmount is not decimal printed || g.Rows.Count == 0) continue;
            decimal sum = g.Rows.Sum(r => r.Extended);
            if (Math.Abs(Math.Abs(sum) - Math.Abs(printed)) > g.Rows.Count * 0.01m)
                warnings.Add(new ConversionWarning(g.Rows[0].LineNo, PdfReasonCodes.LineMathMismatch,
                    $"The {(g.IsCredit ? "DUMP" : "SALES")} section of this Chubiwinos invoice adds up to "
                    + $"{Math.Abs(sum):0.00} but its printed \"Totals\" row says {Math.Abs(printed):0.00}. The "
                    + "invoice still balances overall, so the file was written - please check this section "
                    + "against the paper."));
        }
    }

    // ─────────────────────────────────────────────────────────────────── output

    /// <summary>
    /// Turns a settled row into a line: units signed, cost always positive, retail seeded, and the
    /// barcode decided.
    /// </summary>
    private static ChubiwinosLine BuildLine(Row row, int position, List<ConversionWarning> warnings)
    {
        decimal cost = UnitCostOf(row);
        return new ChubiwinosLine
        {
            Position = position,
            ItemNumber = row.ItemNumber,
            Description = row.Description,
            Units = row.Units,
            UnitCost = cost,
            RetailPrice = RetailSeed.FromCost(cost, RetailMargin),
            Gtin = ResolveBarcode(row, position, warnings),
            PrintedCode = row.PrintedCode,
        };
    }

    /// <summary>Cost of one unit, to the four decimals SSCS keeps.</summary>
    private static decimal UnitCostOf(Row row) =>
        row.Units == 0 ? 0m : decimal.Round(Math.Abs(row.Extended) / Math.Abs(row.Units), 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Decides the barcode to send, or null when the operator has to type it.
    ///
    /// Chubiwinos prints whole barcodes — 28 of the 35 distinct codes across the samples pass their
    /// own check digit — so a code that passes is sent exactly as printed and SSCS drops the check
    /// digit itself. A code that fails has been damaged by the scan, and every one of the seven
    /// failures was a single wrong digit in a code the catalogue already holds ("1501011151062" for
    /// 7501011151062), so the catalogue replaces it. An item the catalogue has never seen is the one
    /// case nothing can settle, and the operator is asked.
    /// </summary>
    private static string? ResolveBarcode(Row row, int position, List<ConversionWarning> warnings)
    {
        string printed = row.PrintedCode;
        string? known = ChubiwinosCatalog.BarcodeFor(row.ItemNumber);

        if (Upc.IsValidGtin(printed))
        {
            // A valid check digit outranks the catalogue: it is arithmetic on the code itself, where
            // the catalogue is only a record of what this vendor printed in the past.
            if (known is not null && known != printed)
                warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
                    $"Line {row.LineNo} (\"{row.Description}\"): item {row.ItemNumber} normally carries barcode "
                    + $"{known} but this invoice prints {printed}, which is a valid barcode in its own right. It "
                    + "was sent as printed. If the vendor has changed the code, the built-in list needs updating."));
            return printed;
        }

        if (known is not null)
        {
            warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
                $"Line {row.LineNo} (\"{row.Description}\"): the barcode read as "
                + $"{(printed.Length == 0 ? "unreadable" : printed)}, which is not a valid barcode. Item "
                + $"{row.ItemNumber} was sent under {known}, the code this vendor prints for it. Please check "
                + "this line in SSCS."));
            return known;
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────── header

    /// <summary>
    /// The invoice total, taken as the majority of the three places Chubiwinos prints it — NET
    /// AMOUNT, TOTAL DUE and INVOICE BALANCE. Sample 6V9141 reads "MET AMOUNT: $684.82" against a
    /// real $584.82, and the other two outvote it. Anything less than agreement is reported.
    /// </summary>
    private static decimal? FindTotal(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        var votes = new List<decimal>();
        foreach (string line in lines)
        {
            if (NetAmountLine().IsMatch(line) || TotalDueLine().IsMatch(line) || BalanceLine().IsMatch(line))
                if (LastMoney(line) is decimal v)
                    votes.Add(v);
        }

        if (votes.Count == 0) return null;

        var best = votes.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => Math.Abs(g.Key)).First();
        if (best.Count() < votes.Count)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"This Chubiwinos invoice prints its total as {string.Join(", ", votes.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)).Distinct())} "
                + $"in different places, so the scan damaged at least one of them. {best.Key:0.00} was used, "
                + "being what most of them say, and the line items add up to it. Please check the total in SSCS."));

        // A single reading with nothing to agree with it is not a majority of anything.
        if (votes.Count == 1)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                "Only one of this Chubiwinos invoice's three printed totals could be read. The line items "
                + "add up to it, so the file was written - please check the total in SSCS."));

        return best.Key;
    }

    private static DateOnly? FindDate(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = DateLine().Match(line);
            if (!m.Success) continue;

            int month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            int day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int year = 2000 + int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12) continue;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) continue;
            return new DateOnly(year, month, day);
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────── scanning

    /// <summary>The rightmost money value on a line, used for the "Totals:" and summary rows.</summary>
    private static decimal? LastMoney(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = tokens.Length - 1; i >= 0; i--)
            if (TryMoney(tokens[i], out decimal v, out _))
                return v;
        return null;
    }

    /// <summary>
    /// A money token, negative when Chubiwinos bracketed it. The currency sign and whatever the
    /// scanner sprayed around it are discarded ("\"$452.90", ".$83.70"), a decimal point read as a
    /// comma or colon is accepted ("$50,22"), and the two decimal places are required — that is what
    /// keeps loose numbers inside a description ("-180 g", "$169") out of the money columns.
    /// </summary>
    private static bool TryMoney(string token, out decimal value, out bool parenthesized)
    {
        value = 0m;
        parenthesized = token.Contains('(', StringComparison.Ordinal) || token.Contains(')', StringComparison.Ordinal);
        if (token.Any(char.IsAsciiLetter)) return false;

        var kept = new StringBuilder(token.Length);
        foreach (char c in token)
            if (char.IsAsciiDigit(c) || c is '.' or ',' or ':' or ';') kept.Append(c);

        string t = kept.ToString().Trim('.', ',', ':', ';');
        if (ThousandsShape().IsMatch(t)) t = t.Replace(",", "", StringComparison.Ordinal);

        var m = MoneyShape().Match(t);
        if (!m.Success) return false;

        value = decimal.Parse(
            m.Groups["whole"].Value + "." + m.Groups["cents"].Value, CultureInfo.InvariantCulture);
        if (parenthesized || token.StartsWith('-')) value = -value;
        return true;
    }

    /// <summary>
    /// A plain unit count. Currency and letters disqualify a token, so a damaged price ("$169") is
    /// never mistaken for a quantity, and the scanner's stray leading dash is trimmed ("-30").
    /// </summary>
    private static bool TryCount(string token, out int value)
    {
        value = 0;
        if (token.Contains('$', StringComparison.Ordinal) || token.Any(char.IsAsciiLetter)) return false;

        string t = token.Trim('-', '.', ',', ':', ';', '"', '\'', '(', ')', '~', '*', '_', '—', '–', '|');
        if (t.Length is 0 or > 4 || !t.All(char.IsAsciiDigit)) return false;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string OnlyDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsAsciiDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
