using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>A Dr Pepper ticket read off the picture, awaiting the operator-keyed header fields.</summary>
public sealed class DrPepperVisionTicket
{
    public required decimal Total { get; init; }
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = DrPepperPdfParser.SupplierName,
        InvoiceNumber = invoiceNumber,
        Date = date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Turns Gemini's Dr Pepper reading into invoice lines. It reads values; it does not argue with
/// them.
///
/// **This file was rewritten on 2026-08-13, and what it stopped doing matters more than what it
/// does.** The version before it had twenty-three ways to refuse an invoice, and across a full day
/// of the store's real tickets not one of them caught a real error. Every refusal that day traced
/// back to our own handling: a retry ladder that never ended, a row locator that counted product
/// lines twice, a verification pass that had never run being read as a verification pass that
/// disagreed, and a second barcode read that slid one row out of step and turned a perfect
/// thirty-row ticket into twenty-nine questions. Meanwhile the readings themselves were exact —
/// $1468.73 against a printed $1468.73, $1110.23 against a printed $1110.23, every row's
/// CASES x NET, every package subtotal.
///
/// So the gates are gone. A figure that does not reconcile is written into the file with a note on
/// it, because the alternative was handing back a delivery a person could read in ten seconds. The
/// checks that remain are arithmetic on numbers the model already returned — no extra request, no
/// prompt, nothing that can block — and they exist because two mistakes here are invisible
/// downstream. A wrong barcode posts the delivery to a duplicate item that nothing will ever scan,
/// so the real product shows nothing received. A wrong unit count balances to the penny and leaves
/// the shelf short forever. Both now arrive as a line the store can act on.
///
/// The AI checks its own arithmetic before it answers (see the prompt in
/// <see cref="GeminiVisionReader"/>), which is where a misread figure is actually cheap to catch:
/// the model can look at the paper again. We cannot.
/// </summary>
public static class DrPepperVisionParser
{
    /// <summary>
    /// Maps the reading onto invoice lines. The only thing that stops a file is a reading with
    /// nothing postable in it at all — every other disagreement is written down and shipped.
    /// </summary>
    public static DrPepperVisionTicket Parse(
        DrPepperVisionReading reading, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(warnings);

        var lines = new List<InvoiceLine>();
        var skipped = new List<string>();
        var unscannable = new List<string>();
        var crooked = new List<string>();
        int soldUnits = 0, returnedUnits = 0, creditLines = 0;

        foreach (DrPepperVisionRow raw in ProductRows(reading))
        {
            string description = Describe(raw);
            bool credit = raw.IsCredit == true;
            string? code = Digits(raw.Barcode);
            int count = Whole(raw.Units);
            decimal amount = Money(raw.Amount);

            // Three things cannot be shipped around. Without a code there is no item to post to,
            // and an empty GTIN is not a line SSCS can take at all. Without a count or an amount
            // there is no cost per unit, and inventing either one is how a delivery that never
            // arrived gets booked. A dropped row is named out loud with its money, so it can be
            // added by hand - which is a minute's work, unlike re-scanning the ticket.
            if (code is null || count <= 0 || amount <= 0m)
            {
                skipped.Add($"{description} ({Printed(raw.Amount)})");
                continue;
            }

            // Dr Pepper prints a whole UPC-A, so the twelfth digit is calculated from the other
            // eleven and the code can say for itself that it was misread. Nothing is repaired here:
            // a repair is a guess among ten equally valid candidates, and this is the one field
            // where a confident wrong answer creates a phantom item. It goes in as read, flagged.
            if (code.Length != 12 || !Upc.IsValidUpcA12(code)) unscannable.Add(description);

            decimal unitCost = decimal.Round(amount / count, 4, MidpointRounding.AwayFromZero);
            int cases = Whole(raw.Cases);
            decimal wholesale = Money(raw.Wholesale);
            decimal net = Money(raw.Net);

            // The row printing its own money a second time in different glyphs. Deliveries only:
            // a credit prints CASES 0 against a real amount, so the rule cannot hold either way
            // round and checking it there would only manufacture noise.
            if (!credit && cases > 0 && net > 0m && Math.Abs(cases * net - amount) >= 0.005m)
                crooked.Add($"{description} ({cases} x {net:0.00} is {cases * net:0.00}, "
                            + $"not {amount:0.00})");

            if (credit) { returnedUnits += count; creditLines++; }
            else soldUnits += count;

            lines.Add(new InvoiceLine
            {
                Gtin = code,
                Description = description,
                Units = credit ? -count : count,
                UnitCost = unitCost,
                // The wholesale column is the vendor's own shelf price and only ever seeds a
                // brand-new item; a credit row does not print one.
                RetailPrice = !credit && wholesale > 0m && cases > 0
                    ? decimal.Round(wholesale * cases / count, 2, MidpointRounding.AwayFromZero)
                    : RetailSeed.FromCost(unitCost),
            });
        }

        if (lines.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "The AI read this Dr Pepper invoice but found no product line with a barcode, a "
                + "unit count and an amount on it. Re-scan the complete invoice and try again.");

        decimal booked = lines.Sum(line => line.ExtendedCost);
        decimal printed = Money(reading.AmountDue);
        Report(warnings, reading, lines.Count, skipped, unscannable, crooked,
            soldUnits, returnedUnits, creditLines, booked, printed);

        // The invoice is paid for what it says it is worth. Our own sum stands in only when the
        // page did not print a total at all.
        return new DrPepperVisionTicket
        {
            Total = printed > 0m ? printed : booked,
            Lines = lines,
        };
    }

    /// <summary>
    /// Everything worth telling somebody, as notes on a file that has already been written. A note
    /// is here because a human has to go and do something about it, so a check nobody could act on
    /// does not get one.
    /// </summary>
    private static void Report(
        List<ConversionWarning> warnings, DrPepperVisionReading reading, int lineCount,
        List<string> skipped, List<string> unscannable, List<string> crooked,
        int soldUnits, int returnedUnits, int creditLines, decimal booked, decimal printed)
    {
        if (skipped.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(skipped.Count, "line")} could not be sent because the scan lost the "
                + $"barcode, the unit count or the amount: {string.Join("; ", skipped)}. "
                + "Add them in SSCS by hand, or re-scan the invoice."));

        if (unscannable.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"{Count(unscannable.Count, "barcode")} did not check out and went in as read: "
                + $"{string.Join(", ", unscannable)}. Each one lands on a new item in SSCS instead "
                + "of the real product, so fix the code there and the delivery follows it."));

        if (crooked.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.LineMathMismatch,
                $"{Count(crooked.Count, "line")} does not multiply out against its own printed "
                + $"figures: {string.Join("; ", crooked)}. The printed amount was used. Worth a "
                + "glance at the paper."));

        // The one mistake the money cannot see. A misread count balances to the penny, because the
        // amount is its own printed column - and the shelf is short for good.
        int printedSold = Whole(reading.TotalSaleUnits);
        if (printedSold == 0) printedSold = Whole(reading.Summary?.SalesUnits);
        if (printedSold > 0 && printedSold != soldUnits)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The invoice counts {printedSold} units sold and the lines come to {soldUnits}. "
                + "Nothing downstream can catch a wrong quantity, so this one is worth checking "
                + "against the paper."));

        int printedBack = Whole(reading.Summary?.CreditUnits);
        if (printedBack > 0 && printedBack != returnedUnits)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The invoice counts {printedBack} units returned and the credit lines come to "
                + $"{returnedUnits}. Worth checking against the paper."));

        if (printed > 0m && Math.Abs(booked - printed) >= 0.005m)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The {Count(lineCount, "line")} sent come to {booked:0.00} against a printed "
                + $"AMOUNT DUE of {printed:0.00}. The invoice was sent for the printed total."));

        if (creditLines > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This invoice has {Count(creditLines, "routine credit line")}. They were sent as "
                + "negative quantities at positive costs on the same payable.",
                Routine: true));
    }

    /// <summary>
    /// The physical products. Dr Pepper prints pallet, shell and debit-scan-sheet lines that are
    /// equipment movements rather than stock — but only when they carry neither money nor a code,
    /// because the same words turn up on real sellable items.
    /// </summary>
    private static IEnumerable<DrPepperVisionRow> ProductRows(DrPepperVisionReading reading) =>
        reading.Rows.Where(row =>
        {
            bool equipment =
                row.Description.Contains("PALLET", StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains("SHELL", StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains("DEBIT SCAN SHEET", StringComparison.OrdinalIgnoreCase);
            return !equipment || Money(row.Amount) > 0m || Digits(row.Barcode) is not null;
        });

    private static string Describe(DrPepperVisionRow row) =>
        string.IsNullOrWhiteSpace(row.Description) ? "DR PEPPER" : row.Description.Trim();

    /// <summary>
    /// The digits of the UPC cell.
    ///
    /// Dr Pepper prints `UPC/SKU` as one cell — `078000082166/10000836` — and the model returns
    /// sometimes the whole cell and sometimes just the code. Both are honest readings of the paper,
    /// and rejecting the first is what took seven of eight tickets down on 2026-08-13. Taking the
    /// part before the slash is the same rule the app used to give the operator in as many words.
    /// The digits are never run together, which would glue the SKU on and silently make a different
    /// code, and nothing here pads to twelve or computes a check digit.
    /// </summary>
    private static string? Digits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cell = raw.Trim();
        int slash = cell.IndexOf('/');
        if (slash >= 0) cell = cell[..slash];
        if (cell.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(cell);
        return digits.Length == 0 ? null : digits;
    }

    private static string Printed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "no amount" : raw.Trim();

    private static string Count(int many, string noun) =>
        $"{many} {noun}{(many == 1 ? "" : "s")}";

    /// <summary>A printed count, unsigned. Brackets and a trailing minus both mean a return.</summary>
    private static int Whole(string? raw)
    {
        string text = Bare(raw);
        return int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out int value) ? Math.Abs(value) : 0;
    }

    /// <summary>Printed money, unsigned. The section says whether it is a sale or a credit.</summary>
    private static decimal Money(string? raw)
    {
        string text = Bare(raw);
        return decimal.TryParse(text,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal value) ? Math.Abs(value) : 0m;
    }

    private static string Bare(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);
        if (text.StartsWith('(') && text.EndsWith(')')) text = text[1..^1];
        if (text.EndsWith('-')) text = text[..^1];
        return text.Trim();
    }
}
