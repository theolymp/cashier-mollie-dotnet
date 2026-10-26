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
    /// Applies a coupon to the subscription. The code is validated when the subscription is
    /// created and the redemption recorded; an unknown code throws <c>CashierException</c> rather
    /// than being accepted and dropped, which is what happened up to and including 0.4.0.
    /// <para>
    /// Whether it reduces money depends on the engine: Managed subtracts the discount from each
    /// recurring charge until the coupon's cycles are exhausted; MollieNative records the
    /// redemption and reduces nothing, because it registers no recurring charge with Mollie.
    /// </para>
    /// </summary>
    /// <param name="coupon">The coupon code, as configured in the coupon repository.</param>
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
    /// Attaches metadata to the first payment Mollie creates for this subscription. Stored at
    /// Mollie as JSON and returned when that payment is fetched.
    /// <para>
    /// First payment only: recurring charges come from order items, which carry no metadata, so
    /// the dictionary does not follow the subscription into later cycles.
    /// </para>
    /// </summary>
    /// <param name="metadata">Key-value pairs, sent to Mollie as JSON.</param>
    ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);

    /// <summary>
    /// Creates the subscription. If the owner has no mandate, a first payment is created
    /// and the result will contain a checkout URL for the user to complete payment.
    /// </summary>
    /// <returns>A result containing the subscription, optional checkout URL, and whether user action is required.</returns>
    Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default);
}
