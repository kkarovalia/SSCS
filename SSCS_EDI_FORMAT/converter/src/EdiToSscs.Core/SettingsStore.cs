using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdiToSscs.Core;

/// <summary>Persisted per-user settings.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("outputFolder")]
    public string? OutputFolder { get; set; }
}

/// <summary>
/// Loads and atomically saves settings.json under the per-user settings directory.
/// Loading never throws: a missing, empty, malformed or partially written file simply
/// yields empty settings so the application can ask the user for a destination again.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _directory;
    private readonly string _settingsPath;

    public SettingsStore(string? directory = null)
    {
        _directory = directory ?? AppPaths.SettingsDirectory;
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();
            string json = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves atomically: the JSON is written to a temporary file in the same directory,
    /// flushed to disk, and then moved over settings.json in a single replace operation,
    /// so a crash can never leave a partially written settings file.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_directory);

        string tempPath = Path.Combine(_directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_settingsPath))
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _settingsPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Persists only the output folder.</summary>
    public void SaveOutputFolder(string? folder)
    {
        var s = Load();
        s.OutputFolder = folder;
        Save(s);
    }
}
