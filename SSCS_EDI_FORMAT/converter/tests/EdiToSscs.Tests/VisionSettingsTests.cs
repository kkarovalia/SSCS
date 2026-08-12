using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// How the AI reader's configuration is kept: off unless deliberately switched on, its key never in
/// the settings file as readable text, and a file written by an older build still loading.
/// </summary>
public class VisionSettingsTests
{
    /// <summary>
    /// The state the converter ships in. Nothing is sent anywhere until somebody goes and turns it
    /// on, which is the only acceptable default for a feature that puts invoices on the internet.
    /// </summary>
    [Fact]
    public void ItIsOffUntilSomebodyTurnsItOn()
    {
        using var tmp = new TempDir();
        var settings = new SettingsStore(tmp.Sub("settings")).Load();

        Assert.NotNull(settings.Vision);
        Assert.False(settings.Vision.Enabled);
        Assert.False(settings.Vision.IsActive);
        Assert.Null(settings.Vision.ProtectedApiKey);
    }

    /// <summary>A key without the switch, or the switch without a key, reads nothing.</summary>
    [Theory]
    [InlineData(true, "a-key", true)]
    [InlineData(false, "a-key", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "   ", false)]
    [InlineData(false, null, false)]
    public void BothTheKeyAndTheSwitchAreNeeded(bool enabled, string? key, bool active) =>
        Assert.Equal(active, new VisionSettings { Enabled = enabled, ProtectedApiKey = key }.IsActive);

    /// <summary>
    /// Several keys round-trip, and a settings file written before there was a list still works —
    /// its single key simply becomes the first one tried.
    /// </summary>
    [Fact]
    public void SeveralKeysRoundTripAndTheOldSingleKeyStillCounts()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));

        store.SaveVision(new VisionSettings
        {
            Enabled = true,
            ProtectedApiKey = "legacy",
            ProtectedApiKeys = ["second", "third"],
            Models = ["gemini-3.5-flash", "gemini-3.5-flash-lite"],
        });

        var loaded = store.Load().Vision;
        Assert.True(loaded.IsActive);
        Assert.Equal(["legacy", "second", "third"], loaded.AllProtectedKeys);
        Assert.Equal(["gemini-3.5-flash", "gemini-3.5-flash-lite"], loaded.Models);
    }

    /// <summary>Blank and duplicate entries are not keys and never become an attempt.</summary>
    [Fact]
    public void BlanksAndDuplicatesAreDropped()
    {
        var settings = new VisionSettings
        {
            ProtectedApiKey = "  one  ",
            ProtectedApiKeys = ["one", "", "   ", "two", null!],
        };

        Assert.Equal(["one", "two"], settings.AllProtectedKeys);
    }

    /// <summary>A settings file with only the old single key is still active on its own.</summary>
    [Fact]
    public void AFileWithOnlyTheOldKeyIsStillActive() =>
        Assert.True(new VisionSettings { Enabled = true, ProtectedApiKey = "cipher" }.IsActive);

    [Fact]
    public void TheSettingsRoundTrip()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));

        store.SaveVision(new VisionSettings
        {
            Enabled = true, Model = "gemini-3.6-flash", ProtectedApiKey = "cipher",
        });

        var loaded = store.Load().Vision;
        Assert.True(loaded.Enabled);
        Assert.Equal("gemini-3.6-flash", loaded.Model);
        Assert.Equal("cipher", loaded.ProtectedApiKey);
    }

    /// <summary>
    /// Saving one section never disturbs another. The store's SSCS address is the single most
    /// consequential setting in the application, and it must not be touched by a change to this.
    /// </summary>
    [Fact]
    public void SavingItLeavesTheEmailAndFolderAlone()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));
        store.Save(new AppSettings
        {
            OutputFolder = @"C:\Out",
            Email = new EmailSettings { ToAddress = "store@example.com", AdminPinHash = "hash" },
        });

        store.SaveVision(new VisionSettings { Enabled = true, ProtectedApiKey = "cipher" });

        var loaded = store.Load();
        Assert.Equal(@"C:\Out", loaded.OutputFolder);
        Assert.Equal("store@example.com", loaded.Email.ToAddress);
        Assert.Equal("hash", loaded.Email.AdminPinHash);
        Assert.True(loaded.Vision.Enabled);
    }

    /// <summary>
    /// A settings file written before this feature existed has no "vision" key at all, and one
    /// written by hand can have an explicit null. Neither may crash the application on startup.
    /// </summary>
    [Theory]
    [InlineData("""{"outputFolder":"C:\\Out"}""")]
    [InlineData("""{"outputFolder":"C:\\Out","vision":null}""")]
    public void ASettingsFileWithoutThisSectionStillLoads(string json)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);

        var loaded = new SettingsStore(dir).Load();
        Assert.Equal(@"C:\Out", loaded.OutputFolder);
        Assert.NotNull(loaded.Vision);
        Assert.False(loaded.Vision.IsActive);
    }

    /// <summary>
    /// The key is encrypted at rest, so it is not sitting in settings.json for anyone who opens the
    /// file — the same treatment the mailbox password gets.
    /// </summary>
    [Fact]
    public void TheKeyIsNotWrittenAsReadableText()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        var store = new SettingsStore(dir);

        const string Key = "AIzaSy-not-a-real-key-0123456789";
        store.SaveVision(new VisionSettings { Enabled = true, ProtectedApiKey = SecretProtector.Protect(Key) });

        string onDisk = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Assert.DoesNotContain(Key, onDisk, StringComparison.Ordinal);
        Assert.Equal(Key, SecretProtector.Unprotect(store.Load().Vision.ProtectedApiKey));
    }
}
