using CashierMollie.Models;

namespace CashierMollie.Interfaces;

public interface ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    ISubscriptionBuilder<TKey> WithCoupon(string coupon);
    ISubscriptionBuilder<TKey> TrialDays(int days);
    ISubscriptionBuilder<TKey> WithProration();
    ISubscriptionBuilder<TKey> WithMandateOnly();
    ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);
    Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default);
}
