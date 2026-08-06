using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Blue Bell PDF -> XML proofs.
///
/// The fixtures below are the REAL text PdfTextExtractor (PdfPig) reconstructs from the six
/// distinct sample invoices, pasted verbatim - scanner damage included. These tickets are
/// photographs of a dot-matrix print with the Blue Bell cow watermark laid across the middle
/// columns, so BX CNT and QTY are frequently destroyed ("\u002E005 1000" where the paper reads
/// "1.00  10.00") while PRICE and EXTENDED at the clean right edge survive. Every expected value
/// here was read off the rendered scan by eye, and each ticket's own printed TOTAL and unit count
/// confirm it.
/// </summary>
public class BlueBellPdfTests
{
﻿    private static readonly string[] Example1Lines =
    [
        "C1®",
        "Blue Bell GG reameTle , L.P.",
        "DATE 10-30-2025 TIME 12:54 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "PICK LIST",
        "PROD DESCRIPTION SELLBOX QTY",
        "925 PT GR SUPREME 5.00 50",
        "TOTAL QUANTITY 5.00 50",
        "Blue Bell=Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 10-30-2025 TIME i2:56- BRANCHt33 ROUTE# 10 TRUCK# 1404",
        "(",
        "SHIP TO #",
        "369 60 \"N0 ~033105035263",
        "BEST STOP ~14~~\\` ~~ °",
        "17828 FM 1314",
        "1 D)jule I B3 II,®",
        "CONROE,TX 77302",
        "****",
        "Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0925 007189905101 PT GR SUPREME",
        "5.00 50.00 2.98 149.00",
        "TOTAL 5.00 50.00 149.00",
        "BALANCEE DU 1 .49.00",
        "Note:",
        "®",
        "5)~r",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Charge",
        "I to 153e1 ®",
    ];

    private static readonly string[] Example2Lines =
    [
        "r",
        "Blue Bell Creameries, L.P.",
        "General tcrraBrz hcnn, Texas",
        "DATE 01-08-2026 TIMEe73:41 BRAN 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO # r",
        "INVOIC E# 033",
        "3692601 033100086682",
        "BEST ST4P14 ~Qjj~",
        "17828 FM 1314",
        "DTI",
        "CONROE,TX 77302 J D)eJI~O",
        "D)jl~e )",
        "****",
        "**** Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0800 007189903720 1/2 GAL GR SUP",
        "2.00 8.00 6.85 54.80",
        "0925 007189905101 PT GR SUPREME",
        "8.00 80.00 2.98 238.40",
        "0122 007189921133 DZ 3OZ HMV CUPS",
        "_ = 1_00 2.00 6.36 12.72",
        "0165 007189921123 DZ'CNC SAND",
        "r ~00' 00 6.84 13.68",
        "JIl ~f",
        "(L lL \\±S LI I®",
        "TOTAL 12.00 92 00 319.60",
        "BAUNGEDU.E34~9)60",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER L 5 .- 2610",
    ];

    private static readonly string[] Example3Lines =
    [
        "Blue BeIrCreamerJes, L.P.",
        "General Offices r,.9'renham, Texas",
        "DATE 03-19-2026 TIME r22:40 BRAN#_ ~ 33 (ROUTE 10 TRUCK# 1404",
        "SHIP TO # vo~l ce#\\",
        "36926Q(_1CJ\" OS( ~3310Q786112",
        "BEST STO1 4 r®",
        "O",
        "17828 FM 1314 ~aL",
        "~)kuie",
        "CONROE,TX 77302",
        "****",
        "**** Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0800 007189903720 1/2 GAL GR SUP",
        "3.00 12.00 6.85 82.20",
        "0781 007189903733 1/2 OUT CHOC",
        "1.00 4.00 6.85 27.40",
        "0925 007189905101 PT GR SUPREME",
        "6.00 60.00 2.98 178.80",
        "0122 007189921133 DZ+302 HMV CUPS/.. ' i",
        "f~ r ~~/~\\1.00 ~~2.00 6.36 12.72",
        "`",
        "J/",
        "0151 007189921122=DZ~~I LLA' SAND~ ~\"",
        "\\ 1 00c 2:001 Q 6.84 13.68",
        "D~Y",
        "0165 00718992112 1 .D2/ C~N Dj- C ~~",
        "1 00 2.00 6.84 13.68",
        "TOTAL 13.00 82.00 328.48",
        "BALANCE DUE 328.48",
        "~•3",
        "Note:",
        "~s",
        "SIGNATURE: I L",
        "7C \\®",
        "OUR REMITTANCE ADDRESflS: P`0. BOr674Z72",
        "DALLAS `TEXAS 75267 4272",
        "OUR PHONE NUMBER Ii '1 281'-446-190",
        "\"lyu~#",
        "Charge",
        "Blue Bell'Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 03-19-2026 TIME 12:40 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO #",
        "369260 l I ~033100786113 D",
        "BEST STOP 1'4",
        "17828 FM 1314",
        "CONROE,TX 77302 J",
        "*** ' Returns)",
        "PROD UPC '-DESCR-IPTION' ' I» L",
        "EA/COST QTY PRICE EXTENDED",
        "0925 007189905101 PT GR SUPREME",
        "-1.00 2.98 -2.98",
        "UNITS RETURNED -1.00",
        "TOTAL -2.98",
        "BALANCE DUE -2.98",
        "Note:",
        "1y",
        "(r-7/ \\ ( '1 3",
        "i",
        "r",
        "SIGNATURE: cam,-1",
        "m Th m",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Charge",
    ];

    private static readonly string[] Example4Lines =
    [
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenhan, Texas",
        "DATE 05-28-2026 TIME 11:01 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
        "SHIP TO # INVOICE#",
        "369260 033101486067",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "Sales",
        "r--",
        "PROD UPC DESCRIPTION ~'~",
        "EA/COST /RETAIL BX CNT ~ ,.QTY PRICE EXTENDED",
        "0781 007189903733-1A2=O1JThCHO `i\"--Z k- l ff i",
        "4.00-®6.85 27.40",
        "0789 00718990373L1 112~NCJ D)e~",
        "J",
        "1 00 4.00 6.85 27.40",
        "0777 007189903735 1/2 HMV",
        "1.00 4.00 6.85 27.40",
        "0915 007189905129 PT GREAT DIVIDE",
        "1.00 10.00 2.98 29.80",
        "0916 007189905115 PT CCC DOUGH",
        "1.00 10.00 2.98 29.80",
        "0919 007189905113 PT BUT PEC",
        "1.00 10.00 2.98 29.80",
        "0921 007189905117 PT CN",
        "/ 1.00 10.00 2.98 29.80",
        "0924 007189905122 PT IHMV",
        "i",
        ".005 1000 2.98 29.80",
        "0928 007189905127 PTROCKY RD",
        "I .1.00J 10.00 ®2.98 29.80",
        "Ti 11(",
        "0020 007189906120l PPFEAN D FLAVOR~~",
        "2.00 20.00 2.98 59.60",
        "0122 007189921133 DZ 307 HMV CUPS",
        "1,00 2.00 6.36 12.72",
        "TOTAL 12.00 94.00 333.32",
        "BALANCE DUE 333.32",
        "Note:",
        "C",
        "'**",
        "SIGNATURE:",
        ")I~)~J;_I I",
        "I",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Charge",
    ];

    private static readonly string[] Example5Lines =
    [
        "BIue'Bell Creameries, L.P.",
        "General Offices -Brenhom, Texas",
        "DATE 04-30-2026 hTIMEr13:23`BRANCH# 33' ROUTE# 10",
        "TRUCK# 2714",
        "~C ~~- ~®",
        "SHIP TO # INVOICE",
        "369260 ` 033101206774",
        "BEST STOP1 4@JI.e J D LL",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "**** ****",
        "Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0789 007189903731 1/2 CNC",
        "1.00 4.00 6.85 27.40",
        "7001 007189903730 1/2 COK 2 SW",
        "~1:00~4.00 6.85 27.40",
        "0020 007189906120 PT FEATURED -FLAVOR",
        "' 1.00 ~1v.00 2.98 29.80",
        "~)0",
        "0915 ./",
        "007189905129 PTIGREATTDDIVIDE",
        "~",
        "'- 1.00 10.00 ® 2.98 29.80",
        "rI",
        "~I ®",
        "0924 007189905122 -PT~HMV s",
        "v91 L. L00-/~ ~10'.90~ 2.98 29.80",
        "0944 007189905124 PT M00 CRUNCH",
        "1.00 10.00 2.98 29.80",
        "8001 007189905116 PT COK 2 STP",
        "1.00 10.00 2.98 29.80",
        "0151 007189921122 DZ VANILLA SAND",
        "1.00 2.00 6.84 13.68",
        "0165 007189921123 DZ CNC SAND",
        "1.00 2.00 6.84 13.68",
        "TOTAL _ 9.00, 6200 231.16",
        "(",
        "II",
        "BALANC,E_DUE'2%1.16",
        "Note: S",
        "O)JIe D) IL",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "Charge",
        "®",
        "BlueB eR1Creameried, ~.P.",
        "1l1 , i f,LJ V'R_ i",
        "Gene al Office,-frenha~n„Uezxas",
        "DATE 04-30-2026 TIME 13:23 BRANCH# 33 ROUTE# 10 TRUCK# 2714",
        "SHIP TO #",
        "INVOICE#033",
        "369260 101 206775",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "Returns",
        "PROD UPC DESCRIPTION",
        "EA/COST QTY PRICE EXTENDED",
        "0925 007189905101 PT GR SUPREME j,,",
        "2.00 2.98 -5.96",
        "LJf.V. 7/\\",
        "?t \\®",
        "UNITS RETURNED -2.00",
        "TOTALJ71 -5.96",
        "BALANCE DU r -596",
        "Note:",
        "7-",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "~DALD S~TEXXAS 75267-4272",
        "OUR PHONE NUMBER/IS: 281-446;1090",
        "r -_\\",
        "Charge'",
        "/~~ ~ ~®",
        "r ice",
        "0",
        "®",
        "D)",
    ];

    private static readonly string[] Example6Lines =
    [
        "Blue Bell Creameries, L.P.",
        "General Offices - Brenham, Texas",
        "DATE 04-16-2026 TIME 12:42 BRANCH# 33",
        "ROUTE# 10 TRUCK# 1404",
        "SHIP TO #",
        "369260 INVOICE#03310",
        "1 066688",
        "BEST STOP 14",
        "17828 FM 1314",
        "CONROE,TX 77302",
        "Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNL_\\ 0Ty PRICE EXTENDED",
        "d c S . i",
        "0781 007189903733'1/9~DUT CHOC \\ \\",
        "~a~Z",
        "~ 0 ~4 .00 6.85 27.40",
        "0777 007189903735 1/2 HMV",
        "1.00J/ ~'.,_4 000 X 6.85 27.40",
        "Ie J D)",
        "0907 007189905126 ~D~ IT l",
        "1.00 10.00 2.98 29.80",
        "0916 007189905115 PT CCC DOUGH",
        "1.00 10.00 2.98 29.80",
        "0921 007189905117 PT CNC",
        "1.00 10.00 2.98 29.80",
        "0923 007189905120 PT DUT CHOC",
        "1.00 10.00 2.98 29.80",
        "0924 007189905122 PT HMV",
        "1.00 10.00 2.98 29.80",
        "0926 007189905128 PT,STRAW ..,,",
        "1:001 10.00 2.98 29.80",
        "(",
        "0020 007189906120 PTS cA7URED_ FLAVOR",
        "~~ ~( /\\1.00 è2 0.00 ®2.98 29.80",
        "0122 007189921133 fDZ)30Z HMV CUP51 ®",
        "fl')",
        "5~ ~1 ) U)L JL 6.36 12.72",
        "TOTAL 10.00 80.00 276.12",
        "BALANCE DUE 276.12",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS:~P:O. VBOX 67427 p",
        "j( j( ~~DALLAS, TEXAS'c 7526704272",
        "OUR PHONE NUMBER K IS: 281-446-1090",
        "i",
        "D)LCharge~ e",
    ];

    private static IReadOnlyList<BlueBellSection> Parse(string[] lines, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return BlueBellPdfParser.ParseAll(lines, warnings);
    }

    // ------------------------------------------------------------------ the printed-code mapping

    /// <summary>
    /// The invoice's UPC column is not a UPC-A - only 3 of the 21 codes in the samples pass a
    /// check-digit test, which is chance. It is the 11-digit UPC-A body padded to 12 with a leading
    /// zero, and that body is what SSCS shows as the item's ID/PLU.
    /// </summary>
    [Fact]
    public void PrintedCodeIsNotAUpcA_AndBecomesOneByRestoringTheCheckDigit()
    {
        Assert.False(Upc.IsValidUpcA12("007189905101"));
        Assert.False(Upc.IsValidUpcA12("007189903720"));

        Assert.Equal("071899051015", BlueBellPdfParser.ToGtin("007189905101"));
        Assert.True(Upc.IsValidUpcA12(BlueBellPdfParser.ToGtin("007189905101")));

        // The point of the whole mapping: SSCS drops the last character and keeps everything else,
        // so this lands on the 07189905101 the store already has rather than making a new item.
        Assert.Equal("07189905101", Upc.SscsIdFromGtin(BlueBellPdfParser.ToGtin("007189905101")));
    }

    /// <summary>
    /// SSCS keeps leading zeros - VM Novelties invoice IN-112 stored 09946143239 and 08526819712
    /// intact - so sending the printed 12 characters unchanged would land one digit short.
    /// </summary>
    [Fact]
    public void SendingThePrintedCodeUnchangedWouldLandOnTheWrongItem()
    {
        Assert.NotEqual("07189905101", Upc.SscsIdFromGtin("007189905101"));
        Assert.Equal("00718990510", Upc.SscsIdFromGtin("007189905101"));
    }

    // ------------------------------------------------------------------ retail seed

    /// <summary>
    /// Blue Bell prints a RETAIL column and never fills it in, so new items are seeded at a 40%
    /// margin (cost / 0.60) rounded up to the next price ending in 9 in the same dime.
    /// </summary>
    [Theory]
    [InlineData(2.98, 4.99)]    // pint:            4.9667
    [InlineData(6.85, 11.49)]   // half gallon:    11.4167
    [InlineData(6.36, 10.69)]   // dz 3oz cups:    10.60 exactly
    [InlineData(6.84, 11.49)]   // dz sandwiches:  11.40 exactly
    public void RetailIsSeededAtFortyPercentMarginRoundedUpToNine(double cost, double expected)
    {
        Assert.Equal((decimal)expected, BlueBellPdfParser.SeedRetail((decimal)cost));
    }

    [Fact]
    public void EveryRetailEndsInNineAndIsAboveCost()
    {
        foreach (var section in Parse(Example4Lines, out _))
            foreach (var line in section.Lines)
            {
                Assert.Equal(9, (int)(line.RetailPrice * 100m) % 10);
                Assert.True(line.RetailPrice > line.UnitCost, $"{line.RetailPrice} should exceed {line.UnitCost}");
            }
    }

    // ------------------------------------------------------------------ whole-invoice proofs

    [Fact]
    public void Example2_ParsesTheCleanTicket()
    {
        var section = Assert.Single(Parse(Example2Lines, out _));

        Assert.Equal("Sales", section.Kind);
        Assert.Equal(new DateOnly(2026, 1, 8), section.Date);
        Assert.Equal(319.60m, section.Total);
        Assert.Equal(4, section.Lines.Count);
        Assert.Equal(136, section.Lines.Sum(l => l.Units));
        Assert.Equal(section.Total, section.Lines.Sum(l => l.ExtendedCost));

        // A half gallon is one unit, so its row "2.00 (boxes) 8.00 (qty) 6.85 54.80" is 8 at 6.85.
        Assert.Equal("071899037200", section.Lines[0].Gtin);
        Assert.Equal(8, section.Lines[0].Units);
        Assert.Equal(6.85m, section.Lines[0].UnitCost);
        Assert.Equal(54.80m, section.Lines[0].ExtendedCost);

        // A "DZ" row is a carton of twelve: "1.00 2.00 6.36 12.72" is 24 cups at 0.53, not 2 at 6.36.
        var cups = section.Lines.Single(l => l.Gtin == BlueBellPdfParser.ToGtin("007189921133"));
        Assert.Equal(24, cups.Units);
        Assert.Equal(0.53m, cups.UnitCost);
        Assert.Equal(12.72m, cups.ExtendedCost);
    }

    /// <summary>
    /// Example 1 is two pages: a PICK LIST packing slip and then the priced invoice. The packing
    /// slip has no UPCs or prices and must not become an invoice of its own.
    /// </summary>
    [Fact]
    public void Example1_SkipsThePickListPage()
    {
        var section = Assert.Single(Parse(Example1Lines, out _));

        Assert.Equal("Sales", section.Kind);
        Assert.Equal(new DateOnly(2025, 10, 30), section.Date);
        Assert.Equal(149.00m, section.Total);
        Assert.Equal(50, Assert.Single(section.Lines).Units);
    }

    /// <summary>
    /// The reason the operator types the invoice number: the scan of example 1 reads 033105035263
    /// where the paper reads 033103035263. Nothing in the file can catch that - the number has no
    /// check digit - so the parser only ever offers it as a suggestion.
    /// </summary>
    [Fact]
    public void SuggestedInvoiceNumberIsAHintOnly_AndIsKnownToBeWrongOnExample1()
    {
        var section = Assert.Single(Parse(Example1Lines, out _));

        Assert.Equal("033105035263", section.SuggestedInvoiceNumber);   // what the scan says
        Assert.NotEqual("033103035263", section.SuggestedInvoiceNumber); // what the paper says
    }

    /// <summary>A Sales ticket and its Returns ticket are separate invoices on separate pages.</summary>
    [Fact]
    public void Example3_SplitsSalesAndReturnsIntoTwoInvoices()
    {
        var sections = Parse(Example3Lines, out _);
        Assert.Equal(2, sections.Count);

        var sales = sections[0];
        Assert.Equal("Sales", sales.Kind);
        Assert.Equal(328.48m, sales.Total);
        Assert.Equal(6, sales.Lines.Count);
        Assert.Equal(148, sales.Lines.Sum(l => l.Units));

        var returns = sections[1];
        Assert.Equal("Returns", returns.Kind);
        Assert.Equal(-2.98m, returns.Total);
        Assert.Equal(-1, Assert.Single(returns.Lines).Units);
        Assert.Equal(2.98m, returns.Lines[0].UnitCost);          // cost stays positive
        Assert.Equal(-2.98m, returns.Lines[0].ExtendedCost);
    }

    /// <summary>Example 5 stacks both tickets on one tall page; the DATE header separates them.</summary>
    [Fact]
    public void Example5_SplitsTwoInvoicesPrintedOnOnePage()
    {
        var sections = Parse(Example5Lines, out _);
        Assert.Equal(2, sections.Count);

        Assert.Equal("Sales", sections[0].Kind);
        Assert.Equal(231.16m, sections[0].Total);
        Assert.Equal(9, sections[0].Lines.Count);
        Assert.Equal(106, sections[0].Lines.Sum(l => l.Units));

        Assert.Equal("Returns", sections[1].Kind);
        Assert.Equal(-5.96m, sections[1].Total);
        Assert.Equal(-2, Assert.Single(sections[1].Lines).Units);
    }

    /// <summary>
    /// Every sample ticket reconciles two independent ways: the line amounts sum to the printed
    /// TOTAL, and the units sum to the printed unit count.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTickets))]
    public void EveryTicketReconcilesToItsOwnPrintedTotals(string name, string[] lines, decimal[] totals, int[] units)
    {
        var sections = Parse(lines, out _);

        Assert.True(totals.Length == sections.Count, $"{name}: expected {totals.Length} invoice(s), got {sections.Count}");
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.Equal(totals[i], sections[i].Total);
            Assert.Equal(totals[i], sections[i].Lines.Sum(l => l.ExtendedCost));
            Assert.Equal(units[i], sections[i].Lines.Sum(l => l.Units));
            Assert.All(sections[i].Lines, l => Assert.NotEqual(0, l.Units));
        }
    }

    public static TheoryData<string, string[], decimal[], int[]> AllTickets() => new()
    {
        // Unit counts are in SELLABLE units, so a "DZ" line contributes twelve per carton: example
        // 2 prints QTY 92 but is 136 units, because two of its four lines are dozen-cartons.
        { "example 1", Example1Lines, [149.00m], [50] },
        { "example 2", Example2Lines, [319.60m], [136] },
        { "example 3", Example3Lines, [328.48m, -2.98m], [148, -1] },
        { "example 4", Example4Lines, [333.32m], [116] },
        { "example 5", Example5Lines, [231.16m, -5.96m], [106, -2] },
        { "example 6", Example6Lines, [276.12m], [102] },
    };

    // ------------------------------------------------------------------ scanner-damage recovery

    /// <summary>
    /// The cow watermark sits over the QTY column. On example 4 the row for PT HMV scans as
    /// "\u002E005 1000 2.98 29.80" where the paper reads "1.00  10.00  2.98  29.80"; on example 6 the
    /// FEATURED FLAVOR row scans as "1.00 e2 0.00 (R)2.98 29.80". Both recover to 10 units from
    /// EXTENDED / PRICE, which is why the totals still balance.
    /// </summary>
    [Fact]
    public void QuantityDestroyedByTheWatermarkIsRecoveredFromExtendedOverPrice()
    {
        var ex4 = Assert.Single(Parse(Example4Lines, out _));
        var ptHmv = ex4.Lines.Single(l => l.Gtin == BlueBellPdfParser.ToGtin("007189905122"));
        Assert.Equal(10, ptHmv.Units);
        Assert.Equal(29.80m, ptHmv.ExtendedCost);

        var ex6 = Assert.Single(Parse(Example6Lines, out _));
        var featured = ex6.Lines.Single(l => l.Gtin == BlueBellPdfParser.ToGtin("007189906120"));
        Assert.Equal(10, featured.Units);   // scans as "0.00" and as "20.00" depending on the reader
        Assert.Equal(29.80m, featured.ExtendedCost);
    }

    /// <summary>
    /// Example 5's Returns row loses the minus sign on QTY ("2.00 2.98 -5.96"), so the sign has to
    /// come from EXTENDED. Booking +2 instead of -2 would have added stock the store sent back.
    /// </summary>
    [Fact]
    public void ReturnKeepsItsNegativeQuantityEvenWhenTheMinusSignIsLost()
    {
        var returns = Parse(Example5Lines, out _)[1];

        Assert.Equal(-2, Assert.Single(returns.Lines).Units);
        Assert.Equal(-5.96m, returns.Lines[0].ExtendedCost);
    }

    /// <summary>
    /// A UPC the scanner mangled past reading is restored from the PROD number, with a warning.
    /// Example 3's DZ CNC SAND row loses a digit ("00718992112"); example 4's 1/2 CNC row gains a
    /// letter ("00718990373L1") and is readable once L is mapped back to 1.
    /// </summary>
    [Fact]
    public void MangledUpcIsRepairedFromTheProdNumberAndWarnsAboutIt()
    {
        var ex3 = Parse(Example3Lines, out var warnings)[0];

        Assert.Equal(BlueBellPdfParser.ToGtin("007189921123"), ex3.Lines[5].Gtin);
        var warning = Assert.Single(warnings, w => w.ReasonCode == PdfReasonCodes.UpcCheckDigit);
        Assert.Contains("0165", warning.Message, StringComparison.Ordinal);

        // The letter-for-digit case needs no repair and therefore no warning.
        var ex4 = Assert.Single(Parse(Example4Lines, out var clean));
        Assert.Equal(BlueBellPdfParser.ToGtin("007189903731"), ex4.Lines[1].Gtin);
        Assert.Empty(clean);
    }

    [Fact]
    public void EveryBarcodeIsAValidUpcASoSscsLandsOnTheStoresOwnItem()
    {
        foreach (var section in Parse(Example4Lines, out _).Concat(Parse(Example5Lines, out _)))
            Assert.All(section.Lines, l =>
            {
                Assert.True(Upc.IsValidUpcA12(l.Gtin), $"'{l.Gtin}' is not a valid UPC-A");
                Assert.Equal(11, Upc.SscsIdFromGtin(l.Gtin).Length);
                Assert.StartsWith("07189", Upc.SscsIdFromGtin(l.Gtin), StringComparison.Ordinal);
            });
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void TotalThatDoesNotReconcile_IsRejected()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "TOTAL 1.00 10.00 99.99",   // does not match the single 29.80 line
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    /// <summary>
    /// An unknown PROD number whose UPC did not scan cleanly is refused rather than guessed - Blue
    /// Bell adds seasonal flavours, and inventing a barcode would post stock against another item.
    /// </summary>
    [Fact]
    public void UnreadableUpcOnAnUnknownProduct_IsRejected()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0999 00718992112 PT BRAND NEW FLAVOR",   // a digit short, and PROD 0999 is unknown
            "1.00 10.00 2.98 29.80",
            "TOTAL 1.00 10.00 29.80",
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.UpcCheckDigit, ex.ReasonCode);
        Assert.Contains("0999", ex.UserMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row damaged past recognition drops out of the parse entirely. That must never pass
    /// silently: the money it carried is missing from the sum, so the TOTAL checksum refuses the
    /// ticket rather than emitting an invoice that is short a line.
    /// </summary>
    [Fact]
    public void RowDamagedPastRecognition_IsCaughtByTheTotalChecksum()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "0800 ~~~~~~~ 1/2 GAL GR SUP",      // code destroyed: this row cannot be read at all
            "1.00 4.00 6.85 27.40",
            "TOTAL 2.00 14.00 57.20",
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    [Fact]
    public void PickListOnItsOwn_IsRejectedWithPlainMessage()
    {
        string[] lines =
        [
            "DATE 10-30-2025 TIME 12:54 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "PICK LIST",
            "PROD DESCRIPTION SELLBOX QTY",
            "925 PT GR SUPREME 5.00 50",
            "TOTAL QUANTITY 5.00 50",
        ];
        var ex = Assert.Throws<EdiConversionException>(() => BlueBellPdfParser.ParseAll(lines, []));
        Assert.Equal(PdfReasonCodes.NoLineItems, ex.ReasonCode);
        Assert.Contains("PICK LIST", ex.UserMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ XML output

    /// <summary>
    /// Builds the invoice the way the pipeline does. The second argument only matters for a ticket
    /// whose date the scan destroyed; every fixture here has a readable one, and its own date wins.
    /// </summary>
    private static Invoice AsInvoice(BlueBellSection section, string invoiceNumber) =>
        section.ToInvoice(invoiceNumber, section.Date ?? default);

    [Fact]
    public void ProducesTheSameNaxmlShapeAsEveryOtherVendor()
    {
        var section = Assert.Single(Parse(Example2Lines, out _));
        var doc = XDocument.Parse(TestUtil.Text(NaxmlBuilder.Build(AsInvoice(section, "033100086682"))));

        Assert.Equal("BLUE BELL", doc.Descendants("Supplier").First().Element("Name")!.Value);
        Assert.Equal("033100086682", doc.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("2026-01-08", doc.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("319.60", doc.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(4, doc.Descendants("LineItem").Count());

        var first = doc.Descendants("LineItem").First();
        Assert.Equal("071899037200", first.Descendants("InvoiceUnitId").Single().Value);
        Assert.Equal("8", first.Descendants("InvoiceUnitQty").Single().Value);
        Assert.Equal("6.8500", first.Descendants("InvoiceUnitCost").Single().Value);
        Assert.Equal("11.49", first.Descendants("RetailPrice").Single().Value);

        // Never zero - an unpriced new item could otherwise ring up free at the register.
        Assert.All(doc.Descendants("RetailPrice"), e => Assert.NotEqual("0.00", e.Value));
    }

    [Fact]
    public void ReturnsInvoiceIsWrittenAsANegativeCredit()
    {
        var returns = Parse(Example3Lines, out _)[1];
        var doc = XDocument.Parse(TestUtil.Text(NaxmlBuilder.Build(AsInvoice(returns, "033100786113"))));

        Assert.Equal("-2.98", doc.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal("-1", doc.Descendants("InvoiceUnitQty").Single().Value);
        Assert.Equal("2.9800", doc.Descendants("InvoiceUnitCost").Single().Value);
    }

    [Fact]
    public void ScannerJunkIsStrippedSoTheFileStaysPlainAscii()
    {
        foreach (var section in Parse(Example5Lines, out _))
        {
            byte[] bytes = NaxmlBuilder.Build(AsInvoice(section, "TEST"));
            Assert.All(bytes, b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2} reached the output"));
        }
    }

    /// <summary>
    /// One ticket carrying two invoices must produce two differently-named files; naming them after
    /// the invoice number the operator keyed keeps them apart and traceable to the paper.
    /// </summary>
    [Fact]
    public void EachInvoiceNumberGetsItsOwnOutputName()
    {
        Assert.Equal("SSCS_033100786112.xml", OutputNaming.BuildInvoiceOutputFileName("033100786112"));
        Assert.Equal("SSCS_033100786113.xml", OutputNaming.BuildInvoiceOutputFileName("033100786113"));
        Assert.Equal("SSCS_IN-112.xml", OutputNaming.BuildInvoiceOutputFileName(" IN-112 "));
        Assert.Equal(OutputNaming.FallbackName, OutputNaming.BuildInvoiceOutputFileName("   "));
    }
}
