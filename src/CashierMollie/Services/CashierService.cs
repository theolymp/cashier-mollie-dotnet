using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

public class CashierService<TKey> : ICashierService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;

    public CashierService(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
    }

    public ISubscriptionBuilder<TKey> NewSubscription(IBillable<TKey> owner, string name, string plan)
        => new SubscriptionBuilder<TKey>(_db, _mollieClient, _eventDispatcher, _options, owner, name, plan);

    public async Task CancelAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);

        // Grace period: end at current cycle end (or configured days from cycle start)
        var graceDays = _options.GracePeriodDays;
        sub.EndsAt = sub.CycleStartedAt?.AddDays(graceDays) ?? DateTimeOffset.UtcNow.AddDays(graceDays);
        sub.Status = SubscriptionStatus.Cancelled;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Cancel on Mollie side if subscription exists there
        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId) && !string.IsNullOrEmpty(sub.MollieCustomerId))
            await _mollieClient.CancelSubscriptionAsync(sub.MollieCustomerId, sub.MollieSubscriptionId, ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionCancelled<TKey>(sub, owner.Id), ct);
    }

    public async Task CancelImmediatelyAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);

        sub.EndsAt = DateTimeOffset.UtcNow;
        sub.Status = SubscriptionStatus.Cancelled;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId) && !string.IsNullOrEmpty(sub.MollieCustomerId))
            await _mollieClient.CancelSubscriptionAsync(sub.MollieCustomerId, sub.MollieSubscriptionId, ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionCancelled<TKey>(sub, owner.Id), ct);
    }

    public async Task ResumeAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionOrThrow(owner, name, ct);

        if (!sub.OnGracePeriod())
            throw new CashierException("Cannot resume a subscription that is not on a grace period.");

        sub.EndsAt = null;
        sub.Status = SubscriptionStatus.Active;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionResumed<TKey>(sub, owner.Id), ct);
    }

    public async Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        var oldPlan = sub.Plan;

        sub.Plan = newPlan;
        sub.UpdatedAt = DateTimeOffset.UtcNow;

        // Update on Mollie side
        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId) && !string.IsNullOrEmpty(sub.MollieCustomerId))
            await _mollieClient.UpdateSubscriptionAsync(sub.MollieCustomerId, sub.MollieSubscriptionId, ct: ct);

        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionPlanSwapped<TKey>(sub, oldPlan, newPlan, owner.Id), ct);

        return sub;
    }

    public async Task<bool> IsSubscribedAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.IsActive() == true || sub?.OnGracePeriod() == true || sub?.OnTrial() == true;
    }

    public async Task<bool> OnGracePeriodAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.OnGracePeriod() == true;
    }

    public async Task<bool> OnTrialAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.OnTrial() == true;
    }

    public async Task<bool> IsCancelledAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.IsCancelled() == true;
    }

    public Task<Subscription<TKey>?> GetSubscriptionAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
        => _db.Subscriptions.FirstOrDefaultAsync(
            s => s.OwnerId.Equals(owner.Id) && s.Name == name, ct);

    public Task<List<Subscription<TKey>>> GetSubscriptionsAsync(IBillable<TKey> owner, CancellationToken ct = default)
        => _db.Subscriptions.Where(s => s.OwnerId.Equals(owner.Id)).ToListAsync(ct);

    private async Task<Subscription<TKey>> GetSubscriptionOrThrow(IBillable<TKey> owner, string name, CancellationToken ct)
        => await GetSubscriptionAsync(owner, name, ct)
           ?? throw new CashierException($"No subscription '{name}' found for owner '{owner.Id}'.");

    private async Task<Subscription<TKey>> GetActiveSubscriptionOrThrow(IBillable<TKey> owner, string name, CancellationToken ct)
    {
        var sub = await GetSubscriptionOrThrow(owner, name, ct);
        if (!sub.IsActive() && !sub.OnGracePeriod())
            throw new CashierException($"Subscription '{name}' is not active.");
        return sub;
    }
}
