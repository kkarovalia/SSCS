using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Blue Bell regression tests for three tickets supplied on 2026-07-26, after the first six.
/// Each one broke the parser in a different way, and each fix is pinned here.
///
/// As always the fixtures are PdfPig's real output, scanner damage included.
/// </summary>
public class BlueBellLaterTicketsTests
{
﻿    private static readonly string[] Invoice626155Lines =
    [
        "Blue Bell Creameries, L.P.",
        "/ Geneal Offtces/- Brenham; Texas",
        "DATE 06-11-2026 ((TTIIMMEE ' 1411 /BRANCH# 33`l~ROUiE# 10 TRUCK# 1404",
        "~(u",
        "SHIP TO #",
        "369260`' I^wvvOI`E#03 3101626155",
        "BEST STOP-'t4 '",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "**** ****",
        "Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0789 007189903731 1/2 CNC",
        "1.00 4.00 6.85 27.40",
        "0915 007189905129 PT GREAT_ DIVIDE",
        "1.0010.00 2.98 29.80",
        "0919 007189905113/ /PT BUT PEC JL 1 \\",
        "J 1.00J \\10.00 2.98 29.80",
        "J",
        "I",
        "0921 007189905117 ET CNC —++",
        "1.00, 10.00 2.98 29.80",
        "0923 007189905 120 PT i DUL CHOC",
        "~ T r",
        "1.00 10.00 2.98 29.80",
        "0926 007189905128 PT STRAW",
        "1.00 10.00 2.98 29.80",
        "8001 007189905116 PT COK 2 STP",
        "1.00 10.00 2.98 29.80",
        "0020 007189906120 PT FEATURED FLAVOR",
        "1.00 10.00 2.98 29.80",
        "0122 007189921133 02 302 HMV CUPS",
        "1.00 2.00 6.36 12.72",
        "TOT '/ r ----- . OOs 248.72",
        "BALA INCE DU~E~ F~I'+ 248.72",
        "c~",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Blue,,BI Creameries, L.P.",
        "DATE 06-11-2026 TINE 14:09 BRANCH#9 33, ROUTE# 10 TRUCK# 1404",
        "PICK LIST k",
        "_r 1~ c L",
        "PROD DE5CRIP,TION -5EL•LBOX QTY",
        "789 1/2=CNL cy `' ~~ 1~ 00 4",
        "II 1111111I1I 1111111 II II IIIII II",
        "071899037316",
        "915 PT GREATD IVIDE 1,00 10",
        "1111111111I1I1II I 111111111I1I 1",
        "071899051299",
        "919 PT BIJT PEC 1.00 10",
        "11111111111111111lI11I1I1II1I1",
        "071899051138",
        "921 PT CNC 1,00 10",
        "11111111I1I1 1 1111111111 11111II",
        "071899051176",
        "923 PT DUT CHOC__, 1.00 10",
        "IIIIIIIIIIiIIIIIIIIIIIIIIHHl .",
        "/071899051206 A",
        "926 PT/STRAW s \\1,00 10",
        "11111111111111111111IIIIIIIIIf r",
        "071899051282 ,~f\"",
        "1",
        "8001 ~PT--CO2K= STP 1,00 10",
        "ICllllllllill IIIIIIIIIIIIICIIII ,' 1 I®",
        "071899051169",
        "20 PT- FEATURE-D FLAVO—R ' 1;@0 10",
        "II 1111111I1I 111111111111111111",
        "071899061205",
        "122 D2 302 HNV CUPS 1.00 2",
        "II IIJII11111I991111111II1 I1I1I II",
        "TOTAL QUANTITY 9.00 76",
    ];

    private static readonly string[] Invoice766252Lines =
    [
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 06-25-2026 TIME 13:57 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP T° # INVOICE#",
        "369260 033101766252",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "0",
        "(~",
        "PROD UPC DESCRIPTION",
        "(",
        "EA/COST . IRETAIL L)I 8X .Ct~J~Q7Yi~ PRICE EXTENDED",
        "0781 007189903733 1/2 DUI CHOC",
        "1.00 4.00 6.85 27.40",
        "0789 007189903731 1/2 CNC",
        "1.00 4.00 6.85 27.40",
        "0921 007189905117 PT CNC",
        "1.00 10.00 2.98 29.80",
        "0953 007189905118 PT COTTON CAN",
        "1.00 10.00 2.98 29.80",
        "8001 007189905116 PT COI( 2 STP",
        "1.00 10.00 2.98 29.80",
        "0020 007189906120 PT~FEATURED-FLAVOR'",
        "1:00 : 10.001 2.98 29.80",
        "/,/~,",
        "0122 007189921133 DZ3OZ~HMICV PS \\(",
        ": 14",
        "1. 00 2,00 — 6.36 12.72",
        "rn",
        "015100718992112 ZVV_Ai1LTh",
        "1",
        "XN1 0~ ~00—' ® 6.84 13.68",
        "0165 007189921123 DZ CNC SAND",
        "1.00 2.00 6.84 13.68",
        "0121 007189921150 DZ KRUNCH BAR",
        "1.00 3.00 6.84 20.52",
        "TOTAL 10.00 57.00 234.60",
        "BALANCE DUE 234.60",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Charge",
    ];

    private static readonly string[] Invoice906349Lines =
    [
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenhaa, Texas",
        "DATE 07-09-2026 TIME 10:14 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO # INVOIC E#",
        "369260 0331019 06349",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "4",
        "****",
        "Sales\\J",
        "f t n -",
        "PROD UPC LADESICR CR P710PL1 `",
        "EA/COST ,RETAIL BX f CNF .QTY1®PRICE EXTENDED",
        "I I 1 1,",
        "0914 007189905123 PT/ MT~CHOCsH",
        "1.00 10.00 2.98 29.80",
        "0919 007189905113 PT BUT PEC",
        "1.00 10.00 2.98 29.80",
        "0924 007189905122 PT HMV",
        "2.00 20.00 2.98 59.60",
        "0928 007189905127 PT ROCKY RD",
        "1.00 10.00 2.98 29.80",
        "0020 007189906120 PT FEATURED FLAVOR",
        "2.00 20.00 2.98 59.60",
        "0165 007189921123 DZ CNGSAND",
        ".1 2.00 6.84 13.68",
        "__ N",
        "v.",
        "// ; \\c",
        "TOTAL kl \\ L` /K L00t72.001~ 222.28",
        "I~ IeTA",
        "BALrANCE1DUE .222!28",
        "l i ~)l i ' 1 — I I",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281=446-1090",
        "Chargelb",
        "I~ 'V",
        "C~",
        "(_`",
        "mja",
    ];

    private static BlueBellSection Only(string[] lines, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return Assert.Single(BlueBellPdfParser.ParseAll(lines, warnings));
    }

    /// <summary>
    /// Invoice 033101626155. Two separate defects on one ticket:
    ///
    ///   "0923 007189905 120 PT i DUL CHOC"   the code is broken across a space (9 digits, not 12)
    ///   "TOT '/ r ----- . OOs 248.72"        the word TOTAL is chewed down to "TOT"
    ///
    /// Before the fix the code fragment was too short to be recognised, the row vanished, and the
    /// TOTAL row could not be found at all, so the ticket was refused outright.
    /// </summary>
    [Fact]
    public void SplitCodeAndMangledTotalWord_StillParse()
    {
        var s = Only(Invoice626155Lines, out var warnings);

        Assert.Equal(new DateOnly(2026, 6, 11), s.Date);
        Assert.Equal(248.72m, s.Total);
        Assert.Equal(9, s.Lines.Count);
        // 98 sellable units: the ticket's own QTY column adds to 76, but its one DZ line is a
        // carton of twelve cups counted as 2, which is 24 units.
        Assert.Equal(98, s.Lines.Sum(l => l.Units));
        Assert.Equal(s.Total, s.Lines.Sum(l => l.ExtendedCost));

        // The broken code is rebuilt from the PROD number, not from the loose "120" on the line.
        var dutChoc = s.Lines.Single(l => l.Gtin == "071899051206");
        Assert.Equal(10, dutChoc.Units);
        Assert.Contains(warnings, w => w.Message.Contains("0923", StringComparison.Ordinal));
    }

    /// <summary>
    /// Invoice 033101626155's second page is a PICK LIST that prints each product's barcode with
    /// the digits underneath — and those are the real, whole UPC-A codes. They confirm, from Blue
    /// Bell's own printing, that restoring the check digit reconstructs the true barcode.
    /// Eight products, eight matches, no SSCS round trip required.
    /// </summary>
    [Theory]
    [InlineData("007189903731", "071899037316")]   // 0789 1/2 CNC
    [InlineData("007189905129", "071899051299")]   // 0915 PT GREAT DIVIDE
    [InlineData("007189905113", "071899051138")]   // 0919 PT BUT PEC
    [InlineData("007189905117", "071899051176")]   // 0921 PT CNC
    [InlineData("007189905120", "071899051206")]   // 0923 PT DUT CHOC
    [InlineData("007189905128", "071899051282")]   // 0926 PT STRAW
    [InlineData("007189905116", "071899051169")]   // 8001 PT COK 2 STP
    [InlineData("007189906120", "071899061205")]   // 0020 PT FEATURED FLAVOR
    public void ReconstructedBarcodeMatchesTheOneBlueBellPrintsOnItsPickList(string printed, string pickListBarcode)
    {
        Assert.Equal(pickListBarcode, BlueBellPdfParser.ToGtin(printed));
        Assert.True(Upc.IsValidUpcA12(pickListBarcode));
        Assert.Equal(printed[1..], Upc.SscsIdFromGtin(pickListBarcode));
    }

    /// <summary>The pick list page itself is a packing slip and must not become a second invoice.</summary>
    [Fact]
    public void PickListPageDoesNotBecomeAnInvoice()
    {
        Assert.Single(BlueBellPdfParser.ParseAll(Invoice626155Lines, []));
    }

    /// <summary>
    /// Invoice 033101766252. The scanner ran a PROD number and its UPC together with no space —
    /// "015100718992112" — so the row was not recognised as a product at all and its 13.68
    /// disappeared. The checksum caught it (220.92 against a printed 234.60) and refused the file,
    /// which was correct but useless; now the row parses.
    /// </summary>
    [Fact]
    public void ProdNumberGluedToItsUpc_StillParses()
    {
        var s = Only(Invoice766252Lines, out var warnings);

        Assert.Equal(new DateOnly(2026, 6, 25), s.Date);
        Assert.Equal(234.60m, s.Total);
        Assert.Equal(10, s.Lines.Count);
        Assert.Equal(156, s.Lines.Sum(l => l.Units));   // QTY column adds to 57; four DZ lines x12
        Assert.Equal(s.Total, s.Lines.Sum(l => l.ExtendedCost));

        var vanillaSand = s.Lines.Single(l => l.Gtin == "071899211228");   // PROD 0151
        Assert.Equal(24, vanillaSand.Units);
        Assert.Equal(0.57m, vanillaSand.UnitCost);
        Assert.Equal(13.68m, vanillaSand.ExtendedCost);
        Assert.Contains(warnings, w => w.Message.Contains("0151", StringComparison.Ordinal));
    }

    /// <summary>
    /// Products first seen on the 2026-07-26 tickets. They scanned cleanly, so they would have
    /// converted with or without the catalogue — the catalogue entries only add the name and the
    /// cross-check. Blue Bell keeps adding flavours, so this is expected to grow.
    /// </summary>
    [Theory]
    [InlineData("007189905118", "071899051183")]   // 0953 PT COTTON CAN
    [InlineData("007189921150", "071899211501")]   // 0121 DZ KRUNCH BAR
    [InlineData("007189905123", "071899051237")]   // 0914 PT MT CHOC CH
    public void FlavoursFirstSeenOnTheLaterTickets(string printed, string expectedGtin)
    {
        Assert.Equal(expectedGtin, BlueBellPdfParser.ToGtin(printed));
        Assert.True(Upc.IsValidUpcA12(expectedGtin));
    }

    /// <summary>Invoice 033101906349 — a clean ticket, carrying the new PT MT CHOC CH.</summary>
    [Fact]
    public void CleanLaterTicketParsesWholly()
    {
        var s = Only(Invoice906349Lines, out var warnings);

        Assert.Equal(new DateOnly(2026, 7, 9), s.Date);
        Assert.Equal(222.28m, s.Total);
        Assert.Equal(6, s.Lines.Count);
        Assert.Equal(94, s.Lines.Sum(l => l.Units));   // QTY column adds to 72; one DZ line x12
        Assert.Equal(s.Total, s.Lines.Sum(l => l.ExtendedCost));
        Assert.Equal("033101906349", s.SuggestedInvoiceNumber);   // spaces inside the number closed up
        Assert.Empty(warnings);

        Assert.Equal("071899051237", s.Lines[0].Gtin);            // 0914 PT MT CHOC CH
        Assert.Equal(20, s.Lines[2].Units);                       // 0924 PT HMV, two boxes
    }

    /// <summary>
    /// Every barcode on all three tickets is a valid UPC-A landing on an 07189-prefixed SSCS id,
    /// and every retail is a non-zero price ending in 9.
    /// </summary>
    [Fact]
    public void AllThreeTicketsProduceSaneBarcodesAndRetails()
    {
        foreach (var lines in new[] { Invoice626155Lines, Invoice766252Lines, Invoice906349Lines })
            foreach (var section in BlueBellPdfParser.ParseAll(lines, []))
                Assert.All(section.Lines, l =>
                {
                    Assert.True(Upc.IsValidUpcA12(l.Gtin), $"'{l.Gtin}' is not a valid UPC-A");
                    Assert.StartsWith("07189", Upc.SscsIdFromGtin(l.Gtin), StringComparison.Ordinal);
                    Assert.True(l.RetailPrice > l.UnitCost);
                    Assert.Equal(9, (int)(l.RetailPrice * 100m) % 10);
                });
    }

    // ------------------------------------------------------------------ the 240 dpi ticket

    /// <summary>
    /// PdfPig's real output for invoice 033103175350 (11-13-2025, $72.32), scanned at 240 dpi.
    /// Three separate things went wrong on this one page: the word DATE came out as "OATS", the
    /// TOTAL row lost its label entirely to the watermark, and BALANCE DUE lost its decimal point.
    /// </summary>
    private static readonly string[] Invoice175350Lines =
    [
        "Blue Bell=CreameriesL.",
        "Ge ner al Offices Brenhan,Texas",
        "e",
        "OATS 11-13-2025 TINE 1131:2` BRAN~H#J 3~ R~V~T-E~# 10 TRUCK# 1404",
        ",",
        "SHIP TO it INVOICE#",
        "369260 033103175350",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "**** Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0925 007189905101 PT GR SUPRE",
        "2.00 X20.00 2.98 59.60",
        "r",
        "0122 007189921133/ DZ13OZ HMV CUPS/i",
        "1.00 00 6.36 12.72",
        "72.32",
        "BALANCE DUE 7232",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS I5: P~0=BO7jj674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER-IS-281.446-.1090",
        "Rye »~~~°",
    ];

    /// <summary>
    /// The QTY on the DZ 3OZ HMV CUPS line scans as a bare "00". It is neither ignored nor taken at
    /// face value: 12.72 / 0.53 gives back the 24 cups that carton holds, and the ticket then
    /// reconciles exactly. Booking 0 units would have posted $12.72 of stock that never arrived.
    /// </summary>
    [Fact]
    public void QuantityThatScannedAsDoubleZero_IsRecoveredNotIgnored()
    {
        var section = Assert.Single(BlueBellPdfParser.ParseAll(Invoice175350Lines, []));

        var cups = section.Lines.Single(l => l.Gtin == BlueBellPdfParser.ToGtin("007189921133"));
        Assert.Equal(24, cups.Units);
        Assert.Equal(0.53m, cups.UnitCost);
        Assert.Equal(12.72m, cups.ExtendedCost);

        Assert.Equal(44, section.Lines.Sum(l => l.Units));
        Assert.Equal(72.32m, section.Lines.Sum(l => l.ExtendedCost));
    }

    /// <summary>The date is found even though the scanner read the word DATE as "OATS".</summary>
    [Fact]
    public void DateIsFoundWhenTheWordDateItselfIsUnreadable()
    {
        var section = Assert.Single(BlueBellPdfParser.ParseAll(Invoice175350Lines, []));

        Assert.Equal(new DateOnly(2025, 11, 13), section.Date);
        Assert.Equal("Sales", section.Kind);
        Assert.Equal("033103175350", section.SuggestedInvoiceNumber);
    }

    /// <summary>
    /// The TOTAL row lost its label, so the total comes from BALANCE DUE — which itself scanned as
    /// "7232" with no decimal point. Blue Bell always prints that figure to the cent, so the last
    /// two digits are the cents, and the reconstruction only stands because the line items still
    /// have to add up to it.
    /// </summary>
    [Fact]
    public void TotalIsRecoveredFromABalanceDueThatLostItsDecimalPoint()
    {
        var section = Assert.Single(BlueBellPdfParser.ParseAll(Invoice175350Lines, []));

        Assert.Equal(72.32m, section.Total);
        Assert.Equal(section.Total, section.Lines.Sum(l => l.ExtendedCost));
    }

    /// <summary>
    /// The neighbouring phone number and ZIP+4 must not be mistaken for the invoice date now that
    /// the word DATE is no longer required to find it — and not by the looser damaged-date reader
    /// either, which is why it insists on a real calendar date.
    /// </summary>
    [Fact]
    public void PhoneNumberAndZipAreNotMistakenForTheDate()
    {
        string[] lines =
        [
            "OUR PHONE NUMBER IS: 281-446-1090",
            "DALLAS, TEXAS 75267-4272",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "TOTAL 1.00 10.00 29.80",
        ];
        var section = Assert.Single(BlueBellPdfParser.ParseAll(lines, []));

        Assert.Null(section.Date);            // no date was invented from either number
        Assert.Equal(29.80m, section.Total);  // the rest of the ticket still parsed
    }

    /// <summary>
    /// A BALANCE DUE whose digits are wrong must not become the total. It is reconstructed, then
    /// thrown out by the checksum, so the file is refused rather than booked at the wrong figure.
    /// This is what keeps the missing-decimal-point reconstruction honest.
    /// </summary>
    [Fact]
    public void BalanceDueThatDisagreesWithTheLines_IsRejected()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "BALANCE DUE 9999",     // reconstructs to 99.99, but the one line is 29.80
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    /// <summary>
    /// When the totals are damaged past even the BALANCE fallback — the word itself unrecognisable,
    /// as in the real "BAUNGEDU.E34~9)60" — there is nothing left to check the line items against,
    /// so the ticket is refused outright rather than trusted.
    /// </summary>
    [Fact]
    public void TotalsDamagedBeyondReading_AreRefusedOutright()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "BAUNGEDU.E34~9)60",   // real damage from another ticket
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.NoTotal, ex.ReasonCode);
    }

    // ------------------------------------------------------------------ units, cartons and returns

    /// <summary>
    /// PdfPig's real output for invoice 033100305634 (BEST STOP #10, 01-30-2025) and the credit
    /// 033100305635 that follows it. This is the ticket that proved the DZ products are cartons:
    /// the credit is written as the fraction "-11/12" because eleven bars of a twelve-bar carton
    /// went back, and the label reads BARS RETURNED rather than UNITS RETURNED.
    /// </summary>
    private static readonly string[] Invoice305634Lines =
    [
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 01-30-2025 TIME 11:48 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO # INVOICE#",
        "266843 033100305634",
        "BEST STOP #10",
        "20110 FM 1314",
        "PORTER,TX 77365",
        "****",
        "**** Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0800 007189903720 1/2 GAL GR SUP",
        "1.00 4.00 6.64 26.56",
        "0925 007189905101 PT GR SUPREME",
        "9.00 90.00 2.91 261.90",
        "0151 007189921122 DZ VANILLA SAND",
        "1.00 2.00 6.48 12.96",
        "0165 007189921123 DZ CNC SAND",
        "1.00 2.00 6.48 12.96",
        "TOTAL 12.00 98.00 314.38",
        "BALANCE DUE -3+",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "COD",
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 01-30-2025 TIME 11:48 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO # INVOICE#",
        "266843 033100305635",
        "BEST STOP #10",
        "20110 EM 1314",
        "PORTER,TX 77365",
        "t ic**",
        "***.*~ ~ Return •",
        "p~",
        "Tk",
        "H i D;eLi",
        "PROD UPC ' —DESCRIPTION",
        "EA/COST QTY PRICE EXTENDED",
        "0151 007189921122 07 VANILLA SAND",
        "-11/12 6.48 -5.94",
        "BARS RETURNED -11.00",
        "TOTAL -5.94",
        "BALANCE DUE -5.94",
        "Note:",
        "SIGNATURE: SI i T Y ~ SRI ` ,f\"~ ~//A~~ `® .",
        "£ 2Lts i 2LJ/'KLLLL",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "COD",
    ];

    /// <summary>
    /// What live SSCS holds for this exact invoice, read off the Line Items tab on 2026-07-26.
    /// This is the authority for the whole units question: a DZ row printed as QTY 2 at $6.48 is
    /// held as 24 at $0.54, while pints and half gallons are already unit counts.
    ///
    ///   07189903720   4.00   6.64    26.56
    ///   07189905101  90.00   2.91   261.90
    ///   07189921122  24.00   0.54    12.96
    ///   07189921123  24.00   0.54    12.96
    ///   07189921122 -11.00   0.54    -5.94
    /// </summary>
    [Fact]
    public void MatchesTheQuantitiesAndCostsLiveSscsHolds()
    {
        var sections = BlueBellPdfParser.ParseAll(Invoice305634Lines, []);
        Assert.Equal(2, sections.Count);

        (string Id, int Qty, decimal Cost, decimal Ext)[] sales =
        [
            ("07189903720", 4, 6.64m, 26.56m),
            ("07189905101", 90, 2.91m, 261.90m),
            ("07189921122", 24, 0.54m, 12.96m),
            ("07189921123", 24, 0.54m, 12.96m),
        ];

        Assert.Equal(sales.Length, sections[0].Lines.Count);
        for (int i = 0; i < sales.Length; i++)
        {
            var line = sections[0].Lines[i];
            Assert.Equal(sales[i].Id, Upc.SscsIdFromGtin(line.Gtin));
            Assert.Equal(sales[i].Qty, line.Units);
            Assert.Equal(sales[i].Cost, line.UnitCost);
            Assert.Equal(sales[i].Ext, line.ExtendedCost);
        }
        Assert.Equal(314.38m, sections[0].Total);

        var credit = Assert.Single(sections[1].Lines);
        Assert.Equal("07189921122", Upc.SscsIdFromGtin(credit.Gtin));
        Assert.Equal(-11, credit.Units);
        Assert.Equal(0.54m, credit.UnitCost);
        Assert.Equal(-5.94m, credit.ExtendedCost);
        Assert.Equal(-5.94m, sections[1].Total);
    }

    /// <summary>
    /// The partial carton needs no fraction handling. "-11/12" matches no money pattern and is
    /// simply ignored; -5.94 divided by the 0.54 a single bar costs gives back -11 exactly. Booking
    /// -1 carton instead would have credited 12 bars when 11 came back.
    /// </summary>
    [Fact]
    public void PartialCartonReturnBooksTheBarsNotTheCarton()
    {
        var credit = Assert.Single(BlueBellPdfParser.ParseAll(Invoice305634Lines, [])[1].Lines);

        Assert.Equal(-11, credit.Units);
        Assert.NotEqual(-1, credit.Units);
        Assert.Equal(credit.Units * credit.UnitCost, credit.ExtendedCost);
    }

    /// <summary>
    /// A carton line seeds a sane shelf price. At $6.48 a carton the old per-carton reading gave a
    /// 40% margin of $11.49 for one ice cream sandwich; per bar it is $0.99.
    /// </summary>
    [Fact]
    public void CartonRetailIsPricedPerBarNotPerCarton()
    {
        var sandwiches = BlueBellPdfParser.ParseAll(Invoice305634Lines, [])[0]
            .Lines.Single(l => Upc.SscsIdFromGtin(l.Gtin) == "07189921122");

        Assert.Equal(0.99m, sandwiches.RetailPrice);
        Assert.True(sandwiches.RetailPrice < 2m, "a single sandwich must not be priced like a carton");
    }

    /// <summary>
    /// The ticket's own QTY total counts cartons, not bars, so it is compared against the carton
    /// count and must not warn on a perfectly good invoice.
    /// </summary>
    [Fact]
    public void CartonTicketDoesNotWarnAboutItsPrintedQuantityTotal()
    {
        var warnings = new List<ConversionWarning>();
        BlueBellPdfParser.ParseAll(Invoice305634Lines, warnings);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("quantities add up", StringComparison.Ordinal));
    }
}
