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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Handwriting on the paper. Invoice 4646600671 (07/27/26, $1317.21) came back from the store
    // with a pen stroke drawn down the AMOUNT column. The stroke was long enough to cross two
    // printed lines, so PdfPig reports a stray "/" on both: the Elct StbEw row and the PKG
    // subtotal under it. Text below is the real extraction, damage included.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] PenMarkedInvoiceLines =
    [
        "Dr Pepper Bottling Company of Texas",
        "10909 Green Crossing BLVD Apt/Suite 100",
        "(713,) 799-1024",
        "DELIVERY ROUTE: 120120645",
        "INVOICE",
        "4646600671",
        "DATE: 07/27/26 TIME: 08:04. AM",
        "BEST STOP 000014",
        "ACCOUNT # 13268486",
        "DESCRIPTION UPC/SEU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "12 OZ ALUM CAN 12PK X 2",
        "12z12p DrPep DEP 078000082166/10000836",
        "24.00 12 24 12.75 0.00 153.00",
        "12z12p Zsgr DP 078000035261/10136728",
        "24.00 1 2 12.75 0.00 12.75",
        "12z12p Dt DP DEP 078000083163/10001754",
        "24.00 1 2 12.75 0.00 12.75",
        "12z12P Squirt 078000016161/10000187",
        "24.00 2 4 12.75 0.00 25.50",
        "PEG 16 32 204.00",
        "20 OZ PLASTIC BTL LS 24",
        "20z24 DrPep 078000082401/10001195",
        "56.00 5 120 36.14 0.00 180.70",
        "20z24 Dt DP 078000083408/10001625",
        "56.00 1 24 36.14 0.00 36.14",
        "20z24 DP Blckbry 078000040586/10165042",
        "56.00 1 24 36.14 0.00 36.14",
        "PEG 7 168 252.98",
        "2 L PLASTIC BTL LS 8",
        "2L8 DrPep 078000082463/10001422",
        "30.00 1 8 21.57 0.00 21.57",
        "2L8 Squirt 078000016468/10001435",
        "30.00 1 8 21.57 0.00 21.57",
        "PEG 2 16 43.14",
        "16 OZ ALUM CAN LS 12",
        "16z12GhstEnSrStpRbCd 810128522694/10175283",
        "35.90 1 12 20.66 0.00 20.66",
        "PEG 1 12 20.66",
        "16 OZ ALUM CAN LS 12 PAL160",
        "16z12 C4Prf JR WtM1n 842595134964/20042067",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfCsmcRbw 84 25 95 12 176 6/2003 44 50", // OCR spaces inside the code token
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfFrznBmscl 842595106596/20034426",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfmStbrBlst 842595121759/20034425",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12 Bre Grpx 840461210644/10175287",
        "31.00 1 12 23.03 0.00 23,03",                    // ',' scanned for '.' in AMOUNT
        "16z121 Brcc Rngr Bry 810140157683/10170027",
        "31.00 1 12 23.03 0.00 23.03",
        "16z121 Brcc TgrStrk 840461210620/10175292",
        "31.00 1 12 23.03 0.00 23.03",
        "PEG 7 84 151.73",
        "16 OZ ALUM CAN LS 24",
        "16z24 VnmEn Mng 078000023763/10146510",
        "36.00 1 24 18.50 0.00 18.50",
        "PEG 1 24 18.50",
        "16 OZ GLASS BTL LS 12",
        "16z12 Clam 014800511758/10011917",
        "24.00 1 12 21.40 0.00 21.40",
        "PEG 1 12 21.40",
        "5 GAL BAG -IN-BOX POSTMIX",
        "SGBIB DrPep 054900710545/10000703",
        "115.95 2 2 115.95 0.00 231.90",
        "PEG 2 2 231.90",
        "17 OZ TETRA PK LS 12",
        "0.5L12 VC PureCocoDb 898999010007/20000642",
        "37.00 1 12 24.85 0.00 24.85",
        "PEG 1 12 24.85",
        "20.280Z/600ML PLASTIC BTL LS 12",
        "600m112 Pnpiel Narjd 078000200270/20020470",
        "22.00 1 12 14.42 0.00 14.42",
        "PEG 1 12 14.42",
        "30.40Z PLASTIC BTL LS 12",
        "30.4z12 CoreHyd 853004004037/10123487",
        "30.00 3 36 17.00 0.00 51.00",
        "PEG 3 36 51.00",
        "23.90Z/707ML PLASTIC BTL LS 24",
        "23.9z24 CoreHyd 853004004952/10123484",
        "50.00 1 24 33.30 0.00 33.30",
        "PEG 1 24 33.30",
        "25.30Z/750ML PLASTIC BTL LS 12",
        "750m1 12 Evian Wtr 079298400113/20028336",
        "35.00 1 12 24.75 0.00 24.75",
        "PEG 1 12 24.75",
        "210Z/621ML POLYPROPYLENE BTL LS 12",
        "21z12 Elct BryB1s 887725000436/20035830",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct BuRsb 887725001303/20035837",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct FP 887725000450/20035831",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct Gp 887725000009/20035821",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Elct LmnLm 887725000061/20035824",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct Mng 887725001365/20035832",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct StbEw 887725000252/20035823",
        "38.00 1 12 25.07 0.00 / 25.07",                  // the pen stroke, first line it crossed
        "PEG 8 96 / 200.56",                              // the same stroke, second line
        "16.50Z/488ML ALUM CAN LS 12",
        "16.5z12 VCJce Pulp 898999012698/20039373",
        "28.00 1 12 24.02 0.00' 24.02",                   // smudge welded to the unused TAX column
        "PEG- 1 12 I 24.02",
        "TOT SALE 53 =554 131721",
        "I~",
        "AMOUNT DUE $1317.21",
        "PALLET PLASTIC 37X37 75001490 1",
        "SHELL PET 208 75000014 7",
        "COD -CASH, CHECK OR MONEY ORDER",
    ];

    /// <summary>
    /// The invoice that started this: the stroke sits in white space between the unused TAX column
    /// and AMOUNT, so it obscures nothing and the file must convert. Before the fix the whole row
    /// was dropped and the invoice was refused for being $25.07 short of its own AMOUNT DUE.
    /// </summary>
    [Fact]
    public void PenStrokeThatObscuresNothing_IsIgnoredAndTheInvoiceConverts()
    {
        var warnings = new List<ConversionWarning>();
        var invoice = DrPepperPdfParser.Parse(PenMarkedInvoiceLines, warnings);

        Assert.Equal("4646600671", invoice.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 7, 27), invoice.Date);
        Assert.Equal(1317.21m, invoice.Total);
        Assert.Equal(33, invoice.Lines.Count);      // every product, including the marked one
        Assert.Equal(invoice.Total, invoice.LineTotal);

        // The struck row itself: read in full, at the price the invoice printed.
        var struck = invoice.Lines.Single(l => l.Gtin == "887725000252");
        Assert.Equal(12, struck.Units);
        Assert.Equal(2.0892m, struck.UnitCost);   // 25.07 / 12
        Assert.Equal(25.07m, struck.ExtendedCost);

        // A mark that hides nothing is not the operator's problem, so it raises no warning.
        Assert.DoesNotContain(warnings, w => w.ReasonCode == PdfReasonCodes.UnreadableLine);
    }

    /// <summary>The stroke crossed the PKG subtotal too; subtotals are skipped, marked or not.</summary>
    [Fact]
    public void AMarkedSubtotalLineIsStillNotAProduct()
    {
        var invoice = DrPepperPdfParser.Parse(PenMarkedInvoiceLines, []);

        Assert.DoesNotContain(invoice.Lines, l => l.ExtendedCost == 200.56m);
        Assert.Equal(7, invoice.Lines.Count(l => l.Description.StartsWith("21z12 Elct", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The other half of the rule: ink on a figure that decides stock or money must stop the file.
    /// Each of these reconciles to AMOUNT DUE or silently books the wrong thing, so the totals
    /// check cannot catch them — the row has to be refused where it is read.
    /// </summary>
    [Theory]
    // Stroke splits the UNITS column: "12" reads as "1" and "2/", booking 1 unit instead of 12.
    [InlineData("38.00 1 1 2/ 25.07 0.00 25.07")]
    // Stroke through AMOUNT itself.
    [InlineData("38.00 1 12 25.07 0.00 25/07")]
    // Stroke through WHSLE, which seeds the shelf price of a new item.
    [InlineData("38/00 1 12 25.07 0.00 25.07")]
    // Stroke welded to CASES.
    [InlineData("38.00 1/ 12 25.07 0.00 25.07")]
    // A column lost entirely to the scan.
    [InlineData("38.00 1 12 25.07 25.07")]
    public void MarkOnAFigureThatDecidesStockOrMoney_IsRefused(string damagedRow)
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            damagedRow,
            "AMOUNT DUE $25.07",
        ];

        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
        Assert.Contains("21z12 Elct StbEw", ex.Message, StringComparison.Ordinal); // names the item
        Assert.Contains(damagedRow, ex.Message, StringComparison.Ordinal);         // quotes the line
    }

    /// <summary>
    /// A stroke through the barcode is the dangerous one: the money still adds up to the printed
    /// AMOUNT DUE, so the invoice would convert clean while an item went missing or landed on the
    /// wrong PLU. The priced row underneath has no product line to belong to, which is the tell.
    /// </summary>
    [Theory]
    [InlineData("21z12 Elct StbEw 887725000/252/20035823")] // stroke read as an extra '/'
    [InlineData("21z12 Elct StbEw 887725000252|20035823")]  // stroke swallowed the real separator
    public void MarkThroughABarcode_IsRefusedEvenThoughTheMoneyReconciles(string damagedProductLine)
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct Mng 887725001365/20035832",
            "38.00 1 12 25.07 0.00 25.07",
            damagedProductLine,
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $50.14", // reconciles either way — this is why the total check is not enough
        ];

        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
        Assert.Contains(damagedProductLine, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guard on the rule above: a "&lt;digits&gt;/&lt;digits&gt;" line that is not a product — the
    /// equipment lines, a package header — must still be skipped in silence, not read as damage.
    /// </summary>
    [Fact]
    public void NonProductLinesAreStillSkippedSilently()
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "20.280Z/600ML PLASTIC BTL LS 12",
            "600m112 Pnpiel Narjd 078000200270/20020470",
            "22.00 1 12 14.42 0.00 14.42",
            "PEG 1 12 14.42",
            "AMOUNT DUE $14.42",
            "PALLET PLASTIC 37X37 75001490 1",
            "SHELL PET 208 75000014 7",
        ];

        var invoice = DrPepperPdfParser.Parse(lines, []);

        Assert.Single(invoice.Lines);
        Assert.Equal("078000200270", invoice.Lines[0].Gtin);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Invoice 4646105204 (07/24/26, $1468.73), store #10. A delivery with returns on it, and the
    // first sample of three things the 2026-07 scans do that the older ones never did:
    //   * a CREDITS section, printed under a shorter header with no WHSLE and money in brackets;
    //   * a description welded to the barcode with no space ("PureCocoDb898999010007/...");
    //   * punctuation dropped inside the code token ("/100,00862", "071817`000453").
    // Two scans of this same ticket exist; the text below is the clean one.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] CreditsInvoiceLines =
    [
        "Dr Pepper Bottling Company of Texas",
        "(713) 799-1024",
        "DELIVERY ROUTE: 120120640",
        "INVOICE",
        "4646105204",
        "4646105205",                                          // the ticket carries two numbers
        "DATE: 07/24/26 TIME: 8:29 AM",
        "BEST STOP 000010",
        "ACCOUNT # 13164167",
        "SALES",
        "DESCRIPTION UPC/SKU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "12 OZ ALUM CAN 12PK X 2",
        "12z12P AW RtBr 078000052169/10000852",
        "24.00 1 2 12.75 0.00 12.75",
        "12z12p BigRed 071817033291/100,00862",                // comma dropped into the SKU
        "24.00 2 4 12.75 0.00 25.50",
        "PEG 3 6 38.25",
        "17 OZ TETRA PK LS 12",
        "0.5L12 VC PureCocoDb898999010007/20000642",           // description welded to the barcode
        "37.00 1 12 24.85 0.00 24.85",
        "PEG 1 12 24.85",
        "1 L PLASTIC BTL LS 15",
        "1L15 BigRed 071817`000453/10000069",                  // a mark inside the barcode itself
        "34.00 1 15 28.12 0.00 28.12",
        "PEG 1 15 28.12",
        "CREDITS",
        "DESCRIPTION UPC/SKU",
        "CASES UNITS NET TAX AMOUNT",                          // note: no WHSLE column
        "12z12p Zsgr DP 078000035261/10136728",
        "0 1 6.37 0.00 (6.37)",
        "12z12p Zsgr DP Bkbr 078000040500/10165043",
        "0 1 6.37 0.00 (6.37)",
        "SALES SUMMARY",
        "Sales Qty Amount",
        "Cases 5 91.22",
        "Units 33",
        "Credits",
        "Cases 0 (12.74)",
        "Units 2",
        "SHELLS 12 0.00",
        "PALLETS 2 0.00",
        "Amount Due $78.48",
        "COD-CASH AND CHECK",
        "RECEIVED BY Monique",
    ];

    private static Invoice ParseCreditsInvoice(out List<ConversionWarning> warnings)
    {
        warnings = [];
        return DrPepperPdfParser.Parse(CreditsInvoiceLines, warnings);
    }

    /// <summary>
    /// Returns are line items like any other, carrying the sign on the count rather than the money.
    /// Without them the invoice cannot reconcile: the
    /// sales come to $91.22 against a printed Amount Due of $78.48.
    /// </summary>
    [Fact]
    public void CreditsAreReadAsNegativeQuantitiesSoTheDeliveryNetsOut()
    {
        var invoice = ParseCreditsInvoice(out _);

        Assert.Equal(78.48m, invoice.Total);
        Assert.Equal(6, invoice.Lines.Count);           // 4 sales + 2 credits
        Assert.Equal(invoice.Total, invoice.LineTotal); // 91.22 - 12.74

        var credit = invoice.Lines.Single(l => l.Gtin == "078000040500");
        Assert.Equal(-1, credit.Units);          // sign on the count …
        Assert.Equal(6.37m, credit.UnitCost);    // … cost stays positive
        Assert.Equal(-6.37m, credit.ExtendedCost);
        Assert.NotEqual(0.00m, credit.RetailPrice); // never zero: an unpriced item rings up free
    }

    /// <summary>Credits are worth a person's attention, and the ticket numbers them separately.</summary>
    [Fact]
    public void CreditsRaiseOneWarningNamingBothInvoiceNumbers()
    {
        ParseCreditsInvoice(out var warnings);

        var w = Assert.Single(warnings, w => w.ReasonCode == PdfReasonCodes.CreditLines);
        Assert.Contains("2 credit lines", w.Message, StringComparison.Ordinal);
        Assert.Contains("4646105204", w.Message, StringComparison.Ordinal);
        Assert.Contains("4646105205", w.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scanner loses the space between description and barcode. The barcode is undamaged, so
    /// the line must convert — and the split has to land on the whole 12 digits, never part-way
    /// through them, which is what the letter boundary in ProductLine buys.
    /// </summary>
    [Theory]
    [InlineData("0.5L12 VC PureCocoDb898999010007/20000642", "898999010007", "0.5L12 VC PureCocoDb")]
    [InlineData("16z12 C4 Prf PnkLmnd842595139778/20042079", "842595139778", "16z12 C4 Prf PnkLmnd")]
    [InlineData("600m112 PnFiel Limda078000200287/20020468", "078000200287", "600m112 PnFiel Limda")]
    public void DescriptionWeldedToTheBarcode_StillSplitsOnTheWholeUpc(string productLine, string gtin, string desc)
    {
        string[] lines =
        [
            "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 8:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            productLine,
            "37.00 1 12 24.85 0.00 24.85",
            "Amount Due $24.85",
        ];

        var invoice = DrPepperPdfParser.Parse(lines, []);

        var line = Assert.Single(invoice.Lines);
        Assert.Equal(gtin, line.Gtin);
        Assert.Equal(desc, line.Description);
        Assert.True(Upc.IsValidUpcA12(line.Gtin));
    }

    /// <summary>
    /// Punctuation dropped into the code token. In the SKU it is ignored outright — the SKU is not
    /// written to the XML at all. In the barcode it is removed, but then the check digit has to
    /// agree, because a mark that lands on a barcode may have taken a digit with it.
    /// </summary>
    [Theory]
    [InlineData("12z12p BigRed 071817033291/100,00862", "071817033291")] // comma in the unused SKU
    [InlineData("1L15 BigRed 071817`000453/10000069", "071817000453")]   // mark inside the barcode
    [InlineData("1L15 BigRed 071817'000453/10000069", "071817000453")]   // the other scan of it
    public void NoiseInTheCodeToken_IsRemovedWhenTheBarcodeStillProvesOut(string productLine, string gtin)
    {
        string[] lines =
        [
            "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 8:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            productLine,
            "34.00 1 15 28.12 0.00 28.12",
            "Amount Due $28.12",
        ];
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings);

        Assert.Equal(gtin, Assert.Single(invoice.Lines).Gtin);
        Assert.True(Upc.IsValidUpcA12(gtin));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The limit of that repair. Take a mark out of a barcode and the check digit still fails, and
    /// a digit went with the mark — there is nothing to recover, so the invoice stops. A *clean*
    /// digit read that fails its check digit keeps the old behaviour: warn and ship.
    /// </summary>
    [Fact]
    public void MarkInsideABarcodeThatNoLongerChecksOut_IsRefused()
    {
        string[] lines =
        [
            "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 8:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "1L15 BigRed 07181`7000451/10000069", // a digit lost under the mark
            "34.00 1 15 28.12 0.00 28.12",
            "Amount Due $28.12",
        ];

        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
        Assert.Contains("07181`7000451", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bad scan of invoice 4646105204 has its 600ml barcode destroyed — "0i8000200.287", where
    /// the 7 read as an i. Letters are not noise and cannot be stripped, so this one is refused; the
    /// clean scan of the same ticket converts. Also pins that a damaged AMOUNT ("5b.14") stops.
    /// </summary>
    [Theory]
    [InlineData("600m112 PnFiel Limda0i8000200.287/2.0.020,4.68", "22.00 1 12 14.42 0.00 14.42")]
    [InlineData("21z12 Slot StbKw 887725000252/20035823", "38.00 2 24 25.07 0.00 5b.14")]
    public void TheBadScanOfTheSameTicket_IsRefused(string productLine, string amountsRow)
    {
        string[] lines =
        [
            "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 8:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            productLine,
            amountsRow,
            "Amount Due $14.42",
        ];

        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
    }

    /// <summary>The SALES SUMMARY prints "Cases 0 (19.11)". It is a total, not a credit line.</summary>
    [Fact]
    public void TheSummarysOwnBracketedTotalIsNotMistakenForACredit()
    {
        var invoice = ParseCreditsInvoice(out _);

        Assert.Equal(2, invoice.Lines.Count(l => l.Units < 0));
        Assert.DoesNotContain(invoice.Lines, l => l.ExtendedCost == -12.74m);
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
