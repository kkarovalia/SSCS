using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdiToSscs.Core;

/// <summary>Persisted per-user settings.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("outputFolder")]
    public string? OutputFolder { get; set; }

    /// <summary>
    /// The store's SSCS address. Never null after a load, so callers never have to null-check their
    /// way through a settings file written by an older build.
    /// </summary>
    [JsonPropertyName("email")]
    public EmailSettings Email { get; set; } = new();
}

/// <summary>
/// The store's SSCS delivery address and the mailbox the converter signs in as.
///
/// Store routing is the email address a file is sent to — nothing inside the file decides which
/// store it lands in. So <see cref="ToAddress"/> is the single most consequential setting in the
/// application, which is why changing any of this is behind <see cref="AdminPinHash"/>.
/// </summary>
public sealed class EmailSettings
{
    /// <summary>The store's SSCS address — where converted XML is delivered.</summary>
    [JsonPropertyName("toAddress")]
    public string? ToAddress { get; set; }

    /// <summary>The mailbox that sends, which for Gmail is also the SMTP username.</summary>
    [JsonPropertyName("fromAddress")]
    public string? FromAddress { get; set; }

    [JsonPropertyName("smtpHost")]
    public string? SmtpHost { get; set; } = DefaultSmtpHost;

    [JsonPropertyName("smtpPort")]
    public int SmtpPort { get; set; } = DefaultSmtpPort;

    /// <summary>
    /// The app password, DPAPI-encrypted for the current Windows user. Read it with
    /// <see cref="SecretProtector.Unprotect"/>; it is never held or displayed as plain text.
    /// </summary>
    [JsonPropertyName("protectedPassword")]
    public string? ProtectedPassword { get; set; }

    /// <summary>PBKDF2 hash of the PIN that gates this section of Settings. Null until one is set.</summary>
    [JsonPropertyName("adminPinHash")]
    public string? AdminPinHash { get; set; }

    /// <summary>Gmail's submission endpoint: STARTTLS on 587, which is what an app password expects.</summary>
    public const string DefaultSmtpHost = "smtp.gmail.com";
    public const int DefaultSmtpPort = 587;

    /// <summary>True when every field a send needs is present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ToAddress)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && !string.IsNullOrWhiteSpace(SmtpHost)
        && SmtpPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(ProtectedPassword);

    public EmailSettings Clone() => new()
    {
        ToAddress = ToAddress,
        FromAddress = FromAddress,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        ProtectedPassword = ProtectedPassword,
        AdminPinHash = AdminPinHash,
    };
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
            var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            // A file written before email existed has no "email" key (the initializer covers that),
            // but an explicit null would slip through it. Normalise here so nothing downstream has
            // to defend against a null section.
            loaded.Email ??= new EmailSettings();
            return loaded;
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

    /// <summary>Persists only the email section, leaving the output folder untouched.</summary>
    public void SaveEmail(EmailSettings email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var s = Load();
        s.Email = email;
        Save(s);
    }
}
