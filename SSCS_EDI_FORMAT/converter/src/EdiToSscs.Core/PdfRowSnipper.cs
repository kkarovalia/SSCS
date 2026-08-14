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
/// A vendor-verified horizontal column on the printed sheet, expressed as fractions of the page
/// width. This is a fallback only: a token or printed heading that can be placed exactly still wins.
/// It exists for fixed-form tickets whose photograph is sound while the hidden OCR text either
/// drops or corrupts the very figure being requested.
/// </summary>
public readonly record struct PdfColumnBand(double LeftFraction, double RightFraction);

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
    /// <param name="lastOfDuplicates">
    /// When the figure appears more than once on the row, mark the **last** one rather than giving up.
    ///
    /// Set only for a question about the extended amount, where it is a fact about the layout rather
    /// than a preference: both vendors print that figure in the final column — Red Bull's TOTAL after
    /// DEP, DISC and SUGAR, Dr Pepper's AMOUNT after TAX, and Dr Pepper's credits the same with the
    /// WHSLE column dropped. So the rightmost occurrence is the one being asked about.
    ///
    /// This exists because a **QTY 1 row prints its price and its total as the same characters** —
    /// `RB36463 1 12 RED BULL 20OZ LS $42.23 $0.00 $0.00 $0.00 $42.23` — and the duplicate guard then
    /// washed the whole row on every single-case line. Not intermittent, though it looks it from the
    /// operator's side: exact whenever QTY is 2 or more, whole-row every time QTY is 1.
    ///
    /// It must stay off for UNITS. That column sits in the middle of the row with counts either side
    /// of it, so the last "12" on the line is as likely to be the CASES figure or part of a
    /// description as the units — and the whole-row wash is the right answer there.
    /// </param>
    /// <param name="columnLeftOf">
    /// The heading word that the wanted column sits immediately to the left of — a fallback for when
    /// the figure being asked about is **not in the text layer at all**, so there is no token to find
    /// and the whole row would otherwise be washed.
    ///
    /// Bimbo needs this and the other two vendors do not, because their damaged figures are usually
    /// still *present* and merely mangled, which <see cref="FindSpan"/> can locate. Bimbo's QTY column
    /// is the one the 200 dpi scans delete outright — ticket 84294890010234 lost it on all forty rows
    /// — so the marker had nothing to attach to and every question arrived with the entire line lit
    /// up. The figure is plainly there in the *picture*, which is the whole premise of this file.
    ///
    /// The column is found by position rather than by its own heading text, because that heading is
    /// exactly what the scan destroys: QTY reads as `UTY`, `OTY`, `OW`, `UN` and `RTY` across the
    /// samples. `RETAIL` survives on all eleven and QTY is always the word before it, so the anchor is
    /// the neighbour rather than the thing itself. Used only when the figure could not be found on the
    /// row; a token that *was* found is always marked in preference to a column.
    /// </param>
    public static PdfRowSnip? TryCut(
        byte[] pdfBytes, int firstLineNo, int lastLineNo,
        string? focusText = null, int? focusLineNo = null, bool markWholeCode = false,
        bool lastOfDuplicates = false, string? columnLeftOf = null,
        PdfColumnBand? fallbackColumn = null)
    {
        try
        {
            return Cut(pdfBytes, firstLineNo, lastLineNo, focusText, focusLineNo, markWholeCode,
                lastOfDuplicates: lastOfDuplicates, columnLeftOf: columnLeftOf,
                fallbackColumn: fallbackColumn);
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
            {
                if (!IsColumnHeader(lines[i].Text)) continue;

                // Both fixed forms split their real headings over two printed lines. Bimbo puts
                // ITEM/SUGG RETAIL/WHOLESALE above UPC/QTY/RETAIL AMOUNT/PRICE/AMOUNT. Dr Pepper
                // puts DESCRIPTION/UPC-SKU above WHSLE/CASES/UNITS/NET/TAX/AMOUNT (or the shorter
                // CREDITS set). Keeping both lines is what makes this a photograph of the vendor's
                // header rather than a partial strip that no longer identifies the product column.
                int first = i > 0 && Continues(lines[i - 1].Text) ? i : i + 1;
                return Cut(pdfBytes, first, i + 1, null, i + 1, false, mark: false);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dr Pepper's complete photographed section header: the SALES or CREDITS banner, followed by
    /// DESCRIPTION/UPC-SKU and that section's numeric columns. Unlike the shared best-effort header
    /// cutter, this is strict about both the vendor form and the requested section. A Dr Pepper
    /// question must never put a SALES strip over a credit row or substitute drawn column names.
    /// </summary>
    public static PdfRowSnip? TryCutDrPepperHeader(
        byte[] pdfBytes, int beforeLineNo, bool credit)
    {
        try
        {
            var lines = PdfTextExtractor.ExtractPositionedLines(pdfBytes);
            for (int i = Math.Min(beforeLineNo, lines.Count) - 1; i >= 2; i--)
            {
                if (!IsDrPepperColumnHeader(lines[i].Text)
                    || !IsDrPepperUpperHeader(lines[i - 1].Text)
                    || !IsDrPepperSection(lines[i - 2].Text, credit)
                    || lines[i - 2].PageNumber != lines[i].PageNumber) continue;

                // i is zero-based for the lower column line. Cut wants one-based line numbers, so
                // i-1 is the SALES/CREDITS banner and i+1 is the lower column line.
                return Cut(pdfBytes, i - 1, i + 1, null, i + 1, false, mark: false);
            }
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
    ///
    /// The page opens at the top, every time. See the note in the body for the measurements that took
    /// out the band, and the scrolling, that a vision model's guess at the row's position once drove.
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

            // Nothing marked, and nothing scrolled to. The page is handed over whole, the caption on
            // the dialog says which row to look for, and the operator finds it.
            //
            // For one day the vision model was asked where each row sat, and the answer was used to
            // draw a band and to scroll the page to it. It was measured on 2026-08-12 by rendering
            // every prompt the store's thirty scans raise and looking at each one: the band was on the
            // row asked about 6 times out of 11 on Red Bull and 0 out of 8 on Dr Pepper — twice on the
            // address block at the head of the ticket, twice on the totals at the foot. Sharpening the
            // instruction (anchor to the whole sheet, keep the steps even, never pass the last product
            // row) moved the numbers slightly and changed nothing about the shape: the model spaces
            // the rows too far apart, so the error grows down the page and the last row of a ticket
            // lands past the end of the goods.
            //
            // Half-right is the worst of the three outcomes. No marker costs a glance down a page that
            // holds the row by construction; a marker on the right row saves that glance; a marker on
            // the *wrong* row is read, typed, and books a figure belonging to another product — and
            // every total still agrees, because it is a real figure off a real row. That is the same
            // rule as everywhere else here: confirm a figure or ask for it, never point confidently at
            // something worked out.
            return new PdfRowSnip(png, 0, 0, image.WidthInSamples, image.HeightInSamples, 0, 0, 0, 0);
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

    /// <summary>
    /// The row of column names, on whichever half of the ticket the row sits in — Dr Pepper's SALES
    /// or CREDITS block, Bimbo's DELIVERIES or RETURNS block. The nearest one above a row wins, which
    /// is what puts a credit row under the header its own section prints.
    ///
    /// Two patterns because the vendors name their columns differently, and each is chosen from what
    /// actually survived the scans rather than from what the paper says. Dr Pepper's `UNITS` and
    /// `AMOUNT` came through intact. Bimbo's did not: `AMOUNT` reaches the text layer as `ANOLNT`,
    /// `AHRUNT`, `AH00NT`, `AHUUNI`, `ANOUNT`, `ANO NT`, `AMONT` and `ANDUTI` across the samples, so
    /// matching on it would find the header on four tickets out of eleven. `RETAIL` and `PRICE` both
    /// survive on all eleven, and no Bimbo product row carries either word.
    /// </summary>
    private static bool IsColumnHeader(string line) =>
        IsDrPepperColumnHeader(line)
        || (line.Contains("RETAIL", StringComparison.OrdinalIgnoreCase)
            && line.Contains("PRICE", StringComparison.OrdinalIgnoreCase));

    private static bool IsDrPepperColumnHeader(string line) =>
        line.Contains("UNITS", StringComparison.OrdinalIgnoreCase)
        && line.Contains("AMOUNT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDrPepperUpperHeader(string line) =>
        line.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase)
        && (line.Contains("UPC", StringComparison.OrdinalIgnoreCase)
            || line.Contains("SKU", StringComparison.OrdinalIgnoreCase));

    private static bool IsDrPepperSection(string line, bool credit) =>
        line.Trim().Equals(credit ? "CREDITS" : "SALES", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the line above a column header is the top half of the same header — Bimbo prints
    /// "ITEM / SUGG RETAIL / WHOLESALE" over "UPC No QTY RETAIL AMOUNT PRICE AMOUNT", and the pair is
    /// what tells the two AMOUNT columns apart. `RETAIL` is the word that survived on all eleven
    /// readable samples; `PRICE` is deliberately not accepted here, because it appears only on the
    /// lower line and matching it would pull in a product row.
    /// </summary>
    private static bool Continues(string line) =>
        line.Contains("RETAIL", StringComparison.OrdinalIgnoreCase)
        && !line.Contains("PRICE", StringComparison.OrdinalIgnoreCase)
        || IsDrPepperUpperHeader(line);

    private static PdfRowSnip? Cut(
        byte[] pdfBytes, int firstLineNo, int lastLineNo,
        string? focusText, int? focusLineNo, bool markWholeCode, bool mark = true,
        bool lastOfDuplicates = false, string? columnLeftOf = null,
        PdfColumnBand? fallbackColumn = null)
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

        // The figure itself when it can be found on the row beyond doubt; failing that the column it
        // belongs to, where the caller can name one; failing both, the whole row.
        var span = markWholeCode ? FindCodeSpan(focus) : FindSpan(focus, focusText, lastOfDuplicates);
        span ??= FindColumnSpan(lines, focus, columnLeftOf);
        if (span is null && fallbackColumn is PdfColumnBand band
            && band.LeftFraction >= 0 && band.RightFraction <= 1
            && band.LeftFraction < band.RightFraction)
        {
            // Fixed-form vendor fallback. The Y coordinate still comes from the exact located row;
            // only X comes from the form. Fractions keep the same column on 200/300 dpi scans and
            // avoid tying a verified layout to one scanner resolution.
            span = (page.Width * band.LeftFraction, page.Width * band.RightFraction);
        }
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
    /// Where the wanted column runs, taken off the heading printed above this row — for a figure the
    /// scan deleted, where there is no token on the row to mark.
    ///
    /// The column is identified as the heading word immediately left of <paramref name="rightOf"/>,
    /// not by its own text, because its own text is what the scan destroys. See
    /// <see cref="TryCut"/>'s `columnLeftOf`.
    ///
    /// The span is the heading's own, opened out — one heading width to the left and three quarters
    /// of one to the right. Bimbo's figures are **right-aligned** just past the heading's right edge
    /// and grow leftward with their digit count, so a box drawn tight to the heading clips both ends.
    /// Measured across the samples: heading `OTY` at 162.1–171.9 carries counts at 168.6–171.9 (`1`),
    /// 167.6–172.6 (`8`) and 165.4–172.5 (`12`), and the returns band's 165.0–174.8 carries
    /// 170.7–178.1 (`-1`). A **returned** line runs furthest right of all, because the minus sign
    /// pushes the digits along, so the right-hand margin is the one that has to be generous.
    ///
    /// The box holds every one of them and still clears its neighbours at both ends: the description
    /// finishes by ~150 and the SUGG RETAIL figures start at ~187, against a box running roughly
    /// 152–186. It cannot spill onto the column either side, which is the property that matters — a
    /// confident marker on the wrong figure is read, typed, and booked.
    ///
    /// Null whenever anything is not exactly as expected — no heading above the row, no such
    /// neighbour, nothing to its left — and the caller then washes the whole row as before. A wrong
    /// box is worse than a wide one.
    /// </summary>
    private static (double Left, double Right)? FindColumnSpan(
        IReadOnlyList<PdfTextLine> lines, PdfTextLine row, string? rightOf)
    {
        if (string.IsNullOrWhiteSpace(rightOf)) return null;

        for (int i = Math.Min(row.LineNumber, lines.Count) - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.PageNumber != row.PageNumber) return null;
            if (!IsColumnHeader(line.Text)) continue;

            int at = -1;
            for (int w = 0; w < line.Words.Count; w++)
                if (line.Words[w].Text.Contains(rightOf, StringComparison.OrdinalIgnoreCase))
                {
                    at = w;
                    break;
                }

            if (at <= 0) return null;

            var heading = line.Words[at - 1];
            double width = Math.Max(1.0, heading.Right - heading.Left);
            return (heading.Left - width, heading.Right + width * 0.75);
        }

        return null;
    }

    /// <summary>
    /// Where on the row a figure is printed, or null when it cannot be placed beyond doubt.
    ///
    /// Ambiguity gives up rather than guesses. "20.66" appears twice on "20.66 0.00 20.66" and
    /// nothing in the text says which one is meant, so the whole row is marked instead. A marker on
    /// the wrong figure is worse than no marker: the operator reads it, types it, and every check on
    /// the page then agrees with a number belonging to something else.
    ///
    /// <paramref name="lastWins"/> lifts that for the one case where the layout does say — the
    /// extended amount is the last column on both vendors, so the rightmost match is it. See
    /// <see cref="TryCut"/>. Without it a QTY 1 row, whose price and total are the same characters,
    /// could never be marked at all.
    /// </summary>
    private static (double Left, double Right)? FindSpan(
        PdfTextLine line, string? focusText, bool lastWins = false)
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
            if (found is not null && !lastWins) return null;   // the same figure twice on one row

            var here = Span(word, 0, word.Text.Length);
            if (here is null) return null;
            found = here;                                      // lastWins: keep walking, right one wins
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
    ///  * **A damaged code is not sliced from OCR character positions.** Where the scan read more
    ///    characters than the paper has ("Limda0i8000200.287/2.0.020,4.68"), OCR spreads its
    ///    characters across the token's real width. A vendor adapter can then use its verified UPC
    ///    column; without that geometry the caller falls back to the whole row rather than drawing
    ///    a confident box around the wrong eleven digits or around the unwanted SKU.
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

            // Damaged. Do not box the UPC/SKU token as a whole: the SKU is not the requested value.
            // A vendor adapter with verified form geometry can now select only the UPC column;
            // without one, the caller gets the unconfident whole-row fallback.
            return null;
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
