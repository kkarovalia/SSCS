using System.Globalization;
using System.Text;

namespace EdiToSscs.Core;

/// <summary>Detected input file format.</summary>
public enum EdiFormat
{
    /// <summary>Standard fixed-width EDI: 76-byte B records with an 11-character code field.</summary>
    StandardEdi,
    /// <summary>EDI13: 78-byte B records with a 13-character code field.</summary>
    Edi13,
    /// <summary>EDI13 Ruby Red variant: 64-byte header, 79-byte B records ending in a fixed zero suffix.</summary>
    Edi13RubyRed,
    /// <summary>STRP/Alp-style standard EDI: 75-byte rows (76 minus the trailing pad) and an 8-digit header date.</summary>
    StandardEdi75,
}

/// <summary>Line-ending kind of a single input record.</summary>
public enum LineEnding { None, Lf, CrLf }

/// <summary>Stable reason codes used in errors, warnings and logs.</summary>
public static class ReasonCodes
{
    public const string EmptyFile = "EMPTY_FILE";
    public const string BinaryContent = "BINARY_CONTENT";
    public const string LoneCarriageReturn = "LONE_CARRIAGE_RETURN";
    public const string InvalidHeader = "INVALID_HEADER";
    public const string AlreadyConverted = "ALREADY_CONVERTED";
    public const string VendorTruncated = "VENDOR_TRUNCATED";
    public const string BlankLine = "BLANK_LINE";
    public const string UnsupportedRecordType = "UNSUPPORTED_RECORD_TYPE";
    public const string UnsupportedRecordLength = "UNSUPPORTED_RECORD_LENGTH";
    public const string UpcEInvalidCheckDigit = "UPCE_INVALID_CHECK_DIGIT";
    public const string UpcEUnsupportedNumberSystem = "UPCE_UNSUPPORTED_NUMBER_SYSTEM";
    public const string RubyRedSuffixMismatch = "RUBYRED_SUFFIX_MISMATCH";
    public const string BomRemoved = "UTF8_BOM_REMOVED";
    public const string NoInvoiceDate = "NO_INVOICE_DATE";
    public const string NonBarcodeCode = "NON_BARCODE_CODE";
    public const string CostFieldIsWholeLine = "COST_FIELD_IS_WHOLE_LINE";
    public const string TotalMismatch = "TOTAL_MISMATCH";
    public const string NoProductRecords = "NO_PRODUCT_RECORDS";
}

/// <summary>
/// A non-fatal conversion note attached to a specific record.
///
/// A note is written to the log and shown beside the file in the list. It never opens a window of
/// its own: the only thing that interrupts an operator is a question the converter cannot answer
/// itself, and anything it *can* answer is now asked about rather than noted — so by the time a
/// note is written there is nothing left for anyone to do about it.
/// </summary>
/// <param name="Routine">
/// True when this describes something normal about the invoice rather than something to go and look
/// at, which keeps it out of <see cref="ConversionResult.HasWarnings"/> and so off the file's status.
/// It is still logged and still listed.
///
/// Only one thing sets it today, and it is the reason the flag exists: "this ticket has returns"
/// fires on very nearly every Bimbo and Dr Pepper ticket, because very nearly every one of them
/// carries returns. Sixteen tickets converted in a row on 2026-08-12 and every single one that
/// succeeded came out amber — for that. A status that is always amber says nothing, and the tickets
/// that were amber for a real reason (a barcode changed, a line that may open a new item in SSCS)
/// looked exactly like the ones that were not.
/// </param>
public sealed record ConversionWarning(
    int LineNumber, string ReasonCode, string Message, bool Routine = false);

/// <summary>Result of a successful in-memory conversion.</summary>
public sealed class ConversionResult
{
    /// <summary>The NAXML document bytes to write out.</summary>
    public required byte[] OutputBytes { get; init; }
    /// <summary>The parsed invoice the XML was built from.</summary>
    public required Invoice Invoice { get; init; }
    public required EdiFormat Format { get; init; }
    public required int RecordCount { get; init; }
    public required int ProductRecordCount { get; init; }
    public required int UpcEExpandedCount { get; init; }
    /// <summary>C (charge) records seen and dropped. They are labels only and carry no money.</summary>
    public required int ChargeRecordCount { get; init; }
    public required bool BomRemoved { get; init; }
    public required long InputByteCount { get; init; }
    public required IReadOnlyList<ConversionWarning> Warnings { get; init; }
    public long OutputByteCount => OutputBytes.LongLength;
    /// <summary>Whether anything here wants a person. Routine notes do not — see <see cref="ConversionWarning.Routine"/>.</summary>
    public bool HasWarnings => Warnings.Any(w => !w.Routine);
}

/// <summary>Thrown when an input file cannot be converted. Carries a plain-language message.</summary>
public sealed class EdiConversionException : Exception
{
    public int? LineNumber { get; }
    public string ReasonCode { get; }
    /// <summary>Plain-language explanation suitable for a non-technical user.</summary>
    public string UserMessage { get; }

    public EdiConversionException(string reasonCode, string userMessage, int? lineNumber = null)
        : base(lineNumber is int n ? $"[{reasonCode}] line {n}: {userMessage}" : $"[{reasonCode}] {userMessage}")
    {
        ReasonCode = reasonCode;
        UserMessage = userMessage;
        LineNumber = lineNumber;
    }
}

/// <summary>
/// Reads a fixed-width EDI / EDI13 invoice and produces an NAXML invoice for SSCS.
///
/// ─────────────────────────────────────────────────────────────────────────────────────────────
/// B RECORD LAYOUT  (corrected 2026-07-25 — the earlier field names in the docs were wrong)
/// ─────────────────────────────────────────────────────────────────────────────────────────────
/// Every layout is the same record with a wider code field. With <c>base = 1 + codeWidth</c>
/// (codeWidth 11 for standard EDI, 13 for EDI13/Ruby Red):
///
///   [0]                      'B'
///   [1 .. base)              product code
///   [base+0  .. base+25)     description
///   [base+25 .. base+31)     vendor item # / SKU
///   [base+31 .. base+37)     cost of ONE CASE, in cents
///   [base+37 .. base+39)     two spaces
///   [base+39 .. base+45)     pack size — units in a case
///   [base+45 .. base+50)     number of cases  (may be negative on a credit line)
///   [base+50 .. base+55)     each-list / retail, in cents
///
/// So <c>units = pack × cases</c> and <c>unit cost = case cost ÷ pack</c>. The docs previously
/// called [base+31..base+37) the line's extended cost and [base+39..base+45) the quantity. That
/// is only true when a line happens to be one case. Proof it is wrong: reading FRITOLAY the old
/// way totals $42.60 against a header of $178.99; reading it as above gives exactly $178.99.
/// Same for PEPSI ($1,013.90 → $1,348.05) and GHRA ($4,774.86 → $10,032.86).
///
/// C (charge) records are dropped. FRITOLAY's <c>CSERVICE CHARGE</c> is a 29-byte label with no
/// amount in it, and that file's product lines already sum to its header total.
/// </summary>
public static class EdiConverter
{
    private const byte Cr = 0x0D;
    private const byte LfByte = 0x0A;
    private const byte Space = 0x20;

    /// <summary>Bytes from the code field to the end of the each-list field.</summary>
    private const int FieldsWidth = 55;

    private readonly record struct Line(int Offset, int Length, LineEnding Eol);

    /// <summary>Converts raw EDI/EDI13 bytes to an NAXML document. Throws <see cref="EdiConversionException"/> on invalid input.</summary>
    public static ConversionResult Convert(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int start = 0;
        bool bomRemoved = false;
        if (input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF)
        {
            start = 3;
            bomRemoved = true;
        }

        if (input.Length - start == 0)
            throw new EdiConversionException(ReasonCodes.EmptyFile, "The file is empty, so there is nothing to convert.");

        ScanForForbiddenBytes(input, start);
        var lines = SplitLines(input, start);

        var warnings = new List<ConversionWarning>();
        if (bomRemoved)
            warnings.Add(new ConversionWarning(1, ReasonCodes.BomRemoved, "A UTF-8 byte-order mark was removed from the start of the file."));

        var header = lines[0];
        if (header.Length == 0)
            throw new EdiConversionException(ReasonCodes.BlankLine, "Line 1 is blank. The file must start with a header record.", 1);

        if (LooksLikeConvertedOutput(input, header, lines))
            throw new EdiConversionException(
                ReasonCodes.AlreadyConverted,
                "This file has already been converted, so it does not need converting again. You can send it as-is.", 1);

        // ---- header ----
        EdiFormat format;
        string supplierName;
        string invoiceNumber;
        DateOnly? date;
        decimal total;

        if (IsRubyRedHeader(input, header))
        {
            format = EdiFormat.Edi13RubyRed;
            ReadOnlySpan<byte> h = input.AsSpan(header.Offset, header.Length);
            supplierName = Ascii(h[6..36]).Trim();      // 30-char company name
            invoiceNumber = Ascii(h[36..46]).Trim();    // 10-char reference, e.g. "IN-3738"
            date = ParseDate(Ascii(h[46..54]), DateShape.YyyyMmDd, 1, warnings);
            total = ParseSigned(Ascii(h[54..64])) / 100m;
        }
        else
        {
            (string vendorToken, int payloadOffset) = ParseVendorHeader(input, header);
            format = DetectVendorStyleFormat(input, header, lines, vendorToken);

            // An EDI13-style '13' suffix is part of the format marker, not the vendor's name.
            if (vendorToken.EndsWith("13", StringComparison.Ordinal) && vendorToken.Length > 2)
                vendorToken = vendorToken[..^2];

            // Keep the 6-character form the retired EDI output used. SSCS's "Map Supplier to Vendor"
            // screen is already keyed on those codes (GHRA, PEPSI, FRITOL, GSC …), so reusing them
            // means every existing mapping keeps resolving with nothing to set up.
            supplierName = vendorToken;
            if (supplierName.Length > 6)
            {
                supplierName = supplierName[..6];
                warnings.Add(new ConversionWarning(1, ReasonCodes.VendorTruncated,
                    $"The vendor code '{vendorToken}' was shortened to '{supplierName}', matching how this vendor is already named in SSCS."));
            }

            (invoiceNumber, date, total) = ParseVendorPayload(input, header, payloadOffset, warnings);
        }

        // ---- records ----
        int codeWidth = format is EdiFormat.Edi13 or EdiFormat.Edi13RubyRed ? 13 : 11;
        int expectedBWidth = format switch
        {
            EdiFormat.StandardEdi => 76,
            EdiFormat.StandardEdi75 => 75,
            EdiFormat.Edi13 => 78,
            _ => 79,
        };

        var raws = new List<RawLine>();
        int expanded = 0, charges = 0;

        for (int li = 1; li < lines.Count; li++)
        {
            var line = lines[li];
            int lineNumber = li + 1;

            // A trailing blank final line is just the file's last newline, not a record.
            if (line.Length == 0)
            {
                if (li == lines.Count - 1) continue;
                throw new EdiConversionException(ReasonCodes.BlankLine,
                    $"Line {lineNumber} is blank. Blank lines are not allowed inside an EDI file.", lineNumber);
            }

            byte type = input[line.Offset];

            if (type == (byte)'C')
            {
                // Charge records are labels with no amount; the product lines already carry the
                // whole invoice total. Dropping one loses no money, and almost every vendor file
                // has one, so this is counted rather than warned about — a warning that fires on
                // every conversion just teaches people to ignore warnings.
                charges++;
                continue;
            }

            if (type != (byte)'B')
                throw new EdiConversionException(
                    ReasonCodes.UnsupportedRecordType,
                    $"Line {lineNumber} starts with an unsupported record type '{(char)type}'. Only header, B and C records are supported.",
                    lineNumber);

            if (line.Length != expectedBWidth)
                throw new EdiConversionException(
                    ReasonCodes.UnsupportedRecordLength,
                    $"Line {lineNumber} has an unsupported record length of {line.Length} bytes (expected {expectedBWidth}).",
                    lineNumber);

            if (format == EdiFormat.Edi13RubyRed && !input.AsSpan(line.Offset + 69, 10).SequenceEqual("00000000  "u8))
                throw new EdiConversionException(
                    ReasonCodes.RubyRedSuffixMismatch,
                    $"Line {lineNumber} does not end with the expected Ruby Red zero suffix.",
                    lineNumber);

            raws.Add(ReadProductRecord(input, line, codeWidth, lineNumber, warnings, ref expanded));
        }

        if (raws.Count == 0)
            throw new EdiConversionException(ReasonCodes.NoProductRecords,
                "This file has a header but no product lines, so there is nothing to send to SSCS.");

        var invoice = new Invoice
        {
            SupplierName = supplierName,
            InvoiceNumber = invoiceNumber,
            Date = date,
            Total = total,
            Lines = InterpretMoney(raws, total, warnings),
        };

        WarnIfTotalDisagrees(invoice, warnings);

        return new ConversionResult
        {
            OutputBytes = NaxmlBuilder.Build(invoice),
            Invoice = invoice,
            Format = format,
            RecordCount = lines.Count,
            ProductRecordCount = invoice.Lines.Count,
            UpcEExpandedCount = expanded,
            ChargeRecordCount = charges,
            BomRemoved = bomRemoved,
            InputByteCount = input.LongLength,
            Warnings = warnings,
        };
    }

    // ---------------------------------------------------------------- records

    /// <summary>A product row's fields before the money is interpreted.</summary>
    private readonly record struct RawLine(string Gtin, string Description, long CostCents, long Pack, long Cases, long EaListCents)
    {
        /// <summary>Pack size zero is malformed; treat such a row as one unit per case.</summary>
        public long Units => (Pack > 0 ? Pack : 1) * Cases;
    }

    /// <summary>What the cost field on a row means. Nearly every vendor writes the first.</summary>
    private enum CostMeaning { PerCase, WholeLine }

    private static RawLine ReadProductRecord(
        byte[] input, Line line, int codeWidth, int lineNumber,
        List<ConversionWarning> warnings, ref int expanded)
    {
        int b = line.Offset;
        int fieldBase = 1 + codeWidth;
        if (line.Length < fieldBase + FieldsWidth)
            throw new EdiConversionException(ReasonCodes.UnsupportedRecordLength,
                $"Line {lineNumber} is too short to contain the product fields.", lineNumber);

        return new RawLine(
            Gtin: ToGtin(Ascii(input.AsSpan(b + 1, codeWidth)), lineNumber, warnings, ref expanded),
            Description: Ascii(input.AsSpan(b + fieldBase, 25)).Trim(),
            CostCents: ParseSigned(Ascii(input.AsSpan(b + fieldBase + 31, 6))),
            Pack: ParseSigned(Ascii(input.AsSpan(b + fieldBase + 39, 6))),
            Cases: ParseSigned(Ascii(input.AsSpan(b + fieldBase + 45, 5))),
            EaListCents: ParseSigned(Ascii(input.AsSpan(b + fieldBase + 50, 5))));
    }

    private static InvoiceLine BuildLine(RawLine raw, CostMeaning meaning)
    {
        long units = raw.Units;
        decimal divisor = meaning == CostMeaning.PerCase
            ? (raw.Pack > 0 ? raw.Pack : 1)
            : (units != 0 ? units : 1);

        return new InvoiceLine
        {
            Gtin = raw.Gtin,
            Description = raw.Description,
            Units = checked((int)units),
            UnitCost = decimal.Round(raw.CostCents / 100m / divisor, 4, MidpointRounding.AwayFromZero),
            RetailPrice = raw.EaListCents / 100m,
        };
    }

    /// <summary>
    /// Chooses how to read the cost field, using the invoice's own printed total as the checksum.
    ///
    /// Every vendor file we have writes a per-case cost, so that is tried first. One hidden GHRA
    /// credit (810738) instead writes each line's whole extended amount there — its lines only add
    /// up to the printed -262.11 when read that way, and doing so also yields clean per-unit prices
    /// ($4.54, $4.85, $5.34) where the per-case reading gives none.
    ///
    /// The fallback is only ever taken when it reconciles the invoice **exactly**, so it cannot
    /// quietly turn a file that already balanced into a different one.
    /// </summary>
    private static List<InvoiceLine> InterpretMoney(List<RawLine> raws, decimal invoiceTotal, List<ConversionWarning> warnings)
    {
        var perCase = raws.Select(r => BuildLine(r, CostMeaning.PerCase)).ToList();
        if (perCase.Sum(l => l.ExtendedCost) == invoiceTotal)
            return perCase;

        var wholeLine = raws.Select(r => BuildLine(r, CostMeaning.WholeLine)).ToList();
        if (wholeLine.Sum(l => l.ExtendedCost) == invoiceTotal)
        {
            warnings.Add(new ConversionWarning(1, ReasonCodes.CostFieldIsWholeLine,
                "This file writes each line's total cost where most vendors write the price of one case. " +
                "Read that way the lines match the invoice total exactly, so that is how it was converted."));
            return wholeLine;
        }

        return perCase;
    }

    /// <summary>
    /// Turns a fixed-width code field into the full barcode to put in the XML.
    ///
    /// The governing constraint: SSCS stores whatever we send minus its last character. The retired
    /// EDI output's code field WAS the stored ID, so to land on the same item we send that content
    /// plus exactly one more character — the real check digit when the code is a barcode body, so
    /// the result is a scannable UPC, otherwise a filler '0'.
    ///
    /// The one deliberate difference: a 13-digit EAN-13 in an EDI13 field goes out whole, so SSCS
    /// keeps 12 characters of it. The old 11-character field truncated those into collisions — the
    /// bug that started this rewrite.
    /// </summary>
    private static string ToGtin(string codeField, int lineNumber, List<ConversionWarning> warnings, ref int expanded)
    {
        string content = codeField.Trim();

        if (content.Length == 0)
        {
            warnings.Add(new ConversionWarning(lineNumber, ReasonCodes.NonBarcodeCode,
                $"Line {lineNumber} has no product code."));
            return "";
        }

        // 8 digits is a short (UPC-E) barcode: expand it to the full UPC-A body first.
        if (content.Length == 8 && content.All(char.IsAsciiDigit))
        {
            char ns = content[0];
            if (ns is not ('0' or '1'))
            {
                warnings.Add(new ConversionWarning(lineNumber, ReasonCodes.UpcEUnsupportedNumberSystem,
                    $"Line {lineNumber}: short barcode '{content}' starts with '{ns}', which is not a valid UPC-E number system. It was sent through unchanged — please check this item in SSCS."));
                return content;
            }

            string body = UpcE.Expand(content[..7]);
            char check = UpcE.ComputeUpcACheckDigit(body);
            if (check != content[7])
                throw new EdiConversionException(
                    ReasonCodes.UpcEInvalidCheckDigit,
                    $"Line {lineNumber} contains the short barcode '{content}', whose check digit is wrong (expected {check}). " +
                    "The barcode may be mistyped, so the file was not converted.",
                    lineNumber);

            expanded++;
            return body + check;
        }

        // 12 or 13 characters is already a whole barcode — send it as it stands.
        if (content.Length >= 12)
            return content;

        // 11 digits is a UPC-A body with its check digit already removed: put it back.
        if (content.Length == 11 && content.All(char.IsAsciiDigit))
            return Upc.AppendCheckDigit(content);

        // Anything else is a store PLU or vendor code rather than a barcode. Pad it by one character
        // so SSCS's drop returns the original code. No warning: the id SSCS ends up storing is
        // exactly the one the retired output wrote, so there is nothing for anyone to act on.
        return content + "0";
    }

    // ---------------------------------------------------------------- header

    private static EdiFormat DetectVendorStyleFormat(byte[] input, Line header, List<Line> lines, string vendorToken)
    {
        int firstBWidth = FirstProductRecordWidth(input, lines);
        if (firstBWidth == 78) return EdiFormat.Edi13;
        if (firstBWidth == 76) return EdiFormat.StandardEdi;
        if (firstBWidth == 75 && IsStrpEightDigitDateHeader(input, header)) return EdiFormat.StandardEdi75;
        if (firstBWidth < 0)
            return vendorToken.EndsWith("13", StringComparison.Ordinal) ? EdiFormat.Edi13 : EdiFormat.StandardEdi;

        for (int i = 1; i < lines.Count; i++)
            if (lines[i].Length > 0 && input[lines[i].Offset] == (byte)'B')
                throw new EdiConversionException(
                    ReasonCodes.UnsupportedRecordLength,
                    $"Line {i + 1} has an unsupported record length of {lines[i].Length} bytes (expected 76 for EDI or 78 for EDI13).",
                    i + 1);
        throw new InvalidOperationException("unreachable");
    }

    private enum DateShape { MmDdYy, YyMmDd, MmDdYyyy, YyyyMmDd }

    /// <summary>
    /// Splits a vendor-style header payload into invoice number, date and total. The payload is
    /// always a 10-digit invoice number, then an optional date, then a 10-digit amount:
    ///   26 bytes → 6-digit date   20 bytes → no date at all (PEPSI)   28 bytes → 8-digit date (STRP).
    /// </summary>
    private static (string Invoice, DateOnly? Date, decimal Total) ParseVendorPayload(
        byte[] input, Line header, int payloadOffset, List<ConversionWarning> warnings)
    {
        string payload = Ascii(input.AsSpan(header.Offset + payloadOffset, header.Length - payloadOffset));
        if (payload.Length < 20)
            throw InvalidHeader(1);

        string invoiceNumber = payload[..10];
        string amount = payload[^10..];
        string middle = payload[10..^10];

        DateOnly? date = middle.Length switch
        {
            0 => NoDate(warnings),
            // Six digits are MMDDYY unless the first pair is over 12, which cannot be a month.
            6 => ParseDate(middle, ParseSigned(middle.AsSpan(0, 2)) > 12 ? DateShape.YyMmDd : DateShape.MmDdYy, 1, warnings),
            8 => ParseDate(middle, DateShape.MmDdYyyy, 1, warnings),
            _ => UnknownDate(middle, warnings),
        };

        return (invoiceNumber, date, ParseSigned(amount) / 100m);
    }

    private static DateOnly? NoDate(List<ConversionWarning> warnings)
    {
        warnings.Add(new ConversionWarning(1, ReasonCodes.NoInvoiceDate,
            "This vendor's file does not include an invoice date, so SSCS will date the invoice on the day it is imported."));
        return null;
    }

    private static DateOnly? UnknownDate(string middle, List<ConversionWarning> warnings)
    {
        warnings.Add(new ConversionWarning(1, ReasonCodes.NoInvoiceDate,
            $"The header contains '{middle}' where a date was expected and it could not be read, so SSCS will date the invoice on the day it is imported."));
        return null;
    }

    private static DateOnly? ParseDate(string digits, DateShape shape, int lineNumber, List<ConversionWarning> warnings)
    {
        if (!digits.All(char.IsAsciiDigit))
            return UnknownDate(digits, warnings);

        int y, m, d;
        switch (shape)
        {
            case DateShape.MmDdYy when digits.Length == 6:
                m = Two(digits, 0); d = Two(digits, 2); y = 2000 + Two(digits, 4); break;
            case DateShape.YyMmDd when digits.Length == 6:
                y = 2000 + Two(digits, 0); m = Two(digits, 2); d = Two(digits, 4); break;
            case DateShape.MmDdYyyy when digits.Length == 8:
                m = Two(digits, 0); d = Two(digits, 2); y = int.Parse(digits[4..8], CultureInfo.InvariantCulture); break;
            case DateShape.YyyyMmDd when digits.Length == 8:
                y = int.Parse(digits[..4], CultureInfo.InvariantCulture); m = Two(digits, 4); d = Two(digits, 6); break;
            default:
                return UnknownDate(digits, warnings);
        }

        if (y is < 1900 or > 2999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
            return UnknownDate(digits, warnings);

        return new DateOnly(y, m, d);
    }

    private static int Two(string s, int i) => (s[i] - '0') * 10 + (s[i + 1] - '0');

    /// <summary>
    /// Notes when the lines do not add up to the printed total. Not an error: a case cost that does
    /// not divide evenly into its pack can leave a cent behind, and the operator fixes that in SSCS.
    /// </summary>
    private static void WarnIfTotalDisagrees(Invoice invoice, List<ConversionWarning> warnings)
    {
        decimal diff = invoice.LineTotal - invoice.Total;
        if (diff == 0) return;
        warnings.Add(new ConversionWarning(1, ReasonCodes.TotalMismatch,
            $"The lines add up to {invoice.LineTotal:0.00} but the invoice total is {invoice.Total:0.00}, " +
            $"a difference of {Math.Abs(diff):0.00}. This is normally a rounding cent — check it in SSCS after importing."));
    }

    // ---------------------------------------------------------------- parsing helpers

    /// <summary>
    /// Reads a fixed-width numeric field. Handles the negative form these files use, where the
    /// minus sits inside the zero padding (e.g. "0000-22000" is -22000, "000-1" is -1).
    /// </summary>
    internal static long ParseSigned(ReadOnlySpan<char> field)
    {
        bool negative = false;
        long value = 0;
        foreach (char c in field)
        {
            if (c == '-') negative = true;
            else if (char.IsAsciiDigit(c)) value = value * 10 + (c - '0');
        }
        return negative ? -value : value;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

    private static void ScanForForbiddenBytes(byte[] input, int start)
    {
        int lineNo = 1;
        for (int i = start; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == Cr)
            {
                if (i + 1 >= input.Length || input[i + 1] != LfByte)
                    throw new EdiConversionException(
                        ReasonCodes.LoneCarriageReturn,
                        $"Line {lineNo} contains a carriage-return character without a line feed. The file appears damaged.",
                        lineNo);
                continue;
            }
            if (b == LfByte) { lineNo++; continue; }
            if (b == 0x00)
                throw new EdiConversionException(
                    ReasonCodes.BinaryContent,
                    $"Line {lineNo} contains a null byte. This does not look like a text EDI file.",
                    lineNo);
            if (b < 0x20)
                throw new EdiConversionException(
                    ReasonCodes.BinaryContent,
                    $"Line {lineNo} contains a control character (0x{b:X2}). This does not look like a text EDI file.",
                    lineNo);
        }
    }

    private static List<Line> SplitLines(byte[] input, int start)
    {
        var lines = new List<Line>();
        int pos = start;
        while (pos < input.Length)
        {
            int nl = Array.IndexOf(input, LfByte, pos);
            if (nl < 0)
            {
                lines.Add(new Line(pos, input.Length - pos, LineEnding.None));
                break;
            }
            int end = nl;
            var eol = LineEnding.Lf;
            if (end > pos && input[end - 1] == Cr) { end--; eol = LineEnding.CrLf; }
            lines.Add(new Line(pos, end - pos, eol));
            pos = nl + 1;
        }
        return lines;
    }

    private static bool IsRubyRedHeader(byte[] input, Line header)
    {
        // 64 bytes: 'GHRA13' + 30-char company name + 10-char reference + 8-digit date + 10-digit amount.
        if (header.Length != 64) return false;
        ReadOnlySpan<byte> h = input.AsSpan(header.Offset, header.Length);
        if (!h[..6].SequenceEqual("GHRA13"u8)) return false;
        if (h[6] == Space) return false;
        for (int i = 46; i < 54; i++)
            if (!IsDigit(h[i])) return false;
        for (int i = 54; i < 64; i++)
            if (!IsDigit(h[i]) && h[i] != (byte)'-') return false;
        return true;
    }

    /// <summary>
    /// Detects a file that is already output rather than input, so the user gets a plain
    /// "already converted" message. Covers both the XML this converter now writes and the
    /// fixed-width files it used to write, since both may be sitting in a user's folder.
    /// </summary>
    private static bool LooksLikeConvertedOutput(byte[] input, Line header, List<Line> lines)
    {
        ReadOnlySpan<byte> h = input.AsSpan(header.Offset, header.Length);
        if (h.StartsWith("<?xml"u8) || h.StartsWith("<NAXML"u8)) return true;

        if (input[header.Offset] != (byte)'A')
            return false;

        // Retired fixed-width output: a 67-byte B record, or a 59-byte header whose company region
        // holds text where any EDI input would have an all-digit payload.
        if (FirstProductRecordWidth(input, lines) == 67) return true;
        if (header.Length == 59)
        {
            for (int i = 7; i < 41; i++)
            {
                byte b = h[i];
                bool isLetter = (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');
                if (b == Space || isLetter) return true;
            }
        }
        return false;
    }

    private static (string VendorToken, int PayloadOffset) ParseVendorHeader(byte[] input, Line header)
    {
        // [0..6] = vendor token left-aligned and space padded, [7..] numeric payload.
        if (header.Length < 8)
            throw InvalidHeader(1);
        ReadOnlySpan<byte> h = input.AsSpan(header.Offset, header.Length);

        int tokenLen = 0;
        while (tokenLen < 7 && h[tokenLen] != Space) tokenLen++;
        if (tokenLen == 0)
            throw InvalidHeader(1);
        for (int i = 0; i < tokenLen; i++)
            if (!IsUpperAlnum(h[i]))
                throw InvalidHeader(1);
        for (int i = tokenLen; i < 7; i++)
            if (h[i] != Space)
                throw InvalidHeader(1);
        for (int i = 7; i < h.Length; i++)
            if (!IsDigit(h[i]) && h[i] != (byte)'-')
                throw InvalidHeader(1);

        return (Ascii(h[..tokenLen]), 7);
    }

    private static EdiConversionException InvalidHeader(int lineNumber) => new(
        ReasonCodes.InvalidHeader,
        "Line 1 is not a recognized EDI header record. This file does not look like a supported EDI or EDI13 invoice.",
        lineNumber);

    /// <summary>STRP/Alp header shape: payload is invoice(10) + an 8-digit MMDDYYYY date + amount(10).</summary>
    private static bool IsStrpEightDigitDateHeader(byte[] input, Line header)
    {
        if (header.Length - 7 != 28) return false;
        ReadOnlySpan<byte> h = input.AsSpan(header.Offset, header.Length);
        int d = 7 + 10;
        for (int i = 0; i < 8; i++)
            if (!IsDigit(h[d + i])) return false;
        int mm = (h[d] - '0') * 10 + (h[d + 1] - '0');
        int dd = (h[d + 2] - '0') * 10 + (h[d + 3] - '0');
        return mm is >= 1 and <= 12 && dd is >= 1 and <= 31;
    }

    private static int FirstProductRecordWidth(byte[] input, List<Line> lines)
    {
        for (int i = 1; i < lines.Count; i++)
            if (lines[i].Length > 0 && input[lines[i].Offset] == (byte)'B')
                return lines[i].Length;
        return -1;
    }

    private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

    private static bool IsUpperAlnum(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9');
}
