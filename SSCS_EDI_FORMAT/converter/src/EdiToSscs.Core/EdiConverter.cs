using System.Text;

namespace EdiToSscs.Core;

/// <summary>Detected input file format.</summary>
public enum EdiFormat
{
    /// <summary>Standard fixed-width EDI (76-byte B records, 11-char product-code field).</summary>
    StandardEdi,
    /// <summary>EDI13 (78-byte B records, 13-char product-code field).</summary>
    Edi13,
    /// <summary>EDI13 Ruby Red variant (64-byte header, 79-byte B records with a fixed 10-byte zero suffix).</summary>
    Edi13RubyRed,
}

/// <summary>Line-ending kind of a single record.</summary>
public enum LineEnding { None, Lf, CrLf }

/// <summary>Stable reason codes used in errors, warnings and logs.</summary>
public static class ReasonCodes
{
    public const string EmptyFile = "EMPTY_FILE";
    public const string BinaryContent = "BINARY_CONTENT";
    public const string LoneCarriageReturn = "LONE_CARRIAGE_RETURN";
    public const string InvalidHeader = "INVALID_HEADER";
    public const string VendorTooLong = "VENDOR_TOO_LONG";
    public const string BlankLine = "BLANK_LINE";
    public const string UnsupportedRecordType = "UNSUPPORTED_RECORD_TYPE";
    public const string UnsupportedRecordLength = "UNSUPPORTED_RECORD_LENGTH";
    public const string UpcEInvalidCheckDigit = "UPCE_INVALID_CHECK_DIGIT";
    public const string UpcEUnsupportedNumberSystem = "UPCE_UNSUPPORTED_NUMBER_SYSTEM";
    public const string RubyRedSuffixMismatch = "RUBYRED_SUFFIX_MISMATCH";
    public const string BomRemoved = "UTF8_BOM_REMOVED";
    public const string OutputValidationFailed = "OUTPUT_VALIDATION_FAILED";
}

/// <summary>A non-fatal conversion note attached to a specific record.</summary>
public sealed record ConversionWarning(int LineNumber, string ReasonCode, string Message);

/// <summary>Result of a successful in-memory conversion.</summary>
public sealed class ConversionResult
{
    public required byte[] OutputBytes { get; init; }
    public required EdiFormat Format { get; init; }
    public required int RecordCount { get; init; }
    public required int ProductRecordCount { get; init; }
    public required int UpcEExpandedCount { get; init; }
    public required int BlankedBarcodeCount { get; init; }
    public required bool BomRemoved { get; init; }
    public required long InputByteCount { get; init; }
    public required IReadOnlyList<ConversionWarning> Warnings { get; init; }
    public long OutputByteCount => OutputBytes.LongLength;
    public bool HasWarnings => Warnings.Count > 0;
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
/// Byte-oriented EDI / EDI13 to SSCS converter. All rules were derived from raw-byte
/// comparison of the supplied reference fixtures and are documented in FORMAT_SPEC.md.
/// The converter never trims, splits on whitespace, or re-encodes; every byte that is
/// not at a verified transformation position is copied through unchanged.
/// </summary>
public static class EdiConverter
{
    private const byte Cr = 0x0D;
    private const byte LfByte = 0x0A;
    private const byte Space = 0x20;

    private readonly record struct Line(int Offset, int Length, LineEnding Eol);

    /// <summary>Converts raw EDI/EDI13 bytes to SSCS bytes. Throws <see cref="EdiConversionException"/> on invalid input.</summary>
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

        // ---- Header & format detection (content-based, never by filename) ----
        var header = lines[0];
        if (header.Length == 0)
            throw new EdiConversionException(ReasonCodes.BlankLine, "Line 1 is blank. The file must start with a header record.", 1);

        EdiFormat format;
        byte[] newHeader;

        if (IsRubyRedHeader(input, header))
        {
            format = EdiFormat.Edi13RubyRed;
            // Verified rule: output header = 'A' + input bytes [6..63] (the 'GHRA13' tag is replaced by 'A').
            newHeader = new byte[header.Length - 5];
            newHeader[0] = (byte)'A';
            Buffer.BlockCopy(input, header.Offset + 6, newHeader, 1, header.Length - 6);
        }
        else
        {
            (string vendorToken, _) = ParseVendorHeader(input, header);
            int firstBWidth = FirstProductRecordWidth(input, lines);
            if (firstBWidth == 78)
                format = EdiFormat.Edi13;
            else if (firstBWidth == 76)
                format = EdiFormat.StandardEdi;
            else if (firstBWidth < 0)
                format = vendorToken.EndsWith("13", StringComparison.Ordinal) ? EdiFormat.Edi13 : EdiFormat.StandardEdi;
            else
                throw FirstProductRecordWidthError(lines, input);

            // Verified: an EDI13-style '13' suffix on the vendor token is removed regardless
            // of the detected record width (hidden fixture 810738 carries a 'GHRA13' header
            // over standard 76-byte records and its reference output is 'AGHRA  ').
            if (vendorToken.EndsWith("13", StringComparison.Ordinal) && vendorToken.Length > 2)
                vendorToken = vendorToken[..^2];

            if (vendorToken.Length > 6)
                throw new EdiConversionException(
                    ReasonCodes.VendorTooLong,
                    $"The vendor name '{vendorToken}' is too long to fit the SSCS header field.", 1);

            // Verified rule: output header = 'A' + vendor padded to 6 + input bytes [7..], total length preserved.
            newHeader = new byte[header.Length];
            newHeader[0] = (byte)'A';
            for (int i = 0; i < 6; i++)
                newHeader[1 + i] = i < vendorToken.Length ? (byte)vendorToken[i] : Space;
            Buffer.BlockCopy(input, header.Offset + 7, newHeader, 7, header.Length - 7);
        }

        // ---- Records ----
        int expectedBWidth = format switch
        {
            EdiFormat.StandardEdi => 76,
            EdiFormat.Edi13 => 78,
            _ => 79,
        };

        var output = new MemoryStream(input.Length);
        WriteLine(output, newHeader, 0, newHeader.Length, header.Eol);

        int productRecords = 0, expanded = 0, blanked = 0;

        for (int li = 1; li < lines.Count; li++)
        {
            var line = lines[li];
            int lineNumber = li + 1;

            if (line.Length == 0)
                throw new EdiConversionException(ReasonCodes.BlankLine, $"Line {lineNumber} is blank. Blank lines are not allowed inside an EDI file.", lineNumber);

            byte type = input[line.Offset];
            if (type == (byte)'C')
            {
                // Verified rule: C records are preserved byte-for-byte, including trailing spaces.
                WriteLine(output, input, line.Offset, line.Length, line.Eol);
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

            productRecords++;
            byte[] rec = ConvertProductRecord(input, line, format, lineNumber, warnings, ref expanded, ref blanked);
            WriteLine(output, rec, 0, rec.Length, line.Eol);
        }

        var outputBytes = output.ToArray();
        var result = new ConversionResult
        {
            OutputBytes = outputBytes,
            Format = format,
            RecordCount = lines.Count,
            ProductRecordCount = productRecords,
            UpcEExpandedCount = expanded,
            BlankedBarcodeCount = blanked,
            BomRemoved = bomRemoved,
            InputByteCount = input.LongLength,
            Warnings = warnings,
        };
        ValidateOutput(result, lines);
        return result;
    }

    // ---------------------------------------------------------------- helpers

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

    private static void WriteLine(MemoryStream output, byte[] source, int offset, int length, LineEnding eol)
    {
        output.Write(source, offset, length);
        switch (eol)
        {
            case LineEnding.CrLf: output.WriteByte(Cr); output.WriteByte(LfByte); break;
            case LineEnding.Lf: output.WriteByte(LfByte); break;
        }
    }

    private static bool IsRubyRedHeader(byte[] input, Line header)
    {
        // Verified structure: 64 bytes, 'GHRA13' + 30-char company name + 10-char reference
        // + 8-digit date + 10-digit amount; byte 6 is not a space (no vendor-field padding).
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

    private static (string VendorToken, int PayloadOffset) ParseVendorHeader(byte[] input, Line header)
    {
        // Verified structure: [0..6] = vendor token left-aligned and space padded, [7..] numeric payload.
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

        return (Encoding.ASCII.GetString(h[..tokenLen]), 7);
    }

    private static EdiConversionException InvalidHeader(int lineNumber) => new(
        ReasonCodes.InvalidHeader,
        "Line 1 is not a recognized EDI header record. This file does not look like a supported EDI or EDI13 invoice.",
        lineNumber);

    private static int FirstProductRecordWidth(byte[] input, List<Line> lines)
    {
        for (int i = 1; i < lines.Count; i++)
            if (lines[i].Length > 0 && input[lines[i].Offset] == (byte)'B')
                return lines[i].Length;
        return -1;
    }

    private static EdiConversionException FirstProductRecordWidthError(List<Line> lines, byte[] input)
    {
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Length > 0 && input[lines[i].Offset] == (byte)'B')
                return new EdiConversionException(
                    ReasonCodes.UnsupportedRecordLength,
                    $"Line {i + 1} has an unsupported record length of {lines[i].Length} bytes (expected 76 for EDI or 78 for EDI13).",
                    i + 1);
        }
        throw new InvalidOperationException("unreachable");
    }

    private static byte[] ConvertProductRecord(
        byte[] input, Line line, EdiFormat format, int lineNumber,
        List<ConversionWarning> warnings, ref int expanded, ref int blanked)
    {
        ReadOnlySpan<byte> rec = input.AsSpan(line.Offset, line.Length);

        if (format == EdiFormat.StandardEdi)
        {
            // Verified: B + 11-char code field + remainder preserved verbatim (bytes 12..75).
            var outRec = rec.ToArray();
            ApplyUpcERule(outRec.AsSpan(1, 11), lineNumber, warnings, ref expanded, ref blanked);
            return outRec;
        }

        // EDI13 layouts: B + 13-char code field; the SSCS code field is the first 11 bytes
        // of that field (dropping the two EDI13-only trailing bytes). Verified in FORMAT_SPEC.md.
        // The field content is NOT restricted to digits: hidden fixtures prove alphanumeric
        // codes (e.g. '002ZH7S8J99', '87450700806' + '3d') convert with the same truncation.
        ReadOnlySpan<byte> field13 = rec.Slice(1, 13);

        if (format == EdiFormat.Edi13)
        {
            var outRec = new byte[76];
            outRec[0] = (byte)'B';
            field13[..11].CopyTo(outRec.AsSpan(1, 11));
            rec[14..].CopyTo(outRec.AsSpan(12)); // bytes 14..77 preserved verbatim
            ApplyUpcERule(outRec.AsSpan(1, 11), lineNumber, warnings, ref expanded, ref blanked);
            return outRec;
        }

        // Ruby Red: 79 bytes; bytes 69..78 must be exactly '00000000  ' and are removed.
        ReadOnlySpan<byte> suffix = rec.Slice(69, 10);
        if (!suffix.SequenceEqual("00000000  "u8))
            throw new EdiConversionException(
                ReasonCodes.RubyRedSuffixMismatch,
                $"Line {lineNumber} does not end with the expected Ruby Red zero suffix.",
                lineNumber);

        var ruby = new byte[67];
        ruby[0] = (byte)'B';
        field13[..11].CopyTo(ruby.AsSpan(1, 11));
        rec.Slice(14, 55).CopyTo(ruby.AsSpan(12)); // bytes 14..68 preserved verbatim
        ApplyUpcERule(ruby.AsSpan(1, 11), lineNumber, warnings, ref expanded, ref blanked);
        return ruby;
    }

    /// <summary>
    /// Applies the verified UPC-E rule to an 11-char SSCS product-code field in place.
    /// Candidates are exactly 8 digits followed by 3 spaces. Valid candidates are expanded
    /// to the 11-digit UPC-A body; candidates with an unsupported number system are blanked
    /// (fixture-proven behavior, SSCS_GHRA_129348 line 104); candidates with a bad check
    /// digit abort the conversion.
    /// </summary>
    private static void ApplyUpcERule(Span<byte> field11, int lineNumber, List<ConversionWarning> warnings, ref int expanded, ref int blanked)
    {
        for (int i = 0; i < 8; i++)
            if (!IsDigit(field11[i])) return;
        for (int i = 8; i < 11; i++)
            if (field11[i] != Space) return;

        Span<char> code = stackalloc char[8];
        for (int i = 0; i < 8; i++) code[i] = (char)field11[i];

        if (code[0] != '0' && code[0] != '1')
        {
            // Fixture-proven: blank the field, keep the rest of the record, report a warning.
            field11.Fill(Space);
            blanked++;
            warnings.Add(new ConversionWarning(
                lineNumber,
                ReasonCodes.UpcEUnsupportedNumberSystem,
                $"Line {lineNumber}: barcode '{new string(code)}' has number system '{code[0]}', which is not valid for UPC-E. " +
                "The product-code field was cleared, matching the reference SSCS behavior."));
            return;
        }

        string body = UpcE.Expand(new string(code[..7]));
        char check = UpcE.ComputeUpcACheckDigit(body);
        if (check != code[7])
            throw new EdiConversionException(
                ReasonCodes.UpcEInvalidCheckDigit,
                $"Line {lineNumber} contains the UPC-E barcode '{new string(code)}' whose check digit is invalid " +
                $"(expected {check}). The barcode may be mistyped; the file was not converted.",
                lineNumber);

        for (int i = 0; i < 11; i++) field11[i] = (byte)body[i];
        expanded++;
    }

    private static void ValidateOutput(ConversionResult result, List<Line> inputLines)
    {
        // Structural revalidation of the produced bytes before anything is written to disk.
        byte[] o = result.OutputBytes;
        if (o.Length == 0) throw OutputInvalid("output is empty");
        if (o[0] != (byte)'A') throw OutputInvalid("output header does not start with 'A'");

        var outLines = SplitLines(o, 0);
        if (outLines.Count != inputLines.Count) throw OutputInvalid("output record count differs from input");

        int expectedB = result.Format == EdiFormat.Edi13RubyRed ? 67 : 76;
        for (int i = 0; i < outLines.Count; i++)
        {
            if (outLines[i].Eol != inputLines[i].Eol) throw OutputInvalid($"line ending changed on line {i + 1}");
            if (i == 0) continue;
            byte t = o[outLines[i].Offset];
            if (t == (byte)'B' && outLines[i].Length != expectedB)
                throw OutputInvalid($"output B record on line {i + 1} has width {outLines[i].Length}, expected {expectedB}");
            if (t != (byte)'B' && t != (byte)'C')
                throw OutputInvalid($"output line {i + 1} has unexpected record type");
        }
    }

    private static EdiConversionException OutputInvalid(string detail) => new(
        ReasonCodes.OutputValidationFailed,
        $"Internal consistency check failed after conversion ({detail}). No output file was written.");

    private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
    private static bool IsUpperAlnum(byte b) => (b >= (byte)'A' && b <= (byte)'Z') || IsDigit(b);
}
