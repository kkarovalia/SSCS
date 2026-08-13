namespace EdiToSscs.Core;

/// <summary>Why a Bimbo picture read did or did not produce an invoice.</summary>
public enum BimboVisionStatus
{
    Success,
    /// <summary>No key, no network, exhausted quota, or the remote service did not answer.</summary>
    Unavailable,
    /// <summary>The page was reached but its product rows or independent totals were not readable.</summary>
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
/// A row locator offered to the second barcode read. The primary UPC is deliberately absent so the
/// second request cannot merely repeat it. RowId is generated locally and is the only value copied
/// back from the response without interpretation.
/// </summary>
public sealed record BimboBarcodeTarget(
    string RowId,
    int Page,
    int Position,
    string ItemNumber,
    string Description,
    string Units,
    string Amount,
    int LineNumber = 0,
    bool PageUpsideDown = false);

public sealed record BimboBarcodeRow(string RowId, string? Barcode);

public sealed record BimboBarcodeVerification
{
    public required BimboVisionStatus Status { get; init; }
    public IReadOnlyList<BimboBarcodeRow> Rows { get; init; } = [];
    public string Note { get; init; } = "";

    public static BimboBarcodeVerification Unavailable(string note = "") =>
        new() { Status = BimboVisionStatus.Unavailable, Note = note };

    public static BimboBarcodeVerification Unreadable(string note = "") =>
        new() { Status = BimboVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Bimbo's vision-primary contract. It is separate from <see cref="IInvoiceVisionReader"/> so the
/// shipped Dr Pepper and Red Bull prompts and response bodies cannot drift while this vendor moves
/// from OCR-confirmation to picture-first conversion.
/// </summary>
public interface IBimboVisionReader
{
    BimboVisionReading ReadBimbo(byte[] pdfBytes);

    /// <summary>Reads only the UPC column, without being shown the primary UPC values.</summary>
    BimboBarcodeVerification VerifyBimboBarcodes(
        byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets);

    /// <summary>
    /// A third read made from isolated, enlarged UPC cells. The crop request is not shown the full
    /// invoice, row descriptions, item numbers, quantities, amounts, or either previous UPC.
    /// </summary>
    BimboBarcodeVerification VerifyBimboBarcodeCrops(
        byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets);
}
