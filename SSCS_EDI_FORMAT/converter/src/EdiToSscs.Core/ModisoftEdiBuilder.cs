using System.Globalization;
using System.Text;

namespace EdiToSscs.Core;

/// <summary>Stable reason codes for the notes a Modisoft file raises about itself.</summary>
public static class ModisoftReasonCodes
{
    /// <summary>A price ran to fractions of a cent, so the file adds up a few cents off the lines.</summary>
    public const string CentsRounded = "MODISOFT_CENTS_ROUNDED";

    /// <summary>Two different barcodes come out as the same twelve characters in the code field.</summary>
    public const string SameCode = "MODISOFT_SAME_CODE";

    /// <summary>A figure does not fit its fixed-width field.</summary>
    public const string FieldTooBig = "MODISOFT_FIELD_TOO_BIG";
}

/// <summary>
/// One product record of a GHRA-layout EDI file, field for field as it is written. The layout
/// belongs to <see cref="ModisoftEdiBuilder"/>; this is only what goes in it.
/// </summary>
public sealed record GhraLine(
    string Code, string Description, string ItemNumber,
    decimal CaseCost, int Pack, int Cases, decimal Retail)
{
    /// <summary>What Modisoft books for this record: so many cases at so much a case.</summary>
    public decimal Amount => Cases * CaseCost;
}

/// <summary>
/// Writes an <see cref="Invoice"/> as the GHRA-style fixed-width EDI file Modisoft imports.
///
/// Nothing here is inferred. The fields are the ones GHRA itself sent to these stores for years
/// (74 files in <c>EDI/</c>, 7,454 product records), and what Modisoft does with them was read
/// back out of real imports (see analysis/FORMAT_SPEC.md, Part 3):
///
/// 1. **Modisoft takes only the lines from the file.** Somebody creates the purchase by hand first,
///    with its date, vendor and invoice number, then imports the file into it. The 2021 purchase
///    said 7/12/2021 and <c>IN#687105</c> while the file said 07/11/21 and 0000687105. So the
///    header is written correctly but nothing in it reaches the books.
/// 2. **Qty is the file's number of cases, and Case Cost is its case cost**, and Modisoft costs one
///    item as case cost divided by units per case. In 2021 the units came from Modisoft's own item
///    list; in 2026 they came from the file (1L DR PEPPER arrived as 15 a case). Either way a line
///    has to arrive in the vendor's own billing unit — cases where the paper bills cases — or every
///    per-item cost Modisoft works out is wrong.
/// 3. **The money is cases × case cost, to the cent.** 387 lines of the 2021 file landed on its
///    $19,301.14 exactly. Lines with 0 cases are skipped, which costs nothing: no PDF line has 0.
/// 4. **The barcode field is twelve characters, not GHRA's eleven.** Verified 2026-10-05 at Best
///    Stop 16: a Dr Pepper file in GHRA's own 76-byte layout came in with every figure one
///    character to the right of where it was written — 1 case of 2 at $12.75 read as 10 cases of
///    20 at $127.50, the whole invoice at $148,032.50 for $1,468.73 printed. A five-line test file
///    settled it. A blank or filled item number made no difference, nor did a description starting
///    with a letter. Only the line carrying the whole 12-digit UPC-A, with every field after it one
///    place right, came in exactly (4 cases of 15 at $28.12).
/// 5. **Since 2026-10-09 the field is thirteen, read with Modisoft's "Xpress Wholesale" option.**
///    Texas Wholesale matches a barcode on its first eleven digits, so two EAN-13s that share them
///    land on one item: Yumi's LLB Grape `6911308117885` and Strawberry `6911308117861` both
///    booked to Strawberry. Under Xpress Wholesale a thirteen-character field came in exactly and
///    matched exactly. A UPC-A goes in as its twelve digits and a blank: written that way, all 49
///    UPC-A lines of Yumi 29008246 matched the store's items. Written as `0` + twelve digits, every
///    one came in as a new item. The whole Dr Pepper invoice of point 4 came in the same under
///    Xpress as it had under Texas Wholesale: 50 lines, three −1 credits, $1,468.73.
///
/// One file per invoice, CRLF between records and none after the last, which is how every GHRA
/// file ends.
/// </summary>
public static class ModisoftEdiBuilder
{
    /// <summary>
    /// The header's vendor field. It says GHRA on every vendor's file because that is the only
    /// header Modisoft has been seen to take, and the vendor is chosen by hand on the purchase
    /// before the file is imported into it — nothing in the file picks one.
    /// </summary>
    public const string VendorToken = "GHRA";

    /// <summary>The charge record every GHRA file ends with, byte for byte. It carries no amount.</summary>
    public const string ChargeRecord = "CSERSERVICE CHARGE           ";

    public const int HeaderWidth = 33;
    public const int RecordWidth = 78;

    /// <summary>
    /// The barcode field: thirteen characters, as Xpress Wholesale reads it. GHRA's 2021 files had
    /// eleven, and Texas Wholesale read twelve.
    /// </summary>
    public const int CodeWidth = 13;

    /// <summary>The each-list field is five digits of cents.</summary>
    private const decimal MaxRetail = 999.99m;

    /// <summary>Lays the invoice out as file bytes, adding any notes about it to <paramref name="warnings"/>.</summary>
    public static byte[] Build(Invoice invoice, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(warnings);
        return Write(invoice.InvoiceNumber, invoice.Date, Lines(invoice, warnings));
    }

    /// <summary>
    /// The records an invoice becomes. A line the vendor billed by the case goes in as those cases
    /// at the case price; anything else goes in one item at a time. Either way the money is the
    /// line's own, and where whole cents cannot carry it exactly the shortfall is said out loud.
    /// </summary>
    public static IReadOnlyList<GhraLine> Lines(Invoice invoice, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(warnings);

        var lines = new List<GhraLine>(invoice.Lines.Count);
        var rounded = new List<string>();
        foreach (InvoiceLine line in invoice.Lines)
        {
            var (cases, pack, caseCost) = Billing(line);
            if (cases * caseCost != line.ExtendedCost) rounded.Add(Text(line.Description));

            // The vendor's item number stays blank. Barcodes are what the PDF path has proved, and
            // Modisoft matched GHRA's lines by barcode even where its item code disagreed (DSD COKE:
            // file 27226, Modisoft 27405). An item number the AI misread is a way onto the wrong
            // item that nothing here could check.
            lines.Add(new GhraLine(
                CodeField(line.Gtin), Text(line.Description), "", caseCost, pack, cases,
                Math.Clamp(decimal.Round(line.RetailPrice, 2, MidpointRounding.AwayFromZero), 0m, MaxRetail)));
        }

        ReportSharedCodes(invoice, warnings);
        ReportRounding(invoice, lines, rounded, warnings);
        return lines;
    }

    /// <summary>
    /// Header, then one 78-byte record per line, then the charge record. The header total is what
    /// the records add up to, which is how GHRA writes it.
    /// </summary>
    public static byte[] Write(string invoiceNumber, DateOnly? date, IReadOnlyList<GhraLine> lines)
    {
        ArgumentNullException.ThrowIfNull(invoiceNumber);
        ArgumentNullException.ThrowIfNull(lines);

        var sb = new StringBuilder(HeaderWidth + (lines.Count + 1) * (RecordWidth + 2));
        sb.Append(VendorToken.PadRight(7))
          .Append(InvoiceField(invoiceNumber))
          .Append(date is DateOnly d ? d.ToString("MMddyy", CultureInfo.InvariantCulture) : "000000")
          .Append(Signed(Cents(lines.Sum(l => l.Amount)), 10, "invoice total"));

        foreach (GhraLine line in lines)
            sb.Append("\r\n").Append(Record(line));

        sb.Append("\r\n").Append(ChargeRecord);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// How Modisoft should count one line. In cases when the vendor billed it that way and the
    /// cases hold a whole number of items each; otherwise one item at a time. The price is the
    /// line's own amount shared out, so the record adds back up to it whenever whole cents allow.
    /// </summary>
    private static (int Cases, int Pack, decimal CaseCost) Billing(InvoiceLine line)
    {
        int units = Math.Abs(line.Units);
        int sign = line.Units < 0 ? -1 : 1;
        decimal amount = Math.Abs(line.ExtendedCost);

        if (line.Cases is int cases && cases > 0 && units > 0 && units % cases == 0)
            return (sign * cases, units / cases, Cent(amount / cases));

        return (sign * units, 1, units == 0 ? 0m : Cent(amount / units));
    }

    /// <summary>
    /// The barcode as Xpress Wholesale reads it: whole, up to thirteen characters. A UPC-A stays
    /// twelve digits and the record pads it with a blank, never a leading zero (which made every
    /// item new); an EAN-13 goes in whole.
    /// </summary>
    internal static string CodeField(string gtin)
    {
        string code = Text(gtin).Replace(" ", "", StringComparison.Ordinal);
        return code.Length > CodeWidth ? code[..CodeWidth] : code;
    }

    /// <summary>
    /// Two products cut down to the same characters cannot be told apart in Modisoft. With eleven
    /// this was a real trap: Chubiwinos' Tostitos Salsa Verde and Flaming Hot both came out as
    /// 75010111308, and Texas Wholesale merged Yumi's LLB Grape and Strawberry the same way. Thirteen
    /// holds every EAN-13 whole, so this can now only catch a code longer than that.
    /// </summary>
    private static void ReportSharedCodes(Invoice invoice, List<ConversionWarning> warnings)
    {
        var clashes = invoice.Lines
            .GroupBy(line => CodeField(line.Gtin), StringComparer.Ordinal)
            .Select(group => (Code: group.Key, Products: group
                .GroupBy(line => line.Gtin, StringComparer.Ordinal)
                .Select(same => $"{same.Key} ({Text(same.First().Description)})")
                .ToList()))
            .Where(clash => clash.Products.Count > 1)
            .Select(clash => $"{string.Join(" and ", clash.Products)} "
                             + $"{(clash.Products.Count == 2 ? "both" : "all")} go in as {clash.Code}")
            .ToList();
        if (clashes.Count == 0) return;

        warnings.Add(new ConversionWarning(0, ModisoftReasonCodes.SameCode,
            "The Modisoft file only has room for 13 digits of a barcode, so different products end "
            + $"up with the same code: {string.Join("; ", clashes)}. Modisoft cannot tell them "
            + "apart, so check those lines after importing."));
    }

    private static void ReportRounding(
        Invoice invoice, IReadOnlyList<GhraLine> lines, List<string> rounded,
        List<ConversionWarning> warnings)
    {
        if (rounded.Count == 0) return;
        decimal file = lines.Sum(line => line.Amount);
        decimal booked = invoice.Lines.Sum(line => line.ExtendedCost);
        if (Math.Abs(file - booked) < 0.005m) return;

        warnings.Add(new ConversionWarning(0, ModisoftReasonCodes.CentsRounded,
            $"The Modisoft file only holds whole cents, so {rounded.Count} "
            + $"line{(rounded.Count == 1 ? " was" : "s were")} rounded and the file adds up to "
            + $"{file:0.00} instead of {booked:0.00}. Fix the {Math.Abs(file - booked):0.00} in "
            + $"Modisoft after importing: {string.Join(", ", rounded)}."));
    }

    private static string Record(GhraLine line)
    {
        string item = Fit(line.ItemNumber, 6);
        return "B"
            + Fit(line.Code, CodeWidth)
            // 24 characters and a blank, never 25 characters. Texas Wholesale read its item code
            // from the byte after the 24th, so a full-width description put its last letter in
            // Modisoft's Item Code column (Silver Eagle 424753, 2026-10-06: "1", "/", "L", "C" on
            // every long line). Xpress Wholesale named new items cleanly ("LLB-GRAPE SINGLE 3D")
            // with the blank kept, so it stays.
            + Fit(line.Description, 24) + " "
            + item
            + Unsigned(Cents(line.CaseCost), 6, "case cost")
            + "  "
            + Unsigned(line.Pack, 6, "pack size")
            + Signed(line.Cases, 5, "number of cases")
            + Unsigned(Cents(line.Retail), 5, "retail price")
            // GHRA repeats the pack's last three digits and the item number at the end of every
            // record. All 7,454 of its records in EDI/ do it, so this does too.
            + Unsigned(line.Pack % 1000, 3, "pack size")
            + item;
    }

    /// <summary>
    /// Ten digits of the typed invoice number, its last ten when it is longer. Modisoft never reads
    /// it — the purchase carries its own, typed in Modisoft — so this only has to be a valid field,
    /// and GHRA's are always digits.
    /// </summary>
    private static string InvoiceField(string invoiceNumber)
    {
        string digits = Upc.DigitsOnly(invoiceNumber);
        if (digits.Length > 10) digits = digits[^10..];
        return digits.PadLeft(10, '0');
    }

    private static decimal Cent(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static long Cents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static string Unsigned(long value, int width, string what)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (value < 0 || text.Length > width) throw TooBig(what);
        return text.PadLeft(width, '0');
    }

    /// <summary>GHRA puts a minus sign inside the zero padding: −22 cases is <c>00-22</c>.</summary>
    private static string Signed(long value, int width, string what)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > width) throw TooBig(what);
        return text.PadLeft(width, '0');
    }

    private static EdiConversionException TooBig(string what) =>
        new(ModisoftReasonCodes.FieldTooBig,
            $"This invoice has a {what} too big for a Modisoft EDI file to hold. Enter this one in "
            + "Modisoft by hand.");

    private static string Fit(string text, int width) =>
        text.Length > width ? text[..width] : text.PadRight(width);

    /// <summary>
    /// Printable ASCII only, single-spaced and trimmed. The file is fixed-width ASCII, and a stray
    /// byte from a scan would push every field after it out of place.
    /// </summary>
    internal static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value) sb.Append(c is >= ' ' and <= '~' ? c : ' ');
        string s = sb.ToString();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }
}
