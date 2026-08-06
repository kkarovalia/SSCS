using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Parser proofs for the Chubiwinos path, run against the eight real sample invoices (03/14/26 to
/// 07/03/26) with their scan damage intact. Nothing here is hand-written sample text.
///
/// The theme is that the invoice checks itself. Every one of these files has something wrong with
/// its text layer — a mangled price, a mangled extended, a mangled total, a mangled barcode — and
/// every one of them still has to come out with the money and the unit count the paper prints.
/// </summary>
public class ChubiwinosPdfTests
{
    private const string Dir = @"Fixtures\PDF";

    /// <summary>Every sample, with the total and unit count printed on the paper.</summary>
    public static TheoryData<string, decimal, int, int> Samples => new()
    {
        // file,           printed total, units, product lines
        { "0V8790_faint_header", 431.43m, 167, 7 },
        { "6V9069", 456.18m, 162, 7 },
        { "6V9141", 584.82m, 198, 10 },
        { "6V9196", 448.44m, 156, 6 },
        { "6V9244", 452.90m, 150, 9 },
        { "6V9293", 431.70m, 150, 6 },
        { "6V9335", 558.65m, 181, 10 },   // 178 sold + 3 dumped
        { "6V9389", 460.23m, 159, 8 },    // 158 sold + 1 dumped
        { "6V9430", 623.14m, 206, 9 },
    };

    /// <summary>The same eight files, for the checks that do not need the printed figures.</summary>
    public static TheoryData<string> SampleNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in new[]
                     { "0V8790_faint_header", "6V9069", "6V9141", "6V9196", "6V9244", "6V9293", "6V9335", "6V9389", "6V9430" })
                data.Add(name);
            return data;
        }
    }

    private static ChubiwinosTicket Parse(string invoice, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return ChubiwinosPdfParser.Parse(
            PdfTextExtractor.ExtractLines(File.ReadAllBytes(Path.Combine(Dir, $"CHUBIWINOS_{invoice}.pdf"))),
            warnings);
    }

    /// <summary>
    /// The whole point of the parser: whatever the scan did to the page, the lines add up to the
    /// invoice's own total to the penny, and the quantities to its own unit count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void EverySampleReconcilesToItsOwnPrintedTotalAndUnitCount(
        string invoice, decimal total, int units, int lineCount)
    {
        var ticket = Parse(invoice, out _);

        Assert.Equal(total, ticket.Total);
        Assert.Equal(total, ticket.Lines.Sum(l => l.ExtendedCost));
        Assert.Equal(units, ticket.Lines.Sum(l => Math.Abs(l.Units)));
        Assert.Equal(lineCount, ticket.Lines.Count);
    }

    /// <summary>
    /// A unit cost that came out of a division is worthless if it is not what the invoice charged,
    /// and a zero retail would let a new item ring up free at the register.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EveryLineHasAPositiveCostAndAPricedRetail(string invoice)
    {
        foreach (var line in Parse(invoice, out _).Lines)
        {
            Assert.True(line.UnitCost > 0m, $"{line.Description} has cost {line.UnitCost}");
            Assert.True(line.RetailPrice > line.UnitCost, $"{line.Description} sells at {line.RetailPrice}");
            Assert.NotEqual(0, line.Units);
        }
    }

    /// <summary>
    /// What live SSCS holds for invoice 6V9389, read off the Line Items tab after the store keyed it
    /// by hand: eight lines, the last of them the DUMP SECTION credit. These are the figures the
    /// converter has to reproduce.
    ///
    /// Two of the eight ID/PLUs the store keyed do NOT come from the barcode Chubiwinos prints —
    /// 245 PAPA CRUJIENTE went in under 750047804595 and 299 DORITOS FLAMING HOT under 750047802555,
    /// where the printed barcodes lead to 750101118049 and 750047801211. Both printed codes pass
    /// their check digit, so the parser sends them as printed and those two lines will arrive as new
    /// items the first time. That is a decision about the store's item file, not a parsing bug; it is
    /// recorded in analysis/vendors/CHUBIWINOS.md so it is not "fixed" by accident.
    /// </summary>
    [Fact]
    public void MatchesWhatLiveSscsHoldsForInvoice6V9389()
    {
        var ticket = Parse("6V9389", out _);

        Assert.Equal(new DateOnly(2026, 6, 15), ticket.Date);   // SSCS Paidout tab: 6/15/2026
        Assert.Equal(460.23m, ticket.Total);                    // SSCS Verify Total: 460.23

        // ID/PLU, Qty, Ea Cost and Ex Cost, in the order SSCS lists them.
        (string Id, int Qty, decimal Cost, decimal Extended)[] expected =
        [
            ("750047804420", 20, 2.79m, 55.80m),   // COLMILLOS
            ("750101113086", 24, 2.79m, 66.96m),   // TOSTITOS VERDE MEXICAN
            ("750101111451", 30, 2.79m, 83.70m),   // RANCHERITOS MEXICAN
            ("750101113106", 24, 2.79m, 66.96m),   // DORITOS MEXICAN
            ("750101118049", 30, 2.79m, 83.70m),   // the store keyed 750047804595 by hand
            ("750047801211", 18, 3.89m, 70.02m),   // the store keyed 750047802555 by hand
            ("60472200818",  12, 2.99m, 35.88m),   // LEO CHI CHA 3.53
            ("750101116766", -1, 2.79m, -2.79m),   // SABRITAS ADOBADAS, the dumped unit
        ];

        Assert.Equal(expected.Length, ticket.Lines.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            var line = ticket.Lines[i];
            Assert.Equal(expected[i].Id, Upc.SscsIdFromGtin(line.Gtin!));
            Assert.Equal(expected[i].Qty, line.Units);
            Assert.Equal(expected[i].Cost, line.UnitCost);
            Assert.Equal(expected[i].Extended, line.ExtendedCost);
        }
    }

    /// <summary>
    /// The retail seed, pinned against the Ea List column of that same SSCS screen. A 2.79 cost shows
    /// as 5.59 there, and 50% is the margin that reproduces it — which is why this vendor does not
    /// use the 40% the others do.
    /// </summary>
    [Fact]
    public void RetailSeedReproducesTheEaListSscsShows()
    {
        Assert.Equal(5.59m, RetailSeed.FromCost(2.79m, ChubiwinosPdfParser.RetailMargin));
        Assert.Equal(4.99m, RetailSeed.FromCost(2.98m));   // the 40% rule is untouched

        var colmillos = Parse("6V9389", out _).Lines[0];
        Assert.Equal(2.79m, colmillos.UnitCost);
        Assert.Equal(5.59m, colmillos.RetailPrice);
    }

    /// <summary>
    /// Invoice 6V9430's text layer reads "$03.70" for two lines that charge $83.70. Both readings are
    /// possible on their own; only one pair adds up to the invoice, and that is how they are settled.
    /// </summary>
    [Fact]
    public void TwoRowsWithAMangledExtendedAreRecoveredFromTheInvoiceTotal()
    {
        var ticket = Parse("6V9430", out var warnings);

        foreach (string product in new[] { "7501011151062", "7501011167674" })   // FRITOS CHORIZO, RUFFLES QUESO
        {
            var line = ticket.Lines.Single(l => l.Gtin == product);
            Assert.Equal(30, line.Units);
            Assert.Equal(2.79m, line.UnitCost);
            Assert.Equal(83.70m, line.ExtendedCost);
        }

        Assert.Equal(2, warnings.Count(w => w.ReasonCode == PdfReasonCodes.LineMathMismatch));
    }

    /// <summary>
    /// Invoice 6V9293 lost a price instead: "$169" where the paper reads $3.89. One money column is
    /// not enough to say whether it is the price or the extended, so both readings go to the same
    /// arithmetic that settles a damaged pair.
    /// </summary>
    [Fact]
    public void ARowWithOnlyOneReadableMoneyColumnIsSettledTheSameWay()
    {
        var line = Parse("6V9293", out _).Lines.Single(l => l.Gtin == "7501011154865");   // PAKETAXO BOTANERO

        Assert.Equal(12, line.Units);
        Assert.Equal(3.89m, line.UnitCost);
        Assert.Equal(46.68m, line.ExtendedCost);
    }

    /// <summary>
    /// Invoice 6V9244 lost BOTH columns on one row — "$2.19 $63.70" for $2.79 and $83.70 — so no
    /// reading of it is right and the leftover has to supply the answer. 452.90 less the eight rows
    /// that do reconcile is 83.70, which over 30 units is the printed 2.79 exactly.
    /// </summary>
    [Fact]
    public void ARowWithBothColumnsDamagedIsRecoveredFromWhatIsLeftOver()
    {
        var ticket = Parse("6V9244", out var warnings);
        var line = ticket.Lines.Single(l => l.Gtin == "7501011167667");   // PAPAS ADOBADAS

        Assert.Equal(30, line.Units);
        Assert.Equal(2.79m, line.UnitCost);
        Assert.Equal(83.70m, line.ExtendedCost);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.LineMathMismatch);
    }

    /// <summary>
    /// Invoice 6V9141's text layer says "MET AMOUNT: $684.82" for a $584.82 invoice. TOTAL DUE and
    /// INVOICE BALANCE outvote it, and the operator is told the three disagreed.
    /// </summary>
    [Fact]
    public void ADamagedTotalIsOutvotedByTheOtherTwoPlacesItIsPrinted()
    {
        var ticket = Parse("6V9141", out var warnings);

        Assert.Equal(584.82m, ticket.Total);
        Assert.Contains(warnings, w =>
            w.ReasonCode == PdfReasonCodes.TotalMismatch && w.Message.Contains("684.82", StringComparison.Ordinal));
    }

    /// <summary>
    /// A barcode that fails its check digit is the one error the money cannot catch, so it is
    /// replaced from the catalogue rather than sent. 6V9069's FRITOS CHORIZO scans with its leading
    /// 7 read as a 1.
    /// </summary>
    [Fact]
    public void ABarcodeThatFailsItsCheckDigitIsReplacedFromTheCatalogue()
    {
        var ticket = Parse("6V9069", out var warnings);
        var line = ticket.Lines.Single(l => l.ItemNumber == "260");

        Assert.Equal("7501011151062", line.Gtin);
        Assert.Equal("1501011151062", line.PrintedCode);
        Assert.False(Upc.IsValidGtin(line.PrintedCode));
        Assert.Contains(warnings, w =>
            w.ReasonCode == PdfReasonCodes.UpcCheckDigit && w.Message.Contains("1501011151062", StringComparison.Ordinal));
    }

    /// <summary>
    /// A barcode that PASSES its check digit is never looked up — the arithmetic on the code itself
    /// outranks a record of what the vendor printed before. Across all eight samples, 28 of the 35
    /// distinct codes pass and go out untouched.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EveryLineGoesOutWithAValidWholeBarcode(string invoice)
    {
        foreach (var line in Parse(invoice, out _).Lines)
        {
            if (line.NeedsBarcode) continue;   // handed to the operator; covered by the pipeline tests
            Assert.True(Upc.IsValidGtin(line.Gtin), $"{line.Description} went out as {line.Gtin}");
            if (Upc.IsValidGtin(line.PrintedCode)) Assert.Equal(line.PrintedCode, line.Gtin);
        }
    }

    /// <summary>
    /// The two products the catalogue deliberately does not hold — 905 never scanned as more than
    /// eleven digits and 918 only ever scanned as a code that fails its check digit. Guessing either
    /// would silently redirect every future invoice for that product, so they go to the operator.
    /// </summary>
    [Fact]
    public void AProductWithNoTrustworthyCodeAnywhereIsHandedToTheOperator()
    {
        var ticket = Parse("6V9244", out _);

        Assert.Equal(["905", "918"], ticket.Lines.Where(l => l.NeedsBarcode).Select(l => l.ItemNumber));
        Assert.Equal("650156007331", ticket.Lines.Single(l => l.ItemNumber == "918").PrintedCode);

        // Everything else about those lines is known; only the barcode is missing.
        var winnuts = ticket.Lines.Single(l => l.ItemNumber == "905");
        Assert.Equal(12, winnuts.Units);
        Assert.Equal(2.49m, winnuts.UnitCost);
    }

    /// <summary>
    /// A DUMP SECTION line is a credit: negative units at a positive cost, exactly as SSCS holds the
    /// SABRITAS ADOBADAS line of 6V9389 (-1.00 @ 2.79 = -2.79).
    /// </summary>
    [Fact]
    public void DumpSectionLinesAreNegativeUnitsAtAPositiveCost()
    {
        var credit = Parse("6V9335", out _).Lines.Single(l => l.Units < 0);

        Assert.Equal(-3, credit.Units);
        Assert.Equal(2.79m, credit.UnitCost);
        Assert.Equal(-8.37m, credit.ExtendedCost);
        Assert.Equal("7500478044207", credit.Gtin);
    }

    /// <summary>Each sample's own printed date, slashes read as ones and all.</summary>
    [Theory]
    [InlineData("6V9069", 2026, 3, 14)]
    [InlineData("6V9141", 2026, 4, 1)]
    [InlineData("6V9196", 2026, 4, 15)]
    [InlineData("6V9244", 2026, 5, 1)]
    [InlineData("6V9293", 2026, 5, 14)]
    [InlineData("6V9335", 2026, 5, 29)]
    [InlineData("6V9389", 2026, 6, 15)]
    [InlineData("6V9430", 2026, 7, 3)]
    public void TheDateSurvivesTheSlashesBeingScannedAsOnes(string invoice, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), Parse(invoice, out _).Date);
    }

    /// <summary>
    /// Every PDF fixture in the repo, swept in one go: a Chubiwinos invoice is accepted and nothing
    /// else is. This is the guard on the two-marker vendor check — loosening it from "the vendor's
    /// name is on the page" bought robustness against a faint header, and this is what proves it did
    /// not also start swallowing other vendors' invoices.
    /// </summary>
    [Fact]
    public void OnlyChubiwinosInvoicesAreAccepted()
    {
        var seen = new List<string>();

        foreach (string path in Directory.GetFiles(Dir, "*.pdf").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            IReadOnlyList<string> lines;
            try { lines = PdfTextExtractor.ExtractLines(File.ReadAllBytes(path)); }
            catch (EdiConversionException) { continue; }   // image-only, no text layer to judge

            bool mismatch = false;
            try { ChubiwinosPdfParser.Parse(lines, []); }
            catch (EdiConversionException ex) { mismatch = ex.ReasonCode == PdfReasonCodes.VendorMismatch; }

            bool isChubiwinos = name.StartsWith("CHUBIWINOS_", StringComparison.Ordinal);
            Assert.Equal(isChubiwinos, !mismatch);
            if (!mismatch) seen.Add(name);
        }

        Assert.Equal(9, seen.Count);   // all nine samples, and nothing else in the folder
    }

    /// <summary>
    /// A real invoice whose vendor name the scan destroyed — the header reads
    /// <c>111181W INOS S&amp;G INC.</c>, so "CHUBI" is gone and a space has split "WINOS" in half.
    /// Neither half of the name is findable and the invoice is still perfectly good.
    ///
    /// This is the file that killed the first version of the vendor check, which required the literal
    /// string WINOS on the page. The header is the faintest part of a photographed thermal ticket
    /// while everything the parser actually needs sits lower down and is legible — 167 units,
    /// $431.43, reconciling exactly, with a damaged extended ($59.16 for $59.76) repaired on the way.
    /// </summary>
    [Fact]
    public void AnInvoiceWhoseVendorNameTheScanDestroyedIsStillParsed()
    {
        var lines = PdfTextExtractor.ExtractLines(
            File.ReadAllBytes(Path.Combine(Dir, "CHUBIWINOS_0V8790_faint_header.pdf")));

        Assert.Contains(lines, l => l.Contains("111181W INOS", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("WINOS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("CHUBI", StringComparison.OrdinalIgnoreCase));

        var warnings = new List<ConversionWarning>();
        var ticket = ChubiwinosPdfParser.Parse(lines, warnings);

        Assert.Equal(new DateOnly(2025, 11, 27), ticket.Date);
        Assert.Equal(431.43m, ticket.Total);
        Assert.Equal(431.43m, ticket.Lines.Sum(l => l.ExtendedCost));

        // "$59.16" where the paper charges $59.76, recovered from the invoice total.
        var doritos = ticket.Lines.Single(l => l.ItemNumber == "240");
        Assert.Equal(24, doritos.Units);
        Assert.Equal(2.49m, doritos.UnitCost);
        Assert.Equal(59.76m, doritos.ExtendedCost);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.LineMathMismatch);
    }

    /// <summary>
    /// The same invoice's summary band scans "TOTAL DUE" as "TOTAL OIIE", which no pattern is going
    /// to match. That costs one of the three votes for the total and nothing else — which is the
    /// whole reason the total is a vote rather than a lookup.
    /// </summary>
    [Fact]
    public void ATotalLabelTooDamagedToMatchCostsOneVoteAndNothingMore()
    {
        var lines = PdfTextExtractor.ExtractLines(
            File.ReadAllBytes(Path.Combine(Dir, "CHUBIWINOS_0V8790_faint_header.pdf")));

        Assert.Contains(lines, l => l.Contains("TOTAL OIIE", StringComparison.Ordinal));
        Assert.Equal(431.43m, ChubiwinosPdfParser.Parse(lines, []).Total);
    }

    /// <summary>
    /// A quantity is the one figure the money cannot check: misread it and the unit cost absorbs the
    /// difference, so the invoice balances to the penny while the store's stock is wrong. The printed
    /// "Totals:" unit count is what catches that, and it has to refuse rather than warn.
    /// </summary>
    [Fact]
    public void AQuantityThatContradictsThePrintedUnitCountIsRefused()
    {
        var lines = PdfTextExtractor
            .ExtractLines(File.ReadAllBytes(Path.Combine(Dir, "CHUBIWINOS_6V9196.pdf")))
            .Select(l => l.Replace("120gm 24 $2.79", "120gm 21 $2.79", StringComparison.Ordinal))
            .ToList();

        var ex = Assert.Throws<EdiConversionException>(() => ChubiwinosPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.QtyMismatch, ex.ReasonCode);
        Assert.Contains("156", ex.UserMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Damage the arithmetic cannot resolve must lose the file, not produce a guess. Here two rows
    /// are wrecked in a way no combination of their readings can reconcile.
    /// </summary>
    [Fact]
    public void DamageThatCannotBeReconciledRefusesTheFile()
    {
        var lines = PdfTextExtractor
            .ExtractLines(File.ReadAllBytes(Path.Combine(Dir, "CHUBIWINOS_6V9196.pdf")))
            .Select(l => l.Replace("$2.79 $83.70", "$2.71 $88.70", StringComparison.Ordinal))
            .ToList();

        var ex = Assert.Throws<EdiConversionException>(() => ChubiwinosPdfParser.Parse(lines, []));

        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    /// <summary>
    /// Every code on record has to be a real barcode. An invalid entry here would be worse than no
    /// entry at all: it would replace a code the scan got right.
    /// </summary>
    [Fact]
    public void EveryCatalogEntryIsAValidBarcode()
    {
        Assert.NotEmpty(ChubiwinosCatalog.All);
        foreach (var (item, code) in ChubiwinosCatalog.All)
        {
            Assert.True(Upc.IsValidGtin(code), $"item {item} is on record as {code}");
            Assert.Equal(3, item.Length);
        }
    }
}
