namespace CashierMollie.Models;

/// <summary>
/// Result of creating a subscription via <see cref="Interfaces.ISubscriptionBuilder{TKey}.CreateAsync"/>.
/// </summary>
/// <param name="Subscription">The created subscription entity.</param>
/// <param name="CheckoutUrl">The Mollie checkout URL if the user needs to complete a first payment, otherwise null.</param>
/// <param name="RequiresAction">True if the user must be redirected to complete payment (no mandate exists).</param>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public record SubscriptionResult<TKey>(
    Subscription<TKey> Subscription,
    string? CheckoutUrl,
    bool RequiresAction
) where TKey : IEquatable<TKey>;
