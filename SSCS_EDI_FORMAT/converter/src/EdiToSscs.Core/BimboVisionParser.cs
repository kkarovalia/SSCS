using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>
/// Turns Gemini's Bimbo reading into the ordinary invoice model. It does not read OCR values: the
/// text layer is consulted only to recover a line number for <see cref="PdfRowSnipper"/>. Its job is
/// deliberately small - validate, ask about a disputed printed figure, normalise, and serialize.
/// </summary>
public static partial class BimboVisionParser
{
    private const decimal CentPerLine = 0.01m;

    private sealed class Row
    {
        public required string RowId { get; init; }
        public required int Position { get; init; }
        public required int Count { get; init; }
        public required BimboVisionRow Raw { get; init; }
        public required bool Credit { get; init; }
        public required int LineNo { get; init; }
        public required int Page { get; init; }
        public string ItemNumber => Raw.ItemNumber.Trim();
        public string Description => string.IsNullOrWhiteSpace(Raw.Description)
            ? "BIMBO" : Raw.Description.Trim();
        public int Units { get; set; }
        public decimal Price { get; set; }
        public decimal? Amount { get; set; }
        public string Upc10 { get; set; } = "";
        public HashSet<LineField> Confirmed { get; } = [];
        public decimal Extended => decimal.Round(
            (Credit ? -1m : 1m) * Units * Price, 2, MidpointRounding.AwayFromZero);
        public int SignedUnits => Credit ? -Units : Units;
        public decimal? SignedAmount => Amount is decimal a ? (Credit ? -a : a) : null;
    }

    public static BimboTicket Parse(
        BimboVisionReading reading,
        BimboBarcodeVerification verification,
        IReadOnlyList<PdfTextLine> textLines,
        List<ConversionWarning> warnings,
        Func<BimboUnknownField, string> ask) =>
        Parse(reading, verification, verification, textLines, warnings, ask);

    public static BimboTicket Parse(
        BimboVisionReading reading,
        BimboBarcodeVerification verification,
        BimboBarcodeVerification cropVerification,
        IReadOnlyList<PdfTextLine> textLines,
        List<ConversionWarning> warnings,
        Func<BimboUnknownField, string> ask)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(cropVerification);
        ArgumentNullException.ThrowIfNull(textLines);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(ask);

        if (reading.Status != BimboVisionStatus.Success || reading.Rows.Count == 0)
            throw UnreadableScan();

        if (!TrySignedInt(reading.TicketUnits, out int printedTicketUnits)
            || !TryMoney(reading.TicketTotal, allowSign: true, out decimal printedTicketTotal))
            throw UnreadableScan();

        var rows = reading.Rows.Select((raw, index) =>
        {
            bool credit = raw.IsCredit
                ?? throw UnreadableScan();
            var located = Locate(raw, textLines);
            return new Row
            {
                RowId = $"p{Math.Max(1, raw.Page)}-r{index + 1}",
                Position = index + 1,
                Count = reading.Rows.Count,
                Raw = raw,
                Credit = credit,
                LineNo = located?.LineNumber ?? 0,
                Page = located?.PageNumber ?? Math.Max(1, raw.Page),
            };
        }).ToList();

        foreach (var row in rows)
        {
            if (!TryUnits(row.Raw.Units, row.Credit, out int units))
                units = AskUnits(row, ask);
            row.Units = units;

            if (!TryMoney(row.Raw.Price, row.Credit, out decimal price) || price <= 0m)
                price = AskMoney(row, LineField.UnitPrice, row.Raw.Price, ask);
            row.Price = price;

            if (TryMoney(row.Raw.Amount, row.Credit, out decimal amount) && amount >= 0m)
                row.Amount = amount;
        }

        SettleBarcodes(rows, verification, cropVerification, ask);

        // A disagreement on one row is localised before any band-wide question. AMOUNT is checked
        // first because it is not written to XML; if that was the model's bad read, one question
        // protects both XML-critical values from needless edits.
        foreach (var row in rows.Where(RowMathDisagrees))
        {
            row.Amount = AskMoney(row, LineField.Amount, row.Raw.Amount, ask);
            if (!RowMathDisagrees(row)) continue;
            row.Price = AskMoney(row, LineField.UnitPrice, row.Raw.Price, ask);
            if (!RowMathDisagrees(row)) continue;
            row.Units = AskUnits(row, ask);
            if (RowMathDisagrees(row))
                warnings.Add(new ConversionWarning(row.LineNo, PdfReasonCodes.LineMathMismatch,
                    $"The confirmed QTY, PRICE and AMOUNT for \"{row.Description}\" do not multiply "
                    + "to the same cent. The confirmed QTY and PRICE were used; check this line in SSCS."));
        }

        SettleUnitChecks(rows, reading, printedTicketUnits, ask, warnings);
        SettleMoneyChecks(rows, reading, printedTicketTotal, ask, warnings);

        var items = rows.Select(row => new InvoiceLine
        {
            Gtin = Upc.AppendCheckDigit("0" + row.Upc10),
            Description = row.Description,
            Units = row.SignedUnits,
            UnitCost = row.Price,
            RetailPrice = RetailSeed.FromCost(row.Price, BimboPdfParser.RetailMargin),
        }).ToList();

        int credits = items.Count(l => l.Units < 0);
        if (credits > 0)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.CreditLines,
                $"This ticket has {credits} return line{(credits == 1 ? "" : "s")} sent as negative "
                + "quantities on the same invoice.", Routine: true));

        return new BimboTicket
        {
            // The model is intentionally not asked for a date. The operator types it into an empty
            // box, just like the invoice number, and this path has no value it could pre-fill with.
            Date = null,
            Total = items.Sum(l => l.ExtendedCost),
            Lines = items,
        };
    }

    private static void SettleBarcodes(
        IReadOnlyList<Row> rows, BimboBarcodeVerification verification,
        BimboBarcodeVerification cropVerification,
        Func<BimboUnknownField, string> ask)
    {
        var second = verification.Status == BimboVisionStatus.Success
            ? verification.Rows.ToDictionary(r => r.RowId, r => NormaliseUpc(r.Barcode), StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
        var third = cropVerification.Status == BimboVisionStatus.Success
            ? cropVerification.Rows.ToDictionary(r => r.RowId, r => NormaliseUpc(r.Barcode), StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            string? first = NormaliseUpc(row.Raw.Barcode);
            second.TryGetValue(row.RowId, out string? again);
            third.TryGetValue(row.RowId, out string? isolated);

            // Product families are not a catalogue. A prefix allow-list made a genuinely new Bimbo
            // item require a code change even when independent picture reads returned the same ten
            // printed digits. All three reads must agree: a two-to-one majority is deliberately not
            // enough because two full-page reads have repeated the same ambiguous 5/6 and 6/8 glyph.
            if (first is not null && first == again && first == isolated)
            {
                row.Upc10 = first;
                continue;
            }

            string typed = ask(new BimboUnknownField(
                row.Position, row.Count, row.Description, first ?? "", row.ItemNumber,
                LineField.Barcode, row.Raw.Barcode ?? "", row.Amount,
                row.LineNo, row.Page, row.Raw.PageUpsideDown));
            string digits = Upc.DigitsOnly(typed);
            if (digits.Length == 12 && digits[0] == '0' && Upc.IsValidUpcA12(digits))
                digits = digits[1..^1];
            if (digits.Length != 10)
                throw new EdiConversionException(PdfReasonCodes.BarcodeRequired,
                    $"The UPC for \"{row.Description}\" must be the ten digits printed on the Bimbo "
                    + "ticket, or its complete 12-digit UPC-A.");
            row.Upc10 = digits;
            row.Confirmed.Add(LineField.Barcode);
        }
    }

    private static void SettleUnitChecks(
        IReadOnlyList<Row> rows, BimboVisionReading reading, int ticketUnits,
        Func<BimboUnknownField, string> ask, List<ConversionWarning> warnings)
    {
        var affected = new HashSet<Row>();
        if (rows.Sum(r => r.SignedUnits) != ticketUnits)
            affected.UnionWith(rows);

        AddBandUnitMismatch(affected, rows, credit: false, reading.Deliveries);
        AddBandUnitMismatch(affected, rows, credit: true, reading.Returns);

        foreach (var row in affected.Where(r => !r.Confirmed.Contains(LineField.Units)))
            row.Units = AskUnits(row, ask);

        if (rows.Sum(r => r.SignedUnits) != ticketUnits)
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
                $"The operator-confirmed rows contain {rows.Sum(r => r.SignedUnits)} units while "
                + $"TICKET TOTALS prints {ticketUnits}. The confirmed rows were used; check the "
                + "ticket in SSCS."));
    }

    private static void AddBandUnitMismatch(
        HashSet<Row> affected, IReadOnlyList<Row> rows, bool credit, BimboVisionBandTotal? total)
    {
        if (total is null || !TrySignedInt(total.Units, out int printed)) return;
        int actual = rows.Where(r => r.Credit == credit).Sum(r => r.SignedUnits);
        int expected = credit ? -Math.Abs(printed) : Math.Abs(printed);
        if (actual != expected) affected.UnionWith(rows.Where(r => r.Credit == credit));
    }

    private static void SettleMoneyChecks(
        IReadOnlyList<Row> rows, BimboVisionReading reading, decimal ticketTotal,
        Func<BimboUnknownField, string> ask, List<ConversionWarning> warnings)
    {
        decimal tolerance = CentPerLine * Math.Max(1, rows.Count);
        decimal computed = rows.Sum(r => r.Extended);
        bool ticketMatches = Near(computed, ticketTotal, tolerance);
        bool cashMatches = TryMoney(reading.CashDue, allowSign: true, out decimal cash)
                           && Near(computed, cash, tolerance);
        bool hasBandChecks = HasReadableBandAmount(reading);
        bool bandsMatch = hasBandChecks && BandsMatch(rows, reading, tolerance);

        if (!ticketMatches && !cashMatches && !(hasBandChecks && bandsMatch))
        {
            // No printed total currently corroborates the XML-critical fields. Confirm the prices
            // in the affected invoice rather than refusing the delivery; quantities have their own
            // independent count check above.
            foreach (var row in rows.Where(r => !r.Confirmed.Contains(LineField.UnitPrice)))
                row.Price = AskMoney(row, LineField.UnitPrice, row.Raw.Price, ask);

            computed = rows.Sum(r => r.Extended);
            ticketMatches = Near(computed, ticketTotal, tolerance);
            cashMatches = TryMoney(reading.CashDue, allowSign: true, out cash)
                          && Near(computed, cash, tolerance);
            bandsMatch = BandsMatch(rows, reading, tolerance);
        }

        if (!ticketMatches || (hasBandChecks && !bandsMatch))
        {
            string checks = bandsMatch ? "the band totals"
                : cashMatches ? "CASH DUE" : "the operator-confirmed rows";
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The confirmed Bimbo rows total {computed:0.00}; TICKET TOTALS reads "
                + $"{ticketTotal:0.00}. {checks} support the confirmed rows, which were used. Check "
                + "the imported invoice in SSCS."));
        }
    }

    private static bool HasReadableBandAmount(BimboVisionReading reading) =>
        reading.Deliveries is not null
            && TryMoney(reading.Deliveries.Amount, allowSign: true, out _)
        || reading.Returns is not null
            && TryMoney(reading.Returns.Amount, allowSign: true, out _);

    private static bool BandsMatch(
        IReadOnlyList<Row> rows, BimboVisionReading reading, decimal tolerance)
    {
        bool any = false;
        foreach (var (credit, total) in new[] { (false, reading.Deliveries), (true, reading.Returns) })
        {
            if (total is null || !TryMoney(total.Amount, allowSign: true, out decimal printed))
                continue;
            any = true;
            decimal expected = credit ? -Math.Abs(printed) : Math.Abs(printed);
            decimal actual = rows.Where(r => r.Credit == credit).Sum(r => r.Extended);
            if (!Near(actual, expected, CentPerLine * Math.Max(1, rows.Count(r => r.Credit == credit))))
                return false;
        }
        return any;
    }

    private static int AskUnits(Row row, Func<BimboUnknownField, string> ask)
    {
        string typed = ask(Field(row, LineField.Units, row.Raw.Units));
        if (!TryUnits(typed, row.Credit, out int units))
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The QTY typed for \"{row.Description}\" was not a positive whole number.");
        row.Confirmed.Add(LineField.Units);
        return units;
    }

    private static decimal AskMoney(
        Row row, LineField field, string? printed, Func<BimboUnknownField, string> ask)
    {
        string typed = ask(Field(row, field, printed));
        if (!TryMoney(typed, allowSign: true, out decimal value) || value <= 0m)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The {(field == LineField.UnitPrice ? "PRICE" : "AMOUNT")} typed for "
                + $"\"{row.Description}\" was not a positive money value.");
        row.Confirmed.Add(field);
        return Math.Abs(value);
    }

    private static BimboUnknownField Field(Row row, LineField field, string? printed) => new(
        row.Position, row.Count, row.Description, row.Upc10, row.ItemNumber, field,
        printed ?? "", row.Amount, row.LineNo, row.Page, row.Raw.PageUpsideDown);

    private static bool RowMathDisagrees(Row row) =>
        row.Amount is null || !Near(Math.Abs(row.Extended), row.Amount.Value, CentPerLine);

    private static string? NormaliseUpc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(trimmed);
        return digits.Length == 10 ? digits : null;
    }

    private static bool TryUnits(string? raw, bool credit, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string text = raw.Trim();
        if (credit && text.StartsWith("-", StringComparison.Ordinal)) text = text[1..];
        if (!WholeNumber().IsMatch(text) || !int.TryParse(text, out value) || value <= 0)
            return false;
        return true;
    }

    private static bool TrySignedInt(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryMoney(string? raw, bool allowSign, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string text = raw.Trim();
        NumberStyles style = NumberStyles.AllowDecimalPoint;
        if (allowSign) style |= NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign;
        return decimal.TryParse(text, style, CultureInfo.InvariantCulture, out value);
    }

    internal static PdfTextLine? Locate(BimboVisionRow row, IReadOnlyList<PdfTextLine> lines)
    {
        var page = (row.Page > 0
            ? lines.Where(l => l.PageNumber == row.Page) : lines).ToList();

        // The same item can legitimately appear once under DELIVERIES and again under RETURNS.
        // PDF 24 does exactly that with item 008785. Looking across the whole page made the exact
        // item-number match ambiguous and the prompt fell back to an unmarked full page. Bimbo's
        // band-closing TOTAL rows are the stable separator (the decorative banners are not), so
        // narrow to the model's declared band before considering any row evidence.
        var totals = page.Where(l => BimboTotalRow().IsMatch(l.Text)).ToList();
        if (totals.Count > 0)
        {
            int deliveryEnd = totals[0].LineNumber;
            if (row.IsCredit == true)
            {
                int returnsEnd = totals.Count > 1 ? totals[1].LineNumber : int.MaxValue;
                page = page.Where(l => l.LineNumber > deliveryEnd && l.LineNumber < returnsEnd).ToList();
            }
            else if (row.IsCredit == false)
            {
                page = page.Where(l => l.LineNumber < deliveryEnd).ToList();
            }
        }

        string item = Upc.DigitsOnly(row.ItemNumber);
        if (item.Length > 0)
        {
            var byItem = page.Where(l => Upc.DigitsOnly(l.Text).Contains(item, StringComparison.Ordinal)).ToList();
            if (byItem.Count == 1) return byItem[0];
        }

        // The hidden OCR commonly changes a zero to O in the item number, and an amount such as
        // 12.04 is normally printed again in a band or ticket total. Either failure used to turn a
        // perfectly locatable product into an unmarked whole-page question. Score the other row
        // evidence together instead: exact UPC body, distinctive description words, amount and
        // price. These values are used only to find the photographed row; none is copied into the
        // invoice from OCR.
        string upc = Upc.DigitsOnly(row.Barcode ?? "");
        string? amountText = TryMoney(row.Amount, allowSign: true, out decimal amount)
            ? Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture) : null;
        string? priceText = TryMoney(row.Price, allowSign: true, out decimal price)
            ? Math.Abs(price).ToString("0.0000", CultureInfo.InvariantCulture) : null;
        var descriptionWords = LocatorWords(row.Description)
            .Where(w => w.Length >= 3 && !LocatorNoise.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var ranked = page.Select(line =>
        {
            string digits = Upc.DigitsOnly(line.Text);
            var words = LocatorWords(line.Text).ToHashSet(StringComparer.Ordinal);
            int descriptionMatches = descriptionWords.Count(words.Contains);
            int score = descriptionMatches * 2;
            if (upc.Length == 10 && digits.Contains(upc, StringComparison.Ordinal)) score += 7;
            if (amountText is not null && line.Text.Contains(amountText, StringComparison.Ordinal)) score += 2;
            if (priceText is not null && line.Text.Contains(priceText, StringComparison.Ordinal)) score += 1;
            return (Line: line, Score: score, DescriptionMatches: descriptionMatches);
        }).OrderByDescending(x => x.Score).ToList();

        if (ranked.Count == 0 || ranked[0].Score < 4) return null;
        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 2) return null;
        return ranked[0].Line;
    }

    private static string[] LocatorWords(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : LocatorWord().Matches(text.ToUpperInvariant()).Select(m => m.Value).ToArray();

    private static readonly HashSet<string> LocatorNoise = new(StringComparer.Ordinal)
    {
        // Brand and package abbreviations repeat on most of the ticket and therefore say nothing
        // about which row is meant. Product words such as PANQUE, NUE or PINGUINO do.
        "BIM", "MLA", "MB", "FC", "FS", "SS", "BX",
    };

    private static bool Near(decimal a, decimal b, decimal tolerance) => Math.Abs(a - b) <= tolerance;

    private static EdiConversionException UnreadableScan() => new(
        PdfReasonCodes.NoLineItems,
        "AI reached this Bimbo ticket but could not read all product rows, their delivery/return "
        + "direction, and the independent TICKET TOTALS checks after retrying. Re-scan the page so "
        + "the printed rows and totals are clear, then try again. OCR values were not substituted.");

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeNumber();

    [GeneratedRegex(@"[A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex LocatorWord();

    // TOTAL/TOTALS with the substitutions seen in Bimbo's hidden OCR (IOTALS, LOTAL, T0TAL).
    [GeneratedRegex(@"[TI1L][O0]TALS?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BimboTotalRow();
}
