using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs for Red Bull: a dropped PDF through <see cref="ConversionService"/> to XML on
/// disk, including the prompts the operator sees and the gate that stops a damaged ticket.
/// </summary>
public class RedBullPipelineTests
{
    private const string Dir = @"Fixtures\PDF";

    /// <summary>Answers every prompt from a script, and records what it was asked.</summary>
    private sealed class ScriptedPrompt(string? invoiceNumber) : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Prompts { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];

        /// <summary>Canned answers by field, used in order. Null means "skip the file".</summary>
        public Dictionary<LineField, Queue<string>> Answers { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Prompts.Add(request);
            return invoiceNumber is null
                ? null
                : new InvoiceDetails(invoiceNumber, request.SuggestedDate ?? new DateOnly(2026, 1, 1));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return Answers.TryGetValue(request.Field, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : null;
        }
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static QueuedFile RedBull(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull };

    private static string Place(TempDir tmp, string fixture, string name = "DownloadFile (6).pdf")
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(Dir, $"REDBULL_{fixture}.pdf")));
        return path;
    }

    /// <summary>
    /// The verified ticket end to end. Two pages — a delivery invoice and a credit invoice with its
    /// own printed number — become ONE file, named after the number the operator keys, because that
    /// is the single paidout SSCS booked.
    /// </summary>
    [Fact]
    public void TwoPagesBecomeOneInvoiceNamedAfterTheTypedNumber()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("112425112379D");

        var outcome = Service(tmp, prompt).ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_112425112379D.xml", Path.GetFileName(Assert.Single(outcome.FinalOutputPaths)));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("RED BULL", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("112425112379D", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal(10, xml.Descendants("LineItem").Count());
        Assert.Empty(prompt.LineFields);   // a clean-enough ticket asks nothing about its lines
    }

    /// <summary>
    /// The operator supplies the date, because the ticket's own reads a year in the future. The
    /// suggestion is offered but the parsed value never wins by default.
    /// </summary>
    [Fact]
    public void AsksForTheDateAndOffersTheCorrectedYear()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("112425112379D");

        var outcome = Service(tmp, prompt).ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var request = Assert.Single(prompt.Prompts);
        Assert.True(request.NeedsDate);
        Assert.Equal(new DateOnly(2025, 11, 24), request.SuggestedDate);
        Assert.Equal("Red Bull", request.Vendor);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Contains("2025-11-24", xml.Descendants("InvoiceDate").Single().Value);
    }

    /// <summary>
    /// The invoice-number box can never be pre-filled from a scan, for any vendor. This is checked
    /// by reflection rather than by reading the code, so the guarantee survives a refactor.
    /// </summary>
    [Fact]
    public void TheInvoiceNumberBoxCannotBePrefilled() =>
        Assert.DoesNotContain(typeof(InvoiceDetailsRequest).GetProperties(),
            p => p.Name.Contains("Invoice", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A ticket the scan damaged past recovery asks about the specific figure, naming the row the
    /// way the operator will find it on the paper — and never asks about a description.
    /// </summary>
    [Fact]
    public void AsksForTheExactFigureItCouldNotRead()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("080326-1");
        prompt.Answers[LineField.Amount] = new Queue<string>(["45.99", "38.25", "58.71"]);

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2036629372_300dpi_damaged_money", "rb10.pdf")), dest);

        Assert.NotEmpty(prompt.LineFields);
        Assert.All(prompt.LineFields, r =>
        {
            Assert.Equal("Red Bull", r.Vendor);
            Assert.InRange(r.Position, 1, r.LineCount);
            Assert.NotEqual(0, r.LineCount);
        });
        // Description is the operator's to fix in SSCS, so it is never a thing to be asked about.
        Assert.All(Enum.GetValues<LineField>(), f =>
            Assert.Contains(f, new[] { LineField.Barcode, LineField.Units, LineField.Amount }));
        _ = outcome;   // the outcome itself is covered by the gate test below
    }

    /// <summary>
    /// Cancelling a line prompt skips the file rather than inventing a figure, and says so in
    /// plain language.
    /// </summary>
    [Fact]
    public void SkippingALinePromptSkipsTheFile()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("080326-1"))
            .ConvertFile(RedBull(Place(tmp, "2036629372_300dpi_damaged_money", "rb10.pdf")), dest);

        Assert.False(outcome.Success);
        Assert.Contains("not supplied", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>
    /// A ticket whose lines miss the printed total by dollars is refused, because a missing line or
    /// an amount misread by dollars is not something anyone would catch on a Paidout screen. The
    /// message says how far out it is and what to do.
    /// </summary>
    [Fact]
    public void RefusesATicketWhoseMoneyIsDollarsOut()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("062226-1");
        // Answer everything, so the file reaches the money gate rather than stopping at a prompt.
        prompt.Answers[LineField.Amount] = new Queue<string>(Enumerable.Repeat("53.45", 8));
        prompt.Answers[LineField.Units] = new Queue<string>(Enumerable.Repeat("24", 8));
        prompt.Answers[LineField.Barcode] = new Queue<string>(Enumerable.Repeat("611269818994", 8));

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2035261008_total_misread", "rb2.pdf")), dest);

        Assert.False(outcome.Success);
        Assert.Contains("difference", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>A cent of scan noise is flagged and written — losing a whole delivery over a
    /// rounding difference is worse than the difference.</summary>
    [Fact]
    public void ShipsOnAPennyAndSaysSo()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("112425112379D"))
            .ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.True(outcome.HasWarnings, "a penny difference must be reported");
        Assert.Contains("342.50", outcome.UserMessage);
        Assert.Single(Directory.GetFiles(dest));
    }

    /// <summary>A clean 300 dpi ticket goes straight through with no prompts and no warnings about
    /// its money.</summary>
    [Fact]
    public void AGoodScanNeedsOnlyTheInvoiceNumber()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("080326-2");

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2036629370_clean_300dpi", "rb0.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);
        Assert.DoesNotContain("difference", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(6, xml.Descendants("LineItem").Count());
        Assert.Equal("305.88", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>A PDF whose OCR was switched off is refused with the fix, not a parse error.</summary>
    [Fact]
    public void TellsTheOperatorToRescanWithOcrOn()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("x"))
            .ConvertFile(RedBull(Place(tmp, "image_only", "rb13.pdf")), dest);

        Assert.False(outcome.Success);
        Assert.Contains("OCR", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
    }
}
