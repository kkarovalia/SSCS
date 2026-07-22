using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>Mandatory output-filename rules.</summary>
public class NamingTests
{
    [Theory]
    [InlineData("EDI_GHRA_131454.txt", "SSCS_GHRA_131454.txt")]
    [InlineData("EDI_PEPSI-80471906.txt", "SSCS_PEPSI-80471906.txt")]
    [InlineData("EDI13-RubyRedSalesServices.txt", "SSCS_RubyRedSalesServices.txt")]
    [InlineData("PEPSI.txt", "SSCS_PEPSI.txt")]
    [InlineData("PEPSI-80471906.txt", "SSCS_PEPSI-80471906.txt")]
    [InlineData("edi pepsi.txt", "SSCS_pepsi.txt")]
    [InlineData("EDI13_GHRA.txt", "SSCS_GHRA.txt")]
    [InlineData("EDI13-GHRA.txt", "SSCS_GHRA.txt")]
    [InlineData("SSCS_GHRA.txt", "SSCS_GHRA.txt")]
    [InlineData("SSCS_SSCS_GHRA.txt", "SSCS_GHRA.txt")] // never more than one leading SSCS_
    [InlineData("EDI.txt", "SSCS_Converted.txt")]
    [InlineData("EDI13.txt", "SSCS_Converted.txt")]
    [InlineData("edi13 ghra invoice.txt", "SSCS_ghra invoice.txt")]
    [InlineData("EDI_PEPSI-80471906 (1).txt", "SSCS_PEPSI-80471906 (1).txt")] // browser-added suffix
    [InlineData("EDI_a.b.c.txt", "SSCS_a.b.c.txt")]                           // multiple periods
    [InlineData("EDI GHRA WEEKLY ORDER.txt", "SSCS_GHRA WEEKLY ORDER.txt")]   // spaces
    [InlineData("EDI_Fakturę_zażółć.txt", "SSCS_Fakturę_zażółć.txt")]         // Unicode preserved
    [InlineData("EDI_GHRA.edi", "SSCS_GHRA.txt")]                             // .edi extension
    [InlineData("EDI13_GHRA.edi13", "SSCS_GHRA.txt")]                         // .edi13 extension
    [InlineData("EDINBURGH.txt", "SSCS_EDINBURGH.txt")]  // token requires a separator boundary
    [InlineData("MY EDI FILE.txt", "SSCS_MY EDI FILE.txt")] // token only stripped at the start
    public void BuildOutputFileName_ProducesCanonicalNames(string input, string expected) =>
        Assert.Equal(expected, OutputNaming.BuildOutputFileName(input));

    [Fact]
    public void AllResults_StartWithExactlyOneSscsPrefix()
    {
        string[] inputs =
        {
            "EDI_GHRA_131454.txt", "SSCS_SSCS_GHRA.txt", "sscs_sscs_sscs_x.txt", "EDI13-EDI-X.txt",
            "EDI.txt", "a.txt", "SSCS_.txt", "edi sscs edi13 y.txt",
        };
        foreach (string input in inputs)
        {
            string result = OutputNaming.BuildOutputFileName(input);
            Assert.StartsWith("SSCS_", result);
            Assert.DoesNotContain("SSCS_SSCS", result, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".txt", result);
        }
    }

    [Fact]
    public void EmptyAfterStripping_UsesSafeFallback()
    {
        Assert.Equal("SSCS_Converted.txt", OutputNaming.BuildOutputFileName("EDI13.txt"));
        Assert.Equal("SSCS_Converted.txt", OutputNaming.BuildOutputFileName("SSCS_.txt"));
        Assert.Equal("SSCS_Converted.txt", OutputNaming.BuildOutputFileName("edi -_ .txt"));
    }

    [Fact]
    public void OnlyWindowsForbiddenCharacters_AreSanitized()
    {
        // ':' and '?' are forbidden; parentheses, plus, comma, accents are not and must survive.
        Assert.Equal("SSCS_a_b_ (c)+,é.txt", OutputNaming.BuildOutputFileName("EDI_a:b? (c)+,é.txt"));
    }

    [Fact]
    public void VeryLongNames_AreHandled()
    {
        string longStem = new string('N', 180);
        string result = OutputNaming.BuildOutputFileName("EDI_" + longStem + ".txt");
        Assert.Equal("SSCS_" + longStem + ".txt", result);
    }

    [Theory]
    [InlineData("SSCS_PEPSI.txt", 0, "SSCS_PEPSI.txt")]
    [InlineData("SSCS_PEPSI.txt", 1, "SSCS_PEPSI (1).txt")]
    [InlineData("SSCS_PEPSI.txt", 2, "SSCS_PEPSI (2).txt")]
    public void CandidateNames_FollowTheDocumentedPattern(string name, int attempt, string expected) =>
        Assert.Equal(expected, OutputNaming.CandidateName(name, attempt));

    // ------------------------------------------------ collision behavior on disk

    [Fact]
    public void ExistingOutputFile_GetsNextAvailableSuffix()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        File.WriteAllText(Path.Combine(dest, "SSCS_GHRA_131454.txt"), "existing");
        File.WriteAllText(Path.Combine(dest, "SSCS_GHRA_131454 (1).txt"), "existing");

        string input = Path.Combine(tmp.Path, "EDI_GHRA_131454.txt");
        File.WriteAllBytes(input, Fixture(@"EDI\EDI_GHRA_131454.txt"));

        var service = new ConversionService(new ConversionLogger(tmp.Sub("logs")), "test");
        var outcome = service.ConvertFile(input, dest);

        Assert.True(outcome.Success);
        Assert.Equal("SSCS_GHRA_131454 (2).txt", Path.GetFileName(outcome.FinalOutputPath));
        Assert.Contains("SSCS_GHRA_131454 (2).txt", outcome.UserMessage); // actual final name is displayed
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "SSCS_GHRA_131454.txt"))); // untouched
    }

    [Fact]
    public void SimultaneousConversions_RequestingTheSameName_GetDistinctFiles()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var logger = new ConversionLogger(tmp.Sub("logs"));
        byte[] fixture = Fixture(@"EDI\EDI_GHRA_131454.txt");

        const int parallel = 8;
        var inputs = new string[parallel];
        for (int i = 0; i < parallel; i++)
        {
            string dir = tmp.Sub($"in{i}");
            inputs[i] = Path.Combine(dir, "EDI_GHRA_131454.txt"); // all normalize to the same output name
            File.WriteAllBytes(inputs[i], fixture);
        }

        var outcomes = inputs.AsParallel().WithDegreeOfParallelism(parallel)
            .Select(p => new ConversionService(logger, "test").ConvertFile(p, dest))
            .ToList();

        Assert.All(outcomes, o => Assert.True(o.Success, o.UserMessage));
        var names = outcomes.Select(o => Path.GetFileName(o.FinalOutputPath)!).ToList();
        Assert.Equal(parallel, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.StartsWith("SSCS_GHRA_131454", n));
        Assert.Empty(Directory.GetFiles(dest, "*.tmp")); // no temp files left behind
        byte[] expected = Fixture(@"SSCS\SSCS_GHRA_131454.txt");
        foreach (string n in names)
            AssertBytesEqual(expected, File.ReadAllBytes(Path.Combine(dest, n)));
    }
}
