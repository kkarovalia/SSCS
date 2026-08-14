using System.Xml.Linq;
using EdiToSscs.Core;
using static EdiToSscs.Tests.TestUtil;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs for the Blue Bell path, run against a real sample PDF.
///
/// A Blue Bell delivery ticket carries a Sales block and a Returns block with separate printed
/// invoice numbers, and it produces ONE file: live SSCS holds this very fixture as a single $325.50
/// paidout of seven lines. The separate number identifies a document, not a payable.
/// </summary>
public class BlueBellPipelineTests
{
    private const string SampleInvoice = @"Fixtures\PDF\BLUEBELL_033100786112.pdf";

    /// <summary>
    /// Answers the prompt the way an operator would, in order: an invoice number, or null to skip
    /// the file. The date is the value the operator reads from this fixture's paper.
    /// </summary>
    private sealed class ScriptedPrompt(params string?[] answers) : IInvoiceDetailsPrompt
    {
        private int _next;
        public List<InvoiceDetailsRequest> Seen { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Seen.Add(request);
            string? number = _next < answers.Length ? answers[_next++] : null;
            return number is null ? null
                : new InvoiceDetails(number, new DateOnly(2026, 3, 19));
        }

        // These tests intentionally prove the unchanged legacy parser against its OCR fixtures.
        public bool ConfirmBlueBellOcrFallback(string fileName) => true;
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static string PlaceSample(TempDir tmp)
    {
        string path = Path.Combine(tmp.Path, "blue bell ticket.pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(SampleInvoice));
        return path;
    }

    private static QueuedFile BlueBell(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell };

    /// <summary>
    /// Sales and Returns are ONE payable, and this fixture is the ticket that proves it: the store's
    /// live SSCS holds it as invoice 031926111202D — a single $325.50 paidout of seven lines, dated
    /// 3/19/2026, with Total Qty 147.
    ///
    /// It used to write two files, on the reasoning that Blue Bell prints a separate invoice number
    /// on the Returns block. The printed number is real; the second payable is not.
    /// </summary>
    [Fact]
    public void OneTicketWithSalesAndReturns_WritesTheSingleInvoiceSscsBooks()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceSample(tmp);
        byte[] originalBytes = File.ReadAllBytes(input);
        var prompt = new ScriptedPrompt("031926111202D");

        var outcome = Service(tmp, prompt).ConvertFile(BlueBell(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_031926111202D.xml",
            Path.GetFileName(Assert.Single(outcome.FinalOutputPaths)));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("BLUE BELL", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("2026-03-19", xml.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("325.50", xml.Descendants("TotalInvoiceDueAmt").Single().Value);   // 328.48 - 2.98
        Assert.Equal(7, xml.Descendants("LineItem").Count());
        Assert.Equal(147, xml.Descendants("InvoiceUnitQty").Sum(q => int.Parse(q.Value)));

        // The return is a negative line on the same invoice, not an invoice of its own.
        Assert.Equal("-1", Assert.Single(xml.Descendants("InvoiceUnitQty")
            .Where(q => q.Value.StartsWith('-'))).Value);

        // One prompt for the whole ticket, showing the figure the operator will see on the Paidout.
        var seen = Assert.Single(prompt.Seen);
        Assert.Equal(325.50m, seen.Total);
        Assert.Equal(1, seen.SectionCount);
        Assert.Equal("Blue Bell", seen.Vendor);

        // The source PDF is untouched and nothing temporary is left behind.
        Assert.Equal(originalBytes, File.ReadAllBytes(input));
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
    }

    /// <summary>
    /// The seven ID/PLUs, quantities and Ea Costs the store's Line Items tab shows for this ticket.
    /// A change that breaks any of these creates duplicate items or wrong money in the live system.
    /// </summary>
    public static TheoryData<string, int, decimal> LiveSscsLineItems => new()
    {
        // ID/PLU,      Qty,  Ea Cost
        { "07189903720", 12, 6.85m },
        { "07189903733",  4, 6.85m },
        { "07189905101", 60, 2.98m },
        { "07189921133", 24, 0.53m },
        { "07189921122", 24, 0.57m },
        { "07189921123", 24, 0.57m },
        { "07189905101", -1, 2.98m },
    };

    [Theory]
    [MemberData(nameof(LiveSscsLineItems))]
    public void EachLineLandsOnTheItemSscsShows(string sscsId, int qty, decimal eaCost)
    {
        var sections = BlueBellPdfParser.ParseAll(
            PdfTextExtractor.ExtractLines(File.ReadAllBytes(SampleInvoice)), []);
        var invoice = BlueBellPdfParser.Merge(sections, "031926111202D", default);

        Assert.Contains(invoice.Lines, l =>
            l.Gtin[..^1] == sscsId && l.Units == qty && l.UnitCost == eaCost);
    }

    [Fact]
    public void CancellingThePrompt_WritesNothingAndIsNotAFailure()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceSample(tmp);

        var outcome = Service(tmp, new ScriptedPrompt([null])).ConvertFile(BlueBell(input), dest);

        Assert.False(outcome.Success);
        Assert.True(outcome.Canceled);
        Assert.Contains("Canceled", outcome.UserMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dest));
        Assert.True(File.Exists(input));
    }

    /// <summary>
    /// The whole ticket is asked about once, so there is no way to write the Sales charge while its
    /// Returns credit goes missing — which would have overstated what the store owes.
    /// </summary>
    [Fact]
    public void TheWholeTicketIsAskedAboutOnce()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("031926111202D", "a second answer that must never be used");

        var outcome = Service(tmp, prompt).ConvertFile(BlueBell(PlaceSample(tmp)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Single(prompt.Seen);
        Assert.Single(Directory.GetFiles(dest));
    }

    [Fact]
    public void BlueBellPdfDroppedUnderDrPepper_IsRefusedRatherThanMisread()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceSample(tmp);
        var file = new QueuedFile { InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

        var outcome = Service(tmp, new ScriptedPrompt("033100786112")).ConvertFile(file, dest);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public void ExistingOutputNameIsNeverOverwritten()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = PlaceSample(tmp);
        File.WriteAllText(Path.Combine(dest, "SSCS_033100786112.xml"), "an earlier conversion");

        var outcome = Service(tmp, new ScriptedPrompt("033100786112", "033100786113"))
            .ConvertFile(BlueBell(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("an earlier conversion", File.ReadAllText(Path.Combine(dest, "SSCS_033100786112.xml")));
        Assert.True(File.Exists(Path.Combine(dest, "SSCS_033100786112 (1).xml")));
    }

    /// <summary>
    /// What live SSCS did with both candidate barcode forms, verified 2026-07-26 by importing a
    /// two-line test invoice into BEST STOP #14 and reading the Line Items tab back:
    ///
    ///   sent 007189905101 (the printed column)  -> ID/PLU 00718990510, flagged "New Item",
    ///                                              department "Unknown items"
    ///   sent 071899051015 (check digit restored) -> ID/PLU 07189905101, matched the store's
    ///                                              existing item, department "ICE CREAM"
    ///
    /// That is the whole reason ToGtin exists, and the reason it must not be "simplified" into
    /// passing the printed code straight through. SSCS also showed Ea List 4.99 and Margin 40.28%
    /// off a 2.98 cost, confirming the 40%-margin retail seed.
    /// </summary>
    [Fact]
    public void MatchesWhatLiveSscsDidWithBothBarcodeForms()
    {
        Assert.Equal("00718990510", Upc.SscsIdFromGtin("007189905101"));   // became a new item
        Assert.Equal("07189905101", Upc.SscsIdFromGtin("071899051015"));   // matched the real item

        var section = Assert.Single(BlueBellPdfParser.ParseAll(
            PdfTextExtractor.ExtractLines(File.ReadAllBytes(SampleInvoice)).Take(40).ToList(), []));
        var ptGrSupreme = section.Lines.Single(l => l.Gtin == "071899051015");

        Assert.Equal("07189905101", Upc.SscsIdFromGtin(ptGrSupreme.Gtin));
        Assert.Equal(2.98m, ptGrSupreme.UnitCost);
        Assert.Equal(4.99m, ptGrSupreme.RetailPrice);   // Ea List SSCS displayed

        // Margin SSCS computed from those two figures, to the hundredth of a percent.
        decimal margin = (ptGrSupreme.RetailPrice - ptGrSupreme.UnitCost) / ptGrSupreme.RetailPrice * 100m;
        Assert.Equal(40.28m, decimal.Round(margin, 2));
    }

    /// <summary>The EDI path never prompts, so it must keep working with no prompt supplied.</summary>
    [Fact]
    public void EdiConversionStillNeedsNoPrompt()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_GHRA_131454.txt");
        File.WriteAllBytes(input, Fixture(@"EDI\EDI_GHRA_131454.txt"));

        var outcome = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test")
            .ConvertFile(input, dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Single(outcome.FinalOutputPaths);
    }
}
