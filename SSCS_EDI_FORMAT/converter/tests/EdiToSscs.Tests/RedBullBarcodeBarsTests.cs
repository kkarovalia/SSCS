using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Reading Red Bull's product codes off the printed <b>bars</b> rather than off the digits under
/// them.
///
/// The bars are the only thing on a scanned invoice that carries error correction, and they are
/// decoded by code that shares nothing with the OCR, so they fail differently. That is what this
/// vendor has lacked everywhere else — every other "second opinion" on the page is the same
/// mistake twice.
///
/// They are not clean, and the tests here say so as much as they say the opposite. Across the
/// store's twenty tickets: 174 decodes, 168 right, 6 wrong, and the wrong ones satisfied their own
/// UPC-A check digit. That is why the bars are hemmed in — prefix, one-per-row, and never allowed
/// to overrule a reading that already resolved.
/// </summary>
public class RedBullBarcodeBarsTests
{
    private const string Dir = @"Fixtures\PDF";

    private sealed class AnswerNothing : IInvoiceDetailsPrompt
    {
        public bool ConfirmRedBullOcrFallback(string fileName) => true;
        public List<LineFieldRequest> LineFields { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new("TEST-1", new DateOnly(2026, 1, 1));

        /// <summary>Records the question and answers it with the code the paper prints.</summary>
        public Dictionary<int, string> Paper { get; } = [];

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return request.Field == LineField.Barcode
                ? Paper.GetValueOrDefault(request.Position, "611269991000")
                : "53.45";
        }
    }

    private static byte[] Bytes(string fixture) =>
        File.ReadAllBytes(Path.Combine(Dir, $"REDBULL_{fixture}.pdf"));

    private static (ConversionOutcome Outcome, AnswerNothing Prompt) Convert(string fixture)
    {
        var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "ticket.pdf");
        File.WriteAllBytes(path, Bytes(fixture));

        var prompt = new AnswerNothing();
        var outcome = new ConversionService(
                new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt)
            .ConvertFile(
                new QueuedFile { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull },
                tmp.Sub("out"));
        return (outcome, prompt);
    }

    private static List<string> Gtins(ConversionOutcome outcome) =>
        XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("LineItem")
            .Select(li => li.Descendants("InvoiceUnitId").First().Value)
            .ToList();

    /// <summary>
    /// The bars decode, and they decode to what the paper prints — measured on the verified ticket,
    /// whose ten lines SSCS itself confirmed.
    /// </summary>
    [Fact]
    public void TheBarsDecodeToWhatIsPrintedUnderThem()
    {
        var found = PdfBarcodeReader.TryReadAll(Bytes("2030211863_sscs_verified"));

        Assert.NotEmpty(found);
        Assert.All(found, b =>
        {
            Assert.Equal(12, b.Text.Length);
            Assert.True(Upc.IsValidUpcA12(b.Text), $"{b.Text} is not a valid UPC-A");
            Assert.True(b.LineNumber > 0, "a barcode nobody can place on a row is no use");
        });

        // Every code the bars gave up is one this ticket really carries.
        var onThePaper = new[]
        {
            "611269991000", "611269546019", "611269133448", "611269002867", "611269818994",
            "611269182460", "611269001303", "611269003123", "611269321210",
        };
        Assert.All(found.Select(b => b.Text).Distinct(), t => Assert.Contains(t, onThePaper));
    }

    /// <summary>
    /// A PDF with no bars on it — or none that will decode — costs nothing. The reader is a
    /// courtesy on top of a parse that has to stand up without it.
    /// </summary>
    [Fact]
    public void APdfWithNothingToDecodeGivesAnEmptyListRatherThanThrowing()
    {
        Assert.Empty(PdfBarcodeReader.TryReadAll(Bytes("image_only")));
        Assert.Empty(PdfBarcodeReader.TryReadAll([1, 2, 3]));
    }

    /// <summary>
    /// The whole point, on the ticket that shows it best: the scan reads this row's barcode column
    /// as "611269546025", one digit out. The repair rungs would take that to `611269546019` —
    /// RED BULL 16OZ, which the catalogue knows — and book a case of Sugarfree as regular, silently,
    /// because the money reconciles either way.
    ///
    /// The bars say `611269546026`, and the bars are a reading rather than a guess.
    /// </summary>
    [Fact]
    public void TheBarsBeatAGuessedRepairThatWouldHaveBookedTheWrongProduct()
    {
        var (outcome, _) = Convert("2031633958_repair_would_be_wrong");

        Assert.True(outcome.Success, outcome.UserMessage);
        // The ticket carries both 16oz products, one row after the other, and that is exactly what
        // makes the repair dangerous: the two codes are one digit apart and only one is catalogued.
        var gtins = Gtins(outcome);
        Assert.Equal("611269546019", gtins[1]);   // RED BULL 16OZ
        Assert.Equal("611269546026", gtins[2]);   // SUGARFREE 16OZ - the repair would have doubled 546019

        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("taken off the printed bars", StringComparison.Ordinal)
            && w.Message.Contains("611269546026", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two readings of the same printed barcode, each passing its own check digit, landing on
    /// different products: the digits read `611269616026` and the bars decode `611269546026`.
    ///
    /// One of them is a bad read and nothing on the page says which — the money reconciles whichever
    /// goes out. So it is the operator's, and neither reading is taken. This is the case that keeps
    /// the bars honest: they are allowed to beat a *guess*, never another reading.
    /// </summary>
    [Fact]
    public void WhenTheDigitsAndTheBarsDisagreeTheOperatorIsAsked()
    {
        var (outcome, prompt) = Convert("2029327159_bars_disagree");

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields, r => r.Field == LineField.Barcode);
        Assert.Equal("611269616026", asked.PrintedValue);
        Assert.NotNull(asked.Snip);

        var note = Assert.Single(outcome.Warnings, w =>
            w.Message.Contains("bars themselves decode", StringComparison.Ordinal));
        Assert.Contains("611269616026", note.Message, StringComparison.Ordinal);
        Assert.Contains("611269546026", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bars too far gone to decode leave the question exactly where it was. Nothing about this is
    /// meant to remove the last resort — only to stop reaching for it on rows the print can answer.
    /// </summary>
    [Fact]
    public void RowsWhoseBarsWillNotDecodeAreStillAskedAbout()
    {
        var (outcome, prompt) = Convert("2030959925_bars_unreadable");

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.LineFields.Count(r => r.Field == LineField.Barcode));

        // And the rows whose bars did decode were not asked about at all.
        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("taken off the printed bars", StringComparison.Ordinal));
    }

    /// <summary>
    /// A single-SKU ticket, read end to end off the bars: the scan made the barcode column
    /// "611268002867" and the print says 611269002867.
    /// </summary>
    [Fact]
    public void EvenAOneLineTicketGetsItsCodeOffThePrint()
    {
        var (outcome, prompt) = Convert("2029376829_single_line");

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("611269002867", Assert.Single(Gtins(outcome)));
        Assert.Empty(prompt.LineFields.Where(r => r.Field == LineField.Barcode));
    }
}
