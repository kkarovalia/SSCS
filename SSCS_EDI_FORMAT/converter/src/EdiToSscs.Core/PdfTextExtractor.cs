using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace EdiToSscs.Core;

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
            throw new EdiConversionException(
                PdfReasonCodes.NoText,
                "This PDF has no readable text. It may be a scanned image without a searchable text layer. " +
                "Re-scan it with the scanner's \"searchable PDF\" / OCR setting turned on, then try again.");
        return all;
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
