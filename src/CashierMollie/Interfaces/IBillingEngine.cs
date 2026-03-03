using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Strategy interface for billing engine implementations.
/// MollieNative uses Mollie's Subscription API; Managed runs its own billing cycle.
/// </summary>
public interface IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Creates a subscription using this engine's billing strategy.</summary>
    Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default);

    /// <summary>Cancels a subscription. If immediately is false, enters grace period.</summary>
    Task CancelSubscriptionAsync(Subscription<TKey> subscription, bool immediately,
        CancellationToken ct = default);

    /// <summary>Resumes a cancelled subscription that is on grace period.</summary>
    Task ResumeSubscriptionAsync(Subscription<TKey> subscription,
        CancellationToken ct = default);

    /// <summary>Swaps a subscription to a different plan.</summary>
    Task<Subscription<TKey>> SwapPlanAsync(Subscription<TKey> subscription, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default);

    /// <summary>Updates subscription quantity (seat-based billing).</summary>
    Task<Subscription<TKey>> UpdateQuantityAsync(Subscription<TKey> subscription,
        int quantity, CancellationToken ct = default);

    /// <summary>Processes due order items (only relevant for ManagedEngine).</summary>
    Task ProcessDueItemsAsync(CancellationToken ct = default);

    /// <summary>Whether this engine requires a background service for processing.</summary>
    bool RequiresBackgroundProcessing { get; }
}
