using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Fluent builder for creating subscriptions with optional configuration.
/// Obtained via <see cref="ICashierService{TKey}.NewSubscription"/>.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Applies a coupon/discount code to the subscription.</summary>
    /// <param name="coupon">The coupon code to apply.</param>
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

    /// <summary>Attaches custom metadata to the subscription.</summary>
    /// <param name="metadata">Key-value pairs of metadata.</param>
    ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);

    /// <summary>
    /// Creates the subscription. If the owner has no mandate, a first payment is created
    /// and the result will contain a checkout URL for the user to complete payment.
    /// </summary>
    /// <returns>A result containing the subscription, optional checkout URL, and whether user action is required.</returns>
    Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default);
}
