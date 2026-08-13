using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// What a second reading off the picture changes on Bimbo — proved on the two real scans the store
/// could not convert, not on constructed ones.
///
/// Both are 200 dpi, from before the scanner setting changed on 2026-07-27, and they fail in the two
/// different ways this vendor fails. Ticket 84294890010234 has a text layer that loses the QTY column
/// on all forty rows and then destroys two rows outright — `3.0100` comes apart into `3 .01 00` — so
/// the ticket is eleven units short of the 236 it prints for itself and is refused whole. Ticket
/// 84294890013049 went through with the scanner's OCR switched off and has no text layer at all.
///
/// The readings below are what is actually printed on those two pages, transcribed off the scans.
/// </summary>
public class BimboVisionTests
{
    private const string QtyColumnLost = "BIMBO_84294890010234_deliveries_only.pdf";
    private const string ImageOnly = "BIMBO_84294890013049_image_only_with_returns.pdf";

    /// <summary>Stands in for the model, returning a reading taken off the paper.</summary>
    private sealed class FakeVision(VisionReading reading) : IInvoiceVisionReader
    {
        public List<VisionVendor> Asked { get; } = [];

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
        {
            Asked.Add(vendor);
            return reading;
        }
    }

    /// <summary>Answers from a script, and fails the test if asked anything unscripted.</summary>
    private sealed class ScriptedPrompt(string invoiceNumber) : IInvoiceDetailsPrompt
    {
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<string, string> Paper { get; } = new(StringComparer.Ordinal);

        /// <summary>Stands in for the operator closing the window without answering.</summary>
        public bool DismissesEverything { get; init; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new(invoiceNumber, new DateOnly(2025, 9, 8));

        public bool ConfirmBimboOcrFallback(string fileName) => true;

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            if (DismissesEverything) return null;
            return Paper.TryGetValue(request.Description, out string? answer)
                ? answer
                : throw new Xunit.Sdk.XunitException(
                    $"unexpected question: {request.Field} for \"{request.Description}\"");
        }
    }

    private static ConversionService Service(
        TempDir tmp, IInvoiceDetailsPrompt prompt, IInvoiceVisionReader? vision = null) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt, vision);

    private static string Place(TempDir tmp, string fixture)
    {
        string path = Path.Combine(tmp.Path, "DownloadFile.pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(@"Fixtures\PDF", fixture)));
        return path;
    }

    private static QueuedFile Bimbo(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo };

    private static VisionRow Row(
        string upc, string item, string description, int units, decimal price, decimal amount,
        bool credit = false) =>
        new()
        {
            ItemNumber = item,
            Description = description,
            Barcode = upc,
            Units = units,
            Price = price,
            Amount = amount,
            IsCredit = credit,
            // The reader stamps this, one request being one page, and every Bimbo ticket is one page.
            // It is what lets a question about a row with no line number show the right picture.
            Page = 1,
        };

    // ────────────────────────────────────────────────────── the ticket that loses its QTY column

    /// <summary>
    /// Ticket 84294890010234 as it is printed: forty delivery rows, no returns block, and no
    /// DELIVERY TOTAL row either — a returns-free ticket goes straight from its last row to
    /// TICKET TOTALS, so the band gates have nothing to check and the ticket's own 236 is the only
    /// count there is.
    /// </summary>
    private static VisionReading TheDeliveriesOnlyTicketAsPrinted() => new()
    {
        Rows =
        [
            Row("7432309183", "008788", "BIM PANQUE NUE FS FC", 4, 3.0100m, 12.04m),
            Row("7432309182", "008787", "BIM PANQUE PAS FS FC", 4, 3.0100m, 12.04m),
            Row("7432309164", "008785", "BIM ROLE CAN FS6P FC", 2, 3.0100m, 6.02m),
            Row("7432309145", "008783", "BIM CONCHAS SS2P FC", 4, 1.7500m, 7.00m),
            Row("7432309157", "008784", "BIM CUERNITO SS2P FC", 6, 1.7500m, 10.50m),
            Row("7432309975", "009290", "BIM NITO DUO SS2P FC", 4, 1.7500m, 7.00m),
            Row("7432373882", "950550", "BIM JULITAS SS 2P", 6, 1.7500m, 10.50m),
            Row("7432309123", "008781", "BIM PANQUECITO SS 2P", 4, 1.7500m, 7.00m),
            Row("7432309079", "008778", "BIM PANQCTO CCH SS2P", 4, 1.7500m, 7.00m),
            Row("7432309139", "008782", "BIM MANTECD SS2P FC", 6, 1.7500m, 10.50m),
            Row("7432309122", "008931", "BIM MINHANT SS4P FC", 10, 1.7500m, 17.50m),
            Row("7432301061", "008770", "BIM MNMNT NUE SS4PBX", 6, 1.7500m, 10.50m),
            Row("7432309173", "008920", "BIM DORADITAS SS3PFC", 6, 1.7500m, 10.50m),
            Row("7432309459", "008880", "BIM REBANADA SS2P FC", 4, 1.7500m, 7.00m),
            Row("7432309104", "008779", "BIM BUNUELO SS4P FC", 4, 1.7500m, 7.00m),
            Row("7432309501", "008796", "MLA GANSITO SS2P BX", 4, 1.5100m, 6.04m),
            Row("7432309535", "008802", "MLA PINGUINO SS2P FC", 4, 1.7500m, 7.00m),
            Row("7432309577", "008807", "MLA CHCRL PNA SS2PBX", 4, 1.7500m, 7.00m),
            Row("7432309585", "008852", "MLA SUBMR STR SS2PBX", 4, 1.7500m, 7.00m),
            Row("7432309438", "008795", "MLA BRITA FRE SS4PBX", 6, 1.7500m, 10.50m),
            Row("7432305156", "008776", "MLA PASTISE SS10P FC", 8, 1.7500m, 14.00m),
            Row("7432309513", "008799", "MLA PASTITA SS12P BX", 8, 1.7500m, 14.00m),
            Row("7432373796", "013534", "MLA CARIBENAS SS 1P", 14, 1.7500m, 24.50m),
            Row("7432309523", "008801", "MLA SPNCH STR SS6PBX", 12, 1.7500m, 21.00m),
            Row("7432309398", "007805", "MLA PRINCP CHO SS8P", 12, 1.7500m, 21.00m),
            Row("7432373899", "950664", "MLA CHOCCH CKY 4.2Z", 8, 1.7500m, 14.00m),
            Row("7432373898", "950663", "MLA CINNA TUBE 4.23Z", 8, 1.7500m, 14.00m),
            Row("7432309552", "008805", "MLA CINNAS FS25P FC", 7, 2.3900m, 16.73m),
            Row("7432309206", "008791", "MLA POLVORO FS15P FC", 7, 2.3900m, 16.73m),
            Row("7087073402", "014155", "MB PRD DON SS 6P 3Z", 8, 1.7500m, 14.00m),
            Row("7087073400", "014153", "MB FRST DON SS6P3.25", 8, 1.7500m, 14.00m),
            Row("7087073401", "014154", "MB CNSGR DON SS 6P3Z", 6, 1.7500m, 10.50m),
            Row("7087000002", "007955", "MB GLZ HNY BN SS 4Z", 6, 1.7500m, 10.50m),
            Row("7087073405", "014744", "MB ICED HNY BN SS 4Z", 6, 1.7500m, 10.50m),
            Row("7087004183", "001825", "MB GLZ CIN RL SS 4Z", 6, 1.7500m, 10.50m),
            Row("7087000742", "007966", "MB APPLE PIE SS 4Z", 4, 1.7500m, 7.00m),
            Row("7087000743", "007967", "MB CHERRY PIE SS 4Z", 4, 1.7500m, 7.00m),
            Row("7087000908", "007906", "MB GNG PRD DON 1OZ", 3, 3.0100m, 9.03m),
            Row("7087000909", "007907", "MB GNG FRST DON10.5Z", 3, 3.0100m, 9.03m),
            Row("7087000910", "007908", "MB GNG CIN DON 1OZ", 2, 3.0100m, 6.02m),
        ],
        PrintedTotal = 443.68m,
        PrintedUnits = 236,
    };

    /// <summary>
    /// With nothing to read the row and nobody answering, the ticket is still refused — and that is
    /// the outcome to keep. The text layer is eleven units short of the count the paper prints for
    /// itself, and eleven units of stock that never arrived is not something to ship on a warning.
    ///
    /// This is also the guard on the change underneath: a row whose quantity nothing could settle is
    /// now kept rather than dropped, and if keeping one ever let the gates pass, this is what would
    /// notice.
    /// </summary>
    [Fact]
    public void WithNoSecondReaderAndNoAnswerTheShortTicketIsStillRefused()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("TEST-1") { DismissesEverything = true })
            .ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(dest));
    }

    /// <summary>
    /// With one, the two rows the scan destroyed are read off the picture and the ticket reaches its
    /// own 236 units and $443.68 — forty lines, no questions.
    ///
    /// Neither quantity is worked out. Each is *read* by the second reader, and taken only because
    /// its own three figures multiply out and its amount is the one the text layer already read in
    /// that column: `3 .01 00` destroyed the price on the first row while `12.04` survived beside it,
    /// and 4 x 3.0100 is that same 12.04.
    /// </summary>
    [Fact]
    public void ThePictureSuppliesTheTwoQuantitiesTheScanDestroyed()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("090825143000D");
        var vision = new FakeVision(TheDeliveriesOnlyTicketAsPrinted());

        var outcome = Service(tmp, prompt, vision)
            .ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);                       // nobody had to be asked
        Assert.Equal(VisionVendor.Bimbo, Assert.Single(vision.Asked));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(40, xml.Descendants("LineItem").Count());
        Assert.Equal("443.68", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(236, xml.Descendants("InvoiceUnitQty").Sum(q => int.Parse(q.Value)));
    }

    /// <summary>
    /// The first of those two rows, in full. Its quantity and its unit price both come off the
    /// picture; the barcode does not, because the text layer read that column perfectly well and a
    /// code this vendor prints without a check digit is not something to take a second opinion on
    /// lightly.
    /// </summary>
    [Fact]
    public void TheRecoveredRowCarriesTheQuantityAndCostThePaperPrints()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("TEST-2"),
                new FakeVision(TheDeliveriesOnlyTicketAsPrinted()))
            .ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        var first = XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("LineItem").First().Element("InvoiceUnit")!;

        Assert.Equal("074323091830", first.Element("InvoiceUnitId")!.Value);
        Assert.Equal("4", first.Element("InvoiceUnitQty")!.Value);
        Assert.Equal("3.0100", first.Element("InvoiceUnitCost")!.Value);
    }

    /// <summary>
    /// A quantity read off the picture is never silent. It is the one figure on this vendor the money
    /// cannot check — a wrong count is absorbed by the unit cost and the invoice still balances to
    /// the penny — so the row is named, with the arithmetic that let it be taken.
    /// </summary>
    [Fact]
    public void SayingSoWhenAQuantityCameOffThePicture()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("TEST-3"),
                new FakeVision(TheDeliveriesOnlyTicketAsPrinted()))
            .ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        var told = outcome.Warnings
            .Where(w => w.Message.Contains("read off the picture", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, told.Count);
        Assert.Contains(told, w => w.Message.Contains("4 at 3.0100", StringComparison.Ordinal));
        Assert.Contains(told, w => w.Message.Contains("7 at 2.3900", StringComparison.Ordinal));
    }

    /// <summary>
    /// A picture reading that does not stand up settles nothing. Here the model reports a quantity
    /// for the row the scan destroyed but no price to check it with, so there is no arithmetic behind
    /// the count and it goes to the operator instead — which is the whole difference between a second
    /// reader and an oracle.
    /// </summary>
    [Fact]
    public void AQuantityWithNothingBehindItIsAskedAboutRatherThanTaken()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var reading = TheDeliveriesOnlyTicketAsPrinted();
        var weakened = reading with
        {
            Rows = [.. reading.Rows.Select((r, i) => i is 0 or 28 ? r with { Price = null } : r)],
        };

        var prompt = new ScriptedPrompt("TEST-4");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "4";
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        var outcome = Service(tmp, prompt, new FakeVision(weakened))
            .ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.LineFields.Count);
        Assert.All(prompt.LineFields, r => Assert.Equal(LineField.Units, r.Field));

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(236, xml.Descendants("InvoiceUnitQty").Sum(q => int.Parse(q.Value)));
    }

    /// <summary>
    /// What that question looks like. The operator is holding the paper, so it names the one column
    /// they have to find and shows them the row it is on, cut out of the scan.
    ///
    /// Bimbo prints one line per product, which is why the snip is a single line here where Dr
    /// Pepper's and Red Bull's are two — both of those carry the barcode on a line of its own.
    /// </summary>
    [Fact]
    public void TheQuestionNamesTheColumnAndShowsTheRow()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("TEST-5");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "4";
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        Service(tmp, prompt).ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        var asked = prompt.LineFields[0];
        Assert.Equal("Bimbo", asked.Vendor);
        Assert.Equal(LineField.Units, asked.Field);
        Assert.Equal("QTY", asked.ColumnName);
        Assert.Contains("QTY", asked.ColumnLegend, StringComparison.Ordinal);
        Assert.Contains("WHOLESALE", asked.ColumnLegend, StringComparison.Ordinal);
        Assert.False(asked.WholePage);
        Assert.NotNull(asked.Snip);

        // The yellow marker belongs on the QTY cell, not across all eight figures on the row.
        // Bimbo's fixed form puts QTY just right of centre; the measured band is deliberately much
        // narrower than either neighbouring money column.
        Assert.True(asked.Snip!.FocusWidth < asked.Snip.Width * 0.15,
            $"the QTY marker covers {asked.Snip.FocusWidth} of {asked.Snip.Width}px");
        double centre = (asked.Snip.FocusLeft + asked.Snip.FocusWidth / 2d) / asked.Snip.Width;
        Assert.InRange(centre, 0.53, 0.61);

        // No hint: there are no cases on this vendor, so there is nothing to convert and nothing to
        // get wrong. A hint nobody needs is a hint nobody reads.
        Assert.Equal("", asked.Hint);
    }

    /// <summary>
    /// Even with no second reader at all, this ticket now converts — one question per damaged row
    /// instead of a refusal over the whole delivery. That is the change the store actually feels:
    /// before, a scan that lost two figures out of forty rows cost the entire ticket.
    /// </summary>
    [Fact]
    public void WithoutVisionTheOperatorIsAskedRatherThanTheTicketRefused()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("090825143000D");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "4";
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        var outcome = Service(tmp, prompt).ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(2, prompt.LineFields.Count);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(40, xml.Descendants("LineItem").Count());
        Assert.Equal("443.68", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
    }

    /// <summary>
    /// A wrong answer to that question does not reach SSCS. The ticket prints its own count, so a
    /// quantity typed short leaves the lines disagreeing with it and the file is refused — which is
    /// the point of asking rather than guessing in the first place.
    /// </summary>
    [Fact]
    public void ATypedQuantityStillHasToReachTheTicketsOwnCount()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("TEST-6");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "3";     // the paper says 4
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        var outcome = Service(tmp, prompt).ConvertFile(Bimbo(Place(tmp, QtyColumnLost)), dest);

        Assert.False(outcome.Success);
        Assert.Equal(PdfReasonCodes.QtyMismatch, outcome.ReasonCode);
        Assert.Empty(Directory.GetFiles(dest));
    }

    // ─────────────────────────────────────────────────────────── the page with no text layer at all

    /// <summary>
    /// Ticket 84294890013049 as it is printed: eight delivered rows and three that went back, with
    /// TICKET TOTALS 23 and $48.00 at the foot.
    /// </summary>
    private static VisionReading TheImageOnlyTicketAsPrinted() => new()
    {
        Rows =
        [
            Row("7432301085", "008771", "BIM PANQUE CAS FS FC", 1, 3.0200m, 3.02m),
            Row("7432309122", "008931", "BIM MINHANT SS4P FC", 4, 1.9400m, 7.76m),
            Row("7432309535", "008802", "MLA PINGUINO SS2P FC", 3, 1.9400m, 5.82m),
            Row("7432309577", "008807", "MLA CHCRL PNA SS2PBX", 5, 1.9400m, 9.70m),
            Row("7432373920", "950913", "MLA CINNAS SNKCK 2P", 4, 1.9400m, 7.76m),
            Row("7087004183", "001825", "MB GLZ CIN RL SS 4Z", 5, 1.9400m, 9.70m),
            Row("7087000743", "007967", "MB CHERRY PIE SS 4Z", 4, 1.9400m, 7.76m),
            Row("7087000200", "007988", "MB LG WHI RTBRD 2OZ", 2, 3.0900m, 6.18m),
            Row("7432309145", "008783", "BIM CONCHAS SS2P FC", 2, 1.9400m, 3.88m, credit: true),
            Row("7432373951", "968113", "BIM HRSH MIN CROSS6P", 2, 1.9400m, 3.88m, credit: true),
            Row("7087073402", "014155", "MB PRD DON SS 6P 3Z", 1, 1.9400m, 1.94m, credit: true),
        ],
        PrintedTotal = 48.00m,
        PrintedUnits = 23,
    };

    /// <summary>
    /// A scan with the OCR switched off used to be the end of the road — "re-scan it with the
    /// searchable PDF setting turned on" — and the page itself is perfectly legible. Now the picture
    /// is the invoice, and it reaches the ticket's own $48.00 across eleven lines.
    /// </summary>
    [Fact]
    public void APageWithNoTextLayerIsReadOffThePicture()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("070726143000D"),
                new FakeVision(TheImageOnlyTicketAsPrinted()))
            .ConvertFile(Bimbo(Place(tmp, ImageOnly)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(outcome.FinalOutputPaths[0]);
        Assert.Equal(11, xml.Descendants("LineItem").Count());
        Assert.Equal("48.00", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(23, xml.Descendants("InvoiceUnitQty").Sum(q => int.Parse(q.Value)));
    }

    /// <summary>
    /// The returns go in as negative quantities on the same invoice, which is how live SSCS booked
    /// this vendor's ticket in the first place — one paidout, deliveries and returns together.
    /// </summary>
    [Fact]
    public void TheReturnsOffThePictureGoInAsNegativeLines()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("TEST-7"),
                new FakeVision(TheImageOnlyTicketAsPrinted()))
            .ConvertFile(Bimbo(Place(tmp, ImageOnly)), dest);

        var quantities = XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitQty").Select(q => int.Parse(q.Value)).ToList();

        Assert.Equal(3, quantities.Count(q => q < 0));
        Assert.Equal([-2, -2, -1], quantities.Where(q => q < 0));
    }

    /// <summary>
    /// And it is said out loud. Every figure on that file came from one reader with no transcription
    /// to check it against, which is a materially different thing from a ticket both readers saw.
    /// </summary>
    [Fact]
    public void SayingSoWhenTheWholeTicketCameOffThePicture()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var outcome = Service(tmp, new ScriptedPrompt("TEST-8"),
                new FakeVision(TheImageOnlyTicketAsPrinted()))
            .ConvertFile(Bimbo(Place(tmp, ImageOnly)), dest);

        Assert.Contains(outcome.Warnings, w =>
            w.Message.Contains("read off the picture of the page instead", StringComparison.Ordinal));
    }

    /// <summary>
    /// A row off the picture that does not multiply out is not billed on one reader's say-so. Here
    /// the model reports 9 units against a price and an amount that come to 4, so the count has
    /// nothing behind it and the operator reads it off the paper.
    ///
    /// This is the guard that matters most on this path: with no text layer there is no second
    /// transcription of anything, so the row's own arithmetic is the only thing standing between a
    /// misread digit and the register.
    /// </summary>
    [Fact]
    public void ARowOffThePictureThatDoesNotMultiplyOutIsAskedAbout()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var reading = TheImageOnlyTicketAsPrinted();
        var wrong = reading with
        {
            Rows = [.. reading.Rows.Select((r, i) => i == 1 ? r with { Units = 9 } : r)],
        };

        var prompt = new ScriptedPrompt("TEST-9");
        prompt.Paper["BIM MINHANT SS4P FC"] = "4";

        var outcome = Service(tmp, prompt, new FakeVision(wrong))
            .ConvertFile(Bimbo(Place(tmp, ImageOnly)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal(LineField.Units, asked.Field);

        // No line number to cut by on a page with no text layer, so the whole page is shown and the
        // dialog has to say which row is wanted rather than mark one.
        Assert.True(asked.WholePage);
        Assert.Contains("find the row by its name", asked.Hint, StringComparison.Ordinal);

        Assert.Equal(23, XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitQty").Sum(q => int.Parse(q.Value)));
    }

    /// <summary>
    /// A row the model could not read a barcode off is asked about too — the one field on this
    /// vendor that nothing can check, on the one path where the text layer is not there to supply it.
    /// </summary>
    [Fact]
    public void ARowOffThePictureWithNoBarcodeIsAskedAbout()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var reading = TheImageOnlyTicketAsPrinted();
        var blanked = reading with
        {
            Rows = [.. reading.Rows.Select((r, i) => i == 0 ? r with { Barcode = null } : r)],
        };

        var prompt = new ScriptedPrompt("TEST-10");
        prompt.Paper["BIM PANQUE CAS FS FC"] = "74323-01085";

        var outcome = Service(tmp, prompt, new FakeVision(blanked))
            .ConvertFile(Bimbo(Place(tmp, ImageOnly)), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal(LineField.Barcode, asked.Field);
        Assert.Contains("10 digits under UPC", asked.Hint, StringComparison.Ordinal);

        // The dash is punctuation, and the ten digits become the whole UPC-A the way a printed code
        // does — SSCS then drops the check digit again and lands on 07432301085.
        Assert.Contains("074323010855", XDocument.Load(outcome.FinalOutputPaths[0])
            .Descendants("InvoiceUnitId").Select(e => e.Value));
    }

    // ───────────────────────────────────────────────────────────────────────── leaving well alone

    /// <summary>
    /// A ticket the text layer reads perfectly well is not touched by any of this. The picture is a
    /// second reader, not a replacement, and the file it produces has to be the same one the store
    /// has been sending since July.
    /// </summary>
    [Fact]
    public void AGoodTicketConvertsIdenticallyWithAndWithoutTheSecondReader()
    {
        static byte[] Convert(IInvoiceVisionReader? vision)
        {
            using var tmp = new TempDir();
            string dest = tmp.Sub("out");
            string path = Path.Combine(tmp.Path, "DownloadFile.pdf");
            File.WriteAllBytes(path, File.ReadAllBytes(
                @"Fixtures\PDF\BIMBO_84294890013239_sscs_verified.pdf"));

            var outcome = new ConversionService(
                    new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test",
                    new ScriptedPrompt("072826132008D"), vision)
                .ConvertFile(Bimbo(path), dest);

            Assert.True(outcome.Success, outcome.UserMessage);
            return outcome.Documents[0].Bytes;
        }

        // A reading that disagrees about everything, so that any leakage into the output shows up.
        var noise = new VisionReading
        {
            Rows = [Row("9999999999", "999999", "NOT A REAL ROW", 99, 9.9900m, 989.01m)],
            PrintedTotal = 1m,
            PrintedUnits = 1,
        };

        TestUtil.AssertBytesEqual(Convert(null), Convert(new FakeVision(noise)));
    }
}
