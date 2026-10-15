using System.Drawing.Imaging;
using UglyToad.PdfPig;

namespace EdiToSscs.Core;

/// <summary>One picture (or one whole PDF) as it is sent to the reader.</summary>
public sealed record InvoicePicture(string MimeType, byte[] Bytes);

/// <summary>
/// Turns whatever was dropped on the Modisoft tab into what Gemini is sent.
///
/// The five known vendors all come off the store scanner, so their reader takes each page's scanned
/// picture and nothing else. An unknown vendor's invoice can be anything: a scan, a PDF with real
/// text that was emailed by the vendor (Silver Eagle's <c>invoice-report.pdf</c> had no picture in
/// it at all), or phone photos. So:
///
/// - a PDF whose every page is one scanned picture sends those pictures, a photo's JPEG as it is
///   rather than blown up into a PNG ten times the size;
/// - any other PDF is sent whole, and Gemini reads it the way it reads any PDF;
/// - a JPEG, PNG, WEBP or HEIC photo is sent as it is.
///
/// Google refuses a request over 20 MB, and the pictures travel as base64, a third bigger again.
/// Three phone photos can be that much on their own, so anything over <see cref="Budget"/> is
/// shrunk until it fits, never cut.
/// </summary>
public static class InvoicePictures
{
    /// <summary>The most picture bytes one request carries: 14 MB is under 19 MB once encoded.</summary>
    public const int Budget = 14 * 1024 * 1024;

    /// <summary>
    /// A page counts as a scan when its biggest picture covers at least this much of it. A text PDF
    /// with a logo on it is not a scan, and sending only the logo would lose the whole invoice.
    /// </summary>
    private const double ScanCoverage = 0.5;

    /// <summary>
    /// The picture type of a photo file, from its first bytes, or null when it is not one Gemini
    /// takes. The name of the file is not trusted: a phone's "photo.jpg" can be a HEIC.
    /// </summary>
    public static string? PictureType(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N'
            && bytes[3] == (byte)'G')
            return "image/png";
        if (bytes.Length >= 12 && Ascii(bytes, 0, 4) == "RIFF" && Ascii(bytes, 8, 4) == "WEBP")
            return "image/webp";
        if (bytes.Length >= 12 && Ascii(bytes, 4, 4) == "ftyp")
        {
            string brand = Ascii(bytes, 8, 4);
            if (brand is "heic" or "heix" or "hevc" or "hevx") return "image/heic";
            if (brand is "mif1" or "msf1" or "heif") return "image/heif";
        }
        return null;
    }

    /// <summary>
    /// What to send for this file, in page order. Empty when the file is neither a PDF nor a photo
    /// Gemini takes. Never throws.
    /// </summary>
    public static IReadOnlyList<InvoicePicture> Prepare(byte[] file)
    {
        try
        {
            if (file is null || file.Length == 0) return [];

            IReadOnlyList<InvoicePicture> parts;
            if (PdfTextExtractor.LooksLikePdf(file))
                parts = ScannedPages(file) ?? [new InvoicePicture("application/pdf", file)];
            else if (PictureType(file) is string mime)
                parts = [new InvoicePicture(mime, file)];
            else
                return [];

            return Fit(parts);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Each page's scanned picture, or null when any page is not simply a scan — then the PDF goes
    /// as it is, so nothing printed as text is left behind.
    /// </summary>
    private static List<InvoicePicture>? ScannedPages(byte[] pdf)
    {
        var pages = new List<InvoicePicture>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            var image = page.GetImages()
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();
            if (image is null) return null;

            double pageArea = page.Width * page.Height;
            double imageArea = image.BoundingBox.Width * image.BoundingBox.Height;
            if (pageArea <= 0 || imageArea / pageArea < ScanCoverage) return null;

            // A photo is stored in the PDF as a JPEG already; send that, not a PNG of it.
            ReadOnlySpan<byte> raw = image.RawMemory.Span;
            if (raw.Length >= 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF)
            {
                pages.Add(new InvoicePicture("image/jpeg", raw.ToArray()));
                continue;
            }

            if (!image.TryGetPng(out byte[]? png) || png is null) return null;
            pages.Add(new InvoicePicture("image/png", png));
        }
        return pages.Count == 0 ? null : pages;
    }

    /// <summary>
    /// Shrinks the biggest pictures until the whole request fits. Each picture gets an even share
    /// of the budget. Only JPEG and PNG can be redrawn here; a whole PDF, WEBP or HEIC goes as it
    /// is, and Google says so if it is too big.
    /// </summary>
    private static IReadOnlyList<InvoicePicture> Fit(IReadOnlyList<InvoicePicture> parts)
    {
        if (parts.Sum(p => (long)p.Bytes.Length) <= Budget) return parts;

        int share = Budget / parts.Count;
        return parts
            .Select(p => p.Bytes.Length > share && p.MimeType is "image/jpeg" or "image/png"
                ? Shrink(p, share) ?? p
                : p)
            .ToList();
    }

    /// <summary>
    /// The same picture as a JPEG of at most <paramref name="limit"/> bytes. Scaled by area, so a
    /// tall receipt keeps its proportions and its narrow digits stay as wide as they can.
    /// </summary>
    private static InvoicePicture? Shrink(InvoicePicture picture, int limit)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return null; // GDI+; this is a WinForms app
        try
        {
            using var source = new System.Drawing.Bitmap(new MemoryStream(picture.Bytes));
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);

            // A PNG of a photo is often too big only because it is a PNG.
            double scale = 1.0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int width = Math.Max(1, (int)(source.Width * scale));
                int height = Math.Max(1, (int)(source.Height * scale));
                using var resized = new System.Drawing.Bitmap(source, width, height);
                using var output = new MemoryStream();
                resized.Save(output, jpeg, quality);
                if (output.Length <= limit) return new InvoicePicture("image/jpeg", output.ToArray());
                scale *= Math.Max(0.5, Math.Sqrt((double)limit / output.Length) * 0.95);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Ascii(byte[] bytes, int start, int length) =>
        System.Text.Encoding.ASCII.GetString(bytes, start, length);
}
