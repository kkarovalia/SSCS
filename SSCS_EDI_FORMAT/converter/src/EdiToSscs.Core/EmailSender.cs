using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;

namespace EdiToSscs.Core;

/// <summary>
/// What became of one send. A failure carries the sentence shown to the operator — the caller
/// never has to interpret an SMTP status code.
/// </summary>
public sealed record EmailSendResult(bool Sent, string? ErrorMessage)
{
    public static EmailSendResult Ok() => new(true, null);
    public static EmailSendResult Failed(string message) => new(false, message);
}

/// <summary>
/// Delivers a converted invoice to the store's SSCS address.
///
/// Everything a store's routing depends on is here: SSCS decides which store a file belongs to by
/// the address it arrived at, so this is the step that actually files an invoice.
/// </summary>
public interface IEmailDelivery
{
    /// <summary>True when email is fully configured. False means the Send button has nothing to do.</summary>
    bool IsConfigured { get; }

    /// <summary>The address files are being sent to, for messages and the Settings display.</summary>
    string? ToAddress { get; }

    /// <summary>
    /// Sends one invoice as one message. Never throws: a failure comes back as
    /// <see cref="EmailSendResult.ErrorMessage"/>.
    /// </summary>
    EmailSendResult Send(ConvertedDocument document, string sourceFileName);
}

/// <summary>
/// Sends over SMTP with the credentials in <see cref="EmailSettings"/>, reading them afresh on every
/// send so a change in Settings takes effect without restarting.
///
/// <b>One invoice per message.</b> A Blue Bell ticket carrying both a Sales and a Returns invoice
/// therefore produces two emails, each with its own attachment — which is what the store asked for,
/// and it also means one invoice failing to send never takes a good one down with it.
///
/// The attachment is built from a <see cref="MemoryStream"/> over the converted bytes rather than
/// read back off disk, so what is sent is provably the same bytes the conversion produced.
/// </summary>
public sealed class SmtpEmailSender : IEmailDelivery
{
    private readonly Func<EmailSettings> _settings;
    private readonly TimeSpan _timeout;

    public SmtpEmailSender(Func<EmailSettings> settingsProvider, TimeSpan? timeout = null)
    {
        _settings = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        // Long enough for a slow shop connection, short enough that a dead network does not look
        // like a hung application.
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
    }

    private EmailSettings Current
    {
        get
        {
            try { return _settings() ?? new EmailSettings(); }
            catch { return new EmailSettings(); }
        }
    }

    public bool IsConfigured => Current.IsComplete;

    public string? ToAddress => Current.ToAddress;

    public EmailSendResult Send(ConvertedDocument document, string sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(document);

        var settings = Current;
        if (!settings.IsComplete)
            return EmailSendResult.Failed("Email is not set up yet. Open Settings and fill in the email details.");

        string? password = SecretProtector.Unprotect(settings.ProtectedPassword);
        if (string.IsNullOrEmpty(password))
        {
            // The saved blob will not decrypt: almost always a settings file copied from another PC
            // or another Windows account, since DPAPI ties it to the one that saved it.
            return EmailSendResult.Failed(
                "The saved email password could not be read on this Windows account. Open Settings and enter the app password again.");
        }

        return SendCore(settings, password, document, sourceFileName, isTest: false, timeout: _timeout);
    }

    /// <summary>
    /// Sends a one-off message proving the settings work, used by the Send Test Email button. Takes
    /// the password directly so it can be tested before it is saved.
    /// </summary>
    public static EmailSendResult SendTest(EmailSettings settings, string password, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var doc = new ConvertedDocument("SSCS_test.xml",
            System.Text.Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Test>EDI to SSCS Converter test message.</Test>\r\n"));
        return SendCore(settings, password, doc, "test", isTest: true, timeout: timeout);
    }

    private static EmailSendResult SendCore(
        EmailSettings settings, string password, ConvertedDocument document,
        string sourceFileName, bool isTest, TimeSpan? timeout)
    {
        MemoryStream? stream = null;
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress!),
                Subject = isTest ? "EDI to SSCS Converter - test message" : $"SSCS invoice {document.FileName}",
                Body = BuildBody(document, sourceFileName, isTest),
                IsBodyHtml = false,
            };
            message.To.Add(settings.ToAddress!);

            stream = new MemoryStream(document.Bytes, writable: false);
            message.Attachments.Add(new Attachment(stream, document.FileName, MediaTypeNames.Text.Xml));

            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = true, // STARTTLS on 587; Gmail refuses the session without it
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(settings.FromAddress, password),
                Timeout = (int)(timeout ?? TimeSpan.FromSeconds(45)).TotalMilliseconds,
            };

            client.Send(message);
            return EmailSendResult.Ok();
        }
        catch (Exception ex)
        {
            return EmailSendResult.Failed(Describe(ex, settings));
        }
        finally
        {
            // The MailMessage owns its attachment and disposes the stream with it; disposing again is
            // harmless and covers the paths that threw before the attachment was added.
            try { stream?.Dispose(); } catch { /* best effort */ }
        }
    }

    private static string BuildBody(ConvertedDocument document, string sourceFileName, bool isTest)
    {
        if (isTest)
        {
            return "This is a test message from the EDI to SSCS Converter.\r\n\r\n"
                 + "If you are reading this, the converter can send converted invoices to this address.\r\n";
        }

        return $"Converted from: {sourceFileName}\r\n\r\n"
             + $"Attached SSCS NAXML file:\r\n  - {document.FileName}\r\n\r\n"
             + "Sent automatically by the EDI to SSCS Converter.\r\n";
    }

    /// <summary>
    /// Turns whatever the mail stack threw into one sentence a person at the register can act on.
    /// The Gmail sign-in failure gets its own wording because it is the one people actually hit:
    /// a normal account password is rejected there, and only a 16-character App Password works.
    /// </summary>
    private static string Describe(Exception ex, EmailSettings settings) => ex switch
    {
        ArgumentException or FormatException =>
            "The email addresses in Settings are not valid. Check the addresses and try again.",

        SmtpFailedRecipientException recipient =>
            $"The mail server would not accept the address {recipient.FailedRecipient}. Check the address in Settings.",

        SmtpException smtp when smtp.StatusCode is SmtpStatusCode.MustIssueStartTlsFirst
            or SmtpStatusCode.ClientNotPermitted
            || LooksLikeAuthFailure(smtp) =>
            $"{Provider(settings)} would not accept the sign-in for {settings.FromAddress}. "
            + "Check the address, and make sure the password is a 16-character App Password rather than the normal account password.",

        SmtpException smtp when smtp.InnerException is SocketException =>
            $"Could not reach the mail server {settings.SmtpHost}:{settings.SmtpPort}. Check the internet connection and the server details in Settings.",

        SmtpException smtp =>
            $"The mail server refused the message: {smtp.Message.TrimEnd('.')}.",

        SocketException =>
            $"Could not reach the mail server {settings.SmtpHost}:{settings.SmtpPort}. Check the internet connection and the server details in Settings.",

        InvalidOperationException =>
            "The email settings are incomplete. Open Settings and check the email details.",

        _ => $"The email could not be sent ({ex.GetType().Name}).",
    };

    /// <summary>
    /// SmtpClient reports a rejected sign-in in more than one shape depending on where in the
    /// handshake it happened, so the message text is checked as well as the status code.
    /// </summary>
    private static bool LooksLikeAuthFailure(SmtpException ex)
    {
        if (ex.StatusCode is SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.TransactionFailed
            or SmtpStatusCode.GeneralFailure)
        {
            string text = ex.Message + " " + (ex.InnerException?.Message ?? "");
            return text.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
                || text.Contains("5.7.8", StringComparison.Ordinal)
                || text.Contains("5.7.0", StringComparison.Ordinal)
                || text.Contains("535", StringComparison.Ordinal);
        }
        return ex.StatusCode == SmtpStatusCode.ClientNotPermitted;
    }

    private static string Provider(EmailSettings settings) =>
        (settings.SmtpHost ?? "").Contains("gmail", StringComparison.OrdinalIgnoreCase) ? "Gmail" : "The mail server";
}
