using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace EdiToSscs.Core;

/// <summary>One word of a PDF's text layer with the horizontal span it occupies on the page.</summary>
public sealed record PdfPositionedWord(string Text, double Left, double Right)
{
    public double Centre => (Left + Right) / 2;
}

/// <summary>One page as visual rows of positioned words, top row first.</summary>
public sealed record PdfPage(double Width, IReadOnlyList<IReadOnlyList<PdfPositionedWord>> Rows);

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
    public static IReadOnlyList<string> ExtractLines(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        var all = new List<string>();
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            foreach (var page in doc.GetPages())
            {
                var lines = ReconstructLines(page);
                // The vendor scan/barcode sheet is appended after the invoice; never parse it or anything after it.
                if (lines.Any(l => l.Contains("DEBIT SCAN SHEET", StringComparison.OrdinalIgnoreCase)))
                    break;
                all.AddRange(lines);
            }
        }

        if (!all.Any(l => l.Any(char.IsLetterOrDigit)))
            throw new EdiConversionException(PdfReasonCodes.NoText, NoTextMessage);
        return all;
    }

    /// <summary>
    /// The same pages, but keeping each word's horizontal position.
    ///
    /// <see cref="ExtractLines"/> throws the coordinates away, which is fine for a vendor whose
    /// columns can be told apart by reading order. Red Bull's cannot: its scans routinely split the
    /// ID column into two tokens ("11 136463" for RB136463) and drop a money column altogether, and
    /// after that no amount of counting tokens says which figure is the price and which is the
    /// total. Column x-positions survive damage that token order does not.
    /// </summary>
    public static IReadOnlyList<PdfPage> ExtractPages(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        var pages = new List<PdfPage>();
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            foreach (var page in doc.GetPages())
            {
                var rows = ReconstructRows(page);
                if (rows.Any(r => r.Any(w => w.Text.Contains("DEBIT SCAN SHEET", StringComparison.OrdinalIgnoreCase))))
                    break;
                pages.Add(new PdfPage(page.Width, rows));
            }
        }

        if (!pages.Any(p => p.Rows.Any(r => r.Any(w => w.Text.Any(char.IsLetterOrDigit)))))
            throw new EdiConversionException(PdfReasonCodes.NoText, NoTextMessage);
        return pages;
    }

    private const string NoTextMessage =
        "This PDF has no readable text. It may be a scanned image without a searchable text layer. " +
        "Re-scan it with the scanner's \"searchable PDF\" / OCR setting turned on, then try again.";

    private static List<IReadOnlyList<PdfPositionedWord>> ReconstructRows(Page page)
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

        return buckets
            .OrderByDescending(b => b.Y)
            .Select(b => (IReadOnlyList<PdfPositionedWord>)b.Words
                .OrderBy(w => w.BoundingBox.Left)
                .Select(w => new PdfPositionedWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Right))
                .ToList())
            .ToList();
    }

    private static List<string> ReconstructLines(Page page)
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

        return buckets
            .OrderByDescending(b => b.Y) // PDF Y increases upward; top of page first
            .Select(b => string.Join(" ", b.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();
    }
}
