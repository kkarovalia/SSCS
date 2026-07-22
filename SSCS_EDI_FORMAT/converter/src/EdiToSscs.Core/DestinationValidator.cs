namespace EdiToSscs.Core;

public enum DestinationStatus
{
    Ok,
    NotConfigured,
    Missing,
    NotADirectory,
    NotWritable,
    Unreachable,
}

/// <summary>
/// Validates that a destination folder exists, is a directory, and is writable.
/// The check runs with a timeout so a stale disconnected network path cannot hang the UI.
/// </summary>
public static class DestinationValidator
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static DestinationStatus Validate(string? path, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DestinationStatus.NotConfigured;

        var task = Task.Run(() => ValidateBlocking(path));
        if (!task.Wait(timeout ?? DefaultTimeout))
            return DestinationStatus.Unreachable; // stale network path or unresponsive device
        return task.Result;
    }

    private static DestinationStatus ValidateBlocking(string path)
    {
        try
        {
            if (File.Exists(path))
                return DestinationStatus.NotADirectory;
            if (!Directory.Exists(path))
                return DestinationStatus.Missing;

            // Probe writability with a real file; attribute checks alone are unreliable.
            string probe = Path.Combine(path, $".sscs-write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    fs.WriteByte(0x20);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return DestinationStatus.NotWritable;
            }
            return DestinationStatus.Ok;
        }
        catch (Exception)
        {
            return DestinationStatus.Unreachable;
        }
    }

    /// <summary>Plain-language description used by the UI when the folder is not usable.</summary>
    public static string Describe(DestinationStatus status) => status switch
    {
        DestinationStatus.Ok => "The output folder is ready.",
        DestinationStatus.NotConfigured => "No output folder has been chosen yet. Please choose a folder for converted files.",
        DestinationStatus.Missing => "Your saved output folder is no longer available. Please choose a new folder before converting this file.",
        DestinationStatus.NotADirectory => "Your saved output location points to a file, not a folder. Please choose a folder before converting this file.",
        DestinationStatus.NotWritable => "Your saved output folder is read-only. Please choose a different folder before converting this file.",
        _ => "Your saved output folder cannot be reached right now (it may be a disconnected network drive). Please choose a new folder before converting this file.",
    };
}
