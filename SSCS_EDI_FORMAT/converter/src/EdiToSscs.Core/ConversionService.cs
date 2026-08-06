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

    public ConversionService(
        ConversionLogger? logger = null,
        string? appVersion = null,
        IInvoiceDetailsPrompt? invoiceDetailsPrompt = null)
    {
        _logger = logger ?? new ConversionLogger();
        _appVersion = appVersion ?? typeof(ConversionService).Assembly.GetName().Version?.ToString() ?? "unknown";
        _invoiceDetailsPrompt = invoiceDetailsPrompt;
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
            if (converted.HasWarnings)
                message += $"\n{string.Join("\n", converted.Warnings.Select(w => w.Message))}";

            return new ConversionOutcome
            {
                Success = true,
                HasWarnings = converted.HasWarnings,
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
        IReadOnlyList<ConversionWarning> Warnings, int UpcExpanded, ConversionResult? EdiResult)
    {
        public bool HasWarnings => Warnings.Count > 0;
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
            return ConvertRedBull(PdfTextExtractor.ExtractPages(inputBytes), inputFileName, warnings);

        IReadOnlyList<string> lines = PdfTextExtractor.ExtractLines(inputBytes);

        if (vendor == PdfVendor.BlueBell)
            return ConvertBlueBell(lines, inputFileName, warnings);

        if (vendor == PdfVendor.Chubiwinos)
            return ConvertChubiwinos(lines, inputFileName, warnings);

        if (vendor == PdfVendor.Bimbo)
            return ConvertBimbo(lines, inputFileName, warnings);

        Invoice invoice = DrPepperPdfParser.Parse(lines, warnings);
        var doc = new ConvertedDocument(OutputNaming.BuildOutputFileName(inputFileName), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/DrPepper", invoice.Lines.Count, warnings, 0, null);
    }

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
            total, lineCount, 1, 1,
            sections.Select(s => s.SuggestedDate).FirstOrDefault(d => d is not null), "Blue Bell"));

        var invoice = BlueBellPdfParser.Merge(sections, keyed.InvoiceNumber, keyed.Date);
        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/BlueBell", invoice.Lines.Count, warnings, 0, null);
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
            null, "Chubiwinos"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date,
            line => AskBarcode(inputFileName, ticket, line, warnings));

        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/Chubiwinos", invoice.Lines.Count, warnings, 0, null);
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
            inputFileName, "Bimbo", ticket.Date, ticket.Total, ticket.Lines.Count, 1, 1,
            ticket.SuggestedDate, "Bimbo"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date);
        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/Bimbo", invoice.Lines.Count, warnings, 0, null);
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
        IReadOnlyList<PdfPage> pages, string inputFileName, List<ConversionWarning> warnings)
    {
        var ticket = RedBullPdfParser.Parse(pages, warnings);

        var keyed = AskInvoiceDetails(new InvoiceDetailsRequest(
            inputFileName, "Red Bull", ticket.Date, ticket.Total, ticket.Lines.Count, 1, 1,
            ticket.SuggestedDate, "Red Bull"));

        var invoice = ticket.ToInvoice(keyed.InvoiceNumber, keyed.Date,
            (line, field) => AskLineField(inputFileName, ticket, line, field, warnings));

        CheckRedBullTotal(ticket, invoice, inputFileName, warnings);

        var doc = new ConvertedDocument(
            OutputNaming.BuildInvoiceOutputFileName(invoice.InvoiceNumber), NaxmlBuilder.Build(invoice));
        return new Converted([doc], "PDF/RedBull", invoice.Lines.Count, warnings, 0, null);
    }

    /// <summary>
    /// The money gate, run after the operator has filled in anything unreadable.
    ///
    /// A cent or two is scan noise: Red Bull's own printed figure is drawn with the same damaged
    /// glyphs as the rows, so an exact match cannot be demanded. The ticket is flagged and written,
    /// which is what stops a whole delivery being lost over a rounding difference.
    ///
    /// A larger gap is different in kind — it means a line was missed or an amount was misread by
    /// dollars, and neither is something an operator would spot on a Paidout screen. That refuses,
    /// because a short invoice reaching SSCS looks perfectly consistent from the inside.
    /// </summary>
    private static void CheckRedBullTotal(
        RedBullTicket ticket, Invoice invoice, string inputFileName, List<ConversionWarning> warnings)
    {
        if (ticket.PrintedTotals.Count == 0) return;

        decimal lines = invoice.LineTotal;
        decimal gap = ticket.PrintedTotals.Min(t => Math.Abs(t - lines));
        if (gap < 0.005m) return;

        decimal closest = ticket.PrintedTotals.OrderBy(t => Math.Abs(t - lines)).First();
        if (gap <= RedBullTotalTolerance)
        {
            warnings.Add(new ConversionWarning(0, PdfReasonCodes.TotalMismatch,
                $"The lines come to {lines:C2} but the ticket prints {closest:C2}. The lines were used - "
                + "check the amounts against the paper before sending."));
            return;
        }

        throw new EdiConversionException(PdfReasonCodes.TotalMismatch,
            $"Could not convert {inputFileName}.\n\nThe {invoice.Lines.Count} product lines that could be "
            + $"read come to {lines:C2}, but the ticket says {closest:C2} - a difference of {gap:C2}. "
            + "That usually means a line is missing or an amount was misread, which nothing later would "
            + "catch. Re-scan the ticket, or enter this one in SSCS by hand. The original file was not "
            + "changed.");
    }

    /// <summary>How far the lines may sit from the printed total and still be written out.</summary>
    private const decimal RedBullTotalTolerance = 0.05m;

    /// <summary>
    /// Asks the operator for one figure on one product row that the scan destroyed, naming the row
    /// so they do not have to count. Cancelling skips the whole file — an invoice missing a line, or
    /// carrying a made-up one, would book the wrong money.
    /// </summary>
    private string AskLineField(
        string inputFileName, RedBullTicket ticket, RedBullLine line, LineField field,
        List<ConversionWarning> warnings)
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

        string? typed = _invoiceDetailsPrompt.AskLineField(new LineFieldRequest(
            inputFileName, "Red Bull", field, line.Position, ticket.Lines.Count,
            line.ItemNumber, line.Description, printed, line.Units, line.Amount));

        if (string.IsNullOrWhiteSpace(typed))
            throw new EdiConversionException(PdfReasonCodes.UnreadableLine,
                $"Skipped - the {what} for line {line.Position} of {inputFileName} was not supplied. "
                + "The original file was not changed.");

        warnings.Add(new ConversionWarning(line.Position, PdfReasonCodes.UnreadableLine,
            $"Line {line.Position}: the {what} was typed in by the operator (the scan read \"{printed}\")."));
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

        // The ticket's own printed date always wins; what came back is only consulted when there
        // was none to begin with, so a prompt cannot overwrite a date that was read from the paper.
        DateOnly date = request.Date ?? answer.Date;
        if (date == default)
            throw new EdiConversionException(PdfReasonCodes.NoDate,
                "The date could not be read from this Blue Bell invoice and none was entered.");

        return new InvoiceDetails(number, date);
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
