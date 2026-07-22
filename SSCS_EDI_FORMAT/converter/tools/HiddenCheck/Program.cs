using EdiToSscs.Core;

string root = @"C:\Users\Kashf\Desktop\SSCS_EDI_FORMAT";
string ediDir = Path.Combine(root, "EDI");
string edi13Dir = Path.Combine(root, "EDI13");
string sscsDir = Path.Combine(root, "SSCS (REAL)");

// Original + previous hidden invoices already proven; everything else is "NEW".
var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "118955","120511","125482","129348","129478","129943","131454","80471906",
    "799005","799427","802533","803109","806054","806602","809716","810377",
    "810738","813411","813871","817443","817778","821220","822056","822057",
    "824937","825293","828787","RubyRedSalesServices",
};

int pass = 0, fail = 0, warn = 0, missing = 0, newPass = 0, newFail = 0, newWarn = 0;

Console.WriteLine("========== EDI -> SSCS (REAL) ==========");
foreach (string path in Directory.GetFiles(ediDir, "*.txt").OrderBy(p => p))
{
    Report("EDI  ", path, ResolveSscs(Path.GetFileName(path)));
}

Console.WriteLine();
Console.WriteLine("========== EDI13 -> SSCS (REAL) ==========");
foreach (string path in Directory.GetFiles(edi13Dir, "*.txt").OrderBy(p => p))
{
    Report("EDI13", path, ResolveSscs(Path.GetFileName(path)));
}

Console.WriteLine();
Console.WriteLine("========== SUMMARY ==========");
Console.WriteLine($"ALL:  PASS={pass} FAIL={fail} WARN-but-match={warn} MISSING-REF={missing}");
Console.WriteLine($"NEW:  PASS={newPass} FAIL={newFail} WARN-but-match={newWarn}");

string? ResolveSscs(string inputName)
{
    string stem = Path.GetFileNameWithoutExtension(inputName);
    // EDI_GHRA13_574197 / EDI_GHRA_574197 / EDI_PEPSI-80471906 / EDI13-RubyRed...
    if (stem.StartsWith("EDI_GHRA13_", StringComparison.OrdinalIgnoreCase))
        return Path.Combine(sscsDir, "SSCS_GHRA_" + stem["EDI_GHRA13_".Length..] + ".txt");
    if (stem.StartsWith("EDI_GHRA_", StringComparison.OrdinalIgnoreCase))
        return Path.Combine(sscsDir, "SSCS_GHRA_" + stem["EDI_GHRA_".Length..] + ".txt");
    if (stem.StartsWith("EDI_PEPSI", StringComparison.OrdinalIgnoreCase))
        return Path.Combine(sscsDir, "SSCS_" + stem["EDI_".Length..] + ".txt");
    if (stem.Contains("RubyRed", StringComparison.OrdinalIgnoreCase))
        return Path.Combine(sscsDir, "EDI-RubyRedSalesServices (SSCS EXAMPLE).txt");
    return Path.Combine(sscsDir, "SSCS_" + stem + ".txt");
}

string InvoiceKey(string inputName)
{
    string stem = Path.GetFileNameWithoutExtension(inputName);
    if (stem.StartsWith("EDI_GHRA13_", StringComparison.OrdinalIgnoreCase)) return stem["EDI_GHRA13_".Length..];
    if (stem.StartsWith("EDI_GHRA_", StringComparison.OrdinalIgnoreCase)) return stem["EDI_GHRA_".Length..];
    if (stem.StartsWith("EDI_PEPSI-", StringComparison.OrdinalIgnoreCase)) return stem["EDI_PEPSI-".Length..];
    if (stem.Contains("RubyRed", StringComparison.OrdinalIgnoreCase)) return "RubyRedSalesServices";
    return stem;
}

void Report(string label, string inputPath, string? expPath)
{
    string name = Path.GetFileName(inputPath);
    string inv = InvoiceKey(name);
    bool isNew = !known.Contains(inv);
    string tag = isNew ? "NEW" : "old";

    if (expPath is null || !File.Exists(expPath))
    {
        missing++;
        Console.WriteLine($"{label} [{tag}] {name} : NO SSCS REFERENCE");
        return;
    }

    try
    {
        var result = EdiConverter.Convert(File.ReadAllBytes(inputPath));
        byte[] actual = result.OutputBytes;
        byte[] expected = File.ReadAllBytes(expPath);
        bool match = actual.AsSpan().SequenceEqual(expected);
        string w = result.HasWarnings
            ? $" warnings={result.Warnings.Count}({string.Join(",", result.Warnings.Select(x => x.ReasonCode + "@L" + x.LineNumber))})"
            : "";

        if (match)
        {
            pass++;
            if (result.HasWarnings) warn++;
            if (isNew) { newPass++; if (result.HasWarnings) newWarn++; }
            Console.WriteLine($"{label} [{tag}] {name} : IDENTICAL fmt={result.Format}{w}");
        }
        else
        {
            fail++;
            if (isNew) newFail++;
            int d = -1, m = Math.Min(actual.Length, expected.Length);
            for (int i = 0; i < m; i++) if (actual[i] != expected[i]) { d = i; break; }
            if (d < 0) d = m;
            int line = 1, ls = 0;
            for (int j = 0; j < d && j < expected.Length; j++) if (expected[j] == 10) { line++; ls = j + 1; }
            string a = d < actual.Length ? $"0x{actual[d]:X2}'{(char)actual[d]}'" : "<eof>";
            string e = d < expected.Length ? $"0x{expected[d]:X2}'{(char)expected[d]}'" : "<eof>";
            Console.WriteLine($"{label} [{tag}] {name} : DIFF off={d} line={line} col={d - ls + 1} actual={a} expected={e} lenA={actual.Length} lenE={expected.Length} fmt={result.Format}{w}");
        }
    }
    catch (EdiConversionException ex)
    {
        fail++;
        if (isNew) newFail++;
        Console.WriteLine($"{label} [{tag}] {name} : REJECTED [{ex.ReasonCode}] line {ex.LineNumber}: {ex.UserMessage}");
    }
    catch (Exception ex)
    {
        fail++;
        if (isNew) newFail++;
        Console.WriteLine($"{label} [{tag}] {name} : ERROR {ex.Message}");
    }
}
