using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Official UPC-E expansion rules, using values verified in the reference fixtures.</summary>
public class UpcETests
{
    [Theory]
    // D6 = 0 (fixture-verified)
    [InlineData("0343200", "03400000320")]
    [InlineData("0227680", "02200000768")]
    [InlineData("0342290", "03400000229")]
    // D6 = 1 (fixture-verified: GRIZZLY 04288619)
    [InlineData("0428861", "04210000886")]
    // D6 = 2 (fixture-verified: PALL MALL 02785424, MARLBORO 02836328)
    [InlineData("0278542", "02720000854")]
    [InlineData("0283632", "02820000363")]
    // D6 = 3 (fixture-verified: MAVERICK 09055138, CAMEL 01230739)
    [InlineData("0905513", "09050000051")]
    [InlineData("0123073", "01230000007")]
    // D6 = 4 (rule case)
    [InlineData("0123454", "01234000005")]
    // D6 = 5..9 (fixture-verified: CHOCOLATE CHERRIES 01122855)
    [InlineData("0112285", "01122800005")]
    [InlineData("0123456", "01234500006")]
    [InlineData("0123459", "01234500009")]
    public void Expand_FollowsOfficialRules(string upcE7, string expectedBody11) =>
        Assert.Equal(expectedBody11, UpcE.Expand(upcE7));

    [Theory]
    // Check digits verified against the supplied fixtures (8th digit of each input barcode).
    [InlineData("03400000320", '4')]
    [InlineData("02200000768", '1')]
    [InlineData("04210000886", '9')]
    [InlineData("02720000854", '4')]
    [InlineData("09050000051", '8')]
    [InlineData("01122800005", '5')]
    public void ComputeUpcACheckDigit_MatchesFixtures(string body11, char expected) =>
        Assert.Equal(expected, UpcE.ComputeUpcACheckDigit(body11));

    [Theory]
    [InlineData("123456")]     // too short
    [InlineData("12345678")]   // too long
    [InlineData("12A4567")]    // non-digit
    public void Expand_RejectsMalformedInput(string bad) =>
        Assert.Throws<ArgumentException>(() => UpcE.Expand(bad));
}
