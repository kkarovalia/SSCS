using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdiToSscs.Core;

/// <summary>A single diagnostic log entry. Never contains invoice contents or full product records.</summary>
public sealed class LogEntry
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTimeOffset.Now.ToString("O");
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = ""; // Success | Warning | Failed | Canceled
    [JsonPropertyName("inputFileName")] public string? InputFileName { get; set; }
    [JsonPropertyName("inputPath")] public string? InputPath { get; set; }
    [JsonPropertyName("detectedFormat")] public string? DetectedFormat { get; set; }
    [JsonPropertyName("destinationPath")] public string? DestinationPath { get; set; }
    [JsonPropertyName("outputFileName")] public string? OutputFileName { get; set; }
    [JsonPropertyName("inputBytes")] public long? InputBytes { get; set; }
    [JsonPropertyName("outputBytes")] public long? OutputBytes { get; set; }
    [JsonPropertyName("recordsProcessed")] public int? RecordsProcessed { get; set; }
    [JsonPropertyName("upcERecordsExpanded")] public int? UpcERecordsExpanded { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
    [JsonPropertyName("userFriendlyReason")] public string? UserFriendlyReason { get; set; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
    [JsonPropertyName("failureLine")] public int? FailureLine { get; set; }
    [JsonPropertyName("exceptionDetail")] public string? ExceptionDetail { get; set; }
    [JsonPropertyName("inputSha256")] public string? InputSha256 { get; set; }
    [JsonPropertyName("outputSha256")] public string? OutputSha256 { get; set; }

    /// <summary>
    /// What the second reader cost on this file — see <see cref="VisionReading.Note"/>. Absent when
    /// the vendor has no vision path or no key is configured, which is also the answer to "was it
    /// even switched on".
    /// </summary>
    [JsonPropertyName("visionRead")] public string? VisionRead { get; set; }
}

/// <summary>
/// Writes JSON-lines diagnostic logs to the per-user Logs directory and enforces the
/// 30-day retention policy. Logging problems never block or fail a conversion.
/// </summary>
public sealed class ConversionLogger
{
    public const string LogFilePrefix = "EDI-to-SSCS_";
    public const int RetentionDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly object _writeLock = new();

    public ConversionLogger(string? directory = null)
    {
        _directory = directory ?? AppPaths.LogsDirectory;
    }

    public string Directory => _directory;

    /// <summary>Appends one entry to today's log file. Failures are swallowed (returns false).</summary>
    public bool TryWrite(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            lock (_writeLock)
            {
                System.IO.Directory.CreateDirectory(_directory);
                string file = Path.Combine(_directory, $"{LogFilePrefix}{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
            CleanupOldLogs(DateTime.UtcNow);
            return true;
        }
        catch
        {
            return false; // diagnostics must never break conversion
        }
    }

    /// <summary>
    /// Deletes log files created by this application that are strictly older than 30 days
    /// (last-write time earlier than now minus 30 days; a file exactly 30 days old is kept).
    /// Only files matching this application's log naming pattern, directly inside its own
    /// Logs directory, are considered. Reparse points (symbolic links, junctions) are never
    /// followed or deleted, and failures are reported but never thrown.
    /// </summary>
    public IReadOnlyList<string> CleanupOldLogs(DateTime utcNow)
    {
        var failures = new List<string>();
        try
        {
            var dir = new DirectoryInfo(_directory);
            if (!dir.Exists)
                return failures;
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                return failures; // never operate through a redirected log directory

            DateTime cutoffUtc = utcNow.AddDays(-RetentionDays);
            foreach (var file in dir.EnumerateFiles($"{LogFilePrefix}*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue; // do not follow links
                    if (file.LastWriteTimeUtc < cutoffUtc)
                        file.Delete();
                }
                catch (Exception ex)
                {
                    failures.Add($"{file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
        return failures;
    }
}
