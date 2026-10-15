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

    /// <summary>The second reader, off by default. Never null after a load, as above.</summary>
    [JsonPropertyName("vision")]
    public VisionSettings Vision { get; set; } = new();
}

/// <summary>
/// The AI reader that looks at the picture of a scanned invoice.
///
/// Off unless a key is present, and the converter works exactly as it always has without one — this
/// is a second opinion layered on top of the existing reading, never a dependency. What it changes
/// is how often the operator is asked about a figure the scan damaged.
///
/// Worth being plain about what turning it on means: the pictures of the invoices are sent to
/// Google. They are vendor delivery tickets — products, counts and prices — with no customer or
/// payment information on them, but they do leave the building, and on the free tier Google may use
/// them to improve their models. That is the trade for it costing nothing.
/// </summary>
public sealed class VisionSettings
{
    /// <summary>
    /// The Gemini API key, DPAPI-encrypted for the current Windows user. Read it with
    /// <see cref="SecretProtector.Unprotect"/>; it is never held or displayed as plain text.
    ///
    /// Superseded by <see cref="ProtectedApiKeys"/> and kept only so a settings file written by an
    /// earlier build still works. <see cref="AllProtectedKeys"/> is what callers should read.
    /// </summary>
    [JsonPropertyName("protectedApiKey")]
    public string? ProtectedApiKey { get; set; }

    /// <summary>
    /// Every key to try, in order, each DPAPI-encrypted. More than one because a free-tier key runs
    /// out: the store's own thirty-five invoices exhausted one in a single afternoon, and a key with
    /// nothing left is a silent loss of the second reader rather than a visible failure.
    /// </summary>
    [JsonPropertyName("protectedApiKeys")]
    public List<string> ProtectedApiKeys { get; set; } = [];

    /// <summary>Every key configured, old single-key setting first. Never null, never blank entries.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllProtectedKeys =>
        [.. new[] { ProtectedApiKey }.Concat(ProtectedApiKeys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Which model to ask. Empty means the current default, so a settings file does not pin the
    /// converter to a model that has since been retired.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// The models to fall through, in order. Each carries its own quota, so a key that has run out
    /// on one may still answer on the next — which is a great deal cheaper than a second account.
    /// Empty means <see cref="GeminiVisionReader.DefaultModels"/>.
    /// </summary>
    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// Whether to use it. Separate from the key so the reader can be turned off for a run — a slow
    /// line, a quota exhausted — without the key having to be typed in again afterwards.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>True when a reading will actually be attempted.</summary>
    public bool IsActive => Enabled && AllProtectedKeys.Count > 0;

    public VisionSettings Clone() => new()
    {
        ProtectedApiKey = ProtectedApiKey,
        ProtectedApiKeys = [.. ProtectedApiKeys],
        Model = Model,
        Models = [.. Models],
        Enabled = Enabled,
    };
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

    /// <summary>
    /// Where PDF to Modisoft EDI sends its files: the store's own email, chosen by the store
    /// (2026-10-05). Unlike SSCS's address it files nothing by itself. Somebody downloads the
    /// attachment and imports it into a purchase they create in Modisoft. A store can have either
    /// address, or both.
    /// </summary>
    [JsonPropertyName("modisoftToAddress")]
    public string? ModisoftToAddress { get; set; }

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

    /// <summary>True when every field an SSCS send needs is present.</summary>
    public bool IsComplete => IsCompleteFor(OutputTarget.Sscs);

    /// <summary>True when the mailbox this PC sends from is fully set up, whoever it sends to.</summary>
    public bool HasSender =>
        !string.IsNullOrWhiteSpace(FromAddress)
        && !string.IsNullOrWhiteSpace(SmtpHost)
        && SmtpPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(ProtectedPassword);

    /// <summary>Where a file for <paramref name="target"/> is sent.</summary>
    public string? AddressFor(OutputTarget target) =>
        target == OutputTarget.Modisoft ? ModisoftToAddress : ToAddress;

    /// <summary>True when a file for <paramref name="target"/> has somewhere to go and a way to get there.</summary>
    public bool IsCompleteFor(OutputTarget target) =>
        HasSender && !string.IsNullOrWhiteSpace(AddressFor(target));

    public EmailSettings Clone() => new()
    {
        ToAddress = ToAddress,
        ModisoftToAddress = ModisoftToAddress,
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
            loaded.Vision ??= new VisionSettings();
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

    /// <summary>Persists only the AI reader section, leaving everything else untouched.</summary>
    public void SaveVision(VisionSettings vision)
    {
        ArgumentNullException.ThrowIfNull(vision);
        var s = Load();
        s.Vision = vision;
        Save(s);
    }
}
