using System.Globalization;

namespace EdiToSscs.Core;

/// <summary>
/// Reads a date the way somebody actually types one into the invoice prompt.
///
/// The box used to accept `mm-dd-yyyy` and nothing else, so `08/11/2026` — a perfectly good date,
/// and the very format the tickets print — came back as "Type the date like 12-14-2023". Nobody
/// keying twenty invoices wants to be told how to punctuate. Slashes, dashes, dots, spaces, one or
/// two digits for the month and day, two or four for the year, or the whole thing as a run of
/// digits: all of it means the same thing and all of it is accepted.
///
/// **Month first is the one thing held fixed**, and deliberately. Every vendor in this application
/// prints month first, and it is the only part of a date a person cannot check by looking: `08-11-2026`
/// is a real date read either way round, so accepting both would book the delivery into the wrong
/// month on a machine set to a day-first locale and say nothing about it. A date that is unreadable
/// as month-first is rejected and asked again, which is loud; a date silently read as the wrong month
/// is not.
/// </summary>
public static class TypedDate
{
    /// <summary>How a date is written back into a box that is offering the scan's own reading.</summary>
    public const string Format = "MM-dd-yyyy";

    /// <summary>
    /// Month, day, year in that order, however it is punctuated. Null when it cannot be read that
    /// way — which is the caller's cue to ask again rather than to guess.
    /// </summary>
    public static DateOnly? TryParse(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;

        foreach (string pattern in Separated)
            if (DateOnly.TryParseExact(
                    trimmed, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

        // Typed as one run of digits: 08112026, or 081126 for the same day.
        if (trimmed.All(char.IsAsciiDigit))
            foreach (string pattern in trimmed.Length == 8 ? Packed8 : trimmed.Length == 6 ? Packed6 : [])
                if (DateOnly.TryParseExact(
                        trimmed, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var packed))
                    return packed;

        return null;
    }

    /// <summary>
    /// Every punctuation a person reaches for. A two-digit year is this century, which is what
    /// .NET's `yy` already does and the right reading for a delivery ticket — nobody keys 1926.
    /// </summary>
    private static readonly string[] Separated =
    [
        "M-d-yyyy", "M-d-yy",
        "M/d/yyyy", "M/d/yy",
        "M.d.yyyy", "M.d.yy",
        "M d yyyy", "M d yy",
    ];

    private static readonly string[] Packed8 = ["MMddyyyy"];
    private static readonly string[] Packed6 = ["MMddyy"];
}
