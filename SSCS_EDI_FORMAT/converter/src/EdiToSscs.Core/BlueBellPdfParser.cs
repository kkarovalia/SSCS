using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// One banner section found inside a Blue Bell PDF — a "Sales" block or a "Returns" block — before
/// the operator supplies the invoice number.
///
/// A ticket normally carries both, and Blue Bell prints a separate invoice number on each. They are
/// still **one payable**: verified in live SSCS on the 03/19/2026 ticket, which the store filed as a
/// single $325.50 paidout of seven lines (a $328.48 Sales block and a −$2.98 Returns block), not as
/// two. The sections are kept apart here only because the parser has to know which rows are returns;
/// <see cref="BlueBellPdfParser.Merge"/> is what actually produces the invoice.
/// </summary>
public sealed class BlueBellSection
{
    /// <summary>"Sales" or "Returns", read from the section banner.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The ticket's printed date, or null when the scan destroyed it. Null is not a parse failure:
    /// everything else on the ticket still reconciles against its own printed TOTAL, so the invoice
    /// is worth converting and the operator is asked to key the date — see
    /// <see cref="SuggestedDate"/>.
    /// </summary>
    public required DateOnly? Date { get; init; }

    /// <summary>
    /// What the date column appears to say once the letters the scanner substitutes for digits are
    /// mapped back ("12-14-Z023" → 12-14-2023). A hint for the operator to confirm against the
    /// paper, never used on its own — set only when <see cref="Date"/> is null.
    /// </summary>
    public DateOnly? SuggestedDate { get; init; }

    /// <summary>Printed TOTAL for this section. Negative for a Returns section.</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>
    /// The invoice number as read from the scan — a hint only. Blue Bell prints it in a large font
    /// that the scanner's OCR gets wrong silently (example 1's text layer says 033105035263 where
    /// the paper reads 033103035263), and it carries no check digit, so nothing can verify it.
    /// The operator types the real number; this is shown beside the box to compare against.
    /// </summary>
    public string? SuggestedInvoiceNumber { get; init; }

    /// <summary>
    /// Builds an invoice from this section alone. Used where one section really is the whole ticket;
    /// a ticket carrying both a Sales and a Returns block goes through
    /// <see cref="BlueBellPdfParser.Merge"/> instead, because SSCS books the pair as one payable.
    /// <paramref name="date"/> is only used if <see cref="Date"/> is null — the ticket's own printed
    /// date always wins.
    /// </summary>
    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = BlueBellPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = Date ?? date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Parses a Blue Bell Creameries, L.P. delivery invoice PDF into one or more
/// <see cref="BlueBellSection"/>s. Rules are documented in analysis/vendors/BLUE_BELL.md.
///
/// Layout — each product is two visual lines:
///
///   PROD UPC          DESCRIPTION
///        EA/COST      RETAIL   BX CNT   QTY   PRICE   EXTENDED
///   0800 007189903720 1/2 GAL GR SUP
///                                2.00   8.00  6.85    54.80
///
/// EA/COST and RETAIL are printed as column headings but Blue Bell leaves them blank on every
/// invoice, so the amounts row carries at most four numbers. QTY is already in individual units
/// (BX CNT is the case count) and PRICE is already the cost of one unit, so
/// EXTENDED = QTY x PRICE and no case arithmetic is needed.
///
/// These PDFs are photographs of a dot-matrix ticket with the Blue Bell cow watermark printed
/// across the middle columns, and the OCR text layer is badly corrupted there — BX CNT and QTY are
/// frequently unreadable while PRICE and EXTENDED, at the clean right-hand edge, survive. QTY is
/// therefore always reconciled against EXTENDED / PRICE and recovered from it when the printed
/// value disagrees. The section's own TOTAL row is the hard checksum.
/// </summary>
public static partial class BlueBellPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. SSCS resolves it to its own Vendor ID through the
    /// "Map Supplier to Vendor" screen.
    /// </summary>
    public const string SupplierName = "BLUE BELL";

    /// <summary>Blue Bell's fixed leading digits in the invoice's UPC column (0 + GS1 prefix 071899).</summary>
    private const string CodePrefix = "0071899";

    /// <summary>Printed UPC column width, e.g. "007189905101".</summary>
    private const int PrintedCodeLength = 12;

    /// <summary>
    /// The ticket's date. The word "DATE" is deliberately not required — one 240 dpi scan reads it
    /// as "OATS 11-13-2025" — and the mm-dd-yyyy shape is distinctive enough by itself: the only
    /// other hyphenated numbers Blue Bell prints are the phone number (281-446-1090) and the ZIP+4
    /// (75267-4272), neither of which matches this pattern, and <see cref="ReadDate"/> additionally
    /// rejects anything that is not a real calendar date.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(\d{2})-(\d{2})-(\d{4})(?!\d)")]
    private static partial Regex DateLine();

    /// <summary>
    /// The same shape, but tolerating the letters the scanner substitutes for digits — one ticket's
    /// date reads "DATE 12-14-Z023". Only ever used through <see cref="ReadDamagedDate"/>, which
    /// maps the letters back and then insists on a real calendar date, so the phone number and the
    /// ZIP+4 stay excluded exactly as they are for <see cref="DateLine"/>.
    ///
    /// A date read this way is a suggestion for the operator to confirm, never the invoice's date:
    /// mapping 'Z' to '2' is arithmetic-free guesswork about a field with no checksum behind it.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)([0-9OQDILSBZ]{2})-([0-9OQDILSBZ]{2})-([0-9OQDILSBZ]{4})(?!\d)")]
    private static partial Regex DamagedDateLine();

    /// <summary>
    /// A product row's leading "PROD code" pair; the code token is cleaned separately. The space
    /// between the two is optional because the scanner sometimes runs them together
    /// ("015100718992112"). Nothing is lost by allowing that: a line only becomes a product row if
    /// the code that follows actually reads as a Blue Bell code, which rules out the address line
    /// "17828 FM 1314" and the header line "369260 033100086682".
    /// </summary>
    [GeneratedRegex(@"^\s*(?<prod>\d{4})\s*(?<code>\S+)(?<rest>.*)$")]
    private static partial Regex ItemHead();

    /// <summary>
    /// Whether a cleaned token is recognisable as this vendor's code — enough to mark the line as a
    /// product row. It must carry the whole <see cref="CodePrefix"/>, but may be several digits
    /// short of the full twelve, because the scanner both drops digits ("00718992112") and breaks
    /// the code across a space ("007189905 120").
    ///
    /// A short code is repaired from the catalogue by PROD number, never by patching the loose
    /// digits on the line back on. On one sample the text after the code reads "00718992112 1 .D2/"
    /// — gluing that "1" on gives 007189921121, a well-formed code for the wrong product, where the
    /// catalogue gives the right one (007189921123). Scanner debris and a genuine missing digit
    /// look identical, so the line's own digits are not trusted to complete a code.
    /// </summary>
    private static bool LooksLikeCode(string cleaned) =>
        cleaned.Length is >= 8 and <= PrintedCodeLength
        && cleaned.StartsWith(CodePrefix, StringComparison.Ordinal);

    /// <summary>A PROD code stranded on its own line, with the UPC pushed onto the next one.</summary>
    [GeneratedRegex(@"^\s*(?<prod>\d{4})\s*[^\d]{0,6}$")]
    private static partial Regex LoneProd();

    /// <summary>
    /// A money value. The decimal point is often scanned as ':' ',' or ';'. The trailing
    /// (?!\d) rejects run-together OCR debris such as "2:001", which must not be read as 2.00.
    /// </summary>
    [GeneratedRegex(@"-?\d[\d,]*[.,:;]\d\d(?!\d)")]
    private static partial Regex MoneyToken();

    /// <summary>Characters the scanner commonly substitutes for digits inside the code column.</summary>
    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['l'] = '1', ['I'] = '1', ['L'] = '1', ['i'] = '1',
        ['O'] = '0', ['o'] = '0', ['Q'] = '0', ['D'] = '0',
        ['S'] = '5', ['s'] = '5', ['B'] = '8', ['Z'] = '2', ['z'] = '2',
    };

    /// <summary>
    /// PROD number to printed UPC, product name, and how many sellable units the invoice's QTY
    /// column counts for that product. Read off the sample scans by eye. Used to repair a code the
    /// scanner mangled beyond reading, to flag a code that contradicts it, to give a clean name to
    /// an item SSCS does not already know, and to convert QTY into units. It is never used to
    /// invent a code for a PROD number that has not been seen — a guessed barcode would post stock
    /// against the wrong item.
    ///
    /// <b>Pack:</b> pints and half gallons are billed one unit at a time, so their QTY is already
    /// the unit count. The "DZ" products are cartons of twelve: Blue Bell prices them per carton
    /// and counts QTY in cartons, but the store stocks and sells the individual bar or cup. See
    /// <see cref="PackFor"/>.
    /// </summary>
    private static readonly Dictionary<string, (string Code, string Name, int Pack)> Catalogue = new()
    {
        ["0020"] = ("007189906120", "PT FEATURED FLAVOR", 1),
        ["0121"] = ("007189921150", "DZ KRUNCH BAR", 12),
        ["0122"] = ("007189921133", "DZ 3OZ HMV CUPS", 12),
        ["0151"] = ("007189921122", "DZ VANILLA SAND", 12),
        ["0165"] = ("007189921123", "DZ CNC SAND", 12),
        ["0732"] = ("007189903747", "1/2 ULT NEA", 1),
        ["0777"] = ("007189903735", "1/2 HMV", 1),
        ["0781"] = ("007189903733", "1/2 DUT CHOC", 1),
        ["0789"] = ("007189903731", "1/2 CNC", 1),
        ["0800"] = ("007189903720", "1/2 GAL GR SUP", 1),
        ["0907"] = ("007189905126", "PT PISTAL", 1),
        ["0914"] = ("007189905123", "PT MT CHOC CH", 1),
        ["0915"] = ("007189905129", "PT GREAT DIVIDE", 1),
        ["0916"] = ("007189905115", "PT CCC DOUGH", 1),
        ["0919"] = ("007189905113", "PT BUT PEC", 1),
        ["0921"] = ("007189905117", "PT CNC", 1),
        ["0923"] = ("007189905120", "PT DUT CHOC", 1),
        ["0924"] = ("007189905122", "PT HMV", 1),
        ["0925"] = ("007189905101", "PT GR SUPREME", 1),
        ["0926"] = ("007189905128", "PT STRAW", 1),
        ["0928"] = ("007189905127", "PT ROCKY RD", 1),
        ["0944"] = ("007189905124", "PT MOO CRUNCH", 1),
        ["0953"] = ("007189905118", "PT COTTON CAN", 1),
        ["7001"] = ("007189903730", "1/2 COK 2 STP", 1),
        ["8001"] = ("007189905116", "PT COK 2 STP", 1),
    };

    /// <summary>
    /// Turns the invoice's printed 12-character code into the barcode to send SSCS.
    ///
    /// The printed column is NOT a UPC-A: only 3 of the 21 codes in the samples pass a check-digit
    /// test, which is chance. It is the 11-digit UPC-A body padded to 12 with a leading zero —
    /// "007189905101" is the body "07189905101", which is what SSCS displays as the item's ID/PLU.
    ///
    /// SSCS stores whatever GTIN it is given minus the last character and does NOT strip leading
    /// zeros (VM Novelties invoice IN-112 kept "09946143239" and "08526819712" intact). So the
    /// printed 12 characters sent as-is would land on "00718990510" and create a duplicate item;
    /// dropping the pad zero and putting the real check digit back lands on "07189905101", the
    /// existing one, and is a genuine scannable barcode.
    /// </summary>
    public static string ToGtin(string printedCode)
    {
        ArgumentNullException.ThrowIfNull(printedCode);
        return printedCode.Length == PrintedCodeLength && printedCode[0] == '0'
            ? Upc.AppendCheckDigit(printedCode[1..])
            : printedCode;
    }

    /// <summary>
    /// Retail seed for a brand-new item. Blue Bell prints a RETAIL column but never fills it in, and
    /// a zero here would let an unpriced new item ring up free; SSCS ignores this for any item it
    /// already knows. The rule itself is shared with the other vendors whose invoices carry no
    /// retail column — see <see cref="RetailSeed.FromCost"/>.
    /// </summary>
    public static decimal SeedRetail(decimal unitCost) => RetailSeed.FromCost(unitCost);

    /// <summary>
    /// Splits the PDF's text into invoice sections and parses each. Throws
    /// <see cref="EdiConversionException"/> when nothing usable is found or a section fails its
    /// own checksum; adds non-fatal notes to <paramref name="warnings"/> otherwise.
    /// </summary>
    /// <summary>
    /// Folds a ticket's sections into the one invoice SSCS actually books.
    ///
    /// Blue Bell prints a separate invoice number on its Returns block, which is why this used to
    /// produce two files. It was wrong: the store's own SSCS holds the 03/19/2026 ticket as a single
    /// $325.50 paidout of seven lines — six Sales rows totalling $328.48 and one −$2.98 return —
    /// under one typed invoice number. Returns are therefore negative lines on the same invoice, the
    /// same shape as Bimbo and Red Bull.
    ///
    /// The rows keep their printed order, Sales first, so the invoice reads down the page the way
    /// the paper does. <paramref name="date"/> is used only for a ticket whose own date the scan
    /// destroyed.
    /// </summary>
    public static Invoice Merge(
        IReadOnlyList<BlueBellSection> sections, string invoiceNumber, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No invoice sections could be read from this Blue Bell ticket.");

        return new Invoice
        {
            SupplierName = SupplierName,
            InvoiceNumber = invoiceNumber,
            // The date the operator confirmed, typed on every invoice now - see
            // InvoiceDetailsRequest.NeedsDate for why a date that read is still not trusted.
            Date = date,
            Total = sections.Sum(s => s.Total),
            Lines = sections.SelectMany(s => s.Lines).ToList(),
        };
    }

    public static IReadOnlyList<BlueBellSection> ParseAll(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        var sections = new List<BlueBellSection>();
        foreach (var block in SplitSections(lines))
        {
            var section = ParseSection(block, lines, warnings);
            if (section is not null) sections.Add(section);
        }

        if (sections.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No Blue Bell invoice was found in this PDF. If it is only the PICK LIST page, "
                + "scan the priced invoice page as well and try again.");
        return sections;
    }

    /// <summary>
    /// Each printed ticket restarts with its own "DATE mm-dd-yyyy" header line, which is how a
    /// Sales ticket and the Returns ticket that follows it are told apart — including when the
    /// scanner puts both on one tall page.
    ///
    /// A date the scanner damaged also counts here, because losing a split point is worse than
    /// losing a date: two tickets merged into one section put both invoices' line items against a
    /// single TOTAL, and the checksum then refuses a ticket that was otherwise fine.
    /// </summary>
    private static List<(int Start, int End)> SplitSections(IReadOnlyList<string> lines)
    {
        var starts = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (DateLine().IsMatch(lines[i]) || ReadDamagedDate(lines[i].ToUpperInvariant()) is not null)
                starts.Add(i);

        if (starts.Count == 0) return [(0, lines.Count)];
        starts[0] = 0; // anything above the first DATE belongs to the first section

        var blocks = new List<(int, int)>();
        for (int i = 0; i < starts.Count; i++)
            blocks.Add((starts[i], i + 1 < starts.Count ? starts[i + 1] : lines.Count));
        return blocks;
    }

    private static BlueBellSection? ParseSection(
        (int Start, int End) block, IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        string kind = "Sales";
        bool sawBanner = false;
        DateOnly? date = null;
        DateOnly? damagedDate = null;   // offered to the operator only if `date` never turns up
        decimal? total = null;
        decimal? balanceDue = null;
        decimal? printedQtyTotal = null;      // the TOTAL row's QTY column, in the invoice's own units
        decimal? printedReturnedUnits = null; // the "... RETURNED" row, always in sellable units
        string? suggested = null;

        var rows = new List<Row>();
        Pending? pending = null;

        for (int i = block.Start; i < block.End; i++)
        {
            string raw = lines[i];
            string s = raw.Trim();
            string u = s.ToUpperInvariant();
            int lineNo = i + 1;

            // The PICK LIST page carries no UPCs or prices; it is a packing slip, not an invoice.
            if (u.Contains("PICK", StringComparison.Ordinal) && u.Contains("LIST", StringComparison.Ordinal))
                return null;

            date ??= ReadDate(u);
            damagedDate ??= ReadDamagedDate(u);
            if (!sawBanner)
            {
                if (Regex.IsMatch(u, @"\bRETURNS?\b")) { kind = "Returns"; sawBanner = true; }
                else if (Regex.IsMatch(u, @"\bSALES\b")) { kind = "Sales"; sawBanner = true; }
            }
            suggested ??= ReadInvoiceNumber(s);

            // A Returns ticket restates what came back, and does so in individual items rather than
            // in the QTY column's units: "UNITS RETURNED -1.00" for pints, "BARS RETURNED -11.00"
            // where eleven of a twelve-bar carton went back. It is not a product row.
            if (u.Contains("RETURNED", StringComparison.Ordinal))
            {
                var n = MoneyValues(s);
                if (n.Count > 0) printedReturnedUnits = n[0];
                continue;
            }

            // The TOTAL row closes the item list; its last money value is the invoice total. The
            // word itself is matched loosely on "TOT" because the scanner mangles it
            // ("TOT '/ r ----- . OOs 248.72"). The only other TOT... row Blue Bell prints is the
            // pick list's TOTAL QUANTITY, and pick lists never reach here.
            if (u.StartsWith("TOT", StringComparison.Ordinal))
            {
                var n = MoneyValues(s);
                if (n.Count > 0)
                {
                    total = n[^1];
                    if (n.Count >= 3) printedQtyTotal = n[^2];
                }
                pending = null;   // a product row with no amounts under it is dropped, not priced at zero
                continue;
            }

            // BALANCE DUE repeats the total in a large font the scanner often mauls
            // ("BALA INCE DU~E~ F~I'+ 248.72", "BALrANCE1DUE .222!28"). It is kept only as a
            // fallback for when the TOTAL row itself is unreadable, and either way must never be
            // mistaken for a product's amounts row.
            if (u.StartsWith("BAL", StringComparison.Ordinal))
            {
                var n = MoneyValues(s);
                balanceDue = n.Count > 0 ? n[^1] : BalanceWithoutItsPoint(s) ?? balanceDue;
                pending = null;
                continue;
            }

            var head = ItemHead().Match(s);
            if (head.Success)
            {
                string code = CleanCode(head.Groups["code"].Value);
                string rest = head.Groups["rest"].Value;

                if (LooksLikeCode(code))
                {
                    pending = new Pending(lineNo, head.Groups["prod"].Value, code, rest);

                    // Some rows carry their amounts on the same visual line as the description.
                    var trailing = MoneyValues(rest);
                    if (trailing.Count >= 2) { rows.Add(pending.Commit(trailing)); pending = null; }
                    continue;
                }
            }

            // "0915 ./" — the PROD number alone, its UPC bumped to the following line.
            var lone = LoneProd().Match(s);
            if (pending is null && lone.Success)
            {
                pending = new Pending(lineNo, lone.Groups["prod"].Value, Code: null, Description: "");
                continue;
            }
            if (pending is { Code: null })
            {
                string code = CleanCode(s.Split(' ')[0]);
                if (LooksLikeCode(code))
                {
                    int space = s.IndexOf(' ', StringComparison.Ordinal);
                    string rest = space < 0 ? "" : s[(space + 1)..];
                    pending = pending with { Code = code, Description = rest };

                    var trailing = MoneyValues(rest);
                    if (trailing.Count >= 2) { rows.Add(pending.Commit(trailing)); pending = null; }
                    continue;
                }
            }

            var amounts = MoneyValues(s);
            if (pending is { Code: not null } ready && amounts.Count >= 2)
            {
                rows.Add(ready.Commit(amounts));
                pending = null;
            }
        }

        if (rows.Count == 0) return null;   // a header fragment with no products is not a section

        // An unreadable date is NOT fatal. It is one field on a ticket whose money still has to
        // reconcile line by line below, and the operator has the paper in front of them — so it is
        // asked for rather than costing them the whole delivery. Contrast the TOTAL, which is what
        // proves the parse and therefore can never be typed in.
        //
        // Falling back to BALANCE DUE is safe because it changes nothing about the hard gate below:
        // the line items still have to add up to whichever figure is used, or the file is refused.
        total ??= balanceDue;
        if (total is null)
            throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not read the TOTAL line on this Blue Bell invoice. Re-scan the whole ticket, "
                + "including the totals at the bottom, and try again.");

        var items = new List<InvoiceLine>();
        decimal sum = 0m;
        decimal unitSum = 0m;      // in sellable units, what SSCS books
        decimal invoiceQtySum = 0m; // in the invoice's own QTY units, what its TOTAL row counts

        foreach (var r in rows)
        {
            string code = ResolveCode(r.Prod, r.Code, r.LineNo, warnings);
            int pack = PackFor(r.Prod, r.Description, r.LineNo, warnings);
            decimal unitCost = decimal.Round(r.Price / pack, 4, MidpointRounding.AwayFromZero);
            int units = ResolveUnits(r, unitCost);

            items.Add(new InvoiceLine
            {
                Gtin = ToGtin(code),
                Description = DescriptionFor(r.Prod, r.Description),
                Units = units,
                UnitCost = unitCost,
                RetailPrice = SeedRetail(unitCost),
            });
            sum += decimal.Round(units * unitCost, 2, MidpointRounding.AwayFromZero);
            unitSum += units;
            invoiceQtySum += (decimal)units / pack;
        }

        // Hard gate: the lines must reconcile to the ticket's own printed TOTAL.
        if (Math.Abs(sum - total.Value) > items.Count * 0.01m)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"The line items add up to {sum:0.00} but this Blue Bell ticket's TOTAL is {total.Value:0.00}. "
                + "A line may be missing or misread. Please re-scan the whole ticket and try again.");

        // Second, independent check: Blue Bell prints its own quantity totals. The TOTAL row counts
        // in the QTY column's units (cartons for the DZ products), while a "... RETURNED" row counts
        // individual items, so each is compared against the matching figure.
        if (printedQtyTotal is decimal pq && Math.Abs(pq - invoiceQtySum) > 0.01m)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"This ticket's quantities add up to {invoiceQtySum:0.##} but it prints {pq:0.##}. The money "
                + "still balances, so the file was written — please check the quantities against the paper."));

        if (printedReturnedUnits is decimal pr && Math.Abs(pr - unitSum) > 0.01m)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"This ticket returns {unitSum:0} items but prints {pr:0}. The money still balances, "
                + "so the file was written — please check the quantities against the paper."));

        return new BlueBellSection
        {
            Kind = kind,
            Date = date,
            SuggestedDate = date is null ? damagedDate : null,
            Total = total.Value,
            Lines = items,
            SuggestedInvoiceNumber = suggested,
        };
    }

    /// <summary>
    /// A finished product row: identity from the first visual line, money from the second.
    ///
    /// The printed QTY is deliberately not kept. It is the most damaged column on the ticket — right
    /// under the watermark, wrong on roughly a quarter of lines — and on a partial-carton return it
    /// is not even a number ("-11/12"). PRICE and EXTENDED survive at the clean right edge, and the
    /// unit count follows from them exactly.
    /// </summary>
    private sealed record Row(
        int LineNo, string Prod, string Code, string Description,
        decimal Price, decimal Extended);

    /// <summary>
    /// A product row seen so far. Blue Bell splits every item across two visual lines, and the
    /// scanner sometimes splits the first of those again, so the PROD number, the code and the
    /// amounts can each arrive separately.
    /// </summary>
    private sealed record Pending(int LineNo, string Prod, string? Code, string Description)
    {
        /// <summary>
        /// Attaches the amounts row. EXTENDED is always last and PRICE second-to-last, whether the
        /// row printed four columns (BX CNT, QTY, PRICE, EXTENDED) or the Returns page's three.
        /// </summary>
        public Row Commit(List<decimal> money) => new(
            LineNo, Prod, Code!, Description, money[^2], money[^1]);
    }

    /// <summary>
    /// How many sellable units one step of the QTY column represents. A pint or half gallon is one
    /// unit; a "DZ" carton is twelve, because Blue Bell prices and counts those by the carton while
    /// the store stocks and sells the individual bar or cup.
    ///
    /// Verified in live SSCS 2026-07-26 on invoice 033100305634: a line printed as QTY 2 at $6.48
    /// is held as <b>24 at $0.54</b>, and the matching credit for eleven loose bars as
    /// <b>-11 at $0.54</b>.
    ///
    /// The catalogue is the source of truth because the printed "DZ" is not reliable — on the
    /// Returns page of that same invoice it scanned as "07 VANILLA SAND". For a product not in the
    /// catalogue the description is the only clue left, so it is used and flagged.
    /// </summary>
    private static int PackFor(string prod, string description, int lineNo, List<ConversionWarning> warnings)
    {
        if (Catalogue.TryGetValue(prod, out var known)) return known.Pack;

        // An unseen product: "DZ" at the start of the description means a carton of twelve. No word
        // boundary is required after it, because the scanner runs it into the flavour on exactly
        // the rows this matters for — "0464 DZJP-Z DUTCH CUP" was silently booked as 2 cartons
        // instead of 24 cups. Guessing 12 and warning is the safe side of that: guessing 1 is what
        // puts wrong stock into SSCS without anything noticing.
        if (Regex.IsMatch(description.TrimStart(), @"^D\s*Z", RegexOptions.IgnoreCase))
        {
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.LineMathMismatch,
                $"Line {lineNo}: PROD {prod} is not in the known Blue Bell list and its description "
                + "looks like a dozen-carton, so its quantity was multiplied by 12. Please check the "
                + "quantity against the paper."));
            return 12;
        }
        return 1;
    }

    /// <summary>
    /// QTY sits directly under the cow watermark and is the most corrupted column on the ticket, so
    /// it is always checked against EXTENDED and recovered from it when it disagrees.
    ///
    /// The check is done in sellable units, which also handles a partial carton without any special
    /// case: eleven bars back out of a twelve-bar carton prints as the fraction "-11/12" — which no
    /// money pattern matches — and -5.94 / 0.54 gives back exactly -11.
    /// </summary>
    private static int ResolveUnits(Row r, decimal unitCost)
    {
        if (unitCost == 0m)
            throw new EdiConversionException(PdfReasonCodes.LineMathMismatch,
                $"Line {r.LineNo} (PROD {r.Prod}) has no readable unit price. Please re-scan the ticket.");

        int derived = (int)decimal.Round(r.Extended / unitCost, 0, MidpointRounding.AwayFromZero);
        if (decimal.Round(derived * unitCost, 2, MidpointRounding.AwayFromZero) != r.Extended)
            throw new EdiConversionException(PdfReasonCodes.LineMathMismatch,
                $"Line {r.LineNo} (PROD {r.Prod}): no whole number of units at {unitCost:0.0000} each gives "
                + $"the printed {r.Extended:0.00}. Please re-scan the ticket and try again.");
        return derived;
    }

    /// <summary>
    /// Accepts the scanned code when it reads as a full Blue Bell code, otherwise falls back to the
    /// PROD number's known code. A PROD number that is not in the catalogue and whose code did not
    /// scan cleanly is refused — a guessed barcode is worse than no file.
    /// </summary>
    private static string ResolveCode(string prod, string code, int lineNo, List<ConversionWarning> warnings)
    {
        bool clean = code.Length == PrintedCodeLength && code.StartsWith(CodePrefix, StringComparison.Ordinal);
        bool inCatalogue = Catalogue.TryGetValue(prod, out var known);

        if (clean)
        {
            if (inCatalogue && known.Code != code)
                warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
                    $"Line {lineNo}: PROD {prod} normally carries UPC {known.Code} but this ticket scanned as "
                    + $"{code}. The scanned code was used — please check it against the paper."));
            return code;
        }

        if (inCatalogue)
        {
            warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
                $"Line {lineNo}: the UPC for PROD {prod} did not scan cleanly, so {known.Code} was used "
                + "from Blue Bell's known product list. Please check it against the paper."));
            return known.Code;
        }

        throw new EdiConversionException(PdfReasonCodes.UpcCheckDigit,
            $"Line {lineNo}: the UPC for PROD {prod} could not be read (scanned as '{code}') and this "
            + "product is not in the known Blue Bell list. Please re-scan the ticket more clearly.");
    }

    /// <summary>
    /// The name to put on the line. SSCS replaces this with its own wording for any item it already
    /// knows (verified 2026-07-26 — it showed "BLUE BELL PT GR SUPREM" over the name in the file),
    /// so it only really matters for a flavour the store has never received before. Blue Bell's own
    /// printed wording is often wrecked by the scan ("PT,STRAW ..,,", "D~ IT l" for PT PISTAL), so
    /// the known name is preferred and the scanned text is used only for an unrecognised product.
    /// </summary>
    private static string DescriptionFor(string prod, string scanned)
    {
        if (Catalogue.TryGetValue(prod, out var known)) return "BLUE BELL " + known.Name;

        string cleaned = new(scanned.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '/' or '-').ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length >= 3 ? "BLUE BELL " + cleaned : "BLUE BELL " + prod;
    }

    private static DateOnly? ReadDate(string upper)
    {
        var m = DateLine().Match(upper);
        return m.Success
            ? BuildDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
            : null;
    }

    /// <summary>
    /// Best-effort read of a date the scanner mangled, for the operator to confirm — never used as
    /// the invoice's own date. See <see cref="DamagedDateLine"/>.
    /// </summary>
    private static DateOnly? ReadDamagedDate(string upper)
    {
        foreach (Match m in DamagedDateLine().Matches(upper))
        {
            string? mm = RepairDigits(m.Groups[1].Value);
            string? dd = RepairDigits(m.Groups[2].Value);
            string? yyyy = RepairDigits(m.Groups[3].Value);
            if (mm is null || dd is null || yyyy is null) continue;
            if (BuildDate(mm, dd, yyyy) is DateOnly d) return d;
        }
        return null;
    }

    /// <summary>
    /// A real calendar date, or null. The year window keeps a mis-repaired reading from becoming a
    /// plausible-looking date centuries away.
    /// </summary>
    private static DateOnly? BuildDate(string month, string day, string year)
    {
        int mm = int.Parse(month, CultureInfo.InvariantCulture);
        int dd = int.Parse(day, CultureInfo.InvariantCulture);
        int yyyy = int.Parse(year, CultureInfo.InvariantCulture);
        if (mm is < 1 or > 12 || yyyy is < 2000 or > 2100) return null;
        if (dd < 1 || dd > DateTime.DaysInMonth(yyyy, mm)) return null;
        return new DateOnly(yyyy, mm, dd);
    }

    /// <summary>All digits, mapping the letters the scanner substitutes; null if any character is
    /// neither.</summary>
    private static string? RepairDigits(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else if (LetterToDigit.TryGetValue(c, out char d)) sb.Append(d);
            else return null;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Best-effort read of the big-font invoice number, offered to the operator as a hint only.
    ///
    /// The number is 12 digits and encodes the branch, so it is not the same everywhere: BEST STOP
    /// 14 is branch 33 and its numbers begin 0331, while BEST STOP #5 is branch 1 and its numbers
    /// begin 0012. Matching a whole 12-digit token therefore works for any store, and can never
    /// pick up a product code, which always begins <see cref="CodePrefix"/>.
    /// </summary>
    private static string? ReadInvoiceNumber(string s)
    {
        if (ItemHead().IsMatch(s) && CleanCode(ItemHead().Match(s).Groups["code"].Value).StartsWith("00718", StringComparison.Ordinal))
            return null; // a product row, not the header

        foreach (string token in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string d = new(token.Where(char.IsAsciiDigit).ToArray());
            if (d.Length == 12 && d[0] == '0' && !d.StartsWith(CodePrefix, StringComparison.Ordinal))
                return d;
        }

        // Fallback for a header the scanner broke apart ("I^wvvOI`E#03 3101626155"). Anchored on
        // 0331 because without a token boundary the ship-to number runs into the invoice number,
        // and only branch 33's prefix is known well enough to find the join reliably.
        string digits = new(s.Where(char.IsAsciiDigit).ToArray());
        int at = digits.IndexOf("0331", StringComparison.Ordinal);
        return at >= 0 && digits.Length - at >= 12 ? digits.Substring(at, 12) : null;
    }

    /// <summary>Digits of a code token, mapping the letters the scanner substitutes for digits.</summary>
    private static string CleanCode(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else if (LetterToDigit.TryGetValue(c, out char d)) sb.Append(d);
            if (sb.Length == PrintedCodeLength) break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads a BALANCE DUE whose decimal point the scanner dropped — "BALANCE DUE 7232" for 72.32.
    /// Blue Bell always prints this figure to the cent, so the last two digits are the cents.
    ///
    /// This is a last resort, used only when the TOTAL row itself was unreadable, and it cannot put
    /// a wrong total into a file: whatever it returns still has to equal the sum of the line items
    /// or the invoice is refused. On a ticket where the digits scan badly ("BAUNGEDU.E34~9)60")
    /// the reconstruction simply disagrees with the lines and the file is rejected.
    /// </summary>
    private static decimal? BalanceWithoutItsPoint(string s)
    {
        var m = Regex.Match(s, @"(?<!\d)(-?)(\d{3,9})(?!\d)");
        if (!m.Success) return null;
        decimal cents = decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return (m.Groups[1].Value == "-" ? -cents : cents) / 100m;
    }

    private static List<decimal> MoneyValues(string s)
    {
        var list = new List<decimal>();
        foreach (Match m in MoneyToken().Matches(s))
        {
            string t = m.Value.Replace(',', '.').Replace(':', '.').Replace(';', '.');
            // A thousands separator would have become a second '.', e.g. "1.234.56".
            int last = t.LastIndexOf('.');
            t = t[..last].Replace(".", "") + t[last..];
            if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v))
                list.Add(v);
        }
        return list;
    }
}
