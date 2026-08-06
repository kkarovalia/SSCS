using System.Security.Cryptography;
using System.Text;

namespace EdiToSscs.Core;

/// <summary>
/// The PIN that gates the email section of Settings, stored only as a PBKDF2 hash.
///
/// This is what stops a member of staff opening Settings and reading who the files go to, or
/// quietly re-pointing them somewhere else. It is deliberately not a password on the application:
/// converting and sending invoices need no PIN, so the everyday job is unchanged and nobody has a
/// reason to share it.
///
/// The stored form is <c>iterations.salt.hash</c> in Base64, and verification is constant-time, so
/// the file reveals nothing about the PIN and comparing cannot be timed. A forgotten PIN is not
/// recoverable by design — deleting settings.json clears the PIN along with the rest of the email
/// configuration.
/// </summary>
public static class AdminPin
{
    private const int Iterations = 210_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Hashes a PIN for storage. Returns null when the PIN is blank.</summary>
    public static string? Hash(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(pin, salt, Iterations);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// True when <paramref name="pin"/> matches <paramref name="storedHash"/>. A missing, blank or
    /// malformed stored hash always returns false: a settings file that lost its PIN must not read
    /// as "any PIN will do".
    /// </summary>
    public static bool Verify(string? pin, string? storedHash)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrWhiteSpace(storedHash)) return false;

        string[] parts = storedHash.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int iterations) || iterations is < 1 or > 5_000_000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length == 0 || expected.Length == 0) return false;

        byte[] actual = Derive(pin, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, length);
}
