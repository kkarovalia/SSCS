using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>A Blue Bell ticket read from the pictures, awaiting its operator-keyed header.</summary>
public sealed class BlueBellVisionTicket
{
    public required decimal Total { get; init; }
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = BlueBellPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Turns Gemini's one Blue Bell reading into invoice lines. The model owns the printed values;
/// Core restores Blue Bell's real UPC-A, expands dozen cartons, handles loose-bar returns and
/// seeds retail. Arithmetic is diagnostic only.
/// </summary>
public static class BlueBellVisionParser
{
    private sealed record Posted(
        BlueBellVisionRow Raw, InvoiceLine Line, decimal InvoiceQuantity, int SellableUnits,
        bool Credit);

    public static BlueBellVisionTicket Parse(
        BlueBellVisionReading reading, List<ConversionWarning> warnings)
    {
        var posted = new List<Posted>();
        var skipped = new List<string>();
        var crooked = new List<string>();
        var derivedPrices = new List<string>();
        var packDisagreements = new List<string>();
        int creditLines = 0;

        foreach (BlueBellVisionRow raw in reading.Rows)
        {
            string description = Describe(raw);
            string? printedUpc = PrintedUpc(raw.Upc);
            int pack = Pack(raw.PackSize, raw.Description);
            bool hasQuantity = TryQuantity(raw.Quantity, pack, out decimal invoiceQuantity,
                out int sellableUnits);
            decimal? price = SignedMoney(raw.Price);
            decimal? extended = SignedMoney(raw.Extended);

            if (printedUpc is null || !hasQuantity
                || Math.Abs(price ?? 0m) <= 0m && Math.Abs(extended ?? 0m) <= 0m)
            {
                skipped.Add($"{description} ({PrintedMoney(raw)})");
                continue;
            }

            bool credit = raw.IsCredit
                          ?? (IsNegative(raw.Quantity) || IsNegative(raw.Extended));
            decimal magnitudePrice = Math.Abs(price ?? 0m);
            decimal magnitudeExtended = Math.Abs(extended ?? 0m);
            decimal unitCost;
            if (magnitudePrice > 0m)
            {
                unitCost = decimal.Round(
                    magnitudePrice / pack, 4, MidpointRounding.AwayFromZero);
            }
            else
            {
                unitCost = decimal.Round(
                    magnitudeExtended / sellableUnits, 4, MidpointRounding.AwayFromZero);
                derivedPrices.Add($"{description} ({magnitudeExtended:0.00} / {sellableUnits} = "
                                  + $"{unitCost:0.0000})");
            }

            if (magnitudePrice > 0m && magnitudeExtended > 0m)
            {
                decimal rowMath = decimal.Round(
                    invoiceQuantity * magnitudePrice, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(rowMath - magnitudeExtended) >= 0.005m)
                    crooked.Add($"{description} ({invoiceQuantity:0.####} x "
                                + $"{magnitudePrice:0.00} is {rowMath:0.00}, not "
                                + $"{magnitudeExtended:0.00})");
            }

            if (BlueBellPdfParser.VisionCataloguePack(raw.ProductNumber) is int knownPack
                && knownPack != pack)
                packDisagreements.Add($"{description} (read as pack {pack}, known list says "
                                      + $"{knownPack})");

            var line = new InvoiceLine
            {
                Gtin = BlueBellPdfParser.ToGtin(printedUpc),
                Description = description,
                Units = credit ? -sellableUnits : sellableUnits,
                UnitCost = unitCost,
                RetailPrice = BlueBellPdfParser.SeedRetail(unitCost),
            };
            posted.Add(new Posted(raw, line, invoiceQuantity, sellableUnits, credit));
            if (credit) creditLines++;
        }

        if (posted.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "The AI read this Blue Bell ticket but found no product line with the twelve "
                + "printed UPC characters, a quantity, and a PRICE or EXTENDED amount. Re-scan "
                + "the complete ticket and try again.");

        decimal booked = posted.Sum(row => row.Line.ExtendedCost);
        decimal total = PrintedTicketTotal(reading, posted, booked, warnings);
        Report(warnings, reading, posted, skipped, crooked, derivedPrices, packDisagreements,
            creditLines);

        return new BlueBellVisionTicket
        {
            Total = total,
            Lines = posted.Select(row => row.Line).ToList(),
        };
    }

    private static decimal PrintedTicketTotal(
        BlueBellVisionReading reading, IReadOnlyList<Posted> posted, decimal booked,
        List<ConversionWarning> warnings)
    {
        var scopes = posted.Select(row => (Page(row.Raw.Page), row.Credit)).Distinct().ToList();
        var totals = new List<decimal>();
        bool complete = true;
        foreach (var scope in scopes)
        {
            BlueBellVisionPageSummary? summary = reading.PageSummaries.FirstOrDefault(value =>
                Page(value.Page) == scope.Item1 && SummaryCredit(value) == scope.Credit
                && SignedMoney(value.TotalAmount) is not null);
            decimal? printed = summary is null ? null : SignedMoney(summary.TotalAmount);
            if (printed is null)
            {
                complete = false;
                continue;
            }
            totals.Add(scope.Credit ? -Math.Abs(printed.Value) : Math.Abs(printed.Value));
        }

        if (complete && totals.Count == scopes.Count) return totals.Sum();

        warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoTotal,
            $"A printed TOTAL could not be read for every Blue Bell section. The "
            + $"{Count(posted.Count, "line")} sent come to {booked:0.00}, and that sum was used "
            + "for the invoice total."));
        return booked;
    }

    private static void Report(
        List<ConversionWarning> warnings, BlueBellVisionReading reading,
        IReadOnlyList<Posted> posted, List<string> skipped, List<string> crooked,
        List<string> derivedPrices, List<string> packDisagreements, int creditLines)
    {
        if (skipped.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(skipped.Count, "line")} could not be sent because the scan lost the UPC, "
                + $"QTY or both money figures: {string.Join("; ", skipped)}. Add it in SSCS by "
                + "hand, or re-scan the ticket."));

        if (derivedPrices.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.PriceDerived,
                $"{Count(derivedPrices.Count, "line")} had no readable PRICE. Its QTY and "
                + $"EXTENDED amount set unit cost: {string.Join("; ", derivedPrices)}. Check the "
                + "cost in SSCS."));

        if (crooked.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"{Count(crooked.Count, "line")} does not multiply out against its own printed "
                + $"figures: {string.Join("; ", crooked)}. The printed QTY and PRICE were sent."));

        if (packDisagreements.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.PackSizeMismatch,
                $"{Count(packDisagreements.Count, "line")} disagrees with Blue Bell's known pack "
                + $"size: {string.Join("; ", packDisagreements)}. The pack read from the printed "
                + "description was sent."));

        ReportTotals(warnings, reading, posted);
        ReportQuantities(warnings, reading, posted);

        if (creditLines > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {Count(creditLines, "routine return line")}. "
                + $"{(creditLines == 1 ? "It was" : "They were")} sent as negative quantities at "
                + "positive costs on the same payable.", Routine: true));
    }

    private static void ReportTotals(
        List<ConversionWarning> warnings, BlueBellVisionReading reading,
        IReadOnlyList<Posted> posted)
    {
        var mismatches = new List<string>();
        foreach (BlueBellVisionPageSummary summary in reading.PageSummaries)
        {
            decimal? printed = SignedMoney(summary.TotalAmount);
            if (printed is null) continue;
            bool credit = SummaryCredit(summary);
            var rows = Scope(posted, summary.Page, credit).ToList();
            if (rows.Count == 0) continue;
            decimal actual = rows.Sum(row => row.Line.ExtendedCost);
            decimal expected = credit ? -Math.Abs(printed.Value) : Math.Abs(printed.Value);
            decimal tolerance = 0.01m * Math.Max(1, rows.Count);
            if (Math.Abs(actual - expected) > tolerance)
                mismatches.Add($"{ScopeName(summary)} lines are {actual:0.00} and TOTAL is "
                               + $"{expected:0.00}");
        }

        if (mismatches.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The printed values do not reach {Count(mismatches.Count, "section TOTAL")}: "
                + $"{string.Join("; ", mismatches)}. The printed row values still shipped."));
    }

    private static void ReportQuantities(
        List<ConversionWarning> warnings, BlueBellVisionReading reading,
        IReadOnlyList<Posted> posted)
    {
        var mismatches = new List<string>();
        foreach (BlueBellVisionPageSummary summary in reading.PageSummaries)
        {
            bool credit = SummaryCredit(summary);
            var rows = Scope(posted, summary.Page, credit).ToList();
            if (rows.Count == 0) continue;

            if (SignedMoney(summary.TotalQuantity) is decimal printedQuantity)
            {
                decimal actual = rows.Sum(row => row.InvoiceQuantity);
                if (Math.Abs(Math.Abs(printedQuantity) - actual) > 0.01m)
                    mismatches.Add($"{ScopeName(summary)} QTY is {actual:0.####} and TOTAL prints "
                                   + $"{Math.Abs(printedQuantity):0.####}");
            }

            string? returned = summary.BarsReturned ?? summary.UnitsReturned;
            if (SignedMoney(returned) is decimal printedReturned)
            {
                int actual = rows.Sum(row => row.SellableUnits);
                if (Math.Abs(Math.Abs(printedReturned) - actual) > 0.01m)
                    mismatches.Add($"{ScopeName(summary)} has {actual} sellable items and the "
                                   + $"returned count prints {Math.Abs(printedReturned):0.####}");
            }
        }

        if (mismatches.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"{Count(mismatches.Count, "printed quantity check")} disagrees with the rows: "
                + $"{string.Join("; ", mismatches)}. The printed row quantities were sent."));
    }

    private static IEnumerable<Posted> Scope(
        IReadOnlyList<Posted> posted, string page, bool credit) =>
        posted.Where(row => Page(row.Raw.Page) == Page(page) && row.Credit == credit);

    private static bool SummaryCredit(BlueBellVisionPageSummary summary) =>
        summary.IsCredit
        ?? (summary.BarsReturned is not null
            || summary.UnitsReturned is not null
            || IsNegative(summary.TotalAmount));

    private static string ScopeName(BlueBellVisionPageSummary summary) =>
        $"page {(Page(summary.Page).Length == 0 ? "?" : Page(summary.Page))} "
        + (SummaryCredit(summary) ? "Returns" : "Sales");

    private static string Page(string? page) => page?.Trim() ?? "";

    private static string Describe(BlueBellVisionRow row)
    {
        string description = string.IsNullOrWhiteSpace(row.Description)
            ? (string.IsNullOrWhiteSpace(row.ProductNumber) ? "BLUE BELL" : row.ProductNumber.Trim())
            : row.Description.Trim();
        return description.StartsWith("BLUE BELL", StringComparison.OrdinalIgnoreCase)
            ? description : "BLUE BELL " + description;
    }

    private static string? PrintedUpc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        return text.Length == 12 && text[0] == '0' && text.All(char.IsAsciiDigit)
            ? text : null;
    }

    private static int Pack(string? raw, string? description)
    {
        string text = Bare(raw);
        if (text == "12") return 12;
        if (text == "1") return 1;
        return description?.TrimStart().StartsWith("DZ", StringComparison.OrdinalIgnoreCase) == true
            ? 12 : 1;
    }

    private static bool TryQuantity(
        string? raw, int pack, out decimal invoiceQuantity, out int sellableUnits)
    {
        invoiceQuantity = 0m;
        sellableUnits = 0;
        string text = Bare(raw).TrimStart('+', '-');
        if (text.Contains('/'))
        {
            string[] parts = text.Split('/');
            if (pack != 12 || parts.Length != 2
                || !decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal numerator)
                || !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal denominator)
                || denominator != 12m || numerator <= 0m || numerator != decimal.Truncate(numerator)
                || numerator > int.MaxValue) return false;
            invoiceQuantity = numerator / 12m;
            sellableUnits = (int)numerator;
            return true;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out decimal quantity) || quantity <= 0m) return false;
        decimal units = quantity * pack;
        if (units != decimal.Truncate(units) || units > int.MaxValue) return false;
        invoiceQuantity = quantity;
        sellableUnits = (int)units;
        return sellableUnits > 0;
    }

    private static decimal? SignedMoney(string? raw)
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
        else if (text.EndsWith('-') && !text.StartsWith('-')) text = "-" + text[..^1];
        return text.Trim();
    }

    private static bool IsNegative(string? raw) => Bare(raw).StartsWith("-", StringComparison.Ordinal);

    private static string PrintedMoney(BlueBellVisionRow row) =>
        !string.IsNullOrWhiteSpace(row.Extended) ? row.Extended.Trim()
        : !string.IsNullOrWhiteSpace(row.Price) ? row.Price.Trim()
        : "no money";

    private static string Count(int many, string noun) =>
        $"{many} {noun}{(many == 1 ? "" : "s")}";
}

/// <summary>
/// The legacy parser file remains unchanged. This partial adapter exposes only the catalogue pack
/// needed by the AI path's warning-only cross-check; it cannot repair or replace the model read.
/// </summary>
public static partial class BlueBellPdfParser
{
    internal static int? VisionCataloguePack(string? productNumber) =>
        productNumber is not null && Catalogue.TryGetValue(productNumber.Trim(), out var known)
            ? known.Pack : null;
}
