using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Interface for generating invoices from processed orders.
/// The default implementation is a no-op. Consumers can replace it with
/// their own implementation (e.g. sevdesk, Stripe Billing, custom PDF).
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface IInvoiceGenerator<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Generates an invoice for the given order.</summary>
    Task GenerateAsync(Order<TKey> order, CancellationToken ct = default);
}
