using System.Globalization;
using System.Text;
using EdiToSscs.Core;

// Turns a hand-checked table of invoice lines into the EDI file Modisoft imports with its
// "Xpress Wholesale" option. For any invoice the converter has no reader for: an AI or a person
// reads the paper, fills the table, and this checks the arithmetic and writes the file with the
// converter's own writer. See analysis/INVOICE_TO_MODISOFT.md for the whole procedure.
//
//   dotnet run --project tools/ModisoftFile -- <table.tsv> --invoice 424753 --date 2026-10-06
//       --total 9357.09 --cases 327 --out "C:\Users\...\Downloads\MODISOFT_VENDOR_424753.txt"
//
// The table is tab-separated with a header row:
//   upc  description  cases  units_per_case  case_cost  line_amount  note
// line_amount and note may be blank. Lines with 0 cases are skipped (not delivered).

var inv = CultureInfo.InvariantCulture;
if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
    return Fail("Usage: ModisoftFile <table.tsv> --invoice N --date yyyy-MM-dd --total 0.00 --cases N --out file.txt");

string table = args[0];
string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string invoice = Opt("--invoice") ?? "";
string? outPath = Opt("--out");
if (invoice.Length == 0 || outPath is null) return Fail("--invoice and --out are required.");
DateOnly date = Opt("--date") is string d ? DateOnly.ParseExact(d, "yyyy-MM-dd", inv) : DateOnly.FromDateTime(DateTime.Today);
decimal? printedTotal = Opt("--total") is string t ? decimal.Parse(t, inv) : null;
int? printedCases = Opt("--cases") is string c ? int.Parse(c, inv) : null;

var rows = File.ReadAllLines(table).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')).ToList();
string[] header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
int Col(string name)
{
    int i = Array.IndexOf(header, name);
    if (i < 0) throw new InvalidOperationException($"The table has no '{name}' column.");
    return i;
}
int cUpc = Col("upc"), cDesc = Col("description"), cCases = Col("cases"), cUnits = Col("units_per_case"),
    cCost = Col("case_cost"), cAmount = Col("line_amount");

var lines = new List<GhraLine>();
var errors = new List<string>();
var warnings = new List<string>();
var seen = new Dictionary<string, int>();
int skipped = 0;

for (int r = 1; r < rows.Count; r++)
{
    string[] f = rows[r];
    string Cell(int i) => i < f.Length ? f[i].Trim() : "";
    int lineNo = r + 1;
    string upc = Cell(cUpc);
    // Fixed-width ASCII: one stray character would push every field after it out of place.
    string desc = new string(Cell(cDesc).Select(ch => ch is >= ' ' and <= '~' ? ch : ' ').ToArray());

    if (!int.TryParse(Cell(cCases), NumberStyles.AllowLeadingSign, inv, out int cases)) { errors.Add($"row {lineNo}: cases '{Cell(cCases)}' is not a whole number"); continue; }
    if (cases == 0) { skipped++; continue; }
    if (!int.TryParse(Cell(cUnits), NumberStyles.None, inv, out int units) || units < 1) { errors.Add($"row {lineNo} ({desc}): units_per_case must be 1 or more"); continue; }
    if (!decimal.TryParse(Cell(cCost), NumberStyles.AllowDecimalPoint, inv, out decimal caseCost)) { errors.Add($"row {lineNo} ({desc}): case_cost '{Cell(cCost)}' is not a price"); continue; }
    if (Cell(cAmount).Length > 0)
    {
        decimal amount = decimal.Parse(Cell(cAmount), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, inv);
        if (cases * caseCost != amount)
            errors.Add($"row {lineNo} ({desc}): {cases} x {caseCost:0.00} = {cases * caseCost:0.00}, but the paper says {amount:0.00}");
    }

    if (upc.Length == 0 || !upc.All(char.IsAsciiDigit)) errors.Add($"row {lineNo} ({desc}): UPC '{upc}' must be digits only");
    else if (!CheckDigitOk(upc)) warnings.Add($"row {lineNo} ({desc}): UPC {upc} fails its check digit - re-read it");
    if (seen.TryGetValue(upc, out int first)) warnings.Add($"row {lineNo} ({desc}): same UPC as row {first}");
    else seen[upc] = lineNo;

    // Retail stays 0: the paper has no shelf price, and Modisoft offers to copy the file's retail
    // onto every item. The item number stays blank (see ModisoftEdiBuilder).
    lines.Add(new GhraLine(upc, desc, "", caseCost, units, cases, 0m));
}

decimal total = lines.Sum(l => l.Amount);
int totalCases = lines.Sum(l => l.Cases);
if (printedTotal is decimal pt && pt != total) errors.Add($"lines add up to {total:0.00}, the invoice says {pt:0.00}");
if (printedCases is int pc && pc != totalCases) errors.Add($"lines add up to {totalCases} cases, the invoice says {pc}");

foreach (string w in warnings) Console.WriteLine("warning: " + w);
if (errors.Count > 0)
{
    foreach (string e in errors) Console.Error.WriteLine("ERROR: " + e);
    return Fail("No file written. Fix the table and run again.");
}

byte[] bytes = ModisoftEdiBuilder.Write(invoice, date, lines);
File.WriteAllBytes(outPath, bytes);
Console.WriteLine($"Wrote {outPath}");
Console.WriteLine($"{lines.Count} lines, {totalCases} cases, {total:0.00} total" + (skipped > 0 ? $" ({skipped} lines with 0 cases left out)" : ""));
Console.WriteLine("In Modisoft: create the purchase, Import Invoice -> EDI -> Xpress Wholesale, upload this file.");
return 0;

static bool CheckDigitOk(string code)
{
    if (code.Length is not (8 or 12 or 13 or 14)) return false;
    int sum = 0;
    for (int i = code.Length - 2, weight = 3; i >= 0; i--, weight = 4 - weight)
        sum += (code[i] - '0') * weight;
    return (10 - sum % 10) % 10 == code[^1] - '0';
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
