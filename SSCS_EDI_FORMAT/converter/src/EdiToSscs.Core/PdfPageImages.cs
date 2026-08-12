using UglyToad.PdfPig;

namespace EdiToSscs.Core;

/// <summary>
/// The pictures a scanned PDF is actually made of, one per page, in page order.
///
/// A scanned page is a single image covering the whole sheet. A logo, a signature stamp or a barcode
/// glyph is not the page, so the largest image on each page is the only candidate — the same rule
/// <see cref="PdfBarcodeReader"/> and <see cref="PdfRowSnipper"/> apply for the same reason.
/// </summary>
public static class PdfPageImages
{
    /// <summary>
    /// Every page's picture as PNG bytes. A PDF that was never a scan, or a page whose image will
    /// not decode, contributes nothing and does not stop the rest. Never throws.
    /// </summary>
    public static IReadOnlyList<byte[]> TryReadAll(byte[] pdfBytes)
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

    private static List<byte[]> ReadAll(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var pages = new List<byte[]>();
        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            byte[]? png = null;
            try
            {
                var image = page.GetImages()
                    .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                    .FirstOrDefault();
                if (image is not null && image.TryGetPng(out byte[]? bytes)) png = bytes;
            }
            catch
            {
                // One unreadable page is not a reason to lose the others.
            }

            if (png is not null) pages.Add(png);
        }
        return pages;
    }
}
