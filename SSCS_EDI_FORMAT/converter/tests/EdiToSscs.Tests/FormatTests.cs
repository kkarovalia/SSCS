using System.Globalization;
using System.Text;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Input-side proofs: which layout a file is recognised as, and which files are refused.
/// What the converter then *produces* is proved in <see cref="XmlConversionTests"/>.
/// </summary>
public class FormatTests
{
    /// <summary>The 64 bytes after an 11-char code field: description, SKU, case cost, pack, cases, each-list.</summary>
    private static string Fields64(int caseCostCents = 100, int pack = 1, int cases = 1, int eaListCents = 199)
        => ("TEST ITEM".PadRight(25)
          + "0".PadRight(6)
          + caseCostCents.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')
          + "  "
          + pack.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')
          + cases.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0')
          + eaListCents.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0')).PadRight(64);

    private static string Fields55() => Fields64()[..55];

    // ------------------------------------------------------------------ format detection

    [Fact]
    public void SeventySixByteRowsAreStandardEdi()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("01200000157", Fields64())));
        Assert.Equal(EdiFormat.StandardEdi, r.Format);
    }

    [Fact]
    public void SeventyEightByteRowsAreEdi13()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.Ghra13Header, TestUtil.B13("012000001574 ", Fields64())));
        Assert.Equal(EdiFormat.Edi13, r.Format);
    }

    [Fact]
    public void RubyRedIsDetectedByStructureNotByFilename()
    {
        var r = EdiConverter.Convert(TestUtil.LfFile(TestUtil.RubyHeader, TestUtil.BRuby("762983638051 ", Fields55())));
        Assert.Equal(EdiFormat.Edi13RubyRed, r.Format);
        Assert.Equal("Ruby Red Sales & Services", r.Invoice.SupplierName);
        Assert.Equal("IN-3738", r.Invoice.InvoiceNumber);
    }

    [Fact]
    public void RubyRedWithTheWrongZeroSuffixIsRejected()
    {
        string bad = "B" + "762983638051 " + Fields55() + "00000000 X";
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.LfFile(TestUtil.RubyHeader, bad)));
        Assert.Equal(ReasonCodes.RubyRedSuffixMismatch, ex.ReasonCode);
    }

    [Fact]
    public void StrpSeventyFiveByteRowsAreDetectedByTheirEightDigitHeaderDate()
    {
        string header = "STRP   0000000006" + "07212026" + "0000013650"; // 35 bytes
        string row = ("B" + "84015861637" + Fields64())[..75];
        var r = EdiConverter.Convert(TestUtil.CrlfFile(header, row));

        Assert.Equal(EdiFormat.StandardEdi75, r.Format);
        Assert.Equal(new DateOnly(2026, 7, 21), r.Invoice.Date);
        Assert.Equal(136.50m, r.Invoice.Total);
    }

    [Fact]
    public void AShortRowWithoutTheStrpHeaderShapeIsStillRejected()
    {
        string row = ("B" + "84015861637" + Fields64())[..75];
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, row)));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
    }

    [Fact]
    public void AGhra13HeaderOverStandardWidthRowsStillStripsTheVendorSuffix()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.Ghra13Header, TestUtil.B("01200000157", Fields64())));
        Assert.Equal(EdiFormat.StandardEdi, r.Format);
        Assert.Equal("GHRA", r.Invoice.SupplierName);
    }

    [Fact]
    public void AnEdi13FileWhoseVendorLacksThe13SuffixIsStillDetectedByWidth()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B13("012000001574 ", Fields64())));
        Assert.Equal(EdiFormat.Edi13, r.Format);
    }

    // ------------------------------------------------------------------ record validation

    [Fact]
    public void AnUnknownRecordTypeIsRejectedWithItsLineNumber()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("01200000157", Fields64()), "Zjunk")));
        Assert.Equal(ReasonCodes.UnsupportedRecordType, ex.ReasonCode);
        Assert.Equal(3, ex.LineNumber);
    }

    [Theory]
    [InlineData(70)]
    [InlineData(77)]
    public void ARowOfTheWrongWidthIsRejected(int width)
    {
        string row = ("B" + "01200000157" + Fields64()).PadRight(width)[..width];
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("01200000157", Fields64()), row)));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
    }

    [Fact]
    public void MixedRecordWidthsInOneFileAreRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(TestUtil.CrlfFile(
            TestUtil.Ghra13Header,
            TestUtil.B13("012000001574 ", Fields64()),
            TestUtil.B("01200000157", Fields64()))));
        Assert.Equal(ReasonCodes.UnsupportedRecordLength, ex.ReasonCode);
    }

    [Fact]
    public void AnInvalidUpcECheckDigitStopsTheWholeFile()
    {
        // 03432005 — the correct check digit for this body is 4, so this barcode is mistyped.
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("03432005   ", Fields64()))));
        Assert.Equal(ReasonCodes.UpcEInvalidCheckDigit, ex.ReasonCode);
        Assert.Contains("03432005", ex.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedUpcENumberSystemWarnsAndKeepsTheCode()
    {
        // Number system 5 is not valid for UPC-E. The retired output blanked the field; we keep the
        // code and flag it, because an empty id is worse than an unusual one.
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("57026404   ", Fields64())));

        Assert.Contains(r.Warnings, w => w.ReasonCode == ReasonCodes.UpcEUnsupportedNumberSystem);
        Assert.Equal("57026404", Assert.Single(r.Invoice.Lines).Gtin);
    }

    [Fact]
    public void NumberSystemOneIsAValidUpcE()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("12345670   ", Fields64())));
        Assert.Equal(1, r.UpcEExpandedCount);
    }

    [Fact]
    public void EightDigitsThatDoNotFillTheUpcEShapeAreNotExpanded()
    {
        // Ten digits and a space is not the 8-digits-plus-3-spaces UPC-E shape.
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("7777777778 ", Fields64())));
        Assert.Equal(0, r.UpcEExpandedCount);
        Assert.Equal("7777777778", Upc.SscsIdFromGtin(Assert.Single(r.Invoice.Lines).Gtin));
    }

    [Fact]
    public void AnEmptyCodeFieldIsFlaggedRatherThanGuessed()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("           ", Fields64())));
        Assert.Contains(r.Warnings, w => w.ReasonCode == ReasonCodes.NonBarcodeCode);
        Assert.Equal("", Assert.Single(r.Invoice.Lines).Gtin);
    }

    // ------------------------------------------------------------------ file-level validation

    [Fact]
    public void AnEmptyFileIsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert([]));
        Assert.Equal(ReasonCodes.EmptyFile, ex.ReasonCode);
    }

    [Fact]
    public void AFileWithAHeaderButNoProductsIsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader)));
        Assert.Equal(ReasonCodes.NoProductRecords, ex.ReasonCode);
    }

    [Fact]
    public void AFileThatDoesNotStartWithAHeaderIsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.B("01200000157", Fields64()))));
        Assert.Equal(ReasonCodes.InvalidHeader, ex.ReasonCode);
    }

    [Fact]
    public void ASecondHeaderInTheMiddleOfTheFileIsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.GhraHeader)));
        Assert.Equal(ReasonCodes.UnsupportedRecordType, ex.ReasonCode);
    }

    [Fact]
    public void ABlankLineInTheMiddleOfTheFileIsRejected()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader, "", TestUtil.B("01200000157", Fields64()))));
        Assert.Equal(ReasonCodes.BlankLine, ex.ReasonCode);
    }

    [Fact]
    public void ABlankFinalLineIsJustTheFilesLastNewlineAndIsAccepted()
    {
        var r = EdiConverter.Convert(TestUtil.LfFile(TestUtil.RubyHeader, TestUtil.BRuby("762983638051 ", Fields55())));
        Assert.Single(r.Invoice.Lines);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x07)]
    public void BinaryContentIsRejected(byte bad)
    {
        byte[] file = TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("01200000157", Fields64()));
        file[^1] = bad;
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(file));
        Assert.Equal(ReasonCodes.BinaryContent, ex.ReasonCode);
    }

    [Fact]
    public void ACarriageReturnWithoutALineFeedIsRejected()
    {
        byte[] file = Encoding.ASCII.GetBytes(TestUtil.GhraHeader + "\r" + TestUtil.B("01200000157", Fields64()));
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(file));
        Assert.Equal(ReasonCodes.LoneCarriageReturn, ex.ReasonCode);
    }

    [Fact]
    public void AUtf8ByteOrderMarkIsRemovedAndReported()
    {
        byte[] body = TestUtil.CrlfFile(TestUtil.GhraHeader, TestUtil.B("01200000157", Fields64()));
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. body];

        var r = EdiConverter.Convert(withBom);

        Assert.True(r.BomRemoved);
        Assert.Contains(r.Warnings, w => w.ReasonCode == ReasonCodes.BomRemoved);
        Assert.Equal("GHRA", r.Invoice.SupplierName);
    }

    // ------------------------------------------------------------------ already-converted

    [Fact]
    public void ARetiredFixedWidthOutputFileIsRecognisedAsAlreadyConverted()
    {
        // 'A' + company(30) + ref(10) + date(8) + amount(10) with 67-byte rows — the old Ruby Red output.
        string header = "A" + "VM NOVELTIES LLC".PadRight(30) + "IN-99".PadRight(10) + "20260714" + "0000393221";
        string row = "B" + "60060910027" + new string('X', 55);

        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(TestUtil.CrlfFile(header, row)));
        Assert.Equal(ReasonCodes.AlreadyConverted, ex.ReasonCode);
    }

    [Fact]
    public void AnEdiVendorCodeStartingWithAIsNotMistakenForConvertedOutput()
    {
        var r = EdiConverter.Convert(TestUtil.CrlfFile("ACME   00001314540813180000950902", TestUtil.B("01200000157", Fields64())));
        Assert.Equal("ACME", r.Invoice.SupplierName);
    }

    // ------------------------------------------------------------------ scale

    [Fact]
    public void AVeryLargeFileConvertsCorrectly()
    {
        var rows = new List<string> { TestUtil.GhraHeader };
        for (int i = 0; i < 5000; i++)
            rows.Add(TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 199)));

        var r = EdiConverter.Convert(TestUtil.CrlfFile([.. rows]));

        Assert.Equal(5000, r.ProductRecordCount);
        Assert.Equal(5000m, r.Invoice.LineTotal);
    }
}
