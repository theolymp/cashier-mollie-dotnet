namespace CashierMollie.Models;

public record SubscriptionResult<TKey>(
    Subscription<TKey> Subscription,
    string? CheckoutUrl,
    bool RequiresAction
) where TKey : IEquatable<TKey>;
