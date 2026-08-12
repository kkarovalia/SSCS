using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs that a Red Bull question shows the operator the row it is asking about.
///
/// Red Bull is the vendor this was hardest to add to, and the two reasons are what these tests pin.
/// It is read from positioned words rather than joined lines, so it has no line numbers of its own
/// unless the extractor hands them over — and it prints the barcode on the line **below** the priced
/// row, the opposite way round from Dr Pepper, so a picture cut by the Dr Pepper rule would show the
/// row above and mark the wrong thing on it.
///
/// A picture of the wrong row is worse than no picture: the operator reads a figure off it, types it
/// in, and the ticket then reconciles against a number belonging to a different product.
/// </summary>
public class RedBullSnipTests
{
    private const string Dir = @"Fixtures\PDF";

    private const string Clean = "2036629370_clean_300dpi";
    private const string Verified = "2030211863_sscs_verified";
    // Barcode questions are rare now that the codes come off the printed bars. This ticket is
    // one of the few that still asks: the digits under one barcode read cleanly as 611269616026
    // and the bars decode to 611269546026, both passing their own check digit, so the operator
    // settles it.
    private const string BarcodeAsk = "2029327159_bars_disagree";
    private const string AmountAsk = "2035039295_no_total_column";
    private const string LostDecimal = "2035939350_lost_decimal";
    private const string UnitsAsk = "2035260898";
    private const string NoPrintedColumns = "2035261008_total_misread";

    private static byte[] Bytes(string fixture) =>
        File.ReadAllBytes(Path.Combine(Dir, $"REDBULL_{fixture}.pdf"));

    private static RedBullTicket Parse(string fixture, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return RedBullPdfParser.Parse(PdfTextExtractor.ExtractPages(Bytes(fixture)), warnings);
    }

    // ─────────────────────────────────────────────────────── the numbering the whole port rests on

    /// <summary>
    /// The line number a positioned word carries is the number of the line that word is actually on.
    ///
    /// Everything else here depends on this one fact. Red Bull's parser only ever sees words, so if
    /// these numbers pointed anywhere else every picture it produced would be of some other row —
    /// and would still look entirely plausible, because it would be a real row of a real invoice.
    /// </summary>
    [Theory]
    [InlineData(Clean)]
    [InlineData(Verified)]
    [InlineData(BarcodeAsk)]
    [InlineData(AmountAsk)]
    [InlineData(LostDecimal)]
    public void AWordsLineNumberIsTheLineThatWordIsOn(string fixture)
    {
        byte[] pdf = Bytes(fixture);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        var pages = PdfTextExtractor.ExtractPages(pdf);

        int checked_ = 0;
        foreach (var row in pages.SelectMany(p => p.Rows))
            foreach (var word in row)
            {
                Assert.InRange(word.LineNumber, 1, lines.Count);
                Assert.Contains(word.Text, lines[word.LineNumber - 1].Text, StringComparison.Ordinal);
                checked_++;
            }

        Assert.True(checked_ > 100, $"only {checked_} words checked - the fixture proved nothing");
    }

    /// <summary>
    /// Every line of the document is reachable from the pages view and vice versa. A numbering that
    /// was merely *valid* — every number in range — could still be shifted by one throughout.
    /// </summary>
    [Fact]
    public void ThePagesViewAndTheLinesViewAreTheSameLines()
    {
        byte[] pdf = Bytes(Clean);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        var pages = PdfTextExtractor.ExtractPages(pdf);

        var joined = pages
            .SelectMany(p => p.Rows)
            .GroupBy(r => r[0].LineNumber)
            .OrderBy(g => g.Key)
            .Select(g => string.Join(" ", g.Single().Select(w => w.Text)))
            .ToList();

        Assert.Equal(lines.Select(l => l.Text), joined);
    }

    // ───────────────────────────────────────────────────────────── what a parsed row knows about itself

    /// <summary>Every product row can say where it is printed, and under which column names.</summary>
    [Theory]
    [InlineData(Clean)]
    [InlineData(Verified)]
    [InlineData(AmountAsk)]
    [InlineData(LostDecimal)]
    public void EveryProductRowKnowsWhereItIsPrinted(string fixture)
    {
        var ticket = Parse(fixture, out _);

        Assert.NotEmpty(ticket.Lines);
        foreach (var line in ticket.Lines)
        {
            Assert.True(line.LineNumber > 0, $"item {line.Position} does not know its own line");
            Assert.True(line.LastLineNumber >= line.LineNumber);
            Assert.True(line.HeaderLineNumber > 0, $"item {line.Position} has no column header");
            Assert.True(line.HeaderLineNumber < line.LineNumber,
                "the column names are printed above the products, not below them");
        }

        // In page order, one row per product, none of them sharing a priced line.
        var numbers = ticket.Lines.Select(l => l.LineNumber).ToList();
        Assert.Equal(numbers.OrderBy(n => n), numbers);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    /// <summary>
    /// Red Bull prints the barcode on the line *under* the priced row. Dr Pepper prints it on the
    /// product line *above*. This is the difference that makes a shared "one line up" rule wrong, and
    /// it is worth a test of its own because getting it backwards produces a picture that is still a
    /// real row and still looks right.
    /// </summary>
    [Theory]
    [InlineData(Clean)]
    [InlineData(Verified)]
    [InlineData(LostDecimal)]
    public void TheBarcodeIsPrintedBelowThePricedRow(string fixture)
    {
        var ticket = Parse(fixture, out _);

        var withCodes = ticket.Lines.Where(l => l.CodeLineNumber > 0).ToList();
        Assert.NotEmpty(withCodes);
        foreach (var line in withCodes)
        {
            Assert.True(line.CodeLineNumber > line.LineNumber,
                $"item {line.Position}: the barcode is on line {line.CodeLineNumber}, "
                + $"at or above its priced row on {line.LineNumber}");
            Assert.True(line.CodeLineNumber <= line.LastLineNumber,
                "the row's picture stops before its own barcode");
        }
    }

    /// <summary>
    /// The header is the ticket's column names, which is a row carrying no money — found by position
    /// because Red Bull's headings reach the text layer as debris ("PflICE", "6UDAn", "IDEAL") and
    /// cannot be found by reading them at all.
    /// </summary>
    [Theory]
    [InlineData(Clean)]
    [InlineData(Verified)]
    [InlineData(AmountAsk)]
    [InlineData(LostDecimal)]
    public void TheHeaderLineCarriesColumnNamesRatherThanFigures(string fixture)
    {
        byte[] pdf = Bytes(fixture);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        var ticket = Parse(fixture, out _);

        string header = lines[ticket.Lines[0].HeaderLineNumber - 1].Text;

        // No money on it — that is the guard, not the wording. A stray "$" inside the debris is
        // routine ("0X11$" is what one scan made of UNITS); a figure with cents on it would mean the
        // legend had slipped onto a product row.
        Assert.DoesNotMatch(@"\d+[.,]\d{2}", header);
        Assert.True(header.Count(char.IsWhiteSpace) >= 4, $"\"{header}\" is not a row of column names");
        Assert.NotNull(PdfRowSnipper.TryCutHeaderLine(pdf, ticket.Lines[0].HeaderLineNumber));
    }

    // ────────────────────────────────────────────────────────────────────── the picture on the screen

    /// <summary>
    /// The band a snip cuts is the band the row occupies. Checked against the invoice's own geometry
    /// rather than a number typed into a test: the row's share of the page has to land on the same
    /// share of the picture.
    /// </summary>
    [Fact]
    public void TheSnipCoversTheRowItNames()
    {
        byte[] pdf = Bytes(Clean);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        var ticket = Parse(Clean, out _);
        var line = ticket.Lines[2];

        var snip = PdfRowSnipper.TryCut(pdf, line.LineNumber, line.LastLineNumber, null, line.LineNumber);

        Assert.NotNull(snip);
        Assert.Equal(0, snip!.Left);                        // full width, so TOTAL is never cut off
        Assert.True(snip.Height > 0 && snip.Width > 0);

        var page = PageGeometry(pdf, lines[line.LineNumber - 1].PageNumber);
        double expectedTop = (page.Height - lines[line.LineNumber - 1].Top) * page.ScaleY;
        double markedTop = snip.Top + snip.FocusTop;
        Assert.True(Math.Abs(markedTop - expectedTop) <= page.RowHeight,
            $"the highlight starts at {markedTop:0} but the row sits at {expectedTop:0}");

        // The barcode line below is context, not the answer.
        Assert.True(snip.FocusHeight < snip.Height);
    }

    /// <summary>
    /// An amount question marks the TOTAL column — the figure itself, in the right-hand strip of the
    /// page — rather than washing the whole row and leaving the operator to pick.
    /// </summary>
    [Theory]
    [InlineData(AmountAsk)]
    [InlineData(LostDecimal)]
    public void AnAmountQuestionMarksTheTotalColumn(string fixture)
    {
        // A row whose TOTAL column reached the text layer at all - a column the scan lost entirely
        // has nothing to mark and is covered by AColumnTheScanNeverRecordedMarksTheWholeRow.
        var request = Ask(fixture, LineField.Amount).First(r => r.PrintedValue.Length > 0);

        Assert.Equal("TOTAL", request.ColumnName);
        var snip = Assert.IsType<PdfRowSnip>(request.Snip);
        Assert.NotNull(request.Header);
        Assert.True(snip.FocusWidth < snip.Width / 4,
            $"the marker is {snip.FocusWidth} of {snip.Width} wide - that is a row, not a figure");
        Assert.True(snip.FocusLeft > snip.Width * 0.6,
            "TOTAL is the last column on the row, so its marker belongs on the right");
    }

    /// <summary>A units question marks UNITS, which is over on the left.</summary>
    [Fact]
    public void AUnitsQuestionMarksTheUnitsColumn()
    {
        var asked = Ask(UnitsAsk, LineField.Units);

        var request = asked[0];
        Assert.Equal("UNITS", request.ColumnName);
        Assert.Equal("Cans or bottles, not cases.", request.Hint);
        var snip = Assert.IsType<PdfRowSnip>(request.Snip);
        Assert.True(snip.FocusWidth < snip.Width / 4);
        Assert.True(snip.FocusLeft < snip.Width * 0.35,
            "UNITS is the third column, so its marker belongs on the left");
    }

    /// <summary>
    /// A barcode question marks the code on the line beneath the priced row, and the picture still
    /// carries the priced row above it so the operator can tell which product it belongs to.
    /// </summary>
    [Fact]
    public void ABarcodeQuestionMarksTheCodeOnTheLineBelow()
    {
        var request = Ask(BarcodeAsk, LineField.Barcode)[0];

        Assert.Equal("", request.ColumnName);          // printed under the description, not in a column
        var snip = Assert.IsType<PdfRowSnip>(request.Snip);
        Assert.True(snip.FocusWidth < snip.Width / 4);
        Assert.InRange(snip.FocusLeft + (snip.FocusWidth / 2.0), snip.Width * 0.3, snip.Width * 0.7);

        // Measured against the paper, not guessed at from the picture's proportions: the marked band
        // has to be the barcode's own line and not the priced row it hangs under.
        byte[] pdf = Bytes(BarcodeAsk);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        var line = Parse(BarcodeAsk, out _).Lines.Single(l => l.Position == request.Position);
        var page = PageGeometry(pdf, lines[line.CodeLineNumber - 1].PageNumber);

        // The middle of the marked band against the middle of each printed line. Comparing top
        // edges would be measuring the breathing space instead: the band is drawn half a row above
        // the line it marks, which on a tightly-spaced ticket puts its top edge nearer the row above
        // while the band itself sits squarely on the barcode.
        double marked = snip.Top + snip.FocusTop + (snip.FocusHeight / 2.0);
        double codeRow = Middle(page, lines[line.CodeLineNumber - 1]);
        double pricedRow = Middle(page, lines[line.LineNumber - 1]);

        Assert.True(Math.Abs(marked - codeRow) < Math.Abs(marked - pricedRow),
            $"the marker at {marked:0} is nearer the priced row at {pricedRow:0} "
            + $"than the barcode at {codeRow:0}");
        Assert.True(Math.Abs(marked - codeRow) <= page.RowHeight);

        // And the priced row came along above it, so the operator can see which product it is.
        Assert.True(snip.Top < pricedRow);
    }

    /// <summary>
    /// Every question about a Red Bull row shows the row and the ticket's own column names. The
    /// picture is best-effort by design, and this is what stops "best effort" quietly meaning
    /// "never" on the vendor nobody checked afterwards.
    /// </summary>
    [Theory]
    [InlineData(UnitsAsk)]
    [InlineData(AmountAsk)]
    [InlineData(BarcodeAsk)]
    [InlineData(LostDecimal)]
    [InlineData(NoPrintedColumns)]
    public void EveryQuestionShowsTheRowAndTheColumnNames(string fixture)
    {
        var asked = Ask(fixture, null);

        Assert.NotEmpty(asked);
        foreach (var request in asked)
        {
            Assert.NotNull(request.Snip);
            Assert.NotNull(request.Header);
            Assert.True(request.Snip!.Height > 0 && request.Snip.FocusHeight > 0);
            Assert.Equal(request.Field == LineField.Barcode ? "" : request.ColumnName, request.ColumnName);
        }
    }

    /// <summary>
    /// A column the scan did not record at all cannot be marked, so the whole row is washed instead.
    /// Marking a guess would be worse: the operator would read it, type it, and the ticket would
    /// reconcile against a figure from the wrong column.
    /// </summary>
    [Fact]
    public void AColumnTheScanNeverRecordedMarksTheWholeRow()
    {
        var asked = Ask(NoPrintedColumns, null);

        var blank = asked.First(r => r.PrintedValue.Length == 0);
        var snip = Assert.IsType<PdfRowSnip>(blank.Snip);
        Assert.Equal(0, snip.FocusLeft);
        Assert.Equal(snip.Width, snip.FocusWidth);
    }

    /// <summary>
    /// The ticket that reached its own printed total through four wrong rows now asks about the two
    /// it would have changed, instead of changing them and saying so afterwards.
    ///
    /// Its lines used to add up to exactly the printed $394.84 with four of eight amounts wrong,
    /// because the errors cancelled to the cent. The search can only choose among readings the scan
    /// produced, and on two rows the truth was never one of them — so reaching the total proved
    /// nothing, and a note saying "used $51.70" sat beside a picture plainly reading $54.70.
    /// </summary>
    [Fact]
    public void TheRowsTheTotalWouldHaveChangedAreAskedAboutInstead()
    {
        var asked = Ask("2035939353_total_reached_wrong_way", LineField.Amount);

        // Two rows are asked about because nothing on them adds up. The ticket then still does not
        // reach its printed total, so the six nobody had looked at are shown too - which is what
        // finds the two that read cleanly and wrongly.
        Assert.Equal(8, asked.Count);
        Assert.Equal(
            new[] { "SUMMER LIME 1202", "RED BULL 1202LS" },
            asked.Take(2).Select(r => r.Description).ToArray());

        // And each one shows the row, so the operator can read the $54.70 the paper prints.
        Assert.All(asked, r =>
        {
            Assert.NotNull(r.Snip);
            Assert.NotNull(r.Header);
            Assert.Equal("TOTAL", r.ColumnName);
        });
    }

    /// <summary>
    /// A note is never a window any more. Whatever the converter still says about a file it says in
    /// the list beside it — so nothing it writes may carry a picture, because there is nowhere left
    /// to show one and building it would be work nobody sees.
    /// </summary>
    [Fact]
    public void NotesCarryNoPicturesBecauseNothingShowsThemAnyMore()
    {
        Assert.DoesNotContain(typeof(ConversionWarning).GetProperties(),
            p => p.PropertyType == typeof(PdfRowSnip));
        Assert.Equal(3, typeof(ConversionWarning).GetProperties().Length);
    }

    // ─────────────────────────────────────────────────────────────────────────────────── plumbing

    /// <summary>Runs the ticket through the real pipeline and returns what it asked about.</summary>
    private static List<LineFieldRequest> Ask(string fixture, LineField? only)
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "ticket.pdf");
        File.WriteAllBytes(path, Bytes(fixture));

        var prompt = new AnswerAnything();
        new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt)
            .ConvertFile(
                new QueuedFile { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull },
                tmp.Sub("out"));

        var asked = only is null ? prompt.LineFields : prompt.LineFields.Where(r => r.Field == only).ToList();
        Assert.True(asked.Count > 0, $"{fixture} asked nothing about its lines");
        return asked;
    }

    /// <summary>
    /// Answers every prompt with something plausible, so the run reaches the end and every question
    /// it wanted to ask is on record. What is being tested is the questions, not the answers.
    /// </summary>
    private sealed class AnswerAnything : IInvoiceDetailsPrompt
    {
        public List<LineFieldRequest> LineFields { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new("TYPED-1", request.SuggestedDate ?? request.Date ?? new DateOnly(2026, 8, 10), request.Total);

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return request.Field switch
            {
                LineField.Barcode => "611269991000",
                LineField.Units => "24",
                _ => "42.00",
            };
        }
    }

    /// <summary>Where the middle of a printed line falls in the page picture, in pixels down.</summary>
    private static double Middle((double Height, double ScaleY, double RowHeight) page, PdfTextLine line) =>
        (page.Height - ((line.Top + line.Bottom) / 2)) * page.ScaleY;

    private static (double Height, double ScaleY, double RowHeight) PageGeometry(byte[] pdf, int pageNumber)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = doc.GetPage(pageNumber);
        var image = page.GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples).First();
        double scaleY = image.HeightInSamples / page.Height;
        double rowHeight = page.GetWords().Max(w => w.BoundingBox.Height) * 3 * scaleY;
        return (page.Height, scaleY, rowHeight);
    }
}
