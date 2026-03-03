namespace CashierMollie.Interfaces;

/// <summary>
/// Dispatches CashierMollie domain events. Implement this to handle
/// billing events (payment received, subscription created, etc.).
/// Register your implementation in DI to replace the default no-op dispatcher.
/// </summary>
public interface ICashierEventDispatcher
{
    Task DispatchAsync<T>(T @event, CancellationToken ct = default) where T : notnull;
}
