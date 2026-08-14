using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>
/// Turns Gemini's one Bimbo reading directly into invoice lines. The model owns every printed
/// value on this path; Core only parses those values, applies Bimbo's fixed UPC transformation and
/// reports arithmetic disagreements as notes on a file that still gets written.
/// </summary>
public static class BimboVisionParser
{
    private const decimal CentPerLine = 0.01m;

    /// <summary>
    /// Maps the reading onto a ticket. A row without a ten-digit printed UPC body, a positive QTY
    /// or a positive wholesale AMOUNT cannot be posted and is skipped. The only conversion failure
    /// left is a reading with no postable row at all.
    /// </summary>
    public static BimboTicket Parse(
        BimboVisionReading reading, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(warnings);

        var lines = new List<InvoiceLine>();
        var skipped = new List<string>();
        var crooked = new List<string>();
        var derivedPrices = new List<string>();
        var missingDirections = new List<string>();
        int deliveredUnits = 0, returnedUnits = 0, creditLines = 0;
        // Each band's own money and row count, accumulated here rather than re-derived afterwards.
        // A second pass over the rows meant a second copy of the postability test and the price
        // fallback, which is two places to keep in step for no gain.
        var delivered = new Band();
        var returned = new Band();

        foreach (BimboVisionRow raw in reading.Rows)
        {
            string description = Describe(raw);
            bool inferredCredit = IsNegative(raw.Units) || IsNegative(raw.Amount);
            bool credit = raw.IsCredit ?? inferredCredit;
            if (raw.IsCredit is null)
                missingDirections.Add($"{description} ({(credit ? "return" : "delivery")})");

            string? body = PrintedUpcBody(raw.Barcode);
            int units = Whole(raw.Units);
            decimal amount = Money(raw.Amount);

            // These are the three printed values a postable row cannot exist without. A missing
            // PRICE is different: QTY and wholesale AMOUNT still state the per-unit cost exactly
            // enough for NAXML, so that one is calculated and called out below.
            if (body is null || units <= 0 || amount <= 0m)
            {
                skipped.Add($"{description} ({Printed(raw.Amount)})");
                continue;
            }

            decimal price = Money(raw.Price);
            if (price <= 0m)
            {
                price = decimal.Round(amount / units, 4, MidpointRounding.AwayFromZero);
                derivedPrices.Add($"{description} ({amount:0.00} / {units} = {price:0.0000})");
            }
            else
            {
                decimal extended = decimal.Round(units * price, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(extended - amount) >= 0.005m)
                    crooked.Add($"{description} ({units} x {price:0.0000} is {extended:0.00}, "
                                + $"not {amount:0.00})");
            }

            decimal booked_ = decimal.Round(units * price, 2, MidpointRounding.AwayFromZero);
            if (credit) { returnedUnits += units; creditLines++; returned.Add(booked_); }
            else { deliveredUnits += units; delivered.Add(booked_); }

            lines.Add(new InvoiceLine
            {
                // Bimbo prints the ten-digit body only. Restoring the known leading zero and then
                // computing the UPC-A check digit is the vendor's fixed format conversion, not a
                // repair of a model reading.
                Gtin = Upc.AppendCheckDigit("0" + body),
                Description = description,
                Units = credit ? -units : units,
                UnitCost = price,
                RetailPrice = RetailSeed.FromCost(price, BimboPdfParser.RetailMargin),
            });
        }

        if (lines.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "The AI read this Bimbo ticket but found no product line with a ten-digit printed "
                + "UPC, a unit count and a wholesale amount on it. Re-scan the complete ticket and "
                + "try again.");

        decimal booked = lines.Sum(line => line.ExtendedCost);
        decimal printedTotal = Money(reading.TicketTotal);
        Report(warnings, reading, lines.Count, skipped, crooked, derivedPrices, missingDirections,
            deliveredUnits, returnedUnits, creditLines, booked, printedTotal, delivered, returned);

        return new BimboTicket
        {
            // Invoice number and date are deliberately absent from the AI response and are typed
            // into empty boxes later. The ticket is paid for its printed total, just like Dr Pepper;
            // the sum stands in only for a malformed direct test double that bypassed the AI gate.
            Date = null,
            Total = printedTotal > 0m ? printedTotal : booked,
            Lines = lines,
        };
    }

    private static void Report(
        List<ConversionWarning> warnings, BimboVisionReading reading, int lineCount,
        List<string> skipped, List<string> crooked, List<string> derivedPrices,
        List<string> missingDirections, int deliveredUnits, int returnedUnits, int creditLines,
        decimal booked, decimal printedTotal, Band delivered, Band returned)
    {
        // Three different things used to share PDF_UNREADABLE_LINE, and only this first one means
        // goods are missing from the file. The other two ship a complete line and now say so in
        // their own code, so a dropped row is findable again.
        if (skipped.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(skipped.Count, "line")} could not be sent because the scan lost the UPC, "
                + $"QTY or wholesale AMOUNT: {string.Join("; ", skipped)}. Add them in SSCS by "
                + "hand, or re-scan the ticket."));

        if (derivedPrices.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.PriceDerived,
                $"{Count(derivedPrices.Count, "line")} had no readable PRICE. Its QTY and wholesale "
                + $"AMOUNT were used for unit cost: {string.Join("; ", derivedPrices)}. Check the "
                + "cost in SSCS."));

        if (crooked.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"{Count(crooked.Count, "line")} does not multiply out against its own printed "
                + $"figures: {string.Join("; ", crooked)}. The printed PRICE was sent. Worth a "
                + "glance at the paper."));

        if (missingDirections.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.SectionUnlabelled,
                $"{Count(missingDirections.Count, "line")} did not identify its DELIVERIES or "
                + $"RETURNS section. The printed sign was used where present and an unsigned row "
                + $"was treated as a delivery: {string.Join("; ", missingDirections)}. Check these "
                + "lines against the paper."));

        int signedUnits = deliveredUnits - returnedUnits;
        if (SignedWhole(reading.TicketUnits) is int ticketUnits && ticketUnits != signedUnits)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The {Count(lineCount, "line")} sent contain {signedUnits} units while TICKET "
                + $"TOTALS prints {ticketUnits}. Nothing downstream can catch a wrong quantity, so "
                + "check this ticket against the paper."));

        ReportBandUnits(warnings, "DELIVERY TOTAL", deliveredUnits, reading.Deliveries, credit: false);
        ReportBandUnits(warnings, "RETURNS TOTAL", returnedUnits, reading.Returns, credit: true);

        decimal tolerance = CentPerLine * Math.Max(1, lineCount);
        if (printedTotal <= 0m)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoTotal,
                $"TICKET TOTALS could not be read as money. The {Count(lineCount, "line")} sent "
                + $"come to {booked:0.00}, and that sum was used for the invoice total."));
        else if (!Near(booked, printedTotal, tolerance))
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The {Count(lineCount, "line")} sent come to {booked:0.00} against TICKET TOTALS "
                + $"of {printedTotal:0.00}. The invoice was sent for the printed total."));

        ReportBandMoney(warnings, "DELIVERY TOTAL", reading.Deliveries, delivered);
        ReportBandMoney(warnings, "RETURNS TOTAL", reading.Returns, returned);

        if (creditLines > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {Count(creditLines, "routine return line")}. They were sent as "
                + "negative quantities at positive costs on the same payable.", Routine: true));
    }

    private static void ReportBandUnits(
        List<ConversionWarning> warnings, string label, int actual,
        BimboVisionBandTotal? band, bool credit)
    {
        if (SignedWhole(band?.Units) is not int printed) return;
        int expected = Math.Abs(printed);
        if (actual == expected) return;
        warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
            $"The posted {(credit ? "return" : "delivery")} rows contain {actual} units while "
            + $"{label} prints {expected}. Check that band against the paper."));
    }

    /// <summary>
    /// Compares one band's posted money against the figure the band prints for itself.
    ///
    /// Both sides are compared as magnitudes. The ticket prints its RETURNS band with a minus in
    /// front of every figure, but nothing tells the model whether to copy that minus or hand back
    /// a bare magnitude the way the product rows are told to — and the two spellings mean the same
    /// money. Reading the sign as meaningful is what fired this warning on six of sixteen live
    /// tickets whose totals reconciled to the cent. The units check next door already drops the
    /// sign; this now matches it.
    /// </summary>
    private static void ReportBandMoney(
        List<ConversionWarning> warnings, string label, BimboVisionBandTotal? band, Band posted)
    {
        decimal printed = Math.Abs(SignedMoney(band?.Amount));
        if (printed == 0m) return;
        // Every line is rounded to the cent on its own before it is added up, so a band of twenty
        // rows can sit a few cents off the printed figure with nothing wrong. One flat cent for a
        // whole band was the wrong allowance; this is the one the ticket total already uses.
        decimal tolerance = CentPerLine * Math.Max(1, posted.Rows);
        if (Near(posted.Money, printed, tolerance)) return;
        warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
            $"The posted rows in this band come to {posted.Money:0.00} while {label} prints "
            + $"{printed:0.00}. The row values were sent as read; check the band in SSCS."));
    }

    /// <summary>One band's posted money and how many lines it was spread across.</summary>
    private sealed class Band
    {
        public decimal Money { get; private set; }
        public int Rows { get; private set; }

        public void Add(decimal extendedCost)
        {
            Money += extendedCost;
            Rows++;
        }
    }

    private static string Describe(BimboVisionRow row) =>
        string.IsNullOrWhiteSpace(row.Description) ? "BIMBO" : row.Description.Trim();

    private static string? PrintedUpcBody(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        if (text.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(text);
        return digits.Length == 10 ? digits : null;
    }

    private static int Whole(string? raw)
    {
        string text = Bare(raw);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return 0;
        return Math.Abs(value);
    }

    private static int? SignedWhole(string? raw)
    {
        string text = Bare(raw);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : null;
    }

    private static decimal Money(string? raw) => Math.Abs(SignedMoney(raw));

    private static decimal SignedMoney(string? raw)
    {
        string text = Bare(raw);
        return decimal.TryParse(text,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal value) ? value : 0m;
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

    private static bool IsNegative(string? raw) => SignedMoney(raw) < 0m;

    private static string Printed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "no amount" : raw.Trim();

    private static string Count(int many, string noun) =>
        $"{many} {noun}{(many == 1 ? "" : "s")}";

    private static bool Near(decimal a, decimal b, decimal tolerance) =>
        Math.Abs(a - b) <= tolerance;
}
