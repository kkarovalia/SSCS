using System.Security.AccessControl;
using System.Security.Principal;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Mandatory destination-setting and settings-persistence behavior.</summary>
public class SettingsAndDestinationTests
{
    // ------------------------------------------------ settings persistence

    [Fact]
    public void FirstLaunch_NoSettingsFile_YieldsEmptySettings()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(Path.Combine(tmp.Path, "does-not-exist-yet"));
        Assert.Null(store.Load().OutputFolder);
    }

    [Fact]
    public void SavedFolder_RoundTrips()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));
        store.Save(new AppSettings { OutputFolder = @"C:\Some\Folder" });
        Assert.Equal(@"C:\Some\Folder", store.Load().OutputFolder);
    }

    [Theory]
    [InlineData("this is not json {{{")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"outputFolder\": 12")] // truncated / partially written
    [InlineData("null")]
    public void MalformedOrEmptySettings_RecoverToEmptySettings(string content)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        File.WriteAllText(Path.Combine(dir, "settings.json"), content);
        var store = new SettingsStore(dir);
        Assert.Null(store.Load().OutputFolder); // never throws
    }

    [Fact]
    public void EmptyJsonObject_YieldsEmptySettings()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        Assert.Null(new SettingsStore(dir).Load().OutputFolder);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileRemains_AndReplacesExisting()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        var store = new SettingsStore(dir);
        store.Save(new AppSettings { OutputFolder = @"C:\First" });
        store.Save(new AppSettings { OutputFolder = @"C:\Second" });

        Assert.Equal(@"C:\Second", store.Load().OutputFolder);
        Assert.Single(Directory.GetFiles(dir)); // only settings.json, no leftover temp files
    }

    [Fact]
    public void SettingsFileCannotBeWritten_ThrowsSoCallerCanWarn()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        // Occupy the settings path with a directory so the atomic replace must fail.
        Directory.CreateDirectory(Path.Combine(dir, "settings.json"));
        var store = new SettingsStore(dir);
        Assert.ThrowsAny<Exception>(() => store.Save(new AppSettings { OutputFolder = @"C:\X" }));
    }

    [Fact]
    public void TwoInstances_ReadingAndWritingConcurrently_StayConsistent()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        var a = new SettingsStore(dir);
        var b = new SettingsStore(dir);

        Parallel.For(0, 50, i =>
        {
            var store = i % 2 == 0 ? a : b;
            try { store.Save(new AppSettings { OutputFolder = $@"C:\Folder{i}" }); }
            catch (IOException) { /* concurrent replace lost the race; acceptable */ }
            var loaded = store.Load(); // must never throw or return garbage
            Assert.True(loaded.OutputFolder is null || loaded.OutputFolder.StartsWith(@"C:\Folder"));
        });

        Assert.StartsWith(@"C:\Folder", new SettingsStore(dir).Load().OutputFolder!);
    }

    [Fact]
    public void UnicodeAndSpaces_InSavedPath_RoundTrip()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));
        string path = Path.Combine(tmp.Path, "Zażółć gęślą jaźń", "With Spaces");
        store.Save(new AppSettings { OutputFolder = path });
        Assert.Equal(path, store.Load().OutputFolder);
    }

    // ------------------------------------------------ destination validation

    [Fact]
    public void ValidWritableFolder_IsOk()
    {
        using var tmp = new TempDir();
        Assert.Equal(DestinationStatus.Ok, DestinationValidator.Validate(tmp.Path));
    }

    [Fact]
    public void UnconfiguredFolder_IsNotConfigured()
    {
        Assert.Equal(DestinationStatus.NotConfigured, DestinationValidator.Validate(null));
        Assert.Equal(DestinationStatus.NotConfigured, DestinationValidator.Validate("  "));
    }

    [Fact]
    public void DeletedFolder_IsMissing()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("gone");
        Directory.Delete(dir);
        Assert.Equal(DestinationStatus.Missing, DestinationValidator.Validate(dir));
    }

    [Fact]
    public void RenamedFolder_IsMissingUnderOldName()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("original");
        Directory.Move(dir, Path.Combine(tmp.Path, "renamed"));
        Assert.Equal(DestinationStatus.Missing, DestinationValidator.Validate(dir));
    }

    [Fact]
    public void PathPointingToFile_IsNotADirectory()
    {
        using var tmp = new TempDir();
        string file = Path.Combine(tmp.Path, "a-file.txt");
        File.WriteAllText(file, "x");
        Assert.Equal(DestinationStatus.NotADirectory, DestinationValidator.Validate(file));
    }

    [Fact]
    public void ReadOnlyFolder_IsNotWritable()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("readonly");
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles, AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            Assert.Equal(DestinationStatus.NotWritable, DestinationValidator.Validate(dir));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void DisconnectedNetworkPath_IsNotOk_AndDoesNotHangForever()
    {
        var started = DateTime.UtcNow;
        var status = DestinationValidator.Validate(@"\\nonexistent-host-sscs-test\share\folder", TimeSpan.FromSeconds(20));
        Assert.NotEqual(DestinationStatus.Ok, status);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void UnicodePath_Validates()
    {
        using var tmp = new TempDir();
        string dir = Path.Combine(tmp.Path, "Ünïcode päth 日本語");
        Directory.CreateDirectory(dir);
        Assert.Equal(DestinationStatus.Ok, DestinationValidator.Validate(dir));
    }

    [Fact]
    public void PathWithSpaces_Validates()
    {
        using var tmp = new TempDir();
        string dir = Path.Combine(tmp.Path, "folder with   spaces");
        Directory.CreateDirectory(dir);
        Assert.Equal(DestinationStatus.Ok, DestinationValidator.Validate(dir));
    }

    [Fact]
    public void EveryFailureStatus_HasAPlainLanguageDescription()
    {
        foreach (DestinationStatus status in Enum.GetValues<DestinationStatus>())
        {
            string text = DestinationValidator.Describe(status);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Exception", text);
        }
        Assert.Contains("no longer available", DestinationValidator.Describe(DestinationStatus.Missing));
    }

    [Fact]
    public void FolderDisappearsBetweenValidationAndOutputCreation_FailsGracefully()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string input = Path.Combine(tmp.Path, "EDI_GHRA_131454.txt");
        File.WriteAllBytes(input, TestUtil.Fixture(@"EDI\EDI_GHRA_131454.txt"));

        Assert.Equal(DestinationStatus.Ok, DestinationValidator.Validate(dest));
        Directory.Delete(dest); // vanish after validation, before conversion

        var service = new ConversionService(new ConversionLogger(tmp.Sub("logs")), "test");
        var outcome = service.ConvertFile(input, dest);

        Assert.False(outcome.Success);
        Assert.Contains("no longer available", outcome.UserMessage);
        Assert.True(File.Exists(input)); // source untouched
    }
}
