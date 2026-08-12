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

    /// <summary>
    /// Where this row is printed, so a question about it can show the paper instead of describing
    /// it. <see cref="LineNumber"/> is the priced row; <see cref="LastLineNumber"/> runs down to the
    /// barcode Red Bull prints beneath it; <see cref="CodeLineNumber"/> is that barcode's own line,
    /// which is the one to mark when the barcode is what is being asked for.
    /// </summary>
    public required int LineNumber { get; init; }
    public required int LastLineNumber { get; init; }
    public required int CodeLineNumber { get; init; }

    /// <summary>The barcode exactly as the scan wrote it, used to find it on its line.</summary>
    public required string CodeToken { get; init; }

    /// <summary>The line holding the ticket's own column names, shown as the legend over a snip.</summary>
    public required int HeaderLineNumber { get; init; }

    /// <summary>
    /// 1-based page this row is on, for a row that came off the picture and so has no line number.
    /// With nothing to cut a band around, the whole page is shown instead, and that is only any use
    /// if it is the right page. 0 where the text layer read the row - it has a line number.
    /// </summary>
    public int Page { get; init; }

    /// <summary>Whether that page was scanned the wrong way up, so its picture needs turning.</summary>
    public bool PageUpsideDown { get; init; }


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

    /// <summary>
    /// The `Units Delivered` figure the ticket prints for itself, added up across its pages, or null
    /// where the scan destroyed it.
    ///
    /// This is the only check that can see a **missing line**. Money cannot: a row the scan lost
    /// takes its dollars with it, so the rest still adds up against a printed total that is itself
    /// suspect on this vendor. The unit count is made of different glyphs again and does not move
    /// when an amount is misread, so a disagreement here means a row is gone or a quantity is wrong.
    /// Checked after the operator has answered, in <see cref="ConversionService"/>.
    /// </summary>
    public required int? PrintedUnits { get; init; }

    public required IReadOnlyList<RedBullLine> Lines { get; init; }

    /// <summary>
    /// Builds the final invoice, calling <paramref name="ask"/> for each figure the scan destroyed.
    /// The invoice total is the sum of the lines, never the printed figure — see
    /// <see cref="RedBullPdfParser"/> for why that is the right way round on this vendor.
    /// </summary>
    /// <param name="recheck">
    /// Positions whose amount must be asked about even though the scan read one.
    ///
    /// This is how a ticket that does not add up gets settled. A row whose PRICE and TOTAL columns
    /// hold the same misreading agrees with itself and is taken silently — one sample prints $54.70
    /// and the scanner renders `$64.70` in both columns — so nothing on the page can find it. The
    /// ticket's own total says *something* is wrong without saying what, and the rows nobody has
    /// looked at are the only candidates. Asking about them costs a question and finds it.
    /// </param>
    public Invoice ToInvoice(
        string invoiceNumber, DateOnly date, Func<RedBullLine, LineField, string> ask,
        IReadOnlySet<int>? recheck = null)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var lines = new List<InvoiceLine>(Lines.Count);
        foreach (var l in Lines)
        {
            string gtin = l.Gtin ?? RedBullPdfParser.ParseTypedGtin(ask(l, LineField.Barcode), l);
            int units = l.Units ?? RedBullPdfParser.ParseTypedUnits(ask(l, LineField.Units), l);
            decimal amount = l.Amount is decimal read && recheck?.Contains(l.Position) != true
                ? read
                : RedBullPdfParser.ParseTypedAmount(ask(l, LineField.Amount), l, units);

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
            // The date the operator confirmed. It is typed on every invoice now, with what the scan
            // read offered in the box, because a plausible wrong date is what this scanner produces
            // and nothing in the file can catch one - see InvoiceDetailsRequest.NeedsDate.
            Date = date,
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

    /// <summary>
    /// Parses every page of the PDF into one ticket.
    /// </summary>
    /// <param name="scannedBarcodes">
    /// The barcodes read off the printed bars by <see cref="PdfBarcodeReader"/>, when they could be
    /// had. Consulted only where the page has otherwise run out of ways to identify a product — see
    /// <see cref="ResolveBarcode"/> for why it is last and not first.
    /// </param>
    /// <param name="vision">
    /// The ticket as a vision model read it off the picture, when one was available. A second reader
    /// of the same paper, made of different mistakes from the OCR — which is what makes agreement
    /// between them worth something, and what lets a row that reconciles with itself be caught. An
    /// empty reading leaves the parse exactly as it was.
    /// </param>
    public static RedBullTicket Parse(
        IReadOnlyList<PdfPage> pages,
        List<ConversionWarning> warnings,
        IReadOnlyList<PdfBarcode>? scannedBarcodes = null,
        VisionReading? vision = null)
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

        var seen = vision ?? VisionReading.Empty;

        // A sheet that went through slightly crooked, or came back with a pen mark across it, reaches
        // the OCR as nothing at all — and that is exactly the page a person can read at a glance. So
        // where the text layer found no rows and the picture did, the picture IS the invoice.
        //
        // Those rows have no second reader behind them, and they are not treated as though they do.
        // What checks them is the paper's own arithmetic: QTY x PRICE = TOTAL over three figures read
        // separately, and the total the ticket prints for itself. A row that cannot pass those is
        // shown to the operator exactly as it would be on any other ticket.
        bool fromThePicture = rows.Count == 0 && seen.Rows.Count > 0;
        if (fromThePicture)
        {
            rows = RowsFromThePicture(seen);
            figures.Add(new PageFigures(0, null, null, seen.PrintedUnits, seen.PrintedTotal, null));
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"Nothing could be read from the text in this PDF, so all {rows.Count} lines were read "
                + "off the picture of the page instead. Check them against the paper before sending."));
        }

        if (rows.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No product lines could be read from this Red Bull ticket. Check that the right vendor "
                + "is selected above the file list, and that the page went through the scanner the "
                + "right way up and whole - one of the store's scans came back upside down, which "
                + "reads as a full page of nothing.");

        // Both readers found rows but not the same number of them. The text layer has lost at least
        // one, and a lost row takes its money with it, so no total on the page can notice. Said out
        // loud rather than patched: the rows that WERE read are still each backed by two readings,
        // and swapping the lot for a single-reader list to recover one row would be a bad trade.
        if (seen.Rows.Count > rows.Count)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoLineItems,
                $"The text in this PDF gives {rows.Count} lines but the picture of the page shows "
                + $"{seen.Rows.Count}. A line is probably missing - count them against the paper "
                + "before sending."));

        var (date, suggested) = ReadDate(pages, warnings);

        // Rows that came off the picture must not then be "corroborated" by the picture — that is one
        // reader agreeing with itself, and it would wave every figure through unchecked.
        var second = fromThePicture ? VisionReading.Empty : seen;
        var lines = Settle(rows, figures, warnings, scannedBarcodes ?? [], second);

        decimal known = lines.Where(l => l.Amount is not null).Sum(l => l.Amount!.Value);
        decimal? printed = figures.Select(f => f.Roa ?? f.Invoice).FirstOrDefault(v => v is not null);

        return new RedBullTicket
        {
            Date = date,
            SuggestedDate = suggested,
            Total = known,
            PrintedTotal = printed,
            PrintedTotals = PrintedTargets(rows, figures).Select(c => c / 100m).ToList(),
            PrintedUnits = figures.Any(f => f.UnitsDelivered is not null)
                ? figures.Sum(f => f.UnitsDelivered ?? 0)
                : null,
            Lines = lines,
        };
    }

    // ───────────────────────────────────────────────────────────────────── page reading

    /// <summary>One product row exactly as the scan presented it, before anything is settled.</summary>
    private sealed class RawRow
    {
        /// <summary>0-based page this row came off, so a page's printed figures can be matched to it.</summary>
        public required int PageIndex { get; init; }
        /// <summary>Whether that page was scanned the wrong way up. Only ever set off the picture.</summary>
        public bool PageUpsideDown { get; init; }
        public required string ItemNumber { get; init; }
        public required string Description { get; init; }
        public required decimal? Qty { get; init; }
        public required int? Units { get; init; }
        public required decimal? Price { get; init; }
        public required decimal? Amount { get; init; }
        public required bool IsCredit { get; init; }
        public required string PrintedUnits { get; init; }
        public required string PrintedAmount { get; init; }

        /// <summary>
        /// What the DEP / DISC / SUGAR columns take off this row, added up.
        ///
        /// Zero on every recent ticket, which is why the row identity was `QTY x PRICE = TOTAL` for
        /// so long. The store's older invoices carry real per-case discounts — one prints
        /// `$40.75 ... $1.00 ... $39.75` — and on those the old identity fails on every discounted
        /// row, so every one went to the operator. The ticket corroborates this itself: the per-row
        /// figures on sample 2013602330 add up to the `DISCOUNT $28.50` it prints at the bottom.
        /// </summary>
        public required decimal Adjustments { get; init; }
        /// <summary>
        /// What a vision model read on this row, where one was consulted and its reading could be
        /// tied here beyond doubt. Null is the normal case — no key configured, no network, or a row
        /// the alignment would not commit to — and it must leave every rule below unchanged.
        /// </summary>
        public VisionRow? Vision { get; set; }

        public List<string> CodeCandidates { get; } = [];
        public string PrintedCode { get; set; } = "";
        public string? Gtin { get; set; }
        public int? Pack { get; set; }

        /// <summary>Where this row is printed, so its picture can be cut out of the scan.</summary>
        public required int LineNo { get; init; }

        /// <summary>The last line belonging to it — Red Bull prints the barcode underneath.</summary>
        public int LastLineNo { get; set; }

        /// <summary>The line the barcode is printed on, which is not the priced row.</summary>
        public int CodeLineNo { get; set; }

        /// <summary>The barcode exactly as the scan wrote it, so it can be found on that line.</summary>
        public string CodeToken { get; set; } = "";

        /// <summary>The column names printed above this row's page.</summary>
        public int HeaderLineNo { get; set; }
    }

    private sealed record PageFigures(
        int PageIndex, int? Skus, decimal? Cases, int? UnitsDelivered, decimal? Invoice, decimal? Roa);

    /// <summary>
    /// Turns a reading taken off the picture into rows, for a PDF whose text layer gave up nothing.
    ///
    /// **Line number 0 throughout, deliberately.** These rows have no place on the page as far as the
    /// text layer is concerned, so no question about one can cut its picture out — and a line number
    /// invented here would land on whatever stray text the OCR did manage, putting a real row of a
    /// real invoice on screen with the wrong figure marked on it. A question with no picture is worth
    /// having; a question with the wrong picture is not.
    /// </summary>
    private static List<RawRow> RowsFromThePicture(VisionReading seen)
    {
        var built = new List<RawRow>(seen.Rows.Count);
        foreach (var v in seen.Rows)
        {
            var row = new RawRow
            {
                PageIndex = Math.Max(0, v.Page - 1),
                PageUpsideDown = v.PageUpsideDown,
                ItemNumber = v.ItemNumber,
                Description = v.Description,
                Qty = v.Qty,
                Units = v.Units,
                Price = v.Price,
                Amount = v.Amount,
                IsCredit = v.IsCredit,
                PrintedUnits = v.Units?.ToString(CultureInfo.InvariantCulture) ?? "",
                PrintedAmount = v.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                // The DEP/DISC/SUGAR columns are not asked for and so are not assumed either. A row
                // carrying a real discount will fail QTY x PRICE = TOTAL and go to the operator,
                // which is the right outcome for a figure nobody read.
                Adjustments = 0m,
                LineNo = 0,
            };

            if (!string.IsNullOrEmpty(v.Barcode))
            {
                row.CodeCandidates.Add(v.Barcode);
                row.PrintedCode = v.Barcode;
            }

            built.Add(row);
        }
        return built;
    }

    private static List<RawRow> ReadPage(PdfPage page, int pageIndex, out PageFigures figures)
    {
        double w = page.Width;
        figures = new PageFigures(pageIndex, null, null, null, null, null);
        var found = new List<RawRow>();

        // Only the rows above the totals block calibrate the columns. The block underneath prints
        // money of its own — DISCOUNT, INVOICE, DEPOSIT, TAX, TOTAL DUE, all in one narrow stack —
        // and on a short ticket that stack is the densest column on the page, so calibrating off the
        // whole page hands PRICE and TOTAL to a set of figures no product row is printed in.
        var productRegion = page.Rows
            .TakeWhile(r => !EndOfProducts().IsMatch(string.Join(" ", r.Select(x => x.Text))))
            .ToList();

        // The money columns are calibrated from the money on those rows rather than from the column
        // headings, which are OCR debris ("PflICE", "TdrAo."). Their positions survive; their text
        // does not.
        var moneyCentres = new List<double>();
        foreach (var row in productRegion)
            foreach (var word in row)
                if (word.Centre > w * MoneyColumnStart && TryMoney(word.Text, out _, out _))
                    moneyCentres.Add(word.Centre);

        // Three readings stacked at the same x is what makes a column a column rather than a stray
        // token. A ticket for one or two SKUs never has three, though, and refusing those refused a
        // real delivery: two of the store's samples are a single-SKU drop. So where the page cannot
        // offer three, every column found in the product region is taken — which is safe here in a
        // way it would not be page-wide, because the totals block is no longer in the running.
        var columns = Cluster(moneyCentres, w * 0.03);
        var clusters = columns.Where(c => c.Count >= 3).ToList();
        if (clusters.Count < 2) clusters = columns;
        if (clusters.Count < 2) return found;
        double priceCentre = clusters[0].Average();
        double totalCentre = clusters[^1].Average();

        // QTY and UNITS are the last two small numbers before the description. Taking the last two
        // rather than the first two is what survives the ID column splitting in half ("11 136463"),
        // and QTY has to allow a decimal because a credit page bills a fraction of a case (0.04).
        var qtyCentres = new List<double>();
        var unitCentres = new List<double>();
        foreach (var row in productRegion)
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
        int headerLineNo = 0;
        IReadOnlyList<PdfPositionedWord> previousRow = [];
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
                // The column names are the row directly above the first product, and only there:
                // Red Bull's headings are OCR debris ("PflICE", "IDEAL"), so they cannot be found by
                // reading them. A row carrying money is a product line, not a heading — that is the
                // guard against a damaged first product shifting the legend onto a real row.
                if (headerLineNo == 0 && found.Count == 0 && pending is null
                    && previousRow.Count > 0 && !previousRow.Any(x => TryMoney(x.Text, out _, out _)))
                    headerLineNo = previousRow.Max(x => x.LineNumber);

                if (pending is not null) found.Add(pending);
                pending = looksLikeProduct
                    ? BuildRow(row, w, qtyCentre, unitCentre, priceCentre, totalWord, pageIndex)
                    : BuildRow([], w, qtyCentre, unitCentre, priceCentre, null, pageIndex, LineSpan(row));
            }
            previousRow = row;

            // Whether or not this row started a product, it may carry that product's barcode. The
            // code is shown back to the operator when one has to be typed, so it is taken from the
            // word that looks most like a barcode rather than from the whole row - the bars beside
            // it come through the text layer as runs of 1s and would be nonsense to show anyone.
            if (pending is not null)
            {
                pending.CodeCandidates.AddRange(codes);
                if (pending.PrintedCode.Length == 0)
                {
                    var printed = row.FirstOrDefault(x => Digits(x.Text).Length is >= 11 and <= 13);
                    if (printed is not null)
                    {
                        pending.PrintedCode = Digits(printed.Text);
                        // The token as written, not just its digits: the marker on the picture is
                        // placed by finding this exact word on the line, and a scan that put a stray
                        // mark inside the code ("6112 69321210") writes a token the digits alone
                        // would no longer match.
                        pending.CodeToken = printed.Text;
                        pending.CodeLineNo = printed.LineNumber;
                    }
                }

                // Red Bull prints the barcode and its bars on the line *under* the priced row, so a
                // row's picture is not finished until the lines below it stop belonging to it.
                pending.LastLineNo = Math.Max(pending.LastLineNo, LineSpan(row).Last);
            }
        }
        if (pending is not null) found.Add(pending);

        foreach (var r in found) r.HeaderLineNo = headerLineNo;

        figures = ReadFigures(figureRows, pageIndex);
        return found;
    }

    /// <summary>The first and last line number the words of a row sit on — normally the same, but a
    /// row the scan's skew split in two spans both halves.</summary>
    private static (int First, int Last) LineSpan(IReadOnlyList<PdfPositionedWord> row)
    {
        var numbered = row.Where(x => x.LineNumber > 0).ToList();
        return numbered.Count == 0 ? (0, 0) : (numbered.Min(x => x.LineNumber), numbered.Max(x => x.LineNumber));
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
        PdfPositionedWord? totalWord, int pageIndex, (int First, int Last)? lineSpan = null)
    {
        var qtyWord = Nearest(row, qtyCentre, w * ColumnTolerance, t => QtyLike().IsMatch(t));
        var unitWord = Nearest(row, unitCentre, w * ColumnTolerance, t => CountLike().IsMatch(t));
        var priceWord = Nearest(row, priceCentre, w * ColumnTolerance, t => TryMoney(t, out _, out _));

        // Everything in the money columns between PRICE and TOTAL: DEP, DISC and SUGAR. They are
        // taken as one figure rather than three because only their sum matters to the row, and
        // because which of the three a token belongs to is exactly the sort of thing a crooked scan
        // gets wrong.
        decimal adjustments = 0m;
        if (priceWord is not null && totalWord is not null)
            foreach (var x in row)
                if (x.Left > priceWord.Right && x.Right < totalWord.Left
                    && TryMoney(x.Text, out decimal middle, out _))
                    adjustments += middle;

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

        var span = lineSpan ?? LineSpan(row);
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
            Adjustments = adjustments,
            LineNo = span.First,
            LastLineNo = span.Last,
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

    /// <summary>
    /// A note about one row, headed by the item and carrying where that row is printed.
    ///
    /// The shape is "&lt;item&gt; - &lt;what happened&gt;" because the "Check these lines" window splits it
    /// there to head each entry, and because the same sentence has to stand on its own in the file
    /// list and in the email, where there is no heading. Short on purpose: this is read standing up,
    /// holding paper, and a second sentence is a sentence that does not get read.
    /// </summary>
    private static ConversionWarning Note(
        RawRow r, string reasonCode, string what, int? lineNo = null) =>
        new(lineNo is > 0 ? lineNo.Value : r.LineNo, reasonCode, $"{Name(r)} - {what}");

    /// <summary>What to call the row: its description, else Red Bull's own item number.</summary>
    private static string Name(RawRow r) =>
        r.Description.Length > 0 ? r.Description
        : r.ItemNumber.Length > 0 ? r.ItemNumber
        : "This line";

    private static List<RedBullLine> Settle(
        List<RawRow> rows, List<PageFigures> figures, List<ConversionWarning> warnings,
        IReadOnlyList<PdfBarcode> scanned, VisionReading vision)
    {
        // Tie the second reader's rows to ours before anything is settled, so every rule below can
        // ask what the picture said about the row it is looking at. The tie is strict and often
        // declines — see VisionReading.AlignTo for why a wrong one would be worse than none.
        var aligned = vision.AlignTo(
            [.. rows.Select(r => new VisionAnchor(r.ItemNumber, r.Units, r.Amount))]);
        for (int i = 0; i < rows.Count; i++) rows[i].Vision = aligned[i];

        foreach (var row in rows)
            ResolveBarcode(row, warnings, scanned);

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
        var byTotal = SettleByPrintedTotal(rows, figures, confirmedUnitRates);

        var lines = new List<RedBullLine>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int position = i + 1;
            decimal? amount = byTotal[i] ?? (r.IsCredit
                ? SettleCreditAmount(r, confirmedUnitRates)
                : SettleAmount(r, confirmedPrices, warnings));
            int? units = SettleUnits(r, figures, rows, warnings);

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
                LineNumber = r.LineNo,
                LastLineNumber = Math.Max(r.LineNo, r.LastLineNo),
                CodeLineNumber = r.CodeLineNo,
                CodeToken = r.CodeToken,
                HeaderLineNumber = r.HeaderLineNo,
                Page = r.LineNo > 0 ? 0 : r.PageIndex + 1,
                PageUpsideDown = r.PageUpsideDown,
            });
        }
        return lines;
    }

    /// <summary>
    /// Settles one row's extended amount, which it may only ever do by **confirming what the ticket
    /// actually prints** — never by working a figure out.
    ///
    /// A delivery row offers two readings: the TOTAL column, and QTY x PRICE. Where they agree, TOTAL
    /// is confirmed by arithmetic over different glyphs and is taken. Where they disagree, one of
    /// them is damaged and there is no way to tell which from the row alone, so the operator reads it
    /// off the picture.
    ///
    /// Arithmetic used to break that tie, and it is exactly the wrong tool: a calculation whose input
    /// was misread returns a confident wrong answer, and the store then books a cost nothing
    /// downstream questions. QTY x PRICE with a damaged PRICE is not a second opinion about TOTAL —
    /// it is the same damage wearing a different hat.
    ///
    /// A vision reading **is** a second opinion, because it comes off the picture rather than out of
    /// the same OCR pass, so the two readers agreeing settles the row on its own. Where they
    /// disagree the row's own arithmetic arbitrates — but only when it is worth something. With
    /// QTY 1, `QTY x PRICE = TOTAL` compares a figure to itself: the scanner renders the same glyph
    /// the same way in both columns, which is how one ticket printed $54.70 and read `$64.70` twice
    /// and reconciled perfectly. So a single-case row may not use its own arithmetic to overrule the
    /// picture. Where nothing independent can separate the two readers, the operator reads the row.
    /// </summary>
    private static decimal? SettleAmount(
        RawRow r, HashSet<decimal> confirmedPrices, List<ConversionWarning> warnings)
    {
        decimal? fromColumn = r.Amount;
        decimal? fromPicture = r.Vision?.Amount;

        // What the row's own columns multiply out to, gross and after the DEP/DISC/SUGAR columns.
        // The discount is allowed for but not demanded: those columns are `$0.00` on every recent
        // ticket and carry real money on the older ones.
        decimal? gross = null, net = null;
        if (r.Qty is > 0 && r.Price is not null)
        {
            gross = decimal.Round(r.Qty.Value * r.Price.Value, 2, MidpointRounding.AwayFromZero);
            net = decimal.Round(gross.Value - r.Adjustments, 2, MidpointRounding.AwayFromZero);
        }

        bool Multiplies(decimal? v) => Near(v, gross) || Near(v, net);

        // Two readers of the same printed figure, each with its own failures. Agreement is the
        // corroboration this row's own columns cannot supply, and it stands whether or not PRICE
        // survived the scan.
        if (Near(fromColumn, fromPicture)) return fromColumn;

        // No second reader: exactly the rule this vendor has always used. A TOTAL nothing confirms
        // is asked about even though it read cleanly — on one ticket PRICE came through as "$3611"
        // with the decimal point gone, so the row had nothing to check its TOTAL against, and the
        // $35.11 it read for a printed $36.11 went in unquestioned.
        if (fromPicture is null) return Multiplies(fromColumn) ? fromColumn : null;   // ask

        // The readers disagree. Only arithmetic over genuinely different glyphs may settle that, and
        // a row billing more than one case is the only place there is any: its TOTAL is a different
        // number from its PRICE, so a glyph the scanner renders wrongly cannot damage both to match.
        if (r.Qty is > 1 && Multiplies(fromColumn)) return fromColumn;
        if (Multiplies(fromPicture)) return fromPicture;

        // Nothing to settle: the scan read no amount at all, so there is no disagreement here, only
        // a hole and one reader who could see the figure. Taken and flagged, rather than asked.
        //
        // This is a deliberate softening of the rule that governs everything else in this file, and
        // it is worth being exact about why it is allowed. Every other case here has two claims
        // about one figure and needs something to choose between them; a figure the scan never
        // produced has one claim and nothing to choose. It is still a **reading** — copied off the
        // picture, never worked out from the other columns — so no checksum is being satisfied by
        // construction. What is given up is corroboration, which is why it goes in the file list for
        // a person to glance at.
        if (fromColumn is null && fromPicture is not null)
        {
            warnings.Add(Note(r, PdfReasonCodes.LineMathMismatch,
                $"the scan lost the TOTAL column, so {fromPicture:C2} was read off the picture. "
                + "Nothing on the page confirms it - check this one against the paper."));
            return fromPicture;
        }

        return null;   // nothing independent separates them - ask
    }

    /// <summary>Two money figures that are the same to the cent, ignoring sign. Null is never near.</summary>
    private static bool Near(decimal? a, decimal? b) =>
        a is not null && b is not null
        && Math.Abs(Math.Abs(a.Value) - Math.Abs(b.Value)) < 0.005m;

    /// <summary>
    /// Lets the ticket's own printed total **confirm** a row's own reading — never replace it.
    ///
    /// Every row offers at most two amounts: what the TOTAL column says, and what QTY x PRICE (or,
    /// on a credit row, UNITS x PRICE) works out to. Where a combination of those readings adds up
    /// to the total the ticket prints for itself, a row whose TOTAL column *already reads* the value
    /// in that combination has been checked against a figure made of different glyphs, and is taken.
    ///
    /// A row the search wants to **change** is not settled here, and that restriction is the whole
    /// point. The search can only choose among readings the scan produced, so where the true figure
    /// was never one of them, reaching the total proves nothing at all — it just means some other
    /// wrong combination happened to add up. On ticket 2035939353 it reached the printed $394.84
    /// exactly, through four wrong rows whose errors cancelled to the cent, and reported it with
    /// complete confidence. Those rows go to the operator now.
    /// </summary>
    private static decimal?[] SettleByPrintedTotal(
        List<RawRow> rows, List<PageFigures> figures, List<decimal> confirmedUnitRates)
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
                if (implied is not null)
                {
                    choices.Add(Cents(implied.Value, r.IsCredit));
                    // And the same row after its DEP/DISC/SUGAR columns, which is what the older
                    // tickets actually bill.
                    if (r.Adjustments != 0m)
                        choices.Add(Cents(implied.Value - r.Adjustments, r.IsCredit));
                }
            }

            // What the picture says this row reads, which on a damaged row is frequently the only
            // candidate that is actually correct. The other choices all come out of one OCR pass, so
            // where that pass rendered a glyph wrongly the true figure was never among them and the
            // search could only ever reach the printed total the wrong way round.
            if (r.Vision?.Amount is decimal seen) choices.Add(Cents(seen, r.IsCredit));

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

                // Confirmation only: the combination has to match a figure some reader actually took
                // off this row — its TOTAL column, or what the picture said. Anything else is the
                // search inventing a figure for a row whose truth it never had, and that row goes to
                // the operator instead.
                //
                // Letting the picture confirm is what repairs a row that reconciles with itself. One
                // ticket prints $54.70 and the scan renders `$64.70` in PRICE and TOTAL both, so the
                // row multiplies out perfectly and no arithmetic on the page can see it — but the
                // ticket only reaches its own printed total with $54.70, and that is the figure the
                // picture read. Before there was a second reader this row could only be asked about.
                if (Near(rows[i].Amount, value) || Near(rows[i].Vision?.Amount, value))
                    settled[i] = value;
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
    private static decimal? SettleCreditAmount(RawRow r, List<decimal> confirmedUnitRates)
    {
        // Two readers of the same printed figure, agreeing. A credit page states its money in
        // brackets and nothing else on it is a checksum, so this is often the only corroboration a
        // returned line can get at all.
        if (Near(r.Amount, r.Vision?.Amount)) return r.Amount;

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

        if (matches.Count == 1 && r.Units is > 0 && r.Amount is not null)
        {
            // The delivery page's exact rate, which is worth more than this row's own truncated one:
            // six returned 12oz cans are $13.36, not 6 x $2.23. Taken only when the amount column
            // already agrees to the rounding, so this corrects the printing and not the reading.
            decimal exact = decimal.Round(r.Units.Value * matches[0], 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(exact - r.Amount.Value) <= 0.005m * r.Units.Value + 0.005m) return exact;
            return null; // ask
        }

        // No delivery page to check against (a credit-only ticket). The row's own two readings have
        // to agree, to within the cent the printed rate is rounded to.
        if (r.Amount is not null && r.Price is > 0 && r.Units is > 0)
        {
            decimal implied = decimal.Round(r.Units.Value * r.Price.Value, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(implied - r.Amount.Value) <= 0.005m * r.Units.Value + 0.005m)
                return r.Amount;
            return null; // ask
        }

        return null; // nothing confirms it - ask
    }

    /// <summary>
    /// Settles one row's unit count, which — like the money — it may only do by confirming what is
    /// printed. UNITS is the figure the money can never check: misread it and the unit cost absorbs
    /// the difference, so the invoice balances to the penny while the store's stock is wrong.
    ///
    /// The pack size and the page's own unit count are **checks, not sources**. Filling a missing
    /// UNITS from QTY x pack is a calculation resting on QTY having been read correctly, and a wrong
    /// QTY there produces a quantity that agrees with everything and is simply not what arrived.
    /// </summary>
    private static int? SettleUnits(
        RawRow r, List<PageFigures> figures, List<RawRow> rows, List<ConversionWarning> warnings)
    {
        int? fromPicture = r.Vision?.Units is int v and > 0 ? v : null;

        // A credit row prints a fraction of a case in QTY, so the pack identity does not apply.
        if (r.IsCredit) return r.Units is > 0 ? r.Units : null;

        int? fromPack = r.Pack is > 0 && r.Qty is > 0 && decimal.Truncate(r.Qty.Value) == r.Qty.Value
            ? (int)(r.Qty.Value * r.Pack.Value)
            : null;

        // Two readers on the printed column, agreeing. That outranks the pack arithmetic below it,
        // which rests on QTY having been read correctly and is a check rather than a source.
        if (r.Units is > 0 && fromPicture == r.Units) return r.Units;

        // The scan lost the column outright, so there is nothing here to disagree with — only a hole
        // and one reader who could see the count. Where the pack this product is billed in predicts
        // the same number the figure is confirmed and goes in quietly; where nothing predicts it, the
        // reading is still taken but flagged, because a count nobody read at all is worse than one
        // read once. See SettleAmount for the full argument.
        if (r.Units is null or <= 0)
        {
            if (fromPicture is null) return null;                       // neither reader saw it - ask
            if (fromPicture == fromPack) return fromPicture;

            warnings.Add(Note(r, PdfReasonCodes.QtyMismatch,
                $"the scan lost the UNITS column, so {fromPicture} was read off the picture. "
                + "Nothing on the page confirms it - check this one against the paper."));
            return fromPicture;
        }

        if (fromPack is null) return r.Units;      // no pack on record to check it against
        if (r.Units == fromPack) return r.Units;

        // UNITS and the pack arithmetic disagree, and the picture lands on the pack's answer: the
        // column was misread and two unrelated things say what it should have been. A picture
        // reading that agrees with neither leaves three answers on the table and goes to a person.
        if (fromPicture is not null && fromPicture == fromPack) return fromPack;

        // Both readable and disagreeing. The page prints its own unit count; whichever reading makes
        // that figure is the right one, and if neither does, a person decides.
        var page = figures.FirstOrDefault(f => f.PageIndex == r.PageIndex);
        if (page?.UnitsDelivered is int printed)
        {
            int othersRead = rows
                .Where(x => !ReferenceEquals(x, r) && !x.IsCredit && x.PageIndex == r.PageIndex)
                .Sum(x => x.Units ?? 0);
            // Only the *printed* reading may be confirmed this way. Where the page's count backs the
            // pack arithmetic instead, two calculations agree with each other and neither of them is
            // what the paper says, so the operator reads it.
            if (othersRead + r.Units.Value == printed) return r.Units;
        }
        return null; // ask
    }

    // ─────────────────────────────────────────────────────────────────────── the barcode

    /// <summary>
    /// Works out which product a row is for. The ladder runs strongest evidence first and stops as
    /// soon as one rung answers; a row that reaches the bottom is handed to the operator.
    ///
    /// This is the one figure on the ticket that is **not** settled by arithmetic, and that is why it
    /// still resolves itself where the money no longer does. A barcode carries its own check digit,
    /// and <see cref="RedBullCatalog"/> is an independent record of what this vendor actually sells:
    /// a repair has to satisfy both, and neither knows anything about the other. Compare the money,
    /// where "QTY x PRICE" is not a second opinion about TOTAL but the same scan damage wearing a
    /// different hat — which is exactly how ticket 2035939353 reached its printed total through
    /// four wrong rows.
    ///
    /// It also cannot be replaced by asking without ruining a *clean* scan: one whole 300 dpi sample
    /// reads its entire barcode column as "611260..." for "611269...", so dropping the repairs puts a
    /// question on every row of a ticket that is otherwise perfect — and a question on a good scan
    /// is what teaches people to click through the questions that matter.
    /// </summary>
    private static void ResolveBarcode(
        RawRow r, List<ConversionWarning> warnings, IReadOnlyList<PdfBarcode> scanned)
    {
        var candidates = r.CodeCandidates.Distinct(StringComparer.Ordinal).ToList();
        string? fromBars = ScannedCodeFor(r, scanned);

        // 1. A reading that satisfies its own check digit and carries Red Bull's prefix.
        foreach (string c in candidates)
            if (Upc.IsValidUpcA12(c) && c.StartsWith(RedBullCatalog.Prefix, StringComparison.Ordinal))
            {
                // Two readings of the same printed barcode, each protected by its own check digit,
                // arrived at different products. One of them is wrong and nothing on the page says
                // which, so this is the operator's - the whole reason the bars are worth reading is
                // that they fail differently from the OCR, and that cuts both ways.
                if (fromBars is not null && !string.Equals(fromBars, c, StringComparison.Ordinal))
                {
                    warnings.Add(Note(r, PdfReasonCodes.UpcCheckDigit,
                        $"the digits under the barcode read {c} but the bars themselves decode to "
                        + $"{fromBars}. Both pass their own check digit, so one of them is a bad read "
                        + "and the page cannot say which.", r.CodeLineNo));
                    r.Gtin = null;
                    r.Pack = PackFromRow(r);
                    return;
                }

                r.Gtin = c;
                r.Pack = RedBullCatalog.Find(c)?.Pack ?? PackFromRow(r);
                return;
            }

        // 2. The printed bars, decoded off the picture.
        //
        //    Ahead of every repair below, and that ordering is the point. A repair is a *guess*: for
        //    a code whose prefix already reads correctly, changing any one of the last six digits
        //    restores the check digit, so a damaged reading has six equally valid candidates and the
        //    catalogue picks between them by what this vendor has been seen to sell. That is how the
        //    store's tickets ended up booking SPRING CHRY as SUMMER LIME and SUGARFREE 16OZ as
        //    RED BULL 16OZ - silently, because the money reconciles whatever code goes out. The bars
        //    are not a guess. They are a second reading of the same printed barcode, decoded by code
        //    that shares nothing with the OCR and carrying its own error correction, and on the
        //    store's twenty tickets they hold the right answer for five of the six rows the repairs
        //    get wrong.
        //
        //    They are not clean either - 6 of 174 decodes were wrong, and satisfied their own check
        //    digit - which is why the prefix test above them, the one-per-row rule, and the conflict
        //    check against a clean reading all have to stay. What is being chosen here is a reading
        //    over a guess, not a reading over another reading.
        if (fromBars is not null)
        {
            var scannedEntry = RedBullCatalog.Find(fromBars);
            warnings.Add(Note(r, PdfReasonCodes.UpcCheckDigit,
                $"the digits under the barcode did not read (the scan made them \"{r.PrintedCode}\"), "
                + $"so the code was taken off the printed bars instead: {fromBars}"
                + (scannedEntry is not null
                    ? $" ({scannedEntry.Description})."
                    : ". This vendor has not been seen selling that product before - worth a look at "
                      + "the line in SSCS."),
                r.CodeLineNo));
            r.Gtin = fromBars;
            r.Pack = scannedEntry?.Pack ?? PackFromRow(r);
            return;
        }

        // 3. A single-digit repair. For a code whose prefix already reads correctly there are six of
        //    these, so the catalogue picks; where only one repair exists at all, the check digit has
        //    already picked.
        var repairs = SingleDigitRepairs(candidates);
        var known = repairs.Where(x => RedBullCatalog.Find(x) is not null).ToList();
        if (known.Count == 1)
        {
            Accept(r, known[0], quiet: true, warnings);
            return;
        }
        if (repairs.Count == 1)
        {
            Accept(r, repairs[0], quiet: false, warnings);
            return;
        }

        // 4. A catalogued product two digits from the reading that also agrees on pack size. Two
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

        // 5. Red Bull's own item number, which is a second encoding of the same product.
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
    /// The barcode decoded off the bars printed on this row, or null when there is not exactly one.
    ///
    /// Three things have to hold, and each one threw out a real mis-decode on the store's samples:
    ///
    ///  * **It has to be a UPC-A carrying Red Bull's company prefix.** `611169546010` and
    ///    `611260321110` are both valid UPC-A codes that a smudged barcode decoded to, and neither
    ///    is a Red Bull code at all.
    ///  * **It has to sit on this row.** Red Bull prints the bars beside the TOTAL column of the
    ///    priced row and the digits on the line beneath, so a row owns the lines from its own down
    ///    to the last one belonging to it. A code borrowed from the row above books the delivery
    ///    onto another product with every total on the page still agreeing.
    ///  * **There has to be exactly one.** Two different codes decoded across the same row means
    ///    the reading or the placement is wrong, and there is no way to tell which - so neither is
    ///    used and the operator is asked, as before.
    /// </summary>
    private static string? ScannedCodeFor(RawRow r, IReadOnlyList<PdfBarcode> scanned)
    {
        if (scanned.Count == 0 || r.LineNo <= 0) return null;

        int last = Math.Max(r.LineNo, Math.Max(r.LastLineNo, r.CodeLineNo));
        var onThisRow = scanned
            .Where(b => b.LineNumber >= r.LineNo && b.LineNumber <= last)
            .Select(b => b.Text)
            .Where(t => t.Length == 12
                        && Upc.IsValidUpcA12(t)
                        && t.StartsWith(RedBullCatalog.Prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return onThisRow.Count == 1 ? onThisRow[0] : null;
    }

    /// <summary>
    /// Takes a repaired barcode. <paramref name="quiet"/> is for a repair two independent checks
    /// already agree on; anything less certain says so in the file list, because it changes which
    /// product the store is billed for and nothing downstream would notice.
    /// </summary>
    private static void Accept(RawRow r, string gtin, bool quiet, List<ConversionWarning> warnings)
    {
        var entry = RedBullCatalog.Find(gtin);
        if (!quiet)
            warnings.Add(Note(r, PdfReasonCodes.UpcCheckDigit,
                $"the barcode read {r.PrintedCode}, which fails its check digit. Used {gtin}"
                + (entry is not null ? $" ({entry.Description})." : "."),
                r.CodeLineNo));
        r.Gtin = gtin;
        r.Pack = entry?.Pack ?? PackFromRow(r);
    }

    private static int Differences(string a, string b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
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
