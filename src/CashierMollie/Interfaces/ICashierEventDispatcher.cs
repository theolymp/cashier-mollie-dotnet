namespace CashierMollie.Interfaces;

/// <summary>
/// Dispatches CashierMollie domain events. Implement this to handle
/// billing events (payment received, subscription created, etc.).
/// Register your implementation in DI to replace the default no-op dispatcher.
/// </summary>
public interface ICashierEventDispatcher
{
    /// <summary>
    /// Dispatches a domain event to registered handlers.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="domainEvent">The event instance to dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DispatchAsync<T>(T domainEvent, CancellationToken ct = default) where T : notnull;
}
