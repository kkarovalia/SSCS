namespace EdiToSscs.Core;

/// <summary>Stable reason codes for the PDF input path.</summary>
public static class PdfReasonCodes
{
    public const string NotAPdf = "PDF_NOT_A_PDF";
    public const string NoText = "PDF_NO_TEXT_LAYER";
    public const string NoInvoiceNumber = "PDF_NO_INVOICE_NUMBER";
    public const string NoDate = "PDF_NO_DATE";
    public const string NoTotal = "PDF_NO_TOTAL";
    public const string NoLineItems = "PDF_NO_LINE_ITEMS";
    public const string TotalMismatch = "PDF_TOTAL_MISMATCH";
    public const string LineMathMismatch = "PDF_LINE_MATH_MISMATCH";
    public const string UpcCheckDigit = "PDF_UPC_CHECK_DIGIT";
    /// <summary>
    /// Ink or scan damage is sitting on a figure that decides an item, a quantity or a price, so the
    /// row cannot be read. Distinct from <see cref="TotalMismatch"/>, which reports the symptom (the
    /// money no longer adds up) rather than the line to go and look at.
    /// </summary>
    public const string UnreadableLine = "PDF_UNREADABLE_LINE";
    /// <summary>The parsed quantities do not match the unit count the invoice prints for itself.</summary>
    public const string QtyMismatch = "PDF_QTY_MISMATCH";
    /// <summary>A barcode neither read nor known, so the operator had to be asked and was not.</summary>
    public const string BarcodeRequired = "PDF_BARCODE_REQUIRED";
    /// <summary>A line the vendor billed without a barcode — a credit, or a mixed display shipper.</summary>
    public const string NoBarcode = "PDF_NO_BARCODE";
    /// <summary>
    /// A barcode that reads cleanly and passes its own check digit, but got fewer than the three
    /// independent AI reads it is meant to get, because the extra passes could not run.
    /// </summary>
    public const string BarcodeUnconfirmed = "PDF_BARCODE_UNCONFIRMED";
    /// <summary>The invoice carries returned goods, sent as negative quantities.</summary>
    public const string CreditLines = "PDF_CREDIT_LINES";
    public const string VendorRequired = "PDF_VENDOR_REQUIRED";
    public const string VendorNoParser = "PDF_VENDOR_NO_PARSER";
    public const string VendorMismatch = "VENDOR_MISMATCH";
}

/// <summary>
/// One product line, ready for <see cref="NaxmlBuilder"/>.
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// WHAT SSCS DOES WITH <see cref="Gtin"/>  (verified in live SSCS, 2026-07-25 — read this
/// before touching barcode handling)
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// SSCS stores the GTIN **minus its last character** as the item's ID/PLU. It does that itself,
/// on import — we must NOT pre-strip anything.
///
///   sent 600609100271  (12-digit UPC-A)  → SSCS ID/PLU 60060910027   (11 chars)
///   sent 6917673180912 (13-digit EAN-13) → SSCS ID/PLU 691767318091  (12 chars)
///
/// Two consequences that matter:
///  1. A 12-digit UPC-A lands on exactly the same 11-character ID the retired fixed-width EDI
///     output produced, so switching a vendor from EDI to XML never creates duplicate items.
///  2. A 13-digit EAN-13 lands on a 12-character ID — which the old 11-character EDI field
///     could not hold at all. That is the whole reason this project moved to XML.
///
/// So: always carry the FULL barcode here, check digit included. When a source document only
/// gives the check-digit-less body (the retired 11-character EDI code field did), recompute the
/// check digit with <see cref="Upc.AppendCheckDigit"/> before putting it here.
/// </summary>
public sealed class InvoiceLine
{
    /// <summary>Full product barcode including its check digit (normally 12 or 13 digits).</summary>
    public required string Gtin { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// Quantity in **individual sellable units** — one pint, one ice cream sandwich. Negative on a
    /// credit/return line. A box, carton or case is NEVER a unit.
    ///
    /// Verified in live SSCS 2026-07-26: a Blue Bell line printed as `QTY 2 @ $6.48` for a
    /// twelve-bar carton is held as **24 @ $0.54**. Note that a vendor can use both meanings in one
    /// column — Blue Bell counts pints individually and `DZ` cartons by the carton on the same
    /// ticket — so what a unit is has to be settled per product, not per vendor.
    ///
    /// Nothing downstream can catch a mistake here: the line's extended amount comes from the
    /// invoice either way, so the money reconciles under either reading and only the quantity and
    /// <see cref="UnitCost"/> come out wrong. The tell is an absurd <see cref="RetailPrice"/>.
    /// </summary>
    public required int Units { get; init; }

    /// <summary>
    /// Cost of one <see cref="Units"/> unit, to 4 decimal places (SSCS's precision for this field).
    /// Divide a case/carton price down: a $6.48 dozen is $0.54 here, not $6.48.
    /// </summary>
    public required decimal UnitCost { get; init; }

    /// <summary>
    /// Each-list: what the item SELLS for, not what it costs. SSCS holds the store's own selling
    /// prices and ignores this for any item it already knows, so it only ever matters as the
    /// starting price of a brand-new item.
    ///
    /// It is still always sent. Leaving it at zero would let a new item ring up free if nobody
    /// priced it, so a vendor-suggested figure — the each-list field in an EDI file, or the
    /// wholesale-column price on a Dr Pepper invoice — is the safer default even when it is only
    /// approximate. Zero is written only when the source document genuinely gives us nothing.
    /// </summary>
    public required decimal RetailPrice { get; init; }

    /// <summary>
    /// What SSCS will show as this line's Ex Cost. Verified 16/16 against live SSCS: it rounds
    /// units × unit cost to the cent **per line**, then sums — errors do not accumulate.
    /// </summary>
    public decimal ExtendedCost => decimal.Round(Units * UnitCost, 2, MidpointRounding.AwayFromZero);
}

/// <summary>An invoice from any source (EDI, EDI13 or a vendor PDF), ready for the XML writer.</summary>
public sealed class Invoice
{
    /// <summary>
    /// Goes in the file's Supplier Name. SSCS resolves it to one of its own Vendor IDs through the
    /// "Map Supplier to Vendor" screen, which is keyed on this exact text (truncated to ~30 chars).
    /// The retired EDI path fed that same screen its 6-character vendor codes, so reusing those codes
    /// (GHRA, PEPSI, FRITOL, GSC …) means existing mappings keep working with nothing to set up.
    /// </summary>
    public required string SupplierName { get; init; }

    public required string InvoiceNumber { get; init; }

    /// <summary>
    /// Invoice date, or null when the vendor's file carries none (PEPSI headers have no date field).
    /// A null date omits the element; SSCS then falls back to the import date — verified 2026-07-25.
    /// </summary>
    public required DateOnly? Date { get; init; }

    /// <summary>Printed invoice total. Negative for a credit invoice.</summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>Where the goods went, when the source document says. SSCS ignores it — delivery is
    /// routed purely by which store's EDI email address the file is sent to (verified 2026-07-25).</summary>
    public ShipToInfo ShipTo { get; init; } = ShipToInfo.Unknown;

    /// <summary>The total SSCS will compute from the lines. Compare against <see cref="Total"/>.</summary>
    public decimal LineTotal => Lines.Sum(l => l.ExtendedCost);
}

/// <summary>Optional delivery details. Present in the file for readability only; SSCS ignores them.</summary>
public sealed record ShipToInfo(
    string Name,
    string StoreNumber,
    string Address = "",
    string City = "",
    string State = "",
    string PostalCode = "")
{
    /// <summary>Used when the source document does not identify a store, which is the normal case
    /// for EDI files. Any text works here — proven by importing a file whose store fields were
    /// literal placeholders into two different stores, each time routed correctly by email.</summary>
    public static readonly ShipToInfo Unknown = new("SSCS", "0");
}
