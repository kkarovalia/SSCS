namespace EdiToSscs.Core;

/// <summary>
/// Bimbo's own product number → the UPC printed beside it, built from the nine sample tickets in
/// analysis/vendors/BIMBO.md.
///
/// WHY THIS EXISTS. Bimbo prints its UPC column with the check digit removed — "74323-09183" is the
/// eleven-digit body 07432309183 with the leading zero dropped too — so **no code on the page can
/// validate itself**. That is worse than Chubiwinos, where seven of thirty-five barcodes failed a
/// check digit and thereby announced themselves. Here a single mis-read digit is completely silent:
/// the money still reconciles to the penny, the unit counts still agree, and SSCS quietly opens a
/// brand-new item under an id that belongs to nothing.
///
/// The item number is the second, independent encoding of the same product, printed one column
/// along. Cross-checking the two columns is the only error detector this vendor offers, and it
/// works: across 170 sample rows it catches every one of the ten UPC mis-reads, four of which
/// (09183→09783, 09165→09155, 09535→09635, 73400→13400) the prefix rule alone cannot see.
///
/// HOW ENTRIES WERE DERIVED. Every (item number, UPC) pair on all nine samples was counted after
/// <see cref="BimboPdfParser"/>'s prefix repair, and the majority reading wins. Item numbers that
/// are themselves a one-digit variant of a better-attested number carrying the same UPC were
/// dropped as OCR noise ("007956" for 007966, "008775" for 008776, "006784" for 008784).
///
/// <see cref="Entry.Trusted"/> marks the 34 entries confirmed by **two or more different tickets**.
/// Only those may correct a UPC that was read off the page; the 20 seen on a single ticket can only
/// raise a warning, because a lone sighting could itself be the mis-read. Every UPC error actually
/// observed sits on a trusted entry, so nothing is lost by the restriction.
///
/// ADDING TO IT is safe and dull: put the item number and the ten digits Bimbo prints (no hyphen)
/// in the table, and mark it trusted only once two separate tickets agree.
/// </summary>
public static class BimboCatalog
{
    /// <summary>One product: the ten UPC digits as Bimbo prints them, and how far it can be trusted.</summary>
    /// <param name="Upc10">The ten digits of the UPC column, hyphen removed.</param>
    /// <param name="Trusted">True when two or more separate tickets agree, which is what licenses a repair.</param>
    public readonly record struct Entry(string Upc10, bool Trusted);

    private static readonly Dictionary<string, Entry> Items = new(StringComparer.Ordinal)
    {
        ["001553"] = new("7087000405", false),   // 1 file
        ["001825"] = new("7087004183", true),    // 5 files
        ["006117"] = new("7432309510", false),   // 1 file
        ["006794"] = new("7432309425", false),   // 1 file
        ["007805"] = new("7432309398", false),   // 1 file
        ["007955"] = new("7087000002", true),    // 5 files
        ["007966"] = new("7087000742", true),    // 4 files
        ["007967"] = new("7087000743", true),    // 3 files
        ["007988"] = new("7087000200", true),    // 3 files
        ["008707"] = new("7087000035", false),   // 1 file
        ["008717"] = new("7087000301", true),    // 2 files
        ["008770"] = new("7432301061", false),   // 1 file
        ["008771"] = new("7432301085", true),    // 3 files
        ["008776"] = new("7432305156", true),    // 2 files
        ["008779"] = new("7432309104", true),    // 2 files
        ["008781"] = new("7432309123", true),    // 2 files
        ["008782"] = new("7432309139", true),    // 6 files
        ["008783"] = new("7432309145", true),    // 5 files
        ["008784"] = new("7432309157", true),    // 6 files
        ["008787"] = new("7432309182", false),   // 1 file
        ["008788"] = new("7432309183", true),    // 4 files
        ["008791"] = new("7432309206", true),    // 2 files
        ["008794"] = new("7432309425", false),   // 1 file
        ["008795"] = new("7432309438", false),   // 1 file
        ["008796"] = new("7432309501", true),    // 2 files
        ["008799"] = new("7432309513", false),   // 1 file
        ["008801"] = new("7432309523", false),   // 1 file
        ["008802"] = new("7432309535", true),    // 3 files
        ["008805"] = new("7432309552", false),   // 1 file
        ["008807"] = new("7432309577", false),   // 1 file
        ["008852"] = new("7432309585", true),    // 2 files
        ["008880"] = new("7432309459", true),    // 2 files
        ["008920"] = new("7432309173", true),    // 3 files
        ["008927"] = new("7432309165", true),    // 7 files
        ["008931"] = new("7432309122", true),    // 8 files
        ["009290"] = new("7432309975", true),    // 6 files
        ["013529"] = new("7432373791", false),   // 1 file
        ["014153"] = new("7087073400", true),    // 4 files
        ["014154"] = new("7087073401", false),   // 1 file
        ["014155"] = new("7087073402", true),    // 5 files
        ["014744"] = new("7087073405", true),    // 2 files
        ["950663"] = new("7432373898", false),   // 1 file
        ["950664"] = new("7432373899", true),    // 2 files
        ["950913"] = new("7432373920", true),    // 3 files
        ["967638"] = new("7432373929", false),   // 1 file
        ["967705"] = new("7432373936", true),    // 2 files
        ["967744"] = new("7432373943", true),    // 5 files
        ["967869"] = new("7185296077", false),   // 1 file
        ["967870"] = new("7185296078", false),   // 1 file
        ["967871"] = new("7087096079", true),    // 4 files
        ["967872"] = new("7087096080", true),    // 4 files
        ["967878"] = new("7087096086", true),    // 5 files
        ["968113"] = new("7432373951", true),    // 4 files
        ["968338"] = new("7432373965", false),   // 1 file
    };

    /// <summary>How many products are on record. Used by the tests to pin the table's size.</summary>
    public static int Count => Items.Count;

    /// <summary>
    /// The UPC on record for one of Bimbo's item numbers, or null when this vendor has never been
    /// seen selling it. Not knowing a product is completely normal — a DSD route adds items every
    /// week — so a miss must never be treated as an error.
    /// </summary>
    public static Entry? Find(string? itemNumber) =>
        itemNumber is not null && Items.TryGetValue(itemNumber, out var e) ? e : null;
}
