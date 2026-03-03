using CashierMollie.Models;

namespace CashierMollie.Events;

public record OrderPaymentPaid<TKey>(Payment<TKey> Payment, Subscription<TKey>? Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

public record OrderPaymentFailed<TKey>(Payment<TKey> Payment, Subscription<TKey>? Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

public record SubscriptionCreated<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

public record SubscriptionCancelled<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

public record SubscriptionResumed<TKey>(Subscription<TKey> Subscription, TKey OwnerId)
    where TKey : IEquatable<TKey>;

public record SubscriptionPlanSwapped<TKey>(Subscription<TKey> Subscription, string OldPlan, string NewPlan, TKey OwnerId)
    where TKey : IEquatable<TKey>;
