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


    /// <summary>The ticket's own TICKET TOTALS wholesale figure. Negative if returns outweighed the drop.</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>
    /// Builds the final invoice with the operator-typed date. The parsed <see cref="Date"/> cannot
    /// override it or reach the prompt.
    /// </summary>
    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = BimboPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        // The date the operator typed. The box starts empty because a plausible wrong date is what
        // this scanner produces
        // and nothing in the file can catch one - see InvoiceDetailsRequest.NeedsDate.
        Date = date,
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
/// opens a new item under an id belonging to nothing. Production therefore uses two independent
/// picture reads and asks the operator whenever they disagree. This parser is only the explicitly
/// approved OCR fallback and contains no product catalogue.
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

    /// <summary>
    /// How much debris may sit in front of the code band before a line stops looking like a product
    /// row. Two characters covers every speck the store's scans put there — "I 74323-09164",
    /// ".J4323-09145", "/4323-73791" — and is short enough that no heading on the page survives it:
    /// the nearest misses are "SOD:", "CASH CUSTOMER" and "GHRA 5813 BEST STOP", each of which still
    /// has a letter in the third position. See <see cref="RepairCode"/>.
    /// </summary>
    private const int LeadingDebris = 2;

    /// <summary>The six digits of the item-number column, which sit at the tail of the code band.</summary>
    private const int ItemDigits = 6;

    /// <summary>
    /// The fewest digits a damaged code band may still show and be treated as a product row that lost
    /// its barcode rather than as some other line of the ticket. Eight is two short of the UPC alone,
    /// so a stray pair of figures cannot reach it, and the address ("20110 FM 1314 RD"), the customer
    /// number ("12154 CUST# 992515-2") and the header all fall well below. It also keeps the band
    /// longer than <see cref="ItemDigits"/>, so the item number can always be taken off its tail.
    /// </summary>
    private const int PartialCodeDigits = 8;

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
    ///
    /// The word's own leading T may be damaged too — one of the store's tickets prints "TICKET
    /// IOTALS", which cost it the ticket-level unit count entirely. That count is the witness
    /// <see cref="CheckUnits"/> uses to overrule a band total the scan mangled, so losing it turns a
    /// ticket that would have shipped with a warning into a refusal: the same page prints "DELIUERY
    /// TOTAL 9~" for 90, and 90 - 6 is exactly the 84 the ticket row states. Matching is still
    /// anchored ahead of the first digit, so no product row can reach this pattern.
    /// </summary>
    [GeneratedRegex(@"^(?<label>[^\d]*?)[TI1L][O0]TALS?\b", RegexOptions.IgnoreCase)]
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
    public static BimboTicket Parse(
        IReadOnlyList<string> lines,
        List<ConversionWarning> warnings,
        Func<BimboUnknownField, string>? ask = null,
        VisionReading? vision = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = vision ?? VisionReading.Empty;

        // The vendor guard runs on the text layer, and only where there is one. A page with no
        // searchable text at all is the OCR-off scan, which is perfectly legible as a picture and is
        // exactly what the second reader is for; the vendor is then the one the operator chose in the
        // dropdown, which is the same trust the other two vendors' picture paths rest on. A text
        // layer that *is* present still has to look like this vendor's ticket.
        bool noTextLayer = lines.Count == 0;
        if (!noTextLayer && !LooksLikeBimbo(lines))
            throw new EdiConversionException(PdfReasonCodes.VendorMismatch,
                "This PDF does not look like a Bimbo delivery ticket - none of the things one always "
                + "has (BAKERIES or CASH STOPS in the heading, a DELIVERIES or RETURNS banner, a "
                + "DELIVERY TOTAL row, a CASH DUE line) could be found on it. Check that the right "
                + "vendor is selected in the dropdown. If it really is a Bimbo ticket, the scan is too "
                + "faint to read - re-scan the whole page and try again.");

        var sections = noTextLayer ? [] : ReadSections(lines, warnings);

        bool fromThePicture = sections.Sum(s => s.Rows.Count) == 0 && seen.Rows.Count > 0;
        if (fromThePicture)
        {
            sections = SectionsFromThePicture(seen);
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"Nothing could be read from the text in this PDF, so all {sections[0].Rows.Count} "
                + "lines were read off the picture of the page instead. Check them against the paper "
                + "before sending."));
        }

        var rows = sections.SelectMany(s => s.Rows).ToList();
        if (rows.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No line items were found on this Bimbo ticket. If the PDF is a photo or a scan with "
                + "no searchable text, re-scan it with the scanner's OCR setting turned on.");

        // Both readers found rows but not the same number of them, so the text layer has lost one -
        // and a lost row takes its money with it, which is why no total on the page can notice.
        if (!fromThePicture && seen.Rows.Count > rows.Count)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoLineItems,
                $"The text in this PDF gives {rows.Count} lines but the picture of the page shows "
                + $"{seen.Rows.Count}. A line may be missing - count them against the paper before "
                + "sending."));

        Settle(rows, seen, fromThePicture, ask, warnings);

        // A row nobody could settle leaves the ticket, and the gates below then refuse it. That is
        // deliberate: a delivery short one line books the wrong money, and by this point the picture
        // has been consulted and the operator has been asked.
        rows = rows.Where(r => r.IsSettled).ToList();
        foreach (var s in sections) s.Rows.RemoveAll(r => !r.IsSettled);

        // Both figures fall back to the picture, because on a page with no text layer there is
        // nowhere else for them to come from and on one that lost only its totals row they are the
        // difference between a gate and no gate at all. They are checks either way round: a wrong one
        // refuses the ticket, which is recoverable, where no gate lets wrong quantities through
        // silently, which is not. Neither is ever asked for: TICKET TOTALS is the largest print on
        // the page, so a ticket that has lost it has lost more than one figure and wants re-scanning.
        var (textUnits, textAmount) = fromThePicture ? (null, null) : ReadTicketTotals(lines);
        int? ticketUnits = textUnits ?? seen.PrintedUnits;
        decimal? ticketAmount = textAmount ?? seen.PrintedTotal;

        CheckUnits(sections, rows, ticketUnits, warnings);
        CheckSectionAmounts(sections, warnings);

        decimal lineTotal = rows.Sum(r => r.Extended);
        decimal total = CheckTicketTotal(sections, ticketAmount, lineTotal, rows.Count, warnings);

        var date = fromThePicture ? null : FindDate(lines);
        var items = rows.Select(BuildLine).ToList();

        int credits = items.Count(l => l.Units < 0);
        if (credits > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {credits} return line{(credits == 1 ? "" : "s")} that went back on the "
                + "truck. They are sent as negative quantities on the same invoice, which is how the "
                + "ticket's own total is worked out.",
                Routine: true));

        return new BimboTicket { Date = date, Total = total, Lines = items };
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

        /// <summary>
        /// Whether the row went back on the truck. The sign comes from here and never from a printed
        /// minus: on the text path it is the band the row was found in, which the scan cannot drop the
        /// way it drops a minus sign; on the picture path it is what the model said.
        /// </summary>
        public required bool IsCredit { get; init; }

        /// <summary>
        /// How many, unsigned — **null until it has been settled**, which is the whole reason this is
        /// a property rather than an init-only field.
        ///
        /// A row whose quantity the scan destroyed used to be dropped here and warned about, and a
        /// dropped row takes its units and its money out of the ticket with it, so both band gates and
        /// the ticket gate then failed and the whole delivery was refused. Ticket 84294890010234 loses
        /// two rows out of forty that way and is refused over 11 units of 236. Keeping the row lets the
        /// picture be consulted about it and, failing that, lets one question be asked about one row.
        /// </summary>
        public int? Qty { get; set; }

        /// <summary>Cost of one unit, always positive, four decimals. Null until settled.</summary>
        public decimal? UnitCost { get; set; }

        /// <summary>What the scan read in the AMOUNT column, kept as the thing a second reader is
        /// checked against and as the context a question carries.</summary>
        public decimal? PrintedAmount { get; init; }

        /// <summary>What the scan made of the QTY column, or empty where it lost the column outright.</summary>
        public string PrintedQty { get; init; } = "";

        /// <summary>
        /// What a vision model read on this row, where one was consulted and its reading could be tied
        /// here beyond doubt. Null is the normal case and must leave every rule unchanged.
        /// </summary>
        public VisionRow? Vision { get; set; }

        /// <summary>1-based page, for a row off the picture that has no line number to cut by.</summary>
        public int Page { get; init; }

        /// <summary>Whether that page went through the scanner the wrong way up.</summary>
        public bool PageUpsideDown { get; init; }

        /// <summary>
        /// The whole barcode to send, settled during <see cref="Settle"/> rather than when the line
        /// is built — because a row with no usable code has to leave the ticket *before* the gates
        /// run, so that the ticket's own count is what refuses it. Dropping a line after the gates
        /// would take its money out of an invoice that had already been proved to add up.
        /// </summary>
        public string? Gtin { get; set; }

        /// <summary>Signed units: negative for a row that went back.</summary>
        public int Units => (IsCredit ? -1 : 1) * (Qty ?? 0);

        /// <summary>Computed as units x cost, the way SSCS computes it — not the printed AMOUNT.</summary>
        public decimal Extended =>
            decimal.Round(Units * (UnitCost ?? 0m), 2, MidpointRounding.AwayFromZero);

        /// <summary>True once everything the line needs is known and the row can be billed.</summary>
        public bool IsSettled => Qty is > 0 && UnitCost is > 0m && Gtin is not null;
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
    ///
    /// A row that carries money but whose **quantity** would not come out is a different thing
    /// entirely, and it is no longer dropped — it comes back unsettled for <see cref="Settle"/> to
    /// take to the picture and then to the operator. Dropping it was silent damage: the row's units
    /// and its money left the ticket together, so all three gates failed at once and a delivery that
    /// needed one number typed was refused whole.
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

        var row = new Row
        {
            LineNo = lineNo,
            Upc10 = upc10,
            ItemNumber = item,
            Description = Describe(rest, nums),
            IsCredit = band == Band.Returns,
            PrintedAmount = amount.Value,
            // Only a whole number is offered back as "what the scan read" — where the column was lost
            // the figure sitting in its place belongs to some other column, and showing that as the
            // machine's reading of the quantity invites it to be confirmed.
            PrintedQty = printedQty is { HasFraction: false }
                ? printedQty.Value.ToString("0", CultureInfo.InvariantCulture)
                : "",
        };

        int? qty = ResolveQty(amount, price, retailAmount, suggRetail, printedQty, lineNo, warnings);
        if (qty is null or 0) return row;                        // unsettled — see Settle

        row.Qty = qty;
        decimal unitCost = ResolveUnitCost(price, amount, qty.Value, lineNo, warnings);
        if (unitCost > 0m) row.UnitCost = unitCost;
        return row;
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
    ///
    /// Four decimals is a strong shape but it is not proof of the digits inside it — one of the store's
    /// tickets prints `1.6900` for 1.8900. It is still taken as printed here, and deliberately: the
    /// AMOUNT column is the only thing that could contradict it, and AMOUNT is damaged far more often
    /// than PRICE is. The same ticket run also prints `338` for 3.78 and `1.56` for 7.56, and letting
    /// a bad amount rewrite a good price turns a row that was 3.78 into one that is 338. A price the
    /// row's own amount contradicts is repaired by <see cref="SettleAgainstThePicture"/> instead,
    /// where a genuinely different reader gets to say which of the two columns moved.
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

    // ────────────────────────────────────────────────────────────────────── second reader

    /// <summary>Two money figures that are the same figure to the cent.</summary>
    private static bool Near(decimal? a, decimal? b) =>
        a is not null && b is not null && Math.Abs(a.Value - b.Value) <= 0.005m;

    /// <summary>
    /// Ties a vision reading to the rows and settles what the text layer could not — by confirming,
    /// and where nothing confirms, by asking.
    ///
    /// The tie is <see cref="VisionReading.AlignTo"/>'s and it declines rather than guesses, for the
    /// reason set out there: a reading attached to the wrong row puts one product's figures onto
    /// another and every total on the page still adds up.
    /// </summary>
    private static void Settle(
        List<Row> rows, VisionReading vision, bool fromThePicture,
        Func<BimboUnknownField, string>? ask, List<ConversionWarning> warnings)
    {
        // Rows that came off the picture must not then be "corroborated" by the picture — one reader
        // agreeing with itself waves every figure through. What checks those is the row's own
        // arithmetic over three separately-read figures, and the count and total the ticket prints
        // for itself lower down the page.
        if (!fromThePicture)
        {
            var aligned = vision.AlignTo(
                [.. rows.Select(r => new VisionAnchor(r.ItemNumber, r.Qty, r.PrintedAmount))]);
            for (int i = 0; i < rows.Count; i++) rows[i].Vision = aligned[i];
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Qty is > 0 && r.UnitCost is > 0m)
            {
                NoteAnyDisagreement(r, warnings);
                SettleAgainstThePicture(r, warnings);
            }
            else SettleUnreadQuantity(r, i + 1, rows.Count, ask, warnings);

            r.Gtin = SettleBarcode(r, i + 1, rows.Count, ask, warnings);
        }
    }

    /// <summary>
    /// The whole barcode to send, or null where the row has no code anything could stand behind.
    ///
    /// Where the column holds ten digits, the explicitly-approved legacy fallback can only apply the
    /// vendor-prefix check. The AI-primary path asks unless two independent picture reads agree.
    ///
    /// Two kinds of row arrive without a code. One is read off the picture — the model is told to
    /// leave the field empty rather than tidy a half-read one, since nothing on a Bimbo ticket can
    /// catch a wrong barcode. The other is a text row whose code band the scan ate into past what
    /// <see cref="RepairShortBand"/> can stand behind. Both are worth exactly one question, and the
    /// question is cheap next to what used to happen to the second kind: it was not a row at all, so
    /// it left the ticket with its units and its money and every gate failed at once.
    /// </summary>
    private static string? SettleBarcode(
        Row r, int position, int lineCount,
        Func<BimboUnknownField, string>? ask, List<ConversionWarning> warnings)
    {
        if (r.Upc10.Length == 10) return Upc.AppendCheckDigit("0" + ResolveUpc(r, warnings));

        if (ask is null)
        {
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.BarcodeRequired,
                $"No barcode could be read for \"{r.Description}\", so that line was left off. Check "
                + "this ticket against the paper in SSCS."));
            return null;
        }

        string typed = ask(new BimboUnknownField(
            position, lineCount, r.Description, r.Upc10, r.ItemNumber,
            LineField.Barcode, r.Upc10, r.PrintedAmount, r.LineNo, r.Page, r.PageUpsideDown));

        string digits = Upc.DigitsOnly(typed);

        // Bimbo prints the body only, so ten digits is the whole of what is on the paper. Someone
        // reading a barcode off a different part of the box may type the full twelve, and that is the
        // same code — dropping back to the body keeps both spellings landing on one SSCS item.
        if (digits.Length == 12 && Upc.IsValidGtin(digits)) digits = digits[1..^1];
        else if (digits.Length == 11) digits = digits[1..];

        if (digits.Length != 10)
        {
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.BarcodeRequired,
                $"The barcode typed in for \"{r.Description}\" was not the ten digits this vendor "
                + "prints, so that line was left off. Check this ticket against the paper in SSCS."));
            return null;
        }

        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UpcCheckDigit,
            $"The barcode for \"{r.Description}\" could not be read off the picture and was typed in "
            + $"as {digits}."));
        return Upc.AppendCheckDigit("0" + digits);
    }

    /// <summary>
    /// Says so when the two readers disagree about a quantity the text layer settled on its own.
    ///
    /// Not an override. `AMOUNT / PRICE` dividing to a whole number is a strong reading — a damaged
    /// digit in either column almost never divides — and it is what nine tickets and a live SSCS
    /// round trip were checked against. What the picture adds here is a voice: where the two differ,
    /// one of them is wrong, and the band and ticket gates below will refuse the ticket if it is
    /// this one. Saying it out loud is what turns "this ticket does not add up" into a row to look at.
    /// </summary>
    private static void NoteAnyDisagreement(Row r, List<ConversionWarning> warnings)
    {
        if (r.Vision?.Units is not int seen || seen <= 0) return;
        if (seen == r.Qty) return;

        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.QtyMismatch,
            $"On the line for \"{r.Description}\" the text in the PDF gives a quantity of {r.Qty} and "
            + $"the picture of the page reads {seen}. The text was used - check this line against the "
            + "paper in SSCS."));
    }

    /// <summary>
    /// Repairs a unit price the row's own AMOUNT column contradicts, where the picture reads one that
    /// does not.
    ///
    /// A four-decimal price is taken as printed (see <see cref="ResolveUnitCost"/>) because the amount
    /// beside it is the column these scans damage most, and arithmetic alone cannot say which of the
    /// two moved: `1.6900` against an amount of 15.12 and `1.8900` against an amount of 338 are the
    /// same shape of disagreement and want opposite answers. A second reader can say, and what it says
    /// is which column it reads the same as the first reader did.
    ///
    /// So the amount has to be agreed by both before the price may be blamed for the disagreement.
    /// On the first of those rows the picture also reads 15.12, which leaves the price as the one
    /// figure the two readers differ on; on the second it reads 3.78 where the text has 338, so the
    /// amount is the damaged column, the printed price stands and the row is worth 3.78 as it always
    /// was. Getting that backwards books a $3.78 line at $338.
    ///
    /// The quantity is never touched here — <see cref="NoteAnyDisagreement"/> owns that — and neither
    /// is the amount, which is only ever a check. Nothing is derived: the price used is one the
    /// picture read off the paper, not one worked out from the other two columns.
    /// </summary>
    private static void SettleAgainstThePicture(Row r, List<ConversionWarning> warnings)
    {
        if (r.Qty is not int qty || r.UnitCost is not decimal cost || r.PrintedAmount is not decimal amount)
            return;
        if (Near(qty * cost, amount)) return;                       // the row already multiplies out

        if (r.Vision?.Price is not decimal seen || seen <= 0m || seen == cost) return;
        if (!Near(r.Vision.Amount, amount)) return;                 // both readers must have the amount
        if (!Near(qty * seen, amount)) return;

        r.UnitCost = seen;
        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.LineMathMismatch,
            $"On the line for \"{r.Description}\" the scan read a price of {cost:0.0000}, which does not "
            + $"make the {amount:0.00} the row prints over {qty}. The picture of the page reads "
            + $"{seen:0.0000}, which does, so that was used - check this line in SSCS."));
    }

    /// <summary>
    /// Settles a row whose quantity none of the three printed readings could produce.
    ///
    /// The picture may supply one, and only on terms strict enough that it is confirmation rather
    /// than a second guess. Three things have to hold together: the model read all of QTY, PRICE and
    /// AMOUNT on that row; those three multiply out; and its AMOUNT is the figure the text layer
    /// already read in that column. So the quantity is not worked out from anything — it is read off
    /// the paper by a second reader whose other two figures on the same row are corroborated, one of
    /// them by the first reader.
    ///
    /// That is exactly the shape of the two rows ticket 84294890010234 loses. The scan breaks
    /// `3.0100` into `3 .01 00`, which leaves the row no divisible pair and no printed QTY column
    /// either, while `12.04` in the AMOUNT column survives intact; the picture reads 4, 3.0100 and
    /// 12.04, and 4 x 3.0100 is the 12.04 both readers agree on.
    ///
    /// Anything short of that goes to the operator with the row on screen. Nothing here may invent a
    /// count: a quantity is the one figure on this vendor the money cannot check, because a wrong one
    /// is absorbed by the unit cost and the invoice still balances to the penny.
    /// </summary>
    private static void SettleUnreadQuantity(
        Row r, int position, int lineCount,
        Func<BimboUnknownField, string>? ask, List<ConversionWarning> warnings)
    {
        if (r.Vision is { Units: int units and > 0, Price: > 0m, Amount: > 0m } v
            && Near(units * v.Price!.Value, v.Amount)
            && (r.PrintedAmount is null || Near(v.Amount, r.PrintedAmount)))
        {
            r.Qty = units;
            r.UnitCost = v.Price.Value;
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UnreadableLine,
                $"The quantity on the line for \"{r.Description}\" was destroyed in the scan and read "
                + $"off the picture of the page as {units} at {v.Price.Value:0.0000}. That comes to the "
                + $"{v.Amount!.Value:0.00} the row prints - check this line in SSCS."));
            return;
        }

        if (ask is null)
        {
            // No way to ask — the row leaves the ticket, and the gates below refuse it rather than
            // let a delivery go out short. This is what the parser has always done here.
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UnreadableLine,
                $"The quantity on the line for \"{r.Description}\" could not be worked out from the "
                + "scan. Check this line against the paper in SSCS."));
            return;
        }

        string typed = ask(new BimboUnknownField(
            position, lineCount, r.Description, r.Upc10, r.ItemNumber,
            LineField.Units, r.PrintedQty, r.PrintedAmount, r.LineNo, r.Page, r.PageUpsideDown));

        if (!int.TryParse(typed?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int keyed)
            || keyed <= 0)
        {
            warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UnreadableLine,
                $"The quantity typed in for \"{r.Description}\" was not a whole number, so that line "
                + "was left off. Check this ticket against the paper in SSCS."));
            return;
        }

        r.Qty = keyed;
        if (r.PrintedAmount is decimal printed && printed > 0m)
            r.UnitCost = decimal.Round(printed / keyed, 4, MidpointRounding.AwayFromZero);

        warnings.Add(new ConversionWarning(r.LineNo, PdfReasonCodes.UnreadableLine,
            $"{r.Description} - the quantity was typed in as {keyed}"
            + (r.PrintedQty.Length > 0 ? $" (the scan read \"{r.PrintedQty}\")." : ".")));
    }

    /// <summary>
    /// Turns a reading taken off the picture into the ticket's rows, for a PDF whose text layer gave
    /// up nothing at all — the scanner's OCR setting switched off, which two of the store's thirteen
    /// samples have.
    ///
    /// One section, not two. The bands are a feature of the printed page and the model was not asked
    /// where they start, so there are no band totals to check against here; what a row's sign comes
    /// from instead is the model's own isCredit, and what catches a row facing the wrong way is the
    /// ticket's own total, which a flipped row misses by twice its value.
    ///
    /// **Line number 0 throughout, deliberately.** These rows have no place on the page as far as the
    /// text layer is concerned, so no question about one can cut its picture out — and a line number
    /// invented here would mark a figure on whatever stray text happened to be at that position.
    /// </summary>
    private static List<Section> SectionsFromThePicture(VisionReading seen)
    {
        var section = new Section { Band = Band.Deliveries };
        foreach (var v in seen.Rows)
        {
            var row = new Row
            {
                LineNo = 0,
                Upc10 = new string((v.Barcode ?? "").Where(char.IsAsciiDigit).ToArray()),
                ItemNumber = new string(v.ItemNumber.Where(char.IsAsciiDigit).ToArray()),
                Description = v.Description,
                IsCredit = v.IsCredit,
                PrintedAmount = v.Amount,
                PrintedQty = v.Units?.ToString(CultureInfo.InvariantCulture) ?? "",
                // At least 1. The reader stamps the page it asked about, but a row that arrived
                // without one would ask for page 0, which cuts nothing — and a question with no
                // picture on this path has nothing else to offer, since there is no line to describe.
                Page = Math.Max(1, v.Page),
                PageUpsideDown = v.PageUpsideDown,
            };

            // The row is taken only where its own three figures agree with each other. One reader
            // reporting a count with nothing behind it is exactly what a person is for, so a row that
            // does not multiply out arrives unsettled and is asked about.
            if (v.Units is int units and > 0 && v.Price is > 0m && v.Amount is > 0m
                && Near(units * v.Price.Value, v.Amount))
            {
                row.Qty = units;
                row.UnitCost = v.Price.Value;
            }

            section.Rows.Add(row);
        }
        return [section];
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
        List<Section> sections, decimal? printed, decimal lineTotal, int rowCount,
        List<ConversionWarning> warnings)
    {
        if (printed is null)
            throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not read the TICKET TOTALS line on this Bimbo ticket. Re-scan the whole page, "
                + "including the totals at the bottom, and try again.");

        decimal tolerance = CentPerLine * Math.Max(1, rowCount);
        if (Math.Abs(lineTotal - printed.Value) > tolerance)
        {
            // TICKET TOTALS is one figure printed once, and the scan damages it like any other:
            // 240.07 comes back "2411.07" on one of the store's tickets and 350.68 as "3511.68" on
            // another. The bands outrank it when they cover the whole ticket, because by this point
            // CheckSectionAmounts has already made each of them agree with the rows underneath it —
            // so DELIVERY TOTAL and RETURNS TOTAL are two separately printed figures, independently
            // reconciled, saying the same thing the lines say. One damaged figure does not outvote
            // that. Where a band prints no amount of its own the cover is incomplete and the ticket
            // is still refused, since the rows it holds have nothing standing behind them.
            decimal bandTotal = sections.Sum(s => s.PrintedAmount ?? 0m);
            if (sections.Count > 0 && sections.All(s => s.PrintedAmount is not null)
                && Math.Abs(bandTotal - lineTotal) <= tolerance)
            {
                warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                    $"This ticket's own TICKET TOTALS reads {printed.Value:0.00}, which its sections do "
                    + $"not come to. They print {bandTotal:0.00} between them and that agrees with the "
                    + "lines, so it was used - check this ticket in SSCS."));
                return bandTotal;
            }

            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"This Bimbo ticket does not add up: it says {printed.Value:0.00} but its lines come to "
                + $"{lineTotal:0.00}. A page may be missing, or the scan has damaged a figure. "
                + "Re-scan the whole page and try again.");
        }

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
    private static InvoiceLine BuildLine(Row row)
    {
        decimal cost = row.UnitCost!.Value;
        return new InvoiceLine
        {
            Gtin = row.Gtin!,
            Description = row.Description,
            Units = row.Units,
            UnitCost = cost,
            RetailPrice = RetailSeed.FromCost(cost, RetailMargin),
        };
    }

    /// <summary>
    /// The ten UPC digits to use, after the two checks that stand in for the check digit this vendor
    /// does not print.
    /// </summary>
    private static string ResolveUpc(Row row, List<ConversionWarning> warnings)
    {
        string upc = RepairPrefix(row.Upc10, row.Description, row.LineNo, warnings);
        string? fromPicture = row.Vision?.Barcode is string b && b.Length == 10 ? b : null;

        // This is the explicitly-approved legacy OCR fallback. It has no product table to consult:
        // new Bimbo products must work without a source edit or rebuild. If its optional picture
        // helper disagrees, neither reading can settle the code, so retain the OCR value and warn.
        if (fromPicture is not null && fromPicture != upc)
            warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.UpcCheckDigit,
                $"On the line for \"{row.Description}\" the text in the PDF gives the barcode {upc} "
                + $"and the picture of the page reads {fromPicture}. Nothing on a Bimbo ticket can "
                + "settle which is right, so the text was used - check this line in SSCS."));
        return upc;
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
    /// The ticket's own SOD date, or null where the scan damaged it.
    ///
    /// A date read with its separators intact ("SOD: 7-28-2026 3:07:49 PM", which is what the 300 dpi
    /// scans give) is used. When the scanner turned the separators into digits — what the older
    /// 200 dpi scans do, "711312026" for 7/13/2026 — the token can be reconstructed, and the
    /// reconstruction is deliberately **thrown away**: it is arithmetic on damage, and the operator
    /// types the date off the paper on every invoice regardless. It used to come back as a suggestion
    /// to pre-fill the box with, which is the habit that let an invoice go out dated 2016.
    /// </summary>
    private static DateOnly? FindDate(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = DateToken().Match(line);
            if (!m.Success) continue;
            if (ReadDateToken(m.Groups["tok"].Value) is not (DateOnly date, bool repaired)) continue;
            return repaired ? null : date;
        }

        return null;
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
    /// to treat a digit as a separator is a repair and is discarded. Neither form reaches the
    /// invoice-details prompt.
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
        if (i < line.Length && char.IsAsciiDigit(line[i]))
        {
            var (digits, end) = ScanCode(line, i, tolerant: false);
            if (digits.Length == CodeDigits)
                return (digits[..10], digits[10..], line[end..].Trim());
        }

        return RepairCode(line);
    }

    /// <summary>
    /// Collects the code band from <paramref name="from"/>, returning its digits and where it
    /// stopped. A letter always ends it — that is the description starting. <paramref name="tolerant"/>
    /// additionally lets any punctuation act as a separator, where the strict read allows only the
    /// space and the hyphen Bimbo actually prints.
    /// </summary>
    private static (string Digits, int End) ScanCode(string line, int from, bool tolerant)
    {
        var digits = new System.Text.StringBuilder(CodeDigits);
        int i = from;
        bool letterSpent = false;
        while (i < line.Length && digits.Length < CodeDigits)
        {
            char c = line[i];
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (char.IsAsciiLetter(c))
            {
                // A letter is normally the description starting. The exception is a hyphen the scan
                // turned into one ("7432r09157" for 74323-09157), which is only credible sitting
                // between two digits, and is allowed once per band so that a product name can never
                // be walked into.
                if (!tolerant || letterSpent || !BetweenDigits(line, i)) break;
                letterSpent = true;
            }
            else if (!tolerant && c is not (' ' or '-')) break; // strict: only Bimbo's own separators
            i++;
        }
        return (digits.ToString(), i);
    }

    /// <summary>True when the character at <paramref name="at"/> has a digit on both sides of it.</summary>
    private static bool BetweenDigits(string line, int at) =>
        at > 0 && char.IsAsciiDigit(line[at - 1])
        && at + 1 < line.Length && char.IsAsciiDigit(line[at + 1]);

    /// <summary>
    /// A second, deliberately looser look at a line the strict read rejected.
    ///
    /// Losing a product row is the most expensive thing this parser can do, and the quietest. The
    /// row's units and its money leave the ticket together, so the band gates and the ticket gate all
    /// fail at once and the whole delivery is refused over one speck of ink — and it is refused
    /// *silently*, because a line that was never a row is never offered to the picture and never
    /// asked about either. Sixteen of the store's tickets on 2026-08-12 produced eight such rows
    /// across six tickets, every one of them a real delivery line that any person could read.
    ///
    /// Two kinds of damage, both off those scans:
    ///
    ///   a speck INSIDE the band  "70870-00910 ,007908"  "70870=00909 007907"  "74323-09398 (07805"
    ///   a speck IN FRONT of it   "I 74323-09164 008785" ".J4323-09145 008783" "/4323-73791 013529"
    ///
    /// What stops a heading being read as a row is that the band must still yield fifteen or sixteen
    /// digits **and end at a letter** — the product description. No heading manages both: the closest
    /// is "500: 21312020 10:0649 AM ...", whose sixteen digits stop mid-figure rather than at a word.
    /// </summary>
    private static (string Upc10, string ItemNumber, string Columns)? RepairCode(string line)
    {
        for (int start = 0; start <= LeadingDebris && start < line.Length; start++)
        {
            if (!char.IsAsciiDigit(line[start])) continue;

            var (digits, end) = ScanCode(line, start, tolerant: true);

            // The band has to hand over to a description. The gap between them is whitespace or more
            // scan debris, so it is stepped over — but only over characters that are neither letters
            // nor digits, which is what keeps "500: 21312020 10:0649 AM" (sixteen digits that stop
            // in the middle of a clock time) from reading as a product row.
            int after = end;
            while (after < line.Length && !char.IsAsciiLetterOrDigit(line[after])) after++;
            if (after >= line.Length || !char.IsAsciiLetter(line[after])) continue;

            string rest = line[end..].Trim();
            if (digits.Length == CodeDigits) return (digits[..10], digits[10..], rest);

            if (digits.Length == CodeDigits - 1 && RepairShortBand(digits) is { } repaired)
                return (repaired.Upc10, repaired.ItemNumber, rest);

            // The band has lost more than the prefix rule can stand behind
            // ("57528-0374? 011095", "74323-738% 950661"). Everything else that makes this a row is
            // still on the line — its name, its count and both money columns — so it is kept with no
            // barcode at all and <see cref="SettleBarcode"/> asks for one. That costs a question; the
            // alternative is the row leaving the ticket with its units and its money, which fails
            // every gate at once and refuses a delivery nobody can see anything wrong with. The item
            // number survives at the tail of the band and is carried through, because it is what a
            // person will read the barcode off the shelf label for.
            if (digits.Length >= PartialCodeDigits) return ("", digits[^ItemDigits..], rest);
        }
        return null;
    }

    /// <summary>
    /// A code band one digit short, which is the scan having eaten a digit rather than having added a
    /// speck ("74323-09164 08785", "/4323-73791 013529"). Which column lost it decides the whole
    /// reading, so it is never guessed: it is settled by the prefix rule or not at all.
    ///
    /// Failing that the *UPC* is taken whole and the item number is short a leading zero, accepted
    /// only when the ten digits open with a prefix Bimbo actually bills under. A row that satisfies
    /// neither keeps no barcode at all rather than a plausible wrong one and goes to the operator.
    /// </summary>
    private static (string Upc10, string ItemNumber)? RepairShortBand(string digits)
    {
        // A missing UPC digit cannot be restored without guessing now that the product catalogue is
        // gone. Keep the row with no barcode and ask the operator instead.

        // The item number lost its leading zero, so the UPC is the whole leading ten.
        string upc10 = digits[..10];
        string item5 = "0" + digits[10..];
        if (!KnownPrefixes.Contains(upc10[..5], StringComparer.Ordinal)) return null;

        return (upc10, item5);
    }

    /// <summary>True when <paramref name="shorter"/> is <paramref name="whole"/> with one character removed.</summary>
    private static bool IsOneDeletionOf(string whole, string shorter)
    {
        if (whole.Length != shorter.Length + 1) return false;
        for (int drop = 0; drop < whole.Length; drop++)
            if (string.Concat(whole[..drop], whole[(drop + 1)..]) == shorter) return true;
        return false;
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

/// <summary>
/// One figure on one Bimbo row that the scan destroyed, that the picture could not confirm, and that
/// none of the ticket's own arithmetic could recover. Described well enough for the operator to find
/// the row on the paper without counting down it.
///
/// The AI-primary path may ask for UPC, quantity, price, or amount when a field cannot be read or the
/// independent checks disagree. Description is carried through from the model; SSCS keeps its own
/// wording for an item it already has.
/// </summary>
public sealed record BimboUnknownField(
    /// <summary>1-based position among the ticket's rows — how the operator counts down it.</summary>
    int Position,
    /// <summary>How many rows the ticket has, so the prompt can say "3 of 27".</summary>
    int LineCount,
    string Description,
    /// <summary>The ten digits under UPC, which is how the operator matches the row to the paper.</summary>
    string Upc10,
    /// <summary>Bimbo's own item number, the second column and the other half of that match.</summary>
    string ItemNumber,
    LineField Field,
    /// <summary>What the scan made of the field, or empty where it lost the column outright.</summary>
    string PrintedValue,
    /// <summary>The row's amount, when it read — context, never the field being asked about.</summary>
    decimal? Amount,
    /// <summary>
    /// 1-based line number of the row in the extracted text, so the caller can cut that row out of
    /// the scanned picture and show the operator the paper rather than describe it. Zero for a row
    /// that came off the picture, which has no place in the text layer to cut by.
    /// </summary>
    int LineNo = 0,
    /// <summary>1-based page this row is on, for a row read off the picture.</summary>
    int Page = 0,
    /// <summary>Whether that page went through the scanner the wrong way up.</summary>
    bool PageUpsideDown = false);
