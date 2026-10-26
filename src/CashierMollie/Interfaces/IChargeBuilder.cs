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

    /// <summary>
    /// Attaches metadata to the charge. Sent to Mollie with the payment — the direct one when the
    /// owner has a mandate, the checkout one when they do not — and returned whenever that payment
    /// is fetched. Up to and including 0.4.0 this was stored in a field nothing read.
    /// </summary>
    /// <param name="metadata">Key-value pairs, sent to Mollie as JSON.</param>
    IChargeBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);

    /// <summary>
    /// Creates the charge. If the owner has a valid mandate, a direct (recurring) payment is created.
    /// If no mandate exists, a first payment with checkout redirect is created instead.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ChargeResult{TKey}"/> containing the payment, order, and optional checkout URL.</returns>
    Task<ChargeResult<TKey>> CreateAsync(CancellationToken ct = default);
}
