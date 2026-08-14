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
        public bool ConfirmRedBullOcrFallback(string fileName) => true;
        public List<InvoiceDetailsRequest> Prompts { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];

        /// <summary>Canned answers by field, used in order. Null means "skip the file".</summary>
        public Dictionary<LineField, Queue<string>> Answers { get; } = [];

        /// <summary>
        /// What the operator types into the date box. There is no pre-fill any more, so a test that
        /// cares about the date has to supply one the way a person would - off the paper.
        /// </summary>
        public DateOnly DateTyped { get; set; }

        /// <summary>Simulates the operator pressing OK with the date box still empty.</summary>
        public bool LeavesTheDateBlank { get; set; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Prompts.Add(request);
            if (invoiceNumber is null) return null;
            DateOnly typed =
                LeavesTheDateBlank ? default
                : DateTyped != default ? DateTyped
                : new DateOnly(2026, 1, 1);
            return new InvoiceDetails(invoiceNumber, typed);
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

    /// <summary>
    /// The verified ticket, answered from the paper. Two of its money columns are damaged with
    /// nothing on their row left to check them against, so both are asked about; the paper prints
    /// $106.90 and ($4.45), which is also what live SSCS holds for those two lines.
    /// </summary>
    private static ScriptedPrompt VerifiedTicketPrompt()
    {
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Answers[LineField.Amount] = new Queue<string>(["106.90", "4.45"]);
        return prompt;
    }

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
        var prompt = VerifiedTicketPrompt();

        var outcome = Service(tmp, prompt).ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SSCS_112425112379D.xml", Path.GetFileName(Assert.Single(outcome.FinalOutputPaths)));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal("RED BULL", xml.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("112425112379D", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal(10, xml.Descendants("LineItem").Count());

        // Exactly the two money columns the scan destroyed, and nothing else.
        Assert.Equal(2, prompt.LineFields.Count);
        Assert.All(prompt.LineFields, r => Assert.Equal(LineField.Amount, r.Field));
    }

    /// <summary>
    /// The operator supplies the date, because the ticket's own reads a year in the future. The
    /// suggestion is offered but the parsed value never wins by default.
    /// </summary>
    [Fact]
    public void AsksForTheDateEveryTimeAndUsesWhatWasTyped()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = VerifiedTicketPrompt();
        prompt.DateTyped = new DateOnly(2025, 11, 24);   // read off the paper, as it now must be

        var outcome = Service(tmp, prompt).ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var request = Assert.Single(prompt.Prompts);
        Assert.True(request.NeedsDate);
        Assert.Equal("Red Bull", request.Vendor);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Contains("2025-11-24", xml.Descendants("InvoiceDate").Single().Value);
    }

    /// <summary>
    /// And with nothing typed the file is refused rather than dated from the scan.
    ///
    /// This is the half that makes the rest mean anything. The date carries no check digit and is
    /// printed once, so a fallback to the scan's own reading is not a safety net - it is the old
    /// behaviour with an extra window in front of it, and it is what let an invoice go out dated
    /// 2016 on 2026-08-12.
    /// </summary>
    [Fact]
    public void ATicketWithNoTypedDateIsRefused()
    {
        using var tmp = new TempDir();
        var prompt = VerifiedTicketPrompt();
        prompt.LeavesTheDateBlank = true;

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2030211863_sscs_verified")), tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Equal(PdfReasonCodes.NoDate, outcome.ReasonCode);
        Assert.Contains("has to be typed in", outcome.UserMessage, StringComparison.Ordinal);
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
        Assert.All(Enum.GetValues<LineField>().Where(f => f != LineField.UnitPrice), f =>
            Assert.Contains(f, new[] { LineField.Barcode, LineField.Units, LineField.Amount }));
        _ = outcome;   // the outcome itself is covered by the gate test below
    }

    /// <summary>
    /// Cancelling a line prompt skips the file rather than inventing a figure, and says so in
    /// plain language.
    ///
    /// Skipped is <b>Canceled</b>, not Failed. Pressing "Skip this file" is a decision, exactly
    /// like dismissing the invoice-number prompt, and it used to come back red with a scan-damage
    /// reason code attached — which is how a queue ends up showing somebody their own click as a
    /// fault in the paper, with no way to tell it from an invoice the app genuinely refused.
    /// </summary>
    [Fact]
    public void SkippingALinePromptSkipsTheFile()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("080326-1"))
            .ConvertFile(RedBull(Place(tmp, "2036629372_300dpi_damaged_money", "rb10.pdf")), dest);

        Assert.False(outcome.Success);
        Assert.True(outcome.Canceled);
        Assert.Null(outcome.ReasonCode);
        Assert.Contains("dismissed", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>
    /// A ticket whose lines miss the printed total by dollars is not thrown away — every amount
    /// nobody has looked at is shown, and the file is written with the disagreement said out loud.
    ///
    /// This is the worst-scanned sample there is: it loses a whole product row, so no amount of
    /// asking will make it balance. What matters is that the operator ends up holding a converted
    /// file and a note telling them a line may be missing, rather than a refusal and no way forward.
    /// </summary>
    [Fact]
    public void ATicketThatCannotBalanceIsStillWrittenWithTheProblemSaidOutLoud()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("062226-1");
        // Answer everything, so the file reaches the money gate rather than stopping at a prompt.
        prompt.Answers[LineField.Amount] = new Queue<string>(Enumerable.Repeat("53.45", 30));
        prompt.Answers[LineField.Units] = new Queue<string>(Enumerable.Repeat("24", 30));
        prompt.Answers[LineField.Barcode] = new Queue<string>(Enumerable.Repeat("611269818994", 30));

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2035261008_total_misread", "rb2.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.True(outcome.HasWarnings);
        Assert.Single(Directory.GetFiles(dest));
        Assert.Contains(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }


    /// <summary>
    /// The ticket that used to reach its own printed total through four wrong rows now comes out
    /// **exactly right**, and this is the whole argument for asking rather than deriving.
    ///
    /// Its lines added up to the printed $394.84 with four of eight amounts wrong, because the errors
    /// cancelled to the cent, and nothing said a word. Two of those rows read cleanly and wrongly —
    /// the scanner rendered the same misreading in PRICE and TOTAL, so the row agreed with itself —
    /// which is why the ticket not adding up is the only thing that can find them. Every amount
    /// nobody had looked at gets shown, the operator reads the paper, and the ticket lands on its own
    /// printed figure with the right money on every line.
    /// </summary>
    [Fact]
    public void TheTicketThatAddedUpThroughTheWrongRowsComesOutRightWhenTheRowsAreChecked()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        // What the paper prints, row by row: 42.00, 36.11, 43.23, then five at 54.70.
        var prompt = new ScriptedPrompt("081026-1");
        prompt.Answers[LineField.Amount] = new Queue<string>(
            ["54.70", "54.70", "42.00", "36.11", "43.23", "54.70", "54.70", "54.70"]);

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2035939353_total_reached_wrong_way", "rb6.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        // All eight amounts end up read by a person, and the result is the ticket's own total.
        Assert.Equal(8, prompt.LineFields.Count(r => r.Field == LineField.Amount));
        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("394.84", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(168, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
        Assert.DoesNotContain(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    /// <summary>
    /// A ticket whose own printed total is the damaged figure still converts, once every amount has
    /// been read off the paper.
    ///
    /// Its five rows plainly print $42.00 and four at $54.70, coming to $315.50, and the ticket prints
    /// $315.60 for itself — the same 5-read-as-6 that damages the rows, landing on the total this
    /// time. Refusing here threw away a delivery that was entirely correct. The lines are used and
    /// the disagreement is said out loud.
    /// </summary>
    [Fact]
    public void ATicketWhoseOwnTotalIsDamagedStillConvertsOnTheRowsThatWereChecked()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("071326-1");
        prompt.Answers[LineField.Amount] = new Queue<string>(
            ["42.00", "54.70", "54.70", "54.70", "109.40"]);

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2035039295_no_total_column", "rb5.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("315.50", xml.Descendants("TotalInvoiceDueAmt").Single().Value);

        var note = Assert.Single(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
        Assert.Contains("read off the paper", note.Message, StringComparison.Ordinal);
    }

    /// <summary>A cent of scan noise is flagged and written — losing a whole delivery over a
    /// rounding difference is worse than the difference.</summary>
    [Fact]
    public void ShipsOnAPennyAndSaysSo()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, VerifiedTicketPrompt())
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

    /// <summary>
    /// A one-SKU delivery, which is a shape the parser could not see at all.
    ///
    /// The money columns are found by where the money on the page stacks up, and a column used to
    /// need three readings before it counted as a column. A ticket for one product has one reading
    /// per column — so the only stack that qualified was the TOTALS block at the bottom, and with
    /// PRICE and TOTAL calibrated onto DISCOUNT and TAX the page contained no products at all.
    /// "No product lines could be read" for a ticket a person can read in two seconds.
    /// </summary>
    [Fact]
    public void ASingleSkuTicketIsReadLikeAnyOther()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("111025-1");
        prompt.Answers[LineField.Amount] = new Queue<string>(["53.45"]);   // the scan read $53.46

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2029376829_single_line", "rb.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("53.45", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal("611269002867", Assert.Single(xml.Descendants("InvoiceUnitId")).Value);
        Assert.Equal(24, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
    }

    /// <summary>
    /// Two SKUs, same story — and this one also shows why the printed total cannot be trusted to
    /// settle a row on this vendor. Its 16oz line prints $35.11 and the scan renders `$36.11` in
    /// PRICE and TOTAL both, so the row agrees with itself; only the ticket's own $142.01 disagrees,
    /// and the operator reads the figure off the picture.
    /// </summary>
    [Fact]
    public void ATwoLineTicketIsReadLikeAnyOther()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("010626-1");
        prompt.Answers[LineField.Amount] = new Queue<string>(["106.90", "35.11"]);

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "2030959983_two_lines", "rb.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("142.01", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(2, xml.Descendants("LineItem").Count());
        Assert.Equal(60, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
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
