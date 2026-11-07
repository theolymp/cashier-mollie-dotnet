using System.Globalization;
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
                // Stored here because recurring charges are created from order items: this is the
                // only thing that carries the dictionary past the first payment. Note the branch
                // -- an item is scheduled on direct activation, so a subscription that goes
                // through the checkout flow has none yet and nothing to hang metadata on.
                Metadata = MetadataJson.Serialise(options.Metadata),
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
    /// <summary>
    /// Fills in <see cref="Order{TKey}.Number"/> from the configured format, or leaves it null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OrderNumberFormat</c> was one of the options documented as read by nothing. Wiring it is
    /// additive: <c>Number</c> was previously **always** null, so no consumer can be relying on a
    /// value, and the default format was already documented as its intended meaning.
    /// </para>
    /// <para>
    /// <b>A bad format must not stop a billing run.</b> The value is consumer configuration, so it
    /// can be malformed (<c>{1}</c>, an unbalanced brace) or longer than the column's 50
    /// characters. Either would otherwise throw inside the charge loop -- after the money was
    /// taken -- and the catch would report a failed payment for a successful charge. Both cases
    /// log and leave the number null instead: an order without a number is a cosmetic gap, an
    /// order rolled back after a charge is not.
    /// </para>
    /// </remarks>
    private void AssignOrderNumber(Order<TKey> order)
    {
        const int numberColumnLength = 50;
        if (string.IsNullOrWhiteSpace(_options.OrderNumberFormat))
        {
            return;
        }

        string formatted;
        try
        {
            formatted = string.Format(
                CultureInfo.InvariantCulture, _options.OrderNumberFormat, order.Id);
        }
        catch (FormatException)
        {
            LogOrderNumberFormatInvalid(order.Id);
            return;
        }

        if (formatted.Length > numberColumnLength)
        {
            LogOrderNumberTooLong(order.Id, formatted.Length);
            return;
        }

        order.Number = formatted;
    }

    /// <summary>
    /// Schedules a first order item for active subscriptions that have none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes.</b> An order item was created in exactly one place:
    /// <c>CreateSubscriptionAsync</c>, and only on its direct-activation branch. A subscription
    /// that went through the first-payment checkout flow is activated by the webhook instead --
    /// which sets <c>Active</c> and <c>CycleStartedAt</c> and dispatches <c>SubscriptionCreated</c>
    /// -- and nothing ever scheduled anything for it. On this engine that means an active
    /// subscription that **never bills**, silently, for as long as it exists.
    /// </para>
    /// <para>
    /// <b>Why here and not in the webhook.</b> The webhook service is engine-agnostic and holds
    /// neither the options nor a logger, so teaching it to schedule would mean changing a public
    /// constructor -- and it would still only help subscriptions activated after the change. A
    /// pass on the timer that already runs costs no new dependency, no public surface, and heals
    /// rows that are already stuck. It is also idempotent by construction: it acts only where the
    /// count of order items is zero.
    /// </para>
    /// <para>
    /// <b>Zero items is the discriminator, not zero unprocessed items.</b> A subscription billing
    /// normally always has at least the processed item and its successor, so it never matches. A
    /// subscription with no item at all can only have come from the path above.
    /// </para>
    /// <para>
    /// <c>ProcessAt</c> is derived from <c>CycleStartedAt</c> rather than from now, so the first
    /// cycle is measured from the payment that started it and not from whenever the timer happened
    /// to notice. <c>UnitPrice</c> starts at 0, exactly as on the direct path -- pricing is the
    /// consumer's, and an unpriced item is skipped rather than charged.
    /// </para>
    /// </remarks>
    private async Task ScheduleMissingFirstItemsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // EndsAt set means cancelled or in a grace period: no new cycle should be scheduled for it.
        var stranded = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.EndsAt == null
                     && !_db.OrderItems.Any(i => i.SubscriptionId == s.Id))
            .ToListAsync(ct);

        if (stranded.Count == 0)
        {
            return;
        }

        foreach (var subscription in stranded)
        {
            var start = subscription.CycleStartedAt ?? now;
            _db.OrderItems.Add(new OrderItem<TKey>
            {
                SubscriptionId = subscription.Id,
                OwnerId = subscription.OwnerId,
                Description = $"{subscription.Plan} subscription",
                Currency = _options.Currency,
                UnitPrice = 0m,
                // Subscription.Quantity is nullable and nothing in the library reads it for
                // billing, so an absent value means "one" rather than "zero".
                Quantity = subscription.Quantity is > 0 ? subscription.Quantity.Value : 1,
                ProcessAt = start.AddDays(DefaultBillingIntervalDays),
            });
            LogScheduledMissingFirstItem(subscription.Id);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ProcessDueItemsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await ScheduleMissingFirstItemsAsync(now, ct);

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

                // An unpriced item is not a charge. UnitPrice starts at 0 by design -- pricing is
                // the consumer's -- so this is the item waiting to be priced, not an amount owed.
                // Without the guard it went to Mollie as 0.00, which Mollie rejects: the call
                // threw, the catch dispatched OrderPaymentFailed, and the item (never consumed)
                // came back every cycle forever. That reported a FAILED PAYMENT for something that
                // was never a payment, and it is the state the reconciliation pass above now
                // creates deliberately, so the guard is part of that change rather than an extra.
                //
                // Deliberately `gross`, NOT `net`: a priced item discounted to zero by a coupon is
                // a different question -- the customer arguably had their cycle -- and answering
                // it would silently redefine coupon semantics. Left exactly as it was; recorded as
                // an open item instead.
                if (gross <= 0m)
                {
                    LogSkippedNotPriced(item.Id);
                    continue;
                }

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

                // Only this library writes the column, so a value that will not parse means
                // something outside it changed the row. Charging without the metadata is the
                // lesser harm -- the money is still correct -- but doing it silently is not, so
                // the item is named and the charge goes ahead.
                if (!MetadataJson.TryDeserialise(item.Metadata, out var metadata))
                {
                    LogMetadataUnreadable(item.Id);
                }

                // Create a recurring payment via Mollie
                var molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                    customerId, mandateId, net,
                    item.Currency, item.Description,
                    _options.EffectiveWebhookUrl, metadata, ct);

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

                // What was actually subtracted, so the charged amount is explainable from the row
                // rather than only from the Mollie payment and a log line. Set unconditionally:
                // writing it only when a coupon applied would leave the previous cycle's value
                // standing on an item that got no discount.
                item.Discount = discount.Amount;

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
                    // Inherited, which is what makes metadata survive every cycle rather than one.
                    Metadata = item.Metadata,
                    // Discount is deliberately NOT copied: it records what a past charge
                    // subtracted, and the next cycle's coupon state is computed when it is charged.
                };
                _db.OrderItems.Add(nextItem);

                // The order header for what was just billed. Until now the library's only
                // `new Order<TKey>` was in ChargeBuilder, so the Managed engine created order
                // *items* and never a header: Number never set, ProcessedAt never set, the
                // order-processed event never dispatched, and the invoice seam with nothing to
                // receive.
                //
                // ONE ORDER PER CHARGE, not per run. Each item is charged with its own Mollie
                // payment, so an order that spanned several items could not name the payment that
                // settles it. "Grouping" would be the aspirational version of what the engine
                // actually does.
                //
                // The money columns mirror what was CHARGED, which is the only way the order and
                // the payment can be reconciled:
                //   Subtotal  gross, before the discount
                //   Total     net, which is what Mollie was asked for
                //   TotalDue  the same, until a webhook reports the payment paid
                //   Tax       0, deliberately -- see below
                //   CreditUsed 0, deliberately -- credit is a ledger nothing spends yet
                //
                // Tax is 0 and not `item.TaxAmount`: no billing path in this library adds tax to a
                // charge. TaxPercentage exists on the item and nothing applies it, so a non-zero
                // Tax here would describe money that was never collected and would make
                // Total != what the payment is for.
                var order = new Order<TKey>
                {
                    OwnerId = item.OwnerId,
                    Currency = item.Currency,
                    Subtotal = gross,
                    Tax = 0m,
                    Total = net,
                    TotalDue = net,
                    CreditUsed = 0m,
                    MolliePaymentId = molliePayment.Id,
                    MolliePaymentStatus = molliePayment.Status,
                    ProcessedAt = now,
                };
                _db.Orders.Add(order);
                // Through the navigation rather than the key, so one SaveChanges is enough: the
                // order has no Id until it is inserted.
                item.Order = order;

                await _db.SaveChangesAsync(ct);

                // Only now does the order have an Id, which both the number and the event need.
                AssignOrderNumber(order);
                await _db.SaveChangesAsync(ct);

                await _eventDispatcher.DispatchAsync(
                    new OrderProcessed<TKey>(order.Id, item.OwnerId), ct);
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order {OrderId} has no number: OrderNumberFormat is not a usable format string")]
    private partial void LogOrderNumberFormatInvalid(long orderId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order {OrderId} has no number: OrderNumberFormat produced {Length} characters "
            + "and the column holds 50")]
    private partial void LogOrderNumberTooLong(long orderId, int length);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scheduled a missing first order item for subscription {SubscriptionId}: it was "
            + "active with no order items, which happens when the first-payment flow activated it")]
    private partial void LogScheduledMissingFirstItem(long subscriptionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping order item {OrderItemId}: it still has the default price of 0, so "
            + "there is nothing to charge. Set UnitPrice before it falls due")]
    private partial void LogSkippedNotPriced(long orderItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping order item {OrderItemId}: no valid mandate found")]
    private partial void LogSkippedNoMandate(long orderItemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process order item {OrderItemId}")]
    private partial void LogOrderItemProcessingFailed(long orderItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Coupon {Code} reduced order item {OrderItemId} from {Gross} to {Net}")]
    private partial void LogCouponApplied(string code, decimal gross, decimal net, long orderItemId);

    // A redemption whose definition is gone. Warning rather than information: the customer is
    // The argument is the id itself, not a formatted string: the generated method checks IsEnabled
    // only after its arguments have been evaluated (CA1873).
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order item {OrderItemId} has metadata that could not be read back as JSON; "
            + "charging without it")]
    private partial void LogMetadataUnreadable(long orderItemId);

    // charged full price while holding a coupon, and nobody finds that by reading a total.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Coupon {Code} is redeemed for order item {OrderItemId} but its definition is no "
            + "longer available from the coupon repository; charging the full amount")]
    private partial void LogCouponDefinitionMissing(string code, long orderItemId);
}
