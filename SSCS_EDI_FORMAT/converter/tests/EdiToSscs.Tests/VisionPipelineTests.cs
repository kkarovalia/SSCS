using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// End-to-end proofs that a second reading off the picture changes what reaches SSCS — on the real
/// scans the store dropped, not on constructed ones.
///
/// The Red Bull ticket here is the one live SSCS booked as a $342.50 paidout. Its scan renders a
/// printed $40.75 as `$40.76` in the PRICE and TOTAL columns both, so the row multiplies out
/// perfectly and nothing on the page can see it, and it destroys two amounts outright. Without a
/// second reader that is two questions and a penny over; with one it is neither.
/// </summary>
public class VisionPipelineTests
{
    /// <summary>Stands in for the model, returning a reading taken off the paper.</summary>
    private sealed class FakeVision(VisionReading reading) : IInvoiceVisionReader
    {
        public int Calls { get; private set; }
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
        {
            Calls++;
            return reading;
        }
    }

    /// <summary>Answers from a script, and fails the test if asked anything unscripted.</summary>
    private sealed class ScriptedPrompt(string invoiceNumber) : IInvoiceDetailsPrompt
    {
        public bool ConfirmDrPepperOcrFallback(string fileName) => true;
        public bool ConfirmRedBullOcrFallback(string fileName) => true;
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<string, string> Paper { get; } = new(StringComparer.Ordinal);

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new(invoiceNumber, new DateOnly(2026, 1, 1));

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return Paper.TryGetValue(request.Description, out string? answer)
                ? answer
                : throw new Xunit.Sdk.XunitException(
                    $"unexpected question: {request.Field} for \"{request.Description}\"");
        }
    }

    private static ConversionService Service(
        TempDir tmp, IInvoiceDetailsPrompt prompt, IInvoiceVisionReader? vision = null) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt, vision);

    private static string Place(TempDir tmp, string fixture, string name)
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(@"Fixtures\PDF", fixture)));
        return path;
    }

    private static VisionRow Row(string item, int units, decimal amount, string? barcode = null) =>
        new() { ItemNumber = item, Units = units, Amount = amount, Barcode = barcode };

    // ───────────────────────────────────────────────────────────────────────────── Red Bull

    private static QueuedFile RedBull(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull };

    /// <summary>
    /// The verified ticket as it is actually printed, in page order.
    ///
    /// The item numbers deliberately do **not** match what the text layer made of them — the scan
    /// reads `O84815` and `9/221027` for two of these — because that is the real case, and the
    /// alignment has to carry it on the counts and amounts instead.
    /// </summary>
    private static VisionReading TheVerifiedTicketAsPrinted() => new()
    {
        Rows =
        [
            Row("0861718", 24, 40.75m),      // the scan reads 40.76 in both money columns
            Row("8633049", 24, 70.22m),
            Row("0836463", 12, 42.23m),
            Row("0824672", 48, 106.90m),     // the scan lost this amount outright
            Row("0848150", 48, 106.90m),
            Row("9020376", 1, 2.23m),
            Row("9024007", 1, 2.23m),
            Row("9624768", 2, 4.45m),        // and this one
            Row("9221027", 6, 13.36m),
            Row("9148160", 1, 2.23m),
        ],
        PrintedTotal = 342.50m,
        PrintedUnits = 156,
    };

    /// <summary>
    /// With a second reader the ticket converts with **no questions at all**, and lands on the
    /// $342.50 live SSCS holds for it rather than a penny over.
    ///
    /// Both things come from the same rule. The picture offers a reading for every row; the total
    /// the ticket prints for itself picks the combination that reaches it; and a row is taken only
    /// where that combination matches what one of the two readers actually saw. Nothing is worked
    /// out from the total — the $40.75 was read off the paper, and the total merely confirmed it.
    /// </summary>
    [Fact]
    public void ASecondReadingSettlesTheVerifiedTicketWithoutAskingAnything()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        var vision = new FakeVision(TheVerifiedTicketAsPrinted());

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);
        Assert.Equal(1, vision.Calls);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(10, xml.Descendants("LineItem").Count());
        Assert.Equal("342.50", xml.Descendants("TotalInvoiceDueAmt").Single().Value);

        // Five delivered and five returned, on one ticket, in one payable — which is the shape live
        // SSCS booked. The returns have to come out negative or the delivery is booked twice over.
        var qty = xml.Descendants("InvoiceUnitQty").Select(e => int.Parse(e.Value)).ToList();
        Assert.Equal(5, qty.Count(q => q > 0));
        Assert.Equal(5, qty.Count(q => q < 0));
        Assert.Equal(145, qty.Sum());

        // And no complaint about the total, because there is nothing left to complain about.
        Assert.DoesNotContain(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    /// <summary>
    /// The same ticket with no second reader: exactly what the converter did before this existed.
    /// Two questions, and a total a penny over that is flagged rather than silently shipped.
    ///
    /// This is the control. The whole feature has to be additive — no key, no network, no change.
    /// </summary>
    [Fact]
    public void WithNoSecondReaderTheTicketBehavesExactlyAsItDidBefore()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.LineFields.Count);
        Assert.All(prompt.LineFields, r => Assert.Equal(LineField.Amount, r.Field));

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("342.51", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Contains(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    /// <summary>
    /// A reading that cannot be tied to the rows changes nothing at all. The model here reported a
    /// different ticket entirely — same shape, unrelated figures — and every question the scan
    /// needed is still asked.
    /// </summary>
    [Fact]
    public void AReadingThatDoesNotBelongToThisTicketIsIgnored()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var vision = new FakeVision(new VisionReading
        {
            Rows = [Row("ZZ1", 99, 11.11m), Row("ZZ2", 98, 22.22m), Row("ZZ3", 97, 33.33m)],
        });

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.LineFields.Count);
        Assert.Equal("342.51", XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>
    /// A second reader that disagrees with the scan and cannot be corroborated does **not** get its
    /// way — the operator does.
    ///
    /// Here the picture claims $50.00 for a row the scan read as $40.76. Neither figure reaches the
    /// ticket's printed total and the row bills a single case, so its own `QTY x PRICE` is the same
    /// glyph twice and settles nothing. Two readers, no tie-breaker, so the row is shown to a person
    /// — which is exactly what a confidently wrong reading has to run into.
    /// </summary>
    [Fact]
    public void ADisagreementNothingCanSettleStillGoesToTheOperator()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["RED BULL 8.40ZLS"] = "40.75";
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var reading = TheVerifiedTicketAsPrinted();
        var rows = reading.Rows.ToList();
        rows[0] = Row("0861718", 24, 50.00m);              // a wrong reading of the first row
        var vision = new FakeVision(reading with { Rows = rows });

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        // The row the two readers disagreed about is asked, and the answer is what goes in.
        Assert.Contains(prompt.LineFields, r => r.Description == "RED BULL 8.40ZLS");
        Assert.DoesNotContain("50.00", XDocument.Load(outcome.FinalOutputPaths[0]).ToString());
    }

    /// <summary>
    /// A count the scan lost entirely is **not** a question any more — the picture read it, and one
    /// reading beats none.
    ///
    /// This is the one place the "confirm, never derive" rule is deliberately relaxed, and the
    /// distinction it turns on is worth keeping straight: every other rule here has two claims about
    /// one figure and needs something to choose between them. A column the scan never produced has
    /// one claim and nothing to choose. It is still a reading copied off the paper — no checksum is
    /// satisfied by construction — so it goes in with a note rather than stopping the clerk.
    ///
    /// Dr Pepper's counts are where this shows cleanly: a row belonging to no package group has no
    /// subtotal that could ever confirm it, so before this it was always a question.
    /// </summary>
    [Fact(Skip = "Superseded: Dr Pepper now uses the dedicated AI-primary three-pass contract.")]
    public void ACountTheScanLostIsTakenFromThePictureAndFlagged()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("5218300220");

        // "5GBIB DrPep" reached the text layer as "1." and sits under a subtotal that cannot settle
        // it, so it is asked about on every run. The other two damaged rows are left unreadable here
        // so they still go to the operator, which keeps the two behaviours apart in one file.
        var vision = new FakeVision(new VisionReading
        {
            Rows = [Row("", 1, 115.95m, "054900710545")],
        });
        prompt.Paper["18z12 Hai CcntMlok"] = "12";
        prompt.Paper["12z12 31m CrspApl"] = "12";

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(DrPepper(Place(tmp, DrPepperScan, "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        // The row the picture could read is no longer asked about; the two it could not still are.
        Assert.DoesNotContain(prompt.LineFields, r => r.Description == "5GBIB DrPep");
        Assert.Equal(2, prompt.LineFields.Count);

        // And it is flagged, because nothing on the invoice corroborates a single reading.
        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("5GBIB DrPep", StringComparison.Ordinal)
            && w.Message.Contains("Read off the picture", StringComparison.Ordinal));

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(585, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
    }

    // ──────────────────────────────────────────────────────────────────────────── Dr Pepper

    private const string DrPepperScan = "DRPEPPER_5218300220_twice_scanned.pdf";

    private static QueuedFile DrPepper(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

    /// <summary>
    /// The three counts this scan damaged, as the paper prints them. Dr Pepper's priced row carries
    /// no item number, so these tie on the UPC off the product line above.
    /// </summary>
    private static VisionReading TheThreeDamagedCounts(int first = 1, int second = 12, int third = 12) => new()
    {
        Rows =
        [
            Row("", first, 115.95m, "054900710545"),     // reached the text layer as "1."
            Row("", second, 17.00m, "813694026719"),     // read 11 for a printed 12
            Row("", third, 20.00m, "842595138214"),      // and again
        ],
    };

    /// <summary>
    /// The store's Dr Pepper ticket converts with no questions when the picture supplies the three
    /// counts the scan destroyed — and lands on the same 585 units and $1080.22 the operator's own
    /// answers produce.
    ///
    /// The counts are not taken because the model said so. They are taken because putting them in
    /// lands each package group on the unit total the invoice prints for itself, which is a figure
    /// neither reader had any hand in.
    /// </summary>
    [Fact(Skip = "Superseded: Dr Pepper now uses the dedicated AI-primary three-pass contract.")]
    public void ASecondReadingSuppliesTheCountsDrPepperUsedToAskAbout()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("5218300220");
        var vision = new FakeVision(TheThreeDamagedCounts());

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(DrPepper(Place(tmp, DrPepperScan, "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(37, xml.Descendants("LineItem").Count());
        Assert.Equal("1080.22", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(585, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));

        // Each repair is reported, because a count a person did not read is one they should glance at.
        Assert.True(outcome.HasWarnings);
    }

    /// <summary>
    /// A count off the picture that the invoice's own package total does not accept is refused, and
    /// the operator is asked as before.
    ///
    /// This is the line the whole design stands on. The subtotal is allowed to **confirm** a figure
    /// somebody read and never to supply one, so a wrong reading cannot be quietly absorbed — and
    /// the invoice still reaches its printed 585 units, off the operator's answers.
    /// </summary>
    [Fact(Skip = "Superseded: Dr Pepper now uses the dedicated AI-primary three-pass contract.")]
    public void ACountThePackageTotalRejectsIsAskedAboutAnyway()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("5218300220");
        prompt.Paper["18z12 Hai CcntMlok"] = "12";
        var vision = new FakeVision(TheThreeDamagedCounts(second: 99));   // nine units too many

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(DrPepper(Place(tmp, DrPepperScan, "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal("18z12 Hai CcntMlok", asked.Description);
        Assert.Equal(LineField.Units, asked.Field);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(585, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
    }
}
