using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
public partial class ManagedBillingEngine<TKey> : IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Default billing interval in days for monthly subscriptions.</summary>
    private const int DefaultBillingIntervalDays = 30;

    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly ILogger<ManagedBillingEngine<TKey>> _logger;
    private readonly ICouponService<TKey> _coupons;

    /// <summary>
    /// Initializes a new instance of <see cref="ManagedBillingEngine{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="coupons">Coupon validation, redemption and discount calculation. Required
    /// rather than optional: an engine that silently cannot apply a coupon is the defect this
    /// parameter exists to end.</param>
    public ManagedBillingEngine(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options,
        ILogger<ManagedBillingEngine<TKey>> logger,
        ICouponService<TKey> coupons)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
        _logger = logger;
        _coupons = coupons;
    }

    /// <inheritdoc />
    public bool RequiresBackgroundProcessing => true;

    /// <inheritdoc />
    public async Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default)
    {
        ProrationSupport.ThrowIfRequested(options.Prorate);

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

        // A coupon code is a money instruction. Validate it and record the redemption here, or
        // reject it: until 2026-10-26 this field travelled from the builder into this method and
        // was dropped — full price, no redemption, no event, no error. ValidateAsync throws for an
        // unknown code, which is why an invalid coupon now fails the create instead of being
        // quietly ignored.
        if (!string.IsNullOrWhiteSpace(options.CouponCode))
        {
            await _coupons.RedeemAsync(owner, options.CouponCode, name, ct);
        }

        string? checkoutUrl = null;
        bool requiresAction = false;

        if (string.IsNullOrEmpty(owner.MollieMandateId) && !options.MandateOnly)
        {
            // First payment flow — redirect to Mollie checkout to acquire a mandate
            var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                owner.MollieCustomerId, _options.PaymentMethodUpdateAmount, _options.Currency,
                $"First payment for {plan}",
                _options.FirstPaymentRedirectUrl,
                _options.EffectiveWebhookUrl, options.Metadata, ct);

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
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionResumed<TKey>(subscription, subscription.OwnerId), ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> SwapPlanAsync(Subscription<TKey> subscription,
        string newPlan, SwapOptions? options = null, CancellationToken ct = default)
    {
        ProrationSupport.ThrowIfRequested(options?.Prorate == true);

        string oldPlan = subscription.Plan;

        // ManagedEngine: set NextPlan for next-cycle swap by default.
        // The plan swap takes effect when the next OrderItem is processed.
        subscription.NextPlan = newPlan;

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
            {
                LogSkippedInactiveSubscription(item.Id);
                continue;
            }

            var customerId = subscription.MollieCustomerId;
            if (string.IsNullOrEmpty(customerId))
            {
                LogSkippedNoCustomer(item.Id);
                continue;
            }

            // Look up the mandate from the most recent successful payment for this owner
            var mandateId = await _db.Payments
                .Where(p => p.OwnerId!.Equals(item.OwnerId) && p.MollieMandateId != null)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => p.MollieMandateId)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrEmpty(mandateId))
            {
                LogSkippedNoMandate(item.Id);
                continue;
            }

            try
            {
                decimal gross = item.UnitPrice * item.Quantity;

                // The unit price and the quantity go in separately: both shipped handlers multiply
                // by quantity themselves, so passing the gross would apply the quantity twice.
                CouponDiscount discount = await _coupons.CalculateActiveDiscountAsync(
                    item.OwnerId, subscription.Name, item.UnitPrice, item.Quantity, ct);

                if (discount.Status == CouponDiscountStatus.DefinitionMissing)
                {
                    // Charging full price here is defensible; doing it silently is not.
                    LogCouponDefinitionMissing(discount.Code!, item.Id);
                }

                decimal net = gross - discount.Amount;

                // Create a recurring payment via Mollie
                var molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                    customerId, mandateId, net,
                    item.Currency, item.Description,
                    _options.EffectiveWebhookUrl, metadata: null, ct);

                if (discount.Status == CouponDiscountStatus.Applied)
                {
                    LogCouponApplied(discount.Code!, gross, net, item.Id);

                    // Only after the charge succeeded: consuming first would spend a billing cycle
                    // of the coupon on a payment that threw.
                    await _coupons.ConsumeRedemptionAsync(item.OwnerId, subscription.Name, ct);
                }

                // Mark the item as processed
                item.MolliePaymentId = molliePayment.Id;
                item.MolliePaymentStatus = molliePayment.Status;
                item.ProcessedAt = now;

                // Update the subscription's cycle start
                subscription.CycleStartedAt = now;

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
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Log and continue — don't let one failed item prevent processing of others.
                // The unprocessed item will be retried on the next cycle.
                LogOrderItemProcessingFailed(item.Id, ex);
                await _eventDispatcher.DispatchAsync(
                    new OrderPaymentFailed<TKey>(
                        new Payment<TKey>
                        {
                            OwnerId = item.OwnerId,
                            SubscriptionId = item.SubscriptionId,
                            // Gross, deliberately: the discount is computed inside the try block,
                            // so a failure before that point has no net amount to report. Stating
                            // the list price is honest; inventing a net one would not be.
                            Amount = item.UnitPrice * item.Quantity,
                            Currency = item.Currency,
                            Status = "failed",
                        },
                        subscription,
                        item.OwnerId), ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping order item {OrderItemId}: subscription is null or not active")]
    private partial void LogSkippedInactiveSubscription(long orderItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping order item {OrderItemId}: no Mollie customer ID")]
    private partial void LogSkippedNoCustomer(long orderItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping order item {OrderItemId}: no valid mandate found")]
    private partial void LogSkippedNoMandate(long orderItemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process order item {OrderItemId}")]
    private partial void LogOrderItemProcessingFailed(long orderItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Coupon {Code} reduced order item {OrderItemId} from {Gross} to {Net}")]
    private partial void LogCouponApplied(string code, decimal gross, decimal net, long orderItemId);

    // A redemption whose definition is gone. Warning rather than information: the customer is
    // charged full price while holding a coupon, and nobody finds that by reading a total.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Coupon {Code} is redeemed for order item {OrderItemId} but its definition is no "
            + "longer available from the coupon repository; charging the full amount")]
    private partial void LogCouponDefinitionMissing(string code, long orderItemId);
}
