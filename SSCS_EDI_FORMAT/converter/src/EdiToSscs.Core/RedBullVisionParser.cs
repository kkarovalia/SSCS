using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>A Red Bull ticket read from the pictures, awaiting the operator-keyed header fields.</summary>
public sealed class RedBullVisionTicket
{
    public required decimal Total { get; init; }
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = RedBullPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Turns one AI reading into Red Bull invoice lines. Gemini owns the printed values; Core parses
/// them, applies the vendor's sign and cost rules, and reports disagreements without blocking the
/// file or asking about a product row.
/// </summary>
public static class RedBullVisionParser
{
    public static RedBullVisionTicket Parse(
        RedBullVisionReading reading, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(warnings);

        var lines = new List<InvoiceLine>();
        var skipped = new List<string>();
        var badCodes = new List<string>();
        var crooked = new List<string>();
        var missingDirections = new List<string>();
        int creditLines = 0;

        foreach (RedBullVisionRow raw in reading.Rows)
        {
            string description = Describe(raw);
            bool inferredCredit = IsNegative(raw.Units) || IsNegative(raw.Amount);
            bool credit = raw.IsCredit ?? inferredCredit;
            if (raw.IsCredit is null)
                missingDirections.Add($"{description} ({(credit ? "return" : "delivery")})");

            string? code = PrintedUpc(raw.Barcode);
            int units = Whole(raw.Units);
            decimal amount = Money(raw.Amount);
            if (code is null || units <= 0 || amount <= 0m)
            {
                skipped.Add($"{description} ({Printed(raw.Amount)})");
                continue;
            }

            if (!Upc.IsValidUpcA12(code)) badCodes.Add($"{description} ({code})");

            decimal cases = Number(raw.Cases);
            decimal price = Money(raw.Price);
            decimal deposit = Money(raw.Deposit);
            decimal discount = Money(raw.Discount);
            decimal sugar = Money(raw.Sugar);
            bool hasOwnMath = !credit && cases > 0m && price > 0m
                              && IsMoney(raw.Deposit) && IsMoney(raw.Discount)
                              && IsMoney(raw.Sugar);
            if (hasOwnMath)
            {
                decimal extended = decimal.Round(
                    cases * price - deposit - discount - sugar,
                    2, MidpointRounding.AwayFromZero);
                if (Math.Abs(extended - amount) >= 0.005m)
                    crooked.Add($"{description} ({cases:0.####} x {price:0.00} less "
                                + $"{deposit + discount + sugar:0.00} is {extended:0.00}, not "
                                + $"{amount:0.00})");
            }

            decimal unitCost = decimal.Round(
                amount / units, 4, MidpointRounding.AwayFromZero);
            if (credit) creditLines++;
            lines.Add(new InvoiceLine
            {
                Gtin = code,
                Description = description,
                Units = credit ? -units : units,
                UnitCost = unitCost,
                RetailPrice = RetailSeed.FromCost(unitCost, RedBullPdfParser.RetailMargin),
            });
        }

        if (lines.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "The AI read this Red Bull ticket but found no product line with a twelve-digit "
                + "barcode, a nonzero whole unit count and a positive TOTAL on it. Re-scan the "
                + "complete ticket and try again.");

        decimal booked = lines.Sum(line => line.ExtendedCost);
        var totals = ComparableTotals(reading);
        Report(warnings, reading, lines, skipped, badCodes, crooked, missingDirections,
            creditLines, booked, totals);

        return new RedBullVisionTicket { Total = booked, Lines = lines };
    }

    private static void Report(
        List<ConversionWarning> warnings, RedBullVisionReading reading,
        IReadOnlyList<InvoiceLine> lines, List<string> skipped, List<string> badCodes,
        List<string> crooked, List<string> missingDirections, int creditLines, decimal booked,
        IReadOnlyList<decimal> totals)
    {
        if (skipped.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(skipped.Count, "line")} could not be sent because the scan lost the "
                + $"barcode, UNITS or TOTAL: {string.Join("; ", skipped)}. Add them in SSCS by "
                + "hand, or re-scan the ticket."));

        if (badCodes.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"{Count(badCodes.Count, "barcode")} did not check out and went in as read: "
                + $"{string.Join(", ", badCodes)}. Check the twelve printed digits against the "
                + "paper and fix the item in SSCS if needed."));

        if (crooked.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"{Count(crooked.Count, "line")} does not multiply out against its own printed "
                + $"figures: {string.Join("; ", crooked)}. The printed TOTAL was sent. Worth a "
                + "glance at the paper."));

        if (missingDirections.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.SectionUnlabelled,
                $"{Count(missingDirections.Count, "line")} did not identify whether it was a "
                + $"delivery or return. The printed sign was used where present and an unsigned "
                + $"row was treated as a delivery: {string.Join("; ", missingDirections)}. Check "
                + "these lines against the paper."));

        if (totals.Count > 0 && totals.All(total => Math.Abs(total - booked) >= 0.005m))
        {
            decimal closest = totals.OrderBy(total => Math.Abs(total - booked)).First();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The {Count(lines.Count, "line")} sent come to {booked:0.00} against the closest "
                + $"printed ticket total of {closest:0.00}. The printed row values were sent as "
                + "read; check this ticket against the paper."));
        }

        ReportUnits(warnings, reading, lines, booked, totals);

        if (creditLines > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {Count(creditLines, "routine credit line")}. They were sent as "
                + "negative quantities at positive costs on the same payable.", Routine: true));
    }

    /// <summary>
    /// Ports the established two-message Red Bull unit warning. Units Delivered is a delivery-page
    /// count, so it is compared only when the ticket has no returns, exactly like the legacy path.
    /// </summary>
    private static void ReportUnits(
        List<ConversionWarning> warnings, RedBullVisionReading reading,
        IReadOnlyList<InvoiceLine> lines, decimal booked, IReadOnlyList<decimal> totals)
    {
        if (lines.Any(line => line.Units < 0)) return;
        int? printed = WholeOrNull(reading.PrintedUnits);
        if (printed is null)
        {
            var counts = reading.PageSummaries
                .Select(summary => WholeOrNull(summary.UnitsDelivered))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .ToList();
            if (counts.Count > 0) printed = counts.Sum();
        }
        if (printed is null) return;

        int units = lines.Sum(line => line.Units);
        if (units == printed.Value) return;
        bool nothingIsMissing = totals.Any(total => Math.Abs(total - booked) < 0.005m);
        warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
            nothingIsMissing
                ? $"The ticket says {printed} units were delivered and the {lines.Count} lines "
                  + $"come to {units}. The money adds up exactly, so no line is missing - either "
                  + "the ticket's own count or one line's UNITS was misread. Worth a glance at the paper."
                : $"The ticket says {printed} units were delivered and the {lines.Count} lines "
                  + $"come to {units}. A product line may be missing from the scan - count the rows "
                  + "on the paper before sending this one."));
    }

    /// <summary>
    /// Comparable whole-ticket totals only. An ROA is already ticket-scoped. Per-page INVOICE
    /// figures become comparable only after their signed values are added across every page.
    /// </summary>
    private static IReadOnlyList<decimal> ComparableTotals(RedBullVisionReading reading)
    {
        var totals = reading.PrintedTotals
            .Select(SignedMoneyOrNull)
            .Where(value => value is not null)
            .Select(value => Math.Abs(value!.Value))
            .ToList();

        totals.AddRange(reading.PageSummaries
            .Select(summary => SignedMoneyOrNull(summary.Roa))
            .Where(value => value is not null)
            .Select(value => Math.Abs(value!.Value)));

        var invoices = reading.PageSummaries
            .Select(summary => SignedMoneyOrNull(summary.Invoice))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToList();
        if (invoices.Count > 0) totals.Add(Math.Abs(invoices.Sum()));
        return totals.Distinct().ToList();
    }

    private static string Describe(RedBullVisionRow row) =>
        string.IsNullOrWhiteSpace(row.Description)
            ? (string.IsNullOrWhiteSpace(row.ItemNumber) ? "RED BULL" : row.ItemNumber.Trim())
            : row.Description.Trim();

    private static string? PrintedUpc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        if (text.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(text);
        return digits.Length == 12 ? digits : null;
    }

    private static int Whole(string? raw) => WholeOrNull(raw) is int value ? Math.Abs(value) : 0;

    private static int? WholeOrNull(string? raw)
    {
        string text = Bare(raw);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Abs(value) : null;
    }

    private static decimal Number(string? raw) =>
        SignedMoneyOrNull(raw) is decimal value ? Math.Abs(value) : 0m;

    private static decimal Money(string? raw) => Number(raw);

    private static bool IsMoney(string? raw) => SignedMoneyOrNull(raw) is not null;

    private static decimal? SignedMoneyOrNull(string? raw)
    {
        string text = Bare(raw);
        return decimal.TryParse(text,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal value) ? value : null;
    }

    private static string Bare(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);
        if (text.StartsWith('(') && text.EndsWith(')')) text = "-" + text[1..^1];
        else if (text.EndsWith('-')) text = "-" + text[..^1];
        return text.Trim();
    }

    private static bool IsNegative(string? raw) => SignedMoneyOrNull(raw) is < 0m;

    private static string Printed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "no amount" : raw.Trim();

    private static string Count(int many, string noun) =>
        $"{many} {noun}{(many == 1 ? "" : "s")}";
}
