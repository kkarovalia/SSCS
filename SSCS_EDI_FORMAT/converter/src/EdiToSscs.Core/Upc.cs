namespace EdiToSscs.Core;

/// <summary>
/// Barcode helpers.
///
/// ────────────────────────────────────────────────────────────────────────────────────────────
/// HOW SSCS STORES A BARCODE  (verified in live SSCS 2026-07-25 — read before touching this)
/// ────────────────────────────────────────────────────────────────────────────────────────────
/// On import, SSCS takes the GTIN it is given and stores it **minus its last character** as the
/// item's ID/PLU. The last character of a barcode is the check digit — a checksum of the others —
/// so this is the standard "body without the check digit".
///
///   sent 600609100271  (UPC-A, 12 digits)  → ID/PLU 60060910027   (11 chars)
///   sent 6917673180912 (EAN-13, 13 digits) → ID/PLU 691767318091  (12 chars)
///
/// SSCS does the dropping. Files we write must always carry the FULL barcode.
///
/// The retired fixed-width EDI output had an 11-character code field and therefore had to drop the
/// check digit itself. That is why a 12-digit UPC-A reaches the same ID/PLU by either route — so a
/// vendor can be moved from EDI to XML without creating duplicate item records — while a 13-digit
/// EAN-13 could never fit the old field at all.
///
/// Rule of thumb: never strip a leading zero, and never pre-drop a check digit.
/// </summary>
public static class Upc
{
    private static bool AllAsciiDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>Keeps only the ASCII digits of <paramref name="s"/> (drops spaces, slashes, OCR punctuation).</summary>
    public static string DigitsOnly(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// Validates a 12-digit UPC-A by its trailing check digit. Returns false for any other length,
    /// non-digits, or a check digit that does not match the first 11. This is the free, no-data
    /// safeguard that catches most single-digit OCR errors in scanned barcodes.
    /// </summary>
    public static bool IsValidUpcA12(string? upc12) =>
        upc12 is not null && upc12.Length == 12 && AllAsciiDigits(upc12) &&
        UpcE.ComputeUpcACheckDigit(upc12[..11]) == upc12[11];

    /// <summary>
    /// Validates a whole barcode of either length SSCS sees — a 12-digit UPC-A or a 13-digit EAN-13
    /// — by its trailing check digit. The two differ only in which end the x3 weighting starts from,
    /// which weighting from the right handles for both.
    ///
    /// This is the only free error detector a scanned invoice offers: the check digit is arithmetic
    /// on the other digits, so a single mis-read digit almost always breaks it. Anything that is not
    /// a barcode at all — a store PLU, a vendor SKU — returns false, so callers must decide what a
    /// non-barcode code means for them rather than reading false as "damaged".
    /// </summary>
    public static bool IsValidGtin(string? gtin)
    {
        if (gtin is null || gtin.Length is not (12 or 13) || !AllAsciiDigits(gtin)) return false;

        int sum = 0;
        for (int i = gtin.Length - 2; i >= 0; i--)
            sum += (gtin[i] - '0') * ((gtin.Length - 2 - i) % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == gtin[^1] - '0';
    }

    /// <summary>
    /// Turns an 11-digit check-digit-less body into the full 12-digit UPC-A by computing and
    /// appending its check digit.
    ///
    /// Needed because the fixed-width EDI files we still read as INPUT store the body only. Passing
    /// those 11 digits straight into the XML would be wrong: SSCS drops the last character of
    /// whatever it is given, so 11 digits in would leave a 10-character ID pointing at nothing.
    /// Recomputing the check digit is exact — it is derived from the other 11 — so the round trip
    /// EDI body → GTIN → SSCS's drop returns the original body.
    /// </summary>
    public static string AppendCheckDigit(string body11)
    {
        ArgumentNullException.ThrowIfNull(body11);
        if (body11.Length != 11 || !AllAsciiDigits(body11))
            throw new ArgumentException("A UPC-A body must be exactly 11 digits.", nameof(body11));
        return body11 + UpcE.ComputeUpcACheckDigit(body11);
    }

    /// <summary>
    /// The ID/PLU SSCS will store for a given GTIN: everything but the last character. Used by the
    /// regression tests to prove that a barcode routed through the XML path lands on exactly the
    /// item the retired EDI output would have written.
    /// </summary>
    public static string SscsIdFromGtin(string gtin)
    {
        ArgumentNullException.ThrowIfNull(gtin);
        return gtin.Length <= 1 ? gtin : gtin[..^1];
    }
}
