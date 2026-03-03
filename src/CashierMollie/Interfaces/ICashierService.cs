using CashierMollie.Models;

namespace CashierMollie.Interfaces;

public interface ICashierService<TKey> where TKey : IEquatable<TKey>
{
    ISubscriptionBuilder<TKey> NewSubscription(IBillable<TKey> owner, string name, string plan);
    Task CancelAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task CancelImmediatelyAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task ResumeAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default);
    Task<bool> IsSubscribedAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<bool> OnGracePeriodAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<bool> OnTrialAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<Subscription<TKey>?> GetSubscriptionAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<List<Subscription<TKey>>> GetSubscriptionsAsync(IBillable<TKey> owner, CancellationToken ct = default);
}
