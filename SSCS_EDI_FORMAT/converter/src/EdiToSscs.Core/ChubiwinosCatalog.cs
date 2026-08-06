namespace EdiToSscs.Core;

/// <summary>
/// What barcode Chubiwinos prints for each of its product numbers.
///
/// WHY THIS EXISTS
/// ───────────────
/// Chubiwinos invoices arrive as scans and the OCR text layer gets a barcode wrong about one time in
/// five: across the eight sample invoices seven of the thirty-five printed codes failed their own
/// check digit. Every single one was a single wrong digit in a code that appears correctly on
/// another invoice — "1501011151062" for 7501011151062, "7501011130669" for ...0869 — because the
/// product number beside it never changed. A wrong barcode is the one error the invoice's own
/// arithmetic cannot catch: the money still balances to the penny while the stock lands on an item
/// id that does not exist.
///
/// So a code that fails its check digit is replaced by what this vendor is known to print for that
/// product number. A code that PASSES its check digit is always sent as printed and never looked up
/// here — the check digit is arithmetic on the code itself, where this table is only a record of the
/// past. See <c>ChubiwinosPdfParser.ResolveBarcode</c>.
///
/// WHAT IT IS NOT
/// ──────────────
/// It is not a vendor→code mapping the user can configure; that was built once and deliberately
/// removed. It is not consulted for quantities, costs or descriptions, which the invoice's own
/// checksums already prove. And it is not required to be complete: an item that is not here and
/// whose code will not read is handed to the operator to type, which is the honest answer.
///
/// MAINTAINING IT
/// ──────────────
/// Add an entry when Chubiwinos starts shipping a new product AND its barcode is seen to fail on a
/// scan. Only ever add a code that passes <see cref="Upc.IsValidGtin"/> — the constructor test
/// <c>EveryCatalogEntryIsAValidBarcode</c> enforces that — and only from an invoice where it read
/// cleanly. Guessing an entry here would silently redirect every future invoice for that product.
/// </summary>
public static class ChubiwinosCatalog
{
    /// <summary>
    /// Product number → barcode, taken from the eight sample invoices (03/14/26 to 07/03/26) where
    /// each code read cleanly and passed its check digit.
    ///
    /// Two products the samples charge are deliberately absent: 905 (WINNUTS PAPAS DIABLA) only ever
    /// scanned as eleven digits, and 918 (WINNUTS CHICHARRONES CHILE-LIMON) only ever scanned as
    /// 650156007331, which fails its check digit. Neither has a reading good enough to record, so
    /// both go to the operator until one turns up.
    /// </summary>
    private static readonly Dictionary<string, string> Barcodes = new(StringComparer.Ordinal)
    {
        ["200"] = "7500478002559",   // CHURRUMAIS LG 185g
        ["205"] = "7501011167667",   // PAPAS ADOBADAS 105 g
        ["210"] = "7500478044160",   // CHEETOS BOLITA -110 g
        ["215"] = "7501011130937",   // CHEETOS XTRA FLAM HOT -150 g
        ["220"] = "7500478044207",   // CHEETO COLMILLO -100 g
        ["224"] = "7501011130869",   // TOSTITOS SALSA VERDE 175 g
        ["225"] = "7501011130876",   // TOSTITOS FLAMING HOT -180 g
        ["230"] = "7501011114517",   // RANCHERITO- 145 g
        ["240"] = "7501011131064",   // DORITOS NACHOS- 150 g
        ["245"] = "7501011180499",   // PAPA CRUJIENTE XTRA HOT
        ["250"] = "7501011131040",   // CRUJITOS LG 120gm
        ["255"] = "7500478044924",   // SABRITONES -160g
        ["260"] = "7501011151062",   // FRITOS CHORIZO -170 g
        ["270"] = "7501011131118",   // FRITOS CHILE Y LIMON -170 g
        ["280"] = "7501011167674",   // RUFFLES QUESO- 120 g
        ["290"] = "7500478044276",   // CHEETOS TORCIDO-145 g
        ["291"] = "7500478044405",   // PAKETAXO DARK
        ["292"] = "7500478012138",   // DORITO INCOGNITO-
        ["294"] = "7501011154902",   // PAKETAXO MEZCLADITO-265 g
        ["296"] = "7501011155022",   // PAKETAXO XTRA FLAMING HOT 228g
        ["297"] = "7501011155008",   // PAKETAXO QUESO-208gm
        ["298"] = "7501011154865",   // PAKETAXO BOTANERO -225gm
        ["299"] = "7500478012114",   // DORITOS FLAMING HOT -223g
        ["800"] = "604722008186",    // LEO CHICHARRON W/ HOT SAUCE 100g
        ["801"] = "604722010950",    // SNAKY CURLS CRACKLINS-99g
        ["802"] = "604722010967",    // SNAKY CURLS HOT CRACKLINS-99g
        ["900"] = "850883002611",    // WINNUTS PAPAS CASERAS CON SAL- 6oz
    };

    /// <summary>Number of products on record; used by the test that validates every entry.</summary>
    public static IReadOnlyDictionary<string, string> All => Barcodes;

    /// <summary>The barcode this vendor prints for a product number, or null if it is not on record.</summary>
    public static string? BarcodeFor(string itemNumber) =>
        itemNumber is not null && Barcodes.TryGetValue(itemNumber, out string? code) ? code : null;
}
