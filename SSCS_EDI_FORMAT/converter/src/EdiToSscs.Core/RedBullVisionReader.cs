namespace EdiToSscs.Core;

/// <summary>Why a Red Bull picture read did or did not produce a ticket.</summary>
public enum RedBullVisionStatus
{
    Success,
    /// <summary>No key, no network, exhausted quota, or the remote service did not answer.</summary>
    Unavailable,
    /// <summary>The service reached the pictures but found no row Core could post.</summary>
    Unreadable,
}

/// <summary>
/// One Red Bull row copied from the physical print. Every printed number remains text until Core
/// parses it, so damage never turns into a trustworthy-looking numeric value on the way in.
/// </summary>
public sealed record RedBullVisionRow
{
    public string ItemNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }
    public string? Cases { get; init; }
    public string? Units { get; init; }
    public string? Price { get; init; }
    public string? Deposit { get; init; }
    public string? Discount { get; init; }
    public string? Sugar { get; init; }
    public string? Amount { get; init; }
    public bool? IsCredit { get; init; }
}

/// <summary>
/// The footer figures physically printed on one page. Keeping their page scope prevents a
/// delivery-page INVOICE subtotal from being compared with a whole multi-page payable.
/// </summary>
public sealed record RedBullVisionPageSummary
{
    public string Page { get; init; } = "";
    public string? UnitsDelivered { get; init; }
    public string? UnitsPickedUp { get; init; }
    public string? Invoice { get; init; }
    public string? Roa { get; init; }
}

/// <summary>The complete Red Bull ticket read once from all supplied page pictures.</summary>
public sealed record RedBullVisionReading
{
    public required RedBullVisionStatus Status { get; init; }
    public IReadOnlyList<RedBullVisionRow> Rows { get; init; } = [];
    public IReadOnlyList<RedBullVisionPageSummary> PageSummaries { get; init; } = [];

    /// <summary>
    /// Same-scope whole-ticket totals supplied by deterministic test or host adapters. The Gemini
    /// adapter uses <see cref="PageSummaries"/> so Core can preserve page scope itself.
    /// </summary>
    public IReadOnlyList<string?> PrintedTotals { get; init; } = [];

    /// <summary>
    /// A directly printed whole-ticket unit count when a host already has one. Gemini supplies the
    /// individual page figures above instead.
    /// </summary>
    public string? PrintedUnits { get; init; }

    public string Note { get; init; } = "";

    public static RedBullVisionReading Unavailable(string note = "") =>
        new() { Status = RedBullVisionStatus.Unavailable, Note = note };

    public static RedBullVisionReading Unreadable(string note = "") =>
        new() { Status = RedBullVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Red Bull's complete AI contract: one request reads every page, then Core writes XML. It is
/// deliberately separate from the generic second reader and from the other primary vendors.
/// </summary>
public interface IRedBullVisionReader
{
    RedBullVisionReading ReadRedBull(byte[] pdfBytes);
}
