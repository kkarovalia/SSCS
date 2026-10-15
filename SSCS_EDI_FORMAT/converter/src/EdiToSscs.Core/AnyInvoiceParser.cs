using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>Any vendor's invoice read off the pictures, awaiting the typed invoice number and date.</summary>
public sealed class AnyInvoiceTicket
{
    /// <summary>The supplier's name as the AI read it. It only names the file.</summary>
    public required string VendorName { get; init; }

    /// <summary>The printed invoice total where one was read, otherwise what the lines come to.</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    public Invoice ToInvoice(string invoiceNumber, DateOnly date) => new()
    {
        SupplierName = AnyInvoiceParser.SupplierToken(VendorName),
        InvoiceNumber = invoiceNumber,
        Date = date,
        Total = Total,
        Lines = Lines,
    };
}

/// <summary>
/// Turns one AI reading of any vendor's invoice into Modisoft lines.
///
/// This is the converter doing what used to be done by hand for every vendor it had no reader for
/// (analysis/INVOICE_TO_MODISOFT.md): Silver Eagle, Faust, Yumi, Pepsi, Frito-Lay, Mini Melts. The
/// same rules hold. The AI owns what is printed and Core never argues with it; a figure that does
/// not add up is written anyway and said out loud, so nothing here asks about a product line. The
/// only refusal is an invoice with no line Modisoft could match at all.
///
/// Two things Modisoft needs that a vendor's paper does not always give:
///
/// - **A whole barcode.** Vendors print them every which way. A complete UPC-A or EAN-13 goes in as
///   printed; an 11-digit body gets the check digit it left off (exact); a 10-digit body (Bimbo's
///   style) gets a 0 in front and the check digit; a UPC-E is expanded; an EAN-13 or GTIN-14 that
///   is only a UPC-A with zeros in front loses the zeros, because Xpress Wholesale matched a
///   <c>0</c> + UPC-A to nothing (Yumi 29008246, 2026-10-09). A code that fails its check digit
///   ships as read and is named, never repaired. A line with no usable code is left out and named
///   with its money, for adding by hand.
/// - **Units per case.** Modisoft books Qty cases at a case cost and divides by the file's units
///   per case to cost one item. Where the paper prints it (a pack column, PPC, U/C, or a UNITS
///   figure), it is used. Where only the description says (<c>24/16 OZ CAN</c>), it is used and the
///   line is named, because pack text misled on eight Silver Eagle and Faust lines and only the
///   store's margins showed it.
/// </summary>
public static class AnyInvoiceParser
{
    public static AnyInvoiceTicket Parse(AnyInvoiceVisionReading reading, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(warnings);

        string vendor = (reading.VendorName ?? "").Trim();
        var lines = new List<InvoiceLine>();
        var printedAmounts = new List<(AnyInvoiceVisionRow Row, InvoiceLine Line)>();
        var noCode = new List<string>();
        var leftOut = new List<string>();
        var badCodes = new List<string>();
        var completed = new List<string>();
        var guessedUnits = new List<string>();
        int credits = 0;

        foreach (AnyInvoiceVisionRow row in reading.Rows)
        {
            string description = Describe(row);
            decimal? signedAmount = SignedNumber(row.Amount);
            bool credit = row.IsCredit ?? (signedAmount < 0m || SignedNumber(row.Quantity) < 0m);
            decimal? amount = signedAmount is decimal a ? Math.Abs(a) : null;

            if (amount is null)
            {
                leftOut.Add($"{description} (no amount could be read)");
                continue;
            }

            var code = Barcode(row.Barcode, vendor);
            if (code.Gtin is null)
            {
                string money = $"{(credit ? "-" : "")}{amount:0.00}";
                if (code.Problem == BarcodeProblem.NotPrinted) noCode.Add($"{description} ({money})");
                else leftOut.Add($"{description} ({money}, only \"{Upc.DigitsOnly(row.Barcode)}\" of its barcode is printed)");
                continue;
            }

            var count = Count(row);
            if (count is null)
            {
                leftOut.Add($"{description} ({(credit ? "-" : "")}{amount:0.00}, no quantity could be read)");
                continue;
            }

            if (code.Problem == BarcodeProblem.FailsCheckDigit) badCodes.Add($"{description} ({code.Gtin})");
            if (code.Problem == BarcodeProblem.Completed) completed.Add($"{description} ({Upc.DigitsOnly(row.Barcode)} went in as {code.Gtin})");
            if (count.Value.Guessed)
                guessedUnits.Add(count.Value.Cases is int cases
                    ? $"{description} ({cases} x {count.Value.Units / cases})"
                    : $"{description} ({count.Value.Units} single items)");
            if (credit) credits++;

            int units = count.Value.Units;
            var line = new InvoiceLine
            {
                Gtin = code.Gtin,
                Description = description,
                Units = credit ? -units : units,
                // Not rounded to SSCS's four places. These lines only ever go to Modisoft, which
                // shares the line's amount out by the case; a rounded unit cost times a few hundred
                // items lands a cent or two away from the printed amount, and this lands on it.
                UnitCost = units == 0 ? 0m : amount.Value / units,
                // Modisoft never takes a price from the file (2021 GHRA import), and Update Retail
                // from EDI is never pressed, so nothing is invented for it.
                RetailPrice = 0m,
                Cases = count.Value.Cases,
            };
            lines.Add(line);
            printedAmounts.Add((row, line));
        }

        if (lines.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                noCode.Count > 0 && leftOut.Count == 0
                    ? $"None of the {Count(noCode.Count, "line")} on this invoice prints a barcode, so "
                      + "there is nothing Modisoft could match them to. Enter this invoice in Modisoft by hand."
                    : "The AI read this invoice but found no line with a whole barcode, a quantity and "
                      + "an amount on it. Take a clearer picture of the whole invoice and try again, or "
                      + "enter it in Modisoft by hand.");

        decimal booked = lines.Sum(line => line.ExtendedCost);
        decimal? printedTotal = SignedNumber(reading.InvoiceTotal);

        Report(warnings, reading, lines, printedAmounts, noCode, leftOut, badCodes, completed,
            guessedUnits, credits, booked, printedTotal);

        return new AnyInvoiceTicket
        {
            VendorName = vendor,
            Total = printedTotal ?? booked,
            Lines = lines,
        };
    }

    /// <summary>
    /// The vendor's name as a short file-name token: its first two words, letters and digits only.
    /// "Silver Eagle Beverages, LLC" is <c>SILVEREAGLE</c>, so the file is
    /// <c>MODISOFT_SILVEREAGLE_424753.txt</c>. Nothing in Modisoft reads it.
    /// </summary>
    public static string SupplierToken(string? vendorName)
    {
        var words = (vendorName ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant())
            .Where(word => word.Length > 0 && word != "THE")
            .Take(2);
        string token = string.Concat(words);
        if (token.Length > 20) token = token[..20];
        return token.Length == 0 ? "INVOICE" : token;
    }

    // ------------------------------------------------------------------ barcodes

    public enum BarcodeProblem { None, NotPrinted, Unusable, FailsCheckDigit, Completed }

    /// <summary>A barcode as it goes into the file, and what (if anything) is wrong with it.</summary>
    public readonly record struct BarcodeResult(string? Gtin, BarcodeProblem Problem);

    /// <summary>
    /// The whole barcode for a printed code, by how many digits were printed. See the class notes.
    /// <paramref name="vendor"/> matters for two vendors whose way of printing a code is known and
    /// was proven in SSCS: Blue Bell pads an 11-digit body to twelve with a leading 0, and Bimbo
    /// prints a 10-digit body. For them the completion is routine and not reported.
    /// </summary>
    public static BarcodeResult Barcode(string? printed, string? vendor)
    {
        string digits = Upc.DigitsOnly(printed);
        if (digits.Length == 0) return new(null, BarcodeProblem.NotPrinted);

        string name = new string((vendor ?? "").Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
        bool blueBell = name.Contains("BLUEBELL", StringComparison.Ordinal);
        bool bimbo = name.Contains("BIMBO", StringComparison.Ordinal);

        switch (digits.Length)
        {
            case 8:
                // UPC-E: number system 0 or 1, six digits, check digit. Expanded only when the
                // printed check digit agrees, otherwise it is not known to be a UPC-E at all.
                if (digits[0] is '0' or '1')
                {
                    string body = UpcE.Expand(digits[..7]);
                    if (UpcE.ComputeUpcACheckDigit(body) == digits[7])
                        return new(Upc.AppendCheckDigit(body), BarcodeProblem.None);
                }
                return new(digits, BarcodeProblem.FailsCheckDigit);

            case 10:
                return new(Upc.AppendCheckDigit("0" + digits),
                    bimbo ? BarcodeProblem.None : BarcodeProblem.Completed);

            case 11:
                return new(Upc.AppendCheckDigit(digits), BarcodeProblem.None);

            case 12:
                if (blueBell && digits[0] == '0') return new(BlueBellPdfParser.ToGtin(digits), BarcodeProblem.None);
                return new(digits, Upc.IsValidGtin(digits) ? BarcodeProblem.None : BarcodeProblem.FailsCheckDigit);

            case 13:
                if (!Upc.IsValidGtin(digits)) return new(digits, BarcodeProblem.FailsCheckDigit);
                return new(digits[0] == '0' ? digits[1..] : digits, BarcodeProblem.None);

            case 14:
                // A GTIN-14 that is only a 12- or 13-digit code with zeros in front (Silver Eagle's
                // pack UPC). One with any other first digit is a case code, which no store item has.
                if (digits[0] != '0') return new(null, BarcodeProblem.Unusable);
                return Barcode(digits[1..], vendor);

            default:
                return new(null, BarcodeProblem.Unusable);
        }
    }

    // ------------------------------------------------------------------ counting

    /// <summary>
    /// How many sellable items the line is, and how many cases when it is a whole number of them.
    /// Null when nothing on the line gives a count.
    ///
    /// A line goes in by the paper's own billing unit, as every converter vendor's has: cases where
    /// the paper bills cases, single items where it bills single items. Within a case line the
    /// line's own printed count of items wins over anything else, because it is printed: Dr
    /// Pepper's UNITS, Red Bull's UNITS, Pepsi's Units. A case count that does not divide it, or a
    /// fraction of a case (a Red Bull credit's 0.25), goes in one item at a time.
    /// </summary>
    public static (int Units, int? Cases, bool Guessed)? Count(AnyInvoiceVisionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        decimal quantity = Math.Abs(SignedNumber(row.Quantity) ?? 0m);
        int? pack = Whole(row.UnitsPerCase) is int p && p > 0 ? p : null;
        int? total = Whole(row.TotalUnits) is int t && t > 0 ? t : null;
        bool printedPack = string.Equals(row.UnitsSource?.Trim(), "printed", StringComparison.OrdinalIgnoreCase);
        int? whole = quantity > 0m && quantity == decimal.Truncate(quantity) && quantity <= int.MaxValue
            ? (int)quantity : null;

        if (BilledByItem(row, pack))
        {
            // Billed by the item, so it goes in by the item, whatever the case pack: Modisoft's Qty
            // is then the paper's own count. Blue Bell's half gallons (QTY 12 @ 6.85, packed four to
            // a box) imported that way and matched on 2026-10-09.
            int? units = total ?? whole;
            return units is null ? null : (units.Value, null, false);
        }

        if (whole is int caseCount)
        {
            if (total is int items)
                return items % caseCount == 0 ? (items, caseCount, false) : (items, null, false);
            if (pack is int perCase) return (caseCount * perCase, caseCount, !printedPack);
            // Nothing says how many are in a case. One per case keeps the money right and the
            // line is named, so somebody sets Units in Modisoft.
            return (caseCount, caseCount, true);
        }

        if (total is int loose) return (loose, null, false);
        if (quantity > 0m && pack is int size && quantity * size == decimal.Truncate(quantity * size))
            return ((int)(quantity * size), null, !printedPack);
        return null;
    }

    private static bool BilledByItem(AnyInvoiceVisionRow row, int? pack)
    {
        string billed = (row.BilledBy ?? "").Trim().ToLowerInvariant();
        if (billed.Contains("item") || billed.Contains("each") || billed.Contains("unit")) return true;
        if (billed.Contains("case") || billed.Contains("pack") || billed.Contains("box")) return false;
        return pack is null or 1;
    }

    // ------------------------------------------------------------------ notes

    private static void Report(
        List<ConversionWarning> warnings, AnyInvoiceVisionReading reading, IReadOnlyList<InvoiceLine> lines,
        IReadOnlyList<(AnyInvoiceVisionRow Row, InvoiceLine Line)> printed, List<string> noCode,
        List<string> leftOut, List<string> badCodes, List<string> completed, List<string> guessedUnits,
        int credits, decimal booked, decimal? printedTotal)
    {
        if (noCode.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.NoBarcode,
                $"{Count(noCode.Count, "line")} on this invoice {(noCode.Count == 1 ? "prints" : "print")} "
                + $"no barcode, so {(noCode.Count == 1 ? "it is" : "they are")} not in the file: "
                + $"{string.Join("; ", noCode)}. Add {(noCode.Count == 1 ? "it" : "them")} in Modisoft by hand."));

        if (leftOut.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                $"{Count(leftOut.Count, "line")} could not go in the file: {string.Join("; ", leftOut)}. "
                + $"Add {(leftOut.Count == 1 ? "it" : "them")} in Modisoft by hand."));

        if (badCodes.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"{Count(badCodes.Count, "barcode")} did not check out and went in as read: "
                + $"{string.Join("; ", badCodes)}. If Modisoft shows {(badCodes.Count == 1 ? "it" : "them")} "
                + "as a new item, check the digits against the paper."));

        if (completed.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.BarcodeCompleted,
                $"{Count(completed.Count, "barcode")} {(completed.Count == 1 ? "was" : "were")} printed "
                + "without the first and last digits, so a 0 went in front and the check digit on the "
                + $"end: {string.Join("; ", completed)}. Check {(completed.Count == 1 ? "it" : "they")} "
                + "matched an item the store already has."));

        if (guessedUnits.Count > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnitsNotPrinted,
                $"The invoice does not print how many come in a case for {Count(guessedUnits.Count, "line")}, "
                + $"so the count was taken from the description: {string.Join("; ", guessedUnits)}. "
                + "Check Units on these lines in Modisoft before saving."));

        decimal charges = reading.Charges.Sum(charge => SignedNumber(charge.Amount) ?? 0m);
        var named = reading.Charges
            .Where(charge => SignedNumber(charge.Amount) is decimal value && value != 0m)
            .Select(charge => $"{(string.IsNullOrWhiteSpace(charge.Label) ? "charge" : charge.Label.Trim())} "
                              + $"{SignedNumber(charge.Amount):0.00}")
            .ToList();

        // A ticket with a returns section often prints its sales total and its returns total and
        // no net (Blue Bell 033100786112: TOTAL 328.48, a 2.98 return, 325.50 owed). The sales
        // total agreeing with the deliveries is as good as the net agreeing with all the lines.
        decimal deliveries = lines.Where(line => line.Units > 0).Sum(line => line.ExtendedCost);
        if (printedTotal is decimal total
            && Math.Abs(booked + charges - total) >= 0.005m
            && Math.Abs(deliveries + charges - total) >= 0.005m)
        {
            var crooked = printed
                .Where(pair => pair.Line.Units > 0)
                .Select(pair => (pair.Row, pair.Line,
                    Quantity: SignedNumber(pair.Row.Quantity), Price: SignedNumber(pair.Row.Price)))
                .Where(row => row.Quantity is > 0m && row.Price is > 0m
                              && Math.Abs(decimal.Round(row.Quantity.Value * row.Price.Value, 2,
                                  MidpointRounding.AwayFromZero) - row.Line.ExtendedCost) >= 0.005m)
                .Select(row => $"{row.Line.Description} ({row.Quantity:0.##} x {row.Price:0.00##} is not "
                               + $"{row.Line.ExtendedCost:0.00})")
                .ToList();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The lines in the file come to {booked:0.00}"
                + (named.Count > 0 ? $", {booked + charges:0.00} with {string.Join(", ", named)}," : "")
                + $" but the invoice says {total:0.00}. A line may be missing or misread; compare the "
                + "file with the paper."
                + (crooked.Count > 0
                    ? $" These lines do not multiply out: {string.Join("; ", crooked)}."
                    : "")));
        }
        else if (named.Count > 0)
        {
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.OtherCharges,
                $"The invoice total also has {string.Join(", ", named)}, which {(named.Count == 1 ? "is" : "are")} "
                + $"not products and so not in the file. The file comes to {booked:0.00}. Add "
                + $"{(named.Count == 1 ? "it" : "them")} in Modisoft by hand if the purchase needs to match the paper."));
        }

        ReportQuantity(warnings, reading, lines, printed, noCode.Count + leftOut.Count);

        if (credits > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This invoice has {Count(credits, "credit line")}. They went in as negative quantities "
                + "on the same purchase.", Routine: true));
    }

    /// <summary>
    /// The invoice's own count of what it delivered, against the lines. It may count cases or
    /// items, and with or without returns, so it is a disagreement only when it matches none of
    /// those. Skipped when a line was left out, which already has its own note.
    /// </summary>
    private static void ReportQuantity(
        List<ConversionWarning> warnings, AnyInvoiceVisionReading reading, IReadOnlyList<InvoiceLine> lines,
        IReadOnlyList<(AnyInvoiceVisionRow Row, InvoiceLine Line)> printed, int missing)
    {
        if (missing > 0 || SignedNumber(reading.TotalQuantity) is not decimal count) return;
        count = Math.Abs(count);

        decimal Sum(Func<(AnyInvoiceVisionRow Row, InvoiceLine Line), decimal> pick, bool deliveriesOnly) =>
            printed.Where(pair => !deliveriesOnly || pair.Line.Units > 0).Sum(pick);

        decimal QuantityOf((AnyInvoiceVisionRow Row, InvoiceLine Line) pair) =>
            Math.Abs(SignedNumber(pair.Row.Quantity) ?? 0m) * Math.Sign(pair.Line.Units);

        var candidates = new[]
        {
            Sum(QuantityOf, deliveriesOnly: true),
            Sum(QuantityOf, deliveriesOnly: false),
            Sum(pair => pair.Line.Units, deliveriesOnly: true),
            Sum(pair => pair.Line.Units, deliveriesOnly: false),
            Sum(pair => pair.Line.Cases is int cases ? cases * Math.Sign(pair.Line.Units) : pair.Line.Units, deliveriesOnly: true),
        };
        if (candidates.Any(candidate => Math.Abs(Math.Abs(candidate) - count) < 0.005m)) return;

        warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
            $"The invoice counts {count:0.##} delivered, and the {Count(lines.Count, "line")} in the file "
            + $"come to {candidates[0]:0.##}. A line may be missing or a quantity misread; compare the "
            + "file with the paper."));
    }

    // ------------------------------------------------------------------ reading figures

    private static string Describe(AnyInvoiceVisionRow row) =>
        !string.IsNullOrWhiteSpace(row.Description) ? row.Description.Trim()
        : !string.IsNullOrWhiteSpace(row.ItemNumber) ? row.ItemNumber.Trim()
        : "ITEM";

    private static int? Whole(string? raw) =>
        SignedNumber(raw) is decimal value && value == decimal.Truncate(value) && Math.Abs(value) <= int.MaxValue
            ? (int)Math.Abs(value) : null;

    /// <summary>
    /// A printed figure with its sign: <c>(6.37)</c>, <c>6.37-</c> and <c>-6.37</c> are all negative;
    /// <c>$1,234.50</c> is money; <c>11/12</c> is a fraction (Blue Bell's part-carton return).
    /// Null for anything else.
    /// </summary>
    public static decimal? SignedNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        if (text.StartsWith('(') && text.EndsWith(')')) text = "-" + text[1..^1];
        else if (text.EndsWith('-')) text = "-" + text[..^1];

        int slash = text.IndexOf('/');
        if (slash > 0)
        {
            return decimal.TryParse(text[..slash], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                       CultureInfo.InvariantCulture, out decimal top)
                   && decimal.TryParse(text[(slash + 1)..], NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out decimal bottom)
                   && bottom != 0m
                ? top / bottom
                : null;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal value) ? value : null;
    }

    private static string Count(int many, string noun) => $"{many} {noun}{(many == 1 ? "" : "s")}";
}
