using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Billing engine that uses Mollie's native Subscription API for recurring billing.
/// This is the default engine — Mollie manages the billing cycle, and this engine
/// delegates subscription CRUD to the Mollie API while maintaining local state.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class MollieBillingEngine<TKey> : IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="MollieBillingEngine{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    public MollieBillingEngine(
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

    /// <inheritdoc />
    public bool RequiresBackgroundProcessing => false;

    /// <inheritdoc />
    public async Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default)
    {
        // Ensure Mollie customer exists
        if (string.IsNullOrEmpty(owner.MollieCustomerId))
        {
            var customer = await _mollieClient.CreateCustomerAsync(
                owner.Name ?? "Customer", owner.Email, ct);
            owner.MollieCustomerId = customer.Id;
        }

        var subscription = new Subscription<TKey>
        {
            OwnerId = owner.Id,
            Name = name,
            Plan = plan,
            MollieCustomerId = owner.MollieCustomerId,
            Status = SubscriptionStatus.Pending,
            Quantity = options.Quantity > 1 ? options.Quantity : null,
        };

        if (options.TrialDays > 0)
            subscription.TrialEndsAt = DateTimeOffset.UtcNow.AddDays(options.TrialDays);

        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);

        string? checkoutUrl = null;
        bool requiresAction = false;

        if (string.IsNullOrEmpty(owner.MollieMandateId) && !options.MandateOnly)
        {
            // First payment flow — redirect to Mollie checkout to acquire a mandate
            var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                owner.MollieCustomerId, _options.PaymentMethodUpdateAmount, _options.Currency,
                $"First payment for {plan}",
                _options.FirstPaymentRedirectUrl,
                _options.EffectiveWebhookUrl, ct);

            var localPayment = new Payment<TKey>
            {
                OwnerId = owner.Id,
                SubscriptionId = subscription.Id,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "open",
                Amount = _options.PaymentMethodUpdateAmount,
                Currency = _options.Currency,
            };
            _db.Payments.Add(localPayment);
            await _db.SaveChangesAsync(ct);

            checkoutUrl = molliePayment.Links?.Checkout?.Href;
            requiresAction = true;
        }
        else
        {
            // Direct activation — mandate exists or mandate-only mode
            subscription.Status = SubscriptionStatus.Active;
            subscription.CycleStartedAt = DateTimeOffset.UtcNow;
            subscription.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _eventDispatcher.DispatchAsync(
                new SubscriptionCreated<TKey>(subscription, owner.Id), ct);
        }

        return new SubscriptionResult<TKey>(subscription, checkoutUrl, requiresAction);
    }

    /// <inheritdoc />
    public async Task CancelSubscriptionAsync(Subscription<TKey> subscription, bool immediately,
        CancellationToken ct = default)
    {
        if (immediately)
        {
            subscription.EndsAt = DateTimeOffset.UtcNow;
        }
        else
        {
            int graceDays = _options.GracePeriodDays;
            subscription.EndsAt = subscription.CycleStartedAt?.AddDays(graceDays)
                ?? DateTimeOffset.UtcNow.AddDays(graceDays);
        }

        subscription.Status = SubscriptionStatus.Cancelled;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Cancel at Mollie if a remote subscription exists
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) &&
            !string.IsNullOrEmpty(subscription.MollieSubscriptionId))
        {
            await _mollieClient.CancelSubscriptionAsync(
                subscription.MollieCustomerId, subscription.MollieSubscriptionId, ct);
        }

        await _eventDispatcher.DispatchAsync(
            new SubscriptionCancelled<TKey>(subscription, subscription.OwnerId), ct);
    }

    /// <inheritdoc />
    public async Task ResumeSubscriptionAsync(Subscription<TKey> subscription,
        CancellationToken ct = default)
    {
        subscription.EndsAt = null;
        subscription.Status = SubscriptionStatus.Active;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionResumed<TKey>(subscription, subscription.OwnerId), ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> SwapPlanAsync(Subscription<TKey> subscription,
        string newPlan, SwapOptions? options = null, CancellationToken ct = default)
    {
        string oldPlan = subscription.Plan;
        subscription.Plan = newPlan;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;

        // Update Mollie subscription if one exists remotely
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) &&
            !string.IsNullOrEmpty(subscription.MollieSubscriptionId))
        {
            await _mollieClient.UpdateSubscriptionAsync(
                subscription.MollieCustomerId, subscription.MollieSubscriptionId,
                ct: ct);
        }

        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionPlanSwapped<TKey>(subscription, oldPlan, newPlan, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> UpdateQuantityAsync(Subscription<TKey> subscription,
        int quantity, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        int oldQuantity = (int)(subscription.Quantity ?? 1);
        subscription.Quantity = quantity;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionQuantityUpdated<TKey>(subscription, oldQuantity, quantity, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public Task ProcessDueItemsAsync(CancellationToken ct = default)
        => Task.CompletedTask; // No-op for Mollie native engine
}
