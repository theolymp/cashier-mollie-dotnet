using CashierMollie.Models;

namespace CashierMollie.Events;

public record OrderPaymentPaid(Payment Payment, Subscription? Subscription, string OwnerId);

public record OrderPaymentFailed(Payment Payment, Subscription? Subscription, string OwnerId);

public record SubscriptionCreated(Subscription Subscription, string OwnerId);

public record SubscriptionCancelled(Subscription Subscription, string OwnerId);

public record SubscriptionResumed(Subscription Subscription, string OwnerId);

public record SubscriptionPlanSwapped(Subscription Subscription, string OldPlan, string NewPlan, string OwnerId);
