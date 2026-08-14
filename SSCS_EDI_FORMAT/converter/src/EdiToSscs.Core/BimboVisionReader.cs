namespace EdiToSscs.Core;

/// <summary>Why a Bimbo picture read did or did not produce an invoice.</summary>
public enum BimboVisionStatus
{
    Success,
    /// <summary>No key, no network, exhausted quota, or the remote service did not answer.</summary>
    Unavailable,
    /// <summary>The page was reached but no postable row plus ticket total could be read.</summary>
    Unreadable,
}

/// <summary>
/// One Bimbo row copied from the page picture. Numeric fields stay as text until Core validates
/// them. That preserves failures such as the real <c>-8-</c> quantity instead of silently turning
/// them into an apparently trustworthy eight.
/// </summary>
public sealed record BimboVisionRow
{
    public string ItemNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }
    public string? Units { get; init; }
    public string? Price { get; init; }
    public string? Amount { get; init; }
    public bool? IsCredit { get; init; }
    public int Page { get; init; }
    public bool PageUpsideDown { get; init; }
}

/// <summary>The count and wholesale amount printed on one band's TOTAL row.</summary>
public sealed record BimboVisionBandTotal(string? Units, string? Amount);

/// <summary>The complete Bimbo invoice read off its page picture.</summary>
public sealed record BimboVisionReading
{
    public required BimboVisionStatus Status { get; init; }
    public IReadOnlyList<BimboVisionRow> Rows { get; init; } = [];
    public BimboVisionBandTotal? Deliveries { get; init; }
    public BimboVisionBandTotal? Returns { get; init; }
    public string? TicketUnits { get; init; }
    public string? TicketTotal { get; init; }
    public string? CashDue { get; init; }
    public string Note { get; init; } = "";

    public static BimboVisionReading Unavailable(string note = "") =>
        new() { Status = BimboVisionStatus.Unavailable, Note = note };

    public static BimboVisionReading Unreadable(string note = "") =>
        new() { Status = BimboVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Bimbo's whole AI contract: one request reads the ticket, then Core writes XML. It remains
/// separate from <see cref="IInvoiceVisionReader"/> so Bimbo changes cannot drift the established
/// Dr Pepper or Red Bull request bodies.
/// </summary>
public interface IBimboVisionReader
{
    BimboVisionReading ReadBimbo(byte[] pdfBytes);
}
