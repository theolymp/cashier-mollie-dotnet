namespace CashierMollie.Models;

/// <summary>
/// Result of a one-off charge operation, containing the created payment, order,
/// and an optional checkout URL if further user action is required.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
/// <param name="Payment">The local payment record created for this charge.</param>
/// <param name="Order">The order aggregate for this charge.</param>
/// <param name="CheckoutUrl">Mollie checkout URL if the payment requires user action, otherwise null.</param>
/// <param name="RequiresAction">True if the user must complete payment via <paramref name="CheckoutUrl"/>.</param>
public record ChargeResult<TKey>(
    Payment<TKey> Payment,
    Order<TKey> Order,
    string? CheckoutUrl,
    bool RequiresAction
) where TKey : IEquatable<TKey>;
