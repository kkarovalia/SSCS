using System.Xml.Linq;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// A Blue Bell ticket whose DATE the scanner destroyed, supplied 2026-07-26.
///
/// The scan reads "DATE 12-14-Z023" — one character of the year came out as a letter. Everything
/// else on the ticket is sound and reconciles against its printed TOTAL, so refusing the whole
/// delivery over that one character would send the operator back to keying the invoice by hand.
/// The parser therefore leaves the date unread rather than throwing, and the operator is asked for
/// it alongside the invoice number they were going to type anyway.
///
/// What must NOT happen is the converter quietly deciding the year is 2023 on its own. 'Z' to '2'
/// is guesswork about a field with no checksum behind it, so the repaired reading is only ever
/// offered as a suggestion for the operator to check against the paper.
///
/// Fixture lines are PdfPig's real output, damage included.
/// </summary>
public class BlueBellDamagedDateTests
{
    private const string SamplePdf = @"Fixtures\PDF\BLUEBELL_033103483286_damaged_date.pdf";

    private static readonly string[] TicketLines =
    [
        "cc",
        "~~ ~",
        "BIue=Bell=Creamerres,'L.P .",
        "`Gene~ral Offices 1BFenham,`ITexas'",
        "DATE 12-14-Z023 TIME\"14:5455 -- BRANCH#B3 OUTE# 10 TRUCK# 1404",
        "Lc=VLL ~s 1 U) L~JS",
        "SHIP TO # INVOICE",
        "266843 #033103483286",
        "BEST STOP #10",
        "20110 FM 1314",
        "PORTER,TX 77365",
        "****",
        "**** Sales",
        "PROD UPC DESCRIPTION",
        "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
        "0925 007189905101 PT GR SUEREM",
        "3.00 !30.00 2.91 87.30",
        "l",
        "TOTAL 87.30",
        "BALrAN'CE DUE37 87130",
        "ir-- ihiil't. irn~l i ii",
        "Note:",
        "SIGNATURE:",
        "OUR REMITTANCE ADDRESS IS: P.O. BOX 674272",
        "DALLAS, TEXAS 75267-4272",
        "OUR PHONE NUMBER IS: 281-446-1090",
        "~nil®",
    ];

    /// <summary>Answers the prompt with a fixed number and date, and records what it was shown.</summary>
    private sealed class Prompt(string number, DateOnly date) : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Seen { get; } = [];

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Seen.Add(request);
            return new InvoiceDetails(number, date);
        }
    }

    private static QueuedFile BlueBell(string path) =>
        new() { InputPath = path, Kind = ConversionKind.Pdf, Vendor = PdfVendor.BlueBell };

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void AnUnreadableDateDoesNotThrowAwayAnOtherwiseGoodTicket()
    {
        var section = Assert.Single(BlueBellPdfParser.ParseAll(TicketLines, []));

        Assert.Null(section.Date);                    // nothing was invented from "12-14-Z023"
        Assert.Equal(87.30m, section.Total);          // the rest of the ticket parsed normally
        Assert.Equal("033103483286", section.SuggestedInvoiceNumber);

        var line = Assert.Single(section.Lines);
        Assert.Equal("071899051015", line.Gtin);
        Assert.Equal(30, line.Units);
        Assert.Equal(2.91m, line.UnitCost);
        Assert.Equal(section.Total, section.Lines.Sum(l => l.ExtendedCost));
    }

    /// <summary>The suggestion the operator is shown, so they have something to compare rather than
    /// having to work out what the smudge said.</summary>
    [Fact]
    public void TheDamagedDateIsOfferedAsASuggestionToCheck()
    {
        var section = Assert.Single(BlueBellPdfParser.ParseAll(TicketLines, []));
        Assert.Equal(new DateOnly(2023, 12, 14), section.SuggestedDate);
    }

    /// <summary>A ticket that read cleanly must offer no suggestion — there is nothing to check.</summary>
    [Fact]
    public void ATicketWithAReadableDateOffersNoSuggestion()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "TOTAL 1.00 10.00 29.80",
        ];
        var section = Assert.Single(BlueBellPdfParser.ParseAll(lines, []));

        Assert.Equal(new DateOnly(2026, 1, 8), section.Date);
        Assert.Null(section.SuggestedDate);
    }

    /// <summary>
    /// A damaged date still has to break one ticket from the next. Merging a Sales ticket into the
    /// Returns ticket below it would put both invoices' line items against a single TOTAL, and the
    /// checksum would then refuse a delivery that was otherwise fine.
    /// </summary>
    [Fact]
    public void ADamagedDateStillSeparatesTheSalesTicketFromTheReturnsTicket()
    {
        string[] lines =
        [
            "DATE 01-08-2026 TIME 13:41 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0925 007189905101 PT GR SUPREME",
            "1.00 10.00 2.98 29.80",
            "TOTAL 1.00 10.00 29.80",
            "DATE Ol-08-2026 TIME 13:42 BRANCH# 33 ROUTE# 10 TRUCK# 1404",
            "Returns",
            "0925 007189905101 PT GR SUPREME",
            "-1.00 2.98 -2.98",
            "TOTAL -2.98",
        ];
        var sections = BlueBellPdfParser.ParseAll(lines, []);

        Assert.Equal(2, sections.Count);
        Assert.Equal(29.80m, sections[0].Total);
        Assert.Equal(new DateOnly(2026, 1, 8), sections[0].Date);
        Assert.Equal(-2.98m, sections[1].Total);
        Assert.Null(sections[1].Date);                                   // "Ol-08-2026" is not trusted
        Assert.Equal(new DateOnly(2026, 1, 8), sections[1].SuggestedDate);
    }

    /// <summary>
    /// An unknown product whose description starts "DZ" is a carton of twelve even when the scanner
    /// glued the flavour onto it. Seen on PROD 0464 "DZJP-Z DUTCH CUP", which was being booked as
    /// 2 cartons at 6.00 rather than 24 cups at 0.50 — and the money reconciles either way, so
    /// nothing would have caught it.
    /// </summary>
    [Fact]
    public void AnUnknownDozenProductIsCountedInCupsEvenWhenDzRunsIntoTheFlavour()
    {
        string[] lines =
        [
            "DATE 11-21-2023 TIME 14:00 BRANCH# 33 ROUTE# 10 TRUCK# 697",
            "Sales",
            "EA/COST RETAIL BX CNT QTY PRICE EXTENDED",
            "0464 007189921200 DZJP-Z DUTCH CUP`",
            "1.-00, 2'.00 6.00 12.00",
            "TOTAL 1.00 2.00 12.00",
        ];
        var warnings = new List<ConversionWarning>();
        var section = Assert.Single(BlueBellPdfParser.ParseAll(lines, warnings));

        var line = Assert.Single(section.Lines);
        Assert.Equal(24, line.Units);
        Assert.Equal(0.50m, line.UnitCost);
        Assert.Equal(12.00m, line.ExtendedCost);

        // Guessed from the description, so the operator is told to check it against the paper.
        Assert.Contains(warnings, w => w.Message.Contains("0464", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public void TheDateTheOperatorTypesIsTheDateInTheXml()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "DownloadFile (2).pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(SamplePdf));

        var prompt = new Prompt("033103483286", new DateOnly(2023, 12, 14));
        var outcome = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt)
            .ConvertFile(BlueBell(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        string path = Assert.Single(outcome.FinalOutputPaths);
        Assert.Equal("SSCS_033103483286.xml", Path.GetFileName(path));

        var doc = XDocument.Load(path);
        Assert.Equal("2023-12-14", doc.Descendants("InvoiceDate").Single().Value);
        Assert.Equal("87.30", doc.Descendants("TotalInvoiceDueAmt").Single().Value);
        Assert.Equal("30", doc.Descendants("InvoiceUnitQty").Single().Value);

        // The operator was told the date was missing, and shown what the scan seemed to say.
        var seen = Assert.Single(prompt.Seen);
        Assert.True(seen.NeedsDate);
        Assert.Null(seen.Date);
        Assert.Equal(new DateOnly(2023, 12, 14), seen.SuggestedDate);
    }

    /// <summary>
    /// A prompt must never be able to move a date the ticket printed clearly. Only the invoice
    /// number is ever taken from the operator on a ticket that scanned properly.
    /// </summary>
    [Fact]
    public void ADateTheTicketPrintedIsNotOverwrittenByThePrompt()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "blue bell ticket.pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(@"Fixtures\PDF\BLUEBELL_033100786112.pdf"));

        var prompt = new Prompt("033100786112", new DateOnly(2001, 1, 1));
        var outcome = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt)
            .ConvertFile(BlueBell(input), dest);

        Assert.True(outcome.Success, outcome.UserMessage);
        foreach (string path in outcome.FinalOutputPaths)
            Assert.Equal("2026-03-19", XDocument.Load(path).Descendants("InvoiceDate").Single().Value);

        Assert.All(prompt.Seen, r => Assert.False(r.NeedsDate));
    }

    /// <summary>
    /// If the date is missing and none comes back, the ticket is refused. Writing year 0001 into
    /// SSCS would be worse than not writing at all.
    /// </summary>
    [Fact]
    public void NoDateFromTheTicketAndNoneFromTheOperatorIsRefused()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "DownloadFile (2).pdf");
        File.WriteAllBytes(input, File.ReadAllBytes(SamplePdf));

        var prompt = new Prompt("033103483286", default);
        var outcome = new ConversionService(new ConversionLogger(Path.Combine(tmp.Path, "logs")), "test", prompt)
            .ConvertFile(BlueBell(input), dest);

        Assert.False(outcome.Success);
        Assert.Equal(PdfReasonCodes.NoDate, outcome.ReasonCode);
        Assert.Empty(Directory.GetFiles(dest));
    }
}
