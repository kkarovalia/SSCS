using System.Diagnostics;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Mandatory diagnostic-log and 30-day retention behavior.</summary>
public class LoggingTests
{
    private static LogEntry SampleEntry() => new()
    {
        AppVersion = "test",
        Status = "Success",
        InputFileName = "EDI_TEST.txt",
    };

    private static string MakeLog(string dir, string name, TimeSpan age)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void LogDirectoryDoesNotExist_ItIsCreatedOnFirstWrite()
    {
        using var tmp = new TempDir();
        string dir = Path.Combine(tmp.Path, "nested", "Logs");
        Assert.False(Directory.Exists(dir));
        Assert.True(new ConversionLogger(dir).TryWrite(SampleEntry()));
        Assert.True(Directory.Exists(dir));
        Assert.Single(Directory.GetFiles(dir, $"{ConversionLogger.LogFilePrefix}*.log"));
    }

    [Fact]
    public void LogDirectoryNotWritable_TryWriteReturnsFalse_NeverThrows()
    {
        using var tmp = new TempDir();
        // Occupy the log directory path with a FILE so directory creation must fail.
        string blocked = Path.Combine(tmp.Path, "Logs");
        File.WriteAllText(blocked, "not a directory");
        Assert.False(new ConversionLogger(blocked).TryWrite(SampleEntry()));
    }

    [Fact]
    public void RetentionBoundary_29DaysKept_Exactly30Kept_OlderDeleted()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("Logs");
        var logger = new ConversionLogger(dir);
        DateTime nowUtc = DateTime.UtcNow;

        string d29 = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}29d.log", TimeSpan.FromDays(29));
        string d30 = Path.Combine(dir, $"{ConversionLogger.LogFilePrefix}30d.log");
        File.WriteAllText(d30, "{}");
        File.SetLastWriteTimeUtc(d30, nowUtc.AddDays(-30)); // exactly the boundary
        string d30plus = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}30d1m.log", TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        string d31 = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}31d.log", TimeSpan.FromDays(31));
        string d90 = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}90d.log", TimeSpan.FromDays(90));

        var failures = logger.CleanupOldLogs(nowUtc);

        Assert.Empty(failures);
        Assert.True(File.Exists(d29), "a 29-day-old log must be retained");
        // Documented boundary: only files STRICTLY older than 30 days are deleted,
        // so a file whose age is exactly 30 days is retained.
        Assert.True(File.Exists(d30), "a log exactly 30 days old must be retained");
        Assert.False(File.Exists(d30plus), "a log older than 30 days must be deleted");
        Assert.False(File.Exists(d31));
        Assert.False(File.Exists(d90));
    }

    [Fact]
    public void UnrelatedFiles_AreNeverDeleted_EvenWhenOld()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("Logs");
        string foreignLog = MakeLog(dir, "someother.log", TimeSpan.FromDays(400));
        string notes = MakeLog(dir, "notes.txt", TimeSpan.FromDays(400));
        string similar = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}old.txt", TimeSpan.FromDays(400)); // wrong extension

        new ConversionLogger(dir).CleanupOldLogs(DateTime.UtcNow);

        Assert.True(File.Exists(foreignLog));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(similar));
    }

    [Fact]
    public void FilesOutsideTheLogDirectory_AreNeverTouched()
    {
        using var tmp = new TempDir();
        string logs = tmp.Sub("Logs");
        string sibling = tmp.Sub("NotLogs");
        string outside = MakeLog(sibling, $"{ConversionLogger.LogFilePrefix}ancient.log", TimeSpan.FromDays(400));
        string nested = tmp.Sub(Path.Combine("Logs", "sub"));
        string nestedFile = MakeLog(nested, $"{ConversionLogger.LogFilePrefix}nested.log", TimeSpan.FromDays(400));

        new ConversionLogger(logs).CleanupOldLogs(DateTime.UtcNow);

        Assert.True(File.Exists(outside), "sibling directories must never be touched");
        Assert.True(File.Exists(nestedFile), "cleanup must not recurse into subdirectories");
    }

    [Fact]
    public void ReparsePointLogDirectory_IsNotFollowed()
    {
        using var tmp = new TempDir();
        string target = tmp.Sub("RealFolder");
        string victim = MakeLog(target, $"{ConversionLogger.LogFilePrefix}victim.log", TimeSpan.FromDays(400));
        string junction = Path.Combine(tmp.Path, "JunctionLogs");

        // Directory junctions can be created without administrator rights.
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0 || !Directory.Exists(junction))
            return; // environment forbids junction creation; nothing to verify here

        new ConversionLogger(junction).CleanupOldLogs(DateTime.UtcNow);

        Assert.True(File.Exists(victim), "cleanup must never operate through a reparse point");
        Directory.Delete(junction);
    }

    [Fact]
    public void CleanupFailure_DoesNotBlockLogging()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("Logs");
        string locked = MakeLog(dir, $"{ConversionLogger.LogFilePrefix}locked.log", TimeSpan.FromDays(90));
        var logger = new ConversionLogger(dir);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failures = logger.CleanupOldLogs(DateTime.UtcNow);
            Assert.Single(failures); // reported, not thrown
            Assert.True(logger.TryWrite(SampleEntry()), "a cleanup failure must not block logging");
        }
    }

    [Fact]
    public void SuccessLog_HasHashesAndCounts_ButNoInvoiceRecords()
    {
        using var tmp = new TempDir();
        string logs = tmp.Sub("Logs");
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_GHRA_131454.txt");
        byte[] inputBytes = TestUtil.Fixture(@"EDI\EDI_GHRA_131454.txt");
        File.WriteAllBytes(input, inputBytes);

        var outcome = new ConversionService(new ConversionLogger(logs), "1.0-test").ConvertFile(input, dest);
        Assert.True(outcome.Success);

        string logText = File.ReadAllText(Directory.GetFiles(logs, "*.log").Single());
        Assert.Contains("EDI_GHRA_131454.txt", logText);
        Assert.Contains("SSCS_GHRA_131454.txt", logText);
        Assert.Contains("StandardEdi", logText);
        Assert.Contains("\"recordsProcessed\":233", logText);
        Assert.Contains("\"upcERecordsExpanded\":39", logText);
        Assert.Contains("\"inputSha256\"", logText);
        Assert.Contains("\"outputSha256\"", logText);
        Assert.Contains("\"appVersion\":\"1.0-test\"", logText);

        // No complete invoice/product records may appear in the log.
        Assert.DoesNotContain("ALMOND JOY", logText);
        Assert.DoesNotContain("KINGS 4+1", logText);
        Assert.DoesNotContain("SERVICE CHARGE", logText);
    }

    [Fact]
    public void FailureLog_ContainsLineNumberTypeAndReasonCode_ButNoRecordContents()
    {
        using var tmp = new TempDir();
        string logs = tmp.Sub("Logs");
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_BAD.txt");
        File.WriteAllBytes(input, TestUtil.CrlfFile(
            TestUtil.GhraHeader,
            TestUtil.B("04799585508"),
            "D SECRET PRODUCT DATA 123456789"));

        var outcome = new ConversionService(new ConversionLogger(logs), "test").ConvertFile(input, dest);
        Assert.False(outcome.Success);

        string logText = File.ReadAllText(Directory.GetFiles(logs, "*.log").Single());
        Assert.Contains("\"status\":\"Failed\"", logText);
        Assert.Contains("\"reasonCode\":\"UNSUPPORTED_RECORD_TYPE\"", logText);
        Assert.Contains("\"failureLine\":3", logText);
        Assert.DoesNotContain("SECRET PRODUCT DATA", logText);
    }
}
