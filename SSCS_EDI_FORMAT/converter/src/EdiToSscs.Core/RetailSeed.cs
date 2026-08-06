namespace EdiToSscs.Core;

/// <summary>
/// The starting shelf price for an item SSCS has never seen, for vendors whose invoice prints no
/// retail column at all.
///
/// <see cref="InvoiceLine.RetailPrice"/> is what the item SELLS for. SSCS holds the store's own
/// prices and ignores this field for any item it already knows, so it only ever matters as the
/// opening price of a brand-new item — but it must never be 0.00, because an unpriced new item
/// would ring up free at the register.
/// </summary>
public static class RetailSeed
{
    /// <summary>
    /// A 40% margin on the invoice's own unit cost, rounded up to the next price ending in 9 within
    /// the same dime: a $2.98 cost gives 4.9667, sent as $4.99.
    ///
    /// Deliberately conservative. The seed is intended to come out under the shelf price rather than
    /// over it. That is the safe direction to be wrong
    /// in: staff correct a new item's price, and an under-priced item loses margin where an
    /// over-priced one overcharges a customer.
    /// </summary>
    public static decimal FromCost(decimal unitCost) => FromCost(unitCost, 0.40m);

    /// <summary>
    /// The same rule at a margin the vendor's own lines are known to carry. Only the percentage
    /// changes — the price still ends in a 9 within its dime, and still comes from nothing but the
    /// invoice's unit cost — so a vendor whose real margin is known does not need a second
    /// implementation of any of this.
    ///
    /// Chubiwinos runs at 50%, which is not a guess: SSCS shows Ea List 5.59 against Ea Cost 2.79 on
    /// invoice 6V9389, and 2.79 at this margin comes back as exactly 5.59.
    /// </summary>
    /// <param name="margin">Gross margin as a fraction, e.g. 0.40m for 40%.</param>
    public static decimal FromCost(decimal unitCost, decimal margin)
    {
        if (unitCost <= 0m) return 0m;
        if (margin is < 0m or >= 1m)
            throw new ArgumentOutOfRangeException(nameof(margin), margin, "A margin must be at least 0 and below 1.");

        decimal cents = decimal.Ceiling(unitCost / (1m - margin) * 100m);
        decimal lastDigit = cents % 10m;
        return (cents + (9m - lastDigit)) / 100m;
    }
}
