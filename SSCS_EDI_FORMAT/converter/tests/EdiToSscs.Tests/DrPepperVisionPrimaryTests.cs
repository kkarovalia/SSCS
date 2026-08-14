using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Dr Pepper's contract after 2026-08-13: one picture request, then XML.
///
/// The suite this replaced spent most of its length pinning the twenty-three ways the parser could
/// refuse an invoice and the three barcode reads it made them agree on. None of that survived
/// contact with the store's real tickets — every refusal that day was our own handling, and the
/// three-read design produced 29 questions on a thirty-row invoice whose money was exact.
///
/// So what is pinned now is the opposite: the AI's values reach the file unchanged, nothing is ever
/// asked, and a figure that does not reconcile arrives as a note on a converted invoice rather than
/// as a refusal. The one refusal left is a reading with nothing postable in it at all.
/// </summary>
public class DrPepperVisionPrimaryTests
{
    private static string SaleUpc => Upc.AppendCheckDigit("07800020028");
    private static string CreditUpc => Upc.AppendCheckDigit("07181700132");

    private static DrPepperVisionReading Reading(
        string? saleBarcode = null, string? creditBarcode = null,
        string? saleUnits = "12", string? saleAmount = "20.66",
        string? printedUnits = "12", string? amountDue = "10.67") => new()
    {
        Status = DrPepperVisionStatus.Success,
        Rows =
        [
            new DrPepperVisionRow
            {
                RowId = "sale-1", Page = "1", Section = "SALES", RowOrder = "1",
                Occurrence = "1", ItemNumber = "10000069", Description = "16z12 Dr Pepper",
                Barcode = saleBarcode ?? SaleUpc, Wholesale = "35.90", Cases = "1",
                Units = saleUnits, Net = "20.66", Tax = "0.00", Amount = saleAmount,
                IsCredit = false,
            },
            new DrPepperVisionRow
            {
                RowId = "credit-1", Page = "1", Section = "CREDITS", RowOrder = "1",
                Occurrence = "1", ItemNumber = "10000862", Description = "Credit product",
                Barcode = creditBarcode ?? CreditUpc, Cases = "0", Units = "6", Net = "0.00",
                Tax = "0.00", Amount = "9.99", IsCredit = true,
            },
        ],
        PackageSubtotals =
        [
            new DrPepperVisionPackageSubtotal
            {
                GroupId = "sales-package", RowIds = ["sale-1"], Cases = "1", Units = "12",
                Amount = "20.66",
            },
        ],
        TotalSaleCases = "1",
        TotalSaleUnits = printedUnits,
        TotalSaleAmount = "20.66",
        Summary = new DrPepperVisionSummary
        {
            SalesCases = "1", SalesUnits = printedUnits, SalesAmount = "20.66",
            CreditCases = "0", CreditUnits = "6", CreditAmount = "9.99",
        },
        AmountDue = amountDue,
    };

    private static DrPepperVisionTicket Convert(
        DrPepperVisionReading reading, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return DrPepperVisionParser.Parse(reading, warnings);
    }

    private static ConversionWarning Note(List<ConversionWarning> warnings, string reasonCode) =>
        Assert.Single(warnings, w => w.ReasonCode == reasonCode);

    private sealed class UnavailableVision : IInvoiceVisionReader, IDrPepperVisionReader
    {
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;
        public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes) =>
            DrPepperVisionReading.Unavailable();
    }

    /// <summary>AI reached the invoice and could not read it. Not the same thing as unavailable.</summary>
    private sealed class UnreadableVision : IInvoiceVisionReader, IDrPepperVisionReader
    {
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;
        public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes) =>
            DrPepperVisionReading.Unreadable();
    }

    private sealed class SuccessfulVision(DrPepperVisionReading? reading = null)
        : IInvoiceVisionReader, IDrPepperVisionReader
    {
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;
        public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes) => reading ?? Reading();
    }

    private sealed class FallbackPrompt(bool approve) : IInvoiceDetailsPrompt
    {
        public int FallbackQuestions { get; private set; }
        public List<InvoiceDetailsRequest> Details { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];
        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Details.Add(request);
            return new InvoiceDetails("OPERATOR", new DateOnly(2026, 8, 13));
        }
        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return null;
        }
        public bool ConfirmDrPepperOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return approve;
        }
    }

    // ---------------------------------------------------------------- values reach the file ----

    [Fact]
    public void EveryValueInTheFileIsTheOneTheAiRead()
    {
        var ticket = Convert(Reading(), out _);

        Assert.Equal(SaleUpc, ticket.Lines[0].Gtin);
        Assert.Equal("16z12 Dr Pepper", ticket.Lines[0].Description);
        Assert.Equal(12, ticket.Lines[0].Units);
        Assert.Equal(20.66m, ticket.Lines[0].ExtendedCost);
        Assert.Equal(10.67m, ticket.Total);
    }

    [Fact]
    public void RoutineCreditsStayGreenAndUseNegativeQuantityPositiveCost()
    {
        var ticket = Convert(Reading(), out var warnings);

        Assert.Equal(-6, ticket.Lines[1].Units);
        Assert.True(ticket.Lines[1].UnitCost > 0m);
        Assert.True(Note(warnings, PdfReasonCodes.CreditLines).Routine);
    }

    /// <summary>The invoice is paid for what it says it is worth, not for what we added up.</summary>
    [Fact]
    public void TheInvoiceIsSentForItsPrintedTotal()
    {
        var ticket = Convert(Reading(saleAmount: "21.66"), out var warnings);

        // 21.66 sold less 9.99 returned is 11.67 against the 10.67 the ticket prints. The printed
        // figure is what the store is billed, so that is what is sent - and the gap is said aloud.
        Assert.Equal(10.67m, ticket.Total);
        string message = Note(warnings, PdfReasonCodes.TotalMismatch).Message;
        Assert.Contains("11.67", message, StringComparison.Ordinal);
        Assert.Contains("10.67", message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderFieldsCannotExistInTheAiContract()
    {
        var names = typeof(DrPepperVisionReading).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Date", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------- the barcode ----

    /// <summary>
    /// The whole point of the rewrite. A code that fails its own check digit is a real misread —
    /// this one is the transposed prefix the store's own ticket produced on 2026-08-13, where
    /// `071817000378` came back as `078170000378`. It goes into the file as read, with a note
    /// naming the row, because the alternative was refusing a delivery over one barcode a person
    /// can retype in SSCS in five seconds.
    /// </summary>
    [Fact]
    public void ABarcodeThatFailsItsCheckDigitShipsFlaggedInsteadOfStoppingTheInvoice()
    {
        var ticket = Convert(Reading(saleBarcode: "078170000378"), out var warnings);

        Assert.Equal("078170000378", ticket.Lines[0].Gtin);
        Assert.Contains("16z12 Dr Pepper", Note(warnings, PdfReasonCodes.UpcCheckDigit).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The other real one: a zero duplicated inside a run of zeros, giving thirteen digits.</summary>
    [Fact]
    public void ABarcodeOfTheWrongLengthShipsFlaggedToo()
    {
        var ticket = Convert(Reading(saleBarcode: "8101696003369"), out var warnings);

        Assert.Equal("8101696003369", ticket.Lines[0].Gtin);
        Assert.Contains(PdfReasonCodes.UpcCheckDigit, warnings.Select(w => w.ReasonCode));
    }

    /// <summary>
    /// Eleven digits are never padded to twelve and no check digit is ever computed. A repaired
    /// code looks perfect and posts the delivery to the wrong product, which is the one mistake
    /// nothing downstream can see — so it ships as read and flagged, exactly like any other.
    /// </summary>
    [Fact]
    public void ElevenDigitsAreNeverPaddedIntoATwelfthDigit()
    {
        var ticket = Convert(Reading(saleBarcode: "07800020028"), out var warnings);

        Assert.Equal("07800020028", ticket.Lines[0].Gtin);
        Assert.NotEqual(SaleUpc, ticket.Lines[0].Gtin);
        Assert.Contains(PdfReasonCodes.UpcCheckDigit, warnings.Select(w => w.ReasonCode));
    }

    /// <summary>
    /// Dr Pepper prints `UPC/SKU` in one cell and the model returns sometimes the code and
    /// sometimes the whole cell. Both are honest readings of the paper. Rejecting the second is
    /// what took seven of eight tickets down on 2026-08-13.
    /// </summary>
    [Fact]
    public void TheWholePrintedUpcSlashSkuCellReadsAsTheBarcode()
    {
        var ticket = Convert(Reading(saleBarcode: $"{SaleUpc}/10000836"), out var warnings);

        Assert.Equal(SaleUpc, ticket.Lines[0].Gtin);
        Assert.DoesNotContain(PdfReasonCodes.UpcCheckDigit, warnings.Select(w => w.ReasonCode));
    }

    [Fact]
    public void TheItemNumberAfterTheSlashIsNeverGluedOntoTheBarcode()
    {
        var ticket = Convert(Reading(saleBarcode: $"{SaleUpc}/10000836"), out _);

        Assert.DoesNotContain("10000836", ticket.Lines[0].Gtin, StringComparison.Ordinal);
        Assert.Equal(12, ticket.Lines[0].Gtin.Length);
    }

    [Fact]
    public void AGoodInvoiceCarriesNoBarcodeNoteAtAll()
    {
        Convert(Reading(), out var warnings);

        Assert.DoesNotContain(PdfReasonCodes.UpcCheckDigit, warnings.Select(w => w.ReasonCode));
    }

    // --------------------------------------------------------------- notes, never refusals ----

    /// <summary>
    /// The row printing its own money twice in different glyphs. It used to refuse the invoice; it
    /// names the row now and the printed amount is used, because the amount is what the vendor
    /// billed and no arithmetic here is entitled to overrule it.
    /// </summary>
    [Fact]
    public void ARowThatDoesNotMultiplyOutIsNotedAndTheInvoiceStillShips()
    {
        var ticket = Convert(Reading(saleAmount: "21.66", amountDue: "11.67"), out var warnings);

        Assert.Equal(21.66m, ticket.Lines[0].ExtendedCost);
        Assert.Contains("16z12 Dr Pepper",
            Note(warnings, PdfReasonCodes.LineMathMismatch).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one mistake the money cannot see: a wrong count balances to the penny and leaves the
    /// shelf short for good. Still only a note — but a loud one, and the file is written.
    /// </summary>
    [Fact]
    public void AUnitCountThatMissesThePrintedTotalIsNoted()
    {
        var ticket = Convert(Reading(saleUnits: "11"), out var warnings);

        Assert.Equal(11, ticket.Lines[0].Units);
        string message = Note(warnings, PdfReasonCodes.QtyMismatch).Message;
        Assert.Contains("12", message, StringComparison.Ordinal);
        Assert.Contains("11", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row with no count cannot be posted at all — there is no cost per unit without one, and
    /// inventing one books stock that never arrived. So that single row is dropped, named with its
    /// money so it can be added by hand, and the rest of the delivery still goes through.
    /// </summary>
    [Fact]
    public void ARowWithNoUsableCountIsNamedAndTheRestOfTheInvoiceStillShips()
    {
        var ticket = Convert(Reading(saleUnits: null), out var warnings);

        Assert.Equal(CreditUpc, Assert.Single(ticket.Lines).Gtin);
        string message = Note(warnings, PdfReasonCodes.UnreadableLine).Message;
        Assert.Contains("16z12 Dr Pepper", message, StringComparison.Ordinal);
        Assert.Contains("20.66", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWithNoBarcodeAtAllIsDroppedRatherThanSentWithAnEmptyCode()
    {
        // An empty UPC cell is what the model returns when it cannot make the digits out at all.
        var ticket = Convert(Reading(saleBarcode: ""), out var warnings);

        Assert.Equal(CreditUpc, Assert.Single(ticket.Lines).Gtin);
        Assert.Contains(PdfReasonCodes.UnreadableLine, warnings.Select(w => w.ReasonCode));
    }

    /// <summary>
    /// The only refusal left. Everything else is a note on a written file; a reading with no
    /// postable row in it has nothing to write.
    /// </summary>
    [Fact]
    public void AReadingWithNothingPostableIsTheOneRemainingRefusal()
    {
        var empty = Reading() with { Rows = [] };

        var error = Assert.Throws<EdiConversionException>(() => Convert(empty, out _));

        Assert.Equal(PdfReasonCodes.NoLineItems, error.ReasonCode);
    }

    // ------------------------------------------------------------------ nothing ever asks ----

    /// <summary>
    /// No Dr Pepper path may open a prompt about a line any more. This prompt throws if one is
    /// raised, so the test fails loudly rather than quietly answering.
    /// </summary>
    [Fact]
    public void NoLineQuestionIsEverRaisedWhateverTheScanDid()
    {
        var damaged = Reading(saleBarcode: "078170000378", saleUnits: "11", saleAmount: "21.66");
        using var temp = new TempDir();
        var prompt = new FallbackPrompt(approve: true);
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt,
            new SuccessfulVision(damaged));
        string input = Path.Combine(temp.Path, "damaged.pdf");
        File.Copy(@"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf", input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out"));

        Assert.True(result.Success, result.UserMessage);
        Assert.Empty(prompt.LineFields);
        Assert.True(result.HasWarnings);
    }

    [Fact]
    public void SuccessfulAiPathOwnsTheInvoiceAndNeverOffersLegacyFallback()
    {
        using var temp = new TempDir();
        var prompt = new FallbackPrompt(approve: true);
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt,
            new SuccessfulVision());
        string input = Path.Combine(temp.Path, "ai.pdf");
        File.Copy(@"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf", input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out"));

        Assert.True(result.Success, result.UserMessage);
        Assert.Equal(0, prompt.FallbackQuestions);
        var details = Assert.Single(prompt.Details);
        Assert.False(details.NeedsTotal);
        Assert.Equal(10.67m, details.Total);
        var xml = System.Xml.Linq.XDocument.Load(Assert.Single(result.FinalOutputPaths));
        Assert.Equal("OPERATOR", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Contains(SaleUpc, xml.Descendants("InvoiceUnitId").Select(e => e.Value));
        Assert.Contains("-6", xml.Descendants("InvoiceUnitQty").Select(e => e.Value));
    }

    // ------------------------------------------------------- when the AI is not there at all ----

    [Fact]
    public void UnavailableAiRequiresOneExplicitApprovalPerBatch()
    {
        using var temp = new TempDir();
        var prompt = new FallbackPrompt(approve: true);
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt,
            new UnavailableVision());
        service.BeginBatch();

        string source = @"Fixtures\PDF\DRPEPPER_4642413020_semicolon_decimal.pdf";
        var outcomes = new List<ConversionOutcome>();
        foreach (string name in new[] { "first.pdf", "second.pdf" })
        {
            string copy = Path.Combine(temp.Path, name);
            File.Copy(source, copy);
            outcomes.Add(service.ConvertFile(new QueuedFile
            {
                InputPath = copy, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
            }, temp.Sub("out")));
        }

        service.BeginBatch();
        string third = Path.Combine(temp.Path, "third.pdf");
        File.Copy(source, third);
        outcomes.Add(service.ConvertFile(new QueuedFile
        {
            InputPath = third, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out")));

        Assert.All(outcomes, o => Assert.True(o.Success, o.UserMessage));
        Assert.Equal(2, prompt.FallbackQuestions);
        Assert.All(prompt.Details, request => Assert.False(request.NeedsTotal));
    }

    /// <summary>
    /// "AI reached the invoice and could not read it" is a rescan, never a quiet OCR read.
    ///
    /// It is the same word to an operator and the opposite decision underneath: unavailable means
    /// nobody looked at the page, unreadable means something looked and could not make it out, and
    /// only the first of those is a reason to fall back to a reader Dr Pepper retired.
    /// </summary>
    [Fact]
    public void AiThatReachedTheInvoiceAndFailedIsNeverOfferedTheLegacyReader()
    {
        using var temp = new TempDir();
        var prompt = new FallbackPrompt(approve: true);
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt,
            new UnreadableVision());
        string input = Path.Combine(temp.Path, "unreadable.pdf");
        File.Copy(@"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf", input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out"));

        Assert.False(result.Success);
        Assert.Equal(0, prompt.FallbackQuestions);
        Assert.Empty(prompt.Details);
        Assert.Empty(result.FinalOutputPaths);
        Assert.Contains("Re-scan", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecliningLegacyFallbackCancelsWithoutCreatingXml()
    {
        using var temp = new TempDir();
        var prompt = new FallbackPrompt(approve: false);
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt,
            new UnavailableVision());
        string input = Path.Combine(temp.Path, "declined.pdf");
        File.Copy(@"Fixtures\PDF\DRPEPPER_4642413020_semicolon_decimal.pdf", input);
        string output = temp.Sub("out");

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, output);

        Assert.True(result.Canceled);
        Assert.Equal(1, prompt.FallbackQuestions);
        Assert.Empty(Directory.GetFiles(output, "*.xml"));
    }
}
