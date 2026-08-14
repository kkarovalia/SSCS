namespace EdiToSscs.Core;

/// <summary>Why a Chubiwinos picture read did or did not produce a ticket.</summary>
public enum ChubiwinosVisionStatus
{
    Success,
    /// <summary>No key, no network, exhausted quota, or the remote service did not answer.</summary>
    Unavailable,
    /// <summary>The service reached the pictures but found no row Core could post.</summary>
    Unreadable,
}

/// <summary>
/// One Chubiwinos row copied from the physical print. Numeric fields remain text until Core parses
/// them, so brackets, signs and damaged figures survive the handoff without looking trustworthy.
/// </summary>
public sealed record ChubiwinosVisionRow
{
    public string ItemNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }
    public string? Quantity { get; init; }
    public string? Price { get; init; }
    public string? Amount { get; init; }
    public bool? IsCredit { get; init; }
}

/// <summary>The count and money printed on one SALES or DUMP section's Totals row.</summary>
public sealed record ChubiwinosVisionSectionTotal
{
    public string Section { get; init; } = "";
    public string? Quantity { get; init; }
    public string? Amount { get; init; }
}

/// <summary>The complete Chubiwinos invoice read once from all supplied page pictures.</summary>
public sealed record ChubiwinosVisionReading
{
    public required ChubiwinosVisionStatus Status { get; init; }
    public IReadOnlyList<ChubiwinosVisionRow> Rows { get; init; } = [];
    public IReadOnlyList<ChubiwinosVisionSectionTotal> SectionTotals { get; init; } = [];
    public string? Sales { get; init; }
    public string? Credits { get; init; }
    public string? NetAmount { get; init; }
    public string? SalesTax { get; init; }
    public string? TotalDue { get; init; }
    public string? TotalPayment { get; init; }
    public string? InvoiceBalance { get; init; }

    /// <summary>
    /// Chubiwinos markers the model physically saw, such as the vendor name, Route, a section
    /// banner or INVOICE BALANCE. They warn about a wrong dropdown choice but never gate the read.
    /// </summary>
    public IReadOnlyList<string> VendorMarkers { get; init; } = [];

    public string Note { get; init; } = "";

    public static ChubiwinosVisionReading Unavailable(string note = "") =>
        new() { Status = ChubiwinosVisionStatus.Unavailable, Note = note };

    public static ChubiwinosVisionReading Unreadable(string note = "") =>
        new() { Status = ChubiwinosVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Chubiwinos' complete AI contract: one request reads every page, then Core applies only the
/// vendor's barcode, credit, retail and warning rules before writing one payable.
/// </summary>
public interface IChubiwinosVisionReader
{
    ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes);
}
