using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// Parses a Dr Pepper Bottling Company of Texas invoice PDF (as reconstructed text lines from
/// <see cref="PdfTextExtractor"/>) into an <see cref="Invoice"/>. Rules are documented in
/// analysis/vendors/DR_PEPPER.md and confirmed end-to-end against live SSCS.
///
/// Layout: each product is two lines — "&lt;description&gt; &lt;UPC(12)&gt;/&lt;SKU&gt;" then
/// "&lt;WHSLE&gt; &lt;CASES&gt; &lt;UNITS&gt; &lt;NET&gt; &lt;TAX&gt; &lt;AMOUNT&gt;". Package-group
/// headers, PEG/PKG/PRO/RAG subtotal lines and non-priced PALLET/SHELL equipment lines are skipped
/// naturally (they match neither pattern).
///
/// Mapping: units = UNITS (never cases); unit cost = AMOUNT ÷ UNITS to 4 decimals; barcode = the
/// printed 12-digit UPC **whole, check digit included** — SSCS drops that digit itself on import.
/// The printed AMOUNT DUE must equal the sum of the line amounts or the file is refused.
/// </summary>
public static partial class DrPepperPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. SSCS resolves it to its own Vendor ID through the
    /// "Map Supplier to Vendor" screen; an unrecognised name still imports and the operator can
    /// pick the vendor from the dropdown.
    /// </summary>
    public const string SupplierName = "DR PEPPER";

    // Product line 1: "<description> <UPC>/<SKU>". The code token is anchored at the end of the line and
    // may contain OCR-inserted spaces inside the digit groups (e.g. "07 181700 1320/10090 16 1"); the
    // description always contains a non-digit character, so it never absorbs the code.
    [GeneratedRegex(@"^(?<desc>.*?\S)\s+(?<code>\d[\d ]*/[\d ]*\d)\s*$")]
    private static partial Regex ProductLine();

    // Product line 2: WHSLE CASES UNITS NET TAX AMOUNT. Only CASES, UNITS and AMOUNT are used, so NET
    // and TAX (the most OCR-prone money columns, e.g. "32.S0") are captured loosely. The money
    // separator tolerates common OCR slips (a '.' scanned as ':' or ','); values are normalized.
    [GeneratedRegex(@"^\s*(?<whsle>\d+[.,:]\d{2})\s+(?<cases>\d+)\s+(?<units>\d+)\s+\S+\s+\S+\s+(?<amount>\d+[.,:]\d{2})\s*$")]
    private static partial Regex AmountsLine();

    [GeneratedRegex(@"\bDATE[;:\s]+(\d{2})/(\d{2})/(\d{2})")]
    private static partial Regex DateLine();

    [GeneratedRegex(@"\bAMOUNT\s+DUE\s*\$?\s*(?<amt>[\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AmountDueLine();

    /// <summary>
    /// Parses the invoice. Adds non-fatal notes (OCR-suspect UPCs) to <paramref name="warnings"/>;
    /// throws <see cref="EdiConversionException"/> for problems that make the result untrustworthy
    /// (no header fields, no items, or a total that does not reconcile).
    /// </summary>
    public static Invoice Parse(IReadOnlyList<string> lines, List<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(warnings);

        string invoiceNumber = FindInvoiceNumber(lines)
            ?? throw new EdiConversionException(PdfReasonCodes.NoInvoiceNumber,
                "Could not find the invoice number on this Dr Pepper invoice. The PDF may be an unexpected layout.");
        DateOnly date = FindDate(lines)
            ?? throw new EdiConversionException(PdfReasonCodes.NoDate,
                "Could not find the invoice date (DATE: MM/DD/YY) on this Dr Pepper invoice.");
        decimal total = FindAmountDue(lines)
            ?? throw new EdiConversionException(PdfReasonCodes.NoTotal,
                "Could not find the AMOUNT DUE total on this Dr Pepper invoice.");

        var items = new List<InvoiceLine>();
        decimal sum = 0m;

        for (int i = 0; i < lines.Count; i++)
        {
            // The invoice total ends the line-item section; anything after it (equipment, notices,
            // scan sheet) is not a product row.
            if (AmountDueLine().IsMatch(lines[i])) break;

            var pm = ProductLine().Match(lines[i]);
            if (!pm.Success) continue;

            // Strip OCR-inserted spaces from the code token, then split UPC/SKU.
            string code = pm.Groups["code"].Value.Replace(" ", "");
            int slash = code.IndexOf('/');
            string upc = slash >= 0 ? code[..slash] : "";
            string sku = slash >= 0 ? code[(slash + 1)..] : "";
            if (upc.Length is < 6 or > 14 || sku.Length is < 3 or > 12)
                continue; // not a real product-code line

            if (i + 1 >= lines.Count) break;
            var am = AmountsLine().Match(lines[i + 1]);
            if (!am.Success) continue; // a "<...>/<...>" line with no amounts row beneath it is not a product

            int lineNo = i + 2; // 1-based line number of the amounts row
            i++;                // consume the amounts row

            string desc = pm.Groups["desc"].Value.Trim();
            int cases = int.Parse(am.Groups["cases"].Value, CultureInfo.InvariantCulture);
            int units = int.Parse(am.Groups["units"].Value, CultureInfo.InvariantCulture);
            decimal whsle = Money(am.Groups["whsle"].Value);
            decimal amount = Money(am.Groups["amount"].Value);

            // Free check-digit safeguard: a single mis-scanned digit almost always fails this.
            if (!Upc.IsValidUpcA12(upc))
                warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
                    $"Line {lineNo} ({desc}): UPC '{upc}' failed its check-digit test and may have been mis-scanned. Please verify the barcode."));

            int unitsSafe = units > 0 ? units : 1;

            items.Add(new InvoiceLine
            {
                // Whole printed barcode. SSCS removes the check digit on import, landing on exactly
                // the item the retired EDI output addressed.
                Gtin = upc,
                Description = desc,
                Units = units,
                UnitCost = decimal.Round(amount / unitsSafe, 4, MidpointRounding.AwayFromZero),
                // The WHSLE column is Dr Pepper's own list price per case, which is above the
                // discounted price actually billed. Per unit it is the closest thing the invoice
                // gives us to a suggested selling price, and it only ever seeds a brand-new item.
                RetailPrice = decimal.Round(whsle * cases / unitsSafe, 2, MidpointRounding.AwayFromZero),
            });
            sum += amount;
        }

        if (items.Count == 0)
            throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                "No product line items were found on this Dr Pepper invoice.");

        // Hard gate: the parsed line items must reconcile to the printed AMOUNT DUE. A mismatch means
        // a page is missing, duplicated, or misread — refuse rather than emit a wrong file.
        if (Math.Abs(sum - total) > items.Count * 0.01m)
            throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
                $"The line items add up to {sum:0.00} but the invoice AMOUNT DUE is {total:0.00}. " +
                "A page may be missing or misread. Please re-scan the whole invoice as one file and try again.");

        return new Invoice
        {
            SupplierName = SupplierName,
            InvoiceNumber = invoiceNumber,
            Date = date,
            Total = total,
            Lines = items,
        };
    }

    private static string? FindInvoiceNumber(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals("INVOICE", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
            {
                string t = lines[j].Trim();
                if (t.Length is >= 6 and <= 12 && t.All(char.IsAsciiDigit))
                    return t;
            }
        }
        return null;
    }

    private static DateOnly? FindDate(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = DateLine().Match(line);
            if (!m.Success) continue;
            int mm = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int dd = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int yy = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (mm is < 1 or > 12) continue;
            int year = 2000 + yy;
            if (dd < 1 || dd > DateTime.DaysInMonth(year, mm)) continue;
            return new DateOnly(year, mm, dd);
        }
        return null;
    }

    private static decimal? FindAmountDue(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            var m = AmountDueLine().Match(line);
            if (m.Success)
                return decimal.Parse(m.Groups["amt"].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>Parses a money token, normalizing an OCR-slipped ':' or ',' decimal separator to '.'.</summary>
    private static decimal Money(string s) =>
        decimal.Parse(s.Replace(':', '.').Replace(',', '.'), CultureInfo.InvariantCulture);
}
