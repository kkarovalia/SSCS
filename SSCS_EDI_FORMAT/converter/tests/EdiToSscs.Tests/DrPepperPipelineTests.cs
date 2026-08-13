using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs for Dr Pepper: a real dropped PDF through <see cref="ConversionService"/> to
/// XML on disk, including the prompt the operator sees.
///
/// The fixture is the 11:29 scan of invoice 5218300220 — the one that arrived with a period inside a
/// barcode, a mark on a units column, and two units misread as 11 for 12. Its text is also pinned in
/// <c>PdfConversionTests</c>; what is proved here is everything around the parser.
/// </summary>
public class DrPepperPipelineTests
{
    private const string Sample = @"Fixtures\PDF\DRPEPPER_5218300220_twice_scanned.pdf";

    /// <summary>
    /// Invoice 4643519283, the store's first Dr Pepper delivery with returns on it. A credit ticket
    /// prints no TOT SALE line at all — it closes with a SALES SUMMARY block — and this one arrived
    /// with a row three columns short, a pen stroke welded to a dollar figure, and a description
    /// letter read as a digit and run into the barcode below it.
    /// </summary>
    private const string CreditSample = @"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf";

    /// <summary>
    /// Answers the prompts the way an operator would, reading each figure off the picture the window
    /// puts in front of them, and records what it was asked.
    ///
    /// The answers are what these two tickets actually print. A question about anything else fails
    /// the test rather than being answered with something plausible, so a change that starts asking
    /// something new shows up here instead of passing quietly.
    /// </summary>
    private sealed class ScriptedPrompt(string? invoiceNumber) : IInvoiceDetailsPrompt
    {
        private static readonly Dictionary<string, string> Paper = new(StringComparer.Ordinal)
        {
            // 5218300220 - "115.95 1 1. 11S.95" under a "PEG 1 1 115.95" subtotal
            ["5GBIB DrPep"] = "1",
            // 5218300220 - both read 11 units where the paper says 12
            ["18z12 Hai CcntMlok"] = "12",
            ["12z12 31m CrspApl"] = "12",
            // 4643519283 - the row that reached the text layer as "20.66 0.00 20.66"
            ["16z12 C4Prf JR GrApl"] = "12",
        };

        public List<InvoiceDetailsRequest> Prompts { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Prompts.Add(request);
            return invoiceNumber is null
                ? null
                : new InvoiceDetails(invoiceNumber, new DateOnly(2026, 8, 10));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return Paper.TryGetValue(request.Description, out string? answer)
                ? answer
                : throw new Xunit.Sdk.XunitException(
                    $"unexpected question: {request.Field} for \"{request.Description}\"");
        }
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static string Place(TempDir tmp, string name = "DR PEPPER 8-10-26.pdf", string source = Sample)
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(source));
        return path;
    }

    private static QueuedFile DrPepper(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

    /// <summary>
    /// The whole ticket, from the file the store actually dropped: 37 lines, 585 units, $1080.22,
    /// filed under the number the operator typed.
    /// </summary>
    [Fact]
    public void ConvertsTheRealTicketTheStoreCouldNotConvertBefore()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp);
        byte[] originalBytes = File.ReadAllBytes(input);

        var outcome = Service(tmp, new ScriptedPrompt("5218300220")).ConvertFile(DrPepper(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("DR PEPPER", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("5218300220", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-08-10", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("1080.22", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(37, xml.Descendants("LineItem").Count());
        Assert.Equal(585, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));

        Assert.Equal(originalBytes, File.ReadAllBytes(input));      // the source is never touched
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
    }

    /// <summary>
    /// Dr Pepper asks for its invoice number like every other vendor, even though the ticket prints
    /// one. That number is the last figure on the page nothing can check — no check digit, no total
    /// to reconcile against, one printed copy — and this scanner does flip digits in short numeric
    /// fields: two scans of this very ticket read its printed TIME as 11:29 and 11:19.
    ///
    /// The same window shows the date and total that WERE read, which is the only check those two
    /// can have, and it lands while the operator is holding the paper.
    /// </summary>
    [Fact]
    public void AsksForTheInvoiceNumberAndShowsWhatItReadForChecking()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("5218300220");

        Service(tmp, prompt).ConvertFile(DrPepper(Place(tmp)), tmp.Sub("out"));

        var request = Assert.Single(prompt.Prompts);
        Assert.Equal("Dr Pepper", request.Vendor);
        Assert.Equal("DR PEPPER 8-10-26.pdf", request.FileName);

        // The date is asked on every invoice, into an empty box: a wrong year is plausible, checks
        // nothing, and files the delivery into the wrong period.
        Assert.True(request.NeedsDate);

        // The total is not: AMOUNT DUE is re-added by every package subtotal, so it checks itself.
        Assert.Equal(1080.22m, request.Total);
        Assert.False(request.NeedsTotal);

        // The invoice number is one window; a figure the scan destroyed is another. They are separate
        // on purpose - the number is always typed, and a row is only asked about when the page cannot
        // settle it. This ticket has three such rows, and they come after the header prompt.
        Assert.Equal(3, prompt.LineFields.Count);
        Assert.All(prompt.LineFields, r => Assert.Equal("DR PEPPER 8-10-26.pdf", r.FileName));
    }

    /// <summary>
    /// The typed number is what the file goes out under, not the one the scan read. Both are on this
    /// ticket, so this is the case that matters: a flipped digit in the printed number would sail
    /// through every other check on the page.
    /// </summary>
    [Fact]
    public void TheTypedNumberIsWhatTheInvoiceGoesOutUnder()
    {
        using var tmp = new TempDir();

        var outcome = Service(tmp, new ScriptedPrompt("081026-DP1"))
            .ConvertFile(DrPepper(Place(tmp)), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("081026-DP1", xml.Descendants("InvoiceNumber").Single().Value);
    }

    /// <summary>
    /// The three quantities this scan damaged are corrected from the invoice's own package subtotals
    /// and each one is reported, because a repaired quantity is a figure a person should glance at
    /// even when the arithmetic is certain.
    /// </summary>
    [Fact]
    public void AsksAboutEveryQuantityItCouldNotRead()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("5218300220");

        var outcome = Service(tmp, prompt).ConvertFile(DrPepper(Place(tmp)), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(
            new[] { "5GBIB DrPep", "18z12 Hai CcntMlok", "12z12 31m CrspApl" },
            prompt.LineFields.Select(r => r.Description).ToArray());

        // Each question shows the printed row with the figure marked, under the ticket's own column
        // names - which is the whole reason the operator can answer it without leaving the screen.
        Assert.All(prompt.LineFields, r =>
        {
            Assert.Equal(LineField.Units, r.Field);
            Assert.Equal("UNITS", r.ColumnName);
            Assert.NotNull(r.Snip);
            Assert.NotNull(r.Header);
        });
    }

    /// <summary>
    /// What was read leads the message, because the Details column shows its first line — so the
    /// invoice number, date and total are on screen against the paper before Send Email.
    /// </summary>
    [Fact]
    public void TheSavedMessageLeadsWithWhatWasRead()
    {
        using var tmp = new TempDir();

        var outcome = Service(tmp, new ScriptedPrompt("5218300220"))
            .ConvertFile(DrPepper(Place(tmp)), tmp.Sub("out"));

        string first = outcome.UserMessage.Split('\n')[0];
        Assert.Contains("5218300220", first, StringComparison.Ordinal);
        Assert.Contains("08-10-2026", first, StringComparison.Ordinal);
        Assert.Contains("1080.22", first, StringComparison.Ordinal);
        Assert.Contains("37 lines", first, StringComparison.Ordinal);
        Assert.Contains("585 units", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// The credit ticket, from the file the store dropped: 34 lines, 544 units, $910.98 — the three
    /// returns going out as negative quantities so the delivery nets to the printed total.
    ///
    /// This one refused outright before. A row that lost three of its six columns went unrecognised,
    /// which took its whole product line with it, and the 33 that were left still reconciled against
    /// each other — so the only thing that noticed was AMOUNT DUE, as a refusal that could not say
    /// which line had gone missing.
    /// </summary>
    [Fact]
    public void ConvertsATicketWithReturnsOnIt()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Place(tmp, "DR PEPPER 8-10-26 (1).pdf", CreditSample);

        var outcome = Service(tmp, new ScriptedPrompt("4643519283")).ConvertFile(DrPepper(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("4643519283", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-08-10", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("910.98", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(34, xml.Descendants("LineItem").Count());

        var qty = xml.Descendants("InvoiceUnitQty").Select(e => int.Parse(e.Value)).ToList();
        Assert.Equal(544, qty.Sum());
        Assert.Equal(3, qty.Count(q => q < 0));      // the returns, as negative quantities

        // The line that used to vanish, and the credit whose barcode had a description digit on it.
        Assert.Contains("842595134971", xml.Descendants("InvoiceUnitId").Select(e => e.Value));
        Assert.Contains("842595136050", xml.Descendants("InvoiceUnitId").Select(e => e.Value));

        // Nothing had to be asked: the package subtotals settled every figure the scan damaged.
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
    }

    /// <summary>
    /// The one repair on that ticket is reported, and the returns are called out — both of them
    /// things a person should glance at against the paper before the invoice is sent.
    /// </summary>
    [Fact]
    public void TheCreditTicketSaysWhatItRepairedAndWhatWentBack()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4643519283");

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DR PEPPER 8-10-26 (1).pdf", CreditSample)), tmp.Sub("out"));

        Assert.True(outcome.HasWarnings);

        // The row that reached the text layer three columns short is the one thing asked about; the
        // rest of the page settles itself.
        var question = Assert.Single(prompt.LineFields);
        Assert.Equal("16z12 C4Prf JR GrApl", question.Description);
        Assert.Equal(LineField.Units, question.Field);

        Assert.Contains("3 credit lines", outcome.UserMessage, StringComparison.Ordinal);
        Assert.Contains("910.98", outcome.UserMessage.Split('\n')[0], StringComparison.Ordinal);
    }

    /// <summary>Dismissing the prompt skips the file: no output, and the original untouched.</summary>
    [Fact]
    public void DismissingThePromptSkipsTheFile()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt(null)).ConvertFile(DrPepper(Place(tmp)), dest);

        Assert.False(outcome.Success);
        Assert.True(outcome.Canceled);
        Assert.Empty(Directory.GetFiles(dest));
    }
}
