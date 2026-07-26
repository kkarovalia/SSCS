using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs for the NAXML output. The retired fixed-width output was proved by byte-for-byte
/// comparison against reference files; there is no such reference for XML, so these tests prove
/// the two things that actually matter instead:
///
///   1. every barcode still resolves to the SAME SSCS item the old output addressed
///      (<see cref="EveryBarcodeLandsOnTheSameSscsItem"/>) — the guarantee that lets a vendor be
///      switched from EDI to XML without creating duplicate item records; and
///   2. the money adds up, line by line, the way live SSCS was observed to compute it.
/// </summary>
public class XmlConversionTests
{
    private static readonly string[] InputDirs = ["EDI", "EDI13", "HiddenEDI", "HiddenEDI13"];

    public static TheoryData<string> AllEdiFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string dir in InputDirs)
        {
            string full = TestUtil.FixturePath(dir);
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.EnumerateFiles(full, "*.txt").OrderBy(x => x))
                data.Add(Path.Combine(dir, Path.GetFileName(f)));
        }
        return data;
    }

    // ------------------------------------------------------------------ shape

    [Theory]
    [MemberData(nameof(AllEdiFixtures))]
    public void EveryFixtureProducesWellFormedXml(string fixture)
    {
        var result = EdiConverter.Convert(TestUtil.Fixture(fixture));
        var doc = XDocument.Parse(TestUtil.Text(result.OutputBytes));

        Assert.Equal("NAXML-BusDoc", doc.Root!.Name.LocalName);
        Assert.Equal("1.5", doc.Root.Attribute("version")!.Value);
        Assert.Equal(result.ProductRecordCount, doc.Descendants("LineItem").Count());
        Assert.NotEmpty(doc.Descendants("InvoiceNumber").Single().Value);

        // LineItem count attributes must run 1..n in order.
        int expected = 1;
        foreach (var li in doc.Descendants("LineItem"))
            Assert.Equal(expected++, int.Parse(li.Attribute("count")!.Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(AllEdiFixtures))]
    public void OutputIsPlainAsciiWithCrlfEndings(string fixture)
    {
        byte[] bytes = EdiConverter.Convert(TestUtil.Fixture(fixture)).OutputBytes;

        Assert.All(bytes, b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2} in the output"));
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                Assert.True(i > 0 && bytes[i - 1] == (byte)'\r', $"line feed at offset {i} is not preceded by a carriage return");
    }

    // ------------------------------------------------------------------ money

    [Theory]
    [MemberData(nameof(AllEdiFixtures))]
    public void LinesAddUpToTheInvoiceTotal(string fixture)
    {
        var result = EdiConverter.Convert(TestUtil.Fixture(fixture));
        var invoice = result.Invoice;

        // SSCS rounds units x unit cost to the cent per line, then sums (verified 16/16 against
        // live SSCS). A case cost that does not divide evenly into its pack can leave a cent
        // behind on a line, so allow one cent per line and no more.
        decimal tolerance = 0.01m * invoice.Lines.Count;
        Assert.True(Math.Abs(invoice.LineTotal - invoice.Total) <= tolerance,
            $"{fixture}: lines total {invoice.LineTotal:0.00} against an invoice total of {invoice.Total:0.00}");
    }

    [Theory]
    [MemberData(nameof(AllEdiFixtures))]
    public void TheXmlCarriesTheSameNumbersAsTheParsedInvoice(string fixture)
    {
        var result = EdiConverter.Convert(TestUtil.Fixture(fixture));
        var doc = XDocument.Parse(TestUtil.Text(result.OutputBytes));

        Assert.Equal(result.Invoice.Total, Money(doc.Descendants("TotalInvoiceDueAmt").Single()));
        Assert.Equal(result.Invoice.Total, Money(doc.Descendants("TotalLineItemNetAmt").Single()));

        var items = doc.Descendants("LineItem").ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var expected = result.Invoice.Lines[i];
            Assert.Equal(expected.Gtin, items[i].Descendants("InvoiceUnitId").Single().Value);
            Assert.Equal(expected.Units, int.Parse(items[i].Descendants("InvoiceUnitQty").Single().Value, CultureInfo.InvariantCulture));
            Assert.Equal(expected.UnitCost, Money(items[i].Descendants("InvoiceUnitCost").Single()));
            Assert.Equal(expected.RetailPrice, Money(items[i].Descendants("RetailPrice").Single()));
        }
    }

    [Fact]
    public void UnitCostKeepsFourDecimalsAndSscsRoundingReproducesTheInvoice()
    {
        // The Dr Pepper case that drove the 4-decimal decision: 96 x 1.3542 = 130.0032, and live
        // SSCS showed 130.00 — it rounds per line, so the error never accumulates.
        var line = new InvoiceLine { Gtin = "078000082401", Description = "20z24 DrPep", Units = 96, UnitCost = 1.3542m, RetailPrice = 2.33m };
        Assert.Equal(130.00m, line.ExtendedCost);

        var venom = new InvoiceLine { Gtin = "078000023763", Description = "16z24 VnmEn", Units = 24, UnitCost = 0.7708m, RetailPrice = 1.50m };
        Assert.Equal(18.50m, venom.ExtendedCost);
    }

    // ------------------------------------------------------------------ barcodes

    /// <summary>
    /// The migration guarantee. For every fixture that has a matching retired-output file, each
    /// barcode we now send must reduce — under SSCS's own "drop the last character" rule — to
    /// exactly the code the old converter wrote. The single allowed difference is a 13-digit
    /// EAN-13, which now keeps one more character than the 11-character field could hold; that is
    /// the collision bug this rewrite exists to fix, so it is asserted as a prefix relationship
    /// rather than ignored.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllEdiFixtures))]
    public void EveryBarcodeLandsOnTheSameSscsItem(string fixture)
    {
        string[]? oldCodes = RetiredOutputCodesFor(fixture);
        if (oldCodes is null) return; // no paired reference file

        var invoice = EdiConverter.Convert(TestUtil.Fixture(fixture)).Invoice;
        Assert.Equal(oldCodes.Length, invoice.Lines.Count);

        int widened = 0;
        for (int i = 0; i < oldCodes.Length; i++)
        {
            string oldCode = oldCodes[i];
            string newId = Upc.SscsIdFromGtin(invoice.Lines[i].Gtin);

            if (newId == oldCode) continue;

            // Documented exception: the old converter blanked a barcode whose UPC-E number system
            // it could not expand. We now pass that code through instead of losing it.
            if (oldCode.Length == 0)
            {
                Assert.NotEmpty(newId);
                continue;
            }

            Assert.True(newId.Length == oldCode.Length + 1 && newId.StartsWith(oldCode, StringComparison.Ordinal),
                $"{fixture} line {i + 1}: old SSCS id '{oldCode}' but the XML would store '{newId}'");
            widened++;
        }

        // A widened id must have come from a 13-character source field, never from a plain UPC-A.
        if (widened > 0)
            Assert.Contains("13", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirteenDigitEansSurviveWholeInsteadOfColliding()
    {
        // Four GEEK NEXT flavours that the 11-character field collapsed onto one id.
        string[] eans = ["6917673165742", "6917673165704", "6917673165766", "6917673165711"];
        var records = eans.Select(e => TestUtil.BRuby(e, Fields(caseCostCents: 1199, pack: 1, cases: 5, eaListCents: 2399))).ToArray();
        byte[] file = TestUtil.LfFile([TestUtil.RubyHeader, .. records]);

        var invoice = EdiConverter.Convert(file).Invoice;

        Assert.Equal(eans, invoice.Lines.Select(l => l.Gtin));
        var ids = invoice.Lines.Select(l => Upc.SscsIdFromGtin(l.Gtin)).ToList();
        Assert.Equal(4, ids.Distinct().Count());          // no collisions
        Assert.All(ids, id => Assert.Equal(12, id.Length)); // 12 characters kept, not 11
    }

    [Fact]
    public void ElevenDigitEdiCodesGetTheirCheckDigitBack()
    {
        // 01200000157 is the Aquafina body the retired EDI field stored; its full UPC-A is
        // 012000001574, and SSCS drops the 4 again on import.
        byte[] file = TestUtil.CrlfFile(TestUtil.PepsiHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 1650, pack: 15, cases: 2, eaListCents: 0)));

        var line = Assert.Single(EdiConverter.Convert(file).Invoice.Lines);

        Assert.Equal("012000001574", line.Gtin);
        Assert.Equal("01200000157", Upc.SscsIdFromGtin(line.Gtin));
        Assert.True(Upc.IsValidUpcA12(line.Gtin));
    }

    [Fact]
    public void ShortUpcEBarcodesAreExpandedToAFullUpc()
    {
        byte[] file = TestUtil.CrlfFile(TestUtil.GhraHeader,
            TestUtil.B("03432004   ", Fields64(caseCostCents: 500, pack: 1, cases: 2, eaListCents: 99)));

        var result = EdiConverter.Convert(file);
        var line = Assert.Single(result.Invoice.Lines);

        Assert.Equal(1, result.UpcEExpandedCount);
        Assert.Equal("03400000320", Upc.SscsIdFromGtin(line.Gtin));  // the verified expansion
        Assert.True(Upc.IsValidUpcA12(line.Gtin));
    }

    [Fact]
    public void ANonBarcodeCodeIsPaddedSoSscsStoresItUnchanged()
    {
        byte[] file = TestUtil.CrlfFile(TestUtil.Ghra13Header,
            TestUtil.B13("002ZH7S8J999 ", Fields64(caseCostCents: 300, pack: 1, cases: 1, eaListCents: 599)));

        var line = Assert.Single(EdiConverter.Convert(file).Invoice.Lines);

        Assert.Equal("002ZH7S8J99", Upc.SscsIdFromGtin(line.Gtin));
    }

    // ------------------------------------------------------------------ header

    [Fact]
    public void APepsiHeaderHasNoDateSoTheElementIsLeftOut()
    {
        byte[] file = TestUtil.CrlfFile(TestUtil.PepsiHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 1650, pack: 15, cases: 2, eaListCents: 0)));

        var result = EdiConverter.Convert(file);
        var doc = XDocument.Parse(TestUtil.Text(result.OutputBytes));

        Assert.Null(result.Invoice.Date);
        Assert.Empty(doc.Descendants("InvoiceDate"));
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.NoInvoiceDate);
    }

    [Fact]
    public void HeaderDatesAreReadInEachVendorsOwnOrder()
    {
        // GHRA writes MMDDYY: 081318 is 13 August 2018.
        byte[] ghra = TestUtil.CrlfFile(TestUtil.GhraHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)));
        Assert.Equal(new DateOnly(2018, 8, 13), EdiConverter.Convert(ghra).Invoice.Date);

        // FRITOLAY writes YYMMDD, recognised because 24 cannot be a month: 240206 is 6 Feb 2024.
        byte[] frito = TestUtil.CrlfFile("FRITOLA00486907392402060000017899",
            TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)));
        Assert.Equal(new DateOnly(2024, 2, 6), EdiConverter.Convert(frito).Invoice.Date);

        // Ruby Red writes YYYYMMDD in a 64-byte header: 20260408 is 8 April 2026.
        byte[] ruby = TestUtil.LfFile(TestUtil.RubyHeader,
            TestUtil.BRuby("762983638051 ", Fields(caseCostCents: 469, pack: 1, cases: 10, eaListCents: 799)));
        Assert.Equal(new DateOnly(2026, 4, 8), EdiConverter.Convert(ruby).Invoice.Date);
    }

    [Fact]
    public void TheVendorCodeBecomesTheSupplierNameSoExistingSscsMappingsKeepWorking()
    {
        byte[] ghra13 = TestUtil.CrlfFile(TestUtil.Ghra13Header,
            TestUtil.B13("012000001574 ", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)));
        Assert.Equal("GHRA", EdiConverter.Convert(ghra13).Invoice.SupplierName);

        // FRITOLA fills the whole 7-character field; SSCS knows this vendor as the 6-character FRITOL.
        byte[] frito = TestUtil.CrlfFile("FRITOLA00486907392402060000017899",
            TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)));
        var result = EdiConverter.Convert(frito);
        Assert.Equal("FRITOL", result.Invoice.SupplierName);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ReasonCodes.VendorTruncated);
    }

    // ------------------------------------------------------------------ quantities and credits

    [Fact]
    public void UnitsAreCasesTimesPackAndCostIsPerUnit()
    {
        // A Pepsi row: $16.50 a case, 15 units to a case, 2 cases => 30 units at $1.10, $33.00.
        byte[] file = TestUtil.CrlfFile(TestUtil.PepsiHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 1650, pack: 15, cases: 2, eaListCents: 0)));

        var line = Assert.Single(EdiConverter.Convert(file).Invoice.Lines);

        Assert.Equal(30, line.Units);
        Assert.Equal(1.1000m, line.UnitCost);
        Assert.Equal(33.00m, line.ExtendedCost);
    }

    [Fact]
    public void TheEachListFieldIsCarriedThroughAsTheSellingPrice()
    {
        byte[] file = TestUtil.CrlfFile(TestUtil.GhraHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 1650, pack: 15, cases: 2, eaListCents: 249)));

        var line = Assert.Single(EdiConverter.Convert(file).Invoice.Lines);

        Assert.Equal(2.49m, line.RetailPrice);   // what it sells for, unrelated to the $1.10 cost
        Assert.Equal(1.1000m, line.UnitCost);
    }

    [Fact]
    public void ACreditLineComesOutAsANegativeQuantity()
    {
        // Credits are the ordinary shape with a minus in the case count — nothing else changes.
        byte[] file = TestUtil.LfFile(TestUtil.RubyHeader,
            TestUtil.BRuby("6917673165728", Fields(caseCostCents: 1199, pack: 1, cases: -1, eaListCents: 2399)));

        var line = Assert.Single(EdiConverter.Convert(file).Invoice.Lines);

        Assert.Equal(-1, line.Units);
        Assert.Equal(11.99m, line.UnitCost);
        Assert.Equal(-11.99m, line.ExtendedCost);
    }

    [Fact]
    public void AWholeCreditInvoiceKeepsItsNegativeTotal()
    {
        // GHRA sends these: the header amount carries the minus inside its zero padding.
        byte[] file = TestUtil.CrlfFile("GHRA   00001189550703180000-22000",
            TestUtil.B("01200000157", Fields64(caseCostCents: 11000, pack: 1, cases: -2, eaListCents: 0)));

        var result = EdiConverter.Convert(file);
        var doc = XDocument.Parse(TestUtil.Text(result.OutputBytes));

        Assert.Equal(-220.00m, result.Invoice.Total);
        Assert.Equal(-220.00m, result.Invoice.LineTotal);
        Assert.Equal("-220.00", doc.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal("-2", doc.Descendants("InvoiceUnitQty").Single().Value);
    }

    // ------------------------------------------------------------------ charge records

    [Fact]
    public void ChargeRecordsAreDroppedBecauseTheyCarryNoMoney()
    {
        // Header total $1.00 so the single line reconciles and nothing else can warn.
        byte[] file = TestUtil.CrlfFile("GHRA   00001314540813180000000100",
            TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)),
            TestUtil.CRecord);

        var result = EdiConverter.Convert(file);

        Assert.Equal(1, result.ChargeRecordCount);
        Assert.Single(result.Invoice.Lines);

        // Counted, not warned about: these appear on nearly every invoice and carry no money, so a
        // warning here would fire constantly and drown out the ones that matter.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheFritoLayFixtureStillTotalsItsHeaderAmountWithoutItsChargeRecord()
    {
        // The reason dropping C records is safe: this file's product lines already sum to $178.99.
        var result = EdiConverter.Convert(TestUtil.Fixture(Path.Combine("EDI", "EDI_FRITOLAY-48690739.txt")));

        Assert.Equal(178.99m, result.Invoice.Total);
        Assert.Equal(178.99m, result.Invoice.LineTotal);
        Assert.True(result.ChargeRecordCount > 0);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void ConvertedXmlIsRecognisedAndRefusedInsteadOfBeingConvertedAgain()
    {
        byte[] once = EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader,
            TestUtil.B("01200000157", Fields64(caseCostCents: 100, pack: 1, cases: 1, eaListCents: 0)))).OutputBytes;

        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(once));
        Assert.Equal(ReasonCodes.AlreadyConverted, ex.ReasonCode);
    }

    [Fact]
    public void AHeaderWithNoProductLinesIsRefused()
    {
        var ex = Assert.Throws<EdiConversionException>(() => EdiConverter.Convert(TestUtil.CrlfFile(TestUtil.GhraHeader)));
        Assert.Equal(ReasonCodes.NoProductRecords, ex.ReasonCode);
    }

    // ------------------------------------------------------------------ helpers

    private static decimal Money(XElement e) => decimal.Parse(e.Value, CultureInfo.InvariantCulture);

    /// <summary>Builds the 55 bytes that follow the code field: description, SKU, case cost, pack, cases, each-list.</summary>
    private static string Fields(int caseCostCents, int pack, int cases, int eaListCents, string description = "TEST ITEM")
        => description.PadRight(25)[..25]
         + "0".PadRight(6)
         + caseCostCents.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')
         + "  "
         + pack.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')
         + Signed(cases, 5)
         + eaListCents.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0');

    /// <summary>The same fields padded out to the 64 trailing bytes a 76/78-byte record carries.</summary>
    private static string Fields64(int caseCostCents, int pack, int cases, int eaListCents, string description = "TEST ITEM")
        => Fields(caseCostCents, pack, cases, eaListCents, description).PadRight(64);

    /// <summary>Writes a possibly negative value the way these files do — the minus sits inside the zero padding.</summary>
    private static string Signed(int value, int width)
    {
        string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        string body = value < 0 ? "-" + digits : digits;
        return body.PadLeft(width, '0');
    }

    /// <summary>
    /// Finds the retired fixed-width output paired with an input fixture and returns its product
    /// codes in order, or null when there is no pair. Pairing is by the trailing number in the
    /// file name (EDI_GHRA13_120511 → SSCS_GHRA_120511).
    /// </summary>
    private static string[]? RetiredOutputCodesFor(string fixture)
    {
        string stem = Path.GetFileNameWithoutExtension(fixture);
        var m = Regex.Match(stem, @"(\d{4,})$");
        if (!m.Success) return null;

        string outputDir = fixture.StartsWith("Hidden", StringComparison.Ordinal) ? "HiddenSSCS" : "SSCS";
        string dir = TestUtil.FixturePath(outputDir);
        if (!Directory.Exists(dir)) return null;

        string? paired = Directory.EnumerateFiles(dir, "*.txt")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith(m.Value, StringComparison.Ordinal));
        if (paired is null) return null;

        return File.ReadAllLines(paired)
            .Where(l => l.StartsWith('B'))
            .Select(l => l.Length >= 12 ? l[1..12].Trim() : l[1..].Trim())
            .ToArray();
    }
}
