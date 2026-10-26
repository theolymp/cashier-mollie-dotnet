namespace CashierMollie.Models;

/// <summary>Why a coupon discount is, or is not, the amount it is.</summary>
public enum CouponDiscountStatus
{
    /// <summary>No redemption with billing cycles left exists for this subscription.</summary>
    NoActiveRedemption,

    /// <summary>A redemption was found, its definition resolved, and the discount computed.</summary>
    Applied,

    /// <summary>
    /// A redemption exists, but its coupon definition is no longer available from the repository —
    /// typically because the code was removed from configuration after someone redeemed it. The
    /// discount is zero and the caller is expected to say so out loud: this is the third state, and
    /// collapsing it into <see cref="NoActiveRedemption"/> would charge full price in silence.
    /// </summary>
    DefinitionMissing,
}

/// <summary>
/// The discount an active redemption grants, with the reason behind it. A bare <c>decimal</c> would
/// make "no coupon" and "coupon whose definition vanished" indistinguishable, and only the second
/// one means somebody should look.
/// </summary>
/// <param name="Amount">The discount, never negative and never more than the gross amount.</param>
/// <param name="Status">Why the amount is what it is.</param>
/// <param name="Code">The redeemed code, when there was one.</param>
public sealed record CouponDiscount(decimal Amount, CouponDiscountStatus Status, string? Code)
{
    /// <summary>No redemption applies.</summary>
    public static readonly CouponDiscount None = new(0m, CouponDiscountStatus.NoActiveRedemption, null);
}
