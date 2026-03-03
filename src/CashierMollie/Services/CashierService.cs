using CashierMollie.Data;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Main entry point for subscription management. Thin coordinator that delegates
/// lifecycle operations (create, cancel, resume, swap) to <see cref="IBillingEngine{TKey}"/>
/// and keeps status checks / query methods locally.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class CashierService<TKey> : ICashierService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IBillingEngine<TKey> _engine;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="CashierService{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="engine">The billing engine used for subscription lifecycle operations.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    public CashierService(
        CashierDbContext<TKey> db,
        IBillingEngine<TKey> engine,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options)
    {
        _db = db;
        _engine = engine;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
    }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> NewSubscription(IBillable<TKey> owner, string name, string plan)
        => new SubscriptionBuilder<TKey>(_db, _engine, _mollieClient, _eventDispatcher, _options, owner, name, plan);

    /// <inheritdoc />
    public async Task CancelAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        await _engine.CancelSubscriptionAsync(sub, immediately: false, ct);
    }

    /// <inheritdoc />
    public async Task CancelImmediatelyAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        await _engine.CancelSubscriptionAsync(sub, immediately: true, ct);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionOrThrow(owner, name, ct);

        if (!sub.OnGracePeriod())
            throw new CashierException("Cannot resume a subscription that is not on a grace period.");

        await _engine.ResumeSubscriptionAsync(sub, ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan,
        SwapOptions? options = null, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        return await _engine.SwapPlanAsync(sub, newPlan, options, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsSubscribedAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.IsActive() == true || sub?.OnGracePeriod() == true || sub?.OnTrial() == true;
    }

    /// <inheritdoc />
    public async Task<bool> OnGracePeriodAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.OnGracePeriod() == true;
    }

    /// <inheritdoc />
    public async Task<bool> OnTrialAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.OnTrial() == true;
    }

    /// <inheritdoc />
    public async Task<bool> IsCancelledAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
    {
        var sub = await GetSubscriptionAsync(owner, name, ct);
        return sub?.IsCancelled() == true;
    }

    /// <inheritdoc />
    public Task<Subscription<TKey>?> GetSubscriptionAsync(IBillable<TKey> owner, string name, CancellationToken ct = default)
        => _db.Subscriptions.FirstOrDefaultAsync(
            s => s.OwnerId.Equals(owner.Id) && s.Name == name, ct);

    /// <inheritdoc />
    public Task<List<Subscription<TKey>>> GetSubscriptionsAsync(IBillable<TKey> owner, CancellationToken ct = default)
        => _db.Subscriptions.Where(s => s.OwnerId.Equals(owner.Id)).ToListAsync(ct);

    /// <inheritdoc />
    public async Task<string> GetOrCreateMollieCustomerAsync(IBillable<TKey> owner, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(owner.MollieCustomerId))
            return owner.MollieCustomerId;

        var customer = await _mollieClient.CreateCustomerAsync(owner.Name ?? "", owner.Email, ct);
        owner.MollieCustomerId = customer.Id;
        return customer.Id;
    }

    /// <inheritdoc />
    public async Task UpdateMollieCustomerAsync(IBillable<TKey> owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner.MollieCustomerId);
        await _mollieClient.GetCustomerAsync(owner.MollieCustomerId, ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> UpdateQuantityAsync(IBillable<TKey> owner, string name,
        int quantity, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        return await _engine.UpdateQuantityAsync(sub, quantity, ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> IncrementQuantityAsync(IBillable<TKey> owner, string name,
        int count = 1, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        int current = (int)(sub.Quantity ?? 1);
        return await _engine.UpdateQuantityAsync(sub, current + count, ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> DecrementQuantityAsync(IBillable<TKey> owner, string name,
        int count = 1, CancellationToken ct = default)
    {
        var sub = await GetActiveSubscriptionOrThrow(owner, name, ct);
        int current = (int)(sub.Quantity ?? 1);
        int newQty = Math.Max(1, current - count);
        return await _engine.UpdateQuantityAsync(sub, newQty, ct);
    }

    /// <inheritdoc />
    public IChargeBuilder<TKey> NewCharge(IBillable<TKey> owner, decimal amount)
        => new ChargeBuilder<TKey>(_db, _mollieClient, _eventDispatcher, _options, owner, amount);

    /// <inheritdoc />
    public async Task<PaymentMethodUpdateResult> UpdatePaymentMethodAsync(IBillable<TKey> owner,
        string? redirectUrl = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(owner.MollieCustomerId))
            throw new CashierException("Owner has no Mollie customer ID.");

        var payment = await _mollieClient.CreateFirstPaymentAsync(
            owner.MollieCustomerId,
            _options.PaymentMethodUpdateAmount,
            _options.Currency,
            "Update payment method",
            redirectUrl ?? _options.PaymentMethodUpdateRedirectUrl,
            _options.WebhookUrl, ct);

        return new PaymentMethodUpdateResult(payment.Links?.Checkout?.Href ?? "");
    }

    /// <inheritdoc />
    public async Task<bool> HasValidMandateAsync(IBillable<TKey> owner, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(owner.MollieCustomerId) || string.IsNullOrEmpty(owner.MollieMandateId))
            return false;

        try
        {
            var mandate = await _mollieClient.GetMandateAsync(
                owner.MollieCustomerId, owner.MollieMandateId, ct);
            return mandate.Status == "valid";
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RevokeMandateAsync(IBillable<TKey> owner, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(owner.MollieCustomerId) && !string.IsNullOrEmpty(owner.MollieMandateId))
        {
            await _mollieClient.RevokeMandateAsync(owner.MollieCustomerId, owner.MollieMandateId, ct);
        }

        var oldMandateId = owner.MollieMandateId;
        owner.MollieMandateId = null;

        await _eventDispatcher.DispatchAsync(
            new Events.MandateCleared<TKey>(oldMandateId ?? "", owner.Id), ct);
    }

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
