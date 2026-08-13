using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs that a snip shows the row it claims to.
///
/// A picture of the wrong row is worse than no picture at all: the operator reads a figure off it,
/// types that figure in, and every check on the page then agrees with a number that belongs to a
/// different product. So what is pinned here is the alignment between the line number a parser
/// reports and the band of paper the snip cuts.
/// </summary>
public class PdfRowSnipperTests
{
    private const string DrPepperCredits = @"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf";
    private const string DrPepperTwice = @"Fixtures\PDF\DRPEPPER_5218300220_twice_scanned.pdf";
    private const string BlueBell = @"Fixtures\PDF\BLUEBELL_033100786112.pdf";
    private const string RedBull = @"Fixtures\PDF\REDBULL_2036629370_clean_300dpi.pdf";
    private const string Chubiwinos = @"Fixtures\PDF\CHUBIWINOS_6V9069.pdf";
    private const string BimboBothBands = @"Fixtures\PDF\BIMBO_84294890012811_displaced_money_row.pdf";
    private const string BimboDeliveriesOnly = @"Fixtures\PDF\BIMBO_84294890010234_deliveries_only.pdf";

    // These tickets are till rolls, so the page is narrow and not a standard size. The snipper scales
    // PDF points to picture pixels by the page's own width, and a test comparing a word's position
    // against a marker has to use the same figure or it silently comes out several pixels adrift —
    // which is exactly how a first cut of the returns test below "found" a clipped marker that was
    // nothing of the sort.
    private const double DeliveriesOnlyPageWidth = 317.52;
    private const double BothBandsPageWidth = 315.72;

    private static byte[] Bytes(string fixture) => File.ReadAllBytes(fixture);

    /// <summary>
    /// A row is shown under the header its **own band** prints, not the one at the top of the page.
    ///
    /// Bimbo prints the whole heading twice — once under `**DELIVERIES**` and again under
    /// `**RETURNS**` — and a question about a returned line has to arrive under the second one. The
    /// figures beneath them differ only by a minus sign, so a strip from the wrong band is not
    /// obviously wrong to look at; it just quietly asserts that the row above it is a delivery.
    ///
    /// Pinned on the ticket that carries both, where the deliveries heading is lines 15–16 and the
    /// returns heading lines 23–24.
    /// </summary>
    [Fact]
    public void ABimboReturnsRowGetsTheReturnsHeadingAndADeliveryRowGetsTheDeliveriesOne()
    {
        byte[] pdf = Bytes(BimboBothBands);

        // Line 19 is a delivery row; line 25 is the ticket's only returned line.
        var delivery = PdfRowSnipper.TryCutHeader(pdf, 19);
        var returns = PdfRowSnipper.TryCutHeader(pdf, 25);

        Assert.NotNull(delivery);
        Assert.NotNull(returns);

        // The returns heading is printed further down the page than the deliveries one, so a cut
        // that took the nearest heading above each row cannot have taken the same band twice.
        Assert.True(returns!.Top > delivery!.Top,
            $"both rows were shown the same heading at y={delivery.Top} - the returns row should "
            + "have been given the one its own band prints, lower down the page");

        // Both are two printed lines, and neither is marked.
        Assert.Equal(0, delivery.FocusHeight);
        Assert.Equal(0, returns.FocusHeight);
    }

    /// <summary>
    /// A figure the scan **deleted** is still marked, by its column.
    ///
    /// This is the common case on Bimbo and it has no equivalent on the other two vendors, whose
    /// damaged figures are usually still present and merely mangled — so `FindSpan` finds them.
    /// Bimbo's 200 dpi scans drop the QTY column outright: on this ticket all forty rows lost it, and
    /// every question therefore arrived with the whole 882px line washed. On a row of eight figures
    /// carrying two columns both headed AMOUNT, that is not a marker, it is a shrug.
    ///
    /// The column is located from the printed heading — the word before RETAIL, since QTY's own
    /// heading is what the scan mangles into `UTY`, `OTY`, `OW` and `UN`. What is pinned here is that
    /// the box is small, and that it lands between the description and the retail figures rather than
    /// on either of them: a confident box on the wrong column is the failure this whole file exists
    /// to avoid.
    /// </summary>
    [Fact]
    public void ABimboQuantityTheScanDeletedIsMarkedByItsColumn()
    {
        byte[] pdf = Bytes(BimboDeliveriesOnly);

        // Line 16 is the first product row. Its QTY (a printed 4) is absent from the text layer, so
        // there is deliberately no focusText to find.
        var snip = PdfRowSnipper.TryCut(pdf, 16, 16, null, 16, false, columnLeftOf: "RETAIL");
        Assert.NotNull(snip);

        Assert.DoesNotContain(" 4 ", PdfTextExtractor.ExtractLines(pdf)[15]);

        // A marker, not a wash. Comfortably under a fifth of the row.
        Assert.True(snip!.FocusWidth < snip.Width / 5,
            $"the marker is {snip.FocusWidth}px of a {snip.Width}px row, which is a wash not a box");
        Assert.True(snip.FocusHeight > 0);

        // And in the right place: the description ends and the retail figures begin either side of
        // it, so a box that had drifted onto a neighbour would fail here.
        var line = PdfTextExtractor.ExtractPositionedLines(pdf)[15];
        double scale = snip.Width / DeliveriesOnlyPageWidth;
        double descriptionEnds = line.Words.First(w => w.Text == "FC").Right * scale;
        double retailBegins = line.Words.First(w => w.Text == "3.19").Left * scale;

        Assert.True(snip.FocusLeft > descriptionEnds,
            $"the marker starts at {snip.FocusLeft}px, inside the description ending at "
            + $"{descriptionEnds:0}px");
        Assert.True(snip.FocusLeft + snip.FocusWidth < retailBegins,
            $"the marker ends at {snip.FocusLeft + snip.FocusWidth}px, over the SUGG RETAIL figure "
            + $"beginning at {retailBegins:0}px");
    }

    /// <summary>
    /// The column marker works on a returned line too, and takes its bearings from the RETURNS
    /// band's own heading rather than the deliveries one further up the page.
    ///
    /// Worth its own test because the two bands are printed at slightly different offsets — the
    /// deliveries heading on this ticket puts QTY at 162.1–171.9 and the returns heading at
    /// 165.0–174.8, while the returned quantity `-1` sits at 170.7–178.1. A marker sized off the
    /// wrong band's heading clips it. A returned line is also the one place the figure carries a
    /// minus sign, and typing 1 where the paper says -1 turns a credit into a delivery.
    /// </summary>
    [Fact]
    public void TheColumnMarkerWorksOnAReturnedLineToo()
    {
        byte[] pdf = Bytes(BimboBothBands);

        // Line 25 is the ticket's only returned line; line 20 is a delivery for comparison.
        var credit = PdfRowSnipper.TryCut(pdf, 25, 25, null, 25, false, columnLeftOf: "RETAIL");
        var delivery = PdfRowSnipper.TryCut(pdf, 20, 20, null, 20, false, columnLeftOf: "RETAIL");

        Assert.NotNull(credit);
        Assert.NotNull(delivery);

        // Both marked, both narrow.
        Assert.True(credit!.FocusWidth > 0 && credit.FocusWidth < credit.Width / 5);
        Assert.True(delivery!.FocusWidth > 0 && delivery.FocusWidth < delivery.Width / 5);

        // The returns band sits further right on this ticket, so the two markers are not the same
        // rectangle — which is the proof that each took its own band's heading.
        Assert.NotEqual(delivery.FocusLeft, credit.FocusLeft);

        // And the marker covers the printed "-1", minus sign included.
        var line = PdfTextExtractor.ExtractPositionedLines(pdf)[24];
        var qty = line.Words.First(w => w.Text == "-1");
        double scale = credit.Width / BothBandsPageWidth;
        Assert.True(credit.FocusLeft <= qty.Left * scale,
            $"the marker starts at {credit.FocusLeft}px, past the minus sign at {qty.Left * scale:0}px");
        Assert.True(credit.FocusLeft + credit.FocusWidth >= qty.Right * scale,
            $"the marker ends at {credit.FocusLeft + credit.FocusWidth}px, short of the figure ending "
            + $"at {qty.Right * scale:0}px");
    }

    /// <summary>
    /// The column fallback is used **only** when the figure itself could not be found. A token the
    /// scan did read is marked exactly, which is both tighter and safer than its column.
    /// </summary>
    [Fact]
    public void AFigureTheScanDidReadIsMarkedItselfRatherThanItsColumn()
    {
        byte[] pdf = Bytes(BimboBothBands);

        // Line 20's QTY of 12 survived the scan on this ticket.
        var byToken = PdfRowSnipper.TryCut(pdf, 20, 20, "12", 20, false, columnLeftOf: "RETAIL");
        var byColumn = PdfRowSnipper.TryCut(pdf, 20, 20, null, 20, false, columnLeftOf: "RETAIL");

        Assert.NotNull(byToken);
        Assert.NotNull(byColumn);
        Assert.True(byToken!.FocusWidth < byColumn!.FocusWidth,
            "the printed figure should be marked more tightly than the column it sits in");
    }

    /// <summary>
    /// Dr Pepper's heading is one printed line and stays one. The two-line cut is Bimbo's, driven by
    /// the line above carrying RETAIL; Dr Pepper's says "DESCRIPTION UPC/SKU", so nothing changes for
    /// it. Worth a test because the two vendors share one <see cref="PdfRowSnipper.TryCutHeader"/>
    /// and a heading that quietly grew a line would push the row itself off the top of the dialog.
    /// </summary>
    [Fact]
    public void DrPeppersHeadingIsStillTheSingleLineItAlwaysWas()
    {
        byte[] pdf = Bytes(DrPepperCredits);

        var header = PdfRowSnipper.TryCutHeader(pdf, 40);
        Assert.NotNull(header);

        var oneLine = PdfRowSnipper.TryCutHeaderLine(pdf, 1);
        Assert.NotNull(oneLine);

        // Same band height as any other single line on the same page, within the rounding a
        // different font size on the heading row can produce.
        Assert.True(header!.Height <= oneLine!.Height * 3 / 2,
            $"the Dr Pepper heading grew from {oneLine.Height}px to {header.Height}px");
    }

    /// <summary>
    /// The line numbering the snipper reads is the same numbering every parser reports by. They are
    /// one pass rather than two precisely so they cannot drift; this pins that.
    /// </summary>
    [Theory]
    [InlineData(DrPepperCredits)]
    [InlineData(DrPepperTwice)]
    [InlineData(BlueBell)]
    [InlineData(Chubiwinos)]
    public void PositionedLinesAreTheSameLinesInTheSameOrder(string fixture)
    {
        byte[] pdf = Bytes(fixture);

        var plain = PdfTextExtractor.ExtractLines(pdf);
        var placed = PdfTextExtractor.ExtractPositionedLines(pdf);

        Assert.Equal(plain, placed.Select(l => l.Text));
    }

    /// <summary>
    /// The band a snip cuts is the band the line occupies. Checked against the invoice's own
    /// geometry rather than against a number I typed in: the row's share of the page, top to bottom,
    /// has to land on the same share of the picture, top to bottom.
    /// </summary>
    [Fact]
    public void TheSnipCoversTheLineItNames()
    {
        byte[] pdf = Bytes(DrPepperCredits);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);

        // The row that reached the text layer with three of its six columns missing.
        int lineNo = lines.Select((l, i) => (l, i)).First(x => x.l.Text.Trim() == "20.66 0.00 20.66").i + 1;

        var snip = PdfRowSnipper.TryCut(pdf, lineNo - 1, lineNo);

        Assert.NotNull(snip);
        Assert.Equal(0, snip!.Left);                       // full width, so the AMOUNT is never cut off
        Assert.True(snip.Height > 0 && snip.Width > 0);

        // Where the marked row sits inside the snip must match where the line sits on the page.
        var page = PageGeometry(pdf, lines[lineNo - 1].PageNumber);
        double expectedTop = (page.Height - lines[lineNo - 1].Top) * page.ScaleY;
        double markedTop = snip.Top + snip.FocusTop;
        Assert.True(Math.Abs(markedTop - expectedTop) <= page.RowHeight,
            $"the highlight starts at {markedTop:0} but the line sits at {expectedTop:0}");

        // And it marks one row, not the whole snip - the row above is context, not the answer.
        Assert.True(snip.FocusHeight < snip.Height,
            $"the highlight ({snip.FocusHeight}) covers the whole snip ({snip.Height})");
    }

    /// <summary>
    /// A figure that can be placed is marked on its own, not by marking the row it sits on. The
    /// operator asked for this: a marker around the whole row invites typing the whole row, and on a
    /// barcode that means typing the SKU after the slash too.
    /// </summary>
    [Fact]
    public void AFigureThatCanBePlacedIsMarkedOnItsOwn()
    {
        byte[] pdf = Bytes(DrPepperTwice);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);

        // "25.00 1 11 17.00 0.00 17.00" - the UNITS column, misread as 11 for 12.
        int lineNo = lines.Select((l, i) => (l, i))
            .First(x => x.l.Text.Trim() == "25.00 1 11 17.00 0.00 17.00").i + 1;

        var snip = PdfRowSnipper.TryCut(pdf, lineNo - 1, lineNo, "11");

        Assert.NotNull(snip);
        Assert.True(snip!.FocusWidth < snip.Width / 4,
            $"the marker is {snip.FocusWidth} of {snip.Width} wide - that is a row, not a figure");
        Assert.True(snip.FocusLeft > 0, "a figure mid-row cannot start at the left edge");
        Assert.True(snip.FocusLeft + snip.FocusWidth <= snip.Width);
    }

    /// <summary>
    /// Where the figure cannot be placed beyond doubt the whole row is marked instead. Both ways of
    /// failing are covered: a figure the scan never recorded, and one that appears twice on its row
    /// with nothing to say which is meant.
    /// </summary>
    [Theory]
    [InlineData("")]            // the column never reached the text layer
    [InlineData("20.66")]       // twice on "20.66 0.00 20.66"
    [InlineData("nonsense")]    // not on the row at all
    public void AFigureThatCannotBePlacedFallsBackToTheWholeRow(string focus)
    {
        byte[] pdf = Bytes(DrPepperCredits);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        int lineNo = lines.Select((l, i) => (l, i)).First(x => x.l.Text.Trim() == "20.66 0.00 20.66").i + 1;

        var snip = PdfRowSnipper.TryCut(pdf, lineNo - 1, lineNo, focus);

        Assert.NotNull(snip);
        Assert.Equal(0, snip!.FocusLeft);
        Assert.Equal(snip.Width, snip.FocusWidth);
    }

    /// <summary>
    /// A barcode is marked whole or not at all. The scan welds a description character onto the front
    /// of a code and drops marks into the middle of one, and once the transcription and the paper
    /// disagree about how many characters a token has, no marker drawn from it covers the right
    /// digits — so a code with a mark in it gets the whole line rather than eleven of its twelve
    /// digits. An operator typing eleven digits would get a check digit invented for them and a
    /// barcode that is not the item.
    /// </summary>
    [Theory]
    [InlineData(DrPepperCredits)]
    [InlineData(DrPepperTwice)]
    public void ABarcodeIsMarkedExactlyOrNotAtAll(string fixture)
    {
        byte[] pdf = Bytes(fixture);
        var lines = PdfTextExtractor.ExtractPositionedLines(pdf);
        int exact = 0, whole = 0;

        for (int i = 1; i <= lines.Count; i++)
        {
            var word = lines[i - 1].Words.FirstOrDefault(w => w.Text.Contains('/'));
            if (word is null || word.Letters.Count != word.Text.Length) continue;
            int slash = word.Text.LastIndexOf('/');
            if (slash < 12) continue;

            var snip = PdfRowSnipper.TryCut(pdf, i, i, null, i, markWholeCode: true);
            if (snip is null) continue;

            double scale = snip.Width / PageWidth(pdf, lines[i - 1].PageNumber);
            double left = snip.FocusLeft / scale;                       // back into PDF points
            double right = (snip.FocusLeft + snip.FocusWidth) / scale;
            const double Slack = 2.5;                                   // the marker's own breathing room

            if (word.Text.Skip(slash - 12).Take(12).All(char.IsAsciiDigit))
            {
                // Exactly the twelve digits: starts at the first of them, stops before the slash.
                exact++;
                Assert.InRange(left, word.Letters[slash - 12].Left - Slack, word.Letters[slash - 12].Left + Slack);
                Assert.InRange(right, word.Letters[slash - 1].Right - Slack, word.Letters[slash].Left + Slack);
            }
            else
            {
                // Damaged: the token as a whole, whose outside edges are still honest - never a
                // slice of it, because OCR spreads its extra characters across the token's real
                // width and a slice would confidently cover ten of the twelve digits.
                whole++;
                Assert.InRange(left, word.Left - Slack, word.Left + Slack);
                Assert.InRange(right, word.Right - Slack, word.Right + Slack);
            }
        }

        Assert.True(exact > 0, "no clean barcode was marked exactly - the test proved nothing");
        Assert.True(exact + whole > 5);
    }

    /// <summary>
    /// Every vendor's scan gives up a picture. A snip is best-effort by design, but "best effort"
    /// must not quietly mean "never" for a vendor nobody checked.
    /// </summary>
    [Theory]
    [InlineData(DrPepperCredits)]
    [InlineData(DrPepperTwice)]
    [InlineData(BlueBell)]
    [InlineData(RedBull)]
    [InlineData(Chubiwinos)]
    public void EveryScannedVendorCanBeSnipped(string fixture)
    {
        byte[] pdf = Bytes(fixture);
        int lineNo = PdfTextExtractor.ExtractPositionedLines(pdf).Count / 2;

        var snip = PdfRowSnipper.TryCut(pdf, lineNo, lineNo);

        Assert.NotNull(snip);
        Assert.NotEmpty(snip!.PagePng);
        Assert.True(snip.Height > 0);
        Assert.True(snip.FocusHeight > 0);
    }

    /// <summary>
    /// Consecutive rows give different bands, in page order. A snipper that returned the same slab
    /// for everything would pass every check above and be useless.
    /// </summary>
    [Fact]
    public void DifferentRowsGiveDifferentBandsGoingDownThePage()
    {
        byte[] pdf = Bytes(DrPepperCredits);
        int count = PdfTextExtractor.ExtractPositionedLines(pdf).Count;

        var tops = Enumerable.Range(20, 12)
            .Select(n => PdfRowSnipper.TryCut(pdf, n, n))
            .Select(s => s!.Top + s.FocusTop)
            .ToList();

        Assert.Equal(tops.Count, tops.Distinct().Count());
        Assert.Equal(tops.OrderBy(t => t), tops);
        Assert.True(count > 32);
    }

    /// <summary>
    /// A line number nobody has, and a PDF with no picture in it, both come back empty rather than
    /// throwing. The message the snip decorates has to stand on its own.
    /// </summary>
    [Fact]
    public void NothingToSnipIsNullRatherThanAnError()
    {
        byte[] pdf = Bytes(DrPepperCredits);

        Assert.Null(PdfRowSnipper.TryCut(pdf, 0, 0));
        Assert.Null(PdfRowSnipper.TryCut(pdf, 99_999, 99_999));
        Assert.Null(PdfRowSnipper.TryCut([1, 2, 3], 1, 1));
    }

    /// <summary>Asking for the rows the wrong way round still cuts the same band.</summary>
    [Fact]
    public void TheOrderOfTheTwoLineNumbersDoesNotMatter()
    {
        byte[] pdf = Bytes(DrPepperCredits);

        var forwards = PdfRowSnipper.TryCut(pdf, 30, 31);
        var backwards = PdfRowSnipper.TryCut(pdf, 31, 30);

        Assert.Equal(forwards!.Top, backwards!.Top);
        Assert.Equal(forwards.Height, backwards.Height);
        Assert.Equal(forwards.FocusTop, backwards.FocusTop);
    }

    private static double PageWidth(byte[] pdf, int pageNumber)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.GetPage(pageNumber).Width;
    }

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
