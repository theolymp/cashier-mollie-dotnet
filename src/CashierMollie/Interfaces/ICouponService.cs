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

    /// <summary>Revokes all active coupons for a subscription.</summary>
    Task RevokeAsync(IBillable<TKey> owner, string subscriptionName = "default",
        CancellationToken ct = default);

    /// <summary>Gets the appropriate handler for a coupon's handler type.</summary>
    ICouponHandler GetHandler(string handlerType);
}
