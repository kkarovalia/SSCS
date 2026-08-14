using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>A Chubiwinos ticket read from the pictures, awaiting its operator-keyed header.</summary>
public sealed class ChubiwinosVisionTicket
{
    public required decimal Total { get; init; }
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = ChubiwinosPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Turns one Chubiwinos picture reading directly into invoice lines. The model owns every printed
/// value on this path. Core removes barcode hyphens, restores the mathematically determined check
/// digit when the paper prints an eleven-digit UPC-A body, applies the DUMP sign and retail margin,
/// and reports every disagreement without refusing a postable invoice.
/// </summary>
public static class ChubiwinosVisionParser
{
    private sealed record Posted(
        ChubiwinosVisionRow Raw, InvoiceLine Line, int Quantity, bool Credit,
        decimal PrintedAmount);

    public static ChubiwinosVisionTicket Parse(
        ChubiwinosVisionReading reading, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(warnings);

        var posted = new List<Posted>();
        var skipped = new List<string>();
        var badCodes = new List<string>();
        var crooked = new List<string>();
        var derivedPrices = new List<string>();
        int creditLines = 0;

        foreach (ChubiwinosVisionRow raw in reading.Rows)
        {
            string description = Describe(raw);
            string? code = FullGtin(raw.Barcode);
            int quantity = Whole(raw.Quantity);
            decimal price = Money(raw.Price);
            decimal amount = Money(raw.Amount);

            // A PRICE or a rightmost TOTAL is enough money to post. If PRICE is gone, TOTAL/QTY
            // still states unit cost; if TOTAL is gone, the printed PRICE is already unit cost.
            if (code is null || quantity <= 0 || price <= 0m && amount <= 0m)
            {
                skipped.Add($"{description} ({PrintedMoney(raw)})");
                continue;
            }

            bool inferredCredit = IsNegative(raw.Quantity)
                                  || IsNegative(raw.Price)
                                  || IsNegative(raw.Amount);
            bool credit = raw.IsCredit ?? inferredCredit;

            if (price <= 0m)
            {
                price = decimal.Round(amount / quantity, 4, MidpointRounding.AwayFromZero);
                derivedPrices.Add($"{description} ({amount:0.00} / {quantity} = {price:0.0000})");
            }

            if (amount > 0m)
            {
                decimal extended = decimal.Round(
                    quantity * price, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(extended - amount) >= 0.005m)
                    crooked.Add($"{description} ({quantity} x {price:0.0000} is "
                                + $"{extended:0.00}, not {amount:0.00})");
            }

            if (!Upc.IsValidGtin(code)) badCodes.Add($"{description} ({code})");

            var line = new InvoiceLine
            {
                // Most Chubiwinos rows print a complete UPC-A or EAN-13. Item 905 proves that an
                // eleven-digit UPC-A body is also physical vendor output. Its twelfth digit is a
                // checksum, not a guessed product value, and must be restored before SSCS removes
                // that digit on import. Hyphens are typography only; no catalogue is consulted.
                Gtin = code,
                Description = description,
                Units = credit ? -quantity : quantity,
                UnitCost = price,
                RetailPrice = RetailSeed.FromCost(price, ChubiwinosPdfParser.RetailMargin),
            };
            posted.Add(new Posted(raw, line, quantity, credit,
                amount > 0m ? amount : Math.Abs(line.ExtendedCost)));
            if (credit) creditLines++;
        }

        if (posted.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "The AI read this Chubiwinos invoice but found no product line with an eleven-digit "
                + "UPC-A body or full twelve- or thirteen-digit barcode, a QTY and a PRICE or TOTAL. "
                + "Re-scan the complete "
                + "invoice and try again.");

        decimal booked = posted.Sum(row => row.Line.ExtendedCost);
        Report(warnings, reading, posted, skipped, badCodes, crooked, derivedPrices,
            creditLines, booked);

        return new ChubiwinosVisionTicket
        {
            // Printed totals are independent checks, never sources. NAXML is paid for exactly what
            // its signed lines compute, even when a footer is absent or disagrees.
            Total = booked,
            Lines = posted.Select(row => row.Line).ToList(),
        };
    }

    private static void Report(
        List<ConversionWarning> warnings, ChubiwinosVisionReading reading,
        IReadOnlyList<Posted> posted, List<string> skipped, List<string> badCodes,
        List<string> crooked, List<string> derivedPrices, int creditLines, decimal booked)
    {
        if (skipped.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(skipped.Count, "line")} could not be sent because the scan lost the "
                + $"barcode, QTY or both money figures: {string.Join("; ", skipped)}. Add it in "
                + "SSCS by hand, or re-scan the invoice."));

        if (badCodes.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"{Count(badCodes.Count, "barcode")} did not check out and went in exactly as "
                + $"printed: {string.Join(", ", badCodes)}. Check the printed digits against the "
                + "paper and scan the product itself if the vendor's number is wrong."));

        if (derivedPrices.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.PriceDerived,
                $"{Count(derivedPrices.Count, "line")} had no readable PRICE. Its QTY and printed "
                + $"TOTAL set unit cost: {string.Join("; ", derivedPrices)}. Check the cost in SSCS."));

        if (crooked.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"{Count(crooked.Count, "line")} does not multiply out against its own printed "
                + $"figures: {string.Join("; ", crooked)}. The printed QTY and PRICE were sent."));

        ReportSectionQuantities(warnings, reading, posted);
        ReportTotals(warnings, reading, posted, booked);

        if (!reading.VendorMarkers.Any(marker => !string.IsNullOrWhiteSpace(marker)))
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.ChubiwinosMarkers,
                "No Chubiwinos name, route, section, Totals or INVOICE BALANCE marker was visible. "
                + "The selected vendor was still used; check that this is a Chubiwinos invoice."));

        if (creditLines > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This invoice has {Count(creditLines, "routine DUMP credit line")}. "
                + $"{(creditLines == 1 ? "It was" : "They were")} sent as negative quantities at "
                + "positive costs on the same payable.", Routine: true));
    }

    private static void ReportSectionQuantities(
        List<ConversionWarning> warnings, ChubiwinosVisionReading reading,
        IReadOnlyList<Posted> posted)
    {
        var mismatches = new List<string>();
        foreach (ChubiwinosVisionSectionTotal total in reading.SectionTotals)
        {
            int? printed = WholeOrNull(total.Quantity);
            if (printed is null) continue;
            bool dump = IsDump(total.Section);
            int actual = posted.Where(row => row.Credit == dump).Sum(row => row.Quantity);
            if (actual != printed.Value)
                mismatches.Add($"{SectionName(dump)} rows contain {actual} units and Totals prints "
                               + $"{printed.Value}");
        }

        if (mismatches.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"{Count(mismatches.Count, "printed unit check")} disagrees with the rows: "
                + $"{string.Join("; ", mismatches)}. Money can still balance when one QTY is wrong, "
                + "so count these quantities against the paper."));
    }

    private static void ReportTotals(
        List<ConversionWarning> warnings, ChubiwinosVisionReading reading,
        IReadOnlyList<Posted> posted, decimal booked)
    {
        var mismatches = new List<string>();
        foreach (ChubiwinosVisionSectionTotal total in reading.SectionTotals)
        {
            decimal? printed = SignedMoneyOrNull(total.Amount);
            if (printed is null) continue;
            bool dump = IsDump(total.Section);
            decimal actual = posted.Where(row => row.Credit == dump)
                .Sum(row => row.PrintedAmount);
            if (!Near(actual, Math.Abs(printed.Value), posted.Count(row => row.Credit == dump)))
                mismatches.Add($"{SectionName(dump)} row TOTAL values are {actual:0.00} and its "
                               + $"Totals row prints {Math.Abs(printed.Value):0.00}");
        }

        decimal sales = posted.Where(row => !row.Credit).Sum(row => row.Line.ExtendedCost);
        decimal credits = Math.Abs(posted.Where(row => row.Credit).Sum(row => row.Line.ExtendedCost));
        Compare("SALES", reading.Sales, sales, posted.Count(row => !row.Credit), mismatches);
        Compare("CREDITS", reading.Credits, credits, posted.Count(row => row.Credit), mismatches);

        var grandTotals = new (string Label, string? Value)[]
        {
            ("NET AMOUNT", reading.NetAmount),
            ("TOTAL DUE", reading.TotalDue),
            ("INVOICE BALANCE", reading.InvoiceBalance),
        };
        int readableGrandTotals = 0;
        foreach (var (label, value) in grandTotals)
        {
            if (SignedMoneyOrNull(value) is not decimal printed) continue;
            readableGrandTotals++;
            if (!Near(booked, printed, posted.Count))
                mismatches.Add($"the {posted.Count} sent lines are {booked:0.00} and {label} prints "
                               + $"{printed:0.00}");
        }
        if (readableGrandTotals == 0)
            mismatches.Add("NET AMOUNT, TOTAL DUE and INVOICE BALANCE were all unreadable");

        if (SignedMoneyOrNull(reading.SalesTax) is decimal tax && Math.Abs(tax) >= 0.005m)
            mismatches.Add($"SALES TAX prints {tax:0.00}; this vendor's product lines carry no tax "
                           + "line to post");

        // These figures have their own printed relationships. They remain checks only and can
        // never replace a row or decide the invoice total.
        decimal? printedSales = SignedMoneyOrNull(reading.Sales);
        decimal? printedCredits = SignedMoneyOrNull(reading.Credits);
        decimal? net = SignedMoneyOrNull(reading.NetAmount);
        decimal? due = SignedMoneyOrNull(reading.TotalDue);
        decimal? payment = SignedMoneyOrNull(reading.TotalPayment);
        decimal? balance = SignedMoneyOrNull(reading.InvoiceBalance);
        decimal taxValue = SignedMoneyOrNull(reading.SalesTax) ?? 0m;
        if (printedSales is decimal s && printedCredits is decimal c && net is decimal n
            && !Near(Math.Abs(s) - Math.Abs(c), n, 1))
            mismatches.Add($"SALES less CREDITS is {Math.Abs(s) - Math.Abs(c):0.00} but NET AMOUNT "
                           + $"prints {n:0.00}");
        if (net is decimal netValue && due is decimal dueValue
            && !Near(netValue + taxValue, dueValue, 1))
            mismatches.Add($"NET AMOUNT plus SALES TAX is {netValue + taxValue:0.00} but TOTAL DUE "
                           + $"prints {dueValue:0.00}");
        if (due is decimal duePrinted && payment is decimal paid && balance is decimal balancePrinted
            && !Near(duePrinted - Math.Abs(paid), balancePrinted, 1))
            mismatches.Add($"TOTAL DUE less TOTAL PAYMENT is {duePrinted - Math.Abs(paid):0.00} but "
                           + $"INVOICE BALANCE prints {balancePrinted:0.00}");

        if (mismatches.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The printed money checks disagree: {string.Join("; ", mismatches)}. The invoice "
                + $"was still written for the signed line sum of {booked:0.00}."));
    }

    private static void Compare(
        string label, string? raw, decimal actual, int rows, List<string> mismatches)
    {
        if (SignedMoneyOrNull(raw) is not decimal printed) return;
        if (!Near(actual, Math.Abs(printed), rows))
            mismatches.Add($"posted {label} lines are {actual:0.00} and {label} prints "
                           + $"{Math.Abs(printed):0.00}");
    }

    private static bool Near(decimal actual, decimal expected, int rows) =>
        Math.Abs(actual - expected) < 0.005m;

    private static string Describe(ChubiwinosVisionRow row) =>
        string.IsNullOrWhiteSpace(row.Description)
            ? (string.IsNullOrWhiteSpace(row.ItemNumber) ? "CHUBIWINOS" : row.ItemNumber.Trim())
            : row.Description.Trim();

    private static string? FullGtin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        if (text.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(text);
        return digits.Length switch
        {
            11 => Upc.AppendCheckDigit(digits),
            12 or 13 => digits,
            _ => null,
        };
    }

    private static int Whole(string? raw) => WholeOrNull(raw) ?? 0;

    private static int? WholeOrNull(string? raw)
    {
        string text = Bare(raw);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Abs(value) : null;
    }

    private static decimal Money(string? raw) =>
        SignedMoneyOrNull(raw) is decimal value ? Math.Abs(value) : 0m;

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

    private static bool IsDump(string? section) =>
        section?.Contains("DUMP", StringComparison.OrdinalIgnoreCase) == true
        || section?.Contains("CREDIT", StringComparison.OrdinalIgnoreCase) == true
        || section?.Contains("RETURN", StringComparison.OrdinalIgnoreCase) == true;

    private static string SectionName(bool dump) => dump ? "DUMP" : "SALES";

    private static string PrintedMoney(ChubiwinosVisionRow row) =>
        !string.IsNullOrWhiteSpace(row.Amount) ? row.Amount.Trim()
        : !string.IsNullOrWhiteSpace(row.Price) ? row.Price.Trim()
        : "no money";

    private static string Count(int many, string noun) =>
        $"{many} {noun}{(many == 1 ? "" : "s")}";
}
