using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>
/// The guard that lets Send Email be a single click with no confirmation dialog: an invoice that
/// has already gone is never sent a second time.
///
/// This matters more than it looks. A duplicate invoice email books the delivery twice in SSCS, the
/// money balances both times, and nothing downstream flags it — so the only place it can be caught
/// is here, before the second send happens.
/// </summary>
public class SendTrackingTests
{
    private static QueuedFile Queued() => new() { InputPath = @"C:\in\ticket.pdf" };

    /// <summary>Mirrors what the Send Email button queues up: every invoice not yet sent.</summary>
    private static List<string> Pending(QueuedFile file, params string[] outputNames) =>
        outputNames.Where(n => !file.WasEmailed(n)).ToList();

    [Fact]
    public void BeforeAnySend_EveryInvoiceIsPending()
    {
        var file = Queued();
        Assert.Equal(2, Pending(file, "SSCS_0331030.xml", "SSCS_0331031.xml").Count);
        Assert.Empty(file.EmailedFileNames);
    }

    [Fact]
    public void AfterSending_ThatInvoiceIsNoLongerPending()
    {
        var file = Queued();

        file.MarkEmailed("SSCS_0331030.xml");

        Assert.Equal("SSCS_0331031.xml",
            Assert.Single(Pending(file, "SSCS_0331030.xml", "SSCS_0331031.xml")));
    }

    [Fact]
    public void PressingSendTwice_SendsNothingTheSecondTime()
    {
        var file = Queued();

        foreach (string name in Pending(file, "SSCS_0331030.xml"))
            file.MarkEmailed(name);

        Assert.Empty(Pending(file, "SSCS_0331030.xml"));
    }

    [Fact]
    public void OnlyTheInvoicesThatSucceeded_AreMarked()
    {
        // A Blue Bell ticket where the Returns half failed: pressing Send again must retry that one
        // and only that one.
        var file = Queued();

        file.MarkEmailed("SSCS_SALES.xml"); // the Returns send failed

        Assert.Equal("SSCS_RETURNS.xml",
            Assert.Single(Pending(file, "SSCS_SALES.xml", "SSCS_RETURNS.xml")));
    }

    [Fact]
    public void MarkingIsCaseInsensitive_SoAFilenameCasingChangeCannotResendAnInvoice()
    {
        var file = Queued();
        file.MarkEmailed("sscs_0331030.XML");
        Assert.True(file.WasEmailed("SSCS_0331030.xml"));
    }

    [Fact]
    public void MarkingTheSameInvoiceTwice_IsHarmless()
    {
        var file = Queued();
        file.MarkEmailed("SSCS_0331030.xml");
        file.MarkEmailed("SSCS_0331030.xml");
        Assert.Single(file.EmailedFileNames);
    }

    [Fact]
    public void TheSentListIsASnapshot_SoTheUiCannotSeeItChangeMidRead()
    {
        var file = Queued();
        file.MarkEmailed("SSCS_1.xml");

        var snapshot = file.EmailedFileNames;
        file.MarkEmailed("SSCS_2.xml");

        Assert.Single(snapshot); // the copy taken earlier is unaffected
        Assert.Equal(2, file.EmailedFileNames.Count);
    }

    [Fact]
    public void MarkingFromManyThreadsAtOnce_LosesNothing()
    {
        // Sends run on a worker thread while the UI reads the same list to repaint rows.
        var file = Queued();
        string[] names = Enumerable.Range(0, 200).Select(i => $"SSCS_{i}.xml").ToArray();

        Parallel.ForEach(names, name => file.MarkEmailed(name));

        Assert.Equal(200, file.EmailedFileNames.Count);
        Assert.Empty(Pending(file, names));
    }

    [Fact]
    public void AFreshFileHasSentNothing()
    {
        Assert.False(Queued().WasEmailed("SSCS_0331030.xml"));
    }

    // ------------------------------------------------ what the queue hands the button

    [Fact]
    public async Task AConvertedFileCarriesItsXmlAndItsSavedPath_WhichIsWhatSendingNeeds()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI13-RubyRedSalesServices.txt");
        File.WriteAllBytes(input, Fixture(@"EDI13\EDI13-RubyRedSalesServices.txt"));

        var service = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test");
        var coordinator = new ConversionCoordinator(service, new FixedDestination(dest));

        var items = coordinator.Enqueue([input]);
        await coordinator.WhenIdleAsync();

        var file = Assert.Single(items);
        var doc = Assert.Single(file.Documents);
        Assert.Equal("SSCS_RubyRedSalesServices.xml", doc.FileName);
        Assert.False(file.WasEmailed(doc.FileName)); // converting does not send

        // Always saved as well as sendable: the attachment is built from these bytes, and the same
        // bytes are on disk, so a failed send never loses the conversion.
        string saved = Assert.Single(file.OutputPaths);
        Assert.True(File.Exists(saved));
        AssertBytesEqual(File.ReadAllBytes(saved), doc.Bytes);
    }

    private sealed class FixedDestination(string folder) : IDestinationProvider
    {
        public string? CurrentFolder => folder;
        public DestinationStatus ValidateCurrent() => DestinationValidator.Validate(folder, TimeSpan.FromSeconds(10));
        public string? RequestNewFolder(string reasonMessage) => null;
        public void SaveFolder(string f) { }
    }
}
