using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs for the Chubiwinos path: a real sample PDF in, an SSCS file on disk out, with
/// the two things a person has to supply going through the same prompt the app uses.
/// </summary>
public class ChubiwinosPipelineTests
{
    private const string Verified = @"Fixtures\PDF\CHUBIWINOS_6V9389.pdf";   // the SSCS round trip
    private const string NeedsBarcodes = @"Fixtures\PDF\CHUBIWINOS_6V9244.pdf";

    /// <summary>
    /// Answers the prompts the way an operator would. Both answers are recorded so a test can check
    /// what the operator was actually shown before being asked.
    /// </summary>
    private sealed class ScriptedPrompt(string? invoiceNumber, params string?[] barcodes) : IInvoiceDetailsPrompt
    {
        private int _next;
        public List<InvoiceDetailsRequest> InvoicePrompts { get; } = [];
        public List<BarcodeRequest> BarcodePrompts { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            InvoicePrompts.Add(request);
            return invoiceNumber is null ? null : new InvoiceDetails(invoiceNumber, request.SuggestedDate ?? default);
        }

        public string? AskBarcode(BarcodeRequest request)
        {
            BarcodePrompts.Add(request);
            return _next < barcodes.Length ? barcodes[_next++] : null;
        }
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static string Place(TempDir tmp, string fixture)
    {
        // Chubiwinos invoices arrive from a mail client under names that say nothing about them.
        string path = Path.Combine(tmp.Path, "DownloadFile (1).pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(fixture));
        return path;
    }

    private static QueuedFile Chubiwinos(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos };

    [Fact]
    public void WritesOneFileNamedAfterTheInvoiceNumberTheOperatorTyped()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);
        byte[] originalBytes = File.ReadAllBytes(input);

        // The number the store actually books this delivery under, which is not what the PDF prints.
        var outcome = Service(tmp, new ScriptedPrompt("061526070552D")).ConvertFile(Chubiwinos(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_061526070552D.xml", Path.GetFileName(Assert.Single(outcome.FinalOutputPaths)));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("CHUBIWINOS", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("061526070552D", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-06-15", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("460.23", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(8, xml.Descendants("LineItem").Count());

        // The credit went out as negative units at a positive cost.
        Assert.Equal("-1", xml.Descendants("InvoiceUnitQty").Last().Value);
        Assert.Equal("2.7900", xml.Descendants("InvoiceUnitCost").Last().Value);

        Assert.Equal(originalBytes, File.ReadAllBytes(input));
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
    }

    /// <summary>
    /// The prompt shows enough to find the right paper — which invoice, what it totals, how many
    /// lines — so that the operator is checking a document rather than answering a bare question.
    /// </summary>
    [Fact]
    public void ThePromptShowsWhichPaperToPickUp()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("061526070552D");

        Service(tmp, prompt).ConvertFile(Chubiwinos(Place(tmp, Verified)), tmp.Sub("out"));

        var request = Assert.Single(prompt.InvoicePrompts);
        Assert.Equal("DownloadFile (1).pdf", request.FileName);
        Assert.Equal(460.23m, request.Total);
        Assert.Equal(8, request.LineCount);
        Assert.Equal(new DateOnly(2026, 6, 15), request.Date);
        // Always a box, pre-filled with what the ticket printed.
        Assert.True(request.NeedsDate);
        Assert.Equal(new DateOnly(2026, 6, 15), request.DateToOffer);
    }

    /// <summary>
    /// No invoice number the scan produced may ever reach the prompt, for ANY vendor. The scanners
    /// get it wrong silently — a Blue Bell ticket's text layer says 033105035263 where the paper
    /// reads 033103035263, and Chubiwinos numbers came back as "0V9293" and "6)9141" — and the number
    /// carries no check digit, so nothing downstream can catch it. A pre-filled box would turn the
    /// one field that needs a person's eyes into an Enter keypress.
    ///
    /// This is enforced by the request type carrying no such field at all, rather than by every
    /// caller remembering to pass null, which is why the check is on the type. A parser may still
    /// read a number off the scan for its own purposes — BlueBellSection.SuggestedInvoiceNumber
    /// exists and is tested — it just has nowhere to hand it to.
    /// </summary>
    [Fact]
    public void NoScannedInvoiceNumberCanReachThePromptForAnyVendor()
    {
        var offered = typeof(InvoiceDetailsRequest)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("Invoice", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Suggested", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(["SuggestedDate"], offered);
    }

    /// <summary>
    /// The two products with no trustworthy code anywhere are asked for, named the way the operator
    /// will find them on the paper — and nothing else is.
    /// </summary>
    [Fact]
    public void OnlyTheLinesWithNoTrustworthyBarcodeAreAskedAbout()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("050126A", "850883002062", "6-50156-00733-4");

        var outcome = Service(tmp, prompt).ConvertFile(Chubiwinos(Place(tmp, NeedsBarcodes)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.BarcodePrompts.Count);

        var first = prompt.BarcodePrompts[0];
        Assert.Equal("905", first.ItemNumber);
        Assert.Equal(8, first.Position);
        Assert.Equal(9, first.LineCount);
        Assert.Equal(12, first.Units);
        Assert.Equal(2.49m, first.UnitCost);
        Assert.Equal("85088300206", first.PrintedCode);

        // The typed codes went out as typed, dashes and all removed, and nothing else moved.
        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        var ids = xml.Descendants("InvoiceUnitId").Select(e => e.Value).ToList();
        Assert.Equal("850883002062", ids[7]);
        Assert.Equal("650156007334", ids[8]);
        Assert.Equal("452.90", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>An 11-digit body is completed rather than refused; the missing digit is arithmetic.</summary>
    [Fact]
    public void AnElevenDigitBodyTypedInGetsItsCheckDigitBack()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("050126A", "85088300206", "650156007334");

        var outcome = Service(tmp, prompt).ConvertFile(Chubiwinos(Place(tmp, NeedsBarcodes)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var ids = XDocument.Load(outcome.FinalOutputPaths[0]).Descendants("InvoiceUnitId").Select(e => e.Value).ToList();
        Assert.Equal("850883002062", ids[7]);
        Assert.True(Upc.IsValidGtin(ids[7]));
    }

    [Fact]
    public void CancellingTheInvoiceNumberPromptWritesNothingAndIsNotAFailure()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt(null)).ConvertFile(Chubiwinos(Place(tmp, Verified)), dest);

        Assert.False(outcome.Success);
        Assert.True(outcome.Canceled);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>
    /// Skipping a barcode prompt loses the whole file. A line with no barcode has nowhere safe to go,
    /// and an invoice short of a line would book the wrong money.
    /// </summary>
    [Fact]
    public void SkippingABarcodePromptWritesNothing()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("050126A", "850883002062", null))
            .ConvertFile(Chubiwinos(Place(tmp, NeedsBarcodes)), dest);

        Assert.True(outcome.Canceled);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public void ChubiwinosPdfDroppedUnderAnotherVendorIsRefusedRatherThanMisread()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);
        var file = new QueuedFile { InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell };

        var outcome = Service(tmp, new ScriptedPrompt("061526070552D")).ConvertFile(file, dest);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(dest));
    }
}
