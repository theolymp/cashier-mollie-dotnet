using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Fluent builder for creating one-off charges (non-subscription payments).
/// Use <see cref="ICashierService{TKey}.NewCharge"/> to obtain an instance.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface IChargeBuilder<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Sets a description for the charge (shown on the Mollie checkout page).</summary>
    /// <param name="description">The payment description.</param>
    IChargeBuilder<TKey> WithDescription(string description);

    /// <summary>Attaches arbitrary metadata to the charge for your own reference.</summary>
    /// <param name="metadata">Key-value pairs of metadata.</param>
    IChargeBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);

    /// <summary>
    /// Creates the charge. If the owner has a valid mandate, a direct (recurring) payment is created.
    /// If no mandate exists, a first payment with checkout redirect is created instead.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ChargeResult{TKey}"/> containing the payment, order, and optional checkout URL.</returns>
    Task<ChargeResult<TKey>> CreateAsync(CancellationToken ct = default);
}
