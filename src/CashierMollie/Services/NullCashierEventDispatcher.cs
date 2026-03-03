using CashierMollie.Interfaces;

namespace CashierMollie.Services;

/// <summary>
/// Default no-op event dispatcher. Replace by registering your own
/// ICashierEventDispatcher implementation in DI.
/// </summary>
public class NullCashierEventDispatcher : ICashierEventDispatcher
{
    public Task DispatchAsync<T>(T @event, CancellationToken ct = default) where T : notnull
        => Task.CompletedTask;
}
