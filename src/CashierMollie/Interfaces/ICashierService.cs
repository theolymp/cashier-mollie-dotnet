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
    /// <param name="newPlan">
    /// The new plan identifier. MollieNative, for a subscription with a price: the new price is
    /// resolved and sent to Mollie at once, so it applies from the next charge, and the plan
    /// itself changes when that charge is paid (<c>NextPlan</c> until then). Use it for a
    /// downgrade, which takes effect at the end of the paid cycle without a refund; an immediate
    /// change with credit is <see cref="UpgradeAsync"/>. For a subscription without a price
    /// (created before 0.6.0) it still changes only the label. Managed advances the label at the
    /// next cycle and carries the previous item's UnitPrice forward.
    /// </param>
    /// <param name="options">Optional swap configuration (e.g. proration).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated subscription.</returns>
    Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Replaces subscriptions with a new one immediately, crediting their unused value
    /// (MollieNative engine only).
    /// </summary>
    /// <remarks>
    /// <para>For each replaced subscription the unused value is <c>net x remaining whole days /
    /// days of the running cycle</c> (28 to 31 for a monthly cycle, see <see cref="Models.BillingCycle"/>),
    /// rounded to the cent, with the current day counted as used. Its net is the
    /// last paid cycle: the payment's <c>AmountNet</c> plus any <c>CreditNet</c> it carried.</para>
    /// <para>With <see cref="UpgradeOptions.OffsetCredit"/> the summed value is offset, net,
    /// against the new plan's net price; the remainder is taxed once and charged from the
    /// owner's mandate. A credit larger than the price is not held: the surplus is refunded onto
    /// the replaced subscriptions' last payments, at the tax rate each was charged at.</para>
    /// <para>The replaced subscriptions end now, at Mollie as well. The new subscription is
    /// active at once, starts a fresh cycle today, and Mollie charges its full price one cycle
    /// later, on the same day of the next month.</para>
    /// <para>Which subscriptions an upgrade replaces is the caller's decision: this library knows
    /// plan ids, not what a plan contains.</para>
    /// </remarks>
    /// <param name="owner">The billable entity; it needs a mandate.</param>
    /// <param name="name">Name of the new subscription. It must not belong to another live
    /// subscription of the owner, unless that one is among <paramref name="replaces"/>.</param>
    /// <param name="newPlan">Plan id of the new subscription.</param>
    /// <param name="replaces">Active, priced subscriptions of the same owner to end. At least one.</param>
    /// <param name="options">Upgrade options; null uses the defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpgradeResult<TKey>> UpgradeAsync(IBillable<TKey> owner, string name, string newPlan,
        IReadOnlyCollection<Subscription<TKey>> replaces, UpgradeOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Re-resolves the price of every active subscription of the owner that Mollie charges, and
    /// sends a changed amount to Mollie, effective from the next charge (MollieNative engine only).
    /// Call it when anything the price depends on has changed, such as the owner's tax treatment.
    /// It also registers an active, paid subscription that is missing at Mollie because its
    /// registration failed after the payment, which makes it the repair for an upgrade whose
    /// <c>UpgradeResult.Incomplete</c> lists a registration.
    /// </summary>
    /// <returns>The number of subscriptions changed or registered at Mollie.</returns>
    Task<int> RepriceAsync(TKey ownerId, CancellationToken ct = default);

    /// <summary>
    /// Creates a Mollie checkout for the oldest outstanding cycle payment of a <c>past_due</c>
    /// subscription (MollieNative engine only). It is a first payment, so it also creates a new
    /// mandate, and once it is paid the Mollie subscription charges that mandate from then on.
    /// When no outstanding payment remains, the subscription is active again.
    /// </summary>
    /// <returns>The checkout URL, the amount, and how many outstanding payments remain after this one.</returns>
    /// <exception cref="Exceptions.CashierException">The subscription has nothing outstanding.</exception>
    Task<OutstandingPaymentResult> PayOutstandingAsync(IBillable<TKey> owner, string name,
        string? redirectUrl = null, CancellationToken ct = default);

    /// <summary>
    /// Ends a subscription now and refunds the unused whole days of its paid cycle onto the
    /// payments behind it (MollieNative engine only), for a termination for good cause. An
    /// ordinary cancellation (<see cref="CancelAsync"/>) refunds nothing: it runs to the end of
    /// the paid cycle.
    /// </summary>
    /// <returns>Remaining days, their net value, the gross refunded and any net that could not be
    /// refunded (also logged); a failed refund is reported there rather than thrown, because the
    /// subscription has already ended.</returns>
    Task<EarlyEndResult> CancelImmediatelyWithRefundAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);

    /// <summary>
    /// The failed priced payments of a subscription that are not yet settled, oldest first: what
    /// keeps it <c>past_due</c> and what <see cref="PayOutstandingAsync"/> checks out next.
    /// </summary>
    Task<IReadOnlyList<Payment<TKey>>> GetOutstandingPaymentsAsync(IBillable<TKey> owner, string name,
        CancellationToken ct = default);

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

    /// <summary>
    /// Updates <c>Subscription.Quantity</c>. NOTE: this is NOT seat-based billing — no billing
    /// path reads that field. MollieNative sends nothing to Mollie, which keeps charging its
    /// constant amount; Managed charges UnitPrice * Quantity of the ORDER ITEM, and the next item
    /// copies the previous item's quantity. Set the quantity on the pending OrderItem to bill a
    /// new seat count.
    /// </summary>
    /// <remarks>
    /// No billing path reads that field, and since 0.5.0 that is true of every path. It was not
    /// before: when a Managed subscription had no order item at all, the engine repaired that by
    /// building one from the SUBSCRIPTION, seat count included, and pricing that item — the step
    /// this summary prescribes — billed UnitPrice * the quantity set here. The repair now writes
    /// a quantity of one, like every other path. **Behaviour change in 0.5.0**, deliberately
    /// without a compatibility switch: a silent path into seat billing was an inconsistency, not
    /// a feature, and measured on 2026-11-09 no consumer reached it.
    /// </remarks>
    Task<Subscription<TKey>> UpdateQuantityAsync(IBillable<TKey> owner, string name, int quantity, CancellationToken ct = default);

    /// <summary>Increments <c>Subscription.Quantity</c> by count. Does not change what is charged —
    /// see <see cref="UpdateQuantityAsync"/>.</summary>
    Task<Subscription<TKey>> IncrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, CancellationToken ct = default);

    /// <summary>Decrements <c>Subscription.Quantity</c> by count (minimum 1). Does not change what is
    /// charged — see <see cref="UpdateQuantityAsync"/>.</summary>
    Task<Subscription<TKey>> DecrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, CancellationToken ct = default);

    /// <summary>
    /// Creates a new one-off charge builder for the given owner and amount.
    /// Use the returned builder's fluent API to configure description, metadata, etc. before calling CreateAsync.
    /// </summary>
    /// <param name="owner">The billable entity (user) being charged.</param>
    /// <param name="amount">The charge amount (must be positive).</param>
    IChargeBuilder<TKey> NewCharge(IBillable<TKey> owner, decimal amount);

    /// <summary>
    /// Initiates a payment method update. Returns a checkout URL for the user to update their payment method.
    /// Creates a first payment (mandate creation) so that Mollie collects new payment details.
    /// </summary>
    /// <param name="owner">The billable entity whose payment method should be updated.</param>
    /// <param name="redirectUrl">Optional redirect URL after the user completes the checkout. Falls back to <see cref="CashierMollieOptions.PaymentMethodUpdateRedirectUrl"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the Mollie checkout URL.</returns>
    Task<PaymentMethodUpdateResult> UpdatePaymentMethodAsync(IBillable<TKey> owner,
        string? redirectUrl = null, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the owner has a valid Mollie mandate.
    /// Returns false if no customer or mandate ID is set, or if the mandate status is not "valid".
    /// </summary>
    Task<bool> HasValidMandateAsync(IBillable<TKey> owner, CancellationToken ct = default);

    /// <summary>
    /// Revokes the owner's Mollie mandate and clears the local mandate reference.
    /// Dispatches a <see cref="Events.MandateCleared{TKey}"/> event.
    /// No-op if the owner has no mandate set.
    /// </summary>
    Task RevokeMandateAsync(IBillable<TKey> owner, CancellationToken ct = default);
}
