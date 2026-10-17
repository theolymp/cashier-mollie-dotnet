using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Interface for generating invoices from processed orders.
/// <para>
/// NOT INVOKED BY THIS LIBRARY — this is a callback the library is meant to make, and no code path
/// makes it. Registering an implementation has no effect today: it will be constructed by DI and
/// never called. The invoice-available event in the Events namespace is likewise never dispatched,
/// so the invoice path is unbuilt at both ends rather than merely lacking a renderer. (That event is
/// not named here on purpose: PlannedEventsTests requires a planned event's name to appear nowhere
/// in src/, and that scan deliberately does not strip comments — there a comment mention is a loud
/// false alarm, which is the safe direction. The fact is this cross-reference's value, not the
/// spelling of the type.)
/// </para>
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface IInvoiceGenerator<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Generates an invoice for the given order.</summary>
    Task GenerateAsync(Order<TKey> order, CancellationToken ct = default);
}
