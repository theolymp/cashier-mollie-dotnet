using CashierMollie.Models;

namespace CashierMollie.Events;

// ── Existing events ─────────────────────────────────────────────────

/// <summary>Dispatched when a payment has been successfully processed.</summary>
public record OrderPaymentPaid<TKey>(Payment<TKey> Payment, Subscription<TKey>? Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a payment attempt has failed, been canceled, or expired.</summary>
public record OrderPaymentFailed<TKey>(Payment<TKey> Payment, Subscription<TKey>? Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a new subscription is activated (either immediately or after first payment).</summary>
public record SubscriptionCreated<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a subscription is cancelled.</summary>
public record SubscriptionCancelled<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a cancelled subscription is resumed during its grace period.</summary>
public record SubscriptionResumed<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a subscription's plan is changed.</summary>
public record SubscriptionPlanSwapped<TKey>(Subscription<TKey> Subscription, string OldPlan, string NewPlan, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// Dispatched when <see cref="Interfaces.ICashierService{TKey}.UpgradeAsync"/> has replaced
/// subscriptions with a new one. Carries the full calculation for the invoice.
/// </summary>
public record SubscriptionUpgraded<TKey>(UpgradeResult<TKey> Result, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// Dispatched when a cycle payment failed and the subscription is now <c>past_due</c>: not active
/// until the outstanding amount is paid (see <see cref="Interfaces.ICashierService{TKey}.PayOutstandingAsync"/>).
/// <c>past_due</c> means "a priced payment is unpaid", not "the customer was dunned": a
/// subscription created from an existing mandate is active at once, so its very first debit can
/// fail too. <see cref="NeverPaid"/> tells that case apart.
/// </summary>
public record SubscriptionPastDue<TKey>(Subscription<TKey> Subscription, Payment<TKey> FailedPayment, TKey OwnerId)
    where TKey : IEquatable<TKey>
{
    /// <summary>
    /// True when no payment of this subscription was ever paid: the first debit failed. A consumer
    /// may treat that as a sign-up that did not go through rather than as a debt to dun.
    /// </summary>
    public bool NeverPaid { get; init; }
}

/// <summary>Dispatched when the last outstanding amount of a <c>past_due</c> subscription was paid
/// and it is active again.</summary>
public record SubscriptionPaymentRecovered<TKey>(Subscription<TKey> Subscription, Payment<TKey> Payment, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Subscription events ─────────────────────────────────────────────

/// <summary>Dispatched when a subscription's quantity is updated.</summary>
public record SubscriptionQuantityUpdated<TKey>(Subscription<TKey> Subscription, int OldQuantity, int NewQuantity, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Payment events ──────────────────────────────────────────────────

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: a payment fails due to an invalid or revoked mandate.
/// </summary>
public record OrderPaymentFailedDueToInvalidMandate<TKey>(Payment<TKey> Payment, Subscription<TKey>? Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when the first payment (mandate creation) succeeds.</summary>
public record FirstPaymentPaid<TKey>(Payment<TKey> Payment, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when the first payment (mandate creation) fails.</summary>
public record FirstPaymentFailed<TKey>(Payment<TKey> Payment, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Mandate events ──────────────────────────────────────────────────

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: a billable entity's mandate is updated to a new one.
/// </summary>
public record MandateUpdated<TKey>(string? OldMandateId, string NewMandateId, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when a billable entity's mandate is cleared (removed).</summary>
public record MandateCleared<TKey>(string MandateId, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Coupon events ───────────────────────────────────────────────────

/// <summary>Dispatched when a coupon is applied to a subscription.</summary>
public record CouponApplied<TKey>(string CouponCode, Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Credit / Balance events ─────────────────────────────────────────

/// <summary>Dispatched when credit is added to a billable entity's balance.</summary>
public record CreditAdded<TKey>(decimal Amount, string Currency, string? Description, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>Dispatched when credit is consumed from a billable entity's balance during payment.</summary>
public record CreditApplied<TKey>(decimal Amount, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: a billable entity's balance becomes stale (e.g. unused for too long).
/// </summary>
public record BalanceTurnedStale<TKey>(decimal Balance, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Refund events ───────────────────────────────────────────────────

/// <summary>Dispatched when a refund is initiated for a payment.</summary>
public record RefundInitiated<TKey>(Payment<TKey> Payment, decimal Amount, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: a refund has been successfully processed.
/// </summary>
public record RefundProcessed<TKey>(Payment<TKey> Payment, decimal Amount, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: a refund attempt has failed.
/// </summary>
public record RefundFailed<TKey>(Payment<TKey> Payment, decimal Amount, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Chargeback events ───────────────────────────────────────────────

/// <summary>Dispatched when a chargeback is received for a payment.</summary>
public record ChargebackReceived<TKey>(Payment<TKey> Payment, decimal Amount, string Currency, TKey OwnerId)
    where TKey : IEquatable<TKey>;

// ── Order events ────────────────────────────────────────────────────

/// <summary>Dispatched when a new order is created.</summary>
public record OrderCreated<TKey>(long OrderId, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// Dispatched when an order has been billed: the Managed engine charged an order item and wrote
/// the order header for it. Carries the order id, so a handler can load the order and its items.
/// </summary>
/// <remarks>
/// Reserved until 0.5.0, because nothing created an order header outside one-off charges. A
/// handler registered before that never ran.
/// </remarks>
public record OrderProcessed<TKey>(long OrderId, TKey OwnerId)
    where TKey : IEquatable<TKey>;

/// <summary>
/// RESERVED — no service dispatches this event; registering a handler has no effect.
/// Intended meaning: an invoice becomes available for an order.
/// </summary>
public record OrderInvoiceAvailable<TKey>(long OrderId, TKey OwnerId)
    where TKey : IEquatable<TKey>;
