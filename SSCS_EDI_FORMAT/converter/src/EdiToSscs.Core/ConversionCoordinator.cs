using System.Collections.Concurrent;

namespace EdiToSscs.Core;

/// <summary>Per-file status shown in the UI.</summary>
public enum FileStatus { Waiting, Converting, Converted, Warning, Failed, Canceled }

/// <summary>One file tracked by the conversion queue.</summary>
public sealed class QueuedFile
{
    public required string InputPath { get; init; }
    public string FileName => Path.GetFileName(InputPath);
    public FileStatus Status { get; internal set; } = FileStatus.Waiting;
    public string Detail { get; internal set; } = "";
    public string? OutputPath { get; internal set; }
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
    ConversionOutcome ConvertFile(string inputPath, string destinationFolder);
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
    public IReadOnlyList<QueuedFile> Enqueue(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = new List<QueuedFile>();
        foreach (string path in paths)
        {
            var item = new QueuedFile { InputPath = path };
            added.Add(item);
            if (Directory.Exists(path))
            {
                item.Status = FileStatus.Failed;
                item.Detail = "Folders are not supported. Drop individual EDI files instead.";
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
                outcome = _converter.ConvertFile(item.InputPath, folder);
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
                item.OutputPath = outcome.FinalOutputPath;
                item.Detail = outcome.UserMessage;
                if (outcome.HasWarnings) warned++; else converted++;
                lastOutputPath = outcome.FinalOutputPath;
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

    private static string BuildSummary(int converted, int warned, int failed, int canceled, string? lastOutputPath, string? lastMessage)
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
