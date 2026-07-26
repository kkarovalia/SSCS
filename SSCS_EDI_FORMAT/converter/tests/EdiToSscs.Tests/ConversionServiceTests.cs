using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>Safe file-writing behavior: atomicity, source protection, friendly failures.</summary>
public class ConversionServiceTests
{
    private static ConversionService Service(TempDir tmp) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test");

    private static string PlaceFixture(TempDir tmp, string fixture, string asName)
    {
        string path = Path.Combine(tmp.Path, asName);
        File.WriteAllBytes(path, Fixture(fixture));
        return path;
    }

    [Fact]
    public void SuccessfulConversion_WritesExactBytes_AndNeverTouchesTheSource()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_131454.txt", "EDI_GHRA_131454.txt");
        byte[] originalBytes = File.ReadAllBytes(input);
        DateTime originalWrite = File.GetLastWriteTimeUtc(input);

        var outcome = Service(tmp).ConvertFile(input, dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_GHRA_131454.xml", Path.GetFileName(outcome.FinalOutputPath));
        Assert.Contains("SSCS_GHRA_131454.xml", outcome.UserMessage);
        Assert.Contains(dest, outcome.UserMessage);
        AssertBytesEqual(EdiConverter.Convert(Fixture(@"EDI\EDI_GHRA_131454.txt")).OutputBytes, File.ReadAllBytes(outcome.FinalOutputPath!));

        // The original input file is bit-identical and untouched.
        Assert.Equal(originalBytes, File.ReadAllBytes(input));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(input));
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
    }

    [Fact]
    public void FailedConversion_LeavesNoOutputFileOfAnyKind()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_BROKEN.txt");
        File.WriteAllBytes(input, CrlfFile(GhraHeader, B("04799585508") + "EXTRA")); // bad width

        var outcome = Service(tmp).ConvertFile(input, dest);

        Assert.False(outcome.Success);
        Assert.Contains("EDI_BROKEN.txt", outcome.UserMessage);
        Assert.Contains("81", outcome.UserMessage); // record length named
        Assert.Contains("The original file was not changed", outcome.UserMessage);
        Assert.Empty(Directory.GetFiles(dest)); // no partial, zero-byte, temp or misnamed file
        Assert.True(File.Exists(input));
    }

    [Fact]
    public void ReadOnlyInputFile_ConvertsFine()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceFixture(tmp, @"EDI\EDI_PEPSI-80471906.txt", "EDI_PEPSI-80471906.txt");
        File.SetAttributes(input, FileAttributes.ReadOnly);
        try
        {
            var outcome = Service(tmp).ConvertFile(input, dest);
            Assert.True(outcome.Success, outcome.UserMessage);
            AssertBytesEqual(EdiConverter.Convert(Fixture(@"EDI\EDI_PEPSI-80471906.txt")).OutputBytes, File.ReadAllBytes(outcome.FinalOutputPath!));
        }
        finally
        {
            File.SetAttributes(input, FileAttributes.Normal);
        }
    }

    [Fact]
    public void LockedSourceFile_FailsWithFriendlyMessage()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_131454.txt", "EDI_GHRA_131454.txt");

        using (new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var outcome = Service(tmp).ConvertFile(input, dest);
            Assert.False(outcome.Success);
            Assert.Contains("open in another program", outcome.UserMessage);
            Assert.DoesNotContain("Exception", outcome.UserMessage);
            Assert.Empty(Directory.GetFiles(dest));
        }
    }

    [Fact]
    public void DestinationSameAsSourceFolder_Works_AndNeverOverwritesTheSource()
    {
        using var tmp = new TempDir();
        string folder = tmp.Sub("both");
        string input = Path.Combine(folder, "EDI_GHRA_131454.txt");
        File.WriteAllBytes(input, Fixture(@"EDI\EDI_GHRA_131454.txt"));

        var outcome = Service(tmp).ConvertFile(input, folder);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_GHRA_131454.xml", Path.GetFileName(outcome.FinalOutputPath));
        Assert.Equal(Fixture(@"EDI\EDI_GHRA_131454.txt"), File.ReadAllBytes(input)); // source intact
    }

    [Fact]
    public void OutputNameCollidingWithSourcePath_SkipsToTheNextName()
    {
        using var tmp = new TempDir();
        string folder = tmp.Sub("both");
        // The input is already named like the output would be; conversion must not touch it.
        string input = Path.Combine(folder, "SSCS_GHRA.xml");
        File.WriteAllBytes(input, Fixture(@"EDI\EDI_GHRA_131454.txt"));
        byte[] originalBytes = File.ReadAllBytes(input);

        var outcome = Service(tmp).ConvertFile(input, folder);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_GHRA (1).xml", Path.GetFileName(outcome.FinalOutputPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(input)); // source byte-identical
    }

    [Fact]
    public void UnsupportedContent_WithEdiInTheName_IsStillRejected()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_VENDOR.txt");
        File.WriteAllText(input, "Just,a,csv\n1,2,3\n");

        var outcome = Service(tmp).ConvertFile(input, dest);

        Assert.False(outcome.Success);
        Assert.Contains("EDI_VENDOR.txt", outcome.UserMessage);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public void MissingSourceFile_FailsWithFriendlyMessage()
    {
        using var tmp = new TempDir();
        var outcome = Service(tmp).ConvertFile(Path.Combine(tmp.Path, "nope.txt"), tmp.Sub("out"));
        Assert.False(outcome.Success);
        Assert.Contains("could not be found", outcome.UserMessage);
    }

    [Fact]
    public void LongDestinationPath_IsSupported()
    {
        using var tmp = new TempDir();
        string deep = tmp.Path;
        // Build a destination well past the classic 260-character MAX_PATH.
        while (deep.Length < 300)
            deep = Path.Combine(deep, "Very Long Subfolder Name With Spaces 0123456789");
        Directory.CreateDirectory(deep);

        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");
        var outcome = Service(tmp).ConvertFile(input, deep);

        Assert.True(outcome.Success, outcome.UserMessage);
        AssertBytesEqual(EdiConverter.Convert(Fixture(@"EDI\EDI_GHRA_118955.txt")).OutputBytes, File.ReadAllBytes(outcome.FinalOutputPath!));
    }

    [Fact]
    public void WarningConversion_ReportsWarningInMessage()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_129348.txt", "EDI_GHRA_129348.txt");

        var outcome = Service(tmp).ConvertFile(input, dest);

        Assert.True(outcome.Success);
        Assert.True(outcome.HasWarnings);
        Assert.Contains("57026404", outcome.UserMessage);
        AssertBytesEqual(EdiConverter.Convert(Fixture(@"EDI\EDI_GHRA_129348.txt")).OutputBytes, File.ReadAllBytes(outcome.FinalOutputPath!));
    }
}
