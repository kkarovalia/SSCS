using System.Collections.Concurrent;

namespace EdiToSscs.Core;

/// <summary>Per-file status shown in the UI.</summary>
public enum FileStatus { Waiting, Converting, Converted, Warning, Failed, Canceled }

/// <summary>Which input a queued file is: a fixed-width EDI file or a searchable PDF invoice.</summary>
public enum ConversionKind { Edi, Pdf }

/// <summary>Which vendor's PDF layout to parse. Each PDF vendor has its own parser.</summary>
public enum PdfVendor { DrPepper, BlueBell, Chubiwinos, Bimbo, RedBull }

/// <summary>One file tracked by the conversion queue.</summary>
public sealed class QueuedFile
{
    public required string InputPath { get; init; }
    public string FileName => Path.GetFileName(InputPath);
    /// <summary>Which pipeline to run. Defaults to EDI so existing callers/tests are unchanged.</summary>
    public ConversionKind Kind { get; init; } = ConversionKind.Edi;
    /// <summary>Which PDF parser to use. Ignored for EDI input.</summary>
    public PdfVendor Vendor { get; init; } = PdfVendor.DrPepper;
    public FileStatus Status { get; internal set; } = FileStatus.Waiting;
    public string Detail { get; internal set; } = "";
    public string? OutputPath => OutputPaths.Count > 0 ? OutputPaths[0] : null;

    /// <summary>
    /// Every file written for this input. Every PDF vendor currently produces one, but the queue and
    /// the email send are both built for several, because nothing guarantees the next vendor will.
    /// </summary>
    public IReadOnlyList<string> OutputPaths { get; internal set; } = [];

    /// <summary>
    /// The XML this file converted to, kept for the session so the viewer can show it without
    /// reading the file back off disk.
    /// </summary>
    public IReadOnlyList<ConvertedDocument> Documents { get; internal set; } = [];

    private readonly HashSet<string> _emailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which of this file's invoices have already been emailed, by output file name.
    ///
    /// This is what lets Send Email be a single click with no confirmation: pressing it again sends
    /// only what has not gone yet. A duplicate invoice email books the delivery twice in SSCS and
    /// nothing downstream catches it, so "already sent" has to be remembered rather than assumed.
    /// </summary>
    public IReadOnlyCollection<string> EmailedFileNames
    {
        get { lock (_emailed) return _emailed.ToArray(); }
    }

    /// <summary>Records that one invoice has been sent. Safe to call from the send worker.</summary>
    public void MarkEmailed(string outputFileName)
    {
        ArgumentNullException.ThrowIfNull(outputFileName);
        lock (_emailed) _emailed.Add(outputFileName);
    }

    /// <summary>True when this invoice has already gone out and must not be sent again.</summary>
    public bool WasEmailed(string outputFileName)
    {
        lock (_emailed) return _emailed.Contains(outputFileName);
    }
}

/// <summary>
/// One invoice found inside a PDF, described for the operator so they can key the fields the
/// document itself cannot supply — always the invoice number, and the date as well when the scan
/// was too damaged to read one.
/// </summary>
public sealed record InvoiceDetailsRequest(
    string FileName,
    string Kind,
    DateOnly? Date,
    decimal Total,
    int LineCount,
    int SectionIndex,
    int SectionCount,
    DateOnly? SuggestedDate = null,
    /// <summary>The vendor whose paper the operator is holding, so the window can say whose invoice
    /// number it wants ("Type invoice for Bimbo") instead of a generic heading.</summary>
    string Vendor = "")
{
    // There is deliberately no SuggestedInvoiceNumber here. The parsers can read one off a scan and
    // some of them do (BlueBellSection.SuggestedInvoiceNumber), but it must not reach the prompt: it
    // is wrong often enough to matter, nothing downstream can catch that, and a filled-in box turns
    // the one field that needs a person's eyes into an Enter keypress. Leaving it out of this record
    // is what makes that a guarantee rather than a habit.

    /// <summary>True when the ticket's own date was unreadable, so the operator must supply one.</summary>
    public bool NeedsDate => Date is null;
}

/// <summary>
/// One product line whose barcode neither read nor could be recovered, described well enough for the
/// operator to find it on the paper and copy the number off it.
/// </summary>
public sealed record BarcodeRequest(
    string FileName,
    /// <summary>1-based position of this product among the invoice's lines.</summary>
    int Position,
    /// <summary>How many product lines the invoice has, so the prompt can say "3 of 9".</summary>
    int LineCount,
    /// <summary>The vendor's own product number as printed in the first column.</summary>
    string ItemNumber,
    string Description,
    int Units,
    decimal UnitCost,
    /// <summary>What the scan made of the barcode, or empty when it found nothing at all.</summary>
    string PrintedCode);

/// <summary>Which figure on a product row the scan destroyed.</summary>
public enum LineField
{
    /// <summary>The barcode printed under the description.</summary>
    Barcode,
    /// <summary>The UNITS column — how many individual units the line is for.</summary>
    Units,
    /// <summary>The line's extended dollar amount, the last column on the row.</summary>
    Amount,
}

/// <summary>
/// One figure on one product row that the scan destroyed and no arithmetic on the ticket could
/// recover, described well enough for the operator to find the row on the paper and read it off.
///
/// There is deliberately no <see cref="LineField"/> for the description. SSCS keeps its own wording
/// for any item it already knows and ignores the file's, so a garbled description costs nothing and
/// the operator can fix it on the rare new item — asking about it would be a prompt that changes
/// nothing. Barcode, units and amount are the opposite: each one is wrong money or wrong stock that
/// nothing downstream will catch.
/// </summary>
public sealed record LineFieldRequest(
    string FileName,
    /// <summary>The vendor whose paper the operator is holding.</summary>
    string Vendor,
    LineField Field,
    /// <summary>1-based position of this product among the invoice's rows.</summary>
    int Position,
    /// <summary>How many product rows the invoice has, so the prompt can say "3 of 9".</summary>
    int LineCount,
    /// <summary>The vendor's own product number as printed in the first column.</summary>
    string ItemNumber,
    /// <summary>Best-effort description, so the operator can find the row without counting.</summary>
    string Description,
    /// <summary>What the scan made of the field, or empty when it found nothing at all.</summary>
    string PrintedValue,
    /// <summary>The row's units, when they were readable — context for the operator.</summary>
    int? Units = null,
    /// <summary>The row's amount, when it was readable — context for the operator.</summary>
    decimal? Amount = null);

/// <summary>What the operator keyed for one invoice. <see cref="Date"/> is ignored when the ticket
/// carried a readable date of its own.</summary>
public sealed record InvoiceDetails(string InvoiceNumber, DateOnly Date = default);

/// <summary>
/// Asks the operator for the fields a scanned invoice cannot supply reliably.
///
/// An invoice number carries no check digit, and the scanners mis-read one silently — a Blue Bell
/// ticket's text layer says 033105035263 where the paper reads 033103035263, and Chubiwinos numbers
/// came back as "0V9293" and "6)9141" on two of eight samples. No amount of parsing can verify one,
/// so it is always typed, and the box is never pre-filled from the scan: a wrong number sitting in
/// the box invites an Enter keypress, which is the whole failure this prompt exists to prevent.
///
/// The date is asked for only when the scan destroyed it (one Blue Bell ticket reads
/// "DATE 12-14-Z023"): the ticket is otherwise perfectly good, and refusing a whole delivery over
/// one mangled character sends the operator back to keying it by hand.
///
/// A barcode is asked for only when it fails its own check digit AND the vendor's catalogue has
/// never recorded one for that product. The WinForms shell implements all of this with dialogs on
/// the UI thread; tests implement it directly.
/// </summary>
public interface IInvoiceDetailsPrompt
{
    /// <summary>Returns what the operator keyed, or null if they cancelled the file.</summary>
    InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request);

    /// <summary>
    /// Asks for one line's barcode. Returns the digits the operator typed, or null if they cancelled
    /// the file. The default refuses, so an implementation that cannot ask — a test fake, a headless
    /// caller — skips the invoice rather than inventing a code.
    /// </summary>
    string? AskBarcode(BarcodeRequest request) => null;

    /// <summary>
    /// Asks for one figure on one product row — its barcode, its units or its amount. Returns what
    /// the operator typed, or null if they cancelled the file. As with <see cref="AskBarcode"/> the
    /// default refuses, so a caller that cannot ask skips the invoice rather than inventing a number.
    /// </summary>
    string? AskLineField(LineFieldRequest request) => null;
}

/// <summary>
/// Supplies and persists the destination folder. The WinForms shell implements this by
/// showing dialogs on the UI thread; tests implement it directly.
/// </summary>
public interface IDestinationProvider
{
    string? CurrentFolder { get; }
    DestinationStatus ValidateCurrent();
    /// <summary>Asks the user for a new folder, explaining why. Returns null if the user cancels.</summary>
    string? RequestNewFolder(string reasonMessage);
    void SaveFolder(string folder);
}

/// <summary>Abstraction over the end-to-end single-file conversion, for testability.</summary>
public interface IFileConverter
{
    ConversionOutcome ConvertFile(QueuedFile file, string destinationFolder);
}

/// <summary>
/// Presentation logic for the main window, free of any WinForms dependency:
/// maintains the file queue, drives statuses (Waiting → Converting → Converted/Warning/
/// Failed/Canceled), revalidates the destination folder before every conversion, and asks
/// for a replacement folder when the saved one is unavailable. Files are processed on a
/// background worker so the UI stays responsive.
/// </summary>
public sealed class ConversionCoordinator
{
    private readonly IFileConverter _converter;
    private readonly IDestinationProvider _destination;
    private readonly ConcurrentQueue<QueuedFile> _queue = new();
    private readonly object _workerLock = new();
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;

    /// <summary>Raised (on a worker thread) whenever a file's status or detail changes.</summary>
    public event Action<QueuedFile>? FileChanged;
    /// <summary>Raised (on a worker thread) with a final plain-language summary when the queue drains.</summary>
    public event Action<string>? BatchCompleted;

    public ConversionCoordinator(IFileConverter converter, IDestinationProvider destination)
    {
        _converter = converter;
        _destination = destination;
    }

    /// <summary>
    /// Adds dropped or picked paths to the queue and starts converting immediately.
    /// Folders are rejected with a Failed status rather than being traversed.
    /// Returns the created queue items (including immediately-rejected ones).
    /// </summary>
    public IReadOnlyList<QueuedFile> Enqueue(
        IEnumerable<string> paths, ConversionKind kind = ConversionKind.Edi, PdfVendor vendor = PdfVendor.DrPepper)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = new List<QueuedFile>();
        foreach (string path in paths)
        {
            var item = new QueuedFile { InputPath = path, Kind = kind, Vendor = vendor };
            added.Add(item);
            if (Directory.Exists(path))
            {
                item.Status = FileStatus.Failed;
                item.Detail = "Folders are not supported. Drop individual files instead.";
                RaiseFileChanged(item);
                continue;
            }
            item.Detail = "Waiting";
            RaiseFileChanged(item);
            _queue.Enqueue(item);
        }
        StartWorkerIfIdle();
        return added;
    }

    /// <summary>True while files are queued or being converted.</summary>
    public bool IsBusy
    {
        get { lock (_workerLock) return _workerRunning || !_queue.IsEmpty; }
    }

    /// <summary>Completes when all queued work has finished (used by tests and shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task worker;
            lock (_workerLock)
            {
                if (!_workerRunning && _queue.IsEmpty) return;
                worker = _worker;
            }
            await worker.ConfigureAwait(false);
        }
    }

    private void StartWorkerIfIdle()
    {
        lock (_workerLock)
        {
            if (_workerRunning || _queue.IsEmpty) return;
            _workerRunning = true;
            _worker = Task.Run(ProcessLoop);
        }
    }

    private void ProcessLoop()
    {
        while (true)
        {
            try
            {
                ProcessQueue();
            }
            catch
            {
                // Defensive: per-file work is already guarded; an escaping exception
                // (e.g. from an event subscriber) must never brick the worker.
            }
            lock (_workerLock)
            {
                // Only stop once the queue is verifiably empty; this closes the race where
                // a file is enqueued just as the previous drain finishes. Enqueue adds the
                // item before taking this lock, so either we see it here or the producer
                // sees _workerRunning == false and starts a fresh worker.
                if (_queue.IsEmpty)
                {
                    _workerRunning = false;
                    return;
                }
            }
        }
    }

    private void RaiseFileChanged(QueuedFile file)
    {
        try { FileChanged?.Invoke(file); }
        catch { /* the UI may be closing; conversion safety never depends on subscribers */ }
    }

    private void RaiseBatchCompleted(string summary)
    {
        try { BatchCompleted?.Invoke(summary); }
        catch { /* see above */ }
    }

    private void ProcessQueue()
    {
        int converted = 0, failed = 0, canceled = 0, warned = 0;
        string? lastOutputPath = null;
        string? lastMessage = null;

        while (_queue.TryDequeue(out var item))
        {
            // Revalidate the destination before every conversion; the folder may have been
            // deleted, renamed, disconnected or made read-only since the last file.
            string? folder = EnsureUsableDestination(out bool userCanceled);
            if (folder is null)
            {
                item.Status = FileStatus.Canceled;
                item.Detail = userCanceled
                    ? "Canceled - no output folder was chosen. The original file was not changed."
                    : "Canceled - the output folder is unavailable. The original file was not changed.";
                RaiseFileChanged(item);
                canceled++;
                // Without a usable destination the rest of the batch cannot proceed either.
                while (_queue.TryDequeue(out var rest))
                {
                    rest.Status = FileStatus.Canceled;
                    rest.Detail = item.Detail;
                    RaiseFileChanged(rest);
                    canceled++;
                }
                break;
            }

            item.Status = FileStatus.Converting;
            item.Detail = "Converting...";
            RaiseFileChanged(item);

            ConversionOutcome outcome;
            try
            {
                outcome = _converter.ConvertFile(item, folder);
            }
            catch (Exception ex)
            {
                outcome = new ConversionOutcome
                {
                    Success = false,
                    UserMessage = $"Could not convert {item.FileName}.\n\nAn unexpected problem occurred ({ex.GetType().Name}). The original file was not changed.",
                };
            }

            if (outcome.Success)
            {
                item.Status = outcome.HasWarnings ? FileStatus.Warning : FileStatus.Converted;
                item.OutputPaths = outcome.FinalOutputPaths;
                item.Documents = outcome.Documents;
                item.Detail = outcome.UserMessage;
                if (outcome.HasWarnings) warned++; else converted++;
                lastOutputPath = outcome.FinalOutputPath;
            }
            else if (outcome.Canceled)
            {
                // The operator dismissed the invoice-number prompt: their choice, not a failure.
                item.Status = FileStatus.Canceled;
                item.Detail = outcome.UserMessage;
                canceled++;
            }
            else
            {
                item.Status = FileStatus.Failed;
                item.Detail = outcome.UserMessage;
                failed++;
            }
            lastMessage = outcome.UserMessage;
            RaiseFileChanged(item);
        }

        RaiseBatchCompleted(BuildSummary(converted, warned, failed, canceled, lastOutputPath, lastMessage));
    }

    private string? EnsureUsableDestination(out bool userCanceled)
    {
        userCanceled = false;
        var status = _destination.ValidateCurrent();
        if (status == DestinationStatus.Ok)
            return _destination.CurrentFolder;

        // The saved folder is unusable: explain why and ask for a replacement. Loop until
        // the user picks a usable folder or cancels.
        string reason = DestinationValidator.Describe(status);
        while (true)
        {
            string? chosen = _destination.RequestNewFolder(reason);
            if (chosen is null)
            {
                userCanceled = true;
                return null;
            }
            var chosenStatus = DestinationValidator.Validate(chosen);
            if (chosenStatus == DestinationStatus.Ok)
            {
                _destination.SaveFolder(chosen);
                return chosen;
            }
            reason = DestinationValidator.Describe(chosenStatus);
        }
    }

    private static string BuildSummary(
        int converted, int warned, int failed, int canceled, string? lastOutputPath, string? lastMessage)
    {
        int okTotal = converted + warned;
        if (okTotal == 1 && failed == 0 && canceled == 0 && lastOutputPath is not null)
            return $"Done. Saved {Path.GetFileName(lastOutputPath)} to {Path.GetDirectoryName(lastOutputPath)}";
        if (okTotal == 0 && failed + canceled == 1 && lastMessage is not null)
            return lastMessage;

        var parts = new List<string>();
        if (okTotal > 0 && lastOutputPath is not null)
            parts.Add($"{okTotal} file{(okTotal == 1 ? "" : "s")} converted to {Path.GetDirectoryName(lastOutputPath)}");
        if (warned > 0) parts.Add($"{warned} with warnings");
        if (failed > 0) parts.Add($"{failed} failed");
        if (canceled > 0) parts.Add($"{canceled} canceled");
        return parts.Count == 0 ? "Nothing to convert." : "Done. " + string.Join(", ", parts) + ".";
    }
}
