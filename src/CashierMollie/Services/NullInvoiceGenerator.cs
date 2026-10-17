using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Default no-op invoice generator.
/// <para>
/// NOT INVOKED BY THIS LIBRARY — no code path calls <see cref="IInvoiceGenerator{TKey}.GenerateAsync"/>,
/// so replacing this in DI does not enable invoice generation. An implementation is constructed and
/// never called. The previous summary said "Replace in DI to enable invoice generation", which is an
/// instruction a consumer can follow — writing a real sevdesk or PDF integration — with no way to
/// tell that it never runs.
/// </para>
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class NullInvoiceGenerator<TKey> : IInvoiceGenerator<TKey> where TKey : IEquatable<TKey>
{
    /// <inheritdoc />
    public Task GenerateAsync(Order<TKey> order, CancellationToken ct = default)
        => Task.CompletedTask;
}
