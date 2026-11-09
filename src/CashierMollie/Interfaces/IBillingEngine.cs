using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Strategy interface for billing engine implementations.
/// MollieNative uses Mollie's Subscription API; Managed runs its own billing cycle.
/// </summary>
/// <remarks>
/// This interface is public and DI-registered, so a consumer can call it directly and reads these
/// summaries in IntelliSense -- which is why they describe measured behaviour rather than intent.
/// Two of them did not: until 0.5.1 this one promised "seat-based billing" and a plan swap without
/// saying the amount is unchanged, both the opposite of what the engines do, and both shipped
/// inside 0.5.0 next to an <see cref="ICashierService{TKey}"/> summary that said so correctly.
/// </remarks>
public interface IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>
    /// Creates a subscription using this engine's billing strategy. Throws
    /// <see cref="NotSupportedException"/> when <c>SubscriptionOptions.Prorate</c> is set.
    /// </summary>
    Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default);

    /// <summary>Cancels a subscription. If immediately is false, enters grace period.</summary>
    Task CancelSubscriptionAsync(Subscription<TKey> subscription, bool immediately,
        CancellationToken ct = default);

    /// <summary>Resumes a cancelled subscription that is on grace period.</summary>
    Task ResumeSubscriptionAsync(Subscription<TKey> subscription,
        CancellationToken ct = default);

    /// <summary>
    /// Assigns a different plan. **This does not change what is charged** -- see
    /// <see cref="ICashierService{TKey}.SwapAsync"/> for the measured behaviour per engine:
    /// MollieNative sends an update carrying neither amount nor description, so Mollie keeps
    /// charging the previous amount; Managed takes effect on the next cycle, whose order item
    /// copies the previous item's unit price. Throws <see cref="NotSupportedException"/> when
    /// <c>SwapOptions.Prorate</c> is set, because no proration is computed.
    /// </summary>
    Task<Subscription<TKey>> SwapPlanAsync(Subscription<TKey> subscription, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>Subscription.Quantity</c>. **This is NOT seat-based billing** -- no billing path
    /// reads that field; see <see cref="ICashierService{TKey}.UpdateQuantityAsync"/>. Set the
    /// quantity on the pending <c>OrderItem</c> to bill a new seat count.
    /// </summary>
    Task<Subscription<TKey>> UpdateQuantityAsync(Subscription<TKey> subscription,
        int quantity, CancellationToken ct = default);

    /// <summary>Processes due order items (only relevant for ManagedEngine).</summary>
    Task ProcessDueItemsAsync(CancellationToken ct = default);

    /// <summary>Whether this engine requires a background service for processing.</summary>
    bool RequiresBackgroundProcessing { get; }
}
