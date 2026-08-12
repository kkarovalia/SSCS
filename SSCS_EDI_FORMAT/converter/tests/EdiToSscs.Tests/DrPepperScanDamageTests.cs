using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// The four kinds of scan damage that used to send a whole Dr Pepper delivery back to the store,
/// each taken from a ticket the store actually dropped on 08/11/2026.
///
/// Every one of them is a page a person can read at a glance. What they had in common was that the
/// invoice's own arithmetic could tell *something* was wrong and the converter answered by refusing
/// — "please re-scan the whole invoice", which a second scan of the same paper does not fix.
/// </summary>
public class DrPepperScanDamageTests
{
    private const string Dir = @"Fixtures\PDF";

    /// <summary>
    /// Answers by row and field, the way an operator reading the picture would, and fails the test on
    /// any question that is not scripted — so a change that starts asking something new shows up here
    /// rather than passing quietly.
    /// </summary>
    private sealed class ScriptedPrompt(string invoiceNumber, decimal total = 0m) : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Prompts { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];
        public Dictionary<(int Position, LineField Field), string> Paper { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Prompts.Add(request);
            return new InvoiceDetails(
                invoiceNumber, request.DateToOffer ?? request.Date ?? default, total);
        }

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return Paper.TryGetValue((request.Position, request.Field), out string? answer)
                ? answer
                : throw new Xunit.Sdk.XunitException(
                    $"unexpected question: {request.Field} on row {request.Position} "
                    + $"(\"{request.Description}\", the scan read \"{request.PrintedValue}\")");
        }
    }

    private static ConversionService Service(TempDir tmp, IInvoiceDetailsPrompt prompt) =>
        new(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt);

    private static QueuedFile DrPepper(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper };

    private static string Place(TempDir tmp, string fixture, string name)
    {
        string path = Path.Combine(tmp.Path, name);
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(Dir, fixture + ".pdf")));
        return path;
    }

    /// <summary>
    /// Invoice 4643014462. One Clamato row prints `21.40` and scans `11.40`, so the invoice is
    /// exactly $10 short of its own AMOUNT DUE — which used to be a flat refusal that could not say
    /// which line was wrong.
    ///
    /// The row says its own money twice, in the AMOUNT column and as CASES x NET, and it is the only
    /// row on 29 where those two disagree. So one question is put in front of the operator, with the
    /// printed row in it, and the delivery lands on the $1240.60 the ticket prints for itself.
    /// </summary>
    [Fact]
    public void AnAmountOffByTenIsAskedAboutOnTheOneRowThatContradictsItself()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("101325-DP1");
        prompt.Paper[(12, LineField.Barcode)] = "898999010229";   // the scan read 898999010129
        prompt.Paper[(17, LineField.Amount)] = "21.40";           // the scan read 11.40

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_4643014462_amount_off_by_ten", "dp.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("1240.60", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(29, xml.Descendants("LineItem").Count());
        Assert.Equal(721, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));

        // Exactly one money question, and it is the damaged row - not a question on every line.
        var money = Assert.Single(prompt.LineFields, r => r.Field == LineField.Amount);
        Assert.Equal("11.40", money.PrintedValue);
        Assert.Equal("AMOUNT", money.ColumnName);
        Assert.NotNull(money.Snip);
    }

    /// <summary>
    /// Invoice 4642411699, which carries the same damage twice over: a package of Slot bills nine
    /// rows at $21.38 and one of them scans as `11.38`, and *both* of the ticket's remaining
    /// subtotals lost their dollar figure to the scan (`23S.18`, and a `PEG 11 132` that reads fine).
    ///
    /// A subtotal thrown away for a damaged dollar figure used to take the group boundary with it, so
    /// there was nothing left to narrow the search down to. Now the counts still close their package
    /// and the row that contradicts itself is the one question.
    /// </summary>
    [Fact]
    public void ASubtotalKeepsItsGroupEvenWhenTheScanDestroysItsMoney()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("092925-DP1");
        prompt.Paper[(14, LineField.Amount)] = "21.38";            // the scan read 11.38
        prompt.Paper[(16, LineField.Barcode)] = "887725000047";    // "88772S000047"
        prompt.Paper[(17, LineField.Barcode)] = "887725000450";    // a backtick welded to the token

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_4642411699_two_damaged_subtotals", "dp.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("730.50", xml.Descendants("TotalInvoiceDueAmt").Single().Value);   // TOT SALE
        Assert.Equal(22, xml.Descendants("LineItem").Count());
        Assert.Equal(391, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
        Assert.Single(prompt.LineFields, r => r.Field == LineField.Amount);
    }

    /// <summary>
    /// Invoice 4642413977, where one UNITS column prints 24 and scans `14`.
    ///
    /// No money check can see this: every dollar on the page stays correct and the delivery goes into
    /// SSCS ten units short. The package subtotal can — it prints 21 cases / 504 units and states the
    /// pack size — but its own dollar figure came through as `682.S0`, and losing the subtotal for
    /// that put **eleven** rows in question, ten of which were perfect. One is asked about now.
    /// </summary>
    [Fact]
    public void ADamagedSubtotalNoLongerPutsAWholePackageInQuestion()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");

        var prompt = new ScriptedPrompt("030226-DP1");
        prompt.Paper[(7, LineField.Units)] = "24";                 // the scan read 14
        prompt.Paper[(8, LineField.Barcode)] = "078000054408";     // the scan read 070000054408

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_4642413977_damaged_subtotal", "dp.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("1005.98", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(656, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));   // TOT SALE

        var count = Assert.Single(prompt.LineFields, r => r.Field == LineField.Units);
        Assert.Equal("14", count.PrintedValue);
        Assert.Equal("20z24 AN RtBr PET", count.Description);
    }

    /// <summary>
    /// Invoice 4642413020, whose only damage is a decimal point: $13.80 reaches the text layer as
    /// `13;80`. A semicolon is the same OCR slip as the colon and comma already allowed, and the
    /// figure it produces is re-added by the package subtotal above it and by AMOUNT DUE — so the
    /// ticket converts with no questions at all, where it used to stop on a perfectly clean page.
    /// </summary>
    [Fact]
    public void AScannedSemicolonIsADecimalPointAndCostsNoQuestions()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("122225-DP1");

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_4642413020_semicolon_decimal", "dp.pdf")), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        Assert.Empty(prompt.LineFields);

        var xml = XDocument.Load(Assert.Single(outcome.FinalOutputPaths));
        Assert.Equal("875.71", xml.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal(20, xml.Descendants("LineItem").Count());
        Assert.Equal(507, xml.Descendants("InvoiceUnitQty").Sum(e => int.Parse(e.Value)));
    }

    /// <summary>
    /// A page fed through the scanner upside down. The OCR read it line by line and produced a full
    /// text layer of nothing — "AMOUNT DUE $1263.93" arrives as "£6'£9ZIS an0 J.NnOHiI", every glyph
    /// its own 180-degree twin — so no field on the page matches anything.
    ///
    /// What matters here is that **nothing is asked**. The operator used to be shown the invoice
    /// number box, then the amount-due box, and only then told it had failed: three windows about a
    /// page that was never readable, when the fix is to turn the paper round.
    /// </summary>
    [Fact]
    public void AnUpsideDownScanIsRefusedBeforeAnythingIsAsked()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        var prompt = new ScriptedPrompt("x");

        var outcome = Service(tmp, prompt)
            .ConvertFile(DrPepper(Place(tmp, "DRPEPPER_upside_down", "dp.pdf")), dest);

        Assert.False(outcome.Success);
        Assert.Empty(prompt.Prompts);
        Assert.Empty(prompt.LineFields);
        Assert.Empty(Directory.GetFiles(dest));
        Assert.Contains("upside down", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scan it again", outcome.UserMessage, StringComparison.OrdinalIgnoreCase);
    }
}
