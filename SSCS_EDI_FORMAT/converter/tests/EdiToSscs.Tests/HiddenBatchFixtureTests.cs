using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>
/// Byte-for-byte proofs against the hidden batch reference set (19 invoices supplied in
/// both standard EDI and EDI13 form, each with an authoritative SSCS output).
/// These fixtures proved two rules the original set could not:
/// alphanumeric EDI13 product codes truncate exactly like numeric ones, and a 'GHRA13'
/// header over standard-width records still has its vendor '13' suffix removed (810738).
/// </summary>
public class HiddenBatchFixtureTests
{
    public static readonly TheoryData<string> Invoices = new()
    {
        "799005", "799427", "802533", "803109", "806054", "806602", "809716",
        "810377", "810738", "813411", "813871", "817443", "817778", "821220",
        "822056", "822057", "824937", "825293", "828787",
    };

    [Theory]
    [MemberData(nameof(Invoices))]
    public void HiddenStandardEdi_ConvertsByteForByte(string invoice)
    {
        var result = EdiConverter.Convert(Fixture(@$"HiddenEDI\EDI_GHRA_{invoice}.txt"));
        AssertBytesEqual(Fixture(@$"HiddenSSCS\SSCS_GHRA_{invoice}.txt"), result.OutputBytes);
    }

    [Theory]
    [MemberData(nameof(Invoices))]
    public void HiddenEdi13_ConvertsByteForByte(string invoice)
    {
        var result = EdiConverter.Convert(Fixture(@$"HiddenEDI13\EDI_GHRA13_{invoice}.txt"));
        AssertBytesEqual(Fixture(@$"HiddenSSCS\SSCS_GHRA_{invoice}.txt"), result.OutputBytes);
    }

    [Fact]
    public void Invoice810738_Edi13File_IsStructurallyStandardEdi()
    {
        // The 'oddball': EDI13-named file with a GHRA13 header but 76-byte records.
        var result = EdiConverter.Convert(Fixture(@"HiddenEDI13\EDI_GHRA13_810738.txt"));
        Assert.Equal(EdiFormat.StandardEdi, result.Format);
        Assert.StartsWith("AGHRA  ", Text(result.OutputBytes));
    }

    [Fact]
    public void Invoice806054_WarnsOnUnsupportedNumberSystem_InBothForms()
    {
        var edi = EdiConverter.Convert(Fixture(@"HiddenEDI\EDI_GHRA_806054.txt"));
        var edi13 = EdiConverter.Convert(Fixture(@"HiddenEDI13\EDI_GHRA13_806054.txt"));
        Assert.Contains(edi.Warnings, w => w.ReasonCode == ReasonCodes.UpcEUnsupportedNumberSystem);
        Assert.Contains(edi13.Warnings, w => w.ReasonCode == ReasonCodes.UpcEUnsupportedNumberSystem);
    }
}
