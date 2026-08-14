using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs for vendors that permit a typed column-name fallback. Dr Pepper no longer does: its
/// AI-primary and approved-legacy regression tests require the real photographed section header.
///
/// The strip is not decoration and it is not the answer to the question — it is what makes the
/// answer readable. A snip is two printed lines cut out of the middle of a ticket, and by item 16
/// the invoice's own "DESCRIPTION UPC/SKU / WHSLE CASES UNITS NET TAX AMOUNT" is a foot further up
/// the page and nowhere near the cut. Without a legend the operator is looking at a bare line of
/// figures with nothing to say which one is the NET and which the AMOUNT.
///
/// <see cref="PdfRowSnipper.TryCutHeader"/> cuts the printed one where it can, and that is the
/// better article because it is **aligned** — same page, same width, UNITS over the units. This
/// covers the fallback for Red Bull or Bimbo when a printed header cannot be located.
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
        public bool ConfirmDrPepperOcrFallback(string fileName) => true;
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<string, string> Paper { get; } = new(StringComparer.Ordinal);

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) =>
            new(invoiceNumber, new DateOnly(2026, 8, 12));

        public bool ConfirmBimboOcrFallback(string fileName) => true;

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

    private static QueuedFile Bimbo(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.Bimbo };

    // ──────────────────────────────────────────────────────────────────────── Dr Pepper, sales

    /// <summary>
    /// A Dr Pepper sales question names Dr Pepper's sales columns — and names them in the order the
    /// invoice prints them, description before the code. That order is not cosmetic: the operator
    /// reads left to right off the strip onto the row, and a reversed legend sends them to the SKU
    /// when the question is about the barcode.
    /// </summary>
    [Fact(Skip = "Superseded by Dr Pepper exact-value AI-primary highlight tests.")]
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
    [Fact(Skip = "Superseded by Dr Pepper exact-value AI-primary highlight tests.")]
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

    // ───────────────────────────────────────────────────────────────────────────────── Bimbo

    /// <summary>
    /// Bimbo's columns, which are a third arrangement again: eight of them, the barcode first rather
    /// than under the description, and **two money pairs** — a suggested-retail pair the store does
    /// not use and a wholesale pair it does. That last part is why the legend has to be right here.
    /// Both pairs are printed as "AMOUNT", the two sit side by side, and the one being asked about is
    /// always the wholesale one on the right.
    ///
    /// One legend for both bands, unlike Dr Pepper: the RETURNS block repeats the same headings and
    /// only puts a minus in front of the figures.
    /// </summary>
    [Fact]
    public void ABimboQuestionCarriesBimbosOwnColumns()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("090825143000D");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "4";
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        var outcome = Service(tmp, prompt).ConvertFile(
            Bimbo(Place(tmp, "BIMBO_84294890010234_deliveries_only.pdf", "bb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.NotEmpty(prompt.LineFields);

        Assert.All(prompt.LineFields, f =>
        {
            foreach (string column in new[] { "UPC", "ITEM", "QTY", "RETAIL", "WHOLESALE", "PRICE" })
                Assert.Contains(column, f.ColumnLegend, StringComparison.Ordinal);

            // Neither of the other two vendors' vocabularies has leaked in.
            Assert.DoesNotContain("WHSLE", f.ColumnLegend, StringComparison.Ordinal);
            Assert.DoesNotContain("SUGAR", f.ColumnLegend, StringComparison.Ordinal);
            Assert.DoesNotContain("CASES", f.ColumnLegend, StringComparison.Ordinal);

            // And the question names the column the operator has to find, which on this vendor is
            // QTY — not the UNITS both the others print.
            Assert.Equal("QTY", f.ColumnName);
        });
    }

    /// <summary>
    /// A Bimbo question shows the ticket's **own** printed header, not our typed-out list of names.
    ///
    /// This shipped wrong once and looked plausible from the inside: `IsColumnHeader` matched on
    /// UNITS and AMOUNT, which are Dr Pepper's words. Bimbo prints QTY rather than UNITS, and its
    /// scans render AMOUNT as `ANOLNT`, `AHRUNT`, `AH00NT`, `AHUUNI`, `ANOUNT`, `ANO NT`, `AMONT` and
    /// `ANDUTI`, so nothing matched, the cut came back null, and every question fell through to the
    /// typed legend — grey, in our own font, and by design not aligned to anything. The operator sees
    /// a row of eight figures and a list of eight names that does not line up over them, on a vendor
    /// whose two AMOUNT columns sit side by side.
    ///
    /// So the cut has to exist, and it has to be **both** printed lines: Bimbo splits its headings
    /// over two, "ITEM / SUGG RETAIL / WHOLESALE" above "UPC No QTY RETAIL AMOUNT PRICE AMOUNT". The
    /// lower line alone reads "RETAIL AMOUNT PRICE AMOUNT" over four figures and names two of them
    /// twice, which is the ambiguity the upper line exists to resolve.
    /// </summary>
    [Fact]
    public void ABimboQuestionShowsTheTicketsOwnHeaderAndBothLinesOfIt()
    {
        using var tmp = new TempDir();
        var prompt = new ScriptedPrompt("090825143000D");
        prompt.Paper["BIN PANOUE Il1E FS FC 3.19"] = "4";
        prompt.Paper["NLA RVORO F515P FC"] = "7";

        var outcome = Service(tmp, prompt).ConvertFile(
            Bimbo(Place(tmp, "BIMBO_84294890010234_deliveries_only.pdf", "bb.pdf")), tmp.Sub("out"));

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.NotEmpty(prompt.LineFields);

        Assert.All(prompt.LineFields, f =>
        {
            Assert.NotNull(f.Snip);
            Assert.NotNull(f.Header);

            // Cut from the same page at the same width, which is the whole reason it is worth more
            // than the typed names: QTY lands over the quantity rather than beside it.
            Assert.Equal(f.Snip!.PagePng.Length, f.Header!.PagePng.Length);
            Assert.Equal(f.Snip.Width, f.Header.Width);

            // Two printed lines against the row's one. Compared to the row rather than to a pixel
            // count so this does not become a test about scanner resolution.
            Assert.True(f.Header.Height > f.Snip.Height,
                $"header {f.Header.Height}px is not taller than the row's {f.Snip.Height}px, so only "
                + "one of the two heading lines was cut");

            // Never marked. The box goes on the figure being typed, never on a column name.
            Assert.Equal(0, f.Header.FocusHeight);
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
    [Fact(Skip = "Whole-page Dr Pepper questions are intentionally forbidden; rescan is required.")]
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
