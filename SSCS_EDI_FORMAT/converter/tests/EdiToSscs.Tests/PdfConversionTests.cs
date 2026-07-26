using System.Globalization;
using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// PDF → XML pipeline proofs (Dr Pepper). The parser is exercised against the real text of
/// Dr Pepper "example 7" (invoice 4643517800, $532.43) as reconstructed by PdfPig, and the result
/// is checked against the line-by-line figures live SSCS displayed after importing this invoice
/// as XML on 2026-07-25.
/// </summary>
public class PdfConversionTests
{
    // Real page-1 text of "Dr pepper example 7.pdf" as reconstructed by PdfTextExtractor (PdfPig).
    // Includes package headers, PEG/PKG/PRO/RAG subtotals and trailing equipment lines so the parser's
    // skip/stop rules are exercised. The '£' on the C4 line is a real OCR artifact.
    private static readonly string[] Example7Lines =
    [
        "Dr Pepper Battling Company of Texas",
        "DELIVERY ROUTE: 120120616",
        "INVOICE",
        "4643517800",
        "DATE; 04/20/26 TIME; 09;27 AM",
        "BEST STOP OOOO14",
        "ACCOUNT # 13268486",
        "DESCRIPTION UPC/SKU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "20 OZ PLASTIC BTL LS 24",
        "20z24 DrPep 078000082401/10001195",
        "56.00 4 96 32.50 0.00 130.00",
        "PRO 4 96 130.00",
        "2 L PLASTIC BTL LS 8",
        "2L8 DrPep, 078000082463/10001422",
        "30.00 1 8 21.57 0.00 21.57",
        "2L8 SK Orange 078000113464/10001455",
        "30.00 1 8 21.57 0.00 21.57",
        "RAG 2 16 43.14",
        "16 OZ ALUM CAN LS 12",
        "16z12GhstEn Og 810169603703/10175130",
        "35.90 1 12 20.00 0.00 20.00",
        "16z12GhstEn W1chGrp 810128528191/10174169",
        "35.90 1 12 20.00 0.00 20.00",
        "RAG 2 24 40.00",
        "16 OZ ALUM CAN LS 12 PAL160",
        "16z12C4Pr£Frznfmscl 842595106596/20034426",
        "35.90 1 12 20.00 0.00 20.00",
        "16z12C4PrfHawPnc 842595133608/20037607",
        "35.90 1 12 20.00 0.00 20.00",
        "16zl2C4PrfmStbrflst 842595121759/20034425",
        "35.90 1 12 20.00 0.00 20.00",
        "PRO 3 36 60.00",
        "16 OZ ALUM CAN LS 24",
        "16z24 VnmEn Mng 078000023763/10146510",
        "36.00 1 24 18.50 0.00 18.50",
        "16z24 VnmEn Orig 078000023749/10146508",
        "36.00 1 24 18.50 0.00 18.50",
        "PRO 2 48 37.00",
        "16 OZ GLASS BTL LS 12",
        "16z12 Clam 014800511758/10011917",
        "24.00 1 12 21.40 0.00 21.40",
        "RAG 1 12 21.40",
        "5 GAL BAG-IN-BOX POSTMIX",
        "5GBIB DrPep 054900710545/10000703",
        "115.95 1 1 115.95 0.00 115.95",
        "PRO 1 1 115.95",
        "30.4z12 CoreHyd 853004004037/10123487",
        "30.00 1 12 20.80 0.00 20.80",
        "PKG 1 12 20.80",
        "21OZ/621ML POLYPROPYLENE BTL LS 12",
        "21z12 Elct BURsb 887725001303/20035837",
        "38.00 1 12 21.38 0.00 21.38",
        "21z12 Elct Ccn 887725000047/20035818",
        "38.00 1 12 21.38 0.00 21.38",
        "21z12 Elct StbKw 887725000252/20035823",
        "38.00 1 12 21.38 0.00 21.38",
        "PRO 3 36 64.14",
        "TOT SALE 19 281 532.43",
        "AMOUNT DUE $532.43",
        "PALLET 37X37 PLASTIC 75001490 1",
        "Shell PET 2L 75000016 2",
        "COD -CASH, CHECK OR MONEY ORDER",
    ];

    private static Invoice ParseExample7(out List<ConversionWarning> warnings)
    {
        warnings = [];
        return DrPepperPdfParser.Parse(Example7Lines, warnings);
    }

    [Fact]
    public void Example7_ParsesHeaderAndAllSixteenLineItems()
    {
        var invoice = ParseExample7(out _);

        Assert.Equal("DR PEPPER", invoice.SupplierName);
        Assert.Equal("4643517800", invoice.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 4, 20), invoice.Date);
        Assert.Equal(532.43m, invoice.Total);
        Assert.Equal(16, invoice.Lines.Count); // 16 products; headers/subtotals/equipment skipped

        // First line: units = UNITS (96, never the 4 cases), unit cost = AMOUNT / UNITS.
        var first = invoice.Lines[0];
        Assert.Equal("078000082401", first.Gtin);   // whole barcode — SSCS drops the check digit itself
        Assert.Equal(96, first.Units);
        Assert.Equal(1.3542m, first.UnitCost);
        Assert.Equal("07800008240", Upc.SscsIdFromGtin(first.Gtin)); // the ID SSCS displayed
    }

    /// <summary>
    /// The figures live SSCS showed in its Ex Cost column for this exact invoice, in order. This is
    /// the proof that 4-decimal unit costs are safe: 14 of these 16 lines do not divide evenly, yet
    /// every one matches and they still sum to the printed total.
    /// </summary>
    [Fact]
    public void Example7_LineCostsMatchWhatLiveSscsComputed()
    {
        decimal[] sscsExCost =
        [
            130.00m, 21.57m, 21.57m, 20.00m, 20.00m, 20.00m, 20.00m, 20.00m,
            18.50m, 18.50m, 21.40m, 115.95m, 20.80m, 21.38m, 21.38m, 21.38m,
        ];

        var invoice = ParseExample7(out _);

        Assert.Equal(sscsExCost, invoice.Lines.Select(l => l.ExtendedCost));
        Assert.Equal(532.43m, invoice.LineTotal);
        Assert.Equal(invoice.Total, invoice.LineTotal);
    }

    [Fact]
    public void Example7_ProducesTheXmlSscsAccepted()
    {
        var invoice = ParseExample7(out _);
        var doc = XDocument.Parse(TestUtil.Text(NaxmlBuilder.Build(invoice)));

        Assert.Equal("DR PEPPER", doc.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("4643517800", doc.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-04-20", doc.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("532.43", doc.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(16, doc.Descendants("LineItem").Count());

        var first = doc.Descendants("LineItem").First();
        Assert.Equal("078000082401", first.Descendants("InvoiceUnitId").Single().Value);
        Assert.Equal("96", first.Descendants("InvoiceUnitQty").Single().Value);
        Assert.Equal("1.3542", first.Descendants("InvoiceUnitCost").Single().Value);

        // Each-list comes from the WHSLE column: 56.00 a case x 4 cases / 96 units = 2.33 each.
        Assert.Equal("2.33", first.Descendants("RetailPrice").Single().Value);

        // Never zero — an unpriced new item could otherwise ring up free.
        Assert.All(doc.Descendants("RetailPrice"), e => Assert.NotEqual("0.00", e.Value));
    }

    [Fact]
    public void EachListIsNoLongerCappedAtTheOldFieldLimit()
    {
        // The retired output squeezed each-list into 4 digits, so anything over $99.99 was written
        // as 0000. XML has no such limit, so the fountain BIB now keeps its real $115.95.
        var bib = ParseExample7(out _).Lines.Single(l => l.Gtin == "054900710545");

        Assert.Equal(115.95m, bib.RetailPrice);
    }

    [Fact]
    public void OcrArtifactsAreStrippedSoTheFileStaysPlainAscii()
    {
        byte[] bytes = NaxmlBuilder.Build(ParseExample7(out _));

        Assert.All(bytes, b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2} reached the output"));

        // The '£' the scanner invented inside "16z12C4Pr£Frznfmscl" is dropped, not encoded.
        string desc = XDocument.Parse(TestUtil.Text(bytes))
            .Descendants("InvoiceUnitDescription")
            .Select(e => e.Value)
            .Single(v => v.Contains("Frznfmscl", StringComparison.Ordinal));
        Assert.Equal("16z12C4Pr Frznfmscl", desc);
    }

    [Fact]
    public void BarcodesGoOutWholeSoSscsLandsOnTheRightItem()
    {
        var invoice = ParseExample7(out _);

        // Every Dr Pepper barcode is a valid 12-digit UPC-A and is sent complete.
        Assert.All(invoice.Lines, l => Assert.True(Upc.IsValidUpcA12(l.Gtin), $"'{l.Gtin}' is not a valid UPC-A"));
        Assert.All(invoice.Lines, l => Assert.Equal(11, Upc.SscsIdFromGtin(l.Gtin).Length));
    }

    [Fact]
    public void CheckDigitHelpersRoundTrip()
    {
        Assert.True(Upc.IsValidUpcA12("079298000078"));  // correct Evian UPC
        Assert.False(Upc.IsValidUpcA12("079198000078")); // the OCR-corrupted one (a 2 became a 1)
        Assert.False(Upc.IsValidUpcA12("07800008240"));  // 11 digits: not a valid 12-digit UPC-A

        // Aquafina: the retired EDI field stored 01200000157; the full barcode is 012000001574.
        Assert.Equal("012000001574", Upc.AppendCheckDigit("01200000157"));
        Assert.Equal("01200000157", Upc.SscsIdFromGtin("012000001574"));
        Assert.StartsWith("0", Upc.AppendCheckDigit("01200000157")); // leading zero never stripped
    }

    [Fact]
    public void MisScannedUpc_IsFlaggedButStillConverts()
    {
        string[] lines =
        [
            "INVOICE", "4643510001", "DATE: 05/06/26 TIME: 10:00 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "1L12 Evian Wtr 079198000078/20028335", // corrupted UPC (fails check digit)
            "36.00 2 24 20.75 0.00 41.50",
            "AMOUNT DUE $41.50",
        ];
        var warnings = new List<ConversionWarning>();
        var invoice = DrPepperPdfParser.Parse(lines, warnings);

        Assert.Single(invoice.Lines);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.UpcCheckDigit);
    }

    [Fact]
    public void TotalThatDoesNotReconcile_IsRejected()
    {
        string[] lines =
        [
            "INVOICE", "4643510002", "DATE: 05/06/26 TIME: 10:00 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "20z24 DrPep 078000082401/10001195",
            "56.00 1 24 32.50 0.00 32.50",
            "AMOUNT DUE $99.99", // does not match the single $32.50 line
        ];
        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));
        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    [Fact]
    public void MissingInvoiceNumber_IsRejectedWithPlainMessage()
    {
        string[] lines =
        [
            "DATE: 05/06/26 TIME: 10:00 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "20z24 DrPep 078000082401/10001195",
            "56.00 1 24 32.50 0.00 32.50",
            "AMOUNT DUE $32.50",
        ];
        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));
        Assert.Equal(PdfReasonCodes.NoInvoiceNumber, ex.ReasonCode);
    }
}
