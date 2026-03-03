using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Default no-op invoice generator. Replace in DI to enable invoice generation.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class NullInvoiceGenerator<TKey> : IInvoiceGenerator<TKey> where TKey : IEquatable<TKey>
{
    /// <inheritdoc />
    public Task GenerateAsync(Order<TKey> order, CancellationToken ct = default)
        => Task.CompletedTask;
}
