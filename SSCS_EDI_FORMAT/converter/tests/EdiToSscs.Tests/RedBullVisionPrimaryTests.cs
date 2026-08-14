using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Red Bull's production contract: one complete-ticket picture read, then XML.</summary>
public class RedBullVisionPrimaryTests
{
    private const string Fixture = @"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf";

    private static RedBullVisionReading Reading() => new()
    {
        Status = RedBullVisionStatus.Success,
        Rows =
        [
            new RedBullVisionRow
            {
                ItemNumber = "RB1718", Description = "RED BULL 8.4OZ LS",
                Barcode = "611269991000", Cases = "1", Units = "24", Price = "40.75",
                Deposit = "0.00", Discount = "0.00", Sugar = "0.00", Amount = "40.75",
                IsCredit = false,
            },
            new RedBullVisionRow
            {
                ItemNumber = "RB33049", Description = "RED BULL 16OZ LS",
                Barcode = "611269546019", Cases = "2", Units = "24", Price = "35.11",
                Deposit = "0.00", Discount = "0.00", Sugar = "0.00", Amount = "70.22",
                IsCredit = false,
            },
            new RedBullVisionRow
            {
                ItemNumber = "RB221027", Description = "RED BULL 12OZ STRAWBE",
                Barcode = "611269321210", Cases = "0.25", Units = "(6)", Price = "2.23",
                Deposit = "0.00", Discount = "0.00", Sugar = "0.00", Amount = "(13.36)",
                IsCredit = true,
            },
        ],
        PrintedTotals = ["97.61"],
    };

    private sealed class Prompt : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Details { get; } = [];
        public List<LineFieldRequest> Fields { get; } = [];
        public int FallbackQuestions { get; private set; }
        public bool AllowFallback { get; set; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Details.Add(request);
            return new InvoiceDetails("OPERATOR-RED-BULL", new DateOnly(2026, 8, 14));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            Fields.Add(request);
            return null;
        }

        public bool ConfirmRedBullOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return AllowFallback;
        }
    }

    private sealed class Vision(RedBullVisionReading reading)
        : IInvoiceVisionReader, IRedBullVisionReader
    {
        public int PrimaryReads { get; private set; }
        public int GenericReads { get; private set; }

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
        {
            GenericReads++;
            return VisionReading.Empty;
        }

        public RedBullVisionReading ReadRedBull(byte[] pdfBytes)
        {
            PrimaryReads++;
            return reading;
        }
    }

    [Fact]
    public void AiRowsTurnStraightIntoInvoiceLines()
    {
        var warnings = new List<ConversionWarning>();

        var ticket = RedBullVisionParser.Parse(Reading(), warnings);

        Assert.Equal(3, ticket.Lines.Count);
        Assert.Equal(42, ticket.Lines.Sum(line => line.Units));
        Assert.Equal(97.61m, ticket.Total);
        Assert.Equal("611269991000", ticket.Lines[0].Gtin);
        Assert.Equal(1.6979m, ticket.Lines[0].UnitCost);
        Assert.Equal(2.2267m, ticket.Lines[2].UnitCost);
        Assert.DoesNotContain(warnings, warning => !warning.Routine);
    }

    [Fact]
    public void EveryArithmeticDisagreementIsAWarningAndPrintedValuesStillShip()
    {
        var reading = Reading() with
        {
            Rows =
            [
                Reading().Rows[0] with { Cases = "2", Units = "24", Amount = "40.75" },
            ],
            PrintedTotals = ["40.74"],
            PrintedUnits = "23",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = RedBullVisionParser.Parse(reading, warnings);

        var line = Assert.Single(ticket.Lines);
        Assert.Equal(24, line.Units);
        Assert.Equal(1.6979m, line.UnitCost); // TOTAL / UNITS, never PRICE / pack
        Assert.Equal(40.75m, ticket.Total);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.LineMathMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.TotalMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.QtyMismatch);
    }

    [Fact]
    public void ABarcodeFailingItsCheckDigitShipsAsReadAndFlagged()
    {
        string readCode = "611269991001";
        var warnings = new List<ConversionWarning>();

        var ticket = RedBullVisionParser.Parse(Reading() with
        {
            Rows = [Reading().Rows[0] with { Barcode = readCode }],
            PrintedTotals = ["40.75"],
        }, warnings);

        Assert.Equal(readCode, Assert.Single(ticket.Lines).Gtin);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UpcCheckDigit
            && warning.Message.Contains(readCode, StringComparison.Ordinal));
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
                Reading().Rows[1] with { Barcode = null, Amount = "$70.22" },
            ],
            PrintedTotals = ["40.75"],
        };

        var ticket = RedBullVisionParser.Parse(reading, warnings);

        Assert.Single(ticket.Lines);
        var skipped = Assert.Single(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UnreadableLine);
        Assert.Contains("70.22", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("RED BULL 16OZ LS", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCreditDirectionUsesThePrintedSignAndNamesTheRow()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows = [Reading().Rows[2] with { IsCredit = null }],
            PrintedTotals = ["13.36"],
        };

        var ticket = RedBullVisionParser.Parse(reading, warnings);

        Assert.Equal(-6, Assert.Single(ticket.Lines).Units);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.SectionUnlabelled
            && warning.Message.Contains("RED BULL 12OZ STRAWBE", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPostableRowIsTheOnlyBusinessRefusal()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Units = "-8-" }],
        };

        var ex = Assert.Throws<EdiConversionException>(() =>
            RedBullVisionParser.Parse(reading, []));

        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
    }

    [Fact]
    public void VisionPathMakesOneReadAndAsksOnlyForInvoiceNumberAndDate()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "red-bull.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var vision = new Vision(Reading());
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(1, vision.PrimaryReads);
        Assert.Equal(0, vision.GenericReads);
        Assert.Empty(prompt.Fields);
        var request = Assert.Single(prompt.Details);
        Assert.True(request.NeedsDate);
        Assert.DoesNotContain(typeof(InvoiceDetailsRequest).GetProperties(), property =>
            property.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Offer", StringComparison.OrdinalIgnoreCase));

        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Contains("OPERATOR-RED-BULL", xml.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-08-14", xml.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TicketMissingItsPrintedTotalStillConvertsWithoutALineQuestion()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "red-bull.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var reading = Reading() with { PrintedTotals = [], PageSummaries = [] };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, new Vision(reading));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.Fields);
    }

    [Fact]
    public void AnUnreadableAiResponseRequiresARescanAndNeverOffersOcr()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "red-bull.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(RedBullVisionReading.Unreadable()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull,
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
            new Vision(RedBullVisionReading.Unavailable()));
        service.BeginBatch();

        for (int index = 0; index < 2; index++)
        {
            string input = Path.Combine(tmp.Path, $"red-bull-{index}.pdf");
            File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
            var outcome = service.ConvertFile(new QueuedFile
            {
                InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull,
            }, tmp.Sub("out"));
            Assert.False(outcome.Success);
            Assert.True(outcome.Canceled);
        }

        Assert.Equal(1, prompt.FallbackQuestions);
    }
}
