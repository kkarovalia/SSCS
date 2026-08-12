namespace EdiToSscs.Core;

/// <summary>
/// One product row of an invoice as a vision model read it off the **picture**, with no sight of the
/// text layer.
///
/// Every field is nullable because a model that cannot read a figure must say so rather than fill it
/// in. A guessed amount that happens to reconcile is the single worst thing that can come out of
/// this file — it would satisfy the checks by construction and book a cost nothing downstream
/// questions.
/// </summary>
public sealed record VisionRow
{
    /// <summary>The vendor's own item number, which is what ties this reading to a parsed row.</summary>
    public required string ItemNumber { get; init; }

    /// <summary>What the row is called. Recorded for the log only — never used in the output.</summary>
    public string Description { get; init; } = "";

    /// <summary>The printed barcode digits, when the row prints them and they were legible.</summary>
    public string? Barcode { get; init; }

    /// <summary>Individual units, not cases — the same figure the UNITS column prints.</summary>
    public int? Units { get; init; }

    /// <summary>
    /// The case count, where the vendor prints one alongside the units.
    ///
    /// Only used on a page the text layer could not read, and there it is what makes the row
    /// checkable at all: `QTY x PRICE = TOTAL` over three figures read separately is a real test of
    /// a reading, and without QTY a single reader's amount has nothing standing behind it.
    /// </summary>
    public decimal? Qty { get; init; }

    /// <summary>The per-case price column.</summary>
    public decimal? Price { get; init; }

    /// <summary>The extended amount at the end of the row.</summary>
    public decimal? Amount { get; init; }

    /// <summary>
    /// Whether the row is a return rather than a delivery — printed in brackets, or under a CREDITS
    /// banner. Every figure here is a magnitude, so this is the only thing carrying the sign, and
    /// getting it wrong flips a return into a delivery and doubles the error.
    /// </summary>
    public bool IsCredit { get; init; }

    /// <summary>
    /// Which page of the PDF this row was read off, 1-based; 0 when nobody recorded one.
    ///
    /// Only used to put the right picture in front of the operator. A row that came off the picture
    /// has no line number and so no band of paper to cut, and the fallback is to show the whole page
    /// — which is only any help if it is the page the row is actually on. One request is one page,
    /// so this is known exactly rather than guessed.
    /// </summary>
    public int Page { get; init; }

    /// <summary>
    /// Whether that page went through the scanner the wrong way up.
    ///
    /// Four of the store's thirty samples did, and the picture shown to the operator is upside down
    /// unless somebody says so. Nothing else on the page can tell us: the text layer on these files
    /// is debris precisely *because* the page is inverted, so it cannot be the thing that reports it.
    /// A reader looking at the picture can, and it costs nothing to be wrong — the worst case is a
    /// rotated page on screen, since no figure is derived from this and none is marked.
    /// </summary>
    public bool PageUpsideDown { get; init; }

    /// <summary>
    /// How far down the page this row is printed, 0 at the top and 1000 at the bottom; null when the
    /// model did not say.
    ///
    /// Used for one thing only: scrolling the picture so the row is in view when the question opens.
    /// **Nothing is marked from it and no figure is derived from it**, because it is a guess and a
    /// marker on the wrong row is the one mistake this whole prompt exists to prevent. Being wrong
    /// costs a scroll, which is exactly what the operator had to do anyway.
    /// </summary>
    public int? VerticalPosition { get; init; }
}

/// <summary>
/// What a parser already knows about one of its own rows, offered so a vision reading can be tied
/// to it. Every field may be missing — a damaged row is exactly the case that matters — and the tie
/// is refused rather than guessed when too few of them line up.
/// </summary>
/// <param name="Key">The row's identifying code: the vendor's item number, or a UPC.</param>
/// <param name="Units">The count the text layer read, where it read one.</param>
/// <param name="Amount">The extended amount the text layer read, where it read one.</param>
public readonly record struct VisionAnchor(string? Key, int? Units, decimal? Amount);

/// <summary>
/// A whole invoice as read off the pictures of its pages.
/// </summary>
public sealed record VisionReading
{
    public required IReadOnlyList<VisionRow> Rows { get; init; }

    /// <summary>The total the ticket prints for itself, when it was legible.</summary>
    public decimal? PrintedTotal { get; init; }

    /// <summary>The unit count the ticket prints for itself, when it was legible.</summary>
    public int? PrintedUnits { get; init; }

    /// <summary>An empty reading, which is what every failure produces.</summary>
    public static VisionReading Empty { get; } = new() { Rows = [] };

    /// <summary>
    /// Lines this reading's rows up against a parser's rows, returning one entry per parser row —
    /// the vision row that belongs to it, or null where nothing could be tied to it beyond doubt.
    ///
    /// This is the dangerous part of the whole feature. A vision row attached to the wrong parser
    /// row is worse than no vision at all: it would put one product's figures onto another product,
    /// and — because the money would still add up somewhere — no checksum on the page would notice.
    /// It is the same failure the bars can produce, and it is guarded the same way, by refusing
    /// every tie that is not forced.
    ///
    /// Position is tried first, because both readers went down the same page in the same order and
    /// on a ticket where they found the same number of rows, row 3 is row 3. Tying on the item
    /// number instead would fail exactly where this feature is wanted: the item column is OCR debris
    /// on these scans — one sample reads `O84815` and `9/221027` — so a key match would go missing
    /// on the damaged rows and hold only on the clean ones nobody needed help with.
    ///
    /// What makes position safe is that it is **checked before it is used**. A parser row and a
    /// vision row that are really the same row agree about something: the item number, the count, or
    /// the amount. Most rows on any ticket are undamaged, so a correct alignment shows a large
    /// majority of agreements and one shifted by a row shows almost none. More than half must agree
    /// or position is abandoned.
    ///
    /// Failing that, rows are tied by key — the n-th vision row carrying a number for the n-th
    /// parser row that carries it, so a ticket billing the same product twice still lines up.
    /// Anything less determined than that is left null. There is deliberately no "last one standing"
    /// rule: pairing off the single row neither side could key looks forced, but it is only forced
    /// if the two lists really describe the same rows, and a model that dropped one row and invented
    /// another satisfies it exactly while tying the wrong two together.
    /// </summary>
    /// <param name="keyOf">
    /// Which field of a vision row an anchor's key is compared against. Red Bull ties on the
    /// vendor's item number; Dr Pepper's priced row has no such column and ties on the UPC.
    /// </param>
    public VisionRow?[] AlignTo(IReadOnlyList<VisionAnchor> anchors, Func<VisionRow, string?>? keyOf = null)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        keyOf ??= row => row.ItemNumber;

        if (Rows.Count == anchors.Count && Rows.Count > 0 && PositionHolds(anchors, keyOf))
            return [.. Rows.Select(r => (VisionRow?)r)];

        var aligned = new VisionRow?[anchors.Count];
        var taken = new bool[Rows.Count];

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < anchors.Count; i++)
        {
            string key = Normalise(anchors[i].Key);
            if (key.Length == 0) continue;

            seen.TryGetValue(key, out int occurrence);
            seen[key] = occurrence + 1;

            int found = -1, matches = 0;
            for (int j = 0; j < Rows.Count; j++)
            {
                if (Normalise(keyOf(Rows[j])) != key) continue;
                if (matches++ == occurrence) found = j;
            }

            if (found >= 0 && !taken[found])
            {
                aligned[i] = Rows[found];
                taken[found] = true;
            }
        }

        return aligned;
    }

    /// <summary>
    /// Whether pairing the two lists off in order is corroborated by the rows themselves.
    ///
    /// A pair agrees when the readers landed on the same item number, the same count, or the same
    /// amount — any one of the three, because a damaged row loses some of them and a whole ticket is
    /// never damaged everywhere at once. A single-row ticket is forced and needs no vote: both
    /// readers found one product on one page and there is nothing else it could be.
    /// </summary>
    private bool PositionHolds(IReadOnlyList<VisionAnchor> anchors, Func<VisionRow, string?> keyOf)
    {
        if (anchors.Count == 1) return true;

        int agree = 0;
        for (int i = 0; i < anchors.Count; i++)
        {
            var a = anchors[i];
            var r = Rows[i];

            string key = Normalise(a.Key), theirs = Normalise(keyOf(r));
            bool sameKey = key.Length > 0 && key == theirs;
            bool sameUnits = a.Units is int u && r.Units is int v && Math.Abs(u) == Math.Abs(v);
            bool sameAmount = a.Amount is decimal x && r.Amount is decimal y
                              && Math.Abs(Math.Abs(x) - Math.Abs(y)) < 0.005m;

            if (sameKey || sameUnits || sameAmount) agree++;
        }
        return agree * 2 > anchors.Count;
    }

    /// <summary>
    /// Item numbers as they compare. The scan renders the same number with and without its spaces
    /// and in either case, and none of that is a real difference.
    /// </summary>
    private static string Normalise(string? itemNumber)
    {
        if (string.IsNullOrWhiteSpace(itemNumber)) return "";
        Span<char> buffer = stackalloc char[itemNumber.Length];
        int n = 0;
        foreach (char c in itemNumber)
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
        return new string(buffer[..n]);
    }
}

/// <summary>Which vendor's ticket is being read, so the reader can ask for the right columns.</summary>
public enum VisionVendor
{
    RedBull,
    DrPepper,
}

/// <summary>
/// Reads an invoice off the picture rather than out of the text layer.
///
/// Why this exists. Every reading this converter has ever got wrong came out of one OCR pass, and
/// two figures from that pass agreeing is usually one mistake twice — which is the entire difficulty
/// with these vendors. A vision model looking at the photograph fails differently, so a vision
/// reading and an OCR reading agreeing is genuine corroboration in the way that `QTY x PRICE`
/// checked against `TOTAL` is not.
///
/// What it is **not** is an oracle. It is a second reader, and it earns a row the same way every
/// other reading here does: by being confirmed against something the ticket prints for itself. Where
/// the vision reading and the text layer disagree and the page can settle which is right — the row
/// multiplies out, or the ticket reaches its own printed total — the winner is taken silently. Where
/// they disagree and nothing on the page can tell them apart, the operator is shown the row. That
/// last case is rare and it is the only place a person is still involved in a figure both readers
/// had an opinion about.
///
/// Best-effort throughout, exactly like <see cref="PdfBarcodeReader"/>: no key configured, no
/// network, a refused request, a reply that will not parse — all give
/// <see cref="VisionReading.Empty"/>, and the conversion goes on doing what it does today. Nothing
/// here may ever cost the store a delivery.
/// </summary>
public interface IInvoiceVisionReader
{
    /// <summary>
    /// Reads every product row off the pages of <paramref name="pdfBytes"/>. Never throws; returns
    /// <see cref="VisionReading.Empty"/> when the invoice could not be read for any reason at all.
    /// </summary>
    VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor);
}
