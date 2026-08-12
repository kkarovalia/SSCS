using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs that the strip of column names above a row is always there, and always the right one.
///
/// The strip is not decoration and it is not the answer to the question — it is what makes the
/// answer readable. A snip is two printed lines cut out of the middle of a ticket, and by item 16
/// the invoice's own "DESCRIPTION UPC/SKU / WHSLE CASES UNITS NET TAX AMOUNT" is a foot further up
/// the page and nowhere near the cut. Without a legend the operator is looking at a bare line of
/// figures with nothing to say which one is the NET and which the AMOUNT.
///
/// <see cref="PdfRowSnipper.TryCutHeader"/> cuts the printed one where it can, and that is the
/// better article because it is **aligned** — same page, same width, UNITS over the units. This
/// covers the fallback for the three ways that fails: a Dr Pepper header the scan chewed up until it
/// no longer reads as one, a Red Bull page where none was found, and a row with no line number.
///
/// The section matters as much as the vendor. Dr Pepper's CREDITS block prints a **shorter column
/// set** with no WHSLE, so a sales legend over a credit row would have the operator counting across
/// to the figure next door — which is the same class of mistake as marking the wrong row, arrived at
/// from the other end.
/// </summary>
public class ColumnLegendTests
{
    private sealed class FakeVision(VisionReading reading) : IInvoiceVisionReader
    {
        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => reading;
    }

    private sealed class ScriptedPrompt(string invoiceNumber) : IInvoiceDetailsPrompt
    {
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<string, string> Paper { get; } = new(StringComparer.Ordinal);

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new(invoiceNumber, request.Date ?? new DateOnly(2026, 8, 12));

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return Paper.TryGetValue(request.Description, out string? answer)
                ? answer
                : throw new Xunit.Sdk.XunitException(
                    $"unexpected question: {request.Field} for \"{request.Description}\"");
        }
    }

    private static ConversionService Service(
        TempDir tmp, IInvoiceDetailsPrompt prompt, IInvoiceVisionReader? vision = null) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt, vision);

    private static string Place(TempDir tmp, string fixture, string name)
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(@"Fixtures\PDF", fixture)));
        return path;
    }

    private static QueuedFile DrPepper(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

    private static QueuedFile RedBull(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.RedBull };

    // ──────────────────────────────────────────────────────────────────────── Dr Pepper, sales

    /// <summary>
    /// A Dr Pepper sales question names Dr Pepper's sales columns — and names them in the order the
    /// invoice prints them, description before the code. That order is not cosmetic: the operator
    /// reads left to right off the strip onto the row, and a reversed legend sends them to the SKU
    /// when the question is about the barcode.
    /// </summary>
    [Fact]
    public void ADrPepperSalesQuestionCarriesTheSalesColumns()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["16z12 Clam"] = "014800511758";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "16z12 Clam", Barcode = "not-a-barcode", Page = 1,
                    Qty = 1, Units = 12, Price = 21.40m, Amount = 21.40m,
                },
            ],
            PrintedTotal = 21.40m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        string legend = Assert.Single(prompt.LineFields).ColumnLegend;
        Assert.Contains("WHSLE", legend, StringComparison.Ordinal);
        Assert.Contains("AMOUNT", legend, StringComparison.Ordinal);
        Assert.DoesNotContain("TOTAL", legend, StringComparison.Ordinal);   // that is Red Bull's word

        // Description first, code second, exactly as the product line prints it.
        Assert.True(
            legend.IndexOf("DESCRIPTION", StringComparison.Ordinal)
            < legend.IndexOf("UPC/SKU", StringComparison.Ordinal),
            $"the legend puts the code before the name: {legend}");
    }

    // ─────────────────────────────────────────────────────────────────────── Dr Pepper, credits

    /// <summary>
    /// A credit row gets the credit columns, and the difference is a whole column.
    ///
    /// Dr Pepper's CREDITS banner drops WHSLE, so its rows print five figures where a sales row
    /// prints six. Handing an operator the sales legend over one of these shifts everything by a
    /// place: they count across to what the strip calls UNITS and read the CASES figure. The money
    /// then reconciles against a count belonging to nothing, which is the quiet kind of wrong.
    /// </summary>
    [Fact]
    public void ADrPepperCreditQuestionCarriesTheCreditColumns()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4646105204");
        prompt.Paper["12z12p Zsgr DP"] = "078000035261";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "5GBIB DrPep", Barcode = "054900710545",
                    Qty = 1, Units = 1, Price = 115.95m, Amount = 115.95m,
                },
                new VisionRow
                {
                    // A returned case, and a code that fails its own check digit - so this is the
                    // row the operator is asked about.
                    ItemNumber = "", Description = "12z12p Zsgr DP", Barcode = "078000035267",
                    Units = 1, Price = 6.37m, Amount = 6.37m, IsCredit = true,
                },
            ],
            PrintedTotal = 109.58m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.Equal("12z12p Zsgr DP", asked.Description);

        // No WHSLE column on a credit, and the rest of the set still named.
        Assert.DoesNotContain("WHSLE", asked.ColumnLegend, StringComparison.Ordinal);
        foreach (string column in new[] { "CASES", "UNITS", "NET", "TAX", "AMOUNT" })
            Assert.Contains(column, asked.ColumnLegend, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────────── Red Bull

    /// <summary>
    /// Red Bull's own columns, which share barely a word with Dr Pepper's — its last money column is
    /// TOTAL, not AMOUNT, and it prints DEP, DISC and SUGAR in between. Sending an operator looking
    /// for a column that is not on their paper is the thing <see cref="LineFieldRequest.ColumnName"/>
    /// exists to prevent, and a legend from the wrong vendor undoes it.
    /// </summary>
    [Fact]
    public void ARedBullQuestionCarriesRedBullsOwnColumns()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.NotEmpty(prompt.LineFields);

        Assert.All(prompt.LineFields, f =>
        {
            foreach (string column in new[] { "ID", "QTY", "UNITS", "PRICE", "SUGAR", "TOTAL" })
                Assert.Contains(column, f.ColumnLegend, StringComparison.Ordinal);
            Assert.DoesNotContain("WHSLE", f.ColumnLegend, StringComparison.Ordinal);
        });
    }

    // ─────────────────────────────────────────────────────────── the row with no line number

    /// <summary>
    /// The screenshot of 2026-08-12: a whole page on screen, nothing marked, and no column names
    /// anywhere on the dialog.
    ///
    /// That row has no line number, so there is no header to cut — and until now the strip was
    /// simply dropped, which is the one case where it is needed most. There is still nothing marked
    /// (the position cannot be had, and a box on a guessed row gets typed in), but the operator now
    /// knows what the columns are called.
    /// </summary>
    [Fact]
    public void AWholePageQuestionStillNamesTheColumns()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("4642411699");
        prompt.Paper["16z12 Clam"] = "014800511758";

        var reading = new VisionReading
        {
            Rows =
            [
                new VisionRow
                {
                    ItemNumber = "", Description = "16z12 Clam", Barcode = "nope", Page = 1,
                    Qty = 1, Units = 12, Price = 21.40m, Amount = 21.40m,
                },
            ],
            PrintedTotal = 21.40m,
        };

        var outcome = Service(tmp, prompt, new FakeVision(reading))
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down.pdf", "dp.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var asked = Assert.Single(prompt.LineFields);
        Assert.True(asked.WholePage);
        Assert.Null(asked.Header);                   // nothing to cut without a line number
        Assert.NotEqual("", asked.ColumnLegend);     // but the names are still given
        Assert.Equal(0, asked.Snip!.FocusHeight);    // and still nothing is marked
    }

    /// <summary>
    /// The legend rides along even where the printed header was cut, so the dialog gets to prefer
    /// the aligned one and fall back without the caller having to know which happened. Belt and
    /// braces on the case that matters: a Dr Pepper header damaged past recognition returns null
    /// from the cut, and that must not leave the row bare.
    /// </summary>
    [Fact]
    public void TheLegendIsSuppliedEvenWhenThePrintedHeaderWasCut()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("112425112379D");
        prompt.Paper["WNTB FUJAPP 1202 LS"] = "106.90";
        prompt.Paper["PINK 120215"] = "4.45";

        var outcome = Service(tmp, prompt)
            .ConvertFile(RedBull(Place(tmp, "REDBULL_2030211863_sscs_verified.pdf", "rb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);

        var cut = prompt.LineFields.Where(f => f.Header is not null).ToList();
        Assert.NotEmpty(cut);
        Assert.All(cut, f => Assert.NotEqual("", f.ColumnLegend));
    }
}
