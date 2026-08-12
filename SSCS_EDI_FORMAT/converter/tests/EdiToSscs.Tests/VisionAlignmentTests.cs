using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs for the one part of the vision reader that can do real damage: deciding which parsed row a
/// vision row belongs to.
///
/// A reading tied to the wrong row puts one product's figures onto another product, and the money
/// still adds up, so nothing downstream would ever notice. Every test here is therefore about what
/// the alignment **refuses** as much as what it accepts.
/// </summary>
public class VisionAlignmentTests
{
    private static VisionRow Row(
        string item, int? units = null, decimal? amount = null, string? barcode = null) =>
        new() { ItemNumber = item, Units = units, Amount = amount, Barcode = barcode };

    private static VisionReading Reading(params VisionRow[] rows) => new() { Rows = rows };

    /// <summary>
    /// The ordinary case: both readers went down the same page and found the same rows, so row 3 is
    /// row 3. The item numbers here disagree on every single row — which is what these scans
    /// actually look like — and the counts and amounts carry the alignment anyway.
    /// </summary>
    [Fact]
    public void TiesRowsByPositionWhenTheTwoReadingsCorroborateEachOther()
    {
        var reading = Reading(
            Row("0861718", 24, 40.75m),
            Row("8633049", 24, 70.22m),
            Row("0846729", 48, 106.90m));

        var aligned = reading.AlignTo([
            new VisionAnchor("861718", 24, 40.76m),      // item and amount both damaged
            new VisionAnchor("8633049", 24, 70.22m),
            new VisionAnchor("08246729", 48, null),      // amount destroyed outright
        ]);

        Assert.Equal(40.75m, aligned[0]!.Amount);
        Assert.Equal(70.22m, aligned[1]!.Amount);
        Assert.Equal(106.90m, aligned[2]!.Amount);
    }

    /// <summary>
    /// A reading shifted by a row agrees about almost nothing, and is thrown out rather than used.
    /// This is the failure that matters: taken at face value it would book every row's money onto
    /// its neighbour, and the invoice would still total correctly.
    /// </summary>
    [Fact]
    public void RefusesPositionWhenTheRowsDoNotCorroborateIt()
    {
        // The model missed the first product and reported the next three, so its row 1 is really the
        // parser's row 2 and every pairing is off by one. Taken at face value this books each row's
        // money onto its neighbour — and the invoice still totals correctly, so nothing downstream
        // would ever catch it.
        var reading = Reading(
            Row("B1", 12, 22.00m),
            Row("C1", 6, 33.00m),
            Row("D1", 3, 44.00m));

        var aligned = reading.AlignTo([
            new VisionAnchor("A0", 24, 11.00m),
            new VisionAnchor("B0", 12, 22.00m),
            new VisionAnchor("C0", 6, 33.00m),
        ]);

        Assert.All(aligned, Assert.Null);
    }

    /// <summary>
    /// One product on one page, found by both readers. There is nothing else it could be, so it is
    /// tied without a vote — the majority test cannot mean anything over a single row.
    /// </summary>
    [Fact]
    public void TiesASingleRowTicketEvenWhenNothingElseAgrees()
    {
        var aligned = Reading(Row("084815", 48, 106.90m))
            .AlignTo([new VisionAnchor("O84B15", null, null)]);

        Assert.Equal(106.90m, Assert.Single(aligned)!.Amount);
    }

    /// <summary>
    /// Where position will not hold, the item number ties what it can — including a product billed
    /// twice on one ticket, whose two rows must not be allowed to swap.
    /// </summary>
    [Fact]
    public void TiesARepeatedItemNumberInTheOrderItIsPrinted()
    {
        var reading = Reading(
            Row("RB1", 24, 10m), Row("ZZZ", 1, 1m), Row("RB1", 48, 20m), Row("YYY", 1, 1m));

        // Four against three, so position is not even considered.
        var aligned = reading.AlignTo([
            new VisionAnchor("RB1", null, null),
            new VisionAnchor("RB1", null, null),
            new VisionAnchor("QQQ", null, null),
        ]);

        Assert.Equal(24, aligned[0]!.Units);
        Assert.Equal(48, aligned[1]!.Units);
        Assert.Null(aligned[2]);
    }

    /// <summary>
    /// The last row neither side could key is **not** paired off, however forced it looks.
    ///
    /// It is only forced if the two lists really describe the same rows. A model that missed one
    /// product and reported another the parser never found satisfies "one left on each side"
    /// exactly, and pairing them ties two unrelated rows together — the delivery goes in under the
    /// wrong product with every total on the page still correct.
    /// </summary>
    [Fact]
    public void NeverPairsOffTheLastUnclaimedRow()
    {
        // The model missed AAA at the top and found a DDD the parser did not. Two of three key up,
        // the amounts rule out position, and the odd pair is left alone.
        var reading = Reading(Row("BBB", 12, 22m), Row("CCC", 6, 33m), Row("DDD", 3, 44m));

        var aligned = reading.AlignTo([
            new VisionAnchor("AAA", 24, 11m),
            new VisionAnchor("BBB", 12, 22m),
            new VisionAnchor("CCC", 6, 33m),
        ]);

        Assert.Null(aligned[0]);                        // never tied to DDD
        Assert.Equal(22m, aligned[1]!.Amount);
        Assert.Equal(33m, aligned[2]!.Amount);
    }

    /// <summary>
    /// Two rows left over is two ways to pair them, and a coin toss here books a delivery onto the
    /// wrong product. Both are left alone.
    /// </summary>
    [Fact]
    public void LeavesUnclaimedRowsAloneRatherThanGuessing()
    {
        var reading = Reading(Row("AAA", 1, 1m), Row("BBB", 2, 2m), Row("CCC", 3, 3m));

        var aligned = reading.AlignTo([
            new VisionAnchor("AAA", 9, 91m),
            new VisionAnchor("B8B", 9, 92m),
            new VisionAnchor("C0C", 9, 93m),
        ]);

        Assert.Equal(1, aligned[0]!.Units);
        Assert.Null(aligned[1]);
        Assert.Null(aligned[2]);
    }

    /// <summary>
    /// Dr Pepper's priced row carries no item number of its own, so it ties on the UPC instead —
    /// the same machinery pointed at a different column.
    /// </summary>
    [Fact]
    public void TiesOnTheBarcodeWhenTheVendorHasNoItemNumber()
    {
        var reading = Reading(
            Row("", 12, 20.00m, barcode: "813694026719"),
            Row("", 24, 40.00m, barcode: "842595138214"));

        var aligned = reading.AlignTo(
            [new VisionAnchor("842595138214", null, null), new VisionAnchor("813694026719", null, null)],
            v => v.Barcode);

        Assert.Equal(24, aligned[0]!.Units);
        Assert.Equal(12, aligned[1]!.Units);
    }

    /// <summary>An empty reading ties nothing and changes nothing — the no-key, no-network case.</summary>
    [Fact]
    public void AnEmptyReadingTiesNothing()
    {
        var aligned = VisionReading.Empty.AlignTo([
            new VisionAnchor("AAA", 1, 1m), new VisionAnchor("BBB", 2, 2m)]);

        Assert.Equal(2, aligned.Length);
        Assert.All(aligned, Assert.Null);
    }

    /// <summary>
    /// Spacing and case in an item number are not real differences; the scan produces both.
    /// </summary>
    [Fact]
    public void IgnoresSpacingAndCaseInAKey()
    {
        var aligned = Reading(Row("rb 1718", 24, 40.75m), Row("QQQ", 1, 1m))
            .AlignTo([new VisionAnchor("RB-1718", 9, 9m), new VisionAnchor("ZZZ", 8, 8m)]);

        Assert.Equal(40.75m, aligned[0]!.Amount);
    }
}
