using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Bimbo's production path: one picture read, then deterministic XML.</summary>
public class BimboVisionPrimaryTests
{
    private const string Fixture = @"Fixtures\PDF\BIMBO_84294890013239_sscs_verified.pdf";

    private static BimboVisionReading Reading() => new()
    {
        Status = BimboVisionStatus.Success,
        Rows =
        [
            new BimboVisionRow
            {
                ItemNumber = "008788", Description = "BIM PANQUE NUE FS FC",
                Barcode = "74323-09183", Units = "4", Price = "3.0100", Amount = "12.04",
                IsCredit = false,
            },
            new BimboVisionRow
            {
                ItemNumber = "008927", Description = "BIM ROLE CAN SS2P FC",
                Barcode = "74323-09165", Units = "2", Price = "1.8900", Amount = "3.78",
                IsCredit = false,
            },
            new BimboVisionRow
            {
                ItemNumber = "008783", Description = "BIM CONCHAS SS2P FC",
                Barcode = "74323-09145", Units = "1", Price = "1.9400", Amount = "1.94",
                IsCredit = true,
            },
        ],
        Deliveries = new("6", "15.82"),
        Returns = new("-1", "-1.94"),
        TicketUnits = "5",
        TicketTotal = "13.88",
        CashDue = "13.88",
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
            return new InvoiceDetails("OPERATOR-ONLY", new DateOnly(2026, 8, 13));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            Fields.Add(request);
            return null;
        }

        public bool ConfirmBimboOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return AllowFallback;
        }
    }

    private sealed class Vision(BimboVisionReading reading)
        : IInvoiceVisionReader, IBimboVisionReader
    {
        public int PrimaryReads { get; private set; }

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;

        public BimboVisionReading ReadBimbo(byte[] pdfBytes)
        {
            PrimaryReads++;
            return reading;
        }
    }

    [Fact]
    public void AiRowsTurnDirectlyIntoInvoiceLines()
    {
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(Reading(), warnings);

        Assert.Equal(5, ticket.Lines.Sum(line => line.Units));
        Assert.Equal(13.88m, ticket.Total);
        Assert.Equal("074323091830", ticket.Lines[0].Gtin);
        Assert.Equal(3.0100m, ticket.Lines[0].UnitCost);
        Assert.DoesNotContain(warnings, warning => !warning.Routine);
    }

    [Fact]
    public void ArithmeticDisagreementsAreWarningsAndThePrintedValuesStillShip()
    {
        var reading = Reading() with
        {
            Rows =
            [
                Reading().Rows[0] with { Units = "5", Price = "3.0100", Amount = "12.04" },
            ],
            Deliveries = new("4", "12.04"), Returns = null,
            TicketUnits = "4", TicketTotal = "12.04", CashDue = "12.04",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        var line = Assert.Single(ticket.Lines);
        Assert.Equal(5, line.Units);
        Assert.Equal(3.0100m, line.UnitCost);
        Assert.Equal(12.04m, ticket.Total);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.LineMathMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.QtyMismatch);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    [Fact]
    public void MissingPriceUsesQtyAndAmountWithoutOpeningALineQuestion()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Price = null }],
            Deliveries = new("4", "12.04"), Returns = null,
            TicketUnits = "4", TicketTotal = "12.04", CashDue = "12.04",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        Assert.Equal(3.0100m, Assert.Single(ticket.Lines).UnitCost);
        // Not PDF_UNREADABLE_LINE. That code means a row was dropped and goods are missing from the
        // file; this line shipped complete, and sharing one code made the two indistinguishable.
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.PriceDerived
            && warning.Message.Contains("no readable PRICE", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UnreadableLine);
    }

    [Fact]
    public void ARowThatDidNotNameItsBandIsNotReportedAsADroppedLine()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { IsCredit = null }],
            Deliveries = new("4", "12.04"), Returns = null,
            TicketUnits = "4", TicketTotal = "12.04", CashDue = "12.04",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        Assert.Equal(4, Assert.Single(ticket.Lines).Units);
        Assert.Contains(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.SectionUnlabelled);
        Assert.DoesNotContain(warnings, warning =>
            warning.ReasonCode == PdfReasonCodes.UnreadableLine);
    }

    /// <summary>
    /// The ticket prints its returns with a minus in front of every figure, and nothing tells the
    /// model whether to copy that or hand back a magnitude the way the product rows are told to.
    /// Both spellings mean the same money. Reading the sign as meaningful put a money warning on
    /// six of sixteen live tickets that reconciled to the cent.
    /// </summary>
    [Theory]
    [InlineData("-1", "-1.94")]
    [InlineData("1", "1.94")]
    public void AReturnsBandIsTheSameMoneyWhicheverWayItsSignIsWritten(
        string units, string amount)
    {
        var warnings = new List<ConversionWarning>();

        BimboVisionParser.Parse(Reading() with { Returns = new(units, amount) }, warnings);

        Assert.DoesNotContain(warnings, warning => !warning.Routine);
    }

    /// <summary>
    /// Each line is rounded to the cent on its own before it is added up, so a band of many rows
    /// can sit a few cents from its printed figure with nothing wrong anywhere. One flat cent for a
    /// whole band was the wrong allowance.
    /// </summary>
    [Fact]
    public void ABandOfManyRowsIsAllowedTheSameCentPerLineTheTicketTotalGets()
    {
        var rows = Enumerable.Range(0, 8).Select(index => new BimboVisionRow
        {
            ItemNumber = $"00100{index}", Description = $"BIM ROW {index}",
            Barcode = "74323-09183", Units = "1", Price = "1.0050", Amount = "1.01",
            IsCredit = false,
        }).ToList();
        // Eight lines of 1.0050 round to 1.01 each and book 8.08; the ticket prints the untruncated
        // 8.04. Four cents across eight rows, and nothing on the paper is wrong.
        var reading = Reading() with
        {
            Rows = rows, Deliveries = new("8", "8.04"), Returns = null,
            TicketUnits = "8", TicketTotal = "8.04", CashDue = "8.04",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        Assert.Equal(8, ticket.Lines.Count);
        Assert.Equal(8.04m, ticket.Total);
        Assert.DoesNotContain(warnings, warning => !warning.Routine);
    }

    /// <summary>
    /// A summary line the scan destroyed is not a reason to send a delivery of legible product rows
    /// back to the scanner. The ticket is invoiced for the sum of its own rows, and says so.
    /// </summary>
    [Fact]
    public void ATicketWhoseTotalsLineWasLostIsStillInvoicedForItsOwnRows()
    {
        var reading = Reading() with
        {
            Deliveries = null, Returns = null,
            TicketUnits = null, TicketTotal = null, CashDue = null,
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        Assert.Equal(3, ticket.Lines.Count);
        Assert.Equal(13.88m, ticket.Total);
        Assert.Contains(warnings, warning => warning.ReasonCode == PdfReasonCodes.NoTotal);
    }

    [Fact]
    public void UnpostableRowsAreDroppedNamedWithTheirMoneyAndTheRestConverts()
    {
        var reading = Reading() with
        {
            Rows =
            [
                Reading().Rows[0],
                Reading().Rows[1] with { Barcode = null, Amount = "3.78" },
                Reading().Rows[2] with { Units = "-8-", Amount = "15.52" },
            ],
            Deliveries = new("6", "15.82"), Returns = new("-1", "-15.52"),
            TicketUnits = "5", TicketTotal = "13.88",
        };
        var warnings = new List<ConversionWarning>();

        var ticket = BimboVisionParser.Parse(reading, warnings);

        Assert.Single(ticket.Lines);
        var skipped = Assert.Single(warnings, warning =>
            warning.Message.Contains("could not be sent", StringComparison.Ordinal));
        Assert.Contains("3.78", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("15.52", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPostableRowsIsTheOnlyBusinessRefusal()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Barcode = null }],
        };

        var ex = Assert.Throws<EdiConversionException>(() =>
            BimboVisionParser.Parse(reading, []));

        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
    }

    [Fact]
    public void VisionPathMakesOneReadAndOnlyAsksForInvoiceNumberAndDate()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var vision = new Vision(Reading());
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Equal(1, vision.PrimaryReads);
        Assert.Empty(prompt.Fields);
        var request = Assert.Single(prompt.Details);
        Assert.True(request.NeedsDate);
        Assert.DoesNotContain(typeof(InvoiceDetailsRequest).GetProperties(), property =>
            property.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Offer", StringComparison.OrdinalIgnoreCase));

        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Contains("OPERATOR-ONLY", xml.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-08-13", xml.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineReturnsStayGreenAndRemainInTheConversionDetails()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", new Prompt(), new Vision(Reading()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.False(outcome.HasWarnings);
        var returns = Assert.Single(outcome.Warnings,
            warning => warning.ReasonCode == PdfReasonCodes.CreditLines);
        Assert.True(returns.Routine);
    }

    [Fact]
    public void AnUnreadableAiResponseRequiresARescanAndNeverOffersOcr()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(BimboVisionReading.Unreadable()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Contains("Re-scan", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prompt.FallbackQuestions);
    }

    [Fact]
    public void OcrFallbackApprovalIsAskedOnlyOnceForAQueueBatch()
    {
        using var tmp = new TempDir();
        string output = tmp.Sub("out");
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(BimboVisionReading.Unavailable()));
        service.BeginBatch();

        for (int index = 0; index < 2; index++)
        {
            string input = Path.Combine(tmp.Path, $"bimbo-{index}.pdf");
            File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
            var outcome = service.ConvertFile(new QueuedFile
            {
                InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
            }, output);
            Assert.True(outcome.Success, outcome.UserMessage);
        }

        Assert.Equal(1, prompt.FallbackQuestions);
    }
}
