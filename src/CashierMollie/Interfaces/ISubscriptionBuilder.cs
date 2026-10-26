using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Fluent builder for creating subscriptions with optional configuration.
/// Obtained via <see cref="ICashierService{TKey}.NewSubscription"/>.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>
    /// Records a coupon code on the options object — and NOTHING ELSE. The code is read by no
    /// billing path: the subscription is created at full price, no redemption is recorded and no
    /// event is dispatched. Measured, not inferred: <c>SubscriptionOptions.CouponCode</c> has one
    /// write site and zero read sites, and neither engine mentions coupons.
    /// To actually use a coupon, call <c>ICouponService.RedeemAsync</c> for the redemption
    /// and <c>ICouponService.GetHandler(type).CalculateDiscount(...)</c> for the amount, then
    /// apply that amount to your own pricing. Kept rather than removed because removing it is a
    /// breaking change on a published API; it fails loudly nowhere, so read this line.
    /// </summary>
    /// <param name="coupon">The coupon code. Stored, never acted on.</param>
    ISubscriptionBuilder<TKey> WithCoupon(string coupon);

    /// <summary>Sets the number of trial days before billing begins.</summary>
    /// <param name="days">Number of trial days (must be non-negative).</param>
    ISubscriptionBuilder<TKey> TrialDays(int days);

    /// <summary>Enables proration when swapping plans mid-cycle.</summary>
    ISubscriptionBuilder<TKey> WithProration();

    /// <summary>
    /// Creates a mandate-only first payment (authorization without charging).
    /// The first payment description will indicate authorization rather than a charge.
    /// </summary>
    ISubscriptionBuilder<TKey> WithMandateOnly();

    /// <summary>
    /// Records metadata on the options object — and NOTHING ELSE. No request this library sends
    /// to Mollie carries metadata, and nothing writes it to a local row, so the dictionary is
    /// dropped. Measured: <c>SubscriptionOptions.Metadata</c> has one write site and zero read
    /// sites. Attach your own identifiers to your own rows instead.
    /// </summary>
    /// <param name="metadata">Key-value pairs. Stored, never sent.</param>
    ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);

    /// <summary>
    /// Creates the subscription. If the owner has no mandate, a first payment is created
    /// and the result will contain a checkout URL for the user to complete payment.
    /// </summary>
    /// <returns>A result containing the subscription, optional checkout URL, and whether user action is required.</returns>
    Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default);
}
