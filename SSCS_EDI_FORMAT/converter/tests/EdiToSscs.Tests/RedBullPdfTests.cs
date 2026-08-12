using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Parser proofs for the Red Bull path, run against the real sample tickets with their scan damage
/// intact. Nothing here is hand-written sample text.
///
/// The theme is that on this vendor no single printed figure can be trusted — not the row, not the
/// column beside it, not even the ticket's own total, because the scanner mis-renders a glyph the
/// same way everywhere it appears. What the parser is allowed to do is take a figure two *different*
/// kinds of arithmetic agree on, and ask a person for the rest.
/// </summary>
public class RedBullPdfTests
{
    private const string Dir = @"Fixtures\PDF";

    private static RedBullTicket Parse(string name, out List<ConversionWarning> warnings)
    {
        warnings = [];
        var pages = PdfTextExtractor.ExtractPages(File.ReadAllBytes(Path.Combine(Dir, $"REDBULL_{name}.pdf")));
        return RedBullPdfParser.Parse(pages, warnings);
    }

    private static RedBullTicket Parse(string name) => Parse(name, out _);

    /// <summary>
    /// What the paper prints for the figures a ticket's scan destroyed, read off the picture the
    /// prompt puts on screen \u2014 which is the whole point of the prompt carrying one.
    ///
    /// Every entry here was read from the scan itself, and the two on the verified ticket are
    /// confirmed a second time by the live SSCS screen: WNTR FUJAPP at $106.90 and the PINK credit at
    /// $4.45. Anything asked about that is not in this table fails the test rather than being
    /// answered with something plausible, so a change that starts asking new questions cannot slip
    /// through by having them quietly answered.
    /// </summary>
    private static readonly Dictionary<(string Description, LineField Field), string> PaperSays = new()
    {
        // 2030211863_sscs_verified - PRICE misread $53.46 for $53.45, so nothing confirmed TOTAL
        { ("WNTB FUJAPP 1202 LS", LineField.Amount), "106.90" },
        // 2030211863_sscs_verified - the credit's own TOTAL read ($1.46) for a printed ($4.45)
        { ("PINK 120215", LineField.Amount), "4.45" },
        // 2034630731_roa_misread - TOTAL read $63.45 for a printed $53.45
        { ("ICED 1202", LineField.Amount), "53.45" },
        // 2034630731_roa_misread - UNITS read 46 for a printed 48
        { ("RED BULL 120218", LineField.Units), "48" },
    };

    /// <summary>Answers a prompt from the paper, and fails loudly on a question nobody expected.</summary>
    private static string FromThePaper(RedBullLine line, LineField field) =>
        PaperSays.TryGetValue((line.Description, field), out string? answer)
            ? answer
            : throw new Xunit.Sdk.XunitException(
                $"unexpected question: {field} for \"{line.Description}\" (line {line.Position}). "
                + "Either the parser started asking something new, or the paper needs reading.");

    // ───────────────────────────────────────────────────── the live SSCS round trip

    /// <summary>
    /// The 11/24/2025 ticket, which SSCS holds as invoice 112425112379D: ten line items, 145 total
    /// units, $342.50. The PDF is two pages — a $367.00 delivery and a $24.50 credit with its own
    /// invoice number — and SSCS booked them as ONE paidout, which is why the whole PDF is one
    /// invoice here rather than two files the way Blue Bell works.
    /// </summary>
    [Fact]
    public void MatchesTheTenLinesLiveSscsShows()
    {
        var ticket = Parse("2030211863_sscs_verified");

        Assert.Equal(10, ticket.Lines.Count);
        Assert.Equal(145, ticket.Lines.Sum(l => l.Units!.Value));
        Assert.Equal(5, ticket.Lines.Count(l => l.IsCredit));

        // Exactly two figures on this ticket are asked about, and both are money the scan destroyed
        // with nothing on the row left to check it against. Every unit count reads and confirms.
        Assert.All(ticket.Lines, l => Assert.False(l.NeedsUnits || l.NeedsBarcode,
            $"line {l.Position} ({l.Description}) should not need the operator"));
        Assert.Equal(
            new[] { "WNTB FUJAPP 1202 LS", "PINK 120215" },
            ticket.Lines.Where(l => l.NeedsAmount).Select(l => l.Description).ToArray());
    }

    /// <summary>
    /// The Line Items tab, straight off the screenshot: SSCS's ID/PLU is the GTIN minus its last
    /// character, Qty is the ticket's UNITS column, and Ex Cost is what SSCS computes from the cost
    /// this file carries. A change that breaks any of these creates duplicate items or wrong money
    /// in the customer's live system.
    /// </summary>
    public static TheoryData<string, int, decimal> LiveSscsLineItems => new()
    {
        // ID/PLU shown by SSCS,  Qty,   Ex Cost
        { "61126999100",           24,   40.75m },
        { "61126954601",           24,   70.22m },
        { "61126913344",           12,   42.23m },
        { "61126900286",           48,  106.90m },
        { "61126981899",           48,  106.90m },
        { "61126918246",           -1,   -2.23m },
        { "61126900130",           -1,   -2.23m },
        { "61126900312",           -2,   -4.45m },
        { "61126932121",           -6,  -13.36m },
        { "61126981899",           -1,   -2.23m },
    };

    /// <summary>
    /// The id and the quantity have to be exactly what SSCS shows — those decide which item is
    /// touched and how much stock moves, and nothing downstream would question either.
    ///
    /// Ex Cost is allowed one cent. That is not slack for its own sake: it is the width of the one
    /// defect this vendor's scans have that no arithmetic on the page can close. The 8.4oz line is
    /// printed $40.75 in both the PRICE and TOTAL columns and the scanner reads $40.76 in both, so
    /// there is no second reading to compare against — see
    /// <see cref="TheOneCentTheScannerCostsIsFlaggedRatherThanHidden"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveSscsLineItems))]
    public void EachLineLandsOnTheItemAndQuantitySscsShows(string sscsId, int qty, decimal exCost)
    {
        var invoice = Parse("2030211863_sscs_verified")
            .ToInvoice("112425112379D", new DateOnly(2025, 11, 24), FromThePaper);

        var match = invoice.Lines.FirstOrDefault(l =>
            l.Gtin[..^1] == sscsId && l.Units == qty && Math.Abs(l.ExtendedCost - exCost) <= 0.01m);
        Assert.True(match is not null,
            $"no line landed on SSCS id {sscsId} with qty {qty} and Ex Cost {exCost:0.00}. Got: "
            + string.Join(", ", invoice.Lines.Select(l => $"{l.Gtin[..^1]}/{l.Units}/{l.ExtendedCost:0.00}")));
    }

    /// <summary>
    /// Nine of the ten lines come out exactly as SSCS holds them. The tenth is a cent high, and this
    /// test exists so that stops being a surprise: the ticket prints $40.75 in the PRICE column and
    /// $40.75 again in the TOTAL column, the scanner renders that glyph as a 6 in both, and two
    /// readings of the same glyph are not two readings. The ticket's own $342.50 is what shows it,
    /// and it is reported rather than silently patched — a penny is flagged and shipped, not fixed
    /// by guessing which of several rows to change.
    /// </summary>
    [Fact]
    public void TheOneCentTheScannerCostsIsFlaggedRatherThanHidden()
    {
        var invoice = Parse("2030211863_sscs_verified")
            .ToInvoice("112425112379D", new DateOnly(2025, 11, 24), FromThePaper);

        var exact = LiveSscsLineItems
            .Select(row => ((string)row[0], (int)row[1], (decimal)row[2]))
            .Count(want => invoice.Lines.Any(l =>
                l.Gtin[..^1] == want.Item1 && l.Units == want.Item2 && l.ExtendedCost == want.Item3));

        Assert.Equal(9, exact);
        Assert.Equal(342.51m, invoice.LineTotal);
        Assert.Contains(342.50m, Parse("2030211863_sscs_verified").PrintedTotals);
    }

    /// <summary>
    /// The one line SSCS costs at 2.2271 rather than the $2.23 the credit page prints. The credit
    /// page rounds its unit rate to the cent, so 2 returned units come to $4.45 and not $4.44 — the
    /// exact rate has to come back off the delivery page.
    /// </summary>
    [Fact]
    public void ACreditIsPricedFromTheDeliveryPageRateNotItsOwnRoundedOne()
    {
        var invoice = Parse("2030211863_sscs_verified")
            .ToInvoice("112425112379D", new DateOnly(2025, 11, 24), FromThePaper);
        var pink = invoice.Lines.Single(l => l.Gtin == "611269003123" && l.Units == -2);

        // The paper prints ($4.45) and the scan read ($1.46), so this one is asked about now rather
        // than rebuilt from the delivery page's rate. What matters is unchanged: SSCS holds -2 at
        // 2.2271, which is 2 x 53.45/24 and not 2 x the credit page's rounded $2.22.
        Assert.Equal(-4.45m, invoice.Lines.Single(l => l.Gtin == "611269003123" && l.Units == -2).ExtendedCost);
        Assert.Equal(2.2250m, pink.UnitCost);
    }

    // ─────────────────────────────────────────────────────────── the sample tickets

    /// <summary>Every readable sample, with the line count the paper carries.</summary>
    public static TheoryData<string, int> Samples => new()
    {
        { "2030211863_sscs_verified",       10 },
        { "2036629370_clean_300dpi",         6 },
        { "2036629372_300dpi_damaged_money", 5 },
        { "2034630731_roa_misread",         11 },
        { "2029902123",                     17 },
        { "2036620317_bs10",                 7 },
        { "2035260898",                     15 },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void ReadsEveryProductLineOnTheTicket(string name, int lines) =>
        Assert.Equal(lines, Parse(name).Lines.Count);

    /// <summary>
    /// A clean 300 dpi ticket needs nobody: six lines, $305.88, matching its printed INVOICE exactly.
    /// This is what the scanner produces now, and it is the case that has to stay silent — a vendor
    /// that prompts on a good scan trains people to click through prompts.
    /// </summary>
    [Fact]
    public void AGoodScanConvertsWithNoQuestionsAsked()
    {
        var ticket = Parse("2036629370_clean_300dpi", out var warnings);

        Assert.Equal(6, ticket.Lines.Count);
        Assert.All(ticket.Lines, l => Assert.False(l.NeedsAnything));
        Assert.Equal(305.88m, ticket.Lines.Sum(l => l.Amount!.Value));
        Assert.Contains(305.88m, ticket.PrintedTotals);
        Assert.DoesNotContain(warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    /// <summary>
    /// One sample prints ROA $509.14 where its INVOICE line says $599.14, and the eleven rows come
    /// to $599.14. Both figures are the ticket's own claim about itself, so the parse is only in
    /// trouble when the lines match neither — keeping them as a set is what stops a damaged ROA
    /// rejecting a perfectly good ticket.
    /// </summary>
    [Fact]
    public void ADamagedRoaDoesNotCondemnATicketTheInvoiceLineAgreesWith()
    {
        var ticket = Parse("2034630731_roa_misread");
        var invoice = ticket.ToInvoice("TEST", new DateOnly(2026, 7, 1), FromThePaper);

        Assert.Equal(599.14m, invoice.LineTotal);
        Assert.Contains(599.14m, ticket.PrintedTotals);
        Assert.Contains(509.14m, ticket.PrintedTotals);
    }

    // ────────────────────────────────────────────────────────────────── the barcode

    /// <summary>
    /// Every barcode the parser accepts has to satisfy its own check digit and carry Red Bull's
    /// prefix. That is the whole reason a repaired code can be trusted at all, and it is what Bimbo
    /// has no equivalent of.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryAcceptedBarcodeIsARealRedBullUpc(string name, int _)
    {
        foreach (var line in Parse(name).Lines.Where(l => l.Gtin is not null))
        {
            Assert.True(Upc.IsValidUpcA12(line.Gtin!), $"{line.Gtin} fails its check digit");
            Assert.StartsWith(RedBullCatalog.Prefix, line.Gtin!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One 300 dpi sample reads every barcode on the page as "611260..." because the scanner turned
    /// that one 9 into a 0. Anchoring the search on six digits instead of five would find nothing on
    /// that whole ticket.
    /// </summary>
    [Fact]
    public void RecoversATicketWhoseWholePrefixColumnIsMisread()
    {
        var ticket = Parse("2036629370_clean_300dpi");
        Assert.All(ticket.Lines, l => Assert.NotNull(l.Gtin));
        Assert.Contains(ticket.Lines, l => l.Gtin == "611269818994");   // read as 611260818004
    }

    [Fact]
    public void TheCatalogueOnlyHoldsRealUpcs()
    {
        Assert.All(RedBullCatalog.All, e =>
        {
            Assert.True(Upc.IsValidUpcA12(e.Gtin), $"{e.Gtin} ({e.Description}) fails its check digit");
            Assert.StartsWith(RedBullCatalog.Prefix, e.Gtin, StringComparison.Ordinal);
            Assert.Contains(e.Pack, new[] { 4, 6, 12, 24, 48 });
        });
        Assert.Equal(RedBullCatalog.All.Select(e => e.Gtin).Distinct().Count(), RedBullCatalog.Count);
    }

    /// <summary>Red Bull's item number is matched as a suffix, because the scan turns the "RB" into
    /// digits — "RB4816" arrives as "984816", "O84816" or "1104816".</summary>
    [Theory]
    [InlineData("984816", "611269818994")]
    [InlineData("1104816", "611269818994")]
    [InlineData("110240073", "611269001303")]
    [InlineData("5836463", "611269133448")]
    public void FindsAProductFromTheScannersVersionOfItsItemNumber(string scanned, string gtin) =>
        Assert.Equal(gtin, RedBullCatalog.FindByItemNumber(scanned)?.Gtin);

    /// <summary>Three digits is not enough to identify a product, so it must not try.</summary>
    [Fact]
    public void RefusesToIdentifyAProductFromTooFewDigits() =>
        Assert.Null(RedBullCatalog.FindByItemNumber("816"));

    // ───────────────────────────────────────────────────────────── units and money

    /// <summary>
    /// UNITS is individual cans, which is what SSCS wants for Qty — never cases. The verified ticket
    /// carries 8 cases and 156 units on its delivery page, and SSCS holds the 156.
    /// </summary>
    [Fact]
    public void CountsIndividualUnitsAndNotCases()
    {
        var ticket = Parse("2030211863_sscs_verified");
        Assert.Equal(156, ticket.Lines.Where(l => !l.IsCredit).Sum(l => l.Units!.Value));
    }

    /// <summary>
    /// A returned line goes out as negative units at a positive cost, on the same invoice as the
    /// deliveries — the shape SSCS actually booked.
    /// </summary>
    [Fact]
    public void ReturnsAreNegativeUnitsOnTheSameInvoice()
    {
        var invoice = Parse("2030211863_sscs_verified")
            .ToInvoice("112425112379D", new DateOnly(2025, 11, 24), FromThePaper);

        Assert.Equal(5, invoice.Lines.Count(l => l.Units < 0));
        Assert.All(invoice.Lines, l => Assert.True(l.UnitCost > 0m, "unit costs are always positive"));
    }

    /// <summary>
    /// A returned line stays negative however the operator types its amount.
    ///
    /// This is the PINK credit on the verified ticket, whose TOTAL the scan destroyed — the prompt in
    /// front of the operator shows "($4.45)". Brackets, a minus, or plain digits all land on the same
    /// line, because the sign comes from the row sitting on a credit page and never from the typing.
    /// SSCS holds this one at -2 @ 2.2271.
    /// </summary>
    [Theory]
    [InlineData("4.45")]
    [InlineData("-4.45")]
    [InlineData("(4.45)")]
    [InlineData("($4.45)")]
    [InlineData("$4.45")]
    public void ATypedCreditGoesInNegativeWhicheverWayItIsWritten(string typed)
    {
        var invoice = Parse("2030211863_sscs_verified").ToInvoice(
            "112425112379D", new DateOnly(2025, 11, 24),
            (line, field) => line.Description == "PINK 120215" ? typed : FromThePaper(line, field));

        var pink = invoice.Lines.Single(l => l.Gtin == "611269003123" && l.Units == -2);
        Assert.Equal(-4.45m, pink.ExtendedCost);
        Assert.True(pink.UnitCost > 0m, "a return is a negative quantity at a positive cost");
    }

    /// <summary>Retail is seeded at 45% and always ends in a 9 — the store's rule for every vendor.</summary>
    [Fact]
    public void SeedsARetailThatAlwaysEndsInNine()
    {
        var invoice = Parse("2036629370_clean_300dpi")
            .ToInvoice("TEST", new DateOnly(2026, 8, 3), FromThePaper);

        Assert.All(invoice.Lines, l =>
        {
            Assert.True(l.RetailPrice > l.UnitCost, "a seeded retail must be above cost");
            Assert.Equal(9, (int)(decimal.Round(l.RetailPrice * 100m) % 10m));
        });
    }

    // ──────────────────────────────────────────────────────────────────────── dates

    /// <summary>
    /// The verified ticket is dated 11/24/2025 and its text layer says 11/24/2026 — the same 5-read-
    /// as-6 that turns a 75320 ZIP into 76320. A ticket cannot be dated in the future, so the parser
    /// declines to use it and offers the year before as a suggestion for the operator to confirm.
    /// </summary>
    [Fact]
    public void WillNotPostAFutureDateButOffersTheObviousCorrection()
    {
        var ticket = Parse("2030211863_sscs_verified", out var warnings);

        Assert.Null(ticket.Date);
        Assert.Equal(new DateOnly(2025, 11, 24), ticket.SuggestedDate);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.NoDate);
    }

    /// <summary>A date that is not in the future is used as printed, with nothing to confirm.</summary>
    [Fact]
    public void UsesAPlausibleDateWithoutAsking()
    {
        var ticket = Parse("2036629370_clean_300dpi");
        Assert.Equal(new DateOnly(2026, 8, 3), ticket.Date);
        Assert.Null(ticket.SuggestedDate);
    }

    // ─────────────────────────────────────────────────────────────── refusing badly

    /// <summary>
    /// A PDF with no text layer at all — the scanner's OCR was switched off — is refused with the
    /// fix rather than a parse error.
    /// </summary>
    [Fact]
    public void TellsTheOperatorToTurnOcrBackOn()
    {
        var ex = Assert.Throws<EdiConversionException>(() => Parse("image_only"));
        Assert.Equal(PdfReasonCodes.NoText, ex.ReasonCode);
        Assert.Contains("OCR", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
