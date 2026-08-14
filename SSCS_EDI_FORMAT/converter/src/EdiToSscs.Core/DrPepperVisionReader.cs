namespace EdiToSscs.Core;

/// <summary>Outcome of a Dr Pepper picture read.</summary>
public enum DrPepperVisionStatus
{
    Success,
    Unavailable,
    Unreadable,
}

/// <summary>
/// A Dr Pepper product row copied from the invoice picture. Every printed number remains text
/// until Core validates it. Invoice number and date are intentionally absent.
/// </summary>
public sealed record DrPepperVisionRow
{
    public string RowId { get; init; } = "";
    public string Page { get; init; } = "";
    public string Section { get; init; } = "";
    public string RowOrder { get; init; } = "";
    public string Occurrence { get; init; } = "";
    public string ItemNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }
    public string? Wholesale { get; init; }
    public string? Cases { get; init; }
    public string? Units { get; init; }
    public string? Net { get; init; }
    public string? Tax { get; init; }
    public string? Amount { get; init; }
    public bool? IsCredit { get; init; }
    public bool PageUpsideDown { get; init; }
}

/// <summary>One PACKAGE SUBTOTAL and the product rows directly above it.</summary>
public sealed record DrPepperVisionPackageSubtotal
{
    public string GroupId { get; init; } = "";
    public IReadOnlyList<string> RowIds { get; init; } = [];
    public string? Cases { get; init; }
    public string? Units { get; init; }
    public string? Amount { get; init; }
}

/// <summary>The separate SALES/CREDITS count-and-money summary near the invoice foot.</summary>
public sealed record DrPepperVisionSummary
{
    public string? SalesCases { get; init; }
    public string? SalesUnits { get; init; }
    public string? SalesAmount { get; init; }
    public string? CreditCases { get; init; }
    public string? CreditUnits { get; init; }
    public string? CreditAmount { get; init; }
}

/// <summary>The complete AI-owned Dr Pepper invoice reading.</summary>
public sealed record DrPepperVisionReading
{
    public required DrPepperVisionStatus Status { get; init; }
    public IReadOnlyList<DrPepperVisionRow> Rows { get; init; } = [];
    public IReadOnlyList<DrPepperVisionPackageSubtotal> PackageSubtotals { get; init; } = [];
    public string? TotalSaleCases { get; init; }
    public string? TotalSaleUnits { get; init; }
    public string? TotalSaleAmount { get; init; }
    public DrPepperVisionSummary? Summary { get; init; }
    public string? AmountDue { get; init; }
    public string Note { get; init; } = "";

    public static DrPepperVisionReading Unavailable(string note = "") =>
        new() { Status = DrPepperVisionStatus.Unavailable, Note = note };

    public static DrPepperVisionReading Unreadable(string note = "") =>
        new() { Status = DrPepperVisionStatus.Unreadable, Note = note };
}

/// <summary>
/// The whole Dr Pepper AI contract: one picture, one reading, no follow-up questions.
///
/// The two barcode verification passes that used to live here were removed on 2026-08-13. They
/// were copied from Bimbo, which needs them because Bimbo prints a UPC body with no check digit and
/// so cannot tell whether it was read correctly. Dr Pepper prints a whole UPC-A, which checks
/// itself for free, and on the day they were removed the check digit had caught three misreads out
/// of three while the extra passes caught nothing it had not — and turned one thirty-row ticket
/// into twenty-nine questions by sliding a row out of step. They also cost two thirds of the free
/// allowance, which is what decided whether an invoice was read by Flash or by Flash-Lite.
/// </summary>
public interface IDrPepperVisionReader
{
    DrPepperVisionReading ReadDrPepper(byte[] pdfBytes);
}
