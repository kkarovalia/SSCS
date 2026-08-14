using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Chubiwinos' production contract: one complete-ticket picture read, then XML.</summary>
public class ChubiwinosVisionPrimaryTests
{
    private const string Fixture = @"Fixtures\PDF\CHUBIWINOS_6V9389.pdf";

    private static ChubiwinosVisionReading Reading() => new()
    {
        Status = ChubiwinosVisionStatus.Success,
        Rows =
        [
            new ChubiwinosVisionRow
            {
                ItemNumber = "220", Description = "CHEETO COLMILLO -100 g",
                Barcode = "7500478044207", Quantity = "20", Price = "2.79",
                Amount = "55.80", IsCredit = false,
            },
            new ChubiwinosVisionRow
            {
                ItemNumber = "205", Description = "PAPAS ADOBADAS 105 g",
                Barcode = "7501011167667", Quantity = "1", Price = "2.79",
                Amount = "2.79", IsCredit = true,
            },
        ],
        SectionTotals =
        [
            new ChubiwinosVisionSectionTotal
                { Section = "SALES", Quantity = "20", Amount = "55.80" },
            new ChubiwinosVisionSectionTotal
                { Section = "DUMP", Quantity = "1", Amount = "(2.79)" },
        ],
        Sales = "55.80",
        Credits = "(2.79)",
        NetAmount = "53.01",
        SalesTax = "0.00",
        TotalDue = "53.01",
        TotalPayment = "0.00",
        InvoiceBalance = "53.01",
        VendorMarkers = ["CHUBIWINOS S&G", "SALES SECTION", "INVOICE BALANCE"],
    };

    private sealed class Prompt : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Details { get; } = [];
        public List<LineFieldRequest> Fields { get; } = [];
        public List<BarcodeRequest> Barcodes { get; } = [];
        public int FallbackQuestions { get; private set; }
        public bool AllowFallback { get; set; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Details.Add(request);
            return new InvoiceDetails("OPERATOR-CHUBIWINOS", new DateOnly(2026, 8, 14));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            Fields.Add(request);
            return null;
        }

        public string? AskBarcode(BarcodeRequest request)
        {
            Barcodes.Add(request);
            return null;
        }

        public bool ConfirmChubiwinosOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return AllowFallback;
        }
    }

    private sealed class Vision(ChubiwinosVisionReading reading)
        : IInvoiceVisionReader, IChubiwinosVisionReader
    {
        public int PrimaryReads { get; private set; }
        public int GenericReads { get; private set; }

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
        {
            GenericReads++;
            return VisionReading.Empty;
        }

        public ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes)
        {
            PrimaryReads++;
            return reading;
        }
    }

    [Fact]
    public void AiRowsTurnStraightIntoInvoiceLinesAndDumpStaysOnThePayable()
    {
        var warnings = new List<ConversionWarning>();

        var ticket = ChubiwinosVisionParser.Parse(Reading(), warnings);

        Assert.Equal(2, ticket.Lines.Count);
        Assert.Equal(19, ticket.Lines.Sum(line => line.Units));
        Assert.Equal(53.01m, ticket.Total);
        Assert.Equal(2.79m, ticket.Lines[0].UnitCost);
        Assert.Equal(-1, ticket.Lines[1].Units);
        Assert.Equal(2.79m, ticket.Lines[1].UnitCost);
        Assert.DoesNotContain(warnings, warning => !warning.Routine);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.CreditLines && warning.Routine);
    }

    [Fact]
    public void AThirteenDigitEanSurvivesIntact()
    {
        var ticket = ChubiwinosVisionParser.Parse(Reading(), []);

        Assert.Equal("7500478044207", ticket.Lines[0].Gtin);
        Assert.Equal(13, ticket.Lines[0].Gtin.Length);
    }

    [Fact]
    public void Item905ElevenDigitBodyGetsItsExactCheckDigitAndReachesXml()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "chubiwinos.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var reading = Reading() with
        {
            Rows =
            [
                new ChubiwinosVisionRow
                {
                    ItemNumber = "905", Description = "WINNUTS PAPAS DIABLA-6oz.",
                    Barcode = "85088300206", Quantity = "12", Price = "2.49",
                    Amount = "29.88", IsCredit = false,
                },
            ],
            SectionTotals =
            [
                new ChubiwinosVisionSectionTotal
                    { Section = "SALES", Quantity = "12", Amount = "29.88" },
            ],
            Sales = "29.88", Credits = null, NetAmount = "29.88", SalesTax = "0.00",
            TotalDue = "29.88", TotalPayment = "0.00", InvoiceBalance = "29.88",
        };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, new Vision(reading));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Equal("850883002062",
            Assert.Single(xml.Descendants("InvoiceUnitId")).Value);
        Assert.Equal("12", Assert.Single(xml.Descendants("InvoiceUnitQty")).Value);
        Assert.Equal("2.4900", Assert.Single(xml.Descendants("InvoiceUnitCost")).Value);
        Assert.Equal("29.88", Assert.Single(xml.Descendants("TotalInvoiceDueAmt")).Value);
        Assert.DoesNotContain(outcome.Warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UnreadableLine);
        Assert.Empty(prompt.Fields);
        Assert.Empty(prompt.Barcodes);
    }

    [Fact]
    public void ABarcodeFailingItsCheckDigitShipsAsReadAndFlagged()
    {
        const string Printed = "850158007331";
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Barcode = Printed }],
            SectionTotals =
            [
                new ChubiwinosVisionSectionTotal
                    { Section = "SALES", Quantity = "20", Amount = "55.80" },
            ],
            Sales = "55.80", Credits = null, NetAmount = "55.80", TotalDue = "55.80",
            InvoiceBalance = "55.80",
        };

        var ticket = ChubiwinosVisionParser.Parse(reading, warnings);

        Assert.Equal(Printed, Assert.Single(ticket.Lines).Gtin);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UpcCheckDigit
            && warning.Message.Contains(Printed, StringComparison.Ordinal));
    }

    [Fact]
    public void ArithmeticDisagreementsWarnAndThePrintedQuantityAndPriceStillShip()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Amount = "55.79" }],
            SectionTotals =
            [
                new ChubiwinosVisionSectionTotal
                    { Section = "SALES", Quantity = "20", Amount = "55.79" },
            ],
            Sales = "55.79", Credits = null, NetAmount = "55.79", TotalDue = "55.79",
            InvoiceBalance = "55.79",
        };

        var ticket = ChubiwinosVisionParser.Parse(reading, warnings);

        var line = Assert.Single(ticket.Lines);
        Assert.Equal(20, line.Units);
        Assert.Equal(2.79m, line.UnitCost);
        Assert.Equal(55.80m, ticket.Total);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.LineMathMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    [Fact]
    public void AnUnpostableRowIsDroppedNamedWithItsMoneyAndTheRestConverts()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows =
            [
                Reading().Rows[0],
                Reading().Rows[1] with { Barcode = null, Amount = "$2.79" },
            ],
            SectionTotals =
            [
                new ChubiwinosVisionSectionTotal
                    { Section = "SALES", Quantity = "20", Amount = "55.80" },
            ],
            Credits = null, NetAmount = "55.80", TotalDue = "55.80",
            InvoiceBalance = "55.80",
        };

        var ticket = ChubiwinosVisionParser.Parse(reading, warnings);

        Assert.Single(ticket.Lines);
        var skipped = Assert.Single(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UnreadableLine);
        Assert.Contains("2.79", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("PAPAS ADOBADAS", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPostableRowIsTheOnlyBusinessRefusal()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Quantity = "-8-" }],
        };

        var ex = Assert.Throws<EdiConversionException>(() =>
            ChubiwinosVisionParser.Parse(reading, []));

        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
    }

    [Fact]
    public void VisionPathMakesOneReadAndAsksOnlyForInvoiceNumberAndDate()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "chubiwinos.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var vision = new Vision(Reading());
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(1, vision.PrimaryReads);
        Assert.Equal(0, vision.GenericReads);
        Assert.Empty(prompt.Fields);
        Assert.Empty(prompt.Barcodes);
        var request = Assert.Single(prompt.Details);
        Assert.True(request.NeedsDate);

        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Contains("OPERATOR-CHUBIWINOS", xml.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-08-14", xml.ToString(), StringComparison.Ordinal);
        Assert.Contains("7500478044207", xml.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvoiceWhosePrintedGrandTotalIsUnreadableStillConverts()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "chubiwinos.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var reading = Reading() with
        {
            NetAmount = null, TotalDue = null, InvoiceBalance = null,
        };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, new Vision(reading));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.Fields);
        Assert.Empty(prompt.Barcodes);
        Assert.Contains(outcome.Warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    [Fact]
    public void UnreadableAiRequiresARescanAndNeverOffersOcr()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "chubiwinos.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(ChubiwinosVisionReading.Unreadable()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos,
        }, tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Contains("Re-scan", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prompt.FallbackQuestions);
    }

    [Fact]
    public void DeclinedOcrFallbackIsAskedOnlyOnceForAQueueBatchAndReportsCanceled()
    {
        using var tmp = new TempDir();
        var prompt = new Prompt();
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(ChubiwinosVisionReading.Unavailable()));
        service.BeginBatch();

        for (int index = 0; index < 2; index++)
        {
            string input = Path.Combine(tmp.Path, $"chubiwinos-{index}.pdf");
            File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
            var outcome = service.ConvertFile(new QueuedFile
            {
                InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Chubiwinos,
            }, tmp.Sub("out"));
            Assert.False(outcome.Success);
            Assert.True(outcome.Canceled);
        }

        Assert.Equal(1, prompt.FallbackQuestions);
    }
}
