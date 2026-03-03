using CashierMollie.Models;

namespace CashierMollie.Events;

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
