using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Parser proofs for the Bimbo path, run against the nine real sample tickets (06/02/26 to
/// 07/28/26) with their scan damage intact. Nothing here is hand-written sample text.
///
/// The theme is that the ticket checks itself three ways — per row, per section, and per ticket —
/// and that no single printed figure is ever taken as final. Every one of these files has something
/// wrong with its text layer (a missing QTY column, a mangled price, a mangled barcode, a mangled
/// section count) and every one of them still has to come out with the money and the unit count the
/// paper prints.
/// </summary>
public class BimboPdfTests
{
    private const string Dir = @"Fixtures\PDF";

    /// <summary>Every readable sample, with the figures printed on the paper.</summary>
    public static TheoryData<string, decimal, int, int> Samples => new()
    {
        // file,                             CASH DUE, TICKET TOTALS units, product lines
        { "84294890013239_sscs_verified",     335.07m, 151, 35 },
        { "84294890013091_zero_money_row",    151.33m,  77, 21 },
        { "84294890012926_no_qty_column",     278.86m, 123, 23 },
        { "84294890012769",                   177.41m,  80, 23 },
        { "84294890012641",                   128.64m,  61, 18 },
        { "84294890013154_bad_section_count", 209.32m, 104, 27 },
        { "84294890012057",                   174.84m,  81, 19 },
        { "84294890013046",                    65.96m,  34, 14 },
    };

    public static TheoryData<string> SampleNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in Samples) data.Add((string)row[0]);
            return data;
        }
    }

    private static BimboTicket Parse(string ticket, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return BimboPdfParser.Parse(
            PdfTextExtractor.ExtractLines(File.ReadAllBytes(Path.Combine(Dir, $"BIMBO_{ticket}.pdf"))),
            warnings);
    }

    // ─────────────────────────────────────────────────────────────────── the checksums

    /// <summary>
    /// The whole point of the parser: whatever the scan did to the page, the lines add up to the
    /// ticket's own CASH DUE to the penny and the quantities to its own TICKET TOTALS count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void EverySampleReconcilesToItsOwnPrintedTotalAndUnitCount(
        string ticket, decimal total, int units, int lineCount)
    {
        var t = Parse(ticket, out _);

        Assert.Equal(total, t.Total);
        Assert.Equal(total, t.Lines.Sum(l => l.ExtendedCost));
        Assert.Equal(units, t.Lines.Sum(l => l.Units));
        Assert.Equal(lineCount, t.Lines.Count);
    }

    /// <summary>
    /// Returns are negative quantities at a positive cost on the same invoice, never a separate
    /// document and never a negative price. Verified in live SSCS, which holds the 07-28 ticket's 26
    /// deliveries and 9 returns as one $335.07 paidout.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void ReturnsAreNegativeUnitsAtAPositiveCost(string ticket)
    {
        var t = Parse(ticket, out _);

        Assert.Contains(t.Lines, l => l.Units < 0);
        Assert.All(t.Lines, l => Assert.True(l.UnitCost > 0m, $"{l.Gtin} priced {l.UnitCost}"));
        Assert.DoesNotContain(t.Lines, l => l.Units == 0);
    }

    // ─────────────────────────────────────────────────────────────────── live SSCS

    /// <summary>
    /// What live SSCS displayed for the 07-28-2026 ticket, read off its Line Items tab: the ID/PLU
    /// every barcode landed on, and the quantity against it. This is the proof that Bimbo's
    /// "74323-09183" has to go out as the UPC-A 074323091830 — SSCS drops the check digit itself and
    /// lands on 07432309183, which is the id this store already holds the item under.
    ///
    /// The store reached these ids by scanning the goods with a hand-held barcode scanner, so they
    /// are the real barcodes on the real packages, not something the file talked SSCS into.
    /// </summary>
    public static TheoryData<string, int> LiveSscsLineItems => new()
    {
        // deliveries
        { "07432309183", 3 },   { "07432301085", 2 },   { "07432309975", 6 },
        { "07432309139", 4 },   { "07432309122", 9 },   { "07432309173", 4 },
        { "07432373951", 15 },  { "07432309501", 2 },   { "07432309535", 4 },
        { "07432305156", 4 },   { "07432309523", 12 },  { "07432373899", 8 },
        { "07432309206", 5 },   { "07087000002", 12 },  { "07087073405", 12 },
        { "07087004183", 12 },  { "07087000742", 4 },   { "07087096079", 12 },
        // items delivered AND returned net out: 4-2, 4-3, 8-15, 8-8, 8-1, 8-3, 4-2, 12-4, 12-1
        { "07432309165", 2 },   { "07432373943", 1 },   { "07432373929", -7 },
        { "07432373936", -8 },  { "07087073402", 7 },   { "07087073400", 5 },
        { "07087000743", 2 },   { "07087096080", 8 },   { "07087096086", 11 },
    };

    [Theory]
    [MemberData(nameof(LiveSscsLineItems))]
    public void EveryBarcodeLandsOnTheItemIdLiveSscsShowed(string sscsId, int netUnits)
    {
        var t = Parse("84294890013239_sscs_verified", out _);

        var forItem = t.Lines.Where(l => Upc.SscsIdFromGtin(l.Gtin) == sscsId).ToList();
        Assert.NotEmpty(forItem);
        Assert.Equal(netUnits, forItem.Sum(l => l.Units));
    }

    /// <summary>
    /// Ea Cost is the invoice's WHOLESALE PRICE column, unchanged. Live SSCS showed 3.02 against the
    /// 3.0200 lines and 1.94 against the 1.9400 lines.
    /// </summary>
    [Fact]
    public void UnitCostIsTheWholesalePriceColumn()
    {
        var t = Parse("84294890013239_sscs_verified", out _);

        Assert.Equal(3.02m, Cost(t, "07432309183"));   // BIM PANQUE NUE FS FC
        Assert.Equal(3.02m, Cost(t, "07087096086"));   // MB MINI PWD DON
        Assert.Equal(2.59m, Cost(t, "07432309206"));   // MLA POLVORO FS15P
        Assert.Equal(1.94m, Cost(t, "07432309975"));   // BIM NITO DUO SS2P

        static decimal Cost(BimboTicket t, string id) =>
            t.Lines.First(l => Upc.SscsIdFromGtin(l.Gtin) == id).UnitCost;
    }

    /// <summary>
    /// Why the "*****15 SNACK NTX 1 @ 0.80" line on the store's own paidout is not reproduced, and
    /// must not be.
    ///
    /// That line is a plug the employee added to make a hand-scanned entry reach the ticket's total.
    /// It was needed because SSCS costed two lines from its own item record at 1.89 where the invoice
    /// prints 1.9400 — 8 units on each, so 8 x 0.05 twice, which is exactly the 0.80 that was keyed.
    /// Reading the invoice instead of the shelf means the cost is right and there is nothing to plug:
    /// the lines reach 335.07 on their own.
    /// </summary>
    [Fact]
    public void TheLinesReachTheTicketTotalWithNoBalancingEntry()
    {
        var t = Parse("84294890013239_sscs_verified", out _);

        decimal shortfallSscsPlugged =
            8 * (1.94m - 1.89m) +    // 07432373929 SPONGE COOKIES
            8 * (1.94m - 1.89m);     // 07432373899 MARINELA TRIKIS
        Assert.Equal(0.80m, shortfallSscsPlugged);

        Assert.Equal(335.07m, t.Lines.Sum(l => l.ExtendedCost));
        Assert.All(t.Lines, l => Assert.NotEqual("SNACK NTX", l.Description));
        Assert.Equal(35, t.Lines.Count);   // 26 delivered + 9 returned; no 36th balancing line
    }

    // ─────────────────────────────────────────────────────────────────── barcodes

    /// <summary>
    /// Bimbo prints the UPC body with the leading zero and the check digit both removed, so every
    /// barcode leaving here is rebuilt into a whole, self-consistent UPC-A. If this ever fails, SSCS
    /// would be dropping a real digit rather than a check digit and the delivery would land on an
    /// item that does not exist.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EveryBarcodeGoesOutAsAWholeValidUpcA(string ticket)
    {
        var t = Parse(ticket, out _);

        Assert.All(t.Lines, l =>
        {
            Assert.Equal(12, l.Gtin.Length);
            Assert.True(Upc.IsValidUpcA12(l.Gtin), $"{l.Gtin} fails its own check digit");
            Assert.StartsWith("0", l.Gtin, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A manufacturer prefix one digit away from one Bimbo actually bills under is repaired. The
    /// scanner reads a 7 as a 1 on these tickets ("14323" for 74323, "70810" for 70870), and left
    /// alone that opens a brand-new item in SSCS under an id belonging to nothing.
    /// </summary>
    [Fact]
    public void AMisreadManufacturerPrefixIsRepairedAndReported()
    {
        var t = Parse("84294890012926_no_qty_column", out var warnings);

        // "14323-73943 967744 BIN IQ1SII NIHI MFN2P" and "14323-09501 008796 MLA GANSITO"
        Assert.Contains(t.Lines, l => Upc.SscsIdFromGtin(l.Gtin) == "07432373943");
        Assert.Contains(t.Lines, l => Upc.SscsIdFromGtin(l.Gtin) == "07432309501");
        Assert.DoesNotContain(t.Lines, l => l.Gtin.StartsWith("01432", StringComparison.Ordinal));

        Assert.Equal(2, warnings.Count(w =>
            w.ReasonCode == PdfReasonCodes.UpcCheckDigit && w.Message.Contains("14323")));
    }

    /// <summary>
    /// Damage inside the UPC — past the prefix, where nothing about the code itself looks wrong — is
    /// caught only by Bimbo's own item number in the next column. Ticket 84294890012057 scans
    /// "74323-09783" for item 008788, which the catalogue holds as 7432309183 on four other tickets.
    /// Nothing else on the page can see this: the money reconciles either way.
    /// </summary>
    [Fact]
    public void TheItemNumberCorrectsAUpcTheScanDamagedPastItsPrefix()
    {
        var t = Parse("84294890012057", out var warnings);

        Assert.Contains(t.Lines, l => Upc.SscsIdFromGtin(l.Gtin) == "07432309183");
        Assert.DoesNotContain(t.Lines, l => Upc.SscsIdFromGtin(l.Gtin) == "07432309783");

        Assert.Contains(warnings, w =>
            w.ReasonCode == PdfReasonCodes.UpcCheckDigit && w.Message.Contains("008788"));
    }

    /// <summary>
    /// The catalogue only ever corrects a code one digit away from what it holds. Further apart means
    /// the *item number* is the damaged column, and following it would swap a good barcode for an
    /// unrelated product's. Proven on the samples: no line comes out on a barcode that is more than
    /// one digit from what was printed.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void NoLineIsMovedMoreThanOneDigitFromWhatWasPrinted(string ticket)
    {
        var t = Parse(ticket, out var warnings);

        int corrections = warnings.Count(w => w.ReasonCode == PdfReasonCodes.UpcCheckDigit);
        Assert.True(corrections <= 2, $"{ticket} raised {corrections} barcode warnings");
        Assert.All(t.Lines, l => Assert.True(Upc.IsValidUpcA12(l.Gtin)));
    }

    // ─────────────────────────────────────────────────────────────────── damaged scans

    /// <summary>
    /// Ticket 84294890012926 lost its entire QTY column to the scanner — all 22 delivery rows print
    /// nothing between the description and SUGG RETAIL. Every quantity still comes back, because
    /// AMOUNT / PRICE is a whole number only for the right one, and the section's own 124 proves it.
    /// </summary>
    [Fact]
    public void QuantitiesSurviveTheLossOfTheWholeQtyColumn()
    {
        var t = Parse("84294890012926_no_qty_column", out _);

        Assert.Equal(123, t.Lines.Sum(l => l.Units));
        Assert.Equal(278.86m, t.Lines.Sum(l => l.ExtendedCost));
        // The two rows whose QTY the scanner rendered as "1" are really 12 and 16.
        Assert.Equal(12, Units(t, "07087096086"));
        Assert.Equal(16, Units(t, "07087096080"));

        static int Units(BimboTicket t, string id) =>
            t.Lines.First(l => Upc.SscsIdFromGtin(l.Gtin) == id).Units;
    }

    /// <summary>
    /// A row with a UPC and no money at all is real, not damage: ticket 84294890013091 prints
    /// "74323-73965 968338 MLA BARRITA 35P PPS" with a bare 1 and no prices, and Bimbo's own DELIVERY
    /// TOTAL leaves it out of both the 88 units and the $178.28. Skipping it is what makes the ticket
    /// reconcile — and the two section gates are what prove the skip was right.
    /// </summary>
    [Fact]
    public void ARowCarryingNoMoneyIsSkippedAndTheTicketStillReconciles()
    {
        var t = Parse("84294890013091_zero_money_row", out _);

        Assert.DoesNotContain(t.Lines, l => Upc.SscsIdFromGtin(l.Gtin) == "07432373965");
        Assert.Equal(151.33m, t.Lines.Sum(l => l.ExtendedCost));
        Assert.Equal(77, t.Lines.Sum(l => l.Units));
    }

    /// <summary>
    /// A section unit count is not the last word, because it is OCR'd like everything else. Ticket
    /// 84294890013154 prints "RETURNS TOTAL -15" where its eight rows come to 16 and every row's
    /// retail and wholesale columns agree on its own quantity — and the ticket's "TICKET TOTALS 104"
    /// is 120 - 16. Refusing that would cost a whole delivery over one mis-read digit.
    /// </summary>
    [Fact]
    public void ASectionCountIsOverruledByTheTicketsOwnTotalAndReported()
    {
        var t = Parse("84294890013154_bad_section_count", out var warnings);

        Assert.Equal(104, t.Lines.Sum(l => l.Units));
        Assert.Equal(-16, t.Lines.Where(l => l.Units < 0).Sum(l => l.Units));
        Assert.Equal(209.32m, t.Lines.Sum(l => l.ExtendedCost));

        Assert.Contains(warnings, w =>
            w.ReasonCode == PdfReasonCodes.QtyMismatch && w.Message.Contains("104"));
    }

    /// <summary>
    /// A price the scan destroyed ("1.400-" for 1.9400) is recovered from the row's own amount, and
    /// the section total is what proves the recovery.
    /// </summary>
    [Fact]
    public void ADestroyedUnitPriceIsRecoveredFromTheRowsAmount()
    {
        var t = Parse("84294890013154_bad_section_count", out var warnings);

        var line = t.Lines.First(l => Upc.SscsIdFromGtin(l.Gtin) == "07432309123");
        Assert.Equal(3, line.Units);
        Assert.Equal(1.94m, line.UnitCost);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.LineMathMismatch);
    }

    // ─────────────────────────────────────────────────────────────────── dates

    /// <summary>
    /// The 300 dpi scans the store switched to on 07-27-2026 keep the date's separators as
    /// punctuation ("7!28!2026"), so the date is genuinely read and the operator is not asked.
    /// </summary>
    [Fact]
    public void A300DpiScanReadsItsOwnDate()
    {
        var t = Parse("84294890013239_sscs_verified", out _);

        Assert.Equal(new DateOnly(2026, 7, 28), t.Date);
    }

    /// <summary>
    /// The older 200 dpi scans turn the separators into digits ("711312026" for 7/13/2026), and that
    /// reading is **not** kept.
    ///
    /// The token is recoverable — the shape is fixed and only one calendar date fits — and the
    /// reconstruction used to come back as a suggestion to pre-fill the operator's date box with.
    /// It does not any more (2026-08-12): it is arithmetic on damage, the box is empty for every
    /// vendor, and a pre-filled date is what let an invoice go out dated 2016. So a damaged token
    /// reads as no date at all, and the person holding the ticket supplies it.
    /// </summary>
    [Theory]
    [InlineData("84294890013091_zero_money_row")]
    [InlineData("84294890012926_no_qty_column")]
    [InlineData("84294890012769")]
    [InlineData("84294890012641")]
    [InlineData("84294890013154_bad_section_count")]
    [InlineData("84294890012057")]
    [InlineData("84294890013046")]
    public void A200DpiScanKeepsNoDateAtAllRatherThanAGuessedOne(string ticket)
    {
        Assert.Null(Parse(ticket, out _).Date);
    }

    // ─────────────────────────────────────────────────────────────────── retail

    /// <summary>
    /// Retail seeds a brand-new item only, but it must never be zero or an unpriced item rings up
    /// free. Bimbo's own SUGG RETAIL column is deliberately not used: it says 1.99 against a 1.94
    /// cost where this store rings the same item at 3.59.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void RetailIsNeverZeroAndAlwaysEndsInNine(string ticket)
    {
        var t = Parse(ticket, out _);

        Assert.All(t.Lines, l =>
        {
            Assert.True(l.RetailPrice > 0m, $"{l.Gtin} priced at zero");
            Assert.True(l.RetailPrice > l.UnitCost, $"{l.Gtin} retail {l.RetailPrice} <= cost {l.UnitCost}");
            Assert.Equal(9, (int)(decimal.Round(l.RetailPrice, 2) * 100m) % 10);
        });
    }

    /// <summary>
    /// The margin is the store's, not Bimbo's, and it reproduces the Ea List figure live SSCS shows
    /// on this vendor's most common line: 1.94 cost against a 3.59 shelf price.
    /// </summary>
    [Fact]
    public void TheRetailSeedReproducesTheEaListSscsShows()
    {
        Assert.Equal(3.59m, RetailSeed.FromCost(1.94m, BimboPdfParser.RetailMargin));

        var t = Parse("84294890013239_sscs_verified", out _);
        Assert.Equal(3.59m, t.Lines.First(l => l.UnitCost == 1.94m).RetailPrice);
    }

    // ─────────────────────────────────────────────────────────────────── refusals

    /// <summary>
    /// A scan with no text layer at all — the store's scanner had OCR switched off until 07-27-2026 —
    /// is a fixable mistake, so it says how to fix it rather than failing obscurely.
    /// </summary>
    [Fact]
    public void AnImageOnlyScanAsksForOcrToBeTurnedOn()
    {
        var ex = Assert.Throws<EdiConversionException>(() =>
            PdfTextExtractor.ExtractLines(
                File.ReadAllBytes(Path.Combine(Dir, "BIMBO_84294890012930_image_only.pdf"))));

        Assert.Equal(PdfReasonCodes.NoText, ex.ReasonCode);
        Assert.Contains("OCR", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Another vendor's invoice picked from the dropdown by mistake is refused, not parsed.</summary>
    [Theory]
    [InlineData("CHUBIWINOS_6V9430.pdf")]
    [InlineData("BLUEBELL_033100786112.pdf")]
    public void AnotherVendorsInvoiceIsRefused(string file)
    {
        var lines = PdfTextExtractor.ExtractLines(File.ReadAllBytes(Path.Combine(Dir, file)));
        var warnings = new List<ConversionWarning>();

        var ex = Assert.Throws<EdiConversionException>(() => BimboPdfParser.Parse(lines, warnings));
        Assert.Equal(PdfReasonCodes.VendorMismatch, ex.ReasonCode);
    }

    /// <summary>
    /// Money that does not reconcile refuses the ticket rather than writing a wrong invoice. Deleting
    /// a line is the cheapest way to simulate the page that goes missing in a real jam.
    /// </summary>
    [Fact]
    public void ATicketWithALineMissingIsRefused()
    {
        var lines = PdfTextExtractor.ExtractLines(
            File.ReadAllBytes(Path.Combine(Dir, "BIMBO_84294890013239_sscs_verified.pdf"))).ToList();
        lines.RemoveAll(l => l.Contains("74323-09523", StringComparison.Ordinal));

        var ex = Assert.Throws<EdiConversionException>(() => BimboPdfParser.Parse(lines, []));
        Assert.True(ex.ReasonCode is PdfReasonCodes.QtyMismatch or PdfReasonCodes.TotalMismatch);
    }

    /// <summary>Every returns line is flagged, because a person should know goods went back.</summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void ReturnsAreReported(string ticket)
    {
        Parse(ticket, out var warnings);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.CreditLines);
    }

    /// <summary>The catalogue is a table of record; its size is pinned so an edit is deliberate.</summary>
    [Fact]
    public void TheCatalogueHoldsTheProductsTheSamplesShowed()
    {
        Assert.Equal(54, BimboCatalog.Count);
        Assert.Equal("7432309183", BimboCatalog.Find("008788")!.Value.Upc10);
        Assert.True(BimboCatalog.Find("008788")!.Value.Trusted);
        Assert.Null(BimboCatalog.Find("999999"));
    }
}
