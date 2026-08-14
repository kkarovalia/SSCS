using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Blue Bell's production contract: one complete-ticket picture read, then XML.</summary>
public class BlueBellVisionPrimaryTests
{
    private const string Fixture = @"Fixtures\PDF\BLUEBELL_033100786112.pdf";

    private static BlueBellVisionReading Reading() => new()
    {
        Status = BlueBellVisionStatus.Success,
        Rows =
        [
            new BlueBellVisionRow
            {
                Page = "1", ProductNumber = "0925", Description = "PT GR SUPREME",
                Upc = "007189905101", Quantity = "10.00", Price = "2.98",
                Extended = "29.80", PackSize = "1", IsCredit = false,
            },
            new BlueBellVisionRow
            {
                Page = "1", ProductNumber = "0151", Description = "DZ VANILLA SAND",
                Upc = "007189921122", Quantity = "2.00", Price = "6.48",
                Extended = "12.96", PackSize = "12", IsCredit = false,
            },
            new BlueBellVisionRow
            {
                Page = "2", ProductNumber = "0151", Description = "DZ VANILLA SAND",
                Upc = "007189921122", Quantity = "-11/12", Price = "6.48",
                Extended = "5.94", PackSize = "12", IsCredit = true,
            },
        ],
        PageSummaries =
        [
            new BlueBellVisionPageSummary
            {
                Page = "1", IsCredit = false, TotalQuantity = "12.00",
                TotalAmount = "42.76", BalanceDue = "36.82",
            },
            new BlueBellVisionPageSummary
            {
                Page = "2", IsCredit = true, TotalQuantity = "-11/12",
                TotalAmount = "-5.94", BarsReturned = "-11.00",
            },
        ],
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
            return new InvoiceDetails("OPERATOR-BLUE-BELL", new DateOnly(2026, 8, 14));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            Fields.Add(request);
            return null;
        }

        public bool ConfirmBlueBellOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return AllowFallback;
        }
    }

    private sealed class Vision(BlueBellVisionReading reading)
        : IInvoiceVisionReader, IBlueBellVisionReader
    {
        public int PrimaryReads { get; private set; }
        public int GenericReads { get; private set; }

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
        {
            GenericReads++;
            return VisionReading.Empty;
        }

        public BlueBellVisionReading ReadBlueBell(byte[] pdfBytes)
        {
            PrimaryReads++;
            return reading;
        }
    }

    [Fact]
    public void RowsTurnStraightIntoLinesAndRestoreTheRealBarcode()
    {
        var warnings = new List<ConversionWarning>();

        var ticket = BlueBellVisionParser.Parse(Reading(), warnings);

        Assert.Equal(3, ticket.Lines.Count);
        Assert.Equal(36.82m, ticket.Total);
        Assert.Equal("071899051015", ticket.Lines[0].Gtin);
        Assert.Equal(2.98m, ticket.Lines[0].UnitCost);
        Assert.Equal(4.99m, ticket.Lines[0].RetailPrice);
        Assert.DoesNotContain(warnings, warning => !warning.Routine);
    }

    [Fact]
    public void ADzCartonExpandsToTwelveSellableItems()
    {
        var ticket = BlueBellVisionParser.Parse(Reading(), []);

        var carton = ticket.Lines[1];
        Assert.Equal(24, carton.Units);
        Assert.Equal(0.54m, carton.UnitCost);
        Assert.Equal(12.96m, carton.ExtendedCost);
    }

    [Fact]
    public void AFractionalReturnBooksThePrintedNumberOfBars()
    {
        var ticket = BlueBellVisionParser.Parse(Reading(), []);

        var returned = ticket.Lines[2];
        Assert.Equal(-11, returned.Units);
        Assert.Equal(0.54m, returned.UnitCost);
        Assert.Equal(-5.94m, returned.ExtendedCost);
    }

    [Fact]
    public void ArithmeticDisagreementsWarnAndPrintedQuantityAndPriceStillShip()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows = [Reading().Rows[1] with { Extended = "99.99" }],
            PageSummaries =
            [
                new BlueBellVisionPageSummary
                {
                    Page = "1", IsCredit = false, TotalQuantity = "3",
                    TotalAmount = "99.99",
                },
            ],
        };

        var ticket = BlueBellVisionParser.Parse(reading, warnings);

        var line = Assert.Single(ticket.Lines);
        Assert.Equal(24, line.Units);
        Assert.Equal(0.54m, line.UnitCost);
        Assert.Equal(12.96m, line.ExtendedCost);
        Assert.Equal(99.99m, ticket.Total);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.LineMathMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.TotalMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.QtyMismatch);
    }

    [Fact]
    public void CataloguePackDisagreementHasItsOwnWarningAndModelPackShips()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows = [Reading().Rows[1] with { PackSize = "1" }],
            PageSummaries =
            [
                new BlueBellVisionPageSummary
                {
                    Page = "1", IsCredit = false, TotalQuantity = "2",
                    TotalAmount = "12.96",
                },
            ],
        };

        var ticket = BlueBellVisionParser.Parse(reading, warnings);

        var line = Assert.Single(ticket.Lines);
        Assert.Equal(2, line.Units);
        Assert.Equal(6.48m, line.UnitCost);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.PackSizeMismatch);
    }

    [Fact]
    public void AnUnpostableRowIsDroppedAndNamedWithItsPrintedMoney()
    {
        var warnings = new List<ConversionWarning>();
        var reading = Reading() with
        {
            Rows =
            [
                Reading().Rows[0],
                Reading().Rows[1] with { Upc = null, Extended = "12.96" },
            ],
            PageSummaries =
            [
                new BlueBellVisionPageSummary
                {
                    Page = "1", IsCredit = false, TotalQuantity = "10",
                    TotalAmount = "29.80",
                },
            ],
        };

        var ticket = BlueBellVisionParser.Parse(reading, warnings);

        Assert.Single(ticket.Lines);
        var warning = Assert.Single(warnings, value =>
            value.ReasonCode == PdfReasonCodes.UnreadableLine);
        Assert.Contains("DZ VANILLA SAND", warning.Message, StringComparison.Ordinal);
        Assert.Contains("12.96", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPostableRowIsTheOnlyBusinessRefusal()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Quantity = "-8-" }],
        };

        var ex = Assert.Throws<EdiConversionException>(() =>
            BlueBellVisionParser.Parse(reading, []));

        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
    }

    [Fact]
    public void VisionPathReadsOnceAsksOnlyForHeaderAndWritesOnePayable()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "blue-bell.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var vision = new Vision(Reading());
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(1, vision.PrimaryReads);
        Assert.Equal(0, vision.GenericReads);
        Assert.Empty(prompt.Fields);
        Assert.Single(prompt.Details);
        Assert.Single(outcome.Documents);
        Assert.Single(outcome.FinalOutputPaths);

        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Equal("OPERATOR-BLUE-BELL", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Contains("-11", xml.Descendants("InvoiceUnitQty").Select(value => value.Value));
        Assert.Equal(36.82m, decimal.Parse(xml.Descendants("TotalInvoiceDueAmt").Single().Value,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TicketWhosePrintedTotalIsUnreadableStillConverts()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "blue-bell.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var reading = Reading() with { PageSummaries = [] };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, new Vision(reading));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.Fields);
        Assert.Contains(outcome.Warnings, warning => warning.ReasonCode == PdfReasonCodes.NoTotal);
    }

    [Fact]
    public void UnreadableAiRequiresARescanAndNeverOffersOcr()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "blue-bell.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(BlueBellVisionReading.Unreadable()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell,
        }, tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Contains("Re-scan", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prompt.FallbackQuestions);
    }

    [Fact]
    public void DeclinedFallbackIsAskedOnlyOncePerBatchAndReportsCanceled()
    {
        using var tmp = new TempDir();
        var prompt = new Prompt();
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(BlueBellVisionReading.Unavailable()));
        service.BeginBatch();

        for (int index = 0; index < 2; index++)
        {
            string input = Path.Combine(tmp.Path, $"blue-bell-{index}.pdf");
            File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
            var outcome = service.ConvertFile(new QueuedFile
            {
                InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell,
            }, tmp.Sub("out"));
            Assert.True(outcome.Canceled);
        }

        Assert.Equal(1, prompt.FallbackQuestions);
    }
}
