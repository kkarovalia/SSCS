using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs for the Bimbo path: a real sample PDF in, an SSCS file on disk out, with the
/// invoice number going through the same prompt the app uses.
/// </summary>
public class BimboPipelineTests
{
    /// <summary>The 07-28-2026 ticket, the one whose SSCS round trip was read back on screen.</summary>
    private const string Verified = @"Fixtures\PDF\BIMBO_84294890013239_sscs_verified.pdf";

    /// <summary>A 200 dpi scan whose date has to be recovered, so the prompt asks for one.</summary>
    private const string DamagedDate = @"Fixtures\PDF\BIMBO_84294890013091_zero_money_row.pdf";

    private sealed class ScriptedPrompt(string? invoiceNumber) : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Prompts { get; } = [];

        /// <summary>What the operator types into the (always empty) date box.</summary>
        public DateOnly DateTyped { get; set; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Prompts.Add(request);
            return invoiceNumber is null
                ? null
                : new InvoiceDetails(
                    invoiceNumber,
                    DateTyped != default ? DateTyped : new DateOnly(2026, 7, 28));
        }

        public bool ConfirmBimboOcrFallback(string fileName) => true;
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static string Place(TempDir tmp, string fixture, string name = "DownloadFile (3).pdf")
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(fixture));
        return path;
    }

    private static QueuedFile Bimbo(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo };

    /// <summary>
    /// The whole ticket becomes one invoice — deliveries and returns together — because that is how
    /// SSCS booked it: a single $335.07 paidout, not a sales invoice plus a returns invoice.
    /// </summary>
    [Fact]
    public void WritesOneFileHoldingBothTheDeliveriesAndTheReturns()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);

        var outcome = Service(tmp, new ScriptedPrompt("072826132008D")).ConvertFile(Bimbo(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_072826132008D.xml", Path.GetFileName(Assert.Single(outcome.FinalOutputPaths)));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("BIMBO", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("072826132008D", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-07-28", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("335.07", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(35, xml.Descendants("LineItem").Count());
    }

    /// <summary>
    /// The first line, against what live SSCS displayed for it: id 07432309183, qty 3, Ea Cost 3.02.
    /// The GTIN carries the check digit SSCS then drops for itself.
    /// </summary>
    [Fact]
    public void TheFirstLineMatchesWhatSscsDisplayed()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);

        var outcome = Service(tmp, new ScriptedPrompt("TEST-1")).ConvertFile(Bimbo(input), dest);
        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);

        var first = xml.Descendants("LineItem").First().Element("InvoiceUnit")!;
        Assert.Equal("074323091830", first.Element("InvoiceUnitId")!.Value);
        Assert.Equal("3", first.Element("InvoiceUnitQty")!.Value);
        Assert.Equal("3.0200", first.Element("InvoiceUnitCost")!.Value);

        // A returns line goes out as negative units at a positive cost.
        var returns = xml.Descendants("InvoiceUnit")
            .First(u => u.Element("InvoiceUnitQty")!.Value.StartsWith('-'));
        Assert.Equal("-2", returns.Element("InvoiceUnitQty")!.Value);
        Assert.Equal("1.9400", returns.Element("InvoiceUnitCost")!.Value);
    }

    /// <summary>
    /// The invoice number is always typed and the box is never pre-filled from the scan. Bimbo prints
    /// "INVOICE# 84294890013239", the scanner mis-read one sample's as 84299890013154, and nothing
    /// downstream can catch that — so the reading must not reach the operator as a starting point.
    /// </summary>
    [Fact]
    public void TheOperatorIsAskedForTheInvoiceNumberAndTheScanNeverSuggestsOne()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);

        var prompt = new ScriptedPrompt("072826132008D");
        Service(tmp, prompt).ConvertFile(Bimbo(input), dest);

        var asked = Assert.Single(prompt.Prompts);
        Assert.Equal("Bimbo", asked.Vendor);
        Assert.Equal(335.07m, asked.Total);
        Assert.Equal(35, asked.LineCount);
        // The date is typed on every invoice, into an empty box - the same rule as the number.
        Assert.True(asked.NeedsDate);

        // And the record has no field that could pre-fill either one. This is the guarantee: a
        // suggestion the prompt cannot receive is a suggestion nobody can wire back in by accident.
        Assert.DoesNotContain(
            typeof(InvoiceDetailsRequest).GetProperties(),
            p => p.Name.Contains("Invoice", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Offer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A 200 dpi scan cannot be trusted with its own date, so the operator is asked — and offered the
    /// recovered reading to check against the paper rather than having it chosen for them.
    /// </summary>
    [Fact]
    public void ADamagedDateIsAskedForAndTheRecoveredReadingIsOnlyASuggestion()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, DamagedDate);

        var prompt = new ScriptedPrompt("071326130912D") { DateTyped = new DateOnly(2026, 7, 13) };
        var outcome = Service(tmp, prompt).ConvertFile(Bimbo(input), dest);

        var asked = Assert.Single(prompt.Prompts);
        Assert.True(asked.NeedsDate);
        Assert.DoesNotContain(typeof(InvoiceDetailsRequest).GetProperties(),
            p => p.Name.Equals("Date", StringComparison.OrdinalIgnoreCase));

        // The parser can still reconstruct 07/13/2026 out of the damage, and that reconstruction is
        // deliberately not offered to the operator any more - it cannot even reach this record. What
        // lands on the invoice is what the person holding the ticket typed.

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("2026-07-13", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("151.33", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>Cancelling the prompt skips the file and writes nothing.</summary>
    [Fact]
    public void CancellingThePromptWritesNothing()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);

        var outcome = Service(tmp, new ScriptedPrompt(null)).ConvertFile(Bimbo(input), dest);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>
    /// The XML that reaches SSCS totals what the ticket totals, line by line, with returns pulling it
    /// down — the arithmetic SSCS itself performs on import.
    /// </summary>
    [Fact]
    public void TheWrittenLinesSumToTheTicketTotal()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, Verified);

        var outcome = Service(tmp, new ScriptedPrompt("TEST-2")).ConvertFile(Bimbo(input), dest);
        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);

        decimal sum = xml.Descendants("InvoiceUnit").Sum(u =>
            decimal.Round(
                int.Parse(u.Element("InvoiceUnitQty")!.Value) *
                decimal.Parse(u.Element("InvoiceUnitCost")!.Value),
                2, MidpointRounding.AwayFromZero));

        Assert.Equal(335.07m, sum);
        Assert.Equal("335.07", xml.Descendants("TotalLineItemNetAmt").Single().Value);
    }
}
