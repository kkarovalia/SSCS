using System.Globalization;
using System.Text;

namespace EdiToSscs.Core;

/// <summary>
/// Writes an <see cref="Invoice"/> as an NAXML-BusDoc 1.5 invoice — the format SSCS accepts by
/// email and the only output this converter produces.
///
/// The element shape reproduces a file VM Novelties sends to SSCS in production, which was read
/// back out of live SSCS line-by-line and matched exactly. Everything here is therefore copied
/// from something known to work, not inferred from the NAXML standard.
///
/// Delivery: SSCS gives every store its own EDI email address and routes purely on which address
/// the file arrives at (verified 2026-07-25 — a file carrying store 19491 emailed to store 14's
/// address landed in store 14). Nothing inside the file selects a store.
/// </summary>
public static class NaxmlBuilder
{
    /// <summary>
    /// Supplier-side identifier copied verbatim from VM's working file. It is not a per-vendor
    /// value — SSCS resolves the vendor from the Supplier Name text — so it stays constant.
    /// </summary>
    private const string SupplierIdent = "otrack";

    /// <summary>Buyer-side identifier, likewise copied verbatim from the working file.</summary>
    private const string BuyerIdent = "sscs1";

    /// <summary>Assembles the complete XML document bytes for an invoice.</summary>
    public static byte[] Build(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var sb = new StringBuilder(1024 + invoice.Lines.Count * 512);
        void Line(string s) => sb.Append(s).Append("\r\n");

        Line("<?xml version=\"1.0\" ?>");
        Line("<NAXML-BusDoc version=\"1.5\">");
        Line("  <Parties>");
        Line("    <Supplier>");
        Line($"      <Name identType=\"CUSTOM\" ident=\"{SupplierIdent}\">{Text(invoice.SupplierName)}</Name>");
        Line("    </Supplier>");
        Line("    <Buyer>");
        Line($"      <Name identType=\"CUSTOM\" ident=\"{BuyerIdent}\">{Text(invoice.ShipTo.Name)}</Name>");
        Line("    </Buyer>");
        Line("    <ShipTo>");
        Line($"      <Name ident=\"{Text(invoice.ShipTo.StoreNumber)}\">{Text(invoice.ShipTo.Name)}</Name>");
        Line("    </ShipTo>");
        Line("  </Parties>");
        Line("  <Invoice>");
        Line("    <Supplier>");
        Line($"      <Name identType=\"CUSTOM\" ident=\"{SupplierIdent}\">{Text(invoice.SupplierName)}</Name>");
        Line("    </Supplier>");
        Line("    <Location>");
        Line("      <Name>No name</Name>");
        Line($"      <Address>{Text(invoice.ShipTo.Address)}</Address>");
        Line($"      <City>{Text(invoice.ShipTo.City)}</City>");
        Line($"      <State>{Text(invoice.ShipTo.State)}</State>");
        Line($"      <PostalCode>{Text(invoice.ShipTo.PostalCode)}</PostalCode>");
        Line("      <OrganizationId identType=\"Store Number\" assignedBy=\"Seller\">"
             + Text(invoice.ShipTo.StoreNumber) + "</OrganizationId>");
        Line("    </Location>");
        Line($"    <InvoiceNumber>{Text(invoice.InvoiceNumber)}</InvoiceNumber>");

        // A missing date is left out entirely rather than guessed. SSCS then dates the invoice on
        // the day it imports it (verified 2026-07-25). Only PEPSI-style headers, which carry no
        // date field at all, reach this branch; the reader records a warning when they do.
        if (invoice.Date is DateOnly d)
            Line($"    <InvoiceDate>{d:yyyy-MM-dd}</InvoiceDate>");

        Line("    <InvoiceDetail>");

        int count = 0;
        foreach (var li in invoice.Lines)
        {
            count++;
            Line($"      <LineItem count=\"{count}\">");
            Line("        <InvoiceUnit>");
            Line($"          <InvoiceUnitId identType=\"GTIN\">{Text(li.Gtin)}</InvoiceUnitId>");
            Line($"          <InvoiceUnitDescription>{Text(li.Description)}</InvoiceUnitDescription>");
            Line($"          <InvoiceUnitQty cstoreUOMBasis=\"EA\">{li.Units.ToString(CultureInfo.InvariantCulture)}</InvoiceUnitQty>");
            Line($"          <InvoiceUnitCost>{Money(li.UnitCost, 4)}</InvoiceUnitCost>");
            Line("        </InvoiceUnit>");
            Line("        <RetailUnitPricing>");
            Line($"          <RetailUnitId identType=\"GTIN\">{Text(li.Gtin)}</RetailUnitId>");
            Line("          <RetailUnitQty identType=\"GTIN\">1</RetailUnitQty>");
            Line($"          <RetailPrice>{Money(li.RetailPrice, 2)}</RetailPrice>");
            Line("        </RetailUnitPricing>");
            Line("      </LineItem>");
        }

        Line("      <InvoiceSummary>");
        Line("        <InvoiceTotals>");
        Line($"          <TotalLineItemNetAmt identType=\"Debit\">{Money(invoice.Total, 2)}</TotalLineItemNetAmt>");
        Line($"          <TotalInvoiceDueAmt identType=\"Debit\">{Money(invoice.Total, 2)}</TotalInvoiceDueAmt>");
        Line("        </InvoiceTotals>");
        Line("      </InvoiceSummary>");
        Line("    </InvoiceDetail>");
        Line("  </Invoice>");
        Line("</NAXML-BusDoc>");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Money(decimal value, int places) =>
        value.ToString(places == 4 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Makes a value safe for both element text and attribute values: XML-escapes the five special
    /// characters, and replaces anything outside printable ASCII with a space. Source documents are
    /// often OCR output and carry stray control bytes and replacement characters; the file must stay
    /// plain ASCII because that is what the working reference file is.
    /// </summary>
    internal static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    sb.Append(c is >= ' ' and <= '~' ? c : ' ');
                    break;
            }
        }

        // Collapse the runs of spaces that stripping non-ASCII can leave behind.
        string s = sb.ToString();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }
}
