using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Billing engine that manages its own billing cycle using mandate payments.
/// Unlike <see cref="MollieBillingEngine{TKey}"/>, this engine does NOT create
/// Mollie subscriptions. Instead, it schedules <see cref="OrderItem{TKey}"/> records
/// with a <see cref="OrderItem{TKey}.ProcessAt"/> date and processes them via
/// <see cref="ProcessDueItemsAsync"/>, creating on-demand recurring payments.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class ManagedBillingEngine<TKey> : IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Default billing interval in days for monthly subscriptions.</summary>
    private const int DefaultBillingIntervalDays = 30;

    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="ManagedBillingEngine{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    public ManagedBillingEngine(
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
    public bool RequiresBackgroundProcessing => true;

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
                _options.WebhookUrl, ct);

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
            var now = DateTimeOffset.UtcNow;
            subscription.Status = SubscriptionStatus.Active;
            subscription.CycleStartedAt = now;
            subscription.UpdatedAt = now;

            // Schedule the first billing OrderItem.
            // UnitPrice starts at 0 — consumers should set pricing via the OrderItem
            // before the item is processed, or use a pricing hook/event.
            var firstItem = new OrderItem<TKey>
            {
                SubscriptionId = subscription.Id,
                OwnerId = owner.Id,
                Description = $"{plan} subscription",
                Currency = _options.Currency,
                UnitPrice = 0m,
                Quantity = options.Quantity > 0 ? options.Quantity : 1,
                ProcessAt = now.AddDays(DefaultBillingIntervalDays),
            };
            _db.OrderItems.Add(firstItem);

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

        // ManagedEngine does NOT cancel on Mollie — no Mollie subscription exists.

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

        // ManagedEngine: set NextPlan for next-cycle swap by default.
        // The plan swap takes effect when the next OrderItem is processed.
        subscription.NextPlan = newPlan;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionPlanSwapped<TKey>(subscription, oldPlan, newPlan, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> UpdateQuantityAsync(Subscription<TKey> subscription,
        int quantity, CancellationToken ct = default)
    {
        int oldQuantity = (int)(subscription.Quantity ?? 1);
        subscription.Quantity = quantity;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionQuantityUpdated<TKey>(subscription, oldQuantity, quantity, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public async Task ProcessDueItemsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Find all unprocessed OrderItems that are due
        var dueItems = await _db.OrderItems
            .Include(i => i.Subscription)
            .Where(i => i.ProcessAt != null && i.ProcessAt <= now && i.ProcessedAt == null)
            .ToListAsync(ct);

        foreach (var item in dueItems)
        {
            var subscription = item.Subscription;
            if (subscription == null || subscription.Status != SubscriptionStatus.Active)
                continue;

            var customerId = subscription.MollieCustomerId;
            if (string.IsNullOrEmpty(customerId))
                continue;

            // Look up the mandate from the most recent successful payment for this owner
            var mandateId = await _db.Payments
                .Where(p => p.OwnerId!.Equals(item.OwnerId) && p.MollieMandateId != null)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => p.MollieMandateId)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrEmpty(mandateId))
                continue;

            try
            {
                // Create a recurring payment via Mollie
                var molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                    customerId, mandateId, item.UnitPrice * item.Quantity,
                    item.Currency, item.Description,
                    _options.WebhookUrl, ct);

                // Mark the item as processed
                item.MolliePaymentId = molliePayment.Id;
                item.MolliePaymentStatus = molliePayment.Status;
                item.ProcessedAt = now;
                item.UpdatedAt = now;

                // Update the subscription's cycle start
                subscription.CycleStartedAt = now;
                subscription.UpdatedAt = now;

                // Apply pending plan swap if one exists
                if (!string.IsNullOrEmpty(subscription.NextPlan))
                {
                    subscription.Plan = subscription.NextPlan;
                    subscription.NextPlan = null;
                }

                // Schedule the next OrderItem for the next billing cycle
                var nextItem = new OrderItem<TKey>
                {
                    SubscriptionId = subscription.Id,
                    OwnerId = item.OwnerId,
                    Description = item.Description,
                    Currency = item.Currency,
                    UnitPrice = item.UnitPrice,
                    Quantity = item.Quantity,
                    ProcessAt = now.AddDays(DefaultBillingIntervalDays),
                };
                _db.OrderItems.Add(nextItem);

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Log and continue — don't let one failed item prevent processing of others.
                // The unprocessed item will be retried on the next cycle.
                await _eventDispatcher.DispatchAsync(
                    new OrderPaymentFailed<TKey>(
                        new Payment<TKey>
                        {
                            OwnerId = item.OwnerId,
                            SubscriptionId = item.SubscriptionId,
                            Amount = item.UnitPrice * item.Quantity,
                            Currency = item.Currency,
                            Status = "failed",
                        },
                        subscription,
                        item.OwnerId), ct);
            }
        }
    }
}
