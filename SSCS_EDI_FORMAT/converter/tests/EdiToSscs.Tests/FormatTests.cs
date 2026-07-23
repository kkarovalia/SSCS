using System.Text;
using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>Mandatory format behavior tests using synthetic byte-exact inputs.</summary>
public class FormatTests
{
    // ------------------------------------------------ valid formats

    [Fact]
    public void ValidStandardEdi_Converts()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"), CRecord));
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        string[] lines = Text(result.OutputBytes).Split("\r\n");
        Assert.Equal("AGHRA  00001314540813180000950902", lines[0]);
        Assert.Equal(B("04799585508"), lines[1]); // ordinary UPC-A body unchanged
        Assert.Equal(CRecord, lines[2]);
    }

    [Fact]
    public void ValidEdi13_Converts_DroppingTwoFieldBytes()
    {
        // 12-digit code + 1 space in the 13-char field -> first 11 bytes kept.
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header, B13("842426145138 "), CRecord));
        Assert.Equal(EdiFormat.Edi13, result.Format);
        string[] lines = Text(result.OutputBytes).Split("\r\n");
        Assert.Equal("AGHRA  00001314540813180000950902", lines[0]); // '13' suffix removed from vendor
        Assert.Equal(B("84242614513"), lines[1]);
        Assert.Equal(76, lines[1].Length);
    }

    [Fact]
    public void ValidEdi13_ThirteenDigitEan_KeepsFirstEleven()
    {
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header, B13("6943424702089")));
        Assert.Equal(B("69434247020"), Text(result.OutputBytes).Split("\r\n")[1]);
    }

    [Fact]
    public void ValidRubyRed_Converts_HeaderAndSuffix()
    {
        var result = EdiConverter.Convert(LfFile(RubyHeader, BRuby("762983638051 ")));
        Assert.Equal(EdiFormat.Edi13RubyRed, result.Format);
        string[] lines = Text(result.OutputBytes).Split('\n');
        Assert.Equal("A" + RubyHeader[6..], lines[0]);
        Assert.Equal(59, lines[0].Length);
        Assert.Equal("B" + "76298363805" + new string('Y', 55), lines[1]);
        Assert.Equal(67, lines[1].Length);
        Assert.Equal("", lines[^1]); // final LF preserved
    }

    [Fact]
    public void RubyRed_DetectedByStructure_NotByFilename()
    {
        // Same bytes, no filename involved at all: detection is content-based.
        var result = EdiConverter.Convert(LfFile(RubyHeader, BRuby("762983638051 ")));
        Assert.Equal(EdiFormat.Edi13RubyRed, result.Format);

        // A GHRA13 header with the vendor-field layout (space at byte 6) is NOT Ruby Red.
        var standard = EdiConverter.Convert(CrlfFile(Ghra13Header, B13("842426145138 ")));
        Assert.Equal(EdiFormat.Edi13, standard.Format);
    }

    [Fact]
    public void RubyRed_WrongSuffix_IsRejected()
    {
        string record = BRuby("762983638051 ")[..69] + "00000001  "; // suffix must be eight zeros + 2 spaces
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(LfFile(RubyHeader, record)));
        Assert.Equal(ReasonCodes.RubyRedSuffixMismatch, ex.ReasonCode);
        Assert.Equal(2, ex.LineNumber);
    }

    // ------------------------------------------------ UPC-E behavior in records

    [Fact]
    public void ValidUpcE_IsExpanded()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("03432004   ")));
        Assert.Equal(B("03400000320"), Text(result.OutputBytes).Split("\r\n")[1]);
        Assert.Equal(1, result.UpcEExpandedCount);
    }

    [Fact]
    public void ValidUpcE_InEdi13Field_IsExpanded()
    {
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header, B13("03432004     ")));
        Assert.Equal(B("03400000320"), Text(result.OutputBytes).Split("\r\n")[1]);
    }

    [Fact]
    public void InvalidUpcECheckDigit_RejectsFile_WithRecordNumberAndReason()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"), B("03432005   "))));
        Assert.Equal(ReasonCodes.UpcEInvalidCheckDigit, ex.ReasonCode);
        Assert.Equal(3, ex.LineNumber);
        Assert.Contains("03432005", ex.UserMessage);
        Assert.Contains("check digit", ex.UserMessage);
    }

    [Fact]
    public void UnsupportedNumberSystem_BlanksField_AndWarns_FixtureProvenBehavior()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("57026404   ")));
        Assert.Equal(B("           "), Text(result.OutputBytes).Split("\r\n")[1]);
        Assert.Equal(1, result.BlankedBarcodeCount);
        Assert.Equal(0, result.UpcEExpandedCount);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(2, warning.LineNumber);
        Assert.Equal(ReasonCodes.UpcEUnsupportedNumberSystem, warning.ReasonCode);
    }

    [Fact]
    public void NumberSystemOne_IsSupported()
    {
        // NS=1 is a valid UPC-E number system: 1343200 expands to 13400000320 + its check digit.
        string body = UpcE.Expand("1343200");
        char check = UpcE.ComputeUpcACheckDigit(body);
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B($"1343200{check}   ")));
        Assert.Equal(B(body), Text(result.OutputBytes).Split("\r\n")[1]);
    }

    [Fact]
    public void EightDigits_OutsideTheUpcEFieldPattern_AreNotExpanded()
    {
        // 8 digits inside the description area (bytes 12..75) must never be touched,
        // and a code field that is not exactly '8 digits + 3 spaces' must pass through.
        string remainder = ("12345678 DESCRIPTION" + new string('X', 64))[..64];
        string oddField = "12345678 9 "; // digits-space-digit-space: not a UPC-E candidate
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B(oddField, remainder)));
        Assert.Equal(B(oddField, remainder), Text(result.OutputBytes).Split("\r\n")[1]);
        Assert.Equal(0, result.UpcEExpandedCount);
    }

    [Fact]
    public void OrdinaryUpcAField_RemainsUnchanged()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("61300871876")));
        Assert.Equal(B("61300871876"), Text(result.OutputBytes).Split("\r\n")[1]);
        Assert.Equal(0, result.UpcEExpandedCount);
    }

    [Fact]
    public void EmptyCodeField_RemainsUnchanged()
    {
        // Fixture-verified (TROLLI record): an all-space code field passes through.
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("           ")));
        Assert.Equal(B("           "), Text(result.OutputBytes).Split("\r\n")[1]);
    }

    // ------------------------------------------------ record-level validation

    [Fact]
    public void CRecord_IsPreservedExactly_IncludingTrailingSpaces()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, CRecord));
        Assert.Equal(CRecord, Text(result.OutputBytes).Split("\r\n")[1]);
    }

    [Fact]
    public void UnknownRecordType_IsRejected_WithLineNumber()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"), "D UNKNOWN RECORD")));
        Assert.Equal(ReasonCodes.UnsupportedRecordType, ex.ReasonCode);
        Assert.Equal(3, ex.LineNumber);
        Assert.Contains("'D'", ex.UserMessage);
    }

    [Fact]
    public void TooShortLine_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508")[..75])));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("75", ex.UserMessage);
    }

    [Fact]
    public void TooLongLine_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508") + "EXTRA")));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
        Assert.Contains("81", ex.UserMessage);
    }

    [Fact]
    public void MixedRecordWidths_AreRejected()
    {
        // First B is 76 (standard EDI), second is 78: the second must be rejected.
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"), B13("842426145138 "))));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
        Assert.Equal(3, ex.LineNumber);
    }

    [Fact]
    public void Edi13AlphanumericCodeField_ConvertsWithSameTruncation()
    {
        // Hidden-fixture-proven: alphanumeric product codes (e.g. '002ZH7S8J999 ',
        // '874507008063d') keep their first 11 bytes exactly like numeric ones.
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header, B13("002ZH7S8J999 "), B13("874507008063d")));
        string[] lines = Text(result.OutputBytes).Split("\r\n");
        Assert.Equal(B("002ZH7S8J99"), lines[1]);
        Assert.Equal(B("87450700806"), lines[2]);
        Assert.Equal(0, result.UpcEExpandedCount);
    }

    [Fact]
    public void Ghra13Header_OverStandardWidthRecords_StillStripsVendorSuffix()
    {
        // Hidden-fixture-proven (invoice 810738): a 'GHRA13' header over 76-byte records
        // converts as standard EDI but the vendor '13' suffix is still removed.
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header, B("04799585508"), CRecord));
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        Assert.StartsWith("AGHRA  ", Text(result.OutputBytes));
    }

    // ------------------------------------------------ file-level validation

    [Fact]
    public void BlankFile_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(Array.Empty<byte>()));
        Assert.Equal(ReasonCodes.EmptyFile, ex.ReasonCode);
    }

    [Fact]
    public void HeaderOnlyFile_Converts()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader));
        Assert.Equal("AGHRA  00001314540813180000950902", Text(result.OutputBytes));
        Assert.Equal(1, result.RecordCount);
        Assert.Equal(0, result.ProductRecordCount);
    }

    [Fact]
    public void MissingHeader_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(CrlfFile(B("04799585508"))));
        Assert.Equal(ReasonCodes.InvalidHeader, ex.ReasonCode);
        Assert.Equal(1, ex.LineNumber);
    }

    [Fact]
    public void DuplicateHeader_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, GhraHeader, B("04799585508"))));
        Assert.Equal(ReasonCodes.UnsupportedRecordType, ex.ReasonCode);
        Assert.Equal(2, ex.LineNumber);
    }

    [Fact]
    public void UnexpectedBlankLine_IsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, "", B("04799585508"))));
        Assert.Equal(ReasonCodes.BlankLine, ex.ReasonCode);
        Assert.Equal(2, ex.LineNumber);
    }

    [Fact]
    public void BinaryFile_IsRejected()
    {
        byte[] binary = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x01, 0xFF, 0xFE };
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(binary));
        Assert.Equal(ReasonCodes.BinaryContent, ex.ReasonCode);
    }

    [Fact]
    public void NullBytes_AreRejected()
    {
        byte[] input = CrlfFile(GhraHeader, B("04799585508"));
        input[40] = 0x00;
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(input));
        Assert.Equal(ReasonCodes.BinaryContent, ex.ReasonCode);
        Assert.Contains("null byte", ex.UserMessage);
    }

    [Fact]
    public void LoneCarriageReturn_IsRejected()
    {
        byte[] input = Encoding.ASCII.GetBytes(GhraHeader + "\r" + B("04799585508"));
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(input));
        Assert.Equal(ReasonCodes.LoneCarriageReturn, ex.ReasonCode);
    }

    // ------------------------------------------------ line endings and byte preservation

    [Fact]
    public void CrlfInput_ProducesCrlfOutput()
    {
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"), CRecord));
        string text = Text(result.OutputBytes);
        Assert.Contains("\r\n", text);
        Assert.DoesNotMatch("(?<!\r)\n", text);
        Assert.False(text.EndsWith('\n')); // no final newline in, none out
    }

    [Fact]
    public void LfInput_ProducesLfOutput()
    {
        var result = EdiConverter.Convert(LfFile(GhraHeader, B("04799585508"), CRecord));
        string text = Text(result.OutputBytes);
        Assert.DoesNotContain("\r", text);
        Assert.True(text.EndsWith('\n')); // final newline in, final newline out
    }

    [Fact]
    public void FinalNewline_IsPreserved()
    {
        byte[] withFinal = Encoding.ASCII.GetBytes(GhraHeader + "\r\n" + CRecord + "\r\n");
        var result = EdiConverter.Convert(withFinal);
        Assert.EndsWith("\r\n", Text(result.OutputBytes));
    }

    [Fact]
    public void NoFinalNewline_IsPreserved()
    {
        byte[] without = Encoding.ASCII.GetBytes(GhraHeader + "\r\n" + CRecord);
        var result = EdiConverter.Convert(without);
        Assert.False(Text(result.OutputBytes).EndsWith('\n'));
        Assert.EndsWith(CRecord, Text(result.OutputBytes));
    }

    [Fact]
    public void TrailingSpaces_ArePreserved()
    {
        string remainder = (new string('X', 40) + new string(' ', 24));
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508", remainder), CRecord));
        string[] lines = Text(result.OutputBytes).Split("\r\n");
        Assert.EndsWith(new string(' ', 24), lines[1]);
        Assert.EndsWith("           ", lines[2]); // C record trailing spaces intact
    }

    [Fact]
    public void Utf8Bom_IsHandledSafely()
    {
        byte[] body = CrlfFile(GhraHeader, B("04799585508"));
        byte[] withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        body.CopyTo(withBom, 3);

        var result = EdiConverter.Convert(withBom);
        Assert.True(result.BomRemoved);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.BomRemoved);
        // Output has no BOM and matches the BOM-less conversion exactly.
        AssertBytesEqual(EdiConverter.Convert(body).OutputBytes, result.OutputBytes);
    }

    [Fact]
    public void NonAsciiDescriptionBytes_ArePreservedByteForByte()
    {
        // 0xE9 = 'é' in Windows-1252, placed in the preserved description area.
        byte[] input = CrlfFile(GhraHeader, B("04799585508"));
        int descOffset = GhraHeader.Length + 2 /*CRLF*/ + 20;
        input[descOffset] = 0xE9;
        var result = EdiConverter.Convert(input);
        Assert.Equal(0xE9, result.OutputBytes[descOffset]);
        // Everything else identical to the unmodified conversion except that one byte.
        byte[] reference = EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508"))).OutputBytes;
        Assert.Equal(reference.Length, result.OutputBytes.Length);
        int diffs = 0;
        for (int i = 0; i < reference.Length; i++)
            if (reference[i] != result.OutputBytes[i]) diffs++;
        Assert.Equal(1, diffs);
    }

    [Fact]
    public void VeryLargeValidInput_ConvertsCorrectly()
    {
        const int records = 150_000;
        var sb = new StringBuilder((records + 1) * 80);
        sb.Append(Ghra13Header).Append("\r\n");
        for (int i = 0; i < records; i++)
            sb.Append(B13("842426145138 ")).Append("\r\n");
        sb.Append(CRecord);

        var result = EdiConverter.Convert(Encoding.ASCII.GetBytes(sb.ToString()));
        Assert.Equal(records, result.ProductRecordCount);
        Assert.Equal(records + 2, result.RecordCount);
        long expectedLength = 33 + 2 + (long)records * (76 + 2) + CRecord.Length;
        Assert.Equal(expectedLength, result.OutputByteCount);
    }

    // ------------------------------------------------ header edge cases

    [Fact]
    public void VendorNameLongerThanSix_IsTruncatedToSix_AndWarns()
    {
        // Fixture EDI_FRITOLAY-48690739 proves a vendor filling the 7-char EDI field is
        // truncated to the 6-char SSCS field, not rejected ('FRITOLA...' -> 'AFRITOL...').
        string header = "ABCDEFG" + "0000131454"; // 7-char vendor -> 'A' + first 6 chars
        var result = EdiConverter.Convert(CrlfFile(header, B("04799585508")));
        Assert.StartsWith("AABCDEF", Text(result.OutputBytes));
        var warning = Assert.Single(result.Warnings, w => w.ReasonCode == ReasonCodes.VendorTruncated);
        Assert.Equal(1, warning.LineNumber);
    }

    [Fact]
    public void YymmddHeaderDate_IsReformattedToMmddyy_WhenUnambiguous()
    {
        // Fixture EDI_FRITOLAY-48690739: a header date whose first two digits exceed 12 can only
        // be YYMMDD and is rewritten to MMDDYY (invoice 0048690739 + 240206 + amount 0000017899).
        string header = "FRITOLA" + "0048690739" + "240206" + "0000017899"; // 33 bytes, 7-char vendor
        var result = EdiConverter.Convert(CrlfFile(header, B("04799585508")));
        Assert.Equal("AFRITOL00486907390206240000017899", Text(result.OutputBytes).Split("\r\n")[0]);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.HeaderDateReformatted);
    }

    [Fact]
    public void MmddyyHeaderDate_IsLeftUnchanged()
    {
        // GHRA-style dates (month <= 12) are already MMDDYY and must never be reordered.
        var result = EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508")));
        Assert.Equal("AGHRA  00001314540813180000950902", Text(result.OutputBytes).Split("\r\n")[0]);
        Assert.DoesNotContain(result.Warnings, w => w.ReasonCode == ReasonCodes.HeaderDateReformatted);
    }

    // ------------------------------------------------ STRP/Alp 75-byte format (verified via SSCS ingestion)

    [Fact]
    public void Strp75Format_IsConverted_PaddingRows_AndShorteningDate()
    {
        // Verified end-to-end against SSCS ingestion of Invoice_RT006. 75-byte rows are the 76-byte
        // layout minus the trailing pad, and the 8-digit MMDDYYYY header date is shortened to MMDDYY.
        string header = "STRP   " + "0000000006" + "07212026" + "0000013650"; // 35 bytes
        string row = "B84015861637ALP MOUNTAIN WINTERGREEN 61637 001950  000005000010069900061637"; // 75 bytes
        Assert.Equal(35, header.Length);
        Assert.Equal(75, row.Length);

        var result = EdiConverter.Convert(CrlfFile(header, row));
        Assert.Equal(EdiFormat.StandardEdi75, result.Format);
        string[] lines = Text(result.OutputBytes).Split("\r\n");
        Assert.Equal("ASTRP  00000000060721260000013650", lines[0]); // date 07212026 -> 072126 (SSCS reads 7/21/2026)
        Assert.Equal(33, lines[0].Length);
        Assert.Equal(row + " ", lines[1]);                            // exactly one trailing pad byte added
        Assert.Equal(76, lines[1].Length);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.HeaderDateReformatted);
    }

    // ------------------------------------------------ already-SSCS detection

    [Fact]
    public void AlreadySscsFile_RubyRedStyle_GivesFriendlyMessage()
    {
        // A file already in SSCS output form (59-byte 'A' header, 67-byte rows) must not be
        // re-converted; it should report a plain "already in SSCS format" message.
        string header = "A" + "VM NOVELTIES LLC".PadRight(30) + "IN-99".PadRight(10) + "20260714" + "0000393221";
        string row = "B" + "60060910027" + new string('X', 55); // 67 bytes
        Assert.Equal(59, header.Length);
        Assert.Equal(67, row.Length);

        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(CrlfFile(header, row)));
        Assert.Equal(ReasonCodes.AlreadySscs, ex.ReasonCode);
        Assert.Contains("already in SSCS format", ex.UserMessage);
    }

    [Fact]
    public void EdiVendorStartingWithA_StillConverts_NotMistakenForSscs()
    {
        // False-positive guard: a genuine EDI input whose vendor code begins with 'A' must still
        // convert normally (its 33-byte header and 76-byte rows are not SSCS-output signatures).
        var result = EdiConverter.Convert(CrlfFile("ACME   00001314540813180000950902", B("04799585508")));
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        Assert.StartsWith("AACME  ", Text(result.OutputBytes));
    }

    [Fact]
    public void ShortRow_WithoutStrpHeaderShape_IsStillRejected()
    {
        // A 75-byte row under an ordinary 33-byte (26-char-payload) header is NOT the STRP shape and
        // must still be rejected — the inferred path only triggers on the 28-char 8-digit-date header.
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(CrlfFile(GhraHeader, B("04799585508")[..75])));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
    }

    [Fact]
    public void Edi13VendorWithout13Suffix_StillConvertsByWidth()
    {
        // B-record width (78) is the structural EDI13 signal; a vendor without the '13'
        // suffix keeps its name unmodified.
        string header = "ACME   00001314540813180000950902";
        var result = EdiConverter.Convert(CrlfFile(header, B13("842426145138 ")));
        Assert.Equal(EdiFormat.Edi13, result.Format);
        Assert.StartsWith("AACME  ", Text(result.OutputBytes));
    }

    [Fact]
    public void HeaderOnly_Edi13File_StripsVendorSuffix()
    {
        var result = EdiConverter.Convert(CrlfFile(Ghra13Header));
        Assert.Equal(EdiFormat.Edi13, result.Format);
        Assert.Equal("AGHRA  00001314540813180000950902", Text(result.OutputBytes));
    }
}
