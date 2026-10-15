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
public sealed class ConversionService : IFileConverter, IBatchFileConverter, IProgressReportingConverter
{
    private readonly ConversionLogger _logger;
    private readonly string _appVersion;
    private readonly IInvoiceDetailsPrompt? _invoiceDetailsPrompt;
    private readonly IInvoiceVisionReader? _visionReader;
    private bool? _bimboOcrFallbackForBatch;
    private bool? _drPepperOcrFallbackForBatch;
    private bool? _redBullOcrFallbackForBatch;
    private bool? _blueBellOcrFallbackForBatch;
    private bool? _chubiwinosOcrFallbackForBatch;

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

        // A picture-primary vendor spends its whole conversion inside network reads before any
        // window opens, so the reader is the only thing that knows anything is still happening.
        if (visionReader is IVisionProgressReporter reporter) reporter.Progress = ReportProgress;
    }

    /// <summary>
    /// Raised on the conversion thread while a long read is outstanding, so the queue can say what
    /// it is waiting on instead of "Converting..." for four minutes.
    /// </summary>
    public event Action<string>? Progress;

    private void ReportProgress(string message)
    {
        try { Progress?.Invoke(message); }
        catch
        {
            // A subscriber throwing is a UI problem; it must never fail the invoice being read.
        }
    }

    /// <summary>Clears each vendor's once-per-queue fallback decision.</summary>
    public void BeginBatch()
    {
        _bimboOcrFallbackForBatch = null;
        _drPepperOcrFallbackForBatch = null;
        _redBullOcrFallbackForBatch = null;
        _blueBellOcrFallbackForBatch = null;
        _chubiwinosOcrFallbackForBatch = null;
    }

    /// <summary>
    /// What the second reader cost on the file being converted, for the log. Reset per file; one
    /// file is converted at a time, on the queue's own thread.
    /// </summary>
    private string? _visionNote;

    /// <summary>Which back office the file being converted is for. Set per file, like the note above.</summary>
    private OutputTarget _target;

    /// <summary>
    /// The finished file for one invoice, in the format its tab writes. Reading the invoice is the
    /// same either way; only this last step differs. <paramref name="sscsName"/> is the name the
    /// vendor's SSCS file has always had. A Modisoft file is named by vendor and invoice number, and
    /// any note it raises about itself joins the invoice's own.
    /// </summary>
    private ConvertedDocument Document(Invoice invoice, string sscsName, List<ConversionWarning> warnings) =>
        _target == OutputTarget.Modisoft
            ? new ConvertedDocument(
                OutputNaming.BuildModisoftFileName(invoice), ModisoftEdiBuilder.Build(invoice, warnings))
            : new ConvertedDocument(sscsName, NaxmlBuilder.Build(invoice));

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
            var reading = _visionReader.TryRead(pdfBytes, vendor) ?? VisionReading.Empty;
            // Recorded even on an empty reading, and especially then: a read that spent ninety
            // seconds and came back with nothing is the case the log is for.
            if (reading.Note.Length > 0) _visionNote = $"{vendor}: {reading.Note}";
            return reading;
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
        _visionNote = null;
        _target = file.Target;
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
            log.VisionRead = _visionNote;
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

        log.DetectedFormat = _target == OutputTarget.Modisoft && file.Kind == ConversionKind.Pdf
            ? converted.Format + " to Modisoft EDI"
            : converted.Format;
        log.RecordsProcessed = converted.RecordCount;
        log.UpcERecordsExpanded = converted.UpcExpanded;
        log.OutputBytes = converted.Documents.Sum(d => d.Bytes.LongLength);
        // Logs retain only coordinates and reason codes. Warning text can contain descriptions,
        // UPCs, quantities, or money and therefore stays in the in-memory UI result only.
        if (converted.Warnings.Count > 0)
            log.Warnings = converted.Warnings
                .Select(w => $"line {w.LineNumber} [{w.ReasonCode}]").ToList();

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
            log.VisionRead = _visionNote;
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

            // Routine notes (for example normal vendor returns) stay visible in the details even
            // though they deliberately do not make the file amber.
            if (converted.Warnings.Count > 0)
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
        public bool HasWarnings => Warnings.Any(w => !w.Routine);
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
        // Each reader goes to one place. The any-invoice reader takes its invoice number and date off
        // the paper, which is harmless only because Modisoft never reads them; SSCS books by them.
        // And the five vendor readers no longer pass on cases, which only Modisoft needs.
        if ((vendor == PdfVendor.Any) != (_target == OutputTarget.Modisoft))
            throw new EdiConversionException(PdfReasonCodes.VendorNoParser,
                vendor == PdfVendor.Any
                    ? "Reading any vendor's invoice only makes Modisoft files. Use the PDF to Modisoft EDI tab."
                    : "Modisoft files are made on the PDF to Modisoft EDI tab, which reads any vendor's invoice.");

        // Any vendor takes photos as well as PDFs, so it is sorted out before the PDF check.
        if (vendor == PdfVendor.Any)
            return ConvertAnyInvoice(inputBytes, inputFileName, new List<ConversionWarning>());

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

        // Blue Bell's AI path owns every value and needs no OCR geometry. The text layer is read
        // only so the explicitly approved legacy parser remains available when AI is unavailable.
        if (vendor == PdfVendor.BlueBell)
            return ConvertBlueBell(
                TextOr([], () => PdfTextExtractor.ExtractLines(inputBytes), vendor),
                inputFileName, warnings, inputBytes);

        // Bimbo and Dr Pepper still run the text-layer pass, but their AI-primary paths receive only
        // highlight coordinates. No value from these records is allowed into an AI-owned invoice.
        if (vendor is PdfVendor.Bimbo or PdfVendor.DrPepper)
        {
            IReadOnlyList<PdfTextLine> positioned = TextOr(
                [], () => PdfTextExtractor.ExtractPositionedLines(inputBytes), vendor);
            return vendor == PdfVendor.Bimbo
                ? ConvertBimbo(positioned, inputFileName, warnings, inputBytes)
                : ConvertDrPepper(positioned, inputFileName, warnings, inputBytes);
        }

        IReadOnlyList<string> lines =
            TextOr([], () => PdfTextExtractor.ExtractLines(inputBytes), vendor);

        if (vendor == PdfVendor.Chubiwinos)
            return ConvertChubiwinos(lines, inputFileName, warnings, inputBytes);

        throw new EdiConversionException(PdfReasonCodes.VendorNoParser,
            "This PDF vendor is not supported.");
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
                                                && (vendor == PdfVendor.DrPepper
                                                    || vendor == PdfVendor.BlueBell
                                                    || vendor == PdfVendor.Chubiwinos
                                                    || _visionReader is not null
                                                    && vendor is PdfVendor.RedBull or PdfVendor.Bimbo))
        {
            return nothing;
        }
    }

    /// <summary>
    /// Dr Pepper: one picture request, then XML.
    ///
    /// It used to be three requests — a full read plus two independent barcode passes that had to
    /// agree — and it asked the operator about every code they did not agree on. That design is
    /// Bimbo's, and Bimbo needs it: Bimbo prints a UPC body with no check digit, so a code there
    /// cannot say for itself whether it was read correctly. Dr Pepper prints a whole UPC-A. The
    /// check digit catches a misread for free, without a network request, and on 2026-08-13 it
    /// caught three misreads out of three while the two extra passes caught nothing the check digit
    /// had not already found — and produced 29 false questions on one thirty-row ticket.
    ///
    /// Dropping them also stretches the free allowance three-fold, which decides how good the
    /// reading is: the day those passes were running, both Flash models were exhausted by early
    /// afternoon and every invoice after that was read by Flash-Lite.
    ///
    /// So nothing here interrogates the model any more. Invoice number and date are still absent
    /// from the AI contract and typed into empty boxes.
    /// </summary>
    private Converted ConvertDrPepper(
        IReadOnlyList<PdfTextLine> positionedLines, string inputFileName,
        List<ConversionWarning> warnings, byte[] pdfBytes)
    {
        DrPepperVisionTicket? ticket = null;
        if (_visionReader is IDrPepperVisionReader ai)
        {
            DrPepperVisionReading reading;
            try
            {
                reading = ai.ReadDrPepper(pdfBytes);
            }
            catch
            {
                reading = DrPepperVisionReading.Unavailable();
            }
            if (reading.Note.Length > 0) _visionNote = $"DrPepper AI: {reading.Note}";

            if (reading.Status == DrPepperVisionStatus.Success)
            {
                ticket = DrPepperVisionParser.Parse(reading, warnings);
            }
            else if (reading.Status == DrPepperVisionStatus.Unreadable)
            {
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    "AI reached this Dr Pepper invoice but could not read a complete, valid invoice "
                    + "after one fresh retry. Re-scan the complete invoice and try again. OCR values "
                    + "were not substituted.");
            }
        }

        if (ticket is null)
        {
            if (!UseDrPepperOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Dr Pepper invoice used the legacy OCR parser "
                + "after the operator approved that fallback for this batch."));

            var lines = positionedLines.Select(l => l.Text).ToList();
            DrPepperPdfParser.RequireAReadableInvoice(lines);
            decimal? legacyAmountDue = DrPepperPdfParser.FindAmountDue(lines);
            if (legacyAmountDue is null)
                throw new EdiConversionException(PdfReasonCodes.NoTotal,
                    "The legacy parser could not read AMOUNT DUE for validation. Re-scan the "
                    + "invoice; AMOUNT DUE is never typed or used to derive missing line values.");

            var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
                inputFileName, "Dr Pepper", legacyAmountDue.Value, 0, 1, 1, "Dr Pepper"));
            Invoice legacy = DrPepperPdfParser.Parse(
                lines, warnings, f => AskDrPepperField(inputFileName, f, pdfBytes),
                keyed.Date, keyed.InvoiceNumber, legacyAmountDue);
            var legacyDoc = Document(legacy, OutputNaming.BuildOutputFileName(inputFileName), warnings);
            return new Converted([legacyDoc], "PDF/DrPepper legacy OCR", legacy.Lines.Count,
                warnings, 0, null, Headline(legacy));
        }

        // Both header boxes begin empty. The AI response shape has no invoice number or date, and
        // AMOUNT DUE is validation context only, never an operator-entered field.
        var details = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Dr Pepper", ticket.Total, ticket.Lines.Count, 1, 1, "Dr Pepper"));
        Invoice invoice = ticket.ToInvoice(details.InvoiceNumber, details.Date);
        var doc = Document(invoice, OutputNaming.BuildOutputFileName(inputFileName), warnings);
        return new Converted([doc], "PDF/DrPepper AI", invoice.Lines.Count,
            warnings, 0, null, Headline(invoice));
    }

    /// <summary>
    /// Any vendor's invoice, for Modisoft: one picture request, then the file.
    ///
    /// There is no OCR fallback, because there is no vendor parser to fall back to. So the three
    /// ways it can stop each say what to do: AI is switched off (switch it on), Google could not be
    /// reached or was busy for its whole patience (try again later), or Google answered and found
    /// no product line (a clearer picture).
    ///
    /// **Nothing is asked** (2026-10-15, the user: the invoice number and date box is "useless for
    /// modisoft"). Modisoft takes the purchase's number and date from what is typed in Modisoft and
    /// never from the file's header (the 2021 GHRA import: file 07/11/21 and 0000687105, purchase
    /// 7/12/2021 and IN#687105). So what the AI reads off the paper only fills the header and names
    /// the file, where a misread digit costs nothing. Every SSCS vendor still types both.
    /// </summary>
    private Converted ConvertAnyInvoice(byte[] inputBytes, string inputFileName, List<ConversionWarning> warnings)
    {
        if (!PdfTextExtractor.LooksLikePdf(inputBytes) && InvoicePictures.PictureType(inputBytes) is null)
            throw new EdiConversionException(PdfReasonCodes.NotAPdf,
                "This file is not a PDF or a photo. Drop the invoice as a PDF, or a JPG or PNG photo of it.");

        AnyInvoiceVisionReading reading;
        if (_visionReader is not IAnyInvoiceVisionReader ai)
            reading = AnyInvoiceVisionReading.Off();
        else
        {
            try
            {
                reading = ai.ReadAnyInvoice(inputBytes);
            }
            catch
            {
                reading = AnyInvoiceVisionReading.Unavailable();
            }
        }
        if (reading.Note.Length > 0) _visionNote = $"Any invoice AI: {reading.Note}";

        switch (reading.Status)
        {
            case AnyInvoiceVisionStatus.Off:
                throw new EdiConversionException(PdfReasonCodes.VendorNoParser,
                    "This tab reads invoices with Google's AI, and AI reading is switched off. Open the "
                    + "Settings tab, press AI Reading Settings and switch it on.");
            case AnyInvoiceVisionStatus.Unavailable:
                throw new EdiConversionException(PdfReasonCodes.VendorNoParser,
                    "Google's AI could not read this invoice right now. It may be busy, or today's free "
                    + "allowance may be used up. Try this file again later.");
            case AnyInvoiceVisionStatus.Unreadable:
                throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                    "Google's AI looked at this invoice but could not read any product lines on it. Scan "
                    + "it again or take a clearer photo of the whole invoice, and try again.");
        }

        AnyInvoiceTicket ticket = AnyInvoiceParser.Parse(reading, warnings);
        Invoice invoice = ticket.ToInvoice(DateOnly.FromDateTime(DateTime.Today));
        var doc = Document(invoice, OutputNaming.BuildOutputFileName(inputFileName), warnings);
        return new Converted([doc], "Any invoice AI", invoice.Lines.Count,
            warnings, 0, null, Headline(invoice));
    }

    private bool UseDrPepperOcrFallback(string inputFileName)
    {
        if (_drPepperOcrFallbackForBatch is bool decided) return decided;
        _drPepperOcrFallbackForBatch =
            _invoiceDetailsPrompt?.ConfirmDrPepperOcrFallback(inputFileName) == true;
        return _drPepperOcrFallbackForBatch.Value;
    }

    /// <summary>
    /// Asks for one figure on one Dr Pepper row that the scan destroyed and the invoice's own
    /// subtotals could not work out, naming the row by its description and barcode so the operator
    /// does not have to count down the page. Cancelling skips the file: a made-up quantity books
    /// stock that never arrived, and nothing downstream would catch it.
    /// </summary>
    private string AskDrPepperField(
        string inputFileName, DrPepperUnknownField field, byte[] pdfBytes)
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

        // Dr Pepper prints one product over two lines: description + UPC, then the priced figures.
        // Gemini's adapter supplies both exact line numbers. The approved legacy OCR parser reports
        // the priced line, so its product line is the one immediately above. OCR is allowed to
        // locate these rectangles, never to supply or arbitrate an AI value.
        bool barcode = field.Field == LineField.Barcode;
        bool aiGeometry = field.ProductLineNo >= 0 || field.AmountLineNo >= 0;
        int productLine = aiGeometry ? field.ProductLineNo : field.LineNo - 1;
        int amountLine = aiGeometry ? field.AmountLineNo : field.LineNo;
        int focusLine = barcode ? productLine : amountLine;
        int firstLine = Math.Min(productLine, amountLine);
        int lastLine = Math.Max(productLine, amountLine);

        // Fixed-form fallbacks measured from the real SALES and CREDITS forms. They are used only
        // if the exact printed token cannot be placed. The vertical coordinate still comes from the
        // exact row, so this can box a field whose hidden OCR token was dropped without guessing a
        // row. These are form columns, not a product catalogue.
        PdfColumnBand exactColumn = field.Field switch
        {
            // Only the twelve-digit UPC half of the UPC/SKU cell. The slash and eight-digit SKU
            // are deliberately outside the box because they are not what the operator is asked to
            // type.
            LineField.Barcode => new PdfColumnBand(0.42, 0.69),
            LineField.Units => new PdfColumnBand(0.34, 0.43),
            LineField.Amount => new PdfColumnBand(0.82, 0.99),
            _ => throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Dr Pepper cannot ask for the unsupported {field.Field} field."),
        };

        PdfRowSnip? snip = firstLine > 0 && lastLine > 0
            ? PdfRowSnipper.TryCut(
                pdfBytes, firstLine, lastLine,
                field.PrintedValue, focusLine, barcode,
                lastOfDuplicates: field.Field == LineField.Amount,
                fallbackColumn: exactColumn)
            : null;
        PdfRowSnip? header = focusLine > 0
            ? PdfRowSnipper.TryCutDrPepperHeader(pdfBytes, focusLine, field.IsCredit)
            : null;

        // The exact boxed row is what a Dr Pepper question is meant to be, and it is still the only
        // thing shown when it can be cut. What changed on 2026-08-13 is what happens when it cannot.
        //
        // The store's scanner is producing pages with no text layer at all, so there are no line
        // coordinates, so nothing can be cut or boxed. Refusing there threw away a whole delivery
        // over one barcode the model duplicated a zero in — a code any person can read off the page
        // in two seconds. So the page itself is shown, with the row named above it, exactly as Red
        // Bull already does. It is worse than a boxed row and it looks it; it is far better than
        // sending the ticket back.
        //
        // The old objection stands and is why `ColumnLegend` is still empty here: a *drawn* column
        // header cannot line up with a scan, and Dr Pepper's CREDITS section has no WHSLE column, so
        // a legend can walk the operator onto the neighbouring figure. A photograph of the page has
        // the real columns on it in the right places.
        bool wholePage = snip is null || snip.FocusWidth < 1 || snip.FocusHeight < 1 || header is null;
        if (wholePage)
        {
            snip = PdfRowSnipper.TryCutWholePage(pdfBytes, field.Page, field.PageUpsideDown);
            header = null;
        }

        if (snip is null)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The {what} for \"{field.Description}\" has to be typed in, and this invoice could "
                + "not be shown as a picture at all. Re-scan the complete invoice and try again.");

        string? typed = _invoiceDetailsPrompt.AskLineField(new LineFieldRequest(
            inputFileName, "Dr Pepper", field.Field, field.Position, field.LineCount,
            field.Gtin, field.Description, field.PrintedValue, null, field.Amount, snip,
            header,
            ColumnLegend: "",
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

        // Skip this file is the operator's decision, exactly like dismissing the invoice-number
        // prompt, and it is reported as Canceled for the same reason. It used to come back as a
        // red Failed with a scan-damage reason code on it, which is how a queue ends up showing
        // somebody their own click as a fault in the invoice.
        if (string.IsNullOrWhiteSpace(typed)) throw new OperationCanceledException();

        return typed;
    }

    /// <summary>
    /// The vendor's column names, in the order they are printed, for the strip above the row.
    ///
    /// A backstop for vendors that permit one: <see cref="PdfRowSnipper"/> cuts the real header off
    /// the same page at the same width, which is far better because it lines up — UNITS sits over
    /// the units. Dr Pepper deliberately returns an empty string here; its questions require the
    /// photographed SALES/CREDITS header and fail closed when that cannot be located.
    ///
    /// Worth having anyway, because the strip is what makes a cut-out row readable. A snip is a
    /// couple of printed lines out of the middle of a ticket, and by item 16 the column names are a
    /// foot further up the page — so a row without them is a line of bare figures and no way to tell
    /// the NET from the AMOUNT.
    ///
    /// <paramref name="credit"/> remains part of the shared adapter signature. Vendors that still
    /// use this textual backstop print the same column set in both sections.
    /// </summary>
    private static string ColumnLegend(PdfVendor vendor, bool credit) => vendor switch
    {
        PdfVendor.RedBull => "ID   QTY   UNITS   DESCRIPTION   PRICE   DEP   DISC   SUGAR   TOTAL",
        // Dr Pepper never receives a synthetic legend. Its prompt is refused unless the real
        // photographed SALES or CREDITS header can be cut from the invoice.
        PdfVendor.DrPepper => "",
        // One column set for both bands: Bimbo's RETURNS block prints the same headings as its
        // DELIVERIES block and only puts a minus in front of the figures, so unlike Dr Pepper there
        // is no second legend to pick between. Two lines because that is how the ticket prints it,
        // with ITEM No and the two money groups on the row above their own columns.
        PdfVendor.Bimbo =>
            "            ITEM                        SUGG   RETAIL    WHOLESALE\n"
            + "UPC   No   DESCRIPTION   QTY   RETAIL   AMOUNT   PRICE   AMOUNT",
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
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings,
        byte[] pdfBytes)
    {
        BlueBellVisionTicket? ticket = null;
        if (_visionReader is IBlueBellVisionReader blueBellVision)
        {
            BlueBellVisionReading reading;
            try { reading = blueBellVision.ReadBlueBell(pdfBytes); }
            catch { reading = BlueBellVisionReading.Unavailable(); }
            if (reading.Note.Length > 0) _visionNote = $"Blue Bell primary: {reading.Note}";

            if (reading.Status == BlueBellVisionStatus.Success)
            {
                ticket = BlueBellVisionParser.Parse(reading, warnings);
            }
            else if (reading.Status == BlueBellVisionStatus.Unreadable)
            {
                throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                    "AI reached this Blue Bell ticket but could not read a postable product row "
                    + "after one fresh retry. Re-scan the complete ticket and try again. OCR "
                    + "values were not substituted.");
            }
        }

        if (ticket is null)
        {
            if (!UseBlueBellOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Blue Bell ticket used the legacy OCR parser "
                + "after the operator approved that fallback for this batch."));

            var sections = BlueBellPdfParser.ParseAll(lines, warnings);
            decimal legacyTotal = sections.Sum(section => section.Total);
            int legacyLineCount = sections.Sum(section => section.Lines.Count);

            var legacyKeyed = AskInvoiceDetails(new InvoiceDetailsRequest(
                inputFileName, "Blue Bell", legacyTotal, legacyLineCount, 1, 1, "Blue Bell"));

            var legacyInvoice = BlueBellPdfParser.Merge(
                sections, legacyKeyed.InvoiceNumber, legacyKeyed.Date);
            var legacyDoc = Document(legacyInvoice,
                OutputNaming.BuildInvoiceOutputFileName(legacyInvoice.InvoiceNumber), warnings);
            return new Converted([legacyDoc], "PDF/BlueBell legacy OCR", legacyInvoice.Lines.Count,
                warnings, 0, null, Headline(legacyInvoice));
        }

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Blue Bell", ticket.Total, ticket.Lines.Count, 1, 1, "Blue Bell"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date);
        var doc = Document(
            invoice, OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), warnings);
        return new Converted([doc], "PDF/BlueBell AI", invoice.Lines.Count, warnings, 0, null,
            Headline(invoice));
    }

    private bool UseBlueBellOcrFallback(string inputFileName)
    {
        if (_blueBellOcrFallbackForBatch is bool decided) return decided;
        _blueBellOcrFallbackForBatch =
            _invoiceDetailsPrompt?.ConfirmBlueBellOcrFallback(inputFileName) == true;
        return _blueBellOcrFallbackForBatch.Value;
    }

    /// <summary>
    /// Chubiwinos: one complete-ticket picture read, then one payable. The AI path asks only for the
    /// invoice number and date; barcode failures ship exactly as printed and are warning-only. The
    /// legacy OCR parser and catalogue remain isolated behind explicit once-per-batch approval.
    /// </summary>
    private Converted ConvertChubiwinos(
        IReadOnlyList<string> lines, string inputFileName, List<ConversionWarning> warnings,
        byte[] pdfBytes)
    {
        ChubiwinosVisionTicket? ticket = null;
        if (_visionReader is IChubiwinosVisionReader chubiwinosVision)
        {
            ChubiwinosVisionReading reading;
            try { reading = chubiwinosVision.ReadChubiwinos(pdfBytes); }
            catch { reading = ChubiwinosVisionReading.Unavailable(); }
            if (reading.Note.Length > 0) _visionNote = $"Chubiwinos primary: {reading.Note}";

            if (reading.Status == ChubiwinosVisionStatus.Success)
            {
                ticket = ChubiwinosVisionParser.Parse(reading, warnings);
            }
            else if (reading.Status == ChubiwinosVisionStatus.Unreadable)
            {
                throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                    "AI reached this Chubiwinos invoice but could not read a postable product row "
                    + "after one fresh retry. Re-scan the complete invoice and try again. OCR "
                    + "values were not substituted.");
            }
        }

        if (ticket is null)
        {
            if (!UseChubiwinosOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Chubiwinos invoice used the legacy OCR "
                + "parser after the operator approved that fallback for this batch."));

            ChubiwinosTicket legacyTicket = ChubiwinosPdfParser.Parse(lines, warnings);
            var legacyKeyed = AskInvoiceDetails(new InvoiceDetailsRequest(
                inputFileName, "Chubiwinos", legacyTicket.Total, legacyTicket.Lines.Count, 1, 1,
                "Chubiwinos"));
            Invoice legacyInvoice = legacyTicket.ToInvoice(
                legacyKeyed.InvoiceNumber, legacyKeyed.Date,
                line => AskBarcode(inputFileName, legacyTicket, line, warnings));
            var legacyDoc = Document(legacyInvoice,
                OutputNaming.BuildInvoiceOutputFileName(legacyInvoice.InvoiceNumber), warnings);
            return new Converted([legacyDoc], "PDF/Chubiwinos legacy OCR",
                legacyInvoice.Lines.Count, warnings, 0, null, Headline(legacyInvoice));
        }

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Chubiwinos", ticket.Total, ticket.Lines.Count, 1, 1,
            "Chubiwinos"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date);

        var doc = Document(
            invoice, OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), warnings);
        return new Converted([doc], "PDF/Chubiwinos AI", invoice.Lines.Count,
            warnings, 0, null, Headline(invoice));
    }

    private bool UseChubiwinosOcrFallback(string inputFileName)
    {
        if (_chubiwinosOcrFallbackForBatch is bool decided) return decided;
        _chubiwinosOcrFallbackForBatch =
            _invoiceDetailsPrompt?.ConfirmChubiwinosOcrFallback(inputFileName) == true;
        return _chubiwinosOcrFallbackForBatch.Value;
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
        IReadOnlyList<PdfTextLine> positionedLines, string inputFileName, List<ConversionWarning> warnings,
        byte[] pdfBytes)
    {
        BimboTicket ticket;
        if (_visionReader is IBimboVisionReader bimboVision)
        {
            BimboVisionReading reading;
            try { reading = bimboVision.ReadBimbo(pdfBytes); }
            catch { reading = BimboVisionReading.Unavailable(); }
            if (reading.Note.Length > 0) _visionNote = $"Bimbo primary: {reading.Note}";

            if (reading.Status == BimboVisionStatus.Success)
            {
                ticket = BimboVisionParser.Parse(reading, warnings);
            }
            else if (reading.Status == BimboVisionStatus.Unavailable)
            {
                if (!UseBimboOcrFallback(inputFileName)) throw new OperationCanceledException();
                warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                    "AI reading was unavailable, so this Bimbo ticket used the legacy PDF text "
                    + "reader after the operator approved that fallback."));
                ticket = BimboPdfParser.Parse(
                    positionedLines.Select(l => l.Text).ToList(), warnings,
                    field => AskBimboField(inputFileName, field, pdfBytes));
            }
            else
            {
                throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                    "AI reached this Bimbo ticket but could not read a postable product row and "
                    + "TICKET TOTALS after one fresh retry. Re-scan the complete ticket and try "
                    + "again. OCR values were not substituted.");
            }
        }
        else if (_visionReader is not null)
        {
            // Compatibility for existing injected readers. The desktop reader implements the
            // Bimbo-specific contract above; old test/host fakes still exercise the legacy path.
            ticket = BimboPdfParser.Parse(
                positionedLines.Select(l => l.Text).ToList(), warnings,
                field => AskBimboField(inputFileName, field, pdfBytes),
                ReadWithVision(pdfBytes, VisionVendor.Bimbo));
        }
        else
        {
            if (!UseBimboOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Bimbo ticket used the legacy PDF text reader "
                + "after the operator approved that fallback."));
            ticket = BimboPdfParser.Parse(
                positionedLines.Select(l => l.Text).ToList(), warnings,
                field => AskBimboField(inputFileName, field, pdfBytes));
        }

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Bimbo", ticket.Total, ticket.Lines.Count, 1, 1, "Bimbo"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date);
        var doc = Document(
            invoice, OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), warnings);
        return new Converted([doc], "PDF/Bimbo", invoice.Lines.Count, warnings, 0, null, Headline(invoice));
    }

    private bool UseBimboOcrFallback(string inputFileName)
    {
        if (_bimboOcrFallbackForBatch is bool decided) return decided;
        _bimboOcrFallbackForBatch = _invoiceDetailsPrompt?.ConfirmBimboOcrFallback(inputFileName) == true;
        return _bimboOcrFallbackForBatch.Value;
    }

    /// <summary>
    /// Asks for one figure on one Bimbo row — its quantity, or on a row that came off the picture its
    /// barcode — by showing the printed row with that figure boxed.
    ///
    /// Bimbo prints **one line per product**, which is the whole difference from the other two: Dr
    /// Pepper carries the barcode on the product line above the priced row and Red Bull on the line
    /// below, so both cut a two-line band and have to say which of the two to mark. Here the row is
    /// the line, and the figure being asked about is on it.
    ///
    /// Cancelling skips the file. A made-up quantity books stock that never arrived, and on this
    /// vendor nothing downstream would catch it — the unit cost absorbs a wrong count and the invoice
    /// still balances to the penny.
    /// </summary>
    private string AskBimboField(string inputFileName, BimboUnknownField field, byte[] pdfBytes)
    {
        bool barcode = field.Field == LineField.Barcode;
        string what = field.Field switch
        {
            LineField.Barcode => "barcode",
            LineField.Units => "quantity",
            LineField.UnitPrice => "unit price",
            _ => "amount",
        };

        if (_invoiceDetailsPrompt is null)
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"The {what} for \"{field.Description}\" could not be read from this ticket and could "
                + "not be worked out from the rest of it, so it has to be typed in - but no prompt is "
                + "available.");

        // The QTY column sits in the middle of the row with counts either side of it, so marking "the
        // last 4 on the line" would as often land on a description or on the retail column. Where the
        // scan actually read the column the figure itself is marked.
        string? focusText = field.PrintedValue.Length > 0 ? field.PrintedValue : null;

        // Where it did not, the column is marked instead — the case this vendor produces constantly,
        // because QTY is the column its 200 dpi scans delete outright (ticket 84294890010234 lost it
        // on all forty rows). Without this the question arrives with the whole line washed, which is
        // no help at all on a row of eight figures with two of them headed AMOUNT. The column is
        // found from the printed heading and the figure is legible in the picture even though the
        // text layer never got it. Not for a barcode: that column is the first on the row with
        // nothing to its left, and a code the scan read is already marked exactly.
        string? column = field.Field == LineField.Units && focusText is null ? "RETAIL" : null;

        // Bimbo is a fixed form. These are the gaps between its printed columns, measured as page
        // fractions so they survive scanner resolution changes. They are used only after both an
        // exact token and the positioned heading fail. That is common precisely when a question is
        // needed: the photograph still shows the figure but the hidden OCR text damaged it.
        //
        // Keeping this in the Bimbo adapter is deliberate. PdfRowSnipper knows how to draw a band;
        // only this vendor knows that UPC is the first cell, QTY the fourth, PRICE the seventh and
        // wholesale AMOUNT the eighth. This is form geometry, not a product catalogue.
        PdfColumnBand fallbackColumn = field.Field switch
        {
            LineField.Barcode => new(0.025, 0.170),
            LineField.Units => new(0.535, 0.600),
            LineField.UnitPrice => new(0.775, 0.865),
            _ => new(0.885, 0.985),
        };

        var snip = field.LineNo > 0
            ? PdfRowSnipper.TryCut(
                pdfBytes, field.LineNo, field.LineNo, focusText, field.LineNo,
                lastOfDuplicates: field.Field == LineField.Amount, columnLeftOf: column,
                fallbackColumn: fallbackColumn)
            : PdfRowSnipper.TryCutWholePage(pdfBytes, field.Page, field.PageUpsideDown);

        bool wholePage = field.LineNo <= 0 && snip is not null;

        string? typed = _invoiceDetailsPrompt.AskLineField(new LineFieldRequest(
            inputFileName, "Bimbo", field.Field, field.Position, field.LineCount,
            field.ItemNumber, field.Description, field.PrintedValue, null, field.Amount, snip,
            snip is null || wholePage ? null : PdfRowSnipper.TryCutHeader(pdfBytes, field.LineNo),
            ColumnLegend: ColumnLegend(PdfVendor.Bimbo, credit: false),
            ColumnName: field.Field switch
            {
                LineField.Units => "QTY",
                LineField.UnitPrice => "PRICE",
                LineField.Amount => "AMOUNT",
                _ => "",
            },
            Hint: Hint(wholePage, barcode
                // Bimbo prints the body of the barcode only - no leading zero, no check digit - so
                // ten is the whole of what is on the paper and a longer answer is somebody reading
                // the box rather than the ticket. Both spellings are accepted; the hint asks for the
                // one that is actually printed in front of them.
                ? "The 10 digits under UPC. The dash does not matter."
                // No cases anywhere on this vendor, so there is nothing to convert and nothing to
                // warn about. The number under QTY is the number to type.
                : ""),
            WholePage: wholePage));

        if (string.IsNullOrWhiteSpace(typed))
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Skipped - the {what} for \"{field.Description}\" on {inputFileName} was not "
                + "supplied. The original file was not changed.");

        return typed;
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
        if (_visionReader is IRedBullVisionReader redBullVision)
        {
            RedBullVisionReading reading;
            try { reading = redBullVision.ReadRedBull(pdfBytes); }
            catch { reading = RedBullVisionReading.Unavailable(); }
            if (reading.Note.Length > 0) _visionNote = $"Red Bull primary: {reading.Note}";

            if (reading.Status == RedBullVisionStatus.Success)
            {
                RedBullVisionTicket ticket = RedBullVisionParser.Parse(reading, warnings);
                var details = AskInvoiceDetails(new InvoiceDetailsRequest(
                    inputFileName, "Red Bull", ticket.Total, ticket.Lines.Count, 1, 1,
                    "Red Bull"));
                Invoice invoice = ticket.ToInvoice(details.InvoiceNumber, details.Date);
                var primaryDoc = Document(invoice,
                    OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), warnings);
                return new Converted([primaryDoc], "PDF/RedBull AI", invoice.Lines.Count,
                    warnings, 0, null, Headline(invoice));
            }

            if (reading.Status == RedBullVisionStatus.Unreadable)
                throw new EdiConversionException(PdfReasonCodes.NoLineItems,
                    "AI reached this Red Bull ticket but could not read a postable product row "
                    + "after one fresh retry. Re-scan the complete ticket and try again. OCR "
                    + "values were not substituted.");

            if (!UseRedBullOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Red Bull ticket used the legacy OCR parser "
                + "after the operator approved that fallback for this batch."));
        }
        else if (_visionReader is null)
        {
            if (!UseRedBullOcrFallback(inputFileName)) throw new OperationCanceledException();
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.UnreadableLine,
                "AI reading was unavailable, so this Red Bull ticket used the legacy OCR parser "
                + "after the operator approved that fallback for this batch."));
        }

        // Explicitly approved legacy path. Its OCR parser, barcode decoder, generic picture reader,
        // settlement ladder and row questions remain unchanged and isolated behind this branch.
        var legacyTicket = RedBullPdfParser.Parse(
            pages, warnings,
            PdfBarcodeReader.TryReadAll(pdfBytes),
            ReadWithVision(pdfBytes, VisionVendor.RedBull));

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Red Bull", legacyTicket.Total, legacyTicket.Lines.Count, 1, 1,
            "Red Bull"));

        // Answers are remembered so a second pass can re-use them: the operator is never asked the
        // same figure twice.
        var answered = new Dictionary<(int Position, LineField Field), string>();
        string Ask(RedBullLine line, LineField field)
        {
            if (answered.TryGetValue((line.Position, field), out string? already)) return already;
            string typed = AskLineField(inputFileName, legacyTicket, line, field, warnings, pdfBytes);
            answered[(line.Position, field)] = typed;
            return typed;
        }

        var legacyInvoice = legacyTicket.ToInvoice(keyed.InvoiceNumber, keyed.Date, Ask);
        legacyInvoice = SettleAgainstThePrintedTotal(
            legacyTicket, legacyInvoice, keyed, Ask, answered, warnings);
        CheckRedBullUnits(legacyTicket, legacyInvoice, warnings);

        var doc = Document(legacyInvoice,
            OutputNaming.BuildInvoiceOutputFileName(legacyInvoice.InvoiceNumber), warnings);
        return new Converted([doc], "PDF/RedBull legacy OCR", legacyInvoice.Lines.Count,
            warnings, 0, null, Headline(legacyInvoice));
    }

    private bool UseRedBullOcrFallback(string inputFileName)
    {
        if (_redBullOcrFallbackForBatch is bool decided) return decided;
        _redBullOcrFallbackForBatch =
            _invoiceDetailsPrompt?.ConfirmRedBullOcrFallback(inputFileName) == true;
        return _redBullOcrFallbackForBatch.Value;
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

        // Skip this file is a decision, not a defect - see the matching note on the Dr Pepper ask.
        if (string.IsNullOrWhiteSpace(typed)) throw new OperationCanceledException();

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
    /// silently. The date follows the same rule and is always typed into an empty box.
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
        // Exception/user text can embed an invoice description, UPC, amount, or a Gemini value.
        // The UI still receives the useful message; the persistent log stores metadata only.
        log.UserFriendlyReason = reasonCode is null ? "Conversion failed." : $"Conversion failed: {reasonCode}.";
        log.ExceptionDetail = ex.GetType().FullName;
        log.VisionRead = _visionNote;
        _logger.TryWrite(log);
        return new ConversionOutcome { Success = false, UserMessage = userMessage, ReasonCode = reasonCode };
    }

    /// <summary>
    /// Why the file was refused, first. The queue's Details column shows one line, so leading with
    /// "Could not convert X" spent it saying what the red word already said and hid the reason
    /// below the fold — a real refusal, a checksum that did not settle and a page that could not be
    /// drawn all read identically in the list.
    /// </summary>
    private static string BuildConversionFailureMessage(string fileName, EdiConversionException ex)
    {
        return $"{ex.UserMessage}\n\nCould not convert {fileName}. The original file was not changed.";
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
