using EdiToSscs.Core;

// Converts a converter vendor's invoice WITHOUT Gemini. A person (or an AI chat) reads the page
// pictures and types the reading in below, in the same shape Gemini returns; this then runs it
// through the converter's own ConversionService exactly as the app would - same parser, same
// warnings, same Modisoft writer. Use it when Google is busy, and to make test files.
//
// The four readings below are the Modisoft test invoices (Downloads\Modisoft test invoices), each
// read off the picture and checked against its printed totals on 2026-10-06. To convert another
// invoice, replace that vendor's rows and totals and its line in `jobs`, keeping to the vendor's AI
// rules in GeminiVisionReader: Bimbo's barcode is the 10 printed digits, Blue Bell's packSize is
// "12" only on a DZ row, Red Bull's credit price is the printed per-can rate, and so on.
//
// Usage: dotnet run --project tools/HandReading -c Release -- <output folder> [input folder]
string logs = Path.Combine(Path.GetTempPath(), "HandReadingLogs");
string outDir = args.Length > 0 ? args[0] : throw new ArgumentException("Give an output folder.");
string inDir = args.Length > 1 ? args[1] : @"C:\Users\Kashf\Downloads\Modisoft test invoices";

var jobs = new (string File, PdfVendor Vendor, string Invoice, DateOnly Date)[]
{
    ("Blue Bell 033100786112 (returns, total 325.50).pdf", PdfVendor.BlueBell, "033100786112", new(2026, 3, 19)),
    ("Chubiwinos 6V9389 (dump, total 460.23).pdf", PdfVendor.Chubiwinos, "6V9389", new(2026, 6, 15)),
    ("Bimbo 84294890013239 (9 returns, total 335.07).pdf", PdfVendor.Bimbo, "84294890013239", new(2026, 7, 28)),
    ("Red Bull 2030211863 (credit page, total 342.50).pdf", PdfVendor.RedBull, "2030211863", new(2026, 11, 24)),
};

foreach (var job in jobs)
{
    var prompt = new Prompt(job.Invoice, job.Date);
    var service = new ConversionService(new ConversionLogger(logs), "hand-read",
        prompt, new HandReading());
    var outcome = service.ConvertFile(new QueuedFile
    {
        InputPath = Path.Combine(inDir, job.File), Kind = ConversionKind.Pdf, Vendor = job.Vendor,
        Target = OutputTarget.Modisoft,
    }, outDir);
    Console.WriteLine($"== {job.Vendor}: success={outcome.Success} {outcome.UserMessage}");
    foreach (var w in outcome.Warnings) Console.WriteLine($"   WARN {w.ReasonCode}: {w.Message}");
    foreach (var p in outcome.FinalOutputPaths) Console.WriteLine($"   -> {p}");
}

sealed class Prompt(string invoice, DateOnly date) : IInvoiceDetailsPrompt
{
    public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request) => new(invoice, date);
}

sealed class HandReading : IInvoiceVisionReader, IBlueBellVisionReader, IChubiwinosVisionReader,
    IBimboVisionReader, IRedBullVisionReader
{
    public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) => VisionReading.Empty;

    public BlueBellVisionReading ReadBlueBell(byte[] pdfBytes)
    {
        BlueBellVisionRow Row(string page, string prod, string desc, string upc, string qty,
            string price, string ext, bool credit) => new()
        {
            Page = page, ProductNumber = prod, Description = desc, Upc = upc, Quantity = qty,
            Price = price, Extended = ext, PackSize = desc.StartsWith("DZ") ? "12" : "1", IsCredit = credit,
        };
        return new()
        {
            Status = BlueBellVisionStatus.Success,
            Rows =
            [
                Row("1", "0800", "1/2 GAL GR SUP", "007189903720", "12.00", "6.85", "82.20", false),
                Row("1", "0781", "1/2 DUT CHOC", "007189903733", "4.00", "6.85", "27.40", false),
                Row("1", "0925", "PT GR SUPREME", "007189905101", "60.00", "2.98", "178.80", false),
                Row("1", "0122", "DZ 3OZ HMV CUPS", "007189921133", "2.00", "6.36", "12.72", false),
                Row("1", "0151", "DZ VANILLA SAND", "007189921122", "2.00", "6.84", "13.68", false),
                Row("1", "0165", "DZ CNC SAND", "007189921123", "2.00", "6.84", "13.68", false),
                Row("2", "0925", "PT GR SUPREME", "007189905101", "-1.00", "2.98", "2.98", true),
            ],
            PageSummaries =
            [
                new() { Page = "1", IsCredit = false, TotalQuantity = "82.00", TotalAmount = "328.48", BalanceDue = "328.48" },
                new() { Page = "2", IsCredit = true, TotalQuantity = "-1.00", TotalAmount = "-2.98", UnitsReturned = "-1.00", BalanceDue = "-2.98" },
            ],
        };
    }

    public ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes)
    {
        ChubiwinosVisionRow Row(string item, string desc, string code, string qty, string price,
            string amount, bool credit) => new()
        {
            ItemNumber = item, Description = desc, Barcode = code, Quantity = qty, Price = price,
            Amount = amount, IsCredit = credit,
        };
        return new()
        {
            Status = ChubiwinosVisionStatus.Success,
            Rows =
            [
                Row("220", "CHETTO COLMILLO -100 g", "7500478044207", "20", "2.79", "55.80", false),
                Row("224", "TOSTITOS SALSA VERDE 175 g", "7501011130869", "24", "2.79", "66.96", false),
                Row("230", "RANCHERITO- 145 g", "7501011114517", "30", "2.79", "83.70", false),
                Row("240", "DORITOS NACHOS- 150 g", "7501011131064", "24", "2.79", "66.96", false),
                Row("245", "PAPA CRUJIENTE XTRA HOT", "7501011180499", "30", "2.79", "83.70", false),
                Row("299", "DORITOS FLAMING HOT -223gm", "7500478012114", "18", "3.89", "70.02", false),
                Row("800", "LEO CHICHARRON W/ HOT SAUCE 100g", "604722008186", "12", "2.99", "35.88", false),
                Row("205", "PAPAS ADOBADAS 105 g", "7501011167667", "1", "2.79", "2.79", true),
            ],
            SectionTotals =
            [
                new() { Section = "SALES", Quantity = "158", Amount = "463.02" },
                new() { Section = "DUMP", Quantity = "1", Amount = "(2.79)" },
            ],
            Sales = "463.02", Credits = "(2.79)", NetAmount = "460.23", SalesTax = "0.00",
            TotalDue = "460.23", TotalPayment = "0.00", InvoiceBalance = "460.23",
            VendorMarkers = ["CHUBIWINOS S&G", "SALES SECTION", "DUMP SECTION", "INVOICE BALANCE"],
        };
    }

    public BimboVisionReading ReadBimbo(byte[] pdfBytes)
    {
        BimboVisionRow Row(string code, string item, string desc, string units, string price,
            string amount, bool credit) => new()
        {
            Barcode = code, ItemNumber = item, Description = desc, Units = units, Price = price,
            Amount = amount, IsCredit = credit, Page = 1,
        };
        return new()
        {
            Status = BimboVisionStatus.Success,
            Rows =
            [
                Row("7432309183", "008788", "BIM PANQUE NUE FS FC", "3", "3.0200", "9.06", false),
                Row("7432301085", "008771", "BIM PANQUE CAS FS FC", "2", "3.0200", "6.04", false),
                Row("7432309975", "009290", "BIM NITO DUO SS2P FC", "6", "1.9400", "11.64", false),
                Row("7432309165", "008927", "BIM ROLE CAN SS2P FC", "4", "1.9400", "7.76", false),
                Row("7432309139", "008782", "BIM MANTECD SS2P FC", "4", "1.9400", "7.76", false),
                Row("7432309122", "008931", "BIM MINMANT SS4P FC", "9", "1.9400", "17.46", false),
                Row("7432309173", "008920", "BIM DORADITAS SS3PFC", "4", "1.9400", "7.76", false),
                Row("7432373951", "968113", "BIM HRSH MIN CROSS6P", "15", "1.9400", "29.10", false),
                Row("7432373943", "967744", "BIM HRSH MINI MFN2P", "4", "1.9400", "7.76", false),
                Row("7432309501", "008796", "MLA GANSITO SS2P BX", "2", "1.9400", "3.88", false),
                Row("7432309535", "008802", "MLA PINGUINO SS2P FC", "4", "1.9400", "7.76", false),
                Row("7432305156", "008776", "MLA PASTISE SS10P FC", "4", "1.9400", "7.76", false),
                Row("7432309523", "008801", "MLA SPNCH STR SS6PBX", "12", "1.9400", "23.28", false),
                Row("7432373929", "967638", "MLA SPN MNGO CKYSS6P", "8", "1.9400", "15.52", false),
                Row("7432373899", "950664", "MLA TRIKIS TUBE 4.2Z", "8", "1.9400", "15.52", false),
                Row("7432309206", "008791", "MLA POLVORO FS15P FC", "5", "2.5900", "12.95", false),
                Row("7087073402", "014155", "MB PWD DON SS 6P 3Z", "8", "1.9400", "15.52", false),
                Row("7087073400", "014153", "MB FRST DON SS6P3.25", "8", "1.9400", "15.52", false),
                Row("7087000002", "007955", "MB GLZ HNY BN SS 4Z", "12", "1.9400", "23.28", false),
                Row("7087073405", "014744", "MB ICED HNY BN SS 4Z", "12", "1.9400", "23.28", false),
                Row("7087004183", "001825", "MB GLZ CIN RL SS 4Z", "12", "1.9400", "23.28", false),
                Row("7087000742", "007966", "MB APPLE PIE SS 4Z", "4", "1.9400", "7.76", false),
                Row("7087000743", "007967", "MB CHERRY PIE SS 4Z", "4", "1.9400", "7.76", false),
                Row("7087096086", "967878", "MB MINI PWD DON 9Z", "12", "3.0200", "36.24", false),
                Row("7087096080", "967872", "MB FRST MINIDON 9.5Z", "12", "3.0200", "36.24", false),
                Row("7087096079", "967871", "MB CIN MINI DON 9Z", "12", "3.0200", "36.24", false),
                Row("7432309165", "008927", "BIM ROLE CAN SS2P FC", "2", "1.9400", "3.88", true),
                Row("7432373943", "967744", "BIM HRSH MINI MFN2P", "3", "1.9400", "5.82", true),
                Row("7432373929", "967638", "MLA SPN MNGO CKYSS6P", "15", "1.9400", "29.10", true),
                Row("7432373936", "967705", "MLA HRSH CH SNCK SS", "8", "1.9400", "15.52", true),
                Row("7087073402", "014155", "MB PWD DON SS 6P 3Z", "1", "1.9400", "1.94", true),
                Row("7087073400", "014153", "MB FRST DON SS6P3.25", "3", "1.9400", "5.82", true),
                Row("7087000743", "007967", "MB CHERRY PIE SS 4Z", "2", "1.9400", "3.88", true),
                Row("7087096086", "967878", "MB MINI PWD DON 9Z", "1", "3.0200", "3.02", true),
                Row("7087096080", "967872", "MB FRST MINIDON 9.5Z", "4", "3.0200", "12.08", true),
            ],
            Deliveries = new("190", "416.13"),
            Returns = new("-39", "-81.06"),
            TicketUnits = "151", TicketTotal = "335.07", CashDue = "335.07",
        };
    }

    public RedBullVisionReading ReadRedBull(byte[] pdfBytes)
    {
        RedBullVisionRow Row(string id, string cases, string units, string desc, string price,
            string amount, string code, bool credit) => new()
        {
            ItemNumber = id, Cases = cases, Units = units, Description = desc, Price = price,
            Deposit = "0.00", Discount = "0.00", Sugar = "0.00", Amount = amount, Barcode = code,
            IsCredit = credit,
        };
        return new()
        {
            Status = RedBullVisionStatus.Success,
            Rows =
            [
                Row("RB1718", "1", "24", "RED BULL 8.4OZ LS", "40.75", "40.75", "611269991000", false),
                Row("RB33049", "2", "24", "RED BULL 16OZ LS", "35.11", "70.22", "611269546019", false),
                Row("RB36463", "1", "12", "RED BULL 20OZ LS", "42.23", "42.23", "611269133448", false),
                Row("RB246729", "2", "48", "WNTR FUJAPP 12OZ LS", "53.45", "106.90", "611269002867", false),
                Row("RB4816", "2", "48", "RED BULL 12OZ LS", "53.45", "106.90", "611269818994", false),
                Row("RB203752", "0.04", "1", "BLUE 12OZ LS", "2.23", "2.23", "611269182460", true),
                Row("RB240073", "0.04", "1", "RB AMBER 12OZ LS", "2.23", "2.23", "611269001303", true),
                Row("RB247584", "0.08", "2", "PINK 12OZ LS", "2.22", "4.45", "611269003123", true),
                Row("RB221027", "0.25", "6", "COCONUT 12OZ LS", "2.23", "13.36", "611269321210", true),
                Row("RB4816", "0.04", "1", "RED BULL 12OZ LS", "2.23", "2.23", "611269818994", true),
            ],
            PageSummaries =
            [
                new() { Page = "1", UnitsDelivered = "156", UnitsPickedUp = "(0)", Invoice = "367.00", Roa = "342.50" },
                new() { Page = "2", UnitsDelivered = "0", UnitsPickedUp = "(0.4", Invoice = "(24.50)", Roa = "0.00" },
            ],
        };
    }
}
