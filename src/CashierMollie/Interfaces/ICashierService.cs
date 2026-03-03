using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Main entry point for subscription management operations.
/// Inject this service to create, cancel, resume, and swap subscriptions.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key (e.g. string, int, Guid).</typeparam>
public interface ICashierService<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>
    /// Creates a new subscription builder for the given owner, subscription name, and plan.
    /// Use the returned builder's fluent API to configure trial days, coupons, etc. before calling CreateAsync.
    /// </summary>
    /// <param name="owner">The billable entity (user) who will own the subscription.</param>
    /// <param name="name">A label for this subscription (e.g. "default"). Used to distinguish multiple subscriptions per user.</param>
    /// <param name="plan">The plan identifier matching your billing configuration.</param>
    ISubscriptionBuilder<TKey> NewSubscription(IBillable<TKey> owner, string name, string plan);

    /// <summary>
    /// Cancels a subscription at the end of the current billing period (grace period).
    /// The subscription remains active until <see cref="CashierMollieOptions.GracePeriodDays"/> after the cycle start.
    /// </summary>
    Task CancelAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Cancels a subscription immediately without a grace period.
    /// The subscription is ended right away.
    /// </summary>
    Task CancelImmediatelyAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Resumes a cancelled subscription that is still within its grace period.
    /// Throws <see cref="Exceptions.CashierException"/> if the subscription is not on a grace period.
    /// </summary>
    Task ResumeAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Swaps the subscription to a different plan.
    /// </summary>
    /// <param name="owner">The billable entity.</param>
    /// <param name="name">The subscription name.</param>
    /// <param name="newPlan">The new plan identifier.</param>
    /// <param name="options">Optional swap configuration (e.g. proration).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated subscription.</returns>
    Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the owner has an active subscription with the given name.
    /// Returns true if the subscription is active, on a grace period, or on a trial.
    /// </summary>
    Task<bool> IsSubscribedAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the subscription is cancelled but still within its grace period.
    /// </summary>
    Task<bool> OnGracePeriodAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the subscription is currently on a trial period.
    /// </summary>
    Task<bool> OnTrialAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the subscription has been cancelled (has an end date set).
    /// Note: a cancelled subscription may still be active during its grace period.
    /// </summary>
    Task<bool> IsCancelledAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single subscription by owner and name, or null if not found.
    /// </summary>
    Task<Subscription<TKey>?> GetSubscriptionAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all subscriptions for the given owner.
    /// </summary>
    Task<List<Subscription<TKey>>> GetSubscriptionsAsync(IBillable<TKey> owner, CancellationToken ct = default);

    /// <summary>Gets or creates a Mollie customer for the owner.</summary>
    Task<string> GetOrCreateMollieCustomerAsync(IBillable<TKey> owner, CancellationToken ct = default);

    /// <summary>Updates the Mollie customer with current owner data.</summary>
    Task UpdateMollieCustomerAsync(IBillable<TKey> owner, CancellationToken ct = default);

    /// <summary>Updates the subscription quantity (seat-based billing).</summary>
    Task<Subscription<TKey>> UpdateQuantityAsync(IBillable<TKey> owner, string name, int quantity, CancellationToken ct = default);

    /// <summary>Increments the subscription quantity by count.</summary>
    Task<Subscription<TKey>> IncrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, CancellationToken ct = default);

    /// <summary>Decrements the subscription quantity by count (minimum 1).</summary>
    Task<Subscription<TKey>> DecrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, CancellationToken ct = default);

    /// <summary>
    /// Creates a new one-off charge builder for the given owner and amount.
    /// Use the returned builder's fluent API to configure description, metadata, etc. before calling CreateAsync.
    /// </summary>
    /// <param name="owner">The billable entity (user) being charged.</param>
    /// <param name="amount">The charge amount (must be positive).</param>
    IChargeBuilder<TKey> NewCharge(IBillable<TKey> owner, decimal amount);
}
