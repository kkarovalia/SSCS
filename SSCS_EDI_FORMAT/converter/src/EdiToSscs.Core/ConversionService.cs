using System.Security.Cryptography;

namespace EdiToSscs.Core;

/// <summary>One finished XML file, named and held in memory.</summary>
public sealed record ConvertedDocument(string FileName, byte[] Bytes);

/// <summary>Outcome of converting a single file end-to-end (read, convert, validate, write).</summary>
public sealed class ConversionOutcome
{
    public required bool Success { get; init; }
    public bool HasWarnings { get; init; }
    /// <summary>True when the operator dismissed a prompt; the file is skipped, not failed.</summary>
    public bool Canceled { get; init; }

    /// <summary>
    /// Every file written. One per invoice; every PDF vendor currently produces a single invoice
    /// per file, but the write path stays plural so that nothing has to change if one does not.
    /// </summary>
    public IReadOnlyList<string> FinalOutputPaths { get; init; } = [];

    public string? FinalOutputPath => FinalOutputPaths.Count > 0 ? FinalOutputPaths[0] : null;

    /// <summary>
    /// The converted XML itself, kept alongside the paths so the viewer can show a file without
    /// reading it back off disk.
    /// </summary>
    public IReadOnlyList<ConvertedDocument> Documents { get; init; } = [];

    public ConversionResult? Result { get; init; }
    /// <summary>Plain-language message shown to the user (success summary or failure reason).</summary>
    public required string UserMessage { get; init; }
    public string? ReasonCode { get; init; }

    /// <summary>
    /// Every note raised while converting, including — for a scanned invoice — the picture of the
    /// printed row each one is about. The shell shows the pictures; nothing else needs them.
    /// </summary>
    public IReadOnlyList<ConversionWarning> Warnings { get; init; } = [];

}

/// <summary>
/// Orchestrates a single file conversion with safe file handling:
/// the source file is only ever opened for reading; output is written to a temporary
/// file in the destination, flushed, re-read and verified, then atomically moved to a
/// non-conflicting final name. A failure never leaves a partial or misleading file behind.
/// </summary>
public sealed class ConversionService : IFileConverter
{
    private readonly ConversionLogger _logger;
    private readonly string _appVersion;
    private readonly IInvoiceDetailsPrompt? _invoiceDetailsPrompt;
    private readonly IInvoiceVisionReader? _visionReader;

    public ConversionService(
        ConversionLogger? logger = null,
        string? appVersion = null,
        IInvoiceDetailsPrompt? invoiceDetailsPrompt = null,
        IInvoiceVisionReader? visionReader = null)
    {
        _logger = logger ?? new ConversionLogger();
        _appVersion = appVersion ?? typeof(ConversionService).Assembly.GetName().Version?.ToString() ?? "unknown";
        _invoiceDetailsPrompt = invoiceDetailsPrompt;
        _visionReader = visionReader;
    }

    /// <summary>
    /// A second reading of a scanned invoice, taken off the picture. Null reader, no key, no
    /// network, a bad reply — every one of them gives an empty reading and the parse carries on
    /// exactly as it did before there was one. Never throws.
    /// </summary>
    private VisionReading ReadWithVision(byte[] pdfBytes, VisionVendor vendor)
    {
        if (_visionReader is null) return VisionReading.Empty;
        try
        {
            return _visionReader.TryRead(pdfBytes, vendor) ?? VisionReading.Empty;
        }
        catch
        {
            return VisionReading.Empty;
        }
    }

    /// <summary>Convenience overload for EDI files (used by tests and any single-path caller).</summary>
    public ConversionOutcome ConvertFile(string inputPath, string destinationFolder)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return ConvertFile(new QueuedFile { InputPath = inputPath }, destinationFolder);
    }

    public ConversionOutcome ConvertFile(QueuedFile file, string destinationFolder)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        string inputPath = file.InputPath;
        string inputFileName = Path.GetFileName(inputPath);
        var log = new LogEntry
        {
            AppVersion = _appVersion,
            InputFileName = inputFileName,
            InputPath = inputPath,
            DestinationPath = destinationFolder,
        };

        byte[] inputBytes;
        try
        {
            inputBytes = ReadInputBytes(inputPath);
        }
        catch (Exception ex)
        {
            return Fail(log, ex, ReadFailureMessage(inputFileName, ex));
        }

        log.InputBytes = inputBytes.LongLength;

        Converted converted;
        try
        {
            converted = file.Kind == ConversionKind.Pdf
                ? ConvertPdf(inputBytes, inputFileName, file.Vendor)
                : ConvertEdi(inputBytes, inputFileName);
        }
        catch (OperationCanceledException)
        {
            log.Status = "Canceled";
            log.UserFriendlyReason = "The operator dismissed a prompt without answering it.";
            _logger.TryWrite(log);
            return new ConversionOutcome
            {
                Success = false,
                Canceled = true,
                UserMessage = $"Canceled {inputFileName} - a prompt was dismissed without an answer. The original file was not changed.",
            };
        }
        catch (EdiConversionException ex)
        {
            log.FailureLine = ex.LineNumber;
            return Fail(log, ex, BuildConversionFailureMessage(inputFileName, ex), ex.ReasonCode);
        }
        catch (Exception ex)
        {
            return Fail(log, ex, $"Could not convert {inputFileName}.\n\nAn unexpected problem occurred. The original file was not changed.");
        }

        log.DetectedFormat = converted.Format;
        log.RecordsProcessed = converted.RecordCount;
        log.UpcERecordsExpanded = converted.UpcExpanded;
        log.OutputBytes = converted.Documents.Sum(d => d.Bytes.LongLength);
        if (converted.HasWarnings)
            log.Warnings = converted.Warnings.Select(w => $"line {w.LineNumber} [{w.ReasonCode}]: {w.Message}").ToList();

        // Every document is written and verified as a temporary file BEFORE any of them claims a
        // final name, so a ticket that produces two invoices never lands half-written.
        var temps = new List<string>();
        var finals = new List<string>();
        try
        {
            foreach (var doc in converted.Documents)
            {
                string tempPath = Path.Combine(destinationFolder, $"~sscs-{Guid.NewGuid():N}.tmp");
                temps.Add(tempPath);

                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(doc.Bytes, 0, doc.Bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                byte[] written = File.ReadAllBytes(tempPath);
                if (!written.AsSpan().SequenceEqual(doc.Bytes))
                    throw new IOException("The written file did not match the converted data (disk problem?).");
            }

            // Compute success metadata BEFORE the first rename so that nothing after the commit
            // point can throw and misreport an already-written file as failed.
            log.Status = converted.HasWarnings ? "Warning" : "Success";
            log.InputSha256 = System.Convert.ToHexString(SHA256.HashData(inputBytes));
            log.OutputSha256 = System.Convert.ToHexString(SHA256.HashData(converted.Documents[0].Bytes));

            for (int i = 0; i < converted.Documents.Count; i++)
                finals.Add(MoveToAvailableName(temps[i], destinationFolder, converted.Documents[i].FileName, inputPath));

            log.OutputFileName = string.Join(", ", finals.Select(Path.GetFileName));
            _logger.TryWrite(log);

            string saved = string.Join(", ", finals.Select(Path.GetFileName));
            string message = finals.Count == 1
                ? $"Saved {saved} to {destinationFolder}"
                : $"Saved {finals.Count} invoices ({saved}) to {destinationFolder}";

            // What was read goes first, because it is the line the operator can actually check
            // against the paper in front of them — and for the invoice number and date it is the
            // only check there is. The Details column shows the first line, so this is what they see
            // without opening anything.
            if (converted.Headline.Length > 0)
                message = $"{converted.Headline}\n{message}";

            if (converted.HasWarnings)
                message += $"\n{string.Join("\n", converted.Warnings.Select(w => w.Message))}";

            return new ConversionOutcome
            {
                Success = true,
                HasWarnings = converted.HasWarnings,
                Warnings = converted.Warnings,
                FinalOutputPaths = finals,
                Documents = converted.Documents,
                Result = converted.EdiResult,
                UserMessage = message,
            };
        }
        catch (Exception ex)
        {
            // Roll back any sibling already renamed, so a part-written ticket never reaches SSCS.
            foreach (string done in finals)
                try { File.Delete(done); } catch { /* best effort */ }
            return Fail(log, ex, WriteFailureMessage(inputFileName, destinationFolder, ex));
        }
        finally
        {
            // A failed conversion must never leave a temporary file that looks like an invoice.
            foreach (string temp in temps)
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Unifies the EDI and PDF conversion outputs so the safe-write path below is shared.</summary>
    private sealed record Converted(
        IReadOnlyList<ConvertedDocument> Documents, string Format, int RecordCount,
        IReadOnlyList<ConversionWarning> Warnings, int UpcExpanded, ConversionResult? EdiResult,
        /// <summary>What was read off the ticket, shown beside the saved file. See Headline.</summary>
        string Headline = "")
    {
        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>
    /// One line saying what was actually read off the invoice, put in front of the operator beside
    /// the saved file.
    ///
    /// The invoice number and the date are the last figures on a scanned invoice that **nothing can
    /// check**: no check digit, no total to reconcile against, and one printed copy each. Every
    /// other field now has something — barcodes have their check digit, the money has AMOUNT DUE,
    /// the quantities have the package subtotals and TOT SALE. These two have only a person.
    ///
    /// And the scanner does flip digits in exactly this kind of short numeric field: two scans of
    /// invoice 5218300220, ten minutes apart, read its printed TIME as 11:29 and 11:19. The same
    /// slip in the date books a delivery into the wrong period, and in the number it breaks the
    /// match with the store's payable — neither with any symptom. Showing them costs nothing and is
    /// the only verification available.
    /// </summary>
    private static string Headline(Invoice invoice)
    {
        int units = invoice.Lines.Sum(l => l.Units);
        return $"Invoice {invoice.InvoiceNumber}   {invoice.Date:MM-dd-yyyy}   {invoice.Total:0.00}   "
            + $"{invoice.Lines.Count} line{(invoice.Lines.Count == 1 ? "" : "s")}, {units} units";
    }

    private static Converted ConvertEdi(byte[] inputBytes, string inputFileName)
    {
        var r = EdiConverter.Convert(inputBytes);
        var doc = new ConvertedDocument(OutputNaming.BuildOutputFileName(inputFileName), r.OutputBytes);
        return new Converted([doc], r.Format.ToString(), r.RecordCount, r.Warnings, r.UpcEExpandedCount, r);
    }

    private Converted ConvertPdf(byte[] inputBytes, string inputFileName, PdfVendor vendor)
    {
        if (!PdfTextExtractor.LooksLikePdf(inputBytes))
            throw new EdiConversionException(PdfReasonCodes.NotAPdf,
                "This file does not look like a PDF. Please drop the vendor's PDF invoice.");

        var warnings = new List<ConversionWarning>();

        // Red Bull is read from positioned words rather than joined lines: its columns cannot be
        // told apart by reading order once the scan has split or dropped one.
        if (vendor == PdfVendor.RedBull)
            return ConvertRedBull(
                TextOr([], () => PdfTextExtractor.ExtractPages(inputBytes), vendor),
                inputFileName, warnings, inputBytes);

        IReadOnlyList<string> lines =
            TextOr([], () => PdfTextExtractor.ExtractLines(inputBytes), vendor);

        if (vendor == PdfVendor.BlueBell)
            return ConvertBlueBell(lines, inputFileName, warnings);

        if (vendor == PdfVendor.Chubiwinos)
            return ConvertChubiwinos(lines, inputFileName, warnings);

        if (vendor == PdfVendor.Bimbo)
            return ConvertBimbo(lines, inputFileName, warnings);

        return ConvertDrPepper(lines, inputFileName, warnings, inputBytes);
    }

    /// <summary>
    /// The PDF's text layer, or nothing at all where there is none and a second reader might still
    /// make something of the page.
    ///
    /// "No searchable text" used to be the end of the road, and for most of this application's life
    /// that was right — there was nothing else to read the page with. There is now, on the two
    /// vendors that have one, so the refusal moves downstream: the parser decides, once it has seen
    /// what the picture gave up. A vendor with no vision reader, or a run with none configured, still
    /// fails here with the same message it always did.
    /// </summary>
    private IReadOnlyList<T> TextOr<T>(
        IReadOnlyList<T> nothing, Func<IReadOnlyList<T>> extract, PdfVendor vendor)
    {
        try
        {
            return extract();
        }
        catch (EdiConversionException ex) when (ex.ReasonCode == PdfReasonCodes.NoText
                                                && _visionReader is not null
                                                && vendor is PdfVendor.RedBull or PdfVendor.DrPepper)
        {
            return nothing;
        }
    }

    /// <summary>
    /// A Dr Pepper invoice prints its own header, but the **invoice number is typed anyway**, the
    /// same as every other PDF vendor. It is the one field on the page nothing can check — no check
    /// digit, no total to reconcile against, one printed copy — and this scanner demonstrably flips
    /// digits in short numeric fields: two scans of ticket 5218300220 read its printed TIME as 11:29
    /// and 11:19. A wrong number breaks the match with the store's payable and has no symptom.
    ///
    /// That one window carries the rest of the header with it. The date and the total are **shown**
    /// beside the box when the scan read them — which is the only check those two can have, and it
    /// lands while the operator is holding the paper — and turn into boxes of their own only when
    /// the scan destroyed them. A row's own figures are asked for from inside the parser, by which
    /// point the package subtotals have already settled everything they can.
    /// </summary>
    private Converted ConvertDrPepper(
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings, byte[] pdfBytes)
    {
        // The picture first, because whether this page is readable at all now depends on it.
        var seen = ReadWithVision(pdfBytes, VisionVendor.DrPepper);

        // Before anything is asked: a page that reads as nothing at all is not an invoice with a
        // damaged field on it, and putting three prompts in front of the operator before saying so
        // is three windows they cannot answer.
        //
        // Unless the picture read it. A sheet that went through crooked is perfectly legible to a
        // person and that is the whole reason for a second reader — but the text layer is then
        // **discarded entirely**, not merely tolerated. The upside-down scan is not blank: it has 136
        // lines on it and some of them match as product rows ("SO'6Z ZI T 9Yd"), so leaving them in
        // would put that garbage into the invoice alongside what the picture read.
        if (!DrPepperPdfParser.LooksReadable(lines))
        {
            if (seen.Rows.Count == 0) DrPepperPdfParser.RequireAReadableInvoice(lines);
            lines = [];
        }

        DateOnly? date = DrPepperPdfParser.FindDate(lines);
        decimal? total = DrPepperPdfParser.FindAmountDue(lines) ?? seen.PrintedTotal;

        // Asking for the total does not weaken the sum-must-match gate, it restores it: a figure a
        // person read off the paper is worth more than one OCR guessed at.
        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Dr Pepper", date, total ?? 0m, 0, 1, 1, "Dr Pepper",
            NeedsTotal: total is null));

        Invoice invoice = DrPepperPdfParser.Parse(
            lines, warnings, f => AskDrPepperField(inputFileName, f, pdfBytes),
            keyed.Date, keyed.InvoiceNumber, total ?? (keyed.Total == 0m ? null : keyed.Total), seen);

        var doc = new ConvertedDocument(OutputNaming.BuildOutputFileName(inputFileName), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/DrPepper", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// Asks for one figure on one Dr Pepper row that the scan destroyed and the invoice's own
    /// subtotals could not work out, naming the row by its description and barcode so the operator
    /// does not have to count down the page. Cancelling skips the file: a made-up quantity books
    /// stock that never arrived, and nothing downstream would catch it.
    /// </summary>
    private string AskDrPepperField(string inputFileName, DrPepperUnknownField field, byte[] pdfBytes)
    {
        string what = field.Field switch
        {
            LineField.Barcode => "barcode",
            LineField.Units => "unit count",
            _ => "amount",
        };

        if (_invoiceDetailsPrompt is null)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The {what} for \"{field.Description}\" could not be read from this invoice and could not be "
                + "worked out from the rest of it, so it has to be typed in - but no prompt is available.");

        // The row itself, so the answer is on screen rather than somewhere on the paper. The line
        // above is the product line, which is what makes the row recognisable.
        // The barcode is printed on the product line, one above the priced row; units and amount are
        // on the priced row itself.
        bool barcode = field.Field == LineField.Barcode;
        int focusLine = barcode ? Math.Max(1, field.LineNo - 1) : field.LineNo;
        var snip = field.LineNo > 0
            ? PdfRowSnipper.TryCut(
                pdfBytes, Math.Max(1, field.LineNo - 1), field.LineNo,
                field.PrintedValue, focusLine, barcode,
                lastOfDuplicates: field.Field == LineField.Amount)
            : PdfRowSnipper.TryCutWholePage(
                pdfBytes, field.Page, field.PageUpsideDown);

        bool wholePage = field.LineNo <= 0 && snip is not null;

        string? typed = _invoiceDetailsPrompt.AskLineField(new LineFieldRequest(
            inputFileName, "Dr Pepper", field.Field, field.Position, field.LineCount,
            field.Gtin, field.Description, field.PrintedValue, null, field.Amount, snip,
            snip is null || wholePage ? null : PdfRowSnipper.TryCutHeader(pdfBytes, field.LineNo),
            ColumnLegend: ColumnLegend(PdfVendor.DrPepper, field.IsCredit),
            ColumnName: field.Field switch
            {
                LineField.Units => "UNITS",
                LineField.Amount => "AMOUNT",
                _ => "",
            },
            Hint: Hint(wholePage, field.Field switch
            {
                LineField.Units => "Cans or bottles, not cases.",
                LineField.Barcode => "12 digits, before the slash.",
                _ => "",
            }),
            WholePage: wholePage));

        if (string.IsNullOrWhiteSpace(typed))
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Skipped - the {what} for \"{field.Description}\" on {inputFileName} was not supplied. "
                + "The original file was not changed.");

        return typed;
    }

    /// <summary>
    /// The vendor's column names, in the order they are printed, for the strip above the row.
    ///
    /// A backstop for the printed header rather than a replacement: <see cref="PdfRowSnipper"/> cuts
    /// the real one off the same page at the same width, which is far better because it lines up —
    /// UNITS sits over the units. This is what gets shown when there is nothing to cut, and there
    /// are three ways to get there. A Dr Pepper header the scan damaged until it no longer reads as
    /// one; a Red Bull page where none was found; a row with no line number at all.
    ///
    /// Worth having anyway, because the strip is what makes a cut-out row readable. A snip is a
    /// couple of printed lines out of the middle of a ticket, and by item 16 the column names are a
    /// foot further up the page — so a row without them is a line of bare figures and no way to tell
    /// the NET from the AMOUNT.
    ///
    /// <paramref name="credit"/> picks the section, which is a real difference and not a detail:
    /// Dr Pepper's CREDITS block prints no WHSLE column, so a sales legend over a credit row would
    /// have the operator counting across to the wrong figure. Red Bull prints one column set for
    /// both and only brackets the figures.
    /// </summary>
    private static string ColumnLegend(PdfVendor vendor, bool credit) => vendor switch
    {
        PdfVendor.RedBull => "ID   QTY   UNITS   DESCRIPTION   PRICE   DEP   DISC   SUGAR   TOTAL",
        PdfVendor.DrPepper when credit =>
            "DESCRIPTION   UPC/SKU\nCASES   UNITS   NET   TAX   AMOUNT",
        PdfVendor.DrPepper =>
            "DESCRIPTION   UPC/SKU\nWHSLE   CASES   UNITS   NET   TAX   AMOUNT",
        _ => "",
    };

    /// <summary>
    /// The line under the box. When the whole page is being shown rather than one cut-out row, the
    /// operator has to be told that — otherwise they look for a highlight that is deliberately not
    /// there. Said first, because it changes what they do with the picture; the field's own hint
    /// follows it.
    /// </summary>
    private static string Hint(bool wholePage, string usual) =>
        !wholePage ? usual
        : usual.Length > 0 ? "Nothing is marked - find the row by its name. " + usual
        : "Nothing is marked - find the row by its name.";

    /// <summary>
    /// A Blue Bell delivery ticket carries a Sales block and, when goods went back on the truck, a
    /// Returns block with its own printed invoice number. It is still **one payable**: live SSCS
    /// holds the 03/19/2026 ticket as a single $325.50 paidout of seven lines, so the returns are
    /// negative lines on the same invoice and the whole PDF produces one file.
    ///
    /// (It used to write one file per block, on the assumption that a separate printed number meant
    /// a separate payable. The store's own books say otherwise.)
    /// </summary>
    private Converted ConvertBlueBell(
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings)
    {
        var sections = BlueBellPdfParser.ParseAll(lines, warnings);
        decimal total = sections.Sum(s => s.Total);
        int lineCount = sections.Sum(s => s.Lines.Count);

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Blue Bell",
            sections.Select(s => s.Date).FirstOrDefault(d => d is not null),
            total, lineCount, 1, 1, "Blue Bell"));

        var invoice = BlueBellPdfParser.Merge(sections, keyed.InvoiceNumber, keyed.Date);
        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/BlueBell", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// A Chubiwinos invoice is one payable, so it produces one file — but two of its fields can need
    /// a person. The invoice number is always typed (the scan mangled it on two of eight samples and
    /// nothing can verify one), and a barcode is typed only when it failed its check digit and the
    /// vendor's catalogue has never recorded one for that product.
    /// </summary>
    private Converted ConvertChubiwinos(
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings)
    {
        var ticket = ChubiwinosPdfParser.Parse(lines, warnings);

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Chubiwinos", ticket.Date, ticket.Total, ticket.Lines.Count, 1, 1,
            "Chubiwinos"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date,
            line => AskBarcode(inputFileName, ticket, line, warnings));

        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/Chubiwinos", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// A Bimbo delivery ticket is one payable even though it prints two bands: SSCS booked a ticket's
    /// 26 deliveries and 9 returns as a single $335.07 paidout, so the returns are negative lines on
    /// the same invoice rather than an invoice of their own.
    ///
    /// The invoice number is typed. Bimbo prints one ("INVOICE# 84294890013239") but it has no check
    /// digit and the scanner mis-reads it silently — one sample came back as 84299890013154 for
    /// 84294890013154 — and the store's own paidouts use a different numbering anyway.
    /// </summary>
    private Converted ConvertBimbo(
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings)
    {
        var ticket = BimboPdfParser.Parse(lines, warnings);

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Bimbo", ticket.Date, ticket.Total, ticket.Lines.Count, 1, 1, "Bimbo"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date);
        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/Bimbo", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// A Red Bull PDF is one payable however many pages it has. Live SSCS booked a two-page ticket —
    /// a delivery page and a credit page with its own invoice number — as a single $342.50 paidout
    /// of ten lines, so every page's rows go into one invoice and the credits are negative lines on
    /// it. That is the shape every PDF vendor here uses, Blue Bell included.
    ///
    /// The invoice number is typed. The ticket prints one, but the store's own paidouts use a
    /// different numbering entirely (the verified example is filed as 112425112379D against a
    /// printed 2030211863), so there is nothing to read.
    /// </summary>
    private Converted ConvertRedBull(
        IReadOnlyList<PdfPage> pages, string inputFileName, List<ConversionWarning> warnings, byte[] pdfBytes)
    {
        // Two more readings of the same paper, both taken off the picture rather than out of the
        // text layer, and both best-effort: empty simply leaves the parser with the readings it
        // always had. The bars carry their own error correction and identify the product; the vision
        // reading covers the money and the counts, which is where the questions actually are.
        var ticket = RedBullPdfParser.Parse(
            pages, warnings,
            PdfBarcodeReader.TryReadAll(pdfBytes),
            ReadWithVision(pdfBytes, VisionVendor.RedBull));

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Red Bull", ticket.Date, ticket.Total, ticket.Lines.Count, 1, 1,
            "Red Bull"));

        // Answers are remembered so a second pass can re-use them: the operator is never asked the
        // same figure twice.
        var answered = new Dictionary<(int Position, LineField Field), string>();
        string Ask(RedBullLine line, LineField field)
        {
            if (answered.TryGetValue((line.Position, field), out string? already)) return already;
            string typed = AskLineField(inputFileName, ticket, line, field, warnings, pdfBytes);
            answered[(line.Position, field)] = typed;
            return typed;
        }

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date, Ask);
        invoice = SettleAgainstThePrintedTotal(ticket, invoice, keyed, Ask, answered, warnings);
        CheckRedBullUnits(ticket, invoice, warnings);

        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/RedBull", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// Settles a ticket that does not agree with its own printed total — by asking, and never by
    /// giving up on the delivery.
    ///
    /// This used to refuse, and refusing was wrong. Every sample that hit it was a ticket a person
    /// could have read in five seconds: the scanner had rendered one digit wrong **in both money
    /// columns at once**, so the row agreed with itself and nothing on the page could find it. One
    /// prints $54.70 and reads `$64.70` twice; another prints $58.71 and reads `$68.71` twice. Both
    /// are a 5 read as a 6, and both are perfectly legible in the picture.
    ///
    /// So the printed total is used for the one thing it is good for: it says *something* is wrong.
    /// The rows nobody has looked at are the only candidates, so those are shown — and once a person
    /// has read every amount off the paper, the lines are the truth. A printed total still
    /// disagreeing at that point is the damaged figure, which on this vendor is routine: one sample
    /// prints its own total as $315.60 where its five rows plainly come to $315.50.
    ///
    /// The file is always written. A wrong total nobody looked at is dangerous; a total a person has
    /// checked line by line, flagged in the list, is a delivery that gets paid.
    /// </summary>
    private Invoice SettleAgainstThePrintedTotal(
        RedBullTicket ticket, Invoice invoice, InvoiceDetails keyed,
        Func<RedBullLine, LineField, string> ask,
        Dictionary<(int Position, LineField Field), string> answered,
        List<ConversionWarning> warnings)
    {
        if (ticket.PrintedTotals.Count == 0) return invoice;

        decimal Gap(Invoice i) => ticket.PrintedTotals.Min(t => Math.Abs(t - i.LineTotal));
        if (Gap(invoice) < 0.005m) return invoice;

        // Anything the operator has not already read off the paper. A row whose amount they typed is
        // not a candidate: they have looked at it, and asking again is how a prompt stops being read.
        var unchecked_ = ticket.Lines
            .Where(l => !answered.ContainsKey((l.Position, LineField.Amount)))
            .Select(l => l.Position)
            .ToHashSet();

        if (unchecked_.Count > 0 && Gap(invoice) > RedBullTotalTolerance && _invoiceDetailsPrompt is not null)
            invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date, ask, unchecked_);

        decimal gap = Gap(invoice);
        if (gap < 0.005m) return invoice;

        decimal closest = ticket.PrintedTotals.OrderBy(t => Math.Abs(t - invoice.LineTotal)).First();
        warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
            gap <= RedBullTotalTolerance
                ? $"The lines come to {invoice.LineTotal:C2} but the ticket prints {closest:C2}. The "
                  + "lines were used - check the amounts against the paper before sending."
                : $"The lines come to {invoice.LineTotal:C2} and the ticket prints {closest:C2}, a "
                  + $"difference of {gap:C2}. Every amount was read off the paper, so the lines were "
                  + "used - but check this one against the ticket before sending."));
        return invoice;
    }

    /// <summary>How far the lines may sit from the printed total before the rows are re-checked.</summary>
    private const decimal RedBullTotalTolerance = 0.05m;

    /// <summary>
    /// The one check that can see a **missing line**, run last, on the settled invoice.
    ///
    /// A row the scan lost entirely takes its dollars with it, so the money still adds up and the
    /// operator is never asked about a row they cannot see. The ticket's own `Units Delivered` does
    /// not move when an amount is misread, so a disagreement here means a line is gone or a quantity
    /// is wrong — and it is the only place either of those shows up.
    ///
    /// Said out loud rather than refused, because the file is worth more converted and flagged than
    /// lost; but this is the note on this vendor worth stopping for.
    /// </summary>
    private static void CheckRedBullUnits(
        RedBullTicket ticket, Invoice invoice, List<ConversionWarning> warnings)
    {
        // The printed count is the delivery's, so it can only be compared where nothing went back.
        if (ticket.PrintedUnits is not int printed || ticket.Lines.Any(l => l.IsCredit)) return;

        int units = invoice.Lines.Sum(l => l.Units);
        if (units == printed) return;

        // A row the scan lost takes its dollars with it, so an invoice landing *exactly* on the
        // total the ticket prints for itself cannot be missing one. What is left is a misread
        // count - and on this vendor the printed count is misread as readily as anything else: one
        // sample prints 384 units and scans "364", another prints 276 and scans "270". Both add up
        // to the penny. Sending the operator to hunt for a row that is on the paper is how the one
        // warning worth stopping for on this vendor stops being read.
        bool nothingIsMissing = invoice.Lines.Count > 0
            && ticket.PrintedTotals.Any(t => Math.Abs(t - invoice.LineTotal) < 0.005m);

        warnings.Add(new ConversionWarning(0, PdfReasonCodes.QtyMismatch,
            nothingIsMissing
                ? $"The ticket says {printed} units were delivered and the {invoice.Lines.Count} lines "
                  + $"come to {units}. The money adds up exactly, so no line is missing - either the "
                  + "ticket's own count or one line's UNITS was misread. Worth a glance at the paper."
                : $"The ticket says {printed} units were delivered and the {invoice.Lines.Count} lines "
                  + $"come to {units}. A product line may be missing from the scan - count the rows on "
                  + "the paper before sending this one."));
    }

    /// <summary>
    /// Asks the operator for one figure on one product row that the scan destroyed, naming the row
    /// so they do not have to count. Cancelling skips the whole file — an invoice missing a line, or
    /// carrying a made-up one, would book the wrong money.
    /// </summary>
    private string AskLineField(
        string inputFileName, RedBullTicket ticket, RedBullLine line, LineField field,
        List<ConversionWarning> warnings, byte[] pdfBytes)
    {
        string what = field switch
        {
            LineField.Barcode => "barcode",
            LineField.Units => "unit count",
            _ => "amount",
        };

        if (_invoiceDetailsPrompt is null)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The {what} for \"{line.Description}\" could not be read from this invoice and could not "
                + "be worked out from the rest of the ticket, so it has to be typed in - but no prompt "
                + "is available.");

        string printed = field switch
        {
            LineField.Barcode => line.PrintedCode,
            LineField.Units => line.PrintedUnits,
            _ => line.PrintedAmount,
        };

        // The barcode is printed on the line *under* the priced row, so that is the line to mark —
        // and it is marked by finding the token the scan actually wrote, which is not the same string
        // as the digits shown back to the operator once the scan has put a mark through it.
        //
        // Only when that token could be the printed code, though. Red Bull prints **bars** as well as
        // digits, and on a poor scan the OCR transcribes the bars themselves as a token of ones and
        // quote marks on a line of their own — `1111'1"III511111` sits above `611269332827` on
        // REDBULL_2030959925. The parser takes that for the code, which is why it asks; the marker
        // then finds it and draws a confident box **round the bars**, leaving the digits the operator
        // has to type unmarked in the middle of the row. A UPC-A is twelve digits, so a token that
        // does not carry twelve is not the printed code, and neither it nor the line it was found on
        // may be pointed at. Marking the priced row instead says "this row" and claims nothing more.
        bool barcode = field == LineField.Barcode;
        bool codeIsPlausible = barcode && line.CodeToken.Count(char.IsDigit) == 12;
        int focusLine = codeIsPlausible && line.CodeLineNumber > 0 ? line.CodeLineNumber : line.LineNumber;
        string? focusText = barcode ? (codeIsPlausible ? line.CodeToken : null) : printed;

        var snip = line.LineNumber > 0
            ? PdfRowSnipper.TryCut(
                pdfBytes, line.LineNumber, Math.Max(line.LineNumber, line.LastLineNumber),
                focusText, focusLine, lastOfDuplicates: field == LineField.Amount)
            : PdfRowSnipper.TryCutWholePage(
                pdfBytes, line.Page, line.PageUpsideDown);

        bool wholePage = line.LineNumber <= 0 && snip is not null;

        string? typed = _invoiceDetailsPrompt.AskLineField(new LineFieldRequest(
            inputFileName, "Red Bull", field, line.Position, ticket.Lines.Count,
            line.ItemNumber, line.Description, printed, line.Units, line.Amount, snip,
            snip is null || wholePage || line.HeaderLineNumber <= 0
                ? null
                : PdfRowSnipper.TryCutHeaderLine(pdfBytes, line.HeaderLineNumber),
            ColumnLegend: ColumnLegend(PdfVendor.RedBull, line.IsCredit),
            ColumnName: field switch
            {
                LineField.Units => "UNITS",
                LineField.Amount => "TOTAL",   // Red Bull's last column; Dr Pepper calls it AMOUNT
                _ => "",
            },
            Hint: Hint(wholePage, field switch
            {
                LineField.Units => "Cans or bottles, not cases.",
                LineField.Barcode => "12 digits, above the bars.",
                _ => "",
            }),
            WholePage: wholePage));

        if (string.IsNullOrWhiteSpace(typed))
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Skipped - the {what} for line {line.Position} of {inputFileName} was not supplied. "
                + "The original file was not changed.");

        warnings.Add(new ConversionWarning(line.LineNumber, PdfReasonCodes.UnreadableLine,
            $"{(line.Description.Length > 0 ? line.Description : line.ItemNumber)} - the {what} was "
            + $"typed in (the scan read \"{printed}\")."));
        return typed;
    }

    /// <summary>
    /// Asks the operator to read one barcode off the paper, naming which product it is so they do not
    /// have to guess. Cancelling skips the whole file: a line with no barcode has nowhere safe to go,
    /// and an invoice missing a line would book the wrong money.
    /// </summary>
    private string AskBarcode(
        string inputFileName, ChubiwinosTicket ticket, ChubiwinosLine line, List<ConversionWarning> warnings)
    {
        if (_invoiceDetailsPrompt is null)
            throw new EdiConversionException(PdfReasonCodes.BarcodeRequired,
                $"The barcode for \"{line.Description}\" could not be read from this invoice and this vendor "
                + "has no code on record for it, so it has to be typed in - but no prompt is available.");

        string? typed = _invoiceDetailsPrompt.AskBarcode(new BarcodeRequest(
            inputFileName, line.Position, ticket.Lines.Count, line.ItemNumber, line.Description,
            line.Units, line.UnitCost, line.PrintedCode));

        string digits = Upc.DigitsOnly(typed);
        if (digits.Length == 0) throw new OperationCanceledException();

        // An 11-digit body is a whole barcode with its check digit left off; the digit is arithmetic
        // on the other eleven, so putting it back is exact rather than a guess.
        if (digits.Length == 11) digits = Upc.AppendCheckDigit(digits);

        if (!Upc.IsValidGtin(digits))
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"The barcode typed in for \"{line.Description}\" ({digits}) does not pass its own check digit, "
                + "so it may have a wrong digit in it. It was used as typed - please check this line in SSCS."));
        else
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UpcCheckDigit,
                $"The barcode for \"{line.Description}\" could not be read from the scan and was typed in as "
                + $"{digits}."));

        return digits;
    }

    /// <summary>
    /// Gets from the operator what the ticket could not supply. The invoice number is always typed:
    /// it is the one field nothing can verify — no check digit, and the scanner mis-reads it
    /// silently. The date is typed only when the scan destroyed it.
    /// </summary>
    private InvoiceDetails AskInvoiceDetails(InvoiceDetailsRequest request)
    {
        if (_invoiceDetailsPrompt is null)
            throw new EdiConversionException(PdfReasonCodes.NoInvoiceNumber,
                "This invoice needs its invoice number typed in, but no prompt is available.");

        var answer = _invoiceDetailsPrompt.AskInvoiceDetails(request);
        if (answer is null) throw new OperationCanceledException();

        string number = (answer.InvoiceNumber ?? "").Trim();
        if (number.Length == 0)
            throw new EdiConversionException(PdfReasonCodes.NoInvoiceNumber,
                "An invoice number is required.");

        // The typed date, and nothing else. There is no fallback to what the scan read, deliberately:
        // a fallback is what a blank box turns into, and the whole point of asking is that the
        // scan's reading is not trusted. A wrong year is the one thing these scanners produce that
        // looks entirely plausible - two of twenty-two samples booked themselves into 2016 and 2020,
        // both for 2026 - and nothing in the file can catch one.
        DateOnly date = answer.Date;
        if (date == default)
            throw new EdiConversionException(PdfReasonCodes.NoDate,
                "The date on this invoice has to be typed in. Copy it from the top of the ticket and "
                + "convert the file again.");

        if (request.NeedsTotal && answer.Total <= 0m)
            throw new EdiConversionException(PdfReasonCodes.NoTotal,
                $"The amount due could not be read from this {request.Vendor} invoice and none was entered.");

        return new InvoiceDetails(number, date, answer.Total);
    }

    private static byte[] ReadInputBytes(string inputPath)
    {
        // FileShare.ReadWrite tolerates files that other programs still hold open for writing
        // (e.g. a browser download); content validation rejects incomplete files afterwards.
        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length > 512 * 1024 * 1024)
            throw new IOException("The file is larger than 512 MB, which is far beyond any supported EDI invoice.");
        using var ms = new MemoryStream((int)fs.Length);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static string MoveToAvailableName(string tempPath, string destinationFolder, string desiredName, string inputPath)
    {
        string fullInput = Path.GetFullPath(inputPath);
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            string candidate = Path.Combine(destinationFolder, OutputNaming.CandidateName(desiredName, attempt));
            if (string.Equals(Path.GetFullPath(candidate), fullInput, StringComparison.OrdinalIgnoreCase))
                continue; // never write over the source file
            if (File.Exists(candidate))
                continue;
            try
            {
                File.Move(tempPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another conversion claimed this name between our check and the move; try the next.
            }
        }
        throw new IOException($"Could not find an available output name for {desiredName} in {destinationFolder}.");
    }

    private ConversionOutcome Fail(LogEntry log, Exception ex, string userMessage, string? reasonCode = null)
    {
        log.Status = "Failed";
        log.ReasonCode = reasonCode;
        log.UserFriendlyReason = userMessage;
        log.ExceptionDetail = ex.ToString();
        _logger.TryWrite(log);
        return new ConversionOutcome { Success = false, UserMessage = userMessage, ReasonCode = reasonCode };
    }

    private static string BuildConversionFailureMessage(string fileName, EdiConversionException ex)
    {
        return $"Could not convert {fileName}.\n\n{ex.UserMessage}\nThe original file was not changed.";
    }

    private static string ReadFailureMessage(string fileName, Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            $"Could not convert {fileName}.\n\nThe file could not be found. It may have been moved or deleted.",
        UnauthorizedAccessException =>
            $"Could not convert {fileName}.\n\nWindows denied access to the file. Check that you have permission to read it.",
        IOException io when IsSharingViolation(io) =>
            $"Could not convert {fileName}.\n\nThe file is open in another program or still downloading. Close it or wait for the download to finish, then try again.",
        PathTooLongException =>
            $"Could not convert {fileName}.\n\nThe file path is too long for Windows. Move the file to a shorter folder path and try again.",
        IOException io =>
            $"Could not convert {fileName}.\n\nThe file could not be read: {io.Message}",
        _ => $"Could not convert {fileName}.\n\nAn unexpected problem occurred while reading the file. The original file was not changed.",
    };

    private static string WriteFailureMessage(string fileName, string destination, Exception ex) => ex switch
    {
        DirectoryNotFoundException =>
            $"Could not convert {fileName}.\n\nThe output folder {destination} is no longer available. Please choose a new output folder.",
        UnauthorizedAccessException =>
            $"Could not convert {fileName}.\n\nWindows denied permission to write in {destination}. Choose a different output folder.",
        PathTooLongException =>
            $"Could not convert {fileName}.\n\nThe output path would be too long for Windows. Choose an output folder with a shorter path.",
        IOException io when IsDiskFull(io) =>
            $"Could not convert {fileName}.\n\nThere is not enough free disk space in {destination}.",
        IOException io =>
            $"Could not convert {fileName}.\n\nThe converted file could not be written: {io.Message}\nNo partial file was left behind.",
        _ => $"Could not convert {fileName}.\n\nAn unexpected problem occurred while saving. No partial file was left behind.",
    };

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    private static bool IsDiskFull(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 39 or 112; // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
    }
}
