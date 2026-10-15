using System.Globalization;
using System.Text;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// PDF to Modisoft EDI (2026-10-05). The PDF is read exactly as for SSCS; what is pinned here is
/// the file written at the end, against the one GHRA file known to have gone into Modisoft:
/// invoice 687105, imported into a Modisoft purchase in 2021 and read back off that purchase on
/// 2026-10-05.
/// </summary>
public class ModisoftEdiTests
{
    private const string ImportedIntoModisoft = @"Modisoft\EDI_GHRA_687105.txt";

    // ------------------------------------- the layout is GHRA's, with a 13-character barcode ----

    /// <summary>
    /// Today's layout is GHRA's with one change: the barcode field is thirteen characters, not
    /// eleven. Every record of the 2021 file, parsed and written again, keeps every other field byte
    /// for byte, two places to the right. The header, the charge record, the CRLFs and the missing
    /// final newline do not move at all.
    ///
    /// GHRA's own layout came into Modisoft at Best Stop 16 on 2026-10-05 with every figure read
    /// one character to the right under Texas Wholesale, which reads twelve. Since 2026-10-09 the
    /// store imports with Xpress Wholesale, which reads thirteen
    /// (see <see cref="ModisoftEdiBuilder"/>).
    /// </summary>
    [Fact]
    public void TodaysLayoutIsGhrasWithAThirteenCharacterBarcodeField()
    {
        byte[] file = TestUtil.Fixture(ImportedIntoModisoft);
        string[] original = Text(file).Split("\r\n");

        string[] written = Text(ModisoftEdiBuilder.Write("687105", new DateOnly(2021, 7, 11), Records(file)))
            .Split("\r\n");

        Assert.Equal(original.Length, written.Length);
        Assert.Equal(original[0], written[0]);
        Assert.Equal(original[^1], written[^1]);
        for (int i = 1; i < original.Length - 1; i++)
        {
            Assert.Equal(original[i][1..12].TrimEnd().PadRight(13), written[i][1..14]);
            // The description keeps 24 characters; the 25th is always blank (see the next test).
            Assert.Equal(original[i][12..36], written[i][14..38]);
            Assert.Equal(' ', written[i][38]);
            Assert.Equal(original[i][37..], written[i][39..]);
        }
    }

    /// <summary>
    /// A description never reaches the item code. Texas Wholesale read its item code from the byte
    /// after the 24th, and on Silver Eagle 424753 (2026-10-06) every 25-character description put
    /// its last letter in Modisoft's Item Code column — "1", "/", "L", "C" — so the 25th byte stays
    /// blank.
    /// </summary>
    [Fact]
    public void ALongDescriptionLeavesTheItemCodeBlank()
    {
        string record = Text(Build(Invoice(new InvoiceLine
        {
            Gtin = "033544950418", Description = "COR PREMIER 4/6 LNNR 24/12", Units = 24,
            UnitCost = 1.5208m, RetailPrice = 2.27m, Cases = 1,
        }), out _)).Split("\r\n")[1];

        Assert.Equal("COR PREMIER 4/6 LNNR 24/ ", record[14..39]);
        Assert.Equal(new string(' ', 7), record[38..45]);
    }

    /// <summary>
    /// Records Modisoft has read back correctly under Xpress Wholesale, 2026-10-09. 1L DR PEPPER, 4
    /// cases of 15 at $28.12, is from the whole Dr Pepper 423423 invoice, which came in as Qty 4,
    /// Units 15, Case Cost $28.12 and matched the store's item. That UPC-A goes in as its twelve
    /// digits and a blank; written with a leading zero instead, Yumi's 49 UPC-A lines all came in
    /// as new items. LLB GRAPE is from Yumi 29008246. Its EAN-13 went in whole and came in as
    /// 6911308117885, 1 case of 24 at $96.72. Under Texas Wholesale it had landed on Strawberry.
    /// </summary>
    [Fact]
    public void TheRecordsModisoftReadBackCorrectlyAreWrittenExactly()
    {
        string upc = Text(Build(Invoice(new InvoiceLine
        {
            Gtin = "078000082456", Description = "1L15 DrPep", Units = 60, UnitCost = 1.8747m,
            RetailPrice = 2.27m, Cases = 4,
        }), out _)).Split("\r\n")[1];
        string ean = Text(Build(Invoice(new InvoiceLine
        {
            Gtin = "6911308117885", Description = "LLB-GRAPE SINGLE 3D", Units = 24, UnitCost = 4.03m,
            RetailPrice = 0m, Cases = 1,
        }), out _)).Split("\r\n")[1];

        Assert.Equal(
            "B078000082456 1L15 DrPep                     002812  0000150000400227015      ",
            upc);
        Assert.Equal(
            "B6911308117885LLB-GRAPE SINGLE 3D            009672  0000240000100000024      ",
            ean);
    }

    /// <summary>
    /// What Modisoft did with that file, as its purchase showed it: every record with cases came
    /// in at cases x case cost, and they add up to the file's own header total to the cent. The 78
    /// records with 0 cases were left out. (The purchase also carries three credits someone added
    /// by hand, -$237.18, which is how it ends at $19,063.96.)
    /// </summary>
    [Fact]
    public void ModisoftBookedCasesTimesCaseCostAndSkippedTheEmptyLines()
    {
        var records = Records(TestUtil.Fixture(ImportedIntoModisoft));

        var booked = records.Where(r => r.Cases != 0).ToList();
        Assert.Equal(465, records.Count);
        Assert.Equal(387, booked.Count);
        Assert.Equal(19301.14m, booked.Sum(r => r.Amount));
        Assert.Equal(19063.96m, booked.Sum(r => r.Amount) - 15.58m - 21.60m - 200.00m);
    }

    [Fact]
    public void EveryRecordIsSeventyEightBytesAndTheHeaderThirtyThree()
    {
        var lines = Text(Build(Invoice(DrPepperRow(), CreditRow()), out _)).Split("\r\n");

        Assert.Equal(ModisoftEdiBuilder.HeaderWidth, lines[0].Length);
        Assert.All(lines[1..^1], line => Assert.Equal(ModisoftEdiBuilder.RecordWidth, line.Length));
        Assert.Equal(ModisoftEdiBuilder.ChargeRecord, lines[^1]);
    }

    // ---------------------------------------------------------- Modisoft counts in cases ----

    /// <summary>
    /// Modisoft's Qty is the file's case count and it divides the case cost by its own units per
    /// case, so a Dr Pepper row printed as 4 cases at $32.50 has to arrive as exactly that.
    /// </summary>
    [Fact]
    public void ARowBilledByTheCaseGoesInAsThoseCasesAtTheCasePrice()
    {
        var line = Assert.Single(Lines(Invoice(DrPepperRow()), out var warnings));

        Assert.Equal(4, line.Cases);
        Assert.Equal(24, line.Pack);
        Assert.Equal(32.50m, line.CaseCost);
        Assert.Equal(130.00m, line.Amount);
        Assert.Empty(warnings);
    }

    /// <summary>A Dr Pepper credit prints CASES 0, so it has no whole case to give.</summary>
    [Fact]
    public void ACreditWithNoWholeCaseGoesInOneUnitAtATimeAsANegativeCount()
    {
        var line = Assert.Single(Lines(Invoice(CreditRow()), out _));

        Assert.Equal(-1, line.Cases);
        Assert.Equal(1, line.Pack);
        Assert.Equal(8.41m, line.CaseCost);

        // The minus goes straight before the digits, inside the zeros: GHRA's own files write one
        // case back as 000-1 (34 times in EDI/ and EDI13/) and 22 as 00-22.
        string record = Text(Build(Invoice(CreditRow()), out _)).Split("\r\n")[1];
        Assert.Equal("000-1", record[59..64]);
    }

    /// <summary>A vendor that bills the single item (Bimbo, Chubiwinos, a Blue Bell pint).</summary>
    [Fact]
    public void ARowBilledByTheItemGoesInOneItemPerCase()
    {
        var line = Assert.Single(Lines(Invoice(new InvoiceLine
        {
            Gtin = "074323091830", Description = "BIM PANQUE", Units = 4, UnitCost = 3.01m,
            RetailPrice = 5.47m,
        }), out _));

        Assert.Equal((4, 1, 3.01m), (line.Cases, line.Pack, line.CaseCost));
    }

    /// <summary>
    /// Cases that do not hold a whole number of items each cannot be written as cases, so the row
    /// falls back to single items rather than inventing a pack size.
    /// </summary>
    [Fact]
    public void CasesThatDoNotDivideTheItemsEvenlyGoInOneItemAtATime()
    {
        var line = Assert.Single(Lines(Invoice(new InvoiceLine
        {
            Gtin = "078000082401", Description = "20z24 DrPep", Units = 30, UnitCost = 1.3542m,
            RetailPrice = 2.33m, Cases = 4,
        }), out _));

        Assert.Equal((30, 1), (line.Cases, line.Pack));
    }

    /// <summary>
    /// Whole cents cannot always carry a line's money item by item. When they cannot, the gap is
    /// named, because somebody has to fix it in Modisoft.
    /// </summary>
    [Fact]
    public void AFractionOfACentPerItemIsSaidOutLoud()
    {
        Lines(Invoice(new InvoiceLine
        {
            Gtin = "611269321210", Description = "RED BULL 12OZ STRAWBE", Units = -6, UnitCost = 2.2267m,
            RetailPrice = 3.71m,
        }), out var warnings);

        var note = Assert.Single(warnings, w => w.ReasonCode == ModisoftReasonCodes.CentsRounded);
        Assert.Contains("-13.38 instead of -13.36", note.Message, StringComparison.Ordinal);
        Assert.Contains("RED BULL 12OZ STRAWBE", note.Message, StringComparison.Ordinal);
        Assert.False(note.Routine);
    }

    // ---------------------------------------------------------------------- barcodes ----

    /// <summary>
    /// Every barcode goes in whole: a UPC-A as its twelve digits, check digit and all, and an
    /// EAN-13 as all thirteen.
    /// </summary>
    [Fact]
    public void AUpcAndAnEan13BothGoInWhole()
    {
        var lines = Lines(Invoice(
            DrPepperRow(),
            new InvoiceLine
            {
                Gtin = "7500478044207", Description = "COLMILLOS", Units = 20, UnitCost = 2.79m,
                RetailPrice = 5.59m,
            }), out _);

        Assert.Equal("078000082401", lines[0].Code);
        Assert.Equal("7500478044207", lines[1].Code);
    }

    /// <summary>
    /// The trap eleven digits set: two EAN-13s that differ only in their last two digits became one
    /// code (6 of the 16 Chubiwinos samples carry such a pair). Texas Wholesale matched on eleven
    /// digits even when given twelve, and booked Yumi's LLB Grape to Strawberry (2026-10-09). All
    /// thirteen keep them apart.
    /// </summary>
    [Fact]
    public void EanThirteensThatElevenDigitsMergedStayApart()
    {
        var lines = Lines(Invoice(
            new InvoiceLine
            {
                Gtin = "7501011130869", Description = "TOSTITOS SALSA VERDE", Units = 24, UnitCost = 2.79m,
                RetailPrice = 5.59m,
            },
            new InvoiceLine
            {
                Gtin = "7501011130876", Description = "TOSTITOS FLAMING HOT", Units = 24, UnitCost = 2.79m,
                RetailPrice = 5.59m,
            }), out var warnings);

        Assert.Equal(["7501011130869", "7501011130876"], lines.Select(l => l.Code));
        Assert.DoesNotContain(warnings, w => w.ReasonCode == ModisoftReasonCodes.SameCode);
    }

    /// <summary>
    /// A code longer than thirteen digits can still lose the part that tells two products apart,
    /// and when it does the file says so.
    /// </summary>
    [Fact]
    public void TwoProductsCutDownToTheSameCodeAreNamed()
    {
        Lines(Invoice(
            new InvoiceLine
            {
                Gtin = "10012000001574", Description = "AQUA CASE OF 15", Units = 15, UnitCost = 1.10m,
                RetailPrice = 2.49m,
            },
            new InvoiceLine
            {
                Gtin = "10012000001570", Description = "AQUA CASE OF 24", Units = 24, UnitCost = 1.10m,
                RetailPrice = 2.49m,
            }), out var warnings);

        var note = Assert.Single(warnings, w => w.ReasonCode == ModisoftReasonCodes.SameCode);
        Assert.Contains("10012000001574 (AQUA CASE OF 15)", note.Message, StringComparison.Ordinal);
        Assert.Contains("10012000001570 (AQUA CASE OF 24)", note.Message, StringComparison.Ordinal);
        Assert.Contains("both go in as 1001200000157", note.Message, StringComparison.Ordinal);
    }

    /// <summary>The same product twice on one invoice is not a clash.</summary>
    [Fact]
    public void TheSameBarcodeTwiceIsNotAClash()
    {
        Lines(Invoice(DrPepperRow(), DrPepperRow()), out var warnings);

        Assert.DoesNotContain(warnings, w => w.ReasonCode == ModisoftReasonCodes.SameCode);
    }

    // ------------------------------------------------------------------------ header ----

    /// <summary>
    /// Modisoft never reads the header: the purchase carries its own invoice number and date, typed
    /// in Modisoft (it showed 7/12/2021 and IN#687105 against a file saying 07/11/21 and
    /// 0000687105). It still has to be a valid GHRA header: ten digits, the last ten of a longer
    /// number, the date as MMDDYY and a signed total with the minus inside the zeros.
    /// </summary>
    [Theory]
    [InlineData("84294890013239", "4890013239")]   // Bimbo, fourteen digits
    [InlineData("033100786112", "3100786112")]     // Blue Bell, twelve
    [InlineData("4646106112", "4646106112")]       // Dr Pepper, ten
    [InlineData("6V9389", "0000069389")]           // Chubiwinos carries a letter
    public void TheHeaderHoldsTheLastTenDigitsOfTheTypedInvoiceNumber(string typed, string field)
    {
        string header = Text(ModisoftEdiBuilder.Write(typed, new DateOnly(2026, 10, 2), [])).Split("\r\n")[0];

        Assert.Equal("GHRA   " + field + "100226" + "0000000000", header);
    }

    [Fact]
    public void ACreditInvoiceTotalCarriesItsMinusInsideTheZeros()
    {
        string header = Text(Build(Invoice(CreditRow()), out _)).Split("\r\n")[0];

        Assert.EndsWith("00000-841", header, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- whole pipeline ----

    /// <summary>
    /// The Modisoft tab reads the PDF exactly like the SSCS tab and writes the EDI file instead of
    /// the XML: named for its vendor and invoice, sale rows in cases, the credit unit by unit.
    /// </summary>
    [Fact]
    public void TheModisoftTabWritesAGhraFileFromTheSameAiReading()
    {
        using var temp = new TempDir();
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", new Prompt(), new DrPepperAi());
        string input = Path.Combine(temp.Path, "dp.pdf");
        File.Copy(TestUtil.FixturePath(@"PDF\DRPEPPER_4643519283_with_credits.pdf"), input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
            Target = OutputTarget.Modisoft,
        }, temp.Sub("out"));

        Assert.True(result.Success, result.UserMessage);
        string path = Assert.Single(result.FinalOutputPaths);
        Assert.Equal("MODISOFT_DRPEPPER_4646106112.txt", Path.GetFileName(path));
        var lines = Text(File.ReadAllBytes(path)).Split("\r\n");
        Assert.Equal("GHRA   4646106112100226", lines[0][..23]);
        Assert.Equal(4, lines.Length);
        Assert.Equal("842595121766 ", lines[1][1..14]);  // the whole UPC-A and a blank
        Assert.Equal("00002", lines[1][59..64]);   // 2 cases...
        Assert.Equal("000012", lines[1][53..59]);  // ...of 12
        Assert.Equal("002066", lines[1][45..51]);  // ...at $20.66
        Assert.Equal("000-1", lines[2][59..64]);   // the credit, one unit back
    }

    /// <summary>The SSCS tab is untouched: the same reading still makes XML.</summary>
    [Fact]
    public void TheSscsTabStillWritesXml()
    {
        using var temp = new TempDir();
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", new Prompt(), new DrPepperAi());
        string input = Path.Combine(temp.Path, "dp.pdf");
        File.Copy(TestUtil.FixturePath(@"PDF\DRPEPPER_4643519283_with_credits.pdf"), input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out"));

        Assert.True(result.Success, result.UserMessage);
        Assert.EndsWith(".xml", Assert.Single(result.FinalOutputPaths), StringComparison.Ordinal);
    }

    // ------------------------------------------------- the readers pass their cases on ----

    [Fact]
    public void DrPepperPassesOnItsCasesButNotACreditsZero()
    {
        var ticket = DrPepperVisionParser.Parse(DrPepperAi.Reading, []);

        Assert.Equal(2, ticket.Lines[0].Cases);
        Assert.Null(ticket.Lines[1].Cases);
    }

    [Fact]
    public void RedBullPassesOnWholeCasesButNotAFractionOfOne()
    {
        var ticket = RedBullVisionParser.Parse(new RedBullVisionReading
        {
            Status = RedBullVisionStatus.Success,
            Rows =
            [
                new RedBullVisionRow
                {
                    Description = "RED BULL 16OZ LS", Barcode = "611269546019", Cases = "2",
                    Units = "24", Price = "35.11", Deposit = "0.00", Discount = "0.00",
                    Sugar = "0.00", Amount = "70.22", IsCredit = false,
                },
                new RedBullVisionRow
                {
                    Description = "RED BULL 12OZ STRAWBE", Barcode = "611269321210", Cases = "0.25",
                    Units = "(6)", Price = "2.23", Amount = "(13.36)", IsCredit = true,
                },
            ],
        }, []);

        Assert.Equal(2, ticket.Lines[0].Cases);
        Assert.Null(ticket.Lines[1].Cases);
    }

    [Fact]
    public void BlueBellPassesOnWholeDozenCartonsOnly()
    {
        var ticket = BlueBellVisionParser.Parse(new BlueBellVisionReading
        {
            Status = BlueBellVisionStatus.Success,
            Rows =
            [
                new BlueBellVisionRow
                {
                    Page = "1", Description = "PT GR SUPREME", Upc = "007189905101",
                    Quantity = "10.00", Price = "2.98", Extended = "29.80", PackSize = "1",
                    IsCredit = false,
                },
                new BlueBellVisionRow
                {
                    Page = "1", Description = "DZ VANILLA SAND", Upc = "007189921122",
                    Quantity = "2.00", Price = "6.48", Extended = "12.96", PackSize = "12",
                    IsCredit = false,
                },
                new BlueBellVisionRow
                {
                    Page = "2", Description = "DZ VANILLA SAND", Upc = "007189921122",
                    Quantity = "-11/12", Price = "6.48", Extended = "5.94", PackSize = "12",
                    IsCredit = true,
                },
            ],
        }, []);

        Assert.Equal(new int?[] { null, 2, null }, ticket.Lines.Select(l => l.Cases));

        // The cartons go to Modisoft as 2 at $6.48, and the returned bars one by one at $0.54.
        var lines = Lines(ticket.ToInvoice("746064", new DateOnly(2026, 10, 1)), out var warnings);
        Assert.Equal((2, 12, 6.48m), (lines[1].Cases, lines[1].Pack, lines[1].CaseCost));
        Assert.Equal((-11, 1, 0.54m), (lines[2].Cases, lines[2].Pack, lines[2].CaseCost));
        Assert.DoesNotContain(warnings, w => w.ReasonCode == ModisoftReasonCodes.CentsRounded);
    }

    // ---------------------------------------------------------------------- settings ----

    /// <summary>
    /// A store that only uses Modisoft has no SSCS address, and that has to be a complete setup
    /// for the Modisoft tab while the SSCS tab still has nowhere to send.
    /// </summary>
    [Fact]
    public void AModisoftOnlyStoreCanSendModisoftFilesAndNothingElse()
    {
        var settings = new EmailSettings
        {
            ModisoftToAddress = "store16@example.com",
            FromAddress = "converter@example.com",
            ProtectedPassword = "x",
        };

        Assert.True(settings.IsCompleteFor(OutputTarget.Modisoft));
        Assert.False(settings.IsCompleteFor(OutputTarget.Sscs));
        Assert.False(settings.IsComplete);
        Assert.Equal("store16@example.com",
            new SmtpEmailSender(() => settings, target: OutputTarget.Modisoft).ToAddress);
        Assert.Null(new SmtpEmailSender(() => settings).ToAddress);
    }

    [Fact]
    public void TheModisoftAddressIsSavedAndLoaded()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(temp.Path);
        store.SaveEmail(new EmailSettings { ToAddress = "sscs@example.com", ModisoftToAddress = "m@example.com" });

        var loaded = store.Load().Email;

        Assert.Equal("m@example.com", loaded.ModisoftToAddress);
        Assert.Equal("m@example.com", loaded.Clone().ModisoftToAddress);
    }

    // ----------------------------------------------------------------------- helpers ----

    private static InvoiceLine DrPepperRow() => new()
    {
        Gtin = "078000082401", Description = "20z24 DrPep", Units = 96, UnitCost = 1.3542m,
        RetailPrice = 2.33m, Cases = 4,
    };

    private static InvoiceLine CreditRow() => new()
    {
        Gtin = "078000082166", Description = "12z12p DrPep DEP", Units = -1, UnitCost = 8.41m,
        RetailPrice = 14.02m,
    };

    private static Invoice Invoice(params InvoiceLine[] lines) => new()
    {
        SupplierName = "DR PEPPER",
        InvoiceNumber = "4646106112",
        Date = new DateOnly(2026, 10, 2),
        Total = lines.Sum(l => l.ExtendedCost),
        Lines = lines,
    };

    private static IReadOnlyList<GhraLine> Lines(Invoice invoice, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return ModisoftEdiBuilder.Lines(invoice, warnings);
    }

    private static byte[] Build(Invoice invoice, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return ModisoftEdiBuilder.Build(invoice, warnings);
    }

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    /// <summary>A GHRA file's product records, read by field position and nothing else.</summary>
    private static List<GhraLine> Records(byte[] file) =>
        Text(file).Split("\r\n")
            .Where(line => line.StartsWith('B'))
            .Select(line => new GhraLine(
                Code: line[1..12].TrimEnd(),
                Description: line[12..37].TrimEnd(),
                ItemNumber: line[37..43].TrimEnd(),
                CaseCost: int.Parse(line[43..49], CultureInfo.InvariantCulture) / 100m,
                Pack: int.Parse(line[51..57], CultureInfo.InvariantCulture),
                Cases: SignedInsideTheZeros(line[57..62]),
                Retail: int.Parse(line[62..67], CultureInfo.InvariantCulture) / 100m))
            .ToList();

    private static int SignedInsideTheZeros(string field)
    {
        string digits = field.TrimStart('0');
        return digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture);
    }

    private sealed class Prompt : IInvoiceDetailsPrompt
    {
        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new("4646106112", new DateOnly(2026, 10, 2));
    }

    private sealed class DrPepperAi : IInvoiceVisionReader, IDrPepperVisionReader
    {
        public static DrPepperVisionReading Reading { get; } = new()
        {
            Status = DrPepperVisionStatus.Success,
            Rows =
            [
                new DrPepperVisionRow
                {
                    RowId = "sale-1", Page = "1", Section = "SALES", RowOrder = "1", Occurrence = "1",
                    Description = "16z12C4PrfCsmcRbw", Barcode = "842595121766", Wholesale = "35.90",
                    Cases = "2", Units = "24", Net = "20.66", Tax = "0.00", Amount = "41.32",
                    IsCredit = false,
                },
                new DrPepperVisionRow
                {
                    RowId = "credit-1", Page = "1", Section = "CREDITS", RowOrder = "1", Occurrence = "1",
                    Description = "12z12p DrPep DEP", Barcode = "078000082166", Cases = "0", Units = "1",
                    Net = "8.41", Tax = "0.00", Amount = "(8.41)", IsCredit = true,
                },
            ],
            TotalSaleUnits = "24",
            AmountDue = "32.91",
        };

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;
        public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes) => Reading;
    }
}
