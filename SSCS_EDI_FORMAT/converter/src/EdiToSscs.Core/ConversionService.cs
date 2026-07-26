using System.Security.Cryptography;

namespace EdiToSscs.Core;

/// <summary>Outcome of converting a single file end-to-end (read, convert, validate, write).</summary>
public sealed class ConversionOutcome
{
    public required bool Success { get; init; }
    public bool HasWarnings { get; init; }
    public string? FinalOutputPath { get; init; }
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

    public ConversionService(ConversionLogger? logger = null, string? appVersion = null)
    {
        _logger = logger ?? new ConversionLogger();
        _appVersion = appVersion ?? typeof(ConversionService).Assembly.GetName().Version?.ToString() ?? "unknown";
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
                ? ConvertPdf(inputBytes)
                : ConvertEdi(inputBytes);
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
        log.OutputBytes = converted.Bytes.LongLength;
        if (converted.HasWarnings)
            log.Warnings = converted.Warnings.Select(w => $"line {w.LineNumber} [{w.ReasonCode}]: {w.Message}").ToList();

        string desiredName = OutputNaming.BuildOutputFileName(inputFileName);
        string tempPath = Path.Combine(destinationFolder, $"~sscs-{Guid.NewGuid():N}.tmp");
        try
        {
            // 1) Write the fully converted bytes to a temporary file in the destination and flush to disk.
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(converted.Bytes, 0, converted.Bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // 2) Re-read and verify the temporary file is a complete, correct copy.
            byte[] written = File.ReadAllBytes(tempPath);
            if (!written.AsSpan().SequenceEqual(converted.Bytes))
                throw new IOException("The written file did not match the converted data (disk problem?).");

            // 3) Compute success metadata BEFORE the final rename so that nothing after the
            //    commit point can throw and misreport an already-written file as failed.
            log.Status = converted.HasWarnings ? "Warning" : "Success";
            log.InputSha256 = System.Convert.ToHexString(SHA256.HashData(inputBytes));
            log.OutputSha256 = System.Convert.ToHexString(SHA256.HashData(converted.Bytes));

            // 4) Atomically claim a non-conflicting final name. File.Move fails if the target
            //    exists, which makes the claim safe even when several conversions run at once.
            string finalPath = MoveToAvailableName(tempPath, destinationFolder, desiredName, inputPath);

            log.OutputFileName = Path.GetFileName(finalPath);
            _logger.TryWrite(log);

            string message = $"Saved {Path.GetFileName(finalPath)} to {destinationFolder}";
            if (converted.HasWarnings)
                message += $"\n{string.Join("\n", converted.Warnings.Select(w => w.Message))}";

            return new ConversionOutcome
            {
                Success = true,
                HasWarnings = converted.HasWarnings,
                FinalOutputPath = finalPath,
                Result = converted.EdiResult,
                UserMessage = message,
            };
        }
        catch (Exception ex)
        {
            return Fail(log, ex, WriteFailureMessage(inputFileName, destinationFolder, ex));
        }
        finally
        {
            // A failed conversion must never leave a temporary file that looks like an invoice.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Unifies the EDI and PDF conversion outputs so the safe-write path below is shared.</summary>
    private sealed record Converted(
        byte[] Bytes, string Format, int RecordCount,
        IReadOnlyList<ConversionWarning> Warnings, int UpcExpanded, ConversionResult? EdiResult)
    {
        public bool HasWarnings => Warnings.Count > 0;
    }

    private static Converted ConvertEdi(byte[] inputBytes)
    {
        var r = EdiConverter.Convert(inputBytes);
        return new Converted(r.OutputBytes, r.Format.ToString(), r.RecordCount, r.Warnings, r.UpcEExpandedCount, r);
    }

    private static Converted ConvertPdf(byte[] inputBytes)
    {
        if (!PdfTextExtractor.LooksLikePdf(inputBytes))
            throw new EdiConversionException(PdfReasonCodes.NotAPdf,
                "This file does not look like a PDF. Please drop the vendor's PDF invoice.");

        IReadOnlyList<string> lines = PdfTextExtractor.ExtractLines(inputBytes);
        var warnings = new List<ConversionWarning>();
        Invoice invoice = DrPepperPdfParser.Parse(lines, warnings);
        byte[] output = NaxmlBuilder.Build(invoice);
        return new Converted(output, "PDF/DrPepper", invoice.Lines.Count, warnings, 0, null);
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
