using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// One product row of a Red Bull delivery ticket. A null <see cref="Gtin"/>, <see cref="Units"/> or
/// <see cref="Amount"/> means the scan destroyed that figure and nothing on the ticket could recover
/// it, so the operator has to read it off the paper. Those are the only three fields worth asking
/// about — see <see cref="LineFieldRequest"/>.
/// </summary>
public sealed class RedBullLine
{
    /// <summary>1-based position among the ticket's product rows, so a prompt can say which one.</summary>
    public required int Position { get; init; }

    /// <summary>Red Bull's own product number as the scan read it ("RB4816" arrives as "984816").</summary>
    public required string ItemNumber { get; init; }

    /// <summary>Best effort. Never asked about: SSCS overrides it for any item it already knows.</summary>
    public required string Description { get; init; }

    /// <summary>Individual units, negative on a returned line. Null when it must be typed.</summary>
    public required int? Units { get; init; }

    /// <summary>The line's extended amount, negative on a returned line. Null when it must be typed.</summary>
    public required decimal? Amount { get; init; }

    /// <summary>Full 12-digit UPC-A. Null when it must be typed.</summary>
    public required string? Gtin { get; init; }

    /// <summary>True when this row came off a credit page — goods that went back on the truck.</summary>
    public required bool IsCredit { get; init; }

    /// <summary>What the scan made of the barcode, shown when asking for a retype.</summary>
    public required string PrintedCode { get; init; }
    /// <summary>What the scan made of the UNITS column.</summary>
    public required string PrintedUnits { get; init; }
    /// <summary>What the scan made of the TOTAL column.</summary>
    public required string PrintedAmount { get; init; }

    public bool NeedsBarcode => Gtin is null;
    public bool NeedsUnits => Units is null;
    public bool NeedsAmount => Amount is null;
    public bool NeedsAnything => NeedsBarcode || NeedsUnits || NeedsAmount;
}

/// <summary>
/// A parsed Red Bull ticket: every page of the PDF, deliveries and credits together, as one payable.
/// </summary>
public sealed class RedBullTicket
{
    /// <summary>The printed date, or null when it was unreadable or impossible and must be keyed.</summary>
    public required DateOnly? Date { get; init; }

    /// <summary>A reconstructed date to offer as a suggestion, never used on its own.</summary>
    public required DateOnly? SuggestedDate { get; init; }

    /// <summary>The sum of the lines that read cleanly — what the operator sees on the number prompt.</summary>
    public required decimal Total { get; init; }

    /// <summary>What the ticket printed for itself (ROA, else INVOICE), when it was readable.</summary>
    public required decimal? PrintedTotal { get; init; }

    /// <summary>
    /// Every total the ticket claims for itself — the ROA figure and the per-page INVOICE figures
    /// added up. They are kept as a set rather than one number because the scanner damages them
    /// individually: one sample reads ROA $509.14 against an INVOICE of $599.14, and the lines prove
    /// the second right. The parse is in trouble only when the lines match *none* of them.
    /// </summary>
    public required IReadOnlyList<decimal> PrintedTotals { get; init; }

    public required IReadOnlyList<RedBullLine> Lines { get; init; }

    /// <summary>
    /// Builds the final invoice, calling <paramref name="ask"/> for each figure the scan destroyed.
    /// The invoice total is the sum of the lines, never the printed figure — see
    /// <see cref="RedBullPdfParser"/> for why that is the right way round on this vendor.
    /// </summary>
    public Invoice ToInvoice(string invoiceNumber, DateOnly date, Func<RedBullLine, LineField, string> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var lines = new List<InvoiceLine>(Lines.Count);
        foreach (var l in Lines)
        {
            string gtin = l.Gtin ?? RedBullPdfParser.ParseTypedGtin(ask(l, LineField.Barcode), l);
            int units = l.Units ?? RedBullPdfParser.ParseTypedUnits(ask(l, LineField.Units), l);
            decimal amount = l.Amount ?? RedBullPdfParser.ParseTypedAmount(ask(l, LineField.Amount), l, units);

            // SSCS wants the cost of one unit; the ticket prints the cost of a case and the extended
            // amount. Extended / units gives the same figure and needs no pack size, which matters
            // because a credit row prints no case price at all.
            decimal unitCost = decimal.Round(Math.Abs(amount) / Math.Abs(units), 4, MidpointRounding.AwayFromZero);
            lines.Add(new InvoiceLine
            {
                Gtin = gtin,
                Description = l.Description,
                Units = units,
                UnitCost = unitCost,
                RetailPrice = RetailSeed.FromCost(unitCost, RedBullPdfParser.RetailMargin),
            });
        }

        return new Invoice
        {
            SupplierName = RedBullPdfParser.SupplierName,
            InvoiceNumber = invoiceNumber,
            Date = Date ?? date,
            Total = lines.Sum(l => l.ExtendedCost),
            Lines = lines,
        };
    }
}

/// <summary>
/// Parses a Red Bull Distribution Company delivery ticket into a <see cref="RedBullTicket"/>. Rules
/// and the sample evidence behind them are in analysis/vendors/RED_BULL.md.
///
/// Layout — each product is a row plus a barcode line underneath it:
///
///   RB1718   1   24   RED BULL 8.4OZ LS   $40.75  $0.00  $0.00  $0.00   $40.75
///                       611269991000                                    ||||||||
///
/// which is item number, QTY in **cases**, UNITS in **individual units**, description, then
/// PRICE (the cost of one case), DEP, DISC, SUGAR and the extended TOTAL. A credit page prints the
/// same columns with QTY as a fraction of a case, UNITS in brackets and the money in brackets.
///
/// UNITS is exactly what SSCS wants for Qty — verified against the live system on the 11/24/2025
/// ticket, which SSCS holds as one $342.50 paidout of ten lines totalling 145 units, where the
/// delivery page prints 156 units and the credit page eleven. So the whole PDF is one payable and
/// there is no case arithmetic anywhere: the ticket has already done it.
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// WHY THE PRINTED TOTAL DOES NOT GATE THIS PARSER
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// Every other vendor here reconciles its lines against the total the invoice prints for itself.
/// On Red Bull that test is not available, because the printed total is damaged as often as the
/// rows are. One sample prints $918.33 and the text layer reads $910.33 in all three of the places
/// the figure appears — a majority vote cannot help, because the same glyph renders the same way
/// every time it is printed.
///
/// The same effect rules out the obvious row-level test. PRICE and TOTAL hold the same number when
/// QTY is 1, so they look like two independent readings — but they are the same glyph twice, and
/// the scanner misreads it identically in both. On the 11/24/2025 ticket both columns read $40.76
/// where the paper says $40.75.
///
/// What survives is arithmetic across *different* glyphs:
///   * QTY x PRICE = TOTAL, which catches damage confined to one column;
///   * UNITS = QTY x pack, from <see cref="RedBullCatalog"/>;
///   * the UPC-A check digit, which is a real detector — Red Bull prints the whole barcode;
///   * the sum of the rows against the printed INVOICE/ROA, which is made of other glyphs again.
/// Where those agree the figure is taken. Where they cannot settle it, the operator is asked for
/// that one field rather than the file being refused or a number being guessed.
/// </summary>
public static partial class RedBullPdfParser
{
    /// <summary>Goes in the file's Supplier Name; matches the Vendor ID the store already uses.</summary>
    public const string SupplierName = "RED BULL";

    /// <summary>
    /// Margin used to seed the shelf price of an item SSCS has never seen. Red Bull prints no retail
    /// column at all, so this is the only source. The store's real margins on these lines run 41-51%
    /// (SSCS shows Ea List 3.49 against Ea Cost 1.6979, and 4.49 against 2.2271).
    /// </summary>
    public const decimal RetailMargin = 0.45m;

    private const double MoneyColumnStart = 0.45;   // fraction of page width; QTY can look like money
    private const double ColumnTolerance = 0.05;    // how far a token may sit from its column centre
    private const double QtyColumnEnd = 0.32;

    /// <summary>Packs Red Bull actually ships, used to sanity-check a UNITS/QTY pair.</summary>
    private static readonly int[] KnownPacks = [4, 6, 12, 24, 48];

    /// <summary>Parses every page of the PDF into one ticket.</summary>
    public static RedBullTicket Parse(IReadOnlyList<PdfPage> pages, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(warnings);

        var rows = new List<RawRow>();
        var figures = new List<PageFigures>();
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var found = ReadPage(pages[pageIndex], pageIndex, out var fig);
            if (found.Count == 0) continue;
            rows.AddRange(found);
            figures.Add(fig);
        }

        if (rows.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No product lines could be read from this Red Bull ticket. Check that the right vendor "
                + "is selected and that the scan covers the whole page.");

        var (date, suggested) = ReadDate(pages, warnings);
        var lines = Settle(rows, figures, warnings);

        decimal known = lines.Where(l => l.Amount is not null).Sum(l => l.Amount!.Value);
        decimal? printed = figures.Select(f => f.Roa ?? f.Invoice).FirstOrDefault(v => v is not null);
        CheckAgainstPrintedFigures(lines, figures, warnings);

        return new RedBullTicket
        {
            Date = date,
            SuggestedDate = suggested,
            Total = known,
            PrintedTotal = printed,
            PrintedTotals = PrintedTargets(rows, figures).Select(c => c / 100m).ToList(),
            Lines = lines,
        };
    }

    // ───────────────────────────────────────────────────────────────────── page reading

    /// <summary>One product row exactly as the scan presented it, before anything is settled.</summary>
    private sealed class RawRow
    {
        /// <summary>0-based page this row came off, so a page's printed figures can be matched to it.</summary>
        public required int PageIndex { get; init; }
        public required string ItemNumber { get; init; }
        public required string Description { get; init; }
        public required decimal? Qty { get; init; }
        public required int? Units { get; init; }
        public required decimal? Price { get; init; }
        public required decimal? Amount { get; init; }
        public required bool IsCredit { get; init; }
        public required string PrintedUnits { get; init; }
        public required string PrintedAmount { get; init; }
        public List<string> CodeCandidates { get; } = [];
        public string PrintedCode { get; set; } = "";
        public string? Gtin { get; set; }
        public int? Pack { get; set; }
    }

    private sealed record PageFigures(
        int PageIndex, int? Skus, decimal? Cases, int? UnitsDelivered, decimal? Invoice, decimal? Roa);

    private static List<RawRow> ReadPage(PdfPage page, int pageIndex, out PageFigures figures)
    {
        double w = page.Width;
        figures = new PageFigures(pageIndex, null, null, null, null, null);
        var found = new List<RawRow>();

        // The money columns are calibrated from the money on the page rather than from the column
        // headings, which are OCR debris ("PflICE", "TdrAo."). Their positions survive; their text
        // does not.
        var moneyCentres = new List<double>();
        foreach (var row in page.Rows)
            foreach (var word in row)
                if (word.Centre > w * MoneyColumnStart && TryMoney(word.Text, out _, out _))
                    moneyCentres.Add(word.Centre);

        var clusters = Cluster(moneyCentres, w * 0.03).Where(c => c.Count >= 3).ToList();
        if (clusters.Count < 2) return found;
        double priceCentre = clusters[0].Average();
        double totalCentre = clusters[^1].Average();

        // QTY and UNITS are the last two small numbers before the description. Taking the last two
        // rather than the first two is what survives the ID column splitting in half ("11 136463"),
        // and QTY has to allow a decimal because a credit page bills a fraction of a case (0.04).
        var qtyCentres = new List<double>();
        var unitCentres = new List<double>();
        foreach (var row in page.Rows)
        {
            var nums = row
                .Where(x => x.Centre < w * QtyColumnEnd
                            && (QtyLike().IsMatch(x.Text) || CountLike().IsMatch(x.Text)))
                .ToList();
            if (nums.Count < 2) continue;
            qtyCentres.Add(nums[^2].Centre);
            unitCentres.Add(nums[^1].Centre);
        }
        if (qtyCentres.Count == 0) return found;
        double qtyCentre = Median(qtyCentres);
        double unitCentre = Median(unitCentres);

        RawRow? pending = null;
        var figureRows = new List<string>();
        foreach (var row in MergeSplitRows(page.Rows, w, qtyCentre, totalCentre))
        {
            string text = string.Join(" ", row.Select(x => x.Text));
            if (EndOfProducts().IsMatch(text))
            {
                figureRows.Add(text);
                continue;
            }
            if (figureRows.Count > 0) { figureRows.Add(text); continue; }

            var totalWord = Nearest(row, totalCentre, w * (ColumnTolerance + 0.01), t => TryMoney(t, out _, out _));

            // A row is a product either because it carries the TOTAL column, or because it carries
            // QTY, UNITS and a word of description. The second test matters: the barcode printed
            // beside the TOTAL column bleeds into it, and on the worst scans destroys it outright.
            // Recognising the row only by its money would drop the line altogether, and a missing
            // line is the one error that leaves the invoice looking perfectly consistent.
            bool looksLikeProduct = totalWord is not null
                || (Nearest(row, qtyCentre, w * ColumnTolerance, t => QtyLike().IsMatch(t)) is not null
                    && Nearest(row, unitCentre, w * ColumnTolerance, t => CountLike().IsMatch(t)) is not null
                    && row.Any(x => x.Centre > unitCentre && DescriptionWord().IsMatch(x.Text)));

            var codes = CodeCandidates(text);

            // A barcode arriving when the current product already has one belongs to the next
            // product, whose row was too damaged to recognise. Rather than let that line disappear
            // - the one failure that leaves the invoice looking perfectly consistent - it becomes a
            // row of its own carrying just the barcode, and the operator is asked for the rest.
            bool orphanBarcode = !looksLikeProduct && codes.Count > 0
                                 && pending is not null && pending.CodeCandidates.Count > 0;

            if (looksLikeProduct || orphanBarcode)
            {
                if (pending is not null) found.Add(pending);
                pending = looksLikeProduct
                    ? BuildRow(row, w, qtyCentre, unitCentre, priceCentre, totalWord, pageIndex)
                    : BuildRow([], w, qtyCentre, unitCentre, priceCentre, null, pageIndex);
            }

            // Whether or not this row started a product, it may carry that product's barcode. The
            // code is shown back to the operator when one has to be typed, so it is taken from the
            // word that looks most like a barcode rather than from the whole row - the bars beside
            // it come through the text layer as runs of 1s and would be nonsense to show anyone.
            if (pending is not null)
            {
                pending.CodeCandidates.AddRange(codes);
                if (pending.PrintedCode.Length == 0)
                {
                    var printed = row
                        .Select(x => Digits(x.Text))
                        .FirstOrDefault(d => d.Length is >= 11 and <= 13);
                    if (printed is not null) pending.PrintedCode = printed;
                }
            }
        }
        if (pending is not null) found.Add(pending);

        figures = ReadFigures(figureRows, pageIndex);
        return found;
    }

    /// <summary>
    /// Reunites a printed row that the scan's skew split in two.
    ///
    /// These tickets are fed through the scanner slightly crooked, so one printed row can drift far
    /// enough that its left end and its right end land on different baselines. The extractor then
    /// reports the money on one line and the item number and QTY on the next:
    ///
    ///     40  BED BULL 8402 LS  $40.75  $0.00  $8.00        .50
    ///     101718  2
    ///
    /// Both halves are needed and neither is a row on its own. They are merged only when one carries
    /// the TOTAL column, the other carries the QTY column, and their horizontal extents do not
    /// overlap — which is what makes them two halves of one row rather than two rows.
    /// </summary>
    private static List<IReadOnlyList<PdfPositionedWord>> MergeSplitRows(
        IReadOnlyList<IReadOnlyList<PdfPositionedWord>> rows, double w, double qtyCentre, double totalCentre)
    {
        var merged = new List<IReadOnlyList<PdfPositionedWord>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i + 1 < rows.Count && IsSplitPair(row, rows[i + 1], w, qtyCentre, totalCentre))
            {
                merged.Add(row.Concat(rows[i + 1]).OrderBy(x => x.Left).ToList());
                i++;
                continue;
            }
            merged.Add(row);
        }
        return merged;
    }

    private static bool IsSplitPair(
        IReadOnlyList<PdfPositionedWord> a, IReadOnlyList<PdfPositionedWord> b,
        double w, double qtyCentre, double totalCentre)
    {
        if (a.Count == 0 || b.Count == 0) return false;
        // Disjoint horizontally: every word of one half sits clear of the other.
        bool disjoint = a.Max(x => x.Right) < b.Min(x => x.Left) || b.Max(x => x.Right) < a.Min(x => x.Left);
        if (!disjoint) return false;

        bool HasTotal(IReadOnlyList<PdfPositionedWord> r) =>
            Nearest(r, totalCentre, w * (ColumnTolerance + 0.01), t => TryMoney(t, out _, out _)) is not null;
        bool HasQty(IReadOnlyList<PdfPositionedWord> r) =>
            Nearest(r, qtyCentre, w * ColumnTolerance, t => QtyLike().IsMatch(t)) is not null;

        return (HasTotal(a) && HasQty(b) && !HasQty(a)) || (HasTotal(b) && HasQty(a) && !HasQty(b));
    }

    private static RawRow BuildRow(
        IReadOnlyList<PdfPositionedWord> row, double w,
        double qtyCentre, double unitCentre, double priceCentre,
        PdfPositionedWord? totalWord, int pageIndex)
    {
        var qtyWord = Nearest(row, qtyCentre, w * ColumnTolerance, t => QtyLike().IsMatch(t));
        var unitWord = Nearest(row, unitCentre, w * ColumnTolerance, t => CountLike().IsMatch(t));
        var priceWord = Nearest(row, priceCentre, w * ColumnTolerance, t => TryMoney(t, out _, out _));

        decimal? amount = null;
        bool amountBracketed = false;
        if (totalWord is not null && TryMoney(totalWord.Text, out decimal a, out amountBracketed)) amount = a;
        decimal? price = priceWord is not null && TryMoney(priceWord.Text, out decimal p, out _) ? p : null;

        bool credit = amountBracketed || (unitWord is not null && Bracketed(unitWord.Text));
        int? units = unitWord is not null && int.TryParse(unitWord.Text.Trim('(', ')'), out int u) ? u : null;
        decimal? qty = qtyWord is not null
            && decimal.TryParse(qtyWord.Text.Trim('(', ')'), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal q)
                ? q : null;

        // The description is whatever sits between the UNITS column and the PRICE column.
        double descStart = unitWord?.Right ?? w * QtyColumnEnd;
        double descEnd = priceWord?.Left ?? totalWord?.Left ?? w;
        string description = string.Join(" ", row
            .Where(x => x.Left >= descStart && x.Right <= descEnd)
            .Select(x => x.Text)).Trim();

        return new RawRow
        {
            PageIndex = pageIndex,
            ItemNumber = row.Count > 0 ? row[0].Text : "",
            Description = CleanDescription(description),
            Qty = qty,
            Units = units,
            Price = price,
            Amount = amount,
            IsCredit = credit,
            PrintedUnits = unitWord?.Text ?? "",
            PrintedAmount = totalWord?.Text ?? "",
        };
    }

    private static PageFigures ReadFigures(List<string> rows, int pageIndex)
    {
        string blob = string.Join(" ", rows);
        return new PageFigures(
            pageIndex,
            MatchInt(blob, SkuCount()),
            MatchMoney(blob, CaseCount()),
            MatchInt(blob, UnitCount()),
            MatchMoney(blob, InvoiceTotal()),
            MatchMoney(blob, RoaTotal()));
    }

    // ───────────────────────────────────────────────────────────────── settling the rows

    private static List<RedBullLine> Settle(
        List<RawRow> rows, List<PageFigures> figures, List<ConversionWarning> warnings)
    {
        foreach (var row in rows)
            ResolveBarcode(row, warnings);

        // Every case price the ticket charges somewhere and proves by its own arithmetic. A ticket
        // bills the same price for every product of a size, and it usually bills several, so a row
        // whose two money columns disagree can be settled by whether one of its readings is a rate
        // this ticket demonstrably charges. Grouping by pack size would be wrong: pack 24 covers
        // both the 8.4oz at $40.75 and the 12oz at $53.45.
        var settledRows = rows
            .Where(r => !r.IsCredit && r.Qty is > 0 && r.Price is not null && r.Amount is not null
                        && Math.Abs(r.Qty.Value * r.Price!.Value - r.Amount!.Value) < 0.005m)
            .ToList();
        var confirmedPrices = settledRows.Select(r => r.Price!.Value).ToHashSet();

        // The same rates per individual unit. A credit page prices in units, not cases, and prints
        // that rate rounded to the cent — $2.23 for a 12oz whose real rate is $53.45/24 = $2.2271 —
        // so the exact figure has to come back from the delivery page rather than off the credit row.
        var confirmedUnitRates = settledRows
            .Where(r => r.Pack is > 0)
            .Select(r => r.Price!.Value / r.Pack!.Value)
            .ToList();

        // Strongest evidence first: the total the ticket prints for itself is made of glyphs the
        // rows do not share, so it can arbitrate between two readings of a row in a way that no
        // amount of comparing the rows to each other can.
        var byTotal = SettleByPrintedTotal(rows, figures, confirmedUnitRates, warnings);

        var lines = new List<RedBullLine>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int position = i + 1;
            decimal? amount = byTotal[i] ?? (r.IsCredit
                ? SettleCreditAmount(r, confirmedUnitRates, position, warnings)
                : SettleAmount(r, confirmedPrices, position, warnings));
            int? units = SettleUnits(r, figures, rows, amount, position, warnings);

            int sign = r.IsCredit ? -1 : 1;
            lines.Add(new RedBullLine
            {
                Position = position,
                ItemNumber = r.ItemNumber,
                Description = r.Description.Length > 0
                    ? r.Description
                    : RedBullCatalog.Find(r.Gtin)?.Description ?? "RED BULL",
                Units = units is null ? null : sign * Math.Abs(units.Value),
                Amount = amount is null ? null : sign * Math.Abs(amount.Value),
                Gtin = r.Gtin,
                IsCredit = r.IsCredit,
                PrintedCode = r.PrintedCode,
                PrintedUnits = r.PrintedUnits,
                PrintedAmount = r.PrintedAmount,
            });
        }
        return lines;
    }

    /// <summary>
    /// Settles one row's extended amount. A delivery row offers two readings — the TOTAL column and
    /// QTY x PRICE — and a credit row offers TOTAL against UNITS x the unit price. Agreement settles
    /// it; disagreement is broken only by what the rest of the ticket charged for the same pack, and
    /// otherwise left for the operator.
    /// </summary>
    private static decimal? SettleAmount(
        RawRow r, HashSet<decimal> confirmedPrices, int position, List<ConversionWarning> warnings)
    {
        decimal? fromColumn = r.Amount;
        decimal? fromRow = r.Qty is > 0 && r.Price is not null
            ? decimal.Round(r.Qty.Value * r.Price.Value, 2, MidpointRounding.AwayFromZero)
            : null;

        if (fromColumn is not null && fromRow is not null && Math.Abs(fromColumn.Value - fromRow.Value) < 0.005m)
            return fromColumn;

        if (fromColumn is not null && fromRow is not null && r.Qty is > 0)
        {
            // Both read and they disagree, so one column is damaged. The tie is broken only when
            // exactly one of the two implied case prices is a rate this ticket charges elsewhere
            // and proves by arithmetic there.
            decimal impliedByColumn = fromColumn.Value / r.Qty.Value;
            bool columnKnown = confirmedPrices.Any(p => Math.Abs(p - impliedByColumn) < 0.005m);
            bool priceKnown = r.Price is not null && confirmedPrices.Contains(r.Price.Value);
            if (columnKnown ^ priceKnown)
            {
                decimal settled = columnKnown
                    ? fromColumn.Value
                    : decimal.Round(r.Qty.Value * r.Price!.Value, 2, MidpointRounding.AwayFromZero);
                warnings.Add(new ConversionWarning(position, PdfReasonCodes.LineMathMismatch,
                    $"Line {position}: the price and the amount disagree ({Fmt(r.Price)} x {r.Qty} "
                    + $"is not {Fmt(fromColumn)}). Used {Fmt(settled)}, which matches a rate this "
                    + "ticket charges elsewhere."));
                return settled;
            }
            return null; // ask
        }

        if (fromColumn is not null) return fromColumn;

        if (fromRow is not null)
        {
            warnings.Add(new ConversionWarning(position, PdfReasonCodes.UnreadableLine,
                $"Line {position}: the amount column did not read; used {Fmt(fromRow)} from "
                + $"{r.Qty} x {Fmt(r.Price)}."));
            return fromRow;
        }

        return null; // ask
    }

    /// <summary>
    /// Lets the ticket's own printed total choose between the readings of each row.
    ///
    /// Every row offers at most two amounts — what the TOTAL column says, and what QTY x PRICE (or,
    /// on a credit row, UNITS x PRICE) works out to. Exactly one combination of those readings is
    /// what Red Bull actually billed, and the printed total is the only figure on the page made of
    /// different glyphs from the rows, so it is the only thing that can pick it out. One sample
    /// prints $894.44 against thirteen rows whose two columns disagree; the printed figure
    /// reconstructs all thirteen.
    ///
    /// Where several combinations reach the total, only the rows on which they all agree are
    /// settled — the rest stay open for the operator. Where none does, nothing is settled here and
    /// the weaker per-row rules run instead: a printed total is damaged about as often as a row is,
    /// so failing to reach it is evidence about the total as much as about the rows.
    /// </summary>
    private static decimal?[] SettleByPrintedTotal(
        List<RawRow> rows, List<PageFigures> figures, List<decimal> confirmedUnitRates,
        List<ConversionWarning> warnings)
    {
        var settled = new decimal?[rows.Count];

        var options = new List<long[]>(rows.Count);
        foreach (var r in rows)
        {
            var choices = new List<long>();

            // A returned line is priced per unit at a rate the delivery page states exactly, so
            // where that rate is known it is the only reading offered. Letting a credit row float
            // between its own two readings is what lets the search reach the right grand total
            // through the wrong lines: on the verified ticket, $4.44 and $13.38 add up the same as
            // the real $4.45 and $13.36.
            var rate = r.IsCredit && r.Units is > 0
                ? confirmedUnitRates.Where(x =>
                        (r.Amount is not null && Math.Abs(r.Amount.Value / r.Units.Value - x) <= 0.0105m)
                        || (r.Price is > 0 && Math.Abs(r.Price.Value - x) <= 0.0105m))
                    .Distinct().ToList()
                : [];
            if (rate.Count == 1)
            {
                choices.Add(Cents(r.Units!.Value * rate[0], true));
            }
            else
            {
                if (r.Amount is not null) choices.Add(Cents(r.Amount.Value, r.IsCredit));
                decimal? implied = r.IsCredit
                    ? (r.Units is > 0 && r.Price is > 0 ? r.Units.Value * r.Price.Value : null)
                    : (r.Qty is > 0 && r.Price is > 0 ? r.Qty.Value * r.Price.Value : null);
                if (implied is not null) choices.Add(Cents(implied.Value, r.IsCredit));
            }

            if (choices.Count == 0) return settled;   // a row with nothing to offer sinks the search
            options.Add(choices.Distinct().ToArray());
        }

        foreach (long target in PrintedTargets(rows, figures))
        {
            var agreed = SolveForTarget(options, target);
            if (agreed is null) continue;
            for (int i = 0; i < rows.Count; i++)
            {
                if (agreed[i] is not long cents) continue;
                decimal value = cents / 100m;
                settled[i] = value;
                if (rows[i].Amount is not null && Math.Abs(Math.Abs(rows[i].Amount!.Value) - Math.Abs(value)) >= 0.005m)
                    warnings.Add(new ConversionWarning(i + 1, PdfReasonCodes.LineMathMismatch,
                        $"Line {i + 1}: the amount column reads {Fmt(rows[i].Amount)}; used {Fmt(value)}, "
                        + "which is the reading that makes the ticket's own printed total."));
            }
            return settled;
        }
        return settled;
    }

    /// <summary>
    /// Which totals the ticket claims for itself: the ROA figure, and the per-page INVOICE figures
    /// added up (a credit-only page contributes a negative). Each is tried in turn.
    /// </summary>
    private static IEnumerable<long> PrintedTargets(List<RawRow> rows, List<PageFigures> figures)
    {
        var seen = new HashSet<long>();
        foreach (var f in figures)
            if (f.Roa is > 0m && seen.Add(Cents(f.Roa.Value, false)))
                yield return Cents(f.Roa.Value, false);

        if (figures.All(f => f.Invoice is not null))
        {
            long sum = 0;
            for (int p = 0; p < figures.Count; p++)
            {
                bool creditPage = rows.Count > 0 && rows.Where(r => r.PageIndex == p).All(r => r.IsCredit)
                                  && rows.Any(r => r.PageIndex == p);
                sum += Cents(figures[p].Invoice!.Value, creditPage);
            }
            if (seen.Add(sum)) yield return sum;
        }
    }

    /// <summary>
    /// Returns, per row, the value every combination reaching <paramref name="target"/> agrees on,
    /// or null for a row they disagree about. Null overall when the target cannot be reached.
    /// </summary>
    private static long?[]? SolveForTarget(List<long[]> options, long target)
    {
        int n = options.Count;
        // Reachable sums after the first i rows, and the sums the remaining rows can still make.
        var forward = new HashSet<long>[n + 1];
        forward[0] = [0L];
        for (int i = 0; i < n; i++)
        {
            forward[i + 1] = [];
            foreach (long sofar in forward[i])
                foreach (long choice in options[i])
                    forward[i + 1].Add(sofar + choice);
            if (forward[i + 1].Count > MaxSearchStates) return null;
        }
        if (!forward[n].Contains(target)) return null;

        var backward = new HashSet<long>[n + 1];
        backward[n] = [0L];
        for (int i = n - 1; i >= 0; i--)
        {
            backward[i] = [];
            foreach (long rest in backward[i + 1])
                foreach (long choice in options[i])
                    backward[i].Add(rest + choice);
            if (backward[i].Count > MaxSearchStates) return null;
        }

        var agreed = new long?[n];
        for (int i = 0; i < n; i++)
        {
            var viable = new HashSet<long>();
            foreach (long choice in options[i])
                foreach (long sofar in forward[i])
                    if (backward[i + 1].Contains(target - sofar - choice)) { viable.Add(choice); break; }
            agreed[i] = viable.Count == 1 ? viable.First() : null;
        }
        return agreed;
    }

    /// <summary>Caps the combination search so a pathological ticket cannot hang the app.</summary>
    private const int MaxSearchStates = 200_000;

    private static long Cents(decimal value, bool negative) =>
        (negative ? -1 : 1) * (long)decimal.Round(Math.Abs(value) * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Settles a returned line's amount. The row's own identity here is UNITS x the unit rate, and
    /// the printed rate is only accurate to the cent, so the exact figure comes from whichever rate
    /// the delivery page proved for that pack.
    /// </summary>
    private static decimal? SettleCreditAmount(
        RawRow r, List<decimal> confirmedUnitRates, int position, List<ConversionWarning> warnings)
    {
        // Both readings of this row's unit rate: from the amount column, and from the printed rate.
        var candidates = new List<decimal>();
        if (r.Amount is not null && r.Units is > 0) candidates.Add(r.Amount.Value / r.Units.Value);
        if (r.Price is > 0) candidates.Add(r.Price.Value);
        if (candidates.Count == 0) return null; // ask

        // A rate the delivery page charges, within a cent of what this row reads. A whole cent
        // rather than half of one, because the printed rate is truncated and not rounded: a 12oz
        // whose real rate is $2.2271 prints as $2.22.
        var matches = confirmedUnitRates
            .Where(rate => candidates.Any(c => Math.Abs(c - rate) <= 0.0105m))
            .Distinct()
            .ToList();

        if (matches.Count == 1 && r.Units is > 0)
        {
            decimal exact = decimal.Round(r.Units.Value * matches[0], 2, MidpointRounding.AwayFromZero);
            if (r.Amount is not null && Math.Abs(exact - r.Amount.Value) >= 0.005m)
                warnings.Add(new ConversionWarning(position, PdfReasonCodes.LineMathMismatch,
                    $"Line {position}: the returned amount reads {Fmt(r.Amount)} but {r.Units} units at "
                    + $"this ticket's rate is {Fmt(exact)}. Used {Fmt(exact)}."));
            return exact;
        }

        // No delivery page to check against (a credit-only ticket). Fall back to the row's own two
        // readings, which have to agree to within the cent the printed rate is rounded to.
        if (r.Amount is not null && r.Price is > 0 && r.Units is > 0)
        {
            decimal implied = decimal.Round(r.Units.Value * r.Price.Value, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(implied - r.Amount.Value) <= 0.005m * r.Units.Value + 0.005m)
                return r.Amount;
            return null; // ask
        }

        return r.Amount; // only one reading, and nothing to contradict it
    }

    /// <summary>
    /// Settles one row's unit count. UNITS is the one figure the money cannot check — misread it and
    /// the unit cost absorbs the difference, so the invoice still balances to the penny while the
    /// store's stock is wrong. The pack size is the check, and the page's own unit count breaks ties.
    /// </summary>
    private static int? SettleUnits(
        RawRow r, List<PageFigures> figures, List<RawRow> rows,
        decimal? amount, int position, List<ConversionWarning> warnings)
    {
        // A credit row prints a fraction of a case in QTY, so the pack identity does not apply. Its
        // unit count is checked against the money instead.
        if (r.IsCredit)
        {
            if (r.Units is > 0) return r.Units;
            if (amount is not null && r.Price is > 0)
            {
                int rebuilt = (int)decimal.Round(Math.Abs(amount.Value) / r.Price.Value, 0, MidpointRounding.AwayFromZero);
                if (rebuilt > 0)
                {
                    warnings.Add(new ConversionWarning(position, PdfReasonCodes.UnreadableLine,
                        $"Line {position}: the units column did not read; used {rebuilt} from "
                        + $"{Fmt(amount)} at {Fmt(r.Price)} each."));
                    return rebuilt;
                }
            }
            return null; // ask
        }

        int? fromPack = r.Pack is > 0 && r.Qty is > 0 && decimal.Truncate(r.Qty.Value) == r.Qty.Value
            ? (int)(r.Qty.Value * r.Pack.Value)
            : null;

        if (r.Units is null or <= 0)
        {
            if (fromPack is null) return null; // ask
            warnings.Add(new ConversionWarning(position, PdfReasonCodes.UnreadableLine,
                $"Line {position}: the units column did not read; used {fromPack} from "
                + $"{r.Qty} case(s) of {r.Pack}."));
            return fromPack;
        }

        if (fromPack is null) return r.Units;      // no pack on record to check it against
        if (r.Units == fromPack) return r.Units;

        // Both readable and disagreeing. The page prints its own unit count; whichever reading makes
        // that figure is the right one, and if neither does, a person decides.
        var page = figures.FirstOrDefault(f => f.PageIndex == r.PageIndex);
        if (page?.UnitsDelivered is int printed)
        {
            int othersRead = rows
                .Where(x => !ReferenceEquals(x, r) && !x.IsCredit && x.PageIndex == r.PageIndex)
                .Sum(x => x.Units ?? 0);
            if (othersRead + fromPack.Value == printed)
            {
                warnings.Add(new ConversionWarning(position, PdfReasonCodes.QtyMismatch,
                    $"Line {position}: the units column read {r.Units} but {r.Qty} case(s) of {r.Pack} "
                    + $"is {fromPack}. Used {fromPack}, which is what makes the ticket's own count of {printed}."));
                return fromPack;
            }
            if (othersRead + r.Units.Value == printed) return r.Units;
        }
        return null; // ask
    }

    private static void CheckAgainstPrintedFigures(
        IReadOnlyList<RedBullLine> lines, List<PageFigures> figures, List<ConversionWarning> warnings)
    {
        // The money is checked again in ConversionService once the operator has filled in whatever
        // could not be read, so it is not repeated here. What is worth saying now is anything about
        // the unit counts, which no later step revisits.
        if (lines.Any(l => l.NeedsAnything)) return;

        int units = lines.Sum(l => l.Units!.Value);
        int? printedUnits = figures.Sum(f => f.UnitsDelivered ?? 0) is var pu && figures.Any(f => f.UnitsDelivered is not null)
            ? pu : null;
        if (printedUnits is not null && lines.All(l => !l.IsCredit) && printedUnits != units)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The lines come to {units} units but the ticket prints {printedUnits}."));
    }

    // ─────────────────────────────────────────────────────────────────────── the barcode

    /// <summary>
    /// Works out which product a row is for. The ladder runs strongest evidence first and stops as
    /// soon as one rung answers; a row that reaches the bottom is handed to the operator.
    /// </summary>
    private static void ResolveBarcode(RawRow r, List<ConversionWarning> warnings)
    {
        var candidates = r.CodeCandidates.Distinct(StringComparer.Ordinal).ToList();

        // 1. A reading that satisfies its own check digit and carries Red Bull's prefix.
        foreach (string c in candidates)
            if (Upc.IsValidUpcA12(c) && c.StartsWith(RedBullCatalog.Prefix, StringComparison.Ordinal))
            {
                r.Gtin = c;
                r.Pack = RedBullCatalog.Find(c)?.Pack ?? PackFromRow(r);
                return;
            }

        // 2. A single-digit repair. For a code whose prefix already reads correctly there are six of
        //    these, so the catalogue picks; where only one repair exists at all, the check digit has
        //    already picked.
        var repairs = SingleDigitRepairs(candidates);
        var known = repairs.Where(x => RedBullCatalog.Find(x) is not null).ToList();
        if (known.Count == 1)
        {
            // Two independent things agree here — the candidate satisfies its own check digit, and
            // it is a product this vendor is on record as selling. Nothing is left for a person to
            // decide, so this does not warn. It would otherwise warn on almost every ticket: one
            // whole 300 dpi sample reads its entire barcode column as "611260..." for "611269...",
            // and warnings that fire on every conversion teach people to click past them.
            Accept(r, known[0], quiet: true, warnings);
            return;
        }
        if (repairs.Count == 1)
        {
            Accept(r, repairs[0], quiet: false, warnings);
            return;
        }

        // 3. A catalogued product two digits from the reading that also agrees on pack size. Two
        //    digits is further than Bimbo is ever allowed to go, and safe here only because the
        //    candidate still has to satisfy its own check digit and match the row's arithmetic.
        int? rowPack = PackFromRow(r);
        var near = RedBullCatalog.All
            .Where(e => candidates.Any(c => Differences(e.Gtin, c) <= 2))
            .Where(e => rowPack is null || e.Pack == rowPack)
            .ToList();
        if (near.Count == 1)
        {
            Accept(r, near[0].Gtin, quiet: false, warnings);
            return;
        }

        // 4. Red Bull's own item number, which is a second encoding of the same product.
        var byItem = RedBullCatalog.FindByItemNumber(Digits(r.ItemNumber));
        if (byItem is not null && (rowPack is null || byItem.Pack == rowPack))
        {
            Accept(r, byItem.Gtin, quiet: false, warnings);
            return;
        }

        r.Gtin = null;
        r.Pack = rowPack;
    }

    /// <summary>
    /// Takes a repaired barcode. <paramref name="quiet"/> is for a repair two independent checks
    /// already agree on; anything less certain says so, because it changes which product the store
    /// is billed for and nothing downstream would notice.
    /// </summary>
    private static void Accept(RawRow r, string gtin, bool quiet, List<ConversionWarning> warnings)
    {
        var entry = RedBullCatalog.Find(gtin);
        if (!quiet)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"The scan read a barcode as \"{r.PrintedCode}\", which is not a valid one; used {gtin}"
                + (entry is not null ? $" ({entry.Description})." : ".")
                + " Check that line against the paper."));
        r.Gtin = gtin;
        r.Pack = entry?.Pack ?? PackFromRow(r);
    }

    private static int? PackFromRow(RawRow r)
    {
        if (r.IsCredit || r.Qty is not > 0 || r.Units is not > 0) return null;
        if (decimal.Truncate(r.Qty.Value) != r.Qty.Value) return null;
        int cases = (int)r.Qty.Value;
        if (cases == 0 || r.Units.Value % cases != 0) return null;
        int pack = r.Units.Value / cases;
        return KnownPacks.Contains(pack) ? pack : null;
    }

    private static List<string> SingleDigitRepairs(IEnumerable<string> candidates)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string c in candidates)
        {
            var chars = c.ToCharArray();
            for (int p = 0; p < chars.Length; p++)
            {
                char was = chars[p];
                for (char d = '0'; d <= '9'; d++)
                {
                    if (d == was) continue;
                    chars[p] = d;
                    string candidate = new(chars);
                    if (Upc.IsValidUpcA12(candidate)
                        && candidate.StartsWith(RedBullCatalog.Prefix, StringComparison.Ordinal))
                        found.Add(candidate);
                }
                chars[p] = was;
            }
        }
        return found.ToList();
    }

    private static int Differences(string a, string b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    /// <summary>
    /// Twelve-digit runs anchored on Red Bull's prefix. Anchoring on five digits rather than the
    /// full six matters: one 300 dpi sample reads every barcode on the page as "611260..." because
    /// the scanner turned that one 9 into a 0.
    /// </summary>
    private static List<string> CodeCandidates(string text)
    {
        string digits = Digits(text);
        var found = new List<string>();
        for (int i = 0; i + 12 <= digits.Length; i++)
            if (string.CompareOrdinal(digits, i, RedBullCatalog.Prefix, 0, 5) == 0)
                found.Add(digits.Substring(i, 12));
        return found;
    }

    // ────────────────────────────────────────────────────────────────────────────── date

    private static (DateOnly? Date, DateOnly? Suggested) ReadDate(
        IReadOnlyList<PdfPage> pages, List<ConversionWarning> warnings)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var page in pages)
            foreach (var row in page.Rows)
            {
                string text = string.Join(" ", row.Select(x => x.Text));
                var m = DateStamp().Match(text);
                if (!m.Success) continue;
                if (!TryDate(m.Groups["m"].Value, m.Groups["d"].Value, m.Groups["y"].Value, out var d))
                    continue;

                if (d <= today) return (d, null);

                // A ticket cannot be dated in the future. Red Bull's scans read 5 as 6 all over the
                // page (a $75320 ZIP comes back as 76320), so a year one ahead is almost always this
                // - but "almost always" is not good enough to post a date on, so it is offered.
                var earlier = d.AddYears(-1);
                warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoDate,
                    $"The ticket's date reads {d:MM/dd/yyyy}, which is in the future. "
                    + $"You will be asked to confirm it; {earlier:MM/dd/yyyy} is offered as a suggestion."));
                return (null, earlier);
            }
        return (null, null);
    }

    private static bool TryDate(string month, string day, string year, out DateOnly date)
    {
        date = default;
        return int.TryParse(month, out int mm) && int.TryParse(day, out int dd) && int.TryParse(year, out int yy)
            && mm is >= 1 and <= 12 && dd >= 1 && yy is >= 2000 and <= 2100
            && dd <= DateTime.DaysInMonth(yy, mm)
            && DateOnly.TryParse($"{yy:0000}-{mm:00}-{dd:00}", CultureInfo.InvariantCulture, out date);
    }

    // ───────────────────────────────────────────────────────── what the operator types back

    internal static string ParseTypedGtin(string typed, RedBullLine line)
    {
        string digits = Upc.DigitsOnly(typed);
        if (digits.Length == 11) digits = Upc.AppendCheckDigit(digits);
        if (digits.Length is not (12 or 13))
            throw new EdiConversionException(PdfReasonCodes.BarcodeRequired,
                $"\"{typed}\" is not a barcode. Line {line.Position} needs the 12 digits printed under "
                + "the product description.");
        return digits;
    }

    internal static int ParseTypedUnits(string typed, RedBullLine line)
    {
        string digits = new((typed ?? "").Where(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(digits, out int units) || units <= 0)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"\"{typed}\" is not a unit count. Line {line.Position} needs the number in the "
                + "UNITS column.");
        return line.IsCredit ? -units : units;
    }

    internal static decimal ParseTypedAmount(string typed, RedBullLine line, int units)
    {
        string cleaned = new((typed ?? "").Where(c => char.IsAsciiDigit(c) || c is '.' or '-').ToArray());
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount == 0m)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"\"{typed}\" is not an amount. Line {line.Position} needs the dollar figure in the "
                + "TOTAL column.");
        return line.IsCredit ? -Math.Abs(amount) : Math.Abs(amount);
    }

    // ─────────────────────────────────────────────────────────────────────────── plumbing

    private static PdfPositionedWord? Nearest(
        IReadOnlyList<PdfPositionedWord> row, double centre, double tolerance, Func<string, bool> accepts)
    {
        PdfPositionedWord? best = null;
        double bestDistance = double.MaxValue;
        foreach (var word in row)
        {
            if (!accepts(word.Text)) continue;
            double distance = Math.Abs(word.Centre - centre);
            if (distance < bestDistance) { bestDistance = distance; best = word; }
        }
        return bestDistance <= tolerance ? best : null;
    }

    private static List<List<double>> Cluster(List<double> values, double gap)
    {
        var clusters = new List<List<double>>();
        foreach (double v in values.OrderBy(x => x))
        {
            if (clusters.Count > 0 && v - clusters[^1][^1] <= gap) clusters[^1].Add(v);
            else clusters.Add([v]);
        }
        return clusters;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Reads a money column. Accepts a comma for the decimal point (the scanner substitutes one
    /// often enough to matter) and reports brackets, which is how the ticket marks a credit.
    /// </summary>
    private static bool TryMoney(string token, out decimal value, out bool bracketed)
    {
        value = 0m;
        bracketed = Bracketed(token);
        var m = MoneyToken().Match(token.Replace(" ", ""));
        if (!m.Success) return false;
        return decimal.TryParse(
            m.Groups["w"].Value.Replace(",", "") + "." + m.Groups["c"].Value,
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool Bracketed(string token)
    {
        string t = token.Trim();
        return t.StartsWith('(') || t.EndsWith(')');
    }

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    /// <summary>Drops the barcode debris the scanner leaves in the middle of a description.</summary>
    private static string CleanDescription(string text)
    {
        string cleaned = DescriptionDebris().Replace(text, " ");
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length > 40 ? cleaned[..40].Trim() : cleaned;
    }

    private static int? MatchInt(string blob, Regex pattern)
    {
        var m = pattern.Match(blob);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : null;
    }

    private static decimal? MatchMoney(string blob, Regex pattern)
    {
        var m = pattern.Match(blob);
        return m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
            NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) ? v : null;
    }

    private static string Fmt(decimal? v) => v is null ? "?" : v.Value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    [GeneratedRegex(@"^\(?\$?(?<w>\d[\d,]*)[.,](?<c>\d\d)\)?$")]
    private static partial Regex MoneyToken();

    [GeneratedRegex(@"^\(?\d{1,4}\)?$")]
    private static partial Regex CountLike();

    [GeneratedRegex(@"^\(?\d{1,3}(?:\.\d{1,2})?\)?$")]
    private static partial Regex QtyLike();

    [GeneratedRegex(@"\d{10,}")]
    private static partial Regex LongDigits();

    /// <summary>
    /// The start of the totals block. Matched loosely on purpose — "Units Picked Up" comes back as
    /// "Unite Picked Up" and "Subtotal" as "Su0tolal" — because a totals row mistaken for a product
    /// row becomes a line item, and a line item nobody bought is money invented out of nothing.
    /// </summary>
    /// Note "TOTALS" is matched with word boundaries and "TOTAL" is not matched at all: the column
    /// heading above the first product row ends in TOTAL, and stopping there would find no products.
    [GeneratedRegex(@"Pick\w*\s*Up|SKU|Deliver|\bTOTALS\b|Signature|Billing|Check\s*[:;#]|Sub\w?total"
        + @"|Su0tolal|Applied|DISCOUNT|DEPOSIT|Involce\s*Total|Invoice\s*Total", RegexOptions.IgnoreCase)]
    private static partial Regex EndOfProducts();

    [GeneratedRegex(@"SKU.{0,3}\s(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SkuCount();

    [GeneratedRegex(@"Cases?\s*Deliver\w*\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CaseCount();

    [GeneratedRegex(@"Unit[se]?\s*Deliver\w*\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitCount();

    [GeneratedRegex(@"(?:INVOICE|Invoice\s*Total|Involce\s*Total|Subtotal|Su0tolal)\s*\(?\$?([\d,]+\.\d\d)",
        RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceTotal();

    [GeneratedRegex(@"(?:ROA|RDA|BOA|RCA|R0A|nbA|I1OA|904)\s*[:;]?\s*\$?([\d,]+\.\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex RoaTotal();

    /// <summary>
    /// The date stamp beside the salesman's name. The separators are read as digits often enough
    /// ("11124120261:16 PM" for 11/24/2025 1:16 PM) that they are matched as one character of
    /// anything rather than as a slash.
    /// </summary>
    [GeneratedRegex(@"(?<![\d/])(?<m>\d{1,2}).(?<d>\d{1,2}).(?<y>\d{4})\s*\d{1,2}\s*[:;.]\s*\d{2}")]
    private static partial Regex DateStamp();

    [GeneratedRegex(@"[|Il1]{3,}|\bI+\b")]
    private static partial Regex DescriptionDebris();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();

    /// <summary>Two letters together - enough to tell a description from barcode-bar debris.</summary>
    [GeneratedRegex(@"[A-Za-z]{2}")]
    private static partial Regex DescriptionWord();
}
