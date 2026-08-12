using System.Drawing;
using System.Drawing.Drawing2D;
using UglyToad.PdfPig;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace EdiToSscs.Core;

/// <summary>
/// One barcode read off the printed bars, and which text line of the invoice it was printed on.
///
/// The line number is what ties it to a product row. A barcode floating on a page belongs to
/// nothing; borrowed by the wrong row it books the delivery onto another item entirely, and no
/// checksum on the page would notice.
/// </summary>
public sealed record PdfBarcode(string Text, int PageNumber, int LineNumber);

/// <summary>
/// Reads the barcodes a scanned invoice prints, from the **bars** rather than from the digits
/// underneath them.
///
/// Why this is worth having. Every other reading on these pages comes out of the same OCR pass, so
/// two readings that agree are usually one mistake twice — that is the whole difficulty with this
/// vendor. The bars are different in kind: they are a machine-readable encoding with error
/// correction built into them, decoded by a completely separate piece of code, so a decode and an
/// OCR reading agreeing is genuine corroboration.
///
/// Why it is not simply trusted. Measured across the store's twenty Red Bull tickets on 2026-08-11:
/// **174 decodes, 168 right, 6 wrong** — and the wrong ones satisfied their own UPC-A check digit,
/// which is what makes them dangerous rather than merely useless. One printed `611269546019` came
/// back as `611269546010` on three separate tickets, so it is not random noise: a particular
/// smudged print decodes wrongly the same way every time, exactly like the OCR does. Five of the
/// six fail Red Bull's company prefix and are thrown out by the caller; the sixth is why a decode
/// is never allowed to overrule a reading that already resolved.
///
/// Best-effort throughout: a PDF that was never a scan, an image that will not decode, a page with
/// no bars on it, all give an empty list. The conversion has to stand up without this.
/// </summary>
public static class PdfBarcodeReader
{
    /// <summary>
    /// A scan gives a UPC-A about two pixels per module at 200-300 dpi, which is on the edge of what
    /// ZXing will lock on to. Enlarging with nearest-neighbour first (no smoothing - blurring the
    /// bars is the one thing that cannot help) recovers a few more without inventing any: on the
    /// store's samples it turned 2 extra codes on one ticket and cost nothing anywhere.
    /// </summary>
    private static readonly int[] Enlargements = [1, 3];

    /// <summary>
    /// Every barcode on every page, tied to the line it was printed on. Never throws.
    /// </summary>
    public static IReadOnlyList<PdfBarcode> TryReadAll(byte[] pdfBytes)
    {
        try
        {
            return ReadAll(pdfBytes);
        }
        catch
        {
            return [];
        }
    }

    private static List<PdfBarcode> ReadAll(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        var found = new List<PdfBarcode>();

        var lines = PdfTextExtractor.ExtractPositionedLines(pdfBytes);
        using var doc = PdfDocument.Open(pdfBytes);

        foreach (var page in doc.GetPages())
        {
            // A scanned page is one image covering the whole sheet; a logo or a signature stamp is
            // not the page and cannot be measured against the text on it.
            var image = page.GetImages()
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();
            if (image is null || !image.TryGetPng(out byte[]? png) || png is null) continue;

            var onThisPage = lines.Where(l => l.PageNumber == page.Number).ToList();
            if (onThisPage.Count == 0) continue;

            using var original = (Bitmap)Image.FromStream(new MemoryStream(png));
            double scaleY = image.HeightInSamples / page.Height;

            var seen = new HashSet<(string Text, int Line)>();
            foreach (int enlargement in Enlargements)
            {
                using var bitmap = Enlarge(original, enlargement);
                foreach (var result in Decode(bitmap))
                {
                    if (result.ResultPoints.Length == 0) continue;

                    // Where the bars sit on the paper. PDF measures up from the bottom of the page
                    // and a picture measures down from its top.
                    double pixelY = result.ResultPoints.Average(p => p.Y) / enlargement;
                    double pdfY = page.Height - pixelY / scaleY;

                    int line = LineAt(onThisPage, pdfY);
                    if (line > 0 && seen.Add((result.Text, line)))
                        found.Add(new PdfBarcode(result.Text, page.Number, line));
                }
            }
        }

        return found;
    }

    private static IEnumerable<Result> Decode(Bitmap bitmap)
    {
        var reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                // Only the formats these invoices actually print. Letting ZXing try everything is
                // how a run of scanner ink becomes a confident ITF read of eight digits.
                PossibleFormats = [BarcodeFormat.UPC_A, BarcodeFormat.EAN_13],
            },
        };
        return reader.DecodeMultiple(bitmap) ?? [];
    }

    /// <summary>
    /// The text line the bars were printed across, or 0 when they sit clear of every line.
    ///
    /// Strict on purpose. A barcode is only useful attached to the right row, and a wrong
    /// attachment is worse than none at all: it would book the line onto a different product and
    /// every total on the page would still agree. So the bars have to overlap a line's own band,
    /// or fall within half a line's height of one; anything further away is discarded.
    /// </summary>
    private static int LineAt(IReadOnlyList<PdfTextLine> lines, double pdfY)
    {
        foreach (var l in lines)
            if (pdfY >= l.Bottom && pdfY <= l.Top) return l.LineNumber;

        PdfTextLine? best = null;
        double bestGap = double.MaxValue;
        foreach (var l in lines)
        {
            double gap = pdfY > l.Top ? pdfY - l.Top : l.Bottom - pdfY;
            double allowed = Math.Max(1.0, l.Top - l.Bottom) / 2;
            if (gap < bestGap && gap <= allowed) { bestGap = gap; best = l; }
        }
        return best?.LineNumber ?? 0;
    }

    private static Bitmap Enlarge(Bitmap source, int scale)
    {
        if (scale == 1) return new Bitmap(source);

        var big = new Bitmap(source.Width * scale, source.Height * scale);
        using var g = Graphics.FromImage(big);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(source, 0, 0, big.Width, big.Height);
        return big;
    }
}
