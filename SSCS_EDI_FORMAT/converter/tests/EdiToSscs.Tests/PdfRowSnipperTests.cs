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

    private static byte[] Bytes(string fixture) => File.ReadAllBytes(fixture);

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
