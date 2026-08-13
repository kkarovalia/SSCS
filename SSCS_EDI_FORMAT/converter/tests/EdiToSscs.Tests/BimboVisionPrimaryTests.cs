using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Bimbo's production path: the picture supplies the invoice, OCR supplies coordinates only, and a
/// second picture read guards every check-digit-less UPC without a product table in the executable.
/// </summary>
public class BimboVisionPrimaryTests
{
    private const string Fixture = @"Fixtures\PDF\BIMBO_84294890013239_sscs_verified.pdf";

    private static BimboVisionReading Reading(string firstBarcode = "74323-09183") => new()
    {
        Status = BimboVisionStatus.Success,
        Rows =
        [
            new BimboVisionRow
            {
                ItemNumber = "008788", Description = "BIM PANQUE NUE FS FC",
                Barcode = firstBarcode, Units = "4", Price = "3.0100", Amount = "12.04",
                IsCredit = false, Page = 1,
            },
            new BimboVisionRow
            {
                ItemNumber = "008927", Description = "BIM ROLE CAN SS2P FC",
                Barcode = "74323-09165", Units = "2", Price = "1.8900", Amount = "3.78",
                IsCredit = false, Page = 1,
            },
            new BimboVisionRow
            {
                ItemNumber = "008783", Description = "BIM CONCHAS SS2P FC",
                Barcode = "74323-09145", Units = "1", Price = "1.9400", Amount = "1.94",
                IsCredit = true, Page = 1,
            },
        ],
        Deliveries = new("6", "15.82"),
        Returns = new("-1", "-1.94"),
        TicketUnits = "5",
        TicketTotal = "13.88",
        CashDue = "13.88",
    };

    private static BimboBarcodeVerification Verified(
        string first = "7432309183", BimboVisionStatus status = BimboVisionStatus.Success) => new()
    {
        Status = status,
        Rows = status == BimboVisionStatus.Success
            ?
            [
                new("p1-r1", first),
                new("p1-r2", "7432309165"),
                new("p1-r3", "7432309145"),
            ]
            : [],
    };

    private sealed class Prompt : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Details { get; } = [];
        public List<LineFieldRequest> Fields { get; } = [];
        public Dictionary<(int Position, LineField Field), string> Answers { get; } = [];
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
            return Answers.TryGetValue((request.Position, request.Field), out string? answer)
                ? answer : null;
        }

        public bool ConfirmBimboOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return AllowFallback;
        }
    }

    private sealed class Vision(
        BimboVisionReading reading, BimboBarcodeVerification verification,
        BimboBarcodeVerification? cropVerification = null)
        : IInvoiceVisionReader, IBimboVisionReader
    {
        public int PrimaryReads { get; private set; }
        public int BarcodeReads { get; private set; }
        public int BarcodeCropReads { get; private set; }
        public IReadOnlyList<BimboBarcodeTarget> Targets { get; private set; } = [];

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;

        public BimboVisionReading ReadBimbo(byte[] pdfBytes)
        {
            PrimaryReads++;
            return reading;
        }

        public BimboBarcodeVerification VerifyBimboBarcodes(
            byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets)
        {
            BarcodeReads++;
            Targets = targets;
            return verification;
        }

        public BimboBarcodeVerification VerifyBimboBarcodeCrops(
            byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets)
        {
            BarcodeCropReads++;
            Targets = targets;
            return cropVerification ?? verification;
        }
    }

    [Fact]
    public void AiRowsAreTheInvoiceAndOcrValuesAreOnlyCoordinates()
    {
        var questions = new List<BimboUnknownField>();
        var text = new[]
        {
            // Every value is deliberately wrong except the item-number locator. If any OCR value is
            // fed back into the row this test changes stock, cost, total and GTIN at once.
            new PdfTextLine(41, 1, "99999-99999 008788 WRONG 88 9.9999 879.91", 10, 12, []),
        };

        var ticket = BimboVisionParser.Parse(
            Reading(), Verified(), text, [], q => { questions.Add(q); return ""; });

        Assert.Empty(questions);
        Assert.Equal(5, ticket.Lines.Sum(l => l.Units));
        Assert.Equal(13.88m, ticket.Lines.Sum(l => l.ExtendedCost));
        Assert.Equal("074323091830", ticket.Lines[0].Gtin);
        Assert.Equal(3.0100m, ticket.Lines[0].UnitCost);
    }

    [Fact]
    public void TwoUpcReadsMustAgreeOrTheExactRowIsAsked()
    {
        var answers = new Dictionary<(int, LineField), string>
        {
            [(1, LineField.Barcode)] = "7432309183",
        };
        var asked = new List<BimboUnknownField>();

        var ticket = BimboVisionParser.Parse(
            Reading(), Verified(first: "7432309122"), [], [], q =>
            {
                asked.Add(q);
                return answers[(q.Position, q.Field)];
            });

        var question = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, question.Field);
        Assert.Equal("008788", question.ItemNumber);
        Assert.Equal("074323091830", ticket.Lines[0].Gtin);
    }

    [Fact]
    public void TwoMatchingFullPageReadsCannotOutvoteTheIsolatedUpcCell()
    {
        var asked = new List<BimboUnknownField>();
        var twoAgree = Verified(first: "7432309167");
        var isolated = Verified(first: "7432309157");

        var ticket = BimboVisionParser.Parse(
            Reading(firstBarcode: "74323-09167"), twoAgree, isolated, [], [], q =>
            {
                asked.Add(q);
                return "7432309157";
            });

        var question = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, question.Field);
        Assert.Equal("074323091571", ticket.Lines[0].Gtin);
    }

    [Fact]
    public void AllThreeMatchingUpcReadsAreAcceptedWithoutAQuestion()
    {
        var asked = new List<BimboUnknownField>();

        var ticket = BimboVisionParser.Parse(
            Reading(), Verified(), Verified(), [], [], q =>
            {
                asked.Add(q);
                return q.Upc10;
            });

        Assert.Empty(asked);
        Assert.Equal("074323091830", ticket.Lines[0].Gtin);
    }

    [Fact]
    public void MissingThirdReadAsksAboutEveryOtherwiseMatchingBarcode()
    {
        var asked = new List<BimboUnknownField>();

        BimboVisionParser.Parse(
            Reading(), Verified(), BimboBarcodeVerification.Unavailable(), [], [], q =>
            {
                asked.Add(q);
                return q.Upc10;
            });

        Assert.Equal(3, asked.Count);
        Assert.All(asked, q => Assert.Equal(LineField.Barcode, q.Field));
    }

    [Fact]
    public void MatchingIndependentReadsAcceptANewManufacturerPrefixWithoutACodeChange()
    {
        var asked = new List<BimboUnknownField>();

        var ticket = BimboVisionParser.Parse(
            Reading(firstBarcode: "57528-04800"), Verified(first: "5752804800"), [], [], q =>
            {
                asked.Add(q);
                return q.Upc10;
            });

        Assert.Empty(asked);
        Assert.Equal("5752804800", ticket.Lines[0].Gtin[1..11]);
    }

    /// <summary>
    /// Bimbo prints its ten UPC digits in a column of their own. The shared Dr Pepper/Red Bull
    /// marker looks for a twelve-digit code joined to a SKU with a slash, so using that marker here
    /// used to wash the entire Bimbo row even though the dialog asked for only the barcode.
    /// </summary>
    [Fact]
    public void ADisputedBarcodePromptHighlightsOnlyTheBimboUpcColumn()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));

        var prompt = new Prompt();
        prompt.Answers[(1, LineField.Barcode)] = "7432309183";
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(Reading(), Verified(first: "7432309122")));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        var asked = Assert.Single(prompt.Fields);
        var snip = Assert.IsType<PdfRowSnip>(asked.Snip);

        Assert.True(snip.FocusWidth < snip.Width * 0.20,
            $"the UPC marker covers {snip.FocusWidth} of {snip.Width}px, so it still marks the row");
        double centre = (snip.FocusLeft + snip.FocusWidth / 2d) / snip.Width;
        Assert.InRange(centre, 0.04, 0.15);
    }

    [Fact]
    public void ARowCanBeLocatedByDescriptionWhenOcrDamagedItsItemNumberAndAmountIsRepeated()
    {
        var first = Reading().Rows[0];
        var reading = Reading() with
        {
            Rows = [first], Deliveries = new("4", "12.04"), Returns = null,
            TicketUnits = "4", TicketTotal = "12.04", CashDue = "12.04",
        };
        var verification = new BimboBarcodeVerification
        {
            Status = BimboVisionStatus.Success,
            Rows = [new("p1-r1", "7432309122")],
        };
        var text = new[]
        {
            // Both machine-readable numeric locators are deliberately unusable: OCR replaced two
            // item-number digits with letters, and the amount is also printed in the totals block.
            new PdfTextLine(22, 1,
                "74323-O9183 OO8788 BIM PANQUE NUE FS FC 4 3.0100 12.04", 20, 18, []),
            new PdfTextLine(30, 1, "TICKET TOTALS 4 12.04", 10, 8, []),
        };
        var asked = new List<BimboUnknownField>();

        BimboVisionParser.Parse(reading, verification, text, [], q =>
        {
            asked.Add(q);
            return "7432309183";
        });

        Assert.Equal(22, Assert.Single(asked).LineNo);
    }

    [Fact]
    public void AUnavailableSecondReadIsNeverTreatedAsAgreement()
    {
        var asked = new List<BimboUnknownField>();
        var ticket = BimboVisionParser.Parse(
            Reading(), Verified(status: BimboVisionStatus.Unavailable), [], [], q =>
            {
                asked.Add(q);
                return q.Upc10;
            });

        Assert.Equal(3, asked.Count);
        Assert.All(asked, q => Assert.Equal(LineField.Barcode, q.Field));
        Assert.Equal(3, ticket.Lines.Count);
    }

    [Fact]
    public void MalformedQuantityIsAskedInsteadOfBeingCleanedUp()
    {
        var reading = Reading() with
        {
            Rows = [Reading().Rows[0] with { Units = "-8-", Amount = "24.08" }],
            Deliveries = new("8", "24.08"), Returns = null,
            TicketUnits = "8", TicketTotal = "24.08", CashDue = "24.08",
        };
        var verified = new BimboBarcodeVerification
        {
            Status = BimboVisionStatus.Success,
            Rows = [new("p1-r1", "7432309183")],
        };
        var asked = new List<BimboUnknownField>();

        var ticket = BimboVisionParser.Parse(reading, verified, [], [], q =>
        {
            asked.Add(q);
            return "8";
        });

        Assert.Equal(LineField.Units, Assert.Single(asked).Field);
        Assert.Equal(8, Assert.Single(ticket.Lines).Units);
    }

    [Fact]
    public void ARepeatedItemInDeliveriesAndReturnsKeepsTheDeliveryPromptOnTheDeliveryRow()
    {
        var reading = new BimboVisionReading
        {
            Status = BimboVisionStatus.Success,
            Rows =
            [
                new BimboVisionRow
                {
                    ItemNumber = "008785", Description = "BIM ROLE CAN FS6P FC",
                    Barcode = "74323-09164", Units = "3", Price = "3.0100", Amount = "9.03",
                    IsCredit = false, Page = 1,
                },
                new BimboVisionRow
                {
                    ItemNumber = "008785", Description = "BIM ROLE CAN FS6P FC",
                    Barcode = "74323-09164", Units = "2", Price = "3.0100", Amount = "6.02",
                    IsCredit = true, Page = 1,
                },
            ],
            Deliveries = new("3", "9.03"), Returns = new("-2", "-6.02"),
            TicketUnits = "1", TicketTotal = "3.01", CashDue = "3.01",
        };
        var second = new BimboBarcodeVerification
        {
            Status = BimboVisionStatus.Success,
            Rows = [new("p1-r1", "7432309164"), new("p1-r2", "7432309164")],
        };
        var third = second with
        {
            Rows = [new("p1-r1", "7432309184"), new("p1-r2", "7432309164")],
        };
        var text = new[]
        {
            new PdfTextLine(1, 1,
                "74323-09164 008785 BIM ROLE CAN FS6P FC 3 3.79 11.37 3.0100 9.03", 80, 90, []),
            new PdfTextLine(2, 1, "DELIVERY TOTAL 3 11.37 9.03", 60, 70, []),
            new PdfTextLine(3, 1,
                "74323-09164 008785 BIM ROLE CAN FS6P FC -2 3.79 -7.58 3.0100 -6.02", 40, 50, []),
            new PdfTextLine(4, 1, "RETURNS TOTAL -2 -7.58 -6.02", 20, 30, []),
            new PdfTextLine(5, 1, "TICKET TOTALS 1 3.79 3.01", 0, 10, []),
        };
        var asked = new List<BimboUnknownField>();

        BimboVisionParser.Parse(reading, second, third, text, [], q =>
        {
            asked.Add(q);
            return "7432309164";
        });

        var question = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, question.Field);
        Assert.Equal(1, question.LineNo);
    }

    [Fact]
    public void VisionPathStillMakesInvoiceNumberAndDateOperatorOnly()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        string output = tmp.Sub("out");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var prompt = new Prompt();
        var vision = new Vision(Reading(), Verified());
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt, vision);

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, output);

        Assert.True(outcome.Success, outcome.UserMessage);
        var request = Assert.Single(prompt.Details);
        Assert.True(request.NeedsDate);
        Assert.DoesNotContain(typeof(InvoiceDetailsRequest).GetProperties(), p =>
            p.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Offer", StringComparison.OrdinalIgnoreCase));

        var xml = XDocument.Parse(System.Text.Encoding.ASCII.GetString(outcome.Documents[0].Bytes));
        Assert.Contains("OPERATOR-ONLY", xml.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-08-13", xml.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, vision.PrimaryReads);
        Assert.Equal(1, vision.BarcodeReads);
        Assert.Equal(1, vision.BarcodeCropReads);
        Assert.All(vision.Targets, t => Assert.DoesNotContain("Barcode", t.GetType().GetProperties()
            .Select(p => p.Name), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoutineReturnsStayGreenAndRemainInTheConversionDetails()
    {
        using var tmp = new TempDir();
        string input = Path.Combine(tmp.Path, "bimbo.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(Fixture));
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", new Prompt(),
            new Vision(Reading(), Verified()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.False(outcome.HasWarnings);
        var returns = Assert.Single(outcome.Warnings, w => w.ReasonCode == PdfReasonCodes.CreditLines);
        Assert.True(returns.Routine);
        Assert.Contains("return line", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
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
            new Vision(BimboVisionReading.Unreadable(), Verified()));

        var outcome = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo,
        }, tmp.Sub("out"));

        Assert.False(outcome.Success);
        Assert.Contains("Re-scan", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prompt.FallbackQuestions);
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp.Path, "out")));
    }

    [Fact]
    public void OcrFallbackApprovalIsAskedOnlyOnceForAQueueBatch()
    {
        using var tmp = new TempDir();
        string output = tmp.Sub("out");
        var prompt = new Prompt { AllowFallback = true };
        var service = new ConversionService(
            new ConversionLogger(tmp.Sub("logs")), "test", prompt,
            new Vision(BimboVisionReading.Unavailable(), Verified()));
        service.BeginBatch();

        for (int i = 0; i < 2; i++)
        {
            string input = Path.Combine(tmp.Path, $"bimbo-{i}.pdf");
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
