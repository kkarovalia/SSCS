using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// The case the whole feature was really wanted for: a PDF whose text layer reads as **nothing**.
///
/// A sheet that went through the scanner slightly crooked, or came back with a pen mark across it,
/// gives the OCR a page of garbage — and is perfectly legible to a person holding it. Those files
/// used to be a total loss, refused before anything was asked. Now the picture supplies the rows and
/// the parser does exactly the same arithmetic on them it does on any other ticket.
/// </summary>
public class VisionOnlyPageTests
{
    private sealed class FakeVision(VisionReading reading) : IInvoiceVisionReader
    {
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => reading;
    }

    private sealed class ScriptedPrompt(string invoiceNumber) : IInvoiceDetailsPrompt
    {
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<string, string> Paper { get; } = new(StringComparer.Ordinal);

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new(invoiceNumber, request.Date ?? new DateOnly(2026, 8, 11));

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

    // ───────────────────────────────────────────────────────────────────────────── Red Bull

    private const string RedBullBlank = "REDBULL_image_only.pdf";

    private static QueuedFile RedBull(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull };

    /// <summary>
    /// A two-line ticket as the picture reads it. QTY, PRICE and TOTAL are three separately read
    /// figures, so `QTY x PRICE = TOTAL` is a real test of each row even with only one reader — and
    /// the rows add up to the total the ticket prints for itself.
    /// </summary>
    private static VisionReading ATicketOnlyThePictureCanRead(decimal firstAmount = 81.50m) => new()
    {
        Rows =
        [
            new VisionRow
            {
                ItemNumber = "0861718", Description = "RED BULL 8.4OZ LS", Barcode = "611269991000",
                Qty = 2, Units = 48, Price = 40.75m, Amount = firstAmount,
            },
            new VisionRow
            {
                ItemNumber = "8633049", Description = "RED BULL 12OZ LS", Barcode = "611269546019",
                Qty = 1, Units = 24, Price = 53.45m, Amount = 53.45m,
            },
        ],
        PrintedTotal = 134.95m,
        PrintedUnits = 72,
    };

    /// <summary>
    /// The file that used to be refused outright now converts, off the picture alone — and it is
    /// flagged, because every figure on it was read once rather than twice.
    /// </summary>
    [Fact]
    public void APdfWithNoReadableTextConvertsFromThePicture()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("081126-RB1");

        var outcome = Service(tmp, prompt, new FakeVision(ATicketOnlyThePictureCanRead()))
            .ConvertFile(RedBull(Place(tmp, RedBullBlank, "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(2, xml.Descendants("LineItem").Count());
        Assert.Equal("134.95", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(72, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));

        // The barcodes off the picture are what the products go in under.
        Assert.Contains("611269991000", xml.Descendants("InvoiceUnitId").Select(e => e.Value));

        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("read off the picture", StringComparison.OrdinalIgnoreCase)
            || w.Message.Contains("off the picture of the page", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same file with no second reader is still refused, and still says why. This is the control:
    /// nothing about the old behaviour moved, a new way through was added beside it.
    /// </summary>
    [Fact]
    public void TheSameFileIsStillRefusedWithNoSecondReader()
    {
        using var tmp = new TempDir();

        var outcome = Service(tmp, new ScriptedPrompt("081126-RB1"))
            .ConvertFile(RedBull(Place(tmp, RedBullBlank, "rb.pdf")), tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Equal(PdfReasonCodes.NoText, outcome.ReasonCode);
        Assert.Contains("no readable text", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A row read off the picture is **not** taken on trust. Here the picture's own figures do not
    /// multiply out and the rows miss the printed total, so the row is asked about — exactly as it
    /// would be on a ticket the text layer had read.
    ///
    /// The question carries the whole page rather than a cut-out row: with no text layer there are no
    /// coordinates, so the model's estimate of where the row sits is what opens the picture at it and
    /// marks the band it is in — never a box round one figure, which would need a position nobody has.
    /// </summary>
    [Fact]
    public void ARowThePicturesOwnArithmeticRejectsIsStillAskedAbout()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("081126-RB1");
        prompt.Paper["RED BULL 8.4OZ LS"] = "81.50";

        // 2 cases at $40.75 cannot come to $99.99, and 99.99 + 53.45 misses the printed 134.95.
        var outcome = Service(tmp, prompt, new FakeVision(ATicketOnlyThePictureCanRead(firstAmount: 99.99m)))
            .ConvertFile(RedBull(Place(tmp, RedBullBlank, "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal("RED BULL 8.4OZ LS", asked.Description);
        Assert.Equal(LineField.Amount, asked.Field);
        Assert.NotNull(asked.Snip);   // the page, opened at the row

        Assert.Equal("134.95", XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>
    /// Both readers found rows, but not the same number of them. The text layer has lost one, and a
    /// lost row takes its money with it so no total on the page can see it. It is said out loud
    /// rather than patched in — the rows that did read are each backed by two readings, and trading
    /// all of that for one recovered row would be the worse deal.
    /// </summary>
    [Fact]
    public void ItSaysSoWhenThePictureSeesMoreRowsThanTheTextDid()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        // The verified ticket has ten rows; claim eleven.
        var rows = Enumerable.Range(0, 11)
            .Select(i => new VisionRow { ItemNumber = $"ZZ{i}", Units = 1, Amount = 1m })
            .ToList();

        var outcome = Service(tmp, prompt, new FakeVision(new VisionReading { Rows = rows }))
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("A line is probably missing", StringComparison.Ordinal));

        // And the ten rows it did read still went in on their own two readings.
        Assert.Equal(10, XDocument.Load(outcome.FinalOutputPaths[0]).Descendants("LineItem").Count());
    }

    // ──────────────────────────────────────────────────────────────────────────── Dr Pepper

    private static QueuedFile DrPepper(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

    /// <summary>
    /// The upside-down scan. Every glyph on it is its own 180-degree twin, so the OCR produced a full
    /// page of nothing and the invoice was refused before any prompt. The picture reads it.
    /// </summary>
    [Fact]
    public void TheUpsideDownDrPepperScanConvertsFromThePicture()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "5GBIB DrPep", Barcode = "054900710545",
                    Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
                new VisionRow
                {
                    ItemNumber = "", Description = "18z12 Hai CcntMlok", Barcode = "813694026719",
                    Qty = 1, Units = 12, Price = 17.00m, Amount = 17.00m,
                },
            ],
            PrintedTotal = 132.95m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(2, xml.Descendants("LineItem").Count());
        Assert.Equal("132.95", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(13, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
    }

    /// <summary>
    /// A page only the picture can read, carrying **both** a delivery and a return — which is the
    /// shape every ticket the store actually gets is in.
    ///
    /// The sign is the one thing no arithmetic on the page can recover: a return booked as a
    /// delivery adds stock that went back on the truck and doubles the error rather than cancelling
    /// it. So it comes from the model saying so, and the invoice total is what catches it being
    /// wrong — 81.50 + 53.45 - 13.36 only reaches the printed 121.59 with the return facing the
    /// right way.
    /// </summary>
    [Fact]
    public void APictureOnlyPageBooksItsReturnsAsNegativeLines()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("081126-RB2");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "0861718", Description = "RED BULL 8.4OZ LS", Barcode = "611269991000",
                    Qty = 2, Units = 48, Price = 40.75m, Amount = 81.50m,
                },
                new VisionRow
                {
                    ItemNumber = "8633049", Description = "RED BULL 12OZ LS", Barcode = "611269546019",
                    Qty = 1, Units = 24, Price = 53.45m, Amount = 53.45m,
                },
                new VisionRow
                {
                    ItemNumber = "9221027", Description = "COCONUT 12OZ LS", Barcode = "611269133448",
                    Units = 6, Price = 2.23m, Amount = 13.36m, IsCredit = true,
                },
            ],
            PrintedTotal = 121.59m,
            PrintedUnits = 72,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(RedBull(Place(tmp, RedBullBlank, "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal(3, xml.Descendants("LineItem").Count());
        Assert.Equal("121.59", xml.Descendants("TotalInvoiceDueAmt").Single().Value);

        var qty = xml.Descendants("InvoiceUnitQty").Select(e => int.Parse(e.Value)).ToList();
        Assert.Equal(2, qty.Count(q => q > 0));
        Assert.Equal(-6, Assert.Single(qty, q => q < 0));
        Assert.Equal(66, qty.Sum());
    }

    /// <summary>
    /// A barcode read off the picture still has to pass its own check digit.
    ///
    /// This is the one thing on the page that reading it a different way does not help with: the model
    /// misreads a digit exactly as the scanner does, and the money is blind to it. The store's first
    /// no-text sample printed 810128528191 and came back 810128529191 — twelve confident digits that
    /// fail their own arithmetic — and the invoice still reached its printed AMOUNT DUE to the cent,
    /// with one line booked onto a product that was never delivered.
    ///
    /// So the row goes to the operator, and everything else on the invoice is untouched.
    /// </summary>
    [Fact]
    public void APictureBarcodeThatFailsItsCheckDigitIsAskedAboutRatherThanShipped()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["18z12 Hai CcntMlck"] = "813694026719";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    Description = "5GBIB DrPep", Barcode = "054900710545",
                    ItemNumber = "", Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
                new VisionRow
                {
                    // 813694026719 is the printed code; a 7 for the 1 keeps the length and the shape
                    // and breaks the arithmetic.
                    Description = "18z12 Hai CcntMlck", Barcode = "813694026779",
                    ItemNumber = "", Qty = 1, Units = 12, Price = 17.00m, Amount = 17.00m,
                },
            ],
            PrintedTotal = 132.95m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal(LineField.Barcode, asked.Field);
        Assert.Equal("18z12 Hai CcntMlck", asked.Description);
        Assert.Equal("813694026779", asked.PrintedValue);

        var codes = XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitId").Select(e => e.Value).ToList();
        Assert.Equal(["054900710545", "813694026719"], codes);
    }

    /// <summary>
    /// The control for the rule above: a code the picture read correctly is used without a word, so
    /// the check is catching wrong digits rather than simply distrusting the picture.
    /// </summary>
    [Fact]
    public void APictureBarcodeThatPassesItsCheckDigitIsUsedWithoutAsking()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    Description = "18z12 Hai CcntMlck", Barcode = "813694026719",
                    ItemNumber = "", Qty = 1, Units = 12, Price = 17.00m, Amount = 17.00m,
                },
            ],
            PrintedTotal = 17.00m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);
        Assert.Equal("813694026719", XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitId").Single().Value);
    }

    /// <summary>
    /// The pallets and shells at the foot of a Dr Pepper ticket are not goods.
    ///
    /// They are printed below AMOUNT DUE as a name, a number and a count — the same shape as a
    /// product row — and the model reported them as products on five of the store's sixteen samples.
    /// Each one then asked for an amount that is printed nowhere, and refused the invoice when nobody
    /// could supply one. They are worth 0.00, so leaving them out cannot move the total.
    /// </summary>
    [Fact]
    public void PalletsAndShellsAreNotProductRows()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "5GBIB DrPep", Barcode = "054900710545",
                    Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
                new VisionRow { ItemNumber = "75001490", Description = "PALLET PLASTIC 37X37", Units = 1 },
                new VisionRow { ItemNumber = "75000013", Description = "SHELL PET 1L15", Units = 1 },
                new VisionRow { ItemNumber = "75000014", Description = "SHELL PET 20Z", Units = 5 },
            ],
            PrintedTotal = 115.95m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("5GBIB DrPep", Assert.Single(xml.Descendants("InvoiceUnitDescription")).Value);
        Assert.Equal("115.95", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>
    /// The control: a priced row is a product whatever it is called, and a row that lost its money is
    /// a question rather than something to drop quietly. Both halves of the test have to hold or the
    /// rule would start eating real deliveries.
    /// </summary>
    [Fact]
    public void ARowCarryingMoneyIsKeptEvenWhenItIsCalledAShell()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "SHELL GAME SODA 12z12", Barcode = "054900710545",
                    Qty = 1, Units = 12, Price = 115.95m, Amount = 115.95m,
                },
            ],
            PrintedTotal = 115.95m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal("SHELL GAME SODA 12z12",
            Assert.Single(XDocument.Load(outcome.FinalOutputPaths[0])
                .Descendants("InvoiceUnitDescription")).Value);
    }

    /// <summary>
    /// The picture's own TOT SALE count has to gate the quantities, exactly as the text layer's does.
    ///
    /// This is the one thing the money genuinely cannot see. A wrong UNITS and a right AMOUNT simply
    /// give a wrong unit cost, so the invoice still reaches its printed AMOUNT DUE to the cent and
    /// nothing downstream ever notices — the delivery just goes into SSCS with the wrong stock on it.
    /// Sample DownloadFile (13) booked 342 units against the 326 printed on the paper and reported
    /// $588.97, which is what the ticket says, in complete silence.
    ///
    /// The count is not derived from the rows and does not confirm them: it is a separate figure at
    /// the foot of the ticket, which is what the text-layer path has always used it as.
    /// </summary>
    [Fact]
    public void ThePicturesOwnUnitCountStillGatesTheQuantities()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "20z24 DrPep", Barcode = "078000082401",
                    Qty = 1, Units = 40, Price = 32.50m, Amount = 32.50m,
                },
                new VisionRow
                {
                    ItemNumber = "", Description = "5GBIB DrPep", Barcode = "054900710545",
                    Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
            ],
            // The rows come to 41 units. The ticket prints 25, and the money is right either way.
            PrintedTotal = 148.45m,
            PrintedUnits = 25,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Equal(PdfReasonCodes.QtyMismatch, outcome.ReasonCode);
        Assert.Contains("41 units", outcome.UserMessage, StringComparison.Ordinal);
        Assert.Contains("own total says 25", outcome.UserMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: counts that agree with the ticket's own go through without a word, so the gate is
    /// catching a misread rather than simply distrusting anything read off a picture.
    /// </summary>
    [Fact]
    public void CountsThatMatchThePrintedTotalGoThroughSilently()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "20z24 DrPep", Barcode = "078000082401",
                    Qty = 1, Units = 24, Price = 32.50m, Amount = 32.50m,
                },
                new VisionRow
                {
                    ItemNumber = "", Description = "5GBIB DrPep", Barcode = "054900710545",
                    Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
            ],
            PrintedTotal = 148.45m,
            PrintedUnits = 25,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(25, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
        Assert.Equal("148.45", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>
    /// A thirteen-digit code off the picture is damage even when it adds up.
    ///
    /// This is the trap in teaching a reader about check digits: one that inserts a digit can land on
    /// a code that passes. The printed `014800511758` came back as `0148000511758` — one extra zero —
    /// and that is a valid GTIN-13, so the check-digit guard let it straight through and SSCS would
    /// have opened a new item under it. Dr Pepper prints UPC-A and nothing else, so a length that is
    /// not twelve is damage whatever the arithmetic says.
    /// </summary>
    [Fact]
    public void AThirteenDigitPictureCodeIsDamageEvenWhenItAddsUp()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["16z12 Clam"] = "014800511758";

        // 0148000511758 passes the GTIN-13 check digit. It is still not what is on the paper.
        Assert.True(Upc.IsValidGtin("0148000511758"));

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "16z12 Clam", Barcode = "0148000511758",
                    Qty = 1, Units = 12, Price = 21.40m, Amount = 21.40m,
                },
            ],
            PrintedTotal = 21.40m,
            PrintedUnits = 12,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(LineField.Barcode, Assert.Single(prompt.LineFields).Field);
        Assert.Equal("014800511758", XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitId").Single().Value);
    }

    /// <summary>
    /// A question about a row read off the picture still shows the operator the paper.
    ///
    /// It used to arrive bare. These rows have no line number, so there was no band to cut, and the
    /// clerk got "type the barcode for 12z12p DrPep DEP" against a description the model had itself
    /// half-garbled — on precisely the invoices that ask the most questions. Now the whole page is
    /// shown, **with nothing marked**: the page holds the row by construction, and a highlight on a
    /// guessed position would put a real row on screen with the wrong figure picked out, which is
    /// worse than no picture at all. The hint says so, because otherwise the operator hunts for a
    /// marker that is deliberately absent. It is opened at roughly the right height, which is a hint
    /// and costs a scroll when it is wrong.
    /// </summary>
    [Fact]
    public void AQuestionAboutAPictureRowShowsTheWholePage()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["16z12 Clam"] = "014800511758";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "16z12 Clam", Barcode = "not-a-barcode", Page = 1,
                    Qty = 1, Units = 12, Price = 21.40m, Amount = 21.40m,
                },
            ],
            PrintedTotal = 21.40m,
            PrintedUnits = 12,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.NotNull(asked.Snip);
        Assert.True(asked.Snip.Width > 0 && asked.Snip.Height > 0);

        // Nothing marked, and no column header - that legend is cut by line number, which this row
        // does not have.
        //
        // The mark was tried. For one day this drew the model's estimate as a band a couple of
        // printed lines tall, and on 2026-08-12 it was rendered for every question the store's thirty
        // samples raise and looked at: on Red Bull it covered the row asked about 6 times out of 11,
        // and on Dr Pepper 0 times out of 8 - twice on the address block at the head of the ticket,
        // twice on the totals at the foot. A sharper instruction did not change the shape of it.
        // Half-right is the one result that cannot ship: a marker on the wrong row is read, typed,
        // and books a figure belonging to another product, and every total still agrees because it
        // is a real figure off a real row.
        Assert.Equal(0, asked.Snip.FocusHeight);
        Assert.Equal(0, asked.Snip.FocusWidth);
        Assert.Null(asked.Header);
        Assert.Contains("Nothing is marked", asked.Hint, StringComparison.Ordinal);

        // And the operator is told which row to look for, because the picture no longer says so.
        Assert.True(asked.WholePage);
        Assert.Equal("16z12 Clam", asked.Description);
    }

    /// <summary>
    /// A row the model gave no position for still shows the page, opened at the top. The scroll is a
    /// convenience layered on the picture, not a condition of getting one.
    /// </summary>
    [Fact]
    public void APictureRowWithNoPositionStillGetsThePage()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["16z12 Clam"] = "014800511758";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "16z12 Clam", Barcode = "nope", Page = 1,
                    Qty = 1, Units = 12, Price = 21.40m, Amount = 21.40m,
                },
            ],
            PrintedTotal = 21.40m,
            PrintedUnits = 12,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        var asked = Assert.Single(prompt.LineFields);
        Assert.NotNull(asked.Snip);
        Assert.Equal(0, asked.Snip.FocusHeight);   // a page holds the row; it never claims which
        Assert.True(asked.WholePage);
    }

    /// <summary>
    /// The control: where the text layer did read the row, the question still gets the tight cut-out
    /// with the figure marked and the column header over it. The whole-page fallback is for rows that
    /// have no line number, and it must not quietly replace the better picture everywhere else.
    /// </summary>
    [Fact]
    public void ARowTheTextLayerReadStillGetsTheTightCutout()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        var asked = prompt.LineFields.Where(f => f.Snip is not null).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, f =>
        {
            Assert.True(f.Snip!.FocusHeight > 0);                         // the figure is marked
            Assert.DoesNotContain("Find the row", f.Hint, StringComparison.Ordinal);
        });
    }

    /// <summary>With no second reader that scan is refused before a single window opens, as before.</summary>
    [Fact]
    public void TheUpsideDownScanIsStillRefusedWithNoSecondReader()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Empty(prompt.LineFields);
    }
}
