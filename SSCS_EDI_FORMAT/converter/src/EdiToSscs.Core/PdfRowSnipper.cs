using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace EdiToSscs.Core;

/// <summary>
/// A piece of the scanned page: the picture of the whole page, the part of it worth looking at, and
/// within that, the one row being talked about.
///
/// The cut is left as a rectangle rather than done here because the pixels are a display concern,
/// and because several snips off one page then share a single copy of that page's picture.
///
/// The Focus rectangle matters more than it looks. A snip shows the row above for context and a
/// sliver of whatever the padding caught, so without a marker the operator has to work out what is
/// being asked about — and reading the wrong thing is the exact mistake this feature exists to stop.
/// It is drawn around the **figure itself** wherever that figure can be located unambiguously, so
/// that a barcode prompt marks 078000200287 and not 078000200287/20020468, and falls back to the
/// whole row only when nothing can be pinned down.
/// </summary>
public sealed record PdfRowSnip(
    byte[] PagePng, int Left, int Top, int Width, int Height,
    int FocusLeft, int FocusTop, int FocusWidth, int FocusHeight);

/// <summary>
/// Cuts the picture of a printed row out of a scanned invoice.
///
/// These PDFs are photographs of paper with a machine's transcription laid underneath. Every bug
/// this converter has ever had came out of that transcription; the photograph itself has never been
/// wrong. So where a figure cannot be read — or was read and had to be repaired — the honest thing
/// to put in front of the operator is the photograph of that row, not our reading of it.
///
/// The picture holds the answer even when the text layer does not: on invoice 4643519283 the row
/// "35.90 1 12 20.66 0.00 20.66" reached the text layer as "20.66 0.00 20.66", and all six figures
/// are plainly legible in the scan.
/// </summary>
public static class PdfRowSnipper
{
    /// <summary>Padding above and below the rows, in multiples of their own height.</summary>
    private const double Breathing = 0.45;

    /// <summary>
    /// Cuts out the given lines, numbered as <see cref="PdfTextExtractor.ExtractLines"/> numbers
    /// them (1-based) — which is how every parser reports a line. Returns null when there is nothing
    /// to show: a PDF built from real text rather than a scan, a page whose picture will not decode,
    /// or a line number that is not on the page.
    ///
    /// Never throws. A snip is a courtesy on top of a message that already stands on its own, so a
    /// PDF that will not give one up must not cost the operator their conversion.
    /// </summary>
    public static PdfRowSnip? TryCut(
        byte[] pdfBytes, int firstLineNo, int lastLineNo,
        string? focusText = null, int? focusLineNo = null, bool markWholeCode = false)
    {
        try
        {
            return Cut(pdfBytes, firstLineNo, lastLineNo, focusText, focusLineNo, markWholeCode);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The invoice's own column header — "WHSLE CASES UNITS NET TAX AMOUNT" — as printed above the
    /// row in question, or null if there is none above it.
    ///
    /// Shown over a snip it is the legend for the columns, cut from the same page at the same width,
    /// so WHSLE sits above WHSLE and AMOUNT above AMOUNT. That is what makes a question answerable
    /// when the marker cannot be trusted to a single figure: the operator can still see which column
    /// is being asked about. The nearest header above wins, which is what puts a credit row under
    /// the CREDITS header — it prints its own, and it has no WHSLE column.
    /// </summary>
    public static PdfRowSnip? TryCutHeader(byte[] pdfBytes, int beforeLineNo)
    {
        try
        {
            var lines = PdfTextExtractor.ExtractPositionedLines(pdfBytes);
            for (int i = Math.Min(beforeLineNo, lines.Count) - 1; i >= 0; i--)
                if (IsColumnHeader(lines[i].Text)) return TryCutHeaderLine(pdfBytes, i + 1);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The given line on its own with nothing marked, for a caller that has already worked out which
    /// line is the column header.
    ///
    /// Red Bull needs this. Its headings reach the text layer as debris — "PflICE", "6UDAn",
    /// "IDEAL" for PRICE, SUGAR and TOTAL — so no amount of reading finds them, and they can only be
    /// had by position. The picture of that same line is perfectly legible, which is the whole
    /// argument of this file in one example: the transcription is wrong and the photograph is not.
    /// </summary>
    public static PdfRowSnip? TryCutHeaderLine(byte[] pdfBytes, int lineNo)
    {
        try
        {
            return Cut(pdfBytes, lineNo, lineNo, null, lineNo, false, mark: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The whole page, with nothing marked — for a row that has no line number to cut around.
    ///
    /// This is the case a page with no text layer at all creates. The rows come off the picture, so
    /// there are no coordinates, so there is no band of paper to cut and every question about such a
    /// row used to arrive bare: "type the barcode for 12z12p DrPep DEP" against a description the
    /// model itself half-garbled. That is the one prompt in this application that does not show the
    /// operator the paper, on exactly the invoices that ask the most questions.
    ///
    /// Showing the whole page fixes that without inventing anything. **Nothing is marked, and that
    /// is the point** — a marker on a guessed position would put a real row of a real invoice on
    /// screen with the wrong figure highlighted, which is worse than no picture at all. The page
    /// contains the row by construction, the operator finds it by name, and the answer is read off
    /// the screen instead of hunted for on paper.
    /// </summary>
    /// <param name="pageNumber">1-based page, as <see cref="VisionRow.Page"/> records it.</param>
    /// <param name="upsideDown">
    /// Turn the picture 180° before showing it. Four of the store's thirty samples went through the
    /// scanner inverted, and those are disproportionately the ones that end up here — an inverted page
    /// is exactly what destroys the text layer in the first place. Showing one the wrong way up is
    /// worse than showing nothing, so this is worth the field it takes to carry.
    /// </param>
    public static PdfRowSnip? TryCutWholePage(byte[] pdfBytes, int pageNumber, bool upsideDown = false)
    {
        try
        {
            if (pdfBytes is null || pageNumber < 1) return null;

            using var doc = PdfDocument.Open(pdfBytes);
            if (pageNumber > doc.NumberOfPages) return null;

            var image = doc.GetPage(pageNumber).GetImages()
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();
            if (image is null || !image.TryGetPng(out byte[]? png) || png is null) return null;

            if (upsideDown) png = Turn(png) ?? png;

            // Zero focus height is how this record says "no marker" - the same thing a column
            // header snip does.
            return new PdfRowSnip(
                png, 0, 0, image.WidthInSamples, image.HeightInSamples, 0, 0, 0, 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The same picture the other way up. Null if it will not turn, in which case the caller shows
    /// what it had — an inverted page beats no page, and neither one can be wrong about a figure.
    /// </summary>
    private static byte[]? Turn(byte[] png)
    {
        try
        {
            using var source = new System.Drawing.Bitmap(new MemoryStream(png));
            source.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone);
            var turned = new MemoryStream();
            source.Save(turned, System.Drawing.Imaging.ImageFormat.Png);
            return turned.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The row of column names, on either the SALES or the CREDITS half of the ticket.</summary>
    private static bool IsColumnHeader(string line) =>
        line.Contains("UNITS", StringComparison.OrdinalIgnoreCase)
        && line.Contains("AMOUNT", StringComparison.OrdinalIgnoreCase);

    private static PdfRowSnip? Cut(
        byte[] pdfBytes, int firstLineNo, int lastLineNo,
        string? focusText, int? focusLineNo, bool markWholeCode, bool mark = true)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (firstLineNo > lastLineNo) (firstLineNo, lastLineNo) = (lastLineNo, firstLineNo);

        var lines = PdfTextExtractor.ExtractPositionedLines(pdfBytes);
        if (firstLineNo < 1 || lastLineNo > lines.Count) return null;

        // A snip spanning a page break has no single picture to come from; keep the asked-for line.
        int pageNumber = lines[lastLineNo - 1].PageNumber;
        var wanted = lines
            .Skip(firstLineNo - 1)
            .Take(lastLineNo - firstLineNo + 1)
            .Where(l => l.PageNumber == pageNumber)
            .ToList();
        if (wanted.Count == 0) return null;

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(pageNumber);

        // A scanned page is one image covering the whole sheet. Anything else — a logo, a signature
        // stamp, a PDF that was never a scan — is not the page and cannot be cut from.
        var image = page.GetImages()
            .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
            .FirstOrDefault();
        if (image is null || !image.TryGetPng(out byte[]? png) || png is null) return null;

        double scaleX = image.WidthInSamples / page.Width;
        double scaleY = image.HeightInSamples / page.Height;
        // Which of the snipped rows is being asked about. Usually the last — the priced row — but a
        // barcode is printed on the product line above it, and marking the row that does not carry
        // the barcode while asking for the barcode is worse than marking nothing.
        var focus = wanted.FirstOrDefault(l => l == LineAt(lines, focusLineNo)) ?? wanted[^1];
        double padding = Math.Max(1.0, focus.Top - focus.Bottom) * Breathing;

        // PDF measures up from the bottom of the page and a picture measures down from its top.
        int Down(double pdfY) =>
            Math.Clamp((int)Math.Round((page.Height - pdfY) * scaleY), 0, image.HeightInSamples);
        int Across(double pdfX) =>
            Math.Clamp((int)Math.Round(pdfX * scaleX), 0, image.WidthInSamples);

        int top = Down(wanted.Max(l => l.Top) + padding);
        int bottom = Math.Max(top + 1, Down(wanted.Min(l => l.Bottom) - padding));
        int focusTop = Down(focus.Top + padding / 2);
        int focusBottom = Math.Max(focusTop + 1, Down(focus.Bottom - padding / 2));

        // The figure itself when it can be found on the row beyond doubt, otherwise the whole row.
        var span = markWholeCode ? FindCodeSpan(focus) : FindSpan(focus, focusText);
        int focusLeft = 0, focusWidth = image.WidthInSamples;
        if (span is not null)
        {
            double bleed = (focus.Top - focus.Bottom) * 0.25;   // a little air around the digits
            focusLeft = Across(span.Value.Left - bleed);
            focusWidth = Math.Max(1, Across(span.Value.Right + bleed) - focusLeft);
        }

        // Full width for the cut every time. These invoices are columns, and a row trimmed to its own
        // text loses the AMOUNT at the right-hand end — often the very figure being asked about.
        // A cut with nothing to mark (the column header) carries no marker at all.
        return new PdfRowSnip(
            png, 0, top, image.WidthInSamples, bottom - top,
            focusLeft, focusTop - top, focusWidth, mark ? focusBottom - focusTop : 0);
    }

    /// <summary>The line with that 1-based number, or null when there is no such line.</summary>
    private static PdfTextLine? LineAt(IReadOnlyList<PdfTextLine> lines, int? lineNo) =>
        lineNo is int n && n >= 1 && n <= lines.Count ? lines[n - 1] : null;

    /// <summary>
    /// Where on the row a figure is printed, or null when it cannot be placed beyond doubt.
    ///
    /// Ambiguity gives up rather than guesses. "20.66" appears twice on "20.66 0.00 20.66" and there
    /// is no telling from the text which one is meant, so the whole row is marked instead. A marker
    /// on the wrong figure is worse than no marker: the operator reads it, types it, and every check
    /// on the page then agrees with a number belonging to something else.
    /// </summary>
    private static (double Left, double Right)? FindSpan(PdfTextLine line, string? focusText)
    {
        string needle = (focusText ?? "").Trim();
        if (needle.Length == 0) return null;

        (double Left, double Right)? found = null;
        foreach (var word in line.Words)
        {
            // The whole token or nothing. Part of a token cannot be measured out of it: where the
            // scan invented characters the paper does not have ("2.0.020,4.68" for "20020468") the
            // character boxes no longer line up with the print, and a marker drawn from them lands
            // a character or two off the figure it is pointing at.
            if (word.Text != needle) continue;
            if (found is not null) return null;      // the same figure twice on one row

            found = Span(word, 0, word.Text.Length);
            if (found is null) return null;
        }

        return found;
    }

    /// <summary>
    /// The whole printed barcode on a product line: the run of digits and scanner ink ending at the
    /// slash before the SKU.
    ///
    /// Two accuracies are available here and the difference matters, so both are used:
    ///
    ///  * **A clean code gets a box around its twelve digits.** Where the scan transcribed the token
    ///    character for character, each character's own box is where that character is printed, and
    ///    the twelve digits before the slash can be marked exactly — SKU outside, description
    ///    outside.
    ///  * **A damaged code gets a box around the whole token instead.** Where the scan read more
    ///    characters than the paper has ("Limda0i8000200.287/2.0.020,4.68" for a token of 26
    ///    printed characters), OCR spreads its characters evenly across the token's real width: the
    ///    advance comes out at 3.5pt where the paper's is 4.2. The token's *outside edges* stay
    ///    honest, so it can be boxed as a whole — but nothing inside it can, and a box drawn from
    ///    those positions covers ten of the twelve digits while looking perfectly confident.
    ///
    /// Marking part of a barcode is not a smaller version of marking all of it. It is a wrong answer
    /// that gets typed in: eleven digits inside a box would have a check digit worked out for them
    /// and land on an item that is not this one.
    /// </summary>
    private static (double Left, double Right)? FindCodeSpan(PdfTextLine line)
    {
        foreach (var word in line.Words)
        {
            int slash = word.Text.LastIndexOf('/');
            if (slash <= 0 || word.Letters.Count != word.Text.Length) continue;

            // The twelve characters before the slash, all of them plain digits: the scan agrees with
            // the paper here, so their own boxes can be trusted.
            // Twelve plain digits, so the scan and the paper agree here and the marker can be exact.
            // A thirteenth digit in front of them is the description's last letter read as one
            // ("SourGumSp1842595136050"), and leaving it outside the box is right: the barcode is
            // the twelve before the slash.
            int start = slash - BarcodeDigits;
            if (start >= 0 && word.Text.AsSpan(start, BarcodeDigits).IndexOfAnyExcept(Digits) < 0)
                return Span(word, start, BarcodeDigits);

            // Damaged. The token's edges are still where the token is printed, so box the lot; the
            // hint under the box says which half to type, and typing both halves is accepted anyway.
            return (word.Left, word.Right);
        }

        return null;
    }

    /// <summary>A barcode on these invoices is a UPC-A: twelve printed digits.</summary>
    private const int BarcodeDigits = 12;

    private static readonly System.Buffers.SearchValues<char> Digits =
        System.Buffers.SearchValues.Create("0123456789");

    /// <summary>
    /// The printed span of <paramref name="length"/> characters from <paramref name="at"/>, taken
    /// from the characters' own boxes rather than by dividing the word's width — the transcription
    /// and the paper routinely disagree about how many characters a token has, and an estimate then
    /// drifts by a whole character and marks the wrong digits. Null when they disagree, which is a
    /// case for marking nothing.
    /// </summary>
    private static (double Left, double Right)? Span(PdfWordSpan word, int at, int length)
    {
        if (word.Letters.Count != word.Text.Length) return null;
        if (at < 0 || length <= 0 || at + length > word.Letters.Count) return null;
        return (word.Letters[at].Left, word.Letters[at + length - 1].Right);
    }
}
