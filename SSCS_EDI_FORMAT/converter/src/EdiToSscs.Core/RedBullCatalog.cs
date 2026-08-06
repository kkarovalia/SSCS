namespace EdiToSscs.Core;

/// <summary>
/// What Red Bull's own tickets have told us about each product: its barcode, its case pack, and the
/// item number printed in the first column.
///
/// Why this exists. Red Bull's barcode carries a real UPC-A check digit, which is a genuine error
/// detector — unlike Bimbo, where nothing on the page validates itself. But a check digit only says
/// *that* a reading is wrong, not what it should have been: for a code whose prefix is already
/// right, changing any one of the last six digits can restore the check digit, so a damaged reading
/// has six equally valid repairs. Picking between them is all this catalogue does. Every entry was
/// derived from sample rows whose code passed its own check digit, so the table is built out of
/// evidence that validated itself rather than out of anybody's judgement.
///
/// It is deliberately small and deliberately not a mapping table: a product it has never seen is
/// asked about, never guessed. See analysis/vendors/RED_BULL.md §3.
/// </summary>
public static class RedBullCatalog
{
    /// <summary>Red Bull's GS1 company prefix. Every barcode on these tickets begins with it.</summary>
    public const string Prefix = "611269";

    /// <summary>One catalogued product.</summary>
    /// <param name="Gtin">Full 12-digit UPC-A, check digit included.</param>
    /// <param name="Pack">Individual units in one case — what the UNITS column shows for QTY 1.</param>
    /// <param name="ItemNumber">Red Bull's own product number, without its "RB" prefix.</param>
    /// <param name="Description">A clean description, for prompts and for brand-new SSCS items.</param>
    public sealed record Entry(string Gtin, int Pack, string ItemNumber, string Description);

    // Sorted by barcode. "seen Nx" records how many sample rows carried this code cleanly; a product
    // seen once is still usable because the check digit — not repetition — is what vouches for it.
    private static readonly Entry[] Products =
    [
        new("611269001303", 24, "240073",  "RB AMBER 12OZ LS"),      // seen 3x
        new("611269001648", 24, "242607",  "RB SEA BLUE 12OZ LS"),   // seen 1x
        new("611269001846", 24, "248896",  "ICED 12OZ"),             // seen 2x
        new("611269002140", 24, "243201",  "SF AMBER 12OZ LS"),      // seen 1x
        new("611269002263", 24, "246003",  "SF SEA BLUE 12OZ LS"),   // seen 1x
        new("611269002447", 24, "248897",  "SF ICED 12OZ"),          // seen 2x
        new("611269002706", 24, "249731",  "W PEACH 12OZ"),          // seen 5x
        new("611269002768", 24, "249729",  "SF W PEACH 12OZ"),       // seen 2x
        new("611269002867", 24, "246729",  "WNTR FUJAPP 12OZ LS"),   // seen 2x
        new("611269003048", 24, "247052",  "SF PINK 12OZ LS"),       // seen 1x
        new("611269003123", 24, "247584",  "PINK 12OZ LS"),          // seen 4x
        new("611269003642", 24, "249873",  "SUMMER LIME 12OZ"),      // seen 2x
        new("611269101713", 24, "2746",    "SUGARFREE 8.4OZ LS"),    // seen 3x
        new("611269113570", 24, "203753",  "YELLOW 12OZ LS"),        // seen 2x
        new("611269133448", 12, "36463",   "RED BULL 20OZ LS"),      // seen 7x
        new("611269133455", 12, "36464",   "SUGARFREE 20OZ LS"),     // seen 1x
        new("611269182460", 24, "203752",  "BLUE 12OZ LS"),          // seen 1x
        new("611269321210", 24, "221027",  "COCONUT 12OZ LS"),       // seen 6x
        new("611269332827", 24, "234435",  "RED 12OZ LS"),           // seen 4x
        new("611269546019", 12, "33049",   "RED BULL 16OZ LS"),      // seen 2x
        new("611269716467", 24, "4817",    "SUGARFREE 12OZ LS"),     // seen 6x
        new("611269818994", 24, "4816",    "RED BULL 12OZ LS"),      // seen 2x
        new("611269991000", 24, "1718",    "RED BULL 8.4OZ LS"),     // seen 4x
    ];

    private static readonly Dictionary<string, Entry> ByGtin =
        Products.ToDictionary(p => p.Gtin, StringComparer.Ordinal);

    /// <summary>
    /// Item numbers that identify exactly one product. Red Bull prints "RB4816" and the scanner
    /// turns the letters into digits ("984816", "O84816", "1104816"), so the number is matched as a
    /// *suffix* of whatever the scan produced. A suffix shared by two products is left out — it
    /// would point at both.
    /// </summary>
    private static readonly Dictionary<string, Entry> ByItemNumber = BuildItemNumberIndex();

    private static Dictionary<string, Entry> BuildItemNumberIndex()
    {
        var byNumber = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var p in Products)
        {
            if (!byNumber.TryGetValue(p.ItemNumber, out var list))
                byNumber[p.ItemNumber] = list = [];
            list.Add(p);
        }
        return byNumber.Where(kv => kv.Value.Count == 1)
                       .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.Ordinal);
    }

    public static int Count => Products.Length;

    public static IReadOnlyList<Entry> All => Products;

    /// <summary>The catalogued product for a barcode, or null.</summary>
    public static Entry? Find(string? gtin) =>
        gtin is not null && ByGtin.TryGetValue(gtin, out var e) ? e : null;

    /// <summary>
    /// The product whose item number is a suffix of <paramref name="scannedDigits"/>, longest first.
    /// Four digits is the shortest number Red Bull prints, and also the shortest that is worth
    /// trusting — below that the scanner's rendering of "RB" supplies enough digits to match by luck.
    /// </summary>
    public static Entry? FindByItemNumber(string? scannedDigits)
    {
        if (string.IsNullOrEmpty(scannedDigits)) return null;
        for (int len = Math.Min(scannedDigits.Length, 7); len >= 4; len--)
            if (ByItemNumber.TryGetValue(scannedDigits[^len..], out var e))
                return e;
        return null;
    }
}
