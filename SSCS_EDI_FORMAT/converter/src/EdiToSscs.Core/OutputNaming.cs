using System.Text.RegularExpressions;

namespace EdiToSscs.Core;

/// <summary>Builds canonical SSCS output filenames from input filenames.</summary>
public static partial class OutputNaming
{
    public const string FallbackName = "SSCS_Converted.xml";

    // One leading EDI13 / EDI / SSCS token followed by separators (space, hyphen, underscore)
    // or the end of the name. EDI13 must be listed before EDI so 'EDI13' never matches as 'EDI' + '13'.
    [GeneratedRegex(@"^(EDI13|EDI|SSCS)([ _\-]+|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTokenRegex();

    /// <summary>
    /// Normalizes an input filename to its canonical SSCS output name:
    /// strips leading EDI13/EDI/SSCS tokens (with following separators), prepends exactly one
    /// 'SSCS_', sanitizes only Windows-forbidden characters, and forces a '.xml' extension.
    /// </summary>
    public static string BuildOutputFileName(string inputFileName)
    {
        ArgumentNullException.ThrowIfNull(inputFileName);

        string name = Path.GetFileNameWithoutExtension(inputFileName);

        // Strip repeatedly so an already-converted name round-trips and the result can
        // never carry more than one leading SSCS_ prefix.
        while (true)
        {
            string stripped = LeadingTokenRegex().Replace(name, string.Empty);
            if (stripped == name) break;
            name = stripped;
        }

        if (name.Length == 0)
            return FallbackName;

        return "SSCS_" + Sanitize(name) + ".xml";
    }

    /// <summary>
    /// Names an output after its invoice number rather than its source file. Used where one input
    /// produces several invoices — a Blue Bell ticket carrying both a Sales and a Returns invoice —
    /// so the two files are told apart by the number a person can read off the paper.
    /// </summary>
    public static string BuildInvoiceOutputFileName(string invoiceNumber)
    {
        ArgumentNullException.ThrowIfNull(invoiceNumber);
        string trimmed = Sanitize(invoiceNumber.Trim()).Trim();
        return trimmed.Length == 0 ? FallbackName : "SSCS_" + trimmed + ".xml";
    }

    /// <summary>
    /// Names a Modisoft EDI file after its vendor and invoice number, e.g.
    /// <c>MODISOFT_DRPEPPER_4646106112.txt</c>. Modisoft ignores the name (confirmed by the user on
    /// 2026-10-05); it is for the person downloading the attachment, who has to pick the right one
    /// for the purchase they just created.
    /// </summary>
    public static string BuildModisoftFileName(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        string vendor = new string(invoice.SupplierName.Where(char.IsAsciiLetterOrDigit).ToArray())
            .ToUpperInvariant();
        string number = Sanitize(invoice.InvoiceNumber.Trim()).Trim();
        return string.Join("_", new[] { "MODISOFT", vendor, number }.Where(part => part.Length > 0))
               + ".txt";
    }

    /// <summary>Replaces only characters Windows forbids in filenames; all other characters are preserved.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        return new string(buffer);
    }

    /// <summary>
    /// Produces the sequence of candidate filenames used when the target already exists:
    /// 'SSCS_X.xml', 'SSCS_X (1).xml', 'SSCS_X (2).xml', ...
    /// </summary>
    public static string CandidateName(string outputFileName, int attempt)
    {
        if (attempt == 0) return outputFileName;
        string stem = Path.GetFileNameWithoutExtension(outputFileName);
        string ext = Path.GetExtension(outputFileName);
        return $"{stem} ({attempt}){ext}";
    }
}
