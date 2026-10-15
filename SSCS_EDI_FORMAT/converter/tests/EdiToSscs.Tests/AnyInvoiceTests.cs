using System.Net;
using System.Text;
using System.Text.Json;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// PDF to Modisoft EDI with no vendor dropdown (2026-10-15): any vendor's invoice, read by AI in
/// one request, written as the Modisoft file. The rows here are taken from invoices that were
/// converted by hand and imported into Modisoft exactly (tools/ModisoftFile/examples).
/// </summary>
public class AnyInvoiceTests
{
    private static AnyInvoiceVisionRow Row(
        string description, string? barcode, string? quantity, string? amount,
        string? billedBy = "case", string? unitsPerCase = null, string? unitsSource = "printed",
        string? totalUnits = null, string? price = null, bool? credit = false) => new()
    {
        Description = description, Barcode = barcode, Quantity = quantity, Amount = amount,
        BilledBy = billedBy, UnitsPerCase = unitsPerCase, UnitsSource = unitsSource,
        TotalUnits = totalUnits, Price = price, IsCredit = credit,
    };

    /// <summary>Two Silver Eagle 424753 lines, which imported with every figure right.</summary>
    private static AnyInvoiceVisionReading SilverEagle() => new()
    {
        Status = AnyInvoiceVisionStatus.Success,
        VendorName = "Silver Eagle Beverages, LLC",
        Rows =
        [
            Row("MOD ESP 2/12 LNNR", "080660957579", "7", "249.20", unitsPerCase: "2", price: "35.60"),
            Row("MOD ESP 24/12 SUITCASE", "080660957104", "9", "268.74", unitsPerCase: "1", price: "29.86"),
        ],
        InvoiceTotal = "517.94",
        TotalQuantity = "16",
    };

    // ------------------------------------------------------------------ barcodes

    [Theory]
    [InlineData("080660957579", "", "080660957579")]           // whole UPC-A
    [InlineData("0-80660-95757-9", "", "080660957579")]        // printed with dashes
    [InlineData("08066095757", "", "080660957579")]            // 11-digit body: check digit added
    [InlineData("7501011130869", "", "7501011130869")]         // EAN-13 stays whole
    [InlineData("0080660957579", "", "080660957579")]          // EAN-13 that is a UPC-A: 0 + UPC-A matched nothing
    [InlineData("00080660957579", "", "080660957579")]         // Silver Eagle's GTIN-14 pack UPC
    [InlineData("04963406", "", "049000006346")]               // UPC-E expanded
    [InlineData("7432309183", "Bimbo Bakeries USA", "074323091830")]   // Bimbo's ten-digit body
    [InlineData("007189905101", "Blue Bell Creameries", "071899051015")] // Blue Bell's padded body
    public void APrintedCodeBecomesTheWholeBarcode(string printed, string vendor, string expected)
    {
        var code = AnyInvoiceParser.Barcode(printed, vendor);

        Assert.Equal(expected, code.Gtin);
        Assert.Equal(AnyInvoiceParser.BarcodeProblem.None, code.Problem);
    }

    /// <summary>
    /// Blue Bell's padded body is only undone on Blue Bell. Anywhere else the same twelve digits
    /// are a UPC-A that fails its check digit, and that ships as read and is named.
    /// </summary>
    [Fact]
    public void ACodeFailingItsCheckDigitShipsAsReadAndIsNeverRepaired()
    {
        var code = AnyInvoiceParser.Barcode("007189905101", "Some Distributor");

        Assert.Equal("007189905101", code.Gtin);
        Assert.Equal(AnyInvoiceParser.BarcodeProblem.FailsCheckDigit, code.Problem);
    }

    [Fact]
    public void ATenDigitBodyFromAnUnknownVendorIsCompletedAndSaidOutLoud()
    {
        var code = AnyInvoiceParser.Barcode("7432309183", "Corner Bakery Supply");

        Assert.Equal("074323091830", code.Gtin);
        Assert.Equal(AnyInvoiceParser.BarcodeProblem.Completed, code.Problem);
    }

    [Theory]
    [InlineData(null, AnyInvoiceParser.BarcodeProblem.NotPrinted)]
    [InlineData("", AnyInvoiceParser.BarcodeProblem.NotPrinted)]
    [InlineData("84073", AnyInvoiceParser.BarcodeProblem.Unusable)]          // Frito-Lay's last five
    [InlineData("10080660957576", AnyInvoiceParser.BarcodeProblem.Unusable)] // a case code
    public void ALineWithNoUsableCodeHasNoBarcode(string? printed, AnyInvoiceParser.BarcodeProblem problem)
    {
        var code = AnyInvoiceParser.Barcode(printed, "");

        Assert.Null(code.Gtin);
        Assert.Equal(problem, code.Problem);
    }

    // ------------------------------------------------------------------ counting

    [Fact]
    public void CasesWithAPrintedPackGoInAsThoseCases()
    {
        var count = AnyInvoiceParser.Count(Row("MOD ESP 2/12", "080660957579", "7", "249.20", unitsPerCase: "2"));

        Assert.Equal((14, (int?)7, false), count);
    }

    /// <summary>Dr Pepper's CASES and UNITS: the printed units decide the pack.</summary>
    [Fact]
    public void APrintedUnitCountDecidesThePack()
    {
        var count = AnyInvoiceParser.Count(Row("12Z12P DR PEPPER", "078000082401", "2", "25.50",
            unitsPerCase: "24", unitsSource: "description", totalUnits: "4"));

        Assert.Equal((4, (int?)2, false), count);
    }

    /// <summary>Dr Pepper's credit prints CASES 0 against UNITS 1; Red Bull's a QTY of 0.25.</summary>
    [Theory]
    [InlineData("0", "1", 1)]
    [InlineData("0.25", "(6)", 6)]
    public void ACreditWithNoWholeCaseGoesInOneItemAtATime(string quantity, string units, int expected)
    {
        var count = AnyInvoiceParser.Count(Row("CREDIT", "078000082401", quantity, "6.37",
            totalUnits: units, credit: true));

        Assert.Equal((expected, (int?)null, false), count);
    }

    /// <summary>
    /// A line billed by the item goes in by the item, whatever it is boxed in, so Modisoft's Qty is
    /// the paper's own count: Blue Bell's half gallons (QTY 12 @ 6.85, four to a box) imported that
    /// way and matched on 2026-10-09. The real read of that ticket on 2026-10-15 had regrouped
    /// them into 3 boxes of 4.
    /// </summary>
    [Fact]
    public void ALineBilledByTheItemGoesInByTheItem()
    {
        var count = AnyInvoiceParser.Count(Row("1/2 GAL GR SUP", "007189903720", "12.00", "82.20",
            billedBy: "item", unitsPerCase: "4"));

        Assert.Equal((12, (int?)null, false), count);
    }

    /// <summary>A Blue Bell DZ carton is billed per dozen: two cartons of twelve.</summary>
    [Fact]
    public void ADozenBilledByTheCartonGoesInAsCartonsOfTwelve()
    {
        var count = AnyInvoiceParser.Count(Row("DZ 3OZ HMV CUPS", "007189921133", "2.00", "12.72",
            unitsPerCase: "12", unitsSource: "description"));

        Assert.Equal((24, (int?)2, true), count);
    }

    [Fact]
    public void APackTakenFromTheDescriptionIsFlagged()
    {
        var count = AnyInvoiceParser.Count(Row("BUSCH 24/16 OZ CAN", "018200284373", "3", "60.00",
            unitsPerCase: "4", unitsSource: "description"));

        Assert.Equal((12, (int?)3, true), count);
    }

    // ------------------------------------------------------------------ the whole reading

    [Fact]
    public void ALineTurnsIntoCasesAtTheCaseCostModisoftImported()
    {
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(SilverEagle(), warnings);
        var records = ModisoftEdiBuilder.Lines(ticket.ToInvoice("424753", new DateOnly(2026, 10, 6)), warnings);

        Assert.Equal(new GhraLine("080660957579", "MOD ESP 2/12 LNNR", "", 35.60m, 2, 7, 0m), records[0]);
        Assert.Equal(new GhraLine("080660957104", "MOD ESP 24/12 SUITCASE", "", 29.86m, 1, 9, 0m), records[1]);
        Assert.Equal(517.94m, ticket.Total);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A unit cost rounded to SSCS's four places, times 300 items, lands a cent off the printed
    /// amount: 100.25 / 300 is 0.3342, and 300 x 0.3342 is 100.26. These lines only go to Modisoft,
    /// so they carry the line's own money exactly.
    /// </summary>
    [Fact]
    public void ABigLineKeepsItsPrintedAmountToTheCent()
    {
        var reading = SilverEagle() with
        {
            Rows = [Row("BIG LINE", "080660957579", "25", "100.25", unitsPerCase: "12")],
            InvoiceTotal = "100.25",
            TotalQuantity = null,
        };
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(reading, warnings);
        var record = Assert.Single(ModisoftEdiBuilder.Lines(ticket.ToInvoice("1", new DateOnly(2026, 10, 15)), warnings));

        Assert.Equal(100.25m, ticket.Lines[0].ExtendedCost);
        Assert.Equal(4.01m, record.CaseCost);
        Assert.Equal(25, record.Cases);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ACreditGoesInNegativeOnTheSamePurchase()
    {
        var reading = SilverEagle() with
        {
            Rows =
            [
                .. SilverEagle().Rows,
                Row("12Z12P AW ROOT BEER", "078000082401", "0", "(6.37)", totalUnits: "1", credit: true),
            ],
            InvoiceTotal = "511.57",
            TotalQuantity = null,
        };
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(reading, warnings);
        var records = ModisoftEdiBuilder.Lines(ticket.ToInvoice("1", new DateOnly(2026, 10, 15)), warnings);

        Assert.Equal(new GhraLine("078000082401", "12Z12P AW ROOT BEER", "", 6.37m, 1, -1, 0m), records[2]);
        Assert.All(warnings, warning => Assert.True(warning.Routine, warning.Message));
    }

    /// <summary>
    /// Blue Bell 033100786112 prints TOTAL 328.48 on its sales and the 2.98 return separately,
    /// and the real read returned the sales figure. That agrees with the deliveries, so nothing is
    /// said; only the routine credit note is.
    /// </summary>
    [Fact]
    public void ASalesTotalThatMatchesTheDeliveriesIsNotADisagreement()
    {
        var reading = SilverEagle() with
        {
            Rows =
            [
                .. SilverEagle().Rows,
                Row("PT GR SUPREME", "007189905101", "1", "2.98", billedBy: "item", credit: true),
            ],
            VendorName = "Blue Bell Creameries, L.P.",
            InvoiceTotal = "517.94",
            TotalQuantity = null,
        };
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(reading, warnings);

        Assert.Equal("071899051015", ticket.Lines[2].Gtin);
        Assert.Equal(-1, ticket.Lines[2].Units);
        Assert.All(warnings, warning => Assert.True(warning.Routine, warning.Message));
    }

    /// <summary>
    /// Tax and deposits are part of the paper's total but are not products. The file carries the
    /// products, and the note says what to add by hand.
    /// </summary>
    [Fact]
    public void ChargesThatAreNotProductsAreNamedNotWritten()
    {
        var reading = SilverEagle() with
        {
            Charges = [new AnyInvoiceCharge("SALES TAX", "4.12"), new AnyInvoiceCharge("DEPOSIT", "2.40")],
            InvoiceTotal = "524.46",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(reading, warnings);

        Assert.Equal(2, ticket.Lines.Count);
        var note = Assert.Single(warnings);
        Assert.Equal(PdfReasonCodes.OtherCharges, note.ReasonCode);
        Assert.Contains("SALES TAX 4.12", note.Message, StringComparison.Ordinal);
        Assert.Contains("517.94", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATotalThatDoesNotAddUpIsSaidAndTheCrookedLineNamed()
    {
        var reading = SilverEagle() with
        {
            Rows =
            [
                Row("MOD ESP 2/12 LNNR", "080660957579", "7", "294.20", unitsPerCase: "2", price: "35.60"),
                SilverEagle().Rows[1],
            ],
        };
        var warnings = new List<ConversionWarning>();

        AnyInvoiceParser.Parse(reading, warnings);

        var note = Assert.Single(warnings, warning => warning.ReasonCode == PdfReasonCodes.TotalMismatch);
        Assert.Contains("517.94", note.Message, StringComparison.Ordinal);
        Assert.Contains("MOD ESP 2/12 LNNR (7 x 35.60 is not 294.20)", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SUITCASE", note.Message, StringComparison.Ordinal);
    }

    /// <summary>Faust prints no barcodes. A line without one is left out with its money; the rest go in.</summary>
    [Fact]
    public void ALineWithNoBarcodeIsLeftOutAndNamedWithItsMoney()
    {
        var reading = SilverEagle() with
        {
            Rows = [.. SilverEagle().Rows, Row("KEYSTONE LT 15PK CN", null, "2", "31.90", unitsPerCase: "2")],
            InvoiceTotal = "549.84",
            TotalQuantity = null,
        };
        var warnings = new List<ConversionWarning>();

        var ticket = AnyInvoiceParser.Parse(reading, warnings);

        Assert.Equal(2, ticket.Lines.Count);
        var note = Assert.Single(warnings, warning => warning.ReasonCode == PdfReasonCodes.NoBarcode);
        Assert.Contains("KEYSTONE LT 15PK CN (31.90)", note.Message, StringComparison.Ordinal);
        Assert.Contains("in Modisoft by hand", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvoiceThatPrintsNoBarcodesAtAllIsRefusedWithTheReason()
    {
        var reading = SilverEagle() with
        {
            Rows = [Row("KEYSTONE LT 15PK CN", null, "2", "31.90"), Row("COORS LT 18PK", null, "1", "17.50")],
        };

        var ex = Assert.Throws<EdiConversionException>(() => AnyInvoiceParser.Parse(reading, []));

        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
        Assert.Contains("prints a barcode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitsTakenFromTheDescriptionAreNamedForCheckingInModisoft()
    {
        var reading = SilverEagle() with
        {
            Rows = [Row("BUSCH 24/16 OZ CAN", "018200284373", "3", "60.00", unitsPerCase: "4", unitsSource: "description")],
            InvoiceTotal = "60.00",
            TotalQuantity = null,
        };
        var warnings = new List<ConversionWarning>();

        AnyInvoiceParser.Parse(reading, warnings);

        var note = Assert.Single(warnings);
        Assert.Equal(PdfReasonCodes.UnitsNotPrinted, note.ReasonCode);
        Assert.Contains("BUSCH 24/16 OZ CAN (3 x 4)", note.Message, StringComparison.Ordinal);
        Assert.False(note.Routine);
    }

    [Fact]
    public void APrintedCountTheLinesDoNotReachIsSaid()
    {
        var reading = SilverEagle() with { TotalQuantity = "17" };
        var warnings = new List<ConversionWarning>();

        AnyInvoiceParser.Parse(reading, warnings);

        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.QtyMismatch);
    }

    [Theory]
    [InlineData("Silver Eagle Beverages, LLC", "SILVEREAGLE")]
    [InlineData("The Faust Distributing Co.", "FAUSTDISTRIBUTING")]
    [InlineData("Dr Pepper Bottling Company of Texas", "DRPEPPER")]
    [InlineData("", "INVOICE")]
    public void TheFileIsNamedAfterTheVendorsFirstTwoWords(string vendor, string token)
    {
        Assert.Equal(token, AnyInvoiceParser.SupplierToken(vendor));
    }

    // ------------------------------------------------------------------ through the converter

    private sealed class Prompt : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Details { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Details.Add(request);
            return new InvoiceDetails("424753", new DateOnly(2026, 10, 6));
        }
    }

    private sealed class Vision(AnyInvoiceVisionReading reading) : IInvoiceVisionReader, IAnyInvoiceVisionReader
    {
        public List<byte[]> Read { get; } = [];

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;

        public AnyInvoiceVisionReading ReadAnyInvoice(byte[] fileBytes)
        {
            Read.Add(fileBytes);
            return reading;
        }
    }

    private static ConversionOutcome Convert(TempDir tmp, string name, byte[] bytes, IInvoiceVisionReader? vision, Prompt prompt)
    {
        string input = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(input, bytes);
        var service = new ConversionService(new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);
        return service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Any, Target = OutputTarget.Modisoft,
        }, tmp.Sub("out"));
    }

    [Fact]
    public void AnyVendorsPdfBecomesAModisoftFileNamedAfterTheVendor()
    {
        using var tmp = new TempDir();
        var prompt = new Prompt();
        var vision = new Vision(SilverEagle());

        var outcome = Convert(tmp, "invoice.pdf", File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"),
            vision, prompt);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Single(vision.Read);
        var request = Assert.Single(prompt.Details);
        Assert.Equal("Silver Eagle Beverages, LLC", request.Vendor);
        Assert.Equal(517.94m, request.Total);

        var doc = Assert.Single(outcome.Documents);
        Assert.Equal("MODISOFT_SILVEREAGLE_424753.txt", doc.FileName);
        string[] records = Encoding.ASCII.GetString(doc.Bytes).Split("\r\n");
        Assert.Equal("GHRA   00004247531006260000051794", records[0]);
        Assert.StartsWith("B080660957579 MOD ESP 2/12 LNNR", records[1], StringComparison.Ordinal);
        Assert.Equal(ModisoftEdiBuilder.ChargeRecord, records[^1]);
    }

    [Fact]
    public void APhotoIsTakenAsWellAsAPdf()
    {
        using var tmp = new TempDir();
        var vision = new Vision(SilverEagle());
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0];

        var outcome = Convert(tmp, "IMG_2997.jpg", jpeg, vision, new Prompt());

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(jpeg, Assert.Single(vision.Read));
    }

    [Fact]
    public void AFileThatIsNeitherAPdfNorAPhotoIsRefusedBeforeAnyRead()
    {
        using var tmp = new TempDir();
        var vision = new Vision(SilverEagle());

        var outcome = Convert(tmp, "notes.jpg", Encoding.ASCII.GetBytes("not a picture"), vision, new Prompt());

        Assert.False(outcome.Success);
        Assert.Empty(vision.Read);
    }

    [Theory]
    [InlineData(AnyInvoiceVisionStatus.Off, "switch it on")]
    [InlineData(AnyInvoiceVisionStatus.Unavailable, "Try this file again later")]
    [InlineData(AnyInvoiceVisionStatus.Unreadable, "clearer photo")]
    public void EachWayAReadCanFailSaysWhatToDo(AnyInvoiceVisionStatus status, string advice)
    {
        using var tmp = new TempDir();
        var prompt = new Prompt();
        var vision = new Vision(new AnyInvoiceVisionReading { Status = status });

        var outcome = Convert(tmp, "invoice.pdf", File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"),
            vision, prompt);

        Assert.False(outcome.Success);
        Assert.Contains(advice, outcome.UserMessage, StringComparison.Ordinal);
        Assert.Empty(prompt.Details);
    }

    [Fact]
    public void WithNoAiReaderAtAllTheTabSaysToSwitchItOn()
    {
        using var tmp = new TempDir();

        var outcome = Convert(tmp, "invoice.pdf", File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"),
            null, new Prompt());

        Assert.False(outcome.Success);
        Assert.Contains("switch it on", outcome.UserMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the Gemini request

    private sealed class Stub(string payload) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            string envelope = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                              + JsonSerializer.Serialize(payload) + "}]}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private const string Reply = """
        {"vendorName": "Silver Eagle Beverages, LLC",
         "rows": [{"itemNumber": "53032", "description": "MOD ESP 2/12 LNNR", "barcode": "080660957579",
                   "quantity": "7", "billedBy": "case", "unitsPerCase": "2", "unitsSource": "printed",
                   "totalUnits": null, "price": "35.60", "amount": "249.20", "isCredit": false}],
         "charges": [{"label": "SALES TAX", "amount": "0.00"}],
         "invoiceTotal": "249.20", "totalQuantity": "7"}
        """;

    private static (AnyInvoiceVisionReading Reading, JsonDocument Request) ReadThroughGemini(byte[] file)
    {
        var handler = new Stub(Reply);
        var reader = new GeminiVisionReader(["test-key"], ["gemini-test"], new HttpClient(handler), []);
        var reading = reader.ReadAnyInvoice(file);
        return (reading, JsonDocument.Parse(Assert.Single(handler.Requests)));
    }

    private static List<string> MimeTypes(JsonDocument request) =>
        request.RootElement.GetProperty("contents")[0].GetProperty("parts").EnumerateArray()
            .Where(part => part.TryGetProperty("inline_data", out _))
            .Select(part => part.GetProperty("inline_data").GetProperty("mime_type").GetString()!)
            .ToList();

    [Fact]
    public void TheReplyIsReadBackFieldForField()
    {
        var (reading, _) = ReadThroughGemini(File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"));

        Assert.Equal(AnyInvoiceVisionStatus.Success, reading.Status);
        Assert.Equal("Silver Eagle Beverages, LLC", reading.VendorName);
        var row = Assert.Single(reading.Rows);
        Assert.Equal("080660957579", row.Barcode);
        Assert.Equal("case", row.BilledBy);
        Assert.Equal("2", row.UnitsPerCase);
        Assert.Equal("249.20", reading.InvoiceTotal);
        Assert.Equal("SALES TAX", Assert.Single(reading.Charges).Label);
    }

    /// <summary>A store scan goes as its page pictures, exactly like the five known vendors'.</summary>
    [Fact]
    public void AScannedPdfIsSentAsItsPagePictures()
    {
        var (_, request) = ReadThroughGemini(File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"));

        var types = MimeTypes(request);
        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.StartsWith("image/", type, StringComparison.Ordinal));
    }

    /// <summary>
    /// Silver Eagle's emailed invoice-report.pdf has real text and no picture. Taking "the biggest
    /// picture on the page" from it would send nothing, or a logo, so the PDF goes whole.
    /// </summary>
    [Fact]
    public void APdfWithRealTextIsSentWhole()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText("SILVER EAGLE  INVOICE 428556  MOD ESP 2/12 LNNR  7  35.60  249.20", 10,
            new UglyToad.PdfPig.Core.PdfPoint(30, 700), font);
        byte[] pdf = builder.Build();

        var (reading, request) = ReadThroughGemini(pdf);

        Assert.Equal(AnyInvoiceVisionStatus.Success, reading.Status);
        Assert.Equal(["application/pdf"], MimeTypes(request));
    }

    [Fact]
    public void APhotoIsSentAsThePhoto()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0];

        var (_, request) = ReadThroughGemini(jpeg);

        Assert.Equal(["image/jpeg"], MimeTypes(request));
    }

    /// <summary>The invoice number and date are typed, never read: the reply has no room for either.</summary>
    [Fact]
    public void TheReplyHasNoFieldForAnInvoiceNumberOrDate()
    {
        var (_, request) = ReadThroughGemini(File.ReadAllBytes(@"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf"));
        var schema = request.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");

        var names = new List<string>();
        Collect(schema, names);

        Assert.Contains("invoiceTotal", names);
        Assert.DoesNotContain(names, name =>
            name.Contains("date", StringComparison.OrdinalIgnoreCase)
            || name.Contains("number", StringComparison.OrdinalIgnoreCase) && !name.Equals("itemNumber", StringComparison.Ordinal));

        static void Collect(JsonElement element, List<string> into)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            if (element.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                {
                    into.Add(property.Name);
                    Collect(property.Value, into);
                }
            if (element.TryGetProperty("items", out var items)) Collect(items, into);
        }
    }

    [Fact]
    public void PictureTypesAreKnownByTheirFirstBytesNotTheirName()
    {
        Assert.Equal("image/jpeg", InvoicePictures.PictureType([0xFF, 0xD8, 0xFF, 0xE1]));
        Assert.Equal("image/png", InvoicePictures.PictureType([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]));
        Assert.Equal("image/heic", InvoicePictures.PictureType(
            [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c']));
        Assert.Null(InvoicePictures.PictureType(Encoding.ASCII.GetBytes("%PDF-1.7")));
    }
}
