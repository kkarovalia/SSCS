using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// The two secrets the email feature stores: the sending mailbox's app password, and the PIN that
/// gates the settings screen. Neither may be readable from settings.json, and neither may fail in a
/// way that opens the gate.
/// </summary>
public class EmailSettingsTests
{
    // ------------------------------------------------ the app password

    [Fact]
    public void ThePassword_RoundTripsThroughDpapi()
    {
        string? sealedText = SecretProtector.Protect("abcd efgh ijkl mnop");
        Assert.NotNull(sealedText);
        Assert.Equal("abcd efgh ijkl mnop", SecretProtector.Unprotect(sealedText));
    }

    [Fact]
    public void ThePassword_IsNotReadableInTheStoredForm()
    {
        string? sealedText = SecretProtector.Protect("hunter2-app-password");
        Assert.NotNull(sealedText);
        Assert.DoesNotContain("hunter2", sealedText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!")]
    [InlineData("YWJjZGVmZ2g=")] // valid Base64, but not a DPAPI blob
    public void ADamagedOrForeignSecret_DecryptsToNullRatherThanThrowing(string? stored)
    {
        // A settings.json copied from another PC or another Windows account lands here. It must
        // degrade to "email not configured", never crash the app on startup.
        Assert.Null(SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void ProtectingNothing_YieldsNothing()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Null(SecretProtector.Protect(""));
    }

    // ------------------------------------------------ the admin PIN

    [Fact]
    public void TheCorrectPin_Verifies()
    {
        Assert.True(AdminPin.Verify("4821", AdminPin.Hash("4821")));
    }

    [Theory]
    [InlineData("4822")]
    [InlineData("482")]
    [InlineData("48211")]
    [InlineData("")]
    public void AWrongPin_DoesNot(string attempt)
    {
        Assert.False(AdminPin.Verify(attempt, AdminPin.Hash("4821")));
    }

    [Fact]
    public void ThePin_IsNotStoredInTheClear()
    {
        string? hash = AdminPin.Hash("4821");
        Assert.NotNull(hash);
        Assert.DoesNotContain("4821", hash);
    }

    [Fact]
    public void TheSamePin_HashesDifferentlyEachTime()
    {
        // Distinct salts, so two stores using the same PIN do not produce a matching settings file.
        Assert.NotEqual(AdminPin.Hash("4821"), AdminPin.Hash("4821"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("210000.!!!.aGFzaA==")]
    [InlineData("210000.c2FsdA==")]
    public void AMissingOrMalformedStoredHash_RefusesEveryPin(string? stored)
    {
        // The failure mode that matters: a damaged settings file must not read as "any PIN will do".
        Assert.False(AdminPin.Verify("4821", stored));
        Assert.False(AdminPin.Verify("", stored));
        Assert.False(AdminPin.Verify("anything", stored));
    }

    // ------------------------------------------------ persistence

    private static EmailSettings FullSettings() => new()
    {
        ToAddress = "store@example.com",
        FromAddress = "converter@example.com",
        SmtpHost = "smtp.gmail.com",
        SmtpPort = 587,
        ProtectedPassword = SecretProtector.Protect("app password"),
        AdminPinHash = AdminPin.Hash("4821"),
    };

    [Fact]
    public void EmailSettings_RoundTripThroughTheSettingsFile()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));

        store.SaveEmail(FullSettings());

        var loaded = store.Load().Email;
        Assert.Equal("store@example.com", loaded.ToAddress);
        Assert.Equal("converter@example.com", loaded.FromAddress);
        Assert.Equal("smtp.gmail.com", loaded.SmtpHost);
        Assert.Equal(587, loaded.SmtpPort);
        Assert.Equal("app password", SecretProtector.Unprotect(loaded.ProtectedPassword));
        Assert.True(AdminPin.Verify("4821", loaded.AdminPinHash));
        Assert.True(loaded.IsComplete);
    }

    [Fact]
    public void SavingEmail_LeavesTheOutputFolderAlone()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));
        store.SaveOutputFolder(@"C:\Converted");

        store.SaveEmail(FullSettings());

        Assert.Equal(@"C:\Converted", store.Load().OutputFolder);
    }

    [Fact]
    public void SavingTheOutputFolder_LeavesEmailAlone()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Sub("settings"));
        store.SaveEmail(FullSettings());

        store.SaveOutputFolder(@"C:\Converted");

        Assert.Equal("store@example.com", store.Load().Email.ToAddress);
    }

    [Fact]
    public void ASettingsFileFromBeforeEmailExisted_LoadsWithEmailUnset()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"outputFolder\": \"C:\\\\Converted\"}");

        var loaded = new SettingsStore(dir).Load();

        Assert.Equal(@"C:\Converted", loaded.OutputFolder);
        Assert.NotNull(loaded.Email);
        Assert.False(loaded.Email.IsComplete);
    }

    [Fact]
    public void AnExplicitlyNullEmailSection_LoadsAsEmptyRatherThanNull()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("settings");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"outputFolder\": null, \"email\": null}");

        Assert.NotNull(new SettingsStore(dir).Load().Email);
    }

    // ------------------------------------------------ the "is it usable" rule

    [Theory]
    [InlineData(null, "from@x.com", "smtp.gmail.com", 587, "secret")]
    [InlineData("to@x.com", null, "smtp.gmail.com", 587, "secret")]
    [InlineData("to@x.com", "from@x.com", null, 587, "secret")]
    [InlineData("to@x.com", "from@x.com", "smtp.gmail.com", 0, "secret")]
    [InlineData("to@x.com", "from@x.com", "smtp.gmail.com", 70000, "secret")]
    [InlineData("to@x.com", "from@x.com", "smtp.gmail.com", 587, null)]
    public void AHalfConfiguredEmailSection_IsNotComplete(
        string? to, string? from, string? host, int port, string? password)
    {
        // Missing any one field means Send Email must say "not set up" rather than half-try.
        var settings = new EmailSettings
        {
            ToAddress = to,
            FromAddress = from,
            SmtpHost = host,
            SmtpPort = port,
            ProtectedPassword = password,
        };

        Assert.False(settings.IsComplete);
    }

    [Fact]
    public void CloningCopiesEverything_SoTheDialogCannotEditLiveSettings()
    {
        var original = FullSettings();

        var copy = original.Clone();
        copy.ToAddress = "somewhere-else@x.com";

        Assert.Equal("store@example.com", original.ToAddress);
        Assert.Equal("converter@example.com", copy.FromAddress);
        Assert.Equal(original.ProtectedPassword, copy.ProtectedPassword);
        Assert.Equal(original.AdminPinHash, copy.AdminPinHash);
        Assert.Equal(587, copy.SmtpPort);
    }

    [Fact]
    public void TheSenderReadsSettingsFreshOnEverySend_SoAChangeTakesEffectAtOnce()
    {
        var live = new EmailSettings();
        var sender = new SmtpEmailSender(() => live);
        Assert.False(sender.IsConfigured);

        live = FullSettings();
        Assert.True(sender.IsConfigured);
        Assert.Equal("store@example.com", sender.ToAddress);
    }

    [Fact]
    public void ASenderWhoseSettingsThrow_ReportsNotConfiguredRatherThanCrashing()
    {
        var sender = new SmtpEmailSender(() => throw new InvalidOperationException("settings gone"));
        Assert.False(sender.IsConfigured);
    }

    [Fact]
    public void SendingWithNoSettings_FailsWithAnExplanationRatherThanThrowing()
    {
        var sender = new SmtpEmailSender(() => new EmailSettings());
        var doc = new ConvertedDocument("SSCS_1.xml", "<a/>"u8.ToArray());

        var result = sender.Send(doc, "invoice.pdf");

        Assert.False(result.Sent);
        Assert.Contains("not set up", result.ErrorMessage);
    }

    [Fact]
    public void SendingWhenThePasswordWillNotDecrypt_SaysToEnterItAgain()
    {
        // The real-world case: settings.json copied from another PC, so DPAPI refuses it here.
        var settings = FullSettings();
        settings.ProtectedPassword = "YWJjZGVmZ2g="; // valid Base64, not our blob
        var sender = new SmtpEmailSender(() => settings);

        var result = sender.Send(new ConvertedDocument("SSCS_1.xml", "<a/>"u8.ToArray()), "invoice.pdf");

        Assert.False(result.Sent);
        Assert.Contains("Windows account", result.ErrorMessage);
    }
}
