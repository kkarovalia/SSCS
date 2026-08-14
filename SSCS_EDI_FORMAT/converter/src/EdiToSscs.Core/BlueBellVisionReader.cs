namespace EdiToSscs.Core;

/// <summary>Why a Blue Bell picture read did or did not produce a ticket.</summary>
public enum BlueBellVisionStatus
{
    Success,
    /// <summary>No key, no network, exhausted quota, or the remote service did not answer.</summary>
    Unavailable,
    /// <summary>The service reached the pictures but found no row Core could post.</summary>
    Unreadable,
}

/// <summary>
/// One Blue Bell row copied from the physical print. Every printed number stays text until Core
/// parses it, including pack size and the fractional quantity Blue Bell can print on a return.
/// </summary>
public sealed record BlueBellVisionRow
{
    public string Page { get; init; } = "";
    public string ProductNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Upc { get; init; }
    public string? Quantity { get; init; }
    public string? Price { get; init; }
    public string? Extended { get; init; }
    public string? PackSize { get; init; }
    public bool? IsCredit { get; init; }
}

/// <summary>
/// The independently printed checks for one Sales or Returns block. A tall scan can carry both
/// blocks on the same physical page, so page and direction together identify the scope.
/// </summary>
public sealed record BlueBellVisionPageSummary
{
    public string Page { get; init; } = "";
    public bool? IsCredit { get; init; }
    public string? TotalQuantity { get; init; }
    public string? TotalAmount { get; init; }
    public string? UnitsReturned { get; init; }
    public string? BarsReturned { get; init; }
    public string? BalanceDue { get; init; }
}

/// <summary>The complete Blue Bell ticket read once from all supplied page pictures.</summary>
public sealed record BlueBellVisionReading
{
    public required BlueBellVisionStatus Status { get; init; }
    public IReadOnlyList<BlueBellVisionRow> Rows { get; init; } = [];
    public IReadOnlyList<BlueBellVisionPageSummary> PageSummaries { get; init; } = [];
    public string Note { get; init; } = "";

    public static BlueBellVisionReading Unavailable(string note = "") =>
        new() { Status = BlueBellVisionStatus.Unavailable, Note = note };

    public static BlueBellVisionReading Unreadable(string note = "") =>
        new() { Status = BlueBellVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Blue Bell's complete AI contract: one request reads every page, then Core applies the vendor's
/// barcode, carton, return and retail rules and writes one payable.
/// </summary>
public interface IBlueBellVisionReader
{
    BlueBellVisionReading ReadBlueBell(byte[] pdfBytes);
}
