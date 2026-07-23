using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>
/// Byte-for-byte proofs against every supplied reference pair: identical length,
/// identical SHA-256, every byte identical (first-diff details on failure).
/// </summary>
public class FixtureByteTests
{
    private static void AssertPairConverts(string inputRelative, string expectedRelative)
    {
        byte[] input = Fixture(inputRelative);
        var result = EdiConverter.Convert(input);
        byte[] expected = Fixture(expectedRelative);
        AssertBytesEqual(expected, result.OutputBytes);
    }

    // ---- The four mandatory reference conversions ----

    [Fact]
    public void Mandatory_StandardGhraEdi_To_GhraSscs() =>
        AssertPairConverts(@"EDI\EDI_GHRA_131454.txt", @"SSCS\SSCS_GHRA_131454.txt");

    [Fact]
    public void Mandatory_PepsiEdi_To_PepsiSscs() =>
        AssertPairConverts(@"EDI\EDI_PEPSI-80471906.txt", @"SSCS\SSCS_PEPSI-80471906.txt");

    [Fact]
    public void Mandatory_Ghra13Edi13_To_GhraSscs() =>
        AssertPairConverts(@"EDI13\EDI_GHRA13_131454.txt", @"SSCS\SSCS_GHRA_131454.txt");

    [Fact]
    public void Mandatory_RubyRedEdi13_To_RubyRedSscsExample() =>
        AssertPairConverts(@"EDI13\EDI13-RubyRedSalesServices.txt", @"SSCS\EDI-RubyRedSalesServices (SSCS EXAMPLE).txt");

    // ---- Every other supplied reference pair ----

    [Theory]
    [InlineData(@"EDI\EDI_GHRA_118955.txt", @"SSCS\SSCS_GHRA_118955.txt")]
    [InlineData(@"EDI\EDI_GHRA_120511.txt", @"SSCS\SSCS_GHRA_120511.txt")]
    [InlineData(@"EDI\EDI_GHRA_125482.txt", @"SSCS\SSCS_GHRA_125482.txt")]
    [InlineData(@"EDI\EDI_GHRA_129348.txt", @"SSCS\SSCS_GHRA_129348.txt")]
    [InlineData(@"EDI\EDI_GHRA_129478.txt", @"SSCS\SSCS_GHRA_129478.txt")]
    [InlineData(@"EDI\EDI_GHRA_129943.txt", @"SSCS\SSCS_GHRA_129943.txt")]
    [InlineData(@"EDI13\EDI_GHRA13_120511.txt", @"SSCS\SSCS_GHRA_120511.txt")]
    [InlineData(@"EDI13\EDI_GHRA13_125482.txt", @"SSCS\SSCS_GHRA_125482.txt")]
    [InlineData(@"EDI13\EDI_GHRA13_129348.txt", @"SSCS\SSCS_GHRA_129348.txt")]
    [InlineData(@"EDI13\EDI_GHRA13_129478.txt", @"SSCS\SSCS_GHRA_129478.txt")]
    [InlineData(@"EDI13\EDI_GHRA13_129943.txt", @"SSCS\SSCS_GHRA_129943.txt")]
    public void AllOtherSuppliedPairs_ConvertByteForByte(string input, string expected) =>
        AssertPairConverts(input, expected);

    /// <summary>
    /// EDI_GHRA13_118955.txt: the task statement lists no reference output for this file,
    /// but the supplied SSCS_GHRA_118955.txt (produced from the standard-EDI twin of the
    /// same invoice) matches its conversion exactly, so it is verified against that.
    /// Structural invariants are asserted regardless.
    /// </summary>
    [Fact]
    public void Ghra13_118955_ConvertsWithVerifiedEdi13Rules()
    {
        byte[] input = Fixture(@"EDI13\EDI_GHRA13_118955.txt");
        var result = EdiConverter.Convert(input);

        // Structural invariants of the verified EDI13 rules.
        Assert.Equal(EdiFormat.Edi13, result.Format);
        string text = Text(result.OutputBytes);
        string[] lines = text.Split("\r\n");
        Assert.StartsWith("AGHRA  ", lines[0]);
        Assert.Equal(33, lines[0].Length);
        foreach (string b in lines.Skip(1).Where(l => l.StartsWith('B')))
            Assert.Equal(76, b.Length);

        // Bonus verification: the supplied SSCS output of the same invoice number matches.
        AssertBytesEqual(Fixture(@"SSCS\SSCS_GHRA_118955.txt"), result.OutputBytes);
    }

    /// <summary>
    /// FRITOLAY invoice: a real non-GHRA vendor. Proves two rules the GHRA/PEPSI set never
    /// exercised — a 7-char vendor field truncates to 6 ('FRITOLA' -> 'FRITOL'), and a YYMMDD
    /// header date is rewritten as MMDDYY ('240206' -> '020624'). Body records are standard
    /// 76-byte EDI and pass through byte-for-byte.
    /// </summary>
    [Fact]
    public void FritoLayEdi_To_FritoLaySscs_ByteForByte()
    {
        byte[] input = Fixture(@"EDI\EDI_FRITOLAY-48690739.txt");
        var result = EdiConverter.Convert(input);
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        AssertBytesEqual(Fixture(@"SSCS\SSCS_FRITOLAY-48690739.txt"), result.OutputBytes);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.VendorTruncated);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.HeaderDateReformatted);
    }

    /// <summary>
    /// Real STRP/Alp invoice (Invoice_RT006). Golden master: the reference is the converter output that
    /// SSCS ingested correctly (invoice #6, 7/21/2026, $136.50, all 7 line items verified in the SSCS
    /// UI), so this locks the confirmed behavior byte-for-byte against regressions.
    /// </summary>
    [Fact]
    public void RealStrpInvoice_ConvertsByteForByte()
    {
        byte[] input = Fixture(@"EDI\Invoice_RT006_EDI.txt");
        var result = EdiConverter.Convert(input);
        Assert.Equal(EdiFormat.StandardEdi75, result.Format);
        AssertBytesEqual(Fixture(@"SSCS\SSCS_Invoice_RT006_EDI.txt"), result.OutputBytes);
    }

    /// <summary>
    /// Real already-SSCS file (VM Novelties, a Ruby-Red-style SSCS output that is misleadingly named
    /// "EDI-..."). Converting it must report ALREADY_SSCS, not the generic "unrecognized header" error.
    /// </summary>
    [Fact]
    public void RealAlreadySscsFile_IsDetected_WithFriendlyMessage()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            EdiConverter.Convert(Fixture(@"SSCS\SSCS_VMNOVELTIESLLC.txt")));
        Assert.Equal(ReasonCodes.AlreadySscs, ex.ReasonCode);
        Assert.Contains("already in SSCS format", ex.UserMessage);
    }

    [Fact]
    public void ConversionStats_AreReported()
    {
        var result = EdiConverter.Convert(Fixture(@"EDI\EDI_GHRA_131454.txt"));
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        Assert.Equal(233, result.RecordCount);
        Assert.Equal(231, result.ProductRecordCount);
        Assert.Equal(39, result.UpcEExpandedCount); // every 8-digit+3-space field in the fixture
        Assert.Equal(0, result.BlankedBarcodeCount);
        Assert.Equal(18082, result.InputByteCount);
        Assert.Equal(18082, result.OutputByteCount);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public void InvalidNumberSystemFixture_ReportsWarning()
    {
        var result = EdiConverter.Convert(Fixture(@"EDI\EDI_GHRA_129348.txt"));
        Assert.Equal(1, result.BlankedBarcodeCount);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(104, warning.LineNumber);
        Assert.Equal(ReasonCodes.UpcEUnsupportedNumberSystem, warning.ReasonCode);
    }
}
