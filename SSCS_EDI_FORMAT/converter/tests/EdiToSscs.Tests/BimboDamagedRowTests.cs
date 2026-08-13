using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// A product row must survive a speck of ink landing on its code.
///
/// Sixteen of the store's tickets converted on 2026-08-12 and nine were refused. Eight of the nine
/// were one thing: a line the text layer did not recognise as a row at all, because a single
/// character in or in front of the sixteen-digit code band was scan debris. The row then left the
/// ticket with its units *and* its money, so the band count, the band amount and the ticket count all
/// disagreed at once, and the delivery was refused with a message about a quantity being mis-read.
///
/// The quiet part is what made it expensive. A line that is not a row is never offered to the second
/// reader and never asked about, so the picture could read the row perfectly and change nothing — the
/// same nine tickets failed identically with the AI switched on and switched off. Nothing in the
/// warnings, the log or the message named the line, and every figure the operator could see on the
/// paper was correct.
///
/// Every line below is transcribed from a real scan in that run.
/// </summary>
public class BimboDamagedRowTests
{
    /// <summary>
    /// A minimal ticket: one delivery row, closed by band and ticket totals that agree with it. The
    /// totals are stated by the caller rather than computed, because they are the gates under test —
    /// a row the parser loses fails them, which is exactly the symptom being pinned.
    /// </summary>
    private static List<string> Ticket(string row, int units, decimal amount) =>
    [
        "BIMBO BAKERIES USA, INC. - CASH STOPS",
        "**DELIVERIES**",
        "ITEM SUGG RETAIL WHOLESALE",
        "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
        row,
        $"DELIVERY TOTAL {units} {amount:0.00} {amount:0.00}",
        $"TICKET TOTALS {units} {amount:0.00} {amount:0.00}",
        "CASH DUE",
    ];

    private static BimboTicket Parse(List<string> lines, out List<ConversionWarning> warnings)
    {
        warnings = [];
        return BimboPdfParser.Parse(lines, warnings, ask: q => q.ItemNumber switch
        {
            // Printed truth from these real fixture rows. The operator supplies a UPC when legacy
            // OCR lost one; there is no product table to update and rebuild.
            "008784" => "7432309157",
            "008783" => "7432309145",
            "013529" => "7432373791",
            _ => "",
        });
    }

    // ──────────────────────────────────────────────────────────── a speck inside the code band

    /// <summary>
    /// The separator Bimbo prints between the two code columns is a space, but the scan renders it as
    /// a comma, an equals sign, an opening bracket or a letter. Only the space and the hyphen used to
    /// be allowed through, so each of these took a whole delivery down.
    /// </summary>
    [Theory]
    // ticket 84294890011242 - a comma where the space between the columns belongs
    [InlineData("70870-00910 ,007908 MB GIG CIN DON 107 6 3.49 20.94 3.0100 18.06", "7087000910", 6, 18.06)]
    // ticket 84294890010553 - the hyphen inside the UPC read as an equals sign
    [InlineData("70870=00909 007907 MB GNG FRST DON 10.5Z 2 3.49 6.98 3.0100 6.02", "7087000909", 2, 6.02)]
    // ticket 84294890011178 - an opening bracket eating the item number's leading zero
    [InlineData("74323-09398 (07805 MA PRINEP CRO SS8P 12 1.99 23.88 1.8900 22.68", "7432309398", 12, 22.68)]
    // ticket 84294890010942 - the hyphen inside the UPC read as the letter r
    [InlineData("7432r09157 008784 BIM CWBNITO SS2P FC 8 1.99 15.92 1.8900 15.12", "7432309157", 8, 15.12)]
    public void ASpeckInsideTheCodeBandNoLongerCostsTheWholeRow(
        string row, string upc10, int units, double amount)
    {
        var t = Parse(Ticket(row, units, (decimal)amount), out _);

        var line = Assert.Single(t.Lines);
        Assert.Equal(units, line.Units);
        Assert.StartsWith("0" + upc10, line.Gtin, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────── a speck in front of the code band

    /// <summary>
    /// Debris printed ahead of the code stopped the scan dead, because the reader gave up at the first
    /// letter it met. Two characters of slack is enough for every one the store's scans produced.
    /// </summary>
    [Theory]
    // ticket 84294890011242 - a stray vertical stroke read as a capital I
    [InlineData("I 74323-09164 008785 BIM ROLE CAN FS6P FC 2 3.99 7.98 3.0200 6.04", 2, 6.04)]
    // ticket 84294890011197 - the 7 of the prefix read as ".J"
    [InlineData(".J4323-09145 008783 BIM CONCHAS SS2P FC 6 1.99 11.94 1.8900 11.34", 6, 11.34)]
    public void ASpeckInFrontOfTheCodeBandNoLongerCostsTheWholeRow(string row, int units, double amount)
    {
        var t = Parse(Ticket(row, units, (decimal)amount), out _);

        Assert.Equal(units, Assert.Single(t.Lines).Units);
    }

    // ─────────────────────────────────────────────────────────────── a digit eaten, not added

    /// <summary>
    /// A band one digit short cannot say which column lost it, and the two readings put the row on
    /// two different products. It is settled by the catalogue — the recorded barcode with one
    /// character deleted must be exactly what the scan produced — and never by preference.
    /// </summary>
    [Fact]
    public void ACodeBandOneDigitShortIsAskedRatherThanGuessed()
    {
        // ticket 84294890010863: the leading 7 of 74323 read as a slash, so fifteen digits survive.
        // Item 013529 is on record as 7432373791, and deleting its first character gives exactly the
        // nine digits the scan has.
        var t = Parse(
            Ticket("/4323-73791 013529 BIM MINI MANT VAIN SS4P 4 1.99 7.96 1.8900 7.56", 4, 7.56m),
            out _);

        var line = Assert.Single(t.Lines);
        Assert.Equal(4, line.Units);
        Assert.StartsWith("07432373791", line.Gtin, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other way round: the UPC is whole and the item number lost its leading zero. Accepted only
    /// because the ten digits open with a prefix Bimbo actually bills under.
    /// </summary>
    [Fact]
    public void AnItemNumberShortOfItsLeadingZeroIsPaddedWhenThePrefixStandsUp()
    {
        // ticket 84294890011168
        var t = Parse(
            Ticket("74323-09164 08785 BIM ROLE CAN FS6P FC 2 3.99 7.98 3.0200 6.04", 2, 6.04m),
            out _);

        var line = Assert.Single(t.Lines);
        Assert.Equal(2, line.Units);
        Assert.StartsWith("07432309164", line.Gtin, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────── a code nothing can stand behind

    /// <summary>
    /// Where the band is damaged past what the catalogue or the prefix rule can confirm, the row is
    /// still a row. Its name, its count and both money columns are on the line and perfectly legible;
    /// only the barcode is gone. That is worth one question — the alternative was the row vanishing
    /// and taking the delivery with it.
    /// </summary>
    [Fact]
    public void ARowWhoseCodeIsBeyondRepairIsKeptAndAskedAboutRatherThanDropped()
    {
        // ticket 84046590008956, a Barcel line: the last digit of the UPC is a question mark and
        // 011095 is a product this vendor has never been seen selling.
        var asked = new List<BimboUnknownField>();
        var warnings = new List<ConversionWarning>();

        var t = BimboPdfParser.Parse(
            Ticket("57528-0374? 011095 BAR TAK BLUHEAT 9.97 4 4.59 18.36 3.9000 15.60", 4, 15.60m),
            warnings,
            ask: q => { asked.Add(q); return "5752803740"; });

        var question = Assert.Single(asked);
        Assert.Equal(LineField.Barcode, question.Field);
        Assert.Equal("011095", question.ItemNumber);

        var line = Assert.Single(t.Lines);
        Assert.Equal(4, line.Units);
        Assert.Equal(15.60m, line.ExtendedCost);
    }

    /// <summary>
    /// And with nobody to ask, the row still leaves and the gates still refuse the ticket. That is the
    /// behaviour that has always been right — a delivery short one line books the wrong money — and
    /// what changed is only that the row now gets as far as being asked about.
    /// </summary>
    [Fact]
    public void WithNobodyToAskTheTicketIsStillRefusedRatherThanShippedShort()
    {
        var ex = Assert.Throws<EdiConversionException>(() => BimboPdfParser.Parse(
            Ticket("57528-0374? 011095 BAR TAK BLUHEAT 9.97 4 4.59 18.36 3.9000 15.60", 4, 15.60m),
            []));

        Assert.Equal(PdfReasonCodes.QtyMismatch, ex.ReasonCode);
    }

    // ────────────────────────────────────────────────────── a price the row's own amount denies

    private static VisionReading Picture(params VisionRow[] rows) =>
        new() { Rows = rows };

    /// <summary>
    /// Ticket 84294890010942 prints `1.6900` where the paper says 1.8900 — four decimals and entirely
    /// plausible, so nothing about its shape gives it away. Its quantity came off the retail pair, so
    /// the wholesale columns were never asked to agree with each other, and eight units at the price
    /// it was given is 13.52 where the row prints 15.12. The band total was short by exactly that
    /// 1.60 and the whole delivery was refused.
    /// </summary>
    [Fact]
    public void APriceTheRowsOwnAmountDeniesIsRepairedWhenThePictureAgreesAboutTheAmount()
    {
        var warnings = new List<ConversionWarning>();

        var t = BimboPdfParser.Parse(
            Ticket("74323-09122 008931 BIM MINI MANT SS4P FC 8 1.99 15.92 1.6900 15.12", 8, 15.12m),
            warnings,
            vision: Picture(new VisionRow
            {
                ItemNumber = "008931", Units = 8, Price = 1.8900m, Amount = 15.12m,
            }));

        Assert.Equal(15.12m, Assert.Single(t.Lines).ExtendedCost);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.LineMathMismatch);
    }

    /// <summary>
    /// The same disagreement the other way round, off ticket 84294890010863: the AMOUNT column reads
    /// `338` for a printed 3.78 while the price is fine. The picture reads 3.78, so the two readers
    /// differ about the amount rather than the price and the printed price stands. Reading this one
    /// as a bad price would book a $3.78 line at $338.
    /// </summary>
    [Fact]
    public void ADamagedAmountDoesNotGetToRewriteAGoodPrice()
    {
        var t = BimboPdfParser.Parse(
            Ticket("74323-09165 008927 BIM ROLE CAN SS2P FC 2 1.99 3.98 1.8900 338", 2, 3.78m),
            [],
            vision: Picture(new VisionRow
            {
                ItemNumber = "008927", Units = 2, Price = 1.8900m, Amount = 3.78m,
            }));

        Assert.Equal(3.78m, Assert.Single(t.Lines).ExtendedCost);
    }

    /// <summary>Without a second reader the printed price stands, which is what it has always done.</summary>
    [Fact]
    public void WithNoPictureThePrintedPriceStandsAndTheBandGateRefusesTheTicket()
    {
        var ex = Assert.Throws<EdiConversionException>(() => Parse(
            Ticket("74323-09122 008931 BIM MINI MANT SS4P FC 8 1.99 15.92 1.6900 15.12", 8, 15.12m),
            out _));

        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    // ──────────────────────────────────────────────────────────────── the grand total is one figure

    /// <summary>
    /// TICKET TOTALS is printed once and the scan damages it like anything else — 240.07 comes back
    /// "2411.07" on one of the store's tickets and 350.68 as "3511.68" on another. Both bands agree
    /// with their own rows and with each other, which is two separately printed figures against one,
    /// so the ticket ships on the bands rather than being refused.
    /// </summary>
    [Fact]
    public void AGrandTotalTheScanDamagedLosesToTwoBandTotalsThatAgreeWithTheirRows()
    {
        var warnings = new List<ConversionWarning>();
        var t = BimboPdfParser.Parse(
        [
            "BIMBO BAKERIES USA, INC. - CASH STOPS",
            "**DELIVERIES**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09183 008788 BIM PANQUE NUE FS FC 4 3.19 12.76 3.0100 12.04",
            "DELIVERY TOTAL 4 12.76 12.04",
            "**RETURNS**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09145 008783 BIM CONCHAS SS2P FC -2 1.99 -3.98 1.8900 -3.78",
            "RETURNS TOTAL -2 -3.98 -3.78",
            "TICKET TOTALS 2 8.78 8266.00",       // the paper says 8.26
            "CASH DUE",
        ], warnings);

        Assert.Equal(8.26m, t.Total);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.TotalMismatch);
    }

    /// <summary>
    /// But only where the bands cover the whole ticket. A band that printed no amount of its own has
    /// nothing standing behind the rows under it, so there is no second witness and the ticket is
    /// refused as before.
    /// </summary>
    [Fact]
    public void AGrandTotalStillRefusesTheTicketWhenABandPrintsNoAmountOfItsOwn()
    {
        var ex = Assert.Throws<EdiConversionException>(() => BimboPdfParser.Parse(
        [
            "BIMBO BAKERIES USA, INC. - CASH STOPS",
            "**DELIVERIES**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09183 008788 BIM PANQUE NUE FS FC 4 3.19 12.76 3.0100 12.04",
            "DELIVERY TOTAL 4",
            "TICKET TOTALS 4 12.76 1200.40",
            "CASH DUE",
        ], []));

        Assert.Equal(PdfReasonCodes.TotalMismatch, ex.ReasonCode);
    }

    /// <summary>
    /// The TICKET TOTALS label itself is scan debris on one of the store's tickets — "TICKET IOTALS" —
    /// and losing it cost that ticket the only witness that could overrule a band count the scan had
    /// mangled ("DELIUERY TOTAL 9~" for 90). It was refused for a quantity mismatch it had the answer
    /// to printed on it.
    /// </summary>
    [Fact]
    public void ATicketTotalsLabelTheScanDamagedIsStillFound()
    {
        var warnings = new List<ConversionWarning>();
        var t = BimboPdfParser.Parse(
        [
            "BIMBO BAKERIES USA, INC. - CASH STOPS",
            "**DELIVERIES**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09183 008788 BIM PANQUE NUE FS FC 4 3.19 12.76 3.0100 12.04",
            "DELIUERY TOTAL 9~ 12.76 12.04",     // the paper says 4
            "TICKET IOTALS 4 12.76 12.04",
            "CASH DUE",
        ], warnings);

        Assert.Equal(4, Assert.Single(t.Lines).Units);
        Assert.Contains(warnings, w => w.ReasonCode == PdfReasonCodes.QtyMismatch);
    }

    // ───────────────────────────────────────────────────────────────────── amber has to mean something

    /// <summary>
    /// Nearly every ticket this vendor sends carries returns, so "this ticket has returns" was on
    /// nearly every file — and it set the file's status, which meant every ticket that converted came
    /// out amber. Sixteen in a row did on 2026-08-12. It is still said, still logged, and no longer
    /// dressed as something to go and look at.
    /// </summary>
    [Fact]
    public void ATicketWhoseOnlyNoteIsThatItHasReturnsIsNotFlagged()
    {
        var warnings = new List<ConversionWarning>();
        BimboPdfParser.Parse(
        [
            "BIMBO BAKERIES USA, INC. - CASH STOPS",
            "**DELIVERIES**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09183 008788 BIM PANQUE NUE FS FC 4 3.19 12.76 3.0100 12.04",
            "DELIVERY TOTAL 4 12.76 12.04",
            "**RETURNS**",
            "UPC No QTY RETAIL AMOUNT PRICE AMOUNT",
            "74323-09145 008783 BIM CONCHAS SS2P FC -2 1.99 -3.98 1.8900 -3.78",
            "RETURNS TOTAL -2 -3.98 -3.78",
            "TICKET TOTALS 2 8.78 8.26",
            "CASH DUE",
        ], warnings);

        var note = Assert.Single(warnings);
        Assert.Equal(PdfReasonCodes.CreditLines, note.ReasonCode);
        Assert.True(note.Routine, "returns are how this vendor works, not something to check");
    }

    /// <summary>And a ticket with something real to say still says it.</summary>
    [Fact]
    public void ATicketWithSomethingWorthCheckingIsStillFlagged()
    {
        var warnings = new List<ConversionWarning>();
        BimboPdfParser.Parse(
            // 03965 is Tia Rosa, which this vendor's prefix rule has never been shown - the line may
            // open a new item in SSCS, and that is exactly what somebody has to go and look at.
            Ticket("03965-00102 007383 TR FLR TACO TORT 10P 2 3.39 6.78 2.9900 5.98", 2, 5.98m),
            warnings);

        Assert.Contains(warnings, w => !w.Routine && w.ReasonCode == PdfReasonCodes.UpcCheckDigit);
    }

    // ─────────────────────────────────────────────────────────── nothing else may become a row

    /// <summary>
    /// The looser read is only safe because a code band has to hand over to a product description.
    /// These are the lines on a Bimbo ticket that come closest to looking like a row, including the
    /// SOD line, whose date and clock time together offer sixteen digits — but they stop in the middle
    /// of a figure rather than at a word.
    /// </summary>
    [Theory]
    [InlineData("500: 21312020 10:0649 AM 84-000032179-01 I NY0I CE# 84294890011242")]
    [InlineData("SOD: 1012012025 3:18:47 PM 84-000028173-08 INVOICE# 84294690010553")]
    [InlineData("84-2948-CHUBIWINOS SNG I COON 167991 PO#:")]
    [InlineData("12154 CUST# 992515-2")]
    [InlineData("20110 FM 1314 RD PAY TERM: Cash")]
    [InlineData("[3104 5813 BEST STOP")]
    [InlineData("77365 1' CONTRACT#:")]
    [InlineData("GHRA 5397 BEST STOP 10")]
    [InlineData("ITEM SUGG RETAIL WHOLESALE")]
    [InlineData("UPC No QTY RETAIL AMOUNT PRICE AMOUNT")]
    [InlineData("23 50.97 47.84")]
    public void TheLinesThatAreNotProductRowsStillAreNot(string line)
    {
        var lines = Ticket(
            "74323-09183 008788 BIM PANQUE NUE FS FC 4 3.19 12.76 3.0100 12.04", 4, 12.04m);
        lines.Insert(4, line);

        // Had the extra line been read as a row, its money would have joined the band and the
        // DELIVERY TOTAL gate would have thrown.
        var t = Parse(lines, out _);
        Assert.Single(t.Lines);
        Assert.Equal(4, t.Lines[0].Units);
    }
}
