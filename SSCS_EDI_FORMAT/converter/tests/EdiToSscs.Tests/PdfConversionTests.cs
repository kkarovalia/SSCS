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

    /// <summary>
    /// A barcode that fails its own check digit is **asked about**, not shipped with a note.
    ///
    /// The check digit is arithmetic on the other eleven digits, so failing it does not mean "may be
    /// wrong" — it means a digit is wrong. This ticket read Evian's printed 079298000078 as
    /// 079198000078: twelve clean digits with nothing sitting on them, which is why the old rule
    /// waved it through. Nothing else on the page can catch it, because the money reconciles
    /// whatever code goes out, so the line would land on an item that is not this one.
    /// </summary>
    [Fact]
    public void AUpcThatFailsItsCheckDigitIsAskedFor()
    {
        var asked = new List<DrPepperUnknownField>();
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(
            MisScannedEvian, warnings, f => { asked.Add(f); return "079298000078"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, q.Field);
        Assert.Equal("079198000078", q.PrintedValue);        // what the scan made of it
        Assert.Equal("1L12 Evian Wtr", q.Description);

        Assert.Equal("079298000078", Assert.Single(invoice.Lines).Gtin);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.UpcCheckDigit);
    }

    /// <summary>With no prompt it refuses rather than shipping a code it knows has a wrong digit.</summary>
    [Fact]
    public void WithNoPromptAUpcThatFailsItsCheckDigitRefuses()
    {
        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(MisScannedEvian, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
        Assert.Contains("1L12 Evian Wtr", ex.Message, StringComparison.Ordinal);
        Assert.Contains("079198000078", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Invoice 4643510001: the scan read 079298000078 as 079198000078 off clean paper.</summary>
    private static readonly string[] MisScannedEvian =
    [
        "INVOICE", "4643510001", "DATE: 05/06/26 TIME: 10:00 AM",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "1L12 Evian Wtr 079198000078/20028335",
        "36.00 2 24 20.75 0.00 41.50",
        "AMOUNT DUE $41.50",
    ];

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
    /// The other half of the rule: ink on a figure that decides stock or money must stop the file
    /// when there is nobody to ask. Each of these reconciles to AMOUNT DUE or silently books the
    /// wrong thing, so the totals check cannot catch them.
    ///
    /// With a prompt available these become questions instead — see the 5218300220 tests. What is
    /// pinned here is that they are never *guessed*.
    /// </summary>
    [Theory]
    // Stroke splits the UNITS column: "12" reads as "1" and "2/", booking 1 unit instead of 12.
    [InlineData("38.00 1 1 2/ 25.07 0.00 25.07")]
    // Stroke through AMOUNT itself.
    [InlineData("38.00 1 12 25.07 0.00 25/07")]
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
    }

    /// <summary>
    /// The columns that are *not* stock or money. WHSLE and CASES feed nothing but the shelf price
    /// suggested for an item SSCS has never seen — it keeps its own for everything it stocks — so
    /// losing one to a pen stroke must not cost the delivery. The line goes out with the right
    /// barcode, quantity and cost, its retail seeded from cost, and a warning saying so.
    ///
    /// This used to refuse, which is the over-blocking the store feels: a whole delivery re-keyed by
    /// hand over a suggested price on a line SSCS was going to price itself anyway.
    /// </summary>
    [Theory]
    [InlineData("38/00 1 12 25.07 0.00 25.07", "38/00")]   // stroke through WHSLE
    [InlineData("38.00 1/ 12 25.07 0.00 25.07", "1/")]     // stroke welded to CASES
    public void AMarkOnAFigureThatOnlySeedsTheShelfPrice_StillConverts(string damagedRow, string damaged)
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            damagedRow,
            "AMOUNT DUE $25.07",
        ];
        var warnings = new List<ConversionWarning>();

        var line = Assert.Single(DrPepperPdfParser.Parse(lines, warnings).Lines);

        Assert.Equal("887725000252", line.Gtin);       // what SSCS matches on
        Assert.Equal(12, line.Units);                  // what it books
        Assert.Equal(25.07m, line.ExtendedCost);       // what it pays
        Assert.NotEqual(0.00m, line.RetailPrice);      // never zero: an unpriced item rings up free

        var w = Assert.Single(warnings);
        Assert.Contains("21z12 Elct StbEw", w.Message, StringComparison.Ordinal);
        Assert.Contains(damaged, w.Message, StringComparison.Ordinal);
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

    // -----------------------------------------------------------------------------------------
    // Invoice 5218300220 (08/10/26, $1080.22), store #10. The store scanned this one ticket twice,
    // eleven minutes apart, and NEITHER scan converted. Between them they carry four kinds of damage
    // the older samples never showed:
    //
    //   * "DATE:." - punctuation dropped beside the label, which cost the whole invoice;
    //   * "05490.0710545" - a period sitting inside a barcode;
    //   * "10136S14" - a letter inside the SKU, which is never written to the XML and cost the line
    //     anyway;
    //   * "1." and two "11"s in the UNITS column. The first took $115.95 off the invoice; the other
    //     two would have booked a unit short at a price that still added up to the penny.
    //
    // Both texts below are the real extraction, damage included. Keeping the pair is the point: they
    // are two photographs of one piece of paper, so whatever the converter reads out of them has to
    // agree, and the damage in each falls in a different place.
    // -----------------------------------------------------------------------------------------

    /// <summary>The 11:29 scan: clean date, damaged barcode, three damaged unit counts.</summary>
    private static readonly string[] TwiceScannedTicket1129 =
    [
        "Dr Pepper Bottling Company of Texas",
        "10909 Green Crossing BLVD Apt/Suite 100",
        "HOUSTON, TX 77038",
        "(713) 799-1024",
        "DELIVERY ROUTE: 120140606",
        "DRIVER: 661816-DORIAN",
        "SALES ROUTE: 120110032",
        "SALES REP: 543139-JARVIS",
        "CARRIER: SPLASH TRANSPORT, INC",
        "INVOICE",
        "5218300220",
        "DATE: 08/10/26 TIME: 11:29 AM",
        "BEST STOP 000010",
        "20110 FM 1314 RD",
        "PORTER, TX 77365-3443",
        "ACCOUNT # 13164167",
        "STORE # 000010",
        "SALES",
        "DESCRIPTION UPC/SKU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "12 OZ ALUM CAN 12PK X 2",
        "12z12p DrPep DEP 078000082166/10000836",
        "24.00 3 6 12.75 0.00 38.25",
        "12z12P Squirt 078000016161/10000187",
        "24.00 1 2 12.75 0.00 12.75",
        "PEG 4 8 51.00",
        "20 OZ PLASTIC BTL LS 24",
        "20z24 DrPep 078000082401/10001195",
        "56.00 3 72 36.14 0.00 108.42",
        "20z24 Dt DP 078000083408/10001625",
        "56.00 1 24 36.14 0.00 36.14",
        "20224 ChryDP 078000098402/10001796",
        "56.00 1 24 36.14 0.00 36.14",
        "20z24 Squirt 078000016406/10001120",
        "56.00 1 24 36.14 0.00 36.14",
        "PEG 6 144 216.84",
        "1 L PLASTIC BTL LS 12 PAL98",
        "1L12 Evian Wtr 079298000078/20028335",
        "36.00 1 12 26.65 0.00 26.65",
        "PEG 1 12 26.65",
        "16 OZ ALUM CAN LS 12",
        "16z12GhstEn Strbngo 810169600023/10174147",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12GhstEn W1chGrp 810128528191/10174169",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12GhstEnSrStpRbCd 810128522694/10175283",
        "35.90 1 12 20.66 0.00 20.66",
        "PEG 3 36 61.98",
        "16 OZ ALUM CAN LS 12 PAL160",
        "16z12 C4Prf JR GrApl 842595134971/20042066",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12 C4Prf MngFgo 842595139310/20042072",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfFrznSmscl 842595106596/20034426",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4U1tm FP 842595121650/20034440",
        "36.00 1 12 20.66 0.00 20.66",
        "PEG 4 48 82.64",
        "16 OZ ALUM CAN LS 24",
        "16z24 VnmEn Mng 078000023763/10146510",
        "36.00 1 24 18.50 0.00 18.50",
        "16z24 VnmEn prig 078000023749/10146508",
        "36.00 1 24 18.50 0.00 18.50",
        "PKG 2 48 37.00",
        "16 OZ GLASS BTL LS 12",
        "16z12 Clam 014800511758/10011917",
        "24.00 1 12 21.40 0.00 21.40",
        "PEG 1 12 21.40",
        "16 OZ PLASTIC BTL LS 12",
        "16z12 SN Apple 076183003282/10136538",
        "23.00 1 12 15.00 0.00 15.00",
        "16z12 SN Frt Pnch 076183003138/10136514",
        "23.00 1 12 15.00 0.00 15.00",
        "16z12 SN KwStrwbry 076183003107/10136515",
        "23.00 1 12 15.00 0.00 15.00",
        "PEG 3 36 45.00",
        "5 GAL BAG -IN-BOX POSTMIX",
        "5GBIB DrPep 05490.0710545/10000703",
        "115.95 1 1. 11S.95 0.00 115.95",
        "PEG 1 1 115.95",
        "18 OZ PLASTIC BTL LS 12",
        "18z12 Hai CcntMlok 813694026719/10159894",
        "25.00 1 11 17.00 0.00 17.00",
        "PEG 1 12 17.00",
        "32 OZ PLASTIC BTL LS 12",
        "32z12 SN Apple 076183328507/10088249",
        "26.00 1 12 23.52 0.00 23.52",
        "PKG 1 12 23.52",
        "17 OZ TETRA PK LS 12",
        "0.5L12 VC PressCocDb 898999010144/20026703",
        "37.00 1 12 24.85 0.00 24.85",
        "0.5L12 VC PureCocoDb 898999010007/20000642",
        "37.00 1 12 24.85 0.00 24.85",
        "PEG 2 24 49.70",
        "20.280Z/600ML PLASTIC BTL LS 12",
        "600m112 PnFiel Limda 078000200287/20020468",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFiel Narjd 078000200270/20020470",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFielAgaMnr 078000200249/20020466",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112PnfelPinaplmd 078000030914/20026469",
        "22.00 1 12 14.42 0.00 14.42",
        "PEG 4 48 57.68",
        "23.90Z/707ML PLASTIC BTL LS 24",
        "23.9z24 CoreHyd 853004004952/10123484",
        "50.00 1 24 33.30 0.00 33.30",
        "PKG 1 24 33.30",
        "12OZ/355ML ALUM CAN SLEEK LS 12",
        "12z12 31m CrspApl 842595138214/20040249",
        "31.00 1 11 20.00 0.00 20.00",
        "12z12 Elm ShrlTmpl 842595141375/20042056",
        "31.00 1 12 20.00 J.00 20.00",
        "PEG 2 24 40.00",
        "2102/621ML POLYPROPYLENE BTL LS 12",
        "21z12 Elet BuRsb 887725001303/20035837",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 E1et Con 887725000047/20035818",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Elct CmbLim 887725000979/20035827",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Slot Gp 887725000009/20035821",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 E1et Orn 887725000054/20035819",
        "38.00 1 12 25.07 0.00 25.07",
        "PEG 8 96 200.56",
        "TOT SALE 44 585 1080.22",
        "AMOUNT DUE $1080.22",
        "PALLET PLASTIC 37X37 75001490 1",
        "SHELL PET 208 75000014 6",
        "rnn-r~►SH, CHECK OR MONEY ORDER",
        "RECEIVED BY Monique",
        "NOTICE",
        "Sales invoices shall be final and binding one",
        "year from the date of issuance. The Company",
        "will not investigate, review or adjust any",
        "invoice which is older than one year.",
        "If your check is returned, it may be re-",
        "presented electronically or by paper draft and",
        "you will be responsible for all collection",
        "costs. A return fee will be charged on any item",
        "that is returned by your bank and the fees may",
        "be collected electronically or by paper draft.",
    ];

    /// <summary>The 11:19 scan of the same paper: damaged date, damaged barcode, damaged SKU.</summary>
    private static readonly string[] TwiceScannedTicket1119 =
    [
        "Dr Pepper Bottling Company of Texas",
        "10909 Green Crossing BLVD Apt/Suite 100",
        "HOUSTON, TX 77038",
        "(713) 799-1024",
        "DELIVERY ROUTE: 120140606",
        "DRIVER: 661816-DORIAN",
        "SALES ROUTE: 120110032",
        "SALES REP: 543139-JARVIS",
        "CARRIER: SPLASH TRANSPORT, INC",
        "INVOICE",
        "5218300220",
        "DATE:. 08/10/26 TIME: 11:19 AM",
        "BEST STOP 000010",
        "20110 FM 1314 RD",
        "PORTER, TX 77365-3443",
        "ACCOUNT # 13164167",
        "STORE # 000010",
        "SALES",
        "DESCRIPTION UPC/SKU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "12 OZ ALUM CAN 12PK X 2",
        "12z12p DrPep DEP 078000082166/10000836",
        "24.00 3 6 12.75 0.00 38.25",
        "12z12P Squirt 078000016161/10000187",
        "24.00 1 2 12.75 0.00 12.75",
        "PKG 4 8 51.00",
        "20 OZ PLASTIC BTL LS 24",
        "20z24 DrPep 078000082401/10001195",
        "56.00 3 72 36.14 0.00 108.42",
        "20z24 Dt DP 078000083408/10001625",
        "56.00 1 24 36.14 0.00 36.14",
        "20z24 ChryDP 078000098402/10001796",
        "56.00 1 24 36.14 0.00 36.14",
        "20z24 Squirt 078000016406/10001120",
        "56.00 1 24 36.14 0.00 36.14",
        "PKG 6 144 216.84",
        "1 L PLASTIC BTL LS 12 PAL98",
        "1L12 Evian Wtr 079298000078/20028335",
        "36.00 1 12 26.65 0.00 26.65",
        "PKG 1 12 26.65",
        "16 OZ ALUM CAN LS 12",
        "16z12GhstEn Strbngo 810169600023/10174147",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12GhstEn W1chGrp 810128528191/10174169",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12GhstEnSrStpRbCd 810128522694/10175283",
        "35.90 1 12 20.66 0.00 20.66",
        "PKG 3 36 61.98",
        "16 OZ ALUM CAN LS 12 PAL160",
        "16z12 C4Prf JR GrApl 842595134971/20042066",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12 C4Prf MngFgo 842595139310/20042072",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfFrznBmscl 842595106596/20034426",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4U1tm FP 842595121650/20034440",
        "36.00 1 12 20.66 0.00 20.66",
        "PKG 4 48 82.64",
        "16 OZ ALUM CAN LS 24",
        "16z24 VnmEn Mng 078000023763/10146510",
        "36.00 1 24 18.50 0.00 18.50",
        "16z24 VnmEn Orig 078000023749/101,46508",
        "36.00 1 24 18.50 0.00 18.50",
        "PKG 2 48 37.00",
        "16 OZ GLASS BTL LS 12",
        "16z12 Clam 014800511758/10011917",
        "24.00 1 12 21.40 0.00 21.40",
        "PKG 1 12 21.40",
        "16 OZ PLASTIC BTL LS 12",
        "16z12 SN Apple 076183003282/10136538",
        "23.00 1 12 15.00 0.00 15.00",
        "16z12 SN Frt Pnch 076183003138/10136S14",
        "23.00 1 12 15.00 0.00 15.00",
        "16z12 SN KwStrwbry 076183003107/10136515",
        "23.00 1 12 15.00 0.00 15.00",
        "PKG 3 36 45.00",
        "5 GAL BAG-IN -BOX POSTMIX",
        "50515 DrPep 05490.0710545/10000703",
        "115.95 1 1 115.95 0.00 115.95",
        "PEG 1 1 115.95",
        "18 OZ PLASTIC BTL LS 12",
        "18z12 Bai CcntMlok 813694026719/10159894",
        "25.00 1 12 17.00 0.00 17.00",
        "PKG 1 12 17.00",
        "32 OZ PLASTIC BTL LS 12",
        "32z12 SN Apple 076183328507/10088249",
        "26.00 1 12 23.52 0.00 23.52",
        "PKG 1 12 23.52",
        "17 OZ TETRA PK LS 12",
        "0.5L12 VC PressCocDb 898999010144/20026703",
        "37.00 1 12 24.85 0.00 24.85",
        "0.5L12 VC PureCocoDb 898999010007/20000642",
        "37.00 1 12 24.85 0.00 24.85",
        "PKG 2 24 49.70",
        "20.280Z/600ML PLASTIC BTL LS 12",
        "600m112 PnFiel Limda 078000200287/20020468",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFiel Narjd 078000200270/20020470",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFielAgaMnr 078000200249/20020466",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112PnfelPinaplmd 078000030914/20026469",
        "22.00 1 12 14.42 0.00 14.42",
        "r",
        "PKG 4 48 57.69",
        "23.9OZ/707ML PLASTIC BTL LS 24",
        "23.9z24 CoreHyd 853004004952/10123484",
        "50.00 1 24 33.30 0.00 33.30",
        "PKG 1 24 33.30",
        "120Z/355ML ALUM CAN SLEEK LS 12",
        "12z12 BIm CrspApl 842595138214/20040249",
        "31.00 1 12 20.00 0.00 20.00",
        "12z12 Bim ShrlTmpl 842595141375/20042086",
        "31.00 1 12 20.00 0.00 20.00",
        "PKG 2 24 40.00",
        "210Z/621ML POLYPROPYLENE BTL LS 12",
        "21s12 Slot 8uRsb 887725001303/20035837",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Slot Con 887725000047/20035818",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Slot CmbLim 887725000979/20035827",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct Gp 887725000009/20035821",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Elct Orn 887725000054/20035819",
        "38.00 1 12 25.07 0.00 25.07",
        "PEG 8 96 200.56",
        "TOT SALE 44 585 108022",
        "AMOUNT DUE $1080.22",
        "PALLET PLASTIC 37X37 75001490 1",
        "SHELL PET 20Z 75000014 6",
        "rnn_rASR, CHECK OR MONEY ORDER",
        "11.77",
        "RECEIVED BY Monique",
        "NOTICE",
        "Sales invoices shall be final and binding one",
        "year from the date of issuance. The Company",
        "will not investigate, review or adjust any",
        "invoice which is older than one year.",
        "If your check is returned, it may be re-",
        "presented electronically or by paper draft and",
        "you will be responsible for all collection",
        "costs. A return fee will be charged on any item",
        "that is returned by your bank and the fees may",
        "be collected electronically or by paper draft.",
    ];

    /// <summary>
    /// The headline: two scans of one ticket must produce one invoice. This is the strongest check
    /// available for a vendor with no reference output - the paper is the same, so every barcode,
    /// quantity, cost and retail has to match across two independent photographs of it.
    /// </summary>
    [Fact]
    public void BothScansOfTheSameTicketProduceTheSameInvoice()
    {
        var first = DrPepperPdfParser.Parse(TwiceScannedTicket1129, [], Paper(("5GBIB DrPep", "1"), ("18z12 Hai CcntMlok", "12"), ("12z12 31m CrspApl", "12")));
        var second = DrPepperPdfParser.Parse(TwiceScannedTicket1119, [], Paper(("5GBIB DrPep", "1"), ("18z12 Hai CcntMlok", "12"), ("12z12 31m CrspApl", "12")));

        Assert.Equal(first.InvoiceNumber, second.InvoiceNumber);
        Assert.Equal(first.Date, second.Date);
        Assert.Equal(first.Total, second.Total);

        static IEnumerable<string> Rows(Invoice i) => i.Lines
            .OrderBy(l => l.Gtin, StringComparer.Ordinal)
            .Select(l => $"{l.Gtin} {l.Units} {l.UnitCost} {l.RetailPrice}");

        Assert.Equal(Rows(first), Rows(second));
    }

    /// <summary>Both scans, read against the paper: 37 products, 585 units, $1080.22.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTwiceScannedTicketReadsAsThePaperPrintsIt(bool firstScan)
    {
        var lines = firstScan ? TwiceScannedTicket1129 : TwiceScannedTicket1119;

        var invoice = DrPepperPdfParser.Parse(lines, [], Paper(("5GBIB DrPep", "1"), ("18z12 Hai CcntMlok", "12"), ("12z12 31m CrspApl", "12")));

        Assert.Equal("5218300220", invoice.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 8, 10), invoice.Date);
        Assert.Equal(1080.22m, invoice.Total);
        Assert.Equal(invoice.Total, invoice.LineTotal);
        Assert.Equal(37, invoice.Lines.Count);
        Assert.Equal(585, invoice.Lines.Sum(l => l.Units));   // the ticket's own TOT SALE count
    }

    /// <summary>
    /// "DATE:. 08/10/26". The scanner drops punctuation next to the label exactly as it does
    /// anywhere else, and this alone refused an otherwise sound invoice.
    /// </summary>
    [Fact]
    public void PunctuationDroppedBesideTheDateLabelDoesNotCostTheInvoice()
    {
        Assert.Contains(TwiceScannedTicket1119,
            l => l.StartsWith("DATE:. 08/10/26", StringComparison.Ordinal));

        Assert.Equal(new DateOnly(2026, 8, 10),
            DrPepperPdfParser.Parse(TwiceScannedTicket1119, []).Date);
    }

    /// <summary>
    /// A period inside the barcode. Both scans print the 5-gallon syrup as "05490.0710545", and
    /// taking the mark out leaves a code that passes its own check digit - which is what says no
    /// digit went with it. On the 11:29 scan that line's UNITS was damaged as well, so no product
    /// claimed the priced row and the whole $115.95 left the invoice in silence.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APeriodInsideTheBarcodeIsInk(bool firstScan)
    {
        var lines = firstScan ? TwiceScannedTicket1129 : TwiceScannedTicket1119;

        var bib = Assert.Single(
            DrPepperPdfParser.Parse(lines, [], Paper(("5GBIB DrPep", "1"), ("18z12 Hai CcntMlok", "12"), ("12z12 31m CrspApl", "12"))).Lines.Where(l => l.Gtin == "054900710545"));

        Assert.True(Upc.IsValidUpcA12(bib.Gtin));
        Assert.Equal(1, bib.Units);
        Assert.Equal(115.95m, bib.ExtendedCost);
    }

    /// <summary>
    /// A letter inside the SKU ("10136S14" for 10136514). The SKU is not written to the XML at all,
    /// so it must not cost the line - while a letter inside the *barcode* is still refused, which
    /// TheBadScanOfTheSameTicket_IsRefused pins.
    /// </summary>
    [Fact]
    public void ALetterInTheUnusedSkuDoesNotCostTheLine()
    {
        Assert.Contains("16z12 SN Frt Pnch 076183003138/10136S14", TwiceScannedTicket1119);

        var line = Assert.Single(
            DrPepperPdfParser.Parse(TwiceScannedTicket1119, [], Paper(("5GBIB DrPep", "1"), ("18z12 Hai CcntMlok", "12"), ("12z12 31m CrspApl", "12"))).Lines.Where(l => l.Gtin == "076183003138"));

        Assert.Equal(12, line.Units);
    }

    /// <summary>
    /// The quantities the money cannot see. Three rows on the 11:29 scan lost their unit count, and
    /// every dollar figure on the page - the row, the package subtotal, the invoice total - is
    /// correct either way, so nothing about the money will ever notice.
    ///
    /// The package subtotal is what **finds** them, and that is all it is allowed to do. It says this
    /// row is wrong; it does not say what the right figure is. Filling the count in from cases x pack
    /// would rest on QTY having been read correctly, and a wrong QTY there produces a quantity that
    /// agrees with every total on the page and is simply not what came off the truck. So each one is
    /// a question with the printed row beside it.
    /// </summary>
    [Fact]
    public void AUnitCountTheMoneyCannotCheckIsAskedAboutRatherThanFilledIn()
    {
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(TwiceScannedTicket1129, [], f =>
        {
            asked.Add(f);
            return f.Description == "5GBIB DrPep" ? "1" : "12";
        });

        // "25.00 1 11 17.00 0.00 17.00" under a "PEG 1 12 17.00" subtotal: one case of twelve.
        Assert.Equal(12, invoice.Lines.Single(l => l.Gtin == "813694026719").Units);
        Assert.Equal(12, invoice.Lines.Single(l => l.Gtin == "842595138214").Units);
        // "115.95 1 1. 11S.95 0.00 115.95" under "PEG 1 1 115.95": one case of one.
        Assert.Equal(1, invoice.Lines.Single(l => l.Gtin == "054900710545").Units);

        Assert.Equal(3, asked.Count);
        Assert.All(asked, f => Assert.Equal(LineField.Units, f.Field));
        Assert.All(asked, f => Assert.True(f.LineNo > 0, "a question must know which row to show"));
        Assert.Equal(
            new[] { "5GBIB DrPep", "18z12 Hai CcntMlok", "12z12 31m CrspApl" },
            asked.Select(f => f.Description).OrderBy(d => d, StringComparer.Ordinal)
                .OrderBy(d => Array.IndexOf(
                    new[] { "5GBIB DrPep", "18z12 Hai CcntMlok", "12z12 31m CrspApl" }, d)).ToArray());
    }

    /// <summary>
    /// The backstop, for a package subtotal too damaged to settle its own group: the invoice's own
    /// TOT SALE count. Without it a misread quantity ships - the money reconciles to the penny while
    /// SSCS books a unit that never came off the truck.
    /// </summary>
    [Fact]
    public void AQuantityNoSubtotalCanSettleIsCaughtByTheTicketsOwnUnitCount()
    {
        string[] lines =
        [
            "INVOICE", "5218300220", "DATE: 08/10/26 TIME: 11:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "18 OZ PLASTIC BTL LS 12",
            "18z12 Hai CcntMlok 813694026719/10159894",
            "25.00 1 11 17.00 0.00 17.00",   // 11 for 12 - every dollar on the page still agrees
            "PEG 1 1Z 17.00",                // the subtotal that would have caught it is damaged too
            "TOT SALE 1 12 17.00",
            "AMOUNT DUE $17.00",
        ];

        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.QtyMismatch, ex.ReasonCode);
        Assert.Contains("11 units", ex.Message, StringComparison.Ordinal);
        Assert.Contains("12", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The last resort, and the reason this is a prompt rather than a refusal: a UNITS column the
    /// scan destroyed on an invoice printing nothing able to settle it. The operator is asked for
    /// that one number, with the row named the way they will find it on the paper.
    /// </summary>
    [Fact]
    public void AUnitCountNothingOnThePageCanSettleIsAskedFor()
    {
        var warnings = new List<ConversionWarning>();
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(UnsettleableUnits, warnings, f => { asked.Add(f); return "1"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Units, q.Field);
        Assert.Equal("1.", q.PrintedValue);              // what the scan made of it
        Assert.Equal("5GBIB DrPep", q.Description);      // named, so nobody counts down the page
        Assert.Equal("054900710545", q.Gtin);
        Assert.Equal(1, q.Position);
        Assert.Equal(1, q.LineCount);

        Assert.Equal(1, Assert.Single(invoice.Lines).Units);
        Assert.Contains(warnings, w => w.Message.Contains("You typed 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// With no prompt to ask - a headless caller, or the operator cancelling - the same invoice is
    /// refused rather than shipped with a made-up quantity, and the message names the row.
    /// </summary>
    [Fact]
    public void WithNoPromptTheUnreadableUnitCountRefusesInsteadOfGuessing()
    {
        var ex = Assert.Throws<EdiConversionException>(() => DrPepperPdfParser.Parse(UnsettleableUnits, []));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
        Assert.Contains("5GBIB DrPep", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A damaged UNITS column with no subtotal and no TOT SALE anywhere to settle it.</summary>
    private static readonly string[] UnsettleableUnits =
    [
        "INVOICE", "5218300220", "DATE: 08/10/26 TIME: 11:29 AM",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "5GBIB DrPep 054900710545/10000703",
        "115.95 1 1. 115.95 0.00 115.95",
        "AMOUNT DUE $115.95",
    ];

    /// <summary>
    /// The guard on reading a period inside a code token: a priced row must never pose as a product
    /// line. A period is also the decimal separator, so a pattern that admitted one carelessly would
    /// read "0.00 / 25.07" as a barcode and book a line the truck never delivered. Every barcode on
    /// a marked-up invoice is still a whole twelve digits.
    /// </summary>
    [Fact]
    public void APricedRowIsNeverReadAsAProductLine()
    {
        var invoice = DrPepperPdfParser.Parse(PenMarkedInvoiceLines, []);

        Assert.Equal(33, invoice.Lines.Count);
        Assert.All(invoice.Lines, l => Assert.Equal(12, l.Gtin.Length));
    }

    // -----------------------------------------------------------------------------------------
    // Everything else a scan can destroy. Each of these used to refuse the whole invoice; each is
    // now a question, and each answer is still checked by whatever the page can check it against.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A mark that took a digit out of the barcode. The code no longer passes its check digit, so
    /// something went with the mark and there is nothing to recover by arithmetic. Nothing else on
    /// the page can catch it either — the money reconciles whatever code goes out — so this is the
    /// one figure that is asked for outright.
    /// </summary>
    [Fact]
    public void ABarcodeAMarkDestroyedIsAskedFor()
    {
        string[] lines =
        [
            "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 8:29 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "1L15 BigRed 07181`7000451/10000069",   // a digit lost under the mark
            "34.00 1 15 28.12 0.00 28.12",
            "Amount Due $28.12",
        ];
        var warnings = new List<ConversionWarning>();
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings, f => { asked.Add(f); return "071817000453"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, q.Field);
        Assert.Equal("1L15 BigRed", q.Description);
        Assert.Equal(15, q.Units);                       // context, so they know which row
        Assert.Equal("07181`7000451", q.PrintedValue);   // what the scan made of it

        Assert.Equal("071817000453", Assert.Single(invoice.Lines).Gtin);
        Assert.Contains(warnings, w => w.Message.Contains("You typed 071817000453", StringComparison.Ordinal));
    }

    /// <summary>
    /// The dangerous one: a stroke through the barcode leaves a priced row that no product line
    /// claims. The money still adds up to AMOUNT DUE either way, so the invoice would convert clean
    /// with an item missing or on the wrong PLU. It is asked about instead, named by the text that
    /// did survive on the line above.
    /// </summary>
    [Fact]
    public void APricedRowWhoseProductLineDidNotRead_IsAskedAbout()
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct Mng 887725001365/20035832",
            "38.00 1 12 25.07 0.00 25.07",
            "21z12 Elct StbEw 887725000252|20035823",   // the stroke swallowed the separator
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $50.14",                        // reconciles either way
        ];
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(lines, [], f => { asked.Add(f); return "887725000252"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, q.Field);
        Assert.Contains("21z12 Elct StbEw", q.Description, StringComparison.Ordinal);

        Assert.Equal(2, invoice.Lines.Count);
        Assert.Equal(50.14m, invoice.LineTotal);
        Assert.Contains(invoice.Lines, l => l.Gtin == "887725000252");
    }

    /// <summary>
    /// A mark through the AMOUNT column is asked for, not worked out.
    ///
    /// AMOUNT DUE minus the other rows would give it exactly, and that is the reason not to: the
    /// sum-equals-total check is the only independent thing this invoice has to say about itself,
    /// and a row back-filled from the total passes it by construction. See the test below.
    /// </summary>
    [Fact]
    public void AMarkThroughTheAmountIsAskedFor()
    {
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(DamagedAmountLines, [], f => { asked.Add(f); return "25.07"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Amount, q.Field);
        Assert.Equal("25/07", q.PrintedValue);
        Assert.Equal("21z12 Elct StbEw", q.Description);
        Assert.Equal(12, q.Units);

        Assert.Equal(25.07m, Assert.Single(invoice.Lines).ExtendedCost);
    }

    /// <summary>
    /// The point of asking rather than deriving: a figure typed wrong is still caught. Had the
    /// amount been back-filled from AMOUNT DUE it would have balanced to the penny no matter what
    /// was really on the paper, and the invoice would have lost its only checksum.
    /// </summary>
    [Fact]
    public void AnAmountTypedWrongIsStillCaughtByTheInvoiceTotal()
    {
        var ex = Assert.Throws<EdiConversionException>(
            () => DrPepperPdfParser.Parse(DamagedAmountLines, [], _ => "99.99"));

        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    private static readonly string[] DamagedAmountLines =
    [
        "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "21z12 Elct StbEw 887725000252/20035823",
        "38.00 1 12 25.07 0.00 25/07",
        "AMOUNT DUE $25.07",
    ];

    /// <summary>
    /// The header fields, when the scan destroyed all three. Nothing on the page carries them twice,
    /// so they come from the operator — but the line items still have to add up to the total that
    /// was typed, which is what keeps a mistyped one from going out.
    /// </summary>
    [Fact]
    public void HeaderFieldsTheScanDestroyedCanBeSupplied()
    {
        string[] lines =
        [
            "1NV01CE", "S2183OO22O",           // the banner and the number both gone
            "DATE. 08!10!26",                  // and the date
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $2S.O7",               // and the total
        ];

        var invoice = DrPepperPdfParser.Parse(
            lines, [], null, new DateOnly(2026, 8, 10), "5218300220", 25.07m);

        Assert.Equal("5218300220", invoice.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 8, 10), invoice.Date);
        Assert.Equal(25.07m, invoice.Total);
        Assert.Equal(invoice.Total, invoice.LineTotal);
    }

    /// <summary>
    /// The **date** the operator confirms overrides the printed one; the **total** does not.
    ///
    /// They are treated differently because they are checked differently. AMOUNT DUE is re-added by
    /// every package subtotal on the page, so it catches its own damage — a supplied one is only a
    /// fallback for a scan that destroyed it. A date is printed once, checks nothing, and a wrong
    /// year looks entirely plausible: one sample books itself into 2016 for 2026. So it is typed on
    /// every invoice, with the scan's reading offered in the box.
    /// </summary>
    [Fact]
    public void TheTypedDateOverridesThePrintedOneButTheTotalDoesNot()
    {
        var invoice = DrPepperPdfParser.Parse(
            Example7Lines, [], null, new DateOnly(2026, 4, 21), null, 1.00m);

        Assert.Equal(new DateOnly(2026, 4, 21), invoice.Date);   // typed wins
        Assert.Equal(532.43m, invoice.Total);                    // printed wins
    }

    /// <summary>
    /// The invoice number is the opposite, and deliberately so: what the operator types **wins over
    /// what the scan read**. It is the one field on the page nothing can check — no check digit, no
    /// total to reconcile against, one printed copy — and this scanner does flip digits in short
    /// numeric fields, reading the same ticket's printed TIME as 11:29 on one scan and 11:19 on
    /// another. So it is read off the paper by a person, as every other PDF vendor already does.
    /// </summary>
    [Fact]
    public void TheTypedInvoiceNumberWinsOverTheScannedOne()
    {
        var invoice = DrPepperPdfParser.Parse(Example7Lines, [], null, null, "112425112379D");

        Assert.Equal("112425112379D", invoice.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 4, 20), invoice.Date);   // still the ticket's own
        Assert.Equal(532.43m, invoice.Total);
    }

    /// <summary>Blank or whitespace is not an answer — the scan's own reading stands in.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTypedNumberFallsBackToWhatTheTicketPrinted(string? typed)
    {
        Assert.Equal("4643517800",
            DrPepperPdfParser.Parse(Example7Lines, [], null, null, typed).InvoiceNumber);
    }

    /// <summary>
    /// Invoice **4643519283**, 08/10/26 - the ticket that arrived with returns on it, and the first
    /// of its layout the converter ever saw. A delivery carrying credits prints no TOT SALE line at
    /// all; it closes with a SALES SUMMARY block instead, and its CREDITS section uses a shorter set
    /// of columns.
    ///
    /// Three separate pieces of scanner damage on one page, all of them pinned by the tests below:
    ///
    ///  * a row that came through **missing three columns** - "20.66 0.00 20.66" where the paper
    ///    reads "35.90 1 12 20.66 0.00 20.66";
    ///  * a **mark welded to a dollar figure**, "20.66-";
    ///  * a **description letter read as a digit** and run into the barcode below it,
    ///    "16z12C4PrfSourGumSp1842595136050" for ...Spl 842595136050.
    /// </summary>
    private static readonly string[] CreditTicket4643519283 =
    [
        "Dr Pepper Bottling Company of Texas",
        "10909 Green Crossing BLVD Apt/Suite 100",
        "HOUSTON, TX 77038",
        "(713) 799-1024",
        "DELIVERY ROUTE: 120120616",
        "DRIVER: 658482-BRITANY",
        "CARRIER: SPLASH TRANSPORT, INC",
        "INVOICE",
        "4643`519282",
        "4643519283",
        "DATE: 08/10/26 TIME: 11:20 AM",
        "BEST STOP 000014",
        "17828 FM 1314 RD",
        "CONROE, TX 77302-5904",
        "ACCOUNT # 13268486",
        "STORE # 000014",
        "SALES",
        "DESCRIPTION UPC/SKU",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "12 OZ ALUM CAN 12PK X 2",
        "12z12p BigRed 071817033291/10000862",
        "24.00 1 2 12.75 0.00 12.75",
        "12z12p Zsgr DP 078000035261/10136728",
        "24.00 1 2 12.75 0.00 12.75",
        "PEG 2 4 25.50",
        "120Z/355ML ALUM CAN SLEEK LS 1",
        "12z12 Blm CrspApl 842595138214/20040249",
        "31.00 1 12 20.00 0.00 20.00",
        "12z12 Blm PearScr 842595141467/20042078",
        "31.00 1 12 20.00 0.00 20.00",
        "12z12 Blm StbWtml 842595135091/20038850",
        "31.00 1 12 20.00 0.00 20.00",
        "PEG 3 36 60.00",
        "16 OZ ALUM CAN LS 12 PAL160",
        "16z12 C4Prf JR GrApl842595134971/20042066",
        "20.66 0.00 20.66",
        "16z12V C4Prf JR WtMln 842595134964/20042067",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfCsmcRbw 842595121766/20034450",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4PrfFrznBmscl 842595106596/20034426",
        "35.90 1 12 20.66 0.00 20.66-",
        "16z12C4PrfHawPnc 842595133608/20037607",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4Prfm5tbrBlst 842595121759/20034425",
        "35.90 1 12 20.66 0.00 20.66",
        "16z12C4U1tm ArcSnwCn 842595106619/20034437",
        "36.00 1 12 20.66 0.00 20.66",
        "16z12C4Ultm FP 842595121650/20034440",
        "36.00 1 12 20.66 0.00 20.66",
        "16z121 Brcc W1dFrst 810140157621/10170028",
        "31.00 1 12 23.03 0.00 23.03",
        "PEG 9 108 188.31",
        "16 OZ PLASTIC BTL LS 12",
        "16z12 SN Apple 076183003282/10136538",
        "23.00 1 12 15.00 0.00 15.00",
        "16z'12 SN Frt Pnch 076183003138/10136514",
        "23.00 1 12 15.00 0.00 15.00",
        "16z12 SN KwStrwbry 076183003107/10136515",
        "23.00 1 12 15.00 0.00 15.00",
        "PEG 3 36 45.00",
        "20 OZ PLASTIC BTL LS 24",
        "20z24 DrPep 078000082401/10001195",
        "56.00 5 120 36.14 0.00 180.70",
        "20z24 Squirt 078000016406/10001120",
        "56.00 1 24 36.14 0.00 36.14",
        "PEG 6 144 216.84",
        "20.280Z/600ML PLASTIC BTL LS 1",
        "600m112 PnFiel Limda078000200287/20020468",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFiel Narjd 078000200270/20020470",
        "22.00 1 12 14.42 0.00 14.42",
        "600m112 PnFielPenMan 078000200256/20020467",
        "22.00 1 12 14.42 0.00 14.42",
        "PKG 3 36 43.26",
        "210Z/621ML POLYPROPYLENE BTL L",
        "21z12 Elct BuRsb 887725001303/20035837",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct Ccn 887725000047/20035818",
        "38.00 2 24 25.07 0.00 50.14",
        "21z12 Elct PP 887725000450/20035831",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct LmnLm 887725000061/20035824",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct Urn 887725000054/20035819",
        "38.00 1 12 25.07 0.00 25.07",
        "21z12 Elct StbKw 887725000252/20035823",
        "38.00 2 24 25.07 0.00 50.14",
        "PEG 8 96 200.56",
        "30.4OZ PLASTIC BTL LS 12",
        "30.4z12 CoreHyd 853004004037/10123487",
        "30.00 5 60 17.00 0.00 85.00",
        "PEG 5 60 85.00",
        "32 OZ PLASTIC BTL LS 12",
        "32z12 SN Apple 076,183328507/10088249",
        "26.00 1 12 23.52 0.00 23.52",
        "PEG 1 12 23.52",
        "1 L PLASTIC BTL LS 15",
        "1L15 Squirt 078000016451/10001322",
        "34.00 1 15 28.12 0.00 28.12",
        "PEG 1 15 28.12",
        "CREDITS",
        "DESCRIPTION UPC/SKU",
        "CASES UNITS NET TAX AMOUNT",
        "16z12C4PrfSourGumSp1842595136050/20040106",
        "0 1 1.72 0.00 (1.72)",
        "16z121 Brcc TgrStrk 840461210620/10175292",
        "0 1 1.91 0.00 (1.91)",
        "20z24 DrPep 078000082401/10001195",
        "0 1 1.50 0.00 (1.50)",
        "SALES SUMMARY",
        "i",
        "Sales Qty Amount",
        "Cases 41 916.11",
        "Units 547",
        "Credits",
        "Cases 0 (5.13)",
        "Units 3",
        "SHELLS 7 0.00",
        "PALLETS 1 0.00",
        "Amount Due $910.98",
        "COD-CASH AND CHECK",
        "RECEIVED BY Best",
        "NOTICE",
        "Sales invoices shall be final and binding one",
        "year from the date of issuance. The Company",
        "will not investigate review or adjust any",
        "invoice which is older than one year.",
        "If your check is returned, it may be re-",
        "presented electronically or by paper draft and",
        "you will be responsible for all collection",
        "costs. A return fee will be charged on any item",
        "that is returned by your bank and the fees may",
        "be collected electronically or by paper draft.",
    ];


    /// <summary>
    /// The whole ticket. Before the fixes below it lost a product line silently - the row underneath
    /// "16z12 C4Prf JR GrApl" had three of its six columns missing, so nothing recognised it as a
    /// priced row, so the product above it was passed over as scenery. The remaining 33 lines still
    /// reconciled against each other perfectly; only AMOUNT DUE noticed, as a flat refusal that could
    /// not say which line had gone.
    /// </summary>
    [Fact]
    public void TheCreditTicketConvertsWholeWithEveryLineOnIt()
    {
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(CreditTicket4643519283, warnings, Paper(("16z12 C4Prf JR GrApl", "12")));

        Assert.Equal(new DateOnly(2026, 8, 10), invoice.Date);
        Assert.Equal(910.98m, invoice.Total);
        Assert.Equal(34, invoice.Lines.Count);                  // 31 sales + 3 credits
        Assert.Equal(544, invoice.Lines.Sum(l => l.Units));     // 547 sold, 3 back on the truck

        // The line that used to disappear, with the figures its package subtotal settled.
        var lost = Assert.Single(invoice.Lines, l => l.Description == "16z12 C4Prf JR GrApl");
        Assert.Equal("842595134971", lost.Gtin);
        Assert.Equal(12, lost.Units);
        Assert.Equal(20.66m, lost.ExtendedCost);

        // And the row whose amount had a pen stroke on it, "20.66-".
        var marked = Assert.Single(invoice.Lines, l => l.Description == "16z12C4PrfFrznBmscl");
        Assert.Equal(20.66m, marked.ExtendedCost);

        Assert.All(invoice.Lines, l => Assert.Equal(12, l.Gtin.Length));
    }

    /// <summary>
    /// The recovered line is **asked about**, not filled in.
    ///
    /// The package subtotal has done its whole job the moment it disagrees: it says this row is
    /// wrong, and it does not say what the right figure is. Working the count out from cases x pack
    /// rests on the other columns having been read correctly, and a wrong QTY there gives a quantity
    /// that agrees with every total on the page and is simply not what came off the truck. So the
    /// subtotal names the row and the operator reads 12 off the picture.
    /// </summary>
    [Fact]
    public void TheRecoveredLineIsAskedAboutRatherThanFilledIn()
    {
        var asked = new List<DrPepperUnknownField>();

        DrPepperPdfParser.Parse(CreditTicket4643519283, [], f => { asked.Add(f); return "12"; });

        var question = Assert.Single(asked);
        Assert.Equal("16z12 C4Prf JR GrApl", question.Description);
        Assert.Equal(LineField.Units, question.Field);
        Assert.Equal("", question.PrintedValue);        // the column never reached the text layer
        Assert.True(question.LineNo > 0, "the question must know which row to show");
    }

    /// <summary>
    /// The recovered figures are the ones the paper actually prints. Repairing the two damaged spots
    /// by hand - putting the row's missing columns back, taking the pen stroke off the amount - has
    /// to land on exactly the same invoice, because a recovery that merely *reconciles* is not the
    /// same thing as a recovery that is *right*.
    ///
    /// The second case moves the damage to a different row in a different package, one of 2 cases of
    /// 12 rather than 1 of 12. It has to work out 24 units there, not 12.
    /// </summary>
    [Theory]
    [InlineData("20.66 0.00 20.66", "35.90 1 12 20.66 0.00 20.66")]              // undamage it
    [InlineData("38.00 2 24 25.07 0.00 50.14", "25.07 0.00 50.14")]              // damage another
    public void RepairingTheScanByHandLandsOnTheSameInvoice(string from, string to)
    {
        string[] lines = [.. CreditTicket4643519283];
        lines[Array.IndexOf(lines, from)] = to;

        // Whichever row carries the damage is the one asked about, and the paper answers it: the
        // 16oz C4 is one case of twelve, the 21oz Elct Ccn two cases of twelve.
        var invoice = DrPepperPdfParser.Parse(lines, [],
            Paper(("16z12 C4Prf JR GrApl", "12"), ("21z12 Elct Ccn", "24")));

        Assert.Equal(34, invoice.Lines.Count);
        Assert.Equal(544, invoice.Lines.Sum(l => l.Units));
        Assert.Equal(910.98m, invoice.Total);
    }

    /// <summary>
    /// A mark that lands on a dollar figure comes off it. This is safe precisely because money is the
    /// best-checked thing on the page - the package subtotal above the row and AMOUNT DUE below it
    /// both re-add it - so a mark rubbed off wrongly fails loudly instead of shipping.
    /// </summary>
    [Theory]
    [InlineData("38.00 1 12 25.07 0.00 25.07-")]    // stroke trailing the AMOUNT
    [InlineData("38.00 1 12 25.07 0.00 |25.07")]    // bar leading it
    [InlineData("38.00 1 12 25.07 0.00 25.07`")]    // a stray quote
    public void AMarkStuckToADollarFigureComesOff(string row)
    {
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(OneLine(row), [], f => { asked.Add(f); return "0"; });

        Assert.Empty(asked);
        Assert.Equal(25.07m, Assert.Single(invoice.Lines).ExtendedCost);
    }

    /// <summary>
    /// A **letter** in a figure is not a mark - it is a mis-read digit standing where the digit was:
    /// "2S.07" is 25.07 with the 5 lost, "1Z" is 12. Rubbing one out would delete the digit and leave
    /// a figure that looks perfectly clean, so a letter is always damage and always asked about.
    /// </summary>
    [Theory]
    [InlineData("38.00 1 12 25.07 0.00 2S.07")]
    [InlineData("38.00 1 12 25.07 0.00 25.O7")]
    public void ALetterInAFigureIsAMisreadDigitAndStaysDamage(string row)
    {
        var asked = new List<DrPepperUnknownField>();

        DrPepperPdfParser.Parse(OneLine(row), [], f => { asked.Add(f); return "25.07"; });

        Assert.Equal(LineField.Amount, Assert.Single(asked).Field);
    }

    /// <summary>
    /// The same mark on a **count** stays damage too, and for the opposite reason: a count is what the
    /// subtotals are made of, so nothing re-checks it. It is settled from a package total where the
    /// invoice prints one, and asked about where it does not.
    /// </summary>
    [Fact]
    public void AMarkStuckToACountIsStillDamage()
    {
        var asked = new List<DrPepperUnknownField>();

        DrPepperPdfParser.Parse(
            OneLine("38.00 1 12- 25.07 0.00 25.07"), [], f => { asked.Add(f); return "12"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Units, q.Field);
        Assert.Equal("12-", q.PrintedValue);
    }

    /// <summary>
    /// A row that lost columns is read from its tail: NET, TAX and AMOUNT are the last three and all
    /// three are money, which is a shape nothing else on the page ends in. That fixes which column is
    /// the AMOUNT however much went missing in front of it, and the package subtotal settles the rest.
    /// </summary>
    [Theory]
    [InlineData("20.66 0.00 20.66")]          // WHSLE, CASES and UNITS all gone - the real one
    [InlineData("1 20.66 0.00 20.66")]        // one lone survivor, which could be either count
    [InlineData("35.90 1 20.66 0.00 20.66")]  // WHSLE and CASES, so UNITS is what went
    public void ARowThatLostAColumnKeepsItsProductLineAndSettlesFromThePackage(string damaged)
    {
        string[] lines =
        [
            "INVOICE", "4643519283", "DATE: 08/10/26 TIME: 11:20 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "16 OZ ALUM CAN LS 12 PAL160",
            "16z12 C4Prf JR GrApl 842595134971/20042066",
            damaged,
            "16z12V C4Prf JR WtMln 842595134964/20042067",
            "35.90 1 12 20.66 0.00 20.66",
            "PEG 2 24 41.32",
            "AMOUNT DUE $41.32",
        ];

        var asked = new List<DrPepperUnknownField>();
        var invoice = DrPepperPdfParser.Parse(lines, [], f => { asked.Add(f); return "12"; });

        Assert.Equal(2, invoice.Lines.Count);
        var recovered = invoice.Lines[0];
        Assert.Equal("842595134971", recovered.Gtin);
        Assert.Equal(20.66m, recovered.ExtendedCost);   // read off the row's own tail

        // The count is the one thing the tail cannot give back, so it is asked for - the package
        // total says this row is wrong without saying what it should be.
        var question = Assert.Single(asked);
        Assert.Equal(LineField.Units, question.Field);
        Assert.Equal("16z12 C4Prf JR GrApl", question.Description);
        Assert.Equal(12, recovered.Units);
    }

    /// <summary>
    /// With no package total to settle it, the quantity on such a row is asked for rather than
    /// invented. The AMOUNT is still read - it survived on the row - so only the one figure nothing
    /// on the page can supply becomes a question.
    /// </summary>
    [Fact]
    public void ARowThatLostAColumnWithNoSubtotalAsksForTheQuantity()
    {
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(
            OneLine("25.07 0.00 25.07"), [], f => { asked.Add(f); return "12"; });

        var q = Assert.Single(asked);
        Assert.Equal(LineField.Units, q.Field);
        Assert.Equal("21z12 Elct StbEw", q.Description);
        Assert.Equal(12, Assert.Single(invoice.Lines).Units);
        Assert.Equal(25.07m, Assert.Single(invoice.Lines).ExtendedCost);
    }

    /// <summary>
    /// Reading short rows must not turn scenery into products. Package headers and subtotals sit
    /// among the priced rows and carry digits of their own; none of them ends in three money columns,
    /// which is what keeps them out.
    /// </summary>
    [Theory]
    [InlineData("16 OZ ALUM CAN LS 12 PAL160")]
    [InlineData("12 OZ ALUM CAN 12PK X 2")]
    [InlineData("20.280Z/600ML PLASTIC BTL LS 1")]
    [InlineData("PEG 9 108 188.31")]
    [InlineData("SHELLS 7 0.00")]
    [InlineData("Cases 41 916.11")]
    public void ALineThatIsNotAPricedRowIsNeverReadAsOne(string scenery)
    {
        string[] lines =
        [
            "INVOICE", "4643519283", "DATE: 08/10/26 TIME: 11:20 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            scenery,
            "21z12 Elct StbEw 887725000252/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $25.07",
        ];

        var invoice = DrPepperPdfParser.Parse(lines, []);

        Assert.Equal("887725000252", Assert.Single(invoice.Lines).Gtin);
    }

    /// <summary>
    /// The SALES SUMMARY block is this layout's TOT SALE, and it is checked the same way: a quantity
    /// misread anywhere on the page leaves every dollar figure correct, so the money can never catch
    /// it and the invoice's own count of itself is the only thing that can.
    /// </summary>
    [Fact]
    public void TheSalesSummaryStandsInForTotSaleOnACreditTicket()
    {
        string[] lines = [.. CreditTicket4643519283];
        lines[Array.IndexOf(lines, "Units 547")] = "Units 548";

        var ex = Assert.Throws<EdiConversionException>(
            () => DrPepperPdfParser.Parse(lines, [], Paper(("16z12 C4Prf JR GrApl", "12"))));

        Assert.Equal(PdfReasonCodes.QtyMismatch, ex.ReasonCode);
        Assert.Contains("547 units", ex.Message, StringComparison.Ordinal);
        Assert.Contains("548", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Credits print no subtotal of their own, so that block is the only line on the page that counts
    /// the returns at all. A disagreement is said out loud rather than refused: the money nets out
    /// against AMOUNT DUE either way, and a handful of units going back on a delivery of hundreds is
    /// worth a person's eyes on the paper, not worth sending the whole invoice away.
    /// </summary>
    [Fact]
    public void AReturnedQuantityThatDisagreesWithTheSummaryIsReported()
    {
        string[] lines = [.. CreditTicket4643519283];
        lines[Array.IndexOf(lines, "Units 3")] = "Units 4";
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings, Paper(("16z12 C4Prf JR GrApl", "12")));

        Assert.Equal(910.98m, invoice.Total);        // still converts
        var w = Assert.Single(warnings, x => x.Message.Contains("credit lines add up", StringComparison.Ordinal));
        Assert.Contains("3 units", w.Message, StringComparison.Ordinal);
        Assert.Contains("summary says 4", w.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A description whose last letter scanned as a digit welds it onto the barcode below:
    /// "...SourGumSpl 842595136050" came through as "...SourGumSp1842595136050". Thirteen digits is a
    /// length the code checks accept, so this would have shipped a barcode that is not the item.
    ///
    /// The check digit is what gives the digit back, and both halves of the test are needed: thirteen
    /// digits that **fail** as a GTIN-13 whose last twelve **pass** as a UPC-A.
    /// </summary>
    [Fact]
    public void ADescriptionDigitWeldedOntoABarcodeIsGivenBack()
    {
        var invoice = DrPepperPdfParser.Parse(CreditTicket4643519283, [], Paper(("16z12 C4Prf JR GrApl", "12")));

        var credit = Assert.Single(
            invoice.Lines, l => l.Description.StartsWith("16z12C4PrfSourGum", StringComparison.Ordinal));
        Assert.Equal("842595136050", credit.Gtin);
        Assert.True(Upc.IsValidUpcA12(credit.Gtin));
        Assert.Equal(-1, credit.Units);
    }

    /// <summary>
    /// A returned line stays negative however the operator types its amount.
    ///
    /// The prompt shows the paper, and the paper prints a credit in brackets — "($4.45)". Somebody
    /// will type the brackets, somebody will type a minus, most will type the digits. The sign is
    /// never taken from what was typed: it comes from the row being under the CREDITS banner, so all
    /// three land on the same negative line. Typing a minus on a *sales* row cannot make it negative
    /// either, which is the same guarantee from the other side.
    /// </summary>
    [Theory]
    [InlineData("1.72")]
    [InlineData("-1.72")]
    [InlineData("(1.72)")]
    [InlineData("($1.72)")]
    [InlineData("$1.72")]
    public void ATypedCreditGoesInNegativeWhicheverWayItIsWritten(string typed)
    {
        string[] lines = [.. CreditTicket4643519283];
        lines[Array.IndexOf(lines, "0 1 1.72 0.00 (1.72)")] = "0 1 1.72 0.00 (1.7Z)";   // damage it

        var invoice = DrPepperPdfParser.Parse(lines, [], f => f.Field == LineField.Amount
            ? typed
            : "12");

        var credit = Assert.Single(
            invoice.Lines, l => l.Description.StartsWith("16z12C4PrfSourGum", StringComparison.Ordinal));
        Assert.Equal(-1, credit.Units);
        Assert.Equal(-1.72m, credit.ExtendedCost);
        Assert.True(credit.UnitCost > 0m, "a return is a negative quantity at a positive cost");
    }

    /// <summary>A genuine 13-digit code passes its own check digit and is left exactly as printed.</summary>
    [Fact]
    public void ARealThirteenDigitBarcodeIsNotTrimmed()
    {
        string[] lines =
        [
            "INVOICE", "4643519283", "DATE: 08/10/26 TIME: 11:20 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 4006381333931/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $25.07",
        ];

        Assert.Equal("4006381333931", Assert.Single(DrPepperPdfParser.Parse(lines, []).Lines).Gtin);
    }

    /// <summary>
    /// The invoice prints its code as "&lt;barcode&gt;/&lt;SKU&gt;", so an operator who types the whole
    /// printed token has read the paper correctly. Where the scan mangled the code badly enough that
    /// the picture can only mark the whole row, copying both halves is the obvious thing to do — and
    /// the slash says exactly where the barcode ends, so it is taken rather than refused.
    /// </summary>
    [Theory]
    [InlineData("887725000252/20035823")]
    [InlineData("887725000252 / 20035823")]
    [InlineData("887725000252")]
    public void ABarcodeTypedWithTheSkuStillOnItIsAccepted(string typed)
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725,000253/20035823",   // a mark took a digit; the check digit fails
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $25.07",
        ];
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(lines, [], f => { asked.Add(f); return typed; });

        Assert.Equal(LineField.Barcode, Assert.Single(asked).Field);
        Assert.Equal("887725000252", Assert.Single(invoice.Lines).Gtin);
    }

    /// <summary>
    /// Only the half in front of the slash is ever taken as the barcode. Anything that amounts to
    /// typing the SKU is refused rather than quietly used: the SKU is a real number that would look
    /// like a plausible item code, and nothing downstream would notice a line booked under one.
    /// </summary>
    [Theory]
    [InlineData("20035823")]                    // the SKU on its own
    [InlineData("/20035823")]                   // the slash and everything after it
    [InlineData("20035823/887725000252")]       // the two halves the wrong way round
    [InlineData("88772500025220035823")]        // both halves run together, slash dropped
    public void AnythingThatAmountsToTypingTheSkuIsRefused(string typed)
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725,000253/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "AMOUNT DUE $25.07",
        ];

        var ex = Assert.Throws<EdiConversionException>(
            () => DrPepperPdfParser.Parse(lines, [], _ => typed));

        Assert.Equal(PdfReasonCodes.UnreadableLine, ex.ReasonCode);
    }

    /// <summary>
    /// A figure the scan destroyed on a **credit** row is asked about, the same as on a sales row.
    ///
    /// It used to refuse the whole invoice. Returns print no subtotal beneath them, so there is
    /// nothing on the page to settle a damaged figure against — but that is an argument for asking,
    /// not for sending a delivery back over one character. The count comes back negative, because
    /// SSCS takes returns as negative quantities at a positive cost.
    /// </summary>
    [Theory]
    [InlineData("0 1Z 6.37 0.00 (6.37)", LineField.Units, "1", -1, -6.37)]      // UNITS damaged
    [InlineData("0 1 6.37 0.00 (6.3Z)", LineField.Amount, "6.37", -1, -6.37)]   // AMOUNT damaged
    [InlineData("6.37 0.00 (6.37)", LineField.Units, "1", -1, -6.37)]           // columns lost
    public void ADamagedCreditRowIsAskedAboutRatherThanRefused(
        string creditRow, LineField expected, string answer, int units, decimal extended)
    {
        string[] lines = CreditInvoice(creditRow);
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(lines, [], f => { asked.Add(f); return answer; });

        Assert.Equal(expected, Assert.Single(asked).Field);

        var credit = Assert.Single(invoice.Lines, l => l.Gtin == "078000035261");
        Assert.Equal(units, credit.Units);
        Assert.Equal(extended, credit.ExtendedCost);
        Assert.Equal(18.70m, invoice.Total);
        Assert.Equal(invoice.Total, invoice.LineTotal);
    }

    /// <summary>With no prompt it still refuses rather than guessing, and names the returned item.</summary>
    [Fact]
    public void WithNoPromptADamagedCreditRowRefusesInsteadOfGuessing()
    {
        var ex = Assert.Throws<EdiConversionException>(
            () => DrPepperPdfParser.Parse(CreditInvoice("0 1Z 6.37 0.00 (6.37)"), []));

        Assert.Contains("12z12p Zsgr DP", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line whose package subtotal did not scan is reported, because otherwise it sits in the file
    /// looking exactly as verified as every other line while nothing has checked its quantity.
    ///
    /// This was the last silent gap: a damaged subtotal raised no question and no note — it simply
    /// stopped being a check. The money cannot stand in for it, since a row misread as 11 for 12
    /// leaves every dollar figure on the page correct.
    /// </summary>
    [Fact]
    public void ALineNoPackageTotalCheckedIsReported()
    {
        string[] lines =
        [
            "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "PEG 1 12 25.07",
            "12z12 Blm CrspApl 842595138214/20040249",
            "31.00 1 12 20.00 0.00 20.00",
            "PEG 1 1Z 20.00",                          // this one did not scan
            "AMOUNT DUE $45.07",
        ];
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings);

        Assert.Equal(24, invoice.Lines.Sum(l => l.Units));   // still converts, nothing is guessed

        var told = Assert.Single(warnings, w => w.Message.Contains("package total", StringComparison.Ordinal));
        Assert.Contains("12z12 Blm CrspApl", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("21z12 Elct StbEw", told.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A subtotal the scan damaged still ends its package, so the damage stays on the line it
    /// happened to.
    ///
    /// Without that, the rows above it merge into the next package — whose printed counts describe
    /// only half of them — and that package then cannot reconcile, so the operator is asked about
    /// lines nothing was ever wrong with. One scanner slip on invoice 4643510004, "PEG L 15 28.12"
    /// where the case count came through as a letter, put two clean rows into question.
    /// </summary>
    [Fact]
    public void ADamagedSubtotalStillEndsItsPackage()
    {
        string[] lines =
        [
            "INVOICE", "4643510004", "DATE: 06/01/26 TIME: 10:00 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            "PEG L 12 25.07",                          // the case count scanned as a letter
            "12z12 Blm CrspApl 842595138214/20040249",
            "31.00 1 12 20.00 0.00 20.00",
            "PEG 1 12 20.00",
            "TOT SALE 2 24 45.07",
            "AMOUNT DUE $45.07",
        ];
        var warnings = new List<ConversionWarning>();
        var asked = new List<DrPepperUnknownField>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings, f => { asked.Add(f); return "12"; });

        Assert.Empty(asked);                                  // nothing clean is put into question
        Assert.Equal(24, invoice.Lines.Sum(l => l.Units));

        // Only the line the damaged subtotal would have checked is reported.
        var told = Assert.Single(warnings, w => w.Message.Contains("package total", StringComparison.Ordinal));
        Assert.Contains("21z12 Elct StbEw", told.Message, StringComparison.Ordinal);
    }

    /// <summary>Equipment lines share a subtotal's shape and must not be mistaken for one.</summary>
    [Theory]
    [InlineData("SHELLS 7 0.00")]
    [InlineData("PALLETS 1 0.00")]
    public void AnEquipmentLineIsNotMistakenForADamagedSubtotal(string equipment)
    {
        string[] lines =
        [
            "INVOICE", "4643510004", "DATE: 06/01/26 TIME: 10:00 AM",
            "WHSLE CASES UNITS NET TAX AMOUNT",
            "21z12 Elct StbEw 887725000252/20035823",
            "38.00 1 12 25.07 0.00 25.07",
            equipment,                                 // sits before the subtotal on a credit ticket
            "PEG 1 12 25.07",
            "AMOUNT DUE $25.07",
        ];
        var warnings = new List<ConversionWarning>();

        var invoice = DrPepperPdfParser.Parse(lines, warnings);

        Assert.Equal(12, Assert.Single(invoice.Lines).Units);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("package total", StringComparison.Ordinal));
    }

    /// <summary>An invoice printing no package totals at all has no missing one to point at.</summary>
    [Fact]
    public void AnInvoiceWithNoPackageTotalsIsNotNaggedAboutEveryLine()
    {
        var warnings = new List<ConversionWarning>();

        DrPepperPdfParser.Parse(OneLine("38.00 1 12 25.07 0.00 25.07"), warnings);

        Assert.DoesNotContain(warnings, w => w.Message.Contains("package total", StringComparison.Ordinal));
    }

    /// <summary>A sale, a return, and a package subtotal over the sale — the shape of a credit ticket.</summary>
    private static string[] CreditInvoice(string creditRow) =>
    [
        "INVOICE", "4646105204", "DATE: 07/24/26 TIME: 08:04 AM",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "21z12 Elct StbEw 887725000252/20035823",
        "38.00 1 12 25.07 0.00 25.07",
        "PEG 1 12 25.07",
        "CREDITS",
        "DESCRIPTION UPC/SKU",
        "CASES UNITS NET TAX AMOUNT",
        "12z12p Zsgr DP 078000035261/10136728",
        creditRow,
        "AMOUNT DUE $18.70",
    ];

    /// <summary>One product on an otherwise bare invoice, for pinning a single row's behaviour.</summary>
    /// <summary>
    /// Answers a prompt with what the paper prints, and fails loudly on any question the test did
    /// not expect.
    ///
    /// Every figure the converter cannot read off the page is now asked about rather than worked out
    /// from the columns around it, so a test that exercises a damaged row has to say what the paper
    /// says. Throwing on an unlisted question is the point: a change that starts asking something new
    /// cannot slip through by having it quietly answered with something plausible.
    /// </summary>
    private static AskDrPepperField Paper(params (string Description, string Answer)[] answers) =>
        field =>
        {
            foreach (var (description, answer) in answers)
                if (description == field.Description) return answer;
            throw new Xunit.Sdk.XunitException(
                $"unexpected question: {field.Field} for \"{field.Description}\" "
                + $"(the scan read \"{field.PrintedValue}\")");
        };

    private static string[] OneLine(string pricedRow) =>
    [
        "INVOICE", "4646600671", "DATE: 07/27/26 TIME: 08:04 AM",
        "WHSLE CASES UNITS NET TAX AMOUNT",
        "21z12 Elct StbEw 887725000252/20035823",
        pricedRow,
        "AMOUNT DUE $25.07",
    ];
}
