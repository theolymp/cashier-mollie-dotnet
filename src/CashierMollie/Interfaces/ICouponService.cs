using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Service for validating and redeeming coupons.</summary>
public interface ICouponService<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Validates a coupon code for the given owner.</summary>
    Task<Coupon> ValidateAsync(string code, IBillable<TKey> owner, CancellationToken ct = default);

    /// <summary>Redeems a coupon for a subscription.</summary>
    Task<RedeemedCoupon<TKey>> RedeemAsync(IBillable<TKey> owner, string code,
        string subscriptionName = "default", CancellationToken ct = default);

    /// <summary>
    /// The discount an active redemption grants on one billing cycle of
    /// <paramref name="unitPrice"/> x <paramref name="quantity"/>. Returns
    /// <see cref="CouponDiscount.None"/> when no redemption has cycles left.
    /// Pass the UNIT price: both shipped handlers multiply by quantity themselves, so handing them
    /// a gross amount would apply the quantity twice.
    /// </summary>
    Task<CouponDiscount> CalculateActiveDiscountAsync(TKey ownerId, string subscriptionName,
        decimal unitPrice, int quantity, CancellationToken ct = default);

    /// <summary>
    /// Consumes one billing cycle of the active redemption (decrements <c>TimesLeft</c>). Call this
    /// only after the discounted charge actually succeeded — consuming first would spend a cycle on
    /// a payment that failed.
    /// </summary>
    Task ConsumeRedemptionAsync(TKey ownerId, string subscriptionName,
        CancellationToken ct = default);

    /// <summary>Revokes all active coupons for a subscription.</summary>
    Task RevokeAsync(IBillable<TKey> owner, string subscriptionName = "default",
        CancellationToken ct = default);

    /// <summary>
    /// Gets the appropriate handler for a coupon's handler type. This is the ONLY route to a
    /// discount calculation: nothing inside the library calls a handler, so if you never call
    /// this, no coupon ever changes an amount.
    /// </summary>
    /// <param name="handlerType">"percentage" or "fixed". Throws for anything else, including an
    /// empty string.</param>
    ICouponHandler GetHandler(string handlerType);
}
