using System.Security.Cryptography;
using System.Text;

namespace EdiToSscs.Core;

/// <summary>
/// Encrypts the one secret this application stores — the sending mailbox's app password — with
/// Windows DPAPI, scoped to the current Windows user.
///
/// What this buys and what it does not is worth being exact about, because the store's PC is used
/// by staff who come and go. DPAPI means the password is not sitting in settings.json as readable
/// text, and that a copied settings.json is inert on any other PC or any other Windows account.
/// It does NOT hide the password from a program run by the same Windows user, because that user is
/// exactly who Windows will decrypt it for — no "remember my password" feature can do better than
/// that. The real containment is the account itself: a dedicated send-only mailbox whose worst case
/// is somebody sending mail as it, never reading the store's real inbox.
///
/// Both directions are total: a settings file written on another machine, hand-edited, or truncated
/// decrypts to null rather than throwing, so a damaged secret degrades to "email not configured"
/// instead of a crash on startup.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Extra entropy mixed into the DPAPI blob. This does not add secrecy — it ships in the EXE —
    /// but it does mean a blob produced by this application can only be unprotected by it.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EDI-to-SSCS-Converter/email/v1");

    /// <summary>Encrypts a secret for the current Windows user. Returns null for empty input.</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        if (!OperatingSystem.IsWindows()) return null; // DPAPI is Windows-only; this is a WinForms app
        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a secret produced by <see cref="Protect"/>. Returns null when there is nothing to
    /// decrypt or when the blob belongs to another user, another machine, or is damaged.
    /// </summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            byte[] cipher = Convert.FromBase64String(protectedBase64);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }
}
