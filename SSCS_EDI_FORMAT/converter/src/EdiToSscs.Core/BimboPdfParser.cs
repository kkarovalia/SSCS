using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// A parsed Bimbo delivery ticket, before the operator types its invoice number.
///
/// A ticket's DELIVERIES and RETURNS sections are **one** payable, not two — unlike a Blue Bell
/// ticket. Blue Bell prints a separate invoice number on its Returns block too, and SSCS books that
/// pair as one payable as well - a printed number identifies a document, not a payable. Verified against live SSCS
/// on the 07-28-2026 ticket: all 36 lines, deliveries and returns together, booked as a single
/// paidout of $335.07 with the returns sitting among the rest as negative quantities. So this is
/// one <see cref="Invoice"/>, and returns are negative lines inside it.
/// </summary>
public sealed class BimboTicket
{
    /// <summary>The printed date, or null when the scan destroyed it and the operator must key one.</summary>
    public required DateOnly? Date { get; init; }

    /// <summary>
    /// A date recovered from a scan whose separators came back as digits ("711312026" for 7/13/2026).
    /// Only ever offered to the operator as a suggestion to check against the paper — never used on
    /// its own, because the recovery is arithmetic on a damaged reading rather than a reading.
    /// </summary>
    public DateOnly? SuggestedDate { get; init; }

    /// <summary>The ticket's own TICKET TOTALS wholesale figure. Negative if returns outweighed the drop.</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>
    /// Builds the final invoice. <paramref name="date"/> is consulted only when the ticket carried no
    /// readable date of its own — the paper always wins.
    /// </summary>
    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = BimboPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = Date ?? date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Parses a BIMBO BAKERIES USA, INC. - CASH STOPS delivery ticket into a <see cref="BimboTicket"/>.
/// Row shapes and the sample evidence are documented in analysis/vendors/BIMBO.md.
///
/// Layout — one visual line per product, under a DELIVERIES banner and then a RETURNS banner:
///
///   UPC        ITEM No  DESCRIPTION            QTY  SUGG RETAIL  RETAIL AMOUNT  PRICE   AMOUNT
///   74323-09183 008788  BIM PANQUE NUE FS FC     3         3.19           9.57  3.0200    9.06
///
/// Each section closes with its own "DELIVERY TOTAL / RETURNS TOTAL &lt;units&gt; &lt;retail&gt;
/// &lt;wholesale&gt;" row, and the ticket closes with "TICKET TOTALS" and "CASH DUE".
///
/// WHAT MAPS TO WHAT (verified against live SSCS on the 07-28-2026 ticket, all 26 delivery lines):
///   * QTY is already **individual sellable units** — "SS2P" is a single-serve two-pack, one thing
///     the store sells — so there is no case arithmetic anywhere on this vendor. SSCS's Qty column
///     matched the printed QTY on 26 of 26 lines.
///   * WHOLESALE PRICE is already the cost of **one unit**, printed to four decimals, and is what
///     SSCS shows as Ea Cost. The SUGG RETAIL column is Bimbo's suggested shelf price and is *not*
///     what this store charges (Bimbo suggests 1.99 where the store rings 3.59), so it is not used
///     — see <see cref="RetailMargin"/>.
///   * The invoice total is TICKET TOTALS' wholesale figure, which is CASH DUE when PRIOR BALANCE
///     is zero. CASH DUE carries the prior balance too, so it is only a cross-check.
///
/// THREE INDEPENDENT CHECKSUMS, which is unusually generous and is what makes the badly damaged
/// scans usable at all:
///   1. per row, QTY x PRICE = AMOUNT;
///   2. per section, the printed unit count and the printed wholesale amount;
///   3. the ticket's TICKET TOTALS, cross-checked against CASH DUE.
/// Nothing is trusted on its own. The quantity is settled by whichever of the three readings
/// available for it agree (<see cref="ResolveQty"/>), and the extended amount is then *computed*
/// from qty x cost rather than read, because that is what SSCS itself computes — the printed AMOUNT
/// becomes one more thing to check rather than a figure to copy.
///
/// THE BARCODES CANNOT CHECK THEMSELVES. Bimbo prints the UPC with its check digit removed and its
/// leading zero dropped, so a single mis-read digit is silent — the money still balances and SSCS
/// opens a new item under an id belonging to nothing. Two rules cover it: the five-digit
/// manufacturer prefix must be one Bimbo actually uses, and the item number column is cross-checked
/// against <see cref="BimboCatalog"/>. Across the 170 sample rows that catches all ten mis-reads.
/// </summary>
public static partial class BimboPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. Matches the Vendor ID the store already has on this
    /// vendor's paidouts ("BIMBO", against the name BIMBO BAKERIES USA, INC), so SSCS's "Map Supplier
    /// to Vendor" screen needs no new setup.
    /// </summary>
    public const string SupplierName = "BIMBO";

    /// <summary>
    /// Margin used to seed the each-list price of an item SSCS has never seen.
    ///
    /// Bimbo *does* print a SUGG RETAIL column, but it is not this store's shelf price and using it
    /// would be actively harmful: Bimbo suggests 1.99 against a 1.94 cost — a 2.5% margin — where the
    /// SSCS Line Items screen shows the store ringing that item at 3.59. A new item seeded from the
    /// printed column would sit a nickel above cost until somebody noticed.
    ///
    /// 45% reproduces the store's own figure exactly on its most common line (1.94 -> 3.59, which is
    /// what <see cref="RetailSeed.FromCost(decimal, decimal)"/> returns once its round-up-to-a-9 rule
    /// is applied) and lands *under* the real price on the rest — 3.02 -> 5.59 against a real 5.99,
    /// 2.59 -> 4.79 against a real 4.99. Under is the safe direction: staff correct a new item's
    /// price, and an under-priced item loses a little margin where an over-priced one overcharges a
    /// customer.
    /// </summary>
    public const decimal RetailMargin = 0.45m;

    /// <summary>
    /// The five-digit manufacturer prefixes Bimbo bills this store under: Bimbo/Marinela (74323),
    /// Mrs Baird's (70870) and Gold'n (71852). Measured over the nine samples, 164 of 170 rows carry
    /// one of these and the six that do not are all a single digit away from one of them ("14323",
    /// "70810" — the scanner reading a 7 as a 1), which is what makes <see cref="RepairPrefix"/>
    /// safe. A genuinely new prefix is possible, so an unrecognised one warns rather than refuses.
    /// </summary>
    private static readonly string[] KnownPrefixes = ["74323", "70870", "71852"];

    /// <summary>How many numeric columns trail a product row: QTY, SUGG, RETAIL AMT, PRICE, AMOUNT.</summary>
    private const int TrailingColumns = 5;

    /// <summary>A cent of drift per line is tolerable; beyond that the parse is not trustworthy.</summary>
    private const decimal CentPerLine = 0.01m;

    // ────────────────────────────────────────────────────────────────────── shapes

    /// <summary>
    /// "BIMBO BAKERIES USA, INC. - CASH STOPS". The samples render the name itself as "BIM 1",
    /// "BIHBO", "81100", "HINDU" and "BII1B0", so the name is never required — BAKERIES and CASH
    /// STOPS both survived every sample intact.
    /// </summary>
    [GeneratedRegex(@"BAKERIES|CASH\s*ST[O0]PS", RegexOptions.IgnoreCase)]
    private static partial Regex VendorName();

    /// <summary>
    /// The "**DELIVERIES**" banner. Printed letter-spaced, so the scans return "DEL I VER IES",
    /// "DELI VERIES" and "OELIVERJES" — every letter may be followed by a space and the leading D
    /// may be an O.
    /// </summary>
    [GeneratedRegex(@"[DO0]\s*E\s*L\s*I?\s*V\s*E\s*R", RegexOptions.IgnoreCase)]
    private static partial Regex DeliveriesBanner();

    /// <summary>The "**RETURNS**" banner, equally letter-spaced ("R E T U R N S").</summary>
    [GeneratedRegex(@"R\s*E\s*T\s*U\s*R\s*N", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnsBanner();

    /// <summary>"CASH DUE" — the samples also read "CASH ODE", "CASH OUE" and "CASH UUE".</summary>
    [GeneratedRegex(@"CASH\s+[DOU0]", RegexOptions.IgnoreCase)]
    private static partial Regex CashDueLine();

    /// <summary>The column header band, which names the two money columns.</summary>
    [GeneratedRegex(@"WH[O0]LESALE|SUGG", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnHeader();

    /// <summary>
    /// A section's closing total row: a label ending in TOTAL/TOTALS with no UPC in front of it.
    /// "DELIVERY TOTAL" also scans as "OELIUEAY TOTAL" and "TICKET TOTALS" as "TIIZET TOTALS", so
    /// only the word TOTAL is matched and the label is classified separately by its first letter.
    /// </summary>
    [GeneratedRegex(@"^(?<label>[^\d]*?)T[O0]TALS?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRow();

    /// <summary>The digits a product row opens with: ten of UPC and six of item number.</summary>
    private const int CodeDigits = 16;

    /// <summary>
    /// One numeric token. The decimal point scans as a comma, colon or semicolon ("3,19", "1:99")
    /// and sometimes doubles ("7..76"); a trailing minus is scan debris ("1.400-"). The sign is not
    /// taken from here — it comes from which section the row is in.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<whole>\d{1,6})(?:[.,:;']+(?<frac>\d{1,4}))?(?!\d)")]
    private static partial Regex NumberToken();

    /// <summary>
    /// The whole "SOD:" date token, whatever its separators became, anchored on the clock time that
    /// follows it — no other line on the ticket carries a date beside a time. The token is taken
    /// unbroken (never mid-way through) because a run of digits starting halfway along a date reads
    /// as a perfectly plausible different date: "6/2012026" scanned from 06-20-2026 offers "2012026",
    /// which reconstructs cleanly and wrongly as 02-01-2026.
    /// </summary>
    [GeneratedRegex(@"(?<![^\s])(?<tok>\d\S{5,11})\s+\d{1,2}[:;.]\d{2}")]
    private static partial Regex DateToken();

    // ────────────────────────────────────────────────────────────────────── parse

    /// <summary>
    /// Parses the ticket. Throws <see cref="EdiConversionException"/> when the result would be
    /// untrustworthy — wrong vendor, no items, quantities or money that do not reconcile against the
    /// ticket's own printed figures — and records everything else a person should look at in
    /// <paramref name="warnings"/>.
    /// </summary>
    public static BimboTicket Parse(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        if (!LooksLikeBimbo(lines))
            throw new EdiConversionException(PdfReasonCodes.VendorMismatch,
                "This PDF does not look like a Bimbo delivery ticket - none of the things one always "
                + "has (BAKERIES or CASH STOPS in the heading, a DELIVERIES or RETURNS banner, a "
                + "DELIVERY TOTAL row, a CASH DUE line) could be found on it. Check that the right "
                + "vendor is selected in the dropdown. If it really is a Bimbo ticket, the scan is too "
                + "faint to read - re-scan the whole page and try again.");

        var sections = ReadSections(lines, warnings);
        var rows = sections.SelectMany(s => s.Rows).ToList();
        if (rows.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No line items were found on this Bimbo ticket. If the PDF is a photo or a scan with "
                + "no searchable text, re-scan it with the scanner's OCR setting turned on.");

        var (ticketUnits, ticketAmount) = ReadTicketTotals(lines);
        CheckUnits(sections, rows, ticketUnits, warnings);
        CheckSectionAmounts(sections, warnings);

        decimal lineTotal = rows.Sum(r => r.Extended);
        decimal total = CheckTicketTotal(ticketAmount, lineTotal, rows.Count, warnings);

        var (date, suggested) = FindDate(lines);
        var items = rows.Select(r => BuildLine(r, warnings)).ToList();

        int credits = items.Count(l => l.Units < 0);
        if (credits > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {credits} return line{(credits == 1 ? "" : "s")} that went back on the "
                + "truck. They are sent as negative quantities on the same invoice, which is how the "
                + "ticket's own total is worked out."));

        return new BimboTicket { Date = date, SuggestedDate = suggested, Total = total, Lines = items };
    }

    /// <summary>
    /// Whether this document is one of this vendor's tickets, decided on two of five independent
    /// markers rather than on the vendor's name alone. These are photographs of a thermal ticket and
    /// the heading is the faintest part of the page — the samples already render the name as "81100
    /// BAKERIES" and "HINDU BAKERIES" — while everything the parser needs sits lower down and reads
    /// perfectly well.
    /// </summary>
    private static bool LooksLikeBimbo(IReadOnlyList<string> lines)
    {
        int markers = 0;
        if (lines.Any(l => VendorName().IsMatch(l))) markers++;
        if (lines.Any(l => DeliveriesBanner().IsMatch(l))) markers++;
        if (lines.Any(l => ReturnsBanner().IsMatch(l))) markers++;
        if (lines.Any(l => CashDueLine().IsMatch(l))) markers++;
        if (lines.Any(l => ColumnHeader().IsMatch(l))) markers++;
        return markers >= 2;
    }

    // ────────────────────────────────────────────────────────────────────── sections

    /// <summary>Which printed band a row came from, which is what gives its quantity its sign.</summary>
    private enum Band { Deliveries, Returns, Ticket }

    private sealed class Row
    {
        public required int LineNo { get; init; }
        public required string Upc10 { get; init; }
        public required string ItemNumber { get; init; }
        public required string Description { get; init; }
        /// <summary>Signed units: negative for a row in the RETURNS band.</summary>
        public required int Units { get; init; }
        /// <summary>Cost of one unit, always positive, four decimals.</summary>
        public required decimal UnitCost { get; init; }
        /// <summary>Computed as units x cost, the way SSCS computes it — not the printed AMOUNT.</summary>
        public decimal Extended => decimal.Round(Units * UnitCost, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The rows of one printed band, with the totals row that closes it.</summary>
    private sealed class Section
    {
        public required Band Band { get; init; }
        public List<Row> Rows { get; } = [];
        /// <summary>The unit count printed on the closing row, signed. Null when it would not read.</summary>
        public int? PrintedUnits { get; set; }
        /// <summary>The wholesale amount printed on the closing row, signed. Null when it would not read.</summary>
        public decimal? PrintedAmount { get; set; }
    }

    /// <summary>
    /// Splits the ticket into its DELIVERIES and RETURNS bands and reads the rows of each.
    ///
    /// The band is decided by the closing total rows rather than by the banners: "**RETURNS**" is
    /// printed letter-spaced and comes back as `"'BFTUIINS•"` on one sample, while the closing
    /// "RETURNS TOTAL" row survived every sample intact. Rows before the first total row are
    /// deliveries, rows between the first and second are returns.
    /// </summary>
    private static List<Section> ReadSections(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        var sections = new List<Section>();
        var current = new Section { Band = Band.Deliveries };
        sections.Add(current);

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];

            if (SplitCode(line) is (string upc10, string item, string rest))
            {
                var row = ReadRow(upc10, item, rest, i + 1, current.Band, warnings);
                if (row is not null) current.Rows.Add(row);
                continue;
            }

            var totals = TotalRow().Match(line);
            if (!totals.Success) continue;

            Band band = ClassifyTotal(totals.Groups["label"].Value);
            if (band == Band.Ticket) break;      // TICKET TOTALS closes the ticket

            ReadSectionTotals(current, line);

            // Deliveries are always printed first, so whatever follows a closing total row is the
            // returns band. A ticket with no returns simply never fills it, and it is dropped below.
            current = new Section { Band = Band.Returns };
            sections.Add(current);
        }

        return sections.Where(s => s.Rows.Count > 0 || s.PrintedUnits is not null).ToList();
    }

    /// <summary>
    /// Which band a total row closes, from the label in front of the word TOTAL. "DELIVERY" scans as
    /// "OELIUEAY" and "TICKET" as "TIIZET", so only the first letter is relied on: D/O for a
    /// delivery, R/B for returns, T for the ticket's own grand total.
    /// </summary>
    private static Band ClassifyTotal(string label)
    {
        string letters = new(label.Where(char.IsLetter).ToArray());
        if (letters.Length == 0) return Band.Ticket;
        return char.ToUpperInvariant(letters[0]) switch
        {
            'D' or 'O' => Band.Deliveries,
            'R' or 'B' => Band.Returns,
            _ => Band.Ticket,
        };
    }

    /// <summary>
    /// Reads the unit count and wholesale amount off a closing total row: "DELIVERY TOTAL 190 413.10
    /// 416.13" is units, retail, wholesale. The wholesale figure is the last of the three.
    /// </summary>
    private static void ReadSectionTotals(Section section, string line)
    {
        var nums = ReadNumbers(line);
        if (nums.Count < 2) return;

        int sign = section.Band == Band.Returns ? -1 : 1;
        // The unit count is the only one printed without a decimal point.
        var units = nums.FirstOrDefault(n => !n.HasFraction);
        if (units is not null && units.Value <= int.MaxValue)
            section.PrintedUnits = sign * (int)units.Value;
        section.PrintedAmount = sign * nums[^1].Value;
    }

    // ────────────────────────────────────────────────────────────────────── rows

    /// <summary>
    /// Reads one product row, or returns null when it carries no money at all.
    ///
    /// A row with no amount is real and must not be treated as damage: example 1's "74323-73965
    /// 968338 MLA BARRITA 35P PPS" prints a bare 1 and no prices whatsoever, and Bimbo's own DELIVERY
    /// TOTAL excludes it from both the unit count (88) and the money ($178.28). Skipping it is
    /// therefore what makes the ticket reconcile, and the section gates prove it: had the row really
    /// carried money, both of them would fail.
    /// </summary>
    private static Row? ReadRow(
        string upc10, string item, string rest, int lineNo, Band band, List<ConversionWarning> warnings)
    {
        var nums = ReadNumbers(rest);
        // AMOUNT and PRICE at minimum; a row with fewer numbers than that carries no money.
        if (nums.Count < 2) return null;

        var tail = nums.TakeLast(TrailingColumns).ToList();
        var amount = tail[^1];
        var price = tail[^2];
        var retailAmount = tail.Count >= 3 ? tail[^3] : null;
        var suggRetail = tail.Count >= 4 ? tail[^4] : null;
        var printedQty = tail.Count >= 5 ? tail[^5] : null;

        // A row whose only numbers are a bare quantity is the no-money case above, not a product.
        if (!amount.HasFraction && !price.HasFraction) return null;

        int? qty = ResolveQty(amount, price, retailAmount, suggRetail, printedQty, lineNo, warnings);
        if (qty is null or 0)
        {
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UnreadableLine,
                $"The quantity on the line for \"{Describe(rest, nums)}\" could not be worked out from "
                + "the scan. Check this line against the paper in SSCS."));
            return null;
        }

        decimal unitCost = ResolveUnitCost(price, amount, qty.Value, lineNo, warnings);
        if (unitCost <= 0m) return null;

        return new Row
        {
            LineNo = lineNo,
            Upc10 = upc10,
            ItemNumber = item,
            Description = Describe(rest, nums),
            Units = band == Band.Returns ? -qty.Value : qty.Value,
            UnitCost = unitCost,
        };
    }

    /// <summary>
    /// Settles a row's quantity from the three readings the row offers for it, in order of how well
    /// each defends itself:
    ///
    ///   1. AMOUNT / PRICE, accepted only when it divides to a whole number — arithmetic on two
    ///      columns, so a damaged digit in either almost always fails to divide;
    ///   2. RETAIL AMOUNT / SUGG RETAIL, the same trick on the other pair of columns;
    ///   3. the printed QTY digit, which defends itself not at all.
    ///
    /// The printed digit is deliberately last. On the 200 dpi scans it is the *least* reliable
    /// column on the row — example 3 lost it entirely on all 22 lines and printed "1" for 12 and for
    /// 16 on two more — while the money columns beside it stayed intact. Every quantity on that
    /// ticket comes back correctly from rule 1 alone, and the section unit count then proves it.
    /// </summary>
    private static int? ResolveQty(
        Number amount, Number price, Number? retailAmount, Number? suggRetail, Number? printedQty,
        int lineNo, List<ConversionWarning> warnings)
    {
        int? fromMoney = DivideExactly(amount.Value, price.Value);
        int? fromRetail = retailAmount is not null && suggRetail is not null
            ? DivideExactly(retailAmount.Value, suggRetail.Value)
            : null;
        int? fromPrint = printedQty is { HasFraction: false } p && p.Value is > 0 and < 10000
            ? (int)p.Value
            : null;

        int? qty = fromMoney ?? fromRetail ?? fromPrint;
        if (qty is null) return null;

        // Two columns that both divide cleanly but disagree means one of them is damaged in a way
        // that happened to stay divisible. Rare, but it is exactly the case worth a person's eyes.
        if (fromMoney is not null && fromRetail is not null && fromMoney != fromRetail)
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.QtyMismatch,
                $"A line's two money columns disagree about its quantity ({fromMoney} from the wholesale "
                + $"columns, {fromRetail} from the retail columns). {fromMoney} was used. Check this line "
                + "against the paper in SSCS."));

        return qty;
    }

    /// <summary>
    /// The cost of one unit. Bimbo prints it to four decimals, which is the most distinctive token on
    /// the row, so a reading that still has four decimals is taken as-is. When the scan destroyed it
    /// ("1.400-" for 1.9400) it is recovered as AMOUNT / QTY, which the section total then proves.
    /// </summary>
    private static decimal ResolveUnitCost(
        Number price, Number amount, int qty, int lineNo, List<ConversionWarning> warnings)
    {
        if (price.Decimals == 4 && price.Value > 0m) return price.Value;

        decimal recovered = decimal.Round(amount.Value / qty, 4, MidpointRounding.AwayFromZero);
        if (price.Value > 0m && Math.Abs(price.Value - recovered) <= 0.005m) return price.Value;

        warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.LineMathMismatch,
            $"A line's unit price did not read cleanly off the scan and was worked out from its amount "
            + $"as {recovered:0.0000}. Check this line against the paper in SSCS."));
        return recovered;
    }

    /// <summary>The whole number <paramref name="a"/>/<paramref name="b"/> makes, or null if it does not divide.</summary>
    private static int? DivideExactly(decimal a, decimal b)
    {
        if (b <= 0m || a <= 0m) return null;
        decimal q = a / b;
        int n = (int)decimal.Round(q, 0, MidpointRounding.AwayFromZero);
        if (n is <= 0 or > 9999) return null;
        return Math.Abs(n * b - a) <= 0.005m ? n : null;
    }

    // ────────────────────────────────────────────────────────────────────── gates

    /// <summary>
    /// Checks each band against the two figures it prints for itself. Both are hard gates.
    ///
    /// The unit count is the one that matters most, and it is the reason it is checked at all: a
    /// mis-read quantity is absorbed by the unit cost and still balances to the penny, so the money
    /// alone cannot see it. Bimbo prints "DELIVERY TOTAL 190" and "RETURNS TOTAL -39" for exactly
    /// this purpose.
    /// </summary>
    private static void CheckUnits(
        List<Section> sections, List<Row> rows, int? ticketUnits, List<ConversionWarning> warnings)
    {
        int grand = rows.Sum(r => r.Units);
        var wrong = sections
            .Where(s => s.PrintedUnits is int p && p != s.Rows.Sum(r => r.Units))
            .ToList();

        // The ticket's own grand count is an independent witness, and it outranks a section count
        // because it is checked against every row rather than a band of them. Example 6 prints
        // "RETURNS TOTAL -15" where the eight rows under it come to 16 and each one's retail *and*
        // wholesale columns agree on its quantity — and the ticket's "TICKET TOTALS 104" is
        // 120 - 16, which settles it. Refusing that ticket would cost a whole delivery over a
        // scanner mis-reading a 6 as a 5 in the one place it is printed.
        if (wrong.Count > 0 && ticketUnits is int tu && grand == tu)
        {
            foreach (var s in wrong)
                warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                    $"The {Name(s.Band)} section prints {Math.Abs(s.PrintedUnits!.Value)} items where its "
                    + $"lines come to {Math.Abs(s.Rows.Sum(r => r.Units))}. The ticket's own total of "
                    + $"{tu} agrees with the lines, so they were used - check this ticket in SSCS."));
            wrong.Clear();
        }

        if (wrong.Count > 0)
        {
            var s = wrong[0];
            throw new EdiConversionException(PdfReasonCodes.QtyMismatch,
                $"This Bimbo ticket does not add up: its {Name(s.Band)} section says "
                + $"{Math.Abs(s.PrintedUnits!.Value)} items but the lines under it come to "
                + $"{Math.Abs(s.Rows.Sum(r => r.Units))}. A quantity has been mis-read, or a line is "
                + "missing. Re-scan the whole page and try again.");
        }

        if (ticketUnits is int printedGrand && grand != printedGrand)
            throw new EdiConversionException(PdfReasonCodes.QtyMismatch,
                $"This Bimbo ticket does not add up: it says {printedGrand} items but its lines come to "
                + $"{grand}. A quantity has been mis-read, or a line is missing. Re-scan the whole page "
                + "and try again.");
    }

    /// <summary>
    /// Checks each band's money against the wholesale figure printed on its closing row. Unlike the
    /// unit counts this needs no cross-check: a section amount that disagrees with its own rows means
    /// a row was mis-read, and the ticket total would have to be wrong by the same amount to agree.
    /// </summary>
    private static void CheckSectionAmounts(List<Section> sections, List<ConversionWarning> warnings)
    {
        foreach (var s in sections)
        {
            if (s.PrintedAmount is decimal printedAmount)
            {
                decimal amount = s.Rows.Sum(r => r.Extended);
                decimal tolerance = CentPerLine * Math.Max(1, s.Rows.Count);
                if (Math.Abs(amount - printedAmount) > tolerance)
                    throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                        $"This Bimbo ticket does not add up: its {Name(s.Band)} section totals "
                        + $"{printedAmount:0.00} but the lines under it come to {amount:0.00}. "
                        + "Re-scan the whole page and try again.");
            }
        }
    }

    private static string Name(Band band) => band == Band.Returns ? "RETURNS" : "DELIVERIES";

    /// <summary>
    /// Reconciles the whole ticket against TICKET TOTALS, and reports the figure to write.
    ///
    /// CASH DUE is only a cross-check, never the answer: it is TICKET TOTALS *plus* PRIOR BALANCE,
    /// and while every sample prints a zero prior balance, one that is not zero would quietly book
    /// last week's money onto this week's invoice.
    /// </summary>
    private static decimal CheckTicketTotal(
        decimal? printed, decimal lineTotal, int rowCount, List<ConversionWarning> warnings)
    {
        if (printed is null)
            throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not read the TICKET TOTALS line on this Bimbo ticket. Re-scan the whole page, "
                + "including the totals at the bottom, and try again.");

        decimal tolerance = CentPerLine * Math.Max(1, rowCount);
        if (Math.Abs(lineTotal - printed.Value) > tolerance)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"This Bimbo ticket does not add up: it says {printed.Value:0.00} but its lines come to "
                + $"{lineTotal:0.00}. A page may be missing, or the scan has damaged a figure. "
                + "Re-scan the whole page and try again.");

        if (lineTotal != printed.Value)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The lines on this ticket come to {lineTotal:0.00} where it prints {printed.Value:0.00}. "
                + "The file was written with the ticket's own figure - check it in SSCS."));

        return printed.Value;
    }

    /// <summary>
    /// The unit count and wholesale amount printed on the ticket's own TICKET TOTALS row — the
    /// figures that cover every band at once, and so the tie-breaker when a section's own totals row
    /// disagrees with the rows under it.
    /// </summary>
    private static (int? Units, decimal? Amount) ReadTicketTotals(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = TotalRow().Match(line);
            if (!m.Success || ClassifyTotal(m.Groups["label"].Value) != Band.Ticket) continue;

            var nums = ReadNumbers(line);
            if (nums.Count < 2) continue;

            var units = nums.FirstOrDefault(n => !n.HasFraction);
            return (units is not null && units.Value <= int.MaxValue ? (int)units.Value : null,
                    nums[^1].Value);
        }
        return (null, null);
    }

    // ────────────────────────────────────────────────────────────────────── barcodes

    /// <summary>
    /// Turns a row into its invoice line, which is where the barcode is settled.
    ///
    /// Bimbo prints "74323-09183" for the UPC-A 074323091830: the leading zero and the trailing check
    /// digit are both absent. Putting the check digit back is arithmetic on the other eleven digits,
    /// not a guess, and SSCS then drops it again on import to land on id 07432309183 — which is
    /// exactly the id this store already holds the item under (verified on all 26 lines of the
    /// 07-28-2026 ticket).
    /// </summary>
    private static InvoiceLine BuildLine(Row row, List<ConversionWarning> warnings)
    {
        string upc10 = ResolveUpc(row, warnings);
        string gtin = Upc.AppendCheckDigit("0" + upc10);

        return new InvoiceLine
        {
            Gtin = gtin,
            Description = row.Description,
            Units = row.Units,
            UnitCost = row.UnitCost,
            RetailPrice = RetailSeed.FromCost(row.UnitCost, RetailMargin),
        };
    }

    /// <summary>
    /// The ten UPC digits to use, after the two checks that stand in for the check digit this vendor
    /// does not print.
    /// </summary>
    private static string ResolveUpc(Row row, List<ConversionWarning> warnings)
    {
        string upc = RepairPrefix(row.Upc10, row.Description, row.LineNo, warnings);

        var known = BimboCatalog.Find(row.ItemNumber);
        if (known is null || known.Value.Upc10 == upc) return upc;

        int differences = CountDifferences(known.Value.Upc10, upc);

        // More than one digit apart means the *item number* is the damaged column, not the UPC —
        // a mis-read item number lands on some other product's record, and following it would swap
        // one good barcode for a completely unrelated one. The barcode as printed is safer.
        if (differences > 1)
        {
            warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
                $"On the line for \"{row.Description}\" the barcode {upc} and Bimbo's item number "
                + $"{row.ItemNumber} point at different products. The barcode was used as printed - "
                + "check this line in SSCS."));
            return upc;
        }

        if (!known.Value.Trusted)
        {
            warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
                $"On the line for \"{row.Description}\" the barcode reads {upc} but item number "
                + $"{row.ItemNumber} has been seen with {known.Value.Upc10}. Only one ticket has ever "
                + "shown that, so the barcode was used as printed - check this line in SSCS."));
            return upc;
        }

        warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
            $"The barcode on the line for \"{row.Description}\" scanned as {upc}, one digit away from "
            + $"the {known.Value.Upc10} Bimbo's item number {row.ItemNumber} is on record with. The "
            + "recorded one was used so the delivery lands on the item SSCS already has."));
        return known.Value.Upc10;
    }

    /// <summary>
    /// Repairs the five-digit manufacturer prefix when the scan produced something one digit away
    /// from exactly one prefix Bimbo actually bills under — "14323" for 74323, "70810" for 70870,
    /// both the scanner reading a 7 as a 1. Ambiguity is never resolved: two candidates leave the
    /// code alone and warn.
    /// </summary>
    private static string RepairPrefix(string upc10, string description, int lineNo, List<ConversionWarning> warnings)
    {
        string prefix = upc10[..5];
        if (KnownPrefixes.Contains(prefix, StringComparer.Ordinal)) return upc10;

        var candidates = KnownPrefixes.Where(p => CountDifferences(p, prefix) == 1).ToList();
        if (candidates.Count != 1)
        {
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
                $"The barcode on the line for \"{description}\" starts with {prefix}, which is not one "
                + "of Bimbo's. It was used as printed, so this line may open a new item - check it in SSCS."));
            return upc10;
        }

        warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
            $"The barcode on the line for \"{description}\" scanned as {upc10}; {prefix} is one digit "
            + $"away from Bimbo's {candidates[0]} and was read as that."));
        return candidates[0] + upc10[5..];
    }

    private static int CountDifferences(string a, string b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    // ────────────────────────────────────────────────────────────────────── date

    /// <summary>
    /// The ticket's own SOD date, and separately a reading recovered from a damaged one.
    ///
    /// A date read with its separators intact ("SOD: 7-28-2026 3:07:49 PM", which is what the 300 dpi
    /// scans give) is used directly. When the scanner turned the separators into digits — which is
    /// what the older 200 dpi scans do, "711312026" for 7/13/2026 — the reading is reconstructed and
    /// returned only as a *suggestion*: it is arithmetic on damage, so the operator checks it against
    /// the paper rather than having it chosen for them.
    /// </summary>
    private static (DateOnly? Date, DateOnly? Suggested) FindDate(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = DateToken().Match(line);
            if (!m.Success) continue;
            if (ReadDateToken(m.Groups["tok"].Value) is not (DateOnly date, bool repaired)) continue;
            return repaired ? (null, date) : (date, null);
        }

        return (null, null);
    }

    /// <summary>
    /// Reads one date token, and says whether it had to be repaired to get there.
    ///
    /// The printed shape is fixed — month, separator, day, separator, four-digit year — and each
    /// separator is exactly one character whether or not the scanner could read it as one. Holding
    /// the separators to one character each is what makes the damaged readings recoverable: of the
    /// eight sample tickets, "7!28!2026", "711312026", "612672026", "6/2012026", "61212026",
    /// "717/2026", "712012026" and "611212026" each admit exactly one valid calendar date. Anything
    /// admitting two is left unread rather than guessed at.
    ///
    /// A token whose separators both survived as real punctuation was genuinely read; one that had
    /// to treat a digit as a separator is a repair, and a repair is only ever offered to the operator
    /// as a suggestion to check against the paper.
    /// </summary>
    private static (DateOnly Date, bool Repaired)? ReadDateToken(string token)
    {
        var found = new Dictionary<DateOnly, bool>();

        for (int monthLen = 1; monthLen <= 2; monthLen++)
        for (int dayLen = 1; dayLen <= 2; dayLen++)
        {
            if (monthLen + 1 + dayLen + 1 + 4 != token.Length) continue;

            string month = token[..monthLen];
            char firstSep = token[monthLen];
            string day = token.Substring(monthLen + 1, dayLen);
            char secondSep = token[monthLen + 1 + dayLen];
            string year = token[^4..];

            if (!TryDate(month, day, year, out var d)) continue;
            bool repaired = char.IsAsciiDigit(firstSep) || char.IsAsciiDigit(secondSep);
            // A reading that needed no repair is the better one for the same date.
            found[d] = found.TryGetValue(d, out bool was) ? was && repaired : repaired;
        }

        return found.Count == 1 ? (found.Keys.Single(), found.Values.Single()) : null;
    }

    /// <summary>
    /// A real calendar date within the range a delivery ticket can plausibly carry. The year bound is
    /// what stops a mis-split producing a date centuries out and having it look reasonable.
    /// </summary>
    private static bool TryDate(string month, string day, string year, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out int m) ||
            !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out int d) ||
            !int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out int y))
            return false;

        if (y is < 2000 or > 2099 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    // ────────────────────────────────────────────────────────────────────── tokens

    /// <summary>One numeric token as printed, with enough about its shape to tell the columns apart.</summary>
    private sealed class Number
    {
        public required decimal Value { get; init; }
        public required int Decimals { get; init; }
        public required int Index { get; init; }
        public bool HasFraction => Decimals > 0;
    }

    /// <summary>
    /// Splits a product row into its UPC, its item number and everything after them, or returns null
    /// when the line is not a product row at all.
    ///
    /// The two code columns are taken as **sixteen digits**, counted rather than pattern-matched,
    /// with spaces and hyphens between them ignored. That is what survives PdfPig's rendering of the
    /// scans: the same row can come back as "74323-09513 008799", "70870- 73400 014153" or
    /// "74323-0 951 3 008799", and a regex with lazy digit groups partitions the third of those
    /// wrongly — it reads the item number as "3 008799" and the row is dropped without a sound.
    /// Counting cannot make that mistake.
    ///
    /// It doubles as the filter for non-product lines, which is the other reason to prefer it: the
    /// header band, the address ("20110 FM 1314 RD"), the customer number ("12154 CUST# 992515-2")
    /// and every totals row all fail to produce sixteen leading digits.
    /// </summary>
    private static (string Upc10, string ItemNumber, string Columns)? SplitCode(string line)
    {
        int i = 0;
        while (i < line.Length && !char.IsAsciiDigit(line[i]) && !char.IsAsciiLetter(line[i])) i++;
        if (i >= line.Length || !char.IsAsciiDigit(line[i])) return null;

        var digits = new System.Text.StringBuilder(CodeDigits);
        while (i < line.Length && digits.Length < CodeDigits)
        {
            char c = line[i];
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (c is not (' ' or '-')) break;   // a letter or punctuation ends the code band
            i++;
        }

        if (digits.Length != CodeDigits) return null;
        string code = digits.ToString();
        return (code[..10], code[10..], line[i..].Trim());
    }

    /// <summary>
    /// Every numeric token in a stretch of text, unsigned. The sign is never taken from the page: a
    /// row's sign comes from which band it is in, so a minus the scan dropped cannot flip a return
    /// into a delivery.
    ///
    /// A number touching a letter is part of the description, not a column — Bimbo's own product
    /// names are full of them ("SS2P", "MB GLZ CIN RL SS 4Z", "FRST DON SS6P3.25") and letting them
    /// through both truncates the description and offers a false reading of the QTY column.
    /// </summary>
    private static List<Number> ReadNumbers(string text)
    {
        var found = new List<Number>();
        foreach (Match m in NumberToken().Matches(text))
        {
            if (TouchesLetter(text, m)) continue;
            string whole = m.Groups["whole"].Value;
            string frac = m.Groups["frac"].Success ? m.Groups["frac"].Value : "";
            if (!decimal.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out decimal value))
                continue;
            if (frac.Length > 0)
                value += decimal.Parse(frac, NumberStyles.None, CultureInfo.InvariantCulture)
                         / (decimal)Math.Pow(10, frac.Length);
            found.Add(new Number { Value = value, Decimals = frac.Length, Index = m.Index });
        }
        return found;
    }

    /// <summary>True when a numeric token is glued to a letter on either side, making it description text.</summary>
    private static bool TouchesLetter(string text, Match m)
    {
        if (m.Index > 0 && char.IsAsciiLetter(text[m.Index - 1])) return true;
        int after = m.Index + m.Length;
        return after < text.Length && char.IsAsciiLetter(text[after]);
    }

    /// <summary>
    /// The product description: everything before the row's numeric columns start. Bimbo's own text
    /// carries digits ("MB GLZ CIN RL SS 4Z", "MLA TRIKIS TUBE 4.2Z"), so the cut is made at the
    /// first of the trailing columns rather than at the first digit on the row.
    /// </summary>
    private static string Describe(string rest, List<Number> nums)
    {
        var tail = nums.TakeLast(TrailingColumns).ToList();
        string text = tail.Count > 0 ? rest[..tail[0].Index] : rest;
        return text.Trim().Length > 0 ? text.Trim() : rest.Trim();
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
}
