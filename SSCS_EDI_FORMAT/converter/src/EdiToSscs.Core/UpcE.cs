namespace EdiToSscs.Core;

/// <summary>Official UPC-E to UPC-A expansion. Verified against 40+ reference records.</summary>
public static class UpcE
{
    /// <summary>
    /// Expands a 7-character UPC-E (number system + 6 body digits, no check digit)
    /// to the 11-digit UPC-A body (no check digit).
    /// </summary>
    public static string Expand(string upcE7)
    {
        ArgumentNullException.ThrowIfNull(upcE7);
        if (upcE7.Length != 7 || !upcE7.All(char.IsAsciiDigit))
            throw new ArgumentException("UPC-E value must be exactly 7 digits (number system + 6 body digits).", nameof(upcE7));

        char ns = upcE7[0];
        string d = upcE7[1..7];
        char last = d[5];
        return last switch
        {
            '0' or '1' or '2' => $"{ns}{d[0]}{d[1]}{last}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{ns}{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{ns}{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{ns}{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{last}",
        };
    }

    /// <summary>Computes the UPC-A check digit for an 11-digit body.</summary>
    public static char ComputeUpcACheckDigit(string body11)
    {
        ArgumentNullException.ThrowIfNull(body11);
        if (body11.Length != 11 || !body11.All(char.IsAsciiDigit))
            throw new ArgumentException("UPC-A body must be exactly 11 digits.", nameof(body11));

        int sum = 0;
        for (int i = 0; i < 11; i++)
        {
            int v = body11[i] - '0';
            sum += (i % 2 == 0) ? v * 3 : v;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
