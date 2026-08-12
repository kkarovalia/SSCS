using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace EdiToSscs.Core;

/// <summary>
/// One word of a PDF's text layer with the horizontal span it occupies on the page, and the number
/// of the line it sits on.
///
/// The line number is what lets a vendor read by column position still point at the paper. A parser
/// working from positioned words has no line numbers of its own, so without this the picture of the
/// row could only be found by guessing which line the row came from.
/// </summary>
public sealed record PdfPositionedWord(string Text, double Left, double Right, int LineNumber = 0)
{
    public double Centre => (Left + Right) / 2;
}

/// <summary>One page as visual rows of positioned words, top row first.</summary>
public sealed record PdfPage(double Width, IReadOnlyList<IReadOnlyList<PdfPositionedWord>> Rows);

/// <summary>
/// One reconstructed visual line together with where it sits on the page it came from, in PDF
/// points with the origin at the bottom-left.
///
/// This is what lets a message point at the paper instead of describing it: the same line the
/// parser is complaining about can be cut out of the scanned picture and shown to the operator.
/// </summary>
public sealed record PdfTextLine(
    int LineNumber, int PageNumber, string Text, double Bottom, double Top, IReadOnlyList<PdfWordSpan> Words);

/// <summary>Where one character was printed, left and right edge, in PDF points.</summary>
public sealed record PdfCharSpan(double Left, double Right);

/// <summary>
/// One word with the exact printed span of every character in it.
///
/// Per character rather than per word, because estimating a character's position by dividing the
/// word's width is wrong exactly when it matters: the scan's transcription of a token routinely has
/// a different number of characters than the paper does — "Limda0i8000200.287" for the printed
/// "Limda078000200287" — so an estimate drifts by a whole character and marks the wrong digits.
/// </summary>
public sealed record PdfWordSpan(
    string Text, double Left, double Right, IReadOnlyList<PdfCharSpan> Letters);

/// <summary>
/// Extracts the text layer of a searchable PDF as reconstructed visual lines, using PdfPig
/// (pure managed, offline, bundled into the single-file EXE). Words are grouped onto a line by
/// their baseline and ordered left-to-right, which reproduces the invoice's row structure far more
/// reliably than a flat text dump. Vendor barcode "scan sheet" pages appended after the invoice are
/// dropped so they never feed the parser.
/// </summary>
public static class PdfTextExtractor
{
    // Words whose baselines are within this many PDF units are treated as the same visual line.
    private const double LineTolerance = 3.0;

    /// <summary>True if the bytes begin with the PDF signature "%PDF-".</summary>
    public static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';

    /// <summary>
    /// Returns the invoice's text lines in reading order across all invoice pages. Pages that are
    /// barcode scan sheets (containing "DEBIT SCAN SHEET") are excluded. Throws
    /// <see cref="EdiConversionException"/> with a plain message if the PDF has no readable text.
    /// </summary>
    public static IReadOnlyList<string> ExtractLines(byte[] pdfBytes) =>
        ExtractPositionedLines(pdfBytes).Select(l => l.Text).ToList();

    /// <summary>
    /// The same lines, in the same order, each carrying the band of page it occupies.
    ///
    /// <see cref="ExtractLines"/> is projected straight off this, deliberately: a parser reports a
    /// problem by line number, and a snip of the paper is only worth showing if it is certainly the
    /// same line. Two separate passes could drift apart on one grouping tweak; one pass cannot.
    /// </summary>
    public static IReadOnlyList<PdfTextLine> ExtractPositionedLines(byte[] pdfBytes)
    {
        var all = Reconstruct(pdfBytes).SelectMany(p => p.Lines).ToList();
        if (!all.Any(l => l.Text.Any(char.IsLetterOrDigit)))
            throw new EdiConversionException(PdfReasonCodes.NoText, NoTextMessage);
        return all;
    }

    /// <summary>
    /// The same pages, but as rows of words keeping each word's horizontal position.
    ///
    /// <see cref="ExtractLines"/> throws the coordinates away, which is fine for a vendor whose
    /// columns can be told apart by reading order. Red Bull's cannot: its scans routinely split the
    /// ID column into two tokens ("11 136463" for RB136463) and drop a money column altogether, and
    /// after that no amount of counting tokens says which figure is the price and which is the
    /// total. Column x-positions survive damage that token order does not.
    ///
    /// Projected off the same single pass as everything else, so a row here and the line the
    /// snipper cuts for it are the same row by construction rather than by two algorithms happening
    /// to agree. They did agree, character for character — but "happens to agree" is a thing that
    /// stops being true on a tweak nobody connects to the picture coming out one row low.
    /// </summary>
    public static IReadOnlyList<PdfPage> ExtractPages(byte[] pdfBytes)
    {
        var pages = Reconstruct(pdfBytes)
            .Select(p => new PdfPage(p.Width, p.Lines
                .Select(l => (IReadOnlyList<PdfPositionedWord>)l.Words
                    .Select(w => new PdfPositionedWord(w.Text, w.Left, w.Right, l.LineNumber))
                    .ToList())
                .ToList()))
            .ToList();

        if (!pages.Any(p => p.Rows.Any(r => r.Any(w => w.Text.Any(char.IsLetterOrDigit)))))
            throw new EdiConversionException(PdfReasonCodes.NoText, NoTextMessage);
        return pages;
    }

    private const string NoTextMessage =
        "This PDF has no readable text. It may be a scanned image without a searchable text layer. " +
        "Re-scan it with the scanner's \"searchable PDF\" / OCR setting turned on, then try again.";

    /// <summary>One page's reconstructed lines, numbered continuously across the whole document.</summary>
    private sealed record ReconstructedPage(double Width, IReadOnlyList<PdfTextLine> Lines);

    /// <summary>
    /// The one pass every other reader is built on: words bucketed onto visual lines, lines numbered
    /// 1-based across the document in reading order. Pages from the vendor's appended barcode sheet
    /// onwards are dropped so they never feed a parser.
    /// </summary>
    private static List<ReconstructedPage> Reconstruct(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        var pages = new List<ReconstructedPage>();
        int lineNumber = 0;

        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            var lines = ReconstructLines(page, ref lineNumber);
            if (lines.Any(l => l.Text.Contains("DEBIT SCAN SHEET", StringComparison.OrdinalIgnoreCase)))
                break;
            pages.Add(new ReconstructedPage(page.Width, lines));
        }

        return pages;
    }

    private static List<PdfTextLine> ReconstructLines(Page page, ref int lineNumber)
    {
        var buckets = new List<(double Y, List<Word> Words)>();
        foreach (var w in page.GetWords())
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            double y = w.BoundingBox.Bottom;
            int idx = buckets.FindIndex(b => Math.Abs(b.Y - y) <= LineTolerance);
            if (idx < 0) buckets.Add((y, [w]));
            else buckets[idx].Words.Add(w);
        }

        var lines = new List<PdfTextLine>(buckets.Count);
        foreach (var b in buckets.OrderByDescending(b => b.Y)) // PDF Y increases upward; top of page first
        {
            var ordered = b.Words.OrderBy(w => w.BoundingBox.Left).ToList();
            lines.Add(new PdfTextLine(
                ++lineNumber,
                page.Number,
                string.Join(" ", ordered.Select(w => w.Text)),
                // The band the row occupies, taken from every word on it: a tall glyph or a
                // subscripted one would otherwise clip the picture cut from these bounds.
                ordered.Min(w => w.BoundingBox.Bottom),
                ordered.Max(w => w.BoundingBox.Top),
                ordered.Select(w => new PdfWordSpan(
                    w.Text,
                    w.BoundingBox.Left,
                    w.BoundingBox.Right,
                    w.Letters.Select(c => new PdfCharSpan(
                        c.GlyphRectangle.Left, c.GlyphRectangle.Right)).ToList())).ToList()));
        }

        return lines;
    }
}
