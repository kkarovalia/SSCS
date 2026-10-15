namespace EdiToSscs.Core;

/// <summary>Why a read of an unknown vendor's invoice did or did not produce a reading.</summary>
public enum AnyInvoiceVisionStatus
{
    Success,
    /// <summary>AI reading is switched off, or no key is saved. Nothing was sent anywhere.</summary>
    Off,
    /// <summary>No network, exhausted allowance, or Google kept saying it was busy.</summary>
    Unavailable,
    /// <summary>Google reached the pictures and answered, but no product line came back.</summary>
    Unreadable,
}

/// <summary>
/// One product line of any vendor's invoice, copied off the paper. Every figure stays text until
/// <see cref="AnyInvoiceParser"/> reads it, for the same reason as every other vendor's row: a
/// damaged figure must never turn into a trustworthy-looking number on the way in.
/// </summary>
public sealed record AnyInvoiceVisionRow
{
    public string ItemNumber { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The digits printed as the product's barcode, exactly as printed (no repair, no padding).</summary>
    public string? Barcode { get; init; }

    /// <summary>What the line is billed by: the count the price is multiplied by.</summary>
    public string? Quantity { get; init; }

    /// <summary>"case" when <see cref="Quantity"/> counts cases or packs, "item" when it counts single items.</summary>
    public string? BilledBy { get; init; }

    /// <summary>How many sellable items (each carrying <see cref="Barcode"/>) one case holds.</summary>
    public string? UnitsPerCase { get; init; }

    /// <summary>"printed", "description" or "unknown": where <see cref="UnitsPerCase"/> came from.</summary>
    public string? UnitsSource { get; init; }

    /// <summary>The line's own printed count of single items, when it prints one.</summary>
    public string? TotalUnits { get; init; }

    public string? Price { get; init; }
    public string? Amount { get; init; }
    public bool? IsCredit { get; init; }
}

/// <summary>A figure that is part of the invoice total but is not a product: tax, deposit, a fee.</summary>
public sealed record AnyInvoiceCharge(string Label, string? Amount);

/// <summary>
/// One invoice from any vendor, read off the pictures in a single request, for Modisoft only.
/// </summary>
public sealed record AnyInvoiceVisionReading
{
    public required AnyInvoiceVisionStatus Status { get; init; }

    /// <summary>The supplier's name as printed at the top of the invoice. Names the file, nothing more.</summary>
    public string VendorName { get; init; } = "";

    /// <summary>
    /// The printed invoice number. Unlike every SSCS vendor's, it is read rather than typed: Modisoft
    /// never takes it from the file, so it only names the file and fills the header.
    /// </summary>
    public string? InvoiceNumber { get; init; }

    /// <summary>The printed invoice date as YYYY-MM-DD. Header only, like the number.</summary>
    public string? InvoiceDate { get; init; }

    public IReadOnlyList<AnyInvoiceVisionRow> Rows { get; init; } = [];
    public IReadOnlyList<AnyInvoiceCharge> Charges { get; init; } = [];

    /// <summary>What the invoice says is owed for this delivery, as printed.</summary>
    public string? InvoiceTotal { get; init; }

    /// <summary>The invoice's own count of what it delivered, when it prints one.</summary>
    public string? TotalQuantity { get; init; }

    public string Note { get; init; } = "";

    public static AnyInvoiceVisionReading Off(string note = "") =>
        new() { Status = AnyInvoiceVisionStatus.Off, Note = note };

    public static AnyInvoiceVisionReading Unavailable(string note = "") =>
        new() { Status = AnyInvoiceVisionStatus.Unavailable, Note = note };

    public static AnyInvoiceVisionReading Unreadable(string note = "") =>
        new() { Status = AnyInvoiceVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// Reads an invoice from any vendor — a scanned PDF, a PDF with real text, or a photo — in one
/// request. The Modisoft tab's only reader: Modisoft takes nothing but the lines from the file,
/// and the vendor is picked on the purchase in Modisoft, so the converter does not need to know who
/// sent it.
/// </summary>
public interface IAnyInvoiceVisionReader
{
    AnyInvoiceVisionReading ReadAnyInvoice(byte[] fileBytes);
}
