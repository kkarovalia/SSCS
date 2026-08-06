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
///
/// Handwriting: these invoices come back marked up, and OCR turns a pen stroke into a stray token
/// wherever it crossed the page. A mark on white space or on the unused NET/TAX columns is dropped
/// silently; a mark on a barcode, quantity or price is refused with the line quoted, because those
/// decide what is booked into stock and at what price. See ReadAmounts and the unclaimed-row scan.
/// </summary>
public static partial class DrPepperPdfParser
{
    /// <summary>
    /// Supplier name written into the XML. SSCS resolves it to its own Vendor ID through the
    /// "Map Supplier to Vendor" screen; an unrecognised name still imports and the operator can
    /// pick the vendor from the dropdown.
    /// </summary>
    public const string SupplierName = "DR PEPPER";

    // Product line 1: "<description> <UPC>/<SKU>". The code token is anchored at the end of the line.
    //
    // Two things the scans do to it, both of which the pattern absorbs and StripCodeNoise undoes:
    //  - it inserts spaces and punctuation inside the digits ("07 181700 1320/10090 16 1",
    //    "071817033291/100,00862", "071817`000453/10000069");
    //  - it loses the space between description and barcode entirely, printing them welded
    //    ("0.5L12 VC PureCocoDb898999010007/20000642").
    //
    // Hence the separator is "whitespace, or none at all provided the character before the code is a
    // letter". A letter is required because it is the one boundary the description cannot fake: were
    // a bare digit allowed to end the description, the code could start part-way through the barcode
    // and produce a shorter, wrong UPC that still passed every length check below.
    [GeneratedRegex(@"^(?<desc>.*?\S)(?:\s+|(?<=\p{L}))(?<code>\d[\d ,'`]*/[\d ,'`]*\d)\s*$")]
    private static partial Regex ProductLine();

    /// <summary>
    /// Punctuation the scanner sprinkles through the code token. Unlike a space — which only ever
    /// splits digits apart and cannot hide one — a mark like these sits *on* the barcode, and may
    /// have taken a digit with it. Codes that needed one removed are held to their check digit.
    /// </summary>
    private static readonly char[] CodeInk = [',', '\'', '`'];

    private static string StripCodeNoise(string code)
    {
        code = code.Replace(" ", "");
        foreach (char c in CodeInk) code = code.Replace(c.ToString(), "");
        return code;
    }

    /// <summary>True when the barcode half of the code token had ink taken out of it.</summary>
    private static bool UpcNeededRepair(string rawCode)
    {
        int slash = rawCode.IndexOf('/');
        string rawUpc = slash >= 0 ? rawCode[..slash] : rawCode;
        return rawUpc.IndexOfAny(CodeInk) >= 0;
    }

    // Product line 2 is read column by column rather than with one regex — see ReadAmounts.
    // A money figure: the separator tolerates common OCR slips (a '.' scanned as ':' or ',').
    [GeneratedRegex(@"^\d+[.,:]\d{2}$")]
    private static partial Regex MoneyToken();

    // A plain count (CASES, UNITS).
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex CountToken();

    [GeneratedRegex(@"\bDATE[;:\s]+(\d{2})/(\d{2})/(\d{2})")]
    private static partial Regex DateLine();

    [GeneratedRegex(@"\bAMOUNT\s+DUE\s*\$?\s*(?<amt>[\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AmountDueLine();

    /// <summary>How a line read as the WHSLE/CASES/UNITS/NET/TAX/AMOUNT row of a product.</summary>
    private enum RowKind
    {
        /// <summary>Not a priced row at all — a package header, a subtotal, an address.</summary>
        NotAmounts,
        /// <summary>Meant to be a priced row, but a column we rely on cannot be trusted.</summary>
        Unreadable,
        Ok,
    }

    /// <summary>The six columns of a priced row, once ink has been discarded.</summary>
    private readonly record struct AmountsRow(decimal Whsle, int Cases, int Units, decimal Amount);

    /// <summary>
    /// A token carrying no digit at all is ink, not data. Invoices come back from the store marked
    /// up by hand, and OCR transcribes a pen stroke as a stray "/" or "|" wherever it crossed the
    /// page — including on two consecutive lines, when the stroke was long enough to cross both.
    /// </summary>
    private static bool IsInk(string token) => !token.Any(char.IsAsciiDigit);

    /// <summary>
    /// Reads a priced row by column instead of by one fixed pattern, so a mark that landed in white
    /// space or on the unused NET/TAX columns is simply dropped. A mark that touches WHSLE, CASES,
    /// UNITS or AMOUNT is *not* dropped: those decide what is booked into stock and at what price,
    /// so the row comes back <see cref="RowKind.Unreadable"/> and the caller refuses the invoice.
    /// </summary>
    private static RowKind ReadAmounts(string line, out AmountsRow row)
    {
        row = default;

        string[] cols = line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !IsInk(t))
            .ToArray();

        // A priced row is identified by its shape — six-ish columns bracketed by money — and it is
        // enough for one end to survive, so a mark on WHSLE is reported as damage rather than
        // quietly demoting the row to "not a product". Package headers carry two or three columns
        // and PEG/PKG subtotals three, so neither ever gets past here.
        if (cols.Length < 4) return RowKind.NotAmounts;
        if (!MoneyToken().IsMatch(cols[0]) && !MoneyToken().IsMatch(cols[^1])) return RowKind.NotAmounts;

        // WHSLE CASES UNITS NET TAX AMOUNT. NET and TAX are unused and the most OCR-prone money
        // columns on the page (e.g. "32.S0", "0.00'"), so they are accepted as-is; every other
        // column must be clean.
        if (cols.Length != 6) return RowKind.Unreadable;
        if (!MoneyToken().IsMatch(cols[0]) || !MoneyToken().IsMatch(cols[5])) return RowKind.Unreadable;
        if (!CountToken().IsMatch(cols[1]) || !CountToken().IsMatch(cols[2])) return RowKind.Unreadable;

        row = new AmountsRow(
            Money(cols[0]),
            int.Parse(cols[1], CultureInfo.InvariantCulture),
            int.Parse(cols[2], CultureInfo.InvariantCulture),
            Money(cols[5]));
        return RowKind.Ok;
    }

    // A credit's AMOUNT, printed in parentheses: "(6.37)". A leading minus is accepted too.
    [GeneratedRegex(@"^(?:\(|-)(?<amt>\d+[.,:]\d{2})\)?$")]
    private static partial Regex CreditAmountToken();

    /// <summary>
    /// Reads a row of the CREDITS section — goods that went back on the truck. Credits print under
    /// their own, shorter header: <c>CASES UNITS NET TAX AMOUNT</c>, with **no WHSLE column** and
    /// the amount in parentheses. Same tolerance as <see cref="ReadAmounts"/>: ink is discarded,
    /// damage to a column that matters is refused.
    /// </summary>
    private static RowKind ReadCredit(string line, out AmountsRow row)
    {
        row = default;

        string[] cols = line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !IsInk(t))
            .ToArray();

        // Identified by the bracketed amount at the end — the one thing on the page that looks
        // like this. The SALES SUMMARY prints "Cases 0 (19.11)", but that is two columns, not five.
        if (cols.Length < 4 || !CreditAmountToken().IsMatch(cols[^1])) return RowKind.NotAmounts;

        if (cols.Length != 5) return RowKind.Unreadable;
        if (!CountToken().IsMatch(cols[0]) || !CountToken().IsMatch(cols[1])) return RowKind.Unreadable;

        // A credit carries no WHSLE column, so there is no vendor list price to seed a retail from.
        row = new AmountsRow(
            0m,
            int.Parse(cols[0], CultureInfo.InvariantCulture),
            int.Parse(cols[1], CultureInfo.InvariantCulture),
            -Money(CreditAmountToken().Match(cols[^1]).Groups["amt"].Value));
        return RowKind.Ok;
    }

    /// <summary>The CREDITS banner that switches the page over to the shorter column layout.</summary>
    private static bool IsCreditsHeader(string line) =>
        line.Trim().Equals("CREDITS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A UPC that needed noise stripped out of it is only trustworthy if it still passes its check
    /// digit. A clean 12-digit read that fails the test is an old, known situation — it gets a
    /// warning and ships (see the UpcCheckDigit path). A *repaired* one is different: something was
    /// physically sitting on the barcode, so a failed check digit means a digit went with it.
    /// </summary>
    private static bool RepairedUpcIsTrustworthy(string upc) => Upc.IsValidUpcA12(upc);

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
        var claimed = new HashSet<int>(); // indexes of priced rows a product line accounted for
        decimal sum = 0m;
        bool inCredits = false;

        for (int i = 0; i < lines.Count; i++)
        {
            // The invoice total ends the line-item section; anything after it (equipment, notices,
            // scan sheet) is not a product row.
            if (AmountDueLine().IsMatch(lines[i])) break;

            // Past this banner the columns change shape and the money turns negative.
            if (IsCreditsHeader(lines[i])) { inCredits = true; continue; }

            var pm = ProductLine().Match(lines[i]);
            if (!pm.Success) continue;

            // Undo the scanner's damage to the code token, then split UPC/SKU.
            string raw = pm.Groups["code"].Value;
            string code = StripCodeNoise(raw);
            int slash = code.IndexOf('/');
            string upc = slash >= 0 ? code[..slash] : "";
            string sku = slash >= 0 ? code[(slash + 1)..] : "";
            if (upc.Length is < 6 or > 14 || sku.Length is < 3 or > 12)
                continue; // not a real product-code line

            if (i + 1 >= lines.Count) break;
            var kind = inCredits
                ? ReadCredit(lines[i + 1], out var row)
                : ReadAmounts(lines[i + 1], out row);
            if (kind == RowKind.NotAmounts) continue; // a "<...>/<...>" line with no amounts row beneath it is not a product

            int lineNo = i + 2; // 1-based line number of the amounts row
            claimed.Add(i + 1);
            i++;                // consume the amounts row

            string desc = pm.Groups["desc"].Value.Trim();

            if (kind == RowKind.Unreadable)
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"Line {lineNo} of this Dr Pepper invoice ({desc}) could not be read: \"{lines[lineNo - 1].Trim()}\". " +
                    "Something is sitting on the quantity or price columns — a pen mark, a staple, or a bad scan. " +
                    "Compare that line with the paper invoice; if the figure is covered on the paper, this invoice has to be keyed in by hand.");

            int cases = row.Cases;
            // Credits are goods going back. SSCS wants the sign on the count, not the money, so
            // units x cost is still the printed amount.
            int units = inCredits ? -Math.Abs(row.Units) : row.Units;
            decimal whsle = row.Whsle;
            decimal amount = row.Amount;

            // The barcode had noise in it and we took the noise out. Only the check digit can say
            // whether a digit came out with it, so here it is a gate rather than a warning.
            if (UpcNeededRepair(raw) && !RepairedUpcIsTrustworthy(upc))
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    $"Line {lineNo - 1} of this Dr Pepper invoice ({desc}) has a damaged barcode: it scanned as " +
                    $"\"{raw.Trim()}\", which reads as '{upc}' and fails its check-digit test. " +
                    "Something is sitting on the barcode, so a digit cannot be recovered. " +
                    "Compare it with the paper invoice; this invoice has to be keyed in by hand.");

            // Free check-digit safeguard: a single mis-scanned digit almost always fails this.
            if (!Upc.IsValidUpcA12(upc))
                warnings.Add(new ConversionWarning(lineNo, PdfReasonCodes.UpcCheckDigit,
                    $"Line {lineNo} ({desc}): UPC '{upc}' failed its check-digit test and may have been mis-scanned. Please verify the barcode."));

            int unitsSafe = units != 0 ? Math.Abs(units) : 1;
            decimal unitCost = decimal.Round(Math.Abs(amount) / unitsSafe, 4, MidpointRounding.AwayFromZero);

            items.Add(new InvoiceLine
            {
                // Whole printed barcode. SSCS removes the check digit on import, landing on exactly
                // the item the retired EDI output addressed.
                Gtin = upc,
                Description = desc,
                Units = units,
                UnitCost = unitCost,
                // The WHSLE column is Dr Pepper's own list price per case, which is above the
                // discounted price actually billed. Per unit it is the closest thing the invoice
                // gives us to a suggested selling price, and it only ever seeds a brand-new item.
                // A credit has no WHSLE column at all, so it falls back to the shared cost-plus
                // seed — and being a return, its item is one the store already stocks and priced.
                RetailPrice = inCredits
                    ? RetailSeed.FromCost(unitCost)
                    : decimal.Round(whsle * cases / unitsSafe, 2, MidpointRounding.AwayFromZero),
            });
            sum += amount;
        }

        // A priced row that no product claimed means the line above it — description and barcode —
        // did not read. The money would still reconcile, so the total check below cannot see this:
        // the invoice would go out short an item, or with the wrong barcode on one. Refuse instead.
        bool creditsHere = false;
        for (int j = 0; j < lines.Count; j++)
        {
            if (AmountDueLine().IsMatch(lines[j])) break;
            if (IsCreditsHeader(lines[j])) { creditsHere = true; continue; }
            if (claimed.Contains(j)) continue;

            var orphan = creditsHere ? ReadCredit(lines[j], out _) : ReadAmounts(lines[j], out _);
            if (orphan != RowKind.Ok) continue;

            string above = j > 0 ? lines[j - 1].Trim() : "";
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Line {j + 1} of this Dr Pepper invoice is priced, but the product line above it could not be read: \"{above}\". " +
                "The barcode there is damaged — a pen mark, a staple, or a bad scan. " +
                "Compare it with the paper invoice; if the barcode is covered on the paper, this invoice has to be keyed in by hand.");
        }

        // Returns are the one thing on this invoice a person should eyeball, because they move stock
        // the other way and because the ticket numbers them separately from the sale.
        int creditCount = items.Count(l => l.Units < 0);
        if (creditCount > 0)
        {
            var numbers = FindInvoiceNumbers(lines);
            string alsoNumbered = numbers.Count > 1
                ? $" The ticket prints {numbers.Count} invoice numbers ({string.Join(" and ", numbers)}); "
                  + $"all of it went out under {invoiceNumber}, which is how the printed Amount Due is totalled."
                : "";
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This invoice has {creditCount} credit line{(creditCount == 1 ? "" : "s")} — goods that went back on "
                + "the truck. They go to SSCS as negative quantities at a positive cost, so the delivery nets out to "
                + $"the printed total.{alsoNumbered}"));
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

    private static string? FindInvoiceNumber(IReadOnlyList<string> lines) =>
        FindInvoiceNumbers(lines).FirstOrDefault();

    /// <summary>
    /// Every number printed under the INVOICE banner. A delivery that carries returns prints two —
    /// 4646105204 and 4646105205 on the 07/24/26 ticket — evidently one document for the sale and
    /// one for the credit, though the ticket totals them into a single Amount Due. We file the whole
    /// thing under the first and say so, rather than guess at splitting one payable in two.
    /// </summary>
    private static List<string> FindInvoiceNumbers(IReadOnlyList<string> lines)
    {
        var found = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals("INVOICE", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
            {
                string t = lines[j].Trim();
                if (t.Length is >= 6 and <= 12 && t.All(char.IsAsciiDigit))
                    found.Add(t);
            }
            if (found.Count > 0) break;
        }
        return found;
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
