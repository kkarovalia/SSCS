using System.Collections.Concurrent;
using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>
/// Testable presentation logic behind the GUI: drop handling, per-file statuses,
/// destination revalidation before every conversion, and the cancel flow.
/// </summary>
public class CoordinatorTests
{
    private sealed class FakeDestination : IDestinationProvider
    {
        public string? CurrentFolder { get; set; }
        public Func<string, string?>? OnRequestNewFolder { get; set; }
        public List<string> SavedFolders { get; } = new();
        public int RequestCount;

        public DestinationStatus ValidateCurrent() => DestinationValidator.Validate(CurrentFolder, TimeSpan.FromSeconds(10));

        public string? RequestNewFolder(string reasonMessage)
        {
            Interlocked.Increment(ref RequestCount);
            LastReason = reasonMessage;
            return OnRequestNewFolder?.Invoke(reasonMessage);
        }

        public string? LastReason;

        public void SaveFolder(string folder)
        {
            SavedFolders.Add(folder);
            CurrentFolder = folder;
        }
    }

    private static (ConversionCoordinator Coordinator, FakeDestination Dest, ConcurrentQueue<(string File, FileStatus Status)> Events, List<string> Summaries)
        Setup(TempDir tmp, string? currentFolder)
    {
        var dest = new FakeDestination { CurrentFolder = currentFolder };
        var service = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test");
        var coordinator = new ConversionCoordinator(service, dest);
        var events = new ConcurrentQueue<(string, FileStatus)>();
        var summaries = new List<string>();
        coordinator.FileChanged += f => events.Enqueue((f.FileName, f.Status));
        coordinator.BatchCompleted += s => { lock (summaries) summaries.Add(s); };
        return (coordinator, dest, events, summaries);
    }

    private static string PlaceFixture(TempDir tmp, string fixture, string asName)
    {
        string path = Path.Combine(tmp.Path, asName);
        File.WriteAllBytes(path, Fixture(fixture));
        return path;
    }

    [Fact]
    public async Task DroppedFile_IsAccepted_AndMovesThroughStatuses()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, events, summaries) = Setup(tmp, dest);
        // Ruby Red is the fixture that converts with nothing to report, so the status lands on
        // Converted rather than Warning.
        string input = PlaceFixture(tmp, @"EDI13\EDI13-RubyRedSalesServices.txt", "EDI13-RubyRedSalesServices.txt");

        coordinator.Enqueue(new[] { input });
        await coordinator.WhenIdleAsync();

        var sequence = events.Where(e => e.File == "EDI13-RubyRedSalesServices.txt").Select(e => e.Status).ToList();
        Assert.Equal(new[] { FileStatus.Waiting, FileStatus.Converting, FileStatus.Converted }, sequence);
        string summary = Assert.Single(summaries);
        Assert.Contains("SSCS_RubyRedSalesServices.xml", summary); // exact output filename
        Assert.Contains(dest, summary);                   // exact output folder
    }

    [Fact]
    public async Task MultipleFilesDroppedTogether_AllConvert()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, events, summaries) = Setup(tmp, dest);
        var inputs = new[]
        {
            PlaceFixture(tmp, @"EDI\EDI_GHRA_131454.txt", "EDI_GHRA_131454.txt"),
            PlaceFixture(tmp, @"EDI\EDI_PEPSI-80471906.txt", "EDI_PEPSI-80471906.txt"),
            PlaceFixture(tmp, @"EDI13\EDI13-RubyRedSalesServices.txt", "EDI13-RubyRedSalesServices.txt"),
        };

        coordinator.Enqueue(inputs);
        await coordinator.WhenIdleAsync();

        // GHRA_131454 has a line with a blank product code and PEPSI's header carries no date, so
        // those two legitimately finish as Warning; all three still converted.
        Assert.Equal(3, events.Count(e => e.Status is FileStatus.Converted or FileStatus.Warning));
        Assert.True(File.Exists(Path.Combine(dest, "SSCS_GHRA_131454.xml")));
        Assert.True(File.Exists(Path.Combine(dest, "SSCS_PEPSI-80471906.xml")));
        Assert.True(File.Exists(Path.Combine(dest, "SSCS_RubyRedSalesServices.xml")));
        Assert.Contains("3 files converted", Assert.Single(summaries));
    }

    [Fact]
    public async Task DroppedFolder_IsRejected_WithFriendlyStatus()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, _, _) = Setup(tmp, dest);

        var items = coordinator.Enqueue(new[] { tmp.Sub("a-folder") });
        await coordinator.WhenIdleAsync();

        var item = Assert.Single(items);
        Assert.Equal(FileStatus.Failed, item.Status);
        Assert.Contains("Folders are not supported", item.Detail);
    }

    [Fact]
    public async Task MixedDrop_FolderRejected_FileConverted()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, _, _) = Setup(tmp, dest);
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");

        var items = coordinator.Enqueue(new[] { tmp.Sub("folder"), input });
        await coordinator.WhenIdleAsync();

        Assert.Equal(FileStatus.Failed, items[0].Status);
        Assert.Equal(FileStatus.Converted, items[1].Status);
    }

    [Fact]
    public async Task InvalidDestination_AsksOnce_ThenConvertsToTheNewFolder()
    {
        using var tmp = new TempDir();
        string replacement = tmp.Sub("replacement");
        var (coordinator, dest, _, _) = Setup(tmp, Path.Combine(tmp.Path, "deleted-folder"));
        dest.OnRequestNewFolder = _ => replacement;
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");

        var items = coordinator.Enqueue(new[] { input });
        await coordinator.WhenIdleAsync();

        Assert.Equal(FileStatus.Converted, items[0].Status);
        Assert.Equal(1, dest.RequestCount);
        Assert.Contains("no longer available", dest.LastReason);
        Assert.Equal(new[] { replacement }, dest.SavedFolders); // replacement persisted
        Assert.True(File.Exists(Path.Combine(replacement, "SSCS_GHRA_118955.xml")));
    }

    [Fact]
    public async Task InvalidDestination_UserCancels_MarksConversionsCanceled_AndSourcesUntouched()
    {
        using var tmp = new TempDir();
        var (coordinator, dest, _, summaries) = Setup(tmp, Path.Combine(tmp.Path, "deleted-folder"));
        dest.OnRequestNewFolder = _ => null; // user cancels the dialog
        string input1 = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");
        string input2 = PlaceFixture(tmp, @"EDI\EDI_GHRA_125482.txt", "EDI_GHRA_125482.txt");
        byte[] original1 = File.ReadAllBytes(input1);

        var items = coordinator.Enqueue(new[] { input1, input2 });
        await coordinator.WhenIdleAsync();

        Assert.All(items, i => Assert.Equal(FileStatus.Canceled, i.Status));
        Assert.Equal(original1, File.ReadAllBytes(input1));
        Assert.Contains("2 canceled", Assert.Single(summaries));
    }

    [Fact]
    public async Task UnusableReplacementFolder_IsRefused_AndAskedAgain()
    {
        using var tmp = new TempDir();
        string good = tmp.Sub("good");
        var (coordinator, dest, _, _) = Setup(tmp, Path.Combine(tmp.Path, "deleted-folder"));
        int calls = 0;
        dest.OnRequestNewFolder = _ => ++calls == 1 ? Path.Combine(tmp.Path, "also-missing") : good;
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");

        var items = coordinator.Enqueue(new[] { input });
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, calls); // the bad pick was validated, refused, and the user was asked again
        Assert.Equal(FileStatus.Converted, items[0].Status);
        Assert.Equal(new[] { good }, dest.SavedFolders);
    }

    [Fact]
    public async Task WarningFile_GetsWarningStatus()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, _, _) = Setup(tmp, dest);
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_129348.txt", "EDI_GHRA_129348.txt");

        var items = coordinator.Enqueue(new[] { input });
        await coordinator.WhenIdleAsync();

        Assert.Equal(FileStatus.Warning, items[0].Status);
        Assert.Contains("57026404", items[0].Detail);
    }

    [Fact]
    public async Task FailingFile_GetsFailedStatus_OthersStillConvert()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, _, summaries) = Setup(tmp, dest);
        string bad = Path.Combine(tmp.Path, "EDI_BAD.txt");
        File.WriteAllText(bad, "not an edi file at all");
        string good = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");

        var items = coordinator.Enqueue(new[] { bad, good });
        await coordinator.WhenIdleAsync();

        Assert.Equal(FileStatus.Failed, items[0].Status);
        Assert.Equal(FileStatus.Converted, items[1].Status);
        string summary = Assert.Single(summaries);
        Assert.Contains("1 file converted", summary);
        Assert.Contains("1 failed", summary);
    }

    [Fact]
    public async Task FilesEnqueuedWhileConverting_AreProcessedToo()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var (coordinator, _, _, _) = Setup(tmp, dest);
        string a = PlaceFixture(tmp, @"EDI\EDI_GHRA_120511.txt", "EDI_GHRA_120511.txt");
        string b = PlaceFixture(tmp, @"EDI\EDI_GHRA_129943.txt", "EDI_GHRA_129943.txt");

        var first = coordinator.Enqueue(new[] { a });
        var second = coordinator.Enqueue(new[] { b });
        await coordinator.WhenIdleAsync();
        // A second Enqueue may race past the first worker's drain; wait for any restarted worker.
        await coordinator.WhenIdleAsync();

        Assert.True(File.Exists(Path.Combine(dest, "SSCS_GHRA_120511.xml")));
        Assert.True(File.Exists(Path.Combine(dest, "SSCS_GHRA_129943.xml")));
        Assert.Equal(FileStatus.Converted, first[0].Status);
        Assert.Equal(FileStatus.Converted, second[0].Status);
    }

    /// <summary>
    /// A converter that says what it is waiting on has that shown on the row it is waiting for, and
    /// never over the outcome.
    ///
    /// Before this, a picture-primary invoice showed "Converting..." for the whole of three network
    /// reads. A late line — a barcode pass finishing after the file was written — must not land on
    /// top of the message the operator actually needs.
    /// </summary>
    private sealed class TalkativeConverter : IFileConverter, IProgressReportingConverter
    {
        public event Action<string>? Progress;

        public ConversionOutcome ConvertFile(QueuedFile file, string destinationFolder)
        {
            Progress?.Invoke("Reading the invoice - try 1 of 8 sent (key 1, m). No reply yet.");
            Progress?.Invoke("Reading the invoice - try 1 of 8: Google's AI is busy.");
            return new ConversionOutcome { Success = true, UserMessage = "Converted 1 invoice." };
        }

        public void SaySomethingLate() => Progress?.Invoke("Checking the barcodes - try 2 of 8 sent.");
    }

    [Fact]
    public async Task ConverterProgressIsShownOnTheConvertingRowAndNeverOverTheOutcome()
    {
        using var tmp = new TempDir();
        var dest = new FakeDestination { CurrentFolder = tmp.Sub("out") };
        var converter = new TalkativeConverter();
        var coordinator = new ConversionCoordinator(converter, dest);
        var details = new ConcurrentQueue<string>();
        coordinator.FileChanged += f => details.Enqueue(f.Detail);
        string input = PlaceFixture(tmp, @"EDI\EDI_GHRA_118955.txt", "EDI_GHRA_118955.txt");

        var items = coordinator.Enqueue(new[] { input });
        await coordinator.WhenIdleAsync();
        converter.SaySomethingLate();

        Assert.Contains(details, d => d.Contains("try 1 of 8 sent", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains("Google's AI is busy", StringComparison.Ordinal));
        Assert.Equal("Converted 1 invoice.", items[0].Detail);
    }
}
